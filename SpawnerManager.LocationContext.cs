using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static readonly int ExpandWorldDataLocationReferenceHash = "locationreference".GetStableHashCode();
    private static readonly int DropNSpawnLocationPrefabHash = "dropnspawn.locationPrefab".GetStableHashCode();
    private static bool _expandWorldDataCurrentLocationFieldResolved;
    private static FieldInfo? _expandWorldDataCurrentLocationField;

    private sealed class PendingLocationRootProvenanceScan
    {
        public int RootInstanceId { get; set; }
        public Transform RootTransform { get; set; } = null!;
        public string LocationPrefab { get; set; } = "";
        public int Epoch { get; set; }
        public List<Transform>? TraversalStack { get; set; }
    }

    internal static void RecordSpawnedLocationProxyProvenance(LocationProxy? proxy, GameObject? spawnedRoot)
    {
        lock (Sync)
        {
            if (proxy == null || spawnedRoot == null)
            {
                return;
            }

            if (!TryGetActiveLocationSpawnContextPrefab(out string locationPrefab) &&
                !TryGetLocationProxyPrefabName(proxy, out locationPrefab))
            {
                return;
            }

            Transform rootTransform = spawnedRoot.transform;
            RecordSpawnedLocationRootProvenance(rootTransform, locationPrefab);
            QueueSpawnedLocationRootProvenanceScan(rootTransform, locationPrefab);
        }
    }

    internal static void RecordSpawnedLocationRootProvenance(GameObject? spawnedRoot)
    {
        lock (Sync)
        {
            if (spawnedRoot == null ||
                !TryGetActiveLocationSpawnContextPrefab(out string locationPrefab))
            {
                return;
            }

            Transform rootTransform = spawnedRoot.transform;
            RecordSpawnedLocationRootProvenance(rootTransform, locationPrefab);
            QueueSpawnedLocationRootProvenanceScan(rootTransform, locationPrefab);
        }
    }

    internal static void BeginLocationSpawnContext(ZoneSystem.ZoneLocation? location)
    {
        lock (Sync)
        {
            PushLocationSpawnContext(GetLocationSpawnContextPrefabName(location));
        }
    }

    internal static void BeginLocationSpawnContext(string? locationPrefab)
    {
        lock (Sync)
        {
            PushLocationSpawnContext(locationPrefab);
        }
    }

    internal static bool TryBeginDerivedLocationSpawnContext(Component? source)
    {
        lock (Sync)
        {
            if (source == null || source.gameObject == null)
            {
                return false;
            }

            if (!TryResolveLocationSpawnContext(source.gameObject, out string locationPrefab))
            {
                return false;
            }

            PushLocationSpawnContext(locationPrefab);
            return true;
        }
    }

    internal static bool TryBeginDerivedLocationSpawnContext(DungeonGenerator? generator)
    {
        lock (Sync)
        {
            if (TryResolveDungeonGeneratorLocationContext(generator, out string locationPrefab))
            {
                PushLocationSpawnContext(locationPrefab);
                return true;
            }
        }

        return TryBeginDerivedLocationSpawnContext(generator as Component);
    }

    internal static void EndLocationSpawnContext()
    {
        lock (Sync)
        {
            ProvenanceRegistry.PopLocationSpawnContext();
        }
    }

    private static void PushLocationSpawnContext(string? locationPrefab)
    {
        ProvenanceRegistry.PushLocationSpawnContext(locationPrefab);
    }

    private static bool TryResolveLocationSpawnContext(GameObject gameObject, out string locationPrefab)
    {
        locationPrefab = "";
        if (gameObject == null)
        {
            return false;
        }

        if (TryGetActiveLocationSpawnContextPrefab(out locationPrefab))
        {
            return locationPrefab.Length > 0;
        }

        if (TryFindRecordedLocationRoot(gameObject.transform, out _, out locationPrefab))
        {
            return locationPrefab.Length > 0;
        }

        if (TryGetLiveLocationProxyPrefab(gameObject, out locationPrefab))
        {
            return locationPrefab.Length > 0;
        }

        if (TryGetClonedZoneLocationContext(gameObject, out locationPrefab))
        {
            return true;
        }

        if (TryGetPersistedSpawnerLocationContext(gameObject, out locationPrefab, out _))
        {
            return true;
        }

        if (TryGetDungeonGeneratorLocationContext(gameObject, out locationPrefab))
        {
            return locationPrefab.Length > 0;
        }

        Location? location = gameObject.GetComponentInParent<Location>(true);
        if (location != null && TryGetLocationPrefabName(location, out locationPrefab))
        {
            return locationPrefab.Length > 0;
        }

        if (TryGetStaticLocationContext(gameObject, out locationPrefab, out _))
        {
            return locationPrefab.Length > 0;
        }

        if (TryGetZoneLocationContext(gameObject, out locationPrefab))
        {
            return locationPrefab.Length > 0;
        }

        if (TryGetSpatialLocationContext(gameObject, out locationPrefab))
        {
            return locationPrefab.Length > 0;
        }

        return false;
    }

    internal static bool TryGetResolvedLocationNameForConditions(GameObject? gameObject, out string locationPrefab)
    {
        lock (Sync)
        {
            locationPrefab = "";
            if (gameObject == null)
            {
                return false;
            }

            if (TryGetRecordedLocationContext(gameObject, out locationPrefab, out _))
            {
                return locationPrefab.Length > 0;
            }

            if (TryGetCurrentLocationSpawnContext(gameObject, out locationPrefab, out _))
            {
                return locationPrefab.Length > 0;
            }

            if (TryGetLiveLocationProxyContext(gameObject, out locationPrefab, out _))
            {
                return locationPrefab.Length > 0;
            }

            if (TryGetClonedZoneLocationContext(gameObject, out locationPrefab))
            {
                return true;
            }

            if (TryGetPersistedSpawnerLocationContext(gameObject, out locationPrefab, out _))
            {
                return true;
            }

            if (TryGetDungeonGeneratorLocationContext(gameObject, out locationPrefab))
            {
                return locationPrefab.Length > 0;
            }

            if (TryGetStaticLocationContext(gameObject, out locationPrefab, out _))
            {
                return locationPrefab.Length > 0;
            }

            if (TryGetZoneLocationContext(gameObject, out locationPrefab))
            {
                return locationPrefab.Length > 0;
            }

            return false;
        }
    }

    internal static void UntrackLocationInstanceProvenance(Location? location)
    {
        lock (Sync)
        {
            if (location == null)
            {
                return;
            }

            ProvenanceRegistry.RemoveSpawnedLocationRoot(location.transform);
        }
    }

    private static bool TryGetLiveLocationProxyContext(GameObject gameObject, out string locationPrefab, out string relativePath)
    {
        locationPrefab = "";
        relativePath = "";
        if (gameObject == null)
        {
            return false;
        }

        LocationProxy? proxy = gameObject.GetComponentInParent<LocationProxy>(true);
        if (proxy == null || !TryGetLocationProxyPrefabName(proxy, out locationPrefab))
        {
            return false;
        }

        Transform? spawnedRoot = GetLocationProxySpawnedRootTransform(proxy.transform, gameObject.transform);
        if (spawnedRoot == null)
        {
            return false;
        }

        relativePath = GetRelativePath(spawnedRoot, gameObject.transform);
        return relativePath.Length > 0;
    }

    private static bool TryGetLocationProxyPrefabName(LocationProxy proxy, out string prefabName)
    {
        prefabName = "";
        if (proxy == null)
        {
            return false;
        }

        ZNetView? nview = proxy.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int expandWorldDataLocationHash = zdo?.GetInt(ExpandWorldDataLocationReferenceHash) ?? 0;
        if (expandWorldDataLocationHash != 0 && TryGetZoneLocationPrefabName(expandWorldDataLocationHash, out prefabName))
        {
            return true;
        }

        int locationHash = zdo?.GetInt(ZDOVars.s_location) ?? 0;
        if (locationHash != 0 && TryGetZoneLocationPrefabName(locationHash, out prefabName))
        {
            return true;
        }

        Location? childLocation = proxy.GetComponentInChildren<Location>(true);
        if (childLocation != null && TryGetLocationPrefabName(childLocation, out prefabName))
        {
            return true;
        }

        return TryGetZoneLocationPrefabName(proxy.transform.position, out prefabName);
    }

    private static bool TryGetDirectLocationContext(GameObject gameObject, out string locationPrefab, out string relativePath)
    {
        locationPrefab = "";
        relativePath = "";
        if (gameObject == null)
        {
            return false;
        }

        Location? location = gameObject.GetComponentInParent<Location>(true);
        if (location == null || !TryGetLocationPrefabName(location, out locationPrefab))
        {
            return false;
        }

        relativePath = GetRelativePath(location.transform, gameObject.transform);
        return locationPrefab.Length > 0;
    }

    private static bool TryGetStaticLocationContext(GameObject gameObject, out string locationPrefab, out string relativePath)
    {
        locationPrefab = "";
        relativePath = "";
        if (gameObject == null)
        {
            return false;
        }

        Location? location = Location.GetLocation(gameObject.transform.position, checkDungeons: true);
        if (location == null)
        {
            location = Location.GetZoneLocation(gameObject.transform.position);
        }

        if (location == null || !TryGetLocationPrefabName(location, out locationPrefab))
        {
            return false;
        }

        relativePath = TryGetRelativePathIfDescendant(location.transform, gameObject.transform);
        return locationPrefab.Length > 0;
    }

    private static bool TryGetRecordedLocationContext(GameObject gameObject, out string locationPrefab, out string relativePath)
    {
        locationPrefab = "";
        relativePath = "";
        if (gameObject == null)
        {
            return false;
        }

        if (gameObject.TryGetComponent(out SpawnArea spawnArea) &&
            ProvenanceRegistry.TryGetSpawnAreaProvenance(spawnArea, out SpawnerLocationProvenance spawnAreaProvenance))
        {
            locationPrefab = spawnAreaProvenance.LocationPrefab;
            relativePath = spawnAreaProvenance.RelativePath;
            return locationPrefab.Length > 0;
        }

        if (gameObject.TryGetComponent(out CreatureSpawner creatureSpawner) &&
            ProvenanceRegistry.TryGetCreatureSpawnerProvenance(creatureSpawner, out SpawnerLocationProvenance creatureSpawnerProvenance))
        {
            locationPrefab = creatureSpawnerProvenance.LocationPrefab;
            relativePath = creatureSpawnerProvenance.RelativePath;
            return locationPrefab.Length > 0;
        }

        return false;
    }

    private static bool TryGetPersistedSpawnerLocationContext(GameObject gameObject, out string locationPrefab, out string relativePath)
    {
        locationPrefab = "";
        relativePath = "";
        if (gameObject == null)
        {
            return false;
        }

        ZNetView? netView = gameObject.GetComponent<ZNetView>();
        ZDO? zdo = netView?.GetZDO();
        locationPrefab = (zdo?.GetString(DropNSpawnLocationPrefabHash, "") ?? "").Trim();
        return locationPrefab.Length > 0;
    }

    private static bool TryGetCurrentLocationSpawnContext(GameObject gameObject, out string locationPrefab, out string relativePath)
    {
        locationPrefab = "";
        relativePath = "";
        if (gameObject == null || !TryGetActiveLocationSpawnContextPrefab(out locationPrefab))
        {
            return false;
        }

        if (!gameObject.TryGetComponent<SpawnArea>(out _) &&
            !gameObject.TryGetComponent<CreatureSpawner>(out _))
        {
            return false;
        }

        return locationPrefab.Length > 0;
    }

    private static bool TryGetSpatialLocationContext(GameObject gameObject, out string locationPrefab)
    {
        locationPrefab = "";
        if (gameObject == null || ZoneSystem.instance == null)
        {
            return false;
        }

        return TryGetSpatialLocationPrefabName(gameObject.transform.position, out locationPrefab);
    }

    private static bool TryGetSpatialLocationPrefabName(Vector3 position, out string locationPrefab)
    {
        locationPrefab = "";
        if (ZoneSystem.instance == null)
        {
            return false;
        }

        float bestDistance = float.MaxValue;
        bool found = false;
        foreach (ZoneSystem.LocationInstance locationInstance in ZoneSystem.instance.m_locationInstances.Values)
        {
            string candidate = GetZoneLocationPrefabName(locationInstance.m_location);
            if (candidate.Length == 0)
            {
                continue;
            }

            float radius = Mathf.Max(locationInstance.m_location.m_exteriorRadius, locationInstance.m_location.m_interiorRadius);
            if (radius <= 0f)
            {
                continue;
            }

            float distance = Utils.DistanceXZ(locationInstance.m_position, position);
            if (distance > radius)
            {
                continue;
            }

            if (!found || distance < bestDistance)
            {
                bestDistance = distance;
                locationPrefab = candidate;
                found = true;
            }
        }

        return found;
    }

    private static bool TryGetZoneLocationContext(GameObject gameObject, out string locationPrefab)
    {
        locationPrefab = "";
        if (gameObject == null || ZoneSystem.instance == null)
        {
            return false;
        }

        Vector2i zone = ZoneSystem.GetZone(gameObject.transform.position);
        if (!ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance locationInstance))
        {
            return false;
        }

        string candidate = GetZoneLocationPrefabName(locationInstance.m_location);
        if (candidate.Length == 0)
        {
            return false;
        }

        locationPrefab = candidate;
        return true;
    }

    private static bool TryGetDungeonGeneratorLocationContext(GameObject gameObject, out string locationPrefab)
    {
        locationPrefab = "";
        if (gameObject == null)
        {
            return false;
        }

        return TryResolveDungeonGeneratorLocationContext(gameObject.GetComponentInParent<DungeonGenerator>(true), out locationPrefab);
    }

    private static bool TryResolveDungeonGeneratorLocationContext(DungeonGenerator? generator, out string locationPrefab)
    {
        locationPrefab = "";
        if (generator == null)
        {
            return false;
        }

        Vector3 originalPosition = generator.m_originalPosition;
        if (originalPosition == Vector3.zero)
        {
            return false;
        }

        return TryGetZoneLocationPrefabName(originalPosition, out locationPrefab) ||
               TryGetSpatialLocationPrefabName(originalPosition, out locationPrefab);
    }

    private static bool TryGetClonedZoneLocationContext(GameObject gameObject, out string locationPrefab)
    {
        locationPrefab = "";
        if (!TryGetZoneLocationContext(gameObject, out string zoneLocationPrefab) ||
            !zoneLocationPrefab.Contains(':'))
        {
            return false;
        }

        locationPrefab = zoneLocationPrefab;
        return true;
    }

    private static Transform? GetLocationProxySpawnedRootTransform(Transform proxyTransform, Transform target)
    {
        Transform? current = target;
        while (current != null && current.parent != null)
        {
            if (ReferenceEquals(current.parent, proxyTransform))
            {
                return current;
            }

            current = current.parent;
        }

        return null;
    }

    private static void RecordSpawnedLocationRootProvenance(Transform rootTransform, string locationPrefab)
    {
        ProvenanceRegistry.RecordSpawnedLocationRoot(rootTransform, locationPrefab);
    }

    private static void QueueSpawnedLocationRootProvenanceScan(Transform rootTransform, string locationPrefab)
    {
        _ = ProvenanceRegistry.TryQueueRootProvenanceScan(rootTransform, locationPrefab, _reconcileQueueEpoch);
    }

    private static bool ProcessQueuedLocationRootProvenanceStep(float deadline)
    {
        while (ProvenanceRegistry.TryPeekPendingRootScan(out PendingLocationRootProvenanceScan pendingScan))
        {
            if (pendingScan.Epoch != _reconcileQueueEpoch)
            {
                ProvenanceRegistry.DiscardPendingRootScan(pendingScan);
                continue;
            }

            if (pendingScan.RootTransform == null || string.IsNullOrWhiteSpace(pendingScan.LocationPrefab))
            {
                ProvenanceRegistry.DiscardPendingRootScan(pendingScan);
                return true;
            }

            pendingScan.TraversalStack ??= new List<Transform>
            {
                pendingScan.RootTransform
            };

            int processedNodes = 0;
            while (pendingScan.TraversalStack.Count > 0)
            {
                if ((processedNodes & 15) == 0 && Time.realtimeSinceStartup >= deadline)
                {
                    return processedNodes > 0;
                }

                int lastIndex = pendingScan.TraversalStack.Count - 1;
                Transform transform = pendingScan.TraversalStack[lastIndex];
                pendingScan.TraversalStack.RemoveAt(lastIndex);
                processedNodes++;

                if (transform == null)
                {
                    continue;
                }

                if (transform.TryGetComponent(out SpawnArea spawnArea) && spawnArea != null)
                {
                    RecordSpawnAreaProvenance(spawnArea, pendingScan.RootTransform, pendingScan.LocationPrefab);
                }

                if (transform.TryGetComponent(out CreatureSpawner creatureSpawner) && creatureSpawner != null)
                {
                    RecordCreatureSpawnerProvenance(creatureSpawner, pendingScan.RootTransform, pendingScan.LocationPrefab);
                }

                for (int childIndex = transform.childCount - 1; childIndex >= 0; childIndex--)
                {
                    Transform child = transform.GetChild(childIndex);
                    if (child != null)
                    {
                        pendingScan.TraversalStack.Add(child);
                    }
                }
            }

            ProvenanceRegistry.DiscardPendingRootScan(pendingScan);
            return true;
        }

        return false;
    }

    private static void CaptureSpawnAreaProvenanceIfAvailable(SpawnArea spawnArea)
    {
        if (spawnArea == null || ProvenanceRegistry.HasSpawnAreaProvenance(spawnArea))
        {
            return;
        }

        if (TryResolveSpawnerProvenance(spawnArea.gameObject, out Transform? rootTransform, out string locationPrefab, out string relativePath))
        {
            RecordSpawnAreaProvenance(spawnArea, rootTransform, locationPrefab, relativePath);
        }
    }

    private static void CaptureCreatureSpawnerProvenanceIfAvailable(CreatureSpawner creatureSpawner)
    {
        if (creatureSpawner == null || ProvenanceRegistry.HasCreatureSpawnerProvenance(creatureSpawner))
        {
            return;
        }

        if (TryResolveSpawnerProvenance(creatureSpawner.gameObject, out Transform? rootTransform, out string locationPrefab, out string relativePath))
        {
            RecordCreatureSpawnerProvenance(creatureSpawner, rootTransform, locationPrefab, relativePath);
        }
    }

    private static bool TryResolveSpawnerProvenance(GameObject gameObject, out Transform? rootTransform, out string locationPrefab, out string relativePath)
    {
        rootTransform = null;
        locationPrefab = "";
        relativePath = "";
        if (gameObject == null)
        {
            return false;
        }

        if (TryFindRecordedLocationRoot(gameObject.transform, out rootTransform, out locationPrefab))
        {
            relativePath = GetRelativePath(rootTransform!, gameObject.transform);
            return locationPrefab.Length > 0;
        }

        if (TryGetActiveLocationSpawnContextPrefab(out locationPrefab))
        {
            rootTransform = GetRootTransform(gameObject.transform);
            return locationPrefab.Length > 0;
        }

        if (TryGetLiveLocationProxyContext(gameObject, out locationPrefab, out relativePath))
        {
            rootTransform = GetLocationContextRootTransform(gameObject);
            return relativePath.Length > 0;
        }

        if (TryGetClonedZoneLocationContext(gameObject, out locationPrefab))
        {
            rootTransform = GetRootTransform(gameObject.transform);
            return locationPrefab.Length > 0;
        }

        if (TryGetPersistedSpawnerLocationContext(gameObject, out locationPrefab, out relativePath))
        {
            rootTransform = GetRootTransform(gameObject.transform);
            return locationPrefab.Length > 0;
        }

        if (TryGetDungeonGeneratorLocationContext(gameObject, out locationPrefab))
        {
            rootTransform = GetRootTransform(gameObject.transform);
            return locationPrefab.Length > 0;
        }

        Location? location = gameObject.GetComponentInParent<Location>(true);
        if (location != null && TryGetLocationPrefabName(location, out locationPrefab))
        {
            rootTransform = location.transform;
            relativePath = GetRelativePath(location.transform, gameObject.transform);
            return locationPrefab.Length > 0;
        }

        if (TryPromoteZoneContextToRecordedProvenance(gameObject, out locationPrefab, out relativePath))
        {
            rootTransform = GetRootTransform(gameObject.transform);
            return locationPrefab.Length > 0;
        }

        if (TryPromoteSpatialContextToRecordedProvenance(gameObject, out locationPrefab, out relativePath))
        {
            rootTransform = GetRootTransform(gameObject.transform);
            return locationPrefab.Length > 0;
        }

        return false;
    }

    private static bool TryFindRecordedLocationRoot(Transform transform, out Transform? rootTransform, out string locationPrefab)
    {
        return ProvenanceRegistry.TryFindRecordedLocationRoot(transform, out rootTransform, out locationPrefab);
    }

    private static Transform? GetLocationContextRootTransform(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return null;
        }

        LocationProxy? proxy = gameObject.GetComponentInParent<LocationProxy>(true);
        if (proxy != null)
        {
            return GetLocationProxySpawnedRootTransform(proxy.transform, gameObject.transform);
        }

        Location? location = gameObject.GetComponentInParent<Location>(true);
        return location?.transform;
    }

    private static int AllocateLocationProvenanceEpoch()
    {
        return ProvenanceRegistry.AllocateLocationProvenanceEpoch();
    }

    private static void RecordSpawnAreaProvenance(SpawnArea? spawnArea, Transform? rootTransform, string locationPrefab, string? relativePath = null)
    {
        if (spawnArea == null || string.IsNullOrWhiteSpace(locationPrefab))
        {
            return;
        }

        string resolvedRelativePath = relativePath ?? (rootTransform != null ? GetRelativePath(rootTransform, spawnArea.transform) : "");

        ProvenanceRegistry.RecordSpawnAreaProvenance(spawnArea, new SpawnerLocationProvenance
        {
            Epoch = AllocateLocationProvenanceEpoch(),
            LocationPrefab = locationPrefab,
            RelativePath = resolvedRelativePath
        });
        StorePersistedSpawnerLocationContext(spawnArea, locationPrefab);
        SelectorCacheStore.RemoveSpawnAreaEntryCache(spawnArea);
        if (LiveRegistryStore.TryGetTrackedPrefabName(spawnArea, out _))
        {
            RefreshSpawnAreaLocationBucketMembership(spawnArea);
        }
    }

    private static void RecordCreatureSpawnerProvenance(CreatureSpawner? creatureSpawner, Transform? rootTransform, string locationPrefab, string? relativePath = null)
    {
        if (creatureSpawner == null || string.IsNullOrWhiteSpace(locationPrefab))
        {
            return;
        }

        string resolvedRelativePath = relativePath ?? (rootTransform != null ? GetRelativePath(rootTransform, creatureSpawner.transform) : "");

        ProvenanceRegistry.RecordCreatureSpawnerProvenance(creatureSpawner, new SpawnerLocationProvenance
        {
            Epoch = AllocateLocationProvenanceEpoch(),
            LocationPrefab = locationPrefab,
            RelativePath = resolvedRelativePath
        });
        StorePersistedSpawnerLocationContext(creatureSpawner, locationPrefab);
        SelectorCacheStore.RemoveCreatureSpawnerEntryCache(creatureSpawner);
        if (LiveRegistryStore.TryGetTrackedPrefabName(creatureSpawner, out _))
        {
            RefreshCreatureSpawnerLocationBucketMembership(creatureSpawner);
        }
    }

    private static void StorePersistedSpawnerLocationContext(Component? component, string locationPrefab)
    {
        if (component == null ||
            string.IsNullOrWhiteSpace(locationPrefab) ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer())
        {
            return;
        }

        ZDO? zdo = component.GetComponent<ZNetView>()?.GetZDO();
        zdo?.Set(DropNSpawnLocationPrefabHash, locationPrefab);
    }

    private static bool TryGetActiveLocationSpawnContextPrefab(out string locationPrefab)
    {
        return ProvenanceRegistry.TryGetActiveLocationSpawnContextPrefab(out locationPrefab);
    }

    private static bool TryPromoteSpatialContextToRecordedProvenance(GameObject gameObject, out string locationPrefab, out string relativePath)
    {
        locationPrefab = "";
        relativePath = "";
        if (gameObject == null || !TryGetSpatialLocationContext(gameObject, out locationPrefab))
        {
            return false;
        }

        if (gameObject.TryGetComponent<SpawnArea>(out SpawnArea? spawnArea))
        {
            RecordSpawnAreaProvenance(spawnArea, GetRootTransform(gameObject.transform), locationPrefab, relativePath);
            return true;
        }

        if (gameObject.TryGetComponent<CreatureSpawner>(out CreatureSpawner? creatureSpawner))
        {
            RecordCreatureSpawnerProvenance(creatureSpawner, GetRootTransform(gameObject.transform), locationPrefab, relativePath);
            return true;
        }

        return false;
    }

    private static bool TryPromoteZoneContextToRecordedProvenance(GameObject gameObject, out string locationPrefab, out string relativePath)
    {
        locationPrefab = "";
        relativePath = "";
        if (gameObject == null || !TryGetZoneLocationContext(gameObject, out locationPrefab))
        {
            return false;
        }

        if (gameObject.TryGetComponent<SpawnArea>(out SpawnArea? spawnArea))
        {
            RecordSpawnAreaProvenance(spawnArea, GetRootTransform(gameObject.transform), locationPrefab, relativePath);
            return true;
        }

        if (gameObject.TryGetComponent<CreatureSpawner>(out CreatureSpawner? creatureSpawner))
        {
            RecordCreatureSpawnerProvenance(creatureSpawner, GetRootTransform(gameObject.transform), locationPrefab, relativePath);
            return true;
        }

        return false;
    }

    private static bool TryGetLocationPrefabName(Location location, out string prefabName)
    {
        prefabName = "";
        if (location == null)
        {
            return false;
        }

        if (TryGetZoneLocationPrefabName(location.transform.position, out prefabName))
        {
            return true;
        }

        prefabName = TrimCloneSuffix(location.gameObject.name).Trim();
        return prefabName.Length > 0;
    }

    private static string GetZoneLocationPrefabName(ZoneSystem.ZoneLocation? location)
    {
        return (location?.m_prefabName ?? location?.m_prefab.Name ?? "").Trim();
    }

    private static string GetLocationSpawnContextPrefabName(ZoneSystem.ZoneLocation? location)
    {
        string locationPrefab = GetZoneLocationPrefabName(location);
        if (!locationPrefab.Contains(':') &&
            TryGetExpandWorldDataCurrentLocationPrefabName(out string currentLocationPrefab) &&
            currentLocationPrefab.Contains(':') &&
            string.Equals(GetExpandWorldDataBaseLocationName(currentLocationPrefab), locationPrefab, StringComparison.OrdinalIgnoreCase))
        {
            return currentLocationPrefab;
        }

        return locationPrefab;
    }

    private static bool TryGetExpandWorldDataCurrentLocationPrefabName(out string locationPrefab)
    {
        locationPrefab = "";
        if (!_expandWorldDataCurrentLocationFieldResolved)
        {
            Type? locationSpawningType = SafeTypeLookup.FindLoadedType("ExpandWorldData.LocationSpawning", "ExpandWorldData");
            _expandWorldDataCurrentLocationField = locationSpawningType?.GetField("CurrentLocation", BindingFlags.Public | BindingFlags.Static);
            _expandWorldDataCurrentLocationFieldResolved = true;
        }

        try
        {
            if (_expandWorldDataCurrentLocationField?.GetValue(null) is not ZoneSystem.ZoneLocation currentLocation)
            {
                return false;
            }

            locationPrefab = GetZoneLocationPrefabName(currentLocation);
            return locationPrefab.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetZoneLocationPrefabName(int locationHash, out string prefabName)
    {
        prefabName = "";
        if (locationHash == 0 || ZoneSystem.instance == null)
        {
            return false;
        }

        foreach (ZoneSystem.ZoneLocation zoneLocation in ZoneSystem.instance.m_locations)
        {
            string candidate = GetZoneLocationPrefabName(zoneLocation);
            if (candidate.Length == 0 || candidate.GetStableHashCode() != locationHash)
            {
                continue;
            }

            prefabName = candidate;
            return true;
        }

        return false;
    }

    private static bool TryGetZoneLocationPrefabName(Vector3 position, out string prefabName)
    {
        prefabName = "";
        if (ZoneSystem.instance == null)
        {
            return false;
        }

        Vector2i zone = ZoneSystem.GetZone(position);
        if (!ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance locationInstance))
        {
            return false;
        }

        float radius = Mathf.Max(locationInstance.m_location.m_exteriorRadius, locationInstance.m_location.m_interiorRadius);
        if (radius > 0f && Utils.DistanceXZ(locationInstance.m_position, position) > radius)
        {
            return false;
        }

        string candidate = GetZoneLocationPrefabName(locationInstance.m_location);
        if (candidate.Length == 0)
        {
            return false;
        }

        prefabName = candidate;
        return true;
    }


    private static string GetExpandWorldDataBaseLocationName(string? locationPrefab)
    {
        string normalized = (locationPrefab ?? "").Trim();
        int separatorIndex = normalized.IndexOf(':');
        return separatorIndex > 0 ? normalized[..separatorIndex].Trim() : normalized;
    }

    private static string TryGetRelativePathIfDescendant(Transform root, Transform target)
    {
        if (root == null || target == null)
        {
            return "";
        }

        if (ReferenceEquals(root, target) || target.IsChildOf(root))
        {
            return GetRelativePath(root, target);
        }

        return "";
    }

    private static Transform GetRootTransform(Transform transform)
    {
        Transform current = transform;
        while (current.parent != null)
        {
            current = current.parent;
        }

        return current;
    }
}
