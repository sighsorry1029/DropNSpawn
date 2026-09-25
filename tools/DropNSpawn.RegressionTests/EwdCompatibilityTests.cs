using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

internal static partial class Program
{
    private static void CheckOptionalEwdContracts(ModContract mod)
    {
        Type compatibility = mod.Type("ExpandWorldDataCompatibility");
        Check((bool)compatibility.GetProperty("IsAvailable", All)!.GetValue(null)! == !mod.WithoutEwd, "optional EWD availability");
        var dependency = mod.Type("DropNSpawnPlugin").GetCustomAttributesData().Single(attribute =>
            attribute.AttributeType.Name == "BepInDependency" && (string)attribute.ConstructorArguments[0].Value! == "expand_world_data");
        Check(Convert.ToInt32(dependency.ConstructorArguments[1].Value) == 2, "EWD is a soft BepInEx dependency");

        // These are the real local/synced configuration acceptance paths. Keep wire
        // fixtures independent: the existing transport contract still carries EWD fields.
        foreach (string domain in new[] { "Spawner", "SpawnSystem", "Event" })
        {
            foreach ((string field, string value) in new[] { ("Data", "\"custom\""), ("Fields", "{\"health\":\"\"}"), ("Objects", "[\"Wood,0,0,0,1\"]") })
            {
                string spawn = "{\"" + field + "\":" + value + "}";
                string json = domain switch
                {
                    "Spawner" => "{\"Prefab\":\"fixture\",\"CreatureSpawner\":" + spawn + "}",
                    "SpawnSystem" => "{\"Prefab\":\"fixture\",\"SpawnSystem\":" + spawn + "}",
                    _ => "{\"Event\":\"fixture\",\"Spawns\":[{\"Prefab\":\"fixture\",\"SpawnSystem\":" + spawn + "}]}"
                };
                Accept(domain, json, !mod.WithoutEwd);
            }
            string empty = "{\"Data\":\" \",\"Fields\":{},\"Objects\":[],\"Faction\":\"ForestMonsters\"}";
            Accept(domain, domain switch
            {
                "Spawner" => "{\"Prefab\":\"fixture\",\"CreatureSpawner\":" + empty + "}",
                "SpawnSystem" => "{\"Prefab\":\"fixture\",\"SpawnSystem\":" + empty + "}",
                _ => "{\"Event\":\"fixture\",\"Spawns\":[{\"Prefab\":\"fixture\",\"SpawnSystem\":" + empty + "}]}"
            }, true);
        }
        Accept("Spawner", "{\"Prefab\":\"fixture\",\"SpawnArea\":{\"Creatures\":[{\"Creature\":\"Boar\",\"Data\":\"custom\"}]}}", !mod.WithoutEwd);
        Accept("Spawner", "{\"Prefab\":\"fixture\",\"Enabled\":false,\"CreatureSpawner\":{\"Data\":\"custom\"}}", true);
        Accept("SpawnSystem", "{\"Prefab\":\"fixture\",\"Enabled\":false,\"SpawnSystem\":{\"Data\":\"custom\"}}", true);
        Accept("Event", "{\"Event\":\"fixture\",\"Spawns\":[{\"Prefab\":\"Boar\",\"Enabled\":false,\"SpawnSystem\":{\"Data\":\"custom\"}}]}", true);
        foreach (string command in new[] { "StartCommands", "EndCommands" })
            Accept("Event", "{\"Event\":\"fixture\",\"" + command + "\":[\"command\"]}", !mod.WithoutEwd);

        if (!mod.WithoutEwd) return;
        Check(!mod.Assemblies.Any(assembly => assembly.GetName().Name == "ExpandWorldData"), "EWD absent from isolated load context");
        Type support = mod.Type("SpawnSystemCustomDataSupport");
        object spawnData = Activator.CreateInstance(mod.LoadGameAssembly("assembly_valheim").GetType("SpawnSystem+SpawnData", true)!)!;
        object entry = System.Text.Json.JsonSerializer.Deserialize("{\"Prefab\":\"Boar\",\"SpawnSystem\":{\"Faction\":\"ForestMonsters\"}}", mod.Type("CanonicalSpawnSystemEntry"), JsonOptions)!;
        object payload = Invoke(support, null, "BuildPreparedPayload", spawnData, entry, "standalone faction")!;
        Check((string)payload.GetType().GetProperty("StandaloneFaction")!.GetValue(payload)! == "ForestMonsters", "standalone faction is retained without EWD data");
        Check(payload.GetType().GetProperty("CustomData")!.GetValue(payload) == null, "standalone faction does not construct EWD data");
        object disabled = System.Text.Json.JsonSerializer.Deserialize("{\"Prefab\":\"Boar\",\"Enabled\":false,\"SpawnSystem\":{\"Data\":\"unused\"}}", mod.Type("CanonicalSpawnSystemEntry"), JsonOptions)!;
        Check(Invoke(support, null, "BuildPreparedPayload", spawnData, disabled, "disabled row") == null, "disabled row still applies without preparing unused EWD data");

        foreach ((string domain, string yaml) in new[]
        {
            ("Spawner", "- prefab: fixture\n  creatureSpawner:\n    data: custom\n"),
            ("SpawnSystem", "- prefab: fixture\n  spawnSystem:\n    fields:\n      health: 40\n"),
            ("Event", "- event: fixture\n  startCommands: [command]\n")
        })
        {
            Type manager = mod.Type(domain + "Manager");
            object runtime = manager.GetField("ConfigurationRuntime", All)!.GetValue(null)!;
            object state = Property(runtime, "LoadState")!;
            string previous = (string)Property(state, "LastLoadedPayload")!;
            string rejected = (string)Property(state, "LastRejectedPayload")!;
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dns-ewd-reject-" + Guid.NewGuid().ToString("N") + ".yml");
            try
            {
                SetProperty(state, "LastLoadedPayload", "previous-accepted-payload");
                System.IO.File.WriteAllText(path, yaml);
                var paths = new List<string> { path };
                object documents = Invoke(mod.Type("ConfigurationLoadSupport"), null, "ReadLocalYamlDocuments", paths)!;
                object parsed = Invoke(manager, null, "ParseLocalConfigurationDocuments", documents)!;
                Check(((IList)Property(parsed, "Errors")!).Count > 0, domain + ": real local parser reports missing EWD");
                // SpawnSystem's rejection logger computes a native scene signature;
                // that final reporting path cannot run in the managed-only host.
                if (domain != "SpawnSystem")
                {
                    Check(Call(runtime, "ReloadSourceOfTruth", paths)!.ToString() == "Rejected", domain + ": actual local reload rejects missing EWD");
                    Check((string)Property(state, "LastLoadedPayload")! == "previous-accepted-payload", domain + ": rejected reload preserves accepted payload");
                }
            }
            finally
            {
                SetProperty(state, "LastLoadedPayload", previous);
                SetProperty(state, "LastRejectedPayload", rejected);
                System.IO.File.Delete(path);
            }
        }

        void Accept(string domain, string json, bool expected)
        {
            string typeName = domain == "Event" ? "EventDefinition" : domain == "SpawnSystem" ? "CanonicalSpawnSystemEntry" : "SpawnerConfigurationEntry";
            Type entryType = mod.Type(typeName);
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
            list.Add(System.Text.Json.JsonSerializer.Deserialize(json, entryType, JsonOptions));
            try
            {
                Invoke(mod.Type(domain == "Event" ? "EventManager" : domain + "Manager"), null,
                    domain == "Event" ? "BuildSyncedConfigurationState" : "NormalizeOwnedConfigurationEntries", list, "optional-ewd.yml");
                Check(expected, domain + ": required EWD fields rejected");
            }
            catch (TargetInvocationException error) when (error.InnerException is System.IO.InvalidDataException dependencyError)
            {
                Check(!expected && dependencyError.Message.Contains("optional-ewd.yml") && dependencyError.Message.Contains("requires Expand World Data"), domain + ": actionable missing-dependency diagnostic");
            }
        }
    }

