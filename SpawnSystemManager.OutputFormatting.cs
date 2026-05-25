using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SpawnSystemConfigurationEntry = DropNSpawn.CanonicalSpawnSystemEntry;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private static List<PrefabOwnerSection<SpawnSystemEntrySnapshot>> BuildBiomeOrderedSnapshotSections(SpawnSystemSnapshot snapshot)
    {
        List<PrefabOwnerSection<SpawnSystemEntrySnapshot>> sections = PrefabOutputSections.BuildSections(
            snapshot.Entries,
            entry => NormalizeReferencePrefabName(entry.Data.m_prefab) ?? "");

        foreach (PrefabOwnerSection<SpawnSystemEntrySnapshot> section in sections)
        {
            section.Entries.Sort(CompareSpawnSystemEntriesForOutput);
        }

        return sections;
    }

    private static List<PrefabOwnerSection<SpawnSystemConfigurationEntry>> BuildBiomeOrderedReferenceSections(IEnumerable<SpawnSystemConfigurationEntry> entries)
    {
        PrefabOwnerResolver.OwnerSnapshot ownerSnapshot = PrefabOwnerResolver.GetSnapshot();
        List<PrefabOwnerSection<SpawnSystemConfigurationEntry>> sections = PrefabOutputSections.BuildSections(
            entries,
            entry => (entry.Prefab ?? "").Trim(),
            entry => !string.IsNullOrWhiteSpace(entry.ReferenceOwnerName)
                ? entry.ReferenceOwnerName!
                : ownerSnapshot.GetOwnerName((entry.Prefab ?? "").Trim()));

        foreach (PrefabOwnerSection<SpawnSystemConfigurationEntry> section in sections)
        {
            section.Entries.Sort(CompareReferenceEntriesForOutput);
        }

        return sections;
    }

    private static SpawnSystemConfigurationEntry ConvertToReferenceEntry(SpawnSystemEntrySnapshot snapshot)
    {
        return ConvertToReferenceEntry(snapshot.Data);
    }

    private static SpawnSystemConfigurationEntry ConvertToReferenceEntry(SpawnSystem.SpawnData data)
    {
        SpawnSystem.SpawnData defaults = new();
        string? normalizedPrefab = NormalizeReferencePrefabName(data.m_prefab);
        string? normalizedName = NormalizeNullable(data.m_name);
        SpawnSystemSpawnDefinition spawn = new()
        {
            Name = string.Equals(normalizedName, normalizedPrefab, StringComparison.OrdinalIgnoreCase) ? null : normalizedName,
            Level = RangeFormatting.FromReference(data.m_minLevel, data.m_maxLevel, defaults.m_minLevel, defaults.m_maxLevel),
            LevelUpMinCenterDistance = GetReferenceFloatOrNull(data.m_levelUpMinCenterDistance, defaults.m_levelUpMinCenterDistance),
            OverrideLevelUpChance = GetReferenceFloatOrNull(data.m_overrideLevelupChance, defaults.m_overrideLevelupChance),
            SpawnInterval = GetReferenceFloatOrNull(data.m_spawnInterval, defaults.m_spawnInterval),
            SpawnChance = GetReferenceFloatOrNull(data.m_spawnChance, defaults.m_spawnChance),
            SpawnRadius = RangeFormatting.FromReference(data.m_spawnRadiusMin, data.m_spawnRadiusMax, defaults.m_spawnRadiusMin, defaults.m_spawnRadiusMax),
            GroupSize = RangeFormatting.FromReference(data.m_groupSizeMin, data.m_groupSizeMax, defaults.m_groupSizeMin, defaults.m_groupSizeMax),
            GroupRadius = GetReferenceFloatOrNull(data.m_groupRadius, defaults.m_groupRadius),
            HuntPlayer = GetReferenceBoolOrNull(data.m_huntPlayer, defaults.m_huntPlayer),
            GroundOffset = GetReferenceFloatOrNull(data.m_groundOffset, defaults.m_groundOffset),
            GroundOffsetRandom = GetReferenceFloatOrNull(data.m_groundOffsetRandom, defaults.m_groundOffsetRandom)
        };

        SpawnSystemConditionsDefinition conditions = new()
        {
            Biomes = data.m_biome == defaults.m_biome ? null : ConvertBiomes(data.m_biome),
            BiomeAreas = data.m_biomeArea == defaults.m_biomeArea ? null : ConvertBiomeAreas(data.m_biomeArea),
            RequiredGlobalKey = NormalizeNullable(data.m_requiredGlobalKey),
            RequiredEnvironments = NormalizeReferenceStringList(data.m_requiredEnvironments),
            TimeOfDay = data.m_spawnAtDay == defaults.m_spawnAtDay && data.m_spawnAtNight == defaults.m_spawnAtNight
                ? null
                : TimeOfDayFormatting.FromSpawnFlags(data.m_spawnAtDay, data.m_spawnAtNight),
            NoSpawnRadius = GetReferenceFloatOrNull(data.m_spawnDistance, defaults.m_spawnDistance),
            MaxSpawned = GetReferenceIntOrNull(data.m_maxSpawned, defaults.m_maxSpawned),
            Altitude = RangeFormatting.FromReference(data.m_minAltitude, data.m_maxAltitude, defaults.m_minAltitude, defaults.m_maxAltitude),
            Tilt = RangeFormatting.FromReference(data.m_minTilt, data.m_maxTilt, defaults.m_minTilt, defaults.m_maxTilt),
            InForest = GetReferenceExclusiveZoneToggle(data.m_inForest, data.m_outsideForest, defaults.m_inForest, defaults.m_outsideForest),
            InLava = GetReferenceExclusiveZoneToggle(data.m_inLava, data.m_outsideLava, defaults.m_inLava, defaults.m_outsideLava),
            CanSpawnCloseToPlayer = GetReferenceBoolOrNull(data.m_canSpawnCloseToPlayer, defaults.m_canSpawnCloseToPlayer),
            InsidePlayerBase = GetReferenceBoolOrNull(data.m_insidePlayerBase, defaults.m_insidePlayerBase),
            OceanDepth = RangeFormatting.FromReference(data.m_minOceanDepth, data.m_maxOceanDepth, defaults.m_minOceanDepth, defaults.m_maxOceanDepth),
            DistanceFromCenter = RangeFormatting.FromReference(data.m_minDistanceFromCenter, data.m_maxDistanceFromCenter, defaults.m_minDistanceFromCenter, defaults.m_maxDistanceFromCenter)
        };

        return new SpawnSystemConfigurationEntry
        {
            Prefab = normalizedPrefab,
            Enabled = data.m_enabled,
            SpawnSystem = HasAnySpawnFields(spawn) ? spawn : null,
            Conditions = HasAnyConditionFields(conditions) ? conditions : null
        };
    }

    internal static SpawnSystemConfigurationEntry CreateReferenceEntryForExternalProjection(
        SpawnSystem.SpawnData data,
        string? prefabNameFallback = null)
    {
        SpawnSystemConfigurationEntry entry = ConvertToReferenceEntry(data);
        if (string.IsNullOrWhiteSpace(entry.Prefab))
        {
            entry.Prefab = NormalizeNullable(prefabNameFallback);
        }

        return entry;
    }

    private static SpawnSystemConfigurationEntry ConvertToConfigurationEntry(SpawnSystemEntrySnapshot snapshot)
    {
        SpawnSystemSpawnDefinition spawn = new()
        {
            Name = NormalizeNullable(snapshot.Data.m_name),
            HuntPlayer = snapshot.Data.m_huntPlayer,
            Level = RangeFormatting.From(snapshot.Data.m_minLevel, snapshot.Data.m_maxLevel),
            LevelUpMinCenterDistance = snapshot.Data.m_levelUpMinCenterDistance,
            OverrideLevelUpChance = snapshot.Data.m_overrideLevelupChance,
            SpawnInterval = snapshot.Data.m_spawnInterval,
            SpawnChance = snapshot.Data.m_spawnChance,
            SpawnRadius = RangeFormatting.From(snapshot.Data.m_spawnRadiusMin, snapshot.Data.m_spawnRadiusMax),
            GroupSize = RangeFormatting.From(snapshot.Data.m_groupSizeMin, snapshot.Data.m_groupSizeMax),
            GroupRadius = snapshot.Data.m_groupRadius,
            GroundOffset = snapshot.Data.m_groundOffset,
            GroundOffsetRandom = snapshot.Data.m_groundOffsetRandom
        };

        SpawnSystemConditionsDefinition conditions = new()
        {
            Biomes = ConvertBiomes(snapshot.Data.m_biome),
            BiomeAreas = ConvertBiomeAreas(snapshot.Data.m_biomeArea),
            RequiredGlobalKey = NormalizeNullable(snapshot.Data.m_requiredGlobalKey),
            RequiredEnvironments = snapshot.Data.m_requiredEnvironments.Select(environment => environment.Trim()).ToList(),
            TimeOfDay = TimeOfDayFormatting.FromSpawnFlags(snapshot.Data.m_spawnAtDay, snapshot.Data.m_spawnAtNight),
            NoSpawnRadius = snapshot.Data.m_spawnDistance,
            MaxSpawned = snapshot.Data.m_maxSpawned,
            Altitude = RangeFormatting.From(snapshot.Data.m_minAltitude, snapshot.Data.m_maxAltitude),
            Tilt = RangeFormatting.From(snapshot.Data.m_minTilt, snapshot.Data.m_maxTilt),
            InForest = ConvertExclusiveZoneToggle(snapshot.Data.m_inForest, snapshot.Data.m_outsideForest),
            InLava = ConvertExclusiveZoneToggle(snapshot.Data.m_inLava, snapshot.Data.m_outsideLava),
            CanSpawnCloseToPlayer = snapshot.Data.m_canSpawnCloseToPlayer,
            InsidePlayerBase = snapshot.Data.m_insidePlayerBase,
            OceanDepth = RangeFormatting.From(snapshot.Data.m_minOceanDepth, snapshot.Data.m_maxOceanDepth),
            DistanceFromCenter = RangeFormatting.From(snapshot.Data.m_minDistanceFromCenter, snapshot.Data.m_maxDistanceFromCenter)
        };

        return new SpawnSystemConfigurationEntry
        {
            Enabled = snapshot.Data.m_enabled,
            Prefab = NormalizeReferencePrefabName(snapshot.Data.m_prefab),
            SpawnSystem = spawn,
            Conditions = conditions
        };
    }

    private static int CompareSpawnSystemEntriesForOutput(SpawnSystemEntrySnapshot left, SpawnSystemEntrySnapshot right)
    {
        (int GroupRank, string GroupName, int EarliestRank, int BiomeCount) leftBiome = GetBiomeSortKey(left);
        (int GroupRank, string GroupName, int EarliestRank, int BiomeCount) rightBiome = GetBiomeSortKey(right);
        int compare = leftBiome.GroupRank.CompareTo(rightBiome.GroupRank);
        if (compare != 0) return compare;
        compare = StringComparer.OrdinalIgnoreCase.Compare(leftBiome.GroupName, rightBiome.GroupName);
        if (compare != 0) return compare;
        compare = leftBiome.EarliestRank.CompareTo(rightBiome.EarliestRank);
        if (compare != 0) return compare;
        compare = rightBiome.BiomeCount.CompareTo(leftBiome.BiomeCount);
        if (compare != 0) return compare;
        compare = StringComparer.OrdinalIgnoreCase.Compare(NormalizeReferencePrefabName(left.Data.m_prefab) ?? "", NormalizeReferencePrefabName(right.Data.m_prefab) ?? "");
        if (compare != 0) return compare;
        compare = StringComparer.OrdinalIgnoreCase.Compare(left.Data.m_name ?? "", right.Data.m_name ?? "");
        if (compare != 0) return compare;
        return StringComparer.Ordinal.Compare(GetStableReferenceSortKey(left), GetStableReferenceSortKey(right));
    }

    private static string GetStableReferenceSortKey(SpawnSystemEntrySnapshot snapshot)
    {
        SpawnSystemConfigurationEntry entry = ConvertToReferenceEntry(snapshot);
        return GetStableReferenceSortKey(entry);
    }

    private static string GetStableReferenceSortKey(SpawnSystemConfigurationEntry entry)
    {
        return Serializer.Serialize(entry).TrimEnd('\r', '\n');
    }

    private static (int GroupRank, string GroupName, int EarliestRank, int BiomeCount) GetBiomeSortKey(SpawnSystemEntrySnapshot snapshot)
    {
        return BuildBiomeSortKey(ConvertBiomes(snapshot.Data.m_biome));
    }

    private static (int GroupRank, string GroupName, int EarliestRank, int BiomeCount) GetBiomeSortKey(SpawnSystemConfigurationEntry entry)
    {
        return BuildBiomeSortKey(entry.Conditions?.Biomes);
    }

    private static int CompareReferenceEntriesForOutput(SpawnSystemConfigurationEntry left, SpawnSystemConfigurationEntry right)
    {
        (int GroupRank, string GroupName, int EarliestRank, int BiomeCount) leftBiome = GetBiomeSortKey(left);
        (int GroupRank, string GroupName, int EarliestRank, int BiomeCount) rightBiome = GetBiomeSortKey(right);
        int compare = leftBiome.GroupRank.CompareTo(rightBiome.GroupRank);
        if (compare != 0) return compare;
        compare = StringComparer.OrdinalIgnoreCase.Compare(leftBiome.GroupName, rightBiome.GroupName);
        if (compare != 0) return compare;
        compare = leftBiome.EarliestRank.CompareTo(rightBiome.EarliestRank);
        if (compare != 0) return compare;
        compare = rightBiome.BiomeCount.CompareTo(leftBiome.BiomeCount);
        if (compare != 0) return compare;
        compare = StringComparer.OrdinalIgnoreCase.Compare(left.Prefab ?? "", right.Prefab ?? "");
        if (compare != 0) return compare;
        compare = StringComparer.OrdinalIgnoreCase.Compare(left.SpawnSystem?.Name ?? "", right.SpawnSystem?.Name ?? "");
        if (compare != 0) return compare;
        return StringComparer.Ordinal.Compare(GetStableReferenceSortKey(left), GetStableReferenceSortKey(right));
    }

    private static (int GroupRank, string GroupName, int EarliestRank, int BiomeCount) BuildBiomeSortKey(List<string>? biomes)
    {
        List<string> normalizedBiomes = (biomes ?? new List<string>())
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .ToList();
        if (normalizedBiomes.Count == 0)
        {
            return (int.MaxValue, "", int.MaxValue, 0);
        }

        bool includesAll = false;
        int highestRank = -1;
        int earliestRank = int.MaxValue;
        foreach (string biome in normalizedBiomes)
        {
            string token = NormalizeBiomeSortToken(biome);
            if (token == "all")
            {
                includesAll = true;
                continue;
            }
            if (!BiomeOutputOrderLookup.TryGetValue(token, out (string CanonicalName, int Rank) mappedBiome))
            {
                continue;
            }
            highestRank = Math.Max(highestRank, mappedBiome.Rank);
            earliestRank = Math.Min(earliestRank, mappedBiome.Rank);
        }

        if (includesAll)
        {
            highestRank = Math.Max(highestRank, BiomeOutputOrder.Length - 1);
            earliestRank = Math.Min(earliestRank, 0);
        }

        if (highestRank >= 0)
        {
            return (highestRank, BiomeOutputOrder[highestRank], earliestRank, normalizedBiomes.Count);
        }

        string fallbackBiome = normalizedBiomes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).First();
        return (BiomeOutputOrder.Length + 1, fallbackBiome, int.MaxValue, normalizedBiomes.Count);
    }

    private static Dictionary<string, (string CanonicalName, int Rank)> BuildBiomeOutputOrderLookup()
    {
        Dictionary<string, (string CanonicalName, int Rank)> lookup = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < BiomeOutputOrder.Length; index++)
        {
            string biomeName = BiomeOutputOrder[index];
            lookup[NormalizeBiomeSortToken(biomeName)] = (biomeName, index);
        }
        lookup["ashlands"] = (nameof(Heightmap.Biome.AshLands), Array.IndexOf(BiomeOutputOrder, nameof(Heightmap.Biome.AshLands)));
        return lookup;
    }

    private static string NormalizeBiomeSortToken(string? value) => BiomeResolutionSupport.NormalizeBiomeToken(value);
}
