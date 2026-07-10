using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static readonly ObjectConditionPlanCacheState ConditionPlanCacheState = new();

    private sealed class ObjectConditionPlanCacheState
    {
        private readonly Dictionary<string, GroupConditionalApplyPlanCacheEntry> _groupConditionalApplyPlans = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _groupConditionalApplyPlanLru = new();
        private readonly Dictionary<int, StaticConditionContextSnapshot> _staticConditionContexts = new();

        public bool TryGetGroupConditionalApplyPlan(string cacheKey, out GroupConditionalApplyPlan? plan)
        {
            if (_groupConditionalApplyPlans.TryGetValue(cacheKey, out GroupConditionalApplyPlanCacheEntry? cachedPlanEntry))
            {
                TouchGroupConditionalApplyPlanCacheEntry(cachedPlanEntry);
                plan = cachedPlanEntry.Plan;
                return true;
            }

            plan = null;
            return false;
        }

        public void StoreGroupConditionalApplyPlan(string cacheKey, GroupConditionalApplyPlan plan)
        {
            if (_groupConditionalApplyPlans.TryGetValue(cacheKey, out GroupConditionalApplyPlanCacheEntry? existingEntry))
            {
                existingEntry.Plan = plan;
                TouchGroupConditionalApplyPlanCacheEntry(existingEntry);
                return;
            }

            LinkedListNode<string> lruNode = _groupConditionalApplyPlanLru.AddLast(cacheKey);
            _groupConditionalApplyPlans[cacheKey] = new GroupConditionalApplyPlanCacheEntry
            {
                Plan = plan,
                LruNode = lruNode
            };
            TrimGroupConditionalApplyPlanCacheIfNeeded();
        }

        public void InvalidateStaticConditionContextForInstance(int instanceId)
        {
            _staticConditionContexts.Remove(instanceId);
        }

        public bool TryGetStaticConditionContext(int instanceId, Vector3 position, out StaticConditionContextSnapshot snapshot)
        {
            if (_staticConditionContexts.TryGetValue(instanceId, out snapshot) &&
                snapshot.Position == position)
            {
                return true;
            }

            snapshot = null!;
            return false;
        }

        public StaticConditionContextSnapshot StoreStaticConditionContext(int instanceId, StaticConditionContextSnapshot snapshot)
        {
            _staticConditionContexts[instanceId] = snapshot;
            return snapshot;
        }

        public void Clear()
        {
            _groupConditionalApplyPlans.Clear();
            _groupConditionalApplyPlanLru.Clear();
            _staticConditionContexts.Clear();
        }

        private void TouchGroupConditionalApplyPlanCacheEntry(GroupConditionalApplyPlanCacheEntry cacheEntry)
        {
            if (cacheEntry.LruNode.List == null || cacheEntry.LruNode == _groupConditionalApplyPlanLru.Last)
            {
                return;
            }

            _groupConditionalApplyPlanLru.Remove(cacheEntry.LruNode);
            _groupConditionalApplyPlanLru.AddLast(cacheEntry.LruNode);
        }

        private void TrimGroupConditionalApplyPlanCacheIfNeeded()
        {
            if (_groupConditionalApplyPlans.Count <= GroupConditionalApplyPlanCacheLimit)
            {
                return;
            }

            while (_groupConditionalApplyPlans.Count > GroupConditionalApplyPlanCacheTrimTarget &&
                   _groupConditionalApplyPlanLru.First != null)
            {
                LinkedListNode<string> oldestNode = _groupConditionalApplyPlanLru.First;
                _groupConditionalApplyPlanLru.RemoveFirst();
                string cacheKey = oldestNode.Value;
                _groupConditionalApplyPlans.Remove(cacheKey);
            }
        }
    }

    private sealed class StaticConditionContextSnapshot
    {
        public Vector3 Position { get; set; }
        public string ResolvedLocationName { get; set; } = "";
        public Heightmap.Biome Biome { get; set; }
        public bool InDungeon { get; set; }
    }

    private static bool CanUseGroupConditionalApplyPlan(ConditionsDefinition? conditions)
    {
        if (!DropConditionEvaluator.HasConditions(conditions))
        {
            return true;
        }

        return conditions != null &&
               !DropConditionEvaluator.HasDynamicConditions(conditions) &&
               !conditions.InForest.HasValue &&
               conditions.DistanceFromCenter?.HasValues() != true &&
               !conditions.MinDistanceFromCenter.HasValue &&
               !conditions.MaxDistanceFromCenter.HasValue &&
               conditions.Altitude?.HasValues() != true &&
               !conditions.MinAltitude.HasValue &&
               !conditions.MaxAltitude.HasValue;
    }

    private static bool TryGetGroupConditionalApplyPlan(
        GameObject gameObject,
        PrefabSnapshot snapshot,
        IReadOnlyCollection<PrefabConfigurationEntry> entries,
        out GroupConditionalApplyPlan? plan)
    {
        plan = null;
        if (entries.Count == 0)
        {
            return false;
        }

        bool hasEligibleEntries = false;
        bool usesLocation = false;
        bool usesBiome = false;
        bool usesInDungeon = false;
        foreach (PrefabConfigurationEntry entry in entries)
        {
            if (!CanUseGroupConditionalApplyPlan(entry.Conditions))
            {
                continue;
            }

            hasEligibleEntries = true;
            ConditionsDefinition? conditions = entry.Conditions;
            usesLocation |= conditions?.Locations?.Count > 0;
            usesBiome |= conditions?.ResolvedBiomeMask.HasValue == true || conditions?.Biomes?.Count > 0;
            usesInDungeon |= conditions?.InDungeon.HasValue == true;
        }

        if (!hasEligibleEntries)
        {
            return false;
        }

        string groupKey = BuildObjectReconcileGroupKey(gameObject, snapshot.Prefab.name);
        StaticConditionContextSnapshot conditionContext = GetOrBuildStaticConditionContextSnapshot(gameObject, usesLocation, usesBiome, usesInDungeon);
        string? resolvedLocationName = usesLocation ? conditionContext.ResolvedLocationName : null;
        string cacheKey = BuildGroupConditionalApplyPlanCacheKey(groupKey, usesLocation, usesBiome, usesInDungeon, conditionContext);
        if (ConditionPlanCacheState.TryGetGroupConditionalApplyPlan(cacheKey, out GroupConditionalApplyPlan? cachedPlan))
        {
            plan = cachedPlan;
            return true;
        }

        EnsureRuntimeDropConfigurationState();
        DropRuntimeState.Configuration.PlansByPrefab.TryGetValue(snapshot.Prefab.name, out CompiledObjectPrefabPlan? prefabPlan);
        List<CompiledObjectDropRule>? compiledRules = prefabPlan?.Rules;

        GroupConditionalApplyPlan builtPlan = new();
        foreach (PrefabConfigurationEntry entry in entries)
        {
            if (!CanUseGroupConditionalApplyPlan(entry.Conditions))
            {
                continue;
            }

            builtPlan.EligibleEntries.Add(entry);
            if (!DropConditionEvaluator.HasConditions(entry.Conditions) ||
                DropConditionEvaluator.AreStaticConditionsSatisfied(gameObject, entry.Conditions, resolvedLocationName))
            {
                builtPlan.MatchingEntries.Add(entry);
            }
        }

        if (compiledRules != null)
        {
            HashSet<PrefabConfigurationEntry> matchingEntries = new(builtPlan.MatchingEntries);
            foreach (CompiledObjectDropRule compiledRule in compiledRules)
            {
                if (!builtPlan.EligibleEntries.Contains(compiledRule.Entry))
                {
                    continue;
                }

                builtPlan.EligibleCompiledRules.Add(compiledRule);
                if (matchingEntries.Contains(compiledRule.Entry))
                {
                    builtPlan.MatchingCompiledRules.Add(compiledRule);
                }
            }
        }

        ConditionPlanCacheState.StoreGroupConditionalApplyPlan(cacheKey, builtPlan);
        plan = builtPlan;
        return true;
    }

    private static StaticConditionContextSnapshot GetOrBuildStaticConditionContextSnapshot(
        GameObject gameObject,
        bool usesLocation,
        bool usesBiome,
        bool usesInDungeon)
    {
        int instanceId = gameObject.GetInstanceID();
        Vector3 position = gameObject.transform.position;
        if (ConditionPlanCacheState.TryGetStaticConditionContext(instanceId, position, out StaticConditionContextSnapshot snapshot))
        {
            return snapshot;
        }

        snapshot = new StaticConditionContextSnapshot
        {
            Position = position
        };

        if (usesLocation)
        {
            snapshot.ResolvedLocationName = DropConditionEvaluator.GetResolvedLocationNameForConditions(gameObject) ?? "";
        }

        if (usesBiome)
        {
            snapshot.Biome = WorldGenerator.instance?.GetBiome(position) ?? Heightmap.FindBiome(position);
        }

        if (usesInDungeon)
        {
            snapshot.InDungeon = Character.InInterior(position);
        }

        return ConditionPlanCacheState.StoreStaticConditionContext(instanceId, snapshot);
    }

    private static string BuildGroupConditionalApplyPlanCacheKey(
        string groupKey,
        bool usesLocation,
        bool usesBiome,
        bool usesInDungeon,
        StaticConditionContextSnapshot conditionContext)
    {
        StringBuilder builder = new(groupKey.Length + 64);
        builder.Append(groupKey);

        if (usesLocation)
        {
            builder.Append("|loc:");
            builder.Append(conditionContext.ResolvedLocationName);
        }

        if (usesBiome)
        {
            builder.Append("|bio:");
            builder.Append((int)conditionContext.Biome);
        }

        if (usesInDungeon)
        {
            builder.Append("|dgn:");
            builder.Append(conditionContext.InDungeon ? '1' : '0');
        }

        return builder.ToString();
    }

    private static void RemovePendingObjectConditionPlanState(int instanceId)
    {
        ConditionPlanCacheState.InvalidateStaticConditionContextForInstance(instanceId);
    }

    private static void ClearLiveObjectConditionCaches()
    {
        ConditionPlanCacheState.Clear();
    }
}
