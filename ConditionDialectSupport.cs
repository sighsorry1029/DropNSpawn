using System;

namespace DropNSpawn;

internal static class ConditionDialectSupport
{
    private const string CharacterConditionLevelReason = "level filters are only valid for character conditions";
    private const string CharacterConditionStateReason = "state filters are only valid for character conditions";
    private const string CharacterConditionFactionReason = "faction filters are only valid for character conditions";
    private const string CharacterDropLevelReason = "level filters are only valid for character-drop conditions";
    private const string CharacterDropStateReason = "state filters are only valid for character-drop conditions";
    private const string CharacterDropFactionReason = "faction filters are only valid for character-drop conditions";

    internal static void StripUnsupportedLocationComponentFields(
        ConditionsDefinition? conditions,
        string context,
        string conditionPath,
        Action<string> warn)
    {
        if (conditions == null)
        {
            return;
        }

        StripLevel(conditions, context, conditionPath, CharacterConditionLevelReason, warn);
        StripTimeOfDay(conditions, context, conditionPath, "location conditions are evaluated only when the location is loaded or reconciled", warn);
        StripRequiredEnvironments(conditions, context, conditionPath, "location conditions are evaluated only when the location is loaded or reconciled", warn);
        StripRequiredGlobalKeys(conditions, context, conditionPath, "location conditions are static location filters only", warn);
        StripForbiddenGlobalKeys(conditions, context, conditionPath, "location conditions are static location filters only", warn);
        StripStates(conditions, context, conditionPath, CharacterConditionStateReason, warn);
        StripFactions(conditions, context, conditionPath, CharacterConditionFactionReason, warn);
        StripInsidePlayerBase(conditions, context, conditionPath, "location conditions are static location filters only", warn);
    }

    internal static void StripUnsupportedObjectDropFields(
        ConditionsDefinition? conditions,
        string context,
        Action<string> warn)
    {
        if (conditions == null)
        {
            return;
        }

        const string conditionPath = "conditions";
        StripLevel(conditions, context, conditionPath, CharacterDropLevelReason, warn);
        StripStates(conditions, context, conditionPath, CharacterDropStateReason, warn);
        StripFactions(conditions, context, conditionPath, CharacterDropFactionReason, warn);
    }

    internal static void StripUnsupportedSpawnerTargetFields(
        ConditionsDefinition? conditions,
        string context,
        bool allowCreatureSpawnerRuntimeOverlapKeys,
        Action<string> warn)
    {
        if (conditions == null)
        {
            return;
        }

        const string conditionPath = "conditions";
        StripLocations(conditions, context, conditionPath, "spawner entries use the top-level locations selector", warn);
        StripLevel(conditions, context, conditionPath, "level filters are not supported for spawner target conditions", warn);
        StripStates(conditions, context, conditionPath, CharacterDropStateReason, warn);
        StripFactions(conditions, context, conditionPath, CharacterDropFactionReason, warn);

        if (allowCreatureSpawnerRuntimeOverlapKeys)
        {
            return;
        }

        StripTimeOfDay(conditions, context, conditionPath, "creatureSpawner uses creatureSpawner.timeOfDay for runtime time-of-day gating", warn);
        StripInsidePlayerBase(conditions, context, conditionPath, "creatureSpawner uses allowInsidePlayerBase for runtime player-base gating", warn);
        StripRequiredGlobalKeys(conditions, context, conditionPath, "creatureSpawner uses requiredGlobalKey for runtime global-key gating", warn);
        StripForbiddenGlobalKeys(conditions, context, conditionPath, "creatureSpawner uses blockingGlobalKey for runtime global-key blocking", warn);
    }

    internal static void StripUnsupportedCreatureSpawnerEntryFields(
        ConditionsDefinition? conditions,
        string context,
        Action<string> warn)
    {
        if (conditions == null)
        {
            return;
        }

        const string conditionPath = "conditions";
        StripTimeOfDay(conditions, context, conditionPath, "creatureSpawner uses creatureSpawner.timeOfDay for runtime time-of-day gating", warn);
        StripInsidePlayerBase(conditions, context, conditionPath, "creatureSpawner does not support inside-only top-level player-base gating. Use allowInsidePlayerBase for the runtime permission flag instead", warn);
        StripRequiredGlobalKeys(conditions, context, conditionPath, "creatureSpawner uses requiredGlobalKey for runtime global-key gating", warn);
        StripForbiddenGlobalKeys(conditions, context, conditionPath, "creatureSpawner uses blockingGlobalKey for runtime global-key blocking", warn);
    }

