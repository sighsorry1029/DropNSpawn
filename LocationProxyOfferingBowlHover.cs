using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static class OfferingBowlHoverInfoFormatter
{
    private static readonly OfferingBowl[] EmptyOfferingBowls = Array.Empty<OfferingBowl>();

    private sealed class HoverInfoCacheEntry
    {
        public int RegistryVersion { get; set; }
        public float ExpiresAt { get; set; }
        public string Info { get; set; } = "";
    }

    private static readonly List<OfferingBowl> RegisteredOfferingBowls = new();
    private static readonly HashSet<int> RegisteredOfferingBowlIds = new();
    private static readonly Dictionary<int, HoverInfoCacheEntry> HoverInfoCacheByOfferingBowl = new();
    private const float HoverInfoCacheLifetimeSeconds = 1f;

    internal static string AppendInfo(string baseText, OfferingBowl? offeringBowl)
    {
        if (!PluginSettingsFacade.ShouldShowLocationProxyOfferingBowlHoverInfo() || offeringBowl == null)
        {
            return baseText ?? "";
        }

        string info = GetCachedInfo(offeringBowl);
        if (info.Length == 0)
        {
            return baseText ?? "";
        }

        if (string.IsNullOrWhiteSpace(baseText))
        {
            return info;
        }

        return $"{baseText}\n{info}";
    }

    internal static void RegisterOfferingBowl(OfferingBowl? offeringBowl)
    {
        if (offeringBowl == null)
        {
            return;
        }

        if (RegisteredOfferingBowlIds.Add(offeringBowl.GetInstanceID()))
        {
            RegisteredOfferingBowls.Add(offeringBowl);
        }
    }

    internal static IReadOnlyList<OfferingBowl> GetKnownOfferingBowls()
    {
        CleanupRegisteredOfferingBowls();
        EnsureOfferingBowlRegistryPopulated();
        return RegisteredOfferingBowls.Count == 0 ? EmptyOfferingBowls : RegisteredOfferingBowls;
    }

    private static string GetCachedInfo(OfferingBowl offeringBowl)
    {
        float now = Time.unscaledTime;
        CleanupExpiredHoverInfoCache(now);
        int offeringBowlId = offeringBowl.GetInstanceID();
        int registryVersion = AltarItemStandHoverInfoFormatter.GetRegistryVersion();
        if (HoverInfoCacheByOfferingBowl.TryGetValue(offeringBowlId, out HoverInfoCacheEntry? cachedEntry) &&
            cachedEntry.RegistryVersion == registryVersion &&
            cachedEntry.ExpiresAt >= now)
        {
            return cachedEntry.Info;
        }

        string info = BuildInfo(offeringBowl);
        HoverInfoCacheByOfferingBowl[offeringBowlId] = new HoverInfoCacheEntry
        {
            RegistryVersion = registryVersion,
            ExpiresAt = now + HoverInfoCacheLifetimeSeconds,
            Info = info
        };
        return info;
    }

    private static string BuildInfo(OfferingBowl offeringBowl)
    {
        string spawnedText = GetSpawnedText(offeringBowl);
        string requiredText = GetRequiredText(offeringBowl);

        if (spawnedText.Length == 0)
        {
            return requiredText;
        }

        if (requiredText.Length == 0)
        {
            return spawnedText;
        }

        return $"{spawnedText}\n{requiredText}";
    }

    private static string GetSpawnedText(OfferingBowl offeringBowl)
    {
        if (offeringBowl.m_bossPrefab != null)
        {
            return GetCharacterDisplayName(offeringBowl.m_bossPrefab);
        }

        if (offeringBowl.m_itemPrefab != null)
        {
            return GetItemDisplayName(offeringBowl.m_itemPrefab);
        }

        return "";
    }

    private static string GetRequiredText(OfferingBowl offeringBowl)
    {
        if (offeringBowl.m_useItemStands)
        {
            return GetRequiredTextFromItemStands(offeringBowl);
        }

        if (offeringBowl.m_bossItem == null)
        {
            return "";
        }

        string itemName = GetItemDisplayName(offeringBowl.m_bossItem);
        if (itemName.Length == 0)
        {
            return "";
        }

        int amount = Math.Max(1, offeringBowl.m_bossItems);
        return amount > 1 ? $"{itemName} x{amount}" : itemName;
    }

    private static string GetRequiredTextFromItemStands(OfferingBowl offeringBowl)
    {
        Dictionary<string, int> countsByName = new(StringComparer.Ordinal);

        foreach (ItemStand itemStand in AltarItemStandHoverInfoFormatter.GetDisplayRelevantItemStands(offeringBowl))
        {
            if (itemStand == null || itemStand.m_supportedItems == null || itemStand.m_supportedItems.Count != 1)
            {
                continue;
            }

            string itemName = GetItemDisplayName(itemStand.m_supportedItems[0]);
            if (itemName.Length == 0)
            {
                continue;
            }

            countsByName[itemName] = countsByName.TryGetValue(itemName, out int currentCount)
                ? currentCount + 1
                : 1;
        }

        if (countsByName.Count == 0)
        {
            return "";
        }

        return string.Join(", ", countsByName.Select(pair => pair.Value > 1 ? $"{pair.Key} x{pair.Value}" : pair.Key));
    }

    private static string GetCharacterDisplayName(GameObject prefab)
    {
        if (!prefab.TryGetComponent(out Character character))
        {
            return Localize(prefab.name);
        }

        return Localize(string.IsNullOrWhiteSpace(character.m_name) ? prefab.name : character.m_name);
    }

    private static string GetItemDisplayName(ItemDrop itemDrop)
    {
        return Localize(itemDrop.m_itemData.m_shared.m_name);
    }

    private static string Localize(string text)
    {
        return Localization.instance != null ? Localization.instance.Localize(text ?? "") : (text ?? "");
    }

    private static void EnsureOfferingBowlRegistryPopulated()
    {
        if (RegisteredOfferingBowls.Count > 0)
        {
            return;
        }

        foreach (OfferingBowl offeringBowl in UnityEngine.Object.FindObjectsByType<OfferingBowl>(FindObjectsSortMode.None))
        {
            RegisterOfferingBowl(offeringBowl);
        }
    }

    private static void CleanupRegisteredOfferingBowls()
    {
        bool removedAny = false;
        for (int index = RegisteredOfferingBowls.Count - 1; index >= 0; index--)
        {
            OfferingBowl offeringBowl = RegisteredOfferingBowls[index];
            if (offeringBowl != null)
            {
                continue;
            }

            RegisteredOfferingBowls.RemoveAt(index);
            removedAny = true;
        }

        if (!removedAny)
        {
            return;
        }

        RegisteredOfferingBowls.RemoveAll(offeringBowl => offeringBowl == null);
        RegisteredOfferingBowlIds.Clear();
        foreach (OfferingBowl registeredOfferingBowl in RegisteredOfferingBowls)
        {
            if (registeredOfferingBowl != null)
            {
                RegisteredOfferingBowlIds.Add(registeredOfferingBowl.GetInstanceID());
            }
        }
    }

    private static void CleanupExpiredHoverInfoCache(float now)
    {
        if (HoverInfoCacheByOfferingBowl.Count == 0)
        {
            return;
        }

        List<int>? expiredIds = null;
        int registryVersion = AltarItemStandHoverInfoFormatter.GetRegistryVersion();
        foreach ((int offeringBowlId, HoverInfoCacheEntry entry) in HoverInfoCacheByOfferingBowl)
        {
            if (entry.RegistryVersion == registryVersion &&
                entry.ExpiresAt >= now)
            {
                continue;
            }

            expiredIds ??= new List<int>();
            expiredIds.Add(offeringBowlId);
        }

        if (expiredIds == null)
        {
            return;
        }

        foreach (int offeringBowlId in expiredIds)
        {
            HoverInfoCacheByOfferingBowl.Remove(offeringBowlId);
        }
    }
}

