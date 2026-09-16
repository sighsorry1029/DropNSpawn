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
                        int callback = output.FindIndex(i => i.operand is MethodInfo m && m.Name == "RecordSpawnedObject");
                        if (type.Name != "SpawnAreaSpawnOnePatch" || output.Count != input.Count + 3 || callback < 2 ||
                            output[callback - 2].opcode != OpCodes.Dup || output[callback - 1].opcode != OpCodes.Ldarg_0)
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
            if (biomeSupport.GetField(fieldName, All)!.GetValue(null) == null) failures.Add("EWD reflection contract: " + fieldName);

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

    private static bool CheckPersistentEventSchemaUpgrade(ModContract current, ModContract baseline, string domain)
    {
        object currentSchema = Invoke(current.Type("NetworkPayloadSyncSupport"), null, $"Create{domain}EntrySchema")!;
        object baselineSchema = Invoke(baseline.Type("NetworkPayloadSyncSupport"), null, $"Create{domain}EntrySchema")!;
        int oldVersion = (int)Property(baselineSchema, "DtoVersion")!;
        int newVersion = (int)Property(currentSchema, "DtoVersion")!;
        if (oldVersion == newVersion) return false;
        Check((domain == "SpawnSystem" && oldVersion == 3 && newVersion == 4) ||
              (domain == "Event" && oldVersion == 2 && newVersion == 3), domain + ": explicit persistent-event schema upgrade");
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
