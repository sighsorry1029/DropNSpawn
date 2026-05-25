# SpawnSystem EWS-Lifecycle Refactor Plan

## Goal

Keep the current `DNS_spawnsystem.yml` schema and semantics, but replace the current per-instance reconcile model with an `ExpandWorldData` / `SpawnThat WorldSpawner` style lifecycle:

1. compile the authoritative spawn table once per relevant signature
2. attach that compiled table to each `SpawnSystem` instance
3. keep only truly dynamic behavior on the runtime path

This is a lifecycle refactor, not a schema rewrite.

## Why This Refactor Exists

Current `spawnsystem` hitch cost comes from treating each new `SpawnSystem` instance as a mutable object that must be reconciled:

- `SpawnSystem.Awake` queues a reconcile step for every new zone `SpawnSystem`
- the reconcile step captures snapshots, clones rows, rebuilds the live table, and reassigns lists
- `UpdateSpawning` still checks whether runtime reconcile is needed
- ESP compatibility destroys and redraws markers after each authoritative replace

That model is fundamentally heavier than needed for world spawn rows.

`spawnsystem` is already an authoritative row domain. It should behave like a compiled table swap, not a live instance replay system.

## Constraints

The following behavior must stay intact:

- current YAML schema in `SpawnSystemConfiguration.cs`
- row-based coexistence and later-wins semantics
- authoritative replacement semantics for the domain
- `cllcWorldLevel` conditional row inclusion
- runtime `timeOfDay` handling
- numeric `requiredGlobalKey` syntax and post-spawn consumption
- EWD-backed `modifiers.data`, `modifiers.fields`, `modifiers.objects`, `modifiers.faction`
- CLLC runtime application after spawn
- server-synced YAML payload behavior
- reference file generation

## Non-Goals

- do not adopt `ExpandWorldSpawn` config syntax
- do not remove DNS-specific modifiers
- do not make `spawnsystem` non-authoritative
- do not mix DNS rows into vanilla lists incrementally

## Target Runtime Model

### Compile Once, Attach Many

Introduce a compiled table model:

```csharp
sealed class CompiledSpawnSystemTable
{
    public string Signature;
    public int GameDataSignature;
    public int? CllcWorldLevelMarker;
    public bool DomainEnabled;

    public List<SpawnSystemList> SharedLists;
    public Dictionary<SpawnSystem.SpawnData, TimeOfDayDefinition> TimeOfDayBySpawnData;
    public Dictionary<SpawnSystem.SpawnData, CllcDefinition> CllcBySpawnData;
}
```

`SharedLists` is the authoritative runtime spawn table. Every live `SpawnSystem` should point at the same compiled list objects.

This is the key behavioral change:

- old model: each `SpawnSystem` gets its own rebuilt rows
- new model: each `SpawnSystem` gets a reference to the same already-compiled rows

### Vanilla Baseline

Keep a separate immutable-ish baseline:

```csharp
sealed class VanillaSpawnSystemTable
{
    public int GameDataSignature;
    public List<SpawnSystemList> SharedLists;
}
```

Use this when:

- `Enable SpawnSystem Overrides = Off`
- DNS config is empty
- source of truth changes and we need to restore vanilla behavior
- we need a stable baseline for reference generation and hot reload fallback

Preferred baseline source:

1. `ZoneSystem.instance.m_zoneCtrlPrefab.GetComponent<SpawnSystem>().m_spawnLists`
2. fallback to first live untouched `SpawnSystem`

## Lifecycle

### 1. Compile Triggers

Recompile the active table only when one of these changes:

- local override YAML changed
- synced spawnsystem payload changed
- source-of-truth ownership changed
- game data signature changed
- `Enable SpawnSystem Overrides` changed
- `cllcWorldLevel` marker changed

This replaces the current per-instance reconcile trigger path.

### 2. SpawnSystem.Awake

Change `SpawnSystem.Awake` handling from:

- queue reconcile work later

to:

- attach the current active compiled table immediately

Preferred patch timing:

- use a `Prefix` or an earlier `Postfix` than ESP's draw hook so ESP sees the final rows on first draw

Behavior:

- if compiled table is ready, assign `__instance.m_spawnLists = ActiveTable.SharedLists`
- if compiled table is not ready yet, leave vanilla for now and perform a one-time live swap after compilation finishes

### 3. UpdateSpawning

Keep only truly dynamic work in the `UpdateSpawning` prefix:

- `RefreshRuntimeTimeOfDayState()`
- enter/exit extended required-global-key evaluation scope

Remove:

- `ReconcileRuntimeIfNeeded()`
- any path that rebuilds or reapplies the whole table from `UpdateSpawning`

