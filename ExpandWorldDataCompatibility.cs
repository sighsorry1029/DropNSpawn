extern alias ewd;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using EwdData = ewd::Data;
using EwdBlueprintObject = ewd::ExpandWorldData.BlueprintObject;

namespace DropNSpawn;

// EWD owns world/AltBiome data; DNS owns normal spawn tables, raids and loot.
// Keep this boundary independent of DNS's override toggles: Off restores DNS's
// baseline, it does not hand the system to EWD. No user configuration is rewritten.
internal static class ExpandWorldDataCompatibility
{
    private static readonly ConditionalWeakTable<SpawnSystem.SpawnData, object> SourceData = new();
    private static Dictionary<SpawnSystem.SpawnData, EwdData.DataEntry?>? _data;
    private static Dictionary<SpawnSystem.SpawnData, List<EwdBlueprintObject>>? _objects;
    private static FieldInfo[] _sourceFields = Array.Empty<FieldInfo>();

    internal static void Initialize(Harmony harmony)
    {
        Assembly assembly = typeof(ewd::ExpandWorldData.EWD).Assembly;
        if (assembly.GetType("ExpandWorld.Spawn.Patcher") == null) return;
        var ewdHarmony = (Harmony)RequireField(RequireType(assembly, "ExpandWorldData.EWD"), "Harmony").GetValue(null);
        InstallPatches(harmony, assembly, ewdHarmony);
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            "EWD compatibility: normal Spawn/Event/Drop features are disabled while DropNSpawn is loaded (including DNS override Off). " +
            "EWD settings and YAML are unchanged; world/AltBiome data and AltBiome spawn customization remain active. Restart after changing the mod set.");
    }

    internal static void InstallPatches(Harmony harmony, Assembly assembly, Harmony ewdHarmony)
    {
        var patches = GetPatchPlan(assembly);
        if (patches.Count == 0) return; // EWD before the Spawn/Event integration.

        Type loader = RequireType(assembly, "ExpandWorld.Spawn.Loader");
        Type source = RequireType(assembly, "ExpandWorld.Spawn.Data");
        _data = (Dictionary<SpawnSystem.SpawnData, EwdData.DataEntry?>)RequireField(loader, "Data").GetValue(null);
        _objects = (Dictionary<SpawnSystem.SpawnData, List<EwdBlueprintObject>>)RequireField(loader, "Objects").GetValue(null);
        _sourceFields = new[] { "data", "fields", "objects", "faction" }.Select(name => RequireField(source, name)).ToArray();

        try
        {
            foreach (var patch in patches)
            {
                var method = new HarmonyMethod(typeof(ExpandWorldDataCompatibility), patch.Callback);
                harmony.Patch(patch.Target,
                    prefix: patch.Kind == HarmonyPatchType.Prefix ? method : null,
                    postfix: patch.Kind == HarmonyPatchType.Postfix ? method : null,
                    transpiler: patch.Kind == HarmonyPatchType.Transpiler ? method : null);
            }

            // EWD.Awake ran first and may already have installed these patches.
            // Run its own disable paths so its patch-state flags stay consistent.
            RefreshEwdPatches(assembly, ewdHarmony);
        }
        catch
        {
            // Do not leave a partially installed compatibility layer behind.
            foreach (var patch in patches)
            {
                try { harmony.Unpatch(patch.Target, AccessTools.DeclaredMethod(typeof(ExpandWorldDataCompatibility), patch.Callback)); }
                catch (Exception removeError)
                {
                    DropNSpawnPlugin.DropNSpawnLogger.LogError($"EWD compatibility patch removal failed; restart required. {removeError}");
                }
            }
            try { RefreshEwdPatches(assembly, ewdHarmony); }
            catch (Exception restoreError)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogError($"EWD patch restoration failed; restart required. {restoreError}");
            }
            throw;
        }
    }

    private static void RefreshEwdPatches(Assembly assembly, Harmony ewdHarmony)
    {
        foreach (string domain in new[] { "Spawn", "Event" })
            RequireMethod(RequireType(assembly, $"ExpandWorld.{domain}.Patcher"), "Patch", typeof(void), typeof(Harmony))
                .Invoke(null, new object[] { ewdHarmony });
    }

    // Resolve the complete contract before changing any patches. Keeping this plan
    // explicit also lets the test host validate the official EWD DLL without Unity.
    internal static List<(MethodInfo Target, HarmonyPatchType Kind, string Callback)> GetPatchPlan(Assembly assembly)
    {
        var result = new List<(MethodInfo, HarmonyPatchType, string)>();
        if (assembly.GetType("ExpandWorld.Spawn.Patcher") == null) return result;

        foreach (string domain in new[] { "Spawn", "Event" })
        {
            Type manager = RequireType(assembly, $"ExpandWorld.{domain}.Manager");
            foreach (string name in new[] { "CreateConfig", "ReadConfig", "Toggle" })
                Add(manager, name, typeof(void), HarmonyPatchType.Prefix, nameof(SkipOwnedDomain));
            foreach (string name in new[] { "FromSetting", "Set" })
                Add(manager, name, typeof(void), HarmonyPatchType.Prefix, nameof(SkipOwnedDomain), typeof(string));
            Add(RequireType(assembly, $"ExpandWorld.{domain}.Patcher"), "Patch", typeof(void),
                HarmonyPatchType.Transpiler, nameof(DisableOwnedFeatures), typeof(Harmony));
        }
        Add(RequireType(assembly, "ExpandWorld.Spawn.Manager"), "ApplySpawnData", typeof(void),
            HarmonyPatchType.Prefix, nameof(SkipOwnedDomain), typeof(SpawnSystem));
        Add(RequireType(assembly, "ExpandWorld.Event.Manager"), "ApplyTiming", typeof(void),
            HarmonyPatchType.Prefix, nameof(SkipOwnedDomain), typeof(RandEventSystem));
        Add(RequireType(assembly, "ExpandWorldData.Api"), "GetCurrentRandomEvent", typeof(RandomEvent),
            HarmonyPatchType.Transpiler, nameof(DisableOwnedFeatures), typeof(Vector3));

        Type data = RequireType(assembly, "ExpandWorld.Spawn.Data");
        Type loader = RequireType(assembly, "ExpandWorld.Spawn.Loader");
        Add(RequireType(assembly, "ExpandWorld.Spawn.LoaderFields"), "HandleCustomData", typeof(EwdData.DataEntry),
            HarmonyPatchType.Transpiler, nameof(DisableOwnedFeatures), data, typeof(SpawnSystem.SpawnData));
        Add(loader, "FromData", typeof(SpawnSystem.SpawnData), HarmonyPatchType.Postfix,
            nameof(CaptureSpawnData), data, typeof(string));
        Add(loader, "ToData", data, HarmonyPatchType.Postfix, nameof(RestoreSpawnData), typeof(SpawnSystem.SpawnData));
        return result;

        void Add(Type type, string name, Type returns, HarmonyPatchType kind, string callback, params Type[] args) =>
            result.Add((RequireMethod(type, name, returns, args), kind, callback));
    }

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true)!;

    private static FieldInfo RequireField(Type type, string name) =>
        AccessTools.DeclaredField(type, name) ?? throw new MissingFieldException(type.FullName, name);

    private static MethodInfo RequireMethod(Type type, string name, Type returns, params Type[] args)
    {
        MethodInfo? method = AccessTools.DeclaredMethod(type, name, args);
        if (method == null || !method.IsStatic || method.ReturnType != returns)
            throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static bool SkipOwnedDomain() => false;

    private static IEnumerable<CodeInstruction> DisableOwnedFeatures(IEnumerable<CodeInstruction> instructions)
    {
        int changed = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            // Patch consumers, not tiny getters that EWD.Awake may have inlined.
            if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
                method.DeclaringType?.FullName == "ExpandWorldData.Configuration" &&
                method.Name is "get_DataSpawns" or "get_DataEvents" or "get_DataDrops")
            {
                instruction.opcode = OpCodes.Ldc_I4_0;
                instruction.operand = null;
                changed++;
            }
            yield return instruction;
        }
        if (changed == 0)
            throw new InvalidOperationException("EWD feature gates changed; refusing an incomplete Spawn/Event compatibility patch.");
    }

    private static void CaptureSpawnData(object __0, SpawnSystem.SpawnData __result)
    {
        if (__result == null) return;
        _data!.TryGetValue(__result, out EwdData.DataEntry? data);
        _objects!.TryGetValue(__result, out List<EwdBlueprintObject>? objects);
        SpawnSystemCustomDataSupport.ApplyPreparedPayload(__result, new SpawnSystemCustomDataSupport.PreparedPayload
        {
            CustomData = data,
            CustomObjects = objects
        });
        // Transfer metadata to the existing weak table: retired AltBiome rows can
        // still serve old heightmaps until regeneration, but must not leak on reload.
        _data.Remove(__result);
        _objects.Remove(__result);
        SourceData.Remove(__result);
        SourceData.Add(__result, __0);
    }

    private static void RestoreSpawnData(SpawnSystem.SpawnData __0, object __result)
    {
        if (!SourceData.TryGetValue(__0, out object source)) return;
        // EWD serializes AltBiome from runtime SpawnData for server synchronization.
        // Its native-field exporter omits these extensions. Do not overwrite native
        // values or restore 'drops': loot remains DNS-owned.
        foreach (FieldInfo field in _sourceFields)
            field.SetValue(__result, field.GetValue(source));
    }
}
