using System;

namespace DropNSpawn;

internal abstract class DomainTransportMetadata
{
    protected DomainTransportMetadata(
        string domainKey,
        int dtoVersion,
        DomainTransportProfile transportProfile,
        string displayName,
        string cacheDirectoryName,
        int clientRequestPriority)
    {
        DomainKey = domainKey ?? "";
        DtoVersion = Math.Max(0, dtoVersion);
        TransportProfile = transportProfile;
        DisplayName = displayName ?? "";
        CacheDirectoryName = cacheDirectoryName ?? "";
        ClientRequestPriority = clientRequestPriority;
    }

    internal string DomainKey { get; }
    internal int DtoVersion { get; }
    internal DomainTransportProfile TransportProfile { get; }
    internal string DisplayName { get; }
    internal string CacheDirectoryName { get; }
    internal int ClientRequestPriority { get; }
}

internal sealed class DomainTransportMetadata<TEntry> : DomainTransportMetadata
{
    internal DomainTransportMetadata(
        string domainKey,
        int dtoVersion,
        DomainTransportProfile transportProfile,
        string displayName,
        string cacheDirectoryName,
        int clientRequestPriority,
        Func<TEntry, string> keySelector,
        Action applyPayloadAction)
        : base(
            domainKey,
            dtoVersion,
            transportProfile,
            displayName,
            cacheDirectoryName,
            clientRequestPriority)
    {
        KeySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        ApplyPayloadAction = applyPayloadAction ?? throw new ArgumentNullException(nameof(applyPayloadAction));
    }

    internal Func<TEntry, string> KeySelector { get; }
    internal Action ApplyPayloadAction { get; }
}
