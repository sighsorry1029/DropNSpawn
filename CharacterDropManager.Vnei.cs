using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class CharacterDropManager
{
    internal static bool HasVneiRelevantEntries(string prefabName)
    {
        lock (Sync)
        {
            return ActiveEntriesByPrefab.TryGetValue(prefabName ?? "", out List<CharacterDropPrefabEntry>? entries) &&
                   entries.Any(HasVneiRelevantDrops);
        }
    }

    internal static bool TryGetVneiDisplayResults(CharacterDrop characterDrop, out List<VneiRecipeResult> results)
    {
        lock (Sync)
        {
            results = new List<VneiRecipeResult>();
            if (characterDrop == null)
            {
                return false;
            }

            string prefabName = characterDrop.gameObject?.name ?? "";
            List<CharacterDropPrefabEntry>? vneiEntries = ActiveEntriesByPrefab.TryGetValue(prefabName, out List<CharacterDropPrefabEntry>? entries)
                ? entries.Where(HasVneiRelevantDrops).ToList()
                : null;
            bool hasEntries = vneiEntries?.Count > 0;
            bool hasSnapshot = CharacterDropRuntime.TryGetSnapshot(prefabName, out CharacterDropSnapshot? snapshot);
            List<CharacterDropItemSnapshot>? baseDrops = hasSnapshot
                ? snapshot!.Drops
                : CloneSnapshotDrops(characterDrop.m_drops);
            if (!hasEntries && baseDrops.Count == 0)
            {
                return false;
            }

            bool suppressVanilla = vneiEntries?.Any(entry => !DropConditionEvaluator.HasCharacterConditions(entry.Conditions)) == true;
            HashSet<string> seen = new(StringComparer.Ordinal);
            if (!suppressVanilla)
            {
                foreach (CharacterDropItemSnapshot drop in baseDrops)
                {
                    AddVneiSnapshotDrop(results, seen, drop);
                }
            }

            foreach (CharacterDropPrefabEntry entry in vneiEntries ?? Enumerable.Empty<CharacterDropPrefabEntry>())
            {
                foreach (CharacterDropEntryDefinition definition in entry.CharacterDrop?.Drops ?? Enumerable.Empty<CharacterDropEntryDefinition>())
                {
                    AddVneiConfiguredDrop(results, seen, definition, $"{prefabName}/characterDrop");
                }
            }

            return hasEntries || baseDrops.Count > 0;
        }
    }

    private static bool HasVneiRelevantDrops(CharacterDropPrefabEntry entry)
    {
        return entry?.CharacterDrop?.Drops?.Count > 0;
    }

    private static void AddVneiSnapshotDrop(List<VneiRecipeResult> results, HashSet<string> seen, CharacterDropItemSnapshot drop)
    {
        if (drop.ItemPrefab == null)
        {
            return;
        }

        string fingerprint = $"{drop.ItemPrefab.name}\n{drop.AmountMin}\n{drop.AmountMax}\n{drop.Chance.ToString("R")}";
        if (!seen.Add(fingerprint))
        {
            return;
        }

        results.Add(new VneiRecipeResult(
            drop.ItemPrefab.name,
            1,
            1,
            1f,
            Math.Max(1, drop.AmountMin),
            Math.Max(Math.Max(1, drop.AmountMin), drop.AmountMax),
            Mathf.Clamp01(drop.Chance)));
    }

    private static void AddVneiConfiguredDrop(List<VneiRecipeResult> results, HashSet<string> seen, CharacterDropEntryDefinition definition, string context)
    {
        string itemName = (definition.Item ?? "").Trim();
        if (itemName.Length == 0)
        {
            return;
        }

        GameObject? prefab = ResolveItemPrefab(itemName, context);
        if (prefab == null)
        {
            return;
        }

        int amountMin = Math.Max(1, definition.AmountMin ?? 1);
        int amountMax = Math.Max(amountMin, definition.AmountMax ?? definition.AmountMin ?? 1);
        float chance = Mathf.Clamp01(definition.Chance ?? 1f);
        string fingerprint = BuildDropRowFingerprint(definition);
        if (!seen.Add(fingerprint))
        {
            return;
        }

        results.Add(new VneiRecipeResult(prefab.name, 1, 1, 1f, amountMin, amountMax, chance));
    }
}
