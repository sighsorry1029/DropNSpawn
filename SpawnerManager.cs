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

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private const string ReferenceAutoUpdateStateKey = "spawner";
    private const string LocationReferenceAutoUpdateStateKey = "spawner.locations";
    private const string UnresolvedSelectorLocationCacheKey = "<unresolved>";
    private const float RuntimeEvaluationIntervalSeconds = 0.25f;
    private const float RuntimeEvaluationIntervalInsidePlayerBaseOnlySeconds = 0.5f;
    internal static readonly DomainModuleDefinition<SpawnerConfigurationEntry> Module =
        new(
            "spawner",
            DropNSpawnPlugin.ReloadDomain.Spawner,
            "spawner_yaml",
            98,
            ShouldReloadForPath,
            ReloadConfiguration,
            Initialize,
            OnGameDataReady,
            HandleExpandWorldDataReady,
            dtoVersion: 6,
            transportProfile: DomainTransportProfile.MediumConfig,
            displayName: "spawner",
            cacheDirectoryName: "spawner",
            clientRequestPriority: 30,
            keySelector: entry => entry.RuleId,
            applyPayloadAction: ApplySyncedPayload,
            workKinds: DomainWorkKinds.Runtime | DomainWorkKinds.Reconcile,
            hasPendingReconcileWork: HasPendingReconcileWork,
            processPendingReconcileStep: ProcessQueuedReconcileStep,
            beforeClientManifestChanged: MarkSyncedPayloadPending,
            onClientAuthorityCutover: EnterPendingSyncedPayloadState);
    internal static DomainDescriptor<SpawnerConfigurationEntry> Descriptor => Module.DescriptorTyped;
    internal static DomainTransportMetadata<SpawnerConfigurationEntry> TransportMetadata => Module.TransportMetadataTyped;
    private static int _invalidEntryWarningSuppressionDepth;

    private sealed class ParsedSpawnerConfigurationDocument
    {
        public List<SpawnerConfigurationEntry> Configuration { get; } = new();
        public List<string> Warnings { get; } = new();
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

    private readonly struct PendingSpawnAreaReconcile
    {
        public PendingSpawnAreaReconcile(SpawnArea spawnArea, int instanceId, int epoch)
        {
            SpawnArea = spawnArea;
            InstanceId = instanceId;
            Epoch = epoch;
        }

        public SpawnArea SpawnArea { get; }
        public int InstanceId { get; }
        public int Epoch { get; }
    }

    private readonly struct PendingCreatureSpawnerReconcile
    {
        public PendingCreatureSpawnerReconcile(CreatureSpawner creatureSpawner, int instanceId, int epoch)
        {
            CreatureSpawner = creatureSpawner;
            InstanceId = instanceId;
            Epoch = epoch;
        }

        public CreatureSpawner CreatureSpawner { get; }
        public int InstanceId { get; }
        public int Epoch { get; }
    }

    private sealed class MatchingEntryCache
    {
        private readonly List<SpawnerRuntimeEntry> _entries = new();
        private readonly List<SpawnerRuntimeEntry> _runtimeEntries = new();
        private readonly List<string> _runtimeRequiredGlobalKeys = new();
        private readonly List<string> _runtimeForbiddenGlobalKeys = new();

        public string ConfigPrefabName { get; set; } = "";
        public string ResolvedLocationKey { get; set; } = "";
        public bool UsesLocationSelector { get; set; }
        public bool HasRecordedLocationProvenanceEpoch { get; set; }
        public int RecordedLocationProvenanceEpoch { get; set; }
        public SharedMatchingEntryTemplate? SharedTemplate { get; private set; }
        public IReadOnlyList<SpawnerRuntimeEntry> Entries => SharedTemplate?.Entries ?? _entries;
        public IReadOnlyList<SpawnerRuntimeEntry> RuntimeEntries => SharedTemplate?.RuntimeEntries ?? _runtimeEntries;
        public Dictionary<int, SpawnerRuntimeEntry?> WinningEntriesByRuntimeSignature { get; } = new();
        public IReadOnlyList<string> RuntimeRequiredGlobalKeys => SharedTemplate?.RuntimeRequiredGlobalKeys ?? _runtimeRequiredGlobalKeys;
        public IReadOnlyList<string> RuntimeForbiddenGlobalKeys => SharedTemplate?.RuntimeForbiddenGlobalKeys ?? _runtimeForbiddenGlobalKeys;
        public bool UsesTimeOfDay { get; set; }
        public bool UsesRequiredEnvironments { get; set; }
        public bool UsesInsidePlayerBase { get; set; }
        internal List<SpawnerRuntimeEntry> MutableEntries => _entries;
        internal List<SpawnerRuntimeEntry> MutableRuntimeEntries => _runtimeEntries;
        internal List<string> MutableRuntimeRequiredGlobalKeys => _runtimeRequiredGlobalKeys;
        internal List<string> MutableRuntimeForbiddenGlobalKeys => _runtimeForbiddenGlobalKeys;

        public void UseSharedTemplate(SharedMatchingEntryTemplate template)
        {
            SharedTemplate = template;
            ConfigPrefabName = template.ConfigPrefabName;
            ResolvedLocationKey = template.ResolvedLocationKey;
            UsesLocationSelector = template.UsesLocationSelector;
            UsesTimeOfDay = template.UsesTimeOfDay;
            UsesRequiredEnvironments = template.UsesRequiredEnvironments;
            UsesInsidePlayerBase = template.UsesInsidePlayerBase;
            _entries.Clear();
            _runtimeEntries.Clear();
            _runtimeRequiredGlobalKeys.Clear();
            _runtimeForbiddenGlobalKeys.Clear();
        }
    }

    private sealed class SharedMatchingEntryTemplate
    {
        public string ConfigPrefabName { get; set; } = "";
        public string ResolvedLocationKey { get; set; } = "";
        public bool UsesLocationSelector { get; set; }
        public List<SpawnerRuntimeEntry> Entries { get; } = new();
        public List<SpawnerRuntimeEntry> RuntimeEntries { get; } = new();
        public List<string> RuntimeRequiredGlobalKeys { get; } = new();
        public List<string> RuntimeForbiddenGlobalKeys { get; } = new();
        public bool UsesTimeOfDay { get; set; }
        public bool UsesRequiredEnvironments { get; set; }
        public bool UsesInsidePlayerBase { get; set; }
    }

    private sealed class RuntimeContextSnapshot
    {
        public int Frame { get; set; }
        public int TimeOfDayPhaseMarker { get; set; }
        public string EnvironmentName { get; set; } = "";
        public Dictionary<string, bool> GlobalKeyStates { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LocalRuntimeState
    {
        public float LastInsidePlayerBaseSampleTime { get; set; } = float.NegativeInfinity;
        public float NextRuntimeEvaluationTime { get; set; } = float.NegativeInfinity;
        public bool IsInsidePlayerBase { get; set; }
        public int LastObservedTimeOfDayPhaseMarker { get; set; } = int.MinValue;
        public string LastObservedEnvironmentName { get; set; } = "";
        public bool HasAppliedWinningEntrySelection { get; set; }
        public string LastAppliedConfigPrefabName { get; set; } = "";
        public string LastAppliedResolvedLocationKey { get; set; } = "";
        public string LastAppliedWinningEntryRuleId { get; set; } = "";
    }

    private sealed class SpawnAreaResolvedSpawnEntry
    {
        public SpawnArea.SpawnData SpawnData { get; set; } = null!;
        public SpawnAreaSpawnDefinition Definition { get; set; } = null!;
        public ExpandWorldSpawnDataPayload? DataPayload { get; set; }
    }

    private sealed class SpawnAreaSpawnSnapshot
    {
        public GameObject? Prefab { get; set; }
        public float Weight { get; set; }
        public int MinLevel { get; set; }
        public int MaxLevel { get; set; }
    }

    private sealed class SpawnAreaComponentSnapshot
    {
        public SpawnArea Component { get; set; } = null!;
        public string ConfigPrefabName { get; set; } = "";
        public string RootPrefabName { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public float LevelUpChance { get; set; }
        public float SpawnInterval { get; set; }
        public float TriggerDistance { get; set; }
        public bool SetPatrolSpawnPoint { get; set; }
        public float SpawnRadius { get; set; }
        public float NearRadius { get; set; }
        public float FarRadius { get; set; }
        public int MaxNear { get; set; }
        public int MaxTotal { get; set; }
        public bool OnGroundOnly { get; set; }
        public List<SpawnAreaSpawnSnapshot> Prefabs { get; set; } = new();
    }

    private sealed class SpawnAreaLiveSnapshot
    {
        public float LevelUpChance { get; set; }
        public float SpawnInterval { get; set; }
        public float TriggerDistance { get; set; }
        public bool SetPatrolSpawnPoint { get; set; }
        public float SpawnRadius { get; set; }
        public float NearRadius { get; set; }
        public float FarRadius { get; set; }
        public int MaxNear { get; set; }
        public int MaxTotal { get; set; }
        public bool OnGroundOnly { get; set; }
        public List<SpawnAreaSpawnSnapshot> Prefabs { get; set; } = new();
    }

    private sealed class CreatureSpawnerComponentSnapshot
    {
        public CreatureSpawner Component { get; set; } = null!;
        public string ConfigPrefabName { get; set; } = "";
        public string RootPrefabName { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public GameObject? CreaturePrefab { get; set; }
        public int MinLevel { get; set; }
        public int MaxLevel { get; set; }
        public float LevelUpChance { get; set; }
        public float RespawnTimeMinutes { get; set; }
        public float TriggerDistance { get; set; }
        public float TriggerNoise { get; set; }
        public bool SpawnAtNight { get; set; }
        public bool SpawnAtDay { get; set; }
        public bool RequireSpawnArea { get; set; }
        public bool SpawnInPlayerBase { get; set; }
        public bool WakeUpAnimation { get; set; }
        public int SpawnCheckInterval { get; set; }
        public string RequiredGlobalKey { get; set; } = "";
        public string BlockingGlobalKey { get; set; } = "";
        public bool SetPatrolSpawnPoint { get; set; }
        public int SpawnGroupId { get; set; }
        public int MaxGroupSpawned { get; set; }
        public float SpawnGroupRadius { get; set; }
        public float SpawnerWeight { get; set; }
    }

    private sealed class CreatureSpawnerLiveSnapshot
    {
        public GameObject? CreaturePrefab { get; set; }
        public int MinLevel { get; set; }
        public int MaxLevel { get; set; }
        public float LevelUpChance { get; set; }
        public float RespawnTimeMinutes { get; set; }
        public float TriggerDistance { get; set; }
        public float TriggerNoise { get; set; }
        public bool SpawnAtNight { get; set; }
        public bool SpawnAtDay { get; set; }
        public bool RequireSpawnArea { get; set; }
        public bool SpawnInPlayerBase { get; set; }
        public bool WakeUpAnimation { get; set; }
        public int SpawnCheckInterval { get; set; }
        public string RequiredGlobalKey { get; set; } = "";
        public string BlockingGlobalKey { get; set; } = "";
        public bool SetPatrolSpawnPoint { get; set; }
        public int SpawnGroupId { get; set; }
        public int MaxGroupSpawned { get; set; }
        public float SpawnGroupRadius { get; set; }
        public float SpawnerWeight { get; set; }
    }

    private sealed class SyncedSpawnerConfigurationState
    {
        public List<SpawnerConfigurationEntry> Configuration { get; set; } = new();
        public List<SpawnerConfigurationEntry> ActiveEntries { get; } = new();
        public Dictionary<string, List<SpawnerConfigurationEntry>> ActiveEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ConfiguredSpawnAreaPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ConfiguredCreatureSpawnerPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RuntimeConfiguredSpawnAreaPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RuntimeConfiguredCreatureSpawnerPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> EntrySignaturesByPrefab { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string ConfigurationSignature { get; set; } = "";
    }

    private sealed class SpawnerRuntimeEntry
    {
        public string Prefab { get; set; } = "";
        public string RuleId { get; set; } = "";
        public string Location { get; set; } = "";
        public ConditionsDefinition? Conditions { get; set; }
        public bool RuntimeReconcile { get; set; }
        public SpawnAreaDefinition? SpawnArea { get; set; }
        public CreatureSpawnerDefinition? CreatureSpawner { get; set; }
    }

    private sealed class CompiledSpawnerPrefabPlan
    {
        public List<SpawnerRuntimeEntry> SpawnAreaEntries { get; } = new();
        public List<SpawnerRuntimeEntry> DynamicSpawnAreaEntries { get; } = new();
        public List<SpawnerRuntimeEntry> CreatureSpawnerEntries { get; } = new();
        public List<SpawnerRuntimeEntry> DynamicCreatureSpawnerEntries { get; } = new();
        public HashSet<string> SpawnAreaSelectorLocationKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> CreatureSpawnerSelectorLocationKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasUnscopedSpawnAreaEntries { get; set; }
        public bool HasUnscopedCreatureSpawnerEntries { get; set; }
    }

    private sealed class SpawnerRuntimeConfigurationSnapshot
    {
        public static SpawnerRuntimeConfigurationSnapshot Empty { get; } = new();

        public Dictionary<string, CompiledSpawnerPrefabPlan> PlansByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ConfiguredSpawnAreaPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ConfiguredCreatureSpawnerPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RuntimeConfiguredSpawnAreaPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RuntimeConfiguredCreatureSpawnerPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SpawnAreaComponentCatalog
    {
        public string ConfigPrefabName { get; set; } = "";
        public string RootPrefabName { get; set; } = "";
        public string RelativePath { get; set; } = "";
    }

    private sealed class CreatureSpawnerComponentCatalog
    {
        public string ConfigPrefabName { get; set; } = "";
        public string RootPrefabName { get; set; } = "";
        public string RelativePath { get; set; } = "";
    }

    private sealed class SpawnerLocationProvenance
    {
        public int Epoch { get; set; }
        public string LocationPrefab { get; set; } = "";
        public string RelativePath { get; set; } = "";
    }

    private sealed class CurrentLocationSpawnContext
    {
        public string LocationPrefab { get; set; } = "";
    }

    private static readonly object Sync = new();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    private static readonly List<SpawnAreaComponentSnapshot> SpawnAreaSnapshots = new();
    private static readonly List<CreatureSpawnerComponentSnapshot> CreatureSpawnerSnapshots = new();
    private static readonly Dictionary<string, SpawnAreaComponentSnapshot> SpawnAreaSnapshotsByExactKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CreatureSpawnerComponentSnapshot> CreatureSpawnerSnapshotsByExactKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<SpawnAreaComponentSnapshot>> SpawnAreaSnapshotsByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<CreatureSpawnerComponentSnapshot>> CreatureSpawnerSnapshotsByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<SpawnerConfigurationEntry> ActiveEntries = new();
    private static readonly Dictionary<string, List<SpawnerConfigurationEntry>> ActiveEntriesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ConfiguredSpawnAreaPrefabs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ConfiguredCreatureSpawnerPrefabs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RuntimeConfiguredSpawnAreaPrefabs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RuntimeConfiguredCreatureSpawnerPrefabs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InvalidEntryWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SpawnAreaComponentCatalog> SpawnAreaCatalogsByExactKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CreatureSpawnerComponentCatalog> CreatureSpawnerCatalogsByExactKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CapturedRootPrefabNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly FieldInfo? CreatureSpawnerCheckedLocationField = AccessTools.Field(typeof(CreatureSpawner), "m_checkedLocation");
    private static readonly FieldInfo? CreatureSpawnerLocationField = AccessTools.Field(typeof(CreatureSpawner), "m_location");
    private static readonly FieldInfo? CreatureSpawnerSpawnGroupField = AccessTools.Field(typeof(CreatureSpawner), "m_spawnGroup");

    private static List<SpawnerConfigurationEntry> _configuration = new();
    private static string _configurationSignature = "";
    private static DomainLoadState LoadState => ConfigurationRuntime.LoadState;
    private static bool _lastAppliedSynchronizedPayloadReady;
    private static bool _initialized;
    private static bool _snapshotsCaptured;
    private static int? _lastProcessedGameDataSignature;
    private static SpawnerRuntimeConfigurationSnapshot _runtimeConfigurationSnapshot = SpawnerRuntimeConfigurationSnapshot.Empty;
    private static bool _referenceArtifactsAutoRefreshConsumed;
    private static readonly Dictionary<string, string> CurrentEntrySignaturesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _lastAppliedEntrySignaturesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static string _lastAppliedConfigurationSignature = "";
    private static int? _lastAppliedGameDataSignature;
    private static bool? _lastAppliedDomainEnabled;
    private static int _reconcileQueueEpoch;
    private static int _trackedSpawnerEligibilityEpoch;
    private const string MockPrefabPrefix = "JVLmock_";
    // Distinguishes an authoritative empty payload from the pre-sync waiting state on clients.
    private static bool _synchronizedPayloadReady;

    private static string ReferenceConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("spawner")}.reference.yml");
    private static string LocationReferenceConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("spawner")}.locations.reference.yml");
    private static string PrimaryOverrideConfigurationPathYml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("spawner")}.yml");
    private static string PrimaryOverrideConfigurationPathYaml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("spawner")}.yaml");
    private static string FullScaffoldConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("spawner")}.full.yml");
    private static readonly DomainConfigurationRuntime<SpawnerConfigurationEntry, SyncedSpawnerConfigurationState> ConfigurationRuntime =
        new(
            new DomainLoadHooks<SpawnerConfigurationEntry, SyncedSpawnerConfigurationState>(
                ParseLocalConfigurationDocuments,
                BuildSyncedConfigurationState,
                CommitSyncedConfigurationState,
                RejectLocalConfigurationPayload,
                state => state.Configuration.Count,
                LogPartiallyAcceptedLocalConfiguration,
                LogLocalConfigurationLoaded,
                OnSourceOfTruthPayloadUnchanged,
                () => ConfigurationDomainHost.PublishSyncedPayload(
                    DropNSpawnPlugin.IsSourceOfTruth,
                    Descriptor,
                    _configuration,
                    _configurationSignature)),
            new DomainSyncHooks<SpawnerConfigurationEntry, SyncedSpawnerConfigurationState>(
                (out List<SpawnerConfigurationEntry> configuration, out string payloadToken) =>
                    ConfigurationDomainHost.TryGetSyncedEntries(Descriptor, out configuration, out payloadToken),
                payloadToken => ConfigurationDomainHost.ShouldSkipSyncedPayload(
                    LoadState,
                    payloadToken,
                    Volatile.Read(ref _synchronizedPayloadReady)),
                BuildSyncedConfigurationState,
                CommitSyncedConfigurationState,
                state => state.ActiveEntries.Count,
                "ServerSync:DropNSpawnSpawner",
                () => ConfigurationDomainHost.HandleWaitingForSyncedPayload(
                    MarkSyncedPayloadPending,
                    "Waiting for synchronized spawner override payload from the server."),
                LogSyncedSpawnerConfigurationLoaded,
                LogSyncedSpawnerConfigurationFailure));

    internal static bool ShouldReloadForPath(string? path)
    {
        return PluginSettingsFacade.IsEligibleOverrideConfigurationPath(path) &&
               IsOverrideConfigurationFileName(Path.GetFileName(path ?? ""));
    }

    private static bool ShouldApplyLocally()
    {
        return PluginSettingsFacade.IsSpawnerDomainEnabled();
    }

    internal static void MarkSyncedPayloadPending()
    {
        lock (Sync)
        {
            ConfigurationRuntime.MarkSyncedPayloadPending(
                DropNSpawnPlugin.IsSourceOfTruth,
                () => Volatile.Write(ref _synchronizedPayloadReady, false));
        }
    }

    internal static void EnterPendingSyncedPayloadState()
    {
        lock (Sync)
        {
            Dictionary<string, string> previousEntrySignatures = CloneCurrentEntrySignaturesByPrefab();
            HashSet<string> previouslyAppliedPrefabs = BuildLastAppliedPrefabs();
            ConfigurationRuntime.EnterPendingSyncedPayloadState(
                DropNSpawnPlugin.IsSourceOfTruth,
                beforeResetLoadState: ResetLoadedConfigurationState,
                afterResetLoadState: () =>
                {
                    _configurationSignature = "";
                    _lastAppliedSynchronizedPayloadReady = false;
                    ReapplyRegisteredLiveObjects(false, previouslyAppliedPrefabs);
                    RefreshVneiCompatibility(previousEntrySignatures);
                });
        }
    }

    internal static bool ShouldBlockClientSpawnerUpdate()
    {
        if (!ShouldApplyLocally() || DropNSpawnPlugin.IsSourceOfTruth)
        {
            return false;
        }

        if (DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Spawner))
        {
            return true;
        }

        if (!IsGameDataReady())
        {
            return true;
        }

        return !Volatile.Read(ref _synchronizedPayloadReady);
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
            LoadConfiguration();
            ApplyIfReady(queueLiveReconcile: true);
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

            string refreshedSignature = NetworkPayloadSyncSupport.ComputeSpawnerConfigurationSignature(_configuration);
            if (string.Equals(refreshedSignature, _configurationSignature, StringComparison.Ordinal))
            {
                return false;
            }

            _configurationSignature = refreshedSignature;
            ConfigurationDomainHost.PublishSyncedPayload(
                DropNSpawnPlugin.IsSourceOfTruth,
                Descriptor,
                _configuration,
                _configurationSignature);
            ApplyIfReady(queueLiveReconcile: true);
            return true;
        }
    }

    internal static void ApplySyncedPayload()
    {
        lock (Sync)
        {
            Dictionary<string, string> previousEntrySignatures = CloneCurrentEntrySignaturesByPrefab();
            ConfigurationRuntime.ApplySyncedPayload(() =>
            {
                RefreshVneiCompatibility(previousEntrySignatures, CloneCurrentEntrySignaturesByPrefab());
                ApplyIfReady(queueLiveReconcile: true);
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

            if (!IsGameDataReady())
            {
                return;
            }

            HashSet<string> availablePrefabs = BuildCurrentSpawnerReferencePrefabKeys()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int gameDataSignature = ComputeGameDataSignature(availablePrefabs);
            if (_lastProcessedGameDataSignature == gameDataSignature)
            {
                return;
            }

            // Full spawner snapshot capture is reserved for explicit scaffold/reference generation.
            ResetReferenceSnapshots();
            ResetRuntimeState(preserveLiveRegistries: true);
            CleanupRegisteredSpawnAreas();
            CleanupRegisteredCreatureSpawners();
            if (DropNSpawnPlugin.IsSourceOfTruth && !_referenceArtifactsAutoRefreshConsumed)
            {
                EnsureReferenceArtifactsUpToDate();
                _referenceArtifactsAutoRefreshConsumed = true;
            }
            else if (!DropNSpawnPlugin.IsSourceOfTruth)
            {
                _referenceArtifactsAutoRefreshConsumed = true;
            }

            if (DropNSpawnPlugin.IsSourceOfTruth && EnsurePrimaryOverrideConfigurationFileExists())
            {
                LoadConfiguration();
            }

            ApplyIfReady(queueLiveReconcile: true);
            _lastProcessedGameDataSignature = gameDataSignature;
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Spawners processed after {source}.");
        }
    }

    internal static bool TryWriteFullScaffoldConfigurationFile(out string path, out string error)
    {
        lock (Sync)
        {
            path = FullScaffoldConfigurationPath;
            error = "";

            if (!IsGameDataReady() && !_snapshotsCaptured)
            {
                error = "Spawner game data is not ready yet.";
                return false;
            }

            CaptureSnapshotsIfNeeded();
            Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
            File.WriteAllText(path, BuildFullScaffoldConfigurationTemplate());
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Wrote spawner full scaffold configuration to {path}.");
            return true;
        }
    }

    internal static void RefreshReferenceConfigurationFile()
    {
        lock (Sync)
        {
            if (!IsGameDataReady())
            {
                return;
            }

            CaptureSnapshotsIfNeeded();
            string referenceContent = BuildReferenceConfigurationTemplate();
            string locationReferenceContent = BuildLocationReferenceConfigurationTemplate();
            WriteReferenceConfigurationFile(
                referenceContent,
                locationReferenceContent,
                $"Updated spawner reference configurations at {ReferenceConfigurationPath} and {LocationReferenceConfigurationPath}.",
                writePrimaryReference: true,
                writeLocationReference: true);
            ReferenceRefreshSupport.RecordAutoUpdateState(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, ComputeReferenceSourceSignature(), logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
            ReferenceRefreshSupport.RecordAutoUpdateState(LocationReferenceAutoUpdateStateKey, LocationReferenceConfigurationPath, ComputeReferenceSourceSignature(), logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
            ResetReferenceSnapshots();
        }
    }

    private static void EnsureReferenceArtifactsUpToDate()
    {
        if (!IsGameDataReady())
        {
            return;
        }

        bool usedReferenceSnapshots = EnsureSpawnerReferenceConfigurationUpToDate();

        if (usedReferenceSnapshots)
        {
            ResetReferenceSnapshots();
        }
    }

    private static bool EnsureSpawnerReferenceConfigurationUpToDate()
    {
        string currentSourceSignature = ComputeReferenceSourceSignature();
        bool referenceFileExists = File.Exists(ReferenceConfigurationPath);
        bool locationReferenceFileExists = File.Exists(LocationReferenceConfigurationPath);
        bool shouldCreateMissingFiles = PluginSettingsFacade.ShouldAutoCreateMissingReferenceFiles();
        bool shouldCreatePrimary = !referenceFileExists && shouldCreateMissingFiles;
        bool shouldCreateLocation = !locationReferenceFileExists && shouldCreateMissingFiles;
        bool shouldRewritePrimary = false;
        bool shouldRewriteLocation = false;

        if (PluginSettingsFacade.ShouldAutoUpdateReferenceFiles())
        {
            if (referenceFileExists)
            {
                shouldRewritePrimary = !ReferenceRefreshSupport.ShouldSkipAutoUpdate(
                    ReferenceAutoUpdateStateKey,
                    ReferenceConfigurationPath,
                    currentSourceSignature,
                    ReferenceRefreshSupport.CurrentReferenceLogicVersion);
            }

            if (locationReferenceFileExists)
            {
                shouldRewriteLocation = !ReferenceRefreshSupport.ShouldSkipAutoUpdate(
                    LocationReferenceAutoUpdateStateKey,
                    LocationReferenceConfigurationPath,
                    currentSourceSignature,
                    ReferenceRefreshSupport.CurrentReferenceLogicVersion);
            }
        }

        bool writePrimaryReference = shouldCreatePrimary || shouldRewritePrimary;
        bool writeLocationReference = shouldCreateLocation || shouldRewriteLocation;
        if (!writePrimaryReference && !writeLocationReference)
        {
            return false;
        }

        CaptureSnapshotsIfNeeded();
        string? referenceContent = writePrimaryReference ? BuildReferenceConfigurationTemplate() : null;
        string? locationReferenceContent = writeLocationReference ? BuildLocationReferenceConfigurationTemplate() : null;
        bool updatedExisting = shouldRewritePrimary || shouldRewriteLocation;
        string action = updatedExisting ? "Updated" : "Created";
        string targetDescription = writePrimaryReference && writeLocationReference
            ? $"spawner reference configurations at {ReferenceConfigurationPath} and {LocationReferenceConfigurationPath}"
            : writePrimaryReference
                ? $"spawner reference configuration at {ReferenceConfigurationPath}"
                : $"spawner location reference configuration at {LocationReferenceConfigurationPath}";

        WriteReferenceConfigurationFile(
            referenceContent,
            locationReferenceContent,
            $"{action} {targetDescription}.",
            writePrimaryReference,
            writeLocationReference);
        if (writePrimaryReference)
        {
            ReferenceRefreshSupport.RecordAutoUpdateState(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, currentSourceSignature, logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
        }

        if (writeLocationReference)
        {
            ReferenceRefreshSupport.RecordAutoUpdateState(LocationReferenceAutoUpdateStateKey, LocationReferenceConfigurationPath, currentSourceSignature, logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
        }

        return true;
    }

    private static IEnumerable<string> BuildCurrentSpawnerReferencePrefabKeys()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (GameObject rootPrefab in EnumerateRootPrefabs())
        {
            foreach (SpawnArea spawnArea in rootPrefab.GetComponentsInChildren<SpawnArea>(true))
            {
                string prefabName = ReferenceRefreshSupport.NormalizeKey(spawnArea?.gameObject?.name);
                if (prefabName.Length > 0 && seen.Add(prefabName))
                {
                    yield return prefabName;
                }
            }

            foreach (CreatureSpawner creatureSpawner in rootPrefab.GetComponentsInChildren<CreatureSpawner>(true))
            {
                string prefabName = ReferenceRefreshSupport.NormalizeKey(creatureSpawner?.gameObject?.name);
                if (prefabName.Length > 0 && seen.Add(prefabName))
                {
                    yield return prefabName;
                }
            }
        }
    }

    private static string ComputeReferenceSourceSignature()
    {
        return ReferenceRefreshSupport.ComputeStableHashForKeys(BuildCurrentSpawnerReferencePrefabKeys());
    }





    internal static bool TryInspectCurrentTarget(out string[] lines, out string error)
    {
        lock (Sync)
        {
            lines = Array.Empty<string>();
            error = "";

            if (Player.m_localPlayer == null)
            {
                error = "Player is not available.";
                return false;
            }

            GameObject? target = ResolveCurrentInspectionTarget();
            if (target == null)
            {
                error = "No hovered or nearby spawner target found.";
                return false;
            }

            List<string> result = BuildInspectionLines(target);
            if (result.Count == 0)
            {
                error = "The current target is not a SpawnArea or CreatureSpawner.";
                return false;
            }

            lines = result.ToArray();
            return true;
        }
    }


    internal static void RecordDirectSpawnAreaSpawnedObject(SpawnArea spawnArea, GameObject? spawnedObject)
    {
        lock (Sync)
        {
            if (spawnArea == null || spawnedObject == null || !LiveReconcilerState.HasPendingSpawnAreaAttempt(spawnArea))
            {
                return;
            }

            LiveReconcilerState.SetPendingSpawnAreaSpawnedObject(spawnArea, spawnedObject);
        }
    }

    internal static void FinalizeSpawnAreaSpawnAttempt(SpawnArea spawnArea, bool succeeded)
    {
        lock (Sync)
        {
            LiveReconcilerState.RemovePendingSpawnAreaAttemptMarker(spawnArea);
            LiveReconcilerState.TryTakePendingSpawnAreaSelection(spawnArea, out SpawnArea.SpawnData? selectedSpawnData);
            bool hasRecordedSpawnPoint = LiveReconcilerState.TryTakePendingSpawnAreaSpawnPoint(spawnArea, out Vector3 recordedSpawnPoint);
            string? faction = null;
            ExpandWorldSpawnDataPayload? payload = null;
            bool hasFaction = selectedSpawnData != null && LiveReconcilerState.TryGetAppliedSpawnAreaFaction(selectedSpawnData, out faction);
            bool hasPayload = selectedSpawnData != null && LiveReconcilerState.TryGetAppliedSpawnAreaData(selectedSpawnData, out payload);
            bool hasObjects = hasPayload && payload!.HasObjects;

            if (succeeded &&
                hasRecordedSpawnPoint &&
                selectedSpawnData != null &&
                (hasFaction || hasObjects))
            {
                if (hasObjects)
                {
                    ExpandWorldSpawnDataSupport.SpawnObjects(recordedSpawnPoint, payload);
                }

                if (hasFaction)
                {
                    Character? spawnedCharacter = LiveReconcilerState.TryTakePendingSpawnAreaSpawnedObject(spawnArea, out GameObject? directSpawnedObject) && directSpawnedObject != null
                        ? directSpawnedObject.GetComponent<Character>()
                        : null;
                    if (spawnedCharacter != null)
                    {
                        string context = $"{GetConfigPrefabName(spawnArea.gameObject, nameof(SpawnArea))}@{DescribeInstance(spawnArea.gameObject)}/spawnArea.spawn";
                        FactionIntegration.Apply(spawnedCharacter, faction, context);
                    }
                }
            }

            if (succeeded)
            {
                RecordSuccessfulSpawnAreaTotalSpawn(spawnArea);
            }
        }
    }

    internal static void ApplyCreatureSpawnerSpawnOverrides(CreatureSpawner creatureSpawner, ZNetView? spawnedView)
    {
        lock (Sync)
        {
            if (creatureSpawner == null ||
                spawnedView == null)
            {
                return;
            }

            string context = $"{GetConfigPrefabName(creatureSpawner.gameObject, nameof(CreatureSpawner))}@{DescribeInstance(creatureSpawner.gameObject)}/creatureSpawner.spawn";
            if (LiveReconcilerState.TryGetAppliedCreatureSpawnerData(creatureSpawner, out ExpandWorldSpawnDataPayload payload) &&
                payload.HasObjects)
            {
                ExpandWorldSpawnDataSupport.SpawnObjects(spawnedView.transform.position, payload);
            }

            Character? spawnedCharacter = spawnedView.GetComponent<Character>();
            if (spawnedCharacter == null)
            {
                return;
            }

            if (LiveReconcilerState.TryGetAppliedCreatureSpawnerFaction(creatureSpawner, out string faction))
            {
                FactionIntegration.Apply(spawnedCharacter, faction, context);
            }
        }
    }

    internal static void InitializeSpawnAreaSpawnData(SpawnArea spawnArea, GameObject? prefab, Vector3 spawnPoint)
    {
        lock (Sync)
        {
            if (spawnArea == null ||
                prefab == null ||
                !LiveReconcilerState.TryGetPendingSpawnAreaSelection(spawnArea, out SpawnArea.SpawnData? selectedSpawnData) ||
                selectedSpawnData == null ||
                !LiveReconcilerState.TryGetAppliedSpawnAreaData(selectedSpawnData, out ExpandWorldSpawnDataPayload payload))
            {
                return;
            }

            ExpandWorldSpawnDataSupport.InitializeSpawn(prefab, spawnPoint, payload);
        }
    }

    internal static void InitializeCreatureSpawnerSpawnData(CreatureSpawner creatureSpawner, GameObject? prefab, Vector3 spawnPoint)
    {
        lock (Sync)
        {
            if (creatureSpawner == null ||
                prefab == null ||
                !LiveReconcilerState.TryGetAppliedCreatureSpawnerData(creatureSpawner, out ExpandWorldSpawnDataPayload payload))
            {
                return;
            }

            ExpandWorldSpawnDataSupport.InitializeSpawn(prefab, spawnPoint, payload);
        }
    }

    internal static bool IsCreatureSpawnerTimeOfDayAllowed(CreatureSpawner creatureSpawner)
    {
        lock (Sync)
        {
            return creatureSpawner == null ||
                   !LiveReconcilerState.TryGetAppliedCreatureSpawnerTimeOfDay(creatureSpawner, out TimeOfDayDefinition timeOfDay) ||
                   TimeOfDayFormatting.MatchesCurrentTime(timeOfDay);
        }
    }

    private static bool IsGameDataReady()
    {
        return ZNetScene.instance != null;
    }

    private static int ComputeGameDataSignature(IEnumerable<string>? availablePrefabs = null)
    {
        if (!IsGameDataReady() || ZNetScene.instance == null)
        {
            return 0;
        }

        unchecked
        {
            int hash = 17;
            hash = hash * 31 + ZNetScene.instance.GetInstanceID();
            hash = HashNormalizedKeys(hash, availablePrefabs ?? BuildCurrentSpawnerReferencePrefabKeys());
            hash = HashNormalizedKeys(hash, BuildConfiguredSpawnerResolutionKeys());
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

    private static IEnumerable<string> BuildConfiguredSpawnerResolutionKeys()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string _, List<SpawnerConfigurationEntry> entries) in ActiveEntriesByPrefab)
        {
            foreach (SpawnerConfigurationEntry entry in entries)
            {
                if (entry.SpawnArea?.Creatures != null)
                {
                    for (int index = 0; index < entry.SpawnArea.Creatures.Count; index++)
                    {
                        string creatureName = ReferenceRefreshSupport.NormalizeKey(entry.SpawnArea.Creatures[index]?.Creature);
                        if (creatureName.Length == 0)
                        {
                            continue;
                        }

                        int resolvedPrefabId = ResolveCreaturePrefabForSignature(creatureName)?.GetInstanceID() ?? 0;
                        string key = $"spawnArea:{creatureName}:{resolvedPrefabId.ToString(CultureInfo.InvariantCulture)}";
                        if (seen.Add(key))
                        {
                            yield return key;
                        }
                    }
                }

                string creatureSpawnerPrefab = ReferenceRefreshSupport.NormalizeKey(entry.CreatureSpawner?.Creature);
                if (creatureSpawnerPrefab.Length == 0)
                {
                    continue;
                }

                int resolvedCreatureSpawnerPrefabId = ResolveCreaturePrefabForSignature(creatureSpawnerPrefab)?.GetInstanceID() ?? 0;
                string creatureSpawnerKey = $"creatureSpawner:{creatureSpawnerPrefab}:{resolvedCreatureSpawnerPrefabId.ToString(CultureInfo.InvariantCulture)}";
                if (seen.Add(creatureSpawnerKey))
                {
                    yield return creatureSpawnerKey;
                }
            }
        }
    }

    private static GameObject? ResolveCreaturePrefabForSignature(string? prefabName)
    {
        string normalizedPrefabName = ReferenceRefreshSupport.NormalizeKey(prefabName);
        if (normalizedPrefabName.Length == 0)
        {
            return null;
        }

        GameObject? prefab = ZNetScene.instance?.GetPrefab(normalizedPrefabName);
        if (prefab == null)
        {
            return null;
        }

        return prefab.TryGetComponent(out Character _) || prefab.TryGetComponent(out BaseAI _)
            ? prefab
            : null;
    }

    private static bool EnsurePrimaryOverrideConfigurationFileExists()
    {
        if (DomainConfigurationFileSupport.HasAnyOverrideConfigurationFile(
                "spawner",
                PrimaryOverrideConfigurationPathYml,
                PrimaryOverrideConfigurationPathYaml))
        {
            return false;
        }

        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        File.WriteAllText(PrimaryOverrideConfigurationPathYml, BuildPrimaryOverrideConfigurationTemplate());
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Created spawner override configuration at {PrimaryOverrideConfigurationPathYml}.");
        return true;
    }

    private static void LoadConfiguration()
    {
        Dictionary<string, string> previousEntrySignatures = CloneCurrentEntrySignaturesByPrefab();
        if (DropNSpawnPlugin.IsSourceOfTruth)
        {
            EnsurePrimaryOverrideConfigurationFileExists();
            if (ConfigurationRuntime.ReloadSourceOfTruth(
                    EnumerateOverrideConfigurationPaths().ToList()) == DomainReloadOutcome.Loaded)
            {
                RefreshVneiCompatibility(previousEntrySignatures);
            }

            return;
        }

        if (ConfigurationRuntime.ReloadSynced() == DomainReloadOutcome.Loaded)
        {
            RefreshVneiCompatibility(previousEntrySignatures, CloneCurrentEntrySignaturesByPrefab());
        }
    }

    private static void RefreshVneiCompatibility(Dictionary<string, string> previousEntrySignatures)
    {
        RefreshVneiCompatibility(previousEntrySignatures, CloneCurrentEntrySignaturesByPrefab());
    }

    private static void RefreshVneiCompatibility(Dictionary<string, string> previousEntrySignatures, Dictionary<string, string> currentEntrySignatures)
    {
        VneiCompatibility.RefreshSpawnerPrefabs(BuildDirtyPrefabs(previousEntrySignatures, currentEntrySignatures));
    }

    private static void ResetLoadedConfigurationState()
    {
        ClearQueuedReconcileState();
        Volatile.Write(ref _synchronizedPayloadReady, false);
        Volatile.Write(ref _runtimeConfigurationSnapshot, SpawnerRuntimeConfigurationSnapshot.Empty);
        ActiveEntries.Clear();
        ActiveEntriesByPrefab.Clear();
        ConfiguredSpawnAreaPrefabs.Clear();
        ConfiguredCreatureSpawnerPrefabs.Clear();
        RuntimeConfiguredSpawnAreaPrefabs.Clear();
        RuntimeConfiguredCreatureSpawnerPrefabs.Clear();
        InvalidEntryWarnings.Clear();
        LiveReconcilerState.ClearMissingComponentWarnings();
        SelectorCacheStore.Clear();
        RuntimeStateStore.Clear();
        LiveRegistryStore.ClearRuntimeView();
        ProvenanceRegistry.Clear(clearCurrentContexts: false);
        _configuration = new List<SpawnerConfigurationEntry>();
        CurrentEntrySignaturesByPrefab.Clear();
        InvalidateTrackedSpawnerEligibility();
    }

    private static List<SpawnerConfigurationEntry> CloneAndNormalizeConfigurationEntries(
        List<SpawnerConfigurationEntry>? configuration,
        string sourceName)
    {
        List<SpawnerConfigurationEntry> normalizedConfiguration =
            NetworkPayloadSyncSupport.CloneEntries(Descriptor, configuration);
        foreach (SpawnerConfigurationEntry entry in normalizedConfiguration)
        {
            entry.SourcePath = string.IsNullOrWhiteSpace(entry.SourcePath) ? sourceName : entry.SourcePath;
            NormalizeEntry(entry);
        }

        return normalizedConfiguration;
    }

    private static List<SpawnerConfigurationEntry> PrepareLocalConfigurationEntries(
        List<SpawnerConfigurationEntry>? configuration,
        string sourceName,
        List<string> warnings)
    {
        List<SpawnerConfigurationEntry> normalizedConfiguration =
            CloneAndNormalizeConfigurationEntries(configuration, sourceName);
        List<SpawnerConfigurationEntry> acceptedEntries = new();
        foreach (SpawnerConfigurationEntry entry in normalizedConfiguration)
        {
            if (!TryAcceptLocalConfigurationEntry(entry, warnings))
            {
                continue;
            }

            acceptedEntries.Add(entry);
        }

        return acceptedEntries;
    }

    private static bool TryAcceptLocalConfigurationEntry(SpawnerConfigurationEntry entry, List<string> warnings)
    {
        if (!entry.Enabled)
        {
            return true;
        }

        string context = CreateConfigurationContext(entry);
        if (string.IsNullOrWhiteSpace(entry.Prefab))
        {
            warnings.Add($"Entry '{context}' is missing required prefab.");
            return false;
        }

        if (!TryResolveConfiguredSpawnerPrefab(entry.Prefab, out bool hasSpawnerComponents))
        {
            warnings.Add($"Entry '{context}' references unknown spawner prefab '{entry.Prefab}'.");
            return false;
        }

        if (!hasSpawnerComponents)
        {
            warnings.Add($"Entry '{context}' references '{entry.Prefab}', but it is not a SpawnArea/CreatureSpawner prefab.");
            return false;
        }

        return true;
    }

    private static bool TryResolveConfiguredSpawnerPrefab(string prefabName, out bool hasSpawnerComponents)
    {
        hasSpawnerComponents = true;
        if (ZNetScene.instance == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return true;
        }

        GameObject? prefab = ZNetScene.instance.GetPrefab(prefabName.Trim());
        if (prefab == null)
        {
            hasSpawnerComponents = false;
            return false;
        }

        hasSpawnerComponents =
            prefab.GetComponentInChildren<SpawnArea>(true) != null ||
            prefab.GetComponentInChildren<CreatureSpawner>(true) != null;
        return true;
    }

    private static SyncedSpawnerConfigurationState BuildSyncedConfigurationState(
        List<SpawnerConfigurationEntry> configuration,
        string sourceName)
    {
        using InvalidEntryWarningSuppressionScope _ = BeginInvalidEntryWarningSuppressionForSyncedClientBuild(sourceName);
        SyncedSpawnerConfigurationState state = new();
        foreach (SpawnerConfigurationEntry entry in CloneAndNormalizeConfigurationEntries(configuration, sourceName))
        {
            if (string.IsNullOrWhiteSpace(entry.Prefab))
            {
                continue;
            }

            RemoveEffectiveConfigurationEntry(state.Configuration, state.ActiveEntries, state.ActiveEntriesByPrefab, entry.Prefab, entry.RuleId);
            if (!entry.Enabled)
            {
                continue;
            }

            state.Configuration.Add(entry);
            state.ActiveEntries.Add(entry);
            GetOrCreateActiveEntries(state.ActiveEntriesByPrefab, entry.Prefab).Add(entry);
        }

        RefreshConfiguredPrefabSets(
            state.ActiveEntries,
            state.ConfiguredSpawnAreaPrefabs,
            state.ConfiguredCreatureSpawnerPrefabs,
            state.RuntimeConfiguredSpawnAreaPrefabs,
            state.RuntimeConfiguredCreatureSpawnerPrefabs);
        state.EntrySignaturesByPrefab = BuildActiveEntrySignaturesByPrefab(state.ActiveEntriesByPrefab);
        state.ConfigurationSignature = NetworkPayloadSyncSupport.ComputeSpawnerConfigurationSignature(state.Configuration);
        return state;
    }

    private static SpawnerRuntimeConfigurationSnapshot BuildRuntimeConfigurationSnapshot(SyncedSpawnerConfigurationState state)
    {
        SpawnerRuntimeConfigurationSnapshot snapshot = new();
        foreach ((string prefabName, List<SpawnerConfigurationEntry> entries) in state.ActiveEntriesByPrefab)
        {
            CompiledSpawnerPrefabPlan prefabPlan = new();
            for (int index = 0; index < entries.Count; index++)
            {
                SpawnerConfigurationEntry entry = entries[index];
                SpawnerRuntimeEntry runtimeEntry = BuildRuntimeEntry(entry);
                if (entry.SpawnArea != null && HasSpawnAreaOverride(entry.SpawnArea))
                {
                    prefabPlan.SpawnAreaEntries.Add(runtimeEntry);
                    if (string.IsNullOrWhiteSpace(runtimeEntry.Location))
                    {
                        prefabPlan.HasUnscopedSpawnAreaEntries = true;
                    }
                    else
                    {
                        prefabPlan.SpawnAreaSelectorLocationKeys.Add(
                            NormalizeSelectorLocationCacheKey(runtimeEntry.Location));
                    }

                    if (runtimeEntry.RuntimeReconcile)
                    {
                        prefabPlan.DynamicSpawnAreaEntries.Add(runtimeEntry);
                    }
                }

                if (entry.CreatureSpawner != null && HasCreatureSpawnerOverride(entry.CreatureSpawner))
                {
                    prefabPlan.CreatureSpawnerEntries.Add(runtimeEntry);
                    if (string.IsNullOrWhiteSpace(runtimeEntry.Location))
                    {
                        prefabPlan.HasUnscopedCreatureSpawnerEntries = true;
                    }
                    else
                    {
                        prefabPlan.CreatureSpawnerSelectorLocationKeys.Add(
                            NormalizeSelectorLocationCacheKey(runtimeEntry.Location));
                    }

                    if (runtimeEntry.RuntimeReconcile)
                    {
                        prefabPlan.DynamicCreatureSpawnerEntries.Add(runtimeEntry);
                    }
                }
            }

            snapshot.PlansByPrefab[prefabName] = prefabPlan;
        }

        snapshot.ConfiguredSpawnAreaPrefabs.UnionWith(state.ConfiguredSpawnAreaPrefabs);
        snapshot.ConfiguredCreatureSpawnerPrefabs.UnionWith(state.ConfiguredCreatureSpawnerPrefabs);
        snapshot.RuntimeConfiguredSpawnAreaPrefabs.UnionWith(state.RuntimeConfiguredSpawnAreaPrefabs);
        snapshot.RuntimeConfiguredCreatureSpawnerPrefabs.UnionWith(state.RuntimeConfiguredCreatureSpawnerPrefabs);
        return snapshot;
    }

    private static SpawnerRuntimeEntry BuildRuntimeEntry(SpawnerConfigurationEntry entry)
    {
        return new SpawnerRuntimeEntry
        {
            Prefab = entry.Prefab ?? "",
            RuleId = entry.RuleId ?? "",
            Location = entry.Location ?? "",
            Conditions = entry.Conditions,
            RuntimeReconcile = ShouldRuntimeReconcile(entry),
            SpawnArea = entry.SpawnArea,
            CreatureSpawner = entry.CreatureSpawner
        };
    }

    private static SpawnerRuntimeConfigurationSnapshot GetRuntimeConfigurationSnapshot()
    {
        return Volatile.Read(ref _runtimeConfigurationSnapshot) ?? SpawnerRuntimeConfigurationSnapshot.Empty;
    }

    private static void CommitSyncedConfigurationState(SyncedSpawnerConfigurationState state, string payloadToken)
    {
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot = BuildRuntimeConfigurationSnapshot(state);
        ResetLoadedConfigurationState();
        _configuration = state.Configuration;
        ActiveEntries.AddRange(state.ActiveEntries);
        foreach ((string prefabName, List<SpawnerConfigurationEntry> entries) in state.ActiveEntriesByPrefab)
        {
            ActiveEntriesByPrefab[prefabName] = entries;
        }

        foreach (string prefabName in state.ConfiguredSpawnAreaPrefabs)
        {
            ConfiguredSpawnAreaPrefabs.Add(prefabName);
        }

        foreach (string prefabName in state.ConfiguredCreatureSpawnerPrefabs)
        {
            ConfiguredCreatureSpawnerPrefabs.Add(prefabName);
        }

        foreach (string prefabName in state.RuntimeConfiguredSpawnAreaPrefabs)
        {
            RuntimeConfiguredSpawnAreaPrefabs.Add(prefabName);
        }

        foreach (string prefabName in state.RuntimeConfiguredCreatureSpawnerPrefabs)
        {
            RuntimeConfiguredCreatureSpawnerPrefabs.Add(prefabName);
        }

        ReplaceEntrySignatures(CurrentEntrySignaturesByPrefab, state.EntrySignaturesByPrefab);
        _configurationSignature = state.ConfigurationSignature;
        LoadState.LastLoadedPayload = payloadToken;
        LoadState.LastRejectedPayload = "";
        LoadState.PendingStrictPayload = "";
        LoadState.LastRejectedValidationKey = "";
        Volatile.Write(ref _runtimeConfigurationSnapshot, runtimeConfigurationSnapshot);
        Volatile.Write(ref _synchronizedPayloadReady, true);
        InvalidateTrackedSpawnerEligibility();
    }

    private static LocalLoadResult<SpawnerConfigurationEntry> ParseLocalConfigurationDocuments(
        List<ConfigurationLoadSupport.LocalYamlDocument> documents)
    {
        List<SpawnerConfigurationEntry> configuration = new();
        List<string> errors = new();
        List<string> warnings = new();
        int parsedEntryCount = 0;
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
                ParsedSpawnerConfigurationDocument parsedDocument = ParseConfiguration(yaml, document.Path);
                warnings.AddRange(parsedDocument.Warnings);
                parsedEntryCount += parsedDocument.Configuration.Count;
                List<SpawnerConfigurationEntry> sourcedConfiguration =
                    PrepareLocalConfigurationEntries(parsedDocument.Configuration, document.Path, warnings);
                configuration.AddRange(sourcedConfiguration);
                loadedFileCount++;
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Failed to parse {document.Path}{FormatYamlExceptionLocation(ex)}. Spawner override YAML must start with a root list like '- prefab: ...'. {ex}");
            }
        }

        return new LocalLoadResult<SpawnerConfigurationEntry>
        {
            Entries = configuration,
            Errors = errors,
            Warnings = warnings,
            ParsedEntryCount = parsedEntryCount,
            LoadedFileCount = loadedFileCount
        };
    }

    private static void RejectLocalConfigurationPayload(string payload, IEnumerable<string> errors)
    {
        if (string.Equals(LoadState.LastRejectedPayload, payload, StringComparison.Ordinal))
        {
            return;
        }

        LoadState.LastRejectedPayload = payload;
        LoadState.PendingStrictPayload = "";
        LoadState.LastRejectedValidationKey = "";
        DropNSpawnPlugin.DropNSpawnLogger.LogError(
            "Rejected spawner reload. Keeping the previous authoritative spawner configuration.");
        foreach (string error in errors
                     .Where(message => !string.IsNullOrWhiteSpace(message))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogError(error);
        }
    }

    private static void LoadLocalConfiguration(List<ConfigurationLoadSupport.LocalYamlDocument> documents)
    {
        if (documents.Count == 0)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo("Loaded 0 spawner configuration(s) from 0 override file(s).");
            return;
        }

        int loadedFileCount = 0;
        int parsedEntryCount = 0;
        List<string> warnings = new();
        foreach (ConfigurationLoadSupport.LocalYamlDocument document in documents)
        {
            if (document.ReadError != null)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to read {document.Path}. {document.ReadError}");
                continue;
            }

            try
            {
                string yaml = document.Yaml ?? "";
                ParsedSpawnerConfigurationDocument parsedDocument = ParseConfiguration(yaml, document.Path);
                warnings.AddRange(parsedDocument.Warnings);
                List<SpawnerConfigurationEntry> configuration =
                    PrepareLocalConfigurationEntries(parsedDocument.Configuration, document.Path, warnings);
                parsedEntryCount += parsedDocument.Configuration.Count;
                MergeConfiguration(configuration);
                loadedFileCount++;
            }
            catch (Exception ex)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogError(
                    $"Failed to parse {document.Path}{FormatYamlExceptionLocation(ex)}. Spawner override YAML must start with a root list like '- prefab: ...'. {ex}");
            }
        }

        if (warnings.Count > 0)
        {
            LogPartiallyAcceptedLocalConfiguration(parsedEntryCount, _configuration.Count, warnings);
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Loaded {ActiveEntries.Count} spawner configuration(s) from {loadedFileCount} override file(s).");
    }

    private static void MergeConfiguration(List<SpawnerConfigurationEntry> configuration)
    {
        foreach (SpawnerConfigurationEntry entry in configuration)
        {
            if (string.IsNullOrWhiteSpace(entry.Prefab))
            {
                continue;
            }

            RemoveEffectiveConfigurationEntry(entry.Prefab, entry.RuleId);
            if (!entry.Enabled)
            {
                continue;
            }

            _configuration.Add(entry);
            ActiveEntries.Add(entry);

            if (!ActiveEntriesByPrefab.TryGetValue(entry.Prefab, out List<SpawnerConfigurationEntry>? entries))
            {
                entries = new List<SpawnerConfigurationEntry>();
                ActiveEntriesByPrefab[entry.Prefab] = entries;
            }

            entries.Add(entry);
            if (entry.SpawnArea != null && HasSpawnAreaOverride(entry.SpawnArea))
            {
                ConfiguredSpawnAreaPrefabs.Add(entry.Prefab);
                if (ShouldRuntimeReconcile(entry))
                {
                    RuntimeConfiguredSpawnAreaPrefabs.Add(entry.Prefab);
                }
            }

            if (entry.CreatureSpawner != null && HasCreatureSpawnerOverride(entry.CreatureSpawner))
            {
                ConfiguredCreatureSpawnerPrefabs.Add(entry.Prefab);
                if (ShouldRuntimeReconcile(entry))
                {
                    RuntimeConfiguredCreatureSpawnerPrefabs.Add(entry.Prefab);
                }
            }
        }

        RefreshConfiguredPrefabSets(
            ActiveEntries,
            ConfiguredSpawnAreaPrefabs,
            ConfiguredCreatureSpawnerPrefabs,
            RuntimeConfiguredSpawnAreaPrefabs,
            RuntimeConfiguredCreatureSpawnerPrefabs);
        InvalidateTrackedSpawnerEligibility();
    }

    private static void InvalidateTrackedSpawnerEligibility()
    {
        unchecked
        {
            _trackedSpawnerEligibilityEpoch++;
            if (_trackedSpawnerEligibilityEpoch == int.MinValue)
            {
                _trackedSpawnerEligibilityEpoch = 0;
            }
        }
    }

    private static void RemoveEffectiveConfigurationEntry(string prefabName, string ruleId)
    {
        RemoveEffectiveConfigurationEntry(_configuration, ActiveEntries, ActiveEntriesByPrefab, prefabName, ruleId);
    }

    private static void RemoveEffectiveConfigurationEntry(
        List<SpawnerConfigurationEntry> configuration,
        List<SpawnerConfigurationEntry> activeEntries,
        Dictionary<string, List<SpawnerConfigurationEntry>> activeEntriesByPrefab,
        string prefabName,
        string ruleId)
    {
        for (int index = configuration.Count - 1; index >= 0; index--)
        {
            SpawnerConfigurationEntry existingEntry = configuration[index];
            if (string.Equals(existingEntry.Prefab, prefabName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existingEntry.RuleId, ruleId, StringComparison.Ordinal))
            {
                configuration.RemoveAt(index);
            }
        }

        for (int index = activeEntries.Count - 1; index >= 0; index--)
        {
            SpawnerConfigurationEntry existingEntry = activeEntries[index];
            if (string.Equals(existingEntry.Prefab, prefabName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existingEntry.RuleId, ruleId, StringComparison.Ordinal))
            {
                activeEntries.RemoveAt(index);
            }
        }

        if (!activeEntriesByPrefab.TryGetValue(prefabName, out List<SpawnerConfigurationEntry>? entries))
        {
            return;
        }

        for (int index = entries.Count - 1; index >= 0; index--)
        {
            if (string.Equals(entries[index].RuleId, ruleId, StringComparison.Ordinal))
            {
                entries.RemoveAt(index);
            }
        }

        if (entries.Count == 0)
        {
            activeEntriesByPrefab.Remove(prefabName);
        }
    }

    private static List<SpawnerConfigurationEntry> GetOrCreateActiveEntries(
        Dictionary<string, List<SpawnerConfigurationEntry>> activeEntriesByPrefab,
        string prefabName)
    {
        if (!activeEntriesByPrefab.TryGetValue(prefabName, out List<SpawnerConfigurationEntry>? entries))
        {
            entries = new List<SpawnerConfigurationEntry>();
            activeEntriesByPrefab[prefabName] = entries;
        }

        return entries;
    }

    private static void RefreshConfiguredPrefabSets(
        IEnumerable<SpawnerConfigurationEntry> entries,
        HashSet<string> configuredSpawnAreaPrefabs,
        HashSet<string> configuredCreatureSpawnerPrefabs,
        HashSet<string> runtimeConfiguredSpawnAreaPrefabs,
        HashSet<string> runtimeConfiguredCreatureSpawnerPrefabs)
    {
        configuredSpawnAreaPrefabs.Clear();
        configuredCreatureSpawnerPrefabs.Clear();
        runtimeConfiguredSpawnAreaPrefabs.Clear();
        runtimeConfiguredCreatureSpawnerPrefabs.Clear();

        foreach (SpawnerConfigurationEntry entry in entries)
        {
            if (entry.SpawnArea != null && HasSpawnAreaOverride(entry.SpawnArea))
            {
                configuredSpawnAreaPrefabs.Add(entry.Prefab);
                if (ShouldRuntimeReconcile(entry))
                {
                    runtimeConfiguredSpawnAreaPrefabs.Add(entry.Prefab);
                }
            }

            if (entry.CreatureSpawner != null && HasCreatureSpawnerOverride(entry.CreatureSpawner))
            {
                configuredCreatureSpawnerPrefabs.Add(entry.Prefab);
                if (ShouldRuntimeReconcile(entry))
                {
                    runtimeConfiguredCreatureSpawnerPrefabs.Add(entry.Prefab);
                }
            }
        }
    }

    private static ParsedSpawnerConfigurationDocument ParseConfiguration(string yaml, string? sourcePath)
    {
        ParsedSpawnerConfigurationDocument result = new();
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
                "Spawner override YAML root must be a sequence.");
        }

        foreach (YamlNode node in sequence.Children)
        {
            if (node is not YamlMappingNode mappingNode)
            {
                result.Warnings.Add(
                    $"Skipped spawner YAML node at {FormatYamlNodeLocation(sourcePath, node.Start)}. Expected a list item object like '- prefab: Fox' but found {DescribeYamlNode(node)}.");
                continue;
            }

            try
            {
                string entryYaml = SerializeYamlNode(mappingNode);
                SpawnerConfigurationEntry entry =
                    Deserializer.Deserialize<SpawnerConfigurationEntry>(entryYaml) ?? new SpawnerConfigurationEntry();
                entry.SourceLine = checked((int)mappingNode.Start.Line);
                entry.SourceColumn = checked((int)mappingNode.Start.Column);
                result.Configuration.Add(entry);
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"Skipped invalid spawner entry at {FormatYamlNodeLocation(sourcePath, mappingNode.Start)}. {FormatEntryParseFailure(ex)}");
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

    private static void NormalizeEntry(SpawnerConfigurationEntry entry)
    {
        entry.Prefab = (entry.Prefab ?? "").Trim();
        entry.Location = NormalizeOptionalString(entry.Location);
        NormalizeSpawnerConditions(entry.Conditions, $"{entry.Prefab}.conditions", allowCreatureSpawnerRuntimeOverlapKeys: true);

        if (entry.SpawnArea != null)
        {
            if (entry.SpawnArea.MaxTotalSpawns.HasValue)
            {
                entry.SpawnArea.MaxTotalSpawns = ClampSpawnAreaMaxTotalSpawns(entry.SpawnArea.MaxTotalSpawns.Value);
            }

            if (entry.SpawnArea.Creatures != null)
            {
                for (int i = 0; i < entry.SpawnArea.Creatures.Count; i++)
                {
                    SpawnAreaSpawnDefinition spawn = entry.SpawnArea.Creatures[i];
                    spawn.Creature = (spawn.Creature ?? "").Trim();
                    spawn.Data = NormalizeOptionalString(spawn.Data);
                    spawn.Fields = NormalizeOptionalStringDictionary(spawn.Fields);
                    spawn.Objects = NormalizeOptionalStringList(spawn.Objects);
                    if (spawn.Level?.HasValues() == true)
                    {
                        spawn.MinLevel = RangeFormatting.GetMin(spawn.Level, spawn.MinLevel);
                        spawn.MaxLevel = RangeFormatting.GetMax(spawn.Level, spawn.MinLevel, spawn.MaxLevel);
                    }

                    spawn.Faction = FactionIntegration.Normalize(spawn.Faction);
                }
            }
        }

        if (entry.CreatureSpawner != null)
        {
            NormalizeCreatureSpawnerEntryConditions(entry.Conditions, $"{entry.Prefab}.conditions");

            if (entry.CreatureSpawner.Level?.HasValues() == true)
            {
                entry.CreatureSpawner.MinLevel = RangeFormatting.GetMin(entry.CreatureSpawner.Level, entry.CreatureSpawner.MinLevel);
                entry.CreatureSpawner.MaxLevel = RangeFormatting.GetMax(entry.CreatureSpawner.Level, entry.CreatureSpawner.MinLevel, entry.CreatureSpawner.MaxLevel);
            }

            entry.CreatureSpawner.Data = NormalizeOptionalString(entry.CreatureSpawner.Data);
            entry.CreatureSpawner.Fields = NormalizeOptionalStringDictionary(entry.CreatureSpawner.Fields);
            entry.CreatureSpawner.Objects = NormalizeOptionalStringList(entry.CreatureSpawner.Objects);
            entry.CreatureSpawner.Faction = FactionIntegration.Normalize(entry.CreatureSpawner.Faction);
            entry.CreatureSpawner.TimeOfDay?.Normalize();
            entry.CreatureSpawner.Creature = entry.CreatureSpawner.Creature?.Trim();
            entry.CreatureSpawner.RequiredGlobalKey = entry.CreatureSpawner.RequiredGlobalKey?.Trim();
            entry.CreatureSpawner.BlockingGlobalKey = entry.CreatureSpawner.BlockingGlobalKey?.Trim();
        }

        entry.RuleId = NormalizeOptionalRuleId(entry.RuleId) ?? BuildRuleId(entry);
    }

    private static string BuildRuleId(SpawnerConfigurationEntry entry)
    {
        SpawnerConfigurationEntry normalizedEntry = new()
        {
            Prefab = entry.Prefab,
            Enabled = true,
            Location = entry.Location,
            Conditions = entry.Conditions,
            SpawnArea = entry.SpawnArea,
            CreatureSpawner = entry.CreatureSpawner
        };

        return $"{entry.Prefab}:{NetworkPayloadSyncSupport.ComputeSpawnerEntryIdentitySignature(normalizedEntry)}";
    }

    private static string? NormalizeOptionalString(string? value)
    {
        if (value == null)
        {
            return null;
        }

        string normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
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

    private static void NormalizeSpawnerConditions(ConditionsDefinition? conditions, string context, bool allowCreatureSpawnerRuntimeOverlapKeys)
    {
        if (conditions == null)
        {
            return;
        }

        if (conditions.Locations?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.locations, but spawner entries use the top-level location selector. The key was ignored.");
            conditions.Locations = null;
        }

        if (conditions.Level?.HasValues() == true || conditions.MinLevel.HasValue)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.level, but level filters are not supported for spawner target conditions. The key was ignored.");
            conditions.Level = null;
            conditions.MinLevel = null;
        }

        if (conditions.MaxLevel.HasValue)
        {
            conditions.MaxLevel = null;
        }

        if (conditions.States?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.states, but state filters are only valid for character-drop conditions. The key was ignored.");
            conditions.States = null;
        }

        if (conditions.Factions?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.factions, but faction filters are only valid for character-drop conditions. The key was ignored.");
            conditions.Factions = null;
        }

        if (allowCreatureSpawnerRuntimeOverlapKeys)
        {
            return;
        }

        if (conditions.TimeOfDay != null)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.timeOfDay, but creatureSpawner uses creatureSpawner.timeOfDay for runtime time-of-day gating. The key was ignored.");
            conditions.TimeOfDay = null;
        }

        if (conditions.InsidePlayerBase.HasValue)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.insidePlayerBase, but creatureSpawner uses allowInsidePlayerBase for runtime player-base gating. The key was ignored.");
            conditions.InsidePlayerBase = null;
        }

        if (conditions.RequiredGlobalKeys?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.requiredGlobalKeys, but creatureSpawner uses requiredGlobalKey for runtime global-key gating. The key was ignored.");
            conditions.RequiredGlobalKeys = null;
        }

        if (conditions.ForbiddenGlobalKeys?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.forbiddenGlobalKeys, but creatureSpawner uses blockingGlobalKey for runtime global-key blocking. The key was ignored.");
            conditions.ForbiddenGlobalKeys = null;
        }
    }

    private static void NormalizeCreatureSpawnerEntryConditions(ConditionsDefinition? conditions, string context)
    {
        if (conditions == null)
        {
            return;
        }

        if (conditions.TimeOfDay != null)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.timeOfDay, but creatureSpawner uses creatureSpawner.timeOfDay for runtime time-of-day gating. The key was ignored.");
            conditions.TimeOfDay = null;
        }

        if (conditions.InsidePlayerBase.HasValue)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.insidePlayerBase, but creatureSpawner does not support inside-only top-level player-base gating. Use allowInsidePlayerBase for the runtime permission flag instead. The key was ignored.");
            conditions.InsidePlayerBase = null;
        }

        if (conditions.RequiredGlobalKeys?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.requiredGlobalKeys, but creatureSpawner uses requiredGlobalKey for runtime global-key gating. The key was ignored.");
            conditions.RequiredGlobalKeys = null;
        }

        if (conditions.ForbiddenGlobalKeys?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.forbiddenGlobalKeys, but creatureSpawner uses blockingGlobalKey for runtime global-key blocking. The key was ignored.");
            conditions.ForbiddenGlobalKeys = null;
        }
    }

    private static bool HasDuplicateSelector(List<SpawnerConfigurationEntry> entries, SpawnerConfigurationEntry candidate)
    {
        foreach (SpawnerConfigurationEntry existing in entries)
        {
            if (!string.Equals(existing.Location, candidate.Location, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool overlapsSpawnArea =
                existing.SpawnArea != null &&
                candidate.SpawnArea != null &&
                HasSpawnAreaOverride(existing.SpawnArea) &&
                HasSpawnAreaOverride(candidate.SpawnArea);

            bool overlapsCreatureSpawner =
                existing.CreatureSpawner != null &&
                candidate.CreatureSpawner != null &&
                HasCreatureSpawnerOverride(existing.CreatureSpawner) &&
                HasCreatureSpawnerOverride(candidate.CreatureSpawner);

            if (overlapsSpawnArea || overlapsCreatureSpawner)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateOverrideConfigurationPaths()
    {
        return DomainConfigurationFileSupport.EnumerateOverrideConfigurationPaths(
            "spawner",
            PrimaryOverrideConfigurationPathYml,
            PrimaryOverrideConfigurationPathYaml);
    }

    private static bool IsOverrideConfigurationFileName(string fileName)
    {
        return DomainConfigurationFileSupport.IsOverrideConfigurationFileName("spawner", fileName);
    }

    private static void CaptureSnapshotsIfNeeded()
    {
        if (_snapshotsCaptured)
        {
            return;
        }

        foreach (GameObject rootPrefab in EnumerateRootPrefabs())
        {
            CaptureSpawnAreaSnapshots(rootPrefab);
            CaptureCreatureSpawnerSnapshots(rootPrefab);
            CapturedRootPrefabNames.Add(rootPrefab.name);
        }

        _snapshotsCaptured = true;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Captured {SpawnAreaSnapshots.Count} SpawnArea snapshot(s) and {CreatureSpawnerSnapshots.Count} CreatureSpawner snapshot(s).");
    }

    private static void ResetReferenceSnapshots()
    {
        SpawnAreaSnapshots.Clear();
        CreatureSpawnerSnapshots.Clear();
        SpawnAreaSnapshotsByExactKey.Clear();
        CreatureSpawnerSnapshotsByExactKey.Clear();
        SpawnAreaSnapshotsByName.Clear();
        CreatureSpawnerSnapshotsByName.Clear();
        CapturedRootPrefabNames.Clear();
        _snapshotsCaptured = false;
    }

    private static void ResetRuntimeState(bool preserveLiveRegistries)
    {
        ClearQueuedReconcileState();
        LiveReconcilerState.Clear();
        LiveRegistryStore.ClearRuntimeView();
        SpawnAreaCatalogsByExactKey.Clear();
        CreatureSpawnerCatalogsByExactKey.Clear();
        SelectorCacheStore.Clear();
        RuntimeStateStore.Clear();
        ProvenanceRegistry.Clear(clearCurrentContexts: true);

        if (!preserveLiveRegistries)
        {
            LiveRegistryStore.ClearLiveRegistries();
            return;
        }

        RebuildTrackedSpawnerLocationBuckets();
    }

    private static void RebuildTrackedSpawnerLocationBuckets()
    {
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot = GetRuntimeConfigurationSnapshot();
        LiveRegistryStore.ForEachTrackedSpawnArea((spawnArea, prefabName) =>
        {
            if (spawnArea.gameObject == null || string.IsNullOrWhiteSpace(prefabName))
            {
                return;
            }

            RefreshSpawnAreaLocationBucketMembership(
                spawnArea,
                runtimeConfigurationSnapshot);
        });

        LiveRegistryStore.ForEachTrackedCreatureSpawner((creatureSpawner, prefabName) =>
        {
            if (creatureSpawner.gameObject == null || string.IsNullOrWhiteSpace(prefabName))
            {
                return;
            }

            RefreshCreatureSpawnerLocationBucketMembership(
                creatureSpawner,
                runtimeConfigurationSnapshot);
        });
    }

    private static void RefreshSnapshots()
    {
        ResetReferenceSnapshots();
        CaptureSnapshotsIfNeeded();
    }

    private static void EnsureSnapshotsCapturedForRootPrefab(string? rootPrefabName)
    {
        if (_snapshotsCaptured || string.IsNullOrWhiteSpace(rootPrefabName) || ZNetScene.instance == null)
        {
            return;
        }

        string normalizedRootPrefabName = rootPrefabName!;

        if (CapturedRootPrefabNames.Contains(normalizedRootPrefabName))
        {
            return;
        }

        GameObject? rootPrefab = ZNetScene.instance.GetPrefab(normalizedRootPrefabName);
        if (rootPrefab == null || rootPrefab.name.StartsWith(MockPrefabPrefix, StringComparison.OrdinalIgnoreCase))
        {
            CapturedRootPrefabNames.Add(normalizedRootPrefabName);
            return;
        }

        CaptureSpawnAreaSnapshots(rootPrefab);
        CaptureCreatureSpawnerSnapshots(rootPrefab);
        CapturedRootPrefabNames.Add(normalizedRootPrefabName);
    }

    private static IEnumerable<GameObject> EnumerateRootPrefabs()
    {
        HashSet<int> seen = new();
        if (ZNetScene.instance == null)
        {
            yield break;
        }

        foreach (GameObject prefab in ZNetScene.instance.m_prefabs)
        {
            if (prefab != null &&
                !prefab.name.StartsWith(MockPrefabPrefix, StringComparison.OrdinalIgnoreCase) &&
                seen.Add(prefab.GetInstanceID()))
            {
                yield return prefab;
            }
        }

        foreach (GameObject prefab in ZNetScene.instance.m_nonNetViewPrefabs)
        {
            if (prefab != null &&
                !prefab.name.StartsWith(MockPrefabPrefix, StringComparison.OrdinalIgnoreCase) &&
                seen.Add(prefab.GetInstanceID()))
            {
                yield return prefab;
            }
        }
    }

    private static void CaptureSpawnAreaSnapshots(GameObject rootPrefab)
    {
        foreach (SpawnArea spawnArea in rootPrefab.GetComponentsInChildren<SpawnArea>(true))
        {
            if (spawnArea == null || spawnArea.gameObject == null)
            {
                continue;
            }

            SpawnAreaComponentSnapshot snapshot = new()
            {
                Component = spawnArea,
                ConfigPrefabName = spawnArea.gameObject.name,
                RootPrefabName = rootPrefab.name,
                RelativePath = GetRelativePath(rootPrefab.transform, spawnArea.transform),
                LevelUpChance = spawnArea.m_levelupChance,
                SpawnInterval = spawnArea.m_spawnIntervalSec,
                TriggerDistance = spawnArea.m_triggerDistance,
                SetPatrolSpawnPoint = spawnArea.m_setPatrolSpawnPoint,
                SpawnRadius = spawnArea.m_spawnRadius,
                NearRadius = spawnArea.m_nearRadius,
                FarRadius = spawnArea.m_farRadius,
                MaxNear = spawnArea.m_maxNear,
                MaxTotal = spawnArea.m_maxTotal,
                OnGroundOnly = spawnArea.m_onGroundOnly,
                Prefabs = CloneSpawnAreaSnapshots(spawnArea.m_prefabs)
            };

            SpawnAreaSnapshots.Add(snapshot);
            SpawnAreaSnapshotsByExactKey[BuildExactKey(snapshot.RootPrefabName, snapshot.RelativePath, nameof(SpawnArea))] = snapshot;
            AddSnapshotByName(SpawnAreaSnapshotsByName, snapshot.ConfigPrefabName, snapshot);
        }
    }

    private static void CaptureCreatureSpawnerSnapshots(GameObject rootPrefab)
    {
        foreach (CreatureSpawner creatureSpawner in rootPrefab.GetComponentsInChildren<CreatureSpawner>(true))
        {
            if (creatureSpawner == null || creatureSpawner.gameObject == null)
            {
                continue;
            }

            CreatureSpawnerComponentSnapshot snapshot = new()
            {
                Component = creatureSpawner,
                ConfigPrefabName = creatureSpawner.gameObject.name,
                RootPrefabName = rootPrefab.name,
                RelativePath = GetRelativePath(rootPrefab.transform, creatureSpawner.transform),
                CreaturePrefab = creatureSpawner.m_creaturePrefab,
                MinLevel = creatureSpawner.m_minLevel,
                MaxLevel = creatureSpawner.m_maxLevel,
                LevelUpChance = creatureSpawner.m_levelupChance,
                RespawnTimeMinutes = creatureSpawner.m_respawnTimeMinuts,
                TriggerDistance = creatureSpawner.m_triggerDistance,
                TriggerNoise = creatureSpawner.m_triggerNoise,
                SpawnAtNight = creatureSpawner.m_spawnAtNight,
                SpawnAtDay = creatureSpawner.m_spawnAtDay,
                RequireSpawnArea = creatureSpawner.m_requireSpawnArea,
                SpawnInPlayerBase = creatureSpawner.m_spawnInPlayerBase,
                WakeUpAnimation = creatureSpawner.m_wakeUpAnimation,
                SpawnCheckInterval = creatureSpawner.m_spawnInterval,
                RequiredGlobalKey = creatureSpawner.m_requiredGlobalKey ?? "",
                BlockingGlobalKey = creatureSpawner.m_blockingGlobalKey ?? "",
                SetPatrolSpawnPoint = creatureSpawner.m_setPatrolSpawnPoint,
                SpawnGroupId = creatureSpawner.m_spawnGroupID,
                MaxGroupSpawned = creatureSpawner.m_maxGroupSpawned,
                SpawnGroupRadius = creatureSpawner.m_spawnGroupRadius,
                SpawnerWeight = creatureSpawner.m_spawnerWeight
            };

            CreatureSpawnerSnapshots.Add(snapshot);
            CreatureSpawnerSnapshotsByExactKey[BuildExactKey(snapshot.RootPrefabName, snapshot.RelativePath, nameof(CreatureSpawner))] = snapshot;
            AddSnapshotByName(CreatureSpawnerSnapshotsByName, snapshot.ConfigPrefabName, snapshot);
        }
    }

    private static List<SpawnAreaSpawnSnapshot> CloneSpawnAreaSnapshots(List<SpawnArea.SpawnData> prefabs)
    {
        List<SpawnAreaSpawnSnapshot> snapshots = new();
        if (prefabs == null)
        {
            return snapshots;
        }

        foreach (SpawnArea.SpawnData prefab in prefabs)
        {
            snapshots.Add(new SpawnAreaSpawnSnapshot
            {
                Prefab = prefab.m_prefab,
                Weight = prefab.m_weight,
                MinLevel = prefab.m_minLevel,
                MaxLevel = prefab.m_maxLevel
            });
        }

        return snapshots;
    }

    private static void ApplyIfReady(bool queueLiveReconcile = false)
    {
        if (!IsGameDataReady())
        {
            return;
        }

        bool synchronizedPayloadReady = Volatile.Read(ref _synchronizedPayloadReady);
        if (!StandardDomainApplySupport.CanApplySynchronizedDomain(synchronizedPayloadReady))
        {
            return;
        }

        HashSet<string> availablePrefabs = BuildCurrentSpawnerReferencePrefabKeys()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int gameDataSignature = ComputeGameDataSignature(availablePrefabs);
        bool domainEnabled = ShouldApplyLocally();
        Dictionary<string, string> currentEntrySignatures = CloneCurrentEntrySignaturesByPrefab();
        if (StandardDomainApplySupport.IsAlreadyApplied(
                _lastAppliedGameDataSignature,
                gameDataSignature,
                _lastAppliedDomainEnabled,
                domainEnabled,
                _lastAppliedConfigurationSignature,
                _configurationSignature,
                _lastAppliedSynchronizedPayloadReady,
                synchronizedPayloadReady))
        {
            return;
        }

        RunApplyCoordinator(availablePrefabs, gameDataSignature, domainEnabled, currentEntrySignatures, queueLiveReconcile);
    }

    private static void ValidateConfiguredPrefabs(HashSet<string> availablePrefabs)
    {
        foreach ((string prefabName, List<SpawnerConfigurationEntry> entries) in ActiveEntriesByPrefab)
        {
            if (availablePrefabs.Contains(prefabName))
            {
                continue;
            }

            foreach (SpawnerConfigurationEntry entry in entries)
            {
                WarnInvalidEntry($"Spawner prefab '{prefabName}' from {DescribeEntrySource(entry)} was not found among SpawnArea/CreatureSpawner prefabs.");
            }
        }
    }

    private static readonly Dictionary<string, string> EmptyEntrySignatures = new(StringComparer.OrdinalIgnoreCase);

    private static void RecordAppliedState(int gameDataSignature, bool domainEnabled, Dictionary<string, string> currentEntrySignatures)
    {
        _lastAppliedGameDataSignature = gameDataSignature;
        _lastAppliedDomainEnabled = domainEnabled;
        _lastAppliedConfigurationSignature = _configurationSignature;
        _lastAppliedSynchronizedPayloadReady = Volatile.Read(ref _synchronizedPayloadReady);
        ReplaceEntrySignatures(_lastAppliedEntrySignaturesByPrefab, currentEntrySignatures);
    }

    private static Dictionary<string, string> CloneCurrentEntrySignaturesByPrefab()
    {
        return new Dictionary<string, string>(CurrentEntrySignaturesByPrefab, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> BuildLastAppliedPrefabs()
    {
        HashSet<string> prefabs = new(StringComparer.OrdinalIgnoreCase);
        if (_lastAppliedDomainEnabled != true)
        {
            return prefabs;
        }

        foreach (string prefabName in _lastAppliedEntrySignaturesByPrefab.Keys)
        {
            prefabs.Add(prefabName);
        }

        return prefabs;
    }

    private static Dictionary<string, string> BuildActiveEntrySignaturesByPrefab()
    {
        return BuildActiveEntrySignaturesByPrefab(ActiveEntriesByPrefab);
    }

    private static Dictionary<string, string> BuildActiveEntrySignaturesByPrefab(
        Dictionary<string, List<SpawnerConfigurationEntry>> activeEntriesByPrefab)
    {
        return DomainEntrySignatureSupport.BuildSignaturesByKey(
            activeEntriesByPrefab,
            NetworkPayloadSyncSupport.ComputeSpawnerConfigurationSignature);
    }

    private static HashSet<string> BuildDirtyPrefabs(Dictionary<string, string> previous, Dictionary<string, string> current)
    {
        return DomainDictionaryDiffSupport.BuildDirtyKeys(previous, current);
    }

    private static HashSet<string> BuildRegisteredCatchupPrefabs(bool domainEnabled, Dictionary<string, string> currentEntrySignatures)
    {
        HashSet<string> prefabs = new(StringComparer.OrdinalIgnoreCase);
        if (domainEnabled)
        {
            foreach (string prefabName in currentEntrySignatures.Keys)
            {
                prefabs.Add(prefabName);
            }
        }

        if (_lastAppliedDomainEnabled == true)
        {
            foreach (string prefabName in _lastAppliedEntrySignaturesByPrefab.Keys)
            {
                prefabs.Add(prefabName);
            }
        }

        return prefabs;
    }

    private static void ReplaceEntrySignatures(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        DomainDictionaryDiffSupport.ReplaceEntries(target, source);
    }

    private static void ReapplyRegisteredLiveObjects(bool domainEnabled, HashSet<string> prefabs)
    {
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot = GetRuntimeConfigurationSnapshot();
        foreach (SpawnArea spawnArea in GetRegisteredSpawnAreas(prefabs, runtimeConfigurationSnapshot))
        {
            TrackSpawnAreaInstanceInternal(spawnArea);
            if (domainEnabled &&
                TryGetActiveSpawnAreaEntries(spawnArea, out IReadOnlyList<SpawnerRuntimeEntry>? entries, out _))
            {
                ReconcileSpawnAreaInstanceInternal(spawnArea, entries!);
                continue;
            }

            RestoreSpawnAreaInstance(spawnArea);
        }

        foreach (CreatureSpawner creatureSpawner in GetRegisteredCreatureSpawners(prefabs, runtimeConfigurationSnapshot))
        {
            TrackCreatureSpawnerInstanceInternal(creatureSpawner);
            if (domainEnabled &&
                TryGetActiveCreatureSpawnerEntries(creatureSpawner, out IReadOnlyList<SpawnerRuntimeEntry>? entries, out _))
            {
                ReconcileCreatureSpawnerInstanceInternal(creatureSpawner, entries!);
                continue;
            }

            RestoreCreatureSpawnerInstance(creatureSpawner, refreshRuntimeState: true);
        }
    }

    private static void ReapplyOrQueueRegisteredLiveObjects(bool domainEnabled, HashSet<string> prefabs)
    {
        SpawnerRuntimeConfigurationSnapshot runtimeConfigurationSnapshot = GetRuntimeConfigurationSnapshot();
        foreach (SpawnArea spawnArea in GetRegisteredSpawnAreas(prefabs, runtimeConfigurationSnapshot))
        {
            TrackSpawnAreaInstanceInternal(spawnArea);
            if (domainEnabled &&
                TryGetActiveSpawnAreaEntryCache(
                    spawnArea,
                    runtimeConfigurationSnapshot,
                    out MatchingEntryCache? entryCache,
                    out string configPrefabName))
            {
                if (runtimeConfigurationSnapshot.RuntimeConfiguredSpawnAreaPrefabs.Contains(configPrefabName))
                {
                    QueueSpawnAreaReconcile(spawnArea);
                    continue;
                }

                ReconcileSpawnAreaInstanceInternal(
                    spawnArea,
                    entryCache!.Entries,
                    entryCache);
                continue;
            }

            RestoreSpawnAreaInstance(spawnArea);
        }

        foreach (CreatureSpawner creatureSpawner in GetRegisteredCreatureSpawners(prefabs, runtimeConfigurationSnapshot))
        {
            TrackCreatureSpawnerInstanceInternal(creatureSpawner);
            if (domainEnabled &&
                TryGetActiveCreatureSpawnerEntryCache(
                    creatureSpawner,
                    runtimeConfigurationSnapshot,
                    out MatchingEntryCache? entryCache,
                    out string configPrefabName))
            {
                if (runtimeConfigurationSnapshot.RuntimeConfiguredCreatureSpawnerPrefabs.Contains(configPrefabName))
                {
                    QueueCreatureSpawnerReconcile(creatureSpawner);
                    continue;
                }

                ReconcileCreatureSpawnerInstanceInternal(
                    creatureSpawner,
                    entryCache!.Entries,
                    entryCache);
                continue;
            }

            RestoreCreatureSpawnerInstance(creatureSpawner, refreshRuntimeState: true);
        }
    }

    private static bool TryGetTargetedSelectorLocationKeys(
        SpawnerRuntimeConfigurationSnapshot? runtimeConfigurationSnapshot,
        string prefabName,
        bool forSpawnArea,
        out HashSet<string>? selectorLocationKeys)
    {
        selectorLocationKeys = null;
        if (runtimeConfigurationSnapshot == null ||
            string.IsNullOrWhiteSpace(prefabName) ||
            !runtimeConfigurationSnapshot.PlansByPrefab.TryGetValue(prefabName, out CompiledSpawnerPrefabPlan? prefabPlan))
        {
            return false;
        }

        if (forSpawnArea)
        {
            if (prefabPlan.HasUnscopedSpawnAreaEntries || prefabPlan.SpawnAreaSelectorLocationKeys.Count == 0)
            {
                return false;
            }

            selectorLocationKeys = prefabPlan.SpawnAreaSelectorLocationKeys;
            return true;
        }

        if (prefabPlan.HasUnscopedCreatureSpawnerEntries || prefabPlan.CreatureSpawnerSelectorLocationKeys.Count == 0)
        {
            return false;
        }

        selectorLocationKeys = prefabPlan.CreatureSpawnerSelectorLocationKeys;
        return true;
    }


    private static void RestoreSpawnArea(SpawnArea target, SpawnAreaComponentSnapshot snapshot)
    {
        ClearAppliedSpawnAreaPostSpawnOverrides(target);
        ClearAppliedSpawnAreaTotalSpawnLimit(target);
        RestoreSpawnAreaValues(
            target,
            snapshot.LevelUpChance,
            snapshot.SpawnInterval,
            snapshot.TriggerDistance,
            snapshot.SetPatrolSpawnPoint,
            snapshot.SpawnRadius,
            snapshot.NearRadius,
            snapshot.FarRadius,
            snapshot.MaxNear,
            snapshot.MaxTotal,
            snapshot.OnGroundOnly,
            snapshot.Prefabs);
    }

    private static void RestoreSpawnArea(SpawnArea target, SpawnAreaLiveSnapshot snapshot)
    {
        ClearAppliedSpawnAreaPostSpawnOverrides(target);
        ClearAppliedSpawnAreaTotalSpawnLimit(target);
        RestoreSpawnAreaValues(
            target,
            snapshot.LevelUpChance,
            snapshot.SpawnInterval,
            snapshot.TriggerDistance,
            snapshot.SetPatrolSpawnPoint,
            snapshot.SpawnRadius,
            snapshot.NearRadius,
            snapshot.FarRadius,
            snapshot.MaxNear,
            snapshot.MaxTotal,
            snapshot.OnGroundOnly,
            snapshot.Prefabs);
    }

    private static void RestoreCreatureSpawner(CreatureSpawner target, CreatureSpawnerComponentSnapshot snapshot)
    {
        LiveReconcilerState.ClearAppliedCreatureSpawnerOverrides(target);
        RestoreCreatureSpawnerValues(
            target,
            snapshot.CreaturePrefab,
            snapshot.MinLevel,
            snapshot.MaxLevel,
            snapshot.LevelUpChance,
            snapshot.RespawnTimeMinutes,
            snapshot.TriggerDistance,
            snapshot.TriggerNoise,
            snapshot.SpawnAtNight,
            snapshot.SpawnAtDay,
            snapshot.RequireSpawnArea,
            snapshot.SpawnInPlayerBase,
            snapshot.WakeUpAnimation,
            snapshot.SpawnCheckInterval,
            snapshot.RequiredGlobalKey,
            snapshot.BlockingGlobalKey,
            snapshot.SetPatrolSpawnPoint,
            snapshot.SpawnGroupId,
            snapshot.MaxGroupSpawned,
            snapshot.SpawnGroupRadius,
            snapshot.SpawnerWeight);
    }

    private static void RestoreCreatureSpawner(CreatureSpawner target, CreatureSpawnerLiveSnapshot snapshot)
    {
        LiveReconcilerState.ClearAppliedCreatureSpawnerOverrides(target);
        RestoreCreatureSpawnerValues(
            target,
            snapshot.CreaturePrefab,
            snapshot.MinLevel,
            snapshot.MaxLevel,
            snapshot.LevelUpChance,
            snapshot.RespawnTimeMinutes,
            snapshot.TriggerDistance,
            snapshot.TriggerNoise,
            snapshot.SpawnAtNight,
            snapshot.SpawnAtDay,
            snapshot.RequireSpawnArea,
            snapshot.SpawnInPlayerBase,
            snapshot.WakeUpAnimation,
            snapshot.SpawnCheckInterval,
            snapshot.RequiredGlobalKey,
            snapshot.BlockingGlobalKey,
            snapshot.SetPatrolSpawnPoint,
            snapshot.SpawnGroupId,
            snapshot.MaxGroupSpawned,
            snapshot.SpawnGroupRadius,
            snapshot.SpawnerWeight);
    }

    private static void RestoreSpawnAreaValues(
        SpawnArea target,
        float levelUpChance,
        float spawnInterval,
        float triggerDistance,
        bool setPatrolSpawnPoint,
        float spawnRadius,
        float nearRadius,
        float farRadius,
        int maxNear,
        int maxTotal,
        bool onGroundOnly,
        List<SpawnAreaSpawnSnapshot> prefabs)
    {
        target.m_levelupChance = levelUpChance;
        target.m_spawnIntervalSec = spawnInterval;
        target.m_triggerDistance = triggerDistance;
        target.m_setPatrolSpawnPoint = setPatrolSpawnPoint;
        target.m_spawnRadius = spawnRadius;
        target.m_nearRadius = nearRadius;
        target.m_farRadius = farRadius;
        target.m_maxNear = maxNear;
        target.m_maxTotal = maxTotal;
        target.m_onGroundOnly = onGroundOnly;
        target.m_prefabs = BuildSpawnAreaPrefabs(prefabs);
    }

    private static void RestoreCreatureSpawnerValues(
        CreatureSpawner target,
        GameObject? creaturePrefab,
        int minLevel,
        int maxLevel,
        float levelUpChance,
        float respawnTimeMinutes,
        float triggerDistance,
        float triggerNoise,
        bool spawnAtNight,
        bool spawnAtDay,
        bool requireSpawnArea,
        bool spawnInPlayerBase,
        bool wakeUpAnimation,
        int spawnCheckInterval,
        string requiredGlobalKey,
        string blockingGlobalKey,
        bool setPatrolSpawnPoint,
        int spawnGroupId,
        int maxGroupSpawned,
        float spawnGroupRadius,
        float spawnerWeight)
    {
        target.m_creaturePrefab = creaturePrefab;
        target.m_minLevel = minLevel;
        target.m_maxLevel = maxLevel;
        target.m_levelupChance = levelUpChance;
        target.m_respawnTimeMinuts = respawnTimeMinutes;
        target.m_triggerDistance = triggerDistance;
        target.m_triggerNoise = triggerNoise;
        target.m_spawnAtNight = spawnAtNight;
        target.m_spawnAtDay = spawnAtDay;
        target.m_requireSpawnArea = requireSpawnArea;
        target.m_spawnInPlayerBase = spawnInPlayerBase;
        target.m_wakeUpAnimation = wakeUpAnimation;
        target.m_spawnInterval = Math.Max(1, spawnCheckInterval);
        target.m_requiredGlobalKey = requiredGlobalKey;
        target.m_blockingGlobalKey = blockingGlobalKey;
        target.m_setPatrolSpawnPoint = setPatrolSpawnPoint;
        target.m_spawnGroupID = spawnGroupId;
        target.m_maxGroupSpawned = maxGroupSpawned;
        target.m_spawnGroupRadius = spawnGroupRadius;
        target.m_spawnerWeight = spawnerWeight;
    }

    private static void ApplySpawnArea(SpawnArea target, SpawnAreaDefinition definition, string context)
    {
        if (definition.LevelUpChance.HasValue)
        {
            target.m_levelupChance = Mathf.Max(0f, definition.LevelUpChance.Value);
        }

        if (definition.SpawnInterval.HasValue)
        {
            target.m_spawnIntervalSec = Mathf.Max(0f, definition.SpawnInterval.Value);
        }

        if (definition.TriggerDistance.HasValue)
        {
            target.m_triggerDistance = Mathf.Max(0f, definition.TriggerDistance.Value);
        }

        if (definition.SetPatrolSpawnPoint.HasValue)
        {
            target.m_setPatrolSpawnPoint = definition.SetPatrolSpawnPoint.Value;
        }

        if (definition.SpawnRadius.HasValue)
        {
            target.m_spawnRadius = Mathf.Max(0f, definition.SpawnRadius.Value);
        }

        if (definition.NearRadius.HasValue)
        {
            target.m_nearRadius = Mathf.Max(0f, definition.NearRadius.Value);
        }

        if (definition.FarRadius.HasValue)
        {
            target.m_farRadius = Mathf.Max(0f, definition.FarRadius.Value);
        }

        if (definition.MaxNear.HasValue)
        {
            target.m_maxNear = Math.Max(0, definition.MaxNear.Value);
        }

        if (definition.MaxTotal.HasValue)
        {
            target.m_maxTotal = Math.Max(0, definition.MaxTotal.Value);
        }

        if (definition.OnGroundOnly.HasValue)
        {
            target.m_onGroundOnly = definition.OnGroundOnly.Value;
        }

        List<SpawnAreaResolvedSpawnEntry>? resolvedSpawnEntries = null;
        if (definition.Creatures != null)
        {
            resolvedSpawnEntries = BuildResolvedSpawnAreaPrefabs(definition.Creatures, context);
            target.m_prefabs = resolvedSpawnEntries.Select(entry => entry.SpawnData).ToList();
        }

        UpdateAppliedSpawnAreaPostSpawnOverrides(target, definition, resolvedSpawnEntries);
    }

    private static void ApplyCreatureSpawner(CreatureSpawner target, CreatureSpawnerDefinition definition, string context)
    {
        if (definition.Creature != null)
        {
            string creatureName = definition.Creature.Trim();
            if (creatureName.Length > 0)
            {
                GameObject? creaturePrefab = ResolveCreaturePrefab(creatureName, context);
                if (creaturePrefab != null)
                {
                    target.m_creaturePrefab = creaturePrefab;
                }
            }
            else
            {
                WarnInvalidEntry($"Entry '{context}' set creature to an empty value. Leave the key out to keep the original creature.");
            }
        }

        ExpandWorldSpawnDataPayload? dataPayload = ExpandWorldSpawnDataSupport.BuildPayload(
            target.m_creaturePrefab,
            definition.Data,
            definition.Fields,
            definition.Objects,
            context);
        if (dataPayload != null)
        {
            LiveReconcilerState.SetAppliedCreatureSpawnerData(target, dataPayload);
        }
        else
        {
            LiveReconcilerState.RemoveAppliedCreatureSpawnerData(target);
        }

        if (definition.MinLevel.HasValue)
        {
            target.m_minLevel = Math.Max(1, definition.MinLevel.Value);
        }

        if (definition.MaxLevel.HasValue)
        {
            target.m_maxLevel = Math.Max(target.m_minLevel, Math.Max(1, definition.MaxLevel.Value));
        }

        if (definition.LevelUpChance.HasValue)
        {
            target.m_levelupChance = Mathf.Max(0f, definition.LevelUpChance.Value);
        }

        if (definition.RespawnTimeMinutes.HasValue)
        {
            target.m_respawnTimeMinuts = Mathf.Max(0f, definition.RespawnTimeMinutes.Value);
        }

        if (definition.TriggerDistance.HasValue)
        {
            target.m_triggerDistance = Mathf.Max(0f, definition.TriggerDistance.Value);
        }

        if (definition.TriggerNoise.HasValue)
        {
            target.m_triggerNoise = Mathf.Max(0f, definition.TriggerNoise.Value);
        }

        TimeOfDayDefinition? timeOfDay = GetConfiguredTimeOfDay(definition);
        if (timeOfDay != null)
        {
            TimeOfDayFormatting.GetBroadSpawnFlags(timeOfDay, out bool allowDay, out bool allowNight);
            target.m_spawnAtDay = allowDay;
            target.m_spawnAtNight = allowNight;
            if (timeOfDay.HasValues())
            {
                LiveReconcilerState.SetAppliedCreatureSpawnerTimeOfDay(target, timeOfDay);
            }
            else
            {
                LiveReconcilerState.RemoveAppliedCreatureSpawnerTimeOfDay(target);
            }
        }
        else
        {
            LiveReconcilerState.RemoveAppliedCreatureSpawnerTimeOfDay(target);
        }

        if (definition.RequireSpawnArea.HasValue)
        {
            target.m_requireSpawnArea = definition.RequireSpawnArea.Value;
        }

        if (definition.AllowInsidePlayerBase.HasValue)
        {
            target.m_spawnInPlayerBase = definition.AllowInsidePlayerBase.Value;
        }

        if (definition.WakeUpAnimation.HasValue)
        {
            target.m_wakeUpAnimation = definition.WakeUpAnimation.Value;
        }

        if (definition.SpawnCheckInterval.HasValue)
        {
            target.m_spawnInterval = Math.Max(1, definition.SpawnCheckInterval.Value);
        }

        if (definition.RequiredGlobalKey != null)
        {
            target.m_requiredGlobalKey = definition.RequiredGlobalKey;
        }

        if (definition.BlockingGlobalKey != null)
        {
            target.m_blockingGlobalKey = definition.BlockingGlobalKey;
        }

        if (definition.SetPatrolSpawnPoint.HasValue)
        {
            target.m_setPatrolSpawnPoint = definition.SetPatrolSpawnPoint.Value;
        }

        if (definition.SpawnGroupId.HasValue)
        {
            target.m_spawnGroupID = definition.SpawnGroupId.Value;
        }

        if (definition.MaxGroupSpawned.HasValue)
        {
            target.m_maxGroupSpawned = Math.Max(0, definition.MaxGroupSpawned.Value);
        }

        if (definition.SpawnGroupRadius.HasValue)
        {
            target.m_spawnGroupRadius = Mathf.Max(0f, definition.SpawnGroupRadius.Value);
        }

        if (definition.SpawnerWeight.HasValue)
        {
            target.m_spawnerWeight = Mathf.Max(0f, definition.SpawnerWeight.Value);
        }

        if (FactionIntegration.HasFaction(definition.Faction))
        {
            LiveReconcilerState.SetAppliedCreatureSpawnerFaction(target, definition.Faction!);
        }
        else
        {
            LiveReconcilerState.RemoveAppliedCreatureSpawnerFaction(target);
        }

    }

    private static bool HasSpawnAreaOverride(SpawnAreaDefinition? definition)
    {
        return definition != null &&
               (definition.LevelUpChance.HasValue ||
                definition.SpawnInterval.HasValue ||
                definition.TriggerDistance.HasValue ||
                definition.SetPatrolSpawnPoint.HasValue ||
                definition.SpawnRadius.HasValue ||
                definition.NearRadius.HasValue ||
                definition.FarRadius.HasValue ||
                definition.MaxNear.HasValue ||
                definition.MaxTotal.HasValue ||
                definition.MaxTotalSpawns.HasValue ||
                definition.OnGroundOnly.HasValue ||
                definition.Creatures != null);
    }

    private static bool HasCreatureSpawnerOverride(CreatureSpawnerDefinition? definition)
    {
        return definition != null &&
               (FactionIntegration.HasFaction(definition.Faction) ||
                definition.Data != null ||
                definition.Fields != null ||
                definition.Objects != null ||
                definition.TimeOfDay != null ||
                definition.Creature != null ||
                definition.MinLevel.HasValue ||
                definition.MaxLevel.HasValue ||
                definition.LevelUpChance.HasValue ||
                definition.RespawnTimeMinutes.HasValue ||
                definition.TriggerDistance.HasValue ||
                definition.TriggerNoise.HasValue ||
                definition.RequireSpawnArea.HasValue ||
                definition.AllowInsidePlayerBase.HasValue ||
                definition.WakeUpAnimation.HasValue ||
                definition.SpawnCheckInterval.HasValue ||
                definition.RequiredGlobalKey != null ||
                definition.BlockingGlobalKey != null ||
                definition.SetPatrolSpawnPoint.HasValue ||
                definition.SpawnGroupId.HasValue ||
                definition.MaxGroupSpawned.HasValue ||
                definition.SpawnGroupRadius.HasValue ||
                definition.SpawnerWeight.HasValue);
    }

    private static bool HasEntryConditions(SpawnerConfigurationEntry? entry)
    {
        return entry != null && DropConditionEvaluator.HasConditions(entry.Conditions);
    }

    private static bool HasDynamicEntryConditions(SpawnerConfigurationEntry? entry)
    {
        return entry != null && DropConditionEvaluator.HasDynamicConditions(entry.Conditions);
    }

    private static TimeOfDayDefinition? GetConfiguredTimeOfDay(CreatureSpawnerDefinition? definition)
    {
        if (definition == null)
        {
            return null;
        }

        if (definition.TimeOfDay != null)
        {
            return definition.TimeOfDay;
        }

        return null;
    }

    private static bool TryGetActiveSpawnAreaEntries(SpawnArea? spawnArea, out IReadOnlyList<SpawnerRuntimeEntry>? entries, out string configPrefabName)
    {
        entries = null;
        if (!TryGetActiveSpawnAreaEntryCache(spawnArea, out MatchingEntryCache? entryCache, out configPrefabName))
        {
            return false;
        }

        entries = entryCache!.Entries;
        return true;
    }

    private static bool TryGetActiveCreatureSpawnerEntries(CreatureSpawner? creatureSpawner, out IReadOnlyList<SpawnerRuntimeEntry>? entries, out string configPrefabName)
    {
        entries = null;
        if (!TryGetActiveCreatureSpawnerEntryCache(creatureSpawner, out MatchingEntryCache? entryCache, out configPrefabName))
        {
            return false;
        }

        entries = entryCache!.Entries;
        return true;
    }

    private static bool ShouldRuntimeReconcile(SpawnerConfigurationEntry? entry)
    {
        return entry != null && HasDynamicEntryConditions(entry);
    }

    private static bool HasAnySpawnAreaSpawnFaction(List<SpawnAreaSpawnDefinition>? prefabs)
    {
        return prefabs?.Any(creature => FactionIntegration.HasFaction(creature.Faction)) == true;
    }

    private static List<SpawnArea.SpawnData> BuildSpawnAreaPrefabs(List<SpawnAreaSpawnSnapshot> snapshots)
    {
        return snapshots
            .Select(snapshot => new SpawnArea.SpawnData
            {
                m_prefab = snapshot.Prefab,
                m_weight = snapshot.Weight,
                m_minLevel = snapshot.MinLevel,
                m_maxLevel = snapshot.MaxLevel
            })
            .ToList();
    }

    private static List<SpawnAreaResolvedSpawnEntry> BuildResolvedSpawnAreaPrefabs(List<SpawnAreaSpawnDefinition> definitions, string context)
    {
        List<SpawnAreaResolvedSpawnEntry> prefabs = new();
        for (int i = 0; i < definitions.Count; i++)
        {
            SpawnAreaSpawnDefinition definition = definitions[i];

            string prefabName = (definition.Creature ?? "").Trim();
            if (prefabName.Length == 0)
            {
                WarnInvalidEntry($"Entry '{context}' contains a SpawnArea creature entry without a creature name.");
                continue;
            }

            GameObject? spawnPrefab = ResolveCreaturePrefab(prefabName, context);
            if (spawnPrefab == null)
            {
                continue;
            }

            int minLevel = Math.Max(1, definition.MinLevel ?? 1);
            prefabs.Add(new SpawnAreaResolvedSpawnEntry
            {
                SpawnData = new SpawnArea.SpawnData
                {
                    m_prefab = spawnPrefab,
                    m_weight = Mathf.Max(0f, definition.Weight ?? 1f),
                    m_minLevel = minLevel,
                    m_maxLevel = Math.Max(minLevel, definition.MaxLevel ?? minLevel)
                },
                Definition = definition,
                DataPayload = ExpandWorldSpawnDataSupport.BuildPayload(
                    spawnPrefab,
                    definition.Data,
                    definition.Fields,
                    definition.Objects,
                    $"{context}.spawnArea.creatures[{i}]")
            });
        }

        return prefabs;
    }

    private static void UpdateAppliedSpawnAreaPostSpawnOverrides(SpawnArea target, SpawnAreaDefinition definition, List<SpawnAreaResolvedSpawnEntry>? resolvedSpawnEntries)
    {
        ClearAppliedSpawnAreaPostSpawnOverrides(target);

        if (!HasAnySpawnAreaSpawnFaction(definition.Creatures) &&
            !HasAnySpawnAreaSpawnData(definition.Creatures))
        {
            return;
        }

        List<SpawnArea.SpawnData> livePrefabs = target.m_prefabs ?? new List<SpawnArea.SpawnData>();
        LiveReconcilerState.SetAppliedSpawnAreaPrefabs(target, livePrefabs.ToList());

        if (resolvedSpawnEntries != null && resolvedSpawnEntries.Count > 0)
        {
            foreach (SpawnAreaResolvedSpawnEntry resolvedEntry in resolvedSpawnEntries)
            {
                string? effectiveFaction = FactionIntegration.Normalize(resolvedEntry.Definition.Faction);
                if (FactionIntegration.HasFaction(effectiveFaction))
                {
                    LiveReconcilerState.SetAppliedSpawnAreaFaction(resolvedEntry.SpawnData, effectiveFaction!);
                }

                if (resolvedEntry.DataPayload != null)
                {
                    LiveReconcilerState.SetAppliedSpawnAreaData(resolvedEntry.SpawnData, resolvedEntry.DataPayload);
                }
            }
        }
    }

    private static void ClearAppliedSpawnAreaPostSpawnOverrides(SpawnArea? target)
    {
        if (!LiveReconcilerState.TryTakeAppliedSpawnAreaPrefabs(target, out List<SpawnArea.SpawnData> previousPrefabs))
        {
            return;
        }

        foreach (SpawnArea.SpawnData previousPrefab in previousPrefabs)
        {
            LiveReconcilerState.RemoveAppliedSpawnAreaData(previousPrefab);
            LiveReconcilerState.RemoveAppliedSpawnAreaFaction(previousPrefab);
        }
    }

    private static bool HasAppliedSpawnAreaTrackedCustomizations(SpawnArea spawnArea)
    {
        return LiveReconcilerState.TryGetAppliedSpawnAreaPrefabs(spawnArea, out List<SpawnArea.SpawnData> prefabs) &&
               prefabs.Any(prefab =>
                   LiveReconcilerState.HasAppliedSpawnAreaData(prefab) ||
                   LiveReconcilerState.HasAppliedSpawnAreaFaction(prefab));
    }

    private static bool RequiresSpawnAreaPostSpawnTracking(SpawnArea.SpawnData? spawnData)
    {
        return spawnData != null &&
               LiveReconcilerState.HasAppliedSpawnAreaFaction(spawnData);
    }

    private static void MaybeRefreshCreatureSpawnerSchedule(CreatureSpawner creatureSpawner, int previousInterval)
    {
        int instanceId = creatureSpawner.GetInstanceID();
        int effectiveInterval = Math.Max(1, creatureSpawner.m_spawnInterval);
        creatureSpawner.m_spawnInterval = effectiveInterval;

        bool shouldRefresh = previousInterval != effectiveInterval;
        if (LiveReconcilerState.TryGetAppliedCreatureSpawnerCheckInterval(instanceId, out int lastAppliedInterval))
        {
            shouldRefresh |= lastAppliedInterval != effectiveInterval;
        }

        LiveReconcilerState.SetAppliedCreatureSpawnerCheckInterval(instanceId, effectiveInterval);
        if (!shouldRefresh)
        {
            return;
        }

        RefreshCreatureSpawnerSchedule(creatureSpawner);
    }

    private static void RefreshCreatureSpawnerSchedule(CreatureSpawner creatureSpawner)
    {
        creatureSpawner.CancelInvoke("UpdateSpawner");
        if (!creatureSpawner.isActiveAndEnabled || !creatureSpawner.gameObject.activeInHierarchy)
        {
            return;
        }

        if (!creatureSpawner.TryGetComponent(out ZNetView? nview) || nview == null)
        {
            return;
        }

        if (nview.GetZDO() == null)
        {
            return;
        }

        float interval = Mathf.Max(1f, creatureSpawner.m_spawnInterval);
        creatureSpawner.InvokeRepeating("UpdateSpawner", UnityEngine.Random.Range(interval / 2f, interval), interval);
    }

    private static void MaybeResetCreatureSpawnerCaches(CreatureSpawner creatureSpawner, int previousGroupId, int previousMaxGroupSpawned, float previousGroupRadius, float previousSpawnerWeight)
    {
        if (previousGroupId == creatureSpawner.m_spawnGroupID &&
            previousMaxGroupSpawned == creatureSpawner.m_maxGroupSpawned &&
            Mathf.Approximately(previousGroupRadius, creatureSpawner.m_spawnGroupRadius) &&
            Mathf.Approximately(previousSpawnerWeight, creatureSpawner.m_spawnerWeight))
        {
            return;
        }

        CreatureSpawnerCheckedLocationField?.SetValue(creatureSpawner, false);
        CreatureSpawnerLocationField?.SetValue(creatureSpawner, null);
        CreatureSpawnerSpawnGroupField?.SetValue(creatureSpawner, null);
    }

    private static GameObject? ResolveCreaturePrefab(string prefabName, string context)
    {
        GameObject? prefab = ZNetScene.instance?.GetPrefab(prefabName);
        if (prefab == null)
        {
            WarnInvalidEntry($"Entry '{context}' references unknown creature prefab '{prefabName}'.");
            return null;
        }

        if (!prefab.TryGetComponent(out Character _) && !prefab.TryGetComponent(out BaseAI _))
        {
            WarnInvalidEntry($"Entry '{context}' references '{prefabName}', but it is not a creature prefab.");
            return null;
        }

        return prefab;
    }

    private static bool TryGetExactContext(GameObject gameObject, string componentType, out string exactKey)
    {
        return TryGetExactContext(gameObject, componentType, out exactKey, out _);
    }

    private static bool TryGetExactContext(GameObject gameObject, string componentType, out string exactKey, out string rootPrefabName)
    {
        exactKey = "";
        rootPrefabName = "";
        if (gameObject == null)
        {
            return false;
        }

        Transform root = GetRootTransform(gameObject.transform);
        rootPrefabName = GetResolvedPrefabName(root.gameObject);
        if (rootPrefabName.Length == 0)
        {
            return false;
        }

        exactKey = BuildExactKey(rootPrefabName, GetRelativePath(root, gameObject.transform), componentType);
        return true;
    }

    private static bool TryGetLiveLocationContext(GameObject gameObject, out string locationPrefab, out string relativePath)
    {
        return TryGetLiveLocationContext(gameObject, out locationPrefab, out relativePath, out _);
    }

    private static bool TryGetLiveLocationContext(GameObject gameObject, out string locationPrefab, out string relativePath, out string sourceLabel)
    {
        locationPrefab = "";
        relativePath = "";
        sourceLabel = "";
        if (gameObject == null)
        {
            return false;
        }

        if (TryGetRecordedLocationContext(gameObject, out locationPrefab, out relativePath))
        {
            sourceLabel = "Provenance";
            return true;
        }

        if (TryGetCurrentLocationSpawnContext(gameObject, out locationPrefab, out relativePath))
        {
            sourceLabel = "SpawnLocationContext";
            return true;
        }

        if (TryGetLiveLocationProxyContext(gameObject, out locationPrefab, out relativePath))
        {
            sourceLabel = nameof(LocationProxy);
            return true;
        }

        if (TryGetDirectLocationContext(gameObject, out locationPrefab, out relativePath))
        {
            sourceLabel = nameof(Location);
            return true;
        }

        if (TryGetStaticLocationContext(gameObject, out locationPrefab, out relativePath))
        {
            sourceLabel = "LocationStatic";
            return true;
        }

        if (TryGetZoneLocationContext(gameObject, out locationPrefab))
        {
            sourceLabel = "LocationZone";
            relativePath = "";
            return true;
        }

        if (TryPromoteSpatialContextToRecordedProvenance(gameObject, out locationPrefab, out relativePath))
        {
            sourceLabel = "LocationRadius";
            return true;
        }

        return false;
    }

    private static GameObject? ResolveCurrentInspectionTarget()
    {
        GameObject? hoverObject = Player.m_localPlayer?.GetHoverObject();
        if (TryResolveInspectionTargetFromObject(hoverObject, out GameObject? target))
        {
            return target;
        }

        Vector3 probePoint = Player.m_localPlayer != null
            ? Player.m_localPlayer.transform.position + Player.m_localPlayer.transform.forward * 5f
            : Vector3.zero;

        if (GameCamera.instance != null &&
            Physics.Raycast(GameCamera.instance.transform.position, GameCamera.instance.transform.forward, out RaycastHit hitInfo, 100f))
        {
            GameObject? hitObject = hitInfo.collider?.attachedRigidbody != null
                ? hitInfo.collider.attachedRigidbody.gameObject
                : hitInfo.collider?.gameObject;
            if (TryResolveInspectionTargetFromObject(hitObject, out target))
            {
                return target;
            }

            probePoint = hitInfo.point;
        }

        return TryFindNearestInspectionTarget(probePoint, out target) ? target : null;
    }

    private static bool TryResolveInspectionTargetFromObject(GameObject? sourceObject, out GameObject? targetObject)
    {
        targetObject = null;
        if (sourceObject == null)
        {
            return false;
        }

        SpawnArea? spawnArea = sourceObject.GetComponent<SpawnArea>() ?? sourceObject.GetComponentInParent<SpawnArea>(true);
        CreatureSpawner? creatureSpawner = sourceObject.GetComponent<CreatureSpawner>() ?? sourceObject.GetComponentInParent<CreatureSpawner>(true);
        if (spawnArea == null && creatureSpawner == null)
        {
            return false;
        }

        if (spawnArea != null && creatureSpawner == null)
        {
            targetObject = spawnArea.gameObject;
            return true;
        }

        if (creatureSpawner != null && spawnArea == null)
        {
            targetObject = creatureSpawner.gameObject;
            return true;
        }

        int spawnAreaDepth = GetAncestorDepth(sourceObject.transform, spawnArea!.transform);
        int creatureSpawnerDepth = GetAncestorDepth(sourceObject.transform, creatureSpawner!.transform);
        targetObject = spawnAreaDepth <= creatureSpawnerDepth ? spawnArea.gameObject : creatureSpawner.gameObject;
        return true;
    }

    private static int GetAncestorDepth(Transform source, Transform ancestor)
    {
        int depth = 0;
        Transform? current = source;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return depth;
            }

            current = current.parent;
            depth++;
        }

        return int.MaxValue;
    }

    private static bool TryFindNearestInspectionTarget(Vector3 probePoint, out GameObject? targetObject)
    {
        targetObject = null;
        float bestDistanceSquared = 8f * 8f;

        foreach (SpawnArea spawnArea in UnityEngine.Object.FindObjectsByType<SpawnArea>(FindObjectsSortMode.None))
        {
            if (spawnArea == null || spawnArea.gameObject == null)
            {
                continue;
            }

            float distanceSquared = (spawnArea.transform.position - probePoint).sqrMagnitude;
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            targetObject = spawnArea.gameObject;
        }

        foreach (CreatureSpawner creatureSpawner in UnityEngine.Object.FindObjectsByType<CreatureSpawner>(FindObjectsSortMode.None))
        {
            if (creatureSpawner == null || creatureSpawner.gameObject == null)
            {
                continue;
            }

            float distanceSquared = (creatureSpawner.transform.position - probePoint).sqrMagnitude;
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            targetObject = creatureSpawner.gameObject;
        }

        return targetObject != null;
    }

    private static List<string> BuildInspectionLines(GameObject targetObject)
    {
        List<string> lines = new();
        if (targetObject == null)
        {
            return lines;
        }

        if (targetObject.TryGetComponent(out SpawnArea spawnArea))
        {
            AppendSpawnAreaInspectionLines(lines, spawnArea);
            return lines;
        }

        if (targetObject.TryGetComponent(out CreatureSpawner creatureSpawner))
        {
            AppendCreatureSpawnerInspectionLines(lines, creatureSpawner);
        }

        return lines;
    }

    private static void AppendSpawnAreaInspectionLines(List<string> lines, SpawnArea spawnArea)
    {
        string configPrefabName = GetConfigPrefabName(spawnArea.gameObject, nameof(SpawnArea));
        lines.Add("Spawner Inspect: SpawnArea");
        lines.Add($"Object: {configPrefabName}@{DescribeInstance(spawnArea.gameObject)}");
        AppendResolvedLocationLines(lines, spawnArea.gameObject, spawnArea, null);

        List<SpawnerConfigurationEntry> configuredEntries = ActiveEntriesByPrefab.TryGetValue(configPrefabName, out List<SpawnerConfigurationEntry>? entries)
            ? entries.Where(entry => entry.SpawnArea != null && HasSpawnAreaOverride(entry.SpawnArea)).ToList()
            : new List<SpawnerConfigurationEntry>();
        lines.Add($"Configured entries: {configuredEntries.Count}");

        bool hasMatchingEntries = TryGetActiveSpawnAreaEntryCache(spawnArea, out MatchingEntryCache? entryCache, out _);
        lines.Add($"Selector-matching entries: {(hasMatchingEntries ? entryCache!.Entries.Count : 0)}");
        IReadOnlyList<SpawnerRuntimeEntry> matchingEntries = hasMatchingEntries
            ? entryCache!.Entries
            : new List<SpawnerRuntimeEntry>();
        if (matchingEntries.Count > 0 &&
            TrySelectWinningSpawnerEntry(spawnArea.gameObject, matchingEntries, forSpawnArea: true, out SpawnerRuntimeEntry? winningEntry) &&
            winningEntry != null)
        {
            lines.Add($"Winning entry: {FormatInspectionEntrySummary(winningEntry)}");
        }
        else
        {
            lines.Add("Winning entry: none");
        }
    }

    private static void AppendCreatureSpawnerInspectionLines(List<string> lines, CreatureSpawner creatureSpawner)
    {
        string configPrefabName = GetConfigPrefabName(creatureSpawner.gameObject, nameof(CreatureSpawner));
        lines.Add("Spawner Inspect: CreatureSpawner");
        lines.Add($"Object: {configPrefabName}@{DescribeInstance(creatureSpawner.gameObject)}");
        AppendResolvedLocationLines(lines, creatureSpawner.gameObject, null, creatureSpawner);

        List<SpawnerConfigurationEntry> configuredEntries = ActiveEntriesByPrefab.TryGetValue(configPrefabName, out List<SpawnerConfigurationEntry>? entries)
            ? entries.Where(entry => entry.CreatureSpawner != null && HasCreatureSpawnerOverride(entry.CreatureSpawner)).ToList()
            : new List<SpawnerConfigurationEntry>();
        lines.Add($"Configured entries: {configuredEntries.Count}");

        bool hasMatchingEntries = TryGetActiveCreatureSpawnerEntryCache(creatureSpawner, out MatchingEntryCache? entryCache, out _);
        lines.Add($"Selector-matching entries: {(hasMatchingEntries ? entryCache!.Entries.Count : 0)}");
        IReadOnlyList<SpawnerRuntimeEntry> matchingEntries = hasMatchingEntries
            ? entryCache!.Entries
            : new List<SpawnerRuntimeEntry>();
        if (matchingEntries.Count > 0 &&
            TrySelectWinningSpawnerEntry(creatureSpawner.gameObject, matchingEntries, forSpawnArea: false, out SpawnerRuntimeEntry? winningEntry) &&
            winningEntry != null)
        {
            lines.Add($"Winning entry: {FormatInspectionEntrySummary(winningEntry)}");
        }
        else
        {
            lines.Add("Winning entry: none");
        }
    }

    private static void AppendResolvedLocationLines(List<string> lines, GameObject gameObject, SpawnArea? spawnArea, CreatureSpawner? creatureSpawner)
    {
        if (TryGetLiveLocationContext(gameObject, out string locationPrefab, out string relativePath, out string sourceLabel))
        {
            lines.Add($"Resolved location: {locationPrefab}");
            lines.Add($"Resolved path: {(relativePath.Length > 0 ? relativePath : "(unavailable)")}");
            lines.Add($"Resolution source: {sourceLabel}");
        }
        else
        {
            lines.Add("Resolved location: unavailable");
            lines.Add("Resolved path: unavailable");
            lines.Add("Resolution source: unavailable");
        }

        AppendLocationFallbackDiagnostics(lines, gameObject);

        SpawnerLocationProvenance? provenance = null;
        if (spawnArea != null)
        {
            ProvenanceRegistry.TryGetSpawnAreaProvenance(spawnArea, out provenance);
        }
        else if (creatureSpawner != null)
        {
            ProvenanceRegistry.TryGetCreatureSpawnerProvenance(creatureSpawner, out provenance);
        }

        if (provenance != null)
        {
            lines.Add($"Recorded provenance: location={provenance.LocationPrefab}, path={provenance.RelativePath}");
        }
    }

    private static void AppendLocationFallbackDiagnostics(List<string> lines, GameObject gameObject)
    {
        if (TryGetStaticLocationContext(gameObject, out string staticLocation, out _))
        {
            lines.Add($"Static location: {staticLocation}");
        }

        if (TryGetZoneLocationContext(gameObject, out string zoneLocation))
        {
            lines.Add($"Zone location: {zoneLocation}");
        }

        if (TryGetSpatialLocationContext(gameObject, out string radiusLocation))
        {
            lines.Add($"Radius location: {radiusLocation}");
        }
    }

    private static string FormatInspectionEntrySummary(SpawnerConfigurationEntry entry)
    {
        if (entry == null)
        {
            return "(null)";
        }

        string selector = entry.Location != null
            ? $"location={entry.Location}"
            : "prefab-only";
        if (entry.CreatureSpawner != null)
        {
            return $"{selector}, creatureSpawner.creature={entry.CreatureSpawner.Creature ?? "(null)"}";
        }

        if (entry.SpawnArea?.Creatures != null)
        {
            return $"{selector}, spawnArea.creatures={entry.SpawnArea.Creatures.Count}";
        }

        return selector;
    }

    private static string FormatInspectionEntrySummary(SpawnerRuntimeEntry entry)
    {
        if (entry == null)
        {
            return "(null)";
        }

        string selector = entry.Location.Length > 0
            ? $"location={entry.Location}"
            : "prefab-only";
        if (entry.CreatureSpawner != null)
        {
            return $"{selector}, creatureSpawner.creature={entry.CreatureSpawner.Creature ?? "(null)"}";
        }

        if (entry.SpawnArea?.Creatures != null)
        {
            return $"{selector}, spawnArea.creatures={entry.SpawnArea.Creatures.Count}";
        }

        return selector;
    }

    private static string BuildExactKey(string rootPrefabName, string relativePath, string componentType)
    {
        return $"{rootPrefabName}|{relativePath}|{componentType}";
    }

    private static string GetRelativePath(Transform root, Transform target)
    {
        if (target == root)
        {
            return ".";
        }

        List<string> segments = new();
        Transform? current = target;
        while (current != null && current != root)
        {
            segments.Add($"{current.name}[{GetSameNameSiblingIndex(current)}]");
            current = current.parent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static int GetSameNameSiblingIndex(Transform transform)
    {
        if (transform.parent == null)
        {
            return 0;
        }

        int index = 0;
        foreach (Transform sibling in transform.parent)
        {
            if (ReferenceEquals(sibling, transform))
            {
                break;
            }

            if (string.Equals(sibling.name, transform.name, StringComparison.Ordinal))
            {
                index++;
            }
        }

        return index;
    }

    private static string DescribeInstance(GameObject gameObject)
    {
        Transform root = GetRootTransform(gameObject.transform);
        return $"{GetResolvedPrefabName(root.gameObject)}/{GetRelativePath(root, gameObject.transform)}";
    }

    private static string GetConfigPrefabName(GameObject gameObject, string componentType)
    {
        if (TryGetExactContext(gameObject, componentType, out string exactKey, out string rootPrefabName))
        {
            if (componentType == nameof(SpawnArea) &&
                SpawnAreaCatalogsByExactKey.TryGetValue(exactKey, out SpawnAreaComponentCatalog? spawnAreaCatalog))
            {
                return spawnAreaCatalog.ConfigPrefabName;
            }

            if (componentType == nameof(CreatureSpawner) &&
                CreatureSpawnerCatalogsByExactKey.TryGetValue(exactKey, out CreatureSpawnerComponentCatalog? creatureSpawnerCatalog))
            {
                return creatureSpawnerCatalog.ConfigPrefabName;
            }

            string configPrefabName = GetLiveComponentPrefabName(gameObject);
            string relativePath = GetRelativePath(GetRootTransform(gameObject.transform), gameObject.transform);
            if (componentType == nameof(SpawnArea))
            {
                SpawnAreaCatalogsByExactKey[exactKey] = new SpawnAreaComponentCatalog
                {
                    ConfigPrefabName = configPrefabName,
                    RootPrefabName = rootPrefabName,
                    RelativePath = relativePath
                };
            }
            else if (componentType == nameof(CreatureSpawner))
            {
                CreatureSpawnerCatalogsByExactKey[exactKey] = new CreatureSpawnerComponentCatalog
                {
                    ConfigPrefabName = configPrefabName,
                    RootPrefabName = rootPrefabName,
                    RelativePath = relativePath
                };
            }

            return configPrefabName;
        }

        return GetLiveComponentPrefabName(gameObject);
    }

    private static string GetLiveComponentPrefabName(GameObject? gameObject)
    {
        string byObjectName = TrimCloneSuffix(gameObject?.name ?? "");
        if (byObjectName.Length > 0)
        {
            return byObjectName;
        }

        return GetResolvedPrefabName(gameObject);
    }

    private static string GetLocationReferencePrefabName(GameObject? gameObject)
    {
        string resolvedPrefabName = NormalizeLocationReferencePrefabName(GetResolvedPrefabName(gameObject));
        if (resolvedPrefabName.Length > 0)
        {
            return resolvedPrefabName;
        }

        return NormalizeLocationReferencePrefabName(GetLiveComponentPrefabName(gameObject));
    }

    private static string GetResolvedPrefabName(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return "";
        }

        ZNetView? nview = gameObject.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (zdo != null && ZNetScene.instance != null)
        {
            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab != null)
            {
                return prefab.name;
            }
        }

        string prefabName = Utils.GetPrefabName(gameObject);
        if (!string.IsNullOrWhiteSpace(prefabName))
        {
            return prefabName;
        }

        const string cloneSuffix = "(Clone)";
        string name = gameObject.name;
        if (name.EndsWith(cloneSuffix, StringComparison.Ordinal))
        {
            return name[..^cloneSuffix.Length].TrimEnd();
        }

        return name;
    }

    private static string TrimCloneSuffix(string name)
    {
        const string cloneSuffix = "(Clone)";
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        if (name.EndsWith(cloneSuffix, StringComparison.Ordinal))
        {
            return name[..^cloneSuffix.Length].TrimEnd();
        }

        return name.Trim();
    }

    private static string NormalizeLocationReferencePrefabName(string name)
    {
        string normalized = TrimCloneSuffix(name);
        if (normalized.Length < 4 || normalized[^1] != ')')
        {
            return normalized;
        }

        int openingParenIndex = normalized.LastIndexOf(" (", StringComparison.Ordinal);
        if (openingParenIndex <= 0)
        {
            return normalized;
        }

        ReadOnlySpan<char> suffix = normalized.AsSpan(openingParenIndex + 2, normalized.Length - openingParenIndex - 3);
        if (suffix.Length == 0)
        {
            return normalized;
        }

        foreach (char character in suffix)
        {
            if (!char.IsDigit(character))
            {
                return normalized;
            }
        }

        return normalized[..openingParenIndex].TrimEnd();
    }

    private static void AddSnapshotByName<T>(Dictionary<string, List<T>> snapshotsByName, string configPrefabName, T snapshot)
    {
        if (!snapshotsByName.TryGetValue(configPrefabName, out List<T>? snapshots))
        {
            snapshots = new List<T>();
            snapshotsByName[configPrefabName] = snapshots;
        }

        snapshots.Add(snapshot);
    }

    private static bool HasAnySpawnAreaSpawnData(List<SpawnAreaSpawnDefinition>? prefabs)
    {
        return prefabs?.Any(prefab => prefab.Data != null || prefab.Fields != null || prefab.Objects != null) == true;
    }

    private static string FormatYamlBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatYamlFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static bool IsReferenceDefault(float value, float defaultValue)
    {
        return Math.Abs(value - defaultValue) < 0.0001f;
    }

    private static string FormatYamlString(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        bool requiresQuotes =
            char.IsWhiteSpace(value[0]) ||
            char.IsWhiteSpace(value[value.Length - 1]) ||
            value.IndexOfAny(new[] { ':', '#', '{', '}', '[', ']', ',', '\'', '"', '&', '*', '!', '|', '>', '%', '@', '`' }) >= 0 ||
            value[0] == '-' ||
            value[0] == '?' ||
            string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        return requiresQuotes ? $"'{value.Replace("'", "''")}'" : value;
    }

    private static void WarnMissingComponent(string key, string componentName)
    {
        if (LiveReconcilerState.TryAddMissingComponentWarning(key))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Spawner configuration references {componentName}, but no matching '{key.Split(':')[0]}' component name was found.");
        }
    }

    private static void LogLocationSelectorDiagnostic(GameObject gameObject, SpawnerConfigurationEntry entry, string reason, string? resolvedLocation = null, string? resolvedPath = null)
    {
        if (gameObject == null || entry == null)
        {
            return;
        }

        string configPrefabName = GetConfigPrefabName(gameObject, entry.SpawnArea != null ? nameof(SpawnArea) : nameof(CreatureSpawner));
        string key = string.Join(
            "|",
            configPrefabName,
            DescribeInstance(gameObject),
            entry.RuleId,
            reason,
            entry.Location ?? "",
            resolvedLocation ?? "");
        if (!SelectorCacheStore.TryAddLocationSelectorDiagnostic(key))
        {
            return;
        }

        string selectorDescription = $"location='{entry.Location}'";
        string resolvedDescription = resolvedLocation != null
            ? $"resolved location='{resolvedLocation}'"
            : "resolved location unavailable";
        DropNSpawnPlugin.DropNSpawnLogger.LogDebug(
            $"Spawner selector mismatch for '{configPrefabName}@{DescribeInstance(gameObject)}' using {selectorDescription}. {reason}; {resolvedDescription}.");
    }

    private static void LogLocationSelectorDiagnostic(GameObject gameObject, SpawnerRuntimeEntry entry, string reason, string? resolvedLocation = null, string? resolvedPath = null)
    {
        if (gameObject == null || entry == null)
        {
            return;
        }

        string componentName = ResolveRuntimeSpawnerComponentName(gameObject);
        string configPrefabName = GetConfigPrefabName(gameObject, componentName);
        string key = string.Join(
            "|",
            configPrefabName,
            DescribeInstance(gameObject),
            entry.RuleId,
            reason,
            entry.Location ?? "",
            resolvedLocation ?? "");
        if (!SelectorCacheStore.TryAddLocationSelectorDiagnostic(key))
        {
            return;
        }

        string selectorDescription = $"location='{entry.Location}'";
        string resolvedDescription = resolvedLocation != null
            ? $"resolved location='{resolvedLocation}'"
            : "resolved location unavailable";
        DropNSpawnPlugin.DropNSpawnLogger.LogDebug(
            $"Spawner selector mismatch for '{configPrefabName}@{DescribeInstance(gameObject)}' using {selectorDescription}. {reason}; {resolvedDescription}.");
    }

    private static string ResolveRuntimeSpawnerComponentName(GameObject gameObject)
    {
        if (gameObject != null && gameObject.TryGetComponent(out SpawnArea _))
        {
            return nameof(SpawnArea);
        }

        return nameof(CreatureSpawner);
    }

    private static void WarnInvalidEntry(string message)
    {
        if (_invalidEntryWarningSuppressionDepth > 0 || ShouldSuppressServerSourcedInvalidEntryWarning(message))
        {
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

    private static void LogPartiallyAcceptedLocalConfiguration(int totalEntries, int acceptedEntries, IEnumerable<string> warnings)
    {
        int skippedEntries = Math.Max(0, totalEntries - acceptedEntries);
        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Skipped {skippedEntries.ToString(CultureInfo.InvariantCulture)} invalid spawner entr{(skippedEntries == 1 ? "y" : "ies")} and kept {acceptedEntries.ToString(CultureInfo.InvariantCulture)} valid entr{(acceptedEntries == 1 ? "y" : "ies")}.");
        foreach (string warning in warnings
                     .Where(message => !string.IsNullOrWhiteSpace(message))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(warning);
        }
    }

    private static void LogLocalConfigurationLoaded(int acceptedEntryCount, int loadedFileCount)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Loaded {acceptedEntryCount} spawner configuration(s) from {loadedFileCount} override file(s).");
    }

    private static void OnSourceOfTruthPayloadUnchanged()
    {
        if (!NetworkPayloadSyncSupport.IsPayloadCurrent(Descriptor, _configurationSignature))
        {
            ConfigurationDomainHost.PublishSyncedPayload(
                DropNSpawnPlugin.IsSourceOfTruth,
                Descriptor,
                _configuration,
                _configurationSignature);
        }
    }

    private static void LogSyncedSpawnerConfigurationLoaded(string payloadToken, int acceptedEntryCount)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Loaded {acceptedEntryCount} synchronized spawner configuration(s) from the server.");
    }

    private static void LogSyncedSpawnerConfigurationFailure(string payloadToken, Exception ex)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to deserialize synchronized spawner payload DTO. {ex}");
    }

    private static string CreateConfigurationContext(SpawnerConfigurationEntry entry)
    {
        string prefabName = string.IsNullOrWhiteSpace(entry.Prefab) ? "<missing prefab>" : entry.Prefab;
        return $"{prefabName} @ {DescribeEntrySource(entry)}";
    }

    private static string DescribeEntrySource(SpawnerConfigurationEntry entry)
    {
        string location = DescribeEntrySource(entry.SourcePath);
        if (entry.SourceLine > 0)
        {
            location = $"{location}:{entry.SourceLine.ToString(CultureInfo.InvariantCulture)}";
        }

        return location;
    }

    private static string DescribeEntrySource(string? sourcePath)
    {
        string explicitSource = sourcePath ?? "";
        if (explicitSource.Length == 0)
        {
            return "unknown source";
        }

        if (explicitSource.StartsWith("ServerSync:", StringComparison.Ordinal))
        {
            return explicitSource;
        }

        return explicitSource;
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
}
