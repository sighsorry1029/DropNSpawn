using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

internal static partial class Program
{
    private static void CheckDungeonSpawnContracts(ModContract mod)
    {
        Type config = mod.Type("DungeonSpawnConfiguration"), manager = mod.Type("DungeonSpawnManager");
        string sample = (string)config.GetField("Sample", All)!.GetRawConstantValue()!;
        IList rules = (IList)Invoke(config, null, "Parse", sample)!;
        Check(rules.Count == 1 && (string)Property(rules[0]!, "Id")! == "sunkencrypt_ambush", "dungeon sample parses with stable id");
        object rule = rules[0]!;
        Check((float)Property(rule, "SpawnInterval")! == 60 && (float)Property(rule, "SpawnChance")! == 10,
            "dungeon interval seconds and percent chance remain distinct from weights");
        foreach (string yaml in new[] {
            sample.Replace("spawnChance: 10", "spawnChance: .nan"), sample.Replace("spawnChance: 10", "spawnChance: 101"),
            sample.Replace("spawnInterval: 60", "spawnInterval: 0"), sample.Replace("spawnRadius: 6~12", "spawnRadius: 12~6"),
            sample.Replace("spawnRadius: 6~12", "spawnRadius: 6~.inf"), sample.Replace("maxAlive: 3", "maxAlive: 0"),
            sample.Replace("weight: 2", "weight: -2"), sample.Replace("locations: [SunkenCrypt4]", "locations: null"),
            sample.Replace("enabled: true", "enabled: true\n  enabled: false"), sample.Replace("spawnChance:", "spwanChance:"),
            "- null", sample + "\n" + sample })
        {
            bool rejected = false;
            try { Invoke(config, null, "Parse", yaml); } catch (TargetInvocationException) { rejected = true; }
            Check(rejected, "dungeon rejects malformed/ambiguous/nonfinite rules");
        }
        Check(((IList)Invoke(config, null, "Parse", "[]")!).Count == 0, "empty dungeon config disables all rules");
        Check((string)Invoke(manager, null, "SelectCreature", rule, 0.5f)! == "BlobElite" &&
              (string)Invoke(manager, null, "SelectCreature", rule, 0.9f)! == "DamnedOne_TW",
            "dungeon relative weights choose exactly one creature after the chance roll");
        Check((bool)Invoke(manager, null, "ShouldReloadForPath", "DNS_dungeon_crypt.yaml")! &&
              !(bool)Invoke(manager, null, "ShouldReloadForPath", "DNS_dungeon.sample.yml")!, "dungeon split files load, samples stay inactive");

        Type document = mod.Type("ConfigurationLoadSupport").GetNestedType("LocalYamlDocument", All)!;
        IList documents = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(document))!;
        foreach (string file in new[] { "one.yml", "two.yml" })
        {
            object doc = Activator.CreateInstance(document, true)!;
            SetProperty(doc, "Path", file); SetProperty(doc, "Yaml", sample); documents.Add(doc);
        }
        object parsed = Invoke(manager, null, "ParseDocuments", documents)!;
        Check(((IList)Property(parsed, "Errors")!).Count > 0, "duplicate IDs across separate files reject the reload");

        Type scheduleType = manager.GetNestedType("Schedule", All)!;
        object first = Activator.CreateInstance(scheduleType, true)!, second = Activator.CreateInstance(scheduleType, true)!;
        Set(first, "Due", 60d); Set(second, "Due", 60d);
        Check(Attempts(first, 0, 4) == 0 && Attempts(first, 59, 4) == 0, "no immediate check on entry");
        Check(Attempts(first, 60, 4) == 4 && Attempts(first, 60, 4) == 0, "four present players receive four attempts in one batch, never another batch on the same tick");
        Check(Attempts(second, 60, 1) == 1, "independent dungeon/rule with one player receives one attempt");
        Check(Attempts(first, 900, 2) == 2 && Attempts(first, 900, 2) == 0 && (double)Get(first, "Due")! == 960,
            "changed occupancy is used once after a long stall, no catch-up burst");
        Check((bool)Call(first, "TryReserve", 2, 3)! && !(bool)Call(first, "TryReserve", 2, 3)! && (int)Get(first, "PendingCount")! == 1,
            "two living creatures leave exactly one shared reservation for all players");
        Check(Attempts(first, 960, 4) == 4, "outstanding replies do not suppress the next interval's player batch");
        Set(first, "PendingCount", 0);
        Check((bool)Call(first, "TryReserve", 0, 1)! && !(bool)Call(first, "TryReserve", 0, 1)!, "maxAlive one cannot reserve for two successful player rolls");
        Set(first, "PendingCount", 0);

