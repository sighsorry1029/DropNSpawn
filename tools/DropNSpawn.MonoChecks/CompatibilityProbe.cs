using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
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
        }

        private static void ObserveEvent() { postfixCalls++; }
        private static void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("FAIL: " + label);
            checks++;
        }
    }
}
