# Performance, Network, And ServerSync Remediation Plan

## Goal

Fix the highest-risk issues in this order:

1. `ServerSync` correctness
2. `spawnsystem` network transport cost and failure behavior
3. remaining runtime and reload hot paths in `object` and `spawner`

This order is intentional.

If sync correctness is wrong, performance work does not matter because clients
can still end up stale or partially configured.

## Priority Summary

### Phase 1. Fix ServerSync correctness for `character` and `spawner`

Why first:

- this is a correctness bug, not just a performance smell
- new clients can fail to receive current server payloads
- stale cache state can hide the bug during testing

Primary issue:

- `NetworkPayloadSyncSupport` publishes `character` and `spawner` payloads
- clients request those manifests
- but payload RPC handlers only serve and receive `object` and `location`

Relevant code:

- [NetworkPayloadSyncSupport.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/NetworkPayloadSyncSupport.cs#L848)
- [NetworkPayloadSyncSupport.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/NetworkPayloadSyncSupport.cs#L922)
- [CharacterDropManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/CharacterDropManager.cs#L343)
- [SpawnerManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/SpawnerManager.cs#L1333)

Required changes:

- add `character` handling to `RPC_RequestPayload`
- add `spawner` handling to `RPC_RequestPayload`
- add `character` handling to `RPC_ReceivePayloadChunk`
- add `spawner` handling to `RPC_ReceivePayloadChunk`
- verify delta/full transfer paths work for both domains
- verify client cache load path works for both domains

Acceptance criteria:

- a fresh client with empty `.cache/network` receives current `character` and
  `spawner` payloads after connect
- cacheless late join and reconnect both converge to current server state
- manifest changes trigger reload on client without requiring preexisting cache
- delta fallback to full transfer works when the client lacks the requested base
  hash

Rollback boundary:

- contained to `NetworkPayloadSyncSupport`

## Phase 2. Move `spawnsystem` off raw synced YAML

Why second:

- current transport is asymmetrical with the other domains
- large authoritative spawn tables are pushed as a single synced string
- decode failure collapses to `""`, which is poor failure behavior

Relevant code:

- [Plugin.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/Plugin.cs#L901)
- [Plugin.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/Plugin.cs#L925)
- [Plugin.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/Plugin.cs#L945)
- [SpawnSystemManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/SpawnSystemManager.cs#L762)

Target model:

- treat `spawnsystem` like `object/location/character/spawner` transport
- publish a manifest through `CustomSyncedValue<string>`
- transfer payload bytes through chunk RPC
- cache by payload hash on clients
- support full transfer first, delta later only if worth keeping

Required changes:

- add `SpawnSystemTransport` to `NetworkPayloadSyncSupport`
- define `spawnsystem` DTO serializer/deserializer
- replace `UpdateSyncedSpawnSystemYaml` with manifest publication
- replace `GetSyncedSpawnSystemYaml` with typed payload fetch
- route `HandleSyncedSpawnSystemYamlChanged` through
  `NetworkPayloadSyncSupport`
- keep `CustomSyncedValue<string>` only as the manifest carrier

Failure model requirements:

- decode/deserialize failure must not silently become empty authoritative config
- if a new payload fails validation, keep last known good payload bytes and log
  the failure

Acceptance criteria:

- fresh client can fetch large `spawnsystem` payload from server cachelessly
- reconnect reuses cached payload when manifest hash matches
- malformed payload does not wipe client-side `spawnsystem` rules
- live server changes converge on clients without pushing a huge raw string

Rollback boundary:

- `Plugin` spawnsystem sync methods
- `NetworkPayloadSyncSupport`
- `SpawnSystemManager` synced-client load path

## Phase 3. Reduce `object` reload cost

Why third:

- this is a major server hitch candidate on reload and source-of-truth swaps
- but it is not as correctness-critical as the sync issues above

Relevant code:

- [ObjectDropManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/ObjectDropManager.cs#L2275)
- [ObjectDropManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/ObjectDropManager.cs#L2292)
- [ObjectDropManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/ObjectDropManager.cs#L2422)
- [ObjectDropManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/ObjectDropManager.cs#L2523)
- [ObjectDropManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/ObjectDropManager.cs#L2778)

Current cost centers:

- restore all prefab snapshots before reapply
- bootstrap live objects with repeated `FindObjectsByType<T>()`
- replay many component kinds even when only a small prefab subset changed

Recommended direction:

- keep snapshot/replay semantics
- narrow bootstrap to touched prefabs only
- introduce prefab-to-component-kind indexes so reload does not probe every
  component family unnecessarily
- separate startup catch-up from hot reload catch-up

Possible cuts:

### Cut 3A

- add instrumentation counters/timers around:
  - `RestoreSnapshots`
  - `EnumerateLiveObjects`
  - `ReapplyActiveEntriesToLiveObjects`

### Cut 3B

- replace repeated global `FindObjectsByType<T>()` scans with a more explicit
  registration model where possible

### Cut 3C

- keep fallback world scan only when no reliable registration set exists

Acceptance criteria:

- reload with small dirty prefab set no longer scans unrelated component kinds
- source-of-truth changes no longer produce large object-domain hitch spikes

## Phase 4. Reduce `spawner` runtime hot-path cost

Why fourth:

- `spawner` still does real work in update-time gameplay paths
- the right fix is structural and overlaps with the hybrid refactor direction

Relevant code:

- [GameDataPatches.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/GameDataPatches.cs#L738)
- [GameDataPatches.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/GameDataPatches.cs#L843)
- [SpawnerManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/SpawnerManager.cs#L602)
- [SpawnerManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/SpawnerManager.cs#L734)
- [SpawnerManager.cs](/C:/Users/blizz/RiderProjects/DropNSpawn/SpawnerManager.cs#L2595)

Current cost centers:

- `SpawnArea.UpdateSpawn` prefix calls runtime reconcile every update
- `CreatureSpawner.UpdateSpawner` prefix calls runtime reconcile every update
- dynamic condition signatures are recalculated on live spawners
- winning-entry selection may lead to restore/reapply

Recommended direction:

- keep runtime reconcile only for entries that actually depend on dynamic state
- split static-at-spawn entries from runtime-sensitive entries during compile
- default most entries to spawn-time application only
- leave a smaller runtime set for:
  - time-of-day changes
  - required environment changes
  - player-base dependent logic
  - other explicitly dynamic conditions

Acceptance criteria:

- static spawner rules no longer participate in update-time reconcile
- runtime signature maps shrink to only truly dynamic entries
- update-time cost scales with dynamic-rule count, not total configured rules

## Phase 5. Instrumentation and regression safety

Why this stays alongside all phases:

- there is no real automated test suite
- regression risk is high across multiplayer, reload, and live world state

Recommended instrumentation:

- per-domain reload timing logs in debug mode
- sync payload size, compressed size, and chunk count logs
- cache hit/miss logs for client payload fetches
- one-line summaries for live object/spawner bootstrap counts

Recommended manual test matrix:

### Sync

- fresh client join with empty cache
- reconnect with warm cache
- server-side YAML edit while client is connected
- source-of-truth handoff

### Gameplay

- move through world with active `spawnsystem`
- spawn many configured mobs
- load locations with altar/item stand overrides
- enter areas with many `SpawnArea` / `CreatureSpawner` instances

### Failure handling

- intentionally corrupt cached network payload
- intentionally invalidate manifest/payload version
- remove base delta cache and verify full-transfer fallback

## Execution Order

1. patch `character`/`spawner` RPC gaps in `NetworkPayloadSyncSupport`
2. validate late-join correctness with empty cache
3. add `spawnsystem` transport to `NetworkPayloadSyncSupport`
4. switch `SpawnSystemManager` synced-client load path to transport payloads
5. instrument `object` reload path and narrow world scans
6. split `spawner` static vs runtime-sensitive entry paths

## What Not To Do

- do not optimize `object`/`spawner` first while sync correctness is still
  ambiguous
- do not move `object` into a fake compile/attach model
- do not keep `spawnsystem` on raw synced YAML long-term once the transport
  layer already exists for other domains

## Short Recommendation

Treat the next work as:

- `Phase 1`: correctness patch
- `Phase 2`: network architecture cleanup
- `Phase 3+`: performance refactor

That sequence gives the best risk reduction per unit of work.
