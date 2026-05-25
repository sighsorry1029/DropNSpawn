using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    internal static void QueueSpawnAreaReconcile(SpawnArea? spawnArea)
    {
        lock (Sync)
        {
            if (spawnArea == null || spawnArea.gameObject == null)
            {
                return;
            }

            _ = ReconcileQueue.TryQueue(spawnArea, _reconcileQueueEpoch);
        }
    }

    internal static void QueueCreatureSpawnerReconcile(CreatureSpawner? creatureSpawner)
    {
        lock (Sync)
        {
            if (creatureSpawner == null || creatureSpawner.gameObject == null)
            {
                return;
            }

            _ = ReconcileQueue.TryQueue(creatureSpawner, _reconcileQueueEpoch);
        }
    }

    internal static bool HasPendingReconcileWork()
    {
        lock (Sync)
        {
            return ProvenanceRegistry.HasPendingRootScans() ||
                   ReconcileQueue.HasPendingWork();
        }
    }

    internal static bool ProcessQueuedReconcileStep(float deadline)
    {
        lock (Sync)
        {
            if (Time.realtimeSinceStartup >= deadline)
            {
                return false;
            }

            if (ShouldBlockClientSpawnerUpdate() ||
                !IsGameDataReady() ||
                DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Spawner))
            {
                return false;
            }

            if (ProcessQueuedLocationRootProvenanceStep(deadline))
            {
                return true;
            }

            while (ReconcileQueue.TryDequeueNextSpawnArea(_reconcileQueueEpoch, out SpawnArea? spawnArea))
            {
                ReconcileSpawnAreaInstanceCore(spawnArea);
                return true;
            }

            while (ReconcileQueue.TryDequeueNextCreatureSpawner(_reconcileQueueEpoch, out CreatureSpawner? creatureSpawner))
            {
                ReconcileCreatureSpawnerInstanceCore(creatureSpawner);
                return true;
            }
        }

        return false;
    }

    private static void ClearQueuedReconcileState()
    {
        _reconcileQueueEpoch++;
        ProvenanceRegistry.ClearPendingRootScans();
        ReconcileQueue.Clear();
    }
}
