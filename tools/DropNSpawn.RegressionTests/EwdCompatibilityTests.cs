using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

internal static partial class Program
{
    private static void CheckEwdCompatibilityContracts(ModContract mod)
    {
        Assembly ewd = mod.LoadGameAssembly("ExpandWorldData");
        Type compat = mod.Type("ExpandWorldDataCompatibility");
        var plan = (IList)Invoke(compat, null, "GetPatchPlan", ewd)!;
        if (ewd.GetType("ExpandWorld.Spawn.Patcher") == null)
        {
            Check(plan.Count == 0, "pre-integration EWD needs no ownership patches");
            return;
        }
        Check(plan.Count == 18, "integrated EWD ownership/metadata contract resolves completely");
        int transpilers = 0;
        foreach (object entry in plan)
        {
            MethodInfo target = (MethodInfo)entry.GetType().GetField("Item1")!.GetValue(entry)!;
            var kind = (HarmonyPatchType)entry.GetType().GetField("Item2")!.GetValue(entry)!;
            string callback = (string)entry.GetType().GetField("Item3")!.GetValue(entry)!;
            if (kind == HarmonyPatchType.Transpiler)
            {
                var original = PatchProcessor.GetOriginalInstructions(target);
                int oldGates = original.Count(IsFeatureGate);
                var rewritten = ((IEnumerable<CodeInstruction>)Invoke(compat, null, callback, original)!).ToList();
                Check(oldGates > 0 && !rewritten.Any(IsFeatureGate), "EWD feature calls suppressed: " + target.DeclaringType!.FullName);
                Check(rewritten.Count == original.Count, "EWD rewrite preserves instruction/branch layout: " + target.Name);
                transpilers++;
            }
            else if (kind == HarmonyPatchType.Prefix)
                Check(!(bool)Invoke(compat, null, callback)!, "EWD owned entrypoint skipped: " + target.DeclaringType!.FullName + "." + target.Name);
        }
        Check(transpilers == 4, "EWD patchers, event API and drop conversion are covered");

        Type loader = ewd.GetType("ExpandWorld.Spawn.Loader", true)!;
        Type dtoType = ewd.GetType("ExpandWorld.Spawn.Data", true)!;
        var data = (IDictionary)loader.GetField("Data")!.GetValue(null)!;
        var objects = (IDictionary)loader.GetField("Objects")!.GetValue(null)!;
        compat.GetField("_data", All)!.SetValue(null, data);
        compat.GetField("_objects", All)!.SetValue(null, objects);
        FieldInfo[] extensions = new[] { "data", "fields", "objects", "faction" }.Select(n => dtoType.GetField(n)!).ToArray();
        compat.GetField("_sourceFields", All)!.SetValue(null, extensions);
        Type spawnType = mod.LoadGameAssembly("assembly_valheim").GetType("SpawnSystem+SpawnData", true)!;
        Type payloadSupport = mod.Type("SpawnSystemCustomDataSupport");
        object spawn = Activator.CreateInstance(spawnType)!;
        object source = Activator.CreateInstance(dtoType)!;
        object exported = Activator.CreateInstance(dtoType)!;
        object customData = Activator.CreateInstance(ewd.GetType("Data.DataEntry", true)!)!;
        var customObjects = (IList)Activator.CreateInstance(objects.GetType().GetGenericArguments()[1])!;
        customObjects.Add(RuntimeHelpers.GetUninitializedObject(ewd.GetType("ExpandWorldData.BlueprintObject", true)!));
        data.Add(spawn, customData);
        objects.Add(spawn, customObjects);
        dtoType.GetField("data")!.SetValue(source, "AltBiomeCustomData");
        dtoType.GetField("faction")!.SetValue(source, "AnimalsVeg");
        dtoType.GetField("fields")!.SetValue(source, new Dictionary<string, string> { ["health"] = "42" });
        dtoType.GetField("objects")!.SetValue(source, new[] { "Wood,0,0,0,1" });
        dtoType.GetField("drops")!.SetValue(source, "DoNotRestoreDrops");
        dtoType.GetField("spawnInterval")!.SetValue(exported, 12.5f);
        Invoke(compat, null, "CaptureSpawnData", source, spawn);
        Check(!data.Contains(spawn) && !objects.Contains(spawn), "AltBiome metadata leaves EWD strong-key dictionaries");
        Check((bool)Invoke(payloadSupport, null, "HasPreparedPayload", spawn)!, "AltBiome receives existing DNS runtime payload");
        object weakPayloads = payloadSupport.GetField("PayloadsBySpawnData", All)!.GetValue(null)!;
        Check(TryReadWeakValue(weakPayloads, spawn, out object? payload) && ReferenceEquals(Property(payload!, "CustomData"), customData) &&
            ReferenceEquals(Property(payload!, "CustomObjects"), customObjects), "EWD prepared data and objects are transferred without reinterpretation");
        Invoke(compat, null, "RestoreSpawnData", spawn, exported);
        foreach (FieldInfo field in extensions)
            Check(Equals(field.GetValue(exported), field.GetValue(source)), "AltBiome sync retains " + field.Name);
        Check((float)dtoType.GetField("spawnInterval")!.GetValue(exported)! == 12.5f, "AltBiome export retains current native interval");
        Check((string)dtoType.GetField("drops")!.GetValue(exported)! == "", "AltBiome export does not enable EWD loot");
        object nativeSpawn = Activator.CreateInstance(spawnType)!;
        object nativeExport = Activator.CreateInstance(dtoType)!;
        Invoke(compat, null, "RestoreSpawnData", nativeSpawn, nativeExport);
        Check(dtoType.GetField("data")!.GetValue(nativeExport) == null, "native AltBiome rows remain unchanged");
        WeakReference retired = CaptureRetiredAltBiome(compat, spawnType, dtoType);
        for (int i = 0; i < 5 && retired.IsAlive; i++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Check(!retired.IsAlive, "AltBiome reload metadata does not retain discarded rows");
        Check((bool)Invoke(payloadSupport, null, "HasPreparedPayload", spawn)!, "live old-heightmap rows survive other row retirement");
        GC.KeepAlive(spawn);
        Console.WriteLine("EWD compatibility input: " + ewd.Location);

        static bool IsFeatureGate(CodeInstruction instruction) => instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
            method.DeclaringType?.FullName == "ExpandWorldData.Configuration" && method.Name is "get_DataSpawns" or "get_DataEvents" or "get_DataDrops";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureRetiredAltBiome(Type compat, Type spawnType, Type dtoType)
    {
        object retired = Activator.CreateInstance(spawnType)!;
        Invoke(compat, null, "CaptureSpawnData", Activator.CreateInstance(dtoType)!, retired);
        return new WeakReference(retired);
    }
}
