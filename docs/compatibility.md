# Using DropNSpawn with other mods

[Back to DropNSpawn](../README.md)

Choose which mod manages each feature. A mod being installed is not, by itself, a reason to turn DNS features off. DNS does not automatically select these switches for you.

Edit the existing values under `4 - Domains` in `BepInEx/config/sighsorry.DropNSpawn.cfg`. In multiplayer, use the server's config. Change `On` to `Off`; lines starting with `#` are comments, not active settings. BepInEx regenerates comments when saving, so personal notes in the cfg are not preserved.

## Feature-by-feature settings

| Other mod | What you use it for | DNS setting |
| --- | --- | --- |
| [MonsterDB](https://github.com/RustyMods/MonsterDB/blob/main/MonsterDB/Wiki/Base.md) | Creature stats or appearance, while DNS handles loot and spawns | Keep the DNS domains you use on |
| [MonsterDB](https://github.com/RustyMods/MonsterDB/blob/main/MonsterDB/Wiki/CharacterDrop.md) | Creature drop tables | `Enable Character Overrides = Off` |
| MonsterDB | Normal world spawn rules (`SpawnData`) | `Enable SpawnSystem Overrides = Off` |
| MonsterDB | Local `CreatureSpawner` or `SpawnArea` settings | `Enable Spawner Overrides = Off` |
| [Drop That!](https://github.com/ASharpPen/Valheim.DropThat/wiki#how-does-it-work) | Creature loot | `Enable Character Overrides = Off` |
| Drop That! | Chest or other object loot | `Enable Object Overrides = Off` |
| [Spawn That!](https://asharppen.github.io/Valheim.SpawnThat/configs/general/overview.html) | Normal world spawns | `Enable SpawnSystem Overrides = Off` |
| Spawn That! | Local spawners or SpawnArea | `Enable Spawner Overrides = Off` |
| Expand World Data | World generation, custom biomes and AltBiome lists alongside DNS | Keep the DNS domains you use on; see below |
| Custom Raids | Raid definitions or scheduling | `Enable Event Overrides = Off`, plus the event settings below |

Turn off both related domains only if the other mod manages both features. For example, Spawn That! world spawning does not require turning off DNS's local Spawner domain.

Turning a domain off keeps its YAML files and removes DNS's overrides using the captured baseline. That baseline may already contain other mods' changes; it is not guaranteed to be an untouched vanilla table. These settings reduce overlapping control, not a guarantee that every load order or live reload combination is compatible.

Character Overrides is not a master switch for every loot feature. Global loot scaling, instant ragdoll loot and the player-aligned killer filter have separate settings under `2 - Character`. Turn those off or select their vanilla modes too if you want the other mod to control all loot behavior. [Character rules and exceptions](override-logic-guide.md#character).

## Expand World Data

EWD is optional. When both mods are installed, use DNS YAML for normal world spawns, raids and drops. DNS suppresses EWD's overlapping Spawn/Event/Drop processing at runtime without changing EWD's cfg or YAML files.

**Turning a DNS domain off does not turn the matching EWD feature back on.** You do not need to disable DNS domains merely because EWD is installed.

EWD continues to manage world generation, custom biomes and separate AltBiome spawn lists. Supported AltBiome spawn payloads such as `data`, `fields`, `objects` and `faction` remain available; EWD per-spawn `drops` are not retained by this integration. DNS biome interval multipliers do not change these separate AltBiome lists.

Compatible EWD builds also provide DNS spawn `data`/`fields`/`objects`, event `startCommands`/`endCommands` and custom biome names. Without EWD, nonempty extension settings reject the affected configuration update and keep the last accepted configuration; they are not silently ignored. Empty extension fields are allowed. Basic DNS features and native faction overrides work without EWD.

Use matching mod builds on the server and clients, and restart after changing the installed mod set. API compatibility is checked; a future version is not automatically guaranteed to work. [Reviewed builds and integration details](compatibility-ewd-1.73.md).

## Custom Raids

DNS's Event switch controls YAML overrides, not the separate global event policies. If Custom Raids manages raids, start with:

| DNS setting | Value |
| --- | --- |
| `4 - Domains / Enable Event Overrides` | `Off` |
| `3 - Events / Event scheduling mode` | `Vanilla` |
| `3 - Events / Default event player base` | `Off` |
| `3 - Events / Event duration multiplier` | `1` |
| `3 - Events / Random event chance` | Match Custom Raids `EventTriggerChance` |
| `3 - Events / Random event interval` | Match Custom Raids `EventCheckInterval`; both are in minutes |

Do not set DNS's duration multiplier or event chance to zero as a way to hand control over: those values can disable events. Custom Raids individual raid checks use its own per-raid chances.

The supplied Custom Raids 1.8.2 DLL and DNS both write the game's event interval/chance fields. DNS can also restore its captured event list during a reload, even with Event Overrides off. The settings above reduce overlap but are not a complete handoff or a verified live-reload compatibility patch. Check the effective raid list after startup and after changes in your actual mod setup.

Custom Raids' event spawns do not, by themselves, require disabling DNS's normal SpawnSystem or local Spawner domains. This guidance comes from code inspection, not a completed multiplayer test.

## Other integrations

- **CLLC:** DNS's scaling handoff is enabled by default. With Creature Level & Loot Control installed, DNS's own amount/chance scaling is inactive, while YAML drop definitions, stacking, instant loot and nearby OnePerPlayer adjustments remain available.
- **EpicLoot:** ordinary CharacterDrop loot can drop instantly; EpicLoot's additional rewards, including LuckyLoot rolls, stay at normal ragdoll cleanup time. If DNS cannot verify or install its guard, instant loot falls back to normal timing without rewriting settings. EpicLoot reward quantities and saved reward data are unchanged.
- **VNEI / ESP:** VNEI can display configured character drops; ESP can inspect spawns and objects.
- **CreatureManager:** dungeon spawning honors its supported boss/Enforcer blockers. See the [dungeon guide](dungeon-spawns.md).
- **BossRules / UsefulRunestones:** boss altars and boss-management features belong to BossRules; runestone pins and Vegvisir rewards belong to UsefulRunestones. DNS retains location lookup for Object and Spawner selectors.

## More World Locations and reference exports

Automatic Object/Spawner references skip MWL location interiors to avoid loading every bundle. Registered prefabs and override rules remain usable; partial exports are labeled. If the MWL manifest is unavailable, automatic interior scanning is deferred for all locations.

Manual `dns:reference object`, `dns:reference spawner` and `dns:full spawner` may load all location interiors, take minutes and use substantial memory. Avoid these full exports on low-memory clients. [Reference commands](override-logic-guide.md#files-and-references).
