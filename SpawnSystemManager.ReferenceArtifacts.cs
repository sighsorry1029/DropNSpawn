using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SpawnSystemConfigurationEntry = DropNSpawn.CanonicalSpawnSystemEntry;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private static ReferenceCatalogSnapshot BuildCurrentReferenceCatalogSnapshot()
    {
        SpawnSystemSnapshot? templateSnapshot = GetCachedTemplateSnapshot();
        if (templateSnapshot != null && templateSnapshot.Entries.Count > 0)
        {
            return BuildReferenceCatalogSnapshotFromTemplateSnapshot(templateSnapshot);
        }

        return BuildReferenceCatalogSnapshot(GetLiveSystems());
    }

    private static ReferenceCatalogSnapshot BuildReferenceCatalogSnapshotFromTemplateSnapshot(SpawnSystemSnapshot snapshot)
    {
        ReferenceCatalogSnapshot referenceCatalogSnapshot = new();
        List<SpawnSystemConfigurationEntry> mergedEntries = MergeUniqueReferenceEntriesWithExternalProjections(
            BuildTemplateReferenceEntries(snapshot),
            forceRefresh: true);
        referenceCatalogSnapshot.LiveEntries.AddRange(mergedEntries);
        referenceCatalogSnapshot.LiveEntries.Sort(CompareReferenceEntriesForOutput);

        string renderedContent = SerializeReferenceEntries(referenceCatalogSnapshot.LiveEntries);
        referenceCatalogSnapshot.SourceSignature = ReferenceRefreshSupport.ComputeStableHash(renderedContent);
        return referenceCatalogSnapshot;
    }

    private static string BuildReferenceConfigurationTemplate(
        ReferenceCatalogSnapshot? referenceCatalogSnapshot = null)
    {
        referenceCatalogSnapshot ??= BuildReferenceCatalogSnapshot();
        return SerializeReferenceEntries(referenceCatalogSnapshot.LiveEntries);
    }

    private static string SerializeReferenceEntries(IEnumerable<SpawnSystemConfigurationEntry> entries)
    {
        StringBuilder builder = new();
        bool wroteAny = false;
        foreach (PrefabOwnerSection<SpawnSystemConfigurationEntry> section in BuildBiomeOrderedReferenceSections(entries ?? Enumerable.Empty<SpawnSystemConfigurationEntry>()))
        {
            if (section.Entries.Count == 0)
            {
                continue;
            }

            if (wroteAny)
            {
                builder.AppendLine();
            }

            PrefabOutputSections.AppendSectionHeaderComment(builder, section.OwnerName);
            foreach (SpawnSystemConfigurationEntry entry in section.Entries)
            {
                AppendReferenceEntry(builder, entry);
                wroteAny = true;
            }
        }

        return wroteAny ? builder.ToString() : "[]" + Environment.NewLine;
    }

    private static void WriteReferenceConfigurationFile(string content, string logMessage)
    {
        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        if (GeneratedFileWriter.WriteAllTextIfChanged(ReferenceConfigurationPath, content))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo(logMessage);
        }
    }

    private static CreatureManagerSpawnReferenceSupport.ReferenceSnapshot? TryGetExternalReferenceProjectionSnapshot(bool forceRefresh)
    {
        try
        {
            return CreatureManagerSpawnReferenceSupport.GetReferenceSnapshot(forceRefresh);
        }
        catch (Exception ex)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Failed to collect external spawnsystem reference projections. {ex}");
            return null;
        }
    }

    private static List<SpawnSystemConfigurationEntry> GetExternalReferenceProjectionEntries(bool forceRefresh)
    {
        CreatureManagerSpawnReferenceSupport.ReferenceSnapshot? snapshot =
            TryGetExternalReferenceProjectionSnapshot(forceRefresh);
        return snapshot?.Projections
            .Select(projection => projection.Entry)
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Prefab))
            .ToList() ?? new List<SpawnSystemConfigurationEntry>();
    }

    private static List<SpawnSystemConfigurationEntry> MergeUniqueReferenceEntriesWithExternalProjections(
        IEnumerable<SpawnSystemConfigurationEntry> entries,
        bool forceRefresh)
    {
        List<SpawnSystemConfigurationEntry> mergedEntries = entries?.ToList() ?? new List<SpawnSystemConfigurationEntry>();
        HashSet<string> stableKeys = new(
            mergedEntries.Select(GetStableReferenceSortKey),
            StringComparer.Ordinal);

        foreach (SpawnSystemConfigurationEntry externalEntry in GetExternalReferenceProjectionEntries(forceRefresh))
        {
            if (!stableKeys.Add(GetStableReferenceSortKey(externalEntry)))
            {
                continue;
            }

            mergedEntries.Add(externalEntry);
        }

        return mergedEntries;
    }

    private static List<SpawnSystemConfigurationEntry> MergeScaffoldEntriesWithExternalProjections(
        IEnumerable<SpawnSystemConfigurationEntry> entries,
        bool forceRefresh)
    {
        List<SpawnSystemConfigurationEntry> mergedEntries = entries?.ToList() ?? new List<SpawnSystemConfigurationEntry>();
        Dictionary<string, int> nativeCoverage = new(StringComparer.Ordinal);
        foreach (SpawnSystemConfigurationEntry entry in mergedEntries)
        {
            string stableKey = GetStableReferenceSortKey(entry);
            nativeCoverage[stableKey] = nativeCoverage.TryGetValue(stableKey, out int count) ? count + 1 : 1;
        }

        foreach (SpawnSystemConfigurationEntry externalEntry in GetExternalReferenceProjectionEntries(forceRefresh))
        {
            string stableKey = GetStableReferenceSortKey(externalEntry);
            if (nativeCoverage.TryGetValue(stableKey, out int coveredCount) && coveredCount > 0)
            {
                nativeCoverage[stableKey] = coveredCount - 1;
                continue;
            }

            mergedEntries.Add(externalEntry);
        }

        return mergedEntries;
    }

    private static List<SpawnSystemConfigurationEntry> BuildTemplateReferenceEntries(SpawnSystemSnapshot snapshot)
    {
        List<SpawnSystemConfigurationEntry> entries = new();
        foreach (PrefabOwnerSection<SpawnSystemEntrySnapshot> section in BuildBiomeOrderedSnapshotSections(snapshot))
        {
            foreach (SpawnSystemEntrySnapshot entrySnapshot in section.Entries)
            {
                SpawnSystemConfigurationEntry entry = ConvertToReferenceEntry(entrySnapshot);
                entry.ReferenceOwnerName = section.OwnerName;
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static List<SpawnSystemConfigurationEntry> BuildTemplateFullScaffoldEntries(SpawnSystemSnapshot snapshot)
    {
        List<SpawnSystemConfigurationEntry> entries = new();
        foreach (PrefabOwnerSection<SpawnSystemEntrySnapshot> section in BuildBiomeOrderedSnapshotSections(snapshot))
        {
            foreach (SpawnSystemEntrySnapshot entrySnapshot in section.Entries)
            {
                SpawnSystemConfigurationEntry entry = ConvertToConfigurationEntry(entrySnapshot);
                entry.ReferenceOwnerName = section.OwnerName;
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static ReferenceCatalogSnapshot BuildReferenceCatalogSnapshot(List<SpawnSystem>? systems = null)
    {
        ReferenceCatalogSnapshot snapshot = new();
        HashSet<string> liveKeys = new(StringComparer.Ordinal);
        foreach (SpawnSystem.SpawnData spawnData in EnumerateReferenceLiveSpawnData(systems))
        {
            SpawnSystemConfigurationEntry entry = ConvertToReferenceEntry(spawnData);
            string stableKey = GetStableReferenceSortKey(entry);
            if (liveKeys.Add(stableKey))
            {
                snapshot.LiveEntries.Add(entry);
            }
        }

        List<SpawnSystemConfigurationEntry> mergedEntries = MergeUniqueReferenceEntriesWithExternalProjections(
            snapshot.LiveEntries,
            forceRefresh: true);
        snapshot.LiveEntries.Clear();
        snapshot.LiveEntries.AddRange(mergedEntries);
        snapshot.LiveEntries.Sort(CompareReferenceEntriesForOutput);

        string renderedContent = SerializeReferenceEntries(snapshot.LiveEntries);
        snapshot.SourceSignature = ReferenceRefreshSupport.ComputeStableHash(renderedContent);
        return snapshot;
    }

    private static IEnumerable<SpawnSystem.SpawnData> EnumerateReferenceLiveSpawnData(List<SpawnSystem>? systems = null)
    {
        CompiledSpawnSystemTable? selectedTable = GetSelectedCompiledTableForCurrentState();
        if (TryEnumerateReferenceLiveSpawnData(selectedTable?.Lists, out IEnumerable<SpawnSystem.SpawnData>? compiledEntries))
        {
            return compiledEntries!;
        }

        SpawnSystemSnapshot? templateSnapshot = GetCachedTemplateSnapshot();
        if (templateSnapshot != null && templateSnapshot.Entries.Count > 0)
        {
            return templateSnapshot.Entries
                .Where(entry => entry?.Data != null)
                .Select(entry => entry.Data);
        }

        systems ??= GetLiveSystems();
        return systems
            .Where(current => current != null)
            .OrderBy(current => current.GetInstanceID())
            .SelectMany(system => system.m_spawnLists ?? new List<SpawnSystemList>())
            .Where(spawnList => spawnList != null)
            .SelectMany(spawnList => spawnList.m_spawners ?? new List<SpawnSystem.SpawnData>())
            .Where(spawnData => spawnData != null);
    }
}
