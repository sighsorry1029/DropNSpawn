using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private static List<PreparedSpawnSystemEntry> BuildPreparedEntriesCore()
    {
        if (IsPreparedEntriesCacheValid())
        {
            return _preparedEntriesCache!;
        }

        List<PreparedSpawnSystemEntry> entries = new();
        for (int index = 0; index < _configuration.Count; index++)
        {
            CanonicalSpawnSystemEntry entry = _configuration[index];
            SpawnSystem.SpawnData data = new();
            string context = CreateConfigurationContext(index, entry);
            if (!ApplyEntry(data, entry, context, applyCustomData: false))
            {
                continue;
            }

            entries.Add(new PreparedSpawnSystemEntry
            {
                Entry = entry,
                Data = data,
                Context = context,
                CustomDataPayload = SpawnSystemCustomDataSupport.BuildPreparedPayload(data, entry, context),
                RuntimeTimeOfDay = GetConfiguredTimeOfDay(entry)
            });
        }

        _preparedEntriesCache = entries;
        return _preparedEntriesCache;
    }

    private static void InvalidatePreparedEntriesCacheCore()
    {
        _preparedEntriesCache = null;
    }

    private static string ComputePreparedEntriesSignatureCore(List<PreparedSpawnSystemEntry> entries)
    {
        return NetworkPayloadSyncSupport.ComputeSpawnSystemProjectedConfigurationSignature(
            entries,
            static entry => entry.Entry);
    }
}
