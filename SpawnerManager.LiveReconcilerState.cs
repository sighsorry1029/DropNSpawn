using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static readonly SpawnerLiveReconcilerState LiveReconcilerState = new();

    private sealed class SpawnerLiveReconcilerState
    {
        private readonly HashSet<string> _missingComponentWarnings = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> _appliedCreatureSpawnerCheckIntervals = new();
        private readonly Dictionary<CreatureSpawner, ExpandWorldSpawnDataPayload> _appliedCreatureSpawnerData = new();
        private readonly Dictionary<CreatureSpawner, string> _appliedCreatureSpawnerFaction = new();
        private readonly Dictionary<CreatureSpawner, TimeOfDayDefinition> _appliedCreatureSpawnerTimeOfDay = new();
        private readonly Dictionary<SpawnArea, List<SpawnArea.SpawnData>> _appliedSpawnAreaPrefabsByInstance = new();
        private readonly Dictionary<SpawnArea.SpawnData, ExpandWorldSpawnDataPayload> _appliedSpawnAreaDataBySpawnData = new();
        private readonly Dictionary<SpawnArea.SpawnData, string> _appliedSpawnAreaFactionBySpawnData = new();
        private readonly Dictionary<SpawnArea, SpawnAreaTotalSpawnLimitState> _appliedSpawnAreaTotalSpawnLimits = new();
        private readonly HashSet<SpawnArea> _pendingSpawnAreaTrackedAttempts = new();
        private readonly Dictionary<SpawnArea, SpawnArea.SpawnData?> _pendingSpawnAreaSelections = new();
        private readonly Dictionary<SpawnArea, Vector3> _pendingSpawnAreaSpawnPoints = new();
        private readonly Dictionary<SpawnArea, GameObject> _pendingSpawnAreaSpawnedObjects = new();

        public bool TryAddMissingComponentWarning(string key)
        {
            return _missingComponentWarnings.Add(key);
        }

        public void ClearMissingComponentWarnings()
        {
            _missingComponentWarnings.Clear();
        }

        public void ResetPendingSpawnAreaAttempt(SpawnArea? spawnArea)
        {
            if (spawnArea == null)
            {
                return;
            }

            _pendingSpawnAreaTrackedAttempts.Remove(spawnArea);
            _pendingSpawnAreaSelections.Remove(spawnArea);
            _pendingSpawnAreaSpawnPoints.Remove(spawnArea);
            _pendingSpawnAreaSpawnedObjects.Remove(spawnArea);
        }

        public void BeginPendingSpawnAreaAttempt(SpawnArea spawnArea)
        {
            if (spawnArea == null)
            {
                return;
            }

            ResetPendingSpawnAreaAttempt(spawnArea);
            _pendingSpawnAreaTrackedAttempts.Add(spawnArea);
        }

        public void RemovePendingSpawnAreaAttemptMarker(SpawnArea? spawnArea)
        {
            if (spawnArea == null)
            {
                return;
            }

            _pendingSpawnAreaTrackedAttempts.Remove(spawnArea);
        }

        public bool HasPendingSpawnAreaAttempt(SpawnArea? spawnArea)
        {
            return spawnArea != null && _pendingSpawnAreaTrackedAttempts.Contains(spawnArea);
        }

        public void SetPendingSpawnAreaSelection(SpawnArea spawnArea, SpawnArea.SpawnData? spawnData)
        {
            if (spawnArea == null)
            {
                return;
            }

            _pendingSpawnAreaSelections[spawnArea] = spawnData;
        }

        public bool TryGetPendingSpawnAreaSelection(SpawnArea? spawnArea, out SpawnArea.SpawnData? spawnData)
        {
            spawnData = null;
            return spawnArea != null && _pendingSpawnAreaSelections.TryGetValue(spawnArea, out spawnData);
        }

        public bool TryTakePendingSpawnAreaSelection(SpawnArea? spawnArea, out SpawnArea.SpawnData? spawnData)
        {
            spawnData = null;
            if (spawnArea == null || !_pendingSpawnAreaSelections.TryGetValue(spawnArea, out spawnData))
            {
                return false;
            }

            _pendingSpawnAreaSelections.Remove(spawnArea);
            return true;
        }

        public void SetPendingSpawnAreaSpawnPoint(SpawnArea spawnArea, Vector3 spawnPoint)
        {
            if (spawnArea == null)
            {
                return;
            }

            _pendingSpawnAreaSpawnPoints[spawnArea] = spawnPoint;
        }

        public void RemovePendingSpawnAreaSpawnPoint(SpawnArea? spawnArea)
        {
            if (spawnArea == null)
            {
                return;
            }

            _pendingSpawnAreaSpawnPoints.Remove(spawnArea);
        }

        public bool TryTakePendingSpawnAreaSpawnPoint(SpawnArea? spawnArea, out Vector3 spawnPoint)
        {
            spawnPoint = default;
            if (spawnArea == null || !_pendingSpawnAreaSpawnPoints.TryGetValue(spawnArea, out spawnPoint))
            {
                return false;
            }

            _pendingSpawnAreaSpawnPoints.Remove(spawnArea);
            return true;
        }

        public void SetPendingSpawnAreaSpawnedObject(SpawnArea spawnArea, GameObject spawnedObject)
        {
            if (spawnArea == null || spawnedObject == null)
            {
                return;
            }

            _pendingSpawnAreaSpawnedObjects[spawnArea] = spawnedObject;
        }

        public bool TryTakePendingSpawnAreaSpawnedObject(SpawnArea? spawnArea, out GameObject? spawnedObject)
        {
            spawnedObject = null;
            if (spawnArea == null || !_pendingSpawnAreaSpawnedObjects.TryGetValue(spawnArea, out spawnedObject))
            {
                return false;
            }

            _pendingSpawnAreaSpawnedObjects.Remove(spawnArea);
            return true;
        }

        public bool TryGetAppliedCreatureSpawnerData(CreatureSpawner? creatureSpawner, out ExpandWorldSpawnDataPayload payload)
        {
            if (creatureSpawner != null && _appliedCreatureSpawnerData.TryGetValue(creatureSpawner, out ExpandWorldSpawnDataPayload? candidate))
            {
                payload = candidate;
                return true;
            }

            payload = null!;
            return false;
        }

        public void SetAppliedCreatureSpawnerData(CreatureSpawner creatureSpawner, ExpandWorldSpawnDataPayload payload)
        {
            if (creatureSpawner == null || payload == null)
            {
                return;
            }

            _appliedCreatureSpawnerData[creatureSpawner] = payload;
        }

        public void RemoveAppliedCreatureSpawnerData(CreatureSpawner? creatureSpawner)
        {
            if (creatureSpawner == null)
            {
                return;
            }

            _appliedCreatureSpawnerData.Remove(creatureSpawner);
        }

        public bool TryGetAppliedCreatureSpawnerFaction(CreatureSpawner? creatureSpawner, out string faction)
        {
            if (creatureSpawner != null && _appliedCreatureSpawnerFaction.TryGetValue(creatureSpawner, out string? candidate))
            {
                faction = candidate;
                return true;
            }

            faction = "";
            return false;
        }

        public void SetAppliedCreatureSpawnerFaction(CreatureSpawner creatureSpawner, string faction)
        {
            if (creatureSpawner == null || string.IsNullOrWhiteSpace(faction))
            {
                return;
            }

            _appliedCreatureSpawnerFaction[creatureSpawner] = faction;
        }

        public void RemoveAppliedCreatureSpawnerFaction(CreatureSpawner? creatureSpawner)
        {
            if (creatureSpawner == null)
            {
                return;
            }

            _appliedCreatureSpawnerFaction.Remove(creatureSpawner);
        }

        public bool TryGetAppliedCreatureSpawnerTimeOfDay(CreatureSpawner? creatureSpawner, out TimeOfDayDefinition timeOfDay)
        {
            if (creatureSpawner != null && _appliedCreatureSpawnerTimeOfDay.TryGetValue(creatureSpawner, out TimeOfDayDefinition? candidate))
            {
                timeOfDay = candidate;
                return true;
            }

            timeOfDay = null!;
            return false;
        }

        public void SetAppliedCreatureSpawnerTimeOfDay(CreatureSpawner creatureSpawner, TimeOfDayDefinition timeOfDay)
        {
            if (creatureSpawner == null || timeOfDay == null)
            {
                return;
            }

            _appliedCreatureSpawnerTimeOfDay[creatureSpawner] = timeOfDay;
        }

        public void RemoveAppliedCreatureSpawnerTimeOfDay(CreatureSpawner? creatureSpawner)
        {
            if (creatureSpawner == null)
            {
                return;
            }

            _appliedCreatureSpawnerTimeOfDay.Remove(creatureSpawner);
        }

        public void ClearAppliedCreatureSpawnerOverrides(CreatureSpawner? creatureSpawner)
        {
            RemoveAppliedCreatureSpawnerData(creatureSpawner);
            RemoveAppliedCreatureSpawnerFaction(creatureSpawner);
            RemoveAppliedCreatureSpawnerTimeOfDay(creatureSpawner);
        }

        public bool TryGetAppliedCreatureSpawnerCheckInterval(int instanceId, out int interval)
        {
            return _appliedCreatureSpawnerCheckIntervals.TryGetValue(instanceId, out interval);
        }

        public void SetAppliedCreatureSpawnerCheckInterval(int instanceId, int interval)
        {
            _appliedCreatureSpawnerCheckIntervals[instanceId] = interval;
        }

        public void SetAppliedSpawnAreaPrefabs(SpawnArea spawnArea, List<SpawnArea.SpawnData> prefabs)
        {
            if (spawnArea == null || prefabs == null)
            {
                return;
            }

            _appliedSpawnAreaPrefabsByInstance[spawnArea] = prefabs;
        }

        public bool TryGetAppliedSpawnAreaPrefabs(SpawnArea? spawnArea, out List<SpawnArea.SpawnData> prefabs)
        {
            if (spawnArea != null && _appliedSpawnAreaPrefabsByInstance.TryGetValue(spawnArea, out List<SpawnArea.SpawnData>? candidate))
            {
                prefabs = candidate;
                return true;
            }

            prefabs = null!;
            return false;
        }

        public bool TryTakeAppliedSpawnAreaPrefabs(SpawnArea? spawnArea, out List<SpawnArea.SpawnData> prefabs)
        {
            if (spawnArea != null && _appliedSpawnAreaPrefabsByInstance.TryGetValue(spawnArea, out List<SpawnArea.SpawnData>? candidate))
            {
                prefabs = candidate;
                _appliedSpawnAreaPrefabsByInstance.Remove(spawnArea);
                return true;
            }

            prefabs = null!;
            return false;
        }

        public bool TryGetAppliedSpawnAreaData(SpawnArea.SpawnData? spawnData, out ExpandWorldSpawnDataPayload payload)
        {
            if (spawnData != null && _appliedSpawnAreaDataBySpawnData.TryGetValue(spawnData, out ExpandWorldSpawnDataPayload? candidate))
            {
                payload = candidate;
                return true;
            }

            payload = null!;
            return false;
        }

        public void SetAppliedSpawnAreaData(SpawnArea.SpawnData spawnData, ExpandWorldSpawnDataPayload payload)
        {
            if (spawnData == null || payload == null)
            {
                return;
            }

            _appliedSpawnAreaDataBySpawnData[spawnData] = payload;
        }

        public void RemoveAppliedSpawnAreaData(SpawnArea.SpawnData? spawnData)
        {
            if (spawnData == null)
            {
                return;
            }

            _appliedSpawnAreaDataBySpawnData.Remove(spawnData);
        }

        public bool HasAppliedSpawnAreaData(SpawnArea.SpawnData? spawnData)
        {
            return spawnData != null && _appliedSpawnAreaDataBySpawnData.ContainsKey(spawnData);
        }

        public bool TryGetAppliedSpawnAreaFaction(SpawnArea.SpawnData? spawnData, out string faction)
        {
            if (spawnData != null && _appliedSpawnAreaFactionBySpawnData.TryGetValue(spawnData, out string? candidate))
            {
                faction = candidate;
                return true;
            }

            faction = "";
            return false;
        }

        public void SetAppliedSpawnAreaFaction(SpawnArea.SpawnData spawnData, string faction)
        {
            if (spawnData == null || string.IsNullOrWhiteSpace(faction))
            {
                return;
            }

            _appliedSpawnAreaFactionBySpawnData[spawnData] = faction;
        }

        public void RemoveAppliedSpawnAreaFaction(SpawnArea.SpawnData? spawnData)
        {
            if (spawnData == null)
            {
                return;
            }

            _appliedSpawnAreaFactionBySpawnData.Remove(spawnData);
        }

        public bool HasAppliedSpawnAreaFaction(SpawnArea.SpawnData? spawnData)
        {
            return spawnData != null && _appliedSpawnAreaFactionBySpawnData.ContainsKey(spawnData);
        }

        public bool TryGetAppliedSpawnAreaTotalSpawnLimit(SpawnArea? spawnArea, out SpawnAreaTotalSpawnLimitState state)
        {
            if (spawnArea != null && _appliedSpawnAreaTotalSpawnLimits.TryGetValue(spawnArea, out state))
            {
                return true;
            }

            state = default;
            return false;
        }

        public void SetAppliedSpawnAreaTotalSpawnLimit(SpawnArea spawnArea, SpawnAreaTotalSpawnLimitState state)
        {
            if (spawnArea == null)
            {
                return;
            }

            _appliedSpawnAreaTotalSpawnLimits[spawnArea] = state;
        }

        public void RemoveAppliedSpawnAreaTotalSpawnLimit(SpawnArea? spawnArea)
        {
            if (spawnArea == null)
            {
                return;
            }

            _appliedSpawnAreaTotalSpawnLimits.Remove(spawnArea);
        }

        public void Clear()
        {
            _missingComponentWarnings.Clear();
            _appliedCreatureSpawnerCheckIntervals.Clear();
            _appliedCreatureSpawnerData.Clear();
            _appliedCreatureSpawnerFaction.Clear();
            _appliedCreatureSpawnerTimeOfDay.Clear();
            _appliedSpawnAreaPrefabsByInstance.Clear();
            _appliedSpawnAreaDataBySpawnData.Clear();
            _appliedSpawnAreaFactionBySpawnData.Clear();
            _appliedSpawnAreaTotalSpawnLimits.Clear();
            _pendingSpawnAreaTrackedAttempts.Clear();
            _pendingSpawnAreaSelections.Clear();
            _pendingSpawnAreaSpawnPoints.Clear();
            _pendingSpawnAreaSpawnedObjects.Clear();
        }
    }
}
