using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class TimeOfDayDefinition : IYamlConvertible
{
    public List<string> Values { get; set; } = new();

    internal bool HasValues()
    {
        return Values.Count > 0;
    }

    internal void Normalize()
    {
        Values = TimeOfDayFormatting.NormalizeValues(Values);
    }

    void IYamlConvertible.Read(IParser parser, Type expectedType, ObjectDeserializer nestedObjectDeserializer)
    {
        if (parser.TryConsume<Scalar>(out Scalar? scalar))
        {
            Values = TimeOfDayFormatting.NormalizeValues(new[] { scalar.Value ?? "" });
            return;
        }

        parser.Consume<SequenceStart>();
        List<string> values = new();
        while (!parser.Accept<SequenceEnd>(out _))
        {
            values.Add(parser.Consume<Scalar>().Value ?? "");
        }

        parser.Consume<SequenceEnd>();
        Values = TimeOfDayFormatting.NormalizeValues(values);
    }

    void IYamlConvertible.Write(IEmitter emitter, ObjectSerializer nestedObjectSerializer)
    {
        emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Flow));
        foreach (string value in Values)
        {
            emitter.Emit(new Scalar(value));
        }

        emitter.Emit(new SequenceEnd());
    }
}

internal static class TimeOfDayFormatting
{
    private const float DayStartFraction = 0.15f;
    private const float NightStartFraction = 0.85f;
    private static readonly HashSet<string> InvalidTokenWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] SupportedTokens =
    {
        "day",
        "afternoon",
        "night"
    };

    internal static List<string> NormalizeValues(IEnumerable<string>? values)
    {
        List<string> normalized = new();
        if (values == null)
        {
            return normalized;
        }

        foreach (string rawValue in values)
        {
            string? token = NormalizeToken(rawValue);
            if (token == null || normalized.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            normalized.Add(token);
        }

        return normalized;
    }

    internal static TimeOfDayDefinition? FromSpawnFlags(bool allowDay, bool allowNight)
    {
        if (allowDay && allowNight)
        {
            return new TimeOfDayDefinition
            {
                Values = new List<string> { "day", "night" }
            };
        }

        if (allowDay)
        {
            return new TimeOfDayDefinition
            {
                Values = new List<string> { "day" }
            };
        }

        if (allowNight)
        {
            return new TimeOfDayDefinition
            {
                Values = new List<string> { "night" }
            };
        }

        return new TimeOfDayDefinition();
    }

    internal static bool MatchesCurrentTime(TimeOfDayDefinition? definition)
    {
        if (definition == null)
        {
            return true;
        }

        if (!definition.HasValues())
        {
            return false;
        }

        return definition.Values.Any(MatchesCurrentTimeToken);
    }

    internal static void GetBroadSpawnFlags(TimeOfDayDefinition? definition, out bool allowDay, out bool allowNight)
    {
        if (definition == null)
        {
            allowDay = true;
            allowNight = true;
            return;
        }

        if (!definition.HasValues())
        {
            allowDay = false;
            allowNight = false;
            return;
        }

        allowDay = definition.Values.Any(IsDayScopedToken);
        allowNight = definition.Values.Any(value => string.Equals(value, "night", StringComparison.OrdinalIgnoreCase));
    }

    internal static void GetRuntimeSpawnFlags(TimeOfDayDefinition? definition, out bool allowDay, out bool allowNight)
    {
        GetBroadSpawnFlags(definition, out allowDay, out allowNight);
        if (definition == null || !definition.HasValues())
        {
            return;
        }

        bool matchesCurrentTime = MatchesCurrentTime(definition);
        if (EnvMan.IsDay() && !matchesCurrentTime)
        {
            allowDay = false;
        }

        if (EnvMan.IsNight() && !matchesCurrentTime)
        {
            allowNight = false;
        }
    }

    internal static int GetCurrentRuntimePhaseMarker()
    {
        if (EnvMan.instance == null)
        {
            return int.MinValue;
        }

        if (EnvMan.IsNight())
        {
            return 0;
        }

        return MatchesConfiguredAfternoon() ? 2 : 1;
    }

    internal static string FormatInlineList(TimeOfDayDefinition? definition, TimeOfDayDefinition? fallback = null)
    {
        List<string> values = definition?.HasValues() == true
            ? definition.Values
            : (fallback?.HasValues() == true ? fallback.Values : new List<string>());

        return $"[{string.Join(", ", values)}]";
    }

    private static string? NormalizeToken(string? rawValue)
    {
        string trimmed = (rawValue ?? "").Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed == "daytime")
        {
            trimmed = "day";
        }

        if (SupportedTokens.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        WarnInvalidToken(trimmed);
        return null;
    }

    private static bool MatchesCurrentTimeToken(string token)
    {
        return token switch
        {
            "day" => EnvMan.IsDay(),
            "afternoon" => MatchesConfiguredAfternoon(),
            "night" => EnvMan.IsNight(),
            _ => false
        };
    }

    private static bool IsDayScopedToken(string token)
    {
        return token.Equals("day", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("afternoon", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesConfiguredAfternoon()
    {
        if (!EnvMan.IsDay())
        {
            return false;
        }

        float rawDayFraction = GetRawDayFraction();
        float afternoonStart = PluginSettingsFacade.GetAfternoonStartFraction();
        return rawDayFraction >= afternoonStart && rawDayFraction < NightStartFraction;
    }

    private static void WarnInvalidToken(string token)
    {
        if (!InvalidTokenWarnings.Add(token))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Unsupported timeOfDay token '{token}' was ignored. Supported values: day, afternoon, night.");
    }

    private static float GetRawDayFraction()
    {
        float smoothDayFraction = EnvMan.instance != null ? EnvMan.instance.GetDayFraction() : 0f;
        if (smoothDayFraction <= 0.25f)
        {
            return smoothDayFraction / 0.25f * DayStartFraction;
        }

        if (smoothDayFraction <= 0.75f)
        {
            return DayStartFraction + ((smoothDayFraction - 0.25f) / 0.5f) * (NightStartFraction - DayStartFraction);
        }

        return NightStartFraction + ((smoothDayFraction - 0.75f) / 0.25f) * (1f - NightStartFraction);
    }
}
