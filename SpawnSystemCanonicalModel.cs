using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class CanonicalSpawnSystemEntry
{
    [YamlMember(Order = 0)]
    public string? Prefab { get; set; }

    [YamlMember(Order = 1)]
    public bool Enabled { get; set; } = true;

    [YamlIgnore]
    public SpawnSystemSpawnDefinition? Spawn { get; set; }

    [YamlMember(Alias = "spawnSystem", Order = 2)]
    public SpawnSystemSpawnDefinition? SpawnSystem
    {
        get => Spawn;
        set => Spawn = value;
    }

    [YamlMember(Order = 3)]
    public SpawnSystemConditionsDefinition? Conditions { get; set; }

    [YamlMember(Order = 4)]
    public SpawnSystemModifiersDefinition? Modifiers { get; set; }

    [YamlIgnore]
    public string RuleId { get; set; } = "";

    [YamlIgnore]
    public string? SourcePath { get; set; }

    [YamlIgnore]
    public int? SourceLine { get; set; }

    [YamlIgnore]
    public int? SourceColumn { get; set; }

    [YamlIgnore]
    public string? ReferenceOwnerName { get; set; }
}
