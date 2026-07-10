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

    private static string ComputePreparedEntriesSignature(List<PreparedSpawnSystemEntry> entries)
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
