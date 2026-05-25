using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;

namespace DropNSpawn;

internal static class ReferenceRefreshSupport
{
    internal const string CurrentReferenceLogicVersion = "2026-03-26-full-rewrite-v1";

    internal static string ComputeStableHashForKeys(IEnumerable<string?> keys)
    {
        StringBuilder builder = new();
        foreach (string key in (keys ?? Enumerable.Empty<string?>())
                     .Select(NormalizeKey)
                     .Where(key => key.Length > 0)
                     .OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(key);
        }

        return ComputeStableHash(builder.ToString());
    }

    internal static bool TryReadYamlList<T>(string path, IDeserializer deserializer, out List<T> entries, out string error)
    {
        entries = new List<T>();
        error = "";

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            string content = File.ReadAllText(path);
            return TryDeserializeYamlList(content, deserializer, out entries, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            entries = new List<T>();
            return false;
        }
    }

    internal static bool TryDeserializeYamlList<T>(string content, IDeserializer deserializer, out List<T> entries, out string error)
    {
        entries = new List<T>();
        error = "";

        try
        {
            using StringReader reader = new(content ?? "");
            entries = deserializer.Deserialize<List<T>>(reader) ?? new List<T>();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            entries = new List<T>();
            return false;
        }
    }

    internal static List<T> MergeMissingByKey<T>(IEnumerable<T> existingEntries, IEnumerable<T> currentEntries, Func<T, string?> getKey, out int addedCount)
    {
        List<T> mergedEntries = existingEntries?.ToList() ?? new List<T>();
        HashSet<string> existingKeys = ToNormalizedKeySet(mergedEntries.Select(getKey));
        addedCount = 0;

        foreach (T currentEntry in currentEntries ?? Enumerable.Empty<T>())
        {
            string key = NormalizeKey(getKey(currentEntry));
            if (key.Length == 0 || !existingKeys.Add(key))
            {
                continue;
            }

            mergedEntries.Add(currentEntry);
            addedCount++;
        }

        return mergedEntries;
    }

    internal static string SerializeReferenceSections<T>(IEnumerable<T> entries, Func<T, string> getPrefabName, ISerializer serializer)
    {
        List<PrefabOwnerSection<T>> sections = PrefabOutputSections.BuildSections(entries ?? Enumerable.Empty<T>(), getPrefabName);
        return PrefabOutputSections.SerializeReferenceSections(sections, serializer);
    }

    internal static HashSet<string> ToNormalizedKeySet(IEnumerable<string?> keys)
    {
        HashSet<string> normalizedKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? key in keys ?? Enumerable.Empty<string?>())
        {
            string normalizedKey = NormalizeKey(key);
            if (normalizedKey.Length > 0)
            {
                normalizedKeys.Add(normalizedKey);
            }
        }

