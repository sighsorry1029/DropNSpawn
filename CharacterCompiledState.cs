using System;
using System.Collections.Generic;
using UnityEngine;

namespace DropNSpawn;

internal sealed class CompiledCharacterDropDefinition
{
    public string Fingerprint { get; set; } = "";
    public GameObject Prefab { get; set; } = null!;
    public int AmountMin { get; set; }
    public int AmountMax { get; set; }
    public float Chance { get; set; }
    public bool DontScale { get; set; }
    public bool LevelMultiplier { get; set; }
    public bool OnePerPlayer { get; set; }
    public int? AmountLimit { get; set; }
    public bool DropInStack { get; set; }
}

internal sealed class CompiledCharacterDropRule
{
    public CharacterDropPrefabEntry Entry { get; set; } = null!;
    public List<CompiledCharacterDropDefinition> Drops { get; } = new();
}

internal sealed class CachedCharacterRuntimeDropResolution
{
    public bool HasMatchedRuntimeRule { get; set; }
    public CompiledCharacterDropDefinition[] Definitions { get; set; } = Array.Empty<CompiledCharacterDropDefinition>();
    public List<CharacterDrop.Drop>? OverrideDrops { get; set; }
    public bool HasCustomDropHandling { get; set; }
}

internal sealed class CharacterRuntimeDropCacheState
{
    public bool IsCacheable { get; set; } = true;
    public bool UsesLevel { get; set; }
    public bool UsesFaction { get; set; }
    public bool UsesState { get; set; }
    public bool UsesTimeOfDay { get; set; }
    public bool UsesRequiredEnvironments { get; set; }
    public bool UsesInsidePlayerBase { get; set; }
    public string[] RequiredGlobalKeys { get; set; } = Array.Empty<string>();
    public string[] ForbiddenGlobalKeys { get; set; } = Array.Empty<string>();
    public Dictionary<int, CachedCharacterRuntimeDropResolution> ResolutionsBySignature { get; } = new();
}

/// <summary>
/// Immutable-ish compiled drop state for the character domain.
/// It owns compiled drop definitions and runtime drop caches, but not live object state or despawn policy state.
/// </summary>
internal sealed class CharacterCompiledState
{
    public static CharacterCompiledState Empty { get; } = new();

    public Dictionary<string, List<CompiledCharacterDropRule>> RuntimeRulesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CharacterRuntimeDropCacheState> RuntimeDropCachesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<CompiledCharacterDropDefinition>> StaticDropsByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<CharacterDrop.Drop>> StaticBuiltDropsByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
}
