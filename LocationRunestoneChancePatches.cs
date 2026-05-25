using HarmonyLib;

namespace DropNSpawn;

[HarmonyPatch(typeof(RuneStone), nameof(RuneStone.Interact))]
internal static class RuneStoneInteractPinChancePatch
{
    private struct SuppressedLocationState
    {
        public bool Hold { get; set; }
        public bool Suppressed { get; set; }
        public string? OriginalLocationName { get; set; }
    }

    private static void Prefix(RuneStone __instance, bool hold, ref SuppressedLocationState __state)
    {
        __state.Hold = hold;
        __state.OriginalLocationName = __instance.m_locationName;
        if (hold || !LocationManager.ShouldSuppressRunestonePinDiscovery(__instance))
        {
            return;
        }

        __state.Suppressed = true;
        __instance.m_locationName = "";
    }

    private static void Postfix(RuneStone __instance, SuppressedLocationState __state)
    {
        if (__state.Suppressed)
        {
            __instance.m_locationName = __state.OriginalLocationName ?? "";
        }

        LocationManager.TryApplyRunestoneGlobalPins(__instance, __state.Hold, __state.OriginalLocationName);
    }

    private static void Finalizer(RuneStone __instance, SuppressedLocationState __state)
    {
        if (__state.Suppressed)
        {
            __instance.m_locationName = __state.OriginalLocationName ?? "";
        }
    }
}
