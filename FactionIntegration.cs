using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DropNSpawn;

internal static class FactionIntegration
{
    private static readonly string NativeFactionList = string.Join(", ", Enum.GetNames(typeof(Character.Faction)));
    private static readonly object Sync = new();
    private static readonly HashSet<string> WarningCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly int HashFaction = "faction".GetStableHashCode();
    private static readonly AccessTools.FieldRef<BaseAI, ZNetView> BaseAiNviewRef =
        AccessTools.FieldRefAccess<BaseAI, ZNetView>("m_nview");
    private static readonly AccessTools.FieldRef<BaseAI, Character> BaseAiCharacterRef =
        AccessTools.FieldRefAccess<BaseAI, Character>("m_character");

    private static bool _resolved;
    private static MethodInfo? _tryGetFactionMethod;
    private static MethodInfo? _baseAiSetupMethod;

    internal static bool HasFaction(string? configuredFaction)
    {
        return !string.IsNullOrWhiteSpace(configuredFaction);
    }

    internal static string? Normalize(string? configuredFaction)
    {
        string trimmed = (configuredFaction ?? "").Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    internal static string GetNativeFactionList()
    {
        return NativeFactionList;
    }

    internal static bool Matches(Character.Faction currentFaction, string? configuredFaction)
    {
        string? normalizedFaction = Normalize(configuredFaction);
        return normalizedFaction != null &&
               TryResolveFaction(normalizedFaction, out Character.Faction resolvedFaction) &&
               currentFaction == resolvedFaction;
    }

    internal static void Apply(Character? character, string? configuredFaction, string context)
    {
        string? normalizedFaction = Normalize(configuredFaction);
        if (character == null || normalizedFaction == null)
        {
            return;
        }

        if (!TryResolveFaction(normalizedFaction, out Character.Faction faction))
        {
            WarnOnce(
                $"invalid-faction:{normalizedFaction}",
                $"Entry '{context}' uses invalid faction '{normalizedFaction}'. Use a vanilla Character.Faction value or an ExpandWorldFactions custom faction name.");
            return;
        }

        character.m_faction = faction;

        ZDO? zdo = character.GetComponent<ZNetView>()?.GetZDO();
        if (zdo != null)
        {
            zdo.Set(HashFaction, normalizedFaction);
            zdo.Set(HashFaction, (int)faction);
        }

        RefreshBaseAi(character.GetComponent<BaseAI>());
    }

    internal static void ApplyFromZdo(BaseAI? baseAi)
    {
        if (baseAi == null)
        {
            return;
        }

        if (TryInvokeExpandWorldFactionsSetup(baseAi))
        {
            return;
        }

        Character? character = BaseAiCharacterRef(baseAi);
        if (character == null)
        {
            return;
        }

        ZDO? zdo = BaseAiNviewRef(baseAi)?.GetZDO();
        if (zdo == null)
        {
            return;
        }

        string configuredFaction = zdo.GetString(HashFaction, "");
        if (!string.IsNullOrWhiteSpace(configuredFaction))
        {
            if (TryResolveFaction(configuredFaction, out Character.Faction faction))
            {
                character.m_faction = faction;
            }

            return;
        }

        int factionValue = zdo.GetInt(HashFaction, 0);
        if (factionValue != 0)
        {
            character.m_faction = (Character.Faction)factionValue;
        }
    }

    private static void RefreshBaseAi(BaseAI? baseAi)
    {
        if (baseAi == null)
        {
            return;
        }

        TryInvokeExpandWorldFactionsSetup(baseAi);
    }

    private static bool TryResolveFaction(string configuredFaction, out Character.Faction faction)
    {
        faction = default;
        if (Enum.TryParse(configuredFaction, true, out faction))
        {
            return true;
        }

        lock (Sync)
        {
            TryResolveApi();
            if (_tryGetFactionMethod == null)
            {
                return false;
            }

            try
            {
                object?[] arguments = { configuredFaction, null };
                if (_tryGetFactionMethod.Invoke(null, arguments) is true &&
                    arguments[1] is Character.Faction resolvedFaction)
                {
                    faction = resolvedFaction;
                    return true;
                }
            }
            catch (Exception ex)
            {
                WarnOnce("faction-resolve-error", $"Failed to resolve ExpandWorldFactions faction names. {ex.Message}");
            }

            return false;
        }
    }

    private static bool TryInvokeExpandWorldFactionsSetup(BaseAI baseAi)
    {
        lock (Sync)
        {
            TryResolveApi();
            if (_baseAiSetupMethod == null)
            {
                return false;
            }

            try
            {
                _baseAiSetupMethod.Invoke(null, new object[] { baseAi });
                return true;
            }
            catch (Exception ex)
            {
                WarnOnce("faction-setup-error", $"Failed to refresh ExpandWorldFactions BaseAI state. {ex.Message}");
                return false;
            }
        }
    }

    private static void TryResolveApi()
    {
        if (_resolved)
        {
            return;
        }

        _resolved = true;

        Type? factionManagerType = SafeTypeLookup.FindLoadedType("ExpandWorldData.Factions.FactionManager", "ExpandWorldFactions");
        if (factionManagerType != null)
        {
            foreach (MethodInfo method in factionManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "TryGetFaction")
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(string) &&
                    parameters[1].IsOut)
                {
                    _tryGetFactionMethod = method;
                    break;
                }
            }
        }

        Type? baseAiAwakeType = SafeTypeLookup.FindLoadedType("ExpandWorldData.Factions.BaseAIAwake", "ExpandWorldFactions");
        _baseAiSetupMethod = baseAiAwakeType?.GetMethod("Setup", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BaseAI) }, null);
    }

    private static void WarnOnce(string key, string message)
    {
        if (WarningCache.Add(key))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(message);
        }
    }
}
