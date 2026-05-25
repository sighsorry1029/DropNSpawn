# Domain Coexistence Design

## Goal

Support multiple rules for the same prefab without regressing startup cost, network cost, or runtime predictability.

This design replaces the current `prefab -> single active entry` model with domain-specific grouped rule sets.

Target behavior:

- exact duplicate rule: later one wins
- non-identical rule: coexists
- runtime starts from snapshot/default state and replays all matching rules in order

This is intentionally not a single universal rule for every domain. Each domain needs its own coexistence unit.

## Core Principles

### 1. Coexistence Unit Must Be The Smallest Safe Unit

Do not force all domains into `top-level YAML row coexistence`.

Use the narrowest unit that can coexist without ambiguous field conflicts:

- `character`: drop row
- `object`: component block or drop row
- `spawnsystem`: top-level spawn row
- `spawner`: selector-scoped spawner block, plus nested spawn rows for `SpawnArea`
- `location`: component rule block

### 2. Exact Duplicate Only

Later rules replace earlier rules only when they are exact duplicates of the same unit.

Recommended rule identity:

- explicit `id` if present
- otherwise normalized fingerprint of the rule body

`enabled: false` should only tombstone the same rule identity, not the whole prefab.

### 3. Replay From Baseline

For live application:

1. restore native/upstream snapshot or live baseline
2. gather matching rules in file order
3. replay them in order
4. for scalar fields, later matching rule wins
5. for list rows, coexist unless the row identity matches

This preserves determinism and keeps the model compatible with the startup/runtime refactors already done.

## Domain Mapping

### Character

Best matching external model: `DropThat`

`DropThat` does not treat top-level duplicate prefab sections as coexisting. It treats a creature as one template with multiple conditional drop rows.

That is also the best fit for `DropNSpawn`.

#### Target internal model

```csharp
Dictionary<string, CharacterPrefabRuleSet>

sealed class CharacterPrefabRuleSet
{
    public string Prefab;
    public List<CharacterDropBlock> Blocks;
}

sealed class CharacterDropBlock
{
    public string RuleKey;
    public int Order;
    public bool Enabled;
    public ConditionsDefinition? BlockConditions;
    public List<CharacterDropRow> Drops;
}

sealed class CharacterDropRow
{
    public string RuleKey;
    public int Order;
    public bool Enabled;
    public ConditionsDefinition? Conditions;
    public CharacterDropEntryDefinition Payload;
}
```

#### YAML meaning

Multiple top-level entries for the same `prefab` are allowed.

Loader behavior:

- group them into one `CharacterPrefabRuleSet`
- each top-level entry becomes one `CharacterDropBlock`
- duplicate block identity: later wins
- inside a block, duplicate drop row identity: later wins

#### Runtime behavior

1. restore snapshot/default drops
2. collect all blocks whose block conditions match
3. collect all drop rows from those blocks whose row conditions match
4. append all matching rows into the effective drop table
5. if two matching rows have the same row identity, later wins

#### Consequence

If two different `Wood` rows both match, both are active.

That means:

- different conditions can coexist
- identical rows do not duplicate if they share the same identity

This is the cleanest interpretation of "different rules coexist, exact duplicates do not."

### Object

Best matching external model: `DropThat`

The object domain should not be a single flat `prefab -> one override` map.

Different object components have different coexistence units.

#### Target internal model

```csharp
Dictionary<string, ObjectPrefabRuleSet>

sealed class ObjectPrefabRuleSet
{
    public string Prefab;
    public List<DropTableBlock> DropOnDestroyedBlocks;
    public List<DropTableBlock> ContainerBlocks;
    public List<DropTableBlock> MineRockBlocks;
    public List<DropTableBlock> TreeBaseBlocks;
    public List<DropTableBlock> TreeLogBlocks;
    public List<PickableBlock> PickableBlocks;
    public List<PickableItemBlock> PickableItemBlocks;
    public List<DestructibleBlock> DestructibleBlocks;
    public List<FishBlock> FishBlocks;
}
```

#### YAML meaning

Multiple top-level entries for the same `prefab` are allowed.

Each matching top-level entry contributes blocks for any populated component section.

Duplicate rule identity is checked per component block, not only per prefab.

#### Runtime behavior

1. restore object snapshot/default
2. for each component type, gather matching blocks in order
3. replay them in order

For scalar component fields:

- later matching block wins per field

For drop-table-like sections:

- matching item rows coexist
- exact duplicate row identity is replaced by the later row

This keeps object drop logic close to `DropThat` while allowing live-object replay from baseline.

### SpawnSystem

Best matching external model: `SpawnThat WorldSpawner`

This domain should be true row coexistence.

#### Target internal model

```csharp
List<SpawnSystemRow>

sealed class SpawnSystemRow
{
    public string RuleKey;
    public int Order;
    public bool Enabled;
    public SpawnSystemConfigurationEntry Payload;
}
```

#### YAML meaning

Each top-level row is one world-spawn row.

Recommended schema:

- add optional `id`
- `RuleKey = id ?? normalized row fingerprint`

#### Merge behavior

- same rule identity: later wins
- `enabled: false` with same rule identity: tombstone
- different identity: coexist

#### Runtime behavior

Build the final authoritative spawn table from all enabled rows, in order.

This is the same shape as `SpawnThat WorldSpawner`, where coexistence is row-based instead of prefab-based.

