using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class ConfigurationEntryCloneSupport
{
    internal static SpawnSystemSpawnDefinition? CloneSpawnSystemSpawnDefinition(SpawnSystemSpawnDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new SpawnSystemSpawnDefinition
        {
            Name = source.Name,
            HuntPlayer = source.HuntPlayer,
            Level = CloneIntRange(source.Level),
            MinLevel = source.MinLevel,
            MaxLevel = source.MaxLevel,
            OverrideLevelUpChance = source.OverrideLevelUpChance,
            LevelUpMinCenterDistance = source.LevelUpMinCenterDistance,
            GroundOffset = source.GroundOffset,
            GroundOffsetRandom = source.GroundOffsetRandom,
            SpawnInterval = source.SpawnInterval,
            SpawnChance = source.SpawnChance,
            SpawnRadius = CloneFloatRange(source.SpawnRadius),
            SpawnRadiusMin = source.SpawnRadiusMin,
            SpawnRadiusMax = source.SpawnRadiusMax,
            GroupSize = CloneIntRange(source.GroupSize),
            GroupSizeMin = source.GroupSizeMin,
            GroupSizeMax = source.GroupSizeMax,
            GroupRadius = source.GroupRadius
        };
    }

    internal static SpawnSystemConditionsDefinition? CloneSpawnSystemConditionsDefinition(SpawnSystemConditionsDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new SpawnSystemConditionsDefinition
        {
            NoSpawnRadius = source.NoSpawnRadius,
            MaxSpawned = source.MaxSpawned,
            Tilt = CloneFloatRange(source.Tilt),
            MinTilt = source.MinTilt,
            MaxTilt = source.MaxTilt,
            Altitude = CloneFloatRange(source.Altitude),
            MinAltitude = source.MinAltitude,
            MaxAltitude = source.MaxAltitude,
            OceanDepth = CloneFloatRange(source.OceanDepth),
            MinOceanDepth = source.MinOceanDepth,
            MaxOceanDepth = source.MaxOceanDepth,
            DistanceFromCenter = CloneFloatRange(source.DistanceFromCenter),
            MinDistanceFromCenter = source.MinDistanceFromCenter,
            MaxDistanceFromCenter = source.MaxDistanceFromCenter,
            Biomes = CloneStringList(source.Biomes),
            ResolvedBiomeMask = source.ResolvedBiomeMask,
            BiomeAreas = CloneStringList(source.BiomeAreas),
            TimeOfDay = CloneTimeOfDay(source.TimeOfDay),
            RequiredEnvironments = CloneStringList(source.RequiredEnvironments),
            RequiredGlobalKey = source.RequiredGlobalKey,
            InLava = source.InLava,
            InForest = source.InForest,
            InsidePlayerBase = source.InsidePlayerBase,
            CanSpawnCloseToPlayer = source.CanSpawnCloseToPlayer
        };
    }

    internal static SpawnSystemModifiersDefinition? CloneSpawnSystemModifiersDefinition(SpawnSystemModifiersDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new SpawnSystemModifiersDefinition
        {
            Fields = CloneStringDictionary(source.Fields),
            Objects = CloneStringList(source.Objects),
            Data = source.Data,
            Faction = source.Faction
        };
    }
}
