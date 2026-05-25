using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal enum DomainReloadOutcome
{
    NoChange,
    Loaded,
    Rejected,
    DeferredStrictValidation,
    WaitingForPayload,
    Failed
}

internal sealed class DomainLoadState
{
    internal string LastLoadedPayload { get; set; } = "";
    internal string LastRejectedPayload { get; set; } = "";
    internal string PendingStrictPayload { get; set; } = "";
    internal string LastRejectedValidationKey { get; set; } = "";
}

internal sealed class LocalLoadResult<TEntry>
{
    internal List<TEntry> Entries { get; set; } = new();
    internal List<string> Errors { get; set; } = new();
    internal List<string> Warnings { get; set; } = new();
    internal int ParsedEntryCount { get; set; }
    internal int LoadedFileCount { get; set; }
}

internal sealed class StrictValidationResult<TEntry>
{
    internal List<TEntry> Entries { get; set; } = new();
    internal List<string> Warnings { get; set; } = new();
}

internal delegate bool TryGetSyncedEntriesDelegate<TEntry>(out List<TEntry> entries, out string payloadToken);

internal sealed class DomainLoadHooks<TEntry, TState>
{
    internal DomainLoadHooks(
        Func<List<ConfigurationLoadSupport.LocalYamlDocument>, LocalLoadResult<TEntry>> parseLocalDocuments,
        Func<List<TEntry>, string, TState> buildSyncedState,
        Action<TState, string> commitState,
        Action<string, IEnumerable<string>> rejectLocalPayload,
        Func<TState, int> getAcceptedEntryCount,
        Action<int, int, IEnumerable<string>>? logPartiallyAcceptedLocalConfiguration = null,
        Action<int, int>? logLocalLoadSuccess = null,
        Action? onUnchangedPayload = null,
        Action? publishCommittedState = null,
        Func<List<TEntry>, bool>? canStrictValidateNow = null,
        Func<List<TEntry>, StrictValidationResult<TEntry>>? strictValidateLocal = null)
    {
        ParseLocalDocuments = parseLocalDocuments;
        BuildSyncedState = buildSyncedState;
        CommitState = commitState;
        RejectLocalPayload = rejectLocalPayload;
        GetAcceptedEntryCount = getAcceptedEntryCount;
        LogPartiallyAcceptedLocalConfiguration = logPartiallyAcceptedLocalConfiguration;
        LogLocalLoadSuccess = logLocalLoadSuccess;
        OnUnchangedPayload = onUnchangedPayload;
        PublishCommittedState = publishCommittedState;
        CanStrictValidateNow = canStrictValidateNow;
        StrictValidateLocal = strictValidateLocal;
    }

    internal Func<List<ConfigurationLoadSupport.LocalYamlDocument>, LocalLoadResult<TEntry>> ParseLocalDocuments { get; }
    internal Func<List<TEntry>, string, TState> BuildSyncedState { get; }
    internal Action<TState, string> CommitState { get; }
    internal Action<string, IEnumerable<string>> RejectLocalPayload { get; }
    internal Func<TState, int> GetAcceptedEntryCount { get; }
    internal Action<int, int, IEnumerable<string>>? LogPartiallyAcceptedLocalConfiguration { get; }
    internal Action<int, int>? LogLocalLoadSuccess { get; }
    internal Action? OnUnchangedPayload { get; }
    internal Action? PublishCommittedState { get; }
    internal Func<List<TEntry>, bool>? CanStrictValidateNow { get; }
    internal Func<List<TEntry>, StrictValidationResult<TEntry>>? StrictValidateLocal { get; }
}

internal sealed class DomainSyncHooks<TEntry, TState>
{
    internal DomainSyncHooks(
        TryGetSyncedEntriesDelegate<TEntry> tryGetSyncedEntries,
        Func<string, bool> shouldSkipPayload,
        Func<List<TEntry>, string, TState> buildSyncedState,
        Action<TState, string> commitState,
        Func<TState, int> getAcceptedEntryCount,
        string sourceName,
        Action? onWaitingForPayload = null,
        Action<string, int>? logSyncedLoadSuccess = null,
        Action<string, Exception>? logSyncedLoadFailure = null)
    {
        TryGetSyncedEntries = tryGetSyncedEntries;
        ShouldSkipPayload = shouldSkipPayload;
        BuildSyncedState = buildSyncedState;
        CommitState = commitState;
        GetAcceptedEntryCount = getAcceptedEntryCount;
        SourceName = sourceName;
        OnWaitingForPayload = onWaitingForPayload;
        LogSyncedLoadSuccess = logSyncedLoadSuccess;
        LogSyncedLoadFailure = logSyncedLoadFailure;
    }

