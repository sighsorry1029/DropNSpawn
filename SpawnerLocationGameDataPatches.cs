using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

[HarmonyPatch(typeof(Location), "OnDestroy")]
internal static class LocationOnDestroyPatch
{
    private static void Prefix(Location __instance)
    {
        if (!PluginSettingsFacade.IsSpawnerDomainEnabled())
        {
            return;
        }

        SpawnerManager.UntrackLocationInstanceProvenance(__instance);
    }
}

[HarmonyPatch(typeof(LocationProxy), "SpawnLocation")]
internal static class LocationProxySpawnLocationPatch
{
    private static readonly AccessTools.FieldRef<LocationProxy, GameObject> InstanceRef = AccessTools.FieldRefAccess<LocationProxy, GameObject>("m_instance");

    private static void Postfix(LocationProxy __instance, bool __result)
    {
        if (!__result)
        {
            return;
        }

        bool spawnerDomainEnabled = PluginSettingsFacade.IsSpawnerDomainEnabled() &&
                                    !DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Spawner);
        if (!spawnerDomainEnabled)
        {
            return;
        }

        GameObject? instance = __instance != null ? InstanceRef(__instance) : null;
        SpawnerManager.RecordSpawnedLocationProxyProvenance(__instance, instance);
    }
}
