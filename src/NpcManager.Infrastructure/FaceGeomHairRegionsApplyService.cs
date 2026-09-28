using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class FaceGeomHairRegionsApplyService
{
    private readonly IFaceGeomHairRegionsIndependentVerifier
        independentVerifier;
    private readonly FaceGeomHairRegionsDocumentCodec documents;
    private readonly FaceGeomHairRegionsProposalMaterializer
        materializer;
    private readonly FaceGeomHairRegionsWorkspaceBoundary boundary;

    public FaceGeomHairRegionsApplyService(
        WorkspacePath workspaceRoot,
        IFaceGeomHairRegionsIndependentVerifier independentVerifier)
        : this(
            workspaceRoot,
            independentVerifier,
            new FaceGeomHairRegionsDocumentCodec())
    {
    }

    public FaceGeomHairRegionsApplyService(
        WorkspacePath workspaceRoot,
        IFaceGeomHairRegionsIndependentVerifier independentVerifier,
        FaceGeomHairRegionsDocumentCodec documents)
    {
        this.independentVerifier = independentVerifier ??
            throw new ArgumentNullException(
                nameof(independentVerifier));
        this.documents = documents ??
            throw new ArgumentNullException(nameof(documents));
        materializer =
            new FaceGeomHairRegionsProposalMaterializer(
                workspaceRoot,
                documents);
        boundary = documents.WorkspaceBoundary;
        if (boundary.WorkspaceRoot != workspaceRoot)
            throw new ArgumentException(
                "The document boundary does not match the Apply workspace.",
                nameof(documents));
    }

    public ValueTask<FaceGeomHairRegionsProposalMaterialization>
        MaterializeAsync(
            StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
                requestDocument,
            StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
                proposalDocument,
            CancellationToken cancellationToken) =>
        materializer.MaterializeAsync(
            requestDocument,
            proposalDocument,
            cancellationToken);

    public async ValueTask<FaceGeomHairRegionsApplyResult> ApplyAsync(
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            requestDocument,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            proposalDocument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestDocument);
        ArgumentNullException.ThrowIfNull(proposalDocument);
        FaceGeomHairRegionsProposal proposal =
            proposalDocument.Value;
        FaceGeomHairRegionsOwnedFile? outputOwned = null;
        FaceGeomHairRegionsOwnedFile? manifestOwned = null;
        try
        {
            FaceGeomHairRegionsProposalMaterialization
                materialization = await materializer.MaterializeAsync(
                    requestDocument,
                    proposalDocument,
                    cancellationToken);
            proposal = documents.ValidateProposal(proposalDocument);
            byte[] source =
                materialization.SourceBytes.ToArray();
            byte[] output =
                materialization.CandidateBytes.ToArray();
            ImmutableArray<int> changed =
                materialization.ChangedByteOffsets;
            FaceGeomHairRegionsFingerprints outputFingerprints =
                materialization.CandidateFingerprints;

            string outputTemporary = TemporaryBeside(
                proposal.Output.Value);
            outputOwned = boundary.CreateOwnedFile(
                new WorkspacePath(outputTemporary),
                proposal.Output,
                "FaceGeom output");
            byte[] independentReadback =
                await outputOwned.WriteAndReadbackAsync(
                output,
                FaceGeomHairRegionsSupport.MaximumSourceBytes,
                cancellationToken);
            if (!independentReadback.AsSpan()
                    .SequenceEqual(output))
                throw new InvalidDataException(
                    "The retained-handle FaceGeom output readback changed before verification.");
            FaceGeomHairRegionsVerification verification =
                independentVerifier.Verify(
                    source,
                    independentReadback,
                    proposal);
            if (verification.Diagnostics.IsDefault ||
                verification.ChangedByteOffsets.IsDefault ||
                verification.Diagnostics.Any(item =>
                    item is null) ||
                verification.ObservedOutput !=
                    proposal.ExpectedOutput)
                throw new InvalidDataException(
                    "Independent FaceGeom verification returned missing result collections.");
            bool verificationHasErrors =
                verification.Diagnostics.Any(item =>
                    item.Severity ==
                    DiagnosticSeverity.Error);
            if (verification.Succeeded ==
                    verificationHasErrors ||
                !verification.Succeeded)
                throw new InvalidDataException(
                    verification.Diagnostics
                        .FirstOrDefault(
                            diagnostic =>
                                diagnostic.Severity ==
                                DiagnosticSeverity.Error)
                        ?.Message ??
                    "Independent FaceGeom verification failed.");

            var manifest = new FaceGeomHairRegionsManifest(
                FaceGeomHairRegionSchemas.Manifest,
                proposalDocument.Sha256,
                proposal.Source,
                proposal.ExpectedOutput,
                proposal.AuthorizedEnvelopes,
                changed,
                outputFingerprints,
                [],
                RuntimeAuthority: false);
            StrictJsonDocumentAuthority<FaceGeomHairRegionsManifest>
                manifestDocument =
                    documents.BindManifest(manifest);
            byte[] manifestBytes =
                manifestDocument.Utf8Json.ToArray();
            string manifestTemporary = TemporaryBeside(
                proposal.Manifest.Value);
            manifestOwned = boundary.CreateOwnedFile(
                new WorkspacePath(manifestTemporary),
                proposal.Manifest,
                "FaceGeom manifest");
            byte[] manifestReadback =
                await manifestOwned.WriteAndReadbackAsync(
                manifestBytes,
                FaceGeomHairRegionsSupport.MaximumSourceBytes,
                cancellationToken);
            if (!manifestBytes.SequenceEqual(
                    manifestReadback))
                throw new InvalidDataException(
                    "The independently read manifest bytes changed before promotion.");

            outputOwned.PromoteNoOverwrite();
            manifestOwned.PromoteNoOverwrite();
            outputOwned.Dispose();
            outputOwned = null;
            manifestOwned.Dispose();
            manifestOwned = null;
            return new FaceGeomHairRegionsApplyResult(
                true,
                manifest,
                manifestDocument,
                verification,
                [],
                [
                    new Diagnostic(
                        "facegeom-hair-regions-applied",
                        DiagnosticSeverity.Info,
                        "The exact HairTint envelopes were independently read back and promoted without overwrite.")
                ]);
        }
        catch (OperationCanceledException exception)
        {
            var survivors =
                ImmutableArray.CreateBuilder<WorkspacePath>();
            var cleanupFailures = new List<string>();
            CleanupOwnedHandle(
                outputOwned,
                survivors,
                cleanupFailures);
            CleanupOwnedHandle(
                manifestOwned,
                survivors,
                cleanupFailures);
            throw new FaceGeomHairRegionsOperationCanceledException(
                "The FaceGeom transaction was canceled; surviving cleanup paths are attached." +
                CleanupSuffix(cleanupFailures),
                survivors.Distinct().ToImmutableArray(),
                exception);
        }
        catch (Exception exception) when (
            exception is
                InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                OverflowException or
                JsonException)
        {
            var survivors =
                ImmutableArray.CreateBuilder<WorkspacePath>();
            var cleanupFailures = new List<string>();
            CleanupOwnedHandle(
                outputOwned,
                survivors,
                cleanupFailures);
            CleanupOwnedHandle(
                manifestOwned,
                survivors,
                cleanupFailures);
            string cleanupSuffix =
                CleanupSuffix(cleanupFailures);
            Exception refusal = cleanupFailures.Count == 0
                ? exception
                : exception is UnauthorizedAccessException
                    ? new UnauthorizedAccessException(
                        exception.Message + cleanupSuffix,
                        exception)
                    : new IOException(
                        exception.Message + cleanupSuffix,
                        exception);
            return new FaceGeomHairRegionsApplyResult(
                false,
                null,
                null,
                null,
                survivors.Distinct().ToImmutableArray(),
                [
                    RefusalDiagnostic(
                        "apply",
                        refusal)
                ]);
        }
    }

    public async ValueTask<FaceGeomHairRegionsVerification> VerifyAsync(
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            requestDocument,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            proposalDocument,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsManifest>
            manifestDocument,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestDocument);
        ArgumentNullException.ThrowIfNull(proposalDocument);
        ArgumentNullException.ThrowIfNull(manifestDocument);
        FaceGeomHairRegionsRequest request =
            requestDocument.Value;
        FaceGeomHairRegionsProposal proposal =
            proposalDocument.Value;
        FaceGeomHairRegionsFile? observedOutput = null;
        try
        {
            request = documents.ValidateRequest(
                requestDocument);
            proposal = documents.ValidateProposal(
                proposalDocument);
            FaceGeomHairRegionsManifest manifest =
                documents.ValidateManifest(manifestDocument);
            FaceGeomHairRegionsProposalMaterializer
                .ValidateAuthorityChain(
                request,
                requestDocument.Sha256,
                proposal);
            if (!string.Equals(
                    manifest.Schema,
                    FaceGeomHairRegionSchemas.Manifest,
                    StringComparison.Ordinal) ||
                manifest.Source != proposal.Source ||
                manifest.Output != proposal.ExpectedOutput ||
                manifest.ProposalSha256 !=
                proposalDocument.Sha256 ||
                !ManifestEnvelopesMatch(
                    manifest.AuthorizedEnvelopes,
                    proposal.AuthorizedEnvelopes) ||
                !manifest.ChangedByteOffsets.SequenceEqual(
                    proposal.PredictedChangedByteOffsets) ||
                manifest.Fingerprints !=
                proposal.ExpectedOutputFingerprints ||
                manifest.SurvivingArtifacts.IsDefault ||
                !manifest.SurvivingArtifacts.IsEmpty ||
                manifest.RuntimeAuthority)
                throw new InvalidDataException(
                    "The HairTint manifest does not match the proposal.");
            byte[] source =
                await FaceGeomHairRegionsSupport.ReadBoundSourceAsync(
                    boundary,
                    proposal.Source,
                    cancellationToken);
            ValidateExistingOutput(
                proposal.Output,
                proposal.ExpectedOutput);
            byte[] output =
                await boundary.ReadExactFileAsync(
                proposal.Output,
                FaceGeomHairRegionsSupport.MaximumSourceBytes,
                "verified FaceGeom output readback",
                cancellationToken);
            observedOutput = new FaceGeomHairRegionsFile(
                proposal.Output,
                output.LongLength,
                FaceGeomHairRegionsSupport.Hash(output));
            FaceGeomHairRegionsVerification verification =
                independentVerifier.Verify(
                source,
                output,
                proposal);
            if (verification.Diagnostics.IsDefault ||
                verification.ChangedByteOffsets.IsDefault ||
                verification.Diagnostics.Any(item =>
                    item is null) ||
                verification.ObservedOutput !=
                    observedOutput)
                throw new InvalidDataException(
                    "The independent verifier returned missing result collections.");
            bool hasErrors =
                verification.Diagnostics.Any(item =>
                    item.Severity ==
                    DiagnosticSeverity.Error);
            if (verification.Succeeded == hasErrors)
                throw new InvalidDataException(
                    "The independent verifier returned an inconsistent success/diagnostic result.");
            return verification;
        }
        catch (Exception exception) when (
            exception is
                InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                OverflowException)
        {
            return new FaceGeomHairRegionsVerification(
                false,
                observedOutput,
                [],
                proposal.SourceFingerprints,
                proposal.ExpectedOutputFingerprints,
                [
                    RefusalDiagnostic(
                        "verify",
                        exception)
                ]);
        }
    }

    private static Diagnostic RefusalDiagnostic(
        string phase,
        Exception exception)
    {
        bool security = exception is UnauthorizedAccessException ||
            exception.Message.Contains(
                "protected",
                StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains(
                "outside",
                StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains(
                "reparse",
                StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains(
                "device",
                StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains(
                "alternate data",
                StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains(
                "unsafe",
                StringComparison.OrdinalIgnoreCase);
        return new Diagnostic(
            security
                ? "facegeom-hair-regions-security-refusal"
                : $"facegeom-hair-regions-{phase}-refused",
            DiagnosticSeverity.Error,
            exception.Message);
    }

    private static void ValidateExistingOutput(
        WorkspacePath path,
        FaceGeomHairRegionsFile authority)
    {
        if (path != authority.Path)
            throw new InvalidDataException(
                "The verified output path does not match its authority.");
    }

    private static bool ManifestEnvelopesMatch(
        ImmutableArray<FaceGeomHairRegionsAuthorizedEnvelope> left,
        ImmutableArray<FaceGeomHairRegionsAuthorizedEnvelope> right)
    {
        if (left.IsDefault ||
            right.IsDefault ||
            left.Length != right.Length ||
            left.Any(item => item is null) ||
            right.Any(item => item is null))
            return false;
        for (int index = 0; index < left.Length; index++)
        {
            FaceGeomHairRegionsAuthorizedEnvelope first =
                left[index];
            FaceGeomHairRegionsAuthorizedEnvelope second =
                right[index];
            if (first.SharedShaderGroupId !=
                    second.SharedShaderGroupId ||
                first.Role != second.Role ||
                first.ByteOffset != second.ByteOffset ||
                first.ByteLength != second.ByteLength ||
                !first.StructuralIds.SequenceEqual(
                    second.StructuralIds,
                    StringComparer.Ordinal) ||
                !first.OldFloatBits.SequenceEqual(
                    second.OldFloatBits) ||
                !first.NewFloatBits.SequenceEqual(
                    second.NewFloatBits))
                return false;
        }
        return true;
    }

    private static string TemporaryBeside(string destination) =>
        Path.Combine(
            Path.GetDirectoryName(destination) ??
            throw new InvalidDataException(
                "Output path has no parent."),
            $".{Path.GetFileName(destination)}." +
            $"{Guid.NewGuid():N}.npcmanager.tmp");

    private static void CleanupOwnedHandle(
        FaceGeomHairRegionsOwnedFile? owned,
        ImmutableArray<WorkspacePath>.Builder survivors,
        List<string> failures)
    {
        if (owned is null)
            return;
        string path = owned.Path;
        if (!owned.TryDelete(out string? failure))
        {
            survivors.Add(new WorkspacePath(path));
            failures.Add(
                $"{path}: {failure ?? "Identity-bound cleanup failed and the exact retained artifact was left untouched."}");
        }
    }

    private static string CleanupSuffix(
        List<string> failures) =>
        failures.Count == 0
            ? string.Empty
            : " Cleanup failures: " +
              string.Join(" | ", failures);
}
