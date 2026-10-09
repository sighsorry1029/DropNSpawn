using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace DropNSpawn;

internal static class DungeonSpawnManager
{
    private const string RequestRpc = "DNS_DungeonPosition_v1", ReplyRpc = "DNS_DungeonPositionReply_v1";
    private static readonly int RuleTag = "DNS_DungeonRule_v1".GetStableHashCode();
    private static readonly int DungeonTag = "DNS_DungeonInstance_v1".GetStableHashCode();
    private static readonly DomainLoadState LoadState = new();
    private static List<DungeonSpawnRule> Rules = new();
    private static readonly Dictionary<(string Rule, string Dungeon), Schedule> Schedules = new();
    private static readonly Dictionary<long, Pending> Requests = new();
    private static readonly HashSet<ZDOID> Tracked = new();
    private static readonly HashSet<string> Warnings = new(StringComparer.Ordinal);
    private static ZRoutedRpc? RegisteredRpc;
    private static ZDOMan? World;
    private static double NextTick;
    private static long Sequence;
    private static bool Active, HasRules, Restored, CompatibilityResolved, CompatibilityUnavailable;
    private static Func<GameObject, Vector3, bool>? CanSpawnWithCreatureManager;

    internal sealed class Schedule
    {
        internal double Due;
        internal int PendingCount;
        internal int BeginInterval(double now, float interval, int players)
        {
            if (now < Due) return 0;
            Due = now + interval; // no catch-up after a stall or failed attempt
            return players;
        }

        internal bool HasCapacity(int alive, int maxAlive) => alive + PendingCount < maxAlive;

        internal bool TryReserve(int alive, int maxAlive)
        {
            if (!HasCapacity(alive, maxAlive)) return false;
            PendingCount++;
            return true;
        }
    }

    private sealed class PlayerContext
    {
        internal long Peer;
        internal ZDOID Character;
        internal Vector3 Position;
    }

    private sealed class Dungeon
    {
        internal string Id = "", Location = "";
        internal Vector2s Zone;
        internal readonly List<PlayerContext> Players = new();
    }

    private sealed class Pending
    {
        internal DungeonSpawnRule Rule = null!;
        internal Dungeon Dungeon = null!;
        internal PlayerContext Player = null!;
        internal Schedule Schedule = null!;
        internal GameObject Prefab = null!;
        internal double Expires;
    }

