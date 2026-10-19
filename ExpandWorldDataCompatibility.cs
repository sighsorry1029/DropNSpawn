extern alias ewd;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using BepInEx.Bootstrap;
using UnityEngine;
using EwdData = ewd::Data;

namespace DropNSpawn;

// EWD owns world/AltBiome data; DNS owns normal spawn tables, raids and loot.
// Keep this boundary independent of DNS's override toggles: Off restores DNS's
// baseline, it does not hand the system to EWD. No user configuration is rewritten.
internal static class ExpandWorldDataCompatibility
{
    private static readonly ConditionalWeakTable<SpawnSystem.SpawnData, object> SourceData = new();
    private static FieldInfo? _data;
    private static FieldInfo? _objects;
    private static FieldInfo? _requirements;
    private static FieldInfo? _requiredKeys;
    private static Action<SpawnSystem.SpawnData>? _consumeAltBiomeKeys;
    private static FieldInfo[] _sourceFields = Array.Empty<FieldInfo>();
    private static Assembly? _assembly;
    // Opaque returns keep EWD types out of the standalone class initialization path.
    private static Func<string, object>? _stringConstant;
    private static Func<int, object>? _intConstant;
    private static Func<string, Vector3, object?, float, object>? _createObject;
    private static Func<object, float>? _objectChance;
    private static Action<object, Vector3>? _spawnObject;

    internal static bool IsAvailable => _assembly != null;
    internal static Type? FindType(string name) => _assembly == null ? null : FindType(_assembly, name);

    private static Type? FindType(Assembly assembly, string name)
    {
        Type? exact = assembly.GetType(name);
        if (exact != null) return exact;
        // Known semantic moves, not fuzzy simple-name matching or VERSION gates.
        string? moved = name switch
        {
            "ExpandWorldData.BlueprintObject" => "ExpandWorldData.Data.BlueprintObject",
            "ExpandWorldData.Spawn" => "ExpandWorldData.Create",
            "ExpandWorldData.BiomeManager" => "ExpandWorldData.Biome.BiomeManager",
            "ExpandWorldData.DataManager" => "ExpandWorldData.Data.DataManager",
            "ExpandWorldData.CommandManager" => "ExpandWorldData.Data.CommandManager",
            "ExpandWorldData.LocationSpawning" => "ExpandWorldData.Locations.LocationSpawning",
            _ when name.StartsWith("ExpandWorld.Spawn.", StringComparison.Ordinal) => name.Replace("ExpandWorld.Spawn.", "ExpandWorldData.Spawn."),
            _ when name.StartsWith("ExpandWorld.Event.", StringComparison.Ordinal) => name.Replace("ExpandWorld.Event.", "ExpandWorldData.Event."),
            _ when name.StartsWith("ExpandWorld.Drops.", StringComparison.Ordinal) => name.Replace("ExpandWorld.Drops.", "ExpandWorldData.Drop."),
            _ => null
        };
        return moved == null ? null : assembly.GetType(moved);
    }

