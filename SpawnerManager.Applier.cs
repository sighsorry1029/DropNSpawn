using System.Collections.Generic;
using System.Linq;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static void ApplyDesiredStateToLiveObjects(SpawnerDesiredState desiredState)
    {
        if (desiredState.ReloadPrefabs.Count == 0)
        {
            return;
        }

        if (desiredState.QueueLiveReconcile)
        {
            ReapplyOrQueueRegisteredLiveObjects(
                desiredState.DomainEnabled,
                desiredState.ReloadPrefabs,
                desiredState.RuntimeConfigurationSnapshot);
        }
        else
        {
            ReapplyRegisteredLiveObjects(
                desiredState.DomainEnabled,
                desiredState.ReloadPrefabs,
                desiredState.RuntimeConfigurationSnapshot);
        }
    }

    private static void ReapplyRegisteredLiveObjects(
        bool domainEnabled,
        HashSet<string> prefabs,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot)
    {
        foreach (SpawnArea spawnArea in GetRegisteredSpawnAreas(prefabs, runtimeConfigurationSnapshot))
        {
            TrackSpawnAreaInstanceInternal(spawnArea);
            if (domainEnabled &&
                TryGetActiveSpawnAreaEntries(
                    spawnArea,
                    runtimeConfigurationSnapshot,
                    out IReadOnlyList<SpawnerRuntimeEntry>? entries,
                    out _))
            {
                ReconcileSpawnAreaInstanceInternal(spawnArea, entries!);
                continue;
            }

            RestoreSpawnAreaInstance(spawnArea);
        }

        foreach (CreatureSpawner creatureSpawner in GetRegisteredCreatureSpawners(prefabs, runtimeConfigurationSnapshot))
        {
            TrackCreatureSpawnerInstanceInternal(creatureSpawner);
            if (domainEnabled &&
                TryGetActiveCreatureSpawnerEntries(
                    creatureSpawner,
                    runtimeConfigurationSnapshot,
                    out IReadOnlyList<SpawnerRuntimeEntry>? entries,
                    out _))
            {
                ReconcileCreatureSpawnerInstanceInternal(creatureSpawner, entries!);
                continue;
            }

            RestoreCreatureSpawnerInstance(creatureSpawner, refreshRuntimeState: true);
        }
    }

    private static void ReapplyOrQueueRegisteredLiveObjects(
        bool domainEnabled,
        HashSet<string> prefabs,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot)
    {
        foreach (SpawnArea spawnArea in GetRegisteredSpawnAreas(prefabs, runtimeConfigurationSnapshot))
        {
            TrackSpawnAreaInstanceInternal(spawnArea);
            if (domainEnabled &&
                TryGetActiveSpawnAreaEntryCache(
                    spawnArea,
                    runtimeConfigurationSnapshot,
                    out MatchingEntryCache? entryCache,
                    out string configPrefabName))
            {
                if (runtimeConfigurationSnapshot.RuntimeConfiguredSpawnAreaPrefabs.Contains(configPrefabName))
                {
                    QueueSpawnAreaReconcile(spawnArea);
                    continue;
                }

                ReconcileSpawnAreaInstanceInternal(
                    spawnArea,
                    entryCache!.Entries,
                    entryCache);
                continue;
            }

            RestoreSpawnAreaInstance(spawnArea);
        }

        foreach (CreatureSpawner creatureSpawner in GetRegisteredCreatureSpawners(prefabs, runtimeConfigurationSnapshot))
        {
            TrackCreatureSpawnerInstanceInternal(creatureSpawner);
            if (domainEnabled &&
                TryGetActiveCreatureSpawnerEntryCache(
                    creatureSpawner,
                    runtimeConfigurationSnapshot,
                    out MatchingEntryCache? entryCache,
                    out string configPrefabName))
            {
                if (runtimeConfigurationSnapshot.RuntimeConfiguredCreatureSpawnerPrefabs.Contains(configPrefabName))
                {
                    QueueCreatureSpawnerReconcile(creatureSpawner);
                    continue;
                }

                ReconcileCreatureSpawnerInstanceInternal(
                    creatureSpawner,
                    entryCache!.Entries,
                    entryCache);
                continue;
            }

            RestoreCreatureSpawnerInstance(creatureSpawner, refreshRuntimeState: true);
        }
    }

    private static bool TryGetActiveSpawnAreaEntries(
        SpawnArea? spawnArea,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot,
        out IReadOnlyList<SpawnerRuntimeEntry>? entries,
        out string configPrefabName)
    {
        entries = null;
        if (!TryGetActiveSpawnAreaEntryCache(spawnArea, runtimeConfigurationSnapshot, out MatchingEntryCache? entryCache, out configPrefabName))
        {
            return false;
        }

        entries = entryCache!.Entries;
        return true;
    }

    private static bool TryGetActiveCreatureSpawnerEntries(
        CreatureSpawner? creatureSpawner,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot,
        out IReadOnlyList<SpawnerRuntimeEntry>? entries,
        out string configPrefabName)
    {
        entries = null;
        if (!TryGetActiveCreatureSpawnerEntryCache(creatureSpawner, runtimeConfigurationSnapshot, out MatchingEntryCache? entryCache, out configPrefabName))
        {
            return false;
        }

        entries = entryCache!.Entries;
        return true;
    }
}
