# Building and validating DropNSpawn

The mod is a .NET Framework 4.8 library loaded by BepInEx inside Valheim. Use a .NET SDK capable of building the project and running the .NET 8 code generator and regression host, plus the .NET Framework 4.8 targeting pack. The references in `DropNSpawn.csproj` require a local Valheim/BepInEx installation, publicized game assemblies, and the referenced optional-mod assemblies. Configure their paths through `environment.props` or MSBuild properties. Compile-time references do not change the plugin's declared runtime dependencies.

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

Mod Release Manager 2.1.0 can automatically upload new ZIPs from registered watch folders. Before Release packaging, confirm the matching manifest/project name, folder, team, target sites, and Hexium installation location. Packaging-only work must use an unwatched output location or have that project's automatic upload disabled; globally pausing the manager is not isolation. Check build success and each site's registration result separately. No release script or marker is required.

`ContinuousIntegrationBuild=true` defaults `CodeGenMode` to `Verify`, which fails on stale generated sources instead of rewriting them. Local builds retain `Generate` as their default; either mode can also be selected explicitly with `-p:CodeGenMode=Verify` or `Generate`.

## Managed regression checks

```powershell
dotnet run --project tools/DropNSpawn.RegressionTests -- --assembly .\bin\Debug\DropNSpawn.dll --game-dir 'C:\Program Files (x86)\Steam\steamapps\common\Valheim'
```

Use the merged DLL. To compare transport fixtures against an earlier release, add `--baseline 'C:\path\to\previous\DropNSpawn.dll'`. Preserve that file before rebuilding. If running outside the repository root, supply `--project-dir 'C:\path\to\DropNSpawn'` as well. The baseline must support the same five domain schemas and is used only for wire/signature comparison, not for the corrected behavior checks.

The console host loads the actual assembly and dependencies, invokes its managed methods through reflection, and exits nonzero on a failed assertion. Coverage includes five-domain serialization/signatures and clone isolation; chunk assembly and delta validation; character empty-list semantics and mutable drop ownership; event selection and metadata/payload lifetime; and VNEI event detachment. It does not compile a second copy of the production algorithms.

The host uses HarmonyX 2.16 only inside the .NET 8 test process because the game's older Harmony build targets Unity Mono. This dependency is not referenced by the mod project or shipped in the mod DLL. Unity objects used as managed fixtures are uninitialized instances with explicitly populated fields; native Unity constructors, scene behavior, and real Harmony installation are not exercised. Assertion counts include individual fields and are not counts of independent gameplay scenarios.

## Gameplay checks before release

Build success and managed regression checks do not replace running the mod in Valheim. Use a disposable world/profile and record the game and optional-mod versions.

| Scenario | Check |
| --- | --- |
| Local game and host | Reload each domain, remove an override, disable/re-enable it, and change worlds. Existing instances and newly created ones must restore or apply the same values without mutating cached baselines. |
| Character drops | Compare omitted/null, explicit `drops: []`, invalid-only lists, matching/nonmatching conditional empty lists, and several matching rules. Check actual death/ragdoll drops, VNEI display, one-per-player, stacked drops, and CLLC combinations. |
| Object references | Generate automatic/manual reference files and full scaffolds with location-scoped objects. Verify stable content, location membership, and ordering. |
| Events | Repeat event start/stop/clone cycles in all scheduling modes, reload definitions during active events, and confirm custom spawn fields/objects persist for live clones. Check ties in nearest-event selection and profile frame allocations and retained objects. |
| Optional integrations | Start with VNEI absent and present; let indexing finish, reload drops, and exit. Confirm later refreshes survive temporary readiness failures and callbacks are detached during plugin destruction. Check EWD, CreatureManager, ESP, and CLLC combinations relevant to the profile. |
| Remote client + dedicated server | Reload on the authoritative server, join/rejoin while a transfer is pending, and verify full/delta fallback and each domain's applied configuration. Check rejected unauthorized/stale messages, out-of-order/duplicate chunks, and ownership transfer. |
| Item safety | With at least two clients, destroy/pick up/kill concurrently around ownership changes and disconnects. Count spawned and collected items; verify no missing or duplicated drops and no repeated spawn customization. |
| Patch/lifecycle compatibility | Inspect actual Harmony targets, order, prefix/postfix state, and cleanup finalizers with the supported mod set. Check Unity initialization/destruction and event cleanup. Plugin shutdown cleanup alone does not promise live DLL unloading/reloading support. |

The structural changes retain configuration keys, YAML models, transport versions, public integration APIs, authority checks, and optional-mod boundaries. The explicit character empty-list correction is a behavior fix documented separately in the 1.3.10 release notes. The unrelated ragdoll limit/stack and auxiliary-object concerns require separate reproduction before changing their behavior.
