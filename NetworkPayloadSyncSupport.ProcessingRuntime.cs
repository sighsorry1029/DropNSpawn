using System;
using System.Globalization;
using System.Threading;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private sealed class PendingReloadAction
    {
        public int RoleEpoch { get; set; }
        public string DomainKey { get; set; } = "";
        public Action Action { get; set; } = null!;
    }

    private sealed class PendingPayloadProcessingJob
    {
        public int RoleEpoch { get; set; }
        public Action Action { get; set; } = null!;
    }

    private sealed class PendingMainThreadPayloadCommit
    {
        public int RoleEpoch { get; set; }
        public Action Action { get; set; } = null!;
    }

    private static void QueueCriticalPayloadProcessingJobLocked(Action job)
    {
        QueueCriticalPayloadProcessingJobLocked(job, _networkRoleEpoch);
    }

    private static void QueueCriticalPayloadProcessingJobLocked(Action job, int roleEpoch)
    {
        PendingCriticalPayloadProcessingJobs.Enqueue(new PendingPayloadProcessingJob
        {
            RoleEpoch = roleEpoch,
            Action = job
        });

        EnsurePayloadProcessingWorkersLocked();
    }

    private static void QueueDeltaArtifactPrewarmJobLocked(Action job)
    {
        QueueDeltaArtifactPrewarmJobLocked(job, _networkRoleEpoch);
    }

    private static void QueueDeltaArtifactPrewarmJobLocked(Action job, int roleEpoch)
    {
        PendingDeltaArtifactPrewarmJobs.Enqueue(new PendingPayloadProcessingJob
        {
            RoleEpoch = roleEpoch,
            Action = job
        });

        EnsurePayloadProcessingWorkersLocked();
    }

    private static void QueueCachePersistenceJobLocked(Action job)
    {
        QueueCachePersistenceJobLocked(job, _networkRoleEpoch);
    }

    private static void QueueCachePersistenceJobLocked(Action job, int roleEpoch)
    {
        PendingCachePersistenceJobs.Enqueue(new PendingPayloadProcessingJob
        {
            RoleEpoch = roleEpoch,
            Action = job
        });

        EnsurePayloadProcessingWorkersLocked();
    }

    private static void EnsurePayloadProcessingWorkersLocked()
    {
        int desiredWorkers = Math.Min(
            MaxPayloadProcessingWorkers,
            PendingCriticalPayloadProcessingJobs.Count +
            Math.Min(
                MaxArtifactPrewarmWorkers,
                PendingDeltaArtifactPrewarmJobs.Count + PendingCachePersistenceJobs.Count));
        while (_payloadProcessingWorkersRunning < desiredWorkers)
        {
            _payloadProcessingWorkersRunning++;
            ThreadPool.QueueUserWorkItem(_ => ProcessPayloadProcessingJobsWorker());
        }
    }

    private static void ProcessPayloadProcessingJobsWorker()
    {
        while (true)
        {
            Action? job = null;
            lock (Sync)
            {
                PendingPayloadProcessingJob? pendingJob = null;
                if (!PendingCriticalPayloadProcessingJobs.TryDequeue(out pendingJob) &&
                    !PendingDeltaArtifactPrewarmJobs.TryDequeue(out pendingJob) &&
                    !PendingCachePersistenceJobs.TryDequeue(out pendingJob))
                {
                    _payloadProcessingWorkersRunning = Math.Max(0, _payloadProcessingWorkersRunning - 1);
                    return;
                }

                if (pendingJob != null && pendingJob.RoleEpoch == _networkRoleEpoch)
                {
                    job = pendingJob.Action;
                }
            }

            if (job == null)
            {
                continue;
            }

            try
            {
                job();
            }
            catch (Exception ex)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Unhandled background payload processing failure. {ex.Message}");
            }
        }
    }

    private static bool IsCurrentRoleEpoch(int roleEpoch)
    {
        lock (Sync)
        {
            return roleEpoch == _networkRoleEpoch;
        }
    }

    private static void QueueMainThreadPayloadCommitLocked(Action action)
    {
        QueueMainThreadPayloadCommitLocked(action, _networkRoleEpoch);
    }

    private static void QueueMainThreadPayloadCommitLocked(Action action, int roleEpoch)
    {
        lock (Sync)
        {
            PendingMainThreadPayloadCommits.Enqueue(new PendingMainThreadPayloadCommit
            {
                RoleEpoch = roleEpoch,
                Action = action
            });
        }
    }

    private static void QueueReloadActionLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        string pendingReloadKey = BuildPendingReloadDomainKey(transport.DomainKey, _networkRoleEpoch);
        if (!PendingReloadDomainKeys.Add(pendingReloadKey))
        {
            return;
        }

        PendingReloadActions.Enqueue(new PendingReloadAction
        {
            RoleEpoch = _networkRoleEpoch,
            DomainKey = transport.DomainKey,
            Action = transport.ReloadAction
        });
    }

    private static bool TryDequeueCurrentMainThreadPayloadCommitLocked(out Action? action)
    {
        action = null;
        while (PendingMainThreadPayloadCommits.TryDequeue(out PendingMainThreadPayloadCommit? pendingCommit))
        {
            if (pendingCommit != null && pendingCommit.RoleEpoch == _networkRoleEpoch)
            {
                action = pendingCommit.Action;
                return true;
            }
        }

        return false;
    }

    private static bool TryDequeueCurrentReloadActionLocked(out PendingReloadAction? pendingReload)
    {
        pendingReload = null;
        while (PendingReloadActions.TryDequeue(out PendingReloadAction? queuedReload))
        {
            if (queuedReload == null)
            {
                continue;
            }

            PendingReloadDomainKeys.Remove(BuildPendingReloadDomainKey(queuedReload.DomainKey, queuedReload.RoleEpoch));
            if (queuedReload.RoleEpoch == _networkRoleEpoch)
            {
                pendingReload = queuedReload;
                return true;
            }
        }

        return false;
    }

    private static string BuildPendingReloadDomainKey(string domainKey, int roleEpoch)
    {
        return roleEpoch.ToString(CultureInfo.InvariantCulture) + ":" + (domainKey ?? "");
    }
}
