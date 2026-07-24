using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DropNSpawn;

internal static class ConfigurationLoadSupport
{
    internal sealed class LocalYamlDocument
    {
        internal string Path { get; set; } = "";
        internal string? Yaml { get; set; }
        internal string? ReadError { get; set; }
    }

    internal sealed class ParsedLocalConfiguration<TEntry>
    {
        internal ParsedLocalConfiguration(
            List<TEntry>? configuration = null,
            List<string>? warnings = null)
        {
            Configuration = configuration ?? new List<TEntry>();
            Warnings = warnings ?? new List<string>();
        }

        internal List<TEntry> Configuration { get; }
        internal List<string> Warnings { get; }
    }

    internal static List<LocalYamlDocument> ReadLocalYamlDocuments(IEnumerable<string> paths)
    {
        List<LocalYamlDocument> documents = new();
        foreach (string path in paths)
        {
            try
            {
                documents.Add(new LocalYamlDocument
                {
                    Path = path,
                    Yaml = File.ReadAllText(path)
                });
            }
            catch (Exception ex)
            {
                documents.Add(new LocalYamlDocument
                {
                    Path = path,
                    ReadError = $"{ex.GetType().Name}: {ex.Message}"
                });
            }
        }

        return documents;
    }

    internal static string BuildLocalPayload(IEnumerable<LocalYamlDocument> documents)
    {
        StringBuilder payload = new();
        foreach (LocalYamlDocument document in documents)
        {
            payload.Append(">>> ").Append(document.Path).AppendLine();
            if (document.ReadError != null)
            {
                payload.Append("!read-error ").AppendLine(document.ReadError);
            }
            else
            {
                payload.Append(document.Yaml);
            }

            payload.AppendLine();
            payload.AppendLine("<<<");
        }

        return payload.ToString();
    }

    internal static LocalLoadResult<TEntry> ParseLocalConfigurationDocuments<TEntry>(
        List<LocalYamlDocument> documents,
        Func<string, string, ParsedLocalConfiguration<TEntry>> parseDocument,
        Func<List<TEntry>, string, List<string>, List<TEntry>> prepareConfiguration,
        Func<Exception, string> formatExceptionLocation,
        string parseFailureHint)
    {
        List<TEntry> configuration = new();
        List<string> errors = new();
        List<string> warnings = new();
        int parsedEntryCount = 0;
        int loadedFileCount = 0;
        foreach (LocalYamlDocument document in documents)
        {
            if (document.ReadError != null)
            {
                errors.Add($"Failed to read {document.Path}. {document.ReadError}");
                continue;
            }

            try
            {
                ParsedLocalConfiguration<TEntry> parsedDocument =
                    parseDocument(document.Yaml ?? "", document.Path);
                warnings.AddRange(parsedDocument.Warnings);
                parsedEntryCount += parsedDocument.Configuration.Count;
                configuration.AddRange(prepareConfiguration(parsedDocument.Configuration, document.Path, warnings));
                loadedFileCount++;
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Failed to parse {document.Path}{formatExceptionLocation(ex)}. {parseFailureHint} {ex}");
            }
        }

        return new LocalLoadResult<TEntry>
        {
            Entries = configuration,
            Errors = errors,
            Warnings = warnings,
            ParsedEntryCount = parsedEntryCount,
            LoadedFileCount = loadedFileCount
        };
    }
}
