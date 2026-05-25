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
        int clientRequestPriority,
        DomainTransportHooks? hooks)
    {
        DomainKey = domainKey ?? "";
        DtoVersion = Math.Max(0, dtoVersion);
        TransportProfile = transportProfile;
        DisplayName = displayName ?? "";
        CacheDirectoryName = cacheDirectoryName ?? "";
        ClientRequestPriority = clientRequestPriority;
        Hooks = hooks ?? DomainTransportHooks.NoOp;
    }

    internal string DomainKey { get; }
    internal int DtoVersion { get; }
    internal DomainTransportProfile TransportProfile { get; }
    internal string DisplayName { get; }
    internal string CacheDirectoryName { get; }
    internal int ClientRequestPriority { get; }
    internal DomainTransportHooks Hooks { get; }
}

internal sealed class DomainTransportMetadata<TEntry> : DomainTransportMetadata
{
    internal DomainTransportMetadata(
        DomainDescriptor<TEntry> domain,
        int dtoVersion,
        DomainTransportProfile transportProfile,
        string displayName,
        string cacheDirectoryName,
        int clientRequestPriority,
        Func<TEntry, string> keySelector,
        Action applyPayloadAction,
        DomainTransportHooks? hooks = null)
        : base(
            domain?.DomainKey ?? "",
            dtoVersion,
            transportProfile,
            displayName,
            cacheDirectoryName,
            clientRequestPriority,
            hooks)
    {
        Domain = domain ?? throw new ArgumentNullException(nameof(domain));
        KeySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        ApplyPayloadAction = applyPayloadAction ?? throw new ArgumentNullException(nameof(applyPayloadAction));
    }

    internal DomainDescriptor<TEntry> Domain { get; }
    internal Func<TEntry, string> KeySelector { get; }
    internal Action ApplyPayloadAction { get; }
}
