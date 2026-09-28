using System.Collections.Immutable;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

/// <summary>
/// Production transaction behind the five-step desktop wizard. It delegates
/// all NIF writing to FaceGeomHairRegionsApplyService and all semantic
/// verification to the independent Bethesda verifier.
/// </summary>
internal sealed class FaceGeomHairRegionsWizardTransaction :
    IFaceGeomHairRegionsWizardTransaction
{
    private readonly object admittedSync = new();
    private readonly Dictionary<
        string,
        AdmittedSelectedSource> admittedSources =
            new(StringComparer.OrdinalIgnoreCase);
    private readonly WorkspacePath labRoot;
    private readonly WorkspacePath transactionRoot;
    private readonly Sha256Hash rendererAuthoritySha256;
    private readonly Sha256Hash rendererScriptSha256;
    private readonly FaceGeomHairRegionsDocumentCodec documents;
    private readonly FaceGeomHairRegionsExactSourceAnalyzer analyzer;
    private readonly FaceGeomHairRegionsAnalyzer templateFactory;
    private readonly FaceGeomHairRegionsProposer proposer;
    private readonly FaceGeomHairRegionsApplyService applyService;
    private readonly IFaceGeomHairRegionsPreviewService previewService;
    private readonly IFaceGeomHairRegionsPreviewSourceService
        previewSourceService;
    private readonly
        FaceGeomHairRegionsReviewedIntakeAuthorityStore intakeStore;

    public FaceGeomHairRegionsWizardTransaction(
        WorkspacePath labRoot,
        IFaceGeomHairRegionsPreviewService previewService,
        IFaceGeomHairRegionsPreviewSourceService previewSourceService,
        WorkspacePath transactionRoot,
        Sha256Hash rendererAuthoritySha256,
        Sha256Hash rendererScriptSha256)
    {
        this.labRoot = labRoot;
        this.previewService = previewService ??
            throw new ArgumentNullException(
                nameof(previewService));
        this.previewSourceService = previewSourceService ??
            throw new ArgumentNullException(
                nameof(previewSourceService));
        this.transactionRoot = transactionRoot;
        this.rendererAuthoritySha256 =
            rendererAuthoritySha256;
        this.rendererScriptSha256 =
            rendererScriptSha256;
        documents =
            new FaceGeomHairRegionsDocumentCodec(
                labRoot);
        documents.WorkspaceBoundary.RequireExistingDirectory(
            transactionRoot,
            "desktop HairTint transaction root");
        analyzer =
            new FaceGeomHairRegionsExactSourceAnalyzer(
                labRoot);
        templateFactory =
            new FaceGeomHairRegionsAnalyzer(
                labRoot);
        proposer =
            new FaceGeomHairRegionsProposer(
                labRoot,
                documents);
        applyService =
            new FaceGeomHairRegionsApplyService(
                labRoot,
                new BethesdaFaceGeomHairRegionsVerifier(),
                documents);
        intakeStore =
            new FaceGeomHairRegionsReviewedIntakeAuthorityStore(
                labRoot,
                documents);
    }

    internal void AdmitSelectedSource(
        FaceGeomHairRegionsSelectedSource source,
        ImmutableArray<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (diagnostics.IsDefault ||
            diagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error))
        {
            throw new InvalidDataException(
                "Only an exact successfully resolved selected source may be admitted to the desktop transaction.");
        }
        lock (admittedSync)
        {
            admittedSources[source.Source.Path.Value] =
                new AdmittedSelectedSource(
                    source,
                    diagnostics);
        }
    }

    internal void RevokeSelectedSource(
        WorkspacePath source)
    {
        lock (admittedSync)
        {
            admittedSources.Remove(
                source.Value);
        }
    }

    internal int AdmittedSelectedSourceCount
    {
        get
        {
            lock (admittedSync)
            {
                return admittedSources.Count;
            }
        }
    }

    public async ValueTask<
        FaceGeomHairRegionsWizardAnalysisState> AnalyzeAsync(
            WorkspacePath source,
            WorkspacePath output,
            WorkspacePath manifest,
            CancellationToken cancellationToken)
    {
        AdmittedSelectedSource? admitted;
        lock (admittedSync)
        {
            admittedSources.TryGetValue(
                source.Value,
                out admitted);
        }
        FaceGeomHairRegionsAnalysis value =
            await analyzer.AnalyzeAsync(
                source,
                admitted?.Source.PluginColorContext,
                cancellationToken);
        if (admitted is not null &&
            value.Source !=
                admitted.Source.Source)
        {
            throw new InvalidDataException(
                "The selected FaceGeom changed after its exact provider authority was admitted.");
        }
        StrictJsonDocumentAuthority<
            FaceGeomHairRegionsAnalysis> analysis =
                documents.BindAnalysis(value);
        FaceGeomHairRegionsRequest template =
            templateFactory.CreateAssignmentTemplate(
                analysis,
                output,
                manifest);
        return new FaceGeomHairRegionsWizardAnalysisState(
            analysis,
            template,
            admitted?.Diagnostics ?? []);
    }

    public async ValueTask<
        FaceGeomHairRegionsWizardProposalState> ProposeAsync(
            StrictJsonDocumentAuthority<
                FaceGeomHairRegionsAnalysis> analysis,
            FaceGeomHairRegionsRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(request);
        _ = documents.ValidateAnalysis(
            analysis);
        StrictJsonDocumentAuthority<
            FaceGeomHairRegionsRequest> requestDocument =
                documents.BindRequest(request);
        FaceGeomHairRegionsProposalResult proposed =
            await proposer.ProposeAsync(
                analysis,
                requestDocument,
                cancellationToken);
        FaceGeomHairRegionsProposalMaterialization
            materialization =
                await applyService.MaterializeAsync(
                    requestDocument,
                    proposed.Proposal,
                    cancellationToken);
        return new FaceGeomHairRegionsWizardProposalState(
            requestDocument,
            proposed.Proposal,
            materialization,
            proposed.Diagnostics);
    }

    public async ValueTask<FaceGeomHairRegionsPreviewResult>
        PreviewAsync(
            FaceGeomHairRegionsWizardProposalState proposal,
            ReviewedGameIntake intake,
            WorkspacePath outputRoot,
            CancellationToken cancellationToken)
    {
        ValidateProposalState(
            proposal);
        await using
            FaceGeomHairRegionsReviewedIntakeAuthorityLease
                intakeAuthority =
                    await intakeStore.CreateAndReloadAsync(
                        intake,
                        transactionRoot,
                        cancellationToken);
        FaceGeomHairRegionsPreviewResult result =
            await previewService.PreviewAsync(
                new FaceGeomHairRegionsPreviewRequest(
                    proposal.RequestDocument,
                    proposal.ProposalDocument,
                    proposal.Materialization,
                    intakeAuthority.Authority,
                    outputRoot),
                cancellationToken);
        if (result.Diagnostics.IsDefault ||
            result.Artifacts.IsDefault)
        {
            throw new InvalidDataException(
                "The desktop preview service returned default result collections.");
        }
        return result;
    }

    public async ValueTask<FaceGeomHairRegionsApplyResult>
        ApplyAsync(
            FaceGeomHairRegionsWizardProposalState proposal,
            CancellationToken cancellationToken)
    {
        ValidateProposalState(
            proposal);
        FaceGeomHairRegionsApplyResult applied =
            await applyService.ApplyAsync(
                proposal.RequestDocument,
                proposal.ProposalDocument,
                cancellationToken);
        if (!applied.Succeeded ||
            applied.ManifestDocument is null)
        {
            return applied;
        }

        FaceGeomHairRegionsVerification verification =
            await applyService.VerifyAsync(
                proposal.RequestDocument,
                proposal.ProposalDocument,
                applied.ManifestDocument,
                cancellationToken);
        if (verification.Succeeded &&
            !verification.Diagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error))
        {
            return applied with
            {
                Verification = verification,
                Diagnostics =
                    applied.Diagnostics.AddRange(
                        verification.Diagnostics)
            };
        }

        FaceGeomHairRegionsProposal value =
            proposal.ProposalDocument.Value;
        ImmutableArray<WorkspacePath> survivors =
            applied.SurvivingArtifacts
                .Add(value.Output)
                .Add(value.Manifest)
                .Distinct()
                .ToImmutableArray();
        return applied with
        {
            Succeeded = false,
            Verification = verification,
            SurvivingArtifacts = survivors,
            Diagnostics =
                applied.Diagnostics
                    .AddRange(
                        verification.Diagnostics)
                    .Add(
                        new Diagnostic(
                            "facegeom-hair-regions-desktop-post-verify-failed",
                            DiagnosticSeverity.Error,
                            "The promoted FaceGeom/manifest pair failed independent post-write verification; exact surviving paths are reported."))
        };
    }

    public async ValueTask<
        FaceGeomHairRegionsPreviewCacheAuthority>
        ResolvePreviewCacheAuthorityAsync(
            FaceGeomHairRegionsWizardProposalState proposal,
            ReviewedGameIntake intake,
            CancellationToken cancellationToken)
    {
        ValidateProposalState(
            proposal);
        await using
            FaceGeomHairRegionsReviewedIntakeAuthorityLease
                intakeAuthority =
                    await intakeStore.CreateAndReloadAsync(
                        intake,
                        transactionRoot,
                        cancellationToken);
        var cacheSourceRoot = new WorkspacePath(
            Path.Combine(
                transactionRoot.Value,
                $"cache-source-{Guid.NewGuid():N}"));
        using var ownedSource =
            new FaceGeomHairRegionsOwnedDirectoryLease(
                labRoot,
                cacheSourceRoot,
                "desktop HairTint cache source");
        FaceGeomHairRegionsPreviewSourceResult composed =
            await previewSourceService.ComposeAsync(
                new FaceGeomHairRegionsPreviewSourceRequest(
                    proposal.Materialization.Candidate,
                    proposal.Materialization.CandidateBytes,
                    intakeAuthority.Authority.Value,
                    cacheSourceRoot),
                cancellationToken);
        if (composed.Diagnostics.IsDefault ||
            !composed.Composed ||
            composed.Source is null ||
            composed.Diagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error) ||
            composed.Source.Candidate.Sha256 !=
                proposal.Materialization.Candidate.Sha256 ||
            composed.Source.Candidate.Bytes !=
                proposal.Materialization.Candidate.ByteLength)
        {
            throw new InvalidDataException(
                "The desktop cache preflight could not resolve one exact candidate/provider authority: " +
                string.Join(
                    " | ",
                    composed.Diagnostics.IsDefault
                        ? ["default diagnostics"]
                        : composed.Diagnostics.Select(item =>
                            $"{item.Code}: {item.Message}")));
        }
        FaceGeomHairRegionsPreviewCacheKey key =
            FaceGeomHairRegionsPreviewCacheKey.Create(
                proposal.Materialization.Candidate
                    .Sha256,
                proposal.RequestDocument.Sha256,
                proposal.ProposalDocument.Sha256,
                intakeAuthority.Authority.Document.Sha256,
                rendererAuthoritySha256,
                rendererScriptSha256,
                intake.AssetIndexFingerprint,
                composed.Source
                    .TextureFingerprintSha256);
        return new FaceGeomHairRegionsPreviewCacheAuthority(
            key,
            intake.IntakeFingerprint);
    }

    private void ValidateProposalState(
        FaceGeomHairRegionsWizardProposalState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        FaceGeomHairRegionsRequest request =
            documents.ValidateRequest(
                state.RequestDocument);
        FaceGeomHairRegionsProposal proposal =
            documents.ValidateProposal(
                state.ProposalDocument);
        if (proposal.RequestSha256 !=
                state.RequestDocument.Sha256 ||
            proposal.Source != request.Source ||
            state.Materialization.Candidate !=
                proposal.ExpectedOutput ||
            state.Materialization.CandidateBytes
                .IsDefaultOrEmpty ||
            state.Materialization.CandidateBytes.Length !=
                proposal.ExpectedOutput.ByteLength ||
            state.Materialization.ChangedByteOffsets
                .IsDefault ||
            !state.Materialization.ChangedByteOffsets
                .SequenceEqual(
                    proposal.PredictedChangedByteOffsets))
        {
            throw new InvalidDataException(
                "The desktop wizard proposal state is not one closed canonical authority chain.");
        }
    }

    private sealed record AdmittedSelectedSource(
        FaceGeomHairRegionsSelectedSource Source,
        ImmutableArray<Diagnostic> Diagnostics);
}
