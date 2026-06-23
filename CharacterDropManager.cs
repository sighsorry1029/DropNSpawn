using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DropNSpawn;

/// <summary>
/// Character domain front door and orchestrator.
/// Parsing, compile orchestration, and apply entrypoints live here; specialized runtimes own compiled and live drop state.
/// </summary>
internal static partial class CharacterDropManager
{
    private const string ReferenceAutoUpdateStateKey = "character";
    private const float GlobalTrophyAmountBonusPerLevel = 0.5f;
    internal static readonly DomainModuleDefinition<CharacterDropPrefabEntry> Module =
        new(new DomainModuleOptions<CharacterDropPrefabEntry>
        {
            DomainKey = "character",
            ReloadDomain = DropNSpawnPlugin.ReloadDomain.Character,
            ManifestSettingKey = "character_yaml",
            ManifestPriority = 99,
            ShouldReloadForPath = ShouldReloadForPath,
            Reload = ReloadConfiguration,
            InitializeRuntime = Initialize,
            OnGameDataReady = OnGameDataReady,
            HandleExpandWorldDataReady = HandleExpandWorldDataReady,
            DtoVersion = 10,
            TransportProfile = DomainTransportProfile.SmallConfig,
            DisplayName = "character",
            CacheDirectoryName = "character",
            ClientRequestPriority = 10,
            KeySelector = entry => entry.RuleId,
            ApplyPayloadAction = ApplySyncedPayload,
            WorkKinds = DomainWorkKinds.Runtime | DomainWorkKinds.SnapshotBuild,
            HasPendingSnapshotBuildWork = HasPendingSnapshotBuildWork,
            GetPendingSnapshotBuildWorkCount = GetPendingSnapshotBuildWorkCount,
            ProcessPendingSnapshotBuildStep = ProcessPendingSnapshotBuildStep,
            BeforeClientManifestChanged = MarkSyncedPayloadPending,
            OnClientAuthorityCutover = EnterPendingSyncedPayloadState
    });
    internal static DomainDescriptor<CharacterDropPrefabEntry> Descriptor => Module.DescriptorTyped;
    internal static DomainTransportMetadata<CharacterDropPrefabEntry> TransportMetadata => Module.TransportMetadataTyped;

    private sealed class ResolvedConfiguredDrop
    {
        public GameObject Prefab { get; set; } = null!;
        public int Amount { get; set; }
        public bool DropInStack { get; set; }
    }

    private sealed class SyncedCharacterConfigurationState
    {
        public List<CharacterDropPrefabEntry> Configuration { get; } = new();
        public Dictionary<string, List<CharacterDropPrefabEntry>> ActiveEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ConfiguredCharacterDropPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PrefabsWithCharacterDropOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> EntrySignaturesByPrefab { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string ConfigurationSignature { get; set; } = "";
    }

