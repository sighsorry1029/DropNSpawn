using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static readonly ObjectReconcileQueueState ReconcileQueueState = new();

    private sealed class ObjectReconcileQueueState
    {
        private readonly RingBufferQueue<PendingObjectReconcileGroup> _pendingHighPriorityObjectReconcileGroups = new();
        private readonly RingBufferQueue<PendingObjectReconcileGroup> _pendingLowPriorityObjectReconcileGroups = new();
        private readonly Dictionary<string, PendingObjectReconcileGroupState> _pendingObjectReconcileGroups = new(StringComparer.Ordinal);
        private readonly HashSet<int> _pendingObjectReconcileInstanceIds = new();

        public bool HasPendingWork()
        {
            return _pendingHighPriorityObjectReconcileGroups.Count > 0 ||
                   _pendingLowPriorityObjectReconcileGroups.Count > 0;
        }

        public bool HasPendingGroups(bool highPriorityOnly)
        {
            return _pendingHighPriorityObjectReconcileGroups.Count > 0 ||
                   (!highPriorityOnly && _pendingLowPriorityObjectReconcileGroups.Count > 0);
        }

        public bool TryGetGroupState(string groupKey, out PendingObjectReconcileGroupState groupState)
        {
            return _pendingObjectReconcileGroups.TryGetValue(groupKey, out groupState);
        }

        public void RemoveGroup(string groupKey)
        {
            _pendingObjectReconcileGroups.Remove(groupKey);
        }

        public IEnumerable<KeyValuePair<string, PendingObjectReconcileGroupState>> EnumerateGroups()
        {
            return _pendingObjectReconcileGroups;
        }

        public void EnqueueGroup(PendingObjectReconcileGroup queuedGroup, bool highPriority)
        {
            if (highPriority)
            {
                _pendingHighPriorityObjectReconcileGroups.Enqueue(queuedGroup);
            }
            else
            {
                _pendingLowPriorityObjectReconcileGroups.Enqueue(queuedGroup);
            }
        }

        public bool TryDequeueGroup(out PendingObjectReconcileGroup queuedGroup, bool highPriorityOnly = false)
        {
            if (_pendingHighPriorityObjectReconcileGroups.TryDequeue(out queuedGroup))
            {
                return true;
            }

            if (highPriorityOnly)
            {
                return false;
            }

            return _pendingLowPriorityObjectReconcileGroups.TryDequeue(out queuedGroup);
        }

        public bool TryAddPendingInstance(int instanceId)
        {
            return _pendingObjectReconcileInstanceIds.Add(instanceId);
        }

        public bool TryTakePendingInstance(int instanceId)
        {
            return _pendingObjectReconcileInstanceIds.Remove(instanceId);
        }

        public void RemovePendingState(int instanceId)
        {
            _pendingObjectReconcileInstanceIds.Remove(instanceId);
        }

        public PendingObjectReconcileGroupState GetOrCreateGroup(
            string groupKey,
            LiveObjectComponentKind configuredKinds,
            bool highPriority)
        {
            if (_pendingObjectReconcileGroups.TryGetValue(groupKey, out PendingObjectReconcileGroupState? groupState))
            {
                return groupState;
            }

            groupState = new PendingObjectReconcileGroupState
            {
                ComponentKinds = configuredKinds,
                HighPriority = highPriority
            };
            _pendingObjectReconcileGroups[groupKey] = groupState;
            return groupState;
        }

        public void Clear()
        {
            _pendingHighPriorityObjectReconcileGroups.Clear();
            _pendingLowPriorityObjectReconcileGroups.Clear();
            _pendingObjectReconcileGroups.Clear();
            _pendingObjectReconcileInstanceIds.Clear();
        }
    }

    internal static void QueueObjectInstanceReconcile(GameObject? gameObject, LiveObjectComponentKind sourceKind)
    {
        lock (Sync)
        {
            if (gameObject == null || !ShouldReconcileLocally(gameObject))
            {
                return;
            }

            string prefabName = GetPrefabName(gameObject);
            if (prefabName.Length == 0)
            {
                return;
            }

            if (!ShouldQueueAwakeReconcileForPrefab(prefabName, sourceKind))
            {
                return;
            }

            if (!TryTrackLiveObjectInstanceLocked(gameObject, out prefabName))
            {
                return;
            }

            QueueTrackedObjectInstanceReconcileLocked(gameObject, prefabName);
        }
    }

    internal static bool HasPendingReconcileWork()
    {
        lock (Sync)
        {
            return HasPendingReconcileWorkLocked();
        }
    }

    private static bool HasPendingReconcileWorkLocked()
    {
        return ReconcileQueueState.HasPendingWork();
    }

    internal static bool ProcessQueuedReconcileStep(double deadline)
    {
        lock (Sync)
        {
            return TryProcessQueuedReconcileWorkLocked(deadline);
        }
    }

    private static bool TryProcessQueuedReconcileWorkLocked(double deadline)
    {
        return TryProcessNextQueuedReconcileItemLocked(deadline, highPriorityOnly: false);
    }

    private static void DrainQueuedHighPriorityReconcilesLocked()
    {
        while (TryProcessNextQueuedReconcileItemLocked(double.MaxValue, highPriorityOnly: true))
        {
        }
    }

    private static bool TryProcessNextQueuedReconcileItemLocked(double deadline, bool highPriorityOnly)
    {
        while (ReconcileQueueState.HasPendingGroups(highPriorityOnly))
        {
            if (!highPriorityOnly && Time.realtimeSinceStartupAsDouble >= deadline)
            {
                return false;
            }

            if (!IsGameDataReady() || DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Object))
            {
                return false;
            }

            if (!TryDequeuePendingObjectReconcileGroup(out PendingObjectReconcileGroup queuedGroup, highPriorityOnly) ||
                !ReconcileQueueState.TryGetGroupState(queuedGroup.GroupKey, out PendingObjectReconcileGroupState groupState))
            {
                continue;
            }

            groupState.IsQueued = false;
            if (queuedGroup.Epoch != _reconcileQueueEpoch)
            {
                continue;
            }

            while (groupState.Items.Count > 0)
            {
                if (!groupState.Items.TryDequeue(out PendingObjectReconcileItem queuedItem))
                {
                    continue;
                }

                groupState.InstanceIds.Remove(queuedItem.InstanceId);
                if (queuedItem.GameObject == null)
                {
                    ReconcileQueueState.RemovePendingState(queuedItem.InstanceId);
                    continue;
                }

                if (!ReconcileQueueState.TryTakePendingInstance(queuedItem.InstanceId))
                {
                    continue;
                }

                ReconcileObjectInstanceCore(queuedItem.GameObject);
                if (groupState.Items.Count > 0)
                {
                    EnqueueObjectReconcileGroup(queuedGroup.GroupKey, groupState);
                }
                else
                {
                    ReconcileQueueState.RemoveGroup(queuedGroup.GroupKey);
                }

                return true;
            }

            ReconcileQueueState.RemoveGroup(queuedGroup.GroupKey);
        }

        return false;
    }

    private static bool ShouldQueueAwakeReconcileForPrefab(string prefabName, LiveObjectComponentKind sourceKind)
    {
        if (prefabName.Length == 0)
        {
            return false;
        }

        if (sourceKind == LiveObjectComponentKind.Piece)
        {
            return RequiresLiveReconcileForPrefab(prefabName);
        }

        return PrefabProfileCatalogState.TryGetReconcileKinds(prefabName, out LiveObjectComponentKind reconcileKinds) &&
               (reconcileKinds & sourceKind) != 0;
    }

    private static string BuildObjectReconcileGroupKey(GameObject gameObject, string prefabName)
    {
        Transform root = gameObject.transform.root;
        if (root != null && root.gameObject != null && root.gameObject != gameObject)
        {
            return $"root:{root.gameObject.GetInstanceID()}:{prefabName}";
        }

        Vector2i zone = ZoneSystem.GetZone(gameObject.transform.position);
        return $"zone:{zone.x}:{zone.y}:{prefabName}";
    }

    private static bool IsHighPriorityPrefab(string prefabName)
    {
        if (!PrefabProfileCatalogState.TryGetReconcileKinds(prefabName, out LiveObjectComponentKind configuredKinds))
        {
            return false;
        }

        LiveObjectComponentKind interactivePriorityKinds = LiveObjectComponentKind.Pickable |
                                                           LiveObjectComponentKind.PickableItem |
                                                           LiveObjectComponentKind.Fish;
        return (configuredKinds & interactivePriorityKinds) != 0;
    }

    private static void PromoteQueuedReconcileGroupsLocked(LiveObjectComponentKind componentKinds)
    {
        foreach ((string groupKey, PendingObjectReconcileGroupState groupState) in ReconcileQueueState.EnumerateGroups())
        {
            if (!groupState.IsQueued ||
                groupState.HighPriority ||
                (groupState.ComponentKinds & componentKinds) == 0)
            {
                continue;
            }

            groupState.HighPriority = true;
            ReconcileQueueState.EnqueueGroup(new PendingObjectReconcileGroup(groupKey, _reconcileQueueEpoch), highPriority: true);
        }
    }

    private static void EnqueueObjectReconcileGroup(string groupKey, PendingObjectReconcileGroupState groupState)
    {
        if (groupState.IsQueued)
        {
            return;
        }

        groupState.IsQueued = true;
        PendingObjectReconcileGroup queuedGroup = new(groupKey, _reconcileQueueEpoch);
        ReconcileQueueState.EnqueueGroup(queuedGroup, groupState.HighPriority);
    }

    private static bool TryDequeuePendingObjectReconcileGroup(out PendingObjectReconcileGroup queuedGroup, bool highPriorityOnly = false)
    {
        return ReconcileQueueState.TryDequeueGroup(out queuedGroup, highPriorityOnly);
    }

    private static void QueueTrackedObjectInstanceReconcileLocked(
        GameObject gameObject,
        string prefabName)
    {
        int instanceId = gameObject.GetInstanceID();
        if (!ReconcileQueueState.TryAddPendingInstance(instanceId))
        {
            return;
        }

        string groupKey = BuildObjectReconcileGroupKey(gameObject, prefabName);
        PrefabProfileCatalogState.TryGetReconcileKinds(prefabName, out LiveObjectComponentKind configuredKinds);
        PendingObjectReconcileGroupState groupState = ReconcileQueueState.GetOrCreateGroup(groupKey, configuredKinds, IsHighPriorityPrefab(prefabName));

        if (!groupState.InstanceIds.Add(instanceId))
        {
            return;
        }

        groupState.Items.Enqueue(new PendingObjectReconcileItem(gameObject, instanceId));
        EnqueueObjectReconcileGroup(groupKey, groupState);
    }

    private static void RemovePendingObjectReconcileState(int instanceId)
    {
        ReconcileQueueState.RemovePendingState(instanceId);
    }

    private static void ClearQueuedReconcileState()
    {
        _reconcileQueueEpoch++;
        ReconcileQueueState.Clear();
        ClearLiveObjectConditionCaches();
    }
}
