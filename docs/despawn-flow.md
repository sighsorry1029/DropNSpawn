# Despawn Flow

This flow reflects the current `bootstrap/register` model. The older periodic fallback sweep model no longer exists.

## Producers

These paths do not mutate tracked despawn state directly. They only enqueue observations or detaches.

- `CharacterDrop.Start` -> loaded character observation
- `ZDOMan.CreateNewZDO(...)` -> created ZDO observation
- Dirty bootstrap scan -> bootstrap-scan observations
- `ZNetView.ResetZDO` -> detach persist request

## Reducers

`DespawnRulesManager.ExecuteServerTick()` is the only state writer.

Tick order:
1. Apply pending observations.
2. If a bootstrap scan is pending, enqueue bootstrap observations and apply them.
3. Apply pending detach persists.
4. Process due scheduled countdown checks.
5. Remove invalid tracked targets.

## Observation Path

`ApplyObservation(...)` is the registration reducer.

It is responsible for:
- resolving the target ZDO
- resolving prefab name/hash hints
- resolving despawn tracking rules from `CharacterDespawnRuntime`
- creating or refreshing tracked state
- scheduling the first or next check

It is not responsible for:
- countdown persistence before unload
- direct destroy/remove execution outside the tick loop

## Detach Path

`ApplyDetachPersist(...)` is the detach reducer.

It is responsible for:
- preserving countdown state when a tracked loaded object is about to lose its live `ZNetView`
- switching tracked state to ZDO-position-based evaluation

It is not responsible for:
- registering new despawn targets

## Scheduler Path

Tracked targets are checked by due time, not by global full scan.

- Active countdown targets: short interval
- Idle targets: longer interval
- New observations: immediate initial evaluation

## Key Invariant

`TrackedDespawnTargets` must only be mutated from the server tick reducer path.

When adding a new input source:
1. enqueue an observation or detach request
2. let the reducer resolve/update state
3. do not write tracked despawn state directly from the producer
