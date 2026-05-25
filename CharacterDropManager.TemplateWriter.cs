using System.Text;

namespace DropNSpawn;

internal static partial class CharacterDropManager
{
    private static string BuildPrimaryOverrideConfigurationTemplate()
    {
        StringBuilder builder = new();

        AppendTemplateComment(builder, $"Any file named {PluginSettingsFacade.GetYamlDomainSupplementalPrefix("character")}*.yml or {PluginSettingsFacade.GetYamlDomainSupplementalPrefix("character")}*.yaml is also loaded # ex) {PluginSettingsFacade.GetYamlDomainFilePrefix("character")}_rand1.yml, {PluginSettingsFacade.GetYamlDomainFilePrefix("character")}_rand2.yaml");
        AppendTemplateComment(builder, $"Use {PluginSettingsFacade.GetYamlDomainFilePrefix("character")}.reference.yml to look up real prefab names and reference values, and run dns:full character for exhaustive field examples");
        AppendTemplateBlankLine(builder);
        AppendTemplateComment(builder, "characterDrop");
        AppendTemplateBlankLine(builder);

        AppendTemplateLine(builder, 0, "- prefab: Greydwarf");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: # If these conditions fail, this custom entry is ignored and the original drops are used");
        AppendTemplateLine(builder, 2, "level: null # ex) 1~3");
        AppendTemplateLine(builder, 2, "altitude: null # ex) -1000~1000 # Range in world-height meters");
        AppendTemplateLine(builder, 2, "distanceFromCenter: null # ex) 0~10000 # Range in meters from the world center");
        AppendTemplateLine(builder, 2, "biomes: [] # ex) [BlackForest, Mistlands]");
        AppendTemplateLine(builder, 2, "locations: [] # ex) [Hildir_camp]");
        AppendTemplateLine(builder, 2, "timeOfDay: null # ex) [day, afternoon, night] # day contains afternoon");
        AppendTemplateLine(builder, 2, "requiredEnvironments: [] # ex) [Clear, Rain]");
        AppendTemplateLine(builder, 2, "requiredGlobalKeys: [] # ex) [defeated_eikthyr, defeated_gdking]");
        AppendTemplateLine(builder, 2, "forbiddenGlobalKeys: [] # ex) [nomap, defeated_bonemass]");
        AppendTemplateLine(builder, 2, "states: [] # ex) [Default, Tamed, Event]");
        AppendTemplateLine(builder, 2, "factions: [] # ex) [ForestMonsters, Demon]");
        AppendTemplateLine(builder, 2, "inForest: null # true = forest only # false = outside forest only # null or no field allows both");
        AppendTemplateLine(builder, 2, "inDungeon: null # true = dungeon only # false = overworld only # null or no field allows both");
        AppendTemplateLine(builder, 2, "insidePlayerBase: null # true = near player base only # false = away from player base only # null or no field allows both");
        AppendTemplateLine(builder, 1, "characterDrop:");
        AppendTemplateLine(builder, 2, "drops: # Set drops: [] to disable character drops for an entry");
        AppendTemplateLine(builder, 2, "- item: null # ex) Resin");
        AppendTemplateLine(builder, 3, "amount: 1~1 # ex) 1~3 # Range of item amount");
        AppendTemplateLine(builder, 3, "chance: 1 # Chance from 0 to 1 for this item on each roll");
        AppendTemplateLine(builder, 3, "dontScale: false # True skips the game's built-in drop scaling for the base amount roll");
        AppendTemplateLine(builder, 3, "levelMultiplier: true # True multiplies the calculated amount by character level when supported");
        AppendTemplateLine(builder, 3, "onePerPlayer: false # True uses nearby player count as the final amount # Configure check range in config");
        AppendTemplateLine(builder, 3, "amountLimit: null # ex) 2 # Integer cap on the final amount");
        AppendTemplateLine(builder, 3, "dropInStack: false # True spawns one stacked drop instead of many singles");
        AppendTemplateLine(builder, 1, "despawn: # Optional unconditional no-player despawn rule for this prefab # top-level conditions do not apply to despawn");
        AppendTemplateLine(builder, 2, "range: 64 # Optional override; falls back to Default Despawn Range config");
        AppendTemplateLine(builder, 2, "delay: 90 # Optional override; falls back to Default Despawn Delay Seconds config");
        AppendTemplateLine(builder, 2, "refunds: # Optional items dropped when this despawn rule removes the prefab");
        AppendTemplateLine(builder, 2, "- item: null # ex) TrophyDeer");
        AppendTemplateLine(builder, 3, "amount: 1 # ex) 2");
        AppendTemplateBlankLine(builder);
        AppendTemplateComment(builder, "bossTamedPressure # Enable with BepInEx config: 2 - Boss / Enable Boss Tamed Pressure");
        AppendActiveTemplateBlankLine(builder);
        AppendActiveTemplateLine(builder, 0, "- bossTamedPressure:");
        AppendActiveTemplateLine(builder, 2, "bossPrefabs: [] # Extra source boss prefabs added to the auto-detected boss set");
        AppendActiveTemplateLine(builder, 2, "excludedBossPrefabs: [] # Boss prefabs to ignore from auto-detected and bossPrefabs sources");
        AppendActiveTemplateLine(builder, 2, "targets:");
        AppendActiveTemplateLine(builder, 3, "range: 32 # Clamp: 0~128. Horizontal XZ range around each boss");
        AppendActiveTemplateLine(builder, 3, "scanInterval: 5 # Clamp: 0.25~30. Seconds between boss/tamed range scans");
        AppendActiveTemplateLine(builder, 3, "maxPerBoss: 4 # Clamp: 1~128. Maximum pressured targets per boss per scan");
        AppendActiveTemplateLine(builder, 3, "excludedTamedPrefabs: [] # Tamed MonsterAI prefabs excluded from the default pressured target set");
        AppendActiveTemplateLine(builder, 3, "extraPressuredPrefabs: [] # Character prefabs pressured even when not tamed");
        AppendActiveTemplateLine(builder, 2, "pressure:");
        AppendActiveTemplateLine(builder, 3, "damageInterval: 1 # Clamp: 0.25~30. Seconds between periodic damage ticks; each tick applies damagePercentPerSecond * damageInterval at once");
        AppendActiveTemplateLine(builder, 3, "damagePercentPerSecond: 0.01 # Clamp: 0~1. 0.01 = 1% of max health per second");
        AppendActiveTemplateLine(builder, 3, "damageMinBaseHealth: 100 # Clamp: 0~100000. Minimum max-health basis for periodic damage");
        AppendActiveTemplateLine(builder, 3, "incomingDamageMultiplier: 1.25 # Clamp: 0~10. Multiplies damage received while affected");
        AppendActiveTemplateLine(builder, 3, "outgoingDamageMultiplier: 0.75 # Clamp: 0~10. Multiplies damage dealt while affected");
        AppendActiveTemplateLine(builder, 2, "message: null # Defaults to \"Tamed creatures near a boss are weakened.\"; empty string disables messages");
        AppendActiveTemplateLine(builder, 2, "messageInterval: 5 # Clamp: 0~300. Per-player message cooldown in seconds");
        AppendActiveTemplateBlankLine(builder);

        return builder.ToString();
    }

