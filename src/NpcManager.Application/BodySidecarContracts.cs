using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record BodySidecarInspectRequest(GameEdition Edition, WorkspacePath File);

public sealed record BodySidecarSculptPart(
    string Host,
    ImmutableArray<RaceMenuSculptVertex> Vertices);

public sealed record BodySidecarTintTexture(
    int Index,
    AssetPath Texture);

public sealed record BodySidecarNpcSummary(
    string Identifier,
    string? EditorId,
    ImmutableDictionary<string, float> BodyMorphs,
    ImmutableDictionary<string, ImmutableDictionary<string, float>> BodyMorphsKeyed,
    string? SkinTemplateId,
    string? Gender,
    int OverlayCount,
    int SseBodyOverlayCount,
    int SseNodeTransformCount,
    int SseSkinOverrideCount,
    int SseCustomMorphCount,
    int SseSculptVertexCount,
    int SseSculptPartCount,
    int SseTintTextureCount,
    ImmutableArray<SkyrimRaceMenuCustomMorphValue> SseCustomMorphs = default,
    ImmutableArray<RaceMenuSculptVertex> SseSculpt = default,
    ImmutableArray<BodySidecarSculptPart> SseSculptParts = default,
    ImmutableArray<BodySidecarTintTexture> SseTintTextures = default);

public sealed record BodySidecarDocumentSummary(
    int Version,
    PluginName Plugin,
    ImmutableArray<BodySidecarNpcSummary> Npcs,
    Sha256Hash SourceSha256,
    Sha256Hash CanonicalSha256,
    bool RoundTripPreserved);

public sealed record BodySidecarInspectResult(
    GameEdition Edition,
    WorkspacePath File,
    BodySidecarDocumentSummary? Document,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => Document is not null && !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IBodySidecarInspectionService
{
    ValueTask<BodySidecarInspectResult> InspectAsync(
        BodySidecarInspectRequest request, CancellationToken cancellationToken);
}
