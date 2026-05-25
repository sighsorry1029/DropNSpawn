using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static readonly SpawnerReconcileQueue ReconcileQueue = new();

    private sealed class SpawnerReconcileQueue
    {
        private readonly RingBufferQueue<PendingSpawnAreaReconcile> _pendingSpawnAreaReconciles = new();
        private readonly HashSet<int> _pendingSpawnAreaReconcileIds = new();
        private readonly RingBufferQueue<PendingCreatureSpawnerReconcile> _pendingCreatureSpawnerReconciles = new();
        private readonly HashSet<int> _pendingCreatureSpawnerReconcileIds = new();

        public bool TryQueue(SpawnArea? spawnArea, int epoch)
        {
            if (spawnArea == null)
            {
                return false;
            }

            int instanceId = spawnArea.GetInstanceID();
            if (!_pendingSpawnAreaReconcileIds.Add(instanceId))
            {
                return false;
            }

            _pendingSpawnAreaReconciles.Enqueue(new PendingSpawnAreaReconcile(spawnArea, instanceId, epoch));
            return true;
        }

        public bool TryQueue(CreatureSpawner? creatureSpawner, int epoch)
        {
            if (creatureSpawner == null)
            {
                return false;
            }

            int instanceId = creatureSpawner.GetInstanceID();
            if (!_pendingCreatureSpawnerReconcileIds.Add(instanceId))
            {
                return false;
            }

            _pendingCreatureSpawnerReconciles.Enqueue(new PendingCreatureSpawnerReconcile(creatureSpawner, instanceId, epoch));
            return true;
        }

        public bool HasPendingWork()
        {
            return _pendingSpawnAreaReconciles.Count > 0 ||
                   _pendingCreatureSpawnerReconciles.Count > 0;
        }

        public bool TryDequeueNextSpawnArea(int epoch, out SpawnArea? spawnArea)
        {
            spawnArea = null;
            while (_pendingSpawnAreaReconciles.Count > 0)
            {
                if (!_pendingSpawnAreaReconciles.TryDequeue(out PendingSpawnAreaReconcile queuedSpawnArea))
                {
                    continue;
                }

                _pendingSpawnAreaReconcileIds.Remove(queuedSpawnArea.InstanceId);
                if (queuedSpawnArea.Epoch != epoch || queuedSpawnArea.SpawnArea == null)
                {
                    continue;
                }

                spawnArea = queuedSpawnArea.SpawnArea;
                return true;
            }

            return false;
        }

        public bool TryDequeueNextCreatureSpawner(int epoch, out CreatureSpawner? creatureSpawner)
        {
            creatureSpawner = null;
            while (_pendingCreatureSpawnerReconciles.Count > 0)
            {
                if (!_pendingCreatureSpawnerReconciles.TryDequeue(out PendingCreatureSpawnerReconcile queuedCreatureSpawner))
                {
                    continue;
                }

                _pendingCreatureSpawnerReconcileIds.Remove(queuedCreatureSpawner.InstanceId);
                if (queuedCreatureSpawner.Epoch != epoch || queuedCreatureSpawner.CreatureSpawner == null)
                {
                    continue;
                }

                creatureSpawner = queuedCreatureSpawner.CreatureSpawner;
                return true;
            }

            return false;
        }

        public void Clear()
        {
            _pendingSpawnAreaReconciles.Clear();
            _pendingSpawnAreaReconcileIds.Clear();
            _pendingCreatureSpawnerReconciles.Clear();
            _pendingCreatureSpawnerReconcileIds.Clear();
        }
    }
}
