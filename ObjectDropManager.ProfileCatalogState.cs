using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static readonly ObjectPrefabProfileCatalogState PrefabProfileCatalogState = new();

    private sealed class ObjectPrefabProfileCatalogState
    {
        private readonly Dictionary<string, LiveObjectComponentKind> _configuredComponentKindsByPrefab = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LiveObjectComponentKind> _reconcileComponentKindsByPrefab = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LiveObjectComponentKind> _lastAppliedConfiguredComponentKindsByPrefab = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LiveObjectComponentKind> _lastAppliedReconcileComponentKindsByPrefab = new(StringComparer.OrdinalIgnoreCase);

        public void Clear()
        {
            ClearCurrentProfiles();
            _lastAppliedConfiguredComponentKindsByPrefab.Clear();
            _lastAppliedReconcileComponentKindsByPrefab.Clear();
        }

        public void ClearCurrentProfiles()
        {
            _configuredComponentKindsByPrefab.Clear();
            _reconcileComponentKindsByPrefab.Clear();
        }

        public void RefreshConfiguredPrefabProfile(Dictionary<string, List<PrefabConfigurationEntry>> activeEntriesByPrefab, string prefabName)
        {
            ObjectDropManager.RefreshConfiguredPrefabProfile(activeEntriesByPrefab, _configuredComponentKindsByPrefab, prefabName);
            ObjectDropManager.RefreshReconcilePrefabProfile(activeEntriesByPrefab, _reconcileComponentKindsByPrefab, prefabName);
        }

        public void ApplySyncedProfiles(
            Dictionary<string, LiveObjectComponentKind> configuredComponentKindsByPrefab,
            Dictionary<string, LiveObjectComponentKind> reconcileComponentKindsByPrefab)
        {
            ReplaceComponentKinds(_configuredComponentKindsByPrefab, configuredComponentKindsByPrefab);
            ReplaceComponentKinds(_reconcileComponentKindsByPrefab, reconcileComponentKindsByPrefab);
        }

        public bool TryGetReconcileKinds(string prefabName, out LiveObjectComponentKind reconcileKinds)
        {
            return _reconcileComponentKindsByPrefab.TryGetValue(prefabName, out reconcileKinds);
        }

        public bool RequiresLiveReconcile(string prefabName)
        {
            if (prefabName.Length == 0)
            {
                return false;
            }

            if (_reconcileComponentKindsByPrefab.TryGetValue(prefabName, out LiveObjectComponentKind currentKinds) &&
                currentKinds != LiveObjectComponentKind.None)
            {
                return true;
            }

            return _lastAppliedReconcileComponentKindsByPrefab.TryGetValue(prefabName, out LiveObjectComponentKind previousKinds) &&
                   previousKinds != LiveObjectComponentKind.None;
        }

        public bool RequiresLiveObjectTracking(string prefabName)
        {
            if (prefabName.Length == 0)
            {
                return false;
            }

            if (_configuredComponentKindsByPrefab.TryGetValue(prefabName, out LiveObjectComponentKind currentKinds) &&
                currentKinds != LiveObjectComponentKind.None)
            {
                return true;
            }

            return _lastAppliedConfiguredComponentKindsByPrefab.TryGetValue(prefabName, out LiveObjectComponentKind previousKinds) &&
                   previousKinds != LiveObjectComponentKind.None;
        }

        public bool RequiresLiveReconcile(string prefabName, LiveObjectComponentKind componentKind)
        {
            if (componentKind == LiveObjectComponentKind.Piece)
            {
                return RequiresLiveReconcile(prefabName);
            }

            return _reconcileComponentKindsByPrefab.TryGetValue(prefabName, out LiveObjectComponentKind reconcileKinds) &&
                   (reconcileKinds & componentKind) != 0;
        }

        public HashSet<string> FilterPrefabsRequiringLiveReconcile(IEnumerable<string>? prefabNames)
        {
            HashSet<string> prefabs = new(StringComparer.OrdinalIgnoreCase);
            if (prefabNames == null)
            {
                return prefabs;
            }

            foreach (string prefabName in prefabNames)
            {
                if (RequiresLiveReconcile(prefabName))
                {
                    prefabs.Add(prefabName);
                }
            }

            return prefabs;
        }

        public HashSet<string> FilterPrefabsRequiringLiveTracking(IEnumerable<string>? prefabNames)
        {
            HashSet<string> prefabs = new(StringComparer.OrdinalIgnoreCase);
            if (prefabNames == null)
            {
                return prefabs;
            }

            foreach (string prefabName in prefabNames)
            {
                if (RequiresLiveObjectTracking(prefabName))
                {
                    prefabs.Add(prefabName);
                }
            }

            return prefabs;
        }

        public HashSet<string> SnapshotPrefabsRequiringLiveTracking()
        {
            HashSet<string> prefabs = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string prefabName, LiveObjectComponentKind kinds) in _configuredComponentKindsByPrefab)
            {
                if (kinds != LiveObjectComponentKind.None)
                {
                    prefabs.Add(prefabName);
                }
            }

            foreach ((string prefabName, LiveObjectComponentKind kinds) in _lastAppliedConfiguredComponentKindsByPrefab)
            {
                if (kinds != LiveObjectComponentKind.None)
                {
                    prefabs.Add(prefabName);
                }
            }

            return prefabs;
        }

        public void RecordCurrentConfiguredKindsAsLastApplied()
        {
            ReplaceComponentKinds(_lastAppliedConfiguredComponentKindsByPrefab, _configuredComponentKindsByPrefab);
        }

        public void RecordCurrentReconcileKindsAsLastApplied()
        {
            ReplaceComponentKinds(_lastAppliedReconcileComponentKindsByPrefab, _reconcileComponentKindsByPrefab);
        }

        public void ClearLastAppliedConfiguredKinds()
        {
            _lastAppliedConfiguredComponentKindsByPrefab.Clear();
        }

        public void ClearLastAppliedReconcileKinds()
        {
            _lastAppliedReconcileComponentKindsByPrefab.Clear();
        }
    }
}
