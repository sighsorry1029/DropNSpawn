using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace DropNSpawn;

internal static class PluginBoundSettings
{
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? ServerConfigLocked { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableObjectOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? StackResourceDrops { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? StackHarvestDrops { get; set; }
    internal static ConfigEntry<string>? ObjectStackItemBlacklist { get; set; }
    private static (string? Text, HashSet<string>? Items) _objectStackBlacklist;
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableCharacterOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableSpawnerOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableSpawnSystemOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? AutoImportUnconfiguredSpawnPrefabs { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableEventOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? DisableGlobalKeySpawnSystemEntriesInLowTierBiomes { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? DungeonSpawnDiagnostics { get; set; }

    internal static HashSet<string> ParsePrefabNameSet(string raw) => raw
        .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(value => value.Trim())
        .Where(value => value.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Called from synchronous item delivery on the Unity thread. A live config change
    // takes effect at the next item without reapplying YAML or scanning scene objects.
    internal static bool IsObjectStackItemBlacklisted(GameObject prefab)
    {
        string? raw = ObjectStackItemBlacklist?.Value;
        if (raw != _objectStackBlacklist.Text)
            _objectStackBlacklist = (raw, string.IsNullOrEmpty(raw) ? null : ParsePrefabNameSet(raw!));
        return _objectStackBlacklist.Items is { Count: > 0 } items && items.Contains(prefab.name);
    }

    internal static void Clear()
    {
        ServerConfigLocked = null;
        EnableObjectOverrides = null;
        StackResourceDrops = null;
        StackHarvestDrops = null;
        ObjectStackItemBlacklist = null;
        _objectStackBlacklist = default;
        EnableCharacterOverrides = null;
        EnableSpawnerOverrides = null;
        EnableSpawnSystemOverrides = null;
        AutoImportUnconfiguredSpawnPrefabs = null;
        EnableEventOverrides = null;
        DisableGlobalKeySpawnSystemEntriesInLowTierBiomes = null;
        DungeonSpawnDiagnostics = null;
    }
}
