using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal sealed class InstanceReconcileQueue<TInstance, TQueuedItem>
    where TInstance : UnityEngine.Object
{
    private readonly RingBufferQueue<TQueuedItem> _pendingItems = new();
    private readonly HashSet<int> _pendingInstanceIds = new();
    private readonly Func<TInstance, int, int, TQueuedItem> _createQueuedItem;
    private readonly Func<TQueuedItem, int> _getInstanceId;
    private readonly Func<TQueuedItem, int> _getEpoch;
    private readonly Func<TQueuedItem, TInstance?> _getInstance;

    public InstanceReconcileQueue(
        Func<TInstance, int, int, TQueuedItem> createQueuedItem,
        Func<TQueuedItem, int> getInstanceId,
        Func<TQueuedItem, int> getEpoch,
        Func<TQueuedItem, TInstance?> getInstance)
    {
        _createQueuedItem = createQueuedItem;
        _getInstanceId = getInstanceId;
        _getEpoch = getEpoch;
        _getInstance = getInstance;
    }

    public bool HasPendingWork => _pendingItems.Count > 0;

    public bool TryQueue(TInstance? instance, int epoch)
    {
        if (instance == null)
        {
            return false;
        }

        int instanceId = instance.GetInstanceID();
        if (!_pendingInstanceIds.Add(instanceId))
        {
            return false;
        }

        _pendingItems.Enqueue(_createQueuedItem(instance, instanceId, epoch));
        return true;
    }

    public bool TryDequeueCurrent(int epoch, out TInstance? instance, out int instanceId)
    {
        instance = null;
        instanceId = 0;
        while (_pendingItems.Count > 0)
        {
            if (!_pendingItems.TryDequeue(out TQueuedItem queuedItem))
            {
                continue;
            }

            instanceId = _getInstanceId(queuedItem);
            _pendingInstanceIds.Remove(instanceId);
            if (_getEpoch(queuedItem) != epoch)
            {
                continue;
            }

            instance = _getInstance(queuedItem);
            if (instance == null)
            {
                continue;
            }

            return true;
        }

        instanceId = 0;
        return false;
    }

    public void Clear()
    {
        _pendingItems.Clear();
        _pendingInstanceIds.Clear();
    }
}
