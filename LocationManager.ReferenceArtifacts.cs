using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private static List<PrefabOwnerSection<LocationSnapshot>> BuildOrderedSnapshots()
    {
        List<PrefabOwnerSection<LocationSnapshot>> sections = PrefabOutputSections.BuildSections(Snapshots, snapshot => snapshot.Prefab);

        foreach (PrefabOwnerSection<LocationSnapshot> section in sections)
        {
            section.Entries.Sort(CompareLocationSnapshotsForOutput);
        }

        return sections;
    }

    private static string BuildReferenceConfigurationTemplate()
    {
        List<PrefabOwnerSection<LocationReferenceEntry>> sections = BuildOrderedSnapshots()
            .Select(section => new PrefabOwnerSection<LocationReferenceEntry>(
                section.OwnerName,
                section.Entries
                    .Select(snapshot => new LocationReferenceEntry
                    {
                        Prefab = snapshot.Prefab,
                        OfferingBowl = snapshot.OfferingBowl != null ? ConvertReferenceOfferingBowl(snapshot.OfferingBowl) : null,
                        ItemStands = snapshot.ItemStands.Count > 0 ? snapshot.ItemStands.Select(ConvertReferenceItemStand).ToList() : null,
                        Vegvisirs = snapshot.Vegvisirs.Count == 0 ? null : snapshot.Vegvisirs.Select(ConvertReferenceVegvisir).ToList(),
                        Runestones = snapshot.Runestones.Count == 0 ? null : snapshot.Runestones.Select(ConvertReferenceRunestone).ToList()
                    })
                    .ToList()))
            .ToList();

        return PrefabOutputSections.SerializeReferenceSections(sections, Serializer);
    }

    private static string SerializeReferenceEntries(IEnumerable<LocationReferenceEntry> entries)
    {
        return ReferenceRefreshSupport.SerializeReferenceSections(entries, entry => entry.Prefab, Serializer);
    }

    private static void WriteReferenceConfigurationFile(string content, string logMessage)
    {
        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        GeneratedFileWriter.WriteAllTextIfChanged(ReferenceConfigurationPath, content);
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(logMessage);
    }

    private static LocationOfferingBowlDefinition ConvertReferenceOfferingBowl(OfferingBowlSnapshot snapshot)
    {
        return new LocationOfferingBowlDefinition
        {
            Name = string.IsNullOrWhiteSpace(snapshot.Name) ? null : snapshot.Name,
            UseItemText = string.IsNullOrWhiteSpace(snapshot.UseItemText) ? null : snapshot.UseItemText,
            UsedAltarText = string.IsNullOrWhiteSpace(snapshot.UsedAltarText) ? null : snapshot.UsedAltarText,
            CantOfferText = string.IsNullOrWhiteSpace(snapshot.CantOfferText) ? null : snapshot.CantOfferText,
            WrongOfferText = string.IsNullOrWhiteSpace(snapshot.WrongOfferText) ? null : snapshot.WrongOfferText,
            IncompleteOfferText = string.IsNullOrWhiteSpace(snapshot.IncompleteOfferText) ? null : snapshot.IncompleteOfferText,
            BossItem = snapshot.BossItem.Length == 0 ? null : snapshot.BossItem,
            BossItems = snapshot.BossItems == 1 ? null : snapshot.BossItems,
            BossPrefab = snapshot.BossPrefab.Length == 0 ? null : snapshot.BossPrefab,
            ItemPrefab = snapshot.ItemPrefab.Length == 0 ? null : snapshot.ItemPrefab,
            SetGlobalKey = string.IsNullOrWhiteSpace(snapshot.SetGlobalKey) ? null : snapshot.SetGlobalKey,
            RenderSpawnAreaGizmos = snapshot.RenderSpawnAreaGizmos ? true : null,
            AlertOnSpawn = snapshot.AlertOnSpawn ? true : null,
            SpawnBossDelay = IsReferenceDefault(snapshot.SpawnBossDelay, 5f) ? null : snapshot.SpawnBossDelay,
            SpawnBossDistance = RangeFormatting.FromReference(snapshot.SpawnBossMinDistance, snapshot.SpawnBossMaxDistance, 0f, 40f),
            SpawnBossMaxYDistance = IsReferenceDefault(snapshot.SpawnBossMaxYDistance, 9999f) ? null : snapshot.SpawnBossMaxYDistance,
            GetSolidHeightMargin = snapshot.GetSolidHeightMargin == 1000 ? null : snapshot.GetSolidHeightMargin,
            EnableSolidHeightCheck = snapshot.EnableSolidHeightCheck ? null : false,
            SpawnPointClearingRadius = IsReferenceDefault(snapshot.SpawnPointClearingRadius, 0f) ? null : snapshot.SpawnPointClearingRadius,
            SpawnYOffset = IsReferenceDefault(snapshot.SpawnYOffset, 1f) ? null : snapshot.SpawnYOffset,
            UseItemStands = snapshot.UseItemStands ? true : null,
            ItemStandPrefix = string.IsNullOrWhiteSpace(snapshot.ItemStandPrefix) ? null : snapshot.ItemStandPrefix,
            ItemStandMaxRange = IsReferenceDefault(snapshot.ItemStandMaxRange, 20f) ? null : snapshot.ItemStandMaxRange,
            RespawnMinutes = null
        };
    }

    private static LocationVegvisirDefinition ConvertReferenceVegvisir(PathScopedVegvisirSnapshot snapshot)
    {
        return new LocationVegvisirDefinition
        {
            Path = snapshot.Path,
            ExpectedLocations = GetExpectedVegvisirLocations(snapshot.Snapshot),
            Name = string.IsNullOrWhiteSpace(snapshot.Snapshot.Name) || snapshot.Snapshot.Name == "$piece_vegvisir" ? null : snapshot.Snapshot.Name,
            UseText = string.IsNullOrWhiteSpace(snapshot.Snapshot.UseText) || snapshot.Snapshot.UseText == "$piece_register_location" ? null : snapshot.Snapshot.UseText,
            HoverName = string.IsNullOrWhiteSpace(snapshot.Snapshot.HoverName) || snapshot.Snapshot.HoverName == "Pin" ? null : snapshot.Snapshot.HoverName,
            SetsGlobalKey = string.IsNullOrWhiteSpace(snapshot.Snapshot.SetsGlobalKey) ? null : snapshot.Snapshot.SetsGlobalKey,
            SetsPlayerKey = string.IsNullOrWhiteSpace(snapshot.Snapshot.SetsPlayerKey) ? null : snapshot.Snapshot.SetsPlayerKey,
            Locations = snapshot.Snapshot.Locations.Count == 0 ? null : snapshot.Snapshot.Locations.Select(ConvertReferenceVegvisirTarget).ToList()
        };
    }

    private static List<string>? GetExpectedVegvisirLocations(VegvisirSnapshot snapshot)
    {
        List<string> expectedLocations = snapshot.Locations
            .Select(location => (location.LocationName ?? "").Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return expectedLocations.Count == 0 ? null : expectedLocations;
    }

    private static LocationItemStandDefinition ConvertReferenceItemStand(PathScopedItemStandSnapshot snapshot)
    {
        return new LocationItemStandDefinition
        {
            Path = snapshot.Path,
            Name = string.IsNullOrWhiteSpace(snapshot.Snapshot.Name) ? null : snapshot.Snapshot.Name,
            CanBeRemoved = snapshot.Snapshot.CanBeRemoved ? null : false,
            AutoAttach = snapshot.Snapshot.AutoAttach ? true : null,
            OrientationType = string.IsNullOrWhiteSpace(snapshot.Snapshot.OrientationType) || snapshot.Snapshot.OrientationType == ItemStand.Orientation.Vertical.ToString() ? null : snapshot.Snapshot.OrientationType,
            SupportedTypes = snapshot.Snapshot.SupportedTypes.Count == 0 ? null : snapshot.Snapshot.SupportedTypes,
            SupportedItems = snapshot.Snapshot.SupportedItems.Count == 0 ? null : snapshot.Snapshot.SupportedItems,
            UnsupportedItems = snapshot.Snapshot.UnsupportedItems.Count == 0 ? null : snapshot.Snapshot.UnsupportedItems,
            PowerActivationDelay = IsReferenceDefault(snapshot.Snapshot.PowerActivationDelay, 2f) ? null : snapshot.Snapshot.PowerActivationDelay,
            GuardianPower = string.IsNullOrWhiteSpace(snapshot.Snapshot.GuardianPower) ? null : snapshot.Snapshot.GuardianPower
        };
    }

    private static LocationRunestoneDefinition ConvertReferenceRunestone(PathScopedRunestoneSnapshot snapshot)
    {
        return new LocationRunestoneDefinition
        {
            Path = snapshot.Path,
            ExpectedLocationName = string.IsNullOrWhiteSpace(snapshot.Snapshot.LocationName) ? null : snapshot.Snapshot.LocationName,
            ExpectedLabel = string.IsNullOrWhiteSpace(snapshot.Snapshot.Label) ? null : snapshot.Snapshot.Label,
            ExpectedTopic = string.IsNullOrWhiteSpace(snapshot.Snapshot.Topic) ? null : snapshot.Snapshot.Topic,
            Name = string.IsNullOrWhiteSpace(snapshot.Snapshot.Name) || snapshot.Snapshot.Name == "Rune stone" ? null : snapshot.Snapshot.Name,
            Topic = string.IsNullOrWhiteSpace(snapshot.Snapshot.Topic) ? null : snapshot.Snapshot.Topic,
            Label = string.IsNullOrWhiteSpace(snapshot.Snapshot.Label) ? null : snapshot.Snapshot.Label,
            Text = string.IsNullOrWhiteSpace(snapshot.Snapshot.Text) ? null : snapshot.Snapshot.Text,
            RandomTexts = snapshot.Snapshot.RandomTexts.Count == 0 ? null : snapshot.Snapshot.RandomTexts.Select(ConvertReferenceRunestoneText).ToList(),
            LocationName = string.IsNullOrWhiteSpace(snapshot.Snapshot.LocationName) ? null : snapshot.Snapshot.LocationName,
            PinName = string.IsNullOrWhiteSpace(snapshot.Snapshot.PinName) || snapshot.Snapshot.PinName == "Pin" ? null : snapshot.Snapshot.PinName,
            PinType = string.IsNullOrWhiteSpace(snapshot.Snapshot.PinType) || snapshot.Snapshot.PinType == Minimap.PinType.Boss.ToString() ? null : snapshot.Snapshot.PinType,
            ShowMap = snapshot.Snapshot.ShowMap ? true : null
        };
    }

    private static LocationRunestoneTextDefinition ConvertReferenceRunestoneText(RunestoneTextSnapshot snapshot)
    {
        return new LocationRunestoneTextDefinition
        {
            Topic = string.IsNullOrWhiteSpace(snapshot.Topic) ? null : snapshot.Topic,
            Label = string.IsNullOrWhiteSpace(snapshot.Label) ? null : snapshot.Label,
            Text = string.IsNullOrWhiteSpace(snapshot.Text) ? null : snapshot.Text
        };
    }

    private static LocationVegvisirTargetDefinition ConvertReferenceVegvisirTarget(VegvisirTargetSnapshot snapshot)
    {
        return new LocationVegvisirTargetDefinition
        {
            LocationName = snapshot.LocationName,
            PinName = string.IsNullOrWhiteSpace(snapshot.PinName) || snapshot.PinName == "Pin" ? null : snapshot.PinName,
            PinType = string.IsNullOrWhiteSpace(snapshot.PinType) || snapshot.PinType == Minimap.PinType.Icon0.ToString() ? null : snapshot.PinType,
            DiscoverAll = snapshot.DiscoverAll ? true : null,
            ShowMap = snapshot.ShowMap ? null : false
        };
    }
}
