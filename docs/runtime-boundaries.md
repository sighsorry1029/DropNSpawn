# Runtime Boundaries

This document records current runtime ownership after the platform refactor and the location/despawn feature split. It is intentionally narrow: it describes who owns state and who is allowed to mutate it.

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
- Character drop reference and scaffold generation

Does not own:
- Shared transport transfer state
- Top-level reload scheduling
- Runtime work scheduling

Reads from:
- `CharacterCompiledState`
- `CharacterDropRuntime`

Writes to:
- Character configuration runtime state
- Character compiled/apply bookkeeping

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

## ObjectDropManager
Owns:
- Object YAML parse/build/apply orchestration
- Object component profile catalog
- Prefab snapshots and live reconcile queues
- Object reference and location-reference generation

Does not own:
- Plugin reload/watcher state
- Shared transport transfer state
- VNEI reflection internals

Reads from:
- `ConfigurationDomainHost`
- `NetworkPayloadSyncSupport`
- `ObjectGameDataPatches`

Writes to:
- Object configuration runtime state
- Object snapshot/runtime drop state
- Object live registry and reconcile state

Called by:
- Plugin reload/runtime paths
- Object Harmony patches
- VNEI compatibility hooks

## SpawnerManager
Owns:
- Spawner YAML parse/build/apply orchestration
- Location selector/provenance caches
- Live `SpawnArea` and `CreatureSpawner` registries
- Spawner reference and location-reference generation

Does not own:
- Location gameplay rules
- Plugin reload/watcher state
- Shared transport transfer state

Reads from:
- `ConfigurationDomainHost`
- `NetworkPayloadSyncSupport`
- `SpawnerLocationGameDataPatches`

Writes to:
- Spawner configuration runtime state
- Live spawner runtime/reconcile state
- Selector and provenance caches

Called by:
- Plugin reload/runtime paths
- Spawner Harmony patches
- Console inspection

## SpawnSystemManager
Owns:
- SpawnSystem YAML parse/build/apply orchestration
- Prepared entry and compiled table pipeline
- Live `SpawnSystem` attach/retirement state
- SpawnSystem reference generation

Does not own:
- `ZoneSystem` lifecycle
- Plugin reload/watcher state
- Shared transport transfer state

Reads from:
- `ConfigurationDomainHost`
- `NetworkPayloadSyncSupport`
- `BiomeResolutionSupport`

Writes to:
- SpawnSystem configuration runtime state
- Prepared entry cache
- Compiled table/runtime metadata state

Called by:
- Plugin reload/runtime paths
- SpawnSystem Harmony patches
- ESP compatibility refresh hooks

## NetworkPayloadSyncSupport
Owns:
- Per-domain payload manifests
- Payload serialization, compression, chunking, deltas, and cache state
- Client request and inbound transfer queues
- Main-thread payload commit and reload queues

Does not own:
- Domain parse/apply semantics
- ServerSync manifest entries
- Plugin reload decisions

Reads from:
- `DomainRegistry`
- Generated transport schemas and codecs
- Per-domain transport profiles and metadata

Writes to:
- Transport runtime state
- Published payload and transfer artifact caches
- Pending network work queues

Called by:
- `PluginManifestCoordinator`
- `PluginRuntimeWorkCoordinator`
- Domain configuration runtimes
