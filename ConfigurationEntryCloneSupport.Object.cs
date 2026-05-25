using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class ConfigurationEntryCloneSupport
{
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
