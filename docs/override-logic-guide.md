# Override Logic Guide

This document explains the current override logic in simple terms for users
writing YAML.

[Back to DropNSpawn](../README.md) · [Mod compatibility](compatibility.md) · [World spawn guide](spawnsystem-guide.md)

## Files and references

Configuration files are in `BepInEx/config/DropNSpawn/`. Use `DNS_<domain>.yml` or `.yaml`, with `character`, `object`, `spawner`, `spawnsystem` or `events` as the domain. Supplemental names such as `DNS_character_wolves.yml` are also loaded. Edit the server's files in multiplayer; accepted YAML changes reload live.

Generated `.reference.yml` files show available names and source values. `.full.yml` files are exhaustive scaffolds, not active overrides. Generated `.sample.yml` files in `examples/` are inactive until copied or renamed to a loaded override filename. Object and Spawner also have `.locations.reference.yml` files for location lookup.

| Console command | Purpose |
| --- | --- |
| `dns:reference [object\|character\|spawner\|spawnsystem\|events\|all]` | Regenerate references |
| `dns:full [object\|character\|spawner\|spawnsystem\|all]` | Write non-loaded full scaffolds |
| `dns:inspect spawner` | Inspect the current or nearest spawner and its location context |

Events use their reference file instead of a full scaffold. Large location mods can make manual full exports expensive; read the [MWL precautions](compatibility.md#more-world-locations-and-reference-exports) before using them on a low-memory client.

For SpawnSystem, keep the complete desired spawn table rather than copying only a few edits. See the [world spawn guide](spawnsystem-guide.md). [Spawner selectors and cumulative limits](spawner-domain-guide.md), [dungeon rules and diagnostics](dungeon-spawns.md), and [omitted/null/empty values](yaml-null-empty-guide.md) have their own guides.

## How to read the rules

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

### Character config options

These options are under `2 - Character` in `sighsorry.DropNSpawn.cfg`. They can affect original game drops as well as YAML overrides; you do not need to copy a drop table just to use them.

| Option | Default | Purpose |
| --- | --- | --- |
| `character loot system` | `CalculateChance` | Use DNS's per-star amount scaling for eligible non-trophy drops. `Vanilla` leaves their scaling to the game and YAML level flags. |
| `chance for additional loot per star for creatures` / `for bosses` | `50` each | For eligible drops, each star adds this percentage of the rolled base quantity. With 50, a base amount of 4 becomes 4 / 6 / 8 at zero / one / two stars. Fractions are probabilistically rounded: 1.5 means one item plus a 50% chance of another. |
| `global trophy level multiplier` | `Off` | Apply those amount rates to eligible successful trophy drops too. YAML `levelMultiplier: false` and the trophy blacklist opt out. Chance-only rows are handled separately. |
| `chance-only drop chance scaling` / `chance-only drop chance increase per star (%)` | `Linear` / `50` | Scale chance instead of quantity for rows with both level flags enabled. See the examples below; these are independent of the amount settings. |
| `monster instant loot drop` | `Off` | Release CharacterDrop loot immediately while keeping the ragdoll visible for its normal lifetime. Creature blacklist and EpicLoot exceptions apply. |
| `global drop in stack` | `Off` | Deliver stackable character loot in stacks, subject to YAML choices and the item blacklist. Requires Character Overrides. |
| `OnePerPlayer drop check range` | `32` m | For existing `onePerPlayer` drops, count living players within this horizontal distance. `0` restores the native online-player count; it does not disable those drops. Requires Character Overrides. |
| `require player-aligned killer for character drops` | `Off` | Suppress non-player creature loot unless its lethal source qualifies. The creature blacklist exempts victims from this filter. |
| `disable DNS character loot scaling when CLLC is loaded` | `On` | Let CLLC handle scaling when installed, without changing your saved DNS settings. Other loot features remain available. |

The killer filter accepts players, tamed characters and characters in the `Players` or `PlayerSpawned` faction. A lethal poison, fire or spirit tick also qualifies when that effect's active damage pool contains a confirmed player-aligned contribution, even mixed with other damage. Unknown-only damage does not qualify, and an unrelated active effect does not turn an environmental or hostile direct kill into a player kill. This filter remains active independently of Character Overrides when enabled.

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

### Chance-only star scaling

With both `levelMultiplier: true` and `levelMultiplierNotAmount: true`, stars increase drop chance instead of quantity. Custom rows default `levelMultiplierNotAmount` to `false`; original rows keep their native flags unless overridden.

Under `2 - Character`, `chance-only drop chance scaling` defaults to `Linear`. Each star adds `chance-only drop chance increase per star (%)` of the base chance. This integer setting is 0–100, default 50: a 10% base becomes 10%, 15%, 20% at zero, one and two stars. At 25 it becomes 10%, 12.5%, 15%; at 0 it stays 10%. The final chance is capped at 100%.

Select `Vanilla` to retain exponential chance scaling, such as 10%, 20%, 40%, 80%, and ignore the linear increase setting. This policy changes no amounts and is independent of `character loot system`, the additional-loot amount percentages and `global trophy level multiplier`. It works on eligible original drops even with Character Overrides off. Server changes affect subsequent rolls, with the same rate for creatures and bosses; the CLLC handoff can disable DNS scaling.

If any row for a dropped prefab has both flags true, DNS excludes all rows for that dropped prefab from its amount scaling. Only the chance-only rows receive the selected chance policy; ordinary rows retain their original chance and amount rules. Global trophy scaling and custom stacked/limited drops respect this exception. Resource multipliers, `onePerPlayer`, amount limits and stacking still apply.

### Stacking and instant loot

For `dropInStack`, omitted/`null` inherits `global drop in stack`; explicit `true` requests stacks and explicit `false` requests singles. The stack blacklist wins. Existing YAML is not rewritten: remove `dropInStack: false` or set it to `null` where inheritance is intended.

Successful rows retain their own policies even when an earlier row for the same item fails. `amountLimit` is applied before saving loot, and new ragdolls save explicit stack choices for delayed or instant delivery. Changing YAML affects subsequent rolls, not already saved loot. Changing the global stack default or blacklist affects subsequent delivery. Older ragdolls without policy metadata inherit the global default.

`monster instant loot drop blacklist` defaults to `Dragon, Hatchling`. These creature prefabs keep normal ragdoll loot timing even with instant loot on. Use comma-separated prefab names, or an empty value for no exclusions; changes affect new ragdolls. [EpicLoot's additional rewards keep their normal timing](compatibility.md#other-integrations).

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

### Event config options

Settings under `3 - Events` work independently of Event YAML overrides. Defaults below are DNS defaults, not a claim that every vanilla or modded event uses them unchanged.

| Option | Default | Purpose |
| --- | --- | --- |
| `Event scheduling mode` | `Vanilla` | `Vanilla`: one active random event, one server-wide check. `MultipleGlobal`: multiple active events, one server-wide check. `MultiplePerPlayer`: multiple active events, independent checks per player. A check still needs an eligible event and passing conditions. |
| `Random event chance` | `20`% | Chance to try starting a random event at each check. `0` disables these random attempts, not every forced event. |
| `Random event interval` | `46` minutes | Time between checks. **`0` means a check every server FixedUpdate**, not Off. |
| `Default event player base` | `Off` | `Off` preserves each event's baseline. `Away`, `Near` or `AwayAndNear` supply the condition when YAML does not specify `conditions.playerBase`. Forced boss events do not use this random-start filter. |
| `Event duration multiplier` | `1` | Scale durations without explicit YAML `settings[2]`: `0.5` halves and `2` doubles them. `0` disables events with a positive effective duration, including explicit YAML durations; duration-zero events remain enabled. |
| `Minimum distance between events` | `100` m | Minimum horizontal distance between concurrent random-event centers. A conflicting new attempt is skipped; the current event continues. |

### YAML event definitions

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

When Custom Raids manages raids, use the [event compatibility settings](compatibility.md#custom-raids). Turning Event Overrides off alone does not hand every event setting over to another mod.

## Writing Tips

- For Character/Object rules, split different selectors into separate top-level entries.
- Put generic scalar rules first and more specific scalar rules later.
- For drop tables, think in terms of matching custom rows being merged together.
- For character, a conditionless custom block becomes the new custom default.
- For object, a conditionless custom entry becomes the new custom default for every populated component in that entry.
