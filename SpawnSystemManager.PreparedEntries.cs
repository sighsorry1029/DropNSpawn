using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

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

    private static void QueuePreparedEntriesBuildLocked(
        int gameDataSignature,
        bool domainEnabled,
        string applyTargetSignature,
        bool queueEspRefreshForLiveSystems)
    {
        ClearQueuedReconcileState();
        BuildPipelineState.PreparedEntriesBuildVersion++;
        int buildVersion = BuildPipelineState.PreparedEntriesBuildVersion;
        BuildPipelineState.PreparedEntriesBuildInFlight = true;
        BuildPipelineState.CompletedPreparedEntriesBuildResult = null;
        BuildPipelineState.PendingCompiledTableBuild = null;
        BuildPipelineState.PendingBuildTargetSignature = applyTargetSignature;
        PendingPreparedEntriesBuildRequest request = new()
        {
            BuildVersion = buildVersion,
            GameDataSignature = gameDataSignature,
            DomainEnabled = domainEnabled,
            ApplyTargetSignature = applyTargetSignature,
            QueueEspRefreshForLiveSystems = queueEspRefreshForLiveSystems
        };
        request.ConfigurationSnapshot.AddRange(RuntimeState.Configuration);
        BuildPipelineState.PendingPreparedEntriesBuildRequest = request;
        EnsurePreparedEntriesBuildWorkerLocked();
    }

    private static void EnsurePreparedEntriesBuildWorkerLocked()
    {
        if (BuildPipelineState.PreparedEntriesBuildWorkerRunning)
        {
            return;
        }

        BuildPipelineState.PreparedEntriesBuildWorkerRunning = true;
        ThreadPool.QueueUserWorkItem(_ => ProcessPreparedEntriesBuildWorker());
    }

    private static void ProcessPreparedEntriesBuildWorker()
    {
        while (true)
        {
            PendingPreparedEntriesBuildRequest? request;
            lock (Sync)
            {
                request = BuildPipelineState.PendingPreparedEntriesBuildRequest;
                BuildPipelineState.PendingPreparedEntriesBuildRequest = null;
                if (request == null)
                {
                    BuildPipelineState.PreparedEntriesBuildWorkerRunning = false;
                    if (BuildPipelineState.CompletedPreparedEntriesBuildResult == null)
                    {
                        BuildPipelineState.PreparedEntriesBuildInFlight = false;
                    }

                    return;
                }
            }

            try
            {
                PreparedEntriesBuildResult? result = BuildPreparedEntriesResult(request);
                if (result == null)
                {
                    continue;
                }

                lock (Sync)
                {
                    if (!IsPreparedEntriesBuildCurrentLocked(result.BuildVersion, result.ApplyTargetSignature))
                    {
                        continue;
                    }

                    PruneFinalizedPreparedEntryCacheLocked(
                        result.GameDataSignature,
                        result.Models.Select(model => model.EntrySignature));
                    BuildPipelineState.CompletedPreparedEntriesBuildResult = result;
                    BuildPipelineState.PreparedEntriesBuildInFlight = BuildPipelineState.PendingPreparedEntriesBuildRequest != null;
                }
            }
            catch (Exception ex)
            {
                lock (Sync)
                {
                    if (request != null &&
                        IsPreparedEntriesBuildCurrentLocked(request.BuildVersion, request.ApplyTargetSignature))
                    {
                        BuildPipelineState.PreparedEntriesBuildInFlight = BuildPipelineState.PendingPreparedEntriesBuildRequest != null;
                        BuildPipelineState.CompletedPreparedEntriesBuildResult = null;
                        if (BuildPipelineState.PendingPreparedEntriesBuildRequest == null)
                        {
                            BuildPipelineState.PendingBuildTargetSignature = "";
                            BuildPipelineState.PendingGameDataSignature = null;
                        }
                    }
                }

                DropNSpawnPlugin.DropNSpawnLogger.LogError($"Failed to prepare staged spawnsystem build. {ex}");
            }
        }
    }

    private static bool IsPreparedEntriesBuildCurrentLocked(int buildVersion, string applyTargetSignature)
    {
        return buildVersion == BuildPipelineState.PreparedEntriesBuildVersion &&
               string.Equals(BuildPipelineState.PendingBuildTargetSignature, applyTargetSignature, StringComparison.Ordinal);
    }

    private static bool IsPreparedEntriesBuildCurrent(int buildVersion, string applyTargetSignature)
    {
        lock (Sync)
        {
            return IsPreparedEntriesBuildCurrentLocked(buildVersion, applyTargetSignature);
        }
    }

    private static PreparedEntriesBuildResult? BuildPreparedEntriesResult(PendingPreparedEntriesBuildRequest request)
    {
        PreparedEntriesBuildResult result = new()
        {
            BuildVersion = request.BuildVersion,
            GameDataSignature = request.GameDataSignature,
            DomainEnabled = request.DomainEnabled,
            ApplyTargetSignature = request.ApplyTargetSignature,
            QueueEspRefreshForLiveSystems = request.QueueEspRefreshForLiveSystems
        };

        for (int index = 0; index < request.ConfigurationSnapshot.Count; index++)
        {
            if (!IsPreparedEntriesBuildCurrent(request.BuildVersion, request.ApplyTargetSignature))
            {
                return null;
            }

            CanonicalSpawnSystemEntry entry = request.ConfigurationSnapshot[index];
            result.Models.Add(new PreparedSpawnSystemModel
            {
                Entry = entry,
                EntrySignature = NetworkPayloadSyncSupport.ComputeSpawnSystemEntrySignature(entry),
                Context = CreateConfigurationContext(index, entry),
                RuntimeTimeOfDay = entry.SpawnSystem?.TimeOfDay
            });
        }

        if (!IsPreparedEntriesBuildCurrent(request.BuildVersion, request.ApplyTargetSignature))
        {
            return null;
        }

        return result;
    }

    private static void PruneFinalizedPreparedEntryCacheLocked(
        int gameDataSignature,
        IEnumerable<string>? activeEntrySignatures = null)
    {
        if (FinalizedPreparedEntryCache.Count == 0)
        {
            return;
        }

        HashSet<string>? activeEntrySignatureSet = null;
        if (activeEntrySignatures != null)
        {
            activeEntrySignatureSet = new HashSet<string>(
                activeEntrySignatures.Where(signature => !string.IsNullOrWhiteSpace(signature)),
                StringComparer.Ordinal);
        }

        List<string> staleKeys = new();
        foreach ((string cacheKey, FinalizedPreparedEntryCacheEntry entry) in FinalizedPreparedEntryCache)
        {
            if (entry == null)
            {
                staleKeys.Add(cacheKey);
                continue;
            }

            if (entry.GameDataSignature != gameDataSignature)
            {
                staleKeys.Add(cacheKey);
                continue;
            }

            if (activeEntrySignatureSet != null &&
                !activeEntrySignatureSet.Contains(entry.EntrySignature))
            {
                staleKeys.Add(cacheKey);
            }
        }

        foreach (string staleKey in staleKeys)
        {
            FinalizedPreparedEntryCache.Remove(staleKey);
        }
    }


    private static bool TryFinalizePreparedSpawnSystemModelLocked(
        PreparedSpawnSystemModel model,
        int gameDataSignature,
        out PreparedSpawnSystemEntry? finalizedEntry)
    {
        finalizedEntry = null;
        if (model == null || model.Entry == null)
        {
            return false;
        }

        string entrySignature = model.EntrySignature.Length > 0
            ? model.EntrySignature
            : NetworkPayloadSyncSupport.ComputeSpawnSystemEntrySignature(model.Entry);
        model.EntrySignature = entrySignature;
        string cacheKey = string.Concat(gameDataSignature.ToString(CultureInfo.InvariantCulture), "|", entrySignature);
        if (FinalizedPreparedEntryCache.TryGetValue(cacheKey, out FinalizedPreparedEntryCacheEntry? cachedEntry) &&
            cachedEntry != null &&
            cachedEntry.GameDataSignature == gameDataSignature)
        {
            if (ShouldSkipLowTierBiomeGlobalKeySpawnSystemEntry(cachedEntry.Data))
            {
                return false;
            }

            finalizedEntry = new PreparedSpawnSystemEntry
            {
                Entry = model.Entry,
                Data = cachedEntry.Data.Clone(),
                CustomDataPayload = cachedEntry.CustomDataPayload,
                RuntimeTimeOfDay = cachedEntry.RuntimeTimeOfDay
            };
            return true;
        }

        SpawnSystem.SpawnData data = new();
        if (!ApplyEntry(data, model.Entry, model.Context, applyCustomData: false))
        {
            return false;
        }

        if (ShouldSkipLowTierBiomeGlobalKeySpawnSystemEntry(data))
        {
            return false;
        }

        SpawnSystemCustomDataSupport.PreparedPayload? customDataPayload =
            SpawnSystemCustomDataSupport.BuildPreparedPayload(data, model.Entry, model.Context);
        FinalizedPreparedEntryCache[cacheKey] = new FinalizedPreparedEntryCacheEntry
        {
            GameDataSignature = gameDataSignature,
            EntrySignature = entrySignature,
            Data = data.Clone(),
            CustomDataPayload = customDataPayload,
            RuntimeTimeOfDay = model.RuntimeTimeOfDay
        };

        finalizedEntry = new PreparedSpawnSystemEntry
        {
            Entry = model.Entry,
            Data = data,
            CustomDataPayload = customDataPayload,
            RuntimeTimeOfDay = model.RuntimeTimeOfDay
        };
        return true;
    }

}
