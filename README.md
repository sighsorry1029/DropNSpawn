# DropNSpawn

Configure object and creature drops, object loot, spawners, and world spawning. Add stacked drops, level-scaled trophies, VNEI support, and location-scoped spawner rules.
## Domains

| Domain | What it controls |
| --- | --- |
| `character` | `CharacterDrop` loot, one-per-player drop counting, and drop-in-stack |
| `object` | Containers, pickables, pickable items, fish, destructibles, mine rocks, trees, and object drop tables |
| `spawner` | `SpawnArea` and `CreatureSpawner` tables, intervals, caps, level ranges, and location-scoped spawner rules |
| `spawnsystem` | World `SpawnSystem` rows, biome rules, time-of-day rules, global-key gates, and extended spawn data |

`spawnsystem` is a full replacement domain: the loaded rows become the live world spawn table. Keep every spawn row you still want.

## Location
DropNSpawn no longer owns a user-editable `DNS_location.yml` domain. Boss altar, altar `ItemStand`, same-boss duplicate blocking, boss despawn, and boss-tamed pressure rules live in the standalone `BossRules` mod. DropNSpawn keeps internal location lookup helpers for object and spawner `locations:` selectors.

RuneStone global pins and Vegvisir rewards live in the standalone `UsefulRunestones` mod.

## Character
![](https://i.ibb.co/nMZ7gcZR/characterdrop.png)

- configure creature loot
- merge multiple conditional loot rows for the same creature
- VNEI-compatible character drop display
- drop creature loot in one stack when configured
- `onePerPlayer` can count nearby living players within the configured range

## Object
![](https://i.ibb.co/yFhNTP60/objectdrop.png)

- chest loot replacement
- tool-tier and health changes for trees and rocks
- tree or rock drop changes
- pickable loot changes (bone piles, fish, berries)
- destructible health and spawn-on-destroy changes
- `DNS_object.locations.reference.yml` exists because many objects are dependent on locations

## Spawner
![](https://i.ibb.co/GQ9bPWb7/spawns.png)

- change spawn tables
- change spawn intervals, trigger distance, caps, level range, respawn time
- apply location-scoped spawner overrides with top-level `locations`
- ExpandWorldData compatible
- `DNS_spawner.locations.reference.yml` exists because many spawners are dependent on locations

## SpawnSystem
The vertical lines in the spawner image are world `SpawnSystem` checks.

- biome/world spawn rules
- global-key-gated spawning
- time-of-day spawn rules
- world-level conditional behavior
- ExpandWorldData compatible
- This domain is authoritative and replaces the live `SpawnSystem` table with the rows you define.
  ![](https://i.ibb.co/wZ4BfJF1/spawnsystem.png)
- Above image explains how Valheim world spawning works.

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

Reference files are generated lookup snapshots. Missing reference files are created automatically, and existing reference files are updated automatically when their source game data changes.

Notes:

- `DNS_spawnsystem.reference.yml` is generated from vanilla and upstream mod SpawnSystem data. `DNS_spawnsystem.yml` full overrides are not used as reference source data.
- `DNS_object.locations.reference.yml` and `DNS_spawner.locations.reference.yml` are generated lookup files and are also kept up to date automatically.

## Console Commands

- `dns:reference [object|character|spawner|spawnsystem|all]`
  Regenerates reference files.
- `dns:full [object|character|spawner|spawnsystem|all]`
  Writes non-loaded full scaffold files.
- `dns:inspect spawner`
  Shows the current or nearest spawner target and resolved location selector context.

## Useful Config

Most server-facing settings are synced from the server.

Domain toggles live under `4 - Domains`:

- `Enable Object Overrides`
- `Enable Character Overrides`
- `Enable Spawner Overrides`
- `Enable SpawnSystem Overrides`

General and character settings include:

- `Lock Configuration`
- `Default SpawnArea Max Total Spawns`
- `OnePerPlayer drop check range`
- `character loot system`
- `chance for additional loot per star for creatures`
- `chance for additional loot per star for bosses`
- `global drop in stack`
- `global drop in stack blacklist`
- `global trophy level multiplier`
- `global trophy level multiplier blacklist`

`character loot system = CalculateChance` keeps each successful character-drop chance intact and scales the final item amount by the configured per-star chance. Trophy drops join that same linear scaling only when `global trophy level multiplier` is on.

Boss altar rules, same boss duplicate blocking for altars and `CreatureSpawner`, per-player boss stones, remote Forsaken Power selection, boss despawn, and boss tamed pressure live in BossRules.

## Compatibility

If another mod fully owns the same system, disable the overlapping DropNSpawn domain instead of stacking both.

- `VNEI`: DropNSpawn character drops are exposed for normal lookup.
- `MonsterDB`: overlaps with `character` and `spawnsystem`.
- `Drop That!`: overlaps with `object` and `character`.
- `Spawn That!`: overlaps with `spawner` and `spawnsystem`.
- `Expand World Spawns`: overlaps with `spawnsystem`.

## Helpful Mods

- `ESP` for spawners, spawn points, and object info
- `XRayVision` for object components
- `Infinity Hammer` for placing and removing test objects

## GitHub
https://github.com/sighsorry1029/DropNSpawn
