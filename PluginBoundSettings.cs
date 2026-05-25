using BepInEx.Configuration;

namespace DropNSpawn;

internal static class PluginBoundSettings
{
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? ServerConfigLocked { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableObjectOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableCharacterOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableSpawnerOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableLocationOverrides { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableSpawnSystemOverrides { get; set; }
    internal static ConfigEntry<float>? AfternoonStartFraction { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? ShowLocationProxyOfferingBowlHoverInfo { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableBossTamedPressure { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? PerPlayerBossStones { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? RemoteForsakenPowerSelection { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.ReferenceUpdateMode>? ReferenceUpdateMode { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableOfferingBowlDiagnostics { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableBossStoneDiagnostics { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableDespawnDiagnostics { get; set; }
    internal static ConfigEntry<DropNSpawnPlugin.Toggle>? EnableSpawnSystemDiagnostics { get; set; }
    internal static ConfigEntry<KeyboardShortcut>? RotateForsakenPowerShortcut { get; set; }

    internal static void Clear()
    {
        ServerConfigLocked = null;
        EnableObjectOverrides = null;
        EnableCharacterOverrides = null;
        EnableSpawnerOverrides = null;
        EnableLocationOverrides = null;
        EnableSpawnSystemOverrides = null;
        AfternoonStartFraction = null;
        ShowLocationProxyOfferingBowlHoverInfo = null;
        EnableBossTamedPressure = null;
        PerPlayerBossStones = null;
        RemoteForsakenPowerSelection = null;
        ReferenceUpdateMode = null;
        EnableOfferingBowlDiagnostics = null;
        EnableBossStoneDiagnostics = null;
        EnableDespawnDiagnostics = null;
        EnableSpawnSystemDiagnostics = null;
        RotateForsakenPowerShortcut = null;
    }
}
