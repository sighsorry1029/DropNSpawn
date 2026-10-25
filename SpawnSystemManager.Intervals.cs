using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    // Candidate-only marker: the shared loader must defer, never commit unscaled rows.
    private sealed class WaitingForIntervalBiomes : List<CanonicalSpawnSystemEntry>
    {
        internal WaitingForIntervalBiomes(IEnumerable<CanonicalSpawnSystemEntry> entries) : base(entries) { }
    }

    private static string SpawnIntervalsPath => Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath,
        $"{PluginSettingsFacade.GetYamlDomainFilePrefix("spawnintervals")}.yml");

    private static bool IsSpawnIntervalsPath(string? path)
    {
        string name = Path.GetFileName(path ?? "");
        string prefix = PluginSettingsFacade.GetYamlDomainFilePrefix("spawnintervals");
        return name.Equals(prefix + ".yml", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(prefix + ".yaml", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateSpawnIntervalsPaths() =>
        PluginSettingsFacade.EnumerateSupplementalOverrideConfigurationPaths("*.y*ml", IsSpawnIntervalsPath);

    private static void EnsureSpawnIntervalsFileExists()
    {
        if (EnumerateSpawnIntervalsPaths().Any()) return;
        GeneratedArtifactWriter.WriteTextAlways(SpawnIntervalsPath,
            "# Normal DNS SpawnSystem intervals only; server-owned and live-reloaded.\n" +
            "# One biome name per key, including EWD custom biome names after registration.\n" +
            "# Omitted biomes use 1.0. Values must be finite and greater than zero.\n" +
            "# 0.5 halves intervals; 2.0 doubles them. Decimal seconds are preserved (minimum 0.01).\n" +
            "# Different multipliers split multi-biome rules internally; equal multipliers stay grouped.\n" +
            "# No event/dungeon/spawner/AltBiome-list effects. Keep only ONE spawnintervals file.\n" +
            "# Meadows: 0.5\n# BlackForest: 2.0\n# MyCustomBiome: 0.75\n{}\n",
            $"Created spawn interval policy at {SpawnIntervalsPath}.");
    }

    private static Dictionary<string, float> ParseSpawnIntervals(string yaml)
    {
        Dictionary<string, float> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(yaml)) return result;
        YamlStream stream = new();
        using StringReader reader = new(yaml);
        stream.Load(reader);
        if (stream.Documents.Count == 0) return result;
        if (stream.Documents.Count != 1)
            throw new YamlException("Use one biome-to-multiplier mapping, not multiple YAML documents.");
        YamlNode root = stream.Documents[0].RootNode;
        if (root is YamlScalarNode empty && string.IsNullOrEmpty(empty.Value)) return result;
        if (root is not YamlMappingNode mapping)
            throw new YamlException(root.Start, root.End, "Expected a biome mapping, for example 'Meadows: 0.5'.");
        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            string name = pair.Key is YamlScalarNode key ? (key.Value ?? "").Trim() : "";
            if (name.Length == 0 || name.Contains(",") ||
                long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
                name.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Land", StringComparison.OrdinalIgnoreCase))
                throw new YamlException(pair.Key.Start, pair.Key.End, "Use an individual biome name, not All/None/Land or a numeric/composite mask.");
            float? value = Deserializer.Deserialize<float?>(SerializeYamlNode(pair.Value));
            if (!value.HasValue || value.Value <= 0 || float.IsNaN(value.Value) || float.IsInfinity(value.Value))
                throw new YamlException(pair.Value.Start, pair.Value.End, $"Multiplier for '{name}' must be finite and greater than zero.");
            if (result.ContainsKey(name))
                throw new YamlException(pair.Key.Start, pair.Key.End, $"Duplicate biome '{name}'.");
            result.Add(name, value.Value);
        }
        return result;
    }

    private static Dictionary<Heightmap.Biome, float> ResolveSpawnIntervals(Dictionary<string, float> policy)
    {
        Dictionary<Heightmap.Biome, float> result = new();
        foreach (KeyValuePair<string, float> pair in policy)
        {
            if (!BiomeResolutionSupport.TryResolveBiomeToken(pair.Key, out Heightmap.Biome biome))
                throw new YamlException($"Unknown biome '{pair.Key}' after biome registration. Check its EWD name and server configuration.");
            uint bits = unchecked((uint)(int)biome);
            if (bits == 0 || (bits & (bits - 1)) != 0)
                throw new YamlException($"'{pair.Key}' is not an individual biome.");
            if (result.ContainsKey(biome))
                throw new YamlException($"More than one key refers to biome '{pair.Key}'. Use only one name per biome.");
            result.Add(biome, pair.Value);
        }
        return result;
    }

    private static void ApplyLocalSpawnIntervals(LocalLoadResult<CanonicalSpawnSystemEntry> result,
        List<ConfigurationLoadSupport.LocalYamlDocument> documents)
    {
        if (result.Errors.Count > 0) return;
        try
        {
            if (documents.Count > 1)
                throw new YamlException("Only one DNS_spawnintervals.yml (or .yaml) is allowed, including subfolders.");
            ConfigurationLoadSupport.LocalYamlDocument? document = documents.FirstOrDefault();
            if (document?.ReadError != null) throw new IOException(document.ReadError);
            Dictionary<string, float> policy = ParseSpawnIntervals(document?.Yaml ?? "");
            if (ShouldDeferForExpandWorldDataBiomeReady(result.Entries) ||
                (policy.Count > 0 && BiomeResolutionSupport.ShouldWaitForExpandWorldDataBiomeResolution(policy.Keys, null)))
            {
                result.Entries = new WaitingForIntervalBiomes(result.Entries);
                return;
            }
            result.Entries = MaterializeSpawnIntervals(result.Entries, ResolveSpawnIntervals(policy));
            // Normalize the aggregate once: originals and generated children may have identical content.
            foreach (CanonicalSpawnSystemEntry entry in result.Entries) entry.RuleId = "";
            NormalizeConfiguration(result.Entries);
            result.ParsedEntryCount = result.Entries.Count;
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Failed to apply spawn interval policy ({string.Join(", ", documents.Select(d => d.Path))}){FormatYamlExceptionLocation(ex)}. {ex.Message}");
        }
    }

    private static List<CanonicalSpawnSystemEntry> MaterializeSpawnIntervals(List<CanonicalSpawnSystemEntry> source,
        Dictionary<Heightmap.Biome, float> policy)
    {
        // Local candidates only. Clients receive these detached, effective rows through the existing transport.
        List<CanonicalSpawnSystemEntry> entries = NetworkPayloadSyncSupport.CloneEntries(Descriptor, source);
        if (policy.Count == 0 || policy.Values.All(value => value == 1f)) return entries;
        SpawnSystem.SpawnData defaults = new();
        List<CanonicalSpawnSystemEntry> result = new();
        foreach (CanonicalSpawnSystemEntry entry in entries)
        {
            SpawnSystemSpawnDefinition? spawn = entry.SpawnSystem;
            Heightmap.Biome mask = defaults.m_biome;
            if (spawn?.Biomes?.Count > 0 && !BiomeResolutionSupport.TryResolveBiomeMask(spawn.Biomes, out mask))
            {
                // Preserve the existing per-entry diagnostic/skip policy for invalid spawn rows.
                result.Add(entry);
                continue;
            }
            Dictionary<float, Heightmap.Biome> groups = new();
            uint bits = unchecked((uint)(int)mask);
            for (int i = 0; i < 32; i++)
            {
                uint bit = 1u << i;
                if ((bits & bit) == 0) continue;
                Heightmap.Biome biome = (Heightmap.Biome)unchecked((int)bit);
                float multiplier = policy.TryGetValue(biome, out float value) ? value : 1f;
                groups.TryGetValue(multiplier, out Heightmap.Biome groupMask);
                groups[multiplier] = groupMask | biome;
            }
            if (groups.Count == 0 || (groups.Count == 1 && groups.ContainsKey(1f)))
            {
                result.Add(entry);
                continue;
            }
            foreach (KeyValuePair<float, Heightmap.Biome> group in groups.OrderBy(pair => pair.Key))
            {
                CanonicalSpawnSystemEntry child = NetworkPayloadSyncSupport.CloneEntries(Descriptor, new List<CanonicalSpawnSystemEntry> { entry })[0];
                child.SpawnSystem ??= new SpawnSystemSpawnDefinition();
                float interval = (spawn?.SpawnInterval ?? defaults.m_spawnInterval) * group.Key;
                if (float.IsNaN(interval) || float.IsInfinity(interval))
                    throw new YamlException($"Non-finite interval for '{entry.Prefab}' at {entry.SourcePath}:{entry.SourceLine}.");
                child.SpawnSystem.SpawnInterval = Math.Max(0.01f, interval);
                if (groups.Count > 1)
                {
                    // Never emit All/Land for a child: resolving that shorthand again could
                    // widen its mask to include custom biomes assigned to a different group.
                    List<string> names = new();
                    uint groupBits = unchecked((uint)(int)group.Value);
                    for (int i = 0; i < 32; i++)
                        if ((groupBits & (1u << i)) != 0)
                            names.Add(BiomeResolutionSupport.GetBiomeDisplayName((Heightmap.Biome)unchecked((int)(1u << i))));
                    child.SpawnSystem.Biomes = names.OrderBy(name => name, StringComparer.Ordinal).ToList();
                    child.SpawnSystem.ResolvedBiomeMask = group.Value;
                    child.LowTierGlobalKeySource = (mask & LowTierBiomeGlobalKeySpawnSystemFilter) != 0;
                }
                result.Add(child);
            }
        }
        return result;
    }
}
