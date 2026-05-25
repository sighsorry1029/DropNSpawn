using System;
using System.Reflection;

namespace DropNSpawn;

internal static class SafeTypeLookup
{
    internal static Type? FindLoadedType(string fullTypeName, string? preferredAssemblySimpleName = null)
    {
        if (string.IsNullOrWhiteSpace(fullTypeName))
        {
            return null;
        }

        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (!string.IsNullOrWhiteSpace(preferredAssemblySimpleName))
        {
            foreach (Assembly assembly in assemblies)
            {
                if (!string.Equals(assembly.GetName().Name, preferredAssemblySimpleName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Type? type = TryGetType(assembly, fullTypeName);
                if (type != null)
                {
                    return type;
                }
            }
        }

        foreach (Assembly assembly in assemblies)
        {
            Type? type = TryGetType(assembly, fullTypeName);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static Type? TryGetType(Assembly assembly, string fullTypeName)
    {
        try
        {
            return assembly.GetType(fullTypeName, throwOnError: false, ignoreCase: false);
        }
        catch
        {
            return null;
        }
    }
}
