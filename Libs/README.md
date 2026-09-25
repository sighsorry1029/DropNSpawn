# Pinned integration references

Recorded 2026-09-17. These files are deliberate build inputs, not automatically refreshed dependencies.

| File | Source | SHA-256 |
| --- | --- | --- |
| ExpandWorldData.dll | Official Expand World Data 1.71.0 Thunderstore package | `F0CF60CA4FC2A60F8EB767E429886D8E7A8844B3EA65F6640CCB1BBA32DCEABC` |
| ServerSync.dll | Reviewed `valheim-1.0.7-r1` reference | `B4DD786997F4E90D770F09EF3E9D64154754FE7E8EDFB4841795751895B35846` |

EWD source: <https://github.com/JereKuusela/valheim-expand_world_data>, checked at commit `775fc71065cb37369fe565345042fb2178b62b26` (`EWD.VERSION = 1.71`). Binary: <https://thunderstore.io/package/download/JereKuusela/Expand_World_Data/1.71.0/>. EWD is a pinned compile-time reference and an optional runtime dependency (minimum 1.71 when present); it is not ILRepacked into DropNSpawn or installed by the Debug copy target. EWD-typed helpers are behind non-inlined optional call boundaries, and core payload/state types do not expose EWD types. Its public-domain license is retained as `ExpandWorldData.LICENSE.txt`.

ServerSync upstream: <https://github.com/blaxxun-boop/ServerSync>, commit `c57c2aa54e07cdcc7630d6068699ea781622323e`. The reviewed build/source/patch/verification record is `C:\Users\blizz\.codex\references\valheim\integrations\serversync\versions\valheim-1.0.7-r1`. Its original-game build fixes the routed-RPC constant, admin API and buffered PeerInfo handling. The previous vendor SHA `166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60` matched that record's baseline. No second binary patch is applied. License: `ServerSync.LICENSE.txt` (MIT-0).

The existing ILRepack inputs remain DropNSpawn, ServerSync and YamlDotNet. ServerSync stays internal to the final DLL; do not install it as a separate game plugin. The reference's version label describes its preparation target, not proof of complete game compatibility. See `docs/compatibility-1.0.12.md` for the original consumer update and `docs/compatibility-1.0.15.md` for the current client/server, EWD 1.72 and dedicated-startup review. The latter does not replace this pinned EWD 1.71 compile-time API input.
