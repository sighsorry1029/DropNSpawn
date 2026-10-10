# Building and validating DropNSpawn

The mod is a .NET Framework 4.8 library loaded by BepInEx inside Valheim. Use a .NET SDK capable of building the project and running the .NET 8 code generator and regression host, plus the .NET Framework 4.8 targeting pack. The references in `DropNSpawn.csproj` require a local Valheim/BepInEx installation, original game assemblies, and the referenced optional-mod assemblies. Configure their paths through `environment.props` or MSBuild properties; some Unity/reference paths in the project remain machine-specific. Do not publicize the game DLLs. Nonpublic accesses use cached Harmony accessors, method delegates or patch field injection. Compile-time references do not change the plugin's declared runtime dependencies. Pinned EWD/ServerSync sources and hashes are recorded in [Libs/README.md](../Libs/README.md).

Run these commands from the repository root:

```powershell
# Routine build and local game DLL update, without release packages.
dotnet build DropNSpawn.csproj -c Debug -p:DeployToGame=true

# Verify checked-in generated code during Debug validation.
dotnet build DropNSpawn.csproj -c Debug -p:DeployToGame=true -p:ContinuousIntegrationBuild=true

# Regenerate transport sources intentionally after a schema/code-generator edit.
dotnet run --project tools/DropNSpawn.CodeGen -- --project-dir . --mode Generate

# Only after an explicit release request: build and create Thunderstore/Nexus ZIPs.
dotnet build DropNSpawn.csproj -c Release
```

`DeployToGame` defaults to `false`; the routine command explicitly enables copying the final merged DLL to `CopyOutputDLLPath` and any configured secondary paths. Use `-p:DeployToGame=false` when copying is explicitly unwanted, and compare source/destination SHA-256 after deployment. `CreatePackages` defaults to `true` for Release and `false` otherwise; the existing packaging targets update the Thunderstore manifest version and write archives on Windows. Debug builds do not run those packaging targets.

Mod Release Manager 2.1.0 can automatically upload new ZIPs from registered watch folders. Ordinary Release builds use existing packaging and saved manager settings; uploader inspection or changes are a separate task. Packaging-only/no-upload work must use an output folder outside the watched locations; globally pausing the manager is not isolation. Report build/package verification separately from any explicitly requested site publication verification. No release script or marker is required.

`ContinuousIntegrationBuild=true` defaults `CodeGenMode` to `Verify`, which fails on stale generated sources instead of rewriting them. Local builds retain `Generate` as their default; either mode can also be selected explicitly with `-p:CodeGenMode=Verify` or `Generate`.

## Managed regression checks

```powershell
dotnet run --project tools/DropNSpawn.RegressionTests -- --assembly .\bin\Debug\DropNSpawn.dll --game-dir 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
```

Use the merged DLL. To compare transport fixtures against an earlier release, add `--baseline 'C:\path\to\previous\DropNSpawn.dll'`. Preserve that file before rebuilding. If running outside the repository root, supply `--project-dir 'C:\path\to\DropNSpawn'` as well. The baseline must support the same five domains and is used only for transport checks, not for the corrected behavior checks. Unchanged schemas must retain bytes/signatures. The intentional SpawnSystem 3-to-4 / Event 2-to-3 / Spawner 7-to-8 changes instead require reciprocal rejection of incompatible payloads. To inspect a dedicated-server snapshot, add `--managed-dir 'C:\path\to\original\valheim_server_Data\Managed'`.

To validate an external EWD integration build without replacing the pinned compile reference, add `--ewd-dll 'C:\path\to\ExpandWorldData.dll'`. Use its original filename in a dependency folder. EWD 1.73/1.74 checks cover the actual dynamic patch targets/transpiler instructions, AltBiome payload handoff, export extension preservation, and weak-reference lifetime. Pre-integration EWD builds must produce an empty integration patch plan. Temporary metadata variants verify version-independent API selection and explicit rejection of missing core/ownership APIs; the supplied DLL is never modified.

Use `--ewd-dll none` to deliberately deny EWD assembly loading, including the vendored reference. This checks standalone managed paths and missing-extension rejection; omitting the option is not an absence test. With EWD present, the same suite verifies acceptance of the existing extensions. YAML/wire schemas remain unchanged. Local parser rejection is exercised in all affected domains; the full SpawnSystem rejection logger requires a native scene signature and is not executed by this host.

