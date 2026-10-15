using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

[HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
internal static class CharacterDropGenerateDropListPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        return CharacterLootPatchInstructions.TryRewrite(instructions, ref CharacterDropManager.DropRowTrackingAvailable, input =>
        {
            int index = CharacterLootPatchInstructions.FindSingleCall(input, CharacterLootPatchInstructions.AddDrop);
            if (index < 4 || input[index - 3].opcode != OpCodes.Ldfld ||
                !Equals(input[index - 3].operand, AccessTools.Field(typeof(CharacterDrop.Drop), nameof(CharacterDrop.Drop.m_prefab))))
                throw new InvalidOperationException("CharacterDrop successful-row contract changed; refusing partial loot scaling.");
            CodeInstruction row = CharacterLootPatchInstructions.LoadLocal(input[index - 4], typeof(CharacterDrop.Drop), __originalMethod);
            CharacterLootPatchInstructions.ReplaceCall(input, index, new[] { row },
                AccessTools.Method(typeof(CharacterDropManager), nameof(CharacterDropManager.RecordGeneratedDrop)));
            return input;
        });
    }

    private readonly struct State
    {
        internal State(
            List<CharacterDrop.Drop>? previousDrops,
            bool hasOnePerPlayerScope,
            IReadOnlyList<CharacterDrop.Drop>? amountSourceDrops)
        {
            PreviousDrops = previousDrops;
            HasOnePerPlayerScope = hasOnePerPlayerScope;
            AmountSourceDrops = amountSourceDrops;
        }

        internal List<CharacterDrop.Drop>? PreviousDrops { get; }
        internal bool HasOnePerPlayerScope { get; }
        internal IReadOnlyList<CharacterDrop.Drop>? AmountSourceDrops { get; }
    }

    private static void Prefix(CharacterDrop __instance, out State __state)
    {
        __state = new State(previousDrops: null, hasOnePerPlayerScope: false, amountSourceDrops: null);
        bool isCharacterDomainEnabled = PluginSettingsFacade.IsCharacterDomainEnabled();
        if (!isCharacterDomainEnabled &&
            !CharacterDropManager.IsGlobalCharacterLootLevelScalingEnabled() &&
            !PluginSettingsFacade.IsLinearChanceOnlyDropScalingEnabled())
        {
            return;
        }

        List<CharacterDrop.Drop>? previousDrops = isCharacterDomainEnabled
            ? CharacterDropManager.OverrideConditionalDrops(__instance)
            : null;
        __state = new State(previousDrops, hasOnePerPlayerScope: false, amountSourceDrops: null);
        List<CharacterDrop.Drop>? previousScaledDrops = CharacterDropManager.PrepareCharacterLootScalingDrops(
            __instance,
            out IReadOnlyList<CharacterDrop.Drop>? amountSourceDrops);
        previousDrops ??= previousScaledDrops;
        __state = new State(previousDrops, hasOnePerPlayerScope: false, amountSourceDrops);
        bool hasOnePerPlayerScope =
            isCharacterDomainEnabled && CharacterDropManager.BeginOnePerPlayerNearbyPlayerScope(__instance);
        __state = new State(
            previousDrops,
            hasOnePerPlayerScope,
            amountSourceDrops);
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CharacterDrop __instance, List<KeyValuePair<GameObject, int>> __result, State __state)
    {
        CharacterDropManager.KeepUnmatchedDropsLoose(__result);
        CharacterDropManager.ApplyGlobalCharacterLootLevelMultiplier(__instance, __result, __state.AmountSourceDrops);

        if (__state.PreviousDrops != null)
        {
            __instance.m_drops = __state.PreviousDrops;
        }

        CharacterDropKillerFilter.SuppressScopedGeneratedDrops(__instance, __result);
    }

    private static Exception? Finalizer(CharacterDrop __instance, State __state,
        List<KeyValuePair<GameObject, int>>? __result, Exception? __exception)
    {
        if (__state.PreviousDrops != null)
        {
            __instance.m_drops = __state.PreviousDrops;
        }

        if (__state.HasOnePerPlayerScope)
        {
            CharacterDropManager.EndOnePerPlayerNearbyPlayerScope();
        }

        // Observe replacements by later postfixes as well, before DropItems or
        // SaveLootList consumes the result. Never alter a failing call's result.
        if (__exception == null && __result != null) CharacterDropManager.KeepUnmatchedDropsLoose(__result);
        return __exception;
    }
}

