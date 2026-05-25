using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class CharacterDropManager
{
    private sealed class CharacterApplyOperations : StandardBaselineDesiredStateOperations<CharacterDesiredState>
    {
        public static CharacterApplyOperations Instance { get; } = new();

        public override string DomainKey => "character";

        public override BaselineDesiredStateCapabilities Capabilities =>
            BaselineDesiredStateCapabilities.Validation |
            BaselineDesiredStateCapabilities.StaticBaseline |
            BaselineDesiredStateCapabilities.StaticApply |
            BaselineDesiredStateCapabilities.LiveApply |
            BaselineDesiredStateCapabilities.StaticRollback;

        public override void Validate(CharacterDesiredState desiredState) => ValidateCharacterDesiredState(desiredState);
        public override void RestoreStaticBaseline(CharacterDesiredState desiredState) => RestoreCharacterStaticBaseline(desiredState);
        public override void ApplyDesiredStateToStaticBaseline(CharacterDesiredState desiredState) => ApplyCharacterDesiredStateToStaticBaseline(desiredState);
        public override void ApplyDesiredStateToLive(CharacterDesiredState desiredState) => ApplyCharacterDesiredStateToLive(desiredState);
        public override void Commit(CharacterDesiredState desiredState) => RecordAppliedState(desiredState.GameDataSignature, desiredState.DomainEnabled, desiredState.CurrentEntrySignatures);
    }

    private sealed class CharacterDesiredState
    {
        public StandardDomainApplyPlan ApplyPlan { get; set; }
        public CharacterCompiledState CompiledState { get; set; } = CharacterCompiledState.Empty;
        public int GameDataSignature { get; set; }
        public Dictionary<string, string> CurrentEntrySignatures { get; set; } = EmptyEntrySignatures;
        public bool DomainEnabled { get; set; }
    }

    private static void RunApplyCoordinator(
        int gameDataSignature,
        bool domainEnabled,
        Dictionary<string, string> currentEntrySignatures)
    {
        CharacterDesiredState desiredState = BuildCharacterDesiredState(gameDataSignature, domainEnabled, currentEntrySignatures);
        StandardApplyOutcome outcome = StandardBaselineDesiredStateCoordinator.Run(
            desiredState.ApplyPlan,
            desiredState,
            CharacterApplyOperations.Instance);
        if (!outcome.Success)
        {
            return;
        }
    }

    private static CharacterDesiredState BuildCharacterDesiredState(
        int gameDataSignature,
        bool domainEnabled,
        Dictionary<string, string> currentEntrySignatures)
    {
        bool canUseTargetedLiveReload = _lastAppliedGameDataSignature == gameDataSignature &&
                                        _lastAppliedDomainEnabled == true &&
                                        !HasAddedPrefabs(_lastAppliedEntrySignaturesByPrefab, currentEntrySignatures);
        EnsureCompiledState();
        return new CharacterDesiredState
        {
            GameDataSignature = gameDataSignature,
            ApplyPlan = StandardDomainApplySupport.BuildPlan(
                _lastAppliedGameDataSignature,
                gameDataSignature,
                _lastAppliedDomainEnabled,
                domainEnabled,
                _lastAppliedEntrySignaturesByPrefab,
                currentEntrySignatures,
                EmptyEntrySignatures,
                canUseTargetedLiveReload),
            CurrentEntrySignatures = currentEntrySignatures,
            CompiledState = _compiledState,
            DomainEnabled = domainEnabled
        };
    }

    private static void RestoreCharacterStaticBaseline(CharacterDesiredState desiredState)
    {
        RestoreSnapshots();
    }

    private static void ValidateCharacterDesiredState(CharacterDesiredState desiredState)
    {
        ValidateConfiguredPrefabs();
    }

    private static void ApplyCharacterDesiredStateToStaticBaseline(CharacterDesiredState desiredState)
    {
        if (!desiredState.DomainEnabled)
        {
            return;
        }

        IEnumerable<string> prefabNames = desiredState.ApplyPlan.DirtyKeys != null
            ? desiredState.ApplyPlan.DirtyKeys
            : desiredState.CompiledState.StaticDropsByPrefab.Keys;
        foreach (string prefabName in prefabNames)
        {
            if (!desiredState.CompiledState.StaticBuiltDropsByPrefab.TryGetValue(prefabName, out List<CharacterDrop.Drop>? staticDrops) ||
                staticDrops.Count == 0 ||
                !CharacterDropRuntime.TryGetSnapshot(prefabName, out CharacterDropSnapshot? snapshot) ||
                snapshot == null ||
                snapshot.Prefab == null ||
                !snapshot.Prefab.TryGetComponent(out CharacterDrop characterDrop))
            {
                continue;
            }

            characterDrop.m_drops = staticDrops;
        }
    }

    private static void ApplyCharacterDesiredStateToLive(CharacterDesiredState desiredState)
    {
        BootstrapRegisteredCharacterDropsIfNeeded(
            desiredState.ApplyPlan.DirtyKeys,
            forceRescan: desiredState.ApplyPlan.DirtyKeys != null);

        IEnumerable<CharacterDrop> characterDrops = desiredState.ApplyPlan.DirtyKeys == null
            ? GetRegisteredCharacterDrops()
            : GetRegisteredCharacterDrops(desiredState.ApplyPlan.DirtyKeys);
        foreach (CharacterDrop characterDrop in characterDrops)
        {
            if (characterDrop == null || characterDrop.gameObject == null)
            {
                continue;
            }

            RegisterLiveCharacterDrop(characterDrop);
            string prefabName = GetPrefabName(characterDrop.gameObject);
            if (!CharacterDropRuntime.TryGetSnapshot(prefabName, out CharacterDropSnapshot? snapshot) ||
                snapshot == null)
            {
                continue;
            }

            characterDrop.m_drops = snapshot.BuiltDrops;
            if (!desiredState.DomainEnabled ||
                !desiredState.CompiledState.StaticBuiltDropsByPrefab.TryGetValue(prefabName, out List<CharacterDrop.Drop>? staticDrops) ||
                staticDrops.Count == 0)
            {
                continue;
            }

            characterDrop.m_drops = staticDrops;
        }
    }
}
