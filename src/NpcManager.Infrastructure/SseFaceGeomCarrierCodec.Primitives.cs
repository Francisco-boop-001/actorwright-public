using System.Buffers.Binary;

namespace NpcManager.Infrastructure;

internal static partial class SseFaceGeomCarrierCodec
{
    private static int CheckedCount(uint value, string role)
    {
        if (value > MaxCount) throw Invalid($"{role} {value} exceeds the {MaxCount} safety bound.");
        return checked((int)value);
    }

    private static byte ReadByte(byte[] data, ref int position, int end, string role)
    {
        EnsureAvailable(position, 1, end, role);
        return data[position++];
    }

    private static ushort ReadUInt16(byte[] data, ref int position, int end, string role)
    {
        EnsureAvailable(position, sizeof(ushort), end, role);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position, sizeof(ushort)));
        position += sizeof(ushort);
        return value;
    }

    private static uint ReadUInt32(byte[] data, ref int position, int end, string role)
    {
        EnsureAvailable(position, sizeof(uint), end, role);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, sizeof(uint)));
        position += sizeof(uint);
        return value;
    }

    private static int ReadInt32(byte[] data, ref int position, int end, string role)
    {
        EnsureAvailable(position, sizeof(int), end, role);
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position, sizeof(int)));
        position += sizeof(int);
        return value;
    }

    private static string ReadSizedString(
        byte[] data,
        ref int position,
        int end,
        int maximumBytes,
        string role)
    {
        var length = ReadUInt32(data, ref position, end, $"{role} length");
        if (length > maximumBytes) throw Invalid($"{role} length {length} exceeds the safety bound.");
        var byteLength = checked((int)length);
        EnsureAvailable(position, byteLength, end, role);
        var value = NifEncoding.GetString(data, position, byteLength);
        position += byteLength;
        return value;
    }

    private static void SkipByteSizedString(byte[] data, ref int position, int end, string role)
    {
        var length = ReadByte(data, ref position, end, $"{role} length");
        Skip(data, ref position, end, length, role);
    }

    private static void Skip(byte[] data, ref int position, int end, int count, string role)
    {
        _ = data;
        EnsureAvailable(position, count, end, role);
        position += count;
    }

    private static void WriteUInt32(byte[] data, ref int position, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(position, sizeof(uint)), value);
        position += sizeof(uint);
    }

    private static void EnsureAvailable(int position, int count, int end, string role)
    {
        if (count < 0 || position < 0 || position > end || count > end - position)
            throw Invalid($"{role} exceeds its bounded NIF region.");
    }

    private static void RequireBlockEnd(int position, int end, int index, string type)
    {
        if (position != end)
            throw Invalid($"Block {index} {type} consumed {position} instead of its exact end {end}.");
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private const string UnsupportedBlockIndexKey = "SseFaceGeomCarrierCodec.UnsupportedBlockIndex";
    private const string UnsupportedBlockTypeKey = "SseFaceGeomCarrierCodec.UnsupportedBlockType";

    private static InvalidDataException Unsupported(int index, string type, string message)
    {
        var exception = new InvalidDataException(message);
        exception.Data[UnsupportedBlockIndexKey] = index;
        exception.Data[UnsupportedBlockTypeKey] = type;
        return exception;
    }

    /// <summary>Tells an unsupported-block refusal from a malformed-file refusal.</summary>
    internal static bool TryGetUnsupportedBlock(InvalidDataException exception, out int index, out string type)
    {
        if (exception.Data[UnsupportedBlockIndexKey] is int blockIndex &&
            exception.Data[UnsupportedBlockTypeKey] is string blockType)
        {
            index = blockIndex;
            type = blockType;
            return true;
        }
        index = 0;
        type = string.Empty;
        return false;
    }
}