internal static class AltarItemStandHoverInfoFormatter
{
    private static readonly ItemStand[] EmptyItemStands = Array.Empty<ItemStand>();

    private sealed class RelevantOfferingBowlCacheEntry
    {
        public int RegistryVersion { get; set; }
        public float ExpiresAt { get; set; }
        public OfferingBowl? OfferingBowl { get; set; }
    }

    private sealed class RelevantItemStandCacheEntry
    {
        public int RegistryVersion { get; set; }
        public float ExpiresAt { get; set; }
        public IReadOnlyList<ItemStand> ItemStands { get; set; } = EmptyItemStands;
        public IReadOnlyList<ItemStand> DisplayItemStands { get; set; } = EmptyItemStands;
    }

    private static readonly Dictionary<int, RelevantOfferingBowlCacheEntry> RelevantOfferingBowlCacheByItemStand = new();
    private static readonly Dictionary<int, RelevantItemStandCacheEntry> RelevantItemStandCacheByOfferingBowl = new();
    private static readonly List<ItemStand> RegisteredItemStands = new();
    private static readonly HashSet<int> RegisteredItemStandIds = new();
    private const float RelevantItemStandCacheLifetimeSeconds = 1f;
    private const float RelevantOfferingBowlCacheLifetimeSeconds = 1f;
    private static int _itemStandRegistryVersion;

