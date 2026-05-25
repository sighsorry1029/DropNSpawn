using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal readonly struct StandardDomainApplyPlan
{
    internal StandardDomainApplyPlan(
        bool sameGameData,
        bool previousDomainEnabled,
        bool shouldSkipLiveReload,
        bool needsLiveReload,
        HashSet<string>? dirtyKeys)
    {
        SameGameData = sameGameData;
        PreviousDomainEnabled = previousDomainEnabled;
        ShouldSkipLiveReload = shouldSkipLiveReload;
        NeedsLiveReload = needsLiveReload;
        DirtyKeys = dirtyKeys;
    }

    internal bool SameGameData { get; }
    internal bool PreviousDomainEnabled { get; }
    internal bool ShouldSkipLiveReload { get; }
    internal bool NeedsLiveReload { get; }
    internal HashSet<string>? DirtyKeys { get; }
}

internal static class StandardDomainApplySupport
{
    internal static bool CanApplySynchronizedDomain(bool synchronizedPayloadReady)
    {
        return DropNSpawnPlugin.IsSourceOfTruth || synchronizedPayloadReady;
    }

    internal static bool IsAlreadyApplied(
        int? lastAppliedGameDataSignature,
        int currentGameDataSignature,
        bool? lastAppliedDomainEnabled,
        bool currentDomainEnabled,
        string lastAppliedConfigurationSignature,
        string currentConfigurationSignature)
    {
        return lastAppliedGameDataSignature == currentGameDataSignature &&
               lastAppliedDomainEnabled.HasValue &&
               lastAppliedDomainEnabled.Value == currentDomainEnabled &&
               string.Equals(lastAppliedConfigurationSignature, currentConfigurationSignature, StringComparison.Ordinal);
    }

    internal static bool IsAlreadyApplied(
        int? lastAppliedGameDataSignature,
        int currentGameDataSignature,
        bool? lastAppliedDomainEnabled,
        bool currentDomainEnabled,
        string lastAppliedConfigurationSignature,
        string currentConfigurationSignature,
        bool lastAppliedSynchronizedPayloadReady,
        bool currentSynchronizedPayloadReady)
    {
        return IsAlreadyApplied(
                   lastAppliedGameDataSignature,
                   currentGameDataSignature,
                   lastAppliedDomainEnabled,
                   currentDomainEnabled,
                   lastAppliedConfigurationSignature,
                   currentConfigurationSignature) &&
               lastAppliedSynchronizedPayloadReady == currentSynchronizedPayloadReady;
    }

    internal static StandardDomainApplyPlan BuildPlan(
        int? lastAppliedGameDataSignature,
        int currentGameDataSignature,
        bool? lastAppliedDomainEnabled,
        bool currentDomainEnabled,
        IReadOnlyDictionary<string, string> lastAppliedEntrySignatures,
        IReadOnlyDictionary<string, string> currentEntrySignatures,
        IReadOnlyDictionary<string, string> emptyEntrySignatures,
        bool canUseTargetedLiveReload)
    {
        bool sameGameData = lastAppliedGameDataSignature == currentGameDataSignature;
        bool previousDomainEnabled = lastAppliedDomainEnabled == true;
        IReadOnlyDictionary<string, string> previousEntrySignatures = previousDomainEnabled
            ? lastAppliedEntrySignatures
            : emptyEntrySignatures;
        IReadOnlyDictionary<string, string> targetEntrySignatures = currentDomainEnabled
            ? currentEntrySignatures
            : emptyEntrySignatures;

        HashSet<string>? dirtyKeys = sameGameData && canUseTargetedLiveReload
            ? DomainDictionaryDiffSupport.BuildDirtyKeys(previousEntrySignatures, targetEntrySignatures)
            : null;

        return new StandardDomainApplyPlan(
            sameGameData,
            previousDomainEnabled,
            sameGameData && lastAppliedDomainEnabled == false && !currentDomainEnabled,
            previousDomainEnabled || targetEntrySignatures.Count > 0,
            dirtyKeys);
    }
}
