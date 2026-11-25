using System;
using System.Collections.Generic;
using System.Linq;
using Row = System.ValueTuple<CharacterDrop.Drop, bool, bool, int?>;

namespace DropNSpawn;

/// <summary>Client browser previews for synchronized DNS_character rules. Revision tokens compare by value.</summary>
public static class CharacterDropCatalog
{
    public static object? GetRevision() => CharacterDropManager.CatalogRevision();

    /// <summary>Each copied row describes an independent base roll, chance and amount level scaling, and an optional final cap.
    /// Conditions follow ObjectDropCatalog.VisitDrops. An empty list replaces the prefab's normal drops.</summary>
    public static void VisitDrops(Action<string, string?, List<(CharacterDrop.Drop Drop, bool LevelChance, bool LevelAmount, int? Limit)>> visit)
        => CharacterDropManager.VisitCatalogDrops((prefab, condition, _, drops) => visit(prefab, condition, drops));

    /// <summary>Also reports whether unconditional YAML rules participate in this preview.
    /// Null marks a native table shown only for global loot scaling.</summary>
    public static void VisitDropsWithRuleInfo(Action<string, string?, bool?, List<(CharacterDrop.Drop Drop, bool LevelChance, bool LevelAmount, int? Limit)>> visit)
        => CharacterDropManager.VisitCatalogDrops(visit);
}

internal static partial class CharacterDropManager
{
    internal static object? CatalogRevision()
    {
        lock (Sync)
        {
            if (!IsGameDataReady() || !CharacterDropRuntime.HasSnapshots() || CharacterDropRuntime.HasPendingSnapshotBuildWork()) return null;
            bool configured = CatalogOverridesActive();
            if (!configured && !IsGlobalCharacterLootLevelScalingEnabled() && !PluginSettingsFacade.IsLinearChanceOnlyDropScalingEnabled()) return null;
            if (!_compiledStateGameDataSignature.HasValue || _compiledStateConfigurationSignature != RuntimeState.ConfigurationSignature)
                EnsureCompiledState();
            return (_compiledState.CatalogRevision, configured, PluginSettingsFacade.IsCharacterDropCalculateChanceLootSystemEnabled(),
                PluginSettingsFacade.IsGlobalCharacterDropTrophyLevelMultiplierEnabled(), CharacterDropGlobalConfig.CatalogTrophyBlacklist,
                PluginSettingsFacade.ShouldDisableCharacterLootScalingForCreatureLevelControl(), DropRowTrackingAvailable,
                PluginSettingsFacade.IsLinearChanceOnlyDropScalingEnabled() && PluginSettingsFacade.GetChanceOnlyDropChanceIncreasePerStar() == 0,
                PluginSettingsFacade.GetCharacterDropAdditionalLootChancePerStar(false) > 0,
                PluginSettingsFacade.GetCharacterDropAdditionalLootChancePerStar(true) > 0);
        }
    }

    private static bool CatalogOverridesActive() => ShouldApplyLocally() && CanUseCurrentRuntimeState() &&
        RuntimeState.PrefabsWithCharacterDropOverrides.Count != 0;

    internal static void VisitCatalogDrops(Action<string, string?, bool?, List<Row>> visit)
    {
        var previews = new List<(string prefab, string? condition, bool? hasUnconditionalRule, List<Row> drops)>();
        lock (Sync)
        {
            if (CatalogRevision() == null) return;
            bool configured = CatalogOverridesActive();
            foreach (var snapshot in CharacterDropRuntime.GetSnapshots())
            {
                if (snapshot.Prefab == null) continue;
                string prefab = snapshot.Prefab.name;
                bool boss = IsBossCharacter(snapshot.Prefab.GetComponent<Character>());
                bool hasStatic = _compiledState.StaticDropsByPrefab.TryGetValue(prefab, out var permanent);
                bool hasRuntime = _compiledState.RuntimeRulesByPrefab.TryGetValue(prefab, out var rules);
                if (!configured || (!hasStatic && !hasRuntime))
                {
                    if (snapshot.Prefab.GetComponent<CharacterDrop>()?.m_drops is not { Count: > 0 } live) continue;
                    var rows = CatalogCharacterRows(live, true, boss);
                    if (rows.Any(r => r.Item2 != r.Item1.m_levelMultiplier ||
                        r.Item3 != (r.Item1.m_levelMultiplier && !r.Item1.m_levelMultiplierNotAmount && !r.Item1.m_onePerPlayer)))
                        previews.Add((prefab, null, null, rows));
                    continue;
                }
                var common = rules?.Where(r => !DropConditionEvaluator.HasCharacterConditions(r.Entry.Conditions)).ToList() ?? new();
                var conditional = rules?.Where(r => DropConditionEvaluator.HasCharacterConditions(r.Entry.Conditions))
                    .GroupBy(r => Serializer.Serialize(r.Entry.Conditions).Trim()).ToList();
                bool hasUnconditionalRule = hasStatic || common.Count != 0;
                var baseline = hasUnconditionalRule ? FromRules(common) : CatalogCharacterRows(snapshot.BuiltDrops, true, boss);
                previews.Add((prefab, conditional?.Count > 0 ? "" : null, hasUnconditionalRule, baseline));
                if (conditional != null)
                    foreach (var group in conditional)
                        previews.Add((prefab, group.Key, hasUnconditionalRule, FromRules(common.Concat(group))));

                List<Row> FromRules(IEnumerable<CompiledCharacterDropRule> matches)
                {
                    var definitions = new SortedDictionary<string, CompiledCharacterDropDefinition>(StringComparer.Ordinal);
                    foreach (var definition in (permanent ?? Enumerable.Empty<CompiledCharacterDropDefinition>()).Concat(matches.SelectMany(r => r.Drops)))
                        definitions.TryAdd(definition.Fingerprint, definition);
                    var drops = BuildDrops(definitions.Values.ToList());
                    bool customHandling = definitions.Values.Any(RequiresRuntimeCharacterDropHandling);
                    return CatalogCharacterRows(drops, !customHandling, boss);
                }
            }
        }
        foreach (var preview in previews) visit(preview.prefab, preview.condition, preview.hasUnconditionalRule, preview.drops);
    }

    private static List<Row> CatalogCharacterRows(List<CharacterDrop.Drop> source, bool nativeHandling, bool boss)
    {
        var rows = new List<Row>();
        foreach (var drop in CloneDrops(source.Where(drop => drop != null).ToList()))
        {
            var policy = GetRowPolicy(drop);
            bool globalAmount = (!nativeHandling || DropRowTrackingAvailable) && !HasChanceOnlyDropForPrefab(source, drop.m_prefab) &&
                ShouldApplyGlobalCharacterLootLevelMultiplier(drop.m_prefab, drop.m_levelMultiplier, policy?.LevelMultiplierOverride);
            bool nativeLevel = drop.m_levelMultiplier && !globalAmount;
            bool levelChance = nativeLevel && !(drop.m_levelMultiplierNotAmount &&
                PluginSettingsFacade.IsLinearChanceOnlyDropScalingEnabled() && PluginSettingsFacade.GetChanceOnlyDropChanceIncreasePerStar() == 0);
            bool levelAmount = !drop.m_onePerPlayer && (globalAmount ? PluginSettingsFacade.GetCharacterDropAdditionalLootChancePerStar(boss) > 0 :
                nativeLevel && !drop.m_levelMultiplierNotAmount);
            rows.Add((drop, levelChance, levelAmount, policy?.AmountLimit));
        }
        return rows;
    }
}
