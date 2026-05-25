using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static SpawnerConfigurationEntry? GetBestPrefabOnlyEntry(List<SpawnerConfigurationEntry>? entries, bool forSpawnArea)
    {
        if (entries == null || entries.Count == 0)
        {
            return null;
        }

        for (int index = entries.Count - 1; index >= 0; index--)
        {
            SpawnerConfigurationEntry entry = entries[index];
            if (entry.Location != null)
            {
                continue;
            }

            if (forSpawnArea)
            {
                if (entry.SpawnArea != null && HasSpawnAreaOverride(entry.SpawnArea))
                {
                    return entry;
                }

                continue;
            }

            if (entry.CreatureSpawner != null && HasCreatureSpawnerOverride(entry.CreatureSpawner))
            {
                return entry;
            }
        }

        return null;
    }

    private static bool TryGetActiveSpawnAreaEntryCache(SpawnArea? spawnArea, out MatchingEntryCache? entryCache, out string configPrefabName)
    {
        return TryGetActiveSpawnAreaEntryCache(
            spawnArea,
            GetRuntimeConfigurationSnapshot(),
            out entryCache,
            out configPrefabName);
    }

    private static bool TryGetActiveSpawnAreaEntryCache(
        SpawnArea? spawnArea,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot,
        out MatchingEntryCache? entryCache,
        out string configPrefabName)
    {
        entryCache = null;
        configPrefabName = "";
        if (spawnArea == null || spawnArea.gameObject == null)
        {
            return false;
        }

        configPrefabName = LiveRegistryStore.TryGetTrackedPrefabName(spawnArea, out string trackedPrefabName)
            ? trackedPrefabName
            : GetConfigPrefabName(spawnArea.gameObject, nameof(SpawnArea));
        if (configPrefabName.Length == 0)
        {
            return false;
        }

        if (SelectorCacheStore.TryGetSpawnAreaEntryCache(spawnArea, out entryCache) &&
            IsMatchingEntryCacheValid(spawnArea.gameObject, configPrefabName, entryCache))
        {
            if (!ShouldPersistMatchingEntryCache(entryCache))
            {
                SelectorCacheStore.RemoveSpawnAreaEntryCache(spawnArea);
                ClearSpawnAreaDynamicRuntimeState(spawnArea);
            }

            return entryCache.Entries.Count > 0;
        }

        entryCache = BuildMatchingEntryCache(spawnArea.gameObject, configPrefabName, runtimeConfigurationSnapshot, forSpawnArea: true);
        if (ShouldPersistMatchingEntryCache(entryCache))
        {
            SelectorCacheStore.SetSpawnAreaEntryCache(spawnArea, entryCache);
        }
        else
        {
            SelectorCacheStore.RemoveSpawnAreaEntryCache(spawnArea);
            ClearSpawnAreaDynamicRuntimeState(spawnArea);
        }

        return entryCache.Entries.Count > 0;
    }

    private static bool TryGetActiveCreatureSpawnerEntryCache(CreatureSpawner? creatureSpawner, out MatchingEntryCache? entryCache, out string configPrefabName)
    {
        return TryGetActiveCreatureSpawnerEntryCache(
            creatureSpawner,
            GetRuntimeConfigurationSnapshot(),
            out entryCache,
            out configPrefabName);
    }

    private static bool TryGetActiveCreatureSpawnerEntryCache(
        CreatureSpawner? creatureSpawner,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot,
        out MatchingEntryCache? entryCache,
        out string configPrefabName)
    {
        entryCache = null;
        configPrefabName = "";
        if (creatureSpawner == null || creatureSpawner.gameObject == null)
        {
            return false;
        }

        configPrefabName = LiveRegistryStore.TryGetTrackedPrefabName(creatureSpawner, out string trackedPrefabName)
            ? trackedPrefabName
            : GetConfigPrefabName(creatureSpawner.gameObject, nameof(CreatureSpawner));
        if (configPrefabName.Length == 0)
        {
            return false;
        }

        if (SelectorCacheStore.TryGetCreatureSpawnerEntryCache(creatureSpawner, out entryCache) &&
            IsMatchingEntryCacheValid(creatureSpawner.gameObject, configPrefabName, entryCache))
        {
            if (!ShouldPersistMatchingEntryCache(entryCache))
            {
                SelectorCacheStore.RemoveCreatureSpawnerEntryCache(creatureSpawner);
                ClearCreatureSpawnerDynamicRuntimeState(creatureSpawner);
            }

            return entryCache.Entries.Count > 0;
        }

        entryCache = BuildMatchingEntryCache(creatureSpawner.gameObject, configPrefabName, runtimeConfigurationSnapshot, forSpawnArea: false);
        if (ShouldPersistMatchingEntryCache(entryCache))
        {
            SelectorCacheStore.SetCreatureSpawnerEntryCache(creatureSpawner, entryCache);
        }
        else
        {
            SelectorCacheStore.RemoveCreatureSpawnerEntryCache(creatureSpawner);
            ClearCreatureSpawnerDynamicRuntimeState(creatureSpawner);
        }

        return entryCache.Entries.Count > 0;
    }

    private static MatchingEntryCache BuildMatchingEntryCache(
        GameObject gameObject,
        string configPrefabName,
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot,
        bool forSpawnArea)
    {
        MatchingEntryCache matches = new()
        {
            ConfigPrefabName = configPrefabName
        };

        if (gameObject == null || string.IsNullOrWhiteSpace(configPrefabName))
        {
            return matches;
        }

        if (forSpawnArea)
        {
            if (!runtimeConfigurationSnapshot.ConfiguredSpawnAreaPrefabs.Contains(configPrefabName))
            {
                return matches;
            }
        }
        else if (!runtimeConfigurationSnapshot.ConfiguredCreatureSpawnerPrefabs.Contains(configPrefabName))
        {
            return matches;
        }

        if (!runtimeConfigurationSnapshot.PlansByPrefab.TryGetValue(configPrefabName, out CompiledSpawnerPrefabPlan? prefabPlan))
        {
            return matches;
        }

        List<SpawnerRuntimeEntry> entries = forSpawnArea ? prefabPlan.SpawnAreaEntries : prefabPlan.CreatureSpawnerEntries;
        if (entries.Count == 0)
        {
            return matches;
        }

        bool requiresLocationSelector = entries.Any(candidate => !string.IsNullOrWhiteSpace(candidate.Location));
        string resolvedLocationPrefab = "";
        string sourceLabel = "";
        bool hasResolvedLocation = !requiresLocationSelector || TryGetLiveLocationContextForSelector(gameObject, out resolvedLocationPrefab, out sourceLabel);
        matches.UsesLocationSelector = requiresLocationSelector;
        matches.ResolvedLocationKey = requiresLocationSelector
            ? NormalizeSelectorLocationCacheKey(hasResolvedLocation ? resolvedLocationPrefab : null)
            : "";
        CaptureRecordedLocationProvenanceEpoch(gameObject, matches);

        if (TryGetSharedMatchingEntryTemplate(
                gameObject,
                configPrefabName,
                entries,
                forSpawnArea,
                hasResolvedLocation,
                hasResolvedLocation ? resolvedLocationPrefab : null,
                out SharedMatchingEntryTemplate? sharedTemplate))
        {
            MatchingEntryCache sharedCache = new();
            sharedCache.UseSharedTemplate(sharedTemplate!);
            CaptureRecordedLocationProvenanceEpoch(gameObject, sharedCache);
            return sharedCache;
        }

        foreach (SpawnerRuntimeEntry candidate in entries)
        {
            if (!TryGetEntryMatchSpecificity(gameObject, candidate, hasResolvedLocation, hasResolvedLocation ? resolvedLocationPrefab : null, hasResolvedLocation ? sourceLabel : "", out _))
            {
                continue;
            }

            if (HasRuntimeEntryConditions(candidate) &&
                !DropConditionEvaluator.AreStaticConditionsSatisfied(gameObject, candidate.Conditions, hasResolvedLocation ? resolvedLocationPrefab : null))
            {
                continue;
            }

            matches.MutableEntries.Add(candidate);
            if (candidate.RuntimeReconcile)
            {
                matches.MutableRuntimeEntries.Add(candidate);
                AccumulateRuntimeConditionUsage(matches, candidate.Conditions);
            }
        }

        return matches;
    }

    private static bool TryGetSharedMatchingEntryTemplate(
        GameObject gameObject,
        string configPrefabName,
        List<SpawnerRuntimeEntry> entries,
        bool forSpawnArea,
        bool hasResolvedLocation,
        string? resolvedLocationPrefab,
        out SharedMatchingEntryTemplate? sharedTemplate)
    {
        sharedTemplate = null;
        if (!TryBuildSharedMatchingEntryTemplateKey(
                gameObject,
                configPrefabName,
                entries,
                forSpawnArea,
                hasResolvedLocation,
                resolvedLocationPrefab,
                out StaticSelectorContextSnapshot contextSnapshot,
                out string? cacheKey,
                out string resolvedLocationKey))
        {
            return false;
        }

        if (SelectorCacheStore.TryGetSharedMatchingEntryTemplate(cacheKey, out sharedTemplate))
        {
            return true;
        }

        sharedTemplate = BuildSharedMatchingEntryTemplate(gameObject, configPrefabName, entries, hasResolvedLocation, resolvedLocationPrefab, resolvedLocationKey, contextSnapshot);
        SelectorCacheStore.SetSharedMatchingEntryTemplate(cacheKey, sharedTemplate);
        return true;
    }

    private static bool TryBuildSharedMatchingEntryTemplateKey(
        GameObject gameObject,
        string configPrefabName,
        List<SpawnerRuntimeEntry> entries,
        bool forSpawnArea,
        bool hasResolvedLocation,
        string? resolvedLocationPrefab,
        out StaticSelectorContextSnapshot contextSnapshot,
        out string cacheKey,
        out string resolvedLocationKey)
    {
        contextSnapshot = null!;
        cacheKey = "";
        resolvedLocationKey = hasResolvedLocation
            ? NormalizeSelectorLocationCacheKey(resolvedLocationPrefab)
            : UnresolvedSelectorLocationCacheKey;
        bool usesSelector = false;
        bool usesConditionLocation = false;
        bool usesBiome = false;
        bool usesInDungeon = false;

        foreach (SpawnerRuntimeEntry entry in entries)
        {
            ConditionsDefinition? conditions = entry.Conditions;
            if (!CanUseSharedMatchingEntryTemplate(conditions))
            {
                return false;
            }

            usesSelector |= !string.IsNullOrWhiteSpace(entry.Location);
            usesConditionLocation |= conditions?.Locations?.Any(name => !string.IsNullOrWhiteSpace(name)) == true;
            usesBiome |= conditions?.ResolvedBiomeMask.HasValue == true || conditions?.Biomes?.Any(name => !string.IsNullOrWhiteSpace(name)) == true;
            usesInDungeon |= conditions?.InDungeon.HasValue == true;
        }

        contextSnapshot = GetOrBuildStaticSelectorContextSnapshot(
            gameObject,
            usesSelector,
            usesConditionLocation,
            usesBiome,
            usesInDungeon,
            hasResolvedLocation,
            resolvedLocationPrefab);

        StringBuilder builder = new(configPrefabName.Length + 64);
        builder.Append(forSpawnArea ? "spawnarea|" : "creaturespawner|");
        builder.Append(configPrefabName);
        builder.Append("|selector:");
        builder.Append(usesSelector ? resolvedLocationKey : "<none>");

        if (usesConditionLocation)
        {
            builder.Append("|condloc:");
            builder.Append(contextSnapshot.ConditionLocationName);
        }

        if (usesBiome)
        {
            builder.Append("|biome:");
            builder.Append((int)contextSnapshot.Biome);
        }

        if (usesInDungeon)
        {
            builder.Append("|dungeon:");
            builder.Append(contextSnapshot.InDungeon ? '1' : '0');
        }

        cacheKey = builder.ToString();
        return true;
    }

    private static bool CanUseSharedMatchingEntryTemplate(ConditionsDefinition? conditions)
    {
        return conditions == null ||
               (!conditions.InForest.HasValue &&
                conditions.DistanceFromCenter?.HasValues() != true &&
                !conditions.MinDistanceFromCenter.HasValue &&
                !conditions.MaxDistanceFromCenter.HasValue &&
                conditions.Altitude?.HasValues() != true &&
                !conditions.MinAltitude.HasValue &&
                !conditions.MaxAltitude.HasValue);
    }

    private static SharedMatchingEntryTemplate BuildSharedMatchingEntryTemplate(
        GameObject gameObject,
        string configPrefabName,
        List<SpawnerRuntimeEntry> entries,
        bool hasResolvedLocation,
        string? resolvedLocationPrefab,
        string resolvedLocationKey,
        StaticSelectorContextSnapshot contextSnapshot)
    {
        SharedMatchingEntryTemplate template = new()
        {
            ConfigPrefabName = configPrefabName,
            UsesLocationSelector = entries.Any(entry => !string.IsNullOrWhiteSpace(entry.Location)),
            ResolvedLocationKey = resolvedLocationKey
        };

        foreach (SpawnerRuntimeEntry candidate in entries)
        {
            if (!TryGetEntryMatchSpecificity(gameObject, candidate, hasResolvedLocation, hasResolvedLocation ? resolvedLocationPrefab : null, "", out _))
            {
                continue;
            }

            if (HasRuntimeEntryConditions(candidate) &&
                !DropConditionEvaluator.AreStaticConditionsSatisfied(gameObject, candidate.Conditions, contextSnapshot.ConditionLocationName))
            {
                continue;
            }

            template.Entries.Add(candidate);
            if (candidate.RuntimeReconcile)
            {
                template.RuntimeEntries.Add(candidate);
                AccumulateRuntimeConditionUsage(template.RuntimeRequiredGlobalKeys, template.RuntimeForbiddenGlobalKeys, candidate.Conditions, out bool usesTimeOfDay, out bool usesRequiredEnvironments, out bool usesInsidePlayerBase);
                template.UsesTimeOfDay |= usesTimeOfDay;
                template.UsesRequiredEnvironments |= usesRequiredEnvironments;
                template.UsesInsidePlayerBase |= usesInsidePlayerBase;
            }
        }

        return template;
    }

    private static StaticSelectorContextSnapshot GetOrBuildStaticSelectorContextSnapshot(
        GameObject gameObject,
        bool usesSelector,
        bool usesConditionLocation,
        bool usesBiome,
        bool usesInDungeon,
        bool hasResolvedLocation,
        string? resolvedLocationPrefab)
    {
        bool usesLocationFields = usesSelector || usesConditionLocation;
        if (SelectorCacheStore.TryGetReusableStaticSelectorContext(gameObject, usesLocationFields, out StaticSelectorContextSnapshot cachedSnapshot))
        {
            return cachedSnapshot;
        }

        Vector3 position = gameObject.transform.position;
        StaticSelectorContextSnapshot snapshot = new()
        {
            Position = position,
            ResolvedSelectorLocationPrefab = hasResolvedLocation ? (resolvedLocationPrefab ?? "").Trim() : "",
            SelectorLocationKey = hasResolvedLocation
                ? NormalizeSelectorLocationCacheKey(resolvedLocationPrefab)
                : UnresolvedSelectorLocationCacheKey
        };

        if (usesSelector && !hasResolvedLocation)
        {
            if (TryGetLiveLocationContextForSelector(gameObject, out string selectorLocationPrefab, out string selectorSourceLabel))
            {
                snapshot.ResolvedSelectorLocationPrefab = selectorLocationPrefab;
                snapshot.SelectorSourceLabel = selectorSourceLabel;
                snapshot.SelectorLocationKey = NormalizeSelectorLocationCacheKey(selectorLocationPrefab);
            }
        }
        else if (usesSelector && hasResolvedLocation)
        {
            snapshot.SelectorSourceLabel = "Cached";
        }

        if (usesConditionLocation)
        {
            snapshot.ConditionLocationName = TryGetResolvedLocationNameForConditions(gameObject, out string conditionLocationName)
                ? conditionLocationName
                : "";
        }

        if (usesBiome)
        {
            snapshot.Biome = WorldGenerator.instance?.GetBiome(position) ?? Heightmap.FindBiome(position);
        }

        if (usesInDungeon)
        {
            snapshot.InDungeon = Character.InInterior(position);
        }

        if (usesLocationFields &&
            TryGetRecordedLocationProvenanceEpoch(gameObject, out int provenanceEpoch))
        {
            snapshot.HasRecordedLocationProvenanceEpoch = true;
            snapshot.RecordedLocationProvenanceEpoch = provenanceEpoch;
        }

        SelectorCacheStore.StoreReusableStaticSelectorContext(gameObject, snapshot, storeForLocationFields: usesLocationFields);
        return snapshot;
    }

    private static bool ShouldPersistMatchingEntryCache(MatchingEntryCache? entryCache)
    {
        return entryCache != null && entryCache.Entries.Count > 0;
    }

    private static void CaptureRecordedLocationProvenanceEpoch(GameObject gameObject, MatchingEntryCache cache)
    {
        cache.HasRecordedLocationProvenanceEpoch = false;
        cache.RecordedLocationProvenanceEpoch = 0;
        if (gameObject == null || cache == null || !cache.UsesLocationSelector)
        {
            return;
        }

        if (!TryGetRecordedLocationProvenanceEpoch(gameObject, out int provenanceEpoch))
        {
            return;
        }

        cache.HasRecordedLocationProvenanceEpoch = true;
        cache.RecordedLocationProvenanceEpoch = provenanceEpoch;
    }

    private static bool TryGetRecordedLocationProvenanceEpoch(GameObject gameObject, out int provenanceEpoch)
    {
        provenanceEpoch = 0;
        if (gameObject == null)
        {
            return false;
        }

        if (gameObject.TryGetComponent(out SpawnArea spawnArea) &&
            ProvenanceRegistry.TryGetSpawnAreaProvenance(spawnArea, out SpawnerLocationProvenance spawnAreaProvenance))
        {
            provenanceEpoch = spawnAreaProvenance.Epoch;
            return provenanceEpoch != 0;
        }

        if (gameObject.TryGetComponent(out CreatureSpawner creatureSpawner) &&
            ProvenanceRegistry.TryGetCreatureSpawnerProvenance(creatureSpawner, out SpawnerLocationProvenance creatureSpawnerProvenance))
        {
            provenanceEpoch = creatureSpawnerProvenance.Epoch;
            return provenanceEpoch != 0;
        }

        return false;
    }
}
