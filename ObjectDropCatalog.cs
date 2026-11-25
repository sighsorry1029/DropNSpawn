using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

/// <summary>Read-only object drop previews for client-side browsers. Call on the game thread.</summary>
public static class ObjectDropCatalog
{
    /// <summary>Opaque identity for the current synchronized object rules, or null when inactive.</summary>
    public static object? GetRevision() => ObjectDropManager.GetCatalogRevision();

    /// <summary>Visits fresh tables only for overridden components. An empty table replaces native drops.
    /// A null condition is unconditional, an empty one is the default when no conditional rules match.
    /// A named condition previews matching that predicate in addition to unconditional rules.
    /// For pieces the placement flag distinguishes player-built (true) and world-generated (false) instances.</summary>
    public static void VisitDrops(Action<string, string, string?, bool?, DropTable> visit) =>
        ObjectDropManager.VisitCatalogDrops((prefab, component, condition, playerBuilt, _, table) => visit(prefab, component, condition, playerBuilt, table));

    /// <summary>Also reports whether unconditional YAML rules participate for this component.
    /// Null marks the original table of a player-built piece. Candidate lists and scalar overrides retain their component-specific behavior.</summary>
    public static void VisitDropsWithRuleInfo(Action<string, string, string?, bool?, bool?, DropTable> visit) => ObjectDropManager.VisitCatalogDrops(visit);
}

internal static partial class ObjectDropManager
{
    internal static object? GetCatalogRevision()
    {
        lock (Sync)
        {
            if (!PluginSettingsFacade.IsObjectDomainEnabled() || !IsGameDataReady() || _pendingSnapshotBuild != null ||
                !_publishedSnapshotSignature.HasValue || RuntimeState.ActiveEntriesByPrefab.Count == 0 ||
                !CanUseCurrentRuntimeState()) return null;
            if (!DropRuntimeState.GameDataSignature.HasValue ||
                !string.Equals(DropRuntimeState.ConfigurationSignature, RuntimeState.ConfigurationSignature, StringComparison.Ordinal))
                EnsureRuntimeDropConfigurationState();
            return DropRuntimeState.Configuration.CatalogRevision;
        }
    }

    internal static void VisitCatalogDrops(Action<string, string, string?, bool?, bool?, DropTable> visit)
    {
        var rows = new List<(string prefab, string component, string? condition, bool? playerBuilt, bool? hasUnconditionalRule, DropTable table)>();
        lock (Sync)
        {
            if (GetCatalogRevision() == null) return;
            foreach (var pair in DropRuntimeState.Configuration.PlansByPrefab)
            {
                if (!SnapshotState.SnapshotsByPrefab.TryGetValue(pair.Key, out var snapshot) || snapshot.Prefab == null) continue;
                CollectCatalogDrops(snapshot.PrefabName, pair.Value.Rules, snapshot, snapshot.Prefab.GetComponent<Piece>() != null, rows);
            }
        }
        foreach (var row in rows) visit(row.prefab, row.component, row.condition, row.playerBuilt, row.hasUnconditionalRule, row.table);
    }

