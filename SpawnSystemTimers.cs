using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DropNSpawn;

// Only DNS-owned normal SpawnSystem lists use this store. Event/AltBiome timers
// and all pre-existing ZDO long keys are deliberately left alone.
internal static class SpawnSystemTimers
{
    internal static readonly int StorageKey = "DropNSpawn.SpawnSystem.Timers".GetStableHashCode();
    private const int FormatVersion = 1;
    private const int RecordBytes = 44; // full SHA-256 (32), duplicate ordinal (4), game ticks (8)
    private static readonly ConditionalWeakTable<SpawnSystem, ZoneState> Zones = new();

    internal readonly struct TimerId : IEquatable<TimerId>, IComparable<TimerId>
    {
        private readonly Guid _first;
        private readonly Guid _second;
        private readonly int _occurrence;

        internal TimerId(string signature, int occurrence)
            : this(Guid.ParseExact(signature.Substring(0, 32), "N"), Guid.ParseExact(signature.Substring(32, 32), "N"), occurrence) { }

        private TimerId(Guid first, Guid second, int occurrence)
        {
            _first = first;
            _second = second;
            _occurrence = occurrence;
        }

        public bool Equals(TimerId other) => _first == other._first && _second == other._second && _occurrence == other._occurrence;
        public override bool Equals(object? obj) => obj is TimerId other && Equals(other);
        public override int GetHashCode() => unchecked((_first.GetHashCode() * 397 ^ _second.GetHashCode()) * 397 ^ _occurrence);
        public int CompareTo(TimerId other)
        {
            int comparison = _first.CompareTo(other._first);
            if (comparison == 0) comparison = _second.CompareTo(other._second);
            return comparison == 0 ? _occurrence.CompareTo(other._occurrence) : comparison;
        }

        internal void Write(BinaryWriter writer)
        {
            writer.Write(_first.ToByteArray());
            writer.Write(_second.ToByteArray());
            writer.Write(_occurrence);
        }

        internal static TimerId Read(BinaryReader reader)
        {
            TimerId id = new(new Guid(reader.ReadBytes(16)), new Guid(reader.ReadBytes(16)), reader.ReadInt32());
            if (id._occurrence < 0) throw new InvalidDataException("Negative SpawnSystem timer ordinal.");
            return id;
        }
    }

    internal static TimerId[] CreateIds(IReadOnlyList<CanonicalSpawnSystemEntry> entries)
    {
        Dictionary<string, int> duplicates = new(StringComparer.Ordinal);
        TimerId[] ids = new TimerId[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            // Enable/disable retains the timer; all spawn settings participate.
            // Source paths, wire RuleIds and runtime-resolved biome masks do not.
            CanonicalSpawnSystemEntry identity = new()
            {
                Prefab = entries[i].Prefab,
                Enabled = true,
                SpawnSystem = entries[i].SpawnSystem
            };
            string signature = NetworkPayloadSyncSupport.ComputeSpawnSystemEntryIdentitySignature(identity);
            duplicates.TryGetValue(signature, out int occurrence);
            ids[i] = new TimerId(signature, occurrence);
            duplicates[signature] = occurrence + 1;
        }
        return ids;
    }

    internal sealed class TimerTable
    {
        internal readonly Dictionary<TimerId, long> Values = new();
        internal bool Dirty;

        internal long Read(TimerId id, long now)
        {
            if (Values.TryGetValue(id, out long ticks)) return ticks;
            Set(id, now); // first use waits one interval; never imports legacy counters
            return now;
        }

        internal void Set(TimerId id, long ticks)
        {
            if (Values.TryGetValue(id, out long previous) && previous == ticks) return;
            Values[id] = ticks;
            Dirty = true;
        }

        internal void Prune(HashSet<TimerId> configuredIds)
        {
            foreach (TimerId id in Values.Keys.Where(id => !configuredIds.Contains(id)).ToArray())
            {
                Values.Remove(id);
                Dirty = true;
            }
        }

