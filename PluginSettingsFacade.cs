using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace DropNSpawn;

internal static class PluginSettingsFacade
{
    internal static bool ShouldShowLocationProxyOfferingBowlHoverInfo() =>
        PluginBoundSettings.ShowLocationProxyOfferingBowlHoverInfo?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsPerPlayerBossStonesEnabled() =>
        PluginBoundSettings.PerPlayerBossStones?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsRemoteForsakenPowerSelectionEnabled() =>
        PluginBoundSettings.RemoteForsakenPowerSelection?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsBossTamedPressureEnabled() =>
        PluginBoundSettings.EnableBossTamedPressure?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool ShouldAutoUpdateReferenceFiles() =>
        PluginBoundSettings.ReferenceUpdateMode?.Value != DropNSpawnPlugin.ReferenceUpdateMode.ManualUpdate;

    internal static bool ShouldAutoCreateMissingReferenceFiles() => true;

    internal static bool IsObjectDomainEnabled() =>
        PluginBoundSettings.EnableObjectOverrides?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsCharacterDomainEnabled() =>
        PluginBoundSettings.EnableCharacterOverrides?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsSpawnerDomainEnabled() =>
        PluginBoundSettings.EnableSpawnerOverrides?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsLocationDomainEnabled() =>
        PluginBoundSettings.EnableLocationOverrides?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsSpawnSystemDomainEnabled() =>
        PluginBoundSettings.EnableSpawnSystemOverrides?.Value != DropNSpawnPlugin.Toggle.Off;

    internal static bool IsRunestoneGlobalPinsEnabled() =>
        LocationRunestoneGlobalPinsConfig.IsEnabled();

    internal static float GetAfternoonStartFraction() =>
        PluginBoundSettings.AfternoonStartFraction?.Value ?? 0.5f;

    internal static KeyboardShortcut GetRotateForsakenPowerShortcut() =>
        PluginBoundSettings.RotateForsakenPowerShortcut?.Value ?? default;

    internal static bool IsGlobalCharacterDropInStackEnabled() =>
        CharacterDropGlobalConfig.IsGlobalDropInStackEnabled();

    internal static float GetSameBossDuplicateBlockRadius() =>
        BossRulesConfig.GetSameBossDuplicateBlockRadius();

    internal static int GetDefaultSpawnAreaMaxTotalSpawns() =>
        SpawnerGlobalConfig.GetDefaultSpawnAreaMaxTotalSpawns();

    internal static float GetDefaultDespawnRange() =>
        DespawnRulesConfig.GetDefaultDespawnRange();

    internal static float GetDefaultDespawnDelaySeconds() =>
        DespawnRulesConfig.GetDefaultDespawnDelaySeconds();

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

    internal static bool IsSpawnSystemDiagnosticsEnabled()
    {
        return PluginBoundSettings.EnableSpawnSystemDiagnostics?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static bool IsOfferingBowlDiagnosticsEnabled()
    {
        return PluginBoundSettings.EnableOfferingBowlDiagnostics?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static bool IsBossStoneDiagnosticsEnabled()
    {
        return PluginBoundSettings.EnableBossStoneDiagnostics?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static bool IsDespawnDiagnosticsEnabled()
    {
        return PluginBoundSettings.EnableDespawnDiagnostics?.Value == DropNSpawnPlugin.Toggle.On;
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
