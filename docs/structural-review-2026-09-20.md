# Structural review — 2026-09-20

## Scope and baseline

- Started on the real `main` checkout at `d92fb7248dfd272d55a913891b907ccaf4e52815`, with a clean worktree. Only the primary session edited, built and committed; parallel agents performed read-only investigation and diff review.
- The solution builds one .NET Framework 4.8 BepInEx plugin, `DropNSpawnPlugin : BaseUnityPlugin`. It is not a preloader patcher. Unity `Awake`, `Update` and `OnDestroy` own coordinator startup, scheduled work and cleanup.
- `DropNSpawn.csproj` explicitly includes 143 C# files, including three generated transport files. `translations/English.yml` is the embedded translation resource. The .NET 8 generator and managed regression host, plus the isolated game-Mono probe, are separate tools.
- The final DLL merges DropNSpawn, pinned `Libs/ServerSync.dll` and YamlDotNet via `ILRepack.targets`. EWD is an external hard dependency (1.71 API); CreatureManager is a declared soft dependency. ESP, VNEI, MWL, faction and CLLC-related paths retain their separate runtime detection/compatibility policies. No dependency or packaging target changed.
- The applicable global reference index is `C:\Users\blizz\.codex\references\valheim\INDEX.md` (the request's `blizz.codex` path does not exist). The installed original client `assembly_valheim.dll` matches the 1.0.15 / build 25390630 SHA-256 in [the compatibility record](compatibility-1.0.15.md). The preserved original dedicated-server input is 1.0.15 / build 25390671. The reviewed ServerSync and pinned EWD hashes still match [Libs/README.md](../Libs/README.md).
- No game assemblies were publicized, recollected or re-extracted. No game support range, mod version, manifest, configuration schema, DTO version or public integration API changed.

The review covered entrypoints, actual compilation/merge inputs, major domain ownership and call paths, live apply/reconcile, reload/transport boundaries, reference projections, optional integrations, existing tests and relevant Git history. It was not a line-by-line audit of every method. Generated transport implementation, vendor library internals, exploratory decompilation files excluded from compilation, native game/Unity internals, full resource contents and external mods' complete implementations were not refactored or exhaustively reviewed.

## Structure decisions

| Area | Assessment | Decision |
| --- | --- | --- |
| Plugin and reload/runtime/manifest coordinators | Mostly balanced; toggle state repeated the same domain list | Use the existing toggle map for capture and comparison. Keep watcher, authority, manifest and main-thread work ownership separate. |
| Character drops | Manager is concentrated, but compile/live/output boundaries are meaningful; baseline rows were represented twice | Remove the redundant snapshot DTO/list/clone path. Keep live drop ownership and the compiled/runtime caches separate. |
| Object drops | Numerous partial files increase navigation, but lazy evaluation, component policies, live registry and restoration have distinct responsibilities | Keep those boundaries. Do not unify Object later-wins/table policy with Character conditional-drop union. |
| Spawner | Some over-fragmentation, but selector, provenance, Unity lifetime and owner checks differ | Keep the state boundaries rather than replacing the stores with one generic framework. |
| SpawnSystem | Substantial but justified separation | Keep prepared data, compiled templates, attached per-system clones and retired hosts/epochs distinct. |
| Events | Concentrated file, without sufficient evidence that another layer would simplify changes | Keep scheduling modes, metadata lifetime and reentrancy protection. No file-size-driven split. |
| Network and optional integrations | Complex but appropriately separated by authority, thread and dependency lifecycle | Preserve sender/request/epoch checks, queues and cancellation, and integration-specific readiness/cleanup. |

No new runtime file, interface, cache or dependency was introduced. Reduced allocations are expected consequences of deleting duplicate snapshot storage, not measured frame-time or memory improvements. Toggle capture now uses one small array per config-file reload instead of a fixed-field value type; this is a deliberate maintenance tradeoff, not a claimed performance optimization.

## Independent changes

### `36267b2` — one owned Character baseline

`CaptureSnapshot` previously cloned the same seven fields into both `CharacterDropItemSnapshot` rows and `CharacterDrop.Drop` rows. Commit `8f636ea` had to change non-item level-multiplier normalization in both paths, demonstrating real change propagation.

The redundant `Drops`, `CharacterDropItemSnapshot` and `CloneSnapshotDrops` are removed. Reference/scaffold and VNEI projections read `BuiltDrops`; the no-snapshot VNEI fallback still takes a normalized detached clone. Neither projection returns baseline rows to external code. All live apply/restore paths retain `CloneDrops`, including the ownership and empty-list behavior established in `926fcdc`.

Regression coverage checks baseline normalization, all preserved scalar fields, output multiplier omission, and upstream/baseline/restored-list isolation using production methods. Native prefab names/components and complete VNEI/reference output still require a Unity scene.

### `3e6cc62` — one domain-toggle mapping

`PluginReloadCoordinator._domainToggles` now drives both raw-value capture and changed-domain selection. The fixed-field `Plugin.DomainToggleState`, repeated domain lookups and manual five-domain comparison are removed. Commit `6ae67e9` previously changed these places together when the location domain was removed.

Actual `ConfigEntry<Toggle>` fixtures verify single/multiple changes, unchanged/reverted values, sender routing and nonstandard enum transitions. Raw values are intentionally retained: mapping only On/Off bits would miss `(Toggle)2` to `(Toggle)3`. Suppression, `Config.Reload`, domain execution order, exception/finally handling, authority transitions and event subscriptions remain unchanged. No real profile is written by these fixtures.

### Documentation corrections

The coexistence proposal is marked historical and linked to current policies. Its removed location domain and proposed Spawner replay behavior must not guide current refactoring. The override guide's invalid Character example now actually shows unsupported nested conditions; conditionless entries remain valid. These corrections change no runtime policy.

## Preserved boundaries and deferred candidates

- `b689157` deliberately distinguished Unity's destroyed-object equality from `ReferenceEquals` while changing Spawner registry/state cleanup. Similar-looking null guards are not interchangeable.
- `eff8680` changed SpawnSystem attachment and location/reconcile paths together; their separate epochs/hosts still represent different lifetimes. Transport role epochs, request IDs and publish/processing versions are not redundant authorization checks.
- `926fcdc` added VNEI event detachment and retry behavior. EWD readiness reflection caches member lookups, not readiness results; caching the result could hide a readiness or reconnect transition.
- Event stopped-event lists remain local because OnStop callbacks may reenter scheduling. No shared scratch cache was added.
- Small candidates such as the one-bool ZoneSystem Harmony state wrapper, Character registry enumeration allocations and Object reconcile forwarding were left unchanged: their benefit is lower than the selected ownership/mapping changes and would require additional runtime-focused coverage.
- `ApplyCreatureSpawnerSpawnOverrides` returns before auxiliary-object creation when the spawned root lacks Character. This is a pre-existing, already documented reproduction candidate, not a newly confirmed bug. SpawnArea pending-attempt cleanup on exceptional/non-faction paths also warrants a separate lifecycle investigation. Neither policy was changed here.

## Verification

Baseline:

- `dotnet build DropNSpawn.sln -c Debug -p:DeployToGame=true -p:CodeGenMode=Verify`: zero warnings/errors, generated sources unchanged, final installed DLL hash matched.
- Original client and server managed checks: 619 assertions each without the optional MWL fixture. With the supplied MWL manifest, client checks passed 884 assertions and inspected 263 IDs without loading bundles.
- Original client/game Mono isolation: 14 checks. The first optional MWL invocation referenced the old, now absent `wml` profile and failed in the native-free harness; the provided `zxcvzxcv` profile resolved that fixture-path issue. This was not a mod regression.

After each code change: Debug build/deployment, baseline transport comparison, relevant managed fixtures, installed DLL SHA-256 equality and diff review passed before its commit. Final checks:

- Client and dedicated original assemblies: **1,366 assertions each**, including comparison with the preserved `d92fb72` DLL and the 263-ID MWL manifest.
- **426** compiled game/EWD member references resolved with original access levels; **114** Harmony patch methods resolved; the existing SpawnArea transpiler executed against the original IL. No new nonpublic game access was introduced.
- Installed Unity Mono 6.13 / Harmony 2.9 isolation: **14 checks per role**. These are managed fixtures, not Unity scenes or network sessions.
- Exported public/protected type, method and field metadata matched the baseline (**28 signatures**). This is not a full execution test of indirect integrations.
- Final Debug/installed DLL SHA-256: `6A098572F379163A54518A5D26CCAF4D76F346144FAE67D5A9A8CF596B803E65`.

Assertion counts include field checks, not independent gameplay scenarios. This task did not launch Valheim, a host, a dedicated server or a multiplayer connection. Remaining execution checks: actual item/non-item reference and VNEI output parity; cfg watcher/live toggle reload; disable/remove/restore drops on live creatures; server authority cutover/rejoin; owner transfers and item counts; MWL first-generation latency/peak memory on Steam Deck. Prior dedicated startup results in the compatibility document are historical, not a test of these commits.

Version stays **1.3.12**. No Release build, ZIP creation, publication, branch/worktree creation or push was performed. Debug deployment updated only the final plugin DLL under the configured Steam `BepInEx/plugins` path; the supplied Gale profile was read-only test input.
