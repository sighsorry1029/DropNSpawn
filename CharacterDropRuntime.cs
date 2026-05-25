using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal sealed class CharacterDropItemSnapshot
{
    public GameObject? ItemPrefab { get; set; }
    public int AmountMin { get; set; }
    public int AmountMax { get; set; }
    public float Chance { get; set; }
    public bool OnePerPlayer { get; set; }
    public bool LevelMultiplier { get; set; }
    public bool DontScale { get; set; }
}

internal sealed class CharacterDropSnapshot
{
    public GameObject Prefab { get; set; } = null!;
    public List<CharacterDropItemSnapshot> Drops { get; set; } = new();
    public List<CharacterDrop.Drop> BuiltDrops { get; set; } = new();
}

internal sealed class PendingCharacterDropSnapshotBuildState
{
    public int BuildVersion { get; set; }
    public int GameDataSignature { get; set; }
    public int SnapshotSignature { get; set; }
    public string Source { get; set; } = "";
    public List<GameObject> Prefabs { get; } = new();
    public List<CharacterDropSnapshot> Snapshots { get; } = new();
    public Dictionary<string, CharacterDropSnapshot> SnapshotsByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int NextIndex { get; set; }
}

/// <summary>
/// Owns live CharacterDrop registry state and snapshot-build state for the character domain.
/// It does not own YAML parsing or explicit despawn rule compilation.
/// </summary>
internal static class CharacterDropRuntime
{
    private static readonly List<CharacterDropSnapshot> Snapshots = new();
    private static readonly Dictionary<string, CharacterDropSnapshot> SnapshotsByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<CharacterDrop>> LiveCharacterDropsByPrefab = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<CharacterDrop, string> LiveCharacterDropPrefabsByInstance = new();

    private static int? _lastProcessedSnapshotSignature;
    private static int? _lastProcessedGameDataSignature;
    private static int? _lastBootstrappedLiveObjectsSceneSignature;
    private static int _snapshotBuildVersion;
    private static PendingCharacterDropSnapshotBuildState? _pendingSnapshotBuild;

    internal static void Reset()
    {
        Snapshots.Clear();
        SnapshotsByPrefab.Clear();
        LiveCharacterDropsByPrefab.Clear();
        LiveCharacterDropPrefabsByInstance.Clear();
        _lastProcessedSnapshotSignature = null;
        _lastProcessedGameDataSignature = null;
        _lastBootstrappedLiveObjectsSceneSignature = null;
        _pendingSnapshotBuild = null;
        _snapshotBuildVersion = 0;
    }

    internal static bool HasSnapshots()
    {
        return Snapshots.Count > 0;
    }

    internal static int SnapshotCount()
    {
        return Snapshots.Count;
    }

    internal static IReadOnlyList<CharacterDropSnapshot> GetSnapshots()
    {
        return Snapshots;
    }

    internal static bool HasSnapshot(string prefabName)
    {
        return !string.IsNullOrWhiteSpace(prefabName) && SnapshotsByPrefab.ContainsKey(prefabName);
    }

    internal static bool TryGetSnapshot(string prefabName, out CharacterDropSnapshot? snapshot)
    {
        return SnapshotsByPrefab.TryGetValue(prefabName ?? "", out snapshot);
    }

    internal static bool IsGameDataAlreadyProcessed(int gameDataSignature)
    {
        return _lastProcessedGameDataSignature == gameDataSignature;
    }

    internal static bool NeedsSnapshotBuild(int snapshotSignature)
    {
        return _lastProcessedSnapshotSignature != snapshotSignature || Snapshots.Count == 0;
    }

    internal static void ScheduleSnapshotBuild(string source, int gameDataSignature, int snapshotSignature, IEnumerable<GameObject> prefabs)
    {
        if (_pendingSnapshotBuild != null &&
            _pendingSnapshotBuild.GameDataSignature == gameDataSignature &&
            _pendingSnapshotBuild.SnapshotSignature == snapshotSignature)
        {
            return;
        }

        PendingCharacterDropSnapshotBuildState buildState = new()
        {
            BuildVersion = ++_snapshotBuildVersion,
            GameDataSignature = gameDataSignature,
            SnapshotSignature = snapshotSignature,
            Source = source
        };
        buildState.Prefabs.AddRange(prefabs);
        _pendingSnapshotBuild = buildState;
    }

