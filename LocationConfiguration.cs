using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class LocationConfigurationEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public bool Enabled { get; set; } = true;

    [YamlMember(Order = 3)]
    public ConditionsDefinition? Conditions { get; set; }

    [YamlMember(Order = 4)]
    public LocationOfferingBowlDefinition? OfferingBowl { get; set; }

    [YamlMember(Order = 5)]
    public List<LocationItemStandDefinition>? ItemStands { get; set; }

    [YamlMember(Order = 6)]
    public LocationVegvisirGlobalEffectsDefinition? VegvisirGlobalEffects { get; set; }

    [YamlMember(Order = 7)]
    public LocationRunestoneGlobalPinsDefinition? RunestoneGlobalPins { get; set; }

    [YamlIgnore]
    public string RuleId { get; set; } = "";

    [YamlIgnore]
    public string? SourcePath { get; set; }

    [YamlIgnore]
    public int SourceLine { get; set; }

    [YamlIgnore]
    public int SourceColumn { get; set; }
}

internal sealed class LocationReferenceEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public LocationOfferingBowlDefinition? OfferingBowl { get; set; }

    [YamlMember(Order = 3)]
    public List<LocationItemStandDefinition>? ItemStands { get; set; }
}

internal sealed class LocationOfferingBowlDefinition
{
    [YamlMember(Order = 1)]
    public string? Name { get; set; }
    [YamlMember(Order = 2)]
    public string? UseItemText { get; set; }
    [YamlMember(Order = 3)]
    public string? UsedAltarText { get; set; }
    [YamlMember(Order = 4)]
    public string? CantOfferText { get; set; }
    [YamlMember(Order = 5)]
    public string? WrongOfferText { get; set; }
    [YamlMember(Order = 6)]
    public string? IncompleteOfferText { get; set; }
    [YamlMember(Order = 7)]
    public string? BossItem { get; set; }
    [YamlMember(Order = 8)]
    public int? BossItems { get; set; }
    [YamlMember(Order = 9)]
    public string? BossPrefab { get; set; }
    [YamlMember(Order = 10)]
    public string? ItemPrefab { get; set; }
    [YamlMember(Order = 11)]
    public string? SetGlobalKey { get; set; }
    [YamlMember(Order = 12)]
    public bool? RenderSpawnAreaGizmos { get; set; }
    [YamlMember(Order = 13)]
    public bool? AlertOnSpawn { get; set; }
    [YamlMember(Order = 14)]
    public float? SpawnBossDelay { get; set; }
    [YamlMember(Order = 15)]
    public FloatRangeDefinition? SpawnBossDistance { get; set; }

    [YamlIgnore]
    public float? SpawnBossMaxDistance { get; set; }
    [YamlIgnore]
    public float? SpawnBossMinDistance { get; set; }
    [YamlMember(Order = 18)]
    public float? SpawnBossMaxYDistance { get; set; }
    [YamlMember(Order = 19)]
    public int? GetSolidHeightMargin { get; set; }
    [YamlMember(Order = 20)]
    public bool? EnableSolidHeightCheck { get; set; }
    [YamlMember(Order = 21)]
    public float? SpawnPointClearingRadius { get; set; }
    [YamlMember(Order = 22)]
    public float? SpawnYOffset { get; set; }
    [YamlMember(Order = 23)]
    public bool? UseItemStands { get; set; }
    [YamlMember(Order = 24)]
    public string? ItemStandPrefix { get; set; }
    [YamlMember(Order = 25)]
    public float? ItemStandMaxRange { get; set; }
    [YamlMember(Order = 26)]
    public float? RespawnMinutes { get; set; }
    [YamlMember(Order = 27)]
    public string? Data { get; set; }
    [YamlMember(Order = 28)]
    public Dictionary<string, string>? Fields { get; set; }
    [YamlMember(Order = 29)]
    public List<string>? Objects { get; set; }
}