### 4. Hot Reload / Runtime Swap

When config or source-of-truth changes:

1. compile a new active table
2. assign `m_spawnLists` on all currently live `SpawnSystem` instances to the new shared lists
3. replace runtime metadata maps atomically
4. optionally refresh ESP markers once for already-live systems

This is a table swap, not a restore-and-replay cycle.

## Semantics Mapping

### Compile-Time Semantics

These stay compile-time:

- prefab resolution
- biome / biomeArea parsing
- level, group, altitude, tilt, ocean depth, radius, forest/lava toggles
- required environments
- broad time-of-day spawn flags
- `cllcWorldLevel` row filtering

Compile these into shared `SpawnData` rows once.

### Runtime Semantics

These stay runtime:

- narrow `timeOfDay` updates on compiled rows
- numeric `requiredGlobalKey` read semantics
- numeric `requiredGlobalKey` consumption after successful spawn
- EWD data initialization before spawn
- EWD object spawn after spawn
- CLLC capture and apply after spawn

Important consequence:

- runtime metadata is attached to compiled `SpawnData` instances, not to per-system clones

That is safe because all `SpawnSystem` instances will reference the same authoritative rows.

## Manager Restructure

### Keep

- configuration loading and normalization
- `BuildPreparedEntries()` logic, renamed into compile terminology
- extended required-global-key helpers
- spawn-time EWD and CLLC hooks
- reference generation helpers

### Remove

Remove the per-instance reconcile/snapshot layer:

- `QueueSpawnSystemReconcile`
- `ProcessQueuedReconcileStep`
- `ReconcileRuntimeIfNeeded`
- `ReconcileSpawnSystemInstanceInternal`
- `ApplyConfiguredEntriesToSystem`
- `ReplaceSpawnSystemWithPreparedEntries`
- `RestoreSpawnSystem`
- `ClearSpawnSystemLists`
- `TryCaptureSnapshotsIfNeeded`
- `CaptureSnapshotIfNeeded`
- `CaptureSnapshot`
- `SnapshotsBySystemId`
- `PendingSpawnSystemReconciles`
- `PendingSpawnSystemReconcileIds`
- `ReconciledSpawnSystemIds`
- reconcile epoch bookkeeping

These methods exist only because the current design treats each `SpawnSystem` as something to mutate and restore independently.

### Add

Introduce methods with explicit compile/swap responsibilities:

```csharp
private static void RefreshCompiledState();
private static CompiledSpawnSystemTable BuildCompiledTable();
private static VanillaSpawnSystemTable BuildVanillaTable();
private static void AttachActiveTable(SpawnSystem system);
private static void AttachTableToLiveSystems();
private static bool IsCompiledTableValid();
private static bool ShouldRecompileForCllcWorldLevel();
private static void ReplaceRuntimeMetadata(CompiledSpawnSystemTable table);
```

Suggested state:

```csharp
private static VanillaSpawnSystemTable? _vanillaTable;
private static CompiledSpawnSystemTable? _activeTable;
private static string _activeTableSignature = "";
private static int? _activeCllcWorldLevelMarker;
private static int? _activeGameDataSignature;
private static bool? _activeDomainEnabled;
```

## Shared-List Shape

For compatibility with current DNS behavior, keep the compiled authoritative table shape identical to today's runtime shape:

- one active `SpawnSystemList`
- all compiled DNS rows placed into `SharedLists[0].m_spawners`

Do not attempt to preserve vanilla multi-list structure for DNS-authored rows unless a real gameplay dependency is found.

That preserves:

- current stable row ordering
- current `stableHashCode` behavior inside `SpawnSystem.UpdateSpawnList`
- current reference/export expectations

## ESP Compatibility

Current behavior is expensive because DNS does:

1. authoritative replace
2. find old ESP marker components
3. destroy them
4. call ESP draw again

New behavior:

- on zone load: do not redraw ESP markers if the active table was attached before ESP draws
- on hot reload: redraw live systems once after table swap, with debounce

This should remove zone-load redraw hitch while preserving correct marker state after manual reloads.

## Reference Generation

Reference generation should remain supported, but it must be moved off the gameplay hot path.

Rules:

- no reference generation during `SpawnSystem.Awake`
- no reference generation during zone-load attach
- no reference generation from runtime time-of-day refresh

Allowed triggers:

- `OnGameDataReady`
- explicit `dns:reference`
- optional delayed auto-update after config reload

Use either:

- `VanillaSpawnSystemTable`
- current live systems after a completed table swap

but never the old reconcile queue.

## Plugin Changes

