using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed partial class BethesdaObjectTemplateBinaryWriteService
{
    private static ImmutableArray<BinaryRecord> FindArmoRecords(byte[] bytes)
    {
        var candidates = ImmutableArray.CreateBuilder<BinaryRecord>();
        for (var offset = 0; offset + 24 <= bytes.Length; offset++)
        {
            if (bytes[offset] != (byte)'A' || bytes[offset + 1] != (byte)'R' || bytes[offset + 2] != (byte)'M' || bytes[offset + 3] != (byte)'O') continue;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (size > int.MaxValue || offset + 24L + size > bytes.Length) continue;
            candidates.Add(new BinaryRecord(offset, checked((int)size)));
        }
        return candidates.ToImmutable();
    }

    private static ImmutableArray<ModKey> ReadMasterNames(byte[] bytes)
    {
        var tes4 = FindRecord(bytes, "TES4");
        if (tes4 is null) throw new InvalidDataException("The materialized plugin has no TES4 header.");
        var payload = bytes.AsSpan(tes4.Value.Offset + 24, tes4.Value.DataSize);
        var masters = ImmutableArray.CreateBuilder<ModKey>();
        var offset = 0;
        while (offset + 6 <= payload.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset + 4, 2));
            if (offset + 6 + length > payload.Length) throw new InvalidDataException("The materialized TES4 header contains a truncated subrecord.");
            if (Encoding.ASCII.GetString(payload.Slice(offset, 4)) == "MAST")
            {
                var value = Encoding.ASCII.GetString(payload.Slice(offset + 6, length)).TrimEnd('\0');
                masters.Add(new ModKey(Path.GetFileNameWithoutExtension(value), ModType.Plugin));
            }
            offset += 6 + length;
        }
        if (offset != payload.Length) throw new InvalidDataException("The materialized TES4 header contains trailing bytes after its subrecords.");
        return masters.ToImmutable();
    }

    private static BinaryRecord? FindRecord(byte[] bytes, string signature)
    {
        for (var offset = 0; offset + 24 <= bytes.Length; offset++)
        {
            if (Encoding.ASCII.GetString(bytes, offset, 4) != signature) continue;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (size <= int.MaxValue && offset + 24L + size <= bytes.Length) return new BinaryRecord(offset, checked((int)size));
        }
        return null;
    }

    private static ImmutableArray<BinaryGroup> FindContainingGroups(byte[] bytes, int recordOffset, int recordEnd)
    {
        var groups = ImmutableArray.CreateBuilder<BinaryGroup>();
        for (var offset = 0; offset + 24 <= bytes.Length; offset++)
        {
            if (bytes[offset] != (byte)'G' || bytes[offset + 1] != (byte)'R' || bytes[offset + 2] != (byte)'U' || bytes[offset + 3] != (byte)'P') continue;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (size < 24 || size > int.MaxValue || offset + size > bytes.Length) continue;
            if (offset <= recordOffset && recordEnd <= offset + size) groups.Add(new BinaryGroup(offset, checked((int)size)));
        }
        return groups.ToImmutable();
    }

    private ImmutableArray<Diagnostic> ValidatePaths(ObjectTemplateBinaryWriteRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.Fallout4) diagnostics.Add(new Diagnostic("object-template-binary-edition", DiagnosticSeverity.Error, "OBTS binary writing is Fallout 4-only."));
        if (!request.Proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("object-template-binary-proposal-outside-lab", DiagnosticSeverity.Error, "The proposal must remain under the K-only lab root."));
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("object-template-binary-output-outside-lab", DiagnosticSeverity.Error, "The output must remain under the K-only lab root."));
        if (!request.Output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("object-template-binary-output-extension", DiagnosticSeverity.Error, "The bounded OBTS writer emits ordinary .esp plugins only."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("object-template-binary-output-exists", DiagnosticSeverity.Error, "Binary OBTS writes never overwrite an existing plugin."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("object-template-binary-output-parent", DiagnosticSeverity.Error, "The output plugin directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        AddReparseDiagnostic(diagnostics, request.Proposal.Value, "proposal");
        if (!File.Exists(request.Proposal.Value)) diagnostics.Add(new Diagnostic("object-template-binary-proposal-missing", DiagnosticSeverity.Error, "The object-template proposal does not exist."));
        if (request.Properties is { } properties)
        {
            if (!properties.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("object-template-binary-properties-outside-lab", DiagnosticSeverity.Error, "The property proposal must remain under the K-only lab root."));
            if (!properties.Value.EndsWith(".object-template-properties-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("object-template-binary-properties-extension", DiagnosticSeverity.Error, "The property proposal must use the .object-template-properties-proposal.json extension."));
            AddReparseDiagnostic(diagnostics, properties.Value, "properties");
            if (!File.Exists(properties.Value)) diagnostics.Add(new Diagnostic("object-template-binary-properties-missing", DiagnosticSeverity.Error, "The object-template property proposal does not exist."));
        }
        return diagnostics.ToImmutable();
    }

    private WorkspacePath? TryWorkspacePath(string value, ImmutableArray<Diagnostic>.Builder diagnostics, string role)
    {
        try
        {
            var path = new WorkspacePath(value);
            if (!path.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("object-template-binary-source-outside-lab", DiagnosticSeverity.Error, $"The {role} path must remain under the K-only lab root."));
            if (!File.Exists(path.Value)) diagnostics.Add(new Diagnostic("object-template-binary-source-missing", DiagnosticSeverity.Error, "The proposal source plugin does not exist."));
            else AddReparseDiagnostic(diagnostics, path.Value, role);
            return path;
        }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("object-template-binary-source-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
    }

    private static ModKey? TryPluginName(string value, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try { return new ModKey(Path.GetFileNameWithoutExtension(value), ModType.Plugin); }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("object-template-binary-plugin-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
    }

    private static void ValidateMasters(IEnumerable<FormReference> references, ModKey source, IEnumerable<ModKey> declaredMasters)
    {
        var allowed = declaredMasters.Append(source).ToHashSet();
        foreach (var reference in references)
        {
            var plugin = new ModKey(Path.GetFileNameWithoutExtension(reference.Plugin.Value), ModType.Plugin);
            if (!allowed.Contains(plugin)) throw new InvalidDataException($"Reference '{reference}' is not provided by the source plugin or its declared masters.");
        }
    }

    private static void AddMasters(ExtendedList<MasterReference> masters, ModKey source, IEnumerable<FormReference> references,
        ModKey output, IEnumerable<ModKey> sourceMasters)
    {
        foreach (var key in new[] { source }.Concat(sourceMasters).Concat(references.Select(reference => new ModKey(Path.GetFileNameWithoutExtension(reference.Plugin.Value), ModType.Plugin)))
                     .Where(key => key != output).Distinct())
            if (!masters.Any(item => item.Master == key)) masters.Add(new MasterReference { Master = key });
    }

    private static ModKey ToModKey(string path) => new(Path.GetFileNameWithoutExtension(path), ModType.Plugin);
    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static ObjectTemplateBinaryWriteResult Refused(ObjectTemplateBinaryWriteRequest request, FormId? target, ImmutableArray<Diagnostic> diagnostics) => new(false, request.Proposal, request.Output, target, null, diagnostics);

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                { diagnostics.Add(new Diagnostic("object-template-binary-reparse", DiagnosticSeverity.Error, $"The {role} traverses a reparse point.")); return; }
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("object-template-binary-path-inspection", DiagnosticSeverity.Error, exception.Message)); return; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
    private readonly record struct BinaryRecord(int Offset, int DataSize);
    private readonly record struct BinaryGroup(int Offset, int Size);
}
