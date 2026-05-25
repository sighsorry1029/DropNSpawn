using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class ConditionsDefinition
{
    [YamlMember(Order = 1)]
    public IntRangeDefinition? Level { get; set; }

    [YamlMember(Order = 2)]
    public FloatRangeDefinition? Altitude { get; set; }

    [YamlIgnore]
    public int? MinLevel { get; set; }

    [YamlIgnore]
    public int? MaxLevel { get; set; }

    [YamlIgnore]
    public float? MinAltitude { get; set; }

    [YamlIgnore]
    public float? MaxAltitude { get; set; }

    [YamlMember(Order = 3)]
    public FloatRangeDefinition? DistanceFromCenter { get; set; }

    [YamlIgnore]
    public float? MinDistanceFromCenter { get; set; }

    [YamlIgnore]
    public float? MaxDistanceFromCenter { get; set; }

    [YamlMember(Order = 4)]
    public List<string>? Biomes { get; set; }

    [YamlIgnore]
    public Heightmap.Biome? ResolvedBiomeMask { get; set; }

    [YamlMember(Order = 5)]
    public List<string>? Locations { get; set; }

    [YamlMember(Order = 6)]
    public TimeOfDayDefinition? TimeOfDay { get; set; }

    [YamlMember(Order = 7)]
    public List<string>? RequiredEnvironments { get; set; }

    [YamlMember(Order = 8)]
    public List<string>? RequiredGlobalKeys { get; set; }

    [YamlMember(Order = 9)]
    public List<string>? ForbiddenGlobalKeys { get; set; }

    [YamlMember(Order = 10)]
    public List<string>? States { get; set; }

    [YamlMember(Order = 11)]
    public List<string>? Factions { get; set; }

    [YamlMember(Order = 12)]
    public bool? InForest { get; set; }

    [YamlMember(Order = 13)]
    public bool? InDungeon { get; set; }

    [YamlMember(Order = 14)]
    public bool? InsidePlayerBase { get; set; }
}

internal static class DropConditionEvaluator
{
    private enum CachedLocationResolutionMode
    {
        Stable,
        Spatial
    }

    private sealed class CachedLocationResolution
    {
        public int Epoch { get; set; }
        public Vector2i Zone { get; set; }
        public Vector3 Position { get; set; }
        public string LocationName { get; set; } = "";
        public bool HasLocationName { get; set; }
        public CachedLocationResolutionMode Mode { get; set; }
    }

    private static readonly Dictionary<int, CachedLocationResolution> CachedLocationResolutionsByObjectId = new();
    private const int MaxCachedLocationResolutions = 4096;
    private const float SpatialResolutionReuseDistanceSqr = 1f;
    private static int _locationResolutionEpoch;
    private static int _lastLocationResolutionZoneSystemId;
    private static int _lastLocationResolutionLocationInstanceCount = -1;

    internal static bool HasConditions(ConditionsDefinition? conditions)
    {
        return conditions != null &&
               (conditions.ResolvedBiomeMask.HasValue ||
                HasAnyValues(conditions.Biomes) ||
                HasAnyValues(conditions.RequiredGlobalKeys) ||
                HasAnyValues(conditions.ForbiddenGlobalKeys) ||
                HasAnyValues(conditions.Locations) ||
                conditions.TimeOfDay != null ||
                HasAnyValues(conditions.RequiredEnvironments) ||
                conditions.InDungeon.HasValue ||
                conditions.InForest.HasValue ||
                conditions.DistanceFromCenter?.HasValues() == true ||
                conditions.MinDistanceFromCenter.HasValue ||
                conditions.MaxDistanceFromCenter.HasValue ||
                conditions.Altitude?.HasValues() == true ||
                conditions.MinAltitude.HasValue ||
                conditions.MaxAltitude.HasValue ||
                conditions.InsidePlayerBase.HasValue);
    }

    internal static bool HasDynamicConditions(ConditionsDefinition? conditions)
    {
        return conditions != null &&
               (HasAnyValues(conditions.RequiredGlobalKeys) ||
                HasAnyValues(conditions.ForbiddenGlobalKeys) ||
                conditions.TimeOfDay != null ||
                HasAnyValues(conditions.RequiredEnvironments) ||
                conditions.InsidePlayerBase.HasValue);
    }

