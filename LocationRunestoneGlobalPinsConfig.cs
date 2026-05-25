using BepInEx.Configuration;

namespace DropNSpawn;

internal static class LocationRunestoneGlobalPinsConfig
{
    private const string ConfigSection = "1 - General";
    private const string ConfigName = "Enable Runestone Global Pins";
    private static ConfigEntry<DropNSpawnPlugin.Toggle> _enabled = null!;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        _enabled = plugin.BindConfigEntry(
            ConfigSection,
            ConfigName,
            DropNSpawnPlugin.Toggle.On,
            $"If on, pinless RuneStones can reveal one saved map pin from the {DropNSpawnPlugin.YamlFilePrefix}_location.yml runestoneGlobalPins table. Check {DropNSpawnPlugin.YamlFilePrefix}_location.yml for targetLocations examples and schema. The selected target rolls once per loaded RuneStone instance, and zone unload/reload can roll again.",
            synchronizedSetting: true,
            configManagerOrder: 100);
    }

    internal static bool IsEnabled()
    {
        return _enabled?.Value == DropNSpawnPlugin.Toggle.On;
    }
}
