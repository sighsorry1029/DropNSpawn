using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using static DropNSpawn.CommentedYamlTemplateSupport;
using SpawnSystemConfigurationEntry = DropNSpawn.CanonicalSpawnSystemEntry;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private static IEnumerable<string> GetSpawnSystemShorthandFieldExampleLines()
    {
        yield return "- prefab: Boar";
        yield return "  spawnSystem: # Flat SpawnSystem row # Controls spawn timing, placement, limits, native conditions, and custom data";
        yield return "    name: null # ex) Boar # Optional row name # Omit to default to the prefab name in this compressed format";
        yield return "    huntPlayer: false # True marks the spawned AI as hunting the player";
        yield return "    level: 1~1 # ex) 1~3 # Range of spawned creature levels";
        yield return "    overrideLevelUpChance: -1 # Use -1 to keep native behavior # Percent per extra level roll";
        yield return "    levelUpMinCenterDistance: 0 # Meters from the world center before native level-up rolls start";
        yield return "    groundOffset: 0.5 # Meters of vertical placement offset";
        yield return "    groundOffsetRandom: 0 # Range in meters of random vertical placement offset";
        yield return "    spawnInterval: 4 # ex) 100 # Seconds between spawn checks";
        yield return "    spawnChance: 100 # Percent chance per successful check";
        yield return "    spawnRadius: 40~80 # ex) 20~60 # Range in meters from one player to the chosen spawn center # Native effective default; 0 on min/max uses the native 40/80 fallback";
        yield return "    groupSize: 1~1 # ex) 1~3 # Range of creatures spawned by one successful attempt";
        yield return "    groupRadius: 3 # Meters from the chosen spawn center to each spawned creature";
        yield return "    noSpawnRadius: 10 # Meters from the chosen spawn center used to block the attempt if the same prefab is already nearby";
        yield return "    maxSpawned: 1 # Active prefab count cap in the wider area loaded around players";
        yield return "    tilt: 0~35 # Range in degrees of allowed ground tilt";
        yield return "    altitude: -1000~1000 # Range in world-height meters";
        yield return "    oceanDepth: 0~0 # ex) 0~10 # Range in meters of water depth at the spawn point # 0~0 leaves the native depth check effectively unconstrained";
        yield return "    distanceFromCenter: 0~0 # ex) 0~10000 # Range in meters from the world center # 0~0 leaves the native distance check effectively unconstrained";
        yield return "    biomes: [Meadows] # Allowed spawn biomes # Expand World Data custom biome names and numeric biome masks also work when EWD is installed";
        yield return "    biomeAreas: [Everything] # Allowed values: Edge, Median, Everything # Edge = biome border band # Median = biome interior # Everything = both";
        yield return "    timeOfDay: null # ex) [day, night]";
        yield return "    requiredEnvironments: [] # ex) [Rain, Clear] # Allowed environment names";
        yield return "    requiredGlobalKey: '' # ex) defeated_gdking # Native default is '' # Supports 'key 10' numeric syntax too";
        yield return "    inLava: false # True = lava only # False = outside lava only";
        yield return "    inForest: null # ex) true = forest only # false = outside forest only # null or no field allows both";
        yield return "    insidePlayerBase: false # False = outside player-base influence only";
        yield return "    canSpawnCloseToPlayer: false # True allows close-to-player spawns";
        yield return "    fields: {} # ex) { Character.m_name: $enemy_boar, health: 200, damage: 2 } # Expand World Data field overrides";
        yield return "    objects: [] # ex) [Wood,0,0,0,1] # Expand World Data object entries";
        yield return "    data: null # Expand World Data data entry name from your EWD config";
        yield return $"    faction: null # ex) ForestMonsters # Values: {FactionIntegration.GetNativeFactionList()}";
    }

    private static string BuildFullScaffoldConfigurationTemplate()
    {
        SpawnSystemSnapshot? snapshot = GetTemplateSnapshot();
        if (snapshot == null)
        {
            return "";
        }

        StringBuilder builder = new();
        bool wroteAny = false;

        List<SpawnSystemConfigurationEntry> entries = MergeScaffoldEntriesWithExternalProjections(
            BuildTemplateFullScaffoldEntries(snapshot),
            forceRefresh: true);
        foreach (PrefabOwnerSection<SpawnSystemConfigurationEntry> section in BuildBiomeOrderedReferenceSections(entries))
        {
            foreach (SpawnSystemConfigurationEntry entry in section.Entries)
            {
                if (wroteAny)
                {
                    builder.AppendLine();
                }

                AppendConfigurationEntry(builder, entry);
                wroteAny = true;
            }
        }

        return wroteAny ? builder.ToString() : "[]" + Environment.NewLine;
    }

    private static string BuildCompressedPrimaryOverrideConfigurationDocument(SpawnSystemSnapshot snapshot)
    {
        StringBuilder builder = new();

        AppendTemplateComment(builder, $"This file is auto-loaded from {Path.GetFileName(PrimaryOverrideConfigurationPathYml)} or {Path.GetFileName(PrimaryOverrideConfigurationPathYaml)}, plus any {PluginSettingsFacade.GetYamlDomainSupplementalPrefix("spawnsystem")}*.yml/.yaml files.");
        AppendTemplateComment(builder, "Loaded files are concatenated in filename order after the primary file.");
        AppendTemplateComment(builder, "This auto-created override is seeded from the current live SpawnSystem snapshot only and does not merge external CreatureManager/Jotunn reference projections.");
        AppendTemplateComment(builder, $"Use dns:reference spawnsystem when you want to export {PluginSettingsFacade.GetYamlDomainFilePrefix("spawnsystem")}.reference.yml for a compact reference snapshot, or {PluginSettingsFacade.GetYamlDomainFilePrefix("spawnsystem")}.full.yml for exhaustive field examples.");
        AppendTemplateComment(builder, "Applying this file still strictly replaces the live SpawnSystem table with the rows defined here.");
        AppendTemplateComment(builder, "requiredGlobalKey also supports 'key 10' to require at least that numeric value and consume it after each successful spawn.");
        foreach (string line in GetSpawnSystemShorthandFieldExampleLines())
        {
            AppendTemplateComment(builder, line);
        }
        AppendTemplateBlankLine(builder);

        bool wroteSection = false;
        List<SpawnSystemConfigurationEntry> entries = BuildTemplateReferenceEntries(snapshot);
        foreach (PrefabOwnerSection<SpawnSystemConfigurationEntry> section in BuildBiomeOrderedReferenceSections(entries))
        {
            if (section.Entries.Count == 0)
            {
                continue;
            }

            if (wroteSection)
            {
                builder.AppendLine();
            }

            foreach (SpawnSystemConfigurationEntry entry in section.Entries)
            {
                AppendReferenceEntry(builder, entry);
                builder.AppendLine();
            }

            wroteSection = true;
        }

        return wroteSection ? builder.ToString() : "[]" + Environment.NewLine;
    }
}
