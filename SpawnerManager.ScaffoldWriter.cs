using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static string BuildFullScaffoldConfigurationTemplate()
    {
        StringBuilder builder = new();
        bool wroteAny = false;

        foreach (PrefabOwnerSection<SpawnerConfigurationEntry> section in BuildConfigurationTemplate())
        {
            foreach (SpawnerConfigurationEntry entry in section.Entries)
            {
                if (wroteAny)
                {
                    AppendScaffoldBlankLine(builder);
                }

                AppendScaffoldListEntryLine(builder, 0, "prefab", entry.Prefab);
                AppendScaffoldLine(builder, 1, $"enabled: {FormatYamlBool(entry.Enabled)}");
                AppendScaffoldStringLine(builder, 1, "location", null);
                AppendScaffoldConditionsBlock(builder, 1, entry.SpawnArea != null);

                if (entry.SpawnArea != null)
                {
                    AppendScaffoldSpawnAreaBlock(builder, entry.SpawnArea);
                }

                if (entry.CreatureSpawner != null)
                {
                    AppendScaffoldCreatureSpawnerBlock(builder, entry.CreatureSpawner);
                }

                wroteAny = true;
            }
        }

        return wroteAny ? builder.ToString() : "[]" + Environment.NewLine;
    }

    private static void AppendScaffoldSpawnAreaBlock(StringBuilder builder, SpawnAreaDefinition definition)
    {
        AppendScaffoldLine(builder, 1, "spawnArea:");
        AppendScaffoldLine(builder, 2, $"levelUpChance: {FormatYamlFloat(definition.LevelUpChance ?? 15f)}");
        AppendScaffoldLine(builder, 2, $"spawnInterval: {FormatYamlFloat(definition.SpawnInterval ?? 30f)}");
        AppendScaffoldLine(builder, 2, $"triggerDistance: {FormatYamlFloat(definition.TriggerDistance ?? 256f)}");
        AppendScaffoldLine(builder, 2, $"setPatrolSpawnPoint: {FormatYamlBool(definition.SetPatrolSpawnPoint ?? true)}");
        AppendScaffoldLine(builder, 2, $"spawnRadius: {FormatYamlFloat(definition.SpawnRadius ?? 2f)}");
        AppendScaffoldLine(builder, 2, $"nearRadius: {FormatYamlFloat(definition.NearRadius ?? 10f)}");
        AppendScaffoldLine(builder, 2, $"farRadius: {FormatYamlFloat(definition.FarRadius ?? 1000f)}");
        AppendScaffoldLine(builder, 2, $"maxNear: {definition.MaxNear ?? 3}");
        AppendScaffoldLine(builder, 2, $"maxTotal: {definition.MaxTotal ?? 20}");
        AppendScaffoldLine(builder, 2, $"maxTotalSpawns: {definition.MaxTotalSpawns ?? 0}");
        AppendScaffoldLine(builder, 2, $"onGroundOnly: {FormatYamlBool(definition.OnGroundOnly ?? false)}");

        List<SpawnAreaSpawnDefinition> creatures = definition.Creatures ?? new List<SpawnAreaSpawnDefinition>();
        if (creatures.Count > 0)
        {
            AppendScaffoldLine(builder, 2, "creatures:");
            foreach (SpawnAreaSpawnDefinition creature in creatures)
            {
                AppendScaffoldListEntryLine(builder, 3, "creature", creature.Creature);
                AppendScaffoldLine(builder, 4, $"weight: {FormatYamlFloat(creature.Weight ?? 1f)}");
                AppendScaffoldLine(builder, 4, $"level: {RangeFormatting.FormatInlineObject(GetLevelRange(creature) ?? RangeFormatting.From(1, 1))}");
                AppendScaffoldStringLine(builder, 4, "faction", creature.Faction);
                AppendScaffoldStringLine(builder, 4, "data", creature.Data);
                AppendScaffoldLine(builder, 4, "fields: {}");
                AppendScaffoldLine(builder, 4, "objects: []");
            }
        }
        else
        {
            AppendScaffoldLine(builder, 2, "creatures: []");
        }
    }

    private static void AppendScaffoldCreatureSpawnerBlock(StringBuilder builder, CreatureSpawnerDefinition definition)
    {
        AppendScaffoldLine(builder, 1, "creatureSpawner:");
        AppendScaffoldStringLine(builder, 2, "creature", definition.Creature);
        AppendScaffoldLine(builder, 2, $"timeOfDay: {TimeOfDayFormatting.FormatInlineList(GetConfiguredTimeOfDay(definition), TimeOfDayFormatting.FromSpawnFlags(true, true))}");
        AppendScaffoldStringLine(builder, 2, "requiredGlobalKey", definition.RequiredGlobalKey ?? "");
        AppendScaffoldStringLine(builder, 2, "blockingGlobalKey", definition.BlockingGlobalKey ?? "");
        AppendScaffoldLine(builder, 2, $"level: {RangeFormatting.FormatInlineObject(GetLevelRange(definition) ?? RangeFormatting.From(1, 1))}");
        AppendScaffoldLine(builder, 2, $"levelUpChance: {FormatYamlFloat(definition.LevelUpChance ?? 10f)}");
        AppendScaffoldLine(builder, 2, $"respawnTimeMinutes: {FormatYamlFloat(definition.RespawnTimeMinutes ?? 20f)}");
        AppendScaffoldLine(builder, 2, $"spawnCheckInterval: {definition.SpawnCheckInterval ?? 5}");
        AppendScaffoldLine(builder, 2, $"spawnGroupId: {definition.SpawnGroupId ?? 0}");
        AppendScaffoldLine(builder, 2, $"spawnGroupRadius: {FormatYamlFloat(definition.SpawnGroupRadius ?? 0f)}");
        AppendScaffoldLine(builder, 2, $"spawnerWeight: {FormatYamlFloat(definition.SpawnerWeight ?? 1f)}");
        AppendScaffoldLine(builder, 2, $"maxGroupSpawned: {definition.MaxGroupSpawned ?? 1}");
        AppendScaffoldLine(builder, 2, $"triggerDistance: {FormatYamlFloat(definition.TriggerDistance ?? 60f)}");
        AppendScaffoldLine(builder, 2, $"triggerNoise: {FormatYamlFloat(definition.TriggerNoise ?? 0f)}");
        AppendScaffoldLine(builder, 2, $"allowInsidePlayerBase: {FormatYamlBool(definition.AllowInsidePlayerBase ?? false)}");
        AppendScaffoldLine(builder, 2, $"wakeUpAnimation: {FormatYamlBool(definition.WakeUpAnimation ?? false)}");
        AppendScaffoldLine(builder, 2, $"setPatrolSpawnPoint: {FormatYamlBool(definition.SetPatrolSpawnPoint ?? false)}");
        AppendScaffoldStringLine(builder, 2, "faction", definition.Faction);
        AppendScaffoldStringLine(builder, 2, "data", definition.Data);
        AppendScaffoldLine(builder, 2, "fields: {}");
        AppendScaffoldLine(builder, 2, "objects: []");
    }

    private static IntRangeDefinition? GetLevelRange(SpawnAreaSpawnDefinition definition)
    {
        return definition.Level ?? RangeFormatting.From(definition.MinLevel, definition.MaxLevel ?? definition.MinLevel);
    }

    private static IntRangeDefinition? GetLevelRange(CreatureSpawnerDefinition definition)
    {
        return definition.Level ?? RangeFormatting.From(definition.MinLevel, definition.MaxLevel ?? definition.MinLevel);
    }

    private static void AppendScaffoldComment(StringBuilder builder, string text)
    {
        builder.Append("# ");
        builder.AppendLine(text);
    }

    private static void AppendScaffoldLine(StringBuilder builder, int indent, string text)
    {
        builder.Append(' ', indent * 2);
        builder.AppendLine(text);
    }

    private static void AppendScaffoldBlankLine(StringBuilder builder)
    {
        builder.AppendLine();
    }

    private static void AppendScaffoldConditionsBlock(StringBuilder builder, int indent, bool includeSpawnAreaOnlyFields)
    {
        AppendScaffoldLine(builder, indent, "conditions:");
        AppendScaffoldLine(builder, indent + 1, "altitude: null");
        AppendScaffoldLine(builder, indent + 1, "distanceFromCenter: null");
        AppendScaffoldLine(builder, indent + 1, "biomes: []");
        if (includeSpawnAreaOnlyFields)
        {
            AppendScaffoldLine(builder, indent + 1, "timeOfDay: null");
        }

        AppendScaffoldLine(builder, indent + 1, "requiredEnvironments: []");
        if (includeSpawnAreaOnlyFields)
        {
            AppendScaffoldLine(builder, indent + 1, "requiredGlobalKeys: []");
            AppendScaffoldLine(builder, indent + 1, "forbiddenGlobalKeys: []");
        }

        AppendScaffoldLine(builder, indent + 1, "inForest: null");
        AppendScaffoldLine(builder, indent + 1, "inDungeon: null");
        if (includeSpawnAreaOnlyFields)
        {
            AppendScaffoldLine(builder, indent + 1, "insidePlayerBase: null");
        }

    }

    private static void AppendScaffoldStringLine(StringBuilder builder, int indent, string key, string? value)
    {
        if (value == null)
        {
            AppendScaffoldLine(builder, indent, $"{key}: null");
            return;
        }

        AppendScaffoldLine(builder, indent, $"{key}: {FormatYamlString(value)}");
    }

    private static void AppendScaffoldListEntryLine(StringBuilder builder, int indent, string key, string? value)
    {
        if (value == null)
        {
            AppendScaffoldLine(builder, indent, $"- {key}: null");
            return;
        }

        AppendScaffoldLine(builder, indent, $"- {key}: {FormatYamlString(value)}");
    }
}
