using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class CharacterDropManager
{
    internal static bool IsDespawnTrackingRuleLookupReady()
    {
        lock (Sync)
        {
            return IsGameDataReady();
        }
    }

    internal static bool IsEligibleDespawnTrackingPrefabName(string prefabName)
    {
        lock (Sync)
        {
            if (!IsGameDataReady() || string.IsNullOrWhiteSpace(prefabName))
            {
                return false;
            }

            return CharacterDespawnRuntime.IsEligibleDespawnTrackingPrefabName(
                prefabName,
                _configurationSignature,
                ActiveEntriesByPrefab);
        }
    }

    internal static bool IsEligibleDespawnTrackingPrefabHash(int prefabHash)
    {
        lock (Sync)
        {
            if (!IsGameDataReady() || prefabHash == 0)
            {
                return false;
            }

            return CharacterDespawnRuntime.IsEligibleDespawnTrackingPrefabHash(
                prefabHash,
                _configurationSignature,
                ActiveEntriesByPrefab);
        }
    }

    internal static IReadOnlyList<string> GetDespawnBootstrapPrefabOrder()
    {
        lock (Sync)
        {
            if (!IsGameDataReady())
            {
                return Array.Empty<string>();
            }

            return CharacterDespawnRuntime.GetDespawnBootstrapPrefabOrder(
                _configurationSignature,
                ActiveEntriesByPrefab);
        }
    }

    internal static bool TryResolveDespawnTrackingRule(
        string prefabName,
        out float? rangeOverride,
        out float? delayOverride,
        out IReadOnlyCollection<DespawnRefundDrop> refunds)
    {
        lock (Sync)
        {
            rangeOverride = null;
            delayOverride = null;
            refunds = Array.Empty<DespawnRefundDrop>();
            if (!IsGameDataReady() || string.IsNullOrWhiteSpace(prefabName))
            {
                return false;
            }

            return CharacterDespawnRuntime.TryResolveDespawnTrackingRule(
                prefabName,
                _configurationSignature,
                ActiveEntriesByPrefab,
                out rangeOverride,
                out delayOverride,
                out refunds);
        }
    }

    internal static bool TryResolveDespawnTrackingRule(
        int prefabHash,
        out string prefabName,
        out float? rangeOverride,
        out float? delayOverride,
        out IReadOnlyCollection<DespawnRefundDrop> refunds)
    {
        lock (Sync)
        {
            prefabName = "";
            rangeOverride = null;
            delayOverride = null;
            refunds = Array.Empty<DespawnRefundDrop>();
            if (!IsGameDataReady() || prefabHash == 0)
            {
                return false;
            }

            return CharacterDespawnRuntime.TryResolveDespawnTrackingRule(
                prefabHash,
                _configurationSignature,
                ActiveEntriesByPrefab,
                out prefabName,
                out rangeOverride,
                out delayOverride,
                out refunds);
        }
    }
}
