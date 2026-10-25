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

Character-drop handling has been updated for **Valheim 1.0.20**. See the earlier [compatibility notes](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/compatibility-1.0.15.md) for reviewed EWD builds and existing-YAML update notes. Restart after changing the installed mod set; mixed EWD capability between peers is not supported for EWD-dependent configurations.

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

Character drops support `levelMultiplierNotAmount` (default `false`). With both `levelMultiplier: true` and `levelMultiplierNotAmount: true`, stars increase the drop chance without multiplying its amount. Original prefab flags are preserved; YAML overrides use their configured flags.

Under `2 - Character`, `chance-only drop chance scaling` controls these drops regardless of item type, including original game drops when Character Overrides is disabled. The default `Linear` uses `chance-only drop chance increase per star (%)` (integer 0–100, default **50**). Each star adds that percentage of the base chance: with a 10% base, 50 gives 10%, 15%, 20%, while 25 gives 10%, 12.5%, 15%. Zero keeps the base chance at every star level. The final chance is capped at 100%. Select `Vanilla` to ignore this increase setting and keep the game's exponential chance scaling: 10%, 20%, 40%, 80%. These settings change no amounts and are independent of `character loot system`, the additional-loot amount percentages, and `global trophy level multiplier`. The existing CLLC handoff disables DNS chance scaling when CLLC is loaded. Changes apply to subsequent drop rolls and are synchronized from the server; the same rate applies to creatures and bosses.

If multiple rows share a drop prefab and any row has both flags true, DNS retains its existing policy of excluding all those rows from amount scaling. Only the chance-only rows use the selected chance mode; ordinary rows retain their original chance and amount rules. This exception also applies to global trophy scaling and custom stacked/limited drops; other drop prefabs keep their configured DNS scaling. Resource multipliers, `onePerPlayer`, amount limits, and stacking still apply. Update the server and all clients together when using this build.

For `dropInStack`, omitted/`null` inherits `global drop in stack`, explicit `true` enables stacks, and explicit `false` keeps separate drops. The stack blacklist always wins. An explicit YAML `levelMultiplier: false` also opts a trophy row out of global trophy amount scaling; it no longer gets overridden by that option. Original game rows without a YAML override retain their global eligibility. See the [override guide](docs/override-logic-guide.md) for defaults, domain switches and blacklist exceptions.

Successful drop rows retain their own policies even when earlier rows for the same item fail. YAML `amountLimit` is applied before loot is saved, and explicit stack choices are saved with new ragdolls for delayed or instant loot. Old ragdolls without this metadata inherit the current global stack default. Changing YAML affects subsequent drop rolls; changing the global stack default or blacklist affects subsequent loot delivery. Existing user YAML is not rewritten: remove `dropInStack: false` or set it to `null` where global inheritance is intended.

Under `2 - Character`, `monster instant loot drop blacklist` defaults to `Dragon, Hatchling`. These creature prefabs keep normal ragdoll loot timing even when instant loot is enabled. Use comma-separated names, or leave it empty for no exclusions; changes affect new ragdolls.

With EpicLoot, ordinary CharacterDrop loot can still drop instantly, but EpicLoot's additional rewards (including LuckyLoot bonus rolls) stay at normal ragdoll cleanup time. This avoids rolling those rewards twice. If DNS cannot verify/install the optional EpicLoot guard, instant loot falls back to normal timing without changing your settings. EpicLoot reward amounts and its saved ragdoll data are not changed.

## Know before editing

