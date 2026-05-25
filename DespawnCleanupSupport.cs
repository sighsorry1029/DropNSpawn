using UnityEngine;

namespace DropNSpawn;

internal static class DespawnCleanupSupport
{
    internal static void ApplyBeforeDestroy(ZDO zdo)
    {
        TryClearActiveBossCountForDespawnedZdo(zdo);
    }

    private static void TryClearActiveBossCountForDespawnedZdo(ZDO zdo)
    {
        if (ZoneSystem.instance == null || !zdo.GetBool("bosscount"))
        {
            return;
        }

        ZoneSystem.instance.GetGlobalKey(GlobalKeys.activeBosses, out float activeBossCount);
        ZoneSystem.instance.SetGlobalKey(GlobalKeys.activeBosses, Mathf.Max(0f, activeBossCount - 1f));
        zdo.Set("bosscount", value: false);
    }
}
