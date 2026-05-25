using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal static class DomainDictionaryDiffSupport
{
    internal static HashSet<string> BuildDirtyKeys(
        IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> current)
    {
        HashSet<string> dirtyKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in current)
        {
            if (!previous.TryGetValue(key, out string? previousValue) ||
                !string.Equals(previousValue, value, StringComparison.Ordinal))
            {
                dirtyKeys.Add(key);
            }
        }

        foreach (string key in previous.Keys)
        {
            if (!current.ContainsKey(key))
            {
                dirtyKeys.Add(key);
            }
        }

        return dirtyKeys;
    }

    internal static void ReplaceEntries<TValue>(
        Dictionary<string, TValue> target,
        IReadOnlyDictionary<string, TValue> source)
    {
        target.Clear();
        foreach ((string key, TValue value) in source)
        {
            target[key] = value;
        }
    }
}
