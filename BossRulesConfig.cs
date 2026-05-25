using BepInEx.Configuration;

namespace DropNSpawn;

internal static class BossRulesConfig
{
    private const float SameBossDuplicateBlockRadius = 64f;
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _enableSameBossDuplicateBlock = null!;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        PluginBoundSettings.EnableBossTamedPressure = plugin.BindConfigEntry(
            "2 - Boss",
            "Enable Boss Tamed Pressure",
            DropNSpawnPlugin.Toggle.Off,
            "If on, character.yml bossTamedPressure entries can apply periodic damage and combat damage multipliers to tamed MonsterAI creatures near configured boss prefabs. If off, configured bossTamedPressure YAML stays on disk but no pressure scan, damage, or multiplier flags are applied.",
            synchronizedSetting: true,
            configManagerOrder: 200);
        _enableSameBossDuplicateBlock = plugin.BindConfigEntry(
            "2 - Boss",
            "Enable Same Boss Duplicate Block",
            DropNSpawnPlugin.Toggle.On,
            $"If on, OfferingBowl and CreatureSpawner both block new spawns when the same boss prefab already exists within {SameBossDuplicateBlockRadius:0} horizontal XZ meters of the altar or spawner. Applies only when the target prefab is marked as a boss.",
            synchronizedSetting: true,
            configManagerOrder: 100);
    }

    internal static float GetSameBossDuplicateBlockRadius()
    {
        return _enableSameBossDuplicateBlock?.Value == DropNSpawnPlugin.Toggle.Off
            ? 0f
            : SameBossDuplicateBlockRadius;
    }
}