- **SpawnSystem replaces the main world spawn table.** Keep every row you still want across your loaded SpawnSystem files. Unlike the other domains, it is not a set of small patches to existing rows. Valheim's separate AltBiome lists remain native.
- **Custom drop lists replace native loot, not add to it.** Matching Character rules merge their custom drops. Put `conditions` at the entry level, not on individual drop items. Omitted or `null` drops leave existing loot unchanged; `drops: []` explicitly contributes an empty replacement.
- **Spawner rules select one winner.** The most specific passing selector wins; later-loaded entries break ties. Use top-level `locations` to scope a rule to a location.
- **CreatureSpawner cumulative limits are optional.** Use `creatureSpawner.maxTotalSpawns` or `Default CreatureSpawner Max Total Spawns` in the config. `0` adds no limit and preserves one-time behavior; a positive limit counts successful spawns while enabled and stops the spawner without destroying it. See the [spawner guide](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawner-domain-guide.md#cumulative-spawn-limit).
- **Generated files are lookup material.** Do not edit `.reference.yml` or `.full.yml` to change gameplay. Generated `.sample.yml` files in `examples/` are inactive until copied or renamed to a loaded override filename.

Both `.yml` and `.yaml` are supported. Loaded names are `DNS_<domain>` and `DNS_<domain>_*`, with either extension. Use `events` for raids, for example `DNS_events.yml`.

`DNS_dungeon.yml` separately adds **periodic ordinary dungeon spawns** with per-player probability rolls and shared per-rule/per-dungeon living limits. New files include English instructions and a commented example above the empty active list. Creature entries use `prefab`; old `creature` keys must be changed manually. Rules are server-owned; server and clients need the updated DNS build for safe placement checks. The optional CreatureManager 1.2.5 integration honors its boss/Enforcer spawn blockers. See the [dungeon spawn guide](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/dungeon-spawns.md) for the inactive sample, schema and multiplayer behavior.

For dungeon spawn troubleshooting, temporarily set `5 - Diagnostics` → `Dungeon spawn diagnostics = On` in the server's and reproducing clients' local cfg. It defaults to `Off`, is not synchronized, and logs bounded `[DungeonDiag]` summaries every 30 seconds without changing spawn conditions. See the [diagnostic instructions](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/dungeon-spawns.md#diagnosing-missing-spawns).

### Scale spawn intervals by biome

Edit the server's `DNS_spawnintervals.yml` beside the other DNS YAML files:

```yaml
Meadows: 0.5
BlackForest: 2.0
# MyCustomBiome: 0.75
```

Each multiplier applies to the base `spawnInterval` of all loaded normal SpawnSystem rules for that biome, across files. A 60-second Meadows/BlackForest rule becomes a 30-second Meadows row and a 120-second BlackForest row internally. Equal multipliers stay grouped; other conditions, names and prefab limits are retained. Mixed-biome zones can run both timers, so this is not just a faster/slower shared timer.

Omitted biomes use `1.0`; an empty mapping (`{}`), empty file or missing file applies no scaling. Values must be finite and greater than zero. Decimal seconds are preserved (`36 × 0.1 = 3.6`), with a minimum effective interval of 0.01 seconds. Use individual biome names, including registered **EWD custom biome names**, not `All`, `Land`, `None` or numeric masks. Only one policy file is allowed (`.yml` or `.yaml`). Duplicate aliases, unknown names after registration, invalid values and overflowing intervals reject the reload and retain the last accepted configuration.

Changes reload live and are applied once on the server; clients receive the effective rows. Update DNS on **both server and clients**. Event, dungeon, CreatureSpawner/SpawnArea and EWD's separate AltBiome spawn lists are unaffected. Native prefab counting, group-spawn and catch-up behavior remain in use; splitting does not create an independent population allowance per biome or a new strict population cap.

The old per-file `- spawnIntervalMultiplier: ...` header has been removed. Delete it manually; a remaining header rejects the reload. There is no migration. Use per-entry base intervals for exceptions that previously depended on separate files. Generated reference/scaffold rows show source values, not policy-expanded rows. Changing a multiplier or split group changes the affected content-based timer IDs, using the same first-check behavior as editing a spawn rule.

World-spawn timers use internal content-based IDs: no `name` or `timerId` is required, and different rules for the same prefab remain independent. Reordering rows or moving them between files preserves timers when their parsed settings stay the same. New or edited rules use vanilla first-check timing, without an extra full-interval wait; existing saved timers keep their progress. DNS stores these timers together and removes retired records as zones are processed. Old timers are **not migrated or deleted**, so existing save warnings may remain. Event and AltBiome timers are unchanged; update DNS on both the server and all clients.

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
