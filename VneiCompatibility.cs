using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

internal static class VneiCompatibility
{
    private const string VneiPluginGuid = "com.maxsch.valheim.vnei";
    private const float RefreshFrameBudgetSeconds = 0.0015f;
    private const int MaxRefreshJobsPerFrame = 8;

    private enum ManagedRecipeKind
    {
        CharacterDrop,
        Container,
        DropOnDestroyed,
        MineRock,
        MineRock5,
        Pickable,
        Fish,
        SpawnArea,
        Destructible,
        TreeBase
    }

    private readonly struct ManagedRecipeKey : IEquatable<ManagedRecipeKey>
    {
        public ManagedRecipeKey(string prefabName, ManagedRecipeKind kind)
        {
            PrefabName = prefabName ?? "";
            Kind = kind;
        }

        public string PrefabName { get; }
        public ManagedRecipeKind Kind { get; }

        public bool Equals(ManagedRecipeKey other)
        {
            return Kind == other.Kind &&
                   string.Equals(PrefabName, other.PrefabName, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj)
        {
            return obj is ManagedRecipeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (StringComparer.OrdinalIgnoreCase.GetHashCode(PrefabName) * 397) ^ (int)Kind;
            }
        }
    }

    private sealed class ManagedRecipeBinding
    {
        public object Recipe { get; set; } = null!;
        public UnityEngine.Object? Source { get; set; }
        public bool IsSupplemental { get; set; }
        public bool DisableSourceItemWhenRemoved { get; set; }
        public string LastDisplayFingerprint { get; set; } = "";
    }

    private static bool _initialized;
    private static bool _available;
    private static Type? _recipeInfoType;
    private static Type? _indexingType;
    private static Type? _amountType;
    private static Type? _itemType;
    private static Type? _partType;
    private static ConstructorInfo? _emptyRecipeCtor;
    private static ConstructorInfo? _rangeAmountCtor;
    private static ConstructorInfo? _singleAmountCtor;
    private static MethodInfo? _addIngredientMethod;
    private static MethodInfo? _addResultMethod;
    private static MethodInfo? _addRecipeToItemsMethod;
    private static MethodInfo? _getItemMethod;
    private static MethodInfo? _hasIndexedMethod;
    private static MethodInfo? _combineGroupAmountsMethod;
    private static MethodInfo? _recipeCalculateIsOnBlacklistMethod;
    private static MethodInfo? _recipeCalculateWidthMethod;
    private static MethodInfo? _recipeUpdateKnownMethod;
    private static MethodInfo? _itemUpdateKnownMethod;
    private static PropertyInfo? _ingredientsProperty;
    private static PropertyInfo? _resultsProperty;
    private static PropertyInfo? _stationsProperty;
    private static PropertyInfo? _recipesProperty;
    private static FieldInfo? _itemIsActiveField;
    private static FieldInfo? _itemResultField;
    private static FieldInfo? _itemIngredientField;
    private static FieldInfo? _partItemField;
    private static FieldInfo? _recipeIsOnBlacklistBackingField;
    private static readonly Dictionary<ManagedRecipeKey, ManagedRecipeBinding> ManagedRecipesByKey = new();
    private static readonly object RefreshSync = new();
    private static readonly RingBufferQueue<ManagedRecipeKey> PendingRefreshWorkItems = new();
    private static readonly HashSet<ManagedRecipeKey> PendingRefreshWorkSet = new();
    private static Coroutine? _pendingRefreshCoroutine;

    internal static void Initialize(Harmony harmony)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (!Chainloader.PluginInfos.ContainsKey(VneiPluginGuid))
        {
            return;
        }

