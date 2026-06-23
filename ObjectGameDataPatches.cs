using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

[HarmonyPatch(typeof(Piece), nameof(Piece.Awake))]
internal static class PieceAwakePatch
{
    private static void Postfix(Piece __instance)
    {
        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.Piece);
    }
}

[HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
internal static class PieceSetCreatorPatch
{
    private static void Postfix(Piece __instance)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            return;
        }

        ObjectDropManager.ReconcilePieceInstance(__instance, __instance.GetCreator() != 0L);
    }
}

[HarmonyPatch(typeof(Piece), "OnDestroy")]
internal static class PieceOnDestroyPatch
{
    private static void Prefix(Piece __instance)
    {
        ObjectDropManager.UntrackObjectInstance(__instance.gameObject);
    }
}

[HarmonyPatch(typeof(ZNetView), "OnDestroy")]
internal static class ZNetViewOnDestroyPatch
{
    private static void Prefix(ZNetView __instance)
    {
        ObjectDropManager.UntrackObjectInstance(__instance.gameObject);
        SpawnArea? spawnArea = __instance.GetComponent<SpawnArea>();
        if (spawnArea != null)
        {
            SpawnerManager.UntrackSpawnAreaInstance(spawnArea);
        }
    }
}

[HarmonyPatch(typeof(Destructible), nameof(Destructible.Awake))]
internal static class DestructibleAwakePatch
{
    private static void Postfix(Destructible __instance)
    {
        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.Destructible);
    }
}

[HarmonyPatch(typeof(Destructible), nameof(Destructible.GetDestructibleType))]
internal static class DestructibleGetDestructibleTypePatch
{
    private static void Prefix(Destructible __instance)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            return;
        }

        ObjectDropManager.ApplyLazyDestructibleTypeIfNeeded(__instance);
    }
}

[HarmonyPatch(typeof(Destructible), "RPC_Damage")]
internal static class DestructibleDamagePatch
{
    private static void Prefix(Destructible __instance)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            return;
        }

        ObjectDropManager.ApplyLazyDestructibleScalarsIfNeeded(__instance);
    }
}

[HarmonyPatch(typeof(Destructible), "Destroy")]
internal static class DestructibleDestroyPatch
{
    private static void Prefix(Destructible __instance, out GameObject? __state)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            __state = null;
            return;
        }

        __state = ObjectDropManager.OverrideConditionalDestructibleSpawnWhenDestroyed(__instance);
    }

    private static void Postfix(Destructible __instance, GameObject? __state)
    {
        if (__state != null)
        {
            __instance.m_spawnWhenDestroyed = __state;
        }
    }
}

[HarmonyPatch(typeof(DropOnDestroyed), nameof(DropOnDestroyed.Awake))]
internal static class DropOnDestroyedAwakePatch
{
    private static void Postfix(DropOnDestroyed __instance)
    {
        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.DropOnDestroyed);
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.Awake))]
internal static class ContainerAwakePatch
{
    private static void Postfix(Container __instance)
    {
        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.Container);
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.Awake))]
internal static class PickableAwakePatch
{
    private static void Postfix(Pickable __instance)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            ObjectDropManager.TrackObjectInstance(__instance.gameObject);
            return;
        }

        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.Pickable);
    }
}

[HarmonyPatch(typeof(PickableItem), nameof(PickableItem.Awake))]
internal static class PickableItemAwakePatch
{
    private static void Postfix(PickableItem __instance)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            ObjectDropManager.TrackObjectInstance(__instance.gameObject);
            return;
        }

        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.PickableItem);
    }
}

[HarmonyPatch(typeof(Fish), nameof(Fish.Awake))]
internal static class FishAwakePatch
{
    private static void Postfix(Fish __instance)
    {
        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.Fish);
    }
}

[HarmonyPatch(typeof(Container), "AddDefaultItems")]
internal static class ContainerAddDefaultItemsPatch
{
    private static void Prefix(Container __instance, out DropTable? __state)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            __state = null;
            return;
        }

        __state = ObjectDropManager.OverrideContainerDrops(__instance);
    }

    private static void Postfix(Container __instance, DropTable? __state)
    {
        if (__state != null)
        {
            __instance.m_defaultItems = __state;
        }
    }
}

