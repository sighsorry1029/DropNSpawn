using BepInEx.Configuration;

namespace DropNSpawn;

internal static class PluginBoundSettings
{
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? ServerConfigLocked { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableObjectOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableCharacterOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableSpawnerOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableSpawnSystemOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableEventOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? DisableGlobalKeySpawnSystemEntriesInLowTierBiomes { get; set; }

    internal static void Clear()
    {
        ServerConfigLocked = null;
        EnableObjectOverrides = null;
        EnableCharacterOverrides = null;
        EnableSpawnerOverrides = null;
        EnableSpawnSystemOverrides = null;
        EnableEventOverrides = null;
        DisableGlobalKeySpawnSystemEntriesInLowTierBiomes = null;
    }
}
