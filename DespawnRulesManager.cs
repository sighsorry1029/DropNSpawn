using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal sealed class DespawnRefundDrop
{
    internal DespawnRefundDrop(GameObject prefab, int amount)
    {
        Prefab = prefab;
        Amount = amount;
    }

    internal GameObject Prefab { get; }
    internal int Amount { get; }
}

/// <summary>
/// Owns the tracked despawn state machine, including observation intake, detach persistence, and scheduled countdown evaluation.
/// ExecuteServerTick is the only path allowed to mutate tracked despawn state.
/// </summary>
internal static class DespawnRulesManager
{
    private const float CreatedZdoObservationRetryIntervalSeconds = 0.25f;
    private const float CreatedZdoObservationRetryTimeoutSeconds = 1f;
    private const float DespawnCountdownCheckIntervalSeconds = 0.5f;
    private const float DespawnIdleCheckIntervalSeconds = 1f;
    private const float DespawnTrackingRefreshIntervalSeconds = 1f;
    private const float DespawnIneligibleProducerSummaryIntervalSeconds = 5f;
    private const int DespawnFinalCountdownSeconds = 5;
    private const int DespawnReminderIntervalSeconds = 5;
    private static readonly Dictionary<ZDOID, TrackedDespawnState> TrackedDespawnTargets = new();
    private static readonly SortedDictionary<long, List<ZDOID>> ScheduledDespawnChecks = new();
    private static readonly Dictionary<ZDOID, PendingDespawnObservation> PendingDespawnObservations = new();
    private static readonly Dictionary<ZDOID, PendingDespawnDetachPersist> PendingDespawnDetachPersists = new();
    private static readonly List<ZDOID> PendingDespawnRemovals = new();
    private static readonly List<ZDOID> PendingDespawnObservationRemovals = new();
    private static readonly List<PendingDespawnObservation> PendingDespawnObservationUpdates = new();
    private static readonly List<ZDOID> PendingDespawnDetachPersistRemovals = new();
    private static readonly List<ZDO> BootstrapScanBuffer = new();
    private static float _nextDespawnTrackingRefreshAt;
    private static float _nextIneligibleProducerSummaryAt;
    private static bool _loggedServerTickActivation;
    private static bool _pendingBootstrapScan = true;
    private static int _createdZdoProducerQueuedCount;
    private static int _createdZdoProducerDeferredCount;
    private static int _createdZdoProducerDeferredExpiredCount;
    private static int _skippedCreatedZdoProducerCount;
    private static int _skippedLoadedCharacterProducerCount;
    private static int _skippedDetachPersistProducerCount;
    private static int _loadedCharacterProducerAttemptCount;
    private static int _loadedCharacterProducerQueuedCount;
    private static int _loadedCharacterProducerMissingCharacterCount;
    private static int _loadedCharacterProducerDeadCharacterCount;
    private static int _loadedCharacterProducerMissingNviewCount;
    private static int _loadedCharacterProducerInvalidNviewCount;
    private static int _loadedCharacterProducerMissingZdoCount;
    private static int _loadedCharacterProducerDeadZdoCount;
    private static int _loadedCharacterProducerMissingPrefabNameCount;

    private enum DespawnObservationSource
    {
        BootstrapScan = 0,
        CreatedZdo = 1,
        LoadedCharacter = 2
    }

    private enum DespawnProducerSkipSource
    {
        CreatedZdo = 0,
        LoadedCharacter = 1,
        DetachPersist = 2
    }

    private enum CreatedZdoObservationDecision
    {
        Eligible = 0,
        Ineligible = 1,
        Unknown = 2
    }

    private enum ObservationApplicationResult
    {
        Applied = 0,
        Dropped = 1,
        Deferred = 2
    }

    private readonly struct PendingDespawnObservation
    {
        internal PendingDespawnObservation(
            ZDOID zdoId,
            int prefabHashHint,
            string prefabNameHint,
            DespawnObservationSource source,
            float nextAttemptAt = 0f,
            float expireAt = 0f,
            int retryCount = 0)
        {
            ZdoId = zdoId;
            PrefabHashHint = prefabHashHint;
            PrefabNameHint = prefabNameHint ?? "";
            Source = source;
            NextAttemptAt = nextAttemptAt;
            ExpireAt = expireAt;
            RetryCount = retryCount;
        }

        internal ZDOID ZdoId { get; }
        internal int PrefabHashHint { get; }
        internal string PrefabNameHint { get; }
        internal DespawnObservationSource Source { get; }
        internal float NextAttemptAt { get; }
        internal float ExpireAt { get; }
        internal int RetryCount { get; }
    }

    private readonly struct PendingDespawnDetachPersist
    {
        internal PendingDespawnDetachPersist(
            ZDOID zdoId,
            Vector3 probePoint,
            int prefabHashHint,
            string prefabNameHint)
        {
            ZdoId = zdoId;
            ProbePoint = probePoint;
            PrefabHashHint = prefabHashHint;
            PrefabNameHint = prefabNameHint ?? "";
        }

        internal ZDOID ZdoId { get; }
        internal Vector3 ProbePoint { get; }
        internal int PrefabHashHint { get; }
        internal string PrefabNameHint { get; }
    }

    private sealed class TrackedDespawnState
    {
        internal string DisplayName { get; set; } = "Target";
        internal string PrefabName { get; set; } = "";
        internal float? RangeOverride { get; set; }
        internal float? DelayOverride { get; set; }
        internal readonly List<DespawnRefundDrop> Refunds = new();
        internal double NoPlayerSince { get; set; } = -1d;
        internal int LastAnnouncedRemainingSeconds { get; set; } = -1;
        internal long LastInterestedPlayerId { get; set; }
        internal long CountdownRecipientPlayerId { get; set; }
        internal long ScheduledCheckAtBucket { get; set; } = long.MinValue;

        internal void UpdateFromCharacter(
            Character character,
            float? rangeOverride,
            float? delayOverride,
            IReadOnlyCollection<DespawnRefundDrop> refunds)
        {
            DisplayName = GetDisplayName(character);
            PrefabName = Utils.GetPrefabName(character.gameObject);
            RangeOverride = rangeOverride;
            DelayOverride = delayOverride;
            Refunds.Clear();
            if (refunds == null)
            {
                return;
            }

            Refunds.AddRange(refunds);
        }

        internal void UpdateFromZdoPrefab(
            string prefabName,
            float? rangeOverride,
            float? delayOverride,
            IReadOnlyCollection<DespawnRefundDrop> refunds)
        {
            DisplayName = string.IsNullOrWhiteSpace(prefabName) ? "Target" : prefabName;
            PrefabName = prefabName ?? "";
            RangeOverride = rangeOverride;
            DelayOverride = delayOverride;
            Refunds.Clear();
            if (refunds == null)
            {
                return;
            }

            Refunds.AddRange(refunds);
        }

