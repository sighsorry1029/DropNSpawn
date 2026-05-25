using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static readonly ObjectReconcileQueueState ReconcileQueueState = new();

    private sealed class ObjectReconcileQueueState
    {
        private readonly RingBufferQueue<PendingObjectReconcileGroup> _pendingHighPriorityObjectReconcileGroups = new();
        private readonly RingBufferQueue<PendingObjectReconcileGroup> _pendingLowPriorityObjectReconcileGroups = new();
        private readonly Dictionary<string, PendingObjectReconcileGroupState> _pendingObjectReconcileGroups = new(StringComparer.Ordinal);
        private readonly Dictionary<int, bool> _pendingObjectReconcileClearFlags = new();

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

        public bool TryMergeOrAddClearFlag(int instanceId, bool clearCreatorRestrictedContainerContents)
        {
            if (_pendingObjectReconcileClearFlags.TryGetValue(instanceId, out bool existingClearFlag))
            {
                _pendingObjectReconcileClearFlags[instanceId] = existingClearFlag || clearCreatorRestrictedContainerContents;
                return true;
            }

            _pendingObjectReconcileClearFlags[instanceId] = clearCreatorRestrictedContainerContents;
            return false;
        }

        public bool TryTakeClearFlag(int instanceId, out bool clearCreatorRestrictedContainerContents)
        {
            if (_pendingObjectReconcileClearFlags.TryGetValue(instanceId, out clearCreatorRestrictedContainerContents))
            {
                _pendingObjectReconcileClearFlags.Remove(instanceId);
                return true;
            }

            return false;
        }

        public void RemovePendingState(int instanceId)
        {
            _pendingObjectReconcileClearFlags.Remove(instanceId);
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
            _pendingObjectReconcileClearFlags.Clear();
        }
    }
}
