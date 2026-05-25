using System;
using System.Collections.Generic;
using System.Text;
using SpawnSystemConfigurationEntry = DropNSpawn.CanonicalSpawnSystemEntry;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private static string BuildFullScaffoldConfigurationTemplate()
    {
        SpawnSystemSnapshot? snapshot = GetTemplateSnapshot();
        if (snapshot == null)
        {
            return "";
        }

        StringBuilder builder = new();
        bool wroteAny = false;

        List<SpawnSystemConfigurationEntry> entries = MergeScaffoldEntriesWithExternalProjections(
            BuildTemplateFullScaffoldEntries(snapshot),
            forceRefresh: true);
        foreach (PrefabOwnerSection<SpawnSystemConfigurationEntry> section in BuildBiomeOrderedReferenceSections(entries))
        {
            foreach (SpawnSystemConfigurationEntry entry in section.Entries)
            {
                if (wroteAny)
                {
                    builder.AppendLine();
                }

                AppendConfigurationEntry(builder, entry);
                wroteAny = true;
            }
        }

        return wroteAny ? builder.ToString() : "[]" + Environment.NewLine;
    }
}
