using System;
using System.Collections.Generic;
using System.Linq;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private static int _invalidEntryWarningSuppressionDepth;

    private readonly struct InvalidEntryWarningSuppressionScope : IDisposable
    {
        private readonly bool _active;

        public InvalidEntryWarningSuppressionScope(bool active)
        {
            _active = active;
            if (_active)
            {
                _invalidEntryWarningSuppressionDepth++;
            }
        }

        public void Dispose()
        {
            if (_active)
            {
                _invalidEntryWarningSuppressionDepth--;
            }
        }
    }

    private static void WarnMissingVegvisirPath(string prefabName, string path)
    {
        string warningKey = $"{prefabName}|missing-path|{path}";
        if (!VegvisirWarningLogs.Add(warningKey))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Location prefab '{prefabName}' has no Vegvisir at path '{path}'. Use {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml to copy an exact vegvisirs.path.");
    }

    private static void WarnMissingRunestonePath(string prefabName, string path)
    {
        string warningKey = $"{prefabName}|missing-runestone-path|{path}";
        if (!RunestoneWarningLogs.Add(warningKey))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Location prefab '{prefabName}' has no RuneStone at path '{path}'. Use {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml to copy an exact runestones.path.");
    }

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

    private static void WarnUnresolvedVegvisirTarget(string prefabName, List<string>? expectedLocations, IEnumerable<string> availablePaths)
    {
        IEnumerable<string> expectedValues = expectedLocations != null ? expectedLocations : Array.Empty<string>();
        string expectedSignature = JoinDiagnosticValues(expectedValues);
        string availableSignature = JoinDiagnosticValues(availablePaths);
        string warningKey = $"{prefabName}|unresolved-target|{expectedSignature}|{availableSignature}";
        if (!VegvisirWarningLogs.Add(warningKey))
        {
            return;
        }

        if (expectedLocations != null && expectedLocations.Count > 0)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                $"Location prefab '{prefabName}' has no Vegvisir matching expectedLocations [{expectedSignature}]. Available paths: [{availableSignature}]. Add an exact path or adjust expectedLocations. The override is skipped.");
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Location prefab '{prefabName}' could not resolve a Vegvisir target without path. Available paths: [{availableSignature}]. Add an exact path or set expectedLocations so exactly one Vegvisir matches. The override is skipped.");
    }

    private static void WarnAmbiguousVegvisirTarget(string prefabName, List<string>? expectedLocations, IEnumerable<string> candidatePaths)
    {
        IEnumerable<string> expectedValues = expectedLocations != null ? expectedLocations : Array.Empty<string>();
        string expectedSignature = JoinDiagnosticValues(expectedValues);
        string candidateSignature = JoinDiagnosticValues(candidatePaths);
        string warningKey = $"{prefabName}|ambiguous-target|{expectedSignature}|{candidateSignature}";
        if (!VegvisirWarningLogs.Add(warningKey))
        {
            return;
        }

        if (expectedLocations != null && expectedLocations.Count > 0)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                $"Location prefab '{prefabName}' has multiple Vegvisirs matching expectedLocations [{expectedSignature}] at paths [{candidateSignature}]. Add an exact path. The override is skipped.");
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Location prefab '{prefabName}' has multiple Vegvisirs at paths [{candidateSignature}]. Add an exact path or set expectedLocations so exactly one Vegvisir matches. The override is skipped.");
    }

    private static void WarnUnexpectedVegvisirTargets(string prefabName, string path, Vegvisir vegvisir, List<string>? expectedLocations)
    {
        string actualSignature = JoinDiagnosticValues(vegvisir.m_locations.Select(location => location.m_locationName));
        IEnumerable<string> expectedValues = expectedLocations != null ? expectedLocations : Array.Empty<string>();
        string expectedSignature = JoinDiagnosticValues(expectedValues);
        string warningKey = $"{prefabName}|target-mismatch|{path}|{actualSignature}|{expectedSignature}";
        if (!VegvisirWarningLogs.Add(warningKey))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Location prefab '{prefabName}' Vegvisir '{path}' does not match expectedLocations. Expected [{expectedSignature}] but found [{actualSignature}]. The override is skipped.");
    }

    private static void WarnUnresolvedRunestoneTarget(string prefabName, LocationRunestoneDefinition entry, IEnumerable<string> availablePaths)
    {
        string expectedSignature = DescribeExpectedRunestone(entry);
        string availableSignature = JoinDiagnosticValues(availablePaths);
        string warningKey = $"{prefabName}|unresolved-runestone-target|{expectedSignature}|{availableSignature}";
        if (!RunestoneWarningLogs.Add(warningKey))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Location prefab '{prefabName}' could not resolve a RuneStone target without path. Expected {expectedSignature}. Available paths: [{availableSignature}]. Add an exact path or adjust expectedLocationName/expectedLabel/expectedTopic so exactly one RuneStone matches. The override is skipped.");
    }

    private static void WarnAmbiguousRunestoneTarget(string prefabName, LocationRunestoneDefinition entry, IEnumerable<string> candidatePaths)
    {
        string expectedSignature = DescribeExpectedRunestone(entry);
        string candidateSignature = JoinDiagnosticValues(candidatePaths);
        string warningKey = $"{prefabName}|ambiguous-runestone-target|{expectedSignature}|{candidateSignature}";
        if (!RunestoneWarningLogs.Add(warningKey))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Location prefab '{prefabName}' has multiple RuneStones matching {expectedSignature} at paths [{candidateSignature}]. Add an exact path. The override is skipped.");
    }

    private static void WarnUnexpectedRunestoneTarget(string prefabName, string path, RuneStone runestone, LocationRunestoneDefinition entry)
    {
        string expectedSignature = DescribeExpectedRunestone(entry);
        string actualSignature = DescribeRunestone(runestone);
        string warningKey = $"{prefabName}|runestone-target-mismatch|{path}|{expectedSignature}|{actualSignature}";
        if (!RunestoneWarningLogs.Add(warningKey))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Location prefab '{prefabName}' RuneStone '{path}' does not match expected values. Expected {expectedSignature} but found {actualSignature}. The override is skipped.");
    }

    private static string DescribeExpectedRunestone(LocationRunestoneDefinition entry)
    {
        List<string> values = new();
        if (!string.IsNullOrWhiteSpace(entry.ExpectedLocationName))
        {
            values.Add($"locationName='{entry.ExpectedLocationName!.Trim()}'");
        }

        if (!string.IsNullOrWhiteSpace(entry.ExpectedLabel))
        {
            values.Add($"label='{entry.ExpectedLabel!.Trim()}'");
        }

        if (!string.IsNullOrWhiteSpace(entry.ExpectedTopic))
        {
            values.Add($"topic='{entry.ExpectedTopic!.Trim()}'");
        }

        return values.Count == 0 ? "(any RuneStone)" : string.Join(", ", values);
    }

    private static string DescribeRunestone(RuneStone runestone)
    {
        return $"locationName='{runestone.m_locationName}', label='{runestone.m_label}', topic='{runestone.m_topic}'";
    }

    private static void WarnInvalidEntry(string message)
    {
        if (!DropNSpawnPlugin.IsSourceOfTruth ||
            _invalidEntryWarningSuppressionDepth > 0 ||
            ShouldSuppressServerSourcedInvalidEntryWarning(message))
        {
            return;
        }

        if (InvalidEntryWarnings.Add(message))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(message);
        }
    }

    private static InvalidEntryWarningSuppressionScope BeginInvalidEntryWarningSuppressionForSyncedClientBuild(string sourceName)
    {
        return !DropNSpawnPlugin.IsSourceOfTruth && sourceName.StartsWith("ServerSync:", StringComparison.Ordinal)
            ? new InvalidEntryWarningSuppressionScope(active: true)
            : default;
    }

    private static bool ShouldSuppressServerSourcedInvalidEntryWarning(string message)
    {
        return !DropNSpawnPlugin.IsSourceOfTruth &&
               message.IndexOf("ServerSync:", StringComparison.Ordinal) >= 0;
    }

    private static string DescribeEntrySource(string? sourcePath)
    {
        return string.IsNullOrWhiteSpace(sourcePath) ? "unknown source" : sourcePath!;
    }
}
