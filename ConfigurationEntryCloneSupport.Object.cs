using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class ConfigurationEntryCloneSupport
{
    internal static PrefabConfigurationEntry ClonePrefabConfigurationEntry(PrefabConfigurationEntry source)
    {
        return new PrefabConfigurationEntry
        {
            RuleId = source.RuleId,
            Prefab = source.Prefab,
            Enabled = source.Enabled,
            Conditions = CloneConditions(source.Conditions),
            DropOnDestroyed = CloneDropTableDefinition(source.DropOnDestroyed),
            MineRock = CloneDamageableDropTableDefinition(source.MineRock),
            MineRock5 = CloneDamageableDropTableDefinition(source.MineRock5),
            TreeBase = CloneDamageableDropTableDefinition(source.TreeBase),
            TreeLog = CloneDamageableDropTableDefinition(source.TreeLog),
            Container = CloneDropTableDefinition(source.Container),
            PickableItem = ClonePickableItemDefinition(source.PickableItem),
            Pickable = ClonePickableDefinition(source.Pickable),
            Fish = CloneFishDefinition(source.Fish),
            Destructible = CloneDestructibleDefinition(source.Destructible),
            SourcePath = source.SourcePath,
            SourceLine = source.SourceLine,
            SourceColumn = source.SourceColumn
        };
    }

    internal static DropTableDefinition? CloneDropTableDefinition(DropTableDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        DropTableDefinition clone = new();
        CopyDropTablePayload(source, clone);
        return clone;
    }

    internal static DamageableDropTableDefinition? CloneDamageableDropTableDefinition(DamageableDropTableDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        DamageableDropTableDefinition clone = new()
        {
            Health = source.Health,
            MinToolTier = source.MinToolTier
        };
        CopyDropTablePayload(source, clone);
        return clone;
    }

    private static void CopyDropTablePayload(DropTablePayloadDefinition source, DropTablePayloadDefinition target)
    {
        target.Rolls = CloneIntRange(source.Rolls);
        target.DropMin = source.DropMin;
        target.DropMax = source.DropMax;
        target.DropChance = source.DropChance;
        target.OneOfEach = source.OneOfEach;
        target.Drops = CloneList(source.Drops, CloneDropEntryDefinition);
    }

    private static DropEntryDefinition CloneDropEntryDefinition(DropEntryDefinition source)
    {
        return new DropEntryDefinition
        {
            Item = source.Item,
            Stack = CloneIntRange(source.Stack),
            StackMin = source.StackMin,
            StackMax = source.StackMax,
            Weight = source.Weight,
            DontScale = source.DontScale
        };
    }

    internal static DestructibleDefinition? CloneDestructibleDefinition(DestructibleDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new DestructibleDefinition
        {
            Health = source.Health,
            MinToolTier = source.MinToolTier,
            DestructibleType = source.DestructibleType,
            SpawnWhenDestroyed = source.SpawnWhenDestroyed
        };
    }

    internal static PickableDefinition? ClonePickableDefinition(PickableDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new PickableDefinition
        {
            OverrideName = source.OverrideName,
            Drop = ClonePickableDropDefinition(source.Drop),
            ExtraDrops = CloneDropTablePayloadDefinition(source.ExtraDrops)
        };
    }

    private static PickableDropDefinition? ClonePickableDropDefinition(PickableDropDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new PickableDropDefinition
        {
            Item = source.Item,
            Amount = source.Amount,
            MinAmountScaled = source.MinAmountScaled,
            DontScale = source.DontScale
        };
    }

    private static DropTablePayloadDefinition? CloneDropTablePayloadDefinition(DropTablePayloadDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        DropTablePayloadDefinition clone = new();
        CopyDropTablePayload(source, clone);
        return clone;
    }

    internal static PickableItemDefinition? ClonePickableItemDefinition(PickableItemDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new PickableItemDefinition
        {
            RandomDrops = CloneList(source.RandomDrops, CloneRandomPickableItemDefinition),
            Drop = ClonePickableItemDropDefinition(source.Drop)
        };
    }

    private static PickableItemDropDefinition? ClonePickableItemDropDefinition(PickableItemDropDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new PickableItemDropDefinition
        {
            Item = source.Item,
            Stack = source.Stack
        };
    }

    private static RandomPickableItemDefinition CloneRandomPickableItemDefinition(RandomPickableItemDefinition source)
    {
        return new RandomPickableItemDefinition
        {
            Item = source.Item,
            Stack = CloneIntRange(source.Stack),
            StackMin = source.StackMin,
            StackMax = source.StackMax,
            Weight = source.Weight
        };
    }

    internal static FishDefinition? CloneFishDefinition(FishDefinition? source)
    {
        if (source == null)
        {
            return null;
        }

        return new FishDefinition
        {
            ExtraDrops = CloneDropTablePayloadDefinition(source.ExtraDrops)
        };
    }
}
