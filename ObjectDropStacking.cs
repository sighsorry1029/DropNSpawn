using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace DropNSpawn;

// Only the final delivery is intercepted. Every drop-table roll, damage conversion, harvest
// bonus and original owner/picked check still runs. No ground scan and no cross-event state.
internal static class ObjectDropStacking
{
    internal static bool ResourceEnabled => PluginBoundSettings.StackResourceDrops?.Value == DropNSpawnPlugin.Toggle.On &&
                                           PluginSettingsFacade.IsObjectDomainEnabled();

    internal sealed class Batch
    {
        private readonly Dictionary<int, (ItemDrop Source, ItemDrop Live)> _open = new();

        // Return the amount still to deliver. SetStack enforces owner and persists to the ZDO;
        // never consume an item just because we requested a write.
        internal int Absorb(GameObject prefab, int amount, bool? cheated)
        {
            if (amount <= 0 || !_open.TryGetValue(prefab.GetInstanceID(), out var slot) ||
                PluginBoundSettings.IsObjectStackItemBlacklisted(prefab) || !slot.Source || !slot.Live ||
                !slot.Live.TryGetComponent(out ZNetView view) || !view.IsValid() || !view.IsOwner())
                return amount;
            var source = slot.Source.m_itemData;
            var live = slot.Live.m_itemData;
            if (!Ordinary(source) || !Ordinary(live)) return amount;
            int max = source.m_shared.m_maxStackSize;
            if (source.m_stack != 1 || live.m_shared.m_maxStackSize != max ||
                live.m_shared.m_name != source.m_shared.m_name || (live.m_dropPrefab && live.m_dropPrefab != prefab) ||
                live.m_quality != source.m_quality || live.m_variant != source.m_variant ||
                live.m_durability != source.m_durability || live.m_stack <= 0 || live.m_stack >= max ||
                live.m_cheated != (cheated ?? source.m_cheated) ||
                live.m_worldLevel != (cheated.HasValue ? (byte)Game.m_worldLevel : source.m_worldLevel))
                return amount;
            // Pickable.Drop calls SetStack once per result (even if resource scaling exceeds
            // maxStackSize). Do not create units vanilla would have clamped away.
            int originalYield = Math.Min(amount, max);
            int before = live.m_stack;
            int take = Math.Min(originalYield, max - before);
            slot.Live.SetStack(before + take);
            int accepted = Math.Min(take, Math.Max(0, live.m_stack - before));
            return accepted == 0 ? amount : originalYield - accepted;
        }

        private static bool Ordinary(ItemDrop.ItemData? item) =>
            item?.m_shared != null && item.m_shared.m_maxStackSize is > 1 and <= ushort.MaxValue && item.m_shared.m_autoStack &&
            !item.m_shared.m_useDurability && !item.m_pickedUp && !item.m_equipped &&
            item.m_crafterID == 0 && string.IsNullOrEmpty(item.m_crafterName) && !(item.m_customData?.Count > 0);

        internal static void Remember(ref Batch? batch, GameObject prefab, ItemDrop live, int expectedAmount)
        {
            // A prefab instance can carry a non-unit initial quantity or per-instance metadata.
            // Leave those entirely to the producer, as well as full and non-stackable drops.
            if (!live || !Ordinary(live.m_itemData) || live.m_itemData.m_stack != expectedAmount ||
                expectedAmount <= 0 || expectedAmount >= live.m_itemData.m_shared.m_maxStackSize ||
                PluginBoundSettings.IsObjectStackItemBlacklisted(prefab) ||
                !prefab.TryGetComponent(out ItemDrop source) || !Ordinary(source.m_itemData) || source.m_itemData.m_stack != 1)
                return;
            (batch ??= new Batch())._open[prefab.GetInstanceID()] = (source, live);
        }
    }

    private static void Emit(GameObject prefab, Vector3 position, Quaternion rotation, bool? cheated, ref Batch? batch)
    {
        if (batch?.Absorb(prefab, 1, cheated) == 0) return;
        GameObject spawned = UObject.Instantiate(prefab, position, rotation);
        if (cheated.HasValue) ItemDrop.OnCreateNew(spawned, cheated.Value);
        if (spawned.TryGetComponent(out ItemDrop item)) Batch.Remember(ref batch, prefab, item, 1);
    }

    private static void EmitInitialized(GameObject prefab, Vector3 position, Quaternion rotation, bool cheated, ref Batch? batch) =>
        Emit(prefab, position, rotation, cheated, ref batch);

    // TreeBase never calls OnCreateNew. Do not introduce a different world level/cheat flag.
    private static void EmitRaw(GameObject prefab, Vector3 position, Quaternion rotation, ref Batch? batch) =>
        Emit(prefab, position, rotation, null, ref batch);

