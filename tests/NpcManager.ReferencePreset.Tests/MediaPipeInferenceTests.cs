using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.ReferencePreset.Tests;

internal static class MediaPipeInferenceTests
{
    private const string ExistingOutputDiagnostic =
        "reference-native-evidence-output-exists";

    private static readonly JsonSerializerOptions EvidenceJsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

    private static readonly (
        string Name,
        ReferenceImageViewRole Role)[] Views =
    [
        ("ref-front-1024.png", ReferenceImageViewRole.Front),
        (
            "ref-left-1024.png",
            ReferenceImageViewRole.LeftThreeQuarter),
        (
            "ref-right-1024.png",
            ReferenceImageViewRole.RightThreeQuarter)
    ];

    public static async Task TestElviraNativeDeterminism()
    {
        NativeInferenceEvidence evidence =
            await CollectElviraEvidenceAsync();
        Require(
            evidence.Views.Length == Views.Length &&
            evidence.Views.All(view => view.ExactHashEquality),
            "reference-real-view-evidence-set");
    }

    public static async Task TestEvidenceNoOverwrite()
    {
        string projectRoot = FindProjectRoot();
        string workRoot = Path.Combine(
            projectRoot,
            "03-builds",
            "work",
            "reference-preset-tests",
            $"native-evidence-no-overwrite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);
        string outputPath = Path.Combine(
            workRoot,
            "existing.json");
        byte[] sentinel = [0x53, 0x41, 0x46, 0x45];
        await File.WriteAllBytesAsync(outputPath, sentinel);
        try
        {
            try
            {
                await WriteElviraEvidenceAsync(outputPath);
                throw new InvalidOperationException(
                    "reference-native-evidence-overwrite-accepted");
            }
            catch (IOException exception)
            {
                Require(
                    exception.Message.Contains(
                        ExistingOutputDiagnostic,
                        StringComparison.Ordinal),
                    "reference-native-evidence-overwrite-diagnostic");
            }

            Require(
                File.ReadAllBytes(outputPath).SequenceEqual(sentinel),
                "reference-native-evidence-overwrite-mutated");
        }
        finally
        {
            string expectedParent = Path.Combine(
                projectRoot,
                "03-builds",
                "work",
                "reference-preset-tests");
            Require(
                Path.GetFullPath(workRoot).StartsWith(
                    Path.GetFullPath(expectedParent) +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase),
                "reference-native-evidence-cleanup-boundary");
            Directory.Delete(workRoot, recursive: true);
        }
    }

    public static async Task WriteElviraEvidenceAsync(
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string projectRoot = FindProjectRoot();
        string labRoot = FindLabRoot(projectRoot);
        string normalizedOutput = Path.GetFullPath(outputPath);
        string normalizedLabPrefix =
            Path.GetFullPath(labRoot)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!normalizedOutput.StartsWith(
                normalizedLabPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "reference-native-evidence-outside-lab");
        }

        if (File.Exists(normalizedOutput) ||
            Directory.Exists(normalizedOutput))
        {
            throw new IOException(ExistingOutputDiagnostic);
        }

        NativeInferenceEvidence evidence =
            await CollectElviraEvidenceAsync();
        string? parent = Path.GetDirectoryName(normalizedOutput);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException(
                "reference-native-evidence-parent-missing");
        }

