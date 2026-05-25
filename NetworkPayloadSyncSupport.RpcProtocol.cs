using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private static readonly AccessTools.FieldRef<ZRoutedRpc, long> RoutedRpcIdRef =
        AccessTools.FieldRefAccess<ZRoutedRpc, long>("m_id");

    private sealed class OutboundChunkMessage
    {
        public long Sender { get; set; }
        public string DomainKey { get; set; } = "";
        public string Hash { get; set; } = "";
        public long RequestId { get; set; }
        public int TransferKind { get; set; }
        public string BaseHash { get; set; } = "";
        public int ChunkIndex { get; set; }
        public int ChunkCount { get; set; }
        public int CompressedSize { get; set; }
    }

    private sealed class OutboundTransferState
    {
        public int RoleEpoch { get; set; }
        public string Hash { get; set; } = "";
        public long RequestId { get; set; }
        public TransferArtifact Artifact { get; set; } = null!;
        public int NextChunkIndex { get; set; }
        public bool IsQueued { get; set; }
        public float LastRequestedAt { get; set; }
    }

    private sealed class PreparedOutboundChunk
    {
        public OutboundTransferKey Key { get; set; }
        public int RoleEpoch { get; set; }
        public long RequestId { get; set; }
        public OutboundChunkMessage Message { get; set; } = null!;
        public byte[] SourceBytes { get; set; } = Array.Empty<byte>();
        public int ChunkOffset { get; set; }
        public int ChunkLength { get; set; }
    }

    private static void EnsureRpcRegisteredLocked()
    {
        if (_host == null || ZRoutedRpc.instance == null || ReferenceEquals(_registeredRpc, ZRoutedRpc.instance))
        {
            return;
        }

        ZRoutedRpc.instance.Register<ZPackage>(PayloadRequestRpc, RPC_RequestPayload);
        ZRoutedRpc.instance.Register<ZPackage>(PayloadChunkRpc, RPC_ReceivePayloadChunk);
        _registeredRpc = ZRoutedRpc.instance;
    }

    private static bool IsOutboundTransferPeerReady(long sender)
    {
        if (sender == 0L)
        {
            return true;
        }

        return ZNet.instance?.GetPeer(sender)?.IsReady() == true;
    }

    private static bool IsOutboundTransferRequestEquivalent(OutboundTransferState state, string hash, long requestId, TransferArtifact artifact)
    {
        return state != null &&
               state.RequestId == requestId &&
               string.Equals(state.Hash, hash, StringComparison.Ordinal) &&
               state.Artifact != null &&
               string.Equals(state.Artifact.TargetHash, artifact.TargetHash, StringComparison.Ordinal) &&
               string.Equals(state.Artifact.BaseHash, artifact.BaseHash, StringComparison.Ordinal) &&
               state.Artifact.TransferKind == artifact.TransferKind;
    }

    private static void EnqueueOutboundTransferKeyLocked(OutboundTransferKey key, OutboundTransferState state)
    {
        if (state.IsQueued)
        {
            return;
        }

        PendingOutboundTransferKeys.Enqueue(key);
        state.IsQueued = true;
    }

    private static void UpdatePinnedOutboundTransferReferencesLocked<TEntry>(
        DomainTransport<TEntry> transport,
        OutboundTransferState? previousState,
        OutboundTransferState? nextState)
    {
        if (previousState != null)
        {
            DecrementRefCountLocked(transport.PinnedPayloadHashRefCounts, previousState.Hash);
            if (previousState.Artifact != null)
            {
                DecrementRefCountLocked(transport.PinnedPayloadHashRefCounts, previousState.Artifact.BaseHash);
                DecrementRefCountLocked(
                    transport.PinnedArtifactKeyRefCounts,
                    previousState.Artifact.CacheKey.Length > 0
                        ? previousState.Artifact.CacheKey
                        : BuildTransferArtifactCacheKey(previousState.Hash, previousState.Artifact.BaseHash));
            }
        }

        if (nextState != null)
        {
            IncrementRefCountLocked(transport.PinnedPayloadHashRefCounts, nextState.Hash);
            if (nextState.Artifact != null)
            {
                IncrementRefCountLocked(transport.PinnedPayloadHashRefCounts, nextState.Artifact.BaseHash);
                IncrementRefCountLocked(
                    transport.PinnedArtifactKeyRefCounts,
                    nextState.Artifact.CacheKey.Length > 0
                        ? nextState.Artifact.CacheKey
                        : BuildTransferArtifactCacheKey(nextState.Hash, nextState.Artifact.BaseHash));
            }
        }
    }

    private static void UpdatePinnedOutboundTransferReferencesForDomainLocked(
        string domainKey,
        OutboundTransferState? previousState,
        OutboundTransferState? nextState)
    {
        if (string.IsNullOrWhiteSpace(domainKey) ||
            !TransportsByDomainKey.TryGetValue(domainKey, out IDomainTransport? transport))
        {
            return;
        }

        transport.UpdatePinnedOutboundTransferReferences(previousState, nextState);
    }

    private static void RemoveActiveOutboundTransferLocked(OutboundTransferKey key)
    {
        if (ActiveOutboundTransfers.TryGetValue(key, out OutboundTransferState? state) && state != null)
        {
            UpdatePinnedOutboundTransferReferencesForDomainLocked(key.DomainKey, state, null);
        }

        ActiveOutboundTransfers.Remove(key);
    }

    private static void UpsertOutboundTransferLocked(string domainKey, long sender, string hash, long requestId, TransferArtifact artifact)
    {
        if (artifact == null)
        {
            return;
        }

        OutboundTransferKey key = new(sender, domainKey);
        float now = Time.realtimeSinceStartup;
        if (ActiveOutboundTransfers.TryGetValue(key, out OutboundTransferState? existingState))
        {
            if (existingState.RoleEpoch != _networkRoleEpoch || !IsOutboundTransferPeerReady(sender))
            {
                RemoveActiveOutboundTransferLocked(key);
            }
            else if (IsOutboundTransferRequestEquivalent(existingState, hash, requestId, artifact))
            {
                existingState.LastRequestedAt = now;
                EnqueueOutboundTransferKeyLocked(key, existingState);
                return;
            }
            else if (requestId != 0L && existingState.RequestId != 0L && requestId < existingState.RequestId)
            {
                return;
            }
        }

        OutboundTransferState state = new()
        {
            RoleEpoch = _networkRoleEpoch,
            Hash = hash ?? "",
            RequestId = requestId,
            Artifact = artifact,
            NextChunkIndex = 0,
            IsQueued = existingState?.IsQueued ?? false,
            LastRequestedAt = now
        };
        UpdatePinnedOutboundTransferReferencesForDomainLocked(domainKey, existingState, state);
        ActiveOutboundTransfers[key] = state;
        EnqueueOutboundTransferKeyLocked(key, state);
    }

    private static void RPC_RequestPayload(long sender, ZPackage package)
    {
        if (!DropNSpawnPlugin.IsSourceOfTruth)
        {
            return;
        }

        string domainKey = package.ReadString();
        string requestedHash = package.ReadString();
        string baseHash = package.ReadString();
        long requestId = package.GetPos() < package.Size() ? package.ReadLong() : 0L;
        if (string.IsNullOrWhiteSpace(domainKey) || string.IsNullOrWhiteSpace(requestedHash))
        {
            return;
        }

        lock (Sync)
        {
            EnsureRpcRegisteredLocked();
            if (TransportsByDomainKey.TryGetValue(domainKey, out IDomainTransport? transport))
            {
                transport.EnqueuePublishedPayloadChunks(sender, requestedHash, baseHash, requestId);
            }
        }
    }

    private static bool TryPrepareOutboundChunkForSendLocked(out PreparedOutboundChunk? preparedChunk)
    {
        preparedChunk = null;
        while (PendingOutboundTransferKeys.TryDequeue(out OutboundTransferKey key))
        {
            if (!ActiveOutboundTransfers.TryGetValue(key, out OutboundTransferState? state) || state == null)
            {
                continue;
            }

            state.IsQueued = false;
            if (state.RoleEpoch != _networkRoleEpoch || !IsOutboundTransferPeerReady(key.Sender))
            {
                RemoveActiveOutboundTransferLocked(key);
                continue;
            }

            if (state.Artifact == null ||
                state.NextChunkIndex < 0 ||
                state.NextChunkIndex >= state.Artifact.ChunkCount)
            {
                RemoveActiveOutboundTransferLocked(key);
                continue;
            }

            int chunkSizeBytes = Math.Max(1, state.Artifact.ChunkSizeBytes);
            int offset = state.NextChunkIndex * chunkSizeBytes;
            int remainingBytes = state.Artifact.CompressedBytes.Length - offset;
            if (remainingBytes <= 0)
            {
                RemoveActiveOutboundTransferLocked(key);
                continue;
            }

            int chunkLength = Math.Min(chunkSizeBytes, remainingBytes);
            if (_outboundBytesSentThisFrame + chunkLength > chunkSizeBytes * 2)
            {
                EnqueueOutboundTransferKeyLocked(key, state);
                return false;
            }

            preparedChunk = new PreparedOutboundChunk
            {
                Key = key,
                RoleEpoch = state.RoleEpoch,
                RequestId = state.RequestId,
                SourceBytes = state.Artifact.CompressedBytes,
                ChunkOffset = offset,
                ChunkLength = chunkLength,
                Message = new OutboundChunkMessage
                {
                    Sender = key.Sender,
                    DomainKey = key.DomainKey,
                    Hash = state.Hash,
                    RequestId = state.RequestId,
                    TransferKind = state.Artifact.TransferKind,
                    BaseHash = state.Artifact.BaseHash,
                    ChunkIndex = state.NextChunkIndex,
                    ChunkCount = state.Artifact.ChunkCount,
                    CompressedSize = state.Artifact.CompressedBytes.Length
                }
            };

            return true;
        }

        return false;
    }

    private static void FinalizePreparedOutboundChunkLocked(PreparedOutboundChunk preparedChunk, bool sent)
    {
        if (preparedChunk == null)
        {
            return;
        }

        if (preparedChunk.RoleEpoch != _networkRoleEpoch || !DropNSpawnPlugin.IsSourceOfTruth)
        {
            return;
        }

        if (!ActiveOutboundTransfers.TryGetValue(preparedChunk.Key, out OutboundTransferState? state) ||
            state == null ||
            state.RoleEpoch != _networkRoleEpoch ||
            state.RequestId != preparedChunk.RequestId)
        {
            return;
        }

        if (!IsOutboundTransferPeerReady(preparedChunk.Key.Sender))
        {
            RemoveActiveOutboundTransferLocked(preparedChunk.Key);
            return;
        }

        if (!sent)
        {
            EnqueueOutboundTransferKeyLocked(preparedChunk.Key, state);
            return;
        }

        _outboundBytesSentThisFrame += preparedChunk.ChunkLength;
        state.NextChunkIndex++;
        if (state.Artifact != null &&
            state.NextChunkIndex < state.Artifact.ChunkCount)
        {
            EnqueueOutboundTransferKeyLocked(preparedChunk.Key, state);
            return;
        }

        RemoveActiveOutboundTransferLocked(preparedChunk.Key);
    }

    private static bool SendOutboundChunk(PreparedOutboundChunk preparedChunk)
    {
        if (ZRoutedRpc.instance == null)
        {
            return false;
        }

        OutboundChunkMessage message = preparedChunk.Message;
        ZPackage response = new();
        response.Write(message.DomainKey);
        response.Write(message.Hash);
        response.Write(message.TransferKind);
        response.Write(message.BaseHash);
        response.Write(message.ChunkIndex);
        response.Write(message.ChunkCount);
        response.Write(message.CompressedSize);
        WriteChunkBytes(response, preparedChunk.SourceBytes, preparedChunk.ChunkOffset, preparedChunk.ChunkLength);
        response.Write(message.RequestId);
        ZRoutedRpc.instance.InvokeRoutedRPC(message.Sender, PayloadChunkRpc, response);
        return true;
    }

    private static void WriteChunkBytes(ZPackage package, byte[] sourceBytes, int offset, int count)
    {
        if (count <= 0)
        {
            package.Write(Array.Empty<byte>());
            return;
        }

        if (ZPackageWriterField?.GetValue(package) is BinaryWriter writer)
        {
            package.Write(count);
            writer.Write(sourceBytes, offset, count);
            return;
        }

        byte[] chunkBytes = new byte[count];
        Buffer.BlockCopy(sourceBytes, offset, chunkBytes, 0, count);
        package.Write(chunkBytes);
    }

    private static void RPC_ReceivePayloadChunk(long sender, ZPackage package)
    {
        if (DropNSpawnPlugin.IsSourceOfTruth)
        {
            return;
        }

        if (!IsServerRoutedSender(sender))
        {
            return;
        }

        string domainKey = package.ReadString();
        string hash = package.ReadString();
        int transferKind = package.ReadInt();
        string baseHash = package.ReadString();
        int chunkIndex = package.ReadInt();
        int chunkCount = package.ReadInt();
        int compressedSize = package.ReadInt();
        byte[] chunkBytes = package.ReadByteArray();
        long requestId = package.ReadLong();

        lock (Sync)
        {
            EnsureRpcRegisteredLocked();
            if (TransportsByDomainKey.TryGetValue(domainKey, out IDomainTransport? transport))
            {
                transport.ReceivePayloadChunk(sender, hash, requestId, transferKind, baseHash, chunkIndex, chunkCount, compressedSize, chunkBytes);
            }
        }
    }

    private static bool TryGetCurrentServerRoutedSender(out long sender)
    {
        sender = 0L;
        if (ZRoutedRpc.instance == null || ZNet.instance == null)
        {
            return false;
        }

        if (ZNet.instance.IsServer())
        {
            sender = RoutedRpcIdRef(ZRoutedRpc.instance);
            return sender != 0L;
        }

        ZNetPeer? serverPeer = ZNet.instance.GetServerPeer();
        if (serverPeer == null)
        {
            return false;
        }

        sender = serverPeer.m_uid;
        return sender != 0L;
    }

    private static bool IsServerRoutedSender(long sender)
    {
        return TryGetCurrentServerRoutedSender(out long expectedSender) && expectedSender == sender;
    }
}
