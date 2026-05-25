using System;
using BepInEx.Configuration;

namespace DropNSpawn;

internal static class SpawnerGlobalConfig
{
    internal const int MinSpawnAreaMaxTotalSpawns = 0;
    internal const int MaxSpawnAreaMaxTotalSpawns = 1000;

    private static ConfigEntry<int> _defaultSpawnAreaMaxTotalSpawns = null!;

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
    }

    internal static int GetDefaultSpawnAreaMaxTotalSpawns()
    {
        return ClampSpawnAreaMaxTotalSpawns(_defaultSpawnAreaMaxTotalSpawns?.Value ?? 0);
    }

    internal static int ClampSpawnAreaMaxTotalSpawns(int value)
    {
        return Math.Max(MinSpawnAreaMaxTotalSpawns, Math.Min(MaxSpawnAreaMaxTotalSpawns, value));
    }
}
