using HarmonyLib;

namespace DropNSpawn;

[HarmonyPatch(typeof(RuneStone), nameof(RuneStone.Interact))]
internal static class RuneStoneInteractGlobalPinsPatch
{
    private struct RunestoneInteractionState
    {
        public bool Hold { get; set; }
        public string? OriginalLocationName { get; set; }
    }

    private static void Prefix(RuneStone __instance, bool hold, ref RunestoneInteractionState __state)
    {
        __state.Hold = hold;
        __state.OriginalLocationName = __instance.m_locationName;
    }

    private static void Postfix(RuneStone __instance, RunestoneInteractionState __state)
    {
        LocationManager.TryApplyRunestoneGlobalPins(__instance, __state.Hold, __state.OriginalLocationName);
    }
}
