using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

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

    private static string FormatYamlBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatYamlFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatYamlString(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        bool requiresQuotes =
            char.IsWhiteSpace(value[0]) ||
            char.IsWhiteSpace(value[value.Length - 1]) ||
            value.IndexOfAny(new[] { ':', '#', '{', '}', '[', ']', ',', '\'', '"', '&', '*', '!', '|', '>', '%', '@', '`' }) >= 0 ||
            value[0] == '-' ||
            value[0] == '?' ||
            string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        return requiresQuotes ? $"'{value.Replace("'", "''")}'" : value;
    }

    private static PrefabConfigurationEntry BuildConfigurationEntry(PrefabSnapshot snapshot)
    {
        return new PrefabConfigurationEntry
        {
            Prefab = snapshot.Prefab.name,
            Enabled = true,
            Destructible = ConvertDestructible(snapshot),
            DropOnDestroyed = snapshot.DropOnDestroyed != null ? ConvertDropTable(snapshot.DropOnDestroyed) : null,
            MineRock = snapshot.MineRock != null ? ConvertDamageableDropTable(snapshot.MineRock, snapshot.Health?.MineRock, snapshot.MinToolTier?.MineRock) : null,
            MineRock5 = snapshot.MineRock5 != null ? ConvertDamageableDropTable(snapshot.MineRock5, snapshot.Health?.MineRock5, snapshot.MinToolTier?.MineRock5) : null,
            TreeBase = snapshot.TreeBase != null ? ConvertDamageableDropTable(snapshot.TreeBase, snapshot.Health?.TreeBase, snapshot.MinToolTier?.TreeBase) : null,
            TreeLog = snapshot.TreeLog != null ? ConvertDamageableDropTable(snapshot.TreeLog, snapshot.Health?.TreeLog, snapshot.MinToolTier?.TreeLog) : null,
            Container = snapshot.Container != null ? ConvertDropTable(snapshot.Container) : null,
            Pickable = snapshot.Pickable != null ? ConvertPickable(snapshot.Pickable) : null,
            PickableItem = snapshot.PickableItem != null ? ConvertPickableItem(snapshot.PickableItem) : null,
            Fish = snapshot.Fish != null ? ConvertFish(snapshot.Fish) : null
        };
    }

    private static int CompareObjectEntriesForOutput(PrefabConfigurationEntry? left, PrefabConfigurationEntry? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left == null)
        {
            return 1;
        }

        if (right == null)
        {
            return -1;
        }

        int primaryComparison = GetPrimaryObjectComponentRank(left).CompareTo(GetPrimaryObjectComponentRank(right));
        if (primaryComparison != 0)
        {
            return primaryComparison;
        }

        int signatureComparison = GetObjectComponentSignatureMask(left).CompareTo(GetObjectComponentSignatureMask(right));
        if (signatureComparison != 0)
        {
            return signatureComparison;
        }

        return string.Compare(left.Prefab, right.Prefab, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPrimaryObjectComponentRank(PrefabConfigurationEntry entry)
    {
        if (entry.Container != null)
        {
            return 0;
        }

        if (entry.Pickable != null)
        {
            return 1;
        }

        if (entry.PickableItem != null)
        {
            return 2;
        }

        if (entry.Fish != null)
        {
            return 3;
        }

        if (entry.MineRock != null)
        {
            return 4;
        }

        if (entry.MineRock5 != null)
        {
            return 5;
        }

        if (entry.TreeBase != null)
        {
            return 6;
        }

        if (entry.TreeLog != null)
        {
            return 7;
        }

        if (entry.DropOnDestroyed != null)
        {
            return 8;
        }

        if (entry.Destructible != null)
        {
            return 9;
        }

        return 10;
    }

    private static int GetObjectComponentSignatureMask(PrefabConfigurationEntry entry)
    {
        int mask = 0;
        if (entry.Container != null)
        {
            mask |= 1 << 0;
        }

        if (entry.Pickable != null)
        {
            mask |= 1 << 1;
        }

        if (entry.PickableItem != null)
        {
            mask |= 1 << 2;
        }

        if (entry.Fish != null)
        {
            mask |= 1 << 3;
        }

        if (entry.MineRock != null)
        {
            mask |= 1 << 4;
        }

        if (entry.MineRock5 != null)
        {
            mask |= 1 << 5;
        }

        if (entry.TreeBase != null)
        {
            mask |= 1 << 6;
        }

        if (entry.TreeLog != null)
        {
            mask |= 1 << 7;
        }

        if (entry.DropOnDestroyed != null)
        {
            mask |= 1 << 8;
        }

        if (entry.Destructible != null)
        {
            mask |= 1 << 9;
        }

        return mask;
    }

    private static DestructibleDefinition? ConvertDestructible(PrefabSnapshot snapshot)
    {
        bool hasHealth = snapshot.Health?.Destructible.HasValue == true;
        bool hasMinToolTier = snapshot.MinToolTier?.Destructible.HasValue == true;
        if (!hasHealth && !hasMinToolTier && snapshot.Destructible == null)
        {
            return null;
        }

        return new DestructibleDefinition
        {
            Health = hasHealth ? snapshot.Health!.Destructible : null,
            MinToolTier = hasMinToolTier ? snapshot.MinToolTier!.Destructible : null,
            DestructibleType = snapshot.Destructible != null && snapshot.Destructible.DestructibleType != DestructibleType.Default
                ? snapshot.Destructible.DestructibleType.ToString()
                : null,
            SpawnWhenDestroyed = NormalizeReferencePrefabName(snapshot.Destructible?.SpawnWhenDestroyed)
        };
    }

    private static DropTableDefinition ConvertDropTable(DropTable dropTable)
    {
        int dropMin = Math.Max(0, dropTable.m_dropMin);
        int dropMax = Math.Max(dropMin, dropTable.m_dropMax);
        List<DropEntryDefinition> drops = dropTable.m_drops
            .Select(drop => new { Name = NormalizeReferencePrefabName(drop.m_item), Drop = drop })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new DropEntryDefinition
            {
                Item = entry.Name!,
                Stack = RangeFormatting.FromReference(entry.Drop.m_stackMin, entry.Drop.m_stackMax, 1, 1),
                Weight = IsReferenceDefault(entry.Drop.m_weight, 1f) ? null : entry.Drop.m_weight,
                DontScale = entry.Drop.m_dontScale ? true : null
            })
            .ToList();

        return new DropTableDefinition
        {
            Rolls = RangeFormatting.FromReference(dropMin, dropMax, 1, 1),
            DropChance = IsReferenceDefault(dropTable.m_dropChance, 1f) ? null : dropTable.m_dropChance,
            OneOfEach = dropTable.m_oneOfEach ? true : null,
            Drops = drops.Count > 0 ? drops : null
        };
    }

    private static DamageableDropTableDefinition ConvertDamageableDropTable(DropTable dropTable, float? health, int? minToolTier)
    {
        DropTableDefinition dropTableDefinition = ConvertDropTable(dropTable);

        return new DamageableDropTableDefinition
        {
            Health = health,
            MinToolTier = minToolTier,
            Rolls = dropTableDefinition.Rolls,
            DropChance = dropTableDefinition.DropChance,
            OneOfEach = dropTableDefinition.OneOfEach,
            Drops = dropTableDefinition.Drops
        };
    }

    private static PickableDefinition ConvertPickable(PickableSnapshot snapshot)
    {
        DropTableDefinition extraDrops = ConvertDropTable(snapshot.ExtraDrops);
        string? itemName = NormalizeReferencePrefabName(snapshot.ItemPrefab);
        PickableDropDefinition? drop = null;
        if (!string.IsNullOrWhiteSpace(itemName) || snapshot.Amount != 1 || snapshot.MinAmountScaled != 0 || snapshot.DontScale)
        {
            drop = new PickableDropDefinition
            {
                Item = itemName ?? "",
                Amount = snapshot.Amount == 1 ? null : snapshot.Amount,
                MinAmountScaled = snapshot.MinAmountScaled == 0 ? null : snapshot.MinAmountScaled,
                DontScale = snapshot.DontScale ? true : null
            };
        }

        return new PickableDefinition
        {
            OverrideName = string.IsNullOrWhiteSpace(snapshot.OverrideName) ? null : snapshot.OverrideName,
            Drop = drop,
            ExtraDrops = HasReferenceDropTableContent(extraDrops) ? extraDrops : null
        };
    }

    private static PickableItemDefinition ConvertPickableItem(PickableItemSnapshot snapshot)
    {
        List<RandomPickableItemDefinition> randomDrops = snapshot.RandomItems
            .Select(item => new { Name = NormalizeReferencePrefabName(item.ItemPrefab), Item = item })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new RandomPickableItemDefinition
            {
                Item = entry.Name!,
                Stack = RangeFormatting.FromReference(entry.Item.StackMin, entry.Item.StackMax, 1, 1)
            })
            .ToList();

        string? fixedItemName = NormalizeReferencePrefabName(snapshot.ItemPrefab);
        return new PickableItemDefinition
        {
            RandomDrops = randomDrops.Count > 0 ? randomDrops : null,
            Drop = randomDrops.Count == 0 && (!string.IsNullOrWhiteSpace(fixedItemName) || snapshot.Stack != 1)
                ? new PickableItemDropDefinition
                {
                    Item = fixedItemName ?? "",
                    Stack = snapshot.Stack == 1 ? null : snapshot.Stack
                }
                : null
        };
    }

    private static FishDefinition ConvertFish(FishSnapshot snapshot)
    {
        DropTableDefinition extraDrops = ConvertDropTable(snapshot.ExtraDrops);
        return new FishDefinition
        {
            ExtraDrops = HasReferenceDropTableContent(extraDrops) ? extraDrops : null
        };
    }

    private static bool HasReferenceDropTableContent(DropTablePayloadDefinition? definition)
    {
        return definition != null &&
               (definition.Rolls?.HasValues() == true ||
                definition.DropMin.HasValue ||
                definition.DropMax.HasValue ||
                definition.DropChance.HasValue ||
                definition.OneOfEach.HasValue ||
                (definition.Drops != null && definition.Drops.Count > 0));
    }

    private static IntRangeDefinition? GetRollsRange(DropTablePayloadDefinition definition)
    {
        return definition.Rolls ?? RangeFormatting.From(definition.DropMin, definition.DropMax ?? definition.DropMin);
    }

    private static IntRangeDefinition? GetStackRange(DropEntryDefinition definition)
    {
        return definition.Stack ?? RangeFormatting.From(definition.StackMin, definition.StackMax ?? definition.StackMin);
    }

    private static IntRangeDefinition? GetStackRange(RandomPickableItemDefinition definition)
    {
        return definition.Stack ?? RangeFormatting.From(definition.StackMin, definition.StackMax ?? definition.StackMin);
    }

    private static bool IsReferenceDefault(float value, float defaultValue)
    {
        return Math.Abs(value - defaultValue) < 0.0001f;
    }

    private static string? NormalizeReferencePrefabName(GameObject? prefab)
    {
        return prefab == null ? null : NormalizeReferencePrefabName(prefab.name);
    }

    private static string? NormalizeReferencePrefabName(string? prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        string resolvedPrefabName = prefabName!;

        if (!resolvedPrefabName.StartsWith(MockPrefabPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return resolvedPrefabName;
        }

        string normalizedName = resolvedPrefabName.Substring(MockPrefabPrefix.Length);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        if (ZNetScene.instance?.GetPrefab(normalizedName) != null || ObjectDB.instance?.GetItemPrefab(normalizedName) != null)
        {
            return normalizedName;
        }

        return null;
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
