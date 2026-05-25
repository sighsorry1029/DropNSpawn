using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class SpawnerManager
{
    private static readonly SpawnerRuntimeStateStore RuntimeStateStore = new();

    private sealed class SpawnerRuntimeStateStore
    {
        private readonly Dictionary<SpawnArea, int> _runtimeSpawnAreaSignatures = new();
        private readonly Dictionary<CreatureSpawner, int> _runtimeCreatureSpawnerSignatures = new();
        private readonly Dictionary<SpawnArea, LocalRuntimeState> _spawnAreaLocalRuntimeStates = new();
        private readonly Dictionary<CreatureSpawner, LocalRuntimeState> _creatureSpawnerLocalRuntimeStates = new();
        private RuntimeContextSnapshot? _runtimeContextSnapshot;

        public bool HasRuntimeSignature(SpawnArea spawnArea)
        {
            return spawnArea != null && _runtimeSpawnAreaSignatures.ContainsKey(spawnArea);
        }

        public bool HasRuntimeSignature(CreatureSpawner creatureSpawner)
        {
            return creatureSpawner != null && _runtimeCreatureSpawnerSignatures.ContainsKey(creatureSpawner);
        }

        public bool TryGetRuntimeSignature(SpawnArea spawnArea, out int signature)
        {
            if (spawnArea != null && _runtimeSpawnAreaSignatures.TryGetValue(spawnArea, out signature))
            {
                return true;
            }

            signature = 0;
            return false;
        }

        public bool TryGetRuntimeSignature(CreatureSpawner creatureSpawner, out int signature)
        {
            if (creatureSpawner != null && _runtimeCreatureSpawnerSignatures.TryGetValue(creatureSpawner, out signature))
            {
                return true;
            }

            signature = 0;
            return false;
        }

        public void SetRuntimeSignature(SpawnArea spawnArea, int signature)
        {
            if (spawnArea == null)
            {
                return;
            }

            _runtimeSpawnAreaSignatures[spawnArea] = signature;
        }

        public void SetRuntimeSignature(CreatureSpawner creatureSpawner, int signature)
        {
            if (creatureSpawner == null)
            {
                return;
            }

            _runtimeCreatureSpawnerSignatures[creatureSpawner] = signature;
        }

        public void RemoveRuntimeSignature(SpawnArea? spawnArea)
        {
            if (spawnArea == null)
            {
                return;
            }

            _runtimeSpawnAreaSignatures.Remove(spawnArea);
        }

        public void RemoveRuntimeSignature(CreatureSpawner? creatureSpawner)
        {
            if (creatureSpawner == null)
            {
                return;
            }

            _runtimeCreatureSpawnerSignatures.Remove(creatureSpawner);
        }

        public LocalRuntimeState GetOrCreateLocalRuntimeState(SpawnArea spawnArea)
        {
            if (!_spawnAreaLocalRuntimeStates.TryGetValue(spawnArea, out LocalRuntimeState? state))
            {
                state = new LocalRuntimeState();
                _spawnAreaLocalRuntimeStates[spawnArea] = state;
            }

            return state;
        }

        public LocalRuntimeState GetOrCreateLocalRuntimeState(CreatureSpawner creatureSpawner)
        {
            if (!_creatureSpawnerLocalRuntimeStates.TryGetValue(creatureSpawner, out LocalRuntimeState? state))
            {
                state = new LocalRuntimeState();
                _creatureSpawnerLocalRuntimeStates[creatureSpawner] = state;
            }

            return state;
        }

        public void RemoveLocalRuntimeState(SpawnArea? spawnArea)
        {
            if (spawnArea == null)
            {
                return;
            }

            _spawnAreaLocalRuntimeStates.Remove(spawnArea);
        }

        public void RemoveLocalRuntimeState(CreatureSpawner? creatureSpawner)
        {
            if (creatureSpawner == null)
            {
                return;
            }

            _creatureSpawnerLocalRuntimeStates.Remove(creatureSpawner);
        }

        public bool TryGetRuntimeContextSnapshot(int frame, out RuntimeContextSnapshot snapshot)
        {
            if (_runtimeContextSnapshot != null && _runtimeContextSnapshot.Frame == frame)
            {
                snapshot = _runtimeContextSnapshot;
                return true;
            }

            snapshot = null!;
            return false;
        }

        public RuntimeContextSnapshot SetRuntimeContextSnapshot(RuntimeContextSnapshot snapshot)
        {
            _runtimeContextSnapshot = snapshot;
            return snapshot;
        }

        public void Clear()
        {
            _runtimeSpawnAreaSignatures.Clear();
            _runtimeCreatureSpawnerSignatures.Clear();
            _spawnAreaLocalRuntimeStates.Clear();
            _creatureSpawnerLocalRuntimeStates.Clear();
            _runtimeContextSnapshot = null;
        }

        public void ClearDynamicCaches()
        {
            _runtimeSpawnAreaSignatures.Clear();
            _runtimeCreatureSpawnerSignatures.Clear();
            _runtimeContextSnapshot = null;
        }
    }
}
