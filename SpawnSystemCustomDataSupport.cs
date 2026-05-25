extern alias ewd;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using UnityEngine;
using EwdData = ewd::Data;
using EwdBlueprintObject = ewd::ExpandWorldData.BlueprintObject;
using EwdSpawn = ewd::ExpandWorldData.Spawn;

namespace DropNSpawn;

internal static class SpawnSystemCustomDataSupport
{
    internal sealed class PreparedPayload
    {
        public EwdData.DataEntry? CustomData { get; set; }
        public List<EwdBlueprintObject>? CustomObjects { get; set; }

        internal bool HasValues()
        {
            return CustomData != null || (CustomObjects?.Count ?? 0) > 0;
        }
    }

    private static readonly Dictionary<SpawnSystem.SpawnData, EwdData.DataEntry?> CustomDataBySpawnData = new();
    private static readonly Dictionary<SpawnSystem.SpawnData, List<EwdBlueprintObject>> ObjectsBySpawnData = new();
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
                CustomDataBySpawnData.Remove(spawnData);
                ObjectsBySpawnData.Remove(spawnData);
            }
        }
    }

    internal static void ClearAll()
    {
        CustomDataBySpawnData.Clear();
        ObjectsBySpawnData.Clear();
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

        PreparedPayload payload = new()
        {
            CustomData = BuildCustomData(spawnData, entry, context),
            CustomObjects = BuildCustomObjects(entry.Modifiers?.Objects, context)
        };

        return payload.HasValues() ? payload : null;
    }

    internal static void ApplyPreparedPayload(SpawnSystem.SpawnData spawnData, PreparedPayload? payload)
    {
        if (spawnData == null)
        {
            return;
        }

        if (payload?.CustomData == null)
        {
            CustomDataBySpawnData.Remove(spawnData);
        }
        else
        {
            CustomDataBySpawnData[spawnData] = payload.CustomData;
        }

        if (payload?.CustomObjects == null || payload.CustomObjects.Count == 0)
        {
            ObjectsBySpawnData.Remove(spawnData);
        }
        else
        {
            ObjectsBySpawnData[spawnData] = payload.CustomObjects;
        }
    }

    internal static void InitializeSpawn(SpawnSystem.SpawnData critter, Vector3 spawnPoint)
    {
        if (critter == null || !CustomDataBySpawnData.TryGetValue(critter, out EwdData.DataEntry? data) || data == null)
        {
            return;
        }

        EwdData.DataHelper.Init(critter.m_prefab, spawnPoint, Quaternion.identity, null, data);
    }

    internal static void SpawnObjects(SpawnSystem.SpawnData critter, Vector3 spawnPoint)
    {
        if (critter == null || !ObjectsBySpawnData.TryGetValue(critter, out List<EwdBlueprintObject>? objects) || objects.Count == 0)
        {
            return;
        }

        foreach (EwdBlueprintObject obj in objects)
        {
            if (obj.Chance < 1f && UnityEngine.Random.value > obj.Chance)
            {
                continue;
            }

            EwdSpawn.BPO(obj, spawnPoint, Quaternion.identity, Vector3.one, ObjectDataIdentity, ObjectPrefabIdentity, null);
        }
    }

    private static EwdData.DataEntry? BuildCustomData(SpawnSystem.SpawnData spawnData, CanonicalSpawnSystemEntry entry, string context)
    {
        SpawnSystemModifiersDefinition? modifiers = entry.Modifiers;
        EwdData.DataEntry? dataFromEntry = null;
        if (modifiers?.Data is string dataName && !string.IsNullOrWhiteSpace(dataName))
        {
            dataFromEntry = EwdData.DataHelper.Get(dataName, $"{DropNSpawnPlugin.ModName}_spawnsystem:{context}");
        }

        EwdData.DataEntry? inlineData = null;
        if (modifiers?.Faction is string factionValue && !string.IsNullOrWhiteSpace(factionValue))
        {
            inlineData ??= new EwdData.DataEntry();
            inlineData.Strings ??= new Dictionary<int, EwdData.IStringValue>();
            inlineData.Strings[HashFaction] = EwdData.DataValue.Simple(factionValue);
        }

        Dictionary<string, string>? configuredFields = modifiers?.Fields;
        if (configuredFields != null && configuredFields.Count > 0)
        {
            inlineData ??= new EwdData.DataEntry();
            ExpandWorldFieldOverrideSupport.AddFieldOverrides(inlineData, spawnData.m_prefab, configuredFields);
        }

        return EwdData.DataHelper.Merge(dataFromEntry, inlineData);
    }

    private static List<EwdBlueprintObject>? BuildCustomObjects(List<string>? configuredObjects, string context)
    {
        if (configuredObjects == null || configuredObjects.Count == 0)
        {
            return null;
        }

        List<EwdBlueprintObject> objects = new();
        foreach (string rawDefinition in configuredObjects)
        {
            if (TryParseObjectDefinition(rawDefinition, context, out EwdBlueprintObject? obj))
            {
                objects.Add(obj!);
            }
        }

        return objects.Count == 0 ? null : objects;
    }

    private static bool TryParseObjectDefinition(string? rawDefinition, string context, out EwdBlueprintObject? obj)
    {
        obj = null;
        if (string.IsNullOrWhiteSpace(rawDefinition))
        {
            return false;
        }

        string[] split = rawDefinition!
            .Split(',', StringSplitOptions.None)
            .Select(part => part.Trim())
            .ToArray();
        if (split.Length == 0 || split[0].Length == 0)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Entry '{context}' contains an invalid objects entry '{rawDefinition}'. Expected format: Prefab,posX,posZ,posY,chance,data.");
            return false;
        }

        if (!TryParseObjectFloat(split, 1, 0f, context, rawDefinition, out float posX) ||
            !TryParseObjectFloat(split, 2, 0f, context, rawDefinition, out float posZ) ||
            !TryParseObjectFloat(split, 3, 0f, context, rawDefinition, out float posY) ||
            !TryParseObjectFloat(split, 4, 1f, context, rawDefinition, out float chance))
        {
            return false;
        }

        string dataName = split.Length > 5 ? split[5] : "";
        EwdData.DataEntry? data = EwdData.DataHelper.Get(dataName, $"{DropNSpawnPlugin.ModName}_spawnsystem:{context}");
        obj = new EwdBlueprintObject(split[0], new Vector3(posX, posY, posZ), Quaternion.identity, Vector3.one, data, chance);
        return true;
    }

    private static bool TryParseObjectFloat(string[] split, int index, float defaultValue, string context, string rawDefinition, out float value)
    {
        value = defaultValue;
        if (index >= split.Length || split[index].Length == 0)
        {
            return true;
        }

        if (float.TryParse(split[index], NumberStyles.Any, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Entry '{context}' contains an invalid objects entry '{rawDefinition}'. '{split[index]}' is not a valid number.");
        return false;
    }

    private static EwdData.DataEntry? ObjectDataIdentity(EwdData.DataEntry? data, string prefab)
    {
        return data;
    }

    private static string ObjectPrefabIdentity(string prefab)
    {
        return prefab;
    }
}
