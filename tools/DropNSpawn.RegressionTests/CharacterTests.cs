using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static partial class Program
{
    private static void CheckInstantLootBlacklistContracts(ModContract mod)
    {
        Type settings = mod.Type("CharacterDropGlobalConfig");
        Type facade = mod.Type("PluginSettingsFacade");
        string defaults = (string)settings.GetField("DefaultMonsterInstantLootDropBlacklist", All)!.GetRawConstantValue()!;
        Check(defaults == "Dragon, Hatchling", "instant loot blacklist defaults to creature prefab names");
        string[] fieldNames = { "_monsterInstantLootDropBlacklistEntry", "_monsterInstantLootDropBlacklistRaw", "_monsterInstantLootDropBlacklist" };
        FieldInfo[] fields = fieldNames.Select(name => settings.GetField(name, All)!).ToArray();
        object?[] previous = fields.Select(field => field.GetValue(null)).ToArray();
        Type configType = mod.LoadGameAssembly("BepInEx").GetType("BepInEx.Configuration.ConfigFile", true)!;
        string path = Path.Combine(Path.GetTempPath(), "dns-unsaved-instant-loot-" + Guid.NewGuid().ToString("N") + ".cfg");
        object config = Activator.CreateInstance(configType, new object?[] { path, false, null })!;
        SetProperty(config, "SaveOnConfigSet", false);
        MethodInfo bind = configType.GetMethods().Single(method => method.Name == "Bind" && method.IsGenericMethodDefinition &&
            method.GetParameters().Length == 4 && method.GetParameters()[0].ParameterType == typeof(string) &&
            method.GetParameters()[3].ParameterType == typeof(string)).MakeGenericMethod(typeof(string));
        object entry = bind.Invoke(config, new object[] { "2 - Character", "monster instant loot drop blacklist", defaults, "" })!;
        try
        {
            fields[0].SetValue(null, entry);
            Check(Excluded("Dragon") && Excluded("Hatchling"), "instant loot excludes both default creatures");
            Check(Excluded(" dragon ") && Excluded("HATCHLING"), "instant loot names ignore surrounding whitespace and case");
            foreach (string? name in new[] { null, "", " ", "Boar", "DragonEgg", "TrophyDragonQueen", "Dragon_ragdoll" })
                Check(!Excluded(name), "instant loot requires an exact creature name: " + (name ?? "null"));
            object cached = fields[2].GetValue(null)!;
            Excluded("Dragon");
            Check(ReferenceEquals(cached, fields[2].GetValue(null)), "unchanged instant loot setting reuses parsed names");

            // Change actual ConfigEntry values, as config reload and server sync do.
            SetProperty(entry, "Value", " Wolf ; Boar\r\nWOLF,, ;\n");
            Check(Excluded("Wolf") && Excluded("boar") && !Excluded("Dragon") && !Excluded("Hatchling"),
                "live blacklist edits replace previous names and support all list separators");
            Check(!ReferenceEquals(cached, fields[2].GetValue(null)) && ((HashSet<string>)fields[2].GetValue(null)!).Count == 2,
                "changed instant loot setting rebuilds and deduplicates its name cache");
            SetProperty(entry, "Value", "");
            Check(!Excluded("Wolf") && !Excluded("Boar") && !Excluded("Dragon") && !Excluded("Hatchling"),
                "empty instant loot blacklist removes all exclusions rather than restoring defaults");
            SetProperty(entry, "Value", defaults);
            Check(Excluded("Dragon") && Excluded("Hatchling") && !Excluded("Wolf"), "restoring defaults replaces the live blacklist");
            Check(!File.Exists(path), "instant loot fixtures never write a real config profile");
        }
        finally
        {
            for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, previous[i]);
        }

        bool Excluded(string? prefab) => (bool)Invoke(facade, null, "IsMonsterInstantLootDropBlacklisted", new object?[] { prefab })!;
    }

    private static void CheckCharacterContracts(ModContract mod)
    {
        Type manager = mod.Type("CharacterDropManager");
        Type entryType = mod.Type("CharacterDropPrefabEntry");
        Type definitionType = mod.Type("CompiledCharacterDropDefinition");
        Type ruleType = mod.Type("CompiledCharacterDropRule");
        FieldInfo runtimeField = manager.GetField("RuntimeState", All)!;
        object previousRuntime = runtimeField.GetValue(null)!;
        object runtime = Activator.CreateInstance(runtimeField.FieldType, nonPublic: true)!;
        var activeEntries = (IDictionary)Property(runtime, "ActiveEntriesByPrefab")!;

        // Use the production diagnostic scope so invalid rows need no BepInEx logger.
        object diagnostics = manager.GetField("InvalidEntryWarnings", All)!.GetValue(null)!;
        Type suppressionType = diagnostics.GetType().GetNestedType("SuppressionScope", All)!;
        using var suppression = (IDisposable)Activator.CreateInstance(suppressionType, new[] { diagnostics })!;
        try
        {
            runtimeField.SetValue(null, runtime);
            AddEntry("Absent", "{}");
            AddYamlEntry("Omitted", "characterDrop: {}");
            AddYamlEntry("Null", "characterDrop: { drops: null }");
            AddYamlEntry("Empty", "characterDrop: { drops: [] }");
            AddYamlEntry("InvalidOnly", "characterDrop: { drops: [{ item: ' ' }] }");
            AddEntry("ConditionalInvalidOnly", "{\"Conditions\":{\"Level\":{\"Min\":2}},\"CharacterDrop\":{\"Drops\":[{\"Item\":\" \"}]}}");
            object levelEmpty = AddEntry("LevelEmpty", "{\"Conditions\":{\"Level\":{\"Min\":2,\"Max\":5}},\"CharacterDrop\":{\"Drops\":[]}}");
            AddEntry("LocationEmpty", "{\"Conditions\":{\"Locations\":[\"Crypt\"]},\"CharacterDrop\":{\"Drops\":[]}}");
            AddEntry("DynamicEmpty", """
                {"Conditions":{"States":["Tamed"],"Factions":["Animals"],"TimeOfDay":{"Values":["day"]},
                 "RequiredEnvironments":["Clear"],"InsidePlayerBase":true,
                 "RequiredGlobalKeys":[" boss ","boss"],"ForbiddenGlobalKeys":[" blocked ","blocked"]},
                 "CharacterDrop":{"Drops":[]}}
                """);
            AddEntry("MixedEmpty", "{\"CharacterDrop\":{\"Drops\":[]}}");
            AddEntry("MixedEmpty", "{\"Conditions\":{\"Level\":{\"Min\":2}},\"CharacterDrop\":{\"Drops\":[]}}");
            AddEntry("MixedEmpty", "{\"CharacterDrop\":{\"Drops\":null}}");
            AddEntry("MixedEmpty", "{\"CharacterDrop\":{\"Drops\":[{\"Item\":\"\"}]}}");

            object compiled = Invoke(manager, null, "BuildCompiledState")!;
            var staticDefinitions = (IDictionary)Property(compiled, "StaticDropsByPrefab")!;
            var staticDrops = (IDictionary)Property(compiled, "StaticBuiltDropsByPrefab")!;
            var runtimeRules = (IDictionary)Property(compiled, "RuntimeRulesByPrefab")!;
            var caches = (IDictionary)Property(compiled, "RuntimeDropCachesByPrefab")!;
            foreach (string name in new[] { "Absent", "Omitted", "Null", "InvalidOnly", "ConditionalInvalidOnly" })
                Check(!staticDefinitions.Contains(name) && !staticDrops.Contains(name) && !runtimeRules.Contains(name),
                    $"Character {name}: no override after actual normalization and compilation");
            Check(staticDefinitions.Contains("Empty") && ((IList)staticDefinitions["Empty"]!).Count == 0 &&
                  ((IList)staticDrops["Empty"]!).Count == 0 && !runtimeRules.Contains("Empty"),
                "Character explicit empty: static override is retained");
            Check(!staticDrops.Contains("LevelEmpty") && ((IList)runtimeRules["LevelEmpty"]!).Count == 1 &&
                  ReferenceEquals(Property(((IList)runtimeRules["LevelEmpty"]!)[0]!, "Entry"), levelEmpty),
                "Character conditional empty: rule stays in runtime evaluation");
            Check((bool)Property(caches["LevelEmpty"]!, "UsesLevel")! && (bool)Property(caches["LevelEmpty"]!, "IsCacheable")!,
                "Character level empty: level participates in cache key");
            Check(!(bool)Property(caches["LocationEmpty"]!, "IsCacheable")!,
                "Character location empty: spatial rule is not cached by dynamic signature");
            foreach (string flag in new[] { "UsesState", "UsesFaction", "UsesTimeOfDay", "UsesRequiredEnvironments", "UsesInsidePlayerBase" })
                Check((bool)Property(caches["DynamicEmpty"]!, flag)!, $"Character dynamic empty: {flag}");
            Check(((string[])Property(caches["DynamicEmpty"]!, "RequiredGlobalKeys")!).SequenceEqual(new[] { "boss" }) &&
                  ((string[])Property(caches["DynamicEmpty"]!, "ForbiddenGlobalKeys")!).SequenceEqual(new[] { "blocked" }),
                "Character dynamic empty: cache keys are normalized and deduplicated");
            Check(((IList)staticDrops["MixedEmpty"]!).Count == 0 && ((IList)runtimeRules["MixedEmpty"]!).Count == 1,
                "Character mixed empty/null/invalid: static and conditional overrides coexist");

            CheckCharacterResolution(manager, definitionType, ruleType, entryType);
            CheckCharacterDropOwnership(manager, mod, compiled);
            CheckCharacterBaselineNormalization(manager, mod);
            CheckChanceOnlyDropContracts(manager, mod);
            CheckChanceOnlyChanceScaling(manager, mod);

            // Local input is still cloned; owned transport data is normalized in place.
            object local = JsonSerializer.Deserialize("{\"Prefab\":\" Creature \",\"Enabled\":false,\"RuleId\":\" local \",\"CharacterDrop\":{\"Drops\":[]}}", entryType, JsonOptions)!;
            IList localEntries = ListOf(entryType, local);
            var prepared = (IList)Invoke(manager, null, "PrepareLocalConfigurationEntries", localEntries, "test.yml", new List<string>())!;
            Check(!ReferenceEquals(prepared[0], local) && (string)Property(local, "Prefab")! == " Creature " &&
                  (string)Property(prepared[0]!, "Prefab")! == "Creature", "Character local input remains detached during normalization");
            object owned = Invoke(manager, null, "NormalizeOwnedConfigurationEntries", prepared, "test.yml")!;
            Check(ReferenceEquals(owned, prepared), "Character owned normalization does not clone again");
        }
        finally
        {
            runtimeField.SetValue(null, previousRuntime);
        }

        object AddEntry(string prefab, string json)
        {
            object entry = JsonSerializer.Deserialize(json, entryType, JsonOptions)!;
            return RegisterEntry(prefab, entry);
        }

        object AddYamlEntry(string prefab, string fields)
        {
            object parsed = Invoke(manager, null, "ParseConfiguration", $"- prefab: {prefab}\n  {fields}\n", "character-tests.yml")!;
            var entries = (IList)Property(parsed, "Configuration")!;
            Check(entries.Count == 1 && ((IList)Property(parsed, "Warnings")!).Count == 0, $"Character {prefab}: YAML shape parses");
            return RegisterEntry(prefab, entries[0]!);
        }

        object RegisterEntry(string prefab, object entry)
        {
            SetProperty(entry, "Prefab", prefab);
            SetProperty(entry, "RuleId", prefab + "-" + (activeEntries.Contains(prefab) ? ((IList)activeEntries[prefab]!).Count : 0));
            Invoke(manager, null, "NormalizeEntry", entry);
            if (!activeEntries.Contains(prefab)) activeEntries.Add(prefab, ListOf(entryType));
            ((IList)activeEntries[prefab]!).Add(entry);
            return entry;
        }
    }

    private static void CheckCharacterResolution(Type manager, Type definitionType, Type ruleType, Type entryType)
    {
        object entry = Activator.CreateInstance(entryType)!;
        object emptyRule = Activator.CreateInstance(ruleType)!;
        SetProperty(emptyRule, "Entry", entry);
        object resolution = Invoke(manager, null, "BuildRuntimeDropResolution", null, ListOf(ruleType, emptyRule), null)!;
        Check((bool)Property(resolution, "HasMatchedRuntimeRule")! && ((IList)Property(resolution, "OverrideDrops")!).Count == 0 &&
              !(bool)Property(resolution, "HasCustomDropHandling")!, "Character matching empty rule produces an empty override");

        object first = JsonSerializer.Deserialize("{\"Fingerprint\":\"a\",\"AmountMin\":2,\"AmountMax\":4,\"Chance\":0.5}", definitionType, JsonOptions)!;
        object duplicate = JsonSerializer.Deserialize("{\"Fingerprint\":\"a\",\"AmountMin\":99}", definitionType, JsonOptions)!;
        object second = JsonSerializer.Deserialize("{\"Fingerprint\":\"b\",\"AmountMin\":3,\"AmountMax\":6,\"DropInStack\":true}", definitionType, JsonOptions)!;
        object populatedRule = Activator.CreateInstance(ruleType)!;
        SetProperty(populatedRule, "Entry", entry);
        ((IList)Property(populatedRule, "Drops")!).Add(duplicate);
        ((IList)Property(populatedRule, "Drops")!).Add(second);
        IList staticRows = ListOf(definitionType, first);
        resolution = Invoke(manager, null, "BuildRuntimeDropResolution", staticRows, ListOf(ruleType, populatedRule, emptyRule), null)!;
        var definitions = (IList)Property(resolution, "Definitions")!;
        Check(definitions.Count == 2 && ReferenceEquals(definitions[0], first) && ReferenceEquals(definitions[1], second),
            "Character matching rules preserve fingerprint union and static duplicate precedence");
        Check((bool)Property(resolution, "HasCustomDropHandling")!, "Character union preserves custom drop handling");
        object reverse = Invoke(manager, null, "BuildRuntimeDropResolution", staticRows, ListOf(ruleType, emptyRule, populatedRule), null)!;
        Check(((IList)Property(reverse, "Definitions")!).Count == 2, "Character empty rule does not erase another rule's rows in either order");
        resolution = Invoke(manager, null, "BuildRuntimeDropResolution", staticRows, ListOf(ruleType), null)!;
        Check(!(bool)Property(resolution, "HasMatchedRuntimeRule")! && Property(resolution, "OverrideDrops") == null,
            "Character no matching runtime rules leave the current static or baseline list untouched");
        // Condition routing/cache metadata is covered above. Actual live predicates require Unity.
    }

    private static void CheckCharacterDropOwnership(Type manager, ModContract mod, object compiled)
    {
        Type componentType = mod.LoadGameAssembly("assembly_valheim").GetType("CharacterDrop", true)!;
        Type dropType = componentType.GetNestedType("Drop", All)!;
        FieldInfo liveDropsField = componentType.GetField("m_drops", All)!;
        FieldInfo minField = dropType.GetField("m_amountMin", All)!;
        object baselineRow = Activator.CreateInstance(dropType)!;
        minField.SetValue(baselineRow, 7);
        dropType.GetField("m_levelMultiplierNotAmount")!.SetValue(baselineRow, true);
        IList baseline = ListOf(dropType, baselineRow);
        object snapshot = Activator.CreateInstance(mod.Type("CharacterDropSnapshot"))!;
        SetProperty(snapshot, "BuiltDrops", baseline);
        object staticRow = Activator.CreateInstance(dropType)!;
        FieldInfo prefabField = dropType.GetField("m_prefab", All)!;
        prefabField.SetValue(staticRow, RuntimeHelpers.GetUninitializedObject(prefabField.FieldType));
        minField.SetValue(staticRow, 13);
        dropType.GetField("m_amountMax", All)!.SetValue(staticRow, 17);
        dropType.GetField("m_chance", All)!.SetValue(staticRow, 0.25f);
        dropType.GetField("m_onePerPlayer", All)!.SetValue(staticRow, true);
        dropType.GetField("m_levelMultiplier", All)!.SetValue(staticRow, false);
        dropType.GetField("m_levelMultiplierNotAmount")!.SetValue(staticRow, true);
        dropType.GetField("m_dontScale", All)!.SetValue(staticRow, true);
        IList template = ListOf(dropType, staticRow);
        var staticDrops = (IDictionary)Property(compiled, "StaticBuiltDropsByPrefab")!;
        staticDrops["Owned"] = template;

        // Only managed field storage is used: no Unity construction, lifecycle or scene access.
        object first = RuntimeHelpers.GetUninitializedObject(componentType);
        object second = RuntimeHelpers.GetUninitializedObject(componentType);
        IList firstDrops = Apply(first, true);
        IList secondDrops = Apply(second, true);
        Check(!ReferenceEquals(firstDrops, template) && !ReferenceEquals(firstDrops[0], staticRow) &&
              !ReferenceEquals(firstDrops, secondDrops) && !ReferenceEquals(firstDrops[0], secondDrops[0]),
            "Character live components own separate lists and mutable rows");
        foreach (FieldInfo field in dropType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Check(field.FieldType.IsValueType
                    ? Equals(field.GetValue(staticRow), field.GetValue(firstDrops[0]))
                    : ReferenceEquals(field.GetValue(staticRow), field.GetValue(firstDrops[0])),
                $"Character cloned row preserves {field.Name}");
        minField.SetValue(firstDrops[0], 99);
        firstDrops.Clear();
        Check(template.Count == 1 && (int)minField.GetValue(staticRow)! == 13 &&
              secondDrops.Count == 1 && (int)minField.GetValue(secondDrops[0])! == 13,
            "Character live mutation cannot corrupt template or another component");
        firstDrops = Apply(first, false);
        Check((int)minField.GetValue(firstDrops[0])! == 7 && !ReferenceEquals(firstDrops, baseline) && !ReferenceEquals(firstDrops[0], baselineRow),
            "Character disabling domain restores an owned baseline copy");
        Check((bool)dropType.GetField("m_levelMultiplierNotAmount")!.GetValue(firstDrops[0])!,
            "Character disabling domain preserves the original chance-only flag");
        staticDrops.Remove("Owned");
        firstDrops = Apply(first, true);
        Check((int)minField.GetValue(firstDrops[0])! == 7, "Character removed static override selects baseline");
        staticDrops["Owned"] = ListOf(dropType);
        firstDrops = Apply(first, true);
        Check(firstDrops.Count == 0 && !ReferenceEquals(firstDrops, staticDrops["Owned"]),
            "Character explicit empty overrides baseline with an owned empty list");
        Check((int)minField.GetValue(baselineRow)! == 7, "Character baseline remains unchanged across apply and restore");

        IList Apply(object component, bool enabled)
        {
            Invoke(manager, null, "ApplyOwnedCharacterDrops", component, snapshot, "Owned", enabled, compiled);
            return (IList)liveDropsField.GetValue(component)!;
        }
    }

    private static void CheckCharacterBaselineNormalization(Type manager, ModContract mod)
    {
        Type dropType = mod.LoadGameAssembly("assembly_valheim").GetType("CharacterDrop+Drop", true)!;
        object row = Activator.CreateInstance(dropType)!;
        // A missing prefab is the non-item case executable without Unity native calls.
        foreach (var (field, value) in new (string, object)[] {
                     ("m_amountMin", 2), ("m_amountMax", 5), ("m_chance", 0.25f),
                     ("m_onePerPlayer", true), ("m_levelMultiplier", true), ("m_levelMultiplierNotAmount", true), ("m_dontScale", true) })
            dropType.GetField(field)!.SetValue(row, value);
        IList source = ListOf(dropType, row);
        IList baseline = (IList)Invoke(manager, null, "CloneDrops", source, true)!;
        object captured = baseline[0]!;
        Check(!ReferenceEquals(source, baseline) && !ReferenceEquals(row, captured), "character capture owns list and rows");
        foreach (string field in new[] { "m_prefab", "m_amountMin", "m_amountMax", "m_chance", "m_onePerPlayer", "m_levelMultiplierNotAmount", "m_dontScale" })
            Check(Equals(dropType.GetField(field)!.GetValue(row), dropType.GetField(field)!.GetValue(captured)), "character capture preserves " + field);
        Check(!(bool)dropType.GetField("m_levelMultiplier")!.GetValue(captured)! &&
              (bool)dropType.GetField("m_levelMultiplier")!.GetValue(row)!, "non-item normalization changes only the owned baseline");
        Check(Invoke(manager, null, "GetReferenceLevelMultiplierOverride", captured) == null,
            "normalized non-item reference omits its default false multiplier");
        Check(Equals(Invoke(manager, null, "GetReferenceLevelMultiplierOverride", row), true),
            "reference projection preserves an explicit nondefault multiplier");
        dropType.GetField("m_amountMin")!.SetValue(row, 99);
        source.Clear();
        Check(baseline.Count == 1 && (int)dropType.GetField("m_amountMin")!.GetValue(captured)! == 2,
            "upstream mutation cannot change captured reference/apply baseline");
        IList restored = (IList)Invoke(manager, null, "CloneDrops", baseline, false)!;
        dropType.GetField("m_amountMax")!.SetValue(restored[0], 99);
        Check((int)dropType.GetField("m_amountMax")!.GetValue(captured)! == 5,
            "restoring a live drop does not mutate the shared read-only baseline");
    }

    private static void CheckChanceOnlyDropContracts(Type manager, ModContract mod)
    {
        Type network = mod.Type("NetworkPayloadSyncSupport");
        object schema = Invoke(network, null, "CreateCharacterEntrySchema")!;
        HashSet<string> signatures = new();
        Dictionary<bool, string> fingerprints = new();
        foreach (bool? flag in new bool?[] { null, false, true })
        {
            string flagLine = flag.HasValue ? $", levelMultiplierNotAmount: {flag.Value.ToString().ToLowerInvariant()}" : "";
            object parsed = Invoke(manager, null, "ParseConfiguration",
                "- prefab: Boar\n  characterDrop:\n    drops:\n      - {item: Wood, levelMultiplier: true" + flagLine + "}\n", "chance-only.yml")!;
            Check(((IList)Property(parsed, "Warnings")!).Count == 0, "chance-only YAML parses: " + flag);
            IList entries = (IList)Property(parsed, "Configuration")!;
            object Row(IList list) => ((IList)Property(Property(list[0]!, "CharacterDrop")!, "Drops")!)[0]!;
            Check(Equals(Property(Row(entries), "LevelMultiplierNotAmount"), flag), "YAML preserves optional chance-only flag: " + flag);
            IList clone = (IList)Call(schema, "CloneEntries", entries)!;
            byte[] bytes = (byte[])Call(schema, "SerializeEntries", entries)!;
            IList restored = (IList)Call(schema, "DeserializeEntries", bytes)!;
            Check(Equals(Property(Row(clone), "LevelMultiplierNotAmount"), flag) &&
                  Equals(Property(Row(restored), "LevelMultiplierNotAmount"), flag), "clone and wire preserve chance-only flag: " + flag);
            signatures.Add((string)Call(schema, "ComputePayloadSignature", entries)!);
            string fingerprint = (string)Invoke(manager, null, "BuildDropRowFingerprint", Row(entries), true)!;
            if (flag == false) Check(fingerprints[false] == fingerprint,
                "omitted and false chance-only flag have the same effective fingerprint");
            fingerprints[flag ?? false] = fingerprint;
        }
        Check(signatures.Count == 3, "wire signatures distinguish omitted/false/true chance-only flags");
        Check(fingerprints[false] != fingerprints[true], "chance-only changes invalidate compiled row fingerprints");

        var stackFingerprints = new HashSet<string>();
        foreach (bool? flag in new bool?[] { null, false, true })
        {
            string flagLine = flag.HasValue ? ", dropInStack: " + flag.Value.ToString().ToLowerInvariant() : "";
            object parsed = Invoke(manager, null, "ParseConfiguration",
                "- prefab: Boar\n  characterDrop:\n    drops:\n      - {item: Wood, levelMultiplier: false" + flagLine + "}\n", "stack.yml")!;
            IList entries = (IList)Property(parsed, "Configuration")!;
            object Row(IList list) => ((IList)Property(Property(list[0]!, "CharacterDrop")!, "Drops")!)[0]!;
            byte[] bytes = (byte[])Call(schema, "SerializeEntries", entries)!;
            foreach (IList list in new[] { entries, (IList)Call(schema, "CloneEntries", entries)!, (IList)Call(schema, "DeserializeEntries", bytes)! })
                Check(Equals(Property(Row(list), "DropInStack"), flag), "nullable stack survives YAML/clone/wire: " + flag);
            object row = Row(entries);
            stackFingerprints.Add((string)Invoke(manager, null, "BuildDropRowFingerprint", row, false)!);
            var builder = new System.Text.StringBuilder();
            Invoke(manager, null, "AppendScaffoldCharacterDropEntry", builder, 0, row);
            Check(builder.ToString().Contains("dropInStack: " + (flag?.ToString().ToLowerInvariant() ?? "null")),
                "full scaffold preserves stack inheritance instead of exporting false");
            SetProperty(row, "LevelMultiplier", null);
            string omitted = (string)Invoke(manager, null, "BuildDropRowFingerprint", row, true)!;
            SetProperty(row, "LevelMultiplier", true);
            Check(omitted == (string)Invoke(manager, null, "BuildDropRowFingerprint", row, true)!,
                "equivalent omitted/true item level flags still deduplicate");
        }
        Check(stackFingerprints.Count == 3, "inherit/false/true stack policies remain distinct matching rows");

        Type dropType = mod.LoadGameAssembly("assembly_valheim").GetType("CharacterDrop+Drop", true)!;
        Type compiledType = mod.Type("CompiledCharacterDropDefinition");
        Type prefabType = dropType.GetField("m_prefab")!.FieldType;
        object prefabA = RuntimeHelpers.GetUninitializedObject(prefabType);
        object prefabB = RuntimeHelpers.GetUninitializedObject(prefabType);
        foreach (bool level in new[] { false, true })
        foreach (bool chanceOnly in new[] { false, true })
        {
            object compiled = Activator.CreateInstance(compiledType)!;
            SetProperty(compiled, "Prefab", prefabA);
            SetProperty(compiled, "LevelMultiplier", level);
            SetProperty(compiled, "LevelMultiplierNotAmount", chanceOnly);
            IList definitions = ListOf(compiledType, compiled);
            IList rows = (IList)Invoke(manager, null, "BuildDrops", definitions)!;
            IList copied = (IList)Invoke(manager, null, "CloneDrops", rows, false)!;
            foreach (IList list in new[] { rows, copied })
                Check(Equals(dropType.GetField("m_levelMultiplier")!.GetValue(list[0]), level) &&
                      Equals(dropType.GetField("m_levelMultiplierNotAmount")!.GetValue(list[0]), chanceOnly),
                    "built/copied game rows retain both level flags");
            Check(Protected(rows, prefabA) == (level && chanceOnly) && Protected(definitions, prefabA) == (level && chanceOnly),
                "chance-only bypass requires both flags in game and custom paths");
        }

        foreach (Type rowType in new[] { dropType, compiledType })
        {
            object ordinary = MakeRow(prefabA, false);
            object chanceOnly = MakeRow(prefabA, true);
            object unrelated = MakeRow(prefabB, false);
            IList rows = ListOf(rowType, ordinary, chanceOnly, unrelated);
            Check(Protected(rows, prefabA) && !Protected(rows, prefabB), "mixed duplicate rows protect only their own prefab");
            rows.RemoveAt(0);
            rows.Add(ordinary);
            Check(Protected(rows, prefabA) && !Protected(rows, prefabB), "chance-only protection is independent of failed-row ordering");
            if (rowType == dropType) rows.Insert(0, null);
            Check(Protected(rows, prefabA), "group lookup tolerates null native rows without Unity object access");

            object MakeRow(object prefab, bool flag)
            {
                object row = Activator.CreateInstance(rowType)!;
                if (rowType == dropType)
                {
                    rowType.GetField("m_prefab")!.SetValue(row, prefab);
                    rowType.GetField("m_levelMultiplier")!.SetValue(row, true);
                    rowType.GetField("m_levelMultiplierNotAmount")!.SetValue(row, flag);
                }
                else
                {
                    SetProperty(row, "Prefab", prefab);
                    SetProperty(row, "LevelMultiplier", true);
                    SetProperty(row, "LevelMultiplierNotAmount", flag);
                }
                return row;
            }
        }

        bool Protected(IList rows, object prefab)
        {
            MethodInfo helper = manager.GetMethods(All).Single(m => m.Name == "HasChanceOnlyDropForPrefab" &&
                m.GetParameters()[0].ParameterType.IsInstanceOfType(rows));
            return (bool)helper.Invoke(null, new[] { rows, prefab })!;
        }
    }

    private static void CheckChanceOnlyChanceScaling(Type manager, ModContract mod)
    {
        Type settings = mod.Type("CharacterDropGlobalConfig");
        string[] names = { "_chanceOnlyDropChanceScaling", "_disableCharacterLootScalingWhenCreatureLevelControlLoaded", "_isCreatureLevelControlLoaded",
            "_chanceOnlyDropChanceIncreasePerStar", "_additionalLootChancePerStarCreature", "_additionalLootChancePerStarBoss" };
        FieldInfo[] fields = names.Select(name => settings.GetField(name, All)!).ToArray();
        object?[] previous = fields.Select(field => field.GetValue(null)).ToArray();
        Type configType = mod.LoadGameAssembly("BepInEx").GetType("BepInEx.Configuration.ConfigFile", true)!;
        string path = Path.Combine(Path.GetTempPath(), "dns-unsaved-chance-scaling-" + Guid.NewGuid().ToString("N") + ".cfg");
        object config = Activator.CreateInstance(configType, new object?[] { path, false, null })!;
        SetProperty(config, "SaveOnConfigSet", false);
        MethodInfo bind = configType.GetMethods().Single(method => method.Name == "Bind" && method.IsGenericMethodDefinition &&
            method.GetParameters().Length == 4 && method.GetParameters()[0].ParameterType == typeof(string) &&
            method.GetParameters()[3].ParameterType == typeof(string));
        object Bind(FieldInfo field, object value)
        {
            Type valueType = field.FieldType.GenericTypeArguments[0];
            object entry = bind.MakeGenericMethod(valueType).Invoke(config,
                new object[] { "2 - Character", field.Name, valueType.IsEnum ? Enum.Parse(valueType, (string)value) : value, "" })!;
            field.SetValue(null, entry);
            return entry;
        }
        try
        {
            object mode = Bind(fields[0], "Linear");
            object handoff = Bind(fields[1], "On");
            fields[2].SetValue(null, false);
            fields[3].SetValue(null, null);
            foreach (var sample in new[] { (1, .1f), (2, .15f), (3, .2f), (4, .25f), (19, 1f), (100, 1f) })
            {
                object[] args = { .1f, sample.Item1, true, true, 0f };
                bool changed = (bool)Invoke(manager, null, "TryScaleChanceOnlyDropChance", args)!;
                Check(changed == (sample.Item1 > 1) && Math.Abs((float)args[4] - sample.Item2) < .00001f,
                    "linear chance-only probability and cap at level " + sample.Item1);
            }
            foreach (bool level in new[] { false, true })
            foreach (bool chanceOnly in new[] { false, true })
            {
                object[] args = { .1f, 3, level, chanceOnly, 0f };
                bool changed = (bool)Invoke(manager, null, "TryScaleChanceOnlyDropChance", args)!;
                Check(changed == (level && chanceOnly) && Math.Abs((float)args[4] - (changed ? .2f : .1f)) < .00001f,
                    "linear chance requires both level flags: " + level + "/" + chanceOnly);
            }
            foreach (float chance in new[] { 0f, 1f })
            {
                object[] args = { chance, 50, true, true, 0f };
                Invoke(manager, null, "TryScaleChanceOnlyDropChance", args);
                Check((float)args[4] == chance, "zero and guaranteed probabilities stay bounded");
            }
            object rate = Bind(fields[3], 50);
            object quantityCreature = Bind(fields[4], 0), quantityBoss = Bind(fields[5], 100);
            foreach (var sample in new[] { (0, .1f), (25, .15f), (50, .2f), (100, .3f), (-10, .1f), (150, .3f) })
            {
                SetProperty(rate, "Value", sample.Item1);
                object[] args = { .1f, 3, true, true, 0f };
                Check((bool)Invoke(manager, null, "TryScaleChanceOnlyDropChance", args)! && Math.Abs((float)args[4] - sample.Item2) < .00001f,
                    "live linear percentage is bounded and zero still replaces native scaling: " + sample.Item1);
            }
            SetProperty(rate, "Value", 100);
            object[] capped = { .4f, 3, true, true, 0f };
            Check((bool)Invoke(manager, null, "TryScaleChanceOnlyDropChance", capped)! && (float)capped[4] == 1f,
                "configured increase caps final chance at 100 percent");
            SetProperty(rate, "Value", 25);
            SetProperty(quantityCreature, "Value", 100); SetProperty(quantityBoss, "Value", 0);
            object[] independent = { .1f, 3, true, true, 0f };
            Invoke(manager, null, "TryScaleChanceOnlyDropChance", independent);
            Check(Math.Abs((float)independent[4] - .15f) < .00001f, "chance percentage is independent of both amount percentages");
            SetProperty(mode, "Value", Enum.Parse(fields[0].FieldType.GenericTypeArguments[0], "Vanilla"));
            CheckUnchanged("Vanilla leaves original probability input and star handling intact");
            SetProperty(mode, "Value", Enum.Parse(fields[0].FieldType.GenericTypeArguments[0], "Linear"));
            fields[2].SetValue(null, true);
            CheckUnchanged("CLLC handoff disables new probability scaling");
            SetProperty(handoff, "Value", Enum.Parse(fields[1].FieldType.GenericTypeArguments[0], "Off"));
            object[] enabled = { .1f, 3, true, true, 0f };
            Check((bool)Invoke(manager, null, "TryScaleChanceOnlyDropChance", enabled)! && Math.Abs((float)enabled[4] - .15f) < .00001f,
                "live handoff disable restores configured probability policy");
            Check(!File.Exists(path), "chance-scaling checks never write a config profile");

            void CheckUnchanged(string message)
            {
                object[] args = { .1f, 3, true, true, 0f };
                Check(!(bool)Invoke(manager, null, "TryScaleChanceOnlyDropChance", args)! && (float)args[4] == .1f, message);
            }
        }
        finally
        {
            for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, previous[i]);
        }

        using var module = Mono.Cecil.ModuleDefinition.ReadModule(mod.Assembly.Location);
        var managerType = module.Types.Single(type => type.FullName == "DropNSpawn.CharacterDropManager");
        foreach (string name in new[] { "PrepareCharacterLootScalingDrops", "GenerateConfiguredDrops" })
        {
            var method = managerType.Methods.Single(method => method.Name == name);
            Check(method.Body.Instructions.Any(i => i.Operand is Mono.Cecil.MethodReference call && call.Name == "TryScaleChanceOnlyDropChance"),
                name + " uses the tested chance policy");
        }
    }

    private static object? Property(object value, string name) => value.GetType().GetProperty(name, All)!.GetValue(value);
    private static void SetProperty(object value, string name, object? propertyValue) => value.GetType().GetProperty(name, All)!.SetValue(value, propertyValue);
    private static IList ListOf(Type elementType, params object[] values)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (object value in values) list.Add(value);
        return list;
    }
}
