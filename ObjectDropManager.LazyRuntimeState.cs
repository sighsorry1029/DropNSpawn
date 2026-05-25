using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static readonly ObjectLazyRuntimeState LazyRuntimeState = new();

    private sealed class EventDropTableStateSet
    {
        private readonly Dictionary<LiveObjectComponentKind, CachedEventDropTableState> _states = new();

        public CachedEventDropTableState GetOrCreate(LiveObjectComponentKind componentKind)
        {
            if (_states.TryGetValue(componentKind, out CachedEventDropTableState? existingState))
            {
                return existingState;
            }

            CachedEventDropTableState createdState = new();
            _states[componentKind] = createdState;
            return createdState;
        }
    }

    private sealed class CachedEventDropTableState
    {
        public int Epoch { get; private set; } = -1;
        public string PrefabName { get; private set; } = "";
        public bool IsInitialized { get; set; }
        public DropTable? SnapshotTable { get; private set; }
        public CompiledObjectDropRule[] RelevantRules { get; set; } = Array.Empty<CompiledObjectDropRule>();
        public bool HasCustomBlock { get; set; }
        public bool UsesTimeOfDay { get; set; }
        public bool UsesRequiredEnvironments { get; set; }
        public bool UsesInsidePlayerBase { get; set; }
        public string[] RequiredGlobalKeys { get; set; } = Array.Empty<string>();
        public string[] ForbiddenGlobalKeys { get; set; } = Array.Empty<string>();
        public int LastRuntimeSignature { get; set; } = int.MinValue;
        public bool HasCachedResolution { get; set; }
        public bool LastHasOverride { get; set; }
        public DropTable? LastResolvedDropTable { get; set; }
        public float LastInsidePlayerBaseSampleTime { get; set; } = float.NegativeInfinity;
        public bool IsInsidePlayerBase { get; set; }

        public void Reset(int epoch, string prefabName, DropTable? snapshotTable)
        {
            Epoch = epoch;
            PrefabName = prefabName ?? "";
            SnapshotTable = snapshotTable;
            IsInitialized = false;
            RelevantRules = Array.Empty<CompiledObjectDropRule>();
            HasCustomBlock = false;
            UsesTimeOfDay = false;
            UsesRequiredEnvironments = false;
            UsesInsidePlayerBase = false;
            RequiredGlobalKeys = Array.Empty<string>();
            ForbiddenGlobalKeys = Array.Empty<string>();
            LastRuntimeSignature = int.MinValue;
            HasCachedResolution = false;
            LastHasOverride = false;
            LastResolvedDropTable = null;
            LastInsidePlayerBaseSampleTime = float.NegativeInfinity;
            IsInsidePlayerBase = false;
        }
    }

    private sealed class EventDropRuntimeContextSnapshot
    {
        public int Frame { get; set; }
        public int TimeOfDayPhaseMarker { get; set; }
        public string EnvironmentName { get; set; } = "";
        public Dictionary<string, bool> GlobalKeyStates { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ObjectLazyRuntimeState
    {
        private readonly ConditionalWeakTable<PickableItem, WeightedRandomPickableItemState> _pickableItemRandomStates = new();
        private readonly ConditionalWeakTable<GameObject, EventDropTableStateSet> _eventDropTableStates = new();
        private EventDropRuntimeContextSnapshot? _eventDropRuntimeContextSnapshot;

        public bool HasRandomState(PickableItem pickableItem)
        {
            return _pickableItemRandomStates.TryGetValue(pickableItem, out _);
        }

        public bool TryGetRandomState(PickableItem pickableItem, out WeightedRandomPickableItemState state)
        {
            return _pickableItemRandomStates.TryGetValue(pickableItem, out state);
        }

        public void SetRandomWeights(PickableItem pickableItem, float[] weights)
        {
            _pickableItemRandomStates.Remove(pickableItem);
            if (weights.Length == 0)
            {
                return;
            }

            _pickableItemRandomStates.Add(pickableItem, new WeightedRandomPickableItemState
            {
                Weights = weights
            });
        }

        public void ClearRandomState(PickableItem pickableItem)
        {
            _pickableItemRandomStates.Remove(pickableItem);
        }

        public CachedEventDropTableState GetOrCreateEventDropTableState(
            GameObject gameObject,
            LiveObjectComponentKind componentKind)
        {
            return _eventDropTableStates.GetOrCreateValue(gameObject).GetOrCreate(componentKind);
        }

        public EventDropRuntimeContextSnapshot GetOrCreateEventDropRuntimeContextSnapshot()
        {
            int currentFrame = Time.frameCount;
            if (_eventDropRuntimeContextSnapshot != null &&
                _eventDropRuntimeContextSnapshot.Frame == currentFrame)
            {
                return _eventDropRuntimeContextSnapshot;
            }

            _eventDropRuntimeContextSnapshot = new EventDropRuntimeContextSnapshot
            {
                Frame = currentFrame,
                TimeOfDayPhaseMarker = TimeOfDayFormatting.GetCurrentRuntimePhaseMarker(),
                EnvironmentName = EnvMan.instance?.GetCurrentEnvironment()?.m_name ?? ""
            };

            return _eventDropRuntimeContextSnapshot;
        }
    }
}