        Directory.CreateDirectory(parent);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            evidence,
            EvidenceJsonOptions);
        await using (var stream = new FileStream(
                         normalizedOutput,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 65536,
                         options: FileOptions.Asynchronous |
                                  FileOptions.WriteThrough))
        {
            await stream.WriteAsync(json);
            await stream.FlushAsync();
        }

        byte[] readback = await File.ReadAllBytesAsync(
            normalizedOutput);
        using JsonDocument document = JsonDocument.Parse(readback);
        Require(
            document.RootElement.GetProperty("outcome")
                .GetString() == "PASS" &&
            SHA256.HashData(readback)
                .SequenceEqual(SHA256.HashData(json)),
            "reference-native-evidence-readback");
    }

    private static async Task<NativeInferenceEvidence>
        CollectElviraEvidenceAsync()
    {
        string projectRoot = FindProjectRoot();
        var labRoot = new WorkspacePath(
            FindLabRoot(projectRoot));
        var runtimeRoot = new WorkspacePath(Path.Combine(
            projectRoot,
            "runtime",
            "reference-preset"));
        Sha256Hash runtimeManifestHash = HashFile(Path.Combine(
            runtimeRoot.Value,
            "runtime-asset-manifest.json"));
        var decoder = new SkiaReferenceImageDecoder(labRoot);
        using var nativeApi = new MediaPipeNativeApi(
            labRoot,
            runtimeRoot);
        var service =
            new MediaPipeFaceLandmarkInferenceService(nativeApi);
        var evidence =
            ImmutableArray.CreateBuilder<NativeViewEvidence>(
                Views.Length);

        foreach ((string name, ReferenceImageViewRole role) in Views)
        {
            var path = new WorkspacePath(Path.Combine(
                labRoot.Value,
                "projects",
                "ElviraFromPreset",
                "sources",
                name));
            FileInfo info = new(path.Value);
            Require(info.Exists, $"reference-real-view-missing:{name}");
            var authority = new ReferenceImageAuthority(
                Path.GetFileNameWithoutExtension(name),
                path,
                HashFile(path.Value),
                info.Length,
                role);
            ReferenceImageDecodeResult decoded =
                await decoder.DecodeAsync(
                    new ReferenceImageDecodeRequest(
                        authority,
                        ReferencePresetAuthoringRules.MaximumDecodedBytes),
                    CancellationToken.None);
            Require(
                decoded.Accepted,
                $"reference-real-view-decode:{name}:{Codes(decoded.Diagnostics)}");

            ReferenceFaceInferenceResult first =
                await service.InferAsync(
                    new ReferenceFaceInferenceRequest(
                        decoded.Image!,
                        runtimeManifestHash),
                    CancellationToken.None);
            ReferenceFaceInferenceResult second =
                await service.InferAsync(
                    new ReferenceFaceInferenceRequest(
                        decoded.Image!,
                        runtimeManifestHash),
                    CancellationToken.None);
            Require(
                first.Accepted && second.Accepted,
                $"reference-real-view-inference:{name}:{Codes(first.Diagnostics)}:{Codes(second.Diagnostics)}");
            Require(
                first.Inference!.Landmarks.Length == 478 &&
                first.Inference.TransformationMatrix.Length == 16 &&
                first.Inference.Landmarks.All(IsFinite) &&
                first.Inference.TransformationMatrix.All(double.IsFinite),
                $"reference-real-view-shape:{name}");
            Require(
                CanonicalHash(first.Inference) ==
                CanonicalHash(second.Inference!),
                $"reference-real-view-determinism:{name}");
            Sha256Hash firstHash = CanonicalHash(first.Inference);
            Sha256Hash secondHash = CanonicalHash(
                second.Inference!);
            evidence.Add(new NativeViewEvidence(
                name,
                role.ToString(),
                path.Value,
                info.Length,
                authority.SourceSha256.Value,
                decoded.Image!.CanonicalRgbaSha256.Value,
                decoded.Image.Width,
                decoded.Image.Height,
                first.Inference.DetectorScore,
                first.Inference.Landmarks.Length,
                first.Inference.TransformationMatrix.Length,
                firstHash.Value,
                secondHash.Value,
                firstHash == secondHash));
        }

        ImmutableArray<NativeViewEvidence> views =
            evidence.MoveToImmutable();
        string canonicalSet = string.Join(
            '\n',
            views.Select(view =>
                $"{view.Name}|{view.FirstInferenceSha256}"));
        return new NativeInferenceEvidence(
            1,
            "npc-manager-native-inference",
            DateTimeOffset.UtcNow,
            runtimeManifestHash.Value,
            views,
            Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonicalSet))),
            "PASS");
    }

    private static bool IsFinite(ReferenceFaceLandmark landmark) =>
        double.IsFinite(landmark.X) &&
        double.IsFinite(landmark.Y) &&
        double.IsFinite(landmark.Z) &&
        (!landmark.Presence.HasValue ||
         double.IsFinite(landmark.Presence.Value)) &&
        (!landmark.Visibility.HasValue ||
         double.IsFinite(landmark.Visibility.Value));

    private static Sha256Hash CanonicalHash(
        ReferenceImageInference inference) =>
        new(Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(inference))));

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

    private static string FindLabRoot(string projectRoot)
    {
        DirectoryInfo? current = new(projectRoot);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "AGENTS.md")) &&
                File.Exists(Path.Combine(
                    current.FullName,
                    "WORKSPACE_MANIFEST.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "reference-runtime-lab-root-missing");
    }

    private static string Codes(
        ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(
            ',',
            diagnostics.Select(item => item.Code));

    private static void Require(bool condition, string diagnostic)
    {
        if (!condition)
        {
            throw new InvalidOperationException(diagnostic);
        }
    }

    private sealed record NativeInferenceEvidence(
        int SchemaVersion,
        string EvidenceKind,
        DateTimeOffset GeneratedUtc,
        string RuntimeManifestSha256,
        ImmutableArray<NativeViewEvidence> Views,
        string CanonicalResultSetSha256,
        string Outcome);

    private sealed record NativeViewEvidence(
        string Name,
        string ViewRole,
        string SourcePath,
        long SourceLength,
        string SourceSha256,
        string CanonicalRgbaSha256,
        int Width,
        int Height,
        double DetectorScore,
        int LandmarkCount,
        int TransformationMatrixLength,
        string FirstInferenceSha256,
        string SecondInferenceSha256,
        bool ExactHashEquality);
}
