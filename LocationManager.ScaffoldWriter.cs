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

        if (snapshot.Vegvisirs.Count > 0)
        {
            AppendScaffoldLine(builder, 1, "vegvisirs:");
            foreach (PathScopedVegvisirSnapshot vegvisir in snapshot.Vegvisirs)
            {
                AppendScaffoldListEntryLine(builder, 1, "path", vegvisir.Path);
                AppendScaffoldInlineListLine(builder, 2, "expectedLocations", GetExpectedVegvisirLocations(vegvisir.Snapshot));
                AppendScaffoldStringLine(builder, 2, "name", vegvisir.Snapshot.Name);
                AppendScaffoldStringLine(builder, 2, "useText", vegvisir.Snapshot.UseText);
                AppendScaffoldStringLine(builder, 2, "hoverName", vegvisir.Snapshot.HoverName);
                AppendScaffoldStringLine(builder, 2, "setsGlobalKey", vegvisir.Snapshot.SetsGlobalKey);
                AppendScaffoldStringLine(builder, 2, "setsPlayerKey", vegvisir.Snapshot.SetsPlayerKey);
                if (vegvisir.Snapshot.Locations.Count == 0)
                {
                    AppendScaffoldLine(builder, 2, "locations: []");
                }
                else
                {
                    AppendScaffoldLine(builder, 2, "locations:");
                    foreach (VegvisirTargetSnapshot target in vegvisir.Snapshot.Locations)
                    {
                        AppendScaffoldListEntryLine(builder, 2, "locationName", target.LocationName);
                        AppendScaffoldStringLine(builder, 3, "pinName", target.PinName);
                        AppendScaffoldStringLine(builder, 3, "pinType", target.PinType);
                        AppendScaffoldLine(builder, 3, $"discoverAll: {FormatYamlBool(target.DiscoverAll)}");
                        AppendScaffoldLine(builder, 3, $"showMap: {FormatYamlBool(target.ShowMap)}");
                        AppendScaffoldLine(builder, 3, "weight: null");
                    }
                }
            }
        }

        if (snapshot.Runestones.Count > 0)
        {
            AppendScaffoldLine(builder, 1, "runestones:");
            foreach (PathScopedRunestoneSnapshot runestone in snapshot.Runestones)
            {
                AppendScaffoldListEntryLine(builder, 1, "path", runestone.Path);
                AppendScaffoldStringLine(builder, 2, "expectedLocationName", string.IsNullOrWhiteSpace(runestone.Snapshot.LocationName) ? null : runestone.Snapshot.LocationName);
                AppendScaffoldStringLine(builder, 2, "expectedLabel", string.IsNullOrWhiteSpace(runestone.Snapshot.Label) ? null : runestone.Snapshot.Label);
                AppendScaffoldStringLine(builder, 2, "expectedTopic", string.IsNullOrWhiteSpace(runestone.Snapshot.Topic) ? null : runestone.Snapshot.Topic);
                AppendScaffoldStringLine(builder, 2, "name", runestone.Snapshot.Name);
                AppendScaffoldStringLine(builder, 2, "topic", runestone.Snapshot.Topic);
                AppendScaffoldStringLine(builder, 2, "label", runestone.Snapshot.Label);
                AppendScaffoldStringLine(builder, 2, "text", runestone.Snapshot.Text);
                if (runestone.Snapshot.RandomTexts.Count == 0)
                {
                    AppendScaffoldLine(builder, 2, "randomTexts: []");
                }
                else
                {
                    AppendScaffoldLine(builder, 2, "randomTexts:");
                    foreach (RunestoneTextSnapshot text in runestone.Snapshot.RandomTexts)
                    {
                        AppendScaffoldListEntryLine(builder, 2, "topic", text.Topic);
                        AppendScaffoldStringLine(builder, 3, "label", text.Label);
                        AppendScaffoldStringLine(builder, 3, "text", text.Text);
                    }
                }

                AppendScaffoldStringLine(builder, 2, "locationName", runestone.Snapshot.LocationName);
                AppendScaffoldStringLine(builder, 2, "pinName", runestone.Snapshot.PinName);
                AppendScaffoldStringLine(builder, 2, "pinType", runestone.Snapshot.PinType);
                AppendScaffoldLine(builder, 2, $"showMap: {FormatYamlBool(runestone.Snapshot.ShowMap)}");
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
