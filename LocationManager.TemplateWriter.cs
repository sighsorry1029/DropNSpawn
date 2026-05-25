using System.Text;
using static DropNSpawn.CommentedYamlTemplateSupport;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private static string BuildPrimaryOverrideConfigurationTemplate()
    {
        StringBuilder builder = new();
        AppendTemplateComment(builder, $"Any file named {PluginSettingsFacade.GetYamlDomainSupplementalPrefix("location")}*.yml or {PluginSettingsFacade.GetYamlDomainSupplementalPrefix("location")}*.yaml is also loaded.");
        AppendTemplateComment(builder, $"Use {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml to look up real location prefab names and run dns:full location to regenerate {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.full.yml.");
        AppendTemplateComment(builder, $"itemStands uses a YAML list. Omit path to apply one row to all relevant item stands for the location, or copy an exact itemStands.path from {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml to target one stand.");
        AppendTemplateComment(builder, $"Vegvisir overrides are path-targeted by default. Copy the exact vegvisirs.path value from {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml when a location has multiple Vegvisirs. Omit path only when exactly one live Vegvisir matches.");
        AppendTemplateComment(builder, $"RuneStone overrides are path-targeted by default. Copy the exact runestones.path value from {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml when a location has multiple RuneStones. Omit path only when exactly one live RuneStone matches.");
        AppendTemplateComment(builder, "Expand World clone aliases are matched exactly # ex) prefab: \"Dragonqueen:clone\" # quotes are required because ':' must stay inside one YAML string");
        AppendTemplateComment(builder, "offeringBowl data/fields/objects require Expand World Data and apply only when bossPrefab spawns a character # objects spawn at the resolved boss spawn point");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "offeringBowl");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: Bonemass");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: # Static location filters only. Dynamic fields like timeOfDay/environments/global keys are ignored here");
        AppendTemplateLine(builder, 2, "biomes: [] # ex) [Meadows, BlackForest] # Allowed biomes # EWD custom biome names also work when EWD is installed");
        AppendTemplateLine(builder, 2, "altitude: null # ex) -1000~1000 # Range in world-height meters");
        AppendTemplateLine(builder, 2, "distanceFromCenter: null # ex) 0~10000 # Range in meters from the world center");
        AppendTemplateLine(builder, 2, "inDungeon: null # ex) true = dungeon only # false = overworld only # null or no field allows both");
        AppendTemplateLine(builder, 2, "inForest: null # ex) true = forest only # false = outside forest only # null or no field allows both");
        AppendTemplateLine(builder, 1, "offeringBowl:");
        AppendTemplateLine(builder, 2, "name: null # ex) '$piece_offerbowl' # Optional hover name");
        AppendTemplateLine(builder, 2, "useItemText: null # ex) '$piece_offerbowl_offeritem' # Optional interaction text");
        AppendTemplateLine(builder, 2, "usedAltarText: null # ex) '$msg_offerdone' # Optional completion text");
        AppendTemplateLine(builder, 2, "cantOfferText: null # ex) '$msg_cantoffer' # Optional failure text");
        AppendTemplateLine(builder, 2, "wrongOfferText: null # ex) '$msg_offerwrong' # Optional wrong-item text");
        AppendTemplateLine(builder, 2, "incompleteOfferText: null # ex) '$msg_incompleteoffering' # Optional incomplete-offering text");
        AppendTemplateLine(builder, 2, "bossItem: null # ex) WitheredBone # Required offering item prefab");
        AppendTemplateLine(builder, 2, "bossItems: null # ex) 10 # Number of bossItem items required for one valid offering");
        AppendTemplateLine(builder, 2, "bossPrefab: null # ex) Bonemass # Boss character prefab spawned after a valid offering");
        AppendTemplateLine(builder, 2, "itemPrefab: null # ex) Wishbone # Optional item reward prefab instead of spawning a boss");
        AppendTemplateLine(builder, 2, "setGlobalKey: null # ex) defeated_bonemass # Optional global key set after a valid offering");
        AppendTemplateLine(builder, 2, "renderSpawnAreaGizmos: false # True draws the boss spawn search area while the altar is selected");
        AppendTemplateLine(builder, 2, "alertOnSpawn: false # True calls BaseAI.Alert() on the spawned boss");
        AppendTemplateLine(builder, 2, "spawnBossDelay: null # ex) 5 # Seconds to wait before spawning the boss");
        AppendTemplateLine(builder, 2, "spawnBossDistance: null # ex) 0~40 # Range in meters of horizontal spawn distance from the altar");
        AppendTemplateLine(builder, 2, "spawnBossMaxYDistance: null # ex) 9999 # Meters of vertical search distance when finding a spawn point");
        AppendTemplateLine(builder, 2, "getSolidHeightMargin: null # ex) 1000 # Meters of terrain raycast margin used by the altar's solid-height search");
        AppendTemplateLine(builder, 2, "enableSolidHeightCheck: true # True requires valid ground height before accepting a spawn point");
        AppendTemplateLine(builder, 2, "spawnPointClearingRadius: 0 # Meters cleared around the final spawn point before boss spawn");
        AppendTemplateLine(builder, 2, "spawnYOffset: 1 # Meters of vertical offset added to the chosen spawn position");
        AppendTemplateLine(builder, 2, "useItemStands: false # True switches the offering bowl to nearby item stands instead of direct UseItem offerings");
        AppendTemplateLine(builder, 2, "itemStandPrefix: null # ex) Boss # Optional object-name prefix used to select nearby item stands");
        AppendTemplateLine(builder, 2, "itemStandMaxRange: 20 # Meters of max scan distance for nearby item stands");
        AppendTemplateLine(builder, 2, "respawnMinutes: 0 # 0 disables cooldown # Minutes of altar cooldown");
        AppendTemplateLine(builder, 2, "data: null # Optional Expand World Data entry applied to the character spawned through bossPrefab");
        AppendTemplateLine(builder, 2, "fields: {} # ex) { Character.m_name: $enemy_bonemass, health: 5000 } # Expand World Data field overrides layered on top of data");
        AppendTemplateLine(builder, 2, "objects: [] # ex) [Wood,0,0,0,1] # Expand World Data object entries spawned at the resolved boss spawn point");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "itemStands");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: StartTemple");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: {} # Same static location-filter shape as the offeringBowl example above");
        AppendTemplateLine(builder, 1, "itemStands:");
        AppendTemplateLine(builder, 1, $"- path: null # ex) BossStone_Eikthyr[0] # Optional exact itemStands.path from {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml");
        AppendTemplateLine(builder, 2, "name: null # ex) '$piece_itemstand' # Optional hover name");
        AppendTemplateLine(builder, 2, "canBeRemoved: true # True allows players to remove the currently attached item");
        AppendTemplateLine(builder, 2, "autoAttach: false # True automatically attaches compatible dropped items");
        AppendTemplateLine(builder, 2, "orientationType: null # ex) Vertical # Optional ItemStand.Orientation name");
        AppendTemplateLine(builder, 2, "supportedTypes: [] # ex) [OneHandedWeapon, TwoHandedWeapon] # Allowed ItemDrop.ItemType names");
        AppendTemplateLine(builder, 2, "supportedItems: [] # ex) [TrophyDeer] # Explicitly allowed item prefabs");
        AppendTemplateLine(builder, 2, "unsupportedItems: [] # ex) [TrophyDeer] # Explicitly blocked item prefabs");
        AppendTemplateLine(builder, 2, "powerActivationDelay: null # ex) 2 # Seconds before guardianPower activates after use");
        AppendTemplateLine(builder, 2, "guardianPower: null # ex) GP_Eikthyr # StatusEffect prefab name granted when this stand is used");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "vegvisirs");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: SwampRuin1");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: {} # Same static location-filter shape as the offeringBowl example above");
        AppendTemplateLine(builder, 1, "vegvisirs:");
        AppendTemplateLine(builder, 1, $"- path: null # ex) Vegvisir_Bonemass (1)[0] # Optional exact vegvisirs.path from {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml");
        AppendTemplateLine(builder, 2, "expectedLocations: [] # ex) [Vendor_BlackForest] # Optional validation list # When path is omitted this must match exactly one live Vegvisir");
        AppendTemplateLine(builder, 2, "name: null # ex) '$piece_vegvisir' # Optional hover name");
        AppendTemplateLine(builder, 2, "useText: null # ex) '$piece_register_location' # Optional interaction text");
        AppendTemplateLine(builder, 2, "hoverName: null # ex) Pin # Optional secondary hover label");
        AppendTemplateLine(builder, 2, "setsGlobalKey: null # ex) defeated_gdking # Optional global key set after interaction");
        AppendTemplateLine(builder, 2, "setsPlayerKey: null # Optional per-player key set after interaction");
        AppendTemplateLine(builder, 2, "locations: # One or more location discoveries triggered by this vegvisir");
        AppendTemplateLine(builder, 2, "- locationName: null # ex) Vendor_BlackForest # Required location prefab name");
        AppendTemplateLine(builder, 3, "pinName: null # ex) Pin # Optional map pin label");
        AppendTemplateLine(builder, 3, "pinType: null # ex) Boss # Optional Minimap.PinType name");
        AppendTemplateLine(builder, 3, "discoverAll: false # True reveals every matching location instead of only the closest one");
        AppendTemplateLine(builder, 3, "showMap: true # True creates or updates a map pin for the discovered location");
        AppendTemplateLine(builder, 3, "weight: null # Optional weighted single-target pick when any vegvisir location defines weight");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "runestones");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: Runestone_Greydwarfs");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, "conditions: {} # Same static location-filter shape as the offeringBowl example above");
        AppendTemplateLine(builder, 1, "runestones:");
        AppendTemplateLine(builder, 1, $"- path: null # ex) RuneStone[0] # Optional exact runestones.path from {PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml");
        AppendTemplateLine(builder, 2, "expectedLocationName: null # Optional validation/disambiguation when path is omitted");
        AppendTemplateLine(builder, 2, "expectedLabel: null # Optional validation/disambiguation by current RuneStone label");
        AppendTemplateLine(builder, 2, "expectedTopic: null # Optional validation/disambiguation by current RuneStone topic");
        AppendTemplateLine(builder, 2, "name: null # ex) 'Rune stone' # Optional hover name");
        AppendTemplateLine(builder, 2, "topic: null # Optional TextViewer topic or localization key");
        AppendTemplateLine(builder, 2, "label: null # Optional known-text label key");
        AppendTemplateLine(builder, 2, "text: null # Optional TextViewer body or localization key");
        AppendTemplateLine(builder, 2, "randomTexts: null # [] clears random texts # list entries replace the whole random text list");
        AppendTemplateLine(builder, 2, "# - topic: null");
        AppendTemplateLine(builder, 2, "#   label: null");
        AppendTemplateLine(builder, 2, "#   text: null");
        AppendTemplateLine(builder, 2, "locationName: null # Optional location prefab discovered on interaction");
        AppendTemplateLine(builder, 2, "pinName: null # ex) Pin # Optional map pin label");
        AppendTemplateLine(builder, 2, "pinType: null # ex) Boss # Optional Minimap.PinType name");
        AppendTemplateLine(builder, 2, "showMap: null # True shows/creates the discovered location map pin");
        AppendTemplateLine(builder, 2, "chance: null # Optional 0..1 pin chance rolled once per loaded RuneStone instance");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "runestoneGlobalPins # Enable with BepInEx config: 1 - General / Enable Runestone Global Pins");
        AppendTemplateBlankLine(builder);
        AppendActiveTemplateLine(builder, 0, "- runestoneGlobalPins:");
        AppendActiveTemplateLine(builder, 2, "targetLocations:");
        AppendRunestoneGlobalPinTarget(builder, "Vendor_BlackForest", includeFieldComments: true);
        AppendRunestoneGlobalPinTarget(builder, "CombatRuin01");
        AppendRunestoneGlobalPinTarget(builder, "Hildir_camp");
        AppendRunestoneGlobalPinTarget(builder, "BogWitch_Camp");
        AppendRunestoneGlobalPinTarget(builder, "SunkenCrypt4");
        AppendRunestoneGlobalPinTarget(builder, "MountainCave02");
        AppendRunestoneGlobalPinTarget(builder, "StoneHenge1");
        AppendRunestoneGlobalPinTarget(builder, "StoneHenge3");
        AppendRunestoneGlobalPinTarget(builder, "StoneHenge4");
        AppendRunestoneGlobalPinTarget(builder, "StoneHenge5");
        AppendRunestoneGlobalPinTarget(builder, "Mistlands_DvergrTownEntrance1");
        AppendRunestoneGlobalPinTarget(builder, "Mistlands_DvergrTownEntrance2");
        AppendRunestoneGlobalPinTarget(builder, "Mistlands_Excavation1");
        AppendRunestoneGlobalPinTarget(builder, "Mistlands_Excavation2");
        AppendRunestoneGlobalPinTarget(builder, "Mistlands_Excavation3");
        AppendRunestoneGlobalPinTarget(builder, "PlaceofMystery1");
        AppendRunestoneGlobalPinTarget(builder, "PlaceofMystery2");
        AppendRunestoneGlobalPinTarget(builder, "PlaceofMystery3");
        AppendActiveTemplateBlankLine(builder);

        return builder.ToString();
    }

    private static void AppendRunestoneGlobalPinTarget(StringBuilder builder, string locationName, bool includeFieldComments = false)
    {
        AppendActiveTemplateLine(builder, 2, includeFieldComments
            ? $"- locationName: {locationName} # ZoneSystem location prefab name # Check expand_locations.yaml for locationNames"
            : $"- locationName: {locationName}");
        AppendActiveTemplateLine(builder, 3, includeFieldComments
            ? "chance: 0.5 # Final selection chance. Remaining chance means no pin; totals over 1 are normalized"
            : "chance: 0.5");
        if (includeFieldComments)
        {
            AppendActiveTemplateLine(builder, 3, "# normalized ex) three targets at 0.5 total 1.5, so each selected target has 0.5 / 1.5 = 33.3%");
        }

        AppendActiveTemplateLine(builder, 3, includeFieldComments
            ? "sourceBiomes: [] # Extra RuneStone source biomes allowed in addition to the target location's own biome"
            : "sourceBiomes: []");
        AppendActiveTemplateLine(builder, 3, includeFieldComments
            ? "pinName: null # Defaults to the target Location.m_discoverLabel, then locationName"
            : "pinName: null");
        AppendActiveTemplateLine(builder, 3, includeFieldComments
            ? "pinType: Icon3 # Options: Icon0, Icon1, Icon2, Icon3, Death, Bed, Icon4, Shout, None, Boss, Player, RandomEvent, Ping, EventArea, Hildir1, Hildir2, Hildir3"
            : "pinType: Icon3");
    }
}