The console host loads the actual assembly and original game dependencies, invokes managed methods through reflection, and exits nonzero on a failed assertion. Coverage includes five-domain serialization/signatures and clone isolation; chunk assembly and delta validation; character empty-list semantics and mutable drop ownership; event selection and metadata/payload lifetime; VNEI event detachment; persistent-event set/clear/omit, export, clone and transport behavior; direct game/EWD member resolution and access levels; static Harmony targets/arguments/state; and the SpawnArea transpiler against original IL. It does not compile a second copy of the production algorithms. This is not a full plugin `Awake`/`PatchAll` or EWD startup test.

The host uses HarmonyX 2.16 only inside the .NET 8 test process because the game's older Harmony build targets Unity Mono. This dependency is not referenced by the mod project or shipped in the mod DLL. Unity objects used as managed fixtures are uninitialized instances with explicitly populated fields; native Unity constructors, scene behavior, and real Harmony installation are not exercised. Assertion counts include individual fields and are not counts of independent gameplay scenarios.

CreatureSpawner cumulative-limit checks cover YAML parsing/normalization, clone/wire/signature preservation, null versus explicit zero, live config defaults, successful/failed counting, owner checks, independent ZDO identities, disable/lower/raise behavior and counter overflow. Replacing a managed ZDO wrapper with the same ID checks its stored integer lookup, **not** an actual world-save/restart round trip. The group transpiler is checked against original IL, including the branch that skips both the candidate and its weight.

## Gameplay checks before release

The additional isolated Mono check uses the installed game's Mono runtime and Harmony, original game DLLs, and the final merged mod:

```powershell
& .\tools\DropNSpawn.MonoChecks\Run.ps1
# Optional server original assemblies, using the same Unity Mono runtime:
& .\tools\DropNSpawn.MonoChecks\Run.ps1 -GameManaged 'C:\path\to\original\valheim_server_Data\Managed'
# Optional official EWD runtime DLL in the disposable test folder only:
& .\tools\DropNSpawn.MonoChecks\Run.ps1 -EwdDll 'C:\path\to\ExpandWorldData.dll'
# Truly absent optional dependency: no EWD DLL is copied to the isolated folder.
& .\tools\DropNSpawn.MonoChecks\Run.ps1 -WithoutEwd
# Optional EpicLoot callback integration (DLL copied only to the test folder):
& .\tools\DropNSpawn.MonoChecks\Run.ps1 -WithoutEwd -EpicLootDll 'C:\path\to\EpicLoot.dll'
# Optional ESP timer-reader IL and runtime adapter checks (no HUD scene):
& .\tools\DropNSpawn.MonoChecks\Run.ps1 -WithoutEwd -EspDll 'C:\path\to\ESP.dll'
# Actual StartupAccelerator wrapper deferral, isolated from the user's profile:
& .\tools\DropNSpawn.MonoChecks\Run.ps1 -EwdDll 'C:\path\to\ExpandWorldData.dll' -StartupAcceleratorDll 'C:\path\to\StartupAccelerator.dll'
# Deliberate failed ownership handoff and rollback (EWD synced-manager layout):
& .\tools\DropNSpawn.MonoChecks\Run.ps1 -EwdDll 'C:\path\to\ExpandWorldData.dll' -StartupAcceleratorDll 'C:\path\to\StartupAccelerator.dll' -EwdHandoffFailure
```

The hidden child process uses a disposable temporary folder and a 30-second timeout. It verifies private field/delegate access, cached delegates observing later Harmony detours and the native managed item-provenance initializer. Test artifacts are retained at the printed path. It starts no Unity scene, game session or networking. Localization targets are checked statically: its static constructor installs scene-related hooks and cannot run in this native-Unity-free harness.

The dungeon RPC lifecycle probe runs the actual manager tick/reset/dispose paths and original `ZRoutedRpc.Register` with only Unity object identity and enabled state substituted. For client and server RPC instances it checks initial registration with no rules, repeated ticks/resets retaining both handlers, disabled ZNet/ZNetScene guards, new-instance registration, and same-instance reactivation. The pre-fix build fails with duplicate key `-238822657` after reset. Real connection failure, lobby transitions and rejoining a dedicated server still require gameplay checks; the probe does not run the game's shutdown sequence or send packets.

