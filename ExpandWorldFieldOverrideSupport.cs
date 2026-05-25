extern alias ewd;

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using EwdData = ewd::Data;

namespace DropNSpawn;

internal static class ExpandWorldFieldOverrideSupport
{
    private static readonly int HashDamage = "damage".GetStableHashCode();
    private static readonly HashSet<int> KnownFloatHashes = new()
    {
        ZDOVars.s_randomSkillFactor,
        HashDamage,
        ZDOVars.s_health,
        ZDOVars.s_maxHealth,
        ZDOVars.s_noise
    };

    private static readonly HashSet<int> KnownIntHashes = new()
    {
        ZDOVars.s_level,
        ZDOVars.s_seed,
        ZDOVars.s_lovePoints
    };

    private static readonly HashSet<int> KnownLongHashes = new()
    {
        ZDOVars.s_spawnTime,
        ZDOVars.s_worldTimeHash,
        ZDOVars.s_pregnant
    };

    private static readonly HashSet<int> KnownBoolHashes = new()
    {
        "bosscount".GetStableHashCode(),
        ZDOVars.s_isBlockingHash,
        ZDOVars.s_tamed,
        ZDOVars.s_aggravated,
        ZDOVars.s_alert,
        ZDOVars.s_shownAlertMessage,
        ZDOVars.s_huntPlayer,
        ZDOVars.s_patrol,
        ZDOVars.s_despawnInDay,
        ZDOVars.s_eventCreature,
        ZDOVars.s_sleeping,
        ZDOVars.s_haveSaddleHash
    };

    private static readonly HashSet<int> KnownVectorHashes = new()
    {
        ZDOVars.s_bodyVelocity,
        ZDOVars.s_spawnPoint,
        ZDOVars.s_patrolPoint
    };

    private static readonly HashSet<int> KnownStringHashes = new()
    {
        ZDOVars.s_tamedName,
        ZDOVars.s_tamedNameAuthor
    };

