using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    internal static bool TryInspectCurrentTarget(out string[] lines, out string error)
    {
        lock (Sync)
        {
            lines = Array.Empty<string>();
            error = "";

            if (Player.m_localPlayer == null)
            {
                error = "Player is not available.";
                return false;
            }

            GameObject? target = ResolveCurrentInspectionTarget();
            if (target == null)
            {
                error = "No hovered or nearby spawner target found.";
                return false;
            }

            List<string> result = BuildInspectionLines(target);
            if (result.Count == 0)
            {
                error = "The current target is not a SpawnArea or CreatureSpawner.";
                return false;
            }

            lines = result.ToArray();
            return true;
        }
    }

    private static GameObject? ResolveCurrentInspectionTarget()
    {
        GameObject? hoverObject = Player.m_localPlayer?.GetHoverObject();
        if (TryResolveInspectionTargetFromObject(hoverObject, out GameObject? target))
        {
            return target;
        }

        Vector3 probePoint = Player.m_localPlayer != null
            ? Player.m_localPlayer.transform.position + Player.m_localPlayer.transform.forward * 5f
            : Vector3.zero;

        if (GameCamera.instance != null &&
            Physics.Raycast(GameCamera.instance.transform.position, GameCamera.instance.transform.forward, out RaycastHit hitInfo, 100f))
        {
            GameObject? hitObject = hitInfo.collider?.attachedRigidbody != null
                ? hitInfo.collider.attachedRigidbody.gameObject
                : hitInfo.collider?.gameObject;
            if (TryResolveInspectionTargetFromObject(hitObject, out target))
            {
                return target;
            }

            probePoint = hitInfo.point;
        }

        return TryFindNearestInspectionTarget(probePoint, out target) ? target : null;
    }

    private static bool TryResolveInspectionTargetFromObject(GameObject? sourceObject, out GameObject? targetObject)
    {
        targetObject = null;
        if (sourceObject == null)
        {
            return false;
        }

        SpawnArea? spawnArea = sourceObject.GetComponent<SpawnArea>() ?? sourceObject.GetComponentInParent<SpawnArea>(true);
        CreatureSpawner? creatureSpawner = sourceObject.GetComponent<CreatureSpawner>() ?? sourceObject.GetComponentInParent<CreatureSpawner>(true);
        if (spawnArea == null && creatureSpawner == null)
        {
            return false;
        }

        if (spawnArea != null && creatureSpawner == null)
        {
            targetObject = spawnArea.gameObject;
            return true;
        }

        if (creatureSpawner != null && spawnArea == null)
        {
            targetObject = creatureSpawner.gameObject;
            return true;
        }

        int spawnAreaDepth = GetAncestorDepth(sourceObject.transform, spawnArea!.transform);
        int creatureSpawnerDepth = GetAncestorDepth(sourceObject.transform, creatureSpawner!.transform);
        targetObject = spawnAreaDepth <= creatureSpawnerDepth ? spawnArea.gameObject : creatureSpawner.gameObject;
        return true;
    }

    private static int GetAncestorDepth(Transform source, Transform ancestor)
    {
        int depth = 0;
        Transform? current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return depth;
            }

            current = current.parent;
            depth++;
        }

        return int.MaxValue;
    }

    private static bool TryFindNearestInspectionTarget(Vector3 probePoint, out GameObject? targetObject)
    {
        targetObject = null;
        float bestDistanceSquared = 8f * 8f;

        foreach (SpawnArea spawnArea in UnityEngine.Object.FindObjectsByType<SpawnArea>(FindObjectsSortMode.None))
        {
            if (spawnArea == null || spawnArea.gameObject == null)
            {
                continue;
            }

            float distanceSquared = (spawnArea.transform.position - probePoint).sqrMagnitude;
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            targetObject = spawnArea.gameObject;
        }

        foreach (CreatureSpawner creatureSpawner in UnityEngine.Object.FindObjectsByType<CreatureSpawner>(FindObjectsSortMode.None))
        {
            if (creatureSpawner == null || creatureSpawner.gameObject == null)
            {
                continue;
            }

            float distanceSquared = (creatureSpawner.transform.position - probePoint).sqrMagnitude;
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            targetObject = creatureSpawner.gameObject;
        }

        return targetObject != null;
    }

    private static List<string> BuildInspectionLines(GameObject targetObject)
    {
        List<string> lines = new();
        if (targetObject == null)
        {
            return lines;
        }

        if (targetObject.TryGetComponent(out SpawnArea spawnArea))
        {
            AppendSpawnAreaInspectionLines(lines, spawnArea);
            return lines;
        }

        if (targetObject.TryGetComponent(out CreatureSpawner creatureSpawner))
        {
            AppendCreatureSpawnerInspectionLines(lines, creatureSpawner);
        }

        return lines;
    }

    private static void AppendSpawnAreaInspectionLines(List<string> lines, SpawnArea spawnArea)
    {
        string configPrefabName = GetConfigPrefabName(spawnArea.gameObject, nameof(SpawnArea));
        lines.Add("Spawner Inspect: SpawnArea");
        lines.Add($"Object: {configPrefabName}@{DescribeInstance(spawnArea.gameObject)}");
        AppendResolvedLocationLines(lines, spawnArea.gameObject, spawnArea, null);

        List<SpawnerConfigurationEntry> configuredEntries = RuntimeState.ActiveEntriesByPrefab.TryGetValue(configPrefabName, out List<SpawnerConfigurationEntry>? entries)
            ? entries.Where(entry => entry.SpawnArea != null && HasSpawnAreaOverride(entry.SpawnArea)).ToList()
            : new List<SpawnerConfigurationEntry>();
        lines.Add($"Configured entries: {configuredEntries.Count}");

        bool hasMatchingEntries = TryGetActiveSpawnAreaEntryCache(spawnArea, out MatchingEntryCache? entryCache, out _);
        lines.Add($"Selector-matching entries: {(hasMatchingEntries ? entryCache!.Entries.Count : 0)}");
        IReadOnlyList<SpawnerRuntimeEntry> matchingEntries = hasMatchingEntries
            ? entryCache!.Entries
            : new List<SpawnerRuntimeEntry>();
        if (matchingEntries.Count > 0 &&
            TrySelectWinningSpawnerEntry(spawnArea.gameObject, matchingEntries, forSpawnArea: true, out SpawnerRuntimeEntry? winningEntry) &&
            winningEntry != null)
        {
            lines.Add($"Winning entry: {FormatInspectionEntrySummary(winningEntry)}");
        }
        else
        {
            lines.Add("Winning entry: none");
        }
    }

    private static void AppendCreatureSpawnerInspectionLines(List<string> lines, CreatureSpawner creatureSpawner)
    {
        string configPrefabName = GetConfigPrefabName(creatureSpawner.gameObject, nameof(CreatureSpawner));
        lines.Add("Spawner Inspect: CreatureSpawner");
        lines.Add($"Object: {configPrefabName}@{DescribeInstance(creatureSpawner.gameObject)}");
        AppendResolvedLocationLines(lines, creatureSpawner.gameObject, null, creatureSpawner);

        List<SpawnerConfigurationEntry> configuredEntries = RuntimeState.ActiveEntriesByPrefab.TryGetValue(configPrefabName, out List<SpawnerConfigurationEntry>? entries)
            ? entries.Where(entry => entry.CreatureSpawner != null && HasCreatureSpawnerOverride(entry.CreatureSpawner)).ToList()
            : new List<SpawnerConfigurationEntry>();
        lines.Add($"Configured entries: {configuredEntries.Count}");

        bool hasMatchingEntries = TryGetActiveCreatureSpawnerEntryCache(creatureSpawner, out MatchingEntryCache? entryCache, out _);
        lines.Add($"Selector-matching entries: {(hasMatchingEntries ? entryCache!.Entries.Count : 0)}");
        IReadOnlyList<SpawnerRuntimeEntry> matchingEntries = hasMatchingEntries
            ? entryCache!.Entries
            : new List<SpawnerRuntimeEntry>();
        if (matchingEntries.Count > 0 &&
            TrySelectWinningSpawnerEntry(creatureSpawner.gameObject, matchingEntries, forSpawnArea: false, out SpawnerRuntimeEntry? winningEntry) &&
            winningEntry != null)
        {
            lines.Add($"Winning entry: {FormatInspectionEntrySummary(winningEntry)}");
        }
        else
        {
            lines.Add("Winning entry: none");
        }
    }

    private static void AppendResolvedLocationLines(List<string> lines, GameObject gameObject, SpawnArea? spawnArea, CreatureSpawner? creatureSpawner)
    {
        if (TryGetLiveLocationContext(gameObject, out string locationPrefab, out string relativePath, out string sourceLabel))
        {
            lines.Add($"Resolved location: {locationPrefab}");
            lines.Add($"Resolved path: {(relativePath.Length > 0 ? relativePath : "(unavailable)")}");
            lines.Add($"Resolution source: {sourceLabel}");
        }
        else
        {
            lines.Add("Resolved location: unavailable");
            lines.Add("Resolved path: unavailable");
            lines.Add("Resolution source: unavailable");
        }

        AppendLocationFallbackDiagnostics(lines, gameObject);

        SpawnerLocationProvenance? provenance = null;
        if (spawnArea != null)
        {
            ProvenanceRegistry.TryGetSpawnAreaProvenance(spawnArea, out provenance);
        }
        else if (creatureSpawner != null)
        {
            ProvenanceRegistry.TryGetCreatureSpawnerProvenance(creatureSpawner, out provenance);
        }

        if (provenance != null)
        {
            lines.Add($"Recorded provenance: location={provenance.LocationPrefab}, path={provenance.RelativePath}");
        }
    }

    private static void AppendLocationFallbackDiagnostics(List<string> lines, GameObject gameObject)
    {
        if (TryGetStaticLocationContext(gameObject, out string staticLocation, out _))
        {
            lines.Add($"Static location: {staticLocation}");
        }

        if (TryGetZoneLocationContext(gameObject, out string zoneLocation))
        {
            lines.Add($"Zone location: {zoneLocation}");
        }

        if (TryGetSpatialLocationContext(gameObject, out string radiusLocation))
        {
            lines.Add($"Radius location: {radiusLocation}");
        }
    }

    private static string FormatInspectionEntrySummary(SpawnerRuntimeEntry entry)
    {
        if (entry == null)
        {
            return "(null)";
        }

        string selector = FormatLocationSelector(entry.Locations);
        if (entry.CreatureSpawner != null)
        {
            return $"{selector}, creatureSpawner.creature={entry.CreatureSpawner.Creature ?? "(null)"}";
        }

        if (entry.SpawnArea?.Creatures != null)
        {
            return $"{selector}, spawnArea.creatures={entry.SpawnArea.Creatures.Count}";
        }

        return selector;
    }
}
