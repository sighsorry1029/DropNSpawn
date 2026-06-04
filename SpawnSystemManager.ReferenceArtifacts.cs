using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SpawnSystemConfigurationEntry = DropNSpawn.CanonicalSpawnSystemEntry;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private static ReferenceCatalogSnapshot BuildCurrentReferenceCatalogSnapshot()
    {
        return BuildReferenceCatalogSnapshot();
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
        GeneratedArtifactWriter.WriteText(ReferenceConfigurationPath, content, logMessage, logOnlyWhenChanged: true);
    }

    internal static bool TryWriteFullScaffoldConfigurationFile(out string path, out string error)
    {
        string content;
        string logMessage;
        lock (Sync)
        {
            path = FullScaffoldConfigurationPath;
            error = "";

            if (!TryCaptureSnapshotsIfNeeded())
            {
                error = "SpawnSystem game data is not ready yet.";
                return false;
            }

            content = BuildFullScaffoldConfigurationTemplate();
            logMessage = $"Wrote spawnsystem full scaffold configuration to {path}.";
        }

        GeneratedArtifactWriter.WriteTextAlways(path, content, logMessage);
        return true;
    }

    internal static bool TryWriteReferenceConfigurationFile(out string path, out string error)
    {
        string content;
        string sourceSignature;
        string logMessage;
        lock (Sync)
        {
            path = ReferenceConfigurationPath;
            error = "";

            if (ZNetScene.instance == null || ObjectDB.instance == null)
            {
                error = "SpawnSystem game data is not ready yet.";
                return false;
            }

            ReferenceCatalogSnapshot referenceCatalogSnapshot = BuildCurrentReferenceCatalogSnapshot();
            if (!referenceCatalogSnapshot.HasAnyEntries)
            {
                error = "SpawnSystem game data is not ready yet.";
                return false;
            }

            content = BuildReferenceConfigurationTemplate(referenceCatalogSnapshot);
            sourceSignature = referenceCatalogSnapshot.SourceSignature;
            logMessage = $"Updated spawnsystem reference configuration at {ReferenceConfigurationPath}.";
            path = ReferenceConfigurationPath;
        }

        WriteReferenceConfigurationFile(content, logMessage);
        ReferenceArtifactLifecycle.RecordUpdate(
            ReferenceAutoUpdateStateKey,
            ReferenceConfigurationPath,
            sourceSignature);
        return true;
    }

    internal static void RefreshReferenceConfigurationFile()
    {
        string content;
        string sourceSignature;
        string logMessage;
        lock (Sync)
        {
            if (ZNetScene.instance == null || ObjectDB.instance == null)
            {
                return;
            }

            ReferenceCatalogSnapshot referenceCatalogSnapshot = BuildCurrentReferenceCatalogSnapshot();
            if (!referenceCatalogSnapshot.HasAnyEntries)
            {
                return;
            }

            content = BuildReferenceConfigurationTemplate(referenceCatalogSnapshot);
            sourceSignature = referenceCatalogSnapshot.SourceSignature;
            logMessage = $"Updated spawnsystem reference configuration at {ReferenceConfigurationPath}.";
        }

        WriteReferenceConfigurationFile(content, logMessage);
        ReferenceArtifactLifecycle.RecordUpdate(
            ReferenceAutoUpdateStateKey,
            ReferenceConfigurationPath,
            sourceSignature);
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

    private static ReferenceCatalogSnapshot BuildReferenceCatalogSnapshot()
    {
        ReferenceCatalogSnapshot snapshot = new();
        HashSet<string> liveKeys = new(StringComparer.Ordinal);
        foreach (SpawnSystem.SpawnData spawnData in EnumerateUpstreamReferenceSpawnData())
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

    private static IEnumerable<SpawnSystem.SpawnData> EnumerateUpstreamReferenceSpawnData()
    {
        if (TryEnumerateReferenceSpawnData(GetReferenceSourceSpawnLists(), out IEnumerable<SpawnSystem.SpawnData>? sourceEntries))
        {
            return sourceEntries!;
        }

        return Enumerable.Empty<SpawnSystem.SpawnData>();
    }

    private static IEnumerable<SpawnSystemList> GetReferenceSourceSpawnLists()
    {
        SpawnSystem? zoneCtrlSpawnSystem = GetZoneCtrlPrefabSpawnSystem();
        if (zoneCtrlSpawnSystem?.m_spawnLists != null)
        {
            return zoneCtrlSpawnSystem.m_spawnLists
                .Where(spawnList => spawnList != null);
        }

        if (_vanillaCompiledTable != null &&
            _vanillaCompiledTable.ReferenceSourceTrusted &&
            _vanillaCompiledTable.Lists.Count > 0)
        {
            return _vanillaCompiledTable.Lists
                .Where(spawnList => spawnList != null);
        }

        return Enumerable.Empty<SpawnSystemList>();
    }
}
