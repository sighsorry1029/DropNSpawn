using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SpawnSystemConfigurationEntry = DropNSpawn.CanonicalSpawnSystemEntry;

namespace DropNSpawn;

internal static class CreatureManagerSpawnReferenceSupport
{
    private enum ProviderKind
    {
        CreatureManagerTemplate,
        JotunnCreatureManager
    }

    internal sealed class ReferenceSnapshot
    {
        public List<ReferenceProjection> Projections { get; set; } = new();
        public string Signature { get; set; } = "";
    }

    internal sealed class ReferenceProjection
    {
        public SpawnSystemConfigurationEntry Entry { get; set; } = null!;
        public string SignatureToken { get; set; } = "";
        public string SourceKey { get; set; } = "";
    }

    private sealed class ProviderHandle
    {
        public ProviderKind Kind { get; set; } = ProviderKind.CreatureManagerTemplate;
        public Assembly Assembly { get; set; } = null!;
        public string AssemblyName { get; set; } = "";
        public Type CreatureType { get; set; } = null!;
        public Type? ManagerType { get; set; }
        public Type? InternalNameAttributeType { get; set; }
        public PropertyInfo? ManagerInstanceProperty { get; set; }
        public FieldInfo? ManagerCreaturesField { get; set; }
        public PropertyInfo? ManagerCreaturesProperty { get; set; }
        public FieldInfo? RegisteredCreaturesField { get; set; }
        public FieldInfo? CreatureConfigsField { get; set; }
    }

    private enum SpawnMode
    {
        Default,
        Custom,
        Disabled
    }

