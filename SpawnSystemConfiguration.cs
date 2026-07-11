using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal class SpawnSystemSpawnDefinition
{
    [YamlMember(Order = 0)]
    public string? Name { get; set; }

    [YamlMember(Order = 1)]
    public bool? HuntPlayer { get; set; }

    [YamlMember(Order = 2)]
    public IntRangeDefinition? Level { get; set; }

    [YamlIgnore]
    public int? MinLevel { get; set; }

    [YamlIgnore]
    public int? MaxLevel { get; set; }

    [YamlMember(Order = 3)]
    public float? OverrideLevelUpChance { get; set; }

    [YamlMember(Order = 4)]
    public float? LevelUpMinCenterDistance { get; set; }

    [YamlMember(Order = 5)]
    public float? GroundOffset { get; set; }

    [YamlMember(Order = 6)]
    public float? GroundOffsetRandom { get; set; }

    [YamlMember(Order = 7)]
    public float? SpawnInterval { get; set; }

    [YamlMember(Order = 8)]
    public float? SpawnChance { get; set; }

    [YamlMember(Order = 9)]
    public FloatRangeDefinition? SpawnRadius { get; set; }

    [YamlIgnore]
    public float? SpawnRadiusMin { get; set; }

    [YamlIgnore]
    public float? SpawnRadiusMax { get; set; }

    [YamlMember(Order = 10)]
    public IntRangeDefinition? GroupSize { get; set; }

    [YamlIgnore]
    public int? GroupSizeMin { get; set; }

    [YamlIgnore]
    public int? GroupSizeMax { get; set; }

    [YamlMember(Order = 11)]
    public float? GroupRadius { get; set; }

    [YamlMember(Order = 12)]
    public float? NoSpawnRadius { get; set; }

    [YamlMember(Order = 13)]
    public int? MaxSpawned { get; set; }

    [YamlMember(Order = 14)]
    public FloatRangeDefinition? Tilt { get; set; }

    [YamlIgnore]
    public float? MinTilt { get; set; }

    [YamlIgnore]
    public float? MaxTilt { get; set; }

    [YamlMember(Order = 15)]
    public FloatRangeDefinition? Altitude { get; set; }

    [YamlIgnore]
    public float? MinAltitude { get; set; }

    [YamlIgnore]
    public float? MaxAltitude { get; set; }

    [YamlMember(Order = 16)]
    public FloatRangeDefinition? OceanDepth { get; set; }

    [YamlIgnore]
    public float? MinOceanDepth { get; set; }

    [YamlIgnore]
    public float? MaxOceanDepth { get; set; }

    [YamlMember(Order = 17)]
    public FloatRangeDefinition? DistanceFromCenter { get; set; }

    [YamlIgnore]
    public float? MinDistanceFromCenter { get; set; }

    [YamlIgnore]
    public float? MaxDistanceFromCenter { get; set; }

    [YamlMember(Order = 18)]
    public List<string>? Biomes { get; set; }

    [YamlIgnore]
    public Heightmap.Biome? ResolvedBiomeMask { get; set; }

    [YamlMember(Order = 19)]
    public List<string>? BiomeAreas { get; set; }

    [YamlMember(Order = 20)]
    public TimeOfDayDefinition? TimeOfDay { get; set; }

    [YamlMember(Order = 21)]
    public List<string>? RequiredEnvironments { get; set; }

    [YamlMember(Order = 22)]
    public string? RequiredGlobalKey { get; set; }

    [YamlMember(Order = 23)]
    public bool? InLava { get; set; }

    [YamlMember(Order = 24)]
    public bool? InForest { get; set; }

    [YamlMember(Order = 25)]
    public bool? InsidePlayerBase { get; set; }

    [YamlMember(Order = 26)]
    public bool? CanSpawnCloseToPlayer { get; set; }

    [YamlMember(Order = 27)]
    public Dictionary<string, string>? Fields { get; set; }

    [YamlMember(Order = 28)]
    public List<string>? Objects { get; set; }

    [YamlMember(Order = 29)]
    public string? Data { get; set; }

    [YamlMember(Order = 30)]
    public string? Faction { get; set; }
}
