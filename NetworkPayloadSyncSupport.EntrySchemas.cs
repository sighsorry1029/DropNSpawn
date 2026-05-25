using System;
using System.Collections.Generic;
using System.IO;

namespace DropNSpawn;

internal static partial class NetworkPayloadSyncSupport
{
    private readonly struct EntrySignatureContext
    {
        public EntrySignatureContext(bool includeRuleId, bool includeResolvedBiomeMask)
        {
            IncludeRuleId = includeRuleId;
            IncludeResolvedBiomeMask = includeResolvedBiomeMask;
        }

        public bool IncludeRuleId { get; }
        public bool IncludeResolvedBiomeMask { get; }
    }

    private sealed class EntryFieldSpec<TEntry>
    {
        public EntryFieldSpec(
            Action<PayloadSignatureBuilder, TEntry, EntrySignatureContext>? signatureWriter,
            Action<ZPackage, TEntry>? payloadWriter,
            Action<ZPackage, TEntry>? payloadReader,
            Action<TEntry, TEntry>? copyValue)
        {
            SignatureWriter = signatureWriter;
            PayloadWriter = payloadWriter;
            PayloadReader = payloadReader;
            CopyValue = copyValue;
        }

        public Action<PayloadSignatureBuilder, TEntry, EntrySignatureContext>? SignatureWriter { get; }
        public Action<ZPackage, TEntry>? PayloadWriter { get; }
        public Action<ZPackage, TEntry>? PayloadReader { get; }
        public Action<TEntry, TEntry>? CopyValue { get; }
    }

    private sealed class EntryTransportSchema<TEntry>
    {
        private readonly EntryFieldSpec<TEntry>[] _fields;

        public EntryTransportSchema(int dtoVersion, Func<TEntry> createEntry, params EntryFieldSpec<TEntry>[] fields)
        {
            DtoVersion = dtoVersion;
            CreateEntry = createEntry ?? throw new ArgumentNullException(nameof(createEntry));
            _fields = fields ?? Array.Empty<EntryFieldSpec<TEntry>>();
        }

        public int DtoVersion { get; }
        public Func<TEntry> CreateEntry { get; }

        public string ComputePayloadSignature(IReadOnlyList<TEntry> entries)
        {
            PayloadSignatureBuilder builder = new();
            builder.WriteInt(DtoVersion);
            builder.WriteInt(entries.Count);

            EntrySignatureContext context = new(includeRuleId: true, includeResolvedBiomeMask: true);
            foreach (TEntry entry in entries)
            {
                WriteSignatureFields(builder, entry, context);
            }

            return builder.ComputeHash();
        }

        public string ComputePayloadSignature<TSource>(IReadOnlyList<TSource> entries, Func<TSource, TEntry> selector)
        {
            if (selector == null)
            {
                throw new ArgumentNullException(nameof(selector));
            }

            PayloadSignatureBuilder builder = new();
            builder.WriteInt(DtoVersion);
            builder.WriteInt(entries.Count);

            EntrySignatureContext context = new(includeRuleId: true, includeResolvedBiomeMask: true);
            foreach (TSource entry in entries)
            {
                WriteSignatureFields(builder, selector(entry), context);
            }

            return builder.ComputeHash();
        }

        public string ComputeEntrySignature(TEntry entry, bool includeRuleId, bool includeResolvedBiomeMask)
        {
            PayloadSignatureBuilder builder = new();
            WriteSignatureFields(builder, entry, new EntrySignatureContext(includeRuleId, includeResolvedBiomeMask));
            return builder.ComputeHash();
        }

        public byte[] SerializeEntries(List<TEntry> entries)
        {
            ZPackage package = new();
            package.Write(DtoVersion);
            package.Write(entries.Count);
            foreach (TEntry entry in entries)
            {
                WritePayloadFields(package, entry);
            }

            return package.GetArray();
        }

        public List<TEntry> DeserializeEntries(byte[] payloadBytes)
        {
            ZPackage package = new(payloadBytes);
            int version = package.ReadInt();
            if (version != DtoVersion)
            {
                throw new InvalidDataException($"Unsupported DTO version '{version}'.");
            }

            int count = package.ReadInt();
            List<TEntry> entries = new(count);
            for (int index = 0; index < count; index++)
            {
                TEntry entry = CreateEntry();
                ReadPayloadFields(package, entry);
                entries.Add(entry);
            }

            return entries;
        }

