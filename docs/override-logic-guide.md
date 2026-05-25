# Override Logic Guide

This document explains the current override logic in simple terms for users
writing YAML.

Terms used below:

- `vanilla`: the game's original data
- `custom block`: one YAML block for a prefab/component
- `matching block`: a block whose `conditions` / `cllc` pass right now

## Character

Current rule:

1. If no custom `characterDrop` block matches, use vanilla drops.
2. If one or more custom `characterDrop` blocks match, vanilla drops are not used.
3. All matching custom blocks contribute their `drops[]`.
4. Exact duplicate rows are deduped.
5. Different rows coexist.

### Important

For `character`, conditions are only allowed at the top level of the entry.

This is valid:

```yaml
- prefab: Boar
  conditions:
    biomes: [Swamp]
  characterDrop:
    drops:
    - item: Resin
```

This is not supported:

```yaml
- prefab: Boar
  characterDrop:
    drops:
    - item: Resin
```

### Character Examples

Example A:

```yaml
- prefab: Boar
  conditions:
    biomes: [Swamp]
  characterDrop:
    drops:
    - item: Resin
```

Behavior:

- Swamp: `Resin`
- anywhere else: vanilla

Example B:

```yaml
- prefab: Boar
  characterDrop:
    drops:
    - item: Stone

- prefab: Boar
  conditions:
    biomes: [Swamp]
  characterDrop:
    drops:
    - item: Resin
```

Behavior:

- Swamp: `Stone + Resin`
- anywhere else: `Stone`

Reason:

- the first block matches everywhere
- at least one custom block matches, so vanilla is replaced
- matching custom rows are merged together

## Object

Agreed user-facing rule for object components:

1. `conditions` are only allowed at the top level of the entry.
2. Everything below that level is payload only.
3. If no custom block for that component matches, use the vanilla component state.
4. If one or more custom drop-table blocks match, vanilla rows for that table are replaced.
5. Matching custom rows are merged together.
6. Exact duplicate rows are deduped.
7. Different rows coexist.
8. Scalar fields use `later-wins`.

### Allowed Condition Locations

Allowed:

- top-level `conditions`

Not allowed:

- `dropOnDestroyed.drops[].conditions`
- `dropOnDestroyed.conditions`
- `container.conditions`
- `mineRock.conditions`
- `destructible.conditions`
- `pickable.conditions`
- `pickableItem.conditions`
- `fish.conditions`

If conditions differ, write another top-level entry for the same `prefab`.

### Object Drop-Table Example

```yaml
- prefab: Rock_4
  dropOnDestroyed:
    drops:
    - item: Resin
      stack: 1~2
      weight: 1

- prefab: Rock_4
  conditions:
    biomes: [Meadows]
  dropOnDestroyed:
    drops:
    - item: Wood
      stack: 1~2
      weight: 1

- prefab: Rock_4
  conditions:
    biomes: [Swamp]
  dropOnDestroyed:
    drops:
    - item: Coal
      stack: 1~2
      weight: 1
```

Behavior:

- Meadows: `Resin + Wood`
- Swamp: `Resin + Coal`
- other biomes: `Resin`

Reason:

- the first block always matches, so it acts as a custom default block
- matching custom rows are merged
- vanilla rows are not used because a custom block matched

If the conditionless `Resin` block is removed, then:

- Meadows: `Wood`
- Swamp: `Coal`
- other biomes: vanilla

That is still `replace`, not `vanilla + conditional add`.

### Scalar Example

```yaml
- prefab: woodwall
  destructible:
    health: 100

- prefab: woodwall
  conditions:
    biomes: [BlackForest]
  destructible:
    health: 200
```

Behavior:

- outside BlackForest: `health = 100`
- inside BlackForest: `health = 200`

Reason:

- `health` is a scalar field
- scalar fields use `later-wins`

If the order is reversed, the last matching block wins instead.

## Writing Tips

- If you need different conditions, split them into separate top-level entries.
- Put generic scalar rules first and more specific scalar rules later.
- For drop tables, think in terms of matching custom rows being merged together.
- For character, a conditionless custom block becomes the new custom default.
- For object, a conditionless custom entry becomes the new custom default for every populated component in that entry.
