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
        Dictionary<string, Vegvisir> liveVegvisirsByPath,
        Dictionary<string, RuneStone> liveRunestonesByPath,
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

            foreach (CompiledLocationVegvisirPlan vegvisirPlan in entryPlan.Vegvisirs)
            {
                if (!TryResolveVegvisirTarget(prefabName, vegvisirPlan.Definition, liveVegvisirsByPath, out Vegvisir vegvisir))
                {
                    continue;
                }

                ApplyVegvisir(vegvisir, vegvisirPlan.Definition, prefabName);
            }

            foreach (CompiledLocationRunestonePlan runestonePlan in entryPlan.Runestones)
            {
                if (!TryResolveRunestoneTarget(prefabName, runestonePlan.Definition, liveRunestonesByPath, out RuneStone runestone))
                {
                    continue;
                }

                ApplyRunestone(runestone, runestonePlan.Definition, prefabName);
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
