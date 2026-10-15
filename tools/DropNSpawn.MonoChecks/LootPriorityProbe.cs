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
    // Executes the original game loot loops and the production Harmony hooks.
    // Only scene inputs, Unity identity/RNG and physical item creation are substituted.
    internal static class LootPriorityProbe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static Type manager, compiled;
        private static Character character;
        private static GameObject first, second;
        private static ItemDrop item;
        private static bool missingSecond;
        private static bool usePseudo;
        private static bool replaceResult;
        private static int resourceMultiplier = 1;
        private static readonly List<int> stackedAmounts = new List<int>();
        private static List<KeyValuePair<GameObject, int>> delivered;
        private static int checks;

        internal static int Run(Assembly mod)
        {
            manager = mod.GetType("DropNSpawn.CharacterDropManager", true);
            compiled = mod.GetType("DropNSpawn.CompiledCharacterDropDefinition", true);
            Type settings = mod.GetType("DropNSpawn.CharacterDropGlobalConfig", true);
            Type bound = mod.GetType("DropNSpawn.PluginBoundSettings", true);
            var previous = new Dictionary<FieldInfo, object>();
            var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "dns-unsaved-loot-" + Guid.NewGuid() + ".cfg"), false) { SaveOnConfigSet = false };
            var production = new Harmony("DropNSpawn.isolated.loot.production");
            var fixture = new Harmony("DropNSpawn.isolated.loot.inputs");
            FieldInfo world = AccessTools.Field(typeof(ZDOMan), "s_instance"), net = AccessTools.Field(typeof(ZNet), "m_instance");
            try
            {
                Bind(settings, "_characterLootSystem", "CalculateChance");
                object chanceMode = Bind(settings, "_chanceOnlyDropChanceScaling", "Vanilla");
                object chanceRate = Bind(settings, "_chanceOnlyDropChanceIncreasePerStar", 50);
                Bind(settings, "_globalTrophyLevelMultiplier", "On");
                Bind(settings, "_disableCharacterLootScalingWhenCreatureLevelControlLoaded", "On");
                object stack = Bind(settings, "_globalDropInStack", "On");
                object blacklist = Bind(settings, "_dropInStackBlacklistEntry", "");
                Bind(settings, "_onePerPlayerNearbyRange", 0f);
                object enabled = Bind(bound, "EnableCharacterOverrides", "On");
                Set(settings.GetField("_isCreatureLevelControlLoaded", All), false);
                foreach (string flag in new[] { "DropRowTrackingAvailable", "SaveStackPolicyAvailable", "LoadStackPolicyAvailable" })
                    Set(manager.GetField(flag, All), false);

                character = Bare<Character>(); first = Bare<GameObject>(); second = Bare<GameObject>();
                item = Bare<ItemDrop>();
                item.m_itemData = new ItemDrop.ItemData { m_shared = new ItemDrop.ItemData.SharedData
                    { m_itemType = ItemDrop.ItemData.ItemType.Trophy, m_maxStackSize = 50 } };
                var source = Bare<CharacterDrop>();
                AccessTools.Field(typeof(CharacterDrop), "m_character").SetValue(source, character);
                var zdoWorld = Bare<ZDOMan>();
                AccessTools.Field(typeof(ZDOMan), "m_clientChangeQueue").SetValue(zdoWorld, new HashSet<ZDOID>());
                Set(world, zdoWorld);
                var fakeNet = Bare<ZNet>(); AccessTools.Field(typeof(ZNet), "m_isServer").SetValue(fakeNet, true); Set(net, fakeNet);

                // The resolved rows are fixture inputs. BuildDrops/CloneDrops/Prepare,
                // row association, native roll loop and postfix are all production code.
                fixture.Patch(manager.GetMethod("OverrideConditionalDrops", All), prefix: new HarmonyMethod(typeof(LootPriorityProbe), nameof(NoConditionalLookup)));
                fixture.Patch(manager.GetMethod("SpawnStackedDrops", All), prefix: new HarmonyMethod(typeof(LootPriorityProbe), nameof(CaptureStack)));
                foreach (MethodInfo method in manager.GetMethods(All).Where(m => new[] {
                    "PrepareCharacterLootScalingDrops", "ApplyGlobalCharacterLootLevelMultiplier", "IsBossCharacter",
                    "IsItemDropPrefab", "IsTrophyItemPrefab", "ShouldApplyGlobalTrophyLevelMultiplier", "ShouldDropInStack",
                    "GenerateConfiguredDrops", "GetOnePerPlayerDropCount", "RollConfiguredDropAmount", "GenerateAndSaveStackPolicy"
                }.Contains(m.Name))) Isolate(method);
                Isolate(mod.GetType("DropNSpawn.CharacterDropKillerFilter", true).GetMethod("SuppressScopedGeneratedDrops", All));
                production.CreateClassProcessor(mod.GetType("DropNSpawn.CharacterDropGenerateDropListPatch", true)).Patch();
                production.CreateClassProcessor(mod.GetType("DropNSpawn.RagdollSaveDropPolicyPatch", true)).Patch();
                production.CreateClassProcessor(mod.GetType("DropNSpawn.RagdollLoadDropPolicyPatch", true)).Patch();
                Isolate(AccessTools.Method(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList)));
                fixture.Patch(AccessTools.Method(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList)),
                    postfix: new HarmonyMethod(typeof(LootPriorityProbe), nameof(ReplaceResult)) { priority = Priority.Last, after = new[] { production.Id } });
                Isolate(AccessTools.Method(typeof(Ragdoll), "SaveLootList"));
                Isolate(AccessTools.Method(typeof(Ragdoll), "SpawnLoot"));
                foreach (string flag in new[] { "DropRowTrackingAvailable", "SaveStackPolicyAvailable", "LoadStackPolicyAvailable" })
                    Check((bool)manager.GetField(flag, All).GetValue(null), "original Mono loot hook installs: " + flag);

                object failed = Row(first, .01f, 2, false, false), loose = Row(first, 1, 2, false, false),
                    stacked = Row(first, 1, 2, true, true), inherited = Row(first, 1, 2, null, null);
                IList definitions = Rows(failed, loose, stacked, inherited);
                source.m_drops = Build(definitions);
                List<CharacterDrop.Drop> original = source.m_drops;
                var result = source.GenerateDropList();
                Check(result.Select(p => p.Value).SequenceEqual(new[] { 2, 4, 4 }), "failed first duplicate does not shift trophy false/true/inherit quantities");
                Check(Policies(result).Cast<object>().Select(p => Field<bool?>(p, "DropInStack")).SequenceEqual(new bool?[] { false, true, null }),
                    "successful duplicate rows retain their own nullable stack policy");
                Check(ReferenceEquals(source.m_drops, original) && original[2].m_levelMultiplier, "native hook restores source list and flags");
                Check(Custom(definitions).Cast<object>().Select(p => (int)p.GetType().GetProperty("Amount").GetValue(p)).SequenceEqual(new[] { 2, 4, 4 }),
                    "custom path agrees on explicit trophy false and inherited amount policy");
                Set(settings.GetField("_isCreatureLevelControlLoaded", All), true);
                Check(source.GenerateDropList().Select(p => p.Value).SequenceEqual(new[] { 2, 8, 8 }), "CLLC handoff preserves native star amounts");
                Check(Custom(definitions).Cast<object>().Select(p => (int)p.GetType().GetProperty("Amount").GetValue(p)).SequenceEqual(new[] { 2, 8, 8 }),
                    "CLLC handoff also preserves custom star amounts");
                Set(settings.GetField("_isCreatureLevelControlLoaded", All), false);
                object resource = Row(first, 1, 2, true, null);
                compiled.GetProperty("DontScale").SetValue(resource, false);
                resourceMultiplier = 2; source.m_drops = Build(Rows(resource));
                Check(source.GenerateDropList().Single().Value == 8, "base resource-scaled amount is not mistaken for vanilla star multiplication");
                resourceMultiplier = 1;
                object chanceRow = Row(first, .55f, 2, true, null);
                compiled.GetProperty("LevelMultiplierNotAmount").SetValue(chanceRow, true);
                IList chanceDefinitions = Rows(chanceRow);
                source.m_drops = Build(chanceDefinitions);
                Value(chanceMode, "Linear");
                foreach (int percent in new[] { 0, 25, 50, 100 })
                {
                    Value(chanceRate, percent);
                    var native = source.GenerateDropList();
                    IList custom = Custom(chanceDefinitions);
                    bool succeeds = percent >= 50; // Fixed fixture roll .9, base .55, two stars.
                    Check(native.Count == (succeeds ? 1 : 0) && custom.Count == native.Count,
                        "native/custom chance-only rolls honor live increase " + percent);
                    if (succeeds) Check(native[0].Value == 2 && (int)custom[0].GetType().GetProperty("Amount").GetValue(custom[0]) == 2,
                        "chance-only increase does not multiply quantity");
                }
                Value(chanceRate, 0); Value(chanceMode, "Vanilla");
                Check(source.GenerateDropList().Single().Value == 2 && Custom(chanceDefinitions).Count == 1,
                    "Vanilla mode ignores zero Linear increase and retains exponential chance");
                Value(chanceMode, "Linear"); Set(settings.GetField("_isCreatureLevelControlLoaded", All), true);
                Check(source.GenerateDropList().Count == 1 && Custom(chanceDefinitions).Count == 1,
                    "CLLC handoff ignores zero Linear increase on native and custom paths");
                Set(settings.GetField("_isCreatureLevelControlLoaded", All), false);
                Value(chanceMode, "Vanilla"); Value(chanceRate, 50);
                object capped = Row(first, 1, 2, true, null, 3), removed = Row(first, 1, 2, true, null, 0), perPlayer = Row(first, 1, 2, true, false, 2, true);
                source.m_drops = Build(Rows(capped, removed, perPlayer));
                Check(source.GenerateDropList().Select(p => p.Value).SequenceEqual(new[] { 3, 2 }), "native YAML caps apply after scaling and onePerPlayer, including limit zero");

                var counters = CharacterDrop.s_pseudoCounter;
                try
                {
                    usePseudo = true;
                    CharacterDrop.s_pseudoCounter = new Dictionary<int, int>();
                    int pseudoKey = "LootFixtureA".GetStableHashCode() + 100;
                    CharacterDrop.s_pseudoCounter[pseudoKey] = 2;
                    source.m_drops = Build(Rows(Row(first, .1f, 2, false, false)));
                    Check(source.GenerateDropList().Count == 0 && CharacterDrop.s_pseudoCounter[pseudoKey] == 1,
                        "native failed pseudo roll still advances the original counter");
                    Check(source.GenerateDropList().Single().Value == 2 && CharacterDrop.s_pseudoCounter[pseudoKey] == 0,
                        "native successful pseudo roll keeps counter reset and explicit loose policy");
                }
                finally { usePseudo = false; CharacterDrop.s_pseudoCounter = counters; }

                Check(!Stack(false) && Stack(true) && Stack(null), "global On respects explicit stack false");
                Value(stack, "Off");
                Check(!Stack(false) && Stack(true) && !Stack(null), "global Off respects explicit stack true");
                Value(blacklist, "LootFixtureA"); Check(!Stack(true), "blacklist denies explicit stack true"); Value(blacklist, "");
                Value(stack, "On");
                source.m_drops = Build(Rows(loose, stacked, inherited));
                result = source.GenerateDropList();
                stackedAmounts.Clear();
                var looseResult = ApplyStacks(result);
                Check(looseResult.Select(p => p.Value).SequenceEqual(new[] { 2 }) && stackedAmounts.SequenceEqual(new[] { 4, 4 }),
                    "stack application partitions successful rows exactly once with global On");
                Value(stack, "Off"); result = source.GenerateDropList(); stackedAmounts.Clear();
                looseResult = ApplyStacks(result);
                Check(looseResult.Select(p => p.Value).SequenceEqual(new[] { 2, 4 }) && stackedAmounts.SequenceEqual(new[] { 4 }),
                    "stack application uses live global Off while explicit true remains stacked");
                Value(stack, "On");
                manager.GetField("SaveStackPolicyAvailable", All).SetValue(null, false);
                result = source.GenerateDropList(); stackedAmounts.Clear();
                Check(ReferenceEquals(ApplyStacks(result), result) && stackedAmounts.Count == 0, "missing save hook disables native stacking without dropping items");
                manager.GetField("SaveStackPolicyAvailable", All).SetValue(null, true);
                // An already-recorded list edited by another postfix is not guessed by prefab.
                source.m_drops = Build(Rows(loose, stacked)); result = source.GenerateDropList();
                result[0] = new KeyValuePair<GameObject, int>(first, 17);
                object[] dropArgs = { result, Vector3.zero, 0f, false };
                manager.GetMethod("ApplyGlobalDropInStack", All).Invoke(null, dropArgs);
                Check(ReferenceEquals(dropArgs[0], result) && result[0].Value == 17, "foreign changed result bypasses stack policy rather than guessing");
                replaceResult = true; result = source.GenerateDropList(); replaceResult = false;
                stackedAmounts.Clear();
                Check(ReferenceEquals(ApplyStacks(result), result) && stackedAmounts.Count == 0 &&
                    Policies(result).Cast<object>().All(p => Field<bool?>(p, "DropInStack") == false),
                    "late foreign postfix replacement remains loose after the production finalizer");

                // Real SaveLootList -> ZDO values -> SpawnLoot, including a missing middle prefab.
                var ragdoll = Bare<Ragdoll>(); var view = Bare<ZNetView>(); var zdo = Bare<ZDO>();
                zdo.m_uid = new ZDOID(9812346L, 95221);
                AccessTools.Property(typeof(ZDO), "Valid").SetValue(zdo, true);
                AccessTools.Property(typeof(ZDO), "Owner").SetValue(zdo, true);
                AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
                AccessTools.Field(typeof(Ragdoll), "m_nview").SetValue(ragdoll, view);
                source.m_drops = Build(Rows(loose, Row(second, 1, 2, false, true), stacked, inherited));
                MethodInfo save = AccessTools.Method(typeof(Ragdoll), "SaveLootList"), spawn = AccessTools.Method(typeof(Ragdoll), "SpawnLoot");
                save.Invoke(ragdoll, new object[] { source });
                int key = (int)manager.GetField("SavedStackPolicyKey", All).GetValue(null);
                byte[] saved = zdo.GetByteArray(key);
                Check(zdo.GetInt(ZDOVars.s_drops) == 4 && saved.Length == 44, "ragdoll stores native amounts and bounded per-row policy metadata");
                source.m_drops.Clear(); // No live source rows are needed to reconstruct policy.
                missingSecond = true;
                spawn.Invoke(ragdoll, new object[] { Vector3.zero });
                Check(delivered.Select(p => p.Value).SequenceEqual(new[] { 2, 4, 4 }) &&
                    Policies(delivered).Cast<object>().Select(p => Field<bool?>(p, "DropInStack")).SequenceEqual(new bool?[] { false, true, null }),
                    "ragdoll restores correct policies using saved index after a missing middle prefab");
                Check(zdo.GetInt(ZDOVars.s_drops) == 4, "row hooks leave loot consumption to existing owner/instant-drop caller");
                Value(stack, "Off"); stackedAmounts.Clear();
                Check(ApplyStacks(delivered).Select(p => p.Value).SequenceEqual(new[] { 2, 4 }) && stackedAmounts.SequenceEqual(new[] { 4 }),
                    "saved YAML policy survives source removal while global default is read at delivery");
                Value(stack, "On");
                zdo.Set(key, Array.Empty<byte>()); spawn.Invoke(ragdoll, new object[] { Vector3.zero });
                Check(Policies(delivered).Cast<object>().All(p => !Field<bool?>(p, "DropInStack").HasValue), "old ragdoll without metadata inherits global policy");
                foreach (byte[] invalid in new[] { new byte[] { 1 }, saved.Take(saved.Length - 1).ToArray(), new byte[saved.Length] })
                {
                    zdo.Set(key, invalid); spawn.Invoke(ragdoll, new object[] { Vector3.zero });
                    Check(delivered.Count == 3 && Policies(delivered).Cast<object>().All(p => !Field<bool?>(p, "DropInStack").HasValue),
                        "malformed metadata preserves loot and falls back to inheritance");
                }
                zdo.Set(key, saved);
                zdo.Set("drop_amount0", 9); spawn.Invoke(ragdoll, new object[] { Vector3.zero });
                Check(delivered[0].Value == 9 && !Field<bool?>(Policies(delivered)[0], "DropInStack").HasValue &&
                    Field<bool?>(Policies(delivered)[1], "DropInStack") == true, "changed saved amount invalidates only its policy");
                source.m_drops = Build(Rows(stacked));
                AccessTools.Property(typeof(ZDO), "Owner").SetValue(zdo, false);
                manager.GetMethod("GenerateAndSaveStackPolicy", All).Invoke(null, new object[] { source, ragdoll });
                Check(ReferenceEquals(zdo.GetByteArray(key), saved), "non-owner wrapper never writes policy metadata");

                Value(enabled, "Off"); source.m_drops = Build(Rows(loose));
                Check(source.GenerateDropList()[0].Value == 4 && !Field<bool?>(Policies(source.GenerateDropList())[0], "DropInStack").HasValue,
                    "Character domain Off ignores YAML row policy while global trophy scaling remains active");
                Check(!File.Exists(config.ConfigFilePath), "loot probe never writes user configuration");
                return checks;
            }
            finally
            {
                production.UnpatchSelf(); fixture.UnpatchSelf();
                foreach (var pair in previous) pair.Key.SetValue(null, pair.Value);
                character = null; first = second = null; item = null; delivered = null; missingSecond = replaceResult = false;
                resourceMultiplier = 1;
            }

            void Set(FieldInfo field, object value) { if (!previous.ContainsKey(field)) previous.Add(field, field.GetValue(null)); field.SetValue(null, value); }
            object Bind(Type holder, string name, object value)
            {
                FieldInfo field = holder.GetField(name, All) ?? holder.GetField("<" + name + ">k__BackingField", All);
                Type type = field.FieldType.GetGenericArguments()[0];
                MethodInfo bind = typeof(ConfigFile).GetMethods().Single(m => m.Name == "Bind" && m.IsGenericMethodDefinition &&
                    m.GetParameters().Length == 4 && m.GetParameters()[0].ParameterType == typeof(string) && m.GetParameters()[3].ParameterType == typeof(string));
                object entry = bind.MakeGenericMethod(type).Invoke(config, new[] { "fixture", name, type.IsEnum ? Enum.Parse(type, (string)value) : value, "" });
                Set(field, entry); return entry;
            }
            void Isolate(MethodInfo method) => fixture.Patch(method, transpiler: new HarmonyMethod(typeof(LootPriorityProbe), nameof(SceneInputs)) { priority = Priority.Last });
        }

        private static T Bare<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
        private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, All).GetValue(value);
        private static void Check(bool success, string description) { if (!success) throw new InvalidOperationException(description); checks++; }
        private static void Value(object entry, object value) { Type type = entry.GetType().GetGenericArguments()[0]; entry.GetType().GetProperty("Value").SetValue(entry, type.IsEnum ? Enum.Parse(type, (string)value) : value); }
        private static IList Rows(params object[] values) { var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(compiled)); foreach (object value in values) list.Add(value); return list; }
        private static object Row(GameObject prefab, float chance, int amount, bool? level, bool? stack, int? limit = null, bool perPlayer = false)
        {
            object row = Activator.CreateInstance(compiled);
            void Set(string key, object value) => compiled.GetProperty(key).SetValue(row, value);
            Set("Prefab", prefab); Set("Chance", chance); Set("AmountMin", amount); Set("AmountMax", amount);
            Set("LevelMultiplier", level ?? true); Set("LevelMultiplierOverride", level); Set("DropInStack", stack);
            Set("AmountLimit", limit); Set("OnePerPlayer", perPlayer); Set("DontScale", true);
            return row;
        }
        private static List<CharacterDrop.Drop> Build(IList rows) => (List<CharacterDrop.Drop>)manager.GetMethod("BuildDrops", All).Invoke(null, new object[] { rows });
        private static IList Policies(List<KeyValuePair<GameObject, int>> rows) => (IList)manager.GetMethod("GetMatchingGeneratedPolicies", All).Invoke(null, new object[] { rows });
        private static IList Custom(IList rows) => (IList)manager.GetMethod("GenerateConfiguredDrops", All).Invoke(null, new object[] { character, rows });
        private static bool Stack(bool? policy) => (bool)manager.GetMethod("ShouldDropInStack", All).Invoke(null, new object[] { first, 2, policy });
        private static List<KeyValuePair<GameObject, int>> ApplyStacks(List<KeyValuePair<GameObject, int>> drops)
        {
            object[] args = { drops, Vector3.zero, 0f, false };
            manager.GetMethod("ApplyGlobalDropInStack", All).Invoke(null, args);
            return (List<KeyValuePair<GameObject, int>>)args[0];
        }
        private static bool CaptureStack(int amount) { stackedAmounts.Add(amount); return false; }
        private static void ReplaceResult(ref List<KeyValuePair<GameObject, int>> __result)
        {
            if (replaceResult) __result = new List<KeyValuePair<GameObject, int>>(__result);
        }
        private static bool NoConditionalLookup(ref List<CharacterDrop.Drop> __result) { __result = null; return false; }

        private static IEnumerable<CodeInstruction> SceneInputs(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.operand is MethodInfo method)
                {
                    string target = null;
                    if (method.DeclaringType == typeof(UnityEngine.Object))
                        target = method.Name == "op_Equality" ? nameof(Equal) : method.Name == "op_Inequality" ? nameof(NotEqual)
                            : method.Name == "op_Implicit" ? nameof(Exists) : method.Name == "get_name" ? nameof(Name) : null;
                    else if (method.DeclaringType == typeof(Component) && method.Name == "GetComponent" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(Character)) target = nameof(GetCharacter);
                    else if (method.DeclaringType == typeof(GameObject) && method.Name == "TryGetComponent" && method.IsGenericMethod && method.GetGenericArguments()[0] == typeof(ItemDrop)) target = nameof(GetItem);
                    else if (method.DeclaringType == typeof(Character)) target = method.Name == "GetLevel" ? nameof(Level) : method.Name == "IsBoss" ? nameof(Boss) : null;
                    else if (method.DeclaringType == typeof(UnityEngine.Random)) target = method.Name == "get_value" ? nameof(Chance) : method.Name == "Range" && method.ReturnType == typeof(int) ? nameof(Amount) : null;
                    else if (method.DeclaringType == typeof(ZoneSystem) && method.Name == "GetGlobalKey" && method.GetParameters()[0].ParameterType == typeof(GlobalKeys)) target = nameof(GlobalKey);
                    else if (method.DeclaringType == typeof(Game) && method.Name == "ScaleDrops") target = nameof(Scale);
                    else if (method.DeclaringType == typeof(ZNet) && method.Name == "GetNrOfPlayers") target = nameof(Players);
                    else if (method.DeclaringType == typeof(ZNetScene)) target = method.Name == "GetPrefabHash" ? nameof(Hash) : method.Name == "GetPrefab" && method.GetParameters()[0].ParameterType == typeof(int) ? nameof(Prefab) : null;
                    else if (method.DeclaringType == typeof(CharacterDrop) && method.Name == "DropItems") target = nameof(Deliver);
                    else if (method.DeclaringType == typeof(ZLog) && method.Name == "LogWarning") target = nameof(IgnoreWarning);
                    if (target != null) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(LootPriorityProbe), target); }
                }
                yield return instruction;
            }
        }
        private static bool Equal(UnityEngine.Object a, UnityEngine.Object b) => ReferenceEquals(a, b);
        private static bool NotEqual(UnityEngine.Object a, UnityEngine.Object b) => !ReferenceEquals(a, b);
        private static bool Exists(UnityEngine.Object value) => !ReferenceEquals(value, null);
        private static string Name(UnityEngine.Object value) => ReferenceEquals(value, second) ? "LootFixtureB" : "LootFixtureA";
        private static Character GetCharacter(Component value) => character;
        private static bool GetItem(GameObject value, out ItemDrop result) { result = item; return !ReferenceEquals(value, null); }
        private static int Level(Character value) => 3;
        private static bool Boss(Character value) => false;
        private static float Chance() => .9f;
        private static int Amount(int min, int max) => min;
        private static bool GlobalKey(ZoneSystem value, GlobalKeys key) => !usePseudo;
        private static int Scale(Game game, GameObject prefab, int min, int max) => min * resourceMultiplier;
        private static int Players(ZNet value) => 3;
        private static int Hash(ZNetScene scene, GameObject prefab) => ReferenceEquals(prefab, second) ? 222 : 111;
        private static GameObject Prefab(ZNetScene scene, int hash) => hash == 222 ? missingSecond ? null : second : first;
        private static void Deliver(List<KeyValuePair<GameObject, int>> drops, Vector3 center, float area, bool cheated) => delivered = drops;
        private static void IgnoreWarning(object value) { }
    }
}
