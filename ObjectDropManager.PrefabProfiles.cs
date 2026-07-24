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

    private static void RefreshConfiguredPrefabProfile(string prefabName)
    {
        PrefabProfileCatalogState.RefreshConfiguredPrefabProfile(RuntimeState.ActiveEntriesByPrefab, prefabName);
    }

    private static void RefreshConfiguredPrefabProfiles(
        Dictionary<string, List<PrefabConfigurationEntry>> activeEntriesByPrefab,
        Dictionary<string, LiveObjectComponentKind> configuredComponentKindsByPrefab,
        Dictionary<string, LiveObjectComponentKind> reconcileComponentKindsByPrefab)
    {
        configuredComponentKindsByPrefab.Clear();
        reconcileComponentKindsByPrefab.Clear();
        foreach (string prefabName in activeEntriesByPrefab.Keys)
        {
            RefreshConfiguredPrefabProfile(activeEntriesByPrefab, configuredComponentKindsByPrefab, prefabName);
            RefreshReconcilePrefabProfile(activeEntriesByPrefab, reconcileComponentKindsByPrefab, prefabName);
        }
    }

    private static void RefreshConfiguredPrefabProfile(
        Dictionary<string, List<PrefabConfigurationEntry>> activeEntriesByPrefab,
        Dictionary<string, LiveObjectComponentKind> configuredComponentKindsByPrefab,
        string prefabName)
    {
        if (!activeEntriesByPrefab.TryGetValue(prefabName, out List<PrefabConfigurationEntry>? entries) || entries.Count == 0)
        {
            configuredComponentKindsByPrefab.Remove(prefabName);
            return;
        }

        LiveObjectComponentKind configuredKinds = LiveObjectComponentKind.None;
        foreach (PrefabConfigurationEntry entry in entries)
        {
            configuredKinds |= GetConfiguredComponentKinds(entry);
        }

        if (configuredKinds == LiveObjectComponentKind.None)
        {
            configuredComponentKindsByPrefab.Remove(prefabName);
            return;
        }

        configuredComponentKindsByPrefab[prefabName] = configuredKinds;
    }

    private static void RefreshReconcilePrefabProfile(
        Dictionary<string, List<PrefabConfigurationEntry>> activeEntriesByPrefab,
        Dictionary<string, LiveObjectComponentKind> reconcileComponentKindsByPrefab,
        string prefabName)
    {
        if (!activeEntriesByPrefab.TryGetValue(prefabName, out List<PrefabConfigurationEntry>? entries) || entries.Count == 0)
        {
            reconcileComponentKindsByPrefab.Remove(prefabName);
            return;
        }

        LiveObjectComponentKind reconcileKinds = LiveObjectComponentKind.None;
        foreach (PrefabConfigurationEntry entry in entries)
        {
            reconcileKinds |= GetReconcileComponentKinds(entry);
        }

        if (reconcileKinds == LiveObjectComponentKind.None)
        {
            reconcileComponentKindsByPrefab.Remove(prefabName);
            return;
        }

        reconcileComponentKindsByPrefab[prefabName] = reconcileKinds;
    }

    private static LiveObjectComponentKind GetConfiguredComponentKinds(PrefabConfigurationEntry entry)
    {
        LiveObjectComponentKind kinds = LiveObjectComponentKind.None;
        if (entry.DropOnDestroyed != null)
        {
            kinds |= LiveObjectComponentKind.DropOnDestroyed;
        }

        if (entry.MineRock != null)
        {
            kinds |= LiveObjectComponentKind.MineRock;
        }

        if (entry.MineRock5 != null)
        {
            kinds |= LiveObjectComponentKind.MineRock5;
        }

        if (entry.TreeBase != null)
        {
            kinds |= LiveObjectComponentKind.TreeBase;
        }

        if (entry.TreeLog != null)
        {
            kinds |= LiveObjectComponentKind.TreeLog;
        }

        if (entry.Container != null)
        {
            kinds |= LiveObjectComponentKind.Container;
        }

        if (entry.Pickable != null)
        {
            kinds |= LiveObjectComponentKind.Pickable;
        }

        if (entry.PickableItem != null)
        {
            kinds |= LiveObjectComponentKind.PickableItem;
        }

        if (entry.Fish != null)
        {
            kinds |= LiveObjectComponentKind.Fish;
        }

        if (RequiresLiveReconcile(entry, entry.Destructible))
        {
            kinds |= LiveObjectComponentKind.Destructible;
        }

        return kinds;
    }

    private static LiveObjectComponentKind GetReconcileComponentKinds(PrefabConfigurationEntry entry)
    {
        LiveObjectComponentKind kinds = LiveObjectComponentKind.None;

        if (RequiresLiveReconcile(entry.DropOnDestroyed, LiveObjectComponentKind.DropOnDestroyed))
        {
            kinds |= LiveObjectComponentKind.DropOnDestroyed;
        }

        if (RequiresLiveReconcile(entry.MineRock, LiveObjectComponentKind.MineRock) &&
            !CanUseLazyDamageableScalarFastPath(entry, LiveObjectComponentKind.MineRock))
        {
            kinds |= LiveObjectComponentKind.MineRock;
        }

        if (RequiresLiveReconcile(entry.MineRock5, LiveObjectComponentKind.MineRock5) &&
            !CanUseLazyDamageableScalarFastPath(entry, LiveObjectComponentKind.MineRock5))
        {
            kinds |= LiveObjectComponentKind.MineRock5;
        }

        if (RequiresLiveReconcile(entry.TreeBase, LiveObjectComponentKind.TreeBase) &&
            !CanUseLazyDamageableScalarFastPath(entry, LiveObjectComponentKind.TreeBase))
        {
            kinds |= LiveObjectComponentKind.TreeBase;
        }

        if (RequiresLiveReconcile(entry.TreeLog, LiveObjectComponentKind.TreeLog) &&
            !CanUseLazyDamageableScalarFastPath(entry, LiveObjectComponentKind.TreeLog))
        {
            kinds |= LiveObjectComponentKind.TreeLog;
        }

        if (entry.Pickable != null)
        {
            kinds |= LiveObjectComponentKind.Pickable;
        }

        if (entry.PickableItem != null)
        {
            kinds |= LiveObjectComponentKind.PickableItem;
        }

        if (entry.Fish != null)
        {
            kinds |= LiveObjectComponentKind.Fish;
        }

        if (RequiresLiveReconcile(entry, entry.Destructible))
        {
            kinds |= LiveObjectComponentKind.Destructible;
        }

        return kinds;
    }

    private static bool RequiresLiveReconcileForPrefab(string prefabName)
    {
        return PrefabProfileCatalogState.RequiresLiveReconcile(prefabName);
    }

    private static bool RequiresLiveTrackingForPrefab(string prefabName)
    {
        return PrefabProfileCatalogState.RequiresLiveObjectTracking(prefabName);
    }

    private static bool RequiresLiveReconcileForPrefab(string prefabName, LiveObjectComponentKind componentKind)
    {
        return PrefabProfileCatalogState.RequiresLiveReconcile(prefabName, componentKind);
    }

    private static HashSet<string> FilterPrefabsRequiringLiveReconcile(IEnumerable<string>? prefabNames)
    {
        return PrefabProfileCatalogState.FilterPrefabsRequiringLiveReconcile(prefabNames);
    }

    private static HashSet<string> FilterPrefabsRequiringLiveTracking(IEnumerable<string>? prefabNames)
    {
        return PrefabProfileCatalogState.FilterPrefabsRequiringLiveTracking(prefabNames);
    }

    private static HashSet<string> SnapshotPrefabsRequiringLiveTracking()
    {
        return PrefabProfileCatalogState.SnapshotPrefabsRequiringLiveTracking();
    }

    private static void ReplaceComponentKinds(
        Dictionary<string, LiveObjectComponentKind> target,
        Dictionary<string, LiveObjectComponentKind> source)
    {
        target.Clear();
        foreach ((string prefabName, LiveObjectComponentKind kinds) in source)
        {
            if (kinds != LiveObjectComponentKind.None)
            {
                target[prefabName] = kinds;
            }
        }
    }
}
