using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class QualifiedFaceGeomCarrierService
{
    private static async ValueTask<byte[]?> ReadNifAsync(
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string role,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!TryVerifyOpenedNifFinalPath(stream.SafeFileHandle, path.Value,
                    out var finalPathError))
            {
                diagnostics.Add(new Diagnostic("qualified-carrier-path-identity",
                    DiagnosticSeverity.Error,
                    $"The {role} NIF could not be identity-pinned to its declared path: " +
                    finalPathError));
                return null;
            }
            if (stream.Length is <= 0 or > MaxNifBytes)
            {
                diagnostics.Add(new Diagnostic("qualified-carrier-size-limit", DiagnosticSeverity.Error,
                    $"The {role} NIF must contain 1 to {MaxNifBytes} bytes."));
                return null;
            }

            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-read-failed", DiagnosticSeverity.Error,
                $"The {role} NIF could not be read: {exception.Message}"));
            return null;
        }
    }

    private static bool TryVerifyOpenedNifFinalPath(
        SafeFileHandle handle,
        string expectedPath,
        out string error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Final-path identity verification is supported only on Windows.";
            return false;
        }
        try
        {
            var actual = CanonicalOpenedNifPath(GetOpenedNifFinalDosPath(handle));
            var expected = CanonicalOpenedNifPath(expectedPath);
            if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                error = string.Empty;
                return true;
            }
            error = $"The opened file resolved as '{actual}' instead of '{expected}'.";
            return false;
        }
        catch (Win32Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string GetOpenedNifFinalDosPath(SafeFileHandle handle)
    {
        const uint fileNameNormalized = 0;
        const uint volumeNameDos = 0;
        var required = GetQualifiedFaceGeomFinalPathNameByHandle(
            handle, null, 0, fileNameNormalized | volumeNameDos);
        if (required == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new char[checked((int)required + 1)];
        var written = GetQualifiedFaceGeomFinalPathNameByHandle(
            handle, buffer, (uint)buffer.Length, fileNameNormalized | volumeNameDos);
        if (written == 0 || written >= buffer.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new string(buffer, 0, checked((int)written));
        return path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                ? path[4..]
                : path;
    }

    private static string CanonicalOpenedNifPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetQualifiedFaceGeomFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[]? path,
        uint pathLength,
        uint flags);

    private static bool ProposalMatches(
        QualifiedFaceGeomCarrierProposal supplied,
        QualifiedFaceGeomCarrierProposal analyzed) =>
        string.Equals(supplied.SchemaVersion, analyzed.SchemaVersion, StringComparison.Ordinal) &&
        string.Equals(supplied.Operation, analyzed.Operation, StringComparison.Ordinal) &&
        supplied.SourceNif == analyzed.SourceNif &&
        supplied.SourceSha256 == analyzed.SourceSha256 &&
        supplied.SourceByteLength == analyzed.SourceByteLength &&
        supplied.OutputNif == analyzed.OutputNif &&
        supplied.ExpectedOutputSha256 == analyzed.ExpectedOutputSha256 &&
        supplied.ExpectedOutputByteLength == analyzed.ExpectedOutputByteLength &&
        supplied.TextureSetBlockIndex == analyzed.TextureSetBlockIndex &&
        supplied.TextureSlotIndex == analyzed.TextureSlotIndex &&
        string.Equals(supplied.OriginalFaceTintPath, analyzed.OriginalFaceTintPath,
            StringComparison.Ordinal) &&
        supplied.TargetFaceTintPath == analyzed.TargetFaceTintPath &&
        supplied.TargetHeadTextures == analyzed.TargetHeadTextures &&
        supplied.TargetHeadTexturesBindingSha256 == analyzed.TargetHeadTexturesBindingSha256 &&
        supplied.QualificationProfile == analyzed.QualificationProfile &&
        StructureMatches(supplied.Structure, analyzed.Structure) &&
        !supplied.CreationKitAuthority && !supplied.RuntimeAuthority;

    private static bool StructureMatches(
        QualifiedFaceGeomCarrierStructure? left,
        QualifiedFaceGeomCarrierStructure? right) =>
        left is not null &&
        right is not null &&
        !left.ReachableShapeNames.IsDefault &&
        !right.ReachableShapeNames.IsDefault &&
        !left.ReachableCensus.IsDefault &&
        !right.ReachableCensus.IsDefault &&
        left.BlockCount == right.BlockCount &&
        left.ReachableBlockCount == right.ReachableBlockCount &&
        left.RootCount == right.RootCount &&
        left.NiNodeCount == right.NiNodeCount &&
        left.FadeNodeCount == right.FadeNodeCount &&
        left.DynamicShapeCount == right.DynamicShapeCount &&
        left.NullChildReferenceCount == right.NullChildReferenceCount &&
        left.GraphSha256 == right.GraphSha256 &&
        left.ReachableShapeNames.SequenceEqual(right.ReachableShapeNames, StringComparer.Ordinal) &&
        left.ReachableCensus.SequenceEqual(right.ReachableCensus);

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static Sha256Hash? BindHeadTextures(SkyrimPrivateHeadTexturePaths? textures)
    {
        if (textures is null) return null;
        return BindPaths(
        [
            textures.Diffuse,
            textures.NormalOrGloss,
            textures.GlowOrDetailMap,
            textures.Height,
            textures.BacklightMaskOrSpecular,
            textures.EnvironmentMaskOrSubsurfaceTint,
            textures.Environment,
            textures.Multilayer
        ]);
    }

    private static Sha256Hash BindTargetTextures(
        AssetPath faceTint,
        SkyrimPrivateHeadTexturePaths? textures) =>
        BindPaths(
        [
            faceTint,
            textures?.Diffuse,
            textures?.NormalOrGloss,
            textures?.GlowOrDetailMap,
            textures?.Height,
            textures?.BacklightMaskOrSpecular,
            textures?.EnvironmentMaskOrSubsurfaceTint,
            textures?.Environment,
            textures?.Multilayer
        ]);

    private static Sha256Hash BindPaths(IEnumerable<AssetPath?> paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var path in paths)
        {
            if (path is null)
            {
                BinaryPrimitives.WriteInt32LittleEndian(length, -1);
                hash.AppendData(length);
                continue;
            }

            var encoded = Encoding.UTF8.GetBytes(path.Value.Value);
            BinaryPrimitives.WriteInt32LittleEndian(length, encoded.Length);
            hash.AppendData(length);
            hash.AppendData(encoded);
        }
        return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static bool HasNifExtension(WorkspacePath path) =>
        string.Equals(Path.GetExtension(path.Value), ".nif", StringComparison.OrdinalIgnoreCase);

    private static bool IsPluginName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        (value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
         value.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
         value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase));

    private static bool IsEightHexDds(string? value) =>
        value is { Length: 12 } &&
        value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) &&
        value.AsSpan(0, 8).ContainsOnlyHexDigits();

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static QualifiedFaceGeomCarrierMaterializationResult RefusedMaterialization(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, null, null, diagnostics.ToImmutable());

    private static QualifiedFaceGeomCarrierVerificationResult RefusedVerification(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, output, null, null, [], diagnostics.ToImmutable());

    private static QualifiedFaceGeomCarrierEvidenceVerificationResult RefusedEvidenceVerification(
        WorkspacePath? output,
        Sha256Hash? outputHash,
        Sha256Hash? reconstructedSourceHash,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, output, outputHash, reconstructedSourceHash, null, [], diagnostics.ToImmutable());

    private static void AddReparseDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        WorkspacePath root,
        WorkspacePath path,
        string role)
    {
        if (!path.IsUnder(root)) return;

        try
        {
            var volumeRoot = Path.GetPathRoot(path.Value);
            if (string.IsNullOrEmpty(volumeRoot))
            {
                diagnostics.Add(new Diagnostic("qualified-carrier-path-inspection-failed",
                    DiagnosticSeverity.Error,
                    $"The {role} path has no inspectable volume root."));
                return;
            }

            var current = volumeRoot;
            CheckReparseIfPresent(current, role, diagnostics);
            var relative = Path.GetRelativePath(volumeRoot, path.Value);
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                CheckReparseIfPresent(current, role, diagnostics);
                if (HasErrors(diagnostics)) return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-path-inspection-failed", DiagnosticSeverity.Error,
                $"The {role} path could not be inspected: {exception.Message}"));
        }
    }

    private static void CheckReparseIfPresent(
        string path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-reparse-refused", DiagnosticSeverity.Error,
                $"The {role} path traverses a reparse point at {path}."));
        }
    }

    private static void DeleteFailedOutput(
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-failed-output-cleanup", DiagnosticSeverity.Error,
                $"The failed output could not be removed: {exception.Message}"));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record AnalysisWork(
        QualifiedFaceGeomCarrierAnalysisResult Result,
        byte[]? OutputBytes,
        byte[]? SourceTextureSetPreimage)
    {
        internal static AnalysisWork Refused(ImmutableArray<Diagnostic>.Builder diagnostics) =>
            new(new QualifiedFaceGeomCarrierAnalysisResult(false, null, diagnostics.ToImmutable()), null, null);
    }
}

internal static class HexSpanExtensions
{
    internal static bool ContainsOnlyHexDigits(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }
}
