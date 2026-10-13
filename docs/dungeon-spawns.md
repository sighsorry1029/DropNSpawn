# Periodic dungeon spawns

`DNS_dungeon.yml`, `DNS_dungeon.yaml` and `DNS_dungeon_*.yml/.yaml` add ordinary creatures while players occupy a dungeon. This scheduler is separate from the native world SpawnSystem table, raids, existing fixed spawners and CreatureManager Karma/Enforcer encounters. The first-created file contains English instructions, a commented example and an active empty list (`[]`); installing the update adds no encounters. Remove `[]` and uncomment the example to enable it, or copy the example rule into an active split file. An inactive example is also generated at `examples/DNS_dungeon.sample.yml`.

Starting with DNS 1.3.19, creature entries use `prefab`. The previous `creature` key is rejected, without an alias or automatic migration. Existing configuration and sample files are preserved, so change any old dungeon entry keys manually to `prefab`. The header is added only when creating a missing default configuration; existing files are not rewritten.

The server (or local host) owns these files. **Install this updated DropNSpawn build on both the server and clients.** Remote clients validate the floor, clearance and reachability in their loaded dungeon; the server owns the probability, reservation, limits and final network-object creation. Clients do not need the server's YAML file. No new hard dependency is added.

```yaml
- id: sunkencrypt_ambush
  enabled: true
  locations: [SunkenCrypt4]
  spawnInterval: 60
  spawnChance: 10
  maxAlive: 3
  spawnRadius: 6~12
  creatures:
    - prefab: BlobElite
      weight: 2
    - prefab: DamnedOne_TW
      weight: 1
```

The numbers above are examples, not measured balance recommendations. DamnedOne_TW requires the mod providing that prefab on every peer. Missing/unsupported prefabs skip that attempt and produce a bounded warning, rather than substituting another creature and changing the weights.

| Field | Meaning |
| --- | --- |
| `id` | Required stable, case-sensitive rule identity, at most 128 characters. Keep it unchanged to retain attribution of existing creatures. Duplicate IDs across files reject the reload. |
| `enabled` | Defaults to true. False stops this rule without deleting existing creatures. |
| `locations` | Required exact location prefab names, such as `SunkenCrypt4`, not room or dungeon-generator names. |
| `spawnInterval` | Seconds between attempts; finite and at least 1. Default 60. |
| `spawnChance` | 0–100 percent per player per interval. Default 10; 0 disables attempts. |
| `maxAlive` | 1–100 living creatures plus the rule's reservation per dungeon. Default 3. Counts only creatures created by this rule in that dungeon, including unloaded creatures. |
| `spawnRadius` | Horizontal meters from the selected player, finite range within 2–64; default `6~12`. All connected living players must also be at least the minimum distance from the final point. |
| `creatures` | Required nonempty list of `prefab` names and positive finite relative `weight` values (default 1). Selects exactly one creature after a successful chance roll. |

In the example, each living player in an occupied crypt gets one independent 10% roll every 60 seconds, then a 2:1 creature choice. Four players provide up to four rolls, each successful roll requesting one creature near that player. The starting player rotates randomly each interval so a nearly full cap does not always favor the first connected peer. Once living creatures plus pending reservations reach `maxAlive`, remaining attempts are skipped. Weights are not EWP-style absolute spawn probabilities. Separate crypts and separate rule IDs have independent timers and limits. All matching rules run; the fixed-spawner domain's most-specific-winner policy does not apply here.

`maxAlive` is shared by all players for this rule ID in this dungeon instance. It counts every creature prefab selected by this rule together, not a separate limit per prefab. Vanilla spawns, EWP scripts, fixed spawners, Enforcers and other rule IDs do not count. Attribution remains with the original dungeon even if a creature moves away; unloading it does not free a slot. Death or destruction frees a slot. The same persistent creature reviving counts again. This is a limit on new spawning, not an instruction to delete existing creatures if revival or a lowered limit puts them over the cap.

The identity is the location prefab plus its registered zone, inside the current world. Detection uses the game's `Character.InInterior` check (above 3000 m in Valheim 1.0.17) and the server's location registry for the player's XZ zone. It does not require the player to stay inside the exterior/interior radius around the surface entrance: outer rooms in the same zone belong to the same dungeon and share its cap. No loaded server-side dungeon scene is required. This is not individual-room occupancy; custom interiors spanning multiple zones, relocated away from their registered zone, or stacked as separate instances need additional integration. The selected point must still stay in the same interior zone, within four vertical meters of the player, on a floor with capsule clearance and either a full path or an unobstructed direct route. Existing spawner anchors are preferred but receive the same checks. No valid point means no spawn.

