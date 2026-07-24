using System;
using System.Diagnostics;

namespace DropNSpawn;

internal enum StandardApplyStage
{
    None = 0,
    Validate = 1,
    RestoreStaticBaseline = 2,
    ApplyStaticBaseline = 3,
    PrepareLiveBaseline = 4,
    ApplyLive = 5,
    Commit = 6
}

[Flags]
internal enum BaselineDesiredStateCapabilities
{
    None = 0,
    Validation = 1 << 0,
    StaticBaseline = 1 << 1,
    StaticApply = 1 << 2,
    LiveBaseline = 1 << 3,
    LiveApply = 1 << 4,
    StaticRollback = 1 << 5
}

internal abstract class StandardBaselineDesiredStateOperations<TDesiredState>
{
    public abstract string DomainKey { get; }
    public abstract BaselineDesiredStateCapabilities Capabilities { get; }
    public virtual void Validate(TDesiredState desiredState) { }
    public virtual void RestoreStaticBaseline(TDesiredState desiredState) { }
    public virtual void ApplyDesiredStateToStaticBaseline(TDesiredState desiredState) { }
    public virtual void PrepareLiveBaseline(TDesiredState desiredState) { }
    public virtual void ApplyDesiredStateToLive(TDesiredState desiredState) { }
    public virtual void Commit(TDesiredState desiredState) { }
    public virtual void HandleFailure(TDesiredState desiredState, bool liveStageFailed) { }
}

internal static class StandardBaselineDesiredStateCoordinator
{
    internal static void Run<TDesiredState>(
        StandardDomainApplyPlan applyPlan,
        TDesiredState desiredState,
        StandardBaselineDesiredStateOperations<TDesiredState> operations)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        BaselineDesiredStateCapabilities capabilities = operations.Capabilities;
        StandardApplyStage currentStage = StandardApplyStage.None;

        try
        {
            if ((capabilities & BaselineDesiredStateCapabilities.Validation) != 0)
            {
                currentStage = StandardApplyStage.Validate;
                operations.Validate(desiredState);
            }

            if ((capabilities & BaselineDesiredStateCapabilities.StaticBaseline) != 0)
            {
                currentStage = StandardApplyStage.RestoreStaticBaseline;
                operations.RestoreStaticBaseline(desiredState);
            }

            if ((capabilities & BaselineDesiredStateCapabilities.StaticApply) != 0)
            {
                currentStage = StandardApplyStage.ApplyStaticBaseline;
                operations.ApplyDesiredStateToStaticBaseline(desiredState);
            }

            if (!applyPlan.ShouldSkipLiveReload && applyPlan.NeedsLiveReload)
            {
                if ((capabilities & BaselineDesiredStateCapabilities.LiveBaseline) != 0)
                {
                    currentStage = StandardApplyStage.PrepareLiveBaseline;
                    operations.PrepareLiveBaseline(desiredState);
                }

                if ((capabilities & BaselineDesiredStateCapabilities.LiveApply) != 0)
                {
                    currentStage = StandardApplyStage.ApplyLive;
                    operations.ApplyDesiredStateToLive(desiredState);
                }
            }

            currentStage = StandardApplyStage.Commit;
            operations.Commit(desiredState);
            stopwatch.Stop();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            StandardApplyStage failedStage = currentStage;

            if (failedStage == StandardApplyStage.ApplyStaticBaseline &&
                (capabilities & BaselineDesiredStateCapabilities.StaticRollback) != 0 &&
                (capabilities & BaselineDesiredStateCapabilities.StaticBaseline) != 0)
            {
                try
                {
                    operations.RestoreStaticBaseline(desiredState);
                }
                catch (Exception rollbackEx)
                {
                    DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                        $"Static rollback failed for domain '{operations.DomainKey}' after apply failure. {rollbackEx.Message}");
                    DropNSpawnPlugin.DropNSpawnLogger.LogError(rollbackEx);
                }
            }

            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                $"Apply coordinator failed for domain '{operations.DomainKey}' at stage '{failedStage}' after {stopwatch.ElapsedMilliseconds} ms. {ex.Message}");
            DropNSpawnPlugin.DropNSpawnLogger.LogError(ex);

            try
            {
                operations.HandleFailure(
                    desiredState,
                    failedStage == StandardApplyStage.PrepareLiveBaseline ||
                    failedStage == StandardApplyStage.ApplyLive);
            }
            catch (Exception failureHandlerEx)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                    $"Failure handler failed for domain '{operations.DomainKey}'. {failureHandlerEx.Message}");
                DropNSpawnPlugin.DropNSpawnLogger.LogError(failureHandlerEx);
            }
        }
    }
}
