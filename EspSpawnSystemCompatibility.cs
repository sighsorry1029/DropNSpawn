using System;
using System.Collections.Generic;
using System.Reflection;
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
    private static bool _typesResolved;
    private static Type? _spawnSystemTextType;
    private static MethodInfo? _drawSpawnSystemsMethod;
    private static bool _loggedRefreshFailure;

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

    internal static bool TryProcessPendingRefresh(float deadline, int expectedEpoch)
    {
        while (PendingRefreshes.Count > 0)
        {
            if (Time.realtimeSinceStartup >= deadline)
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
            if (_loggedRefreshFailure)
            {
                return;
            }

            _loggedRefreshFailure = true;
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Failed to refresh ESP SpawnSystem markers after authoritative replace. {ex}");
        }
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
        if (!_typesResolved)
        {
            _spawnSystemTextType = SafeTypeLookup.FindLoadedType("ESP.SpawnSystemText", "ESP");
            Type? spawnSystemAwakeType = SafeTypeLookup.FindLoadedType("ESP.SpawnSystem_Awake", "ESP");
            _drawSpawnSystemsMethod = spawnSystemAwakeType != null
                ? AccessTools.Method(spawnSystemAwakeType, "DrawSpawnSystems")
                : null;
            _typesResolved = true;
        }

        spawnSystemTextType = _spawnSystemTextType;
        drawSpawnSystemsMethod = _drawSpawnSystemsMethod;
        return spawnSystemTextType != null && drawSpawnSystemsMethod != null;
    }
}
