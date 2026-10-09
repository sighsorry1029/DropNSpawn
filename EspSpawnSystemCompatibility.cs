using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

internal static class EspSpawnSystemCompatibility
{
    private const int RefreshFrameDelay = 1;

    private readonly struct PendingRefresh
    {
        public PendingRefresh(SpawnSystem system, int systemId, int epoch, int readyFrame)
        {
            System = system;
            SystemId = systemId;
            Epoch = epoch;
            ReadyFrame = readyFrame;
        }

        public SpawnSystem System { get; }
        public int SystemId { get; }
        public int Epoch { get; }
        public int ReadyFrame { get; }
    }

    private static readonly RingBufferQueue<PendingRefresh> PendingRefreshes = new();
    private static readonly HashSet<int> PendingRefreshIds = new();
    private static Harmony? _harmony;
    private static bool _hoverGuardPatchAttempted;
    private static bool _timerReaderPatchAttempted;
    private static Func<MonoBehaviour, int, long, double>? _originalGetElapsed;
    private static readonly AccessTools.FieldRef<SpawnSystem, ZNetView> SpawnSystemView =
        AccessTools.FieldRefAccess<SpawnSystem, ZNetView>("m_nview");
    private static bool _typesResolved;
    private static Type? _spawnSystemTextType;
    private static MethodInfo? _drawSpawnSystemsMethod;
    private static FieldInfo? _spawnSystemField;
    private static FieldInfo? _spawnDataField;
    private static bool _loggedCompatibilityFailure;

    internal static void Initialize(Harmony harmony)
    {
        _harmony ??= harmony;
        ResolveTypes();
        TryInstallHoverGuard();
    }

    private static void TryInstallHoverGuard()
    {
        TryInstallTimerReader();
        if (_hoverGuardPatchAttempted || _harmony == null || _spawnSystemTextType == null)
        {
            return;
        }

        _hoverGuardPatchAttempted = true;
        MethodInfo? getHoverTextMethod = AccessTools.DeclaredMethod(
            _spawnSystemTextType,
            "GetHoverText",
            Type.EmptyTypes);
        MethodInfo? prefixMethod = AccessTools.DeclaredMethod(
            typeof(EspSpawnSystemCompatibility),
            nameof(SpawnSystemTextGetHoverTextPrefix));
        if (_spawnSystemField == null || _spawnDataField == null || getHoverTextMethod == null || prefixMethod == null)
        {
            LogCompatibilityFailureOnce("ESP detected, but SpawnSystemText compatibility members could not be resolved.");
            return;
        }

        try
        {
            _harmony.Patch(getHoverTextMethod, prefix: new HarmonyMethod(prefixMethod));
        }
        catch (Exception ex)
        {
            LogCompatibilityFailureOnce($"Failed to install the ESP SpawnSystem hover guard. {ex}");
        }
    }

