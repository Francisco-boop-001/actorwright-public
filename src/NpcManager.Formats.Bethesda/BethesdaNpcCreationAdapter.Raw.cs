using System.Buffers.Binary;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaNpcCreationAdapter
{
    private const int RawRecordHeaderSize = 24;
    private const int RawSubrecordHeaderSize = 6;
    private const uint RawCompressedRecordFlag = 0x0004_0000;

    private static void PatchExactQnam(
        WorkspacePath destination,
        NpcCreationProposal proposal,
        NpcCreationAppearanceSource appearance)
    {
        if (appearance is not FullyAuthoredSkyrimNpcAppearanceSource authored) return;
        var bytes = File.ReadAllBytes(destination.Value);
        if (bytes.Length < RawRecordHeaderSize || !bytes.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException("The authored output does not begin with a TES4 record.");
        var headerEnd = checked(RawRecordHeaderSize +
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        if (headerEnd > bytes.Length)
            throw new InvalidDataException("The authored output TES4 header exceeds the file.");

        var targetFormId = checked((uint)(proposal.Masters.Length << 24)) |
            proposal.AllocatedFormId.Value;
        var qnamOffset = -1;
        var targetCount = 0;
        FindTargetQnam(bytes, headerEnd, bytes.Length, targetFormId, ref targetCount, ref qnamOffset);
        if (targetCount != 1 || qnamOffset < 0)
        {
            throw new InvalidDataException(
                $"Expected one uncompressed output-owned NPC with one 12-byte QNAM; found {targetCount} target records.");
        }

        WriteSingle(bytes, qnamOffset, authored.Qnam.Red);
        WriteSingle(bytes, qnamOffset + sizeof(float), authored.Qnam.Green);
        WriteSingle(bytes, qnamOffset + (2 * sizeof(float)), authored.Qnam.Blue);
        File.WriteAllBytes(destination.Value, bytes);
    }

    private static void PatchPrivateSkinWnam(
        WorkspacePath destination,
        NpcCreationProposal proposal,
        NpcCreationAppearanceSource appearance)
    {
        if (appearance is not FullyAuthoredSkyrimNpcAppearanceSource
            {
                NakedSkinBinding: { } skinBinding
            })
            return;

        var bytes = File.ReadAllBytes(destination.Value);
        if (bytes.Length < RawRecordHeaderSize || !bytes.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException("The authored output does not begin with a TES4 record.");
        var headerEnd = checked(RawRecordHeaderSize +
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        if (headerEnd > bytes.Length)
            throw new InvalidDataException("The authored output TES4 header exceeds the file.");

        var targetFormId = checked((uint)(proposal.Masters.Length << 24)) |
            proposal.AllocatedFormId.Value;
        var skinFormId = checked((uint)(proposal.Masters.Length << 24)) |
            skinBinding.AllocatedArmorLocalFormId.Value;
        RawWnamPatch? patch = null;
        var targetCount = 0;
        var groupOffsets = new List<int>();
        FindTargetWnam(bytes, headerEnd, bytes.Length, targetFormId,
            groupOffsets, ref targetCount, ref patch);
        if (targetCount != 1 || patch is null)
        {
            throw new InvalidDataException(
                $"Expected one uncompressed output-owned NPC for private WNAM patching; found {targetCount} target records.");
        }

        if (patch.WnamDataOffset >= 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(patch.WnamDataOffset, sizeof(uint)), skinFormId);
            File.WriteAllBytes(destination.Value, bytes);
            return;
        }

        File.WriteAllBytes(destination.Value, InsertWnam(bytes, patch, skinFormId));
    }

    private static void FindTargetQnam(
        byte[] bytes,
        int start,
        int end,
        uint targetFormId,
        ref int targetCount,
        ref int qnamOffset)
    {
        var position = start;
        while (position < end)
        {
            if (position + RawRecordHeaderSize > end)
                throw new InvalidDataException("A TES4 record header is truncated while locating QNAM.");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            if (signature == "GRUP")
            {
                if (size < RawRecordHeaderSize)
                    throw new InvalidDataException("A TES4 group is smaller than its header.");
                var groupEnd = checked(position + (int)size);
                if (groupEnd > end) throw new InvalidDataException("A TES4 group exceeds its container.");
                FindTargetQnam(bytes, position + RawRecordHeaderSize, groupEnd,
                    targetFormId, ref targetCount, ref qnamOffset);
                position = groupEnd;
                continue;
            }

            var recordEnd = checked(position + RawRecordHeaderSize + (int)size);
            if (recordEnd > end) throw new InvalidDataException("A TES4 record exceeds its container.");
            if (signature == "NPC_" &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4)) == targetFormId)
            {
                targetCount++;
                if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 8, 4)) &
                     RawCompressedRecordFlag) != 0)
                    throw new InvalidDataException("Compressed authored NPC records are not supported.");
                qnamOffset = FindUniqueQnam(bytes, position + RawRecordHeaderSize, recordEnd);
            }
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("A TES4 container has trailing bytes.");
    }

    private static int FindUniqueQnam(byte[] bytes, int start, int end)
    {
        var position = start;
        var qnamOffset = -1;
        while (position < end)
        {
            if (position + 6 > end) throw new InvalidDataException("An NPC subrecord header is truncated.");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 4, 2));
            var dataOffset = position + 6;
            var fieldEnd = checked(dataOffset + size);
            if (fieldEnd > end) throw new InvalidDataException("An NPC subrecord exceeds its record.");
            if (signature == "XXXX")
                throw new InvalidDataException("Extended subrecords are not expected in the bounded NPC appearance payload.");
            if (signature == "QNAM")
            {
                if (size != 12 || qnamOffset >= 0)
                    throw new InvalidDataException("The authored NPC QNAM must occur once with exactly 12 bytes.");
                qnamOffset = dataOffset;
            }
            position = fieldEnd;
        }
        if (qnamOffset < 0) throw new InvalidDataException("The authored NPC is missing QNAM.");
        return qnamOffset;
    }

    private static void FindTargetWnam(
        byte[] bytes,
        int start,
        int end,
        uint targetFormId,
        List<int> groupOffsets,
        ref int targetCount,
        ref RawWnamPatch? patch)
    {
        var position = start;
        while (position < end)
        {
            if (position + RawRecordHeaderSize > end)
                throw new InvalidDataException("A TES4 record header is truncated while locating WNAM.");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            if (signature == "GRUP")
            {
                if (size < RawRecordHeaderSize)
                    throw new InvalidDataException("A TES4 group is smaller than its header.");
                var groupEnd = checked(position + (int)size);
                if (groupEnd > end) throw new InvalidDataException("A TES4 group exceeds its container.");
                groupOffsets.Add(position);
                FindTargetWnam(bytes, position + RawRecordHeaderSize, groupEnd,
                    targetFormId, groupOffsets, ref targetCount, ref patch);
                groupOffsets.RemoveAt(groupOffsets.Count - 1);
                position = groupEnd;
                continue;
            }

            var recordEnd = checked(position + RawRecordHeaderSize + (int)size);
            if (recordEnd > end) throw new InvalidDataException("A TES4 record exceeds its container.");
            if (signature == "NPC_" &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4)) == targetFormId)
            {
                targetCount++;
                if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 8, 4)) &
                     RawCompressedRecordFlag) != 0)
                    throw new InvalidDataException("Compressed authored NPC records are not supported.");
                patch = FindWnamPatch(bytes, position, recordEnd, groupOffsets);
            }
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("A TES4 container has trailing bytes.");
    }

    private static RawWnamPatch FindWnamPatch(
        byte[] bytes,
        int recordOffset,
        int recordEnd,
        List<int> groupOffsets)
    {
        var position = recordOffset + RawRecordHeaderSize;
        var wnamOffset = -1;
        var insertOffset = -1;
        while (position < recordEnd)
        {
            if (position + RawSubrecordHeaderSize > recordEnd)
                throw new InvalidDataException("An NPC subrecord header is truncated.");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 4, 2));
            var dataOffset = position + RawSubrecordHeaderSize;
            var fieldEnd = checked(dataOffset + size);
            if (fieldEnd > recordEnd) throw new InvalidDataException("An NPC subrecord exceeds its record.");
            if (signature == "XXXX")
                throw new InvalidDataException("Extended subrecords are not expected in the bounded NPC WNAM patch payload.");
            if (signature == "RNAM")
            {
                if (size != 4)
                    throw new InvalidDataException("The authored NPC RNAM race link is malformed.");
                insertOffset = fieldEnd;
            }
            if (signature == "WNAM")
            {
                if (size != 4 || wnamOffset >= 0)
                    throw new InvalidDataException("The authored NPC WNAM must occur at most once with exactly 4 bytes.");
                wnamOffset = dataOffset;
            }
            position = fieldEnd;
        }
        if (wnamOffset < 0 && insertOffset < 0)
            throw new InvalidDataException("The authored NPC is missing RNAM, so WNAM cannot be inserted deterministically.");
        return new RawWnamPatch(
            recordOffset,
            insertOffset,
            wnamOffset,
            groupOffsets.ToArray());
    }

    private static byte[] InsertWnam(
        byte[] bytes,
        RawWnamPatch patch,
        uint skinFormId)
    {
        const int wnamLength = RawSubrecordHeaderSize + sizeof(uint);
        var output = new byte[checked(bytes.Length + wnamLength)];
        Buffer.BlockCopy(bytes, 0, output, 0, patch.InsertOffset);
        "WNAM"u8.CopyTo(output.AsSpan(patch.InsertOffset, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(patch.InsertOffset + 4, sizeof(ushort)), sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(patch.InsertOffset + RawSubrecordHeaderSize, sizeof(uint)),
            skinFormId);
        Buffer.BlockCopy(bytes, patch.InsertOffset, output,
            patch.InsertOffset + wnamLength, bytes.Length - patch.InsertOffset);

        IncrementUInt32(output, patch.RecordOffset + 4, wnamLength);
        foreach (var groupOffset in patch.GroupOffsets)
            IncrementUInt32(output, groupOffset + 4, wnamLength);
        return output;
    }

    private static void IncrementUInt32(byte[] bytes, int offset, int increment)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(offset, sizeof(uint)),
            checked(value + (uint)increment));
    }

    private static void WriteSingle(byte[] bytes, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(offset, sizeof(float)), BitConverter.SingleToInt32Bits(value));

    private sealed record RawWnamPatch(
        int RecordOffset,
        int InsertOffset,
        int WnamDataOffset,
        int[] GroupOffsets);
}
