using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace DropNSpawn;

internal static class CharacterDropKillerFilter
{
    internal enum Attribution
    {
        Unknown,
        Allowed,
        Rejected,
        Ambiguous
    }

    private sealed class DelayedDamageLedger
    {
        internal Character Target = null!;
        internal SE_Poison? PoisonStatus;
        internal Attribution Poison;
        internal SE_Burning? FireStatus;
        internal Attribution Fire;
        internal SE_Burning? SpiritStatus;
        internal Attribution Spirit;
    }

    private readonly struct DeathCredit
    {
        internal DeathCredit(Character target, Attribution attribution)
        {
            Target = target;
            Attribution = attribution;
        }

        internal Character Target { get; }
        internal Attribution Attribution { get; }
    }

    internal struct RpcDamageContext
    {
        internal Character? Target;
        internal HitData? Hit;
        internal Attribution Attribution;
        internal bool HasResolvedAttribution;
    }

    internal struct DelayedDamageTickContext
    {
        internal Character? Target;
        internal HitData.HitType HitType;
        internal Attribution Attribution;
    }

    internal struct DeathContext
    {
        internal CharacterDrop? CharacterDrop;
        internal bool Suppress;
    }

    internal readonly struct RpcDamageScopeState
    {
        internal RpcDamageScopeState(RpcDamageContext previous)
        {
            Previous = previous;
            Changed = true;
        }

        internal RpcDamageContext Previous { get; }
        internal bool Changed { get; }
    }

    internal readonly struct DelayedDamageTickScopeState
    {
        internal DelayedDamageTickScopeState(DelayedDamageTickContext previous)
        {
            Previous = previous;
            Changed = true;
        }

        internal DelayedDamageTickContext Previous { get; }
        internal bool Changed { get; }
    }

    internal readonly struct ApplyDamageState
    {
        internal ApplyDamageState(Character target)
        {
            Target = target;
            Tracking = true;
        }

        internal Character? Target { get; }
        internal bool Tracking { get; }
    }

    internal readonly struct DeathScopeState
    {
        internal DeathScopeState(DeathContext previous, Character target)
        {
            Previous = previous;
            Target = target;
            Changed = true;
        }

        internal DeathContext Previous { get; }
        internal Character? Target { get; }
        internal bool Changed { get; }
    }

    private static readonly Dictionary<int, DelayedDamageLedger> DelayedDamageLedgers = new();
    private static readonly Dictionary<int, DeathCredit> PendingDeathCredits = new();

    [ThreadStatic] private static RpcDamageContext _currentRpcDamage;
    [ThreadStatic] private static DelayedDamageTickContext _currentDelayedDamageTick;
    [ThreadStatic] private static DeathContext _currentDeath;

    private static int _configurationGeneration;
    private static int _observedConfigurationGeneration = -1;

    internal static void NotifyConfigurationChanged()
    {
        Interlocked.Increment(ref _configurationGeneration);
    }

    internal static RpcDamageScopeState BeginRpcDamageScope(Character target, HitData hit)
    {
        if (!EnsureEnabled() ||
            target == null ||
            hit == null ||
            target.IsPlayer() ||
            IsRemoteOwned(target))
        {
            return default;
        }

        RpcDamageContext previous = _currentRpcDamage;
        _currentRpcDamage = new RpcDamageContext
        {
            Target = target,
            Hit = hit
        };
        return new RpcDamageScopeState(previous);
    }

    internal static void EndRpcDamageScope(RpcDamageScopeState state)
    {
        if (state.Changed)
        {
            _currentRpcDamage = state.Previous;
        }
    }

    internal static bool WillPoisonReplaceCurrentPool(SE_Poison status, float damage)
    {
        return EnsureEnabled() &&
               status != null &&
               status.m_character != null &&
               !status.m_character.IsPlayer() &&
               damage >= status.m_damageLeft;
    }

    internal static void RecordPoisonSource(SE_Poison status, bool accepted)
    {
        if (!accepted || !EnsureEnabled() || status == null || status.m_character == null)
        {
            return;
        }

        Character target = status.m_character;
        if (target.IsPlayer())
        {
            return;
        }

        DelayedDamageLedger ledger = GetDelayedDamageLedger(target);
        ledger.PoisonStatus = status;
        ledger.Poison = GetCurrentRpcAttribution(target);
    }

