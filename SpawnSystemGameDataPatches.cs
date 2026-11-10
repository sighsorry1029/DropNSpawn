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
        SpawnSystemTimers.Detach(__instance);
        SpawnSystemManager.UntrackLiveSystem(__instance);
    }
}

[HarmonyPatch(typeof(SpawnSystem), "UpdateSpawnList", typeof(List<SpawnSystem.SpawnData>), typeof(DateTime), typeof(bool), typeof(string))]
internal static class SpawnSystemTimerPatch
{
    private static bool Prefix(SpawnSystem __instance, List<SpawnSystem.SpawnData> spawners, bool eventSpawners,
        string groupSalt, ZNetView ___m_nview, out SpawnSystemTimers.ZoneState? __state)
        => SpawnSystemTimers.Begin(__instance, spawners, eventSpawners, groupSalt, ___m_nview, out __state);

    private static Exception? Finalizer(SpawnSystemTimers.ZoneState? __state, Exception? __exception)
    {
        // Native timers are consumed before conditions/spawning; persist that
        // reservation even when another mod throws later in the spawn attempt.
        try { SpawnSystemTimers.End(__state); }
        catch (Exception ex)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to persist SpawnSystem timer batch: {ex}");
            return __exception ?? ex;
        }
        return __exception;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> input = instructions.ToList();
        LocalVariableInfo[] rows = __originalMethod.GetMethodBody()!.LocalVariables
            .Where(local => local.LocalType == typeof(SpawnSystem.SpawnData)).ToArray();
        MethodInfo get = AccessTools.Method(typeof(ZDO), nameof(ZDO.GetLong), new[] { typeof(int), typeof(long) });
        MethodInfo set = AccessTools.Method(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(long) });
        if (rows.Length != 1 || input.Count(i => i.Calls(get)) != 1 || input.Count(i => i.Calls(set)) != 1)
            throw new InvalidOperationException("SpawnSystem.UpdateSpawnList timer contract changed; refusing a partial timer replacement.");
        List<CodeInstruction> output = new(input.Count + 8);
        foreach (CodeInstruction instruction in input)
        {
            bool read = instruction.Calls(get);
            if (!read && !instruction.Calls(set)) { output.Add(instruction); continue; }
            CodeInstruction first = new(OpCodes.Ldarg_0);
            first.labels.AddRange(instruction.labels);
            first.blocks.AddRange(instruction.blocks);
            output.Add(first);
            output.Add(new CodeInstruction(OpCodes.Ldloc, rows[0].LocalIndex));
            output.Add(new CodeInstruction(OpCodes.Ldarg_3));
            output.Add(new CodeInstruction(OpCodes.Ldarg_S, (byte)4));
            output.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpawnSystemTimers),
                read ? nameof(SpawnSystemTimers.ReadTime) : nameof(SpawnSystemTimers.WriteTime))));
        }
        return output;
    }
}

[HarmonyPatch(typeof(SpawnSystem), "UpdateSpawnList", typeof(List<SpawnSystem.SpawnData>), typeof(DateTime), typeof(bool), typeof(string))]
internal static class SpawnSystemSpawnLimitPatch
{
    // The native ZDO list is a pre-spawn snapshot. Account for new instances by
    // prefab, not by row or timer ID, and use the same count for group capacity.
    private static int CountForLimit(int existing, int allNew, SpawnSystem.SpawnData row, bool enabled,
        Dictionary<int, int>? recent, ref int groupCount)
    {
        if (!enabled) return existing + allNew;
        int spawned = 0;
        recent?.TryGetValue(row.m_prefab.name.GetStableHashCode(), out spawned);
        groupCount = existing + spawned;
        return groupCount;
    }

