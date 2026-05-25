using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

/// <summary>
/// Owns explicit despawn rule compilation and lookup caches used by despawn registration.
/// It does not own the tracked countdown state machine.
/// </summary>
internal static class CharacterDespawnRuntime
{
    private sealed class CompiledDespawnRefundDefinition
    {
        public GameObject Prefab { get; set; } = null!;
        public int Amount { get; set; }
    }

    private sealed class CompiledDespawnRule
    {
        public CharacterDropPrefabEntry Entry { get; set; } = null!;
        public float? RangeOverride { get; set; }
        public float? DelayOverride { get; set; }
        public List<CompiledDespawnRefundDefinition> Refunds { get; } = new();
        public IReadOnlyList<DespawnRefundDrop> ResolvedRefunds { get; set; } = Array.Empty<DespawnRefundDrop>();
    }

    private sealed class RuntimeState
    {
        public static RuntimeState Empty { get; } = new();

        public Dictionary<string, CompiledDespawnRule> RulesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, CompiledDespawnRule> RulesByPrefabHash { get; } = new();
        public Dictionary<int, string> PrefabNamesByHash { get; } = new();
        public HashSet<string> BootstrapPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<string> BootstrapPrefabOrder { get; set; } = Array.Empty<string>();
    }

    private static readonly HashSet<string> LoggedSkippedConditionalDespawnEntries = new(StringComparer.Ordinal);
    private static RuntimeState _runtimeState = RuntimeState.Empty;
    private static string _runtimeConfigurationSignature = "";
    private static int? _runtimeConfigurationGameDataSignature;

    internal static void Reset()
    {
        CharacterBossPolicyRuntime.Reset();
        _runtimeState = RuntimeState.Empty;
        _runtimeConfigurationSignature = "";
        _runtimeConfigurationGameDataSignature = null;
    }

    internal static IReadOnlyList<string> GetDespawnBootstrapPrefabOrder(
        string configurationSignature,
        IReadOnlyDictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab)
    {
        EnsureRuntimeState(configurationSignature, activeEntriesByPrefab);
        return _runtimeState.BootstrapPrefabOrder;
    }

    internal static bool TryResolveDespawnTrackingRule(
        string prefabName,
        string configurationSignature,
        IReadOnlyDictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab,
        out float? rangeOverride,
        out float? delayOverride,
        out IReadOnlyCollection<DespawnRefundDrop> refunds)
    {
        rangeOverride = null;
        delayOverride = null;
        refunds = Array.Empty<DespawnRefundDrop>();
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return false;
        }

        EnsureRuntimeState(configurationSignature, activeEntriesByPrefab);
        if (_runtimeState.RulesByPrefab.TryGetValue(prefabName, out CompiledDespawnRule? explicitRule))
        {
            rangeOverride = explicitRule.RangeOverride;
            delayOverride = explicitRule.DelayOverride;
            refunds = explicitRule.ResolvedRefunds;
            return true;
        }