    internal static IEnumerable<CodeInstruction> PatchResource(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator)
    {
        var code = instructions.ToList();
        var roll = AccessTools.Method(typeof(DropTable), nameof(DropTable.GetDropList), Type.EmptyTypes);
        int[] rolls = Sites(code, i => i.Calls(roll));
        bool raw = original.DeclaringType == typeof(TreeBase);
        var init = AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.OnCreateNew), new[] { typeof(GameObject), typeof(bool) });
        if (rolls.Length != 1) return Unsupported(code, original);
        int[] emit = Sites(code, IsInstantiate).Where(i => i > rolls[0] && (raw
            ? i + 1 < code.Count && code[i + 1].opcode == OpCodes.Pop
            : i + 2 < code.Count && (code[i + 1].IsLdloc() || code[i + 1].opcode == OpCodes.Ldarg_2) && code[i + 2].Calls(init))).ToArray();
        if (emit.Length != 1) return Unsupported(code, original);
        int at = emit[0], last = at + (raw ? 1 : 2);
        if (last + 1 >= code.Count || code.Skip(at).Take(last - at + 1).Any(i => i.labels.Count != 0 || i.blocks.Count != 0))
            return Unsupported(code, original);
        var batch = generator.DeclareLocal(typeof(Batch)); // new per invocation, including each MineRock5 section
        Label vanilla = generator.DefineLabel(), done = generator.DefineLabel();
        code[at] = new CodeInstruction(code[at]).WithLabels(vanilla);
        code[last + 1] = new CodeInstruction(code[last + 1]).WithLabels(done);
        var branch = new List<CodeInstruction> { new(OpCodes.Call, AccessTools.PropertyGetter(typeof(ObjectDropStacking), nameof(ResourceEnabled))),
            new(OpCodes.Brfalse, vanilla) };
        if (!raw) branch.Add(new CodeInstruction(code[at + 1])); // original cheated flag
        branch.Add(new CodeInstruction(OpCodes.Ldloca, batch));
        branch.Add(CodeInstruction.Call(typeof(ObjectDropStacking), raw ? nameof(EmitRaw) : nameof(EmitInitialized)));
        branch.Add(new CodeInstruction(OpCodes.Br, done));
        code.InsertRange(at, branch);
        return code;
    }

    private static bool IsInstantiate(CodeInstruction i) => i.opcode == OpCodes.Call &&
        i.operand is MethodInfo m && m.DeclaringType == typeof(UObject) && m.Name == nameof(UObject.Instantiate) &&
        m.IsGenericMethod && m.ReturnType == typeof(GameObject) &&
        m.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(GameObject), typeof(Vector3), typeof(Quaternion) });

    private static int[] Sites(List<CodeInstruction> code, Func<CodeInstruction, bool> match) =>
        Enumerable.Range(0, code.Count).Where(i => match(code[i])).ToArray();

    private static List<CodeInstruction> Unsupported(List<CodeInstruction> code, MethodBase original)
    {
        DropNSpawnPlugin.DropNSpawnLogger?.LogWarning($"Object drop stacking unavailable for {original.DeclaringType?.Name}.{original.Name}; leaving the original delivery code unchanged.");
        return code;
    }

    // Pickable.Drop is called for both primary and extra yields. Scope its batch to the
    // synchronous owner RPC; nested calls and exceptions restore the outer scope.
    internal sealed class Harvest
    {
        internal readonly Pickable Plant;
        internal readonly Harvest? Previous;
        internal Batch? Batch;
        internal Harvest(Pickable plant, Harvest? previous) { Plant = plant; Previous = previous; }
    }

    [ThreadStatic] private static Harvest? _harvest;
    internal static Harvest? BeginHarvest(Pickable plant) =>
        PluginBoundSettings.StackHarvestDrops?.Value == DropNSpawnPlugin.Toggle.On && PluginSettingsFacade.IsObjectDomainEnabled()
            ? _harvest = new Harvest(plant, _harvest) : null;
    internal static void EndHarvest(Harvest? state)
    {
        if (state != null && ReferenceEquals(state, _harvest)) _harvest = state.Previous;
    }

    private static bool TryAbsorbHarvest(Pickable plant, GameObject prefab, ref int amount)
    {
        if (_harvest?.Batch == null || !ReferenceEquals(_harvest.Plant, plant) || amount <= 0) return false;
        amount = _harvest.Batch.Absorb(prefab, amount, false);
        return amount == 0;
    }

    private static void RememberHarvest(ItemDrop item, Pickable plant, GameObject prefab, int amount)
    {
        if (_harvest != null && ReferenceEquals(_harvest.Plant, plant)) Batch.Remember(ref _harvest.Batch, prefab, item, amount);
    }

    internal static IEnumerable<CodeInstruction> PatchHarvest(IEnumerable<CodeInstruction> instructions, MethodBase original, ILGenerator generator)
    {
        var code = instructions.ToList();
        var init = AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.OnCreateNew), new[] { typeof(ItemDrop), typeof(bool) });
        int[] sites = Sites(code, i => i.Calls(init));
        var setStack = AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.SetStack));
        int[] set = Sites(code, i => i.Calls(setStack));
        int[] spawn = Sites(code, IsInstantiate);
        if (spawn.Length != 1 || set.Length != 1 || sites.Length != 1 ||
            !(spawn[0] < set[0] && set[0] < sites[0] - 1) || !code[sites[0] - 1].LoadsConstant(0) ||
            code.Skip(sites[0] - 1).Take(2).Any(i => i.labels.Count != 0 || i.blocks.Count != 0) ||
            code[0].blocks.Count != 0) return Unsupported(code, original);
        // Keep the original Drop method (including velocity, offsets and callbacks) for any
        // quantity not absorbed. Observe the actual component only after vanilla initialized it.
        code.InsertRange(sites[0] + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarg_3), CodeInstruction.Call(typeof(ObjectDropStacking), nameof(RememberHarvest)) });
        code.Insert(sites[0] - 1, new CodeInstruction(OpCodes.Dup));
        var runOriginal = generator.DefineLabel();
        code[0] = new CodeInstruction(code[0]).WithLabels(runOriginal);
        code.InsertRange(0, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarga_S, (byte)3), CodeInstruction.Call(typeof(ObjectDropStacking), nameof(TryAbsorbHarvest)),
            new CodeInstruction(OpCodes.Brfalse, runOriginal), new CodeInstruction(OpCodes.Ret) });
        return code;
    }
}
