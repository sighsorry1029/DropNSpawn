# Object Domain Schema And Runtime Spec

This document fixes the intended schema and runtime behavior for the `object`
domain after moving activation conditions to the top level of each entry.

## Goals

- Keep condition evaluation simple and predictable.
- Make object overrides follow the same mental model as `character`:
  conditions select blocks, payload stays conditionless.
- Keep object-specific component replay semantics where they are a better fit
  than flat top-level row coexistence.

## Core Rule

`conditions` are only allowed at the top level of an object entry.

Everything below that level is payload only.

This means:

- allowed: top-level `conditions`
- not allowed: `dropOnDestroyed.drops[].conditions`
- not allowed: `dropOnDestroyed.drops[].cllc`
- not allowed: `dropOnDestroyed.conditions`
- not allowed: `container.conditions`
- not allowed: `mineRock.conditions`
- not allowed: `destructible.conditions`
- not allowed: `pickable.conditions`
- not allowed: `pickableItem.conditions`
- not allowed: `fish.conditions`
- not allowed: `pickable.extraDrops.conditions`
- not allowed: `pickable.extraDrops.drops[].conditions`
- not allowed: `fish.extraDrops.conditions`
- not allowed: `fish.extraDrops.drops[].cllc`

If conditions differ, write another top-level entry for the same `prefab`.

## Canonical YAML Shape

```yaml
- prefab: SomePrefab
  enabled: true
  conditions: ...

  dropOnDestroyed:
    rolls: 1~3
    dropChance: 1
    oneOfEach: false
    drops:
    - item: Wood
      stack: 1~2
      weight: 1
      dontScale: false

  mineRock:
    health: 1000
    minToolTier: 1
    rolls: 1~3
    dropChance: 1
    oneOfEach: false
    drops:
    - item: Stone
      stack: 1~3
      weight: 1
      dontScale: false

  container:
    rolls: 1~3
    dropChance: 1
    oneOfEach: false
    drops:
    - item: Coins
      stack: 10~20
      weight: 1
      dontScale: false

  destructible:
    health: 80
    minToolTier: 0
    destructibleType: Default
    spawnWhenDestroyed: SomePrefab

  pickable:
    overrideName: Custom Name
    drop:
      item: Mushroom
      amount: 1
      minAmountScaled: 0
      dontScale: false
    extraDrops:
      rolls: 1~3
      dropChance: 1
      oneOfEach: false
      drops:
      - item: Resin
        stack: 1~2
        weight: 1
        dontScale: false

  pickableItem:
    randomDrops:
    - item: Raspberry
      stack: 1~2
      weight: 1
    drop:
      item: Raspberry
      stack: 1

  fish:
    extraDrops:
      rolls: 1~3
      dropChance: 1
      oneOfEach: false
      drops:
      - item: TrophyFish
        stack: 1
        weight: 1
        dontScale: false
```

## Component Blocks

Each top-level entry is an independent rule block.

All populated component blocks inside that entry share the same top-level
`conditions`.

Supported block kinds:

- `dropOnDestroyed`
- `mineRock`
- `mineRock5`
- `treeBase`
- `treeLog`
- `container`
- `destructible`
- `pickable`
- `pickableItem`
- `fish`

Top-level entries for the same `prefab` may coexist.

Each entry may contribute any subset of these blocks.

## Internal Model

Recommended internal model:

```csharp
Dictionary<string, ObjectPrefabRuleSet>

sealed class ObjectPrefabRuleSet
{
    public string Prefab;
    public List<DropTableBlock> DropOnDestroyedBlocks;
    public List<DropTableBlock> ContainerBlocks;
    public List<DamageableDropTableBlock> MineRockBlocks;
    public List<DamageableDropTableBlock> MineRock5Blocks;
    public List<DamageableDropTableBlock> TreeBaseBlocks;
    public List<DamageableDropTableBlock> TreeLogBlocks;
    public List<DestructibleBlock> DestructibleBlocks;
    public List<PickableBlock> PickableBlocks;
    public List<PickableItemBlock> PickableItemBlocks;
    public List<FishBlock> FishBlocks;
}
```

Recommended payload split:

```csharp
sealed class DropTablePayloadDefinition
{
    public IntRangeDefinition? Rolls;
    public int? DropMin;
    public int? DropMax;
    public float? DropChance;
    public bool? OneOfEach;
    public List<DropEntryDefinition>? Drops;
}

sealed class DropTableDefinition : DropTablePayloadDefinition
{
    public ConditionsDefinition? Conditions;
    public CllcDefinition? Cllc;
}

sealed class DamageableDropTableDefinition : DropTableDefinition
{
    public float? Health;
    public int? MinToolTier;
}
```

