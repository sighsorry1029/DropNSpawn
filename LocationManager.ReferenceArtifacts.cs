using System;
using System.Collections.Generic;
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
                        ItemStands = snapshot.ItemStands.Count > 0 ? snapshot.ItemStands.Select(ConvertReferenceItemStand).ToList() : null
                    })
                    .ToList()))
            .ToList();

        return PrefabOutputSections.SerializeReferenceSections(sections, Serializer);
    }

    private static string SerializeReferenceEntries(IEnumerable<LocationReferenceEntry> entries)
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

            if (!IsGameDataReady() && !_snapshotsCaptured)
            {
                error = "Location game data is not ready yet.";
                return false;
            }

            RefreshReferenceSnapshots();
            content = BuildFullScaffoldConfigurationTemplate();
            logMessage = $"Wrote location full scaffold configuration to {path}.";
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

            RefreshReferenceSnapshots();
            content = BuildReferenceConfigurationTemplate();
            sourceSignature = ComputeReferenceSourceSignature();
            logMessage = $"Updated location reference configuration at {ReferenceConfigurationPath}.";
        }

        WriteReferenceConfigurationFile(content, logMessage);
        ReferenceArtifactLifecycle.RecordUpdate(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, sourceSignature);
        lock (Sync)
        {
            ResetReferenceSnapshots();
        }
    }

    private static void WriteReferenceConfigurationFile(string content, string logMessage)
    {
        GeneratedArtifactWriter.WriteText(ReferenceConfigurationPath, content, logMessage);
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

}
