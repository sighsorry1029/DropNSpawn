using System.IO;

namespace DropNSpawn;

internal static partial class ExampleContentWriter
{
    private static string ExamplesDirectoryPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, "examples");
    private const string CharacterSampleFileName = "DNS_character.sample.yml";
    private const string ObjectSampleFileName = "DNS_object.sample.yml";
    private const string SpawnerSampleFileName = "DNS_spawner.sample.yml";
    private const string SpawnSystemSampleFileName = "DNS_spawnsystem.sample.yml";

    internal static void EnsureDefaultExampleFiles()
    {
        EnsureExampleFile("README.md", ReadmeContent);
        EnsureExampleFile(CharacterSampleFileName, CharacterConditionContent);
        EnsureExampleFile(ObjectSampleFileName, ObjectConditionContent);
        EnsureExampleFile(SpawnerSampleFileName, SpawnerConditionContent);
        EnsureExampleFile(SpawnSystemSampleFileName, SpawnSystemContent);
    }

    private static void EnsureExampleFile(string fileName, string defaultContent)
    {
        string currentPath = Path.Combine(ExamplesDirectoryPath, fileName);

        if (File.Exists(currentPath))
        {
            return;
        }

        GeneratedArtifactWriter.WriteTextAlways(currentPath, defaultContent);
    }

    private const string ReadmeContent = @"Across all field types, `field: null` or omitting the field usually means the field is unspecified.

More broadly, it falls back to that field's default behavior, and the exact effect depends on the field.

For list, map/object, and string fields respectively, `[]`, `{}`, and `''` explicitly assign an empty list, empty object, or empty string, and their exact effect depends on the field.

# `biomes: then indented - BlackForest and - Mistlands on separate lines` is another supported format for Block list

Range shorthand uses 1, 1~5, and ~5. Note: 1~ currently resolves to 1 because open-ended max is not supported.

faction accepts vanilla Character.Faction names and ExpandWorldFactions custom faction names.
";

    private const string CharacterConditionContent = @"# Character-domain coexistence samples.
# Safe here as a .sample.yml file. Copy rows into the active override file, or rename this file to a DNS_<domain>_*.yml name to load it.
# Character conditions are top-level only.
# Every matching entry for the same prefab contributes drops.
# Identical drop rows are deduped.

- prefab: Boar
  enabled: true
  conditions:
    biomes: [Meadows]
  characterDrop:
    drops:
    - item: LeatherScraps
      amount: 1~2
      chance: 1

- prefab: Boar
  enabled: true
  conditions:
    biomes: [Meadows]
    timeOfDay: [night]
  characterDrop:
    drops:
    - item: Coins
      amount: 1~3
      chance: 0.25

- prefab: Skeleton
  enabled: true
  conditions:
    biomes: [BlackForest]
    requiredGlobalKeys: [defeated_gdking]
  characterDrop:
    drops:
    - item: BoneFragments
      amount: 2~4
      chance: 1
";

    private const string ObjectConditionContent = @"# Object-domain coexistence progression
# Safe here as a .sample.yml file. Copy rows into the active override file, or rename this file to a DNS_<domain>_*.yml name to load it.
# Drop-table style payloads merge matching rows.
# Scalar-style payloads are applied in order, so later matching entries override earlier values.

- prefab: TreasureChest_meadows
  enabled: true
  conditions:
    biomes: [Meadows]
  container:
    rolls: 1~2
    drops:
    - item: Coins
      stack: 10~20
      weight: 1

- prefab: RaspberryBush
  enabled: true
  conditions:
    biomes: [Meadows]
  pickable:
    overrideName: Berry Bush
    drop:
      item: Raspberry
      amount: 3
      minAmountScaled: 1

- prefab: MineRock_Copper
  enabled: true
  conditions:
    locations: [CopperDeposit]
  mineRock:
    rolls: 2~3
    drops:
    - item: CopperOre
      stack: 2~4
      weight: 1
";
}
