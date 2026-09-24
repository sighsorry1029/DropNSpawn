using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace DropNSpawn.Tests
{
    // Original game assemblies and the installed Harmony run under Unity's Mono.
    // Objects below are managed field fixtures; no Unity scene or network is started.
    public static class CompatibilityProbe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static int checks;
        private static int postfixCalls;

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
            CheckEwdCompatibility(mod);
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
            MethodInfo spawn = spawnPatcher.GetMethod("Patch");
            MethodInfo events = eventPatcher.GetMethod("Patch");
            Type compat = mod.GetType("DropNSpawn.ExpandWorldDataCompatibility", true);
            try
            {
                // Reproduce EWD.Awake-before-DNS order, including already JITted gates.
                // No RandEventSystem scene exists here; avoid its native null check
                // during the unpatched spawn patcher's active-event enumeration.
                var eventSwitch = (ConfigEntry<bool>)configType.GetField("configDataEvents").GetValue(null);
                eventSwitch.Value = false;
                spawn.Invoke(null, new object[] { upstream });
                events.Invoke(null, new object[] { upstream });
                Check(HasOwner(typeof(SpawnSystem), "Awake", upstream.Id), "EWD normal spawn lifecycle initially enabled");
                // Installing the upstream scheduler needs Player/Animator native
                // initialization. Verify its disabled gates here; removal of an
                // already-installed scheduler remains an in-game check.
                eventSwitch.Value = true;
                // Supply the EWD Harmony instance directly: full EWD/ServerSync
                // startup needs Unity and is intentionally outside this probe.
                compat.GetMethod("InstallPatches", All).Invoke(null, new object[] { compatibility, ewd, upstream });
                Check(!HasOwner(typeof(SpawnSystem), "Awake", upstream.Id), "DNS removes already-installed EWD spawn lifecycle");
                Check(!HasOwner(typeof(RandEventSystem), "FixedUpdate", upstream.Id), "DNS keeps EWD scheduler disabled despite saved true setting");
                spawn.Invoke(null, new object[] { upstream });
                events.Invoke(null, new object[] { upstream });
                Check(!HasOwner(typeof(SpawnSystem), "Awake", upstream.Id) && !HasOwner(typeof(RandEventSystem), "FixedUpdate", upstream.Id),
                    "EWD runtime patch refresh cannot reclaim DNS domains");
                foreach (string domain in new[] { "Spawn", "Event" })
                {
                    Type manager = ewd.GetType("ExpandWorld." + domain + ".Manager", true);
                    foreach (string name in new[] { "CreateConfig", "ReadConfig", "Toggle" })
                        manager.GetMethod(name, All).Invoke(null, null);
                    foreach (string name in new[] { "FromSetting", "Set" })
                        manager.GetMethod(name, All).Invoke(null, new object[] { "invalid YAML: [" });
                    manager.GetMethod(domain == "Spawn" ? "ApplySpawnData" : "ApplyTiming", All).Invoke(null, new object[] { null });
                    Check(true, "EWD " + domain + " direct/reload/sync entrypoints skipped without scene access");
                }
                Type dtoType = ewd.GetType("ExpandWorld.Spawn.Data", true);
                object dto = Activator.CreateInstance(dtoType);
                dtoType.GetField("drops").SetValue(dto, "test_drop");
                object customData = ewd.GetType("ExpandWorld.Spawn.LoaderFields", true).GetMethod("HandleCustomData")
                    .Invoke(null, new[] { dto, (object)new SpawnSystem.SpawnData() });
                Check(customData == null, "EWD drop conversion is disabled despite saved true setting");
                foreach (string name in switches)
                    Check(((ConfigEntry<bool>)configType.GetField(name).GetValue(null)).Value, "EWD user setting preserved: " + name);
                Check(!File.Exists(configPath), "EWD compatibility did not create/save config");
            }
            finally
            {
                compatibility.UnpatchSelf();
                upstream.UnpatchSelf();
            }
        }

        private static bool HasOwner(Type type, string method, string owner)
        {
            var info = Harmony.GetPatchInfo(AccessTools.Method(type, method));
            return info != null && info.Owners.Contains(owner);
        }

        private static void ObserveEvent() { postfixCalls++; }
        private static void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("FAIL: " + label);
            checks++;
        }
    }
}
