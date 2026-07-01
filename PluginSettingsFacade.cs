using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DropNSpawn;

internal static class PluginSettingsFacade
{
    internal static bool IsObjectDomainEnabled() =>
        PluginBoundSettings.EnableObjectOverrides?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsCharacterDomainEnabled() =>
        PluginBoundSettings.EnableCharacterOverrides?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsSpawnerDomainEnabled() =>
        PluginBoundSettings.EnableSpawnerOverrides?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsSpawnSystemDomainEnabled() =>
        PluginBoundSettings.EnableSpawnSystemOverrides?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsEventDomainEnabled() =>
        PluginBoundSettings.EnableEventOverrides?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool ShouldDisableLowTierBiomeGlobalKeySpawnSystemEntries() =>
        PluginBoundSettings.DisableGlobalKeySpawnSystemEntriesInLowTierBiomes?.Value == DropNSpawnPlugin.Toggle.On;

    internal static bool IsMultipleEventsEnabled() =>
        EventGlobalConfig.IsMultipleEventsEnabled();

    internal static bool IsEventCheckPerPlayerEnabled() =>
        EventGlobalConfig.IsCheckPerPlayerEnabled();

    internal static EventGlobalConfig.EventPlayerBaseDefault GetDefaultEventPlayerBase() =>
        EventGlobalConfig.GetDefaultPlayerBase();

    internal static float GetMinimumDistanceBetweenEvents() =>
        EventGlobalConfig.GetMinimumDistanceBetweenEvents();

    internal static float GetRandomEventChance() =>
        EventGlobalConfig.GetRandomEventChance();

    internal static float GetRandomEventIntervalMinutes() =>
        EventGlobalConfig.GetRandomEventIntervalMinutes();

    internal static bool IsGlobalCharacterDropInStackEnabled() =>
        CharacterDropGlobalConfig.IsGlobalDropInStackEnabled();

    internal static bool IsMonsterInstantLootDropEnabled() =>
        CharacterDropGlobalConfig.IsMonsterInstantLootDropEnabled();

    internal static bool IsCharacterDropCalculateChanceLootSystemEnabled() =>
        CharacterDropGlobalConfig.IsCalculateChanceLootSystemEnabled();

    internal static bool ShouldDisableCharacterLootScalingForCreatureLevelControl() =>
        CharacterDropGlobalConfig.ShouldDisableLootScalingForCreatureLevelControl();

    internal static int GetCharacterDropAdditionalLootChancePerStar(bool boss) =>
        CharacterDropGlobalConfig.GetAdditionalLootChancePerStar(boss);

    internal static bool IsGlobalCharacterDropTrophyLevelMultiplierEnabled() =>
        CharacterDropGlobalConfig.IsGlobalTrophyLevelMultiplierEnabled();

    internal static bool IsCharacterDropTrophyLevelMultiplierBlacklisted(string? prefabName) =>
        CharacterDropGlobalConfig.IsTrophyLevelMultiplierBlacklisted(prefabName);

    internal static int GetDefaultSpawnAreaMaxTotalSpawns() =>
        SpawnerGlobalConfig.GetDefaultSpawnAreaMaxTotalSpawns();

    internal static int GetDefaultZeroCreatureSpawnerRespawnTimeMinutes() =>
        SpawnerGlobalConfig.GetDefaultZeroCreatureSpawnerRespawnTimeMinutes();

    internal static float GetCharacterDropOnePerPlayerNearbyRange() =>
        CharacterDropGlobalConfig.GetOnePerPlayerNearbyRange();

    internal static bool IsCharacterDropOnePerPlayerNearbyRangeLivingPlayersOnly() =>
        CharacterDropGlobalConfig.IsOnePerPlayerNearbyRangeLivingPlayersOnly();

    internal static bool IsCharacterDropInStackBlacklisted(string? prefabName) =>
        CharacterDropGlobalConfig.IsDropInStackBlacklisted(prefabName);

    internal static bool IsEligibleOverrideConfigurationPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath = Path.GetFullPath(path);
        string configRoot = EnsureTrailingSeparator(Path.GetFullPath(DropNSpawnPlugin.YamlConfigDirectoryPath));
        return fullPath.StartsWith(configRoot, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetYamlDomainFilePrefix(string domain)
    {
        return $"{DropNSpawnPlugin.YamlFilePrefix}_{domain}";
    }

    internal static string GetYamlDomainSupplementalPrefix(string domain)
    {
        return $"{GetYamlDomainFilePrefix(domain)}_";
    }

    internal static IEnumerable<string> EnumerateSupplementalOverrideConfigurationPaths(
        string searchPattern,
        Func<string, bool> isOverrideFileName)
    {
        if (!Directory.Exists(DropNSpawnPlugin.YamlConfigDirectoryPath))
        {
            yield break;
        }

        IEnumerable<string> overrideFiles = Directory
            .EnumerateFiles(DropNSpawnPlugin.YamlConfigDirectoryPath, searchPattern, SearchOption.AllDirectories)
            .Where(path => IsEligibleOverrideConfigurationPath(path) && isOverrideFileName(Path.GetFileName(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (string path in overrideFiles)
        {
            yield return path;
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }
}
