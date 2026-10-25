using System.Collections;
using System.Reflection;
using System.Text.Json;

internal static partial class Program
{
    private static void CheckSpawnIntervalPolicies(ModContract mod)
    {
        // EWD's biome initializer builds paths even in managed-only tests. Point
        // BepInEx at the test output, never at a user's live config/profile.
        Type paths = mod.LoadGameAssembly("BepInEx").GetType("BepInEx.Paths", true)!;
        Invoke(paths, null, "SetExecutablePath", Path.Combine(AppContext.BaseDirectory, "fixture.exe"),
            AppContext.BaseDirectory, mod.SearchPaths[0], null);
        Type manager = mod.Type("SpawnSystemManager"), entryType = mod.Type("CanonicalSpawnSystemEntry");
        string configRoot = (string)mod.Type("DropNSpawnPlugin").GetProperty("YamlConfigDirectoryPath", All)!.GetValue(null)!;
        Check((bool)Invoke(manager, null, "ShouldReloadForPath", Path.Combine(configRoot, "DNS_spawnintervals.yml"))! &&
            (bool)Invoke(manager, null, "ShouldReloadForPath", Path.Combine(configRoot, "sub", "DNS_spawnintervals.yaml"))! &&
            !(bool)Invoke(manager, null, "ShouldReloadForPath", Path.Combine(configRoot, "DNS_spawnintervals.sample.yml"))!,
            "existing SpawnSystem watcher accepts policy extensions/subfolders but excludes samples");
        Type timers = mod.Type("SpawnSystemTimers"), biomes = mod.Type("BiomeResolutionSupport");
        Type biome = mod.LoadGameAssembly("assembly_valheim").GetType("Heightmap+Biome", true)!;
        object Policy(string text) => Invoke(manager, null, "ParseSpawnIntervals", text)!;
        object Resolved(string text) => Invoke(manager, null, "ResolveSpawnIntervals", Policy(text))!;
        object Entry(string names = "Meadows,BlackForest", float seconds = 60) => JsonSerializer.Deserialize(JsonSerializer.Serialize(new
        {
            Prefab = "Wolf", SourcePath = "DNS_spawnsystem.yml", SourceLine = 4,
            SpawnSystem = new { Name = "preserve-altbiome-block-name", SpawnInterval = seconds,
                Biomes = names.Split(','), MaxSpawned = 3, SpawnChance = 25,
                Fields = new { health = "40" }, Objects = new[] { "Wood,0,0,0,1" }, Faction = "ForestMonsters" }
        }), entryType, JsonOptions)!;
        IList Expand(object entry, string policy) => (IList)Invoke(manager, null, "MaterializeSpawnIntervals", ListOf(entryType, entry), Resolved(policy))!;
        object Spawn(object entry) => Property(entry, "SpawnSystem")!;
        float Interval(object entry) => (float)Property(Spawn(entry), "SpawnInterval")!;
        string Names(object entry) => string.Join(",", (IEnumerable<string>)Property(Spawn(entry), "Biomes")!);
        Array Ids(object entries) => (Array)Invoke(timers, null, "CreateIds", entries)!;
        void Reject(Action action, string label)
        {
            try { action(); Check(false, label); }
            catch (TargetInvocationException) { Check(true, label); }
        }

        object original = Entry();
        IList split = Expand(original, "Meadows: 0.5\nBlackForest: 2");
        Check(split.Count == 2 && Interval(split[0]!) == 30 && Names(split[0]!) == "Meadows" &&
            Interval(split[1]!) == 120 && Names(split[1]!) == "BlackForest", "biome policy: disjoint 30/120-second rows cover exactly the source mask");
        Check(Interval(original) == 60 && Names(original) == "Meadows,BlackForest", "biome policy: original base row remains unchanged");
        foreach (object child in split)
        {
            Check((string)Property(Spawn(child), "Name")! == "preserve-altbiome-block-name" &&
                (int)Property(Spawn(child), "MaxSpawned")! == 3 && (float)Property(Spawn(child), "SpawnChance")! == 25,
                "biome policy: names, caps and chance are not multiplied or renamed");
            Check((string)Property(child, "SourcePath")! == "DNS_spawnsystem.yml" &&
                !ReferenceEquals(Property(Spawn(child), "Fields"), Property(Spawn(original), "Fields")) &&
                !ReferenceEquals(Property(Spawn(child), "Objects"), Property(Spawn(original), "Objects")) &&
                (string)Property(Spawn(child), "Faction")! == "ForestMonsters", "biome policy: source diagnostics and detached extension payloads survive");
            Check((bool)Property(child, "LowTierGlobalKeySource")!, "biome policy: original low-tier filter scope survives split");
        }
        IList grouped = Expand(original, "Meadows: 2\nBlackForest: 2");
        Check(grouped.Count == 1 && Interval(grouped[0]!) == 120 && Names(grouped[0]!) == Names(original), "equal multipliers keep the original biome representation");
        IList withDefault = Expand(original, "Meadows: 0.5");
        Check(withDefault.Count == 2 && Interval(withDefault[1]!) == 60, "unspecified biome multiplier is one");
        Check(Math.Abs(Interval(Expand(Entry("Meadows", 36), "Meadows: 0.1")[0]!) - 3.6f) < 0.00001,
            "decimal intervals are not rounded to whole seconds");
        Check(Interval(Expand(Entry("Meadows", 1), "Meadows: 0.00001")[0]!) == 0.01f, "effective interval retains the native DNS floor");
        foreach (string policy in new[] { "", "{}", "Meadows: 1\nBlackForest: 1", "Swamp: 2" })
            Check(Ids(Expand(original, policy)).Cast<object>().SequenceEqual(Ids(ListOf(entryType, original)).Cast<object>()),
                "no-effect policy preserves timer IDs: " + policy);
        Check(Ids(split).Cast<object>().SequenceEqual(Ids(Expand(original, "BlackForest: 2\nMeadows: 0.5")).Cast<object>()),
            "policy key order and repeated loading do not change timers or accumulate multiplication");
        Reject(() => Expand(Entry("Meadows", float.MaxValue), "Meadows: 2"), "overflow rejects policy application");
        foreach (string invalid in new[] { "Meadows: 0", "Meadows: -1", "Meadows: .nan", "Meadows: .inf", "Meadows: null",
                     "Meadows: 1\nmeadows: 2", "Meadows: 1\nMeadows: 2", "All: 1", "None: 1", "Land: 1", "1: 2",
                     "'Meadows, BlackForest': 2", "- Meadows: 1", "Meadows: 1\n---\nSwamp: 2", "Meadows: []" })
            Reject(() => Policy(invalid), "invalid biome policy rejected: " + invalid);
        Reject(() => Resolved("BlackForest: 1\nBlack Forest: 2"), "duplicate aliases reject rather than last-key-wins");
        Reject(() => Resolved("UnregisteredFixtureBiome: 1"), "unknown biome rejected after registration");

        // Supply registered names through the existing resolver, not a separate policy-only lookup.
        IDictionary lookup = (IDictionary)biomes.GetField("VanillaBiomeLookup", All)!.GetValue(null)!;
        const string custom = "fixturecustombiome";
        lookup.Add(custom, Enum.ToObject(biome, 1 << 20));
        try
        {
            object customEntry = Entry("FixtureCustomBiome,Meadows");
            IList customRows = Expand(customEntry, "FixtureCustomBiome: 2\nMeadows: 0.5");
            Check(customRows.Count == 2 && Interval(customRows[1]!) == 120,
                "registered custom biome name participates in the same policy resolver");
            IList allRows = Expand(Entry("All,FixtureCustomBiome"), "FixtureCustomBiome: 2");
            Check(allRows.Count == 2 && !Names(allRows[0]!).Split(',').Contains("All"),
                "vanilla-all child never re-emits All, which would widen to custom biomes during strict validation");
            Invoke(manager, null, "RefreshResolvedBiomeMasksForConfiguration", allRows);
            int left = Convert.ToInt32(Property(Spawn(allRows[0]!), "ResolvedBiomeMask"));
            int right = Convert.ToInt32(Property(Spawn(allRows[1]!), "ResolvedBiomeMask"));
            Check((left & right) == 0 && right == (1 << 20), "strict re-resolution preserves disjoint vanilla/custom child masks");
        }
        finally { lookup.Remove(custom); }

        if (!mod.WithoutEwd)
        {
            // Exercise the installed EWD's real name APIs with isolated in-memory
            // registry entries; this does not invoke its world/terrain loader.
            MethodInfo getter = (MethodInfo)biomes.GetField("ExpandWorldDataTryGetBiomeMethod", All)!.GetValue(null)!;
            IDictionary forward = (IDictionary)getter.DeclaringType!.GetField("NameToBiome", All)!.GetValue(null)!;
            IDictionary reverse = (IDictionary)getter.DeclaringType.GetField("BiomeToDisplayName", All)!.GetValue(null)!;
            object bitA = Enum.ToObject(biome, 1 << 25), bitB = Enum.ToObject(biome, 1 << 26);
            const string actualName = "PolicyRegistryFixture";
            forward.Add(actualName.ToLowerInvariant(), bitA); reverse.Add(bitA, actualName);
            try
            {
                IList first = Expand(Entry("All"), actualName + ": 2");
                Check(first.Count == 2 && Names(first[1]!) == actualName,
                    "actual EWD name/display APIs expand All into disjoint named custom and vanilla groups");
                reverse.Remove(bitA); reverse.Add(bitB, actualName); forward[actualName.ToLowerInvariant()] = bitB;
                IList moved = Expand(Entry("All"), actualName + ": 2");
                Check(Ids(first).Cast<object>().SequenceEqual(Ids(moved).Cast<object>()) &&
                    Convert.ToInt32(Property(Spawn(moved[1]!), "ResolvedBiomeMask")) == (1 << 26),
                    "actual EWD registry bit reallocation preserves child timer identities and refreshes masks");
            }
            finally { forward.Remove(actualName.ToLowerInvariant()); reverse.Remove(bitA); reverse.Remove(bitB); }
        }

        // The actual DTO round trip carries already-scaled rows and the original filter scope.
        object schema = Invoke(mod.Type("NetworkPayloadSyncSupport"), null, "CreateSpawnSystemEntrySchema")!;
        byte[] bytes = (byte[])Call(schema, "SerializeEntries", split)!;
        IList received = (IList)Call(schema, "DeserializeEntries", bytes)!;
        foreach (object row in received)
        {
            SetProperty(Spawn(row), "Fields", null);
            SetProperty(Spawn(row), "Objects", null);
        }
        IList clientRows = (IList)Invoke(manager, null, "NormalizeOwnedConfigurationEntries", received, "ServerSync:fixture")!;
        Check(Interval(clientRows[0]!) == 30 && Interval(clientRows[1]!) == 120 && (bool)Property(clientRows[1]!, "LowTierGlobalKeySource")!,
            "client materialization uses server intervals once and retains split filter scope");

        Type documentType = mod.Type("ConfigurationLoadSupport").GetNestedType("LocalYamlDocument", All)!;
        object ParseDocuments(params (string Path, string Yaml)[] files)
        {
            IList docs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(documentType))!;
            foreach (var file in files)
            {
                object doc = Activator.CreateInstance(documentType, true)!;
                SetProperty(doc, "Path", file.Path); SetProperty(doc, "Yaml", file.Yaml); docs.Add(doc);
            }
            return Invoke(manager, null, "ParseLocalConfigurationDocuments", docs)!;
        }
        string rowsYaml = "- prefab: Wolf\n  spawnSystem:\n    spawnInterval: 60\n    biomes: [Meadows, BlackForest]\n";
        object accepted = ParseDocuments(("DNS_spawnsystem.yml", rowsYaml), ("DNS_spawnintervals.yml", "Meadows: 0.5\nBlackForest: 2"));
        Check(((IList)Property(accepted, "Errors")!).Count == 0 && ((IList)Property(accepted, "Entries")!).Count == 2,
            "local reload separates policy mapping from spawn lists");
        object dup = ParseDocuments(("DNS_spawnsystem.yml", rowsYaml), ("DNS_spawnintervals.yml", "{}"), ("sub/DNS_spawnintervals.yaml", "{}"));
        Check(((IList)Property(dup, "Errors")!).Count == 1, "multiple policy files reject entire candidate");
        foreach (string yaml in new[] { "- spawnIntervalMultiplier: 2\n" + rowsYaml, rowsYaml + "- spawnIntervalMultiplier: 2\n", "- prefab: Wolf\n  spawnIntervalMultiplier: 2\n" })
        {
            object rejected = ParseDocuments(("DNS_spawnsystem.yml", yaml));
            Check(((IList)Property(rejected, "Errors")!).Count == 1 && ((IList)Property(rejected, "Errors")!)[0]!.ToString()!.Contains("no longer supported"),
                "removed per-file header rejects with actionable diagnostic in any position");
        }
        object duplicateRows = ParseDocuments(("DNS_spawnsystem.yml", rowsYaml + "- prefab: Wolf\n  spawnSystem:\n    spawnInterval: 60\n    biomes: [BlackForest]\n"),
            ("DNS_spawnintervals.yml", "Meadows: 0.5"));
        IList normalized = (IList)Property(duplicateRows, "Entries")!;
        Check(normalized.Count == 3 && normalized.Cast<object>().Select(e => (string)Property(e, "RuleId")!).Distinct().Count() == 3,
            "aggregate normalization keeps a derived row and an identical original row distinct on the wire");
    }
}
