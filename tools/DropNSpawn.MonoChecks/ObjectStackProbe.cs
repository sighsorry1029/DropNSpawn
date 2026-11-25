using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace DropNSpawn.Tests
{
    // Replace native scene lookups/instantiation only; run game SetStack, owner and ZDO serialization.
    internal static class ObjectStackProbe
    {
        private static readonly ConditionalWeakTable<GameObject, ItemDrop> Items = new ConditionalWeakTable<GameObject, ItemDrop>();
        private static readonly ConditionalWeakTable<ItemDrop, ZNetView> Views = new ConditionalWeakTable<ItemDrop, ZNetView>();
        private static readonly List<ItemDrop> Created = new List<ItemDrop>();
        private static uint seq;
        private static T Native<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

        internal static int Run(Assembly mod)
        {
            int checks = 0;
            void Check(bool ok, string label) { if (!ok) throw new Exception("Object stacks: " + label); checks++; }
            var harmony = new Harmony("DropNSpawn.isolated.object.stacks");
            Type stack = mod.GetType("DropNSpawn.ObjectDropStacking", true), batch = stack.GetNestedType("Batch", AccessTools.all);
            var isolate = new HarmonyMethod(typeof(ObjectStackProbe), nameof(SceneInputs));
            object[] args = { null, default(Vector3), default(Quaternion), true, null };
            GameObject a = Prefab("A"), b = Prefab("B");
            void Emit(GameObject prefab) { args[0] = prefab; AccessTools.Method(stack, "EmitInitialized").Invoke(null, args); }
            var worldLevel = Game.m_worldLevel;
            Type settings = mod.GetType("DropNSpawn.PluginBoundSettings", true);
            var resources = settings.GetProperty("StackResourceDrops", AccessTools.all);
            var harvests = settings.GetProperty("StackHarvestDrops", AccessTools.all);
            var blacklistSetting = settings.GetProperty("ObjectStackItemBlacklist", AccessTools.all);
            object oldBlacklist = blacklistSetting.GetValue(null);
            var domain = settings.GetProperty("EnableObjectOverrides", AccessTools.all);
            object oldResources = resources.GetValue(null), oldHarvests = harvests.GetValue(null), oldDomain = domain.GetValue(null);
            Type toggle = resources.PropertyType.GetGenericArguments()[0];
            var config = new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            ConfigEntryBase Entry(string name, string value) => (ConfigEntryBase)typeof(ConfigFile).GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType == typeof(ConfigDescription))
                .MakeGenericMethod(toggle).Invoke(config, new object[] { "Test", name, Enum.Parse(toggle, value), new ConfigDescription("") });
            try
            {
                var blacklist = config.Bind("Test", "blacklist", "", "");
                blacklistSetting.SetValue(null, blacklist);
                // JIT-compiling the real mining/harvest RPCs initializes Player animation ids.
                // There is no native Animator in the isolated Mono host.
                Type animator = Assembly.Load("UnityEngine.AnimationModule").GetType("UnityEngine.Animator", true);
                harmony.Patch(AccessTools.Method(animator, "StringToHash", new[] { typeof(string) }),
                    transpiler: new HarmonyMethod(typeof(ObjectStackProbe), nameof(AnimationIds)));
                foreach (var type in new[] { "DropOnDestroyedPatch", "MineRockHitPatch", "MineRock5SectionStackPatch",
                             "TreeBaseDamagePatch", "TreeLogDestroyPatch", "PickableHarvestStackScopePatch", "PickableHarvestDropPatch" })
                    harmony.CreateClassProcessor(mod.GetType("DropNSpawn." + type, true)).Patch();
                // Verify Unity Mono emitted actual Harmony replacements (including foreach EH regions).
                Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(MineRock5), "DamageArea")).Transpilers.Any(p => p.owner == harmony.Id),
                    "production damage-area hook installed");
                foreach (MethodInfo method in new[] { AccessTools.Method(stack, "Emit"), AccessTools.Method(batch, "Absorb"),
                             AccessTools.Method(batch, "Remember"), AccessTools.Method(settings, "IsObjectStackItemBlacklisted"),
                             AccessTools.Method(typeof(ItemDrop), "Save", Type.EmptyTypes),
                             AccessTools.Method(typeof(ItemDrop.ItemData), "Save", new[] { typeof(ZPackage) }) })
                    harmony.Patch(method, transpiler: isolate);
                harmony.Patch(AccessTools.Method(typeof(ZDO), "IncreaseDataRevision"),
                    prefix: new HarmonyMethod(typeof(ObjectStackProbe), nameof(NoNetworkNotification)));
                Game.m_worldLevel = 3;
                Created.Clear();
                Emit(a); Emit(b); Emit(a); Emit(a); Emit(a); Emit(a);
                Check(Created.Select(i => i.m_itemData.m_stack).SequenceEqual(new[] { 4, 1, 1 }), "interleaved prefab deliveries and max-stack overflow");
                var saved = new ItemDrop.ItemData();
                ItemDrop.LoadFromZDO(saved, Views.GetValue(Created[0], _ => null).GetZDO());
                Check(saved.m_stack == 4 && saved.m_worldLevel == 3 && saved.m_cheated, "network save read-back retains count and provenance");
                Emit(a);
                var last = Created.Last();
                AccessTools.Property(typeof(ZDO), "Owner").SetValue(Views.GetValue(last, _ => null).GetZDO(), false);
                Emit(a);
                Check(last.m_itemData.m_stack == 2 && !ReferenceEquals(Created.Last(), last), "non-owner does not absorb or consume a unit");
                args[4] = null; Emit(a);
                Check(Created.Count == 5, "a separate section or log starts a fresh batch");
                Created.Last().m_itemData.m_customData["instance"] = "special"; Emit(a);
                Check(Created.Count == 6, "item with instance metadata is not augmented");
                args[3] = false; Emit(a);
                Check(Created.Count == 7 && !Created.Last().m_itemData.m_cheated, "different cheat provenance does not share a stack");
                GameObject preset = Prefab("Preset");
                Items.GetValue(preset, _ => null).m_itemData.m_stack = 2;
                Emit(preset); Emit(preset);
                Check(Created.Skip(7).Select(i => i.m_itemData.m_stack).SequenceEqual(new[] { 2, 2 }), "non-unit prefab keeps its native quantity");
                int start = Created.Count;
                args[4] = null;
                blacklist.Value = "B,  resourceA ; ResourceA"; // B is a display key, ResourceB is its prefab name.
                Emit(a); Emit(b); Emit(a); Emit(b);
                Check(Created.Skip(start).Select(i => i.m_itemData.m_stack).SequenceEqual(new[] { 1, 2, 1 }),
                    "blacklisted resource prefab keeps native drops while other items in the batch stack");
                blacklist.Value = "";
                Emit(a); Emit(a);
                Check(Created.Skip(start).Select(i => i.m_itemData.m_stack).SequenceEqual(new[] { 1, 2, 1, 2 }),
                    "clearing the list re-enables stacking without merging into drops created while excluded");
                // Feed the actual pickable hook a partially filled primary stack followed by a
                // large extra roll. Vanilla clamps that individual roll before it can be added.
                var plant = Native<Pickable>(); var other = Native<Pickable>();
                var begin = AccessTools.Method(stack, "BeginHarvest");
                var scope = AccessTools.Field(stack, "_harvest");
                var resourceOption = Entry("resources", "Off");
                var harvestOption = Entry("harvest", "Off");
                var domainOption = Entry("domain", "On");
                resources.SetValue(null, resourceOption); harvests.SetValue(null, harvestOption); domain.SetValue(null, domainOption);
                object on = Enum.Parse(toggle, "On"), off = Enum.Parse(toggle, "Off");
                bool ResourceEnabled() => (bool)AccessTools.Property(stack, "ResourceEnabled").GetValue(null);
                Check(!ResourceEnabled() && begin.Invoke(null, new object[] { plant }) == null, "both stacking paths can be disabled");
                resourceOption.BoxedValue = on;
                Check(ResourceEnabled() && begin.Invoke(null, new object[] { plant }) == null, "resource stacking alone never starts a harvest scope");
                harvestOption.BoxedValue = on;
                object both = begin.Invoke(null, new object[] { plant });
                Check(ResourceEnabled() && both != null, "both stacking paths can be enabled");
                AccessTools.Method(stack, "EndHarvest").Invoke(null, new[] { both });
                domainOption.BoxedValue = off;
                Check(!ResourceEnabled() && begin.Invoke(null, new object[] { plant }) == null, "Object Overrides Off takes precedence over both settings");
                domainOption.BoxedValue = on; resourceOption.BoxedValue = off;
                object harvest = begin.Invoke(null, new object[] { plant });
                Check(!ResourceEnabled() && harvest != null, "harvest stacking alone does not enable resource stacking");
                try { AccessTools.Method(typeof(Pickable), "RPC_Pick").Invoke(other, new object[] { 17L, 0 }); }
                catch (TargetInvocationException ex) when (ex.InnerException is NullReferenceException) { /* no view in this fixture */ }
                Check(harvest != null && ReferenceEquals(scope.GetValue(null), harvest), "exception in nested real pick RPC restores the outer plant");
                args[4] = null; Emit(a);
                var primary = Created.Last();
                AccessTools.Method(stack, "RememberHarvest").Invoke(null, new object[] { primary, plant, a, 1 });
                object[] extra = { other, a, 9 };
                var absorb = AccessTools.Method(stack, "TryAbsorbHarvest");
                Check(!(bool)absorb.Invoke(null, extra) && (int)extra[2] == 9, "another plant cannot use this harvest");
                AccessTools.Method(typeof(Pickable), "Drop").Invoke(plant, new object[] { a, 4, 1 });
                Check(primary.m_itemData.m_stack == 2, "patched native Pickable.Drop absorbs a whole roll without invoking native spawn");
                extra[0] = plant;
                blacklist.Value = "ResourceA";
                Check(!(bool)absorb.Invoke(null, extra) && (int)extra[2] == 9 && primary.m_itemData.m_stack == 2,
                    "live harvest exclusion preserves the native roll amount and does not augment an open primary stack");
                blacklist.Value = "";
                Check(!(bool)absorb.Invoke(null, extra) && (int)extra[2] == 2 && primary.m_itemData.m_stack == 4,
                    "large extra roll conserves vanilla's clamped yield across two stacks");
                AccessTools.Method(stack, "EndHarvest").Invoke(null, new[] { harvest });
                Check(scope.GetValue(null) == null, "harvest does not retain item or plant references");
            }
            finally
            {
                AccessTools.Field(stack, "_harvest").SetValue(null, null);
                resources.SetValue(null, oldResources); harvests.SetValue(null, oldHarvests); domain.SetValue(null, oldDomain);
                blacklistSetting.SetValue(null, oldBlacklist);
                Game.m_worldLevel = worldLevel; Created.Clear(); harmony.UnpatchSelf();
            }
            return checks;
        }

        private static GameObject Prefab(string name)
        {
            var prefab = Native<GameObject>(); var item = Native<ItemDrop>();
            item.m_itemData = new ItemDrop.ItemData { m_shared = new ItemDrop.ItemData.SharedData { m_name = name, m_maxStackSize = 4 } };
            Items.Add(prefab, item);
            return prefab;
        }
        private static GameObject Spawn(GameObject prefab, Vector3 pos, Quaternion rot)
        {
            var go = Native<GameObject>(); var item = Native<ItemDrop>();
            item.m_itemData = Items.GetValue(prefab, _ => null).m_itemData.Clone();
            var view = Native<ZNetView>(); var zdo = Native<ZDO>();
            zdo.m_uid = new ZDOID(9812347L, ++seq);
            AccessTools.Property(typeof(ZDO), "Owner").SetValue(zdo, true);
            AccessTools.Property(typeof(ZDO), "Valid").SetValue(zdo, true);
            AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
            AccessTools.Field(typeof(ItemDrop), "m_nview").SetValue(item, view);
            Views.Add(item, view); Items.Add(go, item); Created.Add(item); return go;
        }
        private static bool Item(GameObject go, out ItemDrop item) => Items.TryGetValue(go, out item);
        private static bool View(Component component, out ZNetView view) => Views.TryGetValue((ItemDrop)component, out view);
        private static bool Exists(UObject value) => !ReferenceEquals(value, null);
        private static bool Equal(UObject a, UObject b) => ReferenceEquals(a, b);
        private static bool Different(UObject a, UObject b) => !ReferenceEquals(a, b);
        private static IEnumerable<CodeInstruction> AnimationIds() => new[] { new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(string), "GetHashCode", Type.EmptyTypes)), new CodeInstruction(OpCodes.Ret) };
        private static void Init(GameObject go, bool cheated) => ItemDrop.OnCreateNew(Items.GetValue(go, _ => null), cheated);
        private static IEnumerable<CodeInstruction> SceneInputs(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var i in instructions)
            {
                if (i.operand is MethodInfo m)
                {
                    string replace = m.DeclaringType == typeof(UObject)
                        ? m.Name == "op_Implicit" ? nameof(Exists) : m.Name == "op_Equality" ? nameof(Equal) :
                          m.Name == "op_Inequality" ? nameof(Different) : m.Name == "Instantiate" ? nameof(Spawn) :
                          m.Name == "GetInstanceID" ? nameof(Id) : m.Name == "get_name" ? nameof(PrefabName) : null
                        : m.Name == "TryGetComponent" ? (m.DeclaringType == typeof(GameObject) ? nameof(Item) : nameof(View))
                        : m.DeclaringType == typeof(ItemDrop) && m.Name == "OnCreateNew" ? nameof(Init) : null;
                    if (replace != null) { i.opcode = OpCodes.Call; i.operand = AccessTools.Method(typeof(ObjectStackProbe), replace); }
                }
                yield return i;
            }
        }
        private static int Id(UObject value) => RuntimeHelpers.GetHashCode(value);
        private static string PrefabName(UObject value) => "Resource" + Items.GetValue((GameObject)value, _ => null).m_itemData.m_shared.m_name;
        private static bool NoNetworkNotification() => false;
    }
}
