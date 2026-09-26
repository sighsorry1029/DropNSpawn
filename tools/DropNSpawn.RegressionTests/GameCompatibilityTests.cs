using System.Reflection;
using System.Collections;
using System.Text;
using System.Text.Json;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mono.Cecil;

internal static partial class Program
{
    private static void CheckConfigReloadContracts(ModContract mod)
    {
        Type toggle = mod.Type("DropNSpawnPlugin").GetNestedType("Toggle", All)!;
        Type reloadDomain = mod.Type("DropNSpawnPlugin").GetNestedType("ReloadDomain", All)!;
        Type configType = mod.LoadGameAssembly("BepInEx").GetType("BepInEx.Configuration.ConfigFile", true)!;
        string path = Path.Combine(Path.GetTempPath(), "dns-unsaved-config-" + Guid.NewGuid().ToString("N") + ".cfg");
        object config = Activator.CreateInstance(configType, new object?[] { path, false, null })!;
        SetProperty(config, "SaveOnConfigSet", false);
        MethodInfo bind = configType.GetMethods().Single(method => method.Name == "Bind" && method.IsGenericMethodDefinition &&
            method.GetParameters().Length == 4 && method.GetParameters()[0].ParameterType == typeof(string) &&
            method.GetParameters()[3].ParameterType == typeof(string)).MakeGenericMethod(toggle);
        string[] names = { "Object", "Character", "Spawner", "SpawnSystem", "Event" };
        object[] entries = names.Select((name, index) => bind.Invoke(config,
            new[] { "test", name, Enum.ToObject(toggle, index % 2), "" })!).ToArray();
        object coordinator = Activator.CreateInstance(mod.Type("PluginReloadCoordinator"), All, null,
            new object?[] { null }.Concat(entries).ToArray(), null)!;
        object initial = Call(coordinator, "CaptureDomainToggleState")!;
        Check(Changed(initial) == 0, "config reload with unchanged toggles selects no domains");
        for (int i = 0; i < entries.Length; i++)
        {
            Set(i, 1 - i % 2);
            Check(Changed(initial) == Mask(names[i]), "config reload selects only changed " + names[i]);
            Check(Convert.ToInt32(Call(coordinator, "GetReloadDomainForToggleSetting", entries[i])) == Mask(names[i]),
                "live setting sender still selects " + names[i]);
            Set(i, i % 2);
        }
        Set(0, 1);
        Set(3, 0);
        Check(Changed(initial) == (Mask("Object") | Mask("SpawnSystem")), "config reload unions independently changed domains");
        Set(0, 0);
        Set(3, 1);
        Check(Changed(initial) == 0, "toggle changes reverted before reload do not select a domain");
        Set(4, 2);
        object raw = Call(coordinator, "CaptureDomainToggleState")!;
        Set(4, 3);
        Check(Changed(raw) == Mask("Event"), "raw non-On/Off enum value changes still trigger reload");
        Check(Convert.ToInt32(Call(coordinator, "GetReloadDomainForToggleSetting", (object?)null)) == 0 &&
              Convert.ToInt32(Call(coordinator, "GetReloadDomainForToggleSetting", new object())) == 0,
            "unrelated setting senders select no domains");
        Check(!File.Exists(path), "config contract fixtures never write a profile");

        int Changed(object previous) => Convert.ToInt32(Call(coordinator, "GetChangedDomainToggles", previous));
        int Mask(string name) => Convert.ToInt32(Enum.Parse(reloadDomain, name));
        void Set(int index, int value) => SetProperty(entries[index], "Value", Enum.ToObject(toggle, value));
    }

