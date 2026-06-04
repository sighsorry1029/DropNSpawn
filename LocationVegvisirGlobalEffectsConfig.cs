using BepInEx.Configuration;

namespace DropNSpawn;

internal static class LocationVegvisirGlobalEffectsConfig
{
    private const string ConfigSection = "1 - General";
    private const string ConfigName = "Enable Vegvisir Global Effects";
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _enabled = null!;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        _enabled = plugin.BindConfigEntry(
            ConfigSection,
            ConfigName,
            DropNSpawnPlugin.Toggle.On,
            $"If on, Vegvisirs can grant weighted status effects from scalar shorthand rows in the {DropNSpawnPlugin.YamlFilePrefix}_location.yml vegvisirGlobalEffects table when a Vegvisir interaction succeeds. The selected effect, optional shared visual effect, and per-player cooldowns are kept on the loaded Vegvisir instance and reset when it unloads. effectPrefab values must start with vfx_, sfx_, or fx_ case-insensitively.",
            synchronizedSetting: true,
            configManagerOrder: 90);
    }

    internal static bool IsEnabled()
    {
        return _enabled?.Value == DropNSpawnPlugin.Toggle.On;
    }
}
