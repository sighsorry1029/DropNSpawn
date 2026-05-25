using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    internal static bool HasVneiRelevantContainerOverride(string prefabName)
    {
        lock (Sync)
        {
            return HasRelevantVneiOverride(prefabName, entry => HasDropTableOverride(entry.Container));
        }
    }

    internal static bool HasVneiRelevantDropOnDestroyedOverride(string prefabName)
    {
        lock (Sync)
        {
            return HasRelevantVneiOverride(prefabName, entry => HasDropTableOverride(entry.DropOnDestroyed));
        }
    }

    internal static bool HasVneiRelevantMineRockOverride(string prefabName)
    {
        lock (Sync)
        {
            return HasRelevantVneiOverride(prefabName, entry => HasDropTableOverride(entry.MineRock));
        }
    }

    internal static bool HasVneiRelevantMineRock5Override(string prefabName)
    {
        lock (Sync)
        {
            return HasRelevantVneiOverride(prefabName, entry => HasDropTableOverride(entry.MineRock5));
        }
    }

    internal static bool HasVneiRelevantPickableOverride(string prefabName)
    {
        lock (Sync)
        {
            return HasRelevantVneiOverride(
                prefabName,
                entry => entry.Pickable != null &&
                         (HasPickableDropOverride(entry.Pickable.Drop) || HasDropTableOverride(entry.Pickable.ExtraDrops)));
        }
    }

    internal static bool HasVneiRelevantFishOverride(string prefabName)
    {
        lock (Sync)
        {
            return HasRelevantVneiOverride(prefabName, entry => HasDropTableOverride(entry.Fish?.ExtraDrops));
        }
    }

    internal static bool HasVneiRelevantDestructibleOverride(string prefabName)
    {
        lock (Sync)
        {
            return HasRelevantVneiOverride(
                prefabName,
                entry => !string.IsNullOrWhiteSpace(entry.Destructible?.SpawnWhenDestroyed));
        }
    }

    internal static bool HasVneiRelevantTreeBaseOverride(string prefabName)
    {
        lock (Sync)
        {
            return HasRelevantVneiOverride(
                prefabName,
                entry => HasDropTableOverride(entry.TreeBase) ||
                         HasDropTableOverride(entry.TreeLog) ||
                         HasDropTableOverride(entry.DropOnDestroyed));
        }
    }

    internal static bool TryGetVneiDisplayForDropTable(GameObject prefab, DropTable dropTable, out List<VneiRecipeResult> results)
    {
        lock (Sync)
        {
            results = new List<VneiRecipeResult>();
            if (prefab == null || dropTable == null)
            {
                return false;
            }

            if (prefab.TryGetComponent(out Container container) &&
                ReferenceEquals(container.m_defaultItems, dropTable))
            {
                return TryGetVneiDisplayForContainer(prefab, out results);
            }

            if (prefab.TryGetComponent(out DropOnDestroyed dropOnDestroyed) &&
                ReferenceEquals(dropOnDestroyed.m_dropWhenDestroyed, dropTable))
            {
                return TryGetVneiDropTableDisplay(prefab, snapshot => snapshot.DropOnDestroyed, entry => entry.DropOnDestroyed, payload => payload, "dropOnDestroyed", 1, out results);
            }

            if (prefab.TryGetComponent(out MineRock mineRock) &&
                ReferenceEquals(mineRock.m_dropItems, dropTable))
            {
                return TryGetVneiDropTableDisplay(prefab, snapshot => snapshot.MineRock, entry => entry.MineRock, payload => payload, "mineRock", 1, out results);
            }

            if (prefab.TryGetComponent(out MineRock5 mineRock5) &&
                ReferenceEquals(mineRock5.m_dropItems, dropTable))
            {
                return TryGetVneiDropTableDisplay(prefab, snapshot => snapshot.MineRock5, entry => entry.MineRock5, payload => payload, "mineRock5", 1, out results);
            }

            if (prefab.TryGetComponent(out Fish fish) &&
                ReferenceEquals(fish.m_extraDrops, dropTable))
            {
                return TryGetVneiDisplayForFish(fish, out results);
            }

            return false;
        }
    }

    internal static bool TryGetVneiDisplayForContainer(GameObject prefab, out List<VneiRecipeResult> results)
    {
        lock (Sync)
        {
            return TryGetVneiDropTableDisplay(prefab, snapshot => snapshot.Container, entry => entry.Container, payload => payload, "container", 1, out results);
        }
    }

    internal static bool TryGetVneiDisplayForPickable(GameObject prefab, Pickable pickable, out List<VneiRecipeResult> results)
    {
        lock (Sync)
        {
            results = new List<VneiRecipeResult>();
            if (prefab == null || pickable == null)
            {
                return false;
            }

            string prefabName = GetPrefabName(prefab);
            List<PrefabConfigurationEntry>? entries = GetVneiEntries(prefabName);
            SnapshotsByPrefab.TryGetValue(prefabName, out PrefabSnapshot? snapshot);
            PickableSnapshot? pickableSnapshot = snapshot?.Pickable;
            bool hasRelevantEntries = entries?.Any(entry => entry.Pickable != null) == true;
            if (!hasRelevantEntries && pickableSnapshot == null)
            {
                return false;
            }

            GameObject? baseItemPrefab = pickableSnapshot?.ItemPrefab;
            int baseAmount = Math.Max(1, pickableSnapshot?.Amount ?? 1);
            bool hasUnconditionalBaseOverride = false;
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (PrefabConfigurationEntry entry in entries ?? Enumerable.Empty<PrefabConfigurationEntry>())
            {
                PickableDefinition? definition = entry.Pickable;
                if (definition == null || DropConditionEvaluator.HasConditions(entry.Conditions) || !HasPickableDropOverride(definition.Drop))
                {
                    continue;
                }

                GameObject? overridePrefab = ResolveItemPrefab(definition.Drop!.Item, $"{prefabName}/pickable.drop");
                if (overridePrefab == null)
                {
                    continue;
                }

                hasUnconditionalBaseOverride = true;
                baseItemPrefab = overridePrefab;
                baseAmount = Math.Max(1, definition.Drop.Amount ?? 1);
            }

            if (baseItemPrefab != null)
            {
                if (!hasUnconditionalBaseOverride || baseItemPrefab != null)
                {
                    AddUniqueVneiRow(results, seen, new VneiRecipeResult(baseItemPrefab.name, 1, 1, 1f, baseAmount, baseAmount, 1f));
                }
            }

            foreach (PrefabConfigurationEntry entry in entries ?? Enumerable.Empty<PrefabConfigurationEntry>())
            {
                PickableDefinition? definition = entry.Pickable;
                if (definition == null || !DropConditionEvaluator.HasConditions(entry.Conditions) || !HasPickableDropOverride(definition.Drop))
                {
                    continue;
                }

                GameObject? overridePrefab = ResolveItemPrefab(definition.Drop!.Item, $"{prefabName}/pickable.drop");
                if (overridePrefab == null)
                {
                    continue;
                }

                int amount = Math.Max(1, definition.Drop.Amount ?? 1);
                AddUniqueVneiRow(results, seen, new VneiRecipeResult(overridePrefab.name, 1, 1, 1f, amount, amount, 1f));
            }

            if (TryGetVneiPickableExtraResults(prefab, snapshot, entries, out List<VneiRecipeResult> extraResults))
            {
                foreach (VneiRecipeResult row in extraResults)
                {
                    AddUniqueVneiRow(results, seen, row);
                }
            }

            return hasRelevantEntries || pickableSnapshot != null;
        }
    }

    internal static bool TryGetVneiDisplayForFish(Fish fish, out List<VneiRecipeResult> results)
    {
        lock (Sync)
        {
            results = new List<VneiRecipeResult>();
            if (fish == null || fish.gameObject == null)
            {
                return false;
            }

            return TryGetVneiDropTableDisplay(
                fish.gameObject,
                snapshot => snapshot.Fish?.ExtraDrops,
                entry => entry.Fish,
                definition => definition.ExtraDrops,
                "fish.extraDrops",
                1,
                out results);
        }
    }

    internal static bool TryGetVneiDisplayForDestructible(Destructible destructible, out List<VneiRecipeResult> results)
    {
        lock (Sync)
        {
            results = new List<VneiRecipeResult>();
            if (destructible == null)
            {
                return false;
            }

            GameObject prefab = destructible.gameObject;
            string prefabName = GetPrefabName(prefab);
            List<PrefabConfigurationEntry>? entries = GetVneiEntries(prefabName);
            SnapshotsByPrefab.TryGetValue(prefabName, out PrefabSnapshot? snapshot);
            GameObject? baseSpawnPrefab = snapshot?.Destructible?.SpawnWhenDestroyed;
            bool hasRelevantEntries = false;
            bool hasUnconditionalOverride = false;
            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (PrefabConfigurationEntry entry in entries ?? Enumerable.Empty<PrefabConfigurationEntry>())
            {
                DestructibleDefinition? definition = entry.Destructible;
                if (definition == null)
                {
                    continue;
                }

                string configuredPrefab = (definition.SpawnWhenDestroyed ?? "").Trim();
                if (configuredPrefab.Length == 0)
                {
                    continue;
                }

                hasRelevantEntries = true;
                GameObject? resolved = ResolvePrefab(configuredPrefab);
                if (resolved == null)
                {
                    continue;
                }

                if (DropConditionEvaluator.HasConditions(entry.Conditions))
                {
                    AddUniqueVneiRow(results, seen, new VneiRecipeResult(resolved.name, 1, 1, 1f, 1, 1, 1f));
                }
                else
                {
                    hasUnconditionalOverride = true;
                    baseSpawnPrefab = resolved;
                }
            }

            if (baseSpawnPrefab != null)
            {
                AddUniqueVneiRow(results, seen, new VneiRecipeResult(baseSpawnPrefab.name, 1, 1, 1f, 1, 1, 1f));
            }
            else if (!hasUnconditionalOverride && snapshot?.Destructible?.SpawnWhenDestroyed != null)
            {
                AddUniqueVneiRow(results, seen, new VneiRecipeResult(snapshot.Destructible.SpawnWhenDestroyed.name, 1, 1, 1f, 1, 1, 1f));
            }

            return hasRelevantEntries || snapshot?.Destructible?.SpawnWhenDestroyed != null;
        }
    }

    internal static bool TryGetVneiDisplayForTreeBase(TreeBase treeBase, out List<VneiRecipeResult> results)
    {
        lock (Sync)
        {
            results = new List<VneiRecipeResult>();
            if (treeBase == null)
            {
                return false;
            }

            bool handled = false;
            HashSet<string> seen = new(StringComparer.Ordinal);

            if (TryGetVneiDropTableDisplay(treeBase.gameObject, snapshot => snapshot.TreeBase, entry => entry.TreeBase, payload => payload, "treeBase", 1, out List<VneiRecipeResult> treeBaseResults))
            {
                handled = true;
                foreach (VneiRecipeResult result in treeBaseResults)
                {
                    AddUniqueVneiRow(results, seen, result);
                }
            }

            if (treeBase.m_stubPrefab != null &&
                TryGetVneiDropTableDisplay(treeBase.m_stubPrefab, snapshot => snapshot.DropOnDestroyed, entry => entry.DropOnDestroyed, payload => payload, "treeBase.stub", 1, out List<VneiRecipeResult> stubResults))
            {
                handled = true;
                foreach (VneiRecipeResult result in stubResults)
                {
                    AddUniqueVneiRow(results, seen, result);
                }
            }

            handled |= AppendTreeLogVneiResults(treeBase.m_logPrefab, 1, 0, results, seen);
            return handled;
        }
    }

    private static bool AppendTreeLogVneiResults(GameObject? logPrefab, int treeCount, int depth, List<VneiRecipeResult> results, HashSet<string> seen)
    {
        if (logPrefab == null || depth > 50 || !logPrefab.TryGetComponent(out TreeLog treeLog))
        {
            return false;
        }

        bool handled = false;
        if (treeLog.m_subLogPoints != null && treeLog.m_subLogPoints.Length > 0)
        {
            handled |= AppendTreeLogVneiResults(treeLog.m_subLogPrefab, Math.Max(1, treeLog.m_subLogPoints.Length), depth + 1, results, seen);
        }

        if (TryGetVneiDropTableDisplay(logPrefab, snapshot => snapshot.TreeLog, entry => entry.TreeLog, payload => payload, "treeLog", Math.Max(1, treeCount), out List<VneiRecipeResult> logResults))
        {
            handled = true;
            foreach (VneiRecipeResult result in logResults)
            {
                AddUniqueVneiRow(results, seen, result);
            }
        }

        return handled;
    }

    private static bool TryGetVneiPickableExtraResults(GameObject prefab, PrefabSnapshot? snapshot, List<PrefabConfigurationEntry>? entries, out List<VneiRecipeResult> results)
    {
        return TryGetVneiDropTableDisplay(
            prefab,
            _ => snapshot?.Pickable?.ExtraDrops,
            entry => entry.Pickable,
            definition => definition.ExtraDrops,
            "pickable.extraDrops",
            1,
            out results);
    }

    private static bool TryGetVneiDropTableDisplay<TBlock>(
        GameObject prefab,
        Func<PrefabSnapshot, DropTable?> snapshotSelector,
        Func<PrefabConfigurationEntry, TBlock?> blockSelector,
        Func<TBlock, DropTablePayloadDefinition?> payloadSelector,
        string contextSuffix,
        int groupMultiplier,
        out List<VneiRecipeResult> results)
        where TBlock : class
    {
        results = new List<VneiRecipeResult>();
        if (prefab == null)
        {
            return false;
        }

        string prefabName = GetPrefabName(prefab);
        List<PrefabConfigurationEntry>? entries = GetVneiEntries(prefabName);
        SnapshotsByPrefab.TryGetValue(prefabName, out PrefabSnapshot? snapshot);
        DropTable? snapshotTable = snapshot != null ? snapshotSelector(snapshot) : null;

        bool hasCustomBlock = false;
        List<DropTablePayloadDefinition> unconditionalPayloads = new();
        List<DropTablePayloadDefinition> conditionalPayloads = new();
        foreach (PrefabConfigurationEntry entry in entries ?? Enumerable.Empty<PrefabConfigurationEntry>())
        {
            TBlock? block = blockSelector(entry);
            if (block == null)
            {
                continue;
            }

            DropTablePayloadDefinition? payload = payloadSelector(block);
            if (!HasDropTableOverride(payload))
            {
                continue;
            }

            hasCustomBlock = true;
            if (DropConditionEvaluator.HasConditions(entry.Conditions))
            {
                conditionalPayloads.Add(payload!);
            }
            else
            {
                unconditionalPayloads.Add(payload!);
            }
        }

        if (!hasCustomBlock && snapshotTable == null)
        {
            return false;
        }

        DropTable baseTable = unconditionalPayloads.Count > 0
            ? BuildEffectiveDropTable(snapshotTable, unconditionalPayloads, $"{prefabName}/{contextSuffix}")
            : snapshotTable != null
                ? CloneDropTable(snapshotTable)
                : CreateDefaultDropTable();

        List<DropTable.DropData> displayRows = baseTable.m_drops
            .Select(CloneDropData)
            .ToList();
        HashSet<string> seenFingerprints = new(displayRows.Select(BuildDropRowFingerprint), StringComparer.Ordinal);
        foreach (DropTablePayloadDefinition payload in conditionalPayloads)
        {
            AppendDropTableRows(displayRows, payload.Drops, $"{prefabName}/{contextSuffix}", seenFingerprints);
        }

        results = BuildVneiResultsFromDropTable(baseTable, displayRows, groupMultiplier);
        return hasCustomBlock || snapshotTable != null;
    }

    private static List<VneiRecipeResult> BuildVneiResultsFromDropTable(DropTable table, List<DropTable.DropData> rows, int groupMultiplier)
    {
        List<VneiRecipeResult> results = new();
        if (table == null || rows == null || rows.Count == 0)
        {
            return results;
        }

        float totalWeight = rows.Sum(row => Mathf.Max(0f, row.m_weight));
        int groupMin = Math.Max(0, table.m_dropMin * Math.Max(1, groupMultiplier));
        int groupMax = Math.Max(groupMin, table.m_dropMax * Math.Max(1, groupMultiplier));
        float groupChance = Mathf.Clamp01(table.m_dropChance);
        foreach (DropTable.DropData row in rows)
        {
            if (row.m_item == null)
            {
                continue;
            }

            float chance = totalWeight <= 0f ? 1f : Mathf.Clamp01(Mathf.Max(0f, row.m_weight) / totalWeight);
            int min = Math.Max(1, row.m_stackMin);
            int max = Math.Max(min, row.m_stackMax);
            results.Add(new VneiRecipeResult(row.m_item.name, groupMin, groupMax, groupChance, min, max, chance));
        }

        return results;
    }

    private static DropTable.DropData CloneDropData(DropTable.DropData row)
    {
        return new DropTable.DropData
        {
            m_item = row.m_item,
            m_stackMin = row.m_stackMin,
            m_stackMax = row.m_stackMax,
            m_weight = row.m_weight,
            m_dontScale = row.m_dontScale
        };
    }

    private static string BuildDropRowFingerprint(DropTable.DropData row)
    {
        DropEntryDefinition definition = new()
        {
            Item = row.m_item != null ? row.m_item.name : "",
            StackMin = row.m_stackMin,
            StackMax = row.m_stackMax,
            Weight = row.m_weight,
            DontScale = row.m_dontScale
        };

        return BuildDropRowFingerprint(definition);
    }

    private static void AddUniqueVneiRow(List<VneiRecipeResult> results, HashSet<string> seen, VneiRecipeResult row)
    {
        string fingerprint = string.Join("\n",
            row.PrefabName ?? "",
            row.GroupMin,
            row.GroupMax,
            row.GroupChance.ToString("R"),
            row.Min,
            row.Max,
            row.Chance.ToString("R"));
        if (!seen.Add(fingerprint))
        {
            return;
        }

        results.Add(row);
    }

    private static GameObject? ResolvePrefab(string prefabName)
    {
        string trimmed = (prefabName ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return ZNetScene.instance?.GetPrefab(trimmed) ?? ObjectDB.instance?.GetItemPrefab(trimmed);
    }

    private static bool HasRelevantVneiOverride(string prefabName, Func<PrefabConfigurationEntry, bool> predicate)
    {
        return GetVneiEntries(prefabName ?? "")?.Any(predicate) == true;
    }
}
