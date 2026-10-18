using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace DropNSpawn.Tests
{
    // Exercise the shipped snapshot/restore/build methods. Only native Unity identity,
    // component lookup, registry input and the file/sync completion boundary are fixtures.
    internal static class ObjectSnapshotProbe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private sealed class Identity : IEqualityComparer<UObject>
        {
            public bool Equals(UObject x, UObject y) => ReferenceEquals(x, y);
            public int GetHashCode(UObject value) => RuntimeHelpers.GetHashCode(value);
        }
        private sealed class NativeState
        {
            internal string Name;
            internal int Id;
            internal bool Alive = true;
            internal Destructible Component;
        }
        private static readonly Dictionary<UObject, NativeState> Native = new Dictionary<UObject, NativeState>(new Identity());
        private static readonly List<GameObject> Registry = new List<GameObject>();
        private static Type manager;
        private static object snapshots;
        private static ZNetScene scene;
        private static ObjectDB database;
        private static int checks, completed, frame;

        internal static int Run(Assembly mod)
        {
            manager = mod.GetType("DropNSpawn.ObjectDropManager", true);
            snapshots = manager.GetField("SnapshotState", All).GetValue(null);
            scene = Make<ZNetScene>("scene", 1);
            database = Make<ObjectDB>("db", 2);
            database.m_items = new List<GameObject>();
            var harmony = new Harmony("DropNSpawn.isolated.object.snapshots");
            try
            {
                foreach (string name in new[] {
                    "CaptureSnapshot", "CreateHealthSnapshot", "CreateMinToolTierSnapshot", "CreateRestoreMask", "BuildConfigurationEntry", "NormalizeReferencePrefabName",
                    "RestoreSnapshots", "RestoreConfiguredComponents", "RestoreResourceComponents", "RestoreInteractiveComponents",
                    "IsGameDataReady", "ComputeGameDataSignature", "ComputeGameDataSignatureCore", "ComputeSnapshotSignature", "ComputeSnapshotSignatureCore",
                    "HashGameObjectCollection", "HashSnapshotGameObjectCollection", "HashPrefabNameCollection", "ProcessPendingSnapshotBuildStep" })
                {
                    foreach (MethodInfo method in manager.GetMethods(All).Where(method => method.Name == name))
                        harmony.Patch(method, transpiler: Hook(nameof(Isolate)));
                }
                foreach (string name in new[] { "GetOrCapture", "PruneDestroyedBaselines" })
                {
                    MethodInfo method = snapshots.GetType().GetMethod(name, All);
                    if (method != null) harmony.Patch(method, transpiler: Hook(nameof(Isolate)));
                }
                harmony.Patch(manager.GetMethod("EnumerateRelevantPrefabs", All), prefix: Hook(nameof(ReadRegistry)));
                harmony.Patch(manager.GetMethod("CompleteGameDataReadyLocked", All), prefix: Hook(nameof(Complete)));

                GameObject dead = Prefab("PT_BlueFissureRock", 10, 80);
                GameObject good = Prefab("Rock", 11, 100);
                object old = Call("CaptureSnapshot", dead), healthy = Call("CaptureSnapshot", good);
                Native[dead].Alive = false;
                // This first assertion reproduces the reported GetName failure on the pre-fix DLL.
                object mask = Call("CreateRestoreMask", old);
                Check((string)Get(mask, "Prefab") == "PT_BlueFissureRock", "destroyed source restore mask uses captured name without a native GetName call");
                Check((string)Get(Call("BuildConfigurationEntry", old), "Prefab") == "PT_BlueFissureRock", "reference metadata can read a retained baseline without dereferencing its destroyed source");
                List().Add(old); List().Add(healthy);
                Names().Add("PT_BlueFissureRock", old); Names().Add("Rock", healthy);
                Native[good].Component.m_health = 999;
                Call("RestoreSnapshots", (object)null);
                Check(Native[good].Component.m_health == 100, "full restore skips destroyed source and continues to healthy templates");
                Native[good].Component.m_health = 999;
                Call("RestoreSnapshots", new HashSet<string> { "PT_BlueFissureRock", "Rock" });
                Check(Native[good].Component.m_health == 100, "targeted restore skips destroyed source and restores the remaining target");
                GameObject live = Prefab("PT_BlueFissureRock(Clone)", 12, 999);
                Call("RestoreConfiguredComponents", live, old, mask, false);
                Check(Native[live].Component.m_health == 80, "surviving instance can still use cached scalar baseline after source destruction");
                Call("RestoreConfiguredComponents", dead, old, mask, false);
                Check(Native[dead].Component.m_health == 80, "destroyed restore target is ignored");

                State("Clear");
                Registry.Add(good);
                Build();
                healthy = Names()["Rock"];
                Native[good].Component.m_health = 999;
                GameObject replacement = Prefab("PT_BlueFissureRock", 13, 250);
                Registry.Add(replacement);
                Build();
                Check(ReferenceEquals(Names()["Rock"], healthy) && Health(Names()["Rock"]) == 100,
                    "registry refresh preserves original baseline of a surviving DNS-modified template");
                Check(Health(Names()["PT_BlueFissureRock"]) == 250, "same-name replacement captures its own defaults, not the destroyed object's baseline");
                Call("RestoreSnapshots", (object)null);
                Check(Native[good].Component.m_health == 100 && Native[replacement].Component.m_health == 250,
                    "restore after replacement preserves both old survivor defaults and new replacement defaults");

                Native[good].Component.m_health = 999;
                Registry.RemoveAll(value => ReferenceEquals(value, good)); Build();
                Registry.Add(good); Build();
                Check(ReferenceEquals(Names()["Rock"], healthy) && Health(Names()["Rock"]) == 100,
                    "temporarily unregistered surviving template does not capture its overridden value on return");
                int beforeRename = (int)Call("ComputeSnapshotSignatureCore");
                Native[good].Name = "RenamedRock";
                int sameIdentity = (int)Call("ComputeGameDataSignatureCore");
                SetStatic("_lastAppliedGameDataSignature", sameIdentity);
                Build();
                Check(!Names().Contains("Rock") && Health(Names()["RenamedRock"]) == 100 &&
                    (string)Get(healthy, "PrefabName") == "Rock", "rename updates published name without mutating earlier snapshot or recapturing overrides");
                Check(beforeRename != (int)Call("ComputeSnapshotSignatureCore") &&
                    manager.GetField("_lastAppliedGameDataSignature", All).GetValue(null) == null,
                    "name-only refresh invalidates both snapshot binding and last-applied state");

                GameObject duplicate = Prefab("RenamedRock", 14, 400);
                Registry.Add(duplicate); Build();
                Check(List().Count == 3 && ReferenceEquals(Get(Names()["RenamedRock"], "Prefab"), good),
                    "duplicate-name registry keeps all snapshots while lookup selects first entry");
                Registry.RemoveAll(value => ReferenceEquals(value, duplicate)); Registry.Insert(0, duplicate); Build();
                Check(ReferenceEquals(Get(Names()["RenamedRock"], "Prefab"), duplicate), "duplicate-name registry reorder updates first-wins lookup");

                GameObject reuse = Prefab("ReusedId", 14, 500);
                Native[duplicate].Alive = false;
                Registry.RemoveAll(value => ReferenceEquals(value, duplicate)); Registry.Insert(0, reuse); Build();
                Check(Health(Names()["ReusedId"]) == 500, "reused native ID never reuses a different managed object's baseline");
                Check((int)StateCache().Count == 3, "refresh prunes destroyed baselines but retains living registry members");

                int publishes = completed;
                Schedule();
                object pending = manager.GetField("_pendingSnapshotBuild", All).GetValue(null);
                Call("ApplyIfReady", false);
                Check(ReferenceEquals(pending, manager.GetField("_pendingSnapshotBuild", All).GetValue(null)) && completed == publishes,
                    "YAML apply during incremental capture cannot publish or bypass the pending snapshot build");
                Call("ProcessPendingSnapshotBuildStep", double.MaxValue);
                GameObject later = Prefab("ReusedId", 15, 600);
                Native[reuse].Alive = false;
                Registry[0] = later;
                // Deliberately keep the same frame: publication must not use cached signatures.
                for (int i = 0; i < 2; i++) Call("ProcessPendingSnapshotBuildStep", double.MaxValue);
                Check(completed == publishes && !ReferenceEquals(pending, manager.GetField("_pendingSnapshotBuild", All).GetValue(null)),
                    "replacement between incremental steps reschedules instead of publishing a stale build in the same frame");
                object dropState = manager.GetField("DropRuntimeState", All).GetValue(null);
                Put(dropState, "GameDataSignature", 42);
                Finish();
                Check(Health(Names()["ReusedId"]) == 600 && completed == publishes + 1 && Get(dropState, "GameDataSignature") == null,
                    "replacement build publishes once and invalidates compiled drop bindings");

                Schedule(); publishes = completed;
                Native[scene].Alive = false;
                Finish();
                Check(completed == publishes, "scene loss cancels pending capture without publishing or applying it");
                Native[scene].Alive = true;
                Registry.Clear(); Build();
                Check(List().Count == 0 && Names().Count == 0 &&
                    (int)manager.GetField("_publishedSnapshotSignature", All).GetValue(null) == (int)Call("ComputeSnapshotSignatureCore"),
                    "empty registry is a valid published state, not a request to rebuild forever");

                GameObject watched = Prefab("Watched", 20, 60);
                Registry.Add(watched); Build();
                SetStatic("_initialized", true);
                Registry[0] = Prefab("Watched", 21, 70);
                Native[watched].Alive = false;
                frame++;
                Call("OnGameDataReady", "same-name replacement");
                Check((bool)Call("HasPendingSnapshotBuildWork"), "game-data-ready entrypoint automatically detects same-name replacement");
                Finish();
                Check(Health(Names()["Watched"]) == 70, "automatic refresh publishes replacement defaults without manual snapshot invalidation");
                Registry[0] = Prefab("Watched", 22, 90);
                frame++;
                Call("ApplyIfReady", false);
                Check((bool)Call("HasPendingSnapshotBuildWork"), "reload entrypoint schedules fresh bindings before applying against a changed registry");
                Finish();
                return checks;
            }
            finally
            {
                harmony.UnpatchSelf();
                State("Clear");
                SetStatic("_pendingSnapshotBuild", null);
                SetStatic("_initialized", false);
                foreach (string field in new[] { "_publishedSnapshotSignature", "_lastAppliedGameDataSignature", "_lastProcessedGameDataSignature", "_lastProcessedSnapshotSignature" })
                    manager.GetField(field, All)?.SetValue(null, null);
                SetStatic("_cachedGameDataSignatureFrame", -1); SetStatic("_cachedSnapshotSignatureFrame", -1);
                Registry.Clear(); Native.Clear();
            }
        }

        private static object Call(string name, params object[] args) => manager.GetMethod(name, All).Invoke(null, args);
        private static object State(string name, params object[] args) => snapshots.GetType().GetMethod(name, All).Invoke(snapshots, args);
        private static object Get(object value, string name)
        {
            if (value == null) throw new InvalidOperationException("Missing fixture value for " + name + " after " + checks + " checks; published names=" + string.Join(",", Names().Keys.Cast<string>()));
            return value.GetType().GetProperty(name, All).GetValue(value);
        }
        private static void Put(object value, string name, object data) => value.GetType().GetProperty(name, All).SetValue(value, data);
        private static IList List() => (IList)Get(snapshots, "Snapshots");
        private static IDictionary Names() => (IDictionary)Get(snapshots, "SnapshotsByPrefab");
        private static IDictionary StateCache() => (IDictionary)snapshots.GetType().GetField("_baselinesByInstance", All).GetValue(snapshots);
        private static float Health(object value) => (float)Get(Get(value, "Health"), "Destructible");
        private static void SetStatic(string name, object value) => manager.GetField(name, All).SetValue(null, value);
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Object snapshot: " + message);
            checks++;
        }
        private static void Schedule()
        {
            frame++;
            Call("ScheduleSnapshotBuildLocked", "fixture", Call("ComputeGameDataSignatureCore"), Call("ComputeSnapshotSignatureCore"));
        }
        private static void Finish()
        {
            for (int i = 0; i < 20; i++)
            {
                if (!(bool)Call("HasPendingSnapshotBuildWork")) return;
                Call("ProcessPendingSnapshotBuildStep", double.MaxValue);
            }
            throw new InvalidOperationException("Object snapshot build did not finish");
        }
        private static void Build() { Schedule(); Finish(); }
        private static T Make<T>(string name, int id) where T : UObject
        {
            var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
            Native.Add(value, new NativeState { Name = name, Id = id });
            return value;
        }
        private static GameObject Prefab(string name, int id, float health)
        {
            GameObject value = Make<GameObject>(name, id);
            Destructible component = Make<Destructible>(name, id + 1000);
            component.m_health = health;
            Native[value].Component = component;
            return value;
        }
        private static bool Alive(UObject value) => !ReferenceEquals(value, null) && Native[value].Alive;
        private static bool Equal(UObject left, UObject right) => !Alive(left) && !Alive(right) || ReferenceEquals(left, right);
        private static bool Different(UObject left, UObject right) => !Equal(left, right);
        private static string Name(UObject value) => Alive(value) ? Native[value].Name : throw new NullReferenceException("native GetName on destroyed object");
        private static int Id(UObject value) => Native[value].Id;
        private static T Component<T>(GameObject value) where T : Component => Alive(value)
            ? Native[value].Component as T
            : throw new NullReferenceException("native component lookup on destroyed object");
        private static bool TryComponent<T>(GameObject value, out T result) where T : Component
        {
            result = Component<T>(value);
            return !ReferenceEquals(result, null);
        }
        private static ZNetScene Scene() => scene;
        private static ObjectDB Database() => database;
        private static int Frame() => frame;
        private static double TimeNow() => 0;
        private static bool ReadRegistry(ref IEnumerable<GameObject> __result) { __result = Registry; return false; }
        private static bool Complete(int gameDataSignature, int snapshotSignature)
        {
            completed++;
            SetStatic("_lastProcessedGameDataSignature", gameDataSignature);
            SetStatic("_lastProcessedSnapshotSignature", snapshotSignature);
            return false;
        }
        private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(ObjectSnapshotProbe), name);
        private static IEnumerable<CodeInstruction> Isolate(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                MethodInfo replacement = null;
                if (instruction.operand is MethodInfo method)
                {
                    string target = null;
                    if (method.DeclaringType == typeof(UObject))
                        target = method.Name == "op_Equality" ? nameof(Equal) : method.Name == "op_Inequality" ? nameof(Different) :
                            method.Name == "get_name" ? nameof(Name) : method.Name == "GetInstanceID" ? nameof(Id) : null;
                    if (method.DeclaringType == typeof(ZNetScene) && method.Name == "get_instance") target = nameof(Scene);
                    if (method.DeclaringType == typeof(ObjectDB) && method.Name == "get_instance") target = nameof(Database);
                    if (method.DeclaringType == typeof(Time)) target = method.Name == "get_frameCount" ? nameof(Frame) : nameof(TimeNow);
                    if (target != null) replacement = typeof(ObjectSnapshotProbe).GetMethod(target, All);
                    if (method.DeclaringType == typeof(GameObject) && method.IsGenericMethod &&
                        (method.Name == "GetComponent" || method.Name == "TryGetComponent"))
                        replacement = typeof(ObjectSnapshotProbe).GetMethod(method.Name == "GetComponent" ? nameof(Component) : nameof(TryComponent), All)
                            .MakeGenericMethod(method.GetGenericArguments());
                }
                if (instruction.opcode == OpCodes.Ldsfld && instruction.operand is FieldInfo field &&
                    field.DeclaringType == typeof(ObjectDB) && field.Name == "instance")
                    replacement = typeof(ObjectSnapshotProbe).GetMethod(nameof(Database), All);
                if (instruction.opcode == OpCodes.Ldsfld && instruction.operand is FieldInfo sceneField &&
                    sceneField.DeclaringType == typeof(ZNetScene) && sceneField.Name == "instance")
                    replacement = typeof(ObjectSnapshotProbe).GetMethod(nameof(Scene), All);
                if (replacement != null) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; }
                yield return instruction;
            }
        }
    }
}