    private static void StripLevel(
        ConditionsDefinition conditions,
        string context,
        string conditionPath,
        string reason,
        Action<string> warn)
    {
        if (conditions.Level?.HasValues() != true &&
            !conditions.MinLevel.HasValue &&
            !conditions.MaxLevel.HasValue)
        {
            return;
        }

        WarnUnsupported(context, conditionPath, "level", reason, warn);
        conditions.Level = null;
        conditions.MinLevel = null;
        conditions.MaxLevel = null;
    }

    private static void StripLocations(
        ConditionsDefinition conditions,
        string context,
        string conditionPath,
        string reason,
        Action<string> warn)
    {
        if (conditions.Locations == null || conditions.Locations.Count == 0)
        {
            return;
        }

        WarnUnsupported(context, conditionPath, "locations", reason, warn);
        conditions.Locations = null;
    }

    private static void StripTimeOfDay(
        ConditionsDefinition conditions,
        string context,
        string conditionPath,
        string reason,
        Action<string> warn)
    {
        if (conditions.TimeOfDay == null)
        {
            return;
        }

        WarnUnsupported(context, conditionPath, "timeOfDay", reason, warn);
        conditions.TimeOfDay = null;
    }

    private static void StripRequiredEnvironments(
        ConditionsDefinition conditions,
        string context,
        string conditionPath,
        string reason,
        Action<string> warn)
    {
        if (conditions.RequiredEnvironments == null || conditions.RequiredEnvironments.Count == 0)
        {
            return;
        }

        WarnUnsupported(context, conditionPath, "requiredEnvironments", reason, warn);
        conditions.RequiredEnvironments = null;
    }

    private static void StripRequiredGlobalKeys(
        ConditionsDefinition conditions,
        string context,
        string conditionPath,
        string reason,
        Action<string> warn)
    {
        if (conditions.RequiredGlobalKeys == null || conditions.RequiredGlobalKeys.Count == 0)
        {
            return;
        }

        WarnUnsupported(context, conditionPath, "requiredGlobalKeys", reason, warn);
        conditions.RequiredGlobalKeys = null;
    }

    private static void StripForbiddenGlobalKeys(
        ConditionsDefinition conditions,
        string context,
        string conditionPath,
        string reason,
        Action<string> warn)
    {
        if (conditions.ForbiddenGlobalKeys == null || conditions.ForbiddenGlobalKeys.Count == 0)
        {
            return;
        }

        WarnUnsupported(context, conditionPath, "forbiddenGlobalKeys", reason, warn);
        conditions.ForbiddenGlobalKeys = null;
    }

    private static void StripStates(
        ConditionsDefinition conditions,
        string context,
        string conditionPath,
        string reason,
        Action<string> warn)
    {
        if (conditions.States == null || conditions.States.Count == 0)
        {
            return;
        }

        WarnUnsupported(context, conditionPath, "states", reason, warn);
        conditions.States = null;
    }

    private static void StripFactions(
        ConditionsDefinition conditions,
        string context,
        string conditionPath,
        string reason,
        Action<string> warn)
    {
        if (conditions.Factions == null || conditions.Factions.Count == 0)
        {
            return;
        }

        WarnUnsupported(context, conditionPath, "factions", reason, warn);
        conditions.Factions = null;
    }

    private static void StripInsidePlayerBase(
        ConditionsDefinition conditions,
        string context,
        string conditionPath,
        string reason,
        Action<string> warn)
    {
        if (!conditions.InsidePlayerBase.HasValue)
        {
            return;
        }

        WarnUnsupported(context, conditionPath, "insidePlayerBase", reason, warn);
        conditions.InsidePlayerBase = null;
    }

    private static void WarnUnsupported(
        string context,
        string conditionPath,
        string fieldName,
        string reason,
        Action<string> warn)
    {
        warn($"Entry '{context}' uses {conditionPath}.{fieldName}, but {reason}. The key was ignored.");
    }
}
