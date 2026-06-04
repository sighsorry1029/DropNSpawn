using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using YamlDotNet.Core;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static void WarnMissingComponent(string key, string componentName)
    {
        if (MissingComponentWarnings.Add(key))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Prefab configuration references {componentName}, but the component was not found on '{key}'.");
        }
    }

    private static void WarnInvalidEntry(string message)
    {
        InvalidEntryWarnings.Warn(message);
    }

    private static InvalidEntryDiagnostics.SuppressionScope BeginInvalidEntryWarningSuppressionForSyncedClientBuild(string sourceName)
    {
        return InvalidEntryWarnings.BeginSuppressionForSyncedClientBuild(sourceName);
    }

    private static void LogPartiallyAcceptedLocalConfiguration(int parsedEntryCount, int acceptedEntryCount, List<string> warnings)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Loaded {acceptedEntryCount} object override entries from {parsedEntryCount} parsed entries. Invalid entries were skipped.");
        foreach (string warning in warnings
                     .Where(message => !string.IsNullOrWhiteSpace(message))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(warning);
        }
    }

    private static void LogPartiallyAcceptedLocalConfigurationHook(int parsedEntryCount, int acceptedEntryCount, IEnumerable<string> warnings)
    {
        LogPartiallyAcceptedLocalConfiguration(parsedEntryCount, acceptedEntryCount, warnings.ToList());
    }

    private static void LogLocalConfigurationLoaded(int acceptedEntryCount, int loadedFileCount)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Loaded {acceptedEntryCount} object override entries from {loadedFileCount} override file(s).");
    }

    private static void OnSourceOfTruthPayloadUnchanged()
    {
        if (!NetworkPayloadSyncSupport.IsPayloadCurrent(Descriptor, _configurationSignature))
        {
            ConfigurationDomainHost.PublishSyncedPayload(
                DropNSpawnPlugin.IsSourceOfTruth,
                Descriptor,
                _configuration,
                _configurationSignature);
        }
    }

    private static void LogSyncedObjectConfigurationLoaded(string payloadToken, int acceptedEntryCount)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Loaded {acceptedEntryCount} synchronized object configuration(s) from the server.");
    }

    private static void LogSyncedObjectConfigurationFailure(string payloadToken, Exception ex)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to deserialize synchronized object payload DTO. {ex}");
    }

    private static string CreateConfigurationContext(PrefabConfigurationEntry entry)
    {
        string prefabName = string.IsNullOrWhiteSpace(entry.Prefab) ? "<missing prefab>" : entry.Prefab;
        return $"{prefabName} @ {DescribeEntrySource(entry)}";
    }

    private static string DescribeEntrySource(PrefabConfigurationEntry entry)
    {
        string source = DescribeEntrySource(entry.SourcePath);
        return entry.SourceLine > 0
            ? $"{source}:{entry.SourceLine.ToString(CultureInfo.InvariantCulture)}"
            : source;
    }

    private static string DescribeEntrySource(string? sourcePath)
    {
        return string.IsNullOrWhiteSpace(sourcePath) ? "unknown source" : sourcePath!;
    }

    private static string FormatYamlExceptionLocation(Exception ex)
    {
        return ex is YamlException yamlException && yamlException.Start.Line > 0
            ? $":{yamlException.Start.Line.ToString(CultureInfo.InvariantCulture)}"
            : "";
    }

    private static string BuildCompiledObjectDropContext(PrefabConfigurationEntry entry, string componentName)
    {
        string prefabName = string.IsNullOrWhiteSpace(entry.Prefab) ? "<unknown prefab>" : entry.Prefab;
        string ruleId = string.IsNullOrWhiteSpace(entry.RuleId) ? "<unknown rule>" : entry.RuleId;
        return $"{prefabName}/{componentName}/{ruleId}@{DescribeEntrySource(entry)}";
    }
}