        FieldInfo rulesField = manager.GetField("Rules", All)!;
        object? savedRules = rulesField.GetValue(null);
        IDictionary requests = (IDictionary)manager.GetField("Requests", All)!.GetValue(null)!;
        Type pendingType = manager.GetNestedType("Pending", All)!, playerType = manager.GetNestedType("PlayerContext", All)!;
        try
        {
            rulesField.SetValue(null, rules);
            AddRequest(41, first, 100, rule);
            AddRequest(46, first, 100, rule, 88);
            Check(Invoke(manager, null, "TakeRequest", 41L, 999L, 10d) == null && requests.Contains(41L) && (int)Get(first, "PendingCount")! == 2, "wrong peer cannot consume another player's request or reservation");
            Check(Invoke(manager, null, "TakeRequest", 41L, 77L, 10d) != null && (int)Get(first, "PendingCount")! == 1, "first reply releases only its own reservation before creation");
            Check(Invoke(manager, null, "TakeRequest", 41L, 77L, 10d) == null && (int)Get(first, "PendingCount")! == 1, "duplicate reply cannot spawn or release another player's reservation");
            Check(!(bool)Call(first, "HasCapacity", 2, 3)!, "completed host spawn plus another pending player fills the shared cap");
            Check(Invoke(manager, null, "TakeRequest", 46L, 88L, 10d) != null && (int)Get(first, "PendingCount")! == 0 &&
                  (bool)Call(first, "HasCapacity", 2, 3)!, "last response consumes its reservation and can fill the last living slot");
            Check(!(bool)Call(first, "HasCapacity", 3, 3)!, "final spawn check rejects cap filled by a concurrent revival");
            AddRequest(42, first, 9, rule);
            Check(Invoke(manager, null, "TakeRequest", 42L, 77L, 10d) == null && !requests.Contains(42L) && (int)Get(first, "PendingCount")! == 0, "expired response releases slot without spawning");
            AddRequest(47, first, 100, rule);
            Invoke(manager, null, "Cancel", 47L); Invoke(manager, null, "Cancel", 47L);
            Check((int)Get(first, "PendingCount")! == 0 && !requests.Contains(47L), "departure/dispatch failure cancellation is idempotent");
            Check((bool)Call(first, "TryReserve", 2, 3)!, "released failed placement can be reused without waiting for every player");
            Set(first, "PendingCount", 0);
            AddRequest(43, first, 100, rule); AddRequest(44, second, 100, rule);
            Check(Invoke(manager, null, "TakeRequest", 43L, 77L, 10d) != null && Invoke(manager, null, "TakeRequest", 44L, 77L, 10d) != null,
                "same peer's simultaneous distinct requests both complete");
            AddRequest(45, first, 100, rule);
            Invoke(manager, null, "ResetTimers");
            Check(Invoke(manager, null, "TakeRequest", 45L, 77L, 10d) == null, "reload/empty dungeon reset invalidates late response");
        }
        finally { requests.Clear(); rulesField.SetValue(null, savedRules); }

        CheckDungeonZdoCounts(mod, manager);
        CheckDungeonBlockerBoundary(mod, manager);