        internal byte[] Serialize()
        {
            using MemoryStream stream = new(8 + Values.Count * RecordBytes);
            using BinaryWriter writer = new(stream);
            writer.Write(FormatVersion);
            writer.Write(Values.Count);
            foreach (KeyValuePair<TimerId, long> record in Values.OrderBy(pair => pair.Key))
            {
                record.Key.Write(writer);
                writer.Write(record.Value);
            }
            return stream.ToArray();
        }

        internal static TimerTable Deserialize(byte[]? bytes)
        {
            TimerTable table = new();
            if (bytes == null) return table;
            if (bytes.Length < 8 || (bytes.Length - 8) % RecordBytes != 0)
                throw new InvalidDataException("Invalid SpawnSystem timer table length.");
            using BinaryReader reader = new(new MemoryStream(bytes, writable: false));
            int version = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (version != FormatVersion || count != (bytes.Length - 8) / RecordBytes)
                throw new InvalidDataException("Unsupported or invalid SpawnSystem timer table header.");
            for (int i = 0; i < count; i++)
            {
                TimerId id = TimerId.Read(reader);
                long ticks = reader.ReadInt64();
                if (ticks < 0 || ticks > DateTime.MaxValue.Ticks || table.Values.ContainsKey(id))
                    throw new InvalidDataException("Invalid or duplicate SpawnSystem timer record.");
                table.Values.Add(id, ticks);
            }
            return table;
        }
    }

    internal sealed class ZoneState
    {
        internal readonly HashSet<TimerId> ConfiguredIds;
        internal readonly Dictionary<SpawnSystem.SpawnData, TimerId> Rows;
        internal readonly HashSet<List<SpawnSystem.SpawnData>> Lists;
        internal ZDO? Zdo;
        internal ZDOID ZdoId;
        internal byte[]? SourceBytes;
        internal TimerTable? Table;
        internal ushort OwnerRevision;
        internal int Depth;
        internal bool NeedsPrune = true;
        private bool _loaded;

        internal ZoneState(HashSet<TimerId> configuredIds, Dictionary<SpawnSystem.SpawnData, TimerId> rows, IEnumerable<SpawnSystemList> lists)
        {
            ConfiguredIds = configuredIds;
            Rows = rows;
            Lists = new HashSet<List<SpawnSystem.SpawnData>>(lists.Select(list => list.m_spawners));
        }

        internal bool Refresh(ZDO zdo)
        {
            byte[]? bytes = zdo.GetByteArray(StorageKey);
            if (_loaded && ReferenceEquals(Zdo, zdo) && ZdoId == zdo.m_uid && ReferenceEquals(SourceBytes, bytes))
                return Table != null;
            Zdo = zdo;
            ZdoId = zdo.m_uid;
            SourceBytes = bytes;
            _loaded = true;
            NeedsPrune = true;
            Table = null;
            try { Table = TimerTable.Deserialize(bytes); }
            catch (InvalidDataException ex)
            {
                // Do not overwrite unreadable/newer data or fall back to legacy
                // timers. Retry when the stored byte array changes.
                DropNSpawnPlugin.DropNSpawnLogger.LogWarning($"SpawnSystem timers on {zdo.m_uid} were preserved; normal spawning is paused: {ex.Message}");
            }
            return Table != null;
        }

        internal void Invalidate()
        {
            _loaded = false;
            Table = null;
        }
    }

    internal static void Attach(SpawnSystem system, HashSet<TimerId>? configuredIds,
        Dictionary<SpawnSystem.SpawnData, TimerId> rows, IEnumerable<SpawnSystemList> lists)
    {
        Zones.Remove(system);
        if (configuredIds != null) Zones.Add(system, new ZoneState(configuredIds, rows, lists));
    }

    internal static void Detach(SpawnSystem system) => Zones.Remove(system);

