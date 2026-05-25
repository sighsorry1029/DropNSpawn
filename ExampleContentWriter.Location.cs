namespace DropNSpawn;

internal static partial class ExampleContentWriter
{
    private const string LocationConditionContent = @"# Location-domain coexistence samples.
# Safe here as a .sample.yml file. Copy rows into the active override file, or rename this file to a DNS_<domain>_*.yml name to load it.
# Location conditions are static location filters only: biomes, altitude, distanceFromCenter, inForest, and inDungeon.
# Ranges may use inline form such as `distanceFromCenter: 1000~4000`.
# offeringBowl, itemStands, vegvisirs, and runestones use sequential override.

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

- prefab: SwampRuin1
  enabled: true
  vegvisirs:
  - path: null
    locations:
    - locationName: Vendor_BlackForest
      showMap: true

- prefab: Runestone_Greydwarfs
  enabled: true
  runestones:
  - path: null
    topic: '$tutorial_greydwarfs_topic'
    label: '$tutorial_greydwarfs'
    text: '$tutorial_greydwarfs_text'
    randomTexts: []
";
}
