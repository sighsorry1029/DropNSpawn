using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn.Tests
{
    // Execute the original private loop and production timer/count patches.
    // Scene queries, RNG and physical creation are observations, not a second
    // implementation of the spawning algorithm. Counts remain a pre-list snapshot.
    internal static class SpawnLimitProbe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static GameObject prefabA, prefabB;
        private static int existingA, existingB, spawnedA, spawnedB;
        private static bool rejectA, throwA;
        private static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

        internal static int Run(Assembly mod)
        {
            int checks = 0;
            void Check(bool value, string message)
            {
                if (!value) throw new InvalidOperationException("Spawn limits: " + message);
                checks++;
            }
            var fixture = new Harmony("DropNSpawn.isolated.spawn.limit.scene");
            var production = new Harmony("DropNSpawn.isolated.spawn.limit.production");
            Type timers = mod.GetType("DropNSpawn.SpawnSystemTimers", true);
            Type patch = mod.GetType("DropNSpawn.SpawnSystemSpawnLimitPatch", true);
            MethodInfo update = AccessTools.Method(typeof(SpawnSystem), "UpdateSpawnList");
            var system = Uninitialized<SpawnSystem>();
            var view = Uninitialized<ZNetView>();
            var heightmap = Uninitialized<Heightmap>();
            AccessTools.Field(typeof(Heightmap), "m_cornerAltBiomes").SetValue(heightmap, new List<AltBiome>());
            AccessTools.Field(typeof(SpawnSystem), "m_nview").SetValue(system, view);
            AccessTools.Field(typeof(SpawnSystem), "m_heightmap").SetValue(system, heightmap);
            var list = Uninitialized<SpawnSystemList>();
            var a = new SpawnSystem.SpawnData();
            var b = new SpawnSystem.SpawnData();
            list.m_spawners = new List<SpawnSystem.SpawnData> { a, b };
            system.m_spawnLists = new List<SpawnSystemList> { list };
            prefabA = Uninitialized<GameObject>(); prefabB = Uninitialized<GameObject>();

            Type entryType = mod.GetType("DropNSpawn.CanonicalSpawnSystemEntry", true);
            var entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));
            foreach (string name in new[] { "CountFixtureA", "CountFixtureB" })
            {
                object entry = Activator.CreateInstance(entryType);
                entryType.GetProperty("Prefab").SetValue(entry, name);
                entries.Add(entry);
            }
            Array ids = (Array)timers.GetMethod("CreateIds", All).Invoke(null, new object[] { entries });
            Type idType = ids.GetType().GetElementType();
            object configured = Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(idType), new object[] { ids });
            var rows = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(SpawnSystem.SpawnData), idType));
            rows.Add(a, ids.GetValue(0)); rows.Add(b, ids.GetValue(1));
            MethodInfo attach = timers.GetMethod("Attach", All), begin = timers.GetMethod("Begin", All),
                end = timers.GetMethod("End", All), write = timers.GetMethod("WriteTime", All);
            FieldInfo net = AccessTools.Field(typeof(ZNet), "m_instance"), world = AccessTools.Field(typeof(ZDOMan), "s_instance");
            object oldNet = net.GetValue(null), oldWorld = world.GetValue(null);
            PropertyInfo setting = mod.GetType("DropNSpawn.PluginBoundSettings", true).GetProperty("EnableSpawnSystemOverrides", All);
            object oldSetting = setting.GetValue(null);
            Type toggle = setting.PropertyType.GetGenericArguments()[0];
            var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "dns-unsaved-spawn-limit-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            MethodInfo bind = typeof(ConfigFile).GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType == typeof(ConfigDescription));
            object enabledEntry = bind.MakeGenericMethod(toggle).Invoke(config, new object[] { "Test", "Enabled", Enum.Parse(toggle, "On"), new ConfigDescription("") });
            void Enabled(bool value) => setting.PropertyType.GetProperty("Value").SetValue(enabledEntry, Enum.Parse(toggle, value ? "On" : "Off"));
            ZDO zdo = null;
            int identity = 0;
            DateTime now = new DateTime(TimeSpan.FromHours(20).Ticks);
            bool isEvent = false;
            string salt = "b_";
            List<SpawnSystem.SpawnData> active = list.m_spawners;
            void Reset(bool managed = true, bool events = false, string group = "b_", bool owned = true)
            {
                Enabled(managed); isEvent = events; salt = group;
                active = owned ? list.m_spawners : new List<SpawnSystem.SpawnData>(list.m_spawners);
                existingA = existingB = spawnedA = spawnedB = 0;
                rejectA = throwA = false;
                a.m_prefab = prefabA; b.m_prefab = prefabB;
                foreach (var row in list.m_spawners)
                {
                    row.m_enabled = true; row.m_spawnChance = 100; row.m_spawnDistance = 0;
                    row.m_spawnAtDay = row.m_spawnAtNight = true;
                    row.m_spawnInterval = 60; row.m_maxSpawned = 3;
                    row.m_groupSizeMin = row.m_groupSizeMax = 3;
                }
                b.m_spawnInterval = 95; b.m_maxSpawned = 1;
                b.m_groupSizeMin = b.m_groupSizeMax = 1;
                zdo = Uninitialized<ZDO>(); zdo.m_uid = new ZDOID(9812346L, (uint)(96000 + ++identity));
                AccessTools.Property(typeof(ZDO), "Owner").SetValue(zdo, true);
                AccessTools.Property(typeof(ZDO), "Valid").SetValue(zdo, true);
                AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
                attach.Invoke(null, new object[] { system, configured, rows, system.m_spawnLists });
            }
            void Seed()
            {
                object[] args = { system, active, isEvent, salt, view, null };
                Check((bool)begin.Invoke(null, args), "owner can seed one due attempt per rule");
                for (int i = 0; i < active.Count; i++)
                    write.Invoke(null, new object[] { zdo, (salt + Name(active[i].m_prefab) + (i + 1)).GetStableHashCode(),
                        now.AddSeconds(-active[i].m_spawnInterval).Ticks, system, active[i], isEvent, salt });
                end.Invoke(null, new[] { args[5] });
            }
            void RunAt(DateTime time)
            {
                spawnedA = spawnedB = 0;
                update.Invoke(system, new object[] { active, time, isEvent, salt });
            }
            try
            {
                setting.SetValue(null, enabledEntry);
                var fakeNet = Uninitialized<ZNet>();
                AccessTools.Field(typeof(ZNet), "m_isServer").SetValue(fakeNet, true);
                net.SetValue(null, fakeNet); world.SetValue(null, Uninitialized<ZDOMan>());
                fixture.Patch(begin, transpiler: new HarmonyMethod(typeof(SpawnLimitProbe), nameof(Scene)));
                fixture.Patch(patch.GetMethod("CountForLimit", All), transpiler: new HarmonyMethod(typeof(SpawnLimitProbe), nameof(Scene)));
                fixture.Patch(patch.GetMethod("RecordSpawn", All), transpiler: new HarmonyMethod(typeof(SpawnLimitProbe), nameof(Scene)));
                // Install scene substitutions last: production sees the real IL contract.
                fixture.Patch(update, transpiler: new HarmonyMethod(typeof(SpawnLimitProbe), nameof(Scene)) { priority = Priority.Last });
                production.CreateClassProcessor(mod.GetType("DropNSpawn.SpawnSystemTimerPatch", true)).Patch();

                Reset(); Seed(); RunAt(now);
                Check(spawnedA == 3 && spawnedB == 0, "unfixed native loop reproduces cross-prefab suppression with 60/95-second intervals");
                production.CreateClassProcessor(patch).Patch();
                Reset(); Seed(); RunAt(now);
                Check(spawnedA == 3 && spawnedB == 1, "corrected loop gives B its own slot after A spawns three");
                RunAt(now);
                Check(spawnedA == 0 && spawnedB == 0, "same-time call does not bypass consumed timers");
                RunAt(now.AddSeconds(95));
                Check(spawnedA == 3 && spawnedB == 1, "next invocation has fresh counts; 60/95-second timing stays native");

                Reset(); b.m_prefab = prefabA; a.m_groupSizeMin = a.m_groupSizeMax = 2;
                b.m_maxSpawned = 3; b.m_groupSizeMin = b.m_groupSizeMax = 2; Seed(); RunAt(now);
                Check(spawnedA == 3, "same prefab in separate rules shares remaining group capacity, not four spawns");
                Reset(); existingA = 2; a.m_groupSizeMin = a.m_groupSizeMax = 4; Seed(); RunAt(now);
                Check(spawnedA == 1 && spawnedB == 1, "existing same-prefab instances consume only their own remaining slot");
                Reset(); a.m_groupSizeMin = a.m_groupSizeMax = 2; b.m_enabled = false; RunAt(now);
                Check(spawnedA == 3, "multiple catch-up groups cannot exceed the same-prefab cap");
                Reset(); a.m_maxSpawned = 0; a.m_groupSizeMin = a.m_groupSizeMax = 4; RunAt(now);
                Check(spawnedA == 4 && spawnedB == 1, "unlimited row keeps native single catch-up attempt without blocking other prefabs");
                Reset(); a.m_spawnChance = 0; Seed(); RunAt(now);
                Check(spawnedA == 0 && spawnedB == 1, "failed chance does not reserve slots");
                Reset(); rejectA = true; Seed(); RunAt(now);
                Check(spawnedA == 0 && spawnedB == 1, "failed placement does not reserve slots");

                foreach (int bypass in new[] { 0, 1, 2, 3 })
                {
                    Reset(managed: bypass != 0, events: bypass == 1, group: bypass == 2 ? "m0" : "b_", owned: bypass != 3);
                    Seed(); RunAt(now);
                    Check(spawnedA == 3 && spawnedB == 0, "native calculation retained for Off/event/AltBiome/unmanaged list: " + bypass);
                }
                Reset(); Seed(); Enabled(false); RunAt(now);
                Check(spawnedA == 3 && spawnedB == 0, "live domain Off bypasses correction on an already attached list");
                Enabled(true); RunAt(now);
                Check(spawnedA == 3 && spawnedB == 1, "live domain On restores correction without replacing the binding");

                Reset(); Seed(); throwA = true;
                bool threw = false;
                try { RunAt(now); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { threw = true; }
                Check(threw, "spawn exception is not swallowed by count correction");
                throwA = false; RunAt(now);
                Check(spawnedA == 0 && spawnedB == 1, "exception consumes A timer only; new invocation has no stale count or timer batch");
                System.Console.WriteLine("PASS: " + checks + " original-loop spawn limit checks (scene/spawn calls isolated).");
            }
            finally
            {
                timers.GetMethod("Detach", All).Invoke(null, new object[] { system });
                production.UnpatchSelf(); fixture.UnpatchSelf();
                setting.SetValue(null, oldSetting); net.SetValue(null, oldNet); world.SetValue(null, oldWorld);
                prefabA = prefabB = null;
            }
            return checks;
        }

        private static IEnumerable<CodeInstruction> Scene(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.operand is MethodInfo method)
                {
                    string name = null;
                    if (method.DeclaringType == typeof(UnityEngine.Object))
                        name = method.Name == "get_name" ? nameof(Name) : method.Name == "op_Equality" ? nameof(Equal) : method.Name == "op_Inequality" ? nameof(Different) : null;
                    else if (method.DeclaringType == typeof(Component) && method.Name == "get_transform") name = nameof(Transform);
                    else if (method.DeclaringType == typeof(Transform) && method.Name == "get_position") name = nameof(Position);
                    else if (method.DeclaringType == typeof(ZDOMan) && method.Name == "FindSectorObjects") name = nameof(Sectors);
                    else if (method.DeclaringType == typeof(Heightmap) && method.Name == "HaveBiome") name = nameof(Biome);
                    else if (method.DeclaringType == typeof(UnityEngine.Random))
                        name = method.Name == "get_insideUnitCircle" ? nameof(Circle) : method.Name == "Range" ? (method.ReturnType == typeof(int) ? nameof(Group) : nameof(Roll)) : null;
                    else if (method.DeclaringType == typeof(SpawnSystem))
                        name = method.Name == "GetNrOfZDOInstances" ? nameof(Count) : method.Name == "FindBaseSpawnPoint" ? nameof(Center)
                            : method.Name == "IsSpawnPointGood" ? nameof(Good) : method.Name == "Spawn" ? nameof(Spawn) : null;
                    else if (method.DeclaringType == typeof(ZLog) && method.Name == "Log") name = nameof(Log);
                    if (name != null) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(SpawnLimitProbe), name); }
                }
                yield return instruction;
            }
        }
        private static bool Equal(UnityEngine.Object a, UnityEngine.Object b) => ReferenceEquals(a, b);
        private static bool Different(UnityEngine.Object a, UnityEngine.Object b) => !ReferenceEquals(a, b);
        private static string Name(UnityEngine.Object value) => ReferenceEquals(value, prefabA) ? "CountFixtureA" : "CountFixtureB";
        private static Transform Transform(Component component) => null;
        private static Vector3 Position(Transform transform) => default;
        private static void Sectors(ZDOMan manager, Vector2s sector, SimulationDistance distance, List<ZDO> objects, List<ZDO> distant) { }
        private static bool Biome(Heightmap map, Heightmap.Biome biome) => true;
        private static Vector2 Circle() => default;
        private static int Group(int min, int max) => max - 1;
        private static float Roll(float min, float max) => (min + max) / 2;
        private static int Count(GameObject prefab, List<ZDO> objects, bool events) => ReferenceEquals(prefab, prefabA) ? existingA : existingB;
        private static bool Center(SpawnSystem system, SpawnSystem.SpawnData row, List<Player> players, out Vector3 center, out Player player)
        { center = default; player = null; return !(rejectA && ReferenceEquals(row.m_prefab, prefabA)); }
        private static bool Good(SpawnSystem system, SpawnSystem.SpawnData row, ref Vector3 point) => true;
        private static void Spawn(SpawnSystem system, SpawnSystem.SpawnData row, Vector3 point, bool events)
        {
            if (ReferenceEquals(row.m_prefab, prefabA))
            {
                if (throwA) throw new InvalidOperationException("fixture spawn failure");
                spawnedA++;
            }
            else spawnedB++;
        }
        private static void Log(object message) { }
    }
}
