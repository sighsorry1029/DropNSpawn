using System;
using System.Collections.Generic;
using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class IntRangeDefinition : IYamlConvertible
{
    public int? Min { get; set; }
    public int? Max { get; set; }

    internal bool HasValues()
    {
        return Min.HasValue || Max.HasValue;
    }

    void IYamlConvertible.Read(IParser parser, Type expectedType, ObjectDeserializer nestedObjectDeserializer)
    {
        if (parser.TryConsume<Scalar>(out Scalar? scalar))
        {
            (Min, Max) = RangeFormatting.ParseIntRange(scalar.Value);
            return;
        }

        parser.Consume<MappingStart>();
        while (!parser.Accept<MappingEnd>(out _))
        {
            string key = (parser.Consume<Scalar>().Value ?? "").Trim();
            switch (key.ToLowerInvariant())
            {
                case "min":
                    Min = RangeFormatting.ParseNullableInt(parser.Consume<Scalar>().Value);
                    break;
                case "max":
                    Max = RangeFormatting.ParseNullableInt(parser.Consume<Scalar>().Value);
                    break;
                default:
                    throw new YamlException($"Unsupported range key '{key}'. Only 'min' and 'max' are supported.");
            }
        }

        parser.Consume<MappingEnd>();
    }

    void IYamlConvertible.Write(IEmitter emitter, ObjectSerializer nestedObjectSerializer)
    {
        emitter.Emit(new Scalar(RangeFormatting.FormatShorthand(this)));
    }
}

internal sealed class FloatRangeDefinition : IYamlConvertible
{
    public float? Min { get; set; }
    public float? Max { get; set; }

    internal bool HasValues()
    {
        return Min.HasValue || Max.HasValue;
    }

    void IYamlConvertible.Read(IParser parser, Type expectedType, ObjectDeserializer nestedObjectDeserializer)
    {
        if (parser.TryConsume<Scalar>(out Scalar? scalar))
        {
            (Min, Max) = RangeFormatting.ParseFloatRange(scalar.Value);
            return;
        }

        parser.Consume<MappingStart>();
        while (!parser.Accept<MappingEnd>(out _))
        {
            string key = (parser.Consume<Scalar>().Value ?? "").Trim();
            switch (key.ToLowerInvariant())
            {
                case "min":
                    Min = RangeFormatting.ParseNullableFloat(parser.Consume<Scalar>().Value);
                    break;
                case "max":
                    Max = RangeFormatting.ParseNullableFloat(parser.Consume<Scalar>().Value);
                    break;
                default:
                    throw new YamlException($"Unsupported range key '{key}'. Only 'min' and 'max' are supported.");
            }
        }

        parser.Consume<MappingEnd>();
    }

    void IYamlConvertible.Write(IEmitter emitter, ObjectSerializer nestedObjectSerializer)
    {
        emitter.Emit(new Scalar(RangeFormatting.FormatShorthand(this)));
    }
}

internal static class RangeFormatting
{
    internal static IntRangeDefinition? From(int? min, int? max)
    {
        if (!min.HasValue && !max.HasValue)
        {
            return null;
        }

        return new IntRangeDefinition
        {
            Min = min,
            Max = max
        };
    }

    internal static IntRangeDefinition? FromReference(int actualMin, int actualMax, int defaultMin, int defaultMax)
    {
        if (actualMin == defaultMin && actualMax == defaultMax)
        {
            return null;
        }

        return From(actualMin, actualMax);
    }

    internal static FloatRangeDefinition? From(float? min, float? max)
    {
        if (!min.HasValue && !max.HasValue)
        {
            return null;
        }

        return new FloatRangeDefinition
        {
            Min = min,
            Max = max
        };
    }

    internal static FloatRangeDefinition? FromReference(float actualMin, float actualMax, float defaultMin, float defaultMax)
    {
        if (Math.Abs(actualMin - defaultMin) < 0.0001f && Math.Abs(actualMax - defaultMax) < 0.0001f)
        {
            return null;
        }

        return From(actualMin, actualMax);
    }

    internal static int? GetMin(IntRangeDefinition? range, int? fallbackMin)
    {
        return range?.Min ?? fallbackMin;
    }

    internal static int? GetMax(IntRangeDefinition? range, int? fallbackMin, int? fallbackMax)
    {
        return range?.Max ?? (range?.Min ?? fallbackMax ?? fallbackMin);
    }

    internal static float? GetMin(FloatRangeDefinition? range, float? fallbackMin)
    {
        return range?.Min ?? fallbackMin;
    }

