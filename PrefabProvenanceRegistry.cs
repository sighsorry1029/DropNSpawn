using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

internal static class PrefabProvenanceRegistry
{
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

    private enum ProviderKind
    {
        JotunnZoneManager = 0,
        LocationManagerTemplate = 1,
        CreatureManagerTemplate = 2,
        JotunnCreatureManager = 3,
        JotunnPrefabManager = 4
    }

    private sealed class ProviderHandle
    {
        public ProviderKind Kind { get; set; }
        public Assembly Assembly { get; set; } = null!;
        public string AssemblyName { get; set; } = "";
        public string OwnerName { get; set; } = "";
        public Type? ManagerType { get; set; }
        public PropertyInfo? ManagerInstanceProperty { get; set; }
        public FieldInfo? ManagerCreaturesField { get; set; }
        public PropertyInfo? ManagerCreaturesProperty { get; set; }
        public FieldInfo? RegisteredCreaturesField { get; set; }
        public FieldInfo? RegisteredLocationsField { get; set; }
        public PropertyInfo? PrefabsProperty { get; set; }
        public FieldInfo? PrefabsField { get; set; }
        public PropertyInfo? LocationsProperty { get; set; }
        public FieldInfo? LocationsField { get; set; }
    }

    private readonly struct OwnerMapping
    {
        internal OwnerMapping(string ownerName, int priority)
        {
            OwnerName = ownerName;
            Priority = priority;
        }

        internal string OwnerName { get; }
        internal int Priority { get; }
    }

    private sealed class InternalMappingSnapshot
    {
        public Dictionary<string, OwnerMapping> Owners { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string Signature { get; set; } = "";
    }

    private static readonly object Sync = new();
    private static string _providerAssemblySignature = "";
    private static List<ProviderHandle> _providerCache = new();
    private static string _mappingSignature = "";
    private static Dictionary<string, OwnerMapping> _owners = new(StringComparer.OrdinalIgnoreCase);
    private static MappingSnapshot _snapshot = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "");
    private static bool _mappingsDirty = true;
    private static bool _allowCacheLoad = true;

    internal static bool TryGetOwnerName(string? prefabName, out string ownerName)
    {
        ownerName = "";
        string normalizedPrefabName = (prefabName ?? "").Trim();
        if (normalizedPrefabName.Length == 0)
        {
            return false;
        }

        EnsureMappingsLoaded();
        foreach (string candidate in EnumerateLookupCandidates(normalizedPrefabName))
        {
            if (_owners.TryGetValue(candidate, out OwnerMapping ownerMapping) &&
                !string.IsNullOrWhiteSpace(ownerMapping.OwnerName))
            {
                ownerName = ownerMapping.OwnerName;
                return true;
            }
        }

        return false;
    }

    internal static string GetOwnerNameOrFallback(string? prefabName)
    {
        return PrefabOwnerResolver.GetOwnerName(prefabName);
    }

    internal static void Invalidate()
    {
        lock (Sync)
        {
            _mappingSignature = "";
            _owners = new Dictionary<string, OwnerMapping>(StringComparer.OrdinalIgnoreCase);
            _snapshot = new MappingSnapshot(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "");
            _mappingsDirty = true;
            _allowCacheLoad = false;
        }
    }

    internal static MappingSnapshot GetSnapshot()
    {
        EnsureMappingsLoaded();
        return _snapshot;
    }

    private static void EnsureMappingsLoaded()
    {
        if (!_mappingsDirty && _snapshot.Signature.Length > 0)
        {
            return;
        }

        lock (Sync)
        {
            if (!_mappingsDirty && _snapshot.Signature.Length > 0)
            {
                return;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .ToArray();
            string assemblySignature = ReferenceRefreshSupport.ComputeStableHashForKeys(
                assemblies.Select(assembly => assembly.FullName ?? assembly.GetName().Name ?? ""));

            if (_allowCacheLoad &&
                TryLoadMappingsSnapshotFromCache(assemblySignature, out InternalMappingSnapshot cachedSnapshot))
            {
                _providerAssemblySignature = assemblySignature;
                _owners = cachedSnapshot.Owners;
                _mappingSignature = cachedSnapshot.Signature;
                _snapshot = new MappingSnapshot(
                    _owners.ToDictionary(pair => pair.Key, pair => pair.Value.OwnerName, StringComparer.OrdinalIgnoreCase),
                    _mappingSignature);
                _mappingsDirty = false;
                return;
            }

            List<ProviderHandle> providers = GetProviders(assemblies, assemblySignature);
            InternalMappingSnapshot snapshot = BuildMappingsSnapshot(providers);

            if (!string.Equals(snapshot.Signature, _mappingSignature, StringComparison.Ordinal))
            {
                _owners = snapshot.Owners;
                _mappingSignature = snapshot.Signature;
                _snapshot = new MappingSnapshot(
                    _owners.ToDictionary(pair => pair.Key, pair => pair.Value.OwnerName, StringComparer.OrdinalIgnoreCase),
                    _mappingSignature);
            }

            SaveMappingsSnapshotToCache(assemblySignature, snapshot);
            _mappingsDirty = false;
            _allowCacheLoad = true;
        }
    }

    private static string GetCachePath()
    {
        string cacheDirectoryPath = Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, "cache");
        Directory.CreateDirectory(cacheDirectoryPath);
        return Path.Combine(cacheDirectoryPath, ".prefab-owner-provenance-cache.txt");
    }

