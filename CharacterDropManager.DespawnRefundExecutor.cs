using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal static partial class CharacterDropManager
{
    private const float DespawnRefundDropArea = 0.5f;

    private static List<ResolvedConfiguredDrop> ResolveConfiguredDespawnRefundDrops(IReadOnlyCollection<DespawnRefundDrop> refunds)
    {
        if (refunds.Count == 0)
        {
            return new List<ResolvedConfiguredDrop>();
        }

        List<ResolvedConfiguredDrop> drops = new(refunds.Count);
        foreach (DespawnRefundDrop refund in refunds)
        {
            if (refund.Prefab == null || refund.Amount <= 0)
            {
                continue;
            }

            drops.Add(new ResolvedConfiguredDrop
            {
                Prefab = refund.Prefab,
                Amount = refund.Amount,
                DropInStack = false
            });
        }

        return drops;
    }

    internal static bool TryExecuteConfiguredDespawnRefunds(
        Vector3 centerPos,
        IReadOnlyCollection<DespawnRefundDrop> refunds)
    {
        List<ResolvedConfiguredDrop> drops = ResolveConfiguredDespawnRefundDrops(refunds);
        if (drops.Count == 0)
        {
            return refunds.Count == 0;
        }

        try
        {
            DropConfiguredItems(drops, centerPos, DespawnRefundDropArea);
            return true;
        }
        catch (Exception ex)
        {
            if (PluginSettingsFacade.IsDespawnDiagnosticsEnabled())
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                    $"[Despawn] Failed to execute configured despawn refunds at {centerPos.x:F1},{centerPos.y:F1},{centerPos.z:F1}: {ex.Message}");
            }

            return false;
        }
    }
}
