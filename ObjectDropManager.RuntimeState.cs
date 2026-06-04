using System;
using System.Collections.Generic;

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

        public void Clear()
        {
            Snapshots.Clear();
            SnapshotsByPrefab.Clear();
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
