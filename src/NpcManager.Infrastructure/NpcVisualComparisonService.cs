using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using SkiaSharp;

namespace NpcManager.Infrastructure;

/// <summary>
/// Produces hash-bound, face-anchor-aligned review artifacts. Numeric
/// differences are regression evidence only and never a likeness verdict.
/// </summary>
public sealed class NpcVisualComparisonService(
    IReferenceImageDecoder decoder,
    IReferenceFaceInferenceService inferenceService,
    IReferenceSemanticLandmarkProjector projector,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    Sha256Hash runtimeManifestSha256)
    : INpcVisualComparisonService
{
    private const int MaximumDimension = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly string[] OutputNames =
    [
        "aligned-comparison.png",
        "side-by-side.png",
        "overlay.png",
        "heatmap.png",
        "blink-preview.png",
        "blink-comparison.png",
        "comparison-evidence.json"
    ];

    public async ValueTask<NpcVisualComparisonResult> CompareAsync(
        NpcVisualComparisonRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ValidateRequest(request).ToBuilder();
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        try
        {
            Sha256Hash previewHash = await HashFileAsync(
                request.PreviewImagePath,
                cancellationToken).ConfigureAwait(false);
            Sha256Hash comparisonHash = await HashFileAsync(
                request.ComparisonImagePath,
                cancellationToken).ConfigureAwait(false);
            if (previewHash != request.ExpectedPreviewImageSha256 ||
                comparisonHash !=
                request.ExpectedComparisonImageSha256)
            {
                diagnostics.Add(Error(
                    "npc-preview-comparison-input-hash",
                    "A comparison input does not match its reviewed SHA-256."));
                return Refused(diagnostics);
            }

            ComparisonImage preview =
                await DecodeAndProjectAsync(
                    "npc-preview-comparison-preview",
                    request.PreviewImagePath,
                    previewHash,
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(preview.Diagnostics);
            ComparisonImage comparison =
                await DecodeAndProjectAsync(
                    "npc-preview-comparison-authority",
                    request.ComparisonImagePath,
                    comparisonHash,
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(comparison.Diagnostics);
            if (HasErrors(diagnostics) ||
                preview.Image is null ||
                comparison.Image is null)
                return Refused(diagnostics);

            NpcVisualComparisonTransform transform =
                ComputeTransform(
                    preview.Image,
                    preview.Anchors,
                    comparison.Image,
                    comparison.Anchors);
            cancellationToken.ThrowIfCancellationRequested();
            using SKBitmap previewBitmap =
                ToBitmap(preview.Image);
            using SKBitmap comparisonBitmap =
                ToBitmap(comparison.Image);
            using SKBitmap aligned = Align(
                comparisonBitmap,
                previewBitmap.Width,
                previewBitmap.Height,
                transform);
            using SKBitmap sideBySide =
                DrawSideBySide(previewBitmap, aligned);
            using SKBitmap overlay =
                DrawOverlay(previewBitmap, aligned);
            using SKBitmap blinkPreview =
                CopyBitmap(previewBitmap);
            using SKBitmap blinkComparison =
                CopyBitmap(aligned);
            using SKBitmap heatmap = DrawHeatmap(
                previewBitmap,
                aligned,
                out double meanAbsoluteDifference);

            WorkspacePath alignedPath = Child(
                request.OutputRoot, OutputNames[0]);
            WorkspacePath sidePath = Child(
                request.OutputRoot, OutputNames[1]);
            WorkspacePath overlayPath = Child(
                request.OutputRoot, OutputNames[2]);
            WorkspacePath heatmapPath = Child(
                request.OutputRoot, OutputNames[3]);
            WorkspacePath blinkPreviewPath = Child(
                request.OutputRoot, OutputNames[4]);
            WorkspacePath blinkComparisonPath = Child(
                request.OutputRoot, OutputNames[5]);
            WorkspacePath evidencePath = Child(
                request.OutputRoot, OutputNames[6]);
            await WritePngNewAsync(
                aligned, alignedPath, cancellationToken);
            await WritePngNewAsync(
                sideBySide, sidePath, cancellationToken);
            await WritePngNewAsync(
                overlay, overlayPath, cancellationToken);
            await WritePngNewAsync(
                heatmap, heatmapPath, cancellationToken);
            await WritePngNewAsync(
                blinkPreview, blinkPreviewPath, cancellationToken);
            await WritePngNewAsync(
                blinkComparison,
                blinkComparisonPath,
                cancellationToken);

            Sha256Hash alignedHash = await HashFileAsync(
                alignedPath, cancellationToken);
            Sha256Hash sideHash = await HashFileAsync(
                sidePath, cancellationToken);
            Sha256Hash overlayHash = await HashFileAsync(
                overlayPath, cancellationToken);
            Sha256Hash heatmapHash = await HashFileAsync(
                heatmapPath, cancellationToken);
            Sha256Hash blinkPreviewHash = await HashFileAsync(
                blinkPreviewPath, cancellationToken);
            Sha256Hash blinkComparisonHash = await HashFileAsync(
                blinkComparisonPath, cancellationToken);
            object evidence = new
            {
                schemaVersion =
                    "npc-preview-comparison-evidence/1",
                label = request.ComparisonLabel,
                kind = request.Kind.ToString(),
                runtimeAuthority = false,
                humanVerdict = "unreviewed",
                numericSimilarityEstablishesLikeness = false,
                preview = new
                {
                    path = request.PreviewImagePath.Value,
                    sha256 = previewHash.Value,
                    width = preview.Image.Width,
                    height = preview.Image.Height,
                    anchors = preview.Anchors
                },
                comparison = new
                {
                    path = request.ComparisonImagePath.Value,
                    sha256 = comparisonHash.Value,
                    width = comparison.Image.Width,
                    height = comparison.Image.Height,
                    anchors = comparison.Anchors
                },
                transform,
                meanAbsoluteRgbDifference =
                    meanAbsoluteDifference,
                artifacts = new[]
                {
                    Artifact("aligned-comparison",
                        alignedPath, alignedHash),
                    Artifact("side-by-side", sidePath, sideHash),
                    Artifact("overlay", overlayPath, overlayHash),
                    Artifact("heatmap", heatmapPath, heatmapHash),
                    Artifact("blink-preview",
                        blinkPreviewPath, blinkPreviewHash),
                    Artifact("blink-comparison",
                        blinkComparisonPath,
                        blinkComparisonHash)
                },
                note =
                    "Alignment and numeric differences may identify regressions. Skyrim runtime and a human reviewer remain the visual authority."
            };
            await WriteBytesNewAsync(
                evidencePath,
                JsonSerializer.SerializeToUtf8Bytes(
                    evidence, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            Sha256Hash evidenceHash = await HashFileAsync(
                evidencePath, cancellationToken);

            if (await HashFileAsync(
                    request.PreviewImagePath,
                    cancellationToken) != previewHash ||
                await HashFileAsync(
                    request.ComparisonImagePath,
                    cancellationToken) != comparisonHash)
            {
                diagnostics.Add(Error(
                    "npc-preview-comparison-source-mutated",
                    "A source image changed while comparison evidence was produced."));
                return Refused(diagnostics);
            }

            return new NpcVisualComparisonResult(
                true,
                alignedPath,
                alignedHash,
                sidePath,
                sideHash,
                overlayPath,
                overlayHash,
                heatmapPath,
                heatmapHash,
                blinkPreviewPath,
                blinkPreviewHash,
                blinkComparisonPath,
                blinkComparisonHash,
                evidencePath,
                evidenceHash,
                transform,
                meanAbsoluteDifference,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                JsonException)
        {
            diagnostics.Add(Error(
                "npc-preview-comparison-failed",
                exception.Message));
            return Refused(diagnostics);
        }
    }

    private async ValueTask<ComparisonImage>
        DecodeAndProjectAsync(
            string imageId,
            WorkspacePath path,
            Sha256Hash hash,
            CancellationToken cancellationToken)
    {
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        var authority = new ReferenceImageAuthority(
            imageId,
            path,
            hash,
            new FileInfo(path.Value).Length,
            ReferenceImageViewRole.Front);
        ReferenceImageDecodeResult decoded =
            await decoder.DecodeAsync(
                new ReferenceImageDecodeRequest(
                    authority,
                    ReferencePresetAuthoringRules
                        .MaximumDecodedBytes),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(decoded.Diagnostics);
        if (!decoded.Accepted || decoded.Image is null)
            return new(null, [], diagnostics.ToImmutable());
        if (decoded.Image.Width > MaximumDimension ||
            decoded.Image.Height > MaximumDimension)
        {
            diagnostics.Add(Error(
                "npc-preview-comparison-dimensions",
                $"Comparison images may not exceed {MaximumDimension} pixels on either axis."));
            return new(null, [], diagnostics.ToImmutable());
        }
        ReferenceFaceInferenceResult inferred =
            await inferenceService.InferAsync(
                new ReferenceFaceInferenceRequest(
                    decoded.Image,
                    runtimeManifestSha256),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(inferred.Diagnostics);
        if (!inferred.Accepted || inferred.Inference is null)
            return new(null, [], diagnostics.ToImmutable());
        ReferenceSemanticLandmarkProjectionResult projected =
            await projector.ProjectAsync(
                new ReferenceSemanticLandmarkProjectionRequest(
                    inferred.Inference),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(projected.Diagnostics);
        if (projected.Anchors.Length != 31)
        {
            diagnostics.Add(Error(
                "npc-preview-comparison-anchors",
                "Both comparison images must expose the exact 31-anchor face projection."));
            return new(null, [], diagnostics.ToImmutable());
        }
        return new(
            decoded.Image,
            projected.Anchors,
            diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidateRequest(
        NpcVisualComparisonRequest request)
    {
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        foreach ((WorkspacePath path, string role) in
                 new[]
                 {
                     (request.PreviewImagePath, "preview image"),
                     (request.ComparisonImagePath,
                         "comparison image")
                 })
        {
            if (!path.IsUnder(labRoot) ||
                !File.Exists(path.Value) ||
                Directory.Exists(path.Value))
                diagnostics.Add(Error(
                    "npc-preview-comparison-input",
                    $"The {role} must be an existing K-local file."));
            else
            {
                diagnostics.AddRange(
                    policy.EvaluateReadRoot(labRoot, path));
                AddReparseDiagnostic(
                    path.Value, role, diagnostics);
            }
        }
        if (!request.OutputRoot.IsUnder(labRoot) ||
            !Directory.Exists(request.OutputRoot.Value))
            diagnostics.Add(Error(
                "npc-preview-comparison-output-root",
                "Comparison output requires a new existing K-local directory."));
        else
        {
            diagnostics.AddRange(
                policy.Evaluate(labRoot, request.OutputRoot));
            AddReparseDiagnostic(
                request.OutputRoot.Value,
                "comparison output root",
                diagnostics);
            if (OutputNames.Any(name =>
                    File.Exists(Path.Combine(
                        request.OutputRoot.Value, name)) ||
                    Directory.Exists(Path.Combine(
                        request.OutputRoot.Value, name))))
                diagnostics.Add(Error(
                    "npc-preview-comparison-output-exists",
                    "Comparison artifacts never overwrite existing paths."));
        }
        if (string.IsNullOrWhiteSpace(
                request.ComparisonLabel) ||
            request.ComparisonLabel.Length > 128)
            diagnostics.Add(Error(
                "npc-preview-comparison-label",
                "The comparison label must contain 1-128 characters."));
        return diagnostics.ToImmutable();
    }

    private static NpcVisualComparisonTransform ComputeTransform(
        DecodedReferenceImage preview,
        ImmutableArray<ReferenceSemanticAnchorProposal>
            previewAnchors,
        DecodedReferenceImage comparison,
        ImmutableArray<ReferenceSemanticAnchorProposal>
            comparisonAnchors)
    {
        (double X, double Y) previewLeft = EyeCenter(
            previewAnchors,
            ReferenceSemanticAnchorKind.LeftEyeInner,
            ReferenceSemanticAnchorKind.LeftEyeOuter,
            preview.Width,
            preview.Height);
        (double X, double Y) previewRight = EyeCenter(
            previewAnchors,
            ReferenceSemanticAnchorKind.RightEyeInner,
            ReferenceSemanticAnchorKind.RightEyeOuter,
            preview.Width,
            preview.Height);
        (double X, double Y) comparisonLeft = EyeCenter(
            comparisonAnchors,
            ReferenceSemanticAnchorKind.LeftEyeInner,
            ReferenceSemanticAnchorKind.LeftEyeOuter,
            comparison.Width,
            comparison.Height);
        (double X, double Y) comparisonRight = EyeCenter(
            comparisonAnchors,
            ReferenceSemanticAnchorKind.RightEyeInner,
            ReferenceSemanticAnchorKind.RightEyeOuter,
            comparison.Width,
            comparison.Height);
        double previewDx = previewRight.X - previewLeft.X;
        double previewDy = previewRight.Y - previewLeft.Y;
        double comparisonDx =
            comparisonRight.X - comparisonLeft.X;
        double comparisonDy =
            comparisonRight.Y - comparisonLeft.Y;
        double comparisonDistance = Math.Sqrt(
            comparisonDx * comparisonDx +
            comparisonDy * comparisonDy);
        double previewDistance = Math.Sqrt(
            previewDx * previewDx +
            previewDy * previewDy);
        if (comparisonDistance <= 1e-6 ||
            previewDistance <= 1e-6)
            throw new InvalidDataException(
                "Eye anchors cannot define a stable comparison transform.");
        double scale = previewDistance /
                       comparisonDistance;
        double rotation =
            Math.Atan2(previewDy, previewDx) -
            Math.Atan2(comparisonDy, comparisonDx);
        double cosine = Math.Cos(rotation);
        double sine = Math.Sin(rotation);
        double comparisonCenterX =
            (comparisonLeft.X + comparisonRight.X) / 2;
        double comparisonCenterY =
            (comparisonLeft.Y + comparisonRight.Y) / 2;
        double previewCenterX =
            (previewLeft.X + previewRight.X) / 2;
        double previewCenterY =
            (previewLeft.Y + previewRight.Y) / 2;
        double translationX =
            previewCenterX -
            scale * (cosine * comparisonCenterX -
                     sine * comparisonCenterY);
        double translationY =
            previewCenterY -
            scale * (sine * comparisonCenterX +
                     cosine * comparisonCenterY);
        return new(
            scale,
            rotation,
            translationX,
            translationY);
    }

    private static (double X, double Y) EyeCenter(
        ImmutableArray<ReferenceSemanticAnchorProposal> anchors,
        ReferenceSemanticAnchorKind inner,
        ReferenceSemanticAnchorKind outer,
        int width,
        int height)
    {
        ReferenceSemanticAnchorProposal left =
            anchors.Single(item => item.Anchor == inner);
        ReferenceSemanticAnchorProposal right =
            anchors.Single(item => item.Anchor == outer);
        return (
            (left.X + right.X) * width / 2,
            (left.Y + right.Y) * height / 2);
    }

    private static SKBitmap ToBitmap(
        DecodedReferenceImage image)
    {
        var bitmap = new SKBitmap(
            image.Width,
            image.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);
        byte[] bytes = image.CanonicalRgba.ToArray();
        Marshal.Copy(
            bytes,
            0,
            bitmap.GetPixels(),
            bytes.Length);
        return bitmap;
    }

    private static SKBitmap Align(
        SKBitmap comparison,
        int width,
        int height,
        NpcVisualComparisonTransform transform)
    {
        var target = new SKBitmap(
            width,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        float cosine = (float)Math.Cos(
            transform.RotationRadians);
        float sine = (float)Math.Sin(
            transform.RotationRadians);
        float scale = (float)transform.Scale;
        var matrix = new SKMatrix
        {
            ScaleX = scale * cosine,
            SkewX = -scale * sine,
            TransX = (float)transform.TranslationX,
            SkewY = scale * sine,
            ScaleY = scale * cosine,
            TransY = (float)transform.TranslationY,
            Persp2 = 1
        };
        canvas.SetMatrix(matrix);
        using var paint = new SKPaint
        {
            IsAntialias = true
        };
        canvas.DrawBitmap(comparison, 0, 0, paint);
        canvas.Flush();
        return target;
    }

    private static SKBitmap DrawSideBySide(
        SKBitmap preview,
        SKBitmap aligned)
    {
        var result = new SKBitmap(
            preview.Width * 2,
            preview.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(new SKColor(24, 27, 31));
        canvas.DrawBitmap(preview, 0, 0);
        canvas.DrawBitmap(aligned, preview.Width, 0);
        canvas.Flush();
        return result;
    }

    private static SKBitmap DrawOverlay(
        SKBitmap preview,
        SKBitmap aligned)
    {
        SKBitmap result = CopyBitmap(preview);
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(128)
        };
        canvas.DrawBitmap(aligned, 0, 0, paint);
        canvas.Flush();
        return result;
    }

    private static SKBitmap DrawHeatmap(
        SKBitmap preview,
        SKBitmap aligned,
        out double meanAbsoluteDifference)
    {
        var result = new SKBitmap(
            preview.Width,
            preview.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        long total = 0;
        long count = 0;
        for (int y = 0; y < preview.Height; y++)
        {
            for (int x = 0; x < preview.Width; x++)
            {
                SKColor left = preview.GetPixel(x, y);
                SKColor right = aligned.GetPixel(x, y);
                if (right.Alpha == 0)
                {
                    result.SetPixel(
                        x, y, new SKColor(0, 0, 0, 255));
                    continue;
                }
                int red = Math.Abs(left.Red - right.Red);
                int green =
                    Math.Abs(left.Green - right.Green);
                int blue = Math.Abs(left.Blue - right.Blue);
                int difference = (red + green + blue) / 3;
                total += red + green + blue;
                count += 3;
                result.SetPixel(
                    x,
                    y,
                    new SKColor(
                        (byte)difference,
                        (byte)Math.Min(255, difference * 2),
                        0,
                        255));
            }
        }
        meanAbsoluteDifference =
            count == 0 ? 0 : (double)total / count;
        return result;
    }

    private static SKBitmap CopyBitmap(SKBitmap source)
    {
        var result = new SKBitmap(
            source.Width,
            source.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, 0, 0);
        canvas.Flush();
        return result;
    }

    private static async ValueTask WritePngNewAsync(
        SKBitmap bitmap,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data =
            image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException(
                "Skia could not encode a comparison image.");
        await using FileStream stream = new(
            destination.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        byte[] bytes = data.ToArray();
        await stream.WriteAsync(
            bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask WriteBytesNewAsync(
        WorkspacePath destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            destination.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        await stream.WriteAsync(
            bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(
                stream, cancellationToken)));
    }

    private static object Artifact(
        string role,
        WorkspacePath path,
        Sha256Hash hash) =>
        new
        {
            role,
            path = path.Value,
            sha256 = hash.Value,
            bytes = new FileInfo(path.Value).Length
        };

    private static WorkspacePath Child(
        WorkspacePath root, string name) =>
        new(Path.Combine(root.Value, name));

    private static void AddReparseDiagnostic(
        string path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (info.Exists &&
                (info.Attributes &
                 FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add(Error(
                    "npc-preview-comparison-reparse",
                    $"The {role} crosses reparse path '{current}'."));
                return;
            }
            string? parent = Path.GetDirectoryName(current);
            if (string.Equals(
                    parent,
                    current,
                    StringComparison.OrdinalIgnoreCase))
                return;
            current = parent ?? "";
        }
    }

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static NpcVisualComparisonResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            false,
            null, null,
            null, null,
            null, null,
            null, null,
            null, null,
            null, null,
            null, null,
            null,
            0,
            diagnostics.ToImmutable());

    private sealed record ComparisonImage(
        DecodedReferenceImage? Image,
        ImmutableArray<ReferenceSemanticAnchorProposal> Anchors,
        ImmutableArray<Diagnostic> Diagnostics);
}
