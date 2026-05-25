using System;
using System.Globalization;
using System.Text;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static string BuildFullScaffoldConfigurationTemplate()
    {
        StringBuilder builder = new();
        bool wroteAny = false;

        foreach (PrefabOwnerSection<PrefabConfigurationEntry> section in BuildConfigurationTemplate())
        {
            foreach (PrefabConfigurationEntry entry in section.Entries)
            {
                if (wroteAny)
                {
                    AppendScaffoldBlankLine(builder);
                }

                AppendScaffoldObjectEntry(builder, entry);
                wroteAny = true;
            }
        }

        return wroteAny ? builder.ToString() : "[]" + Environment.NewLine;
    }

    private static void AppendScaffoldObjectEntry(StringBuilder builder, PrefabConfigurationEntry entry)
    {
        AppendScaffoldListEntryLine(builder, 0, "prefab", entry.Prefab);
        AppendScaffoldLine(builder, 1, $"enabled: {FormatYamlBool(entry.Enabled)}");
        AppendScaffoldConditionsBlock(builder, 1);

        if (entry.DropOnDestroyed != null)
        {
            AppendScaffoldDropTableBlock(builder, 1, "dropOnDestroyed", entry.DropOnDestroyed);
        }

        if (entry.MineRock != null)
        {
            AppendScaffoldDamageableDropTableBlock(builder, 1, "mineRock", entry.MineRock);
        }

        if (entry.MineRock5 != null)
        {
            AppendScaffoldDamageableDropTableBlock(builder, 1, "mineRock5", entry.MineRock5);
        }

        if (entry.TreeBase != null)
        {
            AppendScaffoldDamageableDropTableBlock(builder, 1, "treeBase", entry.TreeBase);
        }

        if (entry.TreeLog != null)
        {
            AppendScaffoldDamageableDropTableBlock(builder, 1, "treeLog", entry.TreeLog);
        }

        if (entry.Container != null)
        {
            AppendScaffoldDropTableBlock(builder, 1, "container", entry.Container);
        }

        if (entry.PickableItem != null)
        {
            AppendScaffoldPickableItemBlock(builder, entry.PickableItem);
        }

        if (entry.Pickable != null)
        {
            AppendScaffoldPickableBlock(builder, entry.Pickable);
        }

        if (entry.Fish != null)
        {
            AppendScaffoldFishBlock(builder, entry.Fish);
        }

        if (entry.Destructible != null)
        {
            AppendScaffoldDestructibleBlock(builder, entry.Destructible);
        }
    }

    private static void AppendScaffoldDamageableDropTableBlock(StringBuilder builder, int indent, string blockName, DamageableDropTableDefinition definition)
    {
        AppendScaffoldLine(builder, indent, $"{blockName}:");
        AppendScaffoldLine(builder, indent + 1, $"health: {FormatYamlFloat(definition.Health ?? 1f)}");
        AppendScaffoldLine(builder, indent + 1, $"minToolTier: {definition.MinToolTier.GetValueOrDefault()}");
        AppendScaffoldDropTableValueLines(builder, indent + 1, definition);
    }

    private static void AppendScaffoldDropTableBlock(StringBuilder builder, int indent, string blockName, DropTableDefinition definition)
    {
        AppendScaffoldLine(builder, indent, $"{blockName}:");
        AppendScaffoldDropTableValueLines(builder, indent + 1, definition);
    }

    private static void AppendScaffoldDropTablePayloadBlock(StringBuilder builder, int indent, string blockName, DropTablePayloadDefinition definition)
    {
        AppendScaffoldLine(builder, indent, $"{blockName}:");
        AppendScaffoldDropTableValueLines(builder, indent + 1, definition);
    }

    private static void AppendScaffoldDropTableValueLines(StringBuilder builder, int indent, DropTablePayloadDefinition definition)
    {
        AppendScaffoldLine(builder, indent, $"rolls: {RangeFormatting.FormatInlineObject(GetRollsRange(definition) ?? RangeFormatting.From(1, 1))}");
        AppendScaffoldLine(builder, indent, $"dropChance: {FormatYamlFloat(definition.DropChance ?? 1f)}");
        AppendScaffoldLine(builder, indent, $"oneOfEach: {FormatYamlBool(definition.OneOfEach ?? false)}");

        if (definition.Drops != null && definition.Drops.Count > 0)
        {
            AppendScaffoldLine(builder, indent, "drops:");
            foreach (DropEntryDefinition drop in definition.Drops)
            {
                AppendScaffoldDropEntry(builder, indent + 1, drop);
            }
        }
        else
        {
            AppendScaffoldLine(builder, indent, "drops: []");
        }
    }

    private static void AppendScaffoldDropEntry(StringBuilder builder, int indent, DropEntryDefinition definition)
    {
        AppendScaffoldListEntryLine(builder, indent, "item", definition.Item);
        AppendScaffoldLine(builder, indent + 1, $"stack: {RangeFormatting.FormatInlineObject(GetStackRange(definition) ?? RangeFormatting.From(1, 1))}");
        AppendScaffoldLine(builder, indent + 1, $"weight: {FormatYamlFloat(definition.Weight ?? 1f)}");
        AppendScaffoldLine(builder, indent + 1, $"dontScale: {FormatYamlBool(definition.DontScale ?? false)}");
    }

    private static void AppendScaffoldDestructibleBlock(StringBuilder builder, DestructibleDefinition definition)
    {
        AppendScaffoldLine(builder, 1, "destructible:");
        AppendScaffoldLine(builder, 2, $"health: {FormatYamlFloat(definition.Health ?? 1f)}");
        AppendScaffoldLine(builder, 2, $"minToolTier: {definition.MinToolTier.GetValueOrDefault()}");
        AppendScaffoldLine(builder, 2, $"destructibleType: {FormatYamlString(definition.DestructibleType ?? DestructibleType.Default.ToString())}");
        AppendScaffoldStringLine(builder, 2, "spawnWhenDestroyed", definition.SpawnWhenDestroyed);
    }

    private static void AppendScaffoldPickableBlock(StringBuilder builder, PickableDefinition definition)
    {
        AppendScaffoldLine(builder, 1, "pickable:");
        AppendScaffoldStringLine(builder, 2, "overrideName", definition.OverrideName);
        AppendScaffoldLine(builder, 2, "drop:");
        AppendScaffoldStringLine(builder, 3, "item", definition.Drop?.Item);
        AppendScaffoldLine(builder, 3, $"amount: {definition.Drop?.Amount ?? 1}");
        AppendScaffoldLine(builder, 3, $"minAmountScaled: {definition.Drop?.MinAmountScaled ?? 0} # Minimum final amount after Game.ScaleDrops; ignored when dontScale is true.");
        AppendScaffoldLine(builder, 3, $"dontScale: {FormatYamlBool(definition.Drop?.DontScale ?? false)}");
        AppendScaffoldDropTablePayloadBlock(builder, 2, "extraDrops", definition.ExtraDrops ?? new DropTablePayloadDefinition());
    }

    private static void AppendScaffoldPickableItemBlock(StringBuilder builder, PickableItemDefinition definition)
    {
        AppendScaffoldLine(builder, 1, "pickableItem:");
        if (definition.RandomDrops != null && definition.RandomDrops.Count > 0)
        {
            AppendScaffoldLine(builder, 2, "randomDrops:");
            foreach (RandomPickableItemDefinition randomItem in definition.RandomDrops)
            {
                AppendScaffoldListEntryLine(builder, 3, "item", randomItem.Item);
                AppendScaffoldLine(builder, 4, $"stack: {RangeFormatting.FormatInlineObject(GetStackRange(randomItem) ?? RangeFormatting.From(1, 1))}");
                if (randomItem.Weight.HasValue)
                {
                    AppendScaffoldLine(builder, 4, $"weight: {FormatYamlFloat(randomItem.Weight.Value)}");
                }
            }
        }
        else
        {
            AppendScaffoldLine(builder, 2, "randomDrops: []");
        }

        if (definition.Drop != null)
        {
            AppendScaffoldLine(builder, 2, "drop:");
            AppendScaffoldStringLine(builder, 3, "item", definition.Drop.Item);
            AppendScaffoldLine(builder, 3, $"stack: {definition.Drop.Stack ?? 1}");
        }
        else
        {
            AppendScaffoldLine(builder, 2, "drop:");
            AppendScaffoldLine(builder, 3, "item: null");
            AppendScaffoldLine(builder, 3, "stack: 1");
        }
    }

    private static void AppendScaffoldFishBlock(StringBuilder builder, FishDefinition definition)
    {
        AppendScaffoldLine(builder, 1, "fish:");
        AppendScaffoldDropTablePayloadBlock(builder, 2, "extraDrops", definition.ExtraDrops ?? new DropTablePayloadDefinition());
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
        AppendScaffoldLine(builder, indent + 1, "altitude: null");
        AppendScaffoldLine(builder, indent + 1, "distanceFromCenter: null");
        AppendScaffoldLine(builder, indent + 1, "biomes: []");
        AppendScaffoldLine(builder, indent + 1, "locations: []");
        AppendScaffoldLine(builder, indent + 1, "timeOfDay: null");
        AppendScaffoldLine(builder, indent + 1, "requiredEnvironments: []");
        AppendScaffoldLine(builder, indent + 1, "requiredGlobalKeys: []");
        AppendScaffoldLine(builder, indent + 1, "forbiddenGlobalKeys: []");
        AppendScaffoldLine(builder, indent + 1, "inForest: null");
        AppendScaffoldLine(builder, indent + 1, "inDungeon: null");
        AppendScaffoldLine(builder, indent + 1, "insidePlayerBase: null");
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

    private static void AppendScaffoldListEntryLine(StringBuilder builder, int indent, string key, string? value)
    {
        if (value == null)
        {
            AppendScaffoldLine(builder, indent, $"- {key}: null");
            return;
        }

        AppendScaffoldLine(builder, indent, $"- {key}: {FormatYamlString(value)}");
    }
}
