namespace DropNSpawn;

internal static partial class ExampleContentWriter
{
    private const string SpawnSystemConditionContent = @"# SpawnSystem-domain coexistence samples.
# Safe here as a .sample.yml file. Copy rows into DNS_spawnsystem.yml one by one.
# IMPORTANT: spawnsystem overrides replace the live world spawn table with the rows you load.
# Every loaded entry becomes its own native SpawnSystem row.

- prefab: Boar
  enabled: true
  spawnSystem:
    name: Boar meadows day
    spawnInterval: 150
    spawnChance: 50
    groupSize: 1~3
    groupRadius: 5
  conditions:
    altitude: 0~1000
    biomes: [Meadows]
    biomeAreas: [Edge, Median]
    timeOfDay: [day]

- prefab: Neck
  enabled: true
  spawnSystem:
    name: Neck rain
    spawnInterval: 120
    spawnChance: 40
    groupSize: 2~4
    groupRadius: 5
  conditions:
    altitude: -1.5~0.5
    biomes: [Meadows]
    requiredEnvironments: [Rain, LightRain]

- prefab: Greydwarf
  enabled: true
  spawnSystem:
    name: Greydwarf night
    spawnInterval: 120
    spawnChance: 30
    groupSize: 2~3
  conditions:
    biomes: [BlackForest]
    timeOfDay: [night]
  modifiers:
    faction: ForestMonsters
";
}
