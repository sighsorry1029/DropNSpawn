using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace DropNSpawn;

internal static partial class LocationManager
{
    private const string VegvisirGlobalEffectClearStatusKey = "DNS_ClearStatus";
    private static readonly object VegvisirGlobalEffectsLock = new();
    private static readonly System.Random VegvisirGlobalEffectsRandom = new();
    private static readonly ConditionalWeakTable<Vegvisir, VegvisirGlobalEffectSelectionState> VegvisirGlobalEffectSelections = new();
    private static readonly HashSet<string> VegvisirGlobalEffectMissingEffectPrefabWarnings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VegvisirGlobalEffectInvalidEffectPrefabWarnings = new(StringComparer.OrdinalIgnoreCase);

    private sealed class VegvisirGlobalEffectCandidate
    {
        public LocationVegvisirGlobalEffectDefinition Definition { get; set; } = new();
        public StatusEffect? StatusEffect { get; set; }
        public Heightmap.Biome SourceBiome { get; set; }
        public float Weight { get; set; }
        public bool ClearsStatusEffects { get; set; }
        public string EffectKey { get; set; } = "";
    }

    private sealed class VegvisirGlobalEffectSelectionState
    {
        public string CandidateSignature { get; set; } = "";
        public int SelectedIndex { get; set; } = -1;
        public Dictionary<long, DateTime> LastGrantedByPlayer { get; } = new();
    }

    internal static void TryApplyVegvisirGlobalEffects(
        Vegvisir? vegvisir,
        Humanoid? character,
        bool hold,
        bool interactionSucceeded)
    {
        if (!interactionSucceeded ||
            hold ||
            vegvisir == null ||
            character is not Player player ||
            player != Player.m_localPlayer ||
            !PluginSettingsFacade.IsLocationDomainEnabled() ||
            !PluginSettingsFacade.IsVegvisirGlobalEffectsEnabled() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location) ||
            !IsGameDataReady() ||
            !VegvisirCanOpenMap(vegvisir) ||
            !HasVegvisirGlobalEffectsConfiguration())
        {
            return;
        }

