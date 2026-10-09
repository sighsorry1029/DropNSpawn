using System.Collections;
using System.Reflection;
using System.Text.Json;

internal static partial class Program
{
    private static void CheckSpawnSystemTimers(ModContract mod)
    {
        Type timers = mod.Type("SpawnSystemTimers");
        Type entryType = mod.Type("CanonicalSpawnSystemEntry");
        Type tableType = timers.GetNestedType("TimerTable", All)!;
        Type idType = timers.GetNestedType("TimerId", All)!;
        Type setType = typeof(HashSet<>).MakeGenericType(idType);
        object Entry(string phase, int chance = 40, float interval = 1200, string? prefab = "Gjall") =>
            JsonSerializer.Deserialize(JsonSerializer.Serialize(new
            {
                Prefab = prefab,
                SpawnSystem = new { TimeOfDay = new { Values = new[] { phase } }, SpawnInterval = interval, SpawnChance = chance, Biomes = new[] { "Mistlands" } }
            }), entryType, JsonOptions)!;
        Array Ids(params object[] entries) => (Array)Invoke(timers, null, "CreateIds", ListOf(entryType, entries))!;
        object Table(byte[]? bytes = null) => Invoke(tableType, null, "Deserialize", new object?[] { bytes })!;
        byte[] Bytes(object table) => (byte[])Call(table, "Serialize")!;
        IDictionary Values(object table) => (IDictionary)tableType.GetField("Values", All)!.GetValue(table)!;
        void Prune(object table, Array ids) => Call(table, "Prune", Activator.CreateInstance(setType, new object[] { ids })!);
        long Read(object table, object id, long now) => (long)Call(table, "Read", id, now)!;
        bool Dirty(object table) => (bool)tableType.GetField("Dirty", All)!.GetValue(table)!;
        void Reject(byte[] bytes, string reason)
        {
            try { Table(bytes); Check(false, reason); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { Check(true, reason); }
        }

        object day = Entry("day"), night = Entry("night", 60), another = Entry("night", prefab: "Wolf");
        string yaml = """
            - prefab: Gjall
              spawnSystem:
                huntPlayer: true
                groundOffset: 6
                spawnInterval: 1200
                spawnChance: 40
                noSpawnRadius: 32
                tilt: 0~90
                altitude: 0~1000
                biomes: [Mistlands]
                biomeAreas: [Median]
                timeOfDay: [day]
            - prefab: Gjall
              spawnSystem:
                huntPlayer: true
                level: 1~3
                groundOffset: 6
                spawnInterval: 1200
                spawnChance: 60
                noSpawnRadius: 32
                tilt: 0~90
                altitude: 0~1000
                biomes: [Mistlands]
                biomeAreas: [Median]
                timeOfDay: [night]
            """;
        Type manager = mod.Type("SpawnSystemManager");
        Array ParseIds(string text, string path)
        {
            object parsed = Invoke(manager, null, "ParseConfiguration", text, path)!;
            object entries = Property(parsed, "Configuration")!;
            Invoke(manager, null, "NormalizeConfiguration", entries);
            return (Array)Invoke(timers, null, "CreateIds", entries)!;
        }
        Array yamlIds = ParseIds(yaml, "spawnsystem.yml"), movedYamlIds = ParseIds("# another file\n" + yaml, "spawnsystem_mistlands.yml");
        Check(yamlIds.Length == 2 && !yamlIds.GetValue(0)!.Equals(yamlIds.GetValue(1)) && yamlIds.Cast<object>().SequenceEqual(movedYamlIds.Cast<object>()),
            "real unnamed Gjall YAML parses unchanged, distinguishes conditions and ignores file paths/comments");
        Array original = Ids(day, night);
        object dayId = original.GetValue(0)!, nightId = original.GetValue(1)!;
        Check(!dayId.Equals(nightId), "unnamed Gjall day/night definitions have independent timer IDs");
        Array reordered = Ids(another, night, day);
        Check(dayId.Equals(reordered.GetValue(2)) && nightId.Equals(reordered.GetValue(1)), "unrelated insertion and reorder preserve content timer IDs");
        SetProperty(day, "RuleId", "untrusted-wire-id");
        SetProperty(day, "SourcePath", "other-file.yml");
        SetProperty(day, "SourceLine", 88);
        SetProperty(day, "Enabled", false);
        Type biomeType = mod.LoadGameAssembly("assembly_valheim").GetType("Heightmap+Biome", true)!;
        SetProperty(Property(day, "SpawnSystem")!, "ResolvedBiomeMask", Enum.ToObject(biomeType, 512));
        Check(dayId.Equals(Ids(day).GetValue(0)), "path, wire metadata, enable flag and resolved biome IDs do not change timers");
        Check(!dayId.Equals(Ids(Entry("day", 41)).GetValue(0)) && !dayId.Equals(Ids(Entry("day", interval: 600)).GetValue(0)),
            "chance-only and interval-only edits create a new timer without changing YAML schema");
        Check(!dayId.Equals(Ids(Entry("day", prefab: "Wolf")).GetValue(0)), "prefab identity participates in timer IDs");

        Array duplicates = Ids(day, Entry("day"), another, Entry("day"));
        Check(duplicates.Cast<object>().Distinct().Count() == 4, "identical entries stay independent, including across files");
        Array movedDuplicates = Ids(another, Entry("day"), day, Entry("day"));
        Check(duplicates.GetValue(0)!.Equals(movedDuplicates.GetValue(1)) && duplicates.GetValue(1)!.Equals(movedDuplicates.GetValue(2)) &&
            duplicates.GetValue(3)!.Equals(movedDuplicates.GetValue(3)), "duplicate ordinals are local to content, not global positions");

        long now = TimeSpan.FromHours(20).Ticks;
        object table = Table();
        Check(Read(table, dayId, now) == now && Dirty(table), "missing timer initializes at current game time, not zero");
        Call(table, "Set", dayId, now + TimeSpan.TicksPerMinute);
        Check(Read(table, nightId, now) == now && Read(table, dayId, now + 1) == now + TimeSpan.TicksPerMinute,
            "one rule consuming an interval does not consume another rule's interval");
        byte[] saved = Bytes(table);
        Check(saved.Length == 8 + 2 * 44, "versioned binary store uses fixed full-hash/ordinal/time records");
        object restored = Table(saved);
        Check(!Dirty(restored) && Read(restored, dayId, now + 10) == now + TimeSpan.TicksPerMinute && !Dirty(restored),
            "fresh table instance restores elapsed time without rewriting unchanged data");
        Call(restored, "Set", dayId, now + TimeSpan.TicksPerMinute);
        Check(!Dirty(restored), "writing the same timestamp is a no-op");
        Prune(restored, Ids(day, night));
        Check(!Dirty(restored) && Values(restored).Count == 2, "disabled or temporarily ineligible configured rows are retained");
        Prune(restored, Ids(day));
        Check(Dirty(restored) && Values(restored).Count == 1 && Values(restored).Contains(dayId), "only absent configured timer identities are pruned");
        object replacement = Entry("day", 41);
        Prune(restored, Ids(replacement));
        Check(Values(restored).Count == 0 && Read(restored, Ids(replacement).GetValue(0)!, now + 300) == now + 300,
            "editing one rule discards its old record and begins its new interval");
        Prune(restored, Ids());
        Check(Values(Table(Bytes(restored))).Count == 0, "empty accepted configuration clears the new store without legacy-key scanning");

        object reverse = Table();
        Call(reverse, "Set", nightId, now);
        Call(reverse, "Set", dayId, now + TimeSpan.TicksPerMinute);
        Check(Bytes(reverse).SequenceEqual(saved), "storage order is deterministic regardless of insertion order");
        object many = Table();
        for (int i = 0; i < 250; i++)
        {
            Array active = Ids(Entry("day", interval: 100 + i));
            Prune(many, active);
            Read(many, active.GetValue(0)!, now + i);
        }
        Check(Values(many).Count == 1 && Bytes(many).Length == 52, "repeated setting edits do not accumulate retired IDs in the new store");
        byte[] wrongVersion = (byte[])saved.Clone(); wrongVersion[0] = 2;
        byte[] wrongCount = (byte[])saved.Clone(); BitConverter.GetBytes(int.MaxValue).CopyTo(wrongCount, 4);
        byte[] badTime = (byte[])saved.Clone(); BitConverter.GetBytes(-1L).CopyTo(badTime, 44);
        byte[] badOrdinal = (byte[])saved.Clone(); BitConverter.GetBytes(-1).CopyTo(badOrdinal, 40);
        byte[] repeated = (byte[])saved.Clone(); Array.Copy(repeated, 8, repeated, 52, 36);
        Reject(Array.Empty<byte>(), "empty corrupt data is not treated as a missing table");
        Reject(saved[..^1], "truncated table rejected");
        Reject(wrongVersion, "unknown format is not overwritten as an empty table");
        Reject(wrongCount, "hostile count is rejected before allocating records");
        Reject(badTime, "invalid game ticks rejected");
        Reject(badOrdinal, "negative duplicate ordinal rejected");
        Reject(repeated, "duplicate persisted identities rejected");
    }
}