    internal static bool IsFirePoolEmpty(SE_Burning status)
    {
        return EnsureEnabled() &&
               status != null &&
               status.m_character != null &&
               !status.m_character.IsPlayer() &&
               status.m_fireDamageLeft <= 0f;
    }

    internal static bool IsSpiritPoolEmpty(SE_Burning status)
    {
        return EnsureEnabled() &&
               status != null &&
               status.m_character != null &&
               !status.m_character.IsPlayer() &&
               status.m_spiritDamageLeft <= 0f;
    }

    internal static void RecordFireSource(SE_Burning status, bool poolWasEmpty, bool accepted)
    {
        RecordAccumulatingDelayedDamageSource(status, poolWasEmpty, accepted, spirit: false);
    }

    internal static void RecordSpiritSource(SE_Burning status, bool poolWasEmpty, bool accepted)
    {
        RecordAccumulatingDelayedDamageSource(status, poolWasEmpty, accepted, spirit: true);
    }

    internal static DelayedDamageTickScopeState BeginPoisonDamageTick(SE_Poison status)
    {
        if (!EnsureEnabled())
        {
            return default;
        }

        Character? target = status?.m_character;
        if (target == null || target.IsPlayer())
        {
            return default;
        }

        DelayedDamageTickContext previous = _currentDelayedDamageTick;
        Attribution attribution = Attribution.Unknown;
        if (DelayedDamageLedgers.TryGetValue(target.GetInstanceID(), out DelayedDamageLedger ledger) &&
            ReferenceEquals(ledger.Target, target) &&
            ReferenceEquals(ledger.PoisonStatus, status))
        {
            attribution = ledger.Poison;
        }

        _currentDelayedDamageTick = new DelayedDamageTickContext
        {
            Target = target,
            HitType = HitData.HitType.Poisoned,
            Attribution = attribution
        };
        return new DelayedDamageTickScopeState(previous);
    }

    internal static DelayedDamageTickScopeState BeginBurningDamageTick(SE_Burning status)
    {
        if (!EnsureEnabled())
        {
            return default;
        }

        Character? target = status?.m_character;
        if (target == null || target.IsPlayer())
        {
            return default;
        }

        DelayedDamageTickContext previous = _currentDelayedDamageTick;
        Attribution fire = Attribution.Unknown;
        Attribution spirit = Attribution.Unknown;
        if (DelayedDamageLedgers.TryGetValue(target.GetInstanceID(), out DelayedDamageLedger ledger) &&
            ReferenceEquals(ledger.Target, target))
        {
            if (ReferenceEquals(ledger.FireStatus, status))
            {
                fire = ledger.Fire;
            }

            if (ReferenceEquals(ledger.SpiritStatus, status))
            {
                spirit = ledger.Spirit;
            }
        }

        bool hasFire = status != null && status.m_fireDamagePerHit > 0f;
        bool hasSpirit = status != null && status.m_spiritDamagePerHit > 0f;
        _currentDelayedDamageTick = new DelayedDamageTickContext
        {
            Target = target,
            HitType = HitData.HitType.Burning,
            Attribution = CombineBurningAttribution(fire, hasFire, spirit, hasSpirit)
        };
        return new DelayedDamageTickScopeState(previous);
    }

    internal static void EndDelayedDamageTick(DelayedDamageTickScopeState state)
    {
        if (state.Changed)
        {
            _currentDelayedDamageTick = state.Previous;
        }
    }

    internal static ApplyDamageState BeginApplyDamage(Character target)
    {
        if (!EnsureEnabled() || target == null || target.IsPlayer() || IsRemoteOwned(target))
        {
            return default;
        }

        return target.GetHealth() > 0f
            ? new ApplyDamageState(target)
            : default;
    }

    internal static void CompleteApplyDamage(Character target, HitData hit, ApplyDamageState state)
    {
        if (!state.Tracking ||
            target == null ||
            hit == null ||
            !ReferenceEquals(state.Target, target) ||
            target.GetHealth() > 0f ||
            !ReferenceEquals(target.m_lastHit, hit))
        {
            return;
        }

        Attribution attribution = TryGetCurrentDelayedDamageAttribution(target, hit, out Attribution delayed)
            ? delayed
            : ClassifySource(hit, target);
        PendingDeathCredits[target.GetInstanceID()] = new DeathCredit(target, attribution);
    }

