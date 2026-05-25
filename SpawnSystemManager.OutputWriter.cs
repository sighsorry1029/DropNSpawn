using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SpawnSystemConfigurationEntry = DropNSpawn.CanonicalSpawnSystemEntry;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private static bool HasAnyConditionFields(SpawnSystemConfigurationEntry entry) => HasAnyConditionFields(entry.Conditions);

    private static bool HasAnySpawnFields(SpawnSystemConfigurationEntry entry) => HasAnySpawnFields(entry.SpawnSystem);

    private static bool HasAnyModifierFields(SpawnSystemConfigurationEntry entry) => HasAnyModifierFields(entry.Modifiers);

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

    private static void AppendYamlSpawnSystemPayloadBlock(StringBuilder builder, int indent, SpawnSystemConfigurationEntry entry, SpawnSystem.SpawnData defaults, bool includeEmptyPlaceholder)
    {
        if (!includeEmptyPlaceholder &&
            !HasAnySpawnFields(entry) &&
            !HasAnyConditionFields(entry) &&
            !HasAnyModifierFields(entry))
        {
            return;
        }

        AppendYamlLine(builder, indent, "spawnSystem:");
        AppendYamlSpawnSystemSpawnBlock(builder, indent + 1, entry, defaults, includeEmptyPlaceholder);
        AppendYamlSpawnSystemConditionsBlock(builder, indent, entry, defaults, includeEmptyPlaceholder);
        AppendYamlSpawnSystemModifiersBlock(builder, indent, entry, includeEmptyPlaceholder);
    }

    private static void AppendYamlSpawnSystemConditionsBlock(StringBuilder builder, int indent, SpawnSystemConfigurationEntry entry, SpawnSystem.SpawnData defaults, bool includeEmptyPlaceholder)
    {
        SpawnSystemConditionsDefinition? conditions = entry.Conditions;
        if (!includeEmptyPlaceholder && !HasAnyConditionFields(conditions))
        {
            return;
        }

        AppendYamlLine(builder, indent, "conditions:");
        TimeOfDayDefinition? defaultTimeOfDay = TimeOfDayFormatting.FromSpawnFlags(defaults.m_spawnAtDay, defaults.m_spawnAtNight);
        if (includeEmptyPlaceholder)
        {
            AppendYamlLine(builder, indent + 1, $"noSpawnRadius: {FormatYamlFloat(conditions?.NoSpawnRadius ?? defaults.m_spawnDistance)}");
            AppendYamlLine(builder, indent + 1, $"maxSpawned: {conditions?.MaxSpawned ?? defaults.m_maxSpawned}");
            AppendYamlLine(builder, indent + 1, $"tilt: {RangeFormatting.FormatInlineObject(GetTiltRange(entry) ?? RangeFormatting.From(defaults.m_minTilt, defaults.m_maxTilt))}");
            AppendYamlLine(builder, indent + 1, $"altitude: {RangeFormatting.FormatInlineObject(GetAltitudeRange(entry) ?? RangeFormatting.From(defaults.m_minAltitude, defaults.m_maxAltitude))}");
            AppendYamlLine(builder, indent + 1, $"oceanDepth: {RangeFormatting.FormatInlineObject(GetOceanDepthRange(entry) ?? RangeFormatting.From(defaults.m_minOceanDepth, defaults.m_maxOceanDepth))}");
            AppendYamlLine(builder, indent + 1, $"distanceFromCenter: {RangeFormatting.FormatInlineObject(GetDistanceFromCenterRange(entry) ?? RangeFormatting.From(defaults.m_minDistanceFromCenter, defaults.m_maxDistanceFromCenter))}");
            AppendYamlConditionalInlineListLine(builder, indent + 1, "biomes", conditions?.Biomes, includeEmptyPlaceholder);
            AppendYamlConditionalInlineListLine(builder, indent + 1, "biomeAreas", conditions?.BiomeAreas, includeEmptyPlaceholder);
            AppendYamlLine(builder, indent + 1, $"timeOfDay: {TimeOfDayFormatting.FormatInlineList(conditions?.TimeOfDay, defaultTimeOfDay)}");
            AppendYamlConditionalInlineListLine(builder, indent + 1, "requiredEnvironments", conditions?.RequiredEnvironments, includeEmptyPlaceholder);
            AppendYamlStringLine(builder, indent + 1, "requiredGlobalKey", conditions?.RequiredGlobalKey ?? defaults.m_requiredGlobalKey);
            AppendYamlLine(builder, indent + 1, $"inLava: {FormatYamlNullableBoolOrNull(conditions?.InLava)}");
            AppendYamlLine(builder, indent + 1, $"inForest: {FormatYamlNullableBoolOrNull(conditions?.InForest)}");
            AppendYamlLine(builder, indent + 1, $"insidePlayerBase: {FormatYamlBool(conditions?.InsidePlayerBase ?? defaults.m_insidePlayerBase)}");
            AppendYamlLine(builder, indent + 1, $"canSpawnCloseToPlayer: {FormatYamlBool(conditions?.CanSpawnCloseToPlayer ?? defaults.m_canSpawnCloseToPlayer)}");
            return;
        }

        AppendYamlOptionalFloatLine(builder, indent + 1, "noSpawnRadius", conditions?.NoSpawnRadius);
        AppendYamlOptionalIntLine(builder, indent + 1, "maxSpawned", conditions?.MaxSpawned);
        AppendYamlOptionalRangeLine(builder, indent + 1, "tilt", GetTiltRange(entry));
        AppendYamlOptionalRangeLine(builder, indent + 1, "altitude", GetAltitudeRange(entry));
        AppendYamlOptionalRangeLine(builder, indent + 1, "oceanDepth", GetOceanDepthRange(entry));
        AppendYamlOptionalRangeLine(builder, indent + 1, "distanceFromCenter", GetDistanceFromCenterRange(entry));
        AppendYamlOptionalInlineListLine(builder, indent + 1, "biomes", conditions?.Biomes);
        AppendYamlOptionalInlineListLine(builder, indent + 1, "biomeAreas", conditions?.BiomeAreas);
        AppendYamlOptionalTimeOfDayLine(builder, indent + 1, "timeOfDay", conditions?.TimeOfDay);
        AppendYamlOptionalInlineListLine(builder, indent + 1, "requiredEnvironments", conditions?.RequiredEnvironments);
        AppendYamlOptionalStringLine(builder, indent + 1, "requiredGlobalKey", conditions?.RequiredGlobalKey);
        AppendYamlOptionalBoolLine(builder, indent + 1, "inLava", conditions?.InLava);
        AppendYamlOptionalBoolLine(builder, indent + 1, "inForest", conditions?.InForest);
        AppendYamlOptionalBoolLine(builder, indent + 1, "insidePlayerBase", conditions?.InsidePlayerBase);
        AppendYamlOptionalBoolLine(builder, indent + 1, "canSpawnCloseToPlayer", conditions?.CanSpawnCloseToPlayer);
    }

    private static void AppendYamlSpawnSystemSpawnBlock(StringBuilder builder, int indent, SpawnSystemConfigurationEntry entry, SpawnSystem.SpawnData defaults, bool includeEmptyPlaceholder)
    {
        SpawnSystemSpawnDefinition? spawn = entry.SpawnSystem;
        if (!includeEmptyPlaceholder && !HasAnySpawnFields(spawn))
        {
            return;
        }
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
            AppendYamlLine(builder, indent, $"spawnRadius: {RangeFormatting.FormatInlineObject(GetSpawnRadiusRange(entry) ?? RangeFormatting.From(defaults.m_spawnRadiusMin, defaults.m_spawnRadiusMax))}");
            AppendYamlLine(builder, indent, $"groupSize: {RangeFormatting.FormatInlineObject(GetGroupSizeRange(entry) ?? RangeFormatting.From(defaults.m_groupSizeMin, defaults.m_groupSizeMax))}");
            AppendYamlLine(builder, indent, $"groupRadius: {FormatYamlFloat(spawn?.GroupRadius ?? defaults.m_groupRadius)}");
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
    }

    private static void AppendYamlSpawnSystemModifiersBlock(StringBuilder builder, int indent, SpawnSystemConfigurationEntry entry, bool includeEmptyPlaceholder)
    {
        SpawnSystemModifiersDefinition? modifiers = entry.Modifiers;
        if (!includeEmptyPlaceholder && !HasAnyModifierFields(modifiers))
        {
            return;
        }

        AppendYamlLine(builder, indent, "modifiers:");
        if (includeEmptyPlaceholder)
        {
            AppendYamlDictionaryLine(builder, indent + 1, "fields", modifiers?.Fields);
            AppendYamlInlineListLine(builder, indent + 1, "objects", modifiers?.Objects);
            AppendYamlStringLine(builder, indent + 1, "data", modifiers?.Data);
            AppendYamlStringLine(builder, indent + 1, "faction", modifiers?.Faction);
            return;
        }

        AppendYamlOptionalDictionaryLine(builder, indent + 1, "fields", modifiers?.Fields);
        AppendYamlOptionalInlineListLine(builder, indent + 1, "objects", modifiers?.Objects);
        AppendYamlOptionalStringLine(builder, indent + 1, "data", modifiers?.Data);
        AppendYamlOptionalStringLine(builder, indent + 1, "faction", modifiers?.Faction);
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

    private static string FormatYamlRangeOrNull(IntRangeDefinition? value)
    {
        return value != null && value.HasValues()
            ? RangeFormatting.FormatInlineObject(value)
            : "null";
    }

    private static string FormatYamlFloat(float value)
    {
        return Math.Abs(value % 1f) < 0.0001f
            ? ((int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static void AppendTemplateComment(StringBuilder builder, string text)
    {
        builder.Append("# ").AppendLine(text);
    }

    private static void AppendTemplateBlankLine(StringBuilder builder)
    {
        builder.AppendLine("#");
    }
}
