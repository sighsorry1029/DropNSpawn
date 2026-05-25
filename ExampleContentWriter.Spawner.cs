namespace DropNSpawn;

internal static partial class ExampleContentWriter
{
    private const string SpawnerConditionContent = @"# Spawner-domain coexistence samples.
# Safe here as a .sample.yml file. Copy rows into the active override file, or rename this file to a DNS_<domain>_*.yml name to load it.
# Use top-level `location` to scope one location-bound spawner.
# Only the most specific passing entry is applied.

- prefab: Spawner_GreydwarfNest
  enabled: true
  spawnArea:
    spawnInterval: 30
    maxNear: 4
    creatures:
    - creature: Greydwarf
      weight: 1

- prefab: Spawner_GreydwarfNest
  enabled: true
  location: Greydwarf_camp1
  spawnArea:
    spawnInterval: 15
    maxNear: 6
    creatures:
    - creature: Greydwarf_Elite
      weight: 1

- prefab: Spawner_Boar
  enabled: true
  creatureSpawner:
    creature: Boar
    level: 1~2
    spawnCheckInterval: 5
    triggerDistance: 60
";
}
