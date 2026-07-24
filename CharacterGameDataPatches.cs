using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

[HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
internal static class CharacterDropGenerateDropListPatch
{
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
            !CharacterDropManager.IsGlobalCharacterLootLevelScalingEnabled())
        {
            return;
        }

        List<CharacterDrop.Drop>? previousDrops = isCharacterDomainEnabled
            ? CharacterDropManager.OverrideConditionalDrops(__instance)
            : null;
        __state = new State(previousDrops, hasOnePerPlayerScope: false, amountSourceDrops: null);
        List<CharacterDrop.Drop>? previousScaledDrops = CharacterDropManager.SuppressGlobalCharacterLootLevelMultiplierDrops(
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
        CharacterDropManager.ApplyGlobalCharacterLootLevelMultiplier(__instance, __result, __state.AmountSourceDrops);

        if (__state.PreviousDrops != null)
        {
            __instance.m_drops = __state.PreviousDrops;
        }
    }

    private static Exception? Finalizer(CharacterDrop __instance, State __state, Exception? __exception)
    {
        if (__state.PreviousDrops != null)
        {
            __instance.m_drops = __state.PreviousDrops;
        }

        if (__state.HasOnePerPlayerScope)
        {
            CharacterDropManager.EndOnePerPlayerNearbyPlayerScope();
        }

        return __exception;
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
    private static void Prefix(ref List<KeyValuePair<GameObject, int>> drops, Vector3 centerPos, float dropArea)
    {
        if (!PluginSettingsFacade.IsCharacterDomainEnabled())
        {
            return;
        }

        CharacterDropManager.ApplyGlobalDropInStack(ref drops, centerPos, dropArea);
    }
}

[HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.Setup))]
internal static class RagdollSetupMonsterInstantLootDropPatch
{
    private static void Postfix(Ragdoll __instance, CharacterDrop characterDrop)
    {
        if (!PluginSettingsFacade.IsMonsterInstantLootDropEnabled())
        {
            return;
        }

        if (characterDrop == null || !__instance.m_dropItems)
        {
            return;
        }

        ZNetView netView = __instance.m_nview;
        if (netView == null || !netView.IsValid() || !netView.IsOwner())
        {
            return;
        }

        ZDO zdo = netView.GetZDO();
        if (zdo.GetInt(ZDOVars.s_drops) <= 0)
        {
            return;
        }

        Vector3 center = __instance.GetAverageBodyPosition();
        if (__instance.m_lootSpawnJoint != null)
        {
            center = __instance.m_lootSpawnJoint.transform.position;
        }

        __instance.SpawnLoot(center);
        zdo.Set(ZDOVars.s_drops, 0);
    }
}