    internal static bool HasPendingSnapshotBuildWork()
    {
        return _pendingSnapshotBuild != null;
    }

    internal static bool ProcessPendingSnapshotBuildStep(
        float deadline,
        Func<GameObject, CharacterDropSnapshot?> captureSnapshot,
        Action<string, int, int> onCompleted)
    {
        PendingCharacterDropSnapshotBuildState? buildState = _pendingSnapshotBuild;
        if (buildState == null)
        {
            return false;
        }

        if (buildState.NextIndex >= buildState.Prefabs.Count)
        {
            CompletePendingSnapshotBuild(buildState, onCompleted);
            return true;
        }

        GameObject? prefab = buildState.Prefabs[buildState.NextIndex];
        buildState.NextIndex++;
        CharacterDropSnapshot? snapshot = prefab != null ? captureSnapshot(prefab) : null;

        if (_pendingSnapshotBuild == null ||
            !ReferenceEquals(_pendingSnapshotBuild, buildState))
        {
            return true;
        }

        if (snapshot != null)
        {
            buildState.Snapshots.Add(snapshot);
            if (!buildState.SnapshotsByPrefab.ContainsKey(snapshot.Prefab.name))
            {
                buildState.SnapshotsByPrefab.Add(snapshot.Prefab.name, snapshot);
            }
        }

        if (buildState.NextIndex >= buildState.Prefabs.Count &&
            Time.realtimeSinceStartup <= deadline)
        {
            CompletePendingSnapshotBuild(buildState, onCompleted);
        }

        return true;
    }

    internal static void CaptureSnapshotsIfNeeded(IEnumerable<GameObject> prefabs, Func<GameObject, CharacterDropSnapshot?> captureSnapshot)
    {
        if (Snapshots.Count > 0)
        {
            return;
        }

        foreach (GameObject prefab in prefabs)
        {
            CharacterDropSnapshot? snapshot = captureSnapshot(prefab);
            if (snapshot == null)
            {
                continue;
            }

            Snapshots.Add(snapshot);
            if (!SnapshotsByPrefab.ContainsKey(snapshot.Prefab.name))
            {
                SnapshotsByPrefab.Add(snapshot.Prefab.name, snapshot);
            }
        }
    }

    internal static void RefreshSnapshots(IEnumerable<GameObject> prefabs, Func<GameObject, CharacterDropSnapshot?> captureSnapshot)
    {
        Snapshots.Clear();
        SnapshotsByPrefab.Clear();
        _lastBootstrappedLiveObjectsSceneSignature = null;
        CaptureSnapshotsIfNeeded(prefabs, captureSnapshot);
    }

