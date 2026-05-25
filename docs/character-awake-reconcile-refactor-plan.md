# Character Awake Reconcile Refactor Plan

## Goal

Reduce `character` domain startup and spawn-time overhead by removing the
default `CharacterDrop.Start -> queue reconcile` path.

The `character` domain should behave as:

- track live instances when they appear
- calculate effective custom drops when drops are actually needed
- keep snapshot-based restore only for prefab baseline and hot reload

It should not behave like a live-instance mutation domain unless a specific
reload or restore path actually needs that work.

## Current Behavior

Today the main startup path is:

1. `CharacterDrop.Start`
2. `TrackCharacterDropInstance(__instance)`
3. `QueueCharacterDropReconcile(__instance)`
4. `Plugin.Update()` spends queue budget on
   `CharacterDropManager.ProcessQueuedReconcileStep`
5. `ReconcileCharacterDropInstanceCore` runs for the queued instance

But the important detail is what that reconcile actually does.

`ReconcileCharacterDropInstanceCore`:

- registers the live instance
- checks whether the prefab is configured
- restores `characterDrop.m_drops` from the prefab snapshot baseline

It does not build and attach the final effective custom drop list for the live
instance.

That means the queue is not the authoritative custom-drop path.

## Where Character Customization Really Happens

The effective `character` behavior already comes from event-time hooks:

- `CharacterDrop.GenerateDropList`:
  `OverrideConditionalDrops()` temporarily swaps in an effective drop list for
  the current character instance
- `CharacterDrop.OnDeath`:
  `TryHandleConfiguredDeath()` can bypass vanilla drop handling and spawn the
  configured drops directly
- `CharacterDrop.DropItems`:
  `ApplyGlobalDropInStack()` adjusts final emitted stacks

This means the domain is already closer to:

- event-time drop assembly
- temporary override during generation
- direct custom drop emission on death

than to a persistent live-instance reconcile model.

## Why The Awake Queue Is Low Value

### 1. It is not the main customization path

The queue does not attach the effective custom drop table to the instance.
Custom behavior is still resolved later from conditions and current character
state.

### 2. It spends runtime budget on every configured live instance

Every configured `CharacterDrop.Start` instance can enqueue work into the shared
plugin reconcile budget even though the result is usually just restoring
vanilla baseline.

### 3. It duplicates work already covered elsewhere

Live instance tracking is already handled separately.

Hot reload replay is already handled by:

- prefab snapshot restore
- targeted live object reapply based on dirty prefab signatures

The `Start` queue is therefore not the only mechanism preserving correctness.

### 4. It is a poor fit for the domain

`character` is not like `spawnsystem`.

`spawnsystem` needed a compiled authoritative table.
`character` does not.

The real unit of behavior is:

- the current `Character`
- top-level entry conditions
- effective drop rows at death/drop generation time

## Recommended Runtime Model

### Keep

- prefab snapshots as vanilla baselines
- live instance tracking
- event-time drop calculation
- targeted live replay on config reload or source-of-truth changes

### Remove As Default Path

- `CharacterDrop.Start` queueing into reconcile budget
- queued per-instance awake reconcile

### New Mental Model

`character` should be:

- baseline captured once per prefab
- instance tracked on spawn
- effective drops computed only when generating or emitting loot

## Proposed Architecture

### 1. Start Path

Change `CharacterDrop.Start` to:

- `TrackCharacterDropInstance(__instance)`

only.

Do not queue reconcile by default.

### 2. Runtime Path

Keep these as the authoritative behavior:

- `OverrideConditionalDrops()`
- `TryHandleConfiguredDeath()`
- `ApplyGlobalDropInStack()`

All effective custom-drop logic stays here.

### 3. Reload Path

Keep `ApplyIfReady()` as the place that:

- restores prefab snapshots
- validates configured prefabs
- replays changes to already tracked live instances when configuration or game
  data signatures change

This is enough for:

- domain on/off
- YAML reload
- synced payload changes
- source-of-truth transitions

### 4. Snapshot Role

Keep snapshots, but narrow their responsibility to:

- prefab vanilla baseline
- live-object restore during reload
- reference/template generation

Do not treat snapshots as justification for a default awake reconcile queue.

## What Can Be Removed

Primary removal candidates:

- `QueueCharacterDropReconcile`
- `PendingCharacterDropReconciles`
- `PendingCharacterDropReconcileIds`
- `ProcessQueuedReconcileStep`
- `ReconcileCharacterDropInstance`
- `ReconcileCharacterDropInstanceCore`
- `ShouldQueueStartReconcileForPrefab`

After this refactor, `Plugin.Update()` should no longer spend queue budget on
`CharacterDropManager`.

## What Must Stay

Keep:

- `TrackCharacterDropInstance`
- `UntrackCharacterDropInstance`
- `ApplyIfReady`
- `RestoreSnapshots`
- `ReapplyLiveObjects`
- `BootstrapRegisteredCharacterDropsIfNeeded`
- `TryBuildEffectiveCustomDropDefinitions`
- `OverrideConditionalDrops`
- `TryHandleConfiguredDeath`

These are still the pieces that preserve correctness.

## Compatibility Note

There is one real behavior change to watch for:

- another mod may read `characterDrop.m_drops` before death and expect DNS to
  have already materialized custom rows onto the live instance

Current DNS behavior does not strongly guarantee that either, because the awake
reconcile only restores baseline and custom rows are resolved later.

So the practical compatibility risk appears low, but it should still be tested
with:

- loot preview mods
- death-hook mods
- mods that inspect `CharacterDrop.m_drops` outside vanilla drop generation

If that risk turns out to matter, the fallback should be a narrow optional
compatibility path, not restoration of the default queue model.

## Implementation Steps

### Phase 1. Remove Start Queue

- patch `CharacterDrop.Start` to track only
- stop queueing reconcile for new instances
- make `ProcessQueuedReconcileStep()` a no-op or remove its plugin slot

Expected effect:

- eliminate per-instance queue work for character spawns

### Phase 2. Remove Dead Queue Infrastructure

- delete reconcile queue fields
- delete queue/reconcile helpers
- remove character queue rotation from `Plugin.Update()`

Expected effect:

- less plugin update overhead
- simpler manager state

### Phase 3. Verify Reload Semantics

Confirm that:

- YAML reload restores/reapplies correctly
- turning domain off restores vanilla drops
- turning domain back on re-enables configured death/drop behavior
- dirty-prefab targeted reload still works

### Phase 4. Optional Compatibility Follow-Up

Only if needed:

- add a narrow opt-in materialization hook for mods that require pre-death
  `m_drops` inspection

This should not be the default path.

## Verification Checklist

### Spawn behavior

- spawning many configured mobs should no longer spend queue budget in
  `CharacterDropManager`
- no visible hitch from `character` start queue activity

### Loot behavior

- `GenerateDropList` still reflects conditions correctly
- `OnDeath` custom drops still emit correctly
- `onePerPlayer` nearby logic still works
- global `dropInStack` still works

### Reload behavior

- config reload updates tracked live instances correctly
- source-of-truth swap still restores and reapplies correctly
- domain off/on preserves vanilla fallback

## Summary

The `character` domain should move away from:

- `Awake` / `Start` queue reconcile

and toward:

- instance tracking
- prefab baseline restore on reload
- death-time / drop-generation-time effective drop calculation

This keeps current semantics, removes low-value queue work, and matches the
real runtime shape of the domain much better than the existing reconcile model.
