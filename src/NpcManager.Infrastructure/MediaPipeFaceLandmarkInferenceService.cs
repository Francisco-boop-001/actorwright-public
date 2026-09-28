using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class MediaPipeFaceLandmarkInferenceService(
    IReferenceFaceInferenceNativeApi nativeApi)
    : IReferenceFaceInferenceService
{
    private readonly IReferenceFaceInferenceNativeApi _nativeApi =
        nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));

    public async ValueTask<ReferenceFaceInferenceResult> InferAsync(
        ReferenceFaceInferenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ReferenceFaceNativeInferenceResult native =
            await _nativeApi.InferAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = native.Diagnostics.ToBuilder();

        if (native.DetectedFaceCount == 0)
        {
            diagnostics.Add(Error(
                "reference-face-none",
                $"Reference image '{request.Image.ImageId}' contains no admitted face."));
        }
        else if (native.DetectedFaceCount != 1)
        {
            diagnostics.Add(Error(
                "reference-face-multiple",
                $"Reference image '{request.Image.ImageId}' contains {native.DetectedFaceCount} faces; exactly one is required."));
        }

        if (!double.IsFinite(native.DetectorScore) ||
            native.DetectorScore <
            ReferencePresetAuthoringRules.MinimumFaceScore ||
            native.DetectorScore > 1.0)
        {
            diagnostics.Add(Error(
                "reference-face-score",
                $"Reference image '{request.Image.ImageId}' has an inadmissible detector score."));
        }
        else if (native.DetectorScore <=
                 ReferencePresetAuthoringRules.LowConfidenceCeiling)
        {
            diagnostics.Add(new Diagnostic(
                "reference-face-low-confidence",
                DiagnosticSeverity.Warning,
                $"Reference image '{request.Image.ImageId}' requires confirmation or correction of every visible semantic anchor."));
        }

        if (native.DetectedFaceCount == 1 &&
            (native.Landmarks.IsDefault ||
             native.Landmarks.Length != 478))
        {
            diagnostics.Add(Error(
                "reference-face-landmark-count",
                $"Reference image '{request.Image.ImageId}' did not produce exactly 478 landmarks."));
        }

        if (native.DetectedFaceCount == 1 &&
            (native.TransformationMatrix.IsDefault ||
             native.TransformationMatrix.Length != 16))
        {
            diagnostics.Add(Error(
                "reference-face-transform-count",
                $"Reference image '{request.Image.ImageId}' did not produce one 4x4 transformation matrix."));
        }

        if (native.DetectedFaceCount == 1 &&
            (native.Landmarks.Any(landmark =>
                 landmark is null ||
                 !double.IsFinite(landmark.X) ||
                 !double.IsFinite(landmark.Y) ||
                 !double.IsFinite(landmark.Z)) ||
             native.TransformationMatrix.Any(value =>
                 !double.IsFinite(value)) ||
             !double.IsFinite(native.AdvisoryYawDegrees)))
        {
            diagnostics.Add(Error(
                "reference-face-nonfinite",
                $"Reference image '{request.Image.ImageId}' produced non-finite face evidence."));
        }

        if (diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            return new ReferenceFaceInferenceResult(
                null,
                diagnostics.ToImmutable());
        }

        var inference = new ReferenceImageInference(
            request.Image.ImageId,
            request.Image.ViewRole,
            request.Image.SourceSha256,
            request.Image.CanonicalRgbaSha256,
            request.Image.Width,
            request.Image.Height,
            native.DetectorScore,
            native.Landmarks,
            native.TransformationMatrix,
            native.AdvisoryYawDegrees,
            native.NativeLibrarySha256,
            native.DetectorModelSha256,
            native.LandmarkerModelSha256);
        return new ReferenceFaceInferenceResult(
            inference,
            diagnostics.ToImmutable());
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
