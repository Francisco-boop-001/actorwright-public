using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;

namespace NpcManager.Formats.Bethesda;

internal static class BethesdaSkyrimRawTopologyPreflight
{
    private const int HeaderSize = 24;
    private const uint CellGroupLabel = 0x4C4C4543; // "CELL" in little-endian GRUP labels

    private sealed record GroupFrame(int Offset, int End, uint Type, uint Label, string Display);

    private sealed record Sibling(string Signature, uint? FormId, int Offset, string Display);

    internal static void Validate(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = File.ReadAllBytes(path);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(bytes, cancellationToken);
    }

    internal static void Validate(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WalkContainer(bytes, 0, bytes.Length, ImmutableArray<GroupFrame>.Empty, cancellationToken);
    }

    private static void WalkContainer(
        ReadOnlySpan<byte> bytes,
        int start,
        int end,
        ImmutableArray<GroupFrame> ancestry,
        CancellationToken cancellationToken)
    {
        var offset = start;
        Sibling? previous = null;
        while (offset < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (end - offset < HeaderSize)
            {
                throw new InvalidDataException($"TES record header at 0x{offset:X8} is truncated.");
            }

            var signature = ReadSignature(bytes, offset);
            if (signature == "GRUP")
            {
                var size = ReadUInt32(bytes, offset + 4, "GRUP size");
                var label = ReadUInt32(bytes, offset + 8, "GRUP label");
                var type = ReadUInt32(bytes, offset + 12, "GRUP type");
                if (size < HeaderSize)
                {
                    throw new InvalidDataException($"GRUP at 0x{offset:X8} has invalid size {size}.");
                }

                var groupEndLong = (long)offset + size;
                if (groupEndLong > end || groupEndLong > int.MaxValue)
                {
                    throw new InvalidDataException($"GRUP at 0x{offset:X8} exceeds its parent container.");
                }

                var groupEnd = (int)groupEndLong;
                var display = FormatGroupDisplay(type, label);
                if (type == 6 && IsInteriorCellChildrenAncestry(ancestry))
                {
                    RequireCellAnchor(offset, label, ancestry, previous);
                }

                var frame = new GroupFrame(offset, groupEnd, type, label, display);
                WalkContainer(bytes, offset + HeaderSize, groupEnd,
                    ancestry.Add(frame), cancellationToken);
                previous = new Sibling("GRUP", null, offset, display);
                offset = groupEnd;
                continue;
            }

            var dataSize = ReadUInt32(bytes, offset + 4, "record data size");
            var recordEndLong = (long)offset + HeaderSize + dataSize;
            if (recordEndLong > end || recordEndLong > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Record {signature} at 0x{offset:X8} exceeds its parent container.");
            }

            var formId = ReadUInt32(bytes, offset + 12, "record FormID");
            previous = new Sibling(signature, formId, offset,
                $"SIGNATURE {signature} 0x{formId:X8}");
            offset = (int)recordEndLong;
        }

        if (offset != end)
        {
            throw new InvalidDataException($"Container ended at an invalid offset 0x{offset:X8}.");
        }
    }

    private static void RequireCellAnchor(
        int offset,
        uint label,
        ImmutableArray<GroupFrame> ancestry,
        Sibling? previous)
    {
        if (previous is not { Signature: "CELL", FormId: var formId } || formId != label)
        {
            var observed = previous is null
                ? "<none>"
                : previous.Signature == "GRUP"
                    ? $"GRUP({previous.Display})"
                    : previous.Display;
            var path = FormatPath(ancestry, label);
            var observedText = previous is { Signature: "CELL", FormId: var precedingId }
                ? $"observed preceding CELL 0x{precedingId:X8}"
                : $"observed preceding sibling {observed}";
            throw new PluginReadDiagnosticException(
                "orphan-cell-children",
                $"orphan-cell-children at offset 0x{offset:X8}: {path}; " +
                $"expected preceding CELL 0x{label:X8}; {observedText}");
        }
    }

    private static bool IsInteriorCellChildrenAncestry(ImmutableArray<GroupFrame> ancestry)
    {
        return ancestry.Length == 3 &&
               ancestry[0].Type == 0 &&
               ancestry[0].Label == CellGroupLabel &&
               ancestry[1].Type == 2 &&
               ancestry[2].Type == 3;
    }

    private static string FormatPath(ImmutableArray<GroupFrame> ancestry, uint label)
    {
        var parts = ancestry.Select(frame => frame.Display).ToList();
        parts.Add(FormatGroupDisplay(6, label));
        return string.Join(" / ", parts);
    }

    private static string FormatGroupDisplay(uint type, uint label)
    {
        return type switch
        {
            0 => "CELL(type=0)",
            2 => $"block(label={label},type=2)",
            3 => $"sub-block(label={label},type=3)",
            6 => $"cell-children(label=0x{label:X8},type=6)",
            _ => $"GRUP(label=0x{label:X8},type={type})"
        };
    }

    private static string ReadSignature(ReadOnlySpan<byte> bytes, int offset)
    {
        return Encoding.ASCII.GetString(bytes.Slice(offset, 4));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset, string field)
    {
        if ((uint)offset > (uint)(bytes.Length - sizeof(uint)))
        {
            throw new InvalidDataException($"Cannot read {field} at 0x{offset:X8}.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));
    }
}
