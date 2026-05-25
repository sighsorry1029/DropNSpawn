using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

[HarmonyPatch(typeof(Location), nameof(Location.Awake))]
internal static class LocationAwakePatch
{
    private static void Postfix(Location __instance)
    {
        LocationManager.TrackLocationInstance(__instance);

        if (!PluginSettingsFacade.IsLocationDomainEnabled() || DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
        {
            return;
        }

        LocationManager.QueueLocationReconcile(__instance);
    }
}

[HarmonyPatch(typeof(Location), "OnDestroy")]
internal static class LocationOnDestroyPatch
{
    private static void Prefix(Location __instance)
    {
        SpawnerManager.UntrackLocationInstanceProvenance(__instance);

        LocationManager.UntrackLocationInstance(__instance);
    }
}

[HarmonyPatch(typeof(LocationProxy), "SpawnLocation")]
internal static class LocationProxySpawnLocationPatch
{
    private static readonly AccessTools.FieldRef<LocationProxy, GameObject> InstanceRef = AccessTools.FieldRefAccess<LocationProxy, GameObject>("m_instance");
    private static readonly Dictionary<int, int> SpawnCountsByProxy = new();

    private static void Postfix(LocationProxy __instance, bool __result)
    {
        if (!__result)
        {
            return;
        }

        int spawnCount = 1;
        if (PluginSettingsFacade.IsOfferingBowlDiagnosticsEnabled() && __instance != null)
        {
            int proxyId = __instance.GetInstanceID();
            if (SpawnCountsByProxy.TryGetValue(proxyId, out int previousCount))
            {
                spawnCount = previousCount + 1;
            }

            SpawnCountsByProxy[proxyId] = spawnCount;
        }

        if (__instance != null &&
            ZNet.instance != null &&
            ZNet.instance.IsServer() &&
            LocationManager.TryResolveZoneLocationPrefabName(__instance.transform.position, out string proxyPrefabName) &&
            proxyPrefabName.Length > 0)
        {
            LocationManager.RecordLocationProxyResolvedPrefab(__instance, proxyPrefabName);
        }

        bool locationDomainEnabled = PluginSettingsFacade.IsLocationDomainEnabled() &&
                                     !DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location);
        bool spawnerDomainEnabled = PluginSettingsFacade.IsSpawnerDomainEnabled() &&
                                    !DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Spawner);
        if (!locationDomainEnabled && !spawnerDomainEnabled)
        {
            return;
        }

        GameObject? instance = __instance != null ? InstanceRef(__instance) : null;
        if (PluginSettingsFacade.IsOfferingBowlDiagnosticsEnabled())
        {
            LocationManager.LogLocationProxySpawnDiagnostics(__instance, instance, spawnCount, __result);
        }

        if (spawnerDomainEnabled)
        {
            SpawnerManager.RecordSpawnedLocationProxyProvenance(__instance, instance);
        }

        if (locationDomainEnabled)
        {
            LocationManager.QueueSpawnedLocationRootReconcile(instance);
            if (LocationManager.HasRuntimeLocationAliasDemand())
            {
                LocationManager.QueueLocationProxyObservation(__instance);
            }
        }
    }
}

[HarmonyPatch(typeof(LocationProxy), nameof(LocationProxy.SetLocation))]
[HarmonyPriority(Priority.First)]
internal static class LocationProxySetLocationAliasPatch
{
    private static void Prefix(string location, ref string __state)
    {
        if (!LocationManager.TryGetPendingLocationProxyCreationPrefabName(out __state))
        {
            __state = (location ?? "").Trim();
        }
    }

