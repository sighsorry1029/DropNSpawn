using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    internal static void OnSpawnSystemAwake(SpawnSystem? system)
    {
        lock (Sync)
        {
            TrackLiveSystemLocked(system);
            if (system == null ||
                ZNetScene.instance == null ||
                ObjectDB.instance == null ||
                DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.SpawnSystem))
            {
                return;
            }

            bool preAttached = PreAttachedSpawnSystemIds.Remove(system.GetInstanceID());
            CompiledSpawnSystemTable? selectedTable = GetSelectedCompiledTableForCurrentState();
            bool preAttachedMutated = preAttached && !IsSystemAttachedToCompiledTable(system, selectedTable);
            bool queueEspRefreshForAwake = !preAttached || preAttachedMutated;

            if (DropNSpawnPlugin.IsSourceOfTruth)
            {
                if (HandleSourceOfTruthSpawnSystemAwake())
                {
                    ApplyIfReady(queueEspRefreshForLiveSystems: queueEspRefreshForAwake);
                    return;
                }
            }
            else if (!RuntimeState.ConfigurationReady)
            {
                if (!CanRetainCurrentCompiledTableWhilePending(ComputeGameDataSignature()))
                {
                    return;
                }
            }
            else if (RuntimeState.ConfigurationReady && (_activeCompiledTable == null || _activeCompiledTable.Lists.Count == 0))
            {
                ApplyIfReady(
                    queueEspRefreshForLiveSystems: queueEspRefreshForAwake,
                    queueLiveSystemAttach: true);
                if (_activeCompiledTable == null || _activeCompiledTable.Lists.Count == 0)
                {
                    return;
                }
            }

            AttachCompiledTableToAwakenedSystem(system, queueEspRefresh: queueEspRefreshForAwake);
        }
    }

    internal static void PreAttachCompiledTableToAwakeningSystem(SpawnSystem? system)
    {
        lock (Sync)
        {
            TrackLiveSystemLocked(system);
            if (system == null ||
                !ShouldApplyLocally() ||
                DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.SpawnSystem))
            {
                return;
            }

            if (!DropNSpawnPlugin.IsSourceOfTruth &&
                ((RuntimeState.ConfigurationReady && (_activeCompiledTable == null || _activeCompiledTable.Lists.Count == 0)) ||
                 (!RuntimeState.ConfigurationReady && !CanRetainCurrentCompiledTableWhilePending(ComputeGameDataSignature()))))
            {
                return;
            }

            CompiledSpawnSystemTable? table = GetSelectedCompiledTableForCurrentState();
            if (table == null)
            {
                return;
            }

            AttachTableToSystem(system, table);
            PreAttachedSpawnSystemIds.Add(system.GetInstanceID());
        }
    }

    internal static void UntrackLiveSystem(SpawnSystem? system)
    {
        lock (Sync)
        {
            UntrackLiveSystemLocked(system);
        }
    }

    internal static bool ShouldBlockClientSpawnSystemUpdate(SpawnSystem? system)
    {
        lock (Sync)
        {
            if (!ShouldApplyLocally() || DropNSpawnPlugin.IsSourceOfTruth)
            {
                return false;
            }

            if (!RuntimeState.ConfigurationReady)
            {
                return true;
            }

            if (_activeCompiledTable == null || _activeCompiledTable.Lists.Count == 0)
            {
                return true;
            }

            return !IsSystemAttachedToCompiledTable(system, _activeCompiledTable);
        }
    }

    private static void QueueEspMarkerRefresh(SpawnSystem? system)
    {
        EspSpawnSystemCompatibility.RequestRefresh(system, _reconcileQueueEpoch);
    }

    private static void EnsureLiveSystemRegistrySessionLocked()
    {
        int currentSceneInstanceId = ZNetScene.instance != null ? ZNetScene.instance.GetInstanceID() : 0;
        if (_liveSystemsRegistrySceneInstanceId == currentSceneInstanceId)
        {
            return;
        }

        FinalizeAllPendingCompiledTableRetirementsLocked();
        _liveSystemsRegistrySceneInstanceId = currentSceneInstanceId;
        LiveSystemsById.Clear();
        LiveSystemsSnapshot.Clear();
        _liveSystemsSnapshotDirty = true;
        _liveSystemsBootstrapAttempted = false;
        SnapshotsBySystemId.Clear();
        _templateSnapshot = null;
        PendingLiveSystemAttaches.Clear();
        PendingLiveSystemAttachIds.Clear();
        PendingLiveSystemAttachEspRefreshIds.Clear();
        EspSpawnSystemCompatibility.ClearPendingRefreshes();
        PreAttachedSpawnSystemIds.Clear();
        ResetPreparedEntriesBuildPipelineLocked(clearPendingTargetSignature: true);
    }

    private static void TrackLiveSystemLocked(SpawnSystem? system)
    {
        EnsureLiveSystemRegistrySessionLocked();
        if (system == null)
        {
            return;
        }

        int systemId = system.GetInstanceID();
        LiveSystemsById[systemId] = system;
        _liveSystemsSnapshotDirty = true;
    }

    private static void UntrackLiveSystemLocked(SpawnSystem? system)
    {
        EnsureLiveSystemRegistrySessionLocked();
        if (system == null)
        {
            return;
        }

        int systemId = system.GetInstanceID();
        if (!LiveSystemsById.Remove(systemId))
        {
            return;
        }

        ClearAttachedRuntimeState(system);
        _liveSystemsSnapshotDirty = true;
        SnapshotsBySystemId.Remove(systemId);
        _templateSnapshot = null;
        PendingLiveSystemAttachIds.Remove(systemId);
        PendingLiveSystemAttachEspRefreshIds.Remove(systemId);
        EspSpawnSystemCompatibility.RemovePendingRefresh(systemId);
        PreAttachedSpawnSystemIds.Remove(systemId);
        MarkSystemMigratedFromRetiredTablesLocked(systemId);
    }

    private static bool HandleSourceOfTruthSpawnSystemAwake()
    {
        if (_activeCompiledTable != null)
        {
            return false;
        }

        bool overrideCreated = EnsurePrimaryOverrideConfigurationFileExists();
        if (overrideCreated)
        {
            LoadConfiguration();
        }

        return true;
    }

    private static void AttachCompiledTableToAwakenedSystem(SpawnSystem system, bool queueEspRefresh)
    {
        CompiledSpawnSystemTable? table = GetSelectedCompiledTableForCurrentState();
        if (table == null)
        {
            return;
        }

        AttachTableToSystem(system, table);
        MarkSystemMigratedFromRetiredTablesLocked(system.GetInstanceID());
        if (queueEspRefresh)
        {
            QueueEspMarkerRefresh(system);
        }
    }

    private static bool TryProcessPendingLiveSystemAttach(double deadline)
    {
        while (PendingLiveSystemAttaches.Count > 0)
        {
            if (Time.realtimeSinceStartupAsDouble >= deadline)
            {
                return false;
            }

            if (!PendingLiveSystemAttaches.TryDequeue(out PendingLiveSystemAttach queuedAttach))
            {
                continue;
            }

            bool queueEspRefresh = PendingLiveSystemAttachEspRefreshIds.Remove(queuedAttach.SystemId);
            PendingLiveSystemAttachIds.Remove(queuedAttach.SystemId);
            if (queuedAttach.Epoch != _reconcileQueueEpoch || queuedAttach.System == null)
            {
                continue;
            }

            if (queuedAttach.BuildVersion != BuildPipelineState.PreparedEntriesBuildVersion ||
                !ReferenceEquals(queuedAttach.TargetTable, GetSelectedCompiledTableForCurrentState()))
            {
                return true;
            }

            if (queuedAttach.TargetTable == null)
            {
                return true;
            }

            AttachTableToSystem(queuedAttach.System, queuedAttach.TargetTable);
            MarkSystemMigratedFromRetiredTablesLocked(queuedAttach.SystemId);
            if (queueEspRefresh)
            {
                QueueEspMarkerRefresh(queuedAttach.System);
            }

            return true;
        }

        return false;
    }

    private static bool TryCaptureSnapshotsIfNeeded()
    {
        List<SpawnSystem> systems = GetLiveSystems();
        if (systems.Count == 0 || ZNetScene.instance == null || ObjectDB.instance == null)
        {
            return false;
        }

        PruneSnapshots(systems);
        foreach (SpawnSystem system in systems)
        {
            CaptureSnapshotIfNeeded(system);
        }

        return SnapshotsBySystemId.Count > 0;
    }

    private static void RefreshSnapshots()
    {
        SnapshotsBySystemId.Clear();
        _templateSnapshot = null;
    }

    private static void InvalidateRuntimeTimeOfDayPhaseMarker()
    {
        _lastRuntimeTimeOfDayPhaseMarker = null;
        _lastRuntimeTimeOfDayRefreshFrame = -1;
    }

    private static void PruneSnapshots(IEnumerable<SpawnSystem> systems)
    {
        HashSet<int> liveSystemIds = systems
            .Where(system => system != null)
            .Select(system => system.GetInstanceID())
            .ToHashSet();

        if (SnapshotsBySystemId.Count == 0)
        {
            return;
        }

        List<int>? staleSystemIds = null;
        foreach (int systemId in SnapshotsBySystemId.Keys)
        {
            if (liveSystemIds.Contains(systemId))
            {
                continue;
            }

            staleSystemIds ??= new List<int>();
            staleSystemIds.Add(systemId);
        }

        if (staleSystemIds == null)
        {
            return;
        }

        foreach (int staleSystemId in staleSystemIds)
        {
            SnapshotsBySystemId.Remove(staleSystemId);
        }

        _templateSnapshot = null;
    }

    private static bool RefreshTemplateSnapshot()
    {
        if (SnapshotsBySystemId.Count > 0)
        {
            _templateSnapshot = CaptureTemplateSnapshot(SnapshotsBySystemId.Values);
            return _templateSnapshot != null;
        }

        List<SpawnSystem> systems = GetLiveSystems();
        if (systems.Count == 0 || ZNetScene.instance == null || ObjectDB.instance == null)
        {
            return false;
        }

        _templateSnapshot = CaptureTemplateSnapshot(systems);
        return _templateSnapshot != null;
    }

    private static SpawnSystemSnapshot CaptureTemplateSnapshot(IEnumerable<SpawnSystem> systems)
    {
        List<SpawnSystemSnapshot> snapshots = systems
            .Where(system => system != null)
            .OrderBy(system => system.GetInstanceID())
            .Select(CaptureSnapshot)
            .ToList();

        return CaptureTemplateSnapshot(snapshots);
    }

    private static SpawnSystemSnapshot CaptureTemplateSnapshot(IEnumerable<SpawnSystemSnapshot> snapshots)
    {
        List<SpawnSystemSnapshot> snapshotList = snapshots
            .Where(snapshot => snapshot != null)
            .OrderBy(snapshot => snapshot.SystemId)
            .ToList();

        if (snapshotList.Count == 0)
        {
            return new SpawnSystemSnapshot();
        }

        SpawnSystemSnapshot aggregatedSnapshot = new()
        {
            SystemId = 0
        };

        // Live SpawnSystem instances often expose the same authoritative table multiple times.
        // For template/full/override output, preserve duplicate multiplicity seen within a single
        // system, but do not multiply identical rows by the number of loaded systems.
        Dictionary<string, (SpawnSystemEntrySnapshot Representative, int MaxCount)> aggregatedEntries = new(StringComparer.Ordinal);
        foreach (SpawnSystemSnapshot snapshot in snapshotList)
        {
            Dictionary<string, (SpawnSystemEntrySnapshot Representative, int Count)> snapshotEntries = new(StringComparer.Ordinal);
            foreach (SpawnSystemEntrySnapshot entry in snapshot.Entries
                         .OrderBy(current => current, Comparer<SpawnSystemEntrySnapshot>.Create(CompareSpawnSystemEntriesForOutput)))
            {
                string stableKey = GetStableReferenceSortKey(entry);
                if (snapshotEntries.TryGetValue(stableKey, out (SpawnSystemEntrySnapshot Representative, int Count) existingSnapshotEntry))
                {
                    snapshotEntries[stableKey] = (existingSnapshotEntry.Representative, existingSnapshotEntry.Count + 1);
                }
                else
                {
                    snapshotEntries[stableKey] = (entry, 1);
                }
            }

            foreach ((string stableKey, (SpawnSystemEntrySnapshot Representative, int Count) snapshotEntry) in snapshotEntries)
            {
                if (aggregatedEntries.TryGetValue(stableKey, out (SpawnSystemEntrySnapshot Representative, int MaxCount) existingAggregate))
                {
                    aggregatedEntries[stableKey] = (
                        existingAggregate.Representative,
                        Math.Max(existingAggregate.MaxCount, snapshotEntry.Count));
                }
                else
                {
                    aggregatedEntries[stableKey] = (snapshotEntry.Representative, snapshotEntry.Count);
                }
            }
        }

        List<SpawnSystemEntrySnapshot> allEntries = aggregatedEntries.Values
            .OrderBy(entry => entry.Representative, Comparer<SpawnSystemEntrySnapshot>.Create(CompareSpawnSystemEntriesForOutput))
            .SelectMany(entry => Enumerable.Range(0, entry.MaxCount).Select(_ => entry.Representative))
            .ToList();

        int entryIndex = 0;
        foreach (SpawnSystemEntrySnapshot entry in allEntries)
        {
            aggregatedSnapshot.Entries.Add(new SpawnSystemEntrySnapshot
            {
                ListIndex = 0,
                EntryIndex = entryIndex++,
                Data = entry.Data.Clone()
            });
        }

        return aggregatedSnapshot;
    }

    private static SpawnSystemSnapshot CaptureSnapshotIfNeeded(SpawnSystem system)
    {
        int systemId = system.GetInstanceID();
        if (SnapshotsBySystemId.TryGetValue(systemId, out SpawnSystemSnapshot? snapshot))
        {
            return snapshot;
        }

        snapshot = CaptureSnapshot(system);
        SnapshotsBySystemId[systemId] = snapshot;
        _templateSnapshot = null;
        return snapshot;
    }

    private static SpawnSystemSnapshot CaptureSnapshot(SpawnSystem system)
    {
        SpawnSystemSnapshot snapshot = new()
        {
            SystemId = system.GetInstanceID()
        };

        for (int listIndex = 0; listIndex < system.m_spawnLists.Count; listIndex++)
        {
            SpawnSystemList spawnList = system.m_spawnLists[listIndex];
            for (int entryIndex = 0; entryIndex < spawnList.m_spawners.Count; entryIndex++)
            {
                SpawnSystem.SpawnData data = spawnList.m_spawners[entryIndex];
                SpawnSystemEntrySnapshot entrySnapshot = new()
                {
                    ListIndex = listIndex,
                    EntryIndex = entryIndex,
                    Data = data.Clone()
                };

                snapshot.Entries.Add(entrySnapshot);
            }
        }

        return snapshot;
    }

    private static List<SpawnSystem> GetLiveSystems()
    {
        EnsureLiveSystemRegistrySessionLocked();
        EnsureLiveSystemsBootstrappedLocked();
        PruneTrackedLiveSystemsLocked();
        if (!_liveSystemsSnapshotDirty)
        {
            return LiveSystemsSnapshot;
        }

        LiveSystemsSnapshot.Clear();
        foreach (SpawnSystem? system in LiveSystemsById.Values)
        {
            if (system != null)
            {
                LiveSystemsSnapshot.Add(system);
            }
        }

        LiveSystemsSnapshot.Sort((left, right) => left.GetInstanceID().CompareTo(right.GetInstanceID()));
        _liveSystemsSnapshotDirty = false;
        return LiveSystemsSnapshot;
    }

    private static void EnsureLiveSystemsBootstrappedLocked()
    {
        if (_liveSystemsBootstrapAttempted || LiveSystemsById.Count > 0)
        {
            return;
        }

        _liveSystemsBootstrapAttempted = true;
        if (SpawnSystemInstancesField?.GetValue(null) is List<SpawnSystem> systems)
        {
            foreach (SpawnSystem? system in systems)
            {
                TrackLiveSystemLocked(system);
            }

            if (LiveSystemsById.Count > 0)
            {
                return;
            }
        }

        foreach (SpawnSystem? system in UnityEngine.Object.FindObjectsByType<SpawnSystem>(FindObjectsSortMode.None))
        {
            TrackLiveSystemLocked(system);
        }
    }

    private static void PruneTrackedLiveSystemsLocked()
    {
        if (LiveSystemsById.Count == 0)
        {
            return;
        }

        List<int>? staleSystemIds = null;
        foreach ((int systemId, SpawnSystem? system) in LiveSystemsById)
        {
            if (system != null)
            {
                continue;
            }

            staleSystemIds ??= new List<int>();
            staleSystemIds.Add(systemId);
        }

        if (staleSystemIds == null)
        {
            return;
        }

        foreach (int staleSystemId in staleSystemIds)
        {
            LiveSystemsById.Remove(staleSystemId);
            SnapshotsBySystemId.Remove(staleSystemId);
            PendingLiveSystemAttachIds.Remove(staleSystemId);
            PendingLiveSystemAttachEspRefreshIds.Remove(staleSystemId);
            EspSpawnSystemCompatibility.RemovePendingRefresh(staleSystemId);
            PreAttachedSpawnSystemIds.Remove(staleSystemId);
            MarkSystemMigratedFromRetiredTablesLocked(staleSystemId);
        }

        _liveSystemsSnapshotDirty = true;
        _templateSnapshot = null;
    }

    private static SpawnSystemSnapshot? GetTemplateSnapshot()
    {
        if (_templateSnapshot == null)
        {
            RefreshTemplateSnapshot();
        }

        return _templateSnapshot;
    }

}