The dungeon placement probe runs the shipped clearance, range and candidate-validation code with native prefab/scene access replaced by fixtures: original Valheim 1.0.17 BlobElite/Skeleton capsule values, a flat supporting plane, interior classification, path results and explicit obstruction responses. The 1.4.0 Release DLL fails the BlobElite plane check; the corrected code raises its actual pivot by 0.2 m while retaining the floor as the navigation target. Checks cover unchanged upright/elevated pivots, uniform/nonuniform/mirrored scales, unsupported capsules, missing floors, obstruction rejection, collision masks and the four-meter limit after correction. These are managed shape/query and control-flow checks, not native Unity physics or actual dungeon/remote-client execution. Test both the server and the player clients with the corrected DLL; remote dungeon placement runs on the selected client's loaded scene.

### Content-addressed SpawnSystem timers

`SpawnSystemTimers` owns the new timer identities, binary table, per-live-system cache and owner-only batched writes. The normal compiled-table pipeline carries identities through both clones. No YAML, cfg, network DTO or dependency change is required. The timer transpiler changes only the one native `GetLong(int,long)` and `Set(int,long)` pair in `UpdateSpawnList`; it rejects changed contracts rather than installing a partial replacement. Other native conditions, chance/count logic and exception propagation remain in the original method. DNS-owned `b_` lists participate; events, AltBiome lists and unmanaged rows retain their native paths. Vanilla fallback tables used with the domain disabled are not enrolled.

- IDs use the existing normalized entry-signature algorithm (full SHA-256) plus an ordinal only among identical content. They are computed from the complete accepted configuration before runtime eligibility filtering, independently of filenames, row order, wire RuleId, resolved biome masks and the enable flag. Spawn settings including name, chance, interval, file multiplier result and conditions participate. No fuzzy matching, sidecar registry or public `timerId` is introduced. Explicit defaults versus omitted values or differently ordered list-valued settings can still produce different content signatures; this is not a general semantic-equivalence solver.
- Storage key `DropNSpawn.SpawnSystem.Timers` contains format version 1, record count, then fixed 44-byte records: full 32-byte hash represented as two .NET Guid byte sequences, 4-byte duplicate ordinal and 8-byte game-time ticks. BinaryWriter integers are little-endian. This bounds **ZDO key count and retired-record growth**, not necessarily byte size: hashes are larger than old 4-byte keys. No file-size, network-bandwidth or frame-time reduction is claimed without measurement.
- Missing/new records initialize to the supplied `ZNet.GetTime()` when the native timer read first becomes eligible, so they wait one interval instead of catching up from zero. Existing timers survive reload/reorder; edited rules start again. Entirely identical copies remain separate but have no individual identity beyond their local duplicate ordinal. Only IDs absent from the complete accepted configuration are pruned, when an owning zone next runs its normal list. Disabled or currently ineligible accepted rows are retained. Existing strict rejection of invalid/unresolved definitions is unchanged.
- Only one `ZDO.Set(byte[])` occurs per dirty outer list invocation, including after a later spawn exception. The native ZDO setter handles data revision, client change queue and save dirtiness. Cache lifetime follows the live SpawnSystem; detach/destroy removes it. ZDO identity/byte-array replacement causes re-read. Losing ownership, changing its revision or receiving another payload during a batch discards stale pending writes. Unreadable/unknown-format data is preserved and pauses that zone's managed normal list until valid data arrives. ESP reads do not initialize, prune or write records, even on non-owner clients.
- There is deliberately no migration, old-key scan, old-key removal, save-warning suppression or rollback compatibility. Existing long-key warnings can remain. Both server and clients must use the updated build. Removing/downgrading DNS resumes native timers; the new store is not converted back.

