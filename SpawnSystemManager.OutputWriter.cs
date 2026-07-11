using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SpawnSystemConfigurationEntry = DropNSpawn.CanonicalSpawnSystemEntry;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private static bool HasAnySpawnFields(SpawnSystemConfigurationEntry entry) => HasAnySpawnFields(entry.SpawnSystem);

    private static List<string>? NormalizeReferenceStringList(IEnumerable<string>? values)
    {
        if (values == null)
        {
            return null;
        }

        List<string> normalized = values
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .ToList();

        return normalized.Count > 0 ? normalized : null;
    }

    private static bool? GetReferenceBoolOrNull(bool value, bool defaultValue) => value == defaultValue ? null : value;

    private static bool? ConvertExclusiveZoneToggle(bool allowInside, bool allowOutside)
    {
        if (allowInside && !allowOutside)
        {
            return true;
        }

        if (!allowInside && allowOutside)
        {
            return false;
        }

        return null;
    }

    private static bool? GetReferenceExclusiveZoneToggle(bool allowInside, bool allowOutside, bool defaultAllowInside, bool defaultAllowOutside)
    {
        bool? value = ConvertExclusiveZoneToggle(allowInside, allowOutside);
        bool? defaultValue = ConvertExclusiveZoneToggle(defaultAllowInside, defaultAllowOutside);
        return value == defaultValue ? null : value;
    }

    private static void ApplyExclusiveZoneToggle(bool? value, ref bool allowInside, ref bool allowOutside)
    {
        if (!value.HasValue)
        {
            return;
        }

        allowInside = value.Value;
        allowOutside = !value.Value;
    }

    private static int? GetReferenceIntOrNull(int value, int defaultValue) => value == defaultValue ? null : value;

    private static float? GetReferenceFloatOrNull(float value, float defaultValue)
    {
        return Math.Abs(value - defaultValue) < 0.0001f ? null : value;
    }

    private static void AppendReferenceEntry(StringBuilder builder, SpawnSystemConfigurationEntry entry)
    {
        AppendYamlListEntryLine(builder, 0, "prefab", entry.Prefab);
        if (!entry.Enabled)
        {
            AppendYamlOptionalBoolLine(builder, 1, "enabled", false);
        }

        AppendYamlSpawnSystemPayloadBlock(builder, 1, entry, new SpawnSystem.SpawnData(), includeEmptyPlaceholder: false);
    }

    private static void AppendConfigurationEntry(StringBuilder builder, SpawnSystemConfigurationEntry entry)
    {
        SpawnSystem.SpawnData defaults = new();

        AppendYamlListEntryLine(builder, 0, "prefab", entry.Prefab);
        AppendYamlLine(builder, 1, $"enabled: {FormatYamlBool(entry.Enabled)}");
        AppendYamlSpawnSystemPayloadBlock(builder, 1, entry, defaults, includeEmptyPlaceholder: true);
    }

    internal static void AppendYamlSpawnSystemPayloadBlock(StringBuilder builder, int indent, SpawnSystemConfigurationEntry entry, SpawnSystem.SpawnData defaults, bool includeEmptyPlaceholder)
    {
        if (!includeEmptyPlaceholder && !HasAnySpawnFields(entry))
        {
            return;
        }

        AppendYamlLine(builder, indent, "spawnSystem:");
        AppendYamlSpawnSystemSpawnBlock(builder, indent + 1, entry, defaults, includeEmptyPlaceholder);
    }

    private static void AppendYamlSpawnSystemSpawnBlock(StringBuilder builder, int indent, SpawnSystemConfigurationEntry entry, SpawnSystem.SpawnData defaults, bool includeEmptyPlaceholder)
    {
        SpawnSystemSpawnDefinition? spawn = entry.SpawnSystem;
        if (!includeEmptyPlaceholder && !HasAnySpawnFields(spawn))
        {
            return;
        }

        TimeOfDayDefinition? defaultTimeOfDay = TimeOfDayFormatting.FromSpawnFlags(defaults.m_spawnAtDay, defaults.m_spawnAtNight);
        if (includeEmptyPlaceholder)
        {
            AppendYamlStringLine(builder, indent, "name", spawn?.Name);
            AppendYamlLine(builder, indent, $"huntPlayer: {FormatYamlBool(spawn?.HuntPlayer ?? defaults.m_huntPlayer)}");
            AppendYamlLine(builder, indent, $"level: {RangeFormatting.FormatInlineObject(GetLevelRange(entry) ?? RangeFormatting.From(defaults.m_minLevel, defaults.m_maxLevel))}");
            AppendYamlLine(builder, indent, $"overrideLevelUpChance: {FormatYamlFloat(spawn?.OverrideLevelUpChance ?? defaults.m_overrideLevelupChance)}");
            AppendYamlLine(builder, indent, $"levelUpMinCenterDistance: {FormatYamlFloat(spawn?.LevelUpMinCenterDistance ?? defaults.m_levelUpMinCenterDistance)}");
            AppendYamlLine(builder, indent, $"groundOffset: {FormatYamlFloat(spawn?.GroundOffset ?? defaults.m_groundOffset)}");
            AppendYamlLine(builder, indent, $"groundOffsetRandom: {FormatYamlFloat(spawn?.GroundOffsetRandom ?? defaults.m_groundOffsetRandom)}");
            AppendYamlLine(builder, indent, $"spawnInterval: {FormatYamlFloat(spawn?.SpawnInterval ?? defaults.m_spawnInterval)}");
            AppendYamlLine(builder, indent, $"spawnChance: {FormatYamlFloat(spawn?.SpawnChance ?? defaults.m_spawnChance)}");
            // SpawnSystem replaces non-positive stored endpoints with its 40-80m global defaults.
            FloatRangeDefinition? spawnRadius = GetSpawnRadiusRange(entry) ?? RangeFormatting.From(defaults.m_spawnRadiusMin, defaults.m_spawnRadiusMax);
            float spawnRadiusMin = spawnRadius?.Min ?? 0f;
            float spawnRadiusMax = spawnRadius?.Max ?? 0f;
            spawnRadius = RangeFormatting.From(
                spawnRadiusMin > 0f ? spawnRadiusMin : 40f,
                spawnRadiusMax > 0f ? spawnRadiusMax : 80f);
            AppendYamlLine(builder, indent, $"spawnRadius: {RangeFormatting.FormatInlineObject(spawnRadius)}");
            AppendYamlLine(builder, indent, $"groupSize: {RangeFormatting.FormatInlineObject(GetGroupSizeRange(entry) ?? RangeFormatting.From(defaults.m_groupSizeMin, defaults.m_groupSizeMax))}");
            AppendYamlLine(builder, indent, $"groupRadius: {FormatYamlFloat(spawn?.GroupRadius ?? defaults.m_groupRadius)}");
            AppendYamlLine(builder, indent, $"noSpawnRadius: {FormatYamlFloat(spawn?.NoSpawnRadius ?? defaults.m_spawnDistance)}");
            AppendYamlLine(builder, indent, $"maxSpawned: {spawn?.MaxSpawned ?? defaults.m_maxSpawned}");
            AppendYamlLine(builder, indent, $"tilt: {RangeFormatting.FormatInlineObject(GetTiltRange(entry) ?? RangeFormatting.From(defaults.m_minTilt, defaults.m_maxTilt))}");
            AppendYamlLine(builder, indent, $"altitude: {RangeFormatting.FormatInlineObject(GetAltitudeRange(entry) ?? RangeFormatting.From(defaults.m_minAltitude, defaults.m_maxAltitude))}");
            AppendYamlLine(builder, indent, $"oceanDepth: {RangeFormatting.FormatInlineObject(GetOceanDepthRange(entry) ?? RangeFormatting.From(defaults.m_minOceanDepth, defaults.m_maxOceanDepth))}");
            AppendYamlLine(builder, indent, $"distanceFromCenter: {RangeFormatting.FormatInlineObject(GetDistanceFromCenterRange(entry) ?? RangeFormatting.From(defaults.m_minDistanceFromCenter, defaults.m_maxDistanceFromCenter))}");
            AppendYamlConditionalInlineListLine(builder, indent, "biomes", spawn?.Biomes, includeEmptyPlaceholder);
            AppendYamlConditionalInlineListLine(builder, indent, "biomeAreas", spawn?.BiomeAreas, includeEmptyPlaceholder);
            AppendYamlLine(builder, indent, $"timeOfDay: {TimeOfDayFormatting.FormatInlineList(spawn?.TimeOfDay, defaultTimeOfDay)}");
            AppendYamlConditionalInlineListLine(builder, indent, "requiredEnvironments", spawn?.RequiredEnvironments, includeEmptyPlaceholder);
            AppendYamlStringLine(builder, indent, "requiredGlobalKey", spawn?.RequiredGlobalKey ?? defaults.m_requiredGlobalKey);
            AppendYamlLine(builder, indent, $"inLava: {FormatYamlNullableBoolOrNull(spawn?.InLava)}");
            AppendYamlLine(builder, indent, $"inForest: {FormatYamlNullableBoolOrNull(spawn?.InForest)}");
            AppendYamlLine(builder, indent, $"insidePlayerBase: {FormatYamlBool(spawn?.InsidePlayerBase ?? defaults.m_insidePlayerBase)}");
            AppendYamlLine(builder, indent, $"canSpawnCloseToPlayer: {FormatYamlBool(spawn?.CanSpawnCloseToPlayer ?? defaults.m_canSpawnCloseToPlayer)}");
            AppendYamlDictionaryLine(builder, indent, "fields", spawn?.Fields);
            AppendYamlInlineListLine(builder, indent, "objects", spawn?.Objects);
            AppendYamlStringLine(builder, indent, "data", spawn?.Data);
            AppendYamlStringLine(builder, indent, "faction", spawn?.Faction);
            return;
        }

        AppendYamlOptionalStringLine(builder, indent, "name", spawn?.Name);
        AppendYamlOptionalBoolLine(builder, indent, "huntPlayer", spawn?.HuntPlayer);
        AppendYamlOptionalRangeLine(builder, indent, "level", GetLevelRange(entry));
        AppendYamlOptionalFloatLine(builder, indent, "overrideLevelUpChance", spawn?.OverrideLevelUpChance);
        AppendYamlOptionalFloatLine(builder, indent, "levelUpMinCenterDistance", spawn?.LevelUpMinCenterDistance);
        AppendYamlOptionalFloatLine(builder, indent, "groundOffset", spawn?.GroundOffset);
        AppendYamlOptionalFloatLine(builder, indent, "groundOffsetRandom", spawn?.GroundOffsetRandom);
        AppendYamlOptionalFloatLine(builder, indent, "spawnInterval", spawn?.SpawnInterval);
        AppendYamlOptionalFloatLine(builder, indent, "spawnChance", spawn?.SpawnChance);
        AppendYamlOptionalRangeLine(builder, indent, "spawnRadius", GetSpawnRadiusRange(entry));
        AppendYamlOptionalRangeLine(builder, indent, "groupSize", GetGroupSizeRange(entry));
        AppendYamlOptionalFloatLine(builder, indent, "groupRadius", spawn?.GroupRadius);
        AppendYamlOptionalFloatLine(builder, indent, "noSpawnRadius", spawn?.NoSpawnRadius);
        AppendYamlOptionalIntLine(builder, indent, "maxSpawned", spawn?.MaxSpawned);
        AppendYamlOptionalRangeLine(builder, indent, "tilt", GetTiltRange(entry));
        AppendYamlOptionalRangeLine(builder, indent, "altitude", GetAltitudeRange(entry));
        AppendYamlOptionalRangeLine(builder, indent, "oceanDepth", GetOceanDepthRange(entry));
        AppendYamlOptionalRangeLine(builder, indent, "distanceFromCenter", GetDistanceFromCenterRange(entry));
        AppendYamlOptionalInlineListLine(builder, indent, "biomes", spawn?.Biomes);
        AppendYamlOptionalInlineListLine(builder, indent, "biomeAreas", spawn?.BiomeAreas);
        AppendYamlOptionalTimeOfDayLine(builder, indent, "timeOfDay", spawn?.TimeOfDay);
        AppendYamlOptionalInlineListLine(builder, indent, "requiredEnvironments", spawn?.RequiredEnvironments);
        AppendYamlOptionalStringLine(builder, indent, "requiredGlobalKey", spawn?.RequiredGlobalKey);
        AppendYamlOptionalBoolLine(builder, indent, "inLava", spawn?.InLava);
        AppendYamlOptionalBoolLine(builder, indent, "inForest", spawn?.InForest);
        AppendYamlOptionalBoolLine(builder, indent, "insidePlayerBase", spawn?.InsidePlayerBase);
        AppendYamlOptionalBoolLine(builder, indent, "canSpawnCloseToPlayer", spawn?.CanSpawnCloseToPlayer);
        AppendYamlOptionalDictionaryLine(builder, indent, "fields", spawn?.Fields);
        AppendYamlOptionalInlineListLine(builder, indent, "objects", spawn?.Objects);
        AppendYamlOptionalStringLine(builder, indent, "data", spawn?.Data);
        AppendYamlOptionalStringLine(builder, indent, "faction", spawn?.Faction);
    }

    private static void AppendYamlLine(StringBuilder builder, int indent, string text)
    {
        builder.Append(' ', indent * 2);
        builder.AppendLine(text);
    }

    private static void AppendYamlListEntryLine(StringBuilder builder, int indent, string key, string? value)
    {
        builder.Append(' ', indent * 2);
        builder.Append("- ").Append(key).Append(": ").AppendLine(FormatYamlString(value));
    }

    private static void AppendYamlStringLine(StringBuilder builder, int indent, string key, string? value)
    {
        builder.Append(' ', indent * 2);
        builder.Append(key).Append(": ");
        if (value == null)
        {
            builder.Append("null");
        }
        else
        {
            builder.Append(FormatYamlString(value));
        }
        builder.AppendLine();
    }

    private static void AppendYamlOptionalStringLine(StringBuilder builder, int indent, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            AppendYamlStringLine(builder, indent, key, value);
        }
    }

    private static void AppendYamlInlineListLine(StringBuilder builder, int indent, string key, List<string>? values)
    {
        builder.Append(' ', indent * 2);
        builder.Append(key).Append(": ").AppendLine(FormatYamlInlineList(values));
    }

    private static void AppendYamlOptionalInlineListLine(StringBuilder builder, int indent, string key, List<string>? values)
    {
        if (values != null && values.Count > 0)
        {
            AppendYamlInlineListLine(builder, indent, key, values);
        }
    }

    private static void AppendYamlConditionalInlineListLine(StringBuilder builder, int indent, string key, List<string>? values, bool includeEmptyPlaceholder)
    {
        if (includeEmptyPlaceholder || (values?.Count ?? 0) > 0)
        {
            AppendYamlInlineListLine(builder, indent, key, values);
        }
    }

    private static void AppendYamlOptionalTimeOfDayLine(StringBuilder builder, int indent, string key, TimeOfDayDefinition? value)
    {
        if (value != null)
        {
            AppendYamlLine(builder, indent, $"{key}: {TimeOfDayFormatting.FormatInlineList(value)}");
        }
    }

    private static void AppendYamlOptionalBoolLine(StringBuilder builder, int indent, string key, bool? value)
    {
        if (value.HasValue)
        {
            AppendYamlLine(builder, indent, $"{key}: {FormatYamlBool(value.Value)}");
        }
    }

    private static void AppendYamlOptionalIntLine(StringBuilder builder, int indent, string key, int? value)
    {
        if (value.HasValue)
        {
            AppendYamlLine(builder, indent, $"{key}: {value.Value}");
        }
    }

    private static void AppendYamlOptionalFloatLine(StringBuilder builder, int indent, string key, float? value)
    {
        if (value.HasValue)
        {
            AppendYamlLine(builder, indent, $"{key}: {FormatYamlFloat(value.Value)}");
        }
    }

    private static void AppendYamlOptionalRangeLine(StringBuilder builder, int indent, string key, IntRangeDefinition? range)
    {
        if (range != null && range.HasValues())
        {
            AppendYamlLine(builder, indent, $"{key}: {RangeFormatting.FormatShorthand(range)}");
        }
    }

    private static void AppendYamlOptionalRangeLine(StringBuilder builder, int indent, string key, FloatRangeDefinition? range)
    {
        if (range != null && range.HasValues())
        {
            AppendYamlLine(builder, indent, $"{key}: {RangeFormatting.FormatShorthand(range)}");
        }
    }

    private static void AppendYamlDictionaryLine(StringBuilder builder, int indent, string key, Dictionary<string, string>? values)
    {
        if (values == null || values.Count == 0)
        {
            AppendYamlLine(builder, indent, $"{key}: {{}}");
            return;
        }

        builder.Append(' ', indent * 2);
        builder.Append(key).Append(": { ");
        builder.Append(string.Join(", ", values.Select(pair => $"{FormatYamlString(pair.Key)}: {FormatYamlString(pair.Value)}")));
        builder.AppendLine(" }");
    }

    private static void AppendYamlOptionalDictionaryLine(StringBuilder builder, int indent, string key, Dictionary<string, string>? values)
    {
        if (values != null && values.Count > 0)
        {
            AppendYamlDictionaryLine(builder, indent, key, values);
        }
    }

    private static string FormatYamlInlineList(List<string>? values)
    {
        if (values == null || values.Count == 0)
        {
            return "[]";
        }

        return $"[{string.Join(", ", values.Select(FormatYamlString))}]";
    }

    private static string FormatYamlString(string? value)
    {
        string normalized = value ?? "";
        return Serializer.Serialize(normalized).TrimEnd('\r', '\n');
    }

    private static string FormatYamlBool(bool value) => value ? "true" : "false";

    private static string FormatYamlNullableBoolOrNull(bool? value) => value.HasValue ? FormatYamlBool(value.Value) : "null";

    private static string FormatYamlFloat(float value)
    {
        return Math.Abs(value % 1f) < 0.0001f
            ? ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