    private static void Postfix(LocationProxy __instance, string __state)
    {
        if (__state.Length > 0)
        {
            LocationManager.RecordLocationProxyResolvedPrefab(__instance, __state);
        }

        if (!PluginSettingsFacade.IsLocationDomainEnabled() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
        {
            return;
        }

        if (LocationManager.HasRuntimeLocationAliasDemand())
        {
            LocationManager.QueueLocationProxyObservation(__instance);
        }
    }
}

[HarmonyPatch(typeof(LocationProxy), "OnDestroy")]
internal static class LocationProxyOnDestroyPatch
{
    private static void Prefix(LocationProxy __instance)
    {
        LocationManager.UntrackLocationProxy(__instance);
    }
}

[HarmonyPatch(typeof(ZoneSystem), "CreateLocationProxy")]
[HarmonyPriority(Priority.First)]
internal static class ZoneSystemCreateLocationProxyAliasContextPatch
{
    private static void Prefix(ZoneSystem.ZoneLocation location, ref bool __state)
    {
        __state = false;
        string prefabName = (location?.m_prefabName ?? location?.m_prefab.Name ?? "").Trim();
        if (prefabName.Length == 0)
        {
            return;
        }

        LocationManager.BeginLocationProxyCreationContext(prefabName);
        __state = true;
    }

    private static void Finalizer(bool __state)
    {
        if (!__state)
        {
            return;
        }

        LocationManager.EndLocationProxyCreationContext();
    }
}

[HarmonyPatch(typeof(OfferingBowl), nameof(OfferingBowl.GetHoverText))]
internal static class OfferingBowlGetHoverTextPatch
{
    private static void Postfix(OfferingBowl __instance, ref string __result)
    {
        if (PluginSettingsFacade.IsLocationDomainEnabled() &&
            !DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
        {
            if (__instance.GetComponentInParent<Location>() == null)
            {
                LocationManager.QueueLooseOfferingBowlOverride(__instance);
            }
        }

        __result = OfferingBowlHoverInfoFormatter.AppendInfo(__result, __instance);
    }
}

[HarmonyPatch(typeof(OfferingBowl), nameof(OfferingBowl.Awake))]
internal static class OfferingBowlAwakeRegistryPatch
{
    private static void Postfix(OfferingBowl __instance)
    {
        OfferingBowlHoverInfoFormatter.RegisterOfferingBowl(__instance);
        LocationManager.LogOfferingBowlDiagnostics(__instance, "awake");
        if (!PluginSettingsFacade.IsLocationDomainEnabled() || DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
        {
            return;
        }

        if (__instance.GetComponentInParent<Location>(true) == null)
        {
            LocationManager.QueueLooseOfferingBowlOverride(__instance);
        }
    }
}

[HarmonyPatch(typeof(OfferingBowl), "Start")]
internal static class OfferingBowlStartDiagnosticsPatch
{
    private static readonly Dictionary<int, int> StartCountsByOfferingBowl = new();