    internal static bool HasStaticConditions(ConditionsDefinition? conditions)
    {
        return conditions != null &&
               (conditions.ResolvedBiomeMask.HasValue ||
                HasAnyValues(conditions.Biomes) ||
                HasAnyValues(conditions.Locations) ||
                conditions.InDungeon.HasValue ||
                conditions.InForest.HasValue ||
                conditions.DistanceFromCenter?.HasValues() == true ||
                conditions.MinDistanceFromCenter.HasValue ||
                conditions.MaxDistanceFromCenter.HasValue ||
                conditions.Altitude?.HasValues() == true ||
                conditions.MinAltitude.HasValue ||
                conditions.MaxAltitude.HasValue);
    }

    internal static bool HasCharacterConditions(ConditionsDefinition? conditions)
    {
        return HasConditions(conditions) ||
               (conditions != null &&
                (conditions.Level?.HasValues() == true ||
                 conditions.MinLevel.HasValue ||
                 conditions.MaxLevel.HasValue ||
                 HasAnyValues(conditions.States) ||
                 HasAnyValues(conditions.Factions)));
    }

    internal static bool AreSatisfied(GameObject gameObject, ConditionsDefinition? conditions)
    {
        return AreSatisfied(gameObject, conditions, (string?)null);
    }

    internal static bool AreSatisfied(GameObject gameObject, ConditionsDefinition? conditions, string? resolvedLocationName)
    {
        if (!HasConditions(conditions))
        {
            return true;
        }

        ConditionsDefinition activeConditions = conditions!;
        Vector3 position = gameObject.transform.position;

        if (HasBiomeCondition(activeConditions))
        {
            Heightmap.Biome currentBiome = WorldGenerator.instance?.GetBiome(position) ?? Heightmap.FindBiome(position);
            if (!MatchesBiomeCondition(currentBiome, activeConditions))
            {
                return false;
            }
        }

        if (HasAnyValues(activeConditions.RequiredGlobalKeys))
        {
            if (ZoneSystem.instance == null)
            {
                return false;
            }

            foreach (string key in activeConditions.RequiredGlobalKeys!)
            {
                string trimmedKey = (key ?? "").Trim();
                if (trimmedKey.Length == 0)
                {
                    continue;
                }

                if (!ZoneSystem.instance.GetGlobalKey(trimmedKey))
                {
                    return false;
                }
            }
        }

        if (HasAnyValues(activeConditions.ForbiddenGlobalKeys) && ZoneSystem.instance != null)
        {
            foreach (string key in activeConditions.ForbiddenGlobalKeys!)
            {
                string trimmedKey = (key ?? "").Trim();
                if (trimmedKey.Length == 0)
                {
                    continue;
                }

                if (ZoneSystem.instance.GetGlobalKey(trimmedKey))
                {
                    return false;
                }
            }
        }

        if (HasAnyValues(activeConditions.Locations))
        {
            string? currentLocation = ResolveLocationName(gameObject, position, resolvedLocationName);
            if (string.IsNullOrWhiteSpace(currentLocation))
            {
                return false;
            }

            string resolvedLocation = currentLocation!;
            if (!activeConditions.Locations!.Any(name => MatchesName(resolvedLocation, name)))
            {
                return false;
            }
        }

        if (activeConditions.TimeOfDay != null && !MatchesTimeOfDay(activeConditions.TimeOfDay))
        {
            return false;
        }

        if (HasAnyValues(activeConditions.RequiredEnvironments))
        {
            string? currentEnvironment = EnvMan.instance?.GetCurrentEnvironment()?.m_name;
            if (string.IsNullOrWhiteSpace(currentEnvironment))
            {
                return false;
            }

            string resolvedEnvironment = currentEnvironment!;
            if (!activeConditions.RequiredEnvironments!.Any(name => MatchesName(resolvedEnvironment, name)))
            {
                return false;
            }
        }

        if (activeConditions.InDungeon.HasValue)
        {
            bool isInDungeon = Character.InInterior(position);
            if (isInDungeon != activeConditions.InDungeon.Value)
            {
                return false;
            }
        }

        if (activeConditions.InForest.HasValue)
        {
            bool isInForest = WorldGenerator.InForest(position);
            if (isInForest != activeConditions.InForest.Value)
            {
                return false;
            }
        }

        float distanceFromCenter = new Vector2(position.x, position.z).magnitude;
        float? minDistanceFromCenter = RangeFormatting.GetMin(activeConditions.DistanceFromCenter, activeConditions.MinDistanceFromCenter);
        float? maxDistanceFromCenter = RangeFormatting.GetMax(activeConditions.DistanceFromCenter, activeConditions.MinDistanceFromCenter, activeConditions.MaxDistanceFromCenter);
        if (minDistanceFromCenter.HasValue && distanceFromCenter < minDistanceFromCenter.Value)
        {
            return false;
        }

        if (maxDistanceFromCenter.HasValue && distanceFromCenter > maxDistanceFromCenter.Value)
        {
            return false;
        }

        float? minAltitude = RangeFormatting.GetMin(activeConditions.Altitude, activeConditions.MinAltitude);
        float? maxAltitude = RangeFormatting.GetMax(activeConditions.Altitude, activeConditions.MinAltitude, activeConditions.MaxAltitude);
        if (minAltitude.HasValue && position.y < minAltitude.Value)
        {
            return false;
        }

        if (maxAltitude.HasValue && position.y > maxAltitude.Value)
        {
            return false;
        }

        if (activeConditions.InsidePlayerBase.HasValue)
        {
            bool isInsidePlayerBase = EffectArea.IsPointInsideArea(position, EffectArea.Type.PlayerBase) != null;
            if (isInsidePlayerBase != activeConditions.InsidePlayerBase.Value)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AreStaticConditionsSatisfied(GameObject gameObject, ConditionsDefinition? conditions, string? resolvedLocationName = null)
    {
        if (!HasStaticConditions(conditions))
        {
            return true;
        }

        ConditionsDefinition activeConditions = conditions!;
        Vector3 position = gameObject.transform.position;

        if (HasBiomeCondition(activeConditions))
        {
            Heightmap.Biome currentBiome = WorldGenerator.instance?.GetBiome(position) ?? Heightmap.FindBiome(position);
            if (!MatchesBiomeCondition(currentBiome, activeConditions))
            {
                return false;
            }
        }

        if (HasAnyValues(activeConditions.Locations))
        {
            string? currentLocation = ResolveLocationName(gameObject, position, resolvedLocationName);
            if (string.IsNullOrWhiteSpace(currentLocation))
            {
                return false;
            }

            string resolvedLocation = currentLocation!;
            if (!activeConditions.Locations!.Any(name => MatchesName(resolvedLocation, name)))
            {
                return false;
            }
        }

        if (activeConditions.InDungeon.HasValue)
        {
            bool isInDungeon = Character.InInterior(position);
            if (isInDungeon != activeConditions.InDungeon.Value)
            {
                return false;
            }
        }

        if (activeConditions.InForest.HasValue)
        {
            bool isInForest = WorldGenerator.InForest(position);
            if (isInForest != activeConditions.InForest.Value)
            {
                return false;
            }
        }

        float distanceFromCenter = new Vector2(position.x, position.z).magnitude;
        float? minDistanceFromCenter = RangeFormatting.GetMin(activeConditions.DistanceFromCenter, activeConditions.MinDistanceFromCenter);
        float? maxDistanceFromCenter = RangeFormatting.GetMax(activeConditions.DistanceFromCenter, activeConditions.MinDistanceFromCenter, activeConditions.MaxDistanceFromCenter);
        if (minDistanceFromCenter.HasValue && distanceFromCenter < minDistanceFromCenter.Value)
        {
            return false;
        }

        if (maxDistanceFromCenter.HasValue && distanceFromCenter > maxDistanceFromCenter.Value)
        {
            return false;
        }

        float? minAltitude = RangeFormatting.GetMin(activeConditions.Altitude, activeConditions.MinAltitude);
        float? maxAltitude = RangeFormatting.GetMax(activeConditions.Altitude, activeConditions.MinAltitude, activeConditions.MaxAltitude);
        if (minAltitude.HasValue && position.y < minAltitude.Value)
        {
            return false;
        }

        if (maxAltitude.HasValue && position.y > maxAltitude.Value)
        {
            return false;
        }

        return true;
    }

    internal static string? GetResolvedLocationNameForConditions(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return null;
        }

        return ResolveLocationName(gameObject, gameObject.transform.position, null);
    }

    internal static bool AreDynamicConditionsSatisfied(
        ConditionsDefinition? conditions,
        int timeOfDayPhaseMarker,
        string? environmentName,
        bool isInsidePlayerBase,
        Func<string, bool> getGlobalKeyState)
    {
        if (!HasDynamicConditions(conditions))
        {
            return true;
        }

        if (HasAnyValues(conditions!.RequiredGlobalKeys))
        {
            foreach (string key in conditions.RequiredGlobalKeys!)
            {
                string trimmedKey = (key ?? "").Trim();
                if (trimmedKey.Length == 0)
                {
                    continue;
                }

                if (!getGlobalKeyState(trimmedKey))
                {
                    return false;
                }
            }
        }

        if (HasAnyValues(conditions.ForbiddenGlobalKeys))
        {
            foreach (string key in conditions.ForbiddenGlobalKeys!)
            {
                string trimmedKey = (key ?? "").Trim();
                if (trimmedKey.Length == 0)
                {
                    continue;
                }

                if (getGlobalKeyState(trimmedKey))
                {
                    return false;
                }
            }
        }

        if (conditions.TimeOfDay != null && !MatchesTimeOfDay(conditions.TimeOfDay, timeOfDayPhaseMarker))
        {
            return false;
        }

        if (HasAnyValues(conditions.RequiredEnvironments))
        {
            string resolvedEnvironment = (environmentName ?? "").Trim();
            if (resolvedEnvironment.Length == 0 ||
                !conditions.RequiredEnvironments!.Any(name => MatchesName(resolvedEnvironment, name)))
            {
                return false;
            }
        }

        if (conditions.InsidePlayerBase.HasValue &&
            isInsidePlayerBase != conditions.InsidePlayerBase.Value)
        {
            return false;
        }

        return true;
    }

    internal static bool AreSatisfied(Character character, ConditionsDefinition? conditions)
    {
        if (!HasCharacterConditions(conditions))
        {
            return true;
        }

        if (character == null)
        {
            return false;
        }

        if (!AreSatisfied(character.gameObject, conditions))
        {
            return false;
        }

        ConditionsDefinition activeConditions = conditions!;
        int? minLevel = RangeFormatting.GetMin(activeConditions.Level, activeConditions.MinLevel);
        int? maxLevel = RangeFormatting.GetMax(activeConditions.Level, activeConditions.MinLevel, activeConditions.MaxLevel);
        if (minLevel.HasValue && character.GetLevel() < minLevel.Value)
        {
            return false;
        }

        if (maxLevel.HasValue && character.GetLevel() > maxLevel.Value)
        {
            return false;
        }

        if (HasAnyValues(activeConditions.Factions))
        {
            Character.Faction currentFaction = character.GetFaction();
            if (!activeConditions.Factions!.Any(name => MatchesFaction(currentFaction, name)))
            {
                return false;
            }
        }

        if (HasAnyValues(activeConditions.States))
        {
            if (!activeConditions.States!.Any(name => MatchesCreatureState(character, name)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasAnyValues(List<string>? values)
    {
        return values != null && values.Any(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool HasBiomeCondition(ConditionsDefinition? conditions)
    {
        return conditions != null &&
               (conditions.ResolvedBiomeMask.HasValue || HasAnyValues(conditions.Biomes));
    }

    private static bool MatchesBiomeCondition(Heightmap.Biome currentBiome, ConditionsDefinition? conditions)
    {
        if (conditions?.ResolvedBiomeMask.HasValue == true)
        {
            return (currentBiome & conditions.ResolvedBiomeMask.Value) != 0;
        }

        return conditions?.Biomes?.Any(name => MatchesBiome(currentBiome, name)) == true;
    }

    private static bool MatchesBiome(Heightmap.Biome currentBiome, string configuredBiome)
    {
        return BiomeResolutionSupport.MatchesBiome(currentBiome, configuredBiome);
    }

    private static bool MatchesName(string currentValue, string configuredValue)
    {
        string trimmedName = (configuredValue ?? "").Trim();
        return trimmedName.Length > 0 && string.Equals(currentValue, trimmedName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesTimeOfDay(TimeOfDayDefinition configuredTimeOfDay)
    {
        return TimeOfDayFormatting.MatchesCurrentTime(configuredTimeOfDay);
    }

    private static bool MatchesTimeOfDay(TimeOfDayDefinition configuredTimeOfDay, int phaseMarker)
    {
        if (!configuredTimeOfDay.HasValues())
        {
            return false;
        }

        return configuredTimeOfDay.Values.Any(value => MatchesTimeOfDayToken(value, phaseMarker));
    }

    private static bool MatchesTimeOfDayToken(string token, int phaseMarker)
    {
        string normalized = (token ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "day" => phaseMarker == 1 || phaseMarker == 2,
            "afternoon" => phaseMarker == 2,
            "night" => phaseMarker == 0,
            _ => false
        };
    }

    private static bool MatchesFaction(Character.Faction currentFaction, string configuredFaction)
    {
        return FactionIntegration.Matches(currentFaction, configuredFaction);
    }

    private static bool MatchesCreatureState(Character character, string configuredState)
    {
        string normalizedState = (configuredState ?? "").Trim().ToLowerInvariant();
        if (normalizedState.Length == 0)
        {
            return false;
        }

        bool isTamed = character.IsTamed();
        MonsterAI? monsterAI = character.GetComponent<MonsterAI>();
        bool isEvent = monsterAI != null && monsterAI.IsEventCreature();

        return normalizedState switch
        {
            "default" => !isTamed && !isEvent,
            "tamed" => isTamed,
            "event" => isEvent,
            _ => false
        };
    }

    private static string? ResolveLocationName(GameObject? gameObject, Vector3 position, string? explicitLocationName)
    {
        string trimmedExplicitName = (explicitLocationName ?? "").Trim();
        if (trimmedExplicitName.Length > 0)
        {
            return trimmedExplicitName;
        }

        EnsureLocationResolutionCacheState();

        if (SpawnerManager.TryGetResolvedLocationNameForConditions(gameObject, out string resolvedSpawnerLocation) &&
            !string.IsNullOrWhiteSpace(resolvedSpawnerLocation))
        {
            CacheResolvedLocationName(gameObject, position, resolvedSpawnerLocation, CachedLocationResolutionMode.Stable);
            return resolvedSpawnerLocation;
        }

        string? parentLocationName = GetOwningLocationName(gameObject);
        if (!string.IsNullOrWhiteSpace(parentLocationName))
        {
            CacheResolvedLocationName(gameObject, position, parentLocationName, CachedLocationResolutionMode.Stable);
            return parentLocationName;
        }

        if (TryGetCachedLocationResolution(gameObject, position, out string? cachedLocationName))
        {
            return cachedLocationName;
        }

        string? zoneLocationName = GetZoneMatchedLocationName(position);
        if (!string.IsNullOrWhiteSpace(zoneLocationName))
        {
            CacheResolvedLocationName(gameObject, position, zoneLocationName, CachedLocationResolutionMode.Spatial);
            return zoneLocationName;
        }

        string? spatialLocationName = GetLocationName(position);
        CacheResolvedLocationName(gameObject, position, spatialLocationName, CachedLocationResolutionMode.Spatial);
        return spatialLocationName;
    }

    private static string? GetOwningLocationName(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return null;
        }

        Location? location = gameObject.GetComponentInParent<Location>(true);
        if (location != null &&
            LocationManager.TryResolveRuntimeLocationPrefabName(location, out string resolvedPrefabName) &&
            !string.IsNullOrWhiteSpace(resolvedPrefabName))
        {
            return resolvedPrefabName;
        }

        return location != null ? GetLocationName(location) : null;
    }

    private static string? GetLocationName(Vector3 position)
    {
        Location? location = Location.GetLocation(position);
        if (location != null &&
            LocationManager.TryResolveRuntimeLocationPrefabName(location, out string resolvedPrefabName) &&
            !string.IsNullOrWhiteSpace(resolvedPrefabName))
        {
            return resolvedPrefabName;
        }

        return location != null ? GetLocationName(location) : null;
    }

    private static string? GetZoneMatchedLocationName(Vector3 position)
    {
        if (ZoneSystem.instance == null)
        {
            return null;
        }

        Vector2i zone = ZoneSystem.GetZone(position);
        if (!ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance locationInstance))
        {
            return null;
        }

        float radius = Mathf.Max(locationInstance.m_location.m_exteriorRadius, locationInstance.m_location.m_interiorRadius);
        if (radius <= 0f || Utils.DistanceXZ(locationInstance.m_position, position) > radius)
        {
            return null;
        }

        Location? liveZoneLocation = Location.GetZoneLocation(position);
        if (liveZoneLocation != null &&
            LocationManager.TryResolveRuntimeLocationPrefabName(liveZoneLocation, out string resolvedPrefabName) &&
            !string.IsNullOrWhiteSpace(resolvedPrefabName))
        {
            return resolvedPrefabName;
        }

        string prefabName = (locationInstance.m_location.m_prefab.Name ?? "").Trim();
        return prefabName.Length > 0 ? prefabName : null;
    }

    private static void EnsureLocationResolutionCacheState()
    {
        int zoneSystemId = ZoneSystem.instance != null ? ZoneSystem.instance.GetInstanceID() : 0;
        int locationInstanceCount = ZoneSystem.instance?.m_locationInstances.Count ?? 0;
        if (_lastLocationResolutionZoneSystemId == zoneSystemId &&
            _lastLocationResolutionLocationInstanceCount == locationInstanceCount)
        {
            return;
        }

        _locationResolutionEpoch++;
        CachedLocationResolutionsByObjectId.Clear();
        _lastLocationResolutionZoneSystemId = zoneSystemId;
        _lastLocationResolutionLocationInstanceCount = locationInstanceCount;
    }

    private static bool TryGetCachedLocationResolution(GameObject? gameObject, Vector3 position, out string? locationName)
    {
        locationName = null;
        if (gameObject == null)
        {
            return false;
        }

        if (!CachedLocationResolutionsByObjectId.TryGetValue(gameObject.GetInstanceID(), out CachedLocationResolution? cachedResolution))
        {
            return false;
        }

        Vector2i currentZone = ZoneSystem.GetZone(position);
        if (cachedResolution.Epoch != _locationResolutionEpoch || cachedResolution.Zone != currentZone)
        {
            return false;
        }

        if (cachedResolution.Mode == CachedLocationResolutionMode.Spatial)
        {
            Vector3 delta = position - cachedResolution.Position;
            float distanceSqr = (delta.x * delta.x) + (delta.z * delta.z);
            if (distanceSqr > SpatialResolutionReuseDistanceSqr)
            {
                return false;
            }
        }

        locationName = cachedResolution.HasLocationName ? cachedResolution.LocationName : null;
        return true;
    }

    private static void CacheResolvedLocationName(GameObject? gameObject, Vector3 position, string? locationName, CachedLocationResolutionMode mode)
    {
        if (gameObject == null)
        {
            return;
        }

        if (CachedLocationResolutionsByObjectId.Count >= MaxCachedLocationResolutions)
        {
            CachedLocationResolutionsByObjectId.Clear();
            _locationResolutionEpoch++;
        }

        CachedLocationResolutionsByObjectId[gameObject.GetInstanceID()] = new CachedLocationResolution
        {
            Epoch = _locationResolutionEpoch,
            Zone = ZoneSystem.GetZone(position),
            Position = position,
            LocationName = (locationName ?? "").Trim(),
            HasLocationName = !string.IsNullOrWhiteSpace(locationName),
            Mode = mode
        };
    }

    private static string? GetLocationName(Location location)
    {
        if (LocationManager.TryResolveRuntimeLocationPrefabName(location, out string resolvedPrefabName) &&
            !string.IsNullOrWhiteSpace(resolvedPrefabName))
        {
            return resolvedPrefabName;
        }

        if (ZoneSystem.instance != null)
        {
            Vector2i zone = ZoneSystem.GetZone(location.transform.position);
            if (ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance locationInstance))
            {
                string prefabName = (locationInstance.m_location.m_prefab.Name ?? "").Trim();
                if (prefabName.Length > 0)
                {
                    return prefabName;
                }
            }
        }

        return TrimCloneSuffix(location.gameObject.name);
    }

    private static string TrimCloneSuffix(string name)
    {
        const string cloneSuffix = "(Clone)";
        return name.EndsWith(cloneSuffix, StringComparison.Ordinal)
            ? name.Substring(0, name.Length - cloneSuffix.Length)
            : name;
    }

}
