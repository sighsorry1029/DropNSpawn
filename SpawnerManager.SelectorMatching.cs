using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static bool IsMatchingEntryCacheValid(GameObject gameObject, string configPrefabName, MatchingEntryCache? entryCache)
    {
        if (gameObject == null || entryCache == null)
        {
            return false;
        }

        if (!string.Equals(entryCache.ConfigPrefabName, configPrefabName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!entryCache.UsesLocationSelector)
        {
            return true;
        }

        if (TryGetRecordedLocationProvenanceEpoch(gameObject, out int currentRecordedProvenanceEpoch))
        {
            return entryCache.HasRecordedLocationProvenanceEpoch &&
                   entryCache.RecordedLocationProvenanceEpoch == currentRecordedProvenanceEpoch;
        }

        if (entryCache.HasRecordedLocationProvenanceEpoch)
        {
            return false;
        }

        return string.Equals(
            entryCache.ResolvedLocationKey,
            BuildSelectorLocationCacheKey(gameObject),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSelectorLocationCacheKey(GameObject gameObject)
    {
        return TryGetLiveLocationContextForSelector(gameObject, out string locationPrefab, out _)
            ? NormalizeSelectorLocationCacheKey(locationPrefab)
            : UnresolvedSelectorLocationCacheKey;
    }

    private static string NormalizeSelectorLocationCacheKey(string? locationPrefab)
    {
        string normalized = (locationPrefab ?? "").Trim();
        return normalized.Length > 0 ? normalized : UnresolvedSelectorLocationCacheKey;
    }

    private static void AccumulateRuntimeConditionUsage(MatchingEntryCache cache, ConditionsDefinition? conditions)
    {
        if (conditions == null)
        {
            return;
        }

        AccumulateRuntimeConditionUsage(
            cache.MutableRuntimeRequiredGlobalKeys,
            cache.MutableRuntimeForbiddenGlobalKeys,
            conditions,
            out bool usesTimeOfDay,
            out bool usesRequiredEnvironments,
            out bool usesInsidePlayerBase);
        cache.UsesTimeOfDay |= usesTimeOfDay;
        cache.UsesRequiredEnvironments |= usesRequiredEnvironments;
        cache.UsesInsidePlayerBase |= usesInsidePlayerBase;
    }

    private static void AccumulateRuntimeConditionUsage(
        List<string> runtimeRequiredGlobalKeys,
        List<string> runtimeForbiddenGlobalKeys,
        ConditionsDefinition? conditions,
        out bool usesTimeOfDay,
        out bool usesRequiredEnvironments,
        out bool usesInsidePlayerBase)
    {
        usesTimeOfDay = false;
        usesRequiredEnvironments = false;
        usesInsidePlayerBase = false;
        if (conditions == null)
        {
            return;
        }

        usesTimeOfDay = conditions.TimeOfDay != null;
        usesRequiredEnvironments = HasConfiguredValues(conditions.RequiredEnvironments);
        usesInsidePlayerBase = conditions.InsidePlayerBase.HasValue;
        AddNormalizedConditionValues(runtimeRequiredGlobalKeys, conditions.RequiredGlobalKeys);
        AddNormalizedConditionValues(runtimeForbiddenGlobalKeys, conditions.ForbiddenGlobalKeys);
    }

    private static bool HasConfiguredValues(List<string>? values)
    {
        return values != null && values.Any(value => !string.IsNullOrWhiteSpace(value));
    }

    private static void AddNormalizedConditionValues(List<string> target, List<string>? values)
    {
        if (values == null)
        {
            return;
        }

        foreach (string value in values)
        {
            string normalized = (value ?? "").Trim();
            if (normalized.Length == 0 || target.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            target.Add(normalized);
        }
    }

    private static bool HasRuntimeEntryConditions(SpawnerRuntimeEntry? entry)
    {
        return entry != null && DropConditionEvaluator.HasConditions(entry.Conditions);
    }

    private static bool TrySelectWinningSpawnerEntryForRuntime(
        GameObject gameObject,
        MatchingEntryCache entryCache,
        bool forSpawnArea,
        RuntimeContextSnapshot runtimeContext,
        LocalRuntimeState localRuntimeState,
        int runtimeSignature,
        out SpawnerRuntimeEntry? winningEntry)
    {
        if (entryCache.WinningEntriesByRuntimeSignature.TryGetValue(runtimeSignature, out winningEntry))
        {
            return winningEntry != null;
        }

        winningEntry = null;
        int winningSpecificity = -1;
        foreach (SpawnerRuntimeEntry entry in entryCache.Entries)
        {
            if (entry.RuntimeReconcile &&
                !AreRuntimeSpawnerConditionsSatisfied(gameObject, entry.Conditions, runtimeContext, localRuntimeState))
            {
                continue;
            }

            int specificity = string.IsNullOrWhiteSpace(entry.Location) ? 0 : 1;
            if (specificity < winningSpecificity)
            {
                continue;
            }

            winningEntry = entry;
            winningSpecificity = specificity;
        }

        if (entryCache.WinningEntriesByRuntimeSignature.Count >= 16)
        {
            entryCache.WinningEntriesByRuntimeSignature.Clear();
        }

        entryCache.WinningEntriesByRuntimeSignature[runtimeSignature] = winningEntry;
        return winningEntry != null;
    }

    private static bool TrySelectWinningSpawnerEntry(
        GameObject gameObject,
        IEnumerable<SpawnerRuntimeEntry>? entries,
        bool forSpawnArea,
        out SpawnerRuntimeEntry? winningEntry)
    {
        winningEntry = null;
        if (gameObject == null || entries == null)
        {
            return false;
        }

        bool locationContextEvaluated = false;
        bool hasResolvedLocation = false;
        string resolvedLocationPrefab = "";
        string resolvedLocationSourceLabel = "";
        int winningSpecificity = -1;
        foreach (SpawnerRuntimeEntry entry in entries)
        {
            if (entry == null)
            {
                continue;
            }

            if (forSpawnArea)
            {
                if (entry.SpawnArea == null || !HasSpawnAreaOverride(entry.SpawnArea))
                {
                    continue;
                }
            }
            else if (entry.CreatureSpawner == null || !HasCreatureSpawnerOverride(entry.CreatureSpawner))
            {
                continue;
            }

            if (!locationContextEvaluated && !string.IsNullOrWhiteSpace(entry.Location))
            {
                hasResolvedLocation = TryGetLiveLocationContextForSelector(gameObject, out resolvedLocationPrefab, out resolvedLocationSourceLabel);
                locationContextEvaluated = true;
            }

            if (!TryGetEntryMatchSpecificity(gameObject, entry, hasResolvedLocation, hasResolvedLocation ? resolvedLocationPrefab : null, hasResolvedLocation ? resolvedLocationSourceLabel : "", out int specificity))
            {
                continue;
            }

            if (HasRuntimeEntryConditions(entry) &&
                !DropConditionEvaluator.AreSatisfied(gameObject, entry.Conditions))
            {
                continue;
            }

            if (specificity < winningSpecificity)
            {
                continue;
            }

            winningEntry = entry;
            winningSpecificity = specificity;
        }

        return winningEntry != null;
    }

    private static bool TrySelectWinningSpawnerEntry(GameObject gameObject, IEnumerable<SpawnerConfigurationEntry>? entries, bool forSpawnArea, out SpawnerConfigurationEntry? winningEntry)
    {
        winningEntry = null;
        if (gameObject == null || entries == null)
        {
            return false;
        }

        bool locationContextEvaluated = false;
        bool hasResolvedLocation = false;
        string resolvedLocationPrefab = "";
        string resolvedLocationSourceLabel = "";
        int winningSpecificity = -1;
        foreach (SpawnerConfigurationEntry entry in entries)
        {
            if (entry == null)
            {
                continue;
            }

            if (forSpawnArea)
            {
                if (entry.SpawnArea == null || !HasSpawnAreaOverride(entry.SpawnArea))
                {
                    continue;
                }
            }
            else if (entry.CreatureSpawner == null || !HasCreatureSpawnerOverride(entry.CreatureSpawner))
            {
                continue;
            }

            if (!locationContextEvaluated && !string.IsNullOrWhiteSpace(entry.Location))
            {
                hasResolvedLocation = TryGetLiveLocationContextForSelector(gameObject, out resolvedLocationPrefab, out resolvedLocationSourceLabel);
                locationContextEvaluated = true;
            }

            if (!TryGetEntryMatchSpecificity(gameObject, entry, hasResolvedLocation, hasResolvedLocation ? resolvedLocationPrefab : null, hasResolvedLocation ? resolvedLocationSourceLabel : "", out int specificity))
            {
                continue;
            }

            if (HasEntryConditions(entry) &&
                !DropConditionEvaluator.AreSatisfied(gameObject, entry.Conditions))
            {
                continue;
            }

            if (specificity < winningSpecificity)
            {
                continue;
            }

            winningEntry = entry;
            winningSpecificity = specificity;
        }

        return winningEntry != null;
    }

    private static bool TryGetEntryMatchSpecificity(GameObject gameObject, SpawnerConfigurationEntry entry, out int specificity)
    {
        bool hasResolvedLocation = TryGetLiveLocationContextForSelector(gameObject, out string locationPrefab, out string sourceLabel);
        return TryGetEntryMatchSpecificity(gameObject, entry, hasResolvedLocation, hasResolvedLocation ? locationPrefab : null, hasResolvedLocation ? sourceLabel : "", out specificity);
    }

    private static bool TryGetEntryMatchSpecificity(GameObject gameObject, SpawnerRuntimeEntry entry, out int specificity)
    {
        bool hasResolvedLocation = TryGetLiveLocationContextForSelector(gameObject, out string locationPrefab, out string sourceLabel);
        return TryGetEntryMatchSpecificity(gameObject, entry, hasResolvedLocation, hasResolvedLocation ? locationPrefab : null, hasResolvedLocation ? sourceLabel : "", out specificity);
    }

    private static bool TryGetEntryMatchSpecificity(
        GameObject gameObject,
        SpawnerConfigurationEntry entry,
        bool hasResolvedLocation,
        string? resolvedLocationPrefab,
        string sourceLabel,
        out int specificity)
    {
        specificity = 0;
        if (gameObject == null)
        {
            return false;
        }

        bool hasLocationSelector = !string.IsNullOrWhiteSpace(entry.Location);
        if (!hasLocationSelector)
        {
            return true;
        }

        if (!hasResolvedLocation)
        {
            LogLocationSelectorDiagnostic(gameObject, entry, "no live location context");
            return false;
        }

        if (!string.Equals(entry.Location, resolvedLocationPrefab, StringComparison.OrdinalIgnoreCase))
        {
            LogLocationSelectorDiagnostic(gameObject, entry, $"location mismatch via {sourceLabel}", resolvedLocationPrefab ?? "");
            return false;
        }

        specificity = 1;
        return true;
    }

    private static bool TryGetEntryMatchSpecificity(
        GameObject gameObject,
        SpawnerRuntimeEntry entry,
        bool hasResolvedLocation,
        string? resolvedLocationPrefab,
        string sourceLabel,
        out int specificity)
    {
        specificity = 0;
        if (gameObject == null)
        {
            return false;
        }

        bool hasLocationSelector = !string.IsNullOrWhiteSpace(entry.Location);
        if (!hasLocationSelector)
        {
            return true;
        }

        if (!hasResolvedLocation)
        {
            LogLocationSelectorDiagnostic(gameObject, entry, "no live location context");
            return false;
        }

        if (!string.Equals(entry.Location, resolvedLocationPrefab, StringComparison.OrdinalIgnoreCase))
        {
            LogLocationSelectorDiagnostic(gameObject, entry, $"location mismatch via {sourceLabel}", resolvedLocationPrefab ?? "");
            return false;
        }

        specificity = 1;
        return true;
    }

    private static bool TryGetLiveLocationContextForSelector(GameObject gameObject, out string locationPrefab, out string sourceLabel)
    {
        locationPrefab = "";
        sourceLabel = "";
        if (gameObject == null)
        {
            return false;
        }

        if (TryGetRecordedLocationContext(gameObject, out locationPrefab, out _))
        {
            sourceLabel = "Provenance";
            return locationPrefab.Length > 0;
        }

        if (TryGetActiveLocationSpawnContextPrefab(out locationPrefab))
        {
            sourceLabel = "SpawnLocationContext";
            return locationPrefab.Length > 0;
        }

        if (TryGetLiveLocationProxyPrefab(gameObject, out locationPrefab))
        {
            sourceLabel = "LocationProxy";
            return locationPrefab.Length > 0;
        }

        if (TryGetDirectLocationContext(gameObject, out locationPrefab, out _))
        {
            sourceLabel = "LocationComponent";
            return locationPrefab.Length > 0;
        }

        if (TryGetStaticLocationContext(gameObject, out locationPrefab, out _))
        {
            sourceLabel = "LocationStatic";
            return locationPrefab.Length > 0;
        }

        if (TryGetZoneLocationContext(gameObject, out locationPrefab))
        {
            sourceLabel = "LocationZone";
            return locationPrefab.Length > 0;
        }

        if (TryPromoteSpatialContextToRecordedProvenance(gameObject, out locationPrefab, out _))
        {
            sourceLabel = "LocationRadius";
            return locationPrefab.Length > 0;
        }

        return false;
    }

    private static bool TryGetLiveLocationProxyPrefab(GameObject gameObject, out string locationPrefab)
    {
        locationPrefab = "";
        if (gameObject == null)
        {
            return false;
        }

        Transform? contextRoot = GetLocationContextRootTransform(gameObject);
        if (contextRoot == null)
        {
            return false;
        }

        return ProvenanceRegistry.TryGetRecordedRootLocationPrefab(contextRoot, out locationPrefab);
    }
}