    internal static bool Begin(SpawnSystem system, List<SpawnSystem.SpawnData> spawners, bool eventSpawners,
        string groupSalt, ZNetView view, out ZoneState? state)
    {
        state = null;
        if (eventSpawners || groupSalt != "b_" || !PluginSettingsFacade.IsSpawnSystemDomainEnabled() ||
            !Zones.TryGetValue(system, out ZoneState zone) || !zone.Lists.Contains(spawners)) return true;
        if (view == null || !view.IsValid() || !view.IsOwner()) return false;
        ZDO zdo = view.GetZDO();
        if (zdo == null || !zdo.IsOwner()) return false;
        if (zone.Depth == 0)
        {
            if (!zone.Refresh(zdo)) return false;
            zone.OwnerRevision = zdo.OwnerRevision;
            // This is the entire accepted configuration, not the rows currently
            // eligible by biome, time, keys, enable flag or prefab availability.
            if (zone.NeedsPrune)
            {
                zone.Table!.Prune(zone.ConfiguredIds);
                zone.NeedsPrune = false;
            }
        }
        zone.Depth++;
        state = zone;
        return true;
    }

    internal static void End(ZoneState? state)
    {
        if (state == null || --state.Depth != 0) return;
        ZDO? zdo = state.Zdo;
        if (zdo == null || !zdo.IsOwner() || zdo.m_uid != state.ZdoId || zdo.OwnerRevision != state.OwnerRevision ||
            !ReferenceEquals(zdo.GetByteArray(StorageKey), state.SourceBytes))
        {
            state.Invalidate(); // ownership/network replacement wins over a stale batch
            return;
        }
        if (state.Table?.Dirty != true) return;
        byte[] bytes = state.Table.Serialize();
        try
        {
            zdo.Set(StorageKey, bytes); // public API marks revision/network/save dirty
            state.SourceBytes = zdo.GetByteArray(StorageKey);
            state.Table.Dirty = false;
        }
        catch { state.Invalidate(); throw; }
    }

    private static bool TryGetActiveRow(SpawnSystem system, SpawnSystem.SpawnData row, ZDO zdo,
        bool eventSpawners, string groupSalt, out ZoneState state, out TimerId id)
    {
        id = default;
        state = null!;
        return !eventSpawners && groupSalt == "b_" && Zones.TryGetValue(system, out state) && state.Depth > 0 &&
            ReferenceEquals(state.Zdo, zdo) && state.Table != null && state.Rows.TryGetValue(row, out id);
    }

    internal static long ReadTime(ZDO zdo, int oldKey, long defaultValue, SpawnSystem system,
        SpawnSystem.SpawnData row, DateTime now, bool eventSpawners, string groupSalt)
    {
        return TryGetActiveRow(system, row, zdo, eventSpawners, groupSalt, out ZoneState state, out TimerId id)
            ? state.Table!.Read(id, now.Ticks) : zdo.GetLong(oldKey, defaultValue);
    }

    internal static void WriteTime(ZDO zdo, int oldKey, long ticks, SpawnSystem system,
        SpawnSystem.SpawnData row, bool eventSpawners, string groupSalt)
    {
        if (TryGetActiveRow(system, row, zdo, eventSpawners, groupSalt, out ZoneState state, out TimerId id))
            state.Table!.Set(id, ticks);
        else zdo.Set(oldKey, ticks);
    }

    internal static bool TryGetElapsed(SpawnSystem system, SpawnSystem.SpawnData row, ZDO zdo, DateTime now, out double elapsed)
    {
        elapsed = 0;
        if (!PluginSettingsFacade.IsSpawnSystemDomainEnabled() || !Zones.TryGetValue(system, out ZoneState state) ||
            !state.Rows.TryGetValue(row, out TimerId id)) return false;
        // ESP never initializes or prunes timers, including on non-owner clients.
        if (state.Depth == 0) state.Refresh(zdo);
        if (state.Table != null && state.Table.Values.TryGetValue(id, out long ticks))
            elapsed = (now.Ticks - ticks) / (double)TimeSpan.TicksPerSecond;
        return true;
    }
}
