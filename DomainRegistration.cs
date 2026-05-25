using System;

namespace DropNSpawn;

[Flags]
internal enum DomainWorkKinds
{
    None = 0,
    Runtime = 1 << 0,
    SnapshotBuild = 1 << 1,
    Reconcile = 1 << 2
}

internal abstract class DomainRegistration
{
    protected DomainRegistration(
        DomainDescriptor descriptor,
        DomainTransportMetadata transportMetadata,
        DomainWorkKinds workKinds,
        Action initializeRuntime)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        TransportMetadata = transportMetadata ?? throw new ArgumentNullException(nameof(transportMetadata));
        WorkKinds = workKinds;
        InitializeRuntime = initializeRuntime ?? throw new ArgumentNullException(nameof(initializeRuntime));
    }

    internal DomainDescriptor Descriptor { get; }
    internal DomainTransportMetadata TransportMetadata { get; }
    internal DomainWorkKinds WorkKinds { get; }
    internal Action InitializeRuntime { get; }
}

internal class DomainRegistration<TEntry> : DomainRegistration
{
    internal DomainRegistration(
        DomainDescriptor<TEntry> descriptor,
        DomainTransportMetadata<TEntry> transportMetadata,
        DomainWorkKinds workKinds,
        Action initializeRuntime)
        : base(descriptor, transportMetadata, workKinds, initializeRuntime)
    {
        DescriptorTyped = descriptor;
        TransportMetadataTyped = transportMetadata;
    }

    internal DomainDescriptor<TEntry> DescriptorTyped { get; }
    internal DomainTransportMetadata<TEntry> TransportMetadataTyped { get; }
}

/// <summary>
/// Immutable domain module definition used to register descriptor, transport intent, and runtime work capabilities.
/// It does not own load state or compiled/live domain state.
/// </summary>
internal sealed class DomainModuleDefinition<TEntry> : DomainRegistration<TEntry>
{
    internal DomainModuleDefinition(
        string domainKey,
        DropNSpawnPlugin.ReloadDomain reloadDomain,
        string manifestSettingKey,
        int manifestPriority,
        Func<string, bool> shouldReloadForPath,
        Action reload,
        Action initializeRuntime,
        Action<string> onGameDataReady,
        Func<bool> handleExpandWorldDataReady,
        int dtoVersion,
        DomainTransportProfile transportProfile,
        string displayName,
        string cacheDirectoryName,
        int clientRequestPriority,
        Func<TEntry, string> keySelector,
        Action applyPayloadAction,
        DomainWorkKinds workKinds,
        Func<bool>? hasPendingSnapshotBuildWork = null,
        Func<float, bool>? processPendingSnapshotBuildStep = null,
        Func<bool>? hasPendingReconcileWork = null,
        Func<float, bool>? processPendingReconcileStep = null,
        Action? beforeClientManifestChanged = null,
        Action? onClientAuthorityCutover = null,
        DomainTransportHooks? hooks = null)
        : this(
            CreateDescriptor(
                domainKey,
                reloadDomain,
                manifestSettingKey,
                manifestPriority,
                shouldReloadForPath,
                reload,
                onGameDataReady,
                handleExpandWorldDataReady,
                hasPendingSnapshotBuildWork,
                processPendingSnapshotBuildStep,
                hasPendingReconcileWork,
                processPendingReconcileStep,
                beforeClientManifestChanged,
                onClientAuthorityCutover),
            dtoVersion,
            transportProfile,
            displayName,
            cacheDirectoryName,
            clientRequestPriority,
            keySelector,
            applyPayloadAction,
            workKinds,
            initializeRuntime,
            hooks)
    {
    }

    private DomainModuleDefinition(
        DomainDescriptor<TEntry> descriptor,
        int dtoVersion,
        DomainTransportProfile transportProfile,
        string displayName,
        string cacheDirectoryName,
        int clientRequestPriority,
        Func<TEntry, string> keySelector,
        Action applyPayloadAction,
        DomainWorkKinds workKinds,
        Action initializeRuntime,
        DomainTransportHooks? hooks)
        : base(
            descriptor,
            new DomainTransportMetadata<TEntry>(
                descriptor,
                dtoVersion,
                transportProfile,
                displayName,
                cacheDirectoryName,
                clientRequestPriority,
                keySelector,
                applyPayloadAction,
                hooks),
            workKinds,
            initializeRuntime)
    {
    }

    private static DomainDescriptor<TEntry> CreateDescriptor(
        string domainKey,
        DropNSpawnPlugin.ReloadDomain reloadDomain,
        string manifestSettingKey,
        int manifestPriority,
        Func<string, bool> shouldReloadForPath,
        Action reload,
        Action<string> onGameDataReady,
        Func<bool> handleExpandWorldDataReady,
        Func<bool>? hasPendingSnapshotBuildWork,
        Func<float, bool>? processPendingSnapshotBuildStep,
        Func<bool>? hasPendingReconcileWork,
        Func<float, bool>? processPendingReconcileStep,
        Action? beforeClientManifestChanged,
        Action? onClientAuthorityCutover)
    {
        return new DomainDescriptor<TEntry>(
            domainKey,
            reloadDomain,
            manifestSettingKey,
            manifestPriority,
            shouldReloadForPath,
            reload,
            onGameDataReady,
            handleExpandWorldDataReady,
            hasPendingSnapshotBuildWork,
            processPendingSnapshotBuildStep,
            hasPendingReconcileWork,
            processPendingReconcileStep,
            beforeClientManifestChanged,
            onClientAuthorityCutover);
    }
}
