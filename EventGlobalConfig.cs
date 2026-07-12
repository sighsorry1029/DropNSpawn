using System;
using BepInEx.Configuration;

namespace DropNSpawn;

internal static class EventGlobalConfig
{
    internal enum EventSchedulingMode
    {
        Vanilla,
        MultipleGlobal,
        MultiplePerPlayer
    }

    internal enum EventPlayerBaseDefault
    {
        Off,
        Away,
        Near,
        AwayAndNear
    }

    private const float DefaultMinimumDistanceBetweenEvents = 100f;
    private const float DefaultEventDurationMultiplier = 1f;
    private const float DefaultRandomEventChance = 20f;
    private const float DefaultRandomEventIntervalMinutes = 46f;

    private static ConfigEntry<EventSchedulingMode>? _eventSchedulingMode;
    private static ConfigEntry<EventPlayerBaseDefault>? _defaultPlayerBase;
    private static ConfigEntry<float>? _minimumDistanceBetweenEvents;
    private static ConfigEntry<float>? _eventDurationMultiplier;
    private static ConfigEntry<float>? _randomEventChance;
    private static ConfigEntry<float>? _randomEventIntervalMinutes;

    internal static void Bind(DropNSpawnPlugin plugin)
    {
        _eventSchedulingMode = plugin.BindConfigEntry(
            "3 - Events",
            "Event scheduling mode",
            EventSchedulingMode.Vanilla,
            "Vanilla allows one active random event and performs one server-wide check per interval. MultipleGlobal allows multiple active random events while keeping one server-wide check per interval. MultiplePerPlayer allows multiple active random events and performs one independent check per player per interval. Standalone events use the same global or per-player evaluation mode.",
            synchronizedSetting: true,
            configManagerOrder: 800);
        _eventSchedulingMode.SettingChanged += HandleRuntimeSettingChanged;

        _defaultPlayerBase = plugin.BindConfigEntry(
            "3 - Events",
            "Default event player base",
            EventPlayerBaseDefault.Off,
            "Global default player-base condition for events that do not specify conditions.playerBase in YAML. Off keeps each event's baseline value. Away acts like playerBase: [away], Near acts like [near], and AwayAndNear acts like [away, near]. YAML values always win. Forced boss events do not use this random-event start filter.",
            synchronizedSetting: true,
            configManagerOrder: 650);
        _defaultPlayerBase.SettingChanged += HandleDefinitionSettingChanged;

        _minimumDistanceBetweenEvents = plugin.BindConfigEntry(
            "3 - Events",
            "Minimum distance between events",
            DefaultMinimumDistanceBetweenEvents,
            new ConfigDescription(
                "Minimum horizontal XZ distance between simultaneously active random event centers. A new event attempt inside this distance is ignored while the active event continues.",
                new AcceptableValueRange<float>(0f, 10000f)),
            synchronizedSetting: true,
            configManagerOrder: 600);
        _minimumDistanceBetweenEvents.SettingChanged += HandleRuntimeSettingChanged;

        _eventDurationMultiplier = plugin.BindConfigEntry(
            "3 - Events",
            "Event duration multiplier",
            DefaultEventDurationMultiplier,
            new ConfigDescription(
                "Positive values scale event durations that do not explicitly set YAML settings[2]. 0.5 halves durations, 1 keeps them unchanged, and 2 doubles them. 0 is a master switch that disables every event whose effective duration after YAML is greater than 0; duration-0 events remain enabled.",
                new AcceptableValueRange<float>(0f, 3f)),
            synchronizedSetting: true,
            configManagerOrder: 550);
        _eventDurationMultiplier.SettingChanged += HandleDefinitionSettingChanged;

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
                "Minutes between random event checks. 0 removes the cooldown and attempts a check every server FixedUpdate. This maps to RandEventSystem.m_eventIntervalMin.",
                new AcceptableValueRange<float>(0f, 10000f)),
            synchronizedSetting: true,
            configManagerOrder: 400);
        _randomEventIntervalMinutes.SettingChanged += HandleRuntimeSettingChanged;
    }

    internal static bool IsMultipleEventsEnabled()
    {
        return (_eventSchedulingMode?.Value ?? EventSchedulingMode.Vanilla) != EventSchedulingMode.Vanilla;
    }

    internal static bool IsCheckPerPlayerEnabled()
    {
        return (_eventSchedulingMode?.Value ?? EventSchedulingMode.Vanilla) == EventSchedulingMode.MultiplePerPlayer;
    }

    internal static EventPlayerBaseDefault GetDefaultPlayerBase()
    {
        return _defaultPlayerBase?.Value ?? EventPlayerBaseDefault.Off;
    }

    internal static float GetMinimumDistanceBetweenEvents()
    {
        return Math.Max(0f, _minimumDistanceBetweenEvents?.Value ?? DefaultMinimumDistanceBetweenEvents);
    }

    internal static float GetEventDurationMultiplier()
    {
        return Clamp(_eventDurationMultiplier?.Value ?? DefaultEventDurationMultiplier, 0f, 3f);
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

    private static void HandleDefinitionSettingChanged(object? sender, EventArgs e)
    {
        EventManager.ReapplyEventDefinitions("event global config");
    }

    private static float Clamp(float value, float min, float max)
    {
        return Math.Max(min, Math.Min(max, value));
    }
}
