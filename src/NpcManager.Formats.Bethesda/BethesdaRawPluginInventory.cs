using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Lossless, ordered TES4 inventory used for standalone-copy preservation
/// evidence. Entries are deliberately a list: signatures and local FormIDs
/// are not unique across a plugin.
/// </summary>
public sealed record BethesdaRawPluginInventoryEntry(
    string GroupPath,
    int Ordinal,
    string Signature,
    uint RawFormId,
    uint RawFlags,
    int ByteLength,
    Sha256Hash Sha256);

/// <summary>
/// Ordered group-header evidence. The hash excludes only the mutable GRUP
/// size field; labels, group types, flags, stamps, and all other header bytes
/// remain independently bound.
/// </summary>
public sealed record BethesdaRawPluginGroupInventoryEntry(
    string GroupPath,
    int Ordinal,
    string Label,
    uint GroupType,
    int ByteLength,
    Sha256Hash HeaderSha256);

public sealed record BethesdaRawPluginInventoryVerification(
    bool Verified,
    ImmutableArray<BethesdaRawPluginInventoryEntry> Source,
    ImmutableArray<BethesdaRawPluginInventoryEntry> Output,
    ImmutableArray<BethesdaRawPluginGroupInventoryEntry> SourceGroups,
    ImmutableArray<BethesdaRawPluginGroupInventoryEntry> OutputGroups,
    ImmutableArray<Diagnostic> Diagnostics);

public static class BethesdaRawPluginInventory
{
    private const int RecordHeaderSize = 24;

    public static ImmutableArray<BethesdaRawPluginInventoryEntry> Read(
        WorkspacePath plugin) => Read(plugin.Value);

    public static ImmutableArray<BethesdaRawPluginInventoryEntry> Read(
        string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The plugin does not exist.", path);
        return ReadDocument(File.ReadAllBytes(path)).Records;
    }

    public static ImmutableArray<BethesdaRawPluginGroupInventoryEntry> ReadGroups(
        WorkspacePath plugin)
    {
        if (!File.Exists(plugin.Value))
            throw new FileNotFoundException("The plugin does not exist.", plugin.Value);
        return ReadDocument(File.ReadAllBytes(plugin.Value)).Groups;
    }

    public static BethesdaRawPluginInventoryVerification Verify(
        WorkspacePath source,
        WorkspacePath output,
        FormId targetFormId)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ImmutableArray<BethesdaRawPluginInventoryEntry> sourceRows = [];
        ImmutableArray<BethesdaRawPluginInventoryEntry> outputRows = [];
        ImmutableArray<BethesdaRawPluginGroupInventoryEntry> sourceGroups = [];
        ImmutableArray<BethesdaRawPluginGroupInventoryEntry> outputGroups = [];
        try
        {
            var sourceDocument = ReadDocument(File.ReadAllBytes(source.Value));
            var outputDocument = ReadDocument(File.ReadAllBytes(output.Value));
            sourceRows = sourceDocument.Records;
            outputRows = outputDocument.Records;
            sourceGroups = sourceDocument.Groups;
            outputGroups = outputDocument.Groups;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException)
        {
            diagnostics.Add(new Diagnostic(
                "npc-standalone-inventory-read-failed",
                DiagnosticSeverity.Error,
                exception.Message));
            return new BethesdaRawPluginInventoryVerification(
                false, sourceRows, outputRows, sourceGroups, outputGroups,
                diagnostics.ToImmutable());
        }

        var targetLocal = targetFormId.Value & 0x00FF_FFFFu;
        var sourceTargets = sourceRows.Where(row =>
                row.Signature == "NPC_" &&
                (row.RawFormId & 0x00FF_FFFFu) == targetLocal)
            .ToArray();
        var outputTargets = outputRows.Where(row =>
                row.Signature == "NPC_" &&
                (row.RawFormId & 0x00FF_FFFFu) == targetLocal)
            .ToArray();
        if (sourceTargets.Length != 1 || outputTargets.Length != 1)
        {
            diagnostics.Add(new Diagnostic(
                "npc-standalone-inventory-drift",
                DiagnosticSeverity.Error,
                $"Expected one target NPC in source and output; observed source={sourceTargets.Length}, output={outputTargets.Length}."));
        }

