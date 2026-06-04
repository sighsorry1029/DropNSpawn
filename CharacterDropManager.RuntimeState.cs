using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class CharacterDropManager
{
    private sealed class CharacterConfigurationRuntimeState
    {
        public List<CharacterDropPrefabEntry> Configuration { get; set; } = new();
        public string ConfigurationSignature { get; set; } = "";
        public Dictionary<string, List<CharacterDropPrefabEntry>> ActiveEntriesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CurrentEntrySignaturesByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ConfiguredCharacterDropPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> PrefabsWithCharacterDropOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<BossTamedPressureDefinition> BossTamedPressureRules { get; } = new();

        public void Reset()
        {
            Configuration = new List<CharacterDropPrefabEntry>();
            ConfigurationSignature = "";
            ActiveEntriesByPrefab.Clear();
            CurrentEntrySignaturesByPrefab.Clear();
            ConfiguredCharacterDropPrefabs.Clear();
            PrefabsWithCharacterDropOverrides.Clear();
            BossTamedPressureRules.Clear();
        }
    }
}
