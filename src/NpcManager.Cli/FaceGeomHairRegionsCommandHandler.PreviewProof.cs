using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed partial class FaceGeomHairRegionsCommandHandler
{
    private const long MaximumPreviewImageEncodedBytes =
        32L * 1024L * 1024L;
    private const long MaximumPreviewEvidenceEncodedBytes =
        1024L * 1024L;
    private const long MaximumPreviewBundleEncodedBytes =
        128L * 1024L * 1024L;

    private CommandExitCode InvalidPreviewResult(
        ParsedCommand command,
        string message,
        ImmutableArray<WorkspacePath> survivors = default)
    {
        bool security = IsSecurityMessage(message);
        if (survivors.IsDefault)
            survivors = [];
        ImmutableArray<Diagnostic> diagnostics =
            survivors.IsDefaultOrEmpty
                ? [
                    new Diagnostic(
                        security
                            ? "facegeom-hair-regions-security-refused"
                            : "facegeom-hair-regions-preview-evidence-invalid",
                        DiagnosticSeverity.Error,
                        message)
                ]
                : [
                    new Diagnostic(
                        security
                            ? "facegeom-hair-regions-security-refused"
                            : "facegeom-hair-regions-preview-evidence-invalid",
                        DiagnosticSeverity.Error,
                        message),
                    new Diagnostic(
                        "facegeom-hair-regions-preview-cleanup-failed",
                        DiagnosticSeverity.Error,
                        "The invalid preview result rollback retained exact private-root survivors.")
                ];
        return Respond(
            command,
            "REFUSED",
            survivors.Select(item =>
                    SuggestedArtifact(
                        "surviving",
                        item))
                .ToImmutableArray(),
            diagnostics,
            security
                ? DiagnosticExitCodeClassifier.KnownSecurityRefusal
                : CommandExitCode.ValidationFailure);
    }

    private async ValueTask<string?> ValidatePreviewProofAsync(
        FaceGeomHairRegionsPreviewResult result,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            requestDocument,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            proposalDocument,
        FaceGeomHairRegionsProposalMaterialization materialization,
        ReviewedGameIntakeDocumentAuthority intakeAuthority,
        WorkspacePath outputRoot,
        FaceGeomHairRegionsPinnedDirectory previewRoot,
        INpcVisualPreviewVisualValidator visualValidator,
        CancellationToken cancellationToken)
    {
        try
        {
            if (result.VisualAuthority ||
                result.RuntimeAuthority ||
                result.Evidence is null ||
                result.Evidence.StagedFaceGeomSha256 !=
                materialization.Candidate.Sha256 ||
                result.Evidence.ProposalSha256 !=
                proposalDocument.Sha256 ||
                result.Evidence.IntakeSha256 !=
                intakeAuthority.Document.Sha256)
                return
                    "Preview evidence does not bind the exact candidate, proposal, intake, and authority=false contract.";

            ImmutableArray<FaceGeomHairRegionAssignment>
                assignments =
                    requestDocument.Value.Assignments;
            int expectedArtifactCount = checked(
                3 + assignments.Length * 2);
            if (result.Artifacts.Length != expectedArtifactCount)
                return
                    "Preview proof does not contain the exact closed artifact count.";
            if (result.Artifacts.Count(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .CombinedFace) != 1 ||
                result.Artifacts.Count(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .ContactSheet) != 1 ||
                result.Artifacts.Count(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .EvidenceDocument) != 1)
                return
                    "Preview proof requires exactly one combined face, contact sheet, and evidence document.";

            var assignmentIds = assignments
                .Select(item => item.StructuralId)
                .ToHashSet(StringComparer.Ordinal);
            string[] thumbnailIds = result.Artifacts
                .Where(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionThumbnail)
                .Select(item => item.StructuralId ?? string.Empty)
                .ToArray();
            string[] maskIds = result.Artifacts
                .Where(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionMask)
                .Select(item => item.StructuralId ?? string.Empty)
                .ToArray();
            if (thumbnailIds.Length != assignments.Length ||
                maskIds.Length != assignments.Length ||
                thumbnailIds.Distinct(StringComparer.Ordinal)
                    .Count() != assignments.Length ||
                maskIds.Distinct(StringComparer.Ordinal)
                    .Count() != assignments.Length ||
                !thumbnailIds.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(assignmentIds) ||
                !maskIds.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(assignmentIds))
                return
                    "Preview proof requires one unique thumbnail and mask for every assignment.";

            long cumulativeEncodedBytes = 0;
            foreach (FaceGeomHairRegionsPreviewArtifact artifact in
                     result.Artifacts)
            {
                long perArtifactLimit =
                    artifact.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .EvidenceDocument
                        ? MaximumPreviewEvidenceEncodedBytes
                        : MaximumPreviewImageEncodedBytes;
                if (artifact.ByteLength is <= 0 ||
                    artifact.ByteLength > perArtifactLimit)
                    return
                        "Preview artifact exceeds its per-artifact encoded byte limit.";
                cumulativeEncodedBytes = checked(
                    cumulativeEncodedBytes +
                    artifact.ByteLength);
            }
            if (cumulativeEncodedBytes >
                MaximumPreviewBundleEncodedBytes)
                return
                    "Preview artifacts exceed the cumulative encoded byte limit.";

            var artifactPaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var observedBytes = new Dictionary<string, byte[]>(
                StringComparer.OrdinalIgnoreCase);
            var pngInspections = new Dictionary<
                string,
                FaceGeomHairRegionsPngInspection>(
                StringComparer.OrdinalIgnoreCase);
            foreach (FaceGeomHairRegionsPreviewArtifact artifact in
                     result.Artifacts)
            {
                bool isRegion = artifact.Kind is
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionThumbnail or
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionMask;
                bool isEvidence =
                    artifact.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .EvidenceDocument;
                if (isRegion !=
                        !string.IsNullOrWhiteSpace(
                            artifact.StructuralId) ||
                    !artifactPaths.Add(artifact.Path.Value) ||
                    artifact.ByteLength <= 0 ||
                    isEvidence &&
                    artifact.NonEmptyPixelCount is not null ||
                    !isEvidence &&
                    artifact.NonEmptyPixelCount is not > 0)
                    return
                        "Preview artifact roles, structural IDs, paths, lengths, or pixel counts are invalid.";
                boundary.RequireExistingFileBeneath(
                    outputRoot,
                    artifact.Path,
                    "preview artifact");
                long encodedLimit =
                    isEvidence
                        ? MaximumPreviewEvidenceEncodedBytes
                        : MaximumPreviewImageEncodedBytes;
                byte[] observed =
                    await previewRoot.ReadExactFileAsync(
                        artifact.Path,
                        encodedLimit,
                        "preview artifact readback",
                        cancellationToken);
                if (observed.LongLength !=
                        artifact.ByteLength ||
                    new Sha256Hash(Convert.ToHexString(
                        SHA256.HashData(observed))) !=
                    artifact.Sha256)
                    return
                        "Preview artifact length or SHA-256 does not match independent readback.";
                observedBytes.Add(
                    artifact.Path.Value,
                    observed);
                if (!isEvidence)
                {
                    if (!Path.GetExtension(artifact.Path.Value)
                            .Equals(
                                ".png",
                                StringComparison.OrdinalIgnoreCase))
                        return
                            "Every preview image artifact must use a .png path.";
                    FaceGeomHairRegionsPngInspection inspection =
                        FaceGeomHairRegionsPreviewArtifactInspector
                            .InspectPng(observed);
                    pngInspections.Add(
                        artifact.Path.Value,
                        inspection);
                    if (inspection.NonEmptyPixelCount <= 0 ||
                        inspection.NonEmptyPixelCount !=
                        artifact.NonEmptyPixelCount)
                        return
                            "Preview PNG non-empty pixels do not match independent decode.";
                }
                else if (!Path.GetExtension(artifact.Path.Value)
                             .Equals(
                                 ".json",
                                 StringComparison.OrdinalIgnoreCase))
                    return
                        "The preview evidence artifact must use a .json path.";
            }

            FaceGeomHairRegionsPreviewArtifact combinedFace =
                result.Artifacts.Single(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .CombinedFace);
            FaceGeomHairRegionsPngInspection combinedInspection =
                pngInspections[combinedFace.Path.Value];
            if (combinedInspection.Width != 900 ||
                combinedInspection.Height != 900)
                return
                    "The production combined-face preview must decode as exactly 900x900 pixels.";
            FaceGeomHairRegionsPreviewArtifact firstMask =
                result.Artifacts.First(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionMask);
            FaceGeomHairRegionsPngInspection maskInspection =
                pngInspections[firstMask.Path.Value];
            NpcVisualPreviewVisualEvidence visualEvidence =
                await visualValidator.ValidateEncodedAsync(
                    new NpcVisualPreviewView(
                        "face-front",
                        combinedFace.Path,
                        combinedFace.Sha256,
                        firstMask.Path,
                        firstMask.Sha256,
                        combinedInspection.Width,
                        combinedInspection.Height),
                    observedBytes[
                        combinedFace.Path.Value],
                    cancellationToken);
            if (visualEvidence.Diagnostics.IsDefault ||
                visualEvidence.Diagnostics.Any(item =>
                    item is null) ||
                visualEvidence.Diagnostics.Any(item =>
                    item.Severity ==
                    DiagnosticSeverity.Error) ||
                visualEvidence.DetectedFaceCount != 1 ||
                visualEvidence.LandmarkCount != 478 ||
                visualEvidence.SemanticAnchorCount != 31 ||
                !visualEvidence.EyesNoseAndMouthBounded ||
                result.Evidence.DetectedFaceCount !=
                    visualEvidence.DetectedFaceCount ||
                result.Evidence.LandmarkCount !=
                    visualEvidence.LandmarkCount ||
                result.Evidence.SemanticAnchorCount !=
                    visualEvidence.SemanticAnchorCount ||
                maskInspection.NonEmptyPixelCount <= 0)
                return
                    "Preview face, 478-landmark, 31-anchor, and independent mask proof must derive from the actual 900x900 combined-face bytes.";

            FaceGeomHairRegionsPreviewArtifact evidenceArtifact =
                result.Artifacts.Single(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind
                        .EvidenceDocument);
            if (evidenceArtifact.Sha256 !=
                result.Evidence.EvidenceDocumentSha256)
                return
                    "Preview evidence-document SHA-256 is not bound to its artifact.";
            StrictJsonDocumentAuthority<
                FaceGeomHairRegionsPreviewEvidenceDocument>
                evidenceDocument = documents.DecodePreviewEvidence(
                    observedBytes[evidenceArtifact.Path.Value],
                    evidenceArtifact.Sha256);
            FaceGeomHairRegionsPreviewEvidenceDocument evidence =
                evidenceDocument.Value;
            if (evidence.StagedFaceGeomSha256 !=
                    result.Evidence.StagedFaceGeomSha256 ||
                evidence.ProposalSha256 !=
                    result.Evidence.ProposalSha256 ||
                evidence.IntakeSha256 !=
                    result.Evidence.IntakeSha256 ||
                evidence.RendererSha256 !=
                    result.Evidence.RendererSha256 ||
                evidence.TextureFingerprintSha256 !=
                    result.Evidence.TextureFingerprintSha256 ||
                evidence.DetectedFaceCount !=
                    result.Evidence.DetectedFaceCount ||
                evidence.LandmarkCount !=
                    result.Evidence.LandmarkCount ||
                evidence.SemanticAnchorCount !=
                    result.Evidence.SemanticAnchorCount ||
                !RenderAuthoritiesEqual(
                    evidence.RenderAuthority,
                    result.Evidence.RenderAuthority) ||
                !ArtifactEvidenceMatches(
                    evidence.Artifacts,
                    result.Artifacts,
                    pngInspections) ||
                evidence.VisualAuthority ||
                evidence.RuntimeAuthority)
                return
                    "Preview evidence JSON does not exactly match the typed authority-false result.";
            ValidateClosedOutputTree(
                previewRoot,
                outputRoot,
                artifactPaths);
            return null;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                OverflowException)
        {
            return exception.Message;
        }
    }

    private static bool RenderAuthoritiesEqual(
        FaceGeomHairRegionsRenderAuthority left,
        FaceGeomHairRegionsRenderAuthority right)
    {
        if (left.BlenderSha256 != right.BlenderSha256 ||
            left.PyniflyArchiveSha256 !=
                right.PyniflyArchiveSha256 ||
            left.PyniflySourceProfileSha256 !=
                right.PyniflySourceProfileSha256 ||
            left.RendererScriptSha256 !=
                right.RendererScriptSha256 ||
            left.TexconvSha256 != right.TexconvSha256 ||
            left.TextureSourceFingerprintSha256 !=
                right.TextureSourceFingerprintSha256 ||
            left.LoadedTextureObservationFingerprintSha256 !=
                right.LoadedTextureObservationFingerprintSha256 ||
            left.PyniflyModuleSourceFingerprintSha256 !=
                right.PyniflyModuleSourceFingerprintSha256 ||
            left.PyniflyModuleCount !=
                right.PyniflyModuleCount ||
            left.OriginalProfileFingerprintSha256 !=
                right.OriginalProfileFingerprintSha256 ||
            left.OriginalProfileFileCount !=
                right.OriginalProfileFileCount ||
            left.PyniflyModules.IsDefault ||
            right.PyniflyModules.IsDefault ||
            !left.PyniflyModules.SequenceEqual(
                right.PyniflyModules) ||
            left.Textures.IsDefault ||
            right.Textures.IsDefault ||
            left.Textures.Length != right.Textures.Length)
            return false;
        for (int index = 0;
             index < left.Textures.Length;
             index++)
        {
            FaceGeomHairTextureEvidence first =
                left.Textures[index];
            FaceGeomHairTextureEvidence second =
                right.Textures[index];
            if (first.AssetPath != second.AssetPath ||
                first.ProviderKind != second.ProviderKind ||
                first.Provider != second.Provider ||
                first.SourceSha256 != second.SourceSha256 ||
                first.SourceBytes != second.SourceBytes ||
                first.DecodedPreviewSha256 !=
                    second.DecodedPreviewSha256 ||
                first.DecodedPreviewBytes !=
                    second.DecodedPreviewBytes ||
                first.DecodeKind != second.DecodeKind ||
                first.Bindings.IsDefault ||
                second.Bindings.IsDefault ||
                !first.Bindings.SequenceEqual(
                    second.Bindings))
                return false;
        }
        return true;
    }

    private static bool ArtifactEvidenceMatches(
        ImmutableArray<FaceGeomHairArtifactEvidence>
            evidence,
        ImmutableArray<FaceGeomHairRegionsPreviewArtifact>
            artifacts,
        Dictionary<string, FaceGeomHairRegionsPngInspection>
            inspections)
    {
        FaceGeomHairRegionsPreviewArtifact[] expected =
            artifacts
                .Where(item =>
                    item.Kind !=
                    FaceGeomHairRegionsPreviewArtifactKind
                        .EvidenceDocument)
                .OrderBy(item => item.Kind switch
                {
                    FaceGeomHairRegionsPreviewArtifactKind
                        .CombinedFace => 0,
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionThumbnail => 1,
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionMask => 2,
                    FaceGeomHairRegionsPreviewArtifactKind
                        .ContactSheet => 3,
                    _ => 4
                })
                .ThenBy(
                    item => item.StructuralId ?? "",
                    StringComparer.Ordinal)
                .ToArray();
        if (evidence.IsDefault ||
            evidence.Length != expected.Length)
            return false;
        for (int index = 0;
             index < expected.Length;
             index++)
        {
            FaceGeomHairRegionsPreviewArtifact artifact =
                expected[index];
            FaceGeomHairArtifactEvidence row =
                evidence[index];
            FaceGeomHairRegionsPngInspection inspection =
                inspections[artifact.Path.Value];
            if (row.Kind != artifact.Kind ||
                row.StructuralId != artifact.StructuralId ||
                row.RelativePath !=
                    Path.GetFileName(
                        artifact.Path.Value) ||
                row.Sha256 != artifact.Sha256 ||
                row.Bytes != artifact.ByteLength ||
                row.Width != inspection.Width ||
                row.Height != inspection.Height ||
                row.NonEmptyPixelCount !=
                    inspection.NonEmptyPixelCount)
                return false;
        }
        return true;
    }

    private void ValidateClosedOutputTree(
        FaceGeomHairRegionsPinnedDirectory previewRoot,
        WorkspacePath outputRoot,
        HashSet<string> artifactPaths)
    {
        var allowedDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string artifactPath in artifactPaths)
        {
            string? current = Path.GetDirectoryName(artifactPath);
            while (current is not null &&
                   !string.Equals(
                       current,
                       outputRoot.Value,
                       StringComparison.OrdinalIgnoreCase))
            {
                allowedDirectories.Add(current);
                current = Path.GetDirectoryName(current);
            }
            if (current is null)
                throw new UnauthorizedAccessException(
                    "A preview artifact directory escaped the output root.");
        }

        var observedFiles =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        foreach (FaceGeomHairRegionsPinnedTreeEntry entry in
                 previewRoot.EnumerateTree())
        {
            if (entry.IsDirectory)
            {
                if (!allowedDirectories.Contains(
                        entry.Path.Value))
                    throw new InvalidDataException(
                        "The preview output tree contains an unlisted directory.");
                continue;
            }
            boundary.RequireExistingFileBeneath(
                outputRoot,
                entry.Path,
                "preview output tree file");
            if (!artifactPaths.Contains(entry.Path.Value))
                throw new InvalidDataException(
                    "The preview output tree contains an unlisted file.");
            observedFiles.Add(entry.Path.Value);
        }
        if (!observedFiles.SetEquals(artifactPaths))
            throw new InvalidDataException(
                "The preview output tree does not exactly equal the declared artifact set.");
    }

    private static string PreviewRole(
        FaceGeomHairRegionsPreviewArtifact artifact)
    {
        string kind = artifact.Kind switch
        {
            FaceGeomHairRegionsPreviewArtifactKind.CombinedFace =>
                "combinedFace",
            FaceGeomHairRegionsPreviewArtifactKind.RegionThumbnail =>
                "regionThumbnail",
            FaceGeomHairRegionsPreviewArtifactKind.RegionMask =>
                "regionMask",
            FaceGeomHairRegionsPreviewArtifactKind.ContactSheet =>
                "contactSheet",
            FaceGeomHairRegionsPreviewArtifactKind.EvidenceDocument =>
                "evidenceDocument",
            _ => throw new InvalidDataException(
                "Preview artifact kind is outside the closed contract.")
        };
        return artifact.StructuralId is null
            ? kind
            : $"{kind}:{artifact.StructuralId}";
    }
}
