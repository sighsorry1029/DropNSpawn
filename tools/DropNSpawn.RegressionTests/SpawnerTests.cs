using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static partial class Program
{
    private static void CheckCreatureSpawnerLimitContracts(ModContract mod)
    {
        Type manager = mod.Type("SpawnerManager");
        Type settings = mod.Type("SpawnerGlobalConfig");
        Type entryType = mod.Type("SpawnerConfigurationEntry");
        Type definitionType = mod.Type("CreatureSpawnerDefinition");
        object schema = Invoke(mod.Type("NetworkPayloadSyncSupport"), null, "CreateSpawnerEntrySchema")!;
        var signatures = new HashSet<string>();
        foreach (int? limit in new int?[] { null, 0, 1, 100, 1000 })
        {
            object definition = Activator.CreateInstance(definitionType)!;
            SetProperty(definition, "MaxTotalSpawns", limit);
            object entry = Activator.CreateInstance(entryType)!;
            SetProperty(entry, "Prefab", "Spawner_Boar");
            SetProperty(entry, "CreatureSpawner", definition);
            var entries = ListOf(entryType, entry);
            byte[] bytes = (byte[])Call(schema, "SerializeEntries", entries)!;
            object restored = Property(((IList)Call(schema, "DeserializeEntries", bytes)!)[0]!, "CreatureSpawner")!;
            object cloned = Invoke(mod.Type("ConfigurationEntryCloneSupport"), null, "CloneCreatureSpawnerDefinition", definition)!;
            Check(Equals(Property(restored, "MaxTotalSpawns"), limit) && Equals(Property(cloned, "MaxTotalSpawns"), limit), "spawner limit wire/clone preserves null, zero and positive values");
            signatures.Add((string)Call(schema, "ComputePayloadSignature", entries)!);
            Check((bool)Invoke(manager, null, "HasCreatureSpawnerOverride", definition)! == limit.HasValue, "limit-only YAML qualifies as a spawner override");
        }
        Check(signatures.Count == 5, "each limit value has a distinct synchronized signature");

        string yamlPath = Path.Combine(Path.GetTempPath(), "dns-spawner-limits-" + Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            File.WriteAllText(yamlPath, "- prefab: Spawner_Boar\n  creatureSpawner:\n    respawnTimeMinutes: 0\n    maxTotalSpawns: 100\n");
            object documents = Invoke(mod.Type("ConfigurationLoadSupport"), null, "ReadLocalYamlDocuments", new List<string> { yamlPath })!;
            object parsed = Invoke(manager, null, "ParseLocalConfigurationDocuments", documents)!;
            Check(((IList)Property(parsed, "Errors")!).Count == 0, "real spawner YAML parser accepts one-time + cumulative limit");
            object parsedEntry = ((IList)Property(parsed, "Entries")!)[0]!;
            object definition = Property(parsedEntry, "CreatureSpawner")!;
            Check((int)Property(definition, "MaxTotalSpawns")! == 100 && (float)Property(definition, "RespawnTimeMinutes")! == 0f, "limit normalization does not enable respawn");
        }
        finally { File.Delete(yamlPath); }

        FieldInfo settingField = settings.GetField("_defaultCreatureSpawnerMaxTotalSpawns", All)!;
        object? previousSetting = settingField.GetValue(null);
        Type configType = mod.LoadGameAssembly("BepInEx").GetType("BepInEx.Configuration.ConfigFile", true)!;
        object config = Activator.CreateInstance(configType, new object?[] { Path.Combine(Path.GetTempPath(), "dns-unsaved-spawner-" + Guid.NewGuid().ToString("N") + ".cfg"), false, null })!;
        SetProperty(config, "SaveOnConfigSet", false);
        MethodInfo bind = configType.GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition && m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType == typeof(string)).MakeGenericMethod(typeof(int));
        object setting = bind.Invoke(config, new object[] { "1 - General", "Default CreatureSpawner Max Total Spawns", 0, "" })!;
        try
        {
            settingField.SetValue(null, setting);
            Check(Resolve(null) == 0, "new global limit defaults to disabled");
            SetProperty(setting, "Value", 100);
            Check(Resolve(null) == 100 && Resolve(0) == 0 && Resolve(3) == 3, "YAML positive/zero override live global default");
            SetProperty(setting, "Value", 50);
            Check(Resolve(null) == 50 && Resolve(3) == 3, "global reload requires no per-spawner default cache refresh");
            Check(Resolve(-1) == 0 && Resolve(1001) == 1000, "limits keep the existing SpawnArea range");
        }
        finally { settingField.SetValue(null, previousSetting); }

        Assembly game = mod.LoadGameAssembly("assembly_valheim");
        Type zdoType = game.GetType("ZDO", true)!;
        Type idType = game.GetType("ZDOID", true)!;
        Type znetType = game.GetType("ZNet", true)!;
        Type zdoManType = game.GetType("ZDOMan", true)!;
        FieldInfo netInstance = znetType.GetField("m_instance", All)!;
        FieldInfo zdoManInstance = zdoManType.GetField("s_instance", All)!;
        object? previousNet = netInstance.GetValue(null), previousZdoMan = zdoManInstance.GetValue(null);
        int key = (int)manager.GetField("CreatureSpawnerTotalSpawnCountZdoKey", All)!.GetValue(null)!;
        object owner = NewZdo(91001), second = NewZdo(91002);
        try
        {
            object net = RuntimeHelpers.GetUninitializedObject(znetType);
            znetType.GetField("m_isServer", All)!.SetValue(net, true);
            netInstance.SetValue(null, net);
            zdoManInstance.SetValue(null, RuntimeHelpers.GetUninitializedObject(zdoManType));
            Check(Allowed(owner, 2) && Allowed(null, 0) && !Allowed(null, 2), "positive limit needs authoritative ZDO, zero adds no condition");
            Record(owner, false);
            Check(Count(owner) == 0, "failed/blocked attempt does not consume a spawn");
            Record(owner, true);
            Check(Count(owner) == 1 && Allowed(owner, 2) && Count(second) == 0, "successful spawn counts per instance, not per prefab");
            SetProperty(owner, "Owner", false);
            Record(owner, true);
            Check(Count(owner) == 1 && !Allowed(owner, 2), "non-owner cannot count or spawn through a positive limit");
            SetProperty(owner, "Owner", true);
            Record(owner, true);
            Check(Count(owner) == 2 && !Allowed(owner, 2), "last allowed success exhausts the limit");
            Check(Allowed(owner, 0) && Count(owner) == 2 && !Allowed(owner, 1) && Allowed(owner, 3), "disable/lower/raise preserves lifetime count and allows resume");
            object reloaded = NewZdo(91001);
            Check(Count(reloaded) == 2 && !Allowed(reloaded, 2), "replacement ZDO wrapper with same persisted identity retains the counter");
            zdoType.GetMethod("Set", new[] { typeof(int), typeof(int), typeof(bool) })!.Invoke(owner, new object[] { key, int.MaxValue, false });
            Record(owner, true);
            Check(Count(owner) == int.MaxValue, "counter cannot overflow into an unlimited-looking negative count");

            object state = manager.GetField("LiveReconcilerState", All)!.GetValue(null)!;
            object spawner = RuntimeHelpers.GetUninitializedObject(game.GetType("CreatureSpawner", true)!);
            Call(state, "SetAppliedCreatureSpawnerTotalSpawnLimit", spawner, 0);
            Check((int)Call(state, "GetAppliedCreatureSpawnerTotalSpawnLimit", spawner)! == 0, "explicit unlimited survives the override cache");
            Call(state, "ClearAppliedCreatureSpawnerOverrides", spawner);
            Check(Call(state, "GetAppliedCreatureSpawnerTotalSpawnLimit", spawner) == null && Count(owner) == int.MaxValue, "instance teardown clears only cached YAML, not the saved counter");
        }
        finally
        {
            netInstance.SetValue(null, previousNet);
            zdoManInstance.SetValue(null, previousZdoMan);
        }

        int Resolve(int? limit) => (int)Invoke(manager, null, "ResolveCreatureSpawnerMaxTotalSpawns", new object?[] { limit })!;
        bool Allowed(object? zdo, int limit) => (bool)Invoke(manager, null, "CanCreatureSpawnerSpawnWithLimit", zdo, limit)!;
        void Record(object zdo, bool success) => Invoke(manager, null, "RecordCreatureSpawnerTotalSpawn", zdo, success);
        int Count(object zdo) => (int)zdoType.GetMethod("GetInt", new[] { typeof(int), typeof(int) })!.Invoke(zdo, new object[] { key, 0 })!;
        object NewZdo(uint id)
        {
            object zdo = RuntimeHelpers.GetUninitializedObject(zdoType);
            zdoType.GetField("m_uid")!.SetValue(zdo, Activator.CreateInstance(idType, new object[] { 9812345L, id }));
            SetProperty(zdo, "Owner", true);
            return zdo;
        }
    }
}
