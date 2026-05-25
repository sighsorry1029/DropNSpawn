using System.Collections.Generic;
using System.IO;
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

    private static void WriteReferenceConfigurationFile(string content, string logMessage)
    {
        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        GeneratedFileWriter.WriteAllTextIfChanged(ReferenceConfigurationPath, content);
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(logMessage);
    }
}