    private static void AppendCharacterTemplateEntry(StringBuilder builder, CharacterDropPrefabEntry entry)
    {
        AppendTemplateComment(builder, $"----- {entry.Prefab} -----");
        AppendTemplateLine(builder, 0, $"- prefab: {entry.Prefab}");
        AppendTemplateLine(builder, 1, $"enabled: {FormatYamlBool(entry.Enabled)}");
        AppendOptionalCharacterConditions(builder, 1);
        AppendTemplateLine(builder, 1, "characterDrop:");
        AppendTemplateLine(builder, 2, "drops:");

        if (entry.CharacterDrop?.Drops != null && entry.CharacterDrop.Drops.Count > 0)
        {
            foreach (CharacterDropEntryDefinition drop in entry.CharacterDrop.Drops)
            {
                AppendCharacterDropEntryTemplate(builder, 2, drop);
            }
        }
        else
        {
            AppendOptionalCharacterDropEntryTemplate(builder, 2);
        }

        AppendTemplateNestedLine(builder, 1, "despawn:");
        AppendTemplateNestedLine(builder, 2, "range: 64");
        AppendTemplateNestedLine(builder, 2, "delay: 90");
        AppendTemplateNestedLine(builder, 2, "refunds:");
        AppendTemplateNestedLine(builder, 2, "- item: TrophyDeer");
        AppendTemplateNestedLine(builder, 3, "amount: 2");
        AppendTemplateBlankLine(builder);
    }

