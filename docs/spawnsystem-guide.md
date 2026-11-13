# World spawn guide

[Back to DropNSpawn](../README.md) · [Mod compatibility](compatibility.md)

`DNS_spawnsystem.yml` controls normal world spawns. SpawnArea, CreatureSpawner, raids and dungeon rules have separate files and systems.

## Keep the complete table

SpawnSystem YAML replaces the main world spawn table. Keep every rule you want across your loaded files; this domain is not a set of small edits to existing rows. Valheim's separate AltBiome spawn lists are outside this replacement.

Use `DNS_spawnsystem.reference.yml` to find source values. Both `.yml` and `.yaml` work, including supplemental files such as `DNS_spawnsystem_mistlands.yml`. Reference and full-scaffold files are not loaded as overrides.

On multiplayer servers, edit the server files and install the same DNS build on every client. Accepted changes reload live. Invalid updates retain the last accepted configuration.

## Progression spawn filter

`1 - General / Disable global key SpawnSystem entries in low tier biomes` defaults to `Off`. With `On` and SpawnSystem Overrides enabled, DNS excludes normal world spawn rules that have a nonempty `requiredGlobalKey` and include Meadows, BlackForest, Swamp, Mountain or Plains. It filters the whole matching source rule, including a rule that also lists other biomes.

This can prevent progression-unlocked creatures from appearing in earlier biomes, but it is not a night-only filter or a universal creature blacklist. Rules without that global-key requirement, raids, local spawners, dungeon rules and separate AltBiome lists are not disabled by this option. Change the server setting and use matching DNS builds on clients.

## Interval multipliers by biome

Edit `DNS_spawnintervals.yml` beside your other DNS YAML files. In a newly generated file, remove the `{}` line and uncomment or add only the entries you want to change. For example:

```yaml
Meadows: 0.5
BlackForest: 2.0
# MyCustomBiome: 0.75
```

These multipliers apply to the base `spawnInterval` of normal SpawnSystem rules across all loaded files. Smaller values mean more frequent checks, not guaranteed spawns.

A 60-second rule covering Meadows and BlackForest becomes a 30-second Meadows rule and a 120-second BlackForest rule internally. Biomes with equal multipliers stay grouped. Other conditions, names and prefab limits remain unchanged. A mixed-biome zone can run both timers; this is different from speeding up one shared timer.

- Omitted biomes use `1.0`. An empty mapping (`{}`), empty file or missing file applies no scaling.
- Multipliers must be finite and greater than zero. `0` is invalid; it does not disable spawning.
- Decimal seconds are preserved: `36 × 0.1 = 3.6`. The minimum effective interval is 0.01 seconds.
- Vanilla keys: `Meadows`, `BlackForest`, `Swamp`, `Ocean`, `Mountain`, `Plains`, `Mistlands`, `AshLands`, `DeepNorth`.
- For an Expand World Data custom biome, replace `MyCustomBiome` with its registered name on the server. Use one biome name per key, not `All`, `Land`, `None` or numeric masks.
- Use only one policy file: `DNS_spawnintervals.yml` or `DNS_spawnintervals.yaml`.
- Duplicate aliases, unresolved names after biome registration, invalid values and overflowing intervals reject the reload.

The server applies scaling once and sends effective rules to clients. Raids, dungeon rules, SpawnArea, CreatureSpawner and EWD's separate AltBiome lists are unaffected. Generated references show source values, not the expanded rules.

The old per-file `- spawnIntervalMultiplier: ...` header is no longer supported. Remove it manually; leaving it in a file rejects the reload. For exceptions that previously depended on separate files, adjust the individual rules' base intervals. There is no automatic migration.

## Spawn limits

`maxSpawned` is a limit on a rule, checked against nearby instances of its prefab. Multiple rules for the same prefab do not receive independent populations simply because their names, conditions or biomes differ.

For DNS-managed normal lists, new spawns within one list update are counted by prefab. Creating one creature type does not consume another type's slots. The same count is used to limit the next group's size. This corrects the native cross-prefab count without changing configured chances, intervals or placement conditions.

This is not a new world-wide hard cap across zones or separate lists. The correction is inactive with SpawnSystem Overrides off, and does not change event or AltBiome lists. A 100% chance still requires every other spawn condition to pass.

## Timers and live edits

DNS assigns internal content-based timer IDs. You do not need a unique `name` or a `timerId` field. Different rules for the same prefab have independent timers, and identical duplicate entries are also kept separate.

- Reordering rows or moving them between files preserves timers if their parsed settings stay the same.
- New or edited rules use the game's first-check timing, without an extra full-interval wait. Existing saved timers keep their progress.
- Changing a biome multiplier or the resulting split groups changes the affected timer IDs, like editing the underlying rule.
- Timers are stored together; retired records are removed as the owning zones are processed.
- Older native timer keys are not migrated or deleted. Existing save warnings about those keys can remain.

Event and AltBiome timers remain native. Install the same updated DNS build on the server and clients.
