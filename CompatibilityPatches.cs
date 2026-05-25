using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

internal static class PreviewGhostCompatibility
{
    private static readonly FieldInfo? ZNetViewFunctionsField = AccessTools.Field(typeof(ZNetView), "m_functions");

    internal static void EnsureNoOpRpc(Component? component, string name, System.Action<long, HitData> handler)
    {
        if (!TryGetPreviewGhostNetView(component, out ZNetView? nview) || HasRegisteredRpc(nview, name))
        {
            return;
        }

        nview.Register(name, handler);
    }

    internal static void EnsureNoOpRpc(Component? component, string name, System.Action<long, HitData, int> handler)
    {
        if (!TryGetPreviewGhostNetView(component, out ZNetView? nview) || HasRegisteredRpc(nview, name))
        {
            return;
        }

        nview.Register(name, handler);
    }

    internal static void EnsureNoOpRpc(Component? component, string name, System.Action<long, int, Vector3> handler)
    {
        if (!TryGetPreviewGhostNetView(component, out ZNetView? nview) || HasRegisteredRpc(nview, name))
        {
            return;
        }

        nview.Register(name, handler);
    }

    internal static void EnsureNoOpRpc(Component? component, string name, System.Action<long, string> handler)
    {
        if (!TryGetPreviewGhostNetView(component, out ZNetView? nview) || HasRegisteredRpc(nview, name))
        {
            return;
        }

        nview.Register(name, handler);
    }

    internal static void EnsureNoOpRpc(Component? component, string name, System.Action<long, Vector3, float, ZDOID> handler)
    {
        if (!TryGetPreviewGhostNetView(component, out ZNetView? nview) || HasRegisteredRpc(nview, name))
        {
            return;
        }

        nview.Register(name, handler);
    }

    internal static bool ShouldRunExternalNetLogic(object? instance)
    {
        return instance is not Component component || !TryGetPreviewGhostNetView(component, out _);
    }

    private static bool TryGetPreviewGhostNetView(Component? component, out ZNetView? nview)
    {
        nview = null;
        if (component == null)
        {
            return false;
        }

        nview = component.GetComponent<ZNetView>();
        return nview != null && nview.GetZDO() == null;
    }

    private static bool HasRegisteredRpc(ZNetView nview, string name)
    {
        if (ZNetViewFunctionsField?.GetValue(nview) is not IDictionary functions)
        {
            return false;
        }

        return functions.Contains(name.GetStableHashCode());
    }
}

[HarmonyPatch(typeof(FootStep), "Awake")]
internal static class FootStepPreviewGhostPatch
{
    private static void Postfix(FootStep __instance)
    {
        PreviewGhostCompatibility.EnsureNoOpRpc(__instance, "Step", static (_, _, _) => { });
    }
}

[HarmonyPatch(typeof(Character), "Awake")]
internal static class CharacterPreviewGhostPatch
{
    private static void Postfix(Character __instance)
    {
        PreviewGhostCompatibility.EnsureNoOpRpc(__instance, "RPC_Damage", static (_, _) => { });
    }
}

[HarmonyPatch(typeof(Destructible), "Awake")]
internal static class DestructiblePreviewGhostPatch
{
    private static void Postfix(Destructible __instance)
    {
        PreviewGhostCompatibility.EnsureNoOpRpc(__instance, "RPC_Damage", static (_, _) => { });
    }
}

[HarmonyPatch(typeof(WearNTear), "Awake")]
internal static class WearNTearPreviewGhostPatch
{
    private static void Postfix(WearNTear __instance)
    {
        PreviewGhostCompatibility.EnsureNoOpRpc(__instance, "RPC_Damage", static (_, _) => { });
    }
}

[HarmonyPatch(typeof(TreeBase), "Awake")]
internal static class TreeBasePreviewGhostPatch
{
    private static void Postfix(TreeBase __instance)
    {
        PreviewGhostCompatibility.EnsureNoOpRpc(__instance, "RPC_Damage", static (_, _) => { });
    }
}

[HarmonyPatch(typeof(MineRock5), "Awake")]
internal static class MineRock5PreviewGhostPatch
{
    private static void Postfix(MineRock5 __instance)
    {
        PreviewGhostCompatibility.EnsureNoOpRpc(__instance, "RPC_Damage", static (_, _, _) => { });
    }
}

[HarmonyPatch(typeof(TreeLog), "Awake")]
internal static class TreeLogPreviewGhostPatch
{
    private static void Postfix(TreeLog __instance)
    {
        PreviewGhostCompatibility.EnsureNoOpRpc(__instance, "RPC_Damage", static (_, _) => { });
    }
}

