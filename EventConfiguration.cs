using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class EventDefinition
{
    [YamlMember(Alias = "event", Order = 0)]
    public string? Event { get; set; }

    [YamlMember(Order = 1)]
    public List<string>? Settings { get; set; }

    [YamlMember(Order = 2)]
    public List<float>? Standalone { get; set; }

    [YamlMember(Order = 3)]
    public float? SpawnerDelay { get; set; }

    [YamlMember(Order = 4)]
    public EventConditionsDefinition? Conditions { get; set; }

    [YamlMember(Order = 5)]
    public List<string>? Messages { get; set; }

    [YamlMember(Order = 6)]
    public string? ForceEnvironment { get; set; }

    [YamlMember(Order = 7)]
    public string? ForceMusic { get; set; }

    [YamlMember(Order = 8)]
    public List<string>? StartCommands { get; set; }

    [YamlMember(Order = 9)]
    public List<string>? EndCommands { get; set; }

    [YamlMember(Order = 10)]
    public List<EventSpawnDefinition>? Spawns { get; set; }
}

internal sealed class EventConditionsDefinition
{
    [YamlMember(Order = 0)]
    public List<string>? Biomes { get; set; }

    [YamlMember(Order = 1)]
    public List<string>? PlayerBase { get; set; }

    [YamlMember(Order = 2)]
    public List<string>? RequiredEnvironments { get; set; }

    [YamlMember(Order = 3)]
    public List<string>? Players { get; set; }

    [YamlMember(Order = 4)]
    public List<string>? RequiredGlobalKeys { get; set; }

    [YamlMember(Order = 5)]
    public List<string>? ForbiddenGlobalKeys { get; set; }

    [YamlMember(Order = 6)]
    public List<string>? RequiredKnownItems { get; set; }

    [YamlMember(Order = 7)]
    public List<string>? ForbiddenKnownItems { get; set; }

    [YamlMember(Order = 8)]
    public List<string>? RequiredPlayerKeysAny { get; set; }

    [YamlMember(Order = 9)]
    public List<string>? RequiredPlayerKeysAll { get; set; }

    [YamlMember(Order = 10)]
    public List<string>? ForbiddenPlayerKeys { get; set; }
}

internal sealed class EventSpawnDefinition
{
    [YamlMember(Order = 0)]
    public string? Prefab { get; set; }

    [YamlMember(Order = 1)]
    public bool? Enabled { get; set; }

    [YamlMember(Order = 2)]
    public SpawnSystemSpawnDefinition? SpawnSystem { get; set; }

    [YamlMember(Order = 3)]
    public SpawnSystemConditionsDefinition? Conditions { get; set; }

    [YamlMember(Order = 4)]
    public EventSpawnModifiersDefinition? Modifiers { get; set; }
}

internal sealed class EventSpawnModifiersDefinition
{
    [YamlMember(Order = 0)]
    public Dictionary<string, string>? Fields { get; set; }

    [YamlMember(Order = 1)]
    public string? Data { get; set; }

    [YamlMember(Order = 2)]
    public string? Faction { get; set; }
}