[HarmonyPatch(typeof(DropOnDestroyed), "OnDestroyed")]
internal static class DropOnDestroyedPatch
{
    private static void Prefix(DropOnDestroyed __instance, out DropTable? __state)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            __state = null;
            return;
        }

        __state = ObjectDropManager.OverrideConditionalDropOnDestroyed(__instance);
    }

    private static void Postfix(DropOnDestroyed __instance, DropTable? __state)
    {
        if (__state != null)
        {
            __instance.m_dropWhenDestroyed = __state;
        }
    }
}

[HarmonyPatch(typeof(MineRock), "RPC_Hit")]
internal static class MineRockHitPatch
{
    private static void Prefix(MineRock __instance, out DropTable? __state)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            __state = null;
            return;
        }

        ObjectDropManager.ApplyLazyMineRockScalarsIfNeeded(__instance);
        __state = ObjectDropManager.OverrideConditionalMineRockDrops(__instance);
    }

    private static void Postfix(MineRock __instance, DropTable? __state)
    {
        if (__state != null)
        {
            __instance.m_dropItems = __state;
        }
    }
}

[HarmonyPatch(typeof(MineRock), "Start")]
internal static class MineRockStartPatch
{
    private static void Postfix(MineRock __instance)
    {
        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.MineRock);
    }
}

[HarmonyPatch(typeof(MineRock5), "RPC_Damage")]
internal static class MineRock5DamagePatch
{
    private static void Prefix(MineRock5 __instance, out DropTable? __state)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            __state = null;
            return;
        }

        ObjectDropManager.ApplyLazyMineRock5ScalarsIfNeeded(__instance);
        __state = ObjectDropManager.OverrideConditionalMineRock5Drops(__instance);
    }

    private static void Postfix(MineRock5 __instance, DropTable? __state)
    {
        if (__state != null)
        {
            __instance.m_dropItems = __state;
        }
    }
}

[HarmonyPatch(typeof(MineRock5), nameof(MineRock5.Awake))]
internal static class MineRock5AwakePatch
{
    private static void Postfix(MineRock5 __instance)
    {
        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.MineRock5);
    }
}

[HarmonyPatch(typeof(TreeBase), "RPC_Damage")]
internal static class TreeBaseDamagePatch
{
    private static void Prefix(TreeBase __instance, out DropTable? __state)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            __state = null;
            return;
        }

        ObjectDropManager.ApplyLazyTreeBaseScalarsIfNeeded(__instance);
        __state = ObjectDropManager.OverrideConditionalTreeBaseDrops(__instance);
    }

    private static void Postfix(TreeBase __instance, DropTable? __state)
    {
        if (__state != null)
        {
            __instance.m_dropWhenDestroyed = __state;
        }
    }
}

[HarmonyPatch(typeof(TreeBase), nameof(TreeBase.Awake))]
internal static class TreeBaseAwakePatch
{
    private static void Postfix(TreeBase __instance)
    {
        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.TreeBase);
    }
}

[HarmonyPatch(typeof(TreeLog), "Destroy")]
internal static class TreeLogDestroyPatch
{
    private static void Prefix(TreeLog __instance, out DropTable? __state)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            __state = null;
            return;
        }

        __state = ObjectDropManager.OverrideConditionalTreeLogDrops(__instance);
    }

    private static void Postfix(TreeLog __instance, DropTable? __state)
    {
        if (__state != null)
        {
            __instance.m_dropWhenDestroyed = __state;
        }
    }
}

[HarmonyPatch(typeof(TreeLog), "RPC_Damage")]
internal static class TreeLogDamagePatch
{
    private static void Prefix(TreeLog __instance)
    {
        if (!PluginSettingsFacade.IsObjectDomainEnabled())
        {
            return;
        }

        ObjectDropManager.ApplyLazyTreeLogScalarsIfNeeded(__instance);
    }
}

[HarmonyPatch(typeof(TreeLog), nameof(TreeLog.Awake))]
internal static class TreeLogAwakePatch
{
    private static void Postfix(TreeLog __instance)
    {
        ObjectDropManager.QueueObjectInstanceReconcile(__instance.gameObject, clearCreatorRestrictedContainerContents: false, ObjectDropManager.LiveObjectComponentKind.TreeLog);
    }
}
