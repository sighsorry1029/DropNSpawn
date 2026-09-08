using System;
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

    private static void RestoreSpawnArea(SpawnArea target, SpawnAreaLiveSnapshot snapshot)
    {
        ClearAppliedSpawnAreaPostSpawnOverrides(target);
        ClearAppliedSpawnAreaTotalSpawnLimit(target);
        RestoreSpawnAreaValues(
            target,
            snapshot.LevelUpChance,
            snapshot.SpawnInterval,
            snapshot.TriggerDistance,
            snapshot.SetPatrolSpawnPoint,
            snapshot.SpawnRadius,
            snapshot.NearRadius,
            snapshot.FarRadius,
            snapshot.MaxNear,
            snapshot.MaxTotal,
            snapshot.OnGroundOnly,
            snapshot.Prefabs);
    }

    private static void RestoreCreatureSpawner(CreatureSpawner target, CreatureSpawnerLiveSnapshot snapshot)
    {
        LiveReconcilerState.ClearAppliedCreatureSpawnerOverrides(target);
        RestoreCreatureSpawnerValues(
            target,
            snapshot.CreaturePrefab,
            snapshot.MinLevel,
            snapshot.MaxLevel,
            snapshot.LevelUpChance,
            snapshot.RespawnTimeMinutes,
            snapshot.TriggerDistance,
            snapshot.TriggerNoise,
            snapshot.SpawnAtNight,
            snapshot.SpawnAtDay,
            snapshot.RequireSpawnArea,
            snapshot.SpawnInPlayerBase,
            snapshot.WakeUpAnimation,
            snapshot.SpawnCheckInterval,
            snapshot.RequiredGlobalKey,
            snapshot.BlockingGlobalKey,
            snapshot.SetPatrolSpawnPoint,
            snapshot.SpawnGroupId,
            snapshot.MaxGroupSpawned,
            snapshot.SpawnGroupRadius,
            snapshot.SpawnerWeight);
    }

    private static void RestoreSpawnAreaValues(
        SpawnArea target,
        float levelUpChance,
        float spawnInterval,
        float triggerDistance,
        bool setPatrolSpawnPoint,
        float spawnRadius,
        float nearRadius,
        float farRadius,
        int maxNear,
        int maxTotal,
        bool onGroundOnly,
        List<SpawnAreaSpawnSnapshot> prefabs)
    {
        target.m_levelupChance = levelUpChance;
        target.m_spawnIntervalSec = spawnInterval;
        target.m_triggerDistance = triggerDistance;
        target.m_setPatrolSpawnPoint = setPatrolSpawnPoint;
        target.m_spawnRadius = spawnRadius;
        target.m_nearRadius = nearRadius;
        target.m_farRadius = farRadius;
        target.m_maxNear = maxNear;
        target.m_maxTotal = maxTotal;
        target.m_onGroundOnly = onGroundOnly;
        target.m_prefabs = BuildSpawnAreaPrefabs(prefabs);
    }

    private static void RestoreCreatureSpawnerValues(
        CreatureSpawner target,
        GameObject? creaturePrefab,
        int minLevel,
        int maxLevel,
        float levelUpChance,
        float respawnTimeMinutes,
        float triggerDistance,
        float triggerNoise,
        bool spawnAtNight,
        bool spawnAtDay,
        bool requireSpawnArea,
        bool spawnInPlayerBase,
        bool wakeUpAnimation,
        int spawnCheckInterval,
        string requiredGlobalKey,
        string blockingGlobalKey,
        bool setPatrolSpawnPoint,
        int spawnGroupId,
        int maxGroupSpawned,
        float spawnGroupRadius,
        float spawnerWeight)
    {
        target.m_creaturePrefab = creaturePrefab;
        target.m_minLevel = minLevel;
        target.m_maxLevel = maxLevel;
        target.m_levelupChance = levelUpChance;
        target.m_respawnTimeMinuts = respawnTimeMinutes;
        ApplyDefaultZeroCreatureSpawnerRespawnTime(target, yamlRespawnTimeSpecified: false);
        target.m_triggerDistance = triggerDistance;
        target.m_triggerNoise = triggerNoise;
        target.m_spawnAtNight = spawnAtNight;
        target.m_spawnAtDay = spawnAtDay;
        target.m_requireSpawnArea = requireSpawnArea;
        target.m_spawnInPlayerBase = spawnInPlayerBase;
        target.m_wakeUpAnimation = wakeUpAnimation;
        target.m_spawnInterval = Math.Max(1, spawnCheckInterval);
        target.m_requiredGlobalKey = requiredGlobalKey;
        target.m_blockingGlobalKey = blockingGlobalKey;
        target.m_setPatrolSpawnPoint = setPatrolSpawnPoint;
        target.m_spawnGroupID = spawnGroupId;
        target.m_maxGroupSpawned = maxGroupSpawned;
        target.m_spawnGroupRadius = spawnGroupRadius;
        target.m_spawnerWeight = spawnerWeight;
    }

    private static void ApplySpawnArea(SpawnArea target, SpawnAreaDefinition definition, string context)
    {
        if (definition.LevelUpChance.HasValue)
        {
            target.m_levelupChance = Mathf.Max(0f, definition.LevelUpChance.Value);
        }

        if (definition.SpawnInterval.HasValue)
        {
            target.m_spawnIntervalSec = Mathf.Max(0f, definition.SpawnInterval.Value);
        }

        if (definition.TriggerDistance.HasValue)
        {
            target.m_triggerDistance = Mathf.Max(0f, definition.TriggerDistance.Value);
        }

        if (definition.SetPatrolSpawnPoint.HasValue)
        {
            target.m_setPatrolSpawnPoint = definition.SetPatrolSpawnPoint.Value;
        }

        if (definition.SpawnRadius.HasValue)
        {
            target.m_spawnRadius = Mathf.Max(0f, definition.SpawnRadius.Value);
        }

        if (definition.NearRadius.HasValue)
        {
            target.m_nearRadius = Mathf.Max(0f, definition.NearRadius.Value);
        }

        if (definition.FarRadius.HasValue)
        {
            target.m_farRadius = Mathf.Max(0f, definition.FarRadius.Value);
        }

        if (definition.MaxNear.HasValue)
        {
            target.m_maxNear = Math.Max(0, definition.MaxNear.Value);
        }

        if (definition.MaxTotal.HasValue)
        {
            target.m_maxTotal = Math.Max(0, definition.MaxTotal.Value);
        }

        if (definition.OnGroundOnly.HasValue)
        {
            target.m_onGroundOnly = definition.OnGroundOnly.Value;
        }

        List<SpawnAreaResolvedSpawnEntry>? resolvedSpawnEntries = null;
        if (definition.Creatures != null)
        {
            resolvedSpawnEntries = BuildResolvedSpawnAreaPrefabs(definition.Creatures, context);
            target.m_prefabs = resolvedSpawnEntries.Select(entry => entry.SpawnData).ToList();
        }

        UpdateAppliedSpawnAreaPostSpawnOverrides(target, definition, resolvedSpawnEntries);
    }

    private static void ApplyCreatureSpawner(CreatureSpawner target, CreatureSpawnerDefinition definition, string context)
    {
        if (definition.Creature != null)
        {
            string creatureName = definition.Creature.Trim();
            if (creatureName.Length > 0)
            {
                GameObject? creaturePrefab = ResolveCreaturePrefab(creatureName, context);
                if (creaturePrefab != null)
                {
                    target.m_creaturePrefab = creaturePrefab;
                }
            }
            else
            {
                WarnInvalidEntry($"Entry '{context}' set creature to an empty value. Leave the key out to keep the original creature.");
            }
        }

        ExpandWorldSpawnDataPayload? dataPayload = ExpandWorldSpawnDataSupport.BuildPayload(
            target.m_creaturePrefab,
            definition.Data,
            definition.Fields,
            definition.Objects,
            context);
        if (dataPayload != null)
        {
            LiveReconcilerState.SetAppliedCreatureSpawnerData(target, dataPayload);
        }
        else
        {
            LiveReconcilerState.RemoveAppliedCreatureSpawnerData(target);
        }

        if (definition.MinLevel.HasValue)
        {
            target.m_minLevel = Math.Max(1, definition.MinLevel.Value);
        }

        if (definition.MaxLevel.HasValue)
        {
            target.m_maxLevel = Math.Max(target.m_minLevel, Math.Max(1, definition.MaxLevel.Value));
        }

        if (definition.LevelUpChance.HasValue)
        {
            target.m_levelupChance = Mathf.Max(0f, definition.LevelUpChance.Value);
        }

        if (definition.RespawnTimeMinutes.HasValue)
        {
            target.m_respawnTimeMinuts = Mathf.Max(0f, definition.RespawnTimeMinutes.Value);
        }
        ApplyDefaultZeroCreatureSpawnerRespawnTime(target, definition.RespawnTimeMinutes.HasValue);

        if (definition.TriggerDistance.HasValue)
        {
            target.m_triggerDistance = Mathf.Max(0f, definition.TriggerDistance.Value);
        }

        if (definition.TriggerNoise.HasValue)
        {
            target.m_triggerNoise = Mathf.Max(0f, definition.TriggerNoise.Value);
        }

        TimeOfDayDefinition? timeOfDay = definition.TimeOfDay;
        if (timeOfDay != null)
        {
            TimeOfDayFormatting.GetBroadSpawnFlags(timeOfDay, out bool allowDay, out bool allowNight);
            target.m_spawnAtDay = allowDay;
            target.m_spawnAtNight = allowNight;
            if (timeOfDay.HasValues())
            {
                LiveReconcilerState.SetAppliedCreatureSpawnerTimeOfDay(target, timeOfDay);
            }
            else
            {
                LiveReconcilerState.RemoveAppliedCreatureSpawnerTimeOfDay(target);
            }
        }
        else
        {
            LiveReconcilerState.RemoveAppliedCreatureSpawnerTimeOfDay(target);
        }

        if (definition.RequireSpawnArea.HasValue)
        {
            target.m_requireSpawnArea = definition.RequireSpawnArea.Value;
        }

        if (definition.AllowInsidePlayerBase.HasValue)
        {
            target.m_spawnInPlayerBase = definition.AllowInsidePlayerBase.Value;
        }

        if (definition.WakeUpAnimation.HasValue)
        {
            target.m_wakeUpAnimation = definition.WakeUpAnimation.Value;
        }

        if (definition.SpawnCheckInterval.HasValue)
        {
            target.m_spawnInterval = Math.Max(1, definition.SpawnCheckInterval.Value);
        }

        if (definition.RequiredGlobalKey != null)
        {
            target.m_requiredGlobalKey = definition.RequiredGlobalKey;
        }

        if (definition.BlockingGlobalKey != null)
        {
            target.m_blockingGlobalKey = definition.BlockingGlobalKey;
        }

        if (definition.SetPatrolSpawnPoint.HasValue)
        {
            target.m_setPatrolSpawnPoint = definition.SetPatrolSpawnPoint.Value;
        }

        if (definition.SpawnGroupId.HasValue)
        {
            target.m_spawnGroupID = definition.SpawnGroupId.Value;
        }

        if (definition.MaxGroupSpawned.HasValue)
        {
            target.m_maxGroupSpawned = Math.Max(0, definition.MaxGroupSpawned.Value);
        }

        if (definition.SpawnGroupRadius.HasValue)
        {
            target.m_spawnGroupRadius = Mathf.Max(0f, definition.SpawnGroupRadius.Value);
        }

        if (definition.SpawnerWeight.HasValue)
        {
            target.m_spawnerWeight = Mathf.Max(0f, definition.SpawnerWeight.Value);
        }

        if (FactionIntegration.HasFaction(definition.Faction))
        {
            LiveReconcilerState.SetAppliedCreatureSpawnerFaction(target, definition.Faction!);
        }
        else
        {
            LiveReconcilerState.RemoveAppliedCreatureSpawnerFaction(target);
        }

    }


    private static void ApplyDefaultZeroCreatureSpawnerRespawnTime(CreatureSpawner? target, bool yamlRespawnTimeSpecified)
    {
        if (target == null || yamlRespawnTimeSpecified || !ShouldApplyLocally())
        {
            return;
        }

        int defaultRespawnTimeMinutes = PluginSettingsFacade.GetDefaultZeroCreatureSpawnerRespawnTimeMinutes();
        if (defaultRespawnTimeMinutes <= 0 || target.m_respawnTimeMinuts > 0f)
        {
            return;
        }

        target.m_respawnTimeMinuts = defaultRespawnTimeMinutes;
    }

}
