using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// Exercise the built mod's managed contracts without starting Unity or changing a game profile.
// Reflection keeps test-only access out of the shipped assembly.
internal static partial class Program
{
    private static readonly BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static int _checks;

    private static readonly (string Domain, string Type, string Json)[] Fixtures =
    {
        ("Object", "PrefabConfigurationEntry", """
        {"Prefab":"TestObject","RuleId":"object-rule","SourcePath":"local.yml","SourceLine":12,
         "Conditions":{"Biomes":["Meadows"],"ResolvedBiomeMask":1,"Level":{"Min":1,"Max":3}},
         "Container":{"Rolls":{"Min":1,"Max":2},"DropChance":0.75,"Drops":[{"Item":"Wood","Stack":{"Min":2,"Max":5},"Weight":1}]},
         "PickableItem":{"Drop":{"Item":"Stone","Stack":2},"RandomDrops":[]}}
        """),
        ("Character", "CharacterDropPrefabEntry", """
        {"Prefab":"TestCreature","RuleId":"character-rule","SourcePath":"local.yml","SourceColumn":4,
         "Conditions":{"States":["Tamed"],"TimeOfDay":{"Values":["day"]}},
         "CharacterDrop":{"Drops":[{"Item":"Resin","Amount":{"Min":1,"Max":4},"Chance":0.5,"AmountLimit":3,"DropInStack":true}]}}
        """),
        ("Spawner", "SpawnerConfigurationEntry", """
        {"Prefab":"TestSpawner","RuleId":"spawner-rule","SourcePath":"local.yml","Locations":["Crypt"],
         "SpawnArea":{"MaxTotalSpawns":7,"Creatures":[{"Creature":"Skeleton","Weight":2,"Fields":{"health":"20"},"Objects":["Wood,0,0,0,1"]}]},
         "CreatureSpawner":{"Creature":"Skeleton","RespawnTimeMinutes":4,"TimeOfDay":{"Values":[]},"Fields":{}}}
        """),
        ("SpawnSystem", "CanonicalSpawnSystemEntry", """
        {"Prefab":"TestWorldSpawn","RuleId":"world-rule","SourcePath":"local.yml","ReferenceOwnerName":"A mod",
         "SpawnSystem":{"Name":"A row","SpawnInterval":12,"Biomes":["Meadows"],"ResolvedBiomeMask":1,
         "RequiredGlobalKey":"key","Fields":{"health":"40"},"Objects":["Wood,0,0,0,1"],"TimeOfDay":{"Values":["night"]}}}
        """),
        ("Event", "EventDefinition", """
        {"Event":"test_event","Settings":["true","30","60"],"Standalone":[10,20],"SpawnerDelay":2,
         "Messages":["start","end"],"ForceEnvironment":"","StartCommands":["command"],"EndCommands":[],
         "Conditions":{"RequiredGlobalKeys":["key"]},
         "Spawns":[{"Prefab":"Skeleton","SpawnSystem":{"SpawnInterval":5,"Fields":{"health":"60"},"Objects":[]}}]}
        """)
    };