    private static readonly object Sync = new();
    private static readonly ISerializer SignatureSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults)
        .Build();

    private static string _providerAssemblySignature = "";
    private static List<ProviderHandle> _providerCache = new();
    private static string _snapshotAssemblySignature = "";
    private static ReferenceSnapshot? _snapshotCache;

    internal static void InvalidateProviderCache()
    {
        lock (Sync)
        {
            _providerAssemblySignature = "";
            _providerCache = new List<ProviderHandle>();
            InvalidateSnapshotUnsafe();
        }
    }

    internal static void InvalidateSnapshot()
    {
        lock (Sync)
        {
            InvalidateSnapshotUnsafe();
        }
    }

    internal static ReferenceSnapshot GetReferenceSnapshot(bool forceRefresh = false)
    {
        lock (Sync)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .ToArray();

            string assemblySignature = ReferenceRefreshSupport.ComputeStableHashForKeys(
                assemblies.Select(assembly => assembly.FullName ?? assembly.GetName().Name ?? ""));
            List<ProviderHandle> providers = GetProviders(assemblies, assemblySignature);
            if (!forceRefresh &&
                _snapshotCache != null &&
                string.Equals(_snapshotAssemblySignature, assemblySignature, StringComparison.Ordinal))
            {
                return _snapshotCache;
            }

            List<ReferenceProjection> projections = new();
            foreach (ProviderHandle provider in providers)
            {
                projections.AddRange(CollectReferenceProjections(provider));
            }

            ReferenceSnapshot snapshot = new()
            {
                Projections = projections,
                Signature = ReferenceRefreshSupport.ComputeStableHashForKeys(projections.Select(projection => projection.SignatureToken))
            };
            _snapshotAssemblySignature = assemblySignature;
            _snapshotCache = snapshot;
            return snapshot;
        }
    }

    private static void InvalidateSnapshotUnsafe()
    {
        _snapshotAssemblySignature = "";
        _snapshotCache = null;
    }

    private static List<ProviderHandle> GetProviders(Assembly[] assemblies, string assemblySignature)
    {
        if (string.Equals(_providerAssemblySignature, assemblySignature, StringComparison.Ordinal))
        {
            return _providerCache;
        }

        List<ProviderHandle> providers = new();
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

            providers.Add(new ProviderHandle
            {
                Kind = ProviderKind.CreatureManagerTemplate,
                Assembly = assembly,
                AssemblyName = assembly.GetName().Name ?? assembly.FullName ?? PrefabOwnerCatalog.UnknownOwnerName,
                CreatureType = creatureType,
                InternalNameAttributeType = SafeGetType(assembly, "CreatureManager.InternalName"),
                RegisteredCreaturesField = registeredCreaturesField,
                CreatureConfigsField = TryGetTypeField(creatureType, "creatureConfigs")
            });
        }

        ProviderHandle? jotunnProvider = TryCreateJotunnProvider(assemblies);
        if (jotunnProvider != null)
        {
            providers.Add(jotunnProvider);
        }

        _providerAssemblySignature = assemblySignature;
        _providerCache = providers;
        return _providerCache;
    }

    private static List<ReferenceProjection> CollectReferenceProjections(ProviderHandle provider)
    {
        return provider.Kind == ProviderKind.JotunnCreatureManager
            ? CollectJotunnReferenceProjections(provider)
            : CollectCreatureManagerTemplateReferenceProjections(provider);
    }

    private static ProviderHandle? TryCreateJotunnProvider(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            Type? managerType = SafeGetType(assembly, "Jotunn.Managers.CreatureManager");
            Type? creatureType = SafeGetType(assembly, "Jotunn.Entities.CustomCreature");
            if (managerType == null || creatureType == null)
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
                AssemblyName = assembly.GetName().Name ?? assembly.FullName ?? PrefabOwnerCatalog.UnknownOwnerName,
                CreatureType = creatureType,
                ManagerType = managerType,
                ManagerInstanceProperty = instanceProperty,
                ManagerCreaturesField = creaturesField,
                ManagerCreaturesProperty = creaturesProperty
            };
        }

        return null;
    }

    private static List<ReferenceProjection> CollectJotunnReferenceProjections(ProviderHandle provider)
    {
        List<ReferenceProjection> projections = new();
        object? managerInstance = provider.ManagerInstanceProperty?.GetValue(null, null);
        if (managerInstance == null)
        {
            return projections;
        }

        object? creaturesValue = provider.ManagerCreaturesField?.GetValue(managerInstance) ??
                                 provider.ManagerCreaturesProperty?.GetValue(managerInstance, null);
        foreach (object? creature in GetEnumerable(creaturesValue))
        {
            if (creature == null)
            {
                continue;
            }

            if (!TryGetRawMemberValue(creature, "Prefab", out object? prefabObject) ||
                prefabObject is not GameObject prefab ||
                string.IsNullOrWhiteSpace(prefab.name))
            {
                continue;
            }

            string sourceKey = ResolveJotunnSourceKey(creature, provider.AssemblyName);
            IEnumerable spawns = Enumerable.Empty<object>();
            if (TryGetRawMemberValue(creature, "Spawns", out object? spawnsValue))
            {
                spawns = GetEnumerable(spawnsValue);
            }

            foreach (object? spawn in spawns)
            {
                if (spawn is not SpawnSystem.SpawnData spawnData)
                {
                    continue;
                }

                SpawnSystemConfigurationEntry entry =
                    SpawnSystemManager.CreateReferenceEntryForExternalProjection(spawnData, prefab.name);
                if (string.IsNullOrWhiteSpace(entry.Prefab))
                {
                    continue;
                }

                entry.ReferenceOwnerName = sourceKey;

                projections.Add(new ReferenceProjection
                {
                    Entry = entry,
                    SignatureToken = $"{sourceKey}:{SignatureSerializer.Serialize(entry).TrimEnd('\r', '\n')}",
                    SourceKey = sourceKey
                });
            }
        }

        return projections;
    }

    private static string ResolveJotunnSourceKey(object creature, string fallback)
    {
        if (TryGetRawMemberValue(creature, "SourceMod", out object? sourceMod) && sourceMod != null)
        {
            if (TryGetRawMemberValue(sourceMod, "GUID", out object? guidValue))
            {
                string? sourceGuid = NormalizeOptionalString(guidValue?.ToString());
                string? pluginOwnerName = ResolvePluginOwnerName(sourceGuid);
                if (!string.IsNullOrWhiteSpace(pluginOwnerName))
                {
                    return pluginOwnerName!;
                }

                if (!string.IsNullOrWhiteSpace(sourceGuid))
                {
                    return sourceGuid!;
                }
            }

            if (TryGetRawMemberValue(sourceMod, "Name", out object? nameValue))
            {
                string? sourceName = NormalizeOptionalString(nameValue?.ToString());
                if (!string.IsNullOrWhiteSpace(sourceName))
                {
                    return sourceName!;
                }
            }
        }

        return fallback;
    }

    private static string? ResolvePluginOwnerName(string? pluginGuid)
    {
        string normalizedGuid = NormalizeOptionalString(pluginGuid) ?? "";
        if (normalizedGuid.Length == 0)
        {
            return null;
        }

        if (Chainloader.PluginInfos.TryGetValue(normalizedGuid, out BepInEx.PluginInfo pluginInfo))
        {
            string pluginName = NormalizeOptionalString(pluginInfo.Metadata.Name) ?? "";
            if (pluginName.Length > 0)
            {
                return pluginName;
            }

            return normalizedGuid;
        }

        return null;
    }

    private static List<ReferenceProjection> CollectCreatureManagerTemplateReferenceProjections(ProviderHandle provider)
    {
        List<ReferenceProjection> projections = new();
        IEnumerable creatures = GetEnumerable(provider.RegisteredCreaturesField?.GetValue(null));
        IDictionary? creatureConfigs = provider.CreatureConfigsField?.GetValue(null) as IDictionary;

        foreach (object? creature in creatures)
        {
            if (creature == null)
            {
                continue;
            }

            if (!TryGetRawMemberValue(creature, "Prefab", out object? prefabObject) ||
                prefabObject is not GameObject prefab ||
                string.IsNullOrWhiteSpace(prefab.name))
            {
                continue;
            }

            object? creatureConfig = null;
            if (creatureConfigs != null && creatureConfigs.Contains(creature))
            {
                creatureConfig = creatureConfigs[creature];
            }

            SpawnMode spawnMode = ResolveSpawnMode(creatureConfig);
            if (spawnMode == SpawnMode.Disabled)
            {
                continue;
            }

            if (!GetBoolEffective(creature, creatureConfig, spawnMode, "CanSpawn", fallbackValue: true))
            {
                continue;
            }

            SpawnSystemConfigurationEntry entry = BuildReferenceEntry(provider, prefab.name, creature, creatureConfig, spawnMode);
            if (string.IsNullOrWhiteSpace(entry.Prefab))
            {
                continue;
            }

            entry.ReferenceOwnerName = provider.AssemblyName;

            projections.Add(new ReferenceProjection
            {
                Entry = entry,
                SignatureToken = $"{provider.AssemblyName}:{SignatureSerializer.Serialize(entry).TrimEnd('\r', '\n')}",
                SourceKey = provider.AssemblyName
            });
        }

        return projections;
    }

    private static SpawnSystemConfigurationEntry BuildReferenceEntry(
        ProviderHandle provider,
        string prefabName,
        object creature,
        object? creatureConfig,
        SpawnMode spawnMode)
    {
        SpawnSystemConfigurationEntry entry = new()
        {
            Prefab = prefabName,
            Enabled = true
        };

        SpawnSystemSpawnDefinition spawn = new();
        SpawnSystemConditionsDefinition conditions = new();
        SpawnSystemModifiersDefinition modifiers = new();
        bool hasSpawn = false;
        bool hasConditions = false;
        bool hasModifiers = false;

        if (TryGetFloatEffective(creature, creatureConfig, spawnMode, "CheckSpawnInterval", out float spawnInterval))
        {
            spawn.SpawnInterval = spawnInterval;
            hasSpawn = true;
        }

        if (TryGetFloatEffective(creature, creatureConfig, spawnMode, "SpawnChance", out float spawnChance))
        {
            spawn.SpawnChance = spawnChance;
            hasSpawn = true;
        }

        if (TryGetFloatRangeEffective(creature, creatureConfig, spawnMode, "GroupSize", out float groupSizeMin, out float groupSizeMax))
        {
            spawn.GroupSize = RangeFormatting.From((int)Math.Round(groupSizeMin), (int)Math.Round(groupSizeMax));
            hasSpawn = true;
        }

        if (TryGetFloatEffective(creature, creatureConfig, spawnMode, "SpawnAltitude", out float groundOffset))
        {
            spawn.GroundOffset = groundOffset;
            hasSpawn = true;
        }

        if (GetBoolEffective(creature, creatureConfig, spawnMode, "AttackImmediately", fallbackValue: false))
        {
            spawn.HuntPlayer = true;
            hasSpawn = true;
        }

        if (!GetBoolEffective(creature, creatureConfig, spawnMode, "CanHaveStars", fallbackValue: true))
        {
            spawn.Level = RangeFormatting.From(1, 1);
            hasSpawn = true;
        }

        if (TryGetFloatEffective(creature, creatureConfig, spawnMode, "Maximum", out float maximum))
        {
            conditions.MaxSpawned = (int)Math.Round(maximum);
            hasConditions = true;
        }

        if (TryGetFloatRangeEffective(creature, creatureConfig, spawnMode, "RequiredAltitude", out float minAltitude, out float maxAltitude))
        {
            conditions.Altitude = RangeFormatting.From(minAltitude, maxAltitude);
            hasConditions = true;
        }

        if (TryGetFloatRangeEffective(creature, creatureConfig, spawnMode, "RequiredOceanDepth", out float minOceanDepth, out float maxOceanDepth))
        {
            conditions.OceanDepth = RangeFormatting.From(minOceanDepth, maxOceanDepth);
            hasConditions = true;
        }

        if (TryGetEffectiveMemberValue(creature, creatureConfig, spawnMode, "SpecificSpawnTime", out object? spawnTimeValue))
        {
            TimeOfDayDefinition? timeOfDay = ConvertTimeOfDayDefinition(spawnTimeValue);
            if (timeOfDay != null)
            {
                conditions.TimeOfDay = timeOfDay;
                hasConditions = true;
            }
        }

        if (TryGetEffectiveMemberValue(creature, creatureConfig, spawnMode, "Biome", out object? biomeValue) &&
            biomeValue is Heightmap.Biome biomes &&
            biomes != Heightmap.Biome.None)
        {
            conditions.Biomes = ConvertBiomes(biomes);
            hasConditions = true;
        }

        if (TryGetEffectiveMemberValue(creature, creatureConfig, spawnMode, "SpecificSpawnArea", out object? spawnAreaValue))
        {
            List<string>? biomeAreas = ConvertBiomeAreas(spawnAreaValue);
            if (biomeAreas != null)
            {
                conditions.BiomeAreas = biomeAreas;
                hasConditions = true;
            }
        }

        if (TryGetEffectiveMemberValue(creature, creatureConfig, spawnMode, "RequiredWeather", out object? weatherValue))
        {
            List<string>? environments = ConvertRequiredEnvironments(provider, weatherValue);
            if (environments != null && environments.Count > 0)
            {
                conditions.RequiredEnvironments = environments;
                hasConditions = true;
            }
        }

        if (TryGetEffectiveMemberValue(creature, creatureConfig, spawnMode, "RequiredGlobalKey", out object? globalKeyValue))
        {
            string? requiredGlobalKey = ConvertInternalName(provider, globalKeyValue);
            if (!string.IsNullOrWhiteSpace(requiredGlobalKey))
            {
                conditions.RequiredGlobalKey = requiredGlobalKey;
                hasConditions = true;
            }
        }

        if (TryGetEffectiveMemberValue(creature, creatureConfig, spawnMode, "ForestSpawn", out object? forestValue))
        {
            bool? inForest = ConvertForestToggle(forestValue);
            if (inForest.HasValue)
            {
                conditions.InForest = inForest;
                hasConditions = true;
            }
        }

        if (TryGetEffectiveMemberValue(creature, creatureConfig, spawnMode, "CreatureFaction", out object? factionValue))
        {
            string? faction = NormalizeOptionalString(factionValue?.ToString());
            if (!string.IsNullOrWhiteSpace(faction))
            {
                modifiers.Faction = faction;
                hasModifiers = true;
            }
        }

        entry.SpawnSystem = hasSpawn ? spawn : null;
        entry.Conditions = hasConditions ? conditions : null;
        entry.Modifiers = hasModifiers ? modifiers : null;
        return entry;
    }

    private static SpawnMode ResolveSpawnMode(object? creatureConfig)
    {
        if (!TryGetWrappedMemberValue(creatureConfig, "Spawn", out object? spawnValue))
        {
            return SpawnMode.Default;
        }

        string token = NormalizeOptionalString(spawnValue?.ToString()) ?? "";
        if (token.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return SpawnMode.Disabled;
        }

        if (token.Equals("Custom", StringComparison.OrdinalIgnoreCase))
        {
            return SpawnMode.Custom;
        }

        return SpawnMode.Default;
    }

    private static IEnumerable GetEnumerable(object? value)
    {
        if (value is string || value is not IEnumerable enumerable)
        {
            return Array.Empty<object>();
        }

        return enumerable;
    }

    private static bool TryGetEffectiveMemberValue(object creature, object? creatureConfig, SpawnMode spawnMode, string memberName, out object? value)
    {
        value = null;
        if (spawnMode == SpawnMode.Custom &&
            TryGetWrappedMemberValue(creatureConfig, memberName, out value))
        {
            return true;
        }

        if (TryGetMemberValue(creature, memberName, out value))
        {
            return true;
        }

        if (spawnMode != SpawnMode.Default &&
            TryGetWrappedMemberValue(creatureConfig, memberName, out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    private static bool GetBoolEffective(object creature, object? creatureConfig, SpawnMode spawnMode, string memberName, bool fallbackValue)
    {
        return TryGetEffectiveMemberValue(creature, creatureConfig, spawnMode, memberName, out object? value) &&
               TryConvertToBool(value, out bool parsed)
            ? parsed
            : fallbackValue;
    }

    private static bool TryGetFloatEffective(object creature, object? creatureConfig, SpawnMode spawnMode, string memberName, out float value)
    {
        if (TryGetEffectiveMemberValue(creature, creatureConfig, spawnMode, memberName, out object? rawValue) &&
            TryConvertToFloat(rawValue, out value))
        {
            return true;
        }

        value = 0f;
        return false;
    }

    private static bool TryGetFloatRangeEffective(object creature, object? creatureConfig, SpawnMode spawnMode, string memberName, out float min, out float max)
    {
        if (TryGetEffectiveMemberValue(creature, creatureConfig, spawnMode, memberName, out object? rawValue) &&
            TryReadRange(rawValue, out min, out max))
        {
            return true;
        }

        min = 0f;
        max = 0f;
        return false;
    }

    private static bool TryReadRange(object? value, out float min, out float max)
    {
        min = 0f;
        max = 0f;
        if (value == null)
        {
            return false;
        }

        Type type = value.GetType();
        if (!TryGetRawMemberValue(value, "min", out object? minValue) ||
            !TryGetRawMemberValue(value, "max", out object? maxValue) ||
            !TryConvertToFloat(minValue, out min) ||
            !TryConvertToFloat(maxValue, out max))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetWrappedMemberValue(object? instance, string memberName, out object? value)
    {
        value = null;
        if (!TryGetRawMemberValue(instance, memberName, out object? rawValue))
        {
            return false;
        }

        value = UnwrapConfigValue(rawValue);
        return true;
    }

    private static bool TryGetMemberValue(object? instance, string memberName, out object? value)
    {
        value = null;
        if (!TryGetRawMemberValue(instance, memberName, out object? rawValue))
        {
            return false;
        }

        value = UnwrapConfigValue(rawValue);
        return true;
    }

    private static bool TryGetRawMemberValue(object? instance, string memberName, out object? value)
    {
        value = null;
        if (instance == null || string.IsNullOrWhiteSpace(memberName))
        {
            return false;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        Type type = instance.GetType();

        FieldInfo? field = type.GetField(memberName, flags) ??
                           type.GetFields(flags).FirstOrDefault(candidate =>
                               string.Equals(candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
        if (field != null)
        {
            value = field.GetValue(instance);
            return true;
        }

        PropertyInfo? property = type.GetProperty(memberName, flags) ??
                                 type.GetProperties(flags).FirstOrDefault(candidate =>
                                     string.Equals(candidate.Name, memberName, StringComparison.OrdinalIgnoreCase));
        if (property == null || !property.CanRead)
        {
            return false;
        }

        value = property.GetValue(instance, null);
        return true;
    }

    private static object? UnwrapConfigValue(object? value)
    {
        object? current = value;
        for (int depth = 0; depth < 4 && current != null; depth++)
        {
            if (current is string || current.GetType().IsPrimitive || current.GetType().IsEnum)
            {
                return current;
            }

            if (current is ConfigEntryBase configEntry)
            {
                return configEntry.BoxedValue;
            }

            MethodInfo? getMethod = current.GetType().GetMethod("get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getMethod != null && getMethod.ReturnType != typeof(void))
            {
                current = getMethod.Invoke(current, null);
                continue;
            }

            PropertyInfo? boxedValueProperty = current.GetType().GetProperty("BoxedValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (boxedValueProperty != null && boxedValueProperty.CanRead)
            {
                current = boxedValueProperty.GetValue(current, null);
                continue;
            }

            PropertyInfo? valueProperty = current.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (valueProperty != null && valueProperty.CanRead)
            {
                current = valueProperty.GetValue(current, null);
                continue;
            }

            break;
        }

        return current;
    }

    private static bool TryConvertToBool(object? value, out bool parsed)
    {
        switch (value)
        {
            case bool boolValue:
                parsed = boolValue;
                return true;
            case string stringValue when bool.TryParse(stringValue, out bool boolResult):
                parsed = boolResult;
                return true;
            default:
                parsed = false;
                return false;
        }
    }

    private static bool TryConvertToFloat(object? value, out float parsed)
    {
        switch (value)
        {
            case byte byteValue:
                parsed = byteValue;
                return true;
            case short shortValue:
                parsed = shortValue;
                return true;
            case int intValue:
                parsed = intValue;
                return true;
            case long longValue:
                parsed = longValue;
                return true;
            case float floatValue:
                parsed = floatValue;
                return true;
            case double doubleValue:
                parsed = (float)doubleValue;
                return true;
            case decimal decimalValue:
                parsed = (float)decimalValue;
                return true;
            case string stringValue when float.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatResult):
                parsed = floatResult;
                return true;
            default:
                parsed = 0f;
                return false;
        }
    }

    private static TimeOfDayDefinition? ConvertTimeOfDayDefinition(object? value)
    {
        string token = NormalizeOptionalString(value?.ToString()) ?? "";
        if (token.Length == 0)
        {
            return null;
        }

        if (token.Equals("Day", StringComparison.OrdinalIgnoreCase))
        {
            return new TimeOfDayDefinition { Values = new List<string> { "day" } };
        }

        if (token.Equals("Night", StringComparison.OrdinalIgnoreCase))
        {
            return new TimeOfDayDefinition { Values = new List<string> { "night" } };
        }

        if (token.Equals("Always", StringComparison.OrdinalIgnoreCase))
        {
            return new TimeOfDayDefinition { Values = new List<string> { "day", "night" } };
        }

        return null;
    }

    private static List<string>? ConvertBiomeAreas(object? value)
    {
        string token = NormalizeOptionalString(value?.ToString()) ?? "";
        if (token.Length == 0)
        {
            return null;
        }

        if (token.Equals("Everywhere", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { nameof(Heightmap.BiomeArea.Everything) };
        }

        if (token.Equals("Center", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { nameof(Heightmap.BiomeArea.Median) };
        }

        if (token.Equals("Edge", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { nameof(Heightmap.BiomeArea.Edge) };
        }

        return null;
    }

    private static bool? ConvertForestToggle(object? value)
    {
        string token = NormalizeOptionalString(value?.ToString()) ?? "";
        if (token.Length == 0)
        {
            return null;
        }

        if (token.Equals("Yes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (token.Equals("No", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return null;
    }

    private static List<string> ConvertBiomes(Heightmap.Biome biomes)
    {
        if (biomes == Heightmap.Biome.All)
        {
            return new List<string> { nameof(Heightmap.Biome.All) };
        }

        List<string> values = new();
        foreach (Heightmap.Biome biome in Enum.GetValues(typeof(Heightmap.Biome)))
        {
            if (biome == Heightmap.Biome.None || biome == Heightmap.Biome.All)
            {
                continue;
            }

            if ((biomes & biome) == biome)
            {
                values.Add(biome.ToString());
            }
        }

        return values;
    }

    private static List<string>? ConvertRequiredEnvironments(ProviderHandle provider, object? value)
    {
        if (value == null || !value.GetType().IsEnum)
        {
            return null;
        }

        List<string> values = new();
        ulong mask = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            object? fieldValue = field.GetValue(null);
            if (fieldValue == null)
            {
                continue;
            }

            ulong fieldMask = Convert.ToUInt64(fieldValue, CultureInfo.InvariantCulture);
            if (fieldMask == 0 || (mask & fieldMask) != fieldMask)
            {
                continue;
            }

            string? internalName = GetInternalName(provider, field);
            if (!string.IsNullOrWhiteSpace(internalName))
            {
                values.Add(internalName!);
            }
        }

        return values.Count == 0 ? null : values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ConvertInternalName(ProviderHandle provider, object? value)
    {
        if (value == null || !value.GetType().IsEnum)
        {
            return NormalizeOptionalString(value?.ToString());
        }

        string? fieldName = value.ToString();
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        FieldInfo? field = value.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
        if (field == null)
        {
            return null;
        }

        string? internalName = GetInternalName(provider, field);
        return NormalizeOptionalString(internalName);
    }

    private static string? GetInternalName(ProviderHandle provider, MemberInfo member)
    {
        if (provider.InternalNameAttributeType == null)
        {
            return member.Name;
        }

        object? attribute = member.GetCustomAttributes(provider.InternalNameAttributeType, inherit: false).FirstOrDefault();
        if (attribute == null)
        {
            return member.Name;
        }

        if (TryGetRawMemberValue(attribute, "internalName", out object? internalNameValue))
        {
            return NormalizeOptionalString(internalNameValue?.ToString());
        }

        return member.Name;
    }

    private static string? NormalizeOptionalString(string? value)
    {
        string normalized = (value ?? "").Trim();
        return normalized.Length == 0 ? null : normalized;
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
}
