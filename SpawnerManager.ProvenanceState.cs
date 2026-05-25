using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static readonly SpawnerProvenanceRegistry ProvenanceRegistry = new();

    private sealed class SpawnerProvenanceRegistry
    {
        private readonly Dictionary<SpawnArea, SpawnerLocationProvenance> _spawnAreaLocationProvenance = new();
        private readonly Dictionary<CreatureSpawner, SpawnerLocationProvenance> _creatureSpawnerLocationProvenance = new();
        private readonly Dictionary<Transform, string> _spawnedLocationRootPrefabsByTransform = new();
        private readonly List<CurrentLocationSpawnContext> _currentLocationSpawnContexts = new();
        private readonly RingBufferQueue<PendingLocationRootProvenanceScan> _pendingLocationRootProvenanceScans = new();
        private readonly HashSet<int> _pendingLocationRootProvenanceScanIds = new();

        private int _nextLocationProvenanceEpoch = 1;

        public void PushLocationSpawnContext(string? locationPrefab)
        {
            string normalizedLocationPrefab = (locationPrefab ?? "").Trim();
            _currentLocationSpawnContexts.Add(new CurrentLocationSpawnContext
            {
                LocationPrefab = normalizedLocationPrefab
            });
        }

        public void PopLocationSpawnContext()
        {
            if (_currentLocationSpawnContexts.Count == 0)
            {
                return;
            }

            _currentLocationSpawnContexts.RemoveAt(_currentLocationSpawnContexts.Count - 1);
        }

        public bool TryGetActiveLocationSpawnContextPrefab(out string locationPrefab)
        {
            locationPrefab = "";
            for (int index = _currentLocationSpawnContexts.Count - 1; index >= 0; index--)
            {
                string candidate = (_currentLocationSpawnContexts[index].LocationPrefab ?? "").Trim();
                if (candidate.Length == 0)
                {
                    continue;
                }

                locationPrefab = candidate;
                return true;
            }

            return false;
        }

        public bool TryQueueRootProvenanceScan(Transform? rootTransform, string? locationPrefab, int epoch)
        {
            string normalizedLocationPrefab = (locationPrefab ?? "").Trim();
            if (rootTransform == null || normalizedLocationPrefab.Length == 0)
            {
                return false;
            }

            int rootInstanceId = rootTransform.GetInstanceID();
            if (!_pendingLocationRootProvenanceScanIds.Add(rootInstanceId))
            {
                return false;
            }

            _pendingLocationRootProvenanceScans.Enqueue(new PendingLocationRootProvenanceScan
            {
                RootInstanceId = rootInstanceId,
                RootTransform = rootTransform,
                LocationPrefab = normalizedLocationPrefab,
                Epoch = epoch
            });
            return true;
        }

        public bool HasPendingRootScans()
        {
            return _pendingLocationRootProvenanceScans.Count > 0;
        }

        public bool TryPeekPendingRootScan(out PendingLocationRootProvenanceScan pendingScan)
        {
            if (_pendingLocationRootProvenanceScans.TryPeek(out PendingLocationRootProvenanceScan? candidate))
            {
                pendingScan = candidate;
                return true;
            }

            pendingScan = null!;
            return false;
        }

        public void DiscardPendingRootScan(PendingLocationRootProvenanceScan pendingScan)
        {
            if (pendingScan == null)
            {
                return;
            }

            _pendingLocationRootProvenanceScans.TryDequeue(out _);
            _pendingLocationRootProvenanceScanIds.Remove(pendingScan.RootInstanceId);
        }

        public void ClearPendingRootScans()
        {
            _pendingLocationRootProvenanceScans.Clear();
            _pendingLocationRootProvenanceScanIds.Clear();
        }

        public int AllocateLocationProvenanceEpoch()
        {
            if (_nextLocationProvenanceEpoch == int.MaxValue)
            {
                _nextLocationProvenanceEpoch = 1;
            }

            return _nextLocationProvenanceEpoch++;
        }

        public void RecordSpawnedLocationRoot(Transform rootTransform, string locationPrefab)
        {
            if (rootTransform == null || string.IsNullOrWhiteSpace(locationPrefab))
            {
                return;
            }

            _spawnedLocationRootPrefabsByTransform[rootTransform] = locationPrefab;
        }

        public void RemoveSpawnedLocationRoot(Transform? rootTransform)
        {
            if (rootTransform == null)
            {
                return;
            }

            _spawnedLocationRootPrefabsByTransform.Remove(rootTransform);
        }

        public bool TryGetRecordedRootLocationPrefab(Transform? rootTransform, out string locationPrefab)
        {
            locationPrefab = "";
            return rootTransform != null &&
                   _spawnedLocationRootPrefabsByTransform.TryGetValue(rootTransform, out locationPrefab) &&
                   !string.IsNullOrWhiteSpace(locationPrefab);
        }

        public bool TryFindRecordedLocationRoot(Transform? transform, out Transform? rootTransform, out string locationPrefab)
        {
            rootTransform = null;
            locationPrefab = "";
            Transform? current = transform;
            while (current != null)
            {
                if (_spawnedLocationRootPrefabsByTransform.TryGetValue(current, out locationPrefab))
                {
                    rootTransform = current;
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        public bool HasSpawnAreaProvenance(SpawnArea? spawnArea)
        {
            return spawnArea != null && _spawnAreaLocationProvenance.ContainsKey(spawnArea);
        }

        public bool TryGetSpawnAreaProvenance(SpawnArea? spawnArea, out SpawnerLocationProvenance provenance)
        {
            if (spawnArea != null && _spawnAreaLocationProvenance.TryGetValue(spawnArea, out SpawnerLocationProvenance? candidate))
            {
                provenance = candidate;
                return true;
            }

            provenance = null!;
            return false;
        }

        public void RecordSpawnAreaProvenance(SpawnArea spawnArea, SpawnerLocationProvenance provenance)
        {
            if (spawnArea == null || provenance == null)
            {
                return;
            }

            _spawnAreaLocationProvenance[spawnArea] = provenance;
        }

        public void RemoveSpawnAreaProvenance(SpawnArea? spawnArea)
        {
            if (spawnArea == null)
            {
                return;
            }

            _spawnAreaLocationProvenance.Remove(spawnArea);
        }

        public bool HasCreatureSpawnerProvenance(CreatureSpawner? creatureSpawner)
        {
            return creatureSpawner != null && _creatureSpawnerLocationProvenance.ContainsKey(creatureSpawner);
        }

        public bool TryGetCreatureSpawnerProvenance(CreatureSpawner? creatureSpawner, out SpawnerLocationProvenance provenance)
        {
            if (creatureSpawner != null && _creatureSpawnerLocationProvenance.TryGetValue(creatureSpawner, out SpawnerLocationProvenance? candidate))
            {
                provenance = candidate;
                return true;
            }

            provenance = null!;
            return false;
        }

        public void RecordCreatureSpawnerProvenance(CreatureSpawner creatureSpawner, SpawnerLocationProvenance provenance)
        {
            if (creatureSpawner == null || provenance == null)
            {
                return;
            }

            _creatureSpawnerLocationProvenance[creatureSpawner] = provenance;
        }

        public void RemoveCreatureSpawnerProvenance(CreatureSpawner? creatureSpawner)
        {
            if (creatureSpawner == null)
            {
                return;
            }

            _creatureSpawnerLocationProvenance.Remove(creatureSpawner);
        }

        public void Clear(bool clearCurrentContexts)
        {
            _spawnAreaLocationProvenance.Clear();
            _creatureSpawnerLocationProvenance.Clear();
            _spawnedLocationRootPrefabsByTransform.Clear();
            ClearPendingRootScans();
            if (clearCurrentContexts)
            {
                _currentLocationSpawnContexts.Clear();
            }

            _nextLocationProvenanceEpoch = 1;
        }
    }
}