    private static int Main(string[] args)
    {
        try
        {
            var options = ParseOptions(args);
            string assemblyPath = Required(options, "--assembly");
            string gamePath = Required(options, "--game-dir");
            string projectPath = options.TryGetValue("--project-dir", out string? configuredProject) ? configuredProject : Directory.GetCurrentDirectory();
            using var current = new ModContract(assemblyPath, gamePath, projectPath);
            Dictionary<string, string> actual = CheckTransportContracts(current);
            if (options.TryGetValue("--baseline", out string? baselinePath))
            {
                using var baseline = new ModContract(baselinePath, gamePath, projectPath);
                Dictionary<string, string> expected = CheckTransportContracts(baseline);
                foreach (KeyValuePair<string, string> pair in expected)
                    Check(actual[pair.Key] == pair.Value, $"wire/signature compatibility: {pair.Key}");
            }
            CheckPayloadLifetime(current);
            CheckNetworkContracts(current);
            CheckCharacterContracts(current);
            CheckEventCompatibilityContracts(current);
            Console.WriteLine($"PASS: {_checks} managed contract checks. Unity gameplay and network execution are not covered.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Dictionary<string, string> CheckTransportContracts(ModContract mod)
    {
        Dictionary<string, string> snapshots = new(StringComparer.Ordinal);
        Type network = mod.Type("NetworkPayloadSyncSupport");
        foreach ((string domain, string typeName, string json) in Fixtures)
        {
            Type entryType = mod.Type(typeName);
            object schema = Invoke(network, null, $"Create{domain}EntrySchema")!;
            var entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
            entries.Add(JsonSerializer.Deserialize(json, entryType, JsonOptions)!);
            entries.Add(Activator.CreateInstance(entryType)!);
            byte[] bytes = (byte[])Call(schema, "SerializeEntries", entries)!;
            object restored = Call(schema, "DeserializeEntries", bytes)!;
            Check(bytes.SequenceEqual((byte[])Call(schema, "SerializeEntries", restored)!), $"{domain}: wire roundtrip");
            var cloned = (IList)Call(schema, "CloneEntries", entries)!;
            AssertDetached(entries, cloned, new HashSet<object>(ReferenceEqualityComparer.Instance));
            Check(bytes.SequenceEqual((byte[])Call(schema, "SerializeEntries", cloned)!), $"{domain}: clone payload");
            string signature = (string)Call(schema, "ComputePayloadSignature", entries)!;
            Check(signature == (string)Call(schema, "ComputePayloadSignature", cloned)!, $"{domain}: clone signature");
            snapshots[domain + ":bytes"] = Hash(bytes);
            snapshots[domain + ":signature"] = signature;

            PropertyInfo? source = entryType.GetProperty("SourcePath");
            if (source != null)
            {
                Check((string?)source.GetValue(cloned[0]) == "local.yml", $"{domain}: clone retains provenance");
                Check(source.GetValue(((IList)restored)[0]) == null, $"{domain}: wire omits provenance");
            }
            byte[] invalidVersion = (byte[])bytes.Clone();
            invalidVersion[0] ^= 0x7f;
            ExpectFailure(() => Call(schema, "DeserializeEntries", invalidVersion), $"{domain}: invalid DTO version");
            ExpectFailure(() => Call(schema, "DeserializeEntries", bytes.Take(bytes.Length - 1).ToArray()), $"{domain}: truncated payload");
        }

        object characterSchema = Invoke(network, null, "CreateCharacterEntrySchema")!;
        Type characterType = mod.Type("CharacterDropPrefabEntry");
        byte[][] emptyShapes = new[] { "null", "[]" }.Select(shape =>
        {
            var entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(characterType))!;
            entries.Add(JsonSerializer.Deserialize("{\"Prefab\":\"Boar\",\"CharacterDrop\":{\"Drops\":" + shape + "}}", characterType, JsonOptions)!);
            object clone = Call(characterSchema, "CloneEntries", entries)!;
            byte[] bytes = (byte[])Call(characterSchema, "SerializeEntries", clone)!;
            Check(bytes.SequenceEqual((byte[])Call(characterSchema, "SerializeEntries", Call(characterSchema, "DeserializeEntries", bytes)!)!), $"Character drops {shape}: preserved roundtrip");
            return bytes;
        }).ToArray();
        Check(!emptyShapes[0].SequenceEqual(emptyShapes[1]), "Character: null and explicit empty remain distinct on wire");
        snapshots["Character:null"] = Hash(emptyShapes[0]);
        snapshots["Character:empty"] = Hash(emptyShapes[1]);
        return snapshots;
    }

    private static void CheckPayloadLifetime(ModContract mod)
    {
        Type support = mod.Type("SpawnSystemCustomDataSupport");
        Type spawnType = mod.LoadGameAssembly("assembly_valheim").GetType("SpawnSystem+SpawnData", throwOnError: true)!;
        object source = Activator.CreateInstance(spawnType)!;
        Type payloadType = support.GetNestedType("PreparedPayload", All)!;
        object payload = Activator.CreateInstance(payloadType, nonPublic: true)!;
        PropertyInfo objectsProperty = payloadType.GetProperty("CustomObjects")!;
        var objects = (IList)Activator.CreateInstance(objectsProperty.PropertyType)!;
        objects.Add(RuntimeHelpers.GetUninitializedObject(objectsProperty.PropertyType.GetGenericArguments()[0]));
        objectsProperty.SetValue(payload, objects);
        Invoke(support, null, "ApplyPreparedPayload", source, payload);
        object liveClone = Activator.CreateInstance(spawnType)!;
        Invoke(support, null, "CopyPreparedPayload", source, liveClone);
        Invoke(support, null, "ApplyPreparedPayload", source, null);
        Check(!(bool)Invoke(support, null, "HasPreparedPayload", source)!, "source payload can be removed independently");
        Check((bool)Invoke(support, null, "HasPreparedPayload", liveClone)!, "live clone retains its payload after source removal");
        WeakReference deadClone = CreateUnownedClone(support, spawnType, liveClone);
        for (int i = 0; i < 5 && deadClone.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Check(!deadClone.IsAlive, "unowned clone is not retained by payload metadata");
        Check((bool)Invoke(support, null, "HasPreparedPayload", liveClone)!, "owned clone remains valid after GC");
        GC.KeepAlive(liveClone);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnownedClone(Type support, Type spawnType, object source)
    {
        object clone = Activator.CreateInstance(spawnType)!;
        Invoke(support, null, "CopyPreparedPayload", source, clone);
        return new WeakReference(clone);
    }

    private static void AssertDetached(object? source, object? clone, HashSet<object> seen)
    {
        if (source == null || source is string || source.GetType().IsValueType)
        {
            // Required identifier fields canonicalize null to empty; optional strings are
            // checked separately by byte-for-byte payload equality below.
            Check(Equals(source, clone) || (source == null && clone is string { Length: 0 }), "clone preserves scalar");
            return;
        }
        Check(clone != null && !ReferenceEquals(source, clone), "clone owns mutable node");
        if (!seen.Add(source)) return;
        if (source is IDictionary dictionary)
        {
            foreach (DictionaryEntry pair in dictionary)
                AssertDetached(pair.Value, ((IDictionary)clone!)[pair.Key], seen);
        }
        else if (source is IList list)
        {
            Check(list.Count == ((IList)clone!).Count, "clone preserves list length");
            for (int i = 0; i < list.Count; i++) AssertDetached(list[i], ((IList)clone)[i], seen);
        }
        else
        {
            foreach (PropertyInfo property in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (property.GetIndexParameters().Length == 0 && property.CanRead)
                    AssertDetached(property.GetValue(source), property.GetValue(clone), seen);
        }
    }

    internal static object? Call(object instance, string method, params object?[] args) => Invoke(instance.GetType(), instance, method, args);
    internal static object? Invoke(Type type, object? instance, string method, params object?[] args)
    {
        MethodInfo target = type.GetMethods(All).Single(m => m.Name == method && !m.IsGenericMethod && m.GetParameters().Length == args.Length);
        return target.Invoke(instance, args);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        _checks++;
    }

    private static void ExpectFailure(Action action, string name)
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is IOException or InvalidDataException or ArgumentException)
        { _checks++; return; }
        throw new InvalidOperationException("FAIL: " + name);
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Options require values.");
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2) result.Add(args[i], args[i + 1]);
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? value) ? value : throw new ArgumentException($"Required: {name}. Usage: --assembly <merged DLL> --game-dir <Valheim> [--baseline <previous merged DLL>] [--project-dir <repo>]");

    private static string Hash(byte[] bytes)
    {
        using SHA256 hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }

    private sealed class ModContract : AssemblyLoadContext, IDisposable
    {
        private readonly string[] _searchPaths;
        private readonly Assembly _assembly;
        internal ModContract(string assemblyPath, string gamePath, string projectPath) : base(isCollectible: true)
        {
            _searchPaths = new[] { Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!,
                Path.Combine(projectPath, "Libs"), Path.Combine(gamePath, "BepInEx", "core"),
                Path.Combine(gamePath, "valheim_Data", "Managed", "publicized_assemblies"),
                Path.Combine(gamePath, "valheim_Data", "Managed") };
            _assembly = LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        }
        internal Type Type(string name) => _assembly.GetType("DropNSpawn." + name, throwOnError: true)!;
        internal Assembly LoadGameAssembly(string name) => LoadFromAssemblyName(new AssemblyName(name));
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "0Harmony") return typeof(HarmonyLib.AccessTools).Assembly;
            if (name.Name is null || name.Name is "mscorlib" or "netstandard" || name.Name.StartsWith("System", StringComparison.Ordinal)) return null;
            foreach (string directory in _searchPaths)
                foreach (string filename in new[] { name.Name + ".dll", name.Name + "_publicized.dll" })
                {
                    string path = Path.Combine(directory, filename);
                    if (File.Exists(path)) return LoadFromAssemblyPath(Path.GetFullPath(path));
                }
            return null;
        }
        public void Dispose() => Unload();
    }
}
