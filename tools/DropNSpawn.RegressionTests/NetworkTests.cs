using System.Collections;
using System.Reflection;

internal static partial class Program
{
    private static void CheckNetworkContracts(ModContract mod)
    {
        Type network = mod.Type("NetworkPayloadSyncSupport");
        Type transferType = network.GetNestedType("PendingInboundTransfer", All)!;

        // These are buffer/descriptor contracts, not routed RPC tests. The caller
        // ReceivePayloadChunkLocked rejects invalid indices, absent requests and
        // wrong senders before invoking these helpers; supply valid indices here.
        object transfer = Activator.CreateInstance(transferType, nonPublic: true)!;
        void Set(string name, object value) => transferType.GetProperty(name, All)!.SetValue(transfer, value);
        int ReceivedCount() => (int)transferType.GetProperty("ReceivedChunkCount", All)!.GetValue(transfer)!;
        byte[] buffer = new byte[10];
        Set("ChunkSizeBytes", 4);
        Set("ChunkCount", 3);
        Set("ExpectedCompressedSize", 10);
        Set("CompressedBuffer", buffer);
        Set("ReceivedChunks", new bool[3]);

        (bool Accepted, bool Duplicate, string Reason) BufferChunk(int index, byte[] bytes)
        {
            object?[] args = { transfer, index, bytes, false, "" };
            bool accepted = (bool)Invoke(network, null, "TryBufferInboundChunkBytes", args)!;
            return (accepted, (bool)args[3]!, (string)args[4]!);
        }

        var last = BufferChunk(2, new byte[] { 8, 9 });
        Check(last.Accepted && !last.Duplicate && ReceivedCount() == 1, "network: terminal chunk can arrive first");
        var middle = BufferChunk(1, new byte[] { 4, 5, 6, 7 });
        Check(middle.Accepted && !middle.Duplicate && ReceivedCount() == 2, "network: reverse-order middle chunk");
        var first = BufferChunk(0, new byte[] { 0, 1, 2, 3 });
        Check(first.Accepted && !first.Duplicate && ReceivedCount() == 3, "network: reverse-order transfer completes");
        byte[] expected = Enumerable.Range(0, 10).Select(value => (byte)value).ToArray();
        Check(buffer.SequenceEqual(expected), "network: reverse-order chunks preserve payload bytes");

        var duplicate = BufferChunk(1, new byte[] { 4, 5, 6, 7 });
        Check(duplicate.Accepted && duplicate.Duplicate && ReceivedCount() == 3, "network: identical duplicate does not increment progress");
        var terminalDuplicate = BufferChunk(2, new byte[] { 8, 9 });
        Check(terminalDuplicate.Accepted && terminalDuplicate.Duplicate && ReceivedCount() == 3, "network: identical short terminal duplicate");
        var conflicting = BufferChunk(1, new byte[] { 4, 5, 99, 7 });
        Check(!conflicting.Accepted && conflicting.Duplicate && conflicting.Reason.Length > 0, "network: conflicting duplicate rejected");
        Check(ReceivedCount() == 3 && buffer.SequenceEqual(expected), "network: rejected duplicate preserves buffered state");

        foreach ((int index, byte[] bytes, string reason) in new[]
                 {
                     (0, new byte[] { 0, 1, 2 }, "short nonterminal"),
                     (1, new byte[] { 4, 5, 6, 7, 8 }, "oversized chunk"),
                     (2, new byte[] { 8 }, "short terminal"),
                     (2, new byte[] { 8, 9, 10 }, "long terminal")
                 })
        {
            var rejected = BufferChunk(index, bytes);
            Check(!rejected.Accepted && rejected.Reason.Length > 0, $"network: {reason} rejected");
            Check(ReceivedCount() == 3 && buffer.SequenceEqual(expected), $"network: {reason} preserves buffered state");
        }

        void Capacity(int count, int index, int length, int size, bool accepted, string name)
        {
            object?[] args = { count, index, length, size, 4, 0, "" };
            bool actual = (bool)Invoke(network, null, "TryGetInboundCompressedBufferCapacity", args)!;
            Check(actual == accepted, "network descriptor: " + name);
            if (accepted)
                Check((int)args[5]! == size, "network descriptor: capacity equals declared compressed length");
            else
                Check(((string)args[6]!).Length > 0, "network descriptor: rejection explains " + name);
        }

        Capacity(3, 2, 2, 10, true, "terminal-first capacity");
        Capacity(3, 0, 4, 10, true, "first-chunk capacity");
        Capacity(2, 0, 4, 10, false, "chunk count mismatch");
        Capacity(3, 1, 3, 10, false, "short nonterminal");
        Capacity(3, 2, 3, 10, false, "terminal exceeds declared size");
        Capacity(3, 0, 5, 10, false, "chunk exceeds configured chunk size");
        Capacity(1, 0, 0, 0, false, "zero compressed length");
        Capacity(1, 0, 4, 64 * 1024 * 1024 + 1, false, "compressed length limit");

        CheckNetworkDeltaContracts(mod, network);
    }