internal sealed class LocationItemStandDefinition
{
    [YamlMember(Order = 1)]
    public string? Path { get; set; }
    [YamlMember(Order = 2)]
    public string? Name { get; set; }
    [YamlMember(Order = 3)]
    public bool? CanBeRemoved { get; set; }
    [YamlMember(Order = 4)]
    public bool? AutoAttach { get; set; }
    [YamlMember(Order = 5)]
    public string? OrientationType { get; set; }
    [YamlMember(Order = 6)]
    public List<string>? SupportedTypes { get; set; }
    [YamlMember(Order = 7)]
    public List<string>? SupportedItems { get; set; }
    [YamlMember(Order = 8)]
    public List<string>? UnsupportedItems { get; set; }
    [YamlMember(Order = 9)]
    public float? PowerActivationDelay { get; set; }
    [YamlMember(Order = 10)]
    public string? GuardianPower { get; set; }
}

internal sealed class LocationVegvisirGlobalEffectsDefinition : IYamlConvertible
{
    [YamlMember(Order = 1)]
    public List<LocationVegvisirGlobalEffectsBiomeDefinition>? Biomes { get; set; }
    [YamlMember(Order = 2)]
    public LocationVegvisirGlobalEffectsLocalizationDefinition? Localize { get; set; }

    void IYamlConvertible.Read(IParser parser, Type expectedType, ObjectDeserializer nestedObjectDeserializer)
    {
        List<LocationVegvisirGlobalEffectsBiomeDefinition> biomes = new();
        LocationVegvisirGlobalEffectsLocalizationDefinition? localize = null;
        parser.Consume<SequenceStart>();
        while (!parser.Accept<SequenceEnd>(out _))
        {
            parser.Consume<MappingStart>();
            while (!parser.Accept<MappingEnd>(out _))
            {
                string biomeKey = (parser.Consume<Scalar>().Value ?? "").Trim();
                if (string.Equals(biomeKey, "Localize", StringComparison.OrdinalIgnoreCase))
                {
                    localize = (LocationVegvisirGlobalEffectsLocalizationDefinition?)nestedObjectDeserializer(
                        typeof(LocationVegvisirGlobalEffectsLocalizationDefinition));
                    continue;
                }

                List<LocationVegvisirGlobalEffectDefinition>? statusEffects =
                    (List<LocationVegvisirGlobalEffectDefinition>?)nestedObjectDeserializer(
                        typeof(List<LocationVegvisirGlobalEffectDefinition>));

                biomes.Add(new LocationVegvisirGlobalEffectsBiomeDefinition
                {
                    Biome = ParseVegvisirGlobalEffectsBiomeKey(biomeKey),
                    StatusEffects = statusEffects
                });
            }

            parser.Consume<MappingEnd>();
        }

        parser.Consume<SequenceEnd>();
        Biomes = biomes;
        Localize = localize;
    }

    void IYamlConvertible.Write(IEmitter emitter, ObjectSerializer nestedObjectSerializer)
    {
        emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
        foreach (LocationVegvisirGlobalEffectsBiomeDefinition biome in Biomes ?? Enumerable.Empty<LocationVegvisirGlobalEffectsBiomeDefinition>())
        {
            emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
            emitter.Emit(new Scalar(FormatVegvisirGlobalEffectsBiomeKey(biome.Biome)));
            nestedObjectSerializer(biome.StatusEffects ?? new List<LocationVegvisirGlobalEffectDefinition>());
            emitter.Emit(new MappingEnd());
        }

        if (Localize != null)
        {
            emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
            emitter.Emit(new Scalar("Localize"));
            nestedObjectSerializer(Localize);
            emitter.Emit(new MappingEnd());
        }

        emitter.Emit(new SequenceEnd());
    }