After this refactor, `Plugin.Update()` no longer needs to spend queue budget on `SpawnSystemManager.ProcessQueuedReconcileStep`.

Expected follow-up:

- remove spawnsystem from the round-robin reconcile queue in `Plugin.cs`
- let `SpawnSystemManager` operate synchronously at compile/swap boundaries instead

## File-Level Plan

### `GameDataPatches.cs`

Change:

- `SpawnSystem.Awake` patch: queue reconcile -> attach active table
- `SpawnSystem.UpdateSpawning` patch: remove runtime reconcile call, keep only dynamic logic

Keep:

- `ZoneSystem.GetGlobalKey`
- `ZoneSystem.RPC_SetGlobalKey`
- `SpawnSystem.Spawn`

### `SpawnSystemManager.cs`

Phase the file from:

- snapshot/reconcile manager

to:

- compile/swap manager

Primary responsibilities after refactor:

- load and normalize config
- compile DNS rows into one shared table
- build and cache vanilla baseline
- attach active table to live/new systems
- update runtime metadata maps
- drive hot reload swaps
- provide reference generation

### `SpawnSystemCustomDataSupport.cs`

Keep the current role, but assume `SpawnData` keys are shared compiled rows instead of per-system clones.

No semantic change should be required if metadata replacement is atomic during table swaps.

## Implementation Phases

### Phase 1. Introduce Compile Model Alongside Existing Code

- add `CompiledSpawnSystemTable` and `VanillaSpawnSystemTable`
- convert `BuildPreparedEntries()` into a compile helper
- build active shared lists and metadata maps
- keep old reconcile path temporarily for fallback

### Phase 2. Convert Awake To Attach-Only

- patch `SpawnSystem.Awake` to attach active/vanilla table
- disable queueing of reconcile work
- keep live-system swap path for config reloads

### Phase 3. Remove Runtime Reconcile

- remove `ReconcileRuntimeIfNeeded()` from `UpdateSpawning`
- delete reconcile queues, epochs, and snapshot restore paths
- replace manager state with active-table state

### Phase 4. Reduce ESP Cost

- stop redraw on zone-load attach
- keep redraw only for live hot reload
- debounce redraws over all live systems after swap

### Phase 5. Move Reference Update Off Gameplay Path

- stop piggybacking reference refresh on reconcile
- trigger only from game-data readiness, manual command, or delayed config reload path

## Validation Matrix

### Core Runtime

- walk across multiple zone boundaries with `spawnsystem` enabled
- confirm no new reconcile-like hitch appears
- verify spawn rows still fire in all intended biomes

### Dynamic Behavior

- day/night transitions update spawn eligibility correctly
- numeric `requiredGlobalKey` rows gate correctly
- numeric `requiredGlobalKey` is consumed once per successful spawn
- `cllcWorldLevel` changes trigger one recompile and one swap

### Content Modifiers

- EWD `data` applies before spawn
- EWD `objects` spawn after spawn
- CLLC modifiers still affect the newly spawned creature

### Reload Paths

- local YAML reload updates all live `SpawnSystem` instances
- server-synced payload update updates clients correctly
- turning domain off restores vanilla live/new systems
- turning domain back on reapplies compiled table

### Compatibility

- with ESP installed, zone load should not cause redraw churn
- with ESP installed, manual config reload should refresh markers once

## Known Risks

### Shared Mutable Rows

Compiled rows are shared across all `SpawnSystem` instances. This is intended.

Risk:

- if any external mod mutates `m_spawnLists` or `m_spawners` in place per instance, shared references can expose that mutation globally

Mitigation:

- treat DNS compiled lists as authoritative ownership
- if a concrete incompatible mod is found, add a compatibility branch for that mod instead of returning to per-instance rebuilds

### Vanilla Baseline Capture Timing

If vanilla baseline is captured after DNS has already attached its compiled table, the baseline is polluted.

Mitigation:

- capture baseline from `m_zoneCtrlPrefab` before first attach if possible
- otherwise capture from the first known untouched live system

### Hot Reload Mid-Spawn

If a table swap happens while a spawn callback is in flight, metadata maps must remain coherent.

Mitigation:

- replace metadata maps atomically under the existing manager lock
- keep spawn-time hooks lock-protected as they are today

## Recommended First Code Cut

The safest first implementation slice is:

1. add compiled/vanilla table types
2. build active shared lists from current `BuildPreparedEntries()` output
3. change `SpawnSystem.Awake` to attach active table
4. remove `ReconcileRuntimeIfNeeded()` from `UpdateSpawning`
5. keep hot reload support by swapping `m_spawnLists` on all live systems

That slice should remove the main zone-load hitch without forcing the full cleanup in the same commit.
