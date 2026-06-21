using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    internal static void RecordDirectSpawnAreaSpawnedObject(SpawnArea spawnArea, GameObject? spawnedObject)
    {
        lock (Sync)
        {
            if (spawnArea == null || spawnedObject == null || !LiveReconcilerState.HasPendingSpawnAreaAttempt(spawnArea))
            {
                return;
            }

            LiveReconcilerState.SetPendingSpawnAreaSpawnedObject(spawnArea, spawnedObject);
        }
    }

    internal static void FinalizeSpawnAreaSpawnAttempt(SpawnArea spawnArea, bool succeeded)
    {
        Vector3 objectSpawnPoint = default;
        ExpandWorldSpawnDataPayload? objectsPayload = null;
        Character? spawnedCharacter = null;
        string? factionToApply = null;
        string? factionContext = null;
        lock (Sync)
        {
            LiveReconcilerState.RemovePendingSpawnAreaAttemptMarker(spawnArea);
            LiveReconcilerState.TryTakePendingSpawnAreaSelection(spawnArea, out SpawnArea.SpawnData? selectedSpawnData);
            bool hasRecordedSpawnPoint = LiveReconcilerState.TryTakePendingSpawnAreaSpawnPoint(spawnArea, out Vector3 recordedSpawnPoint);
            string? faction = null;
            ExpandWorldSpawnDataPayload? payload = null;
            bool hasFaction = selectedSpawnData != null && LiveReconcilerState.TryGetAppliedSpawnAreaFaction(selectedSpawnData, out faction);
            bool hasPayload = selectedSpawnData != null && LiveReconcilerState.TryGetAppliedSpawnAreaData(selectedSpawnData, out payload);
            bool hasObjects = hasPayload && payload!.HasObjects;

            if (succeeded &&
                hasRecordedSpawnPoint &&
                selectedSpawnData != null &&
                (hasFaction || hasObjects))
            {
                if (hasObjects)
                {
                    objectSpawnPoint = recordedSpawnPoint;
                    objectsPayload = payload;
                }

                if (hasFaction)
                {
                    spawnedCharacter = LiveReconcilerState.TryTakePendingSpawnAreaSpawnedObject(spawnArea, out GameObject? directSpawnedObject) && directSpawnedObject != null
                        ? directSpawnedObject.GetComponent<Character>()
                        : null;
                    if (spawnedCharacter != null)
                    {
                        factionToApply = faction;
                        factionContext = $"{GetConfigPrefabName(spawnArea.gameObject, nameof(SpawnArea))}@{DescribeInstance(spawnArea.gameObject)}/spawnArea.spawn";
                    }
                }
            }

            if (succeeded)
            {
                RecordSuccessfulSpawnAreaTotalSpawn(spawnArea);
            }
        }

        if (objectsPayload != null)
        {
            ExpandWorldSpawnDataSupport.SpawnObjects(objectSpawnPoint, objectsPayload);
        }

        if (spawnedCharacter != null && factionToApply != null && factionContext != null)
        {
            FactionIntegration.Apply(spawnedCharacter, factionToApply, factionContext);
        }
    }

    internal static void ApplyCreatureSpawnerSpawnOverrides(CreatureSpawner creatureSpawner, ZNetView? spawnedView)
    {
        Vector3 objectSpawnPoint = default;
        ExpandWorldSpawnDataPayload? objectsPayload = null;
        Character? spawnedCharacter = null;
        string? factionToApply = null;
        string? factionContext = null;
        lock (Sync)
        {
            if (creatureSpawner == null ||
                spawnedView == null)
            {
                return;
            }

            string context = $"{GetConfigPrefabName(creatureSpawner.gameObject, nameof(CreatureSpawner))}@{DescribeInstance(creatureSpawner.gameObject)}/creatureSpawner.spawn";
            if (LiveReconcilerState.TryGetAppliedCreatureSpawnerData(creatureSpawner, out ExpandWorldSpawnDataPayload payload) &&
                payload.HasObjects)
            {
                objectSpawnPoint = spawnedView.transform.position;
                objectsPayload = payload;
            }

            Character? character = spawnedView.GetComponent<Character>();
            if (character == null)
            {
                return;
            }

            if (LiveReconcilerState.TryGetAppliedCreatureSpawnerFaction(creatureSpawner, out string faction))
            {
                spawnedCharacter = character;
                factionToApply = faction;
                factionContext = context;
            }
        }

        if (objectsPayload != null)
        {
            ExpandWorldSpawnDataSupport.SpawnObjects(objectSpawnPoint, objectsPayload);
        }

        if (spawnedCharacter != null && factionToApply != null && factionContext != null)
        {
            FactionIntegration.Apply(spawnedCharacter, factionToApply, factionContext);
        }
    }

    internal static void InitializeSpawnAreaSpawnData(SpawnArea spawnArea, GameObject? prefab, Vector3 spawnPoint)
    {
        ExpandWorldSpawnDataPayload? payloadToApply;
        lock (Sync)
        {
            if (spawnArea == null ||
                prefab == null ||
                !LiveReconcilerState.TryGetPendingSpawnAreaSelection(spawnArea, out SpawnArea.SpawnData? selectedSpawnData) ||
                selectedSpawnData == null ||
                !LiveReconcilerState.TryGetAppliedSpawnAreaData(selectedSpawnData, out ExpandWorldSpawnDataPayload payload))
            {
                return;
            }

            payloadToApply = payload;
        }

        ExpandWorldSpawnDataSupport.InitializeSpawn(prefab, spawnPoint, payloadToApply);
    }

    internal static void InitializeCreatureSpawnerSpawnData(CreatureSpawner creatureSpawner, GameObject? prefab, Vector3 spawnPoint)
    {
        ExpandWorldSpawnDataPayload? payloadToApply;
        lock (Sync)
        {
            if (creatureSpawner == null ||
                prefab == null ||
                !LiveReconcilerState.TryGetAppliedCreatureSpawnerData(creatureSpawner, out ExpandWorldSpawnDataPayload payload))
            {
                return;
            }

            payloadToApply = payload;
        }

        ExpandWorldSpawnDataSupport.InitializeSpawn(prefab, spawnPoint, payloadToApply);
    }

    internal static bool IsCreatureSpawnerTimeOfDayAllowed(CreatureSpawner creatureSpawner)
    {
        lock (Sync)
        {
            return creatureSpawner == null ||
                   !LiveReconcilerState.TryGetAppliedCreatureSpawnerTimeOfDay(creatureSpawner, out TimeOfDayDefinition timeOfDay) ||
                   TimeOfDayFormatting.MatchesCurrentTime(timeOfDay);
        }
    }

    private static void ApplyDesiredStateToLiveObjects(SpawnerDesiredState desiredState)
    {
        if (desiredState.ReloadPrefabs.Count == 0)
        {
            return;
        }

        if (desiredState.QueueLiveReconcile)
        {
            ReapplyOrQueueRegisteredLiveObjects(
                desiredState.DomainEnabled,
                desiredState.ReloadPrefabs,
                desiredState.RuntimeConfigurationSnapshot);
        }
        else
        {
            ReapplyRegisteredLiveObjects(
                desiredState.DomainEnabled,
                desiredState.ReloadPrefabs,
                desiredState.RuntimeConfigurationSnapshot);
        }
    }

    private static void ReapplyRegisteredLiveObjects(
        bool domainEnabled,
        HashSet<string> prefabs,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot)
    {
        foreach (SpawnArea spawnArea in GetRegisteredSpawnAreas(prefabs, runtimeConfigurationSnapshot))
        {
            TrackSpawnAreaInstanceInternal(spawnArea);
            if (domainEnabled &&
                TryGetActiveSpawnAreaEntries(
                    spawnArea,
                    runtimeConfigurationSnapshot,
                    out IReadOnlyList<SpawnerRuntimeEntry>? entries,
                    out _))
            {
                ReconcileSpawnAreaInstanceInternal(spawnArea, entries!);
                continue;
            }

            RestoreSpawnAreaInstance(spawnArea);
        }

        foreach (CreatureSpawner creatureSpawner in GetRegisteredCreatureSpawners(prefabs, runtimeConfigurationSnapshot))
        {
            TrackCreatureSpawnerInstanceInternal(creatureSpawner);
            if (domainEnabled &&
                TryGetActiveCreatureSpawnerEntries(
                    creatureSpawner,
                    runtimeConfigurationSnapshot,
                    out IReadOnlyList<SpawnerRuntimeEntry>? entries,
                    out _))
            {
                ReconcileCreatureSpawnerInstanceInternal(creatureSpawner, entries!);
                continue;
            }

            RestoreCreatureSpawnerInstance(creatureSpawner, refreshRuntimeState: true);
        }
    }

    private static void ReapplyOrQueueRegisteredLiveObjects(
        bool domainEnabled,
        HashSet<string> prefabs,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot)
    {
        foreach (SpawnArea spawnArea in GetRegisteredSpawnAreas(prefabs, runtimeConfigurationSnapshot))
        {
            TrackSpawnAreaInstanceInternal(spawnArea);
            if (domainEnabled &&
                TryGetActiveSpawnAreaEntryCache(
                    spawnArea,
                    runtimeConfigurationSnapshot,
                    out MatchingEntryCache? entryCache,
                    out string configPrefabName))
            {
                if (runtimeConfigurationSnapshot.RuntimeConfiguredSpawnAreaPrefabs.Contains(configPrefabName))
                {
                    QueueSpawnAreaReconcile(spawnArea);
                    continue;
                }

                ReconcileSpawnAreaInstanceInternal(
                    spawnArea,
                    entryCache!.Entries,
                    entryCache);
                continue;
            }

            RestoreSpawnAreaInstance(spawnArea);
        }

        foreach (CreatureSpawner creatureSpawner in GetRegisteredCreatureSpawners(prefabs, runtimeConfigurationSnapshot))
        {
            TrackCreatureSpawnerInstanceInternal(creatureSpawner);
            if (domainEnabled &&
                TryGetActiveCreatureSpawnerEntryCache(
                    creatureSpawner,
                    runtimeConfigurationSnapshot,
                    out MatchingEntryCache? entryCache,
                    out string configPrefabName))
            {
                if (runtimeConfigurationSnapshot.RuntimeConfiguredCreatureSpawnerPrefabs.Contains(configPrefabName))
                {
                    QueueCreatureSpawnerReconcile(creatureSpawner);
                    continue;
                }

                ReconcileCreatureSpawnerInstanceInternal(
                    creatureSpawner,
                    entryCache!.Entries,
                    entryCache);
                continue;
            }

            RestoreCreatureSpawnerInstance(creatureSpawner, refreshRuntimeState: true);
        }
    }

    private static bool TryGetActiveSpawnAreaEntries(
        SpawnArea? spawnArea,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot,
        out IReadOnlyList<SpawnerRuntimeEntry>? entries,
        out string configPrefabName)
    {
        entries = null;
        if (!TryGetActiveSpawnAreaEntryCache(spawnArea, runtimeConfigurationSnapshot, out MatchingEntryCache? entryCache, out configPrefabName))
        {
            return false;
        }

        entries = entryCache!.Entries;
        return true;
    }

    private static bool TryGetActiveCreatureSpawnerEntries(
        CreatureSpawner? creatureSpawner,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot,
        out IReadOnlyList<SpawnerRuntimeEntry>? entries,
        out string configPrefabName)
    {
        entries = null;
        if (!TryGetActiveCreatureSpawnerEntryCache(creatureSpawner, runtimeConfigurationSnapshot, out MatchingEntryCache? entryCache, out configPrefabName))
        {
            return false;
        }

        entries = entryCache!.Entries;
        return true;
    }
}
