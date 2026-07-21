using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DropNSpawn;

internal static class FactionIntegration
{
    private enum FactionProvider
    {
        Native,
        CreatureManager,
        ExpandWorldFactions
    }

    private readonly struct ResolvedFaction
    {
        internal ResolvedFaction(Character.Faction value, FactionProvider provider)
        {
            Value = value;
            Provider = provider;
        }

        internal Character.Faction Value { get; }
        internal FactionProvider Provider { get; }
    }

    private static readonly string NativeFactionList = string.Join(", ", Enum.GetNames(typeof(Character.Faction)));
    private static readonly object Sync = new();
    private static readonly HashSet<string> WarningCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly int HashFaction = "faction".GetStableHashCode();
    private static readonly AccessTools.FieldRef<BaseAI, ZNetView> BaseAiNviewRef =
        AccessTools.FieldRefAccess<BaseAI, ZNetView>("m_nview");
    private static readonly AccessTools.FieldRef<BaseAI, Character> BaseAiCharacterRef =
        AccessTools.FieldRefAccess<BaseAI, Character>("m_character");

    private static bool _creatureManagerApiResolved;
    private static MethodInfo? _creatureManagerTryResolveMethod;
    private static MethodInfo? _creatureManagerTryApplyMethod;
    private static MethodInfo? _creatureManagerGetNamesMethod;
    private static bool _expandWorldFactionsApiResolved;
    private static MethodInfo? _expandWorldFactionsTryGetFactionMethod;
    private static MethodInfo? _expandWorldFactionsBaseAiSetupMethod;

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
        List<string> names = new(Enum.GetNames(typeof(Character.Faction)));
        HashSet<string> knownNames = new(names, StringComparer.OrdinalIgnoreCase);
        foreach (string name in GetCreatureManagerFactionNames())
        {
            if (knownNames.Add(name))
            {
                names.Add(name);
            }
        }

