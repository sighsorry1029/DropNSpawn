using System;
using System.Collections.Generic;
using System.Text;

namespace DropNSpawn;

internal static partial class CharacterDropManager
{
    private static string BuildFullScaffoldConfigurationTemplate()
    {
        StringBuilder builder = new();
        bool wroteAny = false;

        foreach (PrefabOwnerSection<CharacterDropPrefabEntry> section in BuildConfigurationTemplate())
        {
            foreach (CharacterDropPrefabEntry entry in section.Entries)
            {
                if (wroteAny)
                {
                    AppendScaffoldBlankLine(builder);
                }

                AppendScaffoldCharacterEntry(builder, entry);
                wroteAny = true;
            }
        }

        return wroteAny ? builder.ToString() : "[]" + Environment.NewLine;
    }

    private static void AppendScaffoldCharacterEntry(StringBuilder builder, CharacterDropPrefabEntry entry)
    {
        AppendScaffoldListEntryLine(builder, 0, "prefab", entry.Prefab);
        AppendScaffoldLine(builder, 1, $"enabled: {FormatYamlBool(entry.Enabled)}");
        AppendScaffoldConditionsBlock(builder, 1);
        AppendScaffoldLine(builder, 1, "characterDrop:");

        List<CharacterDropEntryDefinition> drops = entry.CharacterDrop?.Drops ?? new List<CharacterDropEntryDefinition>();
        if (drops.Count > 0)
        {
            AppendScaffoldLine(builder, 2, "drops:");
            foreach (CharacterDropEntryDefinition drop in drops)
            {
                AppendScaffoldCharacterDropEntry(builder, 3, drop);
            }
        }
        else
        {
            AppendScaffoldLine(builder, 2, "drops: []");
        }
    }

    private static void AppendScaffoldCharacterDropEntry(StringBuilder builder, int indent, CharacterDropEntryDefinition definition)
    {
        AppendScaffoldListEntryLine(builder, indent, "item", definition.Item);
        AppendScaffoldLine(builder, indent + 1, $"amount: {RangeFormatting.FormatInlineObject(GetAmountRange(definition) ?? RangeFormatting.From(1, 1))}");
        AppendScaffoldLine(builder, indent + 1, $"chance: {FormatYamlFloat(definition.Chance ?? 1f)}");
        AppendScaffoldLine(builder, indent + 1, $"dontScale: {FormatYamlBool(definition.DontScale ?? false)}");
        AppendScaffoldLine(builder, indent + 1, $"levelMultiplier: {FormatYamlBool(definition.LevelMultiplier ?? true)}");
        AppendScaffoldLine(builder, indent + 1, $"onePerPlayer: {FormatYamlBool(definition.OnePerPlayer ?? false)}");
        AppendScaffoldNullableIntLine(builder, indent + 1, "amountLimit", definition.AmountLimit);
        AppendScaffoldLine(builder, indent + 1, $"dropInStack: {FormatYamlBool(definition.DropInStack ?? false)}");
    }

    private static void AppendScaffoldComment(StringBuilder builder, string text)
    {
        builder.Append("# ");
        builder.AppendLine(text);
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
        AppendScaffoldLine(builder, indent + 1, "level: null");
        AppendScaffoldLine(builder, indent + 1, "altitude: null");
        AppendScaffoldLine(builder, indent + 1, "distanceFromCenter: null");
        AppendScaffoldLine(builder, indent + 1, "biomes: []");
        AppendScaffoldLine(builder, indent + 1, "locations: []");
        AppendScaffoldLine(builder, indent + 1, "timeOfDay: null");
        AppendScaffoldLine(builder, indent + 1, "requiredEnvironments: []");
        AppendScaffoldLine(builder, indent + 1, "requiredGlobalKeys: []");
        AppendScaffoldLine(builder, indent + 1, "forbiddenGlobalKeys: []");
        AppendScaffoldLine(builder, indent + 1, "states: []");
        AppendScaffoldLine(builder, indent + 1, "factions: []");
        AppendScaffoldLine(builder, indent + 1, "inForest: null");
        AppendScaffoldLine(builder, indent + 1, "inDungeon: null");
        AppendScaffoldLine(builder, indent + 1, "insidePlayerBase: null");
    }

    private static void AppendScaffoldNullableIntLine(StringBuilder builder, int indent, string key, int? value)
    {
        if (value.HasValue)
        {
            AppendScaffoldLine(builder, indent, $"{key}: {value.Value}");
            return;
        }

        AppendScaffoldLine(builder, indent, $"{key}: null");
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
