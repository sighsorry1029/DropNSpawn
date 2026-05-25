using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace DropNSpawn;

internal static class CharacterDropGlobalConfig
{
    private static readonly object DropInStackBlacklistLock = new();
    private static string _dropInStackBlacklistRaw = "";
    private static HashSet<string> _dropInStackBlacklist = new(StringComparer.OrdinalIgnoreCase);

    private static ConfigEntry<DropNSpawnPlugin.Toggle> _globalDropInStack = null!;
    private static ConfigEntry<string> _dropInStackBlacklistEntry = null!;
    private static ConfigEntry<float> _onePerPlayerNearbyRange = null!;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _onePerPlayerNearbyRangeLivingPlayersOnly = null!;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        _globalDropInStack = plugin.BindConfigEntry(
            "3 - Character",
            "Global Drop In Stack",
            DropNSpawnPlugin.Toggle.Off,
            "If on, all character loot drops in stacks whenever possible, including vanilla drops that are not overridden in YAML. Items listed in the Drop In Stack Blacklist always stay as separate drops. Non-stackable items and single-quantity drops are unchanged. Turning this off only disables the global default; per-entry YAML dropInStack still works unless the item is blacklisted.",
            synchronizedSetting: true);
        _dropInStackBlacklistEntry = plugin.BindConfigEntry(
            "3 - Character",
            "Drop In Stack Blacklist",
            "",
            "Comma, semicolon, or newline separated item prefab names that should never use character loot drop-in-stack. Applies to both vanilla character drops and YAML-driven character drops when they pass through CharacterDrop. This blacklist has higher priority than the global default and higher priority than per-entry YAML dropInStack. Example: Coins,TrophyDeer",
            synchronizedSetting: true);
        _onePerPlayerNearbyRange = plugin.BindConfigEntry(
            "3 - Character",
            "One Per Player Nearby Range",
            32f,
            new ConfigDescription(
                "If 0, disables the nearby-player override and uses vanilla server-wide online player count for character-drop onePerPlayer. If greater than 0, counts only players within this many horizontal XZ meters of the dropping character.",
                new AcceptableValueRange<float>(0f, 100f)),
            synchronizedSetting: true);
        _onePerPlayerNearbyRangeLivingPlayersOnly = plugin.BindConfigEntry(
            "3 - Character",
            "One Per Player Nearby Range Living Players Only",
            DropNSpawnPlugin.Toggle.Off,
            "If on, the One Per Player Nearby Range override counts only living players and excludes dead players waiting to respawn. If off, it counts all nearby players, matching the broader vanilla-style nearby-presence behavior.",
            synchronizedSetting: true);
    }

    internal static bool IsGlobalDropInStackEnabled()
    {
        return _globalDropInStack?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static float GetOnePerPlayerNearbyRange()
    {
        return Mathf.Max(0f, _onePerPlayerNearbyRange?.Value ?? 100f);
    }

    internal static bool IsOnePerPlayerNearbyRangeLivingPlayersOnly()
    {
        return _onePerPlayerNearbyRangeLivingPlayersOnly?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static bool IsDropInStackBlacklisted(string? prefabName)
    {
        if (prefabName == null)
        {
            return false;
        }

        string normalizedPrefabName = prefabName.Trim();
        if (normalizedPrefabName.Length == 0)
        {
            return false;
        }

        lock (DropInStackBlacklistLock)
        {
            EnsureDropInStackBlacklistCache();
            return _dropInStackBlacklist.Contains(normalizedPrefabName);
        }
    }

    private static void EnsureDropInStackBlacklistCache()
    {
        string raw = _dropInStackBlacklistEntry?.Value ?? "";
        if (string.Equals(_dropInStackBlacklistRaw, raw, StringComparison.Ordinal))
        {
            return;
        }

        _dropInStackBlacklistRaw = raw;
        _dropInStackBlacklist = raw
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