`DropEntryDefinition` is payload only:

```csharp
sealed class DropEntryDefinition
{
    public string Item;
    public IntRangeDefinition? Stack;
    public int? StackMin;
    public int? StackMax;
    public float? Weight;
    public bool? DontScale;
}
```

`pickable.extraDrops` and `fish.extraDrops` are `DropTablePayloadDefinition`,
not conditional blocks.

## Merge Rules

For the same `prefab`, object rules merge by component kind, while activation
still happens per whole entry.

### Block matching

A component block matches when:

- the block exists
- the top-level entry is enabled
- the instance is eligible for object reconciliation
- the top-level entry `conditions` pass, or the entry has no conditions

### Block order

Use stable file order after load/normalization.

Object logic is intentionally replay-oriented.

### Scalar components

For scalar component fields:

- replay matching blocks in order
- later matching block wins per field

Applies to:

- `destructible.health`
- `destructible.minToolTier`
- `destructible.destructibleType`
- `destructible.spawnWhenDestroyed`
- `pickable.drop.item`
- `pickable.drop.amount`
- `pickable.drop.minAmountScaled`
- `pickable.drop.dontScale`
- `pickable.overrideName`
- `pickableItem.drop`
- `pickableItem.randomDrops`
- damageable `health`
- damageable `minToolTier`

### Drop-table blocks

For drop-table-like blocks:

- restore vanilla snapshot for that component first
- gather matching custom blocks for that component kind
- if no custom block matches: keep vanilla snapshot table
- if one or more custom blocks match: custom mode starts

In custom mode:

- vanilla rows are replaced
- matching custom rows coexist
- exact duplicate rows are deduped
- different rows coexist
- `rolls`, `dropChance`, and `oneOfEach` use later-wins

This applies to:

- `dropOnDestroyed`
- `container`
- `mineRock`
- `mineRock5`
- `treeBase`
- `treeLog`
- `pickable.extraDrops`
- `fish.extraDrops`

## Drop Row Identity

Without explicit ids, exact duplicate detection is based on normalized row body:

- `item`
- normalized stack min/max
- normalized `weight`
- normalized `dontScale`

If any of those differ, rows coexist.

## Runtime Steps

For a live object instance:

1. restore snapshot/default state for all supported components
2. for each component kind, gather matching blocks for that instance
3. apply scalar fields by replay, later-wins
4. apply drop-table fields using drop-table custom-mode rules
5. update runtime-only state when required by that component

## Pickable And Fish

`pickable` and `fish` follow the same rule as every other component block:

- top-level entry `conditions` decide whether the whole block is active
- `extraDrops` is pure payload
- `extraDrops` never has its own conditions

If you want different `extraDrops` under different conditions, create another
top-level entry for the same `prefab`.

## Examples

### Example 1

```yaml
- prefab: Rock_4
  dropOnDestroyed:
    rolls: 1
    dropChance: 1
    oneOfEach: false
    drops:
    - item: Stone
      stack: 2~4

- prefab: Rock_4
  conditions:
    biomes: [Mountain]
  dropOnDestroyed:
    drops:
    - item: Crystal
      stack: 1

- prefab: Rock_4
  conditions:
    requiredEnvironments: [Rain]
  dropOnDestroyed:
    drops:
    - item: Flint
      stack: 1~2
```

Behavior:

- plains, clear: `Stone`
- mountain, clear: `Stone + Crystal`
- mountain, rain: `Stone + Crystal + Flint`

If the conditionless `Stone` block is removed, then:

- plains, clear: vanilla
- mountain, clear: `Crystal`
- mountain, rain: `Crystal + Flint`

### Example 2

```yaml
- prefab: Pickable_Mushroom
  pickable:
    overrideName: Berry Bush
    drop:
      item: Mushroom
    extraDrops:
      drops:
      - item: Mushroom
        stack: 1

- prefab: Pickable_Mushroom
  conditions:
    requiredEnvironments: [Rain]
  pickable:
    extraDrops:
      drops:
      - item: RoyalJelly
        stack: 1
```

Behavior:

- normal weather: `Mushroom`
- rain: `Mushroom + RoyalJelly`

## Unsupported Legacy Shapes

These shapes are intentionally unsupported:

- nested row conditions in any object drop table
- nested `extraDrops.conditions`
- nested `extraDrops.cllc`
- nested row `cllc` under any object drop table

Those cases must be expressed as another top-level entry for the same `prefab`.