    internal static void RegisterLiveCharacterDrop(CharacterDrop characterDrop, string prefabName)
    {
        if (characterDrop == null || string.IsNullOrWhiteSpace(prefabName))
        {
            return;
        }

        if (LiveCharacterDropPrefabsByInstance.TryGetValue(characterDrop, out string? previousPrefabName))
        {
            if (string.Equals(previousPrefabName, prefabName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            UnregisterLiveCharacterDrop(characterDrop, previousPrefabName);
        }

        LiveCharacterDropPrefabsByInstance[characterDrop] = prefabName;
        if (!LiveCharacterDropsByPrefab.TryGetValue(prefabName, out HashSet<CharacterDrop>? characterDrops))
        {
            characterDrops = new HashSet<CharacterDrop>();
            LiveCharacterDropsByPrefab[prefabName] = characterDrops;
        }

        characterDrops.Add(characterDrop);
    }

    internal static bool TryGetRegisteredPrefabName(CharacterDrop characterDrop, out string prefabName)
    {
        return LiveCharacterDropPrefabsByInstance.TryGetValue(characterDrop, out prefabName!);
    }

    internal static void UnregisterLiveCharacterDrop(CharacterDrop characterDrop, string prefabName)
    {
        LiveCharacterDropPrefabsByInstance.Remove(characterDrop);
        if (!LiveCharacterDropsByPrefab.TryGetValue(prefabName, out HashSet<CharacterDrop>? characterDrops))
        {
            return;
        }

        characterDrops.Remove(characterDrop);
        if (characterDrops.Count == 0)
        {
            LiveCharacterDropsByPrefab.Remove(prefabName);
        }
    }

    internal static void BootstrapRegisteredCharacterDropsIfNeeded(
        int sceneSignature,
        IEnumerable<CharacterDrop> liveCharacterDrops,
        Func<CharacterDrop, string> getPrefabName,
        Func<string, bool> shouldRegisterPrefab,
        ISet<string>? additionalPrefabs = null,
        bool forceRescan = false)
    {
        if (!forceRescan && _lastBootstrappedLiveObjectsSceneSignature == sceneSignature)
        {
            return;
        }

        CleanupRegisteredCharacterDrops();

        foreach (CharacterDrop characterDrop in liveCharacterDrops)
        {
            if (characterDrop == null || characterDrop.gameObject == null)
            {
                continue;
            }

            string prefabName = getPrefabName(characterDrop);
            if (prefabName.Length == 0)
            {
                continue;
            }

            if (!shouldRegisterPrefab(prefabName) &&
                (additionalPrefabs == null || !additionalPrefabs.Contains(prefabName)))
            {
                continue;
            }

            RegisterLiveCharacterDrop(characterDrop, prefabName);
        }

        _lastBootstrappedLiveObjectsSceneSignature = sceneSignature;
    }

    internal static IReadOnlyList<CharacterDrop> GetRegisteredCharacterDrops(HashSet<string>? dirtyPrefabs = null)
    {
        CleanupRegisteredCharacterDrops();
        HashSet<CharacterDrop> visited = new();
        List<CharacterDrop> registered = new();
        if (dirtyPrefabs == null)
        {
            foreach (HashSet<CharacterDrop> characterDrops in LiveCharacterDropsByPrefab.Values)
            {
                AddRegisteredCharacterDrops(characterDrops, visited, registered);
            }

            return registered;
        }

        foreach (string prefabName in dirtyPrefabs)
        {
            if (!LiveCharacterDropsByPrefab.TryGetValue(prefabName, out HashSet<CharacterDrop>? characterDrops))
            {
                continue;
            }

            AddRegisteredCharacterDrops(characterDrops, visited, registered);
        }

        return registered;
    }

    private static void AddRegisteredCharacterDrops(
        IEnumerable<CharacterDrop> characterDrops,
        ISet<CharacterDrop> visited,
        ICollection<CharacterDrop> registered)
    {
        foreach (CharacterDrop characterDrop in characterDrops ?? Enumerable.Empty<CharacterDrop>())
        {
            if (characterDrop != null && characterDrop.gameObject != null && visited.Add(characterDrop))
            {
                registered.Add(characterDrop);
            }
        }
    }

    private static void CleanupRegisteredCharacterDrops()
    {
        List<CharacterDrop>? deadInstances = null;
        foreach (CharacterDrop characterDrop in LiveCharacterDropPrefabsByInstance.Keys.ToList())
        {
            if (characterDrop == null || characterDrop.gameObject == null)
            {
                deadInstances ??= new List<CharacterDrop>();
                deadInstances.Add(characterDrop!);
            }
        }

        if (deadInstances == null)
        {
            return;
        }

        foreach (CharacterDrop deadInstance in deadInstances)
        {
            if (LiveCharacterDropPrefabsByInstance.TryGetValue(deadInstance, out string? prefabName))
            {
                UnregisterLiveCharacterDrop(deadInstance, prefabName);
            }
        }
    }

    private static void CompletePendingSnapshotBuild(
        PendingCharacterDropSnapshotBuildState buildState,
        Action<string, int, int> onCompleted)
    {
        if (_pendingSnapshotBuild == null || !ReferenceEquals(_pendingSnapshotBuild, buildState))
        {
            return;
        }

        Snapshots.Clear();
        Snapshots.AddRange(buildState.Snapshots);
        SnapshotsByPrefab.Clear();
        foreach ((string prefabName, CharacterDropSnapshot snapshot) in buildState.SnapshotsByPrefab)
        {
            SnapshotsByPrefab[prefabName] = snapshot;
        }

        _lastBootstrappedLiveObjectsSceneSignature = null;
        _pendingSnapshotBuild = null;
        _lastProcessedSnapshotSignature = buildState.SnapshotSignature;
        _lastProcessedGameDataSignature = buildState.GameDataSignature;
        onCompleted(buildState.Source, buildState.GameDataSignature, buildState.SnapshotSignature);
    }
}
