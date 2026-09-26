# DropNSpawn

Customize Valheim's drops, loot, spawners, world spawning, and raids with YAML. Use generated references to find prefab names and values, then edit only what you need.

| Character drops | Object loot | Spawners |
| --- | --- | --- |
| ![Character drop configuration](https://i.ibb.co/nMZ7gcZR/characterdrop.png) | ![Object drop configuration](https://i.ibb.co/yFhNTP60/objectdrop.png) | ![Spawner and world spawn checks](https://i.ibb.co/GQ9bPWb7/spawns.png) |

![How Valheim world spawning works](https://i.ibb.co/wZ4BfJF1/spawnsystem.png)

## What you can change

| Domain | Controls |
| --- | --- |
| `character` | Creature drops, quantities, chances, level scaling, and stacked loot |
| `object` | Chests, pickables, fish, trees, rocks, destructibles, and their loot |
| `spawner` | SpawnArea and CreatureSpawner tables, intervals, limits, levels, and location rules |
| `spawnsystem` | World spawn tables, biomes, time of day, and progression requirements |
| `events` | Raid definitions, spawn lists, conditions, and scheduling |

## Install

Install the same DropNSpawn version and its dependencies on the server and all clients. A mod manager is the simplest option; for manual installation, place `DropNSpawn.dll` in `BepInEx/plugins/`.

Requires **BepInExPack Valheim 5.4.2351**. **Expand World Data is optional**: DNS's basic drops, spawners, world spawning, raids, and native faction overrides work without it. Install EWD **1.71 or newer** on the server and clients to use spawn `data`/`fields`/`objects`, event `startCommands`/`endCommands`, or EWD custom biomes. Without EWD, nonempty extension settings reject the affected domain's configuration update and retain its previous accepted configuration; they are not silently ignored. Empty extension fields are allowed.

The current target is **Valheim 1.0.15**; see the [compatibility notes](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/compatibility-1.0.15.md) for reviewed EWD builds and existing-YAML update notes. Restart after changing the installed mod set; mixed EWD capability between peers is not supported for EWD-dependent configurations.

## Quick start

1. Start the game or server and load a world once to generate the configuration files.
2. Open `BepInEx/config/DropNSpawn/` and find the relevant `DNS_<domain>.reference.yml`.
3. Copy the entries you want to customize into `DNS_<domain>.yml`, or a supplemental file such as `DNS_character_wolves.yml`.
4. Save. Loaded YAML files reload at runtime; on a multiplayer server, edit the server's files.

For example, put this in `DNS_character.yml` to replace Lox loot:

```yaml
- prefab: Lox
  characterDrop:
    drops:
    - item: LoxMeat
      amount: 4~6
    - item: LoxPelt
      amount: 2~3
    - item: TrophyLox
      chance: 0.1
      levelMultiplier: false
```

Global options live in `BepInEx/config/sighsorry.DropNSpawn.cfg`. All five domains start enabled; toggle them under `4 - Domains`. Loot scaling, stacked drops, trophy scaling, and event scheduling have their own settings; most server-facing settings sync from the server.

Under `2 - Character`, `monster instant loot drop blacklist` defaults to `Dragon, Hatchling`. These creature prefabs keep normal ragdoll loot timing even when instant loot is enabled. Use comma-separated names, or leave it empty for no exclusions; changes affect new ragdolls.

## Know before editing

- **SpawnSystem replaces the main world spawn table.** Keep every row you still want across your loaded SpawnSystem files. Unlike the other domains, it is not a set of small patches to existing rows. Valheim's separate AltBiome lists remain native.
- **Custom drop lists replace native loot, not add to it.** Matching Character rules merge their custom drops. Put `conditions` at the entry level, not on individual drop items. Omitted or `null` drops leave existing loot unchanged; `drops: []` explicitly contributes an empty replacement.
- **Spawner rules select one winner.** The most specific passing selector wins; later-loaded entries break ties. Use top-level `locations` to scope a rule to a location.
- **CreatureSpawner cumulative limits are optional.** Use `creatureSpawner.maxTotalSpawns` or `Default CreatureSpawner Max Total Spawns` in the config. `0` adds no limit and preserves one-time behavior; a positive limit counts successful spawns while enabled and stops the spawner without destroying it. See the [spawner guide](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawner-domain-guide.md#cumulative-spawn-limit).
- **Generated files are lookup material.** Do not edit `.reference.yml` or `.full.yml` to change gameplay. Generated `.sample.yml` files in `examples/` are inactive until copied or renamed to a loaded override filename.

Both `.yml` and `.yaml` are supported. Loaded names are `DNS_<domain>` and `DNS_<domain>_*`, with either extension. Use `events` for raids, for example `DNS_events.yml`.

### Scale spawn intervals per file

Place this optional header before all prefab entries in a SpawnSystem override file:

```yaml
- spawnIntervalMultiplier: 2.0

- prefab: Deer
  spawnSystem:
    spawnInterval: 36
```

This sets an effective interval of **72 seconds**. `0.5` halves intervals; `2.0` doubles them. The multiplier must be finite and greater than zero, defaults to `1.0`, and affects only that file—not event spawns. Decimal intervals are preserved. Keep the other world spawn rows you want; this is only a header example.

## References and commands

Reference files are generated automatically and refreshed when source data changes. Object and Spawner also provide `.locations.reference.yml` files for location lookup.

| Console command | Purpose |
| --- | --- |
| `dns:reference [object\|character\|spawner\|spawnsystem\|events\|all]` | Regenerate reference files |
| `dns:full [object\|character\|spawner\|spawnsystem\|all]` | Write exhaustive, non-loaded `.full.yml` scaffolds |
| `dns:inspect spawner` | Inspect the current or nearest spawner and its location context |

Events use `DNS_events.reference.yml` instead of a separate full scaffold.

**Using More World Locations AIO?** Automatic Object/Spawner references skip MWL location interiors to avoid loading every bundle. Registered prefabs and override rules remain usable, and partial exports are labeled. If the MWL manifest is unavailable, automatic interior scanning is deferred for all locations.

Manual `dns:reference object`, `dns:reference spawner`, and `dns:full spawner` can load all location interiors, take minutes, and use substantial memory. Avoid these full exports on low-memory clients.

## Compatibility

- **Expand World Data 1.73:** DNS manages normal world spawns, raids, and loot. EWD's corresponding Spawn/Event/Drop features are suppressed at runtime without changing its settings or YAML. World and AltBiome data remain EWD-owned, including AltBiome spawn `data`, `fields`, `objects`, and `faction`. DNS domain Off restores DNS's baseline; it does not enable EWD's overlapping features. Install matching builds on the server and clients and restart after changing the mod set. See the [integration notes](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/compatibility-ewd-1.73.md).
- **VNEI** can display configured character drops. **ESP** is useful for inspecting spawns and objects.
- **Creature Level & Loot Control:** DNS's own character loot scaling is inactive by default when CLLC is installed. YAML overrides, stacked drops, instant loot, and OnePerPlayer range still work.
- If another mod owns the same system, disable the overlapping DNS domain: MonsterDB (`character`, `spawnsystem`), Drop That! (`object`, `character`), Spawn That! (`spawner`, `spawnsystem`), or Expand World Spawns (`spawnsystem`).
- Boss altar and boss-management features belong to **BossRules**; runestone pins and Vegvisir rewards belong to **UsefulRunestones**. DropNSpawn retains location lookup for Object and Spawner selectors.

## Guides and support

- [Override rules and conditional drops](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/override-logic-guide.md)
- [Spawner configuration](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawner-domain-guide.md)
- [Omitted, null, and empty YAML values](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/yaml-null-empty-guide.md)
- [Build and validation guide](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/development.md)
- [Changelog](https://github.com/sighsorry1029/DropNSpawn/blob/main/Thunderstore/CHANGELOG.md) · [Source and issues](https://github.com/sighsorry1029/DropNSpawn) · [Discord](https://discord.com/invite/VFRJcPwUdm)
