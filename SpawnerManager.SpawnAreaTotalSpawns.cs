using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
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

        ZNetView? netView = spawnArea.GetComponent<ZNetView>();
        if (netView == null ||
            !netView.IsValid() ||
            !netView.IsOwner())
        {
            return false;
        }

        Destructible? destructible = spawnArea.GetComponent<Destructible>();
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

        ZNetView? netView = spawnArea.GetComponent<ZNetView>();
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
