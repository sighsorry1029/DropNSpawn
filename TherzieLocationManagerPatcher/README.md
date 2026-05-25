# MonstrumLocationManagerFix

`MonstrumLocationManagerFix` is a small BepInEx plugin for Valheim that fixes Therzie's `Monstrum` and `MonstrumDeepNorth` location registration at runtime and adds a narrow compatibility guard for `Expand World Data`.

## What it fixes

In Therzie's location registration flow, `LocationManager.Location.AddLocationToZoneSystem` can use:

- `UnityEngine.Object.Destroy(...)`

when removing the existing enabled `LocationProxy` from `ZoneSystem.m_locationProxyPrefab`.

In this path, deferred destruction can leave the old proxy alive until later in the frame while a replacement proxy is added immediately. That can cause duplicate or conflicting location proxy behavior.

This plugin changes that call to:

- `UnityEngine.Object.DestroyImmediate(...)`

so the cleanup happens immediately during location setup.

It also patches `ExpandWorldData.DataManager.CleanGhostInit(ZNetView)` to safely skip the cleanup when a spawned ghost object has no `ZDO`, avoiding the server-side `NullReferenceException` seen during some custom location spawns.

## Target mods

This plugin patches loaded assemblies that belong to these plugin GUIDs:

- `Therzie.Monstrum`
- `Therzie.MonstrumDeepNorth`
- `expand_world_data`

It targets these methods:

- `LocationManager.Location.AddLocationToZoneSystem`
- `ExpandWorldData.DataManager.CleanGhostInit(ZNetView)`

## How it works

This is a normal BepInEx plugin, not a preloader patcher.

At startup it:

1. Detects whether `Monstrum` and/or `MonstrumDeepNorth` are loaded.
2. Finds `LocationManager.Location.AddLocationToZoneSystem` in those assemblies.
3. Applies a Harmony transpiler.
4. Replaces `Destroy(Object)` with `DestroyImmediate(Object)` in memory.
5. If `Expand World Data` is present, applies a prefix patch to `CleanGhostInit(ZNetView)` that bails out when the spawned ghost object has no `ZDO`.

## Important behavior

- Install location: `BepInEx/plugins`
- Does not modify `Monstrum.dll`
- Does not modify `MonstrumDeepNorth.dll`
- Does not modify `ExpandWorldData.dll`
- Does not write marker files
- Does not clear the BepInEx cache
- Does not require anti-cheat whitelisting for modified Monstrum DLLs

## Installation

Place `MonstrumLocationManagerFix.dll` in:

- `BepInEx/plugins`

Remove old copies of:

- `MonstrumLocationManagerPatcher.dll`

if you previously used the disk-patching version.

## Example log lines

Successful runtime patch:

- `Loading [MonstrumLocationManagerFix 1.0.1]`
- `Patched Monstrum.LocationManager.Location.AddLocationToZoneSystem and replaced 1 Destroy call(s).`
- `Patched MonstrumDeepNorth.LocationManager.Location.AddLocationToZoneSystem and replaced 1 Destroy call(s).`

Already fixed in the loaded target assembly:

- `Verified Monstrum.LocationManager.Location.AddLocationToZoneSystem is already using DestroyImmediate.`

Patch summary:

- `Applied runtime patch to 2 LocationManager method(s).`

Expand World Data compatibility patch:

- `Applied Expand World Data compatibility patch to DataManager.CleanGhostInit(ZNetView).`
- `Skipped Expand World Data ghost cleanup because the spawned ZNetView had no ZDO.`

## Scope and safety

This plugin is intentionally narrow.

- No config file
- No gameplay changes outside the targeted location proxy cleanup
- No patching of unrelated mods beyond the narrow `Expand World Data` ghost cleanup guard
- No permanent modification of other mods' files

## Why this exists

An earlier version solved the issue by rewriting other mods' DLL files on disk. This version keeps the same fix but applies it at runtime instead.
