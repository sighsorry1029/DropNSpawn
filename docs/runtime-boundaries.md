# Runtime Boundaries

This document records current runtime ownership after the recent platform, character, and despawn refactors. It is intentionally narrow: it describes who owns state and who is allowed to mutate it.

## DropNSpawnPlugin
Owns:
- Unity entrypoints and top-level coordinator wiring
- Shared static accessors used by the runtime platform

Does not own:
- Per-domain runtime state
- Reload/watcher internals
- Manifest sync internals

Reads from:
- `PluginSettingsFacade`
- `DomainRegistry`

Writes to:
- Coordinator startup and teardown only

Called by:
- Unity / BepInEx lifecycle

## PluginBootstrapCoordinator
Owns:
- Config entry binding
- Coordinator initialization order
- Patch and watcher startup

Does not own:
- Reload decisions after startup
- Domain apply logic

Reads from:
- `DropNSpawnPlugin`
- `PluginBoundSettings`

Writes to:
- Bound settings store
- Coordinator instances

Called by:
- `DropNSpawnPlugin.Awake()`

## PluginReloadCoordinator
Owns:
- Config and rules file watchers
- Debounce and queued reload state
- Source-of-truth and domain-toggle cutover flow

Does not own:
- Domain compiled state
- Runtime snapshot/reconcile work

Reads from:
- `PluginBoundSettings`
- `DomainRegistry`

Writes to:
- Reload queues
- Domain reload triggers

Called by:
- `DropNSpawnPlugin`
- File watcher callbacks

## PluginRuntimeWorkCoordinator
Owns:
- Queued game-data refresh state
- Round-robin runtime work scheduling

Does not own:
- Domain-specific apply logic
- Transport sync state

Reads from:
- `DomainRegistry`
- `NetworkPayloadSyncSupport`

Writes to:
- Deferred game-data refresh requests
- Work-lane progress only

Called by:
- `DropNSpawnPlugin.Update()`

## PluginManifestCoordinator
Owns:
- Per-domain synced manifest entries
- Manifest changed handler registration

Does not own:
- Payload chunk transfer logic
- Domain load/apply logic

Reads from:
- `DomainRegistry`
- `NetworkPayloadSyncSupport`

Writes to:
- Manifest synced values

Called by:
- `PluginBootstrapCoordinator`
- `PluginReloadCoordinator`
- `DropNSpawnPlugin`

## DomainModuleDefinition<TEntry>
Owns:
- Immutable domain metadata
- Transport intent for the domain
- Runtime work capabilities for the domain

Does not own:
- Domain load state
- Synced payload lifecycle state
- Compiled or live runtime state

Reads from:
- Domain constructor arguments

Writes to:
- `DescriptorTyped`
- `TransportMetadataTyped`

Called by:
- Domain manager static initialization

## DomainConfigurationRuntime<TEntry, TState>
Owns:
- Domain load state
- Shared synced payload lifecycle glue

Does not own:
- Domain-specific build/apply/reconcile behavior
- Transport transfer state

Reads from:
- `DomainLoadHooks`
- `DomainSyncHooks`

Writes to:
- `DomainLoadState`
- Shared load/cutover transitions

Called by:
- Domain managers

## CharacterDropManager
Owns:
- Character YAML parse/build/apply orchestration
- Character domain front door

Does not own:
- Explicit despawn rule caches
- Boss auto-detect caches
- Live `CharacterDrop` registry
- Despawn tracked state machine

Reads from:
- `CharacterCompiledState`
- `CharacterDespawnRuntime`
- `CharacterBossPolicyRuntime`
- `CharacterDropRuntime`

Writes to:
- Domain runtime orchestration only

Called by:
- Plugin reload/runtime paths
- Game data hooks

## CharacterCompiledState
Owns:
- Compiled character drop definitions
- Runtime drop caches keyed by prefab

Does not own:
- Live objects
- Despawn rule lookup
- Boss policy lookup

Reads from:
- Parsed character entries
- Game data at compile time

Writes to:
- In-memory compiled drop structures only

Called by:
- `CharacterDropManager`

## CharacterDespawnRuntime
Owns:
- Explicit despawn rule compile/cache
- Prefab-name and prefab-hash lookup for despawn tracking
- Bootstrap prefab lookup for despawn registration

Does not own:
- Tracked despawn countdown state
- Same-boss live instance policy
- Live `CharacterDrop` registry

Reads from:
- Active character entries
- `CharacterBossPolicyRuntime`

Writes to:
- Despawn rule lookup caches only

Called by:
- `CharacterDropManager`
- `DespawnRulesManager`

## CharacterBossPolicyRuntime
Owns:
- Auto-detected boss prefab names and hashes

Does not own:
- Live boss instance registry
- Despawn countdown state

Reads from:
- Game data prefab enumeration

Writes to:
- Boss policy lookup caches only

Called by:
- `CharacterDespawnRuntime`
- Boss policy helpers

## CharacterDropRuntime
Owns:
- Live `CharacterDrop` registry
- Character drop snapshot state
- Pending snapshot build state

Does not own:
- Character YAML parsing
- Explicit despawn rule compile
- Boss policy compile

Reads from:
- `CharacterCompiledState`
- Scene and live object state

Writes to:
- Snapshot collections
- Live registry maps

Called by:
- `CharacterDropManager`
- Game data/runtime hooks

## DespawnRulesManager
Owns:
- Tracked despawn state machine
- Observation queue
- Detach queue
- Despawn scheduler

Does not own:
- Despawn rule compilation
- Boss policy caches

Reads from:
- `CharacterDespawnRuntime`
- World/ZDO state
- Player proximity queries

Writes to:
- `TrackedDespawnTargets`
- Scheduled despawn checks
- Countdown and recipient state

Called by:
- Server tick
- `GameDataPatches`

Invariant:
- `ExecuteServerTick()` is the only path allowed to mutate tracked despawn state.
