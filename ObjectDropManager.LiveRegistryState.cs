using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static readonly ObjectLiveRegistryState LiveRegistryState = new();

    private sealed class ObjectLiveRegistryState
    {
        private readonly Dictionary<string, HashSet<GameObject>> _liveObjectsByPrefab = new(System.StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<GameObject, string> _liveObjectPrefabsByInstance = new();
        private readonly HashSet<string> _bootstrappedTrackedPrefabs = new(System.StringComparer.OrdinalIgnoreCase);
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
            HashSet<string> unbootstrappedPrefabs = new(System.StringComparer.OrdinalIgnoreCase);
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
                if (string.Equals(previousPrefabName, prefabName, System.StringComparison.OrdinalIgnoreCase))
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
}
