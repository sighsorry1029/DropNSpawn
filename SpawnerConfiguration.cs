using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class SpawnerConfigurationEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public bool Enabled { get; set; } = true;

    [YamlMember(Order = 3)]
    public string? Location { get; set; }

    [YamlMember(Order = 4)]
    public ConditionsDefinition? Conditions { get; set; }

    [YamlMember(Order = 5)]
    public SpawnAreaDefinition? SpawnArea { get; set; }

    [YamlMember(Order = 6)]
    public CreatureSpawnerDefinition? CreatureSpawner { get; set; }

    [YamlIgnore]
    public string RuleId { get; set; } = "";

    [YamlIgnore]
    public string? SourcePath { get; set; }

    [YamlIgnore]
    public int SourceLine { get; set; }

    [YamlIgnore]
    public int SourceColumn { get; set; }
}

internal sealed class SpawnerReferenceEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public SpawnAreaDefinition? SpawnArea { get; set; }

    [YamlMember(Order = 3)]
    public CreatureSpawnerDefinition? CreatureSpawner { get; set; }
}

internal sealed class SpawnerLocationReferenceEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public List<string> Locations { get; set; } = new();
}

internal sealed class SpawnAreaDefinition
{
    [YamlMember(Order = 1)]
    public float? LevelUpChance { get; set; }
    [YamlMember(Order = 2)]
    public float? SpawnInterval { get; set; }
    [YamlMember(Order = 3)]
    public float? TriggerDistance { get; set; }
    [YamlMember(Order = 4)]
    public bool? SetPatrolSpawnPoint { get; set; }
    [YamlMember(Order = 5)]
    public float? SpawnRadius { get; set; }
    [YamlMember(Order = 6)]
    public float? NearRadius { get; set; }
    [YamlMember(Order = 7)]
    public float? FarRadius { get; set; }
    [YamlMember(Order = 8)]
    public int? MaxNear { get; set; }
    [YamlMember(Order = 9)]
    public int? MaxTotal { get; set; }
    [YamlMember(Order = 10)]
    public int? MaxTotalSpawns { get; set; }
    [YamlMember(Order = 11)]
    public bool? OnGroundOnly { get; set; }
    [YamlMember(Order = 12)]
    public List<SpawnAreaSpawnDefinition>? Creatures { get; set; }
}

internal sealed class SpawnAreaSpawnDefinition
{
    [YamlMember(Order = 1)]
    public string Creature { get; set; } = "";
    [YamlMember(Order = 2)]
    public float? Weight { get; set; }
    [YamlMember(Order = 3)]
    public IntRangeDefinition? Level { get; set; }
    [YamlIgnore]
    public int? MinLevel { get; set; }
    [YamlIgnore]
    public int? MaxLevel { get; set; }
    [YamlMember(Order = 4)]
    public string? Faction { get; set; }
    [YamlMember(Order = 5)]
    public string? Data { get; set; }
    [YamlMember(Order = 6)]
    public Dictionary<string, string>? Fields { get; set; }
    [YamlMember(Order = 7)]
    public List<string>? Objects { get; set; }
}

internal sealed class CreatureSpawnerDefinition
{
    [YamlMember(Order = 1)]
    public string? Creature { get; set; }
    [YamlMember(Order = 2)]
    public TimeOfDayDefinition? TimeOfDay { get; set; }
    [YamlMember(Order = 3)]
    public string? RequiredGlobalKey { get; set; }
    [YamlMember(Order = 4)]
    public string? BlockingGlobalKey { get; set; }
    [YamlMember(Order = 5)]
    public IntRangeDefinition? Level { get; set; }
    [YamlIgnore]
    public int? MinLevel { get; set; }
    [YamlIgnore]
    public int? MaxLevel { get; set; }
    [YamlMember(Order = 6)]
    public float? LevelUpChance { get; set; }
    [YamlMember(Order = 7)]
    public float? RespawnTimeMinutes { get; set; }
    [YamlMember(Order = 8)]
    public int? SpawnCheckInterval { get; set; }
    [YamlMember(Order = 9)]
    public int? SpawnGroupId { get; set; }
    [YamlMember(Order = 10)]
    public float? SpawnGroupRadius { get; set; }
    [YamlMember(Order = 11)]
    public float? SpawnerWeight { get; set; }
    [YamlMember(Order = 12)]
    public int? MaxGroupSpawned { get; set; }
    [YamlMember(Order = 13)]
    public float? TriggerDistance { get; set; }
    [YamlMember(Order = 14)]
    public float? TriggerNoise { get; set; }
    [YamlMember(Order = 15)]
    public bool? RequireSpawnArea { get; set; }
    [YamlMember(Order = 16)]
    public bool? AllowInsidePlayerBase { get; set; }
    [YamlMember(Order = 17)]
    public bool? WakeUpAnimation { get; set; }
    [YamlMember(Order = 18)]
    public bool? SetPatrolSpawnPoint { get; set; }
    [YamlMember(Order = 19)]
    public string? Faction { get; set; }
    [YamlMember(Order = 20)]
    public string? Data { get; set; }
    [YamlMember(Order = 21)]
    public Dictionary<string, string>? Fields { get; set; }
    [YamlMember(Order = 22)]
    public List<string>? Objects { get; set; }
}
