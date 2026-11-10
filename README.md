# DropNSpawn

Configure Valheim's loot, spawners, world spawns and raids with YAML. Generated reference files show the prefab names and settings available in your mod setup.

| Character drops | Object loot | Spawners |
| --- | --- | --- |
| ![Character drop configuration](https://i.ibb.co/nMZ7gcZR/characterdrop.png) | ![Object drop configuration](https://i.ibb.co/yFhNTP60/objectdrop.png) | ![Spawner and world spawn checks](https://i.ibb.co/GQ9bPWb7/spawns.png) |

![How Valheim world spawning works](https://i.ibb.co/wZ4BfJF1/spawnsystem.png)

## Get started

Install **BepInExPack Valheim 5.4.2360** and the same DropNSpawn version on the server and all clients. Expand World Data is optional. For manual installation, put `DropNSpawn.dll` in `BepInEx/plugins/`.

1. Load a world once to generate files in `BepInEx/config/DropNSpawn/`.
2. Find the settings you need in `DNS_<domain>.reference.yml`.
3. Edit the matching `DNS_<domain>.yml`. Reference files are for lookup, not editing.
4. Save to reload. In multiplayer, edit the server's YAML files.

Global settings and domain switches are in `BepInEx/config/sighsorry.DropNSpawn.cfg`. All five domains start enabled; switches are under `4 - Domains`.

## Config features

You can use these settings without writing custom YAML. In multiplayer, change gameplay settings on the server; dungeon diagnostics are local to each machine.

| Section | What you can do |
| --- | --- |
| `1 - General` | Block global-key-gated world spawn rules in lower-tier biomes. Set cumulative limits for local spawners, or give zero-respawn CreatureSpawners a respawn delay. These options are off by default. |
| `2 - Character` — loot scaling | Adjust extra loot per star separately for creatures and bosses, choose linear or vanilla chance-only scaling, and optionally scale trophy quantities. [Defaults and examples](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/override-logic-guide.md#character-config-options). |
| `2 - Character` — loot delivery | Drop loot before the ragdoll disappears, or deliver stackable items in stacks. Both default to Off and have separate blacklists; `Dragon, Hatchling` keep normal loot timing by default. |
| `2 - Character` — eligible kills | Optionally require a player-aligned killer for creature loot, with creature exceptions. Players, tame allies and the Players/PlayerSpawned factions qualify; supported lethal damage-over-time is tracked too. |
| `2 - Character` — nearby rewards | For drops already marked `onePerPlayer`, count living players within 32 m by default. Set `OnePerPlayer drop check range` to `0` for the native server-wide count. |
| `3 - Events` | Tune raid chance, interval, duration and player-base conditions. Choose one active random event, multiple events with a shared check, or checks per player; set minimum spacing between events. [Modes and defaults](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/override-logic-guide.md#event-config-options). |
| `4 - Domains` | Enable or disable YAML systems separately. Some global loot and event options remain active when their YAML domain is off; see compatibility below. |
| `5 - Diagnostics` | Temporarily log why dungeon spawn attempts are blocked. Off by default; enable on both server and affected clients. |

The progression filter is not a blanket ban on high-tier creatures from every source. [Filter scope](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawnsystem-guide.md#progression-spawn-filter) · [Local spawner limits](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawner-domain-guide.md).

## Choose what to change

| File | What it controls | Guide |
| --- | --- | --- |
| `DNS_character.yml` | Creature loot, chances, amounts and drop conditions | [Drops](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/override-logic-guide.md#character) |
| `DNS_object.yml` | Loot and settings for chests, pickables, trees, rocks and other objects | [Objects](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/override-logic-guide.md#object) |
| `DNS_spawner.yml` | SpawnArea and CreatureSpawner settings, including lifetime spawn limits | [Spawners](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawner-domain-guide.md) |
| `DNS_spawnsystem.yml` | Normal world spawns, biomes, time of day and progression requirements | [World spawns](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawnsystem-guide.md) |
| `DNS_events.yml` | Raid definitions, conditions and creatures | [Events](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/override-logic-guide.md#events) |
| `DNS_spawnintervals.yml` | World spawn interval multipliers by biome, including EWD custom biomes | [Interval multipliers](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawnsystem-guide.md#interval-multipliers-by-biome) |
| `DNS_dungeon.yml` | Additional periodic dungeon spawns | [Dungeons and diagnostics](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/dungeon-spawns.md) |

**Before editing:** SpawnSystem YAML replaces the main world spawn table: keep every rule you still want. Custom Character/Object drop lists replace the matching original loot; they do not simply add items. [Examples, file rules and reference commands](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/override-logic-guide.md).

## Using other mods

Choose which mod manages each feature. Installing another mod does not mean you must disable every related DNS domain.

- **Expand World Data:** keep using DNS for normal world spawns, raids and drops. EWD handles world generation, custom biomes and its separate AltBiome spawn lists.
- **MonsterDB, Drop That!, Spawn That! and Custom Raids:** each domain's config description explains when to turn it off. Custom Raids also needs attention to DNS's separate event settings.

See [mod compatibility and settings](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/compatibility.md) for feature-by-feature guidance, EpicLoot/CLLC behavior and More World Locations reference-export precautions.

## Help

[Changelog](https://github.com/sighsorry1029/DropNSpawn/blob/main/Thunderstore/CHANGELOG.md) · [Source and issues](https://github.com/sighsorry1029/DropNSpawn) · [Discord](https://discord.com/invite/VFRJcPwUdm)
