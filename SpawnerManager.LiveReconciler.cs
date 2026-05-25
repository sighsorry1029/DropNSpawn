using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    internal static void ReconcileSpawnAreaInstance(SpawnArea spawnArea)
    {
        lock (Sync)
        {
            ReconcileSpawnAreaInstanceCore(spawnArea);
        }
    }

    internal static void ReconcileCreatureSpawnerInstance(CreatureSpawner creatureSpawner)
    {
        lock (Sync)
        {
            ReconcileCreatureSpawnerInstanceCore(creatureSpawner);
        }
    }

    internal static void BeginSpawnAreaSpawnAttempt(SpawnArea spawnArea)
    {
        lock (Sync)
        {
            if (spawnArea == null)
            {
                return;
            }

            if (!HasAppliedSpawnAreaTrackedCustomizations(spawnArea))
            {
                LiveReconcilerState.ResetPendingSpawnAreaAttempt(spawnArea);
                return;
            }

            LiveReconcilerState.BeginPendingSpawnAreaAttempt(spawnArea);
        }
    }

    internal static void RecordSelectedSpawnAreaPrefab(SpawnArea spawnArea, SpawnArea.SpawnData? spawnData)
    {
        lock (Sync)
        {
            if (spawnArea == null || !LiveReconcilerState.HasPendingSpawnAreaAttempt(spawnArea))
            {
                return;
            }

            LiveReconcilerState.SetPendingSpawnAreaSelection(spawnArea, spawnData);
        }
    }

    internal static void RecordSpawnAreaSpawnPoint(SpawnArea spawnArea, bool succeeded, Vector3 spawnPoint)
    {
        lock (Sync)
        {
            if (spawnArea == null || !LiveReconcilerState.HasPendingSpawnAreaAttempt(spawnArea))
            {
                return;
            }

            if (!succeeded)
            {
                LiveReconcilerState.RemovePendingSpawnAreaSpawnPoint(spawnArea);
                return;
            }

            LiveReconcilerState.SetPendingSpawnAreaSpawnPoint(spawnArea, spawnPoint);
        }
    }

    private static void ReconcileSpawnAreaInstanceCore(SpawnArea? spawnArea)
    {
        TrackSpawnAreaInstanceInternal(spawnArea);

        if (!IsGameDataReady() || spawnArea == null)
        {
            return;
        }

        if (ShouldApplyLocally() &&
            TryGetActiveSpawnAreaEntryCache(spawnArea, out MatchingEntryCache? entryCache, out _))
        {
            ReconcileSpawnAreaInstanceInternal(
                spawnArea,
                entryCache!.Entries,
                entryCache);
            return;
        }

        RestoreSpawnAreaInstance(spawnArea);
    }

    private static void ReconcileCreatureSpawnerInstanceCore(CreatureSpawner? creatureSpawner)
    {
        TrackCreatureSpawnerInstanceInternal(creatureSpawner);

        if (!IsGameDataReady() || creatureSpawner == null)
        {
            return;
        }

        if (ShouldApplyLocally() &&
            TryGetActiveCreatureSpawnerEntryCache(creatureSpawner, out MatchingEntryCache? entryCache, out _))
        {
            ReconcileCreatureSpawnerInstanceInternal(
                creatureSpawner,
                entryCache!.Entries,
                entryCache);
            return;
        }

        RestoreCreatureSpawnerInstance(creatureSpawner, refreshRuntimeState: true);
    }

    private static void ReconcileSpawnAreaInstanceInternal(
        SpawnArea? spawnArea,
        IEnumerable<SpawnerRuntimeEntry>? entries,
        MatchingEntryCache? entryCache = null,
        bool usePreselectedWinner = false,
        SpawnerRuntimeEntry? preselectedWinningEntry = null)
    {
        if (spawnArea == null || spawnArea.gameObject == null)
        {
            return;
        }

        CaptureLiveSpawnAreaSnapshotIfNeeded(spawnArea);
        RestoreSpawnAreaInstance(spawnArea);

        SpawnerRuntimeEntry? winningEntry = preselectedWinningEntry;
        bool hasWinningEntry = usePreselectedWinner
            ? winningEntry != null
            : TrySelectWinningSpawnerEntry(spawnArea.gameObject, entries, forSpawnArea: true, out winningEntry);
        if (hasWinningEntry &&
            winningEntry?.SpawnArea != null)
        {
            ApplySpawnArea(spawnArea, winningEntry.SpawnArea, $"{winningEntry.Prefab}@{DescribeInstance(spawnArea.gameObject)}");
        }

        ApplySpawnAreaTotalSpawnLimit(spawnArea, hasWinningEntry ? winningEntry?.SpawnArea?.MaxTotalSpawns : null);
        entryCache ??= SelectorCacheStore.TryGetSpawnAreaEntryCache(spawnArea, out MatchingEntryCache cachedEntryCache)
            ? cachedEntryCache
            : null;
        UpdateSpawnAreaRuntimeSignature(spawnArea, entries, entryCache);
    }

    private static void ReconcileCreatureSpawnerInstanceInternal(
        CreatureSpawner? creatureSpawner,
        IEnumerable<SpawnerRuntimeEntry>? entries,
        MatchingEntryCache? entryCache = null,
        bool usePreselectedWinner = false,
        SpawnerRuntimeEntry? preselectedWinningEntry = null)
    {
        if (creatureSpawner == null || creatureSpawner.gameObject == null)
        {
            return;
        }

        CaptureLiveCreatureSpawnerSnapshotIfNeeded(creatureSpawner);
        int previousInterval = Math.Max(1, creatureSpawner.m_spawnInterval);
        int previousGroupId = creatureSpawner.m_spawnGroupID;
        int previousMaxGroupSpawned = creatureSpawner.m_maxGroupSpawned;
        float previousGroupRadius = creatureSpawner.m_spawnGroupRadius;
        float previousSpawnerWeight = creatureSpawner.m_spawnerWeight;

        RestoreCreatureSpawnerInstance(creatureSpawner);
        SpawnerRuntimeEntry? winningEntry = preselectedWinningEntry;
        bool hasWinningEntry = usePreselectedWinner
            ? winningEntry != null
            : TrySelectWinningSpawnerEntry(creatureSpawner.gameObject, entries, forSpawnArea: false, out winningEntry);
        if (hasWinningEntry &&
            winningEntry?.CreatureSpawner != null)
        {
            ApplyCreatureSpawner(creatureSpawner, winningEntry.CreatureSpawner, $"{winningEntry.Prefab}@{DescribeInstance(creatureSpawner.gameObject)}");
        }

        entryCache ??= SelectorCacheStore.TryGetCreatureSpawnerEntryCache(creatureSpawner, out MatchingEntryCache cachedEntryCache)
            ? cachedEntryCache
            : null;
        UpdateCreatureSpawnerRuntimeSignature(creatureSpawner, entries, entryCache);
        MaybeRefreshCreatureSpawnerSchedule(creatureSpawner, previousInterval);
        MaybeResetCreatureSpawnerCaches(creatureSpawner, previousGroupId, previousMaxGroupSpawned, previousGroupRadius, previousSpawnerWeight);
    }

    private static void CaptureLiveSpawnAreaSnapshotIfNeeded(SpawnArea spawnArea)
    {
        if (spawnArea == null || LiveRegistryStore.HasLiveSnapshot(spawnArea))
        {
            return;
        }

        LiveRegistryStore.CaptureLiveSnapshot(
            spawnArea,
            new SpawnAreaLiveSnapshot
            {
                LevelUpChance = spawnArea.m_levelupChance,
                SpawnInterval = spawnArea.m_spawnIntervalSec,
                TriggerDistance = spawnArea.m_triggerDistance,
                SetPatrolSpawnPoint = spawnArea.m_setPatrolSpawnPoint,
                SpawnRadius = spawnArea.m_spawnRadius,
                NearRadius = spawnArea.m_nearRadius,
                FarRadius = spawnArea.m_farRadius,
                MaxNear = spawnArea.m_maxNear,
                MaxTotal = spawnArea.m_maxTotal,
                OnGroundOnly = spawnArea.m_onGroundOnly,
                Prefabs = CloneSpawnAreaSnapshots(spawnArea.m_prefabs)
            });
    }

    private static void CaptureLiveCreatureSpawnerSnapshotIfNeeded(CreatureSpawner creatureSpawner)
    {
        if (creatureSpawner == null || LiveRegistryStore.HasLiveSnapshot(creatureSpawner))
        {
            return;
        }

        LiveRegistryStore.CaptureLiveSnapshot(
            creatureSpawner,
            new CreatureSpawnerLiveSnapshot
            {
                CreaturePrefab = creatureSpawner.m_creaturePrefab,
                MinLevel = creatureSpawner.m_minLevel,
                MaxLevel = creatureSpawner.m_maxLevel,
                LevelUpChance = creatureSpawner.m_levelupChance,
                RespawnTimeMinutes = creatureSpawner.m_respawnTimeMinuts,
                TriggerDistance = creatureSpawner.m_triggerDistance,
                TriggerNoise = creatureSpawner.m_triggerNoise,
                SpawnAtNight = creatureSpawner.m_spawnAtNight,
                SpawnAtDay = creatureSpawner.m_spawnAtDay,
                RequireSpawnArea = creatureSpawner.m_requireSpawnArea,
                SpawnInPlayerBase = creatureSpawner.m_spawnInPlayerBase,
                WakeUpAnimation = creatureSpawner.m_wakeUpAnimation,
                SpawnCheckInterval = creatureSpawner.m_spawnInterval,
                RequiredGlobalKey = creatureSpawner.m_requiredGlobalKey ?? "",
                BlockingGlobalKey = creatureSpawner.m_blockingGlobalKey ?? "",
                SetPatrolSpawnPoint = creatureSpawner.m_setPatrolSpawnPoint,
                SpawnGroupId = creatureSpawner.m_spawnGroupID,
                MaxGroupSpawned = creatureSpawner.m_maxGroupSpawned,
                SpawnGroupRadius = creatureSpawner.m_spawnGroupRadius,
                SpawnerWeight = creatureSpawner.m_spawnerWeight
            });
    }

    private static void RestoreSpawnAreaInstance(SpawnArea spawnArea)
    {
        RuntimeStateStore.RemoveRuntimeSignature(spawnArea);
        ClearAppliedSpawnAreaPostSpawnOverrides(spawnArea);
        ClearAppliedSpawnAreaTotalSpawnLimit(spawnArea);
        if (!LiveRegistryStore.TryGetLiveSnapshot(spawnArea, out SpawnAreaLiveSnapshot snapshot))
        {
            return;
        }

        RestoreSpawnArea(spawnArea, snapshot);
    }

    private static void RestoreCreatureSpawnerInstance(CreatureSpawner creatureSpawner, bool refreshRuntimeState = false)
    {
        int previousInterval = Math.Max(1, creatureSpawner.m_spawnInterval);
        int previousGroupId = creatureSpawner.m_spawnGroupID;
        int previousMaxGroupSpawned = creatureSpawner.m_maxGroupSpawned;
        float previousGroupRadius = creatureSpawner.m_spawnGroupRadius;
        float previousSpawnerWeight = creatureSpawner.m_spawnerWeight;
        LiveReconcilerState.ClearAppliedCreatureSpawnerOverrides(creatureSpawner);
        if (!LiveRegistryStore.TryGetLiveSnapshot(creatureSpawner, out CreatureSpawnerLiveSnapshot snapshot))
        {
            return;
        }

        RestoreCreatureSpawner(creatureSpawner, snapshot);
        if (!refreshRuntimeState)
        {
            return;
        }

        RuntimeStateStore.RemoveRuntimeSignature(creatureSpawner);
        MaybeRefreshCreatureSpawnerSchedule(creatureSpawner, previousInterval);
        MaybeResetCreatureSpawnerCaches(creatureSpawner, previousGroupId, previousMaxGroupSpawned, previousGroupRadius, previousSpawnerWeight);
    }
}