Only ordinary, non-player, non-boss prefabs with a persistent root ZNetView and upright capsule are accepted. They enter the native creature lifecycle: ordinary level/modifier, loot and Karma policies still apply. DNS adds no Enforcer/minion flags, bonus loot, forced pursuit or extra level. Collision checks use the current prefab's capsule. If its lower edge extends below the prefab pivot (as on BlobElite), DNS raises the actual spawn position just enough to place that edge on the supporting floor, then checks clearance and revalidates the position. Already-clear pivots are unchanged, and navigation still targets the floor. Wall, ceiling, creature, distance and interior checks remain active; later size changes from other mods still require gameplay verification.

## Timing, reload and persistence

- First attempt is one full interval after occupancy is first observed. Empty dungeons stop; elapsed empty time and frame stalls never accumulate catch-up rolls.
- Each successful player roll reserves one shared-cap slot before requesting a position. Several players may have pending requests together, including across intervals shorter than the 10-second reply timeout. Reservations count toward the cap and are released on reply, expiry, dispatch failure or observed departure/death/dungeon change. Missing clients/geometry, invalid distance or failed placement do not create a creature.
- The server validates the replying peer and character, current dungeon and distances, living cap and optional CM blocker again. Requests are consumed before creation; duplicate replies cannot create another creature.
- Replies within 0.1 meters of a living DNS dungeon creature's current ZDO position are skipped, including creatures from other rules. This prevents simultaneous clients from accepting the same anchor before the first creature reaches their physics scenes. It is a same-point guard, not a replacement for capsule clearance or runtime-size verification.
- Successful creation writes the prefab and stable rule/dungeon tags before handing ordinary network ownership to the selected peer. Tags use native ZDO saving/synchronization. A world-start metadata lookup restores living counts; no loaded-Character-only cap is used. Dead-but-existing identities remain tracked for possible revival.
- Valid live reload cancels pending requests and starts fresh intervals. Invalid YAML, unknown keys, invalid ranges and duplicate IDs preserve the previous entire configuration. `[]` disables all rules without killing their creatures.
- World shutdown cancels timers/requests and clears runtime caches, preserving saved creatures. Restart begins fresh intervals and restores caps from their tags. Changing an ID is a new rule, not a counter-preserving rename.

## CreatureManager integration

With the updated CreatureManager installed, DNS calls `CreatureManager.CreatureManagerSpawnApi.CanSpawn(GameObject, Vector3)` before dispatch and immediately before creation. This honors the existing independent boss/Enforcer ordinary-spawn blocking options and their current area policy. Karma or level-system Off does not bypass those options.

CM is optional: without it, DNS runs standalone. If CM is installed but lacks the API, or binding/calling the API fails, dungeon spawning is skipped with a bounded warning. This prevents silently bypassing its blocking settings. This feature is introduced in DNS 1.3.18 and its CM integration in CreatureManager 1.2.5. Update the server and all clients together.

The old EWP mining rule remains independent. Keeping it enabled adds mining-triggered creatures alongside these timed encounters; these creatures do not consume this scheduler's cap. Remove/disable that rule yourself if replacing the mining behavior.

## Diagnosing missing spawns

In `BepInEx/config/sighsorry.DropNSpawn.cfg`, temporarily enable:

```ini
[5 - Diagnostics]
Dungeon spawn diagnostics = On
```

The default is `Off`. This is a **local, live setting**, not synchronized from the server: enable it on the server and the clients reproducing the issue. A host records both scheduling and local placement in one log. No separate diagnostic DLL is needed. Normal clients do not receive the server's dungeon rules, so client-only logs cannot explain server probability, caps or rule selection.

Stay in the problem room long enough for several configured spawn intervals and at least one 30-second reporting window. Collect `[DungeonDiag]` lines from both sides' `BepInEx/LogOutput.log`, together with the active rule and approximate reproduction time. Compare the same rule/prefab in a larger room. Then set the option back to `Off`; accumulated diagnostics are discarded. A reload, world change or plugin shutdown also clears them.

