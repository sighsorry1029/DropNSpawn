using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
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
            SystemId = 0,
            ListCount = 1
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

        AssignReferenceIds(aggregatedSnapshot);
        return aggregatedSnapshot;
    }

    private static void AssignReferenceIds(SpawnSystemSnapshot snapshot)
    {
        Dictionary<string, int> duplicateOrdinals = new(StringComparer.OrdinalIgnoreCase);
        PrefabOwnerResolver.OwnerSnapshot ownerSnapshot = PrefabOwnerResolver.GetSnapshot();

        foreach (SpawnSystemEntrySnapshot entry in snapshot.Entries
                     .OrderBy(current => current.ListIndex)
                     .ThenBy(current => current.EntryIndex))
        {
            string prefabName = NormalizeReferencePrefabName(entry.Data.m_prefab) ?? "entry";
            string ownerName = ownerSnapshot.GetOwnerName(prefabName);
            string duplicateKey = $"{ownerName}|{prefabName}";
            int ordinal = duplicateOrdinals.TryGetValue(duplicateKey, out int currentOrdinal) ? currentOrdinal + 1 : 1;
            duplicateOrdinals[duplicateKey] = ordinal;
            entry.RefId = BuildReferenceId(ownerName, prefabName, ordinal);
        }
    }

    private static string BuildReferenceId(string ownerName, string prefabName, int ordinal)
    {
        return $"spawn_{NormalizeRefIdToken(ownerName)}_{NormalizeRefIdToken(prefabName)}_{ordinal.ToString("000", CultureInfo.InvariantCulture)}";
    }

    private static string NormalizeRefIdToken(string? value)
    {
        StringBuilder builder = new();
        bool wroteSeparator = false;
        foreach (char rawCharacter in (value ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(rawCharacter))
            {
                builder.Append(rawCharacter);
                wroteSeparator = false;
                continue;
            }

            if (builder.Length == 0 || wroteSeparator)
            {
                continue;
            }

            builder.Append('_');
            wroteSeparator = true;
        }

        string normalized = builder.ToString().Trim('_');
        return normalized.Length == 0 ? "entry" : normalized;
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
            SystemId = system.GetInstanceID(),
            ListCount = Math.Max(1, system.m_spawnLists.Count)
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

        AssignReferenceIds(snapshot);
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

    private static SpawnSystemSnapshot? GetCachedTemplateSnapshot()
    {
        if (_templateSnapshot != null)
        {
            return _templateSnapshot;
        }

        if (SnapshotsBySystemId.Count == 0)
        {
            return null;
        }

        _templateSnapshot = CaptureTemplateSnapshot(SnapshotsBySystemId.Values);
        return _templateSnapshot;
    }
}
