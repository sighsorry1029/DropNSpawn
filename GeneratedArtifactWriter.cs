using System.IO;

namespace DropNSpawn;

internal static class GeneratedArtifactWriter
{
    internal static bool WriteText(string path, string content, string? logMessage = null, bool logOnlyWhenChanged = false)
    {
        bool changed = GeneratedFileWriter.WriteAllTextIfChanged(path, content);
        if (!string.IsNullOrWhiteSpace(logMessage) && (!logOnlyWhenChanged || changed))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo(logMessage);
        }

        return changed;
    }

    internal static void WriteTextAlways(string path, string content, string? logMessage = null)
    {
        string? directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        File.WriteAllText(path, content ?? "");
        if (!string.IsNullOrWhiteSpace(logMessage))
        {
            DropNSpawnPlugin.DropNSpawnLogger.LogInfo(logMessage);
        }
    }
}