    internal static float? GetMax(FloatRangeDefinition? range, float? fallbackMin, float? fallbackMax)
    {
        return range?.Max ?? (range?.Min ?? fallbackMax ?? fallbackMin);
    }

    internal static bool NormalizeAscending(ref int? min, ref int? max)
    {
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            (min, max) = (max, min);
            return true;
        }

        return false;
    }

    internal static bool NormalizeAscending(ref float? min, ref float? max)
    {
        if (min.HasValue && max.HasValue && min.Value > max.Value)
        {
            (min, max) = (max, min);
            return true;
        }

        return false;
    }

    internal static string FormatShorthand(IntRangeDefinition? range)
    {
        if (range == null || !range.HasValues())
        {
            return "";
        }

        int? min = range.Min;
        int? max = range.Max;
        NormalizeDisplayOrder(ref min, ref max);
        return FormatShorthand(min, max, value => value.ToString(CultureInfo.InvariantCulture));
    }

    internal static string FormatShorthand(FloatRangeDefinition? range)
    {
        if (range == null || !range.HasValues())
        {
            return "";
        }

        float? min = range.Min;
        float? max = range.Max;
        NormalizeDisplayOrder(ref min, ref max);
        return FormatShorthand(min, max, value => FormatYamlFloat(value));
    }

    internal static string FormatInlineObject(IntRangeDefinition? range)
    {
        int? min = range?.Min;
        int? max = range?.Max;
        NormalizeDisplayOrder(ref min, ref max);
        return FormatInlineObject(min, max, value => value.ToString(CultureInfo.InvariantCulture));
    }

    internal static string FormatInlineObject(FloatRangeDefinition? range)
    {
        float? min = range?.Min;
        float? max = range?.Max;
        NormalizeDisplayOrder(ref min, ref max);
        return FormatInlineObject(min, max, value => FormatYamlFloat(value));
    }

    internal static (int? min, int? max) ParseIntRange(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == "~")
        {
            return (null, null);
        }

        int separatorIndex = trimmed.IndexOf('~');
        if (separatorIndex < 0)
        {
            int value = ParseRequiredInt(trimmed);
            return (value, value);
        }

        string left = trimmed[..separatorIndex].Trim();
        string right = trimmed[(separatorIndex + 1)..].Trim();
        return (ParseNullableInt(left), ParseNullableInt(right));
    }

    internal static (float? min, float? max) ParseFloatRange(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == "~")
        {
            return (null, null);
        }

        int separatorIndex = trimmed.IndexOf('~');
        if (separatorIndex < 0)
        {
            float value = ParseRequiredFloat(trimmed);
            return (value, value);
        }

        string left = trimmed[..separatorIndex].Trim();
        string right = trimmed[(separatorIndex + 1)..].Trim();
        return (ParseNullableFloat(left), ParseNullableFloat(right));
    }

    internal static int? ParseNullableInt(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        return trimmed.Length == 0 ? null : ParseRequiredInt(trimmed);
    }

    internal static float? ParseNullableFloat(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        return trimmed.Length == 0 ? null : ParseRequiredFloat(trimmed);
    }

    private static int ParseRequiredInt(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new YamlException($"'{raw}' is not a valid integer range value.");
        }

        return value;
    }

    private static float ParseRequiredFloat(string raw)
    {
        if (!float.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float value))
        {
            throw new YamlException($"'{raw}' is not a valid float range value.");
        }

        return value;
    }

    private static string FormatShorthand<T>(T? min, T? max, Func<T, string> formatter)
        where T : struct
    {
        if (min.HasValue && max.HasValue && EqualityComparer<T>.Default.Equals(min.Value, max.Value))
        {
            return formatter(min.Value);
        }

        return $"{(min.HasValue ? formatter(min.Value) : "")}~{(max.HasValue ? formatter(max.Value) : "")}";
    }

    private static string FormatInlineObject<T>(T? min, T? max, Func<T, string> formatter)
        where T : struct
    {
        return $"{{ min: {(min.HasValue ? formatter(min.Value) : "")}, max: {(max.HasValue ? formatter(max.Value) : "")} }}";
    }

    private static void NormalizeDisplayOrder(ref int? min, ref int? max)
    {
        _ = NormalizeAscending(ref min, ref max);
    }

    private static void NormalizeDisplayOrder(ref float? min, ref float? max)
    {
        _ = NormalizeAscending(ref min, ref max);
    }

    private static string FormatYamlFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
