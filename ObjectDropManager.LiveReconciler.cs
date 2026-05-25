using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    internal static void ReconcilePieceInstance(Piece piece, bool clearCreatorRestrictedContainerContents)
    {
        ReconcileObjectInstance(piece.gameObject, clearCreatorRestrictedContainerContents);
    }

    internal static void ReconcileObjectInstance(GameObject gameObject, bool clearCreatorRestrictedContainerContents)
    {
        lock (Sync)
        {
            ReconcileObjectInstanceCore(gameObject, clearCreatorRestrictedContainerContents);
        }
    }

    private static void ReconcileObjectInstanceCore(GameObject? gameObject, bool clearCreatorRestrictedContainerContents)
    {
        if (!TryTrackLiveObjectInstanceLocked(gameObject, out string prefabName) ||
            !ShouldReconcileLocally(gameObject!))
        {
            return;
        }

        bool hasConfiguredEntries = ActiveEntriesByPrefab.TryGetValue(prefabName, out List<PrefabConfigurationEntry>? entries) && entries.Count > 0;
        if (!IsGameDataReady() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Object))
        {
            return;
        }

        if (!SnapshotsByPrefab.TryGetValue(prefabName, out PrefabSnapshot? snapshot))
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

        ReconcileConfiguredInstance(gameObject!, snapshot, entries!, clearCreatorRestrictedContainerContents);
    }

    private static void ReconcileConfiguredInstance(GameObject gameObject, PrefabSnapshot snapshot, IEnumerable<PrefabConfigurationEntry> entries, bool clearCreatorRestrictedContainerContents)
    {
        List<PrefabConfigurationEntry> entryList = entries as List<PrefabConfigurationEntry> ?? entries.ToList();
        TryGetGroupConditionalApplyPlan(gameObject, snapshot, entryList, out GroupConditionalApplyPlan? groupPlan);

        // Callers restore the instance to baseline before entering configured reconcile.
        // Keeping that restore outside this helper avoids paying the full restore cost twice.

        if (!ShouldApplyToInstance(gameObject))
        {
            if (clearCreatorRestrictedContainerContents)
            {
                foreach (PrefabConfigurationEntry entry in entryList)
                {
                    ClearContainerContentsIfNeeded(gameObject, entry);
                }
            }

            return;
        }

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
