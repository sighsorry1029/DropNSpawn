using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private sealed class TemplateAggregate
    {
        public string Prefab { get; set; } = "";
        public string OwnerName { get; set; } = PrefabOwnerCatalog.UnknownOwnerName;
        public HashSet<string> RootPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> LocationPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SpawnAreaComponentSnapshot? SpawnArea { get; set; }
        public CreatureSpawnerComponentSnapshot? CreatureSpawner { get; set; }
    }

    private static List<TemplateAggregate> BuildTemplateAggregates()
    {
        Dictionary<string, TemplateAggregate> aggregates = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SortedSet<string>> locationPrefabsBySpawnerPrefab = BuildLocationReferenceLookup();

        foreach (SpawnAreaComponentSnapshot snapshot in SpawnAreaSnapshots)
        {
            if (!aggregates.TryGetValue(snapshot.ConfigPrefabName, out TemplateAggregate? aggregate))
            {
                aggregate = new TemplateAggregate { Prefab = snapshot.ConfigPrefabName };
                aggregates[snapshot.ConfigPrefabName] = aggregate;
            }

            aggregate.SpawnArea ??= snapshot;
            TrackAggregateRootPrefab(aggregate, snapshot.RootPrefabName);
            TrackAggregateLocationPrefabs(aggregate, locationPrefabsBySpawnerPrefab, snapshot.ConfigPrefabName);
        }

        foreach (CreatureSpawnerComponentSnapshot snapshot in CreatureSpawnerSnapshots)
        {
            if (!aggregates.TryGetValue(snapshot.ConfigPrefabName, out TemplateAggregate? aggregate))
            {
                aggregate = new TemplateAggregate { Prefab = snapshot.ConfigPrefabName };
                aggregates[snapshot.ConfigPrefabName] = aggregate;
            }

            aggregate.CreatureSpawner ??= snapshot;
            TrackAggregateRootPrefab(aggregate, snapshot.RootPrefabName);
            TrackAggregateLocationPrefabs(aggregate, locationPrefabsBySpawnerPrefab, snapshot.ConfigPrefabName);
        }

        List<TemplateAggregate> templateAggregates = aggregates.Values.ToList();
        foreach (TemplateAggregate aggregate in templateAggregates)
        {
            aggregate.OwnerName = ResolveSpawnerOwnerName(aggregate.Prefab, aggregate.LocationPrefabs, aggregate.RootPrefabs);
        }

        templateAggregates.Sort(CompareSpawnerAggregatesForOutput);
        return templateAggregates;
    }

    private static List<PrefabOwnerSection<TemplateAggregate>> BuildTemplateAggregateSections()
    {
        List<PrefabOwnerSection<TemplateAggregate>> aggregateSections = PrefabOutputSections.BuildSections(
            BuildTemplateAggregates(),
            aggregate => aggregate.Prefab,
            aggregate => aggregate.OwnerName);
        foreach (PrefabOwnerSection<TemplateAggregate> section in aggregateSections)
        {
            section.Entries.Sort(CompareSpawnerAggregatesForOutput);
        }

        return aggregateSections;
    }

    private static List<PrefabOwnerSection<SpawnerConfigurationEntry>> BuildConfigurationTemplate()
    {
        List<PrefabOwnerSection<TemplateAggregate>> aggregateSections = BuildTemplateAggregateSections();

        return aggregateSections
            .Select(section => new PrefabOwnerSection<SpawnerConfigurationEntry>(
                section.OwnerName,
                section.Entries.Select(BuildConfigurationEntry).ToList()))
            .ToList();
    }

    private static string BuildReferenceConfigurationTemplate()
    {
        List<PrefabOwnerSection<SpawnerReferenceEntry>> sections = BuildTemplateAggregateSections()
            .Select(section => new PrefabOwnerSection<SpawnerReferenceEntry>(
                section.OwnerName,
                section.Entries
                    .Select(aggregate => new SpawnerReferenceEntry
                    {
                        Prefab = aggregate.Prefab,
                        SpawnArea = aggregate.SpawnArea != null ? ConvertSpawnArea(aggregate.SpawnArea) : null,
                        CreatureSpawner = aggregate.CreatureSpawner != null ? ConvertCreatureSpawner(aggregate.CreatureSpawner) : null
                    })
                    .ToList()))
            .ToList();

        return PrefabOutputSections.SerializeReferenceSections(sections, Serializer);
    }

    private static string BuildLocationReferenceConfigurationTemplate()
    {
        List<TemplateAggregate> aggregates = BuildTemplateAggregates();
        Dictionary<string, string> ownerNamesByPrefab = aggregates.ToDictionary(
            aggregate => aggregate.Prefab,
            aggregate => aggregate.OwnerName,
            StringComparer.OrdinalIgnoreCase);

        List<SpawnerLocationReferenceEntry> entries = aggregates
            .Where(aggregate => aggregate.LocationPrefabs.Count > 0)
            .OrderBy(aggregate => aggregate.Prefab, StringComparer.OrdinalIgnoreCase)
            .Select(aggregate => new SpawnerLocationReferenceEntry
            {
                Prefab = aggregate.Prefab,
                Locations = aggregate.LocationPrefabs.OrderBy(location => location, StringComparer.OrdinalIgnoreCase).ToList()
            })
            .ToList();

        return SerializeLocationReferenceEntries(entries, ownerNamesByPrefab);
    }

    private static Dictionary<string, SortedSet<string>> BuildLocationReferenceLookup()
    {
        Dictionary<string, SortedSet<string>> groupedInstances = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string locationPrefab, GameObject rootPrefab) in EnumerateLocationRootPrefabs())
        {
            foreach (SpawnArea spawnArea in rootPrefab.GetComponentsInChildren<SpawnArea>(true))
            {
                if (spawnArea == null || spawnArea.gameObject == null)
                {
                    continue;
                }

                AddLocationReferenceLocation(
                    groupedInstances,
                    GetLocationReferencePrefabName(spawnArea.gameObject),
                    locationPrefab);
            }

            foreach (CreatureSpawner creatureSpawner in rootPrefab.GetComponentsInChildren<CreatureSpawner>(true))
            {
                if (creatureSpawner == null || creatureSpawner.gameObject == null)
                {
                    continue;
                }

                AddLocationReferenceLocation(
                    groupedInstances,
                    GetLocationReferencePrefabName(creatureSpawner.gameObject),
                    locationPrefab);
            }
        }

        return groupedInstances;
    }

    private static string SerializeLocationReferenceEntries(
        IEnumerable<SpawnerLocationReferenceEntry> entries,
        IReadOnlyDictionary<string, string> ownerNamesByPrefab)
    {
        List<PrefabOwnerSection<SpawnerLocationReferenceEntry>> sections = PrefabOutputSections.BuildSections(
            entries,
            entry => entry.Prefab,
            entry => ResolveSpawnerLocationReferenceOwnerName(entry, ownerNamesByPrefab));
        foreach (PrefabOwnerSection<SpawnerLocationReferenceEntry> section in sections)
        {
            section.Entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Prefab, right.Prefab));
        }

        return PrefabOutputSections.SerializeReferenceSections(sections, Serializer);
    }

    private static void WriteReferenceConfigurationFile(
        string? referenceContent,
        string? locationReferenceContent,
        string logMessage,
        bool writePrimaryReference,
        bool writeLocationReference)
    {
        if (writePrimaryReference && referenceContent != null)
        {
            GeneratedArtifactWriter.WriteText(ReferenceConfigurationPath, referenceContent);
        }

        if (writeLocationReference && locationReferenceContent != null)
        {
            GeneratedArtifactWriter.WriteText(LocationReferenceConfigurationPath, locationReferenceContent);
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(logMessage);
    }

    private static void EnsureReferenceArtifactsUpToDate()
    {
        if (!IsGameDataReady())
        {
            return;
        }

        bool usedReferenceSnapshots = EnsureSpawnerReferenceConfigurationUpToDate();

        if (usedReferenceSnapshots)
        {
            ResetReferenceSnapshots();
        }
    }

    private static bool EnsureSpawnerReferenceConfigurationUpToDate()
    {
        string currentSourceSignature = ComputeReferenceSourceSignature();
        bool writePrimaryReference = ReferenceArtifactLifecycle.TryPlanUpdate(
            ReferenceAutoUpdateStateKey,
            ReferenceConfigurationPath,
            currentSourceSignature,
            out ReferenceArtifactUpdateKind primaryUpdateKind);
        bool writeLocationReference = ReferenceArtifactLifecycle.TryPlanUpdate(
            LocationReferenceAutoUpdateStateKey,
            LocationReferenceConfigurationPath,
            currentSourceSignature,
            out ReferenceArtifactUpdateKind locationUpdateKind);
        if (!writePrimaryReference && !writeLocationReference)
        {
            return false;
        }

        CaptureSnapshotsIfNeeded();
        string? referenceContent = writePrimaryReference ? BuildReferenceConfigurationTemplate() : null;
        string? locationReferenceContent = writeLocationReference ? BuildLocationReferenceConfigurationTemplate() : null;
        bool updatedExisting = primaryUpdateKind == ReferenceArtifactUpdateKind.Updated ||
                               locationUpdateKind == ReferenceArtifactUpdateKind.Updated;
        string action = updatedExisting ? "Updated" : "Created";
        string targetDescription = writePrimaryReference && writeLocationReference
            ? $"spawner reference configurations at {ReferenceConfigurationPath} and {LocationReferenceConfigurationPath}"
            : writePrimaryReference
                ? $"spawner reference configuration at {ReferenceConfigurationPath}"
                : $"spawner location reference configuration at {LocationReferenceConfigurationPath}";

        WriteReferenceConfigurationFile(
            referenceContent,
            locationReferenceContent,
            $"{action} {targetDescription}.",
            writePrimaryReference,
            writeLocationReference);
        if (writePrimaryReference)
        {
            ReferenceArtifactLifecycle.RecordUpdate(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, currentSourceSignature);
        }

        if (writeLocationReference)
        {
            ReferenceArtifactLifecycle.RecordUpdate(LocationReferenceAutoUpdateStateKey, LocationReferenceConfigurationPath, currentSourceSignature);
        }

        return true;
    }

    private static IEnumerable<string> BuildCurrentSpawnerReferencePrefabKeys()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject rootPrefab in EnumerateRootPrefabs())
        {
            foreach (SpawnArea spawnArea in rootPrefab.GetComponentsInChildren<SpawnArea>(true))
            {
                string prefabName = ReferenceRefreshSupport.NormalizeKey(spawnArea?.gameObject?.name);
                if (prefabName.Length > 0 && seen.Add(prefabName))
                {
                    yield return prefabName;
                }
            }

            foreach (CreatureSpawner creatureSpawner in rootPrefab.GetComponentsInChildren<CreatureSpawner>(true))
            {
                string prefabName = ReferenceRefreshSupport.NormalizeKey(creatureSpawner?.gameObject?.name);
                if (prefabName.Length > 0 && seen.Add(prefabName))
                {
                    yield return prefabName;
                }
            }
        }
    }

    private static string ComputeReferenceSourceSignature()
    {
        return ReferenceRefreshSupport.ComputeStableHashForKeys(BuildCurrentSpawnerReferencePrefabKeys());
    }

    internal static bool TryWriteFullScaffoldConfigurationFile(out string path, out string error)
    {
        string content;
        string logMessage;
        lock (Sync)
        {
            path = FullScaffoldConfigurationPath;
            error = "";

            if (!IsGameDataReady() && !_snapshotsCaptured)
            {
                error = "Spawner game data is not ready yet.";
                return false;
            }

            CaptureSnapshotsIfNeeded();
            content = BuildFullScaffoldConfigurationTemplate();
            logMessage = $"Wrote spawner full scaffold configuration to {path}.";
        }

        GeneratedArtifactWriter.WriteTextAlways(path, content, logMessage);
        return true;
    }

    internal static void RefreshReferenceConfigurationFile()
    {
        string referenceContent;
        string locationReferenceContent;
        string sourceSignature;
        string logMessage;
        lock (Sync)
        {
            if (!IsGameDataReady())
            {
                return;
            }

            CaptureSnapshotsIfNeeded();
            referenceContent = BuildReferenceConfigurationTemplate();
            locationReferenceContent = BuildLocationReferenceConfigurationTemplate();
            sourceSignature = ComputeReferenceSourceSignature();
            logMessage = $"Updated spawner reference configurations at {ReferenceConfigurationPath} and {LocationReferenceConfigurationPath}.";
        }

        WriteReferenceConfigurationFile(
            referenceContent,
            locationReferenceContent,
            logMessage,
            writePrimaryReference: true,
            writeLocationReference: true);
        ReferenceArtifactLifecycle.RecordUpdate(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, sourceSignature);
        ReferenceArtifactLifecycle.RecordUpdate(LocationReferenceAutoUpdateStateKey, LocationReferenceConfigurationPath, sourceSignature);
        lock (Sync)
        {
            ResetReferenceSnapshots();
        }
    }

    private static IEnumerable<(string LocationPrefab, GameObject RootPrefab)> EnumerateLocationRootPrefabs()
    {
        if (ZoneSystem.instance == null)
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
        {
            if (!location.m_prefab.IsValid)
            {
                continue;
            }

            string locationPrefab = (location.m_prefab.Name ?? "").Trim();
            if (locationPrefab.Length == 0 || !seen.Add(locationPrefab))
            {
                continue;
            }

            location.m_prefab.Load();
            GameObject? rootPrefab = location.m_prefab.Asset;
            if (rootPrefab == null)
            {
                continue;
            }

            yield return (locationPrefab, rootPrefab);
        }
    }

    private static void AddLocationReferenceLocation(
        Dictionary<string, SortedSet<string>> groupedLocations,
        string configPrefabName,
        string locationPrefab)
    {
        string normalizedConfigPrefabName = (configPrefabName ?? "").Trim();
        string normalizedLocationPrefab = (locationPrefab ?? "").Trim();
        if (normalizedConfigPrefabName.Length == 0 ||
            normalizedLocationPrefab.Length == 0)
        {
            return;
        }

        if (!groupedLocations.TryGetValue(normalizedConfigPrefabName, out SortedSet<string>? locations))
        {
            locations = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            groupedLocations[normalizedConfigPrefabName] = locations;
        }

        locations.Add(normalizedLocationPrefab);
    }

    private static SpawnerConfigurationEntry BuildConfigurationEntry(TemplateAggregate aggregate)
    {
        return new SpawnerConfigurationEntry
        {
            Prefab = aggregate.Prefab,
            Enabled = true,
            SpawnArea = aggregate.SpawnArea != null ? ConvertSpawnArea(aggregate.SpawnArea) : null,
            CreatureSpawner = aggregate.CreatureSpawner != null ? ConvertCreatureSpawner(aggregate.CreatureSpawner) : null
        };
    }

    private static void TrackAggregateRootPrefab(TemplateAggregate aggregate, string rootPrefabName)
    {
        string normalizedRootPrefabName = (rootPrefabName ?? "").Trim();
        if (normalizedRootPrefabName.Length > 0)
        {
            aggregate.RootPrefabs.Add(normalizedRootPrefabName);
        }
    }

    private static void TrackAggregateLocationPrefabs(
        TemplateAggregate aggregate,
        Dictionary<string, SortedSet<string>> locationPrefabsBySpawnerPrefab,
        string configPrefabName)
    {
        string normalizedConfigPrefabName = (configPrefabName ?? "").Trim();
        if (normalizedConfigPrefabName.Length == 0 ||
            !locationPrefabsBySpawnerPrefab.TryGetValue(normalizedConfigPrefabName, out SortedSet<string>? locationPrefabs))
        {
            return;
        }

        foreach (string locationPrefab in locationPrefabs)
        {
            if (!string.IsNullOrWhiteSpace(locationPrefab))
            {
                aggregate.LocationPrefabs.Add(locationPrefab.Trim());
            }
        }
    }

    private static string ResolveSpawnerOwnerName(
        string prefabName,
        IEnumerable<string> locationPrefabs,
        IEnumerable<string> rootPrefabs)
    {
        PrefabOwnerResolver.OwnerSnapshot ownerSnapshot = PrefabOwnerResolver.GetSnapshot();
        List<string> locationOwners = locationPrefabs
            .Select(ownerSnapshot.GetOwnerName)
            .Where(ownerName => !string.Equals(ownerName, PrefabOwnerCatalog.UnknownOwnerName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (locationOwners.Count == 1)
        {
            return locationOwners[0];
        }

        if (locationOwners.Count > 1)
        {
            return PrefabOwnerCatalog.UnknownOwnerName;
        }

        List<string> knownOwners = rootPrefabs
            .Select(ownerSnapshot.GetOwnerName)
            .Where(ownerName => !string.Equals(ownerName, PrefabOwnerCatalog.UnknownOwnerName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (knownOwners.Count == 1)
        {
            return knownOwners[0];
        }

        if (knownOwners.Count > 1)
        {
            return PrefabOwnerCatalog.UnknownOwnerName;
        }

        return ownerSnapshot.GetOwnerName(prefabName);
    }

    private static string ResolveSpawnerLocationReferenceOwnerName(
        SpawnerLocationReferenceEntry entry,
        IReadOnlyDictionary<string, string> ownerNamesByPrefab)
    {
        if (ownerNamesByPrefab.TryGetValue(entry.Prefab, out string? ownerName) &&
            !string.IsNullOrWhiteSpace(ownerName))
        {
            return ownerName;
        }

        return ResolveSpawnerOwnerName(entry.Prefab, entry.Locations ?? new List<string>(), Array.Empty<string>());
    }

    private static int CompareSpawnerAggregatesForOutput(TemplateAggregate? left, TemplateAggregate? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left == null)
        {
            return 1;
        }

        if (right == null)
        {
            return -1;
        }

        int primaryComparison = GetSpawnerPrimaryComponentRank(left).CompareTo(GetSpawnerPrimaryComponentRank(right));
        if (primaryComparison != 0)
        {
            return primaryComparison;
        }

        int signatureComparison = GetSpawnerComponentSignatureMask(left).CompareTo(GetSpawnerComponentSignatureMask(right));
        if (signatureComparison != 0)
        {
            return signatureComparison;
        }

        return string.Compare(left.Prefab, right.Prefab, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetSpawnerPrimaryComponentRank(TemplateAggregate aggregate)
    {
        if (aggregate.SpawnArea != null)
        {
            return 0;
        }

        if (aggregate.CreatureSpawner != null)
        {
            return 1;
        }

        return 2;
    }

    private static int GetSpawnerComponentSignatureMask(TemplateAggregate aggregate)
    {
        int mask = 0;
        if (aggregate.SpawnArea != null)
        {
            mask |= 1 << 0;
        }

        if (aggregate.CreatureSpawner != null)
        {
            mask |= 1 << 1;
        }

        return mask;
    }

    private static SpawnAreaDefinition ConvertSpawnArea(SpawnAreaComponentSnapshot snapshot)
    {
        List<SpawnAreaSpawnDefinition> creatures = snapshot.Prefabs
            .Select(prefab => new { Name = prefab.Prefab?.name ?? "", Prefab = prefab })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new SpawnAreaSpawnDefinition
            {
                Creature = entry.Name,
                Weight = IsReferenceDefault(entry.Prefab.Weight, 1f) ? null : entry.Prefab.Weight,
                Level = RangeFormatting.FromReference(entry.Prefab.MinLevel, entry.Prefab.MaxLevel, 1, 1)
            })
            .ToList();

        return new SpawnAreaDefinition
        {
            LevelUpChance = IsReferenceDefault(snapshot.LevelUpChance, 15f) ? null : snapshot.LevelUpChance,
            SpawnInterval = IsReferenceDefault(snapshot.SpawnInterval, 30f) ? null : snapshot.SpawnInterval,
            TriggerDistance = IsReferenceDefault(snapshot.TriggerDistance, 256f) ? null : snapshot.TriggerDistance,
            SetPatrolSpawnPoint = snapshot.SetPatrolSpawnPoint ? null : false,
            SpawnRadius = IsReferenceDefault(snapshot.SpawnRadius, 2f) ? null : snapshot.SpawnRadius,
            NearRadius = IsReferenceDefault(snapshot.NearRadius, 10f) ? null : snapshot.NearRadius,
            FarRadius = IsReferenceDefault(snapshot.FarRadius, 1000f) ? null : snapshot.FarRadius,
            MaxNear = snapshot.MaxNear == 3 ? null : snapshot.MaxNear,
            MaxTotal = snapshot.MaxTotal == 20 ? null : snapshot.MaxTotal,
            OnGroundOnly = snapshot.OnGroundOnly ? true : null,
            Creatures = creatures.Count > 0 ? creatures : null
        };
    }

    private static CreatureSpawnerDefinition ConvertCreatureSpawner(CreatureSpawnerComponentSnapshot snapshot)
    {
        return new CreatureSpawnerDefinition
        {
            Creature = snapshot.CreaturePrefab != null ? snapshot.CreaturePrefab.name : null,
            Level = RangeFormatting.FromReference(snapshot.MinLevel, snapshot.MaxLevel, 1, 1),
            LevelUpChance = IsReferenceDefault(snapshot.LevelUpChance, 10f) ? null : snapshot.LevelUpChance,
            RespawnTimeMinutes = IsReferenceDefault(snapshot.RespawnTimeMinutes, 20f) ? null : snapshot.RespawnTimeMinutes,
            TriggerDistance = IsReferenceDefault(snapshot.TriggerDistance, 60f) ? null : snapshot.TriggerDistance,
            TriggerNoise = IsReferenceDefault(snapshot.TriggerNoise, 0f) ? null : snapshot.TriggerNoise,
            TimeOfDay = snapshot.SpawnAtDay && snapshot.SpawnAtNight ? null : TimeOfDayFormatting.FromSpawnFlags(snapshot.SpawnAtDay, snapshot.SpawnAtNight),
            AllowInsidePlayerBase = snapshot.SpawnInPlayerBase ? true : null,
            WakeUpAnimation = snapshot.WakeUpAnimation ? true : null,
            SpawnCheckInterval = snapshot.SpawnCheckInterval == 5 ? null : snapshot.SpawnCheckInterval,
            RequiredGlobalKey = snapshot.RequiredGlobalKey.Length > 0 ? snapshot.RequiredGlobalKey : null,
            BlockingGlobalKey = snapshot.BlockingGlobalKey.Length > 0 ? snapshot.BlockingGlobalKey : null,
            SetPatrolSpawnPoint = snapshot.SetPatrolSpawnPoint ? true : null,
            SpawnGroupId = snapshot.SpawnGroupId == 0 ? null : snapshot.SpawnGroupId,
            MaxGroupSpawned = snapshot.MaxGroupSpawned == 1 ? null : snapshot.MaxGroupSpawned,
            SpawnGroupRadius = IsReferenceDefault(snapshot.SpawnGroupRadius, 0f) ? null : snapshot.SpawnGroupRadius,
            SpawnerWeight = IsReferenceDefault(snapshot.SpawnerWeight, 1f) ? null : snapshot.SpawnerWeight
        };
    }
}
