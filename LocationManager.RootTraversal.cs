using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private static void TrackSpawnedLocationRootInternal(GameObject? rootObject)
    {
        if (rootObject == null)
        {
            return;
        }

        List<Location> locations = new();
        CollectLocationsUnderRoot(rootObject.transform, locations);
        foreach (Location location in locations)
        {
            if (location != null)
            {
                TrackLocationInstanceInternal(location);
            }
        }
    }

    private static void CollectLocationsUnderRoot(Transform? root, List<Location> locations)
    {
        if (root == null)
        {
            return;
        }

        SceneTraversalSupport.TraverseHierarchy(root, transform =>
        {
            if (transform.TryGetComponent(out Location location) && location != null)
            {
                locations.Add(location);
            }
        });
    }

    private static void CollectLocationRuntimeComponents(
        Transform? root,
        List<OfferingBowl> offeringBowls,
        List<ItemStand> itemStands,
        List<Vegvisir> vegvisirs,
        List<RuneStone> runestones)
    {
        if (root == null)
        {
            return;
        }

        SceneTraversalSupport.TraverseHierarchy(root, transform =>
        {
            if (transform.TryGetComponent(out OfferingBowl offeringBowl) && offeringBowl != null)
            {
                offeringBowls.Add(offeringBowl);
            }

            if (transform.TryGetComponent(out ItemStand itemStand) && itemStand != null)
            {
                itemStands.Add(itemStand);
            }

            if (transform.TryGetComponent(out Vegvisir vegvisir) && vegvisir != null)
            {
                vegvisirs.Add(vegvisir);
            }

            if (transform.TryGetComponent(out RuneStone runestone) && runestone != null)
            {
                runestones.Add(runestone);
            }
        });
    }

    private static bool ProcessQueuedLocationRootStep(float deadline)
    {
        while (PendingLocationRootReconciles.TryPeek(out PendingLocationRootReconcile pendingRoot))
        {
            if (pendingRoot.Epoch != _reconcileQueueEpoch)
            {
                PendingLocationRootReconciles.TryDequeue(out _);
                PendingLocationRootReconcileIds.Remove(pendingRoot.RootInstanceId);
                continue;
            }

            if (pendingRoot.RootObject == null)
            {
                PendingLocationRootReconciles.TryDequeue(out _);
                PendingLocationRootReconcileIds.Remove(pendingRoot.RootInstanceId);
                return true;
            }

            if (!_initialized)
            {
                Initialize();
            }

            bool didWork = false;
            while (Time.realtimeSinceStartup < deadline)
            {
                switch (pendingRoot.Phase)
                {
                    case PendingLocationRootPhase.TraverseHierarchy:
                        if (!TraversePendingLocationRootHierarchyStep(pendingRoot, deadline))
                        {
                            return didWork;
                        }

                        pendingRoot.Phase = PendingLocationRootPhase.ReconcileLocations;
                        didWork = true;
                        continue;
                    case PendingLocationRootPhase.ReconcileLocations:
                        return ReconcilePendingLocationRootLocationStep(pendingRoot) || didWork;
                    default:
                        return didWork;
                }
            }

            return didWork;
        }

        return false;
    }

    private static bool TraversePendingLocationRootHierarchyStep(PendingLocationRootReconcile pendingRoot, float deadline)
    {
        pendingRoot.Locations ??= new List<Location>();
        pendingRoot.TraversalStack ??= new List<PendingLocationTraversalNode>
        {
            new(pendingRoot.RootObject.transform, null)
        };
        pendingRoot.RuntimeComponentsByLocationId ??= new Dictionary<int, LocationRuntimeComponents>();

        int processedNodes = 0;
        while (pendingRoot.TraversalStack.Count > 0)
        {
            if ((processedNodes & 15) == 0 && Time.realtimeSinceStartup >= deadline)
            {
                return false;
            }

            int lastIndex = pendingRoot.TraversalStack.Count - 1;
            PendingLocationTraversalNode node = pendingRoot.TraversalStack[lastIndex];
            pendingRoot.TraversalStack.RemoveAt(lastIndex);
            processedNodes++;

            Transform transform = node.Transform;
            if (transform == null)
            {
                continue;
            }

            Location? currentLocation = node.CurrentLocation;
            if (transform.TryGetComponent(out Location locationComponent) && locationComponent != null)
            {
                currentLocation = locationComponent;
                EnsurePendingLocationRuntimeComponents(pendingRoot, locationComponent);
            }

            if (currentLocation != null)
            {
                int locationId = currentLocation.GetInstanceID();
                if (pendingRoot.RuntimeComponentsByLocationId != null &&
                    pendingRoot.RuntimeComponentsByLocationId.TryGetValue(locationId, out LocationRuntimeComponents? components) &&
                    transform.TryGetComponent(out OfferingBowl offeringBowl))
                {
                    AddPendingLocationOfferingBowl(components, offeringBowl);
                }

                if (pendingRoot.RuntimeComponentsByLocationId != null &&
                    pendingRoot.RuntimeComponentsByLocationId.TryGetValue(locationId, out components) &&
                    transform.TryGetComponent(out ItemStand itemStand))
                {
                    AddPendingLocationItemStand(components, itemStand);
                }

                if (pendingRoot.RuntimeComponentsByLocationId != null &&
                    pendingRoot.RuntimeComponentsByLocationId.TryGetValue(locationId, out components) &&
                    transform.TryGetComponent(out Vegvisir vegvisir))
                {
                    AddPendingLocationVegvisir(components, vegvisir);
                }

                if (pendingRoot.RuntimeComponentsByLocationId != null &&
                    pendingRoot.RuntimeComponentsByLocationId.TryGetValue(locationId, out components) &&
                    transform.TryGetComponent(out RuneStone runestone))
                {
                    AddPendingLocationRunestone(components, runestone);
                }
            }

            for (int childIndex = transform.childCount - 1; childIndex >= 0; childIndex--)
            {
                Transform child = transform.GetChild(childIndex);
                if (child != null)
                {
                    pendingRoot.TraversalStack.Add(new PendingLocationTraversalNode(child, currentLocation));
                }
            }
        }

        FinalizePendingLocationRootBundle(pendingRoot);
        pendingRoot.NextIndex = 0;
        return true;
    }

    private static void EnsurePendingLocationRuntimeComponents(PendingLocationRootReconcile pendingRoot, Location location)
    {
        if (pendingRoot.RuntimeComponentsByLocationId == null ||
            pendingRoot.Locations == null)
        {
            return;
        }

        int locationId = location.GetInstanceID();
        if (pendingRoot.RuntimeComponentsByLocationId.ContainsKey(locationId))
        {
            return;
        }

        pendingRoot.Locations.Add(location);
        pendingRoot.RuntimeComponentsByLocationId[locationId] = new LocationRuntimeComponents
        {
            Root = location.transform
        };
    }

    private static void AddPendingLocationOfferingBowl(LocationRuntimeComponents components, OfferingBowl offeringBowl)
    {
        if (components == null || offeringBowl == null)
        {
            return;
        }

        components.OfferingBowls.Add(offeringBowl);
        components.PrimaryOfferingBowl ??= offeringBowl;
        components.OfferingBowlsByPath[GetRelativePath(components.Root, offeringBowl.transform)] = offeringBowl;
    }

    private static void AddPendingLocationItemStand(LocationRuntimeComponents components, ItemStand itemStand)
    {
        if (components == null || itemStand == null)
        {
            return;
        }

        components.ItemStands.Add(itemStand);
        components.ItemStandsByPath[GetRelativePath(components.Root, itemStand.transform)] = itemStand;
    }

    private static void AddPendingLocationVegvisir(LocationRuntimeComponents components, Vegvisir vegvisir)
    {
        if (components == null || vegvisir == null)
        {
            return;
        }

        components.Vegvisirs.Add(vegvisir);
        components.VegvisirsByPath[GetRelativePath(components.Root, vegvisir.transform)] = vegvisir;
    }

    private static void AddPendingLocationRunestone(LocationRuntimeComponents components, RuneStone runestone)
    {
        if (components == null || runestone == null)
        {
            return;
        }

        components.Runestones.Add(runestone);
        components.RunestonesByPath[GetRelativePath(components.Root, runestone.transform)] = runestone;
    }

    private static void FinalizePendingLocationRootBundle(PendingLocationRootReconcile pendingRoot)
    {
        if (pendingRoot.Locations == null || pendingRoot.RuntimeComponentsByLocationId == null)
        {
            return;
        }

        for (int index = 0; index < pendingRoot.Locations.Count; index++)
        {
            Location? location = pendingRoot.Locations[index];
            if (location == null)
            {
                continue;
            }

            int locationId = location.GetInstanceID();
            if (!pendingRoot.RuntimeComponentsByLocationId.TryGetValue(locationId, out LocationRuntimeComponents? components))
            {
                continue;
            }

            components.RelevantItemStands = GetRelevantLocationItemStands(components.PrimaryOfferingBowl, components.ItemStands);
        }
    }

    private static bool ReconcilePendingLocationRootLocationStep(PendingLocationRootReconcile pendingRoot)
    {
        while (pendingRoot.Locations != null && pendingRoot.NextIndex < pendingRoot.Locations.Count)
        {
            Location? location = pendingRoot.Locations[pendingRoot.NextIndex++];
            if (location == null)
            {
                continue;
            }

            int instanceId = location.GetInstanceID();
            if (PendingLocationReconcileIds.Contains(instanceId))
            {
                SuppressedQueuedLocationReconciles[instanceId] =
                    SuppressedQueuedLocationReconciles.TryGetValue(instanceId, out int suppressedCount)
                        ? suppressedCount + 1
                        : 1;
            }

            TrackLocationInstanceInternal(location);
            LocationRuntimeComponents? runtimeComponents = null;
            pendingRoot.RuntimeComponentsByLocationId?.TryGetValue(instanceId, out runtimeComponents);
            ReconcileLocationInstanceInternal(location, runtimeComponents);
            return true;
        }

        PendingLocationRootReconciles.TryDequeue(out _);
        PendingLocationRootReconcileIds.Remove(pendingRoot.RootInstanceId);
        return true;
    }
}