[HarmonyPatch(typeof(BaseAI), "Awake")]
internal static class BaseAIPreviewGhostPatch
{
    private static void Postfix(BaseAI __instance)
    {
        PreviewGhostCompatibility.EnsureNoOpRpc(__instance, "OnNearProjectileHit", static (_, _, _, _) => { });
    }
}

[HarmonyPatch(typeof(ZSyncAnimation), "Awake")]
internal static class ZSyncAnimationPreviewGhostPatch
{
    private static void Postfix(ZSyncAnimation __instance)
    {
        PreviewGhostCompatibility.EnsureNoOpRpc(__instance, "SetTrigger", static (_, _) => { });
    }
}

[HarmonyPatch]
internal static class JewelcraftingGemSpawnerPreviewGhostPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type? gemSpawnerType = SafeTypeLookup.FindLoadedType("Jewelcrafting.DestructibleSetup+GemSpawner", "Jewelcrafting");
        if (gemSpawnerType == null)
        {
            yield break;
        }

        MethodBase? awake = AccessTools.Method(gemSpawnerType, "Awake");
        if (awake != null)
        {
            yield return awake;
        }

        MethodBase? start = AccessTools.Method(gemSpawnerType, "Start");
        if (start != null)
        {
            yield return start;
        }

        MethodBase? checkSpawn = AccessTools.Method(gemSpawnerType, "CheckSpawn");
        if (checkSpawn != null)
        {
            yield return checkSpawn;
        }
    }

    private static bool Prefix(object __instance)
    {
        return PreviewGhostCompatibility.ShouldRunExternalNetLogic(__instance);
    }
}

[HarmonyPatch]
internal static class EspSpawnSystemBiomeNamesPatch
{
    private const string ValidColor = "#FFFF00";
    private const string InvalidColor = "#B2BEB5";

    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type? textsType = SafeTypeLookup.FindLoadedType("ESP.Texts", "ESP");
        if (textsType == null)
        {
            yield break;
        }

        MethodBase? getNames = AccessTools.Method(textsType, "GetNames", new[] { typeof(Heightmap.Biome), typeof(Heightmap.Biome) });
        if (getNames != null)
        {
            yield return getNames;
        }
    }

    private static bool Prefix(Heightmap.Biome biome, Heightmap.Biome validBiome, ref string __result)
    {
        __result = FormatBiomeNames(biome, validBiome);
        return false;
    }

    private static string FormatBiomeNames(Heightmap.Biome biome, Heightmap.Biome validBiome)
    {
        if (biome == Heightmap.Biome.None)
        {
            return "None";
        }

        if (biome == Heightmap.Biome.All)
        {
            return Colorize(BiomeResolutionSupport.GetBiomeDisplayName(Heightmap.Biome.All), IsValid(validBiome, Heightmap.Biome.All));
        }

        List<string> names = new();
        int remainingMask = (int)biome;
        foreach (Heightmap.Biome candidate in Enum.GetValues(typeof(Heightmap.Biome)))
        {
            if (candidate == Heightmap.Biome.None || candidate == Heightmap.Biome.All)
            {
                continue;
            }

            if ((biome & candidate) == Heightmap.Biome.None)
            {
                continue;
            }

            names.Add(Colorize(BiomeResolutionSupport.GetBiomeDisplayName(candidate), IsValid(validBiome, candidate)));
            remainingMask &= ~(int)candidate;
        }

        AppendUnknownBiomeBits(names, remainingMask, validBiome);
        return names.Count > 0 ? string.Join(", ", names) : "None";
    }

    private static void AppendUnknownBiomeBits(List<string> names, int remainingMask, Heightmap.Biome validBiome)
    {
        if (remainingMask == 0)
        {
            return;
        }

        uint remainingBits = unchecked((uint)remainingMask);
        for (uint bit = 1; bit != 0 && bit <= remainingBits; bit <<= 1)
        {
            if ((remainingBits & bit) == 0)
            {
                continue;
            }

            Heightmap.Biome biomeBit = (Heightmap.Biome)bit;
            names.Add(Colorize(BiomeResolutionSupport.GetBiomeDisplayName(biomeBit), IsValid(validBiome, biomeBit)));
        }
    }

    private static bool IsValid(Heightmap.Biome validBiome, Heightmap.Biome biome)
    {
        return (validBiome & biome) != Heightmap.Biome.None;
    }

    private static string Colorize(string value, bool valid)
    {
        return $"<color={(valid ? ValidColor : InvalidColor)}>{value}</color>";
    }
}
