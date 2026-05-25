using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

/// <summary>
/// Owns auto-detected boss prefab caches used by character-related policy decisions.
/// It does not own live boss instances or despawn countdown state.
/// </summary>
internal static class CharacterBossPolicyRuntime
{
    private sealed class RuntimeState
    {
        public static RuntimeState Empty { get; } = new();

        public HashSet<string> AutoDetectedBossPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> AutoDetectedBossPrefabHashes { get; } = new();
    }

    private static RuntimeState _runtimeState = RuntimeState.Empty;
    private static int? _runtimeConfigurationGameDataSignature;

    internal static void Reset()
    {
        _runtimeState = RuntimeState.Empty;
        _runtimeConfigurationGameDataSignature = null;
    }

    internal static IReadOnlyCollection<string> GetAutoDetectedBossPrefabNames()
    {
        EnsureRuntimeState();
        return _runtimeState.AutoDetectedBossPrefabs;
    }

    internal static bool IsAutoDetectedBossPrefab(string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return false;
        }

        EnsureRuntimeState();
        return _runtimeState.AutoDetectedBossPrefabs.Contains(prefabName);
    }

    internal static bool IsAutoDetectedBossPrefab(int prefabHash)
    {
        if (prefabHash == 0)
        {
            return false;
        }

        EnsureRuntimeState();
        return _runtimeState.AutoDetectedBossPrefabHashes.Contains(prefabHash);
    }

    private static void EnsureRuntimeState()
    {
        int gameDataSignature = CharacterDropManager.ComputeGameDataSignatureForDespawnRuntime();
        if (_runtimeConfigurationGameDataSignature == gameDataSignature)
        {
            return;
        }

        _runtimeState = BuildRuntimeState();
        _runtimeConfigurationGameDataSignature = gameDataSignature;
    }

    private static RuntimeState BuildRuntimeState()
    {
        RuntimeState state = new();
        foreach (GameObject prefab in CharacterDropManager.EnumeratePrefabsForDespawnRuntime())
        {
            if (!IsBossPrefab(prefab))
            {
                continue;
            }

            string prefabName = CharacterDropManager.GetPrefabNameForDespawnRuntime(prefab);
            if (string.IsNullOrWhiteSpace(prefabName))
            {
                continue;
            }

            state.AutoDetectedBossPrefabs.Add(prefabName);
            state.AutoDetectedBossPrefabHashes.Add(prefabName.GetStableHashCode());
        }

        return state;
    }

    private static bool IsBossPrefab(GameObject? prefab)
    {
        return prefab != null &&
               prefab.TryGetComponent(out Character character) &&
               character.IsBoss();
    }
}
