using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace DropNSpawn;

// Local observations only: no RPC, world writes, additional spawn attempts or scene scans.
internal static class DungeonSpawnDiagnostics
{
    internal enum Reason
    {
        None, NoOccupiedDungeon, RuleDisabled, LocationMismatch, WaitingInterval,
        Capacity, ChanceMiss, UnsupportedPrefab, CMBlocked, ReservationRejected,
        Requested, DispatchFailed, Timeout, PlayerDeparted,
        ClientRequest, ClientStateRejected, ClientInputRejected, ClientUnsupportedPrefab,
        ClientReplyFound, ClientReplyNotFound, ClientException,
        UnknownReply, WrongPeer, ExpiredReply, RemovedRule, PlacementNotFound,
        PlayerUnavailable, DungeonChanged, FinalRange, FinalPlayerDistance, FinalCapacity,
        OccupiedPoint, FinalCMBlocked, Spawned, SpawnException,
        ZoneNotLoaded, InvalidPosition, OutsideInterior, WrongZone, Height, Radius,
        FloorMissing, UnsupportedCapsule, CapsuleBlocked, PlayerDistance, PathAndLineBlocked,
        PlacementFound, PlacementFailed, PlacementException, AnchorCandidate, RandomCandidate,
        PathFailed, DirectRouteAccepted,
        DungeonDetected, NoLocationInZone, EmptyLocationName
    }

    private const int MaxGroups = 16, MaxSamples = 3;
    private const double ReportInterval = 30;
    private static readonly Dictionary<(string Scope, string Rule, string Prefab), Summary> Groups = new();
    private static readonly Collider[] Obstacles = new Collider[8];
    private static double NextReport;
    private static bool Started;
    private static int Generation, CollisionSamples;

    internal static bool Enabled => PluginBoundSettings.DungeonSpawnDiagnostics?.Value == DropNSpawnPlugin.Toggle.On;

    private sealed class Summary
    {
        internal readonly Dictionary<Reason, long> Counts = new();
        internal readonly List<string> Samples = new();
        internal void Add(Reason reason, long count = 1)
        {
            Counts.TryGetValue(reason, out long previous);
            Counts[reason] = previous + count;
        }
    }

    private static Summary Group(string scope, string rule, string prefab)
    {
        var key = (scope, rule, prefab);
        if (Groups.TryGetValue(key, out Summary summary)) return summary;
        if (Groups.Count >= MaxGroups) key = ("overflow", "", "");
        if (!Groups.TryGetValue(key, out summary)) Groups.Add(key, summary = new Summary());
        return summary;
    }

    internal static void Record(Reason reason, string rule = "", long token = 0, string prefab = "", bool client = false,
        int alive = -1, int pending = -1, int limit = -1)
    {
        if (!Enabled) return;
        Summary summary = Group(client ? "client" : "server", rule, prefab);
        summary.Add(reason);
        if ((token != 0 || alive >= 0) && summary.Samples.Count < MaxSamples)
            summary.Samples.Add($"token={token} {reason}" + (alive >= 0 ? $" alive={alive} pending={pending} limit={limit}" : ""));
    }

    internal static void RecordDungeon(Reason reason, Vector3 position, string location = "")
    {
        if (!Enabled) return;
        // Separate failures so ground players cannot hide missing-location samples.
        Summary summary = Group("dungeon", reason.ToString(), location);
        summary.Add(reason);
        if (summary.Samples.Count >= MaxSamples) return;
        string zone = "-";
        if (DungeonSpawnPlacement.Finite(position))
        {
            Vector2s cell = ZoneSystem.GetZone(position);
            zone = cell.x + "," + cell.y;
        }
        summary.Samples.Add(string.Format(CultureInfo.InvariantCulture,
            "zone={0} position=({1:0.0},{2:0.0},{3:0.0})", zone, position.x, position.y, position.z));
    }

    internal static void Reset()
    {
        if (!Started && Groups.Count == 0 && CollisionSamples == 0) return;
        Groups.Clear(); Started = false; NextReport = 0; CollisionSamples = 0; Generation++;
        Array.Clear(Obstacles, 0, Obstacles.Length);
    }

    internal static void Tick(bool server, bool hasRules)
    {
        if (!Enabled) { Reset(); return; }
        // Never let a log listener failure interrupt the scheduler or RPC registration.
        try
        {
            foreach (string line in Collect(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, server, hasRules))
                DropNSpawnPlugin.DropNSpawnLogger.LogInfo(line);
        }
        catch { Reset(); }
    }

