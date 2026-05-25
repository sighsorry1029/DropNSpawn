using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private sealed class PendingInboundTransfer
    {
        public long ExpectedSender { get; set; }
        public long RequestId { get; set; }
        public string Hash { get; set; } = "";
        public bool IsFullFallbackRequest { get; set; }
        public string RequestedBaseHash { get; set; } = "";
        public float LastProgressAt { get; set; }
        public int TransferKind { get; set; } = -1;
        public string BaseHash { get; set; } = "";
        public int ChunkSizeBytes { get; set; }
        public int ChunkCount { get; set; }
        public int ExpectedCompressedSize { get; set; } = -1;
        public byte[] CompressedBuffer { get; set; } = Array.Empty<byte>();
        public bool[] ReceivedChunks { get; set; } = Array.Empty<bool>();
        public int ReceivedChunkCount { get; set; }
    }

    private static int GetInboundChunkOffset(int chunkSizeBytes, int chunkIndex)
    {
        checked
        {
            return chunkIndex * chunkSizeBytes;
        }
    }

    private static bool TryGetInboundCompressedBufferCapacity(
        int chunkCount,
        int chunkIndex,
        int chunkLength,
        int compressedSize,
        int chunkSizeBytes,
        out int capacity,
        out string failureReason)
    {
        capacity = 0;
        failureReason = "";
        if (compressedSize <= 0)
        {
            failureReason = $"compressed size {compressedSize} must be greater than 0.";
            return false;
        }

        if (chunkLength < 0 || chunkLength > chunkSizeBytes)
        {
            failureReason = $"chunk length {chunkLength} is outside the valid range 0-{chunkSizeBytes}.";
            return false;
        }

        int descriptorChunkCount = Math.Max(1, (int)Math.Ceiling(compressedSize / (double)chunkSizeBytes));
        if (descriptorChunkCount != chunkCount)
        {
            failureReason =
                $"chunk count {chunkCount} did not match compressed size {compressedSize} descriptor chunk count {descriptorChunkCount}.";
            return false;
        }

        if (chunkIndex < chunkCount - 1 && chunkLength != chunkSizeBytes)
        {
            failureReason = $"non-terminal chunk {chunkIndex} had length {chunkLength} instead of {chunkSizeBytes}.";
            return false;
        }

        long requiredCapacity = (long)GetInboundChunkOffset(chunkSizeBytes, chunkIndex) + chunkLength;
        long resolvedCapacity = compressedSize;
        if (resolvedCapacity < requiredCapacity || resolvedCapacity > int.MaxValue)
        {
            failureReason = $"compressed buffer capacity {resolvedCapacity} is invalid for chunk {chunkIndex} length {chunkLength}.";
            return false;
        }

        capacity = (int)resolvedCapacity;
        return true;
    }

    private static bool TryInitializePendingInboundTransferBufferLocked<TEntry>(
        DomainTransport<TEntry> transport,
        PendingInboundTransfer pendingTransfer,
        string hash,
        int transferKind,
        string normalizedBaseHash,
        int chunkCount,
        int compressedSize,
        int chunkIndex,
        int chunkLength,
        out string failureReason)
    {
        failureReason = "";
        pendingTransfer.TransferKind = transferKind;
        pendingTransfer.BaseHash = normalizedBaseHash;
        pendingTransfer.ChunkSizeBytes = transport.ChunkSizeBytes;
        pendingTransfer.ChunkCount = chunkCount;
        if (transferKind == FullTransferKind &&
            string.Equals(transport.DesiredPayloadManifest.Hash, hash, StringComparison.Ordinal))
        {
            PayloadManifest manifest = transport.DesiredPayloadManifest;
            if (manifest.ChunkCount > 0 && manifest.ChunkCount != chunkCount)
            {
                failureReason =
                        $"full transfer chunk count {chunkCount} did not match desired manifest chunk count {manifest.ChunkCount}.";
                return false;
            }

            if (manifest.CompressedSize > 0)
            {
                if (manifest.CompressedSize != compressedSize)
                {
                    failureReason =
                        $"full transfer compressed size {compressedSize} did not match desired manifest compressed size {manifest.CompressedSize}.";
                    return false;
                }
            }
        }

        if (!TryGetInboundCompressedBufferCapacity(
                chunkCount,
                chunkIndex,
                chunkLength,
                compressedSize,
                transport.ChunkSizeBytes,
                out int capacity,
                out failureReason))
        {
            return false;
        }

        pendingTransfer.ExpectedCompressedSize = compressedSize;
        pendingTransfer.CompressedBuffer = new byte[capacity];
        pendingTransfer.ReceivedChunks = new bool[chunkCount];
        pendingTransfer.ReceivedChunkCount = 0;
        return true;
    }

    private static bool TryValidateInboundChunkBounds(
        PendingInboundTransfer pendingTransfer,
        int chunkIndex,
        int chunkLength,
        out string failureReason)
    {
        failureReason = "";
        int chunkSizeBytes = Math.Max(1, pendingTransfer.ChunkSizeBytes);
        if (chunkLength < 0 || chunkLength > chunkSizeBytes)
        {
            failureReason = $"chunk length {chunkLength} is outside the valid range 0-{chunkSizeBytes}.";
            return false;
        }

        bool isLastChunk = chunkIndex == pendingTransfer.ChunkCount - 1;
        if (!isLastChunk && chunkLength != chunkSizeBytes)
        {
            failureReason = $"non-terminal chunk {chunkIndex} had length {chunkLength} instead of {chunkSizeBytes}.";
            return false;
        }

        int offset = GetInboundChunkOffset(chunkSizeBytes, chunkIndex);
        if (offset < 0 || offset > pendingTransfer.CompressedBuffer.Length)
        {
            failureReason = $"chunk {chunkIndex} offset {offset} exceeded compressed buffer length {pendingTransfer.CompressedBuffer.Length}.";
            return false;
        }

        int remainingCapacity = pendingTransfer.CompressedBuffer.Length - offset;
        if (!isLastChunk)
        {
            if (chunkLength > remainingCapacity)
            {
                failureReason =
                    $"chunk {chunkIndex} length {chunkLength} exceeded remaining compressed buffer capacity {remainingCapacity}.";
                return false;
            }

            return true;
        }

        int expectedLastChunkLength = pendingTransfer.ExpectedCompressedSize - offset;
        if (expectedLastChunkLength <= 0 || expectedLastChunkLength > chunkSizeBytes)
        {
            failureReason = $"last chunk {chunkIndex} expected length {expectedLastChunkLength} is invalid.";
            return false;
        }

        if (chunkLength != expectedLastChunkLength)
        {
            failureReason =
                $"last chunk {chunkIndex} length {chunkLength} did not match expected length {expectedLastChunkLength}.";
            return false;
        }

        return true;
    }

    private static bool BufferedChunkEquals(byte[] buffer, int offset, byte[] chunkBytes)
    {
        for (int index = 0; index < chunkBytes.Length; index++)
        {
            if (buffer[offset + index] != chunkBytes[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryBufferInboundChunkBytes(
        PendingInboundTransfer pendingTransfer,
        int chunkIndex,
        byte[] chunkBytes,
        out bool isDuplicate,
        out string failureReason)
    {
        isDuplicate = false;
        if (!TryValidateInboundChunkBounds(pendingTransfer, chunkIndex, chunkBytes.Length, out failureReason))
        {
            return false;
        }

        bool isLastChunk = chunkIndex == pendingTransfer.ChunkCount - 1;
        int offset = GetInboundChunkOffset(Math.Max(1, pendingTransfer.ChunkSizeBytes), chunkIndex);
        if (pendingTransfer.ReceivedChunks[chunkIndex])
        {
            int storedLength = isLastChunk
                ? pendingTransfer.ExpectedCompressedSize - offset
                : pendingTransfer.ChunkSizeBytes;
            isDuplicate = true;
            if (storedLength != chunkBytes.Length ||
                !BufferedChunkEquals(pendingTransfer.CompressedBuffer, offset, chunkBytes))
            {
                failureReason = $"duplicate chunk {chunkIndex} differed from the already buffered data.";
                return false;
            }

            return true;
        }

        Buffer.BlockCopy(chunkBytes, 0, pendingTransfer.CompressedBuffer, offset, chunkBytes.Length);
        pendingTransfer.ReceivedChunks[chunkIndex] = true;
        pendingTransfer.ReceivedChunkCount++;

        failureReason = "";
        return true;
    }

    private static int GetInboundCompressedLength(PendingInboundTransfer pendingTransfer)
    {
        return Math.Max(0, pendingTransfer.ExpectedCompressedSize);
    }

    private static bool TryGetPublishedTransferArtifactLocked<TEntry>(
        DomainTransport<TEntry> transport,
        string targetHash,
        string? baseHash,
        out TransferArtifact? artifact)
    {
        artifact = null;
        string cacheKey = BuildTransferArtifactCacheKey(targetHash, baseHash);
        if (!transport.PublishedTransferArtifacts.TryGetValue(cacheKey, out artifact) || artifact == null)
        {
            return false;
        }

        LinkedListNode<string>? artifactNode = artifact.LruNode;
        TouchLruNodeLocked(transport.PublishedTransferArtifactLru, cacheKey, ref artifactNode);
        artifact.LruNode = artifactNode;
        return true;
    }

    private static void StoreTransferArtifactLocked<TEntry>(DomainTransport<TEntry> transport, TransferArtifact artifact)
    {
        if (artifact == null || artifact.CacheKey.Length == 0)
        {
            return;
        }

        if (transport.PublishedTransferArtifacts.TryGetValue(artifact.CacheKey, out TransferArtifact? existingArtifact) && existingArtifact != null)
        {
            RemoveTransferArtifactLocked(transport, existingArtifact);
        }

        transport.PublishedTransferArtifacts[artifact.CacheKey] = artifact;
        AdjustPublishedTransferArtifactBytesLocked(transport, artifact.EstimatedBytes);
        LinkedListNode<string>? artifactNode = artifact.LruNode;
        TouchLruNodeLocked(transport.PublishedTransferArtifactLru, artifact.CacheKey, ref artifactNode);
        artifact.LruNode = artifactNode;
    }

    private static TransferArtifact? EnsureFullTransferArtifactLocked<TEntry>(DomainTransport<TEntry> transport, string targetHash)
    {
        if (transport.PublishedCompressedBytes == null ||
            transport.PublishedPayloadManifest.IsEmpty ||
            !string.Equals(transport.PublishedPayloadManifest.Hash, targetHash, StringComparison.Ordinal))
        {
            return null;
        }

        if (TryGetPublishedTransferArtifactLocked(transport, targetHash, "", out TransferArtifact? cachedArtifact))
        {
            return cachedArtifact;
        }

        TransferArtifact artifact = new()
        {
            CacheKey = BuildTransferArtifactCacheKey(targetHash, ""),
            TransferKind = FullTransferKind,
            BaseHash = "",
            TargetHash = targetHash,
            CompressedBytes = transport.PublishedCompressedBytes,
            ChunkSizeBytes = transport.ChunkSizeBytes,
            ChunkCount = Math.Max(1, (int)Math.Ceiling(transport.PublishedCompressedBytes.Length / (double)transport.ChunkSizeBytes))
        };
        artifact.EstimatedBytes = EstimateTransferArtifactBytes(artifact);
        StoreTransferArtifactLocked(transport, artifact);
        return artifact;
    }

    private static void QueueTransferArtifactBuildIfNeededLocked<TEntry>(
        DomainTransport<TEntry> transport,
        string targetHash,
        string? baseHash)
    {
        if (!transport.EnableArtifactPrewarm || !transport.SupportsDeltaTransfers)
        {
            return;
        }

        string normalizedBaseHash = NormalizeBaseHash(baseHash);
        if (normalizedBaseHash.Length == 0 ||
            transport.PublishedCompressedBytes == null ||
            transport.PublishedPayloadManifest.IsEmpty ||
            !string.Equals(transport.PublishedPayloadManifest.Hash, targetHash, StringComparison.Ordinal) ||
            !transport.PublishedPayloadHistory.TryGetValue(normalizedBaseHash, out PublishedPayloadHistoryEntry<TEntry>? basePayloadEntry) ||
            basePayloadEntry == null ||
            basePayloadEntry.PayloadBytes == null ||
            basePayloadEntry.PayloadBytes.Length == 0)
        {
            return;
        }

        if (TryGetPublishedTransferArtifactLocked(transport, targetHash, normalizedBaseHash, out _))
        {
            return;
        }

        string cacheKey = BuildTransferArtifactCacheKey(targetHash, normalizedBaseHash);
        if (!transport.PendingArtifactBuildKeys.Add(cacheKey))
        {
            return;
        }

        byte[] fullCompressedPayloadBytes = transport.PublishedCompressedBytes;
        byte[] targetPayloadBytes = transport.PublishedPayloadBytes ?? Array.Empty<byte>();
        PayloadEntryIndex<TEntry>? targetPayloadIndex = transport.PublishedPayloadIndex;
        byte[] basePayloadBytes = basePayloadEntry.PayloadBytes;
        PayloadEntryIndex<TEntry>? basePayloadIndex = basePayloadEntry.PayloadIndex;
        int roleEpoch = _networkRoleEpoch;

        LinkedListNode<string>? payloadHistoryNode = basePayloadEntry.LruNode;
        TouchLruNodeLocked(transport.PublishedPayloadHistoryLru, normalizedBaseHash, ref payloadHistoryNode);
        basePayloadEntry.LruNode = payloadHistoryNode;

        QueueDeltaArtifactPrewarmJobLocked(() =>
        {
            try
            {
                if (!IsCurrentRoleEpoch(roleEpoch))
                {
                    QueueMainThreadPayloadCommitLocked(() =>
                    {
                        lock (Sync)
                        {
                            transport.PendingArtifactBuildKeys.Remove(cacheKey);
                        }
                    }, roleEpoch);
                    return;
                }

                if (!TryEnsurePayloadIndex(
                        transport,
                        targetHash,
                        targetPayloadBytes,
                        targetPayloadIndex,
                        out PayloadEntryIndex<TEntry>? resolvedTargetPayloadIndex) ||
                    resolvedTargetPayloadIndex == null)
                {
                    QueueMainThreadPayloadCommitLocked(() =>
                    {
                        lock (Sync)
                        {
                            transport.PendingArtifactBuildKeys.Remove(cacheKey);
                        }
                    }, roleEpoch);
                    return;
                }

                if (!IsCurrentRoleEpoch(roleEpoch))
                {
                    QueueMainThreadPayloadCommitLocked(() =>
                    {
                        lock (Sync)
                        {
                            transport.PendingArtifactBuildKeys.Remove(cacheKey);
                        }
                    }, roleEpoch);
                    return;
                }

                if (!TryEnsurePayloadIndex(
                        transport,
                        normalizedBaseHash,
                        basePayloadBytes,
                        basePayloadIndex,
                        out PayloadEntryIndex<TEntry>? resolvedBasePayloadIndex) ||
                    resolvedBasePayloadIndex == null)
                {
                    QueueMainThreadPayloadCommitLocked(() =>
                    {
                        lock (Sync)
                        {
                            transport.PendingArtifactBuildKeys.Remove(cacheKey);
                        }
                    }, roleEpoch);
                    return;
                }

                if (!IsCurrentRoleEpoch(roleEpoch))
                {
                    QueueMainThreadPayloadCommitLocked(() =>
                    {
                        lock (Sync)
                        {
                            transport.PendingArtifactBuildKeys.Remove(cacheKey);
                        }
                    }, roleEpoch);
                    return;
                }

                if (!TryBuildDeltaPayloadBytes(
                        transport,
                        normalizedBaseHash,
                        targetHash,
                        resolvedBasePayloadIndex,
                        resolvedTargetPayloadIndex,
                        out byte[] deltaPayloadBytes))
                {
                    QueueMainThreadPayloadCommitLocked(() =>
                    {
                        lock (Sync)
                        {
                            transport.PendingArtifactBuildKeys.Remove(cacheKey);
                        }
                    }, roleEpoch);
                    return;
                }

                if (!IsCurrentRoleEpoch(roleEpoch))
                {
                    QueueMainThreadPayloadCommitLocked(() =>
                    {
                        lock (Sync)
                        {
                            transport.PendingArtifactBuildKeys.Remove(cacheKey);
                        }
                    }, roleEpoch);
                    return;
                }

                byte[] compressedDeltaPayloadBytes = CompressBytes(deltaPayloadBytes);
                if (!IsCurrentRoleEpoch(roleEpoch))
                {
                    QueueMainThreadPayloadCommitLocked(() =>
                    {
                        lock (Sync)
                        {
                            transport.PendingArtifactBuildKeys.Remove(cacheKey);
                        }
                    }, roleEpoch);
                    return;
                }

                if (compressedDeltaPayloadBytes.Length >= fullCompressedPayloadBytes.Length * transport.TransportPolicy.MaxDeltaCompressedSizeRatio)
                {
                    QueueMainThreadPayloadCommitLocked(() =>
                    {
                        lock (Sync)
                        {
                            transport.PendingArtifactBuildKeys.Remove(cacheKey);
                        }
                    }, roleEpoch);
                    return;
                }

                TransferArtifact artifact = new()
                {
                    CacheKey = cacheKey,
                    TransferKind = DeltaTransferKind,
                    BaseHash = normalizedBaseHash,
                    TargetHash = targetHash,
                    CompressedBytes = compressedDeltaPayloadBytes,
                    ChunkSizeBytes = transport.ChunkSizeBytes,
                    ChunkCount = Math.Max(1, (int)Math.Ceiling(compressedDeltaPayloadBytes.Length / (double)transport.ChunkSizeBytes))
                };
                artifact.EstimatedBytes = EstimateTransferArtifactBytes(artifact);

                QueueMainThreadPayloadCommitLocked(() =>
                {
                    lock (Sync)
                    {
                        transport.PendingArtifactBuildKeys.Remove(cacheKey);
                        if (roleEpoch != _networkRoleEpoch ||
                            transport.PublishedPayloadManifest.IsEmpty ||
                            !string.Equals(transport.PublishedPayloadManifest.Hash, targetHash, StringComparison.Ordinal) ||
                            transport.PublishedTransferArtifacts.ContainsKey(cacheKey))
                        {
                            return;
                        }

                        if (transport.PublishedPayloadIndex == null)
                        {
                            transport.PublishedPayloadIndex = resolvedTargetPayloadIndex;
                        }

                        RememberPublishedPayloadLocked(
                            transport,
                            normalizedBaseHash,
                            basePayloadBytes,
                            resolvedBasePayloadIndex);
                        StoreTransferArtifactLocked(transport, artifact);
                        TrimTransportCachesLocked(transport);
                        TrimAllPublishedCachesLocked();
                    }
                }, roleEpoch);
            }
            catch (Exception ex)
            {
                QueueMainThreadPayloadCommitLocked(() =>
                {
                    lock (Sync)
                    {
                        transport.PendingArtifactBuildKeys.Remove(cacheKey);
                    }

                    DropNSpawnPlugin.DropNSpawnLogger.LogDebug(
                        $"Failed to prebuild synchronized {transport.DisplayName} delta artifact '{normalizedBaseHash}->{targetHash}'. {ex.Message}");
                }, roleEpoch);
            }
        }, roleEpoch);
    }

    private static long AllocateRequestIdLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        long requestId = transport.NextRequestId + 1;
        if (requestId <= 0)
        {
            requestId = 1;
        }

        transport.NextRequestId = requestId;
        return requestId;
    }

    private static long BeginPendingInboundTransferLocked<TEntry>(
        DomainTransport<TEntry> transport,
        string hash,
        string requestedBaseHash,
        bool isFullFallbackRequest,
        long expectedSender,
        out string resolvedBaseHash)
    {
        float now = Time.realtimeSinceStartup;
        PendingInboundTransfer? pendingTransfer = transport.PendingTransfer;
        if (pendingTransfer != null &&
            string.Equals(pendingTransfer.Hash, hash, StringComparison.Ordinal) &&
            pendingTransfer.IsFullFallbackRequest == isFullFallbackRequest)
        {
            pendingTransfer.ExpectedSender = expectedSender;
            if (pendingTransfer.RequestId <= 0L)
            {
                pendingTransfer.RequestId = AllocateRequestIdLocked(transport);
            }

            if (pendingTransfer.RequestedBaseHash.Length == 0)
            {
                pendingTransfer.RequestedBaseHash = pendingTransfer.TransferKind >= 0
                    ? pendingTransfer.BaseHash
                    : NormalizeBaseHash(requestedBaseHash);
            }

            transport.RequestInFlight = true;
            transport.RequestStartedAt = now;
            resolvedBaseHash = pendingTransfer.RequestedBaseHash;
            return pendingTransfer.RequestId;
        }

        long requestId = AllocateRequestIdLocked(transport);
        transport.PendingTransfer = new PendingInboundTransfer
        {
            ExpectedSender = expectedSender,
            RequestId = requestId,
            Hash = hash ?? "",
            IsFullFallbackRequest = isFullFallbackRequest,
            RequestedBaseHash = NormalizeBaseHash(requestedBaseHash)
        };
        transport.RequestInFlight = true;
        transport.RequestStartedAt = now;
        resolvedBaseHash = transport.PendingTransfer.RequestedBaseHash;
        return requestId;
    }

    private static void ClearPendingInboundTransferLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        transport.PendingTransfer = null;
        transport.RequestInFlight = false;
        transport.RequestStartedAt = 0f;
    }

    private static bool MatchesCurrentPendingRequestLocked<TEntry>(DomainTransport<TEntry> transport, string hash)
    {
        return transport.PendingTransfer != null &&
               string.Equals(transport.PendingTransfer.Hash, hash, StringComparison.Ordinal);
    }

    private static float GetPendingRequestLastActivityAtLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        float lastActivityAt = transport.RequestStartedAt;
        if (transport.PendingTransfer?.LastProgressAt > lastActivityAt)
        {
            lastActivityAt = transport.PendingTransfer.LastProgressAt;
        }

        return lastActivityAt;
    }

    private static bool HasPendingRequestTimedOutLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        if (!transport.RequestInFlight)
        {
            return false;
        }

        float lastActivityAt = GetPendingRequestLastActivityAtLocked(transport);
        return lastActivityAt > 0f &&
               Time.realtimeSinceStartup - lastActivityAt >= RequestRetrySeconds;
    }

    private static void AbortPendingInboundTransferLocked<TEntry>(DomainTransport<TEntry> transport, string message, bool retryAsFull)
    {
        string desiredHash = transport.DesiredPayloadManifest.Hash;
        ClearPendingInboundTransferLocked(transport);
        transport.LastWaitingLogHash = "";
        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(message);

        if (!string.IsNullOrWhiteSpace(desiredHash))
        {
            if (retryAsFull)
            {
                TryRequestFullFallbackOnceLocked(
                    transport,
                    desiredHash,
                    $"source=delta_transfer_validation detail={message}");
            }
            else
            {
                EnsurePayloadRequestedLocked(transport, transport.DesiredPayloadManifest);
            }
        }
    }

    private static bool TryRequestFullFallbackOnceLocked<TEntry>(
        DomainTransport<TEntry> transport,
        string hash,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        if (string.Equals(transport.FullFallbackAttemptedHash, hash, StringComparison.Ordinal))
        {
            BlockManifestLocked(
                transport,
                hash,
                $"{reason} source=delta_full_fallback_exhausted fullFallbackAttempted=true");
            return false;
        }

        transport.FullFallbackAttemptedHash = hash;
        return EnsurePayloadRequestedLocked(transport, transport.DesiredPayloadManifest, "", isFullFallbackRequest: true);
    }

    private static void CommitProcessedPayloadLocked<TEntry>(
        DomainTransport<TEntry> transport,
        int version,
        string hash,
        byte[] payloadBytes,
        List<TEntry>? entries,
        PayloadEntryIndex<TEntry>? payloadIndex,
        int? entryCount,
        string successLogMessage)
    {
        bool committed = false;
        lock (Sync)
        {
            if (!IsProcessingResultCurrentLocked(transport, version, hash) ||
                !string.Equals(transport.DesiredPayloadManifest.Hash, hash, StringComparison.Ordinal))
            {
                return;
            }

            transport.AvailableHash = hash;
            transport.AvailablePayloadBytes = payloadBytes;
            transport.AvailableEntries = entries;
            transport.AvailablePayloadIndex = payloadIndex;
            ClearBlockedManifestLocked(transport);
            ClearPendingInboundTransferLocked(transport);
            transport.LastWaitingLogHash = "";
            transport.ProcessingInFlight = false;
            transport.ProcessingHash = "";
            committed = true;
            QueueReloadActionLocked(transport);
        }

        if (committed)
        {
            NotifyTransportPayloadReadyIfNeeded(transport, hash, entryCount, successLogMessage);
            DropNSpawnPlugin.DropNSpawnLogger.LogDebug(successLogMessage);
        }
    }

    private static void QueueFetchedPayloadCacheWriteLocked<TEntry>(
        DomainTransport<TEntry> transport,
        string hash,
        byte[] payloadBytes,
        byte[]? compressedBytes,
        int compressedLength,
        bool requiresCompression,
        int roleEpoch)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return;
        }

        QueueCachePersistenceJobLocked(() =>
        {
            try
            {
                if (!IsCurrentRoleEpoch(roleEpoch))
                {
                    return;
                }

                if (requiresCompression)
                {
                    byte[] cacheBytes = CompressBytes(payloadBytes);
                    if (cacheBytes.Length == 0)
                    {
                        return;
                    }

                    if (!IsCurrentRoleEpoch(roleEpoch))
                    {
                        return;
                    }

                    WriteCacheFile(transport.CacheDirectoryName, hash, cacheBytes);
                    return;
                }

                byte[] existingCompressedBytes = compressedBytes ?? Array.Empty<byte>();
                if (existingCompressedBytes.Length == 0 || compressedLength <= 0)
                {
                    return;
                }

                if (!IsCurrentRoleEpoch(roleEpoch))
                {
                    return;
                }

                WriteCacheFile(transport.CacheDirectoryName, hash, existingCompressedBytes, compressedLength);
            }
            catch (Exception ex)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                    $"Failed to persist synchronized {transport.DisplayName} payload '{hash}' to cache. {ex.Message}");
            }
        }, roleEpoch);
    }

    private static bool EnsurePayloadRequestedLocked<TEntry>(
        DomainTransport<TEntry> transport,
        PayloadManifest manifest,
        string? baseHashOverride = null,
        bool isFullFallbackRequest = false)
    {
        if (manifest.IsEmpty ||
            IsManifestBlockedLocked(transport, manifest.Hash) ||
            _host == null ||
            ZRoutedRpc.instance == null)
        {
            return false;
        }

        if (transport.RequestInFlight &&
            MatchesCurrentPendingRequestLocked(transport, manifest.Hash) &&
            !HasPendingRequestTimedOutLocked(transport))
        {
            return false;
        }

        if (!transport.RequestInFlight && !HasAvailableClientRequestSlotLocked(transport))
        {
            return false;
        }

        if (!TryGetCurrentServerRoutedSender(out long expectedSender))
        {
            return false;
        }

        string baseHash = NormalizeBaseHash(baseHashOverride ?? (
            transport.AvailablePayloadBytes != null &&
            transport.AvailableHash.Length > 0 &&
            !string.Equals(transport.AvailableHash, manifest.Hash, StringComparison.Ordinal)
                ? transport.AvailableHash
                : ""));
        long requestId = BeginPendingInboundTransferLocked(
            transport,
            manifest.Hash,
            baseHash,
            isFullFallbackRequest,
            expectedSender,
            out string effectiveBaseHash);

        ZPackage package = new();
        package.Write(transport.DomainKey);
        package.Write(manifest.Hash);
        package.Write(effectiveBaseHash);
        package.Write(requestId);
        ZRoutedRpc.instance.InvokeRoutedRPC(0L, PayloadRequestRpc, package);
        return true;
    }

    private static void EnqueuePublishedPayloadChunksLocked<TEntry>(DomainTransport<TEntry> transport, long sender, string requestedHash, string? baseHash, long requestId)
    {
        TransferArtifact? artifact = GetOrCreateTransferArtifactLocked(transport, requestedHash, baseHash);
        if (artifact == null)
        {
            return;
        }

        if (artifact.ChunkCount <= 0)
        {
            return;
        }

        UpsertOutboundTransferLocked(transport.DomainKey, sender, requestedHash, requestId, artifact);
    }

    private static TransferArtifact? GetOrCreateTransferArtifactLocked<TEntry>(DomainTransport<TEntry> transport, string requestedHash, string? baseHash)
    {
        if (transport.PublishedCompressedBytes == null ||
            transport.PublishedPayloadManifest.IsEmpty ||
            !string.Equals(transport.PublishedPayloadManifest.Hash, requestedHash, StringComparison.Ordinal))
        {
            return null;
        }

        string normalizedBaseHash = string.IsNullOrWhiteSpace(baseHash) ? "" : (baseHash ?? "").Trim();
        if (TryGetPublishedTransferArtifactLocked(transport, requestedHash, normalizedBaseHash, out TransferArtifact? cachedArtifact))
        {
            return cachedArtifact;
        }

        TransferArtifact? fullArtifact = EnsureFullTransferArtifactLocked(transport, requestedHash);
        if (normalizedBaseHash.Length > 0 && transport.EnableArtifactPrewarm)
        {
            QueueTransferArtifactBuildIfNeededLocked(transport, requestedHash, normalizedBaseHash);
        }

        TrimTransportCachesLocked(transport);
        TrimAllPublishedCachesLocked();
        return fullArtifact;
    }

    private static bool HasPendingClientRequestWorkLocked()
    {
        foreach (IDomainTransport transport in AllTransports)
        {
            if (transport.HasWaitingRequest())
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasWaitingRequestLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        if (transport.DesiredPayloadManifest.IsEmpty)
        {
            return false;
        }

        if (IsManifestBlockedLocked(transport, transport.DesiredPayloadManifest.Hash))
        {
            return false;
        }

        if (string.Equals(transport.AvailableHash, transport.DesiredPayloadManifest.Hash, StringComparison.Ordinal) &&
            transport.AvailablePayloadBytes != null)
        {
            return false;
        }

        if (transport.ProcessingInFlight &&
            string.Equals(transport.ProcessingHash, transport.DesiredPayloadManifest.Hash, StringComparison.Ordinal))
        {
            return false;
        }

        if (transport.RequestInFlight &&
            MatchesCurrentPendingRequestLocked(transport, transport.DesiredPayloadManifest.Hash) &&
            !HasPendingRequestTimedOutLocked(transport))
        {
            return false;
        }

        return true;
    }

    private static int CountClientRequestsInFlightLocked(bool usesLargeRequestLane)
    {
        int count = 0;
        foreach (IDomainTransport transport in AllTransports)
        {
            count += transport.CountClientRequestInFlight(usesLargeRequestLane);
        }

        return count;
    }

    private static int CountClientRequestInFlightLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        if (!transport.RequestInFlight)
        {
            return 0;
        }

        if (HasPendingRequestTimedOutLocked(transport))
        {
            return 0;
        }

        return 1;
    }

    private static int CountClientRequestInFlightLocked<TEntry>(DomainTransport<TEntry> transport, bool usesLargeRequestLane)
    {
        return transport.UsesLargeRequestLane == usesLargeRequestLane
            ? CountClientRequestInFlightLocked(transport)
            : 0;
    }

    private static bool HasAvailableClientRequestSlotLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        if (transport.UsesLargeRequestLane)
        {
            return CountClientRequestsInFlightLocked(usesLargeRequestLane: true) < MaxConcurrentLargeClientRequests;
        }

        return CountClientRequestsInFlightLocked(usesLargeRequestLane: false) < MaxConcurrentSmallClientRequests;
    }

    private static bool TryStartNextWaitingPayloadRequestLocked()
    {
        if (_host == null || ZRoutedRpc.instance == null)
        {
            return false;
        }

        bool hasLargeSlot = CountClientRequestsInFlightLocked(usesLargeRequestLane: true) < MaxConcurrentLargeClientRequests;
        bool hasSmallSlot = CountClientRequestsInFlightLocked(usesLargeRequestLane: false) < MaxConcurrentSmallClientRequests;
        if (!hasLargeSlot && !hasSmallSlot)
        {
            return false;
        }

        if (hasSmallSlot && TryStartNextWaitingPayloadRequestLocked(SmallLaneRequestPriorityTransports))
        {
            return true;
        }

        return hasLargeSlot && TryStartNextWaitingPayloadRequestLocked(LargeLaneRequestPriorityTransports);
    }

    private static bool TryStartNextWaitingPayloadRequestLocked(IEnumerable<IDomainTransport> transports)
    {
        foreach (IDomainTransport transport in transports)
        {
            if (transport.TryStartDesiredPayloadRequest())
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryStartDesiredPayloadRequestLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        if (!HasWaitingRequestLocked(transport))
        {
            return false;
        }

        return EnsurePayloadRequestedLocked(transport, transport.DesiredPayloadManifest);
    }

    private static bool IsProcessingResultCurrentLocked<TEntry>(DomainTransport<TEntry> transport, int version, string hash)
    {
        return transport.ProcessingVersion == version &&
               string.Equals(transport.ProcessingHash, hash, StringComparison.Ordinal);
    }

    private static void ReceivePayloadChunkLocked<TEntry>(
        DomainTransport<TEntry> transport,
        long sender,
        string hash,
        long requestId,
        int transferKind,
        string baseHash,
        int chunkIndex,
        int chunkCount,
        int compressedSize,
        byte[] chunkBytes)
    {
        PendingInboundTransfer? pendingTransfer = transport.PendingTransfer;
        if (string.IsNullOrWhiteSpace(hash) ||
            chunkCount <= 0 ||
            compressedSize <= 0 ||
            chunkIndex < 0 ||
            chunkIndex >= chunkCount ||
            pendingTransfer == null)
        {
            return;
        }

        if (pendingTransfer.ExpectedSender != 0L && pendingTransfer.ExpectedSender != sender)
        {
            return;
        }

        if (requestId != 0L)
        {
            if (pendingTransfer.RequestId != requestId)
            {
                return;
            }
        }
        else if (pendingTransfer.RequestId == 0L)
        {
            return;
        }

        if (!string.Equals(pendingTransfer.Hash, hash, StringComparison.Ordinal))
        {
            AbortPendingInboundTransferLocked(
                transport,
                $"Discarding synchronized {transport.DisplayName} payload '{hash}' because the inbound request hash does not match the active request.",
                retryAsFull: true);
            return;
        }

        string normalizedBaseHash = NormalizeBaseHash(baseHash);
        if (pendingTransfer.TransferKind < 0)
        {
            if (!TryInitializePendingInboundTransferBufferLocked(
                    transport,
                    pendingTransfer,
                    hash,
                    transferKind,
                    normalizedBaseHash,
                    chunkCount,
                    compressedSize,
                    chunkIndex,
                    (chunkBytes ?? Array.Empty<byte>()).Length,
                    out string failureReason))
            {
                AbortPendingInboundTransferLocked(
                    transport,
                    $"Discarding synchronized {transport.DisplayName} payload '{hash}' because {failureReason}",
                    retryAsFull: true);
                return;
            }
        }
        else if (pendingTransfer.TransferKind != transferKind ||
                 !string.Equals(pendingTransfer.BaseHash, normalizedBaseHash, StringComparison.Ordinal) ||
                 pendingTransfer.ChunkCount != chunkCount ||
                 pendingTransfer.ExpectedCompressedSize != compressedSize)
        {
            AbortPendingInboundTransferLocked(
                transport,
                $"Discarding synchronized {transport.DisplayName} payload '{hash}' because chunk descriptor {transferKind}/{normalizedBaseHash}/{chunkCount}/{compressedSize} did not match the active transfer {pendingTransfer.TransferKind}/{pendingTransfer.BaseHash}/{pendingTransfer.ChunkCount}/{pendingTransfer.ExpectedCompressedSize}.",
                retryAsFull: true);
            return;
        }

        byte[] normalizedChunkBytes = chunkBytes ?? Array.Empty<byte>();
        if (!TryBufferInboundChunkBytes(
                pendingTransfer,
                chunkIndex,
                normalizedChunkBytes,
                out bool isDuplicate,
                out string chunkFailureReason))
        {
            AbortPendingInboundTransferLocked(
                transport,
                $"Discarding synchronized {transport.DisplayName} payload '{hash}' because {chunkFailureReason}",
                retryAsFull: true);
            return;
        }

        if (isDuplicate)
        {
            pendingTransfer.LastProgressAt = Time.realtimeSinceStartup;
            return;
        }

        pendingTransfer.LastProgressAt = Time.realtimeSinceStartup;

        if (pendingTransfer.ReceivedChunkCount < pendingTransfer.ChunkCount)
        {
            return;
        }

        int version = ++transport.ProcessingVersion;
        transport.ProcessingInFlight = true;
        transport.ProcessingHash = hash;
        int roleEpoch = _networkRoleEpoch;
        PayloadEntryIndex<TEntry>? basePayloadIndexSnapshot =
            pendingTransfer.TransferKind == DeltaTransferKind &&
            string.Equals(transport.AvailableHash, pendingTransfer.BaseHash, StringComparison.Ordinal)
                ? transport.AvailablePayloadIndex
                : null;
        byte[]? basePayloadBytesSnapshot =
            pendingTransfer.TransferKind == DeltaTransferKind &&
            string.Equals(transport.AvailableHash, pendingTransfer.BaseHash, StringComparison.Ordinal) &&
            transport.AvailablePayloadBytes != null
                ? transport.AvailablePayloadBytes
                : null;
        int transferDecompressionCapacityHint = pendingTransfer.TransferKind == DeltaTransferKind
            ? Math.Max(0, basePayloadBytesSnapshot?.Length ?? transport.AvailablePayloadBytes?.Length ?? 0)
            : Math.Max(0, transport.AvailablePayloadBytes?.Length ?? 0);
        byte[] compressedBuffer = pendingTransfer.CompressedBuffer;
        int compressedLength = GetInboundCompressedLength(pendingTransfer);

        ClearPendingInboundTransferLocked(transport);
        transport.LastWaitingLogHash = "";

        QueueCriticalPayloadProcessingJobLocked(() =>
        {
            try
            {
                byte[] transferBytes = DecompressBytes(compressedBuffer, compressedLength, transferDecompressionCapacityHint);
                byte[] payloadBytes;
                List<TEntry>? entries = null;
                PayloadEntryIndex<TEntry>? payloadIndex = null;
                if (pendingTransfer.TransferKind == DeltaTransferKind)
                {
                    payloadBytes = ApplyDeltaPayloadBytes(
                        transport,
                        pendingTransfer.BaseHash,
                        hash,
                        transferBytes,
                        basePayloadIndexSnapshot,
                        basePayloadBytesSnapshot,
                        out List<TEntry> mergedEntries);
                    entries = mergedEntries;
                }
                else
                {
                    payloadBytes = transferBytes;
                }

                string payloadHash = ComputeSha256(payloadBytes);
                if (!string.Equals(payloadHash, hash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Discarding synchronized {transport.DisplayName} payload '{hash}' because the fetched hash does not match.");
                }

                if (entries != null)
                {
                    BuildPayloadIndex(transport, entries, hash, out payloadIndex);
                }

                QueueFetchedPayloadCacheWriteLocked(
                    transport,
                    hash,
                    payloadBytes,
                    compressedBuffer,
                    compressedLength,
                    requiresCompression: pendingTransfer.TransferKind == DeltaTransferKind,
                    roleEpoch);

                string successLogMessage = pendingTransfer.TransferKind == DeltaTransferKind
                    ? $"Fetched synchronized {transport.DisplayName} delta payload '{hash}' from the server."
                    : $"Fetched synchronized {transport.DisplayName} payload '{hash}' from the server.";
                QueueMainThreadPayloadCommitLocked(() =>
                    CommitProcessedPayloadLocked(transport, version, hash, payloadBytes, entries, payloadIndex, entries?.Count, successLogMessage),
                    roleEpoch);
            }
            catch (Exception ex)
            {
                QueueMainThreadPayloadCommitLocked(() =>
                {
                    lock (Sync)
                    {
                        if (!IsProcessingResultCurrentLocked(transport, version, hash))
                        {
                            return;
                        }

                        transport.ProcessingInFlight = false;
                        transport.ProcessingHash = "";
                        if (pendingTransfer.TransferKind == DeltaTransferKind)
                        {
                            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                                $"Failed to apply synchronized {transport.DisplayName} delta payload '{hash}'. Retrying as a full payload. {ex.Message}");
                            TryRequestFullFallbackOnceLocked(
                                transport,
                                hash,
                                $"source=delta_apply error={ex.Message}");
                            return;
                        }

                        if (pendingTransfer.IsFullFallbackRequest)
                        {
                            BlockManifestLocked(
                                transport,
                                hash,
                                $"source=full_after_delta_fallback error={ex.Message} fullFallbackAttempted=true");
                            return;
                        }

                        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                            $"Failed to assemble synchronized {transport.DisplayName} payload '{hash}'. Retrying. {ex.Message}");
                        EnsurePayloadRequestedLocked(transport, transport.DesiredPayloadManifest, "");
                    }
                }, roleEpoch);
            }
        }, roleEpoch);
    }
}
