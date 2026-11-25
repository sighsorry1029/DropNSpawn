# DropNSpawn

Set loot for creatures and objects, choose where monsters spawn, and tune raids in Valheim. Generated YAML references show the prefab names and rules available in your world, including those added by other mods.

| Creature loot | Object loot | Spawners |
| --- | --- | --- |
| ![Character drop configuration](https://i.ibb.co/nMZ7gcZR/characterdrop.png) | ![Object drop configuration](https://i.ibb.co/yFhNTP60/objectdrop.png) | ![Spawner and world spawn checks](https://i.ibb.co/GQ9bPWb7/spawns.png) |
| Choose a creature's drops and chances. | Set loot for chests, trees and rocks. | Set which creatures a spawner produces. |

![How Valheim world spawning works](https://i.ibb.co/wZ4BfJF1/spawnsystem.png)

*World spawning: an interval triggers a check, not a guaranteed spawn.*

## Get started

Install **BepInExPack Valheim 5.4.2360** and the same DropNSpawn version on the server and every client.

- **For settings only**, open `BepInEx/config/sighsorry.DropNSpawn.cfg`. Adjust loot per star, drop creature loot instantly, or stack drops from creatures, resources and harvests. You can also limit local spawners, block progression-gated spawns in early biomes, and change raid timing.
- **For custom rules**, load a world once and open `BepInEx/config/DropNSpawn/`. Look up the prefab in the generated reference, then edit the active file. For example, read `DNS_character.reference.yml` and edit `DNS_character.yml`. Save to reload. In multiplayer, edit the server's files.

## YAML files

| To change | File and examples |
| --- | --- |
| Creature loot or drops from objects | [DNS_character.yml](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/override-logic-guide.md#character) · [DNS_object.yml](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/override-logic-guide.md#object) |
| Fixed spawners and their lifetime spawn limits | [DNS_spawner.yml](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawner-domain-guide.md) |
| Normal world spawns or their intervals by biome | [DNS_spawnsystem.yml](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawnsystem-guide.md) · [DNS_spawnintervals.yml](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/spawnsystem-guide.md#interval-multipliers-by-biome) |
| Raids or extra spawns in occupied dungeons | [DNS_events.yml](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/override-logic-guide.md#events) · [DNS_dungeon.yml](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/dungeon-spawns.md) |

A matching custom drop list **replaces** the original loot; it does not just add items. For world spawns, keep every rule you want for each prefab you list. By default, DNS imports original rules only for prefabs missing from all your active world spawn files.

## Compatibility

**Expand World Data is optional.** With both mods installed, DNS manages normal world spawns, raids and loot. EWD manages world generation, custom biomes and its separate AltBiome spawn lists.

If you use MonsterDB, Drop That!, Spawn That! or Custom Raids, disable only the DNS features the other mod should control. See [compatibility settings by mod](https://github.com/sighsorry1029/DropNSpawn/blob/main/docs/compatibility.md) or ask on [Discord](https://discord.com/invite/VFRJcPwUdm).
