using System.IO;

namespace DropNSpawn;

internal enum ReferenceArtifactUpdateKind
{
    None,
    Created,
    Updated
}

internal static class ReferenceArtifactLifecycle
{
    internal static bool TryPlanUpdate(
        string stateKey,
        string referencePath,
        string sourceSignature,
        out ReferenceArtifactUpdateKind updateKind,
        string? requiredExistingHeader = null)
    {
        updateKind = ReferenceArtifactUpdateKind.None;
        if (!File.Exists(referencePath))
        {
            updateKind = ReferenceArtifactUpdateKind.Created;
            return true;
        }

        // A restricted automatic scan must not replace an older/full export.
        // Only files explicitly marked as partial can be refreshed as partial.
        if (requiredExistingHeader != null && !HasHeader(referencePath, requiredExistingHeader))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
                $"Preserved existing reference at {referencePath}: automatic MWL location scanning is restricted. Use dns:reference for an explicit full refresh.");
            return false;
        }

        if (ReferenceRefreshSupport.ShouldSkipAutoUpdate(
                stateKey,
                referencePath,
                sourceSignature,
                ReferenceRefreshSupport.CurrentReferenceLogicVersion))
        {
            return false;
        }

        updateKind = ReferenceArtifactUpdateKind.Updated;
        return true;
    }

    internal static void RecordUpdate(string stateKey, string referencePath, string sourceSignature)
    {
        ReferenceRefreshSupport.RecordAutoUpdateState(
            stateKey,
            referencePath,
            sourceSignature,
            logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
    }

    private static bool HasHeader(string path, string header)
    {
        using StreamReader reader = File.OpenText(path);
        return string.Equals(reader.ReadLine(), header, System.StringComparison.Ordinal);
    }

    internal static string FormatAction(ReferenceArtifactUpdateKind updateKind)
    {
        return updateKind == ReferenceArtifactUpdateKind.Created ? "Created" : "Updated";
    }
}