    internal static void AddFieldOverrides(EwdData.DataEntry customData, GameObject prefab, Dictionary<string, string> configuredFields)
    {
        Dictionary<string, string> otherFields = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Dictionary<string, string>> componentFields = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string rawKey, string rawValue) in configuredFields)
        {
            string key = (rawKey ?? "").Trim();
            string value = rawValue ?? "";
            if (key.Length == 0)
            {
                continue;
            }

            int hash = key.GetStableHashCode();
            if (TryInsertKnownOverride(customData, hash, value))
            {
                continue;
            }

            string[] split = key.Split(new[] { '.' }, 2);
            if (split.Length == 2)
            {
                if (TryInsertTypedOverride(customData, split[0], split[1], value))
                {
                    continue;
                }

                if (!componentFields.TryGetValue(split[0], out Dictionary<string, string>? values))
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    componentFields[split[0]] = values;
                }

                values[split[1]] = value;
                values[$"m_{split[1]}"] = value;
                continue;
            }

            otherFields[key] = value;
            otherFields[$"m_{key}"] = value;
        }

        prefab.GetComponentsInChildren(ZNetView.m_tempComponents);
        foreach (Component component in ZNetView.m_tempComponents)
        {
            Type componentType = component.GetType();
            string componentTypeName = componentType.Name;
            FieldInfo[] fields = componentType.GetFields(BindingFlags.Instance | BindingFlags.Public);
            foreach (FieldInfo field in fields)
            {
                if (componentFields.TryGetValue(componentTypeName, out Dictionary<string, string>? componentValues) &&
                    componentValues.TryGetValue(field.Name, out string? componentValue))
                {
                    InsertReflectedField(customData, component, field, componentValue);
                }

                if (otherFields.TryGetValue(field.Name, out string? value))
                {
                    InsertReflectedField(customData, component, field, value);
                }
            }
        }

        ZNetView.m_tempComponents.Clear();
    }

    private static bool TryInsertKnownOverride(EwdData.DataEntry customData, int hash, string value)
    {
        if (KnownFloatHashes.Contains(hash))
        {
            customData.Floats ??= new Dictionary<int, EwdData.IFloatValue>();
            if (hash == HashDamage)
            {
                hash = ZDOVars.s_randomSkillFactor;
            }

            customData.Floats[hash] = EwdData.DataValue.Float(value);
            return true;
        }

        if (KnownIntHashes.Contains(hash))
        {
            customData.Ints ??= new Dictionary<int, EwdData.IIntValue>();
            customData.Ints[hash] = EwdData.DataValue.Int(value);
            return true;
        }

        if (KnownLongHashes.Contains(hash))
        {
            customData.Longs ??= new Dictionary<int, EwdData.ILongValue>();
            customData.Longs[hash] = EwdData.DataValue.Long(value);
            return true;
        }

        if (KnownBoolHashes.Contains(hash))
        {
            customData.Bools ??= new Dictionary<int, EwdData.IBoolValue>();
            customData.Bools[hash] = EwdData.DataValue.Bool(value);
            return true;
        }

        if (KnownVectorHashes.Contains(hash))
        {
            customData.Vecs ??= new Dictionary<int, EwdData.IVector3Value>();
            customData.Vecs[hash] = EwdData.DataValue.Vector3(value);
            return true;
        }

        if (KnownStringHashes.Contains(hash))
        {
            customData.Strings ??= new Dictionary<int, EwdData.IStringValue>();
            customData.Strings[hash] = EwdData.DataValue.String(value);
            return true;
        }

        return false;
    }

    private static bool TryInsertTypedOverride(EwdData.DataEntry customData, string rawPrefix, string rawKey, string value)
    {
        string key = (rawKey ?? "").Trim();
        if (key.Length == 0)
        {
            return false;
        }

        int hash = key.GetStableHashCode();
        switch ((rawPrefix ?? "").Trim().ToLowerInvariant())
        {
            case "int":
                customData.Ints ??= new Dictionary<int, EwdData.IIntValue>();
                customData.Ints[hash] = EwdData.DataValue.Int(value);
                return true;
            case "float":
                customData.Floats ??= new Dictionary<int, EwdData.IFloatValue>();
                customData.Floats[hash] = EwdData.DataValue.Float(value);
                return true;
            case "bool":
                customData.Bools ??= new Dictionary<int, EwdData.IBoolValue>();
                customData.Bools[hash] = EwdData.DataValue.Bool(value);
                return true;
            case "long":
                customData.Longs ??= new Dictionary<int, EwdData.ILongValue>();
                customData.Longs[hash] = EwdData.DataValue.Long(value);
                return true;
            case "vec":
                customData.Vecs ??= new Dictionary<int, EwdData.IVector3Value>();
                customData.Vecs[hash] = EwdData.DataValue.Vector3(value);
                return true;
            case "quat":
                customData.Quats ??= new Dictionary<int, EwdData.IQuaternionValue>();
                customData.Quats[hash] = EwdData.DataValue.Quaternion(value);
                return true;
            case "string":
                customData.Strings ??= new Dictionary<int, EwdData.IStringValue>();
                customData.Strings[hash] = EwdData.DataValue.String(value);
                return true;
            default:
                return false;
        }
    }

    private static void InsertReflectedField(EwdData.DataEntry customData, Component component, FieldInfo field, string value)
    {
        int key = $"{component.GetType().Name}.{field.Name}".GetStableHashCode();
        customData.Ints ??= new Dictionary<int, EwdData.IIntValue>();
        customData.Ints["HasFields".GetStableHashCode()] = EwdData.DataValue.Simple(1);
        customData.Ints[$"HasFields{component.GetType().Name}".GetStableHashCode()] = EwdData.DataValue.Simple(1);

        if (field.FieldType == typeof(int))
        {
            customData.Ints[key] = EwdData.DataValue.Int(value);
        }
        else if (field.FieldType == typeof(float))
        {
            customData.Floats ??= new Dictionary<int, EwdData.IFloatValue>();
            customData.Floats[key] = EwdData.DataValue.Float(value);
        }
        else if (field.FieldType == typeof(bool))
        {
            customData.Bools ??= new Dictionary<int, EwdData.IBoolValue>();
            customData.Bools[key] = EwdData.DataValue.Bool(value);
        }
        else if (field.FieldType == typeof(long))
        {
            customData.Longs ??= new Dictionary<int, EwdData.ILongValue>();
            customData.Longs[key] = EwdData.DataValue.Long(value);
        }
        else if (field.FieldType == typeof(Vector3))
        {
            customData.Vecs ??= new Dictionary<int, EwdData.IVector3Value>();
            customData.Vecs[key] = EwdData.DataValue.Vector3(value);
        }
        else if (field.FieldType == typeof(Quaternion))
        {
            customData.Quats ??= new Dictionary<int, EwdData.IQuaternionValue>();
            customData.Quats[key] = EwdData.DataValue.Quaternion(value);
        }
        else
        {
            customData.Strings ??= new Dictionary<int, EwdData.IStringValue>();
            customData.Strings[key] = EwdData.DataValue.String(value);
        }
    }
}
