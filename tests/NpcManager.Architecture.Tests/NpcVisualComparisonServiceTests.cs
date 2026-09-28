using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Presets;
using SkiaSharp;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestNpcVisualComparisonService()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"npc-visual-comparison-test-{Guid.NewGuid():N}");
        string preview = Path.Combine(root, "preview.png");
        string comparison = Path.Combine(root, "comparison.png");
        string output = Path.Combine(root, "comparison-output");
        Directory.CreateDirectory(output);
        try
        {
            WriteNpcComparisonImage(
                preview,
                new SKColor(58, 72, 91),
                new SKColor(215, 174, 151));
            WriteNpcComparisonImage(
                comparison,
                new SKColor(72, 55, 47),
                new SKColor(203, 159, 137));
            var service = new NpcVisualComparisonService(
                new SkiaReferenceImageDecoder(labRoot),
                new ComparisonInferenceService(),
                new ComparisonProjector(),
                new KOnlyWorkspacePolicy(
                    labRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                labRoot,
                new Sha256Hash(new string('A', 64)));
            var request = new NpcVisualComparisonRequest(
                new WorkspacePath(preview),
                HashNpcPreviewTestFile(preview),
                new WorkspacePath(comparison),
                HashNpcPreviewTestFile(comparison),
                "fixture runtime",
                NpcVisualComparisonKind.SkyrimRuntime,
                new WorkspacePath(output));
            NpcVisualComparisonResult result =
                await service.CompareAsync(
                    request,
                    CancellationToken.None);

            Assert(result.Produced &&
                   result.Transform is not null &&
                   result.MeanAbsoluteRgbDifference > 0 &&
                   new[]
                   {
                       result.AlignedComparisonPath,
                       result.SideBySidePath,
                       result.OverlayPath,
                       result.HeatmapPath,
                       result.BlinkPreviewPath,
                       result.BlinkComparisonPath,
                       result.EvidencePath
                   }.All(path =>
                       path is not null &&
                       File.Exists(path.Value.Value)),
                "The comparison service did not produce the complete aligned evidence set.");
            Assert(result.Diagnostics.All(item =>
                       item.Severity != DiagnosticSeverity.Error) &&
                   File.ReadAllText(
                           result.EvidencePath!.Value.Value)
                       .Contains(
                           "\"humanVerdict\": \"unreviewed\"",
                           StringComparison.Ordinal) &&
                   File.ReadAllText(
                           result.EvidencePath.Value.Value)
                       .Contains(
                           "\"numericSimilarityEstablishesLikeness\": false",
                           StringComparison.Ordinal),
                "Comparison evidence attempted to issue a visual verdict.");

            NpcVisualComparisonResult duplicate =
                await service.CompareAsync(
                    request,
                    CancellationToken.None);
            Assert(!duplicate.Produced &&
                   duplicate.Diagnostics.Any(item =>
                       item.Code ==
                       "npc-preview-comparison-output-exists"),
                "Comparison outputs were overwritten.");

            File.AppendAllText(preview, "tamper");
            string secondOutput = Path.Combine(root, "tamper-output");
            Directory.CreateDirectory(secondOutput);
            NpcVisualComparisonResult tampered =
                await service.CompareAsync(
                    request with
                    {
                        OutputRoot =
                            new WorkspacePath(secondOutput)
                    },
                    CancellationToken.None);
            Assert(!tampered.Produced &&
                   tampered.Diagnostics.Any(item =>
                       item.Code ==
                       "npc-preview-comparison-input-hash"),
                "A changed comparison input was accepted.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteNpcComparisonImage(
        string path,
        SKColor background,
        SKColor face)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(
            256,
            256,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);
        using var paint = new SKPaint
        {
            Color = face,
            IsAntialias = true
        };
        canvas.DrawOval(
            new SKRect(58, 24, 198, 226),
            paint);
        paint.Color = SKColors.Black;
        canvas.DrawCircle(102, 105, 8, paint);
        canvas.DrawCircle(154, 105, 8, paint);
        canvas.DrawOval(
            new SKRect(105, 168, 151, 178),
            paint);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data =
            image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException(
                "Skia could not encode the comparison fixture.");
        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        data.SaveTo(stream);
    }

    private static async Task<string>
        TestAuthenticNpcVisualComparison(
            string previewPath,
            string comparisonPath,
            string kind)
    {
        WorkspacePath labRoot =
            new("K:\\ExampleWorkspace");
        Assert(File.Exists(previewPath),
            "The authentic preview image does not exist.");
        Assert(File.Exists(comparisonPath),
            "The authentic comparison image does not exist.");
        NpcVisualComparisonKind comparisonKind =
            kind.ToLowerInvariant() switch
            {
                "runtime" =>
                    NpcVisualComparisonKind.SkyrimRuntime,
                "reference" =>
                    NpcVisualComparisonKind.Reference,
                _ => throw new ArgumentException(
                    "Comparison kind must be runtime or reference.",
                    nameof(kind))
            };
        string projectRoot = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation");
        string output = Path.Combine(
            projectRoot,
            "03-builds",
            "work",
            $"npc-visual-authentic-comparison-{kind.ToLowerInvariant()}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);

        WorkspacePath runtimeRoot = new(Path.Combine(
            projectRoot,
            "src",
            "NpcManager.Desktop",
            "bin",
            "Release",
            "net10.0-windows",
            "runtime",
            "reference-preset"));
        using var native =
            new MediaPipeNativeApi(labRoot, runtimeRoot);
        ReferencePresetRuntimeAdmissionResult admission =
            native.AdmitRuntime();
        Assert(admission.Accepted &&
               admission.ManifestSha256 is not null,
            "The admitted reference runtime was unavailable: " +
            string.Join(
                "; ",
                admission.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        Sha256Hash admittedManifestHash =
            admission.ManifestSha256
            ?? throw new InvalidDataException(
                "The admitted runtime did not report a manifest hash.");
        var service = new NpcVisualComparisonService(
            new SkiaReferenceImageDecoder(labRoot),
            new MediaPipeFaceLandmarkInferenceService(native),
            new ReferenceSemanticLandmarkProjector(),
            new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame")),
            labRoot,
            admittedManifestHash);
        var request = new NpcVisualComparisonRequest(
            new WorkspacePath(previewPath),
            HashNpcPreviewTestFile(previewPath),
            new WorkspacePath(comparisonPath),
            HashNpcPreviewTestFile(comparisonPath),
            Path.GetFileName(comparisonPath),
            comparisonKind,
            new WorkspacePath(output));
        NpcVisualComparisonResult result =
            await service.CompareAsync(
                request,
                CancellationToken.None);
        Assert(result.Produced &&
               result.Transform is not null &&
               result.EvidencePath is not null &&
               File.Exists(result.EvidencePath.Value.Value) &&
               !result.Diagnostics.Any(item =>
                   item.Severity == DiagnosticSeverity.Error),
            "Authentic comparison evidence failed: " +
            string.Join(
                "; ",
                result.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        WorkspacePath evidencePath =
            result.EvidencePath
            ?? throw new InvalidDataException(
                "Authentic comparison evidence did not report its JSON path.");
        string evidence =
            File.ReadAllText(evidencePath.Value);
        Assert(
            evidence.Contains(
                "\"humanVerdict\": \"unreviewed\"",
                StringComparison.Ordinal) &&
            evidence.Contains(
                "\"numericSimilarityEstablishesLikeness\": false",
                StringComparison.Ordinal),
            "Authentic comparison evidence issued a visual verdict.");
        return output;
    }

    private sealed class ComparisonInferenceService :
        IReferenceFaceInferenceService
    {
        public ValueTask<ReferenceFaceInferenceResult> InferAsync(
            ReferenceFaceInferenceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<ReferenceFaceLandmark> landmarks =
                Enumerable.Range(0, 478)
                    .Select(index =>
                        new ReferenceFaceLandmark(
                            index,
                            0.5,
                            0.5,
                            0,
                            1,
                            1))
                    .ToImmutableArray();
            return ValueTask.FromResult(
                new ReferenceFaceInferenceResult(
                    new ReferenceImageInference(
                        request.Image.ImageId,
                        request.Image.ViewRole,
                        request.Image.SourceSha256,
                        request.Image.CanonicalRgbaSha256,
                        request.Image.Width,
                        request.Image.Height,
                        0.99,
                        landmarks,
                        [
                            1, 0, 0, 0,
                            0, 1, 0, 0,
                            0, 0, 1, 0,
                            0, 0, 0, 1
                        ],
                        0,
                        new Sha256Hash(new string('B', 64)),
                        new Sha256Hash(new string('C', 64)),
                        new Sha256Hash(new string('D', 64))),
                    []));
        }
    }

    private sealed class ComparisonProjector :
        IReferenceSemanticLandmarkProjector
    {
        public ValueTask<ReferenceSemanticLandmarkProjectionResult>
            ProjectAsync(
                ReferenceSemanticLandmarkProjectionRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool preview = request.Inference.ImageId.Contains(
                "preview",
                StringComparison.Ordinal);
            double shift = preview ? 0.05 : 0;
            ImmutableArray<ReferenceSemanticAnchorProposal> anchors =
                Enum.GetValues<ReferenceSemanticAnchorKind>()
                    .Select((kind, index) =>
                    {
                        double x =
                            kind.ToWireName().Contains(
                                "left",
                                StringComparison.Ordinal)
                                ? 0.35 + shift
                                : kind.ToWireName().Contains(
                                    "right",
                                    StringComparison.Ordinal)
                                    ? 0.65 + shift
                                    : 0.5 + shift;
                        return new ReferenceSemanticAnchorProposal(
                            kind,
                            index,
                            x,
                            0.4 + shift,
                            0.99,
                            true,
                            false);
                    })
                    .ToImmutableArray();
            return ValueTask.FromResult(
                new ReferenceSemanticLandmarkProjectionResult(
                    anchors,
                    []));
        }
    }
}