// Preserve the existing SaveLootList/SpawnLoot bodies and their caller-owned
// ownership/consumption rules. These hooks only carry row policy alongside loot.
[HarmonyPatch(typeof(Ragdoll), "SaveLootList")]
internal static class RagdollSaveDropPolicyPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        return CharacterLootPatchInstructions.TryRewrite(instructions, ref CharacterDropManager.SaveStackPolicyAvailable, input =>
        {
            int index = CharacterLootPatchInstructions.FindSingleCall(input,
                AccessTools.Method(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList)));
            CharacterLootPatchInstructions.ReplaceCall(input, index, new[] { new CodeInstruction(OpCodes.Ldarg_0) },
                AccessTools.Method(typeof(CharacterDropManager), nameof(CharacterDropManager.GenerateAndSaveStackPolicy)));
            return input;
        });
    }
}

[HarmonyPatch(typeof(Ragdoll), "SpawnLoot")]
internal static class RagdollLoadDropPolicyPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        return CharacterLootPatchInstructions.TryRewrite(instructions, ref CharacterDropManager.LoadStackPolicyAvailable, input =>
        {
            CodeInstruction[] FindLocals(string key)
            {
                int[] matches = input.Select((instruction, index) => (instruction, index))
                    .Where(pair => pair.instruction.opcode == OpCodes.Ldstr && Equals(pair.instruction.operand, key))
                    .Select(pair => pair.index).ToArray();
                if (matches.Length != 1 || matches[0] < 1 || matches[0] + 1 >= input.Count)
                    throw new InvalidOperationException("Ragdoll saved-row contract changed: " + key);
                int position = matches[0];
                return new[] {
                    CharacterLootPatchInstructions.LoadLocal(input[position - 1], typeof(ZDO), __originalMethod),
                    CharacterLootPatchInstructions.LoadLocal(input[position + 1], typeof(int), __originalMethod, address: true) };
            }
            CodeInstruction[] hash = FindLocals("drop_hash"), amount = FindLocals("drop_amount");
            if (!Equals(hash[0].operand, amount[0].operand) || !Equals(hash[1].operand, amount[1].operand))
                throw new InvalidOperationException("Ragdoll saved rows no longer share a ZDO/index.");
            int index = CharacterLootPatchInstructions.FindSingleCall(input, CharacterLootPatchInstructions.AddDrop);
            CharacterLootPatchInstructions.ReplaceCall(input, index, hash,
                AccessTools.Method(typeof(CharacterDropManager), nameof(CharacterDropManager.RecordSavedDrop)));
            return input;
        });
    }
}

internal static class CharacterLootPatchInstructions
{
    internal static readonly MethodInfo AddDrop = AccessTools.Method(typeof(List<KeyValuePair<GameObject, int>>), "Add");

    internal static IEnumerable<CodeInstruction> TryRewrite(IEnumerable<CodeInstruction> instructions,
        ref bool available, Func<List<CodeInstruction>, List<CodeInstruction>> rewrite)
    {
        List<CodeInstruction> original = instructions.ToList();
        available = false;
        try
        {
            List<CodeInstruction> result = rewrite(original.Select(instruction => new CodeInstruction(instruction)).ToList());
            available = true;
            return result;
        }
        catch (InvalidOperationException error)
        {
            DropNSpawnPlugin.DropNSpawnLogger?.LogWarning(
                "Loot row tracking hook unavailable; affected native amount/stack overrides are disabled, and existing loot is preserved. " + error.Message);
            return original;
        }
    }

    internal static int FindSingleCall(List<CodeInstruction> instructions, MethodInfo target)
    {
        int[] matches = instructions.Select((instruction, index) => (instruction, index))
            .Where(pair => pair.instruction.Calls(target)).Select(pair => pair.index).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Loot row hook requires one call to " + target);
        return matches[0];
    }