    internal TryGetSyncedEntriesDelegate<TEntry> TryGetSyncedEntries { get; }
    internal Func<string, bool> ShouldSkipPayload { get; }
    internal Func<List<TEntry>, string, TState> BuildSyncedState { get; }
    internal Action<TState, string> CommitState { get; }
    internal Func<TState, int> GetAcceptedEntryCount { get; }
    internal string SourceName { get; }
    internal Action? OnWaitingForPayload { get; }
    internal Action<string, int>? LogSyncedLoadSuccess { get; }
    internal Action<string, Exception>? LogSyncedLoadFailure { get; }
}

internal sealed class DomainConfigurationRuntime<TEntry, TState>
{
    private readonly DomainLoadHooks<TEntry, TState> _loadHooks;
    private readonly DomainSyncHooks<TEntry, TState> _syncHooks;

    internal DomainConfigurationRuntime(
        DomainLoadHooks<TEntry, TState> loadHooks,
        DomainSyncHooks<TEntry, TState> syncHooks)
    {
        _loadHooks = loadHooks ?? throw new ArgumentNullException(nameof(loadHooks));
        _syncHooks = syncHooks ?? throw new ArgumentNullException(nameof(syncHooks));
    }

    /// <summary>
    /// Shared load-state and synced-payload lifecycle glue for a configuration domain.
    /// Domain-specific build, commit, and reconcile logic is supplied through hooks.
    /// </summary>
    internal DomainLoadState LoadState { get; } = new();

    internal DomainReloadOutcome ReloadSourceOfTruth(List<string> overridePaths)
    {
        return ConfigurationDomainHost.RunSourceOfTruthReload(LoadState, overridePaths, _loadHooks);
    }

    internal DomainReloadOutcome ReloadSynced()
    {
        return ConfigurationDomainHost.RunSyncedReload(_syncHooks);
    }

    internal void ResetLoadState()
    {
        ConfigurationDomainHost.ResetLoadState(LoadState);
    }

    internal void MarkSyncedPayloadPending(bool isSourceOfTruth, Action? onClientPending = null)
    {
        if (isSourceOfTruth)
        {
            return;
        }

        onClientPending?.Invoke();
    }

    internal void EnterPendingSyncedPayloadState(
        bool isSourceOfTruth,
        Action? beforeResetLoadState = null,
        Action? afterResetLoadState = null)
    {
        if (isSourceOfTruth)
        {
            return;
        }

        beforeResetLoadState?.Invoke();
        ResetLoadState();
        afterResetLoadState?.Invoke();
    }

    internal bool ApplySyncedPayload(Action? onLoaded = null)
    {
        if (ReloadSynced() != DomainReloadOutcome.Loaded)
        {
            return false;
        }

        onLoaded?.Invoke();
        return true;
    }
}

internal static class ConfigurationDomainHost
{
    internal static void PublishSyncedPayload<TEntry>(
        bool isSourceOfTruth,
        DomainDescriptor<TEntry> descriptor,
        List<TEntry> entries,
        string payloadSignature)
    {
        if (!isSourceOfTruth)
        {
            return;
        }

        DropNSpawnPlugin.PublishSyncedPayload(descriptor, entries, payloadSignature);
    }

    internal static bool TryGetSyncedEntries<TEntry>(
        DomainDescriptor<TEntry> descriptor,
        out List<TEntry> entries,
        out string payloadToken,
        Action? onPayloadAvailable = null)
    {
        bool hasPayload = DropNSpawnPlugin.TryGetSyncedEntries(descriptor, out entries, out payloadToken);
        if (hasPayload)
        {
            onPayloadAvailable?.Invoke();
        }

        return hasPayload;
    }

    internal static bool ShouldSkipSyncedPayload(DomainLoadState loadState, string payloadToken, bool isPayloadReady)
    {
        return isPayloadReady &&
               string.Equals(loadState.LastLoadedPayload, payloadToken, StringComparison.Ordinal);
    }

