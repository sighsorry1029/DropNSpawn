using System;
using BepInEx.Configuration;

namespace DropNSpawn;

internal static class SpawnerGlobalConfig
{
    internal const int MinSpawnAreaMaxTotalSpawns = 0;
    internal const int MaxSpawnAreaMaxTotalSpawns = 1000;
    internal const int MinZeroCreatureSpawnerRespawnTimeMinutes = 0;
    internal const int MaxZeroCreatureSpawnerRespawnTimeMinutes = 60;

    private static ConfigEntry<int> _defaultSpawnAreaMaxTotalSpawns = null!;
    private static ConfigEntry<int> _defaultZeroCreatureSpawnerRespawnTimeMinutes = null!;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        _defaultSpawnAreaMaxTotalSpawns = plugin.BindConfigEntry(
            "1 - General",
            "Default SpawnArea Max Total Spawns",
            0,
            new ConfigDescription(
                $"Default successful-spawn limit for every SpawnArea. 0 disables this option and leaves SpawnAreas unlimited. Values from 1 to {MaxSpawnAreaMaxTotalSpawns} make each SpawnArea destroy itself after that many successful spawns. Override per YAML entry with spawnArea.maxTotalSpawns.",
                new AcceptableValueRange<int>(MinSpawnAreaMaxTotalSpawns, MaxSpawnAreaMaxTotalSpawns)),
            synchronizedSetting: true,
            configManagerOrder: 450);

        _defaultZeroCreatureSpawnerRespawnTimeMinutes = plugin.BindConfigEntry(
            "1 - General",
            "Default zero CreatureSpawner respawn time minutes",
            0,
            new ConfigDescription(
                $"Default respawnTimeMinutes for CreatureSpawner components whose current value is 0. 0 disables this option. Values from 1 to {MaxZeroCreatureSpawnerRespawnTimeMinutes} set only zero-respawn CreatureSpawners. Override per YAML entry with creatureSpawner.respawnTimeMinutes.",
                new AcceptableValueRange<int>(MinZeroCreatureSpawnerRespawnTimeMinutes, MaxZeroCreatureSpawnerRespawnTimeMinutes)),
            synchronizedSetting: true,
            configManagerOrder: 440);
        _defaultZeroCreatureSpawnerRespawnTimeMinutes.SettingChanged += HandleCreatureSpawnerRespawnSettingChanged;
    }

    internal static int GetDefaultSpawnAreaMaxTotalSpawns()
    {
        return ClampSpawnAreaMaxTotalSpawns(_defaultSpawnAreaMaxTotalSpawns?.Value ?? 0);
    }

    internal static int GetDefaultZeroCreatureSpawnerRespawnTimeMinutes()
    {
        return ClampZeroCreatureSpawnerRespawnTimeMinutes(_defaultZeroCreatureSpawnerRespawnTimeMinutes?.Value ?? 0);
    }

    internal static int ClampSpawnAreaMaxTotalSpawns(int value)
    {
        return Math.Max(MinSpawnAreaMaxTotalSpawns, Math.Min(MaxSpawnAreaMaxTotalSpawns, value));
    }

    internal static int ClampZeroCreatureSpawnerRespawnTimeMinutes(int value)
    {
        return Math.Max(
            MinZeroCreatureSpawnerRespawnTimeMinutes,
            Math.Min(MaxZeroCreatureSpawnerRespawnTimeMinutes, value));
    }

    private static void HandleCreatureSpawnerRespawnSettingChanged(object? sender, EventArgs e)
    {
        SpawnerManager.ReapplyCreatureSpawnerGlobalDefaults();
    }
}