    private static void RecordSpawn(SpawnSystem.SpawnData row, bool enabled, ref Dictionary<int, int>? recent)
    {
        if (!enabled) return;
        recent ??= new Dictionary<int, int>();
        int prefab = row.m_prefab.name.GetStableHashCode();
        recent.TryGetValue(prefab, out int spawned);
        recent[prefab] = spawned + 1;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod, ILGenerator generator)
    {
        List<CodeInstruction> input = instructions.ToList();
        MethodInfo count = AccessTools.Method(typeof(SpawnSystem), nameof(SpawnSystem.GetNrOfZDOInstances),
            new[] { typeof(GameObject), typeof(List<ZDO>), typeof(bool) });
        MethodInfo spawn = AccessTools.Method(typeof(SpawnSystem), "Spawn",
            new[] { typeof(SpawnSystem.SpawnData), typeof(Vector3), typeof(bool) });
        FieldInfo maximum = AccessTools.Field(typeof(SpawnSystem.SpawnData), nameof(SpawnSystem.SpawnData.m_maxSpawned));
        MethodBody? body = __originalMethod.GetMethodBody();
        if (count == null || spawn == null || maximum == null || body == null) return SkipCorrection(input);
        int countAt = input.FindIndex(i => i.Calls(count));
        int spawnAt = input.FindIndex(i => i.Calls(spawn));
        int existing = countAt >= 0 && countAt + 7 < input.Count ? LocalIndex(input[countAt + 1], store: true) : -1;
        int total = existing >= 0 ? LocalIndex(input[countAt + 3]) : -1;
        int row = existing >= 0 ? LocalIndex(input[countAt + 5]) : -1;
        IList<LocalVariableInfo> locals = body.LocalVariables;
        bool LocalIs(int index, Type type) => index >= 0 && index < locals.Count && locals[index].LocalType == type;
        // Match the count, comparison, remaining-group subtraction and increment
        // as one contract. Do not depend on version labels or fixed local slots.
        bool matched = countAt >= 0 && spawnAt > countAt + 7 && spawnAt + 4 < input.Count &&
            input.Count(i => i.Calls(count)) == 1 && input.Count(i => i.Calls(spawn)) == 1 &&
            LocalIs(existing, typeof(int)) && LocalIs(total, typeof(int)) && existing != total &&
            LocalIs(row, typeof(SpawnSystem.SpawnData)) && LocalIndex(input[countAt + 2]) == existing &&
            input[countAt + 4].opcode == OpCodes.Add && input[countAt + 6].LoadsField(maximum) &&
            (input[countAt + 7].opcode == OpCodes.Bge || input[countAt + 7].opcode == OpCodes.Bge_S) &&
            LocalIndex(input[spawnAt + 1]) == total && input[spawnAt + 2].opcode == OpCodes.Ldc_I4_1 &&
            input[spawnAt + 3].opcode == OpCodes.Add && LocalIndex(input[spawnAt + 4], store: true) == total;
        if (matched)
        {
            int remaining = 0;
            for (int i = countAt + 8; i + 3 < spawnAt; i++)
                if (LocalIndex(input[i]) == row && input[i + 1].LoadsField(maximum) &&
                    LocalIndex(input[i + 2]) == existing && input[i + 3].opcode == OpCodes.Sub) remaining++;
            matched = remaining == 1;
        }
        if (!matched) return SkipCorrection(input);

        LocalBuilder enabled = generator.DeclareLocal(typeof(bool));
        LocalBuilder recent = generator.DeclareLocal(typeof(Dictionary<int, int>));
        List<CodeInstruction> output = new(input.Count + 18)
        {
            new(OpCodes.Ldarg_0), new(OpCodes.Ldarg_1), new(OpCodes.Ldarg_3), new(OpCodes.Ldarg_S, (byte)4),
            new(OpCodes.Call, AccessTools.Method(typeof(SpawnSystemTimers), nameof(SpawnSystemTimers.IsManagedList))),
            new(OpCodes.Stloc, enabled), new(OpCodes.Ldnull), new(OpCodes.Stloc, recent)
        };
        for (int i = 0; i < input.Count; i++)
        {
            CodeInstruction current = new(input[i]);
            if (i == countAt + 4)
            {
                CodeInstruction first = new(OpCodes.Ldloc, row);
                first.labels.AddRange(current.labels);
                first.blocks.AddRange(current.blocks);
                output.Add(first);
                output.Add(new CodeInstruction(OpCodes.Ldloc, enabled));
                output.Add(new CodeInstruction(OpCodes.Ldloc, recent));
                output.Add(new CodeInstruction(OpCodes.Ldloca, existing));
                output.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpawnSystemSpawnLimitPatch), nameof(CountForLimit))));
                continue;
            }
            // Keep the native total for bypassed lists. Record only after Spawn
            // returns, so a thrown spawn does not reserve another local slot.
            output.Add(current);
            if (i == spawnAt)
            {
                output.Add(new CodeInstruction(OpCodes.Ldloc, row));
                output.Add(new CodeInstruction(OpCodes.Ldloc, enabled));
                output.Add(new CodeInstruction(OpCodes.Ldloca, recent));
                output.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SpawnSystemSpawnLimitPatch), nameof(RecordSpawn))));
            }
        }
        return output;
    }

    private static IEnumerable<CodeInstruction> SkipCorrection(List<CodeInstruction> input)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogWarning("Natural spawn limit correction skipped: UpdateSpawnList count/group contract changed. Existing spawn and timer behavior is unchanged.");
        return input;
    }

    private static int LocalIndex(CodeInstruction instruction, bool store = false)
    {
        OpCode op = instruction.opcode;
        if (op == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
        if (op == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
        if (op == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
        if (op == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
        if (op != (store ? OpCodes.Stloc : OpCodes.Ldloc) && op != (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)) return -1;
        return instruction.operand switch
        {
            LocalVariableInfo local => local.LocalIndex,
            int index => index,
            short index => index,
            byte index => index,
            _ => -1
        };
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
            !SpawnSystemCustomDataSupport.HasPreparedPayload(critter) &&
            !ExpandWorldDataCompatibility.HasAltBiomeKeyConsumer(critter))
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

        if (!ExpandWorldDataCompatibility.ConsumeAltBiomeKeys(critter) && PluginSettingsFacade.IsSpawnSystemDomainEnabled())
        {
            SpawnSystemManager.ConsumeExtendedRequiredGlobalKeyAfterSpawn(critter);
        }
        SpawnSystemCustomDataSupport.SpawnObjects(critter, spawnPoint);
    }

}
