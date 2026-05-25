using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private static void ClearAvailablePayloadLocked<TEntry>(DomainTransport<TEntry> transport, bool deleteCacheFile = false)
    {
        string hash = transport.AvailableHash;
        transport.AvailableHash = "";
        transport.AvailablePayloadBytes = null;
        transport.AvailableEntries = null;
        transport.AvailablePayloadIndex = null;
        if (deleteCacheFile && !string.IsNullOrWhiteSpace(hash))
        {
            DeleteCacheFileIfPresent(transport.CacheDirectoryName, hash);
        }
    }

    private static void InvalidateMalformedAvailablePayloadLocked<TEntry>(
        DomainTransport<TEntry> transport,
        string hash,
        string reason,
        Exception? ex = null)
    {
        ClearAvailablePayloadLocked(transport, deleteCacheFile: true);

        if (!string.IsNullOrWhiteSpace(hash) &&
            string.Equals(transport.DesiredPayloadManifest.Hash, hash, StringComparison.Ordinal))
        {
            BlockManifestLocked(transport, hash, reason);
            return;
        }

        string suffix = ex == null ? "" : $" {ex}";
        DropNSpawnPlugin.DropNSpawnLogger.LogError(
            $"Failed to deserialize in-memory synchronized {transport.DisplayName} payload '{hash}'. Clearing the cached client payload state.{suffix}");
    }

    private static void ClearInvalidManifestLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        transport.InvalidDesiredManifest = "";
    }

    private static bool TryPreserveLastKnownGoodOnInvalidManifestLocked<TEntry>(
        DomainTransport<TEntry> transport,
        string? manifestRaw,
        out List<TEntry> entries,
        out string payloadToken,
        out bool shouldLog)
    {
        string invalidManifest = manifestRaw ?? "";
        shouldLog = !string.Equals(transport.InvalidDesiredManifest, invalidManifest, StringComparison.Ordinal);
        transport.InvalidDesiredManifest = invalidManifest;
        transport.DesiredManifest = invalidManifest;
        transport.DesiredPayloadManifest = new PayloadManifest();
        ClearBlockedManifestLocked(transport);
        ClearPendingInboundTransferLocked(transport);
        transport.LastWaitingLogHash = "";
        transport.ProcessingInFlight = false;
        transport.ProcessingHash = "";
        transport.ProcessingVersion++;

        if (TryGetAvailableEntriesLocked(transport, out entries))
        {
            payloadToken = transport.AvailableHash;
            return true;
        }

        entries = new List<TEntry>();
        payloadToken = transport.AvailableHash;
        return false;
    }

    private static void ClearBlockedManifestLocked<TEntry>(DomainTransport<TEntry> transport)
    {
        transport.BlockedManifestHash = "";
        transport.BlockedManifestReason = "";
    }

    private static void RefreshBlockedManifestForDesiredHashLocked<TEntry>(DomainTransport<TEntry> transport, string desiredHash)
    {
        if (string.IsNullOrWhiteSpace(desiredHash) ||
            !string.Equals(transport.BlockedManifestHash, desiredHash, StringComparison.Ordinal))
        {
            ClearBlockedManifestLocked(transport);
        }

        if (string.IsNullOrWhiteSpace(desiredHash) ||
            !string.Equals(transport.FullFallbackAttemptedHash, desiredHash, StringComparison.Ordinal))
        {
            transport.FullFallbackAttemptedHash = "";
        }
    }

    private static bool IsManifestBlockedLocked<TEntry>(DomainTransport<TEntry> transport, string hash)
    {
        return !string.IsNullOrWhiteSpace(hash) &&
               string.Equals(transport.BlockedManifestHash, hash, StringComparison.Ordinal);
    }

    private static void BlockManifestLocked<TEntry>(DomainTransport<TEntry> transport, string hash, string reason)
    {
        transport.ProcessingInFlight = false;
        transport.ProcessingHash = "";
        transport.ProcessingVersion++;
        ClearPendingInboundTransferLocked(transport);
        transport.RequestInFlight = false;
        transport.RequestStartedAt = 0f;
        transport.LastWaitingLogHash = hash ?? "";
        transport.BlockedManifestHash = hash ?? "";
        transport.BlockedManifestReason = reason ?? "";
        DeleteCacheFileIfPresent(transport.CacheDirectoryName, hash ?? "");
        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"Synchronized {transport.DisplayName} payload '{hash}' is blocked until the server publishes a different manifest. desiredHash={(transport.DesiredPayloadManifest.Hash.Length > 0 ? transport.DesiredPayloadManifest.Hash : "<none>")} reason={reason}");
    }

    private static void HandleManifestChanged<TEntry>(DomainTransport<TEntry> transport, string? manifestRaw)
    {
        lock (Sync)
        {
            EnsureRpcRegisteredLocked();

            if (!PayloadManifest.TryParse(manifestRaw, out PayloadManifest manifest))
            {
                bool hasLastKnownGood = TryPreserveLastKnownGoodOnInvalidManifestLocked(
                    transport,
                    manifestRaw,
                    out _,
                    out _,
                    out bool shouldLog);
                if (shouldLog)
                {
                    DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                        $"Received invalid synchronized {transport.DisplayName} payload manifest '{manifestRaw}'. Keeping the last known good client payload state.");
                }

                if (hasLastKnownGood)
                {
                    QueueReloadActionLocked(transport);
                }
            }
            else
            {
                ClearInvalidManifestLocked(transport);
                transport.DesiredManifest = manifestRaw ?? "";
                transport.DesiredPayloadManifest = manifest;
                RefreshBlockedManifestForDesiredHashLocked(transport, manifest.Hash);
                CancelProcessingIfHashMismatchLocked(transport, manifest.Hash);
                NotifyTransportManifestSeenLocked(transport, manifest);

                if (manifest.IsEmpty)
                {
                    transport.AvailableHash = "";
                    transport.AvailablePayloadBytes = null;
                    transport.AvailableEntries = null;
                    transport.AvailablePayloadIndex = null;
                    ClearPendingInboundTransferLocked(transport);
                    transport.LastWaitingLogHash = "";
                    transport.ProcessingInFlight = false;
                    transport.ProcessingHash = "";
                    transport.ProcessingVersion++;
                    QueueReloadActionLocked(transport);
                }
                else if (string.Equals(transport.AvailableHash, manifest.Hash, StringComparison.Ordinal) &&
                         transport.AvailablePayloadBytes != null)
                {
                    QueueReloadActionLocked(transport);
                }
                else if (IsManifestBlockedLocked(transport, manifest.Hash))
                {
                }
                else if (TryScheduleCachedPayloadLoadLocked(transport, manifest))
                {
                }
                else if (EnsurePayloadRequestedLocked(transport, manifest))
                {
                    transport.LastWaitingLogHash = manifest.Hash;
                    DropNSpawnPlugin.DropNSpawnLogger.LogDebug(
                        $"Requesting synchronized {transport.DisplayName} payload '{manifest.Hash}' from the server.");
                }
            }
        }
    }

    private static void NotifyTransportManifestSeenLocked<TEntry>(DomainTransport<TEntry> transport, PayloadManifest manifest)
    {
        transport.Metadata.Hooks.OnManifestSeen(
            manifest.IsEmpty,
            manifest.Hash,
            manifest.CompressedSize,
            manifest.ChunkCount,
            manifest.EntryCount);
    }

    private static bool TryScheduleCachedPayloadLoadLocked<TEntry>(DomainTransport<TEntry> transport, PayloadManifest manifest)
    {
        if (manifest.IsEmpty)
        {
            return false;
        }

        if (IsManifestBlockedLocked(transport, manifest.Hash))
        {
            return false;
        }

        if (transport.ProcessingInFlight &&
            string.Equals(transport.ProcessingHash, manifest.Hash, StringComparison.Ordinal))
        {
            return true;
        }

        string cachePath = GetCachePath(transport.CacheDirectoryName, manifest.Hash);
        if (!File.Exists(cachePath))
        {
            return false;
        }

        int version = ++transport.ProcessingVersion;
        transport.ProcessingInFlight = true;
        transport.ProcessingHash = manifest.Hash;
        int roleEpoch = _networkRoleEpoch;

        QueueCriticalPayloadProcessingJobLocked(() =>
        {
            try
            {
                if (!TryReadCachedPayloadBytes(transport.CacheDirectoryName, transport.DisplayName, manifest.Hash, out byte[] payloadBytes, out byte[] compressedBytes))
                {
                    QueueMainThreadPayloadCommitLocked(() =>
                    {
                        lock (Sync)
                        {
                            if (!IsProcessingResultCurrentLocked(transport, version, manifest.Hash))
                            {
                                return;
                            }

                            transport.ProcessingInFlight = false;
                            transport.ProcessingHash = "";
                            if (EnsurePayloadRequestedLocked(transport, transport.DesiredPayloadManifest))
                            {
                                transport.LastWaitingLogHash = manifest.Hash;
                                DropNSpawnPlugin.DropNSpawnLogger.LogDebug(
                                    $"Requesting synchronized {transport.DisplayName} payload '{manifest.Hash}' from the server.");
                            }
                        }
                    }, roleEpoch);
                    return;
                }

                QueueMainThreadPayloadCommitLocked(() =>
                    CommitProcessedPayloadLocked(
                        transport,
                        version,
                        manifest.Hash,
                        payloadBytes,
                        null,
                        null,
                        null,
                        $"Loaded synchronized {transport.DisplayName} payload '{manifest.Hash}' from cache."),
                    roleEpoch);
            }
            catch (Exception ex)
            {
                QueueMainThreadPayloadCommitLocked(() =>
                {
                    lock (Sync)
                    {
                        if (!IsProcessingResultCurrentLocked(transport, version, manifest.Hash))
                        {
                            return;
                        }

                        transport.ProcessingInFlight = false;
                        transport.ProcessingHash = "";
                        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                            $"Failed to read cached {transport.DisplayName} payload '{manifest.Hash}'. {ex.Message}");
                        if (EnsurePayloadRequestedLocked(transport, transport.DesiredPayloadManifest))
                        {
                            transport.LastWaitingLogHash = manifest.Hash;
                            DropNSpawnPlugin.DropNSpawnLogger.LogDebug(
                                $"Requesting synchronized {transport.DisplayName} payload '{manifest.Hash}' from the server.");
                        }
                    }
                }, roleEpoch);
            }
        }, roleEpoch);

        return true;
    }

    private static void CancelProcessingIfHashMismatchLocked<TEntry>(DomainTransport<TEntry> transport, string desiredHash)
    {
        if (!transport.ProcessingInFlight ||
            string.Equals(transport.ProcessingHash, desiredHash, StringComparison.Ordinal))
        {
            return;
        }

        transport.ProcessingInFlight = false;
        transport.ProcessingHash = "";
        transport.ProcessingVersion++;
    }
}
