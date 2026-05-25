using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private sealed class ObjectApplyOperations : StandardBaselineDesiredStateOperations<ObjectDesiredState>
    {
        public static ObjectApplyOperations Instance { get; } = new();

        public override string DomainKey => "object";

        public override BaselineDesiredStateCapabilities Capabilities =>
            BaselineDesiredStateCapabilities.Validation |
            BaselineDesiredStateCapabilities.StaticBaseline |
            BaselineDesiredStateCapabilities.StaticApply |
            BaselineDesiredStateCapabilities.LiveApply |
            BaselineDesiredStateCapabilities.StaticRollback;

        public override void Validate(ObjectDesiredState desiredState) => ValidateObjectDesiredState(desiredState);
        public override void RestoreStaticBaseline(ObjectDesiredState desiredState) => RestoreObjectStaticBaseline(desiredState);
        public override void ApplyDesiredStateToStaticBaseline(ObjectDesiredState desiredState) => ApplyObjectDesiredStateToStaticBaseline(desiredState);
        public override void ApplyDesiredStateToLive(ObjectDesiredState desiredState) => ApplyObjectDesiredStateToLive(desiredState);
        public override void Commit(ObjectDesiredState desiredState) => RecordAppliedState(desiredState.GameDataSignature, desiredState.DomainEnabled, desiredState.CurrentEntrySignatures);

        public override void HandleFailure(ObjectDesiredState desiredState, StandardApplyFailureContext failureContext)
        {
            if (!failureContext.LiveStageFailed)
            {
                return;
            }

            QueueReapplyActiveEntriesToLiveObjects(desiredState.LiveDirtyPrefabs);
        }
    }

    private sealed class ObjectDesiredState
    {
        public StandardDomainApplyPlan ApplyPlan { get; set; }
        public int GameDataSignature { get; set; }
        public Dictionary<string, string> CurrentEntrySignatures { get; set; } = EmptyEntrySignatures;
        public ObjectRuntimeDropConfigurationState RuntimeConfigurationState { get; set; } = ObjectRuntimeDropConfigurationState.Empty;
        public bool DomainEnabled { get; set; }
        public bool QueueLiveReconcile { get; set; }
        public HashSet<string>? DirtyPrefabs { get; set; }
        public HashSet<string>? LiveDirtyPrefabs { get; set; }
        public bool NeedsLiveReload { get; set; }
    }

    private static void RunApplyCoordinator(
        int gameDataSignature,
        bool domainEnabled,
        Dictionary<string, string> currentEntrySignatures,
        bool queueLiveReconcile)
    {
        ObjectDesiredState desiredState = BuildObjectDesiredState(gameDataSignature, domainEnabled, currentEntrySignatures, queueLiveReconcile);
        StandardApplyOutcome outcome = StandardBaselineDesiredStateCoordinator.Run(
            desiredState.ApplyPlan,
            desiredState,
            ObjectApplyOperations.Instance);
        if (!outcome.Success)
        {
            return;
        }
    }

    private static ObjectDesiredState BuildObjectDesiredState(
        int gameDataSignature,
        bool domainEnabled,
        Dictionary<string, string> currentEntrySignatures,
        bool queueLiveReconcile)
    {
        StandardDomainApplyPlan applyPlan = StandardDomainApplySupport.BuildPlan(
            _lastAppliedGameDataSignature,
            gameDataSignature,
            _lastAppliedDomainEnabled,
            domainEnabled,
            _lastAppliedEntrySignaturesByPrefab,
            currentEntrySignatures,
            EmptyEntrySignatures,
            canUseTargetedLiveReload: true);
        HashSet<string>? dirtyPrefabs = applyPlan.DirtyKeys;
        HashSet<string>? liveDirtyPrefabs = dirtyPrefabs != null
            ? FilterPrefabsRequiringLiveReconcile(dirtyPrefabs)
            : null;

        EnsureRuntimeDropConfigurationState();
        return new ObjectDesiredState
        {
            GameDataSignature = gameDataSignature,
            ApplyPlan = applyPlan,
            CurrentEntrySignatures = currentEntrySignatures,
            RuntimeConfigurationState = _runtimeDropConfigurationState,
            DomainEnabled = domainEnabled,
            QueueLiveReconcile = queueLiveReconcile,
            DirtyPrefabs = dirtyPrefabs,
            LiveDirtyPrefabs = liveDirtyPrefabs,
            NeedsLiveReload = (applyPlan.PreviousDomainEnabled &&
                               FilterPrefabsRequiringLiveReconcile(_lastAppliedEntrySignaturesByPrefab.Keys).Count > 0) ||
                              (domainEnabled &&
                               FilterPrefabsRequiringLiveReconcile(currentEntrySignatures.Keys).Count > 0)
        };
    }

    private static void RestoreObjectStaticBaseline(ObjectDesiredState desiredState)
    {
        RestoreSnapshots(desiredState.DirtyPrefabs);
    }

    private static void ValidateObjectDesiredState(ObjectDesiredState desiredState)
    {
        ValidateConfiguredPrefabs();
    }

    private static void ApplyObjectDesiredStateToStaticBaseline(ObjectDesiredState desiredState)
    {
        ApplyDesiredStateToPrefabs(desiredState);
    }

    private static void ApplyObjectDesiredStateToLive(ObjectDesiredState desiredState)
    {
        ApplyDesiredStateToLiveObjects(desiredState);
    }
}