    private static void CheckLocationReferenceContracts(ModContract mod, string? mwlManifestPath)
    {
        Type support = mod.Type("ReferenceRefreshSupport");
        Type scanType = support.GetNestedType("LocationReferenceScan", All)!;
        Type assetType = mod.LoadGameAssembly("SoftReferenceableAssets").GetType("SoftReferenceableAssets.AssetID", true)!;
        object firstId = Activator.CreateInstance(assetType, new object[] { 1u, 2u, 3u, 4u })!;
        object otherId = Activator.CreateInstance(assetType, new object[] { 5u, 6u, 7u, 8u })!;
        object ids = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(assetType))!;
        Call(ids, "Add", firstId);
        object scan = NewScan(ids);
        Check((bool)Call(scan, "ShouldSkip", firstId)!, "automatic scan excludes MWL asset IDs");
        Check(!(bool)Call(scan, "ShouldSkip", otherId)!, "automatic scan keeps unrelated assets");
        object deferred = NewScan(null);
        Check((bool)Call(deferred, "ShouldSkip", otherId)!, "missing manifest safely defers interior scanning");
        Check(!Equals(Property(scan, "Signature"), Property(deferred, "Signature")), "deferred and MWL-only scan caches differ");
        Check(Equals(Property(scan, "Signature"), Property(NewScan(ids), "Signature")), "scan signature is deterministic");
        Call(ids, "Add", otherId);
        Check(!Equals(Property(scan, "Signature"), Property(NewScan(ids), "Signature")), "changed manifest IDs invalidate scan cache");
        string header = (string)support.GetField("PartialLocationReferenceHeader", All)!.GetRawConstantValue()!;
        string content = (string)Call(scan, "Annotate", "- prefab: RegisteredPrefab\n")!;
        Check(content.StartsWith(header + Environment.NewLine) && content.Contains("- prefab: RegisteredPrefab"), "partial export labels scope without removing registered entries");

        string testPath = Path.Combine(Path.GetTempPath(), "dns-reference-" + Guid.NewGuid().ToString("N") + ".yml");
        Type lifecycle = mod.Type("ReferenceArtifactLifecycle");
        try
        {
            File.WriteAllText(testPath, "# Existing full reference\n- prefab: KeepMe\n");
            Check(!(bool)Invoke(lifecycle, null, "HasHeader", testPath, header)!, "full/unmarked references are not eligible for partial replacement");
            object?[] planArgs = { "test", testPath, "test-signature", null, header };
            Check(!(bool)Invoke(lifecycle, null, "TryPlanUpdate", planArgs)!, "restricted automatic update preserves full reference before cache access");
            Check(File.ReadAllText(testPath).Contains("KeepMe"), "preserved full reference is unchanged");
            File.WriteAllText(testPath, content);
            Check((bool)Invoke(lifecycle, null, "HasHeader", testPath, header)!, "partial references can be refreshed as partial");
            File.Delete(testPath);
            planArgs[3] = null;
            Check((bool)Invoke(lifecycle, null, "TryPlanUpdate", planArgs)!, "missing references can be created with restricted scanning");
        }
        finally
        {
            File.Delete(testPath);
        }

        Type objectManager = mod.Type("ObjectDropManager");
        Type bucketType = objectManager.GetNestedType("LocationReferenceBucket", All)!;
        object bucket = Activator.CreateInstance(bucketType, nonPublic: true)!;
        Type entryType = mod.Type("PrefabReferenceEntry");
        object first = JsonSerializer.Deserialize("{\"Prefab\":\"Chest\",\"Container\":{\"DropChance\":0.5}}", entryType, JsonOptions)!;
        object same = JsonSerializer.Deserialize("{\"Prefab\":\"Chest\",\"Container\":{\"DropChance\":0.5}}", entryType, JsonOptions)!;
        object different = JsonSerializer.Deserialize("{\"Prefab\":\"Chest\",\"Container\":{\"DropChance\":0.75}}", entryType, JsonOptions)!;
        Check(Property(bucket, "UniqueReferenceEntry") == null, "empty location bucket has no supplemental defaults");
        Call(bucket, "AddReferenceEntry", first);
        Call(bucket, "AddReferenceEntry", same);
        Check(ReferenceEquals(Property(bucket, "UniqueReferenceEntry"), first), "identical location defaults retain only the first entry");
        Call(bucket, "AddReferenceEntry", different);
        Check(Property(bucket, "UniqueReferenceEntry") == null && bucketType.GetField("_signature", All)!.GetValue(bucket) == null,
            "conflicting defaults release both the entry and YAML signature");
        Call(bucket, "AddReferenceEntry", first);
        Call(bucket, "AddReferenceEntry", (object?)null); // Must return before serialization once ambiguous.
        Check(Property(bucket, "UniqueReferenceEntry") == null, "ambiguous bucket never becomes unique again or serializes later entries");