Managed checks execute real YAML parsing for unnamed Gjall day/night rules, content identity/duplicate behavior, codec restart, malformed payload rejection and bounded record cleanup. The Mono probe installs the actual timer Harmony patch on original game assemblies, runs real compiled-list cloning with only native component creation substituted, and exercises original ZDO byte arrays/revisions, client-owner change queuing, non-owner reads, ownership/replacement rejection and original single-ZDO save/load round trips. Unity Object identity and the native persistent-data-path getter are fixture boundaries. This does **not** run a world save, scene, real owner transfer or network session. The optional ESP 1.40.0 DLL check validates its actual text-method IL rewrite and executes DNS's read adapter; full HUD detour preparation needs native Player/Animator initialization and is not established by the scene-free host.

### Other isolated checks

The probe also installs the CreatureSpawner spawn prefix/postfix and group transpiler, checks the original private network-view accessor, and executes the native weighted group loop with exhausted/eligible members. Fixture identity, scene/config-dependent candidate setup, native RNG, logging and actual spawning are isolated; the real limit predicate and original group algorithm run. This verifies managed selection and patch installation, not Instantiate, world persistence or multiplayer ownership transfer. Native RNG/Quaternion JIT-resolution warnings can occur in this scene-free host.

The killer-filter probe runs the shipped DoT bookkeeping with scene identity/health/source lookup and the enabled setting isolated. It covers accepted/rejected poison replacement, PlayerSpawned, mixed known/unknown contributors, actual hit channels, last ticks with zero remaining damage, status replacement, pre-damage snapshots, recovery and cleanup. One hundred status updates without damage perform zero ledger identity lookups; an actual DoT hit performs one. These are operation counts, not frame-time or multiplayer measurements. The relaxed filter accepts a confirmed player-aligned contribution in the lethal DoT pool, regardless of damage share; unrelated active DoT cannot qualify direct/environmental deaths, and unknown-only damage remains denied.

The EpicLoot probe checks absent/missing/unregistered integrations and the normal-timing fallback. With `-EpicLootDll`, it installs the supplied DLL's real `Ragdoll_SpawnLoot_Patch.Postfix`, warms the parent wrapper before adding DNS's callback guard, then exercises immediate versus cleanup calls, other/nested ragdolls, repeated initialization and exception scope restoration. Only vanilla item creation and the two EpicLoot item-spawning helpers are replaced with counters; the actual EpicLoot callback body reads fixture ZDO metadata. Its saved name, level and LuckyLoot rolls remain unchanged. Reviewed with EpicLoot 0.14.13 on original client 1.0.17 and dedicated-server 1.0.16 assemblies; no Unity scene or network session. Full Setup/DestroyNow execution, real item quantities and ownership transfer remain gameplay checks.

This guard deliberately delays EpicLoot rewards until normal ragdoll cleanup. It does not change DNS's killer-filter scope to include EpicLoot rewards, suppress loot exceptions, or introduce retry/rollback for partially created items. The original success-only `s_drops` consumption remains; partial item-spawning exceptions are a separate unresolved risk, not an exactly-once delivery guarantee.

Without EWD, the probe also enumerates all mod types and constructs their Harmony class processors, executes the optional spawn wrappers under Unity Mono, and installs/removes the actual SpawnSystem spawn patch. Standalone faction application captures the new instance immediately after native Instantiate; the EWD-present pre-Awake data path is unchanged. Actual faction behavior, ZDO ownership/rejoin restoration and real plugin startup remain gameplay checks.

With EWD 1.73, the probe also installs the real DNS compatibility patches, removes an already-installed EWD Spawn lifecycle patch, exercises disabled manager entrypoints and patch refreshes, and checks that saved feature flags are unchanged. The EWD event scheduler starts disabled in this probe because installing it requires native Player/Animator initialization. Removal of an already-active EWD scheduler is therefore an in-game check, not a passing isolated scenario. The BepInEx work queue is a managed fixture and ServerSync's deferred startup is not run. Unity Time internal-call resolution warnings can occur while Harmony JITs original methods; those native methods are not executed by the assertions.

The EWD 1.74 synced-manager probe also refreshes the independent Drops patcher, verifies nonempty tables cannot re-enable owned hooks, and leaves unrelated registry callbacks enabled. It supplies the native ZNet server/client role as a fixture while running the actual patch registry and manager methods. Both roles, unconditional lifecycle removal, source-drop immutability and faction preservation are covered; actual world/scheduler/drop execution is not.