        internal void ResetCountdown()
        {
            NoPlayerSince = -1d;
            LastAnnouncedRemainingSeconds = -1;
            CountdownRecipientPlayerId = 0L;
        }

        internal float GetEffectiveRange()
        {
            return Mathf.Clamp(RangeOverride ?? PluginSettingsFacade.GetDefaultDespawnRange(), 0f, 128f);
        }

        internal float GetEffectiveDelaySeconds()
        {
            return Mathf.Clamp(DelayOverride ?? PluginSettingsFacade.GetDefaultDespawnDelaySeconds(), 0f, 300f);
        }
    }

    internal static void ExecuteServerTick()
    {
        if (!DropNSpawnPlugin.IsRuntimeServer())
        {
            return;
        }

        if (!PluginSettingsFacade.IsCharacterDomainEnabled())
        {
            if (TrackedDespawnTargets.Count > 0)
            {
                TrackedDespawnTargets.Clear();
                PendingDespawnRemovals.Clear();
            }

            if (ScheduledDespawnChecks.Count > 0)
            {
                ScheduledDespawnChecks.Clear();
            }

            if (PendingDespawnObservations.Count > 0)
            {
                PendingDespawnObservations.Clear();
                PendingDespawnObservationRemovals.Clear();
            }

            if (PendingDespawnObservationUpdates.Count > 0)
            {
                PendingDespawnObservationUpdates.Clear();
            }

            if (PendingDespawnDetachPersists.Count > 0)
            {
                PendingDespawnDetachPersists.Clear();
                PendingDespawnDetachPersistRemovals.Clear();
            }

            _nextDespawnTrackingRefreshAt = 0f;
            ResetSkippedIneligibleProducerDiagnostics();
            _loggedServerTickActivation = false;
            _pendingBootstrapScan = true;
            return;
        }

        if (!_loggedServerTickActivation)
        {
            LogDiagnostics(
                $"Despawn server tick active. players={Player.GetAllPlayers().Count} characterDomainEnabled={PluginSettingsFacade.IsCharacterDomainEnabled()} trackedCharacters={TrackedDespawnTargets.Count}.");
            _loggedServerTickActivation = true;
        }

        ProcessSkippedIneligibleProducerDiagnostics(Time.time);
        ApplyPendingDespawnObservations();

        float nowRealtime = Time.time;
        if (_nextDespawnTrackingRefreshAt <= nowRealtime)
        {
            PruneTrackedDespawnTargetsAgainstCurrentConfig();
            if (_pendingBootstrapScan && RunPendingBootstrapScan())
            {
                _pendingBootstrapScan = false;
                ApplyPendingDespawnObservations();
            }
            _nextDespawnTrackingRefreshAt = nowRealtime + DespawnTrackingRefreshIntervalSeconds;
        }

        double nowSeconds = GetCurrentDespawnClockSeconds();
        ApplyPendingDespawnDetaches(nowSeconds);

        if (TrackedDespawnTargets.Count == 0)
        {
            if (ScheduledDespawnChecks.Count > 0)
            {
                ScheduledDespawnChecks.Clear();
            }

            return;
        }

        if (ScheduledDespawnChecks.Count == 0)
        {
            return;
        }

        ProcessScheduledDespawnChecks(nowSeconds);
    }

    private static void ProcessScheduledDespawnChecks(double nowSeconds)
    {
        if (ScheduledDespawnChecks.Count == 0)
        {
            return;
        }

        long nowBucket = QuantizeScheduledCheck(nowSeconds);
        PendingDespawnRemovals.Clear();
        while (TryDequeueDueScheduledCheck(nowBucket, out long bucket, out List<ZDOID>? dueTargets))
        {
            foreach (ZDOID zdoId in dueTargets)
            {
                if (!TrackedDespawnTargets.TryGetValue(zdoId, out TrackedDespawnState? state) ||
                    state.ScheduledCheckAtBucket != bucket)
                {
                    continue;
                }

                state.ScheduledCheckAtBucket = long.MinValue;
                ZDO? zdo = ZDOMan.instance?.GetZDO(zdoId);
                if (zdo == null || IsDeadZdo(zdoId))
                {
                    PendingDespawnRemovals.Add(zdoId);
                    continue;
                }

                ProcessTrackedDespawnTarget(zdoId, zdo, state, nowSeconds);
            }
        }

        FlushPendingDespawnRemovals();
    }

    private static bool TryDequeueDueScheduledCheck(long nowBucket, out long bucket, out List<ZDOID> dueTargets)
    {
        if (ScheduledDespawnChecks.Count == 0)
        {
            bucket = long.MinValue;
            dueTargets = null!;
            return false;
        }

        using IEnumerator<KeyValuePair<long, List<ZDOID>>> enumerator = ScheduledDespawnChecks.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            bucket = long.MinValue;
            dueTargets = null!;
            return false;
        }

        KeyValuePair<long, List<ZDOID>> entry = enumerator.Current;
        if (entry.Key > nowBucket)
        {
            bucket = long.MinValue;
            dueTargets = null!;
            return false;
        }

