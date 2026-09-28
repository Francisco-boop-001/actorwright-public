using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Appends one output-owned Skyrim HDPT, preserving existing records and master order.</summary>
public static class BethesdaHeadPartWriter
{
    public static byte[] Compose(byte[]? input, string pluginName, RecordProposalArtifact proposal, byte[]? clonePlugin = null)
    {
        var part = proposal.HeadPart ?? throw new InvalidDataException("HDPT composition is absent.");
        if (part.Flags > 63) throw new InvalidDataException("HDPT flags contain undefined bits.");
        if (!FormId.TryParse(proposal.FormId, out var local) || local.Value is < 0x800 or > 0xFFFFFF)
            throw new InvalidDataException("HDPT allocation requires an unused local FormID from 0x800 through 0xFFFFFF.");
        byte[] source = input ?? EmptyPlugin(proposal.MasterDependencies);
        var census = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(source);
        var header = census.Records.Single(row => row.Signature == "TES4");
        var masters = Fields(header.Bytes).Where(row => row.Name == "MAST").Select(row => Text(row.Data)).ToArray();
        if (masters.Length > 253 || masters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != masters.Length || (!proposal.MasterDependencies.IsEmpty && !masters.SequenceEqual(proposal.MasterDependencies, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("HDPT composition preserves the existing ordered masters; proposal masters must match.");
        uint rawId = checked((uint)masters.Length << 24) | local.Value;
        if (census.NonTes4Records.Any(row => row.RawFormId == rawId)) throw new InvalidDataException("HDPT FormID is already allocated.");
        if ((header.Flags & 0x80) != 0) throw new InvalidDataException("Localized plugins require string-table authoring and are not supported by this bounded writer.");
        if ((header.Flags & 0x200) != 0 && local.Value > 0xFFF) throw new InvalidDataException("ESL HDPT allocation exceeds 0xFFF.");
        byte[] record;
        if (part.CloneFrom is { } cloneText)
        {
            if (!FormReference.TryParse(cloneText, out var clone) || clone.FormId.Value is 0 or > 0xFFFFFF)
                throw new InvalidDataException("Clone FormRef requires a nonzero 24-bit local FormID.");
            byte[] provider = clonePlugin ?? source;
            var providerCensus = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(provider);
            var providerMasters = Fields(providerCensus.Records.Single(row => row.Signature == "TES4").Bytes)
                .Where(row => row.Name == "MAST").Select(row => Text(row.Data)).ToArray();
            var original = providerCensus.NonTes4Records.Single(row => row.Signature == "HDPT" &&
                row.RawFormId == (((uint)providerMasters.Length << 24) | clone.FormId.Value));
            if ((original.Flags & 0x40000) != 0) throw new InvalidDataException("Compressed clone HDPT is unsupported.");
            using var payload = new MemoryStream();
            foreach (var field in Fields(original.Bytes))
            {
                if (field.Name == "EDID") { Field(payload, "EDID", Z(proposal.EditorId)); continue; }
                if (field.Name == "RNAM" && part.ValidRaces is not null) continue;
                byte[] data = field.Data;
                if (field.Name is "HNAM" or "RNAM" or "TNAM" or "CNAM")
                {
                    if (data.Length != 4) throw new InvalidDataException("Malformed clone FormID field.");
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    if (value != 0)
                    {
                        int index = checked((int)(value >> 24));
                        string owner = index == providerMasters.Length ? clone.Plugin.Value : index < providerMasters.Length ? providerMasters[index] : throw new InvalidDataException("Clone master index is invalid.");
                        data = U32(Resolve(owner + "|0x" + (value & 0xFFFFFF).ToString("X")));
                    }
                }
                Field(payload, field.Name, data);
            }
            if (part.ValidRaces is not null) Field(payload, "RNAM", U32(Resolve(part.ValidRaces, "FLST")));
            record = Record("HDPT", rawId, payload.ToArray(), original.Bytes.AsSpan(0, 24).ToArray());
        }
        else
        {
            using var payload = new MemoryStream();
            Field(payload, "EDID", Z(proposal.EditorId));
            if (proposal.Name is not null) Field(payload, "FULL", Z(proposal.Name));
            Field(payload, "MODL", Z(RequiredAsset(part.Model, ".nif")));
            Field(payload, "MODT", []);
            Field(payload, "DATA", [part.Flags]);
            if ((int)part.PartType is < 0 or > 6) throw new InvalidDataException("Skyrim HDPT type must be misc through eyebrows (0..6).");
            Field(payload, "PNAM", U32((uint)part.PartType));
            foreach (string extra in part.ExtraParts) Field(payload, "HNAM", U32(Resolve(extra, "HDPT")));
            Morph(0, part.TriRace); Morph(1, part.TriDialogue); Morph(2, part.TriChargen);
            Field(payload, "TNAM", U32(0));
            if (part.ValidRaces is not null) Field(payload, "RNAM", U32(Resolve(part.ValidRaces, "FLST")));
            record = Record("HDPT", rawId, payload.ToArray());
            void Morph(uint role, string? path) { Field(payload, "NAM0", U32(role)); Field(payload, "NAM1", Z(RequiredAsset(path, ".tri"))); }
        }
        byte[] updated = source.ToArray();
        int cursor = header.Offset + 24;
        bool hedr = false;
        foreach (var field in Fields(header.Bytes))
        {
            if (field.Name == "HEDR")
            {
                if (field.Data.Length != 12 || hedr) throw new InvalidDataException("Invalid TES4 HEDR.");
                hedr = true;
                BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(cursor + 10), checked(BinaryPrimitives.ReadUInt32LittleEndian(field.Data.AsSpan(4)) + 1));
                BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(cursor + 14), Math.Max(BinaryPrimitives.ReadUInt32LittleEndian(field.Data.AsSpan(8)), checked(local.Value + 1)));
            }
            cursor += 6 + field.Data.Length;
        }
        if (!hedr) throw new InvalidDataException("Missing TES4 HEDR.");
        var existingGroups = census.Groups.Where(row => row.Depth == 1 && row.Type == 0 && row.Label == "HDPT").ToArray();
        if (existingGroups.Length > 1)
            throw new InvalidDataException("HDPT composition requires at most one top-level HDPT group.");
        if (existingGroups.Length == 1)
        {
            int groupOffset = existingGroups[0].Offset;
            int groupLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(groupOffset + 4)));
            int groupEnd = checked(groupOffset + groupLength);
            byte[] extended = [.. updated.AsSpan(0, groupEnd), .. record, .. updated.AsSpan(groupEnd)];
            BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(groupOffset + 4),
                checked((uint)(groupLength + record.Length)));
            return extended;
        }
        byte[] group = new byte[24]; "GRUP"u8.CopyTo(group); "HDPT"u8.CopyTo(group.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(group.AsSpan(4), checked((uint)(24 + record.Length)));
        return [.. updated, .. group, .. record];

        uint Resolve(string text, string? signature = null)
        {
            if (!FormReference.TryParse(text, out var reference) || reference.FormId.Value > 0xFFFFFF || reference.FormId.Value == 0)
                throw new InvalidDataException("HDPT links require nonzero local Plugin|FormID references.");
            int index = string.Equals(reference.Plugin.Value, pluginName, StringComparison.OrdinalIgnoreCase) ? masters.Length : Array.FindIndex(masters, name => string.Equals(name, reference.Plugin.Value, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidDataException("HDPT reference is absent from the unchanged master order: " + text);
            if (signature is not null && index == masters.Length && !census.NonTes4Records.Any(row => row.Signature == signature && row.RawFormId == (((uint)index << 24) | reference.FormId.Value)))
                throw new InvalidDataException("Output-owned HDPT reference must resolve to an existing " + signature + ": " + text);
            return ((uint)index << 24) | reference.FormId.Value;
        }
    }

    public static void Verify(string path, string editorId, uint localId)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var part = mod.HeadParts.Single(row => row.FormKey.ModKey == mod.ModKey && row.FormKey.ID == localId);
        if (part.EditorID != editorId || part.Model?.File is null) throw new InvalidDataException("Independent HDPT reopen did not match the requested record.");
    }

    private static string RequiredAsset(string? text, string extension)
    {
        var path = new AssetPath(text ?? throw new InvalidDataException("HDPT model and all three morph paths are required."));
        if (path.Value.Any(char.IsControl)) throw new InvalidDataException("HDPT asset paths cannot contain control characters.");
        if (!path.Value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("HDPT asset extension is invalid.");
        string normalized = path.Value.Replace('/', '\\').TrimStart('\\');
        if (normalized.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[7..];
        return normalized;
    }
    private static byte[] EmptyPlugin(ImmutableArray<string> masters)
    {
        using var payload = new MemoryStream();
        Field(payload, "HEDR", [.. BitConverter.GetBytes(1.7F), .. U32(0), .. U32(0x800)]);
        foreach (string master in masters) { _ = new PluginName(master); Field(payload, "MAST", Z(master)); Field(payload, "DATA", new byte[8]); }
        return Record("TES4", 0, payload.ToArray());
    }
    private static IEnumerable<(string Name, byte[] Data)> Fields(byte[] record)
    {
        for (int i = 24; i < record.Length;)
        {
            if (i + 6 > record.Length) throw new InvalidDataException("Truncated subrecord.");
            string name = Encoding.ASCII.GetString(record, i, 4);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(i + 4));
            if (name == "XXXX" || i + 6 + length > record.Length) throw new InvalidDataException("Unsupported or truncated HDPT/TES4 subrecord.");
            yield return (name, record.AsSpan(i + 6, length).ToArray()); i += 6 + length;
        }
    }
    private static byte[] Record(string signature, uint id, byte[] payload, byte[]? header = null)
    {
        header ??= new byte[24]; Encoding.ASCII.GetBytes(signature).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), id);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 44);
        return [.. header, .. payload];
    }
    private static void Field(Stream stream, string name, byte[] data)
    { stream.Write(Encoding.ASCII.GetBytes(name)); stream.Write(BitConverter.GetBytes(checked((ushort)data.Length))); stream.Write(data); }
    private static byte[] U32(uint value) => BitConverter.GetBytes(value);
    private static byte[] Z(string value) => Encoding.UTF8.GetBytes(value + '\0');
    private static string Text(byte[] value) => Encoding.UTF8.GetString(value).TrimEnd('\0');
}
