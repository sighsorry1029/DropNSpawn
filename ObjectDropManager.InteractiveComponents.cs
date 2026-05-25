using UnityEngine;

namespace DropNSpawn;

internal static partial class ObjectDropManager
{
    private static void RestoreInteractiveComponents(GameObject gameObject, PrefabSnapshot snapshot, PrefabConfigurationEntry entry, bool updateRuntimeState)
    {
        if (UsesLiveDropTableReconcile(LiveObjectComponentKind.Container) &&
            HasDropTableOverride(entry.Container) &&
            gameObject.TryGetComponent(out Container container) &&
            snapshot.Container != null)
        {
            container.m_defaultItems = CloneDropTable(snapshot.Container);
        }

        if (HasPickableOverride(entry.Pickable) && gameObject.TryGetComponent(out Pickable pickable) && snapshot.Pickable != null)
        {
            pickable.m_itemPrefab = snapshot.Pickable.ItemPrefab;
            pickable.m_amount = snapshot.Pickable.Amount;
            pickable.m_minAmountScaled = snapshot.Pickable.MinAmountScaled;
            pickable.m_dontScale = snapshot.Pickable.DontScale;
            pickable.m_overrideName = snapshot.Pickable.OverrideName;
            pickable.m_extraDrops = CloneDropTable(snapshot.Pickable.ExtraDrops);
        }

        if (HasPickableItemOverride(entry.PickableItem) &&
            gameObject.TryGetComponent(out PickableItem pickableItem) &&
            snapshot.PickableItem != null)
        {
            RestorePickableItem(pickableItem, snapshot.PickableItem, updateRuntimeState);
        }

        if (HasFishOverride(entry.Fish) && gameObject.TryGetComponent(out Fish fish) && snapshot.Fish != null)
        {
            fish.m_extraDrops = CloneDropTable(snapshot.Fish.ExtraDrops);
        }
    }

    private static void ApplyInteractiveComponents(
        GameObject gameObject,
        PrefabConfigurationEntry entry,
        CompiledObjectDropRule? compiledRule,
        string contextRoot,
        bool updateRuntimeState)
    {
        if (HasDropTableOverride(entry.Container) &&
            !gameObject.TryGetComponent(out Container _))
        {
            WarnMissingComponent($"{contextRoot}/Container", nameof(Container));
        }

        if (HasPickableOverride(entry.Pickable))
        {
            if (!gameObject.TryGetComponent(out Pickable pickable))
            {
                WarnMissingComponent($"{contextRoot}/Pickable", nameof(Pickable));
            }
            else if (compiledRule?.Pickable != null)
            {
                ApplyPickableDefinition(pickable, compiledRule.Pickable, updateRuntimeState);
            }
            else
            {
                ApplyPickableDefinition(pickable, entry.Pickable!, $"{contextRoot}/Pickable", updateRuntimeState);
            }
        }

        if (HasPickableItemOverride(entry.PickableItem))
        {
            if (!gameObject.TryGetComponent(out PickableItem pickableItem))
            {
                WarnMissingComponent($"{contextRoot}/PickableItem", nameof(PickableItem));
            }
            else if (compiledRule?.PickableItem != null)
            {
                ApplyPickableItemDefinition(pickableItem, compiledRule.PickableItem, updateRuntimeState);
            }
            else
            {
                ApplyPickableItemDefinition(pickableItem, entry.PickableItem!, $"{contextRoot}/PickableItem", updateRuntimeState);
            }
        }
    }
}