    private sealed class ParsedCharacterConfigurationDocument
    {
        public List<CharacterDropPrefabEntry> Configuration { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    private sealed class CharacterRuntimeContextSnapshot
    {
        public int Frame { get; set; }
        public int TimeOfDayPhaseMarker { get; set; }
        public string EnvironmentName { get; set; } = "";
        public Dictionary<string, bool> GlobalKeyStates { get; } = new(StringComparer.Ordinal);
    }

    private static readonly object Sync = new();
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    private static readonly CharacterConfigurationRuntimeState RuntimeState = new();
    private static Dictionary<string, List<CharacterDropPrefabEntry>> ActiveEntriesByPrefab => RuntimeState.ActiveEntriesByPrefab;
    private static Dictionary<string, string> CurrentEntrySignaturesByPrefab => RuntimeState.CurrentEntrySignaturesByPrefab;
    private static HashSet<string> ConfiguredCharacterDropPrefabs => RuntimeState.ConfiguredCharacterDropPrefabs;
    private static HashSet<string> PrefabsWithCharacterDropOverrides => RuntimeState.PrefabsWithCharacterDropOverrides;
    private static readonly InvalidEntryDiagnostics InvalidEntryWarnings = new();
    private static readonly System.Reflection.FieldInfo? DropsEnabledField = AccessTools.Field(typeof(CharacterDrop), "m_dropsEnabled");
    [ThreadStatic] private static CharacterDrop? OnePerPlayerScopeCharacterDrop;
    [ThreadStatic] private static int OnePerPlayerScopeDepth;

    private static List<CharacterDropPrefabEntry> _configuration
    {
        get => RuntimeState.Configuration;
        set => RuntimeState.Configuration = value;
    }

    private static string _configurationSignature
    {
        get => RuntimeState.ConfigurationSignature;
        set => RuntimeState.ConfigurationSignature = value;
    }

    private static DomainLoadState LoadState => ConfigurationRuntime.LoadState;
    private static bool _initialized;
    private static CharacterCompiledState _compiledState = CharacterCompiledState.Empty;
    private static string _compiledStateConfigurationSignature = "";
    private static int? _compiledStateGameDataSignature;
    private static bool _referenceArtifactsAutoRefreshConsumed;
    private static readonly Dictionary<string, string> _lastAppliedEntrySignaturesByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static string _lastAppliedConfigurationSignature = "";
    private static int? _lastAppliedGameDataSignature;
    private static bool? _lastAppliedDomainEnabled;
    private static bool _lastAppliedSynchronizedPayloadReady;
    private static bool _synchronizedPayloadReady;
    private static int? _lastCommittedAuthorityEpoch;
    private static CharacterRuntimeContextSnapshot? _runtimeContextSnapshot;
    private static int _cachedFrameGameDataSignatureFrame = -1;
    private static int _cachedFrameGameDataSignatureValue;
    private const string MockPrefabPrefix = "JVLmock_";
    private const float ConfiguredDropGroundOffset = 0.2f;
    private const int MaxCachedRuntimeDropResolutionsPerPrefab = 32;

    private static string ReferenceConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("character")}.reference.yml");
    private static string PrimaryOverrideConfigurationPathYml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("character")}.yml");
    private static string PrimaryOverrideConfigurationPathYaml => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("character")}.yaml");
    private static string FullScaffoldConfigurationPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $"{PluginSettingsFacade.GetYamlDomainFilePrefix("character")}.full.yml");
    private static readonly DomainConfigurationRuntime<CharacterDropPrefabEntry, SyncedCharacterConfigurationState> ConfigurationRuntime =
        new(
            new DomainLoadHooks<CharacterDropPrefabEntry, SyncedCharacterConfigurationState>(
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
            new DomainSyncHooks<CharacterDropPrefabEntry, SyncedCharacterConfigurationState>(
                (out List<CharacterDropPrefabEntry> configuration, out string payloadToken) =>
                    ConfigurationDomainHost.TryGetSyncedEntries(Descriptor, out configuration, out payloadToken),
                payloadToken => ConfigurationDomainHost.ShouldSkipSyncedPayload(
                    LoadState,
                    payloadToken,
                    Volatile.Read(ref _synchronizedPayloadReady)),
                BuildSyncedConfigurationState,
                CommitSyncedConfigurationState,
                state => state.ActiveEntriesByPrefab.Count,
                "ServerSync:DropNSpawnCharacter",
                () => ConfigurationDomainHost.HandleWaitingForSyncedPayload(
                    MarkSyncedPayloadPending),
                LogSyncedCharacterConfigurationLoaded,
                LogSyncedCharacterConfigurationFailure));
    internal static bool ShouldReloadForPath(string? path)
    {
        return PluginSettingsFacade.IsEligibleOverrideConfigurationPath(path) &&
               IsOverrideConfigurationFileName(Path.GetFileName(path ?? ""));
    }

    private static bool ShouldApplyLocally()
    {
        return PluginSettingsFacade.IsCharacterDomainEnabled();
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
            ApplyIfReady();
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
            Dictionary<string, string> previousEntrySignatures = CloneCurrentEntrySignaturesByPrefab();
            HashSet<string> previouslyAppliedPrefabs = BuildLastAppliedPrefabs();
            ConfigurationRuntime.EnterPendingSyncedPayloadState(
                DropNSpawnPlugin.IsSourceOfTruth,
                beforeResetLoadState: ResetLoadedConfigurationState,
                afterResetLoadState: () =>
                {
                    _configurationSignature = "";
                    _lastAppliedSynchronizedPayloadReady = false;
                    RestoreSnapshots(previouslyAppliedPrefabs);
                    RestoreTrackedCharacterDrops(previouslyAppliedPrefabs);
                    RefreshVneiCompatibility(previousEntrySignatures);
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

            string refreshedSignature = NetworkPayloadSyncSupport.ComputeCharacterConfigurationSignature(_configuration);
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
            ApplyIfReady();
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
                ApplyIfReady();
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
            if (CharacterDropRuntime.IsGameDataAlreadyProcessed(gameDataSignature))
            {
                return;
            }

            int snapshotSignature = ComputeSnapshotSignature();
            if (CharacterDropRuntime.NeedsSnapshotBuild(snapshotSignature))
            {
                CharacterDropRuntime.ScheduleSnapshotBuild(source, gameDataSignature, snapshotSignature, EnumerateRelevantPrefabs());
                return;
            }

            CompleteGameDataReadyLocked(source, gameDataSignature, snapshotSignature);
        }
    }

    internal static bool HasPendingSnapshotBuildWork()
    {
        lock (Sync)
        {
            return CharacterDropRuntime.HasPendingSnapshotBuildWork();
        }
    }

    internal static int GetPendingSnapshotBuildWorkCount()
    {
        lock (Sync)
        {
            return CharacterDropRuntime.GetPendingSnapshotBuildWorkCount();
        }
    }

    internal static bool ProcessPendingSnapshotBuildStep(float deadline)
    {
        lock (Sync)
        {
            return CharacterDropRuntime.ProcessPendingSnapshotBuildStep(
                deadline,
                CaptureSnapshot,
                CompleteGameDataReadyLocked);
        }
    }

    private static bool IsGameDataReady()
    {
        return ZNetScene.instance != null && ObjectDB.instance != null;
    }

    private static bool EnsurePrimaryOverrideConfigurationFileExists()
    {
        if (DomainConfigurationFileSupport.HasAnyOverrideConfigurationFile(
                "character",
                PrimaryOverrideConfigurationPathYml,
                PrimaryOverrideConfigurationPathYaml))
        {
            return false;
        }

        if (!IsGameDataReady() || !CharacterDropRuntime.HasSnapshots())
        {
            return false;
        }

        GeneratedArtifactWriter.WriteTextAlways(
            PrimaryOverrideConfigurationPathYml,
            BuildPrimaryOverrideConfigurationTemplate(),
            $"Created character override configuration at {PrimaryOverrideConfigurationPathYml}.");
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

    private static void CompleteGameDataReadyLocked(string source, int gameDataSignature, int snapshotSignature)
    {
        if (DropNSpawnPlugin.IsSourceOfTruth && !_referenceArtifactsAutoRefreshConsumed)
        {
            EnsureReferenceArtifactsUpToDate();
            _referenceArtifactsAutoRefreshConsumed = true;
        }

        if (DropNSpawnPlugin.IsSourceOfTruth && EnsurePrimaryOverrideConfigurationFileExists())
        {
            LoadConfiguration();
        }

        ApplyIfReady();
    }

    private static void ResetLoadedConfigurationState()
    {
        RuntimeState.Reset();
        InvalidEntryWarnings.Clear();
        CharacterDropRuntime.Reset();
        _compiledState = CharacterCompiledState.Empty;
        _compiledStateConfigurationSignature = "";
        _compiledStateGameDataSignature = null;
        _cachedFrameGameDataSignatureFrame = -1;
        _cachedFrameGameDataSignatureValue = 0;
        Volatile.Write(ref _synchronizedPayloadReady, false);
    }

    private static List<CharacterDropPrefabEntry> CloneAndNormalizeConfigurationEntries(
        List<CharacterDropPrefabEntry>? configuration,
        string sourceName)
    {
        List<CharacterDropPrefabEntry> normalizedConfiguration =
            NetworkPayloadSyncSupport.CloneEntries(Descriptor, configuration);
        foreach (CharacterDropPrefabEntry entry in normalizedConfiguration)
        {
            NormalizeEntry(entry);
            entry.SourcePath = string.IsNullOrWhiteSpace(entry.SourcePath) ? sourceName : entry.SourcePath;
        }

        return normalizedConfiguration;
    }

    private static List<CharacterDropPrefabEntry> PrepareLocalConfigurationEntries(
        List<CharacterDropPrefabEntry>? configuration,
        string sourceName,
        List<string> warnings)
    {
        List<CharacterDropPrefabEntry> normalizedConfiguration =
            CloneAndNormalizeConfigurationEntries(configuration, sourceName);
        List<CharacterDropPrefabEntry> acceptedEntries = new();
        foreach (CharacterDropPrefabEntry entry in normalizedConfiguration)
        {
            if (!TryAcceptLocalConfigurationEntry(entry, warnings))
            {
                continue;
            }

            acceptedEntries.Add(entry);
        }

        return acceptedEntries;
    }

    private static bool TryAcceptLocalConfigurationEntry(CharacterDropPrefabEntry entry, List<string> warnings)
    {
        if (!entry.Enabled)
        {
            return true;
        }

        string context = CreateConfigurationContext(entry);
        bool hasDropOverride = entry.CharacterDrop != null;
        if (string.IsNullOrWhiteSpace(entry.Prefab))
        {
            warnings.Add($"Entry '{context}' is missing required prefab.");
            return false;
        }

        if (!hasDropOverride)
        {
            warnings.Add($"Entry '{context}' does not define characterDrop.");
            return false;
        }

        if (!TryResolveConfiguredCharacterPrefab(entry.Prefab, out bool hasCharacterComponent, out bool hasCharacterDropComponent))
        {
            warnings.Add($"Entry '{context}' references unknown character prefab '{entry.Prefab}'.");
            return false;
        }

        if (!hasCharacterComponent)
        {
            warnings.Add($"Entry '{context}' references '{entry.Prefab}', but it is not a Character prefab.");
            return false;
        }

        if (hasDropOverride && !hasCharacterDropComponent)
        {
            warnings.Add($"Entry '{context}' references '{entry.Prefab}', but it is not a CharacterDrop prefab.");
            return false;
        }

        return true;
    }

    private static bool TryResolveConfiguredCharacterPrefab(
        string prefabName,
        out bool hasCharacterComponent,
        out bool hasCharacterDropComponent)
    {
        hasCharacterComponent = true;
        hasCharacterDropComponent = true;
        if (ZNetScene.instance == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return true;
        }

        GameObject? prefab = ZNetScene.instance.GetPrefab(prefabName.Trim());
        if (prefab == null)
        {
            hasCharacterComponent = false;
            hasCharacterDropComponent = false;
            return false;
        }

        hasCharacterComponent = prefab.GetComponent<Character>() != null;
        hasCharacterDropComponent = prefab.GetComponent<CharacterDrop>() != null;
        return true;
    }

    private static SyncedCharacterConfigurationState BuildSyncedConfigurationState(
        List<CharacterDropPrefabEntry> configuration,
        string sourceName)
    {
        using InvalidEntryDiagnostics.SuppressionScope _ = BeginInvalidEntryWarningSuppressionForSyncedClientBuild(sourceName);
        SyncedCharacterConfigurationState state = new();
        foreach (CharacterDropPrefabEntry entry in CloneAndNormalizeConfigurationEntries(configuration, sourceName))
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

        foreach (string prefabName in state.ActiveEntriesByPrefab.Keys)
        {
            state.ConfiguredCharacterDropPrefabs.Add(prefabName);
        }
        RebuildCharacterDropOverridePrefabSet(state.ActiveEntriesByPrefab, state.PrefabsWithCharacterDropOverrides);

        state.ConfigurationSignature = NetworkPayloadSyncSupport.ComputeCharacterConfigurationSignature(state.Configuration);
        state.EntrySignaturesByPrefab = BuildActiveEntrySignaturesByPrefab(state.ActiveEntriesByPrefab);
        return state;
    }

    private static void CommitSyncedConfigurationState(SyncedCharacterConfigurationState state, string payloadToken)
    {
        ResetLoadedConfigurationState();
        _configuration = state.Configuration;
        foreach ((string prefabName, List<CharacterDropPrefabEntry> entries) in state.ActiveEntriesByPrefab)
        {
            ActiveEntriesByPrefab[prefabName] = entries;
        }

        ReplaceEntrySignatures(CurrentEntrySignaturesByPrefab, state.EntrySignaturesByPrefab);
        foreach (string prefabName in state.ConfiguredCharacterDropPrefabs)
        {
            ConfiguredCharacterDropPrefabs.Add(prefabName);
        }
        foreach (string prefabName in state.PrefabsWithCharacterDropOverrides)
        {
            PrefabsWithCharacterDropOverrides.Add(prefabName);
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

    private static LocalLoadResult<CharacterDropPrefabEntry> ParseLocalConfigurationDocuments(
        List<ConfigurationLoadSupport.LocalYamlDocument> documents)
    {
        return ConfigurationLoadSupport.ParseLocalConfigurationDocuments(
            documents,
            (yaml, path) =>
            {
                ParsedCharacterConfigurationDocument parsedDocument = ParseConfiguration(yaml, path);
                return new ConfigurationLoadSupport.ParsedLocalConfiguration<CharacterDropPrefabEntry>(
                    parsedDocument.Configuration,
                    parsedDocument.Warnings);
            },
            PrepareLocalConfigurationEntries,
            FormatYamlExceptionLocation,
            "Character override YAML must start with a root list like '- prefab: ...'.");
    }

    private static void RejectLocalConfigurationPayload(string payload, IEnumerable<string> errors)
    {
        ConfigurationDomainHost.RejectLocalConfigurationPayload(LoadState, payload, errors, "character");
    }

    private static void RebuildCharacterDropOverridePrefabSet(
        Dictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab,
        HashSet<string> target)
    {
        target.Clear();
        foreach ((string prefabName, List<CharacterDropPrefabEntry> entries) in activeEntriesByPrefab)
        {
            if (entries.Any(entry => entry.CharacterDrop != null))
            {
                target.Add(prefabName);
            }
        }
    }

    private static bool RemoveEffectiveConfigurationEntry(
        List<CharacterDropPrefabEntry> configuration,
        Dictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab,
        string prefabName,
        string ruleId)
    {
        bool removed = false;
        for (int index = configuration.Count - 1; index >= 0; index--)
        {
            CharacterDropPrefabEntry existingEntry = configuration[index];
            if (!string.Equals(existingEntry.Prefab, prefabName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existingEntry.RuleId, ruleId, StringComparison.Ordinal))
            {
                continue;
            }

            configuration.RemoveAt(index);
            removed = true;
        }

        if (activeEntriesByPrefab.TryGetValue(prefabName, out List<CharacterDropPrefabEntry>? entries))
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

    private static ParsedCharacterConfigurationDocument ParseConfiguration(string yaml, string? sourcePath)
    {
        ParsedCharacterConfigurationDocument result = new();
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
                "Character override YAML root must be a sequence.");
        }

        foreach (YamlNode node in sequence.Children)
        {
            if (node is not YamlMappingNode mappingNode)
            {
                result.Warnings.Add(
                    $"Skipped character YAML node at {FormatYamlNodeLocation(sourcePath, node.Start)}. Expected a list item object like '- prefab: Fox' but found {DescribeYamlNode(node)}.");
                continue;
            }

            try
            {
                string entryYaml = SerializeYamlNode(mappingNode);
                CharacterDropPrefabEntry entry =
                    Deserializer.Deserialize<CharacterDropPrefabEntry>(entryYaml) ?? new CharacterDropPrefabEntry();
                entry.SourceLine = checked((int)mappingNode.Start.Line);
                entry.SourceColumn = checked((int)mappingNode.Start.Column);
                result.Configuration.Add(entry);
            }
            catch (Exception ex)
            {
                result.Warnings.Add(
                    $"Skipped invalid character entry at {FormatYamlNodeLocation(sourcePath, mappingNode.Start)}. {FormatEntryParseFailure(ex)}");
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

    private static void NormalizeEntry(CharacterDropPrefabEntry entry)
    {
        entry.Prefab = (entry.Prefab ?? "").Trim();
        if (entry.CharacterDrop != null)
        {
            entry.CharacterDrop.Drops ??= new List<CharacterDropEntryDefinition>();
            foreach (CharacterDropEntryDefinition drop in entry.CharacterDrop.Drops)
            {
                drop.Item = (drop.Item ?? "").Trim();
                if (drop.Amount?.HasValues() == true)
                {
                    drop.AmountMin = RangeFormatting.GetMin(drop.Amount, drop.AmountMin);
                    drop.AmountMax = RangeFormatting.GetMax(drop.Amount, drop.AmountMin, drop.AmountMax);
                }
            }
        }

        entry.RuleId = NormalizeOptionalRuleId(entry.RuleId) ?? BuildRuleId(entry);
    }

    private static List<string>? NormalizeStringList(List<string>? values)
    {
        if (values == null)
        {
            return null;
        }

        return values
            .Select(value => (value ?? "").Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildRuleId(CharacterDropPrefabEntry entry)
    {
        CharacterDropPrefabEntry normalizedEntry = new()
        {
            Prefab = entry.Prefab,
            Enabled = true,
            Conditions = entry.Conditions,
            CharacterDrop = entry.CharacterDrop
        };

        return $"{entry.Prefab}:{NetworkPayloadSyncSupport.ComputeCharacterEntryIdentitySignature(normalizedEntry)}";
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

    private static bool HasCustomDropHandling(List<CharacterDropEntryDefinition>? drops)
    {
        return drops?.Any(drop => (drop.AmountLimit.HasValue && drop.AmountLimit.Value >= 0) || drop.DropInStack == true) == true;
    }

    private static bool HasCustomDropHandling(IReadOnlyList<CompiledCharacterDropDefinition>? drops)
    {
        return drops?.Any(drop => (drop.AmountLimit.HasValue && drop.AmountLimit.Value >= 0) || drop.DropInStack) == true;
    }

    internal static void ApplyGlobalDropInStack(ref List<KeyValuePair<GameObject, int>> drops, Vector3 centerPos, float dropArea)
    {
        if (!PluginSettingsFacade.IsGlobalCharacterDropInStackEnabled() || drops.Count == 0)
        {
            return;
        }

        List<KeyValuePair<GameObject, int>> normalDrops = new(drops.Count);
        bool changed = false;
        foreach (KeyValuePair<GameObject, int> drop in drops)
        {
            if (!ShouldDropInStack(drop.Key, drop.Value, explicitDropInStack: false))
            {
                normalDrops.Add(drop);
                continue;
            }

            SpawnStackedDrops(drop.Key, drop.Value, centerPos, dropArea);
            changed = true;
        }

        if (changed)
        {
            drops = normalDrops;
        }
    }

    internal static List<CharacterDrop.Drop>? SuppressGlobalTrophyLevelMultiplierDrops(CharacterDrop characterDrop)
    {
        if (!PluginSettingsFacade.IsGlobalCharacterDropTrophyLevelMultiplierEnabled() ||
            characterDrop == null ||
            characterDrop.m_drops == null ||
            characterDrop.m_drops.Count == 0)
        {
            return null;
        }

        List<CharacterDrop.Drop> sourceDrops = characterDrop.m_drops;
        List<CharacterDrop.Drop>? adjustedDrops = null;
        for (int index = 0; index < sourceDrops.Count; index++)
        {
            CharacterDrop.Drop drop = sourceDrops[index];
            if (drop == null ||
                !drop.m_levelMultiplier ||
                !ShouldApplyGlobalTrophyLevelMultiplier(drop.m_prefab))
            {
                continue;
            }

            adjustedDrops ??= CloneDrops(sourceDrops);
            adjustedDrops[index].m_levelMultiplier = false;
        }

        if (adjustedDrops == null)
        {
            return null;
        }

        characterDrop.m_drops = adjustedDrops;
        return sourceDrops;
    }

    internal static void ApplyGlobalTrophyLevelMultiplier(
        CharacterDrop characterDrop,
        List<KeyValuePair<GameObject, int>> drops,
        IReadOnlyList<CharacterDrop.Drop>? sourceDrops)
    {
        if (!PluginSettingsFacade.IsGlobalCharacterDropTrophyLevelMultiplierEnabled() ||
            characterDrop == null ||
            drops == null ||
            drops.Count == 0)
        {
            return;
        }

        Character? character = characterDrop.GetComponent<Character>();
        int characterLevel = character != null ? character.GetLevel() : 1;
        if (characterLevel <= 1)
        {
            return;
        }

        int vanillaLevelFactor = GetVanillaDropLevelFactor(characterLevel);
        int sourceIndex = 0;
        for (int index = 0; index < drops.Count; index++)
        {
            KeyValuePair<GameObject, int> result = drops[index];
            if (!ShouldApplyGlobalTrophyLevelMultiplier(result.Key))
            {
                continue;
            }

            CharacterDrop.Drop? sourceDrop = FindNextMatchingSourceDrop(sourceDrops, ref sourceIndex, result.Key);
            if (sourceDrop?.m_onePerPlayer == true)
            {
                continue;
            }

            int baseAmount = NormalizeGeneratedTrophyAmount(result.Value, vanillaLevelFactor, sourceDrop);
            int scaledAmount = ScaleGlobalTrophyAmount(baseAmount, characterLevel);
            if (scaledAmount != result.Value)
            {
                drops[index] = new KeyValuePair<GameObject, int>(result.Key, scaledAmount);
            }
        }
    }

    private static CharacterDrop.Drop? FindNextMatchingSourceDrop(
        IReadOnlyList<CharacterDrop.Drop>? sourceDrops,
        ref int sourceIndex,
        GameObject prefab)
    {
        if (sourceDrops == null || prefab == null)
        {
            return null;
        }

        for (; sourceIndex < sourceDrops.Count; sourceIndex++)
        {
            CharacterDrop.Drop candidate = sourceDrops[sourceIndex];
            if (candidate == null || candidate.m_prefab != prefab)
            {
                continue;
            }

            sourceIndex++;
            return candidate;
        }

        return null;
    }

    private static int NormalizeGeneratedTrophyAmount(
        int amount,
        int vanillaLevelFactor,
        CharacterDrop.Drop? sourceDrop)
    {
        if (amount <= 0 || vanillaLevelFactor <= 1)
        {
            return amount;
        }

        if (sourceDrop != null)
        {
            int configuredMax = Math.Max(1, Math.Max(sourceDrop.m_amountMin, sourceDrop.m_amountMax));
            if (amount <= configuredMax)
            {
                return amount;
            }

            if (amount >= 100 && vanillaLevelFactor > configuredMax)
            {
                return configuredMax;
            }
        }
        else if (amount < vanillaLevelFactor)
        {
            return amount;
        }

        return amount % vanillaLevelFactor == 0
            ? Math.Max(1, amount / vanillaLevelFactor)
            : amount;
    }

    private static int ScaleGlobalTrophyAmount(int amount, int characterLevel)
    {
        if (amount <= 0 || characterLevel <= 1)
        {
            return amount;
        }

        float multiplier = 1f + GlobalTrophyAmountBonusPerLevel * (characterLevel - 1);
        float scaledAmount = amount * multiplier;
        int roundedAmount = Mathf.FloorToInt(scaledAmount);
        float fractionalAmount = scaledAmount - roundedAmount;
        if (fractionalAmount > 0f && UnityEngine.Random.value < fractionalAmount)
        {
            roundedAmount++;
        }

        return Math.Min(100, Math.Max(1, roundedAmount));
    }

    private static int GetVanillaDropLevelFactor(int characterLevel)
    {
        return Mathf.Max(1, (int)Mathf.Pow(2f, characterLevel - 1));
    }

    internal static List<CharacterDrop.Drop>? OverrideConditionalDrops(CharacterDrop characterDrop)
    {
        CachedCharacterRuntimeDropResolution? resolution;
        lock (Sync)
        {
            if (!TryResolveRuntimeDropResolutionLocked(characterDrop, out _, out resolution) ||
                resolution == null ||
                !resolution.HasMatchedRuntimeRule ||
                resolution.OverrideDrops == null)
            {
                return null;
            }
        }

        List<CharacterDrop.Drop> previous = characterDrop.m_drops;
        characterDrop.m_drops = resolution.OverrideDrops;
        return previous;
    }

    internal static bool TryHandleConfiguredDeath(CharacterDrop characterDrop)
    {
        Character? character;
        CachedCharacterRuntimeDropResolution? resolution;
        lock (Sync)
        {
            if (!TryResolveRuntimeDropResolutionLocked(characterDrop, out character, out resolution) ||
                character == null ||
                resolution == null ||
                !resolution.HasMatchedRuntimeRule)
            {
                return false;
            }
        }

        if (DropsEnabledField?.GetValue(characterDrop) is false)
        {
            return true;
        }

        if (!resolution.HasCustomDropHandling)
        {
            return false;
        }

        List<ResolvedConfiguredDrop> drops = GenerateConfiguredDrops(character, resolution.Definitions);
        if (drops.Count == 0)
        {
            return true;
        }

        Vector3 centerPos = character.GetCenterPoint() + characterDrop.transform.TransformVector(characterDrop.m_spawnOffset);
        DropConfiguredItems(drops, centerPos, 0.5f);
        return true;
    }

    internal static bool BeginOnePerPlayerNearbyPlayerScope(CharacterDrop characterDrop)
    {
        float range = PluginSettingsFacade.GetCharacterDropOnePerPlayerNearbyRange();
        if (range <= 0f)
        {
            return false;
        }

        if (OnePerPlayerScopeDepth == 0)
        {
            OnePerPlayerScopeCharacterDrop = characterDrop;
        }

        OnePerPlayerScopeDepth++;
        return true;
    }

    internal static void EndOnePerPlayerNearbyPlayerScope()
    {
        if (OnePerPlayerScopeDepth <= 0)
        {
            return;
        }

        OnePerPlayerScopeDepth--;
        if (OnePerPlayerScopeDepth == 0)
        {
            OnePerPlayerScopeCharacterDrop = null;
        }
    }

    internal static bool TryGetScopedOnePerPlayerNearbyPlayerCount(out int playerCount)
    {
        playerCount = 0;
        float range = PluginSettingsFacade.GetCharacterDropOnePerPlayerNearbyRange();
        if (range <= 0f || OnePerPlayerScopeDepth <= 0 || OnePerPlayerScopeCharacterDrop == null)
        {
            return false;
        }

        playerCount = CountNearbyPlayers(OnePerPlayerScopeCharacterDrop, character: null, range);
        return true;
    }

    private static IEnumerable<string> EnumerateOverrideConfigurationPaths()
    {
        return DomainConfigurationFileSupport.EnumerateOverrideConfigurationPaths(
            "character",
            PrimaryOverrideConfigurationPathYml,
            PrimaryOverrideConfigurationPathYaml);
    }

    private static bool IsOverrideConfigurationFileName(string fileName)
    {
        return DomainConfigurationFileSupport.IsOverrideConfigurationFileName("character", fileName);
    }

    private static void CaptureSnapshotsIfNeeded()
    {
        CharacterDropRuntime.CaptureSnapshotsIfNeeded(EnumerateRelevantPrefabs(), CaptureSnapshot);
    }

    private static void RefreshSnapshots()
    {
        CharacterDropRuntime.RefreshSnapshots(EnumerateRelevantPrefabs(), CaptureSnapshot);
    }

    private static void EnsureCompiledState()
    {
        if (!IsGameDataReady())
        {
            return;
        }

        int gameDataSignature = ComputeGameDataSignature();
        if (_compiledStateGameDataSignature == gameDataSignature &&
            string.Equals(_compiledStateConfigurationSignature, _configurationSignature, StringComparison.Ordinal))
        {
            return;
        }

        _compiledState = BuildCompiledState();
        _compiledStateGameDataSignature = gameDataSignature;
        _compiledStateConfigurationSignature = _configurationSignature;
    }

    private static CharacterCompiledState BuildCompiledState()
    {
        CharacterCompiledState state = new();
        foreach ((string prefabName, List<CharacterDropPrefabEntry> entries) in ActiveEntriesByPrefab)
        {
            SortedDictionary<string, CompiledCharacterDropDefinition> staticDefinitions = new(StringComparer.Ordinal);
            List<CompiledCharacterDropRule> runtimeRules = new();
            CharacterRuntimeDropCacheState runtimeDropCacheState = new();
            HashSet<string>? requiredGlobalKeys = null;
            HashSet<string>? forbiddenGlobalKeys = null;
            foreach (CharacterDropPrefabEntry entry in entries)
            {
                if (entry.CharacterDrop == null)
                {
                    continue;
                }

                CompiledCharacterDropRule compiledRule = CompileCharacterDropRule(entry);
                if (compiledRule.Drops.Count == 0)
                {
                    continue;
                }

                // Character-specific predicates like level/state/faction must be
                // evaluated against the live character, not folded into prefab static drops.
                bool requiresRuntimeEvaluation = DropConditionEvaluator.HasCharacterConditions(entry.Conditions);
                CompiledCharacterDropRule? runtimeRule = null;
                foreach (CompiledCharacterDropDefinition compiledDefinition in compiledRule.Drops)
                {
                    if (!requiresRuntimeEvaluation && !RequiresRuntimeCharacterDropHandling(compiledDefinition))
                    {
                        staticDefinitions.TryAdd(compiledDefinition.Fingerprint, compiledDefinition);
                        continue;
                    }

                    runtimeRule ??= new CompiledCharacterDropRule
                    {
                        Entry = entry
                    };
                    runtimeRule.Drops.Add(compiledDefinition);
                }

                if (runtimeRule != null && runtimeRule.Drops.Count > 0)
                {
                    runtimeRules.Add(runtimeRule);
                    ConditionsDefinition? conditions = entry.Conditions;
                    runtimeDropCacheState.UsesLevel |= conditions?.Level?.HasValues() == true ||
                                                       conditions?.MinLevel.HasValue == true ||
                                                       conditions?.MaxLevel.HasValue == true;
                    runtimeDropCacheState.UsesFaction |= HasConfiguredConditionValues(conditions?.Factions);
                    runtimeDropCacheState.UsesState |= HasConfiguredConditionValues(conditions?.States);
                    runtimeDropCacheState.UsesTimeOfDay |= conditions?.TimeOfDay != null;
                    runtimeDropCacheState.UsesRequiredEnvironments |= HasConfiguredConditionValues(conditions?.RequiredEnvironments);
                    runtimeDropCacheState.UsesInsidePlayerBase |= conditions?.InsidePlayerBase.HasValue == true;
                    runtimeDropCacheState.IsCacheable &= !DropConditionEvaluator.HasStaticConditions(conditions);
                    AddNormalizedConditionValues(conditions?.RequiredGlobalKeys, ref requiredGlobalKeys);
                    AddNormalizedConditionValues(conditions?.ForbiddenGlobalKeys, ref forbiddenGlobalKeys);
                }
            }

            if (runtimeRules.Count > 0)
            {
                state.RuntimeRulesByPrefab[prefabName] = runtimeRules;
                runtimeDropCacheState.RequiredGlobalKeys = requiredGlobalKeys?.ToArray() ?? Array.Empty<string>();
                runtimeDropCacheState.ForbiddenGlobalKeys = forbiddenGlobalKeys?.ToArray() ?? Array.Empty<string>();
                state.RuntimeDropCachesByPrefab[prefabName] = runtimeDropCacheState;
            }

            if (staticDefinitions.Count > 0)
            {
                List<CompiledCharacterDropDefinition> compiledStaticDefinitions = staticDefinitions.Values.ToList();
                state.StaticDropsByPrefab[prefabName] = compiledStaticDefinitions;
                state.StaticBuiltDropsByPrefab[prefabName] = BuildDrops(compiledStaticDefinitions);
            }

        }

        return state;
    }

    private static CompiledCharacterDropRule CompileCharacterDropRule(CharacterDropPrefabEntry entry)
    {
        CompiledCharacterDropRule compiledRule = new()
        {
            Entry = entry
        };

        foreach (CharacterDropEntryDefinition definition in entry.CharacterDrop?.Drops ?? Enumerable.Empty<CharacterDropEntryDefinition>())
        {
            if (TryCompileCharacterDropDefinition(entry, definition, out CompiledCharacterDropDefinition? compiledDefinition))
            {
                compiledRule.Drops.Add(compiledDefinition);
            }
        }

        return compiledRule;
    }

    private static bool TryCompileCharacterDropDefinition(
        CharacterDropPrefabEntry entry,
        CharacterDropEntryDefinition definition,
        out CompiledCharacterDropDefinition compiledDefinition)
    {
        compiledDefinition = null!;
        string context = BuildCompiledDropContext(entry);
        string itemName = (definition.Item ?? "").Trim();
        if (itemName.Length == 0)
        {
            WarnInvalidEntry($"Entry '{context}' contains a character drop without a drop prefab name.");
            return false;
        }

        GameObject? dropPrefab = ResolveDropPrefab(itemName, context);
        if (dropPrefab == null)
        {
            return false;
        }

        int amountMin = Math.Max(1, definition.AmountMin ?? 1);
        int amountMax = Math.Max(amountMin, definition.AmountMax ?? definition.AmountMin ?? 1);
        bool levelMultiplier = GetEffectiveCharacterDropLevelMultiplier(definition, dropPrefab);
        compiledDefinition = new CompiledCharacterDropDefinition
        {
            Fingerprint = BuildDropRowFingerprint(definition, levelMultiplier),
            Prefab = dropPrefab,
            AmountMin = amountMin,
            AmountMax = amountMax,
            Chance = Mathf.Max(0f, definition.Chance ?? 1f),
            DontScale = definition.DontScale ?? false,
            LevelMultiplier = levelMultiplier,
            OnePerPlayer = definition.OnePerPlayer ?? false,
            AmountLimit = definition.AmountLimit.HasValue && definition.AmountLimit.Value >= 0
                ? definition.AmountLimit.Value
                : null,
            DropInStack = definition.DropInStack == true
        };
        return true;
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
            if (prefab != null && prefab.GetComponent<CharacterDrop>() != null)
            {
                yield return prefab;
            }
        }
    }

    private static int ComputeGameDataSignature()
    {
        if (!IsGameDataReady() || ZNetScene.instance == null || ObjectDB.instance == null)
        {
            return 0;
        }

        int frameCount = Time.frameCount;
        if (_cachedFrameGameDataSignatureFrame == frameCount)
        {
            return _cachedFrameGameDataSignatureValue;
        }

        unchecked
        {
            int hash = 17;
            hash = hash * 31 + ZNetScene.instance.GetInstanceID();
            hash = hash * 31 + ObjectDB.instance.GetInstanceID();
            hash = HashGameObjectCollection(hash, EnumerateRelevantPrefabs());
            hash = HashGameObjectCollection(hash, ObjectDB.instance.m_items);
            _cachedFrameGameDataSignatureFrame = frameCount;
            _cachedFrameGameDataSignatureValue = hash;
            return hash;
        }
    }

    private static int ComputeLiveRegistrySceneSignature()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + SceneManager.sceneCount;
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                Scene scene = SceneManager.GetSceneAt(sceneIndex);
                hash = hash * 31 + scene.handle;
                hash = hash * 31 + (scene.isLoaded ? 1 : 0);
            }

            return hash;
        }
    }

    private static int ComputeSnapshotSignature()
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

    private static CharacterDropSnapshot? CaptureSnapshot(GameObject prefab)
    {
        CharacterDrop? characterDrop = prefab.GetComponent<CharacterDrop>();
        if (characterDrop == null)
        {
            return null;
        }

        return new CharacterDropSnapshot
        {
            Prefab = prefab,
            Drops = CloneSnapshotDrops(characterDrop.m_drops),
            BuiltDrops = CloneDrops(characterDrop.m_drops, normalizeNonItemLevelMultiplier: true)
        };
    }

    private static void ApplyIfReady()
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

        if (!CharacterDropRuntime.HasSnapshots())
        {
            int pendingGameDataSignature = ComputeGameDataSignature();
            int snapshotSignature = ComputeSnapshotSignature();
            if (CharacterDropRuntime.NeedsSnapshotBuild(snapshotSignature))
            {
                CharacterDropRuntime.ScheduleSnapshotBuild(
                    "character apply gate",
                    pendingGameDataSignature,
                    snapshotSignature,
                    EnumerateRelevantPrefabs());
            }

            return;
        }

        int gameDataSignature = ComputeGameDataSignature();
        if (!CharacterDropRuntime.IsGameDataAlreadyProcessed(gameDataSignature))
        {
            int snapshotSignature = ComputeSnapshotSignature();
            CharacterDropRuntime.ScheduleSnapshotBuild("character apply gate", gameDataSignature, snapshotSignature, EnumerateRelevantPrefabs());
            return;
        }

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

        RunApplyCoordinator(gameDataSignature, domainEnabled, currentEntrySignatures, _configurationSignature);
    }

    private static readonly Dictionary<string, string> EmptyEntrySignatures = new(StringComparer.OrdinalIgnoreCase);

    private static void RecordAppliedState(int gameDataSignature, bool domainEnabled, Dictionary<string, string> currentEntrySignatures, string effectiveConfigurationSignature)
    {
        _lastAppliedGameDataSignature = gameDataSignature;
        _lastAppliedDomainEnabled = domainEnabled;
        _lastAppliedConfigurationSignature = effectiveConfigurationSignature;
        _lastAppliedSynchronizedPayloadReady = Volatile.Read(ref _synchronizedPayloadReady);
        ReplaceEntrySignatures(_lastAppliedEntrySignaturesByPrefab, currentEntrySignatures);
    }

    private static void RestoreSnapshots(HashSet<string>? targetPrefabs = null)
    {
        if (targetPrefabs == null)
        {
            foreach (CharacterDropSnapshot snapshot in CharacterDropRuntime.GetSnapshots())
            {
                RestoreSnapshot(snapshot);
            }

            return;
        }

        foreach (string prefabName in targetPrefabs)
        {
            if (CharacterDropRuntime.TryGetSnapshot(prefabName, out CharacterDropSnapshot? snapshot) &&
                snapshot != null)
            {
                RestoreSnapshot(snapshot);
            }
        }
    }

    private static void RestoreTrackedCharacterDrops(HashSet<string> prefabs)
    {
        if (prefabs.Count == 0 || !IsGameDataReady() || !CharacterDropRuntime.HasSnapshots())
        {
            return;
        }

        BootstrapRegisteredCharacterDropsIfNeeded(prefabs, forceRescan: true);
        foreach (CharacterDrop characterDrop in GetRegisteredCharacterDrops())
        {
            string prefabName = GetPrefabName(characterDrop.gameObject);
            if (!prefabs.Contains(prefabName) ||
                !CharacterDropRuntime.TryGetSnapshot(prefabName, out CharacterDropSnapshot? snapshot) ||
                snapshot == null)
            {
                continue;
            }

            characterDrop.m_drops = snapshot.BuiltDrops;
        }
    }

    private static void RestoreSnapshot(CharacterDropSnapshot snapshot)
    {
        if (snapshot.Prefab == null)
        {
            return;
        }

        if (snapshot.Prefab.TryGetComponent(out CharacterDrop characterDrop))
        {
            characterDrop.m_drops = snapshot.BuiltDrops;
        }
    }

    private static void ValidateConfiguredPrefabs()
    {
        foreach ((string prefabName, List<CharacterDropPrefabEntry> entries) in ActiveEntriesByPrefab)
        {
            if (!CharacterDropRuntime.HasSnapshot(prefabName))
            {
                foreach (CharacterDropPrefabEntry entry in entries)
                {
                    WarnInvalidEntry($"Character prefab '{prefabName}' from {DescribeEntrySource(entry)} was not found in ZNetScene.");
                }
            }
        }
    }

    private static void ApplyCurrentStateToTrackedCharacterDrop(CharacterDrop characterDrop)
    {
        if (characterDrop == null ||
            characterDrop.gameObject == null ||
            !IsGameDataReady() ||
            !CharacterDropRuntime.HasSnapshots())
        {
            return;
        }

        string prefabName = GetPrefabName(characterDrop.gameObject);
        if (!PrefabsWithCharacterDropOverrides.Contains(prefabName))
        {
            return;
        }

        int gameDataSignature = ComputeGameDataSignature();
        bool domainEnabled = ShouldApplyLocally();
        bool synchronizedPayloadReady = Volatile.Read(ref _synchronizedPayloadReady);
        if (!StandardDomainApplySupport.IsAlreadyApplied(
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

        if (!CharacterDropRuntime.TryGetSnapshot(prefabName, out CharacterDropSnapshot? snapshot) ||
            snapshot == null)
        {
            return;
        }

        characterDrop.m_drops = snapshot.BuiltDrops;
        if (!domainEnabled)
        {
            return;
        }

        EnsureCompiledState();
        if (!_compiledState.StaticBuiltDropsByPrefab.TryGetValue(prefabName, out List<CharacterDrop.Drop>? staticDrops) ||
            staticDrops.Count == 0)
        {
            return;
        }

        characterDrop.m_drops = staticDrops;
    }

    internal static void TrackCharacterDropInstance(CharacterDrop? characterDrop)
    {
        if (characterDrop == null || characterDrop.gameObject == null)
        {
            return;
        }

        string prefabName = GetPrefabName(characterDrop.gameObject);
        if (!PrefabsWithCharacterDropOverrides.Contains(prefabName))
        {
            return;
        }

        lock (Sync)
        {
            if (!PrefabsWithCharacterDropOverrides.Contains(prefabName))
            {
                return;
            }

            RegisterLiveCharacterDrop(characterDrop);
            ApplyCurrentStateToTrackedCharacterDrop(characterDrop);
        }
    }

    internal static void UntrackCharacterDropInstance(CharacterDrop? characterDrop)
    {
        lock (Sync)
        {
            if (characterDrop == null || !CharacterDropRuntime.TryGetRegisteredPrefabName(characterDrop, out string prefabName))
            {
                return;
            }

            UnregisterLiveCharacterDrop(characterDrop, prefabName);
        }
    }

    private static void BootstrapRegisteredCharacterDropsIfNeeded(HashSet<string>? additionalPrefabs = null, bool forceRescan = false)
    {
        if (PrefabsWithCharacterDropOverrides.Count == 0 &&
            (additionalPrefabs == null || additionalPrefabs.Count == 0))
        {
            return;
        }

        int sceneSignature = ComputeLiveRegistrySceneSignature();
        CharacterDropRuntime.BootstrapRegisteredCharacterDropsIfNeeded(
            sceneSignature,
            UnityEngine.Object.FindObjectsByType<CharacterDrop>(FindObjectsSortMode.None),
            characterDrop => GetPrefabName(characterDrop.gameObject),
            ShouldRegisterBootstrappedCharacterDropPrefab,
            additionalPrefabs,
            forceRescan);
    }

    private static IEnumerable<CharacterDrop> GetRegisteredCharacterDrops()
    {
        return CharacterDropRuntime.GetRegisteredCharacterDrops();
    }

    private static Dictionary<string, string> CloneCurrentEntrySignaturesByPrefab()
    {
        return new Dictionary<string, string>(CurrentEntrySignaturesByPrefab, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildActiveEntrySignaturesByPrefab(
        Dictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab)
    {
        return DomainEntrySignatureSupport.BuildSignaturesByKey(
            activeEntriesByPrefab,
            entries => NetworkPayloadSyncSupport.ComputeCharacterConfigurationSignature(entries
                .OrderBy(entry => entry.RuleId, StringComparer.Ordinal)
                .ToList()));
    }

    private static bool TryBuildEffectiveCustomDropDefinitions(IEnumerable<CharacterDropPrefabEntry> entries, Character character, out List<CharacterDropEntryDefinition> definitions)
    {
        SortedDictionary<string, CharacterDropEntryDefinition> definitionsByFingerprint = new(StringComparer.Ordinal);
        bool matchedCustomEntry = false;
        foreach (CharacterDropPrefabEntry entry in entries ?? Enumerable.Empty<CharacterDropPrefabEntry>())
        {
            if (!EntryMatchesCharacter(entry, character))
            {
                continue;
            }

            if (entry.CharacterDrop == null)
            {
                continue;
            }

            matchedCustomEntry = true;
            foreach (CharacterDropEntryDefinition definition in entry.CharacterDrop?.Drops ?? Enumerable.Empty<CharacterDropEntryDefinition>())
            {
                GameObject? dropPrefab = ResolveDropPrefab((definition.Item ?? "").Trim(), BuildCompiledDropContext(entry));
                if (dropPrefab == null)
                {
                    continue;
                }

                string fingerprint = BuildDropRowFingerprint(
                    definition,
                    GetEffectiveCharacterDropLevelMultiplier(definition, dropPrefab));
                definitionsByFingerprint.TryAdd(fingerprint, definition);
            }
        }

        definitions = definitionsByFingerprint.Values.ToList();
        return matchedCustomEntry;
    }

    private static bool TryBuildEffectiveCustomDropDefinitions(
        IEnumerable<CompiledCharacterDropRule> rules,
        Character character,
        out List<CompiledCharacterDropDefinition> definitions)
    {
        SortedDictionary<string, CompiledCharacterDropDefinition> definitionsByFingerprint = new(StringComparer.Ordinal);
        bool matchedCustomEntry = false;
        foreach (CompiledCharacterDropRule rule in rules ?? Enumerable.Empty<CompiledCharacterDropRule>())
        {
            if (!EntryMatchesCharacter(rule.Entry, character))
            {
                continue;
            }

            matchedCustomEntry = true;
            foreach (CompiledCharacterDropDefinition definition in rule.Drops)
            {
                definitionsByFingerprint.TryAdd(definition.Fingerprint, definition);
            }
        }

        definitions = definitionsByFingerprint.Values.ToList();
        return matchedCustomEntry;
    }

    private static bool TryBuildEffectiveRuntimeDropDefinitions(
        IEnumerable<CompiledCharacterDropDefinition>? staticDefinitions,
        IEnumerable<CompiledCharacterDropRule> runtimeRules,
        Character character,
        out List<CompiledCharacterDropDefinition> definitions)
    {
        SortedDictionary<string, CompiledCharacterDropDefinition> definitionsByFingerprint = new(StringComparer.Ordinal);
        foreach (CompiledCharacterDropDefinition definition in staticDefinitions ?? Enumerable.Empty<CompiledCharacterDropDefinition>())
        {
            definitionsByFingerprint.TryAdd(definition.Fingerprint, definition);
        }

        bool matchedRuntimeRule = false;
        foreach (CompiledCharacterDropRule rule in runtimeRules ?? Enumerable.Empty<CompiledCharacterDropRule>())
        {
            if (!EntryMatchesCharacter(rule.Entry, character))
            {
                continue;
            }

            matchedRuntimeRule = true;
            foreach (CompiledCharacterDropDefinition definition in rule.Drops)
            {
                definitionsByFingerprint.TryAdd(definition.Fingerprint, definition);
            }
        }

        definitions = definitionsByFingerprint.Values.ToList();
        return matchedRuntimeRule;
    }

    private static bool TryResolveRuntimeDropResolutionLocked(
        CharacterDrop characterDrop,
        out Character? character,
        out CachedCharacterRuntimeDropResolution? resolution)
    {
        resolution = null;
        character = null;
        if (!CanUseCurrentRuntimeState())
        {
            return false;
        }

        string prefabName = GetPrefabName(characterDrop.gameObject);
        EnsureCompiledState();
        if (!_compiledState.RuntimeRulesByPrefab.TryGetValue(prefabName, out List<CompiledCharacterDropRule>? compiledRules) ||
            compiledRules.Count == 0)
        {
            return false;
        }

        character = characterDrop.GetComponent<Character>();
        if (character == null)
        {
            return false;
        }

        CharacterRuntimeDropCacheState? cacheState =
            _compiledState.RuntimeDropCachesByPrefab.TryGetValue(prefabName, out CharacterRuntimeDropCacheState? configuredCacheState)
                ? configuredCacheState
                : null;
        if (cacheState?.IsCacheable == true)
        {
            int runtimeSignature = ComputeCachedRuntimeDropSignature(character, cacheState);
            if (cacheState.ResolutionsBySignature.TryGetValue(runtimeSignature, out resolution))
            {
                return resolution.HasMatchedRuntimeRule;
            }

            resolution = BuildRuntimeDropResolution(
                _compiledState.StaticDropsByPrefab.TryGetValue(prefabName, out List<CompiledCharacterDropDefinition>? configuredStaticDefinitions)
                    ? configuredStaticDefinitions
                    : null,
                compiledRules,
                character);
            StoreCachedRuntimeDropResolution(cacheState, runtimeSignature, resolution);
            return resolution.HasMatchedRuntimeRule;
        }

        resolution = BuildRuntimeDropResolution(
            _compiledState.StaticDropsByPrefab.TryGetValue(prefabName, out List<CompiledCharacterDropDefinition>? staticDefinitions)
                ? staticDefinitions
                : null,
            compiledRules,
            character);
        return resolution.HasMatchedRuntimeRule;
    }

    private static CachedCharacterRuntimeDropResolution BuildRuntimeDropResolution(
        IReadOnlyList<CompiledCharacterDropDefinition>? staticDefinitions,
        IReadOnlyList<CompiledCharacterDropRule> runtimeRules,
        Character character)
    {
        if (!TryBuildEffectiveRuntimeDropDefinitions(staticDefinitions, runtimeRules, character, out List<CompiledCharacterDropDefinition> definitions))
        {
            return new CachedCharacterRuntimeDropResolution
            {
                HasMatchedRuntimeRule = false
            };
        }

        CompiledCharacterDropDefinition[] resolvedDefinitions = definitions.ToArray();
        return new CachedCharacterRuntimeDropResolution
        {
            HasMatchedRuntimeRule = true,
            Definitions = resolvedDefinitions,
            OverrideDrops = BuildDrops(resolvedDefinitions),
            HasCustomDropHandling = HasCustomDropHandling(resolvedDefinitions)
        };
    }

    private static void StoreCachedRuntimeDropResolution(
        CharacterRuntimeDropCacheState cacheState,
        int runtimeSignature,
        CachedCharacterRuntimeDropResolution resolution)
    {
        if (cacheState.ResolutionsBySignature.Count >= MaxCachedRuntimeDropResolutionsPerPrefab &&
            !cacheState.ResolutionsBySignature.ContainsKey(runtimeSignature))
        {
            cacheState.ResolutionsBySignature.Clear();
        }

        cacheState.ResolutionsBySignature[runtimeSignature] = resolution;
    }

    private static int ComputeCachedRuntimeDropSignature(Character character, CharacterRuntimeDropCacheState cacheState)
    {
        int signature = 17;
        if (cacheState.UsesLevel)
        {
            signature = CombineCachedRuntimeDropSignature(signature, character.GetLevel());
        }

        if (cacheState.UsesFaction)
        {
            signature = CombineCachedRuntimeDropSignature(signature, (int)character.GetFaction());
        }

        if (cacheState.UsesState)
        {
            signature = CombineCachedRuntimeDropSignature(signature, character.IsTamed());
            MonsterAI? monsterAI = character.GetComponent<MonsterAI>();
            signature = CombineCachedRuntimeDropSignature(signature, monsterAI != null && monsterAI.IsEventCreature());
        }

        if (cacheState.UsesTimeOfDay ||
            cacheState.UsesRequiredEnvironments ||
            cacheState.RequiredGlobalKeys.Length > 0 ||
            cacheState.ForbiddenGlobalKeys.Length > 0)
        {
            CharacterRuntimeContextSnapshot runtimeContext = GetOrCreateRuntimeContextSnapshot();
            if (cacheState.UsesTimeOfDay)
            {
                signature = CombineCachedRuntimeDropSignature(signature, runtimeContext.TimeOfDayPhaseMarker);
            }

            if (cacheState.UsesRequiredEnvironments)
            {
                signature = CombineCachedRuntimeDropSignature(signature, runtimeContext.EnvironmentName);
            }

            foreach (string key in cacheState.RequiredGlobalKeys)
            {
                signature = CombineCachedRuntimeDropSignature(signature, key);
                signature = CombineCachedRuntimeDropSignature(signature, GetCachedRuntimeDropGlobalKeyState(runtimeContext, key));
            }

            foreach (string key in cacheState.ForbiddenGlobalKeys)
            {
                signature = CombineCachedRuntimeDropSignature(signature, key);
                signature = CombineCachedRuntimeDropSignature(signature, GetCachedRuntimeDropGlobalKeyState(runtimeContext, key));
            }
        }

        if (cacheState.UsesInsidePlayerBase)
        {
            bool isInsidePlayerBase =
                EffectArea.IsPointInsideArea(character.transform.position, EffectArea.Type.PlayerBase) != null;
            signature = CombineCachedRuntimeDropSignature(signature, isInsidePlayerBase);
        }

        return signature;
    }

    private static CharacterRuntimeContextSnapshot GetOrCreateRuntimeContextSnapshot()
    {
        int currentFrame = Time.frameCount;
        if (_runtimeContextSnapshot != null &&
            _runtimeContextSnapshot.Frame == currentFrame)
        {
            return _runtimeContextSnapshot;
        }

        _runtimeContextSnapshot = new CharacterRuntimeContextSnapshot
        {
            Frame = currentFrame,
            TimeOfDayPhaseMarker = TimeOfDayFormatting.GetCurrentRuntimePhaseMarker(),
            EnvironmentName = EnvMan.instance?.GetCurrentEnvironment()?.m_name ?? ""
        };
        return _runtimeContextSnapshot;
    }

    private static bool GetCachedRuntimeDropGlobalKeyState(CharacterRuntimeContextSnapshot runtimeContext, string key)
    {
        if (runtimeContext.GlobalKeyStates.TryGetValue(key, out bool value))
        {
            return value;
        }

        value = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(key);
        runtimeContext.GlobalKeyStates[key] = value;
        return value;
    }

    private static int CombineCachedRuntimeDropSignature(int current, bool value)
    {
        unchecked
        {
            return (current * 31) + (value ? 1 : 0);
        }
    }

    private static int CombineCachedRuntimeDropSignature(int current, int value)
    {
        unchecked
        {
            return (current * 31) + value;
        }
    }

    private static int CombineCachedRuntimeDropSignature(int current, string value)
    {
        unchecked
        {
            return (current * 31) + (value?.GetHashCode() ?? 0);
        }
    }

    private static bool HasConfiguredConditionValues(IEnumerable<string>? values)
    {
        if (values == null)
        {
            return false;
        }

        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddNormalizedConditionValues(IEnumerable<string>? values, ref HashSet<string>? destination)
    {
        if (values == null)
        {
            return;
        }

        foreach (string value in values)
        {
            string normalizedValue = (value ?? "").Trim();
            if (normalizedValue.Length == 0)
            {
                continue;
            }

            destination ??= new HashSet<string>(StringComparer.Ordinal);
            destination.Add(normalizedValue);
        }
    }

    private static bool RequiresRuntimeCharacterDropHandling(CompiledCharacterDropDefinition definition)
    {
        return definition.AmountLimit.HasValue || definition.DropInStack;
    }

    private static bool EntryMatchesCharacter(CharacterDropPrefabEntry? entry, Character character)
    {
        return entry != null &&
               DropConditionEvaluator.AreSatisfied(character, entry.Conditions);
    }

    private static string BuildDropRowFingerprint(CharacterDropEntryDefinition definition, bool levelMultiplier)
    {
        CharacterDropEntryDefinition normalizedDefinition = new()
        {
            Item = (definition.Item ?? "").Trim(),
            AmountMin = Math.Max(1, definition.AmountMin ?? 1),
            AmountMax = Math.Max(Math.Max(1, definition.AmountMin ?? 1), definition.AmountMax ?? definition.AmountMin ?? 1),
            Chance = Mathf.Max(0f, definition.Chance ?? 1f),
            OnePerPlayer = definition.OnePerPlayer ?? false,
            LevelMultiplier = levelMultiplier,
            DontScale = definition.DontScale ?? false,
            AmountLimit = definition.AmountLimit.HasValue && definition.AmountLimit.Value >= 0
                ? definition.AmountLimit.Value
                : null,
            DropInStack = definition.DropInStack == true
        };

        return NetworkPayloadSyncSupport.ComputeCharacterDropRowSignature(normalizedDefinition);
    }

    private static bool HasAddedPrefabs(Dictionary<string, string> previous, Dictionary<string, string> current)
    {
        foreach (string prefabName in current.Keys)
        {
            if (!previous.ContainsKey(prefabName))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> BuildDirtyPrefabs(Dictionary<string, string> previous, Dictionary<string, string> current)
    {
        return DomainDictionaryDiffSupport.BuildDirtyKeys(previous, current);
    }

    private static void ReplaceEntrySignatures(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        DomainDictionaryDiffSupport.ReplaceEntries(target, source);
    }

    private static void RegisterLiveCharacterDrop(CharacterDrop characterDrop)
    {
        if (characterDrop == null || characterDrop.gameObject == null)
        {
            return;
        }

        string prefabName = GetPrefabName(characterDrop.gameObject);
        if (prefabName.Length == 0)
        {
            return;
        }

        CharacterDropRuntime.RegisterLiveCharacterDrop(characterDrop, prefabName);
    }

    private static bool ShouldRegisterBootstrappedCharacterDropPrefab(string prefabName)
    {
        return prefabName.Length > 0 && PrefabsWithCharacterDropOverrides.Contains(prefabName);
    }

    private static List<CharacterDropPrefabEntry> GetOrCreateActiveEntries(
        Dictionary<string, List<CharacterDropPrefabEntry>> activeEntriesByPrefab,
        string prefabName)
    {
        if (!activeEntriesByPrefab.TryGetValue(prefabName, out List<CharacterDropPrefabEntry>? entries))
        {
            entries = new List<CharacterDropPrefabEntry>();
            activeEntriesByPrefab[prefabName] = entries;
        }

        return entries;
    }

    private static IEnumerable<CharacterDrop> GetRegisteredCharacterDrops(HashSet<string> dirtyPrefabs)
    {
        return CharacterDropRuntime.GetRegisteredCharacterDrops(dirtyPrefabs);
    }

    private static void UnregisterLiveCharacterDrop(CharacterDrop characterDrop, string prefabName)
    {
        CharacterDropRuntime.UnregisterLiveCharacterDrop(characterDrop, prefabName);
    }

    private static List<CharacterDropItemSnapshot> CloneSnapshotDrops(List<CharacterDrop.Drop> drops)
    {
        List<CharacterDropItemSnapshot> clone = new(drops.Count);
        foreach (CharacterDrop.Drop drop in drops)
        {
            clone.Add(new CharacterDropItemSnapshot
            {
                ItemPrefab = drop.m_prefab,
                AmountMin = drop.m_amountMin,
                AmountMax = drop.m_amountMax,
                Chance = drop.m_chance,
                OnePerPlayer = drop.m_onePerPlayer,
                LevelMultiplier = NormalizeBaselineCharacterDropLevelMultiplier(drop.m_prefab, drop.m_levelMultiplier),
                DontScale = drop.m_dontScale
            });
        }

        return clone;
    }

    private static List<CharacterDrop.Drop> CloneDrops(List<CharacterDrop.Drop> drops, bool normalizeNonItemLevelMultiplier = false)
    {
        List<CharacterDrop.Drop> clone = new(drops.Count);
        foreach (CharacterDrop.Drop drop in drops)
        {
            clone.Add(new CharacterDrop.Drop
            {
                m_prefab = drop.m_prefab,
                m_amountMin = drop.m_amountMin,
                m_amountMax = drop.m_amountMax,
                m_chance = drop.m_chance,
                m_onePerPlayer = drop.m_onePerPlayer,
                m_levelMultiplier = normalizeNonItemLevelMultiplier
                    ? NormalizeBaselineCharacterDropLevelMultiplier(drop.m_prefab, drop.m_levelMultiplier)
                    : drop.m_levelMultiplier,
                m_dontScale = drop.m_dontScale
            });
        }

        return clone;
    }

    private static List<CharacterDrop.Drop> BuildDrops(List<CharacterDropItemSnapshot> snapshots)
    {
        return snapshots
            .Select(drop => new CharacterDrop.Drop
            {
                m_prefab = drop.ItemPrefab,
                m_amountMin = drop.AmountMin,
                m_amountMax = drop.AmountMax,
                m_chance = drop.Chance,
                m_onePerPlayer = drop.OnePerPlayer,
                m_levelMultiplier = drop.LevelMultiplier,
                m_dontScale = drop.DontScale
            })
            .ToList();
    }

    private static List<CharacterDrop.Drop> BuildDrops(IReadOnlyList<CompiledCharacterDropDefinition> definitions)
    {
        List<CharacterDrop.Drop> drops = new(definitions.Count);
        foreach (CompiledCharacterDropDefinition definition in definitions)
        {
            drops.Add(new CharacterDrop.Drop
            {
                m_prefab = definition.Prefab,
                m_amountMin = definition.AmountMin,
                m_amountMax = definition.AmountMax,
                m_chance = definition.Chance,
                m_onePerPlayer = definition.OnePerPlayer,
                m_levelMultiplier = definition.LevelMultiplier,
                m_dontScale = definition.DontScale
            });
        }

        return drops;
    }

    private static List<CharacterDrop.Drop> BuildDrops(List<CharacterDropEntryDefinition> definitions, string context)
    {
        List<CharacterDrop.Drop> drops = new();
        foreach (CharacterDropEntryDefinition definition in definitions)
        {
            string itemName = (definition.Item ?? "").Trim();
            if (itemName.Length == 0)
            {
                WarnInvalidEntry($"Entry '{context}' contains a character drop without a drop prefab name.");
                continue;
            }

            GameObject? dropPrefab = ResolveDropPrefab(itemName, context);
            if (dropPrefab == null)
            {
                continue;
            }

            bool levelMultiplier = GetEffectiveCharacterDropLevelMultiplier(definition, dropPrefab);
            drops.Add(new CharacterDrop.Drop
            {
                m_prefab = dropPrefab,
                m_amountMin = Math.Max(1, definition.AmountMin ?? 1),
                m_amountMax = Math.Max(Math.Max(1, definition.AmountMin ?? 1), definition.AmountMax ?? definition.AmountMin ?? 1),
                m_chance = Mathf.Max(0f, definition.Chance ?? 1f),
                m_onePerPlayer = definition.OnePerPlayer ?? false,
                m_levelMultiplier = levelMultiplier,
                m_dontScale = definition.DontScale ?? false
            });
        }

        return drops;
    }

    private static List<ResolvedConfiguredDrop> GenerateConfiguredDrops(Character character, List<CharacterDropEntryDefinition> definitions, string context)
    {
        List<ResolvedConfiguredDrop> drops = new();
        int characterLevel = character.GetLevel();
        int levelFactor = GetVanillaDropLevelFactor(characterLevel);
        int playerCount = GetOnePerPlayerDropCount(character);

        foreach (CharacterDropEntryDefinition definition in definitions)
        {
            string itemName = (definition.Item ?? "").Trim();
            if (itemName.Length == 0)
            {
                WarnInvalidEntry($"Entry '{context}' contains a character drop without a drop prefab name.");
                continue;
            }

            GameObject? dropPrefab = ResolveDropPrefab(itemName, context);
            if (dropPrefab == null)
            {
                continue;
            }

            float chance = Mathf.Max(0f, definition.Chance ?? 1f);
            bool levelMultiplier = GetEffectiveCharacterDropLevelMultiplier(definition, dropPrefab);
            bool applyGlobalTrophyMultiplier = ShouldApplyGlobalTrophyLevelMultiplier(dropPrefab);
            bool applyVanillaLevelMultiplier = levelMultiplier && !applyGlobalTrophyMultiplier;
            if (applyVanillaLevelMultiplier)
            {
                chance *= levelFactor;
            }

            if (UnityEngine.Random.value > chance)
            {
                continue;
            }

            int amountMin = Math.Max(1, definition.AmountMin ?? 1);
            int amountMax = Math.Max(amountMin, definition.AmountMax ?? definition.AmountMin ?? 1);
            int amount = definition.DontScale ?? false
                ? UnityEngine.Random.Range(amountMin, amountMax)
                : RollConfiguredDropAmount(dropPrefab, amountMin, amountMax);

            if (applyVanillaLevelMultiplier)
            {
                amount *= levelFactor;
            }

            bool onePerPlayer = definition.OnePerPlayer ?? false;
            if (onePerPlayer)
            {
                amount = playerCount;
            }

            if (applyGlobalTrophyMultiplier && !onePerPlayer)
            {
                amount = ScaleGlobalTrophyAmount(amount, characterLevel);
            }

            amount = Math.Min(amount, 100);
            if (definition.AmountLimit.HasValue && definition.AmountLimit.Value >= 0)
            {
                amount = Math.Min(amount, definition.AmountLimit.Value);
            }

            if (amount <= 0)
            {
                continue;
            }

            drops.Add(new ResolvedConfiguredDrop
            {
                Prefab = dropPrefab,
                Amount = amount,
                DropInStack = definition.DropInStack == true
            });
        }

        return drops;
    }

    private static List<ResolvedConfiguredDrop> GenerateConfiguredDrops(Character character, IReadOnlyList<CompiledCharacterDropDefinition> definitions)
    {
        List<ResolvedConfiguredDrop> drops = new();
        int characterLevel = character.GetLevel();
        int levelFactor = GetVanillaDropLevelFactor(characterLevel);
        int playerCount = GetOnePerPlayerDropCount(character);

        foreach (CompiledCharacterDropDefinition definition in definitions)
        {
            float chance = definition.Chance;
            bool applyGlobalTrophyMultiplier = ShouldApplyGlobalTrophyLevelMultiplier(definition.Prefab);
            bool applyVanillaLevelMultiplier = definition.LevelMultiplier && !applyGlobalTrophyMultiplier;
            if (applyVanillaLevelMultiplier)
            {
                chance *= levelFactor;
            }

            if (UnityEngine.Random.value > chance)
            {
                continue;
            }

            int amount = definition.DontScale
                ? UnityEngine.Random.Range(definition.AmountMin, definition.AmountMax)
                : RollConfiguredDropAmount(definition.Prefab, definition.AmountMin, definition.AmountMax);

            if (applyVanillaLevelMultiplier)
            {
                amount *= levelFactor;
            }

            if (definition.OnePerPlayer)
            {
                amount = playerCount;
            }

            if (applyGlobalTrophyMultiplier && !definition.OnePerPlayer)
            {
                amount = ScaleGlobalTrophyAmount(amount, characterLevel);
            }

            amount = Math.Min(amount, 100);
            if (definition.AmountLimit.HasValue)
            {
                amount = Math.Min(amount, definition.AmountLimit.Value);
            }

            if (amount <= 0)
            {
                continue;
            }

            drops.Add(new ResolvedConfiguredDrop
            {
                Prefab = definition.Prefab,
                Amount = amount,
                DropInStack = definition.DropInStack
            });
        }

        return drops;
    }

    private static int GetOnePerPlayerDropCount(Character character)
    {
        float range = PluginSettingsFacade.GetCharacterDropOnePerPlayerNearbyRange();
        if (range > 0f && character.TryGetComponent(out CharacterDrop characterDrop))
        {
            return CountNearbyPlayers(characterDrop, character, range);
        }

        return ZNet.instance != null ? ZNet.instance.GetNrOfPlayers() : 1;
    }

    private static int CountNearbyPlayers(CharacterDrop characterDrop, Character? character, float range)
    {
        character ??= characterDrop.GetComponent<Character>();
        Vector3 point = character != null ? character.GetCenterPoint() : characterDrop.transform.position;
        bool livingPlayersOnly = PluginSettingsFacade.IsCharacterDropOnePerPlayerNearbyRangeLivingPlayersOnly();
        return SceneProximityQueries.CountPlayersInRangeXZ(point, range, livingPlayersOnly);
    }

    private static void DropConfiguredItems(List<ResolvedConfiguredDrop> drops, Vector3 centerPos, float dropArea)
    {
        List<KeyValuePair<GameObject, int>> normalDrops = new();
        foreach (ResolvedConfiguredDrop drop in drops)
        {
            if (!ShouldDropInStack(drop.Prefab, drop.Amount, drop.DropInStack))
            {
                normalDrops.Add(new KeyValuePair<GameObject, int>(drop.Prefab, drop.Amount));
                continue;
            }

            SpawnStackedDrops(drop.Prefab, drop.Amount, centerPos, dropArea);
        }

        if (normalDrops.Count > 0)
        {
            SpawnConfiguredLooseDrops(normalDrops, centerPos, dropArea);
        }
    }

    private static bool GetEffectiveCharacterDropLevelMultiplier(CharacterDropEntryDefinition definition, GameObject? dropPrefab)
    {
        return definition.LevelMultiplier ?? GetDefaultCharacterDropLevelMultiplier(dropPrefab);
    }

    private static bool GetConfiguredCharacterDropLevelMultiplierForOutput(CharacterDropEntryDefinition definition)
    {
        return definition.LevelMultiplier ?? GetDefaultCharacterDropLevelMultiplier(ResolveKnownPrefab(definition.Item));
    }

    private static bool GetDefaultCharacterDropLevelMultiplier(GameObject? dropPrefab)
    {
        return IsItemDropPrefab(dropPrefab);
    }

    private static bool NormalizeBaselineCharacterDropLevelMultiplier(GameObject? dropPrefab, bool levelMultiplier)
    {
        return IsItemDropPrefab(dropPrefab) && levelMultiplier;
    }

    private static bool ShouldApplyGlobalTrophyLevelMultiplier(GameObject? itemPrefab)
    {
        return PluginSettingsFacade.IsGlobalCharacterDropTrophyLevelMultiplierEnabled() &&
               itemPrefab != null &&
               !PluginSettingsFacade.IsCharacterDropTrophyLevelMultiplierBlacklisted(itemPrefab.name) &&
               IsTrophyItemPrefab(itemPrefab);
    }

    private static bool IsTrophyItemPrefab(GameObject itemPrefab)
    {
        if (!itemPrefab.TryGetComponent(out ItemDrop itemDrop))
        {
            return false;
        }

        ItemDrop.ItemData.SharedData? sharedData = itemDrop.m_itemData?.m_shared;
        return sharedData != null && sharedData.m_itemType == ItemDrop.ItemData.ItemType.Trophy;
    }

    private static bool IsItemDropPrefab(GameObject? prefab)
    {
        return IsItemDropPrefab(prefab, out _);
    }

    private static bool IsItemDropPrefab(GameObject? prefab, out ItemDrop? itemDrop)
    {
        itemDrop = null;
        return prefab != null && prefab.TryGetComponent(out itemDrop);
    }

    private static bool ShouldDropInStack(GameObject prefab, int amount, bool explicitDropInStack)
    {
        if (prefab == null ||
            !prefab.TryGetComponent(out ItemDrop itemDrop) ||
            itemDrop.m_itemData.m_shared.m_maxStackSize <= 1 ||
            PluginSettingsFacade.IsCharacterDropInStackBlacklisted(prefab.name))
        {
            return false;
        }

        return explicitDropInStack || (PluginSettingsFacade.IsGlobalCharacterDropInStackEnabled() && amount > 1);
    }

    private static void SpawnStackedDrops(GameObject prefab, int amount, Vector3 centerPos, float dropArea)
    {
        if (amount <= 0 || !prefab.TryGetComponent(out ItemDrop itemDrop))
        {
            return;
        }

        int remaining = amount;
        int maxStackSize = Math.Max(1, itemDrop.m_itemData.m_shared.m_maxStackSize);
        while (remaining > 0)
        {
            int stackSize = Math.Min(remaining, maxStackSize);
            SpawnStackedItem(prefab, stackSize, centerPos, dropArea);
            remaining -= stackSize;
        }
    }

    private static void SpawnStackedItem(GameObject prefab, int stackSize, Vector3 centerPos, float dropArea)
    {
        Quaternion rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0, 360), 0f);
        Vector3 spawnPoint = ResolveConfiguredDropSpawnPoint(centerPos, dropArea);
        GameObject spawned = UnityEngine.Object.Instantiate(prefab, spawnPoint, rotation);
        if (spawned.TryGetComponent(out ItemDrop itemDrop))
        {
            ItemDrop.OnCreateNew(itemDrop);
            itemDrop.SetStack(stackSize);
            itemDrop.m_itemData.m_worldLevel = (byte)Game.m_worldLevel;
        }

        if (spawned.TryGetComponent(out Rigidbody rigidbody))
        {
            Vector3 launchVelocity = UnityEngine.Random.insideUnitSphere;
            if (launchVelocity.y < 0f)
            {
                launchVelocity.y = -launchVelocity.y;
            }

            rigidbody.AddForce(launchVelocity * 5f, ForceMode.VelocityChange);
        }
    }

    private static void SpawnConfiguredLooseDrops(List<KeyValuePair<GameObject, int>> drops, Vector3 centerPos, float dropArea)
    {
        foreach (KeyValuePair<GameObject, int> drop in drops)
        {
            for (int i = 0; i < drop.Value; i++)
            {
                SpawnConfiguredLooseItem(drop.Key, centerPos, dropArea);
            }
        }
    }

    private static void SpawnConfiguredLooseItem(GameObject prefab, Vector3 centerPos, float dropArea)
    {
        Quaternion rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0, 360), 0f);
        Vector3 spawnPoint = ResolveConfiguredDropSpawnPoint(centerPos, dropArea);
        GameObject spawned = UnityEngine.Object.Instantiate(prefab, spawnPoint, rotation);
        if (spawned.TryGetComponent(out ItemDrop itemDrop))
        {
            itemDrop.m_itemData.m_worldLevel = (byte)Game.m_worldLevel;
        }

        if (spawned.TryGetComponent(out Rigidbody rigidbody))
        {
            Vector3 launchVelocity = UnityEngine.Random.insideUnitSphere;
            if (launchVelocity.y < 0f)
            {
                launchVelocity.y = -launchVelocity.y;
            }

            rigidbody.AddForce(launchVelocity * 5f, ForceMode.VelocityChange);
        }
    }

    private static Vector3 ResolveConfiguredDropSpawnPoint(Vector3 centerPos, float dropArea)
    {
        Vector2 planarOffset = UnityEngine.Random.insideUnitCircle * dropArea;
        Vector3 spawnPoint = centerPos + new Vector3(planarOffset.x, 0f, planarOffset.y);
        if (TrySnapConfiguredDropSpawnPointToGround(ref spawnPoint))
        {
            return spawnPoint;
        }

        return spawnPoint;
    }

    private static bool TrySnapConfiguredDropSpawnPointToGround(ref Vector3 point)
    {
        if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(point, out float solidHeight))
        {
            point.y = solidHeight + ConfiguredDropGroundOffset;
            return true;
        }

        if (WorldGenerator.instance != null)
        {
            point.y = WorldGenerator.instance.GetHeight(point.x, point.z) + ConfiguredDropGroundOffset;
            return true;
        }

        return false;
    }

    private static int RollConfiguredDropAmount(GameObject prefab, int amountMin, int amountMax)
    {
        return prefab != null && prefab.TryGetComponent(out ItemDrop _)
            ? Game.instance.ScaleDrops(prefab, amountMin, amountMax)
            : UnityEngine.Random.Range(amountMin, amountMax);
    }

    private static GameObject? ResolveDropPrefab(string prefabName, string context)
    {
        string trimmedName = (prefabName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            WarnInvalidEntry($"Entry '{context}' references an empty drop prefab name.");
            return null;
        }

        GameObject? prefab = ResolveKnownPrefab(trimmedName);
        if (prefab == null)
        {
            WarnInvalidEntry($"Entry '{context}' references unknown drop prefab '{trimmedName}'.");
            return null;
        }

        return prefab;
    }

    private static GameObject? ResolveItemPrefab(string itemName, string context)
    {
        string trimmedName = (itemName ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            WarnInvalidEntry($"Entry '{context}' references an empty item prefab name.");
            return null;
        }

        GameObject? prefab = ResolveKnownPrefab(trimmedName);
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

    private static GameObject? ResolveKnownPrefab(string prefabName)
    {
        string trimmedName = (prefabName ?? "").Trim();
        return trimmedName.Length == 0
            ? null
            : ObjectDB.instance?.GetItemPrefab(trimmedName) ?? ZNetScene.instance?.GetPrefab(trimmedName);
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

    private static void WarnInvalidEntry(string message)
    {
        InvalidEntryWarnings.Warn(message);
    }

    private static void LogPartiallyAcceptedLocalConfiguration(int totalEntries, int acceptedEntries, IEnumerable<string> warnings)
    {
        int skippedEntries = Math.Max(0, totalEntries - acceptedEntries);
        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Skipped {skippedEntries.ToString(CultureInfo.InvariantCulture)} invalid character entr{(skippedEntries == 1 ? "y" : "ies")} and kept {acceptedEntries.ToString(CultureInfo.InvariantCulture)} valid entr{(acceptedEntries == 1 ? "y" : "ies")}.");
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
            $"Loaded {acceptedEntryCount} character drop configuration(s) from {loadedFileCount} override file(s).");
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

    private static void LogSyncedCharacterConfigurationLoaded(string payloadToken, int acceptedEntryCount)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Loaded {acceptedEntryCount} synchronized character configuration(s) from the server.");
    }

    private static void LogSyncedCharacterConfigurationFailure(string payloadToken, Exception ex)
    {
        DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to deserialize synchronized character payload DTO. {ex}");
    }

    private static InvalidEntryDiagnostics.SuppressionScope BeginInvalidEntryWarningSuppressionForSyncedClientBuild(string sourceName)
    {
        return InvalidEntryWarnings.BeginSuppressionForSyncedClientBuild(sourceName);
    }

    private static string CreateConfigurationContext(CharacterDropPrefabEntry entry)
    {
        string prefabName = string.IsNullOrWhiteSpace(entry.Prefab) ? "<missing prefab>" : entry.Prefab;
        return $"{prefabName} @ {DescribeEntrySource(entry)}";
    }

    private static string DescribeEntrySource(CharacterDropPrefabEntry entry)
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

    private static string BuildCompiledDropContext(CharacterDropPrefabEntry entry)
    {
        string prefabName = string.IsNullOrWhiteSpace(entry.Prefab) ? "<unknown prefab>" : entry.Prefab;
        string ruleId = string.IsNullOrWhiteSpace(entry.RuleId) ? "<unknown rule>" : entry.RuleId;
        return $"{prefabName}/{ruleId}@{DescribeEntrySource(entry)}";
    }

}