### Spawner

Best matching external model: hybrid of `SpawnThat LocalSpawner` and `SpawnThat SpawnAreaSpawner`

This domain should not use simple top-level prefab coexistence.

It must split by selector and component kind.

#### Target selector key

```text
selectorKey = prefab + "|" + location + "|" + path + "|" + componentKind
```

Where `componentKind` is:

- `SpawnArea`
- `CreatureSpawner`

#### Target internal model

```csharp
Dictionary<string, SpawnerRuleSet>

sealed class SpawnerRuleSet
{
    public string SelectorKey;
    public List<SpawnAreaBlock> SpawnAreaBlocks;
    public List<CreatureSpawnerBlock> CreatureSpawnerBlocks;
}
```

#### CreatureSpawner semantics

`CreatureSpawner` has one live component state.

So coexistence is:

- multiple matching blocks may exist
- replay all matching blocks in order
- later matching block wins per scalar field
- exact duplicate block identity: later wins

This is close to `SpawnThat LocalSpawner`, but extended from "best match one template" to "ordered replay of matching blocks."

#### SpawnArea semantics

`SpawnArea` is different:

- top-level block fields are scalar and replay in order
- nested `creatures[]` rows are the real coexistence unit

Recommended internal structure:

```csharp
sealed class SpawnAreaBlock
{
    public string RuleKey;
    public int Order;
    public bool Enabled;
    public ConditionsDefinition? BlockConditions;
    public SpawnAreaScalarOverrides Scalars;
    public List<SpawnAreaCreatureRow> Creatures;
}
```

Runtime:

1. restore baseline
2. replay matching scalar overrides in order
3. gather matching `creatures[]` rows from all matching blocks
4. build final `m_prefabs` list from those rows
5. exact duplicate creature-row identity: later wins

This keeps `SpawnArea` close to `SpawnThat SpawnAreaSpawner`, where spawn-slot rows coexist inside one spawner template.

### Location

Best matching external model: DNS-specific hybrid

This domain is not well represented by `DropThat` or `SpawnThat`.

The safe coexistence unit is the component rule block, not the top-level location prefab entry.

#### Target internal model

```csharp
Dictionary<string, LocationRuleSet>

sealed class LocationRuleSet
{
    public string Prefab;
    public List<OfferingBowlBlock> OfferingBowls;
    public List<ItemStandBlock> ItemStands;
    public List<VegvisirBlock> Vegvisirs;
}
```

Recommended future selectors for component blocks:

- `path`
- `name`
- `ordinal`

#### YAML meaning

Multiple top-level entries for the same location `prefab` are allowed.

Each entry contributes component blocks.

Duplicate identity is checked per component block, not only per prefab.

#### Runtime behavior

1. restore live location baseline
2. gather matching component blocks in order
3. apply them in order

For scalar component fields:

- later matching block wins per field

For list-valued component fields:

- explicit whole-field replace semantics are safer than implicit append

This keeps the interaction/UI-facing logic deterministic on clients.

## Network And Delta Implications

Current `prefab`-keyed delta transport is not sufficient for coexistence models.

It must move to rule-level keys.

### Required transport keys

- `character`: `prefab|characterDrop|blockOrRowRuleKey`
- `object`: `prefab|componentKind|blockOrRowRuleKey`
- `spawnsystem`: `ruleKey`
- `spawner`: `selectorKey|componentKind|blockOrRowRuleKey`
- `location`: `prefab|componentKind|blockRuleKey`

### Canonical payload form

Transport should publish canonical grouped payloads:

- sorted by prefab/selector
- then by file order
- then by rule order

Manifest hashes must be computed from this canonical grouped form, not from the old single-entry maps.

## Recommended Schema Changes

Legacy compatibility is not required, so the cleanest path is to add explicit rule identities.

### Add optional `id` everywhere

Recommended:

- `character` top-level block `id`
- `characterDrop.drops[]` row `id`
- `object` component block `id`
- `spawnsystem` row `id`
- `spawner` block `id`
- `spawnArea.creatures[]` row `id`
- `location` component block `id`

Without `id`, fallback to normalized fingerprint.

With `id`, users gain:

- exact tombstones
- stable delta keys
- precise override intent

## Migration Strategy

### Phase 1

Implement coexistence for `character`.

Reason:

- smallest surface area
- closest external model already exists in `DropThat`
- easiest domain to validate

### Phase 2

Implement coexistence for `object`.

Reason:

- similar drop-row logic
- same grouped-by-prefab pattern

### Phase 3

Implement `spawnsystem` rule-keyed rows.

Reason:

- already list-based
- transport changes are straightforward

### Phase 4

Implement `spawner`.

Reason:

- selector key and nested creature-row coexistence are more complex

### Phase 5

Implement `location`.

Reason:

- most component-specific live replay behavior
- highest risk of client-visible regressions

## Recommendation

Do not make every domain "top-level row coexistence."

Instead:

- `character` and `object`: use `DropThat`-style grouped templates with conditional inner rows
- `spawnsystem`: use `SpawnThat WorldSpawner`-style row coexistence
- `spawner`: use selector-scoped blocks plus nested row coexistence
- `location`: use component-block coexistence

This gives the user-facing behavior of "different rules can coexist" without introducing ambiguous field conflicts or losing deterministic runtime replay.