    internal static int GetRegistryVersion()
    {
        return _itemStandRegistryVersion;
    }

    internal static string AppendInfo(string baseText, ItemStand? itemStand)
    {
        if (!PluginSettingsFacade.ShouldShowLocationProxyOfferingBowlHoverInfo() || itemStand == null)
        {
            return baseText ?? "";
        }

        if (!TryGetRelevantOfferingBowl(itemStand, out _))
        {
            return baseText ?? "";
        }

        string info = BuildInfo(itemStand);
        if (info.Length == 0)
        {
            return baseText ?? "";
        }

        if (string.IsNullOrWhiteSpace(baseText))
        {
            return info;
        }

        return $"{baseText}\n{info}";
    }

    internal static void RegisterItemStand(ItemStand? itemStand)
    {
        if (itemStand == null)
        {
            return;
        }

        if (RegisteredItemStandIds.Add(itemStand.GetInstanceID()))
        {
            RegisteredItemStands.Add(itemStand);
            _itemStandRegistryVersion++;
        }
    }

    internal static IReadOnlyList<ItemStand> FindRelevantItemStands(OfferingBowl offeringBowl)
    {
        if (offeringBowl == null || !offeringBowl.m_useItemStands)
        {
            return EmptyItemStands;
        }

        CleanupRegisteredItemStands();
        float now = Time.unscaledTime;
        CleanupExpiredRelevantItemStandCache(now);
        int offeringBowlId = offeringBowl.GetInstanceID();
        if (RelevantItemStandCacheByOfferingBowl.TryGetValue(offeringBowlId, out RelevantItemStandCacheEntry? cachedEntry) &&
            cachedEntry.RegistryVersion == _itemStandRegistryVersion &&
            cachedEntry.ExpiresAt >= now)
        {
            return cachedEntry.ItemStands;
        }

        EnsureItemStandRegistryPopulated();

        List<ItemStand> itemStands = new();
        HashSet<int> seenIds = new();
        if (TryGetOfferingBowlStructuralRoot(offeringBowl, out Transform? structuralRoot) && structuralRoot != null)
        {
            AddRelevantItemStands(structuralRoot, offeringBowl, itemStands, seenIds);
        }

        foreach (ItemStand itemStand in RegisteredItemStands)
        {
            if (itemStand == null || !seenIds.Add(itemStand.GetInstanceID()) || !IsRelevantToOfferingBowl(itemStand, offeringBowl))
            {
                continue;
            }

            itemStands.Add(itemStand);
        }

        IReadOnlyList<ItemStand> displayItemStands = BuildDisplayRelevantItemStands(itemStands);
        RelevantItemStandCacheByOfferingBowl[offeringBowlId] = new RelevantItemStandCacheEntry
        {
            RegistryVersion = _itemStandRegistryVersion,
            ExpiresAt = now + RelevantItemStandCacheLifetimeSeconds,
            ItemStands = itemStands.Count == 0 ? EmptyItemStands : itemStands,
            DisplayItemStands = displayItemStands
        };

        return itemStands.Count == 0 ? EmptyItemStands : itemStands;
    }

