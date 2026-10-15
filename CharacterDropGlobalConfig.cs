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
    private const float DefaultOnePerPlayerNearbyRange = 32f;
    private const string DefaultMonsterInstantLootDropBlacklist = "Dragon, Hatchling";

    internal enum CharacterLootSystem
    {
        Vanilla,
        CalculateChance
    }

    internal enum ChanceOnlyDropChanceScaling
    {
        Vanilla,
        Linear
    }

    private static readonly object DropInStackBlacklistLock = new();
    private static readonly object TrophyLevelMultiplierBlacklistLock = new();
    private static readonly object PlayerAlignedKillerBlacklistLock = new();
    private static readonly object MonsterInstantLootDropBlacklistLock = new();
    private static string _dropInStackBlacklistRaw = "";
    private static string _trophyLevelMultiplierBlacklistRaw = "";
    private static string _playerAlignedKillerBlacklistRaw = "";
    private static string _monsterInstantLootDropBlacklistRaw = "";
    private static HashSet<string> _dropInStackBlacklist = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _trophyLevelMultiplierBlacklist = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _playerAlignedKillerBlacklist = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _monsterInstantLootDropBlacklist = new(StringComparer.OrdinalIgnoreCase);
    private static bool? _isCreatureLevelControlLoaded;

    private static ConfigEntry<DropNSpawnPlugin.Toggle> _monsterInstantLootDrop = null!;
    private static ConfigEntry<string> _monsterInstantLootDropBlacklistEntry = null!;
    private static ConfigEntry<CharacterLootSystem> _characterLootSystem = null!;
    private static ConfigEntry<ChanceOnlyDropChanceScaling> _chanceOnlyDropChanceScaling = null!;
    private static ConfigEntry<int> _chanceOnlyDropChanceIncreasePerStar = null!;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _disableCharacterLootScalingWhenCreatureLevelControlLoaded = null!;
    private static ConfigEntry<int> _additionalLootChancePerStarCreature = null!;
    private static ConfigEntry<int> _additionalLootChancePerStarBoss = null!;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _globalDropInStack = null!;
    private static ConfigEntry<string> _dropInStackBlacklistEntry = null!;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _globalTrophyLevelMultiplier = null!;
    private static ConfigEntry<string> _trophyLevelMultiplierBlacklistEntry = null!;
    private static ConfigEntry<float> _onePerPlayerNearbyRange = null!;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _requirePlayerAlignedKiller = null!;
    private static ConfigEntry<string> _playerAlignedKillerBlacklistEntry = null!;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        _requirePlayerAlignedKiller = plugin.BindConfigEntry(
            "2 - Character",
            "require player-aligned killer for character drops",
            DropNSpawnPlugin.Toggle.Off,
            "If on, a non-player CharacterDrop requires a Player, a tamed character, or a character in the Players or PlayerSpawned faction as its lethal source. A lethal poison, fire, or spirit tick also qualifies if its active damage pool contains a confirmed player-aligned contribution, even mixed with hostile or unknown damage. Unrelated active effects do not qualify direct or environmental deaths; unknown-only damage is denied. Applies to vanilla and YAML-driven CharacterDrop independently of Enable Character Overrides. Prefabs in player-aligned killer character drop blacklist are exempt.",
            synchronizedSetting: true,
            configManagerOrder: 600);
        _requirePlayerAlignedKiller.SettingChanged += (_, _) =>
            CharacterDropKillerFilter.NotifyConfigurationChanged();
        _playerAlignedKillerBlacklistEntry = plugin.BindConfigEntry(
            "2 - Character",
            "player-aligned killer character drop blacklist",
            "",
            "Comma, semicolon, or newline separated victim creature prefab names, not dropped item names. Listed creatures are exempt from the player-aligned killer requirement for both original and YAML CharacterDrop, regardless of Enable Character Overrides. YAML drop-row fields do not override this exception. Other loot rules still apply. Case-insensitive; empty means no exceptions. Example: Blob,Deathsquito",
            synchronizedSetting: true,
            configManagerOrder: 590);
        _onePerPlayerNearbyRange = plugin.BindConfigEntry(
            "2 - Character",
            "OnePerPlayer drop check range",
            DefaultOnePerPlayerNearbyRange,
            new ConfigDescription(
                "If 0, disables the nearby-player override and uses vanilla server-wide online player count for character-drop onePerPlayer. If greater than 0, counts only living players within this many horizontal XZ meters of the dropping character.",
                new AcceptableValueRange<float>(0f, 100f)),
            synchronizedSetting: true,
            configManagerOrder: 500);
        _monsterInstantLootDrop = plugin.BindConfigEntry(
            "2 - Character",
            "monster instant loot drop",
            DropNSpawnPlugin.Toggle.Off,
            "If on, monster ragdoll loot saved from CharacterDrop is spawned immediately while the ragdoll remains for its vanilla lifetime. The saved ragdoll loot list is consumed so vanilla ragdoll cleanup does not drop the same items again. EpicLoot additional rewards keep normal ragdoll timing to avoid repeated rolls; if its compatibility guard is unavailable, all loot keeps normal timing. Creature prefabs in monster instant loot drop blacklist keep their normal ragdoll loot timing.",
            synchronizedSetting: true,
            configManagerOrder: 450);
        _monsterInstantLootDropBlacklistEntry = plugin.BindConfigEntry(
            "2 - Character",
            "monster instant loot drop blacklist",
            DefaultMonsterInstantLootDropBlacklist,
            "Comma, semicolon, or newline separated original creature prefab names, not ragdoll or dropped item names. Listed creatures keep normal ragdoll loot timing for both original and YAML CharacterDrop, regardless of Enable Character Overrides. YAML drop-row fields do not override this exception. Drop amounts are unchanged. Case-insensitive; empty means no exceptions. Changes affect newly created ragdolls. Example: Dragon,Hatchling",
            synchronizedSetting: true,
            configManagerOrder: 445);
        _characterLootSystem = plugin.BindConfigEntry(
            "2 - Character",
            "character loot system",
            CharacterLootSystem.CalculateChance,
            "Vanilla leaves ordinary character-drop level scaling to the game and YAML levelMultiplier values. CalculateChance replaces vanilla exponential level scaling with configured per-star amount scaling only for non-trophy item drops whose levelMultiplier is true. Drops with both levelMultiplier and levelMultiplierNotAmount true use chance-only drop chance scaling independently; all rows sharing that drop prefab are excluded from DNS amount scaling. The global trophy level multiplier setting separately controls other trophies, except YAML rows with explicit levelMultiplier: false.",
            synchronizedSetting: true,
            configManagerOrder: 440);
        _disableCharacterLootScalingWhenCreatureLevelControlLoaded = plugin.BindConfigEntry(
            "2 - Character",
            "disable DNS character loot scaling when CLLC is loaded",
            DropNSpawnPlugin.Toggle.On,
            "If on and Creature Level & Loot Control is loaded, DropNSpawn does not rewrite this config file but treats character loot system, both per-star loot chance options, global trophy level multiplier, and chance-only drop chance scaling as inactive at runtime. CLLC can then own loot scaling while Character YAML overrides, drop-in-stack, instant loot, and OnePerPlayer range still work. Turn this off only when CLLC loot is Vanilla and DropNSpawn should own loot scaling.",
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
        _chanceOnlyDropChanceScaling = plugin.BindConfigEntry(
            "2 - Character",
            "chance-only drop chance scaling",
            ChanceOnlyDropChanceScaling.Linear,
            "Applies only to character drops with both levelMultiplier and levelMultiplierNotAmount true, including original game drops and YAML overrides, regardless of item type or Enable Character Overrides. Linear uses chance-only drop chance increase per star (%), default 50: a 10% base becomes 10%, 15%, 20%, 25%, up to 100%. Vanilla keeps the game's exponential chance scaling: 10%, 20%, 40%, 80%. Does not change amounts or use the additional-loot amount percentages. Independent of character loot system and global trophy level multiplier; inactive when the CLLC loot-scaling handoff is enabled and CLLC is loaded.",
            synchronizedSetting: true,
            configManagerOrder: 410);
        _chanceOnlyDropChanceIncreasePerStar = plugin.BindConfigEntry(
            "2 - Character",
            "chance-only drop chance increase per star (%)",
            50,
            new ConfigDescription(
                "Only used when chance-only drop chance scaling is Linear. Each star adds this percentage of the base drop chance, not percentage points. Formula: base chance * (1 + stars * value / 100), capped at 100% final chance. A 10% base with 50 gives 10%, 15%, 20%; with 25 gives 10%, 12.5%, 15%. Zero keeps the base chance at every star level. Applies equally to creatures and bosses, changes no quantities, and does not use the additional-loot amount settings. Changes affect subsequent drop rolls. Ignored in Vanilla mode or while the CLLC loot-scaling handoff is active.",
                new AcceptableValueRange<int>(0, 100)),
            synchronizedSetting: true,
            configManagerOrder: 405);
        _globalDropInStack = plugin.BindConfigEntry(
            "2 - Character",
            "global drop in stack",
            DropNSpawnPlugin.Toggle.Off,
            "Default for character drops with no YAML dropInStack value, including original game drops. Omitted/null inherits this setting; explicit true enables stacks and explicit false keeps separate drops. Items listed in global drop in stack blacklist always stay separate, even with YAML true. Non-stackable items are unchanged. Requires Enable Character Overrides. Saved ragdolls retain the YAML choice from their drop roll; inherited defaults and the blacklist are read when loot spawns.",
            synchronizedSetting: true,
            configManagerOrder: 400);
        _dropInStackBlacklistEntry = plugin.BindConfigEntry(
            "2 - Character",
            "global drop in stack blacklist",
            "",
            "Comma, semicolon, or newline separated dropped item prefab names, not creature names. Listed items stay separate across creatures' original and YAML CharacterDrop, including saved ragdoll loot. This overrides both the global stack default and explicit YAML dropInStack: true. Requires Enable Character Overrides. Read when loot spawns. Case-insensitive; empty means no exceptions. Example: Coins,TrophyDeer",
            synchronizedSetting: true,
            configManagerOrder: 300);
        _globalTrophyLevelMultiplier = plugin.BindConfigEntry(
            "2 - Character",
            "global trophy level multiplier",
            DropNSpawnPlugin.Toggle.Off,
            "If on, eligible successful character trophy drops use their base drop chance and scale their final amount by the configured creature or boss additional-loot chance per star instead of vanilla exponential scaling. Explicit YAML levelMultiplier: false opts that row out; omitted/null and true permit this global policy. Original game rows remain eligible even when their native levelMultiplier is false. Blacklisted trophies skip this amount policy; existing level flags and the separate chance-only drop chance scaling setting still apply. A drop with both levelMultiplier and levelMultiplierNotAmount true also exempts all rows sharing that trophy prefab.",
            synchronizedSetting: true,
            configManagerOrder: 200);
        _trophyLevelMultiplierBlacklistEntry = plugin.BindConfigEntry(
            "2 - Character",
            "global trophy level multiplier blacklist",
            "",
            "Comma, semicolon, or newline separated trophy item prefab names, not creature names. Excludes these trophies from DNS global trophy amount scaling across original and YAML CharacterDrop. Unlike YAML levelMultiplier: false, this does not disable the existing level flags or chance-only drop chance scaling. Explicit YAML levelMultiplier: true does not override this exclusion. Case-insensitive; empty means no exceptions. Changes affect subsequent drop rolls. Example: TrophyEikthyr,TrophyDragonQueen",
            synchronizedSetting: true,
            configManagerOrder: 100);
    }

    internal static bool IsGlobalDropInStackEnabled()
    {
        return _globalDropInStack?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static bool IsPlayerAlignedKillerRequired()
    {
        return _requirePlayerAlignedKiller?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static bool IsPlayerAlignedKillerRequirementBlacklisted(string? prefabName)
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

        lock (PlayerAlignedKillerBlacklistLock)
        {
            EnsurePlayerAlignedKillerBlacklistCache();
            return _playerAlignedKillerBlacklist.Contains(normalizedPrefabName);
        }
    }

    internal static bool IsMonsterInstantLootDropEnabled()
    {
        return _monsterInstantLootDrop?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static bool IsMonsterInstantLootDropBlacklisted(string? prefabName)
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

        lock (MonsterInstantLootDropBlacklistLock)
        {
            string raw = _monsterInstantLootDropBlacklistEntry?.Value ?? DefaultMonsterInstantLootDropBlacklist;
            if (!string.Equals(_monsterInstantLootDropBlacklistRaw, raw, StringComparison.Ordinal))
            {
                _monsterInstantLootDropBlacklist = ParseNameSet(raw);
                _monsterInstantLootDropBlacklistRaw = raw;
            }

            return _monsterInstantLootDropBlacklist.Contains(normalizedPrefabName);
        }
    }

    internal static bool IsCalculateChanceLootSystemEnabled()
    {
        return _characterLootSystem?.Value == CharacterLootSystem.CalculateChance;
    }

    internal static bool IsLinearChanceOnlyDropScalingEnabled()
    {
        return _chanceOnlyDropChanceScaling?.Value == ChanceOnlyDropChanceScaling.Linear &&
               !ShouldDisableLootScalingForCreatureLevelControl();
    }

    internal static int GetChanceOnlyDropChanceIncreasePerStar() =>
        Mathf.Clamp(_chanceOnlyDropChanceIncreasePerStar?.Value ?? 50, 0, 100);

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
        return Mathf.Max(0f, _onePerPlayerNearbyRange?.Value ?? DefaultOnePerPlayerNearbyRange);
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

    private static void EnsurePlayerAlignedKillerBlacklistCache()
    {
        string raw = _playerAlignedKillerBlacklistEntry?.Value ?? "";
        if (string.Equals(_playerAlignedKillerBlacklistRaw, raw, StringComparison.Ordinal))
        {
            return;
        }

        _playerAlignedKillerBlacklistRaw = raw;
        _playerAlignedKillerBlacklist = ParseNameSet(raw);
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