The optional StartupAccelerator check copies its original DLL only to the disposable folder and invokes its real wrapper interceptor. Config initialization stays in that folder. It verifies scoped passthrough restoration, preserved user exemptions, unrelated deferral, and queued wrapper rebuilding from current Harmony metadata. `-EwdHandoffFailure` injects an unregistered EWD-owned callback to exercise rejection, DNS cleanup and EWD restoration through batch flush. It does not launch the full preloader/Chainloader or FejdStartup; this is not a full mod-pack startup test.

Build success and managed regression checks do not replace running the mod in Valheim. Use a disposable world/profile and record the game and optional-mod versions.

| Scenario | Check |
| --- | --- |
| Local game and host | Reload each domain, remove an override, disable/re-enable it, and change worlds. Existing instances and newly created ones must restore or apply the same values without mutating cached baselines. |
| Character drops | Compare omitted/null, explicit `drops: []`, invalid-only lists, matching/nonmatching conditional empty lists, and several matching rules. Check actual death/ragdoll drops, VNEI display, one-per-player, stacked drops, and CLLC combinations. |
| Object references | Generate automatic/manual reference files and full scaffolds with location-scoped objects. Verify stable content, location membership, and ordering. |
| Events | Repeat event start/stop/clone cycles in all scheduling modes, reload definitions during active events, and confirm custom spawn fields/objects persist for live clones. Check ties in nearest-event selection and profile frame allocations and retained objects. |
| CreatureSpawner cumulative limit | Test standalone and grouped spawners, failed attempts, live YAML/config changes (null/0/lower/raise), default changes with explicit overrides, and one-time spawners. Rejoin, restart the server/world, transfer ownership, and confirm the saved count and existing living-group cap survive. Exhausted high-weight members must not starve eligible members; disabling the limit must not enable native one-time respawning. |
| SpawnSystem timers | Use unnamed Gjall day/night rows, duplicate rows, reorder/move files, change an interval/chance or multiplier, disable/re-enable, delete rows and submit invalid YAML. Verify only changed rules restart, no shared timers, no pruning by current biome/time, and ESP elapsed time matches owner state. Save/restart, unload/reload zones and transfer client ownership on a dedicated server. Confirm old long keys/other mods' keys are untouched, new array records are bounded, and event/AltBiome timing remains native. Use updated DNS on every peer. |
| Optional integrations | Start with VNEI absent and present; let indexing finish, reload drops, and exit. Confirm later refreshes survive temporary readiness failures and callbacks are detached during plugin destruction. Check EWD, CreatureManager, ESP, and CLLC combinations relevant to the profile. |
| Remote client + dedicated server | Reload on the authoritative server, join/rejoin while a transfer is pending, and verify full/delta fallback and each domain's applied configuration. Check rejected unauthorized/stale messages, out-of-order/duplicate chunks, and ownership transfer. |
| Dungeon RPC lifecycle | Join a loading server, cancel/fail the connection, return to the lobby and reconnect repeatedly, with empty and active dungeon rules. No duplicate RPC registration should occur; after successful rejoin, dungeon position requests/replies and spawn checks must resume. |
| Item safety | With at least two clients, destroy/pick up/kill concurrently around ownership changes and disconnects. Count spawned and collected items; verify no missing or duplicated drops and no repeated spawn customization. |
| Patch/lifecycle compatibility | Inspect actual Harmony targets, order, prefix/postfix state, and cleanup finalizers with the supported mod set. Check Unity initialization/destruction and event cleanup. Plugin shutdown cleanup alone does not promise live DLL unloading/reloading support. |

The 1.0.12 compatibility update retains existing YAML keys, public integration APIs and RPC identifiers. It adds one optional spawn field and increments the two affected domain DTO versions; all peers must run the same updated build. The earlier explicit character empty-list correction is documented in the 1.3.10 release notes. Check parent-owned SpawnArea cap/destruction and location restoration, persistent-event gates and AltBiome weather/spawns, and cheated versus ordinary drops in the scenarios above. The unrelated ragdoll limit/stack and auxiliary-object concerns require separate reproduction before changing their behavior.
