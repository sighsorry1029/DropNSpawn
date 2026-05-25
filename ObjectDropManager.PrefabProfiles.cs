using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static void RefreshConfiguredPrefabProfile(string prefabName)
    {
        PrefabProfileCatalogState.RefreshConfiguredPrefabProfile(ActiveEntriesByPrefab, prefabName);
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

        if (ContainerNeedsLiveMutation(entry))
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
