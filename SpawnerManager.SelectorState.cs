using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static readonly SpawnerSelectorCacheStore SelectorCacheStore = new();

    private sealed class SpawnerSelectorCacheStore
    {
        private readonly HashSet<string> _locationSelectorDiagnostics = new(StringComparer.Ordinal);
        private readonly Dictionary<SpawnArea, MatchingEntryCache> _matchingSpawnAreaEntriesByInstance = new();
        private readonly Dictionary<CreatureSpawner, MatchingEntryCache> _matchingCreatureSpawnerEntriesByInstance = new();
        private readonly Dictionary<string, SharedMatchingEntryTemplate> _sharedMatchingEntryTemplates = new(StringComparer.Ordinal);
        private readonly Dictionary<int, StaticSelectorContextSnapshot> _staticSelectorContextsByInstance = new();

        public bool TryAddLocationSelectorDiagnostic(string key)
        {
            return _locationSelectorDiagnostics.Add(key);
        }

        public bool TryGetSpawnAreaEntryCache(SpawnArea? spawnArea, out MatchingEntryCache entryCache)
        {
            if (spawnArea != null && _matchingSpawnAreaEntriesByInstance.TryGetValue(spawnArea, out MatchingEntryCache? candidate))
            {
                entryCache = candidate;
                return true;
            }

            entryCache = null!;
            return false;
        }

        public void SetSpawnAreaEntryCache(SpawnArea spawnArea, MatchingEntryCache entryCache)
        {
            if (spawnArea == null || entryCache == null)
            {
                return;
            }

            _matchingSpawnAreaEntriesByInstance[spawnArea] = entryCache;
        }

        public void RemoveSpawnAreaEntryCache(SpawnArea? spawnArea)
        {
            if (spawnArea == null)
            {
                return;
            }

            _matchingSpawnAreaEntriesByInstance.Remove(spawnArea);
        }

        public bool TryGetCreatureSpawnerEntryCache(CreatureSpawner? creatureSpawner, out MatchingEntryCache entryCache)
        {
            if (creatureSpawner != null && _matchingCreatureSpawnerEntriesByInstance.TryGetValue(creatureSpawner, out MatchingEntryCache? candidate))
            {
                entryCache = candidate;
                return true;
            }

            entryCache = null!;
            return false;
        }

        public void SetCreatureSpawnerEntryCache(CreatureSpawner creatureSpawner, MatchingEntryCache entryCache)
        {
            if (creatureSpawner == null || entryCache == null)
            {
                return;
            }

            _matchingCreatureSpawnerEntriesByInstance[creatureSpawner] = entryCache;
        }

        public void RemoveCreatureSpawnerEntryCache(CreatureSpawner? creatureSpawner)
        {
            if (creatureSpawner == null)
            {
                return;
            }

            _matchingCreatureSpawnerEntriesByInstance.Remove(creatureSpawner);
        }

        public bool TryGetSharedMatchingEntryTemplate(string cacheKey, out SharedMatchingEntryTemplate sharedTemplate)
        {
            if (_sharedMatchingEntryTemplates.TryGetValue(cacheKey, out SharedMatchingEntryTemplate? candidate))
            {
                sharedTemplate = candidate;
                return true;
            }

            sharedTemplate = null!;
            return false;
        }

        public void SetSharedMatchingEntryTemplate(string cacheKey, SharedMatchingEntryTemplate sharedTemplate)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || sharedTemplate == null)
            {
                return;
            }

            _sharedMatchingEntryTemplates[cacheKey] = sharedTemplate;
        }

        public void ClearSharedMatchingEntryTemplates()
        {
            _sharedMatchingEntryTemplates.Clear();
        }

        public bool TryGetReusableStaticSelectorContext(
            GameObject gameObject,
            bool usesLocationFields,
            out StaticSelectorContextSnapshot snapshot)
        {
            snapshot = null!;
            if (gameObject == null ||
                !_staticSelectorContextsByInstance.TryGetValue(gameObject.GetInstanceID(), out StaticSelectorContextSnapshot? candidate))
            {
                return false;
            }

            if (candidate.Position != gameObject.transform.position)
            {
                return false;
            }

            if (!usesLocationFields)
            {
                snapshot = candidate;
                return true;
            }

            if (!candidate.HasRecordedLocationProvenanceEpoch ||
                !TryGetRecordedLocationProvenanceEpoch(gameObject, out int currentProvenanceEpoch) ||
                candidate.RecordedLocationProvenanceEpoch != currentProvenanceEpoch)
            {
                return false;
            }

            snapshot = candidate;
            return true;
        }

        public void StoreReusableStaticSelectorContext(GameObject gameObject, StaticSelectorContextSnapshot snapshot, bool storeForLocationFields)
        {
            if (gameObject == null || snapshot == null)
            {
                return;
            }

            if (!storeForLocationFields || snapshot.HasRecordedLocationProvenanceEpoch)
            {
                _staticSelectorContextsByInstance[gameObject.GetInstanceID()] = snapshot;
            }
        }

        public void RemoveStaticSelectorContext(GameObject? gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            _staticSelectorContextsByInstance.Remove(gameObject.GetInstanceID());
        }

        public void Clear()
        {
            _locationSelectorDiagnostics.Clear();
            _matchingSpawnAreaEntriesByInstance.Clear();
            _matchingCreatureSpawnerEntriesByInstance.Clear();
            _sharedMatchingEntryTemplates.Clear();
            _staticSelectorContextsByInstance.Clear();
        }
    }

    private sealed class StaticSelectorContextSnapshot
    {
        public Vector3 Position { get; set; }
        public bool HasRecordedLocationProvenanceEpoch { get; set; }
        public int RecordedLocationProvenanceEpoch { get; set; }
        public string ResolvedSelectorLocationPrefab { get; set; } = "";
        public string SelectorSourceLabel { get; set; } = "";
        public string SelectorLocationKey { get; set; } = "";
        public string ConditionLocationName { get; set; } = "";
        public Heightmap.Biome Biome { get; set; }
        public bool InDungeon { get; set; }
    }
}
