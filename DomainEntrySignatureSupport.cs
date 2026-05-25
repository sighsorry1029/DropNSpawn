using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal static class DomainEntrySignatureSupport
{
    internal static Dictionary<string, string> BuildSignaturesByKey<TEntry>(
        IEnumerable<KeyValuePair<string, List<TEntry>>>? entriesByKey,
        Func<List<TEntry>, string> computeSignature)
    {
        Dictionary<string, string> signatures = new(StringComparer.OrdinalIgnoreCase);
        if (entriesByKey == null)
        {
            return signatures;
        }

        foreach ((string key, List<TEntry> entries) in entriesByKey)
        {
            signatures[key] = computeSignature(entries);
        }

        return signatures;
    }
}
