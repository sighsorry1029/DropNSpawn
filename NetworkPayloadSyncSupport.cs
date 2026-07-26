using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private const string PayloadRequestRpc = "DropNSpawn Payload Request";
    private const string PayloadChunkRpc = "DropNSpawn Payload Chunk";
    private const int DefaultChunkSizeBytes = 240000;
    private const int MaxConcurrentSmallClientRequests = 2;
    private const int MaxConcurrentLargeClientRequests = 1;
    private const int MaxPayloadProcessingWorkers = 3;
    private const int MaxArtifactPrewarmWorkers = 1;
    private const int Sha256HexLength = 64;
    private const int MaxCompressedPayloadBytes = 64 * 1024 * 1024;
    private const int MaxUncompressedPayloadBytes = 256 * 1024 * 1024;
    private const int DecompressionBufferSizeBytes = 81920;
    private const float DefaultMaxDeltaChangedEntryRatio = 0.5f;
    private const float DefaultMaxDeltaCompressedSizeRatio = 0.85f;
    private const float RequestRetrySeconds = 5f;
    private const int TotalPublishedCacheBudgetBytes = 64 * 1024 * 1024;
    private const int PublishedCacheTrimLowWatermarkPercent = 85;
    private const int DeltaDtoVersion = 1;
    private const int FullTransferKind = 0;
    private const int DeltaTransferKind = 1;

    private static readonly object Sync = new();
    private static readonly FieldInfo? ZPackageWriterField = typeof(ZPackage).GetField("m_writer", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly struct DomainTransportPolicy
    {
        public DomainTransportPolicy(
            int publishedCacheBudgetBytes,
            bool usesLargeRequestLane,
            bool enableArtifactPrewarm)
        {
            PublishedCacheBudgetBytes = Math.Max(0, publishedCacheBudgetBytes);
            UsesLargeRequestLane = usesLargeRequestLane;
            EnableArtifactPrewarm = enableArtifactPrewarm;
        }

        public int PublishedCacheBudgetBytes { get; }
        public bool UsesLargeRequestLane { get; }
        public bool EnableArtifactPrewarm { get; }
    }

    private static DomainTransportPolicy ResolveTransportPolicy(DomainTransportProfile profile)
    {
        return profile switch
        {
            DomainTransportProfile.SmallConfig => new DomainTransportPolicy(
                8 * 1024 * 1024,
                usesLargeRequestLane: false,
                enableArtifactPrewarm: false),
            DomainTransportProfile.MediumConfig => new DomainTransportPolicy(
                12 * 1024 * 1024,
                usesLargeRequestLane: false,
                enableArtifactPrewarm: false),
            DomainTransportProfile.LargeConfig => new DomainTransportPolicy(
                24 * 1024 * 1024,
                usesLargeRequestLane: true,
                enableArtifactPrewarm: false),
            DomainTransportProfile.LargeWithArtifacts => new DomainTransportPolicy(
                24 * 1024 * 1024,
                usesLargeRequestLane: true,
                enableArtifactPrewarm: true),
            _ => new DomainTransportPolicy(
                8 * 1024 * 1024,
                usesLargeRequestLane: false,
                enableArtifactPrewarm: false)
        };
    }

    private sealed class PayloadManifest
    {
        public string Hash { get; set; } = "";
        public int CompressedSize { get; set; }
        public int ChunkCount { get; set; }
        public int? EntryCount { get; set; }

        public bool IsEmpty => Hash.Length == 0;

        public string Serialize()
        {
            return IsEmpty
                ? ""
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "v2|{0}|{1}|{2}|{3}",
                    Hash,
                    CompressedSize,
                    ChunkCount,
                    Math.Max(0, EntryCount ?? 0));
        }

        public static bool TryParse(string? raw, out PayloadManifest manifest)
        {
            manifest = new PayloadManifest();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            string[] parts = (raw ?? "").Split('|');
            if (parts.Length != 5 || !string.Equals(parts[0], "v2", StringComparison.Ordinal))
            {
                return false;
            }

            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int compressedSize) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int chunkCount))
            {
                return false;
            }

            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedEntryCount))
            {
                return false;
            }

            if (!IsValidPayloadHash(parts[1]) ||
                compressedSize <= 0 ||
                compressedSize > MaxCompressedPayloadBytes ||
                chunkCount <= 0 ||
                parsedEntryCount < 0)
            {
                return false;
            }

            manifest = new PayloadManifest
            {
                Hash = parts[1],
                CompressedSize = compressedSize,
                ChunkCount = chunkCount,
                EntryCount = parsedEntryCount
            };

            return true;
        }
    }

    private sealed class PayloadSignatureBuilder
    {
        private readonly StringBuilder _builder = new();

        internal void WriteBool(bool value)
        {
            _builder.Append(value ? "T;" : "F;");
        }

        internal void WriteInt(int value)
        {
            _builder.Append('I')
                .Append(value.ToString(CultureInfo.InvariantCulture))
                .Append(';');
        }

        internal void WriteFloat(float value)
        {
            _builder.Append('R')
                .Append(value.ToString("R", CultureInfo.InvariantCulture))
                .Append(';');
        }

        internal void WriteString(string value)
        {
            string normalized = value ?? "";
            _builder.Append('S')
                .Append(normalized.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(normalized)
                .Append(';');
        }

        internal string ComputeHash()
        {
            return ReferenceRefreshSupport.ComputeStableHash(_builder.ToString());
        }
    }

    private interface IDomainTransport
    {
        string DomainKey { get; }
        bool UsesLargeRequestLane { get; }
        int ClientRequestPriority { get; }
        int PublishedCacheBytes { get; }
        int PublishedPayloadHistoryItemCount { get; }
        int PublishedTransferArtifactCount { get; }
        bool HasWaitingRequest();
        int CountClientRequestInFlight(bool usesLargeRequestLane);
        bool TryStartDesiredPayloadRequest();
        void UpdatePinnedOutboundTransferReferences(OutboundTransferState? previousState, OutboundTransferState? nextState);
        void EnqueuePublishedPayloadChunks(long sender, string requestedHash, string baseHash, long requestId);
        void ReceivePayloadChunk(long sender, string hash, long requestId, int transferKind, string baseHash, int chunkIndex, int chunkCount, int compressedSize, byte[] chunkBytes);
        bool TryTrimPublishedCacheOnce();
        void ClearPinnedCacheReferences();
        void ResetState();
    }

    private sealed class DomainTransport<TEntry> : IDomainTransport
    {
        public DomainTransport(
            DomainTransportMetadata<TEntry> metadata,
            DomainCodec<TEntry> codec)
        {
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            TransportPolicy = ResolveTransportPolicy(metadata.TransportProfile);
            DomainKey = metadata.DomainKey;
            DisplayName = metadata.DisplayName;
            CacheDirectoryName = metadata.CacheDirectoryName;
            UsesLargeRequestLane = TransportPolicy.UsesLargeRequestLane;
            PublishedCacheBudgetBytes = TransportPolicy.PublishedCacheBudgetBytes;
            EnableArtifactPrewarm = TransportPolicy.EnableArtifactPrewarm;
            Codec = codec;
            SignatureBuilder = codec.SignatureBuilder;
            EntrySignatureBuilder = codec.EntrySignatureBuilder;
            Serializer = codec.Serializer;
            Deserializer = codec.Deserializer;
            KeySelector = metadata.KeySelector;
            ReloadAction = metadata.ApplyPayloadAction;
        }

        public DomainTransportMetadata<TEntry> Metadata { get; }
        public DomainTransportPolicy TransportPolicy { get; }
        public string DomainKey { get; }
        public string DisplayName { get; }
        public string CacheDirectoryName { get; }
        public bool UsesLargeRequestLane { get; }
        public int PublishedCacheBudgetBytes { get; }
        public int ChunkSizeBytes => DefaultChunkSizeBytes;
        public bool EnableArtifactPrewarm { get; }
        public DomainCodec<TEntry> Codec { get; }
        public Func<List<TEntry>, string> SignatureBuilder { get; }
        public Func<TEntry, string> EntrySignatureBuilder { get; }
        public Func<List<TEntry>, byte[]> Serializer { get; }
        public Func<byte[], List<TEntry>> Deserializer { get; }
        public Func<TEntry, string> KeySelector { get; }
        public Action ReloadAction { get; }

        public string LastPublishedSignature = "";
        public string LastPublishedManifest = "";
        public byte[]? PublishedPayloadBytes;
        public byte[]? PublishedCompressedBytes;
        public PayloadEntryIndex<TEntry>? PublishedPayloadIndex;
        public PayloadManifest PublishedPayloadManifest = new();
        public Dictionary<string, PublishedPayloadHistoryEntry<TEntry>> PublishedPayloadHistory { get; } = new(StringComparer.Ordinal);
        public LinkedList<string> PublishedPayloadHistoryLru { get; } = new();
        public int PublishedPayloadHistoryBytes;

        public string DesiredManifest = "";
        public PayloadManifest DesiredPayloadManifest = new();
        public string InvalidDesiredManifest = "";
        public string BlockedManifestHash = "";
        public string BlockedManifestReason = "";
        public string FullFallbackAttemptedHash = "";
        public string AvailableHash = "";
        public byte[]? AvailablePayloadBytes;
        public List<TEntry>? AvailableEntries;
        public PayloadEntryIndex<TEntry>? AvailablePayloadIndex;
        public PendingInboundTransfer? PendingTransfer;
        public long NextRequestId;
        public bool RequestInFlight;
        public float RequestStartedAt;
        public bool ProcessingInFlight;
        public string ProcessingHash = "";
        public int ProcessingVersion;
        public Dictionary<string, TransferArtifact> PublishedTransferArtifacts { get; } = new(StringComparer.Ordinal);
        public LinkedList<string> PublishedTransferArtifactLru { get; } = new();
        public int PublishedTransferArtifactBytes;
        public int PublishedCacheBytes;
        public Dictionary<string, int> PinnedPayloadHashRefCounts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> PinnedArtifactKeyRefCounts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> PendingArtifactBuildKeys { get; } = new(StringComparer.Ordinal);
        public int PublishVersion;
        public bool PublishInFlight;
        public string PendingPublishedSignature = "";

        int IDomainTransport.PublishedCacheBytes => PublishedCacheBytes;
        bool IDomainTransport.UsesLargeRequestLane => UsesLargeRequestLane;
        int IDomainTransport.ClientRequestPriority => Metadata.ClientRequestPriority;
        int IDomainTransport.PublishedPayloadHistoryItemCount => PublishedPayloadHistory.Count;
        int IDomainTransport.PublishedTransferArtifactCount => PublishedTransferArtifacts.Count;
        bool IDomainTransport.HasWaitingRequest() => HasWaitingRequestLocked(this);
        int IDomainTransport.CountClientRequestInFlight(bool usesLargeRequestLane) => CountClientRequestInFlightLocked(this, usesLargeRequestLane);
        bool IDomainTransport.TryStartDesiredPayloadRequest() => TryStartDesiredPayloadRequestLocked(this);
        void IDomainTransport.UpdatePinnedOutboundTransferReferences(OutboundTransferState? previousState, OutboundTransferState? nextState) =>
            UpdatePinnedOutboundTransferReferencesLocked(this, previousState, nextState);
        void IDomainTransport.EnqueuePublishedPayloadChunks(long sender, string requestedHash, string baseHash, long requestId) =>
            EnqueuePublishedPayloadChunksLocked(this, sender, requestedHash, baseHash, requestId);
        void IDomainTransport.ReceivePayloadChunk(long sender, string hash, long requestId, int transferKind, string baseHash, int chunkIndex, int chunkCount, int compressedSize, byte[] chunkBytes) =>
            ReceivePayloadChunkLocked(this, sender, hash, requestId, transferKind, baseHash, chunkIndex, chunkCount, compressedSize, chunkBytes);
        bool IDomainTransport.TryTrimPublishedCacheOnce() => TryTrimTransportCacheOnceLocked(this);
        void IDomainTransport.ClearPinnedCacheReferences() => ClearPinnedCacheReferencesLocked(this);

        void IDomainTransport.ResetState()
        {
            NetworkPayloadSyncSupport.ResetTransportState(this);
        }
    }

    private readonly struct PreparedPublishPayload<TEntry>
    {
        public PreparedPublishPayload(string signature, string payloadHash, byte[] payloadBytes, int entryCount)
        {
            Signature = signature ?? "";
            PayloadHash = payloadHash ?? "";
            PayloadBytes = payloadBytes ?? Array.Empty<byte>();
            EntryCount = Math.Max(0, entryCount);
        }

        public string Signature { get; }
        public string PayloadHash { get; }
        public byte[] PayloadBytes { get; }
        public int EntryCount { get; }
    }

    private readonly struct OutboundTransferKey : IEquatable<OutboundTransferKey>
    {
        public OutboundTransferKey(long sender, string domainKey)
        {
            Sender = sender;
            DomainKey = domainKey ?? "";
        }

        public long Sender { get; }
        public string DomainKey { get; }

        public bool Equals(OutboundTransferKey other)
        {
            return Sender == other.Sender &&
                   string.Equals(DomainKey, other.DomainKey, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is OutboundTransferKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Sender.GetHashCode() * 397) ^ StringComparer.Ordinal.GetHashCode(DomainKey);
            }
        }
    }

    private static readonly Dictionary<string, IDomainTransport> TransportsByDomainKey =
        DomainRegistry.Transports.ToDictionary(
            static metadata => metadata.DomainKey,
            CreateTransport,
            StringComparer.Ordinal);
    private static readonly IDomainTransport[] AllTransports =
        DomainRegistry.Transports
            .Select(static metadata => TransportsByDomainKey[metadata.DomainKey])
            .ToArray();
    private static readonly IDomainTransport[] SmallLaneRequestPriorityTransports =
        AllTransports
            .Where(static transport => !transport.UsesLargeRequestLane)
            .OrderByDescending(static transport => transport.ClientRequestPriority)
            .ToArray();
    private static readonly IDomainTransport[] LargeLaneRequestPriorityTransports =
        AllTransports
            .Where(static transport => transport.UsesLargeRequestLane)
            .OrderByDescending(static transport => transport.ClientRequestPriority)
            .ToArray();

    private static IDomainTransport CreateTransport(DomainTransportMetadata metadata)
    {
        return metadata switch
        {
            DomainTransportMetadata<PrefabConfigurationEntry> typed => new DomainTransport<PrefabConfigurationEntry>(typed, ObjectCodec),
            DomainTransportMetadata<CharacterDropPrefabEntry> typed => new DomainTransport<CharacterDropPrefabEntry>(typed, CharacterCodec),
            DomainTransportMetadata<SpawnerConfigurationEntry> typed => new DomainTransport<SpawnerConfigurationEntry>(typed, SpawnerCodec),
            DomainTransportMetadata<CanonicalSpawnSystemEntry> typed => new DomainTransport<CanonicalSpawnSystemEntry>(typed, SpawnSystemCodec),
            DomainTransportMetadata<EventDefinition> typed => new DomainTransport<EventDefinition>(typed, EventCodec),
            _ => throw new InvalidOperationException($"Unsupported transport metadata for domain '{metadata.DomainKey}'.")
        };
    }

    private static MonoBehaviour? _host;
    private static ZRoutedRpc? _registeredRpc;
    private static readonly RingBufferQueue<OutboundTransferKey> PendingOutboundTransferKeys = new();
    private static readonly Dictionary<OutboundTransferKey, OutboundTransferState> ActiveOutboundTransfers = new();
    private static readonly RingBufferQueue<PendingPayloadProcessingJob> PendingCriticalPayloadProcessingJobs = new();
    private static readonly RingBufferQueue<PendingPayloadProcessingJob> PendingDeltaArtifactPrewarmJobs = new();
    private static readonly RingBufferQueue<PendingPayloadProcessingJob> PendingCachePersistenceJobs = new();
    private static readonly RingBufferQueue<PendingMainThreadPayloadCommit> PendingMainThreadPayloadCommits = new();
    private static readonly RingBufferQueue<PendingReloadAction> PendingReloadActions = new();
    private static readonly HashSet<string> PendingReloadDomainKeys = new(StringComparer.Ordinal);
    private static int _outboundBudgetFrame = int.MinValue;
    private static int _outboundBytesSentThisFrame;
    private static int _payloadProcessingWorkersRunning;
    private static int _networkRoleEpoch;
    internal static int CurrentAuthorityEpoch => Volatile.Read(ref _networkRoleEpoch);
    internal static void Initialize(MonoBehaviour host)
    {
        lock (Sync)
        {
            _host = host;
            EnsureRpcRegisteredLocked();
        }
    }

    internal static void Shutdown()
    {
        lock (Sync)
        {
            _host = null;
            _registeredRpc = null;
            AdvanceRoleEpochAndResetWorkQueuesLocked();
            foreach (IDomainTransport transport in TransportsByDomainKey.Values)
            {
                transport.ResetState();
            }
        }
    }

    internal static bool HasPendingWork()
    {
        lock (Sync)
        {
            return PendingMainThreadPayloadCommits.Count > 0 ||
                   PendingReloadActions.Count > 0 ||
                   PendingOutboundTransferKeys.Count > 0 ||
                   HasPendingClientRequestWorkLocked();
        }
    }

    internal static bool ProcessPendingWork(double deadline)
    {
        PreparedOutboundChunk? preparedChunk = null;
        Action? mainThreadAction = null;
        int mainThreadActionRoleEpoch = -1;
        bool startedRequest = false;
        lock (Sync)
        {
            EnsureRpcRegisteredLocked();

            int currentFrame = Time.frameCount;
            if (_outboundBudgetFrame != currentFrame)
            {
                _outboundBudgetFrame = currentFrame;
                _outboundBytesSentThisFrame = 0;
            }

            if (TryDequeueCurrentMainThreadPayloadCommitLocked(out mainThreadAction))
            {
                mainThreadActionRoleEpoch = _networkRoleEpoch;
            }
            else if (TryDequeueCurrentReloadActionLocked(out PendingReloadAction? pendingReload))
            {
                mainThreadAction = pendingReload!.Action;
                mainThreadActionRoleEpoch = pendingReload.RoleEpoch;
            }
            else if (TryStartNextWaitingPayloadRequestLocked())
            {
                startedRequest = true;
            }
            else if (ZRoutedRpc.instance != null &&
                     Time.realtimeSinceStartupAsDouble < deadline &&
                     TryPrepareOutboundChunkForSendLocked(out preparedChunk))
            {
            }
        }

        if (mainThreadAction != null)
        {
            lock (Sync)
            {
                if (mainThreadActionRoleEpoch != _networkRoleEpoch)
                {
                    return true;
                }
            }

            mainThreadAction();
            return true;
        }

        if (preparedChunk != null)
        {
            bool shouldSend;
            lock (Sync)
            {
                shouldSend = preparedChunk.RoleEpoch == _networkRoleEpoch && DropNSpawnPlugin.IsSourceOfTruth;
            }

            bool sent = shouldSend && SendOutboundChunk(preparedChunk);
            lock (Sync)
            {
                FinalizePreparedOutboundChunkLocked(preparedChunk, sent);
            }

            return sent;
        }

        return startedRequest;
    }

    internal static void HandleSourceOfTruthChanged(bool isSourceOfTruth)
    {
        lock (Sync)
        {
            EnsureRpcRegisteredLocked();
            AdvanceRoleEpochAndResetWorkQueuesLocked();
            foreach (IDomainTransport transport in TransportsByDomainKey.Values)
            {
                transport.ResetState();
            }
        }
    }

    internal static bool IsPayloadCurrent<TEntry>(DomainDescriptor<TEntry> domain, string configurationSignature)
    {
        return IsPublishedPayloadCurrent(GetTransport(domain), configurationSignature);
    }

    internal static void PublishPayloadAsync<TEntry>(
        DomainDescriptor<TEntry> domain,
        List<TEntry> entries,
        string? knownSignature,
        Action<string> applyManifest)
    {
        QueuePublishPayload(GetTransport(domain), entries, knownSignature, applyManifest);
    }

    internal static List<TEntry> CloneEntries<TEntry>(DomainDescriptor<TEntry> domain, List<TEntry>? entries)
    {
        return GetTransport(domain).Codec.CloneEntries(entries);
    }

    internal static bool TryGetEntries<TEntry>(
        DomainDescriptor<TEntry> domain,
        string manifestRaw,
        out List<TEntry> entries,
        out string payloadToken)
    {
        return TryGetEntries(GetTransport(domain), manifestRaw, out entries, out payloadToken);
    }

    internal static void HandleManifestChanged<TEntry>(DomainDescriptor<TEntry> domain, string manifestRaw)
    {
        HandleManifestChanged(GetTransport(domain), manifestRaw);
    }

    private static DomainTransport<TEntry> GetTransport<TEntry>(DomainDescriptor<TEntry> domain)
    {
        if (TransportsByDomainKey.TryGetValue(domain.DomainKey, out IDomainTransport? transport) &&
            transport is DomainTransport<TEntry> typedTransport)
        {
            return typedTransport;
        }

        throw new InvalidOperationException($"No synchronized transport is registered for domain '{domain.DomainKey}'.");
    }

    private static void ResetTransportState<TEntry>(DomainTransport<TEntry> transport)
    {
        transport.LastPublishedSignature = "";
        transport.LastPublishedManifest = "";
        transport.PublishedPayloadBytes = null;
        transport.PublishedCompressedBytes = null;
        transport.PublishedPayloadIndex = null;
        transport.PublishedPayloadManifest = new PayloadManifest();
        ClearPublishedPayloadHistoryLocked(transport);
        ClearPublishedTransferArtifactsLocked(transport);
        ClearPinnedCacheReferencesLocked(transport);
        transport.PendingArtifactBuildKeys.Clear();
        transport.PublishVersion = 0;
        transport.PublishInFlight = false;
        transport.PendingPublishedSignature = "";
        ResetClientState(transport);
    }

    private static void ResetClientState<TEntry>(DomainTransport<TEntry> transport)
    {
        transport.DesiredManifest = "";
        transport.DesiredPayloadManifest = new PayloadManifest();
        transport.InvalidDesiredManifest = "";
        ClearBlockedManifestLocked(transport);
        transport.FullFallbackAttemptedHash = "";
        transport.AvailableHash = "";
        transport.AvailablePayloadBytes = null;
        transport.AvailableEntries = null;
        transport.AvailablePayloadIndex = null;
        transport.PendingTransfer = null;
        transport.NextRequestId = 0L;
        transport.RequestInFlight = false;
        transport.RequestStartedAt = 0f;
        transport.ProcessingInFlight = false;
        transport.ProcessingHash = "";
        transport.ProcessingVersion++;
    }

    private static bool IsPublishedPayloadCurrent<TEntry>(DomainTransport<TEntry> transport, string configurationSignature)
    {
        lock (Sync)
        {
            return IsPublishedPayloadCurrentLocked(transport, configurationSignature);
        }
    }

    private static bool IsPublishedPayloadCurrentLocked<TEntry>(DomainTransport<TEntry> transport, string configurationSignature)
    {
        return (string.Equals(transport.LastPublishedSignature, configurationSignature, StringComparison.Ordinal) &&
                transport.PublishedPayloadBytes != null &&
                transport.PublishedCompressedBytes != null &&
                transport.LastPublishedManifest.Length > 0) ||
               (transport.PublishInFlight &&
               configurationSignature.Length > 0 &&
                string.Equals(transport.PendingPublishedSignature, configurationSignature, StringComparison.Ordinal));
    }

    private static PreparedPublishPayload<TEntry> PreparePublishPayloadLocked<TEntry>(
        DomainTransport<TEntry> transport,
        List<TEntry>? entries,
        string? knownSignature = null)
    {
        List<TEntry> liveEntries = entries ?? new List<TEntry>();
        byte[] payloadBytes = transport.Serializer(liveEntries);
        EnsurePayloadSizeWithinLimit(payloadBytes.Length, transport.DisplayName);
        string payloadHash = ComputeSha256(payloadBytes);
        string payloadSignature = !string.IsNullOrWhiteSpace(knownSignature)
            ? knownSignature!
            : transport.SignatureBuilder(liveEntries);
        return new PreparedPublishPayload<TEntry>(payloadSignature, payloadHash, payloadBytes, liveEntries.Count);
    }

    private static bool TryReusePublishedManifestForSignatureLocked<TEntry>(
        DomainTransport<TEntry> transport,
        string? signature,
        out string manifest)
    {
        manifest = "";
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        if (!string.Equals(transport.LastPublishedSignature, signature, StringComparison.Ordinal) ||
            transport.PublishedPayloadBytes == null ||
            transport.PublishedCompressedBytes == null ||
            transport.LastPublishedManifest.Length == 0)
        {
            return false;
        }

        manifest = transport.LastPublishedManifest;
        return true;
    }

    private static bool IsPublishPendingForSignatureLocked<TEntry>(DomainTransport<TEntry> transport, string? signature)
    {
        return transport.PublishInFlight &&
               !string.IsNullOrWhiteSpace(signature) &&
               string.Equals(transport.PendingPublishedSignature, signature, StringComparison.Ordinal);
    }

    private static bool TryReusePublishedManifestForHashLocked<TEntry>(
        DomainTransport<TEntry> transport,
        string payloadHash,
        string payloadSignature,
        out string manifest)
    {
        manifest = "";
        if (!string.Equals(transport.PublishedPayloadManifest.Hash, payloadHash, StringComparison.Ordinal) ||
            transport.PublishedPayloadBytes == null ||
            transport.PublishedCompressedBytes == null ||
            transport.LastPublishedManifest.Length == 0)
        {
            return false;
        }

        transport.LastPublishedSignature = payloadSignature;
        manifest = transport.LastPublishedManifest;
        return true;
    }

    private static bool TryGetAvailableEntriesLocked<TEntry>(DomainTransport<TEntry> transport, out List<TEntry> entries)
    {
        if (transport.AvailablePayloadBytes == null)
        {
            entries = new List<TEntry>();
            return false;
        }

        try
        {
            if (transport.AvailableEntries != null)
            {
                entries = transport.Codec.CloneEntries(transport.AvailableEntries);
                return true;
            }

            List<TEntry> availableEntries = transport.Deserializer(transport.AvailablePayloadBytes) ?? new List<TEntry>();
            transport.AvailableEntries = availableEntries;
            if (!string.IsNullOrWhiteSpace(transport.AvailableHash))
            {
                BuildPayloadIndex(transport, availableEntries, transport.AvailableHash, out PayloadEntryIndex<TEntry>? payloadIndex);
                transport.AvailablePayloadIndex = payloadIndex;
            }

            entries = transport.Codec.CloneEntries(availableEntries);
            return true;
        }
        catch (Exception ex)
        {
            string hash = transport.AvailableHash;
            entries = new List<TEntry>();
            string reason = $"source=lazy_deserialize error={ex.Message}";
            InvalidateMalformedAvailablePayloadLocked(transport, hash, reason, ex);
            return false;
        }
    }

    private static void QueuePublishPayload<TEntry>(
        DomainTransport<TEntry> transport,
        List<TEntry> entries,
        string? knownSignature,
        Action<string> applyManifest)
    {
        bool applyImmediately = false;
        string manifestToApply = "";
        int publishVersion = 0;
        int roleEpoch = 0;
        PreparedPublishPayload<TEntry> preparedPayload = default;

        lock (Sync)
        {
            EnsureRpcRegisteredLocked();

            if (TryReusePublishedManifestForSignatureLocked(transport, knownSignature, out manifestToApply))
            {
                applyImmediately = true;
                goto PublishExit;
            }

            if (IsPublishPendingForSignatureLocked(transport, knownSignature))
            {
                return;
            }

            try
            {
                preparedPayload = PreparePublishPayloadLocked(transport, entries, knownSignature);
            }
            catch (Exception ex)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogError(
                    $"Failed to capture synchronized {transport.DisplayName} payload publish snapshot. {ex}");
                return;
            }

            if (TryReusePublishedManifestForSignatureLocked(transport, preparedPayload.Signature, out manifestToApply))
            {
                applyImmediately = true;
            }
            else if (IsPublishPendingForSignatureLocked(transport, preparedPayload.Signature))
            {
                return;
            }
            else if (TryReusePublishedManifestForHashLocked(
                         transport,
                         preparedPayload.PayloadHash,
                         preparedPayload.Signature,
                         out manifestToApply))
            {
                applyImmediately = true;
            }
            else
            {
                publishVersion = ++transport.PublishVersion;
                roleEpoch = _networkRoleEpoch;
                transport.PublishInFlight = true;
                transport.PendingPublishedSignature = preparedPayload.Signature;
                string previousHashSnapshot = transport.PublishedPayloadManifest.Hash;
                byte[]? previousPayloadBytesSnapshot = transport.PublishedPayloadBytes;
                PayloadEntryIndex<TEntry>? previousPayloadIndexSnapshot = transport.PublishedPayloadIndex;

                QueueCriticalPayloadProcessingJobLocked(() =>
                {
                    try
                    {
                        byte[] compressedPayload = CompressBytes(preparedPayload.PayloadBytes);
                        PayloadEntryIndex<TEntry>? preparedPublishedPayloadIndex = null;
                        TransferArtifact? primaryDeltaArtifact = null;
                        if (previousHashSnapshot.Length > 0 &&
                            !string.Equals(previousHashSnapshot, preparedPayload.PayloadHash, StringComparison.Ordinal))
                        {
                            TryBuildPrimaryDeltaArtifact(
                                transport,
                                previousHashSnapshot,
                                previousPayloadBytesSnapshot,
                                previousPayloadIndexSnapshot,
                                preparedPayload.PayloadHash,
                                preparedPayload.PayloadBytes,
                                targetPayloadIndex: null,
                                compressedPayload,
                                out preparedPublishedPayloadIndex,
                                out primaryDeltaArtifact);
                        }

                        PayloadManifest manifest = new()
                        {
                            Hash = preparedPayload.PayloadHash,
                            CompressedSize = compressedPayload.Length,
                            ChunkCount = Math.Max(1, (int)Math.Ceiling(compressedPayload.Length / (double)transport.ChunkSizeBytes)),
                            EntryCount = preparedPayload.EntryCount
                        };

                        QueueMainThreadPayloadCommitLocked(() =>
                        {
                            bool shouldApplyManifest = false;
                            string committedManifest = "";

                            lock (Sync)
                            {
                                if (roleEpoch != _networkRoleEpoch || publishVersion != transport.PublishVersion)
                                {
                                    return;
                                }

                                transport.PublishInFlight = false;
                                transport.PendingPublishedSignature = "";

                                if (TryReusePublishedManifestForHashLocked(
                                        transport,
                                        preparedPayload.PayloadHash,
                                        preparedPayload.Signature,
                                        out committedManifest))
                                {
                                    shouldApplyManifest = true;
                                }
                                else
                                {
                                    string previousHash = transport.PublishedPayloadManifest.Hash;
                                    RememberPublishedPayloadLocked(
                                        transport,
                                        previousHash,
                                        transport.PublishedPayloadBytes,
                                        transport.PublishedPayloadIndex);

                                    transport.LastPublishedSignature = preparedPayload.Signature;
                                    transport.LastPublishedManifest = manifest.Serialize();
                                    transport.PublishedPayloadBytes = preparedPayload.PayloadBytes;
                                    transport.PublishedCompressedBytes = compressedPayload;
                                    transport.PublishedPayloadIndex = preparedPublishedPayloadIndex;
                                    transport.PublishedPayloadManifest = manifest;
                                    ClearPublishedTransferArtifactsLocked(transport);
                                    EnsureFullTransferArtifactLocked(transport, preparedPayload.PayloadHash);
                                    bool storedPrimaryDeltaArtifact = false;
                                    if (primaryDeltaArtifact != null &&
                                        string.Equals(previousHash, previousHashSnapshot, StringComparison.Ordinal) &&
                                        !transport.PublishedTransferArtifacts.ContainsKey(primaryDeltaArtifact.CacheKey))
                                    {
                                        StoreTransferArtifactLocked(transport, primaryDeltaArtifact);
                                        storedPrimaryDeltaArtifact = true;
                                    }

                                    TrimTransportCachesLocked(transport);
                                    TrimAllPublishedCachesLocked();
                                    if (previousHash.Length > 0 &&
                                        !string.Equals(previousHash, preparedPayload.PayloadHash, StringComparison.Ordinal) &&
                                        !storedPrimaryDeltaArtifact &&
                                        !string.Equals(previousHash, previousHashSnapshot, StringComparison.Ordinal))
                                    {
                                        QueueTransferArtifactBuildIfNeededLocked(transport, preparedPayload.PayloadHash, previousHash);
                                    }

                                    committedManifest = transport.LastPublishedManifest;
                                    shouldApplyManifest = true;
                                }
                            }

                            if (shouldApplyManifest)
                            {
                                applyManifest(committedManifest);
                            }
                        }, roleEpoch);
                    }
                    catch (Exception ex)
                    {
                        QueueMainThreadPayloadCommitLocked(() =>
                        {
                            lock (Sync)
                            {
                                if (roleEpoch != _networkRoleEpoch || publishVersion != transport.PublishVersion)
                                {
                                    return;
                                }

                                transport.PublishInFlight = false;
                                transport.PendingPublishedSignature = "";
                            }

                            DropNSpawnPlugin.DropNSpawnLogger.LogError(
                                $"Failed to prepare synchronized {transport.DisplayName} payload publish. {ex}");
                        }, roleEpoch);
                    }
                }, roleEpoch);
            }

PublishExit:
            ;
        }

        if (applyImmediately)
        {
            applyManifest(manifestToApply);
        }
    }

    private static bool TryGetEntries<TEntry>(
        DomainTransport<TEntry> transport,
        string? manifestRaw,
        out List<TEntry> entries,
        out string payloadToken)
    {
        lock (Sync)
        {
            EnsureRpcRegisteredLocked();

            if (!TryParsePayloadManifest(transport, manifestRaw, out PayloadManifest manifest))
            {
                bool hasLastKnownGood = TryPreserveLastKnownGoodOnInvalidManifestLocked(
                    transport,
                    manifestRaw,
                    out entries,
                    out payloadToken,
                    out bool shouldLog);
                if (shouldLog)
                {
                    DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                        $"Received invalid synchronized {transport.DisplayName} payload manifest '{manifestRaw}'. Keeping the last known good client payload state.");
                }

                return hasLastKnownGood;
            }

            ClearInvalidManifestLocked(transport);
            transport.DesiredManifest = manifestRaw ?? "";
            transport.DesiredPayloadManifest = manifest;
            RefreshBlockedManifestForDesiredHashLocked(transport, manifest.Hash);
            CancelProcessingIfHashMismatchLocked(transport, manifest.Hash);

            if (manifest.IsEmpty)
            {
                entries = new List<TEntry>();
                payloadToken = "";
                return false;
            }

            if (string.Equals(transport.AvailableHash, manifest.Hash, StringComparison.Ordinal) &&
                transport.AvailablePayloadBytes != null &&
                TryGetAvailableEntriesLocked(transport, out entries))
            {
                payloadToken = manifest.Hash;
                return true;
            }

            if (IsManifestBlockedLocked(transport, manifest.Hash))
            {
                entries = new List<TEntry>();
                payloadToken = manifest.Hash;
                return false;
            }

            if (TryScheduleCachedPayloadLoadLocked(transport, manifest))
            {
                entries = new List<TEntry>();
                payloadToken = manifest.Hash;
                return false;
            }

            if (transport.ProcessingInFlight &&
                string.Equals(transport.ProcessingHash, manifest.Hash, StringComparison.Ordinal))
            {
                entries = new List<TEntry>();
                payloadToken = manifest.Hash;
                return false;
            }

            EnsurePayloadRequestedLocked(transport, manifest);

            entries = new List<TEntry>();
            payloadToken = manifest.Hash;
            return false;
        }
    }

    private static string NormalizeBaseHash(string? baseHash)
    {
        string normalized = string.IsNullOrWhiteSpace(baseHash) ? "" : (baseHash ?? "").Trim();
        return IsValidPayloadHash(normalized) ? normalized : "";
    }

    private static string BuildTransferArtifactCacheKey(string targetHash, string? baseHash)
    {
        return (targetHash ?? "") + "|" + NormalizeBaseHash(baseHash);
    }

    private static void AdvanceRoleEpochAndResetWorkQueuesLocked()
    {
        _networkRoleEpoch++;
        PendingOutboundTransferKeys.Clear();
        ActiveOutboundTransfers.Clear();
        foreach (IDomainTransport transport in AllTransports)
        {
            transport.ClearPinnedCacheReferences();
        }
        PendingCriticalPayloadProcessingJobs.Clear();
        PendingDeltaArtifactPrewarmJobs.Clear();
        PendingCachePersistenceJobs.Clear();
        PendingMainThreadPayloadCommits.Clear();
        PendingReloadActions.Clear();
        PendingReloadDomainKeys.Clear();
        _outboundBudgetFrame = int.MinValue;
        _outboundBytesSentThisFrame = 0;
    }

    private static bool TryReadCachedPayloadBytes(
        string cacheDirectoryName,
        string displayName,
        string hash,
        out byte[] payloadBytes,
        out byte[] compressedBytes)
    {
        string cachePath = GetCachePath(cacheDirectoryName, hash);
        payloadBytes = Array.Empty<byte>();
        compressedBytes = Array.Empty<byte>();
        if (!File.Exists(cachePath))
        {
            return false;
        }

        long cachedLength = new FileInfo(cachePath).Length;
        if (cachedLength <= 0 || cachedLength > MaxCompressedPayloadBytes)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                $"Discarding cached {displayName} payload '{hash}' because its compressed size {cachedLength.ToString(CultureInfo.InvariantCulture)} is outside the supported range.");
            File.Delete(cachePath);
            return false;
        }

        compressedBytes = File.ReadAllBytes(cachePath);
        payloadBytes = DecompressBytes(compressedBytes);
        string payloadHash = ComputeSha256(payloadBytes);
        if (string.Equals(payloadHash, hash, StringComparison.Ordinal))
        {
            return true;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Discarding stale cached {displayName} payload '{hash}' because the cached hash does not match.");
        File.Delete(cachePath);
        payloadBytes = Array.Empty<byte>();
        compressedBytes = Array.Empty<byte>();
        return false;
    }

    private static string GetCachePath(string cacheDirectoryName, string hash)
    {
        if (!IsValidPayloadHash(hash))
        {
            throw new InvalidDataException("Payload cache hash must be a lowercase SHA-256 value.");
        }

        string cacheDirectory = Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, "cache");
        return Path.Combine(cacheDirectory, $"{hash}.{cacheDirectoryName}.bin");
    }

    private static void WriteCacheFile(string cacheDirectoryName, string hash, byte[] compressedBytes)
    {
        WriteCacheFile(cacheDirectoryName, hash, compressedBytes, compressedBytes?.Length ?? 0);
    }

    private static void WriteCacheFile(string cacheDirectoryName, string hash, byte[] compressedBytes, int compressedLength)
    {
        string cachePath = GetCachePath(cacheDirectoryName, hash);
        string? directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        int lengthToWrite = Math.Max(0, Math.Min(compressedLength, compressedBytes?.Length ?? 0));
        if (lengthToWrite <= 0 || lengthToWrite > MaxCompressedPayloadBytes)
        {
            throw new InvalidDataException(
                $"Compressed payload size {lengthToWrite.ToString(CultureInfo.InvariantCulture)} is outside the supported range.");
        }

        using FileStream stream = File.Create(cachePath);
        stream.Write(compressedBytes, 0, lengthToWrite);
    }

    private static void DeleteCacheFileIfPresent(string cacheDirectoryName, string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        try
        {
            string cachePath = GetCachePath(cacheDirectoryName, hash);
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
        }
        catch
        {
        }
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(bytes);
        StringBuilder builder = new(hash.Length * 2);
        foreach (byte value in hash)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static bool IsValidPayloadHash(string? hash)
    {
        if (hash == null || hash.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (char character in hash)
        {
            if (!(character is >= '0' and <= '9') &&
                !(character is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParsePayloadManifest<TEntry>(
        DomainTransport<TEntry> transport,
        string? manifestRaw,
        out PayloadManifest manifest)
    {
        if (!PayloadManifest.TryParse(manifestRaw, out manifest))
        {
            return false;
        }

        if (manifest.IsEmpty)
        {
            return string.IsNullOrWhiteSpace(manifestRaw);
        }

        int expectedChunkCount = Math.Max(
            1,
            (int)Math.Ceiling(manifest.CompressedSize / (double)transport.ChunkSizeBytes));
        return manifest.ChunkCount == expectedChunkCount;
    }

    private static void EnsurePayloadSizeWithinLimit(int payloadSize, string displayName)
    {
        if (payloadSize < 0 || payloadSize > MaxUncompressedPayloadBytes)
        {
            throw new InvalidDataException(
                $"Synchronized {displayName} payload size {payloadSize.ToString(CultureInfo.InvariantCulture)} exceeds the supported limit {MaxUncompressedPayloadBytes.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static byte[] CompressBytes(byte[] input)
    {
        EnsurePayloadSizeWithinLimit(input.Length, "payload");
        using MemoryStream output = new();
        using (GZipStream gzipStream = new(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzipStream.Write(input, 0, input.Length);
        }

        byte[] compressedBytes = output.ToArray();
        if (compressedBytes.Length > MaxCompressedPayloadBytes)
        {
            throw new InvalidDataException(
                $"Compressed payload size {compressedBytes.Length.ToString(CultureInfo.InvariantCulture)} exceeds the supported limit {MaxCompressedPayloadBytes.ToString(CultureInfo.InvariantCulture)}.");
        }

        return compressedBytes;
    }

    private static byte[] DecompressBytes(byte[] input)
    {
        return DecompressBytes(input, input?.Length ?? 0, 0);
    }

    private static byte[] DecompressBytes(byte[] input, int inputLength, int initialCapacityHint)
    {
        if (input == null || inputLength <= 0)
        {
            return Array.Empty<byte>();
        }

        int boundedInputLength = Math.Max(0, Math.Min(inputLength, input.Length));
        if (boundedInputLength > MaxCompressedPayloadBytes)
        {
            throw new InvalidDataException(
                $"Compressed payload size {boundedInputLength.ToString(CultureInfo.InvariantCulture)} exceeds the supported limit {MaxCompressedPayloadBytes.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (initialCapacityHint > MaxUncompressedPayloadBytes)
        {
            throw new InvalidDataException(
                $"Payload capacity hint {initialCapacityHint.ToString(CultureInfo.InvariantCulture)} exceeds the supported limit {MaxUncompressedPayloadBytes.ToString(CultureInfo.InvariantCulture)}.");
        }

        using MemoryStream source = new(input, 0, boundedInputLength, writable: false, publiclyVisible: true);
        using GZipStream gzipStream = new(source, CompressionMode.Decompress);
        using MemoryStream output = initialCapacityHint > 0 ? new MemoryStream(initialCapacityHint) : new MemoryStream();
        byte[] buffer = new byte[DecompressionBufferSizeBytes];
        int read;
        while ((read = gzipStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxUncompressedPayloadBytes)
            {
                throw new InvalidDataException(
                    $"Decompressed payload exceeds the supported limit {MaxUncompressedPayloadBytes.ToString(CultureInfo.InvariantCulture)}.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static void WriteCharacterDropDefinition(PayloadSignatureBuilder builder, CharacterDropDefinition definition)
    {
        WriteList(builder, definition.Drops, WriteCharacterDropEntryDefinition);
    }

    private static void WriteCharacterDropEntryDefinition(PayloadSignatureBuilder builder, CharacterDropEntryDefinition definition)
    {
        builder.WriteString(definition.Item ?? "");
        WriteOptional(builder, definition.Amount, WriteIntRangeDefinition);
        WriteNullableInt(builder, definition.AmountMin);
        WriteNullableInt(builder, definition.AmountMax);
        WriteNullableFloat(builder, definition.Chance);
        WriteNullableBool(builder, definition.DontScale);
        WriteNullableBool(builder, definition.LevelMultiplier);
        WriteNullableBool(builder, definition.OnePerPlayer);
        WriteNullableInt(builder, definition.AmountLimit);
        WriteNullableBool(builder, definition.DropInStack);
    }

    private static void WriteDropTableDefinition(PayloadSignatureBuilder builder, DropTableDefinition definition)
    {
        WriteDropTablePayloadDefinition(builder, definition);
    }

    private static void WriteDamageableDropTableDefinition(PayloadSignatureBuilder builder, DamageableDropTableDefinition definition)
    {
        WriteNullableFloat(builder, definition.Health);
        WriteNullableInt(builder, definition.MinToolTier);
        WriteDropTablePayloadDefinition(builder, definition);
    }

    private static void WriteDropTablePayloadDefinition(PayloadSignatureBuilder builder, DropTablePayloadDefinition definition)
    {
        WriteOptional(builder, definition.Rolls, WriteIntRangeDefinition);
        WriteNullableInt(builder, definition.DropMin);
        WriteNullableInt(builder, definition.DropMax);
        WriteNullableFloat(builder, definition.DropChance);
        WriteNullableBool(builder, definition.OneOfEach);
        WriteList(builder, definition.Drops, WriteDropEntryDefinition);
    }

    private static void WriteDropEntryDefinition(PayloadSignatureBuilder builder, DropEntryDefinition definition)
    {
        builder.WriteString(definition.Item ?? "");
        WriteOptional(builder, definition.Stack, WriteIntRangeDefinition);
        WriteNullableInt(builder, definition.StackMin);
        WriteNullableInt(builder, definition.StackMax);
        WriteNullableFloat(builder, definition.Weight);
        WriteNullableBool(builder, definition.DontScale);
    }

    private static void WriteSpawnerSyncSpawnAreaDefinition(PayloadSignatureBuilder builder, SpawnAreaDefinition definition)
    {
        WriteNullableFloat(builder, definition.LevelUpChance);
        WriteNullableFloat(builder, definition.SpawnInterval);
        WriteNullableFloat(builder, definition.TriggerDistance);
        WriteNullableBool(builder, definition.SetPatrolSpawnPoint);
        WriteNullableFloat(builder, definition.SpawnRadius);
        WriteNullableFloat(builder, definition.NearRadius);
        WriteNullableFloat(builder, definition.FarRadius);
        WriteNullableInt(builder, definition.MaxNear);
        WriteNullableInt(builder, definition.MaxTotal);
        WriteNullableInt(builder, definition.MaxTotalSpawns);
        WriteNullableBool(builder, definition.OnGroundOnly);
        WriteList(builder, definition.Creatures, WriteSpawnerSyncSpawnAreaCreatureDefinition);
    }

    private static void WriteSpawnerSyncSpawnAreaCreatureDefinition(PayloadSignatureBuilder builder, SpawnAreaSpawnDefinition definition)
    {
        builder.WriteString(definition.Creature ?? "");
        WriteNullableFloat(builder, definition.Weight);
        WriteOptional(builder, definition.Level, WriteIntRangeDefinition);
        WriteNullableInt(builder, definition.MinLevel);
        WriteNullableInt(builder, definition.MaxLevel);
        WriteNullableString(builder, definition.Faction);
        WriteNullableString(builder, definition.Data);
        WriteStringDictionary(builder, definition.Fields);
        WriteStringList(builder, definition.Objects);
    }

    private static void WriteSpawnerSyncCreatureSpawnerDefinition(PayloadSignatureBuilder builder, CreatureSpawnerDefinition definition)
    {
        WriteNullableString(builder, definition.Creature);
        WriteOptional(builder, definition.TimeOfDay, WriteTimeOfDayDefinition);
        WriteNullableString(builder, definition.RequiredGlobalKey);
        WriteNullableString(builder, definition.BlockingGlobalKey);
        WriteOptional(builder, definition.Level, WriteIntRangeDefinition);
        WriteNullableInt(builder, definition.MinLevel);
        WriteNullableInt(builder, definition.MaxLevel);
        WriteNullableFloat(builder, definition.LevelUpChance);
        WriteNullableFloat(builder, definition.RespawnTimeMinutes);
        WriteNullableInt(builder, definition.SpawnCheckInterval);
        WriteNullableInt(builder, definition.SpawnGroupId);
        WriteNullableFloat(builder, definition.SpawnGroupRadius);
        WriteNullableFloat(builder, definition.SpawnerWeight);
        WriteNullableInt(builder, definition.MaxGroupSpawned);
        WriteNullableFloat(builder, definition.TriggerDistance);
        WriteNullableFloat(builder, definition.TriggerNoise);
        WriteNullableBool(builder, definition.RequireSpawnArea);
        WriteNullableBool(builder, definition.AllowInsidePlayerBase);
        WriteNullableBool(builder, definition.WakeUpAnimation);
        WriteNullableBool(builder, definition.SetPatrolSpawnPoint);
        WriteNullableString(builder, definition.Faction);
        WriteNullableString(builder, definition.Data);
        WriteStringDictionary(builder, definition.Fields);
        WriteStringList(builder, definition.Objects);
    }

    private static void WritePickableDefinition(PayloadSignatureBuilder builder, PickableDefinition definition)
    {
        WriteNullableString(builder, definition.OverrideName);
        WriteOptional(builder, definition.Drop, WritePickableDropDefinition);
        WriteOptional(builder, definition.ExtraDrops, WriteDropTablePayloadDefinition);
    }

    private static void WritePickableDropDefinition(PayloadSignatureBuilder builder, PickableDropDefinition definition)
    {
        builder.WriteString(definition.Item ?? "");
        WriteNullableInt(builder, definition.Amount);
        WriteNullableInt(builder, definition.MinAmountScaled);
        WriteNullableBool(builder, definition.DontScale);
    }

    private static void WritePickableItemDefinition(PayloadSignatureBuilder builder, PickableItemDefinition definition)
    {
        WriteList(builder, definition.RandomDrops, WriteRandomPickableItemDefinition);
        WriteOptional(builder, definition.Drop, WritePickableItemDropDefinition);
    }

    private static void WriteFishDefinition(PayloadSignatureBuilder builder, FishDefinition definition)
    {
        WriteOptional(builder, definition.ExtraDrops, WriteDropTablePayloadDefinition);
    }

    private static void WriteDestructibleDefinition(PayloadSignatureBuilder builder, DestructibleDefinition definition)
    {
        WriteNullableFloat(builder, definition.Health);
        WriteNullableInt(builder, definition.MinToolTier);
        WriteNullableString(builder, definition.DestructibleType);
        WriteNullableString(builder, definition.SpawnWhenDestroyed);
    }

    private static void WritePickableItemDropDefinition(PayloadSignatureBuilder builder, PickableItemDropDefinition definition)
    {
        builder.WriteString(definition.Item ?? "");
        WriteNullableInt(builder, definition.Stack);
    }

    private static void WriteRandomPickableItemDefinition(PayloadSignatureBuilder builder, RandomPickableItemDefinition definition)
    {
        builder.WriteString(definition.Item ?? "");
        WriteOptional(builder, definition.Stack, WriteIntRangeDefinition);
        WriteNullableInt(builder, definition.StackMin);
        WriteNullableInt(builder, definition.StackMax);
        WriteNullableFloat(builder, definition.Weight);
    }

    private static void WriteConditionsDefinition(PayloadSignatureBuilder builder, ConditionsDefinition definition, bool includeResolvedBiomeMask)
    {
        WriteStringList(builder, definition.Biomes);
        if (includeResolvedBiomeMask)
        {
            WriteNullableInt(builder, GetResolvedBiomeMaskValue(definition.Biomes, definition.ResolvedBiomeMask));
        }
        WriteStringList(builder, definition.Locations);
        WriteStringList(builder, definition.States);
        WriteStringList(builder, definition.Factions);
        WriteStringList(builder, definition.RequiredEnvironments);
        WriteStringList(builder, definition.RequiredGlobalKeys);
        WriteStringList(builder, definition.ForbiddenGlobalKeys);
        WriteOptional(builder, definition.Level, WriteIntRangeDefinition);
        WriteNullableInt(builder, definition.MinLevel);
        WriteNullableInt(builder, definition.MaxLevel);
        WriteOptional(builder, definition.Altitude, WriteFloatRangeDefinition);
        WriteNullableFloat(builder, definition.MinAltitude);
        WriteNullableFloat(builder, definition.MaxAltitude);
        WriteOptional(builder, definition.DistanceFromCenter, WriteFloatRangeDefinition);
        WriteNullableFloat(builder, definition.MinDistanceFromCenter);
        WriteNullableFloat(builder, definition.MaxDistanceFromCenter);
        WriteOptional(builder, definition.TimeOfDay, WriteTimeOfDayDefinition);
        WriteNullableBool(builder, definition.InDungeon);
        WriteNullableBool(builder, definition.InForest);
        WriteNullableBool(builder, definition.InsidePlayerBase);
    }

    private static void WriteIntRangeDefinition(PayloadSignatureBuilder builder, IntRangeDefinition definition)
    {
        WriteNullableInt(builder, definition.Min);
        WriteNullableInt(builder, definition.Max);
    }

    private static void WriteFloatRangeDefinition(PayloadSignatureBuilder builder, FloatRangeDefinition definition)
    {
        WriteNullableFloat(builder, definition.Min);
        WriteNullableFloat(builder, definition.Max);
    }

    private static void WriteTimeOfDayDefinition(PayloadSignatureBuilder builder, TimeOfDayDefinition definition)
    {
        WriteStringList(builder, definition.Values);
    }

    private static void WriteCharacterDropDefinition(ZPackage package, CharacterDropDefinition definition)
    {
        WriteList(package, definition.Drops, WriteCharacterDropEntryDefinition);
    }

    private static CharacterDropDefinition ReadCharacterDropDefinition(ZPackage package)
    {
        return new CharacterDropDefinition
        {
            Drops = ReadList(package, ReadCharacterDropEntryDefinition)
        };
    }

    private static void WriteCharacterDropEntryDefinition(ZPackage package, CharacterDropEntryDefinition definition)
    {
        package.Write(definition.Item ?? "");
        WriteOptional(package, definition.Amount, WriteIntRangeDefinition);
        WriteNullableInt(package, definition.AmountMin);
        WriteNullableInt(package, definition.AmountMax);
        WriteNullableFloat(package, definition.Chance);
        WriteNullableBool(package, definition.DontScale);
        WriteNullableBool(package, definition.LevelMultiplier);
        WriteNullableBool(package, definition.OnePerPlayer);
        WriteNullableInt(package, definition.AmountLimit);
        WriteNullableBool(package, definition.DropInStack);
    }

    private static CharacterDropEntryDefinition ReadCharacterDropEntryDefinition(ZPackage package)
    {
        return new CharacterDropEntryDefinition
        {
            Item = package.ReadString(),
            Amount = ReadOptional(package, ReadIntRangeDefinition),
            AmountMin = ReadNullableInt(package),
            AmountMax = ReadNullableInt(package),
            Chance = ReadNullableFloat(package),
            DontScale = ReadNullableBool(package),
            LevelMultiplier = ReadNullableBool(package),
            OnePerPlayer = ReadNullableBool(package),
            AmountLimit = ReadNullableInt(package),
            DropInStack = ReadNullableBool(package)
        };
    }

    private static void WriteDropTableDefinition(ZPackage package, DropTableDefinition definition)
    {
        WriteDropTablePayloadDefinition(package, definition);
    }

    private static DropTableDefinition ReadDropTableDefinition(ZPackage package)
    {
        DropTablePayloadDefinition payload = ReadDropTablePayloadDefinition(package);
        return new DropTableDefinition
        {
            Rolls = payload.Rolls,
            DropMin = payload.DropMin,
            DropMax = payload.DropMax,
            DropChance = payload.DropChance,
            OneOfEach = payload.OneOfEach,
            Drops = payload.Drops
        };
    }

    private static void WriteDamageableDropTableDefinition(ZPackage package, DamageableDropTableDefinition definition)
    {
        WriteNullableFloat(package, definition.Health);
        WriteNullableInt(package, definition.MinToolTier);
        WriteDropTablePayloadDefinition(package, definition);
    }

    private static DamageableDropTableDefinition ReadDamageableDropTableDefinition(ZPackage package)
    {
        float? health = ReadNullableFloat(package);
        int? minToolTier = ReadNullableInt(package);
        DropTablePayloadDefinition payload = ReadDropTablePayloadDefinition(package);
        return new DamageableDropTableDefinition
        {
            Health = health,
            MinToolTier = minToolTier,
            Rolls = payload.Rolls,
            DropMin = payload.DropMin,
            DropMax = payload.DropMax,
            DropChance = payload.DropChance,
            OneOfEach = payload.OneOfEach,
            Drops = payload.Drops
        };
    }

    private static void WriteDropTablePayloadDefinition(ZPackage package, DropTablePayloadDefinition definition)
    {
        WriteOptional(package, definition.Rolls, WriteIntRangeDefinition);
        WriteNullableInt(package, definition.DropMin);
        WriteNullableInt(package, definition.DropMax);
        WriteNullableFloat(package, definition.DropChance);
        WriteNullableBool(package, definition.OneOfEach);
        WriteList(package, definition.Drops, WriteDropEntryDefinition);
    }

    private static DropTablePayloadDefinition ReadDropTablePayloadDefinition(ZPackage package)
    {
        return new DropTablePayloadDefinition
        {
            Rolls = ReadOptional(package, ReadIntRangeDefinition),
            DropMin = ReadNullableInt(package),
            DropMax = ReadNullableInt(package),
            DropChance = ReadNullableFloat(package),
            OneOfEach = ReadNullableBool(package),
            Drops = ReadList(package, ReadDropEntryDefinition)
        };
    }

    private static void WriteDropEntryDefinition(ZPackage package, DropEntryDefinition definition)
    {
        package.Write(definition.Item ?? "");
        WriteOptional(package, definition.Stack, WriteIntRangeDefinition);
        WriteNullableInt(package, definition.StackMin);
        WriteNullableInt(package, definition.StackMax);
        WriteNullableFloat(package, definition.Weight);
        WriteNullableBool(package, definition.DontScale);
    }

    private static DropEntryDefinition ReadDropEntryDefinition(ZPackage package)
    {
        return new DropEntryDefinition
        {
            Item = package.ReadString(),
            Stack = ReadOptional(package, ReadIntRangeDefinition),
            StackMin = ReadNullableInt(package),
            StackMax = ReadNullableInt(package),
            Weight = ReadNullableFloat(package),
            DontScale = ReadNullableBool(package)
        };
    }

    private static void WriteSpawnerSyncSpawnAreaDefinition(ZPackage package, SpawnAreaDefinition definition)
    {
        WriteNullableFloat(package, definition.LevelUpChance);
        WriteNullableFloat(package, definition.SpawnInterval);
        WriteNullableFloat(package, definition.TriggerDistance);
        WriteNullableBool(package, definition.SetPatrolSpawnPoint);
        WriteNullableFloat(package, definition.SpawnRadius);
        WriteNullableFloat(package, definition.NearRadius);
        WriteNullableFloat(package, definition.FarRadius);
        WriteNullableInt(package, definition.MaxNear);
        WriteNullableInt(package, definition.MaxTotal);
        WriteNullableInt(package, definition.MaxTotalSpawns);
        WriteNullableBool(package, definition.OnGroundOnly);
        WriteList(package, definition.Creatures, WriteSpawnerSyncSpawnAreaCreatureDefinition);
    }

    private static SpawnAreaDefinition ReadSpawnerSyncSpawnAreaDefinition(ZPackage package)
    {
        return new SpawnAreaDefinition
        {
            LevelUpChance = ReadNullableFloat(package),
            SpawnInterval = ReadNullableFloat(package),
            TriggerDistance = ReadNullableFloat(package),
            SetPatrolSpawnPoint = ReadNullableBool(package),
            SpawnRadius = ReadNullableFloat(package),
            NearRadius = ReadNullableFloat(package),
            FarRadius = ReadNullableFloat(package),
            MaxNear = ReadNullableInt(package),
            MaxTotal = ReadNullableInt(package),
            MaxTotalSpawns = ReadNullableInt(package),
            OnGroundOnly = ReadNullableBool(package),
            Creatures = ReadList(package, ReadSpawnerSyncSpawnAreaCreatureDefinition)
        };
    }

    private static void WriteSpawnerSyncSpawnAreaCreatureDefinition(ZPackage package, SpawnAreaSpawnDefinition definition)
    {
        package.Write(definition.Creature ?? "");
        WriteNullableFloat(package, definition.Weight);
        WriteOptional(package, definition.Level, WriteIntRangeDefinition);
        WriteNullableInt(package, definition.MinLevel);
        WriteNullableInt(package, definition.MaxLevel);
        WriteNullableString(package, definition.Faction);
        WriteNullableString(package, definition.Data);
        WriteStringDictionary(package, definition.Fields);
        WriteStringList(package, definition.Objects);
    }

    private static SpawnAreaSpawnDefinition ReadSpawnerSyncSpawnAreaCreatureDefinition(ZPackage package)
    {
        return new SpawnAreaSpawnDefinition
        {
            Creature = package.ReadString(),
            Weight = ReadNullableFloat(package),
            Level = ReadOptional(package, ReadIntRangeDefinition),
            MinLevel = ReadNullableInt(package),
            MaxLevel = ReadNullableInt(package),
            Faction = ReadNullableString(package),
            Data = ReadNullableString(package),
            Fields = ReadStringDictionary(package),
            Objects = ReadStringList(package)
        };
    }

    private static void WriteSpawnerSyncCreatureSpawnerDefinition(ZPackage package, CreatureSpawnerDefinition definition)
    {
        WriteNullableString(package, definition.Creature);
        WriteOptional(package, definition.TimeOfDay, WriteTimeOfDayDefinition);
        WriteNullableString(package, definition.RequiredGlobalKey);
        WriteNullableString(package, definition.BlockingGlobalKey);
        WriteOptional(package, definition.Level, WriteIntRangeDefinition);
        WriteNullableInt(package, definition.MinLevel);
        WriteNullableInt(package, definition.MaxLevel);
        WriteNullableFloat(package, definition.LevelUpChance);
        WriteNullableFloat(package, definition.RespawnTimeMinutes);
        WriteNullableInt(package, definition.SpawnCheckInterval);
        WriteNullableInt(package, definition.SpawnGroupId);
        WriteNullableFloat(package, definition.SpawnGroupRadius);
        WriteNullableFloat(package, definition.SpawnerWeight);
        WriteNullableInt(package, definition.MaxGroupSpawned);
        WriteNullableFloat(package, definition.TriggerDistance);
        WriteNullableFloat(package, definition.TriggerNoise);
        WriteNullableBool(package, definition.RequireSpawnArea);
        WriteNullableBool(package, definition.AllowInsidePlayerBase);
        WriteNullableBool(package, definition.WakeUpAnimation);
        WriteNullableBool(package, definition.SetPatrolSpawnPoint);
        WriteNullableString(package, definition.Faction);
        WriteNullableString(package, definition.Data);
        WriteStringDictionary(package, definition.Fields);
        WriteStringList(package, definition.Objects);
    }

    private static CreatureSpawnerDefinition ReadSpawnerSyncCreatureSpawnerDefinition(ZPackage package)
    {
        return new CreatureSpawnerDefinition
        {
            Creature = ReadNullableString(package),
            TimeOfDay = ReadOptional(package, ReadTimeOfDayDefinition),
            RequiredGlobalKey = ReadNullableString(package),
            BlockingGlobalKey = ReadNullableString(package),
            Level = ReadOptional(package, ReadIntRangeDefinition),
            MinLevel = ReadNullableInt(package),
            MaxLevel = ReadNullableInt(package),
            LevelUpChance = ReadNullableFloat(package),
            RespawnTimeMinutes = ReadNullableFloat(package),
            SpawnCheckInterval = ReadNullableInt(package),
            SpawnGroupId = ReadNullableInt(package),
            SpawnGroupRadius = ReadNullableFloat(package),
            SpawnerWeight = ReadNullableFloat(package),
            MaxGroupSpawned = ReadNullableInt(package),
            TriggerDistance = ReadNullableFloat(package),
            TriggerNoise = ReadNullableFloat(package),
            RequireSpawnArea = ReadNullableBool(package),
            AllowInsidePlayerBase = ReadNullableBool(package),
            WakeUpAnimation = ReadNullableBool(package),
            SetPatrolSpawnPoint = ReadNullableBool(package),
            Faction = ReadNullableString(package),
            Data = ReadNullableString(package),
            Fields = ReadStringDictionary(package),
            Objects = ReadStringList(package)
        };
    }

    private static void WritePickableDefinition(ZPackage package, PickableDefinition definition)
    {
        WriteNullableString(package, definition.OverrideName);
        WriteOptional(package, definition.Drop, WritePickableDropDefinition);
        WriteOptional(package, definition.ExtraDrops, WriteDropTablePayloadDefinition);
    }

    private static PickableDefinition ReadPickableDefinition(ZPackage package)
    {
        return new PickableDefinition
        {
            OverrideName = ReadNullableString(package),
            Drop = ReadOptional(package, ReadPickableDropDefinition),
            ExtraDrops = ReadOptional(package, ReadDropTablePayloadDefinition)
        };
    }

    private static void WritePickableDropDefinition(ZPackage package, PickableDropDefinition definition)
    {
        package.Write(definition.Item ?? "");
        WriteNullableInt(package, definition.Amount);
        WriteNullableInt(package, definition.MinAmountScaled);
        WriteNullableBool(package, definition.DontScale);
    }

    private static PickableDropDefinition ReadPickableDropDefinition(ZPackage package)
    {
        return new PickableDropDefinition
        {
            Item = package.ReadString(),
            Amount = ReadNullableInt(package),
            MinAmountScaled = ReadNullableInt(package),
            DontScale = ReadNullableBool(package)
        };
    }

    private static void WritePickableItemDefinition(ZPackage package, PickableItemDefinition definition)
    {
        WriteList(package, definition.RandomDrops, WriteRandomPickableItemDefinition);
        WriteOptional(package, definition.Drop, WritePickableItemDropDefinition);
    }

    private static void WriteFishDefinition(ZPackage package, FishDefinition definition)
    {
        WriteOptional(package, definition.ExtraDrops, WriteDropTablePayloadDefinition);
    }

    private static FishDefinition ReadFishDefinition(ZPackage package)
    {
        return new FishDefinition
        {
            ExtraDrops = ReadOptional(package, ReadDropTablePayloadDefinition)
        };
    }

    private static PickableItemDefinition ReadPickableItemDefinition(ZPackage package)
    {
        return new PickableItemDefinition
        {
            RandomDrops = ReadList(package, ReadRandomPickableItemDefinition),
            Drop = ReadOptional(package, ReadPickableItemDropDefinition)
        };
    }

    private static void WriteDestructibleDefinition(ZPackage package, DestructibleDefinition definition)
    {
        WriteNullableFloat(package, definition.Health);
        WriteNullableInt(package, definition.MinToolTier);
        WriteNullableString(package, definition.DestructibleType);
        WriteNullableString(package, definition.SpawnWhenDestroyed);
    }

    private static DestructibleDefinition ReadDestructibleDefinition(ZPackage package)
    {
        return new DestructibleDefinition
        {
            Health = ReadNullableFloat(package),
            MinToolTier = ReadNullableInt(package),
            DestructibleType = ReadNullableString(package),
            SpawnWhenDestroyed = ReadNullableString(package)
        };
    }

    private static void WritePickableItemDropDefinition(ZPackage package, PickableItemDropDefinition definition)
    {
        package.Write(definition.Item ?? "");
        WriteNullableInt(package, definition.Stack);
    }

    private static PickableItemDropDefinition ReadPickableItemDropDefinition(ZPackage package)
    {
        return new PickableItemDropDefinition
        {
            Item = package.ReadString(),
            Stack = ReadNullableInt(package)
        };
    }

    private static void WriteRandomPickableItemDefinition(ZPackage package, RandomPickableItemDefinition definition)
    {
        package.Write(definition.Item ?? "");
        WriteOptional(package, definition.Stack, WriteIntRangeDefinition);
        WriteNullableInt(package, definition.StackMin);
        WriteNullableInt(package, definition.StackMax);
        WriteNullableFloat(package, definition.Weight);
    }

    private static RandomPickableItemDefinition ReadRandomPickableItemDefinition(ZPackage package)
    {
        return new RandomPickableItemDefinition
        {
            Item = package.ReadString(),
            Stack = ReadOptional(package, ReadIntRangeDefinition),
            StackMin = ReadNullableInt(package),
            StackMax = ReadNullableInt(package),
            Weight = ReadNullableFloat(package)
        };
    }

    private static void WriteConditionsDefinition(ZPackage package, ConditionsDefinition definition)
    {
        WriteStringList(package, definition.Biomes);
        WriteNullableInt(package, GetResolvedBiomeMaskValue(definition.Biomes, definition.ResolvedBiomeMask));
        WriteStringList(package, definition.Locations);
        WriteStringList(package, definition.States);
        WriteStringList(package, definition.Factions);
        WriteStringList(package, definition.RequiredEnvironments);
        WriteStringList(package, definition.RequiredGlobalKeys);
        WriteStringList(package, definition.ForbiddenGlobalKeys);
        WriteOptional(package, definition.Level, WriteIntRangeDefinition);
        WriteNullableInt(package, definition.MinLevel);
        WriteNullableInt(package, definition.MaxLevel);
        WriteOptional(package, definition.Altitude, WriteFloatRangeDefinition);
        WriteNullableFloat(package, definition.MinAltitude);
        WriteNullableFloat(package, definition.MaxAltitude);
        WriteOptional(package, definition.DistanceFromCenter, WriteFloatRangeDefinition);
        WriteNullableFloat(package, definition.MinDistanceFromCenter);
        WriteNullableFloat(package, definition.MaxDistanceFromCenter);
        WriteOptional(package, definition.TimeOfDay, WriteTimeOfDayDefinition);
        WriteNullableBool(package, definition.InDungeon);
        WriteNullableBool(package, definition.InForest);
        WriteNullableBool(package, definition.InsidePlayerBase);
    }

    private static ConditionsDefinition ReadConditionsDefinition(ZPackage package)
    {
        return new ConditionsDefinition
        {
            Biomes = ReadStringList(package),
            ResolvedBiomeMask = ReadNullableInt(package) is int resolvedBiomeMask ? (Heightmap.Biome)resolvedBiomeMask : null,
            Locations = ReadStringList(package),
            States = ReadStringList(package),
            Factions = ReadStringList(package),
            RequiredEnvironments = ReadStringList(package),
            RequiredGlobalKeys = ReadStringList(package),
            ForbiddenGlobalKeys = ReadStringList(package),
            Level = ReadOptional(package, ReadIntRangeDefinition),
            MinLevel = ReadNullableInt(package),
            MaxLevel = ReadNullableInt(package),
            Altitude = ReadOptional(package, ReadFloatRangeDefinition),
            MinAltitude = ReadNullableFloat(package),
            MaxAltitude = ReadNullableFloat(package),
            DistanceFromCenter = ReadOptional(package, ReadFloatRangeDefinition),
            MinDistanceFromCenter = ReadNullableFloat(package),
            MaxDistanceFromCenter = ReadNullableFloat(package),
            TimeOfDay = ReadOptional(package, ReadTimeOfDayDefinition),
            InDungeon = ReadNullableBool(package),
            InForest = ReadNullableBool(package),
            InsidePlayerBase = ReadNullableBool(package)
        };
    }

    private static int? GetResolvedBiomeMaskValue(IEnumerable<string>? configuredBiomes, Heightmap.Biome? resolvedBiomeMask)
    {
        if (resolvedBiomeMask.HasValue)
        {
            return (int)resolvedBiomeMask.Value;
        }

        return BiomeResolutionSupport.ResolveBiomeMaskOrNull(configuredBiomes) is Heightmap.Biome resolvedBiome
            ? (int)resolvedBiome
            : null;
    }

    private static void WriteIntRangeDefinition(ZPackage package, IntRangeDefinition definition)
    {
        WriteNullableInt(package, definition.Min);
        WriteNullableInt(package, definition.Max);
    }

    private static IntRangeDefinition ReadIntRangeDefinition(ZPackage package)
    {
        return new IntRangeDefinition
        {
            Min = ReadNullableInt(package),
            Max = ReadNullableInt(package)
        };
    }

    private static void WriteFloatRangeDefinition(ZPackage package, FloatRangeDefinition definition)
    {
        WriteNullableFloat(package, definition.Min);
        WriteNullableFloat(package, definition.Max);
    }

    private static FloatRangeDefinition ReadFloatRangeDefinition(ZPackage package)
    {
        return new FloatRangeDefinition
        {
            Min = ReadNullableFloat(package),
            Max = ReadNullableFloat(package)
        };
    }

    private static void WriteTimeOfDayDefinition(ZPackage package, TimeOfDayDefinition definition)
    {
        WriteStringList(package, definition.Values);
    }

    private static TimeOfDayDefinition ReadTimeOfDayDefinition(ZPackage package)
    {
        return new TimeOfDayDefinition
        {
            Values = ReadStringList(package) ?? new List<string>()
        };
    }

    private static void WriteOptional<T>(ZPackage package, T? value, Action<ZPackage, T> writer)
        where T : class
    {
        bool hasValue = value != null;
        package.Write(hasValue);
        if (hasValue)
        {
            writer(package, value!);
        }
    }

    private static T? ReadOptional<T>(ZPackage package, Func<ZPackage, T> reader)
        where T : class
    {
        return package.ReadBool() ? reader(package) : null;
    }

    private static void WriteOptional<T>(PayloadSignatureBuilder builder, T? value, Action<PayloadSignatureBuilder, T> writer)
        where T : class
    {
        bool hasValue = value != null;
        builder.WriteBool(hasValue);
        if (hasValue)
        {
            writer(builder, value!);
        }
    }

    private static void WriteList<T>(ZPackage package, List<T>? values, Action<ZPackage, T> writer)
    {
        if (values == null)
        {
            package.Write(-1);
            return;
        }

        package.Write(values.Count);
        foreach (T value in values)
        {
            writer(package, value);
        }
    }

    private static void WriteList<T>(PayloadSignatureBuilder builder, List<T>? values, Action<PayloadSignatureBuilder, T> writer)
    {
        if (values == null)
        {
            builder.WriteInt(-1);
            return;
        }

        builder.WriteInt(values.Count);
        foreach (T value in values)
        {
            writer(builder, value);
        }
    }

    private static List<T>? ReadList<T>(ZPackage package, Func<ZPackage, T> reader)
    {
        int count = package.ReadInt();
        if (count < 0)
        {
            return null;
        }

        List<T> values = new(count);
        for (int index = 0; index < count; index++)
        {
            values.Add(reader(package));
        }

        return values;
    }

    private static void WriteStringList(ZPackage package, List<string>? values)
    {
        if (values == null)
        {
            package.Write(-1);
            return;
        }

        package.Write(values.Count);
        foreach (string value in values)
        {
            package.Write(value ?? "");
        }
    }

    private static void WriteStringList(PayloadSignatureBuilder builder, List<string>? values)
    {
        if (values == null)
        {
            builder.WriteInt(-1);
            return;
        }

        builder.WriteInt(values.Count);
        foreach (string value in values)
        {
            builder.WriteString(value ?? "");
        }
    }

    private static List<string>? ReadStringList(ZPackage package)
    {
        int count = package.ReadInt();
        if (count < 0)
        {
            return null;
        }

        List<string> values = new(count);
        for (int index = 0; index < count; index++)
        {
            values.Add(package.ReadString());
        }

        return values;
    }

    private static void WriteStringDictionary(ZPackage package, Dictionary<string, string>? values)
    {
        if (values == null)
        {
            package.Write(-1);
            return;
        }

        List<string> keys = new(values.Keys);
        keys.Sort(StringComparer.Ordinal);
        package.Write(keys.Count);
        foreach (string key in keys)
        {
            string normalizedKey = key ?? "";
            package.Write(normalizedKey);
            values.TryGetValue(normalizedKey, out string? value);
            WriteNullableString(package, value);
        }
    }

    private static void WriteStringDictionary(PayloadSignatureBuilder builder, Dictionary<string, string>? values)
    {
        if (values == null)
        {
            builder.WriteInt(-1);
            return;
        }

        List<string> keys = new(values.Keys);
        keys.Sort(StringComparer.Ordinal);
        builder.WriteInt(keys.Count);
        foreach (string key in keys)
        {
            string normalizedKey = key ?? "";
            builder.WriteString(normalizedKey);
            values.TryGetValue(normalizedKey, out string? value);
            WriteNullableString(builder, value);
        }
    }

    private static Dictionary<string, string>? ReadStringDictionary(ZPackage package)
    {
        int count = package.ReadInt();
        if (count < 0)
        {
            return null;
        }

        Dictionary<string, string> values = new(count, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < count; index++)
        {
            string key = package.ReadString();
            string? value = ReadNullableString(package);
            values[key] = value ?? "";
        }

        return values;
    }

    private static void WriteNullableString(ZPackage package, string? value)
    {
        bool hasValue = value != null;
        package.Write(hasValue);
        if (hasValue)
        {
            package.Write(value!);
        }
    }

    private static void WriteNullableString(PayloadSignatureBuilder builder, string? value)
    {
        bool hasValue = value != null;
        builder.WriteBool(hasValue);
        if (hasValue)
        {
            builder.WriteString(value!);
        }
    }

    private static string? ReadNullableString(ZPackage package)
    {
        return package.ReadBool() ? package.ReadString() : null;
    }

    private static void WriteNullableInt(ZPackage package, int? value)
    {
        package.Write(value.HasValue);
        if (value.HasValue)
        {
            package.Write(value.Value);
        }
    }

    private static void WriteNullableInt(PayloadSignatureBuilder builder, int? value)
    {
        builder.WriteBool(value.HasValue);
        if (value.HasValue)
        {
            builder.WriteInt(value.Value);
        }
    }

    private static int? ReadNullableInt(ZPackage package)
    {
        return package.ReadBool() ? package.ReadInt() : null;
    }

    private static void WriteNullableFloat(ZPackage package, float? value)
    {
        package.Write(value.HasValue);
        if (value.HasValue)
        {
            package.Write(value.Value);
        }
    }

    private static void WriteNullableFloat(PayloadSignatureBuilder builder, float? value)
    {
        builder.WriteBool(value.HasValue);
        if (value.HasValue)
        {
            builder.WriteFloat(value.Value);
        }
    }

    private static float? ReadNullableFloat(ZPackage package)
    {
        return package.ReadBool() ? package.ReadSingle() : null;
    }

    private static void WriteNullableBool(ZPackage package, bool? value)
    {
        package.Write(value.HasValue);
        if (value.HasValue)
        {
            package.Write(value.Value);
        }
    }

    private static void WriteNullableBool(PayloadSignatureBuilder builder, bool? value)
    {
        builder.WriteBool(value.HasValue);
        if (value.HasValue)
        {
            builder.WriteBool(value.Value);
        }
    }

    private static bool? ReadNullableBool(ZPackage package)
    {
        return package.ReadBool() ? package.ReadBool() : null;
    }
}
