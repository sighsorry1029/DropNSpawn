using System;
using System.Collections;
using UnityEngine;

namespace DropNSpawn;

/// <summary>
/// Owns queued game-data refresh state and frame-budgeted runtime work scheduling.
/// It does not own domain-specific apply logic.
/// </summary>
internal sealed class PluginRuntimeWorkCoordinator
{
    private const float GameDataRefreshDebounceSeconds = 0.1f;
    private const float RuntimeWorkFrameBudgetSeconds = 0.001f;
    private const int WorkLaneCount = 3;

    private readonly DropNSpawnPlugin _host;
    private readonly object _gameDataRefreshLock = new();
    private Coroutine? _gameDataRefreshCoroutine;
    private DropNSpawnPlugin.ReloadDomain _deferredGameDataRefreshDomains;
    private DropNSpawnPlugin.ReloadDomain _pendingGameDataRefreshDomains;
    private float _lastQueuedGameDataRefreshTime;
    private int _reconcileRoundRobinCursor;
    private int _snapshotBuildRoundRobinCursor;
    private int _workLaneRoundRobinCursor;
    private bool _hasObservedExpandWorldDataReadyState;
    private bool _lastObservedExpandWorldDataReadyState = true;

    internal PluginRuntimeWorkCoordinator(DropNSpawnPlugin host)
    {
        _host = host;
    }

    internal void Dispose()
    {
        if (_gameDataRefreshCoroutine != null)
        {
            _host.StopCoroutine(_gameDataRefreshCoroutine);
            _gameDataRefreshCoroutine = null;
        }
    }

    internal void ProcessUpdateFrame()
    {
        RuntimeWorkProfiler.Update(Time.realtimeSinceStartup);
        ObserveExpandWorldDataReadyTransition();
        if (!NetworkPayloadSyncSupport.HasPendingWork() &&
            !HasPendingSnapshotBuildWork() &&
            !HasPendingReconcileWork())
        {
            RuntimeWorkProfiler.Update(Time.realtimeSinceStartup);
            return;
        }

        float deadline = Time.realtimeSinceStartup + RuntimeWorkFrameBudgetSeconds;
        int idlePasses = 0;
        while (Time.realtimeSinceStartup < deadline && idlePasses < 5)
        {
            bool processed = ProcessNextPendingWorkLane(deadline);
            idlePasses = processed ? 0 : idlePasses + 1;
        }

        RuntimeWorkProfiler.Update(Time.realtimeSinceStartup);
    }

    internal void QueueGameDataRefresh(DropNSpawnPlugin.ReloadDomain domains, string source)
    {
        if (domains == DropNSpawnPlugin.ReloadDomain.None)
        {
            return;
        }

        lock (_gameDataRefreshLock)
        {
            _pendingGameDataRefreshDomains |= domains;
            _deferredGameDataRefreshDomains |= domains;
            _lastQueuedGameDataRefreshTime = Time.realtimeSinceStartup;
            if (_gameDataRefreshCoroutine != null)
            {
                return;
            }

            _gameDataRefreshCoroutine = _host.StartCoroutine(ProcessQueuedGameDataRefresh(source));
        }
    }

