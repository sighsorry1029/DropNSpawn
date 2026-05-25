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

    internal static DespawnDefinition? CloneDespawnDefinition(DespawnDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new DespawnDefinition
        {
            Range = source.Range,
            Delay = source.Delay,
            Refunds = CloneList(source.Refunds, CloneDespawnRefundEntryDefinition)
        };
    }

    internal static DespawnRefundEntryDefinition CloneDespawnRefundEntryDefinition(DespawnRefundEntryDefinition source)
    {
        return new DespawnRefundEntryDefinition
        {
            Item = source.Item,
            Amount = source.Amount
        };
    }

    internal static BossTamedPressureDefinition? CloneBossTamedPressureDefinition(BossTamedPressureDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new BossTamedPressureDefinition
        {
            BossPrefabs = CloneStringList(source.BossPrefabs),
            ExcludedBossPrefabs = CloneStringList(source.ExcludedBossPrefabs),
            Targets = CloneBossTamedPressureTargetsDefinition(source.Targets),
            Pressure = CloneBossTamedPressurePressureDefinition(source.Pressure),
            Message = source.Message,
            MessageInterval = source.MessageInterval
        };
    }

    internal static BossTamedPressureTargetsDefinition? CloneBossTamedPressureTargetsDefinition(BossTamedPressureTargetsDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new BossTamedPressureTargetsDefinition
        {
            Range = source.Range,
            ScanInterval = source.ScanInterval,
            MaxPerBoss = source.MaxPerBoss,
            ExcludedTamedPrefabs = CloneStringList(source.ExcludedTamedPrefabs),
            ExtraPressuredPrefabs = CloneStringList(source.ExtraPressuredPrefabs)
        };
    }

    internal static BossTamedPressurePressureDefinition? CloneBossTamedPressurePressureDefinition(BossTamedPressurePressureDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new BossTamedPressurePressureDefinition
        {
            DamageInterval = source.DamageInterval,
            DamagePercentPerSecond = source.DamagePercentPerSecond,
            DamageMinBaseHealth = source.DamageMinBaseHealth,
            IncomingDamageMultiplier = source.IncomingDamageMultiplier,
            OutgoingDamageMultiplier = source.OutgoingDamageMultiplier
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

    internal static LocationOfferingBowlDefinition? CloneLocationOfferingBowlDefinition(LocationOfferingBowlDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new LocationOfferingBowlDefinition
        {
            Name = source.Name,
            UseItemText = source.UseItemText,
            UsedAltarText = source.UsedAltarText,
            CantOfferText = source.CantOfferText,
            WrongOfferText = source.WrongOfferText,
            IncompleteOfferText = source.IncompleteOfferText,
            BossItem = source.BossItem,
            BossItems = source.BossItems,
            BossPrefab = source.BossPrefab,
            ItemPrefab = source.ItemPrefab,
            SetGlobalKey = source.SetGlobalKey,
            RenderSpawnAreaGizmos = source.RenderSpawnAreaGizmos,
            AlertOnSpawn = source.AlertOnSpawn,
            SpawnBossDelay = source.SpawnBossDelay,
            SpawnBossDistance = CloneFloatRange(source.SpawnBossDistance),
            SpawnBossMaxDistance = source.SpawnBossMaxDistance,
            SpawnBossMinDistance = source.SpawnBossMinDistance,
            SpawnBossMaxYDistance = source.SpawnBossMaxYDistance,
            GetSolidHeightMargin = source.GetSolidHeightMargin,
            EnableSolidHeightCheck = source.EnableSolidHeightCheck,
            SpawnPointClearingRadius = source.SpawnPointClearingRadius,
            SpawnYOffset = source.SpawnYOffset,
            UseItemStands = source.UseItemStands,
            ItemStandPrefix = source.ItemStandPrefix,
            ItemStandMaxRange = source.ItemStandMaxRange,
            RespawnMinutes = source.RespawnMinutes,
            Data = source.Data,
            Fields = CloneStringDictionary(source.Fields),
            Objects = CloneStringList(source.Objects)
        };
    }

    internal static LocationVegvisirDefinition CloneLocationVegvisirDefinition(LocationVegvisirDefinition source)
    {
        return new LocationVegvisirDefinition
        {
            Path = source.Path,
            ExpectedLocations = CloneStringList(source.ExpectedLocations),
            Name = source.Name,
            UseText = source.UseText,
            HoverName = source.HoverName,
            SetsGlobalKey = source.SetsGlobalKey,
            SetsPlayerKey = source.SetsPlayerKey,
            Locations = CloneList(source.Locations, CloneLocationVegvisirTargetDefinition)
        };
    }

    internal static LocationRunestoneDefinition CloneLocationRunestoneDefinition(LocationRunestoneDefinition source)
    {
        return new LocationRunestoneDefinition
        {
            Path = source.Path,
            ExpectedLocationName = source.ExpectedLocationName,
            ExpectedLabel = source.ExpectedLabel,
            ExpectedTopic = source.ExpectedTopic,
            Name = source.Name,
            Topic = source.Topic,
            Label = source.Label,
            Text = source.Text,
            RandomTexts = CloneList(source.RandomTexts, CloneLocationRunestoneTextDefinition),
            LocationName = source.LocationName,
            PinName = source.PinName,
            PinType = source.PinType,
            ShowMap = source.ShowMap,
            Chance = source.Chance
        };
    }

    internal static LocationRunestoneGlobalPinsDefinition? CloneLocationRunestoneGlobalPinsDefinition(LocationRunestoneGlobalPinsDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new LocationRunestoneGlobalPinsDefinition
        {
            TargetLocations = CloneList(source.TargetLocations, CloneLocationRunestoneGlobalPinTargetDefinition)
        };
    }

    private static LocationRunestoneGlobalPinTargetDefinition CloneLocationRunestoneGlobalPinTargetDefinition(LocationRunestoneGlobalPinTargetDefinition source)
    {
        return new LocationRunestoneGlobalPinTargetDefinition
        {
            LocationName = source.LocationName,
            Chance = source.Chance,
            SourceBiomes = CloneStringList(source.SourceBiomes),
            PinName = source.PinName,
            PinType = source.PinType
        };
    }

    internal static LocationItemStandDefinition CloneLocationItemStandDefinition(LocationItemStandDefinition source)
    {
        return new LocationItemStandDefinition
        {
            Path = source.Path,
            Name = source.Name,
            CanBeRemoved = source.CanBeRemoved,
            AutoAttach = source.AutoAttach,
            OrientationType = source.OrientationType,
            SupportedTypes = CloneStringList(source.SupportedTypes),
            SupportedItems = CloneStringList(source.SupportedItems),
            UnsupportedItems = CloneStringList(source.UnsupportedItems),
            PowerActivationDelay = source.PowerActivationDelay,
            GuardianPower = source.GuardianPower
        };
    }

    private static LocationVegvisirTargetDefinition CloneLocationVegvisirTargetDefinition(LocationVegvisirTargetDefinition source)
    {
        return new LocationVegvisirTargetDefinition
        {
            LocationName = source.LocationName,
            PinName = source.PinName,
            PinType = source.PinType,
            DiscoverAll = source.DiscoverAll,
            ShowMap = source.ShowMap,
            Weight = source.Weight
        };
    }

    private static LocationRunestoneTextDefinition CloneLocationRunestoneTextDefinition(LocationRunestoneTextDefinition source)
    {
        return new LocationRunestoneTextDefinition
        {
            Topic = source.Topic,
            Label = source.Label,
            Text = source.Text
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
