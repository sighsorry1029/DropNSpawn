using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    internal static void ApplyLazyMineRockScalarsIfNeeded(MineRock mineRock)
    {
        lock (Sync)
        {
            if (!TryResolveLazyDamageableScalars(mineRock.gameObject, LiveObjectComponentKind.MineRock, out CompiledDamageableScalarDefinition scalars, out int signature))
            {
                return;
            }

            ApplyLazyMineRockScalars(mineRock, scalars, signature);
        }
    }

    internal static void ApplyLazyDestructibleScalarsIfNeeded(Destructible destructible)
    {
        lock (Sync)
        {
            if (!TryResolveLazyDestructibleScalars(destructible.gameObject, out CompiledDestructibleComponentDefinition compiledDefinition, out int signature))
            {
                return;
            }

            ApplyLazyDestructibleScalars(destructible, compiledDefinition, signature);
        }
    }

    internal static void ApplyLazyDestructibleTypeIfNeeded(Destructible destructible)
    {
        lock (Sync)
        {
            if (!TryResolveLazyDestructibleType(destructible.gameObject, out DestructibleType destructibleType))
            {
                return;
            }

            ApplyDestructibleType(destructible, destructibleType);
        }
    }

    internal static void ApplyLazyMineRock5ScalarsIfNeeded(MineRock5 mineRock5)
    {
        lock (Sync)
        {
            if (!TryResolveLazyDamageableScalars(mineRock5.gameObject, LiveObjectComponentKind.MineRock5, out CompiledDamageableScalarDefinition scalars, out int signature))
            {
                return;
            }

            ApplyLazyMineRock5Scalars(mineRock5, scalars, signature);
        }
    }

    internal static void ApplyLazyTreeBaseScalarsIfNeeded(TreeBase treeBase)
    {
        lock (Sync)
        {
            if (!TryResolveLazyDamageableScalars(treeBase.gameObject, LiveObjectComponentKind.TreeBase, out CompiledDamageableScalarDefinition scalars, out int signature))
            {
                return;
            }

            ApplyLazyTreeBaseScalars(treeBase, scalars, signature);
        }
    }

    internal static void ApplyLazyTreeLogScalarsIfNeeded(TreeLog treeLog)
    {
        lock (Sync)
        {
            if (!TryResolveLazyDamageableScalars(treeLog.gameObject, LiveObjectComponentKind.TreeLog, out CompiledDamageableScalarDefinition scalars, out int signature))
            {
                return;
            }

            ApplyLazyTreeLogScalars(treeLog, scalars, signature);
        }
    }

    private static bool TryResolveLazyDamageableScalars(
        GameObject gameObject,
        LiveObjectComponentKind componentKind,
        out CompiledDamageableScalarDefinition resolvedScalars,
        out int signature)
    {
        resolvedScalars = null!;
        signature = 0;
        if (!TryGetConditionalDropContext(gameObject, out _, out _, out List<CompiledObjectDropRule> compiledRules))
        {
            return false;
        }

        CompiledDamageableScalarDefinition effectiveScalars = new();
        foreach (CompiledObjectDropRule compiledRule in compiledRules)
        {
            if (!CanUseLazyDamageableScalarFastPath(compiledRule.Entry, componentKind) ||
                !EntryMatches(gameObject, compiledRule.Entry, allowConditionalMatches: true))
            {
                continue;
            }

            CompiledDamageableScalarDefinition? candidateScalars = GetDamageableScalarDefinition(compiledRule, componentKind);
            if (candidateScalars == null)
            {
                continue;
            }

            if (candidateScalars.HasHealthOverride)
            {
                effectiveScalars.HasHealthOverride = true;
                effectiveScalars.Health = candidateScalars.Health;
            }

            if (candidateScalars.HasMinToolTierOverride)
            {
                effectiveScalars.HasMinToolTierOverride = true;
                effectiveScalars.MinToolTier = candidateScalars.MinToolTier;
            }
        }

        if (!effectiveScalars.HasHealthOverride &&
            !effectiveScalars.HasMinToolTierOverride)
        {
            return false;
        }

        resolvedScalars = effectiveScalars;
        signature = ComputeLazyDamageableScalarSignature(effectiveScalars);
        return true;
    }

    private static bool TryResolveLazyDestructibleScalars(
        GameObject gameObject,
        out CompiledDestructibleComponentDefinition resolvedDefinition,
        out int signature)
    {
        resolvedDefinition = null!;
        signature = 0;
        if (!TryGetConditionalDropContext(gameObject, out _, out _, out List<CompiledObjectDropRule> compiledRules))
        {
            return false;
        }

        CompiledDestructibleComponentDefinition effectiveDefinition = new();
        foreach (CompiledObjectDropRule compiledRule in compiledRules)
        {
            if (compiledRule.Destructible == null ||
                !CanUseLazyDestructibleScalarFastPath(compiledRule.Entry) ||
                !EntryMatches(gameObject, compiledRule.Entry, allowConditionalMatches: true))
            {
                continue;
            }

            if (compiledRule.Destructible.HasHealthOverride)
            {
                effectiveDefinition.HasHealthOverride = true;
                effectiveDefinition.Health = compiledRule.Destructible.Health;
            }

            if (compiledRule.Destructible.HasMinToolTierOverride)
            {
                effectiveDefinition.HasMinToolTierOverride = true;
                effectiveDefinition.MinToolTier = compiledRule.Destructible.MinToolTier;
            }
        }

        if (!effectiveDefinition.HasHealthOverride &&
            !effectiveDefinition.HasMinToolTierOverride)
        {
            return false;
        }

        resolvedDefinition = effectiveDefinition;
        signature = ComputeLazyDestructibleScalarSignature(effectiveDefinition);
        return true;
    }

    private static bool TryResolveLazyDestructibleType(GameObject gameObject, out DestructibleType resolvedType)
    {
        resolvedType = DestructibleType.Default;
        List<CompiledObjectDropRule>? compiledRules;
        if (!TryGetConditionalContext(gameObject, out string prefabName, out PrefabSnapshot snapshot, out _) ||
            snapshot.Destructible == null)
        {
            return false;
        }

        EnsureRuntimeDropConfigurationState();
        if (!_runtimeDropConfigurationState.PlansByPrefab.TryGetValue(prefabName, out CompiledObjectPrefabPlan? prefabPlan) ||
            (compiledRules = prefabPlan.Rules) == null ||
            compiledRules.Count == 0)
        {
            return false;
        }

        bool hasLazyTypeOverride = false;
        DestructibleType effectiveType = snapshot.Destructible.DestructibleType;
        foreach (CompiledObjectDropRule compiledRule in compiledRules)
        {
            if (compiledRule.Destructible?.HasDestructibleTypeOverride != true)
            {
                continue;
            }

            if (!CanUseLazyDestructibleScalarFastPath(compiledRule.Entry))
            {
                return false;
            }

            hasLazyTypeOverride = true;
            if (EntryMatches(gameObject, compiledRule.Entry, allowConditionalMatches: true))
            {
                effectiveType = compiledRule.Destructible.DestructibleType;
            }
        }

        if (!hasLazyTypeOverride)
        {
            return false;
        }

        resolvedType = effectiveType;
        return true;
    }

    private static CompiledDamageableScalarDefinition? GetDamageableScalarDefinition(
        CompiledObjectDropRule compiledRule,
        LiveObjectComponentKind componentKind)
    {
        return componentKind switch
        {
            LiveObjectComponentKind.MineRock => compiledRule.MineRockScalars,
            LiveObjectComponentKind.MineRock5 => compiledRule.MineRock5Scalars,
            LiveObjectComponentKind.TreeBase => compiledRule.TreeBaseScalars,
            LiveObjectComponentKind.TreeLog => compiledRule.TreeLogScalars,
            _ => null
        };
    }

    private static int ComputeLazyDamageableScalarSignature(CompiledDamageableScalarDefinition scalars)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (scalars.HasHealthOverride ? 1 : 0);
            hash = hash * 31 + (scalars.HasHealthOverride ? BitConverter.SingleToInt32Bits(scalars.Health) : 0);
            hash = hash * 31 + (scalars.HasMinToolTierOverride ? 1 : 0);
            hash = hash * 31 + (scalars.HasMinToolTierOverride ? scalars.MinToolTier : 0);
            return hash;
        }
    }

    private static int ComputeLazyDestructibleScalarSignature(CompiledDestructibleComponentDefinition definition)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (definition.HasHealthOverride ? 1 : 0);
            hash = hash * 31 + (definition.HasHealthOverride ? BitConverter.SingleToInt32Bits(definition.Health) : 0);
            hash = hash * 31 + (definition.HasMinToolTierOverride ? 1 : 0);
            hash = hash * 31 + (definition.HasMinToolTierOverride ? definition.MinToolTier : 0);
            return hash;
        }
    }

    private static void ApplyLazyTreeBaseScalars(TreeBase treeBase, CompiledDamageableScalarDefinition scalars, int signature)
    {
        ZNetView? nview = treeBase.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int existingSignature = zdo?.GetInt(TreeBaseLazyScalarSignatureKey, int.MinValue) ?? int.MinValue;
        bool signatureMatches = existingSignature == signature;
        bool healthMatches = !scalars.HasHealthOverride || Mathf.Approximately(treeBase.m_health, scalars.Health);
        bool minToolTierMatches = !scalars.HasMinToolTierOverride || treeBase.m_minToolTier == scalars.MinToolTier;
        if (healthMatches &&
            minToolTierMatches &&
            (signatureMatches || nview == null || zdo == null || !nview.IsOwner()))
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            treeBase.m_health = scalars.Health;
        }

        if (scalars.HasMinToolTierOverride)
        {
            treeBase.m_minToolTier = scalars.MinToolTier;
        }

        if (nview == null || zdo == null || !nview.IsOwner() || signatureMatches)
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            zdo.Set(ZDOVars.s_health, Mathf.Max(scalars.Health, 0.01f));
        }

        zdo.Set(TreeBaseLazyScalarSignatureKey, signature);
    }

    private static void ApplyLazyTreeLogScalars(TreeLog treeLog, CompiledDamageableScalarDefinition scalars, int signature)
    {
        ZNetView? nview = treeLog.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int existingSignature = zdo?.GetInt(TreeLogLazyScalarSignatureKey, int.MinValue) ?? int.MinValue;
        bool signatureMatches = existingSignature == signature;
        bool healthMatches = !scalars.HasHealthOverride || Mathf.Approximately(treeLog.m_health, scalars.Health);
        bool minToolTierMatches = !scalars.HasMinToolTierOverride || treeLog.m_minToolTier == scalars.MinToolTier;
        if (healthMatches &&
            minToolTierMatches &&
            (signatureMatches || nview == null || zdo == null || !nview.IsOwner()))
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            treeLog.m_health = scalars.Health;
        }

        if (scalars.HasMinToolTierOverride)
        {
            treeLog.m_minToolTier = scalars.MinToolTier;
        }

        if (nview == null || zdo == null || !nview.IsOwner() || signatureMatches)
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            zdo.Set(ZDOVars.s_health, GetScaledMineHealth(Mathf.Max(scalars.Health, 0.01f)));
        }

        zdo.Set(TreeLogLazyScalarSignatureKey, signature);
    }

    private static void ApplyLazyMineRockScalars(MineRock mineRock, CompiledDamageableScalarDefinition scalars, int signature)
    {
        ZNetView? nview = mineRock.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int existingSignature = zdo?.GetInt(MineRockLazyScalarSignatureKey, int.MinValue) ?? int.MinValue;
        bool signatureMatches = existingSignature == signature;
        bool healthMatches = !scalars.HasHealthOverride || Mathf.Approximately(mineRock.m_health, scalars.Health);
        bool minToolTierMatches = !scalars.HasMinToolTierOverride || mineRock.m_minToolTier == scalars.MinToolTier;
        if (healthMatches &&
            minToolTierMatches &&
            (signatureMatches || nview == null || zdo == null || !nview.IsOwner()))
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            mineRock.m_health = scalars.Health;
        }

        if (scalars.HasMinToolTierOverride)
        {
            mineRock.m_minToolTier = scalars.MinToolTier;
        }

        if (nview == null || zdo == null || !nview.IsOwner() || signatureMatches)
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            SetMineRockAreaHealthAbsolute(mineRock, GetScaledMineHealth(Mathf.Max(scalars.Health, 0.01f)));
        }

        zdo.Set(MineRockLazyScalarSignatureKey, signature);
    }

    private static void ApplyLazyMineRock5Scalars(MineRock5 mineRock5, CompiledDamageableScalarDefinition scalars, int signature)
    {
        ZNetView? nview = mineRock5.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int existingSignature = zdo?.GetInt(MineRock5LazyScalarSignatureKey, int.MinValue) ?? int.MinValue;
        bool signatureMatches = existingSignature == signature;
        bool healthMatches = !scalars.HasHealthOverride || Mathf.Approximately(mineRock5.m_health, scalars.Health);
        bool minToolTierMatches = !scalars.HasMinToolTierOverride || mineRock5.m_minToolTier == scalars.MinToolTier;
        if (healthMatches &&
            minToolTierMatches &&
            (signatureMatches || nview == null || zdo == null || !nview.IsOwner()))
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            mineRock5.m_health = scalars.Health;
        }

        if (scalars.HasMinToolTierOverride)
        {
            mineRock5.m_minToolTier = scalars.MinToolTier;
        }

        if (nview == null || zdo == null || !nview.IsOwner() || signatureMatches)
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            SetMineRock5AreaHealthAbsolute(mineRock5, GetScaledMineHealth(Mathf.Max(scalars.Health, 0.01f)));
        }

        zdo.Set(MineRock5LazyScalarSignatureKey, signature);
    }

    private static void ApplyLazyDestructibleScalars(Destructible destructible, CompiledDestructibleComponentDefinition definition, int signature)
    {
        ZNetView? nview = destructible.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int existingSignature = zdo?.GetInt(DestructibleLazyScalarSignatureKey, int.MinValue) ?? int.MinValue;
        bool signatureMatches = existingSignature == signature;
        bool healthMatches = !definition.HasHealthOverride || Mathf.Approximately(destructible.m_health, definition.Health);
        bool minToolTierMatches = !definition.HasMinToolTierOverride || destructible.m_minToolTier == definition.MinToolTier;
        if (healthMatches &&
            minToolTierMatches &&
            (signatureMatches || nview == null || zdo == null || !nview.IsOwner()))
        {
            return;
        }

        if (definition.HasHealthOverride)
        {
            destructible.m_health = definition.Health;
        }

        if (definition.HasMinToolTierOverride)
        {
            destructible.m_minToolTier = definition.MinToolTier;
        }

        if (nview == null || zdo == null || !nview.IsOwner() || signatureMatches)
        {
            return;
        }

        if (definition.HasHealthOverride)
        {
            zdo.Set(ZDOVars.s_health, GetScaledMineHealth(Mathf.Max(definition.Health, 0.01f)));
        }

        zdo.Set(DestructibleLazyScalarSignatureKey, signature);
    }

    private static bool TryGetCachedEventDropTable(
        GameObject gameObject,
        Func<CompiledObjectDropRule, CompiledDropTablePayload?> payloadSelector,
        Func<PrefabSnapshot, DropTable?> snapshotSelector,
        LiveObjectComponentKind componentKind,
        out DropTable? overrideTable)
    {
        overrideTable = null;
        if (!TryGetConditionalContext(gameObject, out string prefabName, out PrefabSnapshot snapshot, out List<PrefabConfigurationEntry> entries))
        {
            return false;
        }

        EnsureRuntimeDropConfigurationState();
        if (!_runtimeDropConfigurationState.PlansByPrefab.TryGetValue(prefabName, out CompiledObjectPrefabPlan? prefabPlan) ||
            prefabPlan.Rules.Count == 0)
        {
            return false;
        }

        CachedEventDropTableState cacheState = LazyRuntimeState.GetOrCreateEventDropTableState(gameObject, componentKind);
        DropTable? snapshotTable = snapshotSelector(snapshot);
        if (cacheState.Epoch != _reconcileQueueEpoch ||
            !string.Equals(cacheState.PrefabName, prefabName, StringComparison.Ordinal))
        {
            cacheState.Reset(_reconcileQueueEpoch, prefabName, snapshotTable);
        }

        if (!cacheState.IsInitialized)
        {
            InitializeCachedEventDropTableState(cacheState, prefabPlan.Rules, payloadSelector);
        }

        if (!cacheState.HasCustomBlock)
        {
            return false;
        }

        int runtimeSignature = ComputeCachedEventDropTableRuntimeSignature(gameObject, cacheState);
        if (cacheState.HasCachedResolution &&
            cacheState.LastRuntimeSignature == runtimeSignature)
        {
            overrideTable = cacheState.LastResolvedDropTable;
            return cacheState.LastHasOverride;
        }

        GroupConditionalApplyPlan? groupPlan = null;
        TryGetGroupConditionalApplyPlan(gameObject, snapshot, entries, out groupPlan);

        bool hasOverride = TryBuildEffectiveDropTable(
            gameObject,
            prefabName,
            cacheState.RelevantRules,
            payloadSelector,
            cacheState.SnapshotTable,
            componentKind,
            allowConditionalMatches: true,
            groupPlan,
            out overrideTable);

        cacheState.LastRuntimeSignature = runtimeSignature;
        cacheState.HasCachedResolution = true;
        cacheState.LastHasOverride = hasOverride;
        cacheState.LastResolvedDropTable = overrideTable;
        return hasOverride;
    }

    private static void InitializeCachedEventDropTableState(
        CachedEventDropTableState cacheState,
        IEnumerable<CompiledObjectDropRule> compiledRules,
        Func<CompiledObjectDropRule, CompiledDropTablePayload?> payloadSelector)
    {
        List<CompiledObjectDropRule> relevantRules = new();
        HashSet<string>? requiredGlobalKeys = null;
        HashSet<string>? forbiddenGlobalKeys = null;

        foreach (CompiledObjectDropRule compiledRule in compiledRules)
        {
            if (payloadSelector(compiledRule) == null)
            {
                continue;
            }

            cacheState.HasCustomBlock = true;
            relevantRules.Add(compiledRule);

            ConditionsDefinition? conditions = compiledRule.Entry.Conditions;
            if (!DropConditionEvaluator.HasDynamicConditions(conditions))
            {
                continue;
            }

            cacheState.UsesTimeOfDay |= conditions?.TimeOfDay != null;
            cacheState.UsesRequiredEnvironments |= HasConfiguredConditionValues(conditions?.RequiredEnvironments);
            cacheState.UsesInsidePlayerBase |= conditions?.InsidePlayerBase.HasValue == true;
            AddNormalizedConditionValues(conditions?.RequiredGlobalKeys, ref requiredGlobalKeys);
            AddNormalizedConditionValues(conditions?.ForbiddenGlobalKeys, ref forbiddenGlobalKeys);
        }

        cacheState.RelevantRules = relevantRules.ToArray();
        cacheState.RequiredGlobalKeys = requiredGlobalKeys?.ToArray() ?? Array.Empty<string>();
        cacheState.ForbiddenGlobalKeys = forbiddenGlobalKeys?.ToArray() ?? Array.Empty<string>();
        cacheState.IsInitialized = true;
    }

    private static bool HasConfiguredConditionValues(IEnumerable<string>? values)
    {
        if (values == null)
        {
            return false;
        }

        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddNormalizedConditionValues(IEnumerable<string>? values, ref HashSet<string>? destination)
    {
        if (values == null)
        {
            return;
        }

        foreach (string value in values)
        {
            string normalizedValue = (value ?? "").Trim();
            if (normalizedValue.Length == 0)
            {
                continue;
            }

            destination ??= new HashSet<string>(StringComparer.Ordinal);
            destination.Add(normalizedValue);
        }
    }

    private static int ComputeCachedEventDropTableRuntimeSignature(
        GameObject gameObject,
        CachedEventDropTableState cacheState)
    {
        if (!cacheState.UsesTimeOfDay &&
            !cacheState.UsesRequiredEnvironments &&
            !cacheState.UsesInsidePlayerBase &&
            cacheState.RequiredGlobalKeys.Length == 0 &&
            cacheState.ForbiddenGlobalKeys.Length == 0)
        {
            return 0;
        }

        EventDropRuntimeContextSnapshot runtimeContext = LazyRuntimeState.GetOrCreateEventDropRuntimeContextSnapshot();
        int signature = 17;
        if (cacheState.UsesTimeOfDay)
        {
            signature = CombineCachedEventDropRuntimeSignature(signature, runtimeContext.TimeOfDayPhaseMarker);
        }

        if (cacheState.UsesRequiredEnvironments)
        {
            signature = CombineCachedEventDropRuntimeSignature(signature, runtimeContext.EnvironmentName);
        }

        if (cacheState.UsesInsidePlayerBase)
        {
            signature = CombineCachedEventDropRuntimeSignature(signature, GetCachedEventInsidePlayerBaseState(gameObject, cacheState));
        }

        foreach (string key in cacheState.RequiredGlobalKeys)
        {
            signature = CombineCachedEventDropRuntimeSignature(signature, key);
            signature = CombineCachedEventDropRuntimeSignature(signature, GetCachedEventGlobalKeyState(runtimeContext, key));
        }

        foreach (string key in cacheState.ForbiddenGlobalKeys)
        {
            signature = CombineCachedEventDropRuntimeSignature(signature, key);
            signature = CombineCachedEventDropRuntimeSignature(signature, GetCachedEventGlobalKeyState(runtimeContext, key));
        }

        return signature;
    }

    private static bool GetCachedEventInsidePlayerBaseState(
        GameObject gameObject,
        CachedEventDropTableState cacheState)
    {
        float now = Time.realtimeSinceStartup;
        if (now - cacheState.LastInsidePlayerBaseSampleTime >= 0.5f)
        {
            cacheState.IsInsidePlayerBase =
                EffectArea.IsPointInsideArea(gameObject.transform.position, EffectArea.Type.PlayerBase) != null;
            cacheState.LastInsidePlayerBaseSampleTime = now;
        }

        return cacheState.IsInsidePlayerBase;
    }

    private static bool GetCachedEventGlobalKeyState(EventDropRuntimeContextSnapshot runtimeContext, string key)
    {
        if (runtimeContext.GlobalKeyStates.TryGetValue(key, out bool value))
        {
            return value;
        }

        value = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(key);
        runtimeContext.GlobalKeyStates[key] = value;
        return value;
    }

    private static int CombineCachedEventDropRuntimeSignature(int current, bool value)
    {
        unchecked
        {
            return (current * 31) + (value ? 1 : 0);
        }
    }

    private static int CombineCachedEventDropRuntimeSignature(int current, int value)
    {
        unchecked
        {
            return (current * 31) + value;
        }
    }

    private static int CombineCachedEventDropRuntimeSignature(int current, string value)
    {
        unchecked
        {
            return (current * 31) + (value?.GetHashCode() ?? 0);
        }
    }

    private static bool PickableItemRandomItemsEqual(IReadOnlyList<PickableItem.RandomItem> currentItems, IReadOnlyList<PickableItem.RandomItem> desiredItems)
    {
        if (currentItems.Count != desiredItems.Count)
        {
            return false;
        }

        for (int index = 0; index < currentItems.Count; index++)
        {
            PickableItem.RandomItem currentItem = currentItems[index];
            PickableItem.RandomItem desiredItem = desiredItems[index];
            if (currentItem.m_itemPrefab != desiredItem.m_itemPrefab ||
                currentItem.m_stackMin != desiredItem.m_stackMin ||
                currentItem.m_stackMax != desiredItem.m_stackMax)
            {
                return false;
            }
        }

        return true;
    }

    private static bool PickableItemRandomWeightsEqual(PickableItem pickableItem, IReadOnlyList<float> desiredWeights)
    {
        if (!LazyRuntimeState.TryGetRandomState(pickableItem, out WeightedRandomPickableItemState currentState))
        {
            return desiredWeights.Count == 0;
        }

        if (currentState.Weights.Length != desiredWeights.Count)
        {
            return false;
        }

        for (int index = 0; index < currentState.Weights.Length; index++)
        {
            if (!Mathf.Approximately(currentState.Weights[index], desiredWeights[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static BuiltRandomPickableItems BuildRandomItems(List<RandomPickableItemDefinition> definitions, string context)
    {
        List<PickableItem.RandomItem> randomItems = new();
        List<float> weights = new();
        foreach (RandomPickableItemDefinition definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Item))
            {
                WarnInvalidEntry($"Entry '{context}' contains a randomDrops item without an item name.");
                continue;
            }

            float weight = definition.Weight ?? 1f;
            if (float.IsNaN(weight) || float.IsInfinity(weight) || weight <= 0f)
            {
                WarnInvalidEntry($"Entry '{context}' contains a randomDrops item for '{definition.Item}' with invalid weight '{weight}'. Weight must be greater than 0.");
                continue;
            }

            GameObject? prefab = ResolveItemPrefab(definition.Item, context);
            if (prefab == null)
            {
                continue;
            }

            randomItems.Add(new PickableItem.RandomItem
            {
                m_itemPrefab = prefab.GetComponent<ItemDrop>(),
                m_stackMin = Math.Max(1, definition.StackMin ?? 1),
                m_stackMax = Math.Max(Math.Max(1, definition.StackMin ?? 1), definition.StackMax ?? definition.StackMin ?? 1)
            });
            weights.Add(weight);
        }

        return new BuiltRandomPickableItems
        {
            Items = randomItems.ToArray(),
            Weights = weights.ToArray()
        };
    }

    private static void RestorePickableItem(PickableItem pickableItem, PickableItemSnapshot snapshot, bool updateRuntimeState)
    {
        ClearPickableItemRandomWeights(pickableItem);
        pickableItem.m_itemPrefab = snapshot.ItemPrefab ? snapshot.ItemPrefab.GetComponent<ItemDrop>() : null;
        pickableItem.m_stack = snapshot.Stack;
        pickableItem.m_randomItemPrefabs = snapshot.RandomItems
            .Select(item => new PickableItem.RandomItem
            {
                m_itemPrefab = item.ItemPrefab ? item.ItemPrefab.GetComponent<ItemDrop>() : null,
                m_stackMin = item.StackMin,
                m_stackMax = item.StackMax
            })
            .ToArray();

        if (updateRuntimeState)
        {
            UpdatePickableItemRuntimeState(pickableItem, pickableItem.m_randomItemPrefabs.Length > 0 && pickableItem.m_itemPrefab == null, forceRandomRefresh: false);
        }
    }

    private static void SetPickableItemRandomWeights(PickableItem pickableItem, IReadOnlyList<float> weights)
    {
        LazyRuntimeState.SetRandomWeights(pickableItem, weights.ToArray());
    }

    private static void ClearPickableItemRandomWeights(PickableItem pickableItem)
    {
        LazyRuntimeState.ClearRandomState(pickableItem);
    }

    private static PickableItem.RandomItem SelectRandomPickableItem(PickableItem pickableItem)
    {
        PickableItem.RandomItem[] randomItems = pickableItem.m_randomItemPrefabs;
        if (randomItems.Length == 0)
        {
            return default;
        }

        if (!LazyRuntimeState.TryGetRandomState(pickableItem, out WeightedRandomPickableItemState weightedState) ||
            weightedState.Weights.Length != randomItems.Length)
        {
            return randomItems[UnityEngine.Random.Range(0, randomItems.Length)];
        }

        float totalWeight = 0f;
        for (int index = 0; index < weightedState.Weights.Length; index++)
        {
            totalWeight += Math.Max(0f, weightedState.Weights[index]);
        }

        if (totalWeight <= 0f)
        {
            return randomItems[UnityEngine.Random.Range(0, randomItems.Length)];
        }

        float roll = UnityEngine.Random.Range(0f, totalWeight);
        float cumulative = 0f;
        int lastPositiveIndex = 0;
        for (int index = 0; index < weightedState.Weights.Length; index++)
        {
            float weight = Math.Max(0f, weightedState.Weights[index]);
            if (weight <= 0f)
            {
                continue;
            }

            lastPositiveIndex = index;
            cumulative += weight;
            if (roll <= cumulative)
            {
                return randomItems[index];
            }
        }

        return randomItems[lastPositiveIndex];
    }

    private static void UpdatePickableItemRuntimeState(PickableItem pickableItem, bool useRandomItems, bool forceRandomRefresh)
    {
        ZNetView? nview = pickableItem.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();

        if (useRandomItems && pickableItem.m_randomItemPrefabs.Length > 0)
        {
            bool hasAuthoritativeZdo = zdo != null && nview != null;
            bool isOwner = nview != null && nview.IsOwner();
            bool loadSavedSelection = zdo != null && zdo.GetInt(ZDOVars.s_itemPrefab) != 0;
            if (forceRandomRefresh && zdo != null && isOwner)
            {
                zdo.Set(ZDOVars.s_itemPrefab, 0);
                zdo.Set(ZDOVars.s_itemStack, 0);
                loadSavedSelection = false;
            }

            if (loadSavedSelection)
            {
                int prefabHash = zdo!.GetInt(ZDOVars.s_itemPrefab);
                GameObject? savedPrefab = ObjectDB.instance?.GetItemPrefab(prefabHash);
                ItemDrop? savedItemDrop = savedPrefab ? savedPrefab.GetComponent<ItemDrop>() : null;
                if (savedItemDrop != null)
                {
                    pickableItem.m_itemPrefab = savedItemDrop;
                    pickableItem.m_stack = Math.Max(1, zdo.GetInt(ZDOVars.s_itemStack, Math.Max(1, pickableItem.m_stack)));
                }
                else
                {
                    loadSavedSelection = false;
                }
            }

            if (!loadSavedSelection && hasAuthoritativeZdo && !isOwner)
            {
                pickableItem.m_itemPrefab = null;
                pickableItem.m_stack = 1;
                RefreshPickableItemVisual(pickableItem);
                return;
            }

            if (!loadSavedSelection)
            {
                PickableItem.RandomItem randomItem = SelectRandomPickableItem(pickableItem);
                pickableItem.m_itemPrefab = randomItem.m_itemPrefab;
                if (pickableItem.m_itemPrefab != null)
                {
                    pickableItem.m_stack = ScalePickableItemStack(randomItem);
                    ObjectDB? objectDb = ObjectDB.instance;
                    if (zdo != null && nview != null && nview.IsOwner() && objectDb != null)
                    {
                        int prefabHash = objectDb.GetPrefabHash(pickableItem.m_itemPrefab.gameObject);
                        zdo.Set(ZDOVars.s_itemPrefab, prefabHash);
                        zdo.Set(ZDOVars.s_itemStack, Math.Max(1, pickableItem.m_stack));
                    }
                }
            }
        }
        else if (zdo != null && nview != null && nview.IsOwner())
        {
            if (pickableItem.m_itemPrefab != null)
            {
                ObjectDB? objectDb = ObjectDB.instance;
                if (objectDb == null)
                {
                    RefreshPickableItemVisual(pickableItem);
                    return;
                }

                int prefabHash = objectDb.GetPrefabHash(pickableItem.m_itemPrefab.gameObject);
                zdo.Set(ZDOVars.s_itemPrefab, prefabHash);
                zdo.Set(ZDOVars.s_itemStack, Math.Max(1, pickableItem.m_stack));
            }
            else
            {
                zdo.Set(ZDOVars.s_itemPrefab, 0);
                zdo.Set(ZDOVars.s_itemStack, 0);
            }
        }

        RefreshPickableItemVisual(pickableItem);
    }

    private static int ScalePickableItemStack(PickableItem.RandomItem randomItem)
    {
        int min = Math.Max(1, randomItem.m_stackMin);
        int max = Math.Max(min, randomItem.m_stackMax);
        if (randomItem.m_itemPrefab == null)
        {
            return min;
        }

        if (Game.instance != null)
        {
            return Game.instance.ScaleDrops(randomItem.m_itemPrefab.m_itemData, min, max + 1);
        }

        return UnityEngine.Random.Range(min, max + 1);
    }

    private static void RefreshPickableItemVisual(PickableItem pickableItem)
    {
        if (PickableItemSetupItemMethod == null)
        {
            return;
        }

        PickableItemSetupItemMethod.Invoke(pickableItem, new object[] { false });
        PickableItemSetupItemMethod.Invoke(pickableItem, new object[] { true });
    }
}
