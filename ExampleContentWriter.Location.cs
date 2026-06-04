namespace DropNSpawn;

internal static partial class ExampleContentWriter
{
    private const string LocationConditionContent = @"# Location-domain coexistence samples.
# Safe here as a .sample.yml file. Copy rows into the active override file, or rename this file to a DNS_<domain>_*.yml name to load it.
# Location conditions are static location filters only: biomes, altitude, distanceFromCenter, inForest, and inDungeon.
# Ranges may use inline form such as `distanceFromCenter: 1000~4000`.
# offeringBowl and itemStands use sequential override.

- prefab: Eikthyrnir
  enabled: true
  conditions:
    biomes: [Meadows]
  offeringBowl:
    respawnMinutes: 2
    alertOnSpawn: true

- prefab: StartTemple
  enabled: true
  itemStands:
  - path: null
    canBeRemoved: false
    autoAttach: true

- vegvisirGlobalEffects:
  - All:
    - Rested, 1500, 1800, 10
  - Meadows:
    - Slimed, 10, 5, 1, vfx_StaminaUpgrade
    - DNS_ClearStatus, 0, 300, 1
  - Swamp:
    - GP_Bonemass, 0, -1

";
}
