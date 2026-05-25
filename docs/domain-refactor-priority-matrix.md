# Domain Refactor Priority Matrix

## Goal

Classify each `DropNSpawn` domain by the cheapest safe runtime model instead of
treating every domain as a live-instance reconcile problem.

The key rule is simple:

- use `compile/attach` only for domains that are already authoritative shared
  tables
- use `spawn-time` application for domains whose static overrides can be applied
  once when an instance appears
- keep `snapshot-retained` only where a live baseline is actually required for
  restore, hot reload, or runtime condition re-evaluation

## Matrix

| Domain | Compile/Attach | Spawn-Time | Snapshot-Retained | Recommended Model | Priority |
| --- | --- | --- | --- | --- | --- |
| `spawnsystem` | High | Low | Low | Full compile/attach | Done first |
| `character` | Low | High | Low | Event-time / death-time hybrid | Next |
| `spawner` | Medium | High | Medium | EWD-style hybrid | After `character` |
| `location` | Low | High | Medium | EWD-style hybrid | Alongside / after `spawner` |
| `object` | Low | Medium | High | Component replay hybrid | Last |

## Domain Decisions

### `spawnsystem`

Best fit: full `compile once, attach many`.

Reason:

- the domain is already an authoritative row table
- runtime behavior is mostly driven by the final compiled `SpawnData` rows
- per-instance rebuild was the main hitch source

Implementation direction:

- compile one active table per relevant signature
- attach that table on `SpawnSystem.Awake`
- keep only narrow runtime checks like `timeOfDay` or spawn-time object hooks

### `character`

Best fit: event-time / death-time hybrid.

Reason:

- the real customization point is effective drop calculation, not live component
  mutation
- most value already comes from override selection at death/drop time
- `Awake` reconcile is not carrying the domain the way it did for
  `spawnsystem`

Implementation direction:

- keep prefab snapshots only as vanilla drop baselines
- stop treating every new `CharacterDrop` as a heavy reconcile candidate
- prefer:
  - instance tracking
  - death-time effective drop calculation
  - targeted hot-reload replay only for already tracked live instances

Likely outcome:

- remove or sharply narrow `QueueCharacterDropReconcile`
- preserve `TryHandleConfiguredDeath` / conditional drop assembly as the core
  runtime path

### `spawner`

Best fit: EWD-style hybrid.

Reason:

- top-level list/index data can be precompiled
- static overrides can often be applied once when `SpawnArea` /
  `CreatureSpawner` becomes live
- some entries still need runtime re-evaluation for time/global-key/environment
  changes

Implementation direction:

- compile selector/path indexes and effective rule sets up front
- apply static fields on `Awake` / `Start` / spawned-location-root discovery
- keep runtime signatures only for entries that actually depend on dynamic
  conditions
- keep live snapshots only for touched instances and hot reload

Likely outcome:

- remove most startup scanning and full-domain replay
- keep a smaller targeted runtime reconcile path for dynamic-condition entries

### `location`

Best fit: EWD-style hybrid, not full compile/attach.

Reason:

- the domain targets child components inside live prefab trees
- rule application depends on path/name/ordinal targeting
- safe replay still needs a baseline for touched live locations

Implementation direction:

- prebuild component catalogs by prefab and relative path
- apply static component overrides when a location root is spawned
- keep snapshots only for touched live locations and hot reload
- do not scan every live location on startup by default

Likely outcome:

- startup cost shifts from deep snapshotting to lightweight catalog/index build
- location root hooks become the main application path

### `object`

Best fit: component replay hybrid.

Reason:

- object overrides span many unrelated component kinds
- restore/apply semantics differ across `Container`, `Destructible`,
  `Pickable`, `MineRock`, `Fish`, and drop-table blocks
- this domain still benefits from replaying from a known baseline per prefab or
  live object

Implementation direction:

- keep grouped rule sets by component kind
- reduce unnecessary live reconcile queueing where static spawn-time hooks are
  sufficient
- retain snapshots for prefab baselines and for live-object restore on reload
- do not force this domain into a fake shared-table model

Likely outcome:

- some queue reduction is possible
- full removal of snapshot/replay is not realistic without changing semantics

## Priority Order

### 1. `character`

Why first:

- lower structural risk than `spawner` / `location`
- likely easy win by shrinking or removing `Awake` reconcile
- semantics are already close to event-time drop assembly

Target:

- convert from "start reconcile for each instance" to "track instances +
  compute effective drops when needed"

### 2. `spawner`

Why second:

- highest remaining startup/runtime cost after `spawnsystem`
- existing refactor plan already points to a hybrid model
- a lot of heavy work is selector/runtime-state plumbing that can be narrowed

Target:

- static-at-spawn path by default
- runtime signatures only for entries that truly need them

### 3. `location`

Why third:

- same architectural direction as `spawner`
- more component-path targeting complexity
- should reuse the same "catalog first, snapshot only when touched" pattern

Target:

- shift cost from startup scans to location-root spawn hooks

### 4. `object`

Why last:

- broadest component surface area
- most semantics tied to restore/replay behavior
- easiest domain to break subtly if forced into the wrong abstraction

Target:

- optimize queueing and replay scope, not total model replacement

## Decision Rules

Before replacing reconcile in any domain, ask:

1. Is the authoritative state a shared table or a live component tree?
2. Can static overrides be applied once when the instance appears?
3. Do dynamic conditions require later re-evaluation?
4. Is a baseline needed for restore or hot reload?

Recommended mapping:

- shared table: prefer `compile/attach`
- spawned instance with mostly static fields: prefer `spawn-time`
- live component tree with restore semantics: keep `snapshot-retained` hybrid

## Practical Recommendation

Do not generalize the `spawnsystem` refactor into a single universal policy.

Use domain-specific policies instead:

- `spawnsystem`: compile/attach
- `character`: event-time
- `spawner`: hybrid with narrow runtime signatures
- `location`: hybrid with catalogs and spawn hooks
- `object`: replay hybrid with targeted queue reduction