    internal static void ClearRecoveredDeathCredit(Character character)
    {
        if (!EnsureEnabled() ||
            character == null ||
            character.IsPlayer() ||
            PendingDeathCredits.Count == 0)
        {
            return;
        }

        int id = character.GetInstanceID();
        if (PendingDeathCredits.TryGetValue(id, out DeathCredit credit) &&
            ReferenceEquals(credit.Target, character) &&
            character.GetHealth() > 0f)
        {
            PendingDeathCredits.Remove(id);
        }
    }

    internal static DeathScopeState BeginDeathScope(Character target)
    {
        if (!EnsureEnabled() || target == null || target.IsPlayer() || IsRemoteOwned(target))
        {
            return default;
        }

        CharacterDrop characterDrop = target.GetComponent<CharacterDrop>();
        if (characterDrop == null)
        {
            return default;
        }

        DeathContext previous = _currentDeath;
        // Ragdoll.Setup serializes GenerateDropList before CharacterDrop.OnDeath, so both
        // paths must share the decision captured for this exact Character.OnDeath call.
        Attribution attribution = GetPendingDeathAttribution(target);
        string prefabName = GetPrefabName(target.gameObject);
        bool blacklisted = PluginSettingsFacade.IsPlayerAlignedKillerRequirementBlacklisted(prefabName);
        _currentDeath = new DeathContext
        {
            CharacterDrop = characterDrop,
            Suppress = ShouldSuppress(enabled: true, victimIsPlayer: false, blacklisted, attribution)
        };
        return new DeathScopeState(previous, target);
    }

    internal static void EndDeathScope(DeathScopeState state)
    {
        if (!state.Changed)
        {
            return;
        }

        _currentDeath = state.Previous;
        if (state.Target != null)
        {
            ForgetCharacter(state.Target);
        }
    }

    internal static void SuppressScopedGeneratedDrops(
        CharacterDrop characterDrop,
        List<KeyValuePair<GameObject, int>> drops)
    {
        if (characterDrop != null &&
            drops != null &&
            ReferenceEquals(_currentDeath.CharacterDrop, characterDrop) &&
            _currentDeath.Suppress)
        {
            drops.Clear();
        }
    }

    internal static bool ShouldSuppressScopedGeneration(CharacterDrop characterDrop)
    {
        return characterDrop != null &&
               ReferenceEquals(_currentDeath.CharacterDrop, characterDrop) &&
               _currentDeath.Suppress;
    }

    internal static bool ShouldSuppressDeathCallback(CharacterDrop characterDrop)
    {
        if (characterDrop == null)
        {
            return false;
        }

        if (ReferenceEquals(_currentDeath.CharacterDrop, characterDrop))
        {
            return _currentDeath.Suppress;
        }

        if (!EnsureEnabled())
        {
            return false;
        }

        Character character = characterDrop.GetComponent<Character>();
        if (character == null || character.IsPlayer() || character.GetHealth() > 0f)
        {
            return false;
        }

        bool blacklisted = PluginSettingsFacade.IsPlayerAlignedKillerRequirementBlacklisted(
            GetPrefabName(character.gameObject));
        return ShouldSuppress(
            enabled: true,
            victimIsPlayer: false,
            blacklisted,
            GetPendingDeathAttribution(character));
    }

    internal static void ForgetCharacter(Character character)
    {
        if (character == null)
        {
            return;
        }

        int id = character.GetInstanceID();
        DelayedDamageLedgers.Remove(id);
        PendingDeathCredits.Remove(id);
    }

    internal static Attribution MergeAttribution(Attribution current, Attribution incoming)
    {
        return current == incoming ? current : Attribution.Ambiguous;
    }

    internal static bool ShouldSuppress(
        bool enabled,
        bool victimIsPlayer,
        bool blacklisted,
        Attribution attribution)
    {
        return enabled && !victimIsPlayer && !blacklisted && attribution != Attribution.Allowed;
    }

