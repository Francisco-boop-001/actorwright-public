using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum FaceGenCorrectionKind
{
    GhoulHeadRear,
    EyebrowsFixedColor,
    MouthVanilla,
    SseNeutralDetail,
    SseOverlayFold
}

public static class FaceGenCorrectionKindExtensions
{
    public static string ToWireName(this FaceGenCorrectionKind kind) => kind switch
    {
        FaceGenCorrectionKind.GhoulHeadRear => "ghoul-head-rear",
        FaceGenCorrectionKind.EyebrowsFixedColor => "eyebrows-fixed-color",
        FaceGenCorrectionKind.MouthVanilla => "mouth-vanilla-fix",
        FaceGenCorrectionKind.SseNeutralDetail => "sse-neutral-detail",
        FaceGenCorrectionKind.SseOverlayFold => "sse-overlay-fold",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported FaceGen correction.")
    };

    public static bool TryParseWireName(string value, out FaceGenCorrectionKind kind)
    {
        kind = value.Trim().ToLowerInvariant() switch
        {
            "ghoul-head-rear" => FaceGenCorrectionKind.GhoulHeadRear,
            "eyebrows-fixed-color" => FaceGenCorrectionKind.EyebrowsFixedColor,
            "mouth-vanilla-fix" => FaceGenCorrectionKind.MouthVanilla,
            "sse-neutral-detail" => FaceGenCorrectionKind.SseNeutralDetail,
            "sse-overlay-fold" => FaceGenCorrectionKind.SseOverlayFold,
            _ => default
        };
        return value.Trim().ToLowerInvariant() is "ghoul-head-rear" or "eyebrows-fixed-color" or
            "mouth-vanilla-fix" or "sse-neutral-detail" or "sse-overlay-fold";
    }
}

/// <summary>Explicit trigger and semantic before/after values for one named correction.</summary>
public sealed record FaceGenCorrectionInput(
    FaceGenCorrectionKind Kind,
    bool Trigger,
    string Before,
    string After);

public sealed record FaceGenCorrectionDecision(
    FaceGenCorrectionKind Kind,
    bool Triggered,
    string Trigger,
    string Before,
    string After,
    string Decision);

public sealed record FaceGenCorrectionArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string InputManifestSha256,
    ImmutableArray<FaceGenCorrectionDecision> Corrections);

public sealed record FaceGenCorrectionRequest(
    GameEdition Edition,
    WorkspacePath ManifestPath,
    WorkspacePath OutputPath,
    FormId? NpcFormId,
    bool StrictShapes = true);

public sealed record FaceGenCorrectionResult(
    bool Written,
    FaceGenCorrectionArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenCorrectionService
{
    ValueTask<FaceGenCorrectionResult> BuildAsync(FaceGenCorrectionRequest request,
        CancellationToken cancellationToken);
}
