using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace DropNSpawn;

internal enum RuntimeWorkProfileKind
{
    NetworkPayload,
    SnapshotBuild,
    Reconcile
}

internal static class RuntimeWorkProfiler
{
    private const float LogIntervalSeconds = 1f;
    private static readonly Dictionary<string, RuntimeWorkProfileCounter> Counters = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, RuntimeHookProfileCounter> HookCounters = new(StringComparer.Ordinal);
    private static bool _wasEnabled;
    private static float _nextLogAt;

    internal static bool IsEnabled()
    {
        return PluginBoundSettings.DebugRuntimeWorkProfiling?.Value == DropNSpawnPlugin.Toggle.On;
    }

    internal static void Update(float now)
    {
        if (!TryEnsureEnabled(now) || now < _nextLogAt)
        {
            return;
        }

        LogAndReset(now);
    }

    internal static float BeginHookSample()
    {
        return IsEnabled() ? Time.realtimeSinceStartup : -1f;
    }

    internal static void EndHookSample(string hookKey, float startedAt)
    {
        if (startedAt < 0f)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (!TryEnsureEnabled(now))
        {
            return;
        }

        string key = hookKey ?? "";
        if (!HookCounters.TryGetValue(key, out RuntimeHookProfileCounter? counter))
        {
            counter = new RuntimeHookProfileCounter(key);
            HookCounters[key] = counter;
        }

        counter.Record(now - startedAt);
    }

    internal static void Record(
        string domainKey,
        RuntimeWorkProfileKind kind,
        bool processed,
        int pendingBefore,
        int pendingAfter,
        float elapsedSeconds)
    {
        if (!_wasEnabled)
        {
            return;
        }

        string key = BuildCounterKey(domainKey, kind);
        if (!Counters.TryGetValue(key, out RuntimeWorkProfileCounter? counter))
        {
            counter = new RuntimeWorkProfileCounter(domainKey, kind);
            Counters[key] = counter;
        }

        counter.Record(processed, pendingBefore, pendingAfter, elapsedSeconds);
    }

    private static bool TryEnsureEnabled(float now)
    {
        if (!IsEnabled())
        {
            if (_wasEnabled)
            {
                Counters.Clear();
                HookCounters.Clear();
                _wasEnabled = false;
                DropNSpawnPlugin.DropNSpawnLogger.LogInfo("Runtime work profiling disabled.");
            }

            return false;
        }

        if (_wasEnabled)
        {
            return true;
        }

        Counters.Clear();
        HookCounters.Clear();
        _wasEnabled = true;
        _nextLogAt = now + LogIntervalSeconds;
        DropNSpawnPlugin.DropNSpawnLogger.LogInfo("Runtime work profiling enabled.");
        return true;
    }

    private static void LogAndReset(float now)
    {
        LogQueueProfile();
        LogHookProfile();
        Counters.Clear();
        HookCounters.Clear();
        _nextLogAt = now + LogIntervalSeconds;
    }

    private static void LogQueueProfile()
    {
        StringBuilder builder = new("Runtime queue profile:");
        if (Counters.Count == 0)
        {
            builder.Append(" idle");
        }
        else
        {
            List<string> keys = new(Counters.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                RuntimeWorkProfileCounter counter = Counters[key];
                builder.Append(' ')
                    .Append(counter.DomainKey)
                    .Append('.')
                    .Append(FormatKind(counter.Kind))
                    .Append(" pending ")
                    .Append(counter.MaxPendingBefore.ToString(CultureInfo.InvariantCulture))
                    .Append("->")
                    .Append(counter.LastPendingAfter.ToString(CultureInfo.InvariantCulture))
                    .Append(" steps ")
                    .Append(counter.ProcessedSteps.ToString(CultureInfo.InvariantCulture));

                if (counter.IdleAttempts > 0)
                {
                    builder.Append(" idle ")
                        .Append(counter.IdleAttempts.ToString(CultureInfo.InvariantCulture));
                }

                builder.Append(" time ")
                    .Append(counter.ElapsedMilliseconds.ToString("0.###", CultureInfo.InvariantCulture))
                    .Append("ms");

                if (counter.ProcessedSteps > 0)
                {
                    builder.Append(" max ")
                        .Append(counter.MaxProcessedStepMilliseconds.ToString("0.###", CultureInfo.InvariantCulture))
                        .Append("ms");
                }

                builder.Append(';');
            }
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(builder.ToString());
    }

    private static void LogHookProfile()
    {
        if (HookCounters.Count == 0)
        {
            return;
        }

        StringBuilder builder = new("Runtime hook profile:");
        List<string> keys = new(HookCounters.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            RuntimeHookProfileCounter counter = HookCounters[key];
            builder.Append(' ')
                .Append(counter.HookKey)
                .Append(" count ")
                .Append(counter.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" time ")
                .Append(counter.ElapsedMilliseconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("ms max ")
                .Append(counter.MaxMilliseconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("ms;");
        }

        DropNSpawnPlugin.DropNSpawnLogger.LogInfo(builder.ToString());
    }

    private static string BuildCounterKey(string domainKey, RuntimeWorkProfileKind kind)
    {
        return string.Concat(domainKey ?? "", "|", kind.ToString());
    }

    private static string FormatKind(RuntimeWorkProfileKind kind)
    {
        return kind switch
        {
            RuntimeWorkProfileKind.NetworkPayload => "payload",
            RuntimeWorkProfileKind.SnapshotBuild => "snapshot",
            RuntimeWorkProfileKind.Reconcile => "reconcile",
            _ => kind.ToString()
        };
    }

    private sealed class RuntimeWorkProfileCounter
    {
        internal RuntimeWorkProfileCounter(string domainKey, RuntimeWorkProfileKind kind)
        {
            DomainKey = domainKey ?? "";
            Kind = kind;
        }

        internal string DomainKey { get; }
        internal RuntimeWorkProfileKind Kind { get; }
        internal int MaxPendingBefore { get; private set; }
        internal int LastPendingAfter { get; private set; }
        internal int ProcessedSteps { get; private set; }
        internal int IdleAttempts { get; private set; }
        internal double ElapsedMilliseconds { get; private set; }
        internal double MaxProcessedStepMilliseconds { get; private set; }

        internal void Record(bool processed, int pendingBefore, int pendingAfter, float elapsedSeconds)
        {
            double elapsedMilliseconds = Math.Max(0f, elapsedSeconds) * 1000d;
            MaxPendingBefore = Math.Max(MaxPendingBefore, Math.Max(0, pendingBefore));
            LastPendingAfter = Math.Max(0, pendingAfter);
            if (processed)
            {
                ProcessedSteps++;
                MaxProcessedStepMilliseconds = Math.Max(MaxProcessedStepMilliseconds, elapsedMilliseconds);
            }
            else
            {
                IdleAttempts++;
            }

            ElapsedMilliseconds += elapsedMilliseconds;
        }
    }

    private sealed class RuntimeHookProfileCounter
    {
        internal RuntimeHookProfileCounter(string hookKey)
        {
            HookKey = hookKey ?? "";
        }

        internal string HookKey { get; }
        internal int Count { get; private set; }
        internal double ElapsedMilliseconds { get; private set; }
        internal double MaxMilliseconds { get; private set; }

        internal void Record(float elapsedSeconds)
        {
            double elapsedMilliseconds = Math.Max(0f, elapsedSeconds) * 1000d;
            Count++;
            ElapsedMilliseconds += elapsedMilliseconds;
            MaxMilliseconds = Math.Max(MaxMilliseconds, elapsedMilliseconds);
        }
    }
}
