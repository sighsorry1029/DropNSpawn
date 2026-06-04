using HarmonyLib;

namespace DropNSpawn;

[HarmonyPatch(typeof(Vegvisir), nameof(Vegvisir.Interact))]
internal static class VegvisirInteractGlobalEffectsPatch
{
    private static void Postfix(Vegvisir __instance, Humanoid character, bool hold, bool __result)
    {
        LocationManager.TryApplyVegvisirGlobalEffects(__instance, character, hold, __result);
    }
}