    internal static void Initialize()
    {
        Active = true;
        string path = Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, "DNS_dungeon.yml");
        if (!DomainConfigurationFileSupport.HasAnyOverrideConfigurationFile("dungeon", path, Path.ChangeExtension(path, ".yaml")))
            GeneratedArtifactWriter.WriteTextAlways(path, DungeonSpawnConfiguration.DefaultContent);
        Reload();
    }

    internal static bool ShouldReloadForPath(string path) =>
        DomainConfigurationFileSupport.IsOverrideConfigurationFileName("dungeon", Path.GetFileName(path));

    internal static void Reload()
    {
        if (!DropNSpawnPlugin.IsSourceOfTruth) { Rules.Clear(); HasRules = false; ResetTimers(); ConfigurationDomainHost.ResetLoadState(LoadState); return; }
        string path = Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, "DNS_dungeon.yml");
        var paths = DomainConfigurationFileSupport.EnumerateOverrideConfigurationPaths("dungeon", path, Path.ChangeExtension(path, ".yaml")).ToList();
        ConfigurationDomainHost.RunSourceOfTruthReload(LoadState, paths,
            new DomainLoadHooks<DungeonSpawnRule, List<DungeonSpawnRule>>(
                ParseDocuments, (entries, _) => entries,
                (entries, payload) => { Rules = entries; HasRules = entries.Exists(rule => rule.Enabled && rule.SpawnChance > 0); LoadState.LastLoadedPayload = payload; ResetTimers(); Warnings.Clear(); },
                (payload, errors) => ConfigurationDomainHost.RejectLocalConfigurationPayload(LoadState, payload, errors, "dungeon"),
                entries => entries.Count,
                logLocalLoadSuccess: (count, files) => DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Loaded {count} dungeon spawn rules from {files} file(s).")));
    }

    internal static LocalLoadResult<DungeonSpawnRule> ParseDocuments(List<ConfigurationLoadSupport.LocalYamlDocument> documents)
    {
        LocalLoadResult<DungeonSpawnRule> result = new();
        foreach (var document in documents)
        {
            try
            {
                if (document.ReadError != null) throw new IOException(document.ReadError);
                result.Entries.AddRange(DungeonSpawnConfiguration.Parse(document.Yaml ?? ""));
                result.LoadedFileCount++;
            }
            catch (Exception ex) { result.Errors.Add($"{document.Path}: {ex.Message}"); }
        }
        try { DungeonSpawnConfiguration.Validate(result.Entries); }
        catch (Exception ex) { result.Errors.Add(ex.Message); }
        result.ParsedEntryCount = result.Entries.Count;
        return result;
    }

    internal static void ResetWorld()
    {
        ResetTimers(); Tracked.Clear(); World = null; Restored = false;
        NextTick = 0;
        Warnings.Clear(); CompatibilityResolved = CompatibilityUnavailable = false; CanSpawnWithCreatureManager = null;
        // RPC handlers survive world shutdown on the old instance. Keep its registration
        // record until Tick observes a new instance, including after Dispose/reinitialization.
        // Sequence deliberately survives world changes: late replies cannot match a new request.
    }

    internal static void Dispose() { Active = HasRules = false; ResetWorld(); Rules.Clear(); ConfigurationDomainHost.ResetLoadState(LoadState); }

    private static void ResetTimers() { Requests.Clear(); Schedules.Clear(); }

    internal static void Tick()
    {
        if (!Active || ZNet.instance == null || !ZNet.instance.enabled || ZRoutedRpc.instance == null ||
            ZDOMan.instance == null || ZNetScene.instance == null || !ZNetScene.instance.enabled) return;
        if (!ReferenceEquals(RegisteredRpc, ZRoutedRpc.instance))
        {
            RegisteredRpc = ZRoutedRpc.instance;
            RegisteredRpc.Register<ZPackage>(RequestRpc, OnPositionRequest);
            RegisteredRpc.Register<ZPackage>(ReplyRpc, OnPositionReply);
        }
        if (!HasRules || !DropNSpawnPlugin.IsRuntimeServer() || ZoneSystem.instance == null) return;
        double now = Time.realtimeSinceStartupAsDouble;
        if (now < NextTick) return;
        NextTick = now + 1;
        try { TickServer(now); }
        catch (Exception ex) { Warn("tick:" + ex.GetType().Name, $"Dungeon spawn check skipped: {ex.Message}"); }
    }

    private static void TickServer(double now)
    {
        if (!ReferenceEquals(World, ZDOMan.instance))
        {
            World = ZDOMan.instance; Tracked.Clear(); Restored = false; ResetTimers();
        }
        if (!Restored)
        {
            // One metadata lookup per world, not a full creature/world scan every interval.
            foreach (ZDOID id in ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.String, RuleTag)) Tracked.Add(id);
            Restored = true;
        }
        List<PlayerContext> players = ConnectedPlayers();
        Dictionary<string, Dungeon> occupied = new(StringComparer.Ordinal);
        foreach (PlayerContext player in players)
        {
            if (!TryDungeon(player.Position, out Dungeon dungeon)) continue;
            if (!occupied.TryGetValue(dungeon.Id, out Dungeon existing)) occupied.Add(dungeon.Id, existing = dungeon);
            existing.Players.Add(player);
        }
        foreach (long token in Requests.Where(pair => pair.Value.Expires <= now ||
                     !occupied.TryGetValue(pair.Value.Dungeon.Id, out Dungeon present) ||
                     !present.Players.Exists(player => player.Peer == pair.Value.Player.Peer && player.Character == pair.Value.Player.Character))
                 .Select(pair => pair.Key).ToArray())
        {
            Pending pending = Requests[token];
            if (pending.Expires <= now && occupied.ContainsKey(pending.Dungeon.Id))
                Warn("timeout:" + pending.Player.Peer, "Dungeon position request timed out; no creature spawned. Remote clients need the updated DropNSpawn build and a loaded dungeon scene.");
            Cancel(token);
        }
        foreach (var key in Schedules.Keys.Where(key => !occupied.ContainsKey(key.Dungeon)).ToArray()) Schedules.Remove(key);
        foreach (Dungeon dungeon in occupied.Values)
        foreach (DungeonSpawnRule rule in Rules)
        {
            if (!rule.Enabled || rule.SpawnChance <= 0 || !rule.Locations.Contains(dungeon.Location, StringComparer.Ordinal)) continue;
            var key = (rule.Id, dungeon.Id);
            if (!Schedules.TryGetValue(key, out Schedule schedule))
            {
                Schedules.Add(key, new Schedule { Due = now + rule.SpawnInterval });
                continue;
            }
            int attempts = schedule.BeginInterval(now, rule.SpawnInterval, dungeon.Players.Count);
            if (attempts == 0) continue;
            // One roll per present player. Rotate the starting player so a nearly full
            // shared cap does not always favor the first connected peer (or local host).
            int start = UnityEngine.Random.Range(0, attempts);
            for (int i = 0; i < attempts; i++)
            {
                if (!schedule.HasCapacity(CountAlive(rule.Id, dungeon.Id), rule.MaxAlive)) break;
                if (UnityEngine.Random.value * 100 >= rule.SpawnChance) continue;
                string creature = SelectCreature(rule, UnityEngine.Random.value);
                GameObject prefab = ZNetScene.instance.GetPrefab(creature);
                if (!Supported(prefab)) { Warn("prefab:" + creature, $"Dungeon spawn skipped: '{creature}' must be a registered ordinary creature with a persistent ZNetView and upright capsule."); continue; }
                PlayerContext target = dungeon.Players[(start + i) % attempts];
                if (!AllowSpawn(prefab, target.Position) || !schedule.TryReserve(CountAlive(rule.Id, dungeon.Id), rule.MaxAlive)) continue;
                long token = ++Sequence;
                Pending request = new() { Rule = rule, Dungeon = dungeon, Player = target, Schedule = schedule, Prefab = prefab, Expires = now + 10 };
                Requests.Add(token, request);
                try
                {
                    if (target.Peer == ZNet.GetUID())
                    {
                        bool found = DungeonSpawnPlacement.Find(prefab, target.Position, dungeon.Zone, rule.SpawnRadius.Min!.Value, rule.SpawnRadius.Max!.Value, out Vector3 position);
                        Complete(token, target.Peer, found, position);
                    }
                    else
                    {
                        ZPackage package = new();
                        package.Write(token); package.Write(target.Character); package.Write(prefab.name.GetStableHashCode());
                        package.Write(target.Position); package.Write(rule.SpawnRadius.Min!.Value); package.Write(rule.SpawnRadius.Max!.Value);
                        RegisteredRpc!.InvokeRoutedRPC(target.Peer, RequestRpc, package);
                    }
                }
                catch (Exception ex)
                {
                    Cancel(token);
                    Warn("dispatch:" + ex.GetType().Name, $"Dungeon spawn attempt skipped: {ex.Message}");
                }
            }
        }
    }

    internal static string SelectCreature(DungeonSpawnRule rule, float roll)
    {
        double total = rule.Creatures.Sum(creature => (double)creature.Weight);
        double cursor = Math.Min(1, Math.Max(0, roll)) * total;
        foreach (DungeonSpawnCreature creature in rule.Creatures)
        {
            cursor -= creature.Weight;
            if (cursor < 0) return creature.Prefab;
        }
        return rule.Creatures[rule.Creatures.Count - 1].Prefab;
    }

    private static List<PlayerContext> ConnectedPlayers()
    {
        List<PlayerContext> players = new(); HashSet<ZDOID> seen = new();
        foreach (ZNetPeer peer in ZNet.instance.GetConnectedPeers())
        {
            if (!peer.IsReady()) continue;
            ZDO zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
            AddPlayer(players, seen, peer.m_uid, zdo);
        }
        Player local = Player.m_localPlayer;
        if (local != null && !local.IsDead()) AddPlayer(players, seen, ZNet.GetUID(), local.GetComponent<ZNetView>()?.GetZDO());
        return players;
    }

    private static void AddPlayer(List<PlayerContext> players, HashSet<ZDOID> seen, long peer, ZDO? zdo)
    {
        if (zdo == null || zdo.m_uid.IsNone() || zdo.GetOwner() != peer || !IsAlive(zdo) || !DungeonSpawnPlacement.Finite(zdo.GetPosition())) return;
        GameObject prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        if (prefab == null || prefab.GetComponent<Player>() == null || !seen.Add(zdo.m_uid)) return;
        players.Add(new PlayerContext { Peer = peer, Character = zdo.m_uid, Position = zdo.GetPosition() });
    }

    private static bool TryDungeon(Vector3 position, out Dungeon dungeon)
    {
        dungeon = null!;
        if (!DungeonSpawnPlacement.Finite(position) || !Character.InInterior(position)) return false;
        Vector2s zone = ZoneSystem.GetZone(position);
        if (!ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance location)) return false;
        string name = SpawnerManager.GetZoneLocationPrefabName(location.m_location);
        float radius = Mathf.Max(location.m_location.m_exteriorRadius, location.m_location.m_interiorRadius);
        if (name.Length == 0 || (radius > 0 && Utils.DistanceXZ(location.m_position, position) > radius)) return false;
        dungeon = new Dungeon { Id = name + ":" + zone.x + ":" + zone.y, Location = name, Zone = zone };
        return true;
    }

    internal static bool IsAlive(ZDO zdo)
    {
        float health = zdo.GetFloat(ZDOVars.s_health, float.PositiveInfinity);
        return zdo.IsValid() && !zdo.GetBool(ZDOVars.s_dead, false) && !float.IsNaN(health) && health > 0;
    }

    private static int CountAlive(string rule, string dungeon)
    {
        // Keep dead-but-existing IDs: revival mods can restore health on the same ZDO.
        Tracked.RemoveWhere(id => { ZDO zdo = ZDOMan.instance.GetZDO(id); return zdo == null || !zdo.IsValid(); });
        int count = 0;
        foreach (ZDOID id in Tracked)
        {
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (IsAlive(zdo) && zdo.GetString(RuleTag) == rule && zdo.GetString(DungeonTag) == dungeon) count++;
        }
        return count;
    }

    private static bool IsSpawnPointOccupied(Vector3 position)
    {
        // Simultaneous client replies can select the same anchor before the first
        // creature appears in either physics scene. Check current ZDO positions,
        // across all dungeon rules, without a separate cache or a new chance roll.
        foreach (ZDOID id in Tracked)
        {
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo != null && IsAlive(zdo) && (zdo.GetPosition() - position).sqrMagnitude < 0.1f * 0.1f) return true;
        }
        return false;
    }

    private static bool Supported(GameObject? prefab)
    {
        if (prefab == null) return false;
        Character character = prefab.GetComponent<Character>();
        CapsuleCollider capsule = prefab.GetComponent<CapsuleCollider>();
        ZNetView view = prefab.GetComponent<ZNetView>();
        return character != null && !character.IsPlayer() && !character.IsBoss() && view != null && view.m_persistent && capsule != null && capsule.direction == 1;
    }

    private static void OnPositionRequest(long sender, ZPackage package)
    {
        if (!Active || DropNSpawnPlugin.IsRuntimeServer() || ZNet.instance == null || ZNet.instance.GetServerPeer()?.m_uid != sender ||
            package == null || package.Size() > 256) return;
        long token = 0;
        try
        {
            token = package.ReadLong(); ZDOID playerId = package.ReadZDOID(); int prefabHash = package.ReadInt();
            Vector3 origin = package.ReadVector3(); float min = package.ReadSingle(), max = package.ReadSingle();
            Player player = Player.m_localPlayer;
            ZDO? local = player != null ? player.GetComponent<ZNetView>()?.GetZDO() : null;
            if (player == null || local == null || local.m_uid != playerId || player.IsDead() || !DungeonSpawnPlacement.Finite(origin) ||
                !DungeonSpawnConfiguration.Finite(min) || !DungeonSpawnConfiguration.Finite(max) || min < 2 || max < min || max > 64 ||
                !Character.InInterior(player.transform.position) || (player.transform.position - origin).sqrMagnitude > 16) return;
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabHash);
            bool found = Supported(prefab) && DungeonSpawnPlacement.Find(prefab, player.transform.position, ZoneSystem.GetZone(origin), min, max, out origin);
            ZPackage reply = new(); reply.Write(token); reply.Write(found); reply.Write(origin);
            RegisteredRpc!.InvokeRoutedRPC(sender, ReplyRpc, reply);
        }
        catch (Exception ex) { Warn("request:" + ex.GetType().Name, $"Dungeon position request {token} skipped: {ex.Message}"); }
    }

    private static void OnPositionReply(long sender, ZPackage package)
    {
        if (!Active || !DropNSpawnPlugin.IsRuntimeServer() || package == null || package.Size() > 64) return;
        try { Complete(package.ReadLong(), sender, package.ReadBool(), package.ReadVector3()); }
        catch (Exception ex) { Warn("reply:" + ex.GetType().Name, $"Dungeon position reply skipped: {ex.Message}"); }
    }

    private static void Cancel(long token)
    {
        if (!Requests.TryGetValue(token, out Pending request)) return;
        Requests.Remove(token); request.Schedule.PendingCount--;
    }

    private static void Complete(long token, long sender, bool found, Vector3 position)
    {
        Pending? request = TakeRequest(token, sender, Time.realtimeSinceStartupAsDouble);
        if (request == null || !found) return;
        List<PlayerContext> players = ConnectedPlayers();
        PlayerContext target = players.Find(player => player.Peer == sender && player.Character == request.Player.Character);
        DungeonSpawnRule rule = request.Rule;
        if (target == null || !TryDungeon(target.Position, out Dungeon current) || current.Id != request.Dungeon.Id ||
            !DungeonSpawnPlacement.WithinRange(target.Position, position, current.Zone, rule.SpawnRadius.Min!.Value, rule.SpawnRadius.Max!.Value) ||
            players.Exists(player => (player.Position - position).sqrMagnitude < rule.SpawnRadius.Min.Value * rule.SpawnRadius.Min.Value) ||
            !request.Schedule.HasCapacity(CountAlive(rule.Id, current.Id), rule.MaxAlive) ||
            IsSpawnPointOccupied(position) || !AllowSpawn(request.Prefab, position)) return;

        ZDO? zdo = null;
        try
        {
            ZNetView template = request.Prefab.GetComponent<ZNetView>();
            // Native ZNetScene creates the ordinary creature from this ZDO on the receiving
            // peer. No server-side Instantiate in an unloaded dungeon, no summon/loot flags.
            zdo = ZDOMan.instance.CreateNewZDO(position, request.Prefab.name.GetStableHashCode());
            zdo.SetPrefab(request.Prefab.name.GetStableHashCode());
            zdo.Persistent = template.m_persistent; zdo.Type = template.m_type; zdo.Distant = template.m_distant;
            zdo.SetRotation(Quaternion.identity);
            if (template.m_syncInitialScale) zdo.Set(ZDOVars.s_scaleHash, request.Prefab.transform.localScale);
            zdo.Set(RuleTag, rule.Id); zdo.Set(DungeonTag, current.Id);
            Tracked.Add(zdo.m_uid);
            zdo.SetOwner(sender);
        }
        catch
        {
            if (zdo != null) { Tracked.Remove(zdo.m_uid); zdo.SetOwner(ZDOMan.GetSessionID()); ZDOMan.instance.DestroyZDO(zdo); }
            throw;
        }
    }

    private static Pending? TakeRequest(long token, long sender, double now)
    {
        if (!Requests.TryGetValue(token, out Pending request) || request.Player.Peer != sender) return null;
        Cancel(token); // consume before external hooks or creation; duplicate replies cannot spawn twice
        return request.Expires >= now && Rules.Contains(request.Rule) ? request : null;
    }

    private static bool AllowSpawn(GameObject prefab, Vector3 position)
    {
        if (!CompatibilityResolved)
        {
            CompatibilityResolved = true;
            try
            {
                if (Chainloader.PluginInfos.TryGetValue("sighsorry.CreatureManager", out var plugin))
                {
                    MethodInfo? method = plugin.Instance.GetType().Assembly.GetType("CreatureManager.CreatureManagerSpawnApi")?
                        .GetMethod("CanSpawn", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject), typeof(Vector3) }, null);
                    if (method != null && method.ReturnType == typeof(bool))
                        CanSpawnWithCreatureManager = (Func<GameObject, Vector3, bool>)Delegate.CreateDelegate(typeof(Func<GameObject, Vector3, bool>), method);
                    else { CompatibilityUnavailable = true; Warn("cm-api", "Dungeon spawns paused: installed CreatureManager lacks CreatureManagerSpawnApi.CanSpawn. Update CreatureManager to preserve its spawn blocking policy."); }
                }
            }
            catch (Exception ex) { CompatibilityUnavailable = true; Warn("cm-bind", $"Dungeon spawns paused: CreatureManager API binding failed: {ex.Message}"); }
        }
        if (CompatibilityUnavailable) return false;
        try { return CanSpawnWithCreatureManager?.Invoke(prefab, position) ?? true; }
        catch (Exception ex) { Warn("cm-call", $"Dungeon spawn blocked after CreatureManager API error: {ex.Message}"); return false; }
    }

    private static void Warn(string key, string message)
    {
        if (Warnings.Add(key)) DropNSpawnPlugin.DropNSpawnLogger.LogWarning(message);
    }
}
