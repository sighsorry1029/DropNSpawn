using System;
using System.Collections.Generic;
using System.Linq;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static List<PrefabOwnerSection<PrefabConfigurationEntry>> BuildConfigurationTemplate()
    {
        Dictionary<string, LocationReferenceBucket> locationBuckets = BuildLocationReferenceBuckets();
        List<PrefabOwnerSection<PrefabConfigurationEntry>> sections = PrefabOutputSections.BuildSections(
            Snapshots.Select(BuildConfigurationEntry),
            entry => entry.Prefab,
            entry => ResolveObjectOwnerName(entry.Prefab, locationBuckets));

        foreach (PrefabOwnerSection<PrefabConfigurationEntry> section in sections)
        {
            section.Entries.Sort(CompareObjectEntriesForOutput);
        }

        return sections;
    }

    private static List<PrefabReferenceEntry> BuildReferenceEntries()
    {
        List<PrefabReferenceEntry> entries = BuildConfigurationTemplate()
            .SelectMany(section => section.Entries)
            .Select(entry => new PrefabReferenceEntry
            {
                Prefab = entry.Prefab,
                DropOnDestroyed = entry.DropOnDestroyed,
                MineRock = entry.MineRock,
                MineRock5 = entry.MineRock5,
                TreeBase = entry.TreeBase,
                TreeLog = entry.TreeLog,
                Container = entry.Container,
                Destructible = entry.Destructible,
                Pickable = entry.Pickable,
                PickableItem = entry.PickableItem,
                Fish = entry.Fish
            })
            .ToList();

        HashSet<string> existingPrefabs = ReferenceRefreshSupport.ToNormalizedKeySet(entries.Select(entry => entry.Prefab));
        foreach (PrefabReferenceEntry entry in BuildSupplementalLocationReferenceEntries(existingPrefabs))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static string BuildReferenceConfigurationTemplate()
    {
        return SerializeReferenceEntries(BuildReferenceEntries());
    }

    private static string SerializeReferenceEntries(IEnumerable<PrefabReferenceEntry> entries)
    {
        Dictionary<string, LocationReferenceBucket> locationBuckets = BuildLocationReferenceBuckets();
        List<PrefabOwnerSection<PrefabReferenceEntry>> sections = PrefabOutputSections.BuildSections(
            entries,
            entry => entry.Prefab,
            entry => ResolveObjectOwnerName(entry.Prefab, locationBuckets));
        foreach (PrefabOwnerSection<PrefabReferenceEntry> section in sections)
        {
            section.Entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Prefab, right.Prefab));
        }

        return PrefabOutputSections.SerializeReferenceSections(sections, Serializer);
    }

    internal static bool TryWriteFullScaffoldConfigurationFile(out string path, out string error)
    {
        string content;
        string logMessage;
        lock (Sync)
        {
            path = FullScaffoldConfigurationPath;
            error = "";

            if (!IsGameDataReady() && Snapshots.Count == 0)
            {
                error = "Object game data is not ready yet.";
                return false;
            }

            CaptureSnapshotsIfNeeded();
            content = BuildFullScaffoldConfigurationTemplate();
            logMessage = $"Wrote object full scaffold configuration to {path}.";
        }

        GeneratedArtifactWriter.WriteTextAlways(path, content, logMessage);
        return true;
    }

    internal static void RefreshReferenceConfigurationFile()
    {
        string referenceContent;
        string locationReferenceContent;
        string sourceSignature;
        string logMessage;
        lock (Sync)
        {
            if (!IsGameDataReady())
            {
                return;
            }

            CaptureSnapshotsIfNeeded();
            referenceContent = BuildReferenceConfigurationTemplate();
            locationReferenceContent = BuildLocationReferenceConfigurationTemplate();
            sourceSignature = ComputeReferenceSourceSignature();
            logMessage = $"Updated object reference configurations at {ReferenceConfigurationPath} and {LocationReferenceConfigurationPath}.";
        }

        WriteReferenceConfigurationFile(referenceContent, logMessage);
        WriteLocationReferenceConfigurationFile(locationReferenceContent);
        ReferenceArtifactLifecycle.RecordUpdate(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, sourceSignature);
        ReferenceArtifactLifecycle.RecordUpdate(LocationReferenceAutoUpdateStateKey, LocationReferenceConfigurationPath, sourceSignature);
    }

    private static void WriteReferenceConfigurationFile(string content, string logMessage)
    {
        GeneratedArtifactWriter.WriteText(ReferenceConfigurationPath, content, logMessage);
    }

    private static string BuildLocationReferenceConfigurationTemplate()
    {
        List<ObjectLocationReferenceEntry> entries = BuildLocationReferenceEntries();
        return SerializeLocationReferenceEntries(entries);
    }

    private static List<ObjectLocationReferenceEntry> BuildLocationReferenceEntries()
    {
        return BuildLocationReferenceBuckets()
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ObjectLocationReferenceEntry
            {
                Prefab = pair.Key,
                Components = pair.Value.Components.ToList(),
                Locations = pair.Value.Locations.ToList()
            })
            .ToList();
    }

    private static string SerializeLocationReferenceEntries(IEnumerable<ObjectLocationReferenceEntry> entries)
    {
        Dictionary<string, LocationReferenceBucket> locationBuckets = BuildLocationReferenceBuckets();
        List<PrefabOwnerSection<ObjectLocationReferenceEntry>> sections = PrefabOutputSections.BuildSections(
            entries,
            entry => entry.Prefab,
            entry => ResolveObjectOwnerName(entry.Prefab, locationBuckets));
        foreach (PrefabOwnerSection<ObjectLocationReferenceEntry> section in sections)
        {
            section.Entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Prefab, right.Prefab));
        }

        return PrefabOutputSections.SerializeReferenceSections(sections, Serializer);
    }

    private static string ResolveObjectOwnerName(string prefabName, Dictionary<string, LocationReferenceBucket> locationBuckets)
    {
        PrefabOwnerResolver.OwnerSnapshot ownerSnapshot = PrefabOwnerResolver.GetSnapshot();
        string normalizedPrefabName = (prefabName ?? "").Trim();
        if (normalizedPrefabName.Length > 0 &&
            locationBuckets.TryGetValue(normalizedPrefabName, out LocationReferenceBucket? bucket))
        {
            List<string> locationOwners = bucket.Locations
                .Select(ownerSnapshot.GetOwnerName)
                .Where(ownerName => !string.Equals(ownerName, PrefabOwnerCatalog.UnknownOwnerName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (locationOwners.Count == 1)
            {
                return locationOwners[0];
            }

            if (locationOwners.Count > 1)
            {
                return PrefabOwnerCatalog.UnknownOwnerName;
            }
        }

        return ownerSnapshot.GetOwnerName(normalizedPrefabName);
    }

    private static void WriteLocationReferenceConfigurationFile(string content)
    {
        GeneratedArtifactWriter.WriteText(LocationReferenceConfigurationPath, content);
    }
}