        return normalizedKeys;
    }

    internal static string NormalizeKey(string? key)
    {
        return (key ?? "").Trim();
    }

    internal static bool ShouldSkipAutoUpdate(string stateKey, string referencePath, string sourceSignature, string? logicVersion = null)
    {
        if (string.IsNullOrWhiteSpace(stateKey) ||
            string.IsNullOrWhiteSpace(referencePath) ||
            string.IsNullOrWhiteSpace(sourceSignature) ||
            !File.Exists(referencePath))
        {
            return false;
        }

        string statePath = ResolveAutoUpdateStatePath(stateKey);
        if (!File.Exists(statePath))
        {
            return false;
        }

        try
        {
            string normalizedLogicVersion = (logicVersion ?? "").Trim();
            string[] lines = File.ReadAllLines(statePath);
            if (lines.Length < 2)
            {
                return false;
            }

            string storedSourceSignature = (lines[0] ?? "").Trim();
            string storedFileStamp = (lines[1] ?? "").Trim();
            string storedLogicVersion = lines.Length >= 4 ? (lines[3] ?? "").Trim() : "";
            if (normalizedLogicVersion.Length > 0 &&
                !string.Equals(storedLogicVersion, normalizedLogicVersion, StringComparison.Ordinal))
            {
                return false;
            }

            return string.Equals(storedSourceSignature, sourceSignature, StringComparison.Ordinal) &&
                   string.Equals(storedFileStamp, BuildReferenceFileStamp(referencePath), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static bool ShouldSkipAutoUpdateByPrecheck(string stateKey, string referencePath, string precheckSignature, string? logicVersion = null)
    {
        if (string.IsNullOrWhiteSpace(stateKey) ||
            string.IsNullOrWhiteSpace(referencePath) ||
            string.IsNullOrWhiteSpace(precheckSignature) ||
            !File.Exists(referencePath))
        {
            return false;
        }

        string statePath = ResolveAutoUpdateStatePath(stateKey);
        if (!File.Exists(statePath))
        {
            return false;
        }

        try
        {
            string normalizedLogicVersion = (logicVersion ?? "").Trim();
            string[] lines = File.ReadAllLines(statePath);
            if (lines.Length < 3)
            {
                return false;
            }

            string storedFileStamp = (lines[1] ?? "").Trim();
            string storedPrecheckSignature = (lines[2] ?? "").Trim();
            string storedLogicVersion = lines.Length >= 4 ? (lines[3] ?? "").Trim() : "";
            if (normalizedLogicVersion.Length > 0 &&
                !string.Equals(storedLogicVersion, normalizedLogicVersion, StringComparison.Ordinal))
            {
                return false;
            }

            return string.Equals(storedPrecheckSignature, precheckSignature, StringComparison.Ordinal) &&
                   string.Equals(storedFileStamp, BuildReferenceFileStamp(referencePath), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static void RecordAutoUpdateState(string stateKey, string referencePath, string sourceSignature, string? precheckSignature = null, string? logicVersion = null)
    {
        if (string.IsNullOrWhiteSpace(stateKey) ||
            string.IsNullOrWhiteSpace(referencePath) ||
            string.IsNullOrWhiteSpace(sourceSignature) ||
            !File.Exists(referencePath))
        {
            return;
        }

        string statePath = GetAutoUpdateStatePath(stateKey);
        string content = sourceSignature.Trim() + Environment.NewLine +
                         BuildReferenceFileStamp(referencePath) + Environment.NewLine +
                         (precheckSignature ?? "").Trim() + Environment.NewLine +
                         (logicVersion ?? "").Trim() + Environment.NewLine;
        GeneratedFileWriter.WriteAllTextIfChanged(statePath, content);

        string legacyStatePath = GetLegacyAutoUpdateStatePath(stateKey);
        if (!string.Equals(statePath, legacyStatePath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(legacyStatePath))
        {
            try
            {
                File.Delete(legacyStatePath);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    internal static string ComputeStableHash(string? value)
    {
        using SHA256 sha256 = SHA256.Create();
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
        byte[] hash = sha256.ComputeHash(bytes);
        StringBuilder builder = new(hash.Length * 2);
        foreach (byte part in hash)
        {
            builder.Append(part.ToString("x2"));
        }

        return builder.ToString();
    }

    private static string GetAutoUpdateStatePath(string stateKey)
    {
        string sanitizedKey = SanitizeFileName(stateKey);
        return Path.Combine(GetAutoUpdateStateDirectoryPath(), $".reference-state.{sanitizedKey}.txt");
    }

    private static string GetLegacyAutoUpdateStatePath(string stateKey)
    {
        string sanitizedKey = SanitizeFileName(stateKey);
        return Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, $".reference-state.{sanitizedKey}.txt");
    }

    private static string ResolveAutoUpdateStatePath(string stateKey)
    {
        string currentPath = GetAutoUpdateStatePath(stateKey);
        if (File.Exists(currentPath))
        {
            return currentPath;
        }

        string legacyPath = GetLegacyAutoUpdateStatePath(stateKey);
        if (!File.Exists(legacyPath))
        {
            return currentPath;
        }

        try
        {
            string? directoryPath = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            File.Move(legacyPath, currentPath);
            return currentPath;
        }
        catch
        {
            return legacyPath;
        }
    }

    private static string GetAutoUpdateStateDirectoryPath()
    {
        return Path.Combine(DropNSpawnPlugin.YamlConfigDirectoryPath, "cache");
    }

    private static string BuildReferenceFileStamp(string referencePath)
    {
        FileInfo fileInfo = new(referencePath);
        return $"{fileInfo.Length}:{fileInfo.LastWriteTimeUtc.Ticks}";
    }

    private static string SanitizeFileName(string value)
    {
        StringBuilder builder = new(value.Length);
        HashSet<char> invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        foreach (char character in value)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }
}
