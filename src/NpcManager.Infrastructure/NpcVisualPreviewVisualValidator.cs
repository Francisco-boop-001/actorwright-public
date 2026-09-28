using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Applies the already-admitted reference-image decoder, native face
/// inference, and semantic anchor projection to the rendered front view.
/// This is a structural regression gate, not a likeness verdict.
/// </summary>
public sealed class NpcVisualPreviewVisualValidator(
    IReferenceImageDecoder decoder,
    IReferenceFaceInferenceService inferenceService,
    IReferenceSemanticLandmarkProjector projector,
    Sha256Hash runtimeManifestSha256)
    : INpcVisualPreviewVisualValidator
{
    private static readonly ImmutableHashSet<ReferenceSemanticAnchorKind>
        RequiredFeatureAnchors =
        [
            ReferenceSemanticAnchorKind.LeftEyeInner,
            ReferenceSemanticAnchorKind.LeftEyeOuter,
            ReferenceSemanticAnchorKind.LeftEyeUpper,
            ReferenceSemanticAnchorKind.LeftEyeLower,
            ReferenceSemanticAnchorKind.RightEyeInner,
            ReferenceSemanticAnchorKind.RightEyeOuter,
            ReferenceSemanticAnchorKind.RightEyeUpper,
            ReferenceSemanticAnchorKind.RightEyeLower,
            ReferenceSemanticAnchorKind.NoseBridge,
            ReferenceSemanticAnchorKind.NoseTip,
            ReferenceSemanticAnchorKind.LeftNoseWing,
            ReferenceSemanticAnchorKind.RightNoseWing,
            ReferenceSemanticAnchorKind.LeftMouthCorner,
            ReferenceSemanticAnchorKind.RightMouthCorner,
            ReferenceSemanticAnchorKind.LeftCupidPeak,
            ReferenceSemanticAnchorKind.RightCupidPeak,
            ReferenceSemanticAnchorKind.LowerLipCenter
        ];

    public async ValueTask<NpcVisualPreviewVisualEvidence> ValidateAsync(
        NpcVisualPreviewView faceFront,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(faceFront);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!File.Exists(faceFront.ImagePath.Value) ||
            Directory.Exists(faceFront.ImagePath.Value))
        {
            diagnostics.Add(Error(
                "npc-preview-face-image-missing",
                "The rendered front-face image is missing."));
            return Empty(diagnostics);
        }

        var info = new FileInfo(faceFront.ImagePath.Value);
        var authority = new ReferenceImageAuthority(
            "npc-preview-face-front",
            faceFront.ImagePath,
            faceFront.ImageSha256,
            info.Length,
            ReferenceImageViewRole.Front);
        ReferenceImageDecodeResult decoded =
            await decoder.DecodeAsync(
                new ReferenceImageDecodeRequest(
                    authority,
                    ReferencePresetAuthoringRules.MaximumDecodedBytes),
                cancellationToken).ConfigureAwait(false);
        return await ValidateDecodedAsync(
            decoded,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<NpcVisualPreviewVisualEvidence>
        ValidateEncodedAsync(
            NpcVisualPreviewView faceFront,
            ReadOnlyMemory<byte> encodedImage,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(faceFront);
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        if (decoder is not IReferenceImageBytesDecoder
            bytesDecoder)
        {
            diagnostics.Add(Error(
                "npc-preview-byte-decoder-unavailable",
                "The admitted visual validator cannot consume exact encoded image bytes."));
            return Empty(diagnostics);
        }
        var authority = new ReferenceImageAuthority(
            "npc-preview-face-front",
            faceFront.ImagePath,
            faceFront.ImageSha256,
            encodedImage.Length,
            ReferenceImageViewRole.Front);
        ReferenceImageDecodeResult decoded =
            await bytesDecoder.DecodeBytesAsync(
                new ReferenceImageDecodeRequest(
                    authority,
                    ReferencePresetAuthoringRules
                        .MaximumDecodedBytes),
                encodedImage,
                cancellationToken).ConfigureAwait(false);
        return await ValidateDecodedAsync(
            decoded,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NpcVisualPreviewVisualEvidence>
        ValidateDecodedAsync(
            ReferenceImageDecodeResult decoded,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        diagnostics.AddRange(decoded.Diagnostics);
        if (!decoded.Accepted || decoded.Image is null)
            return Empty(diagnostics);

        ReferenceFaceInferenceResult inferred =
            await inferenceService.InferAsync(
                new ReferenceFaceInferenceRequest(
                    decoded.Image,
                    runtimeManifestSha256),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(inferred.Diagnostics);
        if (!inferred.Accepted || inferred.Inference is null)
            return Empty(diagnostics);

        ReferenceSemanticLandmarkProjectionResult projected =
            await projector.ProjectAsync(
                new ReferenceSemanticLandmarkProjectionRequest(
                    inferred.Inference),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(projected.Diagnostics);
        bool boundedFeatures =
            projected.Anchors.Length == 31 &&
            RequiredFeatureAnchors.All(required =>
                projected.Anchors.Any(anchor =>
                    anchor.Anchor == required &&
                    anchor.AdvisoryVisible &&
                    double.IsFinite(anchor.X) &&
                    double.IsFinite(anchor.Y) &&
                    anchor.X is >= 0 and <= 1 &&
                    anchor.Y is >= 0 and <= 1));
        if (!boundedFeatures)
        {
            diagnostics.Add(Error(
                "npc-preview-face-features-unbounded",
                "The front render did not expose bounded eye, nose, and mouth anchors."));
        }

        return new NpcVisualPreviewVisualEvidence(
            1,
            inferred.Inference.DetectorScore,
            inferred.Inference.Landmarks.Length,
            projected.Anchors.Length,
            boundedFeatures,
            diagnostics.ToImmutable());
    }

    private static NpcVisualPreviewVisualEvidence Empty(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(0, 0, 0, 0, false, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
