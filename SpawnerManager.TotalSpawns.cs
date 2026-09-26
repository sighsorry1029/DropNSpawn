using System;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static readonly int CreatureSpawnerTotalSpawnCountZdoKey = "DropNSpawn.CreatureSpawner.TotalSpawnCount".GetStableHashCode();
    private static readonly AccessTools.FieldRef<CreatureSpawner, ZNetView> CreatureSpawnerNetView =
        AccessTools.FieldRefAccess<CreatureSpawner, ZNetView>("m_nview");

    // Only the YAML override is cached; reading the global default here makes
    // live config edits effective without scanning/restarting every spawner.
    private static int ResolveCreatureSpawnerMaxTotalSpawns(int? configuredMaxTotalSpawns) =>
        configuredMaxTotalSpawns.HasValue
            ? SpawnerGlobalConfig.ClampSpawnAreaMaxTotalSpawns(configuredMaxTotalSpawns.Value)
            : PluginSettingsFacade.GetDefaultCreatureSpawnerMaxTotalSpawns();

    private static bool PrepareCreatureSpawnerTotalSpawnLimit(CreatureSpawner spawner, out ZDO? counter)
    {
        counter = null;
        int limit = ResolveCreatureSpawnerMaxTotalSpawns(LiveReconcilerState.GetAppliedCreatureSpawnerTotalSpawnLimit(spawner));
        if (limit <= 0) return true;
        ZDO? zdo = CreatureSpawnerNetView(spawner)?.GetZDO();
        if (!CanCreatureSpawnerSpawnWithLimit(zdo, limit)) return false;
        counter = zdo;
        return true;
    }

    // Group selection can call Spawn on a different member without running its
    // UpdateSpawner. Check its applied limit at selection and at the final spawn;
    // normal update/reload reconciliation owns changes to the full YAML rule.
    internal static bool PrepareCreatureSpawnerSpawn(CreatureSpawner spawner, out ZDO? counter)
    {
        counter = null;
        if (!ShouldApplyLocally()) return true;
        if (ShouldBlockClientSpawnerUpdate()) return false;
        return PrepareCreatureSpawnerTotalSpawnLimit(spawner, out counter);
    }

    internal static bool IsCreatureSpawnerGroupCandidate(CreatureSpawner spawner) =>
        PrepareCreatureSpawnerSpawn(spawner, out _);

    internal static bool CanCreatureSpawnerSpawnWithLimit(ZDO? zdo, int limit) =>
        limit <= 0 || (zdo != null && zdo.IsValid() && zdo.IsOwner() &&
                       Math.Max(0, zdo.GetInt(CreatureSpawnerTotalSpawnCountZdoKey, 0)) < limit);

    internal static void RecordCreatureSpawnerTotalSpawn(ZDO? counter, bool successful)
    {
        // Captured only for positive limits before an owned spawn. Never reset
        // counts on reload/disable, destroy the spawner, or alter native respawn
        // flags/connections: one-time and group living-count rules remain native.
        if (!successful || counter == null || !counter.IsValid() || !counter.IsOwner()) return;
        int count = Math.Max(0, counter.GetInt(CreatureSpawnerTotalSpawnCountZdoKey, 0));
        if (count < int.MaxValue) counter.Set(CreatureSpawnerTotalSpawnCountZdoKey, count + 1);
    }

    private static readonly int SpawnAreaTotalSpawnCountZdoKey = "DropNSpawn.SpawnArea.TotalSpawnCount".GetStableHashCode();
    private static readonly int SpawnAreaMaxTotalSpawnsZdoKey = "DropNSpawn.SpawnArea.MaxTotalSpawns".GetStableHashCode();

    private readonly struct SpawnAreaTotalSpawnLimitState
    {
        public SpawnAreaTotalSpawnLimitState(int maxTotalSpawns, bool fromYamlOverride)
        {
            MaxTotalSpawns = SpawnerGlobalConfig.ClampSpawnAreaMaxTotalSpawns(maxTotalSpawns);
            FromYamlOverride = fromYamlOverride;
        }

        public int MaxTotalSpawns { get; }
        public bool FromYamlOverride { get; }
    }

    private static int ClampSpawnAreaMaxTotalSpawns(int value)
    {
        return SpawnerGlobalConfig.ClampSpawnAreaMaxTotalSpawns(value);
    }

    private static int ResolveSpawnAreaMaxTotalSpawns(int? configuredMaxTotalSpawns)
    {
        return configuredMaxTotalSpawns.HasValue
            ? ClampSpawnAreaMaxTotalSpawns(configuredMaxTotalSpawns.Value)
            : PluginSettingsFacade.GetDefaultSpawnAreaMaxTotalSpawns();
    }

    private static bool ApplySpawnAreaTotalSpawnLimit(SpawnArea? spawnArea, int? configuredMaxTotalSpawns)
    {
        if (spawnArea == null)
        {
            return true;
        }

        bool fromYamlOverride = configuredMaxTotalSpawns.HasValue;
        int maxTotalSpawns = ResolveSpawnAreaMaxTotalSpawns(configuredMaxTotalSpawns);

        if (maxTotalSpawns <= 0 && !fromYamlOverride)
        {
            ClearAppliedSpawnAreaTotalSpawnLimit(spawnArea);
            return true;
        }

        SpawnAreaTotalSpawnLimitState nextState = new(maxTotalSpawns, fromYamlOverride);
        bool changed =
            !LiveReconcilerState.TryGetAppliedSpawnAreaTotalSpawnLimit(spawnArea, out SpawnAreaTotalSpawnLimitState previousState) ||
            previousState.MaxTotalSpawns != nextState.MaxTotalSpawns ||
            previousState.FromYamlOverride != nextState.FromYamlOverride;

        LiveReconcilerState.SetAppliedSpawnAreaTotalSpawnLimit(spawnArea, nextState);
        if (changed && TryGetSpawnAreaZdo(spawnArea, out ZDO zdo))
        {
            zdo.Set(SpawnAreaMaxTotalSpawnsZdoKey, maxTotalSpawns);
        }

        return !DestroySpawnAreaIfTotalSpawnLimitExhausted(spawnArea, maxTotalSpawns);
    }

    private static bool PrepareSpawnAreaTotalSpawnLimit(SpawnArea? spawnArea)
    {
        if (spawnArea == null)
        {
            return true;
        }

        if (LiveReconcilerState.TryGetAppliedSpawnAreaTotalSpawnLimit(spawnArea, out SpawnAreaTotalSpawnLimitState state) &&
            state.FromYamlOverride)
        {
            return !DestroySpawnAreaIfTotalSpawnLimitExhausted(spawnArea, state.MaxTotalSpawns);
        }

        return ApplySpawnAreaTotalSpawnLimit(spawnArea, configuredMaxTotalSpawns: null);
    }

    private static void RecordSuccessfulSpawnAreaTotalSpawn(SpawnArea? spawnArea)
    {
        if (spawnArea == null ||
            !PrepareSpawnAreaTotalSpawnLimit(spawnArea) ||
            !LiveReconcilerState.TryGetAppliedSpawnAreaTotalSpawnLimit(spawnArea, out SpawnAreaTotalSpawnLimitState state) ||
            state.MaxTotalSpawns <= 0 ||
            !TryGetSpawnAreaZdo(spawnArea, out ZDO zdo))
        {
            return;
        }

        int nextCount = Mathf.Max(0, zdo.GetInt(SpawnAreaTotalSpawnCountZdoKey, 0)) + 1;
        zdo.Set(SpawnAreaTotalSpawnCountZdoKey, nextCount);
        zdo.Set(SpawnAreaMaxTotalSpawnsZdoKey, state.MaxTotalSpawns);

        if (nextCount >= state.MaxTotalSpawns)
        {
            DestroySpawnAreaForTotalSpawnLimit(spawnArea);
        }
    }

    private static bool DestroySpawnAreaIfTotalSpawnLimitExhausted(SpawnArea? spawnArea, int maxTotalSpawns)
    {
        if (spawnArea == null ||
            maxTotalSpawns <= 0 ||
            !TryGetSpawnAreaZdo(spawnArea, out ZDO zdo))
        {
            return false;
        }

        int currentCount = Mathf.Max(0, zdo.GetInt(SpawnAreaTotalSpawnCountZdoKey, 0));
        if (currentCount < maxTotalSpawns)
        {
            return false;
        }

        return DestroySpawnAreaForTotalSpawnLimit(spawnArea);
    }

    private static bool DestroySpawnAreaForTotalSpawnLimit(SpawnArea? spawnArea)
    {
        if (spawnArea == null ||
            spawnArea.gameObject == null)
        {
            return false;
        }

        ZNetView? netView = spawnArea.GetComponentInParent<ZNetView>();
        if (netView == null ||
            !netView.IsValid() ||
            !netView.IsOwner())
        {
            return false;
        }

        Destructible? destructible = netView.GetComponent<Destructible>();
        if (destructible != null)
        {
            destructible.Destroy();
            return true;
        }

        netView.Destroy();
        return true;
    }

    private static bool TryGetSpawnAreaZdo(SpawnArea? spawnArea, out ZDO zdo)
    {
        zdo = null!;
        if (spawnArea == null)
        {
            return false;
        }

        // Match SpawnArea.Awake in Valheim 1.0.12, including child spawners.
        ZNetView? netView = spawnArea.GetComponentInParent<ZNetView>();
        if (netView == null || !netView.IsValid())
        {
            return false;
        }

        ZDO? candidate = netView.GetZDO();
        if (candidate == null)
        {
            return false;
        }

        zdo = candidate;
        return true;
    }

    private static void ClearAppliedSpawnAreaTotalSpawnLimit(SpawnArea? spawnArea)
    {
        LiveReconcilerState.RemoveAppliedSpawnAreaTotalSpawnLimit(spawnArea);
    }
}
