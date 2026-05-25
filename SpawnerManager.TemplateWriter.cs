using System.Text;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static string BuildPrimaryOverrideConfigurationTemplate()
    {
        StringBuilder builder = new();

        AppendTemplateComment(builder, $"Any file named {PluginSettingsFacade.GetYamlDomainSupplementalPrefix("spawner")}*.yml or {PluginSettingsFacade.GetYamlDomainSupplementalPrefix("spawner")}*.yaml is also loaded.");
        AppendTemplateComment(builder, $"Use {PluginSettingsFacade.GetYamlDomainFilePrefix("spawner")}.reference.yml to look up real spawner object names and {PluginSettingsFacade.GetYamlDomainFilePrefix("spawner")}.locations.reference.yml to look up valid location names");
        AppendTemplateComment(builder, $"Run `dns:full spawner` to regenerate {PluginSettingsFacade.GetYamlDomainFilePrefix("spawner")}.full.yml for exhaustive field examples");
        AppendTemplateComment(builder, "Only the most specific passing entry is applied # Less specific entries act as fallback # If multiple passing entries share the same specificity, the later loaded one wins");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "spawnArea");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: BonePileSpawner");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, $"location: Grave1 # ex) locations[] from {PluginSettingsFacade.GetYamlDomainFilePrefix("spawner")}.locations.reference.yml # Optional");
        AppendTemplateLine(builder, 1, "conditions: # If these conditions fail, this custom entry is ignored and the original spawner behavior is used");
        AppendTemplateLine(builder, 2, "altitude: null # ex) -1000~1000 # Range in world-height meters");
        AppendTemplateLine(builder, 2, "distanceFromCenter: null # ex) 0~10000 # Range in meters from the world center");
        AppendTemplateLine(builder, 2, "biomes: [] # ex) [BlackForest, Mistlands] # Allowed biomes # EWD custom biome names also work when EWD is installed");
        AppendTemplateLine(builder, 2, "timeOfDay: null # ex) [night] # [day, afternoon, night] # day contains afternoon");
        AppendTemplateLine(builder, 2, "requiredEnvironments: [] # ex) [Rain] # Allowed environment names");
        AppendTemplateLine(builder, 2, "requiredGlobalKeys: [] # ex) [defeated_gdking] # Required global keys");
        AppendTemplateLine(builder, 2, "forbiddenGlobalKeys: [] # ex) [nomap] # Forbidden global keys");
        AppendTemplateLine(builder, 2, "inForest: null # ex) true = forest only # false = outside forest only # null or no field allows both");
        AppendTemplateLine(builder, 2, "inDungeon: null # ex) true = dungeon only # false = overworld only # null or no field allows both");
        AppendTemplateLine(builder, 2, "insidePlayerBase: null # ex) true = near player base only # false = away from player base only # null or no field allows both # Valid for spawnArea-only entry gating");
        AppendTemplateLine(builder, 1, "spawnArea:");
        AppendTemplateLine(builder, 2, "levelUpChance: 15 # Percent chance for each extra level roll when creature level is a range");
        AppendTemplateLine(builder, 2, "spawnInterval: 30 # Seconds between spawn attempts after the timer passes");
        AppendTemplateLine(builder, 2, "triggerDistance: 256 # Meters from the spawner within which players must be present for spawning to tick");
        AppendTemplateLine(builder, 2, "setPatrolSpawnPoint: true # True gives spawned AI this spawner as its patrol or home point");
        AppendTemplateLine(builder, 2, "spawnRadius: 2 # Meters around the SpawnArea center used for spawn placement");
        AppendTemplateLine(builder, 2, "nearRadius: 10 # Meters used by the native near-count cap");
        AppendTemplateLine(builder, 2, "farRadius: 1000 # Meters used by the native total-count cap");
        AppendTemplateLine(builder, 2, "maxNear: 3 # Native cap on living creatures inside nearRadius");
        AppendTemplateLine(builder, 2, "maxTotal: 20 # Native cap on living creatures inside farRadius");
        AppendTemplateLine(builder, 2, "maxTotalSpawns: null # Optional total successful-spawn limit for this SpawnArea # null uses General / Default SpawnArea Max Total Spawns # 0 disables # 1~1000 destroys after that many successful spawns");
        AppendTemplateLine(builder, 2, "onGroundOnly: false # True restricts spawn placement to grounded points only");
        AppendTemplateLine(builder, 2, "creatures:");
        AppendTemplateLine(builder, 2, "- creature: null # ex) Draugr # Required creature prefab name");
        AppendTemplateLine(builder, 3, "weight: 1 # Relative weight versus other entries in the same SpawnArea list");
        AppendTemplateLine(builder, 3, "level: 1~1 # ex) 1~2 # Range of spawned creature levels");
        AppendTemplateLine(builder, 3, "data: null # Optional Expand World Data entry applied to the spawned creature");
        AppendTemplateLine(builder, 3, "fields: {} # ex) { Character.m_name: $enemy_draugr, health: 200 } # Expand World Data field overrides layered on top of data");
        AppendTemplateLine(builder, 3, "objects: [] # ex) [Wood,0,0,0,1] # Expand World Data object entries spawned at the final spawn point");
        AppendTemplateLine(builder, 3, $"faction: null # ex) Boss # Values: {FactionIntegration.GetNativeFactionList()}");
        AppendTemplateBlankLine(builder);

        AppendTemplateComment(builder, "creatureSpawner");
        AppendTemplateBlankLine(builder);
        AppendTemplateLine(builder, 0, "- prefab: Spawner_Boar");
        AppendTemplateLine(builder, 1, "enabled: true");
        AppendTemplateLine(builder, 1, $"location: Runestone_Boars # ex) locations[] from {PluginSettingsFacade.GetYamlDomainFilePrefix("spawner")}.locations.reference.yml # Optional");
        AppendTemplateLine(builder, 1, "conditions:");
        AppendTemplateLine(builder, 2, "altitude: null");
        AppendTemplateLine(builder, 2, "distanceFromCenter: null");
        AppendTemplateLine(builder, 2, "biomes: []");
        AppendTemplateLine(builder, 2, "requiredEnvironments: []");
        AppendTemplateLine(builder, 2, "inForest: null");
        AppendTemplateLine(builder, 2, "inDungeon: null");
        AppendTemplateLine(builder, 1, "creatureSpawner:");
        AppendTemplateLine(builder, 2, "creature: null # ex) Skeleton # Required creature prefab name");
        AppendTemplateLine(builder, 2, "timeOfDay: [day, night] # ex) [afternoon] # Runtime gate for this spawner # day contains afternoon");
        AppendTemplateLine(builder, 2, "requiredGlobalKey: '' # ex) defeated_gdking # Use '' to clear the native requirement # Omit the key to keep the current value");
        AppendTemplateLine(builder, 2, "blockingGlobalKey: '' # ex) nomap # Use '' to clear the native block key # Omit the key to keep the current value");
        AppendTemplateLine(builder, 2, "level: 1~1 # ex) 1~2 # Range of spawned creature levels");
        AppendTemplateLine(builder, 2, "levelUpChance: 10 # Percent chance for each extra level roll when level is a range");
        AppendTemplateLine(builder, 2, "respawnTimeMinutes: 20 # 0 disables respawn after the first successful spawn # Minutes before this spawner can respawn after the previous creature is gone");
        AppendTemplateLine(builder, 2, "spawnCheckInterval: 5 # Seconds between UpdateSpawner checks for this spawner");
        AppendTemplateLine(builder, 2, "spawnGroupId: 0 # Use spawnGroupRadius: 0 to avoid grouping # Nearby spawners with the same id can share native group blocking");
        AppendTemplateLine(builder, 2, "spawnGroupRadius: 0 # Meters used to link nearby same-id spawners into one native group");
        AppendTemplateLine(builder, 2, "spawnerWeight: 1 # Relative weight when a native spawn group chooses one spawner to fire");
        AppendTemplateLine(builder, 2, "maxGroupSpawned: 1 # Max living creatures allowed across the native spawn group");
        AppendTemplateLine(builder, 2, "triggerDistance: 60 # Meters from this spawner within which players must be present for UpdateSpawner to pass");
        AppendTemplateLine(builder, 2, "triggerNoise: 0 # 0 ignores noise # Meters-equivalent native noise threshold required inside triggerDistance");
        AppendTemplateLine(builder, 2, "allowInsidePlayerBase: false # True allows spawning inside player-base suppression areas");
        AppendTemplateLine(builder, 2, "wakeUpAnimation: false # True plays the wake-up animation when the creature appears");
        AppendTemplateLine(builder, 2, "setPatrolSpawnPoint: false # True makes spawned AI patrol around this spawner");
        AppendTemplateLine(builder, 2, "faction: null");
        AppendTemplateLine(builder, 2, "data: null");
        AppendTemplateLine(builder, 2, "fields: {}");
        AppendTemplateLine(builder, 2, "objects: []");
        AppendTemplateBlankLine(builder);

        return builder.ToString();
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

    private static void AppendTemplateBlankLine(StringBuilder builder)
    {
        builder.AppendLine("#");
    }
}
