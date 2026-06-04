using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private static string BuildFullScaffoldConfigurationTemplate()
    {
        StringBuilder builder = new();
        bool wroteAny = false;

        foreach (PrefabOwnerSection<LocationSnapshot> section in BuildOrderedSnapshots())
        {
            foreach (LocationSnapshot snapshot in section.Entries)
            {
                if (wroteAny)
                {
                    AppendScaffoldBlankLine(builder);
                }

                AppendScaffoldEntry(builder, snapshot);
                wroteAny = true;
            }
        }

        return wroteAny ? builder.ToString() : "[]" + Environment.NewLine;
    }

    private static void AppendScaffoldEntry(StringBuilder builder, LocationSnapshot snapshot)
    {
        AppendScaffoldListEntryLine(builder, 0, "prefab", snapshot.Prefab);
        AppendScaffoldLine(builder, 1, "enabled: true");
        AppendScaffoldConditionsBlock(builder, 1);

        if (snapshot.OfferingBowl != null)
        {
            AppendScaffoldLine(builder, 1, "offeringBowl:");
            AppendScaffoldStringLine(builder, 2, "name", snapshot.OfferingBowl.Name);
            AppendScaffoldStringLine(builder, 2, "useItemText", snapshot.OfferingBowl.UseItemText);
            AppendScaffoldStringLine(builder, 2, "usedAltarText", snapshot.OfferingBowl.UsedAltarText);
            AppendScaffoldStringLine(builder, 2, "cantOfferText", snapshot.OfferingBowl.CantOfferText);
            AppendScaffoldStringLine(builder, 2, "wrongOfferText", snapshot.OfferingBowl.WrongOfferText);
            AppendScaffoldStringLine(builder, 2, "incompleteOfferText", snapshot.OfferingBowl.IncompleteOfferText);
            AppendScaffoldStringLine(builder, 2, "bossItem", snapshot.OfferingBowl.BossItem);
            AppendScaffoldLine(builder, 2, $"bossItems: {snapshot.OfferingBowl.BossItems}");
            AppendScaffoldStringLine(builder, 2, "bossPrefab", snapshot.OfferingBowl.BossPrefab);
            AppendScaffoldStringLine(builder, 2, "itemPrefab", snapshot.OfferingBowl.ItemPrefab);
            AppendScaffoldStringLine(builder, 2, "setGlobalKey", snapshot.OfferingBowl.SetGlobalKey);
            AppendScaffoldLine(builder, 2, $"renderSpawnAreaGizmos: {FormatYamlBool(snapshot.OfferingBowl.RenderSpawnAreaGizmos)}");
            AppendScaffoldLine(builder, 2, $"alertOnSpawn: {FormatYamlBool(snapshot.OfferingBowl.AlertOnSpawn)}");
            AppendScaffoldLine(builder, 2, $"spawnBossDelay: {FormatYamlFloat(snapshot.OfferingBowl.SpawnBossDelay)}");
            AppendScaffoldLine(builder, 2, $"spawnBossDistance: {RangeFormatting.FormatInlineObject(RangeFormatting.From(snapshot.OfferingBowl.SpawnBossMinDistance, snapshot.OfferingBowl.SpawnBossMaxDistance))}");
            AppendScaffoldLine(builder, 2, $"spawnBossMaxYDistance: {FormatYamlFloat(snapshot.OfferingBowl.SpawnBossMaxYDistance)}");
            AppendScaffoldLine(builder, 2, $"getSolidHeightMargin: {snapshot.OfferingBowl.GetSolidHeightMargin}");
            AppendScaffoldLine(builder, 2, $"enableSolidHeightCheck: {FormatYamlBool(snapshot.OfferingBowl.EnableSolidHeightCheck)}");
            AppendScaffoldLine(builder, 2, $"spawnPointClearingRadius: {FormatYamlFloat(snapshot.OfferingBowl.SpawnPointClearingRadius)}");
            AppendScaffoldLine(builder, 2, $"spawnYOffset: {FormatYamlFloat(snapshot.OfferingBowl.SpawnYOffset)}");
            AppendScaffoldLine(builder, 2, $"useItemStands: {FormatYamlBool(snapshot.OfferingBowl.UseItemStands)}");
            AppendScaffoldStringLine(builder, 2, "itemStandPrefix", snapshot.OfferingBowl.ItemStandPrefix);
            AppendScaffoldLine(builder, 2, $"itemStandMaxRange: {FormatYamlFloat(snapshot.OfferingBowl.ItemStandMaxRange)}");
            AppendScaffoldLine(builder, 2, "respawnMinutes: 0");
            AppendScaffoldStringLine(builder, 2, "data", null);
            AppendScaffoldLine(builder, 2, "fields: {}");
            AppendScaffoldLine(builder, 2, "objects: []");
        }

        if (snapshot.ItemStands.Count > 0)
        {
            AppendScaffoldLine(builder, 1, "itemStands:");
            foreach (PathScopedItemStandSnapshot itemStand in snapshot.ItemStands)
            {
                AppendScaffoldListEntryLine(builder, 1, "path", itemStand.Path);
                AppendScaffoldStringLine(builder, 2, "name", itemStand.Snapshot.Name);
                AppendScaffoldLine(builder, 2, $"canBeRemoved: {FormatYamlBool(itemStand.Snapshot.CanBeRemoved)}");
                AppendScaffoldLine(builder, 2, $"autoAttach: {FormatYamlBool(itemStand.Snapshot.AutoAttach)}");
                AppendScaffoldStringLine(builder, 2, "orientationType", itemStand.Snapshot.OrientationType);
                AppendScaffoldInlineListLine(builder, 2, "supportedTypes", itemStand.Snapshot.SupportedTypes);
                AppendScaffoldInlineListLine(builder, 2, "supportedItems", itemStand.Snapshot.SupportedItems);
                AppendScaffoldInlineListLine(builder, 2, "unsupportedItems", itemStand.Snapshot.UnsupportedItems);
                AppendScaffoldLine(builder, 2, $"powerActivationDelay: {FormatYamlFloat(itemStand.Snapshot.PowerActivationDelay)}");
                AppendScaffoldStringLine(builder, 2, "guardianPower", itemStand.Snapshot.GuardianPower);
            }
        }

    }

    private static void AppendScaffoldLine(StringBuilder builder, int indent, string text)
    {
        builder.Append(' ', indent * 2);
        builder.AppendLine(text);
    }

    private static void AppendScaffoldBlankLine(StringBuilder builder)
    {
        builder.AppendLine();
    }

    private static void AppendScaffoldConditionsBlock(StringBuilder builder, int indent)
    {
        AppendScaffoldLine(builder, indent, "conditions:");
        AppendScaffoldLine(builder, indent + 1, "biomes: []");
        AppendScaffoldLine(builder, indent + 1, "altitude: null");
        AppendScaffoldLine(builder, indent + 1, "distanceFromCenter: null");
        AppendScaffoldLine(builder, indent + 1, "inDungeon: null");
        AppendScaffoldLine(builder, indent + 1, "inForest: null");
    }

    private static void AppendScaffoldStringLine(StringBuilder builder, int indent, string key, string? value)
    {
        if (value == null)
        {
            AppendScaffoldLine(builder, indent, $"{key}: null");
            return;
        }

        AppendScaffoldLine(builder, indent, $"{key}: {FormatYamlString(value)}");
    }

    private static void AppendScaffoldInlineListLine(StringBuilder builder, int indent, string key, List<string>? values)
    {
        if (values == null || values.Count == 0)
        {
            AppendScaffoldLine(builder, indent, $"{key}: []");
            return;
        }

        AppendScaffoldLine(builder, indent, $"{key}: [{string.Join(", ", values.Select(FormatYamlString))}]");
    }

    private static void AppendScaffoldListEntryLine(StringBuilder builder, int indent, string key, string value)
    {
        AppendScaffoldLine(builder, indent, $"- {key}: {FormatYamlString(value)}");
    }
}
