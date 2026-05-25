using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DropNSpawn;

internal static class DomainConfigurationFileSupport
{
    internal static bool HasAnyOverrideConfigurationFile(
        string domain,
        string primaryYmlPath,
        string primaryYamlPath)
    {
        return File.Exists(primaryYmlPath) ||
               File.Exists(primaryYamlPath) ||
               EnumerateSupplementalOverrideConfigurationPaths(domain, fileName => IsOverrideConfigurationFileName(domain, fileName)).Any();
    }

    internal static IEnumerable<string> EnumerateOverrideConfigurationPaths(
        string domain,
        string primaryYmlPath,
        string primaryYamlPath,
        bool usePreferredPrimaryPath = false,
        Action<string>? warn = null)
    {
        if (!Directory.Exists(DropNSpawnPlugin.YamlConfigDirectoryPath))
        {
            yield break;
        }

        if (usePreferredPrimaryPath)
        {
            string? primaryPath = GetPreferredPrimaryOverridePath(
                primaryYmlPath,
                primaryYamlPath,
                warn ?? (_ => { }));
            if (primaryPath != null)
            {
                yield return primaryPath;
            }
        }
        else
        {
            if (File.Exists(primaryYmlPath))
            {
                yield return primaryYmlPath;
            }

            if (File.Exists(primaryYamlPath))
            {
                yield return primaryYamlPath;
            }
        }

        foreach (string path in EnumerateSupplementalOverrideConfigurationPaths(
                     domain,
                     fileName => IsOverrideConfigurationFileName(domain, fileName)))
        {
            yield return path;
        }
    }

    internal static IEnumerable<string> EnumerateSupplementalOverrideConfigurationPaths(
        string domain,
        Func<string, bool> isOverrideFileName)
    {
        return PluginSettingsFacade.EnumerateSupplementalOverrideConfigurationPaths(
            $"{PluginSettingsFacade.GetYamlDomainSupplementalPrefix(domain)}*.*",
            isOverrideFileName);
    }

    internal static string? GetPreferredPrimaryOverridePath(
        string primaryYmlPath,
        string primaryYamlPath,
        Action<string> warn)
    {
        bool hasYml = File.Exists(primaryYmlPath);
        bool hasYaml = File.Exists(primaryYamlPath);
        if (!hasYml && !hasYaml)
        {
            return null;
        }

        if (hasYml && hasYaml)
        {
            warn(
                $"Both '{Path.GetFileName(primaryYmlPath)}' and '{Path.GetFileName(primaryYamlPath)}' exist. Using '{Path.GetFileName(primaryYmlPath)}'.");
        }

        return hasYml ? primaryYmlPath : primaryYamlPath;
    }

    internal static bool IsOverrideConfigurationFileName(string domain, string fileName)
    {
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return fileName.Equals($"{PluginSettingsFacade.GetYamlDomainFilePrefix(domain)}.yml", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals($"{PluginSettingsFacade.GetYamlDomainFilePrefix(domain)}.yaml", StringComparison.OrdinalIgnoreCase) ||
               (fileName.StartsWith(PluginSettingsFacade.GetYamlDomainSupplementalPrefix(domain), StringComparison.OrdinalIgnoreCase) &&
                (fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)));
    }
}
