using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class ExternalHeadPartFaceGeomExclusionVerifier :
    IExternalHeadPartFaceGeomExclusionVerifier
{
    private const string VerifierVersion = "preview254-facegeom-exclusion-v1";
    private const string PhysicsLocator = "HDT Skinned Mesh Physics Object";

    public ValueTask<ExternalHeadPartFaceGeomExclusionVerificationResult> VerifyAsync(
        ExternalHeadPartFaceGeomExclusionVerificationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.ExpectedFaceGeomByteLength <= 0)
        {
            diagnostics.Add(Error("external-facegeom-exclusion-length",
                "Expected FaceGeom byte length must be positive."));
            return Result(diagnostics);
        }
        if (!File.Exists(request.OutputFaceGeom.Value))
        {
            diagnostics.Add(Error("external-facegeom-exclusion-output-missing",
                $"FaceGeom output '{request.OutputFaceGeom}' does not exist."));
            return Result(diagnostics);
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(request.OutputFaceGeom.Value);
            if (bytes.LongLength != request.ExpectedFaceGeomByteLength)
            {
                diagnostics.Add(Error("external-facegeom-exclusion-length-mismatch",
                    "FaceGeom output byte length differs from the materialization attestation."));
                return Result(diagnostics);
            }
            Sha256Hash actualHash = new(Convert.ToHexString(SHA256.HashData(bytes)));
            if (actualHash != request.ExpectedFaceGeomSha256)
            {
                diagnostics.Add(Error("external-facegeom-exclusion-hash-mismatch",
                    "FaceGeom output hash differs from the materialization attestation."));
                return Result(diagnostics);
            }

            SseNifDocument document = SseFaceGeomCarrierCodec.Parse(bytes);
            QualifiedFaceGeomCarrierStructure structure =
                SseFaceGeomCarrierCodec.BuildStructure(document);
            SseFaceGeomCarrierCodec.Qualify(
                document,
                structure,
                QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
                diagnostics);
            if (HasErrors(diagnostics)) return Result(diagnostics);

            var carrierValues = CollectCarrierValues(document);
            ImmutableArray<string> includedNames = request.IncludedOrdinaryShapes
                .Select(item => NormalizeComparisonValue(item.OutputShapeName))
                .ToImmutableArray();
            ImmutableHashSet<string> reachableShapeNames = structure
                .ReachableShapeNames
                .Select(NormalizeComparisonValue)
                .ToImmutableHashSet(StringComparer.Ordinal);
            if (includedNames.IsDefaultOrEmpty ||
                includedNames.Length != reachableShapeNames.Count ||
                includedNames.Distinct(StringComparer.Ordinal).Count() != includedNames.Length ||
                !includedNames.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(reachableShapeNames))
            {
                diagnostics.Add(Error(ExternalHeadPartDiagnosticCodes.FaceGeomContaminated,
                    "The reopened carrier shape set does not exactly match the ordinary shape evidence."));
            }

            ImmutableArray<ExternalHeadPartExcludedShapeEvidence> excludedShapes =
                request.Descriptor.Physics.Shapes
                    .Select(item => new ExternalHeadPartExcludedShapeEvidence(
                        item.ModelNif, item.ShapeName))
                    .DistinctBy(item => item.ProviderModel.Value + "\0" + item.ShapeName,
                        StringComparer.Ordinal)
                    .ToImmutableArray();
            foreach (ExternalHeadPartExcludedShapeEvidence excluded in excludedShapes)
            {
                string normalizedShapeName = NormalizeComparisonValue(
                    excluded.ShapeName);
                if (reachableShapeNames.Contains(normalizedShapeName))
                {
                    diagnostics.Add(Error(ExternalHeadPartDiagnosticCodes.FaceGeomContaminated,
                        $"Provider shape '{excluded.ShapeName}' from '{excluded.ProviderModel}' entered the output carrier."));
                }
            }

            var excludedMetadata = ImmutableArray.CreateBuilder<
                ExternalHeadPartExcludedMetadataEvidence>();
            CheckAbsent(carrierValues, excludedMetadata, "physics-locator",
                PhysicsLocator, diagnostics);
            foreach (ExternalHeadPartPhysicsShapeBinding shape in
                     request.Descriptor.Physics.Shapes)
            {
                CheckAbsent(carrierValues, excludedMetadata, "provider-shape",
                    shape.ShapeName, diagnostics);
                CheckAbsent(carrierValues, excludedMetadata, "physics-xml",
                    shape.XmlPath.Value, diagnostics);
                CheckAbsent(carrierValues, excludedMetadata, "provider-model",
                    shape.ModelNif.Value, diagnostics);
                CheckAbsent(carrierValues, excludedMetadata, "collision-body",
                    shape.ModelNif.Value + "/collision", diagnostics);
            }
            if (request.Descriptor.Physics.MappingAuthority is
                    ExternalHeadPartPhysicsMappingAuthority mapping)
            {
                CheckAbsent(carrierValues, excludedMetadata, "physics-mapping",
                    mapping.Path.Value, diagnostics);
            }
            foreach (ExternalHeadPartAssetDependency asset in
                     request.Descriptor.Assets)
            {
                CheckAbsent(carrierValues, excludedMetadata,
                    "provider-asset", asset.Path.Value, diagnostics);
            }
            foreach (ExternalHeadPartRecordDependency member in
                     request.Descriptor.Members)
            {
                if (member.ModelNif is AssetPath model)
                    CheckAbsent(carrierValues, excludedMetadata,
                        "provider-member-model", model.Value, diagnostics);
                foreach (SkyrimHdptTriRoute tri in member.TriRoutes)
                    CheckAbsent(carrierValues, excludedMetadata,
                        "provider-member-tri", tri.Path.Value, diagnostics);
                CheckAbsent(carrierValues, excludedMetadata,
                    "provider-editor-id", member.EditorId, diagnostics);
            }
            foreach (ExternalHeadPartRuntimePrerequisite prerequisite in
                     request.Descriptor.RuntimePrerequisites)
            {
                CheckAbsent(carrierValues, excludedMetadata,
                    "provider-runtime", prerequisite.Kind + "/" +
                    prerequisite.Requirement, diagnostics);
                CheckAbsent(carrierValues, excludedMetadata,
                    "provider-runtime-source", prerequisite.EvidenceSource,
                    diagnostics);
            }
            ImmutableArray<ExternalHeadPartExcludedMetadataEvidence> metadata =
                excludedMetadata
                    .DistinctBy(item => item.Kind + "\0" + item.PortableValue,
                        StringComparer.Ordinal)
                    .ToImmutableArray();
            if (HasErrors(diagnostics)) return Result(diagnostics);

            AssetPath outputPath = PortableOutputPath(request.OutputFaceGeom);
            var attestation = new ExternalHeadPartFaceGeomExclusionAttestation(
                ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
                default,
                request.Descriptor.DescriptorId,
                outputPath,
                actualHash,
                bytes.LongLength,
                request.IncludedOrdinaryShapes,
                excludedShapes,
                metadata,
                VerifierVersion);
            attestation = attestation with
            {
                AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeAttestationHash(attestation)
            };
            _ = ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(attestation);
            return new ValueTask<ExternalHeadPartFaceGeomExclusionVerificationResult>(
                new ExternalHeadPartFaceGeomExclusionVerificationResult(
                    true, attestation, diagnostics.ToImmutable()));
        }
        catch (Exception exception)
        {
            diagnostics.Add(Error(ExternalHeadPartDiagnosticCodes.FaceGeomContaminated,
                $"The materialized FaceGeom carrier could not be reopened and qualified: {exception.Message}"));
            return Result(diagnostics);
        }
    }

    private static (ImmutableHashSet<string> Structured, ImmutableHashSet<string> Strings)
        CollectCarrierValues(SseNifDocument document)
    {
        var values = ImmutableHashSet.CreateBuilder<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string value in document.TypeNames)
            values.Add(NormalizeComparisonValue(value));
        foreach (string value in document.Strings)
            values.Add(NormalizeComparisonValue(value));
        foreach (SseNifBlock block in document.Blocks)
        {
            values.Add(NormalizeComparisonValue(block.Type));
            if (block.Name is string name)
                values.Add(NormalizeComparisonValue(name));
            foreach (string texture in block.Textures)
                values.Add(NormalizeComparisonValue(texture));
        }
        return (values.ToImmutable(), document.Strings.Select(NormalizeComparisonValue)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static void CheckAbsent(
        (ImmutableHashSet<string> Structured, ImmutableHashSet<string> Strings) carrierValues,
        ImmutableArray<ExternalHeadPartExcludedMetadataEvidence>.Builder evidence,
        string kind,
        string value,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        string normalizedValue = NormalizeComparisonValue(value);
        bool present = normalizedValue.Length < 4
            ? carrierValues.Strings.Contains(normalizedValue)
            : carrierValues.Structured.Any(candidate =>
                ("/" + candidate + "/").Contains("/" + normalizedValue + "/",
                    StringComparison.OrdinalIgnoreCase));
        if (present)
        {
            diagnostics.Add(Error(ExternalHeadPartDiagnosticCodes.FaceGeomContaminated,
                $"Provider metadata '{value}' entered the complete carrier."));
            return;
        }
        evidence.Add(new ExternalHeadPartExcludedMetadataEvidence(kind, value));
    }

    private static string NormalizeComparisonValue(string value) =>
        value.Replace('\\', '/');

    private static AssetPath PortableOutputPath(WorkspacePath output)
    {
        string fullPath = Path.GetFullPath(output.Value);
        string[] segments = fullPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        int dataIndex = Array.FindLastIndex(segments, item =>
            string.Equals(item, "Data", StringComparison.OrdinalIgnoreCase));
        string portable = dataIndex >= 0 && dataIndex < segments.Length - 1
            ? string.Join('/', segments[dataIndex..])
            : Path.GetFileName(fullPath);
        return new AssetPath(portable);
    }

    private static ValueTask<ExternalHeadPartFaceGeomExclusionVerificationResult> Result(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        ValueTask.FromResult(new ExternalHeadPartFaceGeomExclusionVerificationResult(
            false, null, diagnostics.ToImmutable()));

    private static bool HasErrors(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
