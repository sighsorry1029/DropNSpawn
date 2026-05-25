using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class PrefabConfigurationEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public bool Enabled { get; set; } = true;

    [YamlMember(Order = 3)]
    public ConditionsDefinition? Conditions { get; set; }

    [YamlMember(Order = 4)]
    public DropTableDefinition? DropOnDestroyed { get; set; }

    [YamlMember(Order = 5)]
    public DamageableDropTableDefinition? MineRock { get; set; }

    [YamlMember(Order = 6)]
    public DamageableDropTableDefinition? MineRock5 { get; set; }

    [YamlMember(Order = 7)]
    public DamageableDropTableDefinition? TreeBase { get; set; }

    [YamlMember(Order = 8)]
    public DamageableDropTableDefinition? TreeLog { get; set; }

    [YamlMember(Order = 9)]
    public DropTableDefinition? Container { get; set; }

    [YamlMember(Order = 10)]
    public PickableItemDefinition? PickableItem { get; set; }

    [YamlMember(Order = 11)]
    public PickableDefinition? Pickable { get; set; }

    [YamlMember(Order = 12)]
    public FishDefinition? Fish { get; set; }

    [YamlMember(Order = 13)]
    public DestructibleDefinition? Destructible { get; set; }

    [YamlIgnore]
    public string RuleId { get; set; } = "";

    [YamlIgnore]
    public string? SourcePath { get; set; }

    [YamlIgnore]
    public int SourceLine { get; set; }

    [YamlIgnore]
    public int SourceColumn { get; set; }
}

internal sealed class PrefabReferenceEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public DropTableDefinition? DropOnDestroyed { get; set; }

    [YamlMember(Order = 3)]
    public DamageableDropTableDefinition? MineRock { get; set; }

    [YamlMember(Order = 4)]
    public DamageableDropTableDefinition? MineRock5 { get; set; }

    [YamlMember(Order = 5)]
    public DamageableDropTableDefinition? TreeBase { get; set; }

    [YamlMember(Order = 6)]
    public DamageableDropTableDefinition? TreeLog { get; set; }

    [YamlMember(Order = 7)]
    public DropTableDefinition? Container { get; set; }

    [YamlMember(Order = 8)]
    public PickableItemDefinition? PickableItem { get; set; }

    [YamlMember(Order = 9)]
    public PickableDefinition? Pickable { get; set; }

    [YamlMember(Order = 10)]
    public FishDefinition? Fish { get; set; }

    [YamlMember(Order = 11)]
    public DestructibleDefinition? Destructible { get; set; }
}

internal sealed class ObjectLocationReferenceEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public List<string> Components { get; set; } = new();

    [YamlMember(Order = 3)]
    public List<string> Locations { get; set; } = new();
}

internal class DropTablePayloadDefinition
{
    [YamlMember(Order = 10)]
    public IntRangeDefinition? Rolls { get; set; }
    [YamlIgnore]
    public int? DropMin { get; set; }
    [YamlIgnore]
    public int? DropMax { get; set; }
    [YamlMember(Order = 11)]
    public float? DropChance { get; set; }
    [YamlMember(Order = 12)]
    public bool? OneOfEach { get; set; }
    [YamlMember(Order = 13)]
    public List<DropEntryDefinition>? Drops { get; set; }
}

internal class DropTableDefinition : DropTablePayloadDefinition
{
}

internal sealed class DamageableDropTableDefinition : DropTableDefinition
{
    [YamlMember(Order = 1)]
    public float? Health { get; set; }
    [YamlMember(Order = 2)]
    public int? MinToolTier { get; set; }
}

internal sealed class DestructibleDefinition
{
    [YamlMember(Order = 1)]
    public float? Health { get; set; }
    [YamlMember(Order = 2)]
    public int? MinToolTier { get; set; }
    [YamlMember(Order = 3)]
    public string? DestructibleType { get; set; }
    [YamlMember(Order = 4)]
    public string? SpawnWhenDestroyed { get; set; }
}

internal sealed class DropEntryDefinition
{
    [YamlMember(Order = 1)]
    public string Item { get; set; } = "";
    [YamlMember(Order = 2)]
    public IntRangeDefinition? Stack { get; set; }
    [YamlIgnore]
    public int? StackMin { get; set; }
    [YamlIgnore]
    public int? StackMax { get; set; }
    [YamlMember(Order = 3)]
    public float? Weight { get; set; }
    [YamlMember(Order = 4)]
    public bool? DontScale { get; set; }
}

internal sealed class PickableDefinition
{
    [YamlMember(Order = 1)]
    public string? OverrideName { get; set; }
    [YamlMember(Order = 2)]
    public PickableDropDefinition? Drop { get; set; }
    [YamlMember(Order = 3)]
    public DropTablePayloadDefinition? ExtraDrops { get; set; }
}

internal sealed class PickableDropDefinition
{
    [YamlMember(Order = 1)]
    public string Item { get; set; } = "";
    [YamlMember(Order = 2)]
    public int? Amount { get; set; }
    [YamlMember(Order = 3)]
    public int? MinAmountScaled { get; set; }
    [YamlMember(Order = 4)]
    public bool? DontScale { get; set; }
}

internal sealed class PickableItemDefinition
{
    [YamlMember(Order = 1)]
    public List<RandomPickableItemDefinition>? RandomDrops { get; set; }
    [YamlMember(Order = 2)]
    public PickableItemDropDefinition? Drop { get; set; }
}

internal sealed class PickableItemDropDefinition
{
    [YamlMember(Order = 1)]
    public string Item { get; set; } = "";
    [YamlMember(Order = 2)]
    public int? Stack { get; set; }
}

internal sealed class RandomPickableItemDefinition
{
    [YamlMember(Order = 1)]
    public string Item { get; set; } = "";
    [YamlMember(Order = 2)]
    public IntRangeDefinition? Stack { get; set; }
    [YamlIgnore]
    public int? StackMin { get; set; }
    [YamlIgnore]
    public int? StackMax { get; set; }
    [YamlMember(Order = 3)]
    public float? Weight { get; set; }
}

internal sealed class FishDefinition
{
    [YamlMember(Order = 1)]
    public DropTablePayloadDefinition? ExtraDrops { get; set; }
}