        if (sourceRows.Length != outputRows.Length)
        {
            diagnostics.Add(new Diagnostic(
                "npc-standalone-inventory-drift",
                DiagnosticSeverity.Error,
                $"Ordered record inventory cardinality changed from {sourceRows.Length} to {outputRows.Length}."));
        }

        var count = Math.Min(sourceRows.Length, outputRows.Length);
        for (var index = 0; index < count; index++)
        {
            var expected = sourceRows[index];
            var actual = outputRows[index];
            var target = expected.Signature == "NPC_" &&
                         (expected.RawFormId & 0x00FF_FFFFu) == targetLocal;
            if (!SameIdentity(expected, actual, target))
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-inventory-drift",
                    DiagnosticSeverity.Error,
                    $"Ordered record inventory drifted at ordinal {index}: expected {expected.Signature}/{expected.RawFormId:X8}, observed {actual.Signature}/{actual.RawFormId:X8}."));
                continue;
            }

            if (!target && !string.Equals(
                    expected.Sha256.Value,
                    actual.Sha256.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-inventory-drift",
                    DiagnosticSeverity.Error,
                    $"Non-target record {expected.Signature}/{expected.RawFormId:X8} changed bytes."));
            }
        }

        if (sourceGroups.Length != outputGroups.Length)
        {
            diagnostics.Add(new Diagnostic(
                "npc-standalone-group-drift",
                DiagnosticSeverity.Error,
                $"Ordered group inventory cardinality changed from {sourceGroups.Length} to {outputGroups.Length}."));
        }

        var targetGroupPaths = sourceTargets.Length == 1
            ? AncestorPaths(sourceTargets[0].GroupPath)
            : ImmutableHashSet<string>.Empty;
        var groupCount = Math.Min(sourceGroups.Length, outputGroups.Length);
        for (var index = 0; index < groupCount; index++)
        {
            var expected = sourceGroups[index];
            var actual = outputGroups[index];
            if (!SameGroupIdentity(expected, actual))
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-group-drift",
                    DiagnosticSeverity.Error,
                    $"Ordered group header identity drifted at ordinal {index}: expected {expected.GroupPath}, observed {actual.GroupPath}."));
                continue;
            }

            if (expected.HeaderSha256 != actual.HeaderSha256)
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-group-drift",
                    DiagnosticSeverity.Error,
                    $"Group header bytes changed at '{expected.GroupPath}'."));
            }
            if (expected.ByteLength != actual.ByteLength &&
                !targetGroupPaths.Contains(expected.GroupPath))
            {
                diagnostics.Add(new Diagnostic(
                    "npc-standalone-group-drift",
                    DiagnosticSeverity.Error,
                    $"Non-target group size changed at '{expected.GroupPath}'."));
            }
        }

        return new BethesdaRawPluginInventoryVerification(
            diagnostics.All(item => item.Severity != DiagnosticSeverity.Error),
            sourceRows,
            outputRows,
            sourceGroups,
            outputGroups,
            diagnostics.ToImmutable());
    }

    internal static ImmutableArray<BethesdaRawPluginInventoryEntry> Read(
        byte[] bytes) => ReadDocument(bytes).Records;

    internal static ImmutableArray<BethesdaRawPluginGroupInventoryEntry> ReadGroups(
        byte[] bytes) => ReadDocument(bytes).Groups;

    private static RawInventoryDocument ReadDocument(byte[] bytes)
    {
        if (bytes.Length < RecordHeaderSize ||
            !bytes.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException("The plugin must begin with a TES4 record.");

        int tes4Length;
        try
        {
            tes4Length = checked(RecordHeaderSize +
                (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "npc-standalone-inventory-size: the TES4 header size exceeds the supported range.",
                exception);
        }
        if (tes4Length > bytes.Length)
            throw new InvalidDataException(
                "npc-standalone-inventory-bounds: the TES4 header exceeds the plugin.");

        var rows = ImmutableArray.CreateBuilder<BethesdaRawPluginInventoryEntry>();
        var groups = ImmutableArray.CreateBuilder<BethesdaRawPluginGroupInventoryEntry>();
        Walk(bytes, tes4Length, bytes.Length, string.Empty, rows, groups);
        return new RawInventoryDocument(rows.ToImmutable(), groups.ToImmutable());
    }

    private static void Walk(
        byte[] bytes,
        int start,
        int end,
        string groupPath,
        ImmutableArray<BethesdaRawPluginInventoryEntry>.Builder rows,
        ImmutableArray<BethesdaRawPluginGroupInventoryEntry>.Builder groups)
    {
        var position = start;
        var recordOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var groupOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        while (position < end)
        {
            if (position + RecordHeaderSize > end)
                throw new InvalidDataException(
                    "npc-standalone-inventory-header: a TES4 record header is truncated.");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 4, 4));
            int length;
            try
            {
                length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(RecordHeaderSize + (int)size);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "npc-standalone-inventory-size: a record size exceeds the supported range.",
                    exception);
            }
            if (length < RecordHeaderSize)
                throw new InvalidDataException(
                    "npc-standalone-inventory-size: a record is smaller than its header.");
            int recordEnd;
            try
            {
                recordEnd = checked(position + length);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "npc-standalone-inventory-bounds: a record end exceeds its supported range.",
                    exception);
            }
            if (recordEnd > end)
                throw new InvalidDataException(
                    "npc-standalone-inventory-bounds: a record exceeds its containing group.");

            if (signature == "GRUP")
            {
                var label = Encoding.ASCII.GetString(bytes, position + 8, 4);
                var groupType = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 12, 4));
                var groupKey = $"{label}:{groupType:X8}";
                var ordinal = groupOrdinals.GetValueOrDefault(groupKey);
                groupOrdinals[groupKey] = ordinal + 1;
                var childPath = string.IsNullOrEmpty(groupPath)
                    ? $"{groupKey}[{ordinal}]"
                    : $"{groupPath}/{groupKey}[{ordinal}]";
                var header = bytes.AsSpan(position, RecordHeaderSize).ToArray();
                Array.Clear(header, 4, 4);
                groups.Add(new BethesdaRawPluginGroupInventoryEntry(
                    childPath,
                    ordinal,
                    label,
                    groupType,
                    length,
                    new Sha256Hash(Convert.ToHexString(SHA256.HashData(header)))));
                Walk(bytes, position + RecordHeaderSize, recordEnd,
                    childPath, rows, groups);
            }
            else
            {
                var ordinal = recordOrdinals.GetValueOrDefault(groupPath);
                recordOrdinals[groupPath] = ordinal + 1;
                rows.Add(new BethesdaRawPluginInventoryEntry(
                    groupPath,
                    ordinal,
                    signature,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 12, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 8, 4)),
                    length,
                    new Sha256Hash(Convert.ToHexString(
                        SHA256.HashData(bytes.AsSpan(position, length))))));
            }
            position = recordEnd;
        }
        if (position != end)
            throw new InvalidDataException(
                "npc-standalone-inventory-trailing-bytes: a TES4 group contains trailing bytes.");
    }

    private static ImmutableHashSet<string> AncestorPaths(string groupPath)
    {
        if (string.IsNullOrEmpty(groupPath)) return ImmutableHashSet<string>.Empty;
        var parts = groupPath.Split('/');
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        for (var count = 1; count <= parts.Length; count++)
            builder.Add(string.Join('/', parts.Take(count)));
        return builder.ToImmutable();
    }

    private static bool SameIdentity(
        BethesdaRawPluginInventoryEntry expected,
        BethesdaRawPluginInventoryEntry actual,
        bool target) =>
        string.Equals(expected.GroupPath, actual.GroupPath,
            StringComparison.Ordinal) &&
        expected.Ordinal == actual.Ordinal &&
        string.Equals(expected.Signature, actual.Signature,
            StringComparison.Ordinal) &&
        expected.RawFormId == actual.RawFormId &&
        expected.RawFlags == actual.RawFlags &&
        (target || expected.ByteLength == actual.ByteLength);

    private static bool SameGroupIdentity(
        BethesdaRawPluginGroupInventoryEntry expected,
        BethesdaRawPluginGroupInventoryEntry actual) =>
        string.Equals(expected.GroupPath, actual.GroupPath,
            StringComparison.Ordinal) &&
        expected.Ordinal == actual.Ordinal &&
        string.Equals(expected.Label, actual.Label,
            StringComparison.Ordinal) &&
        expected.GroupType == actual.GroupType;

    private sealed record RawInventoryDocument(
        ImmutableArray<BethesdaRawPluginInventoryEntry> Records,
        ImmutableArray<BethesdaRawPluginGroupInventoryEntry> Groups);
}