    private static void Prefix(OfferingBowl __instance)
    {
        if (!PluginSettingsFacade.IsOfferingBowlDiagnosticsEnabled() || __instance == null)
        {
            return;
        }

        int instanceId = __instance.GetInstanceID();
        int nextCount = 1;
        if (StartCountsByOfferingBowl.TryGetValue(instanceId, out int previousCount))
        {
            nextCount = previousCount + 1;
        }

        StartCountsByOfferingBowl[instanceId] = nextCount;
        LocationManager.LogOfferingBowlDiagnostics(__instance, $"start#{nextCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }
}

[HarmonyPatch(typeof(OfferingBowl), nameof(OfferingBowl.Interact))]
internal static class OfferingBowlInteractRespawnPatch
{
    private static bool Prefix(OfferingBowl __instance, Humanoid user, bool hold, bool alt, ref bool __result)
    {
        if (!PluginSettingsFacade.IsLocationDomainEnabled())
        {
            return true;
        }

        if (hold || !__instance.m_useItemStands)
        {
            return true;
        }

        if (__instance.GetComponentInParent<Location>() == null)
        {
            LocationManager.EnsureLooseOfferingBowlOverride(__instance);
        }
        else if (LocationManager.HasRuntimeLocationAliasDemand())
        {
            LocationManager.MaybeQueueRuntimeLocationAliasRefresh(__instance);
        }

        LocationManager.OfferingBowlBlockResult blockResult = LocationManager.EvaluateOfferingBowlBlock(__instance);
        if (!blockResult.Blocked)
        {
            return true;
        }

        __result = true;
        LocationManager.NotifyOfferingBowlBlocked(__instance, user, blockResult);
        return false;
    }
}

[HarmonyPatch(typeof(OfferingBowl), nameof(OfferingBowl.UseItem))]
internal static class OfferingBowlUseItemRespawnPatch
{
    private static bool Prefix(OfferingBowl __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result, ref int __state)
    {
        __state = -1;
        if (!PluginSettingsFacade.IsLocationDomainEnabled())
        {
            return true;
        }

        if (__instance.m_useItemStands)
        {
            return true;
        }

        if (PluginSettingsFacade.IsOfferingBowlDiagnosticsEnabled() &&
            user != null &&
            item != null &&
            !string.IsNullOrEmpty(item.m_shared?.m_name))
        {
            string itemName = item.m_shared!.m_name;
            __state = user.GetInventory().CountItems(itemName);
        }

        if (LocationManager.HasRuntimeLocationAliasDemand())
        {
            LocationManager.MaybeQueueRuntimeLocationAliasRefresh(__instance);
        }

        LocationManager.OfferingBowlBlockResult blockResult = LocationManager.EvaluateOfferingBowlBlock(__instance);
        if (!blockResult.Blocked)
        {
            return true;
        }

        __result = true;
        LocationManager.NotifyOfferingBowlBlocked(__instance, user, blockResult);
        return false;
    }

    private static void Postfix(OfferingBowl __instance, Humanoid user, ItemDrop.ItemData item, bool __result, int __state)
    {
        if (!PluginSettingsFacade.IsOfferingBowlDiagnosticsEnabled() ||
            __instance == null ||
            __instance.m_useItemStands ||
            user == null ||
            item == null ||
            string.IsNullOrEmpty(item.m_shared?.m_name))
        {
            return;
        }

        string itemName = item.m_shared!.m_name;
        int countAfter = user.GetInventory().CountItems(itemName);
        LocationManager.LogOfferingBowlItemFlowDiagnostics(
            __instance,
            user,
            item,
            "UseItem",
            __state,
            countAfter,
            __result);
    }
}

[HarmonyPatch(typeof(OfferingBowl), "SpawnBoss")]
internal static class OfferingBowlSpawnBossRespawnPatch
{
    private static readonly AccessTools.FieldRef<OfferingBowl, Vector3> BossSpawnPointRef =
        AccessTools.FieldRefAccess<OfferingBowl, Vector3>("m_bossSpawnPoint");

    private static void Postfix(OfferingBowl __instance)
    {
        if (!PluginSettingsFacade.IsLocationDomainEnabled())
        {
            return;
        }

        LocationManager.MarkOfferingBowlUsed(__instance);
    }
}

[HarmonyPatch(typeof(OfferingBowl), "RPC_SpawnBoss")]
internal static class OfferingBowlRpcSpawnBossServerValidationPatch
{
    private static bool Prefix(OfferingBowl __instance)
    {
        if (!DropNSpawnPlugin.IsRuntimeServer() || !PluginSettingsFacade.IsLocationDomainEnabled())
        {
            return true;
        }

        return !LocationManager.EvaluateOfferingBowlBlock(__instance).Blocked;
    }
}

[HarmonyPatch(typeof(OfferingBowl), "DelayedSpawnBoss")]
internal static class OfferingBowlDelayedSpawnBossCllcPatch
{
    private static readonly AccessTools.FieldRef<OfferingBowl, Vector3> BossSpawnPointRef =
        AccessTools.FieldRefAccess<OfferingBowl, Vector3>("m_bossSpawnPoint");

    private static void Prefix(OfferingBowl __instance)
    {
        if (!DropNSpawnPlugin.IsRuntimeServer() ||
            !PluginSettingsFacade.IsLocationDomainEnabled())
        {
            return;
        }

        LocationManager.BeginOfferingBowlBossSpawnAttempt(__instance, BossSpawnPointRef(__instance));
    }

    private static void Postfix(OfferingBowl __instance)
    {
        if (!DropNSpawnPlugin.IsRuntimeServer() ||
            !PluginSettingsFacade.IsLocationDomainEnabled())
        {
            return;
        }

        LocationManager.FinalizeOfferingBowlBossSpawnAttempt(__instance, BossSpawnPointRef(__instance));
    }
}

[HarmonyPatch(typeof(OfferingBowl), "SpawnItem")]
internal static class OfferingBowlSpawnItemRespawnPatch
{
    private static void Postfix(OfferingBowl __instance, bool __result)
    {
        if (!PluginSettingsFacade.IsLocationDomainEnabled() || !__result)
        {
            return;
        }

        LocationManager.MarkOfferingBowlUsed(__instance);
    }
}

[HarmonyPatch(typeof(OfferingBowl), "RPC_BossSpawnInitiated")]
internal static class OfferingBowlBossSpawnInitiatedRpcPatch
{
    private static readonly AccessTools.FieldRef<OfferingBowl, Humanoid> InteractUserRef =
        AccessTools.FieldRefAccess<OfferingBowl, Humanoid>("m_interactUser");

    internal static Humanoid? ResolveInteractUser(OfferingBowl offeringBowl)
    {
        Humanoid? interactUser = InteractUserRef(offeringBowl);
        if (interactUser != null)
        {
            return interactUser;
        }

        if (Player.m_localPlayer == null)
        {
            return null;
        }

        InteractUserRef(offeringBowl) = Player.m_localPlayer;
        return Player.m_localPlayer;
    }

    private static bool Prefix(OfferingBowl __instance)
    {
        Humanoid? interactUser = ResolveInteractUser(__instance);
        if (interactUser == null)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                $"OfferingBowl '{__instance.gameObject.name}' received RPC_BossSpawnInitiated without an interact user.");
            return false;
        }

        interactUser.Message(MessageHud.MessageType.Center, __instance.m_usedAltarText);
        return false;
    }
}

[HarmonyPatch(typeof(OfferingBowl), "RPC_RemoveBossSpawnInventoryItems")]
internal static class OfferingBowlRemoveBossSpawnInventoryItemsRpcPatch
{
    private static readonly AccessTools.FieldRef<OfferingBowl, ItemDrop.ItemData> UsedSpawnItemRef =
        AccessTools.FieldRefAccess<OfferingBowl, ItemDrop.ItemData>("m_usedSpawnItem");

