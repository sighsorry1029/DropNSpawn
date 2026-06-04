using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private sealed class SpawnSystemConfigurationRuntimeState
    {
        public List<CanonicalSpawnSystemEntry> Configuration { get; set; } = new();
        public string ConfigurationSignature { get; set; } = "";
        public bool ConfigurationReady { get; set; }

        public void Reset()
        {
            Configuration = new List<CanonicalSpawnSystemEntry>();
            ConfigurationSignature = "";
            ConfigurationReady = false;
        }
    }

    private sealed class DeferredExpandWorldDataBiomeState
    {
        public bool WaitingForBiomeReady { get; private set; }
        public bool QueueEspRefreshForLiveSystems { get; private set; }
        public bool QueueLiveSystemAttach { get; private set; }
        public bool PublishSyncedConfiguration { get; private set; }
        public bool LoggedWait { get; set; }
        public bool HasWork => WaitingForBiomeReady || PublishSyncedConfiguration;

        public void Defer(
            bool queueEspRefreshForLiveSystems,
            bool queueLiveSystemAttach,
            bool publishSyncedConfiguration)
        {
            WaitingForBiomeReady = true;
            QueueEspRefreshForLiveSystems |= queueEspRefreshForLiveSystems;
            QueueLiveSystemAttach |= queueLiveSystemAttach;
            PublishSyncedConfiguration |= publishSyncedConfiguration;
        }

        public void Consume(
            out bool queueEspRefreshForLiveSystems,
            out bool queueLiveSystemAttach,
            out bool publishSyncedConfiguration)
        {
            queueEspRefreshForLiveSystems = QueueEspRefreshForLiveSystems;
            queueLiveSystemAttach = QueueLiveSystemAttach;
            publishSyncedConfiguration = PublishSyncedConfiguration;
            Clear();
        }

        public void ClearPublishSyncedConfiguration()
        {
            PublishSyncedConfiguration = false;
        }

        public void Clear()
        {
            WaitingForBiomeReady = false;
            QueueEspRefreshForLiveSystems = false;
            QueueLiveSystemAttach = false;
            PublishSyncedConfiguration = false;
            LoggedWait = false;
        }
    }

    private sealed class SpawnSystemBuildPipelineState
    {
        public int? PendingGameDataSignature { get; set; }
        public int PreparedEntriesBuildVersion { get; set; }
        public bool PreparedEntriesBuildInFlight { get; set; }
        public bool PreparedEntriesBuildWorkerRunning { get; set; }
        public PreparedEntriesBuildResult? CompletedPreparedEntriesBuildResult { get; set; }
        public PendingPreparedEntriesBuildRequest? PendingPreparedEntriesBuildRequest { get; set; }
        public PendingCompiledTableBuildState? PendingCompiledTableBuild { get; set; }
        public string PendingBuildTargetSignature { get; set; } = "";

        public void ResetPreparedEntriesBuildPipeline(bool clearPendingTargetSignature)
        {
            PreparedEntriesBuildVersion++;
            PreparedEntriesBuildInFlight = false;
            CompletedPreparedEntriesBuildResult = null;
            PendingPreparedEntriesBuildRequest = null;
            PendingCompiledTableBuild = null;
            PendingGameDataSignature = null;
            if (clearPendingTargetSignature)
            {
                PendingBuildTargetSignature = "";
            }
        }
    }
}
