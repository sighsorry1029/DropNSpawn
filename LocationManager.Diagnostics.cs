using System;
using System.Collections.Generic;
using System.Linq;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private static void WarnMissingItemStandPath(string prefabName, string path)
    {
        string warningKey = $"{prefabName}|missing-itemstand-path|{path}";
        if (!ItemStandDiagnosticLogs.Add(warningKey))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Location prefab '{prefabName}' has no ItemStand at path '{path}'. Use {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml to copy an exact itemStands.path.");
    }

    private static void WarnInvalidEntry(string message)
    {
        InvalidEntryWarnings.Warn(message, requireSourceOfTruth: true);
    }

    private static InvalidEntryDiagnostics.SuppressionScope BeginInvalidEntryWarningSuppressionForSyncedClientBuild(string sourceName)
    {
        return InvalidEntryWarnings.BeginSuppressionForSyncedClientBuild(sourceName);
    }

    private static string DescribeEntrySource(string? sourcePath)
    {
        return string.IsNullOrWhiteSpace(sourcePath) ? "unknown source" : sourcePath!;
    }
}
