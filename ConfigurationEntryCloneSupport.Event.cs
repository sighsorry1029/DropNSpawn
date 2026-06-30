using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class ConfigurationEntryCloneSupport
{
    internal static EventConditionsDefinition? CloneEventConditionsDefinition(EventConditionsDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new EventConditionsDefinition
        {
            Biomes = CloneStringList(source.Biomes),
            PlayerBase = CloneStringList(source.PlayerBase),
            RequiredEnvironments = CloneStringList(source.RequiredEnvironments),
            Players = CloneStringList(source.Players),
            RequiredGlobalKeys = CloneStringList(source.RequiredGlobalKeys),
            ForbiddenGlobalKeys = CloneStringList(source.ForbiddenGlobalKeys),
            RequiredKnownItems = CloneStringList(source.RequiredKnownItems),
            ForbiddenKnownItems = CloneStringList(source.ForbiddenKnownItems),
            RequiredPlayerKeysAny = CloneStringList(source.RequiredPlayerKeysAny),
            RequiredPlayerKeysAll = CloneStringList(source.RequiredPlayerKeysAll),
            ForbiddenPlayerKeys = CloneStringList(source.ForbiddenPlayerKeys)
        };
    }

    internal static EventSpawnDefinition? CloneEventSpawnDefinition(EventSpawnDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new EventSpawnDefinition
        {
            Prefab = source.Prefab,
            Enabled = source.Enabled,
            SpawnSystem = CloneSpawnSystemSpawnDefinition(source.SpawnSystem),
            Conditions = CloneSpawnSystemConditionsDefinition(source.Conditions),
            Modifiers = CloneEventSpawnModifiersDefinition(source.Modifiers)
        };
    }

    internal static EventSpawnModifiersDefinition? CloneEventSpawnModifiersDefinition(EventSpawnModifiersDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new EventSpawnModifiersDefinition
        {
            Fields = CloneStringDictionary(source.Fields),
            Data = source.Data,
            Faction = source.Faction
        };
    }
}
