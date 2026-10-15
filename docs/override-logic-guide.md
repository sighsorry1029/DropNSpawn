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

### Global defaults and explicit values

| Setting / field | Omitted or `null` | Explicit YAML value |
| --- | --- | --- |
| `dropInStack` | Use `global drop in stack` | `true` stacks; `false` keeps singles. The stack blacklist still forces singles. |
| Trophy `levelMultiplier` | Item rows default to `true`; global trophy amount scaling may apply | `false` opts out of both level scaling and global trophy amount scaling. `true` permits the selected global/game policy. |
| `levelMultiplierNotAmount` | `false` for a custom row; untouched original rows keep the game value | With `levelMultiplier: true`, `true` selects chance-only scaling rather than quantity scaling. |
| `amountLimit` | No YAML cap | Nonnegative cap on the final row amount, including `0` to suppress that row. |

Blacklists are global exceptions across original and YAML-driven CharacterDrop:

| Config blacklist | Names identify | Priority and effect |
| --- | --- | --- |
| `global drop in stack blacklist` | Dropped item prefabs | Forces separate delivery even with YAML `dropInStack: true`, including saved ragdoll loot. Requires Character Overrides; read at delivery time. |
| `global trophy level multiplier blacklist` | Trophy item prefabs | Excludes DNS global trophy amount scaling even with YAML `levelMultiplier: true`. Preserves existing level flags and the independent chance-only chance policy. |
| `monster instant loot drop blacklist` | Original victim creature prefabs | Keeps normal ragdoll loot timing; no per-drop YAML field overrides this exception. Applies to newly created ragdolls, independently of Character Overrides. |
| `player-aligned killer character drop blacklist` | Victim creature prefabs | Exempts the creature from the killer requirement; no per-drop YAML field overrides this exception. Other loot rules still apply, independently of Character Overrides. |

Names are case-insensitive and may be separated by commas, semicolons or newlines.
Empty lists mean no exceptions. A trophy blacklist entry differs from YAML
`levelMultiplier: false`, which disables that row's star-based chance and amount
scaling. Matching Character YAML replaces the original drop list, so a blacklist
can exclude one item globally without copying every creature's drop table.

CLLC's runtime handoff disables DNS
amount/chance scaling on both the native and custom drop paths, while YAML drop
definitions still apply. Feature switches and compatibility gates are not defaults
that an arbitrary YAML field can override.

`Enable Character Overrides` gates Character YAML, stack delivery and the nearby
`onePerPlayer` override. It is not a master switch for independent global loot
scaling, instant ragdoll loot or the player-aligned killer filter.

Matching rows still form a union. Omitted and explicit `true` item-level flags
deduplicate when all other effective values match. Omitted `dropInStack` and
explicit `false` now express different policies, so otherwise identical rows with
these values remain separate. Avoid overlapping duplicates unless separate rolls
are intended. No automatic YAML migration is performed.

New ragdolls save the successful rows' explicit stack choices; missing metadata
on older ragdolls means inheritance. Global stack defaults and blacklists are
read at delivery time. YAML changes do not retroactively reroll or rewrite saved
loot. Invalid policy metadata preserves saved items and falls back to inheritance.

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

Conditions nested inside `characterDrop` are not supported. An entry with no
conditions is valid and matches everywhere, as shown in Example B below.

```yaml
- prefab: Boar
  characterDrop:
    conditions:
      biomes: [Swamp]
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

## Events

Definitions for the same event apply in loaded-file/entry order. Only explicitly
supplied fields replace earlier values; unrelated command and condition metadata
is preserved. Event `conditions` are the event's execution conditions, not selectors
that decide whether an override row applies.

Omitted/`null` fields preserve the earlier value. Explicit `[]` clears supported
lists: biomes, global/player/known-item keys, required environments, commands and
spawns. `messages: []` clears both messages; `messages: ['', 'end']` keeps the first
position empty and sets only the end message. Empty strings clear forced music
and environment values. Empty `settings`, `standalone`, `playerBase` and `players`
tuples still mean unspecified; a supplied valid `players` tuple uses distance 100
when its second value is omitted. An invalid player range preserves the prior
range and distance.

Explicit YAML player-base rules override the global default. Explicit durations
bypass a positive global duration multiplier, but a global multiplier of `0`
retains its existing role of disabling events. `Enable Event Overrides` disables
Event YAML, not the separately configured global event policies.

## Writing Tips

- For Character/Object rules, split different selectors into separate top-level entries.
- Put generic scalar rules first and more specific scalar rules later.
- For drop tables, think in terms of matching custom rows being merged together.
- For character, a conditionless custom block becomes the new custom default.
- For object, a conditionless custom entry becomes the new custom default for every populated component in that entry.
