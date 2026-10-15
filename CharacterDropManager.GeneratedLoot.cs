using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

// Policies follow actual successful rows. The game only retains prefab/amount
// pairs, which cannot identify a row when another row for that prefab failed.
internal static partial class CharacterDropManager
{
    internal static bool DropRowTrackingAvailable;
    internal static bool SaveStackPolicyAvailable;
    internal static bool LoadStackPolicyAvailable;
    private static bool SavedStackPolicyAvailable => SaveStackPolicyAvailable && LoadStackPolicyAvailable;
    private static readonly ConditionalWeakTable<CharacterDrop.Drop, CompiledCharacterDropDefinition> RowPolicies = new();
    private static readonly ConditionalWeakTable<CharacterDrop.Drop, CharacterDrop.Drop> OriginalRows = new();
    private static readonly ConditionalWeakTable<List<KeyValuePair<GameObject, int>>, List<GeneratedDropPolicy>> GeneratedPolicies = new();
    private static readonly AccessTools.FieldRef<Ragdoll, ZNetView> RagdollView = AccessTools.FieldRefAccess<Ragdoll, ZNetView>("m_nview");
    private static readonly int SavedStackPolicyKey = "DropNSpawn.LootStack.v1".GetStableHashCode();

    private sealed class GeneratedDropPolicy
    {
        internal KeyValuePair<GameObject, int> Result;
        internal CharacterDrop.Drop? Source;
        internal bool NativeAmountUnscaled;
        internal bool? DropInStack;
    }

    private static CompiledCharacterDropDefinition? GetRowPolicy(CharacterDrop.Drop row) =>
        PluginSettingsFacade.IsCharacterDomainEnabled() && RowPolicies.TryGetValue(row, out var value) ? value : null;

    internal static void RecordGeneratedDrop(List<KeyValuePair<GameObject, int>> drops,
        KeyValuePair<GameObject, int> result, CharacterDrop.Drop activeRow)
    {
        drops.Add(result);
        if (!PluginSettingsFacade.IsCharacterDomainEnabled() && !IsGlobalCharacterLootLevelScalingEnabled()) return;
        CharacterDrop.Drop source = OriginalRows.TryGetValue(activeRow, out var original) ? original : activeRow;
        GeneratedPolicies.GetOrCreateValue(drops).Add(new GeneratedDropPolicy
        {
            Result = result,
            Source = source,
            NativeAmountUnscaled = !activeRow.m_levelMultiplier || activeRow.m_levelMultiplierNotAmount,
            DropInStack = GetRowPolicy(source)?.DropInStack
        });
    }

    // A foreign postfix may replace/reorder/change the result. Never guess row
    // identity by prefab in that case; leave that mod's result untouched.
    private static List<GeneratedDropPolicy>? GetMatchingGeneratedPolicies(List<KeyValuePair<GameObject, int>> drops)
    {
        if (!GeneratedPolicies.TryGetValue(drops, out var policies) || policies.Count != drops.Count) return null;
        for (int index = 0; index < drops.Count; index++)
            if (!ReferenceEquals(drops[index].Key, policies[index].Result.Key) || drops[index].Value != policies[index].Result.Value)
                return null;
        return policies;
    }

    internal static void KeepUnmatchedDropsLoose(List<KeyValuePair<GameObject, int>> drops)
    {
        if (!DropRowTrackingAvailable || !PluginSettingsFacade.IsCharacterDomainEnabled() ||
            drops.Count == 0 || GetMatchingGeneratedPolicies(drops) != null) return;
        GeneratedPolicies.Remove(drops);
        var policies = GeneratedPolicies.GetOrCreateValue(drops);
        foreach (var drop in drops)
            policies.Add(new GeneratedDropPolicy { Result = drop, DropInStack = false });
    }

    internal static List<KeyValuePair<GameObject, int>> GenerateAndSaveStackPolicy(CharacterDrop source, Ragdoll ragdoll)
    {
        List<KeyValuePair<GameObject, int>> drops = source.GenerateDropList();
        KeepUnmatchedDropsLoose(drops);
        ZNetView view = RagdollView(ragdoll);
        if (DropRowTrackingAvailable && SavedStackPolicyAvailable && view != null && view.IsValid() && view.IsOwner())
        {
            var policies = GetMatchingGeneratedPolicies(drops);
            bool hasExplicit = policies?.Exists(policy => policy.DropInStack.HasValue) == true;
            byte[] data = Array.Empty<byte>();
            if (hasExplicit)
            {
                using MemoryStream stream = new();
                using BinaryWriter writer = new(stream);
                writer.Write(1);
                writer.Write(drops.Count);
                for (int index = 0; index < drops.Count; index++)
                {
                    writer.Write(ZNetScene.instance.GetPrefabHash(drops[index].Key));
                    writer.Write(drops[index].Value);
                    bool? stack = policies![index].DropInStack;
                    writer.Write((byte)(stack.HasValue ? stack.Value ? 2 : 1 : 0));
                }
                data = stream.ToArray();
            }
            view.GetZDO().Set(SavedStackPolicyKey, data);
        }
        return drops;
    }

    internal static void RecordSavedDrop(List<KeyValuePair<GameObject, int>> drops,
        KeyValuePair<GameObject, int> result, ZDO zdo, int savedIndex)
    {
        drops.Add(result);
        byte[]? data = zdo.GetByteArray(SavedStackPolicyKey);
        bool? stack = ReadSavedStackPolicy(data, zdo.GetInt(ZDOVars.s_drops), savedIndex,
            zdo.GetInt("drop_hash" + savedIndex), result.Value);
        // Keep every surviving row in order, including inherit and missing data.
        // The original game skips missing prefabs, hence savedIndex is essential.
        GeneratedPolicies.GetOrCreateValue(drops).Add(new GeneratedDropPolicy { Result = result, DropInStack = stack });
    }

    private static bool? ReadSavedStackPolicy(byte[]? data, int count, int index, int hash, int amount)
    {
        if (data == null || data.Length < 8 || count < 0 || index < 0 || index >= count ||
            data.Length != 8L + 9L * count || BitConverter.ToInt32(data, 0) != 1 || BitConverter.ToInt32(data, 4) != count)
            return null;
        int offset = 8 + index * 9;
        if (BitConverter.ToInt32(data, offset) != hash || BitConverter.ToInt32(data, offset + 4) != amount) return null;
        return data[offset + 8] switch { 1 => false, 2 => true, _ => null };
    }
}