    private static bool EnsureEnabled()
    {
        int generation = Volatile.Read(ref _configurationGeneration);
        if (_observedConfigurationGeneration != generation)
        {
            DelayedDamageLedgers.Clear();
            PendingDeathCredits.Clear();
            _currentRpcDamage = default;
            _currentDelayedDamageTick = default;
            _currentDeath = default;
            _observedConfigurationGeneration = generation;
        }

        return PluginSettingsFacade.IsPlayerAlignedKillerRequiredForCharacterDrops();
    }

    private static bool IsRemoteOwned(Character character)
    {
        ZNetView? nview = character.m_nview;
        return nview != null && nview.IsValid() && !nview.IsOwner();
    }

    private static void RecordAccumulatingDelayedDamageSource(
        SE_Burning status,
        bool poolWasEmpty,
        bool accepted,
        bool spirit)
    {
        if (!accepted || !EnsureEnabled() || status == null || status.m_character == null)
        {
            return;
        }

        Character target = status.m_character;
        if (target.IsPlayer())
        {
            return;
        }

        DelayedDamageLedger ledger = GetDelayedDamageLedger(target);
        Attribution incoming = GetCurrentRpcAttribution(target);
        if (spirit)
        {
            Attribution current = ReferenceEquals(ledger.SpiritStatus, status)
                ? ledger.Spirit
                : Attribution.Unknown;
            ledger.Spirit = poolWasEmpty ? incoming : MergeAttribution(current, incoming);
            ledger.SpiritStatus = status;
            return;
        }

        Attribution currentFire = ReferenceEquals(ledger.FireStatus, status)
            ? ledger.Fire
            : Attribution.Unknown;
        ledger.Fire = poolWasEmpty ? incoming : MergeAttribution(currentFire, incoming);
        ledger.FireStatus = status;
    }

    private static DelayedDamageLedger GetDelayedDamageLedger(Character target)
    {
        int id = target.GetInstanceID();
        if (!DelayedDamageLedgers.TryGetValue(id, out DelayedDamageLedger ledger) ||
            !ReferenceEquals(ledger.Target, target))
        {
            ledger = new DelayedDamageLedger { Target = target };
            DelayedDamageLedgers[id] = ledger;
        }

        return ledger;
    }

    private static Attribution GetCurrentRpcAttribution(Character target)
    {
        if (!ReferenceEquals(_currentRpcDamage.Target, target) || _currentRpcDamage.Hit == null)
        {
            return Attribution.Unknown;
        }

        if (!_currentRpcDamage.HasResolvedAttribution)
        {
            _currentRpcDamage.Attribution = ClassifySource(_currentRpcDamage.Hit, target);
            _currentRpcDamage.HasResolvedAttribution = true;
        }

        return _currentRpcDamage.Attribution;
    }

    private static bool TryGetCurrentDelayedDamageAttribution(
        Character target,
        HitData hit,
        out Attribution attribution)
    {
        if (ReferenceEquals(_currentDelayedDamageTick.Target, target) &&
            _currentDelayedDamageTick.HitType == hit.m_hitType &&
            (hit.m_hitType == HitData.HitType.Poisoned || hit.m_hitType == HitData.HitType.Burning))
        {
            attribution = _currentDelayedDamageTick.Attribution;
            return true;
        }

        attribution = Attribution.Unknown;
        return false;
    }

    private static Attribution CombineBurningAttribution(
        Attribution fire,
        bool hasFire,
        Attribution spirit,
        bool hasSpirit)
    {
        if (!hasFire)
        {
            return hasSpirit ? spirit : Attribution.Unknown;
        }

        if (!hasSpirit)
        {
            return fire;
        }

        return MergeAttribution(fire, spirit);
    }

    private static Attribution GetPendingDeathAttribution(Character target)
    {
        return PendingDeathCredits.TryGetValue(target.GetInstanceID(), out DeathCredit credit) &&
               ReferenceEquals(credit.Target, target)
            ? credit.Attribution
            : Attribution.Unknown;
    }