    internal static IReadOnlyList<ItemStand> GetDisplayRelevantItemStands(OfferingBowl offeringBowl)
    {
        if (offeringBowl == null || !offeringBowl.m_useItemStands)
        {
            return EmptyItemStands;
        }

        CleanupRegisteredItemStands();
        float now = Time.unscaledTime;
        CleanupExpiredRelevantItemStandCache(now);
        int offeringBowlId = offeringBowl.GetInstanceID();
        if (RelevantItemStandCacheByOfferingBowl.TryGetValue(offeringBowlId, out RelevantItemStandCacheEntry? cachedEntry) &&
            cachedEntry.RegistryVersion == _itemStandRegistryVersion &&
            cachedEntry.ExpiresAt >= now)
        {
            return cachedEntry.DisplayItemStands;
        }

        IReadOnlyList<ItemStand> relevantItemStands = FindRelevantItemStands(offeringBowl);
        if (relevantItemStands.Count <= 1)
        {
            return relevantItemStands;
        }

        if (RelevantItemStandCacheByOfferingBowl.TryGetValue(offeringBowlId, out cachedEntry) &&
            cachedEntry.RegistryVersion == _itemStandRegistryVersion &&
            cachedEntry.ExpiresAt >= now)
        {
            return cachedEntry.DisplayItemStands;
        }

        return BuildDisplayRelevantItemStands(relevantItemStands);
    }

    private static IReadOnlyList<ItemStand> BuildDisplayRelevantItemStands(IReadOnlyList<ItemStand> relevantItemStands)
    {
        if (relevantItemStands.Count <= 1)
        {
            return relevantItemStands.Count == 0 ? EmptyItemStands : relevantItemStands;
        }

        Dictionary<(int X, int Y, int Z), ItemStand> bestByPosition = new();
        foreach (ItemStand itemStand in relevantItemStands)
        {
            if (itemStand == null)
            {
                continue;
            }

            Vector3 position = itemStand.transform.position;
            (int X, int Y, int Z) key =
                ((int)MathF.Round(position.x * 20f), (int)MathF.Round(position.y * 20f), (int)MathF.Round(position.z * 20f));
            if (!bestByPosition.TryGetValue(key, out ItemStand? existing) ||
                CompareDisplayPriority(itemStand, existing) > 0)
            {
                bestByPosition[key] = itemStand;
            }
        }

        return bestByPosition.Count == 0 ? EmptyItemStands : bestByPosition.Values.ToList();
    }

