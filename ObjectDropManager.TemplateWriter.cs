using System;
using System.Text;
using static DropNSpawn.CommentedYamlTemplateSupport;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static string BuildPrimaryOverrideConfigurationTemplate()
    {
        StringBuilder builder = new();

        AppendTemplateComment(builder, $"Any file named {PluginSettingsFacade.GetYamlDomainSupplementalPrefix("object")}*.yml or {PluginSettingsFacade.GetYamlDomainSupplementalPrefix("object")}*.yaml is also loaded # ex) {PluginSettingsFacade.GetYamlDomainFilePrefix("object")}_rand1.yml, {PluginSettingsFacade.GetYamlDomainFilePrefix("object")}_rand2.yaml");
        AppendTemplateComment(builder, $"Use {PluginSettingsFacade.GetYamlDomainFilePrefix("object")}.reference.yml to look up real prefab names and reference values, {PluginSettingsFacade.GetYamlDomainFilePrefix("object")}.locations.reference.yml to see which location roots include a given object prefab, and run `dns:full object` for exhaustive field examples");
        AppendTemplateComment(builder, "Matching drop-table component blocks merge together. If any block matches, vanilla rows for that component are replaced by the union of matching custom rows.");
        AppendTemplateComment(builder, "A conditionless custom entry becomes the custom default for every populated component in that entry.");
        AppendTemplateComment(builder, "Piece-based prefabs are world-only. Player-built instances are not overridden.");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "dropOnDestroyed");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: goblin_totempole");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: # If these conditions fail, this entry is ignored # Vanilla rows are used only when no custom drop-table entry for that component matches");
        AppendTemplateLine(builder, 2, "altitude: null # ex) -1000~1000 # Range in world-height meters");
        AppendTemplateLine(builder, 2, "distanceFromCenter: null # ex) 0~10000 # Range in meters from the world center");
        AppendTemplateLine(builder, 2, "biomes: [] # ex) [BlackForest, Mistlands] # Allowed biomes # EWD custom biome names also work when EWD is installed");
        AppendTemplateLine(builder, 2, "locations: [] # ex) [Hildir_camp] # Allowed location prefab names");
        AppendTemplateLine(builder, 2, "timeOfDay: null # ex) [night] # [day, afternoon, night] # day contains afternoon");
        AppendTemplateLine(builder, 2, "requiredEnvironments: [] # ex) [Rain] # Allowed environment names");
        AppendTemplateLine(builder, 2, "requiredGlobalKeys: [] # ex) [defeated_gdking] # Required global keys");
        AppendTemplateLine(builder, 2, "forbiddenGlobalKeys: [] # ex) [nomap] # Forbidden global keys");
        AppendTemplateLine(builder, 2, "inForest: null # ex) true = forest only # false = outside forest only # null or no field allows both");
        AppendTemplateLine(builder, 2, "inDungeon: null # ex) true = dungeon only # false = overworld only # null or no field allows both");
        AppendTemplateLine(builder, 2, "insidePlayerBase: null # ex) true = near player base only # false = away from player base only # null or no field allows both");
        AppendTemplateLine(builder, 1, "dropOnDestroyed:");
        AppendTemplateLine(builder, 2, "rolls: 1~1 # ex) 1~3 # Range of successful rolls from this table");
        AppendTemplateLine(builder, 2, "dropChance: 1 # Chance from 0 to 1 that this table rolls at all");
        AppendTemplateLine(builder, 2, "oneOfEach: false # True lets each entry roll at most once per table roll");
        AppendTemplateLine(builder, 2, "drops: # Set drops: [] to disable a drop table");
        AppendTemplateLine(builder, 2, "- item: null # ex) Wood # Required item prefab name");
        AppendTemplateLine(builder, 3, "stack: 1~1 # ex) 1~2 # Range of stack size");
        AppendTemplateLine(builder, 3, "weight: 1 # Relative weight versus other entries in the same table");
        AppendTemplateLine(builder, 3, "dontScale: false # True skips the game's built-in drop scaling for this entry");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "mineRock, mineRock5, treeBase, and treeLog");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: MineRock_Copper");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: {}");
        AppendTemplateLine(builder, 1, "mineRock:");
        AppendTemplateLine(builder, 2, "health: null # ex) 1000");
        AppendTemplateLine(builder, 2, "minToolTier: 0 # ex) tier 0 AxeStone/PickaxeAntler, tier 1 AxeFlint/PickaxeBronze, tier 2 AxeBronze/PickaxeIron, tier 3 PickaxeBlackMetal, tier 4 AxeBlackMetal/AxeJotunBane, tier 5 BatteringRam, tier 6 AxeBerzerkr");
        AppendTemplateLine(builder, 2, "rolls: 1~1");
        AppendTemplateLine(builder, 2, "dropChance: 1");
        AppendTemplateLine(builder, 2, "oneOfEach: false");
        AppendTemplateLine(builder, 2, "drops:");
        AppendTemplateLine(builder, 2, "- item: null # ex) Stone");
        AppendTemplateLine(builder, 3, "stack: 1~1 # ex) 1~3 # Range of stack size");
        AppendTemplateLine(builder, 3, "weight: 1");
        AppendTemplateLine(builder, 3, "dontScale: false");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "container");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: TreasureChest_meadows");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: {}");
        AppendTemplateLine(builder, 1, "container:");
        AppendTemplateLine(builder, 2, "rolls: 1~1");
        AppendTemplateLine(builder, 2, "dropChance: 1");
        AppendTemplateLine(builder, 2, "oneOfEach: false");
        AppendTemplateLine(builder, 2, "drops:");
        AppendTemplateLine(builder, 2, "- item: null # ex) Coins # Required item prefab name");
        AppendTemplateLine(builder, 3, "stack: 1~1 # ex) 10~20 # Range of stack size");
        AppendTemplateLine(builder, 3, "weight: 1");
        AppendTemplateLine(builder, 3, "dontScale: false");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "pickableItem # Use either randomDrops or drop # If both are set, randomDrops takes precedence");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: Pickable_DolmenTreasure");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: {}");
        AppendTemplateLine(builder, 1, "pickableItem:");
        AppendTemplateLine(builder, 2, "randomDrops:");
        AppendTemplateLine(builder, 2, "- item: null # ex) Coins");
        AppendTemplateLine(builder, 3, "stack: 1~1 # ex) 1~3 # Range of stack size");
        AppendTemplateLine(builder, 3, "weight: 1");
        AppendTemplateLine(builder, 2, "drop:");
        AppendTemplateLine(builder, 3, "item: null # ex) Coins");
        AppendTemplateLine(builder, 3, "stack: 1");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "pickable");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: BlueberryBush");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: {}");
        AppendTemplateLine(builder, 1, "pickable:");
        AppendTemplateLine(builder, 2, "overrideName: null # Optional display name override");
        AppendTemplateLine(builder, 2, "drop:");
        AppendTemplateLine(builder, 3, "item: null # ex) Blueberries");
        AppendTemplateLine(builder, 3, "amount: 1");
        AppendTemplateLine(builder, 3, "minAmountScaled: 0 # Minimum final amount after Game.ScaleDrops # ignored when dontScale is true");
        AppendTemplateLine(builder, 3, "dontScale: false");
        AppendTemplateLine(builder, 2, "extraDrops:");
        AppendTemplateLine(builder, 3, "rolls: 1~1");
        AppendTemplateLine(builder, 3, "dropChance: 1");
        AppendTemplateLine(builder, 3, "oneOfEach: false");
        AppendTemplateLine(builder, 3, "drops:");
        AppendTemplateLine(builder, 3, "- item: null # ex) Wood");
        AppendTemplateLine(builder, 4, "stack: 1~1 # ex) 1~3 # Range of stack size");
        AppendTemplateLine(builder, 4, "weight: 1");
        AppendTemplateLine(builder, 4, "dontScale: false");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "fish");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: Fish2");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: {}");
        AppendTemplateLine(builder, 1, "fish:");
        AppendTemplateLine(builder, 2, "extraDrops:");
        AppendTemplateLine(builder, 3, "rolls: 1~1");
        AppendTemplateLine(builder, 3, "dropChance: 1");
        AppendTemplateLine(builder, 3, "oneOfEach: false");
        AppendTemplateLine(builder, 3, "drops:");
        AppendTemplateLine(builder, 3, "- item: null # ex) Amber");
        AppendTemplateLine(builder, 4, "stack: 1~1 # ex) 1~3 # Range of stack size");
        AppendTemplateLine(builder, 4, "weight: 1");
        AppendTemplateLine(builder, 4, "dontScale: false");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "destructible");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: CloudberryBush");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: {}");
        AppendTemplateLine(builder, 1, "destructible:");
        AppendTemplateLine(builder, 2, "health: null # ex) 80");
        AppendTemplateLine(builder, 2, "minToolTier: 0");
        AppendTemplateLine(builder, 2, "destructibleType: Default # Values: None, Default, Tree, Character, Everything");
        AppendTemplateLine(builder, 2, "spawnWhenDestroyed: null # ex) Cloudberry # Optional direct spawn prefab");
        AppendTemplateBlankLine(builder);

        return builder.ToString();
    }

    private static void AppendObjectTemplateEntry(StringBuilder builder, PrefabConfigurationEntry entry)
    {
        AppendTemplateComment(builder, $"----- {entry.Prefab} -----");
        AppendTemplateLine(builder, 0, $"- prefab: {entry.Prefab}");
        AppendTemplateLine(builder, 1, $"enabled: {FormatYamlBool(entry.Enabled)}");
        AppendOptionalSharedConditions(builder, 1);

        if (entry.DropOnDestroyed != null)
        {
            AppendDropTableTemplateBlock(builder, 1, "dropOnDestroyed", entry.DropOnDestroyed);
        }

        if (entry.MineRock != null)
        {
            AppendDamageableDropTableTemplateBlock(builder, 1, "mineRock", entry.MineRock);
        }

        if (entry.MineRock5 != null)
        {
            AppendDamageableDropTableTemplateBlock(builder, 1, "mineRock5", entry.MineRock5);
        }

        if (entry.TreeBase != null)
        {
            AppendDamageableDropTableTemplateBlock(builder, 1, "treeBase", entry.TreeBase);
        }

        if (entry.TreeLog != null)
        {
            AppendDamageableDropTableTemplateBlock(builder, 1, "treeLog", entry.TreeLog);
        }

        if (entry.Container != null)
        {
            AppendDropTableTemplateBlock(builder, 1, "container", entry.Container);
        }

        if (entry.PickableItem != null)
        {
            AppendPickableItemTemplateBlock(builder, entry.PickableItem);
        }

        if (entry.Pickable != null)
        {
            AppendPickableTemplateBlock(builder, entry.Pickable);
        }

        if (entry.Fish != null)
        {
            AppendFishTemplateBlock(builder, entry.Fish);
        }

        if (entry.Destructible != null)
        {
            AppendDestructibleTemplateBlock(builder, entry.Destructible);
        }

        AppendTemplateBlankLine(builder);
    }

    private static void AppendDamageableDropTableTemplateBlock(StringBuilder builder, int indent, string blockName, DamageableDropTableDefinition definition)
    {
        AppendTemplateLine(builder, indent, $"{blockName}:");
        AppendTemplateLine(builder, indent + 1, $"health: {FormatYamlFloat(definition.Health ?? 1f)}");
        AppendTemplateLine(builder, indent + 1, $"minToolTier: {definition.MinToolTier.GetValueOrDefault()}");
        AppendDropTableValueLines(builder, indent + 1, definition);
    }

    private static void AppendDropTableTemplateBlock(StringBuilder builder, int indent, string blockName, DropTableDefinition definition)
    {
        AppendTemplateLine(builder, indent, $"{blockName}:");
        AppendDropTableValueLines(builder, indent + 1, definition);
    }

    private static void AppendDropTableValueLines(StringBuilder builder, int indent, DropTablePayloadDefinition definition)
    {
        AppendTemplateLine(builder, indent, $"rolls: {RangeFormatting.FormatShorthand(GetRollsRange(definition) ?? RangeFormatting.From(1, 1))}");
        AppendTemplateLine(builder, indent, $"dropChance: {FormatYamlFloat(definition.DropChance ?? 1f)}");
        AppendTemplateLine(builder, indent, $"oneOfEach: {FormatYamlBool(definition.OneOfEach ?? false)}");
        AppendTemplateLine(builder, indent, "drops:");

        if (definition.Drops != null && definition.Drops.Count > 0)
        {
            foreach (DropEntryDefinition drop in definition.Drops)
            {
                AppendDropEntryTemplate(builder, indent, drop);
            }
        }
        else
        {
            AppendOptionalDropEntryTemplate(builder, indent);
        }
    }

    private static void AppendDropEntryTemplate(StringBuilder builder, int indent, DropEntryDefinition definition)
    {
        AppendTemplateLine(builder, indent, $"- item: {definition.Item}");
        AppendTemplateLine(builder, indent + 1, $"stack: {RangeFormatting.FormatShorthand(GetStackRange(definition) ?? RangeFormatting.From(1, 1))}");
        AppendTemplateLine(builder, indent + 1, $"weight: {FormatYamlFloat(definition.Weight ?? 1f)}");
        AppendTemplateLine(builder, indent + 1, $"dontScale: {FormatYamlBool(definition.DontScale ?? false)}");
    }

    private static void AppendOptionalDropEntryTemplate(StringBuilder builder, int indent)
    {
        AppendTemplateNestedLine(builder, indent, "- item: Wood");
        AppendTemplateNestedLine(builder, indent + 1, "stack: 1~3");
        AppendTemplateNestedLine(builder, indent + 1, "weight: 1");
        AppendTemplateNestedLine(builder, indent + 1, "dontScale: false");
    }

    private static void AppendDestructibleTemplateBlock(StringBuilder builder, DestructibleDefinition definition)
    {
        AppendTemplateLine(builder, 1, "destructible:");
        AppendTemplateLine(builder, 2, $"health: {FormatYamlFloat(definition.Health ?? 1f)}");
        AppendTemplateLine(builder, 2, $"minToolTier: {definition.MinToolTier.GetValueOrDefault()}");
        AppendTemplateLine(builder, 2, $"destructibleType: {definition.DestructibleType ?? DestructibleType.Default.ToString()}");
        if (!string.IsNullOrWhiteSpace(definition.SpawnWhenDestroyed))
        {
            AppendTemplateLine(builder, 2, $"spawnWhenDestroyed: {definition.SpawnWhenDestroyed}");
        }
        else
        {
            AppendTemplateNestedLine(builder, 2, "spawnWhenDestroyed: Cloudberry");
        }
    }

    private static void AppendPickableTemplateBlock(StringBuilder builder, PickableDefinition definition)
    {
        AppendTemplateLine(builder, 1, "pickable:");
        if (!string.IsNullOrWhiteSpace(definition.OverrideName))
        {
            AppendTemplateLine(builder, 2, $"overrideName: {definition.OverrideName}");
        }
        else
        {
            AppendTemplateNestedLine(builder, 2, "overrideName: Custom display name");
        }

        AppendTemplateLine(builder, 2, "drop:");
        AppendTemplateLine(builder, 3, $"item: {definition.Drop?.Item ?? "Blueberries"}");
        AppendTemplateLine(builder, 3, $"amount: {definition.Drop?.Amount.GetValueOrDefault(1) ?? 1}");
        AppendTemplateLine(builder, 3, $"minAmountScaled: {definition.Drop?.MinAmountScaled.GetValueOrDefault() ?? 0} # Minimum final amount after Game.ScaleDrops; ignored when dontScale is true.");
        AppendTemplateLine(builder, 3, $"dontScale: {FormatYamlBool(definition.Drop?.DontScale ?? false)}");

        AppendTemplateNestedLine(builder, 2, "extraDrops:");
        AppendTemplateNestedLine(builder, 3, "rolls: 1~3");
        AppendTemplateNestedLine(builder, 3, "dropChance: 1");
        AppendTemplateNestedLine(builder, 3, "oneOfEach: false");
        AppendTemplateNestedLine(builder, 3, "drops:");
        AppendOptionalDropEntryTemplate(builder, 3);
    }

    private static void AppendPickableItemTemplateBlock(StringBuilder builder, PickableItemDefinition definition)
    {
        AppendTemplateLine(builder, 1, "pickableItem:");
        AppendTemplateLine(builder, 2, "randomDrops:");

        if (definition.RandomDrops != null && definition.RandomDrops.Count > 0)
        {
            foreach (RandomPickableItemDefinition randomItem in definition.RandomDrops)
            {
                AppendTemplateLine(builder, 2, $"- item: {randomItem.Item}");
                AppendTemplateLine(builder, 3, $"stack: {RangeFormatting.FormatShorthand(GetStackRange(randomItem) ?? RangeFormatting.From(1, 1))}");
                if (randomItem.Weight.HasValue)
                {
                    AppendTemplateLine(builder, 3, $"weight: {FormatYamlFloat(randomItem.Weight.Value)}");
                }
            }
        }
        else
        {
            AppendTemplateNestedLine(builder, 2, "- item: Coins");
            AppendTemplateNestedLine(builder, 3, "stack: 1~3");
            AppendTemplateNestedLine(builder, 3, "weight: 1");
        }

        AppendTemplateLine(builder, 2, "drop:");
        AppendTemplateLine(builder, 3, $"item: {definition.Drop?.Item ?? "Coins"}");
        AppendTemplateLine(builder, 3, $"stack: {definition.Drop?.Stack ?? 1}");
    }

    private static void AppendFishTemplateBlock(StringBuilder builder, FishDefinition definition)
    {
        AppendTemplateLine(builder, 1, "fish:");
        AppendTemplateNestedLine(builder, 2, "extraDrops:");
        AppendTemplateNestedLine(builder, 3, "rolls: 1~3");
        AppendTemplateNestedLine(builder, 3, "dropChance: 1");
        AppendTemplateNestedLine(builder, 3, "oneOfEach: false");
        AppendTemplateNestedLine(builder, 3, "drops:");
        AppendOptionalDropEntryTemplate(builder, 3);
    }

    private static void AppendOptionalSharedConditions(StringBuilder builder, int indent, bool nested = false)
    {
        AppendConditionTemplateLine(builder, indent, "conditions:", nested);
        AppendConditionTemplateLine(builder, indent + 1, "altitude: -1000~1000", nested);
        AppendConditionTemplateLine(builder, indent + 1, "distanceFromCenter: 0~10000", nested);
        AppendConditionTemplateLine(builder, indent + 1, "biomes: [BlackForest, Mistlands]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "locations: [Hildir_camp]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "timeOfDay: [night]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "requiredEnvironments: [Rain]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "requiredGlobalKeys: [defeated_gdking]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "forbiddenGlobalKeys: [nomap]", nested);
        AppendConditionTemplateLine(builder, indent + 1, "inForest: true", nested);
        AppendConditionTemplateLine(builder, indent + 1, "inDungeon: false", nested);
        AppendConditionTemplateLine(builder, indent + 1, "insidePlayerBase: false", nested);
    }

    private static void AppendConditionTemplateLine(StringBuilder builder, int indent, string text, bool nested)
    {
        AppendTemplateNestedLine(builder, indent, text);
    }
}
