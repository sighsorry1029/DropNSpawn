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
        Dispose();

        _eventSchedulingMode = plugin.BindConfigEntry(
            "4 - Events",
            "Event scheduling mode",
            EventSchedulingMode.Vanilla,
            "Vanilla allows one active random event and one server-wide check per interval. MultipleGlobal allows multiple active events with one server-wide check. MultiplePerPlayer allows multiple active events with an independent check for each player. Standalone events use the same global or per-player mode.\n" +
            "Use Vanilla when Custom Raids handles scheduling. Enable Event Overrides = Off does not disable this setting.",
            synchronizedSetting: true,
            configManagerOrder: 800);
        _eventSchedulingMode.SettingChanged += HandleRuntimeSettingChanged;

        _defaultPlayerBase = plugin.BindConfigEntry(
            "4 - Events",
            "Default event player base",
            EventPlayerBaseDefault.Off,
            "Global default player-base condition for events that do not specify conditions.playerBase in YAML. Off keeps each event's baseline value. Away acts like playerBase: [away], Near acts like [near], and AwayAndNear acts like [away, near]. YAML values always win. Forced boss events do not use this random-event start filter.",
            synchronizedSetting: true,
            configManagerOrder: 650);
        _defaultPlayerBase.SettingChanged += HandleDefinitionSettingChanged;

        _minimumDistanceBetweenEvents = plugin.BindConfigEntry(
            "4 - Events",
            "Minimum distance between events",
            DefaultMinimumDistanceBetweenEvents,
            new ConfigDescription(
                "Minimum horizontal XZ distance between simultaneously active random event centers. A new event attempt inside this distance is ignored while the active event continues.",
                new AcceptableValueRange<float>(0f, 10000f)),
            synchronizedSetting: true,
            configManagerOrder: 600);
        _minimumDistanceBetweenEvents.SettingChanged += HandleRuntimeSettingChanged;

        _eventDurationMultiplier = plugin.BindConfigEntry(
            "4 - Events",
            "Event duration multiplier",
            DefaultEventDurationMultiplier,
            new ConfigDescription(
                "Positive values scale event durations that do not explicitly set YAML settings[2]. 0.5 halves durations, 1 keeps them unchanged, and 2 doubles them. 0 is a master switch that disables every event whose effective duration after YAML is greater than 0; duration-0 events remain enabled.",
                new AcceptableValueRange<float>(0f, 3f)),
            synchronizedSetting: true,
            configManagerOrder: 550);
        _eventDurationMultiplier.SettingChanged += HandleDefinitionSettingChanged;

        _randomEventChance = plugin.BindConfigEntry(
            "4 - Events",
            "Random event chance",
            DefaultRandomEventChance,
            new ConfigDescription(
                "Chance from 0 to 100 to try starting a random event at each interval. Still applies when Event Overrides is off. With Custom Raids, match its EventTriggerChance; its individual raid checks use separate per-raid chances.",
                new AcceptableValueRange<float>(0f, 100f)),
            synchronizedSetting: true,
            configManagerOrder: 500);
        _randomEventChance.SettingChanged += HandleRuntimeSettingChanged;

        _randomEventIntervalMinutes = plugin.BindConfigEntry(
            "4 - Events",
            "Random event interval",
            DefaultRandomEventIntervalMinutes,
            new ConfigDescription(
                "Minutes between random event checks. 0 removes the cooldown and attempts a check every server FixedUpdate. Still applies when Event Overrides is off. With Custom Raids, match its EventCheckInterval (also in minutes).",
                new AcceptableValueRange<float>(0f, 10000f)),
            synchronizedSetting: true,
            configManagerOrder: 400);
        _randomEventIntervalMinutes.SettingChanged += HandleRuntimeSettingChanged;
    }

    internal static void Dispose()
    {
        if (_eventSchedulingMode != null)
        {
            _eventSchedulingMode.SettingChanged -= HandleRuntimeSettingChanged;
        }

        if (_defaultPlayerBase != null)
        {
            _defaultPlayerBase.SettingChanged -= HandleDefinitionSettingChanged;
        }

        if (_minimumDistanceBetweenEvents != null)
        {
            _minimumDistanceBetweenEvents.SettingChanged -= HandleRuntimeSettingChanged;
        }

        if (_eventDurationMultiplier != null)
        {
            _eventDurationMultiplier.SettingChanged -= HandleDefinitionSettingChanged;
        }

        if (_randomEventChance != null)
        {
            _randomEventChance.SettingChanged -= HandleRuntimeSettingChanged;
        }

        if (_randomEventIntervalMinutes != null)
        {
            _randomEventIntervalMinutes.SettingChanged -= HandleRuntimeSettingChanged;
        }

        _eventSchedulingMode = null;
        _defaultPlayerBase = null;
        _minimumDistanceBetweenEvents = null;
        _eventDurationMultiplier = null;
        _randomEventChance = null;
        _randomEventIntervalMinutes = null;
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
