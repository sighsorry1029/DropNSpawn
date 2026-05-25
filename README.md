# DropNSpawn

Configure Valheim drops, spawns, and boss-location behavior with server-synced YAML.

DropNSpawn is split into five domains. Each domain can be enabled or disabled separately, which makes it easier to coexist with other mods that own the same runtime systems.

## Domains

| Domain | What it controls |
| --- | --- |
| `location` | Boss altars, altar item stands, Vegvisirs, RuneStones, and RuneStone global pins |
| `character` | `CharacterDrop` loot, one-per-player drop counting, drop-in-stack, despawn rules, and boss-tamed pressure |
| `object` | Containers, pickables, pickable items, fish, destructibles, mine rocks, trees, and object drop tables |
| `spawner` | `SpawnArea` and `CreatureSpawner` tables, intervals, caps, level ranges, and location-scoped spawner rules |
| `spawnsystem` | World `SpawnSystem` rows, biome rules, time-of-day rules, global-key gates, and extended spawn data |

`spawnsystem` is a full replacement domain: the loaded rows become the live world spawn table. Keep every spawn row you still want.

## Location
![](https://i.ibb.co/FLPN5q68/altar.png)  
Specific for boss-related locations

- boss altar behavior (Harder boss at night, boss respawn cooldown and so on)
- slot-specific `ItemStand` restrictions (Change offerings)
- Vegvisir target or presentation changes (change icon and the location it points to)
- Hover on altar to see the offerings and boss. (serversync)

## Character
![](https://i.ibb.co/nMZ7gcZR/characterdrop.png)

- Configure creature loot to your liking
- merge multiple conditional loot rows for the same creature (VNEI compatible)
- Check various conditioned examples on the config/DropNSpawn/examples
- Mobs can drop loots in one stack.
- `Loot per person` checks configured range instead of whole world.
## Object
![](https://i.ibb.co/yFhNTP60/objectdrop.png)

- chest loot replacement
- tooltier, health change for trees and rocks
- tree or rock drop changes
- pickable loot changes (bonefiles, fish, berries)
- destructible health and spawn-on-destroy changes
- `DNS_object.locations.reference.yml` exists because many objects are dependent on locations

## Spawner
![](https://i.ibb.co/GQ9bPWb7/spawns.png)

- change spawn tables
- change spawn intervals, trigger distance, caps, level range, respawn time
- apply location-scoped spawner overrides with top-level `location`
- ExpandWorldData compatible
- `DNS_spawner.locations.reference.yml` exists because many spawners are dependent on locations

## SpawnSystem
You can see many vertical lines on above image. Those are SpawnSystems
- biome/world spawn rules
- global-key-gated spawning
- time-of-day spawn rules
- world-level conditional behavior
- ExpandWorldData compatible (Same system with ExpandWorldSpawn just that format is different)
- This domain is authoritative and replaces the live `SpawnSystem` table with the rows you define.
  ![](https://i.ibb.co/wZ4BfJF1/spawnsystem.png)
- Above image is explanation of how spawnsystem works in valheim

## Workflow

1. Open `BepInEx/config/DropNSpawn/`.
2. Use the generated `.reference.yml` files to find real prefab names and current values.
3. Copy only the rows you want to change into `DNS_<domain>.yml` or `DNS_<domain>_*.yml`.
4. Save the YAML file. DropNSpawn reloads loaded YAML at runtime.

Generated samples live in `BepInEx/config/DropNSpawn/examples/`. They are safe examples until you copy them into an active override file or rename them to a loaded `DNS_<domain>_*.yml` file.

## YAML Files

Loaded override files:

- `DNS_<domain>.yml`
- `DNS_<domain>.yaml`
- `DNS_<domain>_*.yml`
- `DNS_<domain>_*.yaml`

Generated helper files:

- `DNS_<domain>.reference.yml` shows current game data and prefab names.
- `DNS_object.locations.reference.yml` shows which location roots contain object prefabs.
- `DNS_spawner.locations.reference.yml` shows location context for spawner rules.
- `DNS_<domain>.full.yml` is an exhaustive scaffold written by `dns:full`; it is not loaded.

Use one primary file per domain when possible. Supplemental files are useful for splitting large configs by biome, progression tier, or feature.

## Reference Updates

`Reference Update Mode` controls generated reference files.

- `AutoUpdate`: creates missing reference files and updates most existing reference files automatically.
- `ManualUpdate`: creates missing reference files, but updates existing reference files only when you run `dns:reference`.

Notes:

- `DNS_spawnsystem.reference.yml` is manual only. Run `dns:reference spawnsystem`.
- `DNS_spawner.locations.reference.yml` is auto-created when missing, but not auto-updated afterward.

## Console Commands

- `dns:reference [object|character|spawner|location|spawnsystem|all]`
  Regenerates reference files.
- `dns:full [object|character|spawner|location|spawnsystem|all]`
  Writes non-loaded full scaffold files.
- `dns:inspect spawner`
  Shows the current or nearest spawner target and resolved location selector context.
- `dns:inspect bossstone`
  Shows per-player boss stone state for the aimed target.
- `dns:bossstone reset <exactPlayerName>`
  Admin command that resets per-player boss stone state for one player.

## Useful Config

Most server-facing settings are synced from the server.

- `Enable Object Overrides`
- `Enable Character Overrides`
- `Enable Spawner Overrides`
- `Enable Location Overrides`
- `Enable SpawnSystem Overrides`
- `Default SpawnArea Max Total Spawns`
- `Afternoon Start Fraction`
- `Enable Runestone Global Pins`
- `Show LocationProxy Offering Bowl Hover Info`
- `Per Player Boss Stones`
- `Remote Forsaken Power Selection`
- `Enable Boss Tamed Pressure`
- `Enable Same Boss Duplicate Block`
- `Default Despawn Range`
- `Default Despawn Delay Seconds`
- `Global Drop In Stack`
- `Drop In Stack Blacklist`
- `One Per Player Nearby Range`
- `One Per Player Nearby Range Living Players Only`

Client-only settings include `Reference Update Mode`, diagnostics toggles, and `Rotate Forsaken Power Shortcut`.

## Compatibility

If another mod fully owns the same system, disable the overlapping DropNSpawn domain instead of stacking both.

- `VNEI`: DropNSpawn character drops are exposed for normal lookup.
- `CLLC`: CLLC effects can be used in supported character and spawn conditions/modifiers.
- `MonsterDB`: overlaps with `character` and `spawnsystem`.
- `Drop That!`: overlaps with `object` and `character`.
- `Spawn That!`: overlaps with `spawner` and `spawnsystem`.
- `Expand World Spawns`: overlaps with `spawnsystem`.
- `Spawner Tweaks`: usually compatible, but disable overlapping DropNSpawn domains or Spawner Tweaks features when both edit the same object, altar, item stand, spawn point, or spawner.

## Helpful Mods

- `ESP` for spawners, spawn points, and object info
- `XRayVision` for object components
- `Infinity Hammer` for placing and removing test objects