    private static string? ParseVegvisirGlobalEffectsBiomeKey(string rawKey)
    {
        string trimmed = rawKey.Trim();
        if (trimmed.Length == 0)
        {
            throw new YamlException("vegvisirGlobalEffects biome key cannot be empty. Use All to match every biome.");
        }

        if (trimmed.StartsWith("[", StringComparison.Ordinal) ||
            trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            throw new YamlException(
                $"vegvisirGlobalEffects biome key '{trimmed}' is invalid. Use one biome name per row, or All to match every biome.");
        }

        if (trimmed.Contains(','))
        {
            throw new YamlException(
                $"vegvisirGlobalEffects biome key '{trimmed}' is invalid. Use one biome name per row instead of comma-separated biome names.");
        }

        if (trimmed == "*")
        {
            throw new YamlException("vegvisirGlobalEffects uses '*' as a biome wildcard. Use All instead.");
        }

        return string.Equals(trimmed, "all", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static string FormatVegvisirGlobalEffectsBiomeKey(string? biome)
    {
        string trimmed = (biome ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "All";
        }

        return trimmed;
    }
}

internal sealed class LocationVegvisirGlobalEffectsLocalizationDefinition : IYamlConvertible
{
    [YamlIgnore]
    public string? YouHaveReceived { get; set; }
    [YamlIgnore]
    public string? YouGotBamboozled { get; set; }
    [YamlIgnore]
    public string? BuffCooldownNs { get; set; }
    [YamlIgnore]
    public string? AlreadyActive { get; set; }

    void IYamlConvertible.Read(IParser parser, Type expectedType, ObjectDeserializer nestedObjectDeserializer)
    {
        parser.Consume<SequenceStart>();
        while (!parser.Accept<SequenceEnd>(out _))
        {
            parser.Consume<MappingStart>();
            while (!parser.Accept<MappingEnd>(out _))
            {
                string key = (parser.Consume<Scalar>().Value ?? "").Trim();
                string? value = parser.Consume<Scalar>().Value;
                SetMessage(key, value);
            }

            parser.Consume<MappingEnd>();
        }

        parser.Consume<SequenceEnd>();
    }

    void IYamlConvertible.Write(IEmitter emitter, ObjectSerializer nestedObjectSerializer)
    {
        emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
        EmitMessage(emitter, "You have received {name}", YouHaveReceived);
        EmitMessage(emitter, "You got bamboozled", YouGotBamboozled);
        EmitMessage(emitter, "Buff Cooldown {seconds}s", BuffCooldownNs);
        EmitMessage(emitter, "Already active {name}", AlreadyActive);
        emitter.Emit(new SequenceEnd());
    }

    private void SetMessage(string key, string? value)
    {
        if (string.Equals(key, "You have received {name}", StringComparison.OrdinalIgnoreCase))
        {
            YouHaveReceived = NormalizeMessage(value);
            return;
        }

        if (string.Equals(key, "You got bamboozled", StringComparison.OrdinalIgnoreCase))
        {
            YouGotBamboozled = NormalizeMessage(value);
            return;
        }

        if (string.Equals(key, "Buff Cooldown {seconds}s", StringComparison.OrdinalIgnoreCase))
        {
            BuffCooldownNs = NormalizeMessage(value);
            return;
        }

        if (string.Equals(key, "Already active {name}", StringComparison.OrdinalIgnoreCase))
        {
            AlreadyActive = NormalizeMessage(value);
            return;
        }

        throw new YamlException(
            $"vegvisirGlobalEffects Localize key '{key}' is invalid. Supported keys: You have received {{name}}, You got bamboozled, Buff Cooldown {{seconds}}s, Already active {{name}}.");
    }

    private static string? NormalizeMessage(string? value)
    {
        string trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static void EmitMessage(IEmitter emitter, string key, string? value)
    {
        emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
        emitter.Emit(new Scalar(key));
        emitter.Emit(new Scalar(value ?? ""));
        emitter.Emit(new MappingEnd());
    }
}

internal sealed class LocationVegvisirGlobalEffectsBiomeDefinition
{
    [YamlMember(Order = 1)]
    public string? Biome { get; set; }
    [YamlMember(Order = 2)]
    public List<LocationVegvisirGlobalEffectDefinition>? StatusEffects { get; set; }
}

internal sealed class LocationVegvisirGlobalEffectDefinition : IYamlConvertible
{
    [YamlIgnore]
    public string StatusEffect { get; set; } = "";
    [YamlIgnore]
    public float? Weight { get; set; }
    [YamlIgnore]
    public float? CooldownSeconds { get; set; }
    [YamlIgnore]
    public float? DurationSeconds { get; set; }
    [YamlIgnore]
    public string? EffectPrefab { get; set; }

    void IYamlConvertible.Read(IParser parser, Type expectedType, ObjectDeserializer nestedObjectDeserializer)
    {
        if (!parser.TryConsume<Scalar>(out Scalar? scalar))
        {
            throw new YamlException(
                "vegvisirGlobalEffects status effect rows must use scalar shorthand: StatusEffect, durationSeconds, cooldownSeconds, weight, effectPrefab.");
        }

        ReadShorthand(scalar.Value);
    }

    void IYamlConvertible.Write(IEmitter emitter, ObjectSerializer nestedObjectSerializer)
    {
        emitter.Emit(new Scalar(FormatShorthand()));
    }

    private void ReadShorthand(string? rawValue)
    {
        string[] parts = (rawValue ?? "").Split(',');
        if (parts.Length > 5)
        {
            throw new YamlException(
                "vegvisirGlobalEffects status effect rows support at most five comma-separated values: StatusEffect, durationSeconds, cooldownSeconds, weight, effectPrefab.");
        }

        StatusEffect = parts.Length > 0 ? parts[0].Trim() : "";
        DurationSeconds = parts.Length > 1 ? ParseOptionalFloat(parts[1], "durationSeconds") : null;
        CooldownSeconds = parts.Length > 2 ? ParseOptionalFloat(parts[2], "cooldownSeconds") : null;
        Weight = parts.Length > 3 ? ParseOptionalFloat(parts[3], "weight") : null;
        EffectPrefab = parts.Length > 4 ? ParseOptionalString(parts[4]) : null;
    }

    private string FormatShorthand()
    {
        List<string> parts = new() { StatusEffect ?? "" };
        int lastValueIndex = !string.IsNullOrWhiteSpace(EffectPrefab)
            ? 4
            : (Weight.HasValue ? 3 : (CooldownSeconds.HasValue ? 2 : (DurationSeconds.HasValue ? 1 : 0)));
        if (lastValueIndex >= 1)
        {
            parts.Add(FormatOptionalFloat(DurationSeconds));
        }

        if (lastValueIndex >= 2)
        {
            parts.Add(FormatOptionalFloat(CooldownSeconds));
        }

        if (lastValueIndex >= 3)
        {
            parts.Add(FormatOptionalFloat(Weight));
        }

        if (lastValueIndex >= 4)
        {
            parts.Add(EffectPrefab ?? "");
        }

        return string.Join(", ", parts);
    }

    private static float? ParseOptionalFloat(string? rawValue, string fieldName)
    {
        string trimmed = (rawValue ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
        {
            throw new YamlException($"vegvisirGlobalEffects value '{trimmed}' is not a valid {fieldName} number.");
        }

        return value;
    }

    private static string FormatOptionalFloat(float? value)
    {
        return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "";
    }

    private static string? ParseOptionalString(string? rawValue)
    {
        string trimmed = (rawValue ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}

internal sealed class LocationRunestoneGlobalPinsDefinition
{
    [YamlMember(Order = 1)]
    public List<LocationRunestoneGlobalPinTargetDefinition>? TargetLocations { get; set; }
}

internal sealed class LocationRunestoneGlobalPinTargetDefinition
{
    [YamlMember(Order = 1)]
    public string LocationName { get; set; } = "";
    [YamlMember(Order = 2)]
    public float? Chance { get; set; }
    [YamlMember(Order = 3)]
    public List<string>? SourceBiomes { get; set; }
    [YamlMember(Order = 4)]
    public string? PinName { get; set; }
    [YamlMember(Order = 5)]
    public string? PinType { get; set; }
}
