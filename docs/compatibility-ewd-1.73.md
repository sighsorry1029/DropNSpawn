# EWD 1.73 Spawn/Event integration

Reviewed and patched on 2026-09-24 for DropNSpawn 1.3.14 and the existing Valheim 1.0.15 target. This is a DNS compatibility boundary, not an EWD fork or a game-version upgrade. Legacy standalone Expand World Spawns / Expand World Events installations are outside this change's supported/tested scope.

## Optional dependency update (2026-09-25, DropNSpawn 1.3.15)

DNS now declares EWD as a soft dependency and no longer requires it in the package manifest. EWD-present ownership and AltBiome behavior below are retained. BepInEx load order is preserved, and DNS checks the existing minimum EWD 1.71 before initializing domains; an unsupported installed EWD is not silently treated as absent. Compatibility patch failures still stop initialization rather than allowing overlapping owners.

Without EWD, core domains and native faction overrides remain available. Nonempty spawn `data`/`fields`/`objects` and event `startCommands`/`endCommands` are rejected at the shared local/synced acceptance boundary, before committing a new domain configuration. Empty fields and disabled spawn rows do not require EWD. Existing files are never rewritten. Install EWD on all participating peers when using its features; rejecting an incompatible synced domain is not a new mixed-mod connection negotiation protocol.

Opaque core payloads and non-inlined typed EWD helpers avoid optional type resolution in the standalone path. No extra runtime DLL, transport version, config key or general integration interface was introduced. Test commands and limits are in [development.md](development.md).

Initial implementation validation, before the 1.3.15 version bump:

- Debug build with `DeployToGame=true` and code-generation verification: zero warnings/errors; final merged DLL and local plugin SHA-256 both `D02D79B5484DB76C4C1ED8D380EC94AC335E6AD135A4F30D016D9D9E76500B47`. The unchanged baseline build initially hit the native-PDB writer crash noted below; an identical retry passed before editing.
- Original 1.0.15 client/server contracts: 694 checks per role with EWD loading denied. With official EWD 1.73 and the 1.3.14 Release baseline: 1,171 checks per role, including unchanged five-domain wire/signature fixtures. Pinned EWD 1.71: 1,138 checks on the client with the same baseline. Counts include field assertions, not independent gameplay scenarios.
- Isolated Unity Mono, original client/server assemblies: 20 checks per role with no EWD DLL, 27 per role with EWD 1.73. Native Unity Time/Random internal-call warnings arise during patch JIT in the scene-free host; those native methods are not executed by the checks.
- Real local parser rejection is covered for Spawner/SpawnSystem/Event; previous accepted payload retention is exercised through the actual reload coordinator for Spawner/Event. SpawnSystem's final rejection logger needs a native scene signature and remains a gameplay check. Full plugin startup, actual faction AI/ZDO propagation, world reload, remote clients and AltBiome gameplay were not executed.

Release 1.3.15 validation:

- Debug build with `DeployToGame=true` and code-generation verification, then ordinary Release packaging: zero warnings/errors. Final Debug DLL and local installed copy both have SHA-256 `E3EBC763D97A3F76203C1157FED5724D0260690C28BA2B0F045C3D5402741106`.
- Final Release DLL, original client/server assemblies, compared with the preserved 1.3.14 Release: 1,148 managed checks per role without EWD; 1,171 per role with EWD 1.73; 1,138 on the client with pinned EWD 1.71. All five domain wire/signature fixtures remain compatible.
- Final Release DLL passed 20 isolated Mono checks per role without EWD and 27 per role with EWD 1.73, with the same scene-free Unity internal-call warnings and gameplay limitations above.
- Assembly version is 1.3.15.0; file/plugin/manifest version is 1.3.15. The manifest and fallback template require only BepInExPack Valheim 5.4.2351. Release DLL SHA-256: `33354C4ED6E4A5547361C74D7F1FDB0E9610E74077F563E1DEF417AB5697C614`.
- Thunderstore and Nexus ZIP layouts and every entry's SHA-256 match their build/source inputs. Thunderstore ZIP SHA-256: `E67962E21F62C4EB19E2442422E7FF01D9DDE02C95D498E1FD7E9623CD59F002`; Nexus ZIP SHA-256: `E02A1CB712A45B30C222BEB156F93170988BC9701FE9F5507E826A50D826B0C1`. No actual game/multiplayer session or site-publication verification was performed.

## Ownership

- DNS owns normal SpawnSystem lists, raid definitions/scheduling, and its loot policy while installed. EWD's general Spawn/Event YAML create/read/sync/apply paths and event timing changes are suppressed, including reload callbacks. User EWD configuration values and YAML files are not rewritten or removed.
- EWD continues to own world, biome, AltBiome, data and blueprint infrastructure. `Alt biome data` retains its configured value. Its separate native AltBiome spawn list is not merged into DNS's normal table or affected by DNS's per-file interval multiplier.
- DNS override Off still means baseline restoration / no YAML override. It is not a handoff to EWD. Using EWD as the normal spawn/event owner would need a separate explicit ownership feature; this patch does not add it.
- EWD AltBiome spawn `data`, `fields`, `objects` and `faction` retain their prepared EWD semantics. EWD `drops` conversion remains disabled; use DNS character drop rules instead. Numeric global-key consumption uses DNS once, and remains disabled when the DNS SpawnSystem domain is Off.
- All peers need the matching patched DNS build. Restart after installing/removing mods; hot DLL unloading or ownership switching within a live world is not supported.

## Implementation and maintenance boundary

