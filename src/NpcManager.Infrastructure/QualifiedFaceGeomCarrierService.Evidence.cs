using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class QualifiedFaceGeomCarrierService
{
    private const int MaxTextureSetPreimageBase64Chars =
        ((MaxTextureSetPreimageBytes + 2) / 3) * 4;

    public async ValueTask<QualifiedFaceGeomCarrierEvidenceVerificationResult> VerifyEvidenceAsync(
        WorkspacePath packageRoot,
        QualifiedFaceGeomCarrierMaterializationEvidence evidence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var outputNif = ValidateEvidenceEnvelope(packageRoot, evidence, diagnostics);
        if (outputNif is null || HasErrors(diagnostics))
            return RefusedEvidenceVerification(outputNif, null, null, diagnostics);

        var materialization = evidence.Materialization;
        var preimageBytes = DecodePreimage(materialization.SourceTextureSetPreimage, diagnostics);
        if (preimageBytes is null || HasErrors(diagnostics))
            return RefusedEvidenceVerification(outputNif, null, null, diagnostics);

        var outputBytes = await ReadNifAsync(
            outputNif.Value, diagnostics, "durable-evidence output", cancellationToken);
        if (outputBytes is null)
            return RefusedEvidenceVerification(outputNif, null, null, diagnostics);

        var outputHash = Hash(outputBytes);
        if (outputHash != materialization.OutputSha256)
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-output-hash-mismatch",
                DiagnosticSeverity.Error,
                "The package FaceGeom NIF does not match the durable evidence output SHA-256."));
        if (outputBytes.LongLength != materialization.OutputByteLength)
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-output-length-mismatch",
                DiagnosticSeverity.Error,
                "The package FaceGeom NIF does not match the durable evidence output length."));
        if (HasErrors(diagnostics))
            return RefusedEvidenceVerification(outputNif, outputHash, null, diagnostics);

        try
        {
            var output = SseFaceGeomCarrierCodec.Parse(outputBytes);
            var reconstructedSourceBytes =
                SseFaceGeomCarrierCodec.ReconstructSourceFromTextureSetPreimage(
                    output,
                    materialization.SourceTextureSetPreimage.BlockIndex,
                    preimageBytes);
            var reconstructedSourceHash = Hash(reconstructedSourceBytes);
            if (reconstructedSourceBytes.LongLength != materialization.SourceByteLength)
                diagnostics.Add(new Diagnostic("qualified-carrier-evidence-source-length-mismatch",
                    DiagnosticSeverity.Error,
                    "The final NIF plus preimage did not reconstruct the declared source length."));
            if (reconstructedSourceHash != materialization.SourceSha256)
                diagnostics.Add(new Diagnostic("qualified-carrier-evidence-source-hash-mismatch",
                    DiagnosticSeverity.Error,
                    "The final NIF plus preimage did not reconstruct the declared source SHA-256."));
            if (HasErrors(diagnostics))
                return RefusedEvidenceVerification(
                    outputNif, outputHash, reconstructedSourceHash, diagnostics);

            var source = SseFaceGeomCarrierCodec.Parse(reconstructedSourceBytes);
            var sourceStructure = SseFaceGeomCarrierCodec.BuildStructure(source);
            var outputStructure = SseFaceGeomCarrierCodec.BuildStructure(output);
            SseFaceGeomCarrierCodec.Qualify(
                source,
                sourceStructure,
                materialization.QualificationProfile,
                diagnostics);
            SseFaceGeomCarrierCodec.Qualify(
                output,
                outputStructure,
                materialization.QualificationProfile,
                diagnostics);
            if (!StructureMatches(sourceStructure, materialization.SourceStructure))
                diagnostics.Add(new Diagnostic("qualified-carrier-evidence-source-structure-drift",
                    DiagnosticSeverity.Error,
                    "The reconstructed source graph does not match the durable source structure."));
            if (!StructureMatches(sourceStructure, outputStructure))
                diagnostics.Add(new Diagnostic("qualified-carrier-evidence-output-graph-drift",
                    DiagnosticSeverity.Error,
                    "The package FaceGeom graph differs from the reconstructed qualified source graph."));

            var sourceTarget = SseFaceGeomCarrierCodec.FindFaceTintTarget(
                source,
                materialization.QualificationProfile,
                diagnostics);
            if (sourceTarget is null ||
                sourceTarget.BlockIndex != materialization.SourceTextureSetPreimage.BlockIndex ||
                sourceTarget.SlotIndex != materialization.TextureSlotIndex ||
                !string.Equals(sourceTarget.OriginalPath,
                    materialization.OriginalFaceTintPath, StringComparison.Ordinal))
            {
                diagnostics.Add(new Diagnostic("qualified-carrier-evidence-source-target-drift",
                    DiagnosticSeverity.Error,
                    "The reconstructed source texture target does not match the durable binding."));
                return new QualifiedFaceGeomCarrierEvidenceVerificationResult(
                    false,
                    outputNif,
                    outputHash,
                    reconstructedSourceHash,
                    outputStructure,
                    [],
                    diagnostics.ToImmutable());
            }

            var changedBlocks = SseFaceGeomCarrierCodec.VerifyRewrite(
                source,
                output,
                sourceTarget,
                materialization.TargetFaceTintPath,
                materialization.TargetHeadTextures,
                materialization.QualificationProfile,
                diagnostics);
            if (!changedBlocks.SequenceEqual(materialization.ChangedBlocks))
                diagnostics.Add(new Diagnostic("qualified-carrier-evidence-change-surface-drift",
                    DiagnosticSeverity.Error,
                    "The reconstructed source-to-output change surface does not match the durable evidence."));
            if (HasErrors(diagnostics))
                return new QualifiedFaceGeomCarrierEvidenceVerificationResult(
                    false,
                    outputNif,
                    outputHash,
                    reconstructedSourceHash,
                    outputStructure,
                    changedBlocks,
                    diagnostics.ToImmutable());

            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-verified",
                DiagnosticSeverity.Info,
                "The relocatable package evidence reconstructed the exact source and verified the one-block texture rewrite without a staging file."));
            return new QualifiedFaceGeomCarrierEvidenceVerificationResult(
                true,
                outputNif,
                outputHash,
                reconstructedSourceHash,
                outputStructure,
                changedBlocks,
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-reconstruction-failed",
                DiagnosticSeverity.Error,
                exception.Message));
            return RefusedEvidenceVerification(outputNif, outputHash, null, diagnostics);
        }
    }

    private WorkspacePath? ValidateEvidenceEnvelope(
        WorkspacePath packageRoot,
        QualifiedFaceGeomCarrierMaterializationEvidence? evidence,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateKLocalPath(packageRoot, "package root", diagnostics);
        if (!packageRoot.IsUnder(_labRoot))
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-package-outside-lab",
                DiagnosticSeverity.Error,
                "Durable FaceGeom evidence may be verified only below the K-local lab root."));
        if (!Directory.Exists(packageRoot.Value))
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-package-missing",
                DiagnosticSeverity.Error,
                "The durable evidence package root does not exist."));
        if (!HasErrors(diagnostics))
        {
            diagnostics.AddRange(_policy.EvaluateReadRoot(_labRoot, packageRoot));
            AddReparseDiagnostic(diagnostics, _labRoot, packageRoot, "evidence-package-root");
        }

        if (evidence is null || evidence.Materialization is null)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-missing",
                DiagnosticSeverity.Error,
                "The durable FaceGeom materialization evidence is missing."));
            return null;
        }

        var materialization = evidence.Materialization;
        ValidateMaterializationEnvelope(materialization, diagnostics);
        if (string.IsNullOrWhiteSpace(evidence.OutputNif.Value) ||
            !IsCanonicalPackageFaceGeomPath(evidence.OutputNif))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-output-path-invalid",
                DiagnosticSeverity.Error,
                "The durable output path must be Data/meshes/actors/character/FaceGenData/FaceGeom/<plugin>/<8-hex-form>.nif."));
            return null;
        }

        WorkspacePath outputNif;
        try
        {
            outputNif = new WorkspacePath(Path.Combine(
                packageRoot.Value,
                evidence.OutputNif.Value.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-output-path-invalid",
                DiagnosticSeverity.Error,
                exception.Message));
            return null;
        }

        if (!outputNif.IsUnder(packageRoot))
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-output-outside-package",
                DiagnosticSeverity.Error,
                "The durable FaceGeom output escaped its package root."));
        if (!HasErrors(diagnostics))
            ValidateOutputRouteBinding(outputNif, materialization.TargetFaceTintPath, diagnostics);
        if (!HasErrors(diagnostics))
        {
            diagnostics.AddRange(_policy.EvaluateReadRoot(_labRoot, outputNif));
            if (!File.Exists(outputNif.Value))
                diagnostics.Add(new Diagnostic("qualified-carrier-evidence-output-missing",
                    DiagnosticSeverity.Error,
                    "The package-relative FaceGeom output does not exist."));
            AddReparseDiagnostic(diagnostics, _labRoot, outputNif, "evidence-output-nif");
        }
        return outputNif;
    }

    private static void ValidateMaterializationEnvelope(
        QualifiedFaceGeomCarrierMaterializationArtifact materialization,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateQualificationProfile(
            materialization.QualificationProfile,
            diagnostics);
        if (!string.Equals(materialization.SchemaVersion, "1", StringComparison.Ordinal) ||
            !string.Equals(materialization.Operation, Operation, StringComparison.Ordinal) ||
            !string.Equals(materialization.ResultLabel, ResultLabel, StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-schema",
                DiagnosticSeverity.Error,
                "The durable FaceGeom evidence schema, operation, or result label is unsupported."));
        if (materialization.CreationKitAuthority || materialization.RuntimeAuthority)
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-authority-overclaim",
                DiagnosticSeverity.Error,
                "Durable carrier materialization evidence cannot claim CK or runtime authority."));
        if (materialization.SourceByteLength is <= 0 or > MaxNifBytes ||
            materialization.OutputByteLength is <= 0 or > MaxNifBytes ||
            materialization.TextureSlotIndex < 0)
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-values",
                DiagnosticSeverity.Error,
                "The durable evidence contains invalid NIF lengths or texture target values."));

        var preimage = materialization.SourceTextureSetPreimage;
        if (preimage is null ||
            !string.Equals(preimage.Encoding, "base64", StringComparison.Ordinal) ||
            !string.Equals(preimage.BlockType, "BSShaderTextureSet", StringComparison.Ordinal) ||
            preimage.BlockIndex < 0 ||
            preimage.ByteLength is <= 0 or > MaxTextureSetPreimageBytes ||
            string.IsNullOrWhiteSpace(preimage.BytesBase64) ||
            preimage.BytesBase64.Length > MaxTextureSetPreimageBase64Chars)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-preimage-envelope",
                DiagnosticSeverity.Error,
                "The source texture-set preimage is missing, malformed, or outside its safety bound."));
        }
        else if (materialization.ChangedBlocks.IsDefault ||
                 !materialization.ChangedBlocks.SequenceEqual([preimage.BlockIndex]))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-change-surface",
                DiagnosticSeverity.Error,
                "Durable evidence must declare exactly its sole texture-set preimage block as changed."));
        }

        var sourceStructure = materialization.SourceStructure;
        if (sourceStructure is null ||
            sourceStructure.ReachableShapeNames.IsDefault ||
            sourceStructure.ReachableCensus.IsDefault)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-source-structure-missing",
                DiagnosticSeverity.Error,
                "The durable source structure and its graph collections must be present."));
        }

        if (string.IsNullOrWhiteSpace(materialization.TargetFaceTintPath.Value))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-target-missing",
                DiagnosticSeverity.Error,
                "The durable target FaceTint route is missing."));
            return;
        }

        ValidateCanonicalFaceTintPath(materialization.TargetFaceTintPath, diagnostics);
        if (!HeadTexturePathsAreInitialized(materialization.TargetHeadTextures))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-head-textures-missing",
                DiagnosticSeverity.Error,
                "One or more required private-head texture paths are missing."));
            return;
        }
        ValidateTargetHeadTextures(materialization.TargetHeadTextures, diagnostics);
        if (materialization.TargetTextureBindingSha256 != BindTargetTextures(
                materialization.TargetFaceTintPath,
                materialization.TargetHeadTextures))
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-target-binding",
                DiagnosticSeverity.Error,
                "The durable target-texture binding does not match its complete typed routes."));
    }

    private static byte[]? DecodePreimage(
        QualifiedFaceGeomCarrierTextureSetPreimage preimage,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(preimage.BytesBase64);
        }
        catch (FormatException)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-preimage-base64",
                DiagnosticSeverity.Error,
                "The source texture-set preimage is not canonical Base64 data."));
            return null;
        }

        if (!string.Equals(Convert.ToBase64String(bytes), preimage.BytesBase64,
                StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-preimage-base64-canonical",
                DiagnosticSeverity.Error,
                "The source texture-set preimage is not encoded as canonical Base64."));
        if (bytes.Length != preimage.ByteLength || bytes.Length > MaxTextureSetPreimageBytes)
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-preimage-length",
                DiagnosticSeverity.Error,
                "The decoded source texture-set preimage does not match its bounded byte length."));
        if (bytes.Length > 0 && Hash(bytes) != preimage.Sha256)
            diagnostics.Add(new Diagnostic("qualified-carrier-evidence-preimage-hash",
                DiagnosticSeverity.Error,
                "The decoded source texture-set preimage does not match its SHA-256."));
        return HasErrors(diagnostics) ? null : bytes;
    }

    private static bool IsCanonicalPackageFaceGeomPath(AssetPath path)
    {
        var segments = path.Value.Split('/');
        return segments.Length == 8 &&
               string.Equals(segments[0], "Data", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[1], "meshes", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[2], "actors", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[3], "character", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[4], "FaceGenData", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[5], "FaceGeom", StringComparison.OrdinalIgnoreCase) &&
               IsPluginName(segments[6]) &&
               segments[7] is { Length: 12 } &&
               segments[7].EndsWith(".nif", StringComparison.OrdinalIgnoreCase) &&
               segments[7].AsSpan(0, 8).ContainsOnlyHexDigits();
    }

    private static bool HeadTexturePathsAreInitialized(SkyrimPrivateHeadTexturePaths? textures) =>
        textures is null ||
        !string.IsNullOrWhiteSpace(textures.Diffuse.Value) &&
        !string.IsNullOrWhiteSpace(textures.NormalOrGloss.Value) &&
        !string.IsNullOrWhiteSpace(textures.GlowOrDetailMap.Value) &&
        !string.IsNullOrWhiteSpace(textures.Height.Value) &&
        !string.IsNullOrWhiteSpace(textures.BacklightMaskOrSpecular.Value);
}
