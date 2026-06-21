using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class CharacterDropPrefabEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public bool Enabled { get; set; } = true;

    [YamlMember(Order = 3)]
    public ConditionsDefinition? Conditions { get; set; }

    [YamlMember(Order = 4)]
    public CharacterDropDefinition? CharacterDrop { get; set; }

    [YamlIgnore]
    public string RuleId { get; set; } = "";

    [YamlIgnore]
    public string? SourcePath { get; set; }

    [YamlIgnore]
    public int SourceLine { get; set; }

    [YamlIgnore]
    public int SourceColumn { get; set; }
}

internal sealed class CharacterDropReferenceEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public CharacterDropDefinition? CharacterDrop { get; set; }
}

internal sealed class CharacterDropDefinition
{
    [YamlMember(Order = 1)]
    public List<CharacterDropEntryDefinition>? Drops { get; set; }
}

internal sealed class CharacterDropEntryDefinition
{
    [YamlMember(Order = 1)]
    public string Item { get; set; } = "";
    [YamlMember(Order = 2)]
    public IntRangeDefinition? Amount { get; set; }
    [YamlIgnore]
    public int? AmountMin { get; set; }
    [YamlIgnore]
    public int? AmountMax { get; set; }
    [YamlMember(Order = 3)]
    public float? Chance { get; set; }
    [YamlMember(Order = 4)]
    public bool? DontScale { get; set; }
    [YamlMember(Order = 5)]
    public bool? LevelMultiplier { get; set; }
    [YamlMember(Order = 6)]
    public bool? OnePerPlayer { get; set; }
    [YamlMember(Order = 7)]
    public int? AmountLimit { get; set; }
    [YamlMember(Order = 8)]
    public bool? DropInStack { get; set; }
}
