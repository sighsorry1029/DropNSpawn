using System;
using System.IO;

namespace DropNSpawn;

internal static class GeneratedFileWriter
{
    internal static bool WriteAllTextIfChanged(string path, string content)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        content ??= "";

        string? directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        if (File.Exists(path))
        {
            string existingContent = File.ReadAllText(path);
            if (string.Equals(existingContent, content, StringComparison.Ordinal))
            {
                return false;
            }
        }

        File.WriteAllText(path, content);
        return true;
    }
}
