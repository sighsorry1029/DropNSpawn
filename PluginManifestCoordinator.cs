using System;
using System.Collections.Generic;
using ServerSync;

namespace DropNSpawn;

/// <summary>
/// Owns synced manifest entries and manifest change handler registration for runtime domains.
/// Payload transfer details stay inside the transport layer.
/// </summary>
internal static class PluginManifestCoordinator
{
    private static readonly Dictionary<DropNSpawnPlugin.ReloadDomain, CustomSyncedValue<string>> SyncedManifests = new();
    private static readonly Dictionary<DropNSpawnPlugin.ReloadDomain, Action> SyncedManifestChangedHandlers = new();

    internal static void Initialize(ConfigSync configSync)
    {
        SyncedManifests.Clear();
        SyncedManifestChangedHandlers.Clear();
        foreach (DomainDescriptor domain in DomainRegistry.RuntimeDomains)
        {
            SyncedManifests[domain.ReloadDomain] =
                new CustomSyncedValue<string>(configSync, domain.ManifestSettingKey, "", domain.ManifestPriority);
            DomainDescriptor capturedDomain = domain;
            SyncedManifestChangedHandlers[domain.ReloadDomain] = () => HandleSyncedManifestChanged(capturedDomain);
        }
    }

    internal static void AttachRuntimeDomainHandlers()
    {
        foreach (DomainDescriptor domain in DomainRegistry.RuntimeDomains)
        {
            GetSyncedManifestEntry(domain).ValueChanged += GetSyncedManifestChangedHandler(domain);
        }
    }

    internal static void DetachRuntimeDomainHandlers()
    {
        foreach (DomainDescriptor domain in DomainRegistry.RuntimeDomains)
        {
            if (!SyncedManifests.TryGetValue(domain.ReloadDomain, out CustomSyncedValue<string>? syncedManifest) ||
                !SyncedManifestChangedHandlers.TryGetValue(domain.ReloadDomain, out Action? handler))
            {
                continue;
            }

            syncedManifest.ValueChanged -= handler;
        }
    }

    internal static string GetSyncedManifestValue(DomainDescriptor domain)
    {
        return GetSyncedManifestEntry(domain).Value ?? "";
    }

    internal static bool TryGetSyncedEntries<TEntry>(
        DomainDescriptor<TEntry> domain,
        out List<TEntry> entries,
        out string payloadToken)
    {
        return NetworkPayloadSyncSupport.TryGetEntries(domain, GetSyncedManifestValue(domain), out entries, out payloadToken);
    }

    internal static void PublishSyncedPayload<TEntry>(
        DomainDescriptor<TEntry> domain,
        List<TEntry> entries,
        string? knownSignature)
    {
        NetworkPayloadSyncSupport.PublishPayloadAsync(
            domain,
            entries,
            knownSignature,
            manifest => AssignServerManifestValue(GetSyncedManifestEntry(domain), manifest, broadcastToConnectedClients: true));
    }

    internal static void EnterClientAuthorityCutover()
    {
        foreach (DomainDescriptor domain in DomainRegistry.RuntimeDomains)
        {
            domain.OnClientAuthorityCutover?.Invoke();
        }
    }

    internal static void ReplayCurrentSyncedManifestStates()
    {
        if (DropNSpawnPlugin.IsSourceOfTruth)
        {
            return;
        }

        foreach (DomainDescriptor domain in DomainRegistry.RuntimeDomains)
        {
            HandleSyncedManifestChanged(domain);
        }
    }

    private static CustomSyncedValue<string> GetSyncedManifestEntry(DomainDescriptor domain)
    {
        if (SyncedManifests.TryGetValue(domain.ReloadDomain, out CustomSyncedValue<string>? syncedManifest))
        {
            return syncedManifest;
        }

        throw new InvalidOperationException($"ServerSync {domain.DomainKey} payload has not been initialized yet.");
    }

    private static Action GetSyncedManifestChangedHandler(DomainDescriptor domain)
    {
        if (SyncedManifestChangedHandlers.TryGetValue(domain.ReloadDomain, out Action? handler))
        {
            return handler;
        }

        throw new InvalidOperationException($"ServerSync {domain.DomainKey} manifest handler has not been initialized yet.");
    }

    private static void HandleSyncedManifestChanged(DomainDescriptor domain)
    {
        if (DropNSpawnPlugin.IsSourceOfTruth)
        {
            return;
        }

        domain.BeforeClientManifestChanged?.Invoke();
        domain.HandleManifestChanged(GetSyncedManifestValue(domain));
    }

    private static void AssignServerManifestValue(
        CustomSyncedValue<string> syncedValue,
        string manifest,
        bool broadcastToConnectedClients)
    {
        manifest ??= "";
        if (string.Equals(syncedValue.Value ?? "", manifest, StringComparison.Ordinal))
        {
            return;
        }

        if (broadcastToConnectedClients || !DropNSpawnPlugin.IsSourceOfTruth)
        {
            syncedValue.AssignLocalValue(manifest);
            return;
        }

        bool originalProcessingServerUpdate = ConfigSync.ProcessingServerUpdate;
        ConfigSync.ProcessingServerUpdate = true;
        try
        {
            syncedValue.AssignLocalValue(manifest);
        }
        finally
        {
            ConfigSync.ProcessingServerUpdate = originalProcessingServerUpdate;
        }
    }
}
