using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal sealed class LocationConfigurationEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public bool Enabled { get; set; } = true;

    [YamlMember(Order = 3)]
    public ConditionsDefinition? Conditions { get; set; }

    [YamlMember(Order = 4)]
    public LocationOfferingBowlDefinition? OfferingBowl { get; set; }

    [YamlMember(Order = 5)]
    public List<LocationItemStandDefinition>? ItemStands { get; set; }

    [YamlMember(Order = 6)]
    public List<LocationVegvisirDefinition>? Vegvisirs { get; set; }

    [YamlMember(Order = 7)]
    public List<LocationRunestoneDefinition>? Runestones { get; set; }
    [YamlMember(Order = 8)]
    public LocationRunestoneGlobalPinsDefinition? RunestoneGlobalPins { get; set; }

    [YamlIgnore]
    public string RuleId { get; set; } = "";

    [YamlIgnore]
    public string? SourcePath { get; set; }

    [YamlIgnore]
    public int SourceLine { get; set; }

    [YamlIgnore]
    public int SourceColumn { get; set; }
}

internal sealed class LocationReferenceEntry
{
    [YamlMember(Order = 1)]
    public string Prefab { get; set; } = "";

    [YamlMember(Order = 2)]
    public LocationOfferingBowlDefinition? OfferingBowl { get; set; }

    [YamlMember(Order = 3)]
    public List<LocationItemStandDefinition>? ItemStands { get; set; }

    [YamlMember(Order = 4)]
    public List<LocationVegvisirDefinition>? Vegvisirs { get; set; }

    [YamlMember(Order = 5)]
    public List<LocationRunestoneDefinition>? Runestones { get; set; }
}

internal sealed class LocationOfferingBowlDefinition
{
    [YamlMember(Order = 1)]
    public string? Name { get; set; }
    [YamlMember(Order = 2)]
    public string? UseItemText { get; set; }
    [YamlMember(Order = 3)]
    public string? UsedAltarText { get; set; }
    [YamlMember(Order = 4)]
    public string? CantOfferText { get; set; }
    [YamlMember(Order = 5)]
    public string? WrongOfferText { get; set; }
    [YamlMember(Order = 6)]
    public string? IncompleteOfferText { get; set; }
    [YamlMember(Order = 7)]
    public string? BossItem { get; set; }
    [YamlMember(Order = 8)]
    public int? BossItems { get; set; }
    [YamlMember(Order = 9)]
    public string? BossPrefab { get; set; }
    [YamlMember(Order = 10)]
    public string? ItemPrefab { get; set; }
    [YamlMember(Order = 11)]
    public string? SetGlobalKey { get; set; }
    [YamlMember(Order = 12)]
    public bool? RenderSpawnAreaGizmos { get; set; }
    [YamlMember(Order = 13)]
    public bool? AlertOnSpawn { get; set; }
    [YamlMember(Order = 14)]
    public float? SpawnBossDelay { get; set; }
    [YamlMember(Order = 15)]
    public FloatRangeDefinition? SpawnBossDistance { get; set; }

    [YamlIgnore]
    public float? SpawnBossMaxDistance { get; set; }
    [YamlIgnore]
    public float? SpawnBossMinDistance { get; set; }
    [YamlMember(Order = 18)]
    public float? SpawnBossMaxYDistance { get; set; }
    [YamlMember(Order = 19)]
    public int? GetSolidHeightMargin { get; set; }
    [YamlMember(Order = 20)]
    public bool? EnableSolidHeightCheck { get; set; }
    [YamlMember(Order = 21)]
    public float? SpawnPointClearingRadius { get; set; }
    [YamlMember(Order = 22)]
    public float? SpawnYOffset { get; set; }
    [YamlMember(Order = 23)]
    public bool? UseItemStands { get; set; }
    [YamlMember(Order = 24)]
    public string? ItemStandPrefix { get; set; }
    [YamlMember(Order = 25)]
    public float? ItemStandMaxRange { get; set; }
    [YamlMember(Order = 26)]
    public float? RespawnMinutes { get; set; }
    [YamlMember(Order = 27)]
    public string? Data { get; set; }
    [YamlMember(Order = 28)]
    public Dictionary<string, string>? Fields { get; set; }
    [YamlMember(Order = 29)]
    public List<string>? Objects { get; set; }
}

