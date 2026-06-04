using System.Collections.Generic;
using System.Linq;

namespace DropNSpawn;

internal static partial class CharacterDropManager
{
    private static List<PrefabOwnerSection<CharacterDropPrefabEntry>> BuildConfigurationTemplate()
    {
        return PrefabOutputSections.BuildSections(
            CharacterDropRuntime.GetSnapshots().Select(BuildConfigurationEntry),
            entry => entry.Prefab);
    }

    private static string BuildReferenceConfigurationTemplate()
    {
        List<PrefabOwnerSection<CharacterDropReferenceEntry>> sections = BuildConfigurationTemplate()
            .Select(section => new PrefabOwnerSection<CharacterDropReferenceEntry>(
                section.OwnerName,
                section.Entries
                    .Select(entry => new CharacterDropReferenceEntry
                    {
                        Prefab = entry.Prefab,
                        CharacterDrop = entry.CharacterDrop
                    })
                    .ToList()))
            .ToList();

        return PrefabOutputSections.SerializeReferenceSections(sections, Serializer);
    }

    private static string SerializeReferenceEntries(IEnumerable<CharacterDropReferenceEntry> entries)
    {
        return ReferenceRefreshSupport.SerializeReferenceSections(entries, entry => entry.Prefab, Serializer);
    }

    internal static bool TryWriteFullScaffoldConfigurationFile(out string path, out string error)
    {
        string content;
        string logMessage;
        lock (Sync)
        {
            path = FullScaffoldConfigurationPath;
            error = "";

            if (!IsGameDataReady() && !CharacterDropRuntime.HasSnapshots())
            {
                error = "Character game data is not ready yet.";
                return false;
            }

            CaptureSnapshotsIfNeeded();
            content = BuildFullScaffoldConfigurationTemplate();
            logMessage = $"Wrote character full scaffold configuration to {path}.";
        }

        GeneratedArtifactWriter.WriteTextAlways(path, content, logMessage);
        return true;
    }

    internal static void RefreshReferenceConfigurationFile()
    {
        string content;
        string sourceSignature;
        string logMessage;
        lock (Sync)
        {
            if (!IsGameDataReady())
            {
                return;
            }

            CaptureSnapshotsIfNeeded();
            content = BuildReferenceConfigurationTemplate();
            sourceSignature = ComputeReferenceSourceSignature();
            logMessage = $"Updated character reference configuration at {ReferenceConfigurationPath}.";
        }

        WriteReferenceConfigurationFile(content, logMessage);
        ReferenceArtifactLifecycle.RecordUpdate(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, sourceSignature);
    }

    private static void WriteReferenceConfigurationFile(string content, string logMessage)
    {
        GeneratedArtifactWriter.WriteText(ReferenceConfigurationPath, content, logMessage);
    }
}
