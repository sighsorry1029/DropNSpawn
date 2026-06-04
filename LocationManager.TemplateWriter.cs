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
        AppendTemplateComment(builder, "vegvisirGlobalEffects is a global Vegvisir reward table applied when a Vegvisir interaction succeeds. It does not change vanilla Vegvisir pins.");
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

        AppendTemplateComment(builder, "vegvisirGlobalEffects # Enable with BepInEx config: 1 - General / Enable Vegvisir Global Effects");
        AppendTemplateBlankLine(builder);
        AppendVegvisirGlobalEffectsDefaults(builder);

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

    private static void AppendVegvisirGlobalEffectsDefaults(StringBuilder builder)
    {
        AppendActiveTemplateLine(builder, 0, "- vegvisirGlobalEffects:");
        AppendActiveTemplateLine(builder, 1, "# Row: StatusEffect, durationSeconds, cooldownSeconds, weight, effectPrefab");
        AppendActiveTemplateLine(builder, 1, "# All is pooled with the current biome row; every other key must be one biome name.");
        AppendActiveTemplateLine(builder, 1, "# Each loaded Vegvisir locks one weighted pick until unload; unload/load resets its pick and cooldown state.");
        AppendActiveTemplateLine(builder, 1, "# Omitted duration/cooldown use StatusEffect prefab values; cooldown 0 = none; cooldown < 0 = once per loaded Vegvisir/player.");
        AppendActiveTemplateLine(builder, 1, "# DNS_ClearStatus ignores duration, clears current status effects, and shows \"You got bamboozled\".");
        AppendActiveTemplateLine(builder, 1, "# If the selected StatusEffect is already active, it is not reapplied and no cooldown/fx starts.");
        AppendActiveTemplateLine(builder, 1, "# effectPrefab is optional and must start with vfx_, sfx_, or fx_ case-insensitively.");
        AppendActiveTemplateLine(builder, 1, "- All:");
        AppendActiveTemplateLine(builder, 2, "- CorpseRun, 60, 120, 0.1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- SoftDeath, 120, 240, 0.5, vfx_HealthUpgrade");
        AppendActiveTemplateLine(builder, 2, "- DNS_ClearStatus, 0, 5, 0.1, sfx_goblin_idle");
        AppendActiveTemplateLine(builder, 1, "- Meadows:");
        AppendActiveTemplateLine(builder, 2, "- Rested, 240, 480, 1, vfx_HealthUpgrade");
        AppendActiveTemplateLine(builder, 2, "- BeltStrength, 120, 240, 1, vfx_HealthUpgrade");
        AppendActiveTemplateLine(builder, 2, "- AdrenalineRush, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "# - Lightning, 5, 10, 1000, fx_eikthyr_stomp");
        AppendActiveTemplateLine(builder, 1, "- BlackForest:");
        AppendActiveTemplateLine(builder, 2, "- GP_Eikthyr, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_health_minor");
        AppendActiveTemplateLine(builder, 2, "- Potion_stamina_minor");
        AppendActiveTemplateLine(builder, 2, "- Potion_tasty, 10, 20, 1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- SetEffect_BerserkerArmor, 120, 240, 1, vfx_HealthUpgrade");
        AppendActiveTemplateLine(builder, 2, "- SetEffect_TrollArmor, 120, 240, 1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- TrinketBronzeHealth, 60, 120, 1, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- TrinketBronzeStamina, 60, 120, 1, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- AdrenalineRush, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Wet, 60, -1, 0.5, sfx_gdking_scream");
        AppendActiveTemplateLine(builder, 1, "- Swamp:");
        AppendActiveTemplateLine(builder, 2, "- GP_TheElder, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_hasty, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_strength, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_swimmer, 240, 360, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_TrollPheromones, 240, 360, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_poisonresist, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_health_medium");
        AppendActiveTemplateLine(builder, 2, "- Potion_stamina_minor");
        AppendActiveTemplateLine(builder, 2, "- SetEffect_RootArmor, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- TrinketIronHealth, 60, 120, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- TrinketIronStamina, 60, 120, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- AdrenalineRush2, 120, 240, 1, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- Puke, 5, 10, 0.5, fx_Bonemass_aoe_start # sfx_Bonemass_alert");
        AppendActiveTemplateLine(builder, 1, "- Mountain:");
        AppendActiveTemplateLine(builder, 2, "- GP_Bonemass, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_frostresist, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_tamer, 240, 480, 1");
        AppendActiveTemplateLine(builder, 2, "- SetEffect_FenringArmor, 120, 240, 1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- SetEffect_WolfArmor, 120, 240, 1, vfx_HealthUpgrade");
        AppendActiveTemplateLine(builder, 2, "- SetEffect_FishingHat, 240, 480, 1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- SetEffect_HarvesterArmor, 240, 480, 1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- TrinketChitinSwim, 120, 240, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- TrinketSilverDamage, 60, 120, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- TrinketSilverResist, 60, 120, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- AdrenalineRush2, 120, 240, 1, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- Frost, 5, 10, 0.5, sfx_dragon_scream");
        AppendActiveTemplateLine(builder, 1, "- Plains:");
        AppendActiveTemplateLine(builder, 2, "- GP_Moder, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_BugRepellent, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_bzerker, 30, 60, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_barleywine, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_stamina_medium");
        AppendActiveTemplateLine(builder, 2, "- SetEffect_BerserkerUndeadArmor, 120, 240, 1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- TrinketBlackDamageHealth, 60, 120, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- TrinketBlackStamina, 60, 120, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- AdrenalineRush3, 120, 240, 1, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- Tared, 5, 10, 0.5, sfx_goblinking_taunt");
        AppendActiveTemplateLine(builder, 1, "- Mistlands:");
        AppendActiveTemplateLine(builder, 2, "- GP_Yagluth, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_eitr_minor");
        AppendActiveTemplateLine(builder, 2, "- Potion_health_major");
        AppendActiveTemplateLine(builder, 2, "- Potion_LightFoot, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_stamina_lingering, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- SetEffect_MageArmor, 120, 240, 1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- SE_Dvergr_buff, 60, 120, 1, vfx_HealthUpgrade");
        AppendActiveTemplateLine(builder, 2, "- SlowFall, 60, 120, 1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- Staff_shield, 120, 240, 1, vfx_HealthUpgrade");
        AppendActiveTemplateLine(builder, 2, "- TrinketCarapaceEitr, 60, 120, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- TrinketScaleStaminaDamage, 60, 120, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- Demister, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- AdrenalineRush3, 120, 240, 1, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- Slimed, 5, 10, 0.5, sfx_HiveQueen_callout");
        AppendActiveTemplateLine(builder, 1, "- AshLands:");
        AppendActiveTemplateLine(builder, 2, "- GP_Queen, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_eitr_lingering, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- Potion_health_lingering, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- SetEffect_AshlandsMediumArmor, 120, 240, 1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- WindRun, 120, 240, 1, vfx_StaminaUpgrade");
        AppendActiveTemplateLine(builder, 2, "- TrinketFlametalEitr, 60, 120, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- TrinketFlametalStaminaHealth, 60, 120, 0.5, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- Warm, 60, 120, 0.5, vfx_HealthUpgrade");
        AppendActiveTemplateLine(builder, 2, "- AdrenalineRush4, 120, 240, 1, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 2, "- Immobilized, 5, 10, 0.5, sfx_fader_taunt");
        AppendActiveTemplateLine(builder, 1, "- DeepNorth:");
        AppendActiveTemplateLine(builder, 2, "- GP_Fader, 120, 240, 1");
        AppendActiveTemplateLine(builder, 2, "- AdrenalineRush4, 120, 240, 1, fx_Adrenaline1");
        AppendActiveTemplateLine(builder, 1, "- Localize:");
        AppendActiveTemplateLine(builder, 2, "- \"You have received {name}\": # \"{name} \uD6A8\uACFC\uB97C \uBC1B\uC558\uC2B5\uB2C8\uB2E4\"");
        AppendActiveTemplateLine(builder, 2, "- \"You got bamboozled\": # \"\uC18D\uC558\uC2B5\uB2C8\uB2E4\"");
        AppendActiveTemplateLine(builder, 2, "- \"Buff Cooldown {seconds}s\": # \"\uBC84\uD504 \uCFE8\uB2E4\uC6B4 {seconds}\uCD08\"");
        AppendActiveTemplateLine(builder, 2, "- \"Already active {name}\": # \"{name} \uD6A8\uACFC\uAC00 \uC774\uBBF8 \uD65C\uC131\uD654\uB418\uC5B4 \uC788\uC2B5\uB2C8\uB2E4\"");
        AppendActiveTemplateBlankLine(builder);
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
            ? "pinName: null # Defaults to target Location.m_discoverLabel, then child Teleport.m_enterText, then locationName"
            : "pinName: null");
        AppendActiveTemplateLine(builder, 3, includeFieldComments
            ? "pinType: Icon3 # Options: Icon0, Icon1, Icon2, Icon3, Death, Bed, Icon4, Shout, None, Boss, Player, RandomEvent, Ping, EventArea, Hildir1, Hildir2, Hildir3"
            : "pinType: Icon3");
    }
}
