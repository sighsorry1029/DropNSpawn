#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonstrumLocationManagerFix;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("Therzie.Monstrum", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency("Therzie.MonstrumDeepNorth", BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(ExpandWorldDataGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class MonstrumLocationManagerFixPlugin : BaseUnityPlugin
{
    internal const string PluginGuid = "sighsorry.MonstrumLocationManagerFix";
    internal const string PluginName = "MonstrumLocationManagerFix";
    internal const string PluginVersion = "1.0.1";

    private const string ExpandWorldDataGuid = "expand_world_data";
    private const string ExpandWorldDataDataManagerTypeFullName = "ExpandWorldData.DataManager";
    private const string ExpandWorldDataCleanGhostInitMethodName = "CleanGhostInit";
    private const string TargetTypeFullName = "LocationManager.Location";
    private const string TargetMethodName = "AddLocationToZoneSystem";
    private const string ZdoTypeFullName = "ZDO";
    private const string ZNetSceneTypeFullName = "ZNetScene";
    private const string ZNetViewTypeFullName = "ZNetView";

    private static readonly HashSet<string> TargetPluginGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "Therzie.Monstrum",
        "Therzie.MonstrumDeepNorth"
    };

    private static readonly MethodInfo? DestroyMethod =
        AccessTools.Method(typeof(Object), nameof(Object.Destroy), new[] { typeof(Object) });

    private static readonly MethodInfo? DestroyImmediateMethod =
        AccessTools.Method(typeof(Object), nameof(Object.DestroyImmediate), new[] { typeof(Object) });

    private static readonly Type? ZdoType = AccessTools.TypeByName(ZdoTypeFullName);
    private static readonly Type? ZNetSceneType = AccessTools.TypeByName(ZNetSceneTypeFullName);
    private static readonly Type? ZNetViewType = AccessTools.TypeByName(ZNetViewTypeFullName);
    private static readonly MethodInfo? ZdoCreatedSetter = ZdoType is null ? null : AccessTools.PropertySetter(ZdoType, "Created");
    private static readonly MethodInfo? ZNetSceneInstanceGetter = ZNetSceneType is null ? null : AccessTools.PropertyGetter(ZNetSceneType, "instance");
    private static readonly FieldInfo? ZNetSceneInstancesField = ZNetSceneType is null ? null : AccessTools.Field(ZNetSceneType, "m_instances");
    private static readonly MethodInfo? ZNetViewGetZdoMethod = ZNetViewType is null ? null : AccessTools.Method(ZNetViewType, "GetZDO");
    private static readonly FieldInfo? ZNetViewGhostField = ZNetViewType is null ? null : AccessTools.Field(ZNetViewType, "m_ghost");
    private static readonly FieldInfo? ZNetViewGhostInitField = ZNetViewType is null ? null : AccessTools.Field(ZNetViewType, "m_ghostInit");

    private static ManualLogSource? LogSource;
    private static bool loggedExpandWorldDataMissingScene;
    private static bool loggedExpandWorldDataMissingZdo;
    private static bool loggedExpandWorldDataReflectionFailure;

    private Harmony? harmony;

    private void Awake()
    {
        LogSource = Logger;

        harmony = new Harmony(PluginGuid);

        try
        {
            int appliedPatchCount = 0;
            appliedPatchCount += ApplyLocationManagerFixes();
            appliedPatchCount += ApplyExpandWorldDataCompatibilityFix();

            if (appliedPatchCount == 0)
            {
                Logger.LogInfo("No compatible target assemblies were found. Nothing to patch.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to apply runtime patch: {ex}");
        }
    }

    private void OnDestroy()
    {
        harmony?.UnpatchSelf();
    }

    private int ApplyLocationManagerFixes()
    {
        if (DestroyMethod is null || DestroyImmediateMethod is null)
        {
            Logger.LogError("Could not resolve UnityEngine.Object destroy methods for LocationManager patching.");
            return 0;
        }

        MethodBase[] targetMethods = FindTargetMethods().ToArray();
        if (targetMethods.Length == 0)
        {
            Logger.LogInfo("No loaded Therzie Monstrum assemblies were found for the LocationManager fix.");
            return 0;
        }

        foreach (MethodBase targetMethod in targetMethods)
        {
            harmony!.Patch(targetMethod, transpiler: new HarmonyMethod(typeof(MonstrumLocationManagerFixPlugin), nameof(ReplaceDestroyWithDestroyImmediate)));
        }

        Logger.LogInfo($"Applied runtime patch to {targetMethods.Length} LocationManager method(s).");
        return targetMethods.Length;
    }

    private int ApplyExpandWorldDataCompatibilityFix()
    {
        Type? dataManagerType = AccessTools.TypeByName(ExpandWorldDataDataManagerTypeFullName);
        if (dataManagerType is null)
        {
            return 0;
        }

        if (ZNetViewType is null)
        {
            Logger.LogWarning("Expand World Data was detected, but the Valheim ZNetView type could not be resolved.");
            return 0;
        }

        if (ZNetViewGhostField is null || ZNetViewGhostInitField is null || ZNetViewGetZdoMethod is null || ZNetSceneInstanceGetter is null || ZNetSceneInstancesField is null || ZdoCreatedSetter is null)
        {
            Logger.LogWarning("Expand World Data was detected, but the required Valheim ghost cleanup members could not be resolved.");
            return 0;
        }

        MethodInfo? cleanGhostInitMethod = AccessTools.Method(dataManagerType, ExpandWorldDataCleanGhostInitMethodName, new[] { ZNetViewType });
        if (cleanGhostInitMethod is null)
        {
            Logger.LogWarning("Expand World Data was detected, but DataManager.CleanGhostInit(ZNetView) could not be found.");
            return 0;
        }

        harmony!.Patch(cleanGhostInitMethod, prefix: new HarmonyMethod(typeof(MonstrumLocationManagerFixPlugin), nameof(ExpandWorldDataCleanGhostInitPrefix)));
        Logger.LogInfo("Applied Expand World Data compatibility patch to DataManager.CleanGhostInit(ZNetView).");
        return 1;
    }

    private static IEnumerable<MethodBase> FindTargetMethods()
    {
        HashSet<string> seenMethods = new(StringComparer.Ordinal);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!IsTargetPluginAssembly(assembly))
            {
                continue;
            }

            Type? locationType = assembly.GetType(TargetTypeFullName, throwOnError: false);
            if (locationType is null)
            {
                LogSource?.LogWarning($"Found target plugin assembly {assembly.GetName().Name}, but type {TargetTypeFullName} was missing.");
                continue;
            }

            MethodInfo[] methods = locationType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(static method => string.Equals(method.Name, TargetMethodName, StringComparison.Ordinal))
                .ToArray();

            if (methods.Length == 0)
            {
                LogSource?.LogWarning($"Found target type {TargetTypeFullName} in {assembly.GetName().Name}, but method {TargetMethodName} was missing.");
                continue;
            }

            foreach (MethodInfo method in methods)
            {
                string methodKey = $"{method.Module.ModuleVersionId:N}:{method.MetadataToken}";
                if (seenMethods.Add(methodKey))
                {
                    yield return method;
                }
            }
        }
    }

    private static bool IsTargetPluginAssembly(Assembly assembly)
    {
        try
        {
            foreach (Type type in SafeGetTypes(assembly))
            {
                object[] pluginAttributes = type.GetCustomAttributes(typeof(BepInPlugin), inherit: false);
                foreach (BepInPlugin pluginAttribute in pluginAttributes.OfType<BepInPlugin>())
                {
                    if (TargetPluginGuids.Contains(pluginAttribute.GUID))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogSource?.LogDebug($"Skipping assembly {assembly.GetName().Name} during target scan: {ex.Message}");
        }

        return false;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type is not null)!;
        }
    }

    public static bool ExpandWorldDataCleanGhostInitPrefix(object? __0)
    {
        if (!IsGhostInitActive() || __0 is null)
        {
            return false;
        }

        try
        {
            if (ZNetViewGhostField is null || ZNetViewGetZdoMethod is null || ZNetSceneInstanceGetter is null || ZNetSceneInstancesField is null || ZdoCreatedSetter is null)
            {
                LogExpandWorldDataCompatibilityWarning(ref loggedExpandWorldDataReflectionFailure, "Skipped Expand World Data ghost cleanup because required Valheim fields could not be resolved.");
                return false;
            }

            ZNetViewGhostField.SetValue(__0, true);

            object? zdo = ZNetViewGetZdoMethod.Invoke(__0, Array.Empty<object>());
            if (zdo is null)
            {
                LogExpandWorldDataCompatibilityWarning(ref loggedExpandWorldDataMissingZdo, "Skipped Expand World Data ghost cleanup because the spawned ZNetView had no ZDO.");
                return false;
            }

            object? scene = ZNetSceneInstanceGetter.Invoke(null, Array.Empty<object>());
            if (scene is null || ZNetSceneInstancesField.GetValue(scene) is not System.Collections.IDictionary instances)
            {
                LogExpandWorldDataCompatibilityWarning(ref loggedExpandWorldDataMissingScene, "Skipped Expand World Data ghost cleanup because ZNetScene instance tracking was unavailable.");
                return false;
            }

            ZdoCreatedSetter.Invoke(zdo, new object[] { false });
            instances.Remove(zdo);
        }
        catch (Exception ex)
        {
            LogExpandWorldDataCompatibilityWarning(ref loggedExpandWorldDataReflectionFailure, $"Expand World Data compatibility patch failed while cleaning ghost init: {ex.Message}");
        }

        return false;
    }

    private static bool IsGhostInitActive()
    {
        return ZNetViewGhostInitField?.GetValue(null) is bool ghostInit && ghostInit;
    }

    private static void LogExpandWorldDataCompatibilityWarning(ref bool alreadyLogged, string message)
    {
        if (alreadyLogged)
        {
            return;
        }

        alreadyLogged = true;
        LogSource?.LogWarning(message);
    }

    public static IEnumerable<CodeInstruction> ReplaceDestroyWithDestroyImmediate(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        if (DestroyMethod is null || DestroyImmediateMethod is null)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
            }

            yield break;
        }

        int replacements = 0;
        bool hasDestroyImmediateCall = false;

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(DestroyImmediateMethod))
            {
                hasDestroyImmediateCall = true;
            }

            if (instruction.Calls(DestroyMethod))
            {
                instruction.operand = DestroyImmediateMethod;
                replacements++;
                hasDestroyImmediateCall = true;
            }

            yield return instruction;
        }

        string declaringAssemblyName = original.DeclaringType?.Assembly.GetName().Name ?? "unknown";
        if (replacements > 0)
        {
            LogSource?.LogInfo($"Patched {declaringAssemblyName}.{TargetTypeFullName}.{original.Name} and replaced {replacements} Destroy call(s).");
            yield break;
        }

        if (hasDestroyImmediateCall)
        {
            LogSource?.LogInfo($"Verified {declaringAssemblyName}.{TargetTypeFullName}.{original.Name} is already using DestroyImmediate.");
            yield break;
        }

        LogSource?.LogWarning($"No matching Destroy(Object) call was found in {declaringAssemblyName}.{TargetTypeFullName}.{original.Name}.");
    }
}