        _recipeInfoType = AccessTools.TypeByName("VNEI.Logic.RecipeInfo");
        _indexingType = AccessTools.TypeByName("VNEI.Logic.Indexing");
        _amountType = AccessTools.TypeByName("VNEI.Logic.Amount");
        _itemType = AccessTools.TypeByName("VNEI.Logic.Item");
        _partType = AccessTools.TypeByName("VNEI.Logic.Part");
        if (_recipeInfoType == null || _indexingType == null || _amountType == null || _itemType == null || _partType == null)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning("VNEI detected, but one or more compatibility types could not be resolved.");
            return;
        }

        _emptyRecipeCtor = AccessTools.Constructor(_recipeInfoType, Type.EmptyTypes);
        _rangeAmountCtor = AccessTools.Constructor(_amountType, new[] { typeof(int), typeof(int), typeof(float) });
        _singleAmountCtor = AccessTools.Constructor(_amountType, new[] { typeof(int), typeof(float) });
        _addIngredientMethod = AccessTools.Method(_recipeInfoType, "AddIngredient", new[] { typeof(string), _amountType, _amountType, typeof(int) });
        _addResultMethod = AccessTools.Method(_recipeInfoType, "AddResult", new[] { typeof(string), _amountType, _amountType, typeof(int) });
        _addRecipeToItemsMethod = AccessTools.Method(_indexingType, "AddRecipeToItems", new[] { _recipeInfoType });
        _getItemMethod = AccessTools.Method(_indexingType, "GetItem", new[] { typeof(string) });
        _hasIndexedMethod = AccessTools.Method(_indexingType, "HasIndexed", Type.EmptyTypes);
        _combineGroupAmountsMethod = AccessTools.Method(_recipeInfoType, "CombineGroupAmounts", new[] { typeof(Dictionary<,>).MakeGenericType(_amountType, typeof(List<>).MakeGenericType(_partType)) });
        _recipeCalculateIsOnBlacklistMethod = AccessTools.Method(_recipeInfoType, "CalculateIsOnBlacklist", Type.EmptyTypes);
        _recipeCalculateWidthMethod = AccessTools.Method(_recipeInfoType, "CalculateWidth", Type.EmptyTypes);
        _recipeUpdateKnownMethod = AccessTools.Method(_recipeInfoType, "UpdateKnown", Type.EmptyTypes);
        _itemUpdateKnownMethod = AccessTools.Method(_itemType, "UpdateKnown", Type.EmptyTypes);
        _ingredientsProperty = AccessTools.Property(_recipeInfoType, "Ingredients");
        _resultsProperty = AccessTools.Property(_recipeInfoType, "Results");
        _stationsProperty = AccessTools.Property(_recipeInfoType, "Stations");
        _recipesProperty = AccessTools.Property(_recipeInfoType, "Recipes");
        _itemIsActiveField = AccessTools.Field(_itemType, "isActive");
        _itemResultField = AccessTools.Field(_itemType, "result");
        _itemIngredientField = AccessTools.Field(_itemType, "ingredient");
        _partItemField = AccessTools.Field(_partType, "item");
        _recipeIsOnBlacklistBackingField = AccessTools.Field(_recipeInfoType, "<IsOnBlacklist>k__BackingField");

        _available =
            _emptyRecipeCtor != null &&
            _rangeAmountCtor != null &&
            _singleAmountCtor != null &&
            _addIngredientMethod != null &&
            _addResultMethod != null &&
            _addRecipeToItemsMethod != null &&
            _getItemMethod != null &&
            _hasIndexedMethod != null &&
            _combineGroupAmountsMethod != null &&
            _recipeCalculateIsOnBlacklistMethod != null &&
            _recipeCalculateWidthMethod != null &&
            _recipeUpdateKnownMethod != null &&
            _itemUpdateKnownMethod != null &&
            _ingredientsProperty != null &&
            _resultsProperty != null &&
            _stationsProperty != null &&
            _recipesProperty != null &&
            _itemResultField != null &&
            _itemIngredientField != null &&
            _partItemField != null;

        if (!_available)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning("VNEI detected, but one or more compatibility members could not be resolved.");
            return;
        }

        PatchConstructor(harmony, new[] { typeof(CharacterDrop) }, nameof(CharacterDropCtorPostfix));
        PatchConstructor(harmony, new[] { typeof(GameObject), typeof(DropTable) }, nameof(DropTableCtorPostfix));
        PatchConstructor(harmony, new[] { typeof(GameObject), typeof(Pickable) }, nameof(PickableCtorPostfix));
        PatchConstructor(harmony, new[] { typeof(SpawnArea) }, nameof(SpawnAreaCtorPostfix));
        PatchConstructor(harmony, new[] { typeof(Destructible) }, nameof(DestructibleCtorPostfix));
        PatchConstructor(harmony, new[] { typeof(TreeBase) }, nameof(TreeBaseCtorPostfix));

        EventInfo? onIndexingItemRecipes = _indexingType.GetEvent("OnIndexingItemRecipes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (onIndexingItemRecipes != null)
        {
            onIndexingItemRecipes.AddEventHandler(null, new Action<GameObject>(HandleIndexingItemRecipes));
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogInfo("VNEI compatibility enabled.");
    }

    internal static void RefreshCharacterPrefabs(IEnumerable<string> prefabNames)
    {
        EnqueueRefresh(NormalizePrefabNames(prefabNames).Select(prefabName => new ManagedRecipeKey(prefabName, ManagedRecipeKind.CharacterDrop)));
    }

    internal static void RefreshObjectPrefabs(IEnumerable<string> prefabNames)
    {
        EnqueueRefresh(ExpandObjectPrefabNames(prefabNames).SelectMany(BuildObjectRefreshWorkItems));
    }

    internal static void RefreshSpawnerPrefabs(IEnumerable<string> prefabNames)
    {
        EnqueueRefresh(NormalizePrefabNames(prefabNames).Select(prefabName => new ManagedRecipeKey(prefabName, ManagedRecipeKind.SpawnArea)));
    }

    private static bool CanRefresh()
    {
        return _available && HasIndexed();
    }

    private static bool HasIndexed()
    {
        try
        {
            return _hasIndexedMethod != null && (bool)(_hasIndexedMethod.Invoke(null, Array.Empty<object>()) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> NormalizePrefabNames(IEnumerable<string> prefabNames)
    {
        return (prefabNames ?? Array.Empty<string>())
            .Select(name => (name ?? "").Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ExpandObjectPrefabNames(IEnumerable<string> prefabNames)
    {
        HashSet<string> expanded = NormalizePrefabNames(prefabNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expanded.Count == 0 || ZNetScene.instance == null)
        {
            return expanded;
        }

        HashSet<GameObject> prefabs = new(ZNetScene.instance.m_prefabs);
        prefabs.UnionWith(ZNetScene.instance.m_namedPrefabs.Values);
        foreach (GameObject prefab in prefabs)
        {
            if (prefab == null || expanded.Contains(prefab.name) || !prefab.TryGetComponent(out TreeBase treeBase))
            {
                continue;
            }

            if (TreeBaseUsesAnyChangedLog(treeBase.m_logPrefab, expanded, 0))
            {
                expanded.Add(prefab.name);
            }
        }

        return expanded;
    }

    private static bool TreeBaseUsesAnyChangedLog(GameObject? logPrefab, HashSet<string> changedPrefabs, int depth)
    {
        if (logPrefab == null || depth > 50)
        {
            return false;
        }

        if (changedPrefabs.Contains(logPrefab.name))
        {
            return true;
        }

        if (!logPrefab.TryGetComponent(out TreeLog treeLog) ||
            treeLog.m_subLogPoints == null ||
            treeLog.m_subLogPoints.Length == 0)
        {
            return false;
        }

        return TreeBaseUsesAnyChangedLog(treeLog.m_subLogPrefab, changedPrefabs, depth + 1);
    }

    private static IEnumerable<ManagedRecipeKey> BuildObjectRefreshWorkItems(string prefabName)
    {
        yield return new ManagedRecipeKey(prefabName, ManagedRecipeKind.Container);
        yield return new ManagedRecipeKey(prefabName, ManagedRecipeKind.DropOnDestroyed);
        yield return new ManagedRecipeKey(prefabName, ManagedRecipeKind.MineRock);
        yield return new ManagedRecipeKey(prefabName, ManagedRecipeKind.MineRock5);
        yield return new ManagedRecipeKey(prefabName, ManagedRecipeKind.Pickable);
        yield return new ManagedRecipeKey(prefabName, ManagedRecipeKind.Fish);
        yield return new ManagedRecipeKey(prefabName, ManagedRecipeKind.Destructible);
        yield return new ManagedRecipeKey(prefabName, ManagedRecipeKind.TreeBase);
    }

    private static void EnqueueRefresh(IEnumerable<ManagedRecipeKey> workItems)
    {
        if (!_available)
        {
            return;
        }

        bool shouldStartCoroutine = false;
        lock (RefreshSync)
        {
            foreach (ManagedRecipeKey workItem in workItems)
            {
                if (PendingRefreshWorkSet.Add(workItem))
                {
                    PendingRefreshWorkItems.Enqueue(workItem);
                }
            }

            if (_pendingRefreshCoroutine == null &&
                DropNSpawnPlugin.Instance != null &&
                PendingRefreshWorkItems.Count > 0)
            {
                shouldStartCoroutine = true;
                _pendingRefreshCoroutine = DropNSpawnPlugin.Instance.StartCoroutine(ProcessQueuedRefreshesCoroutine());
            }
        }

        if (!shouldStartCoroutine && DropNSpawnPlugin.Instance == null)
        {
            ProcessQueuedRefreshes(int.MaxValue, float.PositiveInfinity);
        }
    }

    private static IEnumerator ProcessQueuedRefreshesCoroutine()
    {
        yield return null;
        while (true)
        {
            if (CanRefresh())
            {
                ProcessQueuedRefreshes(MaxRefreshJobsPerFrame, Time.realtimeSinceStartup + RefreshFrameBudgetSeconds);
            }

            lock (RefreshSync)
            {
                if (PendingRefreshWorkItems.Count == 0)
                {
                    _pendingRefreshCoroutine = null;
                    yield break;
                }
            }

            yield return null;
        }
    }

    private static bool ProcessQueuedRefreshes(int maxJobs, float deadline)
    {
        if (!CanRefresh())
        {
            return false;
        }

        int processedJobs = 0;
        HashSet<object>? affectedItems = null;
        while (processedJobs < maxJobs &&
               Time.realtimeSinceStartup < deadline &&
               TryDequeueRefreshWorkItem(out ManagedRecipeKey workItem))
        {
            affectedItems ??= new HashSet<object>();
            ProcessQueuedRefreshWorkItem(workItem, affectedItems);
            processedJobs++;
        }

        if (affectedItems != null && affectedItems.Count > 0)
        {
            UpdateKnownForItems(affectedItems);
        }

        return processedJobs > 0;
    }

    private static bool TryDequeueRefreshWorkItem(out ManagedRecipeKey workItem)
    {
        lock (RefreshSync)
        {
            if (!PendingRefreshWorkItems.TryDequeue(out workItem))
            {
                return false;
            }

            PendingRefreshWorkSet.Remove(workItem);
            return true;
        }
    }

    private static void ProcessQueuedRefreshWorkItem(ManagedRecipeKey workItem, HashSet<object> affectedItems)
    {
        switch (workItem.Kind)
        {
            case ManagedRecipeKind.CharacterDrop:
                RefreshCharacterPrefab(workItem.PrefabName, affectedItems);
                break;
            case ManagedRecipeKind.Container:
                RefreshContainerPrefab(workItem.PrefabName, affectedItems);
                break;
            case ManagedRecipeKind.DropOnDestroyed:
            case ManagedRecipeKind.MineRock:
            case ManagedRecipeKind.MineRock5:
                RefreshDropTablePrefab(workItem.PrefabName, workItem.Kind, affectedItems);
                break;
            case ManagedRecipeKind.Pickable:
                RefreshPickablePrefab(workItem.PrefabName, affectedItems);
                break;
            case ManagedRecipeKind.Fish:
                RefreshFishPrefab(workItem.PrefabName, affectedItems);
                break;
            case ManagedRecipeKind.SpawnArea:
                RefreshSpawnAreaPrefab(workItem.PrefabName, affectedItems);
                break;
            case ManagedRecipeKind.Destructible:
                RefreshDestructiblePrefab(workItem.PrefabName, affectedItems);
                break;
            case ManagedRecipeKind.TreeBase:
                RefreshTreeBasePrefab(workItem.PrefabName, affectedItems);
                break;
        }
    }

    private static void PatchConstructor(Harmony harmony, Type[] argumentTypes, string postfixName)
    {
        if (_recipeInfoType == null)
        {
            return;
        }

        ConstructorInfo? constructor = AccessTools.Constructor(_recipeInfoType, argumentTypes);
        MethodInfo? postfix = AccessTools.Method(typeof(VneiCompatibility), postfixName);
        if (constructor == null || postfix == null)
        {
            return;
        }

        harmony.Patch(constructor, postfix: new HarmonyMethod(postfix));
    }

    private static void CharacterDropCtorPostfix(object __instance, [HarmonyArgument(0)] CharacterDrop characterDrop)
    {
        if (!_available || characterDrop == null || characterDrop.gameObject == null)
        {
            return;
        }

        ManagedRecipeKey key = new(characterDrop.gameObject.name, ManagedRecipeKind.CharacterDrop);
        ManagedRecipeBinding binding = RegisterManagedRecipe(key, __instance, characterDrop, isSupplemental: false, disableSourceItemWhenRemoved: false);
        if (!CharacterDropManager.HasVneiRelevantEntries(characterDrop.gameObject.name))
        {
            return;
        }

        if (CharacterDropManager.TryGetVneiDisplayResults(characterDrop, out List<VneiRecipeResult> results))
        {
            RewriteRecipeResults(__instance, results);
            binding.LastDisplayFingerprint = BuildResultsFingerprint(results);
        }
    }

    private static void DropTableCtorPostfix(
        object __instance,
        [HarmonyArgument(0)] GameObject from,
        [HarmonyArgument(1)] DropTable dropTable)
    {
        if (!_available || from == null || dropTable == null || !TryGetDropTableRecipeKey(from, dropTable, out ManagedRecipeKey key))
        {
            return;
        }

        ManagedRecipeBinding binding = RegisterManagedRecipe(key, __instance, from, isSupplemental: false, disableSourceItemWhenRemoved: false);
        if (!ShouldRewriteDropTableRecipe(key))
        {
            return;
        }

        if (ObjectDropManager.TryGetVneiDisplayForDropTable(from, dropTable, out List<VneiRecipeResult> results))
        {
            RewriteRecipeResults(__instance, results);
            binding.LastDisplayFingerprint = BuildResultsFingerprint(results);
        }
    }

    private static void PickableCtorPostfix(
        object __instance,
        [HarmonyArgument(0)] GameObject prefab,
        [HarmonyArgument(1)] Pickable pickable)
    {
        if (!_available || prefab == null || pickable == null)
        {
            return;
        }

        ManagedRecipeKey key = new(prefab.name, ManagedRecipeKind.Pickable);
        ManagedRecipeBinding binding = RegisterManagedRecipe(key, __instance, pickable, isSupplemental: false, disableSourceItemWhenRemoved: false);
        if (!ObjectDropManager.HasVneiRelevantPickableOverride(prefab.name))
        {
            return;
        }

        if (ObjectDropManager.TryGetVneiDisplayForPickable(prefab, pickable, out List<VneiRecipeResult> results))
        {
            RewriteRecipeResults(__instance, results);
            binding.LastDisplayFingerprint = BuildResultsFingerprint(results);
        }
    }

    private static void SpawnAreaCtorPostfix(object __instance, [HarmonyArgument(0)] SpawnArea spawnArea)
    {
        if (!_available || spawnArea == null || spawnArea.gameObject == null)
        {
            return;
        }

        ManagedRecipeKey key = new(spawnArea.gameObject.name, ManagedRecipeKind.SpawnArea);
        ManagedRecipeBinding binding = RegisterManagedRecipe(key, __instance, spawnArea, isSupplemental: false, disableSourceItemWhenRemoved: false);
        if (!SpawnerManager.HasVneiRelevantSpawnAreaEntries(spawnArea.gameObject.name))
        {
            return;
        }

        if (SpawnerManager.TryGetVneiDisplayForSpawnArea(spawnArea, out List<VneiRecipeResult> results))
        {
            RewriteRecipeResults(__instance, results);
            binding.LastDisplayFingerprint = BuildResultsFingerprint(results);
        }
    }

    private static void DestructibleCtorPostfix(object __instance, [HarmonyArgument(0)] Destructible destructible)
    {
        if (!_available || destructible == null || destructible.gameObject == null)
        {
            return;
        }

        ManagedRecipeKey key = new(destructible.gameObject.name, ManagedRecipeKind.Destructible);
        ManagedRecipeBinding binding = RegisterManagedRecipe(key, __instance, destructible, isSupplemental: false, disableSourceItemWhenRemoved: false);
        if (!ObjectDropManager.HasVneiRelevantDestructibleOverride(destructible.gameObject.name))
        {
            return;
        }

        if (ObjectDropManager.TryGetVneiDisplayForDestructible(destructible, out List<VneiRecipeResult> results))
        {
            RewriteRecipeResults(__instance, results);
            binding.LastDisplayFingerprint = BuildResultsFingerprint(results);
        }
    }

    private static void TreeBaseCtorPostfix(object __instance, [HarmonyArgument(0)] TreeBase treeBase)
    {
        if (!_available || treeBase == null || treeBase.gameObject == null)
        {
            return;
        }

        ManagedRecipeKey key = new(treeBase.gameObject.name, ManagedRecipeKind.TreeBase);
        ManagedRecipeBinding binding = RegisterManagedRecipe(key, __instance, treeBase, isSupplemental: false, disableSourceItemWhenRemoved: false);
        if (!ObjectDropManager.HasVneiRelevantTreeBaseOverride(treeBase.gameObject.name))
        {
            return;
        }

        if (ObjectDropManager.TryGetVneiDisplayForTreeBase(treeBase, out List<VneiRecipeResult> results))
        {
            RewriteRecipeResults(__instance, results);
            binding.LastDisplayFingerprint = BuildResultsFingerprint(results);
        }
    }

    private static void HandleIndexingItemRecipes(GameObject prefab)
    {
        try
        {
            TryAddMissingContainerRecipe(prefab);
            TryAddMissingDestructibleRecipe(prefab);
            TryAddMissingFishRecipe(prefab);
        }
        catch (Exception ex)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"VNEI compatibility failed while indexing '{prefab?.name}': {ex.Message}");
        }
    }

    private static void TryAddMissingContainerRecipe(GameObject prefab)
    {
        if (!ShouldUseSupplementalContainerRecipe(prefab, out List<VneiRecipeResult> results))
        {
            return;
        }

        CreateAndRegisterSupplementalRecipe(
            new ManagedRecipeKey(prefab.name, ManagedRecipeKind.Container),
            prefab,
            results,
            enableSourceItem: false,
            disableSourceItemWhenRemoved: false);
    }

    private static bool ShouldUseSupplementalContainerRecipe(GameObject? prefab, out List<VneiRecipeResult> results)
    {
        results = new List<VneiRecipeResult>();
        return prefab != null &&
               prefab.TryGetComponent(out Piece _) &&
               prefab.TryGetComponent(out Container container) &&
               container.m_defaultItems != null &&
               container.m_defaultItems.m_drops.Count == 0 &&
               ObjectDropManager.TryGetVneiDisplayForContainer(prefab, out results) &&
               results.Count > 0;
    }

    private static void TryAddMissingDestructibleRecipe(GameObject prefab)
    {
        if (!ShouldUseSupplementalDestructibleRecipe(prefab, out Destructible? destructible, out List<VneiRecipeResult> results))
        {
            return;
        }

        CreateAndRegisterSupplementalRecipe(
            new ManagedRecipeKey(prefab.name, ManagedRecipeKind.Destructible),
            destructible!,
            results,
            enableSourceItem: true,
            disableSourceItemWhenRemoved: true);
    }

    private static bool ShouldUseSupplementalDestructibleRecipe(GameObject? prefab, out Destructible? destructible, out List<VneiRecipeResult> results)
    {
        destructible = null;
        results = new List<VneiRecipeResult>();
        return prefab != null &&
               prefab.TryGetComponent(out destructible) &&
               prefab.GetComponent<DropOnDestroyed>() == null &&
               prefab.GetComponent<Plant>() == null &&
               (destructible != null && (destructible.m_spawnWhenDestroyed == null || TryResolveVneiItem(destructible.m_spawnWhenDestroyed.name) == null)) &&
               ObjectDropManager.TryGetVneiDisplayForDestructible(destructible!, out results) &&
               results.Count > 0;
    }

    private static void TryAddMissingFishRecipe(GameObject prefab)
    {
        ManagedRecipeKey key = new(prefab.name, ManagedRecipeKind.Fish);
        if (ManagedRecipesByKey.ContainsKey(key) ||
            !ShouldUseSupplementalFishRecipe(prefab, out Fish? fish, out List<VneiRecipeResult> results))
        {
            return;
        }

        CreateAndRegisterSupplementalRecipe(
            key,
            fish!,
            results,
            enableSourceItem: true,
            disableSourceItemWhenRemoved: false);
    }

    private static bool ShouldUseSupplementalFishRecipe(GameObject? prefab, out Fish? fish, out List<VneiRecipeResult> results)
    {
        fish = null;
        results = new List<VneiRecipeResult>();
        return prefab != null &&
               prefab.TryGetComponent(out fish) &&
               ObjectDropManager.TryGetVneiDisplayForFish(fish!, out results) &&
               results.Count > 0;
    }

    private static void RefreshCharacterPrefab(string prefabName, HashSet<object> affectedItems)
    {
        ManagedRecipeKey key = new(prefabName, ManagedRecipeKind.CharacterDrop);
        if (!ManagedRecipesByKey.TryGetValue(key, out ManagedRecipeBinding? binding) ||
            binding.Source is not CharacterDrop characterDrop)
        {
            return;
        }

        if (!CharacterDropManager.TryGetVneiDisplayResults(characterDrop, out List<VneiRecipeResult> results))
        {
            return;
        }

        RefreshManagedRecipe(key, binding, results, affectedItems);
    }

    private static void RefreshSpawnAreaPrefab(string prefabName, HashSet<object> affectedItems)
    {
        ManagedRecipeKey key = new(prefabName, ManagedRecipeKind.SpawnArea);
        if (!ManagedRecipesByKey.TryGetValue(key, out ManagedRecipeBinding? binding) ||
            binding.Source is not SpawnArea spawnArea ||
            !SpawnerManager.TryGetVneiDisplayForSpawnArea(spawnArea, out List<VneiRecipeResult> results))
        {
            return;
        }

        RefreshManagedRecipe(key, binding, results, affectedItems);
    }

    private static void RefreshContainerPrefab(string prefabName, HashSet<object> affectedItems)
    {
        ManagedRecipeKey key = new(prefabName, ManagedRecipeKind.Container);
        GameObject? prefab = ResolvePrefab(prefabName);
        if (ManagedRecipesByKey.TryGetValue(key, out ManagedRecipeBinding? binding))
        {
            if (prefab != null && ObjectDropManager.TryGetVneiDisplayForContainer(prefab, out List<VneiRecipeResult> results))
            {
                RefreshManagedRecipe(key, binding, results, affectedItems);
            }
            else if (binding.IsSupplemental)
            {
                RemoveManagedRecipe(key, binding, affectedItems);
            }

            return;
        }

        if (ShouldUseSupplementalContainerRecipe(prefab, out List<VneiRecipeResult> supplementalResults))
        {
            CreateAndRegisterSupplementalRecipe(key, prefab!, supplementalResults, enableSourceItem: false, disableSourceItemWhenRemoved: false, affectedItems);
        }
    }

    private static void RefreshDropTablePrefab(string prefabName, ManagedRecipeKind kind, HashSet<object> affectedItems)
    {
        ManagedRecipeKey key = new(prefabName, kind);
        if (!ManagedRecipesByKey.TryGetValue(key, out ManagedRecipeBinding? binding) ||
            binding.Source is not GameObject prefab ||
            !TryGetDropTableForKind(prefab, kind, out DropTable? dropTable) ||
            dropTable == null ||
            !ObjectDropManager.TryGetVneiDisplayForDropTable(prefab, dropTable, out List<VneiRecipeResult> results))
        {
            return;
        }

        RefreshManagedRecipe(key, binding, results, affectedItems);
    }

    private static void RefreshPickablePrefab(string prefabName, HashSet<object> affectedItems)
    {
        ManagedRecipeKey key = new(prefabName, ManagedRecipeKind.Pickable);
        if (!ManagedRecipesByKey.TryGetValue(key, out ManagedRecipeBinding? binding) ||
            binding.Source is not Pickable pickable ||
            !ObjectDropManager.TryGetVneiDisplayForPickable(pickable.gameObject, pickable, out List<VneiRecipeResult> results))
        {
            return;
        }

        RefreshManagedRecipe(key, binding, results, affectedItems);
    }

    private static void RefreshFishPrefab(string prefabName, HashSet<object> affectedItems)
    {
        ManagedRecipeKey key = new(prefabName, ManagedRecipeKind.Fish);
        GameObject? prefab = ResolvePrefab(prefabName);
        if (ManagedRecipesByKey.TryGetValue(key, out ManagedRecipeBinding? binding))
        {
            Fish? fish = binding.Source as Fish ?? prefab?.GetComponent<Fish>();
            if (fish != null && ObjectDropManager.TryGetVneiDisplayForFish(fish, out List<VneiRecipeResult> results))
            {
                RefreshManagedRecipe(key, binding, results, affectedItems);
            }
            else if (binding.IsSupplemental)
            {
                RemoveManagedRecipe(key, binding, affectedItems);
            }

            return;
        }

        if (ShouldUseSupplementalFishRecipe(prefab, out Fish? fishComponent, out List<VneiRecipeResult> supplementalResults))
        {
            CreateAndRegisterSupplementalRecipe(key, fishComponent!, supplementalResults, enableSourceItem: true, disableSourceItemWhenRemoved: false, affectedItems);
        }
    }

    private static void RefreshDestructiblePrefab(string prefabName, HashSet<object> affectedItems)
    {
        ManagedRecipeKey key = new(prefabName, ManagedRecipeKind.Destructible);
        GameObject? prefab = ResolvePrefab(prefabName);
        if (ManagedRecipesByKey.TryGetValue(key, out ManagedRecipeBinding? binding))
        {
            if (binding.Source is Destructible destructible &&
                ObjectDropManager.TryGetVneiDisplayForDestructible(destructible, out List<VneiRecipeResult> results))
            {
                RefreshManagedRecipe(key, binding, results, affectedItems);
            }
            else if (binding.IsSupplemental)
            {
                RemoveManagedRecipe(key, binding, affectedItems);
            }

            return;
        }

        if (ShouldUseSupplementalDestructibleRecipe(prefab, out Destructible? destructibleComponent, out List<VneiRecipeResult> supplementalResults))
        {
            CreateAndRegisterSupplementalRecipe(key, destructibleComponent!, supplementalResults, enableSourceItem: true, disableSourceItemWhenRemoved: true, affectedItems);
        }
    }

    private static void RefreshTreeBasePrefab(string prefabName, HashSet<object> affectedItems)
    {
        ManagedRecipeKey key = new(prefabName, ManagedRecipeKind.TreeBase);
        if (!ManagedRecipesByKey.TryGetValue(key, out ManagedRecipeBinding? binding) ||
            binding.Source is not TreeBase treeBase ||
            !ObjectDropManager.TryGetVneiDisplayForTreeBase(treeBase, out List<VneiRecipeResult> results))
        {
            return;
        }

        RefreshManagedRecipe(key, binding, results, affectedItems);
    }

    private static void RefreshManagedRecipe(ManagedRecipeKey key, ManagedRecipeBinding binding, List<VneiRecipeResult> results, HashSet<object> affectedItems)
    {
        if (binding.IsSupplemental && results.Count == 0)
        {
            RemoveManagedRecipe(key, binding, affectedItems);
            return;
        }

        string fingerprint = BuildResultsFingerprint(results);
        if (string.Equals(binding.LastDisplayFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        affectedItems.UnionWith(CollectRecipeItems(binding.Recipe));
        DetachRecipeFromItems(binding.Recipe, removeFromRecipesList: false);
        RewriteRecipeResults(binding.Recipe, results);
        ResetAndRecalculateRecipe(binding.Recipe);
        _addRecipeToItemsMethod?.Invoke(null, new[] { binding.Recipe });
        affectedItems.UnionWith(CollectRecipeItems(binding.Recipe));
        binding.LastDisplayFingerprint = fingerprint;

        if (binding.IsSupplemental && binding.DisableSourceItemWhenRemoved)
        {
            SetVneiItemActive(key.PrefabName, true);
        }
    }

    private static void RemoveManagedRecipe(ManagedRecipeKey key, ManagedRecipeBinding binding, HashSet<object> affectedItems)
    {
        affectedItems.UnionWith(CollectRecipeItems(binding.Recipe));
        DetachRecipeFromItems(binding.Recipe, removeFromRecipesList: true);
        ManagedRecipesByKey.Remove(key);

        if (binding.IsSupplemental && binding.DisableSourceItemWhenRemoved)
        {
            SetVneiItemActive(key.PrefabName, false);
        }
    }

    private static ManagedRecipeBinding RegisterManagedRecipe(
        ManagedRecipeKey key,
        object recipe,
        UnityEngine.Object? source,
        bool isSupplemental,
        bool disableSourceItemWhenRemoved)
    {
        ManagedRecipeBinding binding = new()
        {
            Recipe = recipe,
            Source = source,
            IsSupplemental = isSupplemental,
            DisableSourceItemWhenRemoved = disableSourceItemWhenRemoved
        };
        ManagedRecipesByKey[key] = binding;
        return binding;
    }

    private static void CreateAndRegisterSupplementalRecipe(
        ManagedRecipeKey key,
        UnityEngine.Object source,
        List<VneiRecipeResult> results,
        bool enableSourceItem,
        bool disableSourceItemWhenRemoved,
        HashSet<object>? affectedItems = null)
    {
        if (_emptyRecipeCtor == null || _addIngredientMethod == null || _addRecipeToItemsMethod == null)
        {
            return;
        }

        object recipe = _emptyRecipeCtor.Invoke(Array.Empty<object>());
        AddIngredient(recipe, source.name, 1, 1, 1f, 1, 1, 1f);
        RewriteRecipeResults(recipe, results);
        ResetAndRecalculateRecipe(recipe);
        _addRecipeToItemsMethod.Invoke(null, new[] { recipe });
        HashSet<object> createdItems = CollectRecipeItems(recipe);
        if (affectedItems != null)
        {
            affectedItems.UnionWith(createdItems);
        }
        else
        {
            UpdateKnownForItems(createdItems);
        }
        ManagedRecipeBinding binding = RegisterManagedRecipe(key, recipe, source, isSupplemental: true, disableSourceItemWhenRemoved);
        binding.LastDisplayFingerprint = BuildResultsFingerprint(results);
        if (enableSourceItem)
        {
            SetVneiItemActive(source.name, true);
        }
    }

    private static bool ShouldRewriteDropTableRecipe(ManagedRecipeKey key)
    {
        return key.Kind switch
        {
            ManagedRecipeKind.Container => ObjectDropManager.HasVneiRelevantContainerOverride(key.PrefabName),
            ManagedRecipeKind.DropOnDestroyed => ObjectDropManager.HasVneiRelevantDropOnDestroyedOverride(key.PrefabName),
            ManagedRecipeKind.MineRock => ObjectDropManager.HasVneiRelevantMineRockOverride(key.PrefabName),
            ManagedRecipeKind.MineRock5 => ObjectDropManager.HasVneiRelevantMineRock5Override(key.PrefabName),
            ManagedRecipeKind.Fish => ObjectDropManager.HasVneiRelevantFishOverride(key.PrefabName),
            _ => false
        };
    }

    private static bool TryGetDropTableRecipeKey(GameObject from, DropTable dropTable, out ManagedRecipeKey key)
    {
        if (from.TryGetComponent(out Container container) && ReferenceEquals(container.m_defaultItems, dropTable))
        {
            key = new ManagedRecipeKey(from.name, ManagedRecipeKind.Container);
            return true;
        }

        if (from.TryGetComponent(out DropOnDestroyed dropOnDestroyed) && ReferenceEquals(dropOnDestroyed.m_dropWhenDestroyed, dropTable))
        {
            key = new ManagedRecipeKey(from.name, ManagedRecipeKind.DropOnDestroyed);
            return true;
        }

        if (from.TryGetComponent(out MineRock mineRock) && ReferenceEquals(mineRock.m_dropItems, dropTable))
        {
            key = new ManagedRecipeKey(from.name, ManagedRecipeKind.MineRock);
            return true;
        }

        if (from.TryGetComponent(out MineRock5 mineRock5) && ReferenceEquals(mineRock5.m_dropItems, dropTable))
        {
            key = new ManagedRecipeKey(from.name, ManagedRecipeKind.MineRock5);
            return true;
        }

        if (from.TryGetComponent(out Fish fish) && ReferenceEquals(fish.m_extraDrops, dropTable))
        {
            key = new ManagedRecipeKey(from.name, ManagedRecipeKind.Fish);
            return true;
        }

        key = default;
        return false;
    }

    private static bool TryGetDropTableForKind(GameObject prefab, ManagedRecipeKind kind, out DropTable? dropTable)
    {
        dropTable = null;
        switch (kind)
        {
            case ManagedRecipeKind.Container:
                if (prefab.TryGetComponent(out Container container))
                {
                    dropTable = container.m_defaultItems;
                    return true;
                }

                return false;
            case ManagedRecipeKind.DropOnDestroyed:
                if (prefab.TryGetComponent(out DropOnDestroyed dropOnDestroyed))
                {
                    dropTable = dropOnDestroyed.m_dropWhenDestroyed;
                    return true;
                }

                return false;
            case ManagedRecipeKind.MineRock:
                if (prefab.TryGetComponent(out MineRock mineRock))
                {
                    dropTable = mineRock.m_dropItems;
                    return true;
                }

                return false;
            case ManagedRecipeKind.MineRock5:
                if (prefab.TryGetComponent(out MineRock5 mineRock5))
                {
                    dropTable = mineRock5.m_dropItems;
                    return true;
                }

                return false;
            case ManagedRecipeKind.Fish:
                if (prefab.TryGetComponent(out Fish fish))
                {
                    dropTable = fish.m_extraDrops;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static GameObject? ResolvePrefab(string prefabName)
    {
        return ZNetScene.instance?.GetPrefab(prefabName);
    }

    private static void RewriteRecipeResults(object recipe, List<VneiRecipeResult> results)
    {
        if (_resultsProperty?.GetValue(recipe) is not IDictionary resultGroups)
        {
            return;
        }

        resultGroups.Clear();
        foreach (VneiRecipeResult result in results)
        {
            AddResult(recipe, result.PrefabName, result.GroupMin, result.GroupMax, result.GroupChance, result.Min, result.Max, result.Chance);
        }

        _combineGroupAmountsMethod?.Invoke(recipe, new[] { resultGroups });
    }

    private static void ResetAndRecalculateRecipe(object recipe)
    {
        _recipeIsOnBlacklistBackingField?.SetValue(recipe, false);
        _recipeCalculateIsOnBlacklistMethod?.Invoke(recipe, Array.Empty<object>());
        _recipeCalculateWidthMethod?.Invoke(recipe, Array.Empty<object>());
        _recipeUpdateKnownMethod?.Invoke(recipe, Array.Empty<object>());
    }

    private static HashSet<object> CollectRecipeItems(object recipe)
    {
        HashSet<object> items = new();
        foreach (object item in EnumerateRecipeItems(recipe))
        {
            items.Add(item);
        }

        return items;
    }

    private static IEnumerable<object> EnumerateRecipeItems(object recipe)
    {
        foreach (object item in EnumerateItemsFromGroupDictionary(_ingredientsProperty?.GetValue(recipe) as IDictionary))
        {
            yield return item;
        }

        foreach (object item in EnumerateItemsFromGroupDictionary(_resultsProperty?.GetValue(recipe) as IDictionary))
        {
            yield return item;
        }

        if (_stationsProperty?.GetValue(recipe) is IEnumerable stations)
        {
            foreach (object? part in stations)
            {
                object? item = part != null ? _partItemField?.GetValue(part) : null;
                if (item != null)
                {
                    yield return item;
                }
            }
        }
    }

    private static IEnumerable<object> EnumerateItemsFromGroupDictionary(IDictionary? groups)
    {
        if (groups == null)
        {
            yield break;
        }

        foreach (DictionaryEntry group in groups)
        {
            if (group.Value is not IEnumerable parts)
            {
                continue;
            }

            foreach (object? part in parts)
            {
                object? item = part != null ? _partItemField?.GetValue(part) : null;
                if (item != null)
                {
                    yield return item;
                }
            }
        }
    }

    private static void DetachRecipeFromItems(object recipe, bool removeFromRecipesList)
    {
        if (_itemResultField == null || _itemIngredientField == null)
        {
            return;
        }

        if (_ingredientsProperty?.GetValue(recipe) is IDictionary ingredients)
        {
            foreach (DictionaryEntry group in ingredients)
            {
                if (group.Value is not IEnumerable parts)
                {
                    continue;
                }

                foreach (object? part in parts)
                {
                    object? item = part != null ? _partItemField?.GetValue(part) : null;
                    if (item != null)
                    {
                        RemoveRecipeReference(_itemIngredientField.GetValue(item), recipe);
                    }
                }
            }
        }

        if (_resultsProperty?.GetValue(recipe) is IDictionary results)
        {
            foreach (DictionaryEntry group in results)
            {
                if (group.Value is not IEnumerable parts)
                {
                    continue;
                }

                foreach (object? part in parts)
                {
                    object? item = part != null ? _partItemField?.GetValue(part) : null;
                    if (item != null)
                    {
                        RemoveRecipeReference(_itemResultField.GetValue(item), recipe);
                    }
                }
            }
        }

        if (_stationsProperty?.GetValue(recipe) is IEnumerable stations)
        {
            foreach (object? part in stations)
            {
                object? item = part != null ? _partItemField?.GetValue(part) : null;
                if (item != null)
                {
                    RemoveRecipeReference(_itemIngredientField.GetValue(item), recipe);
                }
            }
        }

        if (removeFromRecipesList && _recipesProperty?.GetValue(null) is IList recipes)
        {
            recipes.Remove(recipe);
        }
    }

    private static void UpdateKnownForItems(IEnumerable<object> items)
    {
        foreach (object item in items.Distinct())
        {
            _itemUpdateKnownMethod?.Invoke(item, Array.Empty<object>());
        }
    }

    private static void RemoveRecipeReference(object? collection, object recipe)
    {
        if (collection == null)
        {
            return;
        }

        MethodInfo? removeMethod = collection.GetType().GetMethod("Remove", new[] { recipe.GetType() });
        removeMethod?.Invoke(collection, new[] { recipe });
    }

    private static void AddIngredient(object recipe, string prefabName, int groupMin, int groupMax, float groupChance, int min, int max, float chance)
    {
        if (_addIngredientMethod == null)
        {
            return;
        }

        _addIngredientMethod.Invoke(recipe, new[]
        {
            prefabName,
            CreateAmount(groupMin, groupMax, groupChance),
            CreateAmount(min, max, chance),
            1
        });
    }

    private static void AddResult(object recipe, string prefabName, int groupMin, int groupMax, float groupChance, int min, int max, float chance)
    {
        if (_addResultMethod == null)
        {
            return;
        }

        _addResultMethod.Invoke(recipe, new[]
        {
            prefabName,
            CreateAmount(groupMin, groupMax, groupChance),
            CreateAmount(min, max, chance),
            1
        });
    }

    private static object CreateAmount(int min, int max, float chance)
    {
        if (_singleAmountCtor == null || _rangeAmountCtor == null)
        {
            throw new InvalidOperationException("VNEI Amount constructors are unavailable.");
        }

        if (min == max)
        {
            return _singleAmountCtor.Invoke(new object[] { min, chance });
        }

        return _rangeAmountCtor.Invoke(new object[] { min, max, chance });
    }

    private static object? TryResolveVneiItem(string prefabName)
    {
        return _getItemMethod?.Invoke(null, new object[] { prefabName });
    }

    private static void SetVneiItemActive(string prefabName, bool active)
    {
        object? item = TryResolveVneiItem(prefabName);
        if (item != null && _itemIsActiveField != null)
        {
            _itemIsActiveField.SetValue(item, active);
        }
    }

    private static string BuildResultsFingerprint(List<VneiRecipeResult> results)
    {
        if (results == null || results.Count == 0)
        {
            return "";
        }

        StringBuilder builder = new();
        foreach (VneiRecipeResult result in results)
        {
            builder.Append(result.PrefabName ?? "").Append('\n')
                .Append(result.GroupMin).Append('\n')
                .Append(result.GroupMax).Append('\n')
                .Append(result.GroupChance.ToString("R")).Append('\n')
                .Append(result.Min).Append('\n')
                .Append(result.Max).Append('\n')
                .Append(result.Chance.ToString("R")).Append('\n');
        }

        return builder.ToString();
    }
}

internal readonly struct VneiRecipeResult
{
    public readonly string PrefabName;
    public readonly int GroupMin;
    public readonly int GroupMax;
    public readonly float GroupChance;
    public readonly int Min;
    public readonly int Max;
    public readonly float Chance;

    public VneiRecipeResult(string prefabName, int groupMin, int groupMax, float groupChance, int min, int max, float chance)
    {
        PrefabName = prefabName;
        GroupMin = groupMin;
        GroupMax = groupMax;
        GroupChance = groupChance;
        Min = min;
        Max = max;
        Chance = chance;
    }
}
