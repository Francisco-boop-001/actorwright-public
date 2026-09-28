using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcBuildService
{
    private const int MaximumPackagedGeneratedXyzBytes = 16 * 1024 * 1024;
    private const int MaximumPackagedFaceGeomBytes = 256 * 1024 * 1024;
    private const int MaximumSchema8ManagerCarrierBytes = 64 * 1024 * 1024;

    private static bool ValidateStandaloneAuthorityMode(
        RaceMenuNpcStandaloneAssets assets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch (assets.SchemaVersion)
        {
            case 3 when assets.FinalOutputAuthority is not null &&
                             assets.FaceBakeAuthority is null &&
                             assets.FaceTextureBakeAuthority is null:
                return true;
            case 4 when assets.FinalOutputAuthority is null &&
                             assets.FaceBakeAuthority is not null &&
                             assets.FaceTextureBakeAuthority is null:
                return true;
            case 5 when assets.FinalOutputAuthority is null &&
                             assets.FaceBakeAuthority is not null &&
                             assets.FaceTextureBakeAuthority is not null:
                return true;
            case 6 when assets.FinalOutputAuthority is null &&
                             assets.FaceBakeAuthority is null &&
                             assets.FaceTextureBakeAuthority is null:
                return true;
            case 7 when assets.FinalOutputAuthority is null &&
                             assets.FaceBakeAuthority is null &&
                             assets.FaceTextureBakeAuthority is null &&
                             assets.BodySlidePresetAuthority is not null &&
                             assets.BodyMeshAuthority is not null:
                return true;
            case 8 when assets.FinalOutputAuthority is null &&
                             assets.FaceBakeAuthority is null &&
                             assets.FaceTextureBakeAuthority is null &&
                             ((assets.BodySlidePresetAuthority is null &&
                               assets.BodyMeshAuthority is null) ||
                              (assets.BodySlidePresetAuthority is not null &&
                               assets.BodyMeshAuthority is not null)) &&
                             !assets.ExternalHeadPartDependencies.IsDefaultOrEmpty &&
                             !assets.ExternalHeadPartExclusionAttestations.IsDefaultOrEmpty &&
                             assets.ExternalHeadPartDependencies.Length ==
                             assets.ExternalHeadPartExclusionAttestations.Length:
                return true;
            case 3:
                diagnostics.Add(Error("racemenu-build-schema3-authority",
                    "SchemaVersion 3 requires only the exact admitted final-output oracle."));
                break;
            case 4:
                diagnostics.Add(Error("racemenu-build-schema4-authority",
                    "SchemaVersion 4 requires only the generic face-bake authority and forbids a final-output oracle."));
                break;
            case 5:
                diagnostics.Add(Error("racemenu-build-schema5-authority",
                    "SchemaVersion 5 requires both generic FaceGeom and product face-texture bake authorities and forbids a final-output oracle."));
                break;
            case 6:
                diagnostics.Add(Error("racemenu-build-schema6-authority",
                    "SchemaVersion 6 requires only the exact selected CharGen bundle and forbids bake or final-output authorities."));
                break;
            case 7:
                diagnostics.Add(Error("racemenu-build-schema7-authority",
                    "SchemaVersion 7 requires the exact selected CharGen bundle plus external BodySlide preset and body mesh authorities, and forbids bake or final-output authorities."));
                break;
            case 8:
                diagnostics.Add(Error("racemenu-build-schema8-authority",
                    "SchemaVersion 8 requires paired external head-part descriptor and FaceGeom exclusion-attestation arrays, optional paired BodySlide authorities, and forbids bake or final-output authorities."));
                break;
            default:
                diagnostics.Add(Error("racemenu-build-schema-unsupported",
                    $"Standalone-asset schemaVersion {assets.SchemaVersion} is unsupported."));
                break;
        }
        return false;
    }

    private static bool ValidateSchema8AuthorityBinding(
        RaceMenuNpcExecutionRequest request,
        RaceMenuNpcStandaloneAssets assets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (assets.SchemaVersion != 8)
            return true;

        if (request.ManagerOwnedFaceGeomCarrier is not { } managerCarrier)
        {
            diagnostics.Add(Error(
                "racemenu-build-schema8-manager-carrier-required",
                "SchemaVersion 8 requires the Manager-owned FaceGeom carrier authority."));
        }
        else if (!managerCarrier.FaceGeom.Value.EndsWith(
                     ".nif", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "racemenu-build-schema8-manager-carrier-extension",
                "The schema-8 Manager-owned FaceGeom carrier must use the .nif extension."));
        }

        if (!RaceMenuJslotProbeToken.IsValid(request.JslotOutputBindingProbe) &&
            request.SelectedDependencyManifest is null)
        {
            diagnostics.Add(Error(
                "racemenu-build-schema8-selected-dependency-manifest-required",
                "SchemaVersion 8 requires the Manager-authored selected dependency manifest authority."));
        }

        if (request.ExternalHeadPartDependencies.IsDefaultOrEmpty ||
            request.ExternalHeadPartExclusionAttestations.IsDefaultOrEmpty ||
            request.ExternalHeadPartDependencies.Length !=
            request.ExternalHeadPartExclusionAttestations.Length)
        {
            diagnostics.Add(Error(
                "racemenu-build-schema8-request-evidence-pair",
                "SchemaVersion 8 requires paired in-memory external head-part descriptors and FaceGeom exclusion attestations."));
            return !HasErrors(diagnostics);
        }

        if (assets.ExternalHeadPartDependencies.Length !=
                request.ExternalHeadPartDependencies.Length ||
            assets.ExternalHeadPartExclusionAttestations.Length !=
                request.ExternalHeadPartExclusionAttestations.Length)
        {
            diagnostics.Add(Error(
                "racemenu-build-schema8-evidence-drift",
                "SchemaVersion 8 request evidence must contain the exact descriptor and attestation pair admitted by the standalone manifest."));
            return !HasErrors(diagnostics);
        }

        var requestDescriptors = new Dictionary<string,
            ExternalHeadPartDependencyDescriptor>(StringComparer.Ordinal);
        foreach (ExternalHeadPartDependencyDescriptor descriptor in
                 request.ExternalHeadPartDependencies)
        {
            if (descriptor is null)
            {
                diagnostics.Add(Error(
                    "racemenu-build-schema8-descriptor-null",
                    "SchemaVersion 8 request evidence cannot contain a null descriptor."));
                continue;
            }
            if (!requestDescriptors.TryAdd(
                    descriptor.DescriptorId.Value,
                    descriptor))
            {
                diagnostics.Add(Error(
                    "racemenu-build-schema8-descriptor-duplicate",
                    $"SchemaVersion 8 request descriptor '{descriptor.DescriptorId}' occurs more than once."));
            }
        }
        var requestAttestations = new Dictionary<string,
            ExternalHeadPartFaceGeomExclusionAttestation>(StringComparer.Ordinal);
        foreach (ExternalHeadPartFaceGeomExclusionAttestation attestation in
                 request.ExternalHeadPartExclusionAttestations)
        {
            if (attestation is null)
            {
                diagnostics.Add(Error(
                    "racemenu-build-schema8-attestation-null",
                    "SchemaVersion 8 request evidence cannot contain a null attestation."));
                continue;
            }
            if (!requestAttestations.TryAdd(
                    attestation.DescriptorId.Value,
                    attestation))
            {
                diagnostics.Add(Error(
                    "racemenu-build-schema8-attestation-duplicate",
                    $"SchemaVersion 8 request attestation for descriptor '{attestation.DescriptorId}' occurs more than once."));
            }
        }
        foreach (ExternalHeadPartDependencyDescriptor descriptor in
                 assets.ExternalHeadPartDependencies)
        {
            if (!requestDescriptors.TryGetValue(
                    descriptor.DescriptorId.Value,
                    out ExternalHeadPartDependencyDescriptor? requestDescriptor) ||
                !CanonicalDescriptorEquals(requestDescriptor, descriptor))
            {
                diagnostics.Add(Error(
                    "racemenu-build-schema8-descriptor-mismatch",
                    $"SchemaVersion 8 descriptor '{descriptor.DescriptorId}' drifted between the manifest and Manager request."));
            }
        }
        foreach (ExternalHeadPartFaceGeomExclusionAttestation attestation in
                 assets.ExternalHeadPartExclusionAttestations)
        {
            if (!requestAttestations.TryGetValue(
                    attestation.DescriptorId.Value,
                    out ExternalHeadPartFaceGeomExclusionAttestation? requestAttestation) ||
                !CanonicalAttestationEquals(requestAttestation, attestation))
            {
                diagnostics.Add(Error(
                    "racemenu-build-schema8-attestation-mismatch",
                    $"SchemaVersion 8 attestation for descriptor '{attestation.DescriptorId}' drifted between the manifest and Manager request."));
            }
            if (request.ManagerOwnedFaceGeomCarrier is { } carrier &&
                (attestation.OutputFaceGeomSha256 != carrier.FaceGeomSha256 ||
                 requestAttestation?.OutputFaceGeomSha256 != carrier.FaceGeomSha256))
            {
                diagnostics.Add(Error(
                    "racemenu-build-schema8-attestation-carrier-mismatch",
                    $"SchemaVersion 8 attestation for descriptor '{attestation.DescriptorId}' is not bound to the exact Manager carrier."));
            }
        }

        return !HasErrors(diagnostics);
    }

    private static bool CanonicalDescriptorEquals(
        ExternalHeadPartDependencyDescriptor left,
        ExternalHeadPartDependencyDescriptor right)
    {
        try
        {
            return ExternalHeadPartDependencyDescriptorCodec
                .SerializeDescriptor(left)
                .AsSpan()
                .SequenceEqual(ExternalHeadPartDependencyDescriptorCodec
                    .SerializeDescriptor(right));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool CanonicalAttestationEquals(
        ExternalHeadPartFaceGeomExclusionAttestation left,
        ExternalHeadPartFaceGeomExclusionAttestation right)
    {
        try
        {
            return ExternalHeadPartDependencyDescriptorCodec
                .SerializeAttestation(left)
                .AsSpan()
                .SequenceEqual(ExternalHeadPartDependencyDescriptorCodec
                    .SerializeAttestation(right));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async ValueTask<bool> VerifySchema8ManagerCarrierAsync(
        RaceMenuNpcExecutionRequest request,
        RaceMenuNpcStandaloneAssets assets,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (assets.SchemaVersion != 8 ||
            request.ManagerOwnedFaceGeomCarrier is not { } carrier)
            return assets.SchemaVersion != 8;

        byte[]? bytes = await ReadHashBoundOrdinaryFileAsync(
            carrier.FaceGeom,
            carrier.FaceGeomSha256,
            MaximumSchema8ManagerCarrierBytes,
            "racemenu-build-schema8-manager-carrier",
            "schema-8 Manager-owned FaceGeom carrier",
            diagnostics,
            cancellationToken);
        if (bytes is null)
            return false;

        long length = bytes.LongLength;
        foreach (ExternalHeadPartFaceGeomExclusionAttestation attestation in
                 assets.ExternalHeadPartExclusionAttestations)
        {
            if (attestation.OutputFaceGeomSha256 != carrier.FaceGeomSha256 ||
                attestation.OutputFaceGeomByteLength != length)
            {
                diagnostics.Add(Error(
                    "racemenu-build-schema8-attestation-carrier-mismatch",
                    $"SchemaVersion 8 attestation for descriptor '{attestation.DescriptorId}' does not match the reopened Manager carrier hash and byte length."));
            }
        }
        return !HasErrors(diagnostics);
    }

    private async ValueTask<WorkspacePath?> MaterializeSchema8ManagerCarrierAsync(
        RaceMenuManagerOwnedFaceGeomCarrierAuthority carrier,
        WorkspacePath stagingRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        byte[]? bytes = await ReadHashBoundOrdinaryFileAsync(
            carrier.FaceGeom,
            carrier.FaceGeomSha256,
            MaximumSchema8ManagerCarrierBytes,
            "racemenu-build-schema8-manager-carrier",
            "schema-8 Manager-owned FaceGeom carrier",
            diagnostics,
            cancellationToken);
        if (bytes is null || HasErrors(diagnostics))
            return null;

        var destination = new WorkspacePath(Path.Combine(
            stagingRoot.Value,
            "manager-qualified-complete-carrier.nif"));
        diagnostics.AddRange(WorkspacePolicy.EvaluateReadRoot(
            LaboratoryRoot,
            destination));
        if (!destination.IsUnder(stagingRoot) ||
            File.Exists(destination.Value) ||
            Directory.Exists(destination.Value) ||
            HasErrors(diagnostics))
        {
            diagnostics.Add(Error(
                "racemenu-build-schema8-manager-carrier-output",
                "The schema-8 Manager-carrier staging destination is invalid or already exists."));
            return null;
        }

        await using (var stream = new FileStream(
            destination.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        if (await ReadHashBoundOrdinaryFileAsync(
                destination,
                carrier.FaceGeomSha256,
                MaximumSchema8ManagerCarrierBytes,
                "racemenu-build-schema8-manager-carrier-stage",
                "staged schema-8 Manager-owned FaceGeom carrier",
                diagnostics,
                cancellationToken) is null)
            return null;

        diagnostics.Add(new Diagnostic(
            "racemenu-build-schema8-manager-carrier-selected",
            DiagnosticSeverity.Info,
            "The independently reopened Manager-owned FaceGeom carrier is the schema-8 exact NIF source; provider NIF/TRI/DDS/XML bytes remain external."));
        return destination;
    }

    private async ValueTask<bool> AddFaceGeomEvidenceAssetsAsync(
        RaceMenuNpcFaceBakeAuthorityReference faceBake,
        RaceMenuNpcFaceGeomBuildArtifact artifact,
        WorkspacePath stagingRoot,
        ImmutableArray<BlankNpcTransitivePackageAsset>.Builder packageAssets,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (artifact.AuthorityManifestSha256 != faceBake.ExpectedManifestSha256 ||
            artifact.RuntimeAuthority ||
            artifact.Shapes.IsDefaultOrEmpty ||
            !artifact.OutputNif.IsUnder(stagingRoot))
        {
            diagnostics.Add(Error("racemenu-build-facegeom-artifact-invalid",
                "The FaceGeom artifact does not retain the exact authority, non-runtime status, staged output, and generated-shape evidence."));
            return false;
        }

        if (await ReadHashBoundOrdinaryFileAsync(
                artifact.OutputNif,
                artifact.OutputNifSha256,
                MaximumPackagedFaceGeomBytes,
                "racemenu-build-facegeom-output",
                "verified product FaceGeom output",
                diagnostics,
                cancellationToken) is null)
        {
            return false;
        }

        var pending = ImmutableArray.CreateBuilder<BlankNpcTransitivePackageAsset>(
            artifact.Shapes.Length);
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shape in artifact.Shapes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shape.GeneratedXyzSha256 != shape.FinalPositionSha256 ||
                !shape.GeneratedXyzFile.IsUnder(stagingRoot) ||
                !sourcePaths.Add(shape.GeneratedXyzFile.Value))
            {
                diagnostics.Add(Error("racemenu-build-facegeom-xyz-evidence-invalid",
                    $"Generated XYZ evidence for carrier '{shape.CarrierShapeName}' is not a unique hash-bound staging file."));
                continue;
            }

            if (!TryCreateGeneratedXyzDestination(
                    shape.CarrierShapeName, diagnostics, out var destination))
            {
                continue;
            }
            if (!destinations.Add(destination.Value))
            {
                diagnostics.Add(Error("racemenu-build-facegeom-xyz-destination-duplicate",
                    $"Carrier '{shape.CarrierShapeName}' maps to duplicate evidence destination '{destination.Value}'."));
                continue;
            }

            if (await ReadHashBoundOrdinaryFileAsync(
                    shape.GeneratedXyzFile,
                    shape.GeneratedXyzSha256,
                    MaximumPackagedGeneratedXyzBytes,
                    "racemenu-build-facegeom-xyz",
                    $"generated XYZ evidence for carrier '{shape.CarrierShapeName}'",
                    diagnostics,
                    cancellationToken) is null)
            {
                continue;
            }

            pending.Add(new BlankNpcTransitivePackageAsset(
                shape.GeneratedXyzFile,
                shape.GeneratedXyzSha256,
                destination));
        }

        if (HasErrors(diagnostics) || pending.Count != artifact.Shapes.Length)
            return false;

        packageAssets.AddRange(pending);
        diagnostics.Add(new Diagnostic("racemenu-build-facegeom-evidence-staged",
            DiagnosticSeverity.Info,
            $"Added the face-bake authority and {pending.Count} generated XYZ files to the immutable package inventory before staging cleanup."));
        return true;
    }

    private static bool TryCreateGeneratedXyzDestination(
        string carrierShapeName,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out AssetPath destination)
    {
        destination = default;
        if (string.IsNullOrWhiteSpace(carrierShapeName) ||
            carrierShapeName.Length > 128 ||
            carrierShapeName != carrierShapeName.Trim() ||
            carrierShapeName.Any(character => character is < ' ' or > '~' ||
                                                character is '/' or '\\' or ':' or '.'))
        {
            diagnostics.Add(Error("racemenu-build-facegeom-carrier-name-unsafe",
                "Carrier shape names used for package evidence must be trimmed printable ASCII without path, stream, or extension separators."));
            return false;
        }

        var safeName = new StringBuilder(carrierShapeName.Length);
        var separatorPending = false;
        foreach (var character in carrierShapeName)
        {
            if (character is >= 'A' and <= 'Z')
            {
                if (separatorPending && safeName.Length > 0) safeName.Append('-');
                separatorPending = false;
                safeName.Append(char.ToLowerInvariant(character));
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (separatorPending && safeName.Length > 0) safeName.Append('-');
                separatorPending = false;
                safeName.Append(character);
            }
            else
            {
                separatorPending = safeName.Length > 0;
            }
        }

        if (safeName.Length is 0 or > 96)
        {
            diagnostics.Add(Error("racemenu-build-facegeom-carrier-name-unsafe",
                "Carrier shape names must produce a non-empty safe evidence name no longer than 96 characters."));
            return false;
        }

        destination = new AssetPath(
            $"NPCManager/Evidence/FaceGeom/generated-xyz/{safeName}.xyz");
        return true;
    }
}
