using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Composes ordinary hash-bound RaceMenu tint inputs into a conventional
/// FaceTint plus an NPC-private residual diffuse. The implementation is fully
/// in-process and independently decodes both outputs before returning them.
/// </summary>
public sealed partial class RaceMenuNpcFaceTextureBuildService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    IFaceTintTextureDecoder sourceDecoder,
    IFaceTintTextureDecoder outputVerifier) : IRaceMenuNpcFaceTextureBuildService
{
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async ValueTask<RaceMenuNpcFaceTextureBuildResult> BuildAsync(
        RaceMenuNpcFaceTextureBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        RaceMenuFaceTextureOutputSession? outputSession = null;
        var retainOutputs = false;
        var cleanupPerformed = false;
        RaceMenuNpcFaceTextureBuildResult RefuseWrittenOutputs()
        {
            if (outputSession is not null)
                diagnostics.AddRange(outputSession.Cleanup().Diagnostics);
            cleanupPerformed = true;
            return Refused(diagnostics);
        }
        try
        {
            if (!ValidateRequest(request, diagnostics)) return Refused(diagnostics);
            var authority = await ReadAuthorityAsync(request, diagnostics, cancellationToken);
            if (authority is null || HasErrors(diagnostics)) return Refused(diagnostics);
            var composition = await ComposeAsync(authority, diagnostics, cancellationToken);
            if (composition is null || HasErrors(diagnostics)) return Refused(diagnostics);

            var faceTintDds = EncodeBgra8(request.Width, request.Height,
                composition.ConventionalFaceTint);
            var privateDiffuseDds = EncodeBgra8(request.Width, request.Height,
                composition.PrivateDiffuse);
            FaceTextureOutputSessionOpenResult opened =
                RaceMenuFaceTextureOutputSession.Open(request, labRoot);
            diagnostics.AddRange(opened.Diagnostics);
            if (opened.Session is null || HasErrors(diagnostics)) return Refused(diagnostics);
            outputSession = opened.Session;
            if (!await outputSession.TryWriteNewAsync(request.FaceTintOutput,
                    faceTintDds, diagnostics, cancellationToken) ||
                !await outputSession.TryWriteNewAsync(request.PrivateDiffuseOutput,
                    privateDiffuseDds, diagnostics, cancellationToken))
                return RefuseWrittenOutputs();

            var faceTintHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(faceTintDds)));
            var privateDiffuseHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(privateDiffuseDds)));
            if (!await VerifyOutputsAsync(request, authority, composition, faceTintHash,
                    privateDiffuseHash, diagnostics, cancellationToken))
                return RefuseWrittenOutputs();

            var evidence = new CompositionEvidence(
                1,
                "racemenu-face-texture-composition",
                authority.AuthorityId,
                authority.ManifestSha256.Value,
                authority.PresetSha256.Value,
                authority.FullComposite.Sha256.Value,
                authority.BaseDiffuse.Sha256.Value,
                faceTintHash.Value,
                privateDiffuseHash.Value,
                authority.PrivateDiffuseDestination.Value,
                request.Width,
                request.Height,
                authority.ProtectedNeck.StartRowInclusive,
                authority.ProtectedNeck.EndRowInclusive,
                composition.MappedLayerCount,
                composition.BakedLayerCount,
                composition.CompositeModelMaximumRgbByteError,
                composition.CompositeModelMeanRgbByteError,
                composition.ReconstructionMaximumRgbByteError,
                composition.ReconstructionMeanRgbByteError,
                FaceTintOpaqueAlpha: true,
                PrivateAlphaMatchesBase: true,
                ProtectedNeckExact: true,
                IndependentlyDecoded: true,
                RuntimeAuthority: false);
            var evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(evidence,
                EvidenceJsonOptions);
            if (!await outputSession.TryWriteNewAsync(request.EvidenceOutput,
                    evidenceBytes, diagnostics, cancellationToken))
                return RefuseWrittenOutputs();
            var evidenceHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(evidenceBytes)));
            var ownedEvidence = await outputSession.ReadOwnedBytesAsync(
                request.EvidenceOutput, 1 * 1024 * 1024, cancellationToken);
            var reopenedEvidence = await ReadOrdinaryBytesAsync(request.EvidenceOutput,
                1 * 1024 * 1024, cancellationToken);
            if (!evidenceBytes.AsSpan().SequenceEqual(ownedEvidence) ||
                !evidenceBytes.AsSpan().SequenceEqual(reopenedEvidence) ||
                evidenceHash != new Sha256Hash(Convert.ToHexString(
                    SHA256.HashData(reopenedEvidence))))
            {
                diagnostics.Add(Error("face-texture-evidence-readback",
                    "The composition evidence changed when independently reopened."));
                return RefuseWrittenOutputs();
            }

            var artifact = new RaceMenuNpcFaceTextureBuildArtifact(
                authority.AuthorityId,
                request.Authority.ManifestPath,
                authority.ManifestSha256,
                request.FaceTintOutput,
                faceTintHash,
                request.PrivateDiffuseOutput,
                privateDiffuseHash,
                authority.PrivateDiffuseDestination,
                request.EvidenceOutput,
                evidenceHash,
                authority.FullComposite.Sha256,
                authority.BaseDiffuse.Sha256,
                request.Width,
                request.Height,
                authority.ProtectedNeck.StartRowInclusive,
                authority.ProtectedNeck.EndRowInclusive,
                composition.MappedLayerCount,
                composition.BakedLayerCount,
                composition.ReconstructionMaximumRgbByteError,
                composition.ReconstructionMeanRgbByteError,
                RuntimeAuthority: false);
            diagnostics.Add(new Diagnostic("face-texture-static-verified",
                DiagnosticSeverity.Info,
                "The product composed and independently decoded a conventional FaceTint/private-diffuse pair; runtime rendering remains unproven."));
            diagnostics.AddRange(outputSession.Retain().Diagnostics);
            cleanupPerformed = true;
            retainOutputs = true;
            return new RaceMenuNpcFaceTextureBuildResult(
                true, true, artifact, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           OverflowException or
                                           JsonException)
        {
            diagnostics.Add(Error("face-texture-build-failed", exception.Message));
            return outputSession is not null ? RefuseWrittenOutputs() : Refused(diagnostics);
        }
        finally
        {
            if (outputSession is not null && !retainOutputs && !cleanupPerformed)
                diagnostics.AddRange(outputSession.Cleanup().Diagnostics);
        }
    }

    private bool ValidateRequest(
        RaceMenuNpcFaceTextureBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!PathEquals(request.AllowedRoot, labRoot))
            diagnostics.Add(Error("face-texture-allowed-root",
                "The face-texture request must use the configured K-local lab root."));
        if (!request.StagingRoot.IsUnder(labRoot) ||
            !Directory.Exists(request.StagingRoot.Value) ||
            File.GetAttributes(request.StagingRoot.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(Error("face-texture-staging-root",
                "The face-texture staging root must be an existing ordinary K-local directory."));
        var pixelCount = (long)request.Width * request.Height;
        if (request.Width is <= 0 or >
                RaceMenuNpcFaceTextureCompositionLimits.MaximumAxisPixels ||
            request.Height is <= 0 or >
                RaceMenuNpcFaceTextureCompositionLimits.MaximumAxisPixels ||
            pixelCount > RaceMenuNpcFaceTextureCompositionLimits.MaximumPixels)
            diagnostics.Add(Error("face-texture-dimensions",
                $"Face-texture dimensions must be positive, no axis may exceed " +
                $"{RaceMenuNpcFaceTextureCompositionLimits.MaximumAxisPixels}, and the raster " +
                $"may not exceed {RaceMenuNpcFaceTextureCompositionLimits.MaximumPixels} pixels."));
        if (request.Plan.Request.Edition != GameEdition.SkyrimSpecialEdition ||
            !request.Plan.IsReady)
            diagnostics.Add(Error("face-texture-plan",
                "Face-texture composition requires a ready Skyrim Special Edition appearance plan."));
        if (!request.PrivateDiffuseDestination.Value.StartsWith("Textures/",
                StringComparison.OrdinalIgnoreCase) ||
            !request.PrivateDiffuseDestination.Value.EndsWith(".dds",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("face-texture-private-diffuse-destination",
                "The private diffuse destination must be a Data-relative Textures/*.dds path."));
        ValidateOutput(request.FaceTintOutput, request.StagingRoot, ".dds", diagnostics);
        ValidateOutput(request.PrivateDiffuseOutput, request.StagingRoot, ".dds", diagnostics);
        ValidateOutput(request.EvidenceOutput, request.StagingRoot, ".json", diagnostics);
        var outputNames = new[]
        {
            request.FaceTintOutput.Value,
            request.PrivateDiffuseOutput.Value,
            request.EvidenceOutput.Value
        };
        if (outputNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputNames.Length)
            diagnostics.Add(Error("face-texture-output-duplicate",
                "Face-texture outputs must use three distinct paths."));
        return !HasErrors(diagnostics);
    }

    private void ValidateOutput(
        WorkspacePath output,
        WorkspacePath stagingRoot,
        string extension,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!output.IsUnder(stagingRoot) || !output.IsUnder(labRoot) ||
            !output.Value.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
            File.Exists(output.Value))
        {
            diagnostics.Add(Error("face-texture-output-invalid",
                $"Face-texture output '{output.Value}' must be a new {extension} under the K-local staging root."));
            return;
        }
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent) ||
            File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(Error("face-texture-output-parent",
                "Every face-texture output parent must already exist and may not be a reparse point."));
        diagnostics.AddRange(policy.Evaluate(labRoot, output));
    }

    private async ValueTask<bool> VerifyOutputsAsync(
        RaceMenuNpcFaceTextureBuildRequest request,
        FaceTextureAuthority authority,
        CompositionOutcome composition,
        Sha256Hash faceTintHash,
        Sha256Hash privateDiffuseHash,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var faceTint = await outputVerifier.DecodeAsync(request.FaceTintOutput,
            cancellationToken);
        var privateDiffuse = await outputVerifier.DecodeAsync(request.PrivateDiffuseOutput,
            cancellationToken);
        diagnostics.AddRange(faceTint.Diagnostics);
        diagnostics.AddRange(privateDiffuse.Diagnostics);
        if (!faceTint.Decoded || !privateDiffuse.Decoded || faceTint.Bytes is null ||
            privateDiffuse.Bytes is null || faceTint.SourceSha256 != faceTintHash ||
            privateDiffuse.SourceSha256 != privateDiffuseHash ||
            faceTint.Width != request.Width || faceTint.Height != request.Height ||
            privateDiffuse.Width != request.Width || privateDiffuse.Height != request.Height ||
            !faceTint.Bytes.AsSpan().SequenceEqual(composition.ConventionalFaceTint) ||
            !privateDiffuse.Bytes.AsSpan().SequenceEqual(composition.PrivateDiffuse))
        {
            if (!HasErrors(diagnostics))
                diagnostics.Add(Error("face-texture-output-readback",
                    "Face-texture outputs changed when independently decoded."));
            return false;
        }

        for (var offset = 3; offset < faceTint.Bytes.Length; offset += 4)
        {
            if ((offset & 0x3F_FFFF) == 3) cancellationToken.ThrowIfCancellationRequested();
            if (faceTint.Bytes[offset] != byte.MaxValue)
            {
                diagnostics.Add(Error("face-texture-output-alpha",
                    "The generated conventional FaceTint must have opaque alpha."));
                return false;
            }
            if (privateDiffuse.Bytes[offset] != composition.BaseDiffuse[offset])
            {
                diagnostics.Add(Error("face-texture-private-alpha",
                    "The generated private diffuse must preserve the unmodified base diffuse alpha at every pixel."));
                return false;
            }
        }
        var rowBytes = checked(request.Width * 4);
        var neckOffset = checked(authority.ProtectedNeck.StartRowInclusive * rowBytes);
        var neckLength = checked((authority.ProtectedNeck.EndRowInclusive -
                                  authority.ProtectedNeck.StartRowInclusive + 1) * rowBytes);
        if (!privateDiffuse.Bytes.AsSpan(neckOffset, neckLength)
                .SequenceEqual(composition.BaseDiffuse.AsSpan(neckOffset, neckLength)))
        {
            diagnostics.Add(Error("face-texture-protected-neck",
                "The generated private diffuse did not preserve the protected neck rows exactly."));
            return false;
        }
        var readbackError = MeasureShaderReconstruction(privateDiffuse.Bytes,
            faceTint.Bytes, composition.BaseDiffuse, composition.FullComposite,
            cancellationToken);
        if (readbackError.Maximum > authority.Tolerances.SplitShader.MaximumRgbByteError ||
            readbackError.Mean > authority.Tolerances.SplitShader.MaximumMeanRgbByteError ||
            readbackError.Maximum != composition.ReconstructionMaximumRgbByteError ||
            Math.Abs(readbackError.Mean - composition.ReconstructionMeanRgbByteError) > 1E-12D)
        {
            diagnostics.Add(Error("face-texture-readback-reconstruction",
                "Independently decoded outputs do not retain the admitted reconstruction metrics."));
            return false;
        }
        return true;
    }

    private static async ValueTask<byte[]> ReadOrdinaryBytesAsync(
        WorkspacePath source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!WindowsPinnedPath.TryOpenFile(source.Value, false, out var pinned,
                out _, out var openError) || pinned is null)
            throw new InvalidDataException(
                $"Generated evidence is not an ordinary identity-pinned file: {openError}");
        using (pinned)
            return await pinned.ReadAllBytesAsync(maximumBytes, cancellationToken);
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuNpcFaceTextureBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, null, diagnostics.ToImmutable());
}
