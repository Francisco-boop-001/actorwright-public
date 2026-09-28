using System.Collections.Immutable;
using System.ComponentModel;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed partial class FaceGeomHairRegionsCommandHandler
{
    private readonly FaceGeomHairRegionsAnalyzer analyzer;
    private readonly FaceGeomHairRegionsProposer proposer;
    private readonly FaceGeomHairRegionsApplyService applier;
    private readonly FaceGeomHairRegionsDocumentCodec documents;
    private readonly IPreviewServiceFactory<
        FaceGeomHairRegionsPreviewServices> previewFactory;
    private readonly TextWriter output;
    private readonly TextWriter error;
    private readonly FaceGeomHairRegionsWorkspaceBoundary boundary;

    public FaceGeomHairRegionsCommandHandler(
        WorkspacePath workspaceRoot,
        FaceGeomHairRegionsAnalyzer analyzer,
        FaceGeomHairRegionsProposer proposer,
        FaceGeomHairRegionsApplyService applier,
        FaceGeomHairRegionsDocumentCodec documents,
        IPreviewServiceFactory<FaceGeomHairRegionsPreviewServices>
            previewFactory,
        TextWriter output,
        TextWriter error)
    {
        this.analyzer = analyzer;
        this.proposer = proposer;
        this.applier = applier;
        this.documents = documents;
        this.previewFactory = previewFactory ??
            throw new ArgumentNullException(nameof(previewFactory));
        this.output = output;
        this.error = error;
        boundary = documents.WorkspaceBoundary;
        if (boundary.WorkspaceRoot != workspaceRoot)
            throw new ArgumentException(
                "The document boundary does not match the handler workspace.",
                nameof(documents));
    }

    public FaceGeomHairRegionsCommandHandler(
        WorkspacePath workspaceRoot,
        FaceGeomHairRegionsAnalyzer analyzer,
        FaceGeomHairRegionsProposer proposer,
        FaceGeomHairRegionsApplyService applier,
        FaceGeomHairRegionsDocumentCodec documents,
        IFaceGeomHairRegionsPreviewService previewService,
        INpcVisualPreviewVisualValidator visualValidator,
        TextWriter output,
        TextWriter error)
        : this(
            workspaceRoot,
            analyzer,
            proposer,
            applier,
            documents,
            new FixedPreviewFactory(
                previewService,
                visualValidator),
            output,
            error)
    {
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return command.Name switch
            {
                "facegen hair-regions analyze" =>
                    await AnalyzeAsync(command, cancellationToken),
                "facegen hair-regions propose" =>
                    await ProposeAsync(command, cancellationToken),
                "facegen hair-regions preview" =>
                    await PreviewAsync(command, cancellationToken),
                "facegen hair-regions apply" =>
                    await ApplyAsync(command, cancellationToken),
                "facegen hair-regions verify" =>
                    await VerifyAsync(command, cancellationToken),
                _ => Usage(
                    command,
                    "The FaceGeom hair-region phase is not configured.")
            };
        }
        catch (
            FaceGeomHairRegionsOperationCanceledException
                exception)
        {
            return Respond(
                command,
                "CANCELLED",
                exception.SurvivingArtifacts
                    .Select(path =>
                        SuggestedArtifact("surviving", path))
                    .ToImmutableArray(),
                [
                    new Diagnostic(
                        "cancelled",
                        DiagnosticSeverity.Error,
                        CancellationDiagnosticMessage(
                            exception))
                ],
                CommandExitCode.Cancelled);
        }
        catch (
            FaceGeomHairRegionsPreviewOperationalException
                exception)
        {
            bool security =
                IsSecurityRefusal(exception);
            return Respond(
                command,
                "REFUSED",
                exception.SurvivingArtifacts
                    .Select(path =>
                        SuggestedArtifact("surviving", path))
                    .ToImmutableArray(),
                [
                    new Diagnostic(
                        security
                            ? "facegeom-hair-regions-security-refused"
                            : "facegeom-hair-regions-preview-operation-failed",
                        DiagnosticSeverity.Error,
                        ProcessEvidenceDiagnosticMessage(
                            exception.Message,
                            exception.SurvivingProcessIds,
                            exception.ProcessTerminationFailure))
                ],
                security
                    ? DiagnosticExitCodeClassifier.KnownSecurityRefusal
                    : CommandExitCode.ValidationFailure);
        }
        catch (
            FaceGeomHairRegionsCommandFileException
                exception)
        {
            bool security =
                IsSecurityRefusal(exception);
            return Respond(
                command,
                "REFUSED",
                exception.SurvivingArtifacts
                    .Select(path =>
                        SuggestedArtifact("surviving", path))
                    .ToImmutableArray(),
                [
                    new Diagnostic(
                        security
                            ? "facegeom-hair-regions-security-refused"
                            : "facegeom-hair-regions-file-write-refused",
                        DiagnosticSeverity.Error,
                        exception.Message)
                ],
                security
                    ? DiagnosticExitCodeClassifier.KnownSecurityRefusal
                    : CommandExitCode.ValidationFailure);
        }
        catch (OperationCanceledException)
        {
            return Respond(
                command,
                "CANCELLED",
                [],
                [
                    new Diagnostic(
                        "cancelled",
                        DiagnosticSeverity.Error,
                        "The FaceGeom hair-region command was cancelled.")
                ],
                CommandExitCode.Cancelled);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                SecurityException or
                OverflowException or
                ArgumentException or
                JsonException)
        {
            string code = exception.Message.Contains(
                    "expected SHA-256",
                    StringComparison.OrdinalIgnoreCase)
                ? "facegeom-hair-regions-document-hash-mismatch"
                : IsSecurityRefusal(exception)
                    ? "facegeom-hair-regions-security-refused"
                    : "facegeom-hair-regions-document-invalid";
            return Respond(
                command,
                "REFUSED",
                [],
                [
                    new Diagnostic(
                        code,
                        DiagnosticSeverity.Error,
                        exception.Message)
                ],
                code == "facegeom-hair-regions-security-refused"
                    ? DiagnosticExitCodeClassifier.KnownSecurityRefusal
                    : CommandExitCode.ValidationFailure);
        }
    }

    private async ValueTask<CommandExitCode> AnalyzeAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        string[] required =
        [
            "source",
            "expected-source-sha256",
            "analysis",
            "assignment-template"
        ];
        if (!TryRequireOptions(
                command,
                required,
                out ImmutableDictionary<string, string> values,
                out string message))
            return Usage(command, message);

        var source = new WorkspacePath(values["source"]);
        var analysisPath = new WorkspacePath(values["analysis"]);
        var templatePath =
            new WorkspacePath(values["assignment-template"]);
        boundary.RequireExistingFile(source, "FaceGeom source");
        if (File.Exists(analysisPath.Value) ||
            Directory.Exists(analysisPath.Value) ||
            File.Exists(templatePath.Value) ||
            Directory.Exists(templatePath.Value))
            return Collision(
                command,
                "Analysis and assignment-template outputs must both be new.");

        string parent = Path.GetDirectoryName(templatePath.Value) ??
            throw new InvalidDataException(
                "The assignment-template path has no parent.");
        string stem =
            Path.GetFileNameWithoutExtension(templatePath.Value);
        var suggestedOutput = new WorkspacePath(
            Path.Combine(parent, stem + ".output.nif"));
        var suggestedManifest = new WorkspacePath(
            Path.Combine(
                parent,
                stem + ".output.manifest.json"));
        WorkspacePath[] topology =
        [
            source,
            analysisPath,
            templatePath,
            suggestedOutput,
            suggestedManifest
        ];
        if (topology.Select(path => path.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != topology.Length)
            return Respond(
                command,
                "REFUSED",
                [],
                [
                    new Diagnostic(
                        "facegeom-hair-regions-path-collision",
                        DiagnosticSeverity.Error,
                        "Source, metadata, suggested NIF, and manifest paths must all be distinct.")
                ],
                CommandExitCode.ValidationFailure);
        boundary.RequireNewFile(analysisPath, "analysis output");
        boundary.RequireNewFile(
            templatePath,
            "assignment-template output");
        boundary.RequireNewFile(
            suggestedOutput,
            "suggested FaceGeom output");
        boundary.RequireNewFile(
            suggestedManifest,
            "suggested manifest output");
        FaceGeomHairRegionsAnalysis analysis =
            await analyzer.AnalyzeAsync(
                source,
                new Sha256Hash(
                    values["expected-source-sha256"]),
                pluginColorContext: null,
                cancellationToken);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
            analysisDocument = documents.BindAnalysis(analysis);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            templateDocument = documents.BindRequest(
                analyzer.CreateAssignmentTemplate(
                    analysisDocument,
                    suggestedOutput,
                    suggestedManifest));
        await WritePairNoOverwriteAsync(
            analysisPath,
            analysisDocument.Utf8Json,
            templatePath,
            templateDocument.Utf8Json,
            cancellationToken);
        return Respond(
            command,
            "PASS",
            [
                DocumentArtifact(
                    "analysis",
                    analysisPath,
                    analysisDocument),
                DocumentArtifact(
                    "assignmentTemplate",
                    templatePath,
                    templateDocument),
                SuggestedArtifact(
                    "suggestedOutput",
                    suggestedOutput),
                SuggestedArtifact(
                    "suggestedManifest",
                    suggestedManifest)
            ],
            [
                new Diagnostic(
                    "facegeom-hair-regions-analyzed",
                    DiagnosticSeverity.Info,
                    "Canonical analysis and Preserve-default assignment template were written without overwrite.")
            ],
            CommandExitCode.Success);
    }

    private async ValueTask<CommandExitCode> ProposeAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        string[] required =
        [
            "analysis",
            "analysis-sha256",
            "request",
            "request-sha256",
            "proposal"
        ];
        if (!TryRequireOptions(
                command,
                required,
                out ImmutableDictionary<string, string> values,
                out string message))
            return Usage(command, message);
        var analysisPath = new WorkspacePath(values["analysis"]);
        var requestPath = new WorkspacePath(values["request"]);
        var proposalPath = new WorkspacePath(values["proposal"]);
        if (File.Exists(proposalPath.Value) ||
            Directory.Exists(proposalPath.Value))
            return Collision(
                command,
                "The proposal output must be new.");
        boundary.RequireNewFile(proposalPath, "proposal output");
        StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
            analysisDocument = await documents.LoadAnalysisAsync(
                analysisPath,
                new Sha256Hash(values["analysis-sha256"]),
                cancellationToken);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            requestDocument = await documents.LoadRequestAsync(
                requestPath,
                new Sha256Hash(values["request-sha256"]),
                cancellationToken);
        if (new[]
            {
                requestDocument.Value.Source.Path,
                requestDocument.Value.Output,
                requestDocument.Value.Manifest
            }.Any(path =>
                string.Equals(
                    path.Value,
                    proposalPath.Value,
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                "The proposal JSON path must not collide with the source, future NIF output, or future manifest.");
        FaceGeomHairRegionsProposalResult result =
            await proposer.ProposeAsync(
                analysisDocument,
                requestDocument,
                cancellationToken);
        await WriteOneNoOverwriteAsync(
            proposalPath,
            result.Proposal.Utf8Json,
            cancellationToken);
        return Respond(
            command,
            "PASS",
            [
                DocumentArtifact(
                    "proposal",
                    proposalPath,
                    result.Proposal)
            ],
            result.Diagnostics.Add(
                new Diagnostic(
                    "facegeom-hair-regions-proposed",
                    DiagnosticSeverity.Info,
                    "The canonical fixed-width proposal was written without overwrite.")),
            CommandExitCode.Success);
    }

    private async ValueTask<CommandExitCode> PreviewAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        string[] allowed =
        [
            "request",
            "request-sha256",
            "proposal",
            "proposal-sha256",
            "intake",
            "output-root"
        ];
        if (!TryRequireOptions(
                command,
                allowed,
                out ImmutableDictionary<string, string> values,
                out string message))
            return Usage(command, message);

        var requestPath = new WorkspacePath(values["request"]);
        var proposalPath = new WorkspacePath(values["proposal"]);
        var intakePath = new WorkspacePath(values["intake"]);
        var outputRoot = new WorkspacePath(values["output-root"]);
        var requestHash = new Sha256Hash(values["request-sha256"]);
        var proposalHash = new Sha256Hash(values["proposal-sha256"]);
        boundary.RequireNewDirectory(
            outputRoot,
            "preview output root");
        string? outputParent =
            Path.GetDirectoryName(outputRoot.Value);
        if (string.IsNullOrWhiteSpace(outputParent))
            return Usage(
                command,
                "Preview output root must have an existing parent directory.");
        var privateBundleRoot = new WorkspacePath(
            Path.Combine(
                outputParent,
                $".{Path.GetFileName(outputRoot.Value)}-preview-{Guid.NewGuid():N}.tmp"));
        boundary.RequireNewDirectory(
            privateBundleRoot,
            "private preview bundle root");

        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            requestDocument = await documents.LoadRequestAsync(
                requestPath,
                requestHash,
                cancellationToken);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            proposalDocument = await documents.LoadProposalAsync(
                proposalPath,
                proposalHash,
                cancellationToken);
        ReviewedGameIntakeDocumentAuthority intakeAuthority;
        try
        {
            intakeAuthority =
                await documents.LoadReviewedIntakeAsync(
                    intakePath,
                    cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                SecurityException or
                OverflowException or
                ArgumentException or
                JsonException)
        {
            throw new InvalidDataException(
                "The reviewed intake could not be loaded. Produce it with " +
                "workspace preflight --protocol 2 and pass the fresh file " +
                "written by --intake-output to --intake. " +
                exception.Message,
                exception);
        }
        FaceGeomHairRegionsProposalMaterialization materialization =
            await applier.MaterializeAsync(
                requestDocument,
                proposalDocument,
                cancellationToken);
        await using PreviewServiceLease<
            FaceGeomHairRegionsPreviewServices> previewLease =
            await previewFactory.CreateAsync(cancellationToken);
        IFaceGeomHairRegionsPreviewService previewService =
            previewLease.Service.Service;
        INpcVisualPreviewVisualValidator visualValidator =
            previewLease.Service.Validator;
        using FaceGeomHairRegionsPinnedDirectory previewRoot =
            boundary.CreateOwnedDirectory(
                privateBundleRoot,
                "private preview bundle root");

        try
        {
            var previewRequest =
                new FaceGeomHairRegionsPreviewRequest(
                    requestDocument,
                    proposalDocument,
                    materialization,
                    intakeAuthority,
                    privateBundleRoot);
            FaceGeomHairRegionsPreviewResult result =
                previewService is
                    FaceGeomHairRegionsPreviewService realPreview
                    ? await realPreview.PreviewAsync(
                        previewRequest,
                        previewRoot,
                        cancellationToken)
                    : await previewService.PreviewAsync(
                        previewRequest,
                        cancellationToken);
        if (result.Diagnostics.IsDefault ||
            result.Artifacts.IsDefault ||
            result.Diagnostics.Any(item => item is null) ||
            result.Artifacts.Any(item => item is null))
        {
            ImmutableArray<WorkspacePath> survivors =
                previewRoot.DeleteTree();
            return InvalidPreviewResult(
                command,
                "Preview diagnostics and artifacts must be explicit closed collections.",
                survivors);
        }
        bool hasErrors = HasErrors(result.Diagnostics);
        if (result.Succeeded == hasErrors)
        {
            ImmutableArray<WorkspacePath> survivors =
                previewRoot.DeleteTree();
            return InvalidPreviewResult(
                command,
                "Preview Succeeded must be true exactly when diagnostics contain no Error.",
                survivors);
        }
        if (!result.Succeeded)
        {
            ImmutableArray<WorkspacePath> survivors =
                previewRoot.DeleteTree();
            if (!survivors.IsDefaultOrEmpty)
                return Respond(
                    command,
                    "REFUSED",
                    survivors.Select(item =>
                            SuggestedArtifact(
                                "surviving",
                                item))
                        .ToImmutableArray(),
                    result.Diagnostics.Add(
                        new Diagnostic(
                            "facegeom-hair-regions-preview-cleanup-failed",
                            DiagnosticSeverity.Error,
                            "The private preview bundle retained exact survivors after rollback.")),
                    ExitFor(result.Diagnostics));
            return Respond(
                command,
                "REFUSED",
                [],
                result.Diagnostics,
                ExitFor(result.Diagnostics));
        }
        string? proofFailure =
            await ValidatePreviewProofAsync(
                result,
                requestDocument,
                proposalDocument,
                materialization,
                intakeAuthority,
                privateBundleRoot,
                previewRoot,
                visualValidator,
                cancellationToken);
        if (proofFailure is not null)
        {
            ImmutableArray<WorkspacePath> survivors =
                previewRoot.DeleteTree();
            if (!survivors.IsDefaultOrEmpty)
                return Respond(
                    command,
                    "REFUSED",
                    survivors.Select(item =>
                            SuggestedArtifact(
                                "surviving",
                                item))
                        .ToImmutableArray(),
                    [
                        new Diagnostic(
                            "facegeom-hair-regions-preview-cleanup-failed",
                            DiagnosticSeverity.Error,
                            $"{proofFailure} Private bundle rollback retained exact survivors.")
                    ],
                    CommandExitCode.ValidationFailure);
            return InvalidPreviewResult(
                command,
                proofFailure);
        }
        try
        {
            previewRoot.PromoteNoOverwrite(
                outputRoot);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                SecurityException or
                InvalidDataException or
                Win32Exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                previewRoot.DeleteTree();
            return Respond(
                command,
                "REFUSED",
                survivors.Select(item =>
                        SuggestedArtifact(
                            "surviving",
                            item))
                    .ToImmutableArray(),
                [
                    new Diagnostic(
                        "facegeom-hair-regions-preview-bundle-promotion-failed",
                        DiagnosticSeverity.Error,
                        exception.Message)
                ],
                CommandExitCode.ValidationFailure);
        }
        ImmutableArray<ArtifactResponse> artifacts =
            result.Artifacts.Select(item =>
                    new ArtifactResponse(
                        PreviewRole(item),
                        item.StructuralId,
                        Path.Combine(
                            outputRoot.Value,
                            Path.GetFileName(
                                item.Path.Value)),
                        item.Sha256.Value,
                        item.ByteLength,
                        item.NonEmptyPixelCount))
                .ToImmutableArray();
            return Respond(
                command,
                result.Succeeded ? "PASS" : "REFUSED",
                artifacts,
                result.Diagnostics,
                result.Succeeded
                    ? CommandExitCode.Success
                    : ExitFor(result.Diagnostics),
                result.Evidence);
        }
        catch (
            FaceGeomHairRegionsOperationCanceledException
                exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                previewRoot.DeleteTree()
                    .AddRange(exception.SurvivingArtifacts)
                    .Distinct()
                    .ToImmutableArray();
            throw new FaceGeomHairRegionsOperationCanceledException(
                "Hair-region preview was canceled; the complete private bundle was rolled back and exact survivors are attached.",
                survivors,
                exception,
                exception.SurvivingProcessIds,
                exception.ProcessTerminationFailure);
        }
        catch (OperationCanceledException exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                previewRoot.DeleteTree();
            throw new FaceGeomHairRegionsOperationCanceledException(
                "Hair-region preview was canceled; the complete private bundle was rolled back and exact survivors are attached.",
                survivors,
                exception);
        }
        catch (Exception exception) when (
            exception is
                IOException or
                UnauthorizedAccessException or
                SecurityException or
                InvalidDataException or
                Win32Exception)
        {
            ImmutableArray<WorkspacePath> survivors =
                previewRoot.DeleteTree();
            FaceGeomHairRegionsOperationalException?
                rendererFailure =
                    exception as
                        FaceGeomHairRegionsOperationalException;
            survivors = survivors
                .AddRange(
                    rendererFailure?.SurvivingArtifacts ??
                    [])
                .Distinct()
                .ToImmutableArray();
            throw new FaceGeomHairRegionsPreviewOperationalException(
                "Hair-region preview failed during an expected operational boundary; the complete private bundle was rolled back and exact survivors are attached. " +
                exception.Message,
                survivors,
                exception,
                rendererFailure?.SurvivingProcessIds ??
                    [],
                rendererFailure?.ProcessTerminationFailure);
        }
    }

    private sealed class FixedPreviewFactory :
        IPreviewServiceFactory<FaceGeomHairRegionsPreviewServices>
    {
        private readonly FaceGeomHairRegionsPreviewServices _services;

        public FixedPreviewFactory(
            IFaceGeomHairRegionsPreviewService previewService,
            INpcVisualPreviewVisualValidator visualValidator)
        {
            _services = new FaceGeomHairRegionsPreviewServices(
                previewService ?? throw new ArgumentNullException(
                    nameof(previewService)),
                visualValidator ?? throw new ArgumentNullException(
                    nameof(visualValidator)));
        }

        public ValueTask<PreviewServiceLease<
            FaceGeomHairRegionsPreviewServices>> CreateAsync(
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new PreviewServiceLease<
                    FaceGeomHairRegionsPreviewServices>(_services));
        }
    }

    private async ValueTask<CommandExitCode> ApplyAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryApplyOptions(
                command,
                out ImmutableDictionary<string, string> values,
                out string message))
            return Usage(command, message);
        ApplyAuthority authority =
            await LoadApplyAuthorityAsync(
                values,
                cancellationToken);
        if (!OutputPathsMatch(authority))
            return OutputMismatch(command);
        FaceGeomHairRegionsApplyResult result =
            await applier.ApplyAsync(
                authority.RequestDocument,
                authority.ProposalDocument,
                cancellationToken);
        bool resultShapeValid =
            !result.Diagnostics.IsDefault &&
            !result.SurvivingArtifacts.IsDefault &&
            !result.Diagnostics.Any(item => item is null) &&
            (result.Verification is null ||
             !result.Verification.Diagnostics.IsDefault &&
             !result.Verification.ChangedByteOffsets.IsDefault &&
             !result.Verification.Diagnostics.Any(item =>
                 item is null));
        if (!resultShapeValid)
            return Respond(
                command,
                "REFUSED",
                [],
                [
                    new Diagnostic(
                        "facegeom-hair-regions-apply-result-invalid",
                        DiagnosticSeverity.Error,
                        "Apply diagnostics, survivors, and verification collections must be explicit.")
                ],
                CommandExitCode.ValidationFailure);
        bool resultHasErrors = HasErrors(result.Diagnostics);
        bool verificationSuccess =
            result.Verification is
            {
                Succeeded: true
            } verification &&
            !HasErrors(verification.Diagnostics);
        bool completeSuccess =
            result.Succeeded &&
            result.SurvivingArtifacts.IsEmpty &&
            result.Manifest is not null &&
            !result.Manifest.SurvivingArtifacts.IsDefault &&
            result.Manifest.SurvivingArtifacts.IsEmpty &&
            result.ManifestDocument is not null &&
            verificationSuccess;
        if (result.Succeeded == resultHasErrors ||
            result.Succeeded != completeSuccess)
            return Respond(
                command,
                "REFUSED",
                result.SurvivingArtifacts.Select(path =>
                        SuggestedArtifact("surviving", path))
                    .ToImmutableArray(),
                [
                    new Diagnostic(
                        "facegeom-hair-regions-apply-result-invalid",
                        DiagnosticSeverity.Error,
                        "Apply Succeeded must exactly match a complete error-free manifest and verification result.")
                ],
                CommandExitCode.ValidationFailure);
        if (!result.Succeeded ||
            result.ManifestDocument is null)
            return Respond(
                command,
                "REFUSED",
                result.SurvivingArtifacts.Select(path =>
                        SuggestedArtifact("surviving", path))
                    .ToImmutableArray(),
                result.Diagnostics,
                ExitFor(result.Diagnostics));
        return Respond(
            command,
            "PASS",
            [
                DocumentArtifact(
                    "request",
                    authority.RequestPath,
                    authority.RequestDocument),
                DocumentArtifact(
                    "proposal",
                    authority.ProposalPath,
                    authority.ProposalDocument),
                new ArtifactResponse(
                    "output",
                    null,
                    authority.OutputPath.Value,
                    authority.ProposalDocument.Value
                        .ExpectedOutput.Sha256.Value,
                    authority.ProposalDocument.Value
                        .ExpectedOutput.ByteLength,
                    null),
                DocumentArtifact(
                    "manifest",
                    authority.ManifestPath,
                    result.ManifestDocument)
            ],
            result.Diagnostics,
            CommandExitCode.Success);
    }

    private async ValueTask<CommandExitCode> VerifyAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryApplyOptions(
                command,
                out ImmutableDictionary<string, string> values,
                out string message))
            return Usage(command, message);
        ApplyAuthority authority =
            await LoadApplyAuthorityAsync(
                values,
                cancellationToken);
        if (!OutputPathsMatch(authority))
            return OutputMismatch(command);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsManifest>
            manifestDocument = await documents.LoadManifestAsync(
                authority.ManifestPath,
                cancellationToken);
        FaceGeomHairRegionsVerification verification =
            await applier.VerifyAsync(
                authority.RequestDocument,
                authority.ProposalDocument,
                manifestDocument,
                cancellationToken);
        bool verificationShapeValid =
            !verification.Diagnostics.IsDefault &&
            !verification.ChangedByteOffsets.IsDefault &&
            !verification.Diagnostics.Any(item => item is null);
        bool verificationHasErrors =
            !verificationShapeValid ||
            HasErrors(verification.Diagnostics);
        bool exactObservedAuthority =
            verification.ObservedOutput is not null &&
            verification.ObservedOutput ==
                authority.ProposalDocument.Value.ExpectedOutput &&
            verification.ObservedOutput.Path ==
                authority.OutputPath;
        bool verificationInvariant =
            verificationShapeValid &&
            verification.Succeeded != verificationHasErrors &&
            (!verification.Succeeded ||
             exactObservedAuthority);
        var artifacts =
            ImmutableArray.CreateBuilder<ArtifactResponse>();
        artifacts.Add(
            DocumentArtifact(
                "request",
                authority.RequestPath,
                authority.RequestDocument));
        artifacts.Add(
            DocumentArtifact(
                "proposal",
                authority.ProposalPath,
                authority.ProposalDocument));
        if (verification.ObservedOutput is not null)
            artifacts.Add(
                ObservedFileArtifact(
                    "output",
                    verification.ObservedOutput));
        artifacts.Add(
            DocumentArtifact(
                "manifest",
                authority.ManifestPath,
                manifestDocument));
        return Respond(
            command,
            verificationInvariant && verification.Succeeded
                ? "PASS"
                : "REFUSED",
            artifacts.ToImmutable(),
            verificationInvariant
                ? verification.Diagnostics
                :
                [
                    new Diagnostic(
                        "facegeom-hair-regions-verify-result-invalid",
                        DiagnosticSeverity.Error,
                        "Verify Succeeded must be true exactly when diagnostics contain no Error.")
                ],
            verificationInvariant && verification.Succeeded
                ? CommandExitCode.Success
                : verificationInvariant
                    ? ExitFor(verification.Diagnostics)
                    : CommandExitCode.ValidationFailure);
    }

    private static ArtifactResponse ObservedFileArtifact(
        string role,
        FaceGeomHairRegionsFile observed) =>
        new(
            role,
            null,
            observed.Path.Value,
            observed.Sha256.Value,
            observed.ByteLength,
            null);

    private static bool TryApplyOptions(
        ParsedCommand command,
        out ImmutableDictionary<string, string> values,
        out string message) =>
        TryRequireOptions(
            command,
            [
                "request",
                "request-sha256",
                "proposal",
                "proposal-sha256",
                "output",
                "manifest"
            ],
            out values,
            out message);

    private async ValueTask<ApplyAuthority> LoadApplyAuthorityAsync(
        ImmutableDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var requestPath = new WorkspacePath(values["request"]);
        var proposalPath = new WorkspacePath(values["proposal"]);
        var outputPath = new WorkspacePath(values["output"]);
        var manifestPath = new WorkspacePath(values["manifest"]);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            requestDocument = await documents.LoadRequestAsync(
                requestPath,
                new Sha256Hash(values["request-sha256"]),
                cancellationToken);
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            proposalDocument = await documents.LoadProposalAsync(
                proposalPath,
                new Sha256Hash(values["proposal-sha256"]),
                cancellationToken);
        return new ApplyAuthority(
            requestPath,
            proposalPath,
            outputPath,
            manifestPath,
            requestDocument,
            proposalDocument);
    }

    private static bool OutputPathsMatch(
        ApplyAuthority authority) =>
        authority.RequestDocument.Value.Output ==
            authority.OutputPath &&
        authority.RequestDocument.Value.Manifest ==
            authority.ManifestPath &&
        authority.ProposalDocument.Value.Output ==
            authority.OutputPath &&
        authority.ProposalDocument.Value.ExpectedOutput.Path ==
            authority.OutputPath &&
        authority.ProposalDocument.Value.Manifest ==
            authority.ManifestPath;

    private CommandExitCode OutputMismatch(
        ParsedCommand command) =>
        Respond(
            command,
            "REFUSED",
            [],
            [
                new Diagnostic(
                    "facegeom-hair-regions-output-mismatch",
                    DiagnosticSeverity.Error,
                    "CLI --output/--manifest must exactly match the request and proposal paths.")
            ],
            CommandExitCode.ValidationFailure);

    private CommandExitCode Collision(
        ParsedCommand command,
        string message) =>
        Respond(
            command,
            "REFUSED",
            [],
            [
                new Diagnostic(
                    "facegeom-hair-regions-output-exists",
                    DiagnosticSeverity.Error,
                    message)
            ],
            CommandExitCode.ValidationFailure);

    private static bool TryRequireOptions(
        ParsedCommand command,
        IReadOnlyCollection<string> required,
        out ImmutableDictionary<string, string> values,
        out string message)
    {
        int commandWordCount = command.Name.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries)
            .Length;
        if (command.Positionals.Length != commandWordCount)
        {
            values = ImmutableDictionary<string, string>.Empty;
            message =
                $"{command.Name} does not accept trailing positional arguments.";
            return false;
        }
        if (!command.DuplicateOptions.IsDefaultOrEmpty)
        {
            values = ImmutableDictionary<string, string>.Empty;
            message =
                $"Duplicate option '--{command.DuplicateOptions[0]}' is forbidden.";
            return false;
        }
        string? unknown = command.Options.Keys.FirstOrDefault(
            key => !required.Contains(
                key,
                StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
        {
            values = ImmutableDictionary<string, string>.Empty;
            message = $"Unknown option '--{unknown}'.";
            return false;
        }
        foreach (string name in required)
        {
            if (!command.Options.TryGetValue(name, out string? value) ||
                string.IsNullOrWhiteSpace(value))
            {
                values = ImmutableDictionary<string, string>.Empty;
                message = $"{command.Name} requires --{name} <value>.";
                return false;
            }
        }
        values = command.Options;
        message = string.Empty;
        return true;
    }

    private CommandExitCode Usage(
        ParsedCommand command,
        string message) =>
        Respond(
            command,
            "REFUSED",
            [],
            [
                new Diagnostic(
                    "usage-error",
                    DiagnosticSeverity.Error,
                    message)
            ],
            CommandExitCode.UsageError);

    private CommandExitCode Respond(
        ParsedCommand command,
        string phaseVerdict,
        ImmutableArray<ArtifactResponse> artifacts,
        ImmutableArray<Diagnostic> diagnostics,
        CommandExitCode exitCode,
        FaceGeomHairRegionsPreviewEvidence? previewEvidence = null)
    {
        var response = new Response(
            command.Name,
            phaseVerdict,
            artifacts,
            diagnostics,
            previewEvidence is null
                ? null
                : PreviewEvidenceResponse.From(previewEvidence),
            VisualAuthority: false,
            RuntimeAuthority: false);
        if (command.Json)
            output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else
        {
            output.WriteLine(
                $"{command.Name}: {phaseVerdict}");
            foreach (Diagnostic diagnostic in diagnostics)
                error.WriteLine(
                    $"{diagnostic.Code}: {diagnostic.Message}");
        }
        return exitCode;
    }

    private static CommandExitCode ExitFor(
        ImmutableArray<Diagnostic> diagnostics)
    {
        if (!HasErrors(diagnostics))
            return CommandExitCode.ValidationFailure;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }

    private static string CancellationDiagnosticMessage(
        FaceGeomHairRegionsOperationCanceledException
            exception) =>
        ProcessEvidenceDiagnosticMessage(
            exception.Message,
            exception.SurvivingProcessIds,
            exception.ProcessTerminationFailure);

    private static string ProcessEvidenceDiagnosticMessage(
        string message,
        ImmutableArray<int> survivingProcessIds,
        string? processTerminationFailure)
    {
        string processIds =
            survivingProcessIds.IsDefaultOrEmpty
                ? ""
                : " Surviving renderer " +
                  string.Join(
                      ", ",
                      survivingProcessIds
                          .Select(id => $"PID {id}")) +
                  ".";
        string termination =
            string.IsNullOrWhiteSpace(
                processTerminationFailure)
                ? ""
                : " Termination failure: " +
                  processTerminationFailure;
        return message +
               processIds +
               termination;
    }

    private static bool IsSecurityRefusal(Exception exception)
    {
        Exception? current = exception;
        for (int depth = 0;
             current is not null && depth < 16;
             depth++)
        {
            if (current is
                    UnauthorizedAccessException or
                    SecurityException ||
                IsSecurityMessage(current.Message))
                return true;
            current = current.InnerException;
        }
        return false;
    }

    private static bool IsSecurityMessage(string message) =>
        message.Contains(
            "protected",
            StringComparison.OrdinalIgnoreCase) ||
        message.Contains(
            "outside",
            StringComparison.OrdinalIgnoreCase) ||
        message.Contains(
            "reparse",
            StringComparison.OrdinalIgnoreCase) ||
        message.Contains(
            "device",
            StringComparison.OrdinalIgnoreCase) ||
        message.Contains(
            "alternate data",
            StringComparison.OrdinalIgnoreCase) ||
        message.Contains(
            "unsafe",
            StringComparison.OrdinalIgnoreCase);

    private static bool HasErrors(
        ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static ArtifactResponse DocumentArtifact<T>(
        string role,
        WorkspacePath path,
        StrictJsonDocumentAuthority<T> document) =>
        new(
            role,
            null,
            path.Value,
            document.Sha256.Value,
            document.Utf8Json.Length,
            null);

    private static ArtifactResponse SuggestedArtifact(
        string role,
        WorkspacePath path) =>
        new(role, null, path.Value, null, 0, null);

    private sealed record ArtifactResponse(
        string Role,
        string? StructuralId,
        string Path,
        string? Sha256,
        long ByteLength,
        long? NonEmptyPixelCount);

    private sealed record ApplyAuthority(
        WorkspacePath RequestPath,
        WorkspacePath ProposalPath,
        WorkspacePath OutputPath,
        WorkspacePath ManifestPath,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            RequestDocument,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            ProposalDocument);

    private sealed record Response(
        string Command,
        string PhaseVerdict,
        ImmutableArray<ArtifactResponse> Artifacts,
        ImmutableArray<Diagnostic> Diagnostics,
        PreviewEvidenceResponse? PreviewEvidence,
        bool VisualAuthority,
        bool RuntimeAuthority);

    private sealed record PreviewEvidenceResponse(
        string StagedFaceGeomSha256,
        string ProposalSha256,
        string IntakeSha256,
        string RendererSha256,
        string TextureFingerprintSha256,
        string EvidenceDocumentSha256,
        int DetectedFaceCount,
        int LandmarkCount,
        int SemanticAnchorCount)
    {
        public static PreviewEvidenceResponse From(
            FaceGeomHairRegionsPreviewEvidence value) =>
            new(
                value.StagedFaceGeomSha256.Value,
                value.ProposalSha256.Value,
                value.IntakeSha256.Value,
                value.RendererSha256.Value,
                value.TextureFingerprintSha256.Value,
                value.EvidenceDocumentSha256.Value,
                value.DetectedFaceCount,
                value.LandmarkCount,
                value.SemanticAnchorCount);
    }
}
