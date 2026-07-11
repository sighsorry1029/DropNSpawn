using System;
using System.Collections.Generic;
using System.Linq;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private static string ComputeEventPayloadSignature(List<EventDefinition> entries)
    {
        return EventCodec.Schema.ComputePayloadSignature(entries);
    }

    internal static string ComputeEventConfigurationSignature(IEnumerable<EventDefinition>? entries)
    {
        return ComputeEventPayloadSignature((entries ?? Enumerable.Empty<EventDefinition>()).ToList());
    }

    private static void WriteEventConditionsDefinition(PayloadSignatureBuilder builder, EventConditionsDefinition definition)
    {
        WriteStringList(builder, definition.Biomes);
        WriteStringList(builder, definition.PlayerBase);
        WriteStringList(builder, definition.RequiredEnvironments);
        WriteStringList(builder, definition.Players);
        WriteStringList(builder, definition.RequiredGlobalKeys);
        WriteStringList(builder, definition.ForbiddenGlobalKeys);
        WriteStringList(builder, definition.RequiredKnownItems);
        WriteStringList(builder, definition.ForbiddenKnownItems);
        WriteStringList(builder, definition.RequiredPlayerKeysAny);
        WriteStringList(builder, definition.RequiredPlayerKeysAll);
        WriteStringList(builder, definition.ForbiddenPlayerKeys);
    }

    private static void WriteEventSpawnDefinition(
        PayloadSignatureBuilder builder,
        EventSpawnDefinition definition,
        bool includeResolvedBiomeMask)
    {
        WriteNullableString(builder, definition.Prefab);
        WriteNullableBool(builder, definition.Enabled);
        WriteOptional(
            builder,
            definition.SpawnSystem,
            (fieldBuilder, value) => WriteSpawnSystemSpawnDefinition(fieldBuilder, value, includeResolvedBiomeMask));
    }

    private static void WriteEventConditionsDefinition(ZPackage package, EventConditionsDefinition definition)
    {
        WriteStringList(package, definition.Biomes);
        WriteStringList(package, definition.PlayerBase);
        WriteStringList(package, definition.RequiredEnvironments);
        WriteStringList(package, definition.Players);
        WriteStringList(package, definition.RequiredGlobalKeys);
        WriteStringList(package, definition.ForbiddenGlobalKeys);
        WriteStringList(package, definition.RequiredKnownItems);
        WriteStringList(package, definition.ForbiddenKnownItems);
        WriteStringList(package, definition.RequiredPlayerKeysAny);
        WriteStringList(package, definition.RequiredPlayerKeysAll);
        WriteStringList(package, definition.ForbiddenPlayerKeys);
    }

    private static EventConditionsDefinition ReadEventConditionsDefinition(ZPackage package)
    {
        return new EventConditionsDefinition
        {
            Biomes = ReadStringList(package),
            PlayerBase = ReadStringList(package),
            RequiredEnvironments = ReadStringList(package),
            Players = ReadStringList(package),
            RequiredGlobalKeys = ReadStringList(package),
            ForbiddenGlobalKeys = ReadStringList(package),
            RequiredKnownItems = ReadStringList(package),
            ForbiddenKnownItems = ReadStringList(package),
            RequiredPlayerKeysAny = ReadStringList(package),
            RequiredPlayerKeysAll = ReadStringList(package),
            ForbiddenPlayerKeys = ReadStringList(package)
        };
    }

    private static void WriteEventSpawnDefinition(ZPackage package, EventSpawnDefinition definition)
    {
        WriteNullableString(package, definition.Prefab);
        WriteNullableBool(package, definition.Enabled);
        WriteOptional(package, definition.SpawnSystem, WriteSpawnSystemSpawnDefinition);
    }

    private static EventSpawnDefinition ReadEventSpawnDefinition(ZPackage package)
    {
        return new EventSpawnDefinition
        {
            Prefab = ReadNullableString(package),
            Enabled = ReadNullableBool(package),
            SpawnSystem = ReadOptional(package, ReadSpawnSystemSpawnDefinition)
        };
    }
}
