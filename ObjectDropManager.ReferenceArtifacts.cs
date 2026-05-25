using System;
using System.Collections.Generic;
using System.IO;
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

    private static void WriteReferenceConfigurationFile(string content, string logMessage)
    {
        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        GeneratedFileWriter.WriteAllTextIfChanged(ReferenceConfigurationPath, content);
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(logMessage);
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
        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        GeneratedFileWriter.WriteAllTextIfChanged(LocationReferenceConfigurationPath, content);
    }
}
