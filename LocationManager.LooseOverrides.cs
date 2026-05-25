using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class LocationManager
{
    internal static void LogLocationProxySpawnDiagnostics(LocationProxy? proxy, GameObject? instance, int spawnCount, bool spawnResult)
    {
        if (!PluginSettingsFacade.IsOfferingBowlDiagnosticsEnabled() || proxy == null)
        {
            return;
        }

        lock (Sync)
        {
            ZNetView? nview = proxy.GetComponent<ZNetView>();
            Location[] locations = nview != null
                ? nview.GetComponentsInChildren<Location>(true)
                : Array.Empty<Location>();
            OfferingBowl[] offeringBowls = nview != null
                ? nview.GetComponentsInChildren<OfferingBowl>(true)
                : Array.Empty<OfferingBowl>();
            string instancePath = instance != null ? BuildOfferingBowlDiagnosticPath(instance.transform) : "(none)";
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
                $"LocationProxy diag stage=spawn#{spawnCount.ToString(CultureInfo.InvariantCulture)} proxyId={proxy.GetInstanceID().ToString(CultureInfo.InvariantCulture)} result={spawnResult} proxyPath='{BuildOfferingBowlDiagnosticPath(proxy.transform)}' instanceId={(instance != null ? instance.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "(none)")} instancePath='{instancePath}' locationsUnderSameNView={locations.Length.ToString(CultureInfo.InvariantCulture)} bowlsUnderSameNView={offeringBowls.Length.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    internal static void LogOfferingBowlDiagnostics(OfferingBowl? offeringBowl, string stage)
    {
        if (!PluginSettingsFacade.IsOfferingBowlDiagnosticsEnabled() || offeringBowl == null)
        {
            return;
        }

        lock (Sync)
        {
            Transform transform = offeringBowl.transform;
            Location? location = offeringBowl.GetComponentInParent<Location>(true);
            LocationProxy? proxy = offeringBowl.GetComponentInParent<LocationProxy>(true);
            ZNetView? nview = offeringBowl.GetComponentInParent<ZNetView>(true);
            int sameGameObjectCount = offeringBowl.GetComponents<OfferingBowl>().Length;
            int sameNViewCount = nview != null ? nview.GetComponentsInChildren<OfferingBowl>(true).Length : 0;
            string resolvedPrefab = "";
            string rootPath = "(unknown)";
            if (AltarItemStandHoverInfoFormatter.TryResolveOfferingBowlContext(offeringBowl, out string locationPrefab, out Transform root))
            {
                resolvedPrefab = locationPrefab;
                rootPath = BuildOfferingBowlDiagnosticPath(root);
            }

            string offeringBowlPath = BuildOfferingBowlDiagnosticPath(transform);
            string nviewPath = nview != null ? BuildOfferingBowlDiagnosticPath(nview.transform) : "(none)";
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
                $"OfferingBowl diag stage={stage} bowlId={offeringBowl.GetInstanceID().ToString(CultureInfo.InvariantCulture)} object='{offeringBowl.gameObject.name}' path='{offeringBowlPath}' sameGameObjectBowls={sameGameObjectCount.ToString(CultureInfo.InvariantCulture)} parentNViewId={(nview != null ? nview.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "(none)")} parentNViewPath='{nviewPath}' bowlsUnderSameNView={sameNViewCount.ToString(CultureInfo.InvariantCulture)} locationId={(location != null ? location.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "(none)")} proxyId={(proxy != null ? proxy.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "(none)")} resolvedPrefab='{resolvedPrefab}' resolvedRoot='{rootPath}' useItemStands={offeringBowl.m_useItemStands} itemStandPrefix='{offeringBowl.m_itemStandPrefix}' itemStandMaxRange={offeringBowl.m_itemstandMaxRange.ToString(CultureInfo.InvariantCulture)}");
            LogOfferingBowlStructureDiagnostics(stage, nview, proxy);
        }
    }

    internal static void LogOfferingBowlItemFlowDiagnostics(
        OfferingBowl? offeringBowl,
        Humanoid? user,
        ItemDrop.ItemData? item,
        string stage,
        int countBefore,
        int countAfter,
        bool? result = null)
    {
        if (!PluginSettingsFacade.IsOfferingBowlDiagnosticsEnabled() || offeringBowl == null)
        {
            return;
        }

        lock (Sync)
        {
            ZNetView? nview = offeringBowl.GetComponentInParent<ZNetView>(true);
            int sameGameObjectCount = offeringBowl.GetComponents<OfferingBowl>().Length;
            int sameNViewCount = nview != null ? nview.GetComponentsInChildren<OfferingBowl>(true).Length : 0;
            string itemName = item?.m_shared?.m_name ?? "(none)";
            string userName = user?.GetHoverName() ?? user?.name ?? "(none)";
            string offeringBowlPath = BuildOfferingBowlDiagnosticPath(offeringBowl.transform);
            string nviewPath = nview != null ? BuildOfferingBowlDiagnosticPath(nview.transform) : "(none)";
            string resultText = result.HasValue ? result.Value.ToString() : "(n/a)";
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
                $"OfferingBowl diag itemflow stage={stage} bowlId={offeringBowl.GetInstanceID().ToString(CultureInfo.InvariantCulture)} object='{offeringBowl.gameObject.name}' path='{offeringBowlPath}' parentNViewId={(nview != null ? nview.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "(none)")} parentNViewPath='{nviewPath}' sameGameObjectBowls={sameGameObjectCount.ToString(CultureInfo.InvariantCulture)} bowlsUnderSameNView={sameNViewCount.ToString(CultureInfo.InvariantCulture)} item='{itemName}' bossItems={offeringBowl.m_bossItems.ToString(CultureInfo.InvariantCulture)} countBefore={countBefore.ToString(CultureInfo.InvariantCulture)} countAfter={countAfter.ToString(CultureInfo.InvariantCulture)} result={resultText} user='{userName}' useItemStands={offeringBowl.m_useItemStands}");
        }
    }

    private static void LogOfferingBowlStructureDiagnostics(string stage, ZNetView? nview, LocationProxy? proxy)
    {
        if (nview == null)
        {
            return;
        }

        OfferingBowl[] bowls = nview.GetComponentsInChildren<OfferingBowl>(true);
        Location[] locations = nview.GetComponentsInChildren<Location>(true);
        if (bowls.Length <= 1 && locations.Length <= 1)
        {
            return;
        }

        string diagnosticKey = string.Join(
            "|",
            "structure",
            nview.GetInstanceID().ToString(CultureInfo.InvariantCulture),
            bowls.Length.ToString(CultureInfo.InvariantCulture),
            locations.Length.ToString(CultureInfo.InvariantCulture));
        if (!OfferingBowlDiagnosticLogs.Add(diagnosticKey))
        {
            return;
        }

        Transform root = nview.transform;
        string bowlList = FormatDiagnosticEntries(
            bowls
                .Where(bowl => bowl != null)
                .Select(bowl => DescribeOfferingBowlStructureEntry(root, bowl)));
        string locationList = FormatDiagnosticEntries(
            locations
                .Where(location => location != null)
                .Select(location => DescribeLocationStructureEntry(root, location)));
        string duplicateBowlPaths = FormatDiagnosticEntries(
            bowls
                .Where(bowl => bowl != null)
                .Select(bowl => GetRelativePath(root, bowl.transform))
                .GroupBy(path => path, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key} x{group.Count().ToString(CultureInfo.InvariantCulture)}"));
        string duplicateLocationPaths = FormatDiagnosticEntries(
            locations
                .Where(location => location != null)
                .Select(location => GetRelativePath(root, location.transform))
                .GroupBy(path => path, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key} x{group.Count().ToString(CultureInfo.InvariantCulture)}"));
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            $"OfferingBowl diag structure stage={stage} parentNViewId={nview.GetInstanceID().ToString(CultureInfo.InvariantCulture)} parentNViewPath='{BuildOfferingBowlDiagnosticPath(root)}' proxyId={(proxy != null ? proxy.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "(none)")} offeringBowls={bowls.Length.ToString(CultureInfo.InvariantCulture)} locations={locations.Length.ToString(CultureInfo.InvariantCulture)} duplicateBowlPaths='{duplicateBowlPaths}' duplicateLocationPaths='{duplicateLocationPaths}' bowlEntries='{bowlList}' locationEntries='{locationList}'");
    }

    private static string DescribeOfferingBowlStructureEntry(Transform root, OfferingBowl offeringBowl)
    {
        string relativePath = GetRelativePath(root, offeringBowl.transform);
        string resolvedPrefab = "";
        if (!AltarItemStandHoverInfoFormatter.TryResolveOfferingBowlContext(offeringBowl, out resolvedPrefab, out _))
        {
            resolvedPrefab = "";
        }

        Location? location = offeringBowl.GetComponentInParent<Location>(true);
        return $"{relativePath}#bowl={offeringBowl.GetInstanceID().ToString(CultureInfo.InvariantCulture)}#loc={(location != null ? location.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "(none)")}#prefab={resolvedPrefab}";
    }

    private static string DescribeLocationStructureEntry(Transform root, Location location)
    {
        string relativePath = GetRelativePath(root, location.transform);
        string resolvedPrefab = "";
        if (!TryResolveRuntimeLocationPrefabName(location, out resolvedPrefab))
        {
            resolvedPrefab = "";
        }

        return $"{relativePath}#loc={location.GetInstanceID().ToString(CultureInfo.InvariantCulture)}#prefab={resolvedPrefab}";
    }

    private static string FormatDiagnosticEntries(IEnumerable<string> entries, int maxEntries = 12)
    {
        List<string> materialized = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Take(maxEntries + 1)
            .ToList();
        if (materialized.Count == 0)
        {
            return "(none)";
        }

        bool truncated = materialized.Count > maxEntries;
        if (truncated)
        {
            materialized.RemoveAt(materialized.Count - 1);
        }

        string joined = string.Join(" || ", materialized);
        return truncated ? $"{joined} || ...(truncated)" : joined;
    }

    internal static void QueueLooseItemStandOverride(ItemStand? itemStand)
    {
        lock (Sync)
        {
            QueueOrRestoreLooseItemStandOverrideInternal(itemStand);
        }
    }

    internal static void EnsureLooseItemStandOverride(ItemStand? itemStand)
    {
        lock (Sync)
        {
            if (!_initialized)
            {
                Initialize();
            }

            if (!IsGameDataReady() || DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
            {
                return;
            }

            if (itemStand == null || itemStand.gameObject == null || itemStand.GetComponentInParent<Location>() != null)
            {
                return;
            }

            if (AltarItemStandHoverInfoFormatter.TryGetRelevantOfferingBowl(itemStand, out OfferingBowl? offeringBowl) &&
                offeringBowl != null)
            {
                TryApplyLooseOfferingBowlOverrideInternal(offeringBowl);
                return;
            }

            RestoreTrackedLooseItemStand(itemStand);
        }
    }

    internal static void QueueLooseOfferingBowlOverride(OfferingBowl? offeringBowl)
    {
        lock (Sync)
        {
            QueueLooseOfferingBowlOverrideInternal(offeringBowl);
        }
    }

    internal static void EnsureLooseOfferingBowlOverride(OfferingBowl? offeringBowl)
    {
        lock (Sync)
        {
            if (!_initialized)
            {
                Initialize();
            }

            if (!IsGameDataReady() || DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
            {
                return;
            }

            TryApplyLooseOfferingBowlOverrideInternal(offeringBowl);
        }
    }

    internal static void QueueLooseOfferingBowlOverridesUnderRoot(GameObject? rootObject)
    {
        lock (Sync)
        {
            QueueLooseOfferingBowlOverridesUnderRootInternal(rootObject);
        }
    }

    internal static void TryApplyLooseItemStandOverride(ItemStand? itemStand)
    {
        QueueLooseItemStandOverride(itemStand);
    }

    internal enum OfferingBowlBlockReason
    {
        None = 0,
        SameBossNearby,
        RespawnCooldownActive,
    }

    internal readonly struct OfferingBowlBlockResult
    {
        internal static OfferingBowlBlockResult None => default;

        public OfferingBowlBlockResult(bool blocked, OfferingBowlBlockReason reason)
        {
            Blocked = blocked;
            Reason = reason;
        }

        public bool Blocked { get; }
        public OfferingBowlBlockReason Reason { get; }
    }

    internal static OfferingBowlBlockResult EvaluateOfferingBowlBlock(OfferingBowl offeringBowl)
    {
        lock (Sync)
        {
            if (offeringBowl == null || ZNet.instance == null)
            {
                return OfferingBowlBlockResult.None;
            }

            if (BossRulesManager.ShouldBlockConfiguredSameBossSpawn(
                    offeringBowl.m_bossPrefab,
                    offeringBowl.transform.position))
            {
                return new OfferingBowlBlockResult(true, OfferingBowlBlockReason.SameBossNearby);
            }

            OfferingBowlRuntimeState? state = offeringBowl.GetComponent<OfferingBowlRuntimeState>();
            if (state == null)
            {
                return OfferingBowlBlockResult.None;
            }

            if (state.RespawnMinutes <= 0f)
            {
                return OfferingBowlBlockResult.None;
            }

            long lastUseTicks = GetOfferingBowlLastUseTicks(offeringBowl, state);
            if (lastUseTicks <= 0L)
            {
                return OfferingBowlBlockResult.None;
            }

            TimeSpan elapsed = ZNet.instance.GetTime() - new DateTime(lastUseTicks);
            if (elapsed.TotalMinutes >= state.RespawnMinutes)
            {
                return OfferingBowlBlockResult.None;
            }

            return new OfferingBowlBlockResult(true, OfferingBowlBlockReason.RespawnCooldownActive);
        }
    }

    internal static void NotifyOfferingBowlBlocked(OfferingBowl offeringBowl, Humanoid? user, OfferingBowlBlockResult result)
    {
        if (offeringBowl == null || user == null || !result.Blocked)
        {
            return;
        }

        switch (result.Reason)
        {
            case OfferingBowlBlockReason.SameBossNearby:
            case OfferingBowlBlockReason.RespawnCooldownActive:
                user.Message(MessageHud.MessageType.Center, Localization.instance.Localize(offeringBowl.m_cantOfferText));
                break;
            case OfferingBowlBlockReason.None:
            default:
                break;
        }
    }

    internal static void MarkOfferingBowlUsed(OfferingBowl offeringBowl)
    {
        lock (Sync)
        {
            if (offeringBowl == null || ZNet.instance == null)
            {
                return;
            }

            OfferingBowlRuntimeState? state = offeringBowl.GetComponent<OfferingBowlRuntimeState>();
            if (state == null || state.RespawnMinutes <= 0f)
            {
                return;
            }

            long nowTicks = ZNet.instance.GetTime().Ticks;
            state.LocalLastUseTicks = nowTicks;

            ZNetView? view = offeringBowl.GetComponentInParent<ZNetView>();
            if (view == null || !view.IsValid())
            {
                return;
            }

            if (!view.IsOwner())
            {
                view.ClaimOwnership();
            }

            if (!view.IsOwner())
            {
                return;
            }

            view.GetZDO().Set(OfferingBowlLastUseTicksKey, nowTicks);
        }
    }

    internal static void BeginOfferingBowlBossSpawnAttempt(OfferingBowl offeringBowl, Vector3 spawnPoint)
    {
        lock (Sync)
        {
            if (offeringBowl == null)
            {
                return;
            }

            OfferingBowlRuntimeState? state = offeringBowl.GetComponent<OfferingBowlRuntimeState>();
            if (PluginSettingsFacade.IsLocationDomainEnabled() &&
                state?.SpawnPayload != null)
            {
                ExpandWorldSpawnDataSupport.InitializeSpawn(offeringBowl.m_bossPrefab, spawnPoint, state.SpawnPayload);
            }
        }
    }

    internal static void FinalizeOfferingBowlBossSpawnAttempt(OfferingBowl offeringBowl, Vector3 spawnPoint)
    {
        lock (Sync)
        {
            if (offeringBowl == null)
            {
                return;
            }

            OfferingBowlRuntimeState? state = offeringBowl.GetComponent<OfferingBowlRuntimeState>();

            if (PluginSettingsFacade.IsLocationDomainEnabled() &&
                state?.SpawnPayload?.HasObjects == true)
            {
                ExpandWorldSpawnDataSupport.SpawnObjects(spawnPoint, state.SpawnPayload);
            }
        }
    }

    private static void QueueLooseOfferingBowlOverridesUnderRootInternal(GameObject? rootObject)
    {
        if (rootObject == null)
        {
            return;
        }

        foreach (OfferingBowl offeringBowl in rootObject.GetComponentsInChildren<OfferingBowl>(true))
        {
            QueueLooseOfferingBowlOverrideInternal(offeringBowl);
        }
    }

    private static void QueueLooseOfferingBowlOverrideInternal(OfferingBowl? offeringBowl)
    {
        if (offeringBowl == null || offeringBowl.gameObject == null)
        {
            return;
        }

        int offeringBowlInstanceId = offeringBowl.GetInstanceID();
        if (!PendingLooseOfferingBowlOverrideIds.Add(offeringBowlInstanceId))
        {
            return;
        }

        PendingLooseOfferingBowlOverrides.Enqueue(new PendingLooseOfferingBowlOverride(offeringBowl, offeringBowlInstanceId, _reconcileQueueEpoch));
    }

    private static bool HasPendingLooseLocationOverrideWorkLocked()
    {
        return PendingLooseOfferingBowlOverrides.Count > 0;
    }

    private static bool TryProcessPendingLooseLocationOverrideLocked()
    {
        while (PendingLooseOfferingBowlOverrides.Count > 0)
        {
            if (!PendingLooseOfferingBowlOverrides.TryDequeue(out PendingLooseOfferingBowlOverride queuedOverride))
            {
                continue;
            }

            PendingLooseOfferingBowlOverrideIds.Remove(queuedOverride.OfferingBowlInstanceId);
            if (queuedOverride.Epoch != _reconcileQueueEpoch || queuedOverride.OfferingBowl == null)
            {
                continue;
            }

            if (!_initialized)
            {
                Initialize();
            }

            TryApplyLooseOfferingBowlOverrideInternal(queuedOverride.OfferingBowl);
            return true;
        }

        return false;
    }

    private static void RefreshTrackedLooseItemStands(HashSet<string> prefabs)
    {
        CleanupLooseItemStandSnapshots();

        foreach (ItemStand itemStand in GetTrackedLooseItemStands(prefabs))
        {
            QueueOrRestoreLooseItemStandOverrideInternal(itemStand);
        }
    }

    private static IEnumerable<ItemStand> GetTrackedLooseItemStands(HashSet<string> dirtyPrefabs)
    {
        CleanupTrackedLooseItemStandPrefabs();
        List<KeyValuePair<ItemStand, string>> trackedItemStands = TrackedLooseItemStandPrefabs.ToList();
        foreach ((ItemStand itemStand, string prefabName) in trackedItemStands)
        {
            if (itemStand != null && itemStand.gameObject != null && dirtyPrefabs.Contains(prefabName))
            {
                yield return itemStand;
            }
        }
    }

    private static void CleanupTrackedLooseItemStandPrefabs()
    {
        List<ItemStand>? deadItemStands = null;
        foreach (ItemStand itemStand in TrackedLooseItemStandPrefabs.Keys)
        {
            if (itemStand == null || itemStand.gameObject == null)
            {
                deadItemStands ??= new List<ItemStand>();
                deadItemStands.Add(itemStand!);
            }
        }

        if (deadItemStands == null)
        {
            return;
        }

        foreach (ItemStand deadItemStand in deadItemStands)
        {
            TrackedLooseItemStandPrefabs.Remove(deadItemStand);
            LooseItemStandAuthoredPathsByInstance.Remove(deadItemStand);
        }
    }

    private static void QueueOrRestoreLooseItemStandOverrideInternal(ItemStand? itemStand)
    {
        if (itemStand == null || itemStand.gameObject == null || !IsGameDataReady())
        {
            return;
        }

        if (itemStand.GetComponentInParent<Location>() != null)
        {
            return;
        }

        if (!AltarItemStandHoverInfoFormatter.TryGetRelevantOfferingBowl(itemStand, out OfferingBowl? offeringBowl) ||
            offeringBowl == null)
        {
            RestoreTrackedLooseItemStand(itemStand);
            return;
        }

        QueueLooseOfferingBowlOverrideInternal(offeringBowl);
    }

    private static void TryApplyLooseOfferingBowlOverrideInternal(OfferingBowl? offeringBowl)
    {
        if (offeringBowl == null || !IsGameDataReady())
        {
            return;
        }

        CleanupLooseItemStandSnapshots();

        List<ItemStand> detachedRelevantItemStands = AltarItemStandHoverInfoFormatter.FindRelevantItemStands(offeringBowl)
            .Where(itemStand => itemStand != null && itemStand.GetComponentInParent<Location>() == null)
            .ToList();
        LooseOfferingBowlOverrideMode overrideMode = LooseOfferingBowlOverrideMode.RestoreOnly;
        string prefabName = "";
        Transform? root = null;
        List<CompiledLocationEntryPlan>? entryPlans = null;

        if (CanUseCurrentRuntimeState() &&
            offeringBowl.m_useItemStands &&
            PluginSettingsFacade.IsLocationDomainEnabled() &&
            AltarItemStandHoverInfoFormatter.TryResolveOfferingBowlContext(offeringBowl, out prefabName, out Transform resolvedRoot))
        {
            root = resolvedRoot;
            EnsureRuntimeConfigurationState();
            if (_runtimeConfigurationState.PlansByPrefab.TryGetValue(prefabName, out CompiledLocationPrefabPlan? prefabPlan) &&
                prefabPlan.LooseItemStandPlans.Count > 0 &&
                detachedRelevantItemStands.Count > 0)
            {
                entryPlans = prefabPlan.LooseItemStandPlans;
                overrideMode = LooseOfferingBowlOverrideMode.Apply;
            }
        }

        LooseOfferingBowlOverrideStamp overrideStamp = BuildLooseOfferingBowlOverrideStamp(
            overrideMode,
            root,
            prefabName,
            entryPlans?.Count ?? 0,
            detachedRelevantItemStands);
        if (HasCurrentLooseOfferingBowlOverrideStamp(offeringBowl, overrideStamp))
        {
            return;
        }

        foreach (ItemStand itemStand in detachedRelevantItemStands)
        {
            RestoreTrackedLooseItemStand(itemStand);
        }

        if (overrideMode != LooseOfferingBowlOverrideMode.Apply || root == null || entryPlans == null)
        {
            RecordLooseOfferingBowlOverrideStamp(offeringBowl, overrideStamp);
            return;
        }

        if (PluginSettingsFacade.IsOfferingBowlDiagnosticsEnabled())
        {
            string diagnosticKey = $"looseApply|{offeringBowl.GetInstanceID().ToString(CultureInfo.InvariantCulture)}|{prefabName}|{entryPlans.Count.ToString(CultureInfo.InvariantCulture)}";
            if (OfferingBowlDiagnosticLogs.Add(diagnosticKey))
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
                    $"OfferingBowl diag stage=loose_apply bowlId={offeringBowl.GetInstanceID().ToString(CultureInfo.InvariantCulture)} object='{offeringBowl.gameObject.name}' resolvedPrefab='{prefabName}' root='{BuildOfferingBowlDiagnosticPath(root)}' matchingEntries={entryPlans.Count.ToString(CultureInfo.InvariantCulture)} useItemStands={offeringBowl.m_useItemStands}");
            }
        }

        TryStampLooseItemStandAuthoredPaths(offeringBowl, prefabName, detachedRelevantItemStands);
        overrideStamp = BuildLooseOfferingBowlOverrideStamp(
            overrideMode,
            root,
            prefabName,
            entryPlans.Count,
            detachedRelevantItemStands);
        foreach (ItemStand itemStand in detachedRelevantItemStands)
        {
            ApplyCompiledLooseItemStandPlansForContext(itemStand, entryPlans, prefabName, root, offeringBowl);
        }

        RecordLooseOfferingBowlOverrideStamp(offeringBowl, overrideStamp);
    }

    private static LooseOfferingBowlOverrideStamp BuildLooseOfferingBowlOverrideStamp(
        LooseOfferingBowlOverrideMode overrideMode,
        Transform? root,
        string prefabName,
        int entryPlanCount,
        IReadOnlyList<ItemStand> detachedRelevantItemStands)
    {
        return new LooseOfferingBowlOverrideStamp(
            _reconcileQueueEpoch,
            AltarItemStandHoverInfoFormatter.GetRegistryVersion(),
            overrideMode,
            root != null ? root.GetInstanceID() : 0,
            prefabName,
            _configurationSignature,
            entryPlanCount,
            detachedRelevantItemStands.Count,
            ComputeLooseRelevantItemStandSignature(detachedRelevantItemStands));
    }

    private static int ComputeLooseRelevantItemStandSignature(IReadOnlyList<ItemStand> detachedRelevantItemStands)
    {
        HashCode hash = new();
        hash.Add(detachedRelevantItemStands.Count);
        foreach (ItemStand itemStand in detachedRelevantItemStands)
        {
            hash.Add(itemStand != null ? itemStand.GetInstanceID() : 0);
            if (itemStand != null &&
                LooseItemStandAuthoredPathsByInstance.TryGetValue(itemStand, out string? authoredPath))
            {
                hash.Add(authoredPath, StringComparer.Ordinal);
            }
            else
            {
                hash.Add("", StringComparer.Ordinal);
            }
        }

        return hash.ToHashCode();
    }

    private static bool HasCurrentLooseOfferingBowlOverrideStamp(
        OfferingBowl offeringBowl,
        LooseOfferingBowlOverrideStamp overrideStamp)
    {
        return LooseOfferingBowlOverrideStates.TryGetValue(offeringBowl, out LooseOfferingBowlOverrideState? state) &&
               state.HasLastAppliedStamp &&
               state.LastAppliedStamp.Equals(overrideStamp);
    }

    private static void RecordLooseOfferingBowlOverrideStamp(
        OfferingBowl offeringBowl,
        LooseOfferingBowlOverrideStamp overrideStamp)
    {
        LooseOfferingBowlOverrideState state = LooseOfferingBowlOverrideStates.GetOrCreateValue(offeringBowl);
        state.LastAppliedStamp = overrideStamp;
        state.HasLastAppliedStamp = true;
    }

    private static bool TryMatchLooseItemStandDefinition(
        ItemStand itemStand,
        LocationItemStandDefinition definition,
        Transform root,
        string liveRelativePath)
    {
        if (!HasItemStandOverride(definition))
        {
            return false;
        }

        string configuredPath = (definition.Path ?? "").Trim();
        if (configuredPath.Length == 0)
        {
            return true;
        }

        if (liveRelativePath.Length == 0)
        {
            return LooseItemStandAuthoredPathsByInstance.TryGetValue(itemStand, out string? authoredPathOnly) &&
                   string.Equals(configuredPath, authoredPathOnly, StringComparison.Ordinal);
        }

        if (string.Equals(configuredPath, liveRelativePath, StringComparison.Ordinal))
        {
            return true;
        }

        if (configuredPath.EndsWith("/" + liveRelativePath, StringComparison.Ordinal) ||
            liveRelativePath.EndsWith("/" + configuredPath, StringComparison.Ordinal))
        {
            return true;
        }

        string rootedRelativePath = $"{TrimCloneSuffix(root.name)}[0]/{liveRelativePath}";
        if (string.Equals(configuredPath, rootedRelativePath, StringComparison.Ordinal) ||
            configuredPath.EndsWith("/" + rootedRelativePath, StringComparison.Ordinal))
        {
            return true;
        }

        return LooseItemStandAuthoredPathsByInstance.TryGetValue(itemStand, out string? authoredPath) &&
               string.Equals(configuredPath, authoredPath, StringComparison.Ordinal);
    }

    private static string BuildOfferingBowlDiagnosticPath(Transform? transform)
    {
        if (transform == null)
        {
            return "(none)";
        }

        List<string> segments = new();
        Transform? current = transform;
        while (current != null)
        {
            segments.Add(current.name ?? "(null)");
            current = current.parent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static void CaptureLooseItemStandSnapshotIfNeeded(ItemStand itemStand, string prefabName)
    {
        if (!LooseItemStandSnapshots.ContainsKey(itemStand))
        {
            LooseItemStandSnapshots[itemStand] = CaptureItemStandSnapshot(itemStand);
        }

        TrackedLooseItemStandPrefabs[itemStand] = prefabName;
    }

    private static void RestoreTrackedLooseItemStand(ItemStand itemStand)
    {
        if (LooseItemStandSnapshots.TryGetValue(itemStand, out ItemStandSnapshot? snapshot))
        {
            RestoreItemStand(itemStand, snapshot);
        }
    }

    private static void CleanupLooseItemStandSnapshots()
    {
        List<ItemStand>? destroyed = null;
        foreach (ItemStand trackedItemStand in LooseItemStandSnapshots.Keys)
        {
            if (trackedItemStand != null)
            {
                continue;
            }

            destroyed ??= new List<ItemStand>();
            destroyed.Add(trackedItemStand!);
        }

        if (destroyed == null)
        {
            return;
        }

        foreach (ItemStand destroyedItemStand in destroyed)
        {
            LooseItemStandSnapshots.Remove(destroyedItemStand);
            TrackedLooseItemStandPrefabs.Remove(destroyedItemStand);
            LooseItemStandAuthoredPathsByInstance.Remove(destroyedItemStand);
        }
    }

    private static void TryStampLooseItemStandAuthoredPaths(
        OfferingBowl offeringBowl,
        string prefabName,
        IReadOnlyList<ItemStand> detachedRelevantItemStands)
    {
        if (offeringBowl == null ||
            !AuthoredItemStandSlotsByPrefab.TryGetValue(prefabName, out List<AuthoredItemStandSlotTemplate>? templates) ||
            templates.Count == 0 ||
            detachedRelevantItemStands == null ||
            detachedRelevantItemStands.Count == 0)
        {
            return;
        }

        HashSet<int> assignedItemStandIds = new();
        HashSet<string> assignedPaths = new(StringComparer.Ordinal);
        foreach (ItemStand detachedItemStand in detachedRelevantItemStands)
        {
            if (detachedItemStand == null)
            {
                continue;
            }

            if (!LooseItemStandAuthoredPathsByInstance.TryGetValue(detachedItemStand, out string? assignedPath) ||
                string.IsNullOrWhiteSpace(assignedPath) ||
                !templates.Any(template => string.Equals(template.Path, assignedPath, StringComparison.Ordinal)))
            {
                continue;
            }

            assignedItemStandIds.Add(detachedItemStand.GetInstanceID());
            assignedPaths.Add(assignedPath);
        }

        List<(float Distance, ItemStand ItemStand, AuthoredItemStandSlotTemplate Template)> candidates = new();
        foreach (ItemStand detachedItemStand in detachedRelevantItemStands)
        {
            if (assignedItemStandIds.Contains(detachedItemStand.GetInstanceID()))
            {
                continue;
            }

            Vector3 itemStandOffset = offeringBowl.transform.InverseTransformPoint(detachedItemStand.transform.position);
            foreach (AuthoredItemStandSlotTemplate template in templates)
            {
                if (assignedPaths.Contains(template.Path))
                {
                    continue;
                }

                float distance = Vector3.SqrMagnitude(itemStandOffset - template.OfferingBowlLocalOffset);
                candidates.Add((distance, detachedItemStand, template));
            }
        }

        candidates.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        foreach ((float _, ItemStand detachedItemStand, AuthoredItemStandSlotTemplate template) in candidates)
        {
            int itemStandId = detachedItemStand.GetInstanceID();
            if (assignedItemStandIds.Contains(itemStandId) || assignedPaths.Contains(template.Path))
            {
                continue;
            }

            LooseItemStandAuthoredPathsByInstance[detachedItemStand] = template.Path;
            assignedItemStandIds.Add(itemStandId);
            assignedPaths.Add(template.Path);
        }
    }

    private static OfferingBowlRuntimeState GetOrAddOfferingBowlRuntimeState(OfferingBowl offeringBowl)
    {
        OfferingBowlRuntimeState? state = offeringBowl.GetComponent<OfferingBowlRuntimeState>();
        if (state != null)
        {
            return state;
        }

        return offeringBowl.gameObject.AddComponent<OfferingBowlRuntimeState>();
    }

    private static long GetOfferingBowlLastUseTicks(OfferingBowl offeringBowl, OfferingBowlRuntimeState state)
    {
        ZNetView? view = offeringBowl.GetComponentInParent<ZNetView>();
        if (view != null && view.IsValid())
        {
            long zdoTicks = view.GetZDO().GetLong(OfferingBowlLastUseTicksKey, 0L);
            if (zdoTicks > 0L)
            {
                state.LocalLastUseTicks = zdoTicks;
                return zdoTicks;
            }
        }

        return state.LocalLastUseTicks;
    }

}