    internal static void HandleWaitingForSyncedPayload(
        Action markPending,
        string? debugMessage = null,
        Action? onWaiting = null)
    {
        markPending?.Invoke();
        onWaiting?.Invoke();
        if (!string.IsNullOrWhiteSpace(debugMessage))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogDebug(debugMessage);
        }
    }

    internal static void ResetLoadState(DomainLoadState loadState)
    {
        if (loadState == null)
        {
            return;
        }

        loadState.LastLoadedPayload = "";
        loadState.LastRejectedPayload = "";
        loadState.PendingStrictPayload = "";
        loadState.LastRejectedValidationKey = "";
    }

    internal static DomainReloadOutcome RunSourceOfTruthReload<TEntry, TState>(
        DomainLoadState loadState,
        List<string> overridePaths,
        DomainLoadHooks<TEntry, TState> hooks)
    {
        List<ConfigurationLoadSupport.LocalYamlDocument> documents =
            ConfigurationLoadSupport.ReadLocalYamlDocuments(overridePaths);
        string payload = ConfigurationLoadSupport.BuildLocalPayload(documents);
        bool hasPendingStrictValidation =
            loadState.PendingStrictPayload.Length > 0 &&
            string.Equals(loadState.PendingStrictPayload, payload, StringComparison.Ordinal);

        if (string.Equals(loadState.LastLoadedPayload, payload, StringComparison.Ordinal) &&
            !hasPendingStrictValidation)
        {
            loadState.LastRejectedPayload = "";
            loadState.PendingStrictPayload = "";
            hooks.OnUnchangedPayload?.Invoke();
            return DomainReloadOutcome.NoChange;
        }

        LocalLoadResult<TEntry> localLoadResult = hooks.ParseLocalDocuments(documents);
        if (localLoadResult.Errors.Count > 0)
        {
            loadState.PendingStrictPayload = "";
            hooks.RejectLocalPayload(payload, localLoadResult.Errors);
            return DomainReloadOutcome.Rejected;
        }

        List<TEntry> acceptedEntries = localLoadResult.Entries;
        List<string> warnings = localLoadResult.Warnings.Count > 0
            ? new List<string>(localLoadResult.Warnings)
            : new List<string>();

        if (hooks.CanStrictValidateNow != null && hooks.StrictValidateLocal != null)
        {
            if (!hooks.CanStrictValidateNow(acceptedEntries))
            {
                loadState.PendingStrictPayload = payload;
                return DomainReloadOutcome.DeferredStrictValidation;
            }

            loadState.PendingStrictPayload = "";
            StrictValidationResult<TEntry> strictValidationResult = hooks.StrictValidateLocal(acceptedEntries);
            acceptedEntries = strictValidationResult.Entries;
            if (strictValidationResult.Warnings.Count > 0)
            {
                warnings.AddRange(strictValidationResult.Warnings);
            }
        }

        TState state = hooks.BuildSyncedState(acceptedEntries, "");
        hooks.CommitState(state, payload);

        int acceptedEntryCount = hooks.GetAcceptedEntryCount(state);
        if (warnings.Count > 0)
        {
            hooks.LogPartiallyAcceptedLocalConfiguration?.Invoke(
                localLoadResult.ParsedEntryCount,
                acceptedEntryCount,
                warnings);
        }

        hooks.LogLocalLoadSuccess?.Invoke(acceptedEntryCount, localLoadResult.LoadedFileCount);
        hooks.PublishCommittedState?.Invoke();
        return DomainReloadOutcome.Loaded;
    }

    internal static DomainReloadOutcome RunSyncedReload<TEntry, TState>(DomainSyncHooks<TEntry, TState> hooks)
    {
        if (!hooks.TryGetSyncedEntries(out List<TEntry> entries, out string payloadToken))
        {
            hooks.OnWaitingForPayload?.Invoke();
            return DomainReloadOutcome.WaitingForPayload;
        }

        if (hooks.ShouldSkipPayload(payloadToken))
        {
            return DomainReloadOutcome.NoChange;
        }

        try
        {
            TState state = hooks.BuildSyncedState(entries, hooks.SourceName);
            hooks.CommitState(state, payloadToken);
            hooks.LogSyncedLoadSuccess?.Invoke(payloadToken, hooks.GetAcceptedEntryCount(state));
            return DomainReloadOutcome.Loaded;
        }
        catch (Exception ex)
        {
            hooks.LogSyncedLoadFailure?.Invoke(payloadToken, ex);
            return DomainReloadOutcome.Failed;
        }
    }
}
