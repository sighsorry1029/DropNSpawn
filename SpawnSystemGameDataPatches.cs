using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

[HarmonyPatch(typeof(SpawnSystem), "Awake")]
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
    // Spawn returns void. Capture the actual newly-created instance, not a nearby
    // creature search. EWD-present spawns keep their original pre-Awake data path.
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var result = instructions.ToList();
        int index = result.FindIndex(instruction => instruction.opcode == OpCodes.Call &&
            instruction.operand is MethodInfo method && method.DeclaringType == typeof(UnityEngine.Object) &&
            method.Name == nameof(UnityEngine.Object.Instantiate) && method.IsGenericMethod &&
            method.GetGenericArguments().SequenceEqual(new[] { typeof(GameObject) }) &&
            method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion) }));
        if (index < 0) throw new InvalidOperationException("SpawnSystem.Spawn instantiate contract changed; cannot preserve standalone faction overrides.");
        result.InsertRange(index + 1, new[]
        {
            new CodeInstruction(OpCodes.Dup),
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpawnSystemCustomDataSupport), nameof(SpawnSystemCustomDataSupport.ApplyStandaloneFaction)))
        });
        return result;
    }

    private static void Prefix(SpawnSystem.SpawnData critter, Vector3 spawnPoint, out bool __state)
    {
        __state = false;
        if (!PluginSettingsFacade.IsSpawnSystemDomainEnabled() &&
            !SpawnSystemCustomDataSupport.HasPreparedPayload(critter))
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

        if (PluginSettingsFacade.IsSpawnSystemDomainEnabled())
        {
            SpawnSystemManager.ConsumeExtendedRequiredGlobalKeyAfterSpawn(critter);
        }
        SpawnSystemCustomDataSupport.SpawnObjects(critter, spawnPoint);
    }

}