    private static bool Prefix(OfferingBowl __instance)
    {
        Humanoid? interactUser = OfferingBowlBossSpawnInitiatedRpcPatch.ResolveInteractUser(__instance);
        if (interactUser == null)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                $"OfferingBowl '{__instance.gameObject.name}' received RPC_RemoveBossSpawnInventoryItems without an interact user.");
            return false;
        }

        ItemDrop.ItemData? usedSpawnItem = UsedSpawnItemRef(__instance) ?? __instance.m_bossItem?.m_itemData;
        string? itemName = usedSpawnItem?.m_shared?.m_name;
        int countBefore = -1;
        if (!string.IsNullOrEmpty(itemName))
        {
            countBefore = interactUser.GetInventory().CountItems(itemName);
            interactUser.GetInventory().RemoveItem(itemName, __instance.m_bossItems);
        }

        int countAfter = !string.IsNullOrEmpty(itemName)
            ? interactUser.GetInventory().CountItems(itemName)
            : -1;

        if (PluginSettingsFacade.IsOfferingBowlDiagnosticsEnabled())
        {
            LocationManager.LogOfferingBowlItemFlowDiagnostics(
                __instance,
                interactUser,
                usedSpawnItem,
                "RPC_RemoveBossSpawnInventoryItems",
                countBefore,
                countAfter);
        }

        if (usedSpawnItem != null)
        {
            interactUser.ShowRemovedMessage(usedSpawnItem, __instance.m_bossItems);
        }

        interactUser.Message(MessageHud.MessageType.Center, __instance.m_usedAltarText);
        if (__instance.m_itemSpawnPoint)
        {
            __instance.m_fuelAddedEffects.Create(__instance.m_itemSpawnPoint.position, __instance.transform.rotation);
        }

