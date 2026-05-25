using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private sealed class LocationApplyOperations : StandardBaselineDesiredStateOperations<LocationDesiredState>
    {
        public static LocationApplyOperations Instance { get; } = new();

        public override string DomainKey => "location";

        public override BaselineDesiredStateCapabilities Capabilities =>
            BaselineDesiredStateCapabilities.Validation |
            BaselineDesiredStateCapabilities.LiveApply;

        public override void Validate(LocationDesiredState desiredState) => ValidateLocationDesiredState(desiredState);
        public override void ApplyDesiredStateToLive(LocationDesiredState desiredState) => ApplyLocationDesiredStateToLive(desiredState);
        public override void Commit(LocationDesiredState desiredState) => RecordAppliedState(desiredState.GameDataSignature, desiredState.DomainEnabled, desiredState.CurrentEntrySignatures);

        public override void HandleFailure(LocationDesiredState desiredState, StandardApplyFailureContext failureContext)
        {
            if (!failureContext.LiveStageFailed || desiredState.ReloadPrefabs.Count == 0)
            {
                return;
            }

            QueueRegisteredLocationReconciles(desiredState.ReloadPrefabs);
        }
    }

    private sealed class CompiledLocationOfferingBowlPlan
    {
        public LocationOfferingBowlDefinition Definition { get; set; } = new();
    }

    private sealed class CompiledLocationItemStandPlan
    {
        public LocationItemStandDefinition Definition { get; set; } = new();
        public string Path { get; set; } = "";
        public bool HasPath { get; set; }
    }

    private sealed class CompiledLocationVegvisirPlan
    {
        public LocationVegvisirDefinition Definition { get; set; } = new();
    }

    private sealed class CompiledLocationRunestonePlan
    {
        public LocationRunestoneDefinition Definition { get; set; } = new();
    }

    private sealed class CompiledLocationEntryPlan
    {
        public ConditionsDefinition? Conditions { get; set; }
        public bool HasConditions { get; set; }
        public CompiledLocationOfferingBowlPlan? OfferingBowl { get; set; }
        public List<CompiledLocationItemStandPlan> ItemStands { get; } = new();
        public List<CompiledLocationVegvisirPlan> Vegvisirs { get; } = new();
        public List<CompiledLocationRunestonePlan> Runestones { get; } = new();
    }

    private sealed class CompiledLocationPrefabPlan
    {
        public List<CompiledLocationEntryPlan> ActiveEntryPlans { get; } = new();
        public List<CompiledLocationEntryPlan> LooseItemStandPlans { get; } = new();
    }

    private sealed class LocationRuntimeConfigurationState
    {
        public static LocationRuntimeConfigurationState Empty { get; } = new();
        public Dictionary<string, CompiledLocationPrefabPlan> PlansByPrefab { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LocationDesiredState
    {
        public StandardDomainApplyPlan ApplyPlan { get; set; }
        public int GameDataSignature { get; set; }
        public Dictionary<string, string> CurrentEntrySignatures { get; set; } = EmptyEntrySignatures;
        public bool DomainEnabled { get; set; }
        public bool QueueLiveReconcile { get; set; }
        public HashSet<string> ReloadPrefabs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public LocationRuntimeConfigurationState RuntimeConfigurationState { get; set; } = LocationRuntimeConfigurationState.Empty;
    }

    private static LocationRuntimeConfigurationState _runtimeConfigurationState = LocationRuntimeConfigurationState.Empty;
    private static int? _runtimeConfigurationGameDataSignature;
    private static string _runtimeConfigurationSignature = "";

    private static void RunApplyCoordinator(
        int gameDataSignature,
        bool domainEnabled,
        Dictionary<string, string> currentEntrySignatures,
        bool queueLiveReconcile)
    {
        LocationDesiredState desiredState = BuildLocationDesiredState(gameDataSignature, domainEnabled, currentEntrySignatures, queueLiveReconcile);
        StandardApplyOutcome outcome = StandardBaselineDesiredStateCoordinator.Run(
            desiredState.ApplyPlan,
            desiredState,
            LocationApplyOperations.Instance);
        if (!outcome.Success)
        {
            return;
        }
    }

    private static LocationDesiredState BuildLocationDesiredState(
        int gameDataSignature,
        bool domainEnabled,
        Dictionary<string, string> currentEntrySignatures,
        bool queueLiveReconcile)
    {
        StandardDomainApplyPlan applyPlan = StandardDomainApplySupport.BuildPlan(
            _lastAppliedGameDataSignature,
            gameDataSignature,
            _lastAppliedDomainEnabled,
            domainEnabled,
            _lastAppliedEntrySignaturesByPrefab,
            currentEntrySignatures,
            EmptyEntrySignatures,
            canUseTargetedLiveReload: _lastAppliedGameDataSignature == gameDataSignature &&
                                      _lastAppliedDomainEnabled == true);
        if (domainEnabled)
        {
            EnsureRuntimeConfigurationState();
        }

        return new LocationDesiredState
        {
            GameDataSignature = gameDataSignature,
            ApplyPlan = applyPlan,
            CurrentEntrySignatures = currentEntrySignatures,
            DomainEnabled = domainEnabled,
            QueueLiveReconcile = queueLiveReconcile,
            ReloadPrefabs = applyPlan.DirtyKeys ?? BuildRegisteredCatchupPrefabs(domainEnabled, currentEntrySignatures),
            RuntimeConfigurationState = domainEnabled ? _runtimeConfigurationState : LocationRuntimeConfigurationState.Empty
        };
    }

    private static void EnsureRuntimeConfigurationState()
    {
        if (!IsGameDataReady())
        {
            return;
        }

        int gameDataSignature = ComputeGameDataSignature();
        if (_runtimeConfigurationGameDataSignature == gameDataSignature &&
            string.Equals(_runtimeConfigurationSignature, _configurationSignature, StringComparison.Ordinal))
        {
            return;
        }

        _runtimeConfigurationState = BuildRuntimeConfigurationState();
        _runtimeConfigurationGameDataSignature = gameDataSignature;
        _runtimeConfigurationSignature = _configurationSignature;
    }

    private static LocationRuntimeConfigurationState BuildRuntimeConfigurationState()
    {
        LocationRuntimeConfigurationState state = new();

        foreach ((string prefabName, List<LocationConfigurationEntry> entries) in ActiveEntriesByPrefab)
        {
            List<CompiledLocationEntryPlan> compiledPlans = BuildCompiledLocationEntryPlans(entries, itemStandOnly: false);
            if (compiledPlans.Count > 0)
            {
                GetOrCreateCompiledLocationPrefabPlan(state, prefabName).ActiveEntryPlans.AddRange(compiledPlans);
            }
        }

        foreach ((string prefabName, List<LocationConfigurationEntry> entries) in LooseItemStandEntriesByPrefab)
        {
            List<CompiledLocationEntryPlan> compiledPlans = BuildCompiledLocationEntryPlans(entries, itemStandOnly: true);
            if (compiledPlans.Count > 0)
            {
                GetOrCreateCompiledLocationPrefabPlan(state, prefabName).LooseItemStandPlans.AddRange(compiledPlans);
            }
        }

        return state;
    }

    private static CompiledLocationPrefabPlan GetOrCreateCompiledLocationPrefabPlan(LocationRuntimeConfigurationState state, string prefabName)
    {
        if (!state.PlansByPrefab.TryGetValue(prefabName, out CompiledLocationPrefabPlan? prefabPlan))
        {
            prefabPlan = new CompiledLocationPrefabPlan();
            state.PlansByPrefab[prefabName] = prefabPlan;
        }

        return prefabPlan;
    }

    private static List<CompiledLocationEntryPlan> BuildCompiledLocationEntryPlans(
        IEnumerable<LocationConfigurationEntry> entries,
        bool itemStandOnly)
    {
        List<CompiledLocationEntryPlan> compiledPlans = new();
        foreach (LocationConfigurationEntry entry in entries)
        {
            if (TryBuildCompiledLocationEntryPlan(entry, itemStandOnly, out CompiledLocationEntryPlan? compiledPlan))
            {
                compiledPlans.Add(compiledPlan!);
            }
        }

        return compiledPlans;
    }

    private static bool TryBuildCompiledLocationEntryPlan(
        LocationConfigurationEntry entry,
        bool itemStandOnly,
        out CompiledLocationEntryPlan? compiledPlan)
    {
        compiledPlan = new CompiledLocationEntryPlan
        {
            Conditions = entry.Conditions,
            HasConditions = HasConditions(entry.Conditions)
        };

        if (!itemStandOnly &&
            entry.OfferingBowl != null &&
            HasOfferingBowlOverride(entry.OfferingBowl))
        {
            compiledPlan.OfferingBowl = new CompiledLocationOfferingBowlPlan
            {
                Definition = entry.OfferingBowl
            };
        }

        if (entry.ItemStands != null)
        {
            foreach (LocationItemStandDefinition definition in entry.ItemStands)
            {
                bool includeDefinition = itemStandOnly
                    ? HasLooseItemStandOverride(definition)
                    : HasItemStandOverride(definition);
                if (!includeDefinition)
                {
                    continue;
                }

                string trimmedPath = (definition.Path ?? "").Trim();
                compiledPlan.ItemStands.Add(new CompiledLocationItemStandPlan
                {
                    Definition = definition,
                    Path = trimmedPath,
                    HasPath = trimmedPath.Length > 0
                });
            }
        }

        if (!itemStandOnly && entry.Vegvisirs != null)
        {
            foreach (LocationVegvisirDefinition definition in entry.Vegvisirs)
            {
                if (!HasVegvisirOverride(definition))
                {
                    continue;
                }

                compiledPlan.Vegvisirs.Add(new CompiledLocationVegvisirPlan
                {
                    Definition = definition
                });
            }
        }

        if (!itemStandOnly && entry.Runestones != null)
        {
            foreach (LocationRunestoneDefinition definition in entry.Runestones)
            {
                if (!HasRunestoneOverride(definition))
                {
                    continue;
                }

                compiledPlan.Runestones.Add(new CompiledLocationRunestonePlan
                {
                    Definition = definition
                });
            }
        }

        if (compiledPlan.OfferingBowl == null &&
            compiledPlan.ItemStands.Count == 0 &&
            compiledPlan.Vegvisirs.Count == 0 &&
            compiledPlan.Runestones.Count == 0)
        {
            compiledPlan = null;
            return false;
        }

        return true;
    }

    private static void ResetLocationRuntimeConfigurationState()
    {
        _runtimeConfigurationState = LocationRuntimeConfigurationState.Empty;
        _runtimeConfigurationGameDataSignature = null;
        _runtimeConfigurationSignature = "";
    }

    private static void ValidateLocationDesiredState(LocationDesiredState desiredState)
    {
        ValidateConfiguredPrefabs();
    }

    private static void ApplyLocationDesiredStateToLive(LocationDesiredState desiredState)
    {
        if (desiredState.ReloadPrefabs.Count == 0)
        {
            return;
        }

        if (desiredState.QueueLiveReconcile)
        {
            QueueRegisteredLocationReconciles(desiredState.ReloadPrefabs);
        }
        else
        {
            ReapplyActiveEntriesToRegisteredLocations(desiredState.ReloadPrefabs);
        }
    }
}
