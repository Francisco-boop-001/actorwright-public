using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

public sealed class BodySlideTriInspectionService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IBodySlideTriInspectionService
{
    private const int MaxTriBytes = 128 * 1024 * 1024;

    public async ValueTask<BodySlideTriInspectionResult> InspectAsync(
        BodySlideTriInspectionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.TriPath.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("body-tri-outside-lab", DiagnosticSeverity.Error, "TRI input must remain under the K-only lab root."));
        if (!File.Exists(request.TriPath.Value)) diagnostics.Add(new Diagnostic("body-tri-missing", DiagnosticSeverity.Error, "The TRI input does not exist."));
        if (!string.Equals(Path.GetExtension(request.TriPath.Value), ".tri", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("body-tri-extension-invalid", DiagnosticSeverity.Error, "The TRI input must use the .tri extension."));
        var parent = Path.GetDirectoryName(request.TriPath.Value); if (parent is not null) diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        AddReparseDiagnostic(diagnostics, request.TriPath.Value, "TRI input");
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return new(request.Edition, request.TriPath, null, diagnostics.ToImmutable());
        try
        {
            var info = new FileInfo(request.TriPath.Value);
            if (info.Length > MaxTriBytes) throw new TriSizeLimitException($"The PIRT TRI file exceeds the {MaxTriBytes} byte safety limit.");
            var bytes = await File.ReadAllBytesAsync(request.TriPath.Value, cancellationToken);
            if (bytes.Length > MaxTriBytes) throw new TriSizeLimitException($"The PIRT TRI file exceeds the {MaxTriBytes} byte safety limit.");
            var catalog = TriCatalogReader.Read(bytes, new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics);
            return new(request.Edition, request.TriPath, catalog, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception) { diagnostics.Add(new Diagnostic("body-tri-read-failed", DiagnosticSeverity.Error, exception.Message)); }
        catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("body-tri-read-denied", DiagnosticSeverity.Error, exception.Message)); }
        catch (TriSizeLimitException exception) { diagnostics.Add(new Diagnostic("body-tri-size-limit", DiagnosticSeverity.Error, exception.Message)); }
        catch (FormatException exception) { diagnostics.Add(new Diagnostic("body-tri-invalid", DiagnosticSeverity.Error, exception.Message)); }
        return new(request.Edition, request.TriPath, null, diagnostics.ToImmutable());
    }

    private sealed class TriSizeLimitException(string message) : FormatException(message);

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) { diagnostics.Add(new Diagnostic("body-tri-reparse-refused", DiagnosticSeverity.Error, $"The {role} traverses a reparse point.")); return; }
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("body-tri-path-inspection-failed", DiagnosticSeverity.Error, exception.Message)); return; }
            catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("body-tri-path-inspection-denied", DiagnosticSeverity.Error, exception.Message)); return; }
            var parent = Directory.GetParent(current)?.FullName; if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break; current = parent;
        }
    }

    private static class TriCatalogReader
    {
        internal static BodySlideTriCatalog Read(byte[] bytes, Sha256Hash hash, ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            if (bytes.Length < 6 || !bytes.AsSpan(0, 4).SequenceEqual("PIRT"u8)) throw new FormatException("TRI input is not a PIRT BodySlide file.");
            var reader = new Cursor(bytes[4..]); var shapes = new Dictionary<string, ShapeBuilder>(StringComparer.Ordinal);
            ReadSection(reader, shapes, BodySlideTriMorphType.Position, diagnostics); ReadSection(reader, shapes, BodySlideTriMorphType.Uv, diagnostics);
            if (reader.Remaining != 0) throw new FormatException("PIRT TRI contains trailing bytes after its two morph sections.");
            return new BodySlideTriCatalog(hash, shapes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new BodySlideTriShape(pair.Key, pair.Value.Morphs.Values.OrderBy(morph => morph.Name, StringComparer.OrdinalIgnoreCase).ThenBy(morph => morph.Name, StringComparer.Ordinal).Select(morph => new BodySlideTriMorph(morph.Name, morph.Type, morph.Offsets)).ToImmutableArray())).ToImmutableArray());
        }
        private static void ReadSection(Cursor reader, Dictionary<string, ShapeBuilder> shapes, BodySlideTriMorphType type, ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            var shapeCount = reader.ReadUInt16();
            for (var shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                var shapeName = reader.ReadAscii(reader.ReadByte(), "shape"); if (!shapes.TryGetValue(shapeName, out var shape)) { shape = new ShapeBuilder(); shapes.Add(shapeName, shape); }
                var morphCount = reader.ReadUInt16();
                for (var morphIndex = 0; morphIndex < morphCount; morphIndex++)
                {
                    var morphName = reader.ReadAscii(reader.ReadByte(), "morph"); var multiplier = reader.ReadSingle(); if (!float.IsFinite(multiplier)) throw new FormatException($"PIRT {type} morph '{morphName}' has a non-finite multiplier.");
                    var vertexCount = reader.ReadUInt16(); var offsets = ImmutableArray.CreateBuilder<BodySlideTriOffset>(vertexCount);
                    for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++) { var index = reader.ReadUInt16(); var x = reader.ReadInt16() * multiplier; var y = reader.ReadInt16() * multiplier; var z = type == BodySlideTriMorphType.Position ? reader.ReadInt16() * multiplier : 0F; if (x != 0F || y != 0F || z != 0F) offsets.Add(new BodySlideTriOffset(index, x, y, z)); }
                    if (offsets.Count == 0) continue; if (shape.Morphs.ContainsKey(morphName)) { diagnostics.Add(new Diagnostic("body-tri-duplicate-morph", DiagnosticSeverity.Warning, $"Duplicate PIRT morph '{shapeName}/{morphName}' uses the first occurrence.")); continue; } shape.Morphs.Add(morphName, new MorphBuilder(morphName, type, offsets.ToImmutable()));
                }
            }
        }
        private sealed class ShapeBuilder { internal Dictionary<string, MorphBuilder> Morphs { get; } = new(StringComparer.OrdinalIgnoreCase); }
        private sealed record MorphBuilder(string Name, BodySlideTriMorphType Type, ImmutableArray<BodySlideTriOffset> Offsets);
        private sealed class Cursor(byte[] bytes)
        {
            private readonly byte[] _bytes = bytes; private int _position; internal int Remaining => _bytes.Length - _position;
            internal byte ReadByte() { Ensure(1); return _bytes[_position++]; }
            internal ushort ReadUInt16() { Ensure(2); var value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(_position, 2)); _position += 2; return value; }
            internal short ReadInt16() { Ensure(2); var value = BinaryPrimitives.ReadInt16LittleEndian(_bytes.AsSpan(_position, 2)); _position += 2; return value; }
            internal float ReadSingle() { Ensure(4); var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(_position, 4))); _position += 4; return value; }
            internal string ReadAscii(byte length, string kind) { if (length == 0) throw new FormatException($"PIRT {kind} name cannot be empty."); Ensure(length); var slice = _bytes.AsSpan(_position, length); for (var index = 0; index < slice.Length; index++) if (slice[index] > 0x7F) throw new FormatException($"PIRT {kind} name is not ASCII."); var value = Encoding.ASCII.GetString(slice); _position += length; return value; }
            private void Ensure(int count) { if (count < 0 || Remaining < count) throw new FormatException("PIRT TRI ended before a declared field was complete."); }
        }
    }
}