    internal static string[] Collect(double now, bool server, bool hasRules)
    {
        if (!Enabled) { Reset(); return Array.Empty<string>(); }
        if (!Started) { Started = true; NextReport = now + ReportInterval; }
        if (now < NextReport) return Array.Empty<string>();
        NextReport = now + ReportInterval;
        List<string> lines = new()
        {
            $"[DungeonDiag] role={(server ? "server/host" : "client")} window=30s groups={Groups.Count}" +
            (server ? $" activeRules={hasRules}" : " (server rules/caps are not available locally)")
        };
        foreach (var pair in Groups)
        {
            StringBuilder text = new(pair.Key.Scope == "dungeon"
                ? $"[DungeonDiag] dungeon location={Safe(pair.Key.Prefab)}"
                : $"[DungeonDiag] {pair.Key.Scope} rule={Safe(pair.Key.Rule)} prefab={Safe(pair.Key.Prefab)}");
            foreach (var count in pair.Value.Counts) text.Append(' ').Append(count.Key).Append('=').Append(count.Value);
            foreach (string sample in pair.Value.Samples) text.Append(" | ").Append(sample);
            lines.Add(text.ToString());
        }
        Groups.Clear(); CollisionSamples = 0;
        return lines.ToArray();
    }

    private static string Safe(string value)
    {
        if (value.Length == 0) return "-";
        value = value.Length > 96 ? value.Substring(0, 96) : value;
        StringBuilder text = new(value.Length);
        foreach (char c in value) text.Append(char.IsControl(c) ? ' ' : c);
        return text.ToString();
    }

    internal sealed class Placement
    {
        private readonly long Token, StartedAt = Stopwatch.GetTimestamp();
        private readonly int Epoch = Generation;
        private readonly string Prefab;
        private readonly float Min, Max;
        internal readonly Dictionary<Reason, long> Counts = new();
        private string? FirstFailure, Collision;
        internal bool Found, FailedWithException;

        internal Placement(long token, string prefab, float min, float max)
        { Token = token; Prefab = prefab; Min = min; Max = max; }

        internal void Count(Reason reason)
        {
            Counts.TryGetValue(reason, out long previous);
            Counts[reason] = previous + 1;
        }

        internal bool Reject(Reason reason, Vector3 point, string phase)
        {
            Count(reason);
            if (FirstFailure == null)
                FirstFailure = string.Format(CultureInfo.InvariantCulture, "{0}:{1}@({2:0.0},{3:0.0},{4:0.0})", phase, reason, point.x, point.y, point.z);
            return false;
        }

        internal void SampleCollision(Vector3 bottom, Vector3 top, float radius, int mask)
        {
            if (!Enabled || Epoch != Generation || Collision != null || CollisionSamples >= 2) return;
            if (Group("placement", "", Prefab).Samples.Count >= MaxSamples) return;
            CollisionSamples++;
            // At most two extra non-alloc queries per reporting window; they never
            // replace CheckCapsule's decision. Never keep Unity object references.
            try
            {
                int count = Physics.OverlapCapsuleNonAlloc(bottom, top, radius, Obstacles, mask, QueryTriggerInteraction.Ignore);
                StringBuilder text = new(string.Format(CultureInfo.InvariantCulture, "capsuleRadius={0:0.00} blockers=", radius));
                for (int i = 0; i < Math.Min(count, 2); i++)
                {
                    Collider hit = Obstacles[i];
                    if (hit != null) text.Append(Safe(hit.name)).Append("(layer=").Append(hit.gameObject.layer).Append(") ");
                }
                if (count == Obstacles.Length) text.Append("[truncated]");
                Collision = text.ToString();
            }
            catch (Exception ex) { Collision = "collision sample unavailable: " + ex.GetType().Name; }
            finally { Array.Clear(Obstacles, 0, Obstacles.Length); }
        }

        internal void Finish()
        {
            if (!Enabled || Epoch != Generation) return;
            Summary summary = Group("placement", "", Prefab);
            summary.Add(FailedWithException ? Reason.PlacementException : Found ? Reason.PlacementFound : Reason.PlacementFailed);
            foreach (var count in Counts) summary.Add(count.Key, count.Value);
            if (summary.Samples.Count >= MaxSamples) return;
            double milliseconds = (Stopwatch.GetTimestamp() - StartedAt) * 1000d / Stopwatch.Frequency;
            summary.Samples.Add(string.Format(CultureInfo.InvariantCulture,
                "token={0} radius={1:0.##}~{2:0.##} found={3} ms={4:0.###} first={5} {6}",
                Token, Min, Max, Found, milliseconds, FirstFailure ?? "-", Collision ?? ""));
        }
    }
}