    private static Attribution ClassifySource(HitData hit, Character target)
    {
        if (hit == null || target == null)
        {
            return Attribution.Rejected;
        }

        ZDOID sourceId = hit.m_attacker;
        Character? source = hit.GetAttacker();
        if (source != null)
        {
            bool self = ReferenceEquals(source, target) ||
                        (sourceId != ZDOID.None && sourceId == target.GetZDOID());
            return ClassifySourceSnapshot(
                sourceMissing: false,
                self,
                source.IsPlayer(),
                source.IsTamed(),
                source.GetFaction());
        }

        if (sourceId == ZDOID.None || sourceId == target.GetZDOID())
        {
            return Attribution.Rejected;
        }

        if (Player.m_localPlayer != null && Player.m_localPlayer.GetZDOID() == sourceId)
        {
            return Attribution.Allowed;
        }

        if (ZNet.instance != null)
        {
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer != null && peer.IsReady() && peer.m_characterID == sourceId)
                {
                    return Attribution.Allowed;
                }
            }
        }

        if (ZNetScene.instance != null)
        {
            GameObject sourceInstance = ZNetScene.instance.FindInstance(sourceId);
            Character? sourceCharacter = sourceInstance != null
                ? sourceInstance.GetComponent<Character>()
                : null;
            if (sourceCharacter != null)
            {
                return ClassifySourceSnapshot(
                    sourceMissing: false,
                    self: false,
                    sourceCharacter.IsPlayer(),
                    sourceCharacter.IsTamed(),
                    sourceCharacter.GetFaction());
            }
        }

        ZDO? sourceZdo = ZDOMan.instance?.GetZDO(sourceId);
        if (sourceZdo == null)
        {
            return Attribution.Rejected;
        }

        if (sourceZdo.GetBool(ZDOVars.s_tamed, false))
        {
            return Attribution.Allowed;
        }

        GameObject? sourcePrefab = ZNetScene.instance?.GetPrefab(sourceZdo.GetPrefab());
        Character? sourcePrefabCharacter = sourcePrefab != null
            ? sourcePrefab.GetComponent<Character>()
            : null;
        return sourcePrefabCharacter != null
            ? ClassifySourceSnapshot(
                sourceMissing: false,
                self: false,
                sourcePrefabCharacter.IsPlayer(),
                isTamed: false,
                sourcePrefabCharacter.GetFaction())
            : Attribution.Rejected;
    }

    internal static Attribution ClassifySourceSnapshot(
        bool sourceMissing,
        bool self,
        bool isPlayer,
        bool isTamed,
        Character.Faction faction)
    {
        if (sourceMissing || self)
        {
            return Attribution.Rejected;
        }

        return isPlayer ||
               isTamed ||
               faction == Character.Faction.Players ||
               faction == Character.Faction.PlayerSpawned
            ? Attribution.Allowed
            : Attribution.Rejected;
    }

    private static string GetPrefabName(GameObject gameObject)
    {
        if (gameObject == null)
        {
            return "";
        }

        ZNetView? nview = gameObject.GetComponent<ZNetView>();
        ZDO? zdo = nview?.GetZDO();
        if (zdo != null && ZNetScene.instance != null)
        {
            GameObject? prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
            if (prefab != null)
            {
                return prefab.name;
            }
        }

        string prefabName = Utils.GetPrefabName(gameObject);
        return string.IsNullOrWhiteSpace(prefabName) ? gameObject.name : prefabName;
    }
}

