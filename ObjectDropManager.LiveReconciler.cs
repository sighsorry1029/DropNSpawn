using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    internal static void ReconcilePieceInstance(Piece piece)
    {
        ReconcileObjectInstance(piece.gameObject);
    }

    internal static void ReconcileObjectInstance(GameObject gameObject)
    {
        lock (Sync)
        {
            ReconcileObjectInstanceCore(gameObject);
        }
    }

    private static void ReconcileObjectInstanceCore(GameObject? gameObject)
    {
        if (!TryTrackLiveObjectInstanceLocked(gameObject, out string prefabName) ||
            !ShouldReconcileLocally(gameObject!))
        {
            return;
        }

        bool hasConfiguredEntries = RuntimeState.ActiveEntriesByPrefab.TryGetValue(prefabName, out List<PrefabConfigurationEntry>? entries) && entries.Count > 0;
        if (!IsGameDataReady() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Object))
        {
            return;
        }

        if (!SnapshotState.SnapshotsByPrefab.TryGetValue(prefabName, out PrefabSnapshot? snapshot))
        {
            return;
        }

        if (!RequiresLiveReconcileForPrefab(prefabName))
        {
            return;
        }

        RestoreConfiguredComponents(gameObject!, snapshot, CreateRestoreMask(snapshot), updateRuntimeState: true);
        if (!PluginSettingsFacade.IsObjectDomainEnabled() || !hasConfiguredEntries)
        {
            return;
        }

        ReconcileConfiguredInstance(gameObject!, snapshot, entries!);
    }

    private static void ReconcileConfiguredInstance(GameObject gameObject, PrefabSnapshot snapshot, IEnumerable<PrefabConfigurationEntry> entries)
    {
        List<PrefabConfigurationEntry> entryList = entries as List<PrefabConfigurationEntry> ?? entries.ToList();
        if (!ShouldApplyToInstance(gameObject))
        {
            return;
        }

        TryGetGroupConditionalApplyPlan(gameObject, snapshot, entryList, out GroupConditionalApplyPlan? groupPlan);

        // Callers restore the instance to baseline before entering configured reconcile.
        // Keeping that restore outside this helper avoids paying the full restore cost twice.

        if (groupPlan != null)
        {
            foreach (PrefabConfigurationEntry entry in groupPlan.MatchingEntries)
            {
                ApplyConfiguredComponents(gameObject, snapshot, entry, updateRuntimeState: true, allowConditionalMatches: true);
            }
        }

        foreach (PrefabConfigurationEntry entry in entryList)
        {
            if (groupPlan?.EligibleEntries.Contains(entry) == true)
            {
                continue;
            }

            ApplyConfiguredComponents(gameObject, snapshot, entry, updateRuntimeState: true, allowConditionalMatches: true);
        }

        ApplyEffectiveDropTableOverrides(gameObject, snapshot, entryList, allowConditionalMatches: true, groupPlan, includeEventOnlyKinds: false);
    }
}
