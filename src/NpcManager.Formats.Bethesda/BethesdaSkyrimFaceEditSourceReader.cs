using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Exact read-only Skyrim NPC appearance boundary for the desktop face editor.
/// Mutagen supplies typed FormKeys and VMAD script names; bounded raw parsing
/// retains the exact ACBS, NAM7, and QNAM scalar values.
/// </summary>
public sealed class BethesdaSkyrimFaceEditSourceReader : ISkyrimFaceEditSourceReader
{
    private const uint CompressedRecordFlag = 0x00040000;
    private const uint CharGenFacePresetFlag = 0x00000004;

    public SkyrimFaceEditSourceSnapshot Read(
        WorkspacePath pluginPath,
        FormId targetFormId)
    {
        var modKey = ModKey.FromNameAndExtension(Path.GetFileName(pluginPath.Value));
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(modKey, new FilePath(pluginPath.Value)),
            SkyrimRelease.SkyrimSE);
        var npc = overlay.Npcs.FirstOrDefault(item =>
            item.FormKey == new FormKey(modKey, targetFormId.Value)) ??
            throw new InvalidDataException(
                $"NPC {targetFormId} is not owned by {modKey.FileName}.");
        if (string.IsNullOrWhiteSpace(npc.EditorID))
            throw new InvalidDataException("The face-edit source NPC has no EditorID.");

        FormReference race = ToReference(npc.Race.FormKeyNullable) ??
            throw new InvalidDataException("The face-edit source NPC has no RACE reference.");
        ImmutableArray<FormReference> headParts = npc.HeadParts
            .Select(item => ToReference(item.FormKey) ??
                throw new InvalidDataException("The face-edit source NPC has a null PNAM reference."))
            .ToImmutableArray();
        FormReference hairColor = ToReference(npc.HairColor.FormKeyNullable) ??
            throw new InvalidDataException("The face-edit source NPC has no HCLF reference.");
        FormReference? headTexture = ToReference(npc.HeadTexture.FormKeyNullable);
        RawFaceFields raw = ReadRawFields(pluginPath.Value, targetFormId);
        SkyrimFaceMorphSnapshot morph = BethesdaSkyrimFaceMorphAdapter.Read(
            GameEdition.SkyrimSpecialEdition,
            pluginPath,
            targetFormId);
        SkyrimFaceTintSnapshot tint = BethesdaSkyrimFaceTintAdapter.Read(
            GameEdition.SkyrimSpecialEdition,
            pluginPath,
            targetFormId);