        bucket = entry.Key;
        dueTargets = entry.Value;
        ScheduledDespawnChecks.Remove(entry.Key);
        return true;
    }

    internal static void MarkBootstrapScanDirty(string reason)
    {
        _pendingBootstrapScan = true;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            LogDiagnostics($"Marked despawn bootstrap scan dirty. reason={reason}.");
        }
    }

    private static bool RunPendingBootstrapScan()
    {
        if (ZDOMan.instance == null)
        {
            return false;
        }

        IReadOnlyList<string> prefabs = CharacterDropManager.GetDespawnBootstrapPrefabOrder();
        if (prefabs.Count == 0)
        {
            return true;
        }

        LogDiagnostics($"Running despawn bootstrap scan for {prefabs.Count} prefab(s).");
        foreach (string prefabName in prefabs)
        {
            QueueBootstrapScanDespawnObservations(prefabName);
        }
        return true;
    }

    internal static void QueueCreatedDespawnTarget(int prefabHashHint, ZDO? zdo)
    {
        if (!DropNSpawnPlugin.IsRuntimeServer() ||
            !PluginSettingsFacade.IsCharacterDomainEnabled() ||
            zdo == null ||
            zdo.m_uid.IsNone())
        {
            return;
        }

        int authoritativePrefabHash = zdo.GetPrefab();
        int prefabHash = authoritativePrefabHash;
        if (prefabHash == 0)
        {
            prefabHash = prefabHashHint;
        }

        CreatedZdoObservationDecision queueDecision = GetCreatedZdoObservationDecision(authoritativePrefabHash, prefabHashHint);
        if (queueDecision == CreatedZdoObservationDecision.Ineligible)
        {
            RecordSkippedIneligibleProducer(DespawnProducerSkipSource.CreatedZdo);
            return;
        }

        EnqueueDespawnObservation(
            new PendingDespawnObservation(
                zdo.m_uid,
                prefabHash,
                "",
                DespawnObservationSource.CreatedZdo));
        _createdZdoProducerQueuedCount++;
    }

    private static void ApplyPendingDespawnObservations()
    {
        if (PendingDespawnObservations.Count == 0 ||
            ZNetScene.instance == null ||
            ObjectDB.instance == null ||
            ZDOMan.instance == null)
        {
            return;
        }

        float nowRealtime = Time.time;
        PendingDespawnObservationRemovals.Clear();
        PendingDespawnObservationUpdates.Clear();
        foreach (PendingDespawnObservation observation in PendingDespawnObservations.Values)
        {
            if (observation.NextAttemptAt > nowRealtime)
            {
                continue;
            }

            ZDO? zdo = ZDOMan.instance.GetZDO(observation.ZdoId);
            if (zdo == null || IsDeadZdo(observation.ZdoId))
            {
                PendingDespawnObservationRemovals.Add(observation.ZdoId);
                continue;
            }

            ObservationApplicationResult applyResult = ApplyQueuedObservation(observation, zdo, nowRealtime, out PendingDespawnObservation updatedObservation);
            switch (applyResult)
            {
                case ObservationApplicationResult.Applied:
                case ObservationApplicationResult.Dropped:
                    PendingDespawnObservationRemovals.Add(observation.ZdoId);
                    break;
                case ObservationApplicationResult.Deferred:
                    PendingDespawnObservationUpdates.Add(updatedObservation);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(applyResult), applyResult, null);
            }
        }

        foreach (PendingDespawnObservation observation in PendingDespawnObservationUpdates)
        {
            PendingDespawnObservations[observation.ZdoId] = observation;
        }

        foreach (ZDOID zdoId in PendingDespawnObservationRemovals)
        {
            PendingDespawnObservations.Remove(zdoId);
        }

        PendingDespawnObservationRemovals.Clear();
        PendingDespawnObservationUpdates.Clear();
    }

    private static void ApplyPendingDespawnDetaches(double nowSeconds)
    {
        if (PendingDespawnDetachPersists.Count == 0 ||
            ZNetScene.instance == null ||
            ObjectDB.instance == null ||
            ZDOMan.instance == null)
        {
            return;
        }

        PendingDespawnDetachPersistRemovals.Clear();
        foreach (PendingDespawnDetachPersist persist in PendingDespawnDetachPersists.Values)
        {
            ZDO? zdo = ZDOMan.instance.GetZDO(persist.ZdoId);
            if (zdo == null || IsDeadZdo(persist.ZdoId))
            {
                PendingDespawnDetachPersistRemovals.Add(persist.ZdoId);
                continue;
            }

            PendingDespawnDetachPersistRemovals.Add(persist.ZdoId);
            ApplyDetachPersist(persist, zdo, nowSeconds);
        }

        foreach (ZDOID zdoId in PendingDespawnDetachPersistRemovals)
        {
            PendingDespawnDetachPersists.Remove(zdoId);
        }

        PendingDespawnDetachPersistRemovals.Clear();
    }

    internal static void TryPersistDespawnCountdownBeforeResetZdo(ZNetView? nview)
    {
        if (!DropNSpawnPlugin.IsRuntimeServer() ||
            !PluginSettingsFacade.IsCharacterDomainEnabled() ||
            nview == null ||
            !nview.IsValid() ||
            !nview.TryGetComponent(out Character character) ||
            character.GetHealth() <= 0f)
        {
            return;
        }

        ZDO? zdo = nview.GetZDO();
        if (zdo == null)
        {
            return;
        }

        if (!ShouldQueueDespawnObservation(zdo.GetPrefab(), Utils.GetPrefabName(character.gameObject)))
        {
            RecordSkippedIneligibleProducer(DespawnProducerSkipSource.DetachPersist);
            return;
        }

        PendingDespawnDetachPersists[zdo.m_uid] = new PendingDespawnDetachPersist(
            zdo.m_uid,
            character.GetCenterPoint(),
            zdo.GetPrefab(),
            Utils.GetPrefabName(character.gameObject));
    }

    internal static void TryTrackLoadedDespawnTarget(Character? character)
    {
        if (!DropNSpawnPlugin.IsRuntimeServer() ||
            !PluginSettingsFacade.IsCharacterDomainEnabled())
        {
            return;
        }

        _loadedCharacterProducerAttemptCount++;
        if (character == null || character.gameObject == null)
        {
            _loadedCharacterProducerMissingCharacterCount++;
            return;
        }

        if (character.IsDead())
        {
            _loadedCharacterProducerDeadCharacterCount++;
            return;
        }

        ZNetView? nview = character.GetComponent<ZNetView>();
        string prefabName = Utils.GetPrefabName(character.gameObject);
        if (nview == null)
        {
            _loadedCharacterProducerMissingNviewCount++;
            return;
        }

        if (!nview.IsValid())
        {
            _loadedCharacterProducerInvalidNviewCount++;
            return;
        }

        ZDO? zdo = nview.GetZDO();
        if (zdo == null)
        {
            _loadedCharacterProducerMissingZdoCount++;
            return;
        }

        if (IsDeadZdo(zdo.m_uid))
        {
            _loadedCharacterProducerDeadZdoCount++;
            return;
        }

        if (string.IsNullOrWhiteSpace(prefabName))
        {
            _loadedCharacterProducerMissingPrefabNameCount++;
            return;
        }

        if (!ShouldQueueDespawnObservation(zdo.GetPrefab(), prefabName))
        {
            RecordSkippedIneligibleProducer(DespawnProducerSkipSource.LoadedCharacter);
            return;
        }

        EnqueueDespawnObservation(
            new PendingDespawnObservation(
                zdo.m_uid,
                zdo.GetPrefab(),
                prefabName,
                DespawnObservationSource.LoadedCharacter));
        _loadedCharacterProducerQueuedCount++;
        LogDiagnostics($"Queued loaded character observation for despawn tracking zdo={zdo.m_uid} prefab={prefabName} prefabHash={zdo.GetPrefab()}.");
    }

    private static void ProcessTrackedDespawnTarget(
        ZDOID zdoId,
        ZDO zdo,
        TrackedDespawnState state,
        double nowSeconds)
    {
        Character? loadedCharacter = TryGetLoadedTrackedCharacter(zdo);
        if (loadedCharacter != null && loadedCharacter.GetHealth() <= 0f)
        {
            LogDiagnostics($"Dropping tracked despawn target zdo={zdoId} because loaded character is already dead.");
            PendingDespawnRemovals.Add(zdoId);
            return;
        }

        float despawnRange = state.GetEffectiveRange();
        float despawnDelaySeconds = state.GetEffectiveDelaySeconds();
        if (despawnRange <= 0f)
        {
            state.ResetCountdown();
            ScheduleTrackedDespawnCheck(zdoId, state, nowSeconds + GetIdleCheckIntervalSeconds());
            return;
        }

        Vector3 probePoint = loadedCharacter != null ? loadedCharacter.GetCenterPoint() : zdo.GetPosition();
        bool hasPlayerInRange = SceneProximityQueries.TryFindAnyLivingPlayerInRangeXZ(probePoint, despawnRange, out long interestedPlayerId);
        if (hasPlayerInRange)
        {
            if (interestedPlayerId != 0L)
            {
                state.LastInterestedPlayerId = interestedPlayerId;
            }

            if (state.NoPlayerSince >= 0d)
            {
                long cancelRecipientId = interestedPlayerId;
                if (SceneProximityQueries.TryFindNearestLivingPlayerInRangeXZ(probePoint, despawnRange, out long nearestPlayerId))
                {
                    cancelRecipientId = nearestPlayerId;
                }

                SendDespawnMessage(cancelRecipientId, BuildDespawnCanceledMessage(state.DisplayName));
                LogDiagnostics(
                    $"Canceled despawn countdown zdo={zdoId} name={state.DisplayName} recipient={cancelRecipientId} position={FormatPosition(probePoint)}.");
            }

            state.ResetCountdown();
            ScheduleTrackedDespawnCheck(zdoId, state, nowSeconds + GetIdleCheckIntervalSeconds());
            return;
        }

        if (state.NoPlayerSince < 0d)
        {
            StartDespawnCountdown(state, nowSeconds, despawnDelaySeconds);
            LogDiagnostics(
                $"Started despawn countdown zdo={zdoId} name={state.DisplayName} delay={despawnDelaySeconds:0.##} loaded={loadedCharacter != null} position={FormatPosition(probePoint)}.");
        }

        double elapsedSeconds = nowSeconds - state.NoPlayerSince;
        if (elapsedSeconds >= despawnDelaySeconds)
        {
            LogDiagnostics(
                $"Expiring despawn countdown zdo={zdoId} name={state.DisplayName} elapsed={elapsedSeconds:0.##} delay={despawnDelaySeconds:0.##} loaded={loadedCharacter != null} refunds={state.Refunds.Count} position={FormatPosition(probePoint)}.");
            if (!CharacterDropManager.TryExecuteConfiguredDespawnRefunds(probePoint, state.Refunds) &&
                PluginSettingsFacade.IsDespawnDiagnosticsEnabled())
            {
                LogDiagnostics($"Configured despawn refund execution failed zdo={zdoId} name={state.DisplayName} position={probePoint.x:F1},{probePoint.y:F1},{probePoint.z:F1} refunds={state.Refunds.Count}.");
            }
            DespawnCleanupSupport.ApplyBeforeDestroy(zdo);
            zdo.SetOwner(ZDOMan.instance.m_sessionID);
            ZDOMan.instance.DestroyZDO(zdo);
            PendingDespawnRemovals.Add(zdoId);
            return;
        }

        int remainingSeconds = GetRemainingSeconds(despawnDelaySeconds, elapsedSeconds);
        if (state.CountdownRecipientPlayerId != 0L &&
            remainingSeconds != state.LastAnnouncedRemainingSeconds &&
            ShouldAnnounceDespawnRemaining(remainingSeconds))
        {
            SendDespawnMessage(state.CountdownRecipientPlayerId, BuildDespawnReminderMessage(state.DisplayName, remainingSeconds));
            state.LastAnnouncedRemainingSeconds = remainingSeconds;
        }

        ScheduleTrackedDespawnCheck(zdoId, state, nowSeconds + GetCountdownCheckIntervalSeconds());
    }

    private static void PruneTrackedDespawnTargetsAgainstCurrentConfig()
    {
        if (TrackedDespawnTargets.Count == 0)
        {
            return;
        }

        foreach ((ZDOID zdoId, TrackedDespawnState state) in TrackedDespawnTargets)
        {
            if (string.IsNullOrWhiteSpace(state.PrefabName))
            {
                continue;
            }

            if (CharacterDropManager.TryResolveDespawnTrackingRule(
                    state.PrefabName,
                    out _,
                    out _,
                    out _))
            {
                continue;
            }

            PendingDespawnRemovals.Add(zdoId);
            LogDiagnostics(
                $"Stopped tracking despawn target because prefab '{state.PrefabName}' is no longer eligible under current despawn config zdo={zdoId}.");
        }

        FlushPendingDespawnRemovals();
    }

    private static void QueueBootstrapScanDespawnObservations(string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName) || ZDOMan.instance == null)
        {
            return;
        }

        BootstrapScanBuffer.Clear();
        int index = 0;
        while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, BootstrapScanBuffer, ref index))
        {
        }

        foreach (ZDO zdo in BootstrapScanBuffer)
        {
            if (zdo == null || zdo.m_uid.IsNone())
            {
                continue;
            }

            EnqueueDespawnObservation(
                new PendingDespawnObservation(
                    zdo.m_uid,
                    zdo.GetPrefab(),
                    prefabName,
                    DespawnObservationSource.BootstrapScan));
        }
    }

    private static void EnqueueDespawnObservation(PendingDespawnObservation observation)
    {
        if (observation.ZdoId.IsNone())
        {
            return;
        }

        if (!PendingDespawnObservations.TryGetValue(observation.ZdoId, out PendingDespawnObservation existing))
        {
            PendingDespawnObservations[observation.ZdoId] = observation;
            return;
        }

        PendingDespawnObservations[observation.ZdoId] = MergeDespawnObservation(existing, observation);
    }

    private static PendingDespawnObservation MergeDespawnObservation(
        PendingDespawnObservation current,
        PendingDespawnObservation incoming)
    {
        PendingDespawnObservation preferred =
            GetObservationPriority(incoming.Source) >= GetObservationPriority(current.Source)
                ? incoming
                : current;
        int prefabHashHint = incoming.PrefabHashHint != 0 ? incoming.PrefabHashHint : current.PrefabHashHint;
        string prefabNameHint =
            !string.IsNullOrWhiteSpace(incoming.PrefabNameHint)
                ? incoming.PrefabNameHint
                : current.PrefabNameHint;
        float nextAttemptAt = preferred.Source == DespawnObservationSource.CreatedZdo
            ? Mathf.Max(current.NextAttemptAt, incoming.NextAttemptAt)
            : 0f;
        float expireAt = preferred.Source == DespawnObservationSource.CreatedZdo
            ? Mathf.Max(current.ExpireAt, incoming.ExpireAt)
            : 0f;
        int retryCount = preferred.Source == DespawnObservationSource.CreatedZdo
            ? Math.Max(current.RetryCount, incoming.RetryCount)
            : 0;
        return new PendingDespawnObservation(
            preferred.ZdoId,
            prefabHashHint,
            prefabNameHint,
            preferred.Source,
            nextAttemptAt,
            expireAt,
            retryCount);
    }

    private static int GetObservationPriority(DespawnObservationSource source)
    {
        return source switch
        {
            DespawnObservationSource.LoadedCharacter => 3,
            DespawnObservationSource.CreatedZdo => 2,
            _ => 1
        };
    }

    private static string DescribeObservationSource(DespawnObservationSource source)
    {
        return source switch
        {
            DespawnObservationSource.LoadedCharacter => "loaded character",
            DespawnObservationSource.CreatedZdo => "created ZDO",
            _ => "bootstrap scan"
        };
    }

    private static ObservationApplicationResult ApplyQueuedObservation(
        PendingDespawnObservation observation,
        ZDO zdo,
        float nowRealtime,
        out PendingDespawnObservation updatedObservation)
    {
        updatedObservation = observation;
        if (ApplyObservation(observation, zdo) != null)
        {
            return ObservationApplicationResult.Applied;
        }

        if (TryDeferCreatedZdoObservation(observation, zdo, nowRealtime, out updatedObservation))
        {
            return ObservationApplicationResult.Deferred;
        }

        return ObservationApplicationResult.Dropped;
    }

    private static TrackedDespawnState? ApplyObservation(PendingDespawnObservation observation, ZDO zdo)
    {
        return ApplyObservation(
            zdo,
            observation.PrefabHashHint,
            observation.PrefabNameHint,
            DescribeObservationSource(observation.Source));
    }

    private static TrackedDespawnState? ApplyObservation(
        ZDO zdo,
        int prefabHashHint,
        string prefabNameHint,
        string source)
    {
        if (!TryResolveObservationConfig(
                zdo,
                prefabHashHint,
                prefabNameHint,
                out string prefabName,
                out float? rangeOverride,
                out float? delayOverride,
                out IReadOnlyCollection<DespawnRefundDrop> refunds))
        {
            return null;
        }

        return ApplyObservationResolved(
            prefabName,
            zdo,
            rangeOverride,
            delayOverride,
            refunds,
            source);
    }

    private static bool TryResolveObservationConfig(
        ZDO zdo,
        int prefabHashHint,
        string prefabNameHint,
        out string prefabName,
        out float? rangeOverride,
        out float? delayOverride,
        out IReadOnlyCollection<DespawnRefundDrop> refunds)
    {
        prefabName = string.IsNullOrWhiteSpace(prefabNameHint) ? "" : prefabNameHint;
        rangeOverride = null;
        delayOverride = null;
        refunds = Array.Empty<DespawnRefundDrop>();

        int prefabHash = zdo.GetPrefab();
        if (prefabHash == 0)
        {
            prefabHash = prefabHashHint;
        }

        if (prefabHash != 0 &&
            CharacterDropManager.TryResolveDespawnTrackingRule(prefabHash, out string resolvedPrefabName, out rangeOverride, out delayOverride, out refunds))
        {
            prefabName = !string.IsNullOrWhiteSpace(resolvedPrefabName)
                ? resolvedPrefabName
                : prefabName;
            return !string.IsNullOrWhiteSpace(prefabName);
        }

        if (!string.IsNullOrWhiteSpace(prefabName) &&
            CharacterDropManager.TryResolveDespawnTrackingRule(prefabName, out rangeOverride, out delayOverride, out refunds))
        {
            return true;
        }

        return false;
    }

    private static TrackedDespawnState ApplyObservationResolved(
        string prefabName,
        ZDO zdo,
        float? rangeOverride,
        float? delayOverride,
        IReadOnlyCollection<DespawnRefundDrop> refunds,
        string source)
    {
        bool wasTracked = TrackedDespawnTargets.ContainsKey(zdo.m_uid);
        TrackedDespawnState state = GetOrCreateTrackedDespawnState(zdo.m_uid);
        Character? loadedCharacter = TryGetLoadedTrackedCharacter(zdo);
        if (loadedCharacter != null)
        {
            state.UpdateFromCharacter(loadedCharacter, rangeOverride, delayOverride, refunds);
            PrimeTrackedDespawnInterestIfNeeded(state, loadedCharacter.GetCenterPoint());
        }
        else
        {
            state.UpdateFromZdoPrefab(prefabName, rangeOverride, delayOverride, refunds);
            PrimeTrackedDespawnInterestIfNeeded(state, zdo.GetPosition());
        }

        if (!wasTracked)
        {
            LogDiagnostics(
                $"Tracking despawn target from {source} zdo={zdo.m_uid} name={state.DisplayName} prefab={prefabName} loaded={loadedCharacter != null} refunds={state.Refunds.Count} position={FormatPosition(zdo.GetPosition())}.");
        }

        ScheduleTrackedDespawnCheck(zdo.m_uid, state, GetCurrentDespawnClockSeconds());
        return state;
    }

    private static void ApplyDetachPersist(PendingDespawnDetachPersist persist, ZDO zdo, double nowSeconds)
    {
        PendingDespawnObservation observation = new(
            persist.ZdoId,
            persist.PrefabHashHint,
            persist.PrefabNameHint,
            DespawnObservationSource.LoadedCharacter);
        TrackedDespawnState? state = ApplyObservation(observation, zdo);
        if (state == null)
        {
            return;
        }

        float despawnRange = state.GetEffectiveRange();
        if (despawnRange <= 0f)
        {
            return;
        }

        if (SceneProximityQueries.TryFindAnyLivingPlayerInRangeXZ(persist.ProbePoint, despawnRange, out long interestedPlayerId))
        {
            state.LastInterestedPlayerId = interestedPlayerId;
            return;
        }

        if (state.NoPlayerSince >= 0d)
        {
            return;
        }

        float despawnDelaySeconds = state.GetEffectiveDelaySeconds();
        StartDespawnCountdown(state, nowSeconds, despawnDelaySeconds);
        ScheduleTrackedDespawnCheck(persist.ZdoId, state, nowSeconds);
        LogDiagnostics(
            $"Started despawn countdown from ResetZDO path zdo={persist.ZdoId} name={state.DisplayName} range={despawnRange:0.##} delay={despawnDelaySeconds:0.##} position={FormatPosition(persist.ProbePoint)}.");
    }

    private static void StartDespawnCountdown(TrackedDespawnState state, double nowSeconds, float despawnDelaySeconds)
    {
        state.NoPlayerSince = nowSeconds;
        state.CountdownRecipientPlayerId = GetCountdownRecipientPlayerId(state);
        state.LastAnnouncedRemainingSeconds = GetRemainingSeconds(despawnDelaySeconds, 0d);
        if (state.CountdownRecipientPlayerId == 0L)
        {
            LogDiagnostics($"Started despawn countdown without message recipients name={state.DisplayName} prefab={state.PrefabName} delay={despawnDelaySeconds:0.##}.");
        }

        if (despawnDelaySeconds > 0f && state.CountdownRecipientPlayerId != 0L)
        {
            SendDespawnMessage(state.CountdownRecipientPlayerId, BuildDespawnStartMessage(state.DisplayName, state.LastAnnouncedRemainingSeconds));
        }
    }

    private static void PrimeTrackedDespawnInterestIfNeeded(TrackedDespawnState state, Vector3 point)
    {
        if (state.NoPlayerSince >= 0d || state.LastInterestedPlayerId != 0L)
        {
            return;
        }

        float despawnRange = state.GetEffectiveRange();
        if (despawnRange <= 0f)
        {
            return;
        }

        if (!SceneProximityQueries.TryFindAnyLivingPlayerInRangeXZ(point, despawnRange, out long interestedPlayerId))
        {
            return;
        }

        state.LastInterestedPlayerId = interestedPlayerId;
        LogDiagnostics(
            $"Primed despawn message recipient name={state.DisplayName} recipient={interestedPlayerId} position={FormatPosition(point)}.");
    }

    private static long GetCountdownRecipientPlayerId(TrackedDespawnState state)
    {
        long recipientPlayerId = state.LastInterestedPlayerId;
        return IsDespawnMessageRecipientAvailable(recipientPlayerId)
            ? recipientPlayerId
            : 0L;
    }

    private static void FlushPendingDespawnRemovals()
    {
        if (PendingDespawnRemovals.Count == 0)
        {
            return;
        }

        foreach (ZDOID zdoId in PendingDespawnRemovals)
        {
            if (TrackedDespawnTargets.TryGetValue(zdoId, out TrackedDespawnState? state))
            {
                state.ScheduledCheckAtBucket = long.MinValue;
            }

            TrackedDespawnTargets.Remove(zdoId);
        }

        PendingDespawnRemovals.Clear();
    }

    private static TrackedDespawnState GetOrCreateTrackedDespawnState(ZDOID zdoId)
    {
        if (!TrackedDespawnTargets.TryGetValue(zdoId, out TrackedDespawnState? state))
        {
            state = new TrackedDespawnState();
            TrackedDespawnTargets[zdoId] = state;
        }

        return state;
    }

    private static Character? TryGetLoadedTrackedCharacter(ZDO zdo)
    {
        if (ZNetScene.instance == null ||
            !ZNetScene.instance.m_instances.TryGetValue(zdo, out ZNetView nview) ||
            nview == null ||
            nview.gameObject == null)
        {
            return null;
        }

        return nview.GetComponent<Character>();
    }

    private static bool IsDeadZdo(ZDOID zdoId)
    {
        return ZDOMan.instance != null && ZDOMan.instance.m_deadZDOs.ContainsKey(zdoId);
    }

    private static int GetRemainingSeconds(float despawnDelaySeconds, double elapsedSeconds)
    {
        float remainingSeconds = Mathf.Max(0f, despawnDelaySeconds - (float)elapsedSeconds);
        return Mathf.Max(0, Mathf.CeilToInt(remainingSeconds));
    }

    private static void ScheduleTrackedDespawnCheck(ZDOID zdoId, TrackedDespawnState state, double scheduledTime)
    {
        long bucket = QuantizeScheduledCheck(scheduledTime);
        state.ScheduledCheckAtBucket = bucket;
        if (!ScheduledDespawnChecks.TryGetValue(bucket, out List<ZDOID>? scheduledTargets))
        {
            scheduledTargets = new List<ZDOID>();
            ScheduledDespawnChecks[bucket] = scheduledTargets;
        }

        scheduledTargets.Add(zdoId);
    }

    private static CreatedZdoObservationDecision GetCreatedZdoObservationDecision(int authoritativePrefabHash, int prefabHashHint)
    {
        if (!CharacterDropManager.IsDespawnTrackingRuleLookupReady())
        {
            return CreatedZdoObservationDecision.Eligible;
        }

        if (authoritativePrefabHash != 0)
        {
            return CharacterDropManager.IsEligibleDespawnTrackingPrefabHash(authoritativePrefabHash)
                ? CreatedZdoObservationDecision.Eligible
                : CreatedZdoObservationDecision.Ineligible;
        }

        if (prefabHashHint != 0 &&
            CharacterDropManager.IsEligibleDespawnTrackingPrefabHash(prefabHashHint))
        {
            return CreatedZdoObservationDecision.Eligible;
        }

        return CreatedZdoObservationDecision.Unknown;
    }

    private static bool ShouldQueueDespawnObservation(int prefabHashHint, string prefabNameHint)
    {
        if (!CharacterDropManager.IsDespawnTrackingRuleLookupReady())
        {
            return true;
        }

        if (prefabHashHint != 0 &&
            CharacterDropManager.IsEligibleDespawnTrackingPrefabHash(prefabHashHint))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(prefabNameHint) &&
               CharacterDropManager.IsEligibleDespawnTrackingPrefabName(prefabNameHint);
    }

    private static bool TryDeferCreatedZdoObservation(
        PendingDespawnObservation observation,
        ZDO zdo,
        float nowRealtime,
        out PendingDespawnObservation deferredObservation)
    {
        deferredObservation = observation;
        if (observation.Source != DespawnObservationSource.CreatedZdo ||
            zdo.GetPrefab() != 0)
        {
            return false;
        }

        float expireAt = observation.ExpireAt > 0f
            ? observation.ExpireAt
            : nowRealtime + CreatedZdoObservationRetryTimeoutSeconds;
        if (nowRealtime >= expireAt)
        {
            _createdZdoProducerDeferredExpiredCount++;
            return false;
        }

        deferredObservation = new PendingDespawnObservation(
            observation.ZdoId,
            observation.PrefabHashHint,
            observation.PrefabNameHint,
            observation.Source,
            nowRealtime + CreatedZdoObservationRetryIntervalSeconds,
            expireAt,
            observation.RetryCount + 1);
        if (observation.RetryCount == 0)
        {
            _createdZdoProducerDeferredCount++;
        }

        return true;
    }

    private static void RecordSkippedIneligibleProducer(DespawnProducerSkipSource source)
    {
        switch (source)
        {
            case DespawnProducerSkipSource.CreatedZdo:
                _skippedCreatedZdoProducerCount++;
                break;
            case DespawnProducerSkipSource.LoadedCharacter:
                _skippedLoadedCharacterProducerCount++;
                break;
            case DespawnProducerSkipSource.DetachPersist:
                _skippedDetachPersistProducerCount++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, null);
        }
    }

    private static void ProcessSkippedIneligibleProducerDiagnostics(float nowRealtime)
    {
        if (!PluginSettingsFacade.IsDespawnDiagnosticsEnabled())
        {
            ResetSkippedIneligibleProducerDiagnostics();
            _nextIneligibleProducerSummaryAt = nowRealtime + DespawnIneligibleProducerSummaryIntervalSeconds;
            return;
        }

        if (_nextIneligibleProducerSummaryAt <= 0f)
        {
            _nextIneligibleProducerSummaryAt = nowRealtime + DespawnIneligibleProducerSummaryIntervalSeconds;
            return;
        }

        if (_nextIneligibleProducerSummaryAt > nowRealtime)
        {
            return;
        }

        _nextIneligibleProducerSummaryAt = nowRealtime + DespawnIneligibleProducerSummaryIntervalSeconds;
        int totalSkipped = _skippedCreatedZdoProducerCount + _skippedLoadedCharacterProducerCount + _skippedDetachPersistProducerCount;
        bool hasCreatedZdoSummary =
            _createdZdoProducerQueuedCount > 0 ||
            _createdZdoProducerDeferredCount > 0 ||
            _createdZdoProducerDeferredExpiredCount > 0;
        if (totalSkipped <= 0)
        {
            if (!hasCreatedZdoSummary &&
                _loadedCharacterProducerAttemptCount <= 0)
            {
                return;
            }
        }

        if (hasCreatedZdoSummary)
        {
            LogDiagnostics(
                $"Created-ZDO despawn producer over the last {DespawnIneligibleProducerSummaryIntervalSeconds:0.#}s: queued={_createdZdoProducerQueuedCount} deferred={_createdZdoProducerDeferredCount} expired={_createdZdoProducerDeferredExpiredCount} ineligible={_skippedCreatedZdoProducerCount}.");
        }

        if (totalSkipped > 0)
        {
            LogDiagnostics(
                $"Skipped ineligible despawn producer candidates over the last {DespawnIneligibleProducerSummaryIntervalSeconds:0.#}s: createdZdo={_skippedCreatedZdoProducerCount} loadedCharacter={_skippedLoadedCharacterProducerCount} resetZdo={_skippedDetachPersistProducerCount}.");
        }

        if (_loadedCharacterProducerAttemptCount > 0)
        {
            LogDiagnostics(
                $"Loaded-character despawn producer over the last {DespawnIneligibleProducerSummaryIntervalSeconds:0.#}s: attempts={_loadedCharacterProducerAttemptCount} queued={_loadedCharacterProducerQueuedCount} missingCharacter={_loadedCharacterProducerMissingCharacterCount} deadCharacter={_loadedCharacterProducerDeadCharacterCount} nviewMissing={_loadedCharacterProducerMissingNviewCount} nviewInvalid={_loadedCharacterProducerInvalidNviewCount} zdoMissing={_loadedCharacterProducerMissingZdoCount} deadZdo={_loadedCharacterProducerDeadZdoCount} prefabMissing={_loadedCharacterProducerMissingPrefabNameCount} ineligible={_skippedLoadedCharacterProducerCount}.");
        }

        ResetSkippedIneligibleProducerDiagnostics();
    }

    private static void ResetSkippedIneligibleProducerDiagnostics()
    {
        _nextIneligibleProducerSummaryAt = 0f;
        _createdZdoProducerQueuedCount = 0;
        _createdZdoProducerDeferredCount = 0;
        _createdZdoProducerDeferredExpiredCount = 0;
        _skippedCreatedZdoProducerCount = 0;
        _skippedLoadedCharacterProducerCount = 0;
        _skippedDetachPersistProducerCount = 0;
        _loadedCharacterProducerAttemptCount = 0;
        _loadedCharacterProducerQueuedCount = 0;
        _loadedCharacterProducerMissingCharacterCount = 0;
        _loadedCharacterProducerDeadCharacterCount = 0;
        _loadedCharacterProducerMissingNviewCount = 0;
        _loadedCharacterProducerInvalidNviewCount = 0;
        _loadedCharacterProducerMissingZdoCount = 0;
        _loadedCharacterProducerDeadZdoCount = 0;
        _loadedCharacterProducerMissingPrefabNameCount = 0;
    }

    private static long QuantizeScheduledCheck(double scheduledTime)
    {
        return Math.Max(0L, (long)Math.Ceiling(scheduledTime * 1000d));
    }

    private static double GetIdleCheckIntervalSeconds()
    {
        return DespawnIdleCheckIntervalSeconds;
    }

    private static double GetCountdownCheckIntervalSeconds()
    {
        return DespawnCountdownCheckIntervalSeconds;
    }

    private static double GetCurrentDespawnClockSeconds()
    {
        return ZNet.instance?.GetTimeSeconds() ?? Time.time;
    }

    private static bool ShouldAnnounceDespawnRemaining(int remainingSeconds)
    {
        if (remainingSeconds <= 0)
        {
            return false;
        }

        if (remainingSeconds <= DespawnFinalCountdownSeconds)
        {
            return true;
        }

        return remainingSeconds % DespawnReminderIntervalSeconds == 0;
    }

    private static void SendDespawnMessage(long playerId, string message)
    {
        if (playerId == 0L || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (DropNSpawnPlugin.IsRuntimeServer())
        {
            if (TrySendServerDespawnMessage(playerId, message))
            {
                return;
            }

            Player? fallbackPlayer = Player.GetPlayer(playerId);
            if (fallbackPlayer == null)
            {
                LogDiagnostics($"Message skipped because server recipient could not be resolved. recipientId={playerId} message='{message}'.");
                return;
            }

            LogDiagnostics(
                $"Message server fallback delivered locally. recipientId={playerId} name='{fallbackPlayer.GetPlayerName()}' characterId={fallbackPlayer.GetZDOID()} message='{message}'.");
            fallbackPlayer.Message(MessageHud.MessageType.TopLeft, message);
            return;
        }

        Player? player = Player.GetPlayer(playerId);
        if (player == null)
        {
            LogDiagnostics($"Message skipped because recipient player could not be resolved. playerId={playerId} message='{message}'.");
            return;
        }

        if (player.gameObject == null || player.IsDead())
        {
            LogDiagnostics(
                $"Message skipped because recipient player is unavailable. playerId={playerId} name='{player.GetPlayerName()}' dead={player.IsDead()} message='{message}'.");
            return;
        }

        LogDiagnostics(
            $"Message delivered locally. playerId={playerId} name='{player.GetPlayerName()}' characterId={player.GetZDOID()} message='{message}'.");
        player.Message(MessageHud.MessageType.TopLeft, message);
    }

    private static bool IsDespawnMessageRecipientAvailable(long recipientId)
    {
        if (recipientId == 0L)
        {
            return false;
        }

        if (DropNSpawnPlugin.IsRuntimeServer())
        {
            if (IsValidMessageTargetPeerId(recipientId))
            {
                return true;
            }

            Player? player = Player.GetPlayer(recipientId);
            return player != null &&
                   TryResolveMessageTargetPeerId(player, out _, out _);
        }

        Player? localPlayer = Player.GetPlayer(recipientId);
        return localPlayer != null &&
               localPlayer.gameObject != null &&
               !localPlayer.IsDead();
    }

    private static bool TrySendServerDespawnMessage(long recipientId, string message)
    {
        if (ZRoutedRpc.instance == null)
        {
            return false;
        }

        if (IsValidMessageTargetPeerId(recipientId))
        {
            string recipientName = ResolveRecipientName(recipientId);
            bool peerReady = recipientId == ZNet.GetUID() || ZNet.instance?.GetPeer(recipientId)?.IsReady() == true;
            LogDiagnostics(
                $"Sending ShowMessage RPC. recipientId={recipientId} name='{recipientName}' peerReady={peerReady} via=recipientId message='{message}'.");
            ZRoutedRpc.instance.InvokeRoutedRPC(
                recipientId,
                "ShowMessage",
                (int)MessageHud.MessageType.TopLeft,
                message);
            return true;
        }

        Player? player = Player.GetPlayer(recipientId);
        if (player != null &&
            TryResolveMessageTargetPeerId(player, out long targetPeerId, out string resolutionSource))
        {
            bool peerReady = targetPeerId == ZNet.GetUID() || ZNet.instance?.GetPeer(targetPeerId)?.IsReady() == true;
            LogDiagnostics(
                $"Sending ShowMessage RPC. recipientId={recipientId} name='{player.GetPlayerName()}' characterId={player.GetZDOID()} targetPeerId={targetPeerId} peerReady={peerReady} via={resolutionSource} message='{message}'.");
            ZRoutedRpc.instance.InvokeRoutedRPC(
                targetPeerId,
                "ShowMessage",
                (int)MessageHud.MessageType.TopLeft,
                message);
            return true;
        }

        return false;
    }

    private static bool TryResolveMessageTargetPeerId(Player player, out long targetPeerId, out string resolutionSource)
    {
        targetPeerId = 0L;
        resolutionSource = "none";
        if (player == null)
        {
            return false;
        }

        ZDOID characterId = player.GetZDOID();
        long candidatePeerId = characterId.UserID;
        if (IsValidMessageTargetPeerId(candidatePeerId))
        {
            targetPeerId = candidatePeerId;
            resolutionSource = "characterZdoUserId";
            return true;
        }

        List<ZNetPeer>? peers = ZNet.instance?.GetPeers();
        if (peers != null)
        {
            foreach (ZNetPeer peer in peers)
            {
                if (peer != null &&
                    peer.IsReady() &&
                    peer.m_characterID == characterId)
                {
                    targetPeerId = peer.m_uid;
                    resolutionSource = "peerCharacterId";
                    return true;
                }
            }
        }

        string playerName = player.GetPlayerName();
        if (!string.IsNullOrWhiteSpace(playerName))
        {
            ZNetPeer? namedPeer = ZNet.instance?.GetPeerByPlayerName(playerName);
            if (namedPeer != null && namedPeer.IsReady())
            {
                targetPeerId = namedPeer.m_uid;
                resolutionSource = "playerName";
                return true;
            }
        }

        return false;
    }

    private static bool IsValidMessageTargetPeerId(long peerId)
    {
        if (peerId == 0L)
        {
            return false;
        }

        if (peerId == ZNet.GetUID())
        {
            return true;
        }

        return ZNet.instance?.GetPeer(peerId)?.IsReady() == true;
    }

    private static string ResolveRecipientName(long recipientId)
    {
        if (recipientId == ZNet.GetUID())
        {
            return Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "local";
        }

        ZNetPeer? peer = ZNet.instance?.GetPeer(recipientId);
        if (peer != null && !string.IsNullOrWhiteSpace(peer.m_playerName))
        {
            return peer.m_playerName;
        }

        return recipientId.ToString();
    }

    private static string BuildDespawnStartMessage(string displayName, int remainingSeconds)
    {
        return $"{displayName} will despawn in {remainingSeconds}s unless someone returns.";
    }

    private static string BuildDespawnReminderMessage(string displayName, int remainingSeconds)
    {
        return $"{displayName} will despawn in {remainingSeconds}s.";
    }

    private static string BuildDespawnCanceledMessage(string displayName)
    {
        return $"{displayName} despawn canceled.";
    }

    private static string GetDisplayName(Character? character)
    {
        if (character == null)
        {
            return "Target";
        }

        string hoverName = character.GetHoverName();
        if (!string.IsNullOrWhiteSpace(hoverName))
        {
            return hoverName;
        }

        if (!string.IsNullOrWhiteSpace(character.m_name))
        {
            return Localization.instance != null
                ? Localization.instance.Localize(character.m_name)
                : character.m_name;
        }

        return character.gameObject != null && !string.IsNullOrWhiteSpace(character.gameObject.name)
            ? character.gameObject.name
            : "Target";
    }

    internal static bool IsManagedDespawnMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        string text = message!;
        return text.Contains(" will despawn in ", StringComparison.OrdinalIgnoreCase) ||
               text.Contains(" despawn canceled.", StringComparison.OrdinalIgnoreCase);
    }

    internal static void LogDiagnostics(string message)
    {
        if (!PluginSettingsFacade.IsDespawnDiagnosticsEnabled())
        {
            return;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"[Despawn] {message}");
    }

    private static string FormatPosition(Vector3 position)
    {
        return $"{position.x:0.##},{position.y:0.##},{position.z:0.##}";
    }
}
