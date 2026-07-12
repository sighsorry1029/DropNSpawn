using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DropNSpawn;

internal static class EventManager
{
    private const string DomainName = "events";
    private const string ReferenceAutoUpdateStateKey = "events";
    internal static readonly DomainModuleDefinition<EventDefinition> Module =
        new(new DomainModuleOptions<EventDefinition>
        {
            DomainKey = DomainName,
            ReloadDomain = DropNSpawnPlugin.ReloadDomain.Event,
            ManifestSettingKey = "events_manifest",
            ManifestPriority = 95,
            ShouldReloadForPath = ShouldReloadForPath,
            Reload = ReloadConfiguration,
            InitializeRuntime = Initialize,
            OnGameDataReady = NotifyGameDataReady,
            HandleExpandWorldDataReady = HandleExpandWorldDataReady,
            DtoVersion = 2,
            TransportProfile = DomainTransportProfile.MediumConfig,
            DisplayName = "events",
            CacheDirectoryName = "events",
            ClientRequestPriority = 95,
            KeySelector = entry => entry.Event ?? "",
            ApplyPayloadAction = ApplySyncedPayload,
            WorkKinds = DomainWorkKinds.Runtime,
            BeforeClientManifestChanged = MarkSyncedPayloadPending,
            OnClientAuthorityCutover = EnterPendingSyncedPayloadState
        });
    internal static DomainDescriptor<EventDefinition> Descriptor => Module.DescriptorTyped;
    internal static DomainTransportMetadata<EventDefinition> TransportMetadata => Module.TransportMetadataTyped;

    private static readonly object Sync = new();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static bool _initialized;
    private static string _configurationSignature = "";
    private static bool _synchronizedPayloadReady;
    private static List<EventDefinition> ActiveDefinitions = new();
    private static List<RandomEvent>? BaselineEvents;
    private static readonly List<SpawnSystem.SpawnData> AppliedSpawnData = new();
    private static readonly Dictionary<RandomEvent, EventRuntimeMetadata> EventMetadata = new();
    private static readonly List<ActiveMultipleEvent> MultipleActiveEvents = new();
    private static MethodInfo? ExpandWorldCommandRunMethod;
    private static bool ExpandWorldCommandRunMethodLookedUp;
    private static bool MissingCommandManagerWarningLogged;

    private sealed class ActiveMultipleEvent
    {
        internal ActiveMultipleEvent(RandomEvent activeEvent)
        {
            Event = activeEvent;
        }

        internal RandomEvent Event { get; }
    }

    private sealed class EventRuntimeMetadata
    {
        internal PlayerBaseCondition? PlayerBase { get; set; }
        internal List<string>? RequiredEnvironments { get; set; }
        internal IntRangeDefinition? PlayerLimit { get; set; }
        internal float PlayerDistance { get; set; } = 100f;
        internal List<string>? StartCommands { get; set; }
        internal List<string>? EndCommands { get; set; }

        internal bool HasConditionValues()
        {
            return PlayerBase != null ||
                   (RequiredEnvironments?.Count ?? 0) > 0 ||
                   (PlayerLimit?.HasValues() ?? false);
        }

        internal bool HasValues()
        {
            return HasConditionValues() ||
                   (StartCommands?.Count ?? 0) > 0 ||
                   (EndCommands?.Count ?? 0) > 0;
        }
    }

    private sealed class EventReferenceOutputEntry
    {
        internal EventReferenceOutputEntry(EventDefinition definition, string ownerName)
        {
            Definition = definition;
            OwnerName = string.IsNullOrWhiteSpace(ownerName)
                ? PrefabOwnerCatalog.UnknownOwnerName
                : ownerName.Trim();
        }

        internal EventDefinition Definition { get; }
        internal string OwnerName { get; }
    }

    private sealed class KnownVanillaEventVariantGroup
    {
        internal KnownVanillaEventVariantGroup(string name, RandomEvent preferred, List<RandomEvent> variants)
        {
            Name = name;
            Preferred = preferred;
            Variants = variants;
        }

        internal string Name { get; }
        internal RandomEvent Preferred { get; }
        internal List<RandomEvent> Variants { get; }
    }

    private sealed class PlayerBaseCondition
    {
        internal bool AllowNear { get; set; }
        internal bool AllowAway { get; set; }

        internal bool IsNearOnly => AllowNear && !AllowAway;
    }

    private static string DomainPrefix => PluginSettingsFacade.GetYamlDomainFilePrefix(DomainName);
    private static string PrimaryOverrideConfigurationPathYml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{DomainPrefix}.yml");
    private static string PrimaryOverrideConfigurationPathYaml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{DomainPrefix}.yaml");
    private static string ReferenceConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{DomainPrefix}.reference.yml");
    private static readonly DomainConfigurationRuntime<EventDefinition, List<EventDefinition>> ConfigurationRuntime =
        new(
            new DomainLoadHooks<EventDefinition, List<EventDefinition>>(
                ParseLocalConfigurationDocuments,
                BuildSyncedConfigurationState,
                CommitConfigurationState,
                RejectLocalConfigurationPayload,
                state => state.Count,
                onUnchangedPayload: OnSourceOfTruthPayloadUnchanged,
                publishCommittedState: PublishSyncedConfiguration),
            new DomainSyncHooks<EventDefinition, List<EventDefinition>>(
                (out List<EventDefinition> configuration, out string payloadToken) =>
                    ConfigurationDomainHost.TryGetSyncedEntries(Descriptor, out configuration, out payloadToken),
                payloadToken => ConfigurationDomainHost.ShouldSkipSyncedPayload(
                    LoadState,
                    payloadToken,
                    _synchronizedPayloadReady),
                BuildSyncedConfigurationState,
                CommitConfigurationState,
                state => state.Count,
                "ServerSync:DropNSpawnEvents",
                () => ConfigurationDomainHost.HandleWaitingForSyncedPayload(MarkSyncedPayloadPending)));
    private static DomainLoadState LoadState => ConfigurationRuntime.LoadState;

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(DropNSpawnPlugin.YamlConfigDirectoryPath);
            EnsureDefaultOverrideFile();
            DomainReloadOutcome outcome = LoadConfiguration();
            if (DropNSpawnPlugin.IsSourceOfTruth || outcome == DomainReloadOutcome.Loaded)
            {
                ApplyLoadedConfigurationLocked(DropNSpawnPlugin.IsSourceOfTruth ? "events override YAML" : "synced events");
            }

