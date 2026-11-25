# World spawn guide

[Back to DropNSpawn](../README.md) · [Mod compatibility](compatibility.md)

`DNS_spawnsystem.yml` controls normal world spawns. SpawnArea, CreatureSpawner, raids and dungeon rules have separate files and systems.

## Keep the complete table

Active SpawnSystem YAML files together replace the main world spawn table. Once you name a prefab, keep every rule you want for it; DNS does not merge individual rules with its original ones. Valheim's separate AltBiome spawn lists are outside this replacement.

Use `DNS_spawnsystem.reference.yml` to find source values. Both `.yml` and `.yaml` work, including supplemental files such as `DNS_spawnsystem_mistlands.yml`. Reference and full-scaffold files are not loaded as overrides.

On multiplayer servers, edit the server files and install the same DNS build on every client. Accepted changes reload live. Invalid updates retain the last accepted configuration.

### Import prefabs missing from your YAML

**99 - Domains / Auto-import missing SpawnSystem prefabs** defaults to **On**. With SpawnSystem Overrides enabled, the host/server checks on world load and SpawnSystem reload, starting with the first load. For each vanilla or modded prefab missing from **all active YAML files**, it appends the prefab's original normal world spawn rules together to `DNS_spawnsystem_imported.yml`. A registered creature with no normal world spawn rules is not imported. Raids, dungeons, reference-only entries and separate AltBiome lists are not sources. Biome interval multipliers apply after import.

For example, if the source has day and night rules for `Gjall` but your YAML keeps only the day rule, DNS does not add the night rule. If you delete or comment out **every** Gjall row, including those in the imported file, both source rules return on the next check. To exclude Gjall entirely, keep a single `prefab: Gjall` row with `enabled: false` and no other enabled Gjall rows. To manage a strict whitelist instead, turn auto-import **Off**; it does not remove previously imported rows.

You can edit imported rows or move them into another active file. DNS reuses the same imported file (including an existing `.yaml` version); it never creates a numbered copy or deletes rules when a mod disappears. Keep the generated header and block-style YAML list so DNS can safely append without rewriting your comments.

If DNS cannot read the active spawn files safely, it defers the import. If a prefab's native fields would change when read back from DNS YAML (for example an unsupported forest flag combination), none of its rows are imported; check the server log. Manual overrides remain available.

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

Valheim 1.0.30 counts new spawns per rule, so spawning a Boar no longer consumes a Deer's slots. But two active rules for the same prefab can each use the same free slots, and a catch-up group can exceed the remaining limit. In a DNS-managed normal list, each rule uses its own limit and counts all new instances of its prefab in this list update. For example, with a limit of 3 and groups of 2, the second group can add only 1. Chances, intervals and placement conditions are unchanged.

This is not a new world-wide hard cap across zones or separate lists. The correction is inactive with SpawnSystem Overrides off, and does not change event or AltBiome lists. A 100% chance still requires every other spawn condition to pass.

## Timers and live edits

DNS assigns internal content-based timer IDs. You do not need a unique `name` or a `timerId` field. Different rules for the same prefab have independent timers, and identical duplicate entries are also kept separate.

- Reordering rows or moving them between files preserves timers if their parsed settings stay the same.
- New or edited rules use the game's first-check timing, without an extra full-interval wait. Existing saved timers keep their progress.
- Changing a biome multiplier or the resulting split groups changes the affected timer IDs, like editing the underlying rule.
- Timers are stored together; retired records are removed as the owning zones are processed.
- Older native timer keys are not migrated or deleted. Existing save warnings about those keys can remain.

Event and AltBiome timers remain native. Install the same updated DNS build on the server and clients.
