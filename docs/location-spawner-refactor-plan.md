# Location/Spawner Refactor Plan

## Goal

Reduce lobby-to-session startup cost in `DropNSpawn` by moving `location` and `spawner` handling away from full-domain snapshot capture and toward:

1. list-level indexing where possible
2. spawn-time reconciliation for newly created objects
3. per-instance runtime state only where dynamic conditions actually require it

This is intentionally closer to how `ExpandWorldData` works, while preserving `DropNSpawn` features that `ExpandWorldData` does not have.

## What ExpandWorldData Does Differently

`ExpandWorldData` is fast at startup for `location`/`vegetation` because it usually does not:

- capture deep snapshots of every matching prefab subtree
- restore prefab state for every domain before reapplying
- scan all live `Location` / `SpawnArea` / `CreatureSpawner` instances on startup

Instead, it mostly:

- replaces `ZoneSystem`/`DungeonDB` source lists once during initialization
- updates lookup dictionaries like `m_locationsByHash`
- patches generation/spawn methods so customization happens when content is spawned
- skips most heavy data application on clients

This works because its schema mostly targets world-generation data, not arbitrary component mutation inside already-existing prefab trees.

## Important Constraint

For current `DNS_location.yml` and `DNS_spawner.yml`, there are very few true user-facing fields that belong in a pure list-level bucket.

Reason:

- `location.yml` targets child components inside location prefabs: `OfferingBowl`, `ItemStand`, `Vegvisir`
- `spawner.yml` targets child components inside prefabs or spawned locations: `SpawnArea`, `CreatureSpawner`

Those are component-level overrides, not `ZoneLocation` / `ZoneVegetation` list entries.

So the correct translation is:

- use list-level data structures for lookup/indexing
- use spawn-time hooks for most static overrides
- keep snapshots only for touched live instances or for reference generation

## Buckets

### 1. List-Level

These should be represented as lookup/index data built from game data once per signature, not as full mutable snapshots.

#### Location domain

- `prefab` key lookup
- `vegvisirs.path` path catalog
- item stand path/name catalog for diagnostics and targeting
- offering bowl presence/path metadata
- location prefab name to `SoftReference` / source prefab mapping

#### Spawner domain

- `prefab` key lookup
- optional `location` + `path` selector index
- `SpawnArea` exact path catalog
- `CreatureSpawner` exact path catalog
- root prefab name to component path map

#### Notes

- This bucket is mostly internal metadata.
- It replaces `CaptureSnapshotsIfNeeded()` as the default startup path.
- It should only include fields needed to find components and validate selectors.

### 2. Spawn-Time Hook

These are safe to apply when an instance is created or first awakened. They do not need full-domain startup scans.

#### Location domain

Apply on `Location.Awake` or immediately after `ZoneSystem.SpawnLocation` / `LocationProxy.SpawnLocation`:

- `offeringBowl.name`
- `offeringBowl.useItemText`
- `offeringBowl.usedAltarText`
- `offeringBowl.cantOfferText`
- `offeringBowl.wrongOfferText`
- `offeringBowl.incompleteOfferText`
- `offeringBowl.bossItem`
- `offeringBowl.bossItems`
- `offeringBowl.bossPrefab`
- `offeringBowl.itemPrefab`
- `offeringBowl.setGlobalKey`
- `offeringBowl.renderSpawnAreaGizmos`
- `offeringBowl.alertOnSpawn`
- `offeringBowl.spawnBossDelay`
- `offeringBowl.spawnBossDistance`
- `offeringBowl.spawnBossMaxYDistance`
- `offeringBowl.getSolidHeightMargin`
- `offeringBowl.enableSolidHeightCheck`
- `offeringBowl.spawnPointClearingRadius`
- `offeringBowl.spawnYOffset`
- `offeringBowl.useItemStands`
- `offeringBowl.itemStandPrefix`
- `offeringBowl.itemStandMaxRange`
- `itemStand.name`
- `itemStand.canBeRemoved`
- `itemStand.autoAttach`
- `itemStand.orientationType`
- `itemStand.supportedTypes`
- `itemStand.supportedItems`
- `itemStand.unsupportedItems`
- `itemStand.powerActivationDelay`
- `itemStand.guardianPower`
- `vegvisir.name`
- `vegvisir.useText`
- `vegvisir.hoverName`
- `vegvisir.setsGlobalKey`
- `vegvisir.setsPlayerKey`
- `vegvisir.locations`

#### Spawner domain

Apply on `SpawnArea.Awake/Start` and `CreatureSpawner.Awake/Start`, or when a location root becomes live:

- `spawnArea.levelUpChance`
- `spawnArea.spawnInterval`
- `spawnArea.triggerDistance`
- `spawnArea.setPatrolSpawnPoint`
- `spawnArea.spawnRadius`
- `spawnArea.nearRadius`
- `spawnArea.farRadius`
- `spawnArea.maxNear`
- `spawnArea.maxTotal`
- `spawnArea.onGroundOnly`
- `spawnArea.creatures[*].creature`
- `spawnArea.creatures[*].weight`
- `spawnArea.creatures[*].level`
- `creatureSpawner.creature`
- `creatureSpawner.level`
- `creatureSpawner.levelUpChance`
- `creatureSpawner.respawnTimeMinutes`
- `creatureSpawner.triggerDistance`
- `creatureSpawner.triggerNoise`
- `creatureSpawner.timeOfDay`
- `creatureSpawner.requireSpawnArea`
- `creatureSpawner.allowInsidePlayerBase`
- `creatureSpawner.wakeUpAnimation`
- `creatureSpawner.spawnCheckInterval`
- `creatureSpawner.requiredGlobalKey`
- `creatureSpawner.blockingGlobalKey`
- `creatureSpawner.setPatrolSpawnPoint`
- `creatureSpawner.spawnGroupId`
- `creatureSpawner.maxGroupSpawned`
- `creatureSpawner.spawnGroupRadius`
- `creatureSpawner.spawnerWeight`