    internal static CodeInstruction LoadLocal(CodeInstruction instruction, Type type, MethodBase method, bool address = false)
    {
        int index;
        if (!address && instruction.opcode == OpCodes.Ldloc_0) index = 0;
        else if (!address && instruction.opcode == OpCodes.Ldloc_1) index = 1;
        else if (!address && instruction.opcode == OpCodes.Ldloc_2) index = 2;
        else if (!address && instruction.opcode == OpCodes.Ldloc_3) index = 3;
        else if (address ? instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S
            : instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S)
            index = instruction.operand is LocalVariableInfo local ? local.LocalIndex
                : instruction.operand is LocalBuilder builder ? builder.LocalIndex : Convert.ToInt32(instruction.operand);
        else throw new InvalidOperationException("Unexpected loot local instruction.");
        if (index < 0 || index >= method.GetMethodBody()!.LocalVariables.Count || method.GetMethodBody()!.LocalVariables[index].LocalType != type)
            throw new InvalidOperationException("Unexpected loot local type.");
        return new CodeInstruction(OpCodes.Ldloc, index);
    }

    internal static void ReplaceCall(List<CodeInstruction> instructions, int index, CodeInstruction[] arguments, MethodInfo target)
    {
        arguments[0].labels.AddRange(instructions[index].labels);
        arguments[0].blocks.AddRange(instructions[index].blocks);
        instructions[index] = new CodeInstruction(OpCodes.Call, target);
        instructions.InsertRange(index, arguments);
    }
}

[HarmonyPatch(typeof(ZNet), nameof(ZNet.GetNrOfPlayers))]
internal static class ZNetGetNrOfPlayersPatch
{
    private static bool Prefix(ref int __result)
    {
        if (!PluginSettingsFacade.IsCharacterDomainEnabled())
        {
            return true;
        }

        if (!CharacterDropManager.TryGetScopedOnePerPlayerNearbyPlayerCount(out int playerCount))
        {
            return true;
        }

        __result = playerCount;
        return false;
    }
}

[HarmonyPatch(typeof(CharacterDrop), "Start")]
internal static class CharacterDropStartPatch
{
    private static void Postfix(CharacterDrop __instance)
    {
        if (!PluginSettingsFacade.IsCharacterDomainEnabled())
        {
            return;
        }

        CharacterDropManager.TrackCharacterDropInstance(__instance);
    }
}

[HarmonyPatch(typeof(Character), "OnDestroy")]
internal static class CharacterOnDestroyCharacterDropPatch
{
    private static void Postfix(Character __instance)
    {
        CharacterDropKillerFilter.ForgetCharacter(__instance);

        if (__instance != null && __instance.TryGetComponent(out CharacterDrop characterDrop))
        {
            CharacterDropManager.UntrackCharacterDropInstance(characterDrop);
        }
    }
}

[HarmonyPatch(typeof(CharacterDrop), "OnDeath")]
internal static class CharacterDropOnDeathPatch
{
    private static bool Prefix(CharacterDrop __instance)
    {
        if (CharacterDropKillerFilter.ShouldSuppressDeathCallback(__instance))
        {
            return false;
        }

        if (!PluginSettingsFacade.IsCharacterDomainEnabled())
        {
            return true;
        }

        return !CharacterDropManager.TryHandleConfiguredDeath(__instance);
    }
}

[HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.DropItems))]
internal static class CharacterDropDropItemsPatch
{
    private static void Prefix(ref List<KeyValuePair<GameObject, int>> drops, Vector3 centerPos, float dropArea, bool cheated)
    {
        if (!PluginSettingsFacade.IsCharacterDomainEnabled())
        {
            return;
        }

        CharacterDropManager.ApplyGlobalDropInStack(ref drops, centerPos, dropArea, cheated);
    }
}

[HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.Setup))]
internal static class RagdollSetupMonsterInstantLootDropPatch
{
    internal const string EpicLootGuid = "randyknapp.mods.epicloot";
    private static readonly MethodInfo SpawnLootMethod =
        AccessTools.Method(typeof(Ragdoll), "SpawnLoot", new[] { typeof(Vector3) });
    private static readonly System.Action<Ragdoll, Vector3> SpawnLoot =
        AccessTools.MethodDelegate<System.Action<Ragdoll, Vector3>>(SpawnLootMethod);
    private static bool _instantLootCompatible;
    [ThreadStatic] private static Ragdoll? _instantLootRagdoll;

