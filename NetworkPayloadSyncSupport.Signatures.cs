using System;
using System.Collections.Generic;
using System.Linq;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private static string ComputeObjectPayloadSignature(List<PrefabConfigurationEntry> entries)
    {
        return ObjectCodec.Schema.ComputePayloadSignature(entries);
    }

    internal static string ComputeObjectConfigurationSignature(IEnumerable<PrefabConfigurationEntry>? entries)
    {
        return ComputeObjectPayloadSignature((entries ?? Enumerable.Empty<PrefabConfigurationEntry>()).ToList());
    }

    internal static string ComputeObjectEntrySignature(PrefabConfigurationEntry entry)
    {
        return ObjectCodec.Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: true);
    }

    internal static string ComputeObjectEntryIdentitySignature(PrefabConfigurationEntry entry)
    {
        return ObjectCodec.Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: false);
    }

    internal static string ComputeObjectDropRowSignature(DropEntryDefinition definition)
    {
        PayloadSignatureBuilder builder = new();
        WriteDropEntryDefinition(builder, definition);
        return builder.ComputeHash();
    }

    private static string ComputeCharacterPayloadSignature(List<CharacterDropPrefabEntry> entries)
    {
        return CharacterCodec.Schema.ComputePayloadSignature(entries);
    }

    internal static string ComputeCharacterConfigurationSignature(IEnumerable<CharacterDropPrefabEntry>? entries)
    {
        return ComputeCharacterPayloadSignature((entries ?? Enumerable.Empty<CharacterDropPrefabEntry>()).ToList());
    }

    internal static string ComputeCharacterEntrySignature(CharacterDropPrefabEntry entry)
    {
        return CharacterCodec.Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: true);
    }

    internal static string ComputeCharacterEntryIdentitySignature(CharacterDropPrefabEntry entry)
    {
        return CharacterCodec.Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: false);
    }

    internal static string ComputeCharacterDropRowSignature(CharacterDropEntryDefinition definition)
    {
        PayloadSignatureBuilder builder = new();
        WriteCharacterDropEntryDefinition(builder, definition);
        return builder.ComputeHash();
    }

    private static string ComputeSpawnerPayloadSignature(List<SpawnerConfigurationEntry> entries)
    {
        return SpawnerCodec.Schema.ComputePayloadSignature(entries);
    }

    internal static string ComputeSpawnerConfigurationSignature(IEnumerable<SpawnerConfigurationEntry>? entries)
    {
        return ComputeSpawnerPayloadSignature((entries ?? Enumerable.Empty<SpawnerConfigurationEntry>()).ToList());
    }

    internal static string ComputeSpawnerEntrySignature(SpawnerConfigurationEntry entry)
    {
        return SpawnerCodec.Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: true);
    }

    internal static string ComputeSpawnerEntryIdentitySignature(SpawnerConfigurationEntry entry)
    {
        return SpawnerCodec.Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: false);
    }

    private static string ComputeLocationPayloadSignature(List<LocationConfigurationEntry> entries)
    {
        return LocationCodec.Schema.ComputePayloadSignature(entries);
    }

    internal static string ComputeLocationConfigurationSignature(IEnumerable<LocationConfigurationEntry>? entries)
    {
        return ComputeLocationPayloadSignature((entries ?? Enumerable.Empty<LocationConfigurationEntry>()).ToList());
    }

    internal static string ComputeLocationEntrySignature(LocationConfigurationEntry entry)
    {
        return LocationCodec.Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: true);
    }

    internal static string ComputeLocationEntryIdentitySignature(LocationConfigurationEntry entry)
    {
        return LocationCodec.Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: false);
    }

    private static string ComputeSpawnSystemPayloadSignature(IReadOnlyList<CanonicalSpawnSystemEntry> entries)
    {
        return SpawnSystemCodec.Schema.ComputePayloadSignature(entries);
    }

    internal static string ComputeSpawnSystemProjectedConfigurationSignature<TSource>(IReadOnlyList<TSource> entries, Func<TSource, CanonicalSpawnSystemEntry> selector)
    {
        return SpawnSystemCodec.Schema.ComputePayloadSignature(entries, selector);
    }

    internal static string ComputeSpawnSystemConfigurationSignature(IEnumerable<CanonicalSpawnSystemEntry>? entries)
    {
        if (entries == null)
        {
            return ComputeSpawnSystemPayloadSignature(Array.Empty<CanonicalSpawnSystemEntry>());
        }

        if (entries is IReadOnlyList<CanonicalSpawnSystemEntry> list)
        {
            return ComputeSpawnSystemPayloadSignature(list);
        }

        return ComputeSpawnSystemPayloadSignature(entries.ToList());
    }

    internal static string ComputeSpawnSystemEntrySignature(CanonicalSpawnSystemEntry entry)
    {
        return SpawnSystemCodec.Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: true);
    }

    internal static string ComputeSpawnSystemEntryIdentitySignature(CanonicalSpawnSystemEntry entry)
    {
        return SpawnSystemCodec.Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: false);
    }

    private static void WriteSpawnSystemSpawnDefinition(PayloadSignatureBuilder builder, SpawnSystemSpawnDefinition definition)
    {
        WriteNullableString(builder, definition.Name);
        WriteNullableBool(builder, definition.HuntPlayer);
        WriteOptional(builder, definition.Level, WriteIntRangeDefinition);
        WriteNullableFloat(builder, definition.OverrideLevelUpChance);
        WriteNullableFloat(builder, definition.LevelUpMinCenterDistance);
        WriteNullableFloat(builder, definition.GroundOffset);
        WriteNullableFloat(builder, definition.GroundOffsetRandom);
        WriteNullableFloat(builder, definition.SpawnInterval);
        WriteNullableFloat(builder, definition.SpawnChance);
        WriteOptional(builder, definition.SpawnRadius, WriteFloatRangeDefinition);
        WriteOptional(builder, definition.GroupSize, WriteIntRangeDefinition);
        WriteNullableFloat(builder, definition.GroupRadius);
    }

    private static void WriteSpawnSystemConditionsDefinition(PayloadSignatureBuilder builder, SpawnSystemConditionsDefinition definition, bool includeResolvedBiomeMask)
    {
        WriteNullableFloat(builder, definition.NoSpawnRadius);
        WriteNullableInt(builder, definition.MaxSpawned);
        WriteOptional(builder, definition.Tilt, WriteFloatRangeDefinition);
        WriteOptional(builder, definition.Altitude, WriteFloatRangeDefinition);
        WriteOptional(builder, definition.OceanDepth, WriteFloatRangeDefinition);
        WriteOptional(builder, definition.DistanceFromCenter, WriteFloatRangeDefinition);
        WriteStringList(builder, definition.Biomes);
        if (includeResolvedBiomeMask)
        {
            WriteNullableInt(builder, GetResolvedBiomeMaskValue(definition.Biomes, definition.ResolvedBiomeMask));
        }
        WriteStringList(builder, definition.BiomeAreas);
        WriteOptional(builder, definition.TimeOfDay, WriteTimeOfDayDefinition);
        WriteStringList(builder, definition.RequiredEnvironments);
        WriteNullableString(builder, definition.RequiredGlobalKey);
        WriteNullableBool(builder, definition.InLava);
        WriteNullableBool(builder, definition.InForest);
        WriteNullableBool(builder, definition.InsidePlayerBase);
        WriteNullableBool(builder, definition.CanSpawnCloseToPlayer);
    }

    private static void WriteSpawnSystemModifiersDefinition(PayloadSignatureBuilder builder, SpawnSystemModifiersDefinition definition)
    {
        WriteStringDictionary(builder, definition.Fields);
        WriteStringList(builder, definition.Objects);
        WriteNullableString(builder, definition.Data);
        WriteNullableString(builder, definition.Faction);
    }
}