#### Notes

- For entries with only static conditions or no conditions, apply once on spawn and stop.
- Do not scan all live objects at startup just to apply these.

### 3. Snapshot-Retained

Keep snapshots only where we truly need a baseline for restore or re-evaluation.

#### Keep only for touched live instances

- live `Location` instances that were already spawned before config reload
- live `SpawnArea` instances already in scene
- live `CreatureSpawner` instances already in scene
- loose `ItemStand` instances associated with altar logic

#### Keep for reference generation

- `DNS_location.reference.yml`
- `spawner.reference.yml`
- `DNS_spawner.locations.reference.yml`

Reference generation is a tooling feature. It does not need to block gameplay startup.

#### Dynamic/runtime-only state that should stay instance-scoped

- `offeringBowl.respawnMinutes`
- `offeringBowl.cllcInfusion` / `offeringBowl.cllcEffect` / `offeringBowl.cllcBossEffect`
- `spawnArea` per-creature `data`
- `spawnArea` per-creature `faction`
- `spawnArea` per-creature CLLC modifiers
- `creatureSpawner.data`
- `creatureSpawner.faction`
- `creatureSpawner.cllcInfusion` / `creatureSpawner.cllcEffect` / `creatureSpawner.cllcBossEffect`
- `creatureSpawner.timeOfDay` narrow gating
- runtime condition signatures for time/global-key/environment/player-base changes

## Recommended Architecture

### A. Replace full snapshots with component catalogs

Introduce lightweight catalogs:

- `LocationComponentCatalog`
- `SpawnerComponentCatalog`

Each catalog should contain only:

- owning prefab name
- component type
- relative path
- a minimal default-value payload for fields `DropNSpawn` can override

This is cheaper than storing every live component reference from every prefab up front.

### B. Build catalogs only for active prefabs

Current code scans all location/spawner-related prefabs even if only a few YAML entries are active.

Instead:

- parse config first
- determine active prefab names
- build catalog only for those prefabs
- lazily expand if a live instance appears for a prefab not yet indexed

This matches the optimization direction already used by object/character snapshot signatures.

### C. Move primary application to event-driven paths

Prefer:

- `Location.Awake`
- `LocationProxy.SpawnLocation` postfix
- `SpawnArea` instance patch path
- `CreatureSpawner` instance patch path

Avoid:

- global `FindObjectsByType<Location>()` on initial world entry
- global `FindObjectsByType<SpawnArea>()` on initial world entry
- global `FindObjectsByType<CreatureSpawner>()` on initial world entry

Keep a targeted catch-up pass only for hot reload and only for dirty prefabs.

### D. Store originals lazily per touched instance

When an instance is first overridden:

- capture only the fields that may need restore
- store them in per-instance dictionaries

Do not capture originals for every prefab/component in advance.

This is the biggest conceptual shift from current `location`/`spawner` handling.

### E. Separate startup path from tooling path

Reference/scaffold generation should not force full startup capture.

Recommended:

- gameplay startup: build only active-prefab catalogs
- manual reference write command: allow heavy full capture
- optional auto-refresh mode: off by default for `location` and `spawner`

## Field Classification Summary

### Location

True list-level user fields:

- none, effectively

Spawn-time fields:

- almost all `offeringBowl`, `itemStand`, `vegvisir` content fields

Snapshot-retained fields:

- live-instance restore baseline
- loose item stand baseline
- offering bowl runtime extras

### Spawner

True list-level user fields:

- none, effectively

Spawn-time fields:

- most `SpawnArea` and `CreatureSpawner` component values

Snapshot-retained fields:

- live-instance restore baseline
- post-spawn custom payloads
- dynamic condition runtime signatures

## Refactor Order

### Phase 1

Change startup behavior without changing external YAML semantics.

- add active-prefab filtering before location/spawner capture
- stop full-domain capture when no active entries require it
- decouple reference generation from gameplay startup

Expected result:

- immediate startup win with minimal behavior risk

### Phase 2

Replace prefab-wide snapshots with catalogs for `LocationManager`.

- catalog paths for offering bowls, item stands, vegvisirs
- apply only on spawned/live location roots
- keep lazy per-instance restore snapshots

Expected result:

- major reduction in `location` startup cost

### Phase 3

Replace prefab-wide snapshots with catalogs for `SpawnerManager`.

- catalog `SpawnArea` / `CreatureSpawner` paths and defaults
- apply on instance creation/reconciliation only
- keep only per-instance restore state and runtime post-spawn state

Expected result:

- major reduction in `spawner` startup cost

### Phase 4

Replace full live rescans on reload with dirty-prefab targeted reconciliation only.

- maintain instance registries from Awake/OnDestroy style hooks
- reapply only affected prefab groups

Expected result:

- hot reload stays fast without startup-wide scans

## Risks

- `Location` and `Spawner` instances can exist before all config sync is complete on clients.
- Loose altar item stands are not strictly children of a `Location` in every case.
- Narrow time-of-day and global-key-dependent conditions still need runtime refresh logic.
- Hot reload behavior can regress if restore baselines are not captured lazily and correctly.

## Practical Recommendation

Do not try to force `location/spawner` into a pure `ExpandWorldData` model.

Use a hybrid model:

- `ExpandWorldData` style for indexing and spawn-time application
- `DropNSpawn` style only for lazy restore and dynamic runtime state

That keeps the flexible component override features while cutting the most expensive startup work.