            _initialized = true;
        }
    }

    internal static void Dispose()
    {
        MultipleActiveEvents.Clear();
        _initialized = false;
        _synchronizedPayloadReady = false;
        _configurationSignature = "";
        ConfigurationRuntime.ResetLoadState();
    }

    internal static void NotifyGameDataReady(string source)
    {
        lock (Sync)
        {
            CaptureBaselineIfNeeded();
            ApplyGlobalEventSettingsLocked();
            ApplyActiveDefinitionsLocked(source);
            EnsureReferenceConfigurationFileUpToDateLocked();
        }
    }

    internal static void ApplyGlobalEventSettings()
    {
        lock (Sync)
        {
            ApplyGlobalEventSettingsLocked();
        }
    }

    internal static void ReapplyEventDefinitions(string source)
    {
        lock (Sync)
        {
            CaptureBaselineIfNeeded();
            ApplyActiveDefinitionsLocked(source);
            EnsureReferenceConfigurationFileUpToDateLocked();
        }
    }

    internal static void OnRandEventSystemDestroyed()
    {
        lock (Sync)
        {
            BaselineEvents = null;
            ClearAppliedPayloadsLocked();
            EventMetadata.Clear();
            MultipleActiveEvents.Clear();
        }
    }

    internal static void CopySpawnPayloads(RandomEvent source, RandomEvent clone)
    {
        if (source == null || clone == null)
        {
            return;
        }

        if (EventMetadata.TryGetValue(source, out EventRuntimeMetadata metadata))
        {
            EventMetadata[clone] = CloneMetadata(metadata);
        }

        if (source.m_spawn == null || clone.m_spawn == null)
        {
            return;
        }

        int count = Math.Min(source.m_spawn.Count, clone.m_spawn.Count);
        for (int i = 0; i < count; i++)
        {
            SpawnSystemCustomDataSupport.CopyPreparedPayload(source.m_spawn[i], clone.m_spawn[i]);
        }
    }

    internal static void ReloadConfiguration()
    {
        lock (Sync)
        {
            DomainReloadOutcome outcome = LoadConfiguration();
            if (DropNSpawnPlugin.IsSourceOfTruth || outcome == DomainReloadOutcome.Loaded)
            {
                ApplyLoadedConfigurationLocked(DropNSpawnPlugin.IsSourceOfTruth ? "events override YAML" : "synced events");
            }
        }
    }

    internal static bool TryWriteReferenceConfigurationFile(out string path, out string error)
    {
        path = ReferenceConfigurationPath;
        error = "";
        if (!TryGetReferenceEvents(out List<RandomEvent> events))
        {
            error = "Event baseline is not ready yet. Join a world and wait for ZoneSystem.Start before writing event reference YAML.";
            return false;
        }

        try
        {
            string content = BuildReferenceYaml(events);
            GeneratedArtifactWriter.WriteText(path, content, $"Wrote event reference configuration at {path}.");
            ReferenceArtifactLifecycle.RecordUpdate(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, ReferenceRefreshSupport.ComputeStableHash(content));
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to write event reference configuration: {ex.Message}";
            return false;
        }
    }

    internal static bool TryWriteFullScaffoldConfigurationFile(out string path, out string error)
    {
        path = ReferenceConfigurationPath;
        error = "Event full scaffold is not generated. DNS_events.reference.yml contains the full event-level schema with compact spawn entries.";
        return false;
    }

    private static bool TryGetReferenceEvents(out List<RandomEvent> events)
    {
        if (BaselineEvents == null)
        {
            events = new List<RandomEvent>();
            return false;
        }

        events = CloneEvents(BaselineEvents);
        return true;
    }

    private static void EnsureReferenceConfigurationFileUpToDateLocked()
    {
        if (!TryGetReferenceEvents(out List<RandomEvent> events))
        {
            return;
        }

        string content;
        try
        {
            content = BuildReferenceYaml(events);
        }
        catch (Exception ex)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Failed to build event reference configuration: {ex.Message}");
            return;
        }

        string sourceSignature = ReferenceRefreshSupport.ComputeStableHash(content);
        if (!ReferenceArtifactLifecycle.TryPlanUpdate(
                ReferenceAutoUpdateStateKey,
                ReferenceConfigurationPath,
                sourceSignature,
                out ReferenceArtifactUpdateKind updateKind))
        {
            return;
        }

        GeneratedArtifactWriter.WriteText(
            ReferenceConfigurationPath,
            content,
            $"{ReferenceArtifactLifecycle.FormatAction(updateKind)} event reference configuration at {ReferenceConfigurationPath}.");
        ReferenceArtifactLifecycle.RecordUpdate(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, sourceSignature);
    }

    internal static void MarkSyncedPayloadPending()
    {
        lock (Sync)
        {
            ConfigurationRuntime.MarkSyncedPayloadPending(
                DropNSpawnPlugin.IsSourceOfTruth,
                () => _synchronizedPayloadReady = false);
        }
    }

    internal static void EnterPendingSyncedPayloadState()
    {
        lock (Sync)
        {
            ConfigurationRuntime.EnterPendingSyncedPayloadState(
                DropNSpawnPlugin.IsSourceOfTruth,
                afterResetLoadState: () =>
                {
                    _synchronizedPayloadReady = false;
                    _configurationSignature = "";
                });
        }
    }

    internal static void ApplySyncedPayload()
    {
        lock (Sync)
        {
            ConfigurationRuntime.ApplySyncedPayload(() => ApplyLoadedConfigurationLocked("synced events"));
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

            CaptureBaselineIfNeeded();
            ApplyActiveDefinitionsLocked("ExpandWorldData ready");
            EnsureReferenceConfigurationFileUpToDateLocked();
            return true;
        }
    }

    private static DomainReloadOutcome LoadConfiguration()
    {
        if (DropNSpawnPlugin.IsSourceOfTruth)
        {
            EnsureDefaultOverrideFile();
            return ConfigurationRuntime.ReloadSourceOfTruth(EnumerateOverrideConfigurationPaths().ToList());
        }

        return ConfigurationRuntime.ReloadSynced();
    }

    private static void ApplyLoadedConfigurationLocked(string source)
    {
        CaptureBaselineIfNeeded();
        ApplyActiveDefinitionsLocked(source);
        EnsureReferenceConfigurationFileUpToDateLocked();
    }

    private static List<EventDefinition> BuildSyncedConfigurationState(List<EventDefinition> definitions, string sourceName)
    {
        List<EventDefinition> configuration = NetworkPayloadSyncSupport.CloneEntries(Descriptor, definitions);
        NormalizeDefinitions(configuration);
        return configuration;
    }

    private static void CommitConfigurationState(List<EventDefinition> configuration, string payloadToken)
    {
        ActiveDefinitions = configuration;
        _configurationSignature = NetworkPayloadSyncSupport.ComputeEventConfigurationSignature(ActiveDefinitions);
        LoadState.LastLoadedPayload = payloadToken;
        LoadState.LastRejectedPayload = "";
        LoadState.PendingStrictPayload = "";
        LoadState.LastRejectedValidationKey = "";
        _synchronizedPayloadReady = true;
    }

    private static void PublishSyncedConfiguration()
    {
        ConfigurationDomainHost.PublishSyncedPayload(
            DropNSpawnPlugin.IsSourceOfTruth,
            Descriptor,
            ActiveDefinitions,
            _configurationSignature);
    }

    private static void OnSourceOfTruthPayloadUnchanged()
    {
        if (!NetworkPayloadSyncSupport.IsPayloadCurrent(Descriptor, _configurationSignature))
        {
            PublishSyncedConfiguration();
        }
    }

    internal static bool ShouldReloadForPath(string? path)
    {
        return PluginSettingsFacade.IsEligibleOverrideConfigurationPath(path) &&
               IsOverrideConfigurationFileName(Path.GetFileName(path ?? ""));
    }

    private static void EnsureDefaultOverrideFile()
    {
        if (DomainConfigurationFileSupport.HasAnyOverrideConfigurationFile(
                DomainName,
                PrimaryOverrideConfigurationPathYml,
                PrimaryOverrideConfigurationPathYaml))
        {
            return;
        }

        GeneratedArtifactWriter.WriteText(
            PrimaryOverrideConfigurationPathYml,
            BuildDefaultOverrideYaml(),
            $"Created event override configuration at {PrimaryOverrideConfigurationPathYml}.",
            logOnlyWhenChanged: true);
    }

    private static LocalLoadResult<EventDefinition> ParseLocalConfigurationDocuments(
        List<ConfigurationLoadSupport.LocalYamlDocument> documents)
    {
        List<EventDefinition> definitions = new();
        int loadedFileCount = 0;
        foreach (ConfigurationLoadSupport.LocalYamlDocument document in documents)
        {
            if (document.ReadError != null)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to load event override YAML '{document.Path}': {document.ReadError}");
                continue;
            }

            List<EventDefinition> parsed = ParseYaml(document.Yaml ?? "", Path.GetFileName(document.Path));
            definitions.AddRange(parsed);
            loadedFileCount++;
        }

        NormalizeDefinitions(definitions);
        return new LocalLoadResult<EventDefinition>
        {
            Entries = definitions,
            ParsedEntryCount = definitions.Count,
            LoadedFileCount = loadedFileCount
        };
    }

    private static void RejectLocalConfigurationPayload(string payload, IEnumerable<string> errors)
    {
        ConfigurationDomainHost.RejectLocalConfigurationPayload(LoadState, payload, errors, "events");
    }

    private static IEnumerable<string> EnumerateOverrideConfigurationPaths()
    {
        return DomainConfigurationFileSupport.EnumerateOverrideConfigurationPaths(
            DomainName,
            PrimaryOverrideConfigurationPathYml,
            PrimaryOverrideConfigurationPathYaml);
    }

    private static bool IsOverrideConfigurationFileName(string fileName)
    {
        return DomainConfigurationFileSupport.IsOverrideConfigurationFileName(DomainName, fileName);
    }

    private static List<EventDefinition> ParseYaml(string yaml, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return new List<EventDefinition>();
        }

        try
        {
            return Deserializer.Deserialize<List<EventDefinition>>(yaml) ?? new List<EventDefinition>();
        }
        catch (Exception ex)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to parse event YAML '{sourceName}': {ex.Message}");
            return new List<EventDefinition>();
        }
    }

    private static void NormalizeDefinitions(List<EventDefinition> definitions)
    {
        foreach (EventDefinition definition in definitions)
        {
            definition.Event = NormalizeOptionalString(definition.Event);
            definition.Settings = NormalizePositionalStringList(definition.Settings);
            NormalizeConditions(definition.Conditions);
            definition.Messages = NormalizeOptionalStringList(definition.Messages);
            definition.ForceEnvironment = NormalizeOptionalStringPreserveEmpty(definition.ForceEnvironment);
            definition.ForceMusic = NormalizeOptionalStringPreserveEmpty(definition.ForceMusic);
            definition.StartCommands = NormalizeOptionalStringList(definition.StartCommands);
            definition.EndCommands = NormalizeOptionalStringList(definition.EndCommands);
            NormalizeSpawns(definition.Spawns);
        }
    }

    private static void NormalizeConditions(EventConditionsDefinition? conditions)
    {
        if (conditions == null)
        {
            return;
        }

        conditions.Biomes = NormalizeOptionalStringList(conditions.Biomes);
        conditions.PlayerBase = NormalizeOptionalStringList(conditions.PlayerBase);
        conditions.RequiredEnvironments = NormalizeOptionalStringList(conditions.RequiredEnvironments)
            ?.Select(value => value.ToLowerInvariant())
            .ToList();
        conditions.Players = NormalizeOptionalStringList(conditions.Players);
        conditions.RequiredGlobalKeys = NormalizeOptionalStringList(conditions.RequiredGlobalKeys);
        conditions.ForbiddenGlobalKeys = NormalizeOptionalStringList(conditions.ForbiddenGlobalKeys);
        conditions.RequiredPlayerKeysAny = NormalizeOptionalStringList(conditions.RequiredPlayerKeysAny);
        conditions.RequiredPlayerKeysAll = NormalizeOptionalStringList(conditions.RequiredPlayerKeysAll);
        conditions.ForbiddenPlayerKeys = NormalizeOptionalStringList(conditions.ForbiddenPlayerKeys);
        conditions.RequiredKnownItems = NormalizeOptionalStringList(conditions.RequiredKnownItems);
        conditions.ForbiddenKnownItems = NormalizeOptionalStringList(conditions.ForbiddenKnownItems);
    }

    private static void NormalizeSpawns(List<EventSpawnDefinition>? spawns)
    {
        if (spawns == null)
        {
            return;
        }

        foreach (EventSpawnDefinition spawn in spawns)
        {
            spawn.Prefab = NormalizeOptionalString(spawn.Prefab);
        }
    }

    private static void CaptureBaselineIfNeeded()
    {
        if (BaselineEvents != null || RandEventSystem.instance == null)
        {
            return;
        }

        BaselineEvents = CloneEvents(RandEventSystem.instance.m_events);
    }

    private static void ApplyGlobalEventSettingsLocked()
    {
        if (RandEventSystem.instance == null)
        {
            return;
        }

        RandEventSystem.instance.m_eventChance = PluginSettingsFacade.GetRandomEventChance();
        RandEventSystem.instance.m_eventIntervalMin = PluginSettingsFacade.GetRandomEventIntervalMinutes();

        if (!PluginSettingsFacade.IsMultipleEventsEnabled())
        {
            StopAllMultipleEventsLocked(RandEventSystem.instance, sendUpdate: true);
        }
    }

    internal static bool TryRunMultipleEventsFixedUpdate(RandEventSystem eventSystem)
    {
        if (eventSystem == null || !PluginSettingsFacade.IsMultipleEventsEnabled() || !IsEventServer())
        {
            return false;
        }

        float dt = Time.fixedDeltaTime;
        eventSystem.UpdateForcedEvents(dt);
        eventSystem.UpdateRandomEvent(dt);

        RandomEvent forcedEvent = eventSystem.m_forcedEvent;
        if (forcedEvent != null)
        {
            forcedEvent.Update(true, true, true, dt);
        }

        List<ActiveMultipleEvent> stoppedEvents = MultipleActiveEvents
            .Where(activeEvent =>
            {
                bool anyPlayerInArea = eventSystem.IsAnyPlayerInEventArea(activeEvent.Event);
                return activeEvent.Event.Update(true, true, anyPlayerInArea, dt);
            })
            .ToList();

        StopMultipleEventsLocked(stoppedEvents, callOnStop: true);
        SelectLocalMultipleEvent(eventSystem);
        return true;
    }

    internal static bool TryHandleMultipleSetRandomEvent(RandEventSystem eventSystem, RandomEvent ev, Vector3 pos)
    {
        if (eventSystem == null || !PluginSettingsFacade.IsMultipleEventsEnabled() || !IsEventServer())
        {
            return false;
        }

        if (ev == null)
        {
            StopAllMultipleEventsLocked(eventSystem, sendUpdate: true);
            return true;
        }

        float minimumDistance = PluginSettingsFacade.GetMinimumDistanceBetweenEvents();
        if (MultipleActiveEvents.Any(activeEvent =>
                Utils.DistanceXZ(activeEvent.Event.m_pos, pos) < minimumDistance))
        {
            return true;
        }

        RandomEvent clonedEvent = ev.Clone();
        clonedEvent.m_pos = pos;
        clonedEvent.OnStart();
        MultipleActiveEvents.Add(new ActiveMultipleEvent(clonedEvent));
        eventSystem.m_randomEvent = clonedEvent;
        eventSystem.SendCurrentRandomEvent();
        return true;
    }

    internal static bool TrySendMultipleCurrentRandomEvent(RandEventSystem eventSystem)
    {
        if (eventSystem == null || !PluginSettingsFacade.IsMultipleEventsEnabled() || !IsEventServer())
        {
            return false;
        }

        if (eventSystem.m_forcedEvent != null)
        {
            SendEventToEveryone(eventSystem.m_forcedEvent);
            return true;
        }

        if (MultipleActiveEvents.Count == 0)
        {
            return false;
        }

        if (MultipleActiveEvents.Count == 1)
        {
            SendEventToEveryone(MultipleActiveEvents[0].Event);
            return true;
        }

        if (ZNet.instance == null || ZRoutedRpc.instance == null)
        {
            return true;
        }

        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (peer.m_rpc == null)
            {
                continue;
            }

            ActiveMultipleEvent nearestEvent = MultipleActiveEvents
                .OrderBy(activeEvent => Utils.DistanceXZ(activeEvent.Event.m_pos, peer.m_refPos))
                .First();
            ZRoutedRpc.instance.InvokeRoutedRPC(
                peer.m_uid,
                "SetEvent",
                new object[] { nearestEvent.Event.m_name, nearestEvent.Event.m_time, nearestEvent.Event.m_pos });
        }

        return true;
    }

    internal static bool TryRunCheckPerPlayerRandomUpdate(RandEventSystem eventSystem, float dt)
    {
        if (eventSystem == null ||
            !PluginSettingsFacade.IsEventCheckPerPlayerEnabled() ||
            !IsEventServer() ||
            Game.m_eventRate == 0f)
        {
            return false;
        }

        if (RandEventSystem.s_randomEventNeedsRefresh)
        {
            RandEventSystem.RefreshPlayerEventData();
        }

        CheckGlobalEventsPerPlayer(eventSystem, dt);
        CheckStandaloneEventsPerPlayer(eventSystem, dt);
        return true;
    }

    private static void SelectLocalMultipleEvent(RandEventSystem eventSystem)
    {
        if (eventSystem.m_forcedEvent != null)
        {
            eventSystem.SetActiveEvent(eventSystem.m_forcedEvent, false);
            return;
        }

        if (Player.m_localPlayer == null)
        {
            eventSystem.m_randomEvent = null;
            eventSystem.SetActiveEvent(null, false);
            return;
        }

        Vector3 playerPosition = Player.m_localPlayer.transform.position;
        RandomEvent? nearestEvent = MultipleActiveEvents
            .OrderBy(activeEvent => Utils.DistanceXZ(activeEvent.Event.m_pos, playerPosition))
            .FirstOrDefault()
            ?.Event;

        eventSystem.m_randomEvent = nearestEvent;
        if (nearestEvent != null && eventSystem.IsInsideRandomEventArea(nearestEvent, playerPosition))
        {
            eventSystem.SetActiveEvent(nearestEvent, false);
            return;
        }

        eventSystem.SetActiveEvent(null, false);
    }

    private static void CheckGlobalEventsPerPlayer(RandEventSystem eventSystem, float dt)
    {
        eventSystem.m_eventTimer += dt;
        if (eventSystem.m_eventTimer <= eventSystem.m_eventIntervalMin * 60f * Game.m_eventRate)
        {
            return;
        }

        eventSystem.m_eventTimer = 0f;
        foreach (RandEventSystem.PlayerEventData player in RandEventSystem.s_playerEventDatas)
        {
            if (UnityEngine.Random.Range(0f, 100f) > eventSystem.m_eventChance / Game.m_eventRate)
            {
                continue;
            }

            List<KeyValuePair<RandomEvent, Vector3>> possibleEvents = GetPossibleRandomEvents(eventSystem, player);
            if (possibleEvents.Count == 0)
            {
                continue;
            }

            KeyValuePair<RandomEvent, Vector3> selectedEvent = possibleEvents[UnityEngine.Random.Range(0, possibleEvents.Count)];
            eventSystem.SetRandomEvent(selectedEvent.Key, selectedEvent.Value);
        }
    }

    private static void CheckStandaloneEventsPerPlayer(RandEventSystem eventSystem, float dt)
    {
        List<RandEventSystem.PlayerEventData>? playerEvents = null;
        foreach (RandomEvent randomEvent in eventSystem.m_events)
        {
            if (!randomEvent.m_enabled ||
                randomEvent.m_standaloneInterval <= 0f ||
                eventSystem.m_activeEvent == randomEvent)
            {
                continue;
            }

            randomEvent.m_time += dt;
            if (randomEvent.m_time <= randomEvent.m_standaloneInterval * Game.m_eventRate)
            {
                continue;
            }

            if (randomEvent.m_standaloneChance > 0f)
            {
                playerEvents ??= new List<RandEventSystem.PlayerEventData>(1);
                foreach (RandEventSystem.PlayerEventData player in RandEventSystem.s_playerEventDatas)
                {
                    playerEvents.Clear();
                    playerEvents.Add(player);
                    if (UnityEngine.Random.Range(0f, 100f) > randomEvent.m_standaloneChance / Game.m_eventRate ||
                        !eventSystem.HaveGlobalKeys(randomEvent, playerEvents))
                    {
                        continue;
                    }

                    List<Vector3> validPoints = eventSystem.GetValidEventPoints(randomEvent, playerEvents);
                    if (validPoints.Count == 0)
                    {
                        continue;
                    }

                    eventSystem.SetRandomEvent(randomEvent, validPoints[UnityEngine.Random.Range(0, validPoints.Count)]);
                }
            }

            randomEvent.m_time = 0f;
        }
    }

    private static List<KeyValuePair<RandomEvent, Vector3>> GetPossibleRandomEvents(
        RandEventSystem eventSystem,
        RandEventSystem.PlayerEventData player)
    {
        eventSystem.m_lastPossibleEvents.Clear();
        List<RandEventSystem.PlayerEventData> playerEvents = new(1) { player };
        foreach (RandomEvent randomEvent in eventSystem.m_events)
        {
            if (!randomEvent.m_enabled || !randomEvent.m_random || !eventSystem.HaveGlobalKeys(randomEvent, playerEvents))
            {
                continue;
            }

            List<Vector3> validPoints = eventSystem.GetValidEventPoints(randomEvent, playerEvents);
            if (validPoints.Count == 0)
            {
                continue;
            }

            Vector3 selectedPoint = validPoints[UnityEngine.Random.Range(0, validPoints.Count)];
            eventSystem.m_lastPossibleEvents.Add(new KeyValuePair<RandomEvent, Vector3>(randomEvent, selectedPoint));
        }

        return eventSystem.m_lastPossibleEvents;
    }

    private static void StopAllMultipleEventsLocked(RandEventSystem? eventSystem, bool sendUpdate)
    {
        if (MultipleActiveEvents.Count == 0)
        {
            return;
        }

        StopMultipleEventsLocked(MultipleActiveEvents.ToList(), callOnStop: true);
        if (eventSystem != null)
        {
            eventSystem.m_randomEvent = null;
            if (sendUpdate)
            {
                eventSystem.SendCurrentRandomEvent();
            }
        }
    }

    private static void StopMultipleEventsLocked(List<ActiveMultipleEvent> activeEvents, bool callOnStop)
    {
        if (activeEvents.Count == 0)
        {
            return;
        }

        foreach (ActiveMultipleEvent activeEvent in activeEvents)
        {
            if (callOnStop)
            {
                activeEvent.Event.OnStop();
            }

            EventMetadata.Remove(activeEvent.Event);
        }

        MultipleActiveEvents.RemoveAll(activeEvents.Contains);
    }

    private static void SendEventToEveryone(RandomEvent randomEvent)
    {
        if (ZRoutedRpc.instance == null)
        {
            return;
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.Everybody,
            "SetEvent",
            new object[] { randomEvent.m_name, randomEvent.m_time, randomEvent.m_pos });
    }

    private static bool IsEventServer()
    {
        return ZNet.instance != null && ZNet.instance.IsServer();
    }

    private static void ApplyActiveDefinitionsLocked(string source)
    {
        if (RandEventSystem.instance == null || BaselineEvents == null)
        {
            return;
        }

        ClearAppliedPayloadsLocked();
        EventMetadata.Clear();

        List<RandomEvent> events = CloneEvents(BaselineEvents);
        HashSet<RandomEvent> explicitDurationEvents = new();

        if (PluginSettingsFacade.IsEventDomainEnabled())
        {
            List<KnownVanillaEventVariantGroup> knownVanillaVariantGroups =
                FindKnownVanillaEventVariantGroups(events);
            HashSet<string> overriddenEventNames = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<RandomEvent>> byName = events
                .Where(ev => !string.IsNullOrWhiteSpace(ev.m_name))
                .GroupBy(ev => ev.m_name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < ActiveDefinitions.Count; index++)
            {
                EventDefinition definition = ActiveDefinitions[index];
                string eventName = definition.Event ?? "";
                if (eventName.Length == 0)
                {
                    DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Event override entry {index.ToString(CultureInfo.InvariantCulture)} is missing 'event'.");
                    continue;
                }

                if (!byName.TryGetValue(eventName, out List<RandomEvent> targets))
                {
                    RandomEvent target = new();
                    targets = new List<RandomEvent> { target };
                    byName[eventName] = targets;
                    events.Add(target);
                }

                overriddenEventNames.Add(eventName);
                for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                {
                    RandomEvent target = targets[targetIndex];
                    target.m_name = eventName;
                    string context = targets.Count == 1
                        ? $"{source}:{eventName}"
                        : $"{source}:{eventName}[{(targetIndex + 1).ToString(CultureInfo.InvariantCulture)}]";
                    if (ApplyDefinition(target, definition, context))
                    {
                        explicitDurationEvents.Add(target);
                    }
                }
            }

            CollapseEquivalentKnownVanillaEventVariants(
                events,
                knownVanillaVariantGroups,
                overriddenEventNames);
        }

        ApplyEventDurationMultiplier(events, explicitDurationEvents);
        ApplyDefaultPlayerBaseConditions(events);
        RandEventSystem.instance.m_events = events;
        RandEventSystem.SetRandomEventsNeedsRefresh();
    }

    private static void ApplyDefaultPlayerBaseConditions(List<RandomEvent> events)
    {
        PlayerBaseCondition? defaultCondition =
            CreateDefaultPlayerBaseCondition(PluginSettingsFacade.GetDefaultEventPlayerBase());
        if (defaultCondition == null)
        {
            return;
        }

        foreach (RandomEvent ev in events)
        {
            if (ev == null)
            {
                continue;
            }

            if (EventMetadata.TryGetValue(ev, out EventRuntimeMetadata metadata))
            {
                if (metadata.PlayerBase != null)
                {
                    continue;
                }
            }
            else
            {
                metadata = new EventRuntimeMetadata();
            }

            metadata.PlayerBase = ClonePlayerBaseCondition(defaultCondition);
            ev.m_nearBaseOnly = defaultCondition.IsNearOnly;
            EventMetadata[ev] = metadata;
        }
    }

    private static PlayerBaseCondition? CreateDefaultPlayerBaseCondition(EventGlobalConfig.EventPlayerBaseDefault mode)
    {
        return mode switch
        {
            EventGlobalConfig.EventPlayerBaseDefault.Away => new PlayerBaseCondition
            {
                AllowAway = true
            },
            EventGlobalConfig.EventPlayerBaseDefault.Near => new PlayerBaseCondition
            {
                AllowNear = true
            },
            EventGlobalConfig.EventPlayerBaseDefault.AwayAndNear => new PlayerBaseCondition
            {
                AllowNear = true,
                AllowAway = true
            },
            _ => null
        };
    }

    private static void ApplyEventDurationMultiplier(
        IEnumerable<RandomEvent> events,
        ISet<RandomEvent> explicitDurationEvents)
    {
        float multiplier = PluginSettingsFacade.GetEventDurationMultiplier();
        if (multiplier == 1f)
        {
            return;
        }

        foreach (RandomEvent ev in events)
        {
            if (ev == null || ev.m_duration <= 0f)
            {
                continue;
            }

            if (multiplier <= 0f)
            {
                ev.m_enabled = false;
                continue;
            }

            if (!explicitDurationEvents.Contains(ev))
            {
                ev.m_duration *= multiplier;
            }
        }
    }

    private static bool ApplyDefinition(RandomEvent target, EventDefinition definition, string context)
    {
        if (definition.SpawnerDelay.HasValue) target.m_spawnerDelay = Math.Max(0f, definition.SpawnerDelay.Value);

        bool durationOverridden = ApplySettings(target, definition.Settings, context);
        ApplyStandalone(target, definition.Standalone);
        EventRuntimeMetadata metadata = BuildMetadata(definition, context);
        ApplyConditions(target, definition.Conditions, metadata, context);
        ApplyMessages(target, definition.Messages);
        ApplyForces(target, definition);
        if (metadata.HasValues())
        {
            EventMetadata[target] = metadata;
        }

        if (definition.Spawns != null)
        {
            target.m_spawn = BuildSpawnList(definition.Spawns, context);
        }

        return durationOverridden;
    }

    private static bool ApplySettings(RandomEvent target, List<string>? settings, string context)
    {
        if (settings == null || settings.Count == 0)
        {
            return false;
        }

        bool durationOverridden = false;

        if (TryParseBool(settings, 0, target.m_enabled, context, "settings[0]", out bool enabled))
        {
            target.m_enabled = enabled;
        }

        if (TryParseBool(settings, 1, target.m_random, context, "settings[1]", out bool random))
        {
            target.m_random = random;
        }

        if (TryParseFloat(settings, 2, target.m_duration, context, "settings[2]", out float duration))
        {
            target.m_duration = Math.Max(0f, duration);
            durationOverridden = true;
        }

        if (TryParseFloat(settings, 3, target.m_eventRange, context, "settings[3]", out float eventRange))
        {
            target.m_eventRange = Math.Max(0f, eventRange);
        }

        if (TryParseBool(settings, 4, target.m_pauseIfNoPlayerInArea, context, "settings[4]", out bool pauseIfNoPlayerInArea))
        {
            target.m_pauseIfNoPlayerInArea = pauseIfNoPlayerInArea;
        }

        return durationOverridden;
    }

    private static void ApplyStandalone(RandomEvent target, List<float>? standalone)
    {
        if (standalone == null || standalone.Count == 0)
        {
            return;
        }

        target.m_standaloneInterval = Math.Max(0f, standalone[0]);
        if (standalone.Count > 1)
        {
            target.m_standaloneChance = standalone[1];
        }
    }

    private static void ApplyBiomes(RandomEvent target, List<string>? biomes, string context)
    {
        if (biomes == null)
        {
            return;
        }

        if (TryParseBiomes(biomes, context, out Heightmap.Biome biomeMask))
        {
            target.m_biome = biomeMask;
        }
    }

    private static EventRuntimeMetadata BuildMetadata(EventDefinition definition, string context)
    {
        EventRuntimeMetadata metadata = new()
        {
            StartCommands = definition.StartCommands?.ToList(),
            EndCommands = definition.EndCommands?.ToList()
        };

        EventConditionsDefinition? conditions = definition.Conditions;
        if (conditions == null)
        {
            return metadata;
        }

        if (TryParsePlayerBaseCondition(conditions.PlayerBase, context, out PlayerBaseCondition? playerBase))
        {
            metadata.PlayerBase = playerBase;
        }

        if (conditions.RequiredEnvironments != null)
        {
            metadata.RequiredEnvironments = conditions.RequiredEnvironments.ToList();
        }

        if (conditions.Players != null && conditions.Players.Count > 0)
        {
            if (TryParseIntRange(conditions.Players[0], context, "conditions.players[0]", out IntRangeDefinition playerLimit))
            {
                metadata.PlayerLimit = playerLimit;
            }

            if (TryParseFloat(conditions.Players, 1, metadata.PlayerDistance, context, "conditions.players[1]", out float playerDistance))
            {
                metadata.PlayerDistance = Math.Max(0f, playerDistance);
            }
        }

        return metadata;
    }

    private static void ApplyConditions(RandomEvent target, EventConditionsDefinition? conditions, EventRuntimeMetadata metadata, string context)
    {
        if (conditions == null)
        {
            return;
        }

        ApplyBiomes(target, conditions.Biomes, context);

        if (metadata.PlayerBase != null)
        {
            target.m_nearBaseOnly = metadata.PlayerBase.IsNearOnly;
        }

        if (conditions.RequiredGlobalKeys != null) target.m_requiredGlobalKeys = conditions.RequiredGlobalKeys.ToList();
        if (conditions.ForbiddenGlobalKeys != null) target.m_notRequiredGlobalKeys = conditions.ForbiddenGlobalKeys.ToList();
        if (conditions.RequiredPlayerKeysAny != null) target.m_altRequiredPlayerKeysAny = conditions.RequiredPlayerKeysAny.ToList();
        if (conditions.RequiredPlayerKeysAll != null) target.m_altRequiredPlayerKeysAll = conditions.RequiredPlayerKeysAll.ToList();
        if (conditions.ForbiddenPlayerKeys != null) target.m_altNotRequiredPlayerKeys = conditions.ForbiddenPlayerKeys.ToList();
        if (conditions.RequiredKnownItems != null) target.m_altRequiredKnownItems = ResolveItemDrops(conditions.RequiredKnownItems, context);
        if (conditions.ForbiddenKnownItems != null) target.m_altRequiredNotKnownItems = ResolveItemDrops(conditions.ForbiddenKnownItems, context);
    }

    private static void ApplyMessages(RandomEvent target, List<string>? messages)
    {
        if (messages == null)
        {
            return;
        }

        target.m_startMessage = messages.Count > 0 ? messages[0] : "";
        target.m_endMessage = messages.Count > 1 ? messages[1] : "";
    }

    private static void ApplyForces(RandomEvent target, EventDefinition definition)
    {
        if (definition.ForceEnvironment != null)
        {
            target.m_forceEnvironment = definition.ForceEnvironment;
        }

        if (definition.ForceMusic != null)
        {
            target.m_forceMusic = definition.ForceMusic;
        }
    }

    private static List<SpawnSystem.SpawnData> BuildSpawnList(List<EventSpawnDefinition> definitions, string context)
    {
        List<SpawnSystem.SpawnData> spawns = new();
        for (int index = 0; index < definitions.Count; index++)
        {
            EventSpawnDefinition definition = definitions[index];
            CanonicalSpawnSystemEntry entry = new()
            {
                Prefab = definition.Prefab,
                Enabled = definition.Enabled ?? true,
                SpawnSystem = definition.SpawnSystem
            };

            SpawnSystemManager.NormalizeEntry(entry);
            SpawnSystem.SpawnData data = new();
            string spawnContext = $"{context}.spawns[{index.ToString(CultureInfo.InvariantCulture)}]";
            if (!SpawnSystemManager.ApplyEntry(data, entry, spawnContext, applyCustomData: false))
            {
                continue;
            }

            SpawnSystemCustomDataSupport.PreparedPayload? payload =
                SpawnSystemCustomDataSupport.BuildPreparedPayload(data, entry, spawnContext);
            SpawnSystemCustomDataSupport.ApplyPreparedPayload(data, payload);
            AppliedSpawnData.Add(data);
            spawns.Add(data);
        }

        return spawns;
    }

    private static void ClearAppliedPayloadsLocked()
    {
        foreach (SpawnSystem.SpawnData spawnData in AppliedSpawnData)
        {
            SpawnSystemCustomDataSupport.ApplyPreparedPayload(spawnData, null);
        }

        AppliedSpawnData.Clear();
    }

    private static void RemoveAppliedEventState(RandomEvent ev)
    {
        EventMetadata.Remove(ev);
        foreach (SpawnSystem.SpawnData spawnData in ev.m_spawn ?? new List<SpawnSystem.SpawnData>())
        {
            if (!AppliedSpawnData.Remove(spawnData))
            {
                continue;
            }

            SpawnSystemCustomDataSupport.ApplyPreparedPayload(spawnData, null);
        }
    }

    private static string BuildReferenceYaml(List<RandomEvent> events)
    {
        List<RandomEvent> sourceEvents = events ?? new List<RandomEvent>();
        List<KnownVanillaEventVariantGroup> knownVanillaVariantGroups =
            FindKnownVanillaEventVariantGroups(sourceEvents);
        PrefabOwnerResolver.OwnerSnapshot ownerSnapshot = PrefabOwnerResolver.GetSnapshot();
        List<EventReferenceOutputEntry> entries = sourceEvents
            .Where(ev => ev != null)
            .Where(ev => !knownVanillaVariantGroups.Any(group =>
                group.Variants.Any(variant => ReferenceEquals(variant, ev)) &&
                !ReferenceEquals(group.Preferred, ev)))
            .Select(ev =>
            {
                EventDefinition definition = ConvertToDefinition(ev, includeEventDefaults: true, includeSpawnDefaults: false);
                SuppressReferenceOnlyFields(definition);
                return new EventReferenceOutputEntry(definition, ResolveEventOwnerName(definition, ownerSnapshot));
            })
            .ToList();
        return BuildGeneratedYamlHeader("reference") + BuildEventReferenceDefinitionsYaml(entries, includeEventDefaults: true, includeEmptySpawnList: false);
    }

    private static List<KnownVanillaEventVariantGroup> FindKnownVanillaEventVariantGroups(
        IEnumerable<RandomEvent> events)
    {
        List<KnownVanillaEventVariantGroup> result = new();
        foreach (IGrouping<string, RandomEvent> group in (events ?? Enumerable.Empty<RandomEvent>())
                     .Where(ev => ev != null && !string.IsNullOrWhiteSpace(ev.m_name))
                     .GroupBy(ev => ev.m_name, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryGetKnownVanillaEventVariantKeys(group.Key, out string preferredKey, out string legacyKey))
            {
                continue;
            }

            List<RandomEvent> variants = group.ToList();
            if (variants.Count != 2)
            {
                continue;
            }

            List<RandomEvent> preferred = variants
                .Where(ev => HasSingleRequiredPlayerKey(ev, preferredKey))
                .ToList();
            List<RandomEvent> legacy = variants
                .Where(ev => HasSingleRequiredPlayerKey(ev, legacyKey))
                .ToList();
            if (preferred.Count != 1 || legacy.Count != 1)
            {
                continue;
            }

            result.Add(new KnownVanillaEventVariantGroup(group.Key, preferred[0], variants));
        }

        return result;
    }

    private static bool TryGetKnownVanillaEventVariantKeys(
        string eventName,
        out string preferredKey,
        out string legacyKey)
    {
        if (string.Equals(eventName, "army_moder", StringComparison.OrdinalIgnoreCase))
        {
            preferredKey = "GP_Bonemass";
            legacyKey = "$se_bonemass_name";
            return true;
        }

        if (string.Equals(eventName, "army_theelder", StringComparison.OrdinalIgnoreCase))
        {
            preferredKey = "GP_Eikthyr";
            legacyKey = "GP_TheElder";
            return true;
        }

        preferredKey = "";
        legacyKey = "";
        return false;
    }

    private static bool HasSingleRequiredPlayerKey(RandomEvent ev, string expectedKey)
    {
        List<string> keys = ev.m_altRequiredPlayerKeysAny ?? new List<string>();
        return keys.Count == 1 && string.Equals(keys[0], expectedKey, StringComparison.OrdinalIgnoreCase);
    }

    private static void CollapseEquivalentKnownVanillaEventVariants(
        List<RandomEvent> events,
        IEnumerable<KnownVanillaEventVariantGroup> groups,
        ISet<string> overriddenEventNames)
    {
        foreach (KnownVanillaEventVariantGroup group in groups)
        {
            if (!overriddenEventNames.Contains(group.Name))
            {
                continue;
            }

            string preferredSignature = ComputeComparableEventSignature(group.Preferred);
            if (group.Variants.Any(variant =>
                    !string.Equals(
                        ComputeComparableEventSignature(variant),
                        preferredSignature,
                        StringComparison.Ordinal)))
            {
                continue;
            }

            foreach (RandomEvent variant in group.Variants)
            {
                if (ReferenceEquals(variant, group.Preferred))
                {
                    continue;
                }

                RemoveAppliedEventState(variant);
                int index = events.FindIndex(candidate => ReferenceEquals(candidate, variant));
                if (index >= 0)
                {
                    events.RemoveAt(index);
                }
            }
        }
    }

    private static string ComputeComparableEventSignature(RandomEvent ev)
    {
        EventDefinition definition = ConvertToDefinition(ev, includeEventDefaults: true, includeSpawnDefaults: true);
        return NetworkPayloadSyncSupport.ComputeEventConfigurationSignature(new[] { definition });
    }

    private static string BuildEventReferenceDefinitionsYaml(
        List<EventReferenceOutputEntry> entries,
        bool includeEventDefaults,
        bool includeEmptySpawnList)
    {
        if (entries.Count == 0)
        {
            return "[]\n";
        }

        List<PrefabOwnerSection<EventReferenceOutputEntry>> sections = PrefabOutputSections.BuildSections(
            entries,
            entry => entry.Definition.Event ?? "",
            entry => entry.OwnerName);

        StringBuilder builder = new();
        bool wroteSection = false;
        foreach (PrefabOwnerSection<EventReferenceOutputEntry> section in sections)
        {
            if (section.Entries.Count == 0)
            {
                continue;
            }

            if (wroteSection)
            {
                builder.AppendLine();
            }

            PrefabOutputSections.AppendSectionHeaderComment(builder, section.OwnerName);
            foreach (EventReferenceOutputEntry entry in section.Entries)
            {
                AppendEventDefinition(builder, entry.Definition, includeEventDefaults, includeEmptySpawnList);
            }

            wroteSection = true;
        }

        return wroteSection ? builder.ToString() : "[]\n";
    }

    private static string ResolveEventOwnerName(EventDefinition definition, PrefabOwnerResolver.OwnerSnapshot ownerSnapshot)
    {
        List<string> owners = (definition.Spawns ?? new List<EventSpawnDefinition>())
            .Select(spawn => ownerSnapshot.GetOwnerName(spawn.Prefab))
            .Where(ownerName => !string.IsNullOrWhiteSpace(ownerName))
            .Select(ownerName => ownerName.Trim())
            .ToList();

        string? modOwner = owners
            .Where(ownerName =>
                !string.Equals(ownerName, PrefabOwnerCatalog.UnknownOwnerName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(ownerName, PrefabOwnerCatalog.VanillaOwnerName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(ownerName => ownerName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(modOwner))
        {
            return modOwner;
        }

        if (owners.Any(ownerName => string.Equals(ownerName, PrefabOwnerCatalog.VanillaOwnerName, StringComparison.OrdinalIgnoreCase)))
        {
            return PrefabOwnerCatalog.VanillaOwnerName;
        }

        return PrefabOwnerCatalog.UnknownOwnerName;
    }

    private static void SuppressReferenceOnlyFields(EventDefinition definition)
    {
        definition.Standalone = null;
        definition.SpawnerDelay = null;
        definition.StartCommands = null;
        definition.EndCommands = null;
    }

    private static string BuildGeneratedYamlHeader(string kind)
    {
        return $"# Generated DropNSpawn event {kind}.\n" +
               "# Edit DNS_events.yml instead; generated files may be overwritten.\n\n";
    }

    private static void AppendEventDefinition(StringBuilder builder, EventDefinition definition, bool includeEventDefaults, bool includeEmptySpawnList)
    {
        AppendYamlListEntryLine(builder, 0, "event", definition.Event);
        AppendYamlOptionalRawListLine(builder, 1, "settings", definition.Settings);
        AppendYamlOptionalFloatListLine(builder, 1, "standalone", definition.Standalone);
        AppendYamlOptionalFloatLine(builder, 1, "spawnerDelay", definition.SpawnerDelay);
        AppendEventConditions(builder, definition.Conditions, includeEventDefaults);
        AppendYamlOptionalInlineListLine(builder, 1, "messages", definition.Messages);
        if (includeEventDefaults)
        {
            AppendYamlStringLine(builder, 1, "forceEnvironment", definition.ForceEnvironment);
            AppendYamlStringLine(builder, 1, "forceMusic", definition.ForceMusic);
        }
        else
        {
            AppendYamlOptionalStringLine(builder, 1, "forceEnvironment", definition.ForceEnvironment);
            AppendYamlOptionalStringLine(builder, 1, "forceMusic", definition.ForceMusic);
        }
        AppendYamlOptionalInlineListLine(builder, 1, "startCommands", definition.StartCommands);
        AppendYamlOptionalInlineListLine(builder, 1, "endCommands", definition.EndCommands);
        AppendEventSpawns(builder, definition.Spawns, includeEmptySpawnList);
    }

    private static void AppendEventConditions(StringBuilder builder, EventConditionsDefinition? conditions, bool includeEmptyFields)
    {
        if (conditions == null)
        {
            return;
        }

        AppendYamlLine(builder, 1, "conditions:");
        AppendYamlOptionalInlineListLine(builder, 2, "biomes", conditions.Biomes);
        AppendYamlOptionalRawListLine(builder, 2, "playerBase", conditions.PlayerBase);
        AppendYamlOptionalInlineListLine(builder, 2, "requiredEnvironments", conditions.RequiredEnvironments);
        AppendYamlOptionalRawListLine(builder, 2, "players", conditions.Players);
        AppendYamlOptionalInlineListLine(builder, 2, "requiredGlobalKeys", conditions.RequiredGlobalKeys);
        AppendYamlOptionalInlineListLine(builder, 2, "forbiddenGlobalKeys", conditions.ForbiddenGlobalKeys);
        AppendYamlOptionalInlineListLine(builder, 2, "requiredKnownItems", conditions.RequiredKnownItems);
        AppendYamlOptionalInlineListLine(builder, 2, "forbiddenKnownItems", conditions.ForbiddenKnownItems);
        AppendYamlOptionalInlineListLine(builder, 2, "requiredPlayerKeysAny", conditions.RequiredPlayerKeysAny);
        AppendYamlOptionalInlineListLine(builder, 2, "requiredPlayerKeysAll", conditions.RequiredPlayerKeysAll);
        AppendYamlOptionalInlineListLine(builder, 2, "forbiddenPlayerKeys", conditions.ForbiddenPlayerKeys);
    }

    private static void AppendEventSpawns(StringBuilder builder, List<EventSpawnDefinition>? spawns, bool includeEmptyFields)
    {
        if (spawns == null)
        {
            return;
        }

        if (spawns.Count == 0)
        {
            if (includeEmptyFields)
            {
                AppendYamlLine(builder, 1, "spawns: []");
            }

            return;
        }

        AppendYamlLine(builder, 1, "spawns:");
        foreach (EventSpawnDefinition spawn in spawns)
        {
            AppendYamlListEntryLine(builder, 2, "prefab", spawn.Prefab);
            AppendYamlOptionalBoolLine(builder, 3, "enabled", spawn.Enabled);
            SpawnSystemManager.AppendYamlSpawnSystemPayloadBlock(
                builder,
                3,
                ConvertEventSpawnToSpawnSystemEntry(spawn),
                new SpawnSystem.SpawnData(),
                includeEmptyPlaceholder: false);
        }
    }

    private static CanonicalSpawnSystemEntry ConvertEventSpawnToSpawnSystemEntry(EventSpawnDefinition spawn)
    {
        return new CanonicalSpawnSystemEntry
        {
            Prefab = spawn.Prefab,
            Enabled = spawn.Enabled ?? true,
            SpawnSystem = spawn.SpawnSystem
        };
    }

    private static void AppendYamlLine(StringBuilder builder, int indent, string text)
    {
        builder.Append(' ', indent * 2);
        builder.AppendLine(text);
    }

    private static void AppendYamlListEntryLine(StringBuilder builder, int indent, string key, string? value)
    {
        builder.Append(' ', indent * 2);
        builder.Append("- ").Append(key).Append(": ").AppendLine(FormatYamlString(value));
    }

    private static void AppendYamlStringLine(StringBuilder builder, int indent, string key, string? value)
    {
        builder.Append(' ', indent * 2);
        builder.Append(key).Append(": ").AppendLine(FormatYamlString(value));
    }

    private static void AppendYamlOptionalStringLine(StringBuilder builder, int indent, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            AppendYamlStringLine(builder, indent, key, value);
        }
    }

    private static void AppendYamlOptionalBoolLine(StringBuilder builder, int indent, string key, bool? value)
    {
        if (value.HasValue)
        {
            AppendYamlLine(builder, indent, $"{key}: {FormatYamlBool(value.Value)}");
        }
    }

    private static void AppendYamlOptionalFloatLine(StringBuilder builder, int indent, string key, float? value)
    {
        if (value.HasValue)
        {
            AppendYamlLine(builder, indent, $"{key}: {FormatFloat(value.Value)}");
        }
    }

    private static void AppendYamlOptionalInlineListLine(StringBuilder builder, int indent, string key, List<string>? values)
    {
        if (values != null)
        {
            AppendYamlLine(builder, indent, $"{key}: {FormatYamlInlineList(values)}");
        }
    }

    private static void AppendYamlOptionalRawListLine(StringBuilder builder, int indent, string key, List<string>? values)
    {
        if (values != null)
        {
            AppendYamlLine(builder, indent, $"{key}: {FormatYamlInlineRawList(values)}");
        }
    }

    private static void AppendYamlOptionalFloatListLine(StringBuilder builder, int indent, string key, List<float>? values)
    {
        if (values != null)
        {
            AppendYamlLine(builder, indent, $"{key}: [{string.Join(", ", values.Select(FormatFloat))}]");
        }
    }

    private static string FormatYamlInlineList(List<string>? values)
    {
        return values == null || values.Count == 0
            ? "[]"
            : $"[{string.Join(", ", values.Select(FormatYamlString))}]";
    }

    private static string FormatYamlInlineRawList(List<string>? values)
    {
        return values == null || values.Count == 0
            ? "[]"
            : $"[{string.Join(", ", values.Select(value => string.IsNullOrWhiteSpace(value) ? "''" : value.Trim()))}]";
    }

    private static string FormatYamlBool(bool value) => value ? "true" : "false";

    private static string FormatYamlString(string? value)
    {
        string normalized = value ?? "";
        if (normalized.Length == 0)
        {
            return "''";
        }

        bool plainSafe = normalized.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '_' or '-' or '.' or '$' or '/');
        if (plainSafe &&
            !bool.TryParse(normalized, out _) &&
            !string.Equals(normalized, "null", StringComparison.OrdinalIgnoreCase) &&
            !float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return normalized;
        }

        return $"'{normalized.Replace("'", "''")}'";
    }

    private static EventDefinition ConvertToDefinition(RandomEvent ev, bool includeEventDefaults, bool includeSpawnDefaults)
    {
        RandomEvent defaults = new();
        List<SpawnSystem.SpawnData> spawns = ev.m_spawn ?? new List<SpawnSystem.SpawnData>();
        EventRuntimeMetadata? metadata = EventMetadata.TryGetValue(ev, out EventRuntimeMetadata foundMetadata) ? foundMetadata : null;
        EventDefinition definition = new()
        {
            Event = ev.m_name,
            Settings = includeEventDefaults ||
                       ev.m_enabled != defaults.m_enabled ||
                       ev.m_random != defaults.m_random ||
                       !FloatEquals(ev.m_duration, defaults.m_duration) ||
                       !FloatEquals(ev.m_eventRange, defaults.m_eventRange) ||
                       ev.m_pauseIfNoPlayerInArea != defaults.m_pauseIfNoPlayerInArea
                ? new List<string>
                {
                    ev.m_enabled.ToString().ToLowerInvariant(),
                    ev.m_random.ToString().ToLowerInvariant(),
                    FormatFloat(ev.m_duration),
                    FormatFloat(ev.m_eventRange),
                    ev.m_pauseIfNoPlayerInArea.ToString().ToLowerInvariant()
                }
                : null,
            Standalone = includeEventDefaults || !FloatEquals(ev.m_standaloneInterval, defaults.m_standaloneInterval) ||
                         !FloatEquals(ev.m_standaloneChance, defaults.m_standaloneChance)
                ? new List<float> { ev.m_standaloneInterval, ev.m_standaloneChance }
                : null,
            SpawnerDelay = includeEventDefaults || !FloatEquals(ev.m_spawnerDelay, defaults.m_spawnerDelay) ? ev.m_spawnerDelay : null,
            Conditions = ConvertConditions(ev, metadata, includeEventDefaults),
            Messages = includeEventDefaults || !string.IsNullOrWhiteSpace(ev.m_startMessage) || !string.IsNullOrWhiteSpace(ev.m_endMessage)
                ? new List<string> { ev.m_startMessage ?? "", ev.m_endMessage ?? "" }
                : null,
            ForceEnvironment = includeEventDefaults ? ev.m_forceEnvironment ?? "" : !string.IsNullOrWhiteSpace(ev.m_forceEnvironment) ? ev.m_forceEnvironment : null,
            ForceMusic = includeEventDefaults ? ev.m_forceMusic ?? "" : !string.IsNullOrWhiteSpace(ev.m_forceMusic) ? ev.m_forceMusic : null,
            StartCommands = includeEventDefaults || (metadata?.StartCommands?.Count ?? 0) > 0
                ? metadata?.StartCommands?.ToList() ?? new List<string>()
                : null,
            EndCommands = includeEventDefaults || (metadata?.EndCommands?.Count ?? 0) > 0
                ? metadata?.EndCommands?.ToList() ?? new List<string>()
                : null,
            Spawns = spawns
                .Where(spawn => spawn != null)
                .Select(spawn => ConvertSpawnDefinition(spawn, includeSpawnDefaults))
                .ToList()
        };

        if (!includeSpawnDefaults && definition.Spawns.Count == 0)
        {
            definition.Spawns = null;
        }

        return definition;
    }

    private static EventConditionsDefinition? ConvertConditions(RandomEvent ev, EventRuntimeMetadata? metadata, bool full)
    {
        List<string> playerBase = ConvertPlayerBaseCondition(metadata?.PlayerBase, ev.m_nearBaseOnly);
        RandomEvent defaults = new();
        List<string> biomes = ConvertBiomes(ev.m_biome);
        List<string> requiredEnvironments = metadata?.RequiredEnvironments?.ToList() ?? new List<string>();
        IntRangeDefinition? playerLimit = metadata?.PlayerLimit != null ? CloneRange(metadata.PlayerLimit) : null;
        List<string> globalRequired = ev.m_requiredGlobalKeys ?? new List<string>();
        List<string> globalBlocked = ev.m_notRequiredGlobalKeys ?? new List<string>();
        List<ItemDrop> knownItems = ev.m_altRequiredKnownItems ?? new List<ItemDrop>();
        List<string> playerAny = ev.m_altRequiredPlayerKeysAny ?? new List<string>();
        List<string> playerAll = ev.m_altRequiredPlayerKeysAll ?? new List<string>();
        List<ItemDrop> blockedKnownItems = ev.m_altRequiredNotKnownItems ?? new List<ItemDrop>();
        List<string> playerBlocked = ev.m_altNotRequiredPlayerKeys ?? new List<string>();
        bool hasReferenceConditions =
            metadata?.HasConditionValues() == true ||
            ev.m_biome != defaults.m_biome ||
            ev.m_nearBaseOnly ||
            requiredEnvironments.Count > 0 ||
            (playerLimit?.HasValues() ?? false) ||
            HasKeyRequirements(ev);

        if (!full && !hasReferenceConditions)
        {
            return null;
        }

        return new EventConditionsDefinition
        {
            Biomes = full || ev.m_biome != defaults.m_biome ? biomes : null,
            PlayerBase = full || ev.m_nearBaseOnly || metadata?.PlayerBase != null ? playerBase : null,
            RequiredEnvironments = full || requiredEnvironments.Count > 0 ? requiredEnvironments : null,
            Players = full || (playerLimit?.HasValues() ?? false)
                ? new List<string>
                {
                    RangeFormatting.FormatShorthand(playerLimit ?? RangeFormatting.From(0, null)),
                    FormatFloat(metadata?.PlayerDistance ?? 100f)
                }
                : null,
            RequiredGlobalKeys = full || globalRequired.Count > 0 ? globalRequired.ToList() : null,
            ForbiddenGlobalKeys = full || globalBlocked.Count > 0 ? globalBlocked.ToList() : null,
            RequiredPlayerKeysAny = full || playerAny.Count > 0 ? playerAny.ToList() : null,
            RequiredPlayerKeysAll = full || playerAll.Count > 0 ? playerAll.ToList() : null,
            ForbiddenPlayerKeys = full || playerBlocked.Count > 0 ? playerBlocked.ToList() : null,
            RequiredKnownItems = full || knownItems.Count > 0 ? ConvertItemDrops(knownItems) : null,
            ForbiddenKnownItems = full || blockedKnownItems.Count > 0 ? ConvertItemDrops(blockedKnownItems) : null
        };
    }

    private static List<string> ConvertPlayerBaseCondition(PlayerBaseCondition? condition, bool nearBaseOnly)
    {
        if (condition == null)
        {
            return nearBaseOnly ? new List<string> { "near" } : new List<string> { "near", "away" };
        }

        if (condition.AllowNear && condition.AllowAway)
        {
            return new List<string> { "near", "away" };
        }

        if (condition.AllowNear)
        {
            return new List<string> { "near" };
        }

        if (condition.AllowAway)
        {
            return new List<string> { "away" };
        }

        return new List<string> { "near", "away" };
    }

    private static bool HasKeyRequirements(RandomEvent ev)
    {
        return (ev.m_requiredGlobalKeys?.Count ?? 0) > 0 ||
               (ev.m_notRequiredGlobalKeys?.Count ?? 0) > 0 ||
               (ev.m_altRequiredKnownItems?.Count ?? 0) > 0 ||
               (ev.m_altRequiredPlayerKeysAny?.Count ?? 0) > 0 ||
               (ev.m_altRequiredPlayerKeysAll?.Count ?? 0) > 0 ||
               (ev.m_altRequiredNotKnownItems?.Count ?? 0) > 0 ||
               (ev.m_altNotRequiredPlayerKeys?.Count ?? 0) > 0;
    }

    private static EventSpawnDefinition ConvertSpawnDefinition(SpawnSystem.SpawnData data, bool full)
    {
        if (!full)
        {
            CanonicalSpawnSystemEntry referenceEntry = SpawnSystemManager.CreateReferenceEntryForExternalProjection(data);
            return new EventSpawnDefinition
            {
                Prefab = referenceEntry.Prefab,
                Enabled = referenceEntry.Enabled ? null : false,
                SpawnSystem = referenceEntry.SpawnSystem
            };
        }

        return new EventSpawnDefinition
        {
            Prefab = GetPrefabName(data.m_prefab),
            Enabled = data.m_enabled,
            SpawnSystem = new SpawnSystemSpawnDefinition
            {
                Name = NormalizeOptionalString(data.m_name),
                HuntPlayer = data.m_huntPlayer,
                Level = RangeFormatting.From(data.m_minLevel, data.m_maxLevel),
                LevelUpMinCenterDistance = data.m_levelUpMinCenterDistance,
                OverrideLevelUpChance = data.m_overrideLevelupChance,
                GroundOffset = data.m_groundOffset,
                GroundOffsetRandom = data.m_groundOffsetRandom,
                SpawnInterval = data.m_spawnInterval,
                SpawnChance = data.m_spawnChance,
                SpawnRadius = RangeFormatting.From(data.m_spawnRadiusMin, data.m_spawnRadiusMax),
                GroupSize = RangeFormatting.From(data.m_groupSizeMin, data.m_groupSizeMax),
                GroupRadius = data.m_groupRadius,
                NoSpawnRadius = data.m_spawnDistance,
                MaxSpawned = data.m_maxSpawned,
                Tilt = RangeFormatting.From(data.m_minTilt, data.m_maxTilt),
                Altitude = RangeFormatting.From(data.m_minAltitude, data.m_maxAltitude),
                OceanDepth = RangeFormatting.From(data.m_minOceanDepth, data.m_maxOceanDepth),
                DistanceFromCenter = RangeFormatting.From(data.m_minDistanceFromCenter, data.m_maxDistanceFromCenter),
                Biomes = ConvertBiomes(data.m_biome),
                BiomeAreas = ConvertBiomeAreas(data.m_biomeArea),
                TimeOfDay = TimeOfDayFormatting.FromSpawnFlags(data.m_spawnAtDay, data.m_spawnAtNight),
                RequiredEnvironments = (data.m_requiredEnvironments ?? new List<string>()).Select(value => value.Trim()).Where(value => value.Length > 0).ToList(),
                RequiredGlobalKey = NormalizeOptionalString(data.m_requiredGlobalKey),
                InLava = ConvertExclusiveZoneToggle(data.m_inLava, data.m_outsideLava),
                InForest = ConvertExclusiveZoneToggle(data.m_inForest, data.m_outsideForest),
                InsidePlayerBase = data.m_insidePlayerBase,
                CanSpawnCloseToPlayer = data.m_canSpawnCloseToPlayer
            }
        };
    }

    private static bool? ConvertExclusiveZoneToggle(bool inside, bool outside)
    {
        if (inside && !outside)
        {
            return true;
        }

        if (!inside && outside)
        {
            return false;
        }

        return null;
    }

    internal static bool TryCheckBase(RandomEvent ev, RandEventSystem.PlayerEventData player, out bool result)
    {
        result = false;
        if (!TryGetMetadata(ev, out EventRuntimeMetadata metadata) || metadata.PlayerBase == null)
        {
            return false;
        }

        bool nearBase = player.baseValue >= 3;
        result = nearBase ? metadata.PlayerBase.AllowNear : metadata.PlayerBase.AllowAway;
        return true;
    }

    internal static bool PassesExtraChecks(RandomEvent ev, Vector3 point, bool currentResult)
    {
        if (!currentResult)
        {
            return false;
        }

        if (!TryGetMetadata(ev, out EventRuntimeMetadata metadata))
        {
            return true;
        }

        return PassesEnvironmentCheck(point, metadata.RequiredEnvironments) &&
               PassesPlayerCountCheck(point, metadata.PlayerLimit, metadata.PlayerDistance);
    }

    internal static void RunStartCommands(RandomEvent ev)
    {
        if (TryGetMetadata(ev, out EventRuntimeMetadata metadata))
        {
            RunCommands(metadata.StartCommands, ev.m_pos);
        }
    }

    internal static void RunEndCommands(RandomEvent ev)
    {
        if (TryGetMetadata(ev, out EventRuntimeMetadata metadata))
        {
            RunCommands(metadata.EndCommands, ev.m_pos);
        }
    }

    private static bool TryGetMetadata(RandomEvent? ev, out EventRuntimeMetadata metadata)
    {
        metadata = null!;
        if (ev == null)
        {
            return false;
        }

        if (EventMetadata.TryGetValue(ev, out metadata))
        {
            return true;
        }

        if (RandEventSystem.instance == null || string.IsNullOrWhiteSpace(ev.m_name))
        {
            return false;
        }

        RandomEvent registered = RandEventSystem.instance.GetEvent(ev.m_name);
        return registered != null && EventMetadata.TryGetValue(registered, out metadata);
    }

    private static bool PassesEnvironmentCheck(Vector3 point, List<string>? requiredEnvironments)
    {
        if (requiredEnvironments == null || requiredEnvironments.Count == 0)
        {
            return true;
        }

        if (WorldGenerator.instance == null || EnvMan.instance == null || ZNet.instance == null)
        {
            return false;
        }

        Heightmap.Biome biome = WorldGenerator.instance.GetBiome(point);
        List<EnvEntry> availableEnvironments = EnvMan.instance.GetAvailableEnvironments(biome);
        if (availableEnvironments == null || availableEnvironments.Count == 0)
        {
            return false;
        }

        UnityEngine.Random.State state = UnityEngine.Random.state;
        UnityEngine.Random.InitState((int)(ZNet.instance.GetTimeSeconds() / EnvMan.instance.m_environmentDuration));
        EnvSetup selectedEnvironment = EnvMan.instance.SelectWeightedEnvironment(availableEnvironments);
        UnityEngine.Random.state = state;
        return selectedEnvironment != null &&
               requiredEnvironments.Contains((selectedEnvironment.m_name ?? "").ToLowerInvariant());
    }

    private static bool PassesPlayerCountCheck(Vector3 point, IntRangeDefinition? limit, float distance)
    {
        if (limit == null || !limit.HasValues())
        {
            return true;
        }

        int count = RandEventSystem.s_playerEventDatas
            .Count(player => Utils.DistanceXZ(point, player.position) <= distance);
        return RangeContains(limit, count, 0, int.MaxValue);
    }

    private static void RunCommands(List<string>? commands, Vector3 position)
    {
        List<string> commandList = NormalizeOptionalStringList(commands) ?? new List<string>();
        if (commandList.Count == 0 || ZNet.instance is { } zNet && !zNet.IsServer())
        {
            return;
        }

        MethodInfo? runMethod = GetExpandWorldCommandRunMethod();
        if (runMethod == null)
        {
            if (!MissingCommandManagerWarningLogged)
            {
                MissingCommandManagerWarningLogged = true;
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning("Event commands require ExpandWorldData.CommandManager, but it was not found.");
            }

            return;
        }

        try
        {
            Quaternion identity = Quaternion.identity;
            runMethod.Invoke(null, new object[] { commandList, position, identity.eulerAngles });
        }
        catch (Exception ex)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Failed to run event commands: {ex.Message}");
        }
    }

    private static MethodInfo? GetExpandWorldCommandRunMethod()
    {
        if (ExpandWorldCommandRunMethodLookedUp)
        {
            return ExpandWorldCommandRunMethod;
        }

        ExpandWorldCommandRunMethodLookedUp = true;
        Type? commandManagerType = SafeTypeLookup.FindLoadedType("ExpandWorldData.CommandManager", "ExpandWorldData");
        ExpandWorldCommandRunMethod = commandManagerType?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(method => method.Name == "Run" && method.GetParameters().Length == 3);
        return ExpandWorldCommandRunMethod;
    }

    private static EventRuntimeMetadata CloneMetadata(EventRuntimeMetadata metadata)
    {
        return new EventRuntimeMetadata
        {
            PlayerBase = ClonePlayerBaseCondition(metadata.PlayerBase),
            RequiredEnvironments = metadata.RequiredEnvironments?.ToList(),
            PlayerLimit = CloneRange(metadata.PlayerLimit),
            PlayerDistance = metadata.PlayerDistance,
            StartCommands = metadata.StartCommands?.ToList(),
            EndCommands = metadata.EndCommands?.ToList()
        };
    }

    private static PlayerBaseCondition? ClonePlayerBaseCondition(PlayerBaseCondition? condition)
    {
        return condition == null
            ? null
            : new PlayerBaseCondition
            {
                AllowNear = condition.AllowNear,
                AllowAway = condition.AllowAway
            };
    }

    private static IntRangeDefinition? CloneRange(IntRangeDefinition? range)
    {
        if (range == null)
        {
            return null;
        }

        int? min = range.Min;
        int? max = range.Max;
        _ = RangeFormatting.NormalizeAscending(ref min, ref max);
        return new IntRangeDefinition
        {
            Min = min,
            Max = max
        };
    }

    private static bool RangeContains(IntRangeDefinition range, int value, int fallbackMin, int fallbackMax)
    {
        int min = range.Min ?? fallbackMin;
        int max = range.Max ?? fallbackMax;
        if (min > max)
        {
            (min, max) = (max, min);
        }

        return value >= min && value <= max;
    }

    private static List<RandomEvent> CloneEvents(IEnumerable<RandomEvent> events)
    {
        return events.Select(ev => ev.Clone()).ToList();
    }

    private static bool TryParsePlayerBaseCondition(List<string>? values, string context, out PlayerBaseCondition? condition)
    {
        condition = null;
        if (values == null)
        {
            return false;
        }

        if (values.Count == 0)
        {
            return false;
        }

        PlayerBaseCondition parsed = new();
        foreach (string value in values)
        {
            if (string.Equals(value, "near", StringComparison.OrdinalIgnoreCase))
            {
                parsed.AllowNear = true;
                continue;
            }

            if (string.Equals(value, "away", StringComparison.OrdinalIgnoreCase))
            {
                parsed.AllowAway = true;
                continue;
            }

            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                $"Event entry '{context}' contains invalid conditions.playerBase value '{value}'. Use [near], [away], or [near, away].");
            return false;
        }

        if (!parsed.AllowNear && !parsed.AllowAway)
        {
            return false;
        }

        condition = parsed;
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

            if (!BiomeResolutionSupport.TryResolveBiomeToken(name, out Heightmap.Biome parsedBiome))
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Event entry '{context}' contains unknown biome '{name}'.");
                biomes = Heightmap.Biome.None;
                return false;
            }

            biomes |= parsedBiome;
        }

        return true;
    }

    private static List<string> ConvertBiomes(Heightmap.Biome biomes)
    {
        return BiomeResolutionSupport.ConvertBiomeMaskToNames(biomes);
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

    private static List<ItemDrop> ResolveItemDrops(List<string> names, string context)
    {
        List<ItemDrop> items = new();
        foreach (string name in names)
        {
            ItemDrop? itemDrop = ResolveItemDrop(name);
            if (itemDrop == null)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Event entry '{context}' references unknown player item '{name}'.");
                continue;
            }

            items.Add(itemDrop);
        }

        return items;
    }

    private static ItemDrop? ResolveItemDrop(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        GameObject? prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name) : null;
        if (prefab == null && ZNetScene.instance != null)
        {
            prefab = ZNetScene.instance.GetPrefab(name);
        }

        return prefab != null ? prefab.GetComponent<ItemDrop>() : null;
    }

    private static List<string> ConvertItemDrops(List<ItemDrop> itemDrops)
    {
        return (itemDrops ?? new List<ItemDrop>())
            .Where(item => item != null)
            .Select(item => item.gameObject != null ? item.gameObject.name : "")
            .Where(name => name.Length > 0)
            .ToList();
    }

    private static bool TryParseIntRange(string? raw, string context, string fieldName, out IntRangeDefinition range)
    {
        range = new IntRangeDefinition();
        try
        {
            (int? min, int? max) = RangeFormatting.ParseIntRange(raw);
            _ = RangeFormatting.NormalizeAscending(ref min, ref max);
            range.Min = min;
            range.Max = max;
            return range.HasValues();
        }
        catch (Exception ex)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Event entry '{context}' contains invalid {fieldName} range '{raw}': {ex.Message}");
            return false;
        }
    }

    private static bool TryParseFloat(List<string> values, int index, float fallback, string context, string fieldName, out float result)
    {
        result = fallback;
        if (index >= values.Count || string.IsNullOrWhiteSpace(values[index]))
        {
            return false;
        }

        if (float.TryParse(values[index], NumberStyles.Any, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Event entry '{context}' contains invalid {fieldName} value '{values[index]}'.");
        return false;
    }

    private static bool TryParseBool(List<string> values, int index, bool fallback, string context, string fieldName, out bool result)
    {
        result = fallback;
        if (index >= values.Count || string.IsNullOrWhiteSpace(values[index]))
        {
            return false;
        }

        if (bool.TryParse(values[index], out result))
        {
            return true;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Event entry '{context}' contains invalid {fieldName} value '{values[index]}'.");
        return false;
    }

    private static string? NormalizeOptionalString(string? value)
    {
        if (value == null)
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? NormalizeOptionalStringPreserveEmpty(string? value)
    {
        return value?.Trim();
    }

    private static List<string>? NormalizePositionalStringList(List<string>? values)
    {
        return values == null || values.Count == 0
            ? null
            : values.Select(value => (value ?? "").Trim()).ToList();
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

    private static bool FloatEquals(float left, float right)
    {
        return Math.Abs(left - right) < 0.0001f;
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string? GetPrefabName(GameObject? prefab)
    {
        return prefab == null ? null : prefab.name;
    }

    private static string BuildDefaultOverrideYaml()
    {
        StringBuilder builder = new();
        builder.AppendLine("# DropNSpawn event overrides.");
        builder.AppendLine("#");
        builder.AppendLine("# Full schema example. The value before the first # is the fallback/default for a new event.");
        builder.AppendLine("# When overriding an existing event, omitted fields keep that event's baseline value.");
        builder.AppendLine("# The first inline comment value is an example override.");
        builder.AppendLine("#");
        builder.AppendLine("# - event: ''                          # custom_greydwarf_raid # Event id/name. Required.");
        builder.AppendLine("#   settings: [true, true, 60, 96, true] # [false, true, 120, 128, false] # enabled, random, duration, eventRange, pauseIfNoPlayerInArea; use '' to omit a middle value.");
        builder.AppendLine("#   standalone: [0, 100]               # [600, 20] # standaloneInterval seconds, standaloneChance percent.");
        builder.AppendLine("#   spawnerDelay: 0                    # 10 # Delay before event spawners can start spawning.");
        builder.AppendLine("#   conditions:");
        builder.AppendLine("#     biomes: []                       # [Meadows, BlackForest] # Biomes where this event can start. [] means any biome.");
        builder.AppendLine("#     playerBase: [near, away]         # [near] # Allowed player-base states. [] or omitted keeps the baseline value.");
        builder.AppendLine("#     requiredEnvironments: []         # [ThunderStorm] # Event-level required environment names.");
        builder.AppendLine("#     players: [0~, 100]               # [1~4, 100] # Player count range, distance.");
        builder.AppendLine("#     requiredGlobalKeys: []           # [defeated_eikthyr] # Required global keys.");
        builder.AppendLine("#     forbiddenGlobalKeys: []          # [defeated_gdking] # Blocking global keys.");
        builder.AppendLine("#     requiredKnownItems: []           # [CryptKey] # Known item prefabs required for PlayerEvents gating.");
        builder.AppendLine("#     forbiddenKnownItems: []          # [Wishbone] # Known item prefabs that block PlayerEvents gating.");
        builder.AppendLine("#     requiredPlayerKeysAny: []        # [KilledTroll] # Any one player key can satisfy PlayerEvents gating.");
        builder.AppendLine("#     requiredPlayerKeysAll: []        # [KilledTroll, defeated_eikthyr] # All listed player keys are required.");
        builder.AppendLine("#     forbiddenPlayerKeys: []          # [defeated_dragon] # Player keys that block PlayerEvents gating.");
        builder.AppendLine("#   messages: ['', '']                 # ['$event_gdkingarmy_start', '$event_gdkingarmy_end'] # startMessage, endMessage.");
        builder.AppendLine("#   forceEnvironment: ''               # GDKing # Force an environment while the event is active.");
        builder.AppendLine("#   forceMusic: ''                     # boss_gdking # Force music while the event is active.");
        builder.AppendLine("#   startCommands: []                  # ['say Event started'] # ExpandWorldData commands run when the event starts.");
        builder.AppendLine("#   endCommands: []                    # ['say Event ended'] # ExpandWorldData commands run when the event ends.");
        builder.AppendLine("#   spawns: []                         # See spawn entry schema below. [] means no spawns for a new event.");
        builder.AppendLine("#");
        builder.AppendLine("# Advanced standalone/command example. These fields are valid in overrides but are omitted from generated reference files.");
        builder.AppendLine("# - event: custom_greydwarf_raid");
        builder.AppendLine("#   standalone: [600, 20]");
        builder.AppendLine("#   spawnerDelay: 10");
        builder.AppendLine("#   startCommands: ['say Event started']");
        builder.AppendLine("#   endCommands: ['say Event ended']");
        builder.AppendLine("#");
        builder.AppendLine("# Spawn entry schema. Values before # are SpawnSystem.SpawnData defaults for one entry.");
        builder.AppendLine("# All per-spawn fields are flat under spawnSystem; nested conditions and modifiers blocks are not supported.");
        builder.AppendLine("# - event: custom_greydwarf_raid");
        builder.AppendLine("#   spawns:");
        builder.AppendLine("#     - prefab: ''                     # Greydwarf # Creature prefab to spawn. Required for a spawn entry.");
        builder.AppendLine("#       enabled: true                  # false # Enables this spawn entry.");
        builder.AppendLine("#       spawnSystem:");
        builder.AppendLine("#         name: ''                     # Greydwarf raid scout # Optional display/debug name.");
        builder.AppendLine("#         huntPlayer: false            # true # Force spawned creatures to hunt players.");
        builder.AppendLine("#         level: 1                     # 1~2 # Min~max creature level.");
        builder.AppendLine("#         overrideLevelUpChance: -1    # 25 # Override level-up chance percent. -1 uses vanilla/default.");
        builder.AppendLine("#         levelUpMinCenterDistance: 0  # 2000 # Min distance from world center for level-up.");
        builder.AppendLine("#         groundOffset: 0.5            # 0 # Vertical spawn offset.");
        builder.AppendLine("#         groundOffsetRandom: 0        # 1 # Random additional vertical offset.");
        builder.AppendLine("#         spawnInterval: 4             # 10 # Seconds between spawn checks.");
        builder.AppendLine("#         spawnChance: 100             # 50 # Chance percent per interval.");
        builder.AppendLine("#         spawnRadius: 40~80           # 20~60 # Min~max spawn radius. Native effective default; 0 on min/max uses the native 40/80 fallback.");
        builder.AppendLine("#         groupSize: 1                 # 1~3 # Min~max group size.");
        builder.AppendLine("#         groupRadius: 3               # 6 # Radius around the group center.");
        builder.AppendLine("#         noSpawnRadius: 10            # 20 # Minimum distance to another instance of this prefab.");
        builder.AppendLine("#         maxSpawned: 1                # 6 # Max active event creatures from this entry.");
        builder.AppendLine("#         tilt: 0~35                   # 0~25 # Terrain tilt range.");
        builder.AppendLine("#         altitude: -1000~1000         # 1~1000 # Altitude range.");
        builder.AppendLine("#         oceanDepth: 0~0              # 1~30 # Ocean depth range.");
        builder.AppendLine("#         distanceFromCenter: 0~0      # 500~ # Distance from world center.");
        builder.AppendLine("#         biomes: []                   # [Meadows] # Spawn-entry biome filter. [] means no extra entry filter.");
        builder.AppendLine("#         biomeAreas: [Everything]     # [Median] # Biome area filter.");
        builder.AppendLine("#         timeOfDay: [day, night]      # [night] # Time of day filter.");
        builder.AppendLine("#         requiredEnvironments: []     # [Rain] # Spawn-entry required environments.");
        builder.AppendLine("#         requiredGlobalKey: ''        # defeated_eikthyr # Required global key for this spawn entry.");
        builder.AppendLine("#         inLava:                      # true # true = only lava, false = outside lava, empty = vanilla both-state default.");
        builder.AppendLine("#         inForest:                    # true # true = only forest, false = outside forest, empty = vanilla both-state default.");
        builder.AppendLine("#         insidePlayerBase: false      # true # Spawn inside player base.");
        builder.AppendLine("#         canSpawnCloseToPlayer: false # true # Allow spawn close to players.");
        builder.AppendLine("#         fields: {}                   # { m_character.m_faction: ForestMonsters } # Raw field overrides.");
        builder.AppendLine("#         objects: []                  # [Wood,0,0,0,1] # ExpandWorldData object entries.");
        builder.AppendLine("#         data: ''                     # my_spawn_data # ExpandWorldData data entry name.");
        builder.AppendLine("#         faction: ''                  # ForestMonsters # ExpandWorldData faction override.");
        return builder.ToString();
    }
}
