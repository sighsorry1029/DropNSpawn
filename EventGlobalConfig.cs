using System;
using BepInEx.Configuration;

namespace DropNSpawn;

internal static class EventGlobalConfig
{
    private const float DefaultMinimumDistanceBetweenEvents = 100f;
    private const float DefaultRandomEventChance = 20f;
    private const float DefaultRandomEventIntervalMinutes = 46f;

    private static ConfigEntry<DropNSpawnPlugin.Toggle>? _multipleEvents;
    private static ConfigEntry<DropNSpawnPlugin.Toggle>? _checkPerPlayer;
    private static ConfigEntry<float>? _minimumDistanceBetweenEvents;
    private static ConfigEntry<float>? _randomEventChance;
    private static ConfigEntry<float>? _randomEventIntervalMinutes;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        _multipleEvents = plugin.BindConfigEntry(
            "3 - Events",
            "Multiple events",
            DropNSpawnPlugin.Toggle.Off,
            "If on, multiple random events can be active at the same time. DropNSpawn replaces RandEventSystem.FixedUpdate, SetRandomEvent, and SendCurrentRandomEvent while this option is on.",
            synchronizedSetting: true,
            configManagerOrder: 800);
        _multipleEvents.SettingChanged += HandleRuntimeSettingChanged;

        _checkPerPlayer = plugin.BindConfigEntry(
            "3 - Events",
            "Check per player",
            DropNSpawnPlugin.Toggle.Off,
            "If on, random event checks are evaluated separately for each player. DropNSpawn replaces RandEventSystem.UpdateRandomEvent while this option is on.",
            synchronizedSetting: true,
            configManagerOrder: 700);
        _checkPerPlayer.SettingChanged += HandleRuntimeSettingChanged;

        _minimumDistanceBetweenEvents = plugin.BindConfigEntry(
            "3 - Events",
            "Minimum distance between events",
            DefaultMinimumDistanceBetweenEvents,
            new ConfigDescription(
                "Minimum horizontal XZ distance between simultaneously active random events. Starting a new event inside this distance stops nearby active random events first.",
                new AcceptableValueRange<float>(0f, 10000f)),
            synchronizedSetting: true,
            configManagerOrder: 600);
        _minimumDistanceBetweenEvents.SettingChanged += HandleRuntimeSettingChanged;

        _randomEventChance = plugin.BindConfigEntry(
            "3 - Events",
            "Random event chance",
            DefaultRandomEventChance,
            new ConfigDescription(
                "Chance from 0 to 100 to try starting a random event when the random event interval elapses. This maps to RandEventSystem.m_eventChance.",
                new AcceptableValueRange<float>(0f, 100f)),
            synchronizedSetting: true,
            configManagerOrder: 500);
        _randomEventChance.SettingChanged += HandleRuntimeSettingChanged;

        _randomEventIntervalMinutes = plugin.BindConfigEntry(
            "3 - Events",
            "Random event interval",
            DefaultRandomEventIntervalMinutes,
            new ConfigDescription(
                "Minutes between random event checks. This maps to RandEventSystem.m_eventIntervalMin.",
                new AcceptableValueRange<float>(0f, 10000f)),
            synchronizedSetting: true,
            configManagerOrder: 400);
        _randomEventIntervalMinutes.SettingChanged += HandleRuntimeSettingChanged;
    }

    internal static bool IsMultipleEventsEnabled()
    {
        return _multipleEvents?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static bool IsCheckPerPlayerEnabled()
    {
        return _checkPerPlayer?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static float GetMinimumDistanceBetweenEvents()
    {
        return Math.Max(0f, _minimumDistanceBetweenEvents?.Value ?? DefaultMinimumDistanceBetweenEvents);
    }

    internal static float GetRandomEventChance()
    {
        return Clamp(_randomEventChance?.Value ?? DefaultRandomEventChance, 0f, 100f);
    }

    internal static float GetRandomEventIntervalMinutes()
    {
        return Math.Max(0f, _randomEventIntervalMinutes?.Value ?? DefaultRandomEventIntervalMinutes);
    }

    private static void HandleRuntimeSettingChanged(object? sender, EventArgs e)
    {
        EventManager.ApplyGlobalEventSettings();
    }

    private static float Clamp(float value, float min, float max)
    {
        return Math.Max(min, Math.Min(max, value));
    }
}