        using var module = ModuleDefinition.ReadModule(mod.Assembly.Location);
        TypeDefinition iterator = module.GetType("DropNSpawn.ReferenceRefreshSupport").NestedTypes
            .Single(type => type.Name.StartsWith("<EnumerateLocationRootPrefabs>"));
        MethodDefinition moveNext = iterator.Methods.Single(method => method.Name == "MoveNext");
        var calls = moveNext.Body.Instructions.Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).Name).ToList();
        Check(calls.IndexOf("ShouldSkip") >= 0 && calls.IndexOf("ShouldSkip") < calls.IndexOf("Load"), "MWL exclusion occurs before asset loading");
        MethodDefinition releaseFinally = iterator.Methods.Single(method => method.HasBody && method.Body.Instructions.Any(i =>
            i.Operand is MethodReference m && m.Name == "Release" && m.DeclaringType.Name.StartsWith("SoftReference")));
        Check(releaseFinally.Name.Contains("Finally") && iterator.Methods.Any(method => method.Name.EndsWith("Dispose") &&
            method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == releaseFinally.Name)),
            "borrowed asset release is in the iterator finally/disposal path");
        foreach (string managerName in new[] { "ObjectDropManager", "SpawnerManager" })
        {
            TypeDefinition manager = module.GetType("DropNSpawn." + managerName);
            MethodDefinition auto = manager.Methods.Single(method => method.Name == (managerName == "ObjectDropManager" ? "EnsureReferenceArtifactsUpToDate" : "EnsureSpawnerReferenceConfigurationUpToDate"));
            Check(auto.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "CreateAutomaticLocationScan"), managerName + ": automatic export uses restricted policy");
            Check(!manager.NestedTypes.SelectMany(type => type.Methods).Concat(manager.Methods).Where(method => method.HasBody)
                .SelectMany(method => method.Body.Instructions).Any(i => i.Operand is MethodReference m && m.Name == "Load" && m.DeclaringType.Name.StartsWith("SoftReference")),
                managerName + ": no unbalanced private location loader remains");
        }

        if (mwlManifestPath != null)
        {
            Type manifestType = mod.LoadGameAssembly("SoftReferenceableAssets").GetType("SoftReferenceableAssets.AssetBundleManifest", true)!;
            object manifest = Invoke(manifestType, null, "DeserializeFromDisk", Path.GetFullPath(mwlManifestPath))!;
            var map = (IDictionary)manifestType.GetField("m_assetToLocationMap")!.GetValue(manifest)!;
            object realIds = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(assetType))!;
            foreach (object id in map.Keys) Call(realIds, "Add", id);
            object realScan = NewScan(realIds);
            Check(map.Count > 0, "provided MWL manifest is readable with the original game parser");
            foreach (object id in map.Keys) Check((bool)Call(realScan, "ShouldSkip", id)!, "provided MWL asset is excluded without loading its bundle");
            Check(!(bool)Call(realScan, "ShouldSkip", otherId)!, "provided MWL manifest does not exclude unrelated test asset");
            Console.WriteLine($"MWL manifest: {map.Count} asset IDs checked without loading bundles.");
        }

        object NewScan(object? excluded) => Activator.CreateInstance(scanType, All, null, new[] { excluded }, null)!;
    }

    // Static contract checks and accessor construction, not a Unity scene or PatchAll.
    private static void CheckGameContracts(ModContract mod)
    {
        using var resolver = new DefaultAssemblyResolver();
        foreach (string directory in mod.SearchPaths) resolver.AddSearchDirectory(directory);
        using var module = ModuleDefinition.ReadModule(mod.Assembly.Location, new ReaderParameters { AssemblyResolver = resolver });
        int references = 0;
        List<string> failures = new();
        foreach (MemberReference member in module.GetMemberReferences())
        {
            string scope = member.DeclaringType.Scope.Name;
            if (scope == "ExpandWorldData" && mod.WithoutEwd) continue;
            if (member.DeclaringType is ArrayType ||
                !(scope.StartsWith("assembly_", StringComparison.Ordinal) || scope is "ExpandWorldData" or "SoftReferenceableAssets" or "Splatform")) continue;
            references++;
            try
            {
                if (member is MethodReference method)
                {
                    MethodDefinition? resolved = method.Resolve();
                    if (resolved == null) failures.Add("Missing method: " + member.FullName);
                    else if (!resolved.IsPublic) failures.Add("Direct nonpublic method: " + member.FullName);
                }
                else if (member is FieldReference field)
                {
                    FieldDefinition? resolved = field.Resolve();
                    if (resolved == null || resolved.FieldType.FullName != field.FieldType.FullName)
                        failures.Add("Missing/changed field: " + member.FullName);
                    else if (!resolved.IsPublic) failures.Add("Direct nonpublic field: " + member.FullName);
                    else if (resolved.IsLiteral) failures.Add("Stale reference to constant field: " + member.FullName);
                }
            }
            catch (Exception ex) { failures.Add(member.FullName + ": " + ex.Message); }
        }

        // Harmony's own target resolver catches changed overloads and names. Validate
        // named argument/field injections as well, including private original fields.
        MethodInfo getOriginal = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchTools")!.GetMethod("GetOriginalMethod", All)!;
        int patchMethods = 0;
        int transpilers = 0;
        foreach (Type type in mod.Assembly.GetTypes().Where(t => t.Namespace is "DropNSpawn" or "LocalizationManager"))
        {
            List<HarmonyMethod> classInfo = type.GetCustomAttributes<HarmonyPatch>().Select(a => a.info).ToList();
            if (classInfo.Count == 0) continue;
            foreach (MethodInfo patch in type.GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.Name is "Prefix" or "Postfix" or "Finalizer" or "Transpiler"))
            {
                HarmonyMethod info = HarmonyMethod.Merge(classInfo.Concat(patch.GetCustomAttributes<HarmonyPatch>().Select(a => a.info)).ToList());
                if (info.declaringType == null || info.methodName == null)
                {
                    failures.Add("Unresolved dynamic patch: " + type.FullName);
                    continue;
                }
                info.methodType ??= MethodType.Normal;
                try
                {
                    var original = (MethodBase?)getOriginal.Invoke(null, new object[] { info });
                    if (original == null) { failures.Add("Missing patch target: " + type.FullName); continue; }
                    patchMethods++;
                    foreach (ParameterInfo parameter in patch.GetParameters())
                    {
                        string name = parameter.Name!;
                        Type valueType = Element(parameter.ParameterType);
                        if (name.StartsWith("___", StringComparison.Ordinal))
                        {
                            FieldInfo? field = AccessTools.Field(original.DeclaringType, name[3..]);
                            if (field == null || field.FieldType != valueType) failures.Add($"{type.Name}: field injection {name}");
                        }
                        else if (name == "__result")
                        {
                            if (original is not MethodInfo method || method.ReturnType != valueType) failures.Add($"{type.Name}: result injection");
                        }
                        else if (!name.StartsWith("__", StringComparison.Ordinal) && patch.Name != "Transpiler")
                        {
                            ParameterInfo? target = original.GetParameters().SingleOrDefault(p => p.Name == name);
                            if (target == null || Element(target.ParameterType) != valueType) failures.Add($"{type.Name}: argument injection {name}");
                        }
                    }
                    if (patch.Name == "Transpiler")
                    {
                        List<CodeInstruction> input = PatchProcessor.GetOriginalInstructions(original);
                        var output = ((IEnumerable<CodeInstruction>)patch.Invoke(null, new object[] { input })!).ToList();
                        if (type.Name == "CreatureSpawnerGroupSpawnPatch")
                        {
                            int gate = output.FindIndex(i => i.operand is MethodInfo m && m.Name == "IsCreatureSpawnerGroupCandidate");
                            int add = output.FindIndex(i => i.operand is MethodInfo m && m.DeclaringType?.Name == "List`1" && m.Name == "Add");
                            if (output.Count != input.Count + 3 || gate < 1 || add != gate + 5 ||
                                output[gate + 1].opcode != OpCodes.Brfalse || output[gate + 1].operand is not Label skip ||
                                !output.Skip(add + 1).Any(i => i.labels.Contains(skip)))
                                failures.Add("CreatureSpawner group gate must skip both candidate insertion and its weight");
                            transpilers++;
                            continue;
                        }
                        bool worldSpawn = type.Name == "SpawnSystemSpawnPatch";
                        string callbackName = worldSpawn ? "ApplyStandaloneFaction" : "RecordSpawnedObject";
                        int callback = output.FindIndex(i => i.operand is MethodInfo m && m.Name == callbackName);
                        if ((!worldSpawn && type.Name != "SpawnAreaSpawnOnePatch") || output.Count != input.Count + 3 || callback < 2 ||
                            output[callback - 2].opcode != OpCodes.Dup || output[callback - 1].opcode != (worldSpawn ? OpCodes.Ldarg_1 : OpCodes.Ldarg_0))
                            failures.Add(type.Name + ": spawn callback insertion changed or was not found");
                        transpilers++;
                    }
                }
                catch (Exception ex) { failures.Add(type.Name + ": " + ex.GetBaseException().Message); }
            }
            Type[] stateTypes = type.GetMethods(All | BindingFlags.DeclaredOnly).SelectMany(m => m.GetParameters())
                .Where(p => p.Name == "__state").Select(p => Element(p.ParameterType)).Distinct().ToArray();
            if (stateTypes.Length > 1) failures.Add(type.Name + ": inconsistent prefix/postfix/finalizer state");
        }

        foreach (string name in new[] { "assembly_valheim", "assembly_utils", "assembly_guiutils" })
            Check(Path.GetFullPath(mod.LoadGameAssembly(name).Location) == Path.Combine(mod.SearchPaths[0], name + ".dll"), "original runtime reference: " + name);
        foreach (string name in new[] { "EventManager+GameAccess", "CharacterDropKillerFilter", "RagdollSetupMonsterInstantLootDropPatch", "VneiCompatibility" })
            try { RuntimeHelpers.RunClassConstructor(mod.Type(name).TypeHandle); }
            catch (Exception ex) { failures.Add("Cached accessor " + name + ": " + ex.GetBaseException().Message); }

        Type biomeSupport = mod.Type("BiomeResolutionSupport");
        // Localizer's static constructor installs FejdStartup hooks and needs native
        // Unity services. Resolve its target contracts without starting that lifecycle.
        Type localizer = mod.Assembly.GetType("LocalizationManager.Localizer", true)!;
        Type localization = localizer.GetField("AddWord", All)!.FieldType.GenericTypeArguments[0];
        if (AccessTools.Method(localization, "AddWord", new[] { typeof(string), typeof(string) })?.ReturnType != typeof(void) ||
            AccessTools.Field(localization, "m_translations")?.FieldType != typeof(Dictionary<string, string>))
            failures.Add("Localization cached accessor contracts");
        Type startup = mod.LoadGameAssembly("assembly_valheim").GetType("FejdStartup", true)!;
        foreach (string target in new[] { "SetupGui", "Start" })
            if (AccessTools.Method(startup, target, Type.EmptyTypes) == null) failures.Add("Localization lifecycle hook: " + target);
        foreach (string fieldName in new[] { "ExpandWorldDataConfigSyncField", "ExpandWorldDataIsSourceOfTruthProperty", "ExpandWorldDataInitialSyncDoneProperty", "ExpandWorldDataTryGetBiomeMethod", "ExpandWorldDataTryGetDisplayNameMethod", "ExpandWorldDataBiomeToDisplayNameField" })
            if ((biomeSupport.GetField(fieldName, All)!.GetValue(null) == null) != mod.WithoutEwd) failures.Add("EWD reflection contract: " + fieldName);

        TypeDefinition manager = module.Types.Single(t => t.Name == "CharacterDropManager");
        foreach (string methodName in new[] { "SpawnStackedItem", "SpawnConfiguredLooseItem" })
        {
            MethodDefinition method = manager.Methods.Single(m => m.Name == methodName);
            var instructions = method.Body.Instructions;
            int create = instructions.ToList().FindIndex(i => i.Operand is MethodReference m && m.DeclaringType.Name == "ItemDrop" && m.Name == "OnCreateNew");
            int cheatIndex = method.Parameters.Single(p => p.Name == "cheated").Index;
            bool passesCheated = create > 0 && (instructions[create - 1].Operand is ParameterDefinition argument && argument.Name == "cheated" ||
                cheatIndex == 3 && instructions[create - 1].OpCode.Code == Mono.Cecil.Cil.Code.Ldarg_3);
            if (!passesCheated)
                failures.Add(methodName + ": must pass inherited cheat provenance to ItemDrop.OnCreateNew");
            int save = instructions.ToList().FindIndex(i => i.Operand is MethodReference m && m.Name == "SetStack");
            if (save >= 0 && save < create) failures.Add(methodName + ": provenance must precede SetStack persistence");
        }

        Console.WriteLine($"Original game/EWD member references: {references}; Harmony patch methods: {patchMethods}; transpilers executed: {transpilers}; failures: {failures.Count}.");
        foreach (string failure in failures) Console.Error.WriteLine(failure);
        Check(failures.Count == 0, "original game and EWD contracts");
    }

    private static Type Element(Type type) => type.IsByRef ? type.GetElementType()! : type;

    private static bool CheckKnownSchemaUpgrade(ModContract current, ModContract baseline, string domain)
    {
        object currentSchema = Invoke(current.Type("NetworkPayloadSyncSupport"), null, $"Create{domain}EntrySchema")!;
        object baselineSchema = Invoke(baseline.Type("NetworkPayloadSyncSupport"), null, $"Create{domain}EntrySchema")!;
        int oldVersion = (int)Property(baselineSchema, "DtoVersion")!;
        int newVersion = (int)Property(currentSchema, "DtoVersion")!;
        if (oldVersion == newVersion) return false;
        Check((domain == "SpawnSystem" && oldVersion == 3 && newVersion == 4) ||
              (domain == "Event" && oldVersion == 2 && newVersion == 3) ||
              (domain == "Spawner" && oldVersion == 7 && newVersion == 8), domain + ": explicit schema upgrade");
        var fixture = Fixtures.Single(f => f.Domain == domain);
        byte[] oldBytes = Serialize(baseline, baselineSchema);
        byte[] newBytes = Serialize(current, currentSchema);
        ExpectFailure(() => Call(currentSchema, "DeserializeEntries", oldBytes), domain + ": rejects older schema");
        ExpectFailure(() => Call(baselineSchema, "DeserializeEntries", newBytes), domain + ": older peer rejects newer schema");
        return true;

        byte[] Serialize(ModContract mod, object schema) => (byte[])Call(schema, "SerializeEntries",
            ListOf(mod.Type(fixture.Type), JsonSerializer.Deserialize(fixture.Json, mod.Type(fixture.Type), JsonOptions)!))!;
    }

    private static void CheckPersistentEventContracts(ModContract mod)
    {
        Type spawnType = mod.LoadGameAssembly("assembly_valheim").GetType("SpawnSystem+SpawnData", true)!;
        Type manager = mod.Type("SpawnSystemManager");
        Type definitionType = mod.Type("SpawnSystemSpawnDefinition");
        Type entryType = mod.Type("CanonicalSpawnSystemEntry");
        FieldInfo nativeField = spawnType.GetField("m_requiredPersistentEvent")!;
        object diagnostics = manager.GetField("InvalidEntryWarnings", All)!.GetValue(null)!;
        using var suppression = (IDisposable)Activator.CreateInstance(diagnostics.GetType().GetNestedType("SuppressionScope", All)!, new[] { diagnostics })!;
        var signatures = new HashSet<string>();
        foreach (string? requirement in new string?[] { null, "", "persistent_test" })
        {
            object definition = Activator.CreateInstance(definitionType)!;
            SetProperty(definition, "RequiredPersistentEvent", requirement);
            object entry = Activator.CreateInstance(entryType)!;
            SetProperty(entry, "SpawnSystem", definition);
            object source = Activator.CreateInstance(spawnType)!;
            nativeField.SetValue(source, "native_requirement");
            // No prefab lookup/native Unity calls: this deliberately reports an invalid
            // prefab while exercising the real managed field application branch.
            Invoke(manager, null, "ApplyEntry", source, entry, "test", false);
            Check((string)nativeField.GetValue(source)! == (requirement ?? "native_requirement"), "persistent event: omitted/clear/set apply");
            object nativeClone = Call(source, "Clone")!;
            Check(Equals(nativeField.GetValue(source), nativeField.GetValue(nativeClone)), "persistent event: native clone retains gate");
            object normalized = Activator.CreateInstance(definitionType)!;
            SetProperty(normalized, "RequiredPersistentEvent", requirement == null ? null : " " + requirement + " ");
            Invoke(manager, null, "NormalizeSpawnSystemConditionFields", normalized, "test");
            Check(Equals(Property(normalized, "RequiredPersistentEvent"), requirement), "persistent event: normalization preserves empty clear");

            foreach (string domain in new[] { "SpawnSystem", "Event" })
            {
                object outer = entry;
                if (domain == "Event")
                {
                    outer = Activator.CreateInstance(mod.Type("EventDefinition"))!;
                    object eventRow = Activator.CreateInstance(mod.Type("EventSpawnDefinition"))!;
                    SetProperty(eventRow, "SpawnSystem", definition);
                    SetProperty(outer, "Spawns", ListOf(eventRow.GetType(), eventRow));
                }
                object schema = Invoke(mod.Type("NetworkPayloadSyncSupport"), null, $"Create{domain}EntrySchema")!;
                IList entries = ListOf(outer.GetType(), outer);
                byte[] bytes = (byte[])Call(schema, "SerializeEntries", entries)!;
                IList restored = (IList)Call(schema, "DeserializeEntries", bytes)!;
                IList cloned = (IList)Call(schema, "CloneEntries", entries)!;
                foreach (IList result in new[] { restored, cloned })
                {
                    object row = domain == "Event" ? ((IList)Property(result[0]!, "Spawns")!)[0]! : result[0]!;
                    Check(Equals(Property(Property(row, "SpawnSystem")!, "RequiredPersistentEvent"), requirement), domain + ": persistent gate roundtrip/clone");
                }
                Check(signatures.Add(domain + ":" + (string)Call(schema, "ComputePayloadSignature", entries)!), domain + ": gate changes payload signature");
            }
        }

        object native = Activator.CreateInstance(spawnType)!;
        nativeField.SetValue(native, "persistent_test");
        object reference = Invoke(manager, null, "CreateReferenceEntryForExternalProjection", native, "Boar")!;
        Check((string)Property(Property(reference, "SpawnSystem")!, "RequiredPersistentEvent")! == "persistent_test", "persistent event: native reference export retains gate");
        var yaml = new StringBuilder();
        Invoke(manager, null, "AppendYamlSpawnSystemSpawnBlock", yaml, 0, reference, Activator.CreateInstance(spawnType)!, false);
        Check(yaml.ToString().Contains("requiredPersistentEvent:"), "persistent event: YAML writer includes gate");
    }
}