    internal static void DetectDependency()
    {
        // A DLL on disk is not sufficient: BepInEx must have loaded the optional plugin.
        Assembly? assembly = Chainloader.PluginInfos.TryGetValue("expand_world_data", out var plugin)
            ? plugin.Instance.GetType().Assembly : null;
        try { ConfigureDependency(assembly); }
        catch (Exception error) when (IsDependencyFailure(error))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogError($"DNS initialization aborted before configuration/patch installation; existing game tables are unchanged. {DescribeDependencyFailure(error, assembly)}");
            throw;
        }
        if (!IsAvailable)
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo("EWD is not loaded: standalone DNS enabled. EWD data/fields/objects and event commands require EWD 1.71 or newer.");
        else
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo($"EWD data constants: string={_stringConstant!.Method.Name}, int={_intConstant!.Method.Name} (API-selected).");
    }

    internal static void ConfigureDependency(Assembly? assembly)
    {
        Func<string, object>? stringConstant = null;
        Func<int, object>? intConstant = null;
        Func<string, Vector3, object?, float, object>? createObject = null;
        Func<object, float>? objectChance = null;
        Action<object, Vector3>? spawnObject = null;
        if (assembly != null)
        {
            // VERSION is informational, not a compatibility key (including suffixes
            // and forks). Check the core data contract without resolving typed EWD
            // helpers on the standalone path. Ownership contracts are checked below.
            Type data = RequireType(assembly, "Data.DataEntry");
            Type helper = RequireType(assembly, "Data.DataHelper");
            RequireMethod(helper, "Get", data, typeof(string), typeof(string));
            RequireMethod(helper, "Merge", data, data, data);
            RequireMethod(helper, "Init", typeof(ZDO), typeof(GameObject), typeof(Vector3),
                typeof(Quaternion), typeof(Vector3?), data);
            Type values = RequireType(assembly, "Data.DataValue");
            stringConstant = (Func<string, object>)Delegate.CreateDelegate(typeof(Func<string, object>),
                RequireConstantFactory(values, typeof(string), RequireType(assembly, "Data.IStringValue")));
            intConstant = (Func<int, object>)Delegate.CreateDelegate(typeof(Func<int, object>),
                RequireConstantFactory(values, typeof(int), RequireType(assembly, "Data.IIntValue")));
            foreach (string valueType in new[] { "String", "Int", "Float", "Long", "Bool", "Vector3", "Quaternion" })
                RequireMethod(values, valueType, RequireType(assembly, $"Data.I{valueType}Value"), typeof(string));
            (createObject, objectChance, spawnObject) = BuildObjectAccessors(assembly, data);
        }
        // Publish only a complete contract. A failed probe retains any existing one;
        // startup aborts before DNS installs patches or replaces game tables.
        _stringConstant = stringConstant;
        _intConstant = intConstant;
        _createObject = createObject;
        _objectChance = objectChance;
        _spawnObject = spawnObject;
        _assembly = assembly;
    }

    private static (Func<string, Vector3, object?, float, object>, Func<object, float>, Action<object, Vector3>)
        BuildObjectAccessors(Assembly assembly, Type data)
    {
        Type blueprint = RequireType(assembly, "ExpandWorldData.BlueprintObject");
        ConstructorInfo constructor = blueprint.GetConstructor(new[] { typeof(string), typeof(Vector3), typeof(Quaternion),
            typeof(Vector3), data, typeof(float), typeof(bool) }) ?? throw new MissingMethodException(blueprint.FullName, ".ctor");
        FieldInfo chance = RequireField(blueprint, "Chance");
        if (!chance.IsPublic || chance.IsStatic || chance.FieldType != typeof(float))
            throw new MissingFieldException(blueprint.FullName, "public float Chance");
        Type dataTransform = typeof(Func<,,>).MakeGenericType(data, typeof(string), data);
        MethodInfo spawn = RequireMethod(RequireType(assembly, "ExpandWorldData.Spawn"), "BPO", typeof(void),
            blueprint, typeof(Vector3), typeof(Quaternion), typeof(Vector3), dataTransform,
            typeof(Func<string, string>), typeof(List<GameObject>));
        if (!spawn.IsPublic) throw new MissingMethodException(spawn.DeclaringType!.FullName, "public BPO");

        var prefab = Expression.Parameter(typeof(string), "prefab");
        var position = Expression.Parameter(typeof(Vector3), "position");
        var payload = Expression.Parameter(typeof(object), "data");
        var probability = Expression.Parameter(typeof(float), "chance");
        var obj = Expression.Parameter(typeof(object), "obj");
        var typedData = Expression.Parameter(data, "data");
        // Compile once during dependency detection; no reflection or boxed argument
        // arrays on the spawn path. Opaque delegates keep standalone loading safe.
        var create = Expression.Lambda<Func<string, Vector3, object?, float, object>>(
            Expression.New(constructor, prefab, position, Expression.Constant(Quaternion.identity),
                Expression.Constant(Vector3.one), Expression.Convert(payload, data), probability, Expression.Constant(false)),
            prefab, position, payload, probability).Compile();
        var readChance = Expression.Lambda<Func<object, float>>(Expression.Field(Expression.Convert(obj, blueprint), chance), obj).Compile();
        Delegate identity = Expression.Lambda(dataTransform, typedData, typedData, prefab).Compile();
        var invoke = Expression.Lambda<Action<object, Vector3>>(Expression.Call(spawn, Expression.Convert(obj, blueprint), position,
            Expression.Constant(Quaternion.identity), Expression.Constant(Vector3.one), Expression.Constant(identity, dataTransform),
            Expression.Constant((Func<string, string>)(name => name)), Expression.Constant(null, typeof(List<GameObject>))), obj, position).Compile();
        return (create, readChance, invoke);
    }

    internal static object CreateObject(string prefab, Vector3 position, object? data, float chance) =>
        _createObject!(prefab, position, data, chance);

    internal static void SpawnObjects(Vector3 position, IList objects)
    {
        foreach (object obj in objects)
        {
            float chance = _objectChance!(obj);
            if (chance < 1f && UnityEngine.Random.value > chance) continue;
            _spawnObject!(obj, position);
        }
    }

    private static MethodInfo RequireConstantFactory(Type values, Type argument, Type returns)
    {
        foreach (string name in new[] { "Constant", "Simple" })
        {
            MethodInfo? method = values.GetMethod(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                null, new[] { argument }, null);
            if (method != null && !method.ContainsGenericParameters && method.ReturnType == returns) return method;
        }
        throw new MissingMethodException($"EWD requires public static {returns.FullName} {values.FullName}.Constant({argument.FullName}) or Simple({argument.FullName}).");
    }

    internal static object Constant(string value) => _stringConstant!(value);
    internal static object Constant(int value) => _intConstant!(value);

    internal static bool IsDependencyFailure(Exception error)
    {
        if (error is TargetInvocationException or TypeInitializationException && error.InnerException != null)
            return IsDependencyFailure(error.InnerException);
        return error is MissingMemberException or TypeLoadException or ReflectionTypeLoadException or
            FileLoadException or FileNotFoundException or BadImageFormatException;
    }

    internal static string DescribeDependencyFailure(Exception error, Assembly? assembly = null)
    {
        string version = "unknown";
        try
        {
            FieldInfo? field = (assembly ?? _assembly)?.GetType("ExpandWorldData.EWD")?.GetField("VERSION");
            if (field?.IsLiteral == true) version = field.GetRawConstantValue()?.ToString() ?? version;
        }
        catch { /* A diagnostic must not resolve another broken dependency. */ }
        return $"DNS {DropNSpawnPlugin.ModVersion} / EWD {version}: dependency API failure, not an invalid YAML entry. {error.GetBaseException().Message}";
    }

    internal static void RequireExtensions(string? data, Dictionary<string, string>? fields, List<string>? objects, string context)
    {
        if (IsAvailable) return;
        List<string> required = new();
        if (!string.IsNullOrWhiteSpace(data)) required.Add("data");
        if (fields?.Keys.Any(key => !string.IsNullOrWhiteSpace(key)) == true) required.Add("fields");
        if (objects?.Any(value => !string.IsNullOrWhiteSpace(value)) == true) required.Add("objects");
        if (required.Count > 0) RequireFeature(string.Join("/", required), context);
    }

    internal static void RequireFeature(string feature, string context)
    {
        if (!IsAvailable)
            throw new InvalidDataException($"Entry '{context}' uses '{feature}', which requires Expand World Data 1.71 or newer. Install EWD on the server and clients or remove these settings. This configuration update was rejected, not partially applied.");
    }

    internal static void Initialize(Harmony harmony)
    {
        if (!IsAvailable) return;
        InitializePresent(harmony, _assembly!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializePresent(Harmony harmony, Assembly assembly)
    {
        if (!HasIntegratedDomains(assembly)) return;
        var ewdHarmony = (Harmony)RequireField(RequireType(assembly, "ExpandWorldData.EWD"), "Harmony").GetValue(null);
        InstallPatches(harmony, assembly, ewdHarmony);
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(
            "EWD compatibility: normal Spawn/Event/Drop features are disabled while DropNSpawn is loaded (including DNS override Off). " +
            "EWD settings and YAML are unchanged; world/AltBiome data and AltBiome spawn customization remain active. Restart after changing the mod set.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void InstallPatches(Harmony harmony, Assembly assembly, Harmony ewdHarmony)
    {
        var patches = GetPatchPlan(assembly);
        if (patches.Count == 0) return; // EWD before the Spawn/Event integration.

        Type loader = RequireType(assembly, "ExpandWorld.Spawn.Loader");
        Type source = RequireType(assembly, "ExpandWorld.Spawn.Data");
        _data = RequireField(loader, "Data");
        _objects = RequireField(loader, "Objects");
        _sourceFields = GetSourceFields(source);
        Type? requirements = FindType(assembly, "ExpandWorld.Spawn.Requirements");
        _requirements = requirements == null ? null : RequireField(loader, "Requirements");
        _requiredKeys = requirements == null ? null : RequireField(requirements, "Keys");
        _consumeAltBiomeKeys = requirements == null ? null :
            (Action<SpawnSystem.SpawnData>)Delegate.CreateDelegate(typeof(Action<SpawnSystem.SpawnData>),
                RequireMethod(RequireType(assembly, "ExpandWorld.Spawn.GlobalKeys"), "Consume", typeof(void), typeof(SpawnSystem.SpawnData)));

        // The handoff immediately calls the code we patch below. StartupAccelerator
        // normally defers executable wrapper updates until Chainloader has finished.
        // Borrow only its in-memory gate exemptions, including rollback; never save
        // settings or disable batching for the rest of the mod set.
        HashSet<string>? passthrough = GetStartupAcceleratorPassthrough();
        List<string> addedPassthrough = new();
        try
        {
            foreach (var patch in patches)
            {
                if (passthrough != null &&
                    (patch.Callback == nameof(DisableOwnedPatch) ||
                     (patch.Callback == nameof(DisableOwnedFeatures) && patch.Target.Name == "Patch")))
                {
                    string name = patch.Target.DeclaringType!.FullName!;
                    if (passthrough.Add(name)) addedPassthrough.Add(name);
                }
                var method = new HarmonyMethod(typeof(ExpandWorldDataCompatibility), patch.Callback);
                harmony.Patch(patch.Target,
                    prefix: patch.Kind == HarmonyPatchType.Prefix ? method : null,
                    postfix: patch.Kind == HarmonyPatchType.Postfix ? method : null,
                    transpiler: patch.Kind == HarmonyPatchType.Transpiler ? method : null);
            }

            // EWD.Awake ran first and may already have installed these patches.
            // Run its own disable paths so its patch-state flags stay consistent.
            RefreshEwdPatches(assembly, ewdHarmony);
            // The shared EWD registry logs unpatch failures instead of throwing.
            // Verify the ownership boundary rather than treating a void return as success.
            foreach (MethodBase target in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(target);
                if (info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                    .Any(patch => patch.owner == ewdHarmony.Id && patch.PatchMethod.DeclaringType?.Assembly == assembly &&
                                  IsOwnedDomain(patch.PatchMethod.DeclaringType, patch.PatchMethod.Name)))
                    throw new InvalidOperationException($"EWD still owns a DNS domain patch on {target.DeclaringType?.FullName}.{target.Name}; restart required.");
            }
        }
        catch
        {
            // Do not leave a partially installed compatibility layer behind.
            foreach (var patch in patches)
            {
                try { harmony.Unpatch(patch.Target, AccessTools.DeclaredMethod(typeof(ExpandWorldDataCompatibility), patch.Callback)); }
                catch (Exception removeError)
                {
                    DropNSpawnPlugin.DropNSpawnLogger.LogError($"EWD compatibility patch removal failed; restart required. {removeError}");
                }
            }
            try { RefreshEwdPatches(assembly, ewdHarmony); }
            catch (Exception restoreError)
            {
                DropNSpawnPlugin.DropNSpawnLogger.LogError($"EWD patch restoration failed; restart required. {restoreError}");
            }
            throw;
        }
        finally
        {
            foreach (string name in addedPassthrough) passthrough!.Remove(name);
        }
    }

    private static HashSet<string>? GetStartupAcceleratorPassthrough()
    {
        // Inspect the active interceptor, not a DLL on disk or a version string.
        // No optional assembly is loaded and Delay Patching=Off needs no interop.
        Type? patchFunctions = typeof(Harmony).Assembly.GetType("HarmonyLib.PatchFunctions");
        MethodInfo? updateWrapper = patchFunctions == null ? null : AccessTools.DeclaredMethod(patchFunctions, "UpdateWrapper");
        Type? interceptor = updateWrapper == null ? null : Harmony.GetPatchInfo(updateWrapper)?.Prefixes
            .Select(patch => patch.PatchMethod)
            .FirstOrDefault(method => method.Name == "SkipUpdates" &&
                method.DeclaringType?.FullName == "StartupAccelerator.StartupAccelerator+InterceptChainloader")?.DeclaringType;
        if (interceptor == null) return null;

        FieldInfo? field = AccessTools.DeclaredField(interceptor.DeclaringType, "passthroughClasses");
        if (field is { IsStatic: true } && field.GetValue(null) is HashSet<string> classes) return classes;
        throw new InvalidOperationException("StartupAccelerator is deferring Harmony patches, but its passthrough contract is unsupported. " +
            "Set StartupAccelerator's Delay Patching to Off and restart; DNS cannot safely take ownership of EWD domains yet.");
    }

    private static void RefreshEwdPatches(Assembly assembly, Harmony ewdHarmony)
    {
        foreach (string domain in FindType(assembly, "ExpandWorld.Spawn.SpawnManager") != null
                     ? new[] { "Spawn", "Event", "Drops" } : new[] { "Spawn", "Event" })
            RequireMethod(RequireType(assembly, $"ExpandWorld.{domain}.Patcher"), "Patch", typeof(void), typeof(Harmony))
                .Invoke(null, new object[] { ewdHarmony });
    }

    // Resolve the complete contract before changing any patches. Keeping this plan
    // explicit also lets the test host validate the official EWD DLL without Unity.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static List<(MethodInfo Target, HarmonyPatchType Kind, string Callback)> GetPatchPlan(Assembly assembly)
    {
        var result = new List<(MethodInfo, HarmonyPatchType, string)>();
        if (!HasIntegratedDomains(assembly)) return result;

        if (FindType(assembly, "ExpandWorld.Spawn.SpawnManager") != null)
        {
            bool transactional = false;
            // The synced-manager layout uses one patch registry, with unconditional
            // lifecycle hooks and independent event scheduling gates. Let its own
            // unpatch path maintain registry state and run cleanup callbacks.
            Add(RequireType(assembly, "ExpandWorldData.Patches"), "Apply", typeof(void),
                HarmonyPatchType.Prefix, nameof(DisableOwnedPatch), typeof(Harmony), typeof(bool),
                typeof(Type), typeof(string), typeof(Type), typeof(string), typeof(HarmonyPatchType),
                typeof(int), typeof(Type[]), typeof(bool), typeof(object), typeof(Action));
            foreach (string domain in new[] { "Spawn", "Event", "Drops" })
            {
                Type manager = RequireType(assembly, $"ExpandWorld.{domain}.{(domain == "Drops" ? "Drop" : domain)}Manager");
                // Resolve refresh targets before installing any patch as well.
                RequireMethod(RequireType(assembly, $"ExpandWorld.{domain}.Patcher"), "Patch", typeof(void), typeof(Harmony));
                Add(manager, "ReadConfigs", typeof(void), HarmonyPatchType.Prefix, nameof(SkipOwnedDomain));
                Add(manager, "FromSetting", typeof(void), HarmonyPatchType.Prefix, nameof(SkipOwnedDomain), typeof(string));
                MethodInfo set = AccessTools.DeclaredMethod(manager, "Set", new[] { typeof(Dictionary<string, string>) })
                    ?? throw new MissingMethodException(manager.FullName, "Set");
                if (set.ReturnType == typeof(bool))
                    Add(manager, "Set", typeof(bool), HarmonyPatchType.Prefix, nameof(SkipOwnedDomain), typeof(Dictionary<string, string>));
                else
                {
                    Type setResult = RequireType(assembly, "ExpandWorldData.Data.SetResult");
                    RequireFailedResult(setResult);
                    Add(manager, "Set", setResult, HarmonyPatchType.Transpiler, nameof(RejectOwnedSet), typeof(Dictionary<string, string>));
                    if (domain == "Spawn") transactional = true;
                }
                Add(manager, domain == "Drops" ? "ToReferenceFile" : "CreateConfigs", typeof(void), HarmonyPatchType.Prefix, nameof(SkipOwnedDomain));
            }
            Add(RequireType(assembly, "ExpandWorld.Spawn.SpawnManager"), "ApplySpawnData", typeof(void),
                HarmonyPatchType.Prefix, nameof(SkipOwnedDomain), typeof(SpawnSystem));
            Add(RequireType(assembly, "ExpandWorld.Spawn.SpawnManager"), "InitializeSpawnSystem", typeof(void),
                HarmonyPatchType.Prefix, nameof(SkipOwnedDomain), typeof(SpawnSystem));
            Add(RequireType(assembly, "ExpandWorld.Event.EventManager"), "ApplyTiming", typeof(void),
                HarmonyPatchType.Prefix, nameof(SkipOwnedDomain), typeof(RandEventSystem));
            Type events = RequireType(assembly, "ExpandWorld.Event.EventManager");
            if (transactional)
                RequireMethod(events, "Initialize", typeof(void)); // shared content initializer only snapshots native tables
            else
                Add(RequireType(assembly, "ExpandWorld.Spawn.SpawnManager"), "InitializeData", typeof(void),
                    HarmonyPatchType.Prefix, nameof(SkipOwnedDomain));
            foreach (string name in transactional ? new[] { "InitializeServerData" } : new[] { "DelayClientLoad", "InitializeServerData", "InitializeClientData" })
                Add(RequireType(assembly, "ExpandWorld.Event.EventManager"), name, typeof(void), HarmonyPatchType.Prefix, nameof(SkipOwnedDomain));
            Add(RequireType(assembly, "ExpandWorld.Drops.DropManager"), "InitializeData", typeof(void),
                HarmonyPatchType.Prefix, nameof(SkipOwnedDomain));
        }
        else foreach (string domain in new[] { "Spawn", "Event" })
        {
            Type manager = RequireType(assembly, $"ExpandWorld.{domain}.Manager");
            foreach (string name in new[] { "CreateConfig", "ReadConfig", "Toggle" })
                Add(manager, name, typeof(void), HarmonyPatchType.Prefix, nameof(SkipOwnedDomain));
            foreach (string name in new[] { "FromSetting", "Set" })
                Add(manager, name, typeof(void), HarmonyPatchType.Prefix, nameof(SkipOwnedDomain), typeof(string));
            Add(RequireType(assembly, $"ExpandWorld.{domain}.Patcher"), "Patch", typeof(void),
                HarmonyPatchType.Transpiler, nameof(DisableOwnedFeatures), typeof(Harmony));
            Add(manager, domain == "Spawn" ? "ApplySpawnData" : "ApplyTiming", typeof(void),
                HarmonyPatchType.Prefix, nameof(SkipOwnedDomain), domain == "Spawn" ? typeof(SpawnSystem) : typeof(RandEventSystem));
        }
        Add(RequireType(assembly, "ExpandWorldData.Api"), "GetCurrentRandomEvent", typeof(RandomEvent),
            HarmonyPatchType.Transpiler, nameof(DisableOwnedFeatures), typeof(Vector3));

        Type data = RequireType(assembly, "ExpandWorld.Spawn.Data");
        Type loader = RequireType(assembly, "ExpandWorld.Spawn.Loader");
        GetSourceFields(data);
        foreach (string name in new[] { "Data", "Objects" })
        {
            FieldInfo field = RequireField(loader, name);
            if (!field.IsStatic || !typeof(IDictionary).IsAssignableFrom(field.FieldType))
                throw new MissingFieldException(loader.FullName, name);
        }
        Type? requirements = FindType(assembly, "ExpandWorld.Spawn.Requirements");
        if (requirements != null)
        {
            FieldInfo map = RequireField(loader, "Requirements");
            FieldInfo keysField = RequireField(requirements, "Keys");
            if (!map.IsStatic || !typeof(IDictionary).IsAssignableFrom(map.FieldType))
                throw new MissingFieldException(loader.FullName, "static Requirements dictionary");
            if (keysField.IsStatic || keysField.FieldType != typeof(List<string[]>))
                throw new MissingFieldException(requirements.FullName, "List<string[]> Keys");
            foreach (string name in new[] { "TranspileKeys", "TranspileEnvironments" })
                RequireMethod(requirements, name, typeof(IEnumerable<CodeInstruction>), typeof(IEnumerable<CodeInstruction>));
            foreach (string name in new[] { "CheckLocations", "CheckObjects" })
                RequireMethod(requirements, name, typeof(void), typeof(SpawnSystem.SpawnData), typeof(Vector3), typeof(bool).MakeByRefType());
            Type keys = RequireType(assembly, "ExpandWorld.Spawn.GlobalKeys");
            RequireMethod(keys, "Consume", typeof(void), typeof(SpawnSystem.SpawnData));
            RequireMethod(keys, "CheckRequirement", typeof(bool), typeof(ZoneSystem), typeof(string), typeof(bool).MakeByRefType());
            RequireMethod(keys, "Mutate", typeof(void), typeof(ZoneSystem), typeof(string).MakeByRefType());
        }
        Add(RequireType(assembly, "ExpandWorld.Spawn.LoaderFields"), "HandleCustomData", typeof(EwdData.DataEntry),
            HarmonyPatchType.Transpiler, nameof(DisableSpawnDrops), data, typeof(SpawnSystem.SpawnData));
        Add(loader, "FromData", typeof(SpawnSystem.SpawnData), HarmonyPatchType.Postfix,
            nameof(CaptureSpawnData), data, typeof(string));
        Add(loader, "ToData", data, HarmonyPatchType.Postfix, nameof(RestoreSpawnData), typeof(SpawnSystem.SpawnData));
        return result;

        void Add(Type type, string name, Type returns, HarmonyPatchType kind, string callback, params Type[] args) =>
            result.Add((RequireMethod(type, name, returns, args), kind, callback));
    }

    private static Type RequireType(Assembly assembly, string name) =>
        FindType(assembly, name) ?? throw new TypeLoadException($"EWD requires type {name} or its supported relocated API.");

    private static bool HasIntegratedDomains(Assembly assembly)
    {
        if (FindType(assembly, "ExpandWorld.Spawn.Patcher") != null) return true;
        foreach (string name in new[] { "ExpandWorld.Spawn.Manager", "ExpandWorld.Spawn.SpawnManager", "ExpandWorld.Spawn.Loader",
                     "ExpandWorld.Spawn.Data", "ExpandWorld.Event.Patcher", "ExpandWorld.Event.EventManager",
                     "ExpandWorld.Drops.Patcher", "ExpandWorld.Drops.DropManager" })
            if (FindType(assembly, name) != null)
                throw new TypeLoadException("EWD integrated domains are present but the Spawn.Patcher ownership contract is missing.");
        return false;
    }

    private static FieldInfo RequireField(Type type, string name) =>
        AccessTools.DeclaredField(type, name) ?? throw new MissingFieldException(type.FullName, name);

    private static MethodInfo RequireMethod(Type type, string name, Type returns, params Type[] args)
    {
        MethodInfo? method = AccessTools.DeclaredMethod(type, name, args);
        if (method == null || !method.IsStatic || method.ReturnType != returns)
            throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static bool SkipOwnedDomain() => false;

    private static void DisableOwnedPatch(ref bool __1, Type __4, string __5)
    {
        // Arguments are shouldPatch and patchType. Do not affect the shared
        // registry's world, AltBiome, blueprint or common-data patches.
        if (IsOwnedDomain(__4, __5))
            __1 = false;
    }

    private static bool IsOwnedDomain(Type type, string method)
    {
        // These operate on EWD's AltBiome requirement metadata, not normal tables.
        // Keep EWD's own activation/refresh gates; do not force them on globally.
        if (type.FullName is "ExpandWorldData.Spawn.Requirements" or "ExpandWorld.Spawn.Requirements" &&
            method is "TranspileKeys" or "TranspileEnvironments" or "CheckLocations" or "CheckObjects") return false;
        if (type.FullName is "ExpandWorldData.Spawn.GlobalKeys" or "ExpandWorld.Spawn.GlobalKeys" &&
            method is "CheckRequirement" or "Mutate" && FindType(type.Assembly, "ExpandWorld.Spawn.Requirements") != null) return false;
        return type.Namespace is "ExpandWorld.Spawn" or "ExpandWorld.Event" or "ExpandWorld.Drops" or
            "ExpandWorldData.Spawn" or "ExpandWorldData.Event" or "ExpandWorldData.Drop";
    }

    private static int RequireFailedResult(Type result)
    {
        FieldInfo? failed = result.GetField("Failed");
        if (!result.IsEnum || Enum.GetUnderlyingType(result) != typeof(int) || failed?.IsLiteral != true)
            throw new MissingFieldException(result.FullName, "Failed (int enum)");
        return (int)failed.GetRawConstantValue();
    }

    private static IEnumerable<CodeInstruction> RejectOwnedSet(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        // New loaders return Applied=0; skipping with the default return value would
        // falsely report success. Return their declared failure value without loading.
        yield return new CodeInstruction(OpCodes.Ldc_I4, RequireFailedResult(((MethodInfo)__originalMethod).ReturnType));
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static FieldInfo[] GetSourceFields(Type source)
    {
        var fields = new List<FieldInfo>(new[] { "data", "fields", "objects", "faction" }.Select(name => RequireField(source, name)));
        if (FindType(source.Assembly, "ExpandWorld.Spawn.Requirements") != null)
            fields.AddRange(new[] { "requiredGlobalKeys", "forbiddenGlobalKeys", "requiredEnvironments", "forbiddenEnvironments",
                "requiredLocations", "forbiddenLocations", "requiredObjects", "forbiddenObjects" }.Select(name => RequireField(source, name)));
        return fields.ToArray();
    }

    internal static bool HasAltBiomeKeyConsumer(SpawnSystem.SpawnData critter)
    {
        if (_consumeAltBiomeKeys == null || !SourceData.TryGetValue(critter, out _)) return false;
        // Only complex required-key rows need EWD's per-row AND/OR consumption.
        // Simple native keys retain DNS's existing enabled/disabled behavior; EWD
        // does not activate numeric helpers for simple AltBiome keys alone.
        object? requirements = ((IDictionary)_requirements!.GetValue(null))[critter];
        return requirements != null && _requiredKeys!.GetValue(requirements) != null;
    }

    internal static bool ConsumeAltBiomeKeys(SpawnSystem.SpawnData critter)
    {
        if (!HasAltBiomeKeyConsumer(critter)) return false;
        if (!SpawnSystem.m_nospawn && ZoneSystem.instance != null) _consumeAltBiomeKeys!(critter);
        return true;
    }

    private static IEnumerable<CodeInstruction> DisableSpawnDrops(IEnumerable<CodeInstruction> instructions)
    {
        int changed = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            // New loaders no longer test DataDrops. Replace only the value read,
            // not the source DTO, so faction/fields and the user's YAML stay intact.
            if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field &&
                field.DeclaringType?.FullName is "ExpandWorld.Spawn.Data" or "ExpandWorldData.Spawn.Data" && field.Name == "drops" && field.FieldType == typeof(string))
            {
                instruction.opcode = OpCodes.Pop;
                instruction.operand = null;
                yield return instruction; // retain labels/exception markers
                yield return new CodeInstruction(OpCodes.Ldnull);
                changed++;
            }
            else yield return instruction;
        }
        if (changed == 0)
            throw new InvalidOperationException("EWD spawn drop conversion changed; refusing an incomplete loot compatibility patch.");
    }

    private static IEnumerable<CodeInstruction> DisableOwnedFeatures(IEnumerable<CodeInstruction> instructions)
    {
        int changed = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            // Patch consumers, not tiny getters that EWD.Awake may have inlined.
            if (instruction.opcode == OpCodes.Call && instruction.operand is MethodInfo method &&
                method.DeclaringType?.FullName == "ExpandWorldData.Configuration" &&
                method.Name is "get_DataSpawns" or "get_DataEvents" or "get_DataDrops")
            {
                instruction.opcode = OpCodes.Ldc_I4_0;
                instruction.operand = null;
                changed++;
            }
            yield return instruction;
        }
        if (changed == 0)
            throw new InvalidOperationException("EWD feature gates changed; refusing an incomplete Spawn/Event compatibility patch.");
    }

    private static void CaptureSpawnData(object __0, SpawnSystem.SpawnData __result)
    {
        if (__result == null) return;
        // Transactional EWD reloads replace these dictionaries. Cache the fields,
        // not a retired dictionary instance; failed reloads restore their own maps.
        var dataMap = (IDictionary)_data!.GetValue(null);
        var objectMap = (IDictionary)_objects!.GetValue(null);
        object? data = dataMap[__result];
        IList? objects = objectMap[__result] as IList;
        SpawnSystemCustomDataSupport.ApplyPreparedPayload(__result, new SpawnSystemCustomDataSupport.PreparedPayload
        {
            CustomData = data,
            CustomObjects = objects
        });
        // Transfer metadata to the existing weak table: retired AltBiome rows can
        // still serve old heightmaps until regeneration, but must not leak on reload.
        dataMap.Remove(__result);
        objectMap.Remove(__result);
        SourceData.Remove(__result);
        SourceData.Add(__result, __0);
    }

    private static void RestoreSpawnData(SpawnSystem.SpawnData __0, object __result)
    {
        if (!SourceData.TryGetValue(__0, out object source)) return;
        // EWD serializes AltBiome from runtime SpawnData for server synchronization.
        // Its native-field exporter omits these extensions. Do not overwrite native
        // values or restore 'drops': loot remains DNS-owned.
        foreach (FieldInfo field in _sourceFields)
            field.SetValue(__result, field.GetValue(source));
    }
}
