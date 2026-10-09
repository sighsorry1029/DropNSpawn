using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using System.Reflection.Emit;

namespace DropNSpawn.Tests
{
    // Original game assemblies and the installed Harmony run under Unity's Mono.
    // Objects below are managed field fixtures; no Unity scene or network is started.
    public static class CompatibilityProbe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static int checks;
        private static int postfixCalls;
        private static Type spawnerLimitManager;
        private static bool ewdServer;
        private static readonly Dictionary<CreatureSpawner, ZDO> groupCounters = new Dictionary<CreatureSpawner, ZDO>(new SpawnerFixtureComparer());
        private static CreatureSpawner selectedGroupSpawner;

        // Fixture identity only: Unity Object equality needs native scene state.
        private sealed class SpawnerFixtureComparer : IEqualityComparer<CreatureSpawner>
        {
            public bool Equals(CreatureSpawner x, CreatureSpawner y) => ReferenceEquals(x, y);
            public int GetHashCode(CreatureSpawner obj) => RuntimeHelpers.GetHashCode(obj);
        }

        public static int Run()
        {
            try
            {
                string managed = Environment.GetEnvironmentVariable("DROPNSPAWN_MONO_PROBE_MANAGED");
                AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
                {
                    string path = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
                    return File.Exists(path) ? Assembly.LoadFrom(path) : null;
                };
                RunChecks(managed);
                System.Console.WriteLine("PASS: " + checks + " Unity Mono managed checks; no scene, gameplay or network session.");
                return 0;
            }
            catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RunChecks(string managed)
        {
            Assembly mod = Assembly.LoadFrom(Environment.GetEnvironmentVariable("DROPNSPAWN_MONO_PROBE_DLL"));
            Check(Path.GetFullPath(typeof(Character).Assembly.Location) == Path.Combine(managed, "assembly_valheim.dll"), "original game assembly");
            System.Console.WriteLine("Game: " + typeof(Character).Assembly.Location);
            System.Console.WriteLine("Harmony: " + typeof(Harmony).Assembly.FullName);
            Type dungeonConfig = mod.GetType("DropNSpawn.DungeonSpawnConfiguration", true);
            string dungeonSample = (string)dungeonConfig.GetField("Sample", All).GetRawConstantValue();
            var dungeonRules = (System.Collections.IList)dungeonConfig.GetMethod("Parse", All).Invoke(null, new object[] { dungeonSample });
            Check(dungeonRules.Count == 1, "dungeon merged YAML parser under Unity Mono");
            Type dungeon = mod.GetType("DropNSpawn.DungeonSpawnManager", true);
            Type scheduleType = dungeon.GetNestedType("Schedule", All);
            object schedule = Activator.CreateInstance(scheduleType, true);
            scheduleType.GetField("Due", All).SetValue(schedule, 60d);
            MethodInfo due = scheduleType.GetMethod("BeginInterval", All);
            Check((int)due.Invoke(schedule, new object[] { 0d, 60f, 4 }) == 0 && (int)due.Invoke(schedule, new object[] { 60d, 60f, 4 }) == 4 &&
                (int)due.Invoke(schedule, new object[] { 60d, 60f, 4 }) == 0, "dungeon one per-player batch per interval under Unity Mono");
            MethodInfo reserve = scheduleType.GetMethod("TryReserve", All);
            Check((bool)reserve.Invoke(schedule, new object[] { 1, 3 }) && (bool)reserve.Invoke(schedule, new object[] { 1, 3 }) &&
                !(bool)reserve.Invoke(schedule, new object[] { 1, 3 }), "multiple dungeon players share the living plus pending cap under Unity Mono");
            int ruleTag = (int)dungeon.GetField("RuleTag", All).GetValue(null);
            Check(ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.String, ruleTag) != null, "original public ZDO metadata lookup for unloaded dungeon cap restore");
            CheckDungeonRpcLifecycle(dungeon);
            foreach (string name in new[] { "EventManager+GameAccess", "CharacterDropKillerFilter", "RagdollSetupMonsterInstantLootDropPatch", "VneiCompatibility" })
            {
                RuntimeHelpers.RunClassConstructor(mod.GetType("DropNSpawn." + name, true).TypeHandle);
                Check(true, "accessor construction " + name);
            }

            Type access = mod.GetType("DropNSpawn.EventManager+GameAccess", true);
            var system = (RandEventSystem)FormatterServices.GetUninitializedObject(typeof(RandEventSystem));
            var ev = (RandomEvent)FormatterServices.GetUninitializedObject(typeof(RandomEvent));
            ev.m_name = "compatibility_fixture";
            ev.m_enabled = true;
            system.m_events = new List<RandomEvent> { ev };
            var timer = (AccessTools.FieldRef<RandEventSystem, float>)access.GetField("EventTimer", All).GetValue(null);
            timer(system) = 12.5f;
            Check((float)typeof(RandEventSystem).GetField("m_eventTimer", All).GetValue(system) == 12.5f, "private timer write");
            var random = (AccessTools.FieldRef<RandEventSystem, RandomEvent>)access.GetField("RandomEvent", All).GetValue(null);
            random(system) = ev;
            Check(ReferenceEquals(system.GetCurrentRandomEvent(), ev), "private random-event write/public read");
            var needsRefresh = (AccessTools.FieldRef<bool>)access.GetField("NeedsRefresh", All).GetValue(null);
            needsRefresh() = false;
            RandEventSystem.SetRandomEventsNeedsRefresh();
            Check(needsRefresh(), "private static field observes public invalidation");

            var getEvent = (Func<RandEventSystem, string, RandomEvent>)access.GetField("GetEvent", All).GetValue(null);
            Check(ReferenceEquals(getEvent(system, ev.m_name), ev), "private method delegate");
            // Delegates are cached before patching in the real plugin. Confirm that
            // Harmony's replacement remains visible to an already-created delegate.
            var harmony = new Harmony("DropNSpawn.isolated.compatibility");
            MethodInfo original = AccessTools.Method(typeof(RandEventSystem), "GetEvent", new[] { typeof(string) });
            try
            {
                harmony.Patch(original, postfix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(ObserveEvent)));
                Check(ReferenceEquals(getEvent(system, ev.m_name), ev) && postfixCalls == 1, "cached delegate respects Harmony detour");
            }
            finally { harmony.UnpatchSelf(); }

            Type killer = mod.GetType("DropNSpawn.CharacterDropKillerFilter", true);
            var poison = (SE_Poison)FormatterServices.GetUninitializedObject(typeof(SE_Poison));
            typeof(SE_Poison).GetField("m_damageLeft", All).SetValue(poison, 9f);
            var damage = (AccessTools.FieldRef<SE_Poison, float>)killer.GetField("PoisonDamageLeft", All).GetValue(null);
            Check(damage(poison) == 9f, "private poison damage read");
            var character = (Character)FormatterServices.GetUninitializedObject(typeof(Character));
            var hit = new HitData();
            typeof(Character).GetField("m_lastHit", All).SetValue(character, hit);
            var lastHit = (AccessTools.FieldRef<Character, HitData>)killer.GetField("LastHit", All).GetValue(null);
            Check(ReferenceEquals(lastHit(character), hit), "protected death-credit field read");
            CheckKillerAttribution(mod);

