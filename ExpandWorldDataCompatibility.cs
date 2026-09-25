extern alias ewd;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using BepInEx.Bootstrap;
using UnityEngine;
using EwdData = ewd::Data;

namespace DropNSpawn;

// EWD owns world/AltBiome data; DNS owns normal spawn tables, raids and loot.
// Keep this boundary independent of DNS's override toggles: Off restores DNS's
// baseline, it does not hand the system to EWD. No user configuration is rewritten.
internal static class ExpandWorldDataCompatibility
{
    private static readonly ConditionalWeakTable<SpawnSystem.SpawnData, object> SourceData = new();
    private static IDictionary? _data;
    private static IDictionary? _objects;
    private static FieldInfo[] _sourceFields = Array.Empty<FieldInfo>();
    private static Assembly? _assembly;

    internal static bool IsAvailable => _assembly != null;
    internal static Type? FindType(string name) => _assembly?.GetType(name);

    internal static void DetectDependency()
    {
        // A DLL on disk is not sufficient: BepInEx must have loaded the optional plugin.
        ConfigureDependency(Chainloader.PluginInfos.TryGetValue("expand_world_data", out var plugin)
            ? plugin.Instance.GetType().Assembly : null);
        if (!IsAvailable)
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo("EWD is not loaded: standalone DNS enabled. EWD data/fields/objects and event commands require EWD 1.71 or newer.");
    }

    internal static void ConfigureDependency(Assembly? assembly)
    {
        if (assembly != null)
        {
            string? version = assembly.GetType("ExpandWorldData.EWD")?.GetField("VERSION")?.GetRawConstantValue() as string;
            if (!System.Version.TryParse(version, out System.Version parsed) || parsed < new System.Version(1, 71))
                throw new InvalidOperationException("Installed Expand World Data is unsupported; DNS requires EWD 1.71 or newer when EWD is present. Update EWD or remove it before starting DNS.");
        }
        _assembly = assembly;
    }

    internal static void RequireExtensions(string? data, Dictionary<string, string>? fields, List<string>? objects, string context)
    {
        if (IsAvailable) return;
        List<string> required = new();
        if (!string.IsNullOrWhiteSpace(data)) required.Add("data");
        if (fields?.Keys.Any(key => !string.IsNullOrWhiteSpace(key)) == true) required.Add("fields");
        if (objects?.Any(value => !string.IsNullOrWhiteSpace(value)) == true) required.Add("objects");
        if (required.Count > 0) RequireFeature(string.Join("/", required), context);
    }

    internal static void RequireFeature(string feature, string context)
    {
        if (!IsAvailable)
            throw new InvalidDataException($"Entry '{context}' uses '{feature}', which requires Expand World Data 1.71 or newer. Install EWD on the server and clients or remove these settings. This configuration update was rejected, not partially applied.");
    }

    internal static void Initialize(Harmony harmony)
    {
        if (!IsAvailable) return;
        InitializePresent(harmony, _assembly!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializePresent(Harmony harmony, Assembly assembly)
    {
        if (assembly.GetType("ExpandWorld.Spawn.Patcher") == null) return;
        var ewdHarmony = (Harmony)RequireField(RequireType(assembly, "ExpandWorldData.EWD"), "Harmony").GetValue(null);
        InstallPatches(harmony, assembly, ewdHarmony);
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            "EWD compatibility: normal Spawn/Event/Drop features are disabled while DropNSpawn is loaded (including DNS override Off). " +
            "EWD settings and YAML are unchanged; world/AltBiome data and AltBiome spawn customization remain active. Restart after changing the mod set.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void InstallPatches(Harmony harmony, Assembly assembly, Harmony ewdHarmony)
    {
        var patches = GetPatchPlan(assembly);
        if (patches.Count == 0) return; // EWD before the Spawn/Event integration.

        Type loader = RequireType(assembly, "ExpandWorld.Spawn.Loader");
        Type source = RequireType(assembly, "ExpandWorld.Spawn.Data");
        _data = (IDictionary)RequireField(loader, "Data").GetValue(null);
        _objects = (IDictionary)RequireField(loader, "Objects").GetValue(null);
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
    [MethodImpl(MethodImplOptions.NoInlining)]
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
        object? data = _data![__result];
        IList? objects = _objects![__result] as IList;
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