    internal bool IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain domain)
    {
        if (domain == DropNSpawnPlugin.ReloadDomain.None)
        {
            return false;
        }

        lock (_gameDataRefreshLock)
        {
            return (_deferredGameDataRefreshDomains & domain) != 0;
        }
    }

    private void ObserveExpandWorldDataReadyTransition()
    {
        if (!BiomeResolutionSupport.IsExpandWorldDataPresent())
        {
            return;
        }

        bool isReady = BiomeResolutionSupport.IsExpandWorldDataReadyOrUnavailable();
        bool shouldReplay = false;
        if (!_hasObservedExpandWorldDataReadyState)
        {
            _hasObservedExpandWorldDataReadyState = true;
            _lastObservedExpandWorldDataReadyState = isReady;
            shouldReplay = isReady;
        }
        else if (isReady && !_lastObservedExpandWorldDataReadyState)
        {
            _lastObservedExpandWorldDataReadyState = true;
            shouldReplay = true;
        }
        else
        {
            _lastObservedExpandWorldDataReadyState = isReady;
        }

        if (!shouldReplay || !DropNSpawnPlugin.IsSourceOfTruth)
        {
            return;
        }

        bool replayed = false;
        foreach (DomainDescriptor domain in DomainRegistry.RuntimeDomains)
        {
            replayed |= domain.HandleExpandWorldDataReady();
        }

        if (replayed)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
                "ExpandWorldData biome sync became ready; replayed synchronized biome-mask publish for object, character, spawner, and spawnsystem domains.");
        }
    }

    private bool ProcessNextPendingWorkLane(float deadline)
    {
        for (int offset = 0; offset < WorkLaneCount; offset++)
        {
            int lane = (_workLaneRoundRobinCursor + offset) % WorkLaneCount;
            if (!HasPendingWork(lane))
            {
                continue;
            }

            _workLaneRoundRobinCursor = (lane + 1) % WorkLaneCount;
            if (ProcessPendingWork(lane, deadline))
            {
                return true;
            }
        }

        return false;
    }

    private bool ProcessNextPendingSnapshotBuildStep(float deadline)
    {
        for (int offset = 0; offset < DomainRegistry.SnapshotBuildDomains.Length; offset++)
        {
            int domainIndex = (_snapshotBuildRoundRobinCursor + offset) % DomainRegistry.SnapshotBuildDomains.Length;
            DomainDescriptor domain = DomainRegistry.SnapshotBuildDomains[domainIndex];
            if (domain.HasPendingSnapshotBuildWork == null ||
                domain.ProcessPendingSnapshotBuildStep == null ||
                !domain.HasPendingSnapshotBuildWork())
            {
                continue;
            }

            _snapshotBuildRoundRobinCursor = (domainIndex + 1) % DomainRegistry.SnapshotBuildDomains.Length;
            return ProcessProfiledDomainWork(
                domain,
                RuntimeWorkProfileKind.SnapshotBuild,
                domain.GetPendingSnapshotBuildWorkCount,
                domain.HasPendingSnapshotBuildWork,
                domain.ProcessPendingSnapshotBuildStep,
                deadline);
        }

        return false;
    }

    private bool HasPendingWork(int lane)
    {
        return lane switch
        {
            0 => NetworkPayloadSyncSupport.HasPendingWork(),
            1 => HasPendingSnapshotBuildWork(),
            _ => HasPendingReconcileWork()
        };
    }

    private bool ProcessPendingWork(int lane, float deadline)
    {
        return lane switch
        {
            0 => ProcessProfiledNetworkPayloadWork(deadline),
            1 => ProcessNextPendingSnapshotBuildStep(deadline),
            _ => ProcessNextQueuedReconcileStep(deadline)
        };
    }

    private static bool ProcessProfiledNetworkPayloadWork(float deadline)
    {
        if (!RuntimeWorkProfiler.IsEnabled())
        {
            return NetworkPayloadSyncSupport.ProcessPendingWork(deadline);
        }

        int pendingBefore = NetworkPayloadSyncSupport.GetPendingWorkCount();
        float startedAt = Time.realtimeSinceStartup;
        bool processed = NetworkPayloadSyncSupport.ProcessPendingWork(deadline);
        float elapsedSeconds = Time.realtimeSinceStartup - startedAt;
        int pendingAfter = NetworkPayloadSyncSupport.GetPendingWorkCount();
        RuntimeWorkProfiler.Record(
            "network",
            RuntimeWorkProfileKind.NetworkPayload,
            processed,
            pendingBefore,
            pendingAfter,
            elapsedSeconds);
        return processed;
    }

    private bool ProcessNextQueuedReconcileStep(float deadline)
    {
        for (int offset = 0; offset < DomainRegistry.ReconcileDomains.Length; offset++)
        {
            int domainIndex = (_reconcileRoundRobinCursor + offset) % DomainRegistry.ReconcileDomains.Length;
            DomainDescriptor domain = DomainRegistry.ReconcileDomains[domainIndex];
            if (domain.HasPendingReconcileWork == null ||
                domain.ProcessPendingReconcileStep == null ||
                !domain.HasPendingReconcileWork())
            {
                continue;
            }

            _reconcileRoundRobinCursor = (domainIndex + 1) % DomainRegistry.ReconcileDomains.Length;
            return ProcessProfiledDomainWork(
                domain,
                RuntimeWorkProfileKind.Reconcile,
                domain.GetPendingReconcileWorkCount,
                domain.HasPendingReconcileWork,
                domain.ProcessPendingReconcileStep,
                deadline);
        }

        return false;
    }

    private static bool ProcessProfiledDomainWork(
        DomainDescriptor domain,
        RuntimeWorkProfileKind kind,
        Func<int>? countPendingWork,
        Func<bool>? hasPendingWork,
        Func<float, bool> processStep,
        float deadline)
    {
        if (!RuntimeWorkProfiler.IsEnabled())
        {
            return processStep(deadline);
        }

        int pendingBefore = GetPendingWorkCount(countPendingWork, hasPendingWork);
        float startedAt = Time.realtimeSinceStartup;
        bool processed = processStep(deadline);
        float elapsedSeconds = Time.realtimeSinceStartup - startedAt;
        int pendingAfter = GetPendingWorkCount(countPendingWork, hasPendingWork);
        RuntimeWorkProfiler.Record(
            domain.DomainKey,
            kind,
            processed,
            pendingBefore,
            pendingAfter,
            elapsedSeconds);
        return processed;
    }

    private static int GetPendingWorkCount(Func<int>? countPendingWork, Func<bool>? hasPendingWork)
    {
        if (countPendingWork != null)
        {
            return Math.Max(0, countPendingWork());
        }

        return hasPendingWork?.Invoke() == true ? 1 : 0;
    }

    private bool HasPendingReconcileWork()
    {
        foreach (DomainDescriptor domain in DomainRegistry.ReconcileDomains)
        {
            if (domain.HasPendingReconcileWork != null &&
                domain.ProcessPendingReconcileStep != null &&
                domain.HasPendingReconcileWork())
            {
                return true;
            }
        }

        return false;
    }

    private bool HasPendingSnapshotBuildWork()
    {
        foreach (DomainDescriptor domain in DomainRegistry.SnapshotBuildDomains)
        {
            if (domain.HasPendingSnapshotBuildWork != null &&
                domain.ProcessPendingSnapshotBuildStep != null &&
                domain.HasPendingSnapshotBuildWork())
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerator ProcessQueuedGameDataRefresh(string initialSource)
    {
        string source = initialSource;
        while (true)
        {
            float queuedAt;
            lock (_gameDataRefreshLock)
            {
                queuedAt = _lastQueuedGameDataRefreshTime;
            }

            while (Time.realtimeSinceStartup - queuedAt < GameDataRefreshDebounceSeconds)
            {
                yield return null;
                lock (_gameDataRefreshLock)
                {
                    queuedAt = _lastQueuedGameDataRefreshTime;
                }
            }

            DropNSpawnPlugin.ReloadDomain domains;
            lock (_gameDataRefreshLock)
            {
                domains = _pendingGameDataRefreshDomains;
                _pendingGameDataRefreshDomains = DropNSpawnPlugin.ReloadDomain.None;
            }

            if (domains == DropNSpawnPlugin.ReloadDomain.None)
            {
                lock (_gameDataRefreshLock)
                {
                    _gameDataRefreshCoroutine = null;
                }

                yield break;
            }

            DropNSpawnPlugin.DropNSpawnLogger.LogDebug($"Processing queued game-data refresh after {source} for domains: {domains}.");
            PrefabOwnerResolver.Invalidate();

            foreach (DomainDescriptor domain in DomainRegistry.RuntimeDomains)
            {
                if ((domains & domain.ReloadDomain) == 0)
                {
                    continue;
                }

                domain.OnGameDataReady(source);
                MarkGameDataRefreshDomainProcessed(domain.ReloadDomain);
                if (ShouldYieldBetweenQueuedGameDataRefreshDomains(domains, domain.ReloadDomain))
                {
                    yield return null;
                }
            }

            source = "queued game-data refresh";
        }
    }

    private bool ShouldYieldBetweenQueuedGameDataRefreshDomains(
        DropNSpawnPlugin.ReloadDomain domains,
        DropNSpawnPlugin.ReloadDomain processedDomain)
    {
        DropNSpawnPlugin.ReloadDomain remainingDomains = domains & ~processedDomain;
        if (remainingDomains != DropNSpawnPlugin.ReloadDomain.None)
        {
            return true;
        }

        lock (_gameDataRefreshLock)
        {
            return _pendingGameDataRefreshDomains != DropNSpawnPlugin.ReloadDomain.None;
        }
    }

    private void MarkGameDataRefreshDomainProcessed(DropNSpawnPlugin.ReloadDomain domain)
    {
        lock (_gameDataRefreshLock)
        {
            if ((_pendingGameDataRefreshDomains & domain) == 0)
            {
                _deferredGameDataRefreshDomains &= ~domain;
            }
        }
    }
}
