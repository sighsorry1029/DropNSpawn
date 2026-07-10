using System.Text;
using static DropNSpawn.CommentedYamlTemplateSupport;

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
        AppendTemplateLine(builder, 2, "timeOfDay: null # ex) [day, night]");
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
        AppendTemplateLine(builder, 2, "- item: null # ex) Resin # Drop prefab; CharacterDrop can also spawn non-item prefabs such as monsters");
        AppendTemplateLine(builder, 3, "amount: 1~1 # ex) 1~3 # Range of item amount");
        AppendTemplateLine(builder, 3, "chance: 1 # Chance from 0 to 1 for this item on each roll");
        AppendTemplateLine(builder, 3, "dontScale: false # True skips the game's built-in drop scaling for the base amount roll");
        AppendTemplateLine(builder, 3, "levelMultiplier: true # Omitted default: true for ItemDrop prefabs, false for non-item prefabs # Trophy items can be forced true in config");
        AppendTemplateLine(builder, 3, "onePerPlayer: false # True uses nearby player count as the final amount # Configure check range in config");
        AppendTemplateLine(builder, 3, "amountLimit: null # ex) 2 # Integer cap on the final amount");
        AppendTemplateLine(builder, 3, "dropInStack: false # True spawns one stacked drop instead of many singles");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: Eikthyr");
        AppendTemplateLine(builder, 1, "characterDrop:");
        AppendTemplateLine(builder, 2, "drops:");
        AppendTemplateLine(builder, 2, "- item: TrophyEikthyr");
        AppendTemplateLine(builder, 3, "dontScale: true");
        AppendTemplateLine(builder, 3, "levelMultiplier: false");
        AppendTemplateLine(builder, 2, "- item: HardAntler");
        AppendTemplateLine(builder, 3, "amount: 3");
        AppendTemplateLine(builder, 3, "dontScale: true");
        AppendTemplateLine(builder, 3, "levelMultiplier: false");
        AppendTemplateBlankLine(builder);

        return builder.ToString();
    }
}
