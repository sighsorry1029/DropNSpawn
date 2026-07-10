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
    internal DomainWorkKinds WorkKinds { get; set; }
    internal Func<bool>? HasPendingSnapshotBuildWork { get; set; }
    internal Func<int>? GetPendingSnapshotBuildWorkCount { get; set; }
    internal Func<float, bool>? ProcessPendingSnapshotBuildStep { get; set; }
    internal Func<bool>? HasPendingReconcileWork { get; set; }
    internal Func<int>? GetPendingReconcileWorkCount { get; set; }
    internal Func<float, bool>? ProcessPendingReconcileStep { get; set; }
    internal Action? BeforeClientManifestChanged { get; set; }
    internal Action? OnClientAuthorityCutover { get; set; }
}

/// <summary>
/// Immutable domain module definition used to register descriptor, transport intent, and runtime work capabilities.
/// It does not own load state or compiled/live domain state.
/// </summary>
internal sealed class DomainModuleDefinition<TEntry> : DomainRegistration<TEntry>
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
                options.GetPendingSnapshotBuildWorkCount,
                options.ProcessPendingSnapshotBuildStep,
                options.HasPendingReconcileWork,
                options.GetPendingReconcileWorkCount,
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
            options.WorkKinds,
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
        DomainWorkKinds workKinds,
        Action initializeRuntime)
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
                applyPayloadAction),
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
        Func<int>? getPendingSnapshotBuildWorkCount,
        Func<float, bool>? processPendingSnapshotBuildStep,
        Func<bool>? hasPendingReconcileWork,
        Func<int>? getPendingReconcileWorkCount,
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
            getPendingSnapshotBuildWorkCount,
            processPendingSnapshotBuildStep,
            hasPendingReconcileWork,
            getPendingReconcileWorkCount,
            processPendingReconcileStep,
            beforeClientManifestChanged,
            onClientAuthorityCutover);
    }

    private static T Require<T>(T? value, string name) where T : class
    {
        return value ?? throw new ArgumentNullException(name);
    }
}