        return new SkyrimFaceEditSourceSnapshot(
            new EditorId(npc.EditorID),
            race,
            npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)
                ? NpcSex.Female
                : NpcSex.Male,
            headParts,
            hairColor,
            headTexture,
            raw.IsCharGenFacePreset,
            raw.Weight,
            morph,
            new SkyrimFaceTintPatch(tint.Layers),
            raw.Qnam,
            npc.VirtualMachineAdapter?.Scripts
                .Select(item => item.Name)
                .ToImmutableArray() ?? []);
    }

    private static RawFaceFields ReadRawFields(string path, FormId target)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int found = 0;
        RawFaceFields? result = null;
        ReadRange(bytes, 0, bytes.Length, target, ref found, ref result);
        if (found != 1 || result is null)
            throw new InvalidDataException(
                $"Expected exactly one uncompressed NPC record for {target}, found {found}.");
        return result;
    }

    private static void ReadRange(
        byte[] bytes,
        int start,
        int end,
        FormId target,
        ref int found,
        ref RawFaceFields? result)
    {
        int position = start;
        while (position < end)
        {
            (string signature, int recordEnd) = ReadContainer(bytes, position, end);
            if (signature == "GRUP")
            {
                ReadRange(bytes, position + 24, recordEnd, target, ref found, ref result);
            }
            else if (signature == "NPC_" && position + 16 <= recordEnd &&
                     MatchesPluginLocalFormId(
                         BinaryPrimitives.ReadUInt32LittleEndian(
                             bytes.AsSpan(position + 12, 4)),
                         target))
            {
                found++;
                result = ParseNpc(bytes, position, recordEnd);
            }
            position = recordEnd;
        }
        if (position != end)
            throw new InvalidDataException("TES4 container has trailing bytes.");
    }

    private static RawFaceFields ParseNpc(byte[] bytes, int start, int end)
    {
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(start + 8, 4));
        if ((flags & CompressedRecordFlag) != 0)
            throw new InvalidDataException(
                "Compressed NPC records are not admitted by the exact face-edit source reader.");

        uint? acbsFlags = null;
        float? weight = null;
        SkyrimQnamRgb? qnam = null;
        int cursor = start + 24;
        while (cursor + 6 <= end)
        {
            string signature = Text(bytes, cursor, 4);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(cursor + 4, 2));
            int fieldEnd = checked(cursor + 6 + size);
            if (fieldEnd > end)
                throw new InvalidDataException(
                    $"NPC subrecord {signature} exceeds its record.");
            ReadOnlySpan<byte> data = bytes.AsSpan(cursor + 6, size);
            switch (signature)
            {
                case "ACBS":
                    if (acbsFlags is not null || data.Length < 4)
                        throw new InvalidDataException("NPC ACBS is missing or duplicated.");
                    acbsFlags = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    break;
                case "NAM7":
                    if (weight is not null || data.Length != 4)
                        throw new InvalidDataException("Skyrim NAM7 must occur once with four bytes.");
                    weight = ReadSingle(data);
                    break;
                case "QNAM":
                    if (qnam is not null || data.Length != 12)
                        throw new InvalidDataException("Skyrim QNAM must occur once with twelve bytes.");
                    qnam = new SkyrimQnamRgb(
                        ReadSingle(data),
                        ReadSingle(data[4..]),
                        ReadSingle(data[8..]));
                    break;
            }
            cursor = fieldEnd;
        }
        if (cursor != end)
            throw new InvalidDataException("NPC payload has trailing bytes.");
        if (acbsFlags is null ||
            (weight is not null && !float.IsFinite(weight.Value)) ||
            (qnam is not null &&
             (!float.IsFinite(qnam.Value.Red) ||
              !float.IsFinite(qnam.Value.Green) ||
              !float.IsFinite(qnam.Value.Blue))))
        {
            throw new InvalidDataException(
                "The face-edit source NPC contains invalid ACBS, NAM7, or QNAM values.");
        }
        return new RawFaceFields(
            (acbsFlags.Value & CharGenFacePresetFlag) != 0,
            weight,
            qnam);
    }

    private static float ReadSingle(ReadOnlySpan<byte> data) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));

    private static (string Signature, int End) ReadContainer(
        byte[] bytes,
        int position,
        int limit)
    {
        if (position + 8 > limit)
            throw new InvalidDataException("TES4 record header is truncated.");
        string signature = Text(bytes, position, 4);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(position + 4, 4));
        int end = checked(position +
            (signature == "GRUP" ? (int)size : 24 + (int)size));
        if (end > limit || end < position + 8 ||
            (signature == "GRUP" && size < 24))
        {
            throw new InvalidDataException("TES4 record exceeds its container.");
        }
        return (signature, end);
    }

    private static bool MatchesPluginLocalFormId(uint raw, FormId requested) =>
        (raw & 0x00FF_FFFFu) == requested.Value;

    private static string Text(byte[] bytes, int offset, int length) =>
        Encoding.ASCII.GetString(bytes, offset, length);

    private static FormReference? ToReference(FormKey? key) =>
        key is { } value && !value.IsNull
            ? new FormReference(
                new PluginName(value.ModKey.FileName.String),
                new FormId(value.ID))
            : null;

    private sealed record RawFaceFields(
        bool IsCharGenFacePreset,
        float? Weight,
        SkyrimQnamRgb? Qnam);
}
