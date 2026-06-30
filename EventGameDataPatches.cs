using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

[HarmonyPatch(typeof(RandEventSystem), nameof(RandEventSystem.Awake))]
internal static class RandEventSystemAwakePatch
{
    private static void Postfix()
    {
        EventManager.ApplyGlobalEventSettings();
    }
}

[HarmonyPatch(typeof(RandEventSystem), nameof(RandEventSystem.Start))]
internal static class RandEventSystemStartPatch
{
    private static void Postfix()
    {
        EventManager.ApplyGlobalEventSettings();
    }
}

[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Start))]
internal static class ZoneSystemStartEventPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        EventManager.NotifyGameDataReady("ZoneSystem.Start");
    }
}

[HarmonyPatch(typeof(RandEventSystem), "OnDestroy")]
internal static class RandEventSystemOnDestroyPatch
{
    private static void Prefix()
    {
        EventManager.OnRandEventSystemDestroyed();
    }
}

[HarmonyPatch(typeof(RandEventSystem), "FixedUpdate")]
internal static class RandEventSystemFixedUpdatePatch
{
    private static bool Prefix(RandEventSystem __instance)
    {
        return !EventManager.TryRunMultipleEventsFixedUpdate(__instance);
    }
}

[HarmonyPatch(typeof(RandEventSystem), "SetRandomEvent")]
internal static class RandEventSystemSetRandomEventPatch
{
    private static bool Prefix(RandEventSystem __instance, RandomEvent ev, Vector3 pos)
    {
        return !EventManager.TryHandleMultipleSetRandomEvent(__instance, ev, pos);
    }
}

[HarmonyPatch(typeof(RandEventSystem), "SendCurrentRandomEvent")]
internal static class RandEventSystemSendCurrentRandomEventPatch
{
    private static bool Prefix(RandEventSystem __instance)
    {
        return !EventManager.TrySendMultipleCurrentRandomEvent(__instance);
    }
}

[HarmonyPatch(typeof(RandEventSystem), "UpdateRandomEvent")]
internal static class RandEventSystemUpdateRandomEventPatch
{
    private static bool Prefix(RandEventSystem __instance, float dt)
    {
        return !EventManager.TryRunCheckPerPlayerRandomUpdate(__instance, dt);
    }
}

[HarmonyPatch(typeof(RandomEvent), nameof(RandomEvent.Clone))]
internal static class RandomEventClonePatch
{
    private static void Postfix(RandomEvent __instance, RandomEvent __result)
    {
        EventManager.CopySpawnPayloads(__instance, __result);
    }
}

[HarmonyPatch(typeof(RandEventSystem), "CheckBase")]
internal static class RandEventSystemCheckBasePatch
{
    private static bool Prefix(RandomEvent ev, RandEventSystem.PlayerEventData player, ref bool __result)
    {
        if (!EventManager.TryCheckBase(ev, player, out bool result))
        {
            return true;
        }

        __result = result;
        return false;
    }
}

[HarmonyPatch(typeof(RandEventSystem), "InValidBiome")]
internal static class RandEventSystemInValidBiomePatch
{
    private static void Postfix(RandomEvent ev, Vector3 point, ref bool __result)
    {
        __result = EventManager.PassesExtraChecks(ev, point, __result);
    }
}

[HarmonyPatch(typeof(RandomEvent), nameof(RandomEvent.OnStart))]
internal static class RandomEventOnStartPatch
{
    private static void Postfix(RandomEvent __instance)
    {
        EventManager.RunStartCommands(__instance);
    }
}

[HarmonyPatch(typeof(RandomEvent), nameof(RandomEvent.OnStop))]
internal static class RandomEventOnStopPatch
{
    private static void Postfix(RandomEvent __instance)
    {
        EventManager.RunEndCommands(__instance);
    }
}
