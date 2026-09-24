# EWD 1.73 Spawn/Event integration

Reviewed and patched on 2026-09-24 for DropNSpawn 1.3.14 and the existing Valheim 1.0.15 target. This is a DNS compatibility boundary, not an EWD fork or a game-version upgrade. Legacy standalone Expand World Spawns / Expand World Events installations are outside this change's supported/tested scope.

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
