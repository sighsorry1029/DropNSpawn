using System;
using System.Globalization;

namespace DropNSpawn;

internal sealed class SpawnSystemTransportHooks : DomainTransportHooks
{
    internal static SpawnSystemTransportHooks Instance { get; } = new();

    private string _lastLoggedManifestHash = "";
    private string _lastLoggedPayloadReadyHash = "";

    internal override void OnTransportStateReset()
    {
        _lastLoggedManifestHash = "";
        _lastLoggedPayloadReadyHash = "";
    }

    internal override void OnManifestSeen(
        bool isEmpty,
        string manifestHash,
        int compressedSize,
        int chunkCount,
        int? entryCount)
    {
        string normalizedHash = isEmpty ? "<empty>" : (manifestHash ?? "");
        if (string.Equals(_lastLoggedManifestHash, normalizedHash, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedManifestHash = normalizedHash;
        if (isEmpty)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo("Spawnsystem sync stage=manifest_seen hash=<empty>");
            return;
        }

        string entryCountText = entryCount.HasValue
            ? entryCount.Value.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=manifest_seen hash={manifestHash} compressedBytes={compressedSize.ToString(CultureInfo.InvariantCulture)} chunks={chunkCount.ToString(CultureInfo.InvariantCulture)} entries={entryCountText}");
    }

    internal override void OnPayloadReady(
        string hash,
        int? entryCount,
        string successLogMessage,
        string desiredManifestHash,
        int? desiredEntryCount)
    {
        if (string.IsNullOrWhiteSpace(hash) ||
            string.Equals(_lastLoggedPayloadReadyHash, hash, StringComparison.Ordinal))
        {
            return;
        }

        _lastLoggedPayloadReadyHash = hash;
        string source = successLogMessage.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0
            ? "cache"
            : "network";
        if (!entryCount.HasValue &&
            string.Equals(desiredManifestHash, hash, StringComparison.Ordinal))
        {
            entryCount = desiredEntryCount;
        }

        string entryCountText = entryCount.HasValue
            ? entryCount.Value.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"Spawnsystem sync stage=payload_ready hash={hash} source={source} entries={entryCountText}");
    }
}
