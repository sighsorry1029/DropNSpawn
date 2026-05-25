using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    internal static bool HasVneiRelevantSpawnAreaEntries(string prefabName)
    {
        lock (Sync)
        {
            return ActiveEntriesByPrefab.TryGetValue(prefabName ?? "", out List<SpawnerConfigurationEntry>? entries) &&
                   entries.Any(entry => entry.SpawnArea?.Creatures != null);
        }
    }

    internal static bool TryGetVneiDisplayForSpawnArea(SpawnArea spawnArea, out List<VneiRecipeResult> results)
    {
        lock (Sync)
        {
            results = new List<VneiRecipeResult>();
            if (spawnArea == null)
            {
                return false;
            }

            string prefabName = GetConfigPrefabName(spawnArea.gameObject, nameof(SpawnArea));
            ActiveEntriesByPrefab.TryGetValue(prefabName, out List<SpawnerConfigurationEntry>? entries);

            SpawnerConfigurationEntry? baseEntry = null;
            foreach (SpawnerConfigurationEntry entry in entries ?? Enumerable.Empty<SpawnerConfigurationEntry>())
            {
                if (entry.SpawnArea?.Creatures == null ||
                    DropConditionEvaluator.HasConditions(entry.Conditions) ||
                    !string.IsNullOrWhiteSpace(entry.Location))
                {
                    continue;
                }

                baseEntry = entry;
            }

            Dictionary<string, float> weightsByPrefab = new(StringComparer.OrdinalIgnoreCase);
            if (baseEntry?.SpawnArea?.Creatures != null)
            {
                AddVneiSpawnAreaWeights(weightsByPrefab, baseEntry.SpawnArea.Creatures);
            }
            else
            {
                AddVneiSpawnAreaWeights(weightsByPrefab, spawnArea.m_prefabs);
            }

            foreach (SpawnerConfigurationEntry entry in entries ?? Enumerable.Empty<SpawnerConfigurationEntry>())
            {
                if (ReferenceEquals(entry, baseEntry) || entry.SpawnArea?.Creatures == null)
                {
                    continue;
                }

                AddVneiSpawnAreaWeights(weightsByPrefab, entry.SpawnArea.Creatures);
            }

            if (weightsByPrefab.Count == 0)
            {
                return true;
            }

            float totalWeight = weightsByPrefab.Values.Sum(weight => Mathf.Max(0f, weight));
            float fallbackChance = weightsByPrefab.Count == 0 ? 1f : 1f / weightsByPrefab.Count;
            foreach ((string creaturePrefab, float weight) in weightsByPrefab.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                float chance = totalWeight <= 0f ? fallbackChance : Mathf.Clamp01(Mathf.Max(0f, weight) / totalWeight);
                results.Add(new VneiRecipeResult(creaturePrefab, 1, 1, 1f, 1, 1, chance));
            }

            return weightsByPrefab.Count > 0 || (entries?.Count ?? 0) > 0 || spawnArea.m_prefabs.Count > 0;
        }
    }

    private static void AddVneiSpawnAreaWeights(Dictionary<string, float> weightsByPrefab, IEnumerable<SpawnArea.SpawnData>? prefabs)
    {
        foreach (SpawnArea.SpawnData spawnData in prefabs ?? Enumerable.Empty<SpawnArea.SpawnData>())
        {
            if (spawnData?.m_prefab == null)
            {
                continue;
            }

            string prefabName = spawnData.m_prefab.name;
            if (!weightsByPrefab.TryGetValue(prefabName, out float existingWeight))
            {
                existingWeight = 0f;
            }

            weightsByPrefab[prefabName] = existingWeight + Mathf.Max(0f, spawnData.m_weight);
        }
    }

    private static void AddVneiSpawnAreaWeights(Dictionary<string, float> weightsByPrefab, IEnumerable<SpawnAreaSpawnDefinition>? definitions)
    {
        foreach (SpawnAreaSpawnDefinition definition in definitions ?? Enumerable.Empty<SpawnAreaSpawnDefinition>())
        {
            string creatureName = (definition.Creature ?? "").Trim();
            if (creatureName.Length == 0)
            {
                continue;
            }

            if (!weightsByPrefab.TryGetValue(creatureName, out float existingWeight))
            {
                existingWeight = 0f;
            }

            weightsByPrefab[creatureName] = existingWeight + Mathf.Max(0f, definition.Weight ?? 1f);
        }
    }
}
