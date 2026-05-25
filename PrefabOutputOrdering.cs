using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class PrefabOwnerSection<T>
{
    internal PrefabOwnerSection(string ownerName, List<T> entries)
    {
        OwnerName = string.IsNullOrWhiteSpace(ownerName) ? PrefabOwnerCatalog.UnknownOwnerName : ownerName.Trim();
        Entries = entries ?? new List<T>();
    }

    internal string OwnerName { get; }
    internal List<T> Entries { get; }
}

internal static class PrefabOutputSections
{
    private sealed class GroupedEntry<T>
    {
        public T Entry { get; set; } = default!;
        public string PrefabName { get; set; } = "";
        public string OwnerName { get; set; } = PrefabOwnerCatalog.UnknownOwnerName;
    }

    internal static List<PrefabOwnerSection<T>> BuildSections<T>(IEnumerable<T> entries, Func<T, string> getPrefabName)
    {
        PrefabOwnerResolver.OwnerSnapshot snapshot = PrefabOwnerResolver.GetSnapshot();
        return BuildSections(entries, getPrefabName, entry => snapshot.GetOwnerName(getPrefabName(entry)));
    }

    internal static List<PrefabOwnerSection<T>> BuildSections<T>(IEnumerable<T> entries, Func<T, string> getPrefabName, Func<T, string> getOwnerName)
    {
        return entries
            .Select(entry =>
            {
                string prefabName = (getPrefabName(entry) ?? "").Trim();
                string ownerName = (getOwnerName(entry) ?? "").Trim();
                return new GroupedEntry<T>
                {
                    Entry = entry,
                    PrefabName = prefabName,
                    OwnerName = ownerName.Length > 0 ? ownerName : PrefabOwnerCatalog.UnknownOwnerName
                };
            })
            .OrderBy(entry => GetOwnerSortBucket(entry.OwnerName))
            .ThenBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.PrefabName, StringComparer.OrdinalIgnoreCase)
            .GroupBy(entry => entry.OwnerName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PrefabOwnerSection<T>(
                group.First().OwnerName,
                group.Select(entry => entry.Entry).ToList()))
            .ToList();
    }

    internal static string SerializeReferenceSections<T>(IEnumerable<PrefabOwnerSection<T>> sections, ISerializer serializer)
    {
        StringBuilder builder = new();
        bool wroteSection = false;

        foreach (PrefabOwnerSection<T> section in sections)
        {
            if (section.Entries.Count == 0)
            {
                continue;
            }

            if (wroteSection)
            {
                builder.AppendLine();
            }

            AppendSectionHeaderComment(builder, section.OwnerName);
            foreach (T entry in section.Entries)
            {
                string serializedEntry = CollapseScalarBlockListsToInlineLists(serializer.Serialize(new[] { entry }).TrimEnd('\r', '\n'));
                builder.AppendLine(serializedEntry);
            }

            wroteSection = true;
        }

        return wroteSection ? builder.ToString() : "[]" + Environment.NewLine;
    }

    internal static void AppendSectionHeaderComment(StringBuilder builder, string ownerName)
    {
        builder.Append("# ===== ");
        builder.Append(string.IsNullOrWhiteSpace(ownerName) ? PrefabOwnerCatalog.UnknownOwnerName : ownerName.Trim());
        builder.AppendLine(" =====");
    }

    private static string CollapseScalarBlockListsToInlineLists(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml) || yaml.IndexOf("- ", StringComparison.Ordinal) < 0)
        {
            return yaml;
        }

        string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
        StringBuilder builder = new();

        for (int index = 0; index < lines.Length; index++)
        {
            if (TryCollapseScalarBlockList(lines, ref index, out string collapsedLine))
            {
                builder.AppendLine(collapsedLine);
                continue;
            }

            builder.AppendLine(lines[index]);
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private static bool TryCollapseScalarBlockList(string[] lines, ref int index, out string collapsedLine)
    {
        collapsedLine = "";
        string line = lines[index];
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        int firstNonWhitespace = GetFirstNonWhitespaceIndex(line);
        if (firstNonWhitespace < 0)
        {
            return false;
        }

        string trimmedLine = line[firstNonWhitespace..];
        if (trimmedLine.StartsWith("-", StringComparison.Ordinal))
        {
            return false;
        }

        int colonIndex = line.LastIndexOf(':');
        if (colonIndex < 0 || colonIndex != line.Length - 1)
        {
            return false;
        }

        string indent = line[..firstNonWhitespace];
        string itemPrefix = indent + "- ";
        List<string> items = new();
        int lookahead = index + 1;

        while (lookahead < lines.Length && lines[lookahead].StartsWith(itemPrefix, StringComparison.Ordinal))
        {
            string itemLine = lines[lookahead];
            string itemValue = itemLine[itemPrefix.Length..];
            if (!IsSimpleScalarYamlListItem(itemValue))
            {
                return false;
            }

            if (lookahead + 1 < lines.Length)
            {
                string nextLine = lines[lookahead + 1];
                int nextIndent = GetFirstNonWhitespaceIndex(nextLine);
                if (nextIndent > firstNonWhitespace && !nextLine.StartsWith(itemPrefix, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            items.Add(itemValue);
            lookahead++;
        }

        if (items.Count == 0)
        {
            return false;
        }

        collapsedLine = line + " [" + string.Join(", ", items) + "]";
        index = lookahead - 1;
        return true;
    }

    private static bool IsSimpleScalarYamlListItem(string itemValue)
    {
        if (string.IsNullOrWhiteSpace(itemValue))
        {
            return false;
        }

        string trimmedValue = itemValue.Trim();
        if (trimmedValue.EndsWith(":", StringComparison.Ordinal) || trimmedValue.Contains(": ", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static int GetFirstNonWhitespaceIndex(string line)
    {
        for (int index = 0; index < line.Length; index++)
        {
            if (!char.IsWhiteSpace(line[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static int GetOwnerSortBucket(string ownerName)
    {
        if (string.Equals(ownerName, PrefabOwnerCatalog.VanillaOwnerName, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(ownerName, PrefabOwnerCatalog.UnknownOwnerName, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 1;
    }
}

internal static class PrefabOwnerResolver
{
    internal sealed class OwnerSnapshot
    {
        private readonly Dictionary<string, string> _owners;

        internal OwnerSnapshot(Dictionary<string, string> owners, string signature)
        {
            _owners = owners ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Signature = signature ?? "";
        }

        internal string Signature { get; }

        internal string GetOwnerName(string? prefabName)
        {
            string normalizedPrefabName = (prefabName ?? "").Trim();
            if (normalizedPrefabName.Length == 0)
            {
                return PrefabOwnerCatalog.UnknownOwnerName;
            }

            foreach (string candidate in EnumerateLookupCandidates(normalizedPrefabName))
            {
                if (_owners.TryGetValue(candidate, out string ownerName) &&
                    !string.IsNullOrWhiteSpace(ownerName))
                {
                    return ownerName;
                }
            }

            foreach (string candidate in EnumerateLookupCandidates(normalizedPrefabName))
            {
                if (VanillaPrefabCatalog.IsAvailable && VanillaPrefabCatalog.IsVanilla(candidate))
                {
                    return PrefabOwnerCatalog.VanillaOwnerName;
                }
            }

            foreach (string candidate in EnumerateLookupCandidates(normalizedPrefabName))
            {
                if (PrefabOwnerCatalog.IsLikelyRuntimeVanillaPrefab(candidate))
                {
                    return PrefabOwnerCatalog.VanillaOwnerName;
                }
            }

            return PrefabOwnerCatalog.UnknownOwnerName;
        }

        private static IEnumerable<string> EnumerateLookupCandidates(string normalizedPrefabName)
        {
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            void AddIfNew(string candidate)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    seen.Add(candidate.Trim());
                }
            }

            AddIfNew(normalizedPrefabName);

            string withoutCloneSuffix = TrimCloneSuffix(normalizedPrefabName);
            AddIfNew(withoutCloneSuffix);

            int aliasSeparatorIndex = withoutCloneSuffix.IndexOf(':');
            if (aliasSeparatorIndex > 0)
            {
                AddIfNew(withoutCloneSuffix[..aliasSeparatorIndex]);
            }

            foreach (string candidate in seen)
            {
                yield return candidate;
            }
        }

        private static string TrimCloneSuffix(string prefabName)
        {
            const string cloneSuffix = "(Clone)";
            return prefabName.EndsWith(cloneSuffix, StringComparison.Ordinal)
                ? prefabName[..^cloneSuffix.Length].TrimEnd()
                : prefabName;
        }
    }

    private static readonly object Sync = new();
    private static string _snapshotSignature = "";
    private static OwnerSnapshot _snapshot = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "");

    internal static void Invalidate()
    {
        lock (Sync)
        {
            _snapshotSignature = "";
            _snapshot = new OwnerSnapshot(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "");
        }

        PrefabProvenanceRegistry.Invalidate();
    }

    internal static string GetOwnerName(string? prefabName)
    {
        return GetSnapshot().GetOwnerName(prefabName);
    }

    internal static OwnerSnapshot GetSnapshot()
    {
        PrefabProvenanceRegistry.MappingSnapshot provenanceSnapshot = PrefabProvenanceRegistry.GetSnapshot();
        PrefabOwnerCatalog.MappingSnapshot bundleSnapshot = PrefabOwnerCatalog.GetSnapshot();
        string combinedSignature = $"provenance:{provenanceSnapshot.Signature}|bundles:{bundleSnapshot.Signature}";

        if (string.Equals(combinedSignature, _snapshotSignature, StringComparison.Ordinal))
        {
            return _snapshot;
        }

        lock (Sync)
        {
            if (string.Equals(combinedSignature, _snapshotSignature, StringComparison.Ordinal))
            {
                return _snapshot;
            }

            Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string prefabName, string ownerName) in bundleSnapshot.Owners)
            {
                if (!string.IsNullOrWhiteSpace(prefabName) && !string.IsNullOrWhiteSpace(ownerName))
                {
                    owners[prefabName] = ownerName;
                }
            }

            foreach ((string prefabName, string ownerName) in provenanceSnapshot.Owners)
            {
                if (!string.IsNullOrWhiteSpace(prefabName) && !string.IsNullOrWhiteSpace(ownerName))
                {
                    owners[prefabName] = ownerName;
                }
            }

            _snapshot = new OwnerSnapshot(owners, combinedSignature);
            _snapshotSignature = combinedSignature;
            return _snapshot;
        }
    }
}

internal static class PrefabOwnerCatalog
{
    internal const string VanillaOwnerName = "Valheim";
    internal const string UnknownOwnerName = "Unknown / Untracked";

    internal sealed class MappingSnapshot
    {
        internal MappingSnapshot(Dictionary<string, string> owners, string signature)
        {
            Owners = owners ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Signature = signature ?? "";
        }

        internal Dictionary<string, string> Owners { get; }
        internal string Signature { get; }
    }

    private sealed class PluginResources
    {
        public string OwnerName { get; set; } = "";
        public string[] ResourceNames { get; set; } = Array.Empty<string>();
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> PrefabOwners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> BundleOwners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> PrefabsByBundle = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> KnownBundleNames = new(StringComparer.OrdinalIgnoreCase);
    private static string _loadedBundleSignature = "";
    private static string _loadedPluginResourcesSignature = "";
    private static MappingSnapshot _snapshot = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "");
    private static string _pluginResourcesSignature = "";
    private static List<PluginResources> _pluginResourcesCache = new();
    private static bool _mappingsDirty = true;

    internal static string GetOwnerName(string? prefabName)
    {
        string normalizedPrefabName = (prefabName ?? "").Trim();
        if (normalizedPrefabName.Length == 0)
        {
            return UnknownOwnerName;
        }

        foreach (string candidate in EnumerateLookupCandidates(normalizedPrefabName))
        {
            if (VanillaPrefabCatalog.IsAvailable && VanillaPrefabCatalog.IsVanilla(candidate))
            {
                return VanillaOwnerName;
            }
        }

        return PrefabOwnerResolver.GetOwnerName(normalizedPrefabName);
    }

    internal static string GetCurrentBundleSignature()
    {
        return BuildBundleSignature();
    }

    internal static string GetCurrentPluginResourcesSignature()
    {
        return BuildPluginResourcesSignature();
    }

    internal static MappingSnapshot GetSnapshot()
    {
        EnsureMappingsLoaded();
        return _snapshot;
    }

    internal static void Invalidate()
    {
        lock (Sync)
        {
            _mappingsDirty = true;
        }
    }

    private static void EnsureMappingsLoaded()
    {
        string bundleSignature = BuildBundleSignature();
        string pluginResourcesSignature = BuildPluginResourcesSignature();
        if (!_mappingsDirty &&
            string.Equals(bundleSignature, _loadedBundleSignature, StringComparison.Ordinal) &&
            string.Equals(pluginResourcesSignature, _loadedPluginResourcesSignature, StringComparison.Ordinal))
        {
            return;
        }

        lock (Sync)
        {
            if (!_mappingsDirty &&
                string.Equals(bundleSignature, _loadedBundleSignature, StringComparison.Ordinal) &&
                string.Equals(pluginResourcesSignature, _loadedPluginResourcesSignature, StringComparison.Ordinal))
            {
                return;
            }

            if (TryLoadMappingsFromCache(bundleSignature, pluginResourcesSignature))
            {
                _mappingsDirty = false;
                return;
            }

            Dictionary<string, AssetBundle> loadedBundles = GetLoadedBundlesByName();
            HashSet<string> currentBundleNames = new(loadedBundles.Keys, StringComparer.OrdinalIgnoreCase);
            if (TryIncrementallyExtendMappings(loadedBundles, currentBundleNames, bundleSignature, pluginResourcesSignature))
            {
                _mappingsDirty = false;
                return;
            }

            RebuildMappings(loadedBundles, bundleSignature, pluginResourcesSignature);
            _mappingsDirty = false;
        }
    }

    private static void RebuildMappings(Dictionary<string, AssetBundle> loadedBundles, string bundleSignature, string pluginResourcesSignature)
    {
        PrefabOwners.Clear();
        PrefabsByBundle.Clear();
        KnownBundleNames.Clear();

        Dictionary<string, string> prefabToBundle = BuildPrefabToBundleMapping(loadedBundles.Values);
        Dictionary<string, string> bundleToOwner = BuildBundleToOwnerMapping(prefabToBundle.Values.Distinct(StringComparer.OrdinalIgnoreCase));

        foreach ((string prefabName, string bundleName) in prefabToBundle)
        {
            if (bundleToOwner.TryGetValue(bundleName, out string ownerName))
            {
                PrefabOwners[prefabName] = ownerName;
            }

            if (!PrefabsByBundle.TryGetValue(bundleName, out HashSet<string>? prefabs))
            {
                prefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                PrefabsByBundle[bundleName] = prefabs;
            }

            prefabs.Add(prefabName);
        }

        foreach (string bundleName in loadedBundles.Keys)
        {
            KnownBundleNames.Add(bundleName);
        }

        _loadedBundleSignature = bundleSignature;
        _loadedPluginResourcesSignature = pluginResourcesSignature;
        RefreshSnapshot();
        SaveMappingsToCache();
        DropNSpawnPlugin.DropNSpawnLogger.LogDebug($"Tracked {PrefabOwners.Count} prefab owner mapping(s) across {bundleToOwner.Count} mod asset bundle(s).");
    }

    private static Dictionary<string, AssetBundle> GetLoadedBundlesByName()
    {
        Dictionary<string, AssetBundle> loadedBundles = new(StringComparer.OrdinalIgnoreCase);
        foreach (AssetBundle assetBundle in AssetBundle.GetAllLoadedAssetBundles())
        {
            string bundleName = assetBundle.name ?? "";
            if (bundleName.Length == 0)
            {
                continue;
            }

            loadedBundles[bundleName] = assetBundle;
        }

        return loadedBundles;
    }

    private static Dictionary<string, string> BuildPrefabToBundleMapping(IEnumerable<AssetBundle> bundles)
    {
        Dictionary<string, string> prefabToBundle = new(StringComparer.OrdinalIgnoreCase);

        foreach (AssetBundle assetBundle in bundles)
        {
            string bundleName = assetBundle.name ?? "";
            if (bundleName.Length == 0)
            {
                continue;
            }

            foreach (string assetName in assetBundle.GetAllAssetNames())
            {
                if (!assetName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prefabName = Path.GetFileNameWithoutExtension(assetName);
                if (!string.IsNullOrWhiteSpace(prefabName))
                {
                    prefabToBundle[prefabName] = bundleName;
                }
            }
        }

        return prefabToBundle;
    }

    private static bool TryIncrementallyExtendMappings(
        Dictionary<string, AssetBundle> loadedBundles,
        HashSet<string> currentBundleNames,
        string bundleSignature,
        string pluginResourcesSignature)
    {
        if (!string.Equals(_loadedPluginResourcesSignature, pluginResourcesSignature, StringComparison.Ordinal) ||
            KnownBundleNames.Count == 0 ||
            currentBundleNames.Count < KnownBundleNames.Count ||
            KnownBundleNames.Except(currentBundleNames, StringComparer.OrdinalIgnoreCase).Any())
        {
            return false;
        }

        List<string> newBundleNames = currentBundleNames
            .Except(KnownBundleNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (newBundleNames.Count == 0)
        {
            _loadedBundleSignature = bundleSignature;
            _loadedPluginResourcesSignature = pluginResourcesSignature;
            RefreshSnapshot();
            return true;
        }

        Dictionary<string, string> bundleToOwner = BuildBundleToOwnerMapping(newBundleNames);
        foreach (string bundleName in newBundleNames)
        {
            if (!loadedBundles.TryGetValue(bundleName, out AssetBundle? assetBundle))
            {
                continue;
            }

            KnownBundleNames.Add(bundleName);
            if (!PrefabsByBundle.TryGetValue(bundleName, out HashSet<string>? prefabs))
            {
                prefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                PrefabsByBundle[bundleName] = prefabs;
            }

            string ownerName = bundleToOwner.TryGetValue(bundleName, out string resolvedOwnerName)
                ? resolvedOwnerName
                : "";
            foreach (string assetName in assetBundle.GetAllAssetNames())
            {
                if (!assetName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prefabName = Path.GetFileNameWithoutExtension(assetName);
                if (string.IsNullOrWhiteSpace(prefabName))
                {
                    continue;
                }

                prefabs.Add(prefabName);
                if (!string.IsNullOrWhiteSpace(ownerName))
                {
                    PrefabOwners[prefabName] = ownerName;
                }
            }
        }

        _loadedBundleSignature = bundleSignature;
        _loadedPluginResourcesSignature = pluginResourcesSignature;
        RefreshSnapshot();
        SaveMappingsToCache();
        return true;
    }

    private static Dictionary<string, string> BuildBundleToOwnerMapping(IEnumerable<string> bundleNames)
    {
        Dictionary<string, string> bundleToOwner = new(StringComparer.OrdinalIgnoreCase);
        List<PluginResources> plugins = GetPluginResources();

        foreach (string bundleName in bundleNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (bundleName.Length == 0)
            {
                continue;
            }

            if (BundleOwners.TryGetValue(bundleName, out string? cachedOwnerName))
            {
                if (!string.IsNullOrWhiteSpace(cachedOwnerName))
                {
                    bundleToOwner[bundleName] = cachedOwnerName;
                }

                continue;
            }

            string? ownerName = ResolveOwnerName(bundleName, plugins);
            BundleOwners[bundleName] = ownerName ?? "";
            if (!string.IsNullOrWhiteSpace(ownerName))
            {
                bundleToOwner[bundleName] = ownerName!;
            }
        }

        return bundleToOwner;
    }

    private static List<PluginResources> GetPluginResources()
    {
        string pluginResourcesSignature = BuildPluginResourcesSignature();
        if (string.Equals(pluginResourcesSignature, _pluginResourcesSignature, StringComparison.Ordinal))
        {
            return _pluginResourcesCache;
        }

        List<PluginResources> plugins = Chainloader.PluginInfos.Values
            .Select(pluginInfo => new PluginResources
            {
                OwnerName = GetPluginDisplayName(pluginInfo),
                ResourceNames = GetManifestResourceNames(pluginInfo)
            })
            .Where(plugin => plugin.OwnerName.Length > 0)
            .ToList();

        _pluginResourcesSignature = pluginResourcesSignature;
        _pluginResourcesCache = plugins;
        BundleOwners.Clear();
        return _pluginResourcesCache;
    }

    private static void RefreshSnapshot()
    {
        _snapshot = new MappingSnapshot(
            new Dictionary<string, string>(PrefabOwners, StringComparer.OrdinalIgnoreCase),
            ReferenceRefreshSupport.ComputeStableHashForKeys(new[]
            {
                $"bundles:{_loadedBundleSignature}",
                $"plugins:{_loadedPluginResourcesSignature}"
            }));
    }

    private static string GetCachePath()
    {
        string cacheDirectoryPath = Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, "cache");
        Directory.CreateDirectory(cacheDirectoryPath);
        return Path.Combine(cacheDirectoryPath, ".prefab-owner-bundle-cache.txt");
    }

    private static bool TryLoadMappingsFromCache(string bundleSignature, string pluginResourcesSignature)
    {
        string cachePath = GetCachePath();
        if (!File.Exists(cachePath))
        {
            return false;
        }

        try
        {
            string[] lines = File.ReadAllLines(cachePath);
            if (lines.Length < 4 || !string.Equals(lines[0], "v1", StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(lines[1], bundleSignature, StringComparison.Ordinal) ||
                !string.Equals(lines[2], pluginResourcesSignature, StringComparison.Ordinal))
            {
                return false;
            }

            PrefabOwners.Clear();
            PrefabsByBundle.Clear();
            KnownBundleNames.Clear();
            BundleOwners.Clear();

            for (int index = 4; index < lines.Length; index++)
            {
                string line = lines[index];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string[] parts = line.Split('\t');
                if (parts.Length < 3)
                {
                    continue;
                }

                string prefabName = DecodeCacheField(parts[0]);
                string ownerName = DecodeCacheField(parts[1]);
                string mappedBundleName = DecodeCacheField(parts[2]);
                if (string.IsNullOrWhiteSpace(prefabName) || string.IsNullOrWhiteSpace(mappedBundleName))
                {
                    continue;
                }

                PrefabOwners[prefabName] = ownerName;
                KnownBundleNames.Add(mappedBundleName);
                if (!PrefabsByBundle.TryGetValue(mappedBundleName, out HashSet<string>? prefabs))
                {
                    prefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    PrefabsByBundle[mappedBundleName] = prefabs;
                }

                prefabs.Add(prefabName);
                if (!string.IsNullOrWhiteSpace(ownerName))
                {
                    BundleOwners[mappedBundleName] = ownerName;
                }
            }

            _loadedBundleSignature = bundleSignature;
            _loadedPluginResourcesSignature = pluginResourcesSignature;
            _snapshot = new MappingSnapshot(
                new Dictionary<string, string>(PrefabOwners, StringComparer.OrdinalIgnoreCase),
                lines[3] ?? "");
            return _snapshot.Signature.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveMappingsToCache()
    {
        if (string.IsNullOrWhiteSpace(_loadedBundleSignature) ||
            string.IsNullOrWhiteSpace(_loadedPluginResourcesSignature) ||
            string.IsNullOrWhiteSpace(_snapshot.Signature))
        {
            return;
        }

        StringBuilder builder = new();
        builder.AppendLine("v1");
        builder.AppendLine(_loadedBundleSignature);
        builder.AppendLine(_loadedPluginResourcesSignature);
        builder.AppendLine(_snapshot.Signature);
        foreach ((string bundleName, HashSet<string> prefabs) in PrefabsByBundle
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (string prefabName in prefabs.OrderBy(prefabName => prefabName, StringComparer.OrdinalIgnoreCase))
            {
                PrefabOwners.TryGetValue(prefabName, out string? ownerName);
                builder.Append(EncodeCacheField(prefabName))
                    .Append('\t')
                    .Append(EncodeCacheField(ownerName ?? ""))
                    .Append('\t')
                    .Append(EncodeCacheField(bundleName))
                    .AppendLine();
            }
        }

        GeneratedFileWriter.WriteAllTextIfChanged(GetCachePath(), builder.ToString());
    }

    private static string EncodeCacheField(string? value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
    }

    private static string DecodeCacheField(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            return "";
        }
    }

    private static string? ResolveOwnerName(string bundleName, List<PluginResources> plugins)
    {
        PluginResources? plugin = plugins.FirstOrDefault(candidate =>
            candidate.ResourceNames.Any(resourceName =>
                resourceName.EndsWith(bundleName, StringComparison.OrdinalIgnoreCase)));

        return plugin?.OwnerName;
    }

    private static string GetPluginDisplayName(PluginInfo pluginInfo)
    {
        string pluginName = pluginInfo.Metadata.Name?.Trim() ?? "";
        if (pluginName.Length > 0)
        {
            return pluginName;
        }

        string pluginGuid = pluginInfo.Metadata.GUID?.Trim() ?? "";
        return pluginGuid.Length > 0 ? pluginGuid : UnknownOwnerName;
    }

    private static string[] GetManifestResourceNames(PluginInfo pluginInfo)
    {
        try
        {
            return pluginInfo.Instance?.GetType().Assembly.GetManifestResourceNames() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string BuildPluginResourcesSignature()
    {
        return string.Join(
            "|",
            Chainloader.PluginInfos.Values
                .Select(pluginInfo =>
                {
                    string guid = pluginInfo.Metadata.GUID ?? "";
                    string name = pluginInfo.Metadata.Name ?? "";
                    string assemblyName = pluginInfo.Instance?.GetType().Assembly.FullName ?? "";
                    return $"{guid}:{name}:{assemblyName}";
                })
                .OrderBy(signature => signature, StringComparer.OrdinalIgnoreCase));
    }

    private static string BuildBundleSignature()
    {
        return string.Join(
            "|",
            AssetBundle.GetAllLoadedAssetBundles()
                .Select(assetBundle => assetBundle.name ?? "")
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateLookupCandidates(string normalizedPrefabName)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        void YieldIfNew(string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                seen.Add(candidate.Trim());
            }
        }

        YieldIfNew(normalizedPrefabName);

        string withoutCloneSuffix = TrimCloneSuffix(normalizedPrefabName);
        YieldIfNew(withoutCloneSuffix);

        int aliasSeparatorIndex = withoutCloneSuffix.IndexOf(':');
        if (aliasSeparatorIndex > 0)
        {
            YieldIfNew(withoutCloneSuffix.Substring(0, aliasSeparatorIndex));
        }

        foreach (string candidate in seen)
        {
            yield return candidate;
        }
    }

    private static string TrimCloneSuffix(string prefabName)
    {
        const string cloneSuffix = "(Clone)";
        return prefabName.EndsWith(cloneSuffix, StringComparison.Ordinal)
            ? prefabName[..^cloneSuffix.Length].TrimEnd()
            : prefabName;
    }

    // EWD-style final fallback: if a prefab is present in current runtime registries but
    // no mod provenance or bundle owner was resolved, prefer grouping it under Valheim.
    internal static bool IsLikelyRuntimeVanillaPrefab(string prefabName)
    {
        if (string.IsNullOrWhiteSpace(prefabName))
        {
            return false;
        }

        if (ObjectDB.instance?.GetItemPrefab(prefabName) != null)
        {
            return true;
        }

        if (ZNetScene.instance?.GetPrefab(prefabName) != null)
        {
            return true;
        }

        if (ZoneSystem.instance == null)
        {
            return false;
        }

        foreach (ZoneSystem.ZoneLocation? location in ZoneSystem.instance.m_locations)
        {
            string locationPrefabName = (location?.m_prefabName ?? location?.m_prefab.Name ?? "").Trim();
            if (string.Equals(locationPrefabName, prefabName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
