using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static void RestoreResourceComponents(GameObject gameObject, PrefabSnapshot snapshot, PrefabConfigurationEntry entry, bool updateRuntimeState)
    {
        if (HasDestructibleOverride(entry.Destructible) &&
            gameObject.TryGetComponent(out Destructible destructible) &&
            snapshot.Destructible != null)
        {
            if (HasDestructibleHealthOverride(entry.Destructible) && snapshot.Health?.Destructible is float destructibleHealth)
            {
                ApplyDestructibleHealth(destructible, destructibleHealth, updateRuntimeState);
            }

            if (HasDestructibleMinToolTierOverride(entry.Destructible) && snapshot.MinToolTier?.Destructible is int destructibleMinToolTier)
            {
                ApplyDestructibleMinToolTier(destructible, destructibleMinToolTier);
            }

            if (HasDestructibleComponentStateOverride(entry.Destructible))
            {
                if (!string.IsNullOrWhiteSpace(entry.Destructible?.DestructibleType))
                {
                    ApplyDestructibleType(destructible, snapshot.Destructible.DestructibleType);
                }

                if (!updateRuntimeState && HasDestructibleSpawnWhenDestroyedOverride(entry.Destructible))
                {
                    destructible.m_spawnWhenDestroyed = snapshot.Destructible.SpawnWhenDestroyed;
                }
            }
        }

        if (UsesLiveDropTableReconcile(LiveObjectComponentKind.DropOnDestroyed) &&
            HasDropTableOverride(entry.DropOnDestroyed) &&
            gameObject.TryGetComponent(out DropOnDestroyed dropOnDestroyed) &&
            snapshot.DropOnDestroyed != null)
        {
            dropOnDestroyed.m_dropWhenDestroyed = CloneDropTable(snapshot.DropOnDestroyed);
        }

        if (HasDamageableOverride(entry.MineRock) && gameObject.TryGetComponent(out MineRock mineRock))
        {
            if (UsesLiveDropTableReconcile(LiveObjectComponentKind.MineRock) &&
                HasDropTableOverride(entry.MineRock) &&
                snapshot.MineRock != null)
            {
                mineRock.m_dropItems = CloneDropTable(snapshot.MineRock);
            }

            if (HasDamageableHealthOverride(entry.MineRock) && snapshot.Health?.MineRock is float mineRockHealth)
            {
                ApplyMineRockHealth(mineRock, mineRockHealth, updateRuntimeState);
            }

            if (HasDamageableMinToolTierOverride(entry.MineRock) && snapshot.MinToolTier?.MineRock is int mineRockMinToolTier)
            {
                mineRock.m_minToolTier = mineRockMinToolTier;
            }
        }

        if (HasDamageableOverride(entry.MineRock5) && gameObject.TryGetComponent(out MineRock5 mineRock5))
        {
            if (UsesLiveDropTableReconcile(LiveObjectComponentKind.MineRock5) &&
                HasDropTableOverride(entry.MineRock5) &&
                snapshot.MineRock5 != null)
            {
                mineRock5.m_dropItems = CloneDropTable(snapshot.MineRock5);
            }

            if (HasDamageableHealthOverride(entry.MineRock5) && snapshot.Health?.MineRock5 is float mineRock5Health)
            {
                ApplyMineRock5Health(mineRock5, mineRock5Health, updateRuntimeState);
            }

            if (HasDamageableMinToolTierOverride(entry.MineRock5) && snapshot.MinToolTier?.MineRock5 is int mineRock5MinToolTier)
            {
                mineRock5.m_minToolTier = mineRock5MinToolTier;
            }
        }

        if (HasDamageableOverride(entry.TreeBase) && gameObject.TryGetComponent(out TreeBase treeBase))
        {
            if (UsesLiveDropTableReconcile(LiveObjectComponentKind.TreeBase) &&
                HasDropTableOverride(entry.TreeBase) &&
                snapshot.TreeBase != null)
            {
                treeBase.m_dropWhenDestroyed = CloneDropTable(snapshot.TreeBase);
            }

            if (HasDamageableHealthOverride(entry.TreeBase) && snapshot.Health?.TreeBase is float treeBaseHealth)
            {
                ApplyTreeBaseHealth(treeBase, treeBaseHealth, updateRuntimeState);
            }

            if (HasDamageableMinToolTierOverride(entry.TreeBase) && snapshot.MinToolTier?.TreeBase is int treeBaseMinToolTier)
            {
                treeBase.m_minToolTier = treeBaseMinToolTier;
            }
        }

        if (HasDamageableOverride(entry.TreeLog) && gameObject.TryGetComponent(out TreeLog treeLog))
        {
            if (UsesLiveDropTableReconcile(LiveObjectComponentKind.TreeLog) &&
                HasDropTableOverride(entry.TreeLog) &&
                snapshot.TreeLog != null)
            {
                treeLog.m_dropWhenDestroyed = CloneDropTable(snapshot.TreeLog);
            }

            if (HasDamageableHealthOverride(entry.TreeLog) && snapshot.Health?.TreeLog is float treeLogHealth)
            {
                ApplyTreeLogHealth(treeLog, treeLogHealth, updateRuntimeState);
            }

            if (HasDamageableMinToolTierOverride(entry.TreeLog) && snapshot.MinToolTier?.TreeLog is int treeLogMinToolTier)
            {
                treeLog.m_minToolTier = treeLogMinToolTier;
            }
        }
    }

    private static void ApplyResourceComponents(
        GameObject gameObject,
        PrefabConfigurationEntry entry,
        CompiledObjectDropRule? compiledRule,
        string contextRoot,
        bool updateRuntimeState)
    {
        if (HasDestructibleOverride(entry.Destructible))
        {
            if (!gameObject.TryGetComponent(out Destructible destructible))
            {
                WarnMissingComponent($"{contextRoot}/Destructible", nameof(Destructible));
            }
            else
            {
                if (compiledRule?.Destructible?.HasHealthOverride == true)
                {
                    ApplyDestructibleHealth(destructible, compiledRule.Destructible.Health, updateRuntimeState);
                }
                else if (HasDestructibleHealthOverride(entry.Destructible) &&
                         TryGetConfiguredHealth(entry.Destructible!.Health!.Value, $"{contextRoot}/Destructible/Health", out float destructibleHealth))
                {
                    ApplyDestructibleHealth(destructible, destructibleHealth, updateRuntimeState);
                }

                if (compiledRule?.Destructible?.HasMinToolTierOverride == true)
                {
                    ApplyDestructibleMinToolTier(destructible, compiledRule.Destructible.MinToolTier);
                }
                else if (HasDestructibleMinToolTierOverride(entry.Destructible) &&
                         TryGetConfiguredMinToolTier(entry.Destructible!.MinToolTier!.Value, $"{contextRoot}/Destructible/minToolTier", out int destructibleMinToolTier))
                {
                    ApplyDestructibleMinToolTier(destructible, destructibleMinToolTier);
                }

                if (compiledRule?.Destructible != null)
                {
                    if (compiledRule.Destructible.HasDestructibleTypeOverride)
                    {
                        ApplyDestructibleType(destructible, compiledRule.Destructible.DestructibleType);
                    }

                    if (!updateRuntimeState && compiledRule.Destructible.HasSpawnWhenDestroyedOverride)
                    {
                        destructible.m_spawnWhenDestroyed = compiledRule.Destructible.SpawnWhenDestroyed;
                    }
                }
                else if (HasDestructibleComponentStateOverride(entry.Destructible))
                {
                    ApplyDestructibleDefinition(destructible, entry.Destructible!, $"{contextRoot}/Destructible", includeSpawnWhenDestroyed: !updateRuntimeState);
                }
            }
        }

        if (HasDropTableOverride(entry.DropOnDestroyed) &&
            !gameObject.TryGetComponent(out DropOnDestroyed _))
        {
            WarnMissingComponent($"{contextRoot}/DropOnDestroyed", nameof(DropOnDestroyed));
        }

        if (HasDamageableOverride(entry.MineRock))
        {
            if (!gameObject.TryGetComponent(out MineRock mineRock))
            {
                WarnMissingComponent($"{contextRoot}/MineRock", nameof(MineRock));
            }
            else
            {
                if (compiledRule?.MineRockScalars?.HasHealthOverride == true)
                {
                    ApplyMineRockHealth(mineRock, compiledRule.MineRockScalars.Health, updateRuntimeState);
                }
                else if (HasDamageableHealthOverride(entry.MineRock) &&
                         TryGetConfiguredHealth(entry.MineRock!.Health!.Value, $"{contextRoot}/MineRock/Health", out float mineRockHealth))
                {
                    ApplyMineRockHealth(mineRock, mineRockHealth, updateRuntimeState);
                }

                if (compiledRule?.MineRockScalars?.HasMinToolTierOverride == true)
                {
                    mineRock.m_minToolTier = compiledRule.MineRockScalars.MinToolTier;
                }
                else if (HasDamageableMinToolTierOverride(entry.MineRock) &&
                         TryGetConfiguredMinToolTier(entry.MineRock!.MinToolTier!.Value, $"{contextRoot}/MineRock/minToolTier", out int mineRockMinToolTier))
                {
                    mineRock.m_minToolTier = mineRockMinToolTier;
                }
            }
        }

        if (HasDamageableOverride(entry.MineRock5))
        {
            if (!gameObject.TryGetComponent(out MineRock5 mineRock5))
            {
                WarnMissingComponent($"{contextRoot}/MineRock5", nameof(MineRock5));
            }
            else
            {
                if (compiledRule?.MineRock5Scalars?.HasHealthOverride == true)
                {
                    ApplyMineRock5Health(mineRock5, compiledRule.MineRock5Scalars.Health, updateRuntimeState);
                }
                else if (HasDamageableHealthOverride(entry.MineRock5) &&
                         TryGetConfiguredHealth(entry.MineRock5!.Health!.Value, $"{contextRoot}/MineRock5/Health", out float mineRock5Health))
                {
                    ApplyMineRock5Health(mineRock5, mineRock5Health, updateRuntimeState);
                }

                if (compiledRule?.MineRock5Scalars?.HasMinToolTierOverride == true)
                {
                    mineRock5.m_minToolTier = compiledRule.MineRock5Scalars.MinToolTier;
                }
                else if (HasDamageableMinToolTierOverride(entry.MineRock5) &&
                         TryGetConfiguredMinToolTier(entry.MineRock5!.MinToolTier!.Value, $"{contextRoot}/MineRock5/minToolTier", out int mineRock5MinToolTier))
                {
                    mineRock5.m_minToolTier = mineRock5MinToolTier;
                }
            }
        }

        if (HasDamageableOverride(entry.TreeBase))
        {
            if (!gameObject.TryGetComponent(out TreeBase treeBase))
            {
                WarnMissingComponent($"{contextRoot}/TreeBase", nameof(TreeBase));
            }
            else
            {
                if (compiledRule?.TreeBaseScalars?.HasHealthOverride == true)
                {
                    ApplyTreeBaseHealth(treeBase, compiledRule.TreeBaseScalars.Health, updateRuntimeState);
                }
                else if (HasDamageableHealthOverride(entry.TreeBase) &&
                         TryGetConfiguredHealth(entry.TreeBase!.Health!.Value, $"{contextRoot}/TreeBase/Health", out float treeBaseHealth))
                {
                    ApplyTreeBaseHealth(treeBase, treeBaseHealth, updateRuntimeState);
                }

                if (compiledRule?.TreeBaseScalars?.HasMinToolTierOverride == true)
                {
                    treeBase.m_minToolTier = compiledRule.TreeBaseScalars.MinToolTier;
                }
                else if (HasDamageableMinToolTierOverride(entry.TreeBase) &&
                         TryGetConfiguredMinToolTier(entry.TreeBase!.MinToolTier!.Value, $"{contextRoot}/TreeBase/minToolTier", out int treeBaseMinToolTier))
                {
                    treeBase.m_minToolTier = treeBaseMinToolTier;
                }
            }
        }

        if (HasDamageableOverride(entry.TreeLog))
        {
            if (!gameObject.TryGetComponent(out TreeLog treeLog))
            {
                WarnMissingComponent($"{contextRoot}/TreeLog", nameof(TreeLog));
            }
            else
            {
                if (compiledRule?.TreeLogScalars?.HasHealthOverride == true)
                {
                    ApplyTreeLogHealth(treeLog, compiledRule.TreeLogScalars.Health, updateRuntimeState);
                }
                else if (HasDamageableHealthOverride(entry.TreeLog) &&
                         TryGetConfiguredHealth(entry.TreeLog!.Health!.Value, $"{contextRoot}/TreeLog/Health", out float treeLogHealth))
                {
                    ApplyTreeLogHealth(treeLog, treeLogHealth, updateRuntimeState);
                }

                if (compiledRule?.TreeLogScalars?.HasMinToolTierOverride == true)
                {
                    treeLog.m_minToolTier = compiledRule.TreeLogScalars.MinToolTier;
                }
                else if (HasDamageableMinToolTierOverride(entry.TreeLog) &&
                         TryGetConfiguredMinToolTier(entry.TreeLog!.MinToolTier!.Value, $"{contextRoot}/TreeLog/minToolTier", out int treeLogMinToolTier))
                {
                    treeLog.m_minToolTier = treeLogMinToolTier;
                }
            }
        }
    }
}
