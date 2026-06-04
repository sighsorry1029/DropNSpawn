using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private static void ApplyCompiledLocationEntryPlans(
        GameObject conditionTarget,
        IReadOnlyList<CompiledLocationEntryPlan> entryPlans,
        OfferingBowl? offeringBowl,
        List<ItemStand> relevantItemStands,
        Dictionary<string, ItemStand> liveItemStandsByPath,
        string prefabName,
        Transform locationRoot)
    {
        foreach (CompiledLocationEntryPlan entryPlan in entryPlans)
        {
            if (entryPlan.HasConditions &&
                !DropConditionEvaluator.AreSatisfied(conditionTarget, entryPlan.Conditions, prefabName))
            {
                continue;
            }

            if (entryPlan.OfferingBowl != null && offeringBowl != null)
            {
                ApplyOfferingBowl(offeringBowl, entryPlan.OfferingBowl.Definition, prefabName);
            }

            if (entryPlan.ItemStands.Count > 0 && relevantItemStands.Count > 0)
            {
                ApplyConfiguredItemStands(
                    entryPlan.ItemStands,
                    relevantItemStands,
                    liveItemStandsByPath,
                    prefabName,
                    locationRoot,
                    offeringBowl);
            }

        }
    }

    private static void ApplyCompiledLooseItemStandPlansForContext(
        ItemStand itemStand,
        IReadOnlyList<CompiledLocationEntryPlan> entryPlans,
        string prefabName,
        Transform root,
        OfferingBowl? offeringBowl)
    {
        CaptureLooseItemStandSnapshotIfNeeded(itemStand, prefabName);
        if (offeringBowl != null)
        {
            TryStampLooseItemStandAuthoredPaths(offeringBowl, prefabName, new[] { itemStand });
        }

        string liveRelativePath = GetRelativePath(root, itemStand.transform);
        foreach (CompiledLocationEntryPlan entryPlan in entryPlans)
        {
            if (entryPlan.ItemStands.Count == 0)
            {
                continue;
            }

            if (entryPlan.HasConditions &&
                !DropConditionEvaluator.AreSatisfied(itemStand.gameObject, entryPlan.Conditions, prefabName))
            {
                continue;
            }

            foreach (CompiledLocationItemStandPlan itemStandPlan in entryPlan.ItemStands)
            {
                if (!TryMatchLooseItemStandPlan(itemStand, itemStandPlan, root, liveRelativePath))
                {
                    continue;
                }

                ApplyItemStand(itemStand, itemStandPlan.Definition, prefabName, root);
            }
        }
    }

    private static bool TryMatchLooseItemStandPlan(
        ItemStand itemStand,
        CompiledLocationItemStandPlan plan,
        Transform root,
        string liveRelativePath)
    {
        return TryMatchLooseItemStandDefinition(itemStand, plan.Definition, root, liveRelativePath);
    }
}
