using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private const string ReferenceAutoUpdateStateKey = "object";
    private const string LocationReferenceAutoUpdateStateKey = "object.locations";
    internal static readonly DomainModuleDefinition<PrefabConfigurationEntry> Module =
        new(
            "object",
            DropNSpawnPlugin.ReloadDomain.Object,
            "object_yaml",
            100,
            ShouldReloadForPath,
            ReloadConfiguration,
            Initialize,
            OnGameDataReady,
            HandleExpandWorldDataReady,
            dtoVersion: 7,
            transportProfile: DomainTransportProfile.LargeConfig,
            displayName: "object",
            cacheDirectoryName: "object",
            clientRequestPriority: 100,
            keySelector: entry => entry.RuleId,
            applyPayloadAction: ApplySyncedPayload,
            workKinds: DomainWorkKinds.Runtime | DomainWorkKinds.SnapshotBuild | DomainWorkKinds.Reconcile,
            hasPendingSnapshotBuildWork: HasPendingSnapshotBuildWork,
            processPendingSnapshotBuildStep: ProcessPendingSnapshotBuildStep,
            hasPendingReconcileWork: HasPendingReconcileWork,
            processPendingReconcileStep: ProcessQueuedReconcileStep,
            beforeClientManifestChanged: MarkSyncedPayloadPending,
            onClientAuthorityCutover: EnterPendingSyncedPayloadState);
    internal static DomainDescriptor<PrefabConfigurationEntry> Descriptor => Module.DescriptorTyped;
    internal static DomainTransportMetadata<PrefabConfigurationEntry> TransportMetadata => Module.TransportMetadataTyped;

    private readonly struct PendingObjectReconcileGroup
    {
        public PendingObjectReconcileGroup(string groupKey, int epoch)
        {
            GroupKey = groupKey;
            Epoch = epoch;
        }

        public string GroupKey { get; }
        public int Epoch { get; }
    }

    private readonly struct PendingObjectReconcileItem
    {
        public PendingObjectReconcileItem(GameObject gameObject, int instanceId)
        {
            GameObject = gameObject;
            InstanceId = instanceId;
        }

        public GameObject GameObject { get; }
        public int InstanceId { get; }
    }

    private sealed class PendingObjectReconcileGroupState
    {
        public RingBufferQueue<PendingObjectReconcileItem> Items { get; } = new();
        public HashSet<int> InstanceIds { get; } = new();
        public bool ClearCreatorRestrictedContainerContents { get; set; }
        public LiveObjectComponentKind ComponentKinds { get; set; }
        public bool HighPriority { get; set; }
        public bool IsQueued { get; set; }
    }

    private sealed class GroupConditionalApplyPlan
    {
        public HashSet<PrefabConfigurationEntry> EligibleEntries { get; } = new();
        public List<PrefabConfigurationEntry> MatchingEntries { get; } = new();
        public HashSet<CompiledObjectDropRule> EligibleCompiledRules { get; } = new();
        public List<CompiledObjectDropRule> MatchingCompiledRules { get; } = new();
    }

    private sealed class GroupConditionalApplyPlanCacheEntry
    {
        public GroupConditionalApplyPlan Plan { get; set; } = null!;
        public LinkedListNode<string> LruNode { get; set; } = null!;
    }

    private readonly struct StaticObjectMatchCacheEntry
    {
        public StaticObjectMatchCacheEntry(int epoch, bool hasPotentialStaticMatch)
        {
            Epoch = epoch;
            HasPotentialStaticMatch = hasPotentialStaticMatch;
        }

        public int Epoch { get; }
        public bool HasPotentialStaticMatch { get; }
    }

    [Flags]
    internal enum LiveObjectComponentKind
    {
        None = 0,
        DropOnDestroyed = 1 << 0,
        MineRock = 1 << 1,
        MineRock5 = 1 << 2,
        TreeBase = 1 << 3,
        TreeLog = 1 << 4,
        Container = 1 << 5,
        Pickable = 1 << 6,
        PickableItem = 1 << 7,
        Fish = 1 << 8,
        Destructible = 1 << 9,
        Piece = 1 << 10
    }

    private sealed class PickableSnapshot
    {
        public GameObject? ItemPrefab { get; set; }
        public int Amount { get; set; }
        public int MinAmountScaled { get; set; }
        public bool DontScale { get; set; }
        public string OverrideName { get; set; } = "";
        public DropTable ExtraDrops { get; set; } = new();
    }

    private sealed class PickableItemRandomSnapshot
    {
        public GameObject? ItemPrefab { get; set; }
        public int StackMin { get; set; }
        public int StackMax { get; set; }
    }

    private sealed class PickableItemSnapshot
    {
        public GameObject? ItemPrefab { get; set; }
        public int Stack { get; set; }
        public List<PickableItemRandomSnapshot> RandomItems { get; set; } = new();
    }

    private sealed class BuiltRandomPickableItems
    {
        public PickableItem.RandomItem[] Items { get; set; } = Array.Empty<PickableItem.RandomItem>();
        public float[] Weights { get; set; } = Array.Empty<float>();
    }

    private sealed class WeightedRandomPickableItemState
    {
        public float[] Weights { get; set; } = Array.Empty<float>();
    }

    private sealed class FishSnapshot
    {
        public DropTable ExtraDrops { get; set; } = new();
    }

    private sealed class DestructibleSnapshot
    {
        public DestructibleType DestructibleType { get; set; }
        public GameObject? SpawnWhenDestroyed { get; set; }
    }

    private sealed class HealthSnapshot
    {
        public float? Destructible { get; set; }
        public float? MineRock { get; set; }
        public float? MineRock5 { get; set; }
        public float? TreeBase { get; set; }
        public float? TreeLog { get; set; }
    }

    private sealed class MinToolTierSnapshot
    {
        public int? Destructible { get; set; }
        public int? MineRock { get; set; }
        public int? MineRock5 { get; set; }
        public int? TreeBase { get; set; }
        public int? TreeLog { get; set; }
    }

    private sealed class PrefabSnapshot
    {
        public GameObject Prefab { get; set; } = null!;
        public HealthSnapshot? Health { get; set; }
        public MinToolTierSnapshot? MinToolTier { get; set; }
        public DropTable? DropOnDestroyed { get; set; }
        public DropTable? MineRock { get; set; }
        public DropTable? MineRock5 { get; set; }
        public DropTable? TreeBase { get; set; }
        public DropTable? TreeLog { get; set; }
        public DropTable? Container { get; set; }
        public PickableSnapshot? Pickable { get; set; }
        public PickableItemSnapshot? PickableItem { get; set; }
        public FishSnapshot? Fish { get; set; }
        public DestructibleSnapshot? Destructible { get; set; }
    }

    private sealed class SyncedObjectConfigurationState
    {
        public List<PrefabConfigurationEntry> Configuration { get; set; } = new();
        public Dictionary<string, List<PrefabConfigurationEntry>> ActiveEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LiveObjectComponentKind> ConfiguredComponentKindsByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LiveObjectComponentKind> ReconcileComponentKindsByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<PrefabConfigurationEntry>> VneiEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> VneiEntrySignaturesByPrefab { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string ConfigurationSignature { get; set; } = "";
    }

    private sealed class ParsedObjectConfigurationDocument
    {
        public List<PrefabConfigurationEntry> Configuration { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    private sealed class CompiledDropTableRow
    {
        public string Fingerprint { get; set; } = "";
        public GameObject ItemPrefab { get; set; } = null!;
        public int StackMin { get; set; }
        public int StackMax { get; set; }
        public float Weight { get; set; }
        public bool DontScale { get; set; }
    }

    private sealed class CompiledDropTablePayload
    {
        public bool HasDropRangeOverride { get; set; }
        public int DropMin { get; set; }
        public int DropMax { get; set; }
        public bool HasDropChanceOverride { get; set; }
        public float DropChance { get; set; }
        public bool HasOneOfEachOverride { get; set; }
        public bool OneOfEach { get; set; }
        public List<CompiledDropTableRow> Drops { get; } = new();
    }

    private sealed class CompiledPickableDefinition
    {
        public bool HasItemPrefabOverride { get; set; }
        public GameObject? ItemPrefab { get; set; }
        public bool HasAmountOverride { get; set; }
        public int Amount { get; set; }
        public bool HasMinAmountScaledOverride { get; set; }
        public int MinAmountScaled { get; set; }
        public bool HasDontScaleOverride { get; set; }
        public bool DontScale { get; set; }
        public bool HasOverrideNameOverride { get; set; }
        public string OverrideName { get; set; } = "";
    }

    private sealed class CompiledPickableItemRandomEntry
    {
        public ItemDrop ItemPrefab { get; set; } = null!;
        public int StackMin { get; set; }
        public int StackMax { get; set; }
        public float Weight { get; set; }
    }

    private sealed class CompiledPickableItemDefinition
    {
        public bool HasRandomOverride { get; set; }
        public PickableItem.RandomItem[] RandomItems { get; set; } = Array.Empty<PickableItem.RandomItem>();
        public float[] RandomWeights { get; set; } = Array.Empty<float>();
        public bool HasFixedDrop { get; set; }
        public bool HasFixedItemOverride { get; set; }
        public ItemDrop? FixedItemPrefab { get; set; }
        public bool HasFixedStackOverride { get; set; }
        public int FixedStack { get; set; }
    }

    private sealed class CompiledDamageableScalarDefinition
    {
        public bool HasHealthOverride { get; set; }
        public float Health { get; set; }
        public bool HasMinToolTierOverride { get; set; }
        public int MinToolTier { get; set; }
    }

    private sealed class CompiledDestructibleComponentDefinition
    {
        public bool HasHealthOverride { get; set; }
        public float Health { get; set; }
        public bool HasMinToolTierOverride { get; set; }
        public int MinToolTier { get; set; }
        public bool HasDestructibleTypeOverride { get; set; }
        public DestructibleType DestructibleType { get; set; }
        public bool HasSpawnWhenDestroyedOverride { get; set; }
        public GameObject? SpawnWhenDestroyed { get; set; }
    }

    private sealed class CompiledObjectDropRule
    {
        public PrefabConfigurationEntry Entry { get; set; } = null!;
        public bool HasConditions { get; set; }
        public CompiledDropTablePayload? DropOnDestroyed { get; set; }
        public CompiledDropTablePayload? MineRock { get; set; }
        public CompiledDropTablePayload? MineRock5 { get; set; }
        public CompiledDropTablePayload? TreeBase { get; set; }
        public CompiledDropTablePayload? TreeLog { get; set; }
        public CompiledDropTablePayload? Container { get; set; }
        public CompiledDropTablePayload? PickableExtraDrops { get; set; }
        public CompiledDropTablePayload? FishExtraDrops { get; set; }
        public CompiledPickableDefinition? Pickable { get; set; }
        public CompiledPickableItemDefinition? PickableItem { get; set; }
        public CompiledDestructibleComponentDefinition? Destructible { get; set; }
        public CompiledDamageableScalarDefinition? MineRockScalars { get; set; }
        public CompiledDamageableScalarDefinition? MineRock5Scalars { get; set; }
        public CompiledDamageableScalarDefinition? TreeBaseScalars { get; set; }
        public CompiledDamageableScalarDefinition? TreeLogScalars { get; set; }
    }

    private sealed class StaticCompiledDropTableTemplate
    {
        public DropTable Template { get; set; } = null!;
        public HashSet<string> Fingerprints { get; } = new(StringComparer.Ordinal);
    }

    private sealed class CompiledObjectPrefabPlan
    {
        public List<PrefabConfigurationEntry> ActiveEntries { get; } = new();
        public List<CompiledObjectDropRule> Rules { get; } = new();
        public Dictionary<LiveObjectComponentKind, StaticCompiledDropTableTemplate> StaticDropTableTemplates { get; } = new();
    }

    private sealed class ObjectRuntimeDropConfigurationState
    {
        public static ObjectRuntimeDropConfigurationState Empty { get; } = new();

        public Dictionary<string, CompiledObjectPrefabPlan> PlansByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<PrefabConfigurationEntry, CompiledObjectDropRule> RulesByEntry { get; } = new();
    }

    private sealed class PendingSnapshotBuildState
    {
        public int BuildVersion { get; set; }
        public int GameDataSignature { get; set; }
        public int SnapshotSignature { get; set; }
        public string Source { get; set; } = "";
        public List<GameObject> Prefabs { get; } = new();
        public List<PrefabSnapshot> Snapshots { get; } = new();
        public Dictionary<string, PrefabSnapshot> SnapshotsByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int NextIndex { get; set; }
    }

    private sealed class LocationReferenceBucket
    {
        public SortedSet<string> Components { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> Locations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PrefabReferenceEntry> ReferenceEntriesBySignature { get; } = new(StringComparer.Ordinal);
    }

    private static readonly object Sync = new();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    private static readonly List<PrefabSnapshot> Snapshots = new();
    private static readonly Dictionary<string, PrefabSnapshot> SnapshotsByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<PrefabConfigurationEntry>> ActiveEntriesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<PrefabConfigurationEntry>> VneiEntriesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MissingComponentWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> InvalidEntryWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly int DestructibleLazyScalarSignatureKey = $"{DropNSpawnPlugin.ModName}.destructible_scalar_signature".GetStableHashCode();
    private static readonly int MineRockLazyScalarSignatureKey = $"{DropNSpawnPlugin.ModName}.minerock_scalar_signature".GetStableHashCode();
    private static readonly int MineRock5LazyScalarSignatureKey = $"{DropNSpawnPlugin.ModName}.minerock5_scalar_signature".GetStableHashCode();
    private static readonly int TreeBaseLazyScalarSignatureKey = $"{DropNSpawnPlugin.ModName}.treebase_scalar_signature".GetStableHashCode();
    private static readonly int TreeLogLazyScalarSignatureKey = $"{DropNSpawnPlugin.ModName}.treelog_scalar_signature".GetStableHashCode();
    private static readonly MethodInfo? PickableItemSetupItemMethod = AccessTools.Method(typeof(PickableItem), "SetupItem");
    private static readonly FieldInfo? MineRock5HitAreasField = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
    private static readonly MethodInfo? MineRock5SaveHealthMethod = AccessTools.Method(typeof(MineRock5), "SaveHealth");
    private static readonly MethodInfo? MineRock5UpdateMeshMethod = AccessTools.Method(typeof(MineRock5), "UpdateMesh");
    private static readonly FieldInfo? MineRock5HitAreaHealthField = AccessTools.Field(typeof(MineRock5).GetNestedType("HitArea", BindingFlags.NonPublic), "m_health");

    private static List<PrefabConfigurationEntry> _configuration = new();
    private static string _configurationSignature = "";
    private static DomainLoadState LoadState => ConfigurationRuntime.LoadState;
    private static bool _initialized;
    private static int? _lastProcessedSnapshotSignature;
    private static int? _lastProcessedGameDataSignature;
    private static ObjectRuntimeDropConfigurationState _runtimeDropConfigurationState = ObjectRuntimeDropConfigurationState.Empty;
    private static string _runtimeDropConfigurationSignature = "";
    private static int? _runtimeDropConfigurationGameDataSignature;
    private static int _cachedGameDataSignatureFrame = -1;
    private static int _cachedGameDataSignatureValue;
    private static int _cachedSnapshotSignatureFrame = -1;
    private static int _cachedSnapshotSignatureValue;
    private static bool _referenceArtifactsAutoRefreshConsumed;
    private static readonly Dictionary<string, string> _lastAppliedEntrySignaturesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static string _lastAppliedConfigurationSignature = "";
    private static int? _lastAppliedGameDataSignature;
    private static bool? _lastAppliedDomainEnabled;
    private static bool _lastAppliedSynchronizedPayloadReady;
    private static bool _synchronizedPayloadReady;
    private static int? _lastCommittedAuthorityEpoch;
    private static int _reconcileQueueEpoch;
    private static int _snapshotBuildVersion;
    private static PendingSnapshotBuildState? _pendingSnapshotBuild;
    private const string MockPrefabPrefix = "JVLmock_";
    private const int GroupConditionalApplyPlanCacheLimit = 2048;
    private const int GroupConditionalApplyPlanCacheTrimTarget = 1536;

    private static string ReferenceConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("object")}.reference.yml");
    private static string LocationReferenceConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("object")}.locations.reference.yml");
    private static string PrimaryOverrideConfigurationPathYml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("object")}.yml");
    private static string PrimaryOverrideConfigurationPathYaml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("object")}.yaml");
    private static string FullScaffoldConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("object")}.full.yml");
    private static readonly DomainConfigurationRuntime<PrefabConfigurationEntry, SyncedObjectConfigurationState> ConfigurationRuntime =
        new(
            new DomainLoadHooks<PrefabConfigurationEntry, SyncedObjectConfigurationState>(
                ParseLocalConfigurationDocuments,
                BuildSyncedConfigurationState,
                CommitSyncedConfigurationState,
                RejectLocalConfigurationPayload,
                state => state.Configuration.Count,
                LogPartiallyAcceptedLocalConfigurationHook,
                LogLocalConfigurationLoaded,
                OnSourceOfTruthPayloadUnchanged,
                () => ConfigurationDomainHost.PublishSyncedPayload(
                    DropNSpawnPlugin.IsSourceOfTruth,
                    Descriptor,
                    _configuration,
                    _configurationSignature)),
            new DomainSyncHooks<PrefabConfigurationEntry, SyncedObjectConfigurationState>(
                (out List<PrefabConfigurationEntry> configuration, out string payloadToken) =>
                    ConfigurationDomainHost.TryGetSyncedEntries(Descriptor, out configuration, out payloadToken),
                payloadToken => ConfigurationDomainHost.ShouldSkipSyncedPayload(
                    LoadState,
                    payloadToken,
                    Volatile.Read(ref _synchronizedPayloadReady)),
                BuildSyncedConfigurationState,
                CommitSyncedConfigurationState,
                state => state.ActiveEntriesByPrefab.Count,
                "ServerSync:DropNSpawnObject",
                () => ConfigurationDomainHost.HandleWaitingForSyncedPayload(
                    MarkSyncedPayloadPending,
                    "Waiting for synchronized object override payload from the server."),
                LogSyncedObjectConfigurationLoaded,
                LogSyncedObjectConfigurationFailure));

    internal static string RulesWatcherPattern => DropNSpawnPlugin.YamlRulesWatcherPattern;

    internal static bool ShouldReloadForPath(string? path)
    {
        return PluginSettingsFacade.IsEligibleOverrideConfigurationPath(path) &&
               IsOverrideConfigurationFileName(Path.GetFileName(path ?? ""));
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
            PromoteQueuedReconcileGroupsLocked(LiveObjectComponentKind.TreeBase | LiveObjectComponentKind.TreeLog);
            DrainQueuedHighPriorityReconcilesLocked();
        }
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
            Dictionary<string, string> previousVneiEntrySignatures = BuildVneiEntrySignaturesByPrefab();
            HashSet<string> previouslyAppliedPrefabs = BuildLastAppliedPrefabs();
            ConfigurationRuntime.EnterPendingSyncedPayloadState(
                DropNSpawnPlugin.IsSourceOfTruth,
                beforeResetLoadState: ResetLoadedConfigurationState,
                afterResetLoadState: () =>
                {
                    _configurationSignature = "";
                    _lastAppliedSynchronizedPayloadReady = false;
                    RestoreSnapshots(previouslyAppliedPrefabs);
                    RestoreTrackedLiveObjects(previouslyAppliedPrefabs);
                    RefreshVneiCompatibility(previousVneiEntrySignatures);
                });
        }
    }

    private static bool CanUseCurrentRuntimeState()
    {
        return DropNSpawnPlugin.IsSourceOfTruth ||
               Volatile.Read(ref _synchronizedPayloadReady) ||
               _lastCommittedAuthorityEpoch == NetworkPayloadSyncSupport.CurrentAuthorityEpoch;
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

    internal static bool HandleExpandWorldDataReady()
    {
        lock (Sync)
        {
            if (!DropNSpawnPlugin.IsSourceOfTruth)
            {
                return false;
            }

            string refreshedSignature = NetworkPayloadSyncSupport.ComputeObjectConfigurationSignature(_configuration);
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
            Dictionary<string, string> previousVneiEntrySignatures = BuildVneiEntrySignaturesByPrefab();
            ConfigurationRuntime.ApplySyncedPayload(() =>
            {
                RefreshVneiCompatibility(previousVneiEntrySignatures);
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

            int gameDataSignature = ComputeGameDataSignature();
            int snapshotSignature = ComputeSnapshotSignature();
            if (_lastProcessedGameDataSignature == gameDataSignature)
            {
                return;
            }

            bool snapshotsChanged = _lastProcessedSnapshotSignature != snapshotSignature || Snapshots.Count == 0;
            if (snapshotsChanged)
            {
                ScheduleSnapshotBuildLocked(source, gameDataSignature, snapshotSignature);
                return;
            }

            CompleteGameDataReadyLocked(source, gameDataSignature, snapshotSignature);
        }
    }

    internal static bool HasPendingSnapshotBuildWork()
    {
        lock (Sync)
        {
            return _pendingSnapshotBuild != null;
        }
    }

    internal static bool ProcessPendingSnapshotBuildStep(float deadline)
    {
        PendingSnapshotBuildState? buildState;
        GameObject? prefab = null;

        lock (Sync)
        {
            buildState = _pendingSnapshotBuild;
            if (buildState == null)
            {
                return false;
            }

            if (buildState.NextIndex >= buildState.Prefabs.Count)
            {
                CompletePendingSnapshotBuildLocked(buildState);
                return true;
            }

            prefab = buildState.Prefabs[buildState.NextIndex];
            buildState.NextIndex++;
        }

        PrefabSnapshot? snapshot = prefab != null ? CaptureSnapshot(prefab) : null;

        lock (Sync)
        {
            if (_pendingSnapshotBuild == null ||
                buildState == null ||
                !ReferenceEquals(_pendingSnapshotBuild, buildState))
            {
                return true;
            }

            if (snapshot != null)
            {
                buildState.Snapshots.Add(snapshot);
                if (!buildState.SnapshotsByPrefab.ContainsKey(snapshot.Prefab.name))
                {
                    buildState.SnapshotsByPrefab.Add(snapshot.Prefab.name, snapshot);
                }
            }

            if (buildState.NextIndex >= buildState.Prefabs.Count &&
                Time.realtimeSinceStartup <= deadline)
            {
                CompletePendingSnapshotBuildLocked(buildState);
            }
        }

        return true;
    }

    internal static bool TryWriteFullScaffoldConfigurationFile(out string path, out string error)
    {
        lock (Sync)
        {
            path = FullScaffoldConfigurationPath;
            error = "";

            if (!IsGameDataReady() && Snapshots.Count == 0)
            {
                error = "Object game data is not ready yet.";
                return false;
            }

            CaptureSnapshotsIfNeeded();
            Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
            File.WriteAllText(path, BuildFullScaffoldConfigurationTemplate());
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Wrote object full scaffold configuration to {path}.");
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
            WriteReferenceConfigurationFile(referenceContent, $"Updated object reference configurations at {ReferenceConfigurationPath} and {LocationReferenceConfigurationPath}.");
            WriteLocationReferenceConfigurationFile(locationReferenceContent);
            ReferenceRefreshSupport.RecordAutoUpdateState(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, ComputeReferenceSourceSignature(), logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
        }
    }

    private static bool IsGameDataReady()
    {
        return ZNetScene.instance != null && ObjectDB.instance != null;
    }

    private static void EnsureReferenceArtifactsUpToDate()
    {
        if (!IsGameDataReady())
        {
            return;
        }

        string currentSourceSignature = ComputeReferenceSourceSignature();
        bool referenceFileExists = File.Exists(ReferenceConfigurationPath);
        bool locationReferenceFileExists = File.Exists(LocationReferenceConfigurationPath);
        bool shouldCreateMissingFiles = PluginSettingsFacade.ShouldAutoCreateMissingReferenceFiles();
        bool shouldCreatePrimary = !referenceFileExists && shouldCreateMissingFiles;
        bool shouldCreateLocation = !locationReferenceFileExists && shouldCreateMissingFiles;

        if (shouldCreatePrimary || shouldCreateLocation)
        {
            CaptureSnapshotsIfNeeded();
            if (shouldCreatePrimary)
            {
                WriteReferenceConfigurationFile(BuildReferenceConfigurationTemplate(), $"Created object reference configuration at {ReferenceConfigurationPath}.");
                ReferenceRefreshSupport.RecordAutoUpdateState(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, currentSourceSignature, logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
            }

            if (shouldCreateLocation)
            {
                WriteLocationReferenceConfigurationFile(BuildLocationReferenceConfigurationTemplate());
                DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Created object location reference configuration at {LocationReferenceConfigurationPath}.");
                ReferenceRefreshSupport.RecordAutoUpdateState(LocationReferenceAutoUpdateStateKey, LocationReferenceConfigurationPath, currentSourceSignature, logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
            }
            return;
        }

        if (!PluginSettingsFacade.ShouldAutoUpdateReferenceFiles())
        {
            return;
        }

        bool shouldRewritePrimary = !ReferenceRefreshSupport.ShouldSkipAutoUpdate(
            ReferenceAutoUpdateStateKey,
            ReferenceConfigurationPath,
            currentSourceSignature,
            ReferenceRefreshSupport.CurrentReferenceLogicVersion);
        bool shouldRewriteLocation = !ReferenceRefreshSupport.ShouldSkipAutoUpdate(
            LocationReferenceAutoUpdateStateKey,
            LocationReferenceConfigurationPath,
            currentSourceSignature,
            ReferenceRefreshSupport.CurrentReferenceLogicVersion);
        if (!shouldRewritePrimary && !shouldRewriteLocation)
        {
            return;
        }

        CaptureSnapshotsIfNeeded();
        if (shouldRewritePrimary)
        {
            WriteReferenceConfigurationFile(BuildReferenceConfigurationTemplate(), $"Updated object reference configuration at {ReferenceConfigurationPath}.");
            ReferenceRefreshSupport.RecordAutoUpdateState(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, currentSourceSignature, logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
        }

        if (shouldRewriteLocation)
        {
            WriteLocationReferenceConfigurationFile(BuildLocationReferenceConfigurationTemplate());
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Updated object location reference configuration at {LocationReferenceConfigurationPath}.");
            ReferenceRefreshSupport.RecordAutoUpdateState(LocationReferenceAutoUpdateStateKey, LocationReferenceConfigurationPath, currentSourceSignature, logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
        }
    }

    private static bool EnsurePrimaryOverrideConfigurationFileExists()
    {
        if (DomainConfigurationFileSupport.HasAnyOverrideConfigurationFile(
                "object",
                PrimaryOverrideConfigurationPathYml,
                PrimaryOverrideConfigurationPathYaml))
        {
            return false;
        }

        if (!IsGameDataReady() || Snapshots.Count == 0)
        {
            return false;
        }

        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        File.WriteAllText(PrimaryOverrideConfigurationPathYml, BuildPrimaryOverrideConfigurationTemplate());
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Created object override configuration at {PrimaryOverrideConfigurationPathYml}.");
        return true;
    }

    private static void LoadConfiguration()
    {
        Dictionary<string, string> previousVneiEntrySignatures = BuildVneiEntrySignaturesByPrefab();
        if (DropNSpawnPlugin.IsSourceOfTruth)
        {
            if (ConfigurationRuntime.ReloadSourceOfTruth(
                    EnumerateOverrideConfigurationPaths().ToList()) == DomainReloadOutcome.Loaded)
            {
                RefreshVneiCompatibility(previousVneiEntrySignatures);
            }

            return;
        }

        if (ConfigurationRuntime.ReloadSynced() == DomainReloadOutcome.Loaded)
        {
            RefreshVneiCompatibility(previousVneiEntrySignatures);
        }
    }

    private static void RefreshVneiCompatibility(Dictionary<string, string> previousVneiEntrySignatures)
    {
        RefreshVneiCompatibility(previousVneiEntrySignatures, BuildVneiEntrySignaturesByPrefab());
    }

    private static void RefreshVneiCompatibility(
        Dictionary<string, string> previousVneiEntrySignatures,
        Dictionary<string, string> currentVneiEntrySignatures)
    {
        VneiCompatibility.RefreshObjectPrefabs(BuildDirtyPrefabs(previousVneiEntrySignatures, currentVneiEntrySignatures));
    }

    private static void ScheduleSnapshotBuildLocked(string source, int gameDataSignature, int snapshotSignature)
    {
        if (_pendingSnapshotBuild != null &&
            _pendingSnapshotBuild.GameDataSignature == gameDataSignature &&
            _pendingSnapshotBuild.SnapshotSignature == snapshotSignature)
        {
            return;
        }

        PendingSnapshotBuildState buildState = new()
        {
            BuildVersion = ++_snapshotBuildVersion,
            GameDataSignature = gameDataSignature,
            SnapshotSignature = snapshotSignature,
            Source = source
        };
        buildState.Prefabs.AddRange(EnumerateRelevantPrefabs());
        _pendingSnapshotBuild = buildState;
    }

    private static void CompletePendingSnapshotBuildLocked(PendingSnapshotBuildState buildState)
    {
        if (_pendingSnapshotBuild == null || !ReferenceEquals(_pendingSnapshotBuild, buildState))
        {
            return;
        }

        Snapshots.Clear();
        Snapshots.AddRange(buildState.Snapshots);
        SnapshotsByPrefab.Clear();
        foreach ((string prefabName, PrefabSnapshot snapshot) in buildState.SnapshotsByPrefab)
        {
            SnapshotsByPrefab[prefabName] = snapshot;
        }

        _pendingSnapshotBuild = null;
        CompleteGameDataReadyLocked(buildState.Source, buildState.GameDataSignature, buildState.SnapshotSignature);
    }

    private static void CompleteGameDataReadyLocked(string source, int gameDataSignature, int snapshotSignature)
    {
        EnsureLiveObjectRegistrySessionLocked();
        ClearQueuedReconcileState();

        if (DropNSpawnPlugin.IsSourceOfTruth)
        {
            if (!_referenceArtifactsAutoRefreshConsumed)
            {
                EnsureReferenceArtifactsUpToDate();
                _referenceArtifactsAutoRefreshConsumed = true;
            }
            if (EnsurePrimaryOverrideConfigurationFileExists())
            {
                LoadConfiguration();
            }
        }
        else
        {
            _referenceArtifactsAutoRefreshConsumed = true;
        }

        ApplyIfReady(queueLiveReconcile: true);
        _lastProcessedSnapshotSignature = snapshotSignature;
        _lastProcessedGameDataSignature = gameDataSignature;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"{DropNSpawnPlugin.ModName} processed after {source}.");
    }

    private static void ResetLoadedConfigurationState()
    {
        ClearQueuedReconcileState();
        ActiveEntriesByPrefab.Clear();
        PrefabProfileCatalogState.ClearCurrentProfiles();
        VneiEntriesByPrefab.Clear();
        MissingComponentWarnings.Clear();
        InvalidEntryWarnings.Clear();
        _runtimeDropConfigurationState = ObjectRuntimeDropConfigurationState.Empty;
        _runtimeDropConfigurationSignature = "";
        _runtimeDropConfigurationGameDataSignature = null;
        _configuration = new List<PrefabConfigurationEntry>();
        Volatile.Write(ref _synchronizedPayloadReady, false);
    }

    private static List<PrefabConfigurationEntry> CloneAndNormalizeConfigurationEntries(
        List<PrefabConfigurationEntry>? configuration,
        string sourceName)
    {
        List<PrefabConfigurationEntry> normalizedConfiguration =
            NetworkPayloadSyncSupport.CloneEntries(Descriptor, configuration);
        foreach (PrefabConfigurationEntry entry in normalizedConfiguration)
        {
            NormalizeEntry(entry);
            entry.SourcePath = string.IsNullOrWhiteSpace(entry.SourcePath) ? sourceName : entry.SourcePath;
        }

        return normalizedConfiguration;
    }

    private static List<PrefabConfigurationEntry> PrepareLocalConfigurationEntries(
        List<PrefabConfigurationEntry>? configuration,
        string sourceName,
        List<string> warnings)
    {
        List<PrefabConfigurationEntry> normalizedConfiguration =
            CloneAndNormalizeConfigurationEntries(configuration, sourceName);
        List<PrefabConfigurationEntry> acceptedEntries = new();
        foreach (PrefabConfigurationEntry entry in normalizedConfiguration)
        {
            if (!TryAcceptLocalConfigurationEntry(entry, warnings))
            {
                continue;
            }

            acceptedEntries.Add(entry);
        }

        return acceptedEntries;
    }

    private static bool TryAcceptLocalConfigurationEntry(PrefabConfigurationEntry entry, List<string> warnings)
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

        if (!TryResolveConfiguredObjectPrefab(entry.Prefab, out bool hasSupportedObjectComponents))
        {
            warnings.Add($"Entry '{context}' references unknown object prefab '{entry.Prefab}'.");
            return false;
        }

        if (!hasSupportedObjectComponents)
        {
            warnings.Add($"Entry '{context}' references '{entry.Prefab}', but it is not a supported object prefab.");
            return false;
        }

        return true;
    }

    private static bool TryResolveConfiguredObjectPrefab(string prefabName, out bool hasSupportedObjectComponents)
    {
        hasSupportedObjectComponents = true;
        if (ZNetScene.instance == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return true;
        }

        GameObject? prefab = ZNetScene.instance.GetPrefab(prefabName.Trim());
        if (prefab == null)
        {
            hasSupportedObjectComponents = false;
            return false;
        }

        hasSupportedObjectComponents = HasRelevantLiveObjectComponents(prefab);
        return true;
    }

    private static SyncedObjectConfigurationState BuildSyncedConfigurationState(
        List<PrefabConfigurationEntry> configuration,
        string sourceName)
    {
        using InvalidEntryWarningSuppressionScope _ = BeginInvalidEntryWarningSuppressionForSyncedClientBuild(sourceName);
        SyncedObjectConfigurationState state = new();
        foreach (PrefabConfigurationEntry entry in CloneAndNormalizeConfigurationEntries(configuration, sourceName))
        {
            if (string.IsNullOrWhiteSpace(entry.Prefab))
            {
                continue;
            }

            RemoveEffectiveConfigurationEntry(state.Configuration, state.ActiveEntriesByPrefab, entry.Prefab, entry.RuleId);
            if (!entry.Enabled)
            {
                continue;
            }

            state.Configuration.Add(entry);
            GetOrCreateActiveEntries(state.ActiveEntriesByPrefab, entry.Prefab).Add(entry);
        }

        RefreshConfiguredPrefabProfiles(
            state.ActiveEntriesByPrefab,
            state.ConfiguredComponentKindsByPrefab,
            state.ReconcileComponentKindsByPrefab);
        RebuildVneiDisplayEntries(state.Configuration, state.VneiEntriesByPrefab);
        state.VneiEntrySignaturesByPrefab = BuildVneiEntrySignaturesByPrefab(state.VneiEntriesByPrefab);
        state.ConfigurationSignature = NetworkPayloadSyncSupport.ComputeObjectConfigurationSignature(state.Configuration);
        return state;
    }

    private static void CommitSyncedConfigurationState(SyncedObjectConfigurationState state, string payloadToken)
    {
        ResetLoadedConfigurationState();
        _configuration = state.Configuration;
        foreach ((string prefabName, List<PrefabConfigurationEntry> entries) in state.ActiveEntriesByPrefab)
        {
            ActiveEntriesByPrefab[prefabName] = entries;
        }

        PrefabProfileCatalogState.ApplySyncedProfiles(state.ConfiguredComponentKindsByPrefab, state.ReconcileComponentKindsByPrefab);

        foreach ((string prefabName, List<PrefabConfigurationEntry> entries) in state.VneiEntriesByPrefab)
        {
            VneiEntriesByPrefab[prefabName] = entries;
        }

        _configurationSignature = state.ConfigurationSignature;
        LoadState.LastLoadedPayload = payloadToken;
        LoadState.LastRejectedPayload = "";
        LoadState.PendingStrictPayload = "";
        LoadState.LastRejectedValidationKey = "";
        Volatile.Write(ref _synchronizedPayloadReady, true);
        _lastCommittedAuthorityEpoch = DropNSpawnPlugin.IsSourceOfTruth
            ? null
            : NetworkPayloadSyncSupport.CurrentAuthorityEpoch;
    }

    private static LocalLoadResult<PrefabConfigurationEntry> ParseLocalConfigurationDocuments(
        List<ConfigurationLoadSupport.LocalYamlDocument> documents)
    {
        List<PrefabConfigurationEntry> configuration = new();
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
                ParsedObjectConfigurationDocument parsedDocument = ParseConfiguration(yaml, document.Path);
                warnings.AddRange(parsedDocument.Warnings);
                parsedEntryCount += parsedDocument.Configuration.Count;
                List<PrefabConfigurationEntry> sourcedConfiguration =
                    PrepareLocalConfigurationEntries(parsedDocument.Configuration, document.Path, warnings);
                configuration.AddRange(sourcedConfiguration);
                loadedFileCount++;
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Failed to parse {document.Path}{FormatYamlExceptionLocation(ex)}. Object override YAML must start with a root list like '- prefab: ...'. {ex}");
            }
        }

        return new LocalLoadResult<PrefabConfigurationEntry>
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
            "Rejected object reload. Keeping the previous authoritative object configuration.");
        foreach (string error in errors
                     .Where(message => !string.IsNullOrWhiteSpace(message))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogError(error);
        }
    }

    private static bool RemoveEffectiveConfigurationEntry(string prefabName, string ruleId)
    {
        bool removed = RemoveEffectiveConfigurationEntry(_configuration, ActiveEntriesByPrefab, prefabName, ruleId);
        RefreshConfiguredPrefabProfile(prefabName);
        return removed;
    }

    private static bool RemoveEffectiveConfigurationEntry(
        List<PrefabConfigurationEntry> configuration,
        Dictionary<string, List<PrefabConfigurationEntry>> activeEntriesByPrefab,
        string prefabName,
        string ruleId)
    {
        bool removed = false;
        for (int index = configuration.Count - 1; index >= 0; index--)
        {
            PrefabConfigurationEntry existingEntry = configuration[index];
            if (!string.Equals(existingEntry.Prefab, prefabName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existingEntry.RuleId, ruleId, StringComparison.Ordinal))
            {
                continue;
            }

            configuration.RemoveAt(index);
            removed = true;
        }

        if (activeEntriesByPrefab.TryGetValue(prefabName, out List<PrefabConfigurationEntry>? entries))
        {
            for (int index = entries.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(entries[index].RuleId, ruleId, StringComparison.Ordinal))
                {
                    continue;
                }

                entries.RemoveAt(index);
                removed = true;
            }

            if (entries.Count == 0)
            {
                activeEntriesByPrefab.Remove(prefabName);
            }
        }

        return removed;
    }

    private static ParsedObjectConfigurationDocument ParseConfiguration(string yaml, string? sourcePath)
    {
        ParsedObjectConfigurationDocument result = new();
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
                "Object override YAML root must be a sequence.");
        }

        foreach (YamlNode node in sequence.Children)
        {
            if (node is not YamlMappingNode mappingNode)
            {
                result.Warnings.Add(
                    $"Skipped object YAML node at {FormatYamlNodeLocation(sourcePath, node.Start)}. Expected a list item object like '- prefab: wood' but found {DescribeYamlNode(node)}.");
                continue;
            }

            try
            {
                string entryYaml = SerializeYamlNode(mappingNode);
                PrefabConfigurationEntry entry =
                    Deserializer.Deserialize<PrefabConfigurationEntry>(entryYaml) ?? new PrefabConfigurationEntry();
                entry.SourceLine = checked((int)mappingNode.Start.Line);
                entry.SourceColumn = checked((int)mappingNode.Start.Column);
                result.Configuration.Add(entry);
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"Skipped invalid object entry at {FormatYamlNodeLocation(sourcePath, mappingNode.Start)}. {FormatEntryParseFailure(ex)}");
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

    private static void NormalizeEntry(PrefabConfigurationEntry entry)
    {
        entry.Prefab = (entry.Prefab ?? "").Trim();
        NormalizeObjectConditions(entry.Conditions, $"{entry.Prefab}.conditions");
        NormalizeDropTable(entry.DropOnDestroyed);
        NormalizeDropTable(entry.MineRock);
        NormalizeDropTable(entry.MineRock5);
        NormalizeDropTable(entry.TreeBase);
        NormalizeDropTable(entry.TreeLog);
        NormalizeDropTable(entry.Container);
        NormalizeDropTablePayload(entry.Pickable?.ExtraDrops);
        NormalizeDropTablePayload(entry.Fish?.ExtraDrops);
        NormalizeRandomPickableItems(entry.PickableItem?.RandomDrops);
        entry.RuleId = NormalizeOptionalRuleId(entry.RuleId) ?? BuildRuleId(entry);
    }

    private static void NormalizeObjectConditions(ConditionsDefinition? conditions, string context)
    {
        if (conditions == null)
        {
            return;
        }

        if (conditions.Level?.HasValues() == true ||
            conditions.MinLevel.HasValue ||
            conditions.MaxLevel.HasValue)
        {
            WarnInvalidEntry($"Entry '{context}' uses conditions.level, but level filters are only valid for character-drop conditions. The key was ignored.");
            conditions.Level = null;
            conditions.MinLevel = null;
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
    }

    private static string BuildRuleId(PrefabConfigurationEntry entry)
    {
        PrefabConfigurationEntry normalizedEntry = new()
        {
            Prefab = entry.Prefab,
            Enabled = true,
            Conditions = entry.Conditions,
            DropOnDestroyed = entry.DropOnDestroyed,
            MineRock = entry.MineRock,
            MineRock5 = entry.MineRock5,
            TreeBase = entry.TreeBase,
            TreeLog = entry.TreeLog,
            Container = entry.Container,
            Destructible = entry.Destructible,
            Pickable = entry.Pickable,
            PickableItem = entry.PickableItem,
            Fish = entry.Fish
        };

        return $"{entry.Prefab}:{NetworkPayloadSyncSupport.ComputeObjectEntryIdentitySignature(normalizedEntry)}";
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

    private static List<PrefabConfigurationEntry> GetOrCreateActiveEntries(string prefabName)
    {
        return GetOrCreateActiveEntries(ActiveEntriesByPrefab, prefabName);
    }

    private static List<PrefabConfigurationEntry> GetOrCreateActiveEntries(
        Dictionary<string, List<PrefabConfigurationEntry>> activeEntriesByPrefab,
        string prefabName)
    {
        if (!activeEntriesByPrefab.TryGetValue(prefabName, out List<PrefabConfigurationEntry>? entries))
        {
            entries = new List<PrefabConfigurationEntry>();
            activeEntriesByPrefab[prefabName] = entries;
        }

        return entries;
    }

    private static bool CanUseLazyDamageableScalarFastPath(
        PrefabConfigurationEntry entry,
        LiveObjectComponentKind componentKind)
    {
        if (componentKind != LiveObjectComponentKind.MineRock &&
            componentKind != LiveObjectComponentKind.MineRock5 &&
            componentKind != LiveObjectComponentKind.TreeBase &&
            componentKind != LiveObjectComponentKind.TreeLog)
        {
            return false;
        }

        return !DropConditionEvaluator.HasDynamicConditions(entry.Conditions);
    }

    // Container defaultItems overrides are template-only by default. They apply via prefab/AddDefaultItems
    // and do not retroactively mutate existing inventories unless a future live-mutation mode is added.
    private static bool ContainerNeedsLiveMutation(PrefabConfigurationEntry entry)
    {
        return false;
    }

    private static PrefabConfigurationEntry? CreateVneiDisplayBaseEntry(PrefabConfigurationEntry? entry)
    {
        if (entry == null ||
            !entry.Enabled ||
            string.IsNullOrWhiteSpace(entry.Prefab))
        {
            return null;
        }

        DropTableDefinition? dropOnDestroyed = CreateClientProjectionDropTableDefinition(entry.DropOnDestroyed);
        DamageableDropTableDefinition? mineRock = CreateClientProjectionDamageableDropTableDefinition(entry.MineRock);
        DamageableDropTableDefinition? mineRock5 = CreateClientProjectionDamageableDropTableDefinition(entry.MineRock5);
        DamageableDropTableDefinition? treeBase = CreateClientProjectionDamageableDropTableDefinition(entry.TreeBase);
        DamageableDropTableDefinition? treeLog = CreateClientProjectionDamageableDropTableDefinition(entry.TreeLog);
        DropTableDefinition? container = CreateClientProjectionDropTableDefinition(entry.Container);
        PickableDefinition? pickable = CreateClientProjectionPickableDefinition(entry.Pickable);
        PickableItemDefinition? pickableItem = CreateClientProjectionPickableItemDefinition(entry.PickableItem);
        FishDefinition? fish = CreateClientProjectionFishDefinition(entry.Fish);
        DestructibleDefinition? destructible = CreateClientProjectionDestructibleDefinition(entry.Destructible);
        if (dropOnDestroyed == null &&
            mineRock == null &&
            mineRock5 == null &&
            treeBase == null &&
            treeLog == null &&
            container == null &&
            pickable == null &&
            pickableItem == null &&
            fish == null &&
            destructible == null)
        {
            return null;
        }

        return new PrefabConfigurationEntry
        {
            RuleId = entry.RuleId,
            Prefab = entry.Prefab,
            Enabled = true,
            Conditions = entry.Conditions,
            DropOnDestroyed = dropOnDestroyed,
            MineRock = mineRock,
            MineRock5 = mineRock5,
            TreeBase = treeBase,
            TreeLog = treeLog,
            Container = container,
            Pickable = pickable,
            PickableItem = pickableItem,
            Fish = fish,
            Destructible = destructible
        };
    }

    private static DropTableDefinition? CreateClientProjectionDropTableDefinition(DropTableDefinition? definition)
    {
        if (!HasDropTableOverride(definition))
        {
            return null;
        }

        return new DropTableDefinition
        {
            Rolls = CloneIntRange(definition!.Rolls),
            DropMin = definition.DropMin,
            DropMax = definition.DropMax,
            DropChance = definition.DropChance,
            OneOfEach = definition.OneOfEach,
            Drops = CloneDropEntries(definition.Drops)
        };
    }

    private static DamageableDropTableDefinition? CreateClientProjectionDamageableDropTableDefinition(DamageableDropTableDefinition? definition)
    {
        if (!HasDamageableOverride(definition))
        {
            return null;
        }

        return new DamageableDropTableDefinition
        {
            Health = definition!.Health,
            MinToolTier = definition.MinToolTier,
            Rolls = CloneIntRange(definition.Rolls),
            DropMin = definition.DropMin,
            DropMax = definition.DropMax,
            DropChance = definition.DropChance,
            OneOfEach = definition.OneOfEach,
            Drops = CloneDropEntries(definition.Drops)
        };
    }

    private static PickableDefinition? CreateClientProjectionPickableDefinition(PickableDefinition? definition)
    {
        if (!HasClientProjectedPickableOverride(definition))
        {
            return null;
        }

        return new PickableDefinition
        {
            OverrideName = definition!.OverrideName,
            Drop = definition.Drop == null
                ? null
                : new PickableDropDefinition
                {
                    Item = definition.Drop.Item,
                    Amount = definition.Drop.Amount,
                    MinAmountScaled = definition.Drop.MinAmountScaled,
                    DontScale = definition.Drop.DontScale
                },
            ExtraDrops = CreateClientProjectionDropTablePayloadDefinition(definition.ExtraDrops)
        };
    }

    private static PickableItemDefinition? CreateClientProjectionPickableItemDefinition(PickableItemDefinition? definition)
    {
        if (!HasPickableItemOverride(definition))
        {
            return null;
        }

        return new PickableItemDefinition
        {
            RandomDrops = CloneRandomPickableItems(definition!.RandomDrops),
            Drop = definition.Drop == null
                ? null
                : new PickableItemDropDefinition
                {
                    Item = definition.Drop.Item,
                    Stack = definition.Drop.Stack
            }
        };
    }

    private static FishDefinition? CreateClientProjectionFishDefinition(FishDefinition? definition)
    {
        if (!HasFishOverride(definition))
        {
            return null;
        }

        return new FishDefinition
        {
            ExtraDrops = CreateClientProjectionDropTablePayloadDefinition(definition!.ExtraDrops)
        };
    }

    private static DestructibleDefinition? CreateClientProjectionDestructibleDefinition(DestructibleDefinition? definition)
    {
        if (!HasDestructibleOverride(definition))
        {
            return null;
        }

        return new DestructibleDefinition
        {
            Health = definition!.Health,
            MinToolTier = definition.MinToolTier,
            DestructibleType = definition.DestructibleType,
            SpawnWhenDestroyed = definition.SpawnWhenDestroyed
        };
    }

    private static DropTablePayloadDefinition? CreateClientProjectionDropTablePayloadDefinition(DropTablePayloadDefinition? definition)
    {
        if (!HasDropTableOverride(definition))
        {
            return null;
        }

        return new DropTablePayloadDefinition
        {
            Rolls = CloneIntRange(definition!.Rolls),
            DropMin = definition.DropMin,
            DropMax = definition.DropMax,
            DropChance = definition.DropChance,
            OneOfEach = definition.OneOfEach,
            Drops = CloneDropEntries(definition.Drops)
        };
    }

    private static List<DropEntryDefinition>? CloneDropEntries(List<DropEntryDefinition>? definitions)
    {
        return definitions?.Select(definition => new DropEntryDefinition
        {
            Item = definition.Item,
            Stack = CloneIntRange(definition.Stack),
            StackMin = definition.StackMin,
            StackMax = definition.StackMax,
            Weight = definition.Weight,
            DontScale = definition.DontScale
        }).ToList();
    }

    private static List<RandomPickableItemDefinition>? CloneRandomPickableItems(List<RandomPickableItemDefinition>? definitions)
    {
        return definitions?.Select(definition => new RandomPickableItemDefinition
        {
            Item = definition.Item,
            Stack = CloneIntRange(definition.Stack),
            StackMin = definition.StackMin,
            StackMax = definition.StackMax,
            Weight = definition.Weight
        }).ToList();
    }

    private static IntRangeDefinition? CloneIntRange(IntRangeDefinition? range)
    {
        return range == null
            ? null
            : new IntRangeDefinition
            {
                Min = range.Min,
                Max = range.Max
            };
    }

    private static void RebuildVneiDisplayEntries(IEnumerable<PrefabConfigurationEntry> entries)
    {
        RebuildVneiDisplayEntries(entries, VneiEntriesByPrefab);
    }

    private static void RebuildVneiDisplayEntries(
        IEnumerable<PrefabConfigurationEntry> entries,
        Dictionary<string, List<PrefabConfigurationEntry>> vneiEntriesByPrefab)
    {
        vneiEntriesByPrefab.Clear();
        foreach (PrefabConfigurationEntry entry in entries ?? Enumerable.Empty<PrefabConfigurationEntry>())
        {
            PrefabConfigurationEntry? projection = CreateVneiProjectionEntry(entry);
            if (projection == null)
            {
                continue;
            }

            if (!vneiEntriesByPrefab.TryGetValue(projection.Prefab, out List<PrefabConfigurationEntry>? prefabEntries))
            {
                prefabEntries = new List<PrefabConfigurationEntry>();
                vneiEntriesByPrefab[projection.Prefab] = prefabEntries;
            }

            prefabEntries.Add(projection);
        }
    }

    private static PrefabConfigurationEntry? CreateVneiProjectionEntry(PrefabConfigurationEntry? entry)
    {
        if (entry == null ||
            !entry.Enabled ||
            string.IsNullOrWhiteSpace(entry.Prefab))
        {
            return null;
        }

        PrefabConfigurationEntry? projection = CreateVneiDisplayBaseEntry(entry);
        if (projection == null)
        {
            bool hasVneiVisibleFields =
                HasDropTableOverride(entry.DropOnDestroyed) ||
                HasDamageableOverride(entry.MineRock) ||
                HasDamageableOverride(entry.MineRock5) ||
                HasDamageableOverride(entry.TreeBase) ||
                HasDamageableOverride(entry.TreeLog) ||
                HasDropTableOverride(entry.Container) ||
                HasFishOverride(entry.Fish) ||
                HasDestructibleOverride(entry.Destructible);
            if (!hasVneiVisibleFields)
            {
                return null;
            }

            projection = new PrefabConfigurationEntry
            {
                RuleId = entry.RuleId,
                Prefab = entry.Prefab,
                Enabled = true,
                Conditions = entry.Conditions
            };
        }

        projection.PickableItem = null;
        return projection;
    }

    private static Dictionary<string, string> BuildVneiEntrySignaturesByPrefab()
    {
        return BuildVneiEntrySignaturesByPrefab(VneiEntriesByPrefab);
    }

    private static Dictionary<string, string> BuildVneiEntrySignaturesByPrefab(
        Dictionary<string, List<PrefabConfigurationEntry>> vneiEntriesByPrefab)
    {
        return DomainEntrySignatureSupport.BuildSignaturesByKey(
            vneiEntriesByPrefab,
            entries => NetworkPayloadSyncSupport.ComputeObjectConfigurationSignature(entries
                .OrderBy(entry => entry.RuleId, StringComparer.Ordinal)
                .ToList()));
    }

    private static List<PrefabConfigurationEntry>? GetVneiEntries(string prefabName)
    {
        if (VneiEntriesByPrefab.TryGetValue(prefabName ?? "", out List<PrefabConfigurationEntry>? projectedEntries))
        {
            return projectedEntries;
        }

        return ActiveEntriesByPrefab.TryGetValue(prefabName ?? "", out List<PrefabConfigurationEntry>? activeEntries)
            ? activeEntries
            : null;
    }

    private static bool HasDropTableOverride(DropTablePayloadDefinition? definition)
    {
        return definition != null &&
               (definition.Rolls?.HasValues() == true ||
                definition.DropMin.HasValue ||
                definition.DropMax.HasValue ||
                definition.DropChance.HasValue ||
                definition.OneOfEach.HasValue ||
                definition.Drops != null);
    }

    private static bool HasDamageableHealthOverride(DamageableDropTableDefinition? definition)
    {
        return definition?.Health.HasValue == true;
    }

    private static bool HasDamageableMinToolTierOverride(DamageableDropTableDefinition? definition)
    {
        return definition?.MinToolTier.HasValue == true;
    }

    private static bool HasDamageableOverride(DamageableDropTableDefinition? definition)
    {
        return HasDropTableOverride(definition) ||
               HasDamageableHealthOverride(definition) ||
               HasDamageableMinToolTierOverride(definition);
    }

    private static bool HasDestructibleHealthOverride(DestructibleDefinition? definition)
    {
        return definition?.Health.HasValue == true;
    }

    private static bool HasDestructibleMinToolTierOverride(DestructibleDefinition? definition)
    {
        return definition?.MinToolTier.HasValue == true;
    }

    private static bool HasDestructibleTypeOverride(DestructibleDefinition? definition)
    {
        return !string.IsNullOrWhiteSpace(definition?.DestructibleType);
    }

    private static bool HasDestructibleSpawnWhenDestroyedOverride(DestructibleDefinition? definition)
    {
        return !string.IsNullOrWhiteSpace(definition?.SpawnWhenDestroyed);
    }

    private static bool HasDestructibleComponentStateOverride(DestructibleDefinition? definition)
    {
        return definition != null &&
               (!string.IsNullOrWhiteSpace(definition.DestructibleType) ||
                !string.IsNullOrWhiteSpace(definition.SpawnWhenDestroyed));
    }

    private static bool HasDestructibleOverride(DestructibleDefinition? definition)
    {
        return HasDestructibleHealthOverride(definition) ||
               HasDestructibleMinToolTierOverride(definition) ||
               HasDestructibleComponentStateOverride(definition);
    }

    private static void NormalizeDropTable(DropTableDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        NormalizeDropTablePayload(definition);
    }

    private static void NormalizeDropTablePayload(DropTablePayloadDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        if (definition.Rolls?.HasValues() == true)
        {
            definition.DropMin = RangeFormatting.GetMin(definition.Rolls, definition.DropMin);
            definition.DropMax = RangeFormatting.GetMax(definition.Rolls, definition.DropMin, definition.DropMax);
        }

        if (definition.Drops == null)
        {
            return;
        }

        foreach (DropEntryDefinition drop in definition.Drops)
        {
            if (drop.Stack?.HasValues() == true)
            {
                drop.StackMin = RangeFormatting.GetMin(drop.Stack, drop.StackMin);
                drop.StackMax = RangeFormatting.GetMax(drop.Stack, drop.StackMin, drop.StackMax);
            }
        }
    }

    private static void NormalizeRandomPickableItems(List<RandomPickableItemDefinition>? items)
    {
        if (items == null)
        {
            return;
        }

        foreach (RandomPickableItemDefinition item in items)
        {
            if (item.Stack?.HasValues() == true)
            {
                item.StackMin = RangeFormatting.GetMin(item.Stack, item.StackMin);
                item.StackMax = RangeFormatting.GetMax(item.Stack, item.StackMin, item.StackMax);
            }
        }
    }

    private static bool HasPickableOverride(PickableDefinition? definition)
    {
        return definition != null &&
               (HasPickableDropOverride(definition.Drop) ||
                 definition.OverrideName != null ||
                 HasDropTableOverride(definition.ExtraDrops));
    }

    private static bool HasClientVisiblePickableOverride(PickableDefinition? definition)
    {
        return definition != null &&
               (HasPickableDropOverride(definition.Drop) ||
                definition.OverrideName != null);
    }

    private static bool HasClientProjectedPickableOverride(PickableDefinition? definition)
    {
        return definition != null &&
               (HasPickableDropOverride(definition.Drop) ||
                definition.OverrideName != null ||
                HasDropTableOverride(definition.ExtraDrops));
    }

    private static bool HasPickableDropOverride(PickableDropDefinition? definition)
    {
        return definition != null &&
               (!string.IsNullOrWhiteSpace(definition.Item) ||
                definition.Amount.HasValue ||
                definition.MinAmountScaled.HasValue ||
                definition.DontScale.HasValue);
    }

    private static bool HasPickableItemDropOverride(PickableItemDropDefinition? definition)
    {
        return definition != null &&
               (!string.IsNullOrWhiteSpace(definition.Item) ||
                definition.Stack.HasValue);
    }

    private static bool HasPickableItemOverride(PickableItemDefinition? definition)
    {
        return definition != null &&
               (HasPickableItemDropOverride(definition.Drop) ||
                definition.RandomDrops != null);
    }

    private static bool HasFishOverride(FishDefinition? definition)
    {
        return definition != null && HasDropTableOverride(definition.ExtraDrops);
    }

    private static bool IsEventOnlyDropTableFastPathKind(LiveObjectComponentKind componentKind)
    {
        return componentKind == LiveObjectComponentKind.DropOnDestroyed ||
               componentKind == LiveObjectComponentKind.MineRock ||
               componentKind == LiveObjectComponentKind.MineRock5 ||
               componentKind == LiveObjectComponentKind.TreeBase ||
               componentKind == LiveObjectComponentKind.TreeLog ||
               componentKind == LiveObjectComponentKind.Container;
    }

    private static bool UsesLiveDropTableReconcile(LiveObjectComponentKind componentKind)
    {
        return !IsEventOnlyDropTableFastPathKind(componentKind);
    }

    private static bool RequiresLiveReconcile(DropTableDefinition? definition, LiveObjectComponentKind componentKind)
    {
        return definition != null && !IsEventOnlyDropTableFastPathKind(componentKind);
    }

    private static bool RequiresLiveReconcile(DamageableDropTableDefinition? definition, LiveObjectComponentKind componentKind)
    {
        return definition != null &&
               (HasDamageableHealthOverride(definition) ||
                HasDamageableMinToolTierOverride(definition) ||
                (!IsEventOnlyDropTableFastPathKind(componentKind) && HasDropTableOverride(definition)));
    }

    private static bool RequiresLiveReconcile(PrefabConfigurationEntry entry, DestructibleDefinition? definition)
    {
        if (definition == null)
        {
            return false;
        }

        return (HasDestructibleHealthOverride(definition) ||
                HasDestructibleMinToolTierOverride(definition) ||
                HasDestructibleTypeOverride(definition)) &&
               !CanUseLazyDestructibleScalarFastPath(entry);
    }

    private static bool CanUseLazyDestructibleScalarFastPath(PrefabConfigurationEntry entry)
    {
        return !DropConditionEvaluator.HasDynamicConditions(entry.Conditions);
    }

    private static bool ShouldReconcileLocally(GameObject gameObject)
    {
        return PluginSettingsFacade.IsObjectDomainEnabled();
    }

    private static bool TryGetConditionalContext(GameObject gameObject, out string prefabName, out PrefabSnapshot snapshot, out List<PrefabConfigurationEntry> entries)
    {
        snapshot = null!;
        entries = null!;
        if (!CanUseCurrentRuntimeState())
        {
            prefabName = "";
            return false;
        }

        prefabName = GetPrefabName(gameObject);
        if (!ActiveEntriesByPrefab.TryGetValue(prefabName, out entries) ||
            entries.Count == 0 ||
            !SnapshotsByPrefab.TryGetValue(prefabName, out snapshot))
        {
            return false;
        }

        if (!ShouldApplyToInstance(gameObject))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetConditionalDropContext(
        GameObject gameObject,
        out string prefabName,
        out PrefabSnapshot snapshot,
        out List<CompiledObjectDropRule> compiledRules)
    {
        compiledRules = null!;
        if (!TryGetConditionalContext(gameObject, out prefabName, out snapshot, out _))
        {
            return false;
        }

        EnsureRuntimeDropConfigurationState();
        return _runtimeDropConfigurationState.PlansByPrefab.TryGetValue(prefabName, out CompiledObjectPrefabPlan? prefabPlan) &&
               (compiledRules = prefabPlan.Rules) != null &&
               compiledRules.Count > 0;
    }

    private static bool TryGetCompiledObjectDropRule(PrefabConfigurationEntry entry, out CompiledObjectDropRule? compiledRule)
    {
        EnsureRuntimeDropConfigurationState();
        return _runtimeDropConfigurationState.RulesByEntry.TryGetValue(entry, out compiledRule);
    }

    private static bool TryGetStaticDropTableTemplate(
        string prefabName,
        LiveObjectComponentKind componentKind,
        out StaticCompiledDropTableTemplate? template)
    {
        template = null;
        EnsureRuntimeDropConfigurationState();
        return _runtimeDropConfigurationState.PlansByPrefab.TryGetValue(prefabName, out CompiledObjectPrefabPlan? prefabPlan) &&
               prefabPlan.StaticDropTableTemplates.TryGetValue(componentKind, out template);
    }

    private static bool TryGetOverrideDropTable(
        GameObject gameObject,
        Func<CompiledObjectDropRule, CompiledDropTablePayload?> payloadSelector,
        Func<PrefabSnapshot, DropTable?> snapshotSelector,
        LiveObjectComponentKind componentKind,
        string contextSuffix,
        out DropTable? overrideTable)
    {
        return TryGetCachedEventDropTable(gameObject, payloadSelector, snapshotSelector, componentKind, out overrideTable);
    }

    internal static DropTable? OverrideConditionalDropOnDestroyed(DropOnDestroyed dropOnDestroyed)
    {
        lock (Sync)
        {
            if (!TryGetOverrideDropTable(dropOnDestroyed.gameObject, rule => rule.DropOnDestroyed, snapshot => snapshot.DropOnDestroyed, LiveObjectComponentKind.DropOnDestroyed, "DropOnDestroyed", out DropTable? overrideTable))
            {
                return null;
            }

            DropTable previous = dropOnDestroyed.m_dropWhenDestroyed;
            dropOnDestroyed.m_dropWhenDestroyed = overrideTable!;
            return previous;
        }
    }

    internal static DropTable? OverrideConditionalMineRockDrops(MineRock mineRock)
    {
        lock (Sync)
        {
            if (!TryGetOverrideDropTable(mineRock.gameObject, rule => rule.MineRock, snapshot => snapshot.MineRock, LiveObjectComponentKind.MineRock, "MineRock", out DropTable? overrideTable))
            {
                return null;
            }

            DropTable previous = mineRock.m_dropItems;
            mineRock.m_dropItems = overrideTable!;
            return previous;
        }
    }

    internal static DropTable? OverrideConditionalMineRock5Drops(MineRock5 mineRock5)
    {
        lock (Sync)
        {
            if (!TryGetOverrideDropTable(mineRock5.gameObject, rule => rule.MineRock5, snapshot => snapshot.MineRock5, LiveObjectComponentKind.MineRock5, "MineRock5", out DropTable? overrideTable))
            {
                return null;
            }

            DropTable previous = mineRock5.m_dropItems;
            mineRock5.m_dropItems = overrideTable!;
            return previous;
        }
    }

    internal static DropTable? OverrideContainerDrops(Container container)
    {
        lock (Sync)
        {
            if (!TryGetEffectiveContainerDropTable(container.gameObject, out DropTable? overrideTable))
            {
                return null;
            }

            DropTable previous = container.m_defaultItems;
            container.m_defaultItems = overrideTable!;
            return previous;
        }
    }

    private static bool TryGetEffectiveContainerDropTable(GameObject gameObject, out DropTable? overrideTable)
    {
        return TryGetCachedEventDropTable(
            gameObject,
            rule => rule.Container,
            snapshot => snapshot.Container,
            LiveObjectComponentKind.Container,
            out overrideTable);
    }

    internal static DropTable? OverrideConditionalTreeBaseDrops(TreeBase treeBase)
    {
        lock (Sync)
        {
            if (!TryGetOverrideDropTable(treeBase.gameObject, rule => rule.TreeBase, snapshot => snapshot.TreeBase, LiveObjectComponentKind.TreeBase, "TreeBase", out DropTable? overrideTable))
            {
                return null;
            }

            DropTable previous = treeBase.m_dropWhenDestroyed;
            treeBase.m_dropWhenDestroyed = overrideTable!;
            return previous;
        }
    }

    internal static DropTable? OverrideConditionalTreeLogDrops(TreeLog treeLog)
    {
        lock (Sync)
        {
            if (!TryGetOverrideDropTable(treeLog.gameObject, rule => rule.TreeLog, snapshot => snapshot.TreeLog, LiveObjectComponentKind.TreeLog, "TreeLog", out DropTable? overrideTable))
            {
                return null;
            }

            DropTable previous = treeLog.m_dropWhenDestroyed;
            treeLog.m_dropWhenDestroyed = overrideTable!;
            return previous;
        }
    }

    internal static GameObject? OverrideConditionalDestructibleSpawnWhenDestroyed(Destructible destructible)
    {
        lock (Sync)
        {
            if (!TryGetConditionalDropContext(destructible.gameObject, out _, out _, out List<CompiledObjectDropRule> compiledRules))
            {
                return null;
            }

            GameObject? effectiveSpawnPrefab = null;
            bool hasOverride = false;
            foreach (CompiledObjectDropRule compiledRule in compiledRules)
            {
                if (compiledRule.Destructible?.HasSpawnWhenDestroyedOverride != true ||
                    !EntryMatches(destructible.gameObject, compiledRule.Entry, allowConditionalMatches: true))
                {
                    continue;
                }

                hasOverride = true;
                effectiveSpawnPrefab = compiledRule.Destructible.SpawnWhenDestroyed;
            }

            if (!hasOverride)
            {
                return null;
            }

            GameObject? previous = destructible.m_spawnWhenDestroyed;
            destructible.m_spawnWhenDestroyed = effectiveSpawnPrefab;
            return previous;
        }
    }

    private static bool TryResolveLazyDamageableScalars(
        GameObject gameObject,
        LiveObjectComponentKind componentKind,
        out CompiledDamageableScalarDefinition resolvedScalars,
        out int signature)
    {
        resolvedScalars = null!;
        signature = 0;
        if (!TryGetConditionalDropContext(gameObject, out _, out _, out List<CompiledObjectDropRule> compiledRules))
        {
            return false;
        }

        CompiledDamageableScalarDefinition effectiveScalars = new();
        foreach (CompiledObjectDropRule compiledRule in compiledRules)
        {
            if (!CanUseLazyDamageableScalarFastPath(compiledRule.Entry, componentKind) ||
                !EntryMatches(gameObject, compiledRule.Entry, allowConditionalMatches: true))
            {
                continue;
            }

            CompiledDamageableScalarDefinition? candidateScalars = GetDamageableScalarDefinition(compiledRule, componentKind);
            if (candidateScalars == null)
            {
                continue;
            }

            if (candidateScalars.HasHealthOverride)
            {
                effectiveScalars.HasHealthOverride = true;
                effectiveScalars.Health = candidateScalars.Health;
            }

            if (candidateScalars.HasMinToolTierOverride)
            {
                effectiveScalars.HasMinToolTierOverride = true;
                effectiveScalars.MinToolTier = candidateScalars.MinToolTier;
            }
        }

        if (!effectiveScalars.HasHealthOverride &&
            !effectiveScalars.HasMinToolTierOverride)
        {
            return false;
        }

        resolvedScalars = effectiveScalars;
        signature = ComputeLazyDamageableScalarSignature(effectiveScalars);
        return true;
    }

    private static bool TryResolveLazyDestructibleScalars(
        GameObject gameObject,
        out CompiledDestructibleComponentDefinition resolvedDefinition,
        out int signature)
    {
        resolvedDefinition = null!;
        signature = 0;
        if (!TryGetConditionalDropContext(gameObject, out _, out _, out List<CompiledObjectDropRule> compiledRules))
        {
            return false;
        }

        CompiledDestructibleComponentDefinition effectiveDefinition = new();
        foreach (CompiledObjectDropRule compiledRule in compiledRules)
        {
            if (compiledRule.Destructible == null ||
                !CanUseLazyDestructibleScalarFastPath(compiledRule.Entry) ||
                !EntryMatches(gameObject, compiledRule.Entry, allowConditionalMatches: true))
            {
                continue;
            }

            if (compiledRule.Destructible.HasHealthOverride)
            {
                effectiveDefinition.HasHealthOverride = true;
                effectiveDefinition.Health = compiledRule.Destructible.Health;
            }

            if (compiledRule.Destructible.HasMinToolTierOverride)
            {
                effectiveDefinition.HasMinToolTierOverride = true;
                effectiveDefinition.MinToolTier = compiledRule.Destructible.MinToolTier;
            }
        }

        if (!effectiveDefinition.HasHealthOverride &&
            !effectiveDefinition.HasMinToolTierOverride)
        {
            return false;
        }

        resolvedDefinition = effectiveDefinition;
        signature = ComputeLazyDestructibleScalarSignature(effectiveDefinition);
        return true;
    }

    private static bool TryResolveLazyDestructibleType(GameObject gameObject, out DestructibleType resolvedType)
    {
        resolvedType = DestructibleType.Default;
        List<CompiledObjectDropRule>? compiledRules;
        if (!TryGetConditionalContext(gameObject, out string prefabName, out PrefabSnapshot snapshot, out _) ||
            snapshot.Destructible == null)
        {
            return false;
        }

        EnsureRuntimeDropConfigurationState();
        if (!_runtimeDropConfigurationState.PlansByPrefab.TryGetValue(prefabName, out CompiledObjectPrefabPlan? prefabPlan) ||
            (compiledRules = prefabPlan.Rules) == null ||
            compiledRules.Count == 0)
        {
            return false;
        }

        bool hasLazyTypeOverride = false;
        DestructibleType effectiveType = snapshot.Destructible.DestructibleType;
        foreach (CompiledObjectDropRule compiledRule in compiledRules)
        {
            if (compiledRule.Destructible?.HasDestructibleTypeOverride != true)
            {
                continue;
            }

            if (!CanUseLazyDestructibleScalarFastPath(compiledRule.Entry))
            {
                return false;
            }

            hasLazyTypeOverride = true;
            if (EntryMatches(gameObject, compiledRule.Entry, allowConditionalMatches: true))
            {
                effectiveType = compiledRule.Destructible.DestructibleType;
            }
        }

        if (!hasLazyTypeOverride)
        {
            return false;
        }

        resolvedType = effectiveType;
        return true;
    }

    private static CompiledDamageableScalarDefinition? GetDamageableScalarDefinition(
        CompiledObjectDropRule compiledRule,
        LiveObjectComponentKind componentKind)
    {
        return componentKind switch
        {
            LiveObjectComponentKind.MineRock => compiledRule.MineRockScalars,
            LiveObjectComponentKind.MineRock5 => compiledRule.MineRock5Scalars,
            LiveObjectComponentKind.TreeBase => compiledRule.TreeBaseScalars,
            LiveObjectComponentKind.TreeLog => compiledRule.TreeLogScalars,
            _ => null
        };
    }

    private static int ComputeLazyDamageableScalarSignature(CompiledDamageableScalarDefinition scalars)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (scalars.HasHealthOverride ? 1 : 0);
            hash = hash * 31 + (scalars.HasHealthOverride ? BitConverter.SingleToInt32Bits(scalars.Health) : 0);
            hash = hash * 31 + (scalars.HasMinToolTierOverride ? 1 : 0);
            hash = hash * 31 + (scalars.HasMinToolTierOverride ? scalars.MinToolTier : 0);
            return hash;
        }
    }

    private static int ComputeLazyDestructibleScalarSignature(CompiledDestructibleComponentDefinition definition)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (definition.HasHealthOverride ? 1 : 0);
            hash = hash * 31 + (definition.HasHealthOverride ? BitConverter.SingleToInt32Bits(definition.Health) : 0);
            hash = hash * 31 + (definition.HasMinToolTierOverride ? 1 : 0);
            hash = hash * 31 + (definition.HasMinToolTierOverride ? definition.MinToolTier : 0);
            return hash;
        }
    }

    private static void ApplyLazyTreeBaseScalars(TreeBase treeBase, CompiledDamageableScalarDefinition scalars, int signature)
    {
        ZNetView? nview = treeBase.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int existingSignature = zdo?.GetInt(TreeBaseLazyScalarSignatureKey, int.MinValue) ?? int.MinValue;
        bool signatureMatches = existingSignature == signature;
        bool healthMatches = !scalars.HasHealthOverride || Mathf.Approximately(treeBase.m_health, scalars.Health);
        bool minToolTierMatches = !scalars.HasMinToolTierOverride || treeBase.m_minToolTier == scalars.MinToolTier;
        if (healthMatches &&
            minToolTierMatches &&
            (signatureMatches || nview == null || zdo == null || !nview.IsOwner()))
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            treeBase.m_health = scalars.Health;
        }

        if (scalars.HasMinToolTierOverride)
        {
            treeBase.m_minToolTier = scalars.MinToolTier;
        }

        if (nview == null || zdo == null || !nview.IsOwner() || signatureMatches)
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            zdo.Set(ZDOVars.s_health, Mathf.Max(scalars.Health, 0.01f));
        }

        zdo.Set(TreeBaseLazyScalarSignatureKey, signature);
    }

    private static void ApplyLazyTreeLogScalars(TreeLog treeLog, CompiledDamageableScalarDefinition scalars, int signature)
    {
        ZNetView? nview = treeLog.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int existingSignature = zdo?.GetInt(TreeLogLazyScalarSignatureKey, int.MinValue) ?? int.MinValue;
        bool signatureMatches = existingSignature == signature;
        bool healthMatches = !scalars.HasHealthOverride || Mathf.Approximately(treeLog.m_health, scalars.Health);
        bool minToolTierMatches = !scalars.HasMinToolTierOverride || treeLog.m_minToolTier == scalars.MinToolTier;
        if (healthMatches &&
            minToolTierMatches &&
            (signatureMatches || nview == null || zdo == null || !nview.IsOwner()))
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            treeLog.m_health = scalars.Health;
        }

        if (scalars.HasMinToolTierOverride)
        {
            treeLog.m_minToolTier = scalars.MinToolTier;
        }

        if (nview == null || zdo == null || !nview.IsOwner() || signatureMatches)
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            zdo.Set(ZDOVars.s_health, GetScaledMineHealth(Mathf.Max(scalars.Health, 0.01f)));
        }

        zdo.Set(TreeLogLazyScalarSignatureKey, signature);
    }

    private static void ApplyLazyMineRockScalars(MineRock mineRock, CompiledDamageableScalarDefinition scalars, int signature)
    {
        ZNetView? nview = mineRock.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int existingSignature = zdo?.GetInt(MineRockLazyScalarSignatureKey, int.MinValue) ?? int.MinValue;
        bool signatureMatches = existingSignature == signature;
        bool healthMatches = !scalars.HasHealthOverride || Mathf.Approximately(mineRock.m_health, scalars.Health);
        bool minToolTierMatches = !scalars.HasMinToolTierOverride || mineRock.m_minToolTier == scalars.MinToolTier;
        if (healthMatches &&
            minToolTierMatches &&
            (signatureMatches || nview == null || zdo == null || !nview.IsOwner()))
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            mineRock.m_health = scalars.Health;
        }

        if (scalars.HasMinToolTierOverride)
        {
            mineRock.m_minToolTier = scalars.MinToolTier;
        }

        if (nview == null || zdo == null || !nview.IsOwner() || signatureMatches)
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            SetMineRockAreaHealthAbsolute(mineRock, GetScaledMineHealth(Mathf.Max(scalars.Health, 0.01f)));
        }

        zdo.Set(MineRockLazyScalarSignatureKey, signature);
    }

    private static void ApplyLazyMineRock5Scalars(MineRock5 mineRock5, CompiledDamageableScalarDefinition scalars, int signature)
    {
        ZNetView? nview = mineRock5.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int existingSignature = zdo?.GetInt(MineRock5LazyScalarSignatureKey, int.MinValue) ?? int.MinValue;
        bool signatureMatches = existingSignature == signature;
        bool healthMatches = !scalars.HasHealthOverride || Mathf.Approximately(mineRock5.m_health, scalars.Health);
        bool minToolTierMatches = !scalars.HasMinToolTierOverride || mineRock5.m_minToolTier == scalars.MinToolTier;
        if (healthMatches &&
            minToolTierMatches &&
            (signatureMatches || nview == null || zdo == null || !nview.IsOwner()))
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            mineRock5.m_health = scalars.Health;
        }

        if (scalars.HasMinToolTierOverride)
        {
            mineRock5.m_minToolTier = scalars.MinToolTier;
        }

        if (nview == null || zdo == null || !nview.IsOwner() || signatureMatches)
        {
            return;
        }

        if (scalars.HasHealthOverride)
        {
            SetMineRock5AreaHealthAbsolute(mineRock5, GetScaledMineHealth(Mathf.Max(scalars.Health, 0.01f)));
        }

        zdo.Set(MineRock5LazyScalarSignatureKey, signature);
    }

    private static void ApplyLazyDestructibleScalars(Destructible destructible, CompiledDestructibleComponentDefinition definition, int signature)
    {
        ZNetView? nview = destructible.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int existingSignature = zdo?.GetInt(DestructibleLazyScalarSignatureKey, int.MinValue) ?? int.MinValue;
        bool signatureMatches = existingSignature == signature;
        bool healthMatches = !definition.HasHealthOverride || Mathf.Approximately(destructible.m_health, definition.Health);
        bool minToolTierMatches = !definition.HasMinToolTierOverride || destructible.m_minToolTier == definition.MinToolTier;
        if (healthMatches &&
            minToolTierMatches &&
            (signatureMatches || nview == null || zdo == null || !nview.IsOwner()))
        {
            return;
        }

        if (definition.HasHealthOverride)
        {
            destructible.m_health = definition.Health;
        }

        if (definition.HasMinToolTierOverride)
        {
            destructible.m_minToolTier = definition.MinToolTier;
        }

        if (nview == null || zdo == null || !nview.IsOwner() || signatureMatches)
        {
            return;
        }

        if (definition.HasHealthOverride)
        {
            zdo.Set(ZDOVars.s_health, GetScaledMineHealth(Mathf.Max(definition.Health, 0.01f)));
        }

        zdo.Set(DestructibleLazyScalarSignatureKey, signature);
    }

    private static IEnumerable<string> EnumerateOverrideConfigurationPaths()
    {
        return DomainConfigurationFileSupport.EnumerateOverrideConfigurationPaths(
            "object",
            PrimaryOverrideConfigurationPathYml,
            PrimaryOverrideConfigurationPathYaml);
    }

    private static bool IsOverrideConfigurationFileName(string fileName)
    {
        return DomainConfigurationFileSupport.IsOverrideConfigurationFileName("object", fileName);
    }

    private static void CaptureSnapshotsIfNeeded()
    {
        if (Snapshots.Count > 0)
        {
            return;
        }

        foreach (GameObject prefab in EnumerateRelevantPrefabs())
        {
            PrefabSnapshot? snapshot = CaptureSnapshot(prefab);
            if (snapshot == null)
            {
                continue;
            }

            Snapshots.Add(snapshot);
            if (!SnapshotsByPrefab.ContainsKey(snapshot.Prefab.name))
            {
                SnapshotsByPrefab.Add(snapshot.Prefab.name, snapshot);
            }
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Captured {Snapshots.Count} relevant prefab snapshot(s).");
    }

    private static void RefreshSnapshots()
    {
        Snapshots.Clear();
        SnapshotsByPrefab.Clear();
        CaptureSnapshotsIfNeeded();
    }

    private static IEnumerable<GameObject> EnumeratePrefabs()
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

    private static IEnumerable<GameObject> EnumerateRelevantPrefabs()
    {
        foreach (GameObject prefab in EnumeratePrefabs())
        {
            if (HasReferenceRelevantComponents(prefab))
            {
                yield return prefab;
            }
        }
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
        if (!IsGameDataReady() || ZNetScene.instance == null || ObjectDB.instance == null)
        {
            return 0;
        }

        unchecked
        {
            int hash = 17;
            hash = hash * 31 + ZNetScene.instance.GetInstanceID();
            hash = hash * 31 + ObjectDB.instance.GetInstanceID();
            hash = HashGameObjectCollection(hash, EnumerateRelevantPrefabs());
            hash = HashGameObjectCollection(hash, ObjectDB.instance.m_items);
            return hash;
        }
    }

    private static int ComputeSnapshotSignature()
    {
        int frame = Time.frameCount;
        if (_cachedSnapshotSignatureFrame == frame)
        {
            return _cachedSnapshotSignatureValue;
        }

        int signature = ComputeSnapshotSignatureCore();
        _cachedSnapshotSignatureFrame = frame;
        _cachedSnapshotSignatureValue = signature;
        return signature;
    }

    private static int ComputeSnapshotSignatureCore()
    {
        if (!IsGameDataReady() || ZNetScene.instance == null || ObjectDB.instance == null)
        {
            return 0;
        }

        unchecked
        {
            int hash = 17;
            hash = HashNamedGameObjectCollection(hash, EnumerateRelevantPrefabs());
            hash = HashNamedGameObjectCollection(hash, ObjectDB.instance.m_items);
            return hash;
        }
    }

    private static string ComputeReferenceSourceSignature()
    {
        return ReferenceRefreshSupport.ComputeStableHashForKeys(
            EnumerateRelevantPrefabs()
                .Select(prefab => prefab.name));
    }

    private static int HashGameObjectCollection(int hash, IEnumerable<GameObject> prefabs)
    {
        unchecked
        {
            foreach (GameObject prefab in prefabs)
            {
                hash = hash * 31 + (prefab != null ? prefab.GetInstanceID() : 0);
            }
        }

        return hash;
    }

    private static int HashNamedGameObjectCollection(int hash, IEnumerable<GameObject> prefabs)
    {
        unchecked
        {
            List<string> names = prefabs
                .Where(prefab => prefab != null)
                .Select(prefab => prefab.name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            hash = hash * 31 + names.Count;
            foreach (string name in names)
            {
                hash = hash * 31 + name.GetHashCode();
            }
        }

        return hash;
    }

    private static IEnumerable<PrefabReferenceEntry> BuildSupplementalLocationReferenceEntries(HashSet<string> existingPrefabs)
    {
        foreach ((string prefabName, LocationReferenceBucket bucket) in BuildLocationReferenceBuckets()
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            string normalizedPrefabName = ReferenceRefreshSupport.NormalizeKey(prefabName);
            if (normalizedPrefabName.Length == 0 ||
                existingPrefabs.Contains(normalizedPrefabName) ||
                bucket.ReferenceEntriesBySignature.Count != 1)
            {
                continue;
            }

            PrefabReferenceEntry entry = bucket.ReferenceEntriesBySignature.Values.First();
            existingPrefabs.Add(normalizedPrefabName);
            yield return entry;
        }
    }

    private static Dictionary<string, LocationReferenceBucket> BuildLocationReferenceBuckets()
    {
        Dictionary<string, LocationReferenceBucket> buckets = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string locationPrefab, GameObject rootPrefab) in EnumerateLocationRootPrefabs())
        {
            foreach (Transform transform in rootPrefab.GetComponentsInChildren<Transform>(true))
            {
                GameObject? gameObject = transform != null ? transform.gameObject : null;
                if (gameObject == null)
                {
                    continue;
                }

                PrefabReferenceEntry? referenceEntry = CaptureLocationReferenceEntry(gameObject);
                if (referenceEntry == null)
                {
                    continue;
                }

                if (!buckets.TryGetValue(referenceEntry.Prefab, out LocationReferenceBucket? bucket))
                {
                    bucket = new LocationReferenceBucket();
                    buckets[referenceEntry.Prefab] = bucket;
                }

                bucket.Locations.Add(locationPrefab);
                foreach (string component in GetLocationReferenceComponents(gameObject))
                {
                    bucket.Components.Add(component);
                }

                string signature = Serializer.Serialize(referenceEntry).TrimEnd('\r', '\n');
                bucket.ReferenceEntriesBySignature.TryAdd(signature, referenceEntry);
            }
        }

        return buckets;
    }

    private static IEnumerable<(string LocationPrefab, GameObject RootPrefab)> EnumerateLocationRootPrefabs()
    {
        if (ZoneSystem.instance == null)
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
        {
            if (!location.m_prefab.IsValid)
            {
                continue;
            }

            string locationPrefab = (location.m_prefab.Name ?? "").Trim();
            if (locationPrefab.Length == 0 || !seen.Add(locationPrefab))
            {
                continue;
            }

            location.m_prefab.Load();
            GameObject? rootPrefab = location.m_prefab.Asset;
            if (rootPrefab == null)
            {
                continue;
            }

            yield return (locationPrefab, rootPrefab);
        }
    }

    private static PrefabSnapshot? CaptureSnapshot(GameObject prefab)
    {
        DropOnDestroyed? dropOnDestroyed = prefab.GetComponent<DropOnDestroyed>();
        MineRock? mineRock = prefab.GetComponent<MineRock>();
        MineRock5? mineRock5 = prefab.GetComponent<MineRock5>();
        TreeBase? treeBase = prefab.GetComponent<TreeBase>();
        TreeLog? treeLog = prefab.GetComponent<TreeLog>();
        Container? container = prefab.GetComponent<Container>();
        Pickable? pickable = prefab.GetComponent<Pickable>();
        PickableItem? pickableItem = prefab.GetComponent<PickableItem>();
        Fish? fish = prefab.GetComponent<Fish>();
        Destructible? destructible = prefab.GetComponent<Destructible>();

        if (dropOnDestroyed == null &&
            mineRock == null &&
            mineRock5 == null &&
            treeBase == null &&
            treeLog == null &&
            container == null &&
            pickable == null &&
            pickableItem == null &&
            fish == null &&
            destructible == null)
        {
            return null;
        }

        return new PrefabSnapshot
        {
            Prefab = prefab,
            Health = CreateHealthSnapshot(destructible, mineRock, mineRock5, treeBase, treeLog),
            MinToolTier = CreateMinToolTierSnapshot(destructible, mineRock, mineRock5, treeBase, treeLog),
            DropOnDestroyed = dropOnDestroyed != null ? CloneDropTable(dropOnDestroyed.m_dropWhenDestroyed) : null,
            MineRock = mineRock != null ? CloneDropTable(mineRock.m_dropItems) : null,
            MineRock5 = mineRock5 != null ? CloneDropTable(mineRock5.m_dropItems) : null,
            TreeBase = treeBase != null ? CloneDropTable(treeBase.m_dropWhenDestroyed) : null,
            TreeLog = treeLog != null ? CloneDropTable(treeLog.m_dropWhenDestroyed) : null,
            Container = container != null ? CloneDropTable(container.m_defaultItems) : null,
            Pickable = pickable != null
                ? new PickableSnapshot
                {
                    ItemPrefab = pickable.m_itemPrefab,
                    Amount = pickable.m_amount,
                    MinAmountScaled = pickable.m_minAmountScaled,
                    DontScale = pickable.m_dontScale,
                    OverrideName = pickable.m_overrideName,
                    ExtraDrops = CloneDropTable(pickable.m_extraDrops)
                }
                : null,
            PickableItem = pickableItem != null ? CapturePickableItemSnapshot(pickableItem) : null,
            Fish = fish != null
                ? new FishSnapshot
                {
                    ExtraDrops = CloneDropTable(fish.m_extraDrops)
                }
                : null,
            Destructible = destructible != null
                ? new DestructibleSnapshot
                {
                    DestructibleType = destructible.m_destructibleType,
                    SpawnWhenDestroyed = destructible.m_spawnWhenDestroyed
                }
                : null
        };
    }

    private static PrefabReferenceEntry? CaptureLocationReferenceEntry(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return null;
        }

        PrefabSnapshot? snapshot = CaptureSnapshot(gameObject);
        if (snapshot == null)
        {
            return null;
        }

        string prefabName = GetPrefabName(gameObject);
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        snapshot.Prefab = gameObject;
        PrefabConfigurationEntry configurationEntry = BuildConfigurationEntry(snapshot);
        configurationEntry.Prefab = prefabName;

        return new PrefabReferenceEntry
        {
            Prefab = configurationEntry.Prefab,
            DropOnDestroyed = configurationEntry.DropOnDestroyed,
            MineRock = configurationEntry.MineRock,
            MineRock5 = configurationEntry.MineRock5,
            TreeBase = configurationEntry.TreeBase,
            TreeLog = configurationEntry.TreeLog,
            Container = configurationEntry.Container,
            Destructible = configurationEntry.Destructible,
            Pickable = configurationEntry.Pickable,
            PickableItem = configurationEntry.PickableItem,
            Fish = configurationEntry.Fish
        };
    }

    private static IEnumerable<string> GetLocationReferenceComponents(GameObject gameObject)
    {
        if (gameObject == null)
        {
            yield break;
        }

        if (gameObject.GetComponent<DropOnDestroyed>() != null)
        {
            yield return "dropOnDestroyed";
        }

        if (gameObject.GetComponent<MineRock>() != null)
        {
            yield return "mineRock";
        }

        if (gameObject.GetComponent<MineRock5>() != null)
        {
            yield return "mineRock5";
        }

        if (gameObject.GetComponent<TreeBase>() != null)
        {
            yield return "treeBase";
        }

        if (gameObject.GetComponent<TreeLog>() != null)
        {
            yield return "treeLog";
        }

        if (gameObject.GetComponent<Container>() != null)
        {
            yield return "container";
        }

        if (gameObject.GetComponent<Pickable>() != null)
        {
            yield return "pickable";
        }

        if (gameObject.GetComponent<PickableItem>() != null)
        {
            yield return "pickableItem";
        }

        if (gameObject.GetComponent<Fish>() != null)
        {
            yield return "fish";
        }

        if (gameObject.GetComponent<Destructible>() != null)
        {
            yield return "destructible";
        }
    }

    private static bool HasReferenceRelevantComponents(GameObject prefab)
    {
        return HasRelevantLiveObjectComponents(prefab);
    }

    private static HealthSnapshot? CreateHealthSnapshot(Destructible? destructible, MineRock? mineRock, MineRock5? mineRock5, TreeBase? treeBase, TreeLog? treeLog)
    {
        if (destructible == null &&
            mineRock == null &&
            mineRock5 == null &&
            treeBase == null &&
            treeLog == null)
        {
            return null;
        }

        return new HealthSnapshot
        {
            Destructible = destructible?.m_health,
            MineRock = mineRock?.m_health,
            MineRock5 = mineRock5?.m_health,
            TreeBase = treeBase?.m_health,
            TreeLog = treeLog?.m_health
        };
    }

    private static MinToolTierSnapshot? CreateMinToolTierSnapshot(Destructible? destructible, MineRock? mineRock, MineRock5? mineRock5, TreeBase? treeBase, TreeLog? treeLog)
    {
        if (destructible == null &&
            mineRock == null &&
            mineRock5 == null &&
            treeBase == null &&
            treeLog == null)
        {
            return null;
        }

        return new MinToolTierSnapshot
        {
            Destructible = destructible?.m_minToolTier,
            MineRock = mineRock?.m_minToolTier,
            MineRock5 = mineRock5?.m_minToolTier,
            TreeBase = treeBase?.m_minToolTier,
            TreeLog = treeLog?.m_minToolTier
        };
    }

    private static PickableItemSnapshot CapturePickableItemSnapshot(PickableItem pickableItem)
    {
        PickableItemSnapshot snapshot = new()
        {
            ItemPrefab = pickableItem.m_itemPrefab ? pickableItem.m_itemPrefab.gameObject : null,
            Stack = pickableItem.m_stack
        };

        foreach (PickableItem.RandomItem randomItem in pickableItem.m_randomItemPrefabs)
        {
            snapshot.RandomItems.Add(new PickableItemRandomSnapshot
            {
                ItemPrefab = randomItem.m_itemPrefab ? randomItem.m_itemPrefab.gameObject : null,
                StackMin = randomItem.m_stackMin,
                StackMax = randomItem.m_stackMax
            });
        }

        return snapshot;
    }

    private static void ApplyIfReady(bool queueLiveReconcile = false)
    {
        if (!IsGameDataReady() || Snapshots.Count == 0)
        {
            return;
        }

        bool synchronizedPayloadReady = Volatile.Read(ref _synchronizedPayloadReady);
        if (!StandardDomainApplySupport.CanApplySynchronizedDomain(synchronizedPayloadReady))
        {
            return;
        }

        int gameDataSignature = ComputeGameDataSignature();
        bool domainEnabled = PluginSettingsFacade.IsObjectDomainEnabled();
        Dictionary<string, string> currentEntrySignatures = BuildActiveEntrySignaturesByPrefab();
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

        RunApplyCoordinator(gameDataSignature, domainEnabled, currentEntrySignatures, queueLiveReconcile);
    }

    private static void EnsureRuntimeDropConfigurationState()
    {
        if (!IsGameDataReady())
        {
            return;
        }

        int gameDataSignature = ComputeGameDataSignature();
        if (_runtimeDropConfigurationGameDataSignature == gameDataSignature &&
            string.Equals(_runtimeDropConfigurationSignature, _configurationSignature, StringComparison.Ordinal))
        {
            return;
        }

        _runtimeDropConfigurationState = BuildRuntimeDropConfigurationState();
        _runtimeDropConfigurationGameDataSignature = gameDataSignature;
        _runtimeDropConfigurationSignature = _configurationSignature;
    }

    private static ObjectRuntimeDropConfigurationState BuildRuntimeDropConfigurationState()
    {
        ObjectRuntimeDropConfigurationState state = new();
        foreach ((string prefabName, List<PrefabConfigurationEntry> entries) in ActiveEntriesByPrefab)
        {
            CompiledObjectPrefabPlan plan = new();
            plan.ActiveEntries.AddRange(entries);
            foreach (PrefabConfigurationEntry entry in entries)
            {
                CompiledObjectDropRule? compiledRule = CompileObjectDropRule(entry);
                if (compiledRule != null)
                {
                    plan.Rules.Add(compiledRule);
                    state.RulesByEntry[entry] = compiledRule;
                }
            }

            if (plan.ActiveEntries.Count > 0)
            {
                BuildStaticDropTableTemplatesForPrefab(prefabName, plan);
                state.PlansByPrefab[prefabName] = plan;
            }
        }

        return state;
    }

    private static void BuildStaticDropTableTemplatesForPrefab(
        string prefabName,
        CompiledObjectPrefabPlan plan)
    {
        if (!SnapshotsByPrefab.TryGetValue(prefabName, out PrefabSnapshot? snapshot))
        {
            return;
        }

        Dictionary<LiveObjectComponentKind, StaticCompiledDropTableTemplate> templates = new();
        TryAddStaticDropTableTemplate(templates, plan.Rules, snapshot.DropOnDestroyed, LiveObjectComponentKind.DropOnDestroyed, rule => rule.DropOnDestroyed);
        TryAddStaticDropTableTemplate(templates, plan.Rules, snapshot.MineRock, LiveObjectComponentKind.MineRock, rule => rule.MineRock);
        TryAddStaticDropTableTemplate(templates, plan.Rules, snapshot.MineRock5, LiveObjectComponentKind.MineRock5, rule => rule.MineRock5);
        TryAddStaticDropTableTemplate(templates, plan.Rules, snapshot.TreeBase, LiveObjectComponentKind.TreeBase, rule => rule.TreeBase);
        TryAddStaticDropTableTemplate(templates, plan.Rules, snapshot.TreeLog, LiveObjectComponentKind.TreeLog, rule => rule.TreeLog);
        TryAddStaticDropTableTemplate(templates, plan.Rules, snapshot.Container, LiveObjectComponentKind.Container, rule => rule.Container);
        TryAddStaticDropTableTemplate(templates, plan.Rules, snapshot.Pickable?.ExtraDrops, LiveObjectComponentKind.Pickable, rule => rule.PickableExtraDrops);
        TryAddStaticDropTableTemplate(templates, plan.Rules, snapshot.Fish?.ExtraDrops, LiveObjectComponentKind.Fish, rule => rule.FishExtraDrops);
        if (templates.Count > 0)
        {
            foreach ((LiveObjectComponentKind componentKind, StaticCompiledDropTableTemplate template) in templates)
            {
                plan.StaticDropTableTemplates[componentKind] = template;
            }
        }
    }

    private static void TryAddStaticDropTableTemplate(
        Dictionary<LiveObjectComponentKind, StaticCompiledDropTableTemplate> templates,
        IEnumerable<CompiledObjectDropRule> compiledRules,
        DropTable? snapshotTable,
        LiveObjectComponentKind componentKind,
        Func<CompiledObjectDropRule, CompiledDropTablePayload?> payloadSelector)
    {
        List<CompiledDropTablePayload> staticPayloads = new();
        foreach (CompiledObjectDropRule compiledRule in compiledRules ?? Enumerable.Empty<CompiledObjectDropRule>())
        {
            CompiledDropTablePayload? payload = payloadSelector(compiledRule);
            if (payload == null || compiledRule.HasConditions)
            {
                continue;
            }

            staticPayloads.Add(payload);
        }

        if (staticPayloads.Count == 0)
        {
            return;
        }

        templates[componentKind] = BuildStaticDropTableTemplate(snapshotTable, staticPayloads);
    }

    private static StaticCompiledDropTableTemplate BuildStaticDropTableTemplate(
        DropTable? snapshotTable,
        IEnumerable<CompiledDropTablePayload> payloads)
    {
        StaticCompiledDropTableTemplate template = new()
        {
            Template = snapshotTable != null ? CloneDropTable(snapshotTable) : CreateDefaultDropTable()
        };

        template.Template.m_drops = new List<DropTable.DropData>();
        foreach (CompiledDropTablePayload payload in payloads ?? Enumerable.Empty<CompiledDropTablePayload>())
        {
            ApplyDropTableScalarOverrides(template.Template, payload);
            AppendDropTableRows(template.Template.m_drops, payload.Drops, template.Fingerprints);
        }

        return template;
    }

    private static CompiledObjectDropRule? CompileObjectDropRule(PrefabConfigurationEntry entry)
    {
        CompiledObjectDropRule compiledRule = new()
        {
            Entry = entry,
            HasConditions = DropConditionEvaluator.HasConditions(entry.Conditions),
            DropOnDestroyed = CompileDropTablePayload(entry, entry.DropOnDestroyed, "DropOnDestroyed"),
            MineRock = CompileDropTablePayload(entry, entry.MineRock, "MineRock"),
            MineRock5 = CompileDropTablePayload(entry, entry.MineRock5, "MineRock5"),
            TreeBase = CompileDropTablePayload(entry, entry.TreeBase, "TreeBase"),
            TreeLog = CompileDropTablePayload(entry, entry.TreeLog, "TreeLog"),
            Container = CompileDropTablePayload(entry, entry.Container, "Container"),
            PickableExtraDrops = CompileDropTablePayload(entry, entry.Pickable?.ExtraDrops, "Pickable/ExtraDrops"),
            FishExtraDrops = CompileDropTablePayload(entry, entry.Fish?.ExtraDrops, "Fish/ExtraDrops"),
            Pickable = CompilePickableDefinition(entry),
            PickableItem = CompilePickableItemDefinition(entry),
            Destructible = CompileDestructibleComponentDefinition(entry),
            MineRockScalars = CompileDamageableScalarDefinition(entry, entry.MineRock, "MineRock"),
            MineRock5Scalars = CompileDamageableScalarDefinition(entry, entry.MineRock5, "MineRock5"),
            TreeBaseScalars = CompileDamageableScalarDefinition(entry, entry.TreeBase, "TreeBase"),
            TreeLogScalars = CompileDamageableScalarDefinition(entry, entry.TreeLog, "TreeLog")
        };

        return compiledRule.DropOnDestroyed != null ||
               compiledRule.MineRock != null ||
               compiledRule.MineRock5 != null ||
               compiledRule.TreeBase != null ||
               compiledRule.TreeLog != null ||
               compiledRule.Container != null ||
               compiledRule.PickableExtraDrops != null ||
               compiledRule.FishExtraDrops != null ||
               compiledRule.Pickable != null ||
               compiledRule.PickableItem != null ||
               compiledRule.Destructible != null
            ? compiledRule
            : null;
    }

    private static CompiledDamageableScalarDefinition? CompileDamageableScalarDefinition(
        PrefabConfigurationEntry entry,
        DamageableDropTableDefinition? definition,
        string componentName)
    {
        if (!HasDamageableOverride(definition))
        {
            return null;
        }

        string context = BuildCompiledObjectDropContext(entry, componentName);
        CompiledDamageableScalarDefinition compiledDefinition = new();
        if (HasDamageableHealthOverride(definition) &&
            TryGetConfiguredHealth(definition!.Health!.Value, $"{context}/Health", out float health))
        {
            compiledDefinition.HasHealthOverride = true;
            compiledDefinition.Health = health;
        }

        if (HasDamageableMinToolTierOverride(definition) &&
            TryGetConfiguredMinToolTier(definition!.MinToolTier!.Value, $"{context}/minToolTier", out int minToolTier))
        {
            compiledDefinition.HasMinToolTierOverride = true;
            compiledDefinition.MinToolTier = minToolTier;
        }

        return compiledDefinition.HasHealthOverride || compiledDefinition.HasMinToolTierOverride
            ? compiledDefinition
            : null;
    }

    private static CompiledDestructibleComponentDefinition? CompileDestructibleComponentDefinition(PrefabConfigurationEntry entry)
    {
        if (!HasDestructibleOverride(entry.Destructible))
        {
            return null;
        }

        DestructibleDefinition definition = entry.Destructible!;
        string context = BuildCompiledObjectDropContext(entry, "Destructible");
        CompiledDestructibleComponentDefinition compiledDefinition = new();
        if (HasDestructibleHealthOverride(definition) &&
            TryGetConfiguredHealth(definition.Health!.Value, $"{context}/Health", out float health))
        {
            compiledDefinition.HasHealthOverride = true;
            compiledDefinition.Health = health;
        }

        if (HasDestructibleMinToolTierOverride(definition) &&
            TryGetConfiguredMinToolTier(definition.MinToolTier!.Value, $"{context}/minToolTier", out int minToolTier))
        {
            compiledDefinition.HasMinToolTierOverride = true;
            compiledDefinition.MinToolTier = minToolTier;
        }

        if (!string.IsNullOrWhiteSpace(definition.DestructibleType))
        {
            if (Enum.TryParse(definition.DestructibleType, true, out DestructibleType destructibleType))
            {
                compiledDefinition.HasDestructibleTypeOverride = true;
                compiledDefinition.DestructibleType = destructibleType;
            }
            else
            {
                WarnInvalidEntry($"Entry '{context}' has invalid destructibleType '{definition.DestructibleType}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(definition.SpawnWhenDestroyed))
        {
            GameObject? spawnWhenDestroyed = ResolveSpawnPrefab(definition.SpawnWhenDestroyed!, context);
            if (spawnWhenDestroyed != null)
            {
                compiledDefinition.HasSpawnWhenDestroyedOverride = true;
                compiledDefinition.SpawnWhenDestroyed = spawnWhenDestroyed;
            }
        }

        return compiledDefinition.HasHealthOverride ||
               compiledDefinition.HasMinToolTierOverride ||
               compiledDefinition.HasDestructibleTypeOverride ||
               compiledDefinition.HasSpawnWhenDestroyedOverride
            ? compiledDefinition
            : null;
    }

    private static CompiledPickableDefinition? CompilePickableDefinition(PrefabConfigurationEntry entry)
    {
        if (!HasPickableOverride(entry.Pickable))
        {
            return null;
        }

        PickableDefinition definition = entry.Pickable!;
        CompiledPickableDefinition compiledDefinition = new
        (
        );
        if (HasPickableDropOverride(definition.Drop))
        {
            if (!string.IsNullOrWhiteSpace(definition.Drop!.Item))
            {
                GameObject? prefab = ResolveItemPrefab(definition.Drop.Item, BuildCompiledObjectDropContext(entry, "Pickable"));
                if (prefab != null)
                {
                    compiledDefinition.HasItemPrefabOverride = true;
                    compiledDefinition.ItemPrefab = prefab;
                }
            }

            if (definition.Drop.Amount.HasValue)
            {
                compiledDefinition.HasAmountOverride = true;
                compiledDefinition.Amount = Math.Max(1, definition.Drop.Amount.Value);
            }

            if (definition.Drop.MinAmountScaled.HasValue)
            {
                compiledDefinition.HasMinAmountScaledOverride = true;
                compiledDefinition.MinAmountScaled = Math.Max(1, definition.Drop.MinAmountScaled.Value);
            }

            if (definition.Drop.DontScale.HasValue)
            {
                compiledDefinition.HasDontScaleOverride = true;
                compiledDefinition.DontScale = definition.Drop.DontScale.Value;
            }
        }

        if (definition.OverrideName != null)
        {
            compiledDefinition.HasOverrideNameOverride = true;
            compiledDefinition.OverrideName = definition.OverrideName;
        }

        return compiledDefinition;
    }

    private static CompiledPickableItemDefinition? CompilePickableItemDefinition(PrefabConfigurationEntry entry)
    {
        if (!HasPickableItemOverride(entry.PickableItem))
        {
            return null;
        }

        PickableItemDefinition definition = entry.PickableItem!;
        CompiledPickableItemDefinition compiledDefinition = new();
        bool hasRandomOverride = definition.RandomDrops != null;
        List<RandomPickableItemDefinition> randomDrops = definition.RandomDrops ?? new List<RandomPickableItemDefinition>();
        bool hasRandomItems = randomDrops.Count > 0;
        bool hasFixedDrop = HasPickableItemDropOverride(definition.Drop);
        string context = BuildCompiledObjectDropContext(entry, "PickableItem");
        if (hasRandomItems && hasFixedDrop)
        {
            WarnInvalidEntry($"Entry '{context}' defines both drop and randomDrops. randomDrops take precedence.");
        }

        compiledDefinition.HasRandomOverride = hasRandomOverride;
        if (hasRandomItems)
        {
            BuiltRandomPickableItems builtRandomItems = BuildRandomItems(randomDrops, context);
            compiledDefinition.RandomItems = builtRandomItems.Items;
            compiledDefinition.RandomWeights = builtRandomItems.Weights;
        }

        compiledDefinition.HasFixedDrop = hasFixedDrop;
        if (hasFixedDrop)
        {
            if (!string.IsNullOrWhiteSpace(definition.Drop?.Item))
            {
                GameObject? prefab = ResolveItemPrefab(definition.Drop!.Item, context);
                if (prefab != null)
                {
                    compiledDefinition.HasFixedItemOverride = true;
                    compiledDefinition.FixedItemPrefab = prefab.GetComponent<ItemDrop>();
                }
            }

            if (definition.Drop?.Stack.HasValue == true)
            {
                compiledDefinition.HasFixedStackOverride = true;
                compiledDefinition.FixedStack = Math.Max(1, definition.Drop.Stack.Value);
            }
        }

        return compiledDefinition;
    }

    private static CompiledDropTablePayload? CompileDropTablePayload(
        PrefabConfigurationEntry entry,
        DropTablePayloadDefinition? definition,
        string componentName)
    {
        if (!HasDropTableOverride(definition))
        {
            return null;
        }

        CompiledDropTablePayload payload = new();
        if (definition!.DropMin.HasValue || definition.DropMax.HasValue)
        {
            payload.HasDropRangeOverride = true;
            payload.DropMin = Math.Max(0, definition.DropMin ?? 1);
            payload.DropMax = Math.Max(payload.DropMin, definition.DropMax ?? definition.DropMin ?? 1);
        }

        if (definition.DropChance.HasValue)
        {
            payload.HasDropChanceOverride = true;
            payload.DropChance = Mathf.Clamp01(definition.DropChance.Value);
        }

        if (definition.OneOfEach.HasValue)
        {
            payload.HasOneOfEachOverride = true;
            payload.OneOfEach = definition.OneOfEach.Value;
        }

        string context = BuildCompiledObjectDropContext(entry, componentName);
        foreach (DropEntryDefinition drop in definition.Drops ?? Enumerable.Empty<DropEntryDefinition>())
        {
            if (TryCompileDropTableRow(drop, context, out CompiledDropTableRow? compiledRow))
            {
                payload.Drops.Add(compiledRow);
            }
        }

        return payload;
    }

    private static bool TryCompileDropTableRow(
        DropEntryDefinition drop,
        string context,
        out CompiledDropTableRow compiledRow)
    {
        compiledRow = null!;
        string itemName = (drop.Item ?? "").Trim();
        if (itemName.Length == 0)
        {
            WarnInvalidEntry($"Entry '{context}' contains a drop without an item name.");
            return false;
        }

        GameObject? itemPrefab = ResolveItemPrefab(itemName, context);
        if (itemPrefab == null)
        {
            return false;
        }

        compiledRow = new CompiledDropTableRow
        {
            Fingerprint = BuildDropRowFingerprint(drop),
            ItemPrefab = itemPrefab,
            StackMin = Math.Max(1, drop.StackMin ?? 1),
            StackMax = Math.Max(Math.Max(1, drop.StackMin ?? 1), drop.StackMax ?? drop.StackMin ?? 1),
            Weight = Mathf.Max(0f, drop.Weight ?? 1f),
            DontScale = drop.DontScale ?? false
        };
        return true;
    }

    private static void ValidateConfiguredPrefabs()
    {
        foreach ((string prefabName, List<PrefabConfigurationEntry> entries) in ActiveEntriesByPrefab)
        {
            if (SnapshotsByPrefab.ContainsKey(prefabName))
            {
                continue;
            }

            foreach (PrefabConfigurationEntry entry in entries)
            {
                WarnInvalidEntry($"Object prefab '{prefabName}' from {DescribeEntrySource(entry)} was not found in ZNetScene.");
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
        if (domainEnabled)
        {
            PrefabProfileCatalogState.RecordCurrentConfiguredKindsAsLastApplied();
            PrefabProfileCatalogState.RecordCurrentReconcileKindsAsLastApplied();
        }
        else
        {
            PrefabProfileCatalogState.ClearLastAppliedConfiguredKinds();
            PrefabProfileCatalogState.ClearLastAppliedReconcileKinds();
        }
    }

    private static void RestoreSnapshots(HashSet<string>? targetPrefabs = null)
    {
        if (targetPrefabs == null)
        {
            foreach (PrefabSnapshot snapshot in Snapshots)
            {
                RestoreConfiguredComponents(snapshot.Prefab, snapshot, CreateRestoreMask(snapshot), updateRuntimeState: false);
            }

            return;
        }

        foreach (string prefabName in targetPrefabs)
        {
            if (!SnapshotsByPrefab.TryGetValue(prefabName, out PrefabSnapshot? snapshot))
            {
                continue;
            }

            RestoreConfiguredComponents(snapshot.Prefab, snapshot, CreateRestoreMask(snapshot), updateRuntimeState: false);
        }
    }

    private static void RestoreTrackedLiveObjects(HashSet<string> prefabs)
    {
        if (prefabs.Count == 0 || !IsGameDataReady())
        {
            return;
        }

        foreach (GameObject liveObject in GetRegisteredLiveObjects(prefabs))
        {
            if (liveObject == null)
            {
                continue;
            }

            string prefabName = GetPrefabName(liveObject);
            if (!SnapshotsByPrefab.TryGetValue(prefabName, out PrefabSnapshot? snapshot))
            {
                continue;
            }

            RestoreConfiguredComponents(liveObject, snapshot, CreateRestoreMask(snapshot), updateRuntimeState: true);
        }
    }

    private static PrefabConfigurationEntry CreateRestoreMask(PrefabSnapshot snapshot)
    {
        return new PrefabConfigurationEntry
        {
            Prefab = snapshot.Prefab.name,
            Destructible = CreateRestoreDestructibleMask(snapshot),
            DropOnDestroyed = snapshot.DropOnDestroyed != null ? CreateRestoreDropTableMask() : null,
            MineRock = CreateRestoreDamageableDropTableMask(snapshot.MineRock, snapshot.Health?.MineRock, snapshot.MinToolTier?.MineRock),
            MineRock5 = CreateRestoreDamageableDropTableMask(snapshot.MineRock5, snapshot.Health?.MineRock5, snapshot.MinToolTier?.MineRock5),
            TreeBase = CreateRestoreDamageableDropTableMask(snapshot.TreeBase, snapshot.Health?.TreeBase, snapshot.MinToolTier?.TreeBase),
            TreeLog = CreateRestoreDamageableDropTableMask(snapshot.TreeLog, snapshot.Health?.TreeLog, snapshot.MinToolTier?.TreeLog),
            Container = snapshot.Container != null ? CreateRestoreDropTableMask() : null,
            Pickable = snapshot.Pickable != null ? new PickableDefinition { OverrideName = "" } : null,
            PickableItem = snapshot.PickableItem != null ? new PickableItemDefinition { RandomDrops = new List<RandomPickableItemDefinition>() } : null,
            Fish = snapshot.Fish != null ? new FishDefinition { ExtraDrops = CreateRestoreDropTableMask() } : null
        };
    }

    private static DropTableDefinition CreateRestoreDropTableMask()
    {
        return new DropTableDefinition
        {
            Drops = new List<DropEntryDefinition>()
        };
    }

    private static DamageableDropTableDefinition? CreateRestoreDamageableDropTableMask(DropTable? dropTable, float? health, int? minToolTier)
    {
        if (dropTable == null && !health.HasValue && !minToolTier.HasValue)
        {
            return null;
        }

        return new DamageableDropTableDefinition
        {
            Drops = dropTable != null ? new List<DropEntryDefinition>() : null,
            Health = health.HasValue ? 0f : null,
            MinToolTier = minToolTier.HasValue ? 0 : null
        };
    }

    private static DestructibleDefinition? CreateRestoreDestructibleMask(PrefabSnapshot snapshot)
    {
        bool hasHealth = snapshot.Health?.Destructible.HasValue == true;
        bool hasMinToolTier = snapshot.MinToolTier?.Destructible.HasValue == true;
        bool hasState = snapshot.Destructible != null;

        if (!hasHealth && !hasMinToolTier && !hasState)
        {
            return null;
        }

        return new DestructibleDefinition
        {
            Health = hasHealth ? 0f : null,
            MinToolTier = hasMinToolTier ? 0 : null,
            DestructibleType = hasState ? snapshot.Destructible!.DestructibleType.ToString() : null,
            SpawnWhenDestroyed = hasState ? "restore" : null
        };
    }

    private static Dictionary<string, string> BuildActiveEntrySignaturesByPrefab()
    {
        return DomainEntrySignatureSupport.BuildSignaturesByKey(
            ActiveEntriesByPrefab,
            NetworkPayloadSyncSupport.ComputeObjectConfigurationSignature);
    }

    private static HashSet<string> BuildDirtyPrefabs(Dictionary<string, string> previous, Dictionary<string, string> current)
    {
        return DomainDictionaryDiffSupport.BuildDirtyKeys(previous, current);
    }

    private static void ReplaceEntrySignatures(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        DomainDictionaryDiffSupport.ReplaceEntries(target, source);
    }

    private static void ApplyEffectiveDropTableOverrides(
        GameObject gameObject,
        PrefabSnapshot snapshot,
        IEnumerable<PrefabConfigurationEntry> entries,
        bool allowConditionalMatches,
        GroupConditionalApplyPlan? groupPlan = null,
        bool includeEventOnlyKinds = true)
    {
        List<CompiledObjectDropRule>? compiledRules;
        EnsureRuntimeDropConfigurationState();
        if (!_runtimeDropConfigurationState.PlansByPrefab.TryGetValue(snapshot.Prefab.name, out CompiledObjectPrefabPlan? prefabPlan) ||
            (compiledRules = prefabPlan.Rules) == null ||
            compiledRules.Count == 0)
        {
            return;
        }

        if ((includeEventOnlyKinds || (UsesLiveDropTableReconcile(LiveObjectComponentKind.DropOnDestroyed) &&
                                       RequiresLiveReconcileForPrefab(snapshot.Prefab.name, LiveObjectComponentKind.DropOnDestroyed))) &&
            gameObject.TryGetComponent(out DropOnDestroyed dropOnDestroyed) &&
            TryBuildEffectiveDropTable(
                gameObject,
                snapshot.Prefab.name,
                compiledRules,
                rule => rule.DropOnDestroyed,
                snapshot.DropOnDestroyed,
                LiveObjectComponentKind.DropOnDestroyed,
                allowConditionalMatches,
                groupPlan,
                out DropTable? dropOnDestroyedTable))
        {
            dropOnDestroyed.m_dropWhenDestroyed = dropOnDestroyedTable!;
        }

        if ((includeEventOnlyKinds || (UsesLiveDropTableReconcile(LiveObjectComponentKind.MineRock) &&
                                       RequiresLiveReconcileForPrefab(snapshot.Prefab.name, LiveObjectComponentKind.MineRock))) &&
            gameObject.TryGetComponent(out MineRock mineRock) &&
            TryBuildEffectiveDropTable(
                gameObject,
                snapshot.Prefab.name,
                compiledRules,
                rule => rule.MineRock,
                snapshot.MineRock,
                LiveObjectComponentKind.MineRock,
                allowConditionalMatches,
                groupPlan,
                out DropTable? mineRockTable))
        {
            mineRock.m_dropItems = mineRockTable!;
        }

        if ((includeEventOnlyKinds || (UsesLiveDropTableReconcile(LiveObjectComponentKind.MineRock5) &&
                                       RequiresLiveReconcileForPrefab(snapshot.Prefab.name, LiveObjectComponentKind.MineRock5))) &&
            gameObject.TryGetComponent(out MineRock5 mineRock5) &&
            TryBuildEffectiveDropTable(
                gameObject,
                snapshot.Prefab.name,
                compiledRules,
                rule => rule.MineRock5,
                snapshot.MineRock5,
                LiveObjectComponentKind.MineRock5,
                allowConditionalMatches,
                groupPlan,
                out DropTable? mineRock5Table))
        {
            mineRock5.m_dropItems = mineRock5Table!;
        }

        if ((includeEventOnlyKinds || (UsesLiveDropTableReconcile(LiveObjectComponentKind.Container) &&
                                       RequiresLiveReconcileForPrefab(snapshot.Prefab.name, LiveObjectComponentKind.Container))) &&
            gameObject.TryGetComponent(out Container container) &&
            TryBuildEffectiveDropTable(
                gameObject,
                snapshot.Prefab.name,
                compiledRules,
                rule => rule.Container,
                snapshot.Container,
                LiveObjectComponentKind.Container,
                allowConditionalMatches,
                groupPlan,
                out DropTable? containerTable))
        {
            container.m_defaultItems = containerTable!;
        }

        if ((includeEventOnlyKinds || (UsesLiveDropTableReconcile(LiveObjectComponentKind.TreeBase) &&
                                       RequiresLiveReconcileForPrefab(snapshot.Prefab.name, LiveObjectComponentKind.TreeBase))) &&
            gameObject.TryGetComponent(out TreeBase treeBase) &&
            TryBuildEffectiveDropTable(
                gameObject,
                snapshot.Prefab.name,
                compiledRules,
                rule => rule.TreeBase,
                snapshot.TreeBase,
                LiveObjectComponentKind.TreeBase,
                allowConditionalMatches,
                groupPlan,
                out DropTable? treeBaseTable))
        {
            treeBase.m_dropWhenDestroyed = treeBaseTable!;
        }

        if ((includeEventOnlyKinds || (UsesLiveDropTableReconcile(LiveObjectComponentKind.TreeLog) &&
                                       RequiresLiveReconcileForPrefab(snapshot.Prefab.name, LiveObjectComponentKind.TreeLog))) &&
            gameObject.TryGetComponent(out TreeLog treeLog) &&
            TryBuildEffectiveDropTable(
                gameObject,
                snapshot.Prefab.name,
                compiledRules,
                rule => rule.TreeLog,
                snapshot.TreeLog,
                LiveObjectComponentKind.TreeLog,
                allowConditionalMatches,
                groupPlan,
                out DropTable? treeLogTable))
        {
            treeLog.m_dropWhenDestroyed = treeLogTable!;
        }

        if ((includeEventOnlyKinds || RequiresLiveReconcileForPrefab(snapshot.Prefab.name, LiveObjectComponentKind.Pickable)) &&
            gameObject.TryGetComponent(out Pickable pickable) &&
            TryBuildEffectiveDropTable(
                gameObject,
                snapshot.Prefab.name,
                compiledRules,
                rule => rule.PickableExtraDrops,
                snapshot.Pickable?.ExtraDrops,
                LiveObjectComponentKind.Pickable,
                allowConditionalMatches,
                groupPlan,
                out DropTable? pickableExtraDrops))
        {
            pickable.m_extraDrops = pickableExtraDrops!;
        }

        if ((includeEventOnlyKinds || RequiresLiveReconcileForPrefab(snapshot.Prefab.name, LiveObjectComponentKind.Fish)) &&
            gameObject.TryGetComponent(out Fish fish) &&
            TryBuildEffectiveDropTable(
                gameObject,
                snapshot.Prefab.name,
                compiledRules,
                rule => rule.FishExtraDrops,
                snapshot.Fish?.ExtraDrops,
                LiveObjectComponentKind.Fish,
                allowConditionalMatches,
                groupPlan,
                out DropTable? fishExtraDrops))
        {
            fish.m_extraDrops = fishExtraDrops!;
        }
    }

    private static bool TryBuildEffectiveDropTable(
        GameObject gameObject,
        string prefabName,
        IEnumerable<CompiledObjectDropRule> rules,
        Func<CompiledObjectDropRule, CompiledDropTablePayload?> payloadSelector,
        DropTable? snapshotTable,
        LiveObjectComponentKind componentKind,
        bool allowConditionalMatches,
        GroupConditionalApplyPlan? groupPlan,
        out DropTable? effectiveTable)
    {
        effectiveTable = null;
        StaticCompiledDropTableTemplate? staticTemplate = null;
        if (prefabName.Length > 0)
        {
            TryGetStaticDropTableTemplate(prefabName, componentKind, out staticTemplate);
        }

        List<CompiledDropTablePayload> matchingPayloads = new();
        bool hasCustomBlock = false;
        if (groupPlan != null)
        {
            foreach (CompiledObjectDropRule matchedRule in groupPlan.MatchingCompiledRules)
            {
                CompiledDropTablePayload? matchedPayload = payloadSelector(matchedRule);
                if (matchedPayload == null)
                {
                    continue;
                }

                hasCustomBlock = true;
                if (staticTemplate != null && !matchedRule.HasConditions)
                {
                    continue;
                }

                matchingPayloads.Add(matchedPayload);
            }
        }

        foreach (CompiledObjectDropRule rule in rules ?? Enumerable.Empty<CompiledObjectDropRule>())
        {
            CompiledDropTablePayload? payload = payloadSelector(rule);
            if (payload == null)
            {
                continue;
            }

            hasCustomBlock = true;
            if (groupPlan?.EligibleCompiledRules.Contains(rule) == true)
            {
                continue;
            }

            if (staticTemplate != null && !rule.HasConditions)
            {
                continue;
            }

            if (!EntryMatches(gameObject, rule.Entry, allowConditionalMatches))
            {
                continue;
            }

            matchingPayloads.Add(payload);
        }

        if (!hasCustomBlock)
        {
            return false;
        }

        if (staticTemplate != null)
        {
            effectiveTable = BuildEffectiveDropTable(staticTemplate, matchingPayloads);
            return true;
        }

        effectiveTable = BuildEffectiveDropTable(snapshotTable, matchingPayloads);
        return true;
    }

    private static bool EntryMatches(GameObject gameObject, PrefabConfigurationEntry entry, bool allowConditionalMatches)
    {
        if (!DropConditionEvaluator.HasConditions(entry.Conditions))
        {
            return true;
        }

        return allowConditionalMatches && DropConditionEvaluator.AreSatisfied(gameObject, entry.Conditions);
    }

    private static bool ShouldApplyToInstance(GameObject gameObject)
    {
        Piece? piece = gameObject.GetComponent<Piece>();
        if (piece == null)
        {
            return true;
        }

        return piece.GetCreator() == 0L;
    }

    private static void ClearContainerContentsIfNeeded(GameObject gameObject, PrefabConfigurationEntry entry)
    {
        if (!ContainerNeedsLiveMutation(entry) ||
            !HasDropTableOverride(entry.Container) ||
            !gameObject.TryGetComponent(out Container container))
        {
            return;
        }

        if (!EntryMatches(gameObject, entry, allowConditionalMatches: true))
        {
            return;
        }

        if (!container.IsOwner())
        {
            return;
        }

        Inventory inventory = container.GetInventory();
        if (inventory.NrOfItems() == 0)
        {
            return;
        }

        inventory.RemoveAll();
    }

    private static void RestoreConfiguredComponents(GameObject gameObject, PrefabSnapshot snapshot, PrefabConfigurationEntry entry, bool updateRuntimeState)
    {
        RestoreResourceComponents(gameObject, snapshot, entry, updateRuntimeState);
        RestoreInteractiveComponents(gameObject, snapshot, entry, updateRuntimeState);
    }

    private static void ApplyConfiguredComponents(GameObject gameObject, PrefabSnapshot snapshot, PrefabConfigurationEntry entry, bool updateRuntimeState, bool allowConditionalMatches)
    {
        if (!EntryMatches(gameObject, entry, allowConditionalMatches))
        {
            return;
        }

        string contextRoot = $"{snapshot.Prefab.name}@{gameObject.name}";
        TryGetCompiledObjectDropRule(entry, out CompiledObjectDropRule? compiledRule);
        ApplyResourceComponents(gameObject, entry, compiledRule, contextRoot, updateRuntimeState);
        ApplyInteractiveComponents(gameObject, entry, compiledRule, contextRoot, updateRuntimeState);
    }

    private static bool TryGetConfiguredHealth(float configuredHealth, string context, out float normalizedHealth)
    {
        if (float.IsNaN(configuredHealth) || float.IsInfinity(configuredHealth) || configuredHealth <= 0f)
        {
            WarnInvalidEntry($"Entry '{context}' has invalid health '{configuredHealth}'. Health must be greater than 0.");
            normalizedHealth = 0f;
            return false;
        }

        normalizedHealth = configuredHealth;
        return true;
    }

    private static bool TryGetConfiguredMinToolTier(int configuredMinToolTier, string context, out int normalizedMinToolTier)
    {
        if (configuredMinToolTier < 0)
        {
            WarnInvalidEntry($"Entry '{context}' has invalid minToolTier '{configuredMinToolTier}'. minToolTier must be 0 or greater.");
            normalizedMinToolTier = 0;
            return false;
        }

        normalizedMinToolTier = configuredMinToolTier;
        return true;
    }

    private static void ApplyDestructibleHealth(Destructible destructible, float configuredBaseHealth, bool updateRuntimeState)
    {
        float oldBaseHealth = destructible.m_health;
        if (Mathf.Approximately(oldBaseHealth, configuredBaseHealth))
        {
            return;
        }

        destructible.m_health = configuredBaseHealth;
        if (updateRuntimeState)
        {
            AdjustSharedHealthZdo(destructible.gameObject, GetScaledMineHealth(oldBaseHealth), GetScaledMineHealth(configuredBaseHealth));
        }
    }

    private static void ApplyDestructibleMinToolTier(Destructible destructible, int configuredMinToolTier)
    {
        if (destructible.m_minToolTier == configuredMinToolTier)
        {
            return;
        }

        destructible.m_minToolTier = configuredMinToolTier;
    }

    private static void ApplyDestructibleType(Destructible destructible, DestructibleType configuredType)
    {
        if (destructible.m_destructibleType == configuredType)
        {
            return;
        }

        destructible.m_destructibleType = configuredType;
    }

    private static void ApplyMineRockHealth(MineRock mineRock, float configuredBaseHealth, bool updateRuntimeState)
    {
        float oldBaseHealth = mineRock.m_health;
        mineRock.m_health = configuredBaseHealth;
        if (updateRuntimeState)
        {
            AdjustMineRockAreaHealth(mineRock, GetScaledMineHealth(oldBaseHealth), GetScaledMineHealth(configuredBaseHealth));
        }
    }

    private static void ApplyMineRock5Health(MineRock5 mineRock5, float configuredBaseHealth, bool updateRuntimeState)
    {
        float oldBaseHealth = mineRock5.m_health;
        mineRock5.m_health = configuredBaseHealth;
        if (updateRuntimeState)
        {
            AdjustMineRock5AreaHealth(mineRock5, GetScaledMineHealth(oldBaseHealth), GetScaledMineHealth(configuredBaseHealth));
        }
    }

    private static void ApplyTreeBaseHealth(TreeBase treeBase, float configuredBaseHealth, bool updateRuntimeState)
    {
        float oldBaseHealth = treeBase.m_health;
        treeBase.m_health = configuredBaseHealth;
        if (updateRuntimeState)
        {
            AdjustSharedHealthZdo(treeBase.gameObject, Mathf.Max(oldBaseHealth, 0.01f), Mathf.Max(configuredBaseHealth, 0.01f));
        }
    }

    private static void ApplyTreeLogHealth(TreeLog treeLog, float configuredBaseHealth, bool updateRuntimeState)
    {
        float oldBaseHealth = treeLog.m_health;
        treeLog.m_health = configuredBaseHealth;
        if (updateRuntimeState)
        {
            AdjustSharedHealthZdo(treeLog.gameObject, GetScaledMineHealth(oldBaseHealth), GetScaledMineHealth(configuredBaseHealth));
        }
    }

    private static float GetScaledMineHealth(float baseHealth)
    {
        if (Game.instance == null)
        {
            return baseHealth;
        }

        return baseHealth + Game.m_worldLevel * Game.instance.m_worldLevelMineHPMultiplier * baseHealth;
    }

    private static void AdjustSharedHealthZdo(GameObject gameObject, float oldMaxHealth, float newMaxHealth)
    {
        ZNetView? nview = gameObject.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (nview == null || zdo == null || !nview.IsOwner())
        {
            return;
        }

        float currentHealth = zdo.GetFloat(ZDOVars.s_health, oldMaxHealth);
        float adjustedHealth = RemapHealth(currentHealth, oldMaxHealth, newMaxHealth);
        zdo.Set(ZDOVars.s_health, adjustedHealth);
    }

    private static void AdjustMineRockAreaHealth(MineRock mineRock, float oldMaxHealth, float newMaxHealth)
    {
        ZNetView? nview = mineRock.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (nview == null || zdo == null || !nview.IsOwner())
        {
            return;
        }

        int hitAreaCount = GetMineRockAreaCount(mineRock);
        for (int i = 0; i < hitAreaCount; i++)
        {
            string key = $"Health{i}";
            float currentHealth = zdo.GetFloat(key, oldMaxHealth);
            zdo.Set(key, RemapHealth(currentHealth, oldMaxHealth, newMaxHealth));
        }
    }

    private static void SetMineRockAreaHealthAbsolute(MineRock mineRock, float configuredAreaHealth)
    {
        ZNetView? nview = mineRock.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (nview == null || zdo == null || !nview.IsOwner())
        {
            return;
        }

        int hitAreaCount = GetMineRockAreaCount(mineRock);
        for (int i = 0; i < hitAreaCount; i++)
        {
            zdo.Set($"Health{i}", configuredAreaHealth);
        }
    }

    private static int GetMineRockAreaCount(MineRock mineRock)
    {
        Collider[] hitAreas = mineRock.m_areaRoot != null
            ? mineRock.m_areaRoot.GetComponentsInChildren<Collider>()
            : mineRock.gameObject.GetComponentsInChildren<Collider>();

        return hitAreas.Length;
    }

    private static void AdjustMineRock5AreaHealth(MineRock5 mineRock5, float oldMaxHealth, float newMaxHealth)
    {
        if (MineRock5HitAreasField == null || MineRock5HitAreaHealthField == null)
        {
            return;
        }

        ZNetView? nview = mineRock5.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (nview == null || zdo == null || !nview.IsOwner())
        {
            return;
        }

        if (MineRock5HitAreasField.GetValue(mineRock5) is not IList hitAreas)
        {
            return;
        }

        foreach (object? hitArea in hitAreas)
        {
            if (hitArea == null)
            {
                continue;
            }

            float currentHealth = MineRock5HitAreaHealthField.GetValue(hitArea) is float value ? value : oldMaxHealth;
            MineRock5HitAreaHealthField.SetValue(hitArea, RemapHealth(currentHealth, oldMaxHealth, newMaxHealth));
        }

        MineRock5SaveHealthMethod?.Invoke(mineRock5, null);
        MineRock5UpdateMeshMethod?.Invoke(mineRock5, null);
    }

    private static void SetMineRock5AreaHealthAbsolute(MineRock5 mineRock5, float configuredAreaHealth)
    {
        if (MineRock5HitAreasField == null || MineRock5HitAreaHealthField == null)
        {
            return;
        }

        ZNetView? nview = mineRock5.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (nview == null || zdo == null || !nview.IsOwner())
        {
            return;
        }

        if (MineRock5HitAreasField.GetValue(mineRock5) is not IList hitAreas)
        {
            return;
        }

        foreach (object? hitArea in hitAreas)
        {
            if (hitArea == null)
            {
                continue;
            }

            MineRock5HitAreaHealthField.SetValue(hitArea, configuredAreaHealth);
        }

        MineRock5SaveHealthMethod?.Invoke(mineRock5, null);
        MineRock5UpdateMeshMethod?.Invoke(mineRock5, null);
    }

    private static float RemapHealth(float currentHealth, float oldMaxHealth, float newMaxHealth)
    {
        if (newMaxHealth <= 0f || currentHealth <= 0f)
        {
            return 0f;
        }

        if (oldMaxHealth <= 0f)
        {
            return newMaxHealth;
        }

        return Mathf.Clamp01(currentHealth / oldMaxHealth) * newMaxHealth;
    }

    private static void ApplyPickableDefinition(Pickable pickable, PickableDefinition definition, string context, bool updateRuntimeState)
    {
        if (HasPickableDropOverride(definition.Drop))
        {
            if (!string.IsNullOrWhiteSpace(definition.Drop!.Item))
            {
                GameObject? prefab = ResolveItemPrefab(definition.Drop.Item, context);
                if (prefab != null && pickable.m_itemPrefab != prefab)
                {
                    pickable.m_itemPrefab = prefab;
                }
            }

            if (definition.Drop.Amount.HasValue && pickable.m_amount != Math.Max(1, definition.Drop.Amount.Value))
            {
                pickable.m_amount = Math.Max(1, definition.Drop.Amount.Value);
            }

            if (definition.Drop.MinAmountScaled.HasValue && pickable.m_minAmountScaled != Math.Max(1, definition.Drop.MinAmountScaled.Value))
            {
                pickable.m_minAmountScaled = Math.Max(1, definition.Drop.MinAmountScaled.Value);
            }

            if (definition.Drop.DontScale.HasValue && pickable.m_dontScale != definition.Drop.DontScale.Value)
            {
                pickable.m_dontScale = definition.Drop.DontScale.Value;
            }
        }

        if (definition.OverrideName != null && pickable.m_overrideName != definition.OverrideName)
        {
            pickable.m_overrideName = definition.OverrideName;
        }
    }

    private static void ApplyPickableDefinition(Pickable pickable, CompiledPickableDefinition definition, bool updateRuntimeState)
    {
        if (definition.HasItemPrefabOverride && pickable.m_itemPrefab != definition.ItemPrefab)
        {
            pickable.m_itemPrefab = definition.ItemPrefab;
        }

        if (definition.HasAmountOverride && pickable.m_amount != definition.Amount)
        {
            pickable.m_amount = definition.Amount;
        }

        if (definition.HasMinAmountScaledOverride && pickable.m_minAmountScaled != definition.MinAmountScaled)
        {
            pickable.m_minAmountScaled = definition.MinAmountScaled;
        }

        if (definition.HasDontScaleOverride && pickable.m_dontScale != definition.DontScale)
        {
            pickable.m_dontScale = definition.DontScale;
        }

        if (definition.HasOverrideNameOverride && pickable.m_overrideName != definition.OverrideName)
        {
            pickable.m_overrideName = definition.OverrideName;
        }
    }

    private static void ApplyPickableItemDefinition(PickableItem pickableItem, PickableItemDefinition definition, string context, bool updateRuntimeState)
    {
        bool configurationChanged = false;
        bool forceRandomRefresh = false;
        bool hasRandomOverride = definition.RandomDrops != null;
        definition.RandomDrops ??= new List<RandomPickableItemDefinition>();
        bool hasRandomItems = definition.RandomDrops.Count > 0;
        bool hasFixedDrop = HasPickableItemDropOverride(definition.Drop);
        if (hasRandomItems && hasFixedDrop)
        {
            WarnInvalidEntry($"Entry '{context}' defines both drop and randomDrops. randomDrops take precedence.");
        }

        if (hasRandomItems)
        {
            BuiltRandomPickableItems builtRandomItems = BuildRandomItems(definition.RandomDrops, context);
            if (!PickableItemRandomItemsEqual(pickableItem.m_randomItemPrefabs, builtRandomItems.Items))
            {
                pickableItem.m_randomItemPrefabs = builtRandomItems.Items;
                configurationChanged = true;
                forceRandomRefresh = true;
            }

            if (!PickableItemRandomWeightsEqual(pickableItem, builtRandomItems.Weights))
            {
                SetPickableItemRandomWeights(pickableItem, builtRandomItems.Weights);
                configurationChanged = true;
                forceRandomRefresh = true;
            }

            if (pickableItem.m_itemPrefab != null &&
                !builtRandomItems.Items.Any(randomItem => randomItem.m_itemPrefab == pickableItem.m_itemPrefab))
            {
                pickableItem.m_itemPrefab = null;
                configurationChanged = true;
                forceRandomRefresh = true;
            }

            if (builtRandomItems.Items.Length == 0)
            {
                if (pickableItem.m_itemPrefab != null)
                {
                    pickableItem.m_itemPrefab = null;
                    configurationChanged = true;
                }

                if (pickableItem.m_stack != 1)
                {
                    pickableItem.m_stack = 1;
                    configurationChanged = true;
                }
            }
        }
        else
        {
            if (LazyRuntimeState.HasRandomState(pickableItem) &&
                !PickableItemRandomWeightsEqual(pickableItem, Array.Empty<float>()))
            {
                ClearPickableItemRandomWeights(pickableItem);
                configurationChanged = true;
            }

            if (hasRandomOverride)
            {
                if (pickableItem.m_randomItemPrefabs.Length != 0)
                {
                    pickableItem.m_randomItemPrefabs = Array.Empty<PickableItem.RandomItem>();
                    configurationChanged = true;
                }
            }

            if (hasFixedDrop)
            {
                if (!string.IsNullOrWhiteSpace(definition.Drop?.Item))
                {
                    GameObject? prefab = ResolveItemPrefab(definition.Drop!.Item, context);
                    if (prefab != null)
                    {
                        ItemDrop? fixedItemPrefab = prefab.GetComponent<ItemDrop>();
                        if (pickableItem.m_itemPrefab != fixedItemPrefab)
                        {
                            pickableItem.m_itemPrefab = fixedItemPrefab;
                            configurationChanged = true;
                        }
                    }
                }

                if (definition.Drop?.Stack.HasValue == true && pickableItem.m_stack != Math.Max(1, definition.Drop.Stack.Value))
                {
                    pickableItem.m_stack = Math.Max(1, definition.Drop.Stack.Value);
                    configurationChanged = true;
                }
            }
        }

        if (updateRuntimeState && configurationChanged)
        {
            bool useRandomItems = hasRandomItems && pickableItem.m_randomItemPrefabs.Length > 0;
            UpdatePickableItemRuntimeState(pickableItem, useRandomItems, forceRandomRefresh);
        }
    }

    private static void ApplyPickableItemDefinition(PickableItem pickableItem, CompiledPickableItemDefinition definition, bool updateRuntimeState)
    {
        bool configurationChanged = false;
        bool forceRandomRefresh = false;
        bool hasRandomItems = definition.RandomItems.Length > 0;
        if (hasRandomItems)
        {
            if (!PickableItemRandomItemsEqual(pickableItem.m_randomItemPrefabs, definition.RandomItems))
            {
                PickableItem.RandomItem[] randomItems = new PickableItem.RandomItem[definition.RandomItems.Length];
                Array.Copy(definition.RandomItems, randomItems, definition.RandomItems.Length);
                pickableItem.m_randomItemPrefabs = randomItems;
                configurationChanged = true;
                forceRandomRefresh = true;
            }

            if (!PickableItemRandomWeightsEqual(pickableItem, definition.RandomWeights))
            {
                SetPickableItemRandomWeights(pickableItem, definition.RandomWeights);
                configurationChanged = true;
                forceRandomRefresh = true;
            }

            if (pickableItem.m_itemPrefab != null &&
                !definition.RandomItems.Any(randomItem => randomItem.m_itemPrefab == pickableItem.m_itemPrefab))
            {
                pickableItem.m_itemPrefab = null;
                configurationChanged = true;
                forceRandomRefresh = true;
            }

            if (definition.RandomItems.Length == 0)
            {
                if (pickableItem.m_itemPrefab != null)
                {
                    pickableItem.m_itemPrefab = null;
                    configurationChanged = true;
                }

                if (pickableItem.m_stack != 1)
                {
                    pickableItem.m_stack = 1;
                    configurationChanged = true;
                }
            }
        }
        else
        {
            if (LazyRuntimeState.HasRandomState(pickableItem) &&
                !PickableItemRandomWeightsEqual(pickableItem, Array.Empty<float>()))
            {
                ClearPickableItemRandomWeights(pickableItem);
                configurationChanged = true;
            }

            if (definition.HasRandomOverride)
            {
                if (pickableItem.m_randomItemPrefabs.Length != 0)
                {
                    pickableItem.m_randomItemPrefabs = Array.Empty<PickableItem.RandomItem>();
                    configurationChanged = true;
                }
            }

            if (definition.HasFixedDrop)
            {
                if (definition.HasFixedItemOverride && pickableItem.m_itemPrefab != definition.FixedItemPrefab)
                {
                    pickableItem.m_itemPrefab = definition.FixedItemPrefab;
                    configurationChanged = true;
                }

                if (definition.HasFixedStackOverride && pickableItem.m_stack != definition.FixedStack)
                {
                    pickableItem.m_stack = definition.FixedStack;
                    configurationChanged = true;
                }
            }
        }

        if (updateRuntimeState && configurationChanged)
        {
            UpdatePickableItemRuntimeState(pickableItem, hasRandomItems && pickableItem.m_randomItemPrefabs.Length > 0, forceRandomRefresh);
        }
    }

    private static void ApplyDestructibleDefinition(Destructible destructible, DestructibleDefinition definition, string context, bool includeSpawnWhenDestroyed)
    {
        if (!string.IsNullOrWhiteSpace(definition.DestructibleType))
        {
            if (Enum.TryParse(definition.DestructibleType, true, out DestructibleType destructibleType))
            {
                ApplyDestructibleType(destructible, destructibleType);
            }
            else
            {
                WarnInvalidEntry($"Entry '{context}' has invalid destructibleType '{definition.DestructibleType}'.");
            }
        }

        if (includeSpawnWhenDestroyed && !string.IsNullOrWhiteSpace(definition.SpawnWhenDestroyed))
        {
            destructible.m_spawnWhenDestroyed = ResolveSpawnPrefab(definition.SpawnWhenDestroyed!, context);
        }
    }

    private static GameObject? ResolveSpawnPrefab(string prefabName, string context)
    {
        string trimmedName = (prefabName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            return null;
        }

        GameObject? prefab = ZNetScene.instance?.GetPrefab(trimmedName) ?? ObjectDB.instance?.GetItemPrefab(trimmedName);
        if (prefab == null)
        {
            WarnInvalidEntry($"Entry '{context}' references unknown spawn prefab '{trimmedName}'.");
        }

        return prefab;
    }

    private static DropTable BuildDropTable(DropTablePayloadDefinition definition, string context)
    {
        DropTable dropTable = CreateDefaultDropTable();
        ApplyDropTableScalarOverrides(dropTable, definition, resetToDefaultsWhenUnset: true);
        AppendDropTableRows(dropTable.m_drops, definition.Drops, context);
        return dropTable;
    }

    private static DropTable BuildEffectiveDropTable(DropTable? snapshotTable, IEnumerable<DropTablePayloadDefinition> matchingPayloads, string context)
    {
        List<DropTablePayloadDefinition> payloads = (matchingPayloads ?? Enumerable.Empty<DropTablePayloadDefinition>())
            .Where(HasDropTableOverride)
            .ToList();

        if (payloads.Count == 0)
        {
            return snapshotTable != null ? CloneDropTable(snapshotTable) : CreateDefaultDropTable();
        }

        DropTable dropTable = snapshotTable != null ? CloneDropTable(snapshotTable) : CreateDefaultDropTable();
        dropTable.m_drops = new List<DropTable.DropData>();
        HashSet<string> seenFingerprints = new(StringComparer.Ordinal);

        foreach (DropTablePayloadDefinition payload in payloads)
        {
            ApplyDropTableScalarOverrides(dropTable, payload, resetToDefaultsWhenUnset: false);
            AppendDropTableRows(dropTable.m_drops, payload.Drops, context, seenFingerprints);
        }

        return dropTable;
    }

    private static DropTable BuildEffectiveDropTable(DropTable? snapshotTable, IEnumerable<CompiledDropTablePayload> matchingPayloads)
    {
        List<CompiledDropTablePayload> payloads = (matchingPayloads ?? Enumerable.Empty<CompiledDropTablePayload>()).ToList();
        if (payloads.Count == 0)
        {
            return snapshotTable != null ? CloneDropTable(snapshotTable) : CreateDefaultDropTable();
        }

        DropTable dropTable = snapshotTable != null ? CloneDropTable(snapshotTable) : CreateDefaultDropTable();
        dropTable.m_drops = new List<DropTable.DropData>();
        HashSet<string> seenFingerprints = new(StringComparer.Ordinal);

        foreach (CompiledDropTablePayload payload in payloads)
        {
            ApplyDropTableScalarOverrides(dropTable, payload);
            AppendDropTableRows(dropTable.m_drops, payload.Drops, seenFingerprints);
        }

        return dropTable;
    }

    private static DropTable BuildEffectiveDropTable(
        StaticCompiledDropTableTemplate template,
        IEnumerable<CompiledDropTablePayload> matchingPayloads)
    {
        List<CompiledDropTablePayload> payloads = (matchingPayloads ?? Enumerable.Empty<CompiledDropTablePayload>()).ToList();
        if (payloads.Count == 0)
        {
            return CloneDropTable(template.Template);
        }

        DropTable dropTable = CloneDropTable(template.Template);
        HashSet<string> seenFingerprints = new(template.Fingerprints, StringComparer.Ordinal);
        foreach (CompiledDropTablePayload payload in payloads)
        {
            ApplyDropTableScalarOverrides(dropTable, payload);
            AppendDropTableRows(dropTable.m_drops, payload.Drops, seenFingerprints);
        }

        return dropTable;
    }

    private static DropTable CreateDefaultDropTable()
    {
        return new DropTable
        {
            m_dropMin = 1,
            m_dropMax = 1,
            m_dropChance = 1f,
            m_oneOfEach = false,
            m_drops = new List<DropTable.DropData>()
        };
    }

    private static void ApplyDropTableScalarOverrides(DropTable dropTable, DropTablePayloadDefinition definition, bool resetToDefaultsWhenUnset)
    {
        if (definition.DropMin.HasValue || definition.DropMax.HasValue)
        {
            int dropMin = Math.Max(0, definition.DropMin ?? 1);
            int dropMax = Math.Max(dropMin, definition.DropMax ?? definition.DropMin ?? 1);
            dropTable.m_dropMin = dropMin;
            dropTable.m_dropMax = dropMax;
        }
        else if (resetToDefaultsWhenUnset)
        {
            dropTable.m_dropMin = 1;
            dropTable.m_dropMax = 1;
        }

        if (definition.DropChance.HasValue)
        {
            dropTable.m_dropChance = Mathf.Clamp01(definition.DropChance.Value);
        }
        else if (resetToDefaultsWhenUnset)
        {
            dropTable.m_dropChance = 1f;
        }

        if (definition.OneOfEach.HasValue)
        {
            dropTable.m_oneOfEach = definition.OneOfEach.Value;
        }
        else if (resetToDefaultsWhenUnset)
        {
            dropTable.m_oneOfEach = false;
        }
    }

    private static void ApplyDropTableScalarOverrides(DropTable dropTable, CompiledDropTablePayload definition)
    {
        if (definition.HasDropRangeOverride)
        {
            dropTable.m_dropMin = definition.DropMin;
            dropTable.m_dropMax = definition.DropMax;
        }

        if (definition.HasDropChanceOverride)
        {
            dropTable.m_dropChance = definition.DropChance;
        }

        if (definition.HasOneOfEachOverride)
        {
            dropTable.m_oneOfEach = definition.OneOfEach;
        }
    }

    private static void AppendDropTableRows(List<DropTable.DropData> target, IEnumerable<DropEntryDefinition>? definitions, string context, HashSet<string>? seenFingerprints = null)
    {
        foreach (DropEntryDefinition drop in definitions ?? Enumerable.Empty<DropEntryDefinition>())
        {
            string itemName = (drop.Item ?? "").Trim();
            if (itemName.Length == 0)
            {
                WarnInvalidEntry($"Entry '{context}' contains a drop without an item name.");
                continue;
            }

            GameObject? itemPrefab = ResolveItemPrefab(itemName, context);
            if (itemPrefab == null)
            {
                continue;
            }

            string fingerprint = BuildDropRowFingerprint(drop);
            if (seenFingerprints != null && !seenFingerprints.Add(fingerprint))
            {
                continue;
            }

            target.Add(new DropTable.DropData
            {
                m_item = itemPrefab,
                m_stackMin = Math.Max(1, drop.StackMin ?? 1),
                m_stackMax = Math.Max(Math.Max(1, drop.StackMin ?? 1), drop.StackMax ?? drop.StackMin ?? 1),
                m_weight = Mathf.Max(0f, drop.Weight ?? 1f),
                m_dontScale = drop.DontScale ?? false
            });
        }
    }

    private static void AppendDropTableRows(List<DropTable.DropData> target, IEnumerable<CompiledDropTableRow> definitions, HashSet<string>? seenFingerprints = null)
    {
        foreach (CompiledDropTableRow drop in definitions ?? Enumerable.Empty<CompiledDropTableRow>())
        {
            if (seenFingerprints != null && !seenFingerprints.Add(drop.Fingerprint))
            {
                continue;
            }

            target.Add(new DropTable.DropData
            {
                m_item = drop.ItemPrefab,
                m_stackMin = drop.StackMin,
                m_stackMax = drop.StackMax,
                m_weight = drop.Weight,
                m_dontScale = drop.DontScale
            });
        }
    }

    private static string BuildDropRowFingerprint(DropEntryDefinition definition)
    {
        DropEntryDefinition normalized = new()
        {
            Item = (definition.Item ?? "").Trim(),
            StackMin = Math.Max(1, definition.StackMin ?? 1),
            StackMax = Math.Max(Math.Max(1, definition.StackMin ?? 1), definition.StackMax ?? definition.StackMin ?? 1),
            Weight = Mathf.Max(0f, definition.Weight ?? 1f),
            DontScale = definition.DontScale ?? false
        };

        return NetworkPayloadSyncSupport.ComputeObjectDropRowSignature(normalized);
    }

    private static GameObject? ResolveItemPrefab(string itemName, string context)
    {
        string trimmedName = (itemName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            WarnInvalidEntry($"Entry '{context}' references an empty item prefab name.");
            return null;
        }

        GameObject? prefab = ObjectDB.instance?.GetItemPrefab(trimmedName) ?? ZNetScene.instance?.GetPrefab(trimmedName);
        if (prefab == null)
        {
            WarnInvalidEntry($"Entry '{context}' references unknown item prefab '{trimmedName}'.");
            return null;
        }

        if (!prefab.TryGetComponent(out ItemDrop _))
        {
            WarnInvalidEntry($"Entry '{context}' references '{trimmedName}', but it is not an item prefab.");
            return null;
        }

        return prefab;
    }

    private static DropTable CloneDropTable(DropTable source)
    {
        DropTable clone = new()
        {
            m_dropMin = source.m_dropMin,
            m_dropMax = source.m_dropMax,
            m_dropChance = source.m_dropChance,
            m_oneOfEach = source.m_oneOfEach,
            m_drops = new List<DropTable.DropData>(source.m_drops.Count)
        };

        foreach (DropTable.DropData drop in source.m_drops)
        {
            clone.m_drops.Add(new DropTable.DropData
            {
                m_item = drop.m_item,
                m_stackMin = drop.m_stackMin,
                m_stackMax = drop.m_stackMax,
                m_weight = drop.m_weight,
                m_dontScale = drop.m_dontScale
            });
        }

        return clone;
    }
    private static string FormatYamlBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatYamlFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
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

    private static PrefabConfigurationEntry BuildConfigurationEntry(PrefabSnapshot snapshot)
    {
        return new PrefabConfigurationEntry
        {
            Prefab = snapshot.Prefab.name,
            Enabled = true,
            Destructible = ConvertDestructible(snapshot),
            DropOnDestroyed = snapshot.DropOnDestroyed != null ? ConvertDropTable(snapshot.DropOnDestroyed) : null,
            MineRock = snapshot.MineRock != null ? ConvertDamageableDropTable(snapshot.MineRock, snapshot.Health?.MineRock, snapshot.MinToolTier?.MineRock) : null,
            MineRock5 = snapshot.MineRock5 != null ? ConvertDamageableDropTable(snapshot.MineRock5, snapshot.Health?.MineRock5, snapshot.MinToolTier?.MineRock5) : null,
            TreeBase = snapshot.TreeBase != null ? ConvertDamageableDropTable(snapshot.TreeBase, snapshot.Health?.TreeBase, snapshot.MinToolTier?.TreeBase) : null,
            TreeLog = snapshot.TreeLog != null ? ConvertDamageableDropTable(snapshot.TreeLog, snapshot.Health?.TreeLog, snapshot.MinToolTier?.TreeLog) : null,
            Container = snapshot.Container != null ? ConvertDropTable(snapshot.Container) : null,
            Pickable = snapshot.Pickable != null ? ConvertPickable(snapshot.Pickable) : null,
            PickableItem = snapshot.PickableItem != null ? ConvertPickableItem(snapshot.PickableItem) : null,
            Fish = snapshot.Fish != null ? ConvertFish(snapshot.Fish) : null
        };
    }

    private static int CompareObjectEntriesForOutput(PrefabConfigurationEntry? left, PrefabConfigurationEntry? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left == null)
        {
            return 1;
        }

        if (right == null)
        {
            return -1;
        }

        int primaryComparison = GetPrimaryObjectComponentRank(left).CompareTo(GetPrimaryObjectComponentRank(right));
        if (primaryComparison != 0)
        {
            return primaryComparison;
        }

        int signatureComparison = GetObjectComponentSignatureMask(left).CompareTo(GetObjectComponentSignatureMask(right));
        if (signatureComparison != 0)
        {
            return signatureComparison;
        }

        return string.Compare(left.Prefab, right.Prefab, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPrimaryObjectComponentRank(PrefabConfigurationEntry entry)
    {
        if (entry.Container != null)
        {
            return 0;
        }

        if (entry.Pickable != null)
        {
            return 1;
        }

        if (entry.PickableItem != null)
        {
            return 2;
        }

        if (entry.Fish != null)
        {
            return 3;
        }

        if (entry.MineRock != null)
        {
            return 4;
        }

        if (entry.MineRock5 != null)
        {
            return 5;
        }

        if (entry.TreeBase != null)
        {
            return 6;
        }

        if (entry.TreeLog != null)
        {
            return 7;
        }

        if (entry.DropOnDestroyed != null)
        {
            return 8;
        }

        if (entry.Destructible != null)
        {
            return 9;
        }

        return 10;
    }

    private static int GetObjectComponentSignatureMask(PrefabConfigurationEntry entry)
    {
        int mask = 0;
        if (entry.Container != null)
        {
            mask |= 1 << 0;
        }

        if (entry.Pickable != null)
        {
            mask |= 1 << 1;
        }

        if (entry.PickableItem != null)
        {
            mask |= 1 << 2;
        }

        if (entry.Fish != null)
        {
            mask |= 1 << 3;
        }

        if (entry.MineRock != null)
        {
            mask |= 1 << 4;
        }

        if (entry.MineRock5 != null)
        {
            mask |= 1 << 5;
        }

        if (entry.TreeBase != null)
        {
            mask |= 1 << 6;
        }

        if (entry.TreeLog != null)
        {
            mask |= 1 << 7;
        }

        if (entry.DropOnDestroyed != null)
        {
            mask |= 1 << 8;
        }

        if (entry.Destructible != null)
        {
            mask |= 1 << 9;
        }

        return mask;
    }

    private static DestructibleDefinition? ConvertDestructible(PrefabSnapshot snapshot)
    {
        bool hasHealth = snapshot.Health?.Destructible.HasValue == true;
        bool hasMinToolTier = snapshot.MinToolTier?.Destructible.HasValue == true;
        if (!hasHealth && !hasMinToolTier && snapshot.Destructible == null)
        {
            return null;
        }

        return new DestructibleDefinition
        {
            Health = hasHealth ? snapshot.Health!.Destructible : null,
            MinToolTier = hasMinToolTier ? snapshot.MinToolTier!.Destructible : null,
            DestructibleType = snapshot.Destructible != null && snapshot.Destructible.DestructibleType != DestructibleType.Default
                ? snapshot.Destructible.DestructibleType.ToString()
                : null,
            SpawnWhenDestroyed = NormalizeReferencePrefabName(snapshot.Destructible?.SpawnWhenDestroyed)
        };
    }

    private static DropTableDefinition ConvertDropTable(DropTable dropTable)
    {
        int dropMin = Math.Max(0, dropTable.m_dropMin);
        int dropMax = Math.Max(dropMin, dropTable.m_dropMax);
        List<DropEntryDefinition> drops = dropTable.m_drops
            .Select(drop => new { Name = NormalizeReferencePrefabName(drop.m_item), Drop = drop })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new DropEntryDefinition
            {
                Item = entry.Name!,
                Stack = RangeFormatting.FromReference(entry.Drop.m_stackMin, entry.Drop.m_stackMax, 1, 1),
                Weight = IsReferenceDefault(entry.Drop.m_weight, 1f) ? null : entry.Drop.m_weight,
                DontScale = entry.Drop.m_dontScale ? true : null
            })
            .ToList();

        return new DropTableDefinition
        {
            Rolls = RangeFormatting.FromReference(dropMin, dropMax, 1, 1),
            DropChance = IsReferenceDefault(dropTable.m_dropChance, 1f) ? null : dropTable.m_dropChance,
            OneOfEach = dropTable.m_oneOfEach ? true : null,
            Drops = drops.Count > 0 ? drops : null
        };
    }

    private static DamageableDropTableDefinition ConvertDamageableDropTable(DropTable dropTable, float? health, int? minToolTier)
    {
        DropTableDefinition dropTableDefinition = ConvertDropTable(dropTable);

        return new DamageableDropTableDefinition
        {
            Health = health,
            MinToolTier = minToolTier,
            Rolls = dropTableDefinition.Rolls,
            DropChance = dropTableDefinition.DropChance,
            OneOfEach = dropTableDefinition.OneOfEach,
            Drops = dropTableDefinition.Drops
        };
    }

    private static PickableDefinition ConvertPickable(PickableSnapshot snapshot)
    {
        DropTableDefinition extraDrops = ConvertDropTable(snapshot.ExtraDrops);
        string? itemName = NormalizeReferencePrefabName(snapshot.ItemPrefab);
        PickableDropDefinition? drop = null;
        if (!string.IsNullOrWhiteSpace(itemName) || snapshot.Amount != 1 || snapshot.MinAmountScaled != 0 || snapshot.DontScale)
        {
            drop = new PickableDropDefinition
            {
                Item = itemName ?? "",
                Amount = snapshot.Amount == 1 ? null : snapshot.Amount,
                MinAmountScaled = snapshot.MinAmountScaled == 0 ? null : snapshot.MinAmountScaled,
                DontScale = snapshot.DontScale ? true : null
            };
        }

        return new PickableDefinition
        {
            OverrideName = string.IsNullOrWhiteSpace(snapshot.OverrideName) ? null : snapshot.OverrideName,
            Drop = drop,
            ExtraDrops = HasReferenceDropTableContent(extraDrops) ? extraDrops : null
        };
    }

    private static PickableItemDefinition ConvertPickableItem(PickableItemSnapshot snapshot)
    {
        List<RandomPickableItemDefinition> randomDrops = snapshot.RandomItems
            .Select(item => new { Name = NormalizeReferencePrefabName(item.ItemPrefab), Item = item })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new RandomPickableItemDefinition
            {
                Item = entry.Name!,
                Stack = RangeFormatting.FromReference(entry.Item.StackMin, entry.Item.StackMax, 1, 1)
            })
            .ToList();

        string? fixedItemName = NormalizeReferencePrefabName(snapshot.ItemPrefab);
        return new PickableItemDefinition
        {
            RandomDrops = randomDrops.Count > 0 ? randomDrops : null,
            Drop = randomDrops.Count == 0 && (!string.IsNullOrWhiteSpace(fixedItemName) || snapshot.Stack != 1)
                ? new PickableItemDropDefinition
                {
                    Item = fixedItemName ?? "",
                    Stack = snapshot.Stack == 1 ? null : snapshot.Stack
                }
                : null
        };
    }

    private static FishDefinition ConvertFish(FishSnapshot snapshot)
    {
        DropTableDefinition extraDrops = ConvertDropTable(snapshot.ExtraDrops);
        return new FishDefinition
        {
            ExtraDrops = HasReferenceDropTableContent(extraDrops) ? extraDrops : null
        };
    }

    private static bool HasReferenceDropTableContent(DropTablePayloadDefinition? definition)
    {
        return definition != null &&
               (definition.Rolls?.HasValues() == true ||
                definition.DropMin.HasValue ||
                definition.DropMax.HasValue ||
                definition.DropChance.HasValue ||
                definition.OneOfEach.HasValue ||
                (definition.Drops != null && definition.Drops.Count > 0));
    }

    private static IntRangeDefinition? GetRollsRange(DropTablePayloadDefinition definition)
    {
        return definition.Rolls ?? RangeFormatting.From(definition.DropMin, definition.DropMax ?? definition.DropMin);
    }

    private static IntRangeDefinition? GetStackRange(DropEntryDefinition definition)
    {
        return definition.Stack ?? RangeFormatting.From(definition.StackMin, definition.StackMax ?? definition.StackMin);
    }

    private static IntRangeDefinition? GetStackRange(RandomPickableItemDefinition definition)
    {
        return definition.Stack ?? RangeFormatting.From(definition.StackMin, definition.StackMax ?? definition.StackMin);
    }

    private static bool IsReferenceDefault(float value, float defaultValue)
    {
        return Math.Abs(value - defaultValue) < 0.0001f;
    }

    private static string? NormalizeReferencePrefabName(GameObject? prefab)
    {
        return prefab == null ? null : NormalizeReferencePrefabName(prefab.name);
    }

    private static string? NormalizeReferencePrefabName(string? prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return null;
        }

        string resolvedPrefabName = prefabName!;

        if (!resolvedPrefabName.StartsWith(MockPrefabPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return resolvedPrefabName;
        }

        string normalizedName = resolvedPrefabName.Substring(MockPrefabPrefix.Length);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        if (ZNetScene.instance?.GetPrefab(normalizedName) != null || ObjectDB.instance?.GetItemPrefab(normalizedName) != null)
        {
            return normalizedName;
        }

        return null;
    }

    private static string GetPrefabName(GameObject gameObject)
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

}
