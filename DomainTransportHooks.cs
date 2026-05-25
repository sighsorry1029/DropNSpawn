using System;

namespace DropNSpawn;

internal abstract class DomainTransportHooks
{
    internal static DomainTransportHooks NoOp { get; } = new NoOpDomainTransportHooks();

    internal virtual void OnTransportStateReset()
    {
    }

    internal virtual void OnManifestSeen(
        bool isEmpty,
        string manifestHash,
        int compressedSize,
        int chunkCount,
        int? entryCount)
    {
    }

    internal virtual void OnPayloadReady(
        string hash,
        int? entryCount,
        string successLogMessage,
        string desiredManifestHash,
        int? desiredEntryCount)
    {
    }

    private sealed class NoOpDomainTransportHooks : DomainTransportHooks
    {
    }
}