        return names.Count == Enum.GetNames(typeof(Character.Faction)).Length
            ? NativeFactionList
            : string.Join(", ", names);
    }

    internal static bool Matches(Character.Faction currentFaction, string? configuredFaction)
    {
        string? normalizedFaction = Normalize(configuredFaction);
        return normalizedFaction != null &&
               TryResolveFaction(normalizedFaction, out ResolvedFaction resolvedFaction) &&
               currentFaction == resolvedFaction.Value;
    }

    internal static void Apply(Character? character, string? configuredFaction, string context)
    {
        string? normalizedFaction = Normalize(configuredFaction);
        if (character == null || normalizedFaction == null)
        {
            return;
        }

        if (!TryResolveFaction(normalizedFaction, out ResolvedFaction resolvedFaction))
        {
            WarnOnce(
                $"invalid-faction:{normalizedFaction}",
                $"Entry '{context}' uses invalid faction '{normalizedFaction}'. Use a vanilla Character.Faction value, a CreatureManager registered faction, or an ExpandWorldFactions custom faction name. Available vanilla and CreatureManager names: {GetNativeFactionList()}.");
            return;
        }

        if (resolvedFaction.Provider == FactionProvider.CreatureManager)
        {
            if (TryInvokeCreatureManagerApply(character, normalizedFaction))
            {
                return;
            }

            character.m_faction = resolvedFaction.Value;
            WriteFactionToZdo(character, normalizedFaction, resolvedFaction.Value);
            WarnOnce(
                $"creature-manager-faction-apply:{normalizedFaction}",
                $"CreatureManager resolved faction '{normalizedFaction}' for entry '{context}', but its apply call failed. The faction id was applied without CreatureManager's BaseAI refresh.");
            return;
        }

        character.m_faction = resolvedFaction.Value;
        WriteFactionToZdo(character, normalizedFaction, resolvedFaction.Value);
        RefreshBaseAi(character.GetComponent<BaseAI>(), normalizedFaction, resolvedFaction);
    }

    private static void WriteFactionToZdo(Character character, string configuredFaction, Character.Faction faction)
    {
        ZDO? zdo = character.GetComponent<ZNetView>()?.GetZDO();
        if (zdo != null)
        {
            zdo.Set(HashFaction, configuredFaction);
            zdo.Set(HashFaction, (int)faction);
        }
    }

    internal static void ApplyFromZdo(BaseAI? baseAi)
    {
        if (baseAi == null)
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
            string normalizedFaction = configuredFaction.Trim();
            if (TryResolveFaction(normalizedFaction, out ResolvedFaction resolvedFaction))
            {
                if (resolvedFaction.Provider == FactionProvider.CreatureManager)
                {
                    if (TryInvokeCreatureManagerApply(character, normalizedFaction))
                    {
                        return;
                    }

                    character.m_faction = resolvedFaction.Value;
                    WarnOnce(
                        $"creature-manager-zdo-faction-apply:{normalizedFaction}",
                        $"CreatureManager resolved ZDO faction '{normalizedFaction}', but its apply call failed. The faction id was applied without CreatureManager's BaseAI refresh.");
                    return;
                }

                character.m_faction = resolvedFaction.Value;
                RefreshBaseAi(baseAi, normalizedFaction, resolvedFaction);
                return;
            }
        }

        int factionValue = zdo.GetInt(HashFaction, 0);
        if (factionValue != 0)
        {
            string numericFaction = factionValue.ToString();
            if (TryInvokeCreatureManagerResolve(numericFaction, out Character.Faction creatureManagerFaction))
            {
                if (!TryInvokeCreatureManagerApply(character, numericFaction))
                {
                    character.m_faction = creatureManagerFaction;
                    WarnOnce(
                        $"creature-manager-zdo-faction-apply:{numericFaction}",
                        $"CreatureManager resolved ZDO faction id '{numericFaction}', but its apply call failed. The faction id was applied without CreatureManager's BaseAI refresh.");
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(configuredFaction) || !TryInvokeExpandWorldFactionsSetup(baseAi))
            {
                character.m_faction = (Character.Faction)factionValue;
            }
        }
        else if (!string.IsNullOrWhiteSpace(configuredFaction))
        {
            WarnOnce(
                $"invalid-zdo-faction:{configuredFaction.Trim()}",
                $"Spawned creature contains unknown faction '{configuredFaction.Trim()}' in its ZDO. Available vanilla and CreatureManager names: {GetNativeFactionList()}.");
        }
    }

    private static void RefreshBaseAi(BaseAI? baseAi, string configuredFaction, ResolvedFaction resolvedFaction)
    {
        if (baseAi == null)
        {
            return;
        }

        if (resolvedFaction.Provider == FactionProvider.ExpandWorldFactions ||
            (resolvedFaction.Provider == FactionProvider.Native &&
             TryInvokeExpandWorldFactionsResolve(configuredFaction, out Character.Faction expandWorldFaction) &&
             expandWorldFaction == resolvedFaction.Value))
        {
            TryInvokeExpandWorldFactionsSetup(baseAi);
        }
    }

    private static bool TryResolveFaction(string configuredFaction, out ResolvedFaction resolvedFaction)
    {
        resolvedFaction = default;
        if (TryInvokeCreatureManagerResolve(configuredFaction, out Character.Faction creatureManagerFaction))
        {
            resolvedFaction = new ResolvedFaction(creatureManagerFaction, FactionProvider.CreatureManager);
            return true;
        }

        if (Enum.TryParse(configuredFaction, true, out Character.Faction nativeFaction))
        {
            resolvedFaction = new ResolvedFaction(nativeFaction, FactionProvider.Native);
            return true;
        }

        if (TryInvokeExpandWorldFactionsResolve(configuredFaction, out Character.Faction expandWorldFaction))
        {
            resolvedFaction = new ResolvedFaction(expandWorldFaction, FactionProvider.ExpandWorldFactions);
            return true;
        }

        return false;
    }

    private static bool TryInvokeCreatureManagerResolve(string configuredFaction, out Character.Faction faction)
    {
        faction = default;
        lock (Sync)
        {
            TryResolveCreatureManagerApi();
            if (_creatureManagerTryResolveMethod == null)
            {
                return false;
            }

            try
            {
                object?[] arguments = { configuredFaction, null };
                if (_creatureManagerTryResolveMethod.Invoke(null, arguments) is true &&
                    arguments[1] is Character.Faction resolvedFaction)
                {
                    faction = resolvedFaction;
                    return true;
                }
            }
            catch (Exception ex)
            {
                WarnOnce("creature-manager-faction-resolve-error", $"Failed to resolve CreatureManager faction names. {GetExceptionMessage(ex)}");
            }

            return false;
        }
    }

    private static bool TryInvokeCreatureManagerApply(Character character, string configuredFaction)
    {
        lock (Sync)
        {
            TryResolveCreatureManagerApi();
            if (_creatureManagerTryApplyMethod == null)
            {
                return false;
            }

            try
            {
                return _creatureManagerTryApplyMethod.Invoke(null, new object[] { character, configuredFaction }) is true;
            }
            catch (Exception ex)
            {
                WarnOnce("creature-manager-faction-apply-error", $"Failed to apply a CreatureManager faction. {GetExceptionMessage(ex)}");
                return false;
            }
        }
    }

    private static IEnumerable<string> GetCreatureManagerFactionNames()
    {
        lock (Sync)
        {
            TryResolveCreatureManagerApi();
            if (_creatureManagerGetNamesMethod == null)
            {
                return Array.Empty<string>();
            }

            try
            {
                if (_creatureManagerGetNamesMethod.Invoke(null, Array.Empty<object>()) is IEnumerable<string> names)
                {
                    return new List<string>(names);
                }
            }
            catch (Exception ex)
            {
                WarnOnce("creature-manager-faction-names-error", $"Failed to read CreatureManager faction names. {GetExceptionMessage(ex)}");
            }

            return Array.Empty<string>();
        }
    }

    private static bool TryInvokeExpandWorldFactionsResolve(string configuredFaction, out Character.Faction faction)
    {
        faction = default;
        lock (Sync)
        {
            TryResolveExpandWorldFactionsApi();
            if (_expandWorldFactionsTryGetFactionMethod == null)
            {
                return false;
            }

            try
            {
                object?[] arguments = { configuredFaction, null };
                if (_expandWorldFactionsTryGetFactionMethod.Invoke(null, arguments) is true &&
                    arguments[1] is Character.Faction resolvedFaction)
                {
                    faction = resolvedFaction;
                    return true;
                }
            }
            catch (Exception ex)
            {
                WarnOnce("expand-world-faction-resolve-error", $"Failed to resolve ExpandWorldFactions faction names. {GetExceptionMessage(ex)}");
            }

            return false;
        }
    }

    private static bool TryInvokeExpandWorldFactionsSetup(BaseAI baseAi)
    {
        lock (Sync)
        {
            TryResolveExpandWorldFactionsApi();
            if (_expandWorldFactionsBaseAiSetupMethod == null)
            {
                return false;
            }

            try
            {
                _expandWorldFactionsBaseAiSetupMethod.Invoke(null, new object[] { baseAi });
                return true;
            }
            catch (Exception ex)
            {
                WarnOnce("expand-world-faction-setup-error", $"Failed to refresh ExpandWorldFactions BaseAI state. {GetExceptionMessage(ex)}");
                return false;
            }
        }
    }

    private static void TryResolveCreatureManagerApi()
    {
        if (_creatureManagerApiResolved)
        {
            return;
        }

        _creatureManagerApiResolved = true;
        Type? apiType = SafeTypeLookup.FindLoadedType("CreatureManager.CreatureManagerFactionApi", "CreatureManager");
        if (apiType == null)
        {
            return;
        }

        _creatureManagerTryResolveMethod = apiType.GetMethod(
            "TryResolve",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(string), typeof(Character.Faction).MakeByRefType() },
            null);
        _creatureManagerTryApplyMethod = apiType.GetMethod(
            "TryApply",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Character), typeof(string) },
            null);
        _creatureManagerGetNamesMethod = apiType.GetMethod(
            "GetNames",
            BindingFlags.Public | BindingFlags.Static,
            null,
            Type.EmptyTypes,
            null);
    }

    private static void TryResolveExpandWorldFactionsApi()
    {
        if (_expandWorldFactionsApiResolved)
        {
            return;
        }

        _expandWorldFactionsApiResolved = true;

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
                    _expandWorldFactionsTryGetFactionMethod = method;
                    break;
                }
            }
        }

        Type? baseAiAwakeType = SafeTypeLookup.FindLoadedType("ExpandWorldData.Factions.BaseAIAwake", "ExpandWorldFactions");
        _expandWorldFactionsBaseAiSetupMethod = baseAiAwakeType?.GetMethod("Setup", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(BaseAI) }, null);
    }

    private static string GetExceptionMessage(Exception exception)
    {
        return exception is TargetInvocationException { InnerException: { } innerException }
            ? innerException.Message
            : exception.Message;
    }

    private static void WarnOnce(string key, string message)
    {
        if (WarningCache.Add(key))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogWarning(message);
        }
    }
}
