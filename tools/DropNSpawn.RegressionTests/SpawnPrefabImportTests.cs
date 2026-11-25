using System.Collections;
using System.Reflection;

internal static partial class Program
{
    private static void CheckSpawnPrefabImports(ModContract mod)
    {
        var manager = mod.Type("SpawnSystemManager");
        var files = mod.Type("DomainConfigurationFileSupport");
        string dir = Directory.CreateTempSubdirectory("dns-spawn-import-").FullName;
        string primary = Path.Combine(dir, "DNS_spawnsystem.yml");
        string imported = Path.Combine(dir, "DNS_spawnsystem_imported.yml");
        string alternate = Path.Combine(dir, "DNS_spawnsystem_Imported.yaml");
        string ledger = Path.Combine(dir, ".spawnsystem-import-seen.v1.txt");
        string manual = Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "pack")).FullName, "DNS_spawnsystem_pack.yaml");
        Func<IEnumerable<string>> paths = () => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(p => (bool)Invoke(files, null, "IsOverrideConfigurationFileName", "spawnsystem", Path.GetFileName(p))!);
        const string day = "- prefab: Gjall\n  spawnSystem:\n    timeOfDay: [day]\n";
        const string night = "- prefab: Gjall\n  enabled: false\n  spawnSystem:\n    timeOfDay: [night]\n";
        const string modded = "- prefab: ModdedDragon\n";
        // The last row deliberately duplicates the first: import the upstream group, not distinct rows.
        const string gjall = day + night + day;
        Func<string, string?> render = n => n == "Unrepresentable" ? null : n == "Gjall" ? gjall : "- prefab: " + n + "\n";
        object? Import(params string[] names) => Invoke(manager, null, "ReconcilePrefabImport", dir, paths, names, render);
        void Reject(Action action, string label)
        {
            try { action(); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException or InvalidDataException ||
                                                      ex.InnerException?.GetType().Name == "YamlException")
            { Check(true, label); return; }
            Check(false, label);
        }
        try
        {
            File.WriteAllText(Path.Combine(dir, "DNS_spawnsystem.reference.yml"), day);
            Import("Gjall");
            Check(!File.Exists(ledger) && !File.Exists(imported), "reference alone cannot preempt initial full-table generation");
            File.WriteAllText(primary, "[]\n");
            File.WriteAllText(manual, "- prefab: wYvErN\n  enabled: false\n");
            Import("Gjall", "Wyvern", "ModdedDragon");
            string first = File.ReadAllText(imported);
            string header = first[..first.IndexOf('\n')].TrimEnd('\r');
            var parsed = (IList)Property(Invoke(manager, null, "ParseConfiguration", first, imported)!, "Configuration")!;
            Check(parsed.Cast<object>().Select(r => (string?)Property(r, "Prefab")).SequenceEqual(new[] { "Gjall", "Gjall", "Gjall", "ModdedDragon" }) &&
                  !(bool)Property(parsed[1]!, "Enabled")! && first.Contains(gjall) && !File.Exists(ledger),
                "first scan imports all vanilla and modded source rows in group order; disabled nested supplemental claims Wyvern");
            File.WriteAllText(ledger, "obsolete test-build history\n");
            var oldTime = new DateTime(2005, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(imported, oldTime);
            Check(Import("Gjall", "Wyvern", "ModdedDragon") == null && File.GetLastWriteTimeUtc(imported) == oldTime,
                "fully covered catalog does not rewrite or duplicate imported YAML");

            string partial = header + "\n# user comment\n" + day + modded;
            File.WriteAllText(imported, partial);
            Check(Import("Gjall", "ModdedDragon") == null && File.ReadAllText(imported) == partial,
                "one remaining Gjall rule suppresses the whole group, including missing night rule");
            string excluded = header + "\n# - prefab: Gjall\n" + modded;
            File.WriteAllText(imported, excluded);
            File.WriteAllText(manual, "- prefab: gJaLl\n  enabled: false\n");
            Check(Import("ModdedDragon") == null && Import("Gjall", "ModdedDragon") == null && File.ReadAllText(imported) == excluded,
                "single disabled manual row blocks reimport even after a mod is removed and reinstalled");
            File.WriteAllText(manual, "[]\n");
            Import("Gjall", "ModdedDragon");
            string restored = File.ReadAllText(imported);
            Check(restored.StartsWith(excluded, StringComparison.Ordinal) && restored.Contains(gjall) &&
                  !restored[(excluded.Length)..].Contains(modded) && paths().Count() == 3,
                "commenting out the last row restores all and only the missing group in the same file");
            Check(Import("Gjall") == null && File.ReadAllText(imported) == restored,
                "removing an upstream mod does not delete its existing YAML rules");

            File.WriteAllText(imported, header);
            Import("Gjall");
            Check(File.ReadAllText(imported).Contains(gjall), "bare generated header without a newline can accept new rows");
            File.Move(imported, alternate);
            File.WriteAllText(alternate, header + "\n# keep\n[] # empty list\n");
            Import("Gjall");
            string saved = File.ReadAllText(alternate);
            Check(!File.Exists(imported) && saved.Contains("# keep") && saved.Contains("# empty list") && saved.Contains(gjall),
                "reuse existing .yaml destination and keep comments when appending to an empty list");

            File.WriteAllText(manual, "- invalid_scalar_row\n");
            Reject(() => Import("Phoenix"), "a skipped manual row blocks import");
            File.WriteAllText(manual, "[]\n---\n- prefab: Phoenix\n");
            Reject(() => Import("Phoenix"), "a second YAML document is not mistaken for unowned content");
            Check(File.ReadAllText(alternate) == saved, "failed ownership scan preserves imported YAML");
            File.WriteAllText(manual, "[]\n");
            Import("Phoenix", "Unrepresentable");
            string updated = File.ReadAllText(alternate);
            Check(updated.StartsWith(saved, StringComparison.Ordinal) && updated.Contains("- prefab: Phoenix") && !updated.Contains("Unrepresentable"),
                "retry imports only representable groups without rewriting existing text");
            File.WriteAllText(imported, header + "\n");
            Reject(() => Import("Moth"), "ambiguous .yaml and .yml destinations defer without choosing one");
            File.Delete(imported);
            render = n =>
            {
                File.WriteAllText(manual, "- prefab: Moth\n  enabled: false\n");
                return "- prefab: " + n + "\n";
            };
            Reject(() => Import("Moth"), "a mid-import manual claim wins over a pending addition");
            Check(File.ReadAllText(alternate) == updated, "concurrent user edit leaves destination untouched");
            File.WriteAllText(manual, "[]\n");
            const string foreign = "# user-owned file with the reserved name\n[]\n";
            File.WriteAllText(alternate, foreign);
            Reject(() => Import("Moth"), "unmarked file at reserved destination is protected");
            Check(File.ReadAllText(alternate) == foreign && File.ReadAllText(ledger) == "obsolete test-build history\n",
                "no replacement of user file or legacy observation history");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
