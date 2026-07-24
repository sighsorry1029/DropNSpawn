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
        new(new DomainModuleOptions<SpawnerConfigurationEntry>
        {
            DomainKey = "spawner",
            ReloadDomain = DropNSpawnPlugin.ReloadDomain.Spawner,
            ManifestSettingKey = "spawner_yaml",
            ManifestPriority = 98,
            ShouldReloadForPath = ShouldReloadForPath,
            Reload = ReloadConfiguration,
            InitializeRuntime = Initialize,
            OnGameDataReady = OnGameDataReady,
            HandleExpandWorldDataReady = HandleExpandWorldDataReady,
            DtoVersion = 7,
            TransportProfile = DomainTransportProfile.MediumConfig,
            DisplayName = "spawner",
            CacheDirectoryName = "spawner",
            ClientRequestPriority = 30,
            KeySelector = entry => entry.RuleId,
            ApplyPayloadAction = ApplySyncedPayload,
            HasPendingReconcileWork = HasPendingReconcileWork,
            ProcessPendingReconcileStep = ProcessQueuedReconcileStep,
            BeforeClientManifestChanged = MarkSyncedPayloadPending,
            OnClientAuthorityCutover = EnterPendingSyncedPayloadState
        });
    internal static DomainDescriptor<SpawnerConfigurationEntry> Descriptor => Module.DescriptorTyped;
    internal static DomainTransportMetadata<SpawnerConfigurationEntry> TransportMetadata => Module.TransportMetadataTyped;

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
        public List<string>? Locations { get; set; }
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
    private static readonly SpawnerConfigurationRuntimeState RuntimeState = new();
    private static readonly InvalidEntryDiagnostics InvalidEntryWarnings = new();
    private static readonly SpawnerLiveRuntimeState LiveRuntimeState = new();
    private static readonly HashSet<string> CapturedRootPrefabNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly FieldInfo? CreatureSpawnerCheckedLocationField = AccessTools.Field(typeof(CreatureSpawner), "m_checkedLocation");
    private static readonly FieldInfo? CreatureSpawnerLocationField = AccessTools.Field(typeof(CreatureSpawner), "m_location");
    private static readonly FieldInfo? CreatureSpawnerSpawnGroupField = AccessTools.Field(typeof(CreatureSpawner), "m_spawnGroup");

    private static DomainLoadState LoadState => ConfigurationRuntime.LoadState;
    private static bool _lastAppliedSynchronizedPayloadReady;
    private static bool _initialized;
    private static bool _snapshotsCaptured;
    private static int? _lastProcessedGameDataSignature;
    private static SpawnerRuntimeConfigurationSnapshot _runtimeConfigurationSnapshot = SpawnerRuntimeConfigurationSnapshot.Empty;
    private static bool _referenceArtifactsAutoRefreshConsumed;
    private static readonly Dictionary<string, string> _lastAppliedEntrySignaturesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static string _lastAppliedConfigurationSignature = "";
    private static int? _lastAppliedGameDataSignature;
    private static bool? _lastAppliedDomainEnabled;
    private static int _reconcileQueueEpoch;
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
                    RuntimeState.Configuration,
                    RuntimeState.ConfigurationSignature)),
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
                MarkSyncedPayloadPending,
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
                    RuntimeState.ConfigurationSignature = "";
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

            string refreshedSignature = NetworkPayloadSyncSupport.ComputeSpawnerConfigurationSignature(RuntimeState.Configuration);
            if (string.Equals(refreshedSignature, RuntimeState.ConfigurationSignature, StringComparison.Ordinal))
            {
                return false;
            }

            RuntimeState.ConfigurationSignature = refreshedSignature;
            ConfigurationDomainHost.PublishSyncedPayload(
                DropNSpawnPlugin.IsSourceOfTruth,
                Descriptor,
                RuntimeState.Configuration,
                RuntimeState.ConfigurationSignature);
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
        foreach ((string _, List<SpawnerConfigurationEntry> entries) in RuntimeState.ActiveEntriesByPrefab)
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

        GeneratedArtifactWriter.WriteTextAlways(
            PrimaryOverrideConfigurationPathYml,
            BuildPrimaryOverrideConfigurationTemplate(),
            $"Created spawner override configuration at {PrimaryOverrideConfigurationPathYml}.");
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

    private static void ResetLoadedConfigurationState()
    {
        ClearQueuedReconcileState();
        Volatile.Write(ref _synchronizedPayloadReady, false);
        Volatile.Write(ref _runtimeConfigurationSnapshot, SpawnerRuntimeConfigurationSnapshot.Empty);
        RuntimeState.Reset();
        InvalidEntryWarnings.Clear();
        SelectorCacheStore.Clear();
        RuntimeStateStore.Clear();
        LiveRegistryStore.ClearLocationBuckets();
        ProvenanceRegistry.Clear(clearCurrentContexts: false);
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
        using InvalidEntryDiagnostics.SuppressionScope _ = BeginInvalidEntryWarningSuppressionForSyncedClientBuild(sourceName);
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
                    if (!HasLocationSelector(runtimeEntry))
                    {
                        prefabPlan.HasUnscopedSpawnAreaEntries = true;
                    }
                    else
                    {
                        foreach (string location in runtimeEntry.Locations!)
                        {
                            prefabPlan.SpawnAreaSelectorLocationKeys.Add(
                                NormalizeSelectorLocationCacheKey(location));
                        }
                    }

                    if (runtimeEntry.RuntimeReconcile)
                    {
                        prefabPlan.DynamicSpawnAreaEntries.Add(runtimeEntry);
                    }
                }

                if (entry.CreatureSpawner != null && HasCreatureSpawnerOverride(entry.CreatureSpawner))
                {
                    prefabPlan.CreatureSpawnerEntries.Add(runtimeEntry);
                    if (!HasLocationSelector(runtimeEntry))
                    {
                        prefabPlan.HasUnscopedCreatureSpawnerEntries = true;
                    }
                    else
                    {
                        foreach (string location in runtimeEntry.Locations!)
                        {
                            prefabPlan.CreatureSpawnerSelectorLocationKeys.Add(
                                NormalizeSelectorLocationCacheKey(location));
                        }
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
            Locations = CloneStringList(entry.Locations),
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
        RuntimeState.Configuration = state.Configuration;
        RuntimeState.ActiveEntries.AddRange(state.ActiveEntries);
        foreach ((string prefabName, List<SpawnerConfigurationEntry> entries) in state.ActiveEntriesByPrefab)
        {
            RuntimeState.ActiveEntriesByPrefab[prefabName] = entries;
        }

        foreach (string prefabName in state.ConfiguredSpawnAreaPrefabs)
        {
            RuntimeState.ConfiguredSpawnAreaPrefabs.Add(prefabName);
        }

        foreach (string prefabName in state.ConfiguredCreatureSpawnerPrefabs)
        {
            RuntimeState.ConfiguredCreatureSpawnerPrefabs.Add(prefabName);
        }

        foreach (string prefabName in state.RuntimeConfiguredSpawnAreaPrefabs)
        {
            RuntimeState.RuntimeConfiguredSpawnAreaPrefabs.Add(prefabName);
        }

        foreach (string prefabName in state.RuntimeConfiguredCreatureSpawnerPrefabs)
        {
            RuntimeState.RuntimeConfiguredCreatureSpawnerPrefabs.Add(prefabName);
        }

        ReplaceEntrySignatures(RuntimeState.CurrentEntrySignaturesByPrefab, state.EntrySignaturesByPrefab);
        RuntimeState.ConfigurationSignature = state.ConfigurationSignature;
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
        return ConfigurationLoadSupport.ParseLocalConfigurationDocuments(
            documents,
            ParseConfiguration,
            PrepareLocalConfigurationEntries,
            FormatYamlExceptionLocation,
            "Spawner override YAML must start with a root list like '- prefab: ...'.");
    }

    private static void RejectLocalConfigurationPayload(string payload, IEnumerable<string> errors)
    {
        ConfigurationDomainHost.RejectLocalConfigurationPayload(LoadState, payload, errors, "spawner");
    }

    private static void InvalidateTrackedSpawnerEligibility()
    {
        LiveRuntimeState.InvalidateTrackedSpawnerEligibility();
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

    private static ConfigurationLoadSupport.ParsedLocalConfiguration<SpawnerConfigurationEntry> ParseConfiguration(
        string yaml,
        string? sourcePath)
    {
        ConfigurationLoadSupport.ParsedLocalConfiguration<SpawnerConfigurationEntry> result = new();
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
        entry.Locations = NormalizeOptionalSelectorLocations(entry.Locations);
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
            Locations = CloneStringList(entry.Locations),
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

    private static List<string>? NormalizeOptionalSelectorLocations(List<string>? values)
    {
        if (values == null)
        {
            return null;
        }

        SortedSet<string> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values)
        {
            string location = (value ?? "").Trim();
            if (location.Length > 0)
            {
                normalized.Add(location);
            }
        }

        return normalized.Count == 0 ? null : normalized.ToList();
    }

    private static List<string>? CloneStringList(List<string>? values)
    {
        return values == null ? null : new List<string>(values);
    }

    private static bool HasLocationSelector(SpawnerConfigurationEntry? entry)
    {
        return HasLocationSelector(entry?.Locations);
    }

    private static bool HasLocationSelector(SpawnerRuntimeEntry? entry)
    {
        return HasLocationSelector(entry?.Locations);
    }

    private static bool HasLocationSelector(List<string>? locations)
    {
        return locations?.Any(location => !string.IsNullOrWhiteSpace(location)) == true;
    }

    private static bool MatchesLocationSelector(List<string>? locations, string? resolvedLocationPrefab)
    {
        string resolved = (resolvedLocationPrefab ?? "").Trim();
        return resolved.Length > 0 &&
               locations?.Any(location => string.Equals((location ?? "").Trim(), resolved, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static string FormatLocationSelector(List<string>? locations)
    {
        return HasLocationSelector(locations)
            ? $"locations=[{string.Join(", ", locations!)}]"
            : "prefab-only";
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
        ConditionDialectSupport.StripUnsupportedSpawnerTargetFields(
            conditions,
            context,
            allowCreatureSpawnerRuntimeOverlapKeys,
            WarnInvalidEntry);
    }

    private static void NormalizeCreatureSpawnerEntryConditions(ConditionsDefinition? conditions, string context)
    {
        ConditionDialectSupport.StripUnsupportedCreatureSpawnerEntryFields(conditions, context, WarnInvalidEntry);
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
        LiveRuntimeState.ClearComponentCatalogs();
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
                RuntimeState.ConfigurationSignature,
                _lastAppliedSynchronizedPayloadReady,
                synchronizedPayloadReady))
        {
            return;
        }

        RunApplyCoordinator(availablePrefabs, gameDataSignature, domainEnabled, currentEntrySignatures, queueLiveReconcile);
    }

    private static void ValidateConfiguredPrefabs(HashSet<string> availablePrefabs)
    {
        foreach ((string prefabName, List<SpawnerConfigurationEntry> entries) in RuntimeState.ActiveEntriesByPrefab)
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
        _lastAppliedConfigurationSignature = RuntimeState.ConfigurationSignature;
        _lastAppliedSynchronizedPayloadReady = Volatile.Read(ref _synchronizedPayloadReady);
        ReplaceEntrySignatures(_lastAppliedEntrySignaturesByPrefab, currentEntrySignatures);
    }

    private static Dictionary<string, string> CloneCurrentEntrySignaturesByPrefab()
    {
        return new Dictionary<string, string>(RuntimeState.CurrentEntrySignaturesByPrefab, StringComparer.OrdinalIgnoreCase);
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
        return BuildActiveEntrySignaturesByPrefab(RuntimeState.ActiveEntriesByPrefab);
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
        ReapplyRegisteredLiveObjects(
            domainEnabled,
            prefabs,
            GetRuntimeConfigurationSnapshot());
    }

    private static void ReapplyOrQueueRegisteredLiveObjects(bool domainEnabled, HashSet<string> prefabs)
    {
        ReapplyOrQueueRegisteredLiveObjects(
            domainEnabled,
            prefabs,
            GetRuntimeConfigurationSnapshot());
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
        ApplyDefaultZeroCreatureSpawnerRespawnTime(target, yamlRespawnTimeSpecified: false);
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
        ApplyDefaultZeroCreatureSpawnerRespawnTime(target, definition.RespawnTimeMinutes.HasValue);

        if (definition.TriggerDistance.HasValue)
        {
            target.m_triggerDistance = Mathf.Max(0f, definition.TriggerDistance.Value);
        }

        if (definition.TriggerNoise.HasValue)
        {
            target.m_triggerNoise = Mathf.Max(0f, definition.TriggerNoise.Value);
        }

        TimeOfDayDefinition? timeOfDay = definition.TimeOfDay;
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

    private static void ApplyDefaultZeroCreatureSpawnerRespawnTime(CreatureSpawner? target, bool yamlRespawnTimeSpecified)
    {
        if (target == null || yamlRespawnTimeSpecified || !ShouldApplyLocally())
        {
            return;
        }

        int defaultRespawnTimeMinutes = PluginSettingsFacade.GetDefaultZeroCreatureSpawnerRespawnTimeMinutes();
        if (defaultRespawnTimeMinutes <= 0 || target.m_respawnTimeMinuts > 0f)
        {
            return;
        }

        target.m_respawnTimeMinuts = defaultRespawnTimeMinutes;
    }

    private static bool HasDynamicEntryConditions(SpawnerConfigurationEntry? entry)
    {
        return entry != null && DropConditionEvaluator.HasDynamicConditions(entry.Conditions);
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

        if (TryGetClonedZoneLocationContext(gameObject, out locationPrefab))
        {
            sourceLabel = "LocationZone";
            relativePath = "";
            return true;
        }

        if (TryGetPersistedSpawnerLocationContext(gameObject, out locationPrefab, out relativePath))
        {
            sourceLabel = "PersistedProvenance";
            return true;
        }

        if (TryGetDungeonGeneratorLocationContext(gameObject, out locationPrefab))
        {
            sourceLabel = "DungeonGenerator";
            relativePath = "";
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
                LiveRuntimeState.SpawnAreaCatalogsByExactKey.TryGetValue(exactKey, out SpawnAreaComponentCatalog? spawnAreaCatalog))
            {
                return spawnAreaCatalog.ConfigPrefabName;
            }

            if (componentType == nameof(CreatureSpawner) &&
                LiveRuntimeState.CreatureSpawnerCatalogsByExactKey.TryGetValue(exactKey, out CreatureSpawnerComponentCatalog? creatureSpawnerCatalog))
            {
                return creatureSpawnerCatalog.ConfigPrefabName;
            }

            string configPrefabName = GetLiveComponentPrefabName(gameObject);
            string relativePath = GetRelativePath(GetRootTransform(gameObject.transform), gameObject.transform);
            if (componentType == nameof(SpawnArea))
            {
                LiveRuntimeState.SpawnAreaCatalogsByExactKey[exactKey] = new SpawnAreaComponentCatalog
                {
                    ConfigPrefabName = configPrefabName,
                    RootPrefabName = rootPrefabName,
                    RelativePath = relativePath
                };
            }
            else if (componentType == nameof(CreatureSpawner))
            {
                LiveRuntimeState.CreatureSpawnerCatalogsByExactKey[exactKey] = new CreatureSpawnerComponentCatalog
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

    private static void WarnInvalidEntry(string message)
    {
        InvalidEntryWarnings.Warn(message);
    }

    private static InvalidEntryDiagnostics.SuppressionScope BeginInvalidEntryWarningSuppressionForSyncedClientBuild(string sourceName)
    {
        return InvalidEntryWarnings.BeginSuppressionForSyncedClientBuild(sourceName);
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
        if (!NetworkPayloadSyncSupport.IsPayloadCurrent(Descriptor, RuntimeState.ConfigurationSignature))
        {
            ConfigurationDomainHost.PublishSyncedPayload(
                DropNSpawnPlugin.IsSourceOfTruth,
                Descriptor,
                RuntimeState.Configuration,
                RuntimeState.ConfigurationSignature);
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
