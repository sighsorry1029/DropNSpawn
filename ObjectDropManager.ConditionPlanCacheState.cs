using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static readonly ObjectConditionPlanCacheState ConditionPlanCacheState = new();

    private sealed class ObjectConditionPlanCacheState
    {
        private readonly Dictionary<long, StaticObjectMatchCacheEntry> _staticObjectMatchCache = new();
        private readonly Dictionary<string, GroupConditionalApplyPlanCacheEntry> _groupConditionalApplyPlans = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _groupConditionalApplyPlanLru = new();
        private readonly Dictionary<int, StaticConditionContextSnapshot> _staticConditionContexts = new();

        public bool TryGetStaticObjectMatch(long cacheKey, int epoch, out bool hasPotentialStaticMatch)
        {
            if (_staticObjectMatchCache.TryGetValue(cacheKey, out StaticObjectMatchCacheEntry cachedEntry) &&
                cachedEntry.Epoch == epoch)
            {
                hasPotentialStaticMatch = cachedEntry.HasPotentialStaticMatch;
                return true;
            }

            hasPotentialStaticMatch = false;
            return false;
        }

        public void RecordStaticObjectMatch(long cacheKey, int epoch, bool hasPotentialStaticMatch)
        {
            _staticObjectMatchCache[cacheKey] = new StaticObjectMatchCacheEntry(epoch, hasPotentialStaticMatch);
        }

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

        public void InvalidateStaticObjectMatchCacheForInstance(int instanceId)
        {
            for (int componentBit = 1; componentBit <= (int)LiveObjectComponentKind.Piece; componentBit <<= 1)
            {
                _staticObjectMatchCache.Remove(BuildStaticObjectMatchCacheKey(instanceId, (LiveObjectComponentKind)componentBit));
            }

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
            _staticObjectMatchCache.Clear();
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
