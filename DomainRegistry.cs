using System;
using System.Linq;

namespace DropNSpawn;

internal abstract class DomainDescriptor
{
    protected DomainDescriptor(
        string domainKey,
        DropNSpawnPlugin.ReloadDomain reloadDomain,
        string manifestSettingKey,
        int manifestPriority,
        Func<string, bool> shouldReloadForPath,
        Action reload,
        Action<string> onGameDataReady,
        Func<bool> handleExpandWorldDataReady,
        Func<bool>? hasPendingSnapshotBuildWork = null,
        Func<float, bool>? processPendingSnapshotBuildStep = null,
        Func<bool>? hasPendingReconcileWork = null,
        Func<float, bool>? processPendingReconcileStep = null,
        Action? beforeClientManifestChanged = null,
        Action? onClientAuthorityCutover = null)
    {
        DomainKey = domainKey ?? "";
        ReloadDomain = reloadDomain;
        ManifestSettingKey = manifestSettingKey ?? "";
        ManifestPriority = manifestPriority;
        ShouldReloadForPath = shouldReloadForPath;
        Reload = reload;
        OnGameDataReady = onGameDataReady;
        HandleExpandWorldDataReady = handleExpandWorldDataReady;
        HasPendingSnapshotBuildWork = hasPendingSnapshotBuildWork;
        ProcessPendingSnapshotBuildStep = processPendingSnapshotBuildStep;
        HasPendingReconcileWork = hasPendingReconcileWork;
        ProcessPendingReconcileStep = processPendingReconcileStep;
        BeforeClientManifestChanged = beforeClientManifestChanged;
        OnClientAuthorityCutover = onClientAuthorityCutover;
    }

    internal string DomainKey { get; }
    internal DropNSpawnPlugin.ReloadDomain ReloadDomain { get; }
    internal string ManifestSettingKey { get; }
    internal int ManifestPriority { get; }
    internal Func<string, bool> ShouldReloadForPath { get; }
    internal Action Reload { get; }
    internal Action<string> OnGameDataReady { get; }
    internal Func<bool> HandleExpandWorldDataReady { get; }
    internal Func<bool>? HasPendingSnapshotBuildWork { get; }
    internal Func<float, bool>? ProcessPendingSnapshotBuildStep { get; }
    internal Func<bool>? HasPendingReconcileWork { get; }
    internal Func<float, bool>? ProcessPendingReconcileStep { get; }
    internal Action? BeforeClientManifestChanged { get; }
    internal Action? OnClientAuthorityCutover { get; }

    internal abstract void HandleManifestChanged(string manifestRaw);
}

internal sealed class DomainDescriptor<TEntry> : DomainDescriptor
{
    internal DomainDescriptor(
        string domainKey,
        DropNSpawnPlugin.ReloadDomain reloadDomain,
        string manifestSettingKey,
        int manifestPriority,
        Func<string, bool> shouldReloadForPath,
        Action reload,
        Action<string> onGameDataReady,
        Func<bool> handleExpandWorldDataReady,
        Func<bool>? hasPendingSnapshotBuildWork = null,
        Func<float, bool>? processPendingSnapshotBuildStep = null,
        Func<bool>? hasPendingReconcileWork = null,
        Func<float, bool>? processPendingReconcileStep = null,
        Action? beforeClientManifestChanged = null,
        Action? onClientAuthorityCutover = null)
        : base(
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
            onClientAuthorityCutover)
    {
    }

    internal override void HandleManifestChanged(string manifestRaw)
    {
        NetworkPayloadSyncSupport.HandleManifestChanged(this, manifestRaw);
    }
}

internal static class DomainRegistry
{
    internal static readonly DomainRegistration[] AllRegistrations =
    {
        ObjectDropManager.Module,
        CharacterDropManager.Module,
        SpawnerManager.Module,
        SpawnSystemManager.Module,
        EventManager.Module
    };

    internal static readonly DomainDescriptor[] RuntimeDomains =
        AllRegistrations
            .Select(static registration => registration.Descriptor)
            .ToArray();

    internal static readonly DomainDescriptor[] SnapshotBuildDomains =
        RuntimeDomains
            .Where(HasSnapshotBuildHooks)
            .ToArray();

    internal static readonly DomainDescriptor[] ReconcileDomains =
        RuntimeDomains
            .Where(HasReconcileHooks)
            .ToArray();

    internal static readonly DomainTransportMetadata[] Transports =
        AllRegistrations
            .Select(static registration => registration.TransportMetadata)
            .ToArray();

    internal static void InitializeRuntimeDomains()
    {
        foreach (DomainRegistration registration in AllRegistrations)
        {
            registration.InitializeRuntime();
        }
    }

    private static bool HasSnapshotBuildHooks(DomainDescriptor domain)
    {
        return ValidateWorkHookPair(
            domain,
            "snapshot-build",
            domain.HasPendingSnapshotBuildWork != null,
            domain.ProcessPendingSnapshotBuildStep != null);
    }

    private static bool HasReconcileHooks(DomainDescriptor domain)
    {
        return ValidateWorkHookPair(
            domain,
            "reconcile",
            domain.HasPendingReconcileWork != null,
            domain.ProcessPendingReconcileStep != null);
    }

    private static bool ValidateWorkHookPair(
        DomainDescriptor domain,
        string workKind,
        bool hasPendingHook,
        bool hasProcessHook)
    {
        if (hasPendingHook != hasProcessHook)
        {
            throw new InvalidOperationException(
                $"Domain '{domain.DomainKey}' has an incomplete {workKind} coordinator hook pair.");
        }

        return hasPendingHook;
    }
}
