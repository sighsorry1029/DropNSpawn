using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Bootstrap;
using SoftReferenceableAssets;
using UnityEngine;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal static class ReferenceRefreshSupport
{
    internal const string CurrentReferenceLogicVersion = "2026-03-26-full-rewrite-v1";

    internal const string MoreWorldLocationsGuid = "warpalicious.More_World_Locations_AIO";
    internal const string PartialLocationReferenceHeader = "# DropNSpawn location scan: automatic-mwl-excluded-v1";

    // Only automatic reference generation uses this policy. Runtime rules and
    // explicit exports never use the exclusion list.
    internal sealed class LocationReferenceScan
    {
        private readonly HashSet<AssetID>? _excludedAssets;

        internal LocationReferenceScan(HashSet<AssetID>? excludedAssets)
        {
            _excludedAssets = excludedAssets;
            Signature = ComputeStableHashForKeys(new[] { PartialLocationReferenceHeader }.Concat(
                excludedAssets == null ? new[] { "all-locations-deferred" } : excludedAssets.Select(id => id.ToString())));
        }

        internal string Signature { get; }
        internal bool ShouldSkip(AssetID assetId) => _excludedAssets == null || _excludedAssets.Contains(assetId);

        internal string Annotate(string content) => PartialLocationReferenceHeader + Environment.NewLine +
            (_excludedAssets == null
                ? "# Location interiors were not scanned because the MWL manifest could not be read."
                : "# MWL location interiors were not scanned; registered prefab references are still included.") + Environment.NewLine +
            "# Explicit dns:reference object/spawner exports include all locations and can be expensive." + Environment.NewLine + content;
    }

    internal static LocationReferenceScan? CreateAutomaticLocationScan()
    {
        if (!Chainloader.PluginInfos.TryGetValue(MoreWorldLocationsGuid, out var plugin))
        {
            return null;
        }

        try
        {
            string directory = Path.GetDirectoryName(plugin.Location)!;
            string? path = Directory.EnumerateFiles(directory)
                .FirstOrDefault(file => string.Equals(Path.GetFileName(file), "assetBundleManifest_full", StringComparison.OrdinalIgnoreCase));
            SoftReferenceableAssets.AssetBundleManifest? manifest = path == null
                ? null
                : SoftReferenceableAssets.AssetBundleManifest.DeserializeFromDisk(path);
            if (manifest != null && manifest.AssetCount > 0)
            {
                return new LocationReferenceScan(manifest.m_assetToLocationMap.Keys.ToHashSet());
            }
        }
        catch (Exception ex)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Could not read MWL reference-scan metadata: {ex.Message}");
        }

        // Do not fall back to force-loading every location when identification fails.
        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            "MWL manifest unavailable: automatic location-interior references are deferred; registered prefab references and override rules remain available.");
        return new LocationReferenceScan(null);
    }

    internal static IEnumerable<(string LocationPrefab, GameObject RootPrefab)> EnumerateLocationRootPrefabs(LocationReferenceScan? scan = null)
    {
        if (ZoneSystem.instance == null)
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZoneSystem.ZoneLocation location in ZoneSystem.instance.m_locations)
        {
            // Work on a copy: do not leave the borrowed asset cached on ZoneLocation.
            SoftReference<GameObject> prefab = location.m_prefab;
            if (!prefab.IsValid || scan?.ShouldSkip(prefab.m_assetID) == true)
            {
                continue;
            }

            string name = (prefab.Name ?? "").Trim();
            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            try
            {
                if (prefab.Load() != LoadResult.Succeeded)
                {
                    continue;
                }

                GameObject root = prefab.Asset;
                if (root != null)
                {
                    yield return (name, root);
                }
            }
            finally
            {
                // Load holds a reference even on a failed load. Iterator disposal
                // also runs this on consumer exceptions or early termination.
                prefab.Release();
            }
        }
    }

    internal static void WarnBeforeFullLocationScan()
    {
        if (Chainloader.PluginInfos.ContainsKey(MoreWorldLocationsGuid))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
                "Explicit full location reference export includes MWL interiors. This can take minutes and use substantial memory on low-memory systems.");
        }
    }

    internal static string ComputeStableHashForKeys(IEnumerable<string?> keys)
    {
        StringBuilder builder = new();
        foreach (string key in (keys ?? Enumerable.Empty<string?>())
                     .Select(NormalizeKey)
                     .Where(key => key.Length > 0)
                     .OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(key);
        }

        return ComputeStableHash(builder.ToString());
    }

    internal static string SerializeReferenceSections<T>(IEnumerable<T> entries, Func<T, string> getPrefabName, ISerializer serializer)
    {
        List<PrefabOwnerSection<T>> sections = PrefabOutputSections.BuildSections(entries ?? Enumerable.Empty<T>(), getPrefabName);
        return PrefabOutputSections.SerializeReferenceSections(sections, serializer);
    }

    internal static HashSet<string> ToNormalizedKeySet(IEnumerable<string?> keys)
    {
        HashSet<string> normalizedKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? key in keys ?? Enumerable.Empty<string?>())
        {
            string normalizedKey = NormalizeKey(key);
            if (normalizedKey.Length > 0)
            {
                normalizedKeys.Add(normalizedKey);
            }
        }

        return normalizedKeys;
    }

    internal static string NormalizeKey(string? key)
    {
        return (key ?? "").Trim();
    }

    internal static bool ShouldSkipAutoUpdate(string stateKey, string referencePath, string sourceSignature, string? logicVersion = null)
    {
        if (string.IsNullOrWhiteSpace(stateKey) ||
            string.IsNullOrWhiteSpace(referencePath) ||
            string.IsNullOrWhiteSpace(sourceSignature) ||
            !File.Exists(referencePath))
        {
            return false;
        }

        string statePath = GetAutoUpdateStatePath(stateKey);
        if (!File.Exists(statePath))
        {
            return false;
        }

        try
        {
            string normalizedLogicVersion = (logicVersion ?? "").Trim();
            string[] lines = File.ReadAllLines(statePath);
            if (lines.Length < 2)
            {
                return false;
            }

            string storedSourceSignature = (lines[0] ?? "").Trim();
            string storedFileStamp = (lines[1] ?? "").Trim();
            string storedLogicVersion = lines.Length >= 4 ? (lines[3] ?? "").Trim() : "";
            if (normalizedLogicVersion.Length > 0 &&
                !string.Equals(storedLogicVersion, normalizedLogicVersion, StringComparison.Ordinal))
            {
                return false;
            }

            return string.Equals(storedSourceSignature, sourceSignature, StringComparison.Ordinal) &&
                   string.Equals(storedFileStamp, BuildReferenceFileStamp(referencePath), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static void RecordAutoUpdateState(string stateKey, string referencePath, string sourceSignature, string? precheckSignature = null, string? logicVersion = null)
    {
        if (string.IsNullOrWhiteSpace(stateKey) ||
            string.IsNullOrWhiteSpace(referencePath) ||
            string.IsNullOrWhiteSpace(sourceSignature) ||
            !File.Exists(referencePath))
        {
            return;
        }

        string statePath = GetAutoUpdateStatePath(stateKey);
        string content = sourceSignature.Trim() + Environment.NewLine +
                         BuildReferenceFileStamp(referencePath) + Environment.NewLine +
                         (precheckSignature ?? "").Trim() + Environment.NewLine +
                         (logicVersion ?? "").Trim() + Environment.NewLine;
        GeneratedFileWriter.WriteAllTextIfChanged(statePath, content);
    }

    internal static string ComputeStableHash(string? value)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
        byte[] hash = sha256.ComputeHash(bytes);
        StringBuilder builder = new(hash.Length * 2);
        foreach (byte part in hash)
        {
            builder.Append(part.ToString("x2"));
        }

        return builder.ToString();
    }

    private static string GetAutoUpdateStatePath(string stateKey)
    {
        string sanitizedKey = SanitizeFileName(stateKey);
        return Path.Combine(GetAutoUpdateStateDirectoryPath(), $".reference-state.{sanitizedKey}.txt");
    }

    private static string GetAutoUpdateStateDirectoryPath()
    {
        return Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, "cache");
    }

    private static string BuildReferenceFileStamp(string referencePath)
    {
        FileInfo fileInfo = new(referencePath);
        return $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
    }

    private static string SanitizeFileName(string value)
    {
        StringBuilder builder = new(value.Length);
        HashSet<char> invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        foreach (char character in value)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }
}
