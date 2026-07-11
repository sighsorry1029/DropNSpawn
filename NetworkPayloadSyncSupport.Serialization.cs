using System.Collections.Generic;
using System.IO;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private static void WriteSpawnSystemSpawnDefinition(ZPackage package, SpawnSystemSpawnDefinition definition)
    {
        WriteNullableString(package, definition.Name);
        WriteNullableBool(package, definition.HuntPlayer);
        WriteOptional(package, definition.Level, WriteIntRangeDefinition);
        WriteNullableFloat(package, definition.OverrideLevelUpChance);
        WriteNullableFloat(package, definition.LevelUpMinCenterDistance);
        WriteNullableFloat(package, definition.GroundOffset);
        WriteNullableFloat(package, definition.GroundOffsetRandom);
        WriteNullableFloat(package, definition.SpawnInterval);
        WriteNullableFloat(package, definition.SpawnChance);
        WriteOptional(package, definition.SpawnRadius, WriteFloatRangeDefinition);
        WriteOptional(package, definition.GroupSize, WriteIntRangeDefinition);
        WriteNullableFloat(package, definition.GroupRadius);
        WriteNullableFloat(package, definition.NoSpawnRadius);
        WriteNullableInt(package, definition.MaxSpawned);
        WriteOptional(package, definition.Tilt, WriteFloatRangeDefinition);
        WriteOptional(package, definition.Altitude, WriteFloatRangeDefinition);
        WriteOptional(package, definition.OceanDepth, WriteFloatRangeDefinition);
        WriteOptional(package, definition.DistanceFromCenter, WriteFloatRangeDefinition);
        WriteStringList(package, definition.Biomes);
        WriteNullableInt(package, GetResolvedBiomeMaskValue(definition.Biomes, definition.ResolvedBiomeMask));
        WriteStringList(package, definition.BiomeAreas);
        WriteOptional(package, definition.TimeOfDay, WriteTimeOfDayDefinition);
        WriteStringList(package, definition.RequiredEnvironments);
        WriteNullableString(package, definition.RequiredGlobalKey);
        WriteNullableBool(package, definition.InLava);
        WriteNullableBool(package, definition.InForest);
        WriteNullableBool(package, definition.InsidePlayerBase);
        WriteNullableBool(package, definition.CanSpawnCloseToPlayer);
        WriteStringDictionary(package, definition.Fields);
        WriteStringList(package, definition.Objects);
        WriteNullableString(package, definition.Data);
        WriteNullableString(package, definition.Faction);
    }

    private static SpawnSystemSpawnDefinition ReadSpawnSystemSpawnDefinition(ZPackage package)
    {
        return new SpawnSystemSpawnDefinition
        {
            Name = ReadNullableString(package),
            HuntPlayer = ReadNullableBool(package),
            Level = ReadOptional(package, ReadIntRangeDefinition),
            OverrideLevelUpChance = ReadNullableFloat(package),
            LevelUpMinCenterDistance = ReadNullableFloat(package),
            GroundOffset = ReadNullableFloat(package),
            GroundOffsetRandom = ReadNullableFloat(package),
            SpawnInterval = ReadNullableFloat(package),
            SpawnChance = ReadNullableFloat(package),
            SpawnRadius = ReadOptional(package, ReadFloatRangeDefinition),
            GroupSize = ReadOptional(package, ReadIntRangeDefinition),
            GroupRadius = ReadNullableFloat(package),
            NoSpawnRadius = ReadNullableFloat(package),
            MaxSpawned = ReadNullableInt(package),
            Tilt = ReadOptional(package, ReadFloatRangeDefinition),
            Altitude = ReadOptional(package, ReadFloatRangeDefinition),
            OceanDepth = ReadOptional(package, ReadFloatRangeDefinition),
            DistanceFromCenter = ReadOptional(package, ReadFloatRangeDefinition),
            Biomes = ReadStringList(package),
            ResolvedBiomeMask = ReadNullableInt(package) is int resolvedBiomeMask ? (Heightmap.Biome)resolvedBiomeMask : null,
            BiomeAreas = ReadStringList(package),
            TimeOfDay = ReadOptional(package, ReadTimeOfDayDefinition),
            RequiredEnvironments = ReadStringList(package),
            RequiredGlobalKey = ReadNullableString(package),
            InLava = ReadNullableBool(package),
            InForest = ReadNullableBool(package),
            InsidePlayerBase = ReadNullableBool(package),
            CanSpawnCloseToPlayer = ReadNullableBool(package),
            Fields = ReadStringDictionary(package),
            Objects = ReadStringList(package),
            Data = ReadNullableString(package),
            Faction = ReadNullableString(package)
        };
    }
}
