using System.Collections.Generic;
using UnityEngine;
using Reason = DropNSpawn.DungeonSpawnDiagnostics.Reason;

namespace DropNSpawn;

// Geometry is checked by the selected player's loaded scene, never inferred from an
// unloaded server-side spawner ZDO. Adapted from CM's dungeon summon placement, without
// Karma, minion, pursuit or Enforcer state. No scene-wide object scan or position cache.
internal static class DungeonSpawnPlacement
{
    private static readonly List<ZDO> Anchors = new();
    private static readonly List<Vector3> Path = new();
    private static int StaticMask => LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "blocker", "vehicle");

    internal static bool Find(GameObject prefab, Vector3 origin, Vector2s zone, float min, float max, out Vector3 position, long token = 0)
    {
        position = default;
        DungeonSpawnDiagnostics.Placement? trace = null;
        if (DungeonSpawnDiagnostics.Enabled) trace = new(token, prefab.name, min, max);
        try
        {
            if (ZoneSystem.instance == null || !ZoneSystem.instance.IsZoneLoaded(origin))
                return trace?.Reject(Reason.ZoneNotLoaded, origin, "origin") ?? false;
            // Prefer existing dungeon spawn anchors, but validate their geometry and distances too.
            Anchors.Clear();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(1, 0, true), Anchors);
            int attempts = 0;
            foreach (ZDO zdo in Anchors)
            {
                Vector3 point = zdo.GetPosition();
                if (!WithinRange(origin, point, zone, min, max)) continue;
                GameObject anchor = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (anchor == null || (anchor.GetComponent<CreatureSpawner>() == null && anchor.GetComponent<SpawnArea>() == null)) continue;
                trace?.Count(Reason.AnchorCandidate);
                if (Validate(prefab, origin, point, zone, min, max, out position, trace))
                { if (trace != null) trace.Found = true; return true; }
                if (++attempts >= 8) break;
            }
            for (int i = 0; i < 12; i++)
            {
                float angle = Random.Range(0f, Mathf.PI * 2);
                float radius = Mathf.Sqrt(Random.Range(min * min, max * max));
                Vector3 point = origin + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
                trace?.Count(Reason.RandomCandidate);
                if (Validate(prefab, origin, point, zone, min, max, out position, trace))
                { if (trace != null) trace.Found = true; return true; }
            }
            return false;
        }
        catch { if (trace != null) trace.FailedWithException = true; throw; }
        finally { Anchors.Clear(); Path.Clear(); trace?.Finish(); }
    }

    internal static bool WithinRange(Vector3 origin, Vector3 point, Vector2s zone, float min, float max)
        => RangeFailure(origin, point, zone, min, max) == Reason.None;

    private static Reason RangeFailure(Vector3 origin, Vector3 point, Vector2s zone, float min, float max)
    {
        if (!Finite(point) || !Finite(origin)) return Reason.InvalidPosition;
        if (!Character.InInterior(point)) return Reason.OutsideInterior;
        if (!ZoneSystem.GetZone(point).Equals(zone)) return Reason.WrongZone;
        if (Mathf.Abs(point.y - origin.y) > 4f) return Reason.Height;
        float dx = point.x - origin.x, dz = point.z - origin.z;
        float distance = dx * dx + dz * dz;
        return distance >= min * min && distance <= max * max ? Reason.None : Reason.Radius;
    }

    internal static bool Finite(Vector3 point) => DungeonSpawnConfiguration.Finite(point.x) &&
        DungeonSpawnConfiguration.Finite(point.y) && DungeonSpawnConfiguration.Finite(point.z);

    private static bool Validate(GameObject prefab, Vector3 origin, Vector3 candidate, Vector2s zone, float min, float max,
        out Vector3 position, DungeonSpawnDiagnostics.Placement? trace = null)
    {
        position = candidate;
        Reason failure = RangeFailure(origin, candidate, zone, min, max);
        if (failure != Reason.None) return trace?.Reject(failure, candidate, "candidate") ?? false;
        if (!ZoneSystem.instance.GetSolidHeight(candidate, out float floor, 1))
            return trace?.Reject(Reason.FloorMissing, candidate, "floor") ?? false;
        candidate.y = floor;
        Vector3 floorPosition = candidate;
        failure = RangeFailure(origin, candidate, zone, min, max);
        if (failure != Reason.None) return trace?.Reject(failure, candidate, "floor") ?? false;
        if (!TryPlaceOnFloor(prefab, ref candidate, trace)) return false;
        failure = RangeFailure(origin, candidate, zone, min, max);
        if (failure != Reason.None) return trace?.Reject(failure, candidate, "pivot") ?? false;
        foreach (Player player in Player.GetAllPlayers())
            if (player != null && !player.IsDead() && (player.transform.position - candidate).sqrMagnitude < min * min)
                return trace?.Reject(Reason.PlayerDistance, candidate, "players") ?? false;

        Path.Clear();
        BaseAI ai = prefab.GetComponent<BaseAI>();
        // Navigation still targets the supporting floor, not a raised prefab pivot.
        bool fullPath = Pathfinding.instance != null && Pathfinding.instance.GetPath(origin, floorPosition, Path,
            ai != null ? ai.m_pathAgentType : Pathfinding.AgentType.Humanoid, requireFullPath: true, cleanup: false, havePath: true) &&
            Path.Count > 0 && Vector3.Distance(Path[0], origin) <= 2f && Vector3.Distance(Path[Path.Count - 1], floorPosition) <= 1.25f;
        if (!fullPath)
        {
            trace?.Count(Reason.PathFailed);
            if (Physics.Linecast(origin + Vector3.up * 0.8f, floorPosition + Vector3.up * 0.8f, StaticMask, QueryTriggerInteraction.Ignore))
                return trace?.Reject(Reason.PathAndLineBlocked, floorPosition, "route") ?? false;
            trace?.Count(Reason.DirectRouteAccepted);
        }
        position = candidate;
        return true;
    }

    private static bool TryPlaceOnFloor(GameObject prefab, ref Vector3 position, DungeonSpawnDiagnostics.Placement? trace = null)
    {
        CapsuleCollider capsule = prefab.GetComponent<CapsuleCollider>();
        if (capsule == null || capsule.direction != 1)
            return trace?.Reject(Reason.UnsupportedCapsule, position, "capsule") ?? false;
        Vector3 scale = prefab.transform.lossyScale;
        float radius = Mathf.Max(0.05f, capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z)));
        float height = Mathf.Max(radius * 2, capsule.height * Mathf.Abs(scale.y));
        Vector3 centerOffset = Vector3.Scale(capsule.center, scale);
        // A prefab pivot need not be at its feet (BlobElite's capsule extends below it).
        // Raise the actual spawn point before checking clearance; keep already-clear pivots unchanged.
        position.y += Mathf.Max(0, height * 0.5f - centerOffset.y);
        // The spawn rotation is identity too; don't rotate an offset capsule differently here.
        Vector3 center = position + centerOffset + Vector3.up * 0.05f;
        float segment = Mathf.Max(0, height * 0.5f - radius);
        Vector3 bottom = center - Vector3.up * segment, top = center + Vector3.up * segment;
        float checkedRadius = Mathf.Max(0.05f, radius - 0.05f);
        int mask = StaticMask | LayerMask.GetMask("character", "character_net", "character_noenv", "character_ghost");
        if (!Physics.CheckCapsule(bottom, top, checkedRadius, mask, QueryTriggerInteraction.Ignore)) return true;
        trace?.SampleCollision(bottom, top, checkedRadius, mask);
        return trace?.Reject(Reason.CapsuleBlocked, position, "capsule") ?? false;
    }
}
