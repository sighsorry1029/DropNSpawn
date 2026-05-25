using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static readonly SpawnerLiveRegistryStore LiveRegistryStore = new();

    private readonly struct TrackedSpawnerPrefabState
    {
        public TrackedSpawnerPrefabState(string prefabName)
        {
            PrefabName = prefabName;
            ConfiguredEligible = false;
            RuntimeEligible = false;
            EligibilityEpoch = int.MinValue;
        }

        private TrackedSpawnerPrefabState(string prefabName, bool configuredEligible, bool runtimeEligible, int eligibilityEpoch)
        {
            PrefabName = prefabName;
            ConfiguredEligible = configuredEligible;
            RuntimeEligible = runtimeEligible;
            EligibilityEpoch = eligibilityEpoch;
        }

        public string PrefabName { get; }
        public bool ConfiguredEligible { get; }
        public bool RuntimeEligible { get; }
        public int EligibilityEpoch { get; }

        public TrackedSpawnerPrefabState WithEligibility(bool configuredEligible, bool runtimeEligible, int eligibilityEpoch)
        {
            return new TrackedSpawnerPrefabState(PrefabName, configuredEligible, runtimeEligible, eligibilityEpoch);
        }
    }

    private sealed class SpawnerLiveRegistryStore
    {
        private readonly Dictionary<SpawnArea, SpawnAreaLiveSnapshot> _liveSpawnAreaSnapshots = new();
        private readonly Dictionary<CreatureSpawner, CreatureSpawnerLiveSnapshot> _liveCreatureSpawnerSnapshots = new();

        private readonly Dictionary<string, HashSet<SpawnArea>> _liveSpawnAreasByPrefab = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<SpawnArea>> _liveSpawnAreasByPrefabAndLocation = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<SpawnArea, TrackedSpawnerPrefabState> _liveSpawnAreaPrefabStates = new();
        private readonly Dictionary<SpawnArea, string> _spawnAreaLocationBucketByInstance = new();
        private readonly Dictionary<string, HashSet<CreatureSpawner>> _liveCreatureSpawnersByPrefab = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<CreatureSpawner>> _liveCreatureSpawnersByPrefabAndLocation = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<CreatureSpawner, TrackedSpawnerPrefabState> _liveCreatureSpawnerPrefabStates = new();
        private readonly Dictionary<CreatureSpawner, string> _creatureSpawnerLocationBucketByInstance = new();

        public bool TryGetTrackedPrefabName(SpawnArea? spawnArea, out string prefabName)
        {
            if (spawnArea != null && _liveSpawnAreaPrefabStates.TryGetValue(spawnArea, out TrackedSpawnerPrefabState candidate))
            {
                prefabName = candidate.PrefabName;
                return true;
            }

            prefabName = "";
            return false;
        }

        public bool TryGetTrackedPrefabName(CreatureSpawner? creatureSpawner, out string prefabName)
        {
            if (creatureSpawner != null && _liveCreatureSpawnerPrefabStates.TryGetValue(creatureSpawner, out TrackedSpawnerPrefabState candidate))
            {
                prefabName = candidate.PrefabName;
                return true;
            }

            prefabName = "";
            return false;
        }

        public bool TryGetTrackedState(SpawnArea? spawnArea, out TrackedSpawnerPrefabState trackedState)
        {
            if (spawnArea != null && _liveSpawnAreaPrefabStates.TryGetValue(spawnArea, out trackedState))
            {
                return true;
            }

            trackedState = default;
            return false;
        }

        public bool TryGetTrackedState(CreatureSpawner? creatureSpawner, out TrackedSpawnerPrefabState trackedState)
        {
            if (creatureSpawner != null && _liveCreatureSpawnerPrefabStates.TryGetValue(creatureSpawner, out trackedState))
            {
                return true;
            }

            trackedState = default;
            return false;
        }

        public bool HasLiveSnapshot(SpawnArea? spawnArea)
        {
            return spawnArea != null && _liveSpawnAreaSnapshots.ContainsKey(spawnArea);
        }

        public bool HasLiveSnapshot(CreatureSpawner? creatureSpawner)
        {
            return creatureSpawner != null && _liveCreatureSpawnerSnapshots.ContainsKey(creatureSpawner);
        }

        public void CaptureLiveSnapshot(SpawnArea spawnArea, SpawnAreaLiveSnapshot snapshot)
        {
            if (spawnArea == null)
            {
                return;
            }

            _liveSpawnAreaSnapshots[spawnArea] = snapshot;
        }

        public void CaptureLiveSnapshot(CreatureSpawner creatureSpawner, CreatureSpawnerLiveSnapshot snapshot)
        {
            if (creatureSpawner == null)
            {
                return;
            }

            _liveCreatureSpawnerSnapshots[creatureSpawner] = snapshot;
        }

        public bool TryGetLiveSnapshot(SpawnArea spawnArea, out SpawnAreaLiveSnapshot snapshot)
        {
            return _liveSpawnAreaSnapshots.TryGetValue(spawnArea, out snapshot);
        }

        public bool TryGetLiveSnapshot(CreatureSpawner creatureSpawner, out CreatureSpawnerLiveSnapshot snapshot)
        {
            return _liveCreatureSpawnerSnapshots.TryGetValue(creatureSpawner, out snapshot);
        }

        public void RemoveLiveSnapshot(SpawnArea? spawnArea)
        {
            if (spawnArea != null)
            {
                _liveSpawnAreaSnapshots.Remove(spawnArea);
            }
        }

        public void RemoveLiveSnapshot(CreatureSpawner? creatureSpawner)
        {
            if (creatureSpawner != null)
            {
                _liveCreatureSpawnerSnapshots.Remove(creatureSpawner);
            }
        }

        public void TrackSpawnAreaPrefab(SpawnArea spawnArea, string prefabName)
        {
            if (spawnArea == null || string.IsNullOrWhiteSpace(prefabName))
            {
                return;
            }

            _liveSpawnAreaPrefabStates[spawnArea] = new TrackedSpawnerPrefabState(prefabName);
            if (!_liveSpawnAreasByPrefab.TryGetValue(prefabName, out HashSet<SpawnArea>? prefabs))
            {
                prefabs = new HashSet<SpawnArea>();
                _liveSpawnAreasByPrefab[prefabName] = prefabs;
            }

            prefabs.Add(spawnArea);
        }

        public void TrackCreatureSpawnerPrefab(CreatureSpawner creatureSpawner, string prefabName)
        {
            if (creatureSpawner == null || string.IsNullOrWhiteSpace(prefabName))
            {
                return;
            }

            _liveCreatureSpawnerPrefabStates[creatureSpawner] = new TrackedSpawnerPrefabState(prefabName);
            if (!_liveCreatureSpawnersByPrefab.TryGetValue(prefabName, out HashSet<CreatureSpawner>? prefabs))
            {
                prefabs = new HashSet<CreatureSpawner>();
                _liveCreatureSpawnersByPrefab[prefabName] = prefabs;
            }

            prefabs.Add(creatureSpawner);
        }

        public bool UntrackSpawnAreaPrefab(SpawnArea? spawnArea, out string prefabName)
        {
            prefabName = "";
            if (spawnArea == null || !_liveSpawnAreaPrefabStates.Remove(spawnArea, out TrackedSpawnerPrefabState trackedState))
            {
                return false;
            }

            prefabName = trackedState.PrefabName;
            if (_liveSpawnAreasByPrefab.TryGetValue(prefabName, out HashSet<SpawnArea>? spawnAreas))
            {
                spawnAreas.Remove(spawnArea);
                if (spawnAreas.Count == 0)
                {
                    _liveSpawnAreasByPrefab.Remove(prefabName);
                }
            }

            return true;
        }

        public bool UntrackCreatureSpawnerPrefab(CreatureSpawner? creatureSpawner, out string prefabName)
        {
            prefabName = "";
            if (creatureSpawner == null || !_liveCreatureSpawnerPrefabStates.Remove(creatureSpawner, out TrackedSpawnerPrefabState trackedState))
            {
                return false;
            }

            prefabName = trackedState.PrefabName;
            if (_liveCreatureSpawnersByPrefab.TryGetValue(prefabName, out HashSet<CreatureSpawner>? creatureSpawners))
            {
                creatureSpawners.Remove(creatureSpawner);
                if (creatureSpawners.Count == 0)
                {
                    _liveCreatureSpawnersByPrefab.Remove(prefabName);
                }
            }

            return true;
        }

        public void CollectDeadSpawnAreas(List<SpawnArea> target)
        {
            target.Clear();
            foreach (KeyValuePair<SpawnArea, TrackedSpawnerPrefabState> pair in _liveSpawnAreaPrefabStates)
            {
                if (pair.Key == null || pair.Key.gameObject == null)
                {
                    target.Add(pair.Key!);
                }
            }
        }

        public void CollectDeadCreatureSpawners(List<CreatureSpawner> target)
        {
            target.Clear();
            foreach (KeyValuePair<CreatureSpawner, TrackedSpawnerPrefabState> pair in _liveCreatureSpawnerPrefabStates)
            {
                if (pair.Key == null || pair.Key.gameObject == null)
                {
                    target.Add(pair.Key!);
                }
            }
        }

        public void ForEachTrackedSpawnArea(Action<SpawnArea, string> visitor)
        {
            foreach (KeyValuePair<SpawnArea, TrackedSpawnerPrefabState> pair in _liveSpawnAreaPrefabStates)
            {
                if (pair.Key != null)
                {
                    visitor(pair.Key, pair.Value.PrefabName);
                }
            }
        }

        public void ForEachTrackedCreatureSpawner(Action<CreatureSpawner, string> visitor)
        {
            foreach (KeyValuePair<CreatureSpawner, TrackedSpawnerPrefabState> pair in _liveCreatureSpawnerPrefabStates)
            {
                if (pair.Key != null)
                {
                    visitor(pair.Key, pair.Value.PrefabName);
                }
            }
        }

        public void UpdateTrackedEligibility(SpawnArea spawnArea, bool configuredEligible, bool runtimeEligible, int eligibilityEpoch)
        {
            if (spawnArea == null || !_liveSpawnAreaPrefabStates.TryGetValue(spawnArea, out TrackedSpawnerPrefabState trackedState))
            {
                return;
            }

            _liveSpawnAreaPrefabStates[spawnArea] = trackedState.WithEligibility(
                configuredEligible,
                runtimeEligible,
                eligibilityEpoch);
        }

        public void UpdateTrackedEligibility(CreatureSpawner creatureSpawner, bool configuredEligible, bool runtimeEligible, int eligibilityEpoch)
        {
            if (creatureSpawner == null || !_liveCreatureSpawnerPrefabStates.TryGetValue(creatureSpawner, out TrackedSpawnerPrefabState trackedState))
            {
                return;
            }

            _liveCreatureSpawnerPrefabStates[creatureSpawner] = trackedState.WithEligibility(
                configuredEligible,
                runtimeEligible,
                eligibilityEpoch);
        }

        public void AppendTrackedSpawnAreasForPrefab(string prefabName, List<SpawnArea> target)
        {
            if (!_liveSpawnAreasByPrefab.TryGetValue(prefabName, out HashSet<SpawnArea>? spawnAreas))
            {
                return;
            }

            foreach (SpawnArea spawnArea in spawnAreas)
            {
                if (spawnArea != null && spawnArea.gameObject != null)
                {
                    target.Add(spawnArea);
                }
            }
        }

        public void AppendTrackedCreatureSpawnersForPrefab(string prefabName, List<CreatureSpawner> target)
        {
            if (!_liveCreatureSpawnersByPrefab.TryGetValue(prefabName, out HashSet<CreatureSpawner>? creatureSpawners))
            {
                return;
            }

            foreach (CreatureSpawner creatureSpawner in creatureSpawners)
            {
                if (creatureSpawner != null && creatureSpawner.gameObject != null)
                {
                    target.Add(creatureSpawner);
                }
            }
        }

        public bool AppendTrackedSpawnAreasForBucket(string bucketKey, List<SpawnArea> target)
        {
            if (!_liveSpawnAreasByPrefabAndLocation.TryGetValue(bucketKey, out HashSet<SpawnArea>? bucket))
            {
                return false;
            }

            foreach (SpawnArea spawnArea in bucket)
            {
                if (spawnArea != null && spawnArea.gameObject != null)
                {
                    target.Add(spawnArea);
                }
            }

            return true;
        }

        public bool AppendTrackedCreatureSpawnersForBucket(string bucketKey, List<CreatureSpawner> target)
        {
            if (!_liveCreatureSpawnersByPrefabAndLocation.TryGetValue(bucketKey, out HashSet<CreatureSpawner>? bucket))
            {
                return false;
            }

            foreach (CreatureSpawner creatureSpawner in bucket)
            {
                if (creatureSpawner != null && creatureSpawner.gameObject != null)
                {
                    target.Add(creatureSpawner);
                }
            }

            return true;
        }

        public void UpdateSpawnAreaLocationBucket(SpawnArea spawnArea, string nextBucketKey)
        {
            if (spawnArea == null || string.IsNullOrWhiteSpace(nextBucketKey))
            {
                return;
            }

            if (_spawnAreaLocationBucketByInstance.TryGetValue(spawnArea, out string? previousBucketKey) &&
                string.Equals(previousBucketKey, nextBucketKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RemoveSpawnAreaLocationBucket(spawnArea);
            if (!_liveSpawnAreasByPrefabAndLocation.TryGetValue(nextBucketKey, out HashSet<SpawnArea>? bucket))
            {
                bucket = new HashSet<SpawnArea>();
                _liveSpawnAreasByPrefabAndLocation[nextBucketKey] = bucket;
            }

            bucket.Add(spawnArea);
            _spawnAreaLocationBucketByInstance[spawnArea] = nextBucketKey;
        }

        public void UpdateCreatureSpawnerLocationBucket(CreatureSpawner creatureSpawner, string nextBucketKey)
        {
            if (creatureSpawner == null || string.IsNullOrWhiteSpace(nextBucketKey))
            {
                return;
            }

            if (_creatureSpawnerLocationBucketByInstance.TryGetValue(creatureSpawner, out string? previousBucketKey) &&
                string.Equals(previousBucketKey, nextBucketKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RemoveCreatureSpawnerLocationBucket(creatureSpawner);
            if (!_liveCreatureSpawnersByPrefabAndLocation.TryGetValue(nextBucketKey, out HashSet<CreatureSpawner>? bucket))
            {
                bucket = new HashSet<CreatureSpawner>();
                _liveCreatureSpawnersByPrefabAndLocation[nextBucketKey] = bucket;
            }

            bucket.Add(creatureSpawner);
            _creatureSpawnerLocationBucketByInstance[creatureSpawner] = nextBucketKey;
        }

        public void RemoveSpawnAreaLocationBucket(SpawnArea? spawnArea)
        {
            if (spawnArea == null || !_spawnAreaLocationBucketByInstance.Remove(spawnArea, out string? previousBucketKey))
            {
                return;
            }

            if (!_liveSpawnAreasByPrefabAndLocation.TryGetValue(previousBucketKey, out HashSet<SpawnArea>? bucket))
            {
                return;
            }

            bucket.Remove(spawnArea);
            if (bucket.Count == 0)
            {
                _liveSpawnAreasByPrefabAndLocation.Remove(previousBucketKey);
            }
        }

        public void RemoveCreatureSpawnerLocationBucket(CreatureSpawner? creatureSpawner)
        {
            if (creatureSpawner == null || !_creatureSpawnerLocationBucketByInstance.Remove(creatureSpawner, out string? previousBucketKey))
            {
                return;
            }

            if (!_liveCreatureSpawnersByPrefabAndLocation.TryGetValue(previousBucketKey, out HashSet<CreatureSpawner>? bucket))
            {
                return;
            }

            bucket.Remove(creatureSpawner);
            if (bucket.Count == 0)
            {
                _liveCreatureSpawnersByPrefabAndLocation.Remove(previousBucketKey);
            }
        }

        public void ClearRuntimeView()
        {
            _liveSpawnAreaSnapshots.Clear();
            _liveCreatureSpawnerSnapshots.Clear();
            _liveSpawnAreasByPrefabAndLocation.Clear();
            _spawnAreaLocationBucketByInstance.Clear();
            _liveCreatureSpawnersByPrefabAndLocation.Clear();
            _creatureSpawnerLocationBucketByInstance.Clear();
        }

        public void ClearLiveRegistries()
        {
            _liveSpawnAreasByPrefab.Clear();
            _liveSpawnAreaPrefabStates.Clear();
            _liveCreatureSpawnersByPrefab.Clear();
            _liveCreatureSpawnerPrefabStates.Clear();
        }
    }
}
