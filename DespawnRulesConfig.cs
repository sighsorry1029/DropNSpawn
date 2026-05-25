using BepInEx.Configuration;
using UnityEngine;

namespace DropNSpawn;

internal static class DespawnRulesConfig
{
    private static ConfigEntry<float> _defaultDespawnRange = null!;
    private static ConfigEntry<float> _defaultDespawnDelaySeconds = null!;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        _defaultDespawnRange = plugin.BindConfigEntry(
            "3 - Character",
            "Default Despawn Range",
            64f,
            new ConfigDescription(
                "Default horizontal XZ range used by character.yml despawn rules when an entry omits despawn.range. Also applies automatically to boss prefabs auto-detected from the server prefab catalog when no explicit character.yml despawn rule exists. If 0, despawn countdowns are disabled unless a prefab entry overrides range.",
                new AcceptableValueRange<float>(0f, 128f)),
            synchronizedSetting: true);
        _defaultDespawnDelaySeconds = plugin.BindConfigEntry(
            "3 - Character",
            "Default Despawn Delay Seconds",
            60f,
            new ConfigDescription(
                "Default delay used by character.yml despawn rules when an entry omits despawn.delay. Applies only to prefabs with a despawn block.",
                new AcceptableValueRange<float>(0f, 300f)),
            synchronizedSetting: true);
    }

    internal static float GetDefaultDespawnRange()
    {
        return Mathf.Clamp(_defaultDespawnRange?.Value ?? 0f, 0f, 128f);
    }

    internal static float GetDefaultDespawnDelaySeconds()
    {
        return Mathf.Clamp(_defaultDespawnDelaySeconds?.Value ?? 0f, 0f, 300f);
    }
}
