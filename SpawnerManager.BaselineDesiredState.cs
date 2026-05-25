using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private sealed class SpawnerApplyOperations : StandardBaselineDesiredStateOperations<SpawnerDesiredState>
    {
        public static SpawnerApplyOperations Instance { get; } = new();

        public override string DomainKey => "spawner";

        public override BaselineDesiredStateCapabilities Capabilities =>
            BaselineDesiredStateCapabilities.Validation |
            BaselineDesiredStateCapabilities.LiveBaseline |
            BaselineDesiredStateCapabilities.LiveApply;

        public override void Validate(SpawnerDesiredState desiredState) => ValidateSpawnerDesiredState(desiredState);
        public override void PrepareLiveBaseline(SpawnerDesiredState desiredState) => PrepareSpawnerLiveBaseline(desiredState);
        public override void ApplyDesiredStateToLive(SpawnerDesiredState desiredState) => ApplySpawnerDesiredStateToLive(desiredState);
        public override void Commit(SpawnerDesiredState desiredState) => RecordAppliedState(desiredState.GameDataSignature, desiredState.DomainEnabled, desiredState.CurrentEntrySignatures);

        public override void HandleFailure(SpawnerDesiredState desiredState, StandardApplyFailureContext failureContext)
        {
            if (!failureContext.LiveStageFailed || desiredState.ReloadPrefabs.Count == 0)
            {
                return;
            }

            ReapplyOrQueueRegisteredLiveObjects(desiredState.DomainEnabled, desiredState.ReloadPrefabs);
        }
    }

    private sealed class SpawnerDesiredState
    {
        public StandardDomainApplyPlan ApplyPlan { get; set; }
        public int GameDataSignature { get; set; }
        public Dictionary<string, string> CurrentEntrySignatures { get; set; } = EmptyEntrySignatures;
        public bool DomainEnabled { get; set; }
        public bool QueueLiveReconcile { get; set; }
        public HashSet<string> AvailablePrefabs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReloadPrefabs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public SpawnerRuntimeConfigurationSnapshot RuntimeConfigurationSnapshot { get; set; } = SpawnerRuntimeConfigurationSnapshot.Empty;
    }

    private static void RunApplyCoordinator(
        HashSet<string> availablePrefabs,
        int gameDataSignature,
        bool domainEnabled,
        Dictionary<string, string> currentEntrySignatures,
        bool queueLiveReconcile)
    {
        SpawnerDesiredState desiredState = BuildSpawnerDesiredState(availablePrefabs, gameDataSignature, domainEnabled, currentEntrySignatures, queueLiveReconcile);
        StandardApplyOutcome outcome = StandardBaselineDesiredStateCoordinator.Run(
            desiredState.ApplyPlan,
            desiredState,
            SpawnerApplyOperations.Instance);
        if (!outcome.Success)
        {
            return;
        }
    }

    private static SpawnerDesiredState BuildSpawnerDesiredState(
        HashSet<string> availablePrefabs,
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
            canUseTargetedLiveReload: _lastAppliedGameDataSignature == gameDataSignature &&
                                      _lastAppliedDomainEnabled == true);
        return new SpawnerDesiredState
        {
            GameDataSignature = gameDataSignature,
            ApplyPlan = applyPlan,
            CurrentEntrySignatures = currentEntrySignatures,
            DomainEnabled = domainEnabled,
            QueueLiveReconcile = queueLiveReconcile,
            AvailablePrefabs = availablePrefabs,
            ReloadPrefabs = applyPlan.DirtyKeys ?? BuildRegisteredCatchupPrefabs(domainEnabled, currentEntrySignatures),
            RuntimeConfigurationSnapshot = GetRuntimeConfigurationSnapshot()
        };
    }

    private static void PrepareSpawnerLiveBaseline(SpawnerDesiredState desiredState)
    {
        ClearRuntimeReconcileState();
    }

    private static void ValidateSpawnerDesiredState(SpawnerDesiredState desiredState)
    {
        ValidateConfiguredPrefabs(desiredState.AvailablePrefabs);
    }

    private static void ApplySpawnerDesiredStateToLive(SpawnerDesiredState desiredState)
    {
        ApplyDesiredStateToLiveObjects(desiredState);
    }
}
