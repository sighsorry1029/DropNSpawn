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
    typeof(List<GameObject>),
    typeof(bool)
})]
[HarmonyAfter("expand_world_data")]
internal static class ZoneSystemSpawnLocationContextPatch
{
    private sealed class SpawnLocationContextState
    {
        public bool HasContext { get; set; }
    }

    private static void Prefix(ZoneSystem.ZoneLocation location, ref SpawnLocationContextState? __state)
    {
        __state = null;
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Spawner))
        {
            return;
        }

        SpawnerManager.BeginLocationSpawnContext(location);
        __state = new SpawnLocationContextState
        {
            HasContext = true
        };
    }

    private static void Postfix(GameObject __result, SpawnLocationContextState? __state)
    {
        if (__state?.HasContext != true)
        {
            return;
        }

        SpawnerManager.RecordSpawnedLocationRootProvenance(__result);
    }

    private static void Finalizer(SpawnLocationContextState? __state)
    {
        if (__state?.HasContext != true)
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

[HarmonyPatch(typeof(SpawnArea), "Awake")]
internal static class SpawnAreaAwakePatch
{
    private static void Postfix(SpawnArea __instance)
    {
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

[HarmonyPatch(typeof(CreatureSpawner), "Awake")]
internal static class CreatureSpawnerAwakePatch
{
    private static void Postfix(CreatureSpawner __instance)
    {
        SpawnerManager.HandleCreatureSpawnerInstanceAwake(__instance);
    }
}

[HarmonyPatch(typeof(CreatureSpawner), "OnDestroy")]
internal static class CreatureSpawnerOnDestroyPatch
{
    private static void Prefix(CreatureSpawner __instance)
    {
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
    private static bool Prefix(CreatureSpawner __instance, out ZDO? __state)
    {
        __state = null;
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return true;
        }

        if (!SpawnerManager.PrepareCreatureSpawnerSpawn(__instance, out __state)) return false;

        Vector3 spawnPoint = __instance.transform.position;
        if (ZoneSystem.instance != null && ZoneSystem.instance.FindFloor(spawnPoint, out float height))
        {
            spawnPoint.y = height;
        }

        SpawnerManager.InitializeCreatureSpawnerSpawnData(__instance, __instance.m_creaturePrefab, spawnPoint);
        return true;
    }

    private static void Postfix(CreatureSpawner __instance, ZNetView __result, ZDO? __state, bool __runOriginal)
    {
        SpawnerManager.RecordCreatureSpawnerTotalSpawn(__state, __runOriginal && __result != null && __result.IsValid());
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        SpawnerManager.ApplyCreatureSpawnerSpawnOverrides(__instance, __result);
    }
}

[HarmonyPatch(typeof(CreatureSpawner.Group), nameof(CreatureSpawner.Group.SpawnWeighted))]
internal static class CreatureSpawnerGroupSpawnPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var result = instructions.ToList();
        MethodInfo add = AccessTools.Method(typeof(List<CreatureSpawner>), nameof(List<CreatureSpawner>.Add));
        int addIndex = result.FindIndex(instruction => instruction.Calls(add));
        int start = addIndex - 3;
        // The native eligible block adds both the candidate and its weight. Skip
        // the whole block, using the existing loop-continue label, so exhausted
        // spawners cannot consume another member's weighted spawn opportunity.
        if (start < 1 || result[start].opcode != OpCodes.Ldarg_0 ||
            result[start + 1].opcode != OpCodes.Ldfld ||
            result[start + 1].operand is not FieldInfo field || field.Name != "m_activeSpawners" ||
            (result[start - 1].opcode != OpCodes.Brtrue && result[start - 1].opcode != OpCodes.Brtrue_S) ||
            result[start - 1].operand is not Label skip)
            throw new InvalidOperationException("CreatureSpawner.Group.SpawnWeighted candidate/weight contract changed.");

        var load = new CodeInstruction(result[addIndex - 1].opcode, result[addIndex - 1].operand);
        load.labels.AddRange(result[start].labels);
        load.blocks.AddRange(result[start].blocks);
        result[start].labels.Clear();
        result[start].blocks.Clear();
        result.InsertRange(start, new[]
        {
            load,
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpawnerManager), nameof(SpawnerManager.IsCreatureSpawnerGroupCandidate))),
            new CodeInstruction(OpCodes.Brfalse, skip)
        });
        return result;
    }
}

[HarmonyPatch(typeof(BaseAI), "Awake")]
internal static class BaseAIAwakeFactionPatch
{
    private static void Postfix(BaseAI __instance)
    {
        FactionIntegration.ApplyFromZdo(__instance);
    }
}