    private static void AppendCharacterDropEntryTemplate(StringBuilder builder, int indent, CharacterDropEntryDefinition definition)
    {
        AppendTemplateLine(builder, indent, $"- item: {definition.Item}");
        AppendTemplateLine(builder, indent + 1, $"amount: {RangeFormatting.FormatShorthand(GetAmountRange(definition) ?? RangeFormatting.From(1, 1))}");
        AppendTemplateLine(builder, indent + 1, $"chance: {FormatYamlFloat(definition.Chance ?? 1f)}");
        AppendTemplateLine(builder, indent + 1, $"dontScale: {FormatYamlBool(definition.DontScale ?? false)}");
        AppendTemplateLine(builder, indent + 1, $"levelMultiplier: {FormatYamlBool(definition.LevelMultiplier ?? true)}");
        AppendTemplateLine(builder, indent + 1, $"onePerPlayer: {FormatYamlBool(definition.OnePerPlayer ?? false)}");
        AppendTemplateNestedLine(builder, indent + 1, "amountLimit: 1");
        AppendTemplateNestedLine(builder, indent + 1, "dropInStack: true");
    }

    private static void AppendOptionalCharacterDropEntryTemplate(StringBuilder builder, int indent)
    {
        AppendTemplateNestedLine(builder, indent, "- item: Resin");
        AppendTemplateNestedLine(builder, indent + 1, "amount: 1~2");
        AppendTemplateNestedLine(builder, indent + 1, "chance: 1");
        AppendTemplateNestedLine(builder, indent + 1, "dontScale: false");
        AppendTemplateNestedLine(builder, indent + 1, "levelMultiplier: true");
        AppendTemplateNestedLine(builder, indent + 1, "onePerPlayer: false");
        AppendTemplateNestedLine(builder, indent + 1, "amountLimit: 1");
        AppendTemplateNestedLine(builder, indent + 1, "dropInStack: true");
    }

    private static void AppendOptionalCharacterConditions(StringBuilder builder, int indent, bool nested = false)
    {
        AppendConditionTemplateLine(builder, indent, "conditions:", nested);
        AppendConditionTemplateLine(builder, indent + 1, "level: 1~3", nested);
        AppendConditionTemplateLine(builder, indent + 1, "altitude: -1000~1000", nested);
        AppendConditionTemplateLine(builder, indent + 1, "distanceFromCenter: 0~10000", nested);
        AppendConditionTemplateLine(builder, indent + 1, "inForest: true", nested);
        AppendConditionTemplateLine(builder, indent + 1, "inDungeon: false", nested);
        AppendConditionTemplateLine(builder, indent + 1, "insidePlayerBase: false", nested);
        AppendConditionTemplateLine(builder, indent + 1, "biomes: [BlackForest, Mistlands]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "locations: [Hildir_camp]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "timeOfDay: [night]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "requiredEnvironments: [Rain]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "requiredGlobalKeys: [defeated_gdking]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "forbiddenGlobalKeys: [nomap]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "states: [Default, Event]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "factions: [ForestMonsters]", nested);
    }

    private static void AppendConditionTemplateLine(StringBuilder builder, int indent, string text, bool nested)
    {
        if (nested)
        {
            AppendTemplateNestedLine(builder, indent, text);
            return;
        }

        AppendTemplateNestedLine(builder, indent, text);
    }

    private static void AppendTemplateComment(StringBuilder builder, string text)
    {
        builder.Append("# ");
        builder.AppendLine(text);
    }

    private static void AppendTemplateLine(StringBuilder builder, int indent, string text)
    {
        builder.Append("# ");
        builder.Append(' ', indent * 2);
        builder.AppendLine(text);
    }

    private static void AppendTemplateNestedLine(StringBuilder builder, int indent, string text)
    {
        builder.Append("# ");
        builder.Append(' ', indent * 2);
        builder.Append("# ");
        builder.AppendLine(text);
    }

    private static void AppendActiveTemplateLine(StringBuilder builder, int indent, string text)
    {
        builder.Append(' ', indent * 2);
        builder.AppendLine(text);
    }

    private static void AppendActiveTemplateBlankLine(StringBuilder builder)
    {
        builder.AppendLine();
    }

    private static void AppendTemplateBlankLine(StringBuilder builder)
    {
        builder.AppendLine("#");
    }
}
