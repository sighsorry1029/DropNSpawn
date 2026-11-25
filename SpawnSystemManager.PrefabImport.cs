using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private const string ImportHeader = "# DropNSpawn: automatically imported normal world spawn rules.";
    private static readonly StringComparer ImportNameComparer = StringComparer.OrdinalIgnoreCase;

    private static void TryImportUnconfiguredSpawnPrefabs()
    {
        if (PluginBoundSettings.AutoImportUnconfiguredSpawnPrefabs?.Value != DropNSpawnPlugin.Toggle.On ||
            !ShouldApplyLocally() || !DropNSpawnPlugin.IsRuntimeServer() ||
            ZNetScene.instance == null || ObjectDB.instance == null) return;
        try
        {
            if (DropNSpawnPlugin.Instance?.ReloadCoordinator is { IsConfigEntryReloadSuppressed: true } reload)
            {
                reload.QueueRuleReload(DropNSpawnPlugin.ReloadDomain.SpawnSystem);
                return; // Do not write files against a partially reloaded cfg.
            }
            if (!BiomeResolutionSupport.IsExpandWorldDataReadyOrUnavailable()) return;
            // Never inspect an attached zone, an external reference projection, or interval-materialized children.
            var lists = GetZoneCtrlPrefabSpawnSystem()?.m_spawnLists;
            if (lists == null || !TryEnumerateReferenceSpawnData(lists, out var source)) return;
            var rows = source!.Where(row => row.m_prefab != null)
                .ToLookup(row => row.m_prefab.name, ImportNameComparer);
            string? message = ReconcilePrefabImport(DropNSpawnPlugin.YamlConfigDirectoryPath, EnumerateOverrideConfigurationPaths,
                rows.Select(group => group.Key), name => RenderImportGroup(name, rows[name]));
            if (message != null) DropNSpawnPlugin.DropNSpawnLogger.LogInfo(message);
        }
        catch (Exception ex)
        {
            // A failed import must not block ordinary domain loading.
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Spawn prefab import deferred: {ex.Message}");
        }
    }

    private static string? RenderImportGroup(string name, IEnumerable<SpawnSystem.SpawnData> rows)
    {
        var text = new StringBuilder();
        var originals = rows.ToList();
        foreach (var row in originals) AppendReferenceEntry(text, ConvertToReferenceEntry(row));
        string yaml = text.ToString();
        var parsed = ParseSpawnDocument(yaml, name, requireSingleDocument: true);
        if (parsed.Warnings.Count != 0 || parsed.Configuration.Count != originals.Count)
            throw new InvalidDataException($"Could not read back generated rules for {name}.");
        // Cold path only. Some native flags have no DNS equivalent; display formatting can also round floats.
        // Test the actual DNS read-back, not just whether a syntactically valid YAML row can be written.
        var fields = typeof(SpawnSystem.SpawnData).GetFields(BindingFlags.Public | BindingFlags.Instance);
        for (int i = 0; i < originals.Count; i++)
        {
            var entry = parsed.Configuration[i];
            NormalizeEntry(entry);
            var rebuilt = new SpawnSystem.SpawnData();
            string? difference = ApplyEntry(rebuilt, entry, name, applyCustomData: false)
                ? fields.FirstOrDefault(f => !MatchesImportField(f, originals[i], rebuilt))?.Name
                : "unresolved spawn definition";
            if (difference == null) continue;
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Did not import '{name}': source row {i + 1} cannot be represented unchanged ({difference}). No rows for this prefab were added.");
            return null;
        }
        return yaml;
    }

    private static bool MatchesImportField(FieldInfo field, SpawnSystem.SpawnData source, SpawnSystem.SpawnData rebuilt)
    {
        if (field.Name == nameof(SpawnSystem.SpawnData.m_foldout)) return true; // inspector UI
        if (field.Name == nameof(SpawnSystem.SpawnData.m_name) && string.IsNullOrEmpty(source.m_name))
            return rebuilt.m_name == source.m_prefab.name; // DNS's unnamed row convention
        object? a = field.GetValue(source), b = field.GetValue(rebuilt);
        if (field.FieldType == typeof(List<string>))
            return ((List<string>?)a ?? Enumerable.Empty<string>()).SequenceEqual((List<string>?)b ?? Enumerable.Empty<string>());
        if (field.FieldType == typeof(string)) return ((string?)a ?? "") == ((string?)b ?? "");
        return Equals(a, b);
    }

    // File decisions use raw active YAML, before validation removes unresolved rows or interval policies split them.
    private static string? ReconcilePrefabImport(string directory, Func<IEnumerable<string>> activePaths,
        IEnumerable<string> sourceNames, Func<string, string?> render)
    {
        var missing = new HashSet<string>(sourceNames, ImportNameComparer);
        if (missing.Count == 0) return null;
        if (missing.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim() != n || n.Contains('\n') || n.Contains('\r')))
            throw new InvalidDataException("Invalid prefab name in the world spawn catalog.");

        var paths = activePaths().ToArray();
        if (paths.Length == 0) return null; // Do not let a supplemental file preempt initial full-table generation.
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prefabKey = new YamlScalarNode("prefab");
        foreach (string path in paths)
        {
            string yaml = File.ReadAllText(path);
            inputs.Add(path, yaml);
            // Common no-write path: read only the top-level keys. Do not deserialize hundreds
            // of nested spawn definitions a second time just to find that every prefab is claimed.
            var rows = ReadSpawnSequence(yaml, requireSingleDocument: true);
            if (rows == null) continue;
            foreach (var row in rows.Children.OfType<YamlMappingNode>())
                if (row.Children.TryGetValue(prefabKey, out var node) && node is YamlScalarNode scalar && scalar.Value != null)
                    missing.Remove(scalar.Value.Trim());
            if (missing.Count == 0) return null;
        }

        string imported = Path.Combine(directory, PluginSettingsFacade.GetYamlDomainSupplementalPrefix("spawnsystem") + "imported.yml");
        string alternate = Path.ChangeExtension(imported, ".yaml");
        // Use the actual spelling of an existing file on case-sensitive dedicated servers.
        var destinations = paths.Where(p => ImportNameComparer.Equals(p, imported) || ImportNameComparer.Equals(p, alternate)).ToArray();
        if (destinations.Length > 1) throw new IOException($"Multiple import destinations exist in {directory}. Keep one imported file.");
        if (destinations.Length == 1) imported = destinations[0];
        var expected = new List<string>();
        foreach (var input in inputs)
        {
            // Fail closed on unreadable/skipped entries before changing any file.
            string[] names = ReadImportNames(input.Value, input.Key);
            missing.ExceptWith(names);
            if (StringComparer.OrdinalIgnoreCase.Equals(input.Key, imported)) expected.AddRange(names);
        }
        if (missing.Count == 0) return null;

        inputs.TryGetValue(imported, out string? previous);
        string prefix = string.IsNullOrWhiteSpace(previous) ? ImportHeader : previous!;
        if (!(prefix == ImportHeader || prefix.StartsWith(ImportHeader + "\n", StringComparison.Ordinal) ||
              prefix.StartsWith(ImportHeader + "\r\n", StringComparison.Ordinal)))
            throw new IOException($"Will not append to unmarked {imported}. Move your rules to another active SpawnSystem file.");
        if (expected.Count == 0 && ReadSpawnSequence(prefix, requireSingleDocument: true) is { } empty)
        {
            // A user may clear the generated list using []. Remove just its brackets, not comments.
            int start = checked((int)empty.Start.Index), end = start + 1;
            while (end < prefix.Length && char.IsWhiteSpace(prefix[end])) end++;
            if (start >= 0 && end < prefix.Length && prefix[start] == '[' && prefix[end] == ']')
                prefix = prefix.Remove(end, 1).Remove(start, 1);
        }
        var additions = new StringBuilder();
        int importedCount = 0;
        foreach (string name in missing.OrderBy(n => n, StringComparer.Ordinal))
        {
            string? group = render(name);
            if (group == null) continue;
            var names = ReadImportNames(group, imported);
            if (names.Length == 0 || names.Any(n => !ImportNameComparer.Equals(n, name)))
                throw new InvalidDataException($"Incomplete generated spawn rules for '{name}'.");
            expected.AddRange(names);
            additions.AppendLine(group);
            importedCount++;
        }
        if (importedCount == 0) return null;
        string output = prefix + Environment.NewLine + additions;
        if (!ReadImportNames(output, imported).SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException($"Could not safely append rows to {imported}. Use one block-style YAML list.");
        if (!new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase).SetEquals(activePaths()) ||
            inputs.Any(input => ReadImportText(input.Key) != input.Value))
            throw new IOException("Spawn override files changed during import; retry on reload.");
        WriteImportText(imported, previous, output);
        return $"Imported normal spawn rules for {importedCount} unconfigured prefab(s) into {imported}.";
    }

    private static string[] ReadImportNames(string yaml, string path)
    {
        var parsed = ParseSpawnDocument(yaml, path, requireSingleDocument: true);
        if (parsed.Warnings.Count != 0 || parsed.Configuration.Any(e => string.IsNullOrWhiteSpace(e.Prefab)))
            throw new InvalidDataException($"Cannot establish prefab ownership in {path}. Fix skipped or unnamed entries first.");
        return parsed.Configuration.Select(e => e.Prefab!.Trim()).ToArray();
    }

    private static string? ReadImportText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
    }

    private static void WriteImportText(string path, string? previous, string text)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            if (ReadImportText(path) != previous) throw new IOException($"Concurrent edit of {path}; retry on reload.");
            if (previous == null) File.Move(temp, path);
            else File.Replace(temp, path, null);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
