using System.Collections.Generic;
using System.Linq;

namespace DropNSpawn;

internal static partial class ConfigurationEntryCloneSupport
{
    internal static ConditionsDefinition? CloneConditions(ConditionsDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new ConditionsDefinition
        {
            Level = CloneIntRange(source.Level),
            Altitude = CloneFloatRange(source.Altitude),
            MinLevel = source.MinLevel,
            MaxLevel = source.MaxLevel,
            MinAltitude = source.MinAltitude,
            MaxAltitude = source.MaxAltitude,
            DistanceFromCenter = CloneFloatRange(source.DistanceFromCenter),
            MinDistanceFromCenter = source.MinDistanceFromCenter,
            MaxDistanceFromCenter = source.MaxDistanceFromCenter,
            Biomes = CloneStringList(source.Biomes),
            ResolvedBiomeMask = source.ResolvedBiomeMask,
            Locations = CloneStringList(source.Locations),
            TimeOfDay = CloneTimeOfDay(source.TimeOfDay),
            RequiredEnvironments = CloneStringList(source.RequiredEnvironments),
            RequiredGlobalKeys = CloneStringList(source.RequiredGlobalKeys),
            ForbiddenGlobalKeys = CloneStringList(source.ForbiddenGlobalKeys),
            States = CloneStringList(source.States),
            Factions = CloneStringList(source.Factions),
            InForest = source.InForest,
            InDungeon = source.InDungeon,
            InsidePlayerBase = source.InsidePlayerBase
        };
    }

    internal static CharacterDropDefinition? CloneCharacterDropDefinition(CharacterDropDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new CharacterDropDefinition
        {
            Drops = CloneList(source.Drops, CloneCharacterDropEntryDefinition)
        };
    }

    private static CharacterDropEntryDefinition CloneCharacterDropEntryDefinition(CharacterDropEntryDefinition source)
    {
        return new CharacterDropEntryDefinition
        {
            Item = source.Item,
            Amount = CloneIntRange(source.Amount),
            AmountMin = source.AmountMin,
            AmountMax = source.AmountMax,
            Chance = source.Chance,
            DontScale = source.DontScale,
            LevelMultiplier = source.LevelMultiplier,
            OnePerPlayer = source.OnePerPlayer,
            AmountLimit = source.AmountLimit,
            DropInStack = source.DropInStack
        };
    }

    internal static SpawnAreaDefinition? CloneSpawnAreaDefinition(SpawnAreaDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new SpawnAreaDefinition
        {
            LevelUpChance = source.LevelUpChance,
            SpawnInterval = source.SpawnInterval,
            TriggerDistance = source.TriggerDistance,
            SetPatrolSpawnPoint = source.SetPatrolSpawnPoint,
            SpawnRadius = source.SpawnRadius,
            NearRadius = source.NearRadius,
            FarRadius = source.FarRadius,
            MaxNear = source.MaxNear,
            MaxTotal = source.MaxTotal,
            MaxTotalSpawns = source.MaxTotalSpawns,
            OnGroundOnly = source.OnGroundOnly,
            Creatures = CloneList(source.Creatures, CloneSpawnAreaSpawnDefinition)
        };
    }

    private static SpawnAreaSpawnDefinition CloneSpawnAreaSpawnDefinition(SpawnAreaSpawnDefinition source)
    {
        return new SpawnAreaSpawnDefinition
        {
            Creature = source.Creature,
            Weight = source.Weight,
            Level = CloneIntRange(source.Level),
            MinLevel = source.MinLevel,
            MaxLevel = source.MaxLevel,
            Faction = source.Faction,
            Data = source.Data,
            Fields = CloneStringDictionary(source.Fields),
            Objects = CloneStringList(source.Objects)
        };
    }

    internal static CreatureSpawnerDefinition? CloneCreatureSpawnerDefinition(CreatureSpawnerDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new CreatureSpawnerDefinition
        {
            Creature = source.Creature,
            TimeOfDay = CloneTimeOfDay(source.TimeOfDay),
            RequiredGlobalKey = source.RequiredGlobalKey,
            BlockingGlobalKey = source.BlockingGlobalKey,
            Level = CloneIntRange(source.Level),
            MinLevel = source.MinLevel,
            MaxLevel = source.MaxLevel,
            LevelUpChance = source.LevelUpChance,
            RespawnTimeMinutes = source.RespawnTimeMinutes,
            SpawnCheckInterval = source.SpawnCheckInterval,
            SpawnGroupId = source.SpawnGroupId,
            SpawnGroupRadius = source.SpawnGroupRadius,
            SpawnerWeight = source.SpawnerWeight,
            MaxGroupSpawned = source.MaxGroupSpawned,
            TriggerDistance = source.TriggerDistance,
            TriggerNoise = source.TriggerNoise,
            RequireSpawnArea = source.RequireSpawnArea,
            AllowInsidePlayerBase = source.AllowInsidePlayerBase,
            WakeUpAnimation = source.WakeUpAnimation,
            SetPatrolSpawnPoint = source.SetPatrolSpawnPoint,
            Faction = source.Faction,
            Data = source.Data,
            Fields = CloneStringDictionary(source.Fields),
            Objects = CloneStringList(source.Objects)
        };
    }

    private static IntRangeDefinition? CloneIntRange(IntRangeDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new IntRangeDefinition
        {
            Min = source.Min,
            Max = source.Max
        };
    }

    private static FloatRangeDefinition? CloneFloatRange(FloatRangeDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new FloatRangeDefinition
        {
            Min = source.Min,
            Max = source.Max
        };
    }

    private static TimeOfDayDefinition? CloneTimeOfDay(TimeOfDayDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new TimeOfDayDefinition
        {
            Values = source.Values?.ToList() ?? new List<string>()
        };
    }

    private static List<T>? CloneList<T>(List<T>? source, System.Func<T, T> cloneItem)
    {
        if (source == null)
        {
            return null;
        }

        List<T> cloned = new(source.Count);
        foreach (T item in source)
        {
            cloned.Add(cloneItem(item));
        }

        return cloned;
    }

    private static List<string>? CloneStringList(List<string>? source)
    {
        return source?.ToList();
    }

    private static Dictionary<string, string>? CloneStringDictionary(Dictionary<string, string>? source)
    {
        return source == null ? null : new Dictionary<string, string>(source);
    }
}
