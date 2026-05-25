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
        internal State(List<CharacterDrop.Drop>? previousDrops, bool hasOnePerPlayerScope)
        {
            PreviousDrops = previousDrops;
            HasOnePerPlayerScope = hasOnePerPlayerScope;
        }

        internal List<CharacterDrop.Drop>? PreviousDrops { get; }
        internal bool HasOnePerPlayerScope { get; }
    }

    private static void Prefix(CharacterDrop __instance, out State __state)
    {
        if (!PluginSettingsFacade.IsCharacterDomainEnabled())
        {
            __state = new State(previousDrops: null, hasOnePerPlayerScope: false);
            return;
        }

        __state = new State(
            CharacterDropManager.OverrideConditionalDrops(__instance),
            CharacterDropManager.BeginOnePerPlayerNearbyPlayerScope(__instance));
    }

    private static void Postfix(CharacterDrop __instance, State __state)
    {
        if (__state.PreviousDrops != null)
        {
            __instance.m_drops = __state.PreviousDrops;
        }
    }

    private static Exception? Finalizer(State __state, Exception? __exception)
    {
        if (__state.HasOnePerPlayerScope)
        {
            CharacterDropManager.EndOnePerPlayerNearbyPlayerScope();
        }

        return __exception;
    }
}

[HarmonyPatch(typeof(Character), "RPC_Damage")]
internal static class CharacterRpcDamageBossTamedPressurePatch
{
    private static void Prefix(Character __instance, HitData hit)
    {
        BossTamedPressureRuntime.ApplyDamageMultipliers(__instance, hit);
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

[HarmonyPatch]
internal static class ZDOManCreateNewZdoDespawnPatch
{
    private static MethodBase? TargetMethod()
    {
        return AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.CreateNewZDO), new[] { typeof(ZDOID), typeof(Vector3), typeof(int) });
    }

    private static void Postfix(int prefabHashIn, ZDO __result)
    {
        DespawnRulesManager.QueueCreatedDespawnTarget(prefabHashIn, __result);
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.Awake))]
internal static class CharacterAwakeBossRulesPatch
{
    private static void Postfix(Character __instance)
    {
        BossRulesManager.TrackBossCharacter(__instance);
        if (PluginSettingsFacade.IsCharacterDomainEnabled())
        {
            DespawnRulesManager.TryTrackLoadedDespawnTarget(__instance);
        }
    }
}

[HarmonyPatch(typeof(Character), "OnDestroy")]
internal static class CharacterOnDestroyCharacterDropPatch
{
    private static void Postfix(Character __instance)
    {
        BossRulesManager.UntrackBossCharacter(__instance);
        if (__instance != null && __instance.TryGetComponent(out CharacterDrop characterDrop))
        {
            CharacterDropManager.UntrackCharacterDropInstance(characterDrop);
        }
    }
}

[HarmonyPatch(typeof(ZNetView), nameof(ZNetView.ResetZDO))]
internal static class ZNetViewResetZdoDespawnPatch
{
    private static void Prefix(ZNetView __instance)
    {
        DespawnRulesManager.TryPersistDespawnCountdownBeforeResetZdo(__instance);
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
