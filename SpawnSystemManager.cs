using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SpawnSystemConfigurationEntry = DropNSpawn.CanonicalSpawnSystemEntry;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private const int FinalizedPreparedEntriesPerStep = 1;
    private const int CompiledEntryBuildsPerStep = 8;
    internal static readonly DomainModuleDefinition<CanonicalSpawnSystemEntry> Module =
        new(
            "spawnsystem",
            DropNSpawnPlugin.ReloadDomain.SpawnSystem,
            "spawnsystem_yaml",
            96,
            ShouldReloadForPath,
            ReloadConfiguration,
            Initialize,
            OnGameDataReady,
            HandleExpandWorldDataReady,
            dtoVersion: 2,
            transportProfile: DomainTransportProfile.LargeWithArtifacts,
            displayName: "spawnsystem",
            cacheDirectoryName: "spawnsystem",
            clientRequestPriority: 40,
            keySelector: entry => entry.RuleId,
            applyPayloadAction: ApplySyncedPayload,
            workKinds: DomainWorkKinds.Runtime | DomainWorkKinds.Reconcile,
            hasPendingReconcileWork: HasPendingReconcileWork,
            processPendingReconcileStep: ProcessQueuedReconcileStep,
            beforeClientManifestChanged: MarkSyncedPayloadPending,
            onClientAuthorityCutover: EnterPendingSyncedPayloadState,
            hooks: SpawnSystemTransportHooks.Instance);
    internal static DomainDescriptor<CanonicalSpawnSystemEntry> Descriptor => Module.DescriptorTyped;
    internal static DomainTransportMetadata<CanonicalSpawnSystemEntry> TransportMetadata => Module.TransportMetadataTyped;
    private static int _invalidEntryWarningSuppressionDepth;

    private readonly struct PendingLiveSystemAttach
    {
        public PendingLiveSystemAttach(SpawnSystem system, int systemId, int epoch, int buildVersion, CompiledSpawnSystemTable targetTable)
        {
            System = system;
            SystemId = systemId;
            Epoch = epoch;
            BuildVersion = buildVersion;
            TargetTable = targetTable;
        }

        public SpawnSystem System { get; }
        public int SystemId { get; }
        public int Epoch { get; }
        public int BuildVersion { get; }
        public CompiledSpawnSystemTable TargetTable { get; }
    }

    private sealed class SpawnSystemEntrySnapshot
    {
        public string RefId { get; set; } = "";
        public int ListIndex { get; set; }
        public int EntryIndex { get; set; }
        public SpawnSystem.SpawnData Data { get; set; } = null!;
    }

    private sealed class SpawnSystemSnapshot
    {
        public int SystemId { get; set; }
        public int ListCount { get; set; }
        public List<SpawnSystemEntrySnapshot> Entries { get; } = new();
    }

    private sealed class PreparedSpawnSystemEntry
    {
        public CanonicalSpawnSystemEntry Entry { get; set; } = null!;
        public SpawnSystem.SpawnData Data { get; set; } = null!;
        public string Context { get; set; } = "";
        public SpawnSystemCustomDataSupport.PreparedPayload? CustomDataPayload { get; set; }
        public TimeOfDayDefinition? RuntimeTimeOfDay { get; set; }
    }

    private sealed class PreparedSpawnSystemModel
    {
        public CanonicalSpawnSystemEntry Entry { get; set; } = null!;
        public string RuleId { get; set; } = "";
        public string EntrySignature { get; set; } = "";
        public string Context { get; set; } = "";
        public TimeOfDayDefinition? RuntimeTimeOfDay { get; set; }
    }

    private readonly struct InvalidEntryWarningSuppressionScope : IDisposable
    {
        private readonly bool _active;

        public InvalidEntryWarningSuppressionScope(bool active)
        {
            _active = active;
            if (_active)
            {
                _invalidEntryWarningSuppressionDepth++;
            }
        }

        public void Dispose()
        {
            if (_active)
            {
                _invalidEntryWarningSuppressionDepth--;
            }
        }
    }

    private sealed class FinalizedPreparedEntryCacheEntry
    {
        public int GameDataSignature { get; set; }
        public string RuleId { get; set; } = "";
        public string EntrySignature { get; set; } = "";
        public SpawnSystem.SpawnData Data { get; set; } = null!;
        public SpawnSystemCustomDataSupport.PreparedPayload? CustomDataPayload { get; set; }
        public TimeOfDayDefinition? RuntimeTimeOfDay { get; set; }
    }

    private sealed class PreparedEntriesBuildResult
    {
        public int BuildVersion { get; set; }
        public int GameDataSignature { get; set; }
        public bool DomainEnabled { get; set; }
        public string ApplyTargetSignature { get; set; } = "";
        public bool QueueEspRefreshForLiveSystems { get; set; }
        public List<PreparedSpawnSystemModel> Models { get; } = new();
        public string PreparedEntriesSignature { get; set; } = "";
    }

    private sealed class PendingPreparedEntriesBuildRequest
    {
        public int BuildVersion { get; set; }
        public int GameDataSignature { get; set; }
        public bool DomainEnabled { get; set; }
        public string ApplyTargetSignature { get; set; } = "";
        public bool QueueEspRefreshForLiveSystems { get; set; }
        public List<CanonicalSpawnSystemEntry> ConfigurationSnapshot { get; } = new();
    }

    private sealed class PendingCompiledTableBuildState
    {
        public int BuildVersion { get; set; }
        public int GameDataSignature { get; set; }
        public bool DomainEnabled { get; set; }
        public bool EagerClientSyncBuild { get; set; }
        public string ApplyTargetSignature { get; set; } = "";
        public bool QueueEspRefreshForLiveSystems { get; set; }
        public string PreparedEntriesSignature { get; set; } = "";
        public List<PreparedSpawnSystemModel> Models { get; } = new();
        public int NextFinalizeIndex { get; set; }
        public List<PreparedSpawnSystemEntry> FinalizedEntries { get; } = new();
        public int NextCompiledEntryIndex { get; set; }
        public CompiledSpawnSystemTable? BuildingActiveTable { get; set; }
        public List<SpawnSystem.SpawnData>? BuildingLiveEntries { get; set; }
        public CompiledSpawnSystemTable? PreviousActiveTable { get; set; }
        public CompiledSpawnSystemTable? PreviousVanillaTable { get; set; }
        public bool RuntimeStateSwapped { get; set; }
    }

    private sealed class ParsedSpawnSystemConfigurationDocument
    {
        public List<SpawnSystemConfigurationEntry> Configuration { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    private sealed class CompiledSpawnSystemTable
    {
        public int GameDataSignature { get; set; }
        public string Signature { get; set; } = "";
        public int BaselineListCount { get; set; }
        public int BaselineRowCount { get; set; }
        public int BaselineContentHash { get; set; }
        public List<SpawnSystemList> Lists { get; } = new();
        public Dictionary<SpawnSystem.SpawnData, TimeOfDayDefinition> RuntimeTimeOfDayBySpawnData { get; } = new();
        public Dictionary<SpawnSystem.SpawnData, SpawnSystemCustomDataSupport.PreparedPayload?> CustomPayloadsBySpawnData { get; } = new();
    }

    private struct SpawnListSummary
    {
        public int ListCount { get; set; }
        public int RowCount { get; set; }
        public int ContentHash { get; set; }
        public string SamplePrefabs { get; set; }
    }

    private sealed class PendingCompiledTableRetirement
    {
        public CompiledSpawnSystemTable Table { get; set; } = null!;
        public HashSet<int> RemainingSystemIds { get; } = new();
    }

    private sealed class SyncedSpawnSystemConfigurationState
    {
        public List<CanonicalSpawnSystemEntry> Configuration { get; } = new();
        public string ConfigurationSignature { get; set; } = "";
        public bool ConfigurationReady { get; set; }
    }

    private static readonly object Sync = new();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    private static readonly Dictionary<int, SpawnSystemSnapshot> SnapshotsBySystemId = new();
    private static readonly HashSet<string> InvalidEntryWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<SpawnSystem.SpawnData, TimeOfDayDefinition> TimeOfDayBySpawnData = new();
    private static readonly RingBufferQueue<PendingLiveSystemAttach> PendingLiveSystemAttaches = new();
    private static readonly HashSet<int> PendingLiveSystemAttachIds = new();
    private static readonly HashSet<int> PendingLiveSystemAttachEspRefreshIds = new();
    private static readonly List<PendingCompiledTableRetirement> PendingCompiledTableRetirements = new();
    private static readonly HashSet<int> PreAttachedSpawnSystemIds = new();
    private static readonly Dictionary<int, SpawnSystem> LiveSystemsById = new();
    private static readonly List<SpawnSystem> LiveSystemsSnapshot = new();
    private static readonly Dictionary<string, FinalizedPreparedEntryCacheEntry> FinalizedPreparedEntryCache = new(StringComparer.Ordinal);
    private static readonly FieldInfo? SpawnSystemInstancesField = AccessTools.Field(typeof(SpawnSystem), "m_instances");
    private static readonly string[] BiomeOutputOrder =
    {
        nameof(Heightmap.Biome.Meadows),
        nameof(Heightmap.Biome.BlackForest),
        nameof(Heightmap.Biome.Swamp),
        nameof(Heightmap.Biome.Ocean),
        nameof(Heightmap.Biome.Mountain),
        nameof(Heightmap.Biome.Plains),
        nameof(Heightmap.Biome.Mistlands),
        nameof(Heightmap.Biome.AshLands),
        nameof(Heightmap.Biome.DeepNorth)
    };
    private static readonly Dictionary<string, (string CanonicalName, int Rank)> BiomeOutputOrderLookup = BuildBiomeOutputOrderLookup();

    private static List<CanonicalSpawnSystemEntry> _configuration = new();
    private static string _configurationSignature = "";
    private static string _lastFailedConfigurationPayload = "";
    private static DomainLoadState LoadState => ConfigurationRuntime.LoadState;
    private static bool _configurationReady;
    private static bool _initialized;
    private static int? _lastCompletedGameDataSignature;
    private static int? _pendingGameDataSignature;
    private static string _lastAppliedConfigurationSignature = "";
    private static string _lastAppliedPreparedEntriesSignature = "";
    private static int? _lastAppliedGameDataSignature;
    private static int? _lastCommittedAuthorityEpoch;
    private static bool? _lastAppliedDomainEnabled;
    private static int _reconcileQueueEpoch;
    private static int _requiredGlobalKeyEvaluationDepth;
    private static int? _lastRuntimeTimeOfDayPhaseMarker;
    private static int _lastRuntimeTimeOfDayRefreshFrame = -1;
    private static SpawnSystemSnapshot? _templateSnapshot;
    private static List<PreparedSpawnSystemEntry>? _preparedEntriesCache;
    private static bool _hasRuntimeTimeOfDayOverrides;
    private static int _preparedEntriesBuildVersion;
    private static bool _preparedEntriesBuildInFlight;
    private static bool _preparedEntriesBuildWorkerRunning;
    private static PreparedEntriesBuildResult? _completedPreparedEntriesBuildResult;
    private static PendingPreparedEntriesBuildRequest? _pendingPreparedEntriesBuildRequest;
    private static PendingCompiledTableBuildState? _pendingCompiledTableBuild;
    private static CompiledSpawnSystemTable? _activeCompiledTable;
    private static CompiledSpawnSystemTable? _vanillaCompiledTable;
    private static GameObject? _managedSpawnListHost;
    private static GameObject? _attachedSpawnListHost;
    private static bool _liveSystemsSnapshotDirty = true;
    private static bool _liveSystemsBootstrapAttempted;
    private static int? _liveSystemsRegistrySceneInstanceId;
    private static string _lastAppliedBuildTargetSignature = "";
    private static string _pendingBuildTargetSignature = "";
    private static bool _waitingForExpandWorldDataBiomeReady;
    private static bool _deferredQueueEspRefreshForLiveSystems;
    private static bool _deferredQueueLiveSystemAttach;
    private static bool _deferredPublishSyncedConfiguration;
    private static bool _loggedExpandWorldDataBiomeReadyWait;
    private static HashSet<string>? _capturedStrictValidationWarnings;
    private static string _lastLoggedSyncedConfigPayloadToken = "";
    private static bool _loggedPayloadWaiting;
    private static string _lastLoggedVanillaRetainedSignature = "";
    private static string _lastLoggedRuntimeAttachSignature = "";
    private static bool _forceApplyAfterSyncedCommit;
    private static string _lastLoggedApplySkipKey = "";
    private static string _lastLoggedPreparedBuildQueuedSignature = "";
    private static int _lastLoggedPreparedBuildCompletedVersion = -1;
    private static int _lastLoggedCompiledBuildStartedVersion = -1;
    private static int _lastLoggedCompiledBuildFinishedVersion = -1;
    private static string _lastLoggedAwakeRetriggerKey = "";
    private static int _cachedGameDataSignatureFrame = -1;
    private static int _cachedGameDataSignatureValue;
    private static readonly List<string> _cachedConfiguredSpawnSystemResolutionKeys = new();
    private static string _cachedConfiguredSpawnSystemResolutionKeysSignature = "";
    private static int _cachedConfiguredSpawnSystemResolutionKeysSceneStamp = int.MinValue;

    private sealed class ReferenceCatalogSnapshot
    {
        public List<SpawnSystemConfigurationEntry> LiveEntries { get; } = new();
        public string SourceSignature { get; set; } = "";
        public bool HasAnyEntries => LiveEntries.Count > 0;
    }

    private static string ReferenceConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("spawnsystem")}.reference.yml");
    private static string PrimaryOverrideConfigurationPathYml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("spawnsystem")}.yml");
    private static string PrimaryOverrideConfigurationPathYaml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("spawnsystem")}.yaml");
    private static string FullScaffoldConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("spawnsystem")}.full.yml");
    private static readonly DomainConfigurationRuntime<SpawnSystemConfigurationEntry, SyncedSpawnSystemConfigurationState> ConfigurationRuntime =
        new(
            new DomainLoadHooks<SpawnSystemConfigurationEntry, SyncedSpawnSystemConfigurationState>(
                ParseLocalConfigurationDocuments,
                BuildSyncedConfigurationState,
                CommitConfigurationState,
                RejectLocalConfigurationPayload,
                state => state.Configuration.Count,
                LogPartiallyAcceptedLocalConfiguration,
                LogLocalConfigurationLoaded,
                OnSourceOfTruthPayloadUnchanged,
                PublishSyncedConfigurationOrDeferLocked,
                CanStrictlyValidateLocalConfigurationNow,
                StrictValidateLocalConfiguration),
            new DomainSyncHooks<SpawnSystemConfigurationEntry, SyncedSpawnSystemConfigurationState>(
                (out List<SpawnSystemConfigurationEntry> configuration, out string payloadToken) =>
                    ConfigurationDomainHost.TryGetSyncedEntries(
                        Descriptor,
                        out configuration,
                        out payloadToken,
                        ClearPayloadWaitingLogState),
                payloadToken => ConfigurationDomainHost.ShouldSkipSyncedPayload(
                    LoadState,
                    payloadToken,
                    _configurationReady),
                BuildSyncedConfigurationState,
                CommitConfigurationState,
                state => state.Configuration.Count,
                "ServerSync:DropNSpawnSpawnSystem",
                () => ConfigurationDomainHost.HandleWaitingForSyncedPayload(
                    MarkSyncedPayloadPending,
                    "Waiting for synchronized spawnsystem override payload from the server.",
                    LogPayloadWaitingIfNeeded),
                LogSyncedSpawnSystemConfigurationLoaded,
                LogSyncedSpawnSystemConfigurationFailure));
    internal static bool ShouldReloadForPath(string? path)
    {
        return PluginSettingsFacade.IsEligibleOverrideConfigurationPath(path) &&
               IsOverrideConfigurationFileName(Path.GetFileName(path ?? ""));
    }

    private static bool ShouldApplyLocally()
    {
        return PluginSettingsFacade.IsSpawnSystemDomainEnabled();
    }

    internal static void MarkSyncedPayloadPending()
    {
        lock (Sync)
        {
            ConfigurationRuntime.MarkSyncedPayloadPending(
                DropNSpawnPlugin.IsSourceOfTruth,
                () =>
                {
                    ClearQueuedReconcileState();
                    ResetPreparedEntriesBuildPipelineLocked(clearPendingTargetSignature: true);
                    ClearPayloadWaitingLogState();
                    _configurationReady = false;
                    _forceApplyAfterSyncedCommit = false;
                });
        }
    }

    internal static void EnterPendingSyncedPayloadState()
    {
        lock (Sync)
        {
            ConfigurationRuntime.EnterPendingSyncedPayloadState(
                DropNSpawnPlugin.IsSourceOfTruth,
                beforeResetLoadState: ResetLoadedConfigurationState,
                afterResetLoadState: () =>
                {
                    _configurationSignature = "";
                    _lastFailedConfigurationPayload = "";
                    _lastCommittedAuthorityEpoch = null;
                    _lastAppliedConfigurationSignature = "";
                    _lastAppliedPreparedEntriesSignature = "";
                    _lastAppliedBuildTargetSignature = "";
                    _lastAppliedGameDataSignature = null;
                    _lastAppliedDomainEnabled = null;
                    _forceApplyAfterSyncedCommit = false;
                    RestoreBaselineWhileWaitingForSyncedPayload();
                });
        }
    }

    private static bool CanRetainCurrentCompiledTableWhilePending(int gameDataSignature)
    {
        return ShouldApplyLocally() &&
               !DropNSpawnPlugin.IsSourceOfTruth &&
               !_configurationReady &&
               gameDataSignature != 0 &&
               _activeCompiledTable != null &&
               _activeCompiledTable.Lists.Count > 0 &&
               _lastAppliedGameDataSignature == gameDataSignature &&
               _lastCommittedAuthorityEpoch == NetworkPayloadSyncSupport.CurrentAuthorityEpoch;
    }

    private static void RestoreBaselineWhileWaitingForSyncedPayload()
    {
        int gameDataSignature = ComputeGameDataSignature();
        if (gameDataSignature == 0)
        {
            _activeCompiledTable = null;
            return;
        }

        CompiledSpawnSystemTable? previousSelectedTable = GetSelectedCompiledTableForCurrentState();
        List<SpawnSystem> liveSystems = GetLiveSystems();
        EnsureVanillaCompiledTableCurrentLocked(gameDataSignature);
        _activeCompiledTable = null;
        CompiledSpawnSystemTable? baselineTable = GetSelectedCompiledTableForCurrentState();
        QueueLiveSystemAttachForTable(
            baselineTable,
            _preparedEntriesBuildVersion,
            true,
            liveSystems);
        RetireCompiledTableAfterMigrationLocked(previousSelectedTable, baselineTable, liveSystems);
        LogVanillaRetainedIfNeeded(
            $"cutover_pending|{gameDataSignature.ToString(CultureInfo.InvariantCulture)}",
            "authority_cutover_pending",
            baselineTable,
            liveSystems.Count);
    }

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            LoadConfiguration();
            _initialized = true;
        }
    }

    internal static void ReloadConfiguration()
    {
        lock (Sync)
        {
            InvalidatePreparedEntriesCache();
            LoadConfiguration();
            ApplyIfReady(queueEspRefreshForLiveSystems: true, queueLiveSystemAttach: true);
        }
    }

    internal static bool HandleExpandWorldDataReady()
    {
        lock (Sync)
        {
            if (!DropNSpawnPlugin.IsSourceOfTruth)
            {
                return false;
            }

            string previousLoadedPayload = LoadState.LastLoadedPayload;
            string previousSignature = _configurationSignature;
            bool hadPendingValidation = LoadState.PendingStrictPayload.Length > 0;
            bool hadDeferredWork = _waitingForExpandWorldDataBiomeReady || _deferredPublishSyncedConfiguration;

            LoadConfiguration();
            ApplyIfReady(queueEspRefreshForLiveSystems: true, queueLiveSystemAttach: true);

            return hadPendingValidation ||
                   hadDeferredWork ||
                   !string.Equals(previousLoadedPayload, LoadState.LastLoadedPayload, StringComparison.Ordinal) ||
                   !string.Equals(previousSignature, _configurationSignature, StringComparison.Ordinal);
        }
    }

    internal static void ApplySyncedPayload()
    {
        lock (Sync)
        {
            ConfigurationRuntime.ApplySyncedPayload(() =>
            {
                _forceApplyAfterSyncedCommit = true;
                ApplyIfReady(queueEspRefreshForLiveSystems: true, queueLiveSystemAttach: true);
            });
        }
    }

    internal static void OnGameDataReady(string source)
    {
        lock (Sync)
        {
            if (!_initialized)
            {
                Initialize();
            }

            EnsureLiveSystemRegistrySessionLocked();
            int gameDataSignature = ComputeGameDataSignature();
            if (_lastCompletedGameDataSignature == gameDataSignature ||
                _pendingGameDataSignature == gameDataSignature)
            {
                return;
            }

            _pendingGameDataSignature = gameDataSignature;
            ClearQueuedReconcileState();
            RefreshSnapshots();
            InvalidatePreparedEntriesCache();

            if (DropNSpawnPlugin.IsSourceOfTruth)
            {
                HandleSourceOfTruthGameDataReady();
            }
            ApplyIfReady(
                queueEspRefreshForLiveSystems: false,
                queueLiveSystemAttach: true);
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"SpawnSystem processing scheduled after {source}.");
        }
    }

    internal static void OnSpawnSystemAwake(SpawnSystem? system)
    {
        lock (Sync)
        {
            TrackLiveSystemLocked(system);
            if (system == null ||
                ZNetScene.instance == null ||
                ObjectDB.instance == null ||
                DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.SpawnSystem))
            {
                return;
            }

            bool preAttached = PreAttachedSpawnSystemIds.Remove(system.GetInstanceID());
            CompiledSpawnSystemTable? selectedTable = GetSelectedCompiledTableForCurrentState();
            bool preAttachedMutated = preAttached && !IsSystemAttachedToCompiledTable(system, selectedTable);
            bool queueEspRefreshForAwake = !preAttached || preAttachedMutated;
            LogLiveSpawnListState("awake_postfix_entry", system, selectedTable, preAttached);

            if (DropNSpawnPlugin.IsSourceOfTruth)
            {
                if (HandleSourceOfTruthSpawnSystemAwake())
                {
                    ApplyIfReady(queueEspRefreshForLiveSystems: queueEspRefreshForAwake);
                    return;
                }
            }
            else if (!_configurationReady)
            {
                if (!CanRetainCurrentCompiledTableWhilePending(ComputeGameDataSignature()))
                {
                    return;
                }
            }
            else if (_configurationReady && (_activeCompiledTable == null || _activeCompiledTable.Lists.Count == 0))
            {
                LogAwakeRetriggerIfNeeded(system, GetLiveSystems().Count);
                ApplyIfReady(
                    queueEspRefreshForLiveSystems: queueEspRefreshForAwake,
                    queueLiveSystemAttach: true);
                if (_activeCompiledTable == null || _activeCompiledTable.Lists.Count == 0)
                {
                    return;
                }
            }

            AttachCompiledTableToAwakenedSystem(system, queueEspRefresh: queueEspRefreshForAwake);
        }
    }

    internal static void PreAttachCompiledTableToAwakeningSystem(SpawnSystem? system)
    {
        lock (Sync)
        {
            TrackLiveSystemLocked(system);
            if (system == null ||
                !ShouldApplyLocally() ||
                DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.SpawnSystem))
            {
                return;
            }

            if (!DropNSpawnPlugin.IsSourceOfTruth &&
                ((_configurationReady && (_activeCompiledTable == null || _activeCompiledTable.Lists.Count == 0)) ||
                 (!_configurationReady && !CanRetainCurrentCompiledTableWhilePending(ComputeGameDataSignature()))))
            {
                return;
            }

            CompiledSpawnSystemTable? table = GetSelectedCompiledTableForCurrentState();
            if (table == null)
            {
                return;
            }

            AttachTableToSystem(system, table);
            LogLiveSpawnListState("awake_prefix_preattach", system, table, preAttached: true);
            PreAttachedSpawnSystemIds.Add(system.GetInstanceID());
        }
    }

    internal static void UntrackLiveSystem(SpawnSystem? system)
    {
        lock (Sync)
        {
            UntrackLiveSystemLocked(system);
        }
    }

    internal static bool ShouldBlockClientSpawnSystemUpdate(SpawnSystem? system)
    {
        lock (Sync)
        {
            if (!ShouldApplyLocally() || DropNSpawnPlugin.IsSourceOfTruth)
            {
                return false;
            }

            if (!_configurationReady)
            {
                return true;
            }

            if (_activeCompiledTable == null || _activeCompiledTable.Lists.Count == 0)
            {
                return true;
            }

            return !IsSystemAttachedToCompiledTable(system, _activeCompiledTable);
        }
    }

    internal static bool ProcessQueuedReconcileStep(float deadline)
    {
        lock (Sync)
        {
            if (Time.realtimeSinceStartup >= deadline)
            {
                return false;
            }

            if (DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.SpawnSystem))
            {
                return false;
            }

            if (TryActivateCompletedPreparedEntriesBuildLocked())
            {
                return true;
            }

            if (TryProcessDeferredExpandWorldDataBiomeReadyLocked())
            {
                return true;
            }

            if (TryProcessPendingCompiledTableBuild(deadline))
            {
                return true;
            }

            if (TryProcessPendingLiveSystemAttach(deadline))
            {
                return true;
            }

            if (EspSpawnSystemCompatibility.TryProcessPendingRefresh(deadline, _reconcileQueueEpoch))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasPendingReconcileWork()
    {
        lock (Sync)
        {
            return _completedPreparedEntriesBuildResult != null ||
                   _pendingCompiledTableBuild != null ||
                   _waitingForExpandWorldDataBiomeReady ||
                   _deferredPublishSyncedConfiguration ||
                   PendingLiveSystemAttaches.Count > 0 ||
                   EspSpawnSystemCompatibility.HasPendingRefreshes();
        }
    }

    private static void HandleSourceOfTruthGameDataReady()
    {
        EnsurePrimaryOverrideConfigurationFileExists();
        LoadConfiguration();
    }

    private static void EnsureLiveSystemRegistrySessionLocked()
    {
        int currentSceneInstanceId = ZNetScene.instance != null ? ZNetScene.instance.GetInstanceID() : 0;
        if (_liveSystemsRegistrySceneInstanceId == currentSceneInstanceId)
        {
            return;
        }

        FinalizeAllPendingCompiledTableRetirementsLocked();
        _liveSystemsRegistrySceneInstanceId = currentSceneInstanceId;
        LiveSystemsById.Clear();
        LiveSystemsSnapshot.Clear();
        _liveSystemsSnapshotDirty = true;
        _liveSystemsBootstrapAttempted = false;
        SnapshotsBySystemId.Clear();
        _templateSnapshot = null;
        PendingLiveSystemAttaches.Clear();
        PendingLiveSystemAttachIds.Clear();
        PendingLiveSystemAttachEspRefreshIds.Clear();
        EspSpawnSystemCompatibility.ClearPendingRefreshes();
        PreAttachedSpawnSystemIds.Clear();
        ResetPreparedEntriesBuildPipelineLocked(clearPendingTargetSignature: true);
    }

    private static void TrackLiveSystemLocked(SpawnSystem? system)
    {
        EnsureLiveSystemRegistrySessionLocked();
        if (system == null)
        {
            return;
        }

        int systemId = system.GetInstanceID();
        LiveSystemsById[systemId] = system;
        _liveSystemsSnapshotDirty = true;
    }

    private static void UntrackLiveSystemLocked(SpawnSystem? system)
    {
        EnsureLiveSystemRegistrySessionLocked();
        if (system == null)
        {
            return;
        }

        int systemId = system.GetInstanceID();
        if (!LiveSystemsById.Remove(systemId))
        {
            return;
        }

        ClearAttachedRuntimeState(system);
        _liveSystemsSnapshotDirty = true;
        SnapshotsBySystemId.Remove(systemId);
        _templateSnapshot = null;
        PendingLiveSystemAttachIds.Remove(systemId);
        PendingLiveSystemAttachEspRefreshIds.Remove(systemId);
        EspSpawnSystemCompatibility.RemovePendingRefresh(systemId);
        PreAttachedSpawnSystemIds.Remove(systemId);
        MarkSystemMigratedFromRetiredTablesLocked(systemId);
    }

    private static bool HandleSourceOfTruthSpawnSystemAwake()
    {
        bool overrideCreated = EnsurePrimaryOverrideConfigurationFileExists();
        if (overrideCreated)
        {
            LoadConfiguration();
        }

        return overrideCreated || _activeCompiledTable == null;
    }

    private static void AttachCompiledTableToAwakenedSystem(SpawnSystem system, bool queueEspRefresh)
    {
        CompiledSpawnSystemTable? table = GetSelectedCompiledTableForCurrentState();
        if (table == null)
        {
            return;
        }

        AttachTableToSystem(system, table);
        LogLiveSpawnListState("awake_postfix_attached", system, table);
        MarkSystemMigratedFromRetiredTablesLocked(system.GetInstanceID());
        if (queueEspRefresh)
        {
            QueueEspMarkerRefresh(system);
        }

    }

    private static bool TryProcessPendingLiveSystemAttach(float deadline)
    {
        while (PendingLiveSystemAttaches.Count > 0)
        {
            if (Time.realtimeSinceStartup >= deadline)
            {
                return false;
            }

            if (!PendingLiveSystemAttaches.TryDequeue(out PendingLiveSystemAttach queuedAttach))
            {
                continue;
            }

            bool queueEspRefresh = PendingLiveSystemAttachEspRefreshIds.Remove(queuedAttach.SystemId);
            PendingLiveSystemAttachIds.Remove(queuedAttach.SystemId);
            if (queuedAttach.Epoch != _reconcileQueueEpoch || queuedAttach.System == null)
            {
                continue;
            }

            if (queuedAttach.BuildVersion != _preparedEntriesBuildVersion ||
                !ReferenceEquals(queuedAttach.TargetTable, GetSelectedCompiledTableForCurrentState()))
            {
                return true;
            }

            if (queuedAttach.TargetTable == null)
            {
                return true;
            }

            AttachTableToSystem(queuedAttach.System, queuedAttach.TargetTable);
            LogLiveSpawnListState("queued_attach_applied", queuedAttach.System, queuedAttach.TargetTable);
            MarkSystemMigratedFromRetiredTablesLocked(queuedAttach.SystemId);
            if (queueEspRefresh)
            {
                QueueEspMarkerRefresh(queuedAttach.System);
            }

            return true;
        }

        return false;
    }

    internal static void RefreshRuntimeTimeOfDayState()
    {
        if (!_hasRuntimeTimeOfDayOverrides)
        {
            return;
        }

        int currentFrame = Time.frameCount;
        if (_lastRuntimeTimeOfDayRefreshFrame == currentFrame)
        {
            return;
        }

        lock (Sync)
        {
            if (!ShouldApplyLocally())
            {
                _lastRuntimeTimeOfDayPhaseMarker = null;
                _lastRuntimeTimeOfDayRefreshFrame = -1;
                return;
            }

            if (!_hasRuntimeTimeOfDayOverrides)
            {
                return;
            }

            if (_lastRuntimeTimeOfDayRefreshFrame == currentFrame)
            {
                return;
            }

            int currentPhaseMarker = TimeOfDayFormatting.GetCurrentRuntimePhaseMarker();
            if (_lastRuntimeTimeOfDayPhaseMarker.HasValue &&
                _lastRuntimeTimeOfDayPhaseMarker.Value == currentPhaseMarker)
            {
                _lastRuntimeTimeOfDayRefreshFrame = currentFrame;
                return;
            }

            foreach ((SpawnSystem.SpawnData spawnData, TimeOfDayDefinition timeOfDay) in TimeOfDayBySpawnData)
            {
                if (spawnData == null)
                {
                    continue;
                }

                TimeOfDayFormatting.GetRuntimeSpawnFlags(timeOfDay, out bool allowDay, out bool allowNight);
                spawnData.m_spawnAtDay = allowDay;
                spawnData.m_spawnAtNight = allowNight;
            }

            _lastRuntimeTimeOfDayPhaseMarker = currentPhaseMarker;
            _lastRuntimeTimeOfDayRefreshFrame = currentFrame;
        }
    }

    private static void QueueEspMarkerRefresh(SpawnSystem? system)
    {
        EspSpawnSystemCompatibility.RequestRefresh(system, _reconcileQueueEpoch);
    }

    internal static void RecordDirectSpawnedObject(SpawnSystem.SpawnData critter, GameObject? spawnedObject)
    {
        lock (Sync)
        {
            if (critter == null || spawnedObject == null)
            {
                return;
            }
        }
    }

    internal static bool TryWriteFullScaffoldConfigurationFile(out string path, out string error)
    {
        lock (Sync)
        {
            path = FullScaffoldConfigurationPath;
            error = "";

            if (!TryCaptureSnapshotsIfNeeded())
            {
                error = "SpawnSystem game data is not ready yet.";
                return false;
            }

            Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
            File.WriteAllText(path, BuildFullScaffoldConfigurationTemplate());
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Wrote spawnsystem full scaffold configuration to {path}.");
            return true;
        }
    }

    internal static bool TryWriteReferenceConfigurationFile(out string path, out string error)
    {
        lock (Sync)
        {
            path = ReferenceConfigurationPath;
            error = "";

            if (ZNetScene.instance == null || ObjectDB.instance == null)
            {
                error = "SpawnSystem game data is not ready yet.";
                return false;
            }

            ReferenceCatalogSnapshot referenceCatalogSnapshot = BuildCurrentReferenceCatalogSnapshot();
            if (!referenceCatalogSnapshot.HasAnyEntries)
            {
                error = "SpawnSystem game data is not ready yet.";
                return false;
            }

            WriteReferenceConfigurationFile(
                BuildReferenceConfigurationTemplate(referenceCatalogSnapshot),
                $"Updated spawnsystem reference configuration at {ReferenceConfigurationPath}.");
            path = ReferenceConfigurationPath;
            return true;
        }
    }

    internal static void RefreshReferenceConfigurationFile()
    {
        lock (Sync)
        {
            if (ZNetScene.instance == null || ObjectDB.instance == null)
            {
                return;
            }

            ReferenceCatalogSnapshot referenceCatalogSnapshot = BuildCurrentReferenceCatalogSnapshot();
            if (!referenceCatalogSnapshot.HasAnyEntries)
            {
                return;
            }

            WriteReferenceConfigurationFile(
                BuildReferenceConfigurationTemplate(referenceCatalogSnapshot),
                $"Updated spawnsystem reference configuration at {ReferenceConfigurationPath}.");
        }
    }

    private static bool EnsurePrimaryOverrideConfigurationFileExists()
    {
        if (DomainConfigurationFileSupport.HasAnyOverrideConfigurationFile(
                "spawnsystem",
                PrimaryOverrideConfigurationPathYml,
                PrimaryOverrideConfigurationPathYaml))
        {
            return false;
        }

        SpawnSystemSnapshot? snapshot = GetTemplateSnapshot();
        if (snapshot == null || snapshot.Entries.Count == 0)
        {
            return false;
        }

        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        File.WriteAllText(PrimaryOverrideConfigurationPathYml, BuildCompressedPrimaryOverrideConfigurationDocument(snapshot));
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Created spawnsystem override configuration at {PrimaryOverrideConfigurationPathYml}.");
        return true;
    }

    private static void LoadConfiguration()
    {
        if (DropNSpawnPlugin.IsSourceOfTruth)
        {
            ConfigurationRuntime.ReloadSourceOfTruth(
                EnumerateOverrideConfigurationPaths().ToList());
            return;
        }

        ConfigurationRuntime.ReloadSynced();
    }

    private static void ResetLoadedConfigurationState()
    {
        ClearQueuedReconcileState();
        ClearPayloadWaitingLogState();
        ResetPreparedEntriesBuildPipelineLocked(clearPendingTargetSignature: true);
        InvalidEntryWarnings.Clear();
        _configuration = new List<CanonicalSpawnSystemEntry>();
        _configurationReady = false;
        _waitingForExpandWorldDataBiomeReady = false;
        _deferredQueueEspRefreshForLiveSystems = false;
        _deferredQueueLiveSystemAttach = false;
        _deferredPublishSyncedConfiguration = false;
        _loggedExpandWorldDataBiomeReadyWait = false;
        InvalidatePreparedEntriesCache();
    }

    private static List<SpawnSystemConfigurationEntry> CloneAndNormalizeConfigurationEntries(
        List<SpawnSystemConfigurationEntry>? configuration,
        string sourceName)
    {
        List<SpawnSystemConfigurationEntry> normalizedConfiguration =
            NetworkPayloadSyncSupport.CloneEntries(Descriptor, configuration);
        foreach (SpawnSystemConfigurationEntry entry in normalizedConfiguration)
        {
            entry.SourcePath = string.IsNullOrWhiteSpace(entry.SourcePath) ? sourceName : entry.SourcePath;
        }

        if (normalizedConfiguration.Count > 0)
        {
            NormalizeConfiguration(normalizedConfiguration);
        }

        return normalizedConfiguration;
    }

    private static SyncedSpawnSystemConfigurationState BuildSyncedConfigurationState(
        List<SpawnSystemConfigurationEntry> configuration,
        string sourceName)
    {
        using InvalidEntryWarningSuppressionScope _ = BeginInvalidEntryWarningSuppressionForSyncedClientBuild(sourceName);
        SyncedSpawnSystemConfigurationState state = new()
        {
            ConfigurationReady = true
        };
        List<SpawnSystemConfigurationEntry> normalizedConfiguration = CloneAndNormalizeConfigurationEntries(configuration, sourceName);
        foreach (CanonicalSpawnSystemEntry entry in normalizedConfiguration)
        {
            state.Configuration.Add(entry);
        }

        state.ConfigurationSignature = NetworkPayloadSyncSupport.ComputeSpawnSystemConfigurationSignature(state.Configuration);
        return state;
    }

    private static LocalLoadResult<SpawnSystemConfigurationEntry> ParseLocalConfigurationDocuments(
        List<ConfigurationLoadSupport.LocalYamlDocument> documents)
    {
        List<SpawnSystemConfigurationEntry> configuration = new();
        List<string> errors = new();
        List<string> warnings = new();
        int loadedFileCount = 0;
        foreach (ConfigurationLoadSupport.LocalYamlDocument document in documents)
        {
            if (document.ReadError != null)
            {
                errors.Add($"Failed to read {document.Path}. {document.ReadError}");
                continue;
            }

            try
            {
                string yaml = document.Yaml ?? "";
                ParsedSpawnSystemConfigurationDocument parsedDocument = ParseConfiguration(yaml, document.Path);
                warnings.AddRange(parsedDocument.Warnings);
                List<SpawnSystemConfigurationEntry> ownedConfiguration =
                    CloneAndNormalizeConfigurationEntries(parsedDocument.Configuration, document.Path);
                configuration.AddRange(ownedConfiguration);
                loadedFileCount++;
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Failed to parse {document.Path}{FormatYamlExceptionLocation(ex)}. Spawnsystem authoritative YAML must start with a root list like '- prefab: Fox'. {ex}");
            }
        }

        return new LocalLoadResult<SpawnSystemConfigurationEntry>
        {
            Entries = configuration,
            Errors = errors,
            Warnings = warnings,
            ParsedEntryCount = configuration.Count,
            LoadedFileCount = loadedFileCount
        };
    }

    private static bool CanStrictlyValidateLocalConfigurationNow(IEnumerable<SpawnSystemConfigurationEntry> configuration)
    {
        return ZNetScene.instance != null &&
               ObjectDB.instance != null &&
               !ShouldDeferForExpandWorldDataBiomeReady(configuration);
    }

    private static List<SpawnSystemConfigurationEntry> FilterStrictlyValidatedLocalConfiguration(
        IEnumerable<SpawnSystemConfigurationEntry> configuration,
        out List<string> warnings)
    {
        warnings = new List<string>();
        List<SpawnSystemConfigurationEntry> acceptedEntries = new();
        HashSet<string> capturedWarnings = new(StringComparer.OrdinalIgnoreCase);
        _capturedStrictValidationWarnings = capturedWarnings;
        try
        {
            List<SpawnSystemConfigurationEntry> configurationList = configuration?.ToList() ?? new List<SpawnSystemConfigurationEntry>();
            for (int index = 0; index < configurationList.Count; index++)
            {
                SpawnSystemConfigurationEntry entry = configurationList[index];
                string context = CreateConfigurationContext(index, entry);
                int warningCountBefore = capturedWarnings.Count;
                try
                {
                    SpawnSystem.SpawnData data = new();
                    bool compiled = ApplyEntry(data, entry, context, applyCustomData: false);
                    if (!compiled)
                    {
                        if (capturedWarnings.Count == warningCountBefore)
                        {
                            warnings.Add($"Entry '{context}' failed strict spawnsystem validation and was skipped.");
                        }

                        continue;
                    }

                    _ = SpawnSystemCustomDataSupport.BuildPreparedPayload(data, entry, context);
                    acceptedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Entry '{context}' failed strict spawnsystem validation and was skipped. {ex.Message}");
                }
            }
        }
        finally
        {
            _capturedStrictValidationWarnings = null;
        }

        foreach (string warning in capturedWarnings)
        {
            warnings.Add(warning);
        }

        return acceptedEntries;
    }

    private static StrictValidationResult<SpawnSystemConfigurationEntry> StrictValidateLocalConfiguration(
        List<SpawnSystemConfigurationEntry> configuration)
    {
        RefreshResolvedBiomeMasksForConfiguration(configuration);
        List<SpawnSystemConfigurationEntry> validatedConfiguration =
            FilterStrictlyValidatedLocalConfiguration(configuration, out List<string> validationWarnings);
        return new StrictValidationResult<SpawnSystemConfigurationEntry>
        {
            Entries = validatedConfiguration,
            Warnings = validationWarnings
        };
    }

    private static void LogPartiallyAcceptedLocalConfiguration(int totalEntries, int acceptedEntries, IEnumerable<string> warnings)
    {
        int skippedEntries = Math.Max(0, totalEntries - acceptedEntries);
        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Skipped {skippedEntries.ToString(CultureInfo.InvariantCulture)} invalid spawnsystem entr{(skippedEntries == 1 ? "y" : "ies")} and kept {acceptedEntries.ToString(CultureInfo.InvariantCulture)} valid entr{(acceptedEntries == 1 ? "y" : "ies")}.");
        foreach (string warning in warnings
                     .Where(message => !string.IsNullOrWhiteSpace(message))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(warning);
        }
    }

    private static string BuildStrictLocalValidationEnvironmentKey()
    {
        int gameDataSignature = ComputeGameDataSignature();
        string ewdState = BiomeResolutionSupport.IsExpandWorldDataReadyOrUnavailable() ? "ready" : "waiting";
        return string.Concat(gameDataSignature.ToString(CultureInfo.InvariantCulture), "|", ewdState);
    }

    private static void RejectLocalConfigurationPayload(string payload, IEnumerable<string> errors)
    {
        string validationKey = BuildStrictLocalValidationEnvironmentKey();
        if (string.Equals(LoadState.LastRejectedPayload, payload, StringComparison.Ordinal) &&
            string.Equals(LoadState.LastRejectedValidationKey, validationKey, StringComparison.Ordinal))
        {
            return;
        }

        LoadState.LastRejectedPayload = payload;
        LoadState.LastRejectedValidationKey = validationKey;
        LoadState.PendingStrictPayload = "";
        DropNSpawnPlugin.DropNSpawnLogger.LogError(
            "Rejected spawnsystem reload. Keeping the previous authoritative spawnsystem configuration.");
        foreach (string error in errors
                     .Where(message => !string.IsNullOrWhiteSpace(message))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogError(error);
        }
    }

    private static void CommitConfigurationState(SyncedSpawnSystemConfigurationState state, string payloadToken)
    {
        ResetLoadedConfigurationState();
        _configuration = state.Configuration;
        _configurationReady = state.ConfigurationReady;
        _configurationSignature = state.ConfigurationSignature;
        _lastCommittedAuthorityEpoch = DropNSpawnPlugin.IsSourceOfTruth
            ? null
            : NetworkPayloadSyncSupport.CurrentAuthorityEpoch;
        LoadState.LastLoadedPayload = payloadToken;
        _lastFailedConfigurationPayload = "";
        LoadState.LastRejectedPayload = "";
        LoadState.LastRejectedValidationKey = "";
        LoadState.PendingStrictPayload = "";
    }

    private static void LogLocalConfigurationLoaded(int acceptedEntryCount, int loadedFileCount)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Loaded {acceptedEntryCount} spawnsystem configuration(s) from {loadedFileCount} override file(s).");
    }

    private static void OnSourceOfTruthPayloadUnchanged()
    {
        RefreshResolvedBiomeMasksForSourceOfTruthConfigurationLocked();
        _configurationSignature = NetworkPayloadSyncSupport.ComputeSpawnSystemConfigurationSignature(_configuration);
        if (!NetworkPayloadSyncSupport.IsPayloadCurrent(Descriptor, _configurationSignature) ||
            _waitingForExpandWorldDataBiomeReady ||
            _deferredPublishSyncedConfiguration)
        {
            PublishSyncedConfigurationOrDeferLocked();
        }
    }

    private static void LogSyncedSpawnSystemConfigurationLoaded(string payloadToken, int acceptedEntryCount)
    {
        LogSyncedConfigCommittedIfNeeded(payloadToken, acceptedEntryCount);
    }

    private static void LogSyncedSpawnSystemConfigurationFailure(string payloadToken, Exception ex)
    {
        LogSyncedConfigurationFailureOnce(payloadToken, ex);
    }

    private static void LogPayloadWaitingIfNeeded()
    {
        if (_loggedPayloadWaiting)
        {
            return;
        }

        _loggedPayloadWaiting = true;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=payload_waiting lastCommittedHash={(LoadState.LastLoadedPayload.Length > 0 ? LoadState.LastLoadedPayload : "<none>")}");
    }

    private static void ClearPayloadWaitingLogState()
    {
        _loggedPayloadWaiting = false;
    }

    private static void LogSyncedConfigurationFailureOnce(string payloadToken, Exception ex)
    {
        if (!string.Equals(_lastFailedConfigurationPayload, payloadToken, StringComparison.Ordinal))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to deserialize synchronized spawnsystem payload DTO. {ex}");
            _lastFailedConfigurationPayload = payloadToken;
        }
    }

    private static void ClearQueuedReconcileState()
    {
        _reconcileQueueEpoch++;
        PendingLiveSystemAttaches.Clear();
        PendingLiveSystemAttachIds.Clear();
        PendingLiveSystemAttachEspRefreshIds.Clear();
        EspSpawnSystemCompatibility.ClearPendingRefreshes();
        PreAttachedSpawnSystemIds.Clear();
    }

    private static void ResetPreparedEntriesBuildPipelineLocked(bool clearPendingTargetSignature)
    {
        _preparedEntriesBuildVersion++;
        _preparedEntriesBuildInFlight = false;
        _completedPreparedEntriesBuildResult = null;
        _pendingPreparedEntriesBuildRequest = null;
        _pendingCompiledTableBuild = null;
        _pendingGameDataSignature = null;
        if (clearPendingTargetSignature)
        {
            _pendingBuildTargetSignature = "";
        }
    }

    private static IEnumerable<string> EnumerateOverrideConfigurationPaths()
    {
        return DomainConfigurationFileSupport.EnumerateOverrideConfigurationPaths(
            "spawnsystem",
            PrimaryOverrideConfigurationPathYml,
            PrimaryOverrideConfigurationPathYaml,
            usePreferredPrimaryPath: true,
            warn: WarnInvalidEntry);
    }

    private static ParsedSpawnSystemConfigurationDocument ParseConfiguration(string yaml, string? sourcePath)
    {
        ParsedSpawnSystemConfigurationDocument result = new();
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return result;
        }

        using StringReader reader = new(yaml);
        YamlStream stream = new();
        stream.Load(reader);

        if (stream.Documents.Count == 0)
        {
            return result;
        }

        if (stream.Documents[0].RootNode is not YamlSequenceNode sequence)
        {
            throw new YamlException(
                stream.Documents[0].RootNode.Start,
                stream.Documents[0].RootNode.End,
                "Spawnsystem authoritative YAML root must be a sequence.");
        }

        foreach (YamlNode node in sequence.Children)
        {
            if (node is not YamlMappingNode mappingNode)
            {
                result.Warnings.Add(
                    $"Skipped spawnsystem YAML node at {FormatYamlNodeLocation(sourcePath, node.Start)}. Expected a list item object like '- prefab: Fox' but found {DescribeYamlNode(node)}.");
                continue;
            }

            try
            {
                string entryYaml = SerializeYamlNode(mappingNode);
                SpawnSystemConfigurationEntry entry =
                    Deserializer.Deserialize<SpawnSystemConfigurationEntry>(entryYaml) ?? new SpawnSystemConfigurationEntry();
                entry.SourceLine = checked((int)mappingNode.Start.Line);
                entry.SourceColumn = checked((int)mappingNode.Start.Column);
                result.Configuration.Add(entry);
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"Skipped invalid spawnsystem entry at {FormatYamlNodeLocation(sourcePath, mappingNode.Start)}. {FormatEntryParseFailure(ex)}");
            }
        }

        return result;
    }

    private static string SerializeYamlNode(YamlNode node)
    {
        YamlStream stream = new(new YamlDocument(node));
        using StringWriter writer = new(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static string DescribeYamlNode(YamlNode node)
    {
        if (node is YamlScalarNode scalar)
        {
            string value = scalar.Value ?? "";
            return value.Length == 0 ? "an empty scalar" : $"scalar '{value}'";
        }

        if (node is YamlSequenceNode)
        {
            return "a nested sequence";
        }

        if (node is YamlMappingNode)
        {
            return "a mapping";
        }

        return "an unknown YAML node";
    }

    private static string FormatYamlNodeLocation(string? sourcePath, Mark mark)
    {
        string location = string.IsNullOrWhiteSpace(sourcePath) ? "inline YAML" : Path.GetFileName(sourcePath);
        if (mark.Line > 0)
        {
            location = $"{location}:{mark.Line.ToString(CultureInfo.InvariantCulture)}";
        }

        return location;
    }

    private static string FormatEntryParseFailure(Exception ex)
    {
        if (ex is YamlException yamlException)
        {
            return yamlException.Message;
        }

        return ex.Message;
    }

    private static void NormalizeConfiguration(List<SpawnSystemConfigurationEntry> configuration)
    {
        configuration ??= new List<SpawnSystemConfigurationEntry>();
        Dictionary<string, int> duplicateCounts = new(StringComparer.Ordinal);
        foreach (SpawnSystemConfigurationEntry entry in configuration)
        {
        NormalizeEntry(entry);
            string? existingRuleId = NormalizeOptionalRuleId(entry.RuleId);
            if (existingRuleId != null)
            {
                entry.RuleId = existingRuleId;
                continue;
            }

            string baseRuleId = BuildRuleId(entry);
            duplicateCounts.TryGetValue(baseRuleId, out int existingCount);
            entry.RuleId = existingCount == 0 ? baseRuleId : $"{baseRuleId}#{existingCount}";
            duplicateCounts[baseRuleId] = existingCount + 1;
        }
    }

    private static void NormalizeEntry(SpawnSystemConfigurationEntry entry)
    {
        entry.Prefab = NormalizeOptionalString(entry.Prefab);
        string context = entry.SpawnSystem?.Name ?? entry.Prefab ?? "(unnamed)";
        NormalizeSpawnSystemSpawn(entry.SpawnSystem, context);
        NormalizeSpawnSystemConditions(entry.Conditions, context);
        NormalizeSpawnSystemModifiers(entry.Modifiers);
    }

    private static string BuildRuleId(SpawnSystemConfigurationEntry entry)
    {
        SpawnSystemConfigurationEntry normalizedEntry = new()
        {
            Prefab = entry.Prefab,
            Enabled = true,
            SpawnSystem = entry.SpawnSystem != null
                ? ConfigurationEntryCloneSupport.CloneSpawnSystemSpawnDefinition(entry.SpawnSystem)
                : null,
            Conditions = entry.Conditions != null
                ? ConfigurationEntryCloneSupport.CloneSpawnSystemConditionsDefinition(entry.Conditions)
                : null,
            Modifiers = entry.Modifiers != null
                ? ConfigurationEntryCloneSupport.CloneSpawnSystemModifiersDefinition(entry.Modifiers)
                : null
        };

        return $"{entry.Prefab}:{NetworkPayloadSyncSupport.ComputeSpawnSystemEntryIdentitySignature(normalizedEntry)}";
    }

    private static string? NormalizeOptionalRuleId(string? ruleId)
    {
        if (ruleId == null)
        {
            return null;
        }

        string normalized = ruleId.Trim();
        return normalized.Length == 0 ? null : normalized;
    }

    private static void ApplyIfReady(bool queueEspRefreshForLiveSystems, bool queueLiveSystemAttach = false)
    {
        int gameDataSignature = ComputeGameDataSignature();
        if (gameDataSignature == 0)
        {
            LogApplySkipIfNeeded(
                "game_data_unavailable",
                "Spawnsystem sync stage=apply_skipped reason=game_data_unavailable");
            return;
        }

        bool domainEnabled = ShouldApplyLocally();
        string applyTargetSignature = BuildApplyTargetSignature(gameDataSignature, domainEnabled);
        if (!_forceApplyAfterSyncedCommit &&
            string.Equals(_lastAppliedBuildTargetSignature, applyTargetSignature, StringComparison.Ordinal))
        {
            LogApplySkipIfNeeded(
                $"already_applied|{applyTargetSignature}",
                $"Spawnsystem sync stage=apply_skipped reason=already_applied target={applyTargetSignature}");
            return;
        }

        if (string.Equals(_pendingBuildTargetSignature, applyTargetSignature, StringComparison.Ordinal) &&
            (_preparedEntriesBuildInFlight || _completedPreparedEntriesBuildResult != null || _pendingCompiledTableBuild != null))
        {
            LogApplySkipIfNeeded(
                $"pending_build|{applyTargetSignature}",
                $"Spawnsystem sync stage=apply_skipped reason=pending_build target={applyTargetSignature} prepared_in_flight={_preparedEntriesBuildInFlight} prepared_ready={(_completedPreparedEntriesBuildResult != null)} compiled_pending={(_pendingCompiledTableBuild != null)}");
            return;
        }

        if (DropNSpawnPlugin.IsSourceOfTruth)
        {
            EnsurePrimaryOverrideConfigurationFileExists();
        }
        bool sameGameData = _lastAppliedGameDataSignature == gameDataSignature;
        if (!domainEnabled && sameGameData && _lastAppliedDomainEnabled == false)
        {
            RecordAppliedState(gameDataSignature, domainEnabled, "", applyTargetSignature);
            return;
        }

        if (!domainEnabled)
        {
            ApplySelectedTableWithoutActiveBuild(
                gameDataSignature,
                domainEnabled,
                queueEspRefreshForLiveSystems,
                applyTargetSignature);
            return;
        }

        if (!_configurationReady)
        {
            if (CanRetainCurrentCompiledTableWhilePending(gameDataSignature))
            {
                RetainCurrentCompiledTableWhilePending(
                    gameDataSignature,
                    queueEspRefreshForLiveSystems,
                    queueLiveSystemAttach,
                    applyTargetSignature);
                return;
            }

            ApplySelectedTableWithoutActiveBuild(
                gameDataSignature,
                domainEnabled,
                queueEspRefreshForLiveSystems,
                applyTargetSignature);
            return;
        }

        if (ShouldDeferForExpandWorldDataBiomeReadyLocked())
        {
            DeferExpandWorldDataBiomeReadyLocked(
                queueEspRefreshForLiveSystems,
                queueLiveSystemAttach,
                publishSyncedConfiguration: false);
            return;
        }

        QueuePreparedEntriesBuildLocked(
            gameDataSignature,
            domainEnabled,
            applyTargetSignature,
            queueEspRefreshForLiveSystems);
    }

    private static void LogSyncedConfigCommittedIfNeeded(string payloadToken, int entryCount)
    {
        string normalizedPayloadToken = payloadToken ?? "";
        if (string.Equals(_lastLoggedSyncedConfigPayloadToken, normalizedPayloadToken, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedSyncedConfigPayloadToken = normalizedPayloadToken;
        if (PluginSettingsFacade.IsSpawnSystemDiagnosticsEnabled())
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
                $"Spawnsystem sync stage=config_committed hash={(normalizedPayloadToken.Length > 0 ? normalizedPayloadToken : "<empty>")} entries={entryCount.ToString(CultureInfo.InvariantCulture)} build={DropNSpawnPlugin.RuntimeBuildStamp} force_reapply=armed");
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=config_committed hash={(normalizedPayloadToken.Length > 0 ? normalizedPayloadToken : "<empty>")} entries={entryCount.ToString(CultureInfo.InvariantCulture)}");
    }

    private static void RetainCurrentCompiledTableWhilePending(
        int gameDataSignature,
        bool queueEspRefreshForLiveSystems,
        bool queueLiveSystemAttach,
        string applyTargetSignature)
    {
        if (_activeCompiledTable == null)
        {
            return;
        }

        List<SpawnSystem> liveSystems = GetLiveSystems();
        if (queueLiveSystemAttach)
        {
            QueueLiveSystemAttachForTable(
                _activeCompiledTable,
                _preparedEntriesBuildVersion,
                queueEspRefreshForLiveSystems,
                liveSystems);
        }
        else if (queueEspRefreshForLiveSystems)
        {
            foreach (SpawnSystem liveSystem in liveSystems)
            {
                QueueEspMarkerRefresh(liveSystem);
            }
        }

        LogRuntimeTableAttachedIfNeeded(
            applyTargetSignature,
            "retained_pending",
            _activeCompiledTable,
            liveSystems.Count);
        RecordAppliedState(
            gameDataSignature,
            true,
            _lastAppliedPreparedEntriesSignature,
            applyTargetSignature);
    }

    private static void LogApplySkipIfNeeded(string key, string message)
    {
        if (!PluginSettingsFacade.IsSpawnSystemDiagnosticsEnabled())
        {
            return;
        }

        if (string.Equals(_lastLoggedApplySkipKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedApplySkipKey = key;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(message);
    }

    private static void LogPreparedBuildQueuedIfNeeded(int buildVersion, string applyTargetSignature, int entryCount)
    {
        if (!PluginSettingsFacade.IsSpawnSystemDiagnosticsEnabled())
        {
            return;
        }

        string key = $"{buildVersion.ToString(CultureInfo.InvariantCulture)}|{applyTargetSignature}";
        if (string.Equals(_lastLoggedPreparedBuildQueuedSignature, key, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedPreparedBuildQueuedSignature = key;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=prepared_build_queued version={buildVersion.ToString(CultureInfo.InvariantCulture)} entries={entryCount.ToString(CultureInfo.InvariantCulture)} target={applyTargetSignature}");
    }

    private static void LogPreparedBuildCompletedIfNeeded(int buildVersion, int modelCount, string applyTargetSignature)
    {
        if (!PluginSettingsFacade.IsSpawnSystemDiagnosticsEnabled())
        {
            return;
        }

        if (_lastLoggedPreparedBuildCompletedVersion == buildVersion)
        {
            return;
        }

        _lastLoggedPreparedBuildCompletedVersion = buildVersion;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=prepared_build_completed version={buildVersion.ToString(CultureInfo.InvariantCulture)} models={modelCount.ToString(CultureInfo.InvariantCulture)} target={applyTargetSignature}");
    }

    private static void LogPreparedBuildActivatedIfNeeded(int buildVersion, int modelCount, string applyTargetSignature)
    {
        if (!PluginSettingsFacade.IsSpawnSystemDiagnosticsEnabled())
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=prepared_build_activated version={buildVersion.ToString(CultureInfo.InvariantCulture)} models={modelCount.ToString(CultureInfo.InvariantCulture)} target={applyTargetSignature}");
    }

    private static void LogCompiledBuildStartedIfNeeded(int buildVersion, int finalizedEntryCount, string applyTargetSignature)
    {
        if (!PluginSettingsFacade.IsSpawnSystemDiagnosticsEnabled())
        {
            return;
        }

        if (_lastLoggedCompiledBuildStartedVersion == buildVersion)
        {
            return;
        }

        _lastLoggedCompiledBuildStartedVersion = buildVersion;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=compiled_build_started version={buildVersion.ToString(CultureInfo.InvariantCulture)} finalizedEntries={finalizedEntryCount.ToString(CultureInfo.InvariantCulture)} target={applyTargetSignature}");
    }

    private static void LogCompiledBuildFinishedIfNeeded(int buildVersion, int finalizedEntryCount, int liveSystemCount, string applyTargetSignature)
    {
        if (!PluginSettingsFacade.IsSpawnSystemDiagnosticsEnabled())
        {
            return;
        }

        if (_lastLoggedCompiledBuildFinishedVersion == buildVersion)
        {
            return;
        }

        _lastLoggedCompiledBuildFinishedVersion = buildVersion;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=compiled_build_finished version={buildVersion.ToString(CultureInfo.InvariantCulture)} finalizedEntries={finalizedEntryCount.ToString(CultureInfo.InvariantCulture)} liveSystems={liveSystemCount.ToString(CultureInfo.InvariantCulture)} target={applyTargetSignature}");
    }

    private static void LogAwakeRetriggerIfNeeded(SpawnSystem system, int liveSystemCount)
    {
        if (!PluginSettingsFacade.IsSpawnSystemDiagnosticsEnabled())
        {
            return;
        }

        string key = $"{LoadState.LastLoadedPayload}|{system.GetInstanceID().ToString(CultureInfo.InvariantCulture)}";
        if (string.Equals(_lastLoggedAwakeRetriggerKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedAwakeRetriggerKey = key;
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=awake_retrigger version={_preparedEntriesBuildVersion.ToString(CultureInfo.InvariantCulture)} liveSystems={liveSystemCount.ToString(CultureInfo.InvariantCulture)} payloadHash={(LoadState.LastLoadedPayload.Length > 0 ? LoadState.LastLoadedPayload : "<none>")}");
    }

    private static void LogVanillaRetainedIfNeeded(
        string applyTargetSignature,
        string reason,
        CompiledSpawnSystemTable? table,
        int liveSystemCount)
    {
        if (string.Equals(_lastLoggedVanillaRetainedSignature, applyTargetSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedVanillaRetainedSignature = applyTargetSignature;
        _lastLoggedRuntimeAttachSignature = "";
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=vanilla_retained reason={reason} liveSystems={liveSystemCount.ToString(CultureInfo.InvariantCulture)} rows={CountSpawnRows(table).ToString(CultureInfo.InvariantCulture)}");
    }

    private static void LogRuntimeTableAttachedIfNeeded(
        string applyTargetSignature,
        string kind,
        CompiledSpawnSystemTable? table,
        int liveSystemCount)
    {
        if (string.Equals(_lastLoggedRuntimeAttachSignature, applyTargetSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedRuntimeAttachSignature = applyTargetSignature;
        _lastLoggedVanillaRetainedSignature = "";
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=runtime_table_attached kind={kind} liveSystems={liveSystemCount.ToString(CultureInfo.InvariantCulture)} rows={CountSpawnRows(table).ToString(CultureInfo.InvariantCulture)}");
    }

    private static void LogLiveSpawnListState(string stage, SpawnSystem? system, CompiledSpawnSystemTable? table, bool preAttached = false)
    {
        if (!PluginSettingsFacade.IsSpawnSystemDiagnosticsEnabled() || system == null)
        {
            return;
        }

        SpawnListSummary liveSummary = SummarizeSpawnLists(system.m_spawnLists);
        SpawnListSummary tableSummary = SummarizeSpawnLists(table?.Lists);
        bool attached = IsSystemAttachedToCompiledTable(system, table);
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem live stage={stage} systemId={system.GetInstanceID().ToString(CultureInfo.InvariantCulture)} preAttached={preAttached} attached={attached} " +
            $"liveLists={liveSummary.ListCount.ToString(CultureInfo.InvariantCulture)} liveRows={liveSummary.RowCount.ToString(CultureInfo.InvariantCulture)} liveHash={liveSummary.ContentHash.ToString(CultureInfo.InvariantCulture)} " +
            $"tableLists={tableSummary.ListCount.ToString(CultureInfo.InvariantCulture)} tableRows={tableSummary.RowCount.ToString(CultureInfo.InvariantCulture)} tableHash={tableSummary.ContentHash.ToString(CultureInfo.InvariantCulture)} " +
            $"baselineLists={table?.BaselineListCount.ToString(CultureInfo.InvariantCulture) ?? "0"} baselineRows={table?.BaselineRowCount.ToString(CultureInfo.InvariantCulture) ?? "0"} baselineHash={table?.BaselineContentHash.ToString(CultureInfo.InvariantCulture) ?? "0"} " +
            $"liveSample='{liveSummary.SamplePrefabs}' tableSample='{tableSummary.SamplePrefabs}'");
    }

    private static SpawnListSummary SummarizeSpawnLists(IEnumerable<SpawnSystemList>? spawnLists)
    {
        if (spawnLists == null)
        {
            return new SpawnListSummary
            {
                ListCount = 0,
                RowCount = 0,
                ContentHash = 0,
                SamplePrefabs = ""
            };
        }

        int listCount = 0;
        int rowCount = 0;
        int hash = 17;
        List<string> samplePrefabs = new();
        foreach (SpawnSystemList spawnList in spawnLists)
        {
            if (spawnList == null)
            {
                continue;
            }

            listCount++;
            hash = unchecked(hash * 31 + listCount);
            List<SpawnSystem.SpawnData>? spawners = spawnList.m_spawners;
            int spawnerCount = spawners?.Count ?? 0;
            rowCount += spawnerCount;
            hash = unchecked(hash * 31 + spawnerCount);
            if (spawners == null)
            {
                continue;
            }

            foreach (SpawnSystem.SpawnData spawnData in spawners)
            {
                string prefabName = spawnData?.m_prefab != null
                    ? Utils.GetPrefabName(spawnData.m_prefab)
                    : spawnData?.m_name ?? "<null>";
                hash = unchecked(hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(prefabName));
                hash = unchecked(hash * 31 + (spawnData?.m_enabled == true ? 1 : 0));
                if (samplePrefabs.Count < 8)
                {
                    samplePrefabs.Add(prefabName);
                }
            }
        }

        return new SpawnListSummary
        {
            ListCount = listCount,
            RowCount = rowCount,
            ContentHash = hash,
            SamplePrefabs = samplePrefabs.Count > 0 ? string.Join(",", samplePrefabs) : "<empty>"
        };
    }

    private static void FreezeCompiledTableBaseline(CompiledSpawnSystemTable? table)
    {
        if (table == null)
        {
            return;
        }

        SpawnListSummary summary = SummarizeSpawnLists(table.Lists);
        table.BaselineListCount = summary.ListCount;
        table.BaselineRowCount = summary.RowCount;
        table.BaselineContentHash = summary.ContentHash;
    }

    private static int CountSpawnRows(CompiledSpawnSystemTable? table)
    {
        if (table == null)
        {
            return 0;
        }

        int count = 0;
        foreach (SpawnSystemList spawnList in table.Lists)
        {
            if (spawnList?.m_spawners != null)
            {
                count += spawnList.m_spawners.Count;
            }
        }

        return count;
    }

    private static void RecordAppliedState(int gameDataSignature, bool domainEnabled, string preparedEntriesSignature, string applyTargetSignature)
    {
        _lastAppliedGameDataSignature = gameDataSignature;
        _lastAppliedDomainEnabled = domainEnabled;
        _lastAppliedConfigurationSignature = _configurationSignature;
        _lastAppliedPreparedEntriesSignature = preparedEntriesSignature;
        _lastAppliedBuildTargetSignature = applyTargetSignature;
        _forceApplyAfterSyncedCommit = false;
        _lastCompletedGameDataSignature = gameDataSignature;
        if (_pendingGameDataSignature == gameDataSignature)
        {
            _pendingGameDataSignature = null;
        }
    }

    private static string BuildApplyTargetSignature(int gameDataSignature, bool domainEnabled)
    {
        return string.Concat(
            gameDataSignature.ToString(CultureInfo.InvariantCulture),
            "|",
            domainEnabled ? "enabled" : "disabled",
            "|",
            _configurationReady ? "config_ready" : "config_pending",
            "|",
            _configurationSignature);
    }

    private static void ApplySelectedTableWithoutActiveBuild(
        int gameDataSignature,
        bool domainEnabled,
        bool queueEspRefreshForLiveSystems,
        string applyTargetSignature)
    {
        CompiledSpawnSystemTable? previousSelectedTable = GetSelectedCompiledTableForCurrentState();
        List<SpawnSystem> liveSystems = GetLiveSystems();
        EnsureVanillaCompiledTableCurrentLocked(gameDataSignature);
        _activeCompiledTable = null;
        CompiledSpawnSystemTable? selectedTable = GetSelectedCompiledTableForCurrentState();
        QueueLiveSystemAttachForTable(selectedTable, _preparedEntriesBuildVersion, queueEspRefreshForLiveSystems, liveSystems);
        RetireCompiledTableAfterMigrationLocked(previousSelectedTable, selectedTable, liveSystems);
        string reason = !domainEnabled
            ? "domain_disabled"
            : (_configurationReady ? "authoritative_unavailable" : "config_not_ready");
        LogVanillaRetainedIfNeeded(applyTargetSignature, reason, selectedTable, liveSystems.Count);
        RecordAppliedState(gameDataSignature, domainEnabled, "", applyTargetSignature);
    }

    private static void EnsureVanillaCompiledTableCurrentLocked(int gameDataSignature)
    {
        if (_vanillaCompiledTable == null || _vanillaCompiledTable.GameDataSignature != gameDataSignature)
        {
            CompiledSpawnSystemTable? previousVanillaTable = _vanillaCompiledTable;
            _vanillaCompiledTable = BuildVanillaCompiledTable(gameDataSignature);
            DestroyCompiledTableIfInactiveLocked(previousVanillaTable);
        }
    }

    private static void QueuePreparedEntriesBuildLocked(
        int gameDataSignature,
        bool domainEnabled,
        string applyTargetSignature,
        bool queueEspRefreshForLiveSystems)
    {
        ClearQueuedReconcileState();
        _preparedEntriesBuildVersion++;
        int buildVersion = _preparedEntriesBuildVersion;
        _preparedEntriesBuildInFlight = true;
        _completedPreparedEntriesBuildResult = null;
        _pendingCompiledTableBuild = null;
        _pendingBuildTargetSignature = applyTargetSignature;
        PendingPreparedEntriesBuildRequest request = new()
        {
            BuildVersion = buildVersion,
            GameDataSignature = gameDataSignature,
            DomainEnabled = domainEnabled,
            ApplyTargetSignature = applyTargetSignature,
            QueueEspRefreshForLiveSystems = queueEspRefreshForLiveSystems
        };
        request.ConfigurationSnapshot.AddRange(_configuration);
        _pendingPreparedEntriesBuildRequest = request;
        LogPreparedBuildQueuedIfNeeded(buildVersion, applyTargetSignature, request.ConfigurationSnapshot.Count);
        EnsurePreparedEntriesBuildWorkerLocked();
    }

    private static void EnsurePreparedEntriesBuildWorkerLocked()
    {
        if (_preparedEntriesBuildWorkerRunning)
        {
            return;
        }

        _preparedEntriesBuildWorkerRunning = true;
        ThreadPool.QueueUserWorkItem(_ => ProcessPreparedEntriesBuildWorker());
    }

    private static void ProcessPreparedEntriesBuildWorker()
    {
        while (true)
        {
            PendingPreparedEntriesBuildRequest? request;
            lock (Sync)
            {
                request = _pendingPreparedEntriesBuildRequest;
                _pendingPreparedEntriesBuildRequest = null;
                if (request == null)
                {
                    _preparedEntriesBuildWorkerRunning = false;
                    if (_completedPreparedEntriesBuildResult == null)
                    {
                        _preparedEntriesBuildInFlight = false;
                    }

                    return;
                }
            }

            try
            {
                PreparedEntriesBuildResult? result = BuildPreparedEntriesResult(request);
                if (result == null)
                {
                    continue;
                }

                lock (Sync)
                {
                    if (!IsPreparedEntriesBuildCurrentLocked(result.BuildVersion, result.ApplyTargetSignature))
                    {
                        continue;
                    }

                    PruneFinalizedPreparedEntryCacheLocked(
                        result.GameDataSignature,
                        result.Models.Select(model => model.EntrySignature));
                    _completedPreparedEntriesBuildResult = result;
                    _preparedEntriesBuildInFlight = _pendingPreparedEntriesBuildRequest != null;
                    LogPreparedBuildCompletedIfNeeded(result.BuildVersion, result.Models.Count, result.ApplyTargetSignature);
                }
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    if (request != null &&
                        IsPreparedEntriesBuildCurrentLocked(request.BuildVersion, request.ApplyTargetSignature))
                    {
                        _preparedEntriesBuildInFlight = _pendingPreparedEntriesBuildRequest != null;
                        _completedPreparedEntriesBuildResult = null;
                        if (_pendingPreparedEntriesBuildRequest == null)
                        {
                            _pendingBuildTargetSignature = "";
                            _pendingGameDataSignature = null;
                        }
                    }
                }

                DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to prepare staged spawnsystem build. {ex}");
            }
        }
    }

    private static bool IsPreparedEntriesBuildCurrentLocked(int buildVersion, string applyTargetSignature)
    {
        return buildVersion == _preparedEntriesBuildVersion &&
               string.Equals(_pendingBuildTargetSignature, applyTargetSignature, StringComparison.Ordinal);
    }

    private static bool IsPreparedEntriesBuildCurrent(int buildVersion, string applyTargetSignature)
    {
        lock (Sync)
        {
            return IsPreparedEntriesBuildCurrentLocked(buildVersion, applyTargetSignature);
        }
    }

    private static PreparedEntriesBuildResult? BuildPreparedEntriesResult(PendingPreparedEntriesBuildRequest request)
    {
        PreparedEntriesBuildResult result = new()
        {
            BuildVersion = request.BuildVersion,
            GameDataSignature = request.GameDataSignature,
            DomainEnabled = request.DomainEnabled,
            ApplyTargetSignature = request.ApplyTargetSignature,
            QueueEspRefreshForLiveSystems = request.QueueEspRefreshForLiveSystems
        };

        for (int index = 0; index < request.ConfigurationSnapshot.Count; index++)
        {
            if (!IsPreparedEntriesBuildCurrent(request.BuildVersion, request.ApplyTargetSignature))
            {
                return null;
            }

            CanonicalSpawnSystemEntry entry = request.ConfigurationSnapshot[index];
            result.Models.Add(new PreparedSpawnSystemModel
            {
                Entry = entry,
                RuleId = entry.RuleId,
                EntrySignature = NetworkPayloadSyncSupport.ComputeSpawnSystemEntrySignature(entry),
                Context = CreateConfigurationContext(index, entry),
                RuntimeTimeOfDay = GetConfiguredTimeOfDay(entry)
            });
        }

        if (!IsPreparedEntriesBuildCurrent(request.BuildVersion, request.ApplyTargetSignature))
        {
            return null;
        }

        return result;
    }

    private static void PruneFinalizedPreparedEntryCacheLocked(
        int gameDataSignature,
        IEnumerable<string>? activeEntrySignatures = null)
    {
        if (FinalizedPreparedEntryCache.Count == 0)
        {
            return;
        }

        HashSet<string>? activeEntrySignatureSet = null;
        if (activeEntrySignatures != null)
        {
            activeEntrySignatureSet = new HashSet<string>(
                activeEntrySignatures.Where(signature => !string.IsNullOrWhiteSpace(signature)),
                StringComparer.Ordinal);
        }

        List<string> staleKeys = new();
        foreach ((string cacheKey, FinalizedPreparedEntryCacheEntry entry) in FinalizedPreparedEntryCache)
        {
            if (entry == null)
            {
                staleKeys.Add(cacheKey);
                continue;
            }

            if (entry.GameDataSignature != gameDataSignature)
            {
                staleKeys.Add(cacheKey);
                continue;
            }

            if (activeEntrySignatureSet != null &&
                !activeEntrySignatureSet.Contains(entry.EntrySignature))
            {
                staleKeys.Add(cacheKey);
            }
        }

        foreach (string staleKey in staleKeys)
        {
            FinalizedPreparedEntryCache.Remove(staleKey);
        }
    }

    private static void RefreshResolvedBiomeMasksForSourceOfTruthConfigurationLocked()
    {
        if (!DropNSpawnPlugin.IsSourceOfTruth)
        {
            return;
        }

        RefreshResolvedBiomeMasksForConfiguration(_configuration);
    }

    private static void RefreshResolvedBiomeMasksForConfiguration(IEnumerable<CanonicalSpawnSystemEntry> configuration)
    {
        foreach (CanonicalSpawnSystemEntry entry in configuration ?? Enumerable.Empty<CanonicalSpawnSystemEntry>())
        {
            SpawnSystemConditionsDefinition? conditions = entry.Conditions;
            if (conditions == null)
            {
                continue;
            }

            conditions.ResolvedBiomeMask = BiomeResolutionSupport.ResolveBiomeMaskOrNull(conditions.Biomes);
        }
    }

    private static void PublishSyncedConfigurationOrDeferLocked()
    {
        if (!DropNSpawnPlugin.IsSourceOfTruth)
        {
            return;
        }

        if (ShouldDeferForExpandWorldDataBiomeReadyLocked())
        {
            DeferExpandWorldDataBiomeReadyLocked(
                queueEspRefreshForLiveSystems: false,
                queueLiveSystemAttach: false,
                publishSyncedConfiguration: true);
            return;
        }

        _deferredPublishSyncedConfiguration = false;
        ConfigurationDomainHost.PublishSyncedPayload(
            DropNSpawnPlugin.IsSourceOfTruth,
            Descriptor,
            _configuration,
            _configurationSignature);
    }

    private static bool ShouldDeferForExpandWorldDataBiomeReadyLocked()
    {
        return ShouldDeferForExpandWorldDataBiomeReady(_configuration);
    }

    private static bool ShouldDeferForExpandWorldDataBiomeReady(IEnumerable<CanonicalSpawnSystemEntry> configuration)
    {
        if (!BiomeResolutionSupport.IsExpandWorldDataPresent() ||
            BiomeResolutionSupport.IsExpandWorldDataReadyOrUnavailable())
        {
            return false;
        }

        foreach (CanonicalSpawnSystemEntry entry in configuration ?? Enumerable.Empty<CanonicalSpawnSystemEntry>())
        {
            SpawnSystemConditionsDefinition? conditions = entry.Conditions;
            if (conditions == null)
            {
                continue;
            }

            if (BiomeResolutionSupport.ShouldWaitForExpandWorldDataBiomeResolution(
                    conditions.Biomes,
                    conditions.ResolvedBiomeMask))
            {
                return true;
            }
        }

        return false;
    }

    private static void DeferExpandWorldDataBiomeReadyLocked(
        bool queueEspRefreshForLiveSystems,
        bool queueLiveSystemAttach,
        bool publishSyncedConfiguration)
    {
        _waitingForExpandWorldDataBiomeReady = true;
        _deferredQueueEspRefreshForLiveSystems |= queueEspRefreshForLiveSystems;
        _deferredQueueLiveSystemAttach |= queueLiveSystemAttach;
        _deferredPublishSyncedConfiguration |= publishSyncedConfiguration;
        if (_loggedExpandWorldDataBiomeReadyWait)
        {
            return;
        }

        _loggedExpandWorldDataBiomeReadyWait = true;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            "Deferring spawnsystem build until ExpandWorldData biome sync is ready.");
    }

    private static bool TryProcessDeferredExpandWorldDataBiomeReadyLocked()
    {
        if (!_waitingForExpandWorldDataBiomeReady && !_deferredPublishSyncedConfiguration)
        {
            return false;
        }

        if (!BiomeResolutionSupport.IsExpandWorldDataReadyOrUnavailable())
        {
            return false;
        }

        bool queueEspRefreshForLiveSystems = _deferredQueueEspRefreshForLiveSystems;
        bool queueLiveSystemAttach = _deferredQueueLiveSystemAttach;
        bool publishSyncedConfiguration = _deferredPublishSyncedConfiguration;
        _waitingForExpandWorldDataBiomeReady = false;
        _deferredQueueEspRefreshForLiveSystems = false;
        _deferredQueueLiveSystemAttach = false;
        _deferredPublishSyncedConfiguration = false;
        _loggedExpandWorldDataBiomeReadyWait = false;

        RefreshResolvedBiomeMasksForSourceOfTruthConfigurationLocked();
        _configurationSignature = NetworkPayloadSyncSupport.ComputeSpawnSystemConfigurationSignature(_configuration);
        if (publishSyncedConfiguration)
        {
            ConfigurationDomainHost.PublishSyncedPayload(
                DropNSpawnPlugin.IsSourceOfTruth,
                Descriptor,
                _configuration,
                _configurationSignature);
        }

        ApplyIfReady(queueEspRefreshForLiveSystems, queueLiveSystemAttach);
        return true;
    }

    private static bool TryActivateCompletedPreparedEntriesBuildLocked()
    {
        if (_completedPreparedEntriesBuildResult == null)
        {
            return false;
        }

        PreparedEntriesBuildResult completedResult = _completedPreparedEntriesBuildResult;
        _completedPreparedEntriesBuildResult = null;
        if (completedResult.BuildVersion != _preparedEntriesBuildVersion ||
            !string.Equals(_pendingBuildTargetSignature, completedResult.ApplyTargetSignature, StringComparison.Ordinal))
        {
            return true;
        }

        PendingCompiledTableBuildState buildState = new()
        {
            BuildVersion = completedResult.BuildVersion,
            GameDataSignature = completedResult.GameDataSignature,
            DomainEnabled = completedResult.DomainEnabled,
            EagerClientSyncBuild = !DropNSpawnPlugin.IsSourceOfTruth,
            ApplyTargetSignature = completedResult.ApplyTargetSignature,
            QueueEspRefreshForLiveSystems = completedResult.QueueEspRefreshForLiveSystems,
            PreviousActiveTable = _activeCompiledTable,
            PreviousVanillaTable = _vanillaCompiledTable
        };
        buildState.Models.AddRange(completedResult.Models);
        _pendingCompiledTableBuild = buildState;
        LogPreparedBuildActivatedIfNeeded(buildState.BuildVersion, buildState.Models.Count, buildState.ApplyTargetSignature);
        return true;
    }

    private static bool TryProcessPendingCompiledTableBuild(float deadline)
    {
        if (_pendingCompiledTableBuild == null)
        {
            return false;
        }

        PendingCompiledTableBuildState buildState = _pendingCompiledTableBuild;
        if (buildState.BuildVersion != _preparedEntriesBuildVersion ||
            !string.Equals(_pendingBuildTargetSignature, buildState.ApplyTargetSignature, StringComparison.Ordinal))
        {
            _pendingCompiledTableBuild = null;
            return true;
        }

        EnsureVanillaCompiledTableCurrentLocked(buildState.GameDataSignature);

        if (buildState.DomainEnabled && buildState.NextFinalizeIndex < buildState.Models.Count)
        {
            int processedEntries = 0;
            int perStepLimit = buildState.EagerClientSyncBuild ? int.MaxValue : FinalizedPreparedEntriesPerStep;
            while (buildState.NextFinalizeIndex < buildState.Models.Count &&
                   processedEntries < perStepLimit &&
                   (buildState.EagerClientSyncBuild || Time.realtimeSinceStartup < deadline))
            {
                PreparedSpawnSystemModel model = buildState.Models[buildState.NextFinalizeIndex++];
                if (TryFinalizePreparedSpawnSystemModelLocked(model, buildState.GameDataSignature, out PreparedSpawnSystemEntry? finalizedEntry))
                {
                    buildState.FinalizedEntries.Add(finalizedEntry!);
                }

                processedEntries++;
            }

            return processedEntries > 0;
        }

        if (buildState.DomainEnabled)
        {
            buildState.PreparedEntriesSignature = ComputePreparedEntriesSignature(buildState.FinalizedEntries);

            if (buildState.BuildingActiveTable == null)
            {
                buildState.BuildingActiveTable = new CompiledSpawnSystemTable
                {
                    GameDataSignature = buildState.GameDataSignature,
                    Signature = buildState.PreparedEntriesSignature
                };
                buildState.BuildingLiveEntries = new List<SpawnSystem.SpawnData>(buildState.FinalizedEntries.Count);
                LogCompiledBuildStartedIfNeeded(buildState.BuildVersion, buildState.FinalizedEntries.Count, buildState.ApplyTargetSignature);
                return true;
            }

            if (buildState.NextCompiledEntryIndex < buildState.FinalizedEntries.Count)
            {
                int processedEntries = 0;
                int perStepLimit = buildState.EagerClientSyncBuild ? int.MaxValue : CompiledEntryBuildsPerStep;
                while (buildState.NextCompiledEntryIndex < buildState.FinalizedEntries.Count &&
                       processedEntries < perStepLimit &&
                       (buildState.EagerClientSyncBuild || Time.realtimeSinceStartup < deadline))
                {
                    PreparedSpawnSystemEntry finalizedEntry = buildState.FinalizedEntries[buildState.NextCompiledEntryIndex++];
                    SpawnSystem.SpawnData liveEntry = finalizedEntry.Data.Clone();
                    StageCompiledRuntimeMetadata(
                        buildState.BuildingActiveTable,
                        liveEntry,
                        finalizedEntry.CustomDataPayload,
                        finalizedEntry.RuntimeTimeOfDay);
                    buildState.BuildingLiveEntries!.Add(liveEntry);
                    processedEntries++;
                }

                return processedEntries > 0;
            }

            if (buildState.BuildingActiveTable.Lists.Count == 0)
            {
                buildState.BuildingActiveTable.Lists.Add(CreateManagedSpawnList(buildState.BuildingLiveEntries ?? new List<SpawnSystem.SpawnData>()));
                FreezeCompiledTableBaseline(buildState.BuildingActiveTable);
                return true;
            }
        }

        List<SpawnSystem> liveSystems = GetLiveSystems();
        CompiledSpawnSystemTable? previousSelectedTable = GetSelectedCompiledTableForCurrentState();
        _activeCompiledTable = buildState.DomainEnabled ? buildState.BuildingActiveTable : null;
        QueueLiveSystemAttachForTable(_activeCompiledTable, buildState.BuildVersion, buildState.QueueEspRefreshForLiveSystems, liveSystems);
        RetireCompiledTableAfterMigrationLocked(previousSelectedTable, _activeCompiledTable, liveSystems);
        LogCompiledBuildFinishedIfNeeded(buildState.BuildVersion, buildState.FinalizedEntries.Count, liveSystems.Count, buildState.ApplyTargetSignature);
        LogRuntimeTableAttachedIfNeeded(buildState.ApplyTargetSignature, "authoritative", _activeCompiledTable, liveSystems.Count);
        DestroyCompiledTableIfInactiveLocked(buildState.PreviousActiveTable);
        DestroyCompiledTableIfInactiveLocked(buildState.PreviousVanillaTable);
        RecordAppliedState(
            buildState.GameDataSignature,
            buildState.DomainEnabled,
            buildState.PreparedEntriesSignature,
            buildState.ApplyTargetSignature);
        _pendingBuildTargetSignature = "";
        _pendingCompiledTableBuild = null;
        return true;
    }

    private static bool TryFinalizePreparedSpawnSystemModelLocked(
        PreparedSpawnSystemModel model,
        int gameDataSignature,
        out PreparedSpawnSystemEntry? finalizedEntry)
    {
        finalizedEntry = null;
        if (model == null || model.Entry == null)
        {
            return false;
        }

        string ruleId = model.RuleId;
        model.RuleId = ruleId;
        string entrySignature = model.EntrySignature.Length > 0
            ? model.EntrySignature
            : NetworkPayloadSyncSupport.ComputeSpawnSystemEntrySignature(model.Entry);
        model.EntrySignature = entrySignature;
        string cacheKey = string.Concat(gameDataSignature.ToString(CultureInfo.InvariantCulture), "|", entrySignature);
        if (FinalizedPreparedEntryCache.TryGetValue(cacheKey, out FinalizedPreparedEntryCacheEntry? cachedEntry) &&
            cachedEntry != null &&
            cachedEntry.GameDataSignature == gameDataSignature)
        {
            finalizedEntry = new PreparedSpawnSystemEntry
            {
                Entry = model.Entry,
                Data = cachedEntry.Data.Clone(),
                Context = model.Context,
                CustomDataPayload = cachedEntry.CustomDataPayload,
                RuntimeTimeOfDay = cachedEntry.RuntimeTimeOfDay
            };
            return true;
        }

        SpawnSystem.SpawnData data = new();
        if (!ApplyEntry(data, model.Entry, model.Context, applyCustomData: false))
        {
            return false;
        }

        SpawnSystemCustomDataSupport.PreparedPayload? customDataPayload =
            SpawnSystemCustomDataSupport.BuildPreparedPayload(data, model.Entry, model.Context);
        FinalizedPreparedEntryCache[cacheKey] = new FinalizedPreparedEntryCacheEntry
        {
            GameDataSignature = gameDataSignature,
            RuleId = ruleId,
            EntrySignature = entrySignature,
            Data = data.Clone(),
            CustomDataPayload = customDataPayload,
            RuntimeTimeOfDay = model.RuntimeTimeOfDay
        };

        finalizedEntry = new PreparedSpawnSystemEntry
        {
            Entry = model.Entry,
            Data = data,
            Context = model.Context,
            CustomDataPayload = customDataPayload,
            RuntimeTimeOfDay = model.RuntimeTimeOfDay
        };
        return true;
    }

    private static void StageCompiledRuntimeMetadata(
        CompiledSpawnSystemTable? table,
        SpawnSystem.SpawnData spawnData,
        SpawnSystemCustomDataSupport.PreparedPayload? customDataPayload,
        TimeOfDayDefinition? timeOfDay)
    {
        if (table == null || spawnData == null)
        {
            return;
        }

        if (timeOfDay?.HasValues() == true)
        {
            table.RuntimeTimeOfDayBySpawnData[spawnData] = timeOfDay;
        }

        if (customDataPayload != null)
        {
            table.CustomPayloadsBySpawnData[spawnData] = customDataPayload;
        }
    }

    private static void AddCompiledRuntimeState(CompiledSpawnSystemTable? table)
    {
        if (table == null)
        {
            return;
        }

        foreach ((SpawnSystem.SpawnData spawnData, TimeOfDayDefinition timeOfDay) in table.RuntimeTimeOfDayBySpawnData)
        {
            TimeOfDayBySpawnData[spawnData] = timeOfDay;
        }

        foreach ((SpawnSystem.SpawnData spawnData, SpawnSystemCustomDataSupport.PreparedPayload? payload) in table.CustomPayloadsBySpawnData)
        {
            SpawnSystemCustomDataSupport.ApplyPreparedPayload(spawnData, payload);
        }

        _hasRuntimeTimeOfDayOverrides = TimeOfDayBySpawnData.Count > 0;
    }

    private static void RemoveCompiledRuntimeState(CompiledSpawnSystemTable? table)
    {
        if (table == null)
        {
            return;
        }

        InvalidateRuntimeTimeOfDayPhaseMarker();

        foreach (SpawnSystem.SpawnData spawnData in table.RuntimeTimeOfDayBySpawnData.Keys)
        {
            TimeOfDayBySpawnData.Remove(spawnData);
        }

        foreach (SpawnSystem.SpawnData spawnData in table.CustomPayloadsBySpawnData.Keys)
        {
            SpawnSystemCustomDataSupport.ApplyPreparedPayload(spawnData, null);
        }

        _hasRuntimeTimeOfDayOverrides = TimeOfDayBySpawnData.Count > 0;
    }

    private static void ClearRuntimeCompiledState()
    {
        InvalidateRuntimeTimeOfDayPhaseMarker();
        TimeOfDayBySpawnData.Clear();
        _hasRuntimeTimeOfDayOverrides = false;
        SpawnSystemCustomDataSupport.ClearAll();
    }

    private static void RetireCompiledTableAfterMigrationLocked(
        CompiledSpawnSystemTable? previousSelectedTable,
        CompiledSpawnSystemTable? nextSelectedTable,
        IEnumerable<SpawnSystem> liveSystems)
    {
        if (previousSelectedTable == null || ReferenceEquals(previousSelectedTable, nextSelectedTable))
        {
            return;
        }

        PendingCompiledTableRetirement? existingRetirement = PendingCompiledTableRetirements
            .FirstOrDefault(retirement => ReferenceEquals(retirement.Table, previousSelectedTable));
        if (existingRetirement == null)
        {
            existingRetirement = new PendingCompiledTableRetirement
            {
                Table = previousSelectedTable
            };
            PendingCompiledTableRetirements.Add(existingRetirement);
        }

        foreach (SpawnSystem system in liveSystems)
        {
            if (system != null)
            {
                existingRetirement.RemainingSystemIds.Add(system.GetInstanceID());
            }
        }

        if (existingRetirement.RemainingSystemIds.Count == 0)
        {
            FinalizeCompiledTableRetirementLocked(existingRetirement);
        }
    }

    private static void MarkSystemMigratedFromRetiredTablesLocked(int systemId)
    {
        if (systemId == 0 || PendingCompiledTableRetirements.Count == 0)
        {
            return;
        }

        List<PendingCompiledTableRetirement>? completedRetirements = null;
        foreach (PendingCompiledTableRetirement retirement in PendingCompiledTableRetirements)
        {
            if (!retirement.RemainingSystemIds.Remove(systemId) || retirement.RemainingSystemIds.Count > 0)
            {
                continue;
            }

            completedRetirements ??= new List<PendingCompiledTableRetirement>();
            completedRetirements.Add(retirement);
        }

        if (completedRetirements == null)
        {
            return;
        }

        foreach (PendingCompiledTableRetirement completedRetirement in completedRetirements)
        {
            FinalizeCompiledTableRetirementLocked(completedRetirement);
        }
    }

    private static void FinalizeAllPendingCompiledTableRetirementsLocked()
    {
        if (PendingCompiledTableRetirements.Count == 0)
        {
            return;
        }

        foreach (PendingCompiledTableRetirement retirement in PendingCompiledTableRetirements.ToList())
        {
            FinalizeCompiledTableRetirementLocked(retirement);
        }
    }

    private static void FinalizeCompiledTableRetirementLocked(PendingCompiledTableRetirement retirement)
    {
        if (retirement == null)
        {
            return;
        }

        PendingCompiledTableRetirements.Remove(retirement);
        RemoveCompiledRuntimeState(retirement.Table);
        DestroyReplacedCompiledTable(retirement.Table);
    }

    private static void DestroyCompiledTableIfInactiveLocked(CompiledSpawnSystemTable? table)
    {
        if (table == null ||
            PendingCompiledTableRetirements.Any(retirement => ReferenceEquals(retirement.Table, table)))
        {
            return;
        }

        DestroyReplacedCompiledTable(table);
    }

    private static void QueueLiveSystemAttachForTable(
        CompiledSpawnSystemTable? table,
        int buildVersion,
        bool queueEspRefresh,
        IEnumerable<SpawnSystem>? systems = null)
    {
        QueueLiveSystemAttachForTableCore(table, buildVersion, queueEspRefresh, systems);
    }

    private static void QueueLiveSystemAttach(
        SpawnSystem? system,
        CompiledSpawnSystemTable targetTable,
        int buildVersion,
        bool queueEspRefresh)
    {
        QueueLiveSystemAttachCore(system, targetTable, buildVersion, queueEspRefresh);
    }

    private static void AttachTableToSystem(SpawnSystem? system, CompiledSpawnSystemTable? table)
    {
        AttachTableToSystemCore(system, table);
    }

    private static bool IsSystemAttachedToCompiledTable(SpawnSystem? system, CompiledSpawnSystemTable? table)
    {
        return IsSystemAttachedToCompiledTableCore(system, table);
    }

    private static List<SpawnSystemList> CloneAttachedSpawnLists(CompiledSpawnSystemTable table)
    {
        return CloneAttachedSpawnListsCore(table);
    }

    private static CompiledSpawnSystemTable? GetSelectedCompiledTableForCurrentState()
    {
        return GetSelectedCompiledTableForCurrentStateCore();
    }

    private static CompiledSpawnSystemTable? BuildVanillaCompiledTable(int gameDataSignature)
    {
        return BuildVanillaCompiledTableCore(gameDataSignature);
    }

    private static CompiledSpawnSystemTable BuildActiveCompiledTable(int gameDataSignature, List<PreparedSpawnSystemEntry> entries, string preparedEntriesSignature)
    {
        return BuildActiveCompiledTableCore(gameDataSignature, entries, preparedEntriesSignature);
    }

    private static List<SpawnSystemList> GetVanillaSourceSpawnLists()
    {
        return GetVanillaSourceSpawnListsCore();
    }

    private static SpawnSystem? GetZoneCtrlPrefabSpawnSystem()
    {
        return GetZoneCtrlPrefabSpawnSystemCore();
    }

    private static SpawnSystemList CreateManagedSpawnList(List<SpawnSystem.SpawnData> spawners)
    {
        return CreateManagedSpawnListCore(spawners);
    }

    private static SpawnSystemList CreateAttachedSpawnList(List<SpawnSystem.SpawnData> spawners)
    {
        return CreateAttachedSpawnListCore(spawners);
    }

    private static GameObject GetManagedSpawnListHost()
    {
        return GetManagedSpawnListHostCore();
    }

    private static GameObject GetAttachedSpawnListHost()
    {
        return GetAttachedSpawnListHostCore();
    }

    private static void ClearAttachedRuntimeState(SpawnSystem? system)
    {
        ClearAttachedRuntimeStateCore(system);
    }

    private static void DestroyAttachedSpawnLists(IEnumerable<SpawnSystemList>? spawnLists)
    {
        DestroyAttachedSpawnListsCore(spawnLists);
    }

    private static void DestroyReplacedCompiledTable(CompiledSpawnSystemTable? table)
    {
        DestroyReplacedCompiledTableCore(table);
    }

    private static List<PreparedSpawnSystemEntry> BuildPreparedEntries()
    {
        return BuildPreparedEntriesCore();
    }

    private static void InvalidatePreparedEntriesCache()
    {
        InvalidatePreparedEntriesCacheCore();
    }

    private static string ComputePreparedEntriesSignature(List<PreparedSpawnSystemEntry> entries)
    {
        return ComputePreparedEntriesSignatureCore(entries);
    }

    private static bool ApplyEntry(SpawnSystem.SpawnData data, CanonicalSpawnSystemEntry entry, string context, bool applyCustomData = true)
    {
        bool valid = true;
        bool resolvedPrefab = false;
        string? resolvedPrefabName = null;
        SpawnSystemSpawnDefinition? spawn = entry.Spawn;
        SpawnSystemConditionsDefinition? conditions = entry.Conditions;
        SpawnSystemModifiersDefinition? modifiers = entry.Modifiers;

        if (spawn?.Name != null)
        {
            data.m_name = spawn.Name;
        }

        data.m_enabled = entry.Enabled;

        if (string.IsNullOrWhiteSpace(entry.Prefab))
        {
            WarnInvalidEntry($"Entry '{context}' is missing required prefab.");
            valid = false;
        }
        else
        {
            string prefabName = entry.Prefab!;
            GameObject? prefab = ResolvePrefab(prefabName);
            if (prefab == null)
            {
                WarnInvalidEntry($"Entry '{context}' references unknown prefab '{prefabName}'.");
                valid = false;
            }
            else
            {
                data.m_prefab = prefab;
                resolvedPrefab = true;
                resolvedPrefabName = prefab.name;
            }
        }

        if (spawn?.Name == null && resolvedPrefab && !string.IsNullOrWhiteSpace(resolvedPrefabName))
        {
            data.m_name = resolvedPrefabName;
        }

        if (conditions?.ResolvedBiomeMask.HasValue == true)
        {
            data.m_biome = conditions.ResolvedBiomeMask.Value;
        }
        else if (conditions?.Biomes != null)
        {
            if (!TryParseBiomes(conditions.Biomes, context, out Heightmap.Biome biomes))
            {
                valid = false;
            }
            else
            {
                data.m_biome = biomes;
            }
        }

        if (conditions?.BiomeAreas != null)
        {
            if (!TryParseBiomeAreas(conditions.BiomeAreas, context, out Heightmap.BiomeArea biomeAreas))
            {
                valid = false;
            }
            else
            {
                data.m_biomeArea = biomeAreas;
            }
        }

        if (conditions?.RequiredGlobalKey != null)
        {
            data.m_requiredGlobalKey = conditions.RequiredGlobalKey;
        }

        if (conditions?.RequiredEnvironments != null)
        {
            data.m_requiredEnvironments = conditions.RequiredEnvironments
                .Select(value => (value ?? "").Trim())
                .Where(value => value.Length > 0)
                .ToList();
        }

        TimeOfDayDefinition? timeOfDay = GetConfiguredTimeOfDay(entry);
        if (timeOfDay != null)
        {
            TimeOfDayFormatting.GetBroadSpawnFlags(timeOfDay, out bool allowDay, out bool allowNight);
            data.m_spawnAtDay = allowDay;
            data.m_spawnAtNight = allowNight;
        }
        if (spawn?.MinLevel.HasValue == true) data.m_minLevel = Math.Max(1, spawn.MinLevel.Value);
        if (spawn?.MaxLevel.HasValue == true)
        {
            data.m_maxLevel = Math.Max(1, spawn.MaxLevel.Value);
        }
        else if (spawn?.MinLevel.HasValue == true)
        {
            data.m_maxLevel = Math.Max(data.m_minLevel, data.m_maxLevel);
        }
        if (spawn?.LevelUpMinCenterDistance.HasValue == true) data.m_levelUpMinCenterDistance = spawn.LevelUpMinCenterDistance.Value;
        if (spawn?.OverrideLevelUpChance.HasValue == true) data.m_overrideLevelupChance = spawn.OverrideLevelUpChance.Value;
        if (conditions?.MaxSpawned.HasValue == true) data.m_maxSpawned = Math.Max(0, conditions.MaxSpawned.Value);
        if (spawn?.SpawnInterval.HasValue == true) data.m_spawnInterval = Math.Max(0.01f, spawn.SpawnInterval.Value);
        if (spawn?.SpawnChance.HasValue == true) data.m_spawnChance = spawn.SpawnChance.Value;
        if (conditions?.NoSpawnRadius.HasValue == true) data.m_spawnDistance = Math.Max(0f, conditions.NoSpawnRadius.Value);
        if (spawn?.SpawnRadiusMin.HasValue == true) data.m_spawnRadiusMin = Math.Max(0f, spawn.SpawnRadiusMin.Value);
        if (spawn?.SpawnRadiusMax.HasValue == true) data.m_spawnRadiusMax = Math.Max(0f, spawn.SpawnRadiusMax.Value);
        if (spawn?.GroupSizeMin.HasValue == true) data.m_groupSizeMin = Math.Max(1, spawn.GroupSizeMin.Value);
        if (spawn?.GroupSizeMax.HasValue == true)
        {
            data.m_groupSizeMax = Math.Max(1, spawn.GroupSizeMax.Value);
        }
        else if (spawn?.GroupSizeMin.HasValue == true)
        {
            data.m_groupSizeMax = Math.Max(data.m_groupSizeMin, data.m_groupSizeMax);
        }
        if (spawn?.GroupRadius.HasValue == true) data.m_groupRadius = Math.Max(0f, spawn.GroupRadius.Value);
        if (conditions?.MinAltitude.HasValue == true) data.m_minAltitude = conditions.MinAltitude.Value;
        if (conditions?.MaxAltitude.HasValue == true) data.m_maxAltitude = conditions.MaxAltitude.Value;
        if (conditions?.MinTilt.HasValue == true) data.m_minTilt = conditions.MinTilt.Value;
        if (conditions?.MaxTilt.HasValue == true) data.m_maxTilt = conditions.MaxTilt.Value;
        ApplyExclusiveZoneToggle(conditions?.InForest, ref data.m_inForest, ref data.m_outsideForest);
        ApplyExclusiveZoneToggle(conditions?.InLava, ref data.m_inLava, ref data.m_outsideLava);
        if (conditions?.CanSpawnCloseToPlayer.HasValue == true) data.m_canSpawnCloseToPlayer = conditions.CanSpawnCloseToPlayer.Value;
        if (conditions?.InsidePlayerBase.HasValue == true) data.m_insidePlayerBase = conditions.InsidePlayerBase.Value;
        if (conditions?.MinOceanDepth.HasValue == true) data.m_minOceanDepth = conditions.MinOceanDepth.Value;
        if (conditions?.MaxOceanDepth.HasValue == true) data.m_maxOceanDepth = conditions.MaxOceanDepth.Value;
        if (spawn?.HuntPlayer.HasValue == true) data.m_huntPlayer = spawn.HuntPlayer.Value;
        if (spawn?.GroundOffset.HasValue == true) data.m_groundOffset = spawn.GroundOffset.Value;
        if (spawn?.GroundOffsetRandom.HasValue == true) data.m_groundOffsetRandom = spawn.GroundOffsetRandom.Value;
        if (conditions?.MinDistanceFromCenter.HasValue == true) data.m_minDistanceFromCenter = conditions.MinDistanceFromCenter.Value;
        if (conditions?.MaxDistanceFromCenter.HasValue == true) data.m_maxDistanceFromCenter = conditions.MaxDistanceFromCenter.Value;

        if (valid && applyCustomData)
        {
            SpawnSystemCustomDataSupport.ApplyCustomData(data, entry, context);
        }

        return valid;
    }

    private static void ApplyRuntimeMetadata(SpawnSystem.SpawnData spawnData, TimeOfDayDefinition? timeOfDay)
    {
        if (spawnData == null)
        {
            return;
        }

        InvalidateRuntimeTimeOfDayPhaseMarker();

        if (timeOfDay?.HasValues() == true)
        {
            TimeOfDayBySpawnData[spawnData] = timeOfDay;
        }
        else
        {
            TimeOfDayBySpawnData.Remove(spawnData);
        }

        _hasRuntimeTimeOfDayOverrides = TimeOfDayBySpawnData.Count > 0;
    }

    private static void ClearRuntimeMetadata(SpawnSystem system)
    {
        if (system == null)
        {
            return;
        }

        InvalidateRuntimeTimeOfDayPhaseMarker();

        foreach (SpawnSystemList spawnList in system.m_spawnLists)
        {
            foreach (SpawnSystem.SpawnData spawnData in spawnList.m_spawners)
            {
                TimeOfDayBySpawnData.Remove(spawnData);
            }
        }

        _hasRuntimeTimeOfDayOverrides = TimeOfDayBySpawnData.Count > 0;
    }

    private static bool IsPreparedEntriesCacheValid()
    {
        return _preparedEntriesCache != null;
    }

    private static int ComputeGameDataSignature()
    {
        int frame = Time.frameCount;
        if (_cachedGameDataSignatureFrame == frame)
        {
            return _cachedGameDataSignatureValue;
        }

        int signature = ComputeGameDataSignatureCore();
        _cachedGameDataSignatureFrame = frame;
        _cachedGameDataSignatureValue = signature;
        return signature;
    }

    private static int ComputeGameDataSignatureCore()
    {
        if (ZNetScene.instance == null || ObjectDB.instance == null)
        {
            return 0;
        }

        List<SpawnSystem> systems = new();
        SpawnSystem? zoneCtrlSpawnSystem = GetZoneCtrlPrefabSpawnSystem();
        if (zoneCtrlSpawnSystem != null)
        {
            systems.Add(zoneCtrlSpawnSystem);
        }
        else
        {
            systems = GetLiveSystems();
        }

        if (systems.Count == 0)
        {
            return 0;
        }

        unchecked
        {
            int hash = 17;
            hash = hash * 31 + ZNetScene.instance.GetInstanceID();
            hash = hash * 31 + ObjectDB.instance.GetInstanceID();
            hash = HashConfiguredSpawnSystemResolutionKeys(hash);
            foreach (SpawnSystem system in systems)
            {
                hash = hash * 31 + system.GetInstanceID();
                hash = hash * 31 + system.m_spawnLists.Count;
                foreach (SpawnSystemList spawnList in system.m_spawnLists)
                {
                    hash = hash * 31 + spawnList.m_spawners.Count;
                    foreach (SpawnSystem.SpawnData spawnData in spawnList.m_spawners)
                    {
                        hash = hash * 31 + (spawnData?.m_prefab != null ? spawnData.m_prefab.GetInstanceID() : 0);
                    }
                }
            }

            return hash;
        }
    }

    private static int HashConfiguredSpawnSystemResolutionKeys(int hash)
    {
        EnsureConfiguredSpawnSystemResolutionKeysCached();
        unchecked
        {
            foreach (string key in _cachedConfiguredSpawnSystemResolutionKeys)
            {
                hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(key);
            }
        }

        return hash;
    }

    private static void EnsureConfiguredSpawnSystemResolutionKeysCached()
    {
        if (ZNetScene.instance == null)
        {
            _cachedConfiguredSpawnSystemResolutionKeys.Clear();
            _cachedConfiguredSpawnSystemResolutionKeysSignature = "";
            _cachedConfiguredSpawnSystemResolutionKeysSceneStamp = int.MinValue;
            return;
        }

        int sceneStamp = ComputeConfiguredSpawnSystemResolutionKeysSceneStamp();
        if (_cachedConfiguredSpawnSystemResolutionKeysSceneStamp == sceneStamp &&
            string.Equals(_cachedConfiguredSpawnSystemResolutionKeysSignature, _configurationSignature, StringComparison.Ordinal))
        {
            return;
        }

        _cachedConfiguredSpawnSystemResolutionKeys.Clear();
        _cachedConfiguredSpawnSystemResolutionKeys.AddRange(BuildConfiguredSpawnSystemResolutionKeys());
        _cachedConfiguredSpawnSystemResolutionKeys.Sort(StringComparer.OrdinalIgnoreCase);
        _cachedConfiguredSpawnSystemResolutionKeysSignature = _configurationSignature;
        _cachedConfiguredSpawnSystemResolutionKeysSceneStamp = sceneStamp;
    }

    private static int ComputeConfiguredSpawnSystemResolutionKeysSceneStamp()
    {
        if (ZNetScene.instance == null)
        {
            return int.MinValue;
        }

        unchecked
        {
            int hash = 17;
            hash = hash * 31 + ZNetScene.instance.GetInstanceID();
            hash = hash * 31 + ZNetScene.instance.m_prefabs.Count;
            hash = hash * 31 + ZNetScene.instance.m_nonNetViewPrefabs.Count;
            return hash;
        }
    }

    private static int HashNormalizedKeys(int hash, IEnumerable<string?> keys)
    {
        unchecked
        {
            foreach (string key in (keys ?? Enumerable.Empty<string?>())
                         .Select(ReferenceRefreshSupport.NormalizeKey)
                         .Where(key => key.Length > 0)
                         .OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
            {
                hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(key);
            }
        }

        return hash;
    }

    private static IEnumerable<string> BuildConfiguredSpawnSystemResolutionKeys()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (CanonicalSpawnSystemEntry entry in _configuration)
        {
            string prefabName = ReferenceRefreshSupport.NormalizeKey(entry.Prefab);
            if (prefabName.Length == 0)
            {
                continue;
            }

            int resolvedPrefabId = ResolvePrefab(prefabName)?.GetInstanceID() ?? 0;
            string key = $"{prefabName}:{resolvedPrefabId.ToString(CultureInfo.InvariantCulture)}";
            if (seen.Add(key))
            {
                yield return key;
            }
        }
    }

    private static bool IsOverrideConfigurationFileName(string fileName)
    {
        return DomainConfigurationFileSupport.IsOverrideConfigurationFileName("spawnsystem", fileName);
    }

    private static bool TryEnumerateReferenceLiveSpawnData(
        IEnumerable<SpawnSystemList>? lists,
        out IEnumerable<SpawnSystem.SpawnData>? spawnData)
    {
        List<SpawnSystem.SpawnData> entries = lists?
            .Where(spawnList => spawnList != null)
            .SelectMany(spawnList => spawnList.m_spawners ?? new List<SpawnSystem.SpawnData>())
            .Where(current => current != null)
            .ToList() ?? new List<SpawnSystem.SpawnData>();

        if (entries.Count == 0)
        {
            spawnData = null;
            return false;
        }

        spawnData = entries;
        return true;
    }

    private static bool TryParseBiomes(List<string> names, string context, out Heightmap.Biome biomes)
    {
        biomes = Heightmap.Biome.None;
        foreach (string rawName in names)
        {
            string name = (rawName ?? "").Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (!TryResolveBiomeToken(name, out Heightmap.Biome parsedBiome))
            {
                WarnInvalidEntry($"Entry '{context}' contains unknown biome '{name}'.");
                biomes = Heightmap.Biome.None;
                return false;
            }

            if (parsedBiome == Heightmap.Biome.All)
            {
                biomes = Heightmap.Biome.All;
                return true;
            }

            biomes |= parsedBiome;
        }

        return true;
    }

    private static bool TryResolveBiomeToken(string name, out Heightmap.Biome biome)
    {
        if (BiomeResolutionSupport.TryResolveBiomeToken(name, out biome))
        {
            return true;
        }

        biome = Heightmap.Biome.None;
        return false;
    }

    private static bool TryParseBiomeAreas(List<string> names, string context, out Heightmap.BiomeArea biomeAreas)
    {
        biomeAreas = 0;
        foreach (string rawName in names)
        {
            string name = (rawName ?? "").Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (!Enum.TryParse(name, true, out Heightmap.BiomeArea parsedBiomeArea))
            {
                WarnInvalidEntry($"Entry '{context}' contains unknown biomeArea '{name}'.");
                biomeAreas = 0;
                return false;
            }

            if (parsedBiomeArea == Heightmap.BiomeArea.Everything)
            {
                biomeAreas = Heightmap.BiomeArea.Everything;
                return true;
            }

            biomeAreas |= parsedBiomeArea;
        }

        return true;
    }

    private static List<string> ConvertBiomes(Heightmap.Biome biomes)
    {
        List<string> values = new();
        int remainingMask = (int)biomes;
        foreach (Heightmap.Biome biome in Enum.GetValues(typeof(Heightmap.Biome)))
        {
            if (biome == Heightmap.Biome.None || biome == Heightmap.Biome.All)
            {
                continue;
            }

            if ((biomes & biome) == biome)
            {
                values.Add(biome.ToString());
                remainingMask &= ~(int)biome;
            }
        }

        if (remainingMask != 0)
        {
            values.Add(remainingMask.ToString(CultureInfo.InvariantCulture));
        }

        return values;
    }

    private static List<string> ConvertBiomeAreas(Heightmap.BiomeArea biomeAreas)
    {
        if (biomeAreas == Heightmap.BiomeArea.Everything)
        {
            return new List<string> { Heightmap.BiomeArea.Everything.ToString() };
        }

        List<string> values = new();
        foreach (Heightmap.BiomeArea biomeArea in Enum.GetValues(typeof(Heightmap.BiomeArea)))
        {
            if (biomeArea == Heightmap.BiomeArea.Everything)
            {
                continue;
            }

            if ((biomeAreas & biomeArea) == biomeArea)
            {
                values.Add(biomeArea.ToString());
            }
        }

        return values;
    }

    private static GameObject? ResolvePrefab(string prefabName)
    {
        if (ZNetScene.instance == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        GameObject? prefab = ZNetScene.instance.GetPrefab(prefabName);
        if (prefab != null)
        {
            return prefab;
        }

        return ZNetScene.instance.m_prefabs
            .Concat(ZNetScene.instance.m_nonNetViewPrefabs)
            .FirstOrDefault(candidate => string.Equals(candidate.name, prefabName, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateConfigurationContext(int index, SpawnSystemConfigurationEntry entry)
    {
        string ordinal = $"entry[{index.ToString(CultureInfo.InvariantCulture)}]";
        string prefab = entry.Prefab ?? "(no prefab)";
        string name = entry.SpawnSystem?.Name ?? "(no name)";
        string context = $"{ordinal} {prefab} / {name}";
        if (string.IsNullOrWhiteSpace(entry.SourcePath))
        {
            return context;
        }

        string location = Path.GetFileName(entry.SourcePath);
        if (entry.SourceLine.HasValue)
        {
            location = $"{location}:{entry.SourceLine.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return $"{context} @ {location}";
    }

    private static string FormatYamlExceptionLocation(Exception ex)
    {
        if (ex is not YamlException yamlException)
        {
            return "";
        }

        Mark mark = yamlException.Start;
        if (mark.Line <= 0)
        {
            return "";
        }

        return $" at line {mark.Line.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string? NormalizeOptionalString(string? value)
    {
        return value == null ? null : value.Trim();
    }

    private static void NormalizeSpawnSystemConditions(SpawnSystemConditionsDefinition? conditions, string context)
    {
        if (conditions == null)
        {
            return;
        }

        if (conditions.Tilt?.HasValues() == true)
        {
            conditions.MinTilt = RangeFormatting.GetMin(conditions.Tilt, conditions.MinTilt);
            conditions.MaxTilt = RangeFormatting.GetMax(conditions.Tilt, conditions.MinTilt, conditions.MaxTilt);
            float? minTilt = conditions.MinTilt;
            float? maxTilt = conditions.MaxTilt;
            NormalizeSpawnSystemFloatRange(ref minTilt, ref maxTilt, context, "tilt");
            conditions.MinTilt = minTilt;
            conditions.MaxTilt = maxTilt;
        }

        if (conditions.Altitude?.HasValues() == true)
        {
            conditions.MinAltitude = RangeFormatting.GetMin(conditions.Altitude, conditions.MinAltitude);
            conditions.MaxAltitude = RangeFormatting.GetMax(conditions.Altitude, conditions.MinAltitude, conditions.MaxAltitude);
            float? minAltitude = conditions.MinAltitude;
            float? maxAltitude = conditions.MaxAltitude;
            NormalizeSpawnSystemFloatRange(ref minAltitude, ref maxAltitude, context, "altitude");
            conditions.MinAltitude = minAltitude;
            conditions.MaxAltitude = maxAltitude;
        }

        if (conditions.OceanDepth?.HasValues() == true)
        {
            conditions.MinOceanDepth = RangeFormatting.GetMin(conditions.OceanDepth, conditions.MinOceanDepth);
            conditions.MaxOceanDepth = RangeFormatting.GetMax(conditions.OceanDepth, conditions.MinOceanDepth, conditions.MaxOceanDepth);
            float? minOceanDepth = conditions.MinOceanDepth;
            float? maxOceanDepth = conditions.MaxOceanDepth;
            NormalizeSpawnSystemFloatRange(ref minOceanDepth, ref maxOceanDepth, context, "oceanDepth");
            conditions.MinOceanDepth = minOceanDepth;
            conditions.MaxOceanDepth = maxOceanDepth;
        }

        if (conditions.DistanceFromCenter?.HasValues() == true)
        {
            conditions.MinDistanceFromCenter = RangeFormatting.GetMin(conditions.DistanceFromCenter, conditions.MinDistanceFromCenter);
            conditions.MaxDistanceFromCenter = RangeFormatting.GetMax(conditions.DistanceFromCenter, conditions.MinDistanceFromCenter, conditions.MaxDistanceFromCenter);
            float? minDistanceFromCenter = conditions.MinDistanceFromCenter;
            float? maxDistanceFromCenter = conditions.MaxDistanceFromCenter;
            NormalizeSpawnSystemFloatRange(ref minDistanceFromCenter, ref maxDistanceFromCenter, context, "distanceFromCenter");
            conditions.MinDistanceFromCenter = minDistanceFromCenter;
            conditions.MaxDistanceFromCenter = maxDistanceFromCenter;
        }

        conditions.Biomes = NormalizeOptionalStringList(conditions.Biomes);
        conditions.BiomeAreas = NormalizeOptionalStringList(conditions.BiomeAreas);
        conditions.RequiredGlobalKey = NormalizeOptionalString(conditions.RequiredGlobalKey);
        conditions.RequiredEnvironments = NormalizeOptionalStringList(conditions.RequiredEnvironments);
        conditions.TimeOfDay?.Normalize();
    }

    private static void NormalizeSpawnSystemSpawn(SpawnSystemSpawnDefinition? spawn, string context)
    {
        if (spawn == null)
        {
            return;
        }

        spawn.Name = NormalizeOptionalString(spawn.Name);

        if (spawn.Level?.HasValues() == true)
        {
            spawn.MinLevel = RangeFormatting.GetMin(spawn.Level, spawn.MinLevel);
            spawn.MaxLevel = RangeFormatting.GetMax(spawn.Level, spawn.MinLevel, spawn.MaxLevel);
            int? minLevel = spawn.MinLevel;
            int? maxLevel = spawn.MaxLevel;
            NormalizeSpawnSystemIntRange(ref minLevel, ref maxLevel, context, "level");
            spawn.MinLevel = minLevel;
            spawn.MaxLevel = maxLevel;
        }

        if (spawn.SpawnRadius?.HasValues() == true)
        {
            spawn.SpawnRadiusMin = RangeFormatting.GetMin(spawn.SpawnRadius, spawn.SpawnRadiusMin);
            spawn.SpawnRadiusMax = RangeFormatting.GetMax(spawn.SpawnRadius, spawn.SpawnRadiusMin, spawn.SpawnRadiusMax);
            float? minSpawnRadius = spawn.SpawnRadiusMin;
            float? maxSpawnRadius = spawn.SpawnRadiusMax;
            NormalizeSpawnSystemFloatRange(ref minSpawnRadius, ref maxSpawnRadius, context, "spawnRadius");
            spawn.SpawnRadiusMin = minSpawnRadius;
            spawn.SpawnRadiusMax = maxSpawnRadius;
        }

        if (spawn.GroupSize?.HasValues() == true)
        {
            spawn.GroupSizeMin = RangeFormatting.GetMin(spawn.GroupSize, spawn.GroupSizeMin);
            spawn.GroupSizeMax = RangeFormatting.GetMax(spawn.GroupSize, spawn.GroupSizeMin, spawn.GroupSizeMax);
            int? minGroupSize = spawn.GroupSizeMin;
            int? maxGroupSize = spawn.GroupSizeMax;
            NormalizeSpawnSystemIntRange(ref minGroupSize, ref maxGroupSize, context, "groupSize");
            spawn.GroupSizeMin = minGroupSize;
            spawn.GroupSizeMax = maxGroupSize;
        }
    }

    private static void NormalizeSpawnSystemModifiers(SpawnSystemModifiersDefinition? modifiers)
    {
        if (modifiers == null)
        {
            return;
        }

        modifiers.Objects = NormalizeOptionalStringList(modifiers.Objects);
        modifiers.Fields = NormalizeOptionalStringDictionary(modifiers.Fields);
        modifiers.Data = NormalizeOptionalString(modifiers.Data);
        modifiers.Faction = NormalizeOptionalString(modifiers.Faction);
    }

    private static List<string>? NormalizeOptionalStringList(List<string>? values)
    {
        if (values == null)
        {
            return null;
        }

        List<string> normalized = values
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .ToList();

        return normalized.Count == 0 ? null : normalized;
    }

    private static Dictionary<string, string>? NormalizeOptionalStringDictionary(Dictionary<string, string>? values)
    {
        if (values == null)
        {
            return null;
        }

        Dictionary<string, string> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string rawKey, string rawValue) in values)
        {
            string key = (rawKey ?? "").Trim();
            if (key.Length == 0)
            {
                continue;
            }

            normalized[key] = (rawValue ?? "").Trim();
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static TimeOfDayDefinition? GetConfiguredTimeOfDay(SpawnSystemConfigurationEntry? entry)
    {
        return entry?.Conditions?.TimeOfDay;
    }

    private static void NormalizeSpawnSystemIntRange(ref int? min, ref int? max, string context, string fieldName)
    {
        int? originalMin = min;
        int? originalMax = max;
        if (!RangeFormatting.NormalizeAscending(ref min, ref max))
        {
            return;
        }

        WarnInvalidEntry(
            $"Entry '{context}' contains reversed {fieldName} range '{RangeFormatting.FormatShorthand(RangeFormatting.From(originalMin, originalMax))}'. Normalized to '{RangeFormatting.FormatShorthand(RangeFormatting.From(min, max))}'.");
    }

    private static void NormalizeSpawnSystemFloatRange(ref float? min, ref float? max, string context, string fieldName)
    {
        float? originalMin = min;
        float? originalMax = max;
        if (!RangeFormatting.NormalizeAscending(ref min, ref max))
        {
            return;
        }

        WarnInvalidEntry(
            $"Entry '{context}' contains reversed {fieldName} range '{RangeFormatting.FormatShorthand(RangeFormatting.From(originalMin, originalMax))}'. Normalized to '{RangeFormatting.FormatShorthand(RangeFormatting.From(min, max))}'.");
    }

    private static string? NormalizeNullable(string? value)
    {
        string trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? NormalizeReferencePrefabName(GameObject? prefab)
    {
        return prefab == null ? null : prefab.name;
    }

    internal static void EnterRequiredGlobalKeyEvaluation()
    {
        _requiredGlobalKeyEvaluationDepth++;
    }

    internal static void ExitRequiredGlobalKeyEvaluation()
    {
        if (_requiredGlobalKeyEvaluationDepth > 0)
        {
            _requiredGlobalKeyEvaluationDepth--;
        }
    }

    internal static bool TryEvaluateExtendedRequiredGlobalKey(ZoneSystem? zoneSystem, string? rawValue, out bool result)
    {
        result = false;
        if (_requiredGlobalKeyEvaluationDepth <= 0 || zoneSystem == null || !TryParseRequiredGlobalKeyThreshold(rawValue, out string key, out int requiredValue))
        {
            return false;
        }

        if (!zoneSystem.GetGlobalKey(key, out string currentValue) ||
            !int.TryParse(currentValue, NumberStyles.Any, CultureInfo.InvariantCulture, out int currentNumericValue))
        {
            result = false;
            return true;
        }

        result = currentNumericValue >= requiredValue;
        return true;
    }

    internal static bool TryRewriteExtendedGlobalKeyMutation(ZoneSystem? zoneSystem, string? rawValue, out string rewrittenValue)
    {
        rewrittenValue = rawValue ?? "";
        if (zoneSystem == null || !TryParseGlobalKeyMutation(rawValue, out string key, out int delta))
        {
            return false;
        }

        if (zoneSystem.GetGlobalKey(key, out string previousValue) &&
            int.TryParse(previousValue, NumberStyles.Any, CultureInfo.InvariantCulture, out int previousNumericValue))
        {
            rewrittenValue = $"{key} {(previousNumericValue + delta).ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            rewrittenValue = $"{key} {delta.ToString(CultureInfo.InvariantCulture)}";
        }

        return true;
    }

    internal static void ConsumeExtendedRequiredGlobalKeyAfterSpawn(SpawnSystem.SpawnData? critter)
    {
        if (SpawnSystem.m_nospawn || critter == null || ZoneSystem.instance == null)
        {
            return;
        }

        if (!TryParseRequiredGlobalKeyThreshold(critter.m_requiredGlobalKey, out string key, out int amount) || amount <= 0)
        {
            return;
        }

        ZoneSystem.instance.SetGlobalKey($"{key} --{amount.ToString(CultureInfo.InvariantCulture)}");
    }

    private static bool TryParseRequiredGlobalKeyThreshold(string? rawValue, out string key, out int amount)
    {
        key = "";
        amount = 0;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        string normalizedValue = rawValue!.Trim();
        key = ZoneSystem.GetKeyValue(normalizedValue, out string value, out _);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.StartsWith("++", StringComparison.OrdinalIgnoreCase) || value.StartsWith("--", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out amount);
    }

    private static bool TryParseGlobalKeyMutation(string? rawValue, out string key, out int delta)
    {
        key = "";
        delta = 0;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        string normalizedValue = rawValue!.Trim();
        key = ZoneSystem.GetKeyValue(normalizedValue, out string value, out _);
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.StartsWith("++", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[2..], NumberStyles.Any, CultureInfo.InvariantCulture, out int increment))
        {
            delta = increment;
            return true;
        }

        if (value.StartsWith("--", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value[2..], NumberStyles.Any, CultureInfo.InvariantCulture, out int decrement))
        {
            delta = -decrement;
            return true;
        }

        return false;
    }

    private static void WarnInvalidEntry(string message)
    {
        if (_invalidEntryWarningSuppressionDepth > 0 || ShouldSuppressServerSourcedInvalidEntryWarning(message))
        {
            return;
        }

        if (_capturedStrictValidationWarnings != null)
        {
            _capturedStrictValidationWarnings.Add(message);
            return;
        }

        if (InvalidEntryWarnings.Add(message))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(message);
        }
    }

    private static InvalidEntryWarningSuppressionScope BeginInvalidEntryWarningSuppressionForSyncedClientBuild(string sourceName)
    {
        return !DropNSpawnPlugin.IsSourceOfTruth && sourceName.StartsWith("ServerSync:", StringComparison.Ordinal)
            ? new InvalidEntryWarningSuppressionScope(active: true)
            : default;
    }

    private static bool ShouldSuppressServerSourcedInvalidEntryWarning(string message)
    {
        return !DropNSpawnPlugin.IsSourceOfTruth &&
               message.IndexOf("ServerSync:", StringComparison.Ordinal) >= 0;
    }

    private static bool HasAnyConditionFields(SpawnSystemConditionsDefinition? conditions)
    {
        return conditions != null &&
               (conditions.NoSpawnRadius.HasValue ||
                conditions.MaxSpawned.HasValue ||
                (conditions.Biomes?.Count ?? 0) > 0 ||
                (conditions.BiomeAreas?.Count ?? 0) > 0 ||
                !string.IsNullOrWhiteSpace(conditions.RequiredGlobalKey) ||
                (conditions.RequiredEnvironments?.Count ?? 0) > 0 ||
                conditions.TimeOfDay != null ||
                GetAltitudeRange(conditions)?.HasValues() == true ||
                GetTiltRange(conditions)?.HasValues() == true ||
                conditions.InForest.HasValue ||
                conditions.InLava.HasValue ||
                conditions.CanSpawnCloseToPlayer.HasValue ||
                conditions.InsidePlayerBase.HasValue ||
                GetOceanDepthRange(conditions)?.HasValues() == true ||
                GetDistanceFromCenterRange(conditions)?.HasValues() == true);
    }

    private static bool HasAnySpawnFields(SpawnSystemSpawnDefinition? spawn)
    {
        return spawn != null &&
               (!string.IsNullOrWhiteSpace(spawn.Name) ||
                spawn.HuntPlayer.HasValue ||
                GetLevelRange(spawn)?.HasValues() == true ||
                spawn.OverrideLevelUpChance.HasValue ||
                spawn.LevelUpMinCenterDistance.HasValue ||
                spawn.GroundOffset.HasValue ||
                spawn.GroundOffsetRandom.HasValue ||
                spawn.SpawnInterval.HasValue ||
                spawn.SpawnChance.HasValue ||
                GetSpawnRadiusRange(spawn)?.HasValues() == true ||
                GetGroupSizeRange(spawn)?.HasValues() == true ||
                spawn.GroupRadius.HasValue);
    }

    private static bool HasAnyModifierFields(SpawnSystemModifiersDefinition? modifiers)
    {
        return modifiers != null &&
               (!string.IsNullOrWhiteSpace(modifiers.Data) ||
                !string.IsNullOrWhiteSpace(modifiers.Faction) ||
                (modifiers.Fields?.Count ?? 0) > 0 ||
                (modifiers.Objects?.Count ?? 0) > 0);
    }

    private static IntRangeDefinition? GetLevelRange(SpawnSystemConfigurationEntry entry)
    {
        return GetLevelRange(entry.SpawnSystem);
    }

    private static IntRangeDefinition? GetLevelRange(SpawnSystemSpawnDefinition? spawn)
    {
        return spawn?.Level ?? RangeFormatting.From(spawn?.MinLevel, spawn?.MaxLevel ?? spawn?.MinLevel);
    }

    private static FloatRangeDefinition? GetSpawnRadiusRange(SpawnSystemConfigurationEntry entry)
    {
        return GetSpawnRadiusRange(entry.SpawnSystem);
    }

    private static FloatRangeDefinition? GetSpawnRadiusRange(SpawnSystemSpawnDefinition? spawn)
    {
        return spawn?.SpawnRadius ?? RangeFormatting.From(spawn?.SpawnRadiusMin, spawn?.SpawnRadiusMax);
    }

    private static IntRangeDefinition? GetGroupSizeRange(SpawnSystemConfigurationEntry entry)
    {
        return GetGroupSizeRange(entry.SpawnSystem);
    }

    private static IntRangeDefinition? GetGroupSizeRange(SpawnSystemSpawnDefinition? spawn)
    {
        return spawn?.GroupSize ?? RangeFormatting.From(spawn?.GroupSizeMin, spawn?.GroupSizeMax ?? spawn?.GroupSizeMin);
    }

    private static FloatRangeDefinition? GetAltitudeRange(SpawnSystemConfigurationEntry entry)
    {
        return GetAltitudeRange(entry.Conditions);
    }

    private static FloatRangeDefinition? GetAltitudeRange(SpawnSystemConditionsDefinition? conditions)
    {
        return conditions?.Altitude ?? RangeFormatting.From(conditions?.MinAltitude, conditions?.MaxAltitude);
    }

    private static FloatRangeDefinition? GetTiltRange(SpawnSystemConfigurationEntry entry)
    {
        return GetTiltRange(entry.Conditions);
    }

    private static FloatRangeDefinition? GetTiltRange(SpawnSystemConditionsDefinition? conditions)
    {
        return conditions?.Tilt ?? RangeFormatting.From(conditions?.MinTilt, conditions?.MaxTilt);
    }

    private static FloatRangeDefinition? GetOceanDepthRange(SpawnSystemConfigurationEntry entry)
    {
        return GetOceanDepthRange(entry.Conditions);
    }

    private static FloatRangeDefinition? GetOceanDepthRange(SpawnSystemConditionsDefinition? conditions)
    {
        return conditions?.OceanDepth ?? RangeFormatting.From(conditions?.MinOceanDepth, conditions?.MaxOceanDepth);
    }

    private static FloatRangeDefinition? GetDistanceFromCenterRange(SpawnSystemConfigurationEntry entry)
    {
        return GetDistanceFromCenterRange(entry.Conditions);
    }

    private static FloatRangeDefinition? GetDistanceFromCenterRange(SpawnSystemConditionsDefinition? conditions)
    {
        return conditions?.DistanceFromCenter ?? RangeFormatting.From(conditions?.MinDistanceFromCenter, conditions?.MaxDistanceFromCenter);
    }

}
