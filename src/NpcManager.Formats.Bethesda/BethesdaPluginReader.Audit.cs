using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed partial class BethesdaPluginReader
{
    private static PluginInspection? ReadSkeletalWorldAudit(string path, PluginName plugin,
        ImmutableArray<PluginName> normalizedMasters)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var census = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(bytes);
        var missingParents = census.Groups.Where(group => group.Type == 1 &&
            !census.NonTes4Records.Any(row => row.Signature == "WRLD" && row.RawFormId == group.RawLabel &&
                row.Offset + row.Length == group.Offset)).ToArray();
        if (missingParents.Length == 0) return null;
        if (!normalizedMasters.IsDefault)
            throw new PluginReadDiagnosticException("plugin-audit-normalization-unavailable",
                "A skeletal world parent supports raw audit; master normalization requires a complete typed parent graph.");

        var tes4 = census.Records.Single(row => row.Signature == "TES4");
        var header = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(bytes, tes4);
        var hedr = header.Where(row => row.Signature == "HEDR").ToArray();
        if (tes4.Offset != 0 || hedr.Length != 1 || hedr[0].Length < 18)
            throw new PluginReadDiagnosticException("plugin-audit-header",
                "The skeletal audit requires an initial TES4 with one complete HEDR.");
        var masterNames = ImmutableArray.CreateBuilder<PluginName>();
        for (int index = 0; index < header.Length; index++)
        {
            var row = header[index];
            if (row.Signature != "MAST") continue;
            if (row.Length <= 7 || row.Bytes[^1] != 0 || index + 1 >= header.Length ||
                header[index + 1].Signature != "DATA" || header[index + 1].Length != 14)
                throw new PluginReadDiagnosticException("plugin-audit-master-table",
                    "A skeletal audit MAST requires a terminated name and its eight-byte DATA pair.");
            masterNames.Add(new PluginName(Encoding.UTF8.GetString(row.Bytes.AsSpan(6)).TrimEnd('\0')));
        }
        var masters = masterNames.ToImmutable();
        if (masters.Length > byte.MaxValue ||
            masters.Select(row => row.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != masters.Length ||
            masters.Any(row => row.Value.Equals(plugin.Value, StringComparison.OrdinalIgnoreCase)))
            throw new PluginReadDiagnosticException("plugin-audit-master-table",
                "The skeletal audit master table has duplicate, self or excessive owners.");
        var hashes = BethesdaRawRecordDigestReader.Read(bytes, exactGroupLabels: true);
        var records = census.NonTes4Records.Select(row =>
        {
            int owner = checked((int)(row.RawFormId >> 24));
            if (owner > masters.Length)
                throw new InvalidDataException("A skeletal audit record has an invalid owner index.");
            // The complete original bytes remain authoritative. Compressed
            // payloads are hashed intact; no guessed subrecord decoding occurs.
            var fields = (row.Flags & 0x40000) == 0
                ? BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(bytes, row)
                : [];
            string? editorId = fields.Where(field => field.Signature == "EDID")
                .Select(field => Encoding.UTF8.GetString(field.Bytes.AsSpan(6)).TrimEnd('\0'))
                .FirstOrDefault();
            return new PluginRecordSummary(new FormId(row.RawFormId & 0x00ffffff), row.Signature,
                editorId, null, row.Signature == "NPC_", (row.Flags & 0x20) != 0,
                RawRecordSha256: hashes[(row.RawFormId, row.Signature)],
                OwnerPlugin: owner == masters.Length ? plugin : masters[owner]);
        }).ToImmutableArray();
        var diagnostics = missingParents.Select(group => new Diagnostic(
            "plugin-audit-skeletal-world-parent", DiagnosticSeverity.Warning,
            $"WRLD parent 0x{group.RawLabel:X8} at group offset 0x{group.Offset:X8} is skeletal; " +
            "the audit retains every original record and group digest without claiming complete world semantics."))
            .ToImmutableArray();
        return new PluginInspection(GameEdition.SkyrimSpecialEdition, plugin, masters, records, diagnostics);
    }
}