        return CharacterBossPolicyRuntime.IsAutoDetectedBossPrefab(prefabName);
    }

    internal static bool IsEligibleDespawnTrackingPrefabName(
        string prefabName,
        string configurationSignature,
        IReadOnlyDictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return false;
        }

        EnsureRuntimeState(configurationSignature, activeEntriesByPrefab);
        return _runtimeState.BootstrapPrefabs.Contains(prefabName);
    }

    internal static bool TryResolveDespawnTrackingRule(
        int prefabHash,
        string configurationSignature,
        IReadOnlyDictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab,
        out string prefabName,
        out float? rangeOverride,
        out float? delayOverride,
        out IReadOnlyCollection<DespawnRefundDrop> refunds)
    {
        prefabName = "";
        rangeOverride = null;
        delayOverride = null;
        refunds = Array.Empty<DespawnRefundDrop>();
        if (prefabHash == 0)
        {
            return false;
        }

        EnsureRuntimeState(configurationSignature, activeEntriesByPrefab);
        if (!_runtimeState.PrefabNamesByHash.TryGetValue(prefabHash, out prefabName) ||
            string.IsNullOrWhiteSpace(prefabName))
        {
            prefabName = ResolvePrefabName(prefabHash);
        }

        if (_runtimeState.RulesByPrefabHash.TryGetValue(prefabHash, out CompiledDespawnRule? explicitRule))
        {
            rangeOverride = explicitRule.RangeOverride;
            delayOverride = explicitRule.DelayOverride;
            refunds = explicitRule.ResolvedRefunds;
            return !string.IsNullOrWhiteSpace(prefabName);
        }

        return CharacterBossPolicyRuntime.IsAutoDetectedBossPrefab(prefabHash) &&
               !string.IsNullOrWhiteSpace(prefabName);
    }

    internal static bool IsEligibleDespawnTrackingPrefabHash(
        int prefabHash,
        string configurationSignature,
        IReadOnlyDictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab)
    {
        if (prefabHash == 0)
        {
            return false;
        }

        EnsureRuntimeState(configurationSignature, activeEntriesByPrefab);
        return _runtimeState.RulesByPrefabHash.ContainsKey(prefabHash) ||
               CharacterBossPolicyRuntime.IsAutoDetectedBossPrefab(prefabHash);
    }

    private static void EnsureRuntimeState(
        string configurationSignature,
        IReadOnlyDictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab)
    {
        int gameDataSignature = CharacterDropManager.ComputeGameDataSignatureForDespawnRuntime();
        if (_runtimeConfigurationGameDataSignature == gameDataSignature &&
            string.Equals(_runtimeConfigurationSignature, configurationSignature, StringComparison.Ordinal))
        {
            return;
        }

        _runtimeState = BuildRuntimeState(activeEntriesByPrefab);
        _runtimeConfigurationGameDataSignature = gameDataSignature;
        _runtimeConfigurationSignature = configurationSignature;
    }

    private static RuntimeState BuildRuntimeState(IReadOnlyDictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab)
    {
        RuntimeState state = new();
        foreach (string prefabName in CharacterBossPolicyRuntime.GetAutoDetectedBossPrefabNames())
        {
            state.BootstrapPrefabs.Add(prefabName);
            state.PrefabNamesByHash[prefabName.GetStableHashCode()] = prefabName;
        }

        foreach ((string prefabName, List<CharacterDropPrefabEntry> entries) in activeEntriesByPrefab)
        {
            foreach (CharacterDropPrefabEntry entry in entries)
            {
                if (!TryCompileDespawnRule(entry, out CompiledDespawnRule? compiledRule))
                {
                    continue;
                }

                if (state.RulesByPrefab.TryGetValue(prefabName, out CompiledDespawnRule? previousRule))
                {
                    CharacterDropManager.WarnInvalidEntryForDespawnRuntime(
                        $"Character prefab '{prefabName}' defines multiple unconditional despawn rules. The later entry '{CharacterDropManager.BuildCompiledDropContextForDespawnRuntime(entry)}' will override the earlier entry '{CharacterDropManager.BuildCompiledDropContextForDespawnRuntime(previousRule.Entry)}'.");
                }

                state.RulesByPrefab[prefabName] = compiledRule;
                state.RulesByPrefabHash[prefabName.GetStableHashCode()] = compiledRule;
                state.PrefabNamesByHash[prefabName.GetStableHashCode()] = prefabName;
                state.BootstrapPrefabs.Add(prefabName);
            }
        }

        state.BootstrapPrefabOrder = state.BootstrapPrefabs
            .OrderBy(prefabName => prefabName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return state;
    }

    private static bool TryCompileDespawnRule(
        CharacterDropPrefabEntry entry,
        out CompiledDespawnRule compiledRule)
    {
        compiledRule = null!;
        if (entry.Despawn == null)
        {
            return false;
        }

        if (DropConditionEvaluator.HasCharacterConditions(entry.Conditions))
        {
            WarnUnsupportedConditionalDespawn(entry);
            return false;
        }

        DespawnDefinition despawn = entry.Despawn;
        string context = $"{CharacterDropManager.BuildCompiledDropContextForDespawnRuntime(entry)}/despawn";
        CompiledDespawnRule compiled = new()
        {
            Entry = entry,
            RangeOverride = despawn.Range,
            DelayOverride = despawn.Delay
        };

        foreach (DespawnRefundEntryDefinition refund in despawn.Refunds ?? Enumerable.Empty<DespawnRefundEntryDefinition>())
        {
            string itemName = (refund.Item ?? "").Trim();
            if (itemName.Length == 0)
            {
                CharacterDropManager.WarnInvalidEntryForDespawnRuntime(
                    $"Entry '{context}' contains a despawn refund without an item name.");
                continue;
            }

            GameObject? itemPrefab = CharacterDropManager.ResolveItemPrefabForDespawnRuntime(itemName, context);
            if (itemPrefab == null)
            {
                continue;
            }

            compiled.Refunds.Add(
                new CompiledDespawnRefundDefinition
                {
                    Prefab = itemPrefab,
                    Amount = Math.Max(1, refund.Amount ?? 1)
                });
        }

        compiled.ResolvedRefunds = BuildResolvedDespawnRefunds(compiled.Refunds);
        compiledRule = compiled;
        return true;
    }

    private static string ResolvePrefabName(int prefabHash)
    {
        if (prefabHash == 0 || ZNetScene.instance == null)
        {
            return "";
        }

        GameObject? prefab = ZNetScene.instance.GetPrefab(prefabHash);
        return prefab != null ? prefab.name : "";
    }

    private static IReadOnlyList<DespawnRefundDrop> BuildResolvedDespawnRefunds(IReadOnlyList<CompiledDespawnRefundDefinition> refunds)
    {
        if (refunds.Count == 0)
        {
            return Array.Empty<DespawnRefundDrop>();
        }

        DespawnRefundDrop[] resolvedRefunds = new DespawnRefundDrop[refunds.Count];
        for (int i = 0; i < refunds.Count; i++)
        {
            CompiledDespawnRefundDefinition refund = refunds[i];
            resolvedRefunds[i] = new DespawnRefundDrop(refund.Prefab, refund.Amount);
        }

        return resolvedRefunds;
    }

    private static void WarnUnsupportedConditionalDespawn(CharacterDropPrefabEntry entry)
    {
        string context = CharacterDropManager.BuildCompiledDropContextForDespawnRuntime(entry);
        if (!LoggedSkippedConditionalDespawnEntries.Add(context))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Entry '{context}' defines despawn with character conditions. Conditional despawn overrides are not supported because despawn is prefab-wide; conditions on this entry still apply to characterDrop, but the despawn block is skipped. Move despawn to an unconditional entry if it should apply. Boss prefabs without a usable explicit rule will continue using default auto-despawn settings, while non-boss prefabs without a usable explicit rule will not be tracked for despawn.");
    }
}
