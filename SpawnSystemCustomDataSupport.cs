extern alias ewd;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using EwdData = ewd::Data;

namespace DropNSpawn;

internal static class SpawnSystemCustomDataSupport
{
    internal sealed class PreparedPayload
    {
        public object? CustomData { get; set; }
        public IList? CustomObjects { get; set; }
        public string? StandaloneFaction { get; set; }

        internal bool HasValues()
        {
            return CustomData != null || (CustomObjects?.Count ?? 0) > 0 || StandaloneFaction != null;
        }
    }

    // Templates, attached SpawnSystem rows and event clones own their SpawnData.
    // Keeping their payload here must not keep discarded rows or event clones alive.
    private static readonly ConditionalWeakTable<SpawnSystem.SpawnData, PreparedPayload> PayloadsBySpawnData = new();
    private static readonly int HashFaction = "faction".GetStableHashCode();

    internal static void ClearCustomData(SpawnSystem system)
    {
        if (system == null)
        {
            return;
        }

        foreach (SpawnSystemList spawnList in system.m_spawnLists)
        {
            foreach (SpawnSystem.SpawnData spawnData in spawnList.m_spawners)
            {
                PayloadsBySpawnData.Remove(spawnData);
            }
        }
    }

    internal static void ApplyCustomData(SpawnSystem.SpawnData spawnData, CanonicalSpawnSystemEntry entry, string context)
    {
        if (spawnData == null)
        {
            return;
        }

        ApplyPreparedPayload(spawnData, BuildPreparedPayload(spawnData, entry, context));
    }

    internal static PreparedPayload? BuildPreparedPayload(SpawnSystem.SpawnData spawnData, CanonicalSpawnSystemEntry entry, string context)
    {
        if (spawnData == null)
        {
            return null;
        }

        // Disabled rows must still reach the native table to disable an existing
        // spawn. Their unused extensions must not reject/drop that disabling row.
        if (!ExpandWorldDataCompatibility.IsAvailable && !entry.Enabled) return null;

        var definition = entry.SpawnSystem;
        ExpandWorldDataCompatibility.RequireExtensions(definition?.Data, definition?.Fields, definition?.Objects, context);
        if (ExpandWorldDataCompatibility.IsAvailable) return BuildEwdPayload(spawnData, entry, context);
        string? faction = FactionIntegration.Normalize(definition?.Faction);
        return faction == null ? null : new PreparedPayload { StandaloneFaction = faction };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PreparedPayload? BuildEwdPayload(SpawnSystem.SpawnData spawnData, CanonicalSpawnSystemEntry entry, string context)
    {
        PreparedPayload payload = new()
        {
            CustomData = BuildCustomData(spawnData, entry, context),
            CustomObjects = ExpandWorldSpawnDataSupport.BuildCustomObjects(entry.SpawnSystem?.Objects, context, $"{DropNSpawnPlugin.ModName}_spawnsystem:{context}")
        };

        return payload.HasValues() ? payload : null;
    }

    internal static void ApplyPreparedPayload(SpawnSystem.SpawnData spawnData, PreparedPayload? payload)
    {
        if (spawnData == null)
        {
            return;
        }

        if (payload == null || !payload.HasValues())
        {
            PayloadsBySpawnData.Remove(spawnData);
        }
        else
        {
            PayloadsBySpawnData.Remove(spawnData);
            PayloadsBySpawnData.Add(spawnData, payload);
        }
    }

    internal static bool HasPreparedPayload(SpawnSystem.SpawnData? spawnData)
    {
        return spawnData != null &&
               PayloadsBySpawnData.TryGetValue(spawnData, out _);
    }

    internal static void CopyPreparedPayload(SpawnSystem.SpawnData? source, SpawnSystem.SpawnData? target)
    {
        if (source == null || target == null)
        {
            return;
        }

        if (PayloadsBySpawnData.TryGetValue(source, out PreparedPayload? payload))
        {
            PayloadsBySpawnData.Remove(target);
            PayloadsBySpawnData.Add(target, payload);
        }
        else
        {
            PayloadsBySpawnData.Remove(target);
        }
    }

    internal static void InitializeSpawn(SpawnSystem.SpawnData critter, Vector3 spawnPoint)
    {
        if (!ExpandWorldDataCompatibility.IsAvailable || critter == null ||
            !PayloadsBySpawnData.TryGetValue(critter, out PreparedPayload? payload) ||
            payload.CustomData == null)
        {
            return;
        }

        InitializeEwdSpawn(critter.m_prefab, spawnPoint, payload.CustomData);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InitializeEwdSpawn(GameObject prefab, Vector3 spawnPoint, object data) =>
        EwdData.DataHelper.Init(prefab, spawnPoint, Quaternion.identity, null, (EwdData.DataEntry)data);

    internal static void ApplyStandaloneFaction(GameObject spawned, SpawnSystem.SpawnData spawnData)
    {
        if (spawned != null && PayloadsBySpawnData.TryGetValue(spawnData, out PreparedPayload? payload) && payload.StandaloneFaction != null)
            FactionIntegration.Apply(spawned.GetComponent<Character>(), payload.StandaloneFaction, spawnData.m_name);
    }

    internal static void SpawnObjects(SpawnSystem.SpawnData critter, Vector3 spawnPoint)
    {
        if (!ExpandWorldDataCompatibility.IsAvailable || critter == null ||
            !PayloadsBySpawnData.TryGetValue(critter, out PreparedPayload? payload) ||
            payload.CustomObjects == null ||
            payload.CustomObjects.Count == 0)
        {
            return;
        }

        ExpandWorldDataCompatibility.SpawnObjects(spawnPoint, payload.CustomObjects);
    }

    private static EwdData.DataEntry? BuildCustomData(SpawnSystem.SpawnData spawnData, CanonicalSpawnSystemEntry entry, string context)
    {
        SpawnSystemSpawnDefinition? spawnSystem = entry.SpawnSystem;
        EwdData.DataEntry? dataFromEntry = null;
        if (spawnSystem?.Data is string dataName && !string.IsNullOrWhiteSpace(dataName))
        {
            dataFromEntry = EwdData.DataHelper.Get(dataName, $"{DropNSpawnPlugin.ModName}_spawnsystem:{context}");
        }

        EwdData.DataEntry? inlineData = null;
        if (spawnSystem?.Faction is string factionValue && !string.IsNullOrWhiteSpace(factionValue))
        {
            inlineData ??= new EwdData.DataEntry();
            inlineData.Strings ??= new Dictionary<int, EwdData.IStringValue>();
            inlineData.Strings[HashFaction] = (EwdData.IStringValue)ExpandWorldDataCompatibility.Constant(factionValue);
        }

        Dictionary<string, string>? configuredFields = spawnSystem?.Fields;
        if (configuredFields != null && configuredFields.Count > 0)
        {
            inlineData ??= new EwdData.DataEntry();
            ExpandWorldFieldOverrideSupport.AddFieldOverrides(inlineData, spawnData.m_prefab, configuredFields);
        }

        return EwdData.DataHelper.Merge(dataFromEntry, inlineData);
    }

}
