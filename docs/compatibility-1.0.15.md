# Valheim 1.0.15 / Expand World Data 1.72 compatibility review

DropNSpawn 1.3.11 was reviewed on 2026-09-19 against the Windows x64 Valheim 1.0.15 client and dedicated server. No DropNSpawn runtime code or network schema change is required. The existing DLL resolves against both game roles and starts on a dedicated server with the reviewed Expand World Data 1.72 hotfix build.

This review does not add legacy branches or configuration migration. Existing YAML remains user-owned.

## Result

| Area | Result |
| --- | --- |
| Direct game/EWD access | All 421 compiled member references resolve against the original 1.0.15 client and dedicated-server assemblies. No publicized assemblies were used. |
| Harmony | All 114 patch methods resolve, their argument/field injections remain valid, and the SpawnArea transpiler executes against each original target. |
| Core spawn/drop targets | SpawnSystem, SpawnArea, CreatureSpawner, CharacterDrop, Ragdoll, object-drop types, event types, ZoneSystem and the EWD-facing game contracts used by DropNSpawn are unchanged from the 1.0.12 target. |
| Changed patched types | Character, Piece and ZNet changed elsewhere. DropNSpawn's Piece Awake/SetCreator/OnDestroy and ZNet Shutdown/GetNrOfPlayers targets are unchanged. Character.ApplyDamage changed only the game's cheated-victim marking branch; DropNSpawn's typed prefix/postfix still observes the same hit, health and last-hit contracts. |
| 1.0.15 item behavior | The game again allows cheated and ordinary items to stack together. DropNSpawn does not patch Inventory or call FindFreeStackItem. It continues to initialize spawned item provenance through ItemDrop.OnCreateNew, matching CharacterDrop's native behavior. |
| Terrain hotfix | DropNSpawn does not patch TerrainComp or terrain generation. The 1.0.15 terrain-duplication fix requires no DropNSpawn change. |
| EWD / AltBiome | The EWD 1.71 compile-time API and reviewed patched 1.72 runtime API both pass. The isolated server completed EWD biome-grid regeneration before DropNSpawn applied the authoritative SpawnSystem table. Native AltBiome spawn lists remain separate. |
| ServerSync | The merged reviewed ServerSync build resolves against both roles and registered DropNSpawn's ConfigSync RPC during the dedicated-server run. A real remote client connection was not exercised. |

## Existing YAML and the 1.0.15 gameplay changes

The official 1.0.15 update increases Writhan world spawning. `DNS_spawnsystem.yml` is a full replacement of the native SpawnSystem table, so a file generated before the update keeps the old values until it is edited or regenerated.

Update the `Writhan` row from the pre-hotfix values as follows:

| Field | Pre-hotfix | Valheim 1.0.15 |
| --- | ---: | ---: |
| `spawnInterval` | `8000` | `4000` |
| `spawnChance` | `5` | `10` |
| `distanceFromCenter` | `5000~8000` | `2000~8000` |

The complete 1.0.15 row generated from the live table is:

```yaml
- prefab: Writhan
  spawnSystem:
    level: 1~3
    groundOffset: 0
    spawnInterval: 4000
    spawnChance: 10
    groupRadius: 1
    noSpawnRadius: 30
    altitude: -2~10
    distanceFromCenter: 2000~8000
    biomes: [Swamp]
    biomeAreas: [Median]
```

For an unmodified generated file, stop the server, preserve a copy, and let DropNSpawn recreate `DNS_spawnsystem.yml` from 1.0.15. For a customized file, edit only the intended row or merge the current `DNS_spawnsystem.reference.yml`; automatic replacement would overwrite server policy, so this review deliberately does not add migration code.

The update also changes the two Jotun Warrior drop tables so both variants can drop medium and heavy armour moulds. The generated `DNS_character.yml` is empty by default and therefore does not mask that native change. A server whose `DNS_character.yml` explicitly supplies `characterDrop.drops` for `JotunWarrior` or `JotunWarriorDualWield` should regenerate `DNS_character.reference.yml` and merge the new drop rows manually.

Official notes: <https://valheim.com/news/patch-1-0-15/>.

## Inputs

| Target | Steam build | Original `assembly_valheim.dll` SHA-256 |
| --- | ---: | --- |
| Client | 25390630 | `59F53FB55D99D22A33E8ED094EEC8D21E9F133543BCE92BC3D80DCE44033ADB1` |
| Dedicated server | 25390671 | `53ED3C85E0CB78F28084B25CE4DAE6F2B929BA9B9C53B788B9A24F950D73F183` |

The client and server came from preserved, validated snapshots. The installed client originals matched the client snapshot. The local dedicated-server installation was used as an analysis and smoke-test input; it is not the user's external server.

DropNSpawn still compiles against the pinned official EWD 1.71 DLL (`F0CF60CA4FC2A60F8EB767E429886D8E7A8844B3EA65F6640CCB1BBA32DCEABC`). Runtime compatibility was additionally checked against the reviewed EWD 1.72 hotfix DLL (`9982DA96859D6D45CCD68C508F56BBCCA4CA94ACF54FDDFFA5B16722F0C55FBE`). Keeping 1.71 as the minimum API contract does not claim that the unpatched EWD world-regeneration behavior is correct on 1.0.15.

## Verification

- Debug compatibility build with `DeployToGame=true` and `CodeGenMode=Verify`: zero warnings and errors. The required final Debug build with `DeployToGame=true` also completed with zero warnings and errors. ILRepack merged DropNSpawn, ServerSync and YamlDotNet. The final built and installed client DLLs both have SHA-256 `038B91FF298C6C09D466460B0C4DDED0FCA09EAB9B5FE5A73D471C1E984B4D7A`.
- Managed compatibility checks passed 597 assertions on the client originals and 597 on the dedicated-server originals with the pinned EWD 1.71 reference. The same two runs passed again while resolving EWD calls and reflection contracts against the patched EWD 1.72 DLL.
- Mono 6.13 isolation passed 14 checks for each game role, including cached nonpublic access, Harmony detour visibility and item provenance initialization.
- A disposable Windows dedicated server loaded BepInExPack 5.4.2350, Expand World Data 1.72 and DropNSpawn 1.3.11. EWD reported a ready grid of 957 sectors and 94 alternate-biome placements; DropNSpawn completed configuration reload and loaded 103 current SpawnSystem rows. Valheim generated a new world, opened and activated its PlayFab server, completed periodic and shutdown saves, and stopped normally. The final deployed DLL was then copied into that server and passed a saved-world restart, active PlayFab state, shutdown save and clean exit. The BepInEx and game logs contained no exception, load, type, missing-method, Harmony or patch failure in either successful run.

The dedicated smoke test had no connected client and used newly generated default configurations. It does not verify the external server's existing world/YAML, real combat and item drops, owner transfer, client synchronization, optional-mod combinations, long-running spawn distribution or terrain appearance. Those remain gameplay checks rather than identified code incompatibilities.