    private static void CollectCatalogDrops(string prefab, List<CompiledObjectDropRule> rules, PrefabSnapshot snapshot, bool isPiece,
        List<(string prefab, string component, string? condition, bool? playerBuilt, bool? hasUnconditionalRule, DropTable table)> rows)
    {
        var predicates = rules.ToDictionary(r => r, r => r.HasConditions ? Serializer.Serialize(r.Entry.Conditions).Trim() : "");
        Table("dropOnDestroyed", snapshot.DropOnDestroyed, r => r.DropOnDestroyed);
        Table("mineRock", snapshot.MineRock, r => r.MineRock);
        Table("mineRock5", snapshot.MineRock5, r => r.MineRock5);
        Table("treeBase", snapshot.TreeBase, r => r.TreeBase);
        Table("treeLog", snapshot.TreeLog, r => r.TreeLog);
        Table("container", snapshot.Container, r => r.Container);
        Table("pickable.extraDrops", snapshot.Pickable?.ExtraDrops, r => r.PickableExtraDrops);
        Table("fish.extraDrops", snapshot.Fish?.ExtraDrops, r => r.FishExtraDrops);
        if (snapshot.Pickable is { } pickable)
            Preview("pickable.drop", r => r.Pickable is { } p && (p.HasItemPrefabOverride || p.HasAmountOverride),
                matching =>
                {
                    var item = pickable.ItemPrefab;
                    int amount = pickable.Amount;
                    foreach (var rule in matching)
                    {
                        var p = rule.Pickable!;
                        if (p.HasItemPrefabOverride) item = p.ItemPrefab;
                        if (p.HasAmountOverride) amount = p.Amount;
                    }
                    return FixedCatalogDrop(item, amount);
                });
        if (snapshot.Destructible is { } destroyed)
            Preview("destructible", r => r.Destructible?.HasSpawnWhenDestroyedOverride == true,
                matching => FixedCatalogDrop(matching.Count == 0 ? destroyed.SpawnWhenDestroyed :
                    matching[matching.Count - 1].Destructible!.SpawnWhenDestroyed, 1));
        if (snapshot.PickableItem is { } loose)
            Preview("pickableItem", r => r.PickableItem != null, matching => CatalogLooseDrops(loose, matching));

        void Table(string component, DropTable? original, Func<CompiledObjectDropRule, CompiledDropTablePayload?> payload)
        {
            if (original == null) return;
            Preview(component, r => payload(r) != null, matching =>
                BuildEffectiveDropTable(original, matching.Select(r => payload(r)!)));
        }

        void Preview(string component, Func<CompiledObjectDropRule, bool> applies,
            Func<List<CompiledObjectDropRule>, DropTable> build)
        {
            var relevant = rules.Where(applies).ToList();
            if (relevant.Count == 0) return;
            var common = relevant.Where(r => !r.HasConditions).ToList();
            var conditions = relevant.Where(r => r.HasConditions).GroupBy(r => predicates[r]).ToList();
            if (isPiece) rows.Add((prefab, component, null, true, null, build(new())));
            bool? placement = isPiece ? false : null;
            rows.Add((prefab, component, conditions.Count == 0 ? null : "", placement, common.Count != 0, build(common)));
            foreach (var condition in conditions)
            {
                // Drop tables apply the static template before appending matching conditional rows.
                // Scalar/pickable overrides follow the configured order.
                var matching = component is "pickable.drop" or "pickableItem" or "destructible"
                    ? relevant.Where(r => !r.HasConditions || predicates[r] == condition.Key)
                    : common.Concat(condition);
                rows.Add((prefab, component, condition.Key, placement, common.Count != 0, build(matching.ToList())));
            }
        }
    }

    private static DropTable FixedCatalogDrop(GameObject? item, int amount)
    {
        var table = CreateDefaultDropTable();
        if (item != null && amount > 0)
            table.m_drops.Add(new DropTable.DropData { m_item = item, m_weight = 1f, m_stackMin = amount, m_stackMax = amount });
        return table;
    }

    private static DropTable CatalogLooseDrops(PickableItemSnapshot snapshot, List<CompiledObjectDropRule> matching)
    {
        GameObject? item = snapshot.ItemPrefab;
        int stack = snapshot.Stack;
        var random = CreateDefaultDropTable();
        foreach (var option in snapshot.RandomItems)
            random.m_drops.Add(new DropTable.DropData { m_item = option.ItemPrefab, m_weight = 1f,
                m_stackMin = option.StackMin, m_stackMax = option.StackMax });
        foreach (var rule in matching)
        {
            var p = rule.PickableItem!;
            if (p.RandomItems.Length > 0)
            {
                random.m_drops.Clear();
                for (int i = 0; i < p.RandomItems.Length; i++)
                {
                    var option = p.RandomItems[i];
                    random.m_drops.Add(new DropTable.DropData { m_item = option.m_itemPrefab.gameObject,
                        m_weight = p.RandomWeights[i], m_stackMin = option.m_stackMin, m_stackMax = option.m_stackMax });
                }
                item = null;
                continue;
            }
            if (p.HasRandomOverride) random.m_drops.Clear();
            if (p.HasFixedItemOverride) item = p.FixedItemPrefab?.gameObject;
            if (p.HasFixedStackOverride) stack = p.FixedStack;
        }
        if (item == null) return random;
        int count = stack > 0 ? stack : item.GetComponent<ItemDrop>()?.m_itemData.m_stack ?? 1;
        return FixedCatalogDrop(item, Math.Max(1, count));
    }
}
