using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private sealed class PayloadEntryIndex<TEntry>
    {
        public List<string> OrderedKeys { get; } = new();
        public Dictionary<string, TEntry> EntriesByKey { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> EntrySignaturesByKey { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PublishedPayloadHistoryEntry<TEntry>
    {
        public string Hash { get; set; } = "";
        public byte[] PayloadBytes { get; set; } = Array.Empty<byte>();
        public PayloadEntryIndex<TEntry>? PayloadIndex { get; set; }
        public int EstimatedBytes { get; set; }
        public LinkedListNode<string>? LruNode { get; set; }
    }

    private sealed class TransferArtifact
    {
        public string CacheKey { get; set; } = "";
        public int TransferKind { get; set; }
        public string BaseHash { get; set; } = "";
        public string TargetHash { get; set; } = "";
        public byte[] CompressedBytes { get; set; } = Array.Empty<byte>();
        public int ChunkSizeBytes { get; set; }
        public int ChunkCount { get; set; }
        public int EstimatedBytes { get; set; }
        public LinkedListNode<string>? LruNode { get; set; }
    }

    private static void RememberPublishedPayloadLocked<TEntry>(
        DomainTransport<TEntry> transport,
        string hash,
        byte[]? payloadBytes,
        PayloadEntryIndex<TEntry>? payloadIndex)
    {
        if (string.IsNullOrWhiteSpace(hash) || payloadBytes == null || payloadBytes.Length == 0)
        {
            return;
        }

        PublishedPayloadHistoryEntry<TEntry> entry;
        if (!transport.PublishedPayloadHistory.TryGetValue(hash, out entry))
        {
            entry = new PublishedPayloadHistoryEntry<TEntry>
            {
                Hash = hash
            };
            transport.PublishedPayloadHistory[hash] = entry;
        }

        int previousEstimatedBytes = entry.EstimatedBytes;
        entry.PayloadBytes = payloadBytes;
        entry.PayloadIndex = payloadIndex;
        entry.EstimatedBytes = EstimatePublishedPayloadHistoryEntryBytes(hash, payloadBytes, payloadIndex);
        AdjustPublishedPayloadHistoryBytesLocked(transport, entry.EstimatedBytes - previousEstimatedBytes);
        LinkedListNode<string>? payloadHistoryNode = entry.LruNode;
        TouchLruNodeLocked(transport.PublishedPayloadHistoryLru, hash, ref payloadHistoryNode);
        entry.LruNode = payloadHistoryNode;
    }

    private static int EstimatePublishedPayloadHistoryEntryBytes<TEntry>(string hash, byte[] payloadBytes, PayloadEntryIndex<TEntry>? payloadIndex)
    {
        long estimatedBytes = 64L + EstimateStringBytes(hash) + payloadBytes.Length + EstimatePayloadIndexBytes(payloadIndex);
        return ClampByteEstimate(estimatedBytes);
    }

    private static int EstimatePayloadIndexBytes<TEntry>(PayloadEntryIndex<TEntry>? payloadIndex)
    {
        if (payloadIndex == null)
        {
            return 0;
        }

        long estimatedBytes = 64L;
        estimatedBytes += payloadIndex.OrderedKeys.Count * 24L;
        foreach (string key in payloadIndex.OrderedKeys)
        {
            estimatedBytes += EstimateStringBytes(key);
        }

        estimatedBytes += payloadIndex.EntriesByKey.Count * 24L;
        foreach ((string key, string signature) in payloadIndex.EntrySignaturesByKey)
        {
            estimatedBytes += 24L + EstimateStringBytes(key) + EstimateStringBytes(signature);
        }

        return ClampByteEstimate(estimatedBytes);
    }

    private static int EstimateTransferArtifactBytes(TransferArtifact artifact)
    {
        if (artifact == null)
        {
            return 0;
        }

        long estimatedBytes = 64L +
                              EstimateStringBytes(artifact.BaseHash) +
                              EstimateStringBytes(artifact.TargetHash) +
                              artifact.CompressedBytes.Length;
        return ClampByteEstimate(estimatedBytes);
    }

    private static int EstimateStringBytes(string? value)
    {
        return (value?.Length ?? 0) * sizeof(char);
    }

    private static int ClampByteEstimate(long estimatedBytes)
    {
        return estimatedBytes <= 0L
            ? 0
            : estimatedBytes >= int.MaxValue
                ? int.MaxValue
                : (int)estimatedBytes;
    }

    private static int GetTrimTargetBytes(int budgetBytes)
    {
        if (budgetBytes <= 0)
        {
            return 0;
        }

        return Math.Max(0, budgetBytes * PublishedCacheTrimLowWatermarkPercent / 100);
    }

    private static void TouchLruNodeLocked(LinkedList<string> lru, string key, ref LinkedListNode<string>? node)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (node == null || node.List != lru)
        {
            node = lru.AddLast(key);
            return;
        }

        if (node.Next == null)
        {
            return;
        }

        lru.Remove(node);
        lru.AddLast(node);
    }

    private static void RemoveLruNodeLocked(LinkedList<string> lru, ref LinkedListNode<string>? node)
    {
        if (node == null)
        {
            return;
        }

        if (node.List == lru)
        {
            lru.Remove(node);
        }

        node = null;
    }

    private static void AdjustPublishedPayloadHistoryBytesLocked<TEntry>(DomainTransport<TEntry> transport, int deltaBytes)
    {
        if (deltaBytes == 0)
        {
            return;
        }

        transport.PublishedPayloadHistoryBytes = Math.Max(0, transport.PublishedPayloadHistoryBytes + deltaBytes);
        transport.PublishedCacheBytes = Math.Max(0, transport.PublishedCacheBytes + deltaBytes);
    }

    private static void AdjustPublishedTransferArtifactBytesLocked<TEntry>(DomainTransport<TEntry> transport, int deltaBytes)
    {
        if (deltaBytes == 0)
        {
            return;
        }

        transport.PublishedTransferArtifactBytes = Math.Max(0, transport.PublishedTransferArtifactBytes + deltaBytes);
        transport.PublishedCacheBytes = Math.Max(0, transport.PublishedCacheBytes + deltaBytes);
    }

    private static void RemovePublishedPayloadHistoryEntryLocked<TEntry>(DomainTransport<TEntry> transport, PublishedPayloadHistoryEntry<TEntry>? entry)
    {
        if (entry == null)
        {
            return;
        }

        transport.PublishedPayloadHistory.Remove(entry.Hash);
        LinkedListNode<string>? payloadHistoryNode = entry.LruNode;
        RemoveLruNodeLocked(transport.PublishedPayloadHistoryLru, ref payloadHistoryNode);
        entry.LruNode = payloadHistoryNode;
        AdjustPublishedPayloadHistoryBytesLocked(transport, -entry.EstimatedBytes);
    }

    private static void RemoveTransferArtifactLocked<TEntry>(DomainTransport<TEntry> transport, TransferArtifact? artifact)
    {
        if (artifact == null)
        {
            return;
        }

        transport.PublishedTransferArtifacts.Remove(artifact.CacheKey);
        LinkedListNode<string>? artifactNode = artifact.LruNode;
        RemoveLruNodeLocked(transport.PublishedTransferArtifactLru, ref artifactNode);
        artifact.LruNode = artifactNode;
        AdjustPublishedTransferArtifactBytesLocked(transport, -artifact.EstimatedBytes);
    }

    private static void ClearPublishedPayloadHistoryLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        transport.PublishedPayloadHistory.Clear();
        transport.PublishedPayloadHistoryLru.Clear();
        transport.PublishedCacheBytes = Math.Max(0, transport.PublishedCacheBytes - transport.PublishedPayloadHistoryBytes);
        transport.PublishedPayloadHistoryBytes = 0;
    }

    private static void ClearPublishedTransferArtifactsLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        transport.PublishedTransferArtifacts.Clear();
        transport.PublishedTransferArtifactLru.Clear();
        transport.PublishedCacheBytes = Math.Max(0, transport.PublishedCacheBytes - transport.PublishedTransferArtifactBytes);
        transport.PublishedTransferArtifactBytes = 0;
    }

    private static void ClearPinnedCacheReferencesLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        transport.PinnedPayloadHashRefCounts.Clear();
        transport.PinnedArtifactKeyRefCounts.Clear();
    }

    private static void IncrementRefCountLocked(Dictionary<string, int> refCounts, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        refCounts[key] = refCounts.TryGetValue(key, out int existingCount)
            ? existingCount + 1
            : 1;
    }

    private static void DecrementRefCountLocked(Dictionary<string, int> refCounts, string key)
    {
        if (string.IsNullOrWhiteSpace(key) ||
            !refCounts.TryGetValue(key, out int existingCount))
        {
            return;
        }

        if (existingCount <= 1)
        {
            refCounts.Remove(key);
            return;
        }

        refCounts[key] = existingCount - 1;
    }

    private static bool IsPinnedLocked(Dictionary<string, int> refCounts, string key)
    {
        return key.Length > 0 &&
               refCounts.TryGetValue(key, out int refCount) &&
               refCount > 0;
    }

    private static bool TryTrimOneTransferArtifactLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        int attemptsRemaining = transport.PublishedTransferArtifacts.Count;
        LinkedListNode<string>? node = transport.PublishedTransferArtifactLru.First;
        while (node != null && attemptsRemaining-- > 0)
        {
            LinkedListNode<string> currentNode = node;
            node = node.Next;
            string cacheKey = currentNode.Value;
            if (!transport.PublishedTransferArtifacts.TryGetValue(cacheKey, out TransferArtifact? artifact) || artifact == null)
            {
                transport.PublishedTransferArtifactLru.Remove(currentNode);
                continue;
            }

            if (IsPinnedLocked(transport.PinnedArtifactKeyRefCounts, cacheKey))
            {
                transport.PublishedTransferArtifactLru.Remove(currentNode);
                transport.PublishedTransferArtifactLru.AddLast(currentNode);
                continue;
            }

            RemoveTransferArtifactLocked(transport, artifact);
            return true;
        }

        return false;
    }

    private static bool TryTrimOnePayloadHistoryEntryLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        int attemptsRemaining = transport.PublishedPayloadHistory.Count;
        LinkedListNode<string>? node = transport.PublishedPayloadHistoryLru.First;
        while (node != null && attemptsRemaining-- > 0)
        {
            LinkedListNode<string> currentNode = node;
            node = node.Next;
            string hash = currentNode.Value;
            if (!transport.PublishedPayloadHistory.TryGetValue(hash, out PublishedPayloadHistoryEntry<TEntry>? entry) || entry == null)
            {
                transport.PublishedPayloadHistoryLru.Remove(currentNode);
                continue;
            }

            if (IsPinnedLocked(transport.PinnedPayloadHashRefCounts, hash))
            {
                transport.PublishedPayloadHistoryLru.Remove(currentNode);
                transport.PublishedPayloadHistoryLru.AddLast(currentNode);
                continue;
            }

            RemovePublishedPayloadHistoryEntryLocked(transport, entry);
            return true;
        }

        return false;
    }

    private static void TrimTransportCachesLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        int budgetBytes = transport.PublishedCacheBudgetBytes;
        if (budgetBytes <= 0 || transport.PublishedCacheBytes <= budgetBytes)
        {
            return;
        }

        int trimTargetBytes = GetTrimTargetBytes(budgetBytes);
        int attemptsRemaining = transport.PublishedPayloadHistory.Count + transport.PublishedTransferArtifacts.Count;
        while (transport.PublishedCacheBytes > trimTargetBytes && attemptsRemaining-- > 0)
        {
            if (TryTrimOneTransferArtifactLocked(transport))
            {
                continue;
            }

            if (!TryTrimOnePayloadHistoryEntryLocked(transport))
            {
                break;
            }
        }
    }

    private static int GetTotalPublishedCacheBytesLocked()
    {
        int totalBytes = 0;
        foreach (IDomainTransport transport in AllTransports)
        {
            totalBytes += transport.PublishedCacheBytes;
        }

        return totalBytes;
    }

    private static bool TryTrimAnyTransportCacheOnceLocked()
    {
        foreach (IDomainTransport transport in AllTransports.OrderByDescending(static transport => transport.PublishedCacheBytes))
        {
            if (transport.PublishedCacheBytes <= 0)
            {
                continue;
            }

            if (transport.TryTrimPublishedCacheOnce())
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryTrimTransportCacheOnceLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        return TryTrimOneTransferArtifactLocked(transport) ||
               TryTrimOnePayloadHistoryEntryLocked(transport);
    }

    private static void TrimAllPublishedCachesLocked()
    {
        int totalPublishedCacheBytes = GetTotalPublishedCacheBytesLocked();
        if (totalPublishedCacheBytes <= TotalPublishedCacheBudgetBytes)
        {
            return;
        }

        int trimTargetBytes = GetTrimTargetBytes(TotalPublishedCacheBudgetBytes);
        int attemptsRemaining = 0;
        foreach (IDomainTransport transport in AllTransports)
        {
            attemptsRemaining += transport.PublishedPayloadHistoryItemCount + transport.PublishedTransferArtifactCount;
        }
        while (totalPublishedCacheBytes > trimTargetBytes && attemptsRemaining-- > 0)
        {
            if (!TryTrimAnyTransportCacheOnceLocked())
            {
                break;
            }

            totalPublishedCacheBytes = GetTotalPublishedCacheBytesLocked();
        }
    }

    private static bool BuildPayloadIndex<TEntry>(
        DomainTransport<TEntry> transport,
        List<TEntry> entries,
        string payloadHash,
        out PayloadEntryIndex<TEntry>? payloadIndex)
    {
        payloadIndex = new PayloadEntryIndex<TEntry>();
        foreach (TEntry entry in entries)
        {
            string key = transport.KeySelector(entry);
            if (key.Length == 0)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                    $"Skipping synchronized {transport.DisplayName} delta support for payload '{payloadHash}' because it contains an empty entry key.");
                payloadIndex = null;
                return false;
            }

            if (payloadIndex.EntriesByKey.ContainsKey(key))
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                    $"Skipping synchronized {transport.DisplayName} delta support for payload '{payloadHash}' because it contains duplicate key '{key}'.");
                payloadIndex = null;
                return false;
            }

            payloadIndex.OrderedKeys.Add(key);
            payloadIndex.EntriesByKey[key] = entry;
            payloadIndex.EntrySignaturesByKey[key] = transport.EntrySignatureBuilder(entry);
        }

        return true;
    }

    private static bool TryEnsurePayloadIndex<TEntry>(
        DomainTransport<TEntry> transport,
        string payloadHash,
        byte[] payloadBytes,
        PayloadEntryIndex<TEntry>? existingIndex,
        out PayloadEntryIndex<TEntry>? payloadIndex)
    {
        if (existingIndex != null)
        {
            payloadIndex = existingIndex;
            return true;
        }

        List<TEntry> entries = transport.Deserializer(payloadBytes);
        return BuildPayloadIndex(transport, entries, payloadHash, out payloadIndex);
    }

    private static bool TryBuildDeltaPayloadBytes<TEntry>(
        DomainTransport<TEntry> transport,
        string baseHash,
        string targetHash,
        PayloadEntryIndex<TEntry> basePayloadIndex,
        PayloadEntryIndex<TEntry> targetPayloadIndex,
        out byte[] deltaPayloadBytes)
    {
        List<string> orderedKeys = new(targetPayloadIndex.OrderedKeys.Count);
        HashSet<string> orderedKeySet = new(StringComparer.OrdinalIgnoreCase);
        List<TEntry> upserts = new();
        foreach (string key in targetPayloadIndex.OrderedKeys)
        {
            if (!orderedKeySet.Add(key))
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                    $"Skipping synchronized {transport.DisplayName} delta {baseHash}->{targetHash} because payload '{targetHash}' contains duplicate key '{key}'.");
                deltaPayloadBytes = Array.Empty<byte>();
                return false;
            }

            orderedKeys.Add(key);
            if (!basePayloadIndex.EntrySignaturesByKey.TryGetValue(key, out string? baseSignature) ||
                !targetPayloadIndex.EntrySignaturesByKey.TryGetValue(key, out string? targetSignature) ||
                !string.Equals(baseSignature, targetSignature, StringComparison.Ordinal))
            {
                upserts.Add(targetPayloadIndex.EntriesByKey[key]);
            }
        }

        List<string> removedKeys = new();
        foreach (string key in basePayloadIndex.OrderedKeys)
        {
            if (!targetPayloadIndex.EntriesByKey.ContainsKey(key))
            {
                removedKeys.Add(key);
            }
        }

        int changedEntryCount = removedKeys.Count + upserts.Count;
        if (changedEntryCount == 0)
        {
            deltaPayloadBytes = Array.Empty<byte>();
            return false;
        }

        if (targetPayloadIndex.OrderedKeys.Count > 0 &&
            changedEntryCount > Math.Ceiling(targetPayloadIndex.OrderedKeys.Count * transport.TransportPolicy.MaxDeltaChangedEntryRatio))
        {
            deltaPayloadBytes = Array.Empty<byte>();
            return false;
        }

        ZPackage package = new();
        package.Write(DeltaDtoVersion);
        package.Write(baseHash);
        package.Write(targetHash);
        WriteStringList(package, orderedKeys);
        WriteStringList(package, removedKeys);
        package.Write(upserts.Count == 0 ? Array.Empty<byte>() : transport.Serializer(upserts));
        deltaPayloadBytes = package.GetArray();
        return true;
    }

    private static bool TryBuildPrimaryDeltaArtifact<TEntry>(
        DomainTransport<TEntry> transport,
        string baseHash,
        byte[]? basePayloadBytes,
        PayloadEntryIndex<TEntry>? basePayloadIndex,
        string targetHash,
        byte[] targetPayloadBytes,
        PayloadEntryIndex<TEntry>? targetPayloadIndex,
        byte[] fullCompressedPayloadBytes,
        out PayloadEntryIndex<TEntry>? resolvedTargetPayloadIndex,
        out TransferArtifact? artifact)
    {
        resolvedTargetPayloadIndex = targetPayloadIndex;
        artifact = null;

        if (!transport.SupportsDeltaTransfers)
        {
            return false;
        }

        string normalizedBaseHash = NormalizeBaseHash(baseHash);
        if (normalizedBaseHash.Length == 0 ||
            string.IsNullOrWhiteSpace(targetHash) ||
            string.Equals(normalizedBaseHash, targetHash, StringComparison.Ordinal) ||
            basePayloadBytes == null ||
            basePayloadBytes.Length == 0 ||
            targetPayloadBytes.Length == 0 ||
            fullCompressedPayloadBytes.Length == 0)
        {
            return false;
        }

        try
        {
            if (!TryEnsurePayloadIndex(
                    transport,
                    targetHash,
                    targetPayloadBytes,
                    targetPayloadIndex,
                    out resolvedTargetPayloadIndex) ||
                resolvedTargetPayloadIndex == null)
            {
                return false;
            }

            if (!TryEnsurePayloadIndex(
                    transport,
                    normalizedBaseHash,
                    basePayloadBytes,
                    basePayloadIndex,
                    out PayloadEntryIndex<TEntry>? resolvedBasePayloadIndex) ||
                resolvedBasePayloadIndex == null)
            {
                return false;
            }

            if (!TryBuildDeltaPayloadBytes(
                    transport,
                    normalizedBaseHash,
                    targetHash,
                    resolvedBasePayloadIndex,
                    resolvedTargetPayloadIndex,
                    out byte[] deltaPayloadBytes))
            {
                return false;
            }

            byte[] compressedDeltaPayloadBytes = CompressBytes(deltaPayloadBytes);
            if (compressedDeltaPayloadBytes.Length == 0 ||
                compressedDeltaPayloadBytes.Length >= fullCompressedPayloadBytes.Length * transport.TransportPolicy.MaxDeltaCompressedSizeRatio)
            {
                return false;
            }

            artifact = new TransferArtifact
            {
                CacheKey = BuildTransferArtifactCacheKey(targetHash, normalizedBaseHash),
                TransferKind = DeltaTransferKind,
                BaseHash = normalizedBaseHash,
                TargetHash = targetHash,
                CompressedBytes = compressedDeltaPayloadBytes,
                ChunkSizeBytes = transport.ChunkSizeBytes,
                ChunkCount = Math.Max(1, (int)Math.Ceiling(compressedDeltaPayloadBytes.Length / (double)transport.ChunkSizeBytes))
            };
            artifact.EstimatedBytes = EstimateTransferArtifactBytes(artifact);
            return true;
        }
        catch (Exception ex)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogDebug(
                $"Failed to prebuild synchronized {transport.DisplayName} primary delta artifact '{normalizedBaseHash}->{targetHash}'. {ex.Message}");
            return false;
        }
    }

    private static byte[] ApplyDeltaPayloadBytes<TEntry>(
        DomainTransport<TEntry> transport,
        string baseHash,
        string targetHash,
        byte[] deltaPayloadBytes,
        PayloadEntryIndex<TEntry>? basePayloadIndexSnapshot,
        byte[]? basePayloadBytesSnapshot,
        out List<TEntry> mergedEntries)
    {
        mergedEntries = new List<TEntry>();
        if (string.IsNullOrWhiteSpace(baseHash))
        {
            throw new InvalidDataException($"Missing base hash for synchronized {transport.DisplayName} delta payload '{targetHash}'.");
        }

        ZPackage package = new(deltaPayloadBytes);
        int version = package.ReadInt();
        if (version != DeltaDtoVersion)
        {
            throw new InvalidDataException($"Unsupported delta payload DTO version '{version}'.");
        }

        string declaredBaseHash = package.ReadString();
        string declaredTargetHash = package.ReadString();
        if (!string.Equals(baseHash, declaredBaseHash, StringComparison.Ordinal) ||
            !string.Equals(targetHash, declaredTargetHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Synchronized {transport.DisplayName} delta payload hash mismatch. Expected {baseHash}->{targetHash} but got {declaredBaseHash}->{declaredTargetHash}.");
        }

        List<string> orderedKeys = ReadStringList(package) ?? new List<string>();
        List<string> removedKeys = ReadStringList(package) ?? new List<string>();
        byte[] upsertPayloadBytes = package.ReadByteArray();
        List<TEntry> upsertEntries = upsertPayloadBytes.Length == 0 ? new List<TEntry>() : transport.Deserializer(upsertPayloadBytes);

        Dictionary<string, TEntry> entriesByKey = new(StringComparer.OrdinalIgnoreCase);

        if (basePayloadIndexSnapshot != null)
        {
            foreach (KeyValuePair<string, TEntry> pair in basePayloadIndexSnapshot.EntriesByKey)
            {
                entriesByKey[pair.Key] = pair.Value;
            }
        }
        else
        {
            byte[] basePayloadBytes = basePayloadBytesSnapshot ?? Array.Empty<byte>();
            if (basePayloadBytes.Length == 0 &&
                !TryReadCachedPayloadBytes(transport.CacheDirectoryName, transport.DisplayName, baseHash, out basePayloadBytes, out _))
            {
                throw new InvalidDataException(
                    $"Missing cached base payload '{baseHash}' required to apply synchronized {transport.DisplayName} delta '{targetHash}'.");
            }

            List<TEntry> baseEntries = transport.Deserializer(basePayloadBytes);
            foreach (TEntry entry in baseEntries)
            {
                entriesByKey[transport.KeySelector(entry)] = entry;
            }
        }

        foreach (string removedKey in removedKeys)
        {
            entriesByKey.Remove(removedKey);
        }

        foreach (TEntry entry in upsertEntries)
        {
            entriesByKey[transport.KeySelector(entry)] = entry;
        }

        mergedEntries = new List<TEntry>(orderedKeys.Count);
        foreach (string key in orderedKeys)
        {
            if (!entriesByKey.TryGetValue(key, out TEntry? entry))
            {
                throw new InvalidDataException(
                    $"Synchronized {transport.DisplayName} delta payload '{targetHash}' is missing ordered entry '{key}'.");
            }

            mergedEntries.Add(entry);
        }

        return transport.Serializer(mergedEntries);
    }
}
