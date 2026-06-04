using System;
using System.Collections.Generic;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private sealed class DomainCodec<TEntry>
    {
        public DomainCodec(EntryTransportSchema<TEntry> schema)
        {
            Schema = schema ?? throw new ArgumentNullException(nameof(schema));
            SignatureBuilder = Schema.ComputePayloadSignature;
            EntrySignatureBuilder = entry => Schema.ComputeEntrySignature(entry, includeRuleId: false, includeResolvedBiomeMask: true);
            Serializer = Schema.SerializeEntries;
            Deserializer = Schema.DeserializeEntries;
            CloneEntries = Schema.CloneEntries;
        }

        public EntryTransportSchema<TEntry> Schema { get; }
        public Func<List<TEntry>, string> SignatureBuilder { get; }
        public Func<TEntry, string> EntrySignatureBuilder { get; }
        public Func<List<TEntry>, byte[]> Serializer { get; }
        public Func<byte[], List<TEntry>> Deserializer { get; }
        public Func<List<TEntry>?, List<TEntry>> CloneEntries { get; }
    }
}
