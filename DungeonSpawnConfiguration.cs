using System;
using System.Collections.Generic;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DropNSpawn;

// Server-owned encounter rules; these are not native SpawnSystem table replacements.
internal sealed class DungeonSpawnRule
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> Locations { get; set; } = new();
    public float SpawnInterval { get; set; } = 60;
    public float SpawnChance { get; set; } = 10;
    public int MaxAlive { get; set; } = 3;
    public FloatRangeDefinition SpawnRadius { get; set; } = new() { Min = 6, Max = 12 };
    public List<DungeonSpawnCreature> Creatures { get; set; } = new();
}

internal sealed class DungeonSpawnCreature
{
    public string Prefab { get; set; } = "";
    public float Weight { get; set; } = 1;
}

internal static class DungeonSpawnConfiguration
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance).WithDuplicateKeyChecking().Build();

    internal static List<DungeonSpawnRule> Parse(string yaml)
    {
        List<DungeonSpawnRule> rules = Deserializer.Deserialize<List<DungeonSpawnRule>>(yaml) ?? new();
        Validate(rules);
        return rules;
    }

    internal static void Validate(List<DungeonSpawnRule> rules)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (DungeonSpawnRule rule in rules)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 128 || rule.Id != rule.Id.Trim())
                throw new FormatException("Dungeon rules require a nonblank, unpadded id of at most 128 characters.");
            if (!ids.Add(rule.Id)) throw new FormatException($"Duplicate dungeon rule id '{rule.Id}'.");
            if (rule.Locations == null || rule.Locations.Count == 0 || rule.Locations.Exists(string.IsNullOrWhiteSpace))
                throw new FormatException($"Dungeon rule '{rule.Id}' requires nonempty locations.");
            if (!Finite(rule.SpawnInterval) || rule.SpawnInterval < 1 ||
                !Finite(rule.SpawnChance) || rule.SpawnChance < 0 || rule.SpawnChance > 100 ||
                rule.MaxAlive < 1 || rule.MaxAlive > 100)
                throw new FormatException($"Dungeon rule '{rule.Id}': interval must be >= 1 second, chance 0..100%, maxAlive 1..100.");
            FloatRangeDefinition range = rule.SpawnRadius;
            if (range == null || !range.Min.HasValue || !range.Max.HasValue ||
                !Finite(range.Min.Value) || !Finite(range.Max.Value) || range.Min < 2 || range.Max < range.Min || range.Max > 64)
                throw new FormatException($"Dungeon rule '{rule.Id}': spawnRadius must be a finite range within 2..64 meters.");
            if (rule.Creatures == null || rule.Creatures.Count == 0)
                throw new FormatException($"Dungeon rule '{rule.Id}' requires creatures.");
            double total = 0;
            foreach (DungeonSpawnCreature creature in rule.Creatures)
            {
                if (creature == null || string.IsNullOrWhiteSpace(creature.Prefab) || !Finite(creature.Weight) || creature.Weight <= 0)
                    throw new FormatException($"Dungeon rule '{rule.Id}' requires nonempty prefab names and positive finite weights in creatures.");
                total += creature.Weight;
            }
            if (total > float.MaxValue) throw new FormatException($"Dungeon rule '{rule.Id}' weight sum is too large.");
        }
    }

    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    internal static readonly string DefaultContent = """
# DropNSpawn periodic dungeon encounters.
# Active filenames: DNS_dungeon.yml/.yaml or DNS_dungeon_<name>.yml/.yaml.
# Rules are read on the server/local host. Install the updated DNS on every client too.
# locations contains exact location prefab names, not individual dungeon room names.
# Each interval rolls spawnChance independently for each living player in that dungeon.
# maxAlive is shared by this rule ID and dungeon instance, across all listed prefabs.
# It counts this rule's living creatures (including unloaded ones) and pending spawns.
# Vanilla, EWP, Enforcers and other rule IDs do not count toward this rule's limit.
# Keep id stable to retain saved creature attribution across reloads and restarts.
# weight is a relative choice weight after a successful roll, not a percentage.
# A 100% chance still requires a safe floor, clearance, reachability and a free cap slot.
# CreatureManager, when installed, must support the spawn API; its boss/Enforcer blocks apply.
#
# The active list starts empty: installing this file does not enable any encounters.
# To enable the example, remove [] at the bottom and remove the leading '# ' from
# the example lines below. Or copy the rule into a DNS_dungeon_<name>.yml file.
# Existing configuration and sample files are not overwritten.
#
# Example (commented out):
""" + "\n# " + Example.Replace("\r\n", "\n").Replace("\n", "\n# ") + "\n\n[]\n";

    internal const string Sample = """
# Inactive example. Copy into DNS_dungeon.yml or rename to DNS_dungeon_crypt.yml.
# Remove an existing [] before pasting rules into an empty configuration.
# Server/local-host files only. Each interval rolls once per living player in the dungeon.
# id is persisted on spawned creatures: keep it stable across reloads and restarts.
""" + "\n" + Example;

    private const string Example = """
- id: sunkencrypt_ambush
  enabled: true
  locations: [SunkenCrypt4]
  spawnInterval: 60      # seconds; first check after one interval
  spawnChance: 10        # 0..100 percent per player, independent of the creature weights
  maxAlive: 3            # this rule's living creatures + reservations, shared per dungeon
  spawnRadius: 6~12      # horizontal distance from the selected player, meters
  creatures:
    - prefab: BlobElite
      weight: 2
    - prefab: DamnedOne_TW  # requires the supplying mod on server and clients
      weight: 1
""";
}