    internal static void InitializeEpicLootCompatibility(Harmony harmony)
    {
        bool present = Chainloader.PluginInfos.TryGetValue(EpicLootGuid, out var plugin);
        ConfigureEpicLootCompatibility(harmony, present, plugin?.Instance?.GetType().Assembly);
    }

    private static void ConfigureEpicLootCompatibility(Harmony harmony, bool present, Assembly? assembly)
    {
        _instantLootCompatible = !present;
        if (!present)
        {
            return;
        }

        try
        {
            // Inspect the loaded integration, not its version number. A missing or
            // changed callback must leave all loot on the normal delayed path.
            MethodInfo? callback = assembly?.GetType("EpicLoot.Ragdoll_SpawnLoot_Patch", false)?
                .GetMethod("Postfix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                    BindingFlags.DeclaredOnly, null, new[] { typeof(Ragdoll), typeof(Vector3) }, null);
            if (callback == null || callback.ReturnType != typeof(void) || callback.ContainsGenericParameters ||
                Harmony.GetPatchInfo(SpawnLootMethod)?.Postfixes.Any(p => p.PatchMethod == callback) != true)
            {
                throw new InvalidOperationException("The registered EpicLoot Ragdoll.SpawnLoot callback could not be verified.");
            }

            MethodInfo guard = AccessTools.DeclaredMethod(typeof(RagdollSetupMonsterInstantLootDropPatch), nameof(EpicLootSpawnLootPrefix));
            if (Harmony.GetPatchInfo(callback)?.Prefixes.Any(p => p.owner == harmony.Id && p.PatchMethod == guard) != true)
            {
                harmony.Patch(callback, prefix: new HarmonyMethod(guard));
            }
            if (Harmony.GetPatchInfo(callback)?.Prefixes.Any(p => p.owner == harmony.Id && p.PatchMethod == guard) != true)
            {
                throw new InvalidOperationException("The EpicLoot early-loot guard was not installed.");
            }
            _instantLootCompatible = true;
        }
        catch (Exception ex)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                $"EpicLoot instant-loot compatibility unavailable; monster loot will use normal ragdoll timing. Settings are unchanged. {ex}");
        }
    }

    // This patches EpicLoot's static callback, not Ragdoll.SpawnLoot itself.
    // __0 is its first ordinary argument; __instance would mean a static 'this'.
    private static bool EpicLootSpawnLootPrefix(Ragdoll __0)
    {
        return ReferenceEquals(_instantLootRagdoll, null) || !ReferenceEquals(_instantLootRagdoll, __0);
    }

    private static void SpawnInstantLoot(Ragdoll ragdoll, Vector3 center)
    {
        Ragdoll? previous = _instantLootRagdoll;
        _instantLootRagdoll = ragdoll;
        try
        {
            SpawnLoot(ragdoll, center);
        }
        finally
        {
            _instantLootRagdoll = previous;
        }
    }

    private static void Postfix(Ragdoll __instance, CharacterDrop characterDrop, ZNetView ___m_nview)
    {
        if (!_instantLootCompatible || !PluginSettingsFacade.IsMonsterInstantLootDropEnabled())
        {
            return;
        }

        if (characterDrop == null || !__instance.m_dropItems)
        {
            return;
        }

        ZNetView netView = ___m_nview;
        if (netView == null || !netView.IsValid() || !netView.IsOwner())
        {
            return;
        }

        ZDO zdo = netView.GetZDO();
        if (zdo.GetInt(ZDOVars.s_drops) <= 0)
        {
            return;
        }

        // Preserve saved loot for normal ragdoll cleanup; match the creature, not its ragdoll.
        if (PluginSettingsFacade.IsMonsterInstantLootDropBlacklisted(
                CharacterDropManager.GetPrefabName(characterDrop.gameObject)))
        {
            return;
        }

        Vector3 center = __instance.GetAverageBodyPosition();
        if (__instance.m_lootSpawnJoint != null)
        {
            center = __instance.m_lootSpawnJoint.transform.position;
        }

        SpawnInstantLoot(__instance, center);
        zdo.Set(ZDOVars.s_drops, 0);
    }
}
