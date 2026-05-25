using System;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private sealed class ValueCodec<TValue>
        where TValue : class
    {
        public ValueCodec(
            Action<PayloadSignatureBuilder, TValue, EntrySignatureContext> signatureWriter,
            Action<ZPackage, TValue> payloadWriter,
            Func<ZPackage, TValue> payloadReader,
            Func<TValue, TValue> cloneValue)
        {
            SignatureWriter = signatureWriter ?? throw new ArgumentNullException(nameof(signatureWriter));
            PayloadWriter = payloadWriter ?? throw new ArgumentNullException(nameof(payloadWriter));
            PayloadReader = payloadReader ?? throw new ArgumentNullException(nameof(payloadReader));
            CloneValue = cloneValue ?? throw new ArgumentNullException(nameof(cloneValue));
        }

        public Action<PayloadSignatureBuilder, TValue, EntrySignatureContext> SignatureWriter { get; }
        public Action<ZPackage, TValue> PayloadWriter { get; }
        public Func<ZPackage, TValue> PayloadReader { get; }
        public Func<TValue, TValue> CloneValue { get; }
    }
}
