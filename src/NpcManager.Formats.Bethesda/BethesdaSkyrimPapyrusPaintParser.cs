using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;

namespace NpcManager.Formats.Bethesda;

internal static class BethesdaSkyrimPapyrusPaintParser
{
    private const int MaximumVariadicArguments = 32;
    private const int MaximumTextureSlots = 8;

    private const int OpCallMethod = 0x17;
    private const int OpCallStatic = 0x19;

    private static readonly int[] FixedOperandCounts =
    [
        0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 3, 3, 3,
        3, 3, 1, 2, 2, 3, 2, 3, 1, 3, 3, 3, 2, 2, 3, 3, 4, 4
    ];

    private static readonly bool[] HasVariadicTail =
    [
        false, false, false, false, false, false, false, false, false,
        false, false, false, false, false, false, false, false, false,
        false, false, false, false, false, true, true, true, false,
        false, false, false, false, false, false, false, false, false
    ];

    private static readonly byte[][] PaintMarkers =
    [
        "AddWarpaint"u8.ToArray(),
        "AddBodyPaint"u8.ToArray(),
        "AddHandPaint"u8.ToArray(),
        "AddFeetPaint"u8.ToArray(),
        "AddFacePaint"u8.ToArray()
    ];

    public static SkyrimPexPaintParseResult Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        bool mentionsPaint = PaintMarkers.Any(marker => bytes.AsSpan().IndexOf(marker) >= 0);
        if (!mentionsPaint)
            return new SkyrimPexPaintParseResult(false, true, []);
        if (bytes.Length < 16 ||
            bytes[0] != 0xFA || bytes[1] != 0x57 ||
            bytes[2] != 0xC0 || bytes[3] != 0xDE)
        {
            return new SkyrimPexPaintParseResult(true, false, []);
        }

