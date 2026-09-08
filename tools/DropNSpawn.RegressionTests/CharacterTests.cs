using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static partial class Program
{
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

    private static object? Property(object value, string name) => value.GetType().GetProperty(name, All)!.GetValue(value);
    private static void SetProperty(object value, string name, object? propertyValue) => value.GetType().GetProperty(name, All)!.SetValue(value, propertyValue);
    private static IList ListOf(Type elementType, params object[] values)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        foreach (object value in values) list.Add(value);
        return list;
    }
}
