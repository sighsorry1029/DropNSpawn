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

    private sealed class BossCandidate
    {
        public Character Character { get; set; } = null!;
        public Vector3 Position { get; set; }
    }

    private sealed class TargetCandidate
    {
        public Character Character { get; set; } = null!;
        public Vector3 Position { get; set; }
        public int Order { get; set; }
    }

    private sealed class TrackedTarget
    {
        public Character Character { get; set; } = null!;
        public double ExpiresAt { get; set; }
    }

    private readonly struct BucketKey : IEquatable<BucketKey>
    {
        public BucketKey(int x, int z)
        {
            X = x;
            Z = z;
        }

        public int X { get; }
        public int Z { get; }

        public bool Equals(BucketKey other) => X == other.X && Z == other.Z;

        public override bool Equals(object? obj) => obj is BucketKey other && Equals(other);

        public override int GetHashCode() => (X * 397) ^ Z;
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
        if (!DropNSpawnPlugin.IsRuntimeServer() ||
            !PluginSettingsFacade.IsCharacterDomainEnabled() ||
            !PluginSettingsFacade.IsBossTamedPressureEnabled() ||
            Rules.Count == 0 ||
            ZNet.instance == null)
        {
            return;
        }

        double now = GetTimeSeconds();
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
            !PluginSettingsFacade.IsBossTamedPressureEnabled() ||
            Rules.Count == 0)
        {
            return;
        }

        double now = GetTimeSeconds();
        float multiplier = 1f;

        if (TryGetCharacterZdo(victim, out ZDO? victimZdo) &&
            TryGetActiveMultiplier(victimZdo, IncomingMultiplierKey, now, out float incomingMultiplier))
        {
            multiplier *= incomingMultiplier;
        }

        ZDO? attackerZdo = ResolveAttackerZdo(hit);
        if (attackerZdo != null &&
            TryGetActiveMultiplier(attackerZdo, OutgoingMultiplierKey, now, out float outgoingMultiplier))
        {
            multiplier *= outgoingMultiplier;
        }

        if (Mathf.Approximately(multiplier, 1f))
        {
            return;
        }

        hit.ApplyModifier(Mathf.Max(0f, multiplier));
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
        List<Character> characters = Character.GetAllCharacters();
        if (characters == null || characters.Count == 0)
        {
            return;
        }

        float bucketSize = Mathf.Max(rule.Range, 1f);
        List<BossCandidate> bosses = new();
        BuildBossCandidates(rule, characters, bosses);
        if (bosses.Count == 0)
        {
            return;
        }

        Dictionary<BucketKey, List<TargetCandidate>> targetBuckets = new();
        BuildTargetBuckets(rule, characters, bucketSize, targetBuckets);
        if (targetBuckets.Count == 0)
        {
            return;
        }

        float rangeSqr = rule.Range * rule.Range;
        List<TargetCandidate> nearbyTargets = new();
        foreach (BossCandidate boss in bosses)
        {
            CollectTargetsNearBoss(rule, targetBuckets, bucketSize, boss.Position, rangeSqr, nearbyTargets);
            if (nearbyTargets.Count == 0)
            {
                continue;
            }

            if (nearbyTargets.Count > 1)
            {
                nearbyTargets.Sort(static (left, right) => left.Order.CompareTo(right.Order));
            }

            int appliedCount = 0;
            foreach (TargetCandidate candidate in nearbyTargets)
            {
                if (ReferenceEquals(candidate.Character, boss.Character) ||
                    !IsValidCharacter(candidate.Character))
                {
                    continue;
                }

                TrackTarget(rule, candidate.Character, now);
                appliedCount++;
                if (appliedCount >= rule.MaxTargetsPerBoss)
                {
                    break;
                }
            }
        }
    }

    private static void BuildBossCandidates(
        Rule rule,
        List<Character> characters,
        List<BossCandidate> bosses)
    {
        for (int index = 0; index < characters.Count; index++)
        {
            Character character = characters[index];
            if (!IsValidCharacter(character) || !TryGetCharacterZdo(character, out ZDO? zdo))
            {
                continue;
            }

            int prefabHash = zdo.GetPrefab();
            if (IsBossSource(rule, character, prefabHash))
            {
                bosses.Add(new BossCandidate
                {
                    Character = character,
                    Position = character.GetCenterPoint()
                });
            }
        }
    }

    private static void BuildTargetBuckets(
        Rule rule,
        List<Character> characters,
        float bucketSize,
        Dictionary<BucketKey, List<TargetCandidate>> targetBuckets)
    {
        for (int index = 0; index < characters.Count; index++)
        {
            Character character = characters[index];
            if (!IsValidCharacter(character) || !TryGetCharacterZdo(character, out ZDO? zdo))
            {
                continue;
            }

            int prefabHash = zdo.GetPrefab();
            if (!IsEligiblePressureTarget(rule, character, zdo, prefabHash))
            {
                continue;
            }

            Vector3 position = character.transform.position;
            TargetCandidate target = new()
            {
                Character = character,
                Position = position,
                Order = index
            };

            BucketKey key = GetBucketKey(position, bucketSize);
            if (!targetBuckets.TryGetValue(key, out List<TargetCandidate> bucket))
            {
                bucket = new List<TargetCandidate>();
                targetBuckets[key] = bucket;
            }

            bucket.Add(target);
        }
    }

    private static void CollectTargetsNearBoss(
        Rule rule,
        Dictionary<BucketKey, List<TargetCandidate>> targetBuckets,
        float bucketSize,
        Vector3 bossPosition,
        float rangeSqr,
        List<TargetCandidate> nearbyTargets)
    {
        nearbyTargets.Clear();
        int minX = GetBucketCoordinate(bossPosition.x - rule.Range, bucketSize);
        int maxX = GetBucketCoordinate(bossPosition.x + rule.Range, bucketSize);
        int minZ = GetBucketCoordinate(bossPosition.z - rule.Range, bucketSize);
        int maxZ = GetBucketCoordinate(bossPosition.z + rule.Range, bucketSize);

        for (int x = minX; x <= maxX; x++)
        {
            for (int z = minZ; z <= maxZ; z++)
            {
                if (!targetBuckets.TryGetValue(new BucketKey(x, z), out List<TargetCandidate> bucket))
                {
                    continue;
                }

                foreach (TargetCandidate candidate in bucket)
                {
                    if (IsWithinHorizontalRange(bossPosition, candidate.Position, rangeSqr))
                    {
                        nearbyTargets.Add(candidate);
                    }
                }
            }
        }
    }

    private static void TrackTarget(Rule rule, Character target, double now)
    {
        if (!TryGetCharacterZdo(target, out ZDO? zdo))
        {
            return;
        }

        ZDOID targetId = zdo.m_uid;
        double expiresAt = now + rule.ScanInterval + 0.5d;
        rule.Targets[targetId] = new TrackedTarget
        {
            Character = target,
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
    }

    private static void ApplyPeriodicDamage(Rule rule, double now)
    {
        if (rule.PercentMaxHealthPerSecond <= 0f || rule.DamageInterval <= 0f)
        {
            RemoveExpiredTargets(rule, now);
            return;
        }

        foreach (ZDOID targetId in rule.Targets.Keys.ToArray())
        {
            if (!rule.Targets.TryGetValue(targetId, out TrackedTarget? target) ||
                target.ExpiresAt < now ||
                !IsEligiblePressureTarget(rule, target.Character))
            {
                rule.Targets.Remove(targetId);
                continue;
            }

            float baseHealth = Mathf.Max(target.Character.GetMaxHealth(), rule.MinBaseHealth);
            float damage = baseHealth * rule.PercentMaxHealthPerSecond * rule.DamageInterval;
            if (damage <= 0f)
            {
                continue;
            }

            HitData hit = new()
            {
                m_hitType = HitData.HitType.Undefined
            };
            hit.m_damage.m_damage = damage;
            target.Character.Damage(hit);
            TrySendMessage(rule, target.Character, now);
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

    private static bool IsBossSource(Rule rule, Character character, int prefabHash)
    {
        if (prefabHash == 0 || rule.ExcludedBossPrefabHashes.Contains(prefabHash))
        {
            return false;
        }

        return character.IsBoss() ||
               CharacterBossPolicyRuntime.IsAutoDetectedBossPrefab(prefabHash) ||
               rule.BossPrefabHashes.Contains(prefabHash);
    }

    private static bool IsEligiblePressureTarget(Rule rule, Character? character)
    {
        if (!IsValidCharacter(character) ||
            character!.IsPlayer() ||
            !TryGetCharacterZdo(character, out ZDO? zdo))
        {
            return false;
        }

        return IsEligiblePressureTarget(rule, character, zdo, zdo.GetPrefab());
    }

    private static bool IsEligiblePressureTarget(Rule rule, Character character, ZDO zdo, int prefabHash)
    {
        if (zdo == null || character.IsPlayer())
        {
            return false;
        }

        bool hasPrefabTargeting = rule.ExtraPressuredPrefabHashes.Count > 0 || rule.ExcludedTamedPrefabHashes.Count > 0;
        if (!hasPrefabTargeting)
        {
            return IsTamedMonsterAi(character);
        }

        if (prefabHash == 0)
        {
            return false;
        }

        if (rule.ExtraPressuredPrefabHashes.Contains(prefabHash))
        {
            return true;
        }

        return IsTamedMonsterAi(character) &&
               !rule.ExcludedTamedPrefabHashes.Contains(prefabHash);
    }

    private static bool IsTamedMonsterAi(Character character)
    {
        return character.IsTamed() && character.GetComponent<MonsterAI>() != null;
    }

    private static bool IsWithinHorizontalRange(Vector3 origin, Vector3 target, float rangeSqr)
    {
        float dx = target.x - origin.x;
        float dz = target.z - origin.z;
        return dx * dx + dz * dz <= rangeSqr;
    }

    private static BucketKey GetBucketKey(Vector3 position, float bucketSize)
    {
        return new BucketKey(
            GetBucketCoordinate(position.x, bucketSize),
            GetBucketCoordinate(position.z, bucketSize));
    }

    private static int GetBucketCoordinate(float value, float bucketSize)
    {
        return Mathf.FloorToInt(value / bucketSize);
    }

    private static bool IsValidCharacter(Character? character)
    {
        return character != null &&
               character.gameObject != null &&
               !character.IsDead();
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
        if (zdo.GetInt(GenerationKey, 0) != CurrentGeneration ||
            zdo.GetFloat(ActiveUntilKey, 0f) <= now)
        {
            return false;
        }

        multiplier = Mathf.Clamp(zdo.GetFloat(multiplierKey, 1f), 0f, 10f);
        return !Mathf.Approximately(multiplier, 1f);
    }

    private static void TrySendMessage(Rule rule, Character target, double now)
    {
        if (string.IsNullOrWhiteSpace(rule.Message) ||
            !SceneProximityQueries.TryFindNearestLivingPlayerInRangeXZ(target.GetCenterPoint(), Mathf.Max(rule.Range, 32f), out long playerId) ||
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