    private static bool TryLoadMappingsSnapshotFromCache(string assemblySignature, out InternalMappingSnapshot snapshot)
    {
        snapshot = new InternalMappingSnapshot();
        string cachePath = GetCachePath();
        if (!File.Exists(cachePath))
        {
            return false;
        }

        try
        {
            string[] lines = File.ReadAllLines(cachePath);
            if (lines.Length < 3 || !string.Equals(lines[0], "v1", StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(lines[1], assemblySignature, StringComparison.Ordinal))
            {
                return false;
            }

            Dictionary<string, OwnerMapping> owners = new(StringComparer.OrdinalIgnoreCase);
            for (int index = 3; index < lines.Length; index++)
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
                if (!int.TryParse(parts[2], out int priority) ||
                    string.IsNullOrWhiteSpace(prefabName) ||
                    string.IsNullOrWhiteSpace(ownerName))
                {
                    continue;
                }

                owners[prefabName] = new OwnerMapping(ownerName, priority);
            }

            snapshot = new InternalMappingSnapshot
            {
                Owners = owners,
                Signature = lines[2] ?? ""
            };
            return snapshot.Signature.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SaveMappingsSnapshotToCache(string assemblySignature, InternalMappingSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(assemblySignature) || string.IsNullOrWhiteSpace(snapshot.Signature))
        {
            return;
        }

        StringBuilder builder = new();
        builder.AppendLine("v1");
        builder.AppendLine(assemblySignature);
        builder.AppendLine(snapshot.Signature);
        foreach ((string prefabName, OwnerMapping ownerMapping) in snapshot.Owners
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(EncodeCacheField(prefabName))
                .Append('\t')
                .Append(EncodeCacheField(ownerMapping.OwnerName))
                .Append('\t')
                .Append(ownerMapping.Priority)
                .AppendLine();
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

    private static List<ProviderHandle> GetProviders(Assembly[] assemblies, string assemblySignature)
    {
        if (string.Equals(_providerAssemblySignature, assemblySignature, StringComparison.Ordinal))
        {
            return _providerCache;
        }

        List<ProviderHandle> providers = new();
        ProviderHandle? jotunnZoneProvider = TryCreateJotunnZoneProvider(assemblies);
        if (jotunnZoneProvider != null)
        {
            providers.Add(jotunnZoneProvider);
        }

        ProviderHandle? jotunnPrefabProvider = TryCreateJotunnPrefabProvider(assemblies);
        if (jotunnPrefabProvider != null)
        {
            providers.Add(jotunnPrefabProvider);
        }

        ProviderHandle? jotunnCreatureProvider = TryCreateJotunnCreatureProvider(assemblies);
        if (jotunnCreatureProvider != null)
        {
            providers.Add(jotunnCreatureProvider);
        }

        providers.AddRange(CreateCreatureManagerTemplateProviders(assemblies));
        providers.AddRange(CreateLocationManagerTemplateProviders(assemblies));
        providers = providers
            .OrderBy(provider => provider.Kind)
            .ThenBy(provider => provider.OwnerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(provider => provider.AssemblyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _providerAssemblySignature = assemblySignature;
        _providerCache = providers;
        return _providerCache;
    }

    private static ProviderHandle? TryCreateJotunnZoneProvider(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type? managerType = SafeGetType(assembly, "Jotunn.Managers.ZoneManager");
            if (managerType == null)
            {
                continue;
            }

            PropertyInfo? instanceProperty = TryGetTypeProperty(managerType, "Instance");
            PropertyInfo? locationsProperty = TryGetTypeProperty(managerType, "Locations");
            FieldInfo? locationsField = TryGetTypeField(managerType, "Locations");
            if (instanceProperty == null || (locationsProperty == null && locationsField == null))
            {
                continue;
            }

            return new ProviderHandle
            {
                Kind = ProviderKind.JotunnZoneManager,
                Assembly = assembly,
                AssemblyName = assembly.GetName().Name ?? assembly.FullName ?? "Jotunn",
                OwnerName = ResolveAssemblyOwnerName(assembly),
                ManagerType = managerType,
                ManagerInstanceProperty = instanceProperty,
                LocationsProperty = locationsProperty,
                LocationsField = locationsField
            };
        }

        return null;
    }

    private static ProviderHandle? TryCreateJotunnPrefabProvider(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type? managerType = SafeGetType(assembly, "Jotunn.Managers.PrefabManager");
            if (managerType == null)
            {
                continue;
            }

            PropertyInfo? instanceProperty = TryGetTypeProperty(managerType, "Instance");
            PropertyInfo? prefabsProperty = TryGetTypeProperty(managerType, "Prefabs");
            FieldInfo? prefabsField = TryGetTypeField(managerType, "Prefabs");
            if (instanceProperty == null || (prefabsProperty == null && prefabsField == null))
            {
                continue;
            }

            return new ProviderHandle
            {
                Kind = ProviderKind.JotunnPrefabManager,
                Assembly = assembly,
                AssemblyName = assembly.GetName().Name ?? assembly.FullName ?? "Jotunn",
                OwnerName = ResolveAssemblyOwnerName(assembly),
                ManagerType = managerType,
                ManagerInstanceProperty = instanceProperty,
                PrefabsProperty = prefabsProperty,
                PrefabsField = prefabsField
            };
        }

        return null;
    }

    private static ProviderHandle? TryCreateJotunnCreatureProvider(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type? managerType = SafeGetType(assembly, "Jotunn.Managers.CreatureManager");
            if (managerType == null)
            {
                continue;
            }

            PropertyInfo? instanceProperty = TryGetTypeProperty(managerType, "Instance");
            FieldInfo? creaturesField = TryGetTypeField(managerType, "Creatures");
            PropertyInfo? creaturesProperty = TryGetTypeProperty(managerType, "Creatures");
            if (instanceProperty == null || (creaturesField == null && creaturesProperty == null))
            {
                continue;
            }

            return new ProviderHandle
            {
                Kind = ProviderKind.JotunnCreatureManager,
                Assembly = assembly,
                AssemblyName = assembly.GetName().Name ?? assembly.FullName ?? "Jotunn",
                OwnerName = ResolveAssemblyOwnerName(assembly),
                ManagerType = managerType,
                ManagerInstanceProperty = instanceProperty,
                ManagerCreaturesField = creaturesField,
                ManagerCreaturesProperty = creaturesProperty
            };
        }

        return null;
    }

    private static IEnumerable<ProviderHandle> CreateCreatureManagerTemplateProviders(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type? creatureType = SafeGetType(assembly, "CreatureManager.Creature");
            if (creatureType == null)
            {
                continue;
            }

            FieldInfo? registeredCreaturesField = TryGetTypeField(creatureType, "registeredCreatures");
            if (registeredCreaturesField == null)
            {
                continue;
            }

            yield return new ProviderHandle
            {
                Kind = ProviderKind.CreatureManagerTemplate,
                Assembly = assembly,
                AssemblyName = assembly.GetName().Name ?? assembly.FullName ?? PrefabOwnerCatalog.UnknownOwnerName,
                OwnerName = ResolveAssemblyOwnerName(assembly),
                RegisteredCreaturesField = registeredCreaturesField
            };
        }
    }

    private static IEnumerable<ProviderHandle> CreateLocationManagerTemplateProviders(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type? locationType = SafeGetType(assembly, "LocationManager.Location");
            if (locationType == null)
            {
                continue;
            }

            FieldInfo? registeredLocationsField = TryGetTypeField(locationType, "registeredLocations");
            if (registeredLocationsField == null)
            {
                continue;
            }

            yield return new ProviderHandle
            {
                Kind = ProviderKind.LocationManagerTemplate,
                Assembly = assembly,
                AssemblyName = assembly.GetName().Name ?? assembly.FullName ?? PrefabOwnerCatalog.UnknownOwnerName,
                OwnerName = ResolveAssemblyOwnerName(assembly),
                RegisteredLocationsField = registeredLocationsField
            };
        }
    }