    private static void CheckNetworkDeltaContracts(ModContract mod, Type network)
    {
        Type entryType = mod.Type("CharacterDropPrefabEntry");
        object schema = Invoke(network, null, "CreateCharacterEntrySchema")!;
        var transports = (IDictionary)network.GetField("TransportsByDomainKey", All)!.GetValue(null)!;
        object transport = transports["character"]!;

        IList Entries(params string[] keys)
        {
            var entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
            foreach (string key in keys)
            {
                object entry = Activator.CreateInstance(entryType)!;
                entryType.GetProperty("RuleId")!.SetValue(entry, key);
                entryType.GetProperty("Prefab")!.SetValue(entry, "Prefab_" + key);
                entries.Add(entry);
            }
            return entries;
        }

        object? Generic(string name, params object?[] args) => network.GetMethods(All)
            .Single(method => method.Name == name && method.IsGenericMethodDefinition && method.GetParameters().Length == args.Length)
            .MakeGenericMethod(entryType).Invoke(null, args);

        IList original = Entries("A", "B", "C", "D", "E", "F");
        IList desired = Entries("F", "B", "C", "D", "E", "G");
        entryType.GetProperty("Prefab")!.SetValue(desired[1], "Changed_B");
        byte[] originalBytes = (byte[])Call(schema, "SerializeEntries", original)!;
        byte[] desiredBytes = (byte[])Call(schema, "SerializeEntries", desired)!;
        string originalHash = Hash(originalBytes).ToLowerInvariant();
        string desiredHash = Hash(desiredBytes).ToLowerInvariant();
        object?[] originalIndexArgs = { transport, original, originalHash, null };
        object?[] desiredIndexArgs = { transport, desired, desiredHash, null };
        Check((bool)Generic("BuildPayloadIndex", originalIndexArgs)!, "network delta: base index");
        Check((bool)Generic("BuildPayloadIndex", desiredIndexArgs)!, "network delta: target index");
        object?[] deltaArgs = { transport, originalHash, desiredHash, originalIndexArgs[3], desiredIndexArgs[3], null };
        Check((bool)Generic("TryBuildDeltaPayloadBytes", deltaArgs)!, "network delta: construct add/remove/update/reorder delta");
        byte[] deltaBytes = (byte[])deltaArgs[5]!;
        object?[] applyArgs = { transport, originalHash, desiredHash, deltaBytes, originalIndexArgs[3], originalBytes, null };
        byte[] applied = (byte[])Generic("ApplyDeltaPayloadBytes", applyArgs)!;
        Check(applied.SequenceEqual(desiredBytes), "network delta: result equals full target payload");
        Check(((IList)applyArgs[6]!).Count == desired.Count, "network delta: entry count preserved");
        Check(originalBytes.SequenceEqual((byte[])Call(schema, "SerializeEntries", original)!), "network delta: base entries remain unchanged");

        // An in-memory base index avoids cache reads. Hash mismatches are rejected
        // before the merge; these tests do not exercise request epochs or RPC authority.
        ExpectFailure(() => Generic("ApplyDeltaPayloadBytes", transport, new string('0', 64), desiredHash,
            deltaBytes, originalIndexArgs[3], originalBytes, null), "network delta: mismatched base hash rejected");
        ExpectFailure(() => Generic("ApplyDeltaPayloadBytes", transport, originalHash, new string('0', 64),
            deltaBytes, originalIndexArgs[3], originalBytes, null), "network delta: mismatched target hash rejected");
    }
}