    private static void CheckEwdCompatibilityContracts(ModContract mod)
    {
        if (mod.WithoutEwd) return;
        Assembly ewd = mod.LoadGameAssembly("ExpandWorldData");
        Type compat = mod.Type("ExpandWorldDataCompatibility");
        var plan = (IList)Invoke(compat, null, "GetPatchPlan", ewd)!;
        if (ewd.GetType("ExpandWorld.Spawn.Patcher") == null)
        {
            Check(plan.Count == 0, "pre-integration EWD needs no ownership patches");
            return;
        }
        Check(plan.Count == 18, "integrated EWD ownership/metadata contract resolves completely");
        int transpilers = 0;
        foreach (object entry in plan)
        {
            MethodInfo target = (MethodInfo)entry.GetType().GetField("Item1")!.GetValue(entry)!;
            var kind = (HarmonyPatchType)entry.GetType().GetField("Item2")!.GetValue(entry)!;
            string callback = (string)entry.GetType().GetField("Item3")!.GetValue(entry)!;
            if (kind == HarmonyPatchType.Transpiler)
            {
                var original = PatchProcessor.GetOriginalInstructions(target);
                int oldGates = original.Count(IsFeatureGate);
                var rewritten = ((IEnumerable<CodeInstruction>)Invoke(compat, null, callback, original)!).ToList();
                Check(oldGates > 0 && !rewritten.Any(IsFeatureGate), "EWD feature calls suppressed: " + target.DeclaringType!.FullName);
                Check(rewritten.Count == original.Count, "EWD rewrite preserves instruction/branch layout: " + target.Name);
                transpilers++;
            }
            else if (kind == HarmonyPatchType.Prefix)
                Check(!(bool)Invoke(compat, null, callback)!, "EWD owned entrypoint skipped: " + target.DeclaringType!.FullName + "." + target.Name);
        }
        Check(transpilers == 4, "EWD patchers, event API and drop conversion are covered");

        Type loader = ewd.GetType("ExpandWorld.Spawn.Loader", true)!;
        Type dtoType = ewd.GetType("ExpandWorld.Spawn.Data", true)!;
        var data = (IDictionary)loader.GetField("Data")!.GetValue(null)!;
        var objects = (IDictionary)loader.GetField("Objects")!.GetValue(null)!;
        compat.GetField("_data", All)!.SetValue(null, data);
        compat.GetField("_objects", All)!.SetValue(null, objects);
        FieldInfo[] extensions = new[] { "data", "fields", "objects", "faction" }.Select(n => dtoType.GetField(n)!).ToArray();
        compat.GetField("_sourceFields", All)!.SetValue(null, extensions);
        Type spawnType = mod.LoadGameAssembly("assembly_valheim").GetType("SpawnSystem+SpawnData", true)!;
        Type payloadSupport = mod.Type("SpawnSystemCustomDataSupport");
        object spawn = Activator.CreateInstance(spawnType)!;
        object source = Activator.CreateInstance(dtoType)!;
        object exported = Activator.CreateInstance(dtoType)!;
        object customData = Activator.CreateInstance(ewd.GetType("Data.DataEntry", true)!)!;
        var customObjects = (IList)Activator.CreateInstance(objects.GetType().GetGenericArguments()[1])!;
        customObjects.Add(RuntimeHelpers.GetUninitializedObject(ewd.GetType("ExpandWorldData.BlueprintObject", true)!));
        data.Add(spawn, customData);
        objects.Add(spawn, customObjects);
        dtoType.GetField("data")!.SetValue(source, "AltBiomeCustomData");
        dtoType.GetField("faction")!.SetValue(source, "AnimalsVeg");
        dtoType.GetField("fields")!.SetValue(source, new Dictionary<string, string> { ["health"] = "42" });
        dtoType.GetField("objects")!.SetValue(source, new[] { "Wood,0,0,0,1" });
        dtoType.GetField("drops")!.SetValue(source, "DoNotRestoreDrops");
        dtoType.GetField("spawnInterval")!.SetValue(exported, 12.5f);
        Invoke(compat, null, "CaptureSpawnData", source, spawn);
        Check(!data.Contains(spawn) && !objects.Contains(spawn), "AltBiome metadata leaves EWD strong-key dictionaries");
        Check((bool)Invoke(payloadSupport, null, "HasPreparedPayload", spawn)!, "AltBiome receives existing DNS runtime payload");
        object weakPayloads = payloadSupport.GetField("PayloadsBySpawnData", All)!.GetValue(null)!;
        Check(TryReadWeakValue(weakPayloads, spawn, out object? payload) && ReferenceEquals(Property(payload!, "CustomData"), customData) &&
            ReferenceEquals(Property(payload!, "CustomObjects"), customObjects), "EWD prepared data and objects are transferred without reinterpretation");
        Invoke(compat, null, "RestoreSpawnData", spawn, exported);
        foreach (FieldInfo field in extensions)
            Check(Equals(field.GetValue(exported), field.GetValue(source)), "AltBiome sync retains " + field.Name);
        Check((float)dtoType.GetField("spawnInterval")!.GetValue(exported)! == 12.5f, "AltBiome export retains current native interval");
        Check((string)dtoType.GetField("drops")!.GetValue(exported)! == "", "AltBiome export does not enable EWD loot");
        object nativeSpawn = Activator.CreateInstance(spawnType)!;
        object nativeExport = Activator.CreateInstance(dtoType)!;
        Invoke(compat, null, "RestoreSpawnData", nativeSpawn, nativeExport);
        Check(dtoType.GetField("data")!.GetValue(nativeExport) == null, "native AltBiome rows remain unchanged");
        WeakReference retired = CaptureRetiredAltBiome(compat, spawnType, dtoType);
        for (int i = 0; i < 5 && retired.IsAlive; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Check(!retired.IsAlive, "AltBiome reload metadata does not retain discarded rows");
        Check((bool)Invoke(payloadSupport, null, "HasPreparedPayload", spawn)!, "live old-heightmap rows survive other row retirement");
        GC.KeepAlive(spawn);
        Console.WriteLine("EWD compatibility input: " + ewd.Location);

        static bool IsFeatureGate(CodeInstruction instruction) => instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
            method.DeclaringType?.FullName == "ExpandWorldData.Configuration" && method.Name is "get_DataSpawns" or "get_DataEvents" or "get_DataDrops";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureRetiredAltBiome(Type compat, Type spawnType, Type dtoType)
    {
        object retired = Activator.CreateInstance(spawnType)!;
        Invoke(compat, null, "CaptureSpawnData", Activator.CreateInstance(dtoType)!, retired);
        return new WeakReference(retired);
    }
}
