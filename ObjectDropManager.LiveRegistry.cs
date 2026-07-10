using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static readonly ObjectLiveRegistryState LiveRegistryState = new();

    private sealed class ObjectLiveRegistryState
    {
        private readonly Dictionary<string, HashSet<GameObject>> _liveObjectsByPrefab = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<GameObject, string> _liveObjectPrefabsByInstance = new();
        private readonly HashSet<string> _bootstrappedTrackedPrefabs = new(StringComparer.OrdinalIgnoreCase);
        private int? _liveObjectRegistrySceneInstanceId;

        public bool HasSceneSession(int currentSceneInstanceId)
        {
            return _liveObjectRegistrySceneInstanceId == currentSceneInstanceId;
        }

        public void BeginSceneSession(int currentSceneInstanceId)
        {
            _liveObjectRegistrySceneInstanceId = currentSceneInstanceId;
            ClearTracked();
        }

        public HashSet<string> CollectUnbootstrappedTrackedPrefabs(IEnumerable<string> prefabNames)
        {
            HashSet<string> unbootstrappedPrefabs = new(StringComparer.OrdinalIgnoreCase);
            foreach (string prefabName in prefabNames)
            {
                if (!string.IsNullOrWhiteSpace(prefabName) &&
                    !_bootstrappedTrackedPrefabs.Contains(prefabName))
                {
                    unbootstrappedPrefabs.Add(prefabName);
                }
            }

            return unbootstrappedPrefabs;
        }

        public void MarkTrackedPrefabsBootstrapped(IEnumerable<string> prefabNames)
        {
            foreach (string prefabName in prefabNames)
            {
                if (!string.IsNullOrWhiteSpace(prefabName))
                {
                    _bootstrappedTrackedPrefabs.Add(prefabName);
                }
            }
        }

        public IEnumerable<GameObject> EnumerateRegisteredLiveObjects(HashSet<string> prefabNames)
        {
            foreach (string prefabName in prefabNames)
            {
                if (!_liveObjectsByPrefab.TryGetValue(prefabName, out HashSet<GameObject>? liveObjects))
                {
                    continue;
                }

                foreach (GameObject liveObject in liveObjects)
                {
                    if (liveObject != null)
                    {
                        yield return liveObject;
                    }
                }
            }
        }

        public bool TryGetTrackedPrefab(GameObject gameObject, out string prefabName)
        {
            return _liveObjectPrefabsByInstance.TryGetValue(gameObject, out prefabName);
        }

        public void Register(GameObject gameObject, string prefabName)
        {
            if (_liveObjectPrefabsByInstance.TryGetValue(gameObject, out string? previousPrefabName))
            {
                if (string.Equals(previousPrefabName, prefabName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Unregister(gameObject, previousPrefabName);
            }

            _liveObjectPrefabsByInstance[gameObject] = prefabName;
            if (!_liveObjectsByPrefab.TryGetValue(prefabName, out HashSet<GameObject>? objects))
            {
                objects = new HashSet<GameObject>();
                _liveObjectsByPrefab[prefabName] = objects;
            }

            objects.Add(gameObject);
        }

        public void Unregister(GameObject gameObject, string prefabName)
        {
            _liveObjectPrefabsByInstance.Remove(gameObject);
            if (!_liveObjectsByPrefab.TryGetValue(prefabName, out HashSet<GameObject>? objects))
            {
                return;
            }

            objects.Remove(gameObject);
            if (objects.Count == 0)
            {
                _liveObjectsByPrefab.Remove(prefabName);
            }
        }

        public void CollectDeadObjects(List<KeyValuePair<GameObject, string>> target)
        {
            target.Clear();
            foreach (KeyValuePair<GameObject, string> pair in _liveObjectPrefabsByInstance)
            {
                if (pair.Key == null)
                {
                    target.Add(pair);
                }
            }
        }

        public void ClearTracked()
        {
            _liveObjectsByPrefab.Clear();
            _liveObjectPrefabsByInstance.Clear();
            _bootstrappedTrackedPrefabs.Clear();
        }
    }

    internal static void TrackObjectInstance(GameObject? gameObject)
    {
        lock (Sync)
        {
            TryTrackLiveObjectInstanceLocked(gameObject, out _);
        }
    }

    internal static void UntrackObjectInstance(GameObject? gameObject)
    {
        lock (Sync)
        {
            UntrackLiveObjectInstanceLocked(gameObject);
        }
    }

    private static IEnumerable<GameObject> GetRegisteredLiveObjects()
    {
        EnsureLiveObjectRegistrySessionLocked();
        HashSet<string> trackedPrefabs = SnapshotPrefabsRequiringLiveTracking();
        BootstrapLiveObjectRegistryIfNeededLocked(trackedPrefabs);
        CleanupRegisteredLiveObjects();
        return LiveRegistryState.EnumerateRegisteredLiveObjects(trackedPrefabs);
    }

    private static IEnumerable<GameObject> GetRegisteredLiveObjects(HashSet<string> dirtyPrefabs)
    {
        EnsureLiveObjectRegistrySessionLocked();
        HashSet<string> trackedPrefabs = FilterPrefabsRequiringLiveTracking(dirtyPrefabs);
        BootstrapLiveObjectRegistryIfNeededLocked(trackedPrefabs);
        CleanupRegisteredLiveObjects();
        return LiveRegistryState.EnumerateRegisteredLiveObjects(trackedPrefabs);
    }

    private static void EnsureLiveObjectRegistrySessionLocked()
    {
        int currentSceneInstanceId = ZNetScene.instance != null ? ZNetScene.instance.GetInstanceID() : 0;
        if (LiveRegistryState.HasSceneSession(currentSceneInstanceId))
        {
            return;
        }

        LiveRegistryState.BeginSceneSession(currentSceneInstanceId);
        ClearLiveObjectConditionCaches();
    }

    private static void BootstrapLiveObjectRegistryIfNeededLocked(HashSet<string> targetPrefabs)
    {
        if (targetPrefabs == null || targetPrefabs.Count == 0)
        {
            return;
        }

        HashSet<string> missingPrefabs = LiveRegistryState.CollectUnbootstrappedTrackedPrefabs(targetPrefabs);
        if (missingPrefabs.Count == 0)
        {
            return;
        }

        VisitActiveLoadedSceneGameObjects(gameObject =>
        {
            if (!HasRelevantLiveObjectComponents(gameObject))
            {
                return;
            }

            string prefabName = GetPrefabName(gameObject);
            if (prefabName.Length == 0 ||
                !missingPrefabs.Contains(prefabName))
            {
                return;
            }

            RegisterLiveObject(gameObject, prefabName);
        });

        LiveRegistryState.MarkTrackedPrefabsBootstrapped(missingPrefabs);
    }

    private static bool TryTrackLiveObjectInstanceLocked(GameObject? gameObject, out string prefabName)
    {
        EnsureLiveObjectRegistrySessionLocked();
        prefabName = "";
        if (gameObject == null)
        {
            return false;
        }

        if (LiveRegistryState.TryGetTrackedPrefab(gameObject, out string trackedPrefabName) &&
            !string.IsNullOrWhiteSpace(trackedPrefabName))
        {
            prefabName = trackedPrefabName;
            return true;
        }

        prefabName = GetPrefabName(gameObject);
        if (prefabName.Length == 0)
        {
            return false;
        }

        if (!RequiresLiveTrackingForPrefab(prefabName))
        {
            return true;
        }

        RegisterLiveObject(gameObject, prefabName);
        return true;
    }

    private static void UntrackLiveObjectInstanceLocked(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return;
        }

        int instanceId = gameObject.GetInstanceID();
        RemovePendingObjectReconcileState(instanceId);
        RemovePendingObjectConditionPlanState(instanceId);
        if (LiveRegistryState.TryGetTrackedPrefab(gameObject, out string prefabName) &&
            !string.IsNullOrWhiteSpace(prefabName))
        {
            UnregisterLiveObject(gameObject, prefabName);
        }
    }

    private static void RegisterLiveObject(GameObject gameObject, string prefabName)
    {
        if (gameObject == null ||
            prefabName.Length == 0 ||
            !RequiresLiveTrackingForPrefab(prefabName))
        {
            return;
        }

        LiveRegistryState.Register(gameObject, prefabName);
    }

    private static void CleanupRegisteredLiveObjects()
    {
        List<KeyValuePair<GameObject, string>> deadObjects = new();
        LiveRegistryState.CollectDeadObjects(deadObjects);
        if (deadObjects.Count == 0)
        {
            return;
        }

        foreach ((GameObject deadObject, string prefabName) in deadObjects)
        {
            UnregisterLiveObject(deadObject, prefabName);
        }
    }

    private static void UnregisterLiveObject(GameObject gameObject, string prefabName)
    {
        LiveRegistryState.Unregister(gameObject, prefabName);
    }

    private static bool HasRelevantLiveObjectComponents(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return false;
        }

        if (gameObject.TryGetComponent(out DropOnDestroyed _))
        {
            return true;
        }

        if (gameObject.TryGetComponent(out MineRock _))
        {
            return true;
        }

        if (gameObject.TryGetComponent(out MineRock5 _))
        {
            return true;
        }

        if (gameObject.TryGetComponent(out TreeBase _))
        {
            return true;
        }

        if (gameObject.TryGetComponent(out TreeLog _))
        {
            return true;
        }

        if (gameObject.TryGetComponent(out Container _))
        {
            return true;
        }

        if (gameObject.TryGetComponent(out Pickable _))
        {
            return true;
        }

        if (gameObject.TryGetComponent(out PickableItem _))
        {
            return true;
        }

        if (gameObject.TryGetComponent(out Fish _))
        {
            return true;
        }

        return gameObject.TryGetComponent(out Destructible _);
    }

    // Traverse loaded scene hierarchies once so bootstrap work scales with scene size, not component-kind count.
    private static void VisitActiveLoadedSceneGameObjects(Action<GameObject> visitor)
    {
        SceneTraversalSupport.VisitActiveLoadedSceneGameObjects(visitor);
    }

}
