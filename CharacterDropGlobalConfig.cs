using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace DropNSpawn;

internal static class CharacterDropGlobalConfig
{
    private static readonly object DropInStackBlacklistLock = new();
    private static readonly object TrophyLevelMultiplierBlacklistLock = new();
    private static string _dropInStackBlacklistRaw = "";
    private static string _trophyLevelMultiplierBlacklistRaw = "";
    private static HashSet<string> _dropInStackBlacklist = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _trophyLevelMultiplierBlacklist = new(StringComparer.OrdinalIgnoreCase);

    private static ConfigEntry<DropNSpawnPlugin.Toggle> _monsterInstantLootDrop = null!;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _globalDropInStack = null!;
    private static ConfigEntry<string> _dropInStackBlacklistEntry = null!;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _globalTrophyLevelMultiplier = null!;
    private static ConfigEntry<string> _trophyLevelMultiplierBlacklistEntry = null!;
    private static ConfigEntry<float> _onePerPlayerNearbyRange = null!;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        _onePerPlayerNearbyRange = plugin.BindConfigEntry(
            "3 - Character",
            "OnePerPlayer drop check range",
            32f,
            new ConfigDescription(
                "If 0, disables the nearby-player override and uses vanilla server-wide online player count for character-drop onePerPlayer. If greater than 0, counts only living players within this many horizontal XZ meters of the dropping character.",
                new AcceptableValueRange<float>(0f, 100f)),
            synchronizedSetting: true,
            configManagerOrder: 500);
        _monsterInstantLootDrop = plugin.BindConfigEntry(
            "3 - Character",
            "monster instant loot drop",
            DropNSpawnPlugin.Toggle.Off,
            "If on, monster ragdoll loot saved from CharacterDrop is spawned immediately while the ragdoll remains for its vanilla lifetime. The saved ragdoll loot list is consumed so vanilla ragdoll cleanup does not drop the same items again.",
            synchronizedSetting: true,
            configManagerOrder: 450);
        _globalDropInStack = plugin.BindConfigEntry(
            "3 - Character",
            "global drop in stack",
            DropNSpawnPlugin.Toggle.Off,
            "If on, all character loot drops in stacks whenever possible, including vanilla drops that are not overridden in YAML. Items listed in global drop in stack blacklist always stay as separate drops. Non-stackable items and single-quantity drops are unchanged. Turning this off only disables the global default; per-entry YAML dropInStack still works unless the item is blacklisted.",
            synchronizedSetting: true,
            configManagerOrder: 400);
        _dropInStackBlacklistEntry = plugin.BindConfigEntry(
            "3 - Character",
            "global drop in stack blacklist",
            "",
            "Comma, semicolon, or newline separated item prefab names that should never use character loot drop-in-stack. Applies to both vanilla character drops and YAML-driven character drops when they pass through CharacterDrop. This blacklist has higher priority than the global default and higher priority than per-entry YAML dropInStack. Example: Coins,TrophyDeer",
            synchronizedSetting: true,
            configManagerOrder: 300);
        _globalTrophyLevelMultiplier = plugin.BindConfigEntry(
            "3 - Character",
            "global trophy level multiplier",
            DropNSpawnPlugin.Toggle.Off,
            "If on, successful character trophy drops keep their original drop chance and scale their final amount by 50% per character level above 1. Eligible trophies are kept out of vanilla levelMultiplier amount scaling to avoid very large 2^(level-1) trophy stacks. Items listed in global trophy level multiplier blacklist are exempt and keep their vanilla or YAML levelMultiplier behavior.",
            synchronizedSetting: true,
            configManagerOrder: 200);
        _trophyLevelMultiplierBlacklistEntry = plugin.BindConfigEntry(
            "3 - Character",
            "global trophy level multiplier blacklist",
            "",
            "Comma, semicolon, or newline separated trophy item prefab names that should not use global trophy amount scaling. Blacklisted items follow the vanilla or YAML levelMultiplier value exactly. Example: TrophyEikthyr,TrophyDragonQueen",
            synchronizedSetting: true,
            configManagerOrder: 100);
    }

    internal static bool IsGlobalDropInStackEnabled()
    {
        return _globalDropInStack?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static bool IsMonsterInstantLootDropEnabled()
    {
        return _monsterInstantLootDrop?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static bool IsGlobalTrophyLevelMultiplierEnabled()
    {
        return _globalTrophyLevelMultiplier?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static float GetOnePerPlayerNearbyRange()
    {
        return Mathf.Max(0f, _onePerPlayerNearbyRange?.Value ?? 100f);
    }

    internal static bool IsOnePerPlayerNearbyRangeLivingPlayersOnly()
    {
        return true;
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

    internal static bool IsTrophyLevelMultiplierBlacklisted(string? prefabName)
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

        lock (TrophyLevelMultiplierBlacklistLock)
        {
            EnsureTrophyLevelMultiplierBlacklistCache();
            return _trophyLevelMultiplierBlacklist.Contains(normalizedPrefabName);
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
        _dropInStackBlacklist = ParseNameSet(raw);
    }

    private static void EnsureTrophyLevelMultiplierBlacklistCache()
    {
        string raw = _trophyLevelMultiplierBlacklistEntry?.Value ?? "";
        if (string.Equals(_trophyLevelMultiplierBlacklistRaw, raw, StringComparison.Ordinal))
        {
            return;
        }

        _trophyLevelMultiplierBlacklistRaw = raw;
        _trophyLevelMultiplierBlacklist = ParseNameSet(raw);
    }

    private static HashSet<string> ParseNameSet(string raw)
    {
        return raw
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
