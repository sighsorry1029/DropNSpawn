using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Text;
using BepInEx;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private const string ReferenceAutoUpdateStateKey = "location";
    private static readonly ConditionalWeakTable<RuneStone, RunestonePinChanceState> RunestonePinChanceRolls = new();
    private static readonly object RunestonePinChanceLock = new();
    private static readonly System.Random RunestonePinChanceRandom = new();
    internal static readonly DomainModuleDefinition<LocationConfigurationEntry> Module =
        new(
            "location",
            DropNSpawnPlugin.ReloadDomain.Location,
            "location_yaml",
            97,
            ShouldReloadForPath,
            ReloadConfiguration,
            Initialize,
            OnGameDataReady,
            HandleExpandWorldDataReady,
            dtoVersion: 12,
            transportProfile: DomainTransportProfile.SmallConfig,
            displayName: "location",
            cacheDirectoryName: "location",
            clientRequestPriority: 20,
            keySelector: entry => entry.RuleId,
            applyPayloadAction: ApplySyncedPayload,
            workKinds: DomainWorkKinds.Runtime | DomainWorkKinds.Reconcile,
            hasPendingReconcileWork: HasPendingReconcileWork,
            processPendingReconcileStep: ProcessQueuedReconcileStep,
            beforeClientManifestChanged: MarkSyncedPayloadPending,
            onClientAuthorityCutover: EnterPendingSyncedPayloadState);
    internal static DomainDescriptor<LocationConfigurationEntry> Descriptor => Module.DescriptorTyped;
    internal static DomainTransportMetadata<LocationConfigurationEntry> TransportMetadata => Module.TransportMetadataTyped;
    private static readonly int OfferingBowlLastUseTicksKey = $"{DropNSpawnPlugin.ModName}.offering_bowl_last_use_ticks".GetStableHashCode();
    private const int LocationProxyAliasForceSendDebounceFrames = 2;
    private const int LocationProxyAliasForceSendBudgetPerFrame = 8;
    private const int LocationProxyUnresolvedObservationMaxStableCount = 90;
    private const int LocationProxyResolvedObservationMaxStableCount = 24;

    private sealed class OfferingBowlSnapshot
    {
        public string Name { get; set; } = "";
        public string UseItemText { get; set; } = "";
        public string UsedAltarText { get; set; } = "";
        public string CantOfferText { get; set; } = "";
        public string WrongOfferText { get; set; } = "";
        public string IncompleteOfferText { get; set; } = "";
        public string BossItem { get; set; } = "";
        public int BossItems { get; set; }
        public string BossPrefab { get; set; } = "";
        public string ItemPrefab { get; set; } = "";
        public string SetGlobalKey { get; set; } = "";
        public bool RenderSpawnAreaGizmos { get; set; }
        public bool AlertOnSpawn { get; set; }
        public float SpawnBossDelay { get; set; }
        public float SpawnBossMaxDistance { get; set; }
        public float SpawnBossMinDistance { get; set; }
        public float SpawnBossMaxYDistance { get; set; }
        public int GetSolidHeightMargin { get; set; }
        public bool EnableSolidHeightCheck { get; set; }
        public float SpawnPointClearingRadius { get; set; }
        public float SpawnYOffset { get; set; }
        public bool UseItemStands { get; set; }
        public string ItemStandPrefix { get; set; } = "";
        public float ItemStandMaxRange { get; set; }
    }

    private sealed class VegvisirTargetSnapshot
    {
        public string LocationName { get; set; } = "";
        public string PinName { get; set; } = "";
        public string PinType { get; set; } = "";
        public bool DiscoverAll { get; set; }
        public bool ShowMap { get; set; }
    }

    private sealed class VegvisirSnapshot
    {
        public string Name { get; set; } = "";
        public string UseText { get; set; } = "";
        public string HoverName { get; set; } = "";
        public string SetsGlobalKey { get; set; } = "";
        public string SetsPlayerKey { get; set; } = "";
        public List<VegvisirTargetSnapshot> Locations { get; set; } = new();
    }

    private sealed class PathScopedVegvisirSnapshot
    {
        public string Path { get; set; } = "";
        public VegvisirSnapshot Snapshot { get; set; } = new();
    }

    private sealed class RunestoneTextSnapshot
    {
        public string Topic { get; set; } = "";
        public string Label { get; set; } = "";
        public string Text { get; set; } = "";
    }

    private sealed class RunestoneSnapshot
    {
        public string Name { get; set; } = "";
        public string Topic { get; set; } = "";
        public string Label { get; set; } = "";
        public string Text { get; set; } = "";
        public List<RunestoneTextSnapshot> RandomTexts { get; set; } = new();
        public string LocationName { get; set; } = "";
        public string PinName { get; set; } = "";
        public string PinType { get; set; } = "";
        public bool ShowMap { get; set; }
    }

    private sealed class RunestonePinChanceState
    {
        public string RollKey { get; set; } = "";
        public bool AllowsPin { get; set; } = true;
    }

    private sealed class PathScopedRunestoneSnapshot
    {
        public string Path { get; set; } = "";
        public RunestoneSnapshot Snapshot { get; set; } = new();
    }

    private sealed class PathScopedOfferingBowlSnapshot
    {
        public string Path { get; set; } = "";
        public OfferingBowlSnapshot Snapshot { get; set; } = new();
    }

    private sealed class ItemStandSnapshot
    {
        public string Name { get; set; } = "";
        public bool CanBeRemoved { get; set; }
        public bool AutoAttach { get; set; }
        public string OrientationType { get; set; } = "";
        public List<string> SupportedTypes { get; set; } = new();
        public List<string> SupportedItems { get; set; } = new();
        public List<string> UnsupportedItems { get; set; } = new();
        public float PowerActivationDelay { get; set; }
        public string GuardianPower { get; set; } = "";
    }

    private sealed class PathScopedItemStandSnapshot
    {
        public string Path { get; set; } = "";
        public ItemStandSnapshot Snapshot { get; set; } = new();
    }

    private sealed class LocationSnapshot
    {
        public string Prefab { get; set; } = "";
        public OfferingBowlSnapshot? OfferingBowl { get; set; }
        public List<PathScopedItemStandSnapshot> ItemStands { get; set; } = new();
        public List<PathScopedVegvisirSnapshot> Vegvisirs { get; set; } = new();
        public List<PathScopedRunestoneSnapshot> Runestones { get; set; } = new();
    }

    private sealed class LiveLocationSnapshot
    {
        public string Prefab { get; set; } = "";
        public PathScopedOfferingBowlSnapshot? OfferingBowl { get; set; }
        public List<PathScopedItemStandSnapshot> ItemStands { get; set; } = new();
        public List<PathScopedVegvisirSnapshot> Vegvisirs { get; set; } = new();
        public List<PathScopedRunestoneSnapshot> Runestones { get; set; } = new();
    }

    private sealed class SyncedLocationConfigurationState
    {
        public List<LocationConfigurationEntry> Configuration { get; set; } = new();
        public Dictionary<string, List<LocationConfigurationEntry>> ActiveEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<LocationConfigurationEntry>> LooseItemStandEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string ConfigurationSignature { get; set; } = "";
    }

    private sealed class LocationComponentCatalog
    {
        public string Prefab { get; set; } = "";
        public string? OfferingBowlPath { get; set; }
        public List<string> ItemStandPaths { get; set; } = new();
        public List<string> VegvisirPaths { get; set; } = new();
        public List<string> RunestonePaths { get; set; } = new();
    }

    private sealed class LocationRuntimeComponents
    {
        public Transform Root { get; set; } = null!;
        public List<OfferingBowl> OfferingBowls { get; } = new();
        public OfferingBowl? PrimaryOfferingBowl { get; set; }
        public List<ItemStand> ItemStands { get; } = new();
        public List<Vegvisir> Vegvisirs { get; } = new();
        public List<RuneStone> Runestones { get; } = new();
        public Dictionary<string, OfferingBowl> OfferingBowlsByPath { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ItemStand> ItemStandsByPath { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, Vegvisir> VegvisirsByPath { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, RuneStone> RunestonesByPath { get; set; } = new(StringComparer.Ordinal);
        public List<ItemStand> RelevantItemStands { get; set; } = new();
    }

    private sealed class LocationProxyObservationState
    {
        public ZNetView? NView { get; set; }
        public uint LastObservedDataRevision { get; set; } = uint.MaxValue;
        public string LastObservedAlias { get; set; } = "";
        public int StablePollCount { get; set; }
        public int LastObservedDemandEpoch { get; set; } = -1;
    }

    private sealed class LocationAliasRefreshRequestState
    {
        public int LastQueuedAliasEpoch { get; set; } = -1;
        public int LastQueuedFrame { get; set; } = -1;
        public string LastQueuedResolvedPrefabName { get; set; } = "";
    }

    private sealed class PendingLocationProxyAliasZdoFlush
    {
        public int Epoch { get; set; }
        public int DueFrame { get; set; }
        public uint DataRevision { get; set; }
    }

    private enum LooseOfferingBowlOverrideMode
    {
        RestoreOnly,
        Apply
    }

    private readonly struct LooseOfferingBowlOverrideStamp : IEquatable<LooseOfferingBowlOverrideStamp>
    {
        public LooseOfferingBowlOverrideStamp(
            int reconcileQueueEpoch,
            int registryVersion,
            LooseOfferingBowlOverrideMode mode,
            int rootInstanceId,
            string prefabName,
            string configurationSignature,
            int entryPlanCount,
            int relevantItemStandCount,
            int relevantItemStandSignature)
        {
            ReconcileQueueEpoch = reconcileQueueEpoch;
            RegistryVersion = registryVersion;
            Mode = mode;
            RootInstanceId = rootInstanceId;
            PrefabName = prefabName ?? "";
            ConfigurationSignature = configurationSignature ?? "";
            EntryPlanCount = entryPlanCount;
            RelevantItemStandCount = relevantItemStandCount;
            RelevantItemStandSignature = relevantItemStandSignature;
        }

        public int ReconcileQueueEpoch { get; }
        public int RegistryVersion { get; }
        public LooseOfferingBowlOverrideMode Mode { get; }
        public int RootInstanceId { get; }
        public string PrefabName { get; }
        public string ConfigurationSignature { get; }
        public int EntryPlanCount { get; }
        public int RelevantItemStandCount { get; }
        public int RelevantItemStandSignature { get; }

        public bool Equals(LooseOfferingBowlOverrideStamp other)
        {
            return ReconcileQueueEpoch == other.ReconcileQueueEpoch &&
                   RegistryVersion == other.RegistryVersion &&
                   Mode == other.Mode &&
                   RootInstanceId == other.RootInstanceId &&
                   StringComparer.Ordinal.Equals(PrefabName, other.PrefabName) &&
                   StringComparer.Ordinal.Equals(ConfigurationSignature, other.ConfigurationSignature) &&
                   EntryPlanCount == other.EntryPlanCount &&
                   RelevantItemStandCount == other.RelevantItemStandCount &&
                   RelevantItemStandSignature == other.RelevantItemStandSignature;
        }

        public override bool Equals(object? obj)
        {
            return obj is LooseOfferingBowlOverrideStamp other && Equals(other);
        }

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(ReconcileQueueEpoch);
            hash.Add(RegistryVersion);
            hash.Add((int)Mode);
            hash.Add(RootInstanceId);
            hash.Add(PrefabName, StringComparer.Ordinal);
            hash.Add(ConfigurationSignature, StringComparer.Ordinal);
            hash.Add(EntryPlanCount);
            hash.Add(RelevantItemStandCount);
            hash.Add(RelevantItemStandSignature);
            return hash.ToHashCode();
        }
    }

    private sealed class LooseOfferingBowlOverrideState
    {
        public bool HasLastAppliedStamp { get; set; }
        public LooseOfferingBowlOverrideStamp LastAppliedStamp { get; set; }
    }

    private readonly struct PendingLocationProxyObservation
    {
        public PendingLocationProxyObservation(LocationProxy proxy, int proxyInstanceId, int epoch, int dueFrame)
        {
            Proxy = proxy;
            ProxyInstanceId = proxyInstanceId;
            Epoch = epoch;
            DueFrame = dueFrame;
        }

        public LocationProxy Proxy { get; }
        public int ProxyInstanceId { get; }
        public int Epoch { get; }
        public int DueFrame { get; }
    }

    private sealed class ScheduledFrameQueue<T>
    {
        private readonly SortedDictionary<int, RingBufferQueue<T>> _buckets = new();

        public int Count { get; private set; }

        public void Enqueue(int dueFrame, T item)
        {
            if (!_buckets.TryGetValue(dueFrame, out RingBufferQueue<T>? bucket))
            {
                bucket = new RingBufferQueue<T>();
                _buckets[dueFrame] = bucket;
            }

            bucket.Enqueue(item);
            Count++;
        }

        public bool TryPeekDueFrame(out int dueFrame)
        {
            foreach (KeyValuePair<int, RingBufferQueue<T>> bucket in _buckets)
            {
                dueFrame = bucket.Key;
                return true;
            }

            dueFrame = int.MaxValue;
            return false;
        }

        public bool HasDueItems(int currentFrame)
        {
            return TryPeekDueFrame(out int dueFrame) && dueFrame <= currentFrame;
        }

        public bool TryDequeue(out int dueFrame, out T item)
        {
            while (TryPeekDueFrame(out dueFrame))
            {
                RingBufferQueue<T> bucket = _buckets[dueFrame];
                if (!bucket.TryDequeue(out item))
                {
                    _buckets.Remove(dueFrame);
                    continue;
                }

                Count--;
                if (bucket.Count == 0)
                {
                    _buckets.Remove(dueFrame);
                }

                return true;
            }

            dueFrame = int.MaxValue;
            item = default!;
            return false;
        }

        public void Clear()
        {
            _buckets.Clear();
            Count = 0;
        }
    }

    private sealed class AuthoredItemStandSlotTemplate
    {
        public string Path { get; set; } = "";
        public Vector3 OfferingBowlLocalOffset { get; set; }
    }

    private enum PendingLocationRootPhase
    {
        TraverseHierarchy,
        ReconcileLocations
    }

    private readonly struct PendingLocationTraversalNode
    {
        public PendingLocationTraversalNode(Transform transform, Location? currentLocation)
        {
            Transform = transform;
            CurrentLocation = currentLocation;
        }

        public Transform Transform { get; }
        public Location? CurrentLocation { get; }
    }

    private sealed class PendingLocationRootReconcile
    {
        public int RootInstanceId { get; set; }
        public GameObject RootObject { get; set; } = null!;
        public int Epoch { get; set; }
        public PendingLocationRootPhase Phase { get; set; }
        public List<Location>? Locations { get; set; }
        public List<PendingLocationTraversalNode>? TraversalStack { get; set; }
        public Dictionary<int, LocationRuntimeComponents>? RuntimeComponentsByLocationId { get; set; }
        public int NextIndex { get; set; }
    }

    private readonly struct PendingLocationReconcile
    {
        public PendingLocationReconcile(Location location, int locationInstanceId, int epoch)
        {
            Location = location;
            LocationInstanceId = locationInstanceId;
            Epoch = epoch;
        }

        public Location Location { get; }
        public int LocationInstanceId { get; }
        public int Epoch { get; }
    }

    private readonly struct PendingLooseOfferingBowlOverride
    {
        public PendingLooseOfferingBowlOverride(OfferingBowl offeringBowl, int offeringBowlInstanceId, int epoch)
        {
            OfferingBowl = offeringBowl;
            OfferingBowlInstanceId = offeringBowlInstanceId;
            Epoch = epoch;
        }

        public OfferingBowl OfferingBowl { get; }
        public int OfferingBowlInstanceId { get; }
        public int Epoch { get; }
    }

    private static readonly object Sync = new();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    private static readonly List<LocationSnapshot> Snapshots = new();
    private static readonly Dictionary<string, LocationSnapshot> SnapshotsByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<LocationConfigurationEntry>> ActiveEntriesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> LocationPrefabNamesByHash = new();
    private static readonly Dictionary<LocationProxy, string> RuntimeLocationProxyPrefabsByInstance = new();
    private static readonly Dictionary<ZDOID, string> RuntimeLocationProxyPrefabsByZdoId = new();
    private static readonly ConditionalWeakTable<LocationProxy, LocationProxyObservationState> LocationProxyObservationStates = new();
    private static readonly ConditionalWeakTable<Location, LocationAliasRefreshRequestState> LocationAliasRefreshRequestStates = new();
    private const string LocationProxyResolvedPrefabZdoKey = "DropNSpawn Location Prefab";
    private static readonly HashSet<string> InvalidEntryWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> DuplicateComponentWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> OfferingBowlDiagnosticLogs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ItemStandDiagnosticLogs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LocationDiagnosticLogs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RedundantLocationConditionWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VegvisirWarningLogs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> RunestoneWarningLogs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<ItemStand, ItemStandSnapshot> LooseItemStandSnapshots = new();
    private static readonly Dictionary<Location, LiveLocationSnapshot> LiveLocationSnapshots = new();
    private static readonly Dictionary<string, LocationComponentCatalog> CatalogsByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<Location>> LiveLocationsByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Location, string> LiveLocationPrefabsByInstance = new();
    private static readonly HashSet<LocationProxy> TrackedLocationProxies = new();
    private static readonly ConditionalWeakTable<OfferingBowl, LooseOfferingBowlOverrideState> LooseOfferingBowlOverrideStates = new();
    private static readonly Dictionary<string, List<AuthoredItemStandSlotTemplate>> AuthoredItemStandSlotsByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<LocationConfigurationEntry>> LooseItemStandEntriesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<ItemStand, string> TrackedLooseItemStandPrefabs = new();
    private static readonly Dictionary<ItemStand, string> LooseItemStandAuthoredPathsByInstance = new();
    private static readonly RingBufferQueue<PendingLocationReconcile> PendingLocationReconciles = new();
    private static readonly HashSet<int> PendingLocationReconcileIds = new();
    private static readonly Dictionary<int, int> SuppressedQueuedLocationReconciles = new();
    private static readonly RingBufferQueue<PendingLocationRootReconcile> PendingLocationRootReconciles = new();
    private static readonly HashSet<int> PendingLocationRootReconcileIds = new();
    private static readonly RingBufferQueue<PendingLooseOfferingBowlOverride> PendingLooseOfferingBowlOverrides = new();
    private static readonly HashSet<int> PendingLooseOfferingBowlOverrideIds = new();
    private static readonly ScheduledFrameQueue<ZDOID> PendingLocationProxyAliasZdoFlushIds = new();
    private static readonly Dictionary<ZDOID, PendingLocationProxyAliasZdoFlush> PendingLocationProxyAliasZdoFlushes = new();
    private static readonly Dictionary<ZDOID, int> PendingLocationProxyAliasZdoFlushEnqueuedDueFrames = new();
    private static readonly ScheduledFrameQueue<PendingLocationProxyObservation> PendingLocationProxyObservations = new();
    private static readonly HashSet<int> PendingLocationProxyObservationIds = new();
    private static readonly List<string> PendingLocationProxyCreationPrefabs = new();
    private static readonly HashSet<string> PendingRuntimeLocationProxyAliasDemands = new(StringComparer.OrdinalIgnoreCase);
    private const int LocationAliasRefreshInteractionCooldownFrames = 30;

    private static List<LocationConfigurationEntry> _configuration = new();
    private static string _configurationSignature = "";
    private static bool _initialized;
    private static bool _snapshotsCaptured;
    private static int? _lastProcessedGameDataSignature;
    private static bool _referenceArtifactsAutoRefreshConsumed;
    private static readonly Dictionary<string, string> _lastAppliedEntrySignaturesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static string _lastAppliedConfigurationSignature = "";
    private static int? _lastAppliedGameDataSignature;
    private static bool? _lastAppliedDomainEnabled;
    private static bool _lastAppliedSynchronizedPayloadReady;
    private static bool _synchronizedPayloadReady;
    private static int? _lastCommittedAuthorityEpoch;
    private static int _reconcileQueueEpoch;
    private static bool _needsRuntimeLocationProxyObservation;
    private static int _locationProxyObservationDemandEpoch;
    private static int _runtimeLocationAliasEpoch;
    private static int _locationProxyAliasFlushBudgetFrame = int.MinValue;
    private static int _locationProxyAliasFlushesSentThisFrame;

    private static string ReferenceConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.reference.yml");
    private static string PrimaryOverrideConfigurationPathYml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.yml");
    private static string PrimaryOverrideConfigurationPathYaml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.yaml");
    private static string FullScaffoldConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("location")}.full.yml");
    private static readonly DomainConfigurationRuntime<LocationConfigurationEntry, SyncedLocationConfigurationState> ConfigurationRuntime =
        new(
            new DomainLoadHooks<LocationConfigurationEntry, SyncedLocationConfigurationState>(
                ParseLocalConfigurationDocuments,
                BuildSyncedConfigurationState,
                CommitSyncedConfigurationState,
                RejectLocalConfigurationPayload,
                state => state.ActiveEntriesByPrefab.Count,
                logLocalLoadSuccess: LogLocalConfigurationLoaded,
                onUnchangedPayload: OnSourceOfTruthPayloadUnchanged,
                publishCommittedState: () => ConfigurationDomainHost.PublishSyncedPayload(
                    DropNSpawnPlugin.IsSourceOfTruth,
                    Descriptor,
                    _configuration,
                    _configurationSignature)),
            new DomainSyncHooks<LocationConfigurationEntry, SyncedLocationConfigurationState>(
                (out List<LocationConfigurationEntry> configuration, out string payloadToken) =>
                    ConfigurationDomainHost.TryGetSyncedEntries(Descriptor, out configuration, out payloadToken),
                payloadToken => ConfigurationDomainHost.ShouldSkipSyncedPayload(
                    LoadState,
                    payloadToken,
                    Volatile.Read(ref _synchronizedPayloadReady)),
                BuildSyncedConfigurationState,
                CommitSyncedConfigurationState,
                state => state.ActiveEntriesByPrefab.Count,
                "ServerSync:DropNSpawnLocation",
                () => ConfigurationDomainHost.HandleWaitingForSyncedPayload(
                    MarkSyncedPayloadPending,
                    "Waiting for synchronized location override payload from the server."),
                LogSyncedLocationConfigurationLoaded,
                LogSyncedLocationConfigurationFailure));
    private static DomainLoadState LoadState => ConfigurationRuntime.LoadState;

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
            HashSet<string> previouslyAppliedPrefabs = BuildLastAppliedPrefabs();
            ConfigurationRuntime.EnterPendingSyncedPayloadState(
                DropNSpawnPlugin.IsSourceOfTruth,
                beforeResetLoadState: ResetLoadedConfigurationState,
                afterResetLoadState: () =>
                {
                    _configurationSignature = "";
                    _lastAppliedSynchronizedPayloadReady = false;
                    RestoreTrackedLocations(previouslyAppliedPrefabs);
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

    private static void RestoreTrackedLocations(HashSet<string> prefabs)
    {
        if (prefabs.Count == 0 || !IsGameDataReady())
        {
            return;
        }

        ReapplyActiveEntriesToRegisteredLocations(prefabs);
    }

    internal static bool HandleExpandWorldDataReady()
    {
        lock (Sync)
        {
            if (!DropNSpawnPlugin.IsSourceOfTruth)
            {
                return false;
            }

            string refreshedSignature = NetworkPayloadSyncSupport.ComputeLocationConfigurationSignature(_configuration);
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
            ConfigurationRuntime.ApplySyncedPayload(() => ApplyIfReady(queueLiveReconcile: true));
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
            if (_lastProcessedGameDataSignature == gameDataSignature)
            {
                return;
            }

            // Full location snapshot capture is reserved for explicit scaffold/reference generation.
            ResetReferenceSnapshots();
            ResetRuntimeState(preserveLiveRegistries: true);
            CleanupRegisteredLocations();
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
            _lastProcessedGameDataSignature = gameDataSignature;
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Locations processed after {source}.");
        }
    }

    internal static void ReconcileLocationInstance(Location location)
    {
        lock (Sync)
        {
            TrackLocationInstanceInternal(location);

            if (!_initialized)
            {
                Initialize();
            }

            if (!IsGameDataReady())
            {
                return;
            }

            ReconcileLocationInstanceInternal(location);
        }
    }

    private static bool HasCurrentLocationReconcileWorkForPrefab(string prefabName)
    {
        return prefabName.Length > 0 &&
               (ActiveEntriesByPrefab.ContainsKey(prefabName) ||
                LooseItemStandEntriesByPrefab.ContainsKey(prefabName));
    }

    private static bool ShouldQueueLocationReconcileLocked(Location? location)
    {
        if (location == null || location.gameObject == null)
        {
            return false;
        }

        if (LiveLocationSnapshots.ContainsKey(location))
        {
            return true;
        }

        if (LiveLocationPrefabsByInstance.TryGetValue(location, out string? trackedPrefabName) &&
            HasCurrentLocationReconcileWorkForPrefab(trackedPrefabName ?? ""))
        {
            return true;
        }

        return TryGetLocationPrefabName(location, out string prefabName) &&
               HasCurrentLocationReconcileWorkForPrefab(prefabName);
    }

    internal static void QueueLocationReconcile(Location? location)
    {
        lock (Sync)
        {
            if (!ShouldQueueLocationReconcileLocked(location))
            {
                return;
            }

            int instanceId = location!.GetInstanceID();
            if (!PendingLocationReconcileIds.Add(instanceId))
            {
                return;
            }

            PendingLocationReconciles.Enqueue(new PendingLocationReconcile(location, instanceId, _reconcileQueueEpoch));
        }
    }

    internal static void RecordLocationProxyResolvedPrefab(LocationProxy? proxy, string? prefabName)
    {
        if (proxy == null)
        {
            return;
        }

        string normalizedPrefabName = (prefabName ?? "").Trim();
        if (normalizedPrefabName.Length == 0)
        {
            return;
        }

        LocationProxyObservationState observationState = LocationProxyObservationStates.GetOrCreateValue(proxy);
        observationState.NView ??= proxy.GetComponent<ZNetView>();

        lock (Sync)
        {
            TrackLocationProxyInternal(proxy);
            CacheLocationProxyResolvedPrefabInternal(
                proxy,
                normalizedPrefabName,
                persistToZdo: ZNet.instance != null && ZNet.instance.IsServer(),
                queueLocationReconciles: true);
        }

        ZDO? zdo = observationState.NView?.GetZDO();
        observationState.LastObservedAlias = normalizedPrefabName;
        observationState.LastObservedDataRevision = zdo?.DataRevision ?? uint.MaxValue;
        observationState.StablePollCount = 0;
        observationState.LastObservedDemandEpoch = _locationProxyObservationDemandEpoch;
    }

    internal static void QueueLocationProxyObservation(LocationProxy? proxy)
    {
        lock (Sync)
        {
            QueueLocationProxyObservationInternal(proxy, Time.frameCount);
        }
    }

    internal static bool HasRuntimeLocationAliasDemand()
    {
        lock (Sync)
        {
            return _needsRuntimeLocationProxyObservation;
        }
    }

    internal static void UntrackLocationProxy(LocationProxy? proxy)
    {
        if (proxy == null)
        {
            return;
        }

        LocationProxyObservationStates.Remove(proxy);

        lock (Sync)
        {
            TrackedLocationProxies.Remove(proxy);
            PendingLocationProxyObservationIds.Remove(proxy.GetInstanceID());
            RuntimeLocationProxyPrefabsByInstance.Remove(proxy);

            ZNetView? nview = proxy.GetComponent<ZNetView>();
            ZDOID zdoId = nview?.GetZDO()?.m_uid ?? ZDOID.None;
            if (zdoId != ZDOID.None)
            {
                RuntimeLocationProxyPrefabsByZdoId.Remove(zdoId);
            }

            RefreshLocationProxyObservationDemandLocked();
        }
    }

    internal static void BeginLocationProxyCreationContext(string? prefabName)
    {
        string normalizedPrefabName = (prefabName ?? "").Trim();
        if (normalizedPrefabName.Length == 0)
        {
            return;
        }

        lock (Sync)
        {
            PendingLocationProxyCreationPrefabs.Add(normalizedPrefabName);
        }
    }

    private static int GetLocationProxyObservationDelayFrames(bool hasObservedAlias, int stablePollCount)
    {
        if (!hasObservedAlias)
        {
            return stablePollCount switch
            {
                < 60 => 1,
                < 180 => 10,
                _ => 60
            };
        }

        return stablePollCount switch
        {
            < 30 => 5,
            < 120 => 30,
            _ => 180
        };
    }

    private static bool ShouldContinueLocationProxyObservation(bool hasObservedAlias, int stablePollCount)
    {
        return stablePollCount <
               (hasObservedAlias
                   ? LocationProxyResolvedObservationMaxStableCount
                   : LocationProxyUnresolvedObservationMaxStableCount);
    }

    internal static void EndLocationProxyCreationContext()
    {
        lock (Sync)
        {
            int count = PendingLocationProxyCreationPrefabs.Count;
            if (count > 0)
            {
                PendingLocationProxyCreationPrefabs.RemoveAt(count - 1);
            }
        }
    }

    internal static bool TryGetPendingLocationProxyCreationPrefabName(out string prefabName)
    {
        lock (Sync)
        {
            int count = PendingLocationProxyCreationPrefabs.Count;
            if (count > 0)
            {
                prefabName = PendingLocationProxyCreationPrefabs[count - 1];
                return prefabName.Length > 0;
            }
        }

        prefabName = "";
        return false;
    }

    private static void CacheLocationProxyResolvedPrefabInternal(
        LocationProxy proxy,
        string prefabName,
        bool persistToZdo,
        bool queueLocationReconciles)
    {
        if (proxy == null || prefabName.Length == 0)
        {
            return;
        }

        TrackLocationProxyInternal(proxy);
        bool changed = !RuntimeLocationProxyPrefabsByInstance.TryGetValue(proxy, out string? previousPrefabName) ||
                       !string.Equals(previousPrefabName, prefabName, StringComparison.OrdinalIgnoreCase);
        RuntimeLocationProxyPrefabsByInstance[proxy] = prefabName;

        ZNetView? nview = proxy.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (zdo != null)
        {
            ZDOID zdoId = zdo.m_uid;
            if (zdoId != ZDOID.None)
            {
                if (!RuntimeLocationProxyPrefabsByZdoId.TryGetValue(zdoId, out string? previousZdoPrefabName) ||
                    !string.Equals(previousZdoPrefabName, prefabName, StringComparison.OrdinalIgnoreCase))
                {
                    RuntimeLocationProxyPrefabsByZdoId[zdoId] = prefabName;
                    changed = true;
                }
            }

            if (persistToZdo &&
                !string.Equals(zdo.GetString(LocationProxyResolvedPrefabZdoKey, ""), prefabName, StringComparison.Ordinal))
            {
                zdo.Set(LocationProxyResolvedPrefabZdoKey, prefabName);
                QueueLocationProxyAliasZdoFlushLocked(zdo);
                changed = true;
            }
        }

        if (changed && queueLocationReconciles)
        {
            QueueLocationReconcilesUnderProxyInternal(proxy);
        }

        if (changed)
        {
            _runtimeLocationAliasEpoch++;
            RefreshLocationProxyObservationDemandLocked();
        }
    }

    private static void QueueLocationProxyAliasZdoFlushLocked(ZDO zdo)
    {
        if (zdo == null)
        {
            return;
        }

        ZDOID zdoId = zdo.m_uid;
        if (zdoId == ZDOID.None)
        {
            return;
        }

        PendingLocationProxyAliasZdoFlush request = new()
        {
            Epoch = _reconcileQueueEpoch,
            DueFrame = Time.frameCount + LocationProxyAliasForceSendDebounceFrames,
            DataRevision = zdo.DataRevision
        };
        PendingLocationProxyAliasZdoFlushes[zdoId] = request;
        if (!PendingLocationProxyAliasZdoFlushEnqueuedDueFrames.TryGetValue(zdoId, out int enqueuedDueFrame) ||
            enqueuedDueFrame != request.DueFrame)
        {
            PendingLocationProxyAliasZdoFlushIds.Enqueue(request.DueFrame, zdoId);
            PendingLocationProxyAliasZdoFlushEnqueuedDueFrames[zdoId] = request.DueFrame;
        }
    }

    private static bool TryProcessPendingLocationProxyAliasZdoFlushLocked(float deadline)
    {
        if (PendingLocationProxyAliasZdoFlushes.Count == 0 ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ZDOMan.instance == null)
        {
            return false;
        }

        int currentFrame = Time.frameCount;
        if (_locationProxyAliasFlushBudgetFrame != currentFrame)
        {
            _locationProxyAliasFlushBudgetFrame = currentFrame;
            _locationProxyAliasFlushesSentThisFrame = 0;
        }

        if (_locationProxyAliasFlushesSentThisFrame >= LocationProxyAliasForceSendBudgetPerFrame)
        {
            return false;
        }

        if (!PendingLocationProxyAliasZdoFlushIds.TryPeekDueFrame(out int nextDueFrame) ||
            nextDueFrame > currentFrame)
        {
            return false;
        }

        while (PendingLocationProxyAliasZdoFlushIds.TryPeekDueFrame(out nextDueFrame) &&
               nextDueFrame <= currentFrame)
        {
            if (Time.realtimeSinceStartup >= deadline)
            {
                return false;
            }

            if (!PendingLocationProxyAliasZdoFlushIds.TryDequeue(out int queuedDueFrame, out ZDOID zdoId))
            {
                return false;
            }

            if (!PendingLocationProxyAliasZdoFlushes.TryGetValue(zdoId, out PendingLocationProxyAliasZdoFlush? request))
            {
                continue;
            }

            if (request.Epoch != _reconcileQueueEpoch)
            {
                PendingLocationProxyAliasZdoFlushes.Remove(zdoId);
                PendingLocationProxyAliasZdoFlushEnqueuedDueFrames.Remove(zdoId);
                continue;
            }

            if (request.DueFrame != queuedDueFrame)
            {
                continue;
            }

            ZDO? zdo = ZDOMan.instance.GetZDO(zdoId);
            if (zdo == null)
            {
                PendingLocationProxyAliasZdoFlushes.Remove(zdoId);
                PendingLocationProxyAliasZdoFlushEnqueuedDueFrames.Remove(zdoId);
                continue;
            }

            if (zdo.DataRevision < request.DataRevision)
            {
                request.DueFrame = currentFrame + 1;
                PendingLocationProxyAliasZdoFlushes[zdoId] = request;
                PendingLocationProxyAliasZdoFlushIds.Enqueue(request.DueFrame, zdoId);
                PendingLocationProxyAliasZdoFlushEnqueuedDueFrames[zdoId] = request.DueFrame;
                continue;
            }

            PendingLocationProxyAliasZdoFlushes.Remove(zdoId);
            PendingLocationProxyAliasZdoFlushEnqueuedDueFrames.Remove(zdoId);
            _locationProxyAliasFlushesSentThisFrame++;
            ZDOMan.instance.ForceSendZDO(zdoId);
            return true;
        }

        return false;
    }

    private static void TrackLocationProxyInternal(LocationProxy? proxy)
    {
        if (proxy == null || proxy.gameObject == null)
        {
            return;
        }

        TrackedLocationProxies.Add(proxy);
        LocationProxyObservationState observationState = LocationProxyObservationStates.GetOrCreateValue(proxy);
        observationState.NView ??= proxy.GetComponent<ZNetView>();
    }

    private static void CleanupTrackedLocationProxiesLocked()
    {
        List<LocationProxy?> deadProxies = new();
        foreach (LocationProxy proxy in TrackedLocationProxies)
        {
            if (proxy == null || proxy.gameObject == null)
            {
                deadProxies.Add(proxy);
            }
        }

        foreach (LocationProxy? deadProxy in deadProxies)
        {
            if (deadProxy != null)
            {
                TrackedLocationProxies.Remove(deadProxy);
            }
        }
    }

    private static bool ShouldObserveLocationProxyResolvedPrefabLocked(LocationProxy proxy, LocationProxyObservationState observationState)
    {
        if (!_needsRuntimeLocationProxyObservation || proxy == null || proxy.gameObject == null)
        {
            return false;
        }

        if (observationState.LastObservedDemandEpoch != _locationProxyObservationDemandEpoch)
        {
            observationState.LastObservedDemandEpoch = _locationProxyObservationDemandEpoch;
            observationState.StablePollCount = 0;
        }

        return observationState.LastObservedAlias.Length == 0 ||
               PendingRuntimeLocationProxyAliasDemands.Contains(observationState.LastObservedAlias);
    }

    private static void QueueTrackedLocationProxyObservationsLocked(int dueFrame)
    {
        CleanupTrackedLocationProxiesLocked();
        foreach (LocationProxy proxy in TrackedLocationProxies.ToList())
        {
            QueueLocationProxyObservationInternal(proxy, dueFrame);
        }
    }

    private static void QueueLocationProxyObservationInternal(LocationProxy? proxy, int dueFrame)
    {
        if (proxy == null || proxy.gameObject == null)
        {
            return;
        }

        TrackLocationProxyInternal(proxy);
        LocationProxyObservationState observationState = LocationProxyObservationStates.GetOrCreateValue(proxy);
        if (!ShouldObserveLocationProxyResolvedPrefabLocked(proxy, observationState))
        {
            return;
        }

        int proxyInstanceId = proxy.GetInstanceID();
        if (!PendingLocationProxyObservationIds.Add(proxyInstanceId))
        {
            return;
        }

        PendingLocationProxyObservations.Enqueue(dueFrame, new PendingLocationProxyObservation(
            proxy,
            proxyInstanceId,
            _reconcileQueueEpoch,
            dueFrame));
    }

    private static void QueueNextLocationProxyObservationLocked(LocationProxy proxy, LocationProxyObservationState observationState, bool hasObservedAlias)
    {
        observationState.StablePollCount++;
        if (!ShouldContinueLocationProxyObservation(hasObservedAlias, observationState.StablePollCount))
        {
            return;
        }

        QueueLocationProxyObservationInternal(
            proxy,
            Time.frameCount + GetLocationProxyObservationDelayFrames(hasObservedAlias, observationState.StablePollCount));
    }

    private static bool TryProcessPendingLocationProxyObservationLocked(float deadline)
    {
        int currentFrame = Time.frameCount;
        if (!PendingLocationProxyObservations.TryPeekDueFrame(out int nextDueFrame) ||
            nextDueFrame > currentFrame)
        {
            return false;
        }

        while (PendingLocationProxyObservations.TryPeekDueFrame(out nextDueFrame) &&
               nextDueFrame <= currentFrame)
        {
            if (Time.realtimeSinceStartup >= deadline)
            {
                return false;
            }

            if (!PendingLocationProxyObservations.TryDequeue(out _, out PendingLocationProxyObservation request))
            {
                return false;
            }

            PendingLocationProxyObservationIds.Remove(request.ProxyInstanceId);
            if (request.Epoch != _reconcileQueueEpoch || request.Proxy == null || request.Proxy.gameObject == null)
            {
                continue;
            }

            TrackLocationProxyInternal(request.Proxy);
            LocationProxyObservationState observationState = LocationProxyObservationStates.GetOrCreateValue(request.Proxy);
            if (!ShouldObserveLocationProxyResolvedPrefabLocked(request.Proxy, observationState))
            {
                return true;
            }

            observationState.NView ??= request.Proxy.GetComponent<ZNetView>();
            ZDO? zdo = observationState.NView?.GetZDO();
            if (zdo == null)
            {
                QueueNextLocationProxyObservationLocked(request.Proxy, observationState, observationState.LastObservedAlias.Length > 0);
                return true;
            }

            uint dataRevision = zdo.DataRevision;
            if (dataRevision == observationState.LastObservedDataRevision)
            {
                QueueNextLocationProxyObservationLocked(request.Proxy, observationState, observationState.LastObservedAlias.Length > 0);
                return true;
            }

            observationState.LastObservedDataRevision = dataRevision;
            string normalizedPrefabName = (zdo.GetString(LocationProxyResolvedPrefabZdoKey, "") ?? "").Trim();
            if (normalizedPrefabName.Length == 0)
            {
                QueueNextLocationProxyObservationLocked(request.Proxy, observationState, false);
                return true;
            }

            if (string.Equals(observationState.LastObservedAlias, normalizedPrefabName, StringComparison.OrdinalIgnoreCase))
            {
                QueueNextLocationProxyObservationLocked(request.Proxy, observationState, true);
                return true;
            }

            observationState.LastObservedAlias = normalizedPrefabName;
            observationState.StablePollCount = 0;
            CacheLocationProxyResolvedPrefabInternal(
                request.Proxy,
                normalizedPrefabName,
                persistToZdo: false,
                queueLocationReconciles: true);
            QueueLocationProxyObservationInternal(request.Proxy, Time.frameCount + 1);
            return true;
        }

        return false;
    }

    private static void QueueLocationReconcilesUnderProxyInternal(LocationProxy proxy)
    {
        if (proxy == null || proxy.gameObject == null)
        {
            return;
        }

        List<Location> locations = new();
        CollectLocationsUnderRoot(proxy.transform, locations);
        foreach (Location location in locations)
        {
            if (location == null || location.gameObject == null)
            {
                continue;
            }

            int instanceId = location.GetInstanceID();
            if (!PendingLocationReconcileIds.Add(instanceId))
            {
                continue;
            }

            PendingLocationReconciles.Enqueue(new PendingLocationReconcile(location, instanceId, _reconcileQueueEpoch));
        }
    }

    internal static void MaybeQueueRuntimeLocationAliasRefresh(Component? component)
    {
        lock (Sync)
        {
            if (component == null ||
                component.gameObject == null ||
                !IsGameDataReady())
            {
                return;
            }

            Location? location = component.GetComponentInParent<Location>(true);
            if (location == null || location.gameObject == null)
            {
                return;
            }

            if (!TryResolveRuntimeLocationPrefabName(location, out string resolvedPrefabName) ||
                resolvedPrefabName.Length == 0)
            {
                return;
            }

            if (LiveLocationPrefabsByInstance.TryGetValue(location, out string? trackedPrefabName) &&
                string.Equals(trackedPrefabName, resolvedPrefabName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            LocationAliasRefreshRequestState requestState = LocationAliasRefreshRequestStates.GetOrCreateValue(location);
            if (requestState.LastQueuedAliasEpoch == _runtimeLocationAliasEpoch &&
                string.Equals(requestState.LastQueuedResolvedPrefabName, resolvedPrefabName, StringComparison.OrdinalIgnoreCase) &&
                Time.frameCount - requestState.LastQueuedFrame < LocationAliasRefreshInteractionCooldownFrames)
            {
                return;
            }

            requestState.LastQueuedAliasEpoch = _runtimeLocationAliasEpoch;
            requestState.LastQueuedResolvedPrefabName = resolvedPrefabName;
            requestState.LastQueuedFrame = Time.frameCount;

            QueueLocationReconcile(location);
        }
    }

    internal static void ReconcileSpawnedLocationRoot(GameObject? rootObject)
    {
        QueueSpawnedLocationRootReconcile(rootObject);
    }

    internal static void QueueSpawnedLocationRootReconcile(GameObject? rootObject)
    {
        lock (Sync)
        {
            if (rootObject == null)
            {
                return;
            }

            int instanceId = rootObject.GetInstanceID();
            if (!PendingLocationRootReconcileIds.Add(instanceId))
            {
                return;
            }

            PendingLocationRootReconciles.Enqueue(new PendingLocationRootReconcile
            {
                RootInstanceId = instanceId,
                RootObject = rootObject,
                Epoch = _reconcileQueueEpoch,
                Phase = PendingLocationRootPhase.TraverseHierarchy
            });
        }
    }

    internal static void TrackLocationInstance(Location? location)
    {
        lock (Sync)
        {
            TrackLocationInstanceInternal(location);
        }
    }

    internal static void TrackSpawnedLocationRoot(GameObject? rootObject)
    {
        lock (Sync)
        {
            TrackSpawnedLocationRootInternal(rootObject);
        }
    }

    internal static bool HasPendingReconcileWork()
    {
        lock (Sync)
        {
            int currentFrame = Time.frameCount;
            return PendingLocationReconciles.Count > 0 ||
                   PendingLocationRootReconciles.Count > 0 ||
                   HasPendingLooseLocationOverrideWorkLocked() ||
                   (PendingLocationProxyObservationIds.Count > 0 &&
                    PendingLocationProxyObservations.HasDueItems(currentFrame)) ||
                   (PendingLocationProxyAliasZdoFlushes.Count > 0 &&
                    PendingLocationProxyAliasZdoFlushIds.HasDueItems(currentFrame));
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

            if (TryProcessPendingLocationProxyAliasZdoFlushLocked(deadline))
            {
                return true;
            }

            if (TryProcessPendingLocationProxyObservationLocked(deadline))
            {
                return true;
            }

            if (!IsGameDataReady() || DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
            {
                return false;
            }

            if (PendingLocationRootReconciles.Count > 0)
            {
                return ProcessQueuedLocationRootStep(deadline);
            }

            while (PendingLocationReconciles.Count > 0)
            {
                if (!PendingLocationReconciles.TryDequeue(out PendingLocationReconcile queuedReconcile))
                {
                    continue;
                }

                int instanceId = queuedReconcile.LocationInstanceId;
                PendingLocationReconcileIds.Remove(instanceId);
                if (queuedReconcile.Epoch != _reconcileQueueEpoch || queuedReconcile.Location == null)
                {
                    continue;
                }

                Location location = queuedReconcile.Location;
                if (SuppressedQueuedLocationReconciles.TryGetValue(instanceId, out int suppressedCount) && suppressedCount > 0)
                {
                    if (suppressedCount == 1)
                    {
                        SuppressedQueuedLocationReconciles.Remove(instanceId);
                    }
                    else
                    {
                        SuppressedQueuedLocationReconciles[instanceId] = suppressedCount - 1;
                    }

                    return true;
                }

                TrackLocationInstanceInternal(location);
                if (!_initialized)
                {
                    Initialize();
                }

                ReconcileLocationInstanceInternal(location);
                return true;
            }

            return TryProcessPendingLooseLocationOverrideLocked();
        }
    }

    internal static void UntrackLocationInstance(Location? location)
    {
        lock (Sync)
        {
            if (location != null && LiveLocationPrefabsByInstance.TryGetValue(location, out string? prefabName))
            {
                UnregisterLiveLocation(location, prefabName);
            }
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
                error = "Location game data is not ready yet.";
                return false;
            }

            RefreshReferenceSnapshots();
            Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
            File.WriteAllText(path, BuildFullScaffoldConfigurationTemplate());
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Wrote location full scaffold configuration to {path}.");
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

            RefreshReferenceSnapshots();
            WriteReferenceConfigurationFile(BuildReferenceConfigurationTemplate(), $"Updated location reference configuration at {ReferenceConfigurationPath}.");
            ReferenceRefreshSupport.RecordAutoUpdateState(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, ComputeReferenceSourceSignature(), logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
            ResetReferenceSnapshots();
        }
    }

    private static bool IsGameDataReady()
    {
        return ZoneSystem.instance != null && ZNetScene.instance != null && ObjectDB.instance != null;
    }

    private static int ComputeGameDataSignature()
    {
        if (!IsGameDataReady() || ZoneSystem.instance == null || ZNetScene.instance == null || ObjectDB.instance == null)
        {
            return 0;
        }

        unchecked
        {
            int hash = 17;
            hash = hash * 31 + ZoneSystem.instance.GetInstanceID();
            hash = hash * 31 + ZNetScene.instance.GetInstanceID();
            hash = hash * 31 + ObjectDB.instance.GetInstanceID();
            hash = HashNormalizedKeys(hash, BuildLiveLocationSourceKeys());
            hash = HashNormalizedKeys(hash, BuildConfiguredLocationResolutionKeys());
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

    private static IEnumerable<string> BuildLiveLocationSourceKeys()
    {
        if (ZoneSystem.instance == null)
        {
            yield break;
        }

        foreach (ZoneSystem.ZoneLocation? location in ZoneSystem.instance.m_locations)
        {
            string prefabName = ReferenceRefreshSupport.NormalizeKey(GetZoneLocationPrefabName(location));
            if (prefabName.Length == 0)
            {
                continue;
            }

            yield return prefabName;
        }
    }

    private static IEnumerable<string> BuildConfiguredLocationResolutionKeys()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string _, List<LocationConfigurationEntry> entries) in ActiveEntriesByPrefab)
        {
            foreach (LocationConfigurationEntry entry in entries)
            {
                LocationOfferingBowlDefinition? offeringBowl = entry.OfferingBowl;
                if (offeringBowl != null)
                {
                    foreach (string key in BuildConfiguredLocationResolutionKeys(offeringBowl, seen))
                    {
                        yield return key;
                    }
                }

                if (entry.ItemStands == null)
                {
                    continue;
                }

                foreach (LocationItemStandDefinition itemStand in entry.ItemStands)
                {
                    foreach (string key in BuildConfiguredLocationResolutionKeys(itemStand, seen))
                    {
                        yield return key;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> BuildConfiguredLocationResolutionKeys(
        LocationOfferingBowlDefinition definition,
        HashSet<string> seen)
    {
        if (TryBuildResolvedPrefabSignatureKey("item", definition.BossItem, ResolveItemPrefab(definition.BossItem, null), out string? bossItemKey) &&
            seen.Add(bossItemKey))
        {
            yield return bossItemKey;
        }

        if (TryBuildResolvedPrefabSignatureKey("spawn", definition.BossPrefab, ResolveSpawnPrefab(definition.BossPrefab, null), out string? bossPrefabKey) &&
            seen.Add(bossPrefabKey))
        {
            yield return bossPrefabKey;
        }

        if (TryBuildResolvedPrefabSignatureKey("item", definition.ItemPrefab, ResolveItemPrefab(definition.ItemPrefab, null), out string? itemPrefabKey) &&
            seen.Add(itemPrefabKey))
        {
            yield return itemPrefabKey;
        }
    }

    private static IEnumerable<string> BuildConfiguredLocationResolutionKeys(
        LocationItemStandDefinition definition,
        HashSet<string> seen)
    {
        foreach (string? itemName in definition.SupportedItems ?? Enumerable.Empty<string>())
        {
            if (TryBuildResolvedPrefabSignatureKey("item", itemName, ResolveItemPrefab(itemName, null), out string? supportedItemKey) &&
                seen.Add(supportedItemKey))
            {
                yield return supportedItemKey;
            }
        }

        foreach (string? itemName in definition.UnsupportedItems ?? Enumerable.Empty<string>())
        {
            if (TryBuildResolvedPrefabSignatureKey("item", itemName, ResolveItemPrefab(itemName, null), out string? unsupportedItemKey) &&
                seen.Add(unsupportedItemKey))
            {
                yield return unsupportedItemKey;
            }
        }

        if (TryBuildResolvedStatusEffectSignatureKey(definition.GuardianPower, out string? guardianPowerKey) &&
            seen.Add(guardianPowerKey))
        {
            yield return guardianPowerKey;
        }
    }

    private static bool TryBuildResolvedPrefabSignatureKey(string prefix, string? prefabName, GameObject? prefab, out string key)
    {
        string normalizedPrefabName = ReferenceRefreshSupport.NormalizeKey(prefabName);
        if (normalizedPrefabName.Length == 0)
        {
            key = "";
            return false;
        }

        key = string.Concat(
            prefix,
            ":",
            normalizedPrefabName,
            ":",
            (prefab != null ? prefab.GetInstanceID() : 0).ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private static bool TryBuildResolvedStatusEffectSignatureKey(string? statusEffectName, out string key)
    {
        string normalizedStatusEffectName = ReferenceRefreshSupport.NormalizeKey(statusEffectName);
        if (normalizedStatusEffectName.Length == 0)
        {
            key = "";
            return false;
        }

        StatusEffect? statusEffect = ResolveStatusEffect(normalizedStatusEffectName, null);
        key = string.Concat(
            "status:",
            normalizedStatusEffectName,
            ":",
            (statusEffect != null ? statusEffect.GetInstanceID() : 0).ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private static string ComputeReferenceSourceSignature()
    {
        if (ZoneSystem.instance == null)
        {
            return "";
        }

        return ReferenceRefreshSupport.ComputeStableHashForKeys(
            ZoneSystem.instance.m_locations.Select(location => location?.m_prefabName ?? location?.m_prefab.Name));
    }

    private static bool EnsurePrimaryOverrideConfigurationFileExists()
    {
        if (DomainConfigurationFileSupport.HasAnyOverrideConfigurationFile(
                "location",
                PrimaryOverrideConfigurationPathYml,
                PrimaryOverrideConfigurationPathYaml))
        {
            return false;
        }

        Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
        File.WriteAllText(PrimaryOverrideConfigurationPathYml, BuildPrimaryOverrideConfigurationTemplate());
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Created location override configuration at {PrimaryOverrideConfigurationPathYml}.");
        return true;
    }

    private static void EnsureReferenceArtifactsUpToDate()
    {
        if (!IsGameDataReady())
        {
            return;
        }

        string currentSourceSignature = ComputeReferenceSourceSignature();
        if (!File.Exists(ReferenceConfigurationPath))
        {
            if (!PluginSettingsFacade.ShouldAutoCreateMissingReferenceFiles())
            {
                return;
            }

            RefreshReferenceSnapshots();
            WriteReferenceConfigurationFile(BuildReferenceConfigurationTemplate(), $"Created location reference configuration at {ReferenceConfigurationPath}.");
            ReferenceRefreshSupport.RecordAutoUpdateState(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, currentSourceSignature, logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
            ResetReferenceSnapshots();
            return;
        }

        if (!PluginSettingsFacade.ShouldAutoUpdateReferenceFiles())
        {
            return;
        }

        if (ReferenceRefreshSupport.ShouldSkipAutoUpdate(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, currentSourceSignature, ReferenceRefreshSupport.CurrentReferenceLogicVersion))
        {
            return;
        }

        RefreshReferenceSnapshots();
        WriteReferenceConfigurationFile(BuildReferenceConfigurationTemplate(), $"Updated location reference configuration at {ReferenceConfigurationPath}.");
        ReferenceRefreshSupport.RecordAutoUpdateState(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, currentSourceSignature, logicVersion: ReferenceRefreshSupport.CurrentReferenceLogicVersion);
        ResetReferenceSnapshots();
    }

    private static void LoadConfiguration()
    {
        if (DropNSpawnPlugin.IsSourceOfTruth)
        {
            EnsurePrimaryOverrideConfigurationFileExists();
            ConfigurationRuntime.ReloadSourceOfTruth(EnumerateOverrideConfigurationPaths().ToList());
            return;
        }

        ConfigurationRuntime.ReloadSynced();
    }

    private static void ResetLoadedConfigurationState()
    {
        ClearQueuedReconcileState();
        ResetLocationRuntimeConfigurationState();
        LocationPrefabNamesByHash.Clear();
        ActiveEntriesByPrefab.Clear();
        AuthoredItemStandSlotsByPrefab.Clear();
        LooseItemStandEntriesByPrefab.Clear();
        OfferingBowlDiagnosticLogs.Clear();
        InvalidEntryWarnings.Clear();
        ItemStandDiagnosticLogs.Clear();
        LocationDiagnosticLogs.Clear();
        RunestoneWarningLogs.Clear();
        _configuration = new List<LocationConfigurationEntry>();
        Volatile.Write(ref _synchronizedPayloadReady, false);
        RefreshLocationProxyObservationDemandLocked();
    }

    private static List<LocationConfigurationEntry> CloneAndNormalizeConfigurationEntries(
        List<LocationConfigurationEntry>? configuration,
        string source)
    {
        List<LocationConfigurationEntry> normalizedConfiguration =
            NetworkPayloadSyncSupport.CloneEntries(Descriptor, configuration);
        foreach (LocationConfigurationEntry entry in normalizedConfiguration)
        {
            string effectiveSource = string.IsNullOrWhiteSpace(entry.SourcePath) ? source : (entry.SourcePath ?? "");
            entry.SourcePath = effectiveSource;
            entry.Prefab = (entry.Prefab ?? "").Trim();
            NormalizeOfferingBowlDefinition(entry.OfferingBowl);
            NormalizeItemStandDefinitions(entry.ItemStands);
            NormalizeVegvisirDefinitions(entry.Vegvisirs);
            NormalizeRunestoneDefinitions(entry.Runestones);
            NormalizeRunestoneGlobalPinsDefinition(entry.RunestoneGlobalPins);
            FinalizeNormalizedEntry(entry);
            StripRedundantLocationComponentConditions(entry, effectiveSource);
        }

        return normalizedConfiguration;
    }

    private static SyncedLocationConfigurationState BuildSyncedConfigurationState(
        List<LocationConfigurationEntry> configuration,
        string source)
    {
        using InvalidEntryWarningSuppressionScope _ = BeginInvalidEntryWarningSuppressionForSyncedClientBuild(source);
        SyncedLocationConfigurationState state = new();
        foreach (LocationConfigurationEntry entry in CloneAndNormalizeConfigurationEntries(configuration, source))
        {
            string effectiveSource = entry.SourcePath ?? "";

            bool hasRunestoneGlobalPins = HasRunestoneGlobalPinsOverride(entry.RunestoneGlobalPins);
            if (entry.Prefab.Length == 0 && !hasRunestoneGlobalPins)
            {
                WarnInvalidEntry($"Entry in '{effectiveSource}' is missing prefab.");
                continue;
            }

            if (!entry.Enabled)
            {
                continue;
            }

            RemoveEffectiveConfigurationEntry(
                state.Configuration,
                state.ActiveEntriesByPrefab,
                state.LooseItemStandEntriesByPrefab,
                entry.Prefab,
                entry.RuleId);
            state.Configuration.Add(entry);

            if (entry.Prefab.Length == 0 || !HasOverride(entry))
            {
                continue;
            }

            GetOrCreateActiveEntries(state.ActiveEntriesByPrefab, entry.Prefab).Add(entry);
            if (HasLooseItemStandOverride(entry.ItemStands))
            {
                GetOrCreateLooseItemStandEntries(state.LooseItemStandEntriesByPrefab, entry.Prefab).Add(entry);
            }
        }

        state.ConfigurationSignature = NetworkPayloadSyncSupport.ComputeLocationConfigurationSignature(state.Configuration);
        return state;
    }

    private static void CommitSyncedConfigurationState(SyncedLocationConfigurationState state, string payloadToken)
    {
        ResetLoadedConfigurationState();
        _configuration = state.Configuration;
        foreach ((string prefabName, List<LocationConfigurationEntry> entries) in state.ActiveEntriesByPrefab)
        {
            ActiveEntriesByPrefab[prefabName] = entries;
        }

        foreach ((string prefabName, List<LocationConfigurationEntry> entries) in state.LooseItemStandEntriesByPrefab)
        {
            LooseItemStandEntriesByPrefab[prefabName] = entries;
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
        RefreshLocationProxyObservationDemandLocked();
    }

    private static LocalLoadResult<LocationConfigurationEntry> ParseLocalConfigurationDocuments(
        List<ConfigurationLoadSupport.LocalYamlDocument> documents)
    {
        bool success = TryBuildLocalConfigurationState(
            documents,
            out SyncedLocationConfigurationState state,
            out List<string> errors,
            out int loadedFileCount);

        return new LocalLoadResult<LocationConfigurationEntry>
        {
            Entries = success ? state.Configuration : new List<LocationConfigurationEntry>(),
            Errors = errors,
            Warnings = new List<string>(),
            ParsedEntryCount = success ? state.Configuration.Count : 0,
            LoadedFileCount = loadedFileCount
        };
    }

    private static bool TryBuildLocalConfigurationState(
        List<ConfigurationLoadSupport.LocalYamlDocument> documents,
        out SyncedLocationConfigurationState state,
        out List<string> errors,
        out int loadedFileCount)
    {
        state = new SyncedLocationConfigurationState();
        errors = new List<string>();
        loadedFileCount = 0;

        List<LocationConfigurationEntry> configuration = new();
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
                List<LocationConfigurationEntry> parsedConfiguration = ParseConfiguration(yaml);
                List<LocationConfigurationEntry> sourcedConfiguration =
                    NetworkPayloadSyncSupport.CloneEntries(Descriptor, parsedConfiguration);
                foreach (LocationConfigurationEntry entry in sourcedConfiguration)
                {
                    entry.SourcePath = document.Path;
                }

                configuration.AddRange(sourcedConfiguration);
                loadedFileCount++;
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to parse location YAML '{document.Path}'. Location override YAML must start with a root list like '- prefab: ...'. {ex}");
            }
        }

        if (errors.Count > 0)
        {
            return false;
        }

        state = BuildSyncedConfigurationState(configuration, "");
        return true;
    }

    private static void LogLocalConfigurationLoaded(int acceptedEntryCount, int loadedFileCount)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Loaded {acceptedEntryCount} location configuration(s) from {loadedFileCount} override file(s).");
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

    private static void LogSyncedLocationConfigurationLoaded(string payloadToken, int acceptedEntryCount)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Loaded {acceptedEntryCount} synchronized location configuration(s) from the server.");
    }

    private static void LogSyncedLocationConfigurationFailure(string payloadToken, Exception ex)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to deserialize synchronized location payload DTO. {ex}");
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
            "Rejected location reload. Keeping the previous authoritative location configuration.");
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
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo("Loaded 0 location configuration(s) from 0 override file(s).");
            return;
        }

        int loadedFileCount = 0;
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
                List<LocationConfigurationEntry> configuration = ParseConfiguration(yaml);
                MergeConfiguration(configuration, document.Path);
                loadedFileCount++;
            }
            catch (Exception ex)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to parse location YAML '{document.Path}'. Location override YAML must start with a root list like '- prefab: ...'. {ex}");
            }
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Loaded {ActiveEntriesByPrefab.Count} location configuration(s) from {loadedFileCount} override file(s).");
    }

    private static List<LocationConfigurationEntry> ParseConfiguration(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new List<LocationConfigurationEntry>();
        }

        return Deserializer.Deserialize<List<LocationConfigurationEntry>>(yaml) ?? new List<LocationConfigurationEntry>();
    }

    private static void MergeConfiguration(List<LocationConfigurationEntry> configuration, string source)
    {
        foreach (LocationConfigurationEntry entry in CloneAndNormalizeConfigurationEntries(configuration, source))
        {
            bool hasRunestoneGlobalPins = HasRunestoneGlobalPinsOverride(entry.RunestoneGlobalPins);
            if (entry.Prefab.Length == 0 && !hasRunestoneGlobalPins)
            {
                WarnInvalidEntry($"Entry in '{entry.SourcePath}' is missing prefab.");
                continue;
            }

            if (!entry.Enabled)
            {
                continue;
            }

            RemoveEffectiveConfigurationEntry(entry.Prefab, entry.RuleId);
            _configuration.Add(entry);

            if (entry.Prefab.Length > 0 && HasOverride(entry))
            {
                GetOrCreateActiveEntries(entry.Prefab).Add(entry);
                if (HasLooseItemStandOverride(entry.ItemStands))
                {
                    GetOrCreateLooseItemStandEntries(entry.Prefab).Add(entry);
                }
            }
        }
    }

    private static bool RemoveEffectiveConfigurationEntry(string prefabName, string ruleId)
    {
        return RemoveEffectiveConfigurationEntry(
            _configuration,
            ActiveEntriesByPrefab,
            LooseItemStandEntriesByPrefab,
            prefabName,
            ruleId);
    }

    private static bool RemoveEffectiveConfigurationEntry(
        List<LocationConfigurationEntry> configuration,
        Dictionary<string, List<LocationConfigurationEntry>> activeEntriesByPrefab,
        Dictionary<string, List<LocationConfigurationEntry>> looseItemStandEntriesByPrefab,
        string prefabName,
        string ruleId)
    {
        bool removed = false;
        for (int index = configuration.Count - 1; index >= 0; index--)
        {
            LocationConfigurationEntry existingEntry = configuration[index];
            if (!string.Equals(existingEntry.Prefab, prefabName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existingEntry.RuleId, ruleId, StringComparison.Ordinal))
            {
                continue;
            }

            configuration.RemoveAt(index);
            removed = true;
        }

        if (activeEntriesByPrefab.TryGetValue(prefabName, out List<LocationConfigurationEntry>? entries))
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

        if (looseItemStandEntriesByPrefab.TryGetValue(prefabName, out List<LocationConfigurationEntry>? looseItemStandEntries))
        {
            for (int index = looseItemStandEntries.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(looseItemStandEntries[index].RuleId, ruleId, StringComparison.Ordinal))
                {
                    continue;
                }

                looseItemStandEntries.RemoveAt(index);
                removed = true;
            }

            if (looseItemStandEntries.Count == 0)
            {
                looseItemStandEntriesByPrefab.Remove(prefabName);
            }
        }

        return removed;
    }

    private static List<LocationConfigurationEntry> GetOrCreateActiveEntries(string prefabName)
    {
        return GetOrCreateActiveEntries(ActiveEntriesByPrefab, prefabName);
    }

    private static List<LocationConfigurationEntry> GetOrCreateActiveEntries(
        Dictionary<string, List<LocationConfigurationEntry>> activeEntriesByPrefab,
        string prefabName)
    {
        if (!activeEntriesByPrefab.TryGetValue(prefabName, out List<LocationConfigurationEntry>? entries))
        {
            entries = new List<LocationConfigurationEntry>();
            activeEntriesByPrefab[prefabName] = entries;
        }

        return entries;
    }

    private static List<LocationConfigurationEntry> GetOrCreateLooseItemStandEntries(string prefabName)
    {
        return GetOrCreateLooseItemStandEntries(LooseItemStandEntriesByPrefab, prefabName);
    }

    private static List<LocationConfigurationEntry> GetOrCreateLooseItemStandEntries(
        Dictionary<string, List<LocationConfigurationEntry>> looseItemStandEntriesByPrefab,
        string prefabName)
    {
        if (!looseItemStandEntriesByPrefab.TryGetValue(prefabName, out List<LocationConfigurationEntry>? entries))
        {
            entries = new List<LocationConfigurationEntry>();
            looseItemStandEntriesByPrefab[prefabName] = entries;
        }

        return entries;
    }

    private static void NormalizeOfferingBowlDefinition(LocationOfferingBowlDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        definition.BossItem = definition.BossItem?.Trim();
        definition.BossPrefab = definition.BossPrefab?.Trim();
        definition.ItemPrefab = definition.ItemPrefab?.Trim();
        definition.SetGlobalKey = definition.SetGlobalKey?.Trim();
        definition.ItemStandPrefix = definition.ItemStandPrefix?.Trim();
        definition.Data = NormalizeOptionalString(definition.Data);
        definition.Fields = NormalizeOptionalStringDictionary(definition.Fields);
        definition.Objects = NormalizeOptionalStringList(definition.Objects);
        if (definition.SpawnBossDistance?.HasValues() == true)
        {
            definition.SpawnBossMinDistance = RangeFormatting.GetMin(definition.SpawnBossDistance, definition.SpawnBossMinDistance);
            definition.SpawnBossMaxDistance = RangeFormatting.GetMax(definition.SpawnBossDistance, definition.SpawnBossMinDistance, definition.SpawnBossMaxDistance);
        }
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

    private static void NormalizeVegvisirDefinitions(List<LocationVegvisirDefinition>? definitions)
    {
        if (definitions == null)
        {
            return;
        }

        foreach (LocationVegvisirDefinition definition in definitions)
        {
            NormalizeVegvisirDefinition(definition);
        }
    }

    private static void NormalizeVegvisirDefinition(LocationVegvisirDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        definition.Path = (definition.Path ?? "").Trim();
        definition.ExpectedLocations = definition.ExpectedLocations?
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        definition.Name = definition.Name?.Trim();
        definition.UseText = definition.UseText?.Trim();
        definition.HoverName = definition.HoverName?.Trim();
        definition.SetsGlobalKey = definition.SetsGlobalKey?.Trim();
        definition.SetsPlayerKey = definition.SetsPlayerKey?.Trim();
        if (definition.Locations == null)
        {
            return;
        }

        foreach (LocationVegvisirTargetDefinition target in definition.Locations)
        {
            target.LocationName = (target.LocationName ?? "").Trim();
            target.PinName = target.PinName?.Trim();
            target.PinType = target.PinType?.Trim();
            if (target.Weight.HasValue)
            {
                target.Weight = Mathf.Max(0f, target.Weight.Value);
            }
        }
    }

    private static void NormalizeRunestoneDefinitions(List<LocationRunestoneDefinition>? definitions)
    {
        if (definitions == null)
        {
            return;
        }

        foreach (LocationRunestoneDefinition definition in definitions)
        {
            NormalizeRunestoneDefinition(definition);
        }
    }

    private static void NormalizeRunestoneDefinition(LocationRunestoneDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        definition.Path = (definition.Path ?? "").Trim();
        definition.ExpectedLocationName = definition.ExpectedLocationName?.Trim();
        definition.ExpectedLabel = definition.ExpectedLabel?.Trim();
        definition.ExpectedTopic = definition.ExpectedTopic?.Trim();
        definition.Name = definition.Name?.Trim();
        definition.Topic = definition.Topic?.Trim();
        definition.Label = definition.Label?.Trim();
        definition.Text = definition.Text?.Trim();
        definition.LocationName = definition.LocationName?.Trim();
        definition.PinName = definition.PinName?.Trim();
        definition.PinType = definition.PinType?.Trim();
        if (definition.Chance.HasValue)
        {
            definition.Chance = Mathf.Clamp01(definition.Chance.Value);
        }

        if (definition.RandomTexts == null)
        {
            return;
        }

        foreach (LocationRunestoneTextDefinition text in definition.RandomTexts)
        {
            text.Topic = text.Topic?.Trim();
            text.Label = text.Label?.Trim();
            text.Text = text.Text?.Trim();
        }
    }

    private static void NormalizeRunestoneGlobalPinsDefinition(LocationRunestoneGlobalPinsDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        if (definition.TargetLocations == null)
        {
            return;
        }

        foreach (LocationRunestoneGlobalPinTargetDefinition target in definition.TargetLocations)
        {
            target.LocationName = (target.LocationName ?? "").Trim();
            target.PinName = target.PinName?.Trim();
            target.PinType = target.PinType?.Trim();
            target.SourceBiomes = target.SourceBiomes?
                .Select(value => (value ?? "").Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (target.Chance.HasValue)
            {
                target.Chance = Mathf.Clamp01(target.Chance.Value);
            }
        }
    }

    private static void NormalizeItemStandDefinitions(List<LocationItemStandDefinition>? definitions)
    {
        if (definitions == null)
        {
            return;
        }

        foreach (LocationItemStandDefinition definition in definitions)
        {
            NormalizeItemStandDefinition(definition);
        }
    }

    private static void NormalizeItemStandDefinition(LocationItemStandDefinition? definition)
    {
        if (definition == null)
        {
            return;
        }

        definition.Path = definition.Path?.Trim();
        definition.Name = definition.Name?.Trim();
        definition.OrientationType = definition.OrientationType?.Trim();
        definition.GuardianPower = definition.GuardianPower?.Trim();
        definition.SupportedTypes = definition.SupportedTypes?
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        definition.SupportedItems = definition.SupportedItems?
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        definition.UnsupportedItems = definition.UnsupportedItems?
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void FinalizeNormalizedEntry(LocationConfigurationEntry entry)
    {
        entry.RuleId = NormalizeOptionalRuleId(entry.RuleId) ?? BuildRuleId(entry);
    }

    private static string BuildRuleId(LocationConfigurationEntry entry)
    {
        LocationConfigurationEntry normalizedEntry = new()
        {
            Prefab = entry.Prefab,
            Enabled = true,
            Conditions = entry.Conditions,
            OfferingBowl = entry.OfferingBowl,
            ItemStands = entry.ItemStands,
            Vegvisirs = entry.Vegvisirs,
            Runestones = entry.Runestones,
            RunestoneGlobalPins = entry.RunestoneGlobalPins
        };

        return $"{entry.Prefab}:{NetworkPayloadSyncSupport.ComputeLocationEntryIdentitySignature(normalizedEntry)}";
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

    private static void StripRedundantLocationComponentConditions(LocationConfigurationEntry entry, string source)
    {
        StripRedundantLocationFilter(entry.Prefab, "conditions", entry.Conditions, source);
        StripUnsupportedLocationConditionFields(entry.Prefab, "conditions", entry.Conditions);
    }

    private static void StripRedundantLocationFilter(string prefabName, string componentName, ConditionsDefinition? conditions, string source)
    {
        if (conditions?.Locations == null || conditions.Locations.Count == 0)
        {
            return;
        }

        conditions.Locations = null;

        string normalizedPrefabName = string.IsNullOrWhiteSpace(prefabName) ? "(missing prefab)" : prefabName;
        string warningKey = $"{source}|{normalizedPrefabName}|{componentName}";
        if (!RedundantLocationConditionWarnings.Add(warningKey))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Entry '{normalizedPrefabName}' in '{source}' uses '{componentName}.conditions.locations', but {componentName} is already scoped by the parent location prefab. This filter is ignored.");
    }

    private static void StripUnsupportedLocationConditionFields(string prefabName, string componentName, ConditionsDefinition? conditions)
    {
        if (conditions == null)
        {
            return;
        }

        string context = string.IsNullOrWhiteSpace(prefabName)
            ? "(missing prefab)"
            : prefabName.Trim();

        if (conditions.Level?.HasValues() == true ||
            conditions.MinLevel.HasValue ||
            conditions.MaxLevel.HasValue)
        {
            WarnInvalidEntry($"Entry '{context}' uses {componentName}.conditions.level, but level filters are only valid for character conditions. The key was ignored.");
            conditions.Level = null;
            conditions.MinLevel = null;
            conditions.MaxLevel = null;
        }

        if (conditions.TimeOfDay != null)
        {
            WarnInvalidEntry($"Entry '{context}' uses {componentName}.conditions.timeOfDay, but location conditions are evaluated only when the location is loaded or reconciled. The key was ignored.");
            conditions.TimeOfDay = null;
        }

        if (conditions.RequiredEnvironments?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses {componentName}.conditions.requiredEnvironments, but location conditions are evaluated only when the location is loaded or reconciled. The key was ignored.");
            conditions.RequiredEnvironments = null;
        }

        if (conditions.RequiredGlobalKeys?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses {componentName}.conditions.requiredGlobalKeys, but location conditions are static location filters only. The key was ignored.");
            conditions.RequiredGlobalKeys = null;
        }

        if (conditions.ForbiddenGlobalKeys?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses {componentName}.conditions.forbiddenGlobalKeys, but location conditions are static location filters only. The key was ignored.");
            conditions.ForbiddenGlobalKeys = null;
        }

        if (conditions.States?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses {componentName}.conditions.states, but state filters are only valid for character conditions. The key was ignored.");
            conditions.States = null;
        }

        if (conditions.Factions?.Count > 0)
        {
            WarnInvalidEntry($"Entry '{context}' uses {componentName}.conditions.factions, but faction filters are only valid for character conditions. The key was ignored.");
            conditions.Factions = null;
        }

        if (conditions.InsidePlayerBase.HasValue)
        {
            WarnInvalidEntry($"Entry '{context}' uses {componentName}.conditions.insidePlayerBase, but location conditions are static location filters only. The key was ignored.");
            conditions.InsidePlayerBase = null;
        }
    }

    private static bool HasOverride(LocationConfigurationEntry entry)
    {
        return HasOfferingBowlOverride(entry.OfferingBowl) ||
               HasItemStandOverride(entry.ItemStands) ||
               HasVegvisirOverride(entry.Vegvisirs) ||
               HasRunestoneOverride(entry.Runestones);
    }

    private static bool HasRunestoneGlobalPinsOverride(LocationRunestoneGlobalPinsDefinition? definition)
    {
        return definition?.TargetLocations != null;
    }

    private static bool HasOfferingBowlOverride(LocationOfferingBowlDefinition? definition)
    {
        if (definition == null)
        {
            return false;
        }

        return definition.Name != null ||
               definition.UseItemText != null ||
               definition.UsedAltarText != null ||
               definition.CantOfferText != null ||
               definition.WrongOfferText != null ||
               definition.IncompleteOfferText != null ||
               definition.BossItem != null ||
               definition.BossItems.HasValue ||
               definition.BossPrefab != null ||
               definition.ItemPrefab != null ||
               definition.SetGlobalKey != null ||
               definition.RenderSpawnAreaGizmos.HasValue ||
               definition.AlertOnSpawn.HasValue ||
               definition.SpawnBossDelay.HasValue ||
               definition.SpawnBossDistance?.HasValues() == true ||
               definition.SpawnBossMaxDistance.HasValue ||
               definition.SpawnBossMinDistance.HasValue ||
               definition.SpawnBossMaxYDistance.HasValue ||
               definition.GetSolidHeightMargin.HasValue ||
               definition.EnableSolidHeightCheck.HasValue ||
               definition.SpawnPointClearingRadius.HasValue ||
               definition.SpawnYOffset.HasValue ||
               definition.UseItemStands.HasValue ||
               definition.ItemStandPrefix != null ||
               definition.ItemStandMaxRange.HasValue ||
               definition.RespawnMinutes.HasValue ||
               definition.Data != null ||
               definition.Fields != null ||
               definition.Objects != null;
    }

    private static bool HasVegvisirOverride(List<LocationVegvisirDefinition>? definitions)
    {
        return definitions != null && definitions.Any(HasVegvisirOverride);
    }

    private static bool HasVegvisirOverride(LocationVegvisirDefinition? definition)
    {
        if (definition == null)
        {
            return false;
        }

        return definition.Name != null ||
               definition.UseText != null ||
               definition.HoverName != null ||
               definition.SetsGlobalKey != null ||
               definition.SetsPlayerKey != null ||
               definition.Locations != null;
    }

    private static bool HasRunestoneOverride(List<LocationRunestoneDefinition>? definitions)
    {
        return definitions != null && definitions.Any(HasRunestoneOverride);
    }

    private static bool HasRunestoneOverride(LocationRunestoneDefinition? definition)
    {
        if (definition == null)
        {
            return false;
        }

        return definition.Name != null ||
               definition.Topic != null ||
               definition.Label != null ||
               definition.Text != null ||
               definition.RandomTexts != null ||
               definition.LocationName != null ||
               definition.PinName != null ||
               definition.PinType != null ||
               definition.ShowMap.HasValue ||
               definition.Chance.HasValue;
    }

    private static bool HasItemStandOverride(List<LocationItemStandDefinition>? definitions)
    {
        return definitions != null && definitions.Any(HasItemStandOverride);
    }

    private static bool HasLooseItemStandOverride(List<LocationItemStandDefinition>? definitions)
    {
        return definitions != null && definitions.Any(HasLooseItemStandOverride);
    }

    private static bool HasItemStandOverride(LocationItemStandDefinition? definition)
    {
        if (definition == null)
        {
            return false;
        }

        return definition.Name != null ||
               definition.CanBeRemoved.HasValue ||
               definition.AutoAttach.HasValue ||
               definition.OrientationType != null ||
               definition.SupportedTypes != null ||
               definition.SupportedItems != null ||
               definition.UnsupportedItems != null ||
               definition.PowerActivationDelay.HasValue ||
               definition.GuardianPower != null;
    }

    private static bool HasLooseItemStandOverride(LocationItemStandDefinition? definition)
    {
        // Detached altar clones can only be resolved after runtime spawn, even when the authored
        // definition is path-targeted in the location template. Keep all effective itemStand
        // overrides available to the loose-itemstand path and let runtime matching narrow them.
        return HasItemStandOverride(definition);
    }

    private static bool HasConditions(ConditionsDefinition? conditions)
    {
        return DropConditionEvaluator.HasConditions(conditions);
    }

    private static IEnumerable<string> EnumerateOverrideConfigurationPaths()
    {
        return DomainConfigurationFileSupport.EnumerateOverrideConfigurationPaths(
            "location",
            PrimaryOverrideConfigurationPathYml,
            PrimaryOverrideConfigurationPathYaml);
    }

    private static bool IsOverrideConfigurationFileName(string fileName)
    {
        return DomainConfigurationFileSupport.IsOverrideConfigurationFileName("location", fileName);
    }

    private static void CaptureReferenceSnapshotsIfNeeded()
    {
        if (_snapshotsCaptured)
        {
            return;
        }

        if (ZoneSystem.instance == null)
        {
            return;
        }

        foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
        {
            CaptureSnapshot(location);
        }

        _snapshotsCaptured = true;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"Captured {Snapshots.Count} location snapshot(s).");
    }

    private static void ResetReferenceSnapshots()
    {
        Snapshots.Clear();
        SnapshotsByPrefab.Clear();
        DuplicateComponentWarnings.Clear();
        _snapshotsCaptured = false;
    }

    private static void ResetRuntimeState(bool preserveLiveRegistries)
    {
        ClearQueuedReconcileState();
        CatalogsByPrefab.Clear();
        LiveLocationSnapshots.Clear();
        LooseItemStandSnapshots.Clear();
        TrackedLooseItemStandPrefabs.Clear();
        LooseItemStandAuthoredPathsByInstance.Clear();
        PendingLocationProxyCreationPrefabs.Clear();

        if (!preserveLiveRegistries)
        {
            LiveLocationsByPrefab.Clear();
            LiveLocationPrefabsByInstance.Clear();
            TrackedLocationProxies.Clear();
            RuntimeLocationProxyPrefabsByInstance.Clear();
            RuntimeLocationProxyPrefabsByZdoId.Clear();
            _runtimeLocationAliasEpoch++;
        }

        RefreshLocationProxyObservationDemandLocked();
    }

    private static void ClearQueuedReconcileState()
    {
        _reconcileQueueEpoch++;
        PendingLocationReconciles.Clear();
        PendingLocationReconcileIds.Clear();
        SuppressedQueuedLocationReconciles.Clear();
        PendingLocationRootReconciles.Clear();
        PendingLocationRootReconcileIds.Clear();
        PendingLooseOfferingBowlOverrides.Clear();
        PendingLooseOfferingBowlOverrideIds.Clear();
        PendingLocationProxyAliasZdoFlushIds.Clear();
        PendingLocationProxyAliasZdoFlushes.Clear();
        PendingLocationProxyAliasZdoFlushEnqueuedDueFrames.Clear();
        PendingLocationProxyObservations.Clear();
        PendingLocationProxyObservationIds.Clear();
        _locationProxyAliasFlushBudgetFrame = int.MinValue;
        _locationProxyAliasFlushesSentThisFrame = 0;
    }

    private static void RefreshReferenceSnapshots()
    {
        ResetReferenceSnapshots();
        CaptureReferenceSnapshotsIfNeeded();
    }

    private static void CaptureSnapshot(ZoneSystem.ZoneLocation location)
    {
        if (!location.m_prefab.IsValid)
        {
            return;
        }

        string prefabName = GetZoneLocationPrefabName(location);
        if (prefabName.Length == 0 || SnapshotsByPrefab.ContainsKey(prefabName))
        {
            return;
        }

        location.m_prefab.Load();
        GameObject? rootPrefab = location.m_prefab.Asset;
        if (rootPrefab == null)
        {
            return;
        }

        List<OfferingBowl> offeringBowlList = new();
        List<ItemStand> itemStandList = new();
        List<Vegvisir> vegvisirList = new();
        List<RuneStone> runestoneList = new();
        CollectLocationRuntimeComponents(rootPrefab.transform, offeringBowlList, itemStandList, vegvisirList, runestoneList);
        OfferingBowl[] offeringBowls = offeringBowlList.ToArray();
        ItemStand[] itemStands = itemStandList.ToArray();
        Vegvisir[] vegvisirs = vegvisirList.ToArray();
        RuneStone[] runestones = runestoneList.ToArray();
        if (offeringBowls.Length == 0 && itemStands.Length == 0 && vegvisirs.Length == 0 && runestones.Length == 0)
        {
            return;
        }

        WarnDuplicateComponent(prefabName, "OfferingBowl", offeringBowls.Length);

        LocationSnapshot snapshot = new()
        {
            Prefab = prefabName,
            OfferingBowl = offeringBowls.Length > 0 ? CaptureOfferingBowlSnapshot(offeringBowls[0]) : null,
            ItemStands = itemStands
                .Select(itemStand => CapturePathScopedItemStandSnapshot(rootPrefab.transform, itemStand))
                .OrderBy(itemStand => itemStand.Path, StringComparer.Ordinal)
                .ToList(),
            Vegvisirs = vegvisirs
                .Select(vegvisir => CaptureVegvisirSnapshot(rootPrefab.transform, vegvisir))
                .OrderBy(vegvisir => vegvisir.Path, StringComparer.Ordinal)
                .ToList(),
            Runestones = runestones
                .Select(runestone => CaptureRunestoneSnapshot(rootPrefab.transform, runestone))
                .OrderBy(runestone => runestone.Path, StringComparer.Ordinal)
                .ToList()
        };

        Snapshots.Add(snapshot);
        SnapshotsByPrefab[prefabName] = snapshot;
    }

    private static void WarnDuplicateComponent(string prefabName, string componentName, int count)
    {
        if (count <= 1)
        {
            return;
        }

        string key = $"{prefabName}@{componentName}";
        if (!DuplicateComponentWarnings.Add(key))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Location prefab '{prefabName}' has multiple {componentName} components. The first one will be used for location.yml.");
    }

    private static OfferingBowlSnapshot CaptureOfferingBowlSnapshot(OfferingBowl offeringBowl)
    {
        return new OfferingBowlSnapshot
        {
            Name = offeringBowl.m_name,
            UseItemText = offeringBowl.m_useItemText,
            UsedAltarText = offeringBowl.m_usedAltarText,
            CantOfferText = offeringBowl.m_cantOfferText,
            WrongOfferText = offeringBowl.m_wrongOfferText,
            IncompleteOfferText = offeringBowl.m_incompleteOfferText,
            BossItem = NormalizeReferencePrefabName(offeringBowl.m_bossItem != null ? offeringBowl.m_bossItem.gameObject : null) ?? "",
            BossItems = offeringBowl.m_bossItems,
            BossPrefab = NormalizeReferencePrefabName(offeringBowl.m_bossPrefab) ?? "",
            ItemPrefab = NormalizeReferencePrefabName(offeringBowl.m_itemPrefab != null ? offeringBowl.m_itemPrefab.gameObject : null) ?? "",
            SetGlobalKey = offeringBowl.m_setGlobalKey,
            RenderSpawnAreaGizmos = offeringBowl.m_renderSpawnAreaGizmos,
            AlertOnSpawn = offeringBowl.m_alertOnSpawn,
            SpawnBossDelay = offeringBowl.m_spawnBossDelay,
            SpawnBossMaxDistance = offeringBowl.m_spawnBossMaxDistance,
            SpawnBossMinDistance = offeringBowl.m_spawnBossMinDistance,
            SpawnBossMaxYDistance = offeringBowl.m_spawnBossMaxYDistance,
            GetSolidHeightMargin = offeringBowl.m_getSolidHeightMargin,
            EnableSolidHeightCheck = offeringBowl.m_enableSolidHeightCheck,
            SpawnPointClearingRadius = offeringBowl.m_spawnPointClearingRadius,
            SpawnYOffset = offeringBowl.m_spawnYOffset,
            UseItemStands = offeringBowl.m_useItemStands,
            ItemStandPrefix = offeringBowl.m_itemStandPrefix,
            ItemStandMaxRange = offeringBowl.m_itemstandMaxRange
        };
    }

    private static PathScopedVegvisirSnapshot CaptureVegvisirSnapshot(Transform root, Vegvisir vegvisir)
    {
        return new PathScopedVegvisirSnapshot
        {
            Path = GetRelativePath(root, vegvisir.transform),
            Snapshot = CaptureVegvisirSnapshot(vegvisir)
        };
    }

    private static VegvisirSnapshot CaptureVegvisirSnapshot(Vegvisir vegvisir)
    {
        return new VegvisirSnapshot
        {
            Name = vegvisir.m_name,
            UseText = vegvisir.m_useText,
            HoverName = vegvisir.m_hoverName,
            SetsGlobalKey = vegvisir.m_setsGlobalKey,
            SetsPlayerKey = vegvisir.m_setsPlayerKey,
            Locations = vegvisir.m_locations
                .Select(location => new VegvisirTargetSnapshot
                {
                    LocationName = location.m_locationName,
                    PinName = location.m_pinName,
                    PinType = location.m_pinType.ToString(),
                    DiscoverAll = location.m_discoverAll,
                    ShowMap = location.m_showMap
                })
                .ToList()
        };
    }

    private static PathScopedRunestoneSnapshot CaptureRunestoneSnapshot(Transform root, RuneStone runestone)
    {
        return new PathScopedRunestoneSnapshot
        {
            Path = GetRelativePath(root, runestone.transform),
            Snapshot = CaptureRunestoneSnapshot(runestone)
        };
    }

    private static RunestoneSnapshot CaptureRunestoneSnapshot(RuneStone runestone)
    {
        return new RunestoneSnapshot
        {
            Name = runestone.m_name,
            Topic = runestone.m_topic,
            Label = runestone.m_label,
            Text = runestone.m_text,
            RandomTexts = (runestone.m_randomTexts ?? new List<RuneStone.RandomRuneText>())
                .Select(text => new RunestoneTextSnapshot
                {
                    Topic = text.m_topic,
                    Label = text.m_label,
                    Text = text.m_text
                })
                .ToList(),
            LocationName = runestone.m_locationName,
            PinName = runestone.m_pinName,
            PinType = runestone.m_pinType.ToString(),
            ShowMap = runestone.m_showMap
        };
    }

    private static ItemStandSnapshot CaptureItemStandSnapshot(ItemStand itemStand)
    {
        return new ItemStandSnapshot
        {
            Name = itemStand.m_name,
            CanBeRemoved = itemStand.m_canBeRemoved,
            AutoAttach = itemStand.m_autoAttach,
            OrientationType = itemStand.m_orientationType.ToString(),
            SupportedTypes = itemStand.m_supportedTypes.Select(type => type.ToString()).ToList(),
            SupportedItems = itemStand.m_supportedItems
                .Where(item => item != null)
                .Select(item => NormalizeReferencePrefabName(item.gameObject) ?? "")
                .Where(name => name.Length > 0)
                .ToList(),
            UnsupportedItems = itemStand.m_unsupportedItems
                .Where(item => item != null)
                .Select(item => NormalizeReferencePrefabName(item.gameObject) ?? "")
                .Where(name => name.Length > 0)
                .ToList(),
            PowerActivationDelay = itemStand.m_powerActivationDelay,
            GuardianPower = itemStand.m_guardianPower != null ? itemStand.m_guardianPower.name : ""
        };
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

        int gameDataSignature = ComputeGameDataSignature();
        bool domainEnabled = PluginSettingsFacade.IsLocationDomainEnabled();
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

    private static void ValidateConfiguredPrefabs()
    {
        if (ZoneSystem.instance == null)
        {
            return;
        }

        HashSet<string> availablePrefabs = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
        {
            string prefabName = (location?.m_prefabName ?? location?.m_prefab.Name ?? "").Trim();
            if (prefabName.Length > 0)
            {
                availablePrefabs.Add(prefabName);
            }
        }

        foreach ((string prefabName, List<LocationConfigurationEntry> entries) in ActiveEntriesByPrefab)
        {
            if (availablePrefabs.Contains(prefabName))
            {
                continue;
            }

            foreach (LocationConfigurationEntry entry in entries)
            {
                if (LooksLikeExternalLocationAlias(prefabName))
                {
                    continue;
                }

                string basePrefabName = GetLocationPrefabBaseName(prefabName);
                if (prefabName.IndexOf(':') >= 0 &&
                    basePrefabName.Length > 0 &&
                    availablePrefabs.Contains(basePrefabName))
                {
                    if (IsServerSynchronizedLocationSource(entry.SourcePath))
                    {
                        continue;
                    }

                    WarnInvalidEntry(
                        $"Location prefab '{prefabName}' from {DescribeEntrySource(entry.SourcePath)} was not found in ZoneSystem, but base prefab '{basePrefabName}' exists. If this is an external alias, it must be observed at runtime; otherwise this may be a typo.");
                    continue;
                }

                WarnInvalidEntry($"Location prefab '{prefabName}' from {DescribeEntrySource(entry.SourcePath)} was not found in ZoneSystem.");
            }
        }
    }

    private static bool LooksLikeExternalLocationAlias(string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName) || prefabName.IndexOf(':') < 0)
        {
            return false;
        }

        return RuntimeLocationProxyPrefabsByInstance.Values.Any(value => string.Equals(value, prefabName, StringComparison.OrdinalIgnoreCase)) ||
               RuntimeLocationProxyPrefabsByZdoId.Values.Any(value => string.Equals(value, prefabName, StringComparison.OrdinalIgnoreCase)) ||
               LiveLocationPrefabsByInstance.Values.Any(value => string.Equals(value, prefabName, StringComparison.OrdinalIgnoreCase)) ||
               LiveLocationsByPrefab.ContainsKey(prefabName) ||
               CatalogsByPrefab.ContainsKey(prefabName);
    }

    private static void RefreshLocationProxyObservationDemandLocked()
    {
        HashSet<string> nextPendingAliases = new(StringComparer.OrdinalIgnoreCase);
        CollectPendingRuntimeLocationProxyAliasDemandsLocked(ActiveEntriesByPrefab.Keys, nextPendingAliases);
        CollectPendingRuntimeLocationProxyAliasDemandsLocked(LooseItemStandEntriesByPrefab.Keys, nextPendingAliases);

        bool demandChanged = !PendingRuntimeLocationProxyAliasDemands.SetEquals(nextPendingAliases);
        if (demandChanged)
        {
            PendingRuntimeLocationProxyAliasDemands.Clear();
            foreach (string alias in nextPendingAliases)
            {
                PendingRuntimeLocationProxyAliasDemands.Add(alias);
            }

            PendingLocationProxyObservations.Clear();
            PendingLocationProxyObservationIds.Clear();
            _locationProxyObservationDemandEpoch++;
        }

        _needsRuntimeLocationProxyObservation = PendingRuntimeLocationProxyAliasDemands.Count > 0;
        if (_needsRuntimeLocationProxyObservation &&
            (demandChanged || PendingLocationProxyObservationIds.Count == 0))
        {
            QueueTrackedLocationProxyObservationsLocked(Time.frameCount);
        }
    }

    private static void CollectPendingRuntimeLocationProxyAliasDemandsLocked(
        IEnumerable<string> prefabNames,
        HashSet<string> target)
    {
        foreach (string prefabName in prefabNames ?? Enumerable.Empty<string>())
        {
            string normalizedPrefabName = (prefabName ?? "").Trim();
            if (normalizedPrefabName.Length == 0 ||
                normalizedPrefabName.IndexOf(':') < 0 ||
                LooksLikeExternalLocationAlias(normalizedPrefabName))
            {
                continue;
            }

            target.Add(normalizedPrefabName);
        }
    }

    private static bool IsServerSynchronizedLocationSource(string? sourcePath)
    {
        string normalizedSource = (sourcePath ?? "").Trim();
        return normalizedSource.StartsWith("ServerSync:", StringComparison.OrdinalIgnoreCase);
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

    private static void ReapplyActiveEntriesToRegisteredLocations(HashSet<string> prefabs)
    {
        foreach (Location location in GetRegisteredLocations(prefabs))
        {
            if (location != null)
            {
                ReconcileLocationInstanceInternal(location);
            }
        }

        RefreshTrackedLooseItemStands(prefabs);
    }

    private static void QueueRegisteredLocationReconciles(HashSet<string> prefabs)
    {
        foreach (Location location in GetRegisteredLocations(prefabs))
        {
            if (location != null)
            {
                QueueLocationReconcile(location);
            }
        }

        RefreshTrackedLooseItemStands(prefabs);
    }

    private static Dictionary<string, string> BuildActiveEntrySignaturesByPrefab()
    {
        return DomainEntrySignatureSupport.BuildSignaturesByKey(
            ActiveEntriesByPrefab,
            NetworkPayloadSyncSupport.ComputeLocationConfigurationSignature);
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

    private static void RegisterLiveLocation(Location location, string prefabName)
    {
        if (location == null || location.gameObject == null || prefabName.Length == 0)
        {
            return;
        }

        if (LiveLocationPrefabsByInstance.TryGetValue(location, out string? previousPrefabName))
        {
            if (string.Equals(previousPrefabName, prefabName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UnregisterLiveLocation(location, previousPrefabName);
        }

        LiveLocationPrefabsByInstance[location] = prefabName;
        if (!LiveLocationsByPrefab.TryGetValue(prefabName, out HashSet<Location>? locations))
        {
            locations = new HashSet<Location>();
            LiveLocationsByPrefab[prefabName] = locations;
        }

        locations.Add(location);
    }

    private static void TrackLocationInstanceInternal(Location? location)
    {
        if (location == null || !TryGetLocationPrefabName(location, out string prefabName))
        {
            return;
        }

        RegisterLiveLocation(location, prefabName);
    }

    private static IEnumerable<Location> GetRegisteredLocations(HashSet<string> dirtyPrefabs)
    {
        CleanupRegisteredLocations();
        HashSet<Location> visited = new();
        foreach (string prefabName in dirtyPrefabs)
        {
            if (!LiveLocationsByPrefab.TryGetValue(prefabName, out HashSet<Location>? locations))
            {
                continue;
            }

            foreach (Location location in locations)
            {
                if (location != null && location.gameObject != null && visited.Add(location))
                {
                    yield return location;
                }
            }
        }
    }

    private static void CleanupRegisteredLocations()
    {
        List<Location>? deadLocations = null;
        foreach (Location location in LiveLocationPrefabsByInstance.Keys)
        {
            if (location == null || location.gameObject == null)
            {
                deadLocations ??= new List<Location>();
                deadLocations.Add(location!);
            }
        }

        if (deadLocations == null)
        {
            return;
        }

        foreach (Location deadLocation in deadLocations)
        {
            if (LiveLocationPrefabsByInstance.TryGetValue(deadLocation, out string? prefabName))
            {
                UnregisterLiveLocation(deadLocation, prefabName);
            }
        }
    }

    private static void UnregisterLiveLocation(Location location, string prefabName)
    {
        LiveLocationPrefabsByInstance.Remove(location);
        LiveLocationSnapshots.Remove(location);
        if (!LiveLocationsByPrefab.TryGetValue(prefabName, out HashSet<Location>? locations))
        {
            return;
        }

        locations.Remove(location);
        if (locations.Count == 0)
        {
            LiveLocationsByPrefab.Remove(prefabName);
        }
    }

    private static void ReconcileLocationInstanceInternal(Location location, LocationRuntimeComponents? runtimeComponents = null)
    {
        if (location == null || !TryGetLocationPrefabName(location, out string prefabName))
        {
            return;
        }

        RegisterLiveLocation(location, prefabName);
        bool domainEnabled = PluginSettingsFacade.IsLocationDomainEnabled();
        bool hasLiveSnapshot = LiveLocationSnapshots.ContainsKey(location);
        List<CompiledLocationEntryPlan>? entryPlans = null;
        if (domainEnabled)
        {
            EnsureRuntimeConfigurationState();
            if (_runtimeConfigurationState.PlansByPrefab.TryGetValue(prefabName, out CompiledLocationPrefabPlan? prefabPlan) &&
                prefabPlan.ActiveEntryPlans.Count > 0)
            {
                entryPlans = prefabPlan.ActiveEntryPlans;
            }
        }

        bool hasActiveEntry = entryPlans != null;
        if (!hasLiveSnapshot && !hasActiveEntry)
        {
            return;
        }

        Transform locationRoot = runtimeComponents?.Root ?? location.transform;
        IReadOnlyList<OfferingBowl> offeringBowls;
        IReadOnlyList<ItemStand> itemStands;
        IReadOnlyList<Vegvisir> vegvisirs;
        IReadOnlyList<RuneStone> runestones;
        if (runtimeComponents != null)
        {
            offeringBowls = runtimeComponents.OfferingBowls;
            itemStands = runtimeComponents.ItemStands;
            vegvisirs = runtimeComponents.Vegvisirs;
            runestones = runtimeComponents.Runestones;
        }
        else
        {
            List<OfferingBowl> offeringBowlList = new();
            List<ItemStand> itemStandList = new();
            List<Vegvisir> vegvisirList = new();
            List<RuneStone> runestoneList = new();
            CollectLocationRuntimeComponents(locationRoot, offeringBowlList, itemStandList, vegvisirList, runestoneList);
            offeringBowls = offeringBowlList;
            itemStands = itemStandList;
            vegvisirs = vegvisirList;
            runestones = runestoneList;
        }

        OfferingBowl? offeringBowl = runtimeComponents?.PrimaryOfferingBowl ?? offeringBowls.FirstOrDefault();
        LocationComponentCatalog catalog = GetOrCreateLocationComponentCatalog(prefabName, locationRoot, offeringBowls, itemStands, vegvisirs, runestones);
        CaptureLiveLocationSnapshotIfNeeded(location, prefabName, locationRoot, offeringBowls, itemStands, vegvisirs, runestones);
        if (runtimeComponents != null)
        {
            RestoreLiveLocationSnapshot(location, runtimeComponents);
        }
        else
        {
            RestoreLiveLocationSnapshot(location, locationRoot, offeringBowls, itemStands, vegvisirs, runestones);
        }

        if (!domainEnabled)
        {
            return;
        }

        if (entryPlans == null)
        {
            return;
        }

        Dictionary<string, Vegvisir> liveVegvisirsByPath = runtimeComponents?.VegvisirsByPath ?? BuildVegvisirLookup(locationRoot, vegvisirs);
        Dictionary<string, RuneStone> liveRunestonesByPath = runtimeComponents?.RunestonesByPath ?? BuildRunestoneLookup(locationRoot, runestones);
        LogLocationReconcileCandidate(location, prefabName, offeringBowl, itemStands, vegvisirs.Count, runestones.Count, catalog, LiveLocationSnapshots[location]);

        List<ItemStand> relevantItemStands = runtimeComponents?.RelevantItemStands ?? GetRelevantLocationItemStands(offeringBowl, itemStands);
        Dictionary<string, ItemStand> liveItemStandsByPath = runtimeComponents?.ItemStandsByPath ?? BuildItemStandLookup(locationRoot, itemStands);
        ApplyCompiledLocationEntryPlans(
            location.gameObject,
            entryPlans,
            offeringBowl,
            relevantItemStands,
            liveItemStandsByPath,
            liveVegvisirsByPath,
            liveRunestonesByPath,
            prefabName,
            locationRoot);
    }

    private static LocationComponentCatalog GetOrCreateLocationComponentCatalog(
        string prefabName,
        Transform locationRoot,
        IReadOnlyList<OfferingBowl> offeringBowls,
        IReadOnlyList<ItemStand> itemStands,
        IReadOnlyList<Vegvisir> vegvisirs,
        IReadOnlyList<RuneStone> runestones)
    {
        if (CatalogsByPrefab.TryGetValue(prefabName, out LocationComponentCatalog? existingCatalog))
        {
            return existingCatalog;
        }

        WarnDuplicateComponent(prefabName, "OfferingBowl", offeringBowls.Count);

        LocationComponentCatalog catalog = new()
        {
            Prefab = prefabName,
            OfferingBowlPath = offeringBowls.Count > 0 ? GetRelativePath(locationRoot, offeringBowls[0].transform) : null,
            ItemStandPaths = itemStands
                .Where(itemStand => itemStand != null)
                .Select(itemStand => GetRelativePath(locationRoot, itemStand.transform))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList(),
            VegvisirPaths = vegvisirs
                .Where(vegvisir => vegvisir != null)
                .Select(vegvisir => GetRelativePath(locationRoot, vegvisir.transform))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList(),
            RunestonePaths = runestones
                .Where(runestone => runestone != null)
                .Select(runestone => GetRelativePath(locationRoot, runestone.transform))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList()
        };

        CatalogsByPrefab[prefabName] = catalog;
        return catalog;
    }

    private static void CaptureLiveLocationSnapshotIfNeeded(
        Location location,
        string prefabName,
        Transform locationRoot,
        IReadOnlyList<OfferingBowl> offeringBowls,
        IReadOnlyList<ItemStand> itemStands,
        IReadOnlyList<Vegvisir> vegvisirs,
        IReadOnlyList<RuneStone> runestones)
    {
        if (LiveLocationSnapshots.ContainsKey(location))
        {
            return;
        }

        LiveLocationSnapshots[location] = new LiveLocationSnapshot
        {
            Prefab = prefabName,
            OfferingBowl = offeringBowls.Count > 0 ? CapturePathScopedOfferingBowlSnapshot(locationRoot, offeringBowls[0]) : null,
            ItemStands = itemStands
                .Where(itemStand => itemStand != null)
                .Select(itemStand => CapturePathScopedItemStandSnapshot(locationRoot, itemStand))
                .OrderBy(snapshot => snapshot.Path, StringComparer.Ordinal)
                .ToList(),
            Vegvisirs = vegvisirs
                .Where(vegvisir => vegvisir != null)
                .Select(vegvisir => CaptureVegvisirSnapshot(locationRoot, vegvisir))
                .OrderBy(snapshot => snapshot.Path, StringComparer.Ordinal)
                .ToList(),
            Runestones = runestones
                .Where(runestone => runestone != null)
                .Select(runestone => CaptureRunestoneSnapshot(locationRoot, runestone))
                .OrderBy(snapshot => snapshot.Path, StringComparer.Ordinal)
                .ToList()
        };
    }

    private static void RestoreLiveLocationSnapshot(Location location, LocationRuntimeComponents runtimeComponents)
    {
        if (!LiveLocationSnapshots.TryGetValue(location, out LiveLocationSnapshot? snapshot))
        {
            return;
        }

        if (snapshot.OfferingBowl != null &&
            runtimeComponents.OfferingBowlsByPath.TryGetValue(snapshot.OfferingBowl.Path, out OfferingBowl? offeringBowl))
        {
            RestoreOfferingBowl(offeringBowl, snapshot.OfferingBowl.Snapshot);
        }

        foreach (PathScopedItemStandSnapshot itemStandSnapshot in snapshot.ItemStands)
        {
            if (runtimeComponents.ItemStandsByPath.TryGetValue(itemStandSnapshot.Path, out ItemStand? itemStand))
            {
                RestoreItemStand(itemStand, itemStandSnapshot.Snapshot);
            }
        }

        foreach (PathScopedVegvisirSnapshot vegvisirSnapshot in snapshot.Vegvisirs)
        {
            if (runtimeComponents.VegvisirsByPath.TryGetValue(vegvisirSnapshot.Path, out Vegvisir? vegvisir))
            {
                RestoreVegvisir(vegvisir, vegvisirSnapshot.Snapshot);
            }
        }

        foreach (PathScopedRunestoneSnapshot runestoneSnapshot in snapshot.Runestones)
        {
            if (runtimeComponents.RunestonesByPath.TryGetValue(runestoneSnapshot.Path, out RuneStone? runestone))
            {
                RestoreRunestone(runestone, runestoneSnapshot.Snapshot);
            }
        }
    }

    private static void RestoreLiveLocationSnapshot(
        Location location,
        Transform locationRoot,
        IReadOnlyList<OfferingBowl> offeringBowls,
        IReadOnlyList<ItemStand> itemStands,
        IReadOnlyList<Vegvisir> vegvisirs,
        IReadOnlyList<RuneStone> runestones)
    {
        if (!LiveLocationSnapshots.TryGetValue(location, out LiveLocationSnapshot? snapshot))
        {
            return;
        }

        Dictionary<string, OfferingBowl> liveOfferingBowlsByPath = BuildOfferingBowlLookup(locationRoot, offeringBowls);
        if (snapshot.OfferingBowl != null &&
            liveOfferingBowlsByPath.TryGetValue(snapshot.OfferingBowl.Path, out OfferingBowl? offeringBowl))
        {
            RestoreOfferingBowl(offeringBowl, snapshot.OfferingBowl.Snapshot);
        }

        Dictionary<string, ItemStand> liveItemStandsByPath = BuildItemStandLookup(locationRoot, itemStands);
        foreach (PathScopedItemStandSnapshot itemStandSnapshot in snapshot.ItemStands)
        {
            if (liveItemStandsByPath.TryGetValue(itemStandSnapshot.Path, out ItemStand? itemStand))
            {
                RestoreItemStand(itemStand, itemStandSnapshot.Snapshot);
            }
        }

        Dictionary<string, Vegvisir> liveVegvisirsByPath = BuildVegvisirLookup(locationRoot, vegvisirs);
        foreach (PathScopedVegvisirSnapshot vegvisirSnapshot in snapshot.Vegvisirs)
        {
            if (liveVegvisirsByPath.TryGetValue(vegvisirSnapshot.Path, out Vegvisir? vegvisir))
            {
                RestoreVegvisir(vegvisir, vegvisirSnapshot.Snapshot);
            }
        }

        Dictionary<string, RuneStone> liveRunestonesByPath = BuildRunestoneLookup(locationRoot, runestones);
        foreach (PathScopedRunestoneSnapshot runestoneSnapshot in snapshot.Runestones)
        {
            if (liveRunestonesByPath.TryGetValue(runestoneSnapshot.Path, out RuneStone? runestone))
            {
                RestoreRunestone(runestone, runestoneSnapshot.Snapshot);
            }
        }
    }

    private static PathScopedOfferingBowlSnapshot CapturePathScopedOfferingBowlSnapshot(Transform root, OfferingBowl offeringBowl)
    {
        return new PathScopedOfferingBowlSnapshot
        {
            Path = GetRelativePath(root, offeringBowl.transform),
            Snapshot = CaptureOfferingBowlSnapshot(offeringBowl)
        };
    }

    private static PathScopedItemStandSnapshot CapturePathScopedItemStandSnapshot(Transform root, ItemStand itemStand)
    {
        return new PathScopedItemStandSnapshot
        {
            Path = GetRelativePath(root, itemStand.transform),
            Snapshot = CaptureItemStandSnapshot(itemStand)
        };
    }

    private static List<ItemStand> GetRelevantLocationItemStands(OfferingBowl? offeringBowl, IEnumerable<ItemStand> childItemStands)
    {
        List<ItemStand> relevantItemStands = new();
        HashSet<int> seenIds = new();

        foreach (ItemStand itemStand in childItemStands)
        {
            if (itemStand != null && seenIds.Add(itemStand.GetInstanceID()))
            {
                relevantItemStands.Add(itemStand);
            }
        }

        if (offeringBowl == null || !offeringBowl.m_useItemStands)
        {
            return relevantItemStands;
        }

        foreach (ItemStand itemStand in AltarItemStandHoverInfoFormatter.FindRelevantItemStands(offeringBowl))
        {
            if (itemStand != null && seenIds.Add(itemStand.GetInstanceID()))
            {
                relevantItemStands.Add(itemStand);
            }
        }

        return relevantItemStands;
    }

    private static bool TryGetLocationPrefabName(Location location, out string prefabName)
    {
        prefabName = "";
        if (location == null)
        {
            return false;
        }

        LocationProxy? proxy = location.GetComponentInParent<LocationProxy>(true);
        if (proxy != null && TryGetRecordedLocationProxyPrefabName(proxy, out prefabName))
        {
            return true;
        }

        if (TryGetLocationPrefabNameWithoutProxy(location, out prefabName))
        {
            return true;
        }

        proxy = location.GetComponentInParent<LocationProxy>(true);
        return proxy != null && TryGetLocationProxyHashPrefabName(proxy, out prefabName);
    }

    private static bool TryGetLocationPrefabNameWithoutProxy(Location location, out string prefabName)
    {
        prefabName = "";
        if (location == null)
        {
            return false;
        }

        string livePrefabName = TrimCloneSuffix(location.gameObject.name);
        string liveRootPrefabName = TryGetLocationRootPrefabName(location);
        string zonePrefabName = "";
        if (ZoneSystem.instance != null)
        {
            Vector2i zone = ZoneSystem.GetZone(location.transform.position);
            if (ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance locationInstance))
            {
                zonePrefabName = GetZoneLocationPrefabName(locationInstance.m_location);
            }
        }

        if (ShouldPreferLiveLocationPrefabName(liveRootPrefabName, zonePrefabName))
        {
            prefabName = liveRootPrefabName;
            return true;
        }

        if (ShouldPreferLiveLocationPrefabName(livePrefabName, zonePrefabName))
        {
            prefabName = livePrefabName;
            return true;
        }

        if (zonePrefabName.Length > 0)
        {
            prefabName = zonePrefabName;
            return true;
        }

        prefabName = livePrefabName;
        return prefabName.Length > 0;
    }

    private static string TryGetLocationRootPrefabName(Location location)
    {
        if (location == null)
        {
            return "";
        }

        Transform? candidateRoot = null;
        LocationProxy? proxy = location.GetComponentInParent<LocationProxy>(true);
        if (proxy != null)
        {
            Transform? current = location.transform;
            while (current != null && current.parent != null)
            {
                if (ReferenceEquals(current.parent, proxy.transform))
                {
                    candidateRoot = current;
                    break;
                }

                current = current.parent;
            }
        }

        candidateRoot ??= GetRootTransform(location.transform);
        return candidateRoot != null ? TrimCloneSuffix(candidateRoot.gameObject.name) : "";
    }

    internal static bool TryResolveRuntimeLocationPrefabName(Location? location, out string prefabName)
    {
        if (location != null)
        {
            return TryGetLocationPrefabName(location, out prefabName);
        }

        prefabName = "";
        return false;
    }

    internal static bool TryResolveLocationProxyPrefabName(LocationProxy? proxy, out string prefabName)
    {
        prefabName = "";
        if (proxy == null)
        {
            return false;
        }

        if (TryGetRecordedLocationProxyPrefabName(proxy, out prefabName))
        {
            return true;
        }

        if (TryGetLocationProxyHashPrefabName(proxy, out prefabName))
        {
            return true;
        }

        Location? location = proxy.GetComponentInChildren<Location>(true);
        if (location is Location resolvedLocation &&
            TryGetLocationPrefabNameWithoutProxy(resolvedLocation, out prefabName))
        {
            ZNetView? nview = proxy.GetComponent<ZNetView>();
            ZDO? zdo = nview?.GetZDO();
            int locationHash = zdo?.GetInt(ZDOVars.s_location) ?? 0;
            if (locationHash != 0)
            {
                LocationPrefabNamesByHash[locationHash] = prefabName;
            }

            return true;
        }

        ZNetView? failedNview = proxy.GetComponent<ZNetView>();
        ZDO? failedZdo = failedNview?.GetZDO();
        int failedLocationHash = failedZdo?.GetInt(ZDOVars.s_location) ?? 0;
        if (failedLocationHash != 0)
        {
            LocationPrefabNamesByHash[failedLocationHash] = "";
        }

        lock (Sync)
        {
            QueueLocationProxyObservationInternal(proxy, Time.frameCount);
        }

        return false;
    }

    private static bool TryGetLocationProxyHashPrefabName(LocationProxy proxy, out string prefabName)
    {
        prefabName = "";
        if (proxy == null)
        {
            return false;
        }

        if (TryGetRecordedLocationProxyPrefabName(proxy, out prefabName))
        {
            return true;
        }

        ZNetView? nview = proxy.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        int locationHash = zdo?.GetInt(ZDOVars.s_location) ?? 0;
        if (locationHash != 0 &&
            LocationPrefabNamesByHash.TryGetValue(locationHash, out string? cachedPrefabName))
        {
            prefabName = cachedPrefabName ?? "";
            return prefabName.Length > 0;
        }

        if (locationHash != 0 && ZoneSystem.instance != null)
        {
            foreach (ZoneSystem.ZoneLocation zoneLocation in ZoneSystem.instance.m_locations)
            {
                string candidate = GetZoneLocationPrefabName(zoneLocation);
                if (candidate.Length == 0 || candidate.GetStableHashCode() != locationHash)
                {
                    continue;
                }

                prefabName = candidate;
                LocationPrefabNamesByHash[locationHash] = prefabName;
                return true;
            }
        }

        if (locationHash != 0)
        {
            LocationPrefabNamesByHash[locationHash] = "";
        }

        if (locationHash != 0)
        {
            return false;
        }

        return false;
    }

    private static bool TryGetRecordedLocationProxyPrefabName(LocationProxy? proxy, out string prefabName)
    {
        prefabName = "";
        if (proxy == null)
        {
            return false;
        }

        ZNetView? nview = proxy.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (zdo != null)
        {
            string zdoPrefabName = (zdo.GetString(LocationProxyResolvedPrefabZdoKey, "") ?? "").Trim();
            if (zdoPrefabName.Length > 0)
            {
                CacheLocationProxyResolvedPrefabInternal(
                    proxy,
                    zdoPrefabName,
                    persistToZdo: false,
                    queueLocationReconciles: false);
                prefabName = zdoPrefabName;
                return true;
            }

            ZDOID zdoId = zdo.m_uid;
            if (zdoId != ZDOID.None &&
                RuntimeLocationProxyPrefabsByZdoId.TryGetValue(zdoId, out string? zdoCachedPrefabName) &&
                !string.IsNullOrWhiteSpace(zdoCachedPrefabName))
            {
                RuntimeLocationProxyPrefabsByInstance[proxy] = zdoCachedPrefabName;
                prefabName = zdoCachedPrefabName;
                return true;
            }
        }

        if (RuntimeLocationProxyPrefabsByInstance.TryGetValue(proxy, out string? cachedPrefabName))
        {
            prefabName = cachedPrefabName ?? "";
            return prefabName.Length > 0;
        }

        return false;
    }

    internal static bool TryResolveZoneLocationPrefabName(Vector3 position, out string prefabName)
    {
        prefabName = "";
        if (ZoneSystem.instance == null)
        {
            return false;
        }

        Vector2i zone = ZoneSystem.GetZone(position);
        if (!ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance locationInstance))
        {
            return false;
        }

        string candidate = GetZoneLocationPrefabName(locationInstance.m_location);
        if (candidate.Length == 0)
        {
            return false;
        }

        prefabName = candidate;
        return true;
    }

    private static string GetZoneLocationPrefabName(ZoneSystem.ZoneLocation? location)
    {
        return (location?.m_prefabName ?? location?.m_prefab.Name ?? "").Trim();
    }


    private static bool ShouldPreferLiveLocationPrefabName(string livePrefabName, string zonePrefabName)
    {
        if (livePrefabName.Length == 0 ||
            string.Equals(livePrefabName, zonePrefabName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (zonePrefabName.Length > 0 &&
            !livePrefabName.Contains(':') &&
            (zonePrefabName.Contains(':') || string.Equals(GetLocationPrefabBaseName(zonePrefabName), livePrefabName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return livePrefabName.Contains(':') ||
               ActiveEntriesByPrefab.ContainsKey(livePrefabName) ||
               CatalogsByPrefab.ContainsKey(livePrefabName) ||
               LooseItemStandEntriesByPrefab.ContainsKey(livePrefabName);
    }

    private static string GetLocationPrefabBaseName(string prefabName)
    {
        string trimmedName = (prefabName ?? "").Trim();
        int separatorIndex = trimmedName.IndexOf(':');
        return separatorIndex >= 0 ? trimmedName.Substring(0, separatorIndex) : trimmedName;
    }

    private static void RestoreOfferingBowl(OfferingBowl offeringBowl, OfferingBowlSnapshot snapshot)
    {
        offeringBowl.m_name = snapshot.Name;
        offeringBowl.m_useItemText = snapshot.UseItemText;
        offeringBowl.m_usedAltarText = snapshot.UsedAltarText;
        offeringBowl.m_cantOfferText = snapshot.CantOfferText;
        offeringBowl.m_wrongOfferText = snapshot.WrongOfferText;
        offeringBowl.m_incompleteOfferText = snapshot.IncompleteOfferText;
        offeringBowl.m_bossItem = ResolveItemDrop(snapshot.BossItem, null);
        offeringBowl.m_bossItems = Math.Max(1, snapshot.BossItems);
        offeringBowl.m_bossPrefab = ResolveSpawnPrefab(snapshot.BossPrefab, null);
        offeringBowl.m_itemPrefab = ResolveItemDrop(snapshot.ItemPrefab, null);
        offeringBowl.m_setGlobalKey = snapshot.SetGlobalKey;
        offeringBowl.m_renderSpawnAreaGizmos = snapshot.RenderSpawnAreaGizmos;
        offeringBowl.m_alertOnSpawn = snapshot.AlertOnSpawn;
        offeringBowl.m_spawnBossDelay = snapshot.SpawnBossDelay;
        offeringBowl.m_spawnBossMaxDistance = snapshot.SpawnBossMaxDistance;
        offeringBowl.m_spawnBossMinDistance = snapshot.SpawnBossMinDistance;
        offeringBowl.m_spawnBossMaxYDistance = snapshot.SpawnBossMaxYDistance;
        offeringBowl.m_getSolidHeightMargin = snapshot.GetSolidHeightMargin;
        offeringBowl.m_enableSolidHeightCheck = snapshot.EnableSolidHeightCheck;
        offeringBowl.m_spawnPointClearingRadius = snapshot.SpawnPointClearingRadius;
        offeringBowl.m_spawnYOffset = snapshot.SpawnYOffset;
        offeringBowl.m_useItemStands = snapshot.UseItemStands;
        offeringBowl.m_itemStandPrefix = snapshot.ItemStandPrefix;
        offeringBowl.m_itemstandMaxRange = snapshot.ItemStandMaxRange;
        OfferingBowlRuntimeState state = GetOrAddOfferingBowlRuntimeState(offeringBowl);
        state.RespawnMinutes = 0f;
    }

    private static void RestoreItemStand(ItemStand itemStand, ItemStandSnapshot snapshot)
    {
        itemStand.m_name = snapshot.Name;
        itemStand.m_canBeRemoved = snapshot.CanBeRemoved;
        itemStand.m_autoAttach = snapshot.AutoAttach;
        if (Enum.TryParse(snapshot.OrientationType, true, out ItemStand.Orientation orientation))
        {
            itemStand.m_orientationType = orientation;
        }

        itemStand.m_supportedTypes = snapshot.SupportedTypes
            .Select(ParseItemStandType)
            .Where(type => type.HasValue)
            .Select(type => type!.Value)
            .ToList();
        itemStand.m_supportedItems = ResolveItemDropList(snapshot.SupportedItems, null);
        itemStand.m_unsupportedItems = ResolveItemDropList(snapshot.UnsupportedItems, null);
        itemStand.m_powerActivationDelay = snapshot.PowerActivationDelay;
        itemStand.m_guardianPower = ResolveStatusEffect(snapshot.GuardianPower, null);
    }

    private static void ApplyOfferingBowl(OfferingBowl offeringBowl, LocationOfferingBowlDefinition entry, string prefabName)
    {
        string context = $"{prefabName}@offeringBowl";

        if (entry.Name != null)
        {
            offeringBowl.m_name = entry.Name;
        }

        if (entry.UseItemText != null)
        {
            offeringBowl.m_useItemText = entry.UseItemText;
        }

        if (entry.UsedAltarText != null)
        {
            offeringBowl.m_usedAltarText = entry.UsedAltarText;
        }

        if (entry.CantOfferText != null)
        {
            offeringBowl.m_cantOfferText = entry.CantOfferText;
        }

        if (entry.WrongOfferText != null)
        {
            offeringBowl.m_wrongOfferText = entry.WrongOfferText;
        }

        if (entry.IncompleteOfferText != null)
        {
            offeringBowl.m_incompleteOfferText = entry.IncompleteOfferText;
        }

        if (entry.BossItem != null)
        {
            offeringBowl.m_bossItem = ResolveItemDrop(entry.BossItem, $"{context}/bossItem");
        }

        if (entry.BossItems.HasValue)
        {
            offeringBowl.m_bossItems = Math.Max(1, entry.BossItems.Value);
        }

        if (entry.BossPrefab != null)
        {
            offeringBowl.m_bossPrefab = ResolveSpawnPrefab(entry.BossPrefab, $"{context}/bossPrefab");
        }

        if (entry.ItemPrefab != null)
        {
            offeringBowl.m_itemPrefab = ResolveItemDrop(entry.ItemPrefab, $"{context}/itemPrefab");
        }

        if (entry.SetGlobalKey != null)
        {
            offeringBowl.m_setGlobalKey = entry.SetGlobalKey;
        }

        if (entry.RenderSpawnAreaGizmos.HasValue)
        {
            offeringBowl.m_renderSpawnAreaGizmos = entry.RenderSpawnAreaGizmos.Value;
        }

        if (entry.AlertOnSpawn.HasValue)
        {
            offeringBowl.m_alertOnSpawn = entry.AlertOnSpawn.Value;
        }

        if (entry.SpawnBossDelay.HasValue)
        {
            offeringBowl.m_spawnBossDelay = Mathf.Max(0f, entry.SpawnBossDelay.Value);
        }

        if (entry.SpawnBossMaxDistance.HasValue)
        {
            offeringBowl.m_spawnBossMaxDistance = Mathf.Max(0f, entry.SpawnBossMaxDistance.Value);
        }

        if (entry.SpawnBossMinDistance.HasValue)
        {
            offeringBowl.m_spawnBossMinDistance = Mathf.Max(0f, entry.SpawnBossMinDistance.Value);
        }

        if (entry.SpawnBossMaxYDistance.HasValue)
        {
            offeringBowl.m_spawnBossMaxYDistance = Mathf.Max(0f, entry.SpawnBossMaxYDistance.Value);
        }

        if (entry.GetSolidHeightMargin.HasValue)
        {
            offeringBowl.m_getSolidHeightMargin = Math.Max(0, entry.GetSolidHeightMargin.Value);
        }

        if (entry.EnableSolidHeightCheck.HasValue)
        {
            offeringBowl.m_enableSolidHeightCheck = entry.EnableSolidHeightCheck.Value;
        }

        if (entry.SpawnPointClearingRadius.HasValue)
        {
            offeringBowl.m_spawnPointClearingRadius = Mathf.Max(0f, entry.SpawnPointClearingRadius.Value);
        }

        if (entry.SpawnYOffset.HasValue)
        {
            offeringBowl.m_spawnYOffset = entry.SpawnYOffset.Value;
        }

        if (entry.UseItemStands.HasValue)
        {
            offeringBowl.m_useItemStands = entry.UseItemStands.Value;
        }

        if (entry.ItemStandPrefix != null)
        {
            offeringBowl.m_itemStandPrefix = entry.ItemStandPrefix;
        }

        if (entry.ItemStandMaxRange.HasValue)
        {
            offeringBowl.m_itemstandMaxRange = Mathf.Max(0f, entry.ItemStandMaxRange.Value);
        }

        OfferingBowlRuntimeState state = GetOrAddOfferingBowlRuntimeState(offeringBowl);
        state.RespawnMinutes = entry.RespawnMinutes.HasValue
            ? Mathf.Max(0f, entry.RespawnMinutes.Value)
            : 0f;
        if (offeringBowl.m_bossPrefab != null)
        {
            state.SpawnPayload = ExpandWorldSpawnDataSupport.BuildPayload(
                offeringBowl.m_bossPrefab,
                entry.Data,
                entry.Fields,
                entry.Objects,
                context);
        }
        else
        {
            state.SpawnPayload = null;
            if (entry.Data != null || entry.Fields != null || entry.Objects != null)
            {
                WarnInvalidEntry($"Entry '{context}' configured offeringBowl data/fields/objects, but no bossPrefab is available. Those fields were ignored.");
            }
        }

    }

    private static void ApplyItemStand(ItemStand itemStand, LocationItemStandDefinition entry, string prefabName, Transform locationRoot)
    {
        string context = string.IsNullOrWhiteSpace(entry.Path)
            ? $"{prefabName}@itemStands"
            : $"{prefabName}@itemStands[{entry.Path}]";
        List<ItemDrop>? resolvedSupportedItems = null;

        if (entry.Name != null)
        {
            itemStand.m_name = entry.Name;
        }

        if (entry.CanBeRemoved.HasValue)
        {
            itemStand.m_canBeRemoved = entry.CanBeRemoved.Value;
        }

        if (entry.AutoAttach.HasValue)
        {
            itemStand.m_autoAttach = entry.AutoAttach.Value;
        }

        if (entry.OrientationType != null)
        {
            ItemStand.Orientation? orientation = ParseItemStandOrientation(entry.OrientationType, $"{context}/orientationType");
            if (orientation.HasValue)
            {
                itemStand.m_orientationType = orientation.Value;
            }
        }

        if (entry.SupportedTypes != null)
        {
            itemStand.m_supportedTypes = ResolveItemStandTypes(entry.SupportedTypes, $"{context}/supportedTypes");
        }

        if (entry.SupportedItems != null)
        {
            resolvedSupportedItems = ResolveItemDropList(entry.SupportedItems, $"{context}/supportedItems");
            itemStand.m_supportedItems = resolvedSupportedItems;
            WarnAboutNonAttachableItemStandItems(resolvedSupportedItems, $"{context}/supportedItems");
        }

        if (entry.UnsupportedItems != null)
        {
            itemStand.m_unsupportedItems = ResolveItemDropList(entry.UnsupportedItems, $"{context}/unsupportedItems");
        }
        else if (resolvedSupportedItems != null)
        {
            RemoveSupportedItemsFromUnsupportedList(itemStand, resolvedSupportedItems);
        }

        if (entry.PowerActivationDelay.HasValue)
        {
            itemStand.m_powerActivationDelay = Mathf.Max(0f, entry.PowerActivationDelay.Value);
        }

        if (entry.GuardianPower != null)
        {
            itemStand.m_guardianPower = ResolveStatusEffect(entry.GuardianPower, $"{context}/guardianPower");
        }

    }

    private static void RemoveSupportedItemsFromUnsupportedList(ItemStand itemStand, List<ItemDrop> supportedItems)
    {
        if (itemStand.m_unsupportedItems == null || itemStand.m_unsupportedItems.Count == 0 || supportedItems.Count == 0)
        {
            return;
        }

        HashSet<string> supportedNames = supportedItems
            .Select(GetItemSharedName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (supportedNames.Count == 0)
        {
            return;
        }

        itemStand.m_unsupportedItems = itemStand.m_unsupportedItems
            .Where(item => !supportedNames.Contains(GetItemSharedName(item)))
            .ToList();
    }

    private static void WarnAboutNonAttachableItemStandItems(List<ItemDrop> supportedItems, string warnContext)
    {
        foreach (ItemDrop itemDrop in supportedItems)
        {
            if (itemDrop == null || ItemStand.GetAttachPrefab(itemDrop.gameObject) != null)
            {
                continue;
            }

            string prefabName = NormalizeReferencePrefabName(itemDrop.gameObject) ?? itemDrop.gameObject.name ?? "(unknown item)";
            WarnInvalidEntry($"Entry '{warnContext}' references '{prefabName}', but that item has no ItemStand attach prefab and cannot be placed on an ItemStand.");
        }
    }

    private static string GetItemSharedName(ItemDrop? itemDrop)
    {
        return itemDrop?.m_itemData?.m_shared?.m_name ?? "";
    }

    private static void LogLocationReconcileCandidate(
        Location location,
        string prefabName,
        OfferingBowl? offeringBowl,
        IReadOnlyList<ItemStand> itemStands,
        int liveVegvisirCount,
        int liveRunestoneCount,
        LocationComponentCatalog catalog,
        LiveLocationSnapshot snapshot)
    {
        if (!ActiveEntriesByPrefab.ContainsKey(prefabName))
        {
            return;
        }

        string key = $"reconcile|{prefabName}|{location.transform.position}";
        if (!LocationDiagnosticLogs.Add(key))
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogDebug(
            $"Reconciling live location: prefab='{prefabName}', object='{location.gameObject.name}', itemStandCount={itemStands.Count}, catalogItemStandCount={catalog.ItemStandPaths.Count}, snapshotItemStandCount={snapshot.ItemStands.Count}, hasOfferingBowl={offeringBowl != null}, catalogHasOfferingBowl={catalog.OfferingBowlPath != null}, liveVegvisirCount={liveVegvisirCount}, catalogVegvisirCount={catalog.VegvisirPaths.Count}, snapshotVegvisirCount={snapshot.Vegvisirs.Count}, liveRunestoneCount={liveRunestoneCount}, catalogRunestoneCount={catalog.RunestonePaths.Count}, snapshotRunestoneCount={snapshot.Runestones.Count}.");
    }

    private static string JoinDiagnosticValues(IEnumerable<string> values)
    {
        List<string> materialized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return materialized.Count == 0 ? "(none)" : string.Join(", ", materialized);
    }

    private static Dictionary<string, OfferingBowl> BuildOfferingBowlLookup(Transform locationRoot, IEnumerable<OfferingBowl> offeringBowls)
    {
        Dictionary<string, OfferingBowl> lookup = new(StringComparer.Ordinal);
        foreach (OfferingBowl offeringBowl in offeringBowls)
        {
            if (offeringBowl == null)
            {
                continue;
            }

            lookup[GetRelativePath(locationRoot, offeringBowl.transform)] = offeringBowl;
        }

        return lookup;
    }

    private static Dictionary<string, ItemStand> BuildItemStandLookup(Transform locationRoot, IEnumerable<ItemStand> itemStands)
    {
        Dictionary<string, ItemStand> lookup = new(StringComparer.Ordinal);
        foreach (ItemStand itemStand in itemStands)
        {
            if (itemStand == null)
            {
                continue;
            }

            lookup[GetRelativePath(locationRoot, itemStand.transform)] = itemStand;
        }

        return lookup;
    }

    private static void ApplyConfiguredItemStands(
        IReadOnlyList<CompiledLocationItemStandPlan> definitions,
        List<ItemStand> relevantItemStands,
        Dictionary<string, ItemStand> liveItemStandsByPath,
        string prefabName,
        Transform locationRoot,
        OfferingBowl? offeringBowl)
    {
        HashSet<int> exactMatchedItemStandIds = new();
        List<LocationItemStandDefinition> unresolvedPathDefinitions = new();

        foreach (CompiledLocationItemStandPlan itemStandPlan in definitions)
        {
            LocationItemStandDefinition definition = itemStandPlan.Definition;

            if (!itemStandPlan.HasPath)
            {
                foreach (ItemStand itemStand in relevantItemStands)
                {
                    if (itemStand.GetComponentInParent<Location>() == null)
                    {
                        CaptureLooseItemStandSnapshotIfNeeded(itemStand, prefabName);
                    }

                    ApplyItemStand(itemStand, definition, prefabName, locationRoot);
                }

                continue;
            }

            string path = itemStandPlan.Path;
            if (!liveItemStandsByPath.TryGetValue(path, out ItemStand? itemStandByPath))
            {
                unresolvedPathDefinitions.Add(definition);
                continue;
            }

            exactMatchedItemStandIds.Add(itemStandByPath.GetInstanceID());
            CaptureAuthoredItemStandSlot(prefabName, path, itemStandByPath, offeringBowl);
            ApplyItemStand(itemStandByPath, definition, prefabName, locationRoot);
        }

        if (unresolvedPathDefinitions.Count == 0)
        {
            return;
        }

        if (offeringBowl == null)
        {
            foreach (LocationItemStandDefinition unresolvedDefinition in unresolvedPathDefinitions)
            {
                string unresolvedPath = (unresolvedDefinition.Path ?? "").Trim();
                WarnMissingItemStandPath(prefabName, unresolvedPath);
            }

            return;
        }

        List<ItemStand> unmatchedRelevantItemStands = relevantItemStands
            .Where(itemStand => itemStand != null && !exactMatchedItemStandIds.Contains(itemStand.GetInstanceID()))
            .ToList();
        if (unmatchedRelevantItemStands.Count == 0)
        {
            foreach (LocationItemStandDefinition unresolvedDefinition in unresolvedPathDefinitions)
            {
                string unresolvedPath = (unresolvedDefinition.Path ?? "").Trim();
                WarnMissingItemStandPath(prefabName, unresolvedPath);
            }

            return;
        }

        foreach (ItemStand unmatchedItemStand in unmatchedRelevantItemStands)
        {
            LooseItemStandAuthoredPathsByInstance.Remove(unmatchedItemStand);
        }

        TryStampLooseItemStandAuthoredPaths(offeringBowl, prefabName, unmatchedRelevantItemStands);
        foreach (LocationItemStandDefinition unresolvedDefinition in unresolvedPathDefinitions)
        {
            string unresolvedPath = (unresolvedDefinition.Path ?? "").Trim();
            ItemStand? mappedItemStand = unmatchedRelevantItemStands.FirstOrDefault(itemStand =>
                LooseItemStandAuthoredPathsByInstance.TryGetValue(itemStand, out string? authoredPath) &&
                string.Equals(authoredPath, unresolvedPath, StringComparison.Ordinal));
            if (mappedItemStand == null)
            {
                WarnMissingItemStandPath(prefabName, unresolvedPath);
                continue;
            }

            ApplyItemStand(mappedItemStand, unresolvedDefinition, prefabName, locationRoot);
        }
    }

    private static void CaptureAuthoredItemStandSlot(string prefabName, string configuredPath, ItemStand itemStand, OfferingBowl? offeringBowl)
    {
        if (offeringBowl == null)
        {
            return;
        }

        string trimmedPath = (configuredPath ?? "").Trim();
        if (trimmedPath.Length == 0)
        {
            return;
        }

        if (!AuthoredItemStandSlotsByPrefab.TryGetValue(prefabName, out List<AuthoredItemStandSlotTemplate>? slots))
        {
            slots = new List<AuthoredItemStandSlotTemplate>();
            AuthoredItemStandSlotsByPrefab[prefabName] = slots;
        }

        Vector3 offset = offeringBowl.transform.InverseTransformPoint(itemStand.transform.position);
        int existingIndex = slots.FindIndex(slot => string.Equals(slot.Path, trimmedPath, StringComparison.Ordinal));
        AuthoredItemStandSlotTemplate slotTemplate = new()
        {
            Path = trimmedPath,
            OfferingBowlLocalOffset = offset
        };
        if (existingIndex >= 0)
        {
            slots[existingIndex] = slotTemplate;
        }
        else
        {
            slots.Add(slotTemplate);
        }
    }

    private static Dictionary<string, Vegvisir> BuildVegvisirLookup(Transform locationRoot, IEnumerable<Vegvisir> vegvisirs)
    {
        Dictionary<string, Vegvisir> lookup = new(StringComparer.Ordinal);
        foreach (Vegvisir vegvisir in vegvisirs)
        {
            if (vegvisir == null)
            {
                continue;
            }

            lookup[GetRelativePath(locationRoot, vegvisir.transform)] = vegvisir;
        }

        return lookup;
    }

    private static Dictionary<string, RuneStone> BuildRunestoneLookup(Transform locationRoot, IEnumerable<RuneStone> runestones)
    {
        Dictionary<string, RuneStone> lookup = new(StringComparer.Ordinal);
        foreach (RuneStone runestone in runestones)
        {
            if (runestone == null)
            {
                continue;
            }

            lookup[GetRelativePath(locationRoot, runestone.transform)] = runestone;
        }

        return lookup;
    }

    private static bool TryResolveVegvisirTarget(
        string prefabName,
        LocationVegvisirDefinition entry,
        Dictionary<string, Vegvisir> liveVegvisirsByPath,
        out Vegvisir resolvedVegvisir)
    {
        resolvedVegvisir = null!;

        string path = entry.Path ?? "";
        if (path.Length > 0)
        {
            if (!liveVegvisirsByPath.TryGetValue(path, out Vegvisir? vegvisir))
            {
                WarnMissingVegvisirPath(prefabName, path);
                return false;
            }

            if (!MatchesExpectedVegvisirLocations(vegvisir, entry.ExpectedLocations))
            {
                WarnUnexpectedVegvisirTargets(prefabName, path, vegvisir, entry.ExpectedLocations);
                return false;
            }

            resolvedVegvisir = vegvisir;
            return true;
        }

        List<KeyValuePair<string, Vegvisir>> candidates = liveVegvisirsByPath
            .Where(pair => MatchesExpectedVegvisirLocations(pair.Value, entry.ExpectedLocations))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 1)
        {
            resolvedVegvisir = candidates[0].Value;
            return true;
        }

        if (candidates.Count == 0)
        {
            WarnUnresolvedVegvisirTarget(prefabName, entry.ExpectedLocations, liveVegvisirsByPath.Keys);
            return false;
        }

        WarnAmbiguousVegvisirTarget(prefabName, entry.ExpectedLocations, candidates.Select(pair => pair.Key));
        return false;
    }

    private static bool TryResolveRunestoneTarget(
        string prefabName,
        LocationRunestoneDefinition entry,
        Dictionary<string, RuneStone> liveRunestonesByPath,
        out RuneStone resolvedRunestone)
    {
        resolvedRunestone = null!;

        string path = entry.Path ?? "";
        if (path.Length > 0)
        {
            if (!liveRunestonesByPath.TryGetValue(path, out RuneStone? runestone))
            {
                WarnMissingRunestonePath(prefabName, path);
                return false;
            }

            if (!MatchesExpectedRunestone(runestone, entry))
            {
                WarnUnexpectedRunestoneTarget(prefabName, path, runestone, entry);
                return false;
            }

            resolvedRunestone = runestone;
            return true;
        }

        List<KeyValuePair<string, RuneStone>> candidates = liveRunestonesByPath
            .Where(pair => MatchesExpectedRunestone(pair.Value, entry))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 1)
        {
            resolvedRunestone = candidates[0].Value;
            return true;
        }

        if (candidates.Count == 0)
        {
            WarnUnresolvedRunestoneTarget(prefabName, entry, liveRunestonesByPath.Keys);
            return false;
        }

        WarnAmbiguousRunestoneTarget(prefabName, entry, candidates.Select(pair => pair.Key));
        return false;
    }

    private static bool MatchesExpectedVegvisirLocations(Vegvisir vegvisir, List<string>? expectedLocations)
    {
        if (expectedLocations == null || expectedLocations.Count == 0)
        {
            return true;
        }

        List<string> normalizedExpected = expectedLocations
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> normalizedActual = vegvisir.m_locations
            .Select(location => (location.m_locationName ?? "").Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalizedExpected.SequenceEqual(normalizedActual, StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesExpectedRunestone(RuneStone runestone, LocationRunestoneDefinition entry)
    {
        return MatchesExpectedRunestoneValue(entry.ExpectedLocationName, runestone.m_locationName) &&
               MatchesExpectedRunestoneValue(entry.ExpectedLabel, runestone.m_label) &&
               MatchesExpectedRunestoneValue(entry.ExpectedTopic, runestone.m_topic);
    }

    private static bool MatchesExpectedRunestoneValue(string? expected, string? actual)
    {
        string expectedValue = (expected ?? "").Trim();
        if (expectedValue.Length == 0)
        {
            return true;
        }

        return string.Equals(expectedValue, (actual ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
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

    private static Transform GetRootTransform(Transform transform)
    {
        Transform current = transform;
        while (current.parent != null)
        {
            current = current.parent;
        }

        return current;
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
                return index;
            }

            if (string.Equals(sibling.name, transform.name, StringComparison.Ordinal))
            {
                index++;
            }
        }

        return index;
    }

    private static void RestoreVegvisir(Vegvisir vegvisir, VegvisirSnapshot snapshot)
    {
        vegvisir.m_name = snapshot.Name;
        vegvisir.m_useText = snapshot.UseText;
        vegvisir.m_hoverName = snapshot.HoverName;
        vegvisir.m_setsGlobalKey = snapshot.SetsGlobalKey;
        vegvisir.m_setsPlayerKey = snapshot.SetsPlayerKey;
        vegvisir.m_locations = snapshot.Locations
            .Select(CreateVegvisirLocation)
            .ToList();
    }

    private static void RestoreRunestone(RuneStone runestone, RunestoneSnapshot snapshot)
    {
        ClearRunestonePinChanceRoll(runestone);
        runestone.m_name = snapshot.Name;
        runestone.m_topic = snapshot.Topic;
        runestone.m_label = snapshot.Label;
        runestone.m_text = snapshot.Text;
        runestone.m_randomTexts = snapshot.RandomTexts
            .Select(CreateRunestoneText)
            .ToList();
        runestone.m_locationName = snapshot.LocationName;
        runestone.m_pinName = snapshot.PinName;
        runestone.m_pinType = ParsePinType(snapshot.PinType, null) ?? Minimap.PinType.Boss;
        runestone.m_showMap = snapshot.ShowMap;
    }

    private static void ApplyVegvisir(Vegvisir vegvisir, LocationVegvisirDefinition entry, string prefabName)
    {
        string context = $"{prefabName}@vegvisirs[{entry.Path}]";

        if (entry.Name != null)
        {
            vegvisir.m_name = entry.Name;
        }

        if (entry.UseText != null)
        {
            vegvisir.m_useText = entry.UseText;
        }

        if (entry.HoverName != null)
        {
            vegvisir.m_hoverName = entry.HoverName;
        }

        if (entry.SetsGlobalKey != null)
        {
            vegvisir.m_setsGlobalKey = entry.SetsGlobalKey;
        }

        if (entry.SetsPlayerKey != null)
        {
            vegvisir.m_setsPlayerKey = entry.SetsPlayerKey;
        }

        if (entry.Locations != null)
        {
            if (entry.Locations.Any(location => location.Weight.HasValue))
            {
                Vegvisir.VegvisrLocation? weightedTarget = SelectWeightedVegvisirLocation(entry.Locations, context);
                vegvisir.m_locations = weightedTarget != null
                    ? new List<Vegvisir.VegvisrLocation> { weightedTarget }
                    : new List<Vegvisir.VegvisrLocation>();
                return;
            }

            List<Vegvisir.VegvisrLocation> targets = new();
            for (int index = 0; index < entry.Locations.Count; index++)
            {
                LocationVegvisirTargetDefinition definition = entry.Locations[index];
                Vegvisir.VegvisrLocation? target = CreateVegvisirLocation(definition, $"{context}/locations[{index}]");
                if (target != null)
                {
                    targets.Add(target);
                }
            }

            vegvisir.m_locations = targets;
        }
    }

    private static void ApplyRunestone(RuneStone runestone, LocationRunestoneDefinition entry, string prefabName)
    {
        string context = $"{prefabName}@runestones[{entry.Path}]";

        if (entry.Name != null)
        {
            runestone.m_name = entry.Name;
        }

        if (entry.Topic != null)
        {
            runestone.m_topic = entry.Topic;
        }

        if (entry.Label != null)
        {
            runestone.m_label = entry.Label;
        }

        if (entry.Text != null)
        {
            runestone.m_text = entry.Text;
        }

        if (entry.RandomTexts != null)
        {
            runestone.m_randomTexts = entry.RandomTexts
                .Select(CreateRunestoneText)
                .ToList();
        }

        if (entry.LocationName != null)
        {
            runestone.m_locationName = entry.LocationName;
        }

        if (entry.PinName != null)
        {
            runestone.m_pinName = entry.PinName;
        }

        if (entry.PinType != null)
        {
            Minimap.PinType? pinType = ParsePinType(entry.PinType, context);
            if (pinType.HasValue)
            {
                runestone.m_pinType = pinType.Value;
            }
        }

        if (entry.ShowMap.HasValue)
        {
            runestone.m_showMap = entry.ShowMap.Value;
        }

        if (entry.Chance.HasValue)
        {
            ApplyRunestonePinChanceRoll(runestone, entry, prefabName);
        }
    }

    internal static bool ShouldSuppressRunestonePinDiscovery(RuneStone? runestone)
    {
        return runestone != null &&
               RunestonePinChanceRolls.TryGetValue(runestone, out RunestonePinChanceState state) &&
               !state.AllowsPin;
    }

    private static void ApplyRunestonePinChanceRoll(
        RuneStone runestone,
        LocationRunestoneDefinition entry,
        string prefabName)
    {
        float chance = Mathf.Clamp01(entry.Chance ?? 1f);
        if (chance >= 1f)
        {
            ClearRunestonePinChanceRoll(runestone);
            return;
        }

        string rollKey = CreateRunestonePinChanceRollKey(runestone, entry, prefabName, chance);
        lock (RunestonePinChanceLock)
        {
            if (!RunestonePinChanceRolls.TryGetValue(runestone, out RunestonePinChanceState state))
            {
                state = new RunestonePinChanceState();
                RunestonePinChanceRolls.Add(runestone, state);
            }

            if (state.RollKey == rollKey)
            {
                return;
            }

            state.RollKey = rollKey;
            state.AllowsPin = chance > 0f && RunestonePinChanceRandom.NextDouble() <= chance;
        }
    }

    private static string CreateRunestonePinChanceRollKey(
        RuneStone runestone,
        LocationRunestoneDefinition entry,
        string prefabName,
        float chance)
    {
        return string.Join(
            "\n",
            prefabName,
            entry.Path ?? "",
            runestone.m_locationName ?? "",
            runestone.m_pinName ?? "",
            runestone.m_pinType.ToString(),
            runestone.m_showMap ? "true" : "false",
            chance.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void ClearRunestonePinChanceRoll(RuneStone? runestone)
    {
        if (runestone == null)
        {
            return;
        }

        RunestonePinChanceRolls.Remove(runestone);
    }

    private static RuneStone.RandomRuneText CreateRunestoneText(LocationRunestoneTextDefinition definition)
    {
        return new RuneStone.RandomRuneText
        {
            m_topic = definition.Topic ?? "",
            m_label = definition.Label ?? "",
            m_text = definition.Text ?? ""
        };
    }

    private static RuneStone.RandomRuneText CreateRunestoneText(RunestoneTextSnapshot snapshot)
    {
        return new RuneStone.RandomRuneText
        {
            m_topic = snapshot.Topic,
            m_label = snapshot.Label,
            m_text = snapshot.Text
        };
    }

    private static Vegvisir.VegvisrLocation? SelectWeightedVegvisirLocation(
        List<LocationVegvisirTargetDefinition> definitions,
        string context)
    {
        List<(Vegvisir.VegvisrLocation Target, float Weight)> weightedTargets = new();
        float totalWeight = 0f;

        for (int index = 0; index < definitions.Count; index++)
        {
            LocationVegvisirTargetDefinition definition = definitions[index];
            Vegvisir.VegvisrLocation? target = CreateVegvisirLocation(definition, $"{context}/locations[{index}]");
            if (target == null)
            {
                continue;
            }

            float weight = Mathf.Max(0f, definition.Weight ?? 1f);
            if (weight <= 0f)
            {
                continue;
            }

            weightedTargets.Add((target, weight));
            totalWeight += weight;
        }

        if (weightedTargets.Count == 0 || totalWeight <= 0f)
        {
            return null;
        }

        float roll = UnityEngine.Random.Range(0f, totalWeight);
        float cumulativeWeight = 0f;
        foreach ((Vegvisir.VegvisrLocation target, float weight) in weightedTargets)
        {
            cumulativeWeight += weight;
            if (roll <= cumulativeWeight)
            {
                return target;
            }
        }

        return weightedTargets[weightedTargets.Count - 1].Target;
    }

    private static Vegvisir.VegvisrLocation CreateVegvisirLocation(VegvisirTargetSnapshot snapshot)
    {
        Minimap.PinType pinType = ParsePinType(snapshot.PinType, null) ?? Minimap.PinType.Icon0;
        return new Vegvisir.VegvisrLocation
        {
            m_locationName = snapshot.LocationName,
            m_pinName = snapshot.PinName,
            m_pinType = pinType,
            m_discoverAll = snapshot.DiscoverAll,
            m_showMap = snapshot.ShowMap
        };
    }

    private static Vegvisir.VegvisrLocation? CreateVegvisirLocation(LocationVegvisirTargetDefinition definition, string warnContext)
    {
        string locationName = (definition.LocationName ?? "").Trim();
        if (locationName.Length == 0)
        {
            WarnInvalidEntry($"Entry '{warnContext}' is missing locationName.");
            return null;
        }

        Minimap.PinType pinType = ParsePinType(definition.PinType, warnContext) ?? Minimap.PinType.Icon0;
        return new Vegvisir.VegvisrLocation
        {
            m_locationName = locationName,
            m_pinName = string.IsNullOrWhiteSpace(definition.PinName) ? "Pin" : definition.PinName!,
            m_pinType = pinType,
            m_discoverAll = definition.DiscoverAll ?? false,
            m_showMap = definition.ShowMap ?? true
        };
    }

    private static Minimap.PinType? ParsePinType(string? pinTypeName, string? warnContext)
    {
        string trimmedName = (pinTypeName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            return null;
        }

        if (Enum.TryParse(trimmedName, true, out Minimap.PinType pinType))
        {
            return pinType;
        }

        if (!string.IsNullOrWhiteSpace(warnContext))
        {
            WarnInvalidEntry($"Entry '{warnContext}' uses unknown Minimap.PinType '{trimmedName}'.");
        }

        return null;
    }

    private static ItemStand.Orientation? ParseItemStandOrientation(string? orientationName, string? warnContext)
    {
        string trimmedName = (orientationName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            return null;
        }

        if (Enum.TryParse(trimmedName, true, out ItemStand.Orientation orientation))
        {
            return orientation;
        }

        if (!string.IsNullOrWhiteSpace(warnContext))
        {
            WarnInvalidEntry($"Entry '{warnContext}' uses unknown ItemStand.Orientation '{trimmedName}'.");
        }

        return null;
    }

    private static ItemDrop.ItemData.ItemType? ParseItemStandType(string? typeName)
    {
        string trimmedName = (typeName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            return null;
        }

        return Enum.TryParse(trimmedName, true, out ItemDrop.ItemData.ItemType itemType)
            ? itemType
            : null;
    }

    private static List<ItemDrop.ItemData.ItemType> ResolveItemStandTypes(List<string> typeNames, string warnContext)
    {
        List<ItemDrop.ItemData.ItemType> types = new();
        foreach (string typeName in typeNames)
        {
            ItemDrop.ItemData.ItemType? itemType = ParseItemStandType(typeName);
            if (!itemType.HasValue)
            {
                WarnInvalidEntry($"Entry '{warnContext}' uses unknown ItemDrop.ItemData.ItemType '{typeName}'.");
                continue;
            }

            types.Add(itemType.Value);
        }

        return types;
    }

    private static ItemDrop? ResolveItemDrop(string? prefabName, string? warnContext)
    {
        GameObject? prefab = ResolveItemPrefab(prefabName, warnContext);
        return prefab != null ? prefab.GetComponent<ItemDrop>() : null;
    }

    private static List<ItemDrop> ResolveItemDropList(List<string> prefabNames, string? warnContext)
    {
        List<ItemDrop> items = new();
        for (int index = 0; index < prefabNames.Count; index++)
        {
            string? itemContext = warnContext == null ? null : $"{warnContext}[{index}]";
            ItemDrop? itemDrop = ResolveItemDrop(prefabNames[index], itemContext);
            if (itemDrop != null)
            {
                items.Add(itemDrop);
            }
        }

        return items;
    }

    private static GameObject? ResolveItemPrefab(string? prefabName, string? warnContext)
    {
        string trimmedName = (prefabName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            return null;
        }

        GameObject? prefab = ObjectDB.instance?.GetItemPrefab(trimmedName) ?? ZNetScene.instance?.GetPrefab(trimmedName);
        if (prefab == null)
        {
            if (!string.IsNullOrWhiteSpace(warnContext))
            {
                WarnInvalidEntry($"Entry '{warnContext}' references unknown item prefab '{trimmedName}'.");
            }

            return null;
        }

        if (!prefab.TryGetComponent(out ItemDrop _))
        {
            if (!string.IsNullOrWhiteSpace(warnContext))
            {
                WarnInvalidEntry($"Entry '{warnContext}' references '{trimmedName}', but it is not an item prefab.");
            }

            return null;
        }

        return prefab;
    }

    private static GameObject? ResolveSpawnPrefab(string? prefabName, string? warnContext)
    {
        string trimmedName = (prefabName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            return null;
        }

        GameObject? prefab = ZNetScene.instance?.GetPrefab(trimmedName) ?? ObjectDB.instance?.GetItemPrefab(trimmedName);
        if (prefab == null && !string.IsNullOrWhiteSpace(warnContext))
        {
            WarnInvalidEntry($"Entry '{warnContext}' references unknown spawn prefab '{trimmedName}'.");
        }

        return prefab;
    }

    private static StatusEffect? ResolveStatusEffect(string? statusEffectName, string? warnContext)
    {
        string trimmedName = (statusEffectName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            return null;
        }

        StatusEffect? statusEffect = ObjectDB.instance?.GetStatusEffect(trimmedName.GetStableHashCode());
        if (statusEffect != null)
        {
            return statusEffect;
        }

        statusEffect = ObjectDB.instance?.m_StatusEffects.FirstOrDefault(effect =>
            string.Equals(effect.name, trimmedName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(effect.m_name, trimmedName, StringComparison.OrdinalIgnoreCase));
        if (statusEffect != null)
        {
            return statusEffect;
        }

        if (!string.IsNullOrWhiteSpace(warnContext))
        {
            WarnInvalidEntry($"Entry '{warnContext}' references unknown status effect '{trimmedName}'.");
        }

        return null;
    }


    private static int CompareLocationSnapshotsForOutput(LocationSnapshot? left, LocationSnapshot? right)
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

        int primaryComparison = GetLocationPrimaryComponentRank(left).CompareTo(GetLocationPrimaryComponentRank(right));
        if (primaryComparison != 0)
        {
            return primaryComparison;
        }

        int signatureComparison = GetLocationComponentSignatureMask(left).CompareTo(GetLocationComponentSignatureMask(right));
        if (signatureComparison != 0)
        {
            return signatureComparison;
        }

        return string.Compare(left.Prefab, right.Prefab, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetLocationPrimaryComponentRank(LocationSnapshot snapshot)
    {
        if (snapshot.OfferingBowl != null)
        {
            return 0;
        }

        if (snapshot.ItemStands.Count > 0)
        {
            return 1;
        }

        if (snapshot.Vegvisirs.Count > 0)
        {
            return 2;
        }

        if (snapshot.Runestones.Count > 0)
        {
            return 3;
        }

        return 4;
    }

    private static int GetLocationComponentSignatureMask(LocationSnapshot snapshot)
    {
        int mask = 0;
        if (snapshot.OfferingBowl != null)
        {
            mask |= 1 << 0;
        }

        if (snapshot.ItemStands.Count > 0)
        {
            mask |= 1 << 1;
        }

        if (snapshot.Vegvisirs.Count > 0)
        {
            mask |= 1 << 2;
        }

        if (snapshot.Runestones.Count > 0)
        {
            mask |= 1 << 3;
        }

        return mask;
    }
    private static string? NormalizeReferencePrefabName(GameObject? prefab)
    {
        return prefab == null ? null : TrimCloneSuffix(prefab.name);
    }

    private static string TrimCloneSuffix(string name)
    {
        const string cloneSuffix = "(Clone)";
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        return name.EndsWith(cloneSuffix, StringComparison.Ordinal)
            ? name[..^cloneSuffix.Length].TrimEnd()
            : name;
    }

    private static bool IsReferenceDefault(float value, float defaultValue)
    {
        return Math.Abs(value - defaultValue) < 0.0001f;
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
}