| Log reason | Interpretation |
| --- | --- |
| `dungeon location=... DungeonDetected` | A server lookup recognized this registered location. Bounded `zone=x,z position=(x,y,z)` samples distinguish outer-room occupancy from placement failures; these are lookups, including final reply revalidation, not spawn attempts. |
| `dungeon ... OutsideInterior`, `NoLocationInZone`, `EmptyLocationName`, `InvalidPosition` | A player position failed dungeon detection: native interior test, missing location registration for that zone, missing prefab name, or nonfinite coordinates. These are separate from a recognized dungeon failing a rule's `locations` filter. |
| `NoOccupiedDungeon`, `LocationMismatch`, `RuleDisabled`, `WaitingInterval` | No eligible occupied dungeon/rule yet, or the next scheduled interval is not due. These are scheduler observations, not failed placement attempts. |
| `Capacity`, `FinalCapacity` | The shared living-plus-pending limit was reached; limited samples include `alive`, `pending` and `limit`. |
| `ChanceMiss`, `UnsupportedPrefab`, `CMBlocked` | Probability rejected the attempt, the prefab was unsupported, or the optional CreatureManager gate denied it. `FinalCMBlocked` is the gate at the proposed point. |
| `Requested`, `ClientRequest`, `Timeout`, `ClientStateRejected` | Follow dispatch, client reception, timeout or changed/invalid player state. No client requests can simply mean the server has not dispatched any; check its log too. |
| `FloorMissing`, `Height`, `Radius`, `OutsideInterior`, `WrongZone` | A candidate failed floor, vertical/horizontal range or interior/zone validation. `first=` gives one candidate's earliest failure and position. |
| `CapsuleBlocked`, `PlayerDistance` | Insufficient room for the selected prefab, or too close to another living player. Limited capsule samples show collider names and numeric layers; these samples are observations, not a substitute for the original collision result. |
| `PathFailed`, `DirectRouteAccepted`, `PathAndLineBlocked` | A failed full path is not necessarily rejection: an unobstructed direct route still passes. Only failure of both routes rejects here. |
| `PlacementFound`, `PlacementFailed`, `PlacementNotFound` | Local search result, followed by the server accepting a no-position reply. `AnchorCandidate`/`RandomCandidate` count tested candidates, not creatures or whole requests. |
| `FinalRange`, `FinalPlayerDistance`, `OccupiedPoint`, `DungeonChanged` | A proposed point failed the server's final validation, for example because a player moved while a request was pending. |
| `Spawned` | The server created and tagged the ZDO and assigned its owner. This does **not** confirm that the remote client instantiated the creature successfully. |

Counts describe the first rejecting stage of each evaluated candidate/attempt; later checks are not run after rejection. They are not independent probabilities or a total of all obstructions in the room. A request can test several candidates, reject some and still succeed. Compare `PlacementFound`/`PlacementFailed` with individual rejection counts before concluding that spawning is broken.

Each process reports at most 16 detailed groups plus an overflow group, with at most three samples per group per window. Existing `token` values correlate sampled requests and replies within one server session; not every request has a sample, and client/server reporting windows need not align. The logs omit player names and account IDs, but samples can include world positions, configured rule/prefab labels and collider names: review these before sharing.

When `Off`, event recording returns immediately without per-event strings, timing or extra physics queries. When `On`, counters and placement timing add diagnostic overhead; collider samples make at most two additional non-allocating overlap queries per process per reporting window. They run only after an existing capsule rejection and never affect its result. There is no extra spawn attempt, RNG draw, RPC, YAML change or world-state write. Native physics and performance effects still require in-game measurement.

## Validation scope

Managed checks exercise the actual parser, per-player scheduling, shared-cap reservations, request consumption and persisted-ZDO counting logic, including malformed files, duplicate IDs, several pending players in one dungeon, concurrent independent requests, wrong/duplicate/expired replies, missing health fields, revival, removed objects and wrapper replacement. Wrapper replacement verifies stored metadata access, not an actual save/restart.

Before release, use a disposable host and dedicated-server world to verify two players in one crypt, two separate crypts, multiple simultaneous rules, loaded/unloaded geometry, placement near walls/doors, death/revival, disconnect during a request, reload during a request and save/restart cap restoration. Test CM absent/present and both blocker toggles. Builds and managed/isolated Mono checks do not execute Unity physics or network gameplay.