        TryApplyVegvisirGlobalEffects(player, vegvisir);
    }

    private static LocationVegvisirGlobalEffectsDefinition? GetEffectiveVegvisirGlobalEffectsDefinition()
    {
        for (int index = _configuration.Count - 1; index >= 0; index--)
        {
            LocationVegvisirGlobalEffectsDefinition? definition = _configuration[index].VegvisirGlobalEffects;
            if (HasVegvisirGlobalEffectsOverride(definition))
            {
                return definition;
            }
        }

        return null;
    }

    private static void TryApplyVegvisirGlobalEffects(Player player, Vegvisir vegvisir)
    {
        if (player != Player.m_localPlayer ||
            !PluginSettingsFacade.IsLocationDomainEnabled() ||
            !PluginSettingsFacade.IsVegvisirGlobalEffectsEnabled() ||
            DropNSpawnPlugin.IsGameDataRefreshDeferred(DropNSpawnPlugin.ReloadDomain.Location) ||
            !IsGameDataReady())
        {
            return;
        }

        LocationVegvisirGlobalEffectsDefinition? definition = GetEffectiveVegvisirGlobalEffectsDefinition();
        if (definition?.Biomes == null || definition.Biomes.Count == 0)
        {
            return;
        }

        Heightmap.Biome sourceBiome = GetVegvisirGlobalEffectBiome(vegvisir.transform.position);
        List<VegvisirGlobalEffectCandidate> candidates = BuildVegvisirGlobalEffectCandidates(sourceBiome, definition);
        VegvisirGlobalEffectSelectionState state = VegvisirGlobalEffectSelections.GetOrCreateValue(vegvisir);
        VegvisirGlobalEffectCandidate? selected = SelectVegvisirGlobalEffectCandidate(state, candidates);
        if (selected == null)
        {
            return;
        }

        long playerId = player.GetPlayerID();
        if (IsVegvisirGlobalEffectOnCooldown(
                state,
                playerId,
                selected,
                out int remainingCooldownSeconds,
                out bool alreadyReceived))
        {
            if (alreadyReceived)
            {
                QueueVegvisirGlobalEffectMessage(player, selected);
                return;
            }

            QueueVegvisirGlobalEffectCooldownMessage(player, remainingCooldownSeconds);
            return;
        }

        if (!TryGrantVegvisirGlobalEffect(
                player,
                selected,
                out bool alreadyActive))
        {
            if (alreadyActive && selected.StatusEffect != null)
            {
                QueueVegvisirGlobalEffectAlreadyActiveMessage(player, selected.StatusEffect);
            }

            return;
        }

        SetVegvisirGlobalEffectLastGranted(state, playerId, selected);
        SpawnVegvisirGlobalEffectPrefab(vegvisir, selected.Definition);
        QueueVegvisirGlobalEffectMessage(player, selected);
    }

    private static List<VegvisirGlobalEffectCandidate> BuildVegvisirGlobalEffectCandidates(
        Heightmap.Biome sourceBiome,
        LocationVegvisirGlobalEffectsDefinition definition)
    {
        List<VegvisirGlobalEffectCandidate> candidates = new();
        foreach (LocationVegvisirGlobalEffectsBiomeDefinition biome in definition.Biomes ?? Enumerable.Empty<LocationVegvisirGlobalEffectsBiomeDefinition>())
        {
            if (!MatchesVegvisirGlobalEffectBiome(sourceBiome, biome))
            {
                continue;
            }

            foreach (LocationVegvisirGlobalEffectDefinition effect in biome.StatusEffects ?? Enumerable.Empty<LocationVegvisirGlobalEffectDefinition>())
            {
                float weight = Mathf.Max(0f, effect.Weight ?? 1f);
                if (weight <= 0f)
                {
                    continue;
                }

                string effectName = (effect.StatusEffect ?? "").Trim();
                if (IsVegvisirGlobalClearStatusEffect(effectName))
                {
                    candidates.Add(new VegvisirGlobalEffectCandidate
                    {
                        Definition = effect,
                        ClearsStatusEffects = true,
                        EffectKey = VegvisirGlobalEffectClearStatusKey,
                        SourceBiome = sourceBiome,
                        Weight = weight
                    });
                    continue;
                }

                StatusEffect? statusEffect = ResolveStatusEffect(effectName, "vegvisirGlobalEffects/statusEffects/statusEffect");
                if (statusEffect == null)
                {
                    continue;
                }

                candidates.Add(new VegvisirGlobalEffectCandidate
                {
                    Definition = effect,
                    StatusEffect = statusEffect,
                    EffectKey = GetVegvisirGlobalEffectNameKey(statusEffect),
                    SourceBiome = sourceBiome,
                    Weight = weight
                });
            }
        }

        return candidates;
    }

    private static VegvisirGlobalEffectCandidate? SelectVegvisirGlobalEffectCandidate(
        VegvisirGlobalEffectSelectionState state,
        List<VegvisirGlobalEffectCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        string candidateSignature = BuildVegvisirGlobalEffectCandidateSignature(candidates);
        lock (VegvisirGlobalEffectsLock)
        {
            if (string.Equals(state.CandidateSignature, candidateSignature, StringComparison.Ordinal) &&
                state.SelectedIndex >= 0 &&
                state.SelectedIndex < candidates.Count)
            {
                return candidates[state.SelectedIndex];
            }

            state.CandidateSignature = candidateSignature;
            state.SelectedIndex = SelectVegvisirGlobalEffectCandidateIndex(candidates);
            state.LastGrantedByPlayer.Clear();
            return state.SelectedIndex >= 0 ? candidates[state.SelectedIndex] : null;
        }
    }

    private static int SelectVegvisirGlobalEffectCandidateIndex(List<VegvisirGlobalEffectCandidate> candidates)
    {
        float totalWeight = 0f;
        foreach (VegvisirGlobalEffectCandidate candidate in candidates)
        {
            totalWeight += candidate.Weight;
        }

        if (totalWeight <= 0f)
        {
            return -1;
        }

        double roll = VegvisirGlobalEffectsRandom.NextDouble() * totalWeight;

        float cursor = 0f;
        for (int index = 0; index < candidates.Count; index++)
        {
            cursor += candidates[index].Weight;
            if (roll < cursor)
            {
                return index;
            }
        }

        return candidates.Count - 1;
    }

    private static string BuildVegvisirGlobalEffectCandidateSignature(List<VegvisirGlobalEffectCandidate> candidates)
    {
        StringBuilder builder = new();
        builder.Append(_configurationSignature ?? "");
        foreach (VegvisirGlobalEffectCandidate candidate in candidates)
        {
            builder
                .Append('|')
                .Append(((int)candidate.SourceBiome).ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(candidate.EffectKey)
                .Append(':')
                .Append(candidate.Weight.ToString("R", CultureInfo.InvariantCulture))
                .Append(':')
                .Append(
                    GetEffectiveVegvisirGlobalEffectCooldownSeconds(candidate)
                        .ToString("R", CultureInfo.InvariantCulture))
                .Append(':')
                .Append(FormatVegvisirGlobalEffectSignatureFloat(candidate.Definition.DurationSeconds))
                .Append(':')
                .Append(candidate.Definition.EffectPrefab ?? "");
        }

        return builder.ToString();
    }

    private static string FormatVegvisirGlobalEffectSignatureFloat(float? value)
    {
        return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "";
    }

    private static bool MatchesVegvisirGlobalEffectBiome(
        Heightmap.Biome sourceBiome,
        LocationVegvisirGlobalEffectsBiomeDefinition definition)
    {
        string configuredBiome = (definition.Biome ?? "").Trim();
        if (configuredBiome.Length == 0)
        {
            return true;
        }

        if (configuredBiome.Contains(',') ||
            configuredBiome.StartsWith("[", StringComparison.Ordinal) ||
            configuredBiome.EndsWith("]", StringComparison.Ordinal))
        {
            WarnInvalidEntry(
                $"vegvisirGlobalEffects biome key '{configuredBiome}' is ignored. Use one biome name per row, or All to match every biome.");
            return false;
        }

        if (configuredBiome == "*")
        {
            WarnInvalidEntry("vegvisirGlobalEffects uses '*' as a biome wildcard. Use All instead.");
            return false;
        }

        if (!BiomeResolutionSupport.TryResolveBiomeToken(configuredBiome, out Heightmap.Biome biomeMask))
        {
            WarnInvalidEntry($"vegvisirGlobalEffects uses unknown biome '{configuredBiome}'. That biome block is ignored.");
            return false;
        }

        return biomeMask == Heightmap.Biome.All || (sourceBiome & biomeMask) != 0;
    }

    private static bool TryGrantVegvisirGlobalEffect(
        Player player,
        VegvisirGlobalEffectCandidate candidate,
        out bool alreadyActive)
    {
        alreadyActive = false;
        SEMan seMan = player.GetSEMan();

        if (candidate.ClearsStatusEffects)
        {
            ClearVegvisirGlobalStatusEffects(player, seMan);
            return true;
        }

        StatusEffect? statusEffect = candidate.StatusEffect;
        if (statusEffect == null)
        {
            return false;
        }

        StatusEffect? existing = seMan.GetStatusEffect(statusEffect.NameHash());
        if (existing != null)
        {
            alreadyActive = true;
            return false;
        }

        RemoveVegvisirGlobalEffectCategoryConflicts(seMan, statusEffect);

        StatusEffect? added = seMan.AddStatusEffect(statusEffect, resetTime: true, itemLevel: 0, skillLevel: 0f);
        StatusEffect? granted = added ?? seMan.GetStatusEffect(statusEffect.NameHash());
        if (granted == null)
        {
            return false;
        }

        ApplyVegvisirGlobalEffectDuration(granted, candidate.Definition);
        return true;
    }

    private static void ClearVegvisirGlobalStatusEffects(Player player, SEMan seMan)
    {
        player.ClearHardDeath();
        seMan.RemoveAllStatusEffects(quiet: true);
    }

    private static void ApplyVegvisirGlobalEffectDuration(
        StatusEffect activeStatusEffect,
        LocationVegvisirGlobalEffectDefinition definition)
    {
        float durationSeconds = GetEffectiveVegvisirGlobalEffectDurationSeconds(definition, activeStatusEffect);
        if (durationSeconds <= 0f)
        {
            return;
        }

        activeStatusEffect.m_ttl = durationSeconds;
        activeStatusEffect.m_time = 0f;
    }

    private static float GetEffectiveVegvisirGlobalEffectDurationSeconds(
        LocationVegvisirGlobalEffectDefinition definition,
        StatusEffect statusEffect)
    {
        if (definition.DurationSeconds.HasValue && definition.DurationSeconds.Value > 0f)
        {
            return definition.DurationSeconds.Value;
        }

        return statusEffect.m_ttl > 0f ? statusEffect.m_ttl : 0f;
    }

    private static void SpawnVegvisirGlobalEffectPrefab(
        Vegvisir vegvisir,
        LocationVegvisirGlobalEffectDefinition definition)
    {
        string effectPrefabName = (definition.EffectPrefab ?? "").Trim();
        if (effectPrefabName.Length == 0 || ZNetScene.instance == null)
        {
            return;
        }

        if (!IsAllowedVegvisirGlobalEffectPrefabName(effectPrefabName))
        {
            WarnInvalidVegvisirGlobalEffectPrefab(effectPrefabName);
            return;
        }

        GameObject? prefab = ZNetScene.instance.GetPrefab(effectPrefabName);
        if (prefab == null)
        {
            WarnMissingVegvisirGlobalEffectPrefab(effectPrefabName);
            return;
        }

        ZNetScene.instance.SpawnObject(
            vegvisir.transform.position,
            vegvisir.transform.rotation,
            prefab);
    }

    private static bool IsAllowedVegvisirGlobalEffectPrefabName(string effectPrefabName)
    {
        return effectPrefabName.StartsWith("vfx_", StringComparison.OrdinalIgnoreCase) ||
               effectPrefabName.StartsWith("sfx_", StringComparison.OrdinalIgnoreCase) ||
               effectPrefabName.StartsWith("fx_", StringComparison.OrdinalIgnoreCase);
    }

    private static void WarnInvalidVegvisirGlobalEffectPrefab(string effectPrefabName)
    {
        lock (VegvisirGlobalEffectsLock)
        {
            if (!VegvisirGlobalEffectInvalidEffectPrefabWarnings.Add(effectPrefabName))
            {
                return;
            }
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"vegvisirGlobalEffects effectPrefab '{effectPrefabName}' is ignored. effectPrefab must start with vfx_, sfx_, or fx_ (case-insensitive).");
    }

    private static void WarnMissingVegvisirGlobalEffectPrefab(string effectPrefabName)
    {
        lock (VegvisirGlobalEffectsLock)
        {
            if (!VegvisirGlobalEffectMissingEffectPrefabWarnings.Add(effectPrefabName))
            {
                return;
            }
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(
            $"vegvisirGlobalEffects references unknown effect prefab '{effectPrefabName}'.");
    }

    private static void RemoveVegvisirGlobalEffectCategoryConflicts(SEMan seMan, StatusEffect statusEffect)
    {
        string category = statusEffect.m_category;
        if (string.IsNullOrWhiteSpace(category))
        {
            return;
        }

        int selectedHash = statusEffect.NameHash();
        List<StatusEffect> statusEffects = seMan.GetStatusEffects();
        for (int index = statusEffects.Count - 1; index >= 0; index--)
        {
            StatusEffect current = statusEffects[index];
            if (current != null &&
                current.NameHash() != selectedHash &&
                string.Equals(current.m_category, category, StringComparison.Ordinal))
            {
                seMan.RemoveStatusEffect(current, quiet: true);
            }
        }
    }

    private static bool IsVegvisirGlobalEffectOnCooldown(
        VegvisirGlobalEffectSelectionState state,
        long playerId,
        VegvisirGlobalEffectCandidate candidate,
        out int remainingCooldownSeconds,
        out bool alreadyReceived)
    {
        remainingCooldownSeconds = 0;
        alreadyReceived = false;

        float cooldownSeconds = GetEffectiveVegvisirGlobalEffectCooldownSeconds(candidate);
        if (cooldownSeconds == 0f)
        {
            return false;
        }

        DateTime lastGrantedAt;
        lock (VegvisirGlobalEffectsLock)
        {
            if (!state.LastGrantedByPlayer.TryGetValue(playerId, out lastGrantedAt))
            {
                return false;
            }
        }

        if (cooldownSeconds < 0f)
        {
            alreadyReceived = true;
            return true;
        }

        double elapsedSeconds = (GetVegvisirGlobalEffectNow() - lastGrantedAt).TotalSeconds;
        if (elapsedSeconds < 0)
        {
            remainingCooldownSeconds = Mathf.CeilToInt(cooldownSeconds);
            return true;
        }

        double remainingSeconds = cooldownSeconds - elapsedSeconds;
        if (remainingSeconds <= 0.0)
        {
            return false;
        }

        remainingCooldownSeconds = Mathf.Max(1, Mathf.CeilToInt((float)remainingSeconds));
        return true;
    }

    private static void SetVegvisirGlobalEffectLastGranted(
        VegvisirGlobalEffectSelectionState state,
        long playerId,
        VegvisirGlobalEffectCandidate candidate)
    {
        if (GetEffectiveVegvisirGlobalEffectCooldownSeconds(candidate) == 0f)
        {
            lock (VegvisirGlobalEffectsLock)
            {
                state.LastGrantedByPlayer.Remove(playerId);
            }

            return;
        }

        lock (VegvisirGlobalEffectsLock)
        {
            state.LastGrantedByPlayer[playerId] = GetVegvisirGlobalEffectNow();
        }
    }

    private static float GetEffectiveVegvisirGlobalEffectCooldownSeconds(VegvisirGlobalEffectCandidate candidate)
    {
        return GetEffectiveVegvisirGlobalEffectCooldownSeconds(candidate.Definition, candidate.StatusEffect);
    }

    private static float GetEffectiveVegvisirGlobalEffectCooldownSeconds(
        LocationVegvisirGlobalEffectDefinition definition,
        StatusEffect? statusEffect)
    {
        if (definition.CooldownSeconds.HasValue)
        {
            return definition.CooldownSeconds.Value;
        }

        float prefabCooldown = statusEffect?.m_cooldown ?? 0f;
        return prefabCooldown > 0f ? prefabCooldown : 0f;
    }

    private static string GetVegvisirGlobalEffectNameKey(StatusEffect statusEffect)
    {
        return string.IsNullOrWhiteSpace(statusEffect.name)
            ? statusEffect.m_name
            : statusEffect.name;
    }

    private static bool IsVegvisirGlobalClearStatusEffect(string? statusEffectName)
    {
        string trimmedName = (statusEffectName ?? "").Trim();
        return string.Equals(trimmedName, VegvisirGlobalEffectClearStatusKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasVegvisirGlobalEffectsConfiguration()
    {
        LocationVegvisirGlobalEffectsDefinition? definition = GetEffectiveVegvisirGlobalEffectsDefinition();
        return definition?.Biomes != null && definition.Biomes.Count > 0;
    }

    private static bool VegvisirCanOpenMap(Vegvisir vegvisir)
    {
        return vegvisir.m_locations != null && vegvisir.m_locations.Any(location => location != null && location.m_showMap);
    }

    private static DateTime GetVegvisirGlobalEffectNow()
    {
        return ZNet.instance != null ? ZNet.instance.GetTime() : DateTime.UtcNow;
    }

    private static Heightmap.Biome GetVegvisirGlobalEffectBiome(Vector3 position)
    {
        return WorldGenerator.instance != null
            ? WorldGenerator.instance.GetBiome(position)
            : Heightmap.FindBiome(position);
    }

    private static void QueueVegvisirGlobalEffectMessage(Player player, VegvisirGlobalEffectCandidate candidate)
    {
        LocationVegvisirGlobalEffectsLocalizationDefinition? localize = GetVegvisirGlobalEffectsLocalization();
        if (candidate.ClearsStatusEffects)
        {
            QueueVegvisirGlobalEffectCenterMessage(
                player,
                GetVegvisirGlobalEffectLocalizedText(localize?.YouGotBamboozled, "You got bamboozled"));
            return;
        }

        if (candidate.StatusEffect == null)
        {
            return;
        }

        QueueVegvisirGlobalEffectCenterMessage(
            player,
            FormatVegvisirGlobalEffectNamedMessage(
                GetVegvisirGlobalEffectLocalizedText(localize?.YouHaveReceived, "You have received {name}"),
                GetVegvisirGlobalEffectDisplayName(candidate.StatusEffect)));
    }

    private static void QueueVegvisirGlobalEffectCooldownMessage(Player player, int remainingCooldownSeconds)
    {
        LocationVegvisirGlobalEffectsLocalizationDefinition? localize = GetVegvisirGlobalEffectsLocalization();
        QueueVegvisirGlobalEffectCenterMessage(
            player,
            FormatVegvisirGlobalEffectCooldownMessage(
                GetVegvisirGlobalEffectLocalizedText(localize?.BuffCooldownNs, "Buff Cooldown {seconds}s"),
                Mathf.Max(1, remainingCooldownSeconds)));
    }

    private static void QueueVegvisirGlobalEffectAlreadyActiveMessage(Player player, StatusEffect statusEffect)
    {
        LocationVegvisirGlobalEffectsLocalizationDefinition? localize = GetVegvisirGlobalEffectsLocalization();
        QueueVegvisirGlobalEffectCenterMessage(
            player,
            FormatVegvisirGlobalEffectNamedMessage(
                GetVegvisirGlobalEffectLocalizedText(localize?.AlreadyActive, "Already active {name}"),
                GetVegvisirGlobalEffectDisplayName(statusEffect)));
    }

    private static LocationVegvisirGlobalEffectsLocalizationDefinition? GetVegvisirGlobalEffectsLocalization()
    {
        return GetEffectiveVegvisirGlobalEffectsDefinition()?.Localize;
    }

    private static string GetVegvisirGlobalEffectLocalizedText(string? configuredText, string fallbackText)
    {
        string trimmedText = (configuredText ?? "").Trim();
        return trimmedText.Length == 0 ? fallbackText : trimmedText;
    }

    private static string FormatVegvisirGlobalEffectNamedMessage(string message, string displayName)
    {
        return message.IndexOf("{name}", StringComparison.Ordinal) >= 0
            ? message.Replace("{name}", displayName)
            : $"{message} \"{displayName}\"";
    }

    private static string FormatVegvisirGlobalEffectCooldownMessage(string message, int remainingCooldownSeconds)
    {
        string seconds = remainingCooldownSeconds.ToString(CultureInfo.InvariantCulture);
        if (message.IndexOf("{seconds}", StringComparison.Ordinal) >= 0)
        {
            return message.Replace("{seconds}", seconds);
        }

        if (message.IndexOf('N') >= 0)
        {
            return message.Replace("N", seconds);
        }

        return $"{message} {seconds}s";
    }

    private static void QueueVegvisirGlobalEffectCenterMessage(Player player, string message)
    {
        DropNSpawnPlugin.Instance?.StartCoroutine(ShowVegvisirGlobalEffectCenterMessageAfterDelay(
            player.GetPlayerID(),
            message));
    }

    private static IEnumerator ShowVegvisirGlobalEffectCenterMessageAfterDelay(long playerId, string message)
    {
        yield return new WaitForSeconds(1f);

        Player? player = Player.m_localPlayer;
        if (player == null || player.GetPlayerID() != playerId)
        {
            yield break;
        }

        player.Message(MessageHud.MessageType.Center, message);
    }

    private static string GetVegvisirGlobalEffectDisplayName(StatusEffect statusEffect)
    {
        string rawName = string.IsNullOrWhiteSpace(statusEffect.m_name)
            ? GetVegvisirGlobalEffectNameKey(statusEffect)
            : statusEffect.m_name;
        string localizedName = Localization.instance != null
            ? Localization.instance.Localize(rawName)
            : rawName;
        return string.IsNullOrWhiteSpace(localizedName)
            ? GetVegvisirGlobalEffectNameKey(statusEffect)
            : localizedName;
    }
}
