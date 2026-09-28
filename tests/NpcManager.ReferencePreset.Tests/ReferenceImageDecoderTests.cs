using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using SkiaSharp;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferenceImageDecoderTests
{
    private static readonly Sha256Hash RuntimeHash = Hash('a');
    private static readonly Sha256Hash NativeHash = Hash('b');
    private static readonly Sha256Hash DetectorHash = Hash('c');
    private static readonly Sha256Hash LandmarkerHash = Hash('d');

    private static readonly (
        string Name,
        Func<byte[]> Bytes,
        ReferenceImageFormat Format)[] EncodedFixtures =
    [
        (
            "pixel.png",
            () => Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
            ReferenceImageFormat.Png),
        (
            "pixel.jpg",
            () => Convert.FromBase64String(
                "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EP/EABQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQIBAT8Q/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxB//9k="),
            ReferenceImageFormat.Jpeg),
        (
            "pixel.webp",
            CreateWebpFixture,
            ReferenceImageFormat.WebP)
    ];

    public static async Task TestSkiaFormatAdmission()
    {
        WorkspacePath root = NewFixtureRoot();
        var decoder = new SkiaReferenceImageDecoder(root);
        foreach ((string name, Func<byte[]> bytes, ReferenceImageFormat format)
                 in EncodedFixtures)
        {
            byte[] encoded = bytes();
            WorkspacePath path = WriteFixture(
                root,
                name,
                encoded);
            ReferenceImageAuthority authority = Authority(path, name);
            ReferenceImageDecodeRequest request = new(
                authority,
                ReferencePresetAuthoringRules.MaximumDecodedBytes);
            ReferenceImageDecodeResult first = await decoder.DecodeAsync(
                request,
                CancellationToken.None);
            ReferenceImageDecodeResult second = await decoder.DecodeAsync(
                request,
                CancellationToken.None);
            ReferenceImageDecodeResult fromExactBytes =
                await decoder.DecodeBytesAsync(
                    request,
                    encoded,
                    CancellationToken.None);
            Require(
                first.Accepted &&
                second.Accepted &&
                fromExactBytes.Accepted,
                $"reference-image-valid:{name}:{Codes(first.Diagnostics)}");
            Require(first.Image!.Format == format,
                $"reference-image-format:{name}");
            Require(first.Image.Width == 1 && first.Image.Height == 1,
                $"reference-image-dimensions:{name}");
            Require(first.Image.CanonicalRgba.SequenceEqual(
                    second.Image!.CanonicalRgba) &&
                first.Image.CanonicalRgba.SequenceEqual(
                    fromExactBytes.Image!.CanonicalRgba) &&
                first.Image.CanonicalRgbaSha256 ==
                    second.Image.CanonicalRgbaSha256 &&
                first.Image.CanonicalRgbaSha256 ==
                    fromExactBytes.Image.CanonicalRgbaSha256,
                $"reference-image-deterministic:{name}");
        }

        WorkspacePath mismatched = WriteFixture(
            root,
            "png-named-jpeg.jpg",
            EncodedFixtures[0].Bytes());
        ReferenceImageDecodeResult mismatch = await decoder.DecodeAsync(
            new ReferenceImageDecodeRequest(
                Authority(mismatched, "mismatch"),
                ReferencePresetAuthoringRules.MaximumDecodedBytes),
            CancellationToken.None);
        RequireCode(
            mismatch.Diagnostics,
            "reference-image-extension-format");

        WorkspacePath oversized = new(Path.Combine(
            root.Value,
            "oversized.png"));
        using (FileStream stream = new(
                   oversized.Value,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(
                ReferencePresetAuthoringRules.MaximumEncodedImageBytes + 1);
        }

        ReferenceImageDecodeResult tooLarge = await decoder.DecodeAsync(
            new ReferenceImageDecodeRequest(
                new ReferenceImageAuthority(
                    "oversized",
                    oversized,
                    HashFile(oversized.Value),
                    ReferencePresetAuthoringRules.MaximumEncodedImageBytes + 1,
                    ReferenceImageViewRole.Front),
                ReferencePresetAuthoringRules.MaximumDecodedBytes),
            CancellationToken.None);
        RequireCode(
            tooLarge.Diagnostics,
            "reference-image-encoded-bytes");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await RequireCancelledAsync(() => decoder.DecodeAsync(
            new ReferenceImageDecodeRequest(
                Authority(
                    new WorkspacePath(Path.Combine(
                        root.Value,
                        EncodedFixtures[0].Name)),
                    "cancelled"),
                ReferencePresetAuthoringRules.MaximumDecodedBytes),
            cancellation.Token));
    }

    public static async Task TestFaceCountAndConfidencePolicy()
    {
        DecodedReferenceImage image = ValidDecodedImage();
        foreach ((int faceCount, double score, bool accepted, string? code)
                 in new[]
                 {
                     (0, 0.0, false, "reference-face-none"),
                     (2, 0.95, false, "reference-face-multiple"),
                     (1, 0.49, false, "reference-face-score"),
                     (1, 0.50, true, "reference-face-low-confidence"),
                     (1, 0.75, true, "reference-face-low-confidence"),
                     (1, 0.76, true, null)
                 })
        {
            var service = new MediaPipeFaceLandmarkInferenceService(
                new FakeNativeApi(NativeResult(faceCount, score)));
            ReferenceFaceInferenceResult result = await service.InferAsync(
                new ReferenceFaceInferenceRequest(image, RuntimeHash),
                CancellationToken.None);
            Require(result.Accepted == accepted,
                $"reference-face-policy:{faceCount}:{score}:{Codes(result.Diagnostics)}");
            if (code is not null)
            {
                RequireCode(result.Diagnostics, code);
            }
        }

        var cancelledService = new MediaPipeFaceLandmarkInferenceService(
            new FakeNativeApi(NativeResult(1, 0.9)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await RequireCancelledAsync(() => cancelledService.InferAsync(
            new ReferenceFaceInferenceRequest(image, RuntimeHash),
            cancellation.Token));
    }

    private static ReferenceFaceNativeInferenceResult NativeResult(
        int faceCount,
        double score)
    {
        ImmutableArray<ReferenceFaceLandmark> landmarks =
            faceCount == 1
                ? Enumerable.Range(0, 478)
                    .Select(index => new ReferenceFaceLandmark(
                        index,
                        0.5,
                        0.5,
                        0.0,
                        1.0,
                        1.0))
                    .ToImmutableArray()
                : ImmutableArray<ReferenceFaceLandmark>.Empty;
        ImmutableArray<double> matrix = faceCount == 1
            ? Enumerable.Range(0, 16)
                .Select(index => index % 5 == 0 ? 1.0 : 0.0)
                .ToImmutableArray()
            : ImmutableArray<double>.Empty;
        return new ReferenceFaceNativeInferenceResult(
            faceCount,
            score,
            landmarks,
            matrix,
            0.0,
            NativeHash,
            DetectorHash,
            LandmarkerHash,
            ImmutableArray<Diagnostic>.Empty);
    }

    private static byte[] CreateWebpFixture()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            1,
            1,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        bitmap.SetPixel(0, 0, new SKColor(12, 34, 56, 255));
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(
            SKEncodedImageFormat.Webp,
            100);
        return data.ToArray();
    }

    private static DecodedReferenceImage ValidDecodedImage() => new(
        "front",
        ReferenceImageViewRole.Front,
        new WorkspacePath(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work\reference-native-policy\front.png"),
        Hash('e'),
        4,
        ReferenceImageFormat.Png,
        ReferenceImageOrientation.TopLeft,
        1,
        1,
        4,
        ImmutableArray.Create<byte>(255, 255, 255, 255),
        Hash('f'));

    private static WorkspacePath NewFixtureRoot()
    {
        string path = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work",
            "p12-009-reference-image-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new WorkspacePath(path);
    }

    private static WorkspacePath WriteFixture(
        WorkspacePath root,
        string name,
        byte[] bytes)
    {
        string path = Path.Combine(root.Value, name);
        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
        return new WorkspacePath(path);
    }

    private static ReferenceImageAuthority Authority(
        WorkspacePath path,
        string imageId)
    {
        FileInfo info = new(path.Value);
        return new ReferenceImageAuthority(
            imageId,
            path,
            HashFile(path.Value),
            info.Length,
            ReferenceImageViewRole.Front);
    }

    private static Sha256Hash HashFile(string path) =>
        new(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(path))));

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private static string Codes(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(',', diagnostics.Select(item => item.Code));

    private static void RequireCode(
        ImmutableArray<Diagnostic> diagnostics,
        string code)
    {
        Require(
            diagnostics.Any(item =>
                string.Equals(item.Code, code, StringComparison.Ordinal)),
            $"missing-diagnostic:{code}:{Codes(diagnostics)}");
    }

    private static async Task RequireCancelledAsync(
        Func<ValueTask<ReferenceImageDecodeResult>> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("expected-cancellation");
    }

    private static async Task RequireCancelledAsync(
        Func<ValueTask<ReferenceFaceInferenceResult>> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("expected-cancellation");
    }

    private static void Require(bool condition, string diagnostic)
    {
        if (!condition)
        {
            throw new InvalidOperationException(diagnostic);
        }
    }

    private sealed class FakeNativeApi(
        ReferenceFaceNativeInferenceResult result)
        : IReferenceFaceInferenceNativeApi
    {
        public ValueTask<ReferenceFaceNativeInferenceResult> InferAsync(
            ReferenceFaceInferenceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }
}
