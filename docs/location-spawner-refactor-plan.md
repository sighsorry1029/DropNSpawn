# Spawner Location Lookup Design

## Current State

DropNSpawn no longer owns a user-editable location domain. Former location-domain gameplay features moved out:

- Boss altar and altar ItemStand rules live in BossRules.
- Boss despawn, boss-tamed pressure, same-boss duplicate blocking, Personalized BossStone, and remote Forsaken Power selection live in BossRules.
- RuneStone global pins and Vegvisir global effects live in UsefulRunestones.

DropNSpawn still keeps location lookup helpers because the `object` and `spawner` domains can use location-scoped selectors and generated location reference files.

## What Remains In DropNSpawn

### Object Domain

The object domain may generate:

- `DNS_object.reference.yml`
- `DNS_object.locations.reference.yml`

`DNS_object.locations.reference.yml` is a lookup file. It shows which location roots contain object prefabs. It is not a loaded override file.

### Spawner Domain

The spawner domain may generate:

- `DNS_spawner.reference.yml`
- `DNS_spawner.locations.reference.yml`

`DNS_spawner.locations.reference.yml` is a lookup file for top-level `locations:` selectors. It is not a loaded override file.

Spawner entries still use component-level overrides:

- `SpawnArea`
- `CreatureSpawner`

The `locations:` field restricts where a spawner entry applies. It does not replace ZoneSystem or DungeonDB location data.

## Design Direction

Keep location handling as lookup/provenance support, not as a gameplay domain.

Preferred behavior:

- Use `Location`, `LocationProxy`, dungeon room, and EWD clone provenance to resolve the current location context.
- Build generated reference files from vanilla and upstream mod data, not from DNS override YAML.
- Avoid full location-domain snapshots.
- Apply spawner changes through SpawnArea/CreatureSpawner runtime and reconcile paths.
- Keep generated location reference files auto-updated as tooling artifacts.

## Performance Notes

Location lookup should stay cheap during zone loading:

- Cache provenance when a spawned location root becomes available.
- Reconcile only registered or newly awakened spawner components.
- Avoid scanning every live object when only selector lookup data is needed.
- Keep EWD clone names distinct when a spawned root is known to be a clone.

## Out Of Scope

The following are intentionally not part of DropNSpawn anymore:

- `DNS_location.yml`
- location-domain transport schema
- offering bowl editing
- altar ItemStand editing
- RuneStone and Vegvisir gameplay edits
- boss-specific despawn or pressure rules

Those responsibilities belong to BossRules or UsefulRunestones.