        return false;
    }
}

[HarmonyPatch(typeof(ItemStand), nameof(ItemStand.GetHoverText))]
internal static class ItemStandGetHoverTextPatch
{
    private static void Postfix(ItemStand __instance, ref string __result)
    {
        if (PluginSettingsFacade.IsLocationDomainEnabled() &&
            !DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
        {
            if (__instance.GetComponentInParent<Location>() == null)
            {
                LocationManager.QueueLooseItemStandOverride(__instance);
            }
        }

        __result = AltarItemStandHoverInfoFormatter.AppendInfo(__result, __instance);
    }
}

[HarmonyPatch(typeof(ItemStand), nameof(ItemStand.Interact))]
internal static class ItemStandInteractLocationOverridePatch
{
    private static void Prefix(ItemStand __instance)
    {
        if (!PluginSettingsFacade.IsLocationDomainEnabled() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
        {
            return;
        }

        if (__instance.GetComponentInParent<Location>() != null)
        {
            if (LocationManager.HasRuntimeLocationAliasDemand())
            {
                LocationManager.MaybeQueueRuntimeLocationAliasRefresh(__instance);
            }

            return;
        }

        LocationManager.EnsureLooseItemStandOverride(__instance);
    }
}

[HarmonyPatch(typeof(ItemStand), nameof(ItemStand.UseItem))]
internal static class ItemStandUseItemLocationOverridePatch
{
    private static void Prefix(ItemStand __instance)
    {
        if (!PluginSettingsFacade.IsLocationDomainEnabled() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
        {
            return;
        }

        if (__instance.GetComponentInParent<Location>() != null)
        {
            if (LocationManager.HasRuntimeLocationAliasDemand())
            {
                LocationManager.MaybeQueueRuntimeLocationAliasRefresh(__instance);
            }

            return;
        }

        LocationManager.EnsureLooseItemStandOverride(__instance);
    }
}

[HarmonyPatch(typeof(ItemStand), nameof(ItemStand.UseItem))]
internal static class ItemStandUseItemBossStonePerPlayerPatch
{
    private static bool Prefix(ItemStand __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
    {
        if (BossStonePerPlayerRuntime.TryHandleUseItem(__instance, user, item, out bool result))
        {
            __result = result;
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(ItemStand), nameof(ItemStand.HaveAttachment))]
internal static class ItemStandHaveAttachmentBossStonePerPlayerPatch
{
    private static bool Prefix(ItemStand __instance, ref bool __result)
    {
        if (BossStonePerPlayerRuntime.TryOverrideHaveAttachment(__instance, out bool result))
        {
            __result = result;
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(ItemStand), "UpdateVisual")]
internal static class ItemStandUpdateVisualBossStonePerPlayerPatch
{
    private static bool Prefix(ItemStand __instance)
    {
        return !BossStonePerPlayerRuntime.TryOverrideUpdateVisual(__instance);
    }
}

[HarmonyPatch(typeof(Player), "Update")]
internal static class PlayerUpdateForsakenPowerSelectionPatch
{
    private static void Postfix(Player __instance)
    {
        ForsakenPowerSelectionRuntime.TryRotateSelection(__instance);
    }
}

[HarmonyPatch(typeof(Hud), "UpdateGuardianPower")]
internal static class HudUpdateGuardianPowerForsakenPowerSelectionPatch
{
    private static void Postfix(Player player)
    {
        ForsakenPowerSelectionRuntime.UpdateHudHint(Hud.instance, player);
    }
}

[HarmonyPatch(typeof(ItemStand), nameof(ItemStand.Awake))]
internal static class ItemStandAwakeLocationOverridePatch
{
    private static void Postfix(ItemStand __instance)
    {
        AltarItemStandHoverInfoFormatter.RegisterItemStand(__instance);
        if (!PluginSettingsFacade.IsLocationDomainEnabled() || DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location))
        {
            return;
        }

        if (__instance.GetComponentInParent<Location>(true) == null)
        {
            LocationManager.QueueLooseItemStandOverride(__instance);
        }
    }
}