internal sealed class LocationVegvisirDefinition
{
    [YamlMember(Order = 1)]
    public string Path { get; set; } = "";
    [YamlMember(Order = 2)]
    public List<string>? ExpectedLocations { get; set; }
    [YamlMember(Order = 3)]
    public string? Name { get; set; }
    [YamlMember(Order = 4)]
    public string? UseText { get; set; }
    [YamlMember(Order = 5)]
    public string? HoverName { get; set; }
    [YamlMember(Order = 6)]
    public string? SetsGlobalKey { get; set; }
    [YamlMember(Order = 7)]
    public string? SetsPlayerKey { get; set; }
    [YamlMember(Order = 8)]
    public List<LocationVegvisirTargetDefinition>? Locations { get; set; }
}

internal sealed class LocationItemStandDefinition
{
    [YamlMember(Order = 1)]
    public string? Path { get; set; }
    [YamlMember(Order = 2)]
    public string? Name { get; set; }
    [YamlMember(Order = 3)]
    public bool? CanBeRemoved { get; set; }
    [YamlMember(Order = 4)]
    public bool? AutoAttach { get; set; }
    [YamlMember(Order = 5)]
    public string? OrientationType { get; set; }
    [YamlMember(Order = 6)]
    public List<string>? SupportedTypes { get; set; }
    [YamlMember(Order = 7)]
    public List<string>? SupportedItems { get; set; }
    [YamlMember(Order = 8)]
    public List<string>? UnsupportedItems { get; set; }
    [YamlMember(Order = 9)]
    public float? PowerActivationDelay { get; set; }
    [YamlMember(Order = 10)]
    public string? GuardianPower { get; set; }
}

internal sealed class LocationRunestoneDefinition
{
    [YamlMember(Order = 1)]
    public string Path { get; set; } = "";
    [YamlMember(Order = 2)]
    public string? ExpectedLocationName { get; set; }
    [YamlMember(Order = 3)]
    public string? ExpectedLabel { get; set; }
    [YamlMember(Order = 4)]
    public string? ExpectedTopic { get; set; }
    [YamlMember(Order = 5)]
    public string? Name { get; set; }
    [YamlMember(Order = 6)]
    public string? Topic { get; set; }
    [YamlMember(Order = 7)]
    public string? Label { get; set; }
    [YamlMember(Order = 8)]
    public string? Text { get; set; }
    [YamlMember(Order = 9)]
    public List<LocationRunestoneTextDefinition>? RandomTexts { get; set; }
    [YamlMember(Order = 10)]
    public string? LocationName { get; set; }
    [YamlMember(Order = 11)]
    public string? PinName { get; set; }
    [YamlMember(Order = 12)]
    public string? PinType { get; set; }
    [YamlMember(Order = 13)]
    public bool? ShowMap { get; set; }
    [YamlMember(Order = 14)]
    public float? Chance { get; set; }
}

internal sealed class LocationRunestoneTextDefinition
{
    [YamlMember(Order = 1)]
    public string? Topic { get; set; }
    [YamlMember(Order = 2)]
    public string? Label { get; set; }
    [YamlMember(Order = 3)]
    public string? Text { get; set; }
}

internal sealed class LocationRunestoneGlobalPinsDefinition
{
    [YamlMember(Order = 1)]
    public List<LocationRunestoneGlobalPinTargetDefinition>? TargetLocations { get; set; }
}

internal sealed class LocationRunestoneGlobalPinTargetDefinition
{
    [YamlMember(Order = 1)]
    public string LocationName { get; set; } = "";
    [YamlMember(Order = 2)]
    public float? Chance { get; set; }
    [YamlMember(Order = 3)]
    public List<string>? SourceBiomes { get; set; }
    [YamlMember(Order = 4)]
    public string? PinName { get; set; }
    [YamlMember(Order = 5)]
    public string? PinType { get; set; }
}

internal sealed class LocationVegvisirTargetDefinition
{
    [YamlMember(Order = 1)]
    public string LocationName { get; set; } = "";
    [YamlMember(Order = 2)]
    public string? PinName { get; set; }
    [YamlMember(Order = 3)]
    public string? PinType { get; set; }
    [YamlMember(Order = 4)]
    public bool? DiscoverAll { get; set; }
    [YamlMember(Order = 5)]
    public bool? ShowMap { get; set; }
    [YamlMember(Order = 6)]
    public float? Weight { get; set; }
}
