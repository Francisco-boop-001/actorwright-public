using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum BodySlideTriMorphType
{
    Position,
    Uv
}

public sealed record BodySlideTriOffset(int VertexIndex, float X, float Y, float Z);

public sealed record BodySlideTriMorph(
    string Name,
    BodySlideTriMorphType Type,
    ImmutableArray<BodySlideTriOffset> Offsets);

public sealed record BodySlideTriShape(
    string Name,
    ImmutableArray<BodySlideTriMorph> Morphs);

public sealed record BodySlideTriCatalog(
    Sha256Hash SourceHash,
    ImmutableArray<BodySlideTriShape> Shapes)
{
    public ImmutableArray<string> SliderNames => Shapes
        .SelectMany(shape => shape.Morphs.Select(morph => morph.Name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(name => name, StringComparer.Ordinal)
        .ToImmutableArray();
}

public sealed record BodySlideTriShapeSummary(
    string Name,
    ImmutableArray<string> MorphNames);

public sealed record BodySlideSliderValue(string Name, float Value);

public sealed record BodySlideSliderPresetRow(string Name, string Size, float Value);

public sealed record BodySlideSliderPresetDocument(
    Sha256Hash SourceHash,
    string PresetName,
    string SliderSet,
    ImmutableArray<string> Groups,
    ImmutableArray<BodySlideSliderPresetRow> Sliders);

public sealed record BodySlideSliderPresetInspectionRequest(
    GameEdition Edition,
    WorkspacePath PresetXml);

public sealed record BodySlideSliderPresetInspectionResult(
    GameEdition Edition,
    WorkspacePath PresetXml,
    Sha256Hash? SourceSha256,
    string? PresetName,
    string? SliderSet,
    ImmutableArray<string> Groups,
    ImmutableArray<BodySlideSliderPresetRow> Sliders,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IBodySlideSliderPresetInspectionService
{
    ValueTask<BodySlideSliderPresetInspectionResult> InspectAsync(
        BodySlideSliderPresetInspectionRequest request,
        CancellationToken cancellationToken);
}

public sealed record BodySlideResolvedChannel(
    string Shape,
    string Slider,
    BodySlideTriMorphType Type,
    float Weight,
    ImmutableArray<BodySlideTriOffset> Offsets);

public sealed record BodySlideResolutionRequest(
    GameEdition Edition,
    WorkspacePath TriPath,
    WorkspacePath PresetPath);

public sealed record BodySlideResolutionResult(
    GameEdition Edition,
    WorkspacePath TriPath,
    WorkspacePath PresetPath,
    Sha256Hash? TriSha256,
    ImmutableArray<BodySlideTriShapeSummary> Shapes,
    ImmutableArray<BodySlideSliderValue> Requested,
    ImmutableArray<BodySlideResolvedChannel> Channels,
    ImmutableArray<string> MissingSliders,
    ImmutableArray<string> ExcludedSliders,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IBodySlideResolutionService
{
    ValueTask<BodySlideResolutionResult> ResolveAsync(
        BodySlideResolutionRequest request, CancellationToken cancellationToken);
}

public sealed record BodySlideTriInspectionRequest(
    GameEdition Edition,
    WorkspacePath TriPath);

public sealed record BodySlideTriInspectionResult(
    GameEdition Edition,
    WorkspacePath TriPath,
    BodySlideTriCatalog? Catalog,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => Catalog is not null &&
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IBodySlideTriInspectionService
{
    ValueTask<BodySlideTriInspectionResult> InspectAsync(
        BodySlideTriInspectionRequest request,
        CancellationToken cancellationToken);
}
