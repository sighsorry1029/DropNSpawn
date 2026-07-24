using System;

namespace DropNSpawn;

internal abstract class DomainRegistration
{
    protected DomainRegistration(
        DomainDescriptor descriptor,
        DomainTransportMetadata transportMetadata,
        Action initializeRuntime)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        TransportMetadata = transportMetadata ?? throw new ArgumentNullException(nameof(transportMetadata));
        InitializeRuntime = initializeRuntime ?? throw new ArgumentNullException(nameof(initializeRuntime));
    }

    internal DomainDescriptor Descriptor { get; }
    internal DomainTransportMetadata TransportMetadata { get; }
    internal Action InitializeRuntime { get; }
}

internal sealed class DomainModuleOptions<TEntry>
{
    internal string DomainKey { get; set; } = "";
    internal DropNSpawnPlugin.ReloadDomain ReloadDomain { get; set; }
    internal string ManifestSettingKey { get; set; } = "";
    internal int ManifestPriority { get; set; }
    internal Func<string, bool>? ShouldReloadForPath { get; set; }
    internal Action? Reload { get; set; }
    internal Action? InitializeRuntime { get; set; }
    internal Action<string>? OnGameDataReady { get; set; }
    internal Func<bool>? HandleExpandWorldDataReady { get; set; }
    internal int DtoVersion { get; set; }
    internal DomainTransportProfile TransportProfile { get; set; }
    internal string DisplayName { get; set; } = "";
    internal string CacheDirectoryName { get; set; } = "";
    internal int ClientRequestPriority { get; set; }
    internal Func<TEntry, string>? KeySelector { get; set; }
    internal Action? ApplyPayloadAction { get; set; }
    internal Func<bool>? HasPendingSnapshotBuildWork { get; set; }
    internal Func<float, bool>? ProcessPendingSnapshotBuildStep { get; set; }
    internal Func<bool>? HasPendingReconcileWork { get; set; }
    internal Func<float, bool>? ProcessPendingReconcileStep { get; set; }
    internal Action? BeforeClientManifestChanged { get; set; }
    internal Action? OnClientAuthorityCutover { get; set; }
}

/// <summary>
/// Immutable domain module definition used to register descriptor, transport intent, and runtime work capabilities.
/// It does not own load state or compiled/live domain state.
/// </summary>
internal sealed class DomainModuleDefinition<TEntry> : DomainRegistration
{
    internal DomainModuleDefinition(DomainModuleOptions<TEntry> options)
        : this(
            CreateDescriptor(
                options.DomainKey,
                options.ReloadDomain,
                options.ManifestSettingKey,
                options.ManifestPriority,
                Require(options.ShouldReloadForPath, nameof(options.ShouldReloadForPath)),
                Require(options.Reload, nameof(options.Reload)),
                Require(options.OnGameDataReady, nameof(options.OnGameDataReady)),
                Require(options.HandleExpandWorldDataReady, nameof(options.HandleExpandWorldDataReady)),
                options.HasPendingSnapshotBuildWork,
                options.ProcessPendingSnapshotBuildStep,
                options.HasPendingReconcileWork,
                options.ProcessPendingReconcileStep,
                options.BeforeClientManifestChanged,
                options.OnClientAuthorityCutover),
            options.DtoVersion,
            options.TransportProfile,
            options.DisplayName,
            options.CacheDirectoryName,
            options.ClientRequestPriority,
            Require(options.KeySelector, nameof(options.KeySelector)),
            Require(options.ApplyPayloadAction, nameof(options.ApplyPayloadAction)),
            Require(options.InitializeRuntime, nameof(options.InitializeRuntime)))
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
        Action initializeRuntime)
        : base(
            descriptor,
            new DomainTransportMetadata<TEntry>(
                descriptor.DomainKey,
                dtoVersion,
                transportProfile,
                displayName,
                cacheDirectoryName,
                clientRequestPriority,
                keySelector,
                applyPayloadAction),
            initializeRuntime)
    {
        DescriptorTyped = descriptor;
        TransportMetadataTyped = (DomainTransportMetadata<TEntry>)TransportMetadata;
    }

    internal DomainDescriptor<TEntry> DescriptorTyped { get; }
    internal DomainTransportMetadata<TEntry> TransportMetadataTyped { get; }

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

    private static T Require<T>(T? value, string name) where T : class
    {
        return value ?? throw new ArgumentNullException(name);
    }
}
