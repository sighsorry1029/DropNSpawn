using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private sealed class ObjectConfigurationRuntimeState
    {
        public List<PrefabConfigurationEntry> Configuration { get; set; } = new();
        public string ConfigurationSignature { get; set; } = "";
        public Dictionary<string, List<PrefabConfigurationEntry>> ActiveEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<PrefabConfigurationEntry>> VneiEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Reset()
        {
            Configuration = new List<PrefabConfigurationEntry>();
            ConfigurationSignature = "";
            ActiveEntriesByPrefab.Clear();
            VneiEntriesByPrefab.Clear();
        }
    }

    private sealed class ObjectSnapshotRuntimeState
    {
        public List<PrefabSnapshot> Snapshots { get; } = new();
        public Dictionary<string, PrefabSnapshot> SnapshotsByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Retain the original values for surviving templates, including temporarily
        // unregistered ones. Recapturing a DNS-modified template would corrupt its baseline.
        private readonly Dictionary<int, PrefabSnapshot> _baselinesByInstance = new();

        public PrefabSnapshot? GetOrCapture(GameObject? prefab)
        {
            if (prefab == null) return null;
            int id = prefab.GetInstanceID();
            if (_baselinesByInstance.TryGetValue(id, out PrefabSnapshot? snapshot) &&
                ReferenceEquals(snapshot.Prefab, prefab))
            {
                if (!string.Equals(snapshot.PrefabName, prefab.name, StringComparison.Ordinal))
                    _baselinesByInstance[id] = snapshot = snapshot.WithName(prefab.name);
                return snapshot;
            }

            snapshot = CaptureSnapshot(prefab);
            if (snapshot != null) _baselinesByInstance[id] = snapshot;
            return snapshot;
        }

        public void PruneDestroyedBaselines()
        {
            List<int> dead = new();
            foreach (var pair in _baselinesByInstance)
                if (pair.Value.Prefab == null) dead.Add(pair.Key);
            foreach (int id in dead) _baselinesByInstance.Remove(id);
        }

        public void Clear()
        {
            Snapshots.Clear();
            SnapshotsByPrefab.Clear();
            _baselinesByInstance.Clear();
        }

        public void ReplaceWith(PendingSnapshotBuildState buildState)
        {
            Snapshots.Clear();
            Snapshots.AddRange(buildState.Snapshots);
            SnapshotsByPrefab.Clear();
            foreach ((string prefabName, PrefabSnapshot snapshot) in buildState.SnapshotsByPrefab)
            {
                SnapshotsByPrefab[prefabName] = snapshot;
            }
        }
    }

    private sealed class ObjectRuntimeDropState
    {
        public ObjectRuntimeDropConfigurationState Configuration { get; set; } = ObjectRuntimeDropConfigurationState.Empty;
        public string ConfigurationSignature { get; set; } = "";
        public int? GameDataSignature { get; set; }

        public void Reset()
        {
            Configuration = ObjectRuntimeDropConfigurationState.Empty;
            ConfigurationSignature = "";
            GameDataSignature = null;
        }
    }
}
