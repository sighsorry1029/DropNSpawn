using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace DropNSpawn;

internal static class CharacterDropGlobalConfig
{
    private const string CreatureLevelControlPluginGuid = "org.bepinex.plugins.creaturelevelcontrol";

    internal enum CharacterLootSystem
    {
        Vanilla,
        CalculateChance
    }

    private static readonly object DropInStackBlacklistLock = new();
    private static readonly object TrophyLevelMultiplierBlacklistLock = new();
    private static string _dropInStackBlacklistRaw = "";
    private static string _trophyLevelMultiplierBlacklistRaw = "";
    private static HashSet<string> _dropInStackBlacklist = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _trophyLevelMultiplierBlacklist = new(StringComparer.OrdinalIgnoreCase);
    private static bool? _isCreatureLevelControlLoaded;

    private static ConfigEntry<DropNSpawnPlugin.Toggle> _monsterInstantLootDrop = null!;
    private static ConfigEntry<CharacterLootSystem> _characterLootSystem = null!;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _disableCharacterLootScalingWhenCreatureLevelControlLoaded = null!;
    private static ConfigEntry<int> _additionalLootChancePerStarCreature = null!;
    private static ConfigEntry<int> _additionalLootChancePerStarBoss = null!;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _globalDropInStack = null!;
    private static ConfigEntry<string> _dropInStackBlacklistEntry = null!;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _globalTrophyLevelMultiplier = null!;
    private static ConfigEntry<string> _trophyLevelMultiplierBlacklistEntry = null!;
    private static ConfigEntry<float> _onePerPlayerNearbyRange = null!;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        _onePerPlayerNearbyRange = plugin.BindConfigEntry(
            "2 - Character",
            "OnePerPlayer drop check range",
            32f,
            new ConfigDescription(
                "If 0, disables the nearby-player override and uses vanilla server-wide online player count for character-drop onePerPlayer. If greater than 0, counts only living players within this many horizontal XZ meters of the dropping character.",
                new AcceptableValueRange<float>(0f, 100f)),
            synchronizedSetting: true,
            configManagerOrder: 500);
        _monsterInstantLootDrop = plugin.BindConfigEntry(
            "2 - Character",
            "monster instant loot drop",
            DropNSpawnPlugin.Toggle.Off,
            "If on, monster ragdoll loot saved from CharacterDrop is spawned immediately while the ragdoll remains for its vanilla lifetime. The saved ragdoll loot list is consumed so vanilla ragdoll cleanup does not drop the same items again.",
            synchronizedSetting: true,
            configManagerOrder: 450);
        _characterLootSystem = plugin.BindConfigEntry(
            "2 - Character",
            "character loot system",
            CharacterLootSystem.CalculateChance,
            "Vanilla leaves character-drop level scaling to the game and YAML levelMultiplier values. CalculateChance replaces vanilla exponential level scaling with configured per-star amount scaling only for non-trophy item drops whose levelMultiplier is true. The global trophy level multiplier setting separately controls trophies and can override their levelMultiplier value.",
            synchronizedSetting: true,
            configManagerOrder: 440);
        _disableCharacterLootScalingWhenCreatureLevelControlLoaded = plugin.BindConfigEntry(
            "2 - Character",
            "disable DNS character loot scaling when CLLC is loaded",
            DropNSpawnPlugin.Toggle.On,
            "If on and Creature Level & Loot Control is loaded, DropNSpawn does not rewrite this config file but treats character loot system, both per-star loot chance options, and global trophy level multiplier as inactive at runtime. CLLC can then own loot quantities while Character YAML overrides, drop-in-stack, instant loot, and OnePerPlayer range still work. Turn this off only when CLLC loot is Vanilla and DropNSpawn should own loot scaling.",
            synchronizedSetting: true,
            configManagerOrder: 435);
        _additionalLootChancePerStarCreature = plugin.BindConfigEntry(
            "2 - Character",
            "chance for additional loot per star for creatures",
            50,
            new ConfigDescription(
                "Percent of an additional successful item drop per creature star when character loot system is CalculateChance. Also controls trophy scaling for non-boss creatures when global trophy level multiplier is on.",
                new AcceptableValueRange<int>(0, 100)),
            synchronizedSetting: true,
            configManagerOrder: 430);
        _additionalLootChancePerStarBoss = plugin.BindConfigEntry(
            "2 - Character",
            "chance for additional loot per star for bosses",
            50,
            new ConfigDescription(
                "Percent of an additional successful item drop per boss star when character loot system is CalculateChance. Also controls trophy scaling for bosses when global trophy level multiplier is on.",
                new AcceptableValueRange<int>(0, 100)),
            synchronizedSetting: true,
            configManagerOrder: 420);
        _globalDropInStack = plugin.BindConfigEntry(
            "2 - Character",
            "global drop in stack",
            DropNSpawnPlugin.Toggle.Off,
            "If on, all character loot drops in stacks whenever possible, including vanilla drops that are not overridden in YAML. Items listed in global drop in stack blacklist always stay as separate drops. Non-stackable items and single-quantity drops are unchanged. Turning this off only disables the global default; per-entry YAML dropInStack still works unless the item is blacklisted.",
            synchronizedSetting: true,
            configManagerOrder: 400);
        _dropInStackBlacklistEntry = plugin.BindConfigEntry(
            "2 - Character",
            "global drop in stack blacklist",
            "",
            "Comma, semicolon, or newline separated item prefab names that should never use character loot drop-in-stack. Applies to both vanilla character drops and YAML-driven character drops when they pass through CharacterDrop. This blacklist has higher priority than the global default and higher priority than per-entry YAML dropInStack. Example: Coins,TrophyDeer",
            synchronizedSetting: true,
            configManagerOrder: 300);
        _globalTrophyLevelMultiplier = plugin.BindConfigEntry(
            "2 - Character",
            "global trophy level multiplier",
            DropNSpawnPlugin.Toggle.Off,
            "If on, successful character trophy drops keep their original drop chance and scale their final amount by the configured creature or boss additional-loot chance per star. Eligible trophies are kept out of vanilla levelMultiplier amount scaling to avoid very large 2^(level-1) trophy stacks. Items listed in global trophy level multiplier blacklist are exempt and keep their vanilla or YAML levelMultiplier behavior.",
            synchronizedSetting: true,
            configManagerOrder: 200);
        _trophyLevelMultiplierBlacklistEntry = plugin.BindConfigEntry(
            "2 - Character",
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

    internal static bool IsCalculateChanceLootSystemEnabled()
    {
        return _characterLootSystem?.Value == CharacterLootSystem.CalculateChance;
    }

    internal static bool ShouldDisableLootScalingForCreatureLevelControl()
    {
        return _disableCharacterLootScalingWhenCreatureLevelControlLoaded?.Value == DropNSpawnPlugin.Toggle.On &&
               IsCreatureLevelControlLoaded();
    }

    internal static int GetAdditionalLootChancePerStar(bool boss)
    {
        return Mathf.Clamp(boss
            ? _additionalLootChancePerStarBoss?.Value ?? 50
            : _additionalLootChancePerStarCreature?.Value ?? 50, 0, 100);
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

    private static bool IsCreatureLevelControlLoaded()
    {
        if (_isCreatureLevelControlLoaded.HasValue)
        {
            return _isCreatureLevelControlLoaded.Value;
        }

        _isCreatureLevelControlLoaded = Chainloader.PluginInfos.ContainsKey(CreatureLevelControlPluginGuid);
        return _isCreatureLevelControlLoaded.Value;
    }
}
