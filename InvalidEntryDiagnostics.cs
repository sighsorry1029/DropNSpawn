using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal sealed class InvalidEntryDiagnostics
{
    private readonly HashSet<string> _warnings;
    private int _suppressionDepth;

    internal InvalidEntryDiagnostics()
    {
        _warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal void Clear()
    {
        _warnings.Clear();
    }

    internal bool Warn(string message, bool requireSourceOfTruth = false, ISet<string>? capturedWarnings = null)
    {
        if ((requireSourceOfTruth && !DropNSpawnPlugin.IsSourceOfTruth) ||
            _suppressionDepth > 0 ||
            ShouldSuppressServerSourcedWarning(message))
        {
            return false;
        }

        if (capturedWarnings != null)
        {
            capturedWarnings.Add(message);
            return true;
        }

        if (!_warnings.Add(message))
        {
            return false;
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogWarning(message);
        return true;
    }

    internal SuppressionScope BeginSuppressionForSyncedClientBuild(string sourceName)
    {
        return !DropNSpawnPlugin.IsSourceOfTruth && IsServerSyncSource(sourceName)
            ? new SuppressionScope(this)
            : default;
    }

    private static bool IsServerSyncSource(string sourceName)
    {
        return sourceName.StartsWith("ServerSync:", StringComparison.Ordinal);
    }

    private static bool ShouldSuppressServerSourcedWarning(string message)
    {
        return !DropNSpawnPlugin.IsSourceOfTruth &&
               message.IndexOf("ServerSync:", StringComparison.Ordinal) >= 0;
    }

    internal readonly struct SuppressionScope : IDisposable
    {
        private readonly InvalidEntryDiagnostics? _owner;

        public SuppressionScope(InvalidEntryDiagnostics owner)
        {
            _owner = owner;
            _owner._suppressionDepth++;
        }

        public void Dispose()
        {
            if (_owner != null && _owner._suppressionDepth > 0)
            {
                _owner._suppressionDepth--;
            }
        }
    }
}
