using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static void ApplyDesiredStateToPrefabs(ObjectDesiredState desiredState)
    {
        if (desiredState.DomainEnabled)
        {
            ApplyConfigurationToPrefabs(desiredState, desiredState.DirtyPrefabs);
        }
    }

    private static void ApplyDesiredStateToLiveObjects(ObjectDesiredState desiredState)
    {
        if (!desiredState.NeedsLiveReload)
        {
            return;
        }

        if (desiredState.ApplyPlan.SameGameData && desiredState.LiveDirtyPrefabs != null)
        {
            if (desiredState.LiveDirtyPrefabs.Count == 0)
            {
                return;
            }

            if (desiredState.QueueLiveReconcile)
            {
                QueueReapplyActiveEntriesToLiveObjects(desiredState.LiveDirtyPrefabs);
            }
            else
            {
                ReapplyActiveEntriesToLiveObjects(desiredState, desiredState.DomainEnabled, desiredState.LiveDirtyPrefabs);
            }

            return;
        }

        if (desiredState.QueueLiveReconcile)
        {
            QueueReapplyActiveEntriesToLiveObjects();
        }
        else
        {
            ReapplyActiveEntriesToLiveObjects(desiredState, desiredState.DomainEnabled);
        }
    }

    private static void ApplyConfigurationToPrefabs(ObjectDesiredState desiredState, HashSet<string>? targetPrefabs = null)
    {
        IReadOnlyDictionary<string, CompiledObjectPrefabPlan> plansByPrefab = desiredState.RuntimeConfigurationState.PlansByPrefab;
        IEnumerable<string> prefabNames = targetPrefabs != null
            ? targetPrefabs
            : plansByPrefab.Keys;
        foreach (string prefabName in prefabNames)
        {
            if (!plansByPrefab.TryGetValue(prefabName, out CompiledObjectPrefabPlan? plan))
            {
                continue;
            }

            List<PrefabConfigurationEntry> entries = plan.ActiveEntries;

            if (!SnapshotsByPrefab.TryGetValue(prefabName, out PrefabSnapshot? snapshot))
            {
                foreach (PrefabConfigurationEntry entry in entries)
                {
                    WarnInvalidEntry($"Object prefab '{prefabName}' from {DescribeEntrySource(entry)} was not found in ZNetScene.");
                }

                continue;
            }

            foreach (PrefabConfigurationEntry entry in entries)
            {
                ApplyConfiguredComponents(snapshot.Prefab, snapshot, entry, updateRuntimeState: false, allowConditionalMatches: false);
            }

            ApplyEffectiveDropTableOverrides(snapshot.Prefab, snapshot, entries, allowConditionalMatches: false);
        }
    }

    private static void ReapplyActiveEntriesToLiveObjects(
        ObjectDesiredState desiredState,
        bool domainEnabled,
        HashSet<string>? dirtyPrefabs = null)
    {
        IReadOnlyDictionary<string, CompiledObjectPrefabPlan> plansByPrefab = desiredState.RuntimeConfigurationState.PlansByPrefab;
        IEnumerable<GameObject> liveObjects = dirtyPrefabs == null
            ? GetRegisteredLiveObjects()
            : GetRegisteredLiveObjects(dirtyPrefabs);
        foreach (GameObject liveObject in liveObjects)
        {
            string prefabName = GetPrefabName(liveObject);
            if (!RequiresLiveReconcileForPrefab(prefabName))
            {
                continue;
            }

            RegisterLiveObject(liveObject, prefabName);
            if (!SnapshotsByPrefab.TryGetValue(prefabName, out PrefabSnapshot? snapshot))
            {
                continue;
            }

            RestoreConfiguredComponents(liveObject, snapshot, CreateRestoreMask(snapshot), updateRuntimeState: true);
            if (!domainEnabled || !plansByPrefab.TryGetValue(prefabName, out CompiledObjectPrefabPlan? plan))
            {
                continue;
            }

            ReconcileConfiguredInstance(liveObject, snapshot, plan.ActiveEntries, clearCreatorRestrictedContainerContents: false);
        }
    }

    private static void QueueReapplyActiveEntriesToLiveObjects(
        HashSet<string>? dirtyPrefabs = null)
    {
        IEnumerable<GameObject> liveObjects = dirtyPrefabs == null
            ? GetRegisteredLiveObjects()
            : GetRegisteredLiveObjects(dirtyPrefabs);
        foreach (GameObject liveObject in liveObjects)
        {
            string prefabName = GetPrefabName(liveObject);
            if (prefabName.Length == 0 || !RequiresLiveReconcileForPrefab(prefabName))
            {
                continue;
            }

            RegisterLiveObject(liveObject, prefabName);
            if (!SnapshotsByPrefab.ContainsKey(prefabName))
            {
                continue;
            }

            QueueTrackedObjectInstanceReconcileLocked(liveObject, prefabName, clearCreatorRestrictedContainerContents: false);
        }
    }
}
