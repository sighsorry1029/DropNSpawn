using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    internal static void TrackSpawnAreaInstance(SpawnArea? spawnArea)
    {
        lock (Sync)
        {
            TrackSpawnAreaInstanceInternal(spawnArea);
        }
    }

    internal static void TrackCreatureSpawnerInstance(CreatureSpawner? creatureSpawner)
    {
        lock (Sync)
        {
            TrackCreatureSpawnerInstanceInternal(creatureSpawner);
        }
    }

    internal static void HandleSpawnAreaInstanceAwake(SpawnArea? spawnArea)
    {
        lock (Sync)
        {
            TrackSpawnAreaInstanceInternal(spawnArea);

            if (spawnArea == null ||
                spawnArea.gameObject == null ||
                !ShouldApplyLocally() ||
                ShouldBlockClientSpawnerUpdate() ||
                DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Spawner) ||
                !IsGameDataReady())
            {
                return;
            }

            SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot = GetRuntimeConfigurationSnapshot();
            if (!TryGetTrackedOrCurrentSpawnAreaEligibility(
                    spawnArea,
                    runtimeConfigurationSnapshot,
                    out _,
                    out bool configuredEligible,
                    out _) ||
                !configuredEligible)
            {
                return;
            }

            if (!TryGetActiveSpawnAreaEntryCache(
                    spawnArea,
                    runtimeConfigurationSnapshot,
                    out MatchingEntryCache? entryCache,
                    out _))
            {
                return;
            }

            ReconcileSpawnAreaInstanceInternal(
                spawnArea,
                entryCache!.Entries,
                entryCache);
        }
    }

    internal static void HandleCreatureSpawnerInstanceAwake(CreatureSpawner? creatureSpawner)
    {
        lock (Sync)
        {
            TrackCreatureSpawnerInstanceInternal(creatureSpawner);

            if (creatureSpawner == null ||
                creatureSpawner.gameObject == null ||
                !ShouldApplyLocally() ||
                ShouldBlockClientSpawnerUpdate() ||
                DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Spawner) ||
                !IsGameDataReady())
            {
                return;
            }

            SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot = GetRuntimeConfigurationSnapshot();
            if (!TryGetTrackedOrCurrentCreatureSpawnerEligibility(
                    creatureSpawner,
                    runtimeConfigurationSnapshot,
                    out _,
                    out bool configuredEligible,
                    out _) ||
                !configuredEligible)
            {
                return;
            }

            if (!TryGetActiveCreatureSpawnerEntryCache(
                    creatureSpawner,
                    runtimeConfigurationSnapshot,
                    out MatchingEntryCache? entryCache,
                    out _))
            {
                return;
            }

            ReconcileCreatureSpawnerInstanceInternal(
                creatureSpawner,
                entryCache!.Entries,
                entryCache);
        }
    }

    internal static void UntrackSpawnAreaInstance(SpawnArea? spawnArea)
    {
        lock (Sync)
        {
            ClearAppliedSpawnAreaPostSpawnOverrides(spawnArea);
            ClearAppliedSpawnAreaTotalSpawnLimit(spawnArea);
            LiveReconcilerState.ResetPendingSpawnAreaAttempt(spawnArea);

            if (spawnArea != null &&
                LiveRegistryStore.TryGetTrackedPrefabName(spawnArea, out string prefabName))
            {
                UnregisterLiveSpawnArea(spawnArea, prefabName);
            }
        }
    }

    internal static void UntrackCreatureSpawnerInstance(CreatureSpawner? creatureSpawner)
    {
        lock (Sync)
        {
            if (creatureSpawner != null &&
                LiveRegistryStore.TryGetTrackedPrefabName(creatureSpawner, out string prefabName))
            {
                UnregisterLiveCreatureSpawner(creatureSpawner, prefabName);
            }
        }
    }

    private static void RegisterLiveSpawnArea(SpawnArea? spawnArea)
    {
        if (spawnArea == null || spawnArea.gameObject == null)
        {
            return;
        }

        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot = GetRuntimeConfigurationSnapshot();
        if (LiveRegistryStore.TryGetTrackedPrefabName(spawnArea, out string trackedPrefabName))
        {
            CaptureSpawnAreaProvenanceIfAvailable(spawnArea);
            if (!string.IsNullOrWhiteSpace(trackedPrefabName))
            {
                RefreshSpawnAreaLocationBucketMembership(
                    spawnArea,
                    runtimeConfigurationSnapshot);
            }

            return;
        }

        CaptureSpawnAreaProvenanceIfAvailable(spawnArea);

        string prefabName = GetConfigPrefabName(spawnArea.gameObject, nameof(SpawnArea));
        if (prefabName.Length == 0)
        {
            return;
        }

        if (LiveRegistryStore.TryGetTrackedPrefabName(spawnArea, out string previousPrefabName))
        {
            if (string.Equals(previousPrefabName, prefabName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UnregisterLiveSpawnArea(spawnArea, previousPrefabName);
        }

        LiveRegistryStore.TrackSpawnAreaPrefab(spawnArea, prefabName);
        RefreshSpawnAreaLocationBucketMembership(
            spawnArea,
            runtimeConfigurationSnapshot);
    }

    private static void TrackSpawnAreaInstanceInternal(SpawnArea? spawnArea)
    {
        RegisterLiveSpawnArea(spawnArea);
    }

    private static void RegisterLiveCreatureSpawner(CreatureSpawner? creatureSpawner)
    {
        if (creatureSpawner == null || creatureSpawner.gameObject == null)
        {
            return;
        }

        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot = GetRuntimeConfigurationSnapshot();
        if (LiveRegistryStore.TryGetTrackedPrefabName(creatureSpawner, out string trackedPrefabName))
        {
            CaptureCreatureSpawnerProvenanceIfAvailable(creatureSpawner);
            if (!string.IsNullOrWhiteSpace(trackedPrefabName))
            {
                RefreshCreatureSpawnerLocationBucketMembership(
                    creatureSpawner,
                    runtimeConfigurationSnapshot);
            }

            return;
        }

        CaptureCreatureSpawnerProvenanceIfAvailable(creatureSpawner);

        string prefabName = GetConfigPrefabName(creatureSpawner.gameObject, nameof(CreatureSpawner));
        if (prefabName.Length == 0)
        {
            return;
        }

        if (LiveRegistryStore.TryGetTrackedPrefabName(creatureSpawner, out string previousPrefabName))
        {
            if (string.Equals(previousPrefabName, prefabName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UnregisterLiveCreatureSpawner(creatureSpawner, previousPrefabName);
        }

        LiveRegistryStore.TrackCreatureSpawnerPrefab(creatureSpawner, prefabName);
        RefreshCreatureSpawnerLocationBucketMembership(
            creatureSpawner,
            runtimeConfigurationSnapshot);
    }

    private static void TrackCreatureSpawnerInstanceInternal(CreatureSpawner? creatureSpawner)
    {
        RegisterLiveCreatureSpawner(creatureSpawner);
    }

    private static IEnumerable<SpawnArea> GetRegisteredSpawnAreas(HashSet<string> dirtyPrefabs)
    {
        return GetRegisteredSpawnAreas(dirtyPrefabs, runtimeConfigurationSnapshot: null);
    }

    private static IEnumerable<SpawnArea> GetRegisteredSpawnAreas(
        HashSet<string> dirtyPrefabs,
        SpawnerRuntimeConfigurationSnapshot? runtimeConfigurationSnapshot)
    {
        CleanupRegisteredSpawnAreas();
        List<SpawnArea> spawnAreaBuffer = new();
        foreach (string prefabName in dirtyPrefabs)
        {
            spawnAreaBuffer.Clear();
            if (TryGetTargetedSelectorLocationKeys(
                    runtimeConfigurationSnapshot,
                    prefabName,
                    forSpawnArea: true,
                    out HashSet<string>? selectorLocationKeys) &&
                TryGetRegisteredSpawnAreasFromLocationBuckets(prefabName, selectorLocationKeys!, spawnAreaBuffer))
            {
                foreach (SpawnArea spawnArea in spawnAreaBuffer)
                {
                    yield return spawnArea;
                }

                continue;
            }

            LiveRegistryStore.AppendTrackedSpawnAreasForPrefab(prefabName, spawnAreaBuffer);
            foreach (SpawnArea spawnArea in spawnAreaBuffer)
            {
                yield return spawnArea;
            }
        }
    }

    private static IEnumerable<CreatureSpawner> GetRegisteredCreatureSpawners(HashSet<string> dirtyPrefabs)
    {
        return GetRegisteredCreatureSpawners(dirtyPrefabs, runtimeConfigurationSnapshot: null);
    }

    private static IEnumerable<CreatureSpawner> GetRegisteredCreatureSpawners(
        HashSet<string> dirtyPrefabs,
        SpawnerRuntimeConfigurationSnapshot? runtimeConfigurationSnapshot)
    {
        CleanupRegisteredCreatureSpawners();
        List<CreatureSpawner> creatureSpawnerBuffer = new();
        foreach (string prefabName in dirtyPrefabs)
        {
            creatureSpawnerBuffer.Clear();
            if (TryGetTargetedSelectorLocationKeys(
                    runtimeConfigurationSnapshot,
                    prefabName,
                    forSpawnArea: false,
                    out HashSet<string>? selectorLocationKeys) &&
                TryGetRegisteredCreatureSpawnersFromLocationBuckets(prefabName, selectorLocationKeys!, creatureSpawnerBuffer))
            {
                foreach (CreatureSpawner creatureSpawner in creatureSpawnerBuffer)
                {
                    yield return creatureSpawner;
                }

                continue;
            }

            LiveRegistryStore.AppendTrackedCreatureSpawnersForPrefab(prefabName, creatureSpawnerBuffer);
            foreach (CreatureSpawner creatureSpawner in creatureSpawnerBuffer)
            {
                yield return creatureSpawner;
            }
        }
    }

    private static bool TryGetTrackedOrCurrentSpawnAreaPrefabName(SpawnArea? spawnArea, out string configPrefabName)
    {
        configPrefabName = "";
        if (spawnArea == null || spawnArea.gameObject == null)
        {
            return false;
        }

        configPrefabName = LiveRegistryStore.TryGetTrackedPrefabName(spawnArea, out string trackedPrefabName)
            ? trackedPrefabName
            : GetConfigPrefabName(spawnArea.gameObject, nameof(SpawnArea));
        return configPrefabName.Length > 0;
    }

    private static bool TryGetTrackedOrCurrentCreatureSpawnerPrefabName(CreatureSpawner? creatureSpawner, out string configPrefabName)
    {
        configPrefabName = "";
        if (creatureSpawner == null || creatureSpawner.gameObject == null)
        {
            return false;
        }

        configPrefabName = LiveRegistryStore.TryGetTrackedPrefabName(creatureSpawner, out string trackedPrefabName)
            ? trackedPrefabName
            : GetConfigPrefabName(creatureSpawner.gameObject, nameof(CreatureSpawner));
        return configPrefabName.Length > 0;
    }

    private static bool TryGetTrackedOrCurrentSpawnAreaEligibility(
        SpawnArea? spawnArea,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot,
        out string configPrefabName,
        out bool configuredEligible,
        out bool runtimeEligible)
    {
        configPrefabName = "";
        configuredEligible = false;
        runtimeEligible = false;
        if (spawnArea == null || spawnArea.gameObject == null)
        {
            return false;
        }

        if (LiveRegistryStore.TryGetTrackedState(spawnArea, out TrackedSpawnerPrefabState trackedState))
        {
            configPrefabName = trackedState.PrefabName;
            if (configPrefabName.Length == 0)
            {
                return false;
            }

            if (trackedState.EligibilityEpoch == _trackedSpawnerEligibilityEpoch)
            {
                configuredEligible = trackedState.ConfiguredEligible;
                runtimeEligible = trackedState.RuntimeEligible;
                return true;
            }

            configuredEligible = runtimeConfigurationSnapshot.ConfiguredSpawnAreaPrefabs.Contains(configPrefabName);
            runtimeEligible = runtimeConfigurationSnapshot.RuntimeConfiguredSpawnAreaPrefabs.Contains(configPrefabName);
            LiveRegistryStore.UpdateTrackedEligibility(
                spawnArea,
                configuredEligible,
                runtimeEligible,
                _trackedSpawnerEligibilityEpoch);
            return true;
        }

        configPrefabName = GetConfigPrefabName(spawnArea.gameObject, nameof(SpawnArea));
        if (configPrefabName.Length == 0)
        {
            return false;
        }

        configuredEligible = runtimeConfigurationSnapshot.ConfiguredSpawnAreaPrefabs.Contains(configPrefabName);
        runtimeEligible = runtimeConfigurationSnapshot.RuntimeConfiguredSpawnAreaPrefabs.Contains(configPrefabName);
        return true;
    }

    private static bool TryGetTrackedOrCurrentCreatureSpawnerEligibility(
        CreatureSpawner? creatureSpawner,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot,
        out string configPrefabName,
        out bool configuredEligible,
        out bool runtimeEligible)
    {
        configPrefabName = "";
        configuredEligible = false;
        runtimeEligible = false;
        if (creatureSpawner == null || creatureSpawner.gameObject == null)
        {
            return false;
        }

        if (LiveRegistryStore.TryGetTrackedState(creatureSpawner, out TrackedSpawnerPrefabState trackedState))
        {
            configPrefabName = trackedState.PrefabName;
            if (configPrefabName.Length == 0)
            {
                return false;
            }

            if (trackedState.EligibilityEpoch == _trackedSpawnerEligibilityEpoch)
            {
                configuredEligible = trackedState.ConfiguredEligible;
                runtimeEligible = trackedState.RuntimeEligible;
                return true;
            }

            configuredEligible = runtimeConfigurationSnapshot.ConfiguredCreatureSpawnerPrefabs.Contains(configPrefabName);
            runtimeEligible = runtimeConfigurationSnapshot.RuntimeConfiguredCreatureSpawnerPrefabs.Contains(configPrefabName);
            LiveRegistryStore.UpdateTrackedEligibility(
                creatureSpawner,
                configuredEligible,
                runtimeEligible,
                _trackedSpawnerEligibilityEpoch);
            return true;
        }

        configPrefabName = GetConfigPrefabName(creatureSpawner.gameObject, nameof(CreatureSpawner));
        if (configPrefabName.Length == 0)
        {
            return false;
        }

        configuredEligible = runtimeConfigurationSnapshot.ConfiguredCreatureSpawnerPrefabs.Contains(configPrefabName);
        runtimeEligible = runtimeConfigurationSnapshot.RuntimeConfiguredCreatureSpawnerPrefabs.Contains(configPrefabName);
        return true;
    }

    private static void RefreshSpawnAreaLocationBucketMembership(
        SpawnArea? spawnArea,
        SpawnerRuntimeConfigurationSnapshot? runtimeConfigurationSnapshot = null)
    {
        if (spawnArea == null || spawnArea.gameObject == null)
        {
            return;
        }

        SpawnerRuntimeConfigurationSnapshot snapshot = runtimeConfigurationSnapshot ?? GetRuntimeConfigurationSnapshot();
        if (!TryGetTrackedOrCurrentSpawnAreaEligibility(
                spawnArea,
                snapshot,
                out string configPrefabName,
                out bool configuredEligible,
                out _))
        {
            RemoveSpawnAreaLocationBucket(spawnArea);
            return;
        }

        if (!configuredEligible)
        {
            RemoveSpawnAreaLocationBucket(spawnArea);
            return;
        }

        UpdateSpawnAreaLocationBucket(spawnArea, configPrefabName);
    }

    private static void RefreshCreatureSpawnerLocationBucketMembership(
        CreatureSpawner? creatureSpawner,
        SpawnerRuntimeConfigurationSnapshot? runtimeConfigurationSnapshot = null)
    {
        if (creatureSpawner == null || creatureSpawner.gameObject == null)
        {
            return;
        }

        SpawnerRuntimeConfigurationSnapshot snapshot = runtimeConfigurationSnapshot ?? GetRuntimeConfigurationSnapshot();
        if (!TryGetTrackedOrCurrentCreatureSpawnerEligibility(
                creatureSpawner,
                snapshot,
                out string configPrefabName,
                out bool configuredEligible,
                out _))
        {
            RemoveCreatureSpawnerLocationBucket(creatureSpawner);
            return;
        }

        if (!configuredEligible)
        {
            RemoveCreatureSpawnerLocationBucket(creatureSpawner);
            return;
        }

        UpdateCreatureSpawnerLocationBucket(creatureSpawner, configPrefabName);
    }

    private static bool TryGetRegisteredSpawnAreasFromLocationBuckets(
        string prefabName,
        HashSet<string> selectorLocationKeys,
        List<SpawnArea> targetedSpawnAreas)
    {
        bool foundBucket = false;
        foreach (string locationKey in EnumerateTargetedLocationBucketKeys(selectorLocationKeys))
        {
            string bucketKey = BuildPrefabLocationBucketKey(prefabName, locationKey);
            foundBucket |= LiveRegistryStore.AppendTrackedSpawnAreasForBucket(bucketKey, targetedSpawnAreas);
        }

        return foundBucket;
    }

    private static bool TryGetRegisteredCreatureSpawnersFromLocationBuckets(
        string prefabName,
        HashSet<string> selectorLocationKeys,
        List<CreatureSpawner> targetedCreatureSpawners)
    {
        bool foundBucket = false;
        foreach (string locationKey in EnumerateTargetedLocationBucketKeys(selectorLocationKeys))
        {
            string bucketKey = BuildPrefabLocationBucketKey(prefabName, locationKey);
            foundBucket |= LiveRegistryStore.AppendTrackedCreatureSpawnersForBucket(bucketKey, targetedCreatureSpawners);
        }

        return foundBucket;
    }

    private static IEnumerable<string> EnumerateTargetedLocationBucketKeys(HashSet<string> selectorLocationKeys)
    {
        if (selectorLocationKeys != null)
        {
            foreach (string selectorLocationKey in selectorLocationKeys)
            {
                if (!string.IsNullOrWhiteSpace(selectorLocationKey))
                {
                    yield return selectorLocationKey;
                }
            }
        }

        yield return UnresolvedSelectorLocationCacheKey;
    }

    private static void CleanupRegisteredSpawnAreas()
    {
        List<SpawnArea> deadInstances = new();
        LiveRegistryStore.CollectDeadSpawnAreas(deadInstances);
        if (deadInstances.Count == 0)
        {
            return;
        }

        foreach (SpawnArea deadInstance in deadInstances)
        {
            if (LiveRegistryStore.TryGetTrackedPrefabName(deadInstance, out string prefabName))
            {
                UnregisterLiveSpawnArea(deadInstance, prefabName);
            }
        }
    }

    private static void CleanupRegisteredCreatureSpawners()
    {
        List<CreatureSpawner> deadInstances = new();
        LiveRegistryStore.CollectDeadCreatureSpawners(deadInstances);
        if (deadInstances.Count == 0)
        {
            return;
        }

        foreach (CreatureSpawner deadInstance in deadInstances)
        {
            if (LiveRegistryStore.TryGetTrackedPrefabName(deadInstance, out string prefabName))
            {
                UnregisterLiveCreatureSpawner(deadInstance, prefabName);
            }
        }
    }

    private static void UnregisterLiveSpawnArea(SpawnArea spawnArea, string prefabName)
    {
        ClearAppliedSpawnAreaPostSpawnOverrides(spawnArea);
        ClearAppliedSpawnAreaTotalSpawnLimit(spawnArea);
        LiveReconcilerState.ResetPendingSpawnAreaAttempt(spawnArea);
        LiveRegistryStore.UntrackSpawnAreaPrefab(spawnArea, out _);
        RemoveSpawnAreaLocationBucket(spawnArea);
        LiveRegistryStore.RemoveLiveSnapshot(spawnArea);
        SelectorCacheStore.RemoveSpawnAreaEntryCache(spawnArea);
        SelectorCacheStore.RemoveStaticSelectorContext(spawnArea.gameObject);
        RuntimeStateStore.RemoveLocalRuntimeState(spawnArea);
        ProvenanceRegistry.RemoveSpawnAreaProvenance(spawnArea);
    }

    private static void UnregisterLiveCreatureSpawner(CreatureSpawner creatureSpawner, string prefabName)
    {
        LiveRegistryStore.UntrackCreatureSpawnerPrefab(creatureSpawner, out _);
        RemoveCreatureSpawnerLocationBucket(creatureSpawner);
        LiveRegistryStore.RemoveLiveSnapshot(creatureSpawner);
        SelectorCacheStore.RemoveCreatureSpawnerEntryCache(creatureSpawner);
        SelectorCacheStore.RemoveStaticSelectorContext(creatureSpawner.gameObject);
        RuntimeStateStore.RemoveLocalRuntimeState(creatureSpawner);
        ProvenanceRegistry.RemoveCreatureSpawnerProvenance(creatureSpawner);
    }

    private static string BuildPrefabLocationBucketKey(string prefabName, string locationKey)
    {
        return $"{prefabName}|{NormalizeSelectorLocationCacheKey(locationKey)}";
    }

    private static void UpdateSpawnAreaLocationBucket(SpawnArea spawnArea, string prefabName)
    {
        if (spawnArea == null || spawnArea.gameObject == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return;
        }

        string nextBucketKey = BuildPrefabLocationBucketKey(prefabName, BuildSelectorLocationCacheKey(spawnArea.gameObject));
        LiveRegistryStore.UpdateSpawnAreaLocationBucket(spawnArea, nextBucketKey);
    }

    private static void UpdateCreatureSpawnerLocationBucket(CreatureSpawner creatureSpawner, string prefabName)
    {
        if (creatureSpawner == null || creatureSpawner.gameObject == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return;
        }

        string nextBucketKey = BuildPrefabLocationBucketKey(prefabName, BuildSelectorLocationCacheKey(creatureSpawner.gameObject));
        LiveRegistryStore.UpdateCreatureSpawnerLocationBucket(creatureSpawner, nextBucketKey);
    }

    private static void RemoveSpawnAreaLocationBucket(SpawnArea spawnArea)
    {
        LiveRegistryStore.RemoveSpawnAreaLocationBucket(spawnArea);
    }

    private static void RemoveCreatureSpawnerLocationBucket(CreatureSpawner creatureSpawner)
    {
        LiveRegistryStore.RemoveCreatureSpawnerLocationBucket(creatureSpawner);
    }
}