        public List<TEntry> CloneEntries(List<TEntry>? entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return new List<TEntry>();
            }

            List<TEntry> clonedEntries = new(entries.Count);
            foreach (TEntry entry in entries)
            {
                clonedEntries.Add(CloneEntry(entry));
            }

            return clonedEntries;
        }

        private TEntry CloneEntry(TEntry source)
        {
            TEntry clone = CreateEntry();
            foreach (EntryFieldSpec<TEntry> field in _fields)
            {
                field.CopyValue?.Invoke(source, clone);
            }

            return clone;
        }

        private void WriteSignatureFields(PayloadSignatureBuilder builder, TEntry entry, EntrySignatureContext context)
        {
            foreach (EntryFieldSpec<TEntry> field in _fields)
            {
                field.SignatureWriter?.Invoke(builder, entry, context);
            }
        }

        private void WritePayloadFields(ZPackage package, TEntry entry)
        {
            foreach (EntryFieldSpec<TEntry> field in _fields)
            {
                field.PayloadWriter?.Invoke(package, entry);
            }
        }

        private void ReadPayloadFields(ZPackage package, TEntry entry)
        {
            foreach (EntryFieldSpec<TEntry> field in _fields)
            {
                field.PayloadReader?.Invoke(package, entry);
            }
        }
    }

    private static EntryFieldSpec<TEntry> StringField<TEntry>(
        Func<TEntry, string?> getter,
        Action<TEntry, string> setter,
        Func<EntrySignatureContext, bool>? includeInSignature = null)
    {
        Func<EntrySignatureContext, bool> signatureFilter = includeInSignature ?? AlwaysIncludeInSignature;
        return new EntryFieldSpec<TEntry>(
            (builder, entry, context) =>
            {
                if (!signatureFilter(context))
                {
                    return;
                }

                builder.WriteString(getter(entry) ?? "");
            },
            (package, entry) => package.Write(getter(entry) ?? ""),
            (package, entry) => setter(entry, package.ReadString()),
            (source, target) => setter(target, getter(source) ?? ""));
    }

    private static EntryFieldSpec<TEntry> BoolField<TEntry>(
        Func<TEntry, bool> getter,
        Action<TEntry, bool> setter,
        Func<EntrySignatureContext, bool>? includeInSignature = null)
    {
        Func<EntrySignatureContext, bool> signatureFilter = includeInSignature ?? AlwaysIncludeInSignature;
        return new EntryFieldSpec<TEntry>(
            (builder, entry, context) =>
            {
                if (!signatureFilter(context))
                {
                    return;
                }

                builder.WriteBool(getter(entry));
            },
            (package, entry) => package.Write(getter(entry)),
            (package, entry) => setter(entry, package.ReadBool()),
            (source, target) => setter(target, getter(source)));
    }

    private static EntryFieldSpec<TEntry> NullableStringField<TEntry>(
        Func<TEntry, string?> getter,
        Action<TEntry, string?> setter,
        Func<EntrySignatureContext, bool>? includeInSignature = null)
    {
        Func<EntrySignatureContext, bool> signatureFilter = includeInSignature ?? AlwaysIncludeInSignature;
        return new EntryFieldSpec<TEntry>(
            (builder, entry, context) =>
            {
                if (!signatureFilter(context))
                {
                    return;
                }

                WriteNullableString(builder, getter(entry));
            },
            (package, entry) => WriteNullableString(package, getter(entry)),
            (package, entry) => setter(entry, ReadNullableString(package)),
            (source, target) => setter(target, getter(source)));
    }

    private static EntryFieldSpec<TEntry> OptionalField<TEntry, TValue>(
        Func<TEntry, TValue?> getter,
        Action<TEntry, TValue?> setter,
        Action<PayloadSignatureBuilder, TValue, EntrySignatureContext> signatureWriter,
        Action<ZPackage, TValue> payloadWriter,
        Func<ZPackage, TValue> payloadReader,
        Func<TValue?, TValue?> cloneValue,
        Func<EntrySignatureContext, bool>? includeInSignature = null)
        where TValue : class
    {
        Func<EntrySignatureContext, bool> signatureFilter = includeInSignature ?? AlwaysIncludeInSignature;
        return new EntryFieldSpec<TEntry>(
            (builder, entry, context) =>
            {
                if (!signatureFilter(context))
                {
                    return;
                }

                WriteOptional(
                    builder,
                    getter(entry),
                    (fieldBuilder, value) => signatureWriter(fieldBuilder, value, context));
            },
            (package, entry) => WriteOptional(package, getter(entry), payloadWriter),
            (package, entry) => setter(entry, ReadOptional(package, payloadReader)),
            (source, target) => setter(target, cloneValue(getter(source))));
    }

    private static EntryFieldSpec<TEntry> OptionalField<TEntry, TValue>(
        Func<TEntry, TValue?> getter,
        Action<TEntry, TValue?> setter,
        ValueCodec<TValue> codec,
        Func<EntrySignatureContext, bool>? includeInSignature = null)
        where TValue : class
    {
        Func<EntrySignatureContext, bool> signatureFilter = includeInSignature ?? AlwaysIncludeInSignature;
        return new EntryFieldSpec<TEntry>(
            (builder, entry, context) =>
            {
                if (!signatureFilter(context))
                {
                    return;
                }

                WriteOptional(
                    builder,
                    getter(entry),
                    (fieldBuilder, value) => codec.SignatureWriter(fieldBuilder, value, context));
            },
            (package, entry) => WriteOptional(package, getter(entry), codec.PayloadWriter),
            (package, entry) => setter(entry, ReadOptional(package, codec.PayloadReader)),
            (source, target) =>
            {
                TValue? value = getter(source);
                setter(target, value == null ? null : codec.CloneValue(value));
            });
    }

    private static EntryFieldSpec<TEntry> ListField<TEntry, TValue>(
        Func<TEntry, List<TValue>?> getter,
        Action<TEntry, List<TValue>?> setter,
        Action<PayloadSignatureBuilder, TValue, EntrySignatureContext> signatureWriter,
        Action<ZPackage, TValue> payloadWriter,
        Func<ZPackage, TValue> payloadReader,
        Func<TValue, TValue> cloneValue,
        Func<EntrySignatureContext, bool>? includeInSignature = null)
    {
        Func<EntrySignatureContext, bool> signatureFilter = includeInSignature ?? AlwaysIncludeInSignature;
        return new EntryFieldSpec<TEntry>(
            (builder, entry, context) =>
            {
                if (!signatureFilter(context))
                {
                    return;
                }

                WriteList(
                    builder,
                    getter(entry),
                    (fieldBuilder, value) => signatureWriter(fieldBuilder, value, context));
            },
            (package, entry) => WriteList(package, getter(entry), payloadWriter),
            (package, entry) => setter(entry, ReadList(package, payloadReader)),
            (source, target) => setter(target, CloneListValue(getter(source), cloneValue)));
    }

    private static EntryFieldSpec<TEntry> ListField<TEntry, TValue>(
        Func<TEntry, List<TValue>?> getter,
        Action<TEntry, List<TValue>?> setter,
        ValueCodec<TValue> codec,
        Func<EntrySignatureContext, bool>? includeInSignature = null)
        where TValue : class
    {
        Func<EntrySignatureContext, bool> signatureFilter = includeInSignature ?? AlwaysIncludeInSignature;
        return new EntryFieldSpec<TEntry>(
            (builder, entry, context) =>
            {
                if (!signatureFilter(context))
                {
                    return;
                }

                WriteList(
                    builder,
                    getter(entry),
                    (fieldBuilder, value) => codec.SignatureWriter(fieldBuilder, value, context));
            },
            (package, entry) => WriteList(package, getter(entry), codec.PayloadWriter),
            (package, entry) => setter(entry, ReadList(package, codec.PayloadReader)),
            (source, target) => setter(target, CloneListValue(getter(source), codec.CloneValue)));
    }

    private static EntryFieldSpec<TEntry> CopyOnlyField<TEntry>(Action<TEntry, TEntry> copyValue)
    {
        return new EntryFieldSpec<TEntry>(null, null, null, copyValue);
    }

    private static bool AlwaysIncludeInSignature(EntrySignatureContext context)
    {
        return true;
    }

    private static List<TValue>? CloneListValue<TValue>(List<TValue>? source, Func<TValue, TValue> cloneValue)
    {
        if (source == null)
        {
            return null;
        }

        List<TValue> clone = new(source.Count);
        foreach (TValue value in source)
        {
            clone.Add(cloneValue(value));
        }

        return clone;
    }
}
