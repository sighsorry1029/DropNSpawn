using System;
using System.Collections;
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
    private static readonly FieldInfo? ExpandWorldDataConfigSyncField = Type
        .GetType("ExpandWorldData.EWD, ExpandWorldData")
        ?.GetField("ConfigSync", BindingFlags.Public | BindingFlags.Static);
    private static readonly PropertyInfo? ExpandWorldDataIsReadyProperty = ExpandWorldDataDataManagerType
        ?.GetProperty("IsReady", BindingFlags.Public | BindingFlags.Static);
    private static readonly PropertyInfo? ExpandWorldDataIsSourceOfTruthProperty = ExpandWorldDataConfigSyncField
        ?.FieldType.GetProperty("IsSourceOfTruth", BindingFlags.Public | BindingFlags.Instance);
    private static readonly PropertyInfo? ExpandWorldDataInitialSyncDoneProperty = ExpandWorldDataConfigSyncField
        ?.FieldType.GetProperty("InitialSyncDone", BindingFlags.Public | BindingFlags.Instance);
    private static readonly MethodInfo? ExpandWorldDataTryGetBiomeMethod = Type
        .GetType("ExpandWorldData.BiomeManager, ExpandWorldData")
        ?.GetMethod("TryGetBiome", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Heightmap.Biome).MakeByRefType() }, null);
    private static readonly MethodInfo? ExpandWorldDataTryGetDisplayNameMethod = ExpandWorldDataBiomeManagerType
        ?.GetMethod("TryGetDisplayName", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Heightmap.Biome), typeof(string).MakeByRefType() }, null);
    private static readonly FieldInfo? ExpandWorldDataBiomeToDisplayNameField = ExpandWorldDataBiomeManagerType
        ?.GetField("BiomeToDisplayName", BindingFlags.Public | BindingFlags.Static);

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
            biome = GetKnownBiomeMask();
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

    internal static Heightmap.Biome GetKnownBiomeMask()
    {
        Heightmap.Biome mask = Heightmap.Biome.All;
        if (TryGetExpandWorldDataKnownBiomeMask(out Heightmap.Biome expandWorldDataMask))
        {
            mask |= expandWorldDataMask;
        }

        return mask;
    }

    internal static bool IsKnownAllBiomeMask(Heightmap.Biome biomes)
    {
        return biomes == Heightmap.Biome.All || biomes == GetKnownBiomeMask();
    }

    internal static List<string> ConvertBiomeMaskToNames(Heightmap.Biome biomes)
    {
        if (biomes == Heightmap.Biome.None)
        {
            return new List<string>();
        }

        if (IsKnownAllBiomeMask(biomes))
        {
            return new List<string> { nameof(Heightmap.Biome.All) };
        }

        List<string> values = new();
        uint remainingMask = unchecked((uint)(int)biomes);
        foreach (Heightmap.Biome biome in GetKnownBiomeValues())
        {
            uint biomeMask = unchecked((uint)(int)biome);
            if (biomeMask == 0 || (remainingMask & biomeMask) != biomeMask)
            {
                continue;
            }

            values.Add(GetBiomeDisplayName(biome));
            remainingMask &= ~biomeMask;
        }

        AppendRemainingBiomeBits(values, remainingMask);
        return values;
    }

    internal static bool IsExpandWorldDataPresent()
    {
        return ExpandWorldDataTryGetBiomeMethod != null || ExpandWorldDataIsReadyProperty != null;
    }

    internal static bool IsExpandWorldDataReadyOrUnavailable()
    {
        if (ExpandWorldDataIsReadyProperty != null)
        {
            try
            {
                if (ExpandWorldDataIsReadyProperty.GetValue(null) is bool isReady)
                {
                    return isReady;
                }
            }
            catch
            {
                // Fall through to the ConfigSync contract used by newer EWD versions.
            }
        }

        if (ExpandWorldDataConfigSyncField == null ||
            ExpandWorldDataIsSourceOfTruthProperty == null ||
            ExpandWorldDataInitialSyncDoneProperty == null)
        {
            return true;
        }

        try
        {
            object? configSync = ExpandWorldDataConfigSyncField.GetValue(null);
            if (configSync == null)
            {
                return true;
            }

            if (ExpandWorldDataIsSourceOfTruthProperty.GetValue(configSync) is not bool isSourceOfTruth ||
                ExpandWorldDataInitialSyncDoneProperty.GetValue(configSync) is not bool initialSyncDone)
            {
                return true;
            }

            return isSourceOfTruth || initialSyncDone;
        }
        catch
        {
            return true;
        }
    }

    internal static bool ShouldWaitForExpandWorldDataBiomeResolution(IEnumerable<string>? configuredBiomes, Heightmap.Biome? resolvedBiomeMask)
    {
        if (!IsExpandWorldDataPresent() ||
            IsExpandWorldDataReadyOrUnavailable())
        {
            return false;
        }

        if (ContainsAllBiomeToken(configuredBiomes))
        {
            return true;
        }

        if (resolvedBiomeMask.HasValue)
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

    private static List<Heightmap.Biome> GetKnownBiomeValues()
    {
        List<Heightmap.Biome> values = new();
        HashSet<int> seen = new();

        foreach (Heightmap.Biome biome in Enum.GetValues(typeof(Heightmap.Biome)))
        {
            AddKnownBiomeValue(values, seen, biome);
        }

        foreach (Heightmap.Biome biome in GetExpandWorldDataKnownBiomeValues())
        {
            AddKnownBiomeValue(values, seen, biome);
        }

        return values;
    }

    private static void AddKnownBiomeValue(List<Heightmap.Biome> values, HashSet<int> seen, Heightmap.Biome biome)
    {
        if (biome == Heightmap.Biome.None || biome == Heightmap.Biome.All)
        {
            return;
        }

        int numeric = (int)biome;
        if (numeric == 0 || !seen.Add(numeric))
        {
            return;
        }

        values.Add(biome);
    }

    private static IEnumerable<Heightmap.Biome> GetExpandWorldDataKnownBiomeValues()
    {
        if (!TryGetExpandWorldDataBiomeDictionary(out IDictionary? biomeDictionary))
        {
            yield break;
        }

        foreach (DictionaryEntry entry in biomeDictionary!)
        {
            if (entry.Key is Heightmap.Biome biome)
            {
                yield return biome;
            }
        }
    }

    private static bool TryGetExpandWorldDataKnownBiomeMask(out Heightmap.Biome mask)
    {
        mask = Heightmap.Biome.None;
        bool sawBiome = false;
        foreach (Heightmap.Biome biome in GetExpandWorldDataKnownBiomeValues())
        {
            if (biome == Heightmap.Biome.None || biome == Heightmap.Biome.All)
            {
                continue;
            }

            mask |= biome;
            sawBiome = true;
        }

        return sawBiome;
    }

    private static bool TryGetExpandWorldDataBiomeDictionary(out IDictionary? biomeDictionary)
    {
        biomeDictionary = null;
        if (ExpandWorldDataBiomeToDisplayNameField == null)
        {
            return false;
        }

        try
        {
            biomeDictionary = ExpandWorldDataBiomeToDisplayNameField.GetValue(null) as IDictionary;
            return biomeDictionary != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsAllBiomeToken(IEnumerable<string>? configuredBiomes)
    {
        foreach (string? rawBiome in configuredBiomes ?? Array.Empty<string>())
        {
            if (string.Equals((rawBiome ?? "").Trim(), nameof(Heightmap.Biome.All), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendRemainingBiomeBits(List<string> values, uint remainingMask)
    {
        if (remainingMask == 0)
        {
            return;
        }

        for (uint bit = 1; bit != 0 && bit <= remainingMask; bit <<= 1)
        {
            if ((remainingMask & bit) == 0)
            {
                continue;
            }

            values.Add(GetBiomeDisplayName((Heightmap.Biome)(int)bit));
        }
    }
}
