extern alias ewd;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using EwdData = ewd::Data;
using EwdBlueprintObject = ewd::ExpandWorldData.BlueprintObject;
using EwdSpawn = ewd::ExpandWorldData.Spawn;

namespace DropNSpawn;

internal sealed class ExpandWorldSpawnDataPayload
{
    internal ExpandWorldSpawnDataPayload(EwdData.DataEntry? data, List<EwdBlueprintObject>? objects)
    {
        Data = data;
        Objects = objects;
    }

    internal EwdData.DataEntry? Data { get; }
    internal List<EwdBlueprintObject>? Objects { get; }
    internal bool HasData => Data != null;
    internal bool HasObjects => Objects is { Count: > 0 };
}

internal static class ExpandWorldSpawnDataSupport
{
    internal static ExpandWorldSpawnDataPayload? BuildPayload(GameObject? prefab, string? dataName, Dictionary<string, string>? fields, List<string>? objects, string context)
    {
        EwdData.DataEntry? data = BuildCustomData(prefab, dataName, fields, context);
        List<EwdBlueprintObject>? customObjects = BuildCustomObjects(objects, context);
        if (data == null && (customObjects == null || customObjects.Count == 0))
        {
            return null;
        }

        return new ExpandWorldSpawnDataPayload(data, customObjects);
    }

    internal static void InitializeSpawn(GameObject? prefab, Vector3 spawnPoint, ExpandWorldSpawnDataPayload? payload)
    {
        if (prefab == null || payload?.Data == null)
        {
            return;
        }

        EwdData.DataHelper.Init(prefab, spawnPoint, Quaternion.identity, null, payload.Data);
    }

    internal static void SpawnObjects(Vector3 spawnPoint, ExpandWorldSpawnDataPayload? payload)
    {
        if (payload?.Objects == null || payload.Objects.Count == 0)
        {
            return;
        }

        foreach (EwdBlueprintObject obj in payload.Objects)
        {
            if (obj.Chance < 1f && UnityEngine.Random.value > obj.Chance)
            {
                continue;
            }

            EwdSpawn.BPO(obj, spawnPoint, Quaternion.identity, Vector3.one, ObjectDataIdentity, ObjectPrefabIdentity, null);
        }
    }

    private static EwdData.DataEntry? BuildCustomData(GameObject? prefab, string? dataName, Dictionary<string, string>? fields, string context)
    {
        EwdData.DataEntry? dataFromEntry = null;
        if (!string.IsNullOrWhiteSpace(dataName))
        {
            string normalizedName = dataName!.Trim();
            dataFromEntry = EwdData.DataHelper.Get(normalizedName, $"{DropNSpawnPlugin.ModName}_spawn:{context}");
        }

        Dictionary<string, string>? normalizedFields = NormalizeConfiguredFields(fields);
        if (normalizedFields == null)
        {
            return dataFromEntry;
        }

        if (prefab == null)
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"Entry '{context}' configured fields but no spawn prefab was available. The fields block was ignored.");
            return dataFromEntry;
        }

        EwdData.DataEntry inlineData = new();
        ExpandWorldFieldOverrideSupport.AddFieldOverrides(inlineData, prefab, normalizedFields);
        return EwdData.DataHelper.Merge(dataFromEntry, inlineData);
    }

    private static Dictionary<string, string>? NormalizeConfiguredFields(Dictionary<string, string>? values)
    {
        if (values == null)
        {
            return null;
        }

        Dictionary<string, string> normalized = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string rawKey, string rawValue) in values)
        {
            string key = (rawKey ?? "").Trim();
            if (key.Length == 0)
            {
                continue;
            }

            normalized[key] = (rawValue ?? "").Trim();
        }

        return normalized.Count == 0 ? null : normalized;
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
        EwdData.DataEntry? data = EwdData.DataHelper.Get(dataName, $"{DropNSpawnPlugin.ModName}_spawn:{context}");
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