    private static string BuildInfo(ItemStand itemStand)
    {
        if (itemStand.m_supportedItems == null || itemStand.m_supportedItems.Count == 0)
        {
            return "";
        }

        List<string> names = itemStand.m_supportedItems
            .Where(item => item != null)
            .Select(item => Localize(item.m_itemData.m_shared.m_name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            return "";
        }

        return string.Join(", ", names);
    }

    internal static bool TryGetRelevantOfferingBowl(ItemStand itemStand, out OfferingBowl? offeringBowl)
    {
        offeringBowl = null;
        float now = Time.unscaledTime;
        CleanupExpiredRelevantOfferingBowlCache(now);
        int itemStandId = itemStand.GetInstanceID();
        if (RelevantOfferingBowlCacheByItemStand.TryGetValue(itemStandId, out RelevantOfferingBowlCacheEntry? cachedEntry) &&
            cachedEntry.RegistryVersion == _itemStandRegistryVersion &&
            cachedEntry.ExpiresAt >= now)
        {
            offeringBowl = cachedEntry.OfferingBowl;
            return offeringBowl != null;
        }

        Location? location = itemStand.GetComponentInParent<Location>(true);
        if (location == null)
        {
            if (TryGetDetachedStructureRoot(itemStand.transform, out Transform? detachedRoot) && detachedRoot != null)
            {
                offeringBowl = FindNearestRelevantOfferingBowl(itemStand, detachedRoot.GetComponentsInChildren<OfferingBowl>(true));
                if (offeringBowl != null)
                {
                    return true;
                }
            }

            offeringBowl = FindNearestRelevantOfferingBowl(itemStand, OfferingBowlHoverInfoFormatter.GetKnownOfferingBowls());
            CacheRelevantOfferingBowl(itemStandId, offeringBowl, now);
            return offeringBowl != null;
        }

        offeringBowl = FindNearestRelevantOfferingBowl(itemStand, location.GetComponentsInChildren<OfferingBowl>(true));
        CacheRelevantOfferingBowl(itemStandId, offeringBowl, now);
        return offeringBowl != null;
    }

    internal static bool TryResolveOfferingBowlContext(OfferingBowl? offeringBowl, out string locationPrefab, out Transform root)
    {
        locationPrefab = "";
        root = null!;
        if (offeringBowl == null)
        {
            return false;
        }

        root = offeringBowl.transform;
        Location? location = offeringBowl.GetComponentInParent<Location>(true);
        if (location != null)
        {
            root = location.transform;
        }

        if (TryGetDetachedStructureRoot(offeringBowl.transform, out Transform? detachedRoot) && detachedRoot != null)
        {
            root = location != null ? location.transform : detachedRoot;
        }

        if (LocationManager.TryResolveRuntimeLocationPrefabName(location, out locationPrefab))
        {
            return locationPrefab.Length > 0;
        }

        if (LocationManager.TryResolveZoneLocationPrefabName(offeringBowl.transform.position, out locationPrefab))
        {
            return true;
        }

        LocationProxy? proxy = offeringBowl.GetComponentInParent<LocationProxy>(true);
        if (proxy != null && LocationManager.TryResolveLocationProxyPrefabName(proxy, out locationPrefab))
        {
            return locationPrefab.Length > 0;
        }

        return false;
    }

    internal static bool IsRelevantToOfferingBowl(ItemStand? itemStand, OfferingBowl? offeringBowl)
    {
        if (itemStand == null || offeringBowl == null || !offeringBowl.m_useItemStands)
        {
            return false;
        }

        if (Vector3.Distance(offeringBowl.transform.position, itemStand.transform.position) > offeringBowl.m_itemstandMaxRange)
        {
            return false;
        }

        return itemStand.gameObject.name.CustomStartsWith(offeringBowl.m_itemStandPrefix);
    }

    private static void AddRelevantItemStands(Transform root, OfferingBowl offeringBowl, List<ItemStand> itemStands, HashSet<int> seenIds)
    {
        foreach (ItemStand itemStand in root.GetComponentsInChildren<ItemStand>(true))
        {
            if (itemStand == null || !seenIds.Add(itemStand.GetInstanceID()) || !IsRelevantToOfferingBowl(itemStand, offeringBowl))
            {
                continue;
            }

            itemStands.Add(itemStand);
        }
    }

    private static OfferingBowl? FindNearestRelevantOfferingBowl(ItemStand itemStand, IEnumerable<OfferingBowl> candidates)
    {
        OfferingBowl? nearest = null;
        float bestDistance = float.MaxValue;
        foreach (OfferingBowl candidate in candidates)
        {
            if (candidate == null || !IsRelevantToOfferingBowl(itemStand, candidate))
            {
                continue;
            }

            float distance = Vector3.SqrMagnitude(candidate.transform.position - itemStand.transform.position);
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            nearest = candidate;
        }

        return nearest;
    }

    private static int CompareDisplayPriority(ItemStand candidate, ItemStand existing)
    {
        return GetDisplayPriority(candidate).CompareTo(GetDisplayPriority(existing));
    }

    private static int GetDisplayPriority(ItemStand itemStand)
    {
        int score = 0;
        if (itemStand.gameObject.activeInHierarchy)
        {
            score += 4;
        }

        if (itemStand.GetComponentInParent<Location>(true) == null)
        {
            score += 2;
        }

        score += itemStand.m_supportedItems?.Count ?? 0;
        return score;
    }

    private static bool TryGetOfferingBowlStructuralRoot(OfferingBowl offeringBowl, out Transform? root)
    {
        root = offeringBowl.GetComponentInParent<Location>(true)?.transform;
        if (root != null)
        {
            return true;
        }

        return TryGetDetachedStructureRoot(offeringBowl.transform, out root);
    }

    internal static bool TryGetDetachedStructureRoot(Transform transform, out Transform? root)
    {
        if (transform == null)
        {
            root = null;
            return false;
        }

        Transform current = transform;
        while (current.parent != null)
        {
            Transform parent = current.parent;
            if (parent.GetComponent<Location>() != null ||
                parent.GetComponent<LocationProxy>() != null ||
                string.Equals(parent.name, "_ZoneCtrl(Clone)", StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        root = current;
        return root != null;
    }

    private static string TrimCloneSuffix(string? name)
    {
        string value = (name ?? "").Trim();
        const string cloneSuffix = "(Clone)";
        if (value.EndsWith(cloneSuffix, StringComparison.Ordinal))
        {
            value = value.Substring(0, value.Length - cloneSuffix.Length).TrimEnd();
        }

        return value;
    }

    private static string Localize(string text)
    {
        return Localization.instance != null ? Localization.instance.Localize(text ?? "") : (text ?? "");
    }

    private static void EnsureItemStandRegistryPopulated()
    {
        if (RegisteredItemStands.Count > 0)
        {
            return;
        }

        foreach (ItemStand itemStand in UnityEngine.Object.FindObjectsByType<ItemStand>(FindObjectsSortMode.None))
        {
            RegisterItemStand(itemStand);
        }
    }

    private static void CleanupRegisteredItemStands()
    {
        bool removedAny = false;
        for (int index = RegisteredItemStands.Count - 1; index >= 0; index--)
        {
            ItemStand itemStand = RegisteredItemStands[index];
            if (itemStand != null)
            {
                continue;
            }

            RegisteredItemStands.RemoveAt(index);
            removedAny = true;
        }

        if (removedAny)
        {
            RegisteredItemStandIds.Clear();
            foreach (ItemStand registeredItemStand in RegisteredItemStands)
            {
                if (registeredItemStand != null)
                {
                    RegisteredItemStandIds.Add(registeredItemStand.GetInstanceID());
                }
            }

            _itemStandRegistryVersion++;
        }
    }

    private static void CacheRelevantOfferingBowl(int itemStandId, OfferingBowl? offeringBowl, float now)
    {
        RelevantOfferingBowlCacheByItemStand[itemStandId] = new RelevantOfferingBowlCacheEntry
        {
            RegistryVersion = _itemStandRegistryVersion,
            ExpiresAt = now + RelevantOfferingBowlCacheLifetimeSeconds,
            OfferingBowl = offeringBowl
        };
    }

    private static void CleanupExpiredRelevantOfferingBowlCache(float now)
    {
        if (RelevantOfferingBowlCacheByItemStand.Count == 0)
        {
            return;
        }

        List<int>? expiredIds = null;
        foreach ((int itemStandId, RelevantOfferingBowlCacheEntry entry) in RelevantOfferingBowlCacheByItemStand)
        {
            if (entry.RegistryVersion == _itemStandRegistryVersion &&
                entry.ExpiresAt >= now)
            {
                continue;
            }

            expiredIds ??= new List<int>();
            expiredIds.Add(itemStandId);
        }

        if (expiredIds == null)
        {
            return;
        }

        foreach (int itemStandId in expiredIds)
        {
            RelevantOfferingBowlCacheByItemStand.Remove(itemStandId);
        }
    }

    private static void CleanupExpiredRelevantItemStandCache(float now)
    {
        if (RelevantItemStandCacheByOfferingBowl.Count == 0)
        {
            return;
        }

        List<int>? expiredIds = null;
        foreach ((int offeringBowlId, RelevantItemStandCacheEntry entry) in RelevantItemStandCacheByOfferingBowl)
        {
            if (entry.ExpiresAt >= now)
            {
                continue;
            }

            expiredIds ??= new List<int>();
            expiredIds.Add(offeringBowlId);
        }

        if (expiredIds == null)
        {
            return;
        }

        foreach (int offeringBowlId in expiredIds)
        {
            RelevantItemStandCacheByOfferingBowl.Remove(offeringBowlId);
        }
    }
}
