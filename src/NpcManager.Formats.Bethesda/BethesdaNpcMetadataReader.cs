using System.Buffers.Binary;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reads the small set of NPC relationship fields needed by the read-only browser.
/// Mutagen remains authoritative for typed record decoding; this scanner only supplies
/// cross-record provenance and classification edges that are not exposed by the summary contract.
/// Unsupported or compressed payloads degrade to explicit null metadata and diagnostics.
/// </summary>
internal static class BethesdaNpcMetadataReader
{
    private const uint CompressedRecordFlag = 0x00040000;
    private const uint LocalizedPluginFlag = 0x00000080;
    private const uint FemaleFlag = 0x00000001;
    private const uint CharGenFacePresetFlag = 0x00000004;

    public static NpcMetadataScanResult Read(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var state = new ScanState();
            ScanRange(bytes, 0, bytes.Length, state);
            foreach (var (signature, count) in state.CompressedRecordCounts
                         .OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                state.Diagnostics.Add(new Diagnostic("npc-metadata-compressed-record", DiagnosticSeverity.Warning,
                    $"NPC relationship metadata skipped {count} compressed {signature} record(s)."));
            }
            if (state.CompressedRecordCounts.TryGetValue(
                    "NPC_", out int compressedNpcCount))
            {
                state.Diagnostics.Add(new Diagnostic(
                    "npc-raw-name-fallback",
                    DiagnosticSeverity.Warning,
                    $"Raw FULL name inspection skipped {compressedNpcCount} compressed NPC record(s); typed-name fallback is required."));
            }
            if (state.LocalizedNpcNameFallbackCount > 0)
            {
                state.Diagnostics.Add(new Diagnostic(
                    "npc-raw-name-fallback",
                    DiagnosticSeverity.Warning,
                    $"Raw FULL name inspection skipped {state.LocalizedNpcNameFallbackCount} localized NPC record(s) whose four-byte values are string IDs; typed-name fallback is required."));
            }
            foreach (var templateFormId in state.Npcs.Values
                         .Where(npc => npc.TemplateFormId is not null)
                         .Select(npc => npc.TemplateFormId!.Value.Value))
            {
                state.TemplateSources.Add(templateFormId);
            }
            var metadata = state.Npcs.Values
                .Select(npc => new
                {
                    npc.FormId,
                    Metadata = new NpcRecordMetadata(
                        npc.Sex,
                        npc.RaceFormId,
                        npc.SkyrimWeight,
                        npc.Fallout4ThinWeight,
                        npc.Fallout4MuscularWeight,
                        npc.Fallout4FatWeight,
                        npc.TemplateFormId.HasValue,
                        npc.TemplateFormId,
                        state.TemplateSources.Contains(npc.FormId),
                        state.PlacedNpcs.Contains(npc.FormId),
                        state.LeveledNpcs.Contains(npc.FormId),
                        npc.IsCharGenFacePreset)
                })
                .ToImmutableDictionary(item => item.FormId, item => item.Metadata);
            return new NpcMetadataScanResult(metadata, state.Signatures.ToImmutableDictionary(), state.Diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            return new NpcMetadataScanResult(
                ImmutableDictionary<uint, NpcRecordMetadata>.Empty,
                ImmutableDictionary<uint, string>.Empty,
                [new Diagnostic("npc-metadata-unavailable", DiagnosticSeverity.Warning,
                    $"NPC relationship metadata was not available: {exception.Message}")]);
        }
    }

    private static void ScanRange(byte[] bytes, int start, int end, ScanState state)
    {
        var position = start;
        while (position < end)
        {
            if (end - position < 8)
                throw new InvalidDataException("Plugin contains a truncated record header.");

            var signature = ReadSignature(bytes, position);
            var size = ReadUInt32(bytes, position + 4);
            if (size > int.MaxValue)
                throw new InvalidDataException("Plugin record size exceeds the supported range.");

            var recordEnd = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
            if (recordEnd > end || recordEnd < position)
                throw new InvalidDataException($"Plugin record {signature} extends beyond its parent.");

            if (signature == "GRUP")
            {
                if (recordEnd - position < 24)
                    throw new InvalidDataException("Plugin group header is truncated.");
                ScanRange(bytes, position + 24, recordEnd, state);
            }
            else if (recordEnd - position >= 24)
            {
                if (signature == "TES4")
                {
                    state.IsLocalized =
                        (ReadUInt32(bytes, position + 8) &
                         LocalizedPluginFlag) != 0;
                    position = recordEnd;
                    continue;
                }
                var formId = ReadUInt32(bytes, position + 12);
                state.Signatures[formId] = signature;
                if (signature is not ("NPC_" or "ACHR" or "LVLN"))
                {
                    position = recordEnd;
                    continue;
                }

                var flags = ReadUInt32(bytes, position + 8);
                if ((flags & CompressedRecordFlag) != 0)
                {
                    state.CompressedRecordCounts[signature] =
                        state.CompressedRecordCounts.GetValueOrDefault(signature) + 1;
                }
                else
                {
                    IReadOnlyList<RawSubrecord> fields =
                        ReadSubrecords(bytes, position + 24, recordEnd);
                    switch (signature)
                    {
                        case "NPC_":
                            ParseNpc(bytes, position + 12, fields, state);
                            if (state.IsLocalized &&
                                fields.Any(field =>
                                    field.Signature == "FULL" &&
                                    field.Value.Length == 4))
                                state.LocalizedNpcNameFallbackCount++;
                            break;
                        case "ACHR": ParsePlaced(fields, state); break;
                        case "LVLN": ParseLeveled(fields, state); break;
                    }
                }
            }

            position = recordEnd;
        }
    }

