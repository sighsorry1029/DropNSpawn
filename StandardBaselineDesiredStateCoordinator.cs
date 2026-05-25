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
    Commit = 6,
    RollbackStaticBaseline = 7
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

internal readonly struct StandardApplyFailureContext
{
    internal StandardApplyFailureContext(
        StandardApplyStage failedStage,
        Exception error,
        bool rollbackAttempted,
        bool rollbackSucceeded,
        bool liveStageFailed,
        long elapsedMilliseconds)
    {
        FailedStage = failedStage;
        Error = error;
        RollbackAttempted = rollbackAttempted;
        RollbackSucceeded = rollbackSucceeded;
        LiveStageFailed = liveStageFailed;
        ElapsedMilliseconds = elapsedMilliseconds;
    }

    internal StandardApplyStage FailedStage { get; }
    internal Exception Error { get; }
    internal bool RollbackAttempted { get; }
    internal bool RollbackSucceeded { get; }
    internal bool LiveStageFailed { get; }
    internal long ElapsedMilliseconds { get; }
}

internal readonly struct StandardApplyOutcome
{
    internal StandardApplyOutcome(
        bool success,
        StandardApplyStage completedStage,
        bool liveApplied,
        bool rollbackAttempted,
        bool rollbackSucceeded,
        long elapsedMilliseconds,
        Exception? error = null)
    {
        Success = success;
        CompletedStage = completedStage;
        LiveApplied = liveApplied;
        RollbackAttempted = rollbackAttempted;
        RollbackSucceeded = rollbackSucceeded;
        ElapsedMilliseconds = elapsedMilliseconds;
        Error = error;
    }

    internal bool Success { get; }
    internal StandardApplyStage CompletedStage { get; }
    internal bool LiveApplied { get; }
    internal bool RollbackAttempted { get; }
    internal bool RollbackSucceeded { get; }
    internal long ElapsedMilliseconds { get; }
    internal Exception? Error { get; }
}

internal interface IStandardBaselineDesiredStateOperations<TDesiredState>
{
    string DomainKey { get; }
    BaselineDesiredStateCapabilities Capabilities { get; }
    void Validate(TDesiredState desiredState);
    void RestoreStaticBaseline(TDesiredState desiredState);
    void ApplyDesiredStateToStaticBaseline(TDesiredState desiredState);
    void PrepareLiveBaseline(TDesiredState desiredState);
    void ApplyDesiredStateToLive(TDesiredState desiredState);
    void Commit(TDesiredState desiredState);
    void HandleFailure(TDesiredState desiredState, StandardApplyFailureContext failureContext);
}

internal abstract class StandardBaselineDesiredStateOperations<TDesiredState> : IStandardBaselineDesiredStateOperations<TDesiredState>
{
    public abstract string DomainKey { get; }
    public abstract BaselineDesiredStateCapabilities Capabilities { get; }
    public virtual void Validate(TDesiredState desiredState) { }
    public virtual void RestoreStaticBaseline(TDesiredState desiredState) { }
    public virtual void ApplyDesiredStateToStaticBaseline(TDesiredState desiredState) { }
    public virtual void PrepareLiveBaseline(TDesiredState desiredState) { }
    public virtual void ApplyDesiredStateToLive(TDesiredState desiredState) { }
    public virtual void Commit(TDesiredState desiredState) { }
    public virtual void HandleFailure(TDesiredState desiredState, StandardApplyFailureContext failureContext) { }
}

internal static class StandardBaselineDesiredStateCoordinator
{
    internal static StandardApplyOutcome Run<TDesiredState>(
        StandardDomainApplyPlan applyPlan,
        TDesiredState desiredState,
        IStandardBaselineDesiredStateOperations<TDesiredState> operations)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        BaselineDesiredStateCapabilities capabilities = operations.Capabilities;
        StandardApplyStage currentStage = StandardApplyStage.None;
        bool liveApplied = false;

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
                    liveApplied = true;
                }
            }

            currentStage = StandardApplyStage.Commit;
            operations.Commit(desiredState);
            stopwatch.Stop();
            return new StandardApplyOutcome(
                success: true,
                completedStage: StandardApplyStage.Commit,
                liveApplied: liveApplied,
                rollbackAttempted: false,
                rollbackSucceeded: false,
                elapsedMilliseconds: stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            StandardApplyStage failedStage = currentStage;
            bool rollbackAttempted = false;
            bool rollbackSucceeded = false;

            if (failedStage == StandardApplyStage.ApplyStaticBaseline &&
                (capabilities & BaselineDesiredStateCapabilities.StaticRollback) != 0 &&
                (capabilities & BaselineDesiredStateCapabilities.StaticBaseline) != 0)
            {
                rollbackAttempted = true;
                try
                {
                    operations.RestoreStaticBaseline(desiredState);
                    rollbackSucceeded = true;
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
                    new StandardApplyFailureContext(
                        failedStage,
                        ex,
                        rollbackAttempted,
                        rollbackSucceeded,
                        failedStage == StandardApplyStage.PrepareLiveBaseline || failedStage == StandardApplyStage.ApplyLive,
                        stopwatch.ElapsedMilliseconds));
            }
            catch (Exception failureHandlerEx)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                    $"Failure handler failed for domain '{operations.DomainKey}'. {failureHandlerEx.Message}");
                DropNSpawnPlugin.DropNSpawnLogger.LogError(failureHandlerEx);
            }

            return new StandardApplyOutcome(
                success: false,
                completedStage: failedStage,
                liveApplied: liveApplied,
                rollbackAttempted: rollbackAttempted,
                rollbackSucceeded: rollbackSucceeded,
                elapsedMilliseconds: stopwatch.ElapsedMilliseconds,
                error: ex);
        }
    }
}
