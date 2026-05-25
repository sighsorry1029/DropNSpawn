using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DropNSpawn;

internal static class VanillaPrefabCatalog
{
    private enum CatalogState
    {
        Uninitialized,
        Loaded,
        Unavailable
    }

    private static readonly object Sync = new();
    private static readonly HashSet<string> PrefabNames = new(StringComparer.OrdinalIgnoreCase);
    private static CatalogState _state;

    internal static bool IsAvailable
    {
        get
        {
            EnsureLoaded();
            return _state == CatalogState.Loaded;
        }
    }

    internal static bool IsVanilla(string prefabName)
    {
        EnsureLoaded();
        return _state == CatalogState.Loaded &&
               !string.IsNullOrWhiteSpace(prefabName) &&
               PrefabNames.Contains(prefabName);
    }

    private static void EnsureLoaded()
    {
        if (_state != CatalogState.Uninitialized)
        {
            return;
        }

        lock (Sync)
        {
            if (_state != CatalogState.Uninitialized)
            {
                return;
            }

            string manifestPath = Path.Combine(Application.dataPath, "StreamingAssets", "SoftRef", "manifest_extended");
            if (!File.Exists(manifestPath))
            {
                _state = CatalogState.Unavailable;
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Vanilla prefab manifest was not found at '{manifestPath}'. Prefab owner grouping will still track bundle-backed modded prefabs, but unmapped prefabs will fall under '{PrefabOwnerCatalog.UnknownOwnerName}'.");
                return;
            }

            const string marker = "path in bundle:";
            foreach (string rawLine in File.ReadLines(manifestPath))
            {
                int markerIndex = rawLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    continue;
                }

                string bundlePath = rawLine.Substring(markerIndex + marker.Length).Trim();
                if (!bundlePath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prefabName = Path.GetFileNameWithoutExtension(bundlePath);
                if (!string.IsNullOrWhiteSpace(prefabName))
                {
                    PrefabNames.Add(prefabName);
                }
            }

            _state = CatalogState.Loaded;
            DropNSpawnPlugin.DropNSpawnLogger.LogDebug($"Loaded {PrefabNames.Count} vanilla prefab names from '{manifestPath}'.");
        }
    }
}
