using System;
using System.Collections.Generic;
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
}