`ExpandWorldDataCompatibility` is one integration file rather than a general ownership framework. It keeps reflection/patch contracts together; it adds one startup navigation point but avoids spreading EWD-version details across domain managers. There is no added per-frame reflection, polling or scene search.

EWD Awake precedes DNS Awake. Tiny feature-getter detours can miss already-inlined callers, so the integration rewrites the boolean feature reads in the two EWD patcher consumers, the current-event API, and drop conversion. EWD's own disable paths then remove only its overlapping patches and update its patch-state flags. World and common data patches are not removed. Manager prefixes also block direct/reload/sync application independently of patch order.

The original EWD 1.73 patcher does not include AltBiome rows in its custom-spawn activation scan. The integration transfers prepared metadata from EWD's strong dictionaries to the existing DNS weak-key payload path. This reuses DNS's spawn hooks without adding per-spawn reflection or duplicate object creation. Old heightmaps retain valid old-row payloads until regeneration; discarded rows are collectible.

EWD 1.73 exports AltBiome runtime rows back to YAML for server synchronization, but its spawn exporter omits extension fields. A weak-key source record restores only `data/fields/objects/faction` on export, preserving current native values. It does not restore EWD loot. No DNS transport schema, RPC identifier or permission rule changes.

The tested source is upstream commit `2168284b489888b0678c7a8053d6afbfac66665d`, with the official Thunderstore 1.73.0 DLL SHA-256 `556DBB7178949E0AC5AF3547F0F57A301A35BBB4F0C6145AB608F1B0A10C424B`. Relevant binary contracts were checked against that DLL. The pinned EWD 1.71 compile-time reference, dependency minimum and external DLL packaging remain unchanged; EWD before the integrated features receives no patches. Later EWD versions require review, not an assumption of semantic compatibility based on matching signatures alone.

## Verification and remaining gameplay checks

The original Valheim client/server managed assemblies are used; no game DLL was publicized or replaced. Debug build, managed contract checks and isolated Unity Mono tests are distinct from real game execution. Commands and probe boundaries are in [development.md](development.md).

Initial implementation validation, before the 1.3.14 version bump:

- Baseline Debug build: zero warnings/errors; existing managed suite: 663 checks.
- Patched Debug build with code generation verification and `DeployToGame=true`: zero warnings/errors. ILRepack completed and the installed local DNS DLL matched the final build's SHA-256 (`DD823C12BF3B546637DD676261197336A0750429FC59A3C8D6208C02A65AEE1A`). One earlier native-PDB writer process crashed; subsequent unchanged build settings completed successfully. No failed-build DLL was deployed.
- Pinned EWD 1.71: 664 managed checks and 14 Mono checks passed.
- Official EWD 1.73.0: 697 managed checks on each original game role, including all 18 dynamic compatibility targets; 27 Mono checks on each role. Mono reported unresolved Unity Time internal-call warnings while preparing original methods in the scene-free process; the managed assertions completed with exit code 0. This is not a full EWD/game startup result.
- These initial checks preceded release packaging. The pinned libraries and dependency minimum remain unchanged in 1.3.14.

Release 1.3.14 validation:

- Debug build with `DeployToGame=true` and code generation verification, then the ordinary Release build: zero warnings/errors. The local game received the final Debug DLL with matching SHA-256 `375C51ECAE03BB76FD1800ACD6CE347826ECAFE22634343211712AB09E1124D7`.
- The updated Debug DLL passed 1,151 managed checks including transport/signature comparison against the previous 1.3.13 Release DLL. The final Release DLL passed 697 checks per game role with official EWD 1.73.0, plus 664 checks with the pinned EWD 1.71 reference.
- The final Release DLL passed 27 isolated Mono checks per game role, with the same Unity Time warning and scene-free limitations described above.
- Release assembly/file version is 1.3.14.0 and BepInPlugin/manifest version is 1.3.14. ServerSync and YamlDotNet are merged, and EWD remains external. Release DLL SHA-256: `A9A1F45F4AE2A00E92FCD00F5586CB4330A298DF1C057BAECEDF1E7074E568FD`.
- Thunderstore and Nexus ZIP layouts and every entry's hash were checked against their build/source inputs. Thunderstore ZIP SHA-256: `BD31217D320BBE0AF027F0A57FF5E823E21B681508AA0CC1AE4B3270992FD9C3`; Nexus ZIP SHA-256: `F8B736590708A66E3C574B693AC8A55415325BC9200697D5A9BBC9A6142F156E`.

Remaining in-game validation on a host and a dedicated server with a remote client:

1. Start with EWD Spawn/Event/Drop saved both Off and On. DNS must produce one normal spawn table and one event scheduler; EWD YAML/config must remain unchanged. In particular, exercise removal of EWD event patches already installed during EWD Awake.
2. Edit both DNS and EWD general spawn/event YAML while connected. Only DNS may update these systems. Create new zones and reconnect; no stale EWD table may replace DNS's table.
3. Edit an AltBiome spawn's native values and `data/fields/objects/faction` on the server. After regeneration and client sync, verify spawning on a client-owned zone, exactly one auxiliary-object set, and preserved environment/block rules.
4. Toggle DNS SpawnSystem/Event overrides Off and On. EWD remains suppressed; prepared AltBiome data still works. Numeric keys must not be consumed while SpawnSystem is Off or consumed twice while On.
5. Repeat AltBiome reload/world changes and verify old live heightmaps remain valid without retained discarded spawn records.

Full EWD/ServerSync startup, real network synchronization, native spawning, actual drops, environment appearance and long-session gameplay are not established by the isolated tests. EWD itself is neither modified nor installed by DNS's build/package process.