            var item = (ItemDrop)FormatterServices.GetUninitializedObject(typeof(ItemDrop));
            item.m_itemData = new ItemDrop.ItemData();
            Game.m_worldLevel = 2;
            foreach (bool cheated in new[] { true, false })
            {
                ItemDrop.OnCreateNew(item, cheated);
                Check(item.m_itemData.m_cheated == cheated && item.m_itemData.m_worldLevel == 2, "game item provenance " + cheated);
            }
            string testRoot = Path.GetDirectoryName(typeof(CompatibilityProbe).Assembly.Location);
            AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null,
                new object[] { Path.Combine(testRoot, "CompatibilityMonoHost.exe"), testRoot, managed, null });
            // EWD's managed initializer queues ServerSync startup. Keep that queue
            // pending: full BepInEx/Unity/network startup is outside this probe.
            var threading = (BepInEx.ThreadingHelper)FormatterServices.GetUninitializedObject(typeof(BepInEx.ThreadingHelper));
            AccessTools.Field(typeof(BepInEx.ThreadingHelper), "_invokeLock").SetValue(threading, new object());
            AccessTools.PropertySetter(typeof(BepInEx.ThreadingHelper), "Instance").Invoke(null, new object[] { threading });
            string ewdPath = Path.Combine(testRoot, "ExpandWorldData.dll");
            Type compatibilityType = mod.GetType("DropNSpawn.ExpandWorldDataCompatibility", true);
            compatibilityType.GetMethod("ConfigureDependency", All).Invoke(null, new object[] { File.Exists(ewdPath) ? Assembly.LoadFrom(ewdPath) : null });
            CheckEpicLootInstantCompatibility(mod, Path.Combine(testRoot, "EpicLoot.dll"));
            CheckCreatureSpawnerLimits(mod);
            if (File.Exists(ewdPath)) CheckEwdCompatibility(mod);
            else CheckStandalone(mod);
        }

        private static bool dungeonNetEnabled, dungeonSceneEnabled;

        private static void CheckDungeonRpcLifecycle(Type dungeon)
        {
            var fixture = new Harmony("DropNSpawn.isolated.dungeon.rpc");
            MethodInfo tick = dungeon.GetMethod("Tick", All), reset = dungeon.GetMethod("ResetWorld", All);
            FieldInfo net = AccessTools.Field(typeof(ZNet), "m_instance"), scene = AccessTools.Field(typeof(ZNetScene), "s_instance"),
                world = AccessTools.Field(typeof(ZDOMan), "s_instance"), rpc = AccessTools.Field(typeof(ZRoutedRpc), "s_instance"),
                active = dungeon.GetField("Active", All), hasRules = dungeon.GetField("HasRules", All), registered = dungeon.GetField("RegisteredRpc", All);
            FieldInfo[] fields = { net, scene, world, rpc, active, hasRules, registered };
            object[] saved = fields.Select(field => field.GetValue(null)).ToArray();
            FieldInfo handlers = AccessTools.Field(typeof(ZRoutedRpc), "m_functions");
            int request = "DNS_DungeonPosition_v1".GetStableHashCode(), reply = "DNS_DungeonPositionReply_v1".GetStableHashCode();
            try
            {
                // Only scene identity/enabled state is substituted. Tick, ResetWorld and
                // the original ZRoutedRpc.Register/Dictionary.Add execute unchanged.
                fixture.Patch(tick, transpiler: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolateDungeonSceneCalls)));
                net.SetValue(null, FormatterServices.GetUninitializedObject(typeof(ZNet)));
                scene.SetValue(null, FormatterServices.GetUninitializedObject(typeof(ZNetScene)));
                world.SetValue(null, FormatterServices.GetUninitializedObject(typeof(ZDOMan)));
                hasRules.SetValue(null, false); // Clients still need both RPCs with no local rules.
                foreach (bool server in new[] { false, true })
                {
                    active.SetValue(null, true);
                    dungeonNetEnabled = dungeonSceneEnabled = true;
                    var current = new ZRoutedRpc(server);
                    var table = (System.Collections.IDictionary)handlers.GetValue(current);
                    tick.Invoke(null, null);
                    Check(table.Count == 2 && table.Contains(request) && table.Contains(reply), "dungeon registers request/reply with no rules, server=" + server);
                    object requestHandler = table[request], replyHandler = table[reply];
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        reset.Invoke(null, null);
                        tick.Invoke(null, null);
                        tick.Invoke(null, null);
                    }
                    Check(table.Count == 2 && ReferenceEquals(table[request], requestHandler) && ReferenceEquals(table[reply], replyHandler),
                        "world reset/repeated tick retains original handlers without duplicate registration, server=" + server);

                    current = new ZRoutedRpc(server);
                    table = (System.Collections.IDictionary)handlers.GetValue(current);
                    dungeonNetEnabled = false;
                    tick.Invoke(null, null);
                    Check(table.Count == 0, "shutdown ZNet cannot register dungeon RPCs, server=" + server);
                    dungeonNetEnabled = true; dungeonSceneEnabled = false;
                    tick.Invoke(null, null);
                    Check(table.Count == 0, "shutdown ZNetScene cannot register dungeon RPCs, server=" + server);
                    dungeonSceneEnabled = true;
                    tick.Invoke(null, null);
                    Check(table.Count == 2 && table.Contains(request) && table.Contains(reply) && ReferenceEquals(registered.GetValue(null), current),
                        "new active RPC instance registers both handlers after reconnect, server=" + server);

                    dungeon.GetMethod("Dispose", All).Invoke(null, null);
                    tick.Invoke(null, null);
                    Check(!(bool)active.GetValue(null) && table.Count == 2, "disposed dungeon manager stays inactive without deleting RPC handlers");
                    active.SetValue(null, true);
                    tick.Invoke(null, null);
                    Check(table.Count == 2, "reactivation on the same RPC instance does not register twice");
                }
            }
            finally
            {
                fixture.UnpatchSelf();
                for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, saved[i]);
            }
        }

        private static bool DungeonFixtureEnabled(UnityEngine.Behaviour value) => value is ZNet ? dungeonNetEnabled : dungeonSceneEnabled;

        private static IEnumerable<CodeInstruction> IsolateDungeonSceneCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.operand is MethodInfo method)
                {
                    if (method.DeclaringType == typeof(UnityEngine.Object) && method.Name == "op_Equality")
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(object), nameof(ReferenceEquals));
                    }
                    else if (method.DeclaringType == typeof(UnityEngine.Behaviour) && method.Name == "get_enabled")
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(CompatibilityProbe), nameof(DungeonFixtureEnabled));
                    }
                }
                yield return instruction;
            }
        }

        private static Action<Ragdoll> instantLootBody;
        private static int instantVanillaCalls;
        private static int epicLootCalls;
        private static int epicLuckyRolls;

        private static void CheckEpicLootInstantCompatibility(Assembly mod, string epicPath)
        {
            Type integration = mod.GetType("DropNSpawn.RagdollSetupMonsterInstantLootDropPatch", true);
            MethodInfo configure = integration.GetMethod("ConfigureEpicLootCompatibility", All);
            MethodInfo early = integration.GetMethod("SpawnInstantLoot", All);
            FieldInfo enabled = integration.GetField("_instantLootCompatible", All);
            FieldInfo scope = integration.GetField("_instantLootRagdoll", All);
            object previousEnabled = enabled.GetValue(null);
            var guard = new Harmony("DropNSpawn.isolated.epic.guard");
            var fixture = new Harmony("DropNSpawn.isolated.epic.fixture");
            MethodInfo spawn = AccessTools.Method(typeof(Ragdoll), "SpawnLoot", new[] { typeof(UnityEngine.Vector3) });
            var normal = AccessTools.MethodDelegate<Action<Ragdoll, UnityEngine.Vector3>>(spawn);
            var center = new UnityEngine.Vector3(1f, 2f, 3f);
            void Configure(bool present, Assembly assembly) => configure.Invoke(null, new object[] { guard, present, assembly });
            void Early(Ragdoll ragdoll) => early.Invoke(null, new object[] { ragdoll, center });
            try
            {
                Configure(false, null);
                Check((bool)enabled.GetValue(null), "EpicLoot absent keeps instant loot available without loading it");
                Configure(true, null);
                Check(!(bool)enabled.GetValue(null), "present but unavailable EpicLoot fails closed");
                Configure(true, typeof(CompatibilityProbe).Assembly);
                Check(!(bool)enabled.GetValue(null), "missing EpicLoot callback falls back to normal loot timing");
                // The actual Setup postfix must return before scene/config access on fallback.
                integration.GetMethod("Postfix", All).Invoke(null, new object[] { null, null, null });
                Check(true, "unavailable guard bypasses the actual DNS immediate-loot entry");
                if (!File.Exists(epicPath)) return;

                Assembly epic = Assembly.LoadFrom(epicPath);
                MethodInfo callback = epic.GetType("EpicLoot.Ragdoll_SpawnLoot_Patch", true).GetMethod("Postfix", All);
                Configure(true, epic);
                Check(!(bool)enabled.GetValue(null), "matching but unregistered EpicLoot callback is not accepted");
                fixture.Patch(spawn,
                    prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolateRagdollLootBody)),
                    postfix: new HarmonyMethod(callback));
                // JIT the unmodified callback in the parent wrapper first. Missing
                // metadata keeps this warmup off the native item-creation path.
                Ragdoll unprepared = EpicRagdoll(94000, saveName: false);
                normal(unprepared, center);
                Check(true, "unmodified EpicLoot callback runs before DNS guard installation");
                // Keep that callback's body, replacing only item creation. Real
                // Ragdoll/ZDO reads and the existing parent wrapper still execute.
                fixture.Patch(callback, transpiler: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolateEpicLootSpawning)));
                Ragdoll outer = EpicRagdoll(94001), other = EpicRagdoll(94002);
                instantVanillaCalls = epicLootCalls = epicLuckyRolls = 0;
                // Warm up the existing parent wrapper before installing the callback guard.
                normal(outer, center);
                Check(epicLootCalls == 1 && epicLuckyRolls == 3, "actual EpicLoot callback rolls after skipped vanilla body");
                Configure(true, epic);
                Check((bool)enabled.GetValue(null), "verified EpicLoot callback guard installs under game Mono");
                Configure(true, epic);
                Check(Harmony.GetPatchInfo(callback).Prefixes.Count(p => p.owner == guard.Id) == 1, "reinitialization does not duplicate the guard");
                Early(outer);
                Check(instantVanillaCalls == 2 && epicLootCalls == 1 && epicLuckyRolls == 3,
                    "DNS early call keeps ordinary path but suppresses both EpicLoot rolls in the warmed wrapper");
                normal(outer, center);
                Check(epicLootCalls == 2 && epicLuckyRolls == 6, "normal cleanup still executes EpicLoot and LuckyLoot once");
                Check(scope.GetValue(null) == null, "normal early return restores scope");

                int before = epicLootCalls;
                instantLootBody = ragdoll =>
                {
                    if (!ReferenceEquals(ragdoll, outer)) return;
                    normal(other, center);
                    Check(epicLootCalls == before + 1, "unrelated ragdoll remains eligible inside early scope");
                    Early(other);
                    Check(ReferenceEquals(scope.GetValue(null), outer), "nested early call restores the outer ragdoll");
                };
                Early(outer);
                Check(epicLootCalls == before + 1 && scope.GetValue(null) == null, "nested early calls do not leak or duplicate EpicLoot rolls");

                instantLootBody = ragdoll =>
                {
                    if (ReferenceEquals(ragdoll, other)) throw new InvalidOperationException("instant fixture failure");
                    try { Early(other); throw new Exception("Expected nested failure"); }
                    catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
                    Check(ReferenceEquals(scope.GetValue(null), outer), "nested failure restores outer scope");
                };
                Early(outer);
                try { Early(other); throw new Exception("Expected early failure"); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
                Check(scope.GetValue(null) == null, "early exception propagates but clears the guard scope");
                instantLootBody = null;
                before = epicLootCalls;
                normal(outer, center);
                Check(epicLootCalls == before + 1, "normal EpicLoot path remains usable after an early exception");
                ZDO data = (ZDO)AccessTools.Field(typeof(ZNetView), "m_zdo").GetValue(AccessTools.Field(typeof(Ragdoll), "m_nview").GetValue(outer));
                Check(data.GetString("characterName", "") == "Eikthyr" && data.GetInt("level", 0) == 2 && data.GetInt("el-lky-rolls", 0) == 3,
                    "integration preserves EpicLoot saved metadata and bonus rolls");
                System.Console.WriteLine("EpicLoot instant compatibility: actual callback/parent wrapper, early vs delayed, independent/nested ragdolls, exceptions and fallback passed. Item creation and vanilla body substituted; no scene/network.");
            }
            finally
            {
                instantLootBody = null;
                guard.UnpatchSelf();
                fixture.UnpatchSelf();
                enabled.SetValue(null, previousEnabled);
                scope.SetValue(null, null);
            }
        }

        private static Ragdoll EpicRagdoll(uint id, bool saveName = true)
        {
            var ragdoll = (Ragdoll)FormatterServices.GetUninitializedObject(typeof(Ragdoll));
            var view = (ZNetView)FormatterServices.GetUninitializedObject(typeof(ZNetView));
            var data = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
            data.m_uid = new ZDOID(9812346L, id);
            AccessTools.Field(typeof(Ragdoll), "m_nview").SetValue(ragdoll, view);
            AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, data);
            if (saveName)
                AccessTools.Method(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(string) })
                    .Invoke(null, new object[] { data.m_uid, "characterName".GetStableHashCode(), "Eikthyr" });
            MethodInfo setInt = AccessTools.Method(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(int) });
            setInt.Invoke(null, new object[] { data.m_uid, "level".GetStableHashCode(), 2 });
            setInt.Invoke(null, new object[] { data.m_uid, "el-lky-rolls".GetStableHashCode(), 3 });
            return ragdoll;
        }

        private static bool IsolateRagdollLootBody(Ragdoll __instance)
        {
            instantVanillaCalls++;
            instantLootBody?.Invoke(__instance);
            return false;
        }

        private static void ObserveEpicLootRoll(string name, int level, UnityEngine.Vector3 point) { epicLootCalls++; }
        private static void ObserveLuckyRolls(string name, int level, UnityEngine.Vector3 point, int count) { epicLuckyRolls += count; }
        private static IEnumerable<CodeInstruction> IsolateEpicLootSpawning(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.operand is MethodInfo method)
                {
                    string replacement = method.DeclaringType.FullName == "EpicLoot.EpicLootDropsHelper" && method.Name == "OnCharacterDeath"
                        ? nameof(ObserveEpicLootRoll)
                        : method.DeclaringType.FullName == "EpicLoot.MagicItemEffects.Shards.LuckyLoot" && method.Name == "RollBonusEpicLootDrops"
                        ? nameof(ObserveLuckyRolls) : null;
                    if (replacement != null) instruction.operand = typeof(CompatibilityProbe).GetMethod(replacement, All);
                }
                yield return instruction;
            }
        }

        private static Character attributionTarget;
        private static Character attributionAttacker;
        private static Character attributionPlayer;
        private static Type attributionFilter;
        private static float attributionHealth;
        private static int attributionIdentityReads;

        private static void CheckKillerAttribution(Assembly mod)
        {
            Type type = mod.GetType("DropNSpawn.CharacterDropKillerFilter", true);
            attributionFilter = type;
            Type outcome = type.GetNestedType("Attribution", All);
            object allowed = Enum.Parse(outcome, "Allowed");
            object rejected = Enum.Parse(outcome, "Rejected");
            object unknown = Enum.Parse(outcome, "Unknown");
            object Call(string name, params object[] args) => type.GetMethod(name, All).Invoke(null, args);
            object Read(object value, string name) => value.GetType().GetProperty(name, All).GetValue(value);
            var isolation = new Harmony("DropNSpawn.isolated.dot");
            try
            {
                // Execute the shipped methods; Unity identity/health/source lookup and the
                // enabled setting are fixtures. No game assemblies or source algorithms are copied.
                foreach (MethodInfo method in type.GetMethods(All | BindingFlags.DeclaredOnly).Where(m => m.IsStatic &&
                    m.Name != "ClassifySource" && m.Name != "GetPrefabName" && m.Name != "BeginDeathScope" && m.Name != "ShouldSuppressDeathCallback"))
                    isolation.Patch(method, transpiler: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolateAttributionCalls)));
                isolation.Patch(type.GetMethod("EnsureEnabled", All),
                    prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(AttributionEnabled)));
                attributionTarget = (Character)FormatterServices.GetUninitializedObject(typeof(Character));
                var player = (Character)FormatterServices.GetUninitializedObject(typeof(Character));
                attributionPlayer = player;
                var hostile = (Character)FormatterServices.GetUninitializedObject(typeof(Character));
                hostile.m_faction = Character.Faction.ForestMonsters;
                var summon = (Character)FormatterServices.GetUninitializedObject(typeof(Character));
                summon.m_faction = Character.Faction.PlayerSpawned;
                var poison = (SE_Poison)FormatterServices.GetUninitializedObject(typeof(SE_Poison));
                poison.m_character = attributionTarget;
                var burning = (SE_Burning)FormatterServices.GetUninitializedObject(typeof(SE_Burning));
                burning.m_character = attributionTarget;
                attributionHealth = 100f;
                void Source(Character source, string method, StatusEffect status, params object[] rest)
                {
                    attributionAttacker = source;
                    object scope = Call("BeginRpcDamageScope", attributionTarget, new HitData());
                    try { Call(method, new object[] { status }.Concat(rest).ToArray()); }
                    finally { Call("EndRpcDamageScope", scope); }
                }
                object Tick(StatusEffect status, HitData hit)
                {
                    object scope = Call(status is SE_Poison ? "BeginPoisonDamageTick" : "BeginBurningDamageTick", status);
                    try { return Call("BeginApplyDamage", attributionTarget, hit); }
                    finally { Call("EndDelayedDamageTick", scope); }
                }
                var poisonHit = new HitData { m_hitType = HitData.HitType.Poisoned };
                poisonHit.m_damage.m_poison = 5f;
                var fireHit = new HitData { m_hitType = HitData.HitType.Burning };
                fireHit.m_damage.m_fire = 5f;
                Check(Call("MergeAttribution", allowed, rejected).Equals(allowed) &&
                      Call("MergeAttribution", unknown, allowed).Equals(allowed), "known contribution survives hostile/unknown mixture");
                Check((bool)Call("ShouldSuppress", true, false, false, Call("MergeAttribution", unknown, rejected)), "unknown/hostile alone stays denied");
                Source(player, "RecordPoisonSource", poison, true);
                typeof(SE_Poison).GetField("m_damageLeft", All).SetValue(poison, 10f);
                bool weaker = (bool)Call("WillPoisonReplaceCurrentPool", poison, 4f);
                Source(hostile, "RecordPoisonSource", poison, weaker);
                Check(Read(Tick(poison, poisonHit), "Attribution").Equals(allowed), "rejected weaker poison preserves player's pool");
                Source(hostile, "RecordPoisonSource", poison, (bool)Call("WillPoisonReplaceCurrentPool", poison, 12f));
                Check(Read(Tick(poison, poisonHit), "Attribution").Equals(rejected), "replacement poison discards past player contribution");
                Source(summon, "RecordPoisonSource", poison, true);
                typeof(SE_Poison).GetField("m_damageLeft", All).SetValue(poison, 0f);
                Check(Read(Tick(poison, poisonHit), "Attribution").Equals(allowed), "PlayerSpawned last poison tick still qualifies at zero remaining damage");

                attributionIdentityReads = 0;
                for (int i = 0; i < 100; i++)
                    Call("EndDelayedDamageTick", Call("BeginPoisonDamageTick", poison));
                Check(attributionIdentityReads == 0, "100 non-damaging status updates perform zero ledger identity lookups");
                object frozen = Tick(poison, poisonHit);
                Check(attributionIdentityReads == 1, "actual DoT hit performs one ledger identity lookup");
                Source(hostile, "RecordPoisonSource", poison, true);
                // Nested/unrelated scopes and later source replacement cannot change this hit.
                object outer = Call("BeginPoisonDamageTick", poison);
                try
                {
                    object inner = Tick(burning, fireHit);
                    object restored = Call("BeginApplyDamage", attributionTarget, poisonHit);
                    Check(Read(inner, "Attribution").Equals(unknown) && (bool)Read(restored, "Delayed") &&
                          Read(restored, "Attribution").Equals(rejected), "nested burning scope restores outer poison attribution");
                }
                finally { Call("EndDelayedDamageTick", outer); }
                attributionHealth = 0f;
                typeof(Character).GetField("m_lastHit", All).SetValue(attributionTarget, poisonHit);
                Call("CompleteApplyDamage", attributionTarget, poisonHit, frozen);
                Check(Call("GetPendingDeathAttribution", attributionTarget).Equals(allowed), "lethal credit consumes the pre-damage snapshot");
                attributionHealth = 100f;
                Call("ClearRecoveredDeathCredit", attributionTarget);
                Check(Call("GetPendingDeathAttribution", attributionTarget).Equals(unknown), "recovery clears lethal credit");

                Source(hostile, "RecordFireSource", burning, true, true);
                Source(player, "RecordSpiritSource", burning, true, true);
                Check(Read(Tick(burning, fireHit), "Attribution").Equals(rejected), "fire-only hit cannot borrow an unrelated player's spirit pool");
                Source(player, "RecordFireSource", burning, false, true);
                Check(Read(Tick(burning, fireHit), "Attribution").Equals(allowed), "lethal mixed fire retains a confirmed contribution");
                Source(hostile, "RecordFireSource", burning, true, true);
                Check(Read(Tick(burning, fireHit), "Attribution").Equals(rejected), "empty/restarted fire pool forgets previous contributions");
                var replacement = (SE_Burning)FormatterServices.GetUninitializedObject(typeof(SE_Burning));
                replacement.m_character = attributionTarget;
                Check(Read(Tick(replacement, fireHit), "Attribution").Equals(unknown), "replacement status cannot inherit stale evidence");
                var direct = new HitData();
                attributionAttacker = hostile;
                object directState = Call("BeginApplyDamage", attributionTarget, direct);
                attributionHealth = 0f;
                typeof(Character).GetField("m_lastHit", All).SetValue(attributionTarget, direct);
                Call("CompleteApplyDamage", attributionTarget, direct, directState);
                Check(Call("GetPendingDeathAttribution", attributionTarget).Equals(rejected), "direct hostile lethal hit ignores other active player DoT");
                Call("ForgetCharacter", attributionTarget);
                Check(Call("GetPendingDeathAttribution", attributionTarget).Equals(unknown), "destroy/death cleanup removes attribution");
            }
            finally
            {
                ((System.Collections.IDictionary)type.GetField("DelayedDamageLedgers", All).GetValue(null)).Clear();
                ((System.Collections.IDictionary)type.GetField("PendingDeathCredits", All).GetValue(null)).Clear();
                isolation.UnpatchSelf();
                attributionTarget = attributionAttacker = attributionPlayer = null;
            }
        }

        private static bool AttributionEnabled(ref bool __result) { __result = true; return false; }
        private static int AttributionIdentity(UnityEngine.Object value) { attributionIdentityReads++; return RuntimeHelpers.GetHashCode(value); }
        private static bool AttributionEqual(UnityEngine.Object a, UnityEngine.Object b) => ReferenceEquals(a, b);
        private static bool AttributionDifferent(UnityEngine.Object a, UnityEngine.Object b) => !ReferenceEquals(a, b);
        private static float AttributionHealth(Character value) => ReferenceEquals(value, attributionTarget) ? attributionHealth : 100f;
        private static bool AttributionPlayer(Character value) => ReferenceEquals(value, attributionPlayer);
        private static bool AttributionTamed(Character value) => false;
        private static Character AttributionAttacker(HitData hit) => attributionAttacker;
        private static int AttributionSource(HitData hit, Character target) => (int)attributionFilter.GetMethod("ClassifySourceSnapshot", All).Invoke(null,
            new object[] { ReferenceEquals(attributionAttacker, null), ReferenceEquals(attributionAttacker, target),
                AttributionPlayer(attributionAttacker), false, attributionAttacker.m_faction });
        private static IEnumerable<CodeInstruction> IsolateAttributionCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.operand is MethodInfo method)
                {
                    string replacement = method.DeclaringType == typeof(UnityEngine.Object)
                        ? method.Name == "GetInstanceID" ? nameof(AttributionIdentity) :
                          method.Name == "op_Equality" ? nameof(AttributionEqual) :
                          method.Name == "op_Inequality" ? nameof(AttributionDifferent) : null
                        : method.DeclaringType == typeof(Character)
                        ? method.Name == "GetHealth" ? nameof(AttributionHealth) :
                          method.Name == "IsPlayer" ? nameof(AttributionPlayer) :
                          method.Name == "IsTamed" ? nameof(AttributionTamed) : null
                        : method.DeclaringType == typeof(HitData) && method.Name == "GetAttacker" ? nameof(AttributionAttacker)
                        : method.DeclaringType == attributionFilter && method.Name == "ClassifySource" ? nameof(AttributionSource) : null;
                    if (replacement != null) { instruction.opcode = OpCodes.Call; instruction.operand = typeof(CompatibilityProbe).GetMethod(replacement, All); }
                }
                yield return instruction;
            }
        }

        private static void CheckCreatureSpawnerLimits(Assembly mod)
        {
            var production = new Harmony("DropNSpawn.isolated.spawner.limit");
            var isolation = new Harmony("DropNSpawn.isolated.spawner.fixtures");
            spawnerLimitManager = mod.GetType("DropNSpawn.SpawnerManager", true);
            try
            {
                production.CreateClassProcessor(mod.GetType("DropNSpawn.CreatureSpawnerSpawnPatch", true)).Patch();
                Check(true, "CreatureSpawner Spawn prefix/postfix including state and runOriginal install under Mono");
                production.UnpatchSelf();
                production.CreateClassProcessor(mod.GetType("DropNSpawn.CreatureSpawnerGroupSpawnPatch", true)).Patch();

                // Run the real group loop + production transpiler. Only native RNG,
                // scene/config-dependent candidate setup and Instantiate are replaced.
                isolation.Patch(spawnerLimitManager.GetMethod("IsCreatureSpawnerGroupCandidate", All),
                    prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolatedGroupCandidate)));
                isolation.Patch(AccessTools.Method(typeof(CreatureSpawner), "Spawn"),
                    prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolatedGroupSpawn)));
                isolation.Patch(AccessTools.Method(typeof(CreatureSpawner.Group), "SpawnWeighted"),
                    transpiler: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolateGroupNativeCalls)));

                var exhausted = (CreatureSpawner)FormatterServices.GetUninitializedObject(typeof(CreatureSpawner));
                var eligible = (CreatureSpawner)FormatterServices.GetUninitializedObject(typeof(CreatureSpawner));
                var view = (ZNetView)FormatterServices.GetUninitializedObject(typeof(ZNetView));
                AccessTools.Field(typeof(CreatureSpawner), "m_nview").SetValue(eligible, view);
                var netView = (AccessTools.FieldRef<CreatureSpawner, ZNetView>)spawnerLimitManager.GetField("CreatureSpawnerNetView", All).GetValue(null);
                Check(ReferenceEquals(netView(eligible), view), "cached accessor reads the original private CreatureSpawner network view under Mono");
                exhausted.m_spawnerWeight = 100f;
                eligible.m_spawnerWeight = 1f;
                int countKey = (int)spawnerLimitManager.GetField("CreatureSpawnerTotalSpawnCountZdoKey", All).GetValue(null);
                MethodInfo setInt = AccessTools.Method(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(int) });
                foreach (var pair in new[] { new KeyValuePair<CreatureSpawner, uint>(exhausted, 92001), new KeyValuePair<CreatureSpawner, uint>(eligible, 92002) })
                {
                    var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
                    zdo.m_uid = new ZDOID(9812346L, pair.Value);
                    AccessTools.Property(typeof(ZDO), "Owner").SetValue(zdo, true);
                    groupCounters.Add(pair.Key, zdo);
                }
                setInt.Invoke(null, new object[] { groupCounters[exhausted].m_uid, countKey, 1 });
                var group = new CreatureSpawner.Group();
                typeof(HashSet<CreatureSpawner>).GetFields(All)
                    .Single(field => field.FieldType == typeof(IEqualityComparer<CreatureSpawner>))
                    .SetValue(group, new SpawnerFixtureComparer());
                group.Add(exhausted);
                group.Add(eligible);
                Check(group.Count == 2, "two distinct managed spawner fixtures in the native group");
                group.SpawnWeighted();
                Check(ReferenceEquals(selectedGroupSpawner, eligible), "exhausted high-weight member neither spawns nor consumes another member's probability");
                selectedGroupSpawner = null;
                setInt.Invoke(null, new object[] { groupCounters[eligible].m_uid, countKey, 1 });
                group.SpawnWeighted();
                Check(ReferenceEquals(selectedGroupSpawner, null), "group with all limits exhausted selects no spawner");
                setInt.Invoke(null, new object[] { groupCounters[exhausted].m_uid, countKey, 0 });
                group.SpawnWeighted();
                Check(ReferenceEquals(selectedGroupSpawner, exhausted), "eligibility is reevaluated on the next group attempt");
            }
            finally
            {
                isolation.UnpatchSelf();
                production.UnpatchSelf();
                groupCounters.Clear();
                selectedGroupSpawner = null;
                spawnerLimitManager = null;
            }
        }

        private static bool IsolatedGroupCandidate(CreatureSpawner spawner, ref bool __result)
        {
            __result = (bool)spawnerLimitManager.GetMethod("CanCreatureSpawnerSpawnWithLimit", All).Invoke(null, new object[] { groupCounters[spawner], 1 });
            return false;
        }

        private static bool IsolatedGroupSpawn(CreatureSpawner __instance, ref ZNetView __result)
        {
            selectedGroupSpawner = __instance;
            __result = null;
            return false;
        }

        private static IEnumerable<CodeInstruction> IsolateGroupNativeCalls(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(UnityEngine.Random) && method.Name == "Range")
                    instruction.operand = AccessTools.Method(typeof(CompatibilityProbe), nameof(FirstWeight));
                else if (instruction.operand is MethodInfo log && log.DeclaringType == typeof(ZLog) && log.Name == "LogError")
                    instruction.operand = AccessTools.Method(typeof(CompatibilityProbe), nameof(IgnoreEmptyGroupLog));
                yield return instruction;
            }
        }

        private static float FirstWeight(float min, float max) => min;
        private static void IgnoreEmptyGroupLog(object message) { }

        private static void CheckStandalone(Assembly mod)
        {
            var harmony = new Harmony("DropNSpawn.isolated.standalone");
            foreach (Type type in mod.GetTypes()) harmony.CreateClassProcessor(type);
            Check(true, "all DNS types and Harmony class processors load without EWD");
            Type compatibility = mod.GetType("DropNSpawn.ExpandWorldDataCompatibility", true);
            compatibility.GetMethod("Initialize", All).Invoke(null, new object[] { harmony });
            Check(!(bool)compatibility.GetProperty("IsAvailable", All).GetValue(null), "standalone initialization skips EWD");
            Type support = mod.GetType("DropNSpawn.SpawnSystemCustomDataSupport", true);
            Type entryType = mod.GetType("DropNSpawn.CanonicalSpawnSystemEntry", true);
            Type definitionType = mod.GetType("DropNSpawn.SpawnSystemSpawnDefinition", true);
            object entry = Activator.CreateInstance(entryType, true);
            object definition = Activator.CreateInstance(definitionType, true);
            entryType.GetProperty("SpawnSystem").SetValue(entry, definition);
            definitionType.GetProperty("Faction").SetValue(definition, "ForestMonsters");
            var spawn = new SpawnSystem.SpawnData();
            object payload = support.GetMethod("BuildPreparedPayload", All).Invoke(null, new object[] { spawn, entry, "Mono standalone" });
            Check(payload != null, "standalone faction payload builds under Mono JIT");
            support.GetMethod("ApplyPreparedPayload", All).Invoke(null, new[] { (object)spawn, payload });
            foreach (string name in new[] { "InitializeSpawn", "SpawnObjects" })
                support.GetMethod(name, All).Invoke(null, new object[] { spawn, default(UnityEngine.Vector3) });
            Type spawner = mod.GetType("DropNSpawn.ExpandWorldSpawnDataSupport", true);
            Check(spawner.GetMethod("BuildPayload", All).Invoke(null, new object[] { null, null, null, null, "Mono standalone" }) == null, "standalone spawner builds without resolving EWD");
            Type spawnPatch = mod.GetType("DropNSpawn.SpawnSystemSpawnPatch", true);
            try
            {
                harmony.CreateClassProcessor(spawnPatch).Patch();
                Check(true, "standalone spawn Harmony patch installs on original game IL");
            }
            finally { harmony.UnpatchSelf(); }
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "ExpandWorldData"), "no EWD assembly loaded by standalone paths");
        }

        private static void CheckEwdCompatibility(Assembly mod)
        {
            Assembly ewd = Assembly.Load("ExpandWorldData");
            Type spawnPatcher = ewd.GetType("ExpandWorld.Spawn.Patcher");
            if (spawnPatcher == null) return;
            Type eventPatcher = ewd.GetType("ExpandWorld.Event.Patcher", true);
            Type configType = ewd.GetType("ExpandWorldData.Configuration", true);
            string configPath = Path.Combine(Path.GetTempPath(), "dns-ewd-mono-" + Guid.NewGuid().ToString("N") + ".cfg");
            var config = new ConfigFile(configPath, false) { SaveOnConfigSet = false };
            string[] switches = { "configDataSpawns", "configDataEvents", "configDataDrops", "configMultipleEvents", "configCheckPerPlayer" };
            foreach (string name in switches)
                configType.GetField(name).SetValue(null, config.Bind("test", name, true, ""));
            var upstream = new Harmony("DropNSpawn.isolated.ewd");
            var compatibility = new Harmony("DropNSpawn.isolated.ewd.compatibility");
            var isolation = new Harmony("DropNSpawn.isolated.ewd.scene-fixture");
            MethodInfo spawn = spawnPatcher.GetMethod("Patch");
            MethodInfo events = eventPatcher.GetMethod("Patch");
            Type compat = mod.GetType("DropNSpawn.ExpandWorldDataCompatibility", true);
            bool syncedManagers = ewd.GetType("ExpandWorld.Spawn.SpawnManager") != null;
            MethodInfo drops = ewd.GetType("ExpandWorld.Drops.Patcher")?.GetMethod("Patch");
            MethodInfo registry = ewd.GetType("ExpandWorldData.Patches")?.GetMethod("Apply");
            Type accelerator = null;
            HashSet<string> passthrough = null;
            string[] savedPassthrough = null;
            try
            {
                if (syncedManagers)
                {
                    // The new patcher queries native ZNet/Object state even when
                    // features are off. Only the server/client role is a fixture.
                    ewdServer = true;
                    isolation.Patch(ewd.GetType("ExpandWorldData.Helper", true).GetMethod("IsServer"),
                        prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(EwdServerFixture)));
                }
                string acceleratorPath = Path.Combine(Path.GetDirectoryName(typeof(CompatibilityProbe).Assembly.Location), "StartupAccelerator.dll");
                if (File.Exists(acceleratorPath))
                {
                    // Use the actual optional preloader's interceptor. Its config and
                    // dependencies are confined to this disposable BepInEx test root.
                    Type startup = Assembly.LoadFrom(acceleratorPath).GetType("StartupAccelerator.StartupAccelerator", true);
                    accelerator = startup.GetNestedType("InterceptChainloader", All);
                    passthrough = (HashSet<string>)startup.GetField("passthroughClasses", All).GetValue(null);
                    passthrough.Add("DNS.test.user.setting");
                    passthrough.Add("ExpandWorld.Spawn.Patcher"); // preexisting legacy gate exemption
                    savedPassthrough = passthrough.ToArray();
                    accelerator.GetMethod("Prefix", All).Invoke(null, null);
                }
                // Reproduce EWD.Awake-before-DNS order, including already JITted gates.
                // No RandEventSystem scene exists here; avoid its native null check
                // during the unpatched spawn patcher's active-event enumeration.
                var eventSwitch = (ConfigEntry<bool>)configType.GetField("configDataEvents").GetValue(null);
                eventSwitch.Value = false;
                var multipleSwitch = (ConfigEntry<bool>)configType.GetField("configMultipleEvents").GetValue(null);
                var perPlayerSwitch = (ConfigEntry<bool>)configType.GetField("configCheckPerPlayer").GetValue(null);
                multipleSwitch.Value = perPlayerSwitch.Value = false;
                spawn.Invoke(null, new object[] { upstream });
                events.Invoke(null, new object[] { upstream });
                drops?.Invoke(null, new object[] { upstream });
                Check(HasOwner(typeof(SpawnSystem), "Awake", upstream.Id), "EWD normal spawn lifecycle initially enabled");
                bool testFailure = File.Exists(Path.Combine(Path.GetDirectoryName(typeof(CompatibilityProbe).Assembly.Location), "ewd-handoff-failure"));
                if (testFailure)
                {
                    Check(syncedManagers, "rollback fixture requires EWD synced-manager lifecycle");
                    // Deliberately add an EWD-owned callback outside its registry so
                    // the ownership guard must reject the handoff after refreshing.
                    upstream.Patch(AccessTools.Method(typeof(CompatibilityProbe), nameof(RegistrySentinel)),
                        prefix: new HarmonyMethod(ewd.GetType("ExpandWorld.Event.EventManager", true).GetMethod("DelayClientLoad", All)));
                    bool rejected = false;
                    try { compat.GetMethod("InstallPatches", All).Invoke(null, new object[] { compatibility, ewd, upstream }); }
                    catch (TargetInvocationException error)
                    {
                        rejected = error.InnerException is InvalidOperationException && error.InnerException.Message.Contains("EWD still owns");
                    }
                    Check(rejected, "unregistered owned callback still rejects unsafe handoff");
                    Check(passthrough.SetEquals(savedPassthrough), "failed handoff restores user passthrough entries");
                    upstream.Unpatch(AccessTools.Method(typeof(CompatibilityProbe), nameof(RegistrySentinel)), HarmonyPatchType.All, upstream.Id);
                    Check(!Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method).Owners.Contains(compatibility.Id)), "failed handoff removes all DNS compatibility callbacks");
                    Check(HasOwner(typeof(SpawnSystem), "Awake", upstream.Id) && HasOwner(typeof(ZNet), "Awake", upstream.Id), "failed handoff restores EWD lifecycle registration");
                    FlushStartupAccelerator(accelerator);
                    Check(!Harmony.GetAllPatchedMethods().Any(method => Harmony.GetPatchInfo(method).Owners.Contains(compatibility.Id)), "deferred flush cannot resurrect failed DNS compatibility callbacks");
                    Check(HasOwner(typeof(ZNet), "Awake", upstream.Id), "deferred flush preserves restored EWD ownership");
                    return;
                }
                // Installing the upstream scheduler needs Player/Animator native
                // initialization. Verify its disabled gates here; removal of an
                // already-installed scheduler remains an in-game check.
                eventSwitch.Value = true;
                multipleSwitch.Value = perPlayerSwitch.Value = true;
                // Supply the EWD Harmony instance directly: full EWD/ServerSync
                // startup needs Unity and is intentionally outside this probe.
                compat.GetMethod("InstallPatches", All).Invoke(null, new object[] { compatibility, ewd, upstream });
                if (accelerator != null)
                {
                    Check(passthrough.SetEquals(savedPassthrough), "successful handoff restores user passthrough entries");
                    int calls = postfixCalls;
                    isolation.Patch(AccessTools.Method(typeof(CompatibilityProbe), nameof(RegistrySentinel)),
                        postfix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(ObserveEvent)));
                    RegistrySentinel();
                    Check(postfixCalls == calls, "unrelated patches remain deferred after EWD handoff");
                    FlushStartupAccelerator(accelerator);
                    RegistrySentinel();
                    Check(postfixCalls == calls + 1, "queued unrelated wrapper applies at batch flush");
                    Check(!HasOwner(typeof(ZNet), "Awake", upstream.Id), "batch flush cannot resurrect removed EWD ownership");
                }
                Check(!HasOwner(typeof(SpawnSystem), "Awake", upstream.Id), "DNS removes already-installed EWD spawn lifecycle");
                Check(!HasOwner(typeof(RandEventSystem), "FixedUpdate", upstream.Id), "DNS keeps EWD scheduler disabled despite saved true setting");
                spawn.Invoke(null, new object[] { upstream });
                events.Invoke(null, new object[] { upstream });
                drops?.Invoke(null, new object[] { upstream });
                Check(!HasOwner(typeof(SpawnSystem), "Awake", upstream.Id) && !HasOwner(typeof(RandEventSystem), "FixedUpdate", upstream.Id),
                    "EWD runtime patch refresh cannot reclaim DNS domains");
                if (syncedManagers)
                {
                    Check(!HasOwner(typeof(ZoneSystem), "Start", upstream.Id) && !HasOwner(typeof(RandEventSystem), "Awake", upstream.Id) &&
                        !HasOwner(typeof(ZNet), "Awake", upstream.Id), "EWD unconditional lifecycle hooks are removed");
                    // A non-owned callback must still pass through the shared registry.
                    registry.Invoke(null, new object[] { upstream, true, typeof(CompatibilityProbe), "RegistrySentinel",
                        typeof(CompatibilityProbe), "ObserveEvent", HarmonyPatchType.Postfix, 400, null, false, null, null });
                    Check(HasOwner(typeof(CompatibilityProbe), "RegistrySentinel", upstream.Id), "shared registry still installs non-owned patches");
                    // Nonempty tables must not let later refreshes reinstall loot,
                    // spawn customization or numeric-key consumption hooks either.
                    var spawnRow = new SpawnSystem.SpawnData { m_requiredGlobalKey = "fixture 1" };
                    ewd.GetType("ExpandWorld.Spawn.SpawnManager", true).GetField("Override").SetValue(null,
                        new List<SpawnSystem.SpawnData> { spawnRow });
                    var spawnData = (System.Collections.IDictionary)ewd.GetType("ExpandWorld.Spawn.Loader", true).GetField("Data").GetValue(null);
                    spawnData.Add(spawnRow, Activator.CreateInstance(ewd.GetType("Data.DataEntry", true)));
                    var dropData = (System.Collections.IDictionary)ewd.GetType("ExpandWorld.Drops.DropManager", true).GetField("DataByHash").GetValue(null);
                    dropData.Add(1, FormatterServices.GetUninitializedObject(ewd.GetType("ExpandWorld.Drops.Data", true)));
                    spawn.Invoke(null, new object[] { upstream });
                    drops.Invoke(null, new object[] { upstream });
                    Check(!HasOwner(typeof(SpawnSystem), "Spawn", upstream.Id) && !HasOwner(typeof(ZoneSystem), "RPC_SetGlobalKey", upstream.Id) &&
                        !HasOwner(typeof(CharacterDrop), "GenerateDropList", upstream.Id) && !HasOwner(typeof(Container), "AddDefaultItems", upstream.Id),
                        "nonempty EWD tables cannot reinstall spawn, key or drop hooks");
                    spawnData.Remove(spawnRow);
                    dropData.Clear();
                    ewd.GetType("ExpandWorld.Spawn.SpawnManager", true).GetField("Override").SetValue(null, null);
                    ewdServer = false;
                    events.Invoke(null, new object[] { upstream });
                    Check(!HasOwner(typeof(SpawnSystem), "Awake", upstream.Id) && !HasOwner(typeof(RandEventSystem), "Awake", upstream.Id),
                        "client-side EWD lifecycle refresh stays suppressed");
                }
                foreach (string domain in syncedManagers ? new[] { "Spawn", "Event", "Drops" } : new[] { "Spawn", "Event" })
                {
                    Type manager = ewd.GetType("ExpandWorld." + domain + "." + (syncedManagers ? (domain == "Drops" ? "Drop" : domain) : "") + "Manager", true);
                    if (syncedManagers)
                    {
                        foreach (string name in domain == "Spawn" ? new[] { "ReadConfigs", "CreateConfigs", "InitializeData" } :
                            domain == "Event" ? new[] { "ReadConfigs", "CreateConfigs", "DelayClientLoad", "InitializeServerData", "InitializeClientData" } :
                            new[] { "ReadConfigs", "ToReferenceFile", "InitializeData" })
                            manager.GetMethod(name, All).Invoke(null, null);
                        Check(!(bool)manager.GetMethod("Set", All).Invoke(null, new object[] { new Dictionary<string, string> { ["invalid.yaml"] = "invalid: [" } }),
                            "EWD " + domain + " Set rejects without publishing data");
                        if (domain == "Spawn") manager.GetMethod("InitializeSpawnSystem", All).Invoke(null, new object[] { null });
                    }
                    else
                    {
                        foreach (string name in new[] { "CreateConfig", "ReadConfig", "Toggle" })
                            manager.GetMethod(name, All).Invoke(null, null);
                        manager.GetMethod("Set", All).Invoke(null, new object[] { "invalid YAML: [" });
                    }
                    manager.GetMethod("FromSetting", All).Invoke(null, new object[] { "invalid YAML: [" });
                    if (domain != "Drops") manager.GetMethod(domain == "Spawn" ? "ApplySpawnData" : "ApplyTiming", All).Invoke(null, new object[] { null });
                    Check(true, "EWD " + domain + " direct/reload/sync entrypoints skipped without scene access");
                }
                Type dtoType = ewd.GetType("ExpandWorld.Spawn.Data", true);
                object dto = Activator.CreateInstance(dtoType);
                dtoType.GetField("drops").SetValue(dto, "test_drop");
                object customData = ewd.GetType("ExpandWorld.Spawn.LoaderFields", true).GetMethod("HandleCustomData")
                    .Invoke(null, new[] { dto, (object)new SpawnSystem.SpawnData() });
                Check(customData == null, "EWD drop conversion is disabled despite saved true setting");
                Check((string)dtoType.GetField("drops").GetValue(dto) == "test_drop", "EWD source drop definition is not mutated");
                dtoType.GetField("faction").SetValue(dto, "AnimalsVeg");
                customData = ewd.GetType("ExpandWorld.Spawn.LoaderFields", true).GetMethod("HandleCustomData")
                    .Invoke(null, new[] { dto, (object)new SpawnSystem.SpawnData() });
                Check(customData != null && customData.GetType().GetField("Hashes").GetValue(customData) == null &&
                    customData.GetType().GetField("Strings").GetValue(customData) != null, "EWD faction data survives while drop hashes stay absent");
                foreach (string name in switches)
                    Check(((ConfigEntry<bool>)configType.GetField(name).GetValue(null)).Value, "EWD user setting preserved: " + name);
                Check(!File.Exists(configPath), "EWD compatibility did not create/save config");
            }
            finally
            {
                compatibility.UnpatchSelf();
                upstream.UnpatchSelf();
                if (accelerator != null) StopStartupAccelerator(accelerator);
                isolation.UnpatchSelf();
            }
        }

        private static void StopStartupAccelerator(Type interceptor)
        {
            interceptor.GetField("doNotSkipUpdate", All).SetValue(null, true);
            ((Harmony)interceptor.DeclaringType.GetField("delayedPatcherHarmony", All).GetValue(null)).UnpatchSelf();
        }

        private static void FlushStartupAccelerator(Type interceptor)
        {
            // Match the installed preloader's latest-PatchInfo rebuild without its
            // Chainloader/FejdStartup hooks or a native Unity scene.
            StopStartupAccelerator(interceptor);
            var pending = (HashSet<MethodBase>)interceptor.GetField("methods", All).GetValue(null);
            Type manager = typeof(Harmony).Assembly.GetType("HarmonyLib.Public.Patching.PatchManager", true);
            MethodInfo update = (MethodInfo)interceptor.DeclaringType.GetField("harmonyPatcher", All).GetValue(null);
            foreach (MethodBase method in pending.ToArray())
            {
                object info = manager.GetMethod("ToPatchInfo", All).Invoke(null, new object[] { method });
                object replacement = update.Invoke(null, new[] { method, info });
                manager.GetMethod("AddReplacementOriginal", All).Invoke(null, new[] { method, replacement });
            }
            pending.Clear();
        }

        private static bool HasOwner(Type type, string method, string owner)
        {
            var info = Harmony.GetPatchInfo(AccessTools.Method(type, method));
            return info != null && info.Owners.Contains(owner);
        }

        private static void ObserveEvent() { postfixCalls++; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegistrySentinel() { }
        private static bool EwdServerFixture(ref bool __result) { __result = ewdServer; return false; }
        private static void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("FAIL: " + label);
            checks++;
        }
    }
}
