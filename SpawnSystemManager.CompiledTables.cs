using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private static void QueueLiveSystemAttachForTableCore(
        CompiledSpawnSystemTable? table,
        int buildVersion,
        bool queueEspRefresh,
        IEnumerable<SpawnSystem>? systems = null)
    {
        if (table == null)
        {
            return;
        }

        IEnumerable<SpawnSystem> systemsToQueue = systems ?? GetLiveSystems();
        foreach (SpawnSystem system in systemsToQueue)
        {
            QueueLiveSystemAttach(system, table, buildVersion, queueEspRefresh);
        }
    }

    private static void QueueLiveSystemAttachCore(
        SpawnSystem? system,
        CompiledSpawnSystemTable targetTable,
        int buildVersion,
        bool queueEspRefresh)
    {
        if (system == null)
        {
            return;
        }

        int systemId = system.GetInstanceID();
        if (!PendingLiveSystemAttachIds.Add(systemId))
        {
            if (queueEspRefresh)
            {
                PendingLiveSystemAttachEspRefreshIds.Add(systemId);
            }

            return;
        }

        if (queueEspRefresh)
        {
            PendingLiveSystemAttachEspRefreshIds.Add(systemId);
        }

        PendingLiveSystemAttaches.Enqueue(new PendingLiveSystemAttach(system, systemId, _reconcileQueueEpoch, buildVersion, targetTable));
    }

    private static void AttachTableToSystemCore(SpawnSystem? system, CompiledSpawnSystemTable? table)
    {
        if (system == null || table == null || table.Lists.Count == 0)
        {
            return;
        }

        ClearAttachedRuntimeState(system);
        SnapshotsBySystemId.Remove(system.GetInstanceID());
        _templateSnapshot = null;
        system.m_spawnLists = CloneAttachedSpawnLists(table);
    }

    private static bool IsSystemAttachedToCompiledTableCore(SpawnSystem? system, CompiledSpawnSystemTable? table)
    {
        if (system == null || table == null || table.Lists.Count == 0)
        {
            return false;
        }

        List<SpawnSystemList>? currentLists = system.m_spawnLists;
        SpawnListSummary liveSummary = SummarizeSpawnLists(currentLists);
        int expectedListCount = table.BaselineListCount > 0 ? table.BaselineListCount : table.Lists.Count;
        int expectedRowCount = table.BaselineListCount > 0 || table.BaselineRowCount > 0
            ? table.BaselineRowCount
            : CountSpawnRows(table);
        int expectedHash = table.BaselineContentHash != 0
            ? table.BaselineContentHash
            : SummarizeSpawnLists(table.Lists).ContentHash;
        return liveSummary.ListCount == expectedListCount &&
               liveSummary.RowCount == expectedRowCount &&
               liveSummary.ContentHash == expectedHash;
    }

    private static List<SpawnSystemList> CloneAttachedSpawnListsCore(CompiledSpawnSystemTable table)
    {
        List<SpawnSystemList> attachedLists = new(table.Lists.Count);
        foreach (SpawnSystemList sourceList in table.Lists)
        {
            List<SpawnSystem.SpawnData> clonedEntries = new(sourceList?.m_spawners?.Count ?? 0);
            if (sourceList?.m_spawners != null)
            {
                foreach (SpawnSystem.SpawnData templateSpawnData in sourceList.m_spawners)
                {
                    if (templateSpawnData == null)
                    {
                        continue;
                    }

                    SpawnSystem.SpawnData attachedSpawnData = templateSpawnData.Clone();
                    table.RuntimeTimeOfDayBySpawnData.TryGetValue(templateSpawnData, out TimeOfDayDefinition? timeOfDay);
                    table.CustomPayloadsBySpawnData.TryGetValue(templateSpawnData, out SpawnSystemCustomDataSupport.PreparedPayload? payload);
                    SpawnSystemCustomDataSupport.ApplyPreparedPayload(attachedSpawnData, payload);
                    ApplyRuntimeMetadata(attachedSpawnData, timeOfDay);
                    clonedEntries.Add(attachedSpawnData);
                }
            }

            attachedLists.Add(CreateAttachedSpawnList(clonedEntries));
        }

        return attachedLists;
    }

    private static CompiledSpawnSystemTable? GetSelectedCompiledTableForCurrentStateCore()
    {
        return PluginSettingsFacade.IsSpawnSystemDomainEnabled()
            ? _activeCompiledTable ?? _vanillaCompiledTable
            : _vanillaCompiledTable;
    }

    private static CompiledSpawnSystemTable? BuildVanillaCompiledTableCore(int gameDataSignature)
    {
        List<SpawnSystemList> sourceLists = GetVanillaSourceSpawnLists();
        if (sourceLists.Count == 0)
        {
            return null;
        }

        CompiledSpawnSystemTable table = new()
        {
            GameDataSignature = gameDataSignature,
            Signature = $"vanilla:{gameDataSignature.ToString(CultureInfo.InvariantCulture)}"
        };

        foreach (SpawnSystemList sourceList in sourceLists)
        {
            List<SpawnSystem.SpawnData> clonedEntries = sourceList.m_spawners
                .Where(spawnData => spawnData != null)
                .Select(spawnData => spawnData.Clone())
                .ToList();
            table.Lists.Add(CreateManagedSpawnList(clonedEntries));
        }

        FreezeCompiledTableBaseline(table);
        return table;
    }

    private static CompiledSpawnSystemTable BuildActiveCompiledTableCore(int gameDataSignature, List<PreparedSpawnSystemEntry> entries, string preparedEntriesSignature)
    {
        CompiledSpawnSystemTable table = new()
        {
            GameDataSignature = gameDataSignature,
            Signature = preparedEntriesSignature
        };

        List<SpawnSystem.SpawnData> liveEntries = new(entries.Count);
        foreach (PreparedSpawnSystemEntry entry in entries)
        {
            SpawnSystem.SpawnData liveEntry = entry.Data.Clone();
            SpawnSystemCustomDataSupport.ApplyPreparedPayload(liveEntry, entry.CustomDataPayload);
            ApplyRuntimeMetadata(liveEntry, entry.RuntimeTimeOfDay);
            liveEntries.Add(liveEntry);
        }

        table.Lists.Add(CreateManagedSpawnList(liveEntries));
        FreezeCompiledTableBaseline(table);
        return table;
    }

    private static List<SpawnSystemList> GetVanillaSourceSpawnListsCore()
    {
        SpawnSystem? zoneCtrlSpawnSystem = GetZoneCtrlPrefabSpawnSystem();
        if (zoneCtrlSpawnSystem != null)
        {
            return zoneCtrlSpawnSystem.m_spawnLists
                .Where(spawnList => spawnList != null)
                .ToList();
        }

        SpawnSystem? firstLiveSystem = GetLiveSystems().FirstOrDefault();
        if (firstLiveSystem == null)
        {
            return new List<SpawnSystemList>();
        }

        return firstLiveSystem.m_spawnLists
            .Where(spawnList => spawnList != null)
            .ToList();
    }

    private static SpawnSystem? GetZoneCtrlPrefabSpawnSystemCore()
    {
        if (ZoneSystem.instance?.m_zoneCtrlPrefab == null)
        {
            return null;
        }

        return ZoneSystem.instance.m_zoneCtrlPrefab.GetComponent<SpawnSystem>();
    }

    private static SpawnSystemList CreateManagedSpawnListCore(List<SpawnSystem.SpawnData> spawners)
    {
        GameObject host = GetManagedSpawnListHost();
        SpawnSystemList spawnList = host.AddComponent<SpawnSystemList>();
        spawnList.hideFlags = HideFlags.HideAndDontSave;
        spawnList.m_spawners = spawners ?? new List<SpawnSystem.SpawnData>();
        spawnList.m_biomeFolded = new List<Heightmap.Biome>();
        return spawnList;
    }

    private static SpawnSystemList CreateAttachedSpawnListCore(List<SpawnSystem.SpawnData> spawners)
    {
        GameObject host = GetAttachedSpawnListHost();
        SpawnSystemList spawnList = host.AddComponent<SpawnSystemList>();
        spawnList.hideFlags = HideFlags.HideAndDontSave;
        spawnList.m_spawners = spawners ?? new List<SpawnSystem.SpawnData>();
        spawnList.m_biomeFolded = new List<Heightmap.Biome>();
        return spawnList;
    }

    private static GameObject GetManagedSpawnListHostCore()
    {
        if (_managedSpawnListHost != null)
        {
            return _managedSpawnListHost;
        }

        _managedSpawnListHost = new GameObject("DropNSpawn.SpawnSystemLists")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        UnityEngine.Object.DontDestroyOnLoad(_managedSpawnListHost);
        if (DropNSpawnPlugin.Instance != null)
        {
            _managedSpawnListHost.transform.SetParent(DropNSpawnPlugin.Instance.transform, false);
        }

        return _managedSpawnListHost;
    }

    private static GameObject GetAttachedSpawnListHostCore()
    {
        if (_attachedSpawnListHost != null)
        {
            return _attachedSpawnListHost;
        }

        _attachedSpawnListHost = new GameObject("DropNSpawn.SpawnSystemAttachedLists")
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        UnityEngine.Object.DontDestroyOnLoad(_attachedSpawnListHost);
        if (DropNSpawnPlugin.Instance != null)
        {
            _attachedSpawnListHost.transform.SetParent(DropNSpawnPlugin.Instance.transform, false);
        }

        return _attachedSpawnListHost;
    }

    private static void ClearAttachedRuntimeStateCore(SpawnSystem? system)
    {
        if (system == null)
        {
            return;
        }

        ClearRuntimeMetadata(system);
        SpawnSystemCustomDataSupport.ClearCustomData(system);
        DestroyAttachedSpawnLists(system.m_spawnLists);
    }

    private static void DestroyAttachedSpawnListsCore(IEnumerable<SpawnSystemList>? spawnLists)
    {
        if (spawnLists == null)
        {
            return;
        }

        foreach (SpawnSystemList spawnList in spawnLists)
        {
            if (spawnList == null)
            {
                continue;
            }

            if (_attachedSpawnListHost != null && spawnList.gameObject == _attachedSpawnListHost)
            {
                UnityEngine.Object.Destroy(spawnList);
            }
        }
    }

    private static void DestroyReplacedCompiledTableCore(CompiledSpawnSystemTable? table)
    {
        if (table == null ||
            ReferenceEquals(table, _activeCompiledTable) ||
            ReferenceEquals(table, _vanillaCompiledTable))
        {
            return;
        }

        foreach (SpawnSystemList spawnList in table.Lists)
        {
            if (spawnList != null)
            {
                UnityEngine.Object.Destroy(spawnList);
            }
        }

        table.Lists.Clear();
    }
}
