using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static class BossTamedPressureRuntime
{
    private const float DefaultRange = 24f;
    private const float DefaultScanInterval = 2f;
    private const float DefaultDamageInterval = 1f;
    private const int DefaultMaxTargetsPerBoss = 6;
    private const float DefaultPercentMaxHealthPerSecond = 0.007f;
    private const float DefaultMinBaseHealth = 300f;
    private const float DefaultIncomingDamageMultiplier = 1f;
    private const float DefaultOutgoingDamageMultiplier = 1f;
    private const float DefaultMessageInterval = 8f;
    private const string DefaultMessage = "Tamed creatures near a boss are weakened.";

    private static readonly int ActiveUntilKey = "DropNSpawn_BossTamedPressure_Until".GetStableHashCode();
    private static readonly int IncomingMultiplierKey = "DropNSpawn_BossTamedPressure_Incoming".GetStableHashCode();
    private static readonly int OutgoingMultiplierKey = "DropNSpawn_BossTamedPressure_Outgoing".GetStableHashCode();
    private static readonly int GenerationKey = "DropNSpawn_BossTamedPressure_Generation".GetStableHashCode();
    private static readonly List<Rule> Rules = new();
    private static int CurrentGeneration = 1;
    private static CharacterPrefabCatalog _characterPrefabCatalog = CharacterPrefabCatalog.Empty;

    private sealed class Rule
    {
        public HashSet<string> BossPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExcludedBossPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExcludedTamedPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExtraPressuredPrefabs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> BossPrefabHashes { get; } = new();
        public HashSet<int> ExcludedBossPrefabHashes { get; } = new();
        public HashSet<int> ExcludedTamedPrefabHashes { get; } = new();
        public HashSet<int> ExtraPressuredPrefabHashes { get; } = new();
        public float Range { get; set; }
        public float ScanInterval { get; set; }
        public float DamageInterval { get; set; }
        public int MaxTargetsPerBoss { get; set; }
        public float PercentMaxHealthPerSecond { get; set; }
        public float MinBaseHealth { get; set; }
        public float IncomingDamageMultiplier { get; set; }
        public float OutgoingDamageMultiplier { get; set; }
        public string? Message { get; set; }
        public float MessageInterval { get; set; }
        public double NextScanAt { get; set; }
        public double NextDamageAt { get; set; }
        public Dictionary<ZDOID, TrackedTarget> Targets { get; } = new();
        public Dictionary<long, double> NextMessageByPlayer { get; } = new();
    }

    private sealed class CharacterPrefabCatalog
    {
        public static CharacterPrefabCatalog Empty { get; } = new();

        public int GameDataSignature { get; set; } = -1;
        public HashSet<int> CharacterPrefabHashes { get; } = new();
        public HashSet<int> MonsterAiCharacterPrefabHashes { get; } = new();
        public HashSet<int> PlayerPrefabHashes { get; } = new();
        public Dictionary<int, string> PrefabNamesByHash { get; } = new();
        public Dictionary<int, float> BaseHealthByHash { get; } = new();
    }

    private sealed class BossCandidate
    {
        public ZDO Zdo { get; set; } = null!;
        public Vector3 Position { get; set; }
    }

    private sealed class TargetCandidate
    {
        public ZDO Zdo { get; set; } = null!;
        public Vector3 Position { get; set; }
        public float DistanceSqr { get; set; }
        public int Order { get; set; }
    }

    private sealed class TrackedTarget
    {
        public int PrefabHash { get; set; }
        public Vector3 LastKnownPosition { get; set; }
        public double ExpiresAt { get; set; }
    }

    internal static void Configure(IEnumerable<BossTamedPressureDefinition> definitions)
    {
        AdvanceGeneration();
        Rules.Clear();
        foreach (BossTamedPressureDefinition definition in definitions ?? Enumerable.Empty<BossTamedPressureDefinition>())
        {
            Rules.Add(CompileRule(definition));
        }
    }

    internal static void ExecuteServerTick()
    {
        double now = GetTimeSeconds();
        if (ZNet.instance == null)
        {
            return;
        }

        if (!DropNSpawnPlugin.IsRuntimeServer())
        {
            return;
        }

        if (!PluginSettingsFacade.IsCharacterDomainEnabled())
        {
            return;
        }

        if (!PluginSettingsFacade.IsBossTamedPressureEnabled())
        {
            return;
        }

        if (Rules.Count == 0)
        {
            return;
        }

        foreach (Rule rule in Rules)
        {
            if (now >= rule.NextScanAt)
            {
                ScanRule(rule, now);
                rule.NextScanAt = now + rule.ScanInterval;
            }

            if (now >= rule.NextDamageAt)
            {
                ApplyPeriodicDamage(rule, now);
                rule.NextDamageAt = now + rule.DamageInterval;
            }
        }
    }

    internal static void ApplyDamageMultipliers(Character? victim, HitData? hit)
    {
        if (victim == null ||
            hit == null ||
            !hit.HaveAttacker() ||
            !PluginSettingsFacade.IsCharacterDomainEnabled() ||
            !PluginSettingsFacade.IsBossTamedPressureEnabled())
        {
            return;
        }

        double now = GetTimeSeconds();
        float multiplier = 1f;

        float incomingMultiplier = 1f;
        bool incomingActive = TryGetCharacterZdo(victim, out ZDO? victimZdo) &&
                              TryGetActiveMultiplier(victimZdo, IncomingMultiplierKey, now, out incomingMultiplier);
        if (incomingActive)
        {
            multiplier *= incomingMultiplier;
        }

        ZDO? attackerZdo = ResolveAttackerZdo(hit);
        float outgoingMultiplier = 1f;
        bool outgoingActive = attackerZdo != null &&
                              TryGetActiveMultiplier(attackerZdo, OutgoingMultiplierKey, now, out outgoingMultiplier);
        if (outgoingActive)
        {
            multiplier *= outgoingMultiplier;
        }

        if (Mathf.Approximately(multiplier, 1f))
        {
            return;
        }

        float appliedMultiplier = Mathf.Max(0f, multiplier);
        hit.ApplyModifier(appliedMultiplier);
    }

    internal static string BuildRuleKey(BossTamedPressureDefinition definition)
    {
        return string.Join("|",
            string.Join(",", definition.BossPrefabs ?? new List<string>()),
            string.Join(",", definition.ExcludedBossPrefabs ?? new List<string>()),
            definition.Targets?.Range?.ToString("R") ?? "",
            definition.Targets?.ScanInterval?.ToString("R") ?? "",
            definition.Targets?.MaxPerBoss?.ToString() ?? "",
            string.Join(",", definition.Targets?.ExcludedTamedPrefabs ?? new List<string>()),
            string.Join(",", definition.Targets?.ExtraPressuredPrefabs ?? new List<string>()),
            definition.Pressure?.DamageInterval?.ToString("R") ?? "",
            definition.Pressure?.DamagePercentPerSecond?.ToString("R") ?? "",
            definition.Pressure?.DamageMinBaseHealth?.ToString("R") ?? "",
            definition.Pressure?.IncomingDamageMultiplier?.ToString("R") ?? "",
            definition.Pressure?.OutgoingDamageMultiplier?.ToString("R") ?? "",
            definition.Message ?? "",
            definition.MessageInterval?.ToString("R") ?? "");
    }

    private static Rule CompileRule(BossTamedPressureDefinition definition)
    {
        BossTamedPressureTargetsDefinition? targets = definition.Targets;
        BossTamedPressurePressureDefinition? pressure = definition.Pressure;
        Rule rule = new()
        {
            Range = targets?.Range ?? DefaultRange,
            ScanInterval = targets?.ScanInterval ?? DefaultScanInterval,
            DamageInterval = pressure?.DamageInterval ?? DefaultDamageInterval,
            MaxTargetsPerBoss = targets?.MaxPerBoss ?? DefaultMaxTargetsPerBoss,
            PercentMaxHealthPerSecond = pressure?.DamagePercentPerSecond ?? DefaultPercentMaxHealthPerSecond,
            MinBaseHealth = pressure?.DamageMinBaseHealth ?? DefaultMinBaseHealth,
            IncomingDamageMultiplier = pressure?.IncomingDamageMultiplier ?? DefaultIncomingDamageMultiplier,
            OutgoingDamageMultiplier = pressure?.OutgoingDamageMultiplier ?? DefaultOutgoingDamageMultiplier,
            Message = definition.Message ?? DefaultMessage,
            MessageInterval = definition.MessageInterval ?? DefaultMessageInterval
        };

        AddAll(rule.BossPrefabs, rule.BossPrefabHashes, definition.BossPrefabs);
        AddAll(rule.ExcludedBossPrefabs, rule.ExcludedBossPrefabHashes, definition.ExcludedBossPrefabs);
        AddAll(rule.ExcludedTamedPrefabs, rule.ExcludedTamedPrefabHashes, targets?.ExcludedTamedPrefabs);
        AddAll(rule.ExtraPressuredPrefabs, rule.ExtraPressuredPrefabHashes, targets?.ExtraPressuredPrefabs);
        return rule;
    }

    private static void ScanRule(Rule rule, double now)
    {
        CharacterPrefabCatalog catalog = EnsureCharacterPrefabCatalog();
        List<BossCandidate> bosses = new();
        BuildBossCandidates(rule, bosses, catalog);
        if (bosses.Count == 0)
        {
            return;
        }

        float rangeSqr = rule.Range * rule.Range;
        List<TargetCandidate> nearbyTargets = new();
        foreach (BossCandidate boss in bosses)
        {
            CollectTargetsNearBoss(rule, catalog, boss, rangeSqr, nearbyTargets);
            if (nearbyTargets.Count == 0)
            {
                continue;
            }

            if (nearbyTargets.Count > 1)
            {
                nearbyTargets.Sort(static (left, right) =>
                {
                    int distanceComparison = left.DistanceSqr.CompareTo(right.DistanceSqr);
                    return distanceComparison != 0 ? distanceComparison : left.Order.CompareTo(right.Order);
                });
            }

            int appliedCount = 0;
            foreach (TargetCandidate candidate in nearbyTargets)
            {
                if (candidate.Zdo.m_uid == boss.Zdo.m_uid)
                {
                    continue;
                }

                if (TrackTarget(rule, candidate.Zdo, candidate.Position, now))
                {
                    appliedCount++;
                }

                if (appliedCount >= rule.MaxTargetsPerBoss)
                {
                    break;
                }
            }
        }
    }

    private static void BuildBossCandidates(
        Rule rule,
        List<BossCandidate> bosses,
        CharacterPrefabCatalog catalog)
    {
        if (ZDOMan.instance == null)
        {
            return;
        }

        foreach (int bossPrefabHash in EnumerateBossPrefabHashes(rule))
        {
            if (!catalog.PrefabNamesByHash.TryGetValue(bossPrefabHash, out string prefabName) ||
                string.IsNullOrWhiteSpace(prefabName))
            {
                continue;
            }

            List<ZDO> bossZdos = new();
            int index = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, bossZdos, ref index))
            {
            }

            foreach (ZDO bossZdo in bossZdos)
            {
                if (IsValidLiveZdo(bossZdo, GetBaseHealth(catalog, bossPrefabHash)))
                {
                    bosses.Add(new BossCandidate
                    {
                        Zdo = bossZdo,
                        Position = bossZdo.GetPosition()
                    });
                }
            }
        }
    }

    private static int CollectTargetsNearBoss(
        Rule rule,
        CharacterPrefabCatalog catalog,
        BossCandidate boss,
        float rangeSqr,
        List<TargetCandidate> nearbyTargets)
    {
        nearbyTargets.Clear();
        if (ZDOMan.instance == null || ZoneSystem.instance == null)
        {
            return 0;
        }

        List<ZDO> sectorObjects = new();
        int sectorRange = Mathf.Max(0, Mathf.CeilToInt(rule.Range / ZoneSystem.c_ZoneSize) + 1);
        ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(boss.Position), sectorRange, 0, sectorObjects);

        int order = 0;
        foreach (ZDO candidate in sectorObjects)
        {
            if (candidate == null ||
                candidate.m_uid == boss.Zdo.m_uid ||
                !IsEligiblePressureTarget(rule, candidate, catalog))
            {
                continue;
            }

            Vector3 position = candidate.GetPosition();
            float distanceSqr = GetHorizontalDistanceSqr(boss.Position, position);
            if (distanceSqr > rangeSqr)
            {
                continue;
            }

            nearbyTargets.Add(new TargetCandidate
            {
                Zdo = candidate,
                Position = position,
                DistanceSqr = distanceSqr,
                Order = order++
            });
        }

        return sectorObjects.Count;
    }

    private static bool TrackTarget(
        Rule rule,
        ZDO zdo,
        Vector3 position,
        double now)
    {
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        ZDOID targetId = zdo.m_uid;
        int prefabHash = zdo.GetPrefab();
        double expiresAt = now + rule.ScanInterval + 0.5d;
        rule.Targets[targetId] = new TrackedTarget
        {
            PrefabHash = prefabHash,
            LastKnownPosition = position,
            ExpiresAt = expiresAt
        };

        float existingUntil = zdo.GetFloat(ActiveUntilKey, 0f);
        float newUntil = (float)Math.Max(existingUntil, expiresAt);
        zdo.Set(ActiveUntilKey, newUntil);

        float incoming = rule.IncomingDamageMultiplier;
        float outgoing = rule.OutgoingDamageMultiplier;
        if (existingUntil > now && zdo.GetInt(GenerationKey, 0) == CurrentGeneration)
        {
            incoming = Math.Max(zdo.GetFloat(IncomingMultiplierKey, 1f), incoming);
            outgoing = Math.Min(zdo.GetFloat(OutgoingMultiplierKey, 1f), outgoing);
        }

        zdo.Set(GenerationKey, CurrentGeneration);
        zdo.Set(IncomingMultiplierKey, Mathf.Clamp(incoming, 0f, 10f));
        zdo.Set(OutgoingMultiplierKey, Mathf.Clamp(outgoing, 0f, 10f));

        // Damage multipliers are evaluated by the damage owner, which can be a client on dedicated servers.
        ZDOMan.instance?.ForceSendZDO(targetId);
        return true;
    }

    private static void ApplyPeriodicDamage(Rule rule, double now)
    {
        if (rule.PercentMaxHealthPerSecond <= 0f || rule.DamageInterval <= 0f)
        {
            RemoveExpiredTargets(rule, now);
            return;
        }

        CharacterPrefabCatalog catalog = EnsureCharacterPrefabCatalog();
        foreach (ZDOID targetId in rule.Targets.Keys.ToArray())
        {
            if (!rule.Targets.TryGetValue(targetId, out TrackedTarget? target) || target.ExpiresAt < now)
            {
                rule.Targets.Remove(targetId);
                continue;
            }

            ZDO? zdo = ZDOMan.instance?.GetZDO(targetId);
            if (zdo == null || !IsEligiblePressureTarget(rule, zdo, catalog))
            {
                rule.Targets.Remove(targetId);
                continue;
            }

            target.PrefabHash = zdo.GetPrefab();
            target.LastKnownPosition = zdo.GetPosition();
            float baseHealth = Mathf.Max(GetMaxHealth(zdo, target.PrefabHash, catalog), rule.MinBaseHealth);
            float damage = baseHealth * rule.PercentMaxHealthPerSecond * rule.DamageInterval;
            if (damage <= 0f)
            {
                continue;
            }

            HitData hit = new()
            {
                m_hitType = HitData.HitType.Undefined,
                m_point = target.LastKnownPosition
            };
            hit.m_damage.m_damage = damage;
            ZRoutedRpc.instance?.InvokeRoutedRPC(zdo.GetOwner(), zdo.m_uid, "RPC_Damage", hit);
            TrySendMessage(rule, target.LastKnownPosition, now);
        }
    }

    private static void RemoveExpiredTargets(Rule rule, double now)
    {
        foreach (ZDOID targetId in rule.Targets.Keys.ToArray())
        {
            if (!rule.Targets.TryGetValue(targetId, out TrackedTarget? target) || target.ExpiresAt < now)
            {
                rule.Targets.Remove(targetId);
            }
        }
    }

    private static bool IsEligiblePressureTarget(Rule rule, ZDO zdo, CharacterPrefabCatalog catalog)
    {
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        int prefabHash = zdo.GetPrefab();
        if (prefabHash == 0 ||
            !catalog.CharacterPrefabHashes.Contains(prefabHash) ||
            catalog.PlayerPrefabHashes.Contains(prefabHash) ||
            !IsValidLiveZdo(zdo, GetBaseHealth(catalog, prefabHash)))
        {
            return false;
        }

        bool hasPrefabTargeting = rule.ExtraPressuredPrefabHashes.Count > 0 || rule.ExcludedTamedPrefabHashes.Count > 0;
        if (!hasPrefabTargeting)
        {
            return IsTamedMonsterAiZdo(zdo, prefabHash, catalog);
        }

        if (rule.ExtraPressuredPrefabHashes.Contains(prefabHash))
        {
            return true;
        }

        return IsTamedMonsterAiZdo(zdo, prefabHash, catalog) &&
               !rule.ExcludedTamedPrefabHashes.Contains(prefabHash);
    }

    private static bool IsTamedMonsterAiZdo(ZDO zdo, int prefabHash, CharacterPrefabCatalog catalog)
    {
        return zdo.GetBool(ZDOVars.s_tamed) &&
               catalog.MonsterAiCharacterPrefabHashes.Contains(prefabHash);
    }

    private static bool IsValidLiveZdo(ZDO? zdo, float baseHealth)
    {
        if (zdo == null || !zdo.IsValid())
        {
            return false;
        }

        float maxHealth = zdo.GetFloat(ZDOVars.s_maxHealth, Mathf.Max(baseHealth, 1f));
        return zdo.GetFloat(ZDOVars.s_health, maxHealth) > 0f;
    }

    private static float GetHorizontalDistanceSqr(Vector3 origin, Vector3 target)
    {
        float dx = target.x - origin.x;
        float dz = target.z - origin.z;
        return dx * dx + dz * dz;
    }

    private static CharacterPrefabCatalog EnsureCharacterPrefabCatalog()
    {
        int gameDataSignature = CharacterDropManager.ComputeGameDataSignatureForDespawnRuntime();
        if (_characterPrefabCatalog.GameDataSignature == gameDataSignature)
        {
            return _characterPrefabCatalog;
        }

        CharacterPrefabCatalog catalog = new()
        {
            GameDataSignature = gameDataSignature
        };

        foreach (GameObject prefab in CharacterDropManager.EnumeratePrefabsForDespawnRuntime())
        {
            if (prefab == null || !prefab.TryGetComponent(out Character character))
            {
                continue;
            }

            string prefabName = CharacterDropManager.GetPrefabNameForDespawnRuntime(prefab);
            if (string.IsNullOrWhiteSpace(prefabName))
            {
                continue;
            }

            int prefabHash = prefabName.GetStableHashCode();
            catalog.CharacterPrefabHashes.Add(prefabHash);
            catalog.PrefabNamesByHash[prefabHash] = prefabName;
            catalog.BaseHealthByHash[prefabHash] = Mathf.Max(character.m_health, 1f);
            if (prefab.GetComponent<MonsterAI>() != null)
            {
                catalog.MonsterAiCharacterPrefabHashes.Add(prefabHash);
            }

            if (character.IsPlayer())
            {
                catalog.PlayerPrefabHashes.Add(prefabHash);
            }
        }

        foreach (string bossPrefabName in CharacterBossPolicyRuntime.GetAutoDetectedBossPrefabNames())
        {
            string normalizedName = (bossPrefabName ?? "").Trim();
            if (normalizedName.Length == 0)
            {
                continue;
            }

            int prefabHash = normalizedName.GetStableHashCode();
            catalog.PrefabNamesByHash[prefabHash] = normalizedName;
        }

        _characterPrefabCatalog = catalog;
        return _characterPrefabCatalog;
    }

    private static IEnumerable<int> EnumerateBossPrefabHashes(Rule rule)
    {
        HashSet<int> yielded = new();
        foreach (string bossPrefabName in CharacterBossPolicyRuntime.GetAutoDetectedBossPrefabNames())
        {
            string normalizedName = (bossPrefabName ?? "").Trim();
            if (normalizedName.Length == 0)
            {
                continue;
            }

            int prefabHash = normalizedName.GetStableHashCode();
            if (prefabHash != 0 &&
                !rule.ExcludedBossPrefabHashes.Contains(prefabHash) &&
                yielded.Add(prefabHash))
            {
                yield return prefabHash;
            }
        }

        foreach (int prefabHash in rule.BossPrefabHashes)
        {
            if (prefabHash != 0 &&
                !rule.ExcludedBossPrefabHashes.Contains(prefabHash) &&
                yielded.Add(prefabHash))
            {
                yield return prefabHash;
            }
        }
    }

    private static float GetBaseHealth(CharacterPrefabCatalog catalog, int prefabHash)
    {
        return catalog.BaseHealthByHash.TryGetValue(prefabHash, out float baseHealth)
            ? Mathf.Max(baseHealth, 1f)
            : 1f;
    }

    private static float GetMaxHealth(ZDO zdo, int prefabHash, CharacterPrefabCatalog catalog)
    {
        float baseHealth = GetBaseHealth(catalog, prefabHash);
        int level = Mathf.Max(1, zdo.GetInt(ZDOVars.s_level, 1));
        return zdo.GetFloat(ZDOVars.s_maxHealth, baseHealth * level);
    }

    private static bool TryGetCharacterZdo(Character character, [NotNullWhen(true)] out ZDO? zdo)
    {
        zdo = character?.m_nview?.GetZDO();
        return zdo != null;
    }

    private static ZDO? ResolveAttackerZdo(HitData hit)
    {
        Character? attacker = hit.GetAttacker();
        if (attacker != null && TryGetCharacterZdo(attacker, out ZDO? characterZdo))
        {
            return characterZdo;
        }

        return !hit.m_attacker.IsNone() ? ZDOMan.instance?.GetZDO(hit.m_attacker) : null;
    }

    private static bool TryGetActiveMultiplier(ZDO zdo, int multiplierKey, double now, out float multiplier)
    {
        multiplier = 1f;
        if (zdo.GetFloat(ActiveUntilKey, 0f) <= now)
        {
            return false;
        }

        multiplier = Mathf.Clamp(zdo.GetFloat(multiplierKey, 1f), 0f, 10f);
        return !Mathf.Approximately(multiplier, 1f);
    }

    private static void TrySendMessage(
        Rule rule,
        Vector3 targetPosition,
        double now)
    {
        if (string.IsNullOrWhiteSpace(rule.Message))
        {
            return;
        }

        if (!SceneProximityQueries.TryFindNearestLivingPlayerInRangeXZ(targetPosition, Mathf.Max(rule.Range, 32f), out long playerId) ||
            playerId == 0L)
        {
            return;
        }

        float interval = Mathf.Max(rule.MessageInterval, rule.DamageInterval);
        if (rule.NextMessageByPlayer.TryGetValue(playerId, out double nextMessageAt) && now < nextMessageAt)
        {
            return;
        }

        rule.NextMessageByPlayer[playerId] = now + interval;
        if (playerId == ZNet.GetUID() && Player.m_localPlayer != null)
        {
            Player.m_localPlayer.Message(MessageHud.MessageType.TopLeft, rule.Message);
            return;
        }

        ZRoutedRpc.instance?.InvokeRoutedRPC(
            playerId,
            "ShowMessage",
            (int)MessageHud.MessageType.TopLeft,
            rule.Message);
    }

    private static double GetTimeSeconds()
    {
        return ZNet.instance?.GetTimeSeconds() ?? Time.time;
    }

    private static void AdvanceGeneration()
    {
        unchecked
        {
            CurrentGeneration++;
            if (CurrentGeneration <= 0)
            {
                CurrentGeneration = 1;
            }
        }
    }

    private static void AddAll(HashSet<string> target, HashSet<int> hashes, IEnumerable<string>? values)
    {
        if (values == null)
        {
            return;
        }

        foreach (string value in values)
        {
            string normalized = (value ?? "").Trim();
            if (normalized.Length > 0)
            {
                target.Add(normalized);
                hashes.Add(normalized.GetStableHashCode());
            }
        }
    }
}
