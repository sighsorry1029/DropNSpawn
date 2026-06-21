using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace DropNSpawn;

internal static partial class CharacterDropManager
{
    private static List<PrefabOwnerSection<CharacterDropPrefabEntry>> BuildConfigurationTemplate()
    {
        return PrefabOutputSections.BuildSections(
            CharacterDropRuntime.GetSnapshots().Select(BuildConfigurationEntry),
            entry => entry.Prefab);
    }

    private static string BuildReferenceConfigurationTemplate()
    {
        List<PrefabOwnerSection<CharacterDropReferenceEntry>> sections = BuildConfigurationTemplate()
            .Select(section => new PrefabOwnerSection<CharacterDropReferenceEntry>(
                section.OwnerName,
                section.Entries
                    .Select(entry => new CharacterDropReferenceEntry
                    {
                        Prefab = entry.Prefab,
                        CharacterDrop = entry.CharacterDrop
                    })
                    .ToList()))
            .ToList();

        return PrefabOutputSections.SerializeReferenceSections(sections, Serializer);
    }

    private static string SerializeReferenceEntries(IEnumerable<CharacterDropReferenceEntry> entries)
    {
        return ReferenceRefreshSupport.SerializeReferenceSections(entries, entry => entry.Prefab, Serializer);
    }

    internal static bool TryWriteFullScaffoldConfigurationFile(out string path, out string error)
    {
        string content;
        string logMessage;
        lock (Sync)
        {
            path = FullScaffoldConfigurationPath;
            error = "";

            if (!IsGameDataReady() && !CharacterDropRuntime.HasSnapshots())
            {
                error = "Character game data is not ready yet.";
                return false;
            }

            CaptureSnapshotsIfNeeded();
            content = BuildFullScaffoldConfigurationTemplate();
            logMessage = $"Wrote character full scaffold configuration to {path}.";
        }

        GeneratedArtifactWriter.WriteTextAlways(path, content, logMessage);
        return true;
    }

    internal static void RefreshReferenceConfigurationFile()
    {
        string content;
        string sourceSignature;
        string logMessage;
        lock (Sync)
        {
            if (!IsGameDataReady())
            {
                return;
            }

            CaptureSnapshotsIfNeeded();
            content = BuildReferenceConfigurationTemplate();
            sourceSignature = ComputeReferenceSourceSignature();
            logMessage = $"Updated character reference configuration at {ReferenceConfigurationPath}.";
        }

        WriteReferenceConfigurationFile(content, logMessage);
        ReferenceArtifactLifecycle.RecordUpdate(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, sourceSignature);
    }

    private static void WriteReferenceConfigurationFile(string content, string logMessage)
    {
        GeneratedArtifactWriter.WriteText(ReferenceConfigurationPath, content, logMessage);
    }

    private static void EnsureReferenceArtifactsUpToDate()
    {
        if (!IsGameDataReady())
        {
            return;
        }

        string currentSourceSignature = ComputeReferenceSourceSignature();
        if (!ReferenceArtifactLifecycle.TryPlanUpdate(
                ReferenceAutoUpdateStateKey,
                ReferenceConfigurationPath,
                currentSourceSignature,
                out ReferenceArtifactUpdateKind updateKind))
        {
            return;
        }

        CaptureSnapshotsIfNeeded();
        WriteReferenceConfigurationFile(
            BuildReferenceConfigurationTemplate(),
            $"{ReferenceArtifactLifecycle.FormatAction(updateKind)} character reference configuration at {ReferenceConfigurationPath}.");
        ReferenceArtifactLifecycle.RecordUpdate(ReferenceAutoUpdateStateKey, ReferenceConfigurationPath, currentSourceSignature);
    }

    private static string ComputeReferenceSourceSignature()
    {
        return ReferenceRefreshSupport.ComputeStableHashForKeys(
            EnumerateRelevantPrefabs()
                .Select(prefab => prefab.name));
    }

    private static string FormatYamlBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatYamlFloat(float value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatYamlString(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        bool requiresQuotes =
            char.IsWhiteSpace(value[0]) ||
            char.IsWhiteSpace(value[value.Length - 1]) ||
            value.IndexOfAny(new[] { ':', '#', '{', '}', '[', ']', ',', '\'', '"', '&', '*', '!', '|', '>', '%', '@', '`' }) >= 0 ||
            value[0] == '-' ||
            value[0] == '?' ||
            string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

        return requiresQuotes ? $"'{value.Replace("'", "''")}'" : value;
    }

    private static CharacterDropPrefabEntry BuildConfigurationEntry(CharacterDropSnapshot snapshot)
    {
        List<CharacterDropEntryDefinition> drops = snapshot.Drops
            .Select(drop => new { Name = NormalizeReferenceItemName(drop.ItemPrefab), Drop = drop })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new CharacterDropEntryDefinition
            {
                Item = entry.Name!,
                Amount = RangeFormatting.FromReference(entry.Drop.AmountMin, entry.Drop.AmountMax, 1, 1),
                Chance = IsReferenceDefault(entry.Drop.Chance, 1f) ? null : entry.Drop.Chance,
                OnePerPlayer = entry.Drop.OnePerPlayer ? true : null,
                LevelMultiplier = GetReferenceLevelMultiplierOverride(entry.Drop),
                DontScale = entry.Drop.DontScale ? true : null
            })
            .ToList();

        return new CharacterDropPrefabEntry
        {
            Prefab = snapshot.Prefab.name,
            Enabled = true,
            CharacterDrop = new CharacterDropDefinition
            {
                Drops = drops.Count > 0 ? drops : null
            }
        };
    }

    private static bool IsReferenceDefault(float value, float defaultValue)
    {
        return Math.Abs(value - defaultValue) < 0.0001f;
    }

    private static IntRangeDefinition? GetAmountRange(CharacterDropEntryDefinition definition)
    {
        return definition.Amount ?? RangeFormatting.From(definition.AmountMin, definition.AmountMax ?? definition.AmountMin);
    }

    private static bool? GetReferenceLevelMultiplierOverride(CharacterDropItemSnapshot drop)
    {
        bool defaultValue = GetDefaultCharacterDropLevelMultiplier(drop.ItemPrefab);
        return drop.LevelMultiplier == defaultValue ? null : drop.LevelMultiplier;
    }

    private static string? NormalizeReferenceItemName(GameObject? itemPrefab)
    {
        if (itemPrefab == null)
        {
            return null;
        }

        string prefabName = itemPrefab.name;
        if (!prefabName.StartsWith(MockPrefabPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return prefabName;
        }

        string normalizedName = prefabName.Substring(MockPrefabPrefix.Length);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return null;
        }

        if (ObjectDB.instance?.GetItemPrefab(normalizedName) != null || ZNetScene.instance?.GetPrefab(normalizedName) != null)
        {
            return normalizedName;
        }

        return null;
    }
}
