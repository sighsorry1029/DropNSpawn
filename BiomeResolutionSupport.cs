using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace DropNSpawn;

internal static class BiomeResolutionSupport
{
    private static readonly Dictionary<string, Heightmap.Biome> VanillaBiomeLookup = BuildVanillaBiomeLookup();
    private static readonly Type? ExpandWorldDataBiomeManagerType = Type.GetType("ExpandWorldData.BiomeManager, ExpandWorldData");
    private static readonly Type? ExpandWorldDataDataManagerType = Type.GetType("ExpandWorldData.DataManager, ExpandWorldData");
    private static readonly PropertyInfo? ExpandWorldDataIsReadyProperty = ExpandWorldDataDataManagerType
        ?.GetProperty("IsReady", BindingFlags.Public | BindingFlags.Static);
    private static readonly MethodInfo? ExpandWorldDataTryGetBiomeMethod = Type
        .GetType("ExpandWorldData.BiomeManager, ExpandWorldData")
        ?.GetMethod("TryGetBiome", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Heightmap.Biome).MakeByRefType() }, null);
    private static readonly MethodInfo? ExpandWorldDataTryGetDisplayNameMethod = ExpandWorldDataBiomeManagerType
        ?.GetMethod("TryGetDisplayName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Heightmap.Biome), typeof(string).MakeByRefType() }, null);

    internal static bool TryResolveBiomeToken(string? configuredBiome, out Heightmap.Biome biome)
    {
        string trimmedName = (configuredBiome ?? "").Trim();
        if (trimmedName.Length == 0)
        {
            biome = Heightmap.Biome.None;
            return false;
        }

        if (string.Equals(trimmedName, nameof(Heightmap.Biome.All), StringComparison.OrdinalIgnoreCase))
        {
            biome = Heightmap.Biome.All;
            return true;
        }

        if (int.TryParse(trimmedName, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericMask) &&
            numericMask != 0)
        {
            biome = (Heightmap.Biome)numericMask;
            return true;
        }

        if (Enum.TryParse(trimmedName, true, out biome))
        {
            return true;
        }

        if (VanillaBiomeLookup.TryGetValue(NormalizeBiomeToken(trimmedName), out biome))
        {
            return true;
        }

        if (TryResolveExpandWorldDataBiome(trimmedName, out biome))
        {
            return true;
        }

        biome = Heightmap.Biome.None;
        return false;
    }

    internal static bool MatchesBiome(Heightmap.Biome currentBiome, string? configuredBiome)
    {
        return TryResolveBiomeToken(configuredBiome, out Heightmap.Biome configured) &&
               (currentBiome & configured) != 0;
    }

    internal static bool TryResolveBiomeMask(IEnumerable<string>? configuredBiomes, out Heightmap.Biome biomeMask)
    {
        biomeMask = Heightmap.Biome.None;
        bool sawValue = false;
        foreach (string? rawBiome in configuredBiomes ?? Array.Empty<string>())
        {
            string configuredBiome = (rawBiome ?? "").Trim();
            if (configuredBiome.Length == 0)
            {
                continue;
            }

            sawValue = true;
            if (!TryResolveBiomeToken(configuredBiome, out Heightmap.Biome resolvedBiome))
            {
                biomeMask = Heightmap.Biome.None;
                return false;
            }

            if (resolvedBiome == Heightmap.Biome.All)
            {
                biomeMask = Heightmap.Biome.All;
                return true;
            }

            biomeMask |= resolvedBiome;
        }

        return sawValue;
    }

    internal static Heightmap.Biome? ResolveBiomeMaskOrNull(IEnumerable<string>? configuredBiomes)
    {
        return TryResolveBiomeMask(configuredBiomes, out Heightmap.Biome biomeMask)
            ? biomeMask
            : null;
    }

    internal static string GetBiomeDisplayName(Heightmap.Biome biome)
    {
        if (TryGetExpandWorldDataBiomeDisplayName(biome, out string displayName))
        {
            return displayName;
        }

        return Enum.GetName(typeof(Heightmap.Biome), biome) ??
               ((int)biome).ToString(CultureInfo.InvariantCulture);
    }

    internal static bool IsExpandWorldDataPresent()
    {
        return ExpandWorldDataTryGetBiomeMethod != null || ExpandWorldDataIsReadyProperty != null;
    }

    internal static bool IsExpandWorldDataReadyOrUnavailable()
    {
        if (ExpandWorldDataIsReadyProperty == null)
        {
            return true;
        }

        try
        {
            return ExpandWorldDataIsReadyProperty.GetValue(null) as bool? ?? true;
        }
        catch
        {
            return true;
        }
    }

    internal static bool ShouldWaitForExpandWorldDataBiomeResolution(IEnumerable<string>? configuredBiomes, Heightmap.Biome? resolvedBiomeMask)
    {
        if (resolvedBiomeMask.HasValue ||
            !IsExpandWorldDataPresent() ||
            IsExpandWorldDataReadyOrUnavailable())
        {
            return false;
        }

        return !TryResolveBiomeMask(configuredBiomes, out _);
    }

    internal static string NormalizeBiomeToken(string? value)
    {
        StringBuilder builder = new();
        foreach (char character in (value ?? "").Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static Dictionary<string, Heightmap.Biome> BuildVanillaBiomeLookup()
    {
        Dictionary<string, Heightmap.Biome> lookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (Heightmap.Biome biome in Enum.GetValues(typeof(Heightmap.Biome)))
        {
            lookup[NormalizeBiomeToken(biome.ToString())] = biome;
        }

        lookup["ashlands"] = Heightmap.Biome.AshLands;
        return lookup;
    }

    private static bool TryResolveExpandWorldDataBiome(string configuredBiome, out Heightmap.Biome biome)
    {
        if (ExpandWorldDataTryGetBiomeMethod == null)
        {
            biome = Heightmap.Biome.None;
            return false;
        }

        object[] args = { configuredBiome, Heightmap.Biome.None };
        if (ExpandWorldDataTryGetBiomeMethod.Invoke(null, args) is bool matched &&
            matched &&
            args[1] is Heightmap.Biome customBiome)
        {
            biome = customBiome;
            return true;
        }

        biome = Heightmap.Biome.None;
        return false;
    }

    private static bool TryGetExpandWorldDataBiomeDisplayName(Heightmap.Biome biome, out string displayName)
    {
        displayName = "";
        if (ExpandWorldDataTryGetDisplayNameMethod == null)
        {
            return false;
        }

        object?[] args = { biome, null };
        if (ExpandWorldDataTryGetDisplayNameMethod.Invoke(null, args) is bool matched &&
            matched &&
            args[1] is string resolvedName &&
            !string.IsNullOrWhiteSpace(resolvedName))
        {
            displayName = resolvedName;
            return true;
        }

        return false;
    }
}
