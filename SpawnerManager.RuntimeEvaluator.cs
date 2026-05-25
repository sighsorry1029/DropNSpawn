using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    internal static void ReconcileSpawnAreaRuntime(SpawnArea spawnArea)
    {
        ReconcileSpawnAreaRuntimeCore(spawnArea);
    }

    internal static bool PrepareSpawnAreaForUpdate(SpawnArea spawnArea)
    {
        ReconcileSpawnAreaRuntimeCore(spawnArea);
        return PrepareSpawnAreaTotalSpawnLimit(spawnArea);
    }

    internal static void ReconcileCreatureSpawnerRuntime(CreatureSpawner creatureSpawner)
    {
        ReconcileCreatureSpawnerRuntimeCore(creatureSpawner);
    }

    internal static bool PrepareCreatureSpawnerForUpdate(CreatureSpawner creatureSpawner)
    {
        return PrepareCreatureSpawnerForUpdateCore(creatureSpawner);
    }

    private static void ReconcileSpawnAreaRuntimeCore(SpawnArea? spawnArea)
    {
        if (spawnArea == null)
        {
            return;
        }

        if (!ShouldApplyLocally())
        {
            ClearSpawnAreaDynamicRuntimeState(spawnArea);
            return;
        }

        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot = GetRuntimeConfigurationSnapshot();
        if (!TryGetTrackedOrCurrentSpawnAreaEligibility(
                spawnArea,
                runtimeConfigurationSnapshot,
                out string configPrefabName,
                out _,
                out bool runtimeEligible) ||
            !runtimeEligible)
        {
            ClearSpawnAreaDynamicRuntimeState(spawnArea);
            return;
        }

        TrackSpawnAreaInstanceInternal(spawnArea);

        if (!IsGameDataReady())
        {
            return;
        }

        if (!TryGetActiveSpawnAreaEntryCache(
                spawnArea,
                runtimeConfigurationSnapshot,
                out MatchingEntryCache? entryCache,
                out _))
        {
            ClearSpawnAreaDynamicRuntimeState(spawnArea, clearMatchingCache: true);
            return;
        }

        IReadOnlyList<SpawnerRuntimeEntry> runtimeEntries = entryCache!.RuntimeEntries;
        if (runtimeEntries.Count == 0)
        {
            ClearSpawnAreaDynamicRuntimeState(spawnArea, clearMatchingCache: true);
            return;
        }

        LocalRuntimeState localRuntimeState = RuntimeStateStore.GetOrCreateLocalRuntimeState(spawnArea);
        RuntimeContextSnapshot runtimeContext = GetRuntimeContextSnapshot();
        if (!ShouldEvaluateRuntimeState(entryCache, localRuntimeState, runtimeContext) &&
            RuntimeStateStore.HasRuntimeSignature(spawnArea))
        {
            return;
        }

        ArmNextRuntimeEvaluationWindow(entryCache, localRuntimeState, runtimeContext);
        int runtimeSignature = ComputeRuntimeConditionSignature(spawnArea.gameObject, entryCache, localRuntimeState, runtimeContext);
        if (RuntimeStateStore.TryGetRuntimeSignature(spawnArea, out int previousSignature) &&
            previousSignature == runtimeSignature)
        {
            return;
        }

        TrySelectWinningSpawnerEntryForRuntime(
            spawnArea.gameObject,
            entryCache,
            forSpawnArea: true,
            runtimeContext,
            localRuntimeState,
            runtimeSignature,
            out SpawnerRuntimeEntry? winningEntry);
        if (HasMatchingAppliedWinningEntry(localRuntimeState, entryCache, configPrefabName, winningEntry))
        {
            RuntimeStateStore.SetRuntimeSignature(spawnArea, runtimeSignature);
            return;
        }

        ReconcileSpawnAreaInstanceInternal(
            spawnArea,
            entries: null,
            entryCache,
            usePreselectedWinner: true,
            winningEntry);
        RecordAppliedWinningEntry(localRuntimeState, entryCache, configPrefabName, winningEntry);
    }

    private static void ReconcileCreatureSpawnerRuntimeCore(CreatureSpawner? creatureSpawner)
    {
        if (creatureSpawner == null)
        {
            return;
        }

        if (!ShouldApplyLocally())
        {
            ClearCreatureSpawnerDynamicRuntimeState(creatureSpawner);
            return;
        }

        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot = GetRuntimeConfigurationSnapshot();
        if (!TryGetTrackedOrCurrentCreatureSpawnerEligibility(
                creatureSpawner,
                runtimeConfigurationSnapshot,
                out string configPrefabName,
                out _,
                out bool runtimeEligible) ||
            !runtimeEligible)
        {
            ClearCreatureSpawnerDynamicRuntimeState(creatureSpawner);
            return;
        }

        TrackCreatureSpawnerInstanceInternal(creatureSpawner);

        if (!IsGameDataReady())
        {
            return;
        }

        if (!TryGetActiveCreatureSpawnerEntryCache(
                creatureSpawner,
                runtimeConfigurationSnapshot,
                out MatchingEntryCache? entryCache,
                out _))
        {
            ClearCreatureSpawnerDynamicRuntimeState(creatureSpawner, clearMatchingCache: true);
            return;
        }

        IReadOnlyList<SpawnerRuntimeEntry> runtimeEntries = entryCache!.RuntimeEntries;
        if (runtimeEntries.Count == 0)
        {
            ClearCreatureSpawnerDynamicRuntimeState(creatureSpawner, clearMatchingCache: true);
            return;
        }

        LocalRuntimeState localRuntimeState = RuntimeStateStore.GetOrCreateLocalRuntimeState(creatureSpawner);
        RuntimeContextSnapshot runtimeContext = GetRuntimeContextSnapshot();
        if (!ShouldEvaluateRuntimeState(entryCache, localRuntimeState, runtimeContext) &&
            RuntimeStateStore.HasRuntimeSignature(creatureSpawner))
        {
            return;
        }

        ArmNextRuntimeEvaluationWindow(entryCache, localRuntimeState, runtimeContext);
        int runtimeSignature = ComputeRuntimeConditionSignature(creatureSpawner.gameObject, entryCache, localRuntimeState, runtimeContext);
        if (RuntimeStateStore.TryGetRuntimeSignature(creatureSpawner, out int previousSignature) &&
            previousSignature == runtimeSignature)
        {
            return;
        }

        TrySelectWinningSpawnerEntryForRuntime(
            creatureSpawner.gameObject,
            entryCache,
            forSpawnArea: false,
            runtimeContext,
            localRuntimeState,
            runtimeSignature,
            out SpawnerRuntimeEntry? winningEntry);
        if (HasMatchingAppliedWinningEntry(localRuntimeState, entryCache, configPrefabName, winningEntry))
        {
            RuntimeStateStore.SetRuntimeSignature(creatureSpawner, runtimeSignature);
            return;
        }

        ReconcileCreatureSpawnerInstanceInternal(
            creatureSpawner,
            entries: null,
            entryCache,
            usePreselectedWinner: true,
            winningEntry);
        RecordAppliedWinningEntry(localRuntimeState, entryCache, configPrefabName, winningEntry);
    }

    private static bool PrepareCreatureSpawnerForUpdateCore(CreatureSpawner? creatureSpawner)
    {
        ReconcileCreatureSpawnerRuntimeCore(creatureSpawner);
        if (creatureSpawner == null)
        {
            return true;
        }

        bool timeOfDayAllowed =
            !LiveReconcilerState.TryGetAppliedCreatureSpawnerTimeOfDay(creatureSpawner, out TimeOfDayDefinition timeOfDay) ||
            TimeOfDayFormatting.MatchesCurrentTime(timeOfDay);
        if (!timeOfDayAllowed)
        {
            return false;
        }

        if (BossRulesManager.ShouldBlockConfiguredSameBossSpawn(
                creatureSpawner.m_creaturePrefab,
                creatureSpawner.transform.position))
        {
            return false;
        }

        return true;
    }

    private static void ClearRuntimeReconcileState()
    {
        RuntimeStateStore.ClearDynamicCaches();
        SelectorCacheStore.ClearSharedMatchingEntryTemplates();
    }

    private static void UpdateSpawnAreaRuntimeSignature(SpawnArea spawnArea, IEnumerable<SpawnerRuntimeEntry>? entries, MatchingEntryCache? entryCache = null)
    {
        IReadOnlyList<SpawnerRuntimeEntry> runtimeEntries = entryCache?.RuntimeEntries ??
                                                            entries?.Where(entry => entry.RuntimeReconcile).ToList() ??
                                                            new List<SpawnerRuntimeEntry>();
        if (runtimeEntries.Count == 0)
        {
            ClearSpawnAreaDynamicRuntimeState(spawnArea);
            return;
        }

        RuntimeStateStore.SetRuntimeSignature(spawnArea, entryCache != null
            ? ComputeSpawnAreaRuntimeSignature(spawnArea, entryCache)
            : ComputeSpawnAreaRuntimeSignature(spawnArea, runtimeEntries));
    }

    private static void UpdateCreatureSpawnerRuntimeSignature(CreatureSpawner creatureSpawner, IEnumerable<SpawnerRuntimeEntry>? entries, MatchingEntryCache? entryCache = null)
    {
        IReadOnlyList<SpawnerRuntimeEntry> runtimeEntries = entryCache?.RuntimeEntries ??
                                                            entries?.Where(entry => entry.RuntimeReconcile).ToList() ??
                                                            new List<SpawnerRuntimeEntry>();
        if (runtimeEntries.Count == 0)
        {
            ClearCreatureSpawnerDynamicRuntimeState(creatureSpawner);
            return;
        }

        RuntimeStateStore.SetRuntimeSignature(creatureSpawner, entryCache != null
            ? ComputeCreatureSpawnerRuntimeSignature(creatureSpawner, entryCache)
            : ComputeCreatureSpawnerRuntimeSignature(creatureSpawner, runtimeEntries));
    }

    private static int ComputeSpawnAreaRuntimeSignature(SpawnArea spawnArea, MatchingEntryCache entryCache)
    {
        return ComputeRuntimeConditionSignature(
            spawnArea.gameObject,
            entryCache,
            RuntimeStateStore.GetOrCreateLocalRuntimeState(spawnArea));
    }

    private static int ComputeCreatureSpawnerRuntimeSignature(CreatureSpawner creatureSpawner, MatchingEntryCache entryCache)
    {
        return ComputeRuntimeConditionSignature(
            creatureSpawner.gameObject,
            entryCache,
            RuntimeStateStore.GetOrCreateLocalRuntimeState(creatureSpawner));
    }

    private static int ComputeSpawnAreaRuntimeSignature(SpawnArea spawnArea, IEnumerable<SpawnerRuntimeEntry> entries)
    {
        int signature = 17;
        foreach (SpawnerRuntimeEntry entry in entries ?? Enumerable.Empty<SpawnerRuntimeEntry>())
        {
            if (!entry.RuntimeReconcile)
            {
                continue;
            }

            signature = CombineRuntimeSignature(signature, entry.RuleId);
            bool topLevelSatisfied = !HasRuntimeEntryConditions(entry) ||
                                     DropConditionEvaluator.AreSatisfied(spawnArea.gameObject, entry.Conditions);
            signature = CombineRuntimeSignature(signature, topLevelSatisfied);
        }

        return signature;
    }

    private static int ComputeRuntimeConditionSignature(GameObject gameObject, MatchingEntryCache entryCache, LocalRuntimeState localRuntimeState)
    {
        return ComputeRuntimeConditionSignature(gameObject, entryCache, localRuntimeState, GetRuntimeContextSnapshot());
    }

    private static int ComputeRuntimeConditionSignature(
        GameObject gameObject,
        MatchingEntryCache entryCache,
        LocalRuntimeState localRuntimeState,
        RuntimeContextSnapshot runtimeContext)
    {
        int signature = 17;
        foreach (SpawnerRuntimeEntry entry in entryCache.RuntimeEntries)
        {
            signature = CombineRuntimeSignature(signature, entry.RuleId);
        }

        if (entryCache.UsesTimeOfDay)
        {
            signature = CombineRuntimeSignature(signature, runtimeContext.TimeOfDayPhaseMarker);
        }

        if (entryCache.UsesRequiredEnvironments)
        {
            signature = CombineRuntimeSignature(signature, runtimeContext.EnvironmentName);
        }

        if (entryCache.UsesInsidePlayerBase)
        {
            signature = CombineRuntimeSignature(signature, GetInsidePlayerBaseState(gameObject, localRuntimeState));
        }

        foreach (string key in entryCache.RuntimeRequiredGlobalKeys)
        {
            signature = CombineRuntimeSignature(signature, key);
            signature = CombineRuntimeSignature(signature, GetGlobalKeyState(runtimeContext, key));
        }

        foreach (string key in entryCache.RuntimeForbiddenGlobalKeys)
        {
            signature = CombineRuntimeSignature(signature, key);
            signature = CombineRuntimeSignature(signature, GetGlobalKeyState(runtimeContext, key));
        }

        return signature;
    }

    private static int ComputeCreatureSpawnerRuntimeSignature(CreatureSpawner creatureSpawner, IEnumerable<SpawnerRuntimeEntry> entries)
    {
        int signature = 17;
        foreach (SpawnerRuntimeEntry entry in entries ?? Enumerable.Empty<SpawnerRuntimeEntry>())
        {
            if (!entry.RuntimeReconcile)
            {
                continue;
            }

            signature = CombineRuntimeSignature(signature, entry.RuleId);
            bool satisfied = !HasRuntimeEntryConditions(entry) ||
                             DropConditionEvaluator.AreSatisfied(creatureSpawner.gameObject, entry.Conditions);
            signature = CombineRuntimeSignature(signature, satisfied);
        }

        return signature;
    }

    private static void ClearSpawnAreaDynamicRuntimeState(SpawnArea? spawnArea, bool clearMatchingCache = false)
    {
        if (spawnArea == null)
        {
            return;
        }

        RuntimeStateStore.RemoveRuntimeSignature(spawnArea);
        RuntimeStateStore.RemoveLocalRuntimeState(spawnArea);
        if (clearMatchingCache)
        {
            SelectorCacheStore.RemoveSpawnAreaEntryCache(spawnArea);
        }
    }

    private static void ClearCreatureSpawnerDynamicRuntimeState(CreatureSpawner? creatureSpawner, bool clearMatchingCache = false)
    {
        if (creatureSpawner == null)
        {
            return;
        }

        RuntimeStateStore.RemoveRuntimeSignature(creatureSpawner);
        RuntimeStateStore.RemoveLocalRuntimeState(creatureSpawner);
        if (clearMatchingCache)
        {
            SelectorCacheStore.RemoveCreatureSpawnerEntryCache(creatureSpawner);
        }
    }

    private static bool HasMatchingAppliedWinningEntry(
        LocalRuntimeState localRuntimeState,
        MatchingEntryCache entryCache,
        string configPrefabName,
        SpawnerRuntimeEntry? winningEntry)
    {
        if (!localRuntimeState.HasAppliedWinningEntrySelection)
        {
            return false;
        }

        if (!string.Equals(localRuntimeState.LastAppliedConfigPrefabName, configPrefabName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(localRuntimeState.LastAppliedResolvedLocationKey, entryCache.ResolvedLocationKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string currentRuleId = winningEntry?.RuleId ?? "";
        return string.Equals(localRuntimeState.LastAppliedWinningEntryRuleId, currentRuleId, StringComparison.Ordinal);
    }

    private static void RecordAppliedWinningEntry(
        LocalRuntimeState localRuntimeState,
        MatchingEntryCache entryCache,
        string configPrefabName,
        SpawnerRuntimeEntry? winningEntry)
    {
        localRuntimeState.HasAppliedWinningEntrySelection = true;
        localRuntimeState.LastAppliedConfigPrefabName = configPrefabName ?? "";
        localRuntimeState.LastAppliedResolvedLocationKey = entryCache.ResolvedLocationKey ?? "";
        localRuntimeState.LastAppliedWinningEntryRuleId = winningEntry?.RuleId ?? "";
    }

    private static int CombineRuntimeSignature(int current, bool value)
    {
        unchecked
        {
            return (current * 31) + (value ? 1 : 0);
        }
    }

    private static int CombineRuntimeSignature(int current, int value)
    {
        unchecked
        {
            return (current * 31) + value;
        }
    }

    private static int CombineRuntimeSignature(int current, string value)
    {
        unchecked
        {
            return (current * 31) + (value?.GetHashCode() ?? 0);
        }
    }

    private static RuntimeContextSnapshot GetRuntimeContextSnapshot()
    {
        int currentFrame = Time.frameCount;
        if (RuntimeStateStore.TryGetRuntimeContextSnapshot(currentFrame, out RuntimeContextSnapshot cachedSnapshot))
        {
            return cachedSnapshot;
        }

        RuntimeContextSnapshot snapshot = new RuntimeContextSnapshot
        {
            Frame = currentFrame,
            TimeOfDayPhaseMarker = TimeOfDayFormatting.GetCurrentRuntimePhaseMarker(),
            EnvironmentName = EnvMan.instance?.GetCurrentEnvironment()?.m_name ?? ""
        };

        return RuntimeStateStore.SetRuntimeContextSnapshot(snapshot);
    }

    private static bool GetInsidePlayerBaseState(GameObject gameObject, LocalRuntimeState localRuntimeState)
    {
        float now = Time.realtimeSinceStartup;
        if (now - localRuntimeState.LastInsidePlayerBaseSampleTime >= 0.5f)
        {
            localRuntimeState.IsInsidePlayerBase =
                EffectArea.IsPointInsideArea(gameObject.transform.position, EffectArea.Type.PlayerBase) != null;
            localRuntimeState.LastInsidePlayerBaseSampleTime = now;
        }

        return localRuntimeState.IsInsidePlayerBase;
    }

    private static bool GetGlobalKeyState(RuntimeContextSnapshot runtimeContext, string key)
    {
        if (runtimeContext.GlobalKeyStates.TryGetValue(key, out bool value))
        {
            return value;
        }

        value = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(key);
        runtimeContext.GlobalKeyStates[key] = value;
        return value;
    }

    private static bool ShouldEvaluateRuntimeState(
        MatchingEntryCache entryCache,
        LocalRuntimeState localRuntimeState,
        RuntimeContextSnapshot runtimeContext)
    {
        if (localRuntimeState.NextRuntimeEvaluationTime == float.NegativeInfinity)
        {
            return true;
        }

        if (entryCache.UsesTimeOfDay &&
            localRuntimeState.LastObservedTimeOfDayPhaseMarker != runtimeContext.TimeOfDayPhaseMarker)
        {
            return true;
        }

        if (entryCache.UsesRequiredEnvironments &&
            !string.Equals(localRuntimeState.LastObservedEnvironmentName, runtimeContext.EnvironmentName, StringComparison.Ordinal))
        {
            return true;
        }

        return Time.realtimeSinceStartup >= localRuntimeState.NextRuntimeEvaluationTime;
    }

    private static void ArmNextRuntimeEvaluationWindow(
        MatchingEntryCache entryCache,
        LocalRuntimeState localRuntimeState,
        RuntimeContextSnapshot runtimeContext)
    {
        localRuntimeState.LastObservedTimeOfDayPhaseMarker = runtimeContext.TimeOfDayPhaseMarker;
        localRuntimeState.LastObservedEnvironmentName = runtimeContext.EnvironmentName;

        bool usesOnlyInsidePlayerBase =
            entryCache.UsesInsidePlayerBase &&
            !entryCache.UsesTimeOfDay &&
            !entryCache.UsesRequiredEnvironments &&
            entryCache.RuntimeRequiredGlobalKeys.Count == 0 &&
            entryCache.RuntimeForbiddenGlobalKeys.Count == 0;

        float interval = usesOnlyInsidePlayerBase
            ? RuntimeEvaluationIntervalInsidePlayerBaseOnlySeconds
            : RuntimeEvaluationIntervalSeconds;
        localRuntimeState.NextRuntimeEvaluationTime = Time.realtimeSinceStartup + interval;
    }

    private static bool AreRuntimeSpawnerConditionsSatisfied(
        GameObject gameObject,
        ConditionsDefinition? conditions,
        RuntimeContextSnapshot runtimeContext,
        LocalRuntimeState localRuntimeState)
    {
        return DropConditionEvaluator.AreDynamicConditionsSatisfied(
            conditions,
            runtimeContext.TimeOfDayPhaseMarker,
            runtimeContext.EnvironmentName,
            GetInsidePlayerBaseState(gameObject, localRuntimeState),
            key => GetGlobalKeyState(runtimeContext, key));
    }
}