    private static void ParseNpc(
        byte[] bytes,
        int formIdOffset,
        IReadOnlyList<RawSubrecord> fields,
        ScanState state)
    {
        var formId = ReadUInt32(bytes, formIdOffset);
        TryGetLast(fields, "ACBS", out byte[]? acbs);
        TryGetLast(fields, "RNAM", out byte[]? race);
        TryGetLast(fields, "TPLT", out byte[]? template);
        TryGetLast(fields, "NAM7", out byte[]? skyrimWeight);
        TryGetLast(fields, "MWGT", out byte[]? falloutWeight);
        var flags = acbs is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(acbs) : (uint?)null;
        state.Npcs[formId] = new RawNpc(
            formId,
            flags is null ? null : (flags.Value & FemaleFlag) != 0 ? NpcSex.Female : NpcSex.Male,
            ReadOptionalFormId(race),
            ReadFloat(skyrimWeight),
            ReadFloat(falloutWeight, 0),
            ReadFloat(falloutWeight, 4),
            ReadFloat(falloutWeight, 8),
            ReadOptionalFormId(template),
            flags is not null && (flags.Value & CharGenFacePresetFlag) != 0);
    }

    private static void ParsePlaced(
        IReadOnlyList<RawSubrecord> fields,
        ScanState state)
    {
        if (TryGetLast(fields, "NAME", out byte[]? value) &&
            value is not null)
            AddTarget(state.PlacedNpcs, value);
    }

    private static void ParseLeveled(
        IReadOnlyList<RawSubrecord> fields,
        ScanState state)
    {
        foreach (RawSubrecord field in fields)
        {
            if (field.Signature == "LVLO")
                AddTarget(state.LeveledNpcs, field.Value, 4);
        }
    }

    private static List<RawSubrecord> ReadSubrecords(
        byte[] bytes,
        int start,
        int end)
    {
        var fields = new List<RawSubrecord>();
        var position = start;
        uint? extendedSize = null;
        while (position < end)
        {
            if (end - position < 6)
                throw new InvalidDataException("Plugin subrecord header is truncated.");
            var signature = ReadSignature(bytes, position);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 4, 2));
            var actualSize = extendedSize ?? size;
            extendedSize = null;
            if (signature == "XXXX" && size == 4)
            {
                extendedSize = ReadUInt32(bytes, position + 6);
                position += 10;
                continue;
            }
            if (actualSize > int.MaxValue)
                throw new InvalidDataException("Plugin subrecord size exceeds the supported range.");
            var fieldEnd = checked(position + 6 + (int)actualSize);
            if (fieldEnd > end)
                throw new InvalidDataException($"Plugin subrecord {signature} extends beyond its record.");
            if (signature != "XXXX")
                fields.Add(new RawSubrecord(
                    signature,
                    bytes.AsSpan(position + 6, (int)actualSize).ToArray()));
            position = fieldEnd;
        }
        return fields;
    }

    private static bool TryGetLast(
        IReadOnlyList<RawSubrecord> fields,
        string signature,
        out byte[]? value)
    {
        for (int index = fields.Count - 1; index >= 0; index--)
        {
            if (fields[index].Signature == signature)
            {
                value = fields[index].Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static void AddTarget(
        HashSet<uint> targets,
        byte[] value,
        int offset = 0)
    {
        if (value.Length >= offset + 4)
            targets.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                value.AsSpan(offset, 4)));
    }

    private static FormId? ReadOptionalFormId(byte[]? value) =>
        value is { Length: >= 4 } && BinaryPrimitives.ReadUInt32LittleEndian(value) != 0
            ? new FormId(BinaryPrimitives.ReadUInt32LittleEndian(value))
            : null;

    private static float? ReadFloat(byte[]? value, int offset = 0) =>
        value is not null && value.Length >= offset + 4
            ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(offset, 4)))
            : null;

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
            throw new InvalidDataException("Plugin field is truncated.");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }

    private static string ReadSignature(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
            throw new InvalidDataException("Plugin signature is truncated.");
        return System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
    }

    private sealed class ScanState
    {
        public Dictionary<uint, RawNpc> Npcs { get; } = [];
        public Dictionary<uint, string> Signatures { get; } = [];
        public HashSet<uint> TemplateSources { get; } = [];
        public HashSet<uint> PlacedNpcs { get; } = [];
        public HashSet<uint> LeveledNpcs { get; } = [];
        public Dictionary<string, int> CompressedRecordCounts { get; } = new(StringComparer.Ordinal);
        public ImmutableArray<Diagnostic>.Builder Diagnostics { get; } = ImmutableArray.CreateBuilder<Diagnostic>();
        public bool IsLocalized { get; set; }
        public int LocalizedNpcNameFallbackCount { get; set; }

        public ScanState()
        {
            // Filled after all NPC records have been read by the caller's final projection.
        }
    }

    private sealed record RawSubrecord(
        string Signature,
        byte[] Value);

    private sealed record RawNpc(
        uint FormId,
        NpcSex? Sex,
        FormId? RaceFormId,
        float? SkyrimWeight,
        float? Fallout4ThinWeight,
        float? Fallout4MuscularWeight,
        float? Fallout4FatWeight,
        FormId? TemplateFormId,
        bool IsCharGenFacePreset);
}

internal sealed record NpcMetadataScanResult(
    ImmutableDictionary<uint, NpcRecordMetadata> Metadata,
    ImmutableDictionary<uint, string> Signatures,
    ImmutableArray<Diagnostic> Diagnostics);