        try
        {
            var cursor = new Cursor(bytes);
            _ = cursor.ReadUInt32();
            _ = cursor.ReadByte();
            _ = cursor.ReadByte();
            _ = cursor.ReadUInt16();
            cursor.Skip(8);
            _ = cursor.ReadWideString();
            _ = cursor.ReadWideString();
            _ = cursor.ReadWideString();

            int stringCount = cursor.ReadUInt16();
            var strings = new string[stringCount];
            for (int index = 0; index < strings.Length; index++)
                strings[index] = cursor.ReadWideString();

            int hasDebugInfo = cursor.ReadByte();
            if (hasDebugInfo is not (0 or 1)) throw new InvalidDataException("Invalid PEX debug flag.");
            if (hasDebugInfo == 1)
            {
                cursor.Skip(8);
                int debugFunctionCount = cursor.ReadUInt16();
                for (int index = 0; index < debugFunctionCount; index++)
                {
                    _ = cursor.ReadStringIndex(strings);
                    _ = cursor.ReadStringIndex(strings);
                    _ = cursor.ReadStringIndex(strings);
                    _ = cursor.ReadByte();
                    int lineCount = cursor.ReadUInt16();
                    cursor.Skip(checked(lineCount * 2));
                }
            }

            int userFlagCount = cursor.ReadUInt16();
            for (int index = 0; index < userFlagCount; index++)
            {
                _ = cursor.ReadStringIndex(strings);
                _ = cursor.ReadByte();
            }

            var calls = new List<PexCall>();
            int objectCount = cursor.ReadUInt16();
            for (int objectIndex = 0; objectIndex < objectCount; objectIndex++)
            {
                _ = cursor.ReadStringIndex(strings);
                _ = cursor.ReadUInt32();
                _ = cursor.ReadStringIndex(strings);
                _ = cursor.ReadStringIndex(strings);
                _ = cursor.ReadUInt32();
                _ = cursor.ReadStringIndex(strings);

                int variableCount = cursor.ReadUInt16();
                for (int variableIndex = 0; variableIndex < variableCount; variableIndex++)
                {
                    _ = cursor.ReadStringIndex(strings);
                    _ = cursor.ReadStringIndex(strings);
                    _ = cursor.ReadUInt32();
                    _ = ReadOperand(cursor, strings);
                }

                int propertyCount = cursor.ReadUInt16();
                for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
                    ReadProperty(cursor, strings);

                int stateCount = cursor.ReadUInt16();
                for (int stateIndex = 0; stateIndex < stateCount; stateIndex++)
                {
                    _ = cursor.ReadStringIndex(strings);
                    int functionCount = cursor.ReadUInt16();
                    for (int functionIndex = 0; functionIndex < functionCount; functionIndex++)
                    {
                        _ = cursor.ReadStringIndex(strings);
                        ReadFunction(cursor, strings, calls);
                    }
                }
            }

            return new SkyrimPexPaintParseResult(
                true,
                true,
                ExtractRegistrations(calls));
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                           OverflowException or
                                           ArgumentException or
                                           DecoderFallbackException)
        {
            return new SkyrimPexPaintParseResult(true, false, []);
        }
    }

    private static void ReadProperty(Cursor cursor, string[] strings)
    {
        _ = cursor.ReadStringIndex(strings);
        _ = cursor.ReadStringIndex(strings);
        _ = cursor.ReadStringIndex(strings);
        _ = cursor.ReadUInt32();
        int flags = cursor.ReadByte();
        if ((flags & 0x04) != 0)
        {
            _ = cursor.ReadStringIndex(strings);
            return;
        }
        if ((flags & 0x01) != 0) ReadFunction(cursor, strings, null);
        if ((flags & 0x02) != 0) ReadFunction(cursor, strings, null);
    }

    private static void ReadFunction(
        Cursor cursor,
        string[] strings,
        List<PexCall>? calls)
    {
        _ = cursor.ReadStringIndex(strings);
        _ = cursor.ReadStringIndex(strings);
        _ = cursor.ReadUInt32();
        _ = cursor.ReadByte();

        int parameterCount = cursor.ReadUInt16();
        for (int index = 0; index < parameterCount; index++)
        {
            _ = cursor.ReadStringIndex(strings);
            _ = cursor.ReadStringIndex(strings);
        }

        int localCount = cursor.ReadUInt16();
        for (int index = 0; index < localCount; index++)
        {
            _ = cursor.ReadStringIndex(strings);
            _ = cursor.ReadStringIndex(strings);
        }

        int instructionCount = cursor.ReadUInt16();
        for (int instructionIndex = 0; instructionIndex < instructionCount; instructionIndex++)
        {
            int opcode = cursor.ReadByte();
            if ((uint)opcode >= (uint)FixedOperandCounts.Length)
                throw new InvalidDataException("Unsupported Skyrim PEX opcode.");

            int fixedCount = FixedOperandCounts[opcode];
            var fixedOperands = new PexOperand[fixedCount];
            for (int index = 0; index < fixedOperands.Length; index++)
                fixedOperands[index] = ReadOperand(cursor, strings);

            ImmutableArray<PexOperand> arguments = [];
            if (HasVariadicTail[opcode])
            {
                PexOperand count = ReadOperand(cursor, strings);
                if (count.Kind != SkyrimPexPaintOperandKind.Integer ||
                    count.IntegerValue < 0 ||
                    count.IntegerValue > MaximumVariadicArguments)
                {
                    throw new InvalidDataException("Invalid Skyrim PEX variadic argument count.");
                }
                var builder = ImmutableArray.CreateBuilder<PexOperand>(count.IntegerValue);
                for (int index = 0; index < count.IntegerValue; index++)
                    builder.Add(ReadOperand(cursor, strings));
                arguments = builder.MoveToImmutable();
            }

            string? method = opcode switch
            {
                OpCallMethod when fixedOperands.Length >= 1 => fixedOperands[0].StringValue,
                OpCallStatic when fixedOperands.Length >= 2 => fixedOperands[1].StringValue,
                _ => null
            };
            if (!string.IsNullOrEmpty(method)) calls?.Add(new PexCall(method, arguments));
        }
    }

    private static PexOperand ReadOperand(Cursor cursor, string[] strings)
    {
        int kind = cursor.ReadByte();
        return kind switch
        {
            0 => new PexOperand(SkyrimPexPaintOperandKind.Null, null, 0),
            1 => new PexOperand(SkyrimPexPaintOperandKind.Identifier,
                cursor.ReadStringIndex(strings), 0),
            2 => new PexOperand(SkyrimPexPaintOperandKind.StringLiteral,
                cursor.ReadStringIndex(strings), 0),
            3 => new PexOperand(SkyrimPexPaintOperandKind.Integer, null,
                cursor.ReadInt32()),
            4 => SkipOperand(cursor, 4, SkyrimPexPaintOperandKind.Float),
            5 => SkipOperand(cursor, 1, SkyrimPexPaintOperandKind.Boolean),
            _ => throw new InvalidDataException("Unsupported Skyrim PEX operand type.")
        };
    }

    private static PexOperand SkipOperand(
        Cursor cursor,
        int bytes,
        SkyrimPexPaintOperandKind kind)
    {
        cursor.Skip(bytes);
        return new PexOperand(kind, null, 0);
    }

    private static ImmutableArray<SkyrimPexPaintRegistration> ExtractRegistrations(
        IEnumerable<PexCall> calls)
    {
        var registrations = ImmutableArray.CreateBuilder<SkyrimPexPaintRegistration>();
        foreach (PexCall call in calls)
        {
            if (!TryCategory(call.Method, out SkyrimRaceMenuPaintCategory category, out bool extended) ||
                call.Arguments.Length < 2 ||
                !IsStringOperand(call.Arguments[0]) ||
                !IsStringOperand(call.Arguments[1]))
            {
                continue;
            }

            if (!extended)
            {
                registrations.Add(new SkyrimPexPaintRegistration(
                    category,
                    call.Arguments[0].StringValue!,
                    call.Arguments[1].StringValue!,
                    []));
                continue;
            }

            int slotCount = call.Arguments.Length - 1;
            if (slotCount > MaximumTextureSlots) continue;
            var slots = ImmutableArray.CreateBuilder<SkyrimPexPaintSlot>(slotCount);
            for (int index = 1; index < call.Arguments.Length; index++)
            {
                PexOperand operand = call.Arguments[index];
                slots.Add(new SkyrimPexPaintSlot(
                    index - 1,
                    operand.Kind,
                    operand.StringValue));
            }
            registrations.Add(new SkyrimPexPaintRegistration(
                category,
                call.Arguments[0].StringValue!,
                call.Arguments[1].StringValue!,
                slots.MoveToImmutable()));
        }
        return registrations.ToImmutable();
    }

    private static bool IsStringOperand(PexOperand operand) =>
        operand.Kind is SkyrimPexPaintOperandKind.Identifier or
            SkyrimPexPaintOperandKind.StringLiteral;

    private static bool TryCategory(
        string method,
        out SkyrimRaceMenuPaintCategory category,
        out bool extended)
    {
        extended = method.EndsWith("Ex", StringComparison.OrdinalIgnoreCase);
        string baseName = extended ? method[..^2] : method;
        switch (baseName.ToLowerInvariant())
        {
            case "addwarpaint":
                category = SkyrimRaceMenuPaintCategory.Warpaint;
                return true;
            case "addbodypaint":
                category = SkyrimRaceMenuPaintCategory.Body;
                return true;
            case "addhandpaint":
                category = SkyrimRaceMenuPaintCategory.Hands;
                return true;
            case "addfeetpaint":
                category = SkyrimRaceMenuPaintCategory.Feet;
                return true;
            case "addfacepaint":
                category = SkyrimRaceMenuPaintCategory.Face;
                return true;
            default:
                category = default;
                return false;
        }
    }

    private sealed class Cursor(byte[] data)
    {
        private int position;

        public int ReadByte()
        {
            EnsureAvailable(1);
            return data[position++];
        }

        public int ReadUInt16()
        {
            EnsureAvailable(2);
            int value = (data[position] << 8) | data[position + 1];
            position += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            EnsureAvailable(4);
            uint value = ((uint)data[position] << 24) |
                         ((uint)data[position + 1] << 16) |
                         ((uint)data[position + 2] << 8) |
                         data[position + 3];
            position += 4;
            return value;
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public string ReadWideString()
        {
            int length = ReadUInt16();
            EnsureAvailable(length);
            string value = Encoding.Latin1.GetString(data, position, length);
            position += length;
            return value;
        }

        public string ReadStringIndex(string[] strings)
        {
            int index = ReadUInt16();
            if ((uint)index >= (uint)strings.Length)
                throw new InvalidDataException("PEX string-table index is out of bounds.");
            return strings[index];
        }

        public void Skip(int count)
        {
            EnsureAvailable(count);
            position += count;
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || position < 0 || position > data.Length - count)
                throw new InvalidDataException("Truncated Skyrim PEX data.");
        }
    }

    private readonly record struct PexOperand(
        SkyrimPexPaintOperandKind Kind,
        string? StringValue,
        int IntegerValue);

    private sealed record PexCall(
        string Method,
        ImmutableArray<PexOperand> Arguments);
}

internal sealed record SkyrimPexPaintParseResult(
    bool MentionsPaint,
    bool Parsed,
    ImmutableArray<SkyrimPexPaintRegistration> Registrations);

internal sealed record SkyrimPexPaintRegistration(
    SkyrimRaceMenuPaintCategory Category,
    string Name,
    string Path,
    ImmutableArray<SkyrimPexPaintSlot> Slots);

internal sealed record SkyrimPexPaintSlot(
    int Index,
    SkyrimPexPaintOperandKind OperandKind,
    string? Value);

internal enum SkyrimPexPaintOperandKind
{
    Null,
    Identifier,
    StringLiteral,
    Integer,
    Float,
    Boolean
}
