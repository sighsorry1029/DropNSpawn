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
            string testRoot = Path.GetDirectoryName(typeof(CompatibilityProbe).Assembly.Location);
            AccessTools.Method(typeof(BepInEx.Paths), "SetExecutablePath").Invoke(null,
                new object[] { Path.Combine(testRoot, "CompatibilityMonoHost.exe"), testRoot, managed, null });
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
            CheckSpawnSystemTimers(mod);
            ChanceScalingFixture.Run(mod);
            checks += LootPriorityProbe.Run(mod);
            DungeonPlacementFixture.Run(mod);
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
            if (File.Exists(ewdPath))
            {
                CheckEwdValueFactories(mod);
                EwdFailureFixture.Run(mod);
                CheckEwdCompatibility(mod);
            }
            else CheckStandalone(mod);
        }

        private static bool dungeonNetEnabled, dungeonSceneEnabled;

        private static void CheckSpawnSystemTimers(Assembly mod)
        {
            Type timers = mod.GetType("DropNSpawn.SpawnSystemTimers", true);
            Type entryType = mod.GetType("DropNSpawn.CanonicalSpawnSystemEntry", true);
            Type definitionType = mod.GetType("DropNSpawn.SpawnSystemSpawnDefinition", true);
            Type idType = timers.GetNestedType("TimerId", All);
            MethodInfo attach = timers.GetMethod("Attach", All), begin = timers.GetMethod("Begin", All), end = timers.GetMethod("End", All),
                read = timers.GetMethod("ReadTime", All), write = timers.GetMethod("WriteTime", All), elapsed = timers.GetMethod("TryGetElapsed", All);
            var entries = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
            foreach (float chance in new[] { 40f, 60f })
            {
                object entry = Activator.CreateInstance(entryType), definition = Activator.CreateInstance(definitionType);
                entryType.GetProperty("Prefab").SetValue(entry, "Gjall");
                definitionType.GetProperty("SpawnChance").SetValue(definition, chance);
                entryType.GetProperty("SpawnSystem").SetValue(entry, definition);
                entries.Add(entry);
            }
            Array ids = (Array)timers.GetMethod("CreateIds", All).Invoke(null, new object[] { entries });
            object configuredIds = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(idType), new object[] { ids });
            var rows = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(SpawnSystem.SpawnData), idType));
            SpawnSystem.SpawnData first = new SpawnSystem.SpawnData(), second = new SpawnSystem.SpawnData();
            rows.Add(first, ids.GetValue(0)); rows.Add(second, ids.GetValue(1));
            var list = (SpawnSystemList)FormatterServices.GetUninitializedObject(typeof(SpawnSystemList));
            list.m_spawners = new List<SpawnSystem.SpawnData> { first, second };
            var system = (SpawnSystem)FormatterServices.GetUninitializedObject(typeof(SpawnSystem));
            system.m_spawnLists = new List<SpawnSystemList> { list };
            var view = (ZNetView)FormatterServices.GetUninitializedObject(typeof(ZNetView));
            AccessTools.Field(typeof(SpawnSystem), "m_nview").SetValue(system, view);
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
            zdo.m_uid = new ZDOID(9812346L, 94001);
            AccessTools.Property(typeof(ZDO), "Owner").SetValue(zdo, true);
            AccessTools.Property(typeof(ZDO), "Valid").SetValue(zdo, true);
            AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
            FieldInfo net = AccessTools.Field(typeof(ZNet), "m_instance"), world = AccessTools.Field(typeof(ZDOMan), "s_instance");
            object oldNet = net.GetValue(null), oldWorld = world.GetValue(null);
            var fakeNet = (ZNet)FormatterServices.GetUninitializedObject(typeof(ZNet));
            AccessTools.Field(typeof(ZNet), "m_isServer").SetValue(fakeNet, true);
            var production = new Harmony("DropNSpawn.isolated.spawn.timers");
            var isolation = new Harmony("DropNSpawn.isolated.spawn.timer.fixtures");
            int key = (int)timers.GetField("StorageKey", All).GetValue(null);
            int legacy = "b_Gjall1".GetStableHashCode();
            DateTime now = new DateTime(TimeSpan.FromHours(20).Ticks);
            object Start(bool isEvent = false, string salt = "b_")
            {
                object[] args = { system, list.m_spawners, isEvent, salt, view, null };
                bool run = (bool)begin.Invoke(null, args);
                Check(run, "owner SpawnSystem timer batch may execute");
                return args[5];
            }
            long Read(SpawnSystem.SpawnData row) => (long)read.Invoke(null, new object[] { zdo, legacy, 0L, system, row, false, "b_" });
            void Write(SpawnSystem.SpawnData row, long ticks) => write.Invoke(null, new object[] { zdo, legacy, ticks, system, row, false, "b_" });
            void Attach() => attach.Invoke(null, new object[] { system, configuredIds, rows, system.m_spawnLists });
            MethodInfo setBytes = AccessTools.Method(typeof(ZDOExtraData), "Set", new[] { typeof(ZDOID), typeof(int), typeof(byte[]) });
            try
            {
                net.SetValue(null, fakeNet);
                var timerWorld = (ZDOMan)FormatterServices.GetUninitializedObject(typeof(ZDOMan));
                var changeQueue = new HashSet<ZDOID>();
                AccessTools.Field(typeof(ZDOMan), "m_clientChangeQueue").SetValue(timerWorld, changeQueue);
                world.SetValue(null, timerWorld);
                // Only Unity's native Object equality is substituted. Real ZDO,
                // ownership flags, revision increments and byte-array APIs run.
                isolation.Patch(begin, transpiler: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolateTimerIdentity)));
                isolation.Patch(AccessTools.PropertyGetter(typeof(UnityEngine.Application), "persistentDataPath"),
                    prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(TimerFixtureDataPath)));
                production.CreateClassProcessor(mod.GetType("DropNSpawn.SpawnSystemTimerPatch", true)).Patch();
                Check(true, "original private UpdateSpawnList timer patch installs under Unity Mono");
                Type manager = mod.GetType("DropNSpawn.SpawnSystemManager", true);
                Type compiledType = manager.GetNestedType("CompiledSpawnSystemTable", All);
                object compiled = Activator.CreateInstance(compiledType, true);
                compiledType.GetProperty("TimerIds").SetValue(compiled, configuredIds);
                var compiledIds = (System.Collections.IDictionary)compiledType.GetProperty("TimerIdsBySpawnData").GetValue(compiled);
                compiledIds.Add(first, ids.GetValue(0)); compiledIds.Add(second, ids.GetValue(1));
                ((List<SpawnSystemList>)compiledType.GetProperty("Lists").GetValue(compiled)).Add(list);
                isolation.Patch(manager.GetMethod("CreateAttachedSpawnList", All), prefix: new HarmonyMethod(typeof(CompatibilityProbe), nameof(TimerFixtureList)));
                object[] cloneArgs = { compiled, null };
                system.m_spawnLists = (List<SpawnSystemList>)manager.GetMethod("CloneAttachedSpawnLists", All).Invoke(null, cloneArgs);
                rows = (System.Collections.IDictionary)cloneArgs[1];
                list = system.m_spawnLists[0];
                Check(!ReferenceEquals(first, list.m_spawners[0]) && rows[list.m_spawners[0]].Equals(ids.GetValue(0)) &&
                    rows[list.m_spawners[1]].Equals(ids.GetValue(1)), "real per-zone cloning carries timer identities to both independent live rows");
                first = list.m_spawners[0]; second = list.m_spawners[1];

                // Run the original loop, not a second implementation of its
                // timer arithmetic. Only scene queries and chance draws are
                // substituted; every draw fails, so no native spawn is needed.
                MethodInfo update = AccessTools.Method(typeof(SpawnSystem), "UpdateSpawnList");
                isolation.Patch(update, transpiler: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolateTimerSpawnScene)));
                var heightmap = (Heightmap)FormatterServices.GetUninitializedObject(typeof(Heightmap));
                AccessTools.Field(typeof(Heightmap), "m_cornerAltBiomes").SetValue(heightmap, new List<AltBiome>());
                AccessTools.Field(typeof(SpawnSystem), "m_heightmap").SetValue(system, heightmap);
                AccessTools.Field(typeof(SpawnSystem), "m_tempNearZDOs").SetValue(system, new List<ZDO>());
                first.m_enabled = second.m_enabled = true;
                first.m_spawnChance = second.m_spawnChance = -1;
                first.m_spawnInterval = 1500; second.m_spawnInterval = 3000;
                first.m_maxSpawned = 2; second.m_maxSpawned = 3;
                first.m_prefab = second.m_prefab = (UnityEngine.GameObject)FormatterServices.GetUninitializedObject(typeof(UnityEngine.GameObject));
                Attach();
                int Attempts(DateTime time, bool isEvent = false, string salt = "b_")
                {
                    timerChanceDraws = 0;
                    update.Invoke(system, new object[] { list.m_spawners, time, isEvent, salt });
                    return timerChanceDraws;
                }
                Check(Attempts(new DateTime(TimeSpan.FromSeconds(34).Ticks)) == 0 && zdo.GetByteArray(key) == null,
                    "native young-world loop waits only for elapsed world time without creating a new cooldown");
                Check(Attempts(now) == 5 && zdo.GetByteArray(key).Length == 96,
                    "native mature-world first check immediately attempts both long-interval rules with their native caps");
                byte[] nativeSaved = zdo.GetByteArray(key);
                uint nativeRevision = zdo.DataRevision;
                Check(Attempts(now) == 0 && ReferenceEquals(nativeSaved, zdo.GetByteArray(key)) && zdo.DataRevision == nativeRevision,
                    "failed chance draws consume the interval once; same-time checks neither retry nor write");
                Check(Attempts(now.AddSeconds(1499)) == 0 && Attempts(now.AddSeconds(1500)) == 1 &&
                    Attempts(now.AddSeconds(3000)) == 2,
                    "native independent 1500/3000-second cooldowns remain due at their original intervals");
                Check(Attempts(now, true, "e_") == 5 && zdo.GetLong("e_TimerFixture1".GetStableHashCode()) == now.Ticks,
                    "native event first checks still use their original long-key timers");
                Check(Attempts(now, false, "m0") == 5 && zdo.GetLong("m0TimerFixture1".GetStableHashCode()) == now.Ticks,
                    "native AltBiome first checks still use their original long-key timers");
                first.m_maxSpawned = 0; second.m_enabled = false;
                Check(Attempts(now, true, "unlimited_") == 1,
                    "native unlimited-count rules retain the one-attempt catch-up cap");
                first.m_maxSpawned = 2; second.m_enabled = true;

                // Separate fresh ZDO for the batch/save/ownership fixtures below.
                zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
                zdo.m_uid = new ZDOID(9812346L, 94002);
                AccessTools.Property(typeof(ZDO), "Owner").SetValue(zdo, true);
                AccessTools.Property(typeof(ZDO), "Valid").SetValue(zdo, true);
                AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
                zdo.Set(legacy, 123L);
                uint revision = zdo.DataRevision;
                Attach();
                object state = Start();
                Check(Read(first) == 0 && Read(second) == 0, "fresh timers ignore legacy values and retain the native zero baseline");
                end.Invoke(null, new[] { state });
                object[] missingHover = { system, first, zdo, now, 0d };
                Check((bool)elapsed.Invoke(null, missingHover) && (double)missingHover[4] == now.Ticks / (double)TimeSpan.TicksPerSecond &&
                    zdo.DataRevision == revision && zdo.GetByteArray(key) == null,
                    "missing timer reads and ESP reflect native elapsed time without creating or syncing records");
                state = Start();
                Write(first, now.Ticks + 100);
                Write(second, now.Ticks);
                Check(zdo.DataRevision == revision && zdo.GetByteArray(key) == null, "row writes are batched, not individually synchronized");
                end.Invoke(null, new[] { state });
                byte[] saved = zdo.GetByteArray(key);
                Check(saved != null && zdo.DataRevision == revision + 1 && zdo.GetLong(legacy) == 123L,
                    "batch writes one new array, marks revision and preserves old long keys");
                ZDOExtraData.PrepareSave();
                ZPackage package = new ZPackage();
                zdo.Clone().Save(package);
                var loaded = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
                loaded.Load(new ZPackage(package.GetArray()), global::Version.c_WorldVersion);
                Check(loaded.GetByteArray(key).SequenceEqual(saved) && loaded.GetLong(legacy) == 123L,
                    "original ZDO save/load format preserves new timer bytes and untouched legacy values");
                revision = zdo.DataRevision;
                state = Start();
                Check(Read(first) == now.Ticks + 100 && Read(second) == now.Ticks, "independent timer state survives the next attempt");
                end.Invoke(null, new[] { state });
                Check(zdo.DataRevision == revision && ReferenceEquals(zdo.GetByteArray(key), saved), "unchanged attempt performs no write");
                AccessTools.Field(typeof(ZNet), "m_isServer").SetValue(fakeNet, false);
                state = Start(); Write(first, now.Ticks + 150);
                Exception sentinel = new InvalidOperationException("fixture spawn failure");
                object exceptionResult = mod.GetType("DropNSpawn.SpawnSystemTimerPatch", true).GetMethod("Finalizer", All)
                    .Invoke(null, new object[] { state, sentinel });
                Check(ReferenceEquals(exceptionResult, sentinel) && changeQueue.Contains(zdo.m_uid) && zdo.DataRevision == revision + 1,
                    "client owner consumes and queues the timer even on a later spawn exception, preserving the original exception");
                AccessTools.Field(typeof(ZNet), "m_isServer").SetValue(fakeNet, true);
                setBytes.Invoke(null, new object[] { zdo.m_uid, key, saved });
                revision = zdo.DataRevision;
                Attach(); // new in-memory binding, same saved ZDO
                state = Start();
                Check(Read(first) == now.Ticks + 100, "reattachment reconstructs timer state from the ZDO");
                end.Invoke(null, new[] { state });
                Check(Start(isEvent: true, salt: "e_") == null && Start(salt: "m0") == null, "events and AltBiomes do not enter DNS storage");
                Check((long)read.Invoke(null, new object[] { zdo, legacy, 0L, system, first, true, "e_" }) == 123L,
                    "event timer reads retain the native path even for a shared row object");

                state = Start(); Write(first, now.Ticks + 200);
                AccessTools.Property(typeof(ZDO), "Owner").SetValue(zdo, false);
                end.Invoke(null, new[] { state });
                Check(ReferenceEquals(zdo.GetByteArray(key), saved), "lost owner never flushes its stale timer batch");
                object[] denied = { system, list.m_spawners, false, "b_", view, null };
                Check(!(bool)begin.Invoke(null, denied) && denied[5] == null, "non-owner cannot begin a timer write batch");
                object[] hover = { system, first, zdo, now.AddSeconds(5), 0d };
                Check((bool)elapsed.Invoke(null, hover) && Math.Abs((double)hover[4] - 4.99999) < 0.000001 && zdo.DataRevision == revision,
                    "non-owner ESP reads saved time without initializing or writing timers");
                string espPath = Path.Combine(Path.GetDirectoryName(typeof(CompatibilityProbe).Assembly.Location), "ESP.dll");
                if (File.Exists(espPath))
                {
                    Assembly esp = Assembly.LoadFrom(espPath);
                    Type compat = mod.GetType("DropNSpawn.EspSpawnSystemCompatibility", true);
                    MethodInfo text = esp.GetType("ESP.Texts", true).GetMethod("Get", new[] { typeof(SpawnSystem), typeof(SpawnSystem.SpawnData), typeof(int) });
                    // Preparing ESP's full UI detour initializes Player/Animator,
                    // which requires Unity native services. Validate its real IL
                    // rewrite here, then execute only the production read adapter.
                    List<CodeInstruction> originalText = PatchProcessor.GetOriginalInstructions(text);
                    var rewrittenText = ((IEnumerable<CodeInstruction>)compat.GetMethod("SpawnSystemTextTimerTranspiler", All)
                        .Invoke(null, new object[] { originalText })).ToList();
                    Check(rewrittenText.Count == originalText.Count + 1 && rewrittenText.Count(i => i.operand is MethodInfo method && method.Name == "GetSpawnSystemElapsed") == 1 &&
                        !rewrittenText.Any(i => i.operand is MethodInfo method && method.DeclaringType.FullName == "Service.Helper" && method.Name == "GetElapsed"),
                        "actual ESP IL replaces only its SpawnSystem elapsed-time read");
                    MethodInfo wrapper = compat.GetMethod("GetSpawnSystemElapsed", All);
                    isolation.Patch(wrapper, transpiler: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolateTimerIdentity)));
                    AccessTools.Field(typeof(ZNet), "m_netTime").SetValue(fakeNet, now.AddSeconds(5).Ticks / (double)TimeSpan.TicksPerSecond);
                    Check(Math.Abs((double)wrapper.Invoke(null, new object[] { system, legacy, 0L, first }) - 4.99999) < 0.000001 && zdo.DataRevision == revision,
                        "ESP wrapper reads DNS time through the original private view accessor without writing");
                    FieldInfo originalElapsed = compat.GetField("_originalGetElapsed", All);
                    object oldElapsed = originalElapsed.GetValue(null);
                    try
                    {
                        originalElapsed.SetValue(null, new Func<UnityEngine.MonoBehaviour, int, long, double>((obj, hash, fallback) => 777d));
                        Check((double)wrapper.Invoke(null, new object[] { system, legacy, 0L, new SpawnSystem.SpawnData() }) == 777d,
                            "ESP delegates unmanaged and AltBiome rows to its unchanged native reader");
                    }
                    finally { originalElapsed.SetValue(null, oldElapsed); }
                }
                AccessTools.Property(typeof(ZDO), "Owner").SetValue(zdo, true);
                state = Start(); Write(first, now.Ticks + 300);
                zdo.OwnerRevision++;
                end.Invoke(null, new[] { state });
                Check(ReferenceEquals(zdo.GetByteArray(key), saved), "ownership generation change discards stale writes even after regaining owner");
                state = Start(); Write(first, now.Ticks + 400);
                byte[] incoming = (byte[])saved.Clone(); BitConverter.GetBytes(now.Ticks + 777).CopyTo(incoming, 44);
                setBytes.Invoke(null, new object[] { zdo.m_uid, key, incoming });
                end.Invoke(null, new[] { state });
                Check(ReferenceEquals(zdo.GetByteArray(key), incoming), "received replacement wins over a local pending batch");
                setBytes.Invoke(null, new object[] { zdo.m_uid, key, saved });

                // Removing the second accepted definition prunes only its new record.
                Array onlyFirst = Array.CreateInstance(idType, 1); onlyFirst.SetValue(ids.GetValue(0), 0);
                configuredIds = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(idType), new object[] { onlyFirst });
                rows.Remove(second); list.m_spawners.Remove(second);
                Attach(); state = Start(); end.Invoke(null, new[] { state });
                Check(zdo.GetByteArray(key).Length == 52 && zdo.GetLong(legacy) == 123L, "new table prunes retired records without deleting legacy keys");
                byte[] valid = zdo.GetByteArray(key), unknown = (byte[])valid.Clone(); unknown[0] = 2;
                setBytes.Invoke(null, new object[] { zdo.m_uid, key, unknown });
                denied = new object[] { system, list.m_spawners, false, "b_", view, null };
                Check(!(bool)begin.Invoke(null, denied) && ReferenceEquals(zdo.GetByteArray(key), unknown), "unsupported storage pauses normal spawning without destructive fallback");
                setBytes.Invoke(null, new object[] { zdo.m_uid, key, valid });
                state = Start(); Check(Read(first) == now.Ticks + 100, "valid replacement recovers a previously unreadable store"); end.Invoke(null, new[] { state });
                timers.GetMethod("Detach", All).Invoke(null, new object[] { system });
                hover = new object[] { system, first, zdo, now, 0d };
                Check(!(bool)elapsed.Invoke(null, hover), "destroy/detach releases timer binding and ESP override");
            }
            finally
            {
                timers.GetMethod("Detach", All).Invoke(null, new object[] { system });
                production.UnpatchSelf(); isolation.UnpatchSelf();
                net.SetValue(null, oldNet); world.SetValue(null, oldWorld);
            }
        }

        private static IEnumerable<CodeInstruction> IsolateTimerIdentity(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(UnityEngine.Object) &&
                    (method.Name == "op_Equality" || method.Name == "op_Inequality"))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(CompatibilityProbe), method.Name == "op_Equality" ? nameof(AttributionEqual) : nameof(AttributionDifferent));
                }
                yield return instruction;
            }
        }

        private static class ChanceScalingFixture
        {
            private static Character character;
            private static int characterLookups;

            internal static void Run(Assembly mod)
            {
                Type settings = mod.GetType("DropNSpawn.CharacterDropGlobalConfig", true);
                Type manager = mod.GetType("DropNSpawn.CharacterDropManager", true);
                Type patch = mod.GetType("DropNSpawn.CharacterDropGenerateDropListPatch", true);
                Type bound = mod.GetType("DropNSpawn.PluginBoundSettings", true);
                var previous = new Dictionary<FieldInfo, object>();
                var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "dns-unsaved-chance-mono-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
                var fixture = new Harmony("DropNSpawn.isolated.chance-scaling");
                MethodInfo prefix = patch.GetMethod("Prefix", All), postfix = patch.GetMethod("Postfix", All), finalizer = patch.GetMethod("Finalizer", All);
                try
                {
                    object mode = Bind(settings, "_chanceOnlyDropChanceScaling", "Linear");
                    Bind(settings, "_characterLootSystem", "Vanilla");
                    Bind(settings, "_globalTrophyLevelMultiplier", "Off");
                    Bind(settings, "_disableCharacterLootScalingWhenCreatureLevelControlLoaded", "On");
                    Bind(bound, "EnableCharacterOverrides", "Off");
                    Set(settings.GetField("_isCreatureLevelControlLoaded", All), false);
                    fixture.Patch(manager.GetMethod("PrepareCharacterLootScalingDrops", All),
                        transpiler: new HarmonyMethod(typeof(ChanceScalingFixture), nameof(Isolate)));
                    fixture.Patch(mod.GetType("DropNSpawn.CharacterDropKillerFilter", true).GetMethod("SuppressScopedGeneratedDrops", All),
                        transpiler: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolateTimerIdentity)));
                    character = (Character)FormatterServices.GetUninitializedObject(typeof(Character));
                    var component = (CharacterDrop)FormatterServices.GetUninitializedObject(typeof(CharacterDrop));
                    var prefab = (UnityEngine.GameObject)FormatterServices.GetUninitializedObject(typeof(UnityEngine.GameObject));
                    var ordinary = new CharacterDrop.Drop { m_prefab = prefab, m_chance = .25f, m_levelMultiplier = true };
                    var chanceOnly = new CharacterDrop.Drop { m_prefab = prefab, m_chance = .1f, m_levelMultiplier = true,
                        m_levelMultiplierNotAmount = true, m_onePerPlayer = true, m_dontScale = true, m_amountMin = 2, m_amountMax = 3 };
                    var inactive = new CharacterDrop.Drop { m_prefab = prefab, m_chance = .2f, m_levelMultiplier = false, m_levelMultiplierNotAmount = true };
                    var original = new List<CharacterDrop.Drop> { ordinary, chanceOnly, inactive };
                    component.m_drops = original;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        characterLookups = 0;
                        object[] args = { component, null };
                        prefix.Invoke(null, args);
                        object state = args[1];
                        var active = component.m_drops;
                        Check(!ReferenceEquals(active, original) && !ReferenceEquals(active[1], chanceOnly) && characterLookups == 1,
                            "chance-only native preparation owns its rows and resolves the character once");
                        Check(Math.Abs(active[1].m_chance - .2f) < .00001f && !active[1].m_levelMultiplier && active[1].m_levelMultiplierNotAmount,
                            "native effective chance is linear with no second star multiplication");
                        Check(active[0].m_levelMultiplier && active[0].m_chance == .25f && !active[2].m_levelMultiplier && active[2].m_chance == .2f,
                            "mixed ordinary and disabled rows retain their chance and amount policies");
                        Check(active[1].m_onePerPlayer && active[1].m_dontScale && active[1].m_amountMin == 2 && active[1].m_amountMax == 3,
                            "chance preparation preserves amount and one-per-player fields");
                        Check(ReferenceEquals(state.GetType().GetProperty("AmountSourceDrops", All).GetValue(state), original) &&
                            chanceOnly.m_levelMultiplier && chanceOnly.m_chance == .1f,
                            "postfix source preserves original chance-only identity without cumulative changes");
                        MethodInfo protectedRows = manager.GetMethods(All).Single(m => m.Name == "HasChanceOnlyDropForPrefab" &&
                            m.GetParameters()[0].ParameterType.GetGenericArguments()[0] == typeof(CharacterDrop.Drop));
                        Check((bool)protectedRows.Invoke(null, new object[] { original, prefab }), "same-prefab group remains excluded from DNS amount scaling");
                        if (pass == 0)
                        {
                            postfix.Invoke(null, new object[] { component, new List<KeyValuePair<UnityEngine.GameObject, int>>(), state });
                            Check(ReferenceEquals(component.m_drops, original), "normal postfix restores original list reference");
                            finalizer.Invoke(null, new object[] { component, state, null, null });
                        }
                        else
                        {
                            var error = new InvalidOperationException("fixture failure");
                            Check(ReferenceEquals(finalizer.Invoke(null, new object[] { component, state, null, error }), error), "finalizer preserves original exception");
                        }
                        Check(ReferenceEquals(component.m_drops, original), "finalizer restores original rows on success and failure");
                    }
                    foreach (string value in new[] { "Vanilla", "Linear" })
                    {
                        mode.GetType().GetProperty("Value").SetValue(mode, Enum.Parse(mode.GetType().GetGenericArguments()[0], value));
                        if (value == "Linear") settings.GetField("_isCreatureLevelControlLoaded", All).SetValue(null, true);
                        characterLookups = 0;
                        object[] args = { component, null };
                        prefix.Invoke(null, args);
                        finalizer.Invoke(null, new object[] { component, args[1], null, null });
                        Check(ReferenceEquals(component.m_drops, original) && characterLookups == 0,
                            value == "Vanilla" ? "Vanilla mode skips new chance preparation" : "CLLC handoff skips new chance preparation");
                    }
                }
                finally
                {
                    fixture.UnpatchSelf();
                    foreach (var pair in previous) pair.Key.SetValue(null, pair.Value);
                    character = null;
                }

                void Set(FieldInfo field, object value)
                {
                    if (!previous.ContainsKey(field)) previous.Add(field, field.GetValue(null));
                    field.SetValue(null, value);
                }
                object Bind(Type holder, string fieldName, string value)
                {
                    FieldInfo field = holder.GetField(fieldName, All) ?? holder.GetField("<" + fieldName + ">k__BackingField", All);
                    Type type = field.FieldType.GetGenericArguments()[0];
                    MethodInfo bind = typeof(ConfigFile).GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition &&
                        m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType == typeof(string));
                    object entry = bind.MakeGenericMethod(type).Invoke(config, new object[] { "fixture", fieldName, Enum.Parse(type, value), "" });
                    Set(field, entry);
                    return entry;
                }
            }

            private static IEnumerable<CodeInstruction> Isolate(IEnumerable<CodeInstruction> instructions)
            {
                foreach (CodeInstruction instruction in IsolateTimerIdentity(instructions))
                {
                    if (instruction.operand is MethodInfo method && method.DeclaringType == typeof(UnityEngine.Component) &&
                        method.Name == "GetComponent" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(Character))
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(ChanceScalingFixture), nameof(GetCharacter));
                    }
                    else if (instruction.operand is MethodInfo levelMethod && levelMethod.DeclaringType == typeof(Character) && levelMethod.Name == "GetLevel")
                    {
                        // Scene input only: invoking Character triggers Unity-native static initialization.
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(ChanceScalingFixture), nameof(GetCharacterLevel));
                    }
                    yield return instruction;
                }
            }

            private static Character GetCharacter(UnityEngine.Component component) { characterLookups++; return character; }
            private static int GetCharacterLevel(Character value) => 3;
        }

        private static int timerChanceDraws;

        private static IEnumerable<CodeInstruction> IsolateTimerSpawnScene(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.operand is MethodInfo method)
                {
                    string replacement = null;
                    if (method.DeclaringType == typeof(UnityEngine.Component) && method.Name == "get_transform") replacement = nameof(TimerFixtureTransform);
                    else if (method.DeclaringType == typeof(UnityEngine.Transform) && method.Name == "get_position") replacement = nameof(TimerFixturePosition);
                    else if (method.DeclaringType == typeof(UnityEngine.Object) && method.Name == "get_name") replacement = nameof(TimerFixtureName);
                    else if (method.DeclaringType == typeof(ZDOMan) && method.Name == "FindSectorObjects") replacement = nameof(TimerFixtureSectorObjects);
                    else if (method.DeclaringType == typeof(Heightmap) && method.Name == "HaveBiome") replacement = nameof(TimerFixtureBiome);
                    else if (method.DeclaringType == typeof(UnityEngine.Random) && method.Name == "Range" && method.ReturnType == typeof(float)) replacement = nameof(TimerFixtureChance);
                    if (replacement != null)
                    {
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(CompatibilityProbe), replacement);
                    }
                }
                yield return instruction;
            }
        }

        private static UnityEngine.Transform TimerFixtureTransform(UnityEngine.Component component) => null;
        private static UnityEngine.Vector3 TimerFixturePosition(UnityEngine.Transform transform) => default;
        private static string TimerFixtureName(UnityEngine.Object obj) => "TimerFixture";
        private static bool TimerFixtureBiome(Heightmap heightmap, Heightmap.Biome biome) => true;
        private static void TimerFixtureSectorObjects(ZDOMan manager, Vector2s sector, SimulationDistance distance,
            List<ZDO> objects, List<ZDO> distant) { }
        private static float TimerFixtureChance(float min, float max) { timerChanceDraws++; return 0; }

        private static bool TimerFixtureList(List<SpawnSystem.SpawnData> spawners, ref SpawnSystemList __result)
        {
            __result = (SpawnSystemList)FormatterServices.GetUninitializedObject(typeof(SpawnSystemList));
            __result.m_spawners = spawners;
            return false;
        }

        private static bool TimerFixtureDataPath(ref string __result)
        {
            __result = Path.GetDirectoryName(typeof(CompatibilityProbe).Assembly.Location);
            return false;
        }

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

        // Executes the shipped placement/validation methods. Native scene queries are
        // substituted with a flat supporting plane and explicit obstruction results;
        // this checks the queried shape and returned position, not Unity physics itself.
        private static class DungeonPlacementFixture
        {
            private static readonly UnityEngine.GameObject Prefab = (UnityEngine.GameObject)FormatterServices.GetUninitializedObject(typeof(UnityEngine.GameObject));
            private static readonly UnityEngine.CapsuleCollider Capsule = (UnityEngine.CapsuleCollider)FormatterServices.GetUninitializedObject(typeof(UnityEngine.CapsuleCollider));
            private static readonly UnityEngine.Transform Transform = (UnityEngine.Transform)FormatterServices.GetUninitializedObject(typeof(UnityEngine.Transform));
            private static readonly ZoneSystem Zone = (ZoneSystem)FormatterServices.GetUninitializedObject(typeof(ZoneSystem));
            private static readonly Pathfinding Navigation = (Pathfinding)FormatterServices.GetUninitializedObject(typeof(Pathfinding));
            private static readonly List<Player> Players = new List<Player>();
            private static readonly string[] MaskNames = { "Default", "static_solid", "Default_small", "piece", "terrain", "blocker", "vehicle", "character", "character_net", "character_noenv", "character_ghost" };
            private static float radius, height, floor, queryBottom;
            private static UnityEngine.Vector3 center, scale, pathTarget, lineTarget;
            private static int axis, queries, pathQueries, lineQueries, diagnosticQueries, queryMask, lineMask;
            private static bool missing, blocked, floorFound, havePath, lineBlocked, interior;
            private static UnityEngine.QueryTriggerInteraction triggers;

            internal static void Run(Assembly mod)
            {
                Type placement = mod.GetType("DropNSpawn.DungeonSpawnPlacement", true);
                // The old method name lets the same regression expose the 1.4.0 failure.
                MethodInfo place = placement.GetMethod("TryPlaceOnFloor", All) ?? placement.GetMethod("Clear", All);
                MethodInfo validate = placement.GetMethod("Validate", All);
                Type diagnostics = mod.GetType("DropNSpawn.DungeonSpawnDiagnostics", true);
                Type traceType = diagnostics.GetNestedType("Placement", All);
                Type reasonType = diagnostics.GetNestedType("Reason", All);
                Type toggle = mod.GetType("DropNSpawn.DropNSpawnPlugin+Toggle", true);
                PropertyInfo setting = mod.GetType("DropNSpawn.PluginBoundSettings", true).GetProperty("DungeonSpawnDiagnostics", All);
                object oldSetting = setting.GetValue(null);
                var config = new ConfigFile(Path.Combine(Path.GetDirectoryName(typeof(CompatibilityProbe).Assembly.Location), "diagnostics.cfg"), false) { SaveOnConfigSet = false };
                MethodInfo bind = typeof(ConfigFile).GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition &&
                    m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType == typeof(string)).MakeGenericMethod(toggle);
                object option = bind.Invoke(config, new[] { "test", "diagnostics", Enum.ToObject(toggle, 0), "" });
                object trace = null;
                var fixture = new Harmony("DropNSpawn.isolated.dungeon.placement");
                try
                {
                    setting.SetValue(null, option);
                    foreach (MethodInfo method in new[] { place, validate, placement.GetMethod("WithinRange", All), placement.GetMethod("RangeFailure", All), placement.GetMethod("get_StaticMask", All), traceType.GetMethod("SampleCollision", All) })
                        fixture.Patch(method, transpiler: new HarmonyMethod(typeof(DungeonPlacementFixture), nameof(Isolate)));

                    ResetBlob();
                    Check(Place(out var point) && Near(point.y, floor + 0.2f) && queryBottom >= floor,
                        "BlobElite floor placement raises the actual pivot by 0.2m and clears the supporting plane");
                    Check(Near(point.x, 8) && Near(point.z, 0) && queryMask == (1 << MaskNames.Length) - 1 && triggers == UnityEngine.QueryTriggerInteraction.Ignore,
                        "floor correction preserves horizontal position, solid/character masks and trigger policy");
                    blocked = true;
                    Check(!Place(out _), "floor correction still rejects an occupied capsule, including walls/ceilings/creatures");

                    ResetBlob(); radius = 0.4f; height = 1.85f; center = new UnityEngine.Vector3(0, 0.925f, 0);
                    Check(Place(out point) && point.y == floor, "native Skeleton upright capsule retains its original spawn height");
                    center.y = 2;
                    Check(Place(out point) && point.y == floor, "an already-clear elevated capsule is not moved down into the floor");
                    ResetBlob(); scale = new UnityEngine.Vector3(2, 2, 2);
                    Check(Place(out point) && Near(point.y, floor + 0.4f), "uniform prefab scaling adjusts the actual pivot offset");
                    ResetBlob(); scale = new UnityEngine.Vector3(-2, 1, 0.5f);
                    Check(Place(out point) && Near(point.y, floor + 1.4f), "nonuniform/mirrored scale preserves radius and vertical-center rules");
                    ResetBlob(); missing = true;
                    Check(!Place(out _) && queries == 0, "missing capsule remains unsupported without a geometry query");
                    missing = false; axis = 0;
                    Check(!Place(out _) && queries == 0, "horizontal capsule remains unsupported");

                    ResetBlob();
                    Check(Validate(out point) && Near(point.y, floor + 0.2f) && Near(pathTarget.y, floor) && pathQueries == 1,
                        "full validation returns the corrected spawn point but navigates to the supporting floor");
                    ResetBlob(); havePath = false; lineBlocked = true;
                    Check(!Validate(out _) && Near(lineTarget.y, floor + 0.8f) && lineMask == (1 << 7) - 1,
                        "failed full path still rejects an obstructed floor-relative direct route");
                    lineBlocked = false;
                    Check(Validate(out point) && Near(point.y, floor + 0.2f), "unobstructed native direct-route fallback retains the corrected position");
                    ResetBlob(); blocked = true;
                    Check(!Validate(out _) && pathQueries == 0, "full validation cannot bypass failed capsule clearance");
                    ResetBlob(); floorFound = false;
                    Check(!Validate(out _) && queries == 0, "missing supporting floor cannot create a floating candidate");
                    ResetBlob(); interior = false;
                    Check(!Validate(out _) && queries == 0, "outside-interior candidates remain rejected");
                    ResetBlob(); floor = 5003.9f;
                    Check(!Validate(out _) && queries == 1 && pathQueries == 0,
                        "corrected pivot is revalidated against the original four-meter vertical limit");

                    string[] reasons = { null, "CapsuleBlocked", "FloorMissing", "OutsideInterior", "PathAndLineBlocked", null, "Height", "UnsupportedCapsule" };
                    for (int scenario = 0; scenario < reasons.Length; scenario++)
                    {
                        void Prepare()
                        {
                            ResetBlob();
                            if (scenario == 1) blocked = true;
                            if (scenario == 2) floorFound = false;
                            if (scenario == 3) interior = false;
                            if (scenario == 4) { havePath = false; lineBlocked = true; }
                            if (scenario == 5) havePath = false;
                            if (scenario == 6) floor = 5003.9f;
                            if (scenario == 7) missing = true;
                        }
                        option.GetType().GetProperty("Value").SetValue(option, Enum.ToObject(toggle, 0));
                        diagnostics.GetMethod("Reset", All).Invoke(null, null); trace = null; Prepare();
                        bool expected = Validate(out var expectedPoint);
                        int capsuleCalls = queries, pathCalls = pathQueries, lineCalls = lineQueries;
                        Check(diagnosticQueries == 0, "diagnostics Off performs no obstacle sampling");
                        option.GetType().GetProperty("Value").SetValue(option, Enum.ToObject(toggle, 1));
                        trace = Activator.CreateInstance(traceType, All, null, new object[] { 17L, "BlobElite", 6f, 12f }, null);
                        Prepare();
                        Check(Validate(out point) == expected && point.Equals(expectedPoint) && queries == capsuleCalls && pathQueries == pathCalls && lineQueries == lineCalls,
                            "diagnostics On preserves placement result, position and original query short-circuiting: scenario " + scenario);
                        var counts = (System.Collections.IDictionary)traceType.GetField("Counts", All).GetValue(trace);
                        if (reasons[scenario] != null)
                            Check(counts.Contains(Enum.Parse(reasonType, reasons[scenario])), "diagnostics identifies first rejection: " + reasons[scenario]);
                        if (scenario == 1) Check(diagnosticQueries == 1, "blocked capsule adds only a bounded observation query without changing the decision");
                    }
                    diagnostics.GetMethod("Reset", All).Invoke(null, null);
                    ResetBlob(); blocked = true;
                    for (int i = 0; i < 5; i++)
                    {
                        trace = Activator.CreateInstance(traceType, All, null, new object[] { (long)i, "BlobElite", 6f, 12f }, null);
                        Validate(out _);
                    }
                    Check(diagnosticQueries == 2, "obstacle sampling is capped at two extra queries across all requests in a window");
                }
                finally { setting.SetValue(null, oldSetting); diagnostics.GetMethod("Reset", All).Invoke(null, null); fixture.UnpatchSelf(); }

                bool Place(out UnityEngine.Vector3 position)
                {
                    object[] args = { Prefab, new UnityEngine.Vector3(8, floor, 0), trace };
                    bool result = (bool)place.Invoke(null, args);
                    position = (UnityEngine.Vector3)args[1];
                    return result;
                }
                bool Validate(out UnityEngine.Vector3 position)
                {
                    object[] args = { Prefab, new UnityEngine.Vector3(0, 5000, 0), new UnityEngine.Vector3(8, 5000, 0), new Vector2s(0, 0), 6f, 12f, default(UnityEngine.Vector3), trace };
                    bool result = (bool)validate.Invoke(null, args);
                    position = (UnityEngine.Vector3)args[6];
                    return result;
                }
            }

            private static bool Near(float value, float expected) => Math.Abs(value - expected) < 0.001f;
            private static void ResetBlob()
            {
                // Original Valheim 1.0.17 BlobElite.prefab in SoftRef/Bundles/c4210710.
                radius = 1.2f; height = 1; center = new UnityEngine.Vector3(0, 1, -0.04794228f); scale = UnityEngine.Vector3.one;
                axis = 1; floor = 5000; missing = blocked = lineBlocked = false; floorFound = havePath = interior = true;
                queries = pathQueries = lineQueries = diagnosticQueries = queryMask = lineMask = 0;
            }
            private static UnityEngine.CapsuleCollider GetCapsule(UnityEngine.GameObject _) => missing ? null : Capsule;
            private static BaseAI GetAI(UnityEngine.GameObject _) => null;
            private static UnityEngine.Transform GetTransform(UnityEngine.GameObject _) => Transform;
            private static UnityEngine.Vector3 GetScale(UnityEngine.Transform _) => scale;
            private static UnityEngine.Vector3 GetCenter(UnityEngine.CapsuleCollider _) => center;
            private static float GetRadius(UnityEngine.CapsuleCollider _) => radius;
            private static float GetHeight(UnityEngine.CapsuleCollider _) => height;
            private static int GetAxis(UnityEngine.CapsuleCollider _) => axis;
            private static ZoneSystem GetZoneSystem() => Zone;
            private static Pathfinding GetPathfinding() => Navigation;
            private static List<Player> GetPlayers() => Players;
            private static bool InInterior(UnityEngine.Vector3 _) => interior;
            private static int GetMask(string[] names) => names.Aggregate(0, (mask, name) => mask | (1 << Array.IndexOf(MaskNames, name)));
            private static bool GetFloor(ZoneSystem _, UnityEngine.Vector3 point, out float y, int margin) { y = floor; return floorFound; }
            private static bool CapsuleBlocked(UnityEngine.Vector3 bottom, UnityEngine.Vector3 top, float r, int mask, UnityEngine.QueryTriggerInteraction interaction)
            {
                queries++; queryBottom = Math.Min(bottom.y, top.y) - r; queryMask = mask; triggers = interaction;
                return blocked || queryBottom < floor - 0.001f;
            }
            private static bool GetPath(Pathfinding _, UnityEngine.Vector3 from, UnityEngine.Vector3 to, List<UnityEngine.Vector3> path,
                Pathfinding.AgentType agent, bool full, bool cleanup, bool existing)
            {
                pathQueries++; pathTarget = to;
                if (havePath) { path.Add(from); path.Add(to); }
                return havePath;
            }
            private static bool Linecast(UnityEngine.Vector3 from, UnityEngine.Vector3 to, int mask, UnityEngine.QueryTriggerInteraction interaction)
            { lineQueries++; lineTarget = to; lineMask = mask; return lineBlocked; }
            private static int ObserveCapsule(UnityEngine.Vector3 bottom, UnityEngine.Vector3 top, float radius, UnityEngine.Collider[] hits,
                int mask, UnityEngine.QueryTriggerInteraction interaction) { diagnosticQueries++; return 0; }

            private static IEnumerable<CodeInstruction> Isolate(IEnumerable<CodeInstruction> instructions)
            {
                foreach (CodeInstruction instruction in instructions)
                {
                    if (instruction.operand is MethodInfo method)
                    {
                        string replacement = null;
                        if (method.DeclaringType == typeof(UnityEngine.GameObject))
                            replacement = method.Name == "get_transform" ? nameof(GetTransform) : method.Name == "GetComponent" && method.IsGenericMethod
                                ? method.GetGenericArguments()[0] == typeof(UnityEngine.CapsuleCollider) ? nameof(GetCapsule) : nameof(GetAI) : null;
                        else if (method.DeclaringType == typeof(UnityEngine.Transform) && method.Name == "get_lossyScale") replacement = nameof(GetScale);
                        else if (method.DeclaringType == typeof(UnityEngine.CapsuleCollider))
                            replacement = method.Name == "get_radius" ? nameof(GetRadius) : method.Name == "get_height" ? nameof(GetHeight) :
                                method.Name == "get_center" ? nameof(GetCenter) : method.Name == "get_direction" ? nameof(GetAxis) : null;
                        else if (method.DeclaringType == typeof(UnityEngine.LayerMask) && method.Name == "GetMask") replacement = nameof(GetMask);
                        else if (method.DeclaringType == typeof(UnityEngine.Physics))
                            replacement = method.Name == "CheckCapsule" ? nameof(CapsuleBlocked) : method.Name == "Linecast" ? nameof(Linecast) :
                                method.Name == "OverlapCapsuleNonAlloc" ? nameof(ObserveCapsule) : null;
                        else if (method.DeclaringType == typeof(ZoneSystem))
                            replacement = method.Name == "get_instance" ? nameof(GetZoneSystem) : method.Name == "GetSolidHeight" ? nameof(GetFloor) : null;
                        else if (method.DeclaringType == typeof(Pathfinding))
                            replacement = method.Name == "get_instance" ? nameof(GetPathfinding) : method.Name == "GetPath" ? nameof(GetPath) : null;
                        else if (method.DeclaringType == typeof(Player) && method.Name == "GetAllPlayers") replacement = nameof(GetPlayers);
                        else if (method.DeclaringType == typeof(Character) && method.Name == "InInterior") replacement = nameof(InInterior);
                        if (replacement != null)
                        {
                            instruction.opcode = OpCodes.Call;
                            instruction.operand = AccessTools.Method(typeof(DungeonPlacementFixture), replacement);
                        }
                        else if (method.DeclaringType == typeof(UnityEngine.Object) && (method.Name == "op_Equality" || method.Name == "op_Inequality"))
                        {
                            instruction.opcode = OpCodes.Call;
                            instruction.operand = AccessTools.Method(typeof(CompatibilityProbe), method.Name == "op_Equality" ? nameof(AttributionEqual) : nameof(AttributionDifferent));
                        }
                    }
                    yield return instruction;
                }
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

        private static class EwdFailureFixture
        {
            private static readonly List<SpawnSystem.SpawnData> Seen = new List<SpawnSystem.SpawnData>();
            private static Func<string, object> originalConstant;
            private static int constantCalls;
            private static object Read(object obj, string name) => obj.GetType().GetProperty(name, All).GetValue(obj);
            private static void Set(object obj, string name, object value) => obj.GetType().GetProperty(name, All).SetValue(obj, value);
            private static bool Skip() => false;
            private static bool Allow(ref bool __result) { __result = true; return false; }
            private static bool EnvironmentKey(ref string __result) { __result = "dependency-fixture"; return false; }
            private static bool ApplyEntry(object[] __args, ref bool __result)
            {
                Seen.Add((SpawnSystem.SpawnData)__args[0]);
                __result = (string)Read(__args[1], "Prefab") != "missing";
                return false;
            }
            private static object FailingConstant(string value)
            {
                if (++constantCalls == 2) throw new MissingMethodException("Injected EWD constant API failure");
                return originalConstant(value);
            }
            internal static void Run(Assembly mod)
            {
                Type compat = mod.GetType("DropNSpawn.ExpandWorldDataCompatibility", true);
                Type manager = mod.GetType("DropNSpawn.SpawnSystemManager", true);
                Type events = mod.GetType("DropNSpawn.EventManager", true);
                Type support = mod.GetType("DropNSpawn.SpawnSystemCustomDataSupport", true);
                Type entryType = mod.GetType("DropNSpawn.CanonicalSpawnSystemEntry", true);
                Type definitionType = mod.GetType("DropNSpawn.SpawnSystemSpawnDefinition", true);
                FieldInfo constantField = compat.GetField("_stringConstant", All);
                originalConstant = (Func<string, object>)constantField.GetValue(null);
                var fixture = new Harmony("DropNSpawn.isolated.ewd.failure");
                var restore = new List<Action>();
                string yamlPath = Path.Combine(Path.GetTempPath(), "dns-ewd-fault-" + Guid.NewGuid().ToString("N") + ".yml");
                try
                {
                    fixture.Patch(manager.GetMethod("ApplyEntry", All), prefix: new HarmonyMethod(typeof(EwdFailureFixture), nameof(ApplyEntry)));
                    foreach (string method in new[] { "RefreshResolvedBiomeMasksForConfiguration", "EnsureVanillaCompiledTableCurrentLocked" })
                        fixture.Patch(manager.GetMethod(method, All), prefix: new HarmonyMethod(typeof(EwdFailureFixture), nameof(Skip)));
                    fixture.Patch(manager.GetMethod("CanStrictlyValidateLocalConfigurationNow", All), prefix: new HarmonyMethod(typeof(EwdFailureFixture), nameof(Allow)));
                    fixture.Patch(manager.GetMethod("BuildStrictLocalValidationEnvironmentKey", All), prefix: new HarmonyMethod(typeof(EwdFailureFixture), nameof(EnvironmentKey)));
                    fixture.Patch(events.GetMethod("ApplyActiveDefinitionsLocked", All), transpiler: new HarmonyMethod(typeof(CompatibilityProbe), nameof(IsolateTimerIdentity)));
                    // RandomEvent's default AnimationCurve requires native Unity.
                    // This fixture supplies the fields relevant to candidate rollback.
                    fixture.Patch(AccessTools.Constructor(typeof(RandomEvent)), prefix: new HarmonyMethod(typeof(EwdFailureFixture), nameof(Skip)));
                    fixture.Patch(AccessTools.Method(typeof(RandEventSystem), "SetRandomEventsNeedsRefresh"), prefix: new HarmonyMethod(typeof(EwdFailureFixture), nameof(Skip)));
                    MethodInfo hasPayload = support.GetMethod("HasPreparedPayload", All);
                    object runtime = manager.GetField("ConfigurationRuntime", All).GetValue(null);
                    object load = Read(runtime, "LoadState");
                    foreach (PropertyInfo property in load.GetType().GetProperties(All))
                    {
                        object old = property.GetValue(load); restore.Add(() => property.SetValue(load, old));
                    }
                    Set(load, "LastLoadedPayload", "accepted-before-fault");
                    object runtimeState = manager.GetField("RuntimeState", All).GetValue(null);
                    object oldConfiguration = Read(runtimeState, "Configuration");
                    File.WriteAllText(yamlPath, "- prefab: first\n  spawnSystem:\n    faction: ForestMonsters\n- prefab: second\n  spawnSystem:\n    faction: ForestMonsters\n");
                    Arm();
                    object outcome = runtime.GetType().GetMethod("ReloadSourceOfTruth", All).Invoke(runtime, new object[] { new List<string> { yamlPath } });
                    Check(outcome.ToString() == "Rejected" && (string)Read(load, "LastLoadedPayload") == "accepted-before-fault" &&
                        ReferenceEquals(Read(runtimeState, "Configuration"), oldConfiguration), "common API failure rejects local reload before authoritative configuration replacement");
                    Check(constantCalls == 2 && manager.GetField("_capturedStrictValidationWarnings", All).GetValue(null) == null,
                        "strict validation aborts once and releases diagnostic capture after dependency failure");

                    constantField.SetValue(null, originalConstant);
                    var entries = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
                    object[] args = { entries, null };
                    var filter = manager.GetMethod("FilterStrictlyValidatedLocalConfiguration", All);
                    Check(((System.Collections.IList)filter.Invoke(null, args)).Count == 0, "intentional empty SpawnSystem configuration remains valid");
                    entries.Add(Entry("missing")); entries.Add(Entry("valid"));
                    Check(((System.Collections.IList)filter.Invoke(null, args)).Count == 1, "unknown-prefab skip remains separate from dependency failure");

                    object pipeline = manager.GetField("BuildPipelineState", All).GetValue(null);
                    foreach (PropertyInfo property in pipeline.GetType().GetProperties(All))
                    {
                        object old = property.GetValue(pipeline); restore.Add(() => property.SetValue(pipeline, old));
                    }
                    var cache = (System.Collections.IDictionary)manager.GetField("FinalizedPreparedEntryCache", All).GetValue(null);
                    var oldCache = cache.Cast<System.Collections.DictionaryEntry>().ToArray();
                    restore.Add(() => { cache.Clear(); foreach (var item in oldCache) cache.Add(item.Key, item.Value); });
                    cache.Clear();
                    object pending = Activator.CreateInstance(manager.GetNestedType("PendingCompiledTableBuildState", All), true);
                    Set(pending, "BuildVersion", Read(pipeline, "PreparedEntriesBuildVersion"));
                    Set(pending, "GameDataSignature", 123); Set(pending, "DomainEnabled", true); Set(pending, "EagerClientSyncBuild", true);
                    Set(pending, "ApplyTargetSignature", "candidate");
                    Set(pipeline, "PendingCompiledTableBuild", pending); Set(pipeline, "PendingBuildTargetSignature", "candidate");
                    foreach (string name in new[] { "first", "second" })
                    {
                        object model = Activator.CreateInstance(manager.GetNestedType("PreparedSpawnSystemModel", All), true);
                        Set(model, "Entry", Entry(name)); Set(model, "EntrySignature", name); Set(model, "Context", name);
                        ((System.Collections.IList)Read(pending, "Models")).Add(model);
                    }
                    object oldActive = manager.GetField("_activeCompiledTable", All).GetValue(null);
                    object sentinel = Activator.CreateInstance(manager.GetNestedType("CompiledSpawnSystemTable", All), true);
                    manager.GetField("_activeCompiledTable", All).SetValue(null, sentinel);
                    restore.Add(() => manager.GetField("_activeCompiledTable", All).SetValue(null, oldActive));
                    Arm();
                    MethodInfo step = manager.GetMethod("TryProcessPendingCompiledTableBuild", All);
                    Check((bool)step.Invoke(null, new object[] { double.MaxValue }) && constantCalls == 2 &&
                        Read(pipeline, "PendingCompiledTableBuild") == null && ReferenceEquals(manager.GetField("_activeCompiledTable", All).GetValue(null), sentinel),
                        "partially prepared table is discarded on API failure while previous active table stays selected");
                    Check(!(bool)step.Invoke(null, new object[] { double.MaxValue }), "failed staged table does not retry/skip rows on the next frame");

                    constantField.SetValue(null, originalConstant);
                    FieldInfo instance = typeof(RandEventSystem).GetFields(All).Single(field => field.IsStatic && field.FieldType == typeof(RandEventSystem));
                    object oldInstance = instance.GetValue(null); restore.Add(() => instance.SetValue(null, oldInstance));
                    var system = (RandEventSystem)FormatterServices.GetUninitializedObject(typeof(RandEventSystem));
                    instance.SetValue(null, system);
                    var oldSpawn = new SpawnSystem.SpawnData();
                    object oldPayload = support.GetMethod("BuildPreparedPayload", All).Invoke(null, new[] { oldSpawn, Entry("old"), "old" });
                    support.GetMethod("ApplyPreparedPayload", All).Invoke(null, new[] { oldSpawn, oldPayload });
                    restore.Add(() => support.GetMethod("ApplyPreparedPayload", All).Invoke(null, new object[] { oldSpawn, null }));
                    var oldEvent = new RandomEvent { m_name = "old", m_forceEnvironment = "old-env", m_forceMusic = "old-music", m_spawn = new List<SpawnSystem.SpawnData> { oldSpawn } };
                    system.m_events = new List<RandomEvent> { oldEvent };
                    object oldEvents = system.m_events;
                    foreach (string name in new[] { "BaselineEvents", "ActiveDefinitions", "EventMetadata" })
                    {
                        FieldInfo field = events.GetField(name, All); object old = field.GetValue(null); restore.Add(() => field.SetValue(null, old));
                    }
                    events.GetField("BaselineEvents", All).SetValue(null, new List<RandomEvent>());
                    var definitions = (System.Collections.IList)Activator.CreateInstance(events.GetField("ActiveDefinitions", All).FieldType);
                    Type eventType = mod.GetType("DropNSpawn.EventDefinition", true);
                    Type eventSpawnType = mod.GetType("DropNSpawn.EventSpawnDefinition", true);
                    foreach (string name in new[] { "first", "second" })
                    {
                        object ev = Activator.CreateInstance(eventType, true); Set(ev, "Event", name);
                        object spawn = Activator.CreateInstance(eventSpawnType, true); Set(spawn, "Prefab", name); Set(spawn, "SpawnSystem", Definition());
                        var spawns = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(eventSpawnType)); spawns.Add(spawn); Set(ev, "Spawns", spawns);
                        definitions.Add(ev);
                    }
                    events.GetField("ActiveDefinitions", All).SetValue(null, definitions);
                    object metadata = events.GetField("EventMetadata", All).GetValue(null);
                    object oldMetadata = Activator.CreateInstance(metadata.GetType().GetGenericArguments()[1], true);
                    metadata.GetType().GetMethod("Add").Invoke(metadata, new[] { oldEvent, oldMetadata });
                    restore.Add(() => metadata.GetType().GetMethod("Remove").Invoke(metadata, new object[] { oldEvent }));
                    var applied = (System.Collections.IList)events.GetField("AppliedSpawnData", All).GetValue(null);
                    var oldApplied = applied.Cast<object>().ToArray(); restore.Add(() => { applied.Clear(); foreach (var item in oldApplied) applied.Add(item); });
                    restore.Add(() => events.GetMethod("ClearAppliedPayloadsLocked", All).Invoke(null, null));
                    applied.Clear(); applied.Add(oldSpawn);
                    Arm();
                    try { events.GetMethod("ApplyActiveDefinitionsLocked", All).Invoke(null, new object[] { "failure fixture" }); throw new Exception("event fault did not propagate"); }
                    catch (TargetInvocationException error) when (error.InnerException is MissingMethodException) { }
                    Check(constantCalls == 2 && ReferenceEquals(system.m_events, oldEvents) && ReferenceEquals(events.GetField("EventMetadata", All).GetValue(null), metadata) &&
                        oldEvent.m_forceEnvironment == "old-env" && oldEvent.m_forceMusic == "old-music", "failed event candidate retains previous event list, metadata, environment and music");
                    Check(applied.Count == 1 && ReferenceEquals(applied[0], oldSpawn) && (bool)hasPayload.Invoke(null, new object[] { oldSpawn }) &&
                        Seen.All(spawn => !(bool)hasPayload.Invoke(null, new object[] { spawn })), "failed event candidate cleans new payloads but preserves old payload ownership");
                    constantField.SetValue(null, originalConstant);
                    definitions.Clear();
                    events.GetMethod("ApplyActiveDefinitionsLocked", All).Invoke(null, new object[] { "intentional empty" });
                    Check(system.m_events.Count == 0 && applied.Count == 0 && !(bool)hasPayload.Invoke(null, new object[] { oldSpawn }),
                        "valid empty event definitions still activate and retire previous payloads after recovery");
                }
                finally
                {
                    constantField.SetValue(null, originalConstant);
                    for (int index = restore.Count - 1; index >= 0; index--) restore[index]();
                    fixture.UnpatchSelf();
                    File.Delete(yamlPath);
                }
                object Definition() { object value = Activator.CreateInstance(definitionType, true); Set(value, "Faction", "ForestMonsters"); return value; }
                object Entry(string name) { object value = Activator.CreateInstance(entryType, true); Set(value, "Prefab", name); Set(value, "SpawnSystem", Definition()); return value; }
                void Arm() { constantCalls = 0; Seen.Clear(); constantField.SetValue(null, (Func<string, object>)FailingConstant); }
            }
        }

        private sealed class EwdValueFields : UnityEngine.MonoBehaviour
        {
            public int Count = 0;
            public float Speed = 0;
            public bool Active = false;
            public long Ticks = 0;
            public string Label = "";
            public UnityEngine.Vector3 Position = default;
            public UnityEngine.Quaternion Rotation = default;
        }

        private static void CheckEwdValueFactories(Assembly mod)
        {
            Assembly ewd = Assembly.Load("ExpandWorldData");
            Type support = mod.GetType("DropNSpawn.SpawnSystemCustomDataSupport", true);
            Type entryType = mod.GetType("DropNSpawn.CanonicalSpawnSystemEntry", true);
            object entry = Activator.CreateInstance(entryType, true);
            PropertyInfo definitionProperty = entryType.GetProperty("SpawnSystem", All);
            object definition = Activator.CreateInstance(definitionProperty.PropertyType, true);
            definitionProperty.SetValue(entry, definition);
            const string literal = "ForestMonsters,<literal>,AnimalsVeg";
            definition.GetType().GetProperty("Faction", All).SetValue(definition, literal);
            definition.GetType().GetProperty("Objects", All).SetValue(definition, new List<string> { "Wood,1,2,3,1" });
            object payload = support.GetMethod("BuildPreparedPayload", All).Invoke(null, new[] { new SpawnSystem.SpawnData(), entry, "EWD fixture" });
            object data = payload.GetType().GetProperty("CustomData").GetValue(payload);
            var strings = (System.Collections.IDictionary)data.GetType().GetField("Strings").GetValue(data);
            Check((string)ReadValue(strings[literalHash("faction")]) == literal, "EWD faction constant remains literal (commas/value-group tokens are not parsed)");
            var objects = (System.Collections.IList)payload.GetType().GetProperty("CustomObjects").GetValue(payload);
            Check(objects.Count == 1 && objects[0].GetType().FullName == "ExpandWorldData.BlueprintObject", "EWD object payload still builds alongside faction");
            object plain = support.GetMethod("BuildPreparedPayload", All).Invoke(null, new[] { new SpawnSystem.SpawnData(), Activator.CreateInstance(entryType, true), "plain fixture" });
            Check(plain == null, "plain spawn still prepares successfully with EWD installed");

            object component = FormatterServices.GetUninitializedObject(typeof(EwdValueFields));
            Type fields = mod.GetType("DropNSpawn.ExpandWorldFieldOverrideSupport", true);
            // Dynamic rotation is parsed but not evaluated: numeric Euler conversion
            // belongs to Unity's native engine, which this managed probe does not host.
            foreach (var value in new[] { ("Count", "2"), ("Speed", "2.5"), ("Active", "true"), ("Ticks", "123"), ("Label", "literal"), ("Position", "1,2,3"), ("Rotation", "<rotation>") })
            {
                object custom = Activator.CreateInstance(ewd.GetType("Data.DataEntry", true));
                fields.GetMethod("InsertReflectedField", All).Invoke(null, new[] { custom, component, typeof(EwdValueFields).GetField(value.Item1), value.Item2 });
                var ints = (System.Collections.IDictionary)custom.GetType().GetField("Ints").GetValue(custom);
                Check((int)ReadValue(ints[literalHash("HasFields")]) == 1 && (int)ReadValue(ints[literalHash("HasFieldsEwdValueFields")]) == 1,
                    "EWD reflected " + value.Item1 + " preserves both HasFields markers");
                string map = value.Item1 == "Count" ? "Ints" : value.Item1 == "Speed" ? "Floats" : value.Item1 == "Active" ? "Bools" : value.Item1 == "Ticks" ? "Longs" : value.Item1 == "Position" ? "Vecs" : value.Item1 == "Rotation" ? "Quats" : "Strings";
                Check(((System.Collections.IDictionary)custom.GetType().GetField(map).GetValue(custom)).Contains(literalHash("EwdValueFields." + value.Item1)), "EWD reflected value retained: " + value.Item1);
            }
            object ReadValue(object value) => value.GetType().GetMethod("Get", All).Invoke(value, new object[] { null });
            int literalHash(string text) => text.GetStableHashCode();
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
