using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.ReferencePreset.Tests;

internal static class MediaPipeNativeApiTests
{
    public static async Task TestInteropLayoutAndRuntimeAuthority()
    {
        MediaPipeNativeInteropLayout.ValidateWindowsX64();

        string projectRoot = FindProjectRoot();
        var labRoot = new WorkspacePath(
            @"K:\ExampleWorkspace");
        var runtimeRoot = new WorkspacePath(Path.Combine(
            projectRoot,
            "runtime",
            "reference-preset"));
        using var api = new MediaPipeNativeApi(labRoot, runtimeRoot);
        ReferencePresetRuntimeAdmissionResult admission =
            api.AdmitRuntime();
        Require(
            admission.Accepted &&
            admission.ManifestSha256 == HashFile(Path.Combine(
                runtimeRoot.Value,
                "runtime-asset-manifest.json")) &&
            admission.Diagnostics.IsEmpty,
            $"reference-runtime-admission:{Codes(admission.Diagnostics)}");
        ReferenceFaceNativeInferenceResult result = await api.InferAsync(
            new ReferenceFaceInferenceRequest(
                ValidDecodedImage(),
                Hash('a')),
            CancellationToken.None);

        RequireCode(
            result.Diagnostics,
            "reference-native-runtime-manifest-hash");
        Require(
            result.DetectedFaceCount == 0 &&
            result.Landmarks.IsEmpty &&
            result.TransformationMatrix.IsEmpty,
            "reference-native-runtime-mismatch-must-not-infer");
    }

    public static async Task TestAuthenticSofiaFergarContactSheetRefusal()
    {
        string projectRoot = FindProjectRoot();
        var labRoot = new WorkspacePath(
            @"K:\ExampleWorkspace");
        var runtimeRoot = new WorkspacePath(Path.Combine(
            projectRoot,
            "runtime",
            "reference-preset"));
        var referencePath = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "Resources",
            "Screenshot",
            "Sofia Fergar.png"));
        FileInfo referenceInfo = new(referencePath.Value);
        Require(referenceInfo.Exists,
            "reference-authentic-sofia-missing");

        var authority = new ReferenceImageAuthority(
            "sofia-reference-1",
            referencePath,
            HashFile(referencePath.Value),
            referenceInfo.Length,
            ReferenceImageViewRole.Front);
        var decoder = new SkiaReferenceImageDecoder(labRoot);
        ReferenceImageDecodeResult decoded = await decoder.DecodeAsync(
            new ReferenceImageDecodeRequest(
                authority,
                ReferencePresetAuthoringRules.MaximumDecodedBytes),
            CancellationToken.None);
        Require(
            decoded.Accepted,
            $"reference-authentic-sofia-decode:{Codes(decoded.Diagnostics)}");

        Sha256Hash runtimeManifestHash = HashFile(Path.Combine(
            runtimeRoot.Value,
            "runtime-asset-manifest.json"));
        using var nativeApi = new MediaPipeNativeApi(
            labRoot,
            runtimeRoot);
        var service =
            new MediaPipeFaceLandmarkInferenceService(nativeApi);
        ReferenceFaceInferenceResult result =
            await service.InferAsync(
                new ReferenceFaceInferenceRequest(
                    decoded.Image!,
                    runtimeManifestHash),
                CancellationToken.None);

        Require(
            !result.Accepted &&
            result.Inference is null &&
            result.Diagnostics.Any(item =>
                item.Code == "reference-face-multiple"),
            $"reference-authentic-sofia-contact-sheet-not-refused:{Codes(result.Diagnostics)}");
    }

    private static DecodedReferenceImage ValidDecodedImage() => new(
        "front",
        ReferenceImageViewRole.Front,
        new WorkspacePath(Path.Combine(
            @"K:\ExampleWorkspace",
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "reference-native-policy",
            "front.png")),
        Hash('b'),
        4,
        ReferenceImageFormat.Png,
        ReferenceImageOrientation.TopLeft,
        1,
        1,
        4,
        ImmutableArray.Create<byte>(255, 255, 255, 255),
        Hash('c'));

    private static string FindProjectRoot()
    {
        foreach (string start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            DirectoryInfo? current = new(start);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(
                        current.FullName, "Actorwright.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new InvalidOperationException(
            "reference-runtime-project-root-missing");
    }

    private static void RequireCode(
        ImmutableArray<Diagnostic> diagnostics,
        string code)
    {
        Require(
            diagnostics.Any(item =>
                string.Equals(
                    item.Code,
                    code,
                    StringComparison.Ordinal)),
            $"missing-diagnostic:{code}");
    }

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private static Sha256Hash HashFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(stream)));
    }

    private static string Codes(
        ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(
            " | ",
            diagnostics.Select(item =>
                $"{item.Code}:{item.Message}"));

    private static void Require(bool condition, string diagnostic)
    {
        if (!condition)
        {
            throw new InvalidOperationException(diagnostic);
        }
    }
}
