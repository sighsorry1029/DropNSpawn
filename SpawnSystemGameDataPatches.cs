using System;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

[HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.Awake))]
[HarmonyPriority(Priority.Last)]
internal static class SpawnSystemAwakePatch
{
    private static void Prefix(SpawnSystem __instance)
    {
        SpawnSystemManager.PreAttachCompiledTableToAwakeningSystem(__instance);
    }

    private static void Postfix(SpawnSystem __instance)
    {
        SpawnSystemManager.OnSpawnSystemAwake(__instance);
    }
}

[HarmonyPatch(typeof(SpawnSystem), "OnDestroy")]
internal static class SpawnSystemOnDestroyPatch
{
    private static void Prefix(SpawnSystem __instance)
    {
        SpawnSystemManager.UntrackLiveSystem(__instance);
    }
}

[HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
internal static class SpawnSystemUpdateSpawningRequiredGlobalKeyPatch
{
    private static bool Prefix(SpawnSystem __instance, out bool __state)
    {
        __state = false;
        if (!PluginSettingsFacade.IsSpawnSystemDomainEnabled())
        {
            return true;
        }

        if (SpawnSystemManager.ShouldBlockClientSpawnSystemUpdate(__instance))
        {
            return false;
        }

        SpawnSystemManager.RefreshRuntimeTimeOfDayState();
        __state = true;
        SpawnSystemManager.EnterRequiredGlobalKeyEvaluation();
        return true;
    }

    private static Exception? Finalizer(bool __state, Exception? __exception)
    {
        if (__state)
        {
            SpawnSystemManager.ExitRequiredGlobalKeyEvaluation();
        }

        return __exception;
    }
}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGlobalKey), typeof(string))]
internal static class ZoneSystemGetGlobalKeyStringPatch
{
    private static bool Prefix(ZoneSystem __instance, string name, ref bool __result)
    {
        if (!PluginSettingsFacade.IsSpawnSystemDomainEnabled())
        {
            return true;
        }

        if (!SpawnSystemManager.TryEvaluateExtendedRequiredGlobalKey(__instance, name, out bool result))
        {
            return true;
        }

        __result = result;
        return false;
    }
}

[HarmonyPatch(typeof(ZoneSystem), "RPC_SetGlobalKey")]
internal static class ZoneSystemRpcSetGlobalKeyPatch
{
    private static void Prefix(ZoneSystem __instance, ref string name)
    {
        if (!DropNSpawnPlugin.IsSourceOfTruth || !PluginSettingsFacade.IsSpawnSystemDomainEnabled())
        {
            return;
        }

        if (SpawnSystemManager.TryRewriteExtendedGlobalKeyMutation(__instance, name, out string rewrittenName))
        {
            name = rewrittenName;
        }
    }
}

[HarmonyPatch(typeof(SpawnSystem), "Spawn")]
internal static class SpawnSystemSpawnPatch
{
    private static void Prefix(SpawnSystem.SpawnData critter, Vector3 spawnPoint, out bool __state)
    {
        __state = false;
        if (!PluginSettingsFacade.IsSpawnSystemDomainEnabled())
        {
            return;
        }

        __state = true;
        SpawnSystemCustomDataSupport.InitializeSpawn(critter, spawnPoint);
    }

    private static void Postfix(SpawnSystem.SpawnData critter, Vector3 spawnPoint, bool __state)
    {
        if (!__state)
        {
            return;
        }

        SpawnSystemManager.ConsumeExtendedRequiredGlobalKeyAfterSpawn(critter);
        SpawnSystemCustomDataSupport.SpawnObjects(critter, spawnPoint);
    }

    private static Exception? Finalizer(SpawnSystem.SpawnData critter, bool __state, Exception? __exception)
    {
        return __exception;
    }
}
