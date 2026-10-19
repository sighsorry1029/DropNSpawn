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

    private static Type? EwdType(Assembly ewd, string name, bool required = false)
    {
        Type? type = ewd.GetType(name) ?? ewd.GetType(name
            .Replace("ExpandWorld.Spawn.", "ExpandWorldData.Spawn.")
            .Replace("ExpandWorld.Event.", "ExpandWorldData.Event.")
            .Replace("ExpandWorld.Drops.", "ExpandWorldData.Drop.")
            .Replace("ExpandWorldData.BlueprintObject", "ExpandWorldData.Data.BlueprintObject")
            .Replace("ExpandWorldData.AltBiomeManager", "ExpandWorldData.AltBiomes.AltBiomeManager"));
        return type ?? (required ? throw new TypeLoadException(name) : null);
    }

    private static void CheckEwdCompatibilityContracts(ModContract mod)
    {
        if (mod.WithoutEwd) return;
        Assembly ewd = mod.LoadGameAssembly("ExpandWorldData");
        Type compat = mod.Type("ExpandWorldDataCompatibility");
        using (var module = Mono.Cecil.ModuleDefinition.ReadModule(mod.Assembly.Location))
        {
            Check(!module.GetMemberReferences().OfType<Mono.Cecil.MethodReference>().Any(method =>
                method.DeclaringType.FullName == "Data.DataValue" && method.Name is "Simple" or "Constant"),
                "EWD constant factories have no version-specific direct MemberRefs");
            Check(!module.GetTypeReferences().Any(type => type.FullName is "ExpandWorldData.BlueprintObject" or "ExpandWorldData.Spawn"),
                "final DLL has no stale BlueprintObject/Spawn type references");
        }
        var plan = (IList)Invoke(compat, null, "GetPatchPlan", ewd)!;
        CheckEwdCapabilitySelection(mod, ewd, plan.Count);
        if (EwdType(ewd, "ExpandWorld.Spawn.Patcher") == null)
        {
            Check(plan.Count == 0, "pre-integration EWD needs no ownership patches");
            return;
        }
        bool syncedManagers = EwdType(ewd, "ExpandWorld.Spawn.SpawnManager") != null;
        bool requirementsApi = ewd.GetType("ExpandWorldData.Spawn.Requirements") != null;
        Check(plan.Count == (requirementsApi ? 22 : syncedManagers ? 25 : 18), "integrated EWD ownership/metadata contract resolves completely");
        int transpilers = 0;
        foreach (object entry in plan)
        {
            MethodInfo target = (MethodInfo)entry.GetType().GetField("Item1")!.GetValue(entry)!;
            var kind = (HarmonyPatchType)entry.GetType().GetField("Item2")!.GetValue(entry)!;
            string callback = (string)entry.GetType().GetField("Item3")!.GetValue(entry)!;
            if (kind == HarmonyPatchType.Transpiler)
            {
                var original = PatchProcessor.GetOriginalInstructions(target);
                if (callback == "RejectOwnedSet")
                {
                    var rejection = ((IEnumerable<CodeInstruction>)Invoke(compat, null, callback, original, target)!).ToList();
                    Check(rejection.Count == 2 && rejection[0].opcode == OpCodes.Ldc_I4 &&
                        (int)rejection[0].operand == Convert.ToInt32(Enum.Parse(target.ReturnType, "Failed")) && rejection[1].opcode == OpCodes.Ret,
                        "EWD enum Set reports failure without invoking loader");
                    continue;
                }
                int oldGates = original.Count(IsFeatureGate);
                var rewritten = ((IEnumerable<CodeInstruction>)Invoke(compat, null, callback, original.Select(i => new CodeInstruction(i)))!).ToList();
                if (callback == "DisableSpawnDrops")
                {
                    int reads = original.Count(IsDropRead);
                    Check(reads > 0 && !rewritten.Any(IsDropRead), "EWD drop conversion cannot read the configured drop table");
                    Check(rewritten.Count == original.Count + reads, "each drop value read is replaced by a stack-balanced pop/null");
                    Check(original.Where(i => i.operand is FieldInfo field && field.Name is "faction" or "fields")
                        .All(i => rewritten.Any(r => r.opcode == i.opcode && Equals(r.operand, i.operand))), "EWD faction/fields reads remain intact");
                }
                else
                {
                    Check(oldGates > 0 && !rewritten.Any(IsFeatureGate), "EWD feature calls suppressed: " + target.DeclaringType!.FullName);
                    Check(rewritten.Count == original.Count, "EWD rewrite preserves instruction/branch layout: " + target.Name);
                }
                transpilers++;
            }
            else if (callback == "DisableOwnedPatch")
            {
                foreach (string typeName in new[] { "ExpandWorld.Spawn.SpawnManager", "ExpandWorld.Event.EventManager", "ExpandWorld.Drops.DropManager", "ExpandWorldData.AltBiomeManager", "Data.DataHelper" })
                {
                    Type patchType = EwdType(ewd, typeName, true)!;
                    foreach (bool enabled in new[] { false, true })
                    {
                        object?[] args = { enabled, patchType, "Fixture" };
                        Invoke(compat, null, callback, args);
                        Check((bool)args[0]! == (enabled && !typeName.StartsWith("ExpandWorld.", StringComparison.Ordinal)), "registry ownership boundary: " + typeName + "/" + enabled);
                    }
                }
            }
            else if (kind == HarmonyPatchType.Prefix)
                Check(!(bool)Invoke(compat, null, callback)!, "EWD owned entrypoint skipped: " + target.DeclaringType!.FullName + "." + target.Name);
        }
        Check(transpilers == (syncedManagers ? 2 : 4), "EWD ownership, event API and drop conversion are covered");
        if (requirementsApi)
        {
            foreach ((string typeName, string method, bool allowed) in new[]
            {
                ("ExpandWorldData.Spawn.Requirements", "TranspileKeys", true),
                ("ExpandWorldData.Spawn.Requirements", "TranspileEnvironments", true),
                ("ExpandWorldData.Spawn.Requirements", "CheckLocations", true),
                ("ExpandWorldData.Spawn.Requirements", "CheckObjects", true),
                ("ExpandWorldData.Spawn.Requirements", "UnknownHook", false),
                ("ExpandWorldData.Spawn.GlobalKeys", "CheckRequirement", true),
                ("ExpandWorldData.Spawn.GlobalKeys", "Mutate", true),
                ("ExpandWorldData.Spawn.GlobalKeys", "Consume", false)
            })
            {
                object?[] args = { true, ewd.GetType(typeName, true), method };
                Invoke(compat, null, "DisableOwnedPatch", args);
                Check((bool)args[0]! == allowed, "narrow AltBiome callback ownership: " + method);
            }
        }

        Type loader = EwdType(ewd, "ExpandWorld.Spawn.Loader", true)!;
        Type dtoType = EwdType(ewd, "ExpandWorld.Spawn.Data", true)!;
        var data = (IDictionary)loader.GetField("Data")!.GetValue(null)!;
        var objects = (IDictionary)loader.GetField("Objects")!.GetValue(null)!;
        compat.GetField("_data", All)!.SetValue(null, loader.GetField("Data"));
        compat.GetField("_objects", All)!.SetValue(null, loader.GetField("Objects"));
        FieldInfo[] extensions = (FieldInfo[])Invoke(compat, null, "GetSourceFields", dtoType)!;
        compat.GetField("_sourceFields", All)!.SetValue(null, extensions);
        Check(extensions.Length == (requirementsApi ? 12 : 4), "all AltBiome extensions have an explicit export contract");
        Type spawnType = mod.LoadGameAssembly("assembly_valheim").GetType("SpawnSystem+SpawnData", true)!;
        Type payloadSupport = mod.Type("SpawnSystemCustomDataSupport");
        object spawn = Activator.CreateInstance(spawnType)!;
        object source = Activator.CreateInstance(dtoType)!;
        object exported = Activator.CreateInstance(dtoType)!;
        object? originalDrops = dtoType.GetField("drops")!.GetValue(exported);
        object customData = Activator.CreateInstance(ewd.GetType("Data.DataEntry", true)!)!;
        var customObjects = (IList)Activator.CreateInstance(objects.GetType().GetGenericArguments()[1])!;
        customObjects.Add(RuntimeHelpers.GetUninitializedObject(EwdType(ewd, "ExpandWorldData.BlueprintObject", true)!));
        data.Add(spawn, customData);
        objects.Add(spawn, customObjects);
        FieldInfo dataField = dtoType.GetField("data")!;
        dataField.SetValue(source, dataField.FieldType == typeof(string) ? "AltBiomeCustomData" :
            Activator.CreateInstance(dataField.FieldType, new object?[] { "AltBiomeCustomData", null }));
        dtoType.GetField("faction")!.SetValue(source, "AnimalsVeg");
        dtoType.GetField("fields")!.SetValue(source, new Dictionary<string, string> { ["health"] = "42" });
        dtoType.GetField("objects")!.SetValue(source, new[] { "Wood,0,0,0,1" });
        dtoType.GetField("drops")!.SetValue(source, "DoNotRestoreDrops");
        dtoType.GetField("spawnInterval")!.SetValue(exported, 12.5f);
        foreach (FieldInfo field in extensions.Where(field => field.Name.StartsWith("required") || field.Name.StartsWith("forbidden")))
            field.SetValue(source, RuntimeHelpers.GetUninitializedObject(field.FieldType));
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
        Check(Equals(dtoType.GetField("drops")!.GetValue(exported), originalDrops), "AltBiome export does not enable EWD loot");
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
        if (requirementsApi)
        {
            // Real transactional reload swaps the dictionaries, including rollback.
            Type transactionType = ewd.GetType("ExpandWorldData.Spawn.MetadataLoad", true)!;
            object replacement = Activator.CreateInstance(spawnType)!;
            using (var transaction = (IDisposable)Activator.CreateInstance(transactionType)!)
            {
                var replacementMap = (IDictionary)loader.GetField("Data")!.GetValue(null)!;
                Check(!ReferenceEquals(replacementMap, data), "EWD transaction installs a new map");
                replacementMap.Add(replacement, customData);
                Invoke(compat, null, "CaptureSpawnData", source, replacement);
                Check(!replacementMap.Contains(replacement) && TryReadWeakValue(weakPayloads, replacement, out var replacedPayload) &&
                    ReferenceEquals(Property(replacedPayload!, "CustomData"), customData), "capture uses current transaction map, not stale cache");
            }
            Check(ReferenceEquals(loader.GetField("Data")!.GetValue(null), data), "failed EWD reload retains its original metadata map");
            Check((bool)Invoke(payloadSupport, null, "HasPreparedPayload", spawn)!, "rollback leaves old AltBiome payload usable");
        }
        GC.KeepAlive(spawn);
        Console.WriteLine("EWD compatibility input: " + ewd.Location);

        static bool IsFeatureGate(CodeInstruction instruction) => instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
            method.DeclaringType?.FullName == "ExpandWorldData.Configuration" && method.Name is "get_DataSpawns" or "get_DataEvents" or "get_DataDrops";
        static bool IsDropRead(CodeInstruction instruction) => instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field &&
            field.DeclaringType?.FullName is "ExpandWorld.Spawn.Data" or "ExpandWorldData.Spawn.Data" && field.Name == "drops";
    }

    private static void CheckEwdCapabilitySelection(ModContract mod, Assembly ewd, int expectedPatches)
    {
        string directory = Path.Combine(Path.GetTempPath(), "dns-ewd-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = "";
        try
        {
            foreach (string version in new[] { "0.1", "1.74-preview", "999.0" })
            {
                string variantDirectory = Path.Combine(directory, version);
                Directory.CreateDirectory(variantDirectory);
                path = Path.Combine(variantDirectory, "ExpandWorldData.dll");
                using (var module = Mono.Cecil.ModuleDefinition.ReadModule(ewd.Location))
                {
                    var plugin = module.GetType("ExpandWorldData.EWD");
                    plugin.Fields.Single(f => f.Name == "VERSION").Constant = version;
                    plugin.CustomAttributes.Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin").ConstructorArguments[2] =
                        new Mono.Cecil.CustomAttributeArgument(module.TypeSystem.String, version);
                    module.Assembly.Name.Version = new Version(9, 8, 7, 6);
                    module.Write(path);
                }
                using var variant = new ModContract(mod.Assembly.Location, Path.GetDirectoryName(Path.GetDirectoryName(mod.SearchPaths[1]))!,
                    Directory.GetCurrentDirectory(), mod.SearchPaths[0], path, ewdFromMemory: true);
                var plan = (IList)Invoke(variant.Type("ExpandWorldDataCompatibility"), null, "GetPatchPlan", variant.LoadGameAssembly("ExpandWorldData"))!;
                Check(plan.Count == expectedPatches, "same EWD API accepted regardless of plugin/assembly version: " + version);
            }
            path = Path.Combine(directory, "ExpandWorldData.dll");
            foreach (string defect in new[] { "constructor", "chance", "spawn" })
            {
                using (var module = Mono.Cecil.ModuleDefinition.ReadModule(ewd.Location))
                {
                    var bp = module.GetType("ExpandWorldData.BlueprintObject") ?? module.GetType("ExpandWorldData.Data.BlueprintObject");
                    if (defect == "constructor") bp.Methods.Single(m => m.IsConstructor && m.Parameters.Count == 7).Parameters[0].ParameterType = module.TypeSystem.Int32;
                    else if (defect == "chance") bp.Fields.Single(f => f.Name == "Chance").FieldType = module.TypeSystem.Double;
                    else
                    {
                        var create = module.GetType("ExpandWorldData.Spawn") ?? module.GetType("ExpandWorldData.Create");
                        create.Methods.Single(m => m.Name == "BPO" && m.Parameters.Count == 7 && m.Parameters[1].ParameterType.FullName == "UnityEngine.Vector3").Name = "MissingBpo";
                    }
                    module.Write(path);
                }
                try
                {
                    using var incompatible = new ModContract(mod.Assembly.Location, Path.GetDirectoryName(Path.GetDirectoryName(mod.SearchPaths[1]))!,
                        Directory.GetCurrentDirectory(), mod.SearchPaths[0], path, ewdFromMemory: true);
                    throw new InvalidOperationException("EWD with broken object API was accepted");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is MissingMemberException)
                {
                    Check(true, "same version rejects changed object contract: " + defect);
                }
            }
            foreach (string argument in new[] { "System.String", "System.Int32" })
            foreach (string defect in new[] { "missing", "return", "instance" })
            {
                using (var module = Mono.Cecil.ModuleDefinition.ReadModule(ewd.Location))
                {
                    foreach (var factory in module.GetType("Data.DataValue").Methods.Where(method =>
                        method.Name is "Constant" or "Simple" && method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == argument))
                    {
                        if (defect == "missing") factory.Name = "Unavailable" + factory.Name;
                        else if (defect == "return") factory.ReturnType = module.TypeSystem.Object;
                        else { factory.IsStatic = false; factory.HasThis = true; }
                    }
                    module.Write(path);
                }
                try
                {
                    using var incompatible = new ModContract(mod.Assembly.Location, Path.GetDirectoryName(Path.GetDirectoryName(mod.SearchPaths[1]))!,
                        Directory.GetCurrentDirectory(), mod.SearchPaths[0], path, ewdFromMemory: true);
                    throw new InvalidOperationException("Invalid EWD constant factory was accepted");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is MissingMethodException)
                {
                    Check(ex.InnerException.Message.Contains(argument), "invalid constant factory rejected before payload processing: " + argument + "/" + defect);
                }
            }
            using (var module = Mono.Cecil.ModuleDefinition.ReadModule(ewd.Location))
            {
                var helper = module.GetType("Data.DataHelper");
                helper.Methods.Single(m => m.Name == "Get" && m.Parameters.Count == 2 && m.Parameters.All(p => p.ParameterType.FullName == "System.String")).Name = "MissingGet";
                module.Write(path);
            }
            try
            {
                using var incompatible = new ModContract(mod.Assembly.Location, Path.GetDirectoryName(Path.GetDirectoryName(mod.SearchPaths[1]))!,
                    Directory.GetCurrentDirectory(), mod.SearchPaths[0], path, ewdFromMemory: true);
                throw new InvalidOperationException("EWD with missing core API was accepted");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is MissingMethodException)
            {
                Check(true, "same version with missing core API is rejected explicitly");
            }
            if (expectedPatches > 0)
            {
                using (var module = Mono.Cecil.ModuleDefinition.ReadModule(ewd.Location))
                {
                    var patcher = module.GetType("ExpandWorld.Spawn.Patcher") ?? module.GetType("ExpandWorldData.Spawn.Patcher");
                    patcher.Name = "UnknownPatcher";
                    module.Write(path);
                }
                using (var partial = new ModContract(mod.Assembly.Location, Path.GetDirectoryName(Path.GetDirectoryName(mod.SearchPaths[1]))!,
                    Directory.GetCurrentDirectory(), mod.SearchPaths[0], path, ewdFromMemory: true))
                {
                    try
                    {
                        Invoke(partial.Type("ExpandWorldDataCompatibility"), null, "GetPatchPlan", partial.LoadGameAssembly("ExpandWorldData"));
                        throw new InvalidOperationException("Partial integration was mistaken for data-only EWD");
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is TypeLoadException)
                    {
                        Check(true, "missing integrated patcher is rejected rather than silently skipped");
                    }
                }
                using (var module = Mono.Cecil.ModuleDefinition.ReadModule(ewd.Location))
                {
                    var manager = module.GetType("ExpandWorld.Spawn.SpawnManager") ?? module.GetType("ExpandWorldData.Spawn.SpawnManager") ?? module.GetType("ExpandWorld.Spawn.Manager");
                    manager.Methods.Single(m => m.Name is "ReadConfigs" or "ReadConfig").Name = "MissingReadConfig";
                    module.Write(path);
                }
                using var changed = new ModContract(mod.Assembly.Location, Path.GetDirectoryName(Path.GetDirectoryName(mod.SearchPaths[1]))!,
                    Directory.GetCurrentDirectory(), mod.SearchPaths[0], path, ewdFromMemory: true);
                try
                {
                    Invoke(changed.Type("ExpandWorldDataCompatibility"), null, "GetPatchPlan", changed.LoadGameAssembly("ExpandWorldData"));
                    throw new InvalidOperationException("EWD with missing ownership API was accepted");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is MissingMethodException)
                {
                    Check(true, "same version with changed ownership API is rejected before installing patches");
                }
            }
        }
        finally
        {
            foreach (string variantDirectory in Directory.GetDirectories(directory))
            {
                File.Delete(Path.Combine(variantDirectory, "ExpandWorldData.dll"));
                Directory.Delete(variantDirectory);
            }
            File.Delete(Path.Combine(directory, "ExpandWorldData.dll"));
            Directory.Delete(directory);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureRetiredAltBiome(Type compat, Type spawnType, Type dtoType)
    {
        object retired = Activator.CreateInstance(spawnType)!;
        Invoke(compat, null, "CaptureSpawnData", Activator.CreateInstance(dtoType)!, retired);
        return new WeakReference(retired);
    }
}
