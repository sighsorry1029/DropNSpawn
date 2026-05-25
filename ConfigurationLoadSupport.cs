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
}
