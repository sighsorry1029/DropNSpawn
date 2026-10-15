# YAML Null And Empty Values Guide

This guide explains how `DropNSpawn` interprets:

- omitted fields
- `null`
- `[]`
- `{}`
- `''`

The short version:

- `null` and omitting a field usually mean the field is unspecified
- `[]`, `{}`, and `''` explicitly assign an empty list, empty object, or empty string
- the effect of those values still depends on the field

## Value Shapes

Most YAML values used by this mod fall into one of these shapes:

- `scalar`
  - single values such as strings, booleans, numbers, ranges, or `null`
- `list`
  - arrays such as `biomes`, `drops`, `requiredEnvironments`
- `map` / `object`
  - nested named fields such as `conditions`, `drop`, `modifiers`, `fields`

Examples:

```yaml
someScalar: 1
someScalarNull:
someList: [Meadows, BlackForest]
someListEmpty: []
someObject:
  enabled: true
someObjectEmpty: {}
someEmptyString: ''
```

## Omitted Field vs `null`

In current `DropNSpawn` YAML binding, these are usually treated the same:

```yaml
field:
```

```yaml
field: null
```

and:

- omitting `field` entirely

In practice, all three usually mean:

- the field is unspecified

What happens next depends on the domain and field:

- some fields keep the current/native value
- some conditions are simply not checked
- some `spawnsystem` fields fall back to native/default row behavior because that domain rebuilds rows authoritatively

## Empty List, Empty Object, Empty String

These are explicit values, not the same as omission:

```yaml
listField: []
objectField: {}
stringField: ''
```

They mean:

- `[]` = empty list
- `{}` = empty object/map
- `''` = empty string

These values are explicit inputs and are not the same as `null`.
Some fields intentionally treat them like omission: Event `settings: []`,
`standalone: []`, `conditions.playerBase: []` and `conditions.players: []` are
unspecified tuples, rather than requests to clear earlier values.

## Important Field-Type Rule

As a mental model:

- if a field normally contains `- item` style entries, it is a list, so use `[]` for empty
- if a field normally contains named child fields, it is a map/object, so use `{}`
- otherwise it is usually a scalar

Examples:

```yaml
biomes: []
conditions: {}
requiredGlobalKey: ''
```

## Field-Dependent Meaning

The same empty-looking value can mean different things depending on the field.

### Example: `conditions.biomes`

For Character/Object rule selectors, these all effectively mean "do not filter by biome":

```yaml
conditions:
  biomes:
```

```yaml
conditions:
  biomes: []
```

```yaml
conditions: {}
```

or just omitting `biomes`

Reason:

- this is a selector field
- an empty or missing biome list means there is no biome restriction

Event conditions are different: omitted/`null` `biomes` preserves the event's
previous biome condition; `biomes: []` explicitly removes that restriction.

### Example: `drops: []`

This is different:

```yaml
characterDrop:
  drops: []
```

or:

```yaml
dropOnDestroyed:
  drops: []
```

This does not mean "field missing".

It means:

- an explicit empty drop list

That usually acts like:

- "replace the matching drop rows with nothing"
- effectively disabling that drop output for the matching block

### Example: `requiredGlobalKey`

For string fields that support clearing, `''` is important:

```yaml
requiredGlobalKey: ''
```

This is different from:

```yaml
requiredGlobalKey:
```

or omitting the field

For fields such as `creatureSpawner.requiredGlobalKey`:

- `''` = explicitly clear the native value
- `null` or omitted = leave it unspecified, which usually means keep the current value

## Practical Rules

Use these rules when editing YAML:

- omit a field or use `null` when you want it to stay unspecified
- use `[]` when you want an explicitly empty list
- use `{}` when you want an explicitly empty object/map
- use `''` when you want an explicitly empty string
- do not assume that `null` always means "reset to default"
- do not assume that `[]` always means "ignore this field"

## Domain Note

For Character `dropInStack`, omitted/`null` inherits the global stack default;
`false` explicitly keeps singles, and `true` enables stacks unless blacklisted.

For Event definitions, supported empty lists (biomes, keys, known items,
environments, commands and spawns) clear that field. `messages: []` clears both
messages; `messages: ['', 'end']` preserves the empty first tuple position.
`forceEnvironment: ''` and `forceMusic: ''` clear their values. Omitted/`null`
preserves earlier values, including metadata from another definition of the same
event. See the [override guide](override-logic-guide.md#events) for tuple exceptions.

For most domains:

- omitted or `null` usually behaves like "do not override this field"

For `spawnsystem`:

- the domain rebuilds rows authoritatively
- omitted or `null` often means "leave that row field at native/default row behavior"

So the general mental model is still valid, but `spawnsystem` is the domain where "unspecified" most often becomes "native rebuilt value" rather than "keep the existing live object field".