[HarmonyPatch(typeof(Character), "RPC_Damage")]
internal static class CharacterDropKillerFilterRpcDamagePatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(
        Character __instance,
        HitData hit,
        out CharacterDropKillerFilter.RpcDamageScopeState __state)
    {
        __state = CharacterDropKillerFilter.BeginRpcDamageScope(__instance, hit);
    }

    private static Exception? Finalizer(
        CharacterDropKillerFilter.RpcDamageScopeState __state,
        Exception? __exception)
    {
        CharacterDropKillerFilter.EndRpcDamageScope(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
internal static class CharacterDropKillerFilterGenerateDropListPatch
{
    // Run after stateful prefixes have initialized their __state, then skip only the
    // original RNG-producing method. The final postfix clear remains a second guard.
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(
        CharacterDrop __instance,
        ref List<KeyValuePair<GameObject, int>> __result)
    {
        if (!CharacterDropKillerFilter.ShouldSuppressScopedGeneration(__instance))
        {
            return true;
        }

        __result = new List<KeyValuePair<GameObject, int>>();
        return false;
    }
}

[HarmonyPatch(
    typeof(Character),
    nameof(Character.ApplyDamage),
    new[] { typeof(HitData), typeof(bool), typeof(bool), typeof(HitData.DamageModifier) })]
internal static class CharacterDropKillerFilterApplyDamagePatch
{
    private static void Prefix(
        Character __instance,
        out CharacterDropKillerFilter.ApplyDamageState __state)
    {
        __state = CharacterDropKillerFilter.BeginApplyDamage(__instance);
    }

    [HarmonyPriority(Priority.Last)]
    private static void Postfix(
        Character __instance,
        HitData hit,
        CharacterDropKillerFilter.ApplyDamageState __state)
    {
        CharacterDropKillerFilter.CompleteApplyDamage(__instance, hit, __state);
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.SetHealth), new[] { typeof(float) })]
internal static class CharacterDropKillerFilterSetHealthPatch
{
    private static void Postfix(Character __instance)
    {
        CharacterDropKillerFilter.ClearRecoveredDeathCredit(__instance);
    }
}

[HarmonyPatch(typeof(SE_Poison), nameof(SE_Poison.AddDamage), new[] { typeof(float) })]
internal static class CharacterDropKillerFilterPoisonSourcePatch
{
    private static void Prefix(SE_Poison __instance, float damage, out bool __state)
    {
        __state = CharacterDropKillerFilter.WillPoisonReplaceCurrentPool(__instance, damage);
    }

    private static void Postfix(SE_Poison __instance, bool __state, bool __runOriginal)
    {
        CharacterDropKillerFilter.RecordPoisonSource(__instance, __state && __runOriginal);
    }
}

[HarmonyPatch(typeof(SE_Burning), nameof(SE_Burning.AddFireDamage), new[] { typeof(float) })]
internal static class CharacterDropKillerFilterFireSourcePatch
{
    private static void Prefix(SE_Burning __instance, out bool __state)
    {
        __state = CharacterDropKillerFilter.IsFirePoolEmpty(__instance);
    }

    private static void Postfix(SE_Burning __instance, bool __state, bool __result)
    {
        CharacterDropKillerFilter.RecordFireSource(__instance, __state, __result);
    }
}

[HarmonyPatch(typeof(SE_Burning), nameof(SE_Burning.AddSpiritDamage), new[] { typeof(float) })]
internal static class CharacterDropKillerFilterSpiritSourcePatch
{
    private static void Prefix(SE_Burning __instance, out bool __state)
    {
        __state = CharacterDropKillerFilter.IsSpiritPoolEmpty(__instance);
    }

    private static void Postfix(SE_Burning __instance, bool __state, bool __result)
    {
        CharacterDropKillerFilter.RecordSpiritSource(__instance, __state, __result);
    }
}

[HarmonyPatch(typeof(SE_Poison), nameof(SE_Poison.UpdateStatusEffect), new[] { typeof(float) })]
internal static class CharacterDropKillerFilterPoisonTickPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(
        SE_Poison __instance,
        out CharacterDropKillerFilter.DelayedDamageTickScopeState __state)
    {
        __state = CharacterDropKillerFilter.BeginPoisonDamageTick(__instance);
    }

    private static Exception? Finalizer(
        CharacterDropKillerFilter.DelayedDamageTickScopeState __state,
        Exception? __exception)
    {
        CharacterDropKillerFilter.EndDelayedDamageTick(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(SE_Burning), nameof(SE_Burning.UpdateStatusEffect), new[] { typeof(float) })]
internal static class CharacterDropKillerFilterBurningTickPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(
        SE_Burning __instance,
        out CharacterDropKillerFilter.DelayedDamageTickScopeState __state)
    {
        __state = CharacterDropKillerFilter.BeginBurningDamageTick(__instance);
    }

    private static Exception? Finalizer(
        CharacterDropKillerFilter.DelayedDamageTickScopeState __state,
        Exception? __exception)
    {
        CharacterDropKillerFilter.EndDelayedDamageTick(__state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Character), "OnDeath")]
internal static class CharacterDropKillerFilterDeathScopePatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(
        Character __instance,
        out CharacterDropKillerFilter.DeathScopeState __state)
    {
        __state = CharacterDropKillerFilter.BeginDeathScope(__instance);
    }

    private static Exception? Finalizer(
        CharacterDropKillerFilter.DeathScopeState __state,
        Exception? __exception)
    {
        CharacterDropKillerFilter.EndDeathScope(__state);
        return __exception;
    }
}
