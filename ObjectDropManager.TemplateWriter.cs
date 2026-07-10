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
        AppendTemplateLine(builder, 2, "timeOfDay: null # ex) [night] # [day, night]");
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

}