        int Attempts(object schedule, double now, int players) => (int)Call(schedule, "BeginInterval", now, 60f, players)!;
        void AddRequest(long token, object schedule, double expiry, object currentRule, long peer = 77)
        {
            object player = Activator.CreateInstance(playerType, true)!; Set(player, "Peer", peer);
            object pending = Activator.CreateInstance(pendingType, true)!;
            Set(pending, "Player", player); Set(pending, "Schedule", schedule); Set(pending, "Expires", expiry); Set(pending, "Rule", currentRule);
            Set(schedule, "PendingCount", (int)Get(schedule, "PendingCount")! + 1); requests.Add(token, pending);
        }
        static void Set(object obj, string name, object value) => obj.GetType().GetField(name, All)!.SetValue(obj, value);
        static object? Get(object obj, string name) => obj.GetType().GetField(name, All)!.GetValue(obj);
    }

    private static void CheckDungeonBlockerBoundary(ModContract mod, Type manager)
    {
        Type unityObject = mod.LoadGameAssembly("UnityEngine.CoreModule").GetType("UnityEngine.GameObject", true)!;
        Type vector = mod.LoadGameAssembly("UnityEngine.CoreModule").GetType("UnityEngine.Vector3", true)!;
        FieldInfo resolved = manager.GetField("CompatibilityResolved", All)!, unavailable = manager.GetField("CompatibilityUnavailable", All)!, callback = manager.GetField("CanSpawnWithCreatureManager", All)!;
        object? oldResolved = resolved.GetValue(null), oldUnavailable = unavailable.GetValue(null), oldCallback = callback.GetValue(null);
        object zero = Activator.CreateInstance(vector)!;
        try
        {
            resolved.SetValue(null, true); unavailable.SetValue(null, false); callback.SetValue(null, null);
            Check(Allowed(), "absent optional CM allows standalone dungeon spawns");
            unavailable.SetValue(null, true);
            Check(!Allowed(), "installed incompatible CM does not silently bypass blocking policy");
            unavailable.SetValue(null, false);
            foreach (bool allow in new[] { false, true })
            {
                DynamicMethod method = new("CMFixture", typeof(bool), new[] { unityObject, vector });
                ILGenerator il = method.GetILGenerator(); il.Emit(allow ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
                callback.SetValue(null, method.CreateDelegate(callback.FieldType));
                Check(Allowed() == allow, "cached CM callback preserves both block and allow decisions");
            }
            DynamicMethod failing = new("CMFailure", typeof(bool), new[] { unityObject, vector });
            ILGenerator failIl = failing.GetILGenerator(); failIl.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(Type.EmptyTypes)!); failIl.Emit(OpCodes.Throw);
            callback.SetValue(null, failing.CreateDelegate(callback.FieldType));
            Check(!Allowed(), "optional blocker exception fails closed for this attempt");
        }
        finally { resolved.SetValue(null, oldResolved); unavailable.SetValue(null, oldUnavailable); callback.SetValue(null, oldCallback); }
        bool Allowed() => (bool)Invoke(manager, null, "AllowSpawn", new object?[] { null, zero })!;
    }

    private static void CheckDungeonZdoCounts(ModContract mod, Type manager)
    {
        Assembly game = mod.LoadGameAssembly("assembly_valheim");
        Type zdoType = game.GetType("ZDO", true)!, idType = game.GetType("ZDOID", true)!, manType = game.GetType("ZDOMan", true)!;
        FieldInfo singleton = manType.GetField("s_instance", All)!;
        object? previous = singleton.GetValue(null);
        Type netType = game.GetType("ZNet", true)!;
        FieldInfo netSingleton = netType.GetField("m_instance", All)!;
        object? previousNet = netSingleton.GetValue(null);
        object net = RuntimeHelpers.GetUninitializedObject(netType);
        netType.GetField("m_isServer", All)!.SetValue(net, true);
        object man = RuntimeHelpers.GetUninitializedObject(manType);
        IDictionary objects = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(idType, zdoType))!;
        manType.GetField("m_objectsByID", All)!.SetValue(man, objects);
        object tracked = manager.GetField("Tracked", All)!.GetValue(null)!;
        int ruleTag = (int)manager.GetField("RuleTag", All)!.GetValue(null)!, dungeonTag = (int)manager.GetField("DungeonTag", All)!.GetValue(null)!;
        int health = (int)game.GetType("ZDOVars", true)!.GetField("s_health", All)!.GetValue(null)!;
        Type vector = mod.LoadGameAssembly("UnityEngine.CoreModule").GetType("UnityEngine.Vector3", true)!;
        try
        {
            singleton.SetValue(null, man);
            netSingleton.SetValue(null, net);
            object one = Zdo(93001, "rule", "crypt:1"), two = Zdo(93002, "rule", "crypt:2"), other = Zdo(93003, "other", "crypt:1");
            SetPosition(one, 0, 5000, 0); SetPosition(two, 100, 5000, 0); SetPosition(other, 10, 5000, 0);
            Check(Occupied(0, 5000, 0) && Occupied(0.05f, 5000, 0), "first accepted ZDO blocks simultaneous same-anchor replies before scene creation");
            Check(Occupied(10, 5000, 0), "same-point guard also covers another dungeon rule");
            Check(!Occupied(1, 5000, 0) && !Occupied(0, 5002, 0), "point guard permits distinct positions and floors; it is not a full capsule overlap test");
            Check(Count("rule", "crypt:1") == 1 && Count("rule", "crypt:2") == 1 && Count("other", "crypt:1") == 1,
                "unloaded persistent ZDO counts isolate dungeon instances and rule IDs");
            Check((bool)Invoke(manager, null, "IsAlive", one)!, "absent health key is full health, not death");
            zdoType.GetMethod("Set", new[] { typeof(int), typeof(float) })!.Invoke(one, new object[] { health, 0f });
            Check(Count("rule", "crypt:1") == 0, "dead creature releases the living cap");
            Check(!Occupied(0, 5000, 0), "dead ZDO does not retain a same-point exclusion");
            zdoType.GetMethod("Set", new[] { typeof(int), typeof(float) })!.Invoke(one, new object[] { health, 10f });
            Check(Count("rule", "crypt:1") == 1, "reviving the same persisted identity restores its cap contribution");
            object rebuilt = Zdo(93001, "rule", "crypt:1");
            SetPosition(rebuilt, 0, 5000, 0);
            Check(Count("rule", "crypt:1") == 1, "replacement managed ZDO wrapper keeps persistent tag/health state");
            objects.Remove(zdoType.GetField("m_uid")!.GetValue(rebuilt)!);
            Check(!Occupied(0, 5000, 0), "destroyed ZDO does not block a delayed candidate");
            Check(Count("rule", "crypt:1") == 0, "destroyed ZDO is pruned without retaining a cap slot");
        }
        finally { Call(tracked, "Clear"); singleton.SetValue(null, previous); netSingleton.SetValue(null, previousNet); }

        int Count(string rule, string dungeon) => (int)Invoke(manager, null, "CountAlive", rule, dungeon)!;
        object Position(float x, float y, float z) => Activator.CreateInstance(vector, new object[] { x, y, z })!;
        void SetPosition(object zdo, float x, float y, float z) => zdoType.GetField("m_position", All)!.SetValue(zdo, Position(x, y, z));
        bool Occupied(float x, float y, float z) => (bool)Invoke(manager, null, "IsSpawnPointOccupied", Position(x, y, z))!;
        object Zdo(uint id, string rule, string dungeon)
        {
            object zdo = RuntimeHelpers.GetUninitializedObject(zdoType);
            object uid = Activator.CreateInstance(idType, new object[] { 9834567L, id })!;
            zdoType.GetField("m_uid")!.SetValue(zdo, uid);
            zdoType.GetField("m_prefab", All)!.SetValue(zdo, 12345);
            SetProperty(zdo, "Owner", true);
            zdoType.GetMethod("Set", new[] { typeof(int), typeof(string) })!.Invoke(zdo, new object[] { ruleTag, rule });
            zdoType.GetMethod("Set", new[] { typeof(int), typeof(string) })!.Invoke(zdo, new object[] { dungeonTag, dungeon });
            objects[uid] = zdo; Call(tracked, "Add", uid);
            return zdo;
        }
    }
}
