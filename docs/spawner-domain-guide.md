# Spawner Domain Guide

This guide explains the `spawner` domain in user-facing terms.

Scope:

- `SpawnArea`
- `CreatureSpawner`

This document is about the prefab-bound `spawner` domain, not the world `spawnsystem` domain.

## Entry Model

Each top-level YAML entry is one selector-scoped rule block.

Structure:

```yaml
- prefab: ExamplePrefabName
  enabled: true
  location: ExampleLocationPrefab
  conditions: {}
  spawnArea: {}
```

Meaning:

- top-level `conditions` decide whether the entry is active
- `spawnArea` and `creatureSpawner` are payload blocks
- if an entry does not match, it is ignored

Selector notes:

- `prefab` only: matches every spawner prefab with that name
- `prefab + location`: matches only spawners under that location prefab
- only the most specific passing entry is applied
- less specific entries act as fallback
- if multiple passing entries share the same specificity, the later loaded one wins

Important:

- do not use `conditions.locations` in the `spawner` domain
- use top-level `location` for selector scoping
- `creatureSpawner` does not support top-level `conditions.insidePlayerBase`
- use `creatureSpawner.allowInsidePlayerBase` for runtime player-base gating instead

## SpawnArea

`spawnArea` is a multi-row native spawner.

Its `creatures:` rows are payload rows:

```yaml
spawnArea:
  spawnInterval: 10
  maxNear: 3
  maxTotalSpawns: 100
  creatures:
  - creature: Skeleton
    weight: 1
    level: 1~2
  - creature: Draugr
    weight: 0.5
    level: 2~3
```

Meaning:

- the `spawnArea` component controls the area-level spawn behavior
- each `creatures[]` row is one possible creature choice inside that native `SpawnArea`
- `maxTotalSpawns` counts successful spawns for this `SpawnArea` instance and destroys the spawner when the count is reached
- `maxTotalSpawns: null` uses `1 - General / Default SpawnArea Max Total Spawns`; `0` disables the lifetime count for this entry

## CreatureSpawner

`creatureSpawner` is a single native spawner that spawns one creature at a time.

Example:

```yaml
creatureSpawner:
  creature: Skeleton
  level: 1~2
  respawnTimeMinutes: 20
  triggerDistance: 60
  timeOfDay: [night]
  allowInsidePlayerBase: false
```

Meaning:

- `creature`: what this spawner creates
- `level`: spawned creature level range
- `respawnTimeMinutes`: respawn delay after the previous spawned creature is gone
- `triggerDistance`: players must be close enough for the spawner to pass its native runtime check
- `timeOfDay`: native day/night gate
- `allowInsidePlayerBase`: if `false`, native player-base suppression still blocks this spawner

Legacy note:

- `requireSpawnArea` may still appear in older configs or native snapshots
- current Valheim code does not use it, so treat it as a no-op and omit it

## Native Spawn Group Fields

These four fields belong to Valheim's native `CreatureSpawner` group-blocking system:

- `spawnGroupId`
- `maxGroupSpawned`
- `spawnGroupRadius`
- `spawnerWeight`

They let multiple nearby `CreatureSpawner` components behave like one shared spawn group.

### `spawnGroupId`

This is the group identifier.

Spawners with the same id can be grouped together.

Important:

- vanilla code does not treat `0` as special
- if multiple nearby spawners all use `spawnGroupId: 0` and non-zero group radii, they can still group together
- if you do not want group behavior, keep `spawnGroupRadius: 0`

### `spawnGroupRadius`

This decides which same-id spawners are linked into one native group.

Two spawners join the same group when:

- they have the same `spawnGroupId`
- the distance between them is less than or equal to the sum of their two `spawnGroupRadius` values

Example:

- Spawner A: `spawnGroupId: 7`, `spawnGroupRadius: 10`
- Spawner B: `spawnGroupId: 7`, `spawnGroupRadius: 10`

Result:

- distance `15`: grouped, because `15 <= 20`
- distance `25`: not grouped, because `25 > 20`

Grouping is chain-based.

Example:

- A-B distance: `15`
- B-C distance: `15`
- A-C distance: `30`
- all three have the same id and `spawnGroupRadius: 10`

Result:

- A links to B
- B links to C
- all three end up in one group, even though A and C do not directly overlap

### `maxGroupSpawned`

This is the cap for the whole native spawn group, not for one spawner.

Example:

```yaml
creatureSpawner:
  spawnGroupId: 9
  maxGroupSpawned: 2
  spawnGroupRadius: 20
```

If three nearby same-id spawners are grouped together, that group can have at most `2` valid spawned creatures according to native group rules.

Important runtime detail:

- if `respawnTimeMinutes > 0`, the cap works like a current living-group cap
- if `respawnTimeMinutes <= 0`, native code also checks whether the group has already spawned before, so this behaves more like a lifetime cap

### `spawnerWeight`

This is used when a grouped native spawn chooses which one spawner should fire.

Example:

- A weight `1`
- B weight `1`
- C weight `3`

If all three are currently eligible inside one group, the native weighted choice is roughly:

- A: `20%`
- B: `20%`
- C: `60%`

## Grouping Different Spawner Prefabs Or Creatures

Grouping does not care about:

- the `CreatureSpawner` prefab name
- the creature being spawned

Grouping only cares about:

- `spawnGroupId`
- `spawnGroupRadius`
- physical distance between spawners

So this can still group together:

```yaml
- prefab: SpawnerA
  creatureSpawner:
    creature: Wolf
    spawnGroupId: 10
    maxGroupSpawned: 1
    spawnGroupRadius: 20
    spawnerWeight: 1

- prefab: SpawnerB
  creatureSpawner:
    creature: Fenring
    spawnGroupId: 10
    maxGroupSpawned: 1
    spawnGroupRadius: 20
    spawnerWeight: 1
```

If those two spawners are close enough, they can form one native group.

That means:

- a spawned `Wolf` can block the `Fenring` spawner
- or vice versa

## Safe Defaults

If you do not want native group blocking behavior, use:

```yaml
creatureSpawner:
  spawnGroupId: 0
  maxGroupSpawned: 1
  spawnGroupRadius: 0
  spawnerWeight: 1
```

The important part is `spawnGroupRadius: 0`.

## Mental Model

Use this shorthand:

- `spawnGroupId`: which group this spawner belongs to
- `spawnGroupRadius`: how far that group can link to nearby same-id spawners
- `maxGroupSpawned`: how many spawned creatures the whole group can allow
- `spawnerWeight`: which spawner wins when the group chooses one spawner to fire