    private static void TryInstallTimerReader()
    {
        if (_timerReaderPatchAttempted || _harmony == null || _spawnSystemTextType == null) return;
        _timerReaderPatchAttempted = true;
        Assembly assembly = _spawnSystemTextType.Assembly;
        MethodInfo? text = assembly.GetType("ESP.Texts")?.GetMethod("Get", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(SpawnSystem), typeof(SpawnSystem.SpawnData), typeof(int) }, null);
        MethodInfo? elapsed = assembly.GetType("Service.Helper")?.GetMethod("GetElapsed", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(MonoBehaviour), typeof(int), typeof(long) }, null);
        if (text == null || elapsed == null || elapsed.ReturnType != typeof(double))
        {
            LogCompatibilityFailureOnce("ESP timer reader contract was not found; SpawnSystem elapsed times may be inaccurate.");
            return;
        }
        try
        {
            _originalGetElapsed = (Func<MonoBehaviour, int, long, double>)Delegate.CreateDelegate(typeof(Func<MonoBehaviour, int, long, double>), elapsed);
            _harmony.Patch(text, transpiler: new HarmonyMethod(typeof(EspSpawnSystemCompatibility), nameof(SpawnSystemTextTimerTranspiler)));
        }
        catch (Exception ex)
        {
            LogCompatibilityFailureOnce($"Failed to install ESP SpawnSystem timer reading. {ex}");
        }
    }

    private static IEnumerable<CodeInstruction> SpawnSystemTextTimerTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> result = instructions.ToList();
        int[] reads = result.Select((instruction, index) => (instruction, index)).Where(item =>
            item.instruction.operand is MethodInfo method && method.DeclaringType?.FullName == "Service.Helper" &&
            method.Name == "GetElapsed" && method.ReturnType == typeof(double) &&
            method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(MonoBehaviour), typeof(int), typeof(long) }))
            .Select(item => item.index).ToArray();
        if (reads.Length != 1) throw new InvalidOperationException("ESP SpawnSystem timer lookup changed.");
        int index = reads[0];
        CodeInstruction loadRow = new(OpCodes.Ldarg_1);
        loadRow.labels.AddRange(result[index].labels);
        loadRow.blocks.AddRange(result[index].blocks);
        result[index] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(EspSpawnSystemCompatibility), nameof(GetSpawnSystemElapsed)));
        result.Insert(index, loadRow);
        return result;
    }

    private static double GetSpawnSystemElapsed(MonoBehaviour obj, int key, long defaultValue, SpawnSystem.SpawnData row)
    {
        if (obj is SpawnSystem system && system != null && ZNet.instance != null)
        {
            ZNetView view = SpawnSystemView(system);
            if (view != null && view.IsValid() &&
                SpawnSystemTimers.TryGetElapsed(system, row, view.GetZDO(), ZNet.instance.GetTime(), out double elapsed))
                return elapsed;
        }
        return _originalGetElapsed!(obj, key, defaultValue);
    }

    internal static bool HasPendingRefreshes()
    {
        return PendingRefreshes.Count > 0;
    }

    internal static void ClearPendingRefreshes()
    {
        PendingRefreshes.Clear();
        PendingRefreshIds.Clear();
    }

    internal static void RemovePendingRefresh(int systemId)
    {
        PendingRefreshIds.Remove(systemId);
    }

    internal static void RequestRefresh(SpawnSystem? system, int epoch)
    {
        if (system == null || ShouldSkipRefresh())
        {
            return;
        }

        int instanceId = system.GetInstanceID();
        if (!PendingRefreshIds.Add(instanceId))
        {
            return;
        }

        PendingRefreshes.Enqueue(new PendingRefresh(
            system,
            instanceId,
            epoch,
            Time.frameCount + RefreshFrameDelay));
    }

    internal static bool TryProcessPendingRefresh(double deadline, int expectedEpoch)
    {
        while (PendingRefreshes.Count > 0)
        {
            if (Time.realtimeSinceStartupAsDouble >= deadline)
            {
                return false;
            }

            if (DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.SpawnSystem))
            {
                return false;
            }

            if (!PendingRefreshes.TryPeek(out PendingRefresh queuedRefresh))
            {
                continue;
            }

            if (queuedRefresh.ReadyFrame > Time.frameCount)
            {
                return false;
            }

            if (!PendingRefreshes.TryDequeue(out queuedRefresh))
            {
                continue;
            }

            PendingRefreshIds.Remove(queuedRefresh.SystemId);
            if (queuedRefresh.Epoch != expectedEpoch || queuedRefresh.System == null)
            {
                continue;
            }

            RefreshMarkers(queuedRefresh.System);
            return true;
        }

        return false;
    }

    private static void RefreshMarkers(SpawnSystem system)
    {
        if (ShouldSkipRefresh())
        {
            return;
        }

        if (!TryResolveHooks(out Type? spawnSystemTextType, out MethodInfo? drawSpawnSystemsMethod))
        {
            return;
        }

        try
        {
            HashSet<GameObject> markerObjects = new();
            foreach (Component component in system.GetComponentsInChildren(spawnSystemTextType, true))
            {
                if (component != null && component.gameObject != null)
                {
                    ClearMarkerReferences(component);
                    markerObjects.Add(component.gameObject);
                }
            }

            foreach (GameObject markerObject in markerObjects)
            {
                markerObject.SetActive(false);
                UnityEngine.Object.Destroy(markerObject);
            }

            drawSpawnSystemsMethod!.Invoke(null, new object[] { system });
        }
        catch (Exception ex)
        {
            LogCompatibilityFailureOnce($"Failed to refresh ESP SpawnSystem markers after authoritative replace. {ex}");
        }
    }

    private static void ClearMarkerReferences(Component marker)
    {
        try
        {
            _spawnSystemField?.SetValue(marker, null);
            _spawnDataField?.SetValue(marker, null);
        }
        catch (Exception ex)
        {
            LogCompatibilityFailureOnce($"Failed to clear stale ESP SpawnSystem marker references. {ex}");
        }
    }

    private static bool SpawnSystemTextGetHoverTextPrefix(object __instance, ref string __result)
    {
        try
        {
            if (ZNet.instance != null &&
                _spawnSystemField?.GetValue(__instance) is SpawnSystem spawnSystem &&
                spawnSystem != null &&
                _spawnDataField?.GetValue(__instance) != null)
            {
                ZNetView? netView = spawnSystem.GetComponent<ZNetView>();
                if (netView != null && netView.IsValid() && netView.GetZDO() != null)
                {
                    return true;
                }
            }
        }
        catch
        {
            // A marker can be destroyed between the HUD lookup and this guard.
        }

        __result = "";
        return false;
    }

    private static bool ShouldSkipRefresh()
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
        {
            return true;
        }

        return ZNet.instance != null && ZNet.instance.IsDedicated();
    }

    private static bool TryResolveHooks(out Type? spawnSystemTextType, out MethodInfo? drawSpawnSystemsMethod)
    {
        ResolveTypes();
        TryInstallHoverGuard();

        spawnSystemTextType = _spawnSystemTextType;
        drawSpawnSystemsMethod = _drawSpawnSystemsMethod;
        return spawnSystemTextType != null && drawSpawnSystemsMethod != null;
    }

    private static void ResolveTypes()
    {
        if (_typesResolved)
        {
            return;
        }

        Type? spawnSystemTextType = SafeTypeLookup.FindLoadedType("ESP.SpawnSystemText", "ESP");
        if (spawnSystemTextType == null)
        {
            return;
        }

        _spawnSystemTextType = spawnSystemTextType;
        Type? spawnSystemAwakeType = SafeTypeLookup.FindLoadedType("ESP.SpawnSystem_Awake", "ESP");
        _drawSpawnSystemsMethod = spawnSystemAwakeType != null
            ? AccessTools.Method(spawnSystemAwakeType, "DrawSpawnSystems")
            : null;
        _spawnSystemField = _spawnSystemTextType != null
            ? AccessTools.Field(_spawnSystemTextType, "spawnSystem")
            : null;
        _spawnDataField = _spawnSystemTextType != null
            ? AccessTools.Field(_spawnSystemTextType, "spawnData")
            : null;
        _typesResolved = true;
    }

    private static void LogCompatibilityFailureOnce(string message)
    {
        if (_loggedCompatibilityFailure)
        {
            return;
        }

        _loggedCompatibilityFailure = true;
        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(message);
    }
}
