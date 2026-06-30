using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class SpawnSystemManager
{
    private static readonly Heightmap.Biome LowTierBiomeGlobalKeySpawnSystemFilter =
        Heightmap.Biome.Meadows |
        Heightmap.Biome.BlackForest |
        Heightmap.Biome.Swamp |
        Heightmap.Biome.Mountain |
        Heightmap.Biome.Plains;

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

            if (ShouldSkipLowTierBiomeGlobalKeySpawnSystemEntry(data))
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

    private static bool ShouldSkipLowTierBiomeGlobalKeySpawnSystemEntry(SpawnSystem.SpawnData data)
    {
        if (!PluginSettingsFacade.ShouldDisableLowTierBiomeGlobalKeySpawnSystemEntries())
        {
            return false;
        }

        if (data == null || string.IsNullOrWhiteSpace(data.m_requiredGlobalKey))
        {
            return false;
        }

        return (data.m_biome & LowTierBiomeGlobalKeySpawnSystemFilter) != Heightmap.Biome.None;
    }
}
