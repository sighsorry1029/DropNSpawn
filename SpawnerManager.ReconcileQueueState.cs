namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static readonly SpawnerReconcileQueue ReconcileQueue = new();

    private sealed class SpawnerReconcileQueue
    {
        private readonly InstanceReconcileQueue<SpawnArea, PendingSpawnAreaReconcile> _pendingSpawnAreaReconciles =
            new(
                (spawnArea, instanceId, epoch) => new PendingSpawnAreaReconcile(spawnArea, instanceId, epoch),
                queuedSpawnArea => queuedSpawnArea.InstanceId,
                queuedSpawnArea => queuedSpawnArea.Epoch,
                queuedSpawnArea => queuedSpawnArea.SpawnArea);

        private readonly InstanceReconcileQueue<CreatureSpawner, PendingCreatureSpawnerReconcile> _pendingCreatureSpawnerReconciles =
            new(
                (creatureSpawner, instanceId, epoch) => new PendingCreatureSpawnerReconcile(creatureSpawner, instanceId, epoch),
                queuedCreatureSpawner => queuedCreatureSpawner.InstanceId,
                queuedCreatureSpawner => queuedCreatureSpawner.Epoch,
                queuedCreatureSpawner => queuedCreatureSpawner.CreatureSpawner);

        public bool TryQueue(SpawnArea? spawnArea, int epoch)
        {
            return _pendingSpawnAreaReconciles.TryQueue(spawnArea, epoch);
        }

        public bool TryQueue(CreatureSpawner? creatureSpawner, int epoch)
        {
            return _pendingCreatureSpawnerReconciles.TryQueue(creatureSpawner, epoch);
        }

        public bool HasPendingWork()
        {
            return _pendingSpawnAreaReconciles.HasPendingWork ||
                   _pendingCreatureSpawnerReconciles.HasPendingWork;
        }

        public int GetPendingWorkCount()
        {
            return _pendingSpawnAreaReconciles.Count + _pendingCreatureSpawnerReconciles.Count;
        }

        public bool TryDequeueNextSpawnArea(int epoch, out SpawnArea? spawnArea)
        {
            return _pendingSpawnAreaReconciles.TryDequeueCurrent(epoch, out spawnArea, out _);
        }

        public bool TryDequeueNextCreatureSpawner(int epoch, out CreatureSpawner? creatureSpawner)
        {
            return _pendingCreatureSpawnerReconciles.TryDequeueCurrent(epoch, out creatureSpawner, out _);
        }

        public void Clear()
        {
            _pendingSpawnAreaReconciles.Clear();
            _pendingCreatureSpawnerReconciles.Clear();
        }
    }
}