    private static InternalMappingSnapshot BuildMappingsSnapshot(IEnumerable<ProviderHandle> providers)
    {
        Dictionary<string, OwnerMapping> owners = new(StringComparer.OrdinalIgnoreCase);
        List<string> tokens = new();

        foreach (ProviderHandle provider in providers)
        {
            switch (provider.Kind)
            {
                case ProviderKind.JotunnZoneManager:
                    CollectJotunnLocationMappings(provider, owners, tokens);
                    break;
                case ProviderKind.LocationManagerTemplate:
                    CollectLocationManagerTemplateMappings(provider, owners, tokens);
                    break;
                case ProviderKind.JotunnPrefabManager:
                    CollectJotunnPrefabMappings(provider, owners, tokens);
                    break;
                case ProviderKind.JotunnCreatureManager:
                    CollectJotunnCreatureMappings(provider, owners, tokens);
                    break;
                case ProviderKind.CreatureManagerTemplate:
                    CollectCreatureManagerTemplateMappings(provider, owners, tokens);
                    break;
            }
        }

        return new InternalMappingSnapshot
        {
            Owners = owners,
            Signature = ReferenceRefreshSupport.ComputeStableHashForKeys(tokens)
        };
    }

    private static void CollectJotunnLocationMappings(
        ProviderHandle provider,
        Dictionary<string, OwnerMapping> owners,
        List<string> tokens)
    {
        object? managerInstance = provider.ManagerInstanceProperty?.GetValue(null, null);
        if (managerInstance == null)
        {
            return;
        }

        object? locationsValue = provider.LocationsField?.GetValue(managerInstance) ??
                                 provider.LocationsProperty?.GetValue(managerInstance, null);
        IEnumerable locationEntries = locationsValue is IDictionary dictionary
            ? dictionary.Values
            : GetEnumerable(locationsValue);

        foreach (object customLocation in locationEntries
                     .Cast<object?>()
                     .Where(entry => entry != null)
                     .Select(entry => entry!)
                     .OrderBy(entry => GetPrefabNameFromHolder(entry) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            string? prefabName = GetPrefabNameFromHolder(customLocation);
            string? ownerName = ResolveOwnerNameFromSourceModHolder(customLocation, provider.OwnerName);
            if (string.IsNullOrWhiteSpace(prefabName) || string.IsNullOrWhiteSpace(ownerName))
            {
                continue;
            }

            TryAddOwnerMapping(owners, prefabName!, ownerName!, priority: 0);
            tokens.Add($"jl:{prefabName}:{ownerName}");
        }
    }

    private static void CollectJotunnPrefabMappings(
        ProviderHandle provider,
        Dictionary<string, OwnerMapping> owners,
        List<string> tokens)
    {
        object? managerInstance = provider.ManagerInstanceProperty?.GetValue(null, null);
        if (managerInstance == null)
        {
            return;
        }

        object? prefabsValue = provider.PrefabsField?.GetValue(managerInstance) ??
                               provider.PrefabsProperty?.GetValue(managerInstance, null);
        IEnumerable prefabEntries = prefabsValue is IDictionary dictionary
            ? dictionary.Values
            : GetEnumerable(prefabsValue);

        foreach (object customPrefab in prefabEntries
                     .Cast<object?>()
                     .Where(entry => entry != null)
                     .Select(entry => entry!)
                     .OrderBy(entry => GetPrefabNameFromHolder(entry) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            string? prefabName = GetPrefabNameFromHolder(customPrefab);
            string? ownerName = ResolveOwnerNameFromSourceModHolder(customPrefab, provider.OwnerName);
            if (string.IsNullOrWhiteSpace(prefabName) || string.IsNullOrWhiteSpace(ownerName))
            {
                continue;
            }

            TryAddOwnerMapping(owners, prefabName!, ownerName!, priority: 2);
            tokens.Add($"jp:{prefabName}:{ownerName}");
        }
    }

    private static void CollectJotunnCreatureMappings(
        ProviderHandle provider,
        Dictionary<string, OwnerMapping> owners,
        List<string> tokens)
    {
        object? managerInstance = provider.ManagerInstanceProperty?.GetValue(null, null);
        if (managerInstance == null)
        {
            return;
        }

        object? creaturesValue = provider.ManagerCreaturesField?.GetValue(managerInstance) ??
                                 provider.ManagerCreaturesProperty?.GetValue(managerInstance, null);
        foreach (object creature in GetEnumerable(creaturesValue)
                     .Cast<object?>()
                     .Where(entry => entry != null)
                     .Select(entry => entry!)
                     .OrderBy(entry => GetPrefabNameFromHolder(entry) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            string? prefabName = GetPrefabNameFromHolder(creature);
            string? ownerName = ResolveOwnerNameFromSourceModHolder(creature, provider.OwnerName);
            if (string.IsNullOrWhiteSpace(prefabName) || string.IsNullOrWhiteSpace(ownerName))
            {
                continue;
            }

            TryAddOwnerMapping(owners, prefabName!, ownerName!, priority: 0);
            tokens.Add($"jc:{prefabName}:{ownerName}");
        }
    }

    private static void CollectCreatureManagerTemplateMappings(
        ProviderHandle provider,
        Dictionary<string, OwnerMapping> owners,
        List<string> tokens)
    {
        IEnumerable creatures = GetEnumerable(provider.RegisteredCreaturesField?.GetValue(null));
        foreach (object? creature in creatures
                     .Cast<object?>()
                     .Where(entry => entry != null)
                     .OrderBy(entry => GetPrefabNameFromHolder(entry) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            string? prefabName = GetPrefabNameFromHolder(creature);
            if (string.IsNullOrWhiteSpace(prefabName) || string.IsNullOrWhiteSpace(provider.OwnerName))
            {
                continue;
            }

            TryAddOwnerMapping(owners, prefabName!, provider.OwnerName, priority: 1);
            tokens.Add($"cm:{provider.OwnerName}:{prefabName}");
        }
    }

    private static void CollectLocationManagerTemplateMappings(
        ProviderHandle provider,
        Dictionary<string, OwnerMapping> owners,
        List<string> tokens)
    {
        IEnumerable locations = GetEnumerable(provider.RegisteredLocationsField?.GetValue(null));
        foreach (object? location in locations
                     .Cast<object?>()
                     .Where(entry => entry != null)
                     .OrderBy(entry => GetPrefabNameFromHolder(entry) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            string? prefabName = GetPrefabNameFromHolder(location);
            if (string.IsNullOrWhiteSpace(prefabName) || string.IsNullOrWhiteSpace(provider.OwnerName))
            {
                continue;
            }

            TryAddOwnerMapping(owners, prefabName!, provider.OwnerName, priority: 1);
            tokens.Add($"lm:{provider.OwnerName}:{prefabName}");
        }
    }

    private static void TryAddOwnerMapping(
        Dictionary<string, OwnerMapping> owners,
        string prefabName,
        string ownerName,
        int priority)
    {
        string normalizedPrefabName = (prefabName ?? "").Trim();
        string normalizedOwnerName = (ownerName ?? "").Trim();
        if (normalizedPrefabName.Length == 0 || normalizedOwnerName.Length == 0)
        {
            return;
        }

        if (owners.TryGetValue(normalizedPrefabName, out OwnerMapping existing) &&
            existing.Priority <= priority)
        {
            return;
        }

        owners[normalizedPrefabName] = new OwnerMapping(normalizedOwnerName, priority);
    }

    private static string? GetPrefabNameFromHolder(object? holder)
    {
        if (holder == null)
        {
            return null;
        }

        if (!TryGetRawMemberValue(holder, "Prefab", out object? prefabValue) ||
            prefabValue is not GameObject prefab)
        {
            return GetPrefabNameFromLocationHolder(holder);
        }

        string prefabName = (prefab.name ?? "").Trim();
        if (prefabName.Length > 0)
        {
            return prefabName;
        }

        return GetPrefabNameFromLocationHolder(holder);
    }

    private static string? GetPrefabNameFromLocationHolder(object holder)
    {
        if (TryGetLocationComponentHolderPrefabName(holder, "Location", out string? prefabName) ||
            TryGetLocationComponentHolderPrefabName(holder, "location", out prefabName))
        {
            return prefabName;
        }

        if (TryGetRawMemberValue(holder, "ZoneLocation", out object? zoneLocationValue) &&
            zoneLocationValue != null &&
            TryGetRawMemberValue(zoneLocationValue, "m_prefabName", out object? prefabNameValue))
        {
            string normalizedPrefabName = (prefabNameValue?.ToString() ?? "").Trim();
            if (normalizedPrefabName.Length > 0)
            {
                return normalizedPrefabName;
            }
        }

        return null;
    }

    private static bool TryGetLocationComponentHolderPrefabName(object holder, string memberName, out string? prefabName)
    {
        prefabName = null;
        if (!TryGetRawMemberValue(holder, memberName, out object? memberValue) || memberValue == null)
        {
            return false;
        }

        if (memberValue is GameObject gameObject)
        {
            string normalizedPrefabName = (gameObject.name ?? "").Trim();
            if (normalizedPrefabName.Length == 0)
            {
                return false;
            }

            prefabName = normalizedPrefabName;
            return true;
        }

        if (memberValue is global::Location locationComponent)
        {
            string normalizedPrefabName = (((UnityEngine.Object)locationComponent).name ?? "").Trim();
            if (normalizedPrefabName.Length == 0)
            {
                return false;
            }

            prefabName = normalizedPrefabName;
            return true;
        }

        return false;
    }

    private static string ResolveOwnerNameFromSourceModHolder(object holder, string fallbackOwnerName)
    {
        if (TryGetRawMemberValue(holder, "SourceMod", out object? sourceMod) &&
            sourceMod != null &&
            TryResolvePluginOwnerName(sourceMod, out string ownerName))
        {
            return ownerName;
        }

        return fallbackOwnerName;
    }

    private static string ResolveAssemblyOwnerName(Assembly assembly)
    {
        foreach (PluginInfo pluginInfo in Chainloader.PluginInfos.Values)
        {
            if (!ReferenceEquals(pluginInfo.Instance?.GetType().Assembly, assembly))
            {
                continue;
            }

            string pluginName = (pluginInfo.Metadata.Name ?? "").Trim();
            if (pluginName.Length > 0)
            {
                return pluginName;
            }

            string pluginGuid = (pluginInfo.Metadata.GUID ?? "").Trim();
            if (pluginGuid.Length > 0)
            {
                return pluginGuid;
            }
        }

        string assemblyName = assembly.GetName().Name ?? assembly.FullName ?? "";
        return string.IsNullOrWhiteSpace(assemblyName) ? PrefabOwnerCatalog.UnknownOwnerName : assemblyName;
    }

    private static bool TryResolvePluginOwnerName(object sourceMod, out string ownerName)
    {
        ownerName = "";

        if (TryGetRawMemberValue(sourceMod, "GUID", out object? guidValue))
        {
            string pluginGuid = (guidValue?.ToString() ?? "").Trim();
            if (pluginGuid.Length > 0 &&
                Chainloader.PluginInfos.TryGetValue(pluginGuid, out PluginInfo pluginInfo))
            {
                string pluginName = (pluginInfo.Metadata.Name ?? "").Trim();
                ownerName = pluginName.Length > 0 ? pluginName : pluginGuid;
                return true;
            }

            if (pluginGuid.Length > 0)
            {
                ownerName = pluginGuid;
                return true;
            }
        }

        if (TryGetRawMemberValue(sourceMod, "Name", out object? nameValue))
        {
            string pluginName = (nameValue?.ToString() ?? "").Trim();
            if (pluginName.Length > 0)
            {
                ownerName = pluginName;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable GetEnumerable(object? value)
    {
        return value as IEnumerable ?? Array.Empty<object>();
    }

    private static bool TryGetRawMemberValue(object instance, string memberName, out object? value)
    {
        value = null;
        Type? currentType = instance.GetType();
        while (currentType != null)
        {
            PropertyInfo? property = currentType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                try
                {
                    value = property.GetValue(instance, null);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            FieldInfo? field = currentType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try
                {
                    value = field.GetValue(instance);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            currentType = currentType.BaseType;
        }

        return false;
    }

    private static Type? SafeGetType(Assembly assembly, string fullTypeName)
    {
        try
        {
            return assembly.GetType(fullTypeName, throwOnError: false, ignoreCase: false);
        }
        catch
        {
            return null;
        }
    }

    private static PropertyInfo? TryGetTypeProperty(Type? type, string memberName)
    {
        if (type == null || string.IsNullOrWhiteSpace(memberName))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            PropertyInfo? property = current.GetProperty(memberName, flags) ??
                                     current.GetProperties(flags).FirstOrDefault(candidate =>
                                         string.Equals(candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (property != null)
            {
                return property;
            }
        }

        return null;
    }

    private static FieldInfo? TryGetTypeField(Type? type, string memberName)
    {
        if (type == null || string.IsNullOrWhiteSpace(memberName))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            FieldInfo? field = current.GetField(memberName, flags) ??
                               current.GetFields(flags).FirstOrDefault(candidate =>
                                   string.Equals(candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
            if (field != null)
            {
                return field;
            }
        }

        return null;
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
}
