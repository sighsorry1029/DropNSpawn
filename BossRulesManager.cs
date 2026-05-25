using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static class BossRulesManager
{
    private static readonly Dictionary<string, HashSet<Character>> TrackedBossesByPrefab =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool ShouldBlockConfiguredSameBossSpawn(GameObject? targetPrefab, Vector3 sourcePosition)
    {
        return ShouldBlockSameBossSpawn(targetPrefab, sourcePosition, PluginSettingsFacade.GetSameBossDuplicateBlockRadius());
    }

    internal static bool ShouldBlockSameBossSpawn(GameObject? targetPrefab, Vector3 sourcePosition, float radius)
    {
        if (radius <= 0f || !TryGetBossPrefabName(targetPrefab, out string targetPrefabName))
        {
            return false;
        }

        if (!TrackedBossesByPrefab.TryGetValue(targetPrefabName, out HashSet<Character>? trackedBosses) ||
            trackedBosses.Count == 0)
        {
            return false;
        }

        float radiusSquared = radius * radius;
        trackedBosses.RemoveWhere(static character => !IsTrackableBossCharacter(character));
        if (trackedBosses.Count == 0)
        {
            TrackedBossesByPrefab.Remove(targetPrefabName);
            return false;
        }

        foreach (Character trackedBoss in trackedBosses)
        {
            if (IsWithinRangeXZ(trackedBoss.GetCenterPoint(), sourcePosition, radiusSquared))
            {
                return true;
            }
        }

        return false;
    }

    internal static void TrackBossCharacter(Character? character)
    {
        if (!TryGetTrackableBossPrefabName(character, out string prefabName))
        {
            return;
        }

        if (!TrackedBossesByPrefab.TryGetValue(prefabName, out HashSet<Character>? trackedBosses))
        {
            trackedBosses = new HashSet<Character>();
            TrackedBossesByPrefab[prefabName] = trackedBosses;
        }

        trackedBosses.Add(character!);
    }

    internal static void UntrackBossCharacter(Character? character)
    {
        if (character == null)
        {
            return;
        }

        if (TryGetTrackableBossPrefabName(character, out string prefabName) &&
            TrackedBossesByPrefab.TryGetValue(prefabName, out HashSet<Character>? trackedBosses))
        {
            trackedBosses.Remove(character);
            if (trackedBosses.Count == 0)
            {
                TrackedBossesByPrefab.Remove(prefabName);
            }

            return;
        }

        RemoveBossCharacterByScan(character);
    }

    internal static void ClearRuntimeState()
    {
        TrackedBossesByPrefab.Clear();
    }

    private static void RemoveBossCharacterByScan(Character character)
    {
        string? prefabNameToRemove = null;
        foreach ((string prefabName, HashSet<Character> trackedBosses) in TrackedBossesByPrefab)
        {
            if (!trackedBosses.Remove(character))
            {
                continue;
            }

            prefabNameToRemove = trackedBosses.Count == 0 ? prefabName : null;
            break;
        }

        if (prefabNameToRemove != null)
        {
            TrackedBossesByPrefab.Remove(prefabNameToRemove);
        }
    }

    private static bool TryGetTrackableBossPrefabName(Character? character, out string prefabName)
    {
        prefabName = "";
        return IsTrackableBossCharacter(character) &&
               TryGetBossPrefabName(character!.gameObject, out prefabName);
    }

    private static bool IsTrackableBossCharacter(Character? character)
    {
        return character != null &&
               character.gameObject != null &&
               !character.IsDead() &&
               character.IsBoss();
    }

    private static bool IsWithinRangeXZ(Vector3 source, Vector3 target, float rangeSquared)
    {
        Vector3 offset = source - target;
        offset.y = 0f;
        return offset.sqrMagnitude < rangeSquared;
    }

    private static bool TryGetBossPrefabName(GameObject? prefab, out string prefabName)
    {
        prefabName = GetPrefabName(prefab);
        return prefab != null &&
               prefabName.Length > 0 &&
               prefab.TryGetComponent(out Character character) &&
               character.IsBoss();
    }

    private static string GetPrefabName(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return "";
        }

        ZNetView? nview = gameObject.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (zdo != null && ZNetScene.instance != null)
        {
            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab != null)
            {
                return prefab.name;
            }
        }

        string prefabName = Utils.GetPrefabName(gameObject);
        if (!string.IsNullOrWhiteSpace(prefabName))
        {
            return prefabName;
        }

        const string cloneSuffix = "(Clone)";
        string name = gameObject.name ?? "";
        if (name.EndsWith(cloneSuffix, StringComparison.Ordinal))
        {
            return name[..^cloneSuffix.Length].TrimEnd();
        }

        return name;
    }
}
