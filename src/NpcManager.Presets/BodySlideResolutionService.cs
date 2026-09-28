using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

public sealed class BodySlideResolutionService(
    IPresetService presetService,
    IBodySlideTriInspectionService triInspectionService,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IBodySlideResolutionService
{
    public BodySlideResolutionService(
        IPresetService presetService,
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
        : this(presetService, new BodySlideTriInspectionService(policy, labRoot), policy, labRoot)
    {
    }

    private const int MaxTriBytes = 128 * 1024 * 1024;

    public async ValueTask<BodySlideResolutionResult> ResolveAsync(
        BodySlideResolutionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Empty(request, diagnostics.ToImmutable());

        var format = request.Edition == GameEdition.Fallout4
            ? PresetFormat.LooksMenu
            : PresetFormat.RaceMenuJslot;
        var preset = await presetService.InspectAsync(
            new PresetParseRequest(format, request.Edition, request.PresetPath), cancellationToken);
        diagnostics.AddRange(preset.Diagnostics);
        if (preset.Document is null || preset.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Empty(request, diagnostics.ToImmutable());

        var requested = preset.Document.Appearance.BodyMorphs
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new BodySlideSliderValue(pair.Key, pair.Value))
            .ToImmutableArray();
        if (requested.Length == 0)
            diagnostics.Add(new Diagnostic("body-sliders-empty", DiagnosticSeverity.Warning,
                "The preset declares no BodySlide body sliders."));

        var triInspection = await triInspectionService.InspectAsync(
            new BodySlideTriInspectionRequest(request.Edition, request.TriPath), cancellationToken);
        diagnostics.AddRange(triInspection.Diagnostics);
        if (triInspection.Catalog is null || triInspection.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Empty(request, diagnostics.ToImmutable(), requested);
        var catalog = triInspection.Catalog;

        var channels = ImmutableArray.CreateBuilder<BodySlideResolvedChannel>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slider in requested)
        {
            if (MathF.Abs(slider.Value) < 0.001F) continue;
            if (IsExcludedSliderName(slider.Name))
            {
                excluded.Add(slider.Name);
                continue;
            }

            var matched = false;
            foreach (var shape in catalog.Shapes)
            {
                var morph = shape.Morphs.FirstOrDefault(item =>
                    string.Equals(item.Name, slider.Name, StringComparison.OrdinalIgnoreCase));
                if (morph is null || morph.Offsets.IsDefaultOrEmpty) continue;
                matched = true;
                channels.Add(new BodySlideResolvedChannel(shape.Name, morph.Name, morph.Type,
                    slider.Value, morph.Offsets));
            }
            if (!matched) missing.Add(slider.Name);
        }

        var summaries = catalog.Shapes
            .Select(shape => new BodySlideTriShapeSummary(shape.Name,
                shape.Morphs.Select(morph => morph.Name).ToImmutableArray()))
            .ToImmutableArray();
        return new BodySlideResolutionResult(request.Edition, request.TriPath, request.PresetPath,
            catalog.SourceHash, summaries, requested, channels.ToImmutable(),
            missing.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ThenBy(item => item, StringComparer.Ordinal).ToImmutableArray(),
            excluded.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ThenBy(item => item, StringComparer.Ordinal).ToImmutableArray(),
            diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidatePaths(BodySlideResolutionRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.TriPath.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("body-tri-outside-lab", DiagnosticSeverity.Error,
                "TRI input must remain under the K-only lab root."));
        if (!request.PresetPath.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("body-preset-outside-lab", DiagnosticSeverity.Error,
                "Preset input must remain under the K-only lab root."));
        if (!File.Exists(request.TriPath.Value))
            diagnostics.Add(new Diagnostic("body-tri-missing", DiagnosticSeverity.Error,
                "The TRI input does not exist."));
        if (!File.Exists(request.PresetPath.Value))
            diagnostics.Add(new Diagnostic("body-preset-missing", DiagnosticSeverity.Error,
                "The preset input does not exist."));
        if (!string.Equals(Path.GetExtension(request.TriPath.Value), ".tri", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("body-tri-extension-invalid", DiagnosticSeverity.Error,
                "The TRI input must use the .tri extension."));
        var triParent = Path.GetDirectoryName(request.TriPath.Value);
        if (triParent is not null) diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(triParent)));
        var presetParent = Path.GetDirectoryName(request.PresetPath.Value);
        if (presetParent is not null) diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(presetParent)));
        AddReparseDiagnostic(diagnostics, request.TriPath.Value, "TRI input");
        AddReparseDiagnostic(diagnostics, request.PresetPath.Value, "preset input");
        return diagnostics.ToImmutable();
    }

    private static BodySlideResolutionResult Empty(BodySlideResolutionRequest request,
        ImmutableArray<Diagnostic> diagnostics, ImmutableArray<BodySlideSliderValue>? requested = null) =>
        new(request.Edition, request.TriPath, request.PresetPath, null, ImmutableArray<BodySlideTriShapeSummary>.Empty,
            requested ?? ImmutableArray<BodySlideSliderValue>.Empty, ImmutableArray<BodySlideResolvedChannel>.Empty,
            ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, diagnostics);

    private static bool IsExcludedSliderName(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (name.Equals("WeightThin", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("WeightMuscular", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("WeightFat", StringComparison.OrdinalIgnoreCase)) return true;
        if (!name.StartsWith("MorphRegion", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(name["MorphRegion".Length..], out _);
    }

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("body-tri-reparse-refused", DiagnosticSeverity.Error,
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("body-tri-path-inspection-failed", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("body-tri-path-inspection-denied", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static class TriCatalogReader
    {
        internal static BodySlideTriCatalog Read(byte[] bytes, Sha256Hash hash,
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            if (bytes.Length < 6 || !bytes.AsSpan(0, 4).SequenceEqual("PIRT"u8))
                throw new FormatException("TRI input is not a PIRT BodySlide file.");
            var reader = new Cursor(bytes[4..]);
            var shapes = new Dictionary<string, ShapeBuilder>(StringComparer.Ordinal);
            ReadSection(reader, shapes, BodySlideTriMorphType.Position, diagnostics);
            ReadSection(reader, shapes, BodySlideTriMorphType.Uv, diagnostics);
            if (reader.Remaining != 0)
                throw new FormatException("PIRT TRI contains trailing bytes after its two morph sections.");

            var result = shapes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new BodySlideTriShape(pair.Key, pair.Value.Morphs.Values
                    .OrderBy(morph => morph.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(morph => morph.Name, StringComparer.Ordinal)
                    .Select(morph => new BodySlideTriMorph(morph.Name, morph.Type, morph.Offsets))
                    .ToImmutableArray()))
                .ToImmutableArray();
            return new BodySlideTriCatalog(hash, result);
        }

        private static void ReadSection(Cursor reader, Dictionary<string, ShapeBuilder> shapes,
            BodySlideTriMorphType type, ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            var shapeCount = reader.ReadUInt16();
            for (var shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                var shapeName = reader.ReadAscii(reader.ReadByte(), "shape");
                if (!shapes.TryGetValue(shapeName, out var shape))
                {
                    shape = new ShapeBuilder();
                    shapes.Add(shapeName, shape);
                }
                var morphCount = reader.ReadUInt16();
                for (var morphIndex = 0; morphIndex < morphCount; morphIndex++)
                {
                    var morphName = reader.ReadAscii(reader.ReadByte(), "morph");
                    var multiplier = reader.ReadSingle();
                    if (!float.IsFinite(multiplier))
                        throw new FormatException($"PIRT {type} morph '{morphName}' has a non-finite multiplier.");
                    var vertexCount = reader.ReadUInt16();
                    var offsets = ImmutableArray.CreateBuilder<BodySlideTriOffset>(vertexCount);
                    for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
                    {
                        var index = reader.ReadUInt16();
                        var x = reader.ReadInt16() * multiplier;
                        var y = reader.ReadInt16() * multiplier;
                        var z = type == BodySlideTriMorphType.Position ? reader.ReadInt16() * multiplier : 0F;
                        if (x != 0F || y != 0F || z != 0F)
                            offsets.Add(new BodySlideTriOffset(index, x, y, z));
                    }
                    if (offsets.Count == 0) continue;
                    if (shape.Morphs.ContainsKey(morphName))
                    {
                        diagnostics.Add(new Diagnostic("body-tri-duplicate-morph", DiagnosticSeverity.Warning,
                            $"Duplicate PIRT morph '{shapeName}/{morphName}' uses the first occurrence."));
                        continue;
                    }
                    shape.Morphs.Add(morphName, new MorphBuilder(morphName, type, offsets.ToImmutable()));
                }
            }
        }

        private sealed class ShapeBuilder
        {
            internal Dictionary<string, MorphBuilder> Morphs { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed record MorphBuilder(string Name, BodySlideTriMorphType Type,
            ImmutableArray<BodySlideTriOffset> Offsets);

        private sealed class Cursor(byte[] bytes)
        {
            private readonly byte[] _bytes = bytes;
            private int _position;

            internal int Remaining => _bytes.Length - _position;

            internal byte ReadByte()
            {
                Ensure(1);
                return _bytes[_position++];
            }

            internal ushort ReadUInt16()
            {
                Ensure(2);
                var value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(_position, 2));
                _position += 2;
                return value;
            }

            internal short ReadInt16()
            {
                Ensure(2);
                var value = BinaryPrimitives.ReadInt16LittleEndian(_bytes.AsSpan(_position, 2));
                _position += 2;
                return value;
            }

            internal float ReadSingle()
            {
                Ensure(4);
                var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(_position, 4)));
                _position += 4;
                return value;
            }

            internal string ReadAscii(byte length, string kind)
            {
                if (length == 0) throw new FormatException($"PIRT {kind} name cannot be empty.");
                Ensure(length);
                var slice = _bytes.AsSpan(_position, length);
                for (var index = 0; index < slice.Length; index++)
                {
                    if (slice[index] > 0x7F)
                        throw new FormatException($"PIRT {kind} name is not ASCII.");
                }
                var value = Encoding.ASCII.GetString(slice);
                _position += length;
                return value;
            }

            private void Ensure(int count)
            {
                if (count < 0 || Remaining < count)
                    throw new FormatException("PIRT TRI ended before a declared field was complete.");
            }
        }
    }
}
