using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

internal static partial class Program
{
    private static void CheckEventCompatibilityContracts(ModContract mod)
    {
        CheckEventCloneMetadataLifetime(mod);
        CheckNearestEventOrdering(mod);
        CheckVneiSubscriptionShutdown(mod);
    }

    private static void CheckEventCloneMetadataLifetime(ModContract mod)
    {
        Type manager = mod.Type("EventManager");
        Type support = mod.Type("SpawnSystemCustomDataSupport");
        Assembly game = mod.LoadGameAssembly("assembly_valheim");
        Type eventType = game.GetType("RandomEvent", throwOnError: true)!;
        Type spawnType = game.GetType("SpawnSystem+SpawnData", throwOnError: true)!;
        object metadataTable = manager.GetField("EventMetadata", All)!.GetValue(null)!;
        Type metadataType = manager.GetNestedType("EventRuntimeMetadata", All)!;
        object metadata = Activator.CreateInstance(metadataType, nonPublic: true)!;
        PropertyInfo commandsProperty = metadataType.GetProperty("StartCommands", All)!;
        var sourceCommands = new List<string> { "source command" };
        commandsProperty.SetValue(metadata, sourceCommands);

        object source = NewManagedEvent(eventType, spawnType, out object sourceSpawn);
        object liveClone = NewManagedEvent(eventType, spawnType, out object liveSpawn);
        Call(metadataTable, "Add", source, metadata);
        object payload = NewEventTestPayload(support);
        Invoke(support, null, "ApplyPreparedPayload", sourceSpawn, payload);
        try
        {
            Invoke(manager, null, "CopySpawnPayloads", source, liveClone);
            Check(TryReadWeakValue(metadataTable, liveClone, out object? clonedMetadata), "event clone receives metadata");
            Check(!ReferenceEquals(metadata, clonedMetadata), "event clone owns its metadata object");
            var clonedCommands = (List<string>)commandsProperty.GetValue(clonedMetadata)!;
            Check(!ReferenceEquals(sourceCommands, clonedCommands), "event clone owns mutable command list");
            clonedCommands.Add("clone command");
            Check(sourceCommands.SequenceEqual(new[] { "source command" }), "event clone mutation preserves source metadata");
            Check((bool)Invoke(support, null, "HasPreparedPayload", liveSpawn)!, "event clone receives spawn payload");

            (WeakReference unownedEvent, WeakReference unownedSpawn) =
                CreateUnownedEventClone(manager, eventType, spawnType, liveClone);
            for (int i = 0; i < 5 && (unownedEvent.IsAlive || unownedSpawn.IsAlive); i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Check(!unownedEvent.IsAlive, "event metadata does not retain an unowned event clone");
            Check(!unownedSpawn.IsAlive, "event payload does not retain an unowned clone's spawn row");
            Check(TryReadWeakValue(metadataTable, liveClone, out object? survivingMetadata) &&
                  ReferenceEquals(clonedMetadata, survivingMetadata), "owned event clone retains metadata after GC");

            Call(metadataTable, "Remove", source);
            Invoke(support, null, "ApplyPreparedPayload", sourceSpawn, null);
            Check(!TryReadWeakValue(metadataTable, source, out _), "source event metadata can be removed independently");
            Check(TryReadWeakValue(metadataTable, liveClone, out _), "source cleanup preserves owned clone metadata");
            Check((bool)Invoke(support, null, "HasPreparedPayload", liveSpawn)!, "source cleanup preserves owned event clone payload");
            GC.KeepAlive(source);
            GC.KeepAlive(liveClone);
        }
        finally
        {
            Call(metadataTable, "Remove", source);
            Call(metadataTable, "Remove", liveClone);
            Invoke(support, null, "ApplyPreparedPayload", sourceSpawn, null);
            Invoke(support, null, "ApplyPreparedPayload", liveSpawn, null);
        }
    }

    private static object NewEventTestPayload(Type support)
    {
        Type payloadType = support.GetNestedType("PreparedPayload", All)!;
        object payload = Activator.CreateInstance(payloadType, nonPublic: true)!;
        PropertyInfo objectsProperty = payloadType.GetProperty("CustomObjects")!;
        var objects = (IList)Activator.CreateInstance(objectsProperty.PropertyType)!;
        objects.Add(RuntimeHelpers.GetUninitializedObject(objectsProperty.PropertyType.GetGenericArguments()[0]));
        objectsProperty.SetValue(payload, objects);
        return payload;
    }

    private static object NewManagedEvent(Type eventType, Type spawnType, out object spawn)
    {
        // RandomEvent's normal constructor creates a native AnimationCurve.
        // These tests call only its mod-owned metadata path, using actual game
        // objects and fields without constructing any native Unity object.
        object randomEvent = RuntimeHelpers.GetUninitializedObject(eventType);
        FieldInfo spawnField = eventType.GetField("m_spawn", All)!;
        var spawns = (IList)Activator.CreateInstance(spawnField.FieldType)!;
        spawn = Activator.CreateInstance(spawnType)!;
        spawns.Add(spawn);
        spawnField.SetValue(randomEvent, spawns);
        return randomEvent;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Event, WeakReference Spawn) CreateUnownedEventClone(
        Type manager, Type eventType, Type spawnType, object source)
    {
        object clone = NewManagedEvent(eventType, spawnType, out object spawn);
        Invoke(manager, null, "CopySpawnPayloads", source, clone);
        return (new WeakReference(clone), new WeakReference(spawn));
    }

    private static bool TryReadWeakValue(object table, object key, out object? value)
    {
        object?[] args = { key, null };
        bool found = (bool)Call(table, "TryGetValue", args)!;
        value = args[1];
        return found;
    }

    private static void CheckNearestEventOrdering(ModContract mod)
    {
        Type manager = mod.Type("EventManager");
        Type eventType = mod.LoadGameAssembly("assembly_valheim").GetType("RandomEvent", throwOnError: true)!;
        Type utilsType = mod.LoadGameAssembly("assembly_utils").GetType("Utils", throwOnError: true)!;
        FieldInfo positionField = eventType.GetField("m_pos", All)!;
        Type vectorType = positionField.FieldType;
        MethodInfo distance = utilsType.GetMethod("DistanceXZ", All, null, new[] { vectorType, vectorType }, null)!;
        var activeEvents = (IList)manager.GetField("MultipleActiveEvents", All)!.GetValue(null)!;
        object[] previousEvents = activeEvents.Cast<object>().ToArray();
        object origin = EventTestPosition(vectorType, 0f, 0f, 0f);
        (string Name, (float X, float Y, float Z)[] Positions)[] cases =
        {
            ("empty", Array.Empty<(float, float, float)>()),
            ("single", new[] { (3f, 0f, 4f) }),
            ("first equal distance wins, ignoring height", new[] { (3f, 99f, 4f), (-3f, -99f, -4f) }),
            ("later nearer event", new[] { (10f, 0f, 10f), (1f, 0f, 0f), (8f, 0f, 0f) }),
            ("NaN after finite", new[] { (1f, 0f, 0f), (float.NaN, 0f, 0f), (2f, 0f, 0f) }),
            ("first NaN wins", new[] { (float.NaN, 0f, 0f), (float.NaN, 1f, 0f), (0f, 0f, 0f) }),
            ("finite after infinity", new[] { (float.PositiveInfinity, 0f, 0f), (2f, 0f, 0f) }),
            ("equal infinite distances", new[] { (float.NegativeInfinity, 0f, 0f), (float.PositiveInfinity, 0f, 0f) })
        };

        try
        {
            foreach ((string name, (float X, float Y, float Z)[] positions) in cases)
            {
                activeEvents.Clear();
                foreach ((float x, float y, float z) in positions)
                {
                    object randomEvent = RuntimeHelpers.GetUninitializedObject(eventType);
                    positionField.SetValue(randomEvent, EventTestPosition(vectorType, x, y, z));
                    activeEvents.Add(randomEvent);
                }

                // The former LINQ expression is the compatibility oracle. Both
                // paths call the actual game's DistanceXZ implementation.
                object? expected = activeEvents.Cast<object>()
                    .OrderBy(randomEvent => (float)distance.Invoke(null, new[] { positionField.GetValue(randomEvent), origin })!)
                    .FirstOrDefault();
                object? actual = Invoke(manager, null, "FindNearestMultipleEvent", origin);
                Check(ReferenceEquals(expected, actual), "nearest event preserves LINQ ordering: " + name);
            }
        }
        finally
        {
            activeEvents.Clear();
            foreach (object randomEvent in previousEvents) activeEvents.Add(randomEvent);
        }
    }

    private static object EventTestPosition(Type vectorType, float x, float y, float z)
    {
        object position = Activator.CreateInstance(vectorType)!;
        vectorType.GetField("x")!.SetValue(position, x);
        vectorType.GetField("y")!.SetValue(position, y);
        vectorType.GetField("z")!.SetValue(position, z);
        return position;
    }

    private static void CheckVneiSubscriptionShutdown(ModContract mod)
    {
        Type compatibility = mod.Type("VneiCompatibility");
        FieldInfo subscribedEventField = compatibility.GetField("_indexingItemRecipesEvent", All)!;
        var handler = (Delegate)compatibility.GetField("IndexingItemRecipesHandler", All)!.GetValue(null)!;
        Type fixtureType = typeof(SyntheticIndexingEvents<>).MakeGenericType(handler.GetType().GetGenericArguments()[0]);
        EventInfo indexingEvent = fixtureType.GetEvent("Indexing", All)!;
        PropertyInfo subscriptionCount = fixtureType.GetProperty("SubscriptionCount", All)!;
        object queue = compatibility.GetField("PendingRefreshWorkItems", All)!.GetValue(null)!;
        object pendingSet = compatibility.GetField("PendingRefreshWorkSet", All)!.GetValue(null)!;
        var recipes = (IDictionary)compatibility.GetField("ManagedRecipesByKey", All)!.GetValue(null)!;
        FieldInfo generationField = compatibility.GetField("_refreshGeneration", All)!;
        FieldInfo availableField = compatibility.GetField("_available", All)!;
        FieldInfo initializedField = compatibility.GetField("_initialized", All)!;
        Check(compatibility.GetField("_pendingRefreshCoroutine", All)!.GetValue(null) == null,
            "VNEI managed shutdown test has no native coroutine");
        Check(subscribedEventField.GetValue(null) == null, "VNEI managed shutdown test has no external subscription");

        try
        {
            indexingEvent.AddEventHandler(null, handler);
            subscribedEventField.SetValue(null, indexingEvent);
            Check((int)subscriptionCount.GetValue(null)! == 1, "VNEI fixture has exactly one real delegate subscription");
            Check((bool)Invoke(compatibility, null, "TryDetachIndexingEvent")!, "VNEI callback detaches");
            Check((int)subscriptionCount.GetValue(null)! == 0 && subscribedEventField.GetValue(null) == null,
                "VNEI detach removes its callback and clears event handle");
            Check((bool)Invoke(compatibility, null, "TryDetachIndexingEvent")!, "VNEI callback detach is idempotent");

            indexingEvent.AddEventHandler(null, handler);
            subscribedEventField.SetValue(null, indexingEvent);
            Type recipeKeyType = compatibility.GetNestedType("ManagedRecipeKey", All)!;
            Type recipeKindType = compatibility.GetNestedType("ManagedRecipeKind", All)!;
            object key = Activator.CreateInstance(recipeKeyType, All, null,
                new[] { (object)"regression-prefab", Enum.ToObject(recipeKindType, 0) }, null)!;
            Call(queue, "Enqueue", key);
            Call(pendingSet, "Add", key);
            Type bindingType = compatibility.GetNestedType("ManagedRecipeBinding", All)!;
            object binding = Activator.CreateInstance(bindingType, nonPublic: true)!;
            bindingType.GetProperty("Recipe", All)!.SetValue(binding, new object());
            recipes.Add(key, binding);
            availableField.SetValue(null, true);
            initializedField.SetValue(null, true);
            int initialGeneration = (int)generationField.GetValue(null)!;

            CheckVneiReadinessRetry(compatibility, queue, pendingSet);

            Invoke(compatibility, null, "Shutdown", new object?[] { null });
            Check((int)subscriptionCount.GetValue(null)! == 0, "VNEI shutdown detaches callback");
            Check((int)queue.GetType().GetProperty("Count", All)!.GetValue(queue)! == 0 &&
                  (int)pendingSet.GetType().GetProperty("Count", All)!.GetValue(pendingSet)! == 0,
                "VNEI shutdown clears queued work and deduplication set");
            Check(recipes.Count == 0, "VNEI shutdown releases managed recipe bindings");
            Check(!(bool)availableField.GetValue(null)! && !(bool)initializedField.GetValue(null)!,
                "VNEI shutdown disables callbacks and permits initialization retry");
            Check((int)generationField.GetValue(null)! == unchecked(initialGeneration + 1),
                "VNEI shutdown invalidates prior refresh generation");
            Invoke(compatibility, null, "Shutdown", new object?[] { null });
            Check((int)subscriptionCount.GetValue(null)! == 0 && recipes.Count == 0,
                "VNEI shutdown is idempotent without a coroutine host");
        }
        finally
        {
            indexingEvent.RemoveEventHandler(null, handler);
            subscribedEventField.SetValue(null, null);
            Invoke(compatibility, null, "Shutdown", new object?[] { null });
        }
    }

    private static void CheckVneiReadinessRetry(Type compatibility, object queue, object pendingSet)
    {
        FieldInfo methodField = compatibility.GetField("_hasIndexedMethod", All)!;
        FieldInfo loggedField = compatibility.GetField("_compatibilityFailureLogged", All)!;
        object? originalMethod = methodField.GetValue(null);
        object? originalLogged = loggedField.GetValue(null);
        try
        {
            SyntheticIndexingReadiness.FailuresRemaining = 3;
            SyntheticIndexingReadiness.Ready = false;
            methodField.SetValue(null, typeof(SyntheticIndexingReadiness).GetMethod("HasIndexed", All)!);
            for (int i = 0; i < 3; i++)
            {
                Check(!(bool)Invoke(compatibility, null, "HasIndexed")!,
                    "VNEI readiness reflection failure returns not ready");
            }
            Check(!(bool)Invoke(compatibility, null, "HasIndexed")!,
                "VNEI successful readiness query preserves not-yet-indexed result");
            Check((int)queue.GetType().GetProperty("Count", All)!.GetValue(queue)! == 1 &&
                  (int)pendingSet.GetType().GetProperty("Count", All)!.GetValue(pendingSet)! == 1,
                "VNEI direct readiness queries preserve pending refresh work");

            SyntheticIndexingReadiness.Ready = true;
            Check((bool)Invoke(compatibility, null, "HasIndexed")!, "VNEI readiness recovers after reflection failures");
            Check((int)queue.GetType().GetProperty("Count", All)!.GetValue(queue)! == 1,
                "VNEI pending work survives until readiness recovers");
            // Coroutine MoveNext references Unity's native clock even on its
            // not-ready branch and cannot be JIT-compiled by this CoreCLR host.
            // Coroutine scheduling, retry and finally require an in-game check.
        }
        finally
        {
            methodField.SetValue(null, originalMethod);
            loggedField.SetValue(null, originalLogged);
            SyntheticIndexingReadiness.FailuresRemaining = 0;
            SyntheticIndexingReadiness.Ready = false;
        }
    }

    // A real CLR event with the same Action<GameObject> delegate type as VNEI.
    // No native GameObject or replacement compatibility implementation is used.
    private static class SyntheticIndexingEvents<T>
    {
        public static event Action<T>? Indexing;
        public static int SubscriptionCount => Indexing?.GetInvocationList().Length ?? 0;
    }

    private static class SyntheticIndexingReadiness
    {
        internal static int FailuresRemaining;
        internal static bool Ready;

        public static bool HasIndexed()
        {
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Synthetic external indexing failure");
            }

            return Ready;
        }
    }
}
