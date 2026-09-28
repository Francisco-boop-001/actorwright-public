using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class BlankNpcBuildService
{
    private const string CanonicalExactFaceTintEvidence =
        "evidence/facetint-exact-source.json";
    private const int MaximumExactFaceTintEvidenceBytes = 64 * 1024;
    private const uint ExactFaceTintFileFlagOverlapped = 0x40000000;

    private static readonly JsonSerializerOptions ExactFaceTintEvidenceJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static async ValueTask<ExactFaceTintEvidenceVerificationResult>
        VerifyExactFaceTintEvidenceFileAsync(
            WorkspacePath packageRoot,
            AssetPath evidenceFile,
            IWorkspacePolicy policy,
            WorkspacePath labRoot,
            IFaceTintTextureDecoder decoder,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!string.Equals(evidenceFile.Value, CanonicalExactFaceTintEvidence,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(ExactFaceTintError("blank-npc-exact-facetint-evidence-path",
                $"Durable exact FaceTint evidence must use {CanonicalExactFaceTintEvidence}."));
            return ExactFaceTintRefused(diagnostics);
        }
        if (!packageRoot.IsUnder(labRoot) ||
            !IsOrdinaryExactFaceTintDirectory(packageRoot.Value))
        {
            diagnostics.Add(ExactFaceTintError("blank-npc-exact-facetint-package-root",
                "The durable exact FaceTint package root must be an ordinary K-local directory."));
            return ExactFaceTintRefused(diagnostics);
        }
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, packageRoot));
        if (HasErrors(diagnostics)) return ExactFaceTintRefused(diagnostics);

        var evidencePath = new WorkspacePath(Path.Combine(packageRoot.Value,
            evidenceFile.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!evidencePath.IsUnder(packageRoot))
        {
            diagnostics.Add(ExactFaceTintError("blank-npc-exact-facetint-evidence-file",
                "The durable exact FaceTint evidence must be an ordinary file inside its package."));
            return ExactFaceTintRefused(diagnostics);
        }
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, evidencePath));
        if (HasErrors(diagnostics)) return ExactFaceTintRefused(diagnostics);

        try
        {
            if (!TryOpenPinnedExactFaceTintFile(evidencePath,
                    MaximumExactFaceTintEvidenceBytes, out var evidenceStream,
                    out var evidenceOpenError) || evidenceStream is null)
            {
                diagnostics.Add(ExactFaceTintError(
                    "blank-npc-exact-facetint-evidence-file",
                    $"The durable exact FaceTint evidence could not be identity-pinned: " +
                    evidenceOpenError));
                return ExactFaceTintRefused(diagnostics);
            }
            byte[] bytes;
            using (evidenceStream)
                bytes = await ReadPinnedExactFaceTintBytesAsync(evidenceStream,
                    cancellationToken);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            RejectExactFaceTintDuplicateKeys(document.RootElement);
            var evidence = JsonSerializer.Deserialize<ExactFaceTintMaterializationArtifact>(
                bytes, ExactFaceTintEvidenceJsonOptions) ??
                throw new InvalidDataException("Exact FaceTint evidence is empty.");
            if (evidence.SchemaVersion != "1" ||
                evidence.ArtifactKind != "exact-dds-facetint-materialization-v2" ||
                string.IsNullOrWhiteSpace(evidence.SourceSha256) ||
                string.IsNullOrWhiteSpace(evidence.OutputDds) ||
                string.IsNullOrWhiteSpace(evidence.OutputSha256) ||
                evidence.SourceByteLength <= 0 || evidence.OutputByteLength <= 0 ||
                evidence.SourceByteLength != evidence.OutputByteLength ||
                evidence.Width is <= 0 or > 8192 || evidence.Height is <= 0 or > 8192 ||
                (long)evidence.Width * evidence.Height >
                    RaceMenuNpcFaceTextureCompositionLimits.MaximumPixels ||
                evidence.OutputByteLength >
                    RaceMenuNpcFaceTextureCompositionLimits.MaximumEncodedBgra8DdsBytes ||
                !evidence.ByteExact || evidence.RuntimeAuthority)
            {
                throw new InvalidDataException(
                    "Exact FaceTint evidence fields do not satisfy the durable byte-exact contract.");
            }
            var sourceHash = new Sha256Hash(evidence.SourceSha256);
            var outputHash = new Sha256Hash(evidence.OutputSha256);
            if (sourceHash != outputHash)
                throw new InvalidDataException(
                    "Byte-exact FaceTint evidence requires identical source and output hashes.");
            var outputDds = new AssetPath(evidence.OutputDds);
            if (!outputDds.Value.StartsWith(
                    "Data/textures/actors/character/FaceGenData/FaceTint/",
                    StringComparison.OrdinalIgnoreCase) ||
                !outputDds.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Exact FaceTint evidence outputDds is not a canonical package FaceTint path.");
            }
            var outputPath = new WorkspacePath(Path.Combine(packageRoot.Value,
                outputDds.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!outputPath.IsUnder(packageRoot))
            {
                throw new InvalidDataException(
                    "The durable FaceTint output escaped its package root.");
            }
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, outputPath));
            if (HasErrors(diagnostics)) return ExactFaceTintRefused(diagnostics);
            if (!TryOpenPinnedExactFaceTintFile(outputPath,
                    RaceMenuNpcFaceTextureCompositionLimits.MaximumEncodedBgra8DdsBytes,
                    out var outputStream, out var outputOpenError) || outputStream is null)
            {
                throw new InvalidDataException(
                    $"The durable FaceTint output could not be identity-pinned: " +
                    outputOpenError);
            }
            Sha256Hash actualHash;
            FaceTintTextureDecodeResult decoded;
            using (outputStream)
            {
                if (outputStream.Length != evidence.OutputByteLength)
                    throw new InvalidDataException(
                        "The durable FaceTint output has the wrong byte length.");
                outputStream.Position = 0;
                actualHash = new Sha256Hash(Convert.ToHexString(
                    await SHA256.HashDataAsync(outputStream, cancellationToken)));
                outputStream.Position = 0;
                decoded = await decoder.DecodeAsync(outputPath, cancellationToken);
            }
            diagnostics.AddRange(decoded.Diagnostics);
            if (actualHash != outputHash || !decoded.Decoded || decoded.SourceSha256 != outputHash ||
                decoded.Width != evidence.Width || decoded.Height != evidence.Height ||
                HasErrors(diagnostics))
            {
                if (!HasErrors(diagnostics))
                    diagnostics.Add(ExactFaceTintError(
                        "blank-npc-exact-facetint-evidence-readback",
                        "The relocated package FaceTint did not retain its admitted hash and dimensions."));
                return ExactFaceTintRefused(diagnostics, outputPath, sourceHash, actualHash,
                    evidence.SourceByteLength, evidence.OutputByteLength,
                    evidence.Width, evidence.Height);
            }
            diagnostics.Add(new Diagnostic("blank-npc-exact-facetint-evidence-verified",
                DiagnosticSeverity.Info,
                "The package-relative FaceTint evidence rehashed and decoded the retained byte-exact source representation."));
            return new ExactFaceTintEvidenceVerificationResult(
                true, outputPath, sourceHash, actualHash,
                evidence.SourceByteLength, evidence.OutputByteLength,
                evidence.Width, evidence.Height, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           JsonException or
                                           InvalidDataException or
                                           ArgumentException or
                                           FormatException or
                                           OverflowException)
        {
            diagnostics.Add(ExactFaceTintError("blank-npc-exact-facetint-evidence-invalid",
                exception.Message));
            return ExactFaceTintRefused(diagnostics);
        }
    }

    private static void RejectExactFaceTintDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Duplicate exact FaceTint evidence field '{property.Name}' is refused.");
                RejectExactFaceTintDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                RejectExactFaceTintDuplicateKeys(item);
        }
    }

    private static bool IsOrdinaryExactFaceTintDirectory(string path)
    {
        try
        {
            return Directory.Exists(path) &&
                   !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryOpenPinnedExactFaceTintFile(
        WorkspacePath path,
        long maximumBytes,
        out FileStream? stream,
        out string error)
    {
        stream = null;
        error = string.Empty;
        var handle = OpenFileHandle(path.Value, GenericRead | FileReadAttributes,
            FileShare.Read,
            FileFlagOpenReparsePoint | ExactFaceTintFileFlagOverlapped);
        if (handle.IsInvalid)
        {
            var nativeError = Marshal.GetLastWin32Error();
            handle.Dispose();
            error = new Win32Exception(nativeError).Message;
            return false;
        }
        try
        {
            if (!TryReadHandleIdentity(handle, out var identity, out error) ||
                identity.Attributes.HasFlag(FileAttributes.Directory) ||
                identity.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (string.IsNullOrWhiteSpace(error))
                    error = "The pinned path is not an ordinary file.";
                return false;
            }
            if (!string.Equals(CanonicalPath(GetFinalDosPath(handle)),
                    CanonicalPath(path.Value), StringComparison.OrdinalIgnoreCase))
            {
                error = "The pinned file resolved to a different final path.";
                return false;
            }
            stream = new FileStream(handle, FileAccess.Read, 64 * 1024, isAsync: true);
            if (stream.Length is <= 0 || stream.Length > maximumBytes)
            {
                error = $"File size {stream.Length} is outside the admitted range " +
                        $"1..{maximumBytes} bytes.";
                stream.Dispose();
                stream = null;
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException or
                                           Win32Exception)
        {
            error = exception.Message;
            stream?.Dispose();
            stream = null;
            return false;
        }
        finally
        {
            if (stream is null) handle.Dispose();
        }
    }

    private static async ValueTask<byte[]> ReadPinnedExactFaceTintBytesAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException(
                    "The pinned exact FaceTint evidence ended before its admitted length.");
            offset += read;
        }
        stream.Position = 0;
        return bytes;
    }

    private static Diagnostic ExactFaceTintError(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ExactFaceTintEvidenceVerificationResult ExactFaceTintRefused(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        WorkspacePath? outputDds = null,
        Sha256Hash? sourceSha256 = null,
        Sha256Hash? outputSha256 = null,
        long? sourceByteLength = null,
        long? outputByteLength = null,
        int? width = null,
        int? height = null) =>
        new(false, outputDds, sourceSha256, outputSha256, sourceByteLength,
            outputByteLength, width, height, diagnostics.ToImmutable());
}

internal sealed record ExactFaceTintEvidenceVerificationResult(
    bool Verified,
    WorkspacePath? OutputDds,
    Sha256Hash? SourceSha256,
    Sha256Hash? OutputSha256,
    long? SourceByteLength,
    long? OutputByteLength,
    int? Width,
    int? Height,
    ImmutableArray<Diagnostic> Diagnostics);
