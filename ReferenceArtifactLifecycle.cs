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
        out ReferenceArtifactUpdateKind updateKind)
    {
        updateKind = ReferenceArtifactUpdateKind.None;
        if (!File.Exists(referencePath))
        {
            updateKind = ReferenceArtifactUpdateKind.Created;
            return true;
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

    internal static string FormatAction(ReferenceArtifactUpdateKind updateKind)
    {
        return updateKind == ReferenceArtifactUpdateKind.Created ? "Created" : "Updated";
    }
}
