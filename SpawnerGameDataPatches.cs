using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

[HarmonyPatch(typeof(ZoneSystem), "SpawnLocation", new[]
{
    typeof(ZoneSystem.ZoneLocation),
    typeof(int),
    typeof(Vector3),
    typeof(Quaternion),
    typeof(ZoneSystem.SpawnMode),
    typeof(List<GameObject>)
})]
internal static class ZoneSystemSpawnLocationContextPatch
{
    private static void Prefix(ZoneSystem.ZoneLocation location, ref bool __state)
    {
        __state = false;
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Spawner))
        {
            return;
        }

        SpawnerManager.BeginLocationSpawnContext(location);
        __state = true;
    }

    private static void Finalizer(bool __state)
    {
        if (!__state)
        {
            return;
        }

        SpawnerManager.EndLocationSpawnContext();
    }
}

[HarmonyPatch(typeof(DungeonGenerator), nameof(DungeonGenerator.Generate), new[]
{
    typeof(ZoneSystem.SpawnMode)
})]
internal static class DungeonGeneratorGenerateContextPatch
{
    private static void Prefix(DungeonGenerator __instance, ref bool __state)
    {
        __state = false;
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Spawner))
        {
            return;
        }

        __state = SpawnerManager.TryBeginDerivedLocationSpawnContext(__instance);
    }

    private static void Finalizer(bool __state)
    {
        if (!__state)
        {
            return;
        }

        SpawnerManager.EndLocationSpawnContext();
    }
}

[HarmonyPatch(typeof(DungeonGenerator), "Spawn")]
internal static class DungeonGeneratorSpawnContextPatch
{
    private static void Prefix(DungeonGenerator __instance, ref bool __state)
    {
        __state = false;
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Spawner))
        {
            return;
        }

        __state = SpawnerManager.TryBeginDerivedLocationSpawnContext(__instance);
    }

    private static void Finalizer(bool __state)
    {
        if (!__state)
        {
            return;
        }

        SpawnerManager.EndLocationSpawnContext();
    }
}

[HarmonyPatch(typeof(SpawnArea), nameof(SpawnArea.Awake))]
internal static class SpawnAreaAwakePatch
{
    private static void Postfix(SpawnArea __instance)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        SpawnerManager.HandleSpawnAreaInstanceAwake(__instance);
    }
}

[HarmonyPatch(typeof(SpawnArea), "UpdateSpawn")]
internal static class SpawnAreaUpdateSpawnPatch
{
    private static bool Prefix(SpawnArea __instance)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return true;
        }

        if (SpawnerManager.ShouldBlockClientSpawnerUpdate())
        {
            return false;
        }

        return SpawnerManager.PrepareSpawnAreaForUpdate(__instance);
    }
}

[HarmonyPatch(typeof(SpawnArea), "SelectWeightedPrefab")]
internal static class SpawnAreaSelectWeightedPrefabPatch
{
    private static void Postfix(SpawnArea __instance, SpawnArea.SpawnData __result)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        SpawnerManager.RecordSelectedSpawnAreaPrefab(__instance, __result);
    }
}

[HarmonyPatch(typeof(SpawnArea), "FindSpawnPoint")]
internal static class SpawnAreaFindSpawnPointPatch
{
    private static void Postfix(SpawnArea __instance, GameObject prefab, ref Vector3 point, bool __result)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        SpawnerManager.RecordSpawnAreaSpawnPoint(__instance, __result, point);
        if (__result)
        {
            SpawnerManager.InitializeSpawnAreaSpawnData(__instance, prefab, point);
        }
    }
}

[HarmonyPatch(typeof(SpawnArea), "SpawnOne")]
internal static class SpawnAreaSpawnOnePatch
{
    private static readonly MethodInfo InstantiateGameObjectMethod = typeof(UnityEngine.Object)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(method => method.Name == nameof(UnityEngine.Object.Instantiate) && method.IsGenericMethodDefinition)
        .First(method =>
        {
            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 3 &&
                   parameters[2].ParameterType == typeof(Quaternion);
        })
        .MakeGenericMethod(typeof(GameObject));

    private static void Prefix(SpawnArea __instance)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        SpawnerManager.BeginSpawnAreaSpawnAttempt(__instance);
    }

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        CodeMatcher matcher = new(instructions);
        matcher.MatchForward(false, new CodeMatch(OpCodes.Call, InstantiateGameObjectMethod));
        if (!matcher.IsValid)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogError("Failed to locate SpawnArea.SpawnOne instantiate call for faction tracking.");
            return instructions;
        }

        return matcher
            .Advance(1)
            .InsertAndAdvance(new CodeInstruction(OpCodes.Dup))
            .InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_0))
            .InsertAndAdvance(new CodeInstruction(
                OpCodes.Call,
                Transpilers.EmitDelegate<Action<GameObject, SpawnArea>>(RecordSpawnedObject).operand))
            .InstructionEnumeration();
    }

    private static void Postfix(SpawnArea __instance, bool __result)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        SpawnerManager.FinalizeSpawnAreaSpawnAttempt(__instance, __result);
    }

    private static void RecordSpawnedObject(GameObject spawnedObject, SpawnArea spawnArea)
    {
        SpawnerManager.RecordDirectSpawnAreaSpawnedObject(spawnArea, spawnedObject);
    }
}

[HarmonyPatch(typeof(CreatureSpawner), nameof(CreatureSpawner.Awake))]
internal static class CreatureSpawnerAwakePatch
{
    private static void Postfix(CreatureSpawner __instance)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        SpawnerManager.HandleCreatureSpawnerInstanceAwake(__instance);
    }
}

[HarmonyPatch(typeof(CreatureSpawner), "OnDestroy")]
internal static class CreatureSpawnerOnDestroyPatch
{
    private static void Prefix(CreatureSpawner __instance)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        SpawnerManager.UntrackCreatureSpawnerInstance(__instance);
    }
}

[HarmonyPatch(typeof(CreatureSpawner), "UpdateSpawner")]
internal static class CreatureSpawnerUpdateSpawnerPatch
{
    private static bool Prefix(CreatureSpawner __instance)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return true;
        }

        if (SpawnerManager.ShouldBlockClientSpawnerUpdate())
        {
            return false;
        }

        return SpawnerManager.PrepareCreatureSpawnerForUpdate(__instance);
    }
}

[HarmonyPatch(typeof(CreatureSpawner), "Spawn")]
internal static class CreatureSpawnerSpawnPatch
{
    private static void Prefix(CreatureSpawner __instance)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        Vector3 spawnPoint = __instance.transform.position;
        if (ZoneSystem.instance != null && ZoneSystem.instance.FindFloor(spawnPoint, out float height))
        {
            spawnPoint.y = height;
        }

        SpawnerManager.InitializeCreatureSpawnerSpawnData(__instance, __instance.m_creaturePrefab, spawnPoint);
    }

    private static void Postfix(CreatureSpawner __instance, ZNetView __result)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        SpawnerManager.ApplyCreatureSpawnerSpawnOverrides(__instance, __result);
    }
}

[HarmonyPatch(typeof(BaseAI), nameof(BaseAI.Awake))]
internal static class BaseAIAwakeFactionPatch
{
    private static void Postfix(BaseAI __instance)
    {
        FactionIntegration.ApplyFromZdo(__instance);
    }
}
