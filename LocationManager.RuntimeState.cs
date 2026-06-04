using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private sealed class LocationConfigurationRuntimeState
    {
        public List<LocationConfigurationEntry> Configuration { get; set; } = new();
        public string ConfigurationSignature { get; set; } = "";
        public Dictionary<string, List<LocationConfigurationEntry>> ActiveEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<LocationConfigurationEntry>> LooseItemStandEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Reset()
        {
            Configuration = new List<LocationConfigurationEntry>();
            ConfigurationSignature = "";
            ActiveEntriesByPrefab.Clear();
            LooseItemStandEntriesByPrefab.Clear();
        }
    }

    private sealed class LocationPendingRuntimeState
    {
        public InstanceReconcileQueue<Location, PendingLocationReconcile> LocationReconciles { get; } =
            new(
                (location, instanceId, epoch) => new PendingLocationReconcile(location, instanceId, epoch),
                queuedReconcile => queuedReconcile.LocationInstanceId,
                queuedReconcile => queuedReconcile.Epoch,
                queuedReconcile => queuedReconcile.Location);

        public Dictionary<int, int> SuppressedLocationReconciles { get; } = new();
        public RingBufferQueue<PendingLocationRootReconcile> LocationRootReconciles { get; } = new();
        public HashSet<int> LocationRootReconcileIds { get; } = new();
        public InstanceReconcileQueue<OfferingBowl, PendingLooseOfferingBowlOverride> LooseOfferingBowlOverrides { get; } =
            new(
                (offeringBowl, instanceId, epoch) => new PendingLooseOfferingBowlOverride(offeringBowl, instanceId, epoch),
                queuedOverride => queuedOverride.OfferingBowlInstanceId,
                queuedOverride => queuedOverride.Epoch,
                queuedOverride => queuedOverride.OfferingBowl);

        public ScheduledFrameQueue<ZDOID> LocationProxyAliasZdoFlushIds { get; } = new();
        public Dictionary<ZDOID, PendingLocationProxyAliasZdoFlush> LocationProxyAliasZdoFlushes { get; } = new();
        public Dictionary<ZDOID, int> LocationProxyAliasZdoFlushEnqueuedDueFrames { get; } = new();
        public ScheduledFrameQueue<PendingLocationProxyObservation> LocationProxyObservations { get; } = new();
        public HashSet<int> LocationProxyObservationIds { get; } = new();
        public List<string> LocationProxyCreationPrefabs { get; } = new();
        public HashSet<string> RuntimeLocationProxyAliasDemands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ReconcileQueueEpoch { get; private set; }
        public bool NeedsRuntimeLocationProxyObservation { get; set; }
        public int LocationProxyObservationDemandEpoch { get; set; }
        public int LocationProxyAliasFlushBudgetFrame { get; set; } = int.MinValue;
        public int LocationProxyAliasFlushesSentThisFrame { get; set; }

        public bool HasPendingReconcileWork(int currentFrame, bool hasPendingLooseOverrideWork)
        {
            return LocationReconciles.HasPendingWork ||
                   LocationRootReconciles.Count > 0 ||
                   hasPendingLooseOverrideWork ||
                   (LocationProxyObservationIds.Count > 0 &&
                    LocationProxyObservations.HasDueItems(currentFrame)) ||
                   (LocationProxyAliasZdoFlushes.Count > 0 &&
                    LocationProxyAliasZdoFlushIds.HasDueItems(currentFrame));
        }

        public int GetPendingReconcileWorkCount()
        {
            return LocationReconciles.Count +
                   LocationRootReconciles.Count +
                   LooseOfferingBowlOverrides.Count +
                   LocationProxyObservationIds.Count +
                   LocationProxyAliasZdoFlushes.Count;
        }

        public void ClearQueuedReconcileState()
        {
            ReconcileQueueEpoch++;
            LocationReconciles.Clear();
            SuppressedLocationReconciles.Clear();
            LocationRootReconciles.Clear();
            LocationRootReconcileIds.Clear();
            LooseOfferingBowlOverrides.Clear();
            LocationProxyAliasZdoFlushIds.Clear();
            LocationProxyAliasZdoFlushes.Clear();
            LocationProxyAliasZdoFlushEnqueuedDueFrames.Clear();
            LocationProxyObservations.Clear();
            LocationProxyObservationIds.Clear();
            LocationProxyAliasFlushBudgetFrame = int.MinValue;
            LocationProxyAliasFlushesSentThisFrame = 0;
        }
    }

    private sealed class LocationLiveRuntimeState
    {
        public Dictionary<int, string> LocationPrefabNamesByHash { get; } = new();
        public Dictionary<LocationProxy, string> RuntimeLocationProxyPrefabsByInstance { get; } = new();
        public Dictionary<ZDOID, string> RuntimeLocationProxyPrefabsByZdoId { get; } = new();
        public ConditionalWeakTable<LocationProxy, LocationProxyObservationState> LocationProxyObservationStates { get; } = new();
        public ConditionalWeakTable<Location, LocationAliasRefreshRequestState> LocationAliasRefreshRequestStates { get; } = new();
        public Dictionary<ItemStand, ItemStandSnapshot> LooseItemStandSnapshots { get; } = new();
        public Dictionary<Location, LiveLocationSnapshot> LiveLocationSnapshots { get; } = new();
        public Dictionary<string, LocationComponentCatalog> CatalogsByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<Location>> LiveLocationsByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Location, string> LiveLocationPrefabsByInstance { get; } = new();
        public HashSet<LocationProxy> TrackedLocationProxies { get; } = new();
        public ConditionalWeakTable<OfferingBowl, LooseOfferingBowlOverrideState> LooseOfferingBowlOverrideStates { get; } = new();
        public Dictionary<string, List<AuthoredItemStandSlotTemplate>> AuthoredItemStandSlotsByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<ItemStand, string> TrackedLooseItemStandPrefabs { get; } = new();
        public Dictionary<ItemStand, string> LooseItemStandAuthoredPathsByInstance { get; } = new();
        public int RuntimeLocationAliasEpoch { get; set; }

        public void ClearLoadedConfigurationCaches()
        {
            LocationPrefabNamesByHash.Clear();
            AuthoredItemStandSlotsByPrefab.Clear();
        }

        public void ClearRuntimeState(bool preserveLiveRegistries)
        {
            CatalogsByPrefab.Clear();
            LiveLocationSnapshots.Clear();
            LooseItemStandSnapshots.Clear();
            TrackedLooseItemStandPrefabs.Clear();
            LooseItemStandAuthoredPathsByInstance.Clear();

            if (preserveLiveRegistries)
            {
                return;
            }

            LiveLocationsByPrefab.Clear();
            LiveLocationPrefabsByInstance.Clear();
            TrackedLocationProxies.Clear();
            RuntimeLocationProxyPrefabsByInstance.Clear();
            RuntimeLocationProxyPrefabsByZdoId.Clear();
            RuntimeLocationAliasEpoch++;
        }
    }
}
