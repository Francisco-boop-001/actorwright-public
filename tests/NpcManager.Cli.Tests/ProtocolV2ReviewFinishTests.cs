using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2ReviewFinishTests
{
    private const string PredecessorDigest =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string GuiDigest =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static readonly string[] RequiredReviewOptionNames =
    [
        "workflow-bundle", "workflow-bundle-sha256", "proposal",
        "proposal-sha256", "preview-manifest",
        "preview-manifest-sha256", "outcome", "receipt-output",
        "workflow-output"
    ];
    private static readonly string[] ReviewOptionNames =
    [
        "workflow-bundle", "workflow-bundle-sha256", "proposal",
        "proposal-sha256", "preview-manifest",
        "preview-manifest-sha256", "outcome", "operator-attestation",
        "reviewer-note", "receipt-output", "workflow-output"
    ];

    public static async Task RunAsync()
    {
        string rootValue = Path.Combine(
            @"K:\Actorwright\artifacts\test-work",
            $"protocol-v2-review-finish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootValue);
        try
        {
            var root = new WorkspacePath(rootValue);
            var policy = new KOnlyWorkspacePolicy(
                root,
                new WorkspacePath(@"F:\ExampleGame"));
            var lifecycle = new AgentWorkflowBundleTransitionService(
                new AgentWorkflowBundleCodec(policy, root));
            var receiptService = new AgentReviewReceiptService(policy, root);
            ReviewFixture package = ReviewFixture.CreatePackage(
                root,
                lifecycle);
            ReviewFixture finish = ReviewFixture.CreateFinish(
                root,
                lifecycle);

            AssertUnadvertisedComposition(root);
            await AssertReviewRequiredFinishBridgeAsync(root, lifecycle, receiptService, finish);
            await AssertProductionFinishAdmissionAsync(
                root,
                lifecycle,
                receiptService,
                finish);
            await AssertProductionPackageAdmissionAsync(
                root,
                lifecycle,
                receiptService);
            await AssertAcceptedSuccessorsAsync(
                root,
                lifecycle,
                receiptService,
                package,
                finish);
            await AssertNonAuthorizingOutcomesAsync(
                root,
                lifecycle,
                receiptService,
                package);
            await AssertExactSyntaxRefusalsAsync(
                root,
                lifecycle,
                receiptService,
                package);
            await AssertBindingAndCollisionRefusalsAsync(
                root,
                lifecycle,
                receiptService,
                package);
            await AssertWorkflowPostPromotionRollbackTruthAsync(
                root,
                receiptService,
                package);
            await AssertTwoFileSecondPromotionRollbackAsync(root);
            await AssertPrepublicationReceiptFailureProjectionAsync(
                root,
                lifecycle,
                package);
            await AssertPromotedReadbackFailureTruthAsync(
                root,
                lifecycle,
                package);
            await AssertFirstPostRenameValidationFailureTruthAsync(
                root,
                lifecycle,
                package);
            await AssertCancellationBoundaryAsync(
                root,
                lifecycle,
                receiptService,
                package);
            await AssertTypedReviewPathRefusalsAsync(
                root,
                lifecycle,
                receiptService,
                package);
            await AssertRunnerJournalRedactionAsync(
                root,
                lifecycle,
                receiptService,
                package);
            AssertFinishProposalStore(root);
        }
        finally
        {
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }
    }

    private static void AssertUnadvertisedComposition(WorkspacePath root)
    {
        AgentCommandContract gui = AgentCommandRegistry.GetRequired("gui");
        Require(gui.Readiness == ProtocolReadiness.Legacy &&
                gui.ResultSchemaIds.IsEmpty &&
                gui.Options.Select(item => item.CliName).SequenceEqual(
                    [
                        "launch", "executable", "workflow-bundle",
                        "workflow-bundle-sha256"
                    ],
                    StringComparer.Ordinal) &&
                AgentCommandRegistry.All.Length == 142,
            "Task 3A advertised protocol-v2 gui or changed legacy/Desktop metadata.");
        ImmutableArray<IProtocolV2CommandAdapter> adapters =
            ProtocolV2GoldenWorkflowComposition.Create(root);
        Require(adapters.Length == 8 &&
                adapters.Count(item => item.Commands.Contains("npc finish analyze", StringComparer.Ordinal) &&
                    item.Commands.Contains("npc finish apply", StringComparer.Ordinal)) == 1 &&
                adapters.Count(item => item.Commands.Contains(
                    "gui",
                    StringComparer.Ordinal)) == 1 &&
                typeof(ProtocolV2ReviewReceiptAdapter).Assembly
                    .GetReferencedAssemblies().All(item =>
                        !string.Equals(
                            item.Name,
                            "NpcManager.Desktop",
                            StringComparison.Ordinal)),
            "Production composition did not add exactly one Desktop-free gui receipt adapter.");
    }

    private static async Task AssertReviewRequiredFinishBridgeAsync(WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle, AgentReviewReceiptService receipts, ReviewFixture finish)
    {
        WorkspacePath source = finish.SourceManifest ?? throw new InvalidOperationException("Finish fixture lacks its source manifest.");
        byte[] sourceBytes = File.ReadAllBytes(source.Value);
        WorkflowArtifactBinding package = new(WorkflowArtifactKinds.NpcPackageManifest, "application/json", source,
            sourceBytes.LongLength, Hash(sourceBytes), "npc create-from-jslot", PredecessorDigest, []);
        AgentWorkflowBundleTransition pending = lifecycle.WriteInitial(finish.Transition.Document.Bundle.Npc,
            PredecessorDigest, [.. finish.Transition.Document.Bundle.Artifacts, package], Child(root, "pending-finish-review.json"));
        ReviewFixture pendingFixture = finish with { Transition = pending };
        (WorkspacePath receipt, WorkspacePath successor) = Outputs(root, "pending-finish-accepted");
        ProtocolCommandResult reviewed = await new ProtocolV2ReviewReceiptAdapter(root, receipts, lifecycle).RunAsync(
            pendingFixture.Command(receipt, successor, "accepted", "true"), GuiDigest, CancellationToken.None);
        using (reviewed.TerminalArtifactLease)
        {
            Require(!reviewed.Diagnostics.Any(row => row.Severity == DiagnosticSeverity.Error) && File.Exists(receipt.Value) && File.Exists(successor.Value),
                "A review-required Finish workflow must reach the real receipt producer: " + JsonSerializer.Serialize(reviewed.Diagnostics));
            RequireWriteLocked(successor.Value, "The reviewed successor must remain leased through envelope emission.");
        }
        AgentWorkflowBundleTransition admitted = lifecycle.LoadForReviewedCommand(successor, Hash(File.ReadAllBytes(successor.Value)),
            "npc finish apply", document => receipts.LoadForSuccessor(document, receipt, Hash(File.ReadAllBytes(receipt.Value))));
        Require(admitted.Document.Bundle.Artifacts.Length == pending.Document.Bundle.Artifacts.Length + 1 &&
            admitted.Evaluation.Authority.Single(row => row.Kind == AgentAuthorityKind.HumanVisualAcceptance).State == AgentAuthorityState.Required,
            "A real operator receipt must retain exact lineage without granting visual acceptance.");
    }

    private static async Task AssertProductionFinishAdmissionAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture finish)
    {
        ProtocolV2ReviewReceiptAdapter adapter = new(
            root,
            receiptService,
            lifecycle);
        (WorkspacePath receipt, WorkspacePath workflow) = Outputs(
            root,
            "production-finish");
        ProtocolCommandResult result = await adapter.RunAsync(
            finish.Command(receipt, workflow, "accepted", "true"),
            GuiDigest,
            CancellationToken.None);
        AcceptedReviewEvidence evidence;
        WorkflowArtifactBinding request = finish.Transition.Document.Bundle
            .Artifacts.Single(item => item.Kind ==
                WorkflowArtifactKinds.NpcFinishCoreRequest);
        WorkspacePath sourceManifest = finish.SourceManifest ??
            throw new InvalidOperationException(
                "Canonical Finish fixture has no source manifest.");
        WorkspacePath sourcePlugin = finish.SourcePlugin ??
            throw new InvalidOperationException(
                "Canonical Finish fixture has no source plugin.");
        WorkspacePath sourceFaceGeom = finish.SourceFaceGeom ??
            throw new InvalidOperationException(
                "Canonical Finish fixture has no source FaceGeom.");
        WorkspacePath sourceFaceTint = finish.SourceFaceTint ??
            throw new InvalidOperationException(
                "Canonical Finish fixture has no source FaceTint.");
        WorkspacePath sourceProposal = Child(
            finish.SourcePackageRoot ??
            throw new InvalidOperationException(
                "Canonical Finish fixture has no source package root."),
            "evidence/npc-creation-proposal.json");
        try
        {
            evidence = AssertAcceptedWhileRetained(
                result,
                finish,
                [
                    WorkflowArtifactKinds.NpcFinishCoreRequest,
                    WorkflowArtifactKinds.NpcFinishCoreProposal,
                    WorkflowArtifactKinds.NpcPreviewManifest,
                    WorkflowArtifactKinds.ReviewReceipt
                ],
                "npc finish apply");
            Require(finish.Proposal.SemanticSha256 is { } semantic &&
                    IsUpperSha256(semantic),
                "Finish workflow semantic SHA-256 is not uppercase wire data.");
            RequireWriteLocked(request.Path.Value, "Finish request input");
            RequireWriteLocked(finish.Proposal.Path.Value,
                "Finish proposal input");
            RequireWriteLocked(finish.Preview.Path.Value,
                "preview manifest input");
            RequireWriteLocked(sourceManifest.Value,
                "Finish source package manifest");
            RequireWriteLocked(sourcePlugin.Value,
                "Finish source package plugin");
            RequireWriteLocked(sourceFaceGeom.Value,
                "Finish source package FaceGeom");
            RequireWriteLocked(sourceFaceTint.Value,
                "Finish source package FaceTint");
            RequireWriteLocked(sourceProposal.Value,
                "Finish source package creation proposal");
        }
        finally
        {
            result.TerminalArtifactLease?.Dispose();
        }
        AssertAcceptedReleased(evidence, finish);
        RequireWriteReleased(request.Path.Value, "Finish request input");
        RequireWriteReleased(finish.Proposal.Path.Value,
            "Finish proposal input");
        RequireWriteReleased(finish.Preview.Path.Value,
            "preview manifest input");
        RequireWriteReleased(sourceManifest.Value,
            "Finish source package manifest");
        RequireWriteReleased(sourcePlugin.Value,
            "Finish source package plugin");
        RequireWriteReleased(sourceFaceGeom.Value,
            "Finish source package FaceGeom");
        RequireWriteReleased(sourceFaceTint.Value,
            "Finish source package FaceTint");
        RequireWriteReleased(sourceProposal.Value,
            "Finish source package creation proposal");

        ReviewFixture intakePreview = ReviewFixture.CreateFinish(
            root, lifecycle, "finish-preview-intake-input", includePreviewIntake: true);
        var intakeAdmission = ProtocolV2ReviewPredecessorAdmission.CreateDefault(root, lifecycle);
        using (ProtocolV2ReviewPredecessorLease intakeLease = await intakeAdmission.AdmitAsync(
                   new ProtocolV2PhysicalFileBinding(intakePreview.Transition.Document.Path, intakePreview.Transition.Document.Sha256),
                   new ProtocolV2PhysicalFileBinding(intakePreview.Proposal.Path, intakePreview.Proposal.Sha256),
                   new ProtocolV2PhysicalFileBinding(intakePreview.Preview.Path, intakePreview.Preview.Sha256), CancellationToken.None))
            intakeLease.Revalidate();
        await AssertProductionAdmissionRefusalAsync(root, lifecycle, receiptService,
            ReviewFixture.CreateFinish(root, lifecycle, "finish-preview-wrong-package",
                includePreviewIntake: true, wrongPreviewPackage: true),
            "finish-preview-wrong-package", command => command);

        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreatePackage(root, lifecycle, "stale-package"),
            "stale-package",
            command => command with
            {
                Options = command.Options.SetItem(
                    "proposal-sha256",
                    new string('C', 64))
            });
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreateFinish(root, lifecycle, "stale-preview"),
            "stale-preview",
            command => command with
            {
                Options = command.Options.SetItem(
                    "preview-manifest-sha256",
                    new string('C', 64))
            });

        ReviewFixture staleRequest = ReviewFixture.CreateFinish(
            root,
            lifecycle,
            "stale-request");
        WorkflowArtifactBinding staleRequestBinding = staleRequest.Transition
            .Document.Bundle.Artifacts.Single(item => item.Kind ==
                WorkflowArtifactKinds.NpcFinishCoreRequest);
        File.AppendAllText(staleRequestBinding.Path.Value, " ",
            new UTF8Encoding(false));
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            staleRequest,
            "stale-request",
            command => command);

        ReviewFixture staleProposal = ReviewFixture.CreateFinish(
            root,
            lifecycle,
            "stale-proposal-file");
        File.AppendAllText(staleProposal.Proposal.Path.Value, " ",
            new UTF8Encoding(false));
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            staleProposal,
            "stale-proposal-file",
            command => command);

        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreateFinish(
                root,
                lifecycle,
                "nested-request-drift",
                nestedRequestDrift: true),
            "nested-request-drift",
            command => command);
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreateFinish(
                root,
                lifecycle,
                "workflow-npc-drift",
                workflowNpcDrift: true),
            "workflow-npc-drift",
            command => command);
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreateFinish(
                root,
                lifecycle,
                "preview-identity-drift",
                previewIdentityDrift: true),
            "preview-identity-drift",
            command => command);
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreateFinish(
                root,
                lifecycle,
                "preview-lineage-drift",
                previewLineageDrift: true),
            "preview-lineage-drift",
            command => command);
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreateFinish(
                root,
                lifecycle,
                "preview-asset-identity-drift",
                previewAssetIdentityDrift: true),
            "preview-asset-identity-drift",
            command => command);

        ReviewFixture staleManifest = ReviewFixture.CreateFinish(
            root,
            lifecycle,
            "finish-source-manifest-drift");
        File.AppendAllText(
            staleManifest.SourceManifest!.Value.Value,
            " ",
            new UTF8Encoding(false));
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            staleManifest,
            "finish-source-manifest-drift",
            command => command);

        ReviewFixture staleTree = ReviewFixture.CreateFinish(
            root,
            lifecycle,
            "finish-source-tree-drift");
        File.WriteAllBytes(
            Path.Combine(
                staleTree.SourcePackageRoot!.Value.Value,
                "undeclared.bin"),
            [4, 3, 2, 1]);
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            staleTree,
            "finish-source-tree-drift",
            command => command);

        ReviewFixture staleAsset = ReviewFixture.CreateFinish(
            root,
            lifecycle,
            "finish-source-asset-drift");
        File.WriteAllText(
            staleAsset.SourceFaceTint!.Value.Value,
            "changed-facetint",
            new UTF8Encoding(false));
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            staleAsset,
            "finish-source-asset-drift",
            command => command);

        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreateFinish(
                root,
                lifecycle,
                "finish-cross-package",
                crossPackageManifest: true),
            "finish-cross-package",
            command => command);

        await AssertPackageOutputOverlapRefusedAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreateFinish(
                root,
                lifecycle,
                "finish-receipt-under-package"),
            receiptUnderPackage: true);
        await AssertPackageOutputOverlapRefusedAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreateFinish(
                root,
                lifecycle,
                "finish-workflow-under-package"),
            receiptUnderPackage: false);
    }

    private static async Task AssertProductionPackageAdmissionAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService)
    {
        ReviewFixture valid = ReviewFixture.CreatePackage(
            root,
            lifecycle,
            "production-package-valid");
        (WorkspacePath receipt, WorkspacePath workflow) = Outputs(
            root,
            "production-package-valid");
        ProtocolCommandResult accepted = await new ProtocolV2ReviewReceiptAdapter(
                root,
                receiptService,
                lifecycle)
            .RunAsync(
                valid.Command(receipt, workflow, "accepted", "true"),
                GuiDigest,
                CancellationToken.None);
        try
        {
            _ = AssertAcceptedWhileRetained(
                accepted,
                valid,
                [
                    WorkflowArtifactKinds.NpcPackageManifest,
                    WorkflowArtifactKinds.NpcPreviewManifest,
                    WorkflowArtifactKinds.ReviewReceipt
                ],
                "npc finish analyze");
        }
        finally
        {
            accepted.TerminalArtifactLease?.Dispose();
        }

        ReviewFixture copied = ReviewFixture.CreatePackage(
            root, lifecycle, "production-package-copied", copyPreviewAssets: true);
        var copiedAdmission = ProtocolV2ReviewPredecessorAdmission.CreateDefault(root, lifecycle);
        using (ProtocolV2ReviewPredecessorLease copiedLease = await copiedAdmission.AdmitAsync(
                   new ProtocolV2PhysicalFileBinding(copied.Transition.Document.Path, copied.Transition.Document.Sha256),
                   new ProtocolV2PhysicalFileBinding(copied.Proposal.Path, copied.Proposal.Sha256),
                   new ProtocolV2PhysicalFileBinding(copied.Preview.Path, copied.Preview.Sha256), CancellationToken.None))
            copiedLease.Revalidate();
        await AssertProductionAdmissionRefusalAsync(root, lifecycle, receiptService,
            ReviewFixture.CreatePackage(root, lifecycle, "production-package-copied-provider",
                copyPreviewAssets: true, wrongPreviewProvider: true),
            "production-package-copied-provider", command => command);
        await AssertProductionAdmissionRefusalAsync(root, lifecycle, receiptService,
            ReviewFixture.CreatePackage(root, lifecycle, "production-package-copied-path",
                copyPreviewAssets: true, wrongPreviewPath: true),
            "production-package-copied-path", command => command);

        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            ReviewFixture.CreatePackage(
                root,
                lifecycle,
                "production-package-asset-drift",
                previewAssetDrift: true),
            "production-package-asset-drift",
            command => command);

        ReviewFixture emptyDirectory = ReviewFixture.CreatePackage(
            root,
            lifecycle,
            "production-package-empty-directory");
        string emptyPackageRoot = Path.GetDirectoryName(
            emptyDirectory.Proposal.Path.Value) ??
            throw new InvalidOperationException(
                "Package fixture manifest has no root.");
        Directory.CreateDirectory(Path.Combine(
            emptyPackageRoot,
            "undeclared-empty"));
        await AssertProductionAdmissionRefusalAsync(
            root,
            lifecycle,
            receiptService,
            emptyDirectory,
            "production-package-empty-directory",
            command => command);

        ReviewFixture tree = ReviewFixture.CreatePackage(
            root,
            lifecycle,
            "production-package-tree-drift");
        var admission = ProtocolV2ReviewPredecessorAdmission.CreateDefault(
            root,
            lifecycle);
        using ProtocolV2ReviewPredecessorLease lease = await admission.AdmitAsync(
            new ProtocolV2PhysicalFileBinding(
                tree.Transition.Document.Path,
                tree.Transition.Document.Sha256),
            new ProtocolV2PhysicalFileBinding(
                tree.Proposal.Path,
                tree.Proposal.Sha256),
            new ProtocolV2PhysicalFileBinding(
                tree.Preview.Path,
                tree.Preview.Sha256),
            CancellationToken.None);
        string packageRoot = Path.GetDirectoryName(tree.Proposal.Path.Value) ??
            throw new InvalidOperationException(
                "Package fixture manifest has no root.");
        string undeclared = Path.Combine(packageRoot, "undeclared.bin");
        File.WriteAllBytes(undeclared, [1, 2, 3]);
        RequireThrows<InvalidDataException>(
            lease.Revalidate,
            "Package predecessor revalidation accepted a newly created undeclared sibling.");
    }

    private static async Task AssertPackageOutputOverlapRefusedAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture fixture,
        bool receiptUnderPackage)
    {
        WorkspacePath packageRoot = fixture.SourcePackageRoot ??
            throw new InvalidOperationException(
                "Finish fixture did not expose its package root.");
        (WorkspacePath ordinaryReceipt, WorkspacePath ordinaryWorkflow) =
            Outputs(
                root,
                receiptUnderPackage
                    ? "receipt-under-package"
                    : "workflow-under-package");
        WorkspacePath receipt = receiptUnderPackage
            ? Child(packageRoot, "forbidden-receipt.json")
            : ordinaryReceipt;
        WorkspacePath workflow = receiptUnderPackage
            ? ordinaryWorkflow
            : Child(packageRoot, "forbidden-workflow.json");
        ProtocolCommandResult result = await new ProtocolV2ReviewReceiptAdapter(
                root,
                receiptService,
                lifecycle)
            .RunAsync(
                fixture.Command(receipt, workflow, "accepted", "true"),
                GuiDigest,
                CancellationToken.None);
        try
        {
            string outputOption = receiptUnderPackage
                ? "receipt-output"
                : "workflow-output";
            string expectedCode = receiptUnderPackage
                ? ProtocolV2DiagnosticCodes.ReviewReceiptPathRefused
                : ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused;
            Require(result.Diagnostics is [var diagnostic] &&
                    diagnostic.Code == expectedCode &&
                    diagnostic.Class == DiagnosticClass.Security &&
                    diagnostic.Recovery is
                    {
                        Action: RecoveryAction.ChooseFreshOutput,
                        RetryUnchangedSafe: false
                    } recovery &&
                    recovery.Option == outputOption &&
                    result.Artifacts.IsEmpty &&
                    result.NextActions.IsEmpty &&
                    result.Effects.Select(Effect).SequenceEqual(
                        [
                            "readWorkspace|completed|workspace",
                            "writeNewArtifact|refused|k-local-output"
                        ],
                        StringComparer.Ordinal) &&
                    !File.Exists(receipt.Value) &&
                    !File.Exists(workflow.Value),
                $"Review admitted '--{outputOption}' beneath its retained source package.");
        }
        finally
        {
            result.TerminalArtifactLease?.Dispose();
        }
    }

    private static Task AssertPromotedReadbackFailureTruthAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        ReviewFixture fixture) => AssertPublishedUnverifiedFailureTruthAsync(
        root,
        lifecycle,
        fixture,
        "promoted-readback",
        static () => new FailPromotedReadbackHooks());

    private static Task AssertFirstPostRenameValidationFailureTruthAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        ReviewFixture fixture) => AssertPublishedUnverifiedFailureTruthAsync(
        root,
        lifecycle,
        fixture,
        "first-post-rename-validation",
        static () => new FailFirstPostRenameValidationHooks());

    private static async Task AssertPublishedUnverifiedFailureTruthAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        ReviewFixture fixture,
        string failureName,
        Func<IFaceGeomHairRegionsPinnedFileSystemHooks> hooks)
    {
        Require(AgentReviewContract.TryGetPurpose(
                fixture.Proposal.Kind,
                out string scope,
                out string authorityNoticeSha256),
            "Published-unverified fixture proposal has no canonical purpose.");
        string[] expectedInputs =
        [
            fixture.Transition.Document.Sha256,
            fixture.Proposal.Sha256,
            fixture.Preview.Sha256,
            authorityNoticeSha256
        ];
        Array.Sort(expectedInputs, StringComparer.Ordinal);
        foreach (ReviewOutcome outcome in new[]
                 {
                     ReviewOutcome.Accepted,
                     ReviewOutcome.Rejected,
                     ReviewOutcome.RevisionRequested
                 })
        {
            var receiptService = new AgentReviewReceiptService(
                fixture.Policy,
                root);
            SetPinnedFileSystem(
                receiptService,
                new FaceGeomHairRegionsPinnedFileSystem(
                    root,
                    hooks()));
            string name = failureName + "-" +
                outcome.ToString().ToLowerInvariant();
            (WorkspacePath receipt, WorkspacePath workflow) = Outputs(
                root,
                name);
            AgentReviewReceipt expectedReceipt =
                AgentWorkflowContractValidation.CreateReviewReceipt(
                    fixture.Transition.Document.Sha256,
                    fixture.Proposal.Sha256,
                    [fixture.Proposal.Sha256, fixture.Preview.Sha256],
                    authorityNoticeSha256,
                    outcome,
                    scope,
                    reviewerNote: null);
            byte[] expectedCanonical =
                AgentReviewReceiptService.ComputeCanonicalBytes(
                    expectedReceipt);
            ProtocolCommandResult result = await Adapter(
                    root,
                    lifecycle,
                    receiptService,
                    new FakeAdmission(fixture.Transition))
                .RunAsync(
                    fixture.Command(
                        receipt,
                        workflow,
                        outcome switch
                        {
                            ReviewOutcome.Accepted => "accepted",
                            ReviewOutcome.Rejected => "rejected",
                            _ => "revision-requested"
                        },
                        outcome == ReviewOutcome.Accepted ? "true" : null),
                    GuiDigest,
                    CancellationToken.None);
            try
            {
                Require(result.Diagnostics is [var diagnostic] &&
                        diagnostic.Code == ProtocolV2DiagnosticCodes
                            .ReviewReceiptPersistenceFailed &&
                        diagnostic.Class == DiagnosticClass.Operation &&
                        diagnostic.Recovery is
                        {
                            Action: RecoveryAction.RepairEnvironment,
                            Option: null,
                            ArtifactKind: WorkflowArtifactKinds.ReviewReceipt,
                            RetryUnchangedSafe: false
                        } recovery &&
                        recovery.Constraint ==
                            "Repair the receipt publication environment. Replay the unchanged review with both fresh --receipt-output and --workflow-output paths." &&
                        result.Artifacts is [var artifact] &&
                        artifact.Kind == WorkflowArtifactKinds.ReviewReceipt &&
                        artifact.SchemaOrMediaType ==
                            AgentWorkflowSchemas.ReviewReceiptV1 &&
                        artifact.State == "publishedUnverified" &&
                        artifact.ProducerCommand == "gui" &&
                        artifact.RequestDigest == GuiDigest &&
                        artifact.InputBindings.SequenceEqual(
                            expectedInputs.Distinct(StringComparer.Ordinal),
                            StringComparer.Ordinal) &&
                        string.Equals(
                            artifact.Path,
                            receipt.Value,
                            StringComparison.OrdinalIgnoreCase) &&
                        artifact.Size is > 0 &&
                        IsUpperSha256(artifact.Sha256 ?? string.Empty) &&
                        result.ResultSchemaId ==
                            AgentProtocolSchemaIds.ReviewReceiptResult &&
                        result.NextActions.IsEmpty &&
                        HasPublishedUnverifiedAuthority(result) &&
                        HasPublishedUnverifiedResult(
                            result,
                            outcome,
                            receipt,
                            artifact) &&
                        result.Effects.Select(Effect).SequenceEqual(
                            outcome == ReviewOutcome.Accepted
                                ?
                                [
                                    "readWorkspace|completed|workspace",
                                    "writeNewArtifact|completed|k-local-output",
                                    "writeNewArtifact|blocked|k-local-output"
                                ]
                                :
                                [
                                    "readWorkspace|completed|workspace",
                                    "writeNewArtifact|completed|k-local-output"
                                ],
                            StringComparer.Ordinal) &&
                        !File.Exists(workflow.Value),
                    $"{outcome} {failureName} failure overstated receipt verification or stage effects.");
                RequireWriteLocked(
                    receipt.Value,
                    $"{outcome} published-unverified receipt");
            }
            finally
            {
                result.TerminalArtifactLease?.Dispose();
            }
            byte[] committed = File.ReadAllBytes(receipt.Value);
            ProtocolArtifact committedArtifact = result.Artifacts.Single();
            RequireWriteReleased(
                receipt.Value,
                $"{outcome} published-unverified receipt");
            Require(committedArtifact.Size == committed.LongLength &&
                    committedArtifact.Sha256 == Hash(committed) &&
                    committed.SequenceEqual(expectedCanonical) &&
                    File.ReadAllBytes(receipt.Value).SequenceEqual(committed),
                $"{outcome} {failureName} failure changed committed receipt bytes.");
        }
    }

    private static async Task AssertPrepublicationReceiptFailureProjectionAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        ReviewFixture fixture)
    {
        await AssertPrepublicationReceiptFailureProjectionCaseAsync(
            root,
            lifecycle,
            fixture,
            "prepublication-reparse-race",
            new RefusePrepublicationReparseHooks(),
            ProtocolV2DiagnosticCodes.ReviewReceiptPathRefused,
            DiagnosticClass.Security,
            RecoveryAction.ChooseFreshOutput,
            "Choose a fresh --receipt-output with a non-reparse parent and replay the unchanged review request.",
            "readWorkspace|completed|workspace",
            "writeNewArtifact|refused|k-local-output");
        await AssertPrepublicationReceiptFailureProjectionCaseAsync(
            root,
            lifecycle,
            fixture,
            "prepublication-before-rename-write",
            new FailBeforeRenameHooks(),
            ProtocolV2DiagnosticCodes.ReviewReceiptPersistenceFailed,
            DiagnosticClass.Operation,
            RecoveryAction.RepairEnvironment,
            "Repair the receipt publication environment and replay the unchanged review request with a fresh --receipt-output.",
            "readWorkspace|completed|workspace",
            "writeNewArtifact|failed|k-local-output");
        await AssertPrepublicationReceiptFailureProjectionCaseAsync(
            root,
            lifecycle,
            fixture,
            "prepublication-access-denied",
            new FailAccessDeniedBeforeCreateHooks(),
            ProtocolV2DiagnosticCodes.ReviewReceiptPersistenceFailed,
            DiagnosticClass.Operation,
            RecoveryAction.RepairEnvironment,
            "Repair the receipt publication environment and replay the unchanged review request with a fresh --receipt-output.",
            "readWorkspace|completed|workspace",
            "writeNewArtifact|failed|k-local-output");
        await AssertPrepublicationReceiptFailureProjectionCaseAsync(
            root,
            lifecycle,
            fixture,
            "prepublication-staged-readback-mismatch",
            new CorruptStagedReadbackHooks(),
            ProtocolV2DiagnosticCodes.ReviewReceiptPersistenceFailed,
            DiagnosticClass.Operation,
            RecoveryAction.RepairEnvironment,
            "Repair the receipt publication environment and replay the unchanged review request with a fresh --receipt-output.",
            "readWorkspace|completed|workspace",
            "writeNewArtifact|failed|k-local-output");
    }

    private static async Task AssertTwoFileSecondPromotionRollbackAsync(
        WorkspacePath root)
    {
        WorkspacePath first = new(Path.Combine(
            root.Value,
            "two-file-first.json"));
        WorkspacePath second = new(Path.Combine(
            root.Value,
            "two-file-second.json"));
        byte[] secondSentinel = "occupied second output"u8.ToArray();
        File.WriteAllBytes(second.Value, secondSentinel);
        var hooks = new ObserveTwoFilePromotionsHooks();
        var boundary = new FaceGeomHairRegionsWorkspaceBoundary(
            root,
            hooks);
        var documents = new FaceGeomHairRegionsDocumentCodec(boundary);
        var handler = new FaceGeomHairRegionsCommandHandler(
            root,
            new FaceGeomHairRegionsAnalyzer(root, boundary),
            new FaceGeomHairRegionsProposer(root, documents),
            new FaceGeomHairRegionsApplyService(
                root,
                new BethesdaFaceGeomHairRegionsVerifier(),
                documents),
            documents,
            new UnconfiguredFaceGeomHairRegionsPreviewFactory(),
            TextWriter.Null,
            TextWriter.Null);
        System.Reflection.MethodInfo transaction =
            typeof(FaceGeomHairRegionsCommandHandler).GetMethod(
                "WritePairNoOverwriteAsync",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "FaceGeom two-file transaction method was not found.");
        FaceGeomHairRegionsCommandFileException? failure = null;
        try
        {
            var pending = (ValueTask)(transaction.Invoke(
                handler,
                [
                    first,
                    "first public output"u8.ToArray().ToImmutableArray(),
                    second,
                    "second public output"u8.ToArray().ToImmutableArray(),
                    CancellationToken.None
                ]) ?? throw new InvalidOperationException(
                    "FaceGeom two-file transaction returned no operation."));
            await pending;
        }
        catch (FaceGeomHairRegionsCommandFileException exception)
        {
            failure = exception;
        }

        Require(failure is not null &&
                failure.SurvivingArtifacts.IsEmpty &&
                hooks.RenameDestinations.Count == 2 &&
                string.Equals(
                    hooks.RenameDestinations[0],
                    first.Value,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    hooks.RenameDestinations[1],
                    second.Value,
                    StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(first.Value) &&
                File.ReadAllBytes(second.Value)
                    .SequenceEqual(secondSentinel) &&
                !Directory.EnumerateFiles(
                        root.Value,
                        "*.npcmanager.tmp",
                        SearchOption.TopDirectoryOnly)
                    .Any(),
            "Two-file transaction did not roll back its exact first promoted output after the second no-overwrite promotion failed.");
    }

    private static async Task AssertWorkflowPostPromotionRollbackTruthAsync(
        WorkspacePath root,
        AgentReviewReceiptService receiptService,
        ReviewFixture fixture)
    {
        var codec = new AgentWorkflowBundleCodec(
            fixture.Policy,
            root);
        SetPinnedFileSystem(
            codec,
            new FaceGeomHairRegionsPinnedFileSystem(
                root,
                new CorruptPromotedReadbackHooks()));
        var lifecycle = new AgentWorkflowBundleTransitionService(codec);
        (WorkspacePath receipt, WorkspacePath workflow) = Outputs(
            root,
            "workflow-promoted-readback-rollback");
        ProtocolCommandResult result = await Adapter(
                root,
                lifecycle,
                receiptService,
                new FakeAdmission(fixture.Transition))
            .RunAsync(
                fixture.Command(
                    receipt,
                    workflow,
                    "accepted",
                    "true"),
                GuiDigest,
                CancellationToken.None);
        ProtocolArtifact committedArtifact;
        try
        {
            Require(result.Diagnostics is [var diagnostic] &&
                    diagnostic.Code == ProtocolV2DiagnosticCodes
                        .WorkflowBundlePersistenceFailed &&
                    diagnostic.Class == DiagnosticClass.Operation &&
                    diagnostic.Recovery is
                    {
                        Action: RecoveryAction.RepairEnvironment,
                        Option: null,
                        ArtifactKind: "workflow-bundle",
                        RetryUnchangedSafe: false,
                        Constraint: "Repair the workflow publication environment. Replay the unchanged review with both fresh --receipt-output and --workflow-output paths."
                    } &&
                    result.Artifacts is [var artifact] &&
                    artifact.Kind == WorkflowArtifactKinds.ReviewReceipt &&
                    artifact.State == "independentlyVerified" &&
                    artifact.Size is > 0 &&
                    IsUpperSha256(artifact.Sha256 ?? string.Empty) &&
                    string.Equals(
                        artifact.Path,
                        receipt.Value,
                        StringComparison.OrdinalIgnoreCase) &&
                    result.ResultSchemaId ==
                        AgentProtocolSchemaIds.ReviewReceiptResult &&
                    HasResult(
                        result,
                        created: true,
                        status: "review-receipt-persisted",
                        outcome: "accepted",
                        hasReceipt: true,
                        hasWorkflow: false) &&
                    HasAdmittedAuthority(result) &&
                    result.NextActions.IsEmpty &&
                    result.Effects.Select(Effect).SequenceEqual(
                        [
                            "readWorkspace|completed|workspace",
                            "writeNewArtifact|completed|k-local-output",
                            "writeNewArtifact|failed|k-local-output"
                        ],
                        StringComparer.Ordinal) &&
                    File.Exists(receipt.Value) &&
                    !File.Exists(workflow.Value),
                "Workflow post-promotion readback failure did not roll back only the workflow output while preserving durable receipt truth.");
            RequireWriteLocked(
                receipt.Value,
                "workflow rollback committed receipt");
            committedArtifact = result.Artifacts.Single();
        }
        finally
        {
            result.TerminalArtifactLease?.Dispose();
        }
        RequireWriteReleased(
            receipt.Value,
            "workflow rollback committed receipt");
        byte[] committed = File.ReadAllBytes(receipt.Value);
        Require(
            committedArtifact.Size == committed.LongLength &&
            committedArtifact.Sha256 == Hash(committed) &&
            File.ReadAllBytes(receipt.Value).SequenceEqual(committed),
            "Workflow rollback changed or misidentified the already committed review receipt.");
    }

    private static async Task AssertPrepublicationReceiptFailureProjectionCaseAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        ReviewFixture fixture,
        string name,
        IFaceGeomHairRegionsPinnedFileSystemHooks hooks,
        string expectedCode,
        DiagnosticClass expectedClass,
        RecoveryAction expectedAction,
        string expectedConstraint,
        params string[] expectedEffects)
    {
        var receiptService = new AgentReviewReceiptService(
            fixture.Policy,
            root);
        SetPinnedFileSystem(
            receiptService,
            new FaceGeomHairRegionsPinnedFileSystem(root, hooks));
        (WorkspacePath receipt, WorkspacePath workflow) = Outputs(root, name);
        var predecessorProbe = new DisposalProbe();
        ProtocolCommandResult result = await Adapter(
                root,
                lifecycle,
                receiptService,
                new FakeAdmission(
                    fixture.Transition,
                    retained: [predecessorProbe]))
            .RunAsync(
                fixture.Command(
                    receipt,
                    workflow,
                    "accepted",
                    "true"),
                GuiDigest,
                CancellationToken.None);
        try
        {
            Require(result.Diagnostics is [var diagnostic] &&
                    diagnostic.Code == expectedCode &&
                    diagnostic.Class == expectedClass &&
                    diagnostic.Recovery is
                    {
                        Option: "receipt-output",
                        ArtifactKind: WorkflowArtifactKinds.ReviewReceipt,
                        RetryUnchangedSafe: false
                    } recovery &&
                    recovery.Action == expectedAction &&
                    recovery.Constraint == expectedConstraint &&
                    result.Artifacts.IsEmpty &&
                    result.NextActions.IsEmpty &&
                    result.ResultSchemaId ==
                        AgentProtocolSchemaIds.ReviewReceiptResult &&
                    HasResult(
                        result,
                        created: false,
                        status: "refused",
                        outcome: null,
                        hasReceipt: false,
                        hasWorkflow: false) &&
                    HasAdmittedAuthority(result) &&
                    result.Effects.Select(Effect).SequenceEqual(
                        expectedEffects,
                        StringComparer.Ordinal) &&
                    result.TerminalArtifactLease is not null &&
                    !predecessorProbe.Disposed &&
                    !File.Exists(receipt.Value) &&
                    !File.Exists(workflow.Value),
                $"{name} did not preserve the exact prepublication refusal contract.");
        }
        finally
        {
            result.TerminalArtifactLease?.Dispose();
        }
        Require(predecessorProbe.Disposed,
            $"{name} did not deterministically release the predecessor terminal lease.");
        Require(!File.Exists(receipt.Value),
            $"{name} retained or published a receipt child artifact.");
    }

    private static async Task AssertProductionAdmissionRefusalAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture fixture,
        string name,
        Func<ParsedCommand, ParsedCommand> mutate)
    {
        (WorkspacePath receipt, WorkspacePath workflow) = Outputs(
            root,
            "production-" + name);
        ParsedCommand command = mutate(fixture.Command(
            receipt,
            workflow,
            "accepted",
            "true"));
        ProtocolCommandResult result = await new ProtocolV2ReviewReceiptAdapter(
                root,
                receiptService,
                lifecycle)
            .RunAsync(command, GuiDigest, CancellationToken.None);
        try
        {
            Require(result.Diagnostics is [var diagnostic] &&
                    diagnostic.Code ==
                        ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed &&
                    diagnostic.Class == DiagnosticClass.Validation &&
                    diagnostic.Recovery is
                    {
                        Action: RecoveryAction.CorrectInput,
                        RetryUnchangedSafe: false
                    } &&
                    result.Artifacts.IsEmpty &&
                    result.NextActions.IsEmpty &&
                    result.Effects.Select(Effect).SequenceEqual(
                        [
                            "readWorkspace|completed|workspace",
                            "writeNewArtifact|refused|k-local-output"
                        ],
                        StringComparer.Ordinal) &&
                    !File.Exists(receipt.Value) &&
                    !File.Exists(workflow.Value),
                $"Production review admission case '{name}' did not fail closed.");
        }
        finally
        {
            result.TerminalArtifactLease?.Dispose();
        }
    }

    private static async Task AssertAcceptedSuccessorsAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture package,
        ReviewFixture finish)
    {
        ProtocolCommandResult packageResult = await RunAsync(
            root,
            lifecycle,
            receiptService,
            package,
            "accepted-package",
            "accepted",
            attestation: "true");
        AcceptedReviewEvidence packageEvidence;
        try
        {
            packageEvidence = AssertAcceptedWhileRetained(
                packageResult,
                package,
                [
                    WorkflowArtifactKinds.NpcPackageManifest,
                    WorkflowArtifactKinds.NpcPreviewManifest,
                    WorkflowArtifactKinds.ReviewReceipt
                ],
                "npc finish analyze");
        }
        finally
        {
            packageResult.TerminalArtifactLease?.Dispose();
        }
        AssertAcceptedReleased(packageEvidence, package);

        ProtocolCommandResult finishResult = await RunAsync(
            root,
            lifecycle,
            receiptService,
            finish,
            "accepted-finish",
            "accepted",
            attestation: "true");
        AcceptedReviewEvidence finishEvidence;
        try
        {
            finishEvidence = AssertAcceptedWhileRetained(
                finishResult,
                finish,
                [
                    WorkflowArtifactKinds.NpcFinishCoreRequest,
                    WorkflowArtifactKinds.NpcFinishCoreProposal,
                    WorkflowArtifactKinds.NpcPreviewManifest,
                    WorkflowArtifactKinds.ReviewReceipt
                ],
                "npc finish apply");
        }
        finally
        {
            finishResult.TerminalArtifactLease?.Dispose();
        }
        AssertAcceptedReleased(finishEvidence, finish);
    }

    private static AcceptedReviewEvidence AssertAcceptedWhileRetained(
        ProtocolCommandResult result,
        ReviewFixture fixture,
        string[] expectedKinds,
        string expectedCommand)
    {
        Require(result.Diagnostics.IsEmpty &&
                result.Artifacts.Length == 2 &&
                result.Effects.Select(Effect).SequenceEqual(
                    [
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|completed|k-local-output",
                        "writeNewArtifact|completed|k-local-output"
                    ],
                    StringComparer.Ordinal) &&
                result.ResultSchemaId ==
                    AgentProtocolSchemaIds.ReviewReceiptResult,
            "Accepted review did not report its two exact commit stages.");
        ProtocolArtifact receipt = result.Artifacts.Single(item =>
            item.Kind == WorkflowArtifactKinds.ReviewReceipt);
        ProtocolArtifact workflow = result.Artifacts.Single(item =>
            item.Kind == "workflow-bundle");
        Require(AgentReviewContract.TryGetPurpose(
                    fixture.Proposal.Kind,
                    out _,
                    out string authorityNoticeSha256),
            "Accepted review proposal has no canonical purpose.");
        string[] expectedInputs =
        [
            fixture.Transition.Document.Sha256,
            fixture.Proposal.Sha256,
            fixture.Preview.Sha256,
            authorityNoticeSha256
        ];
        Array.Sort(expectedInputs, StringComparer.Ordinal);
        Require(receipt.RequestDigest == GuiDigest &&
                receipt.RequestDigest != PredecessorDigest &&
                receipt.InputBindings.SequenceEqual(
                    expectedInputs.Distinct(StringComparer.Ordinal),
                    StringComparer.Ordinal) &&
                HasAdmittedAuthority(result) &&
                HasResult(
                    result,
                    created: true,
                    status: "review-receipt-persisted",
                    outcome: "accepted",
                    hasReceipt: true,
                    hasWorkflow: true),
            "Accepted review lost the current GUI digest or overclaimed authority.");
        AgentWorkflowBundleDocument successor = new AgentWorkflowBundleCodec(
                fixture.Policy,
                fixture.Root)
            .Load(new WorkspacePath(workflow.Path), workflow.Sha256!);
        Require(successor.Bundle.Artifacts.Select(item => item.Kind)
                    .SequenceEqual(expectedKinds, StringComparer.Ordinal) &&
                result.NextActions is [var next] &&
                next.Command == expectedCommand,
            "Accepted review produced the wrong closed successor signature.");

        RequireWriteLocked(receipt.Path, "receipt");
        RequireWriteLocked(workflow.Path, "workflow");
        return new AcceptedReviewEvidence(receipt, workflow, successor);
    }

    private static void AssertAcceptedReleased(
        AcceptedReviewEvidence evidence,
        ReviewFixture fixture)
    {
        RequireWriteReleased(evidence.Receipt.Path, "receipt");
        RequireWriteReleased(evidence.Workflow.Path, "workflow");
        AgentReviewReceiptDocument reopened = fixture.ReceiptService.LoadForSuccessor(
            evidence.Successor,
            new WorkspacePath(evidence.Receipt.Path),
            evidence.Receipt.Sha256!);
        Require(reopened.Receipt.DisplayedArtifactHashes.SequenceEqual(
                    new[]
                    {
                        fixture.Proposal.Sha256,
                        fixture.Preview.Sha256
                    }.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal),
            "Released receipt did not reopen with exactly proposal plus preview displayed.");
    }

    private static async Task AssertNonAuthorizingOutcomesAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture fixture)
    {
        foreach (string outcome in new[] { "rejected", "revision-requested" })
        {
            ProtocolCommandResult result = await RunAsync(
                root,
                lifecycle,
                receiptService,
                fixture,
                outcome,
                outcome,
                attestation: null);
            ProtocolArtifact? receiptArtifact = null;
            try
            {
                Require(result.Diagnostics.IsEmpty &&
                        result.Artifacts is [var receipt] &&
                        (receiptArtifact = receipt).Kind ==
                            WorkflowArtifactKinds.ReviewReceipt &&
                        File.Exists(receipt.Path) &&
                        result.NextActions.IsEmpty &&
                        HasAdmittedAuthority(result) &&
                        HasResult(
                            result,
                            created: true,
                            status: "review-receipt-persisted",
                            outcome: outcome == "rejected"
                                ? "rejected"
                                : "revisionRequested",
                            hasReceipt: true,
                            hasWorkflow: false) &&
                        result.Effects.Select(Effect).SequenceEqual(
                            [
                                "readWorkspace|completed|workspace",
                                "writeNewArtifact|completed|k-local-output"
                            ],
                            StringComparer.Ordinal),
                    $"{outcome} review did not persist only non-authorizing receipt evidence.");
                RequireWriteLocked(receiptArtifact!.Path,
                    $"{outcome} receipt");
            }
            finally
            {
                result.TerminalArtifactLease?.Dispose();
            }
            Require(receiptArtifact is not null,
                $"{outcome} result omitted its receipt artifact.");
            RequireWriteReleased(receiptArtifact!.Path,
                $"{outcome} receipt");
            AgentReviewReceiptDocument reopened = fixture.ReceiptService.Load(
                fixture.Transition.Document,
                fixture.Proposal.Sha256,
                [fixture.Proposal, fixture.Preview],
                new WorkspacePath(receiptArtifact.Path),
                receiptArtifact.Sha256!);
            ReviewOutcome expected = outcome == "rejected"
                ? ReviewOutcome.Rejected
                : ReviewOutcome.RevisionRequested;
            Require(reopened.Receipt.Outcome == expected &&
                    reopened.Receipt.DisplayedArtifactHashes.SequenceEqual(
                        new[]
                        {
                            fixture.Proposal.Sha256,
                            fixture.Preview.Sha256
                        }.Order(StringComparer.Ordinal),
                        StringComparer.Ordinal),
                $"{outcome} durable receipt changed after terminal release.");
        }
    }

    private static async Task AssertExactSyntaxRefusalsAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture fixture)
    {
        var cases = new (string Name, string Outcome, string? Attestation,
            bool Valueless, string ExpectedCode, string ExpectedOption)[]
        {
            ("missing-attestation", "accepted", null, false,
                ProtocolV2DiagnosticCodes.OptionFlagValue,
                "operator-attestation"),
            ("valueless-attestation", "accepted", "true", true,
                ProtocolV2DiagnosticCodes.OptionFlagValue,
                "operator-attestation"),
            ("attestation-case", "accepted", "True", false,
                ProtocolV2DiagnosticCodes.OptionFlagValue,
                "operator-attestation"),
            ("outcome-case", "Accepted", "true", false,
                ProtocolV2DiagnosticCodes.OptionEnumValue,
                "outcome"),
            ("rejected-attestation", "rejected", "true", false,
                ProtocolV2DiagnosticCodes.OptionConflict,
                "operator-attestation")
        };
        foreach ((string name, string outcome, string? attestation,
                     bool valueless, string expectedCode,
                     string expectedOption) in cases)
        {
            var fake = new FakeAdmission(fixture.Transition);
            (WorkspacePath receipt, WorkspacePath workflow) = Outputs(
                root,
                "syntax-" + name);
            ParsedCommand command = fixture.Command(
                receipt,
                workflow,
                outcome,
                attestation,
                valueless);
            ProtocolCommandResult result = await Adapter(
                    root,
                    lifecycle,
                    receiptService,
                    fake)
                .RunAsync(command, GuiDigest, CancellationToken.None);
            Require(result.Diagnostics is [var diagnostic] &&
                    diagnostic.Code == expectedCode &&
                    diagnostic.Class == DiagnosticClass.Usage &&
                    diagnostic.Recovery is
                    {
                        Action: RecoveryAction.CorrectInput,
                        RetryUnchangedSafe: false
                    } recovery &&
                    recovery.Option == expectedOption &&
                    recovery.ArtifactKind is null &&
                    fake.Calls == 0 &&
                    result.Artifacts.IsEmpty &&
                    result.NextActions.IsEmpty &&
                    HasBindingAuthority(result) &&
                    HasResult(
                        result,
                        created: false,
                        status: "refused",
                        outcome: null,
                        hasReceipt: false,
                        hasWorkflow: false) &&
                    result.Effects.Select(Effect).SequenceEqual(
                        [
                            "readWorkspace|completed|workspace",
                            "writeNewArtifact|refused|k-local-output"
                        ],
                        StringComparer.Ordinal) &&
                    !File.Exists(receipt.Value) &&
                    !File.Exists(workflow.Value),
                $"Exact syntax case '{name}' reached admission or wrote output.");
        }
    }

    private static async Task AssertBindingAndCollisionRefusalsAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture fixture)
    {
        var fake = new FakeAdmission(fixture.Transition);
        (WorkspacePath staleReceipt, WorkspacePath staleWorkflow) = Outputs(
            root,
            "stale-proposal");
        ParsedCommand original = fixture.Command(
            staleReceipt,
            staleWorkflow,
            "accepted",
            "true");
        ParsedCommand stale = original with
        {
            Options = original.Options.SetItem(
                "proposal-sha256",
                new string('C', 64))
        };
        ProtocolCommandResult staleResult = await Adapter(
                root,
                lifecycle,
                receiptService,
                fake)
            .RunAsync(stale, GuiDigest, CancellationToken.None);
        Require(staleResult.Diagnostics is [var staleDiagnostic] &&
                staleDiagnostic.Code ==
                    ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed &&
                staleDiagnostic.Class == DiagnosticClass.Validation &&
                staleDiagnostic.Recovery is
                {
                    Action: RecoveryAction.CorrectInput,
                    Option: "workflow-bundle",
                    ArtifactKind: "workflow-bundle",
                    RetryUnchangedSafe: false
                } &&
                fake.Calls == 1 &&
                HasBindingAuthority(staleResult) &&
                staleResult.Artifacts.IsEmpty &&
                staleResult.NextActions.IsEmpty &&
                staleResult.Effects.Select(Effect).SequenceEqual(
                    [
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|refused|k-local-output"
                    ],
                    StringComparer.Ordinal) &&
                !File.Exists(staleReceipt.Value),
            "Stale proposal binding did not fail closed before receipt publication.");

        (WorkspacePath driftReceipt, WorkspacePath driftWorkflow) = Outputs(
            root,
            "before-mutation");
        var drift = new FakeAdmission(
            fixture.Transition,
            revalidate: call =>
            {
                if (call == 1)
                    throw new InvalidDataException(
                        "The preview changed before receipt publication.");
            });
        ProtocolCommandResult driftResult = await Adapter(
                root,
                lifecycle,
                receiptService,
                drift)
            .RunAsync(
                fixture.Command(
                    driftReceipt,
                    driftWorkflow,
                    "accepted",
                    "true"),
                GuiDigest,
                CancellationToken.None);
        try
        {
            Require(driftResult.Diagnostics is [var driftDiagnostic] &&
                driftDiagnostic.Code ==
                    ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed &&
                driftDiagnostic.Class == DiagnosticClass.Validation &&
                driftDiagnostic.Recovery is
                {
                    Action: RecoveryAction.CorrectInput,
                    Option: "workflow-bundle",
                    ArtifactKind: "workflow-bundle",
                    RetryUnchangedSafe: false
                } &&
                HasAdmittedAuthority(driftResult) &&
                driftResult.Artifacts.IsEmpty &&
                driftResult.NextActions.IsEmpty &&
                driftResult.Effects.Select(Effect).SequenceEqual(
                    [
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|refused|k-local-output"
                    ],
                    StringComparer.Ordinal) &&
                !File.Exists(driftReceipt.Value),
                "Before-publication mutation produced a receipt.");
        }
        finally
        {
            driftResult.TerminalArtifactLease?.Dispose();
        }

        (WorkspacePath callbackReceipt, WorkspacePath callbackWorkflow) =
            Outputs(root, "prepublication-callback-drift");
        var callbackDrift = new FakeAdmission(
            fixture.Transition,
            revalidate: call =>
            {
                if (call == 2)
                    throw new InvalidDataException(
                        "The package changed during receipt staging.");
            });
        ProtocolCommandResult callbackResult = await Adapter(
                root,
                lifecycle,
                receiptService,
                callbackDrift)
            .RunAsync(
                fixture.Command(
                    callbackReceipt,
                    callbackWorkflow,
                    "accepted",
                    "true"),
                GuiDigest,
                CancellationToken.None);
        try
        {
            Require(callbackResult.Diagnostics is [var callbackDiagnostic] &&
                callbackDiagnostic.Code ==
                    ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed &&
                callbackDiagnostic.Class == DiagnosticClass.Validation &&
                callbackDiagnostic.Recovery is
                {
                    Action: RecoveryAction.CorrectInput,
                    Option: "workflow-bundle",
                    ArtifactKind: "workflow-bundle",
                    RetryUnchangedSafe: false
                } &&
                HasAdmittedAuthority(callbackResult) &&
                callbackResult.Artifacts.IsEmpty &&
                callbackResult.NextActions.IsEmpty &&
                callbackResult.Effects.Select(Effect).SequenceEqual(
                    [
                        "readWorkspace|completed|workspace",
                        "writeNewArtifact|refused|k-local-output"
                    ],
                    StringComparer.Ordinal) &&
                !File.Exists(callbackReceipt.Value) &&
                !File.Exists(callbackWorkflow.Value),
                "Staging callback drift was not normalized as prepublication validation refusal.");
        }
        finally
        {
            callbackResult.TerminalArtifactLease?.Dispose();
        }

        (WorkspacePath afterReceipt, WorkspacePath afterWorkflow) = Outputs(
            root,
            "after-publication-mutation");
        var afterMutation = new FakeAdmission(
            fixture.Transition,
            revalidate: call =>
            {
                if (call == 3)
                    throw new InvalidDataException(
                        "The preview changed after receipt publication.");
            });
        ProtocolCommandResult afterResult = await Adapter(
                root,
                lifecycle,
                receiptService,
                afterMutation)
            .RunAsync(
                fixture.Command(
                    afterReceipt,
                    afterWorkflow,
                    "accepted",
                    "true"),
                GuiDigest,
                CancellationToken.None);
        try
        {
            Require(afterResult.Diagnostics is [var afterDiagnostic] &&
                    afterDiagnostic.Code ==
                        ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed &&
                    afterDiagnostic.Class == DiagnosticClass.Validation &&
                    afterDiagnostic.Recovery is
                    {
                        Action: RecoveryAction.CorrectInput,
                        Option: "workflow-bundle",
                        ArtifactKind: "workflow-bundle",
                        RetryUnchangedSafe: false
                    } &&
                    afterResult.Artifacts is [var retainedReceipt] &&
                    retainedReceipt.Kind ==
                        WorkflowArtifactKinds.ReviewReceipt &&
                    File.Exists(afterReceipt.Value) &&
                    !File.Exists(afterWorkflow.Value) &&
                    HasAdmittedAuthority(afterResult) &&
                    HasResult(
                        afterResult,
                        created: true,
                        status: "review-receipt-persisted",
                        outcome: "accepted",
                        hasReceipt: true,
                        hasWorkflow: false) &&
                    afterResult.Effects.Select(Effect).SequenceEqual(
                        [
                            "readWorkspace|completed|workspace",
                            "writeNewArtifact|completed|k-local-output",
                            "writeNewArtifact|refused|k-local-output"
                        ],
                        StringComparer.Ordinal),
                "After-publication mutation did not preserve and advertise only the durable receipt.");
        }
        finally
        {
            afterResult.TerminalArtifactLease?.Dispose();
        }

        (WorkspacePath receipt, WorkspacePath workflow) = Outputs(
            root,
            "workflow-collision");
        byte[] sentinel = [9, 8, 7, 6];
        var collision = new FakeAdmission(
            fixture.Transition,
            revalidate: call =>
            {
                if (call == 3)
                    File.WriteAllBytes(workflow.Value, sentinel);
            });
        ProtocolCommandResult collisionResult = await Adapter(
                root,
                lifecycle,
                receiptService,
                collision)
            .RunAsync(
                fixture.Command(
                    receipt,
                    workflow,
                    "accepted",
                    "true"),
                GuiDigest,
                CancellationToken.None);
        try
        {
            Require(collisionResult.Diagnostics is [var diagnostic] &&
                    diagnostic.Code ==
                        ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused &&
                    diagnostic.Class == DiagnosticClass.Security &&
                    diagnostic.Recovery is
                    {
                        Action: RecoveryAction.ChooseFreshOutput,
                        Option: null,
                        ArtifactKind: "workflow-bundle",
                        RetryUnchangedSafe: false
                    } recovery &&
                    recovery.Constraint.Contains(
                        "--receipt-output",
                        StringComparison.Ordinal) &&
                    recovery.Constraint.Contains(
                        "--workflow-output",
                        StringComparison.Ordinal) &&
                    collisionResult.Artifacts is [var receiptArtifact] &&
                    receiptArtifact.Kind ==
                        WorkflowArtifactKinds.ReviewReceipt &&
                    File.Exists(receipt.Value) &&
                    File.ReadAllBytes(workflow.Value).SequenceEqual(sentinel) &&
                    HasAdmittedAuthority(collisionResult) &&
                    HasResult(
                        collisionResult,
                        created: true,
                        status: "review-receipt-persisted",
                        outcome: "accepted",
                        hasReceipt: true,
                        hasWorkflow: false) &&
                    collisionResult.Effects.Select(Effect).SequenceEqual(
                        [
                            "readWorkspace|completed|workspace",
                            "writeNewArtifact|completed|k-local-output",
                            "writeNewArtifact|failed|k-local-output"
                        ],
                        StringComparer.Ordinal),
                "Post-receipt workflow collision lost receipt/sentinel bytes or misstated stage effects.");
        }
        finally
        {
            collisionResult.TerminalArtifactLease?.Dispose();
        }
    }

    private static async Task AssertCancellationBoundaryAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture fixture)
    {
        using var beforeCancellation = new CancellationTokenSource();
        var before = new FakeAdmission(
            fixture.Transition,
            revalidate: call =>
            {
                if (call == 2)
                    beforeCancellation.Cancel();
            });
        (WorkspacePath beforeReceipt, WorkspacePath beforeWorkflow) = Outputs(
            root,
            "cancel-before-publish");
        ProtocolCommandResult beforeResult = await Adapter(
                root,
                lifecycle,
                receiptService,
                before)
            .RunAsync(
                fixture.Command(
                    beforeReceipt,
                    beforeWorkflow,
                    "accepted",
                    "true"),
                GuiDigest,
                beforeCancellation.Token);
        try
        {
            Require(beforeResult.Diagnostics is [var beforeDiagnostic] &&
                beforeDiagnostic.Code ==
                    ProtocolV2DiagnosticCodes.ProtocolOperationCancelled &&
                beforeDiagnostic.Class == DiagnosticClass.Cancellation &&
                before.Calls == 1 &&
                !File.Exists(beforeReceipt.Value) &&
                !File.Exists(beforeWorkflow.Value),
                "Cancellation before receipt promotion committed durable output.");
        }
        finally
        {
            beforeResult.TerminalArtifactLease?.Dispose();
        }

        using var afterCancellation = new CancellationTokenSource();
        var after = new FakeAdmission(
            fixture.Transition,
            revalidate: call =>
            {
                if (call == 3)
                    afterCancellation.Cancel();
            });
        (WorkspacePath afterReceipt, WorkspacePath afterWorkflow) = Outputs(
            root,
            "cancel-after-publish");
        ProtocolCommandResult afterResult = await Adapter(
                root,
                lifecycle,
                receiptService,
                after)
            .RunAsync(
                fixture.Command(
                    afterReceipt,
                    afterWorkflow,
                    "accepted",
                    "true"),
                GuiDigest,
                afterCancellation.Token);
        try
        {
            _ = AssertAcceptedWhileRetained(
                afterResult,
                fixture,
                [
                    WorkflowArtifactKinds.NpcPackageManifest,
                    WorkflowArtifactKinds.NpcPreviewManifest,
                    WorkflowArtifactKinds.ReviewReceipt
                ],
                "npc finish analyze");
        }
        finally
        {
            afterResult.TerminalArtifactLease?.Dispose();
        }
        Require(afterCancellation.IsCancellationRequested &&
                File.Exists(afterReceipt.Value) &&
                File.Exists(afterWorkflow.Value),
            "Cancellation observed after durable receipt publication interrupted workflow advancement.");
    }

    private static SkyrimNpcFinishCoreAiPolicy CurrentV2FinishAiPolicy() =>
        new()
        {
            Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
            Confidence = SkyrimNpcFinishCoreConfidence.Brave,
            Energy = 50,
            Morality = SkyrimNpcFinishCoreMorality.NoCrime,
            Assistance = SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies,
            Mood = SkyrimNpcFinishCoreMood.Neutral
        };

    private static void AssertFinishProposalStore(WorkspacePath root)
    {
        var request = new SkyrimNpcFinishCoreRequest
        {
            AiPolicy = CurrentV2FinishAiPolicy(),
            Actor = new SkyrimNpcFinishCoreActor
            {
                EditorId = new EditorId("ReviewNpc"),
                FormId = new FormId(0x800)
            },
            SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
            {
                Template = new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x1B217)),
                TemplateEditorId = "DefaultSandboxEditorLocation512"
            },
            Output = new SkyrimNpcFinishCoreOutput
            {
                PluginFileName = "ReviewNpc.esp"
            }
        };
        byte[] requestBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
            request,
            root);
        WorkspacePath requestPath = Child(root, "strict", "request.json");
        File.WriteAllBytes(requestPath.Value, requestBytes);
        string requestSha = Hash(requestBytes);
        var proposal = new SkyrimNpcFinishCoreProposal
        {
            Request = request,
            RequestSha256 = new Sha256Hash(requestSha),
            Status = SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite
        };
        Sha256Hash semantic = SkyrimNpcFinishCoreDocumentCodec
            .HashProposalWithoutSelf(
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                    proposal,
                    root));
        byte[] proposalBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal with { ProposalSha256 = semantic },
            root);
        WorkspacePath proposalPath = Child(root, "strict", "proposal.json");
        File.WriteAllBytes(proposalPath.Value, proposalBytes);
        var store = new SkyrimNpcFinishCoreWorkflowDocumentStore(root);
        SkyrimNpcFinishCoreRequestDocument loadedRequest = store.LoadRequest(
            requestPath,
            requestSha);
        SkyrimNpcFinishCoreProposalDocument loadedProposal =
            store.LoadProposalForReview(proposalPath, Hash(proposalBytes));
        Require(loadedRequest.Sha256 == requestSha &&
                IsUpperSha256(requestSha) &&
                loadedProposal.SemanticSha256 == semantic.Value &&
                IsUpperSha256(
                    loadedProposal.SemanticSha256.ToUpperInvariant()),
            "Strict Finish request/proposal documents did not preserve physical and semantic identity.");
        RequireThrows<InvalidDataException>(
            () => store.LoadRequest(requestPath, new string('D', 64)),
            "Strict Finish request store accepted physical hash drift.");

        var requestReadMutation = new MutatePinnedReadbackHooks();
        SetPinnedFileSystem(
            store,
            new FaceGeomHairRegionsPinnedFileSystem(
                root,
                requestReadMutation));
        RequireThrows<InvalidDataException>(
            () => store.LoadRequest(requestPath, requestSha),
            "Finish request store accepted same-length bytes changed after physical admission.");
        Require(requestReadMutation.Mutated,
            "Finish request read-mutation hook did not mutate the admitted bytes.");

        byte[] staleSemanticBytes = SkyrimNpcFinishCoreDocumentCodec
            .SerializeProposal(
                proposal with
                {
                    ProposalSha256 = new Sha256Hash(new string('D', 64))
                },
                root);
        WorkspacePath staleSemanticPath = Child(
            root,
            "strict",
            "stale-semantic-proposal.json");
        File.WriteAllBytes(staleSemanticPath.Value, staleSemanticBytes);
        RequireThrows<InvalidDataException>(
            () => store.LoadProposalForReview(
                staleSemanticPath,
                Hash(staleSemanticBytes)),
            "Strict Finish proposal store accepted stale semantic identity.");

        var proposalReadMutation = new MutatePinnedReadbackHooks();
        SetPinnedFileSystem(
            store,
            new FaceGeomHairRegionsPinnedFileSystem(
                root,
                proposalReadMutation));
        RequireThrows<InvalidDataException>(
            () => store.LoadProposalForReview(proposalPath, Hash(proposalBytes)),
            "Finish proposal store accepted same-length bytes changed after physical admission.");
        Require(proposalReadMutation.Mutated,
            "Finish proposal read-mutation hook did not mutate the admitted bytes.");

        SkyrimNpcFinishCoreProposal noChangesProposal = proposal with
        {
            ProposalSha256 = null,
            Status = SkyrimNpcFinishCoreStatus.NoChanges
        };
        Sha256Hash noChangesSemantic = SkyrimNpcFinishCoreDocumentCodec
            .HashProposalWithoutSelf(
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                    noChangesProposal,
                    root));
        byte[] noChangesBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            noChangesProposal with { ProposalSha256 = noChangesSemantic },
            root);
        WorkspacePath noChangesPath = Child(
            root,
            "strict",
            "no-changes.json");
        File.WriteAllBytes(noChangesPath.Value, noChangesBytes);
        RequireThrows<InvalidDataException>(
            () => store.LoadProposalForReview(
                noChangesPath,
                Hash(noChangesBytes)),
            "NoChanges Finish proposal entered GUI review admission.");
    }

    private static async Task AssertTypedReviewPathRefusalsAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture fixture)
    {
        var cases = new (string Name,
            Func<WorkspacePath, WorkspacePath, ParsedCommand> Command,
            string ExpectedCode,
            string ExpectedOption)[]
        {
            (
                "output-overlap",
                (receipt, workflow) => fixture.Command(
                    receipt,
                    receipt,
                    "accepted",
                    "true"),
                ProtocolV2DiagnosticCodes.ReviewReceiptPathRefused,
                "workflow-output"),
            (
                "receipt-protected-root",
                (_, workflow) => fixture.Command(
                    root,
                    workflow,
                    "accepted",
                    "true"),
                ProtocolV2DiagnosticCodes.ReviewReceiptPathRefused,
                "receipt-output"),
            (
                "workflow-protected-root",
                (receipt, _) => fixture.Command(
                    receipt,
                    root,
                    "accepted",
                    "true"),
                ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused,
                "workflow-output")
        };
        foreach ((string name,
                     Func<WorkspacePath, WorkspacePath, ParsedCommand> command,
                     string expectedCode,
                     string expectedOption) in cases)
        {
            var fake = new FakeAdmission(fixture.Transition);
            (WorkspacePath receipt, WorkspacePath workflow) = Outputs(
                root,
                "path-" + name);
            ProtocolCommandResult result = await Adapter(
                    root,
                    lifecycle,
                    receiptService,
                    fake)
                .RunAsync(
                    command(receipt, workflow),
                    GuiDigest,
                    CancellationToken.None);
            Require(result.Diagnostics is [var diagnostic] &&
                    diagnostic.Code == expectedCode &&
                    diagnostic.Class == DiagnosticClass.Security &&
                    diagnostic.Recovery is
                    {
                        Action: RecoveryAction.ChooseFreshOutput,
                        RetryUnchangedSafe: false
                    } recovery &&
                    recovery.Option == expectedOption &&
                    fake.Calls == 0 &&
                    result.Artifacts.IsEmpty &&
                    result.NextActions.IsEmpty &&
                    HasBindingAuthority(result) &&
                    result.Effects.Select(Effect).SequenceEqual(
                        [
                            "readWorkspace|completed|workspace",
                            "writeNewArtifact|refused|k-local-output"
                        ],
                        StringComparer.Ordinal),
                $"Typed review path case '{name}' did not refuse before admission.");
        }
    }

    private static async Task AssertRunnerJournalRedactionAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture fixture)
    {
        const string note = "SECRET-REVIEWER-NOTE-MUST-NOT-LEAK";
        (WorkspacePath receipt, WorkspacePath workflow) = Outputs(
            root,
            "runner-note-redaction");
        ParsedCommand command = fixture.Command(
            receipt,
            workflow,
            "rejected",
            attestation: null) with
        {
            Options = fixture.Command(
                    receipt,
                    workflow,
                    "rejected",
                    attestation: null)
                .Options.SetItem("reviewer-note", note)
        };
        var fake = new FakeAdmission(fixture.Transition);
        var journal = new ProbingJournal(
            () => RequireWriteLocked(receipt.Value,
                "runner receipt during journal append"));
        using var output = new ProbingWriter(
            () => RequireWriteLocked(receipt.Value,
                "runner receipt during envelope write"));
        ImmutableArray<AgentCommandContract> contracts =
            SyntheticReviewContracts();
        string? previousRoot = Environment.GetEnvironmentVariable(
            ActorwrightWorkspace.WorkspaceRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                root.Value);
            var runner = new ProtocolV2Runner(
                output,
                _ => journal,
                [Adapter(root, lifecycle, receiptService, fake)],
                contracts);
            CommandExitCode exit = await runner.RunAsync(
                command,
                CancellationToken.None);
            Require(exit == CommandExitCode.Success &&
                    journal.Records is [var record] &&
                    record.Command == "gui" &&
                    record.Outcome == "succeeded" &&
                    record.ArtifactHashes.Length == 1 &&
                    output.Probes == 1 &&
                    journal.Probes == 1 &&
                    !JsonSerializer.Serialize(record).Contains(
                        note,
                        StringComparison.Ordinal) &&
                    !output.Content.Contains(note, StringComparison.Ordinal),
                "Review runner leaked note text or missed terminal journal/writer probes.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ActorwrightWorkspace.WorkspaceRootEnvironmentVariable,
                previousRoot);
        }
        RequireWriteReleased(receipt.Value, "runner receipt");
    }

    private static ImmutableArray<AgentCommandContract>
        SyntheticReviewContracts()
    {
        AgentCommandContract legacy = AgentCommandRegistry.GetRequired("gui");
        ImmutableArray<AgentOptionContract> options = ReviewOptionNames
            .Select(name => new AgentOptionContract(
            name,
            JsonNamingPolicy.CamelCase.ConvertName(name.Replace("-", "")),
            name.EndsWith("sha256", StringComparison.Ordinal)
                ? AgentValueKind.Sha256
                : name is "workflow-bundle" or "proposal" or
                    "preview-manifest" or "receipt-output" or
                    "workflow-output"
                    ? AgentValueKind.Path
                    : name == "outcome"
                        ? AgentValueKind.Enum
                        : name == "operator-attestation"
                            ? AgentValueKind.Boolean
                            : AgentValueKind.String,
            RequiredReviewOptionNames.Contains(name, StringComparer.Ordinal),
            name == "outcome"
                ? ["accepted", "rejected", "revision-requested"]
                : [],
            [],
            false)
            {
                ValueSyntax = $"<{name}>",
                Description = $"Synthetic protocol-v2 review option --{name}."
            }).ToImmutableArray();
        AgentCommandContract review = legacy with
        {
            Readiness = ProtocolReadiness.V2,
            Options = options,
            InputArtifactKinds = [],
            ResultSchemaIds = [AgentProtocolSchemaIds.ReviewReceiptResult],
            Effects =
            [
                new AgentEffectContract(
                    AgentEffectKind.ReadWorkspace,
                    "When review evidence is admitted.",
                    "workspace")
                {
                    AllowedResultScopes =
                        [ApplicationEffectScope.Workspace]
                },
                new AgentEffectContract(
                    AgentEffectKind.WriteNewArtifact,
                    "When receipt/workflow output is attempted.",
                    "k-local-output")
                {
                    AllowedResultScopes =
                        [ApplicationEffectScope.KLocalOutput]
                }
            ],
            Authority =
            [
                .. Enum.GetValues<AgentAuthorityKind>().Select(kind =>
                    new AgentAuthorityContract(
                        kind,
                        kind is AgentAuthorityKind.HumanVisualAcceptance or
                            AgentAuthorityKind.GameRuntimeVerification or
                            AgentAuthorityKind.PromotionApproval
                            ? AgentAuthorityState.Required
                            : AgentAuthorityState.Established,
                        "Synthetic unadvertised review contract."))
            ],
            OptionRelationships = [],
            InputArtifacts = [],
            OutputArtifacts = [],
            Transitions = []
        };
        return AgentCommandRegistry.All.Select(contract =>
                contract.Name == "gui" ? review : contract)
            .ToImmutableArray();
    }

    private static async Task<ProtocolCommandResult> RunAsync(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        ReviewFixture fixture,
        string name,
        string outcome,
        string? attestation)
    {
        (WorkspacePath receipt, WorkspacePath workflow) = Outputs(root, name);
        var fake = new FakeAdmission(fixture.Transition);
        return await Adapter(root, lifecycle, receiptService, fake).RunAsync(
            fixture.Command(receipt, workflow, outcome, attestation),
            GuiDigest,
            CancellationToken.None);
    }

    private static ProtocolV2ReviewReceiptAdapter Adapter(
        WorkspacePath root,
        AgentWorkflowBundleTransitionService lifecycle,
        AgentReviewReceiptService receiptService,
        IProtocolV2ReviewPredecessorAdmission admission) => new(
        root,
        receiptService,
        lifecycle,
        admission);

    private static (WorkspacePath Receipt, WorkspacePath Workflow) Outputs(
        WorkspacePath root,
        string name)
    {
        string directory = Path.Combine(root.Value, "results", name);
        Directory.CreateDirectory(directory);
        return (
            new WorkspacePath(Path.Combine(directory, "receipt.json")),
            new WorkspacePath(Path.Combine(directory, "workflow.json")));
    }

    private static WorkspacePath Child(
        WorkspacePath root,
        params string[] parts)
    {
        string path = parts.Aggregate(root.Value, Path.Combine);
        string? parent = Path.GetDirectoryName(path);
        if (parent is not null)
            Directory.CreateDirectory(parent);
        return new WorkspacePath(path);
    }

    private static (WorkspacePath Path, byte[] Bytes) WriteCanonicalPreview(
        WorkspacePath root,
        string name,
        string sourcePlugin = "ReviewNpc.esp",
        uint sourceFormId = 0x800,
        string? assetPlugin = null,
        WorkspacePath? faceGeomSource = null,
        WorkspacePath? faceTintSource = null,
        string? assetProvider = null)
    {
        WorkspacePath previewRoot = Child(root, "inputs", name, "preview");
        Directory.CreateDirectory(previewRoot.Value);
        byte[] png = CreatePng(900, 900);
        WorkspacePath status = Child(previewRoot, "renderer-status.json");
        File.WriteAllText(status.Value, "{}", new UTF8Encoding(false));
        WorkspacePath contact = Child(previewRoot, "contact-sheet.png");
        File.WriteAllBytes(contact.Value, png);
        ImmutableArray<NpcVisualPreviewView>.Builder views =
            ImmutableArray.CreateBuilder<NpcVisualPreviewView>();
        foreach (string id in NpcVisualPreviewPersistenceContract.RequiredViewIds)
        {
            WorkspacePath image = Child(previewRoot, id + ".png");
            WorkspacePath mask = Child(previewRoot, id + "-roles.png");
            File.WriteAllBytes(image.Value, png);
            File.WriteAllBytes(mask.Value, png);
            views.Add(new NpcVisualPreviewView(
                id,
                image,
                new Sha256Hash(Hash(File.ReadAllBytes(image.Value))),
                mask,
                new Sha256Hash(Hash(File.ReadAllBytes(mask.Value))),
                900,
                900));
        }

        WorkspacePath faceGeom = faceGeomSource ??
            Child(previewRoot, "ReviewNpc-facegeom.nif");
        WorkspacePath faceTint = faceTintSource ??
            Child(previewRoot, "ReviewNpc-facetint.dds");
        if (faceGeomSource is null)
            File.WriteAllText(
                faceGeom.Value,
                "facegeom",
                new UTF8Encoding(false));
        if (faceTintSource is null)
            File.WriteAllText(
                faceTint.Value,
                "facetint",
                new UTF8Encoding(false));
        assetPlugin ??= sourcePlugin;
        string formId = sourceFormId.ToString("X8");
        var identity = new SkyrimMainWorkspaceIdentity(
            new PluginName(sourcePlugin),
            new PluginName(sourcePlugin),
            new FormId(sourceFormId),
            "NPC_");
        ImmutableArray<NpcVisualAsset> assets =
        [
            new NpcVisualAsset(
                NpcVisualAssetRole.FaceGeom,
                new AssetPath(
                    $"meshes/actors/character/FaceGenData/FaceGeom/{assetPlugin}/{formId}.nif"),
                assetProvider ?? assetPlugin,
                new Sha256Hash(Hash(File.ReadAllBytes(faceGeom.Value))),
                new FileInfo(faceGeom.Value).Length,
                faceGeom,
                true,
                []),
            new NpcVisualAsset(
                NpcVisualAssetRole.FaceTint,
                new AssetPath(
                    $"textures/actors/character/FaceGenData/FaceTint/{assetPlugin}/{formId}.dds"),
                assetProvider ?? assetPlugin,
                new Sha256Hash(Hash(File.ReadAllBytes(faceTint.Value))),
                new FileInfo(faceTint.Value).Length,
                faceTint,
                false,
                [])
        ];
        var source = new NpcVisualSourceGraph(
            NpcVisualPreviewRoute.Cotr,
            identity,
            NpcSex.Female,
            50,
            "NordRace",
            "#101010",
            "#F0D0C0",
            assets,
            [],
            false,
            []);
        var render = new NpcVisualPreviewRenderEvidence(
            "4.5.1",
            "BLENDER_EEVEE_NEXT",
            1,
            true,
            1,
            ["NPC Root [Root]"],
            0,
            ImmutableDictionary<string, int>.Empty.Add("Face", 1),
            ImmutableDictionary<string, long>.Empty.Add(
                "face-front:FaceGeom",
                4),
            ImmutableDictionary<string, double>.Empty,
            [
                new NpcVisualPreviewImportedMesh(
                    NpcVisualAssetRole.FaceGeom,
                    assets[0].AssetPath,
                    "FaceGeom",
                    3,
                    ["Face"],
                    [1, 0, 0, 0, 0, 1, 0, 0,
                     0, 0, 1, 0, 0, 0, 0, 1])
            ],
            [
                new NpcVisualPreviewImportedMaterial(
                    "FaceGeom",
                    "Face",
                    ImmutableDictionary<string, string>.Empty,
                    [],
                    "OPAQUE",
                    false)
            ],
            status,
            new Sha256Hash(Hash(File.ReadAllBytes(status.Value))));
        var payload = new NpcVisualPreviewPersistenceDocument(
            NpcVisualPreviewPersistenceContract.BundleSchema,
            NpcVisualPreviewPersistenceContract.SceneSchema,
            NpcVisualPreviewPersistenceContract.OffEngineLabel,
            false,
            source,
            views.ToImmutable(),
            contact,
            new Sha256Hash(Hash(File.ReadAllBytes(contact.Value))),
            render,
            new NpcVisualPreviewVisualEvidence(1, 0.99, 478, 31, true, []),
            []);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        NpcVisualPreviewJson.AddCanonicalDictionaryConverters(options);
        WorkspacePath bundle = Child(
            previewRoot,
            "npc-preview-bundle.json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, options);
        File.WriteAllBytes(bundle.Value, bytes);
        WorkspacePath hashes = Child(
            previewRoot,
            "npc-preview.hashes.sha256");
        string content = string.Join(
            "\n",
            Directory.EnumerateFiles(
                    previewRoot.Value,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path => !string.Equals(
                    path,
                    hashes.Value,
                    StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .Select(path =>
                    $"{Hash(File.ReadAllBytes(path)).ToLowerInvariant()}  " +
                    Path.GetRelativePath(previewRoot.Value, path)
                        .Replace('\\', '/'))) + "\n";
        File.WriteAllText(hashes.Value, content, new UTF8Encoding(false));
        return (bundle, bytes);
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var result = new MemoryStream();
        result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(4),
            checked((uint)height));
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(result, "IHDR", header);
        using var raw = new MemoryStream();
        byte[] row = new byte[checked((width * 4) + 1)];
        for (int index = 0; index < height; index++)
            raw.Write(row);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(
                   compressed,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
            zlib.Write(raw.GetBuffer().AsSpan(0, checked((int)raw.Length)));
        WritePngChunk(result, "IDAT", compressed.ToArray());
        WritePngChunk(result, "IEND", ReadOnlySpan<byte>.Empty);
        return result.ToArray();
    }

    private static void WritePngChunk(
        MemoryStream destination,
        string type,
        ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        destination.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        destination.Write(typeBytes);
        destination.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in typeBytes.Concat(data.ToArray()))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^
                    (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        destination.Write(checksum);
    }

    private static string Effect(ProtocolEffect effect) =>
        $"{JsonNamingPolicy.CamelCase.ConvertName(effect.Kind.ToString())}|" +
        $"{effect.Status}|{effect.Scope}";

    private static AgentAuthorityState Authority(
        ProtocolCommandResult result,
        AgentAuthorityKind kind) => result.Authority.Single(item =>
            item.Kind == kind).State;

    private static bool HasAdmittedAuthority(ProtocolCommandResult result) =>
        result.Authority.Length == 8 &&
        Authority(result, AgentAuthorityKind.InputAdmission) ==
            AgentAuthorityState.Established &&
        Authority(result, AgentAuthorityKind.SourceProviderIdentity) ==
            AgentAuthorityState.Established &&
        Authority(result, AgentAuthorityKind.DeterministicMaterialization) ==
            AgentAuthorityState.Established &&
        Authority(result, AgentAuthorityKind.IndependentStaticVerification) ==
            AgentAuthorityState.Established &&
        Authority(result, AgentAuthorityKind.OffEnginePreview) ==
            AgentAuthorityState.Established &&
        Authority(result, AgentAuthorityKind.HumanVisualAcceptance) ==
            AgentAuthorityState.Required &&
        Authority(result, AgentAuthorityKind.GameRuntimeVerification) ==
            AgentAuthorityState.Required &&
        Authority(result, AgentAuthorityKind.PromotionApproval) ==
            AgentAuthorityState.Required;

    private static bool HasPublishedUnverifiedAuthority(
        ProtocolCommandResult result) =>
        result.Authority.Length == 8 &&
        Authority(result, AgentAuthorityKind.InputAdmission) ==
            AgentAuthorityState.Established &&
        Authority(result, AgentAuthorityKind.SourceProviderIdentity) ==
            AgentAuthorityState.Established &&
        Authority(result, AgentAuthorityKind.DeterministicMaterialization) ==
            AgentAuthorityState.Established &&
        Authority(result, AgentAuthorityKind.IndependentStaticVerification) ==
            AgentAuthorityState.Required &&
        Authority(result, AgentAuthorityKind.OffEnginePreview) ==
            AgentAuthorityState.Established &&
        Authority(result, AgentAuthorityKind.HumanVisualAcceptance) ==
            AgentAuthorityState.Required &&
        Authority(result, AgentAuthorityKind.GameRuntimeVerification) ==
            AgentAuthorityState.Required &&
        Authority(result, AgentAuthorityKind.PromotionApproval) ==
            AgentAuthorityState.Required;

    private static bool HasBindingAuthority(ProtocolCommandResult result) =>
        result.Authority.Length == 8 &&
        Authority(result, AgentAuthorityKind.InputAdmission) ==
            AgentAuthorityState.Blocked &&
        new[]
        {
            AgentAuthorityKind.SourceProviderIdentity,
            AgentAuthorityKind.DeterministicMaterialization,
            AgentAuthorityKind.IndependentStaticVerification,
            AgentAuthorityKind.OffEnginePreview,
            AgentAuthorityKind.HumanVisualAcceptance,
            AgentAuthorityKind.GameRuntimeVerification,
            AgentAuthorityKind.PromotionApproval
        }.All(kind => Authority(result, kind) == AgentAuthorityState.Required);

    private static bool HasResult(
        ProtocolCommandResult result,
        bool created,
        string status,
        string? outcome,
        bool hasReceipt,
        bool hasWorkflow)
    {
        if (result.Result is not { } value ||
            value.GetProperty("created").GetBoolean() != created ||
            value.GetProperty("status").GetString() != status ||
            value.GetProperty("humanVisualAuthority").GetBoolean() ||
            value.GetProperty("runtimeAuthority").GetBoolean() ||
            value.GetProperty("promotionAuthority").GetBoolean())
            return false;
        JsonElement outcomeValue = value.GetProperty("outcome");
        if (outcome is null
                ? outcomeValue.ValueKind != JsonValueKind.Null
                : outcomeValue.GetString() != outcome)
            return false;
        return (value.GetProperty("receiptPath").ValueKind !=
                    JsonValueKind.Null) == hasReceipt &&
               (value.GetProperty("workflowPath").ValueKind !=
                    JsonValueKind.Null) == hasWorkflow;
    }

    private static bool HasPublishedUnverifiedResult(
        ProtocolCommandResult result,
        ReviewOutcome outcome,
        WorkspacePath receipt,
        ProtocolArtifact artifact)
    {
        if (result.Result is not { } value ||
            !value.GetProperty("created").GetBoolean() ||
            value.GetProperty("status").GetString() !=
                "review-receipt-persisted-unverified" ||
            value.GetProperty("outcome").GetString() !=
                JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()) ||
            !string.Equals(
                value.GetProperty("receiptPath").GetString(),
                receipt.Value,
                StringComparison.OrdinalIgnoreCase) ||
            value.GetProperty("receiptSize").GetInt64() != artifact.Size ||
            value.GetProperty("receiptSha256").GetString() != artifact.Sha256 ||
            value.GetProperty("workflowPath").ValueKind != JsonValueKind.Null ||
            value.GetProperty("workflowSize").ValueKind != JsonValueKind.Null ||
            value.GetProperty("workflowSha256").ValueKind != JsonValueKind.Null ||
            value.GetProperty("humanVisualAuthority").GetBoolean() ||
            value.GetProperty("runtimeAuthority").GetBoolean() ||
            value.GetProperty("promotionAuthority").GetBoolean())
            return false;
        return true;
    }

    private static void SetPinnedFileSystem(
        AgentReviewReceiptService service,
        FaceGeomHairRegionsPinnedFileSystem fileSystem)
    {
        System.Reflection.FieldInfo field =
            typeof(AgentReviewReceiptService).GetField(
                "fileSystem",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "AgentReviewReceiptService file-system field was not found.");
        field.SetValue(service, fileSystem);
    }

    private static void SetPinnedFileSystem(
        AgentWorkflowBundleCodec codec,
        FaceGeomHairRegionsPinnedFileSystem fileSystem)
    {
        System.Reflection.FieldInfo field =
            typeof(AgentWorkflowBundleCodec).GetField(
                "fileSystem",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "AgentWorkflowBundleCodec file-system field was not found.");
        field.SetValue(codec, fileSystem);
    }

    private static void SetPinnedFileSystem(
        SkyrimNpcFinishCoreWorkflowDocumentStore store,
        FaceGeomHairRegionsPinnedFileSystem fileSystem)
    {
        System.Reflection.FieldInfo field =
            typeof(SkyrimNpcFinishCoreWorkflowDocumentStore).GetField(
                "fileSystem",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Skyrim Finish workflow document-store file-system field was not found.");
        field.SetValue(store, fileSystem);
    }

    private static bool IsUpperSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void RequireWriteLocked(string path, string role)
    {
        try
        {
            using FileStream _ = File.Open(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None);
        }
        catch (IOException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"The promoted {role} was not retained through terminal response lifetime.");
    }

    private static void RequireWriteReleased(string path, string role)
    {
        try
        {
            using FileStream _ = File.Open(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"The promoted {role} remained locked after terminal response disposal.",
                exception);
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static void RequireThrows<T>(Action action, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class FakeAdmission(
        AgentWorkflowBundleTransition transition,
        Action<int>? revalidate = null,
        IEnumerable<IDisposable>? retained = null) :
        IProtocolV2ReviewPredecessorAdmission
    {
        private int revalidations;

        internal int Calls { get; private set; }

        public ValueTask<ProtocolV2ReviewPredecessorLease> AdmitAsync(
            ProtocolV2PhysicalFileBinding workflow,
            ProtocolV2PhysicalFileBinding proposal,
            ProtocolV2PhysicalFileBinding preview,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            WorkflowArtifactBinding expectedProposal = transition.Document
                .Bundle.Artifacts.Single(item =>
                    item.Kind is WorkflowArtifactKinds.NpcPackageManifest or
                        WorkflowArtifactKinds.NpcFinishCoreProposal);
            WorkflowArtifactBinding expectedPreview = transition.Document
                .Bundle.Artifacts.Single(item => item.Kind ==
                    WorkflowArtifactKinds.NpcPreviewManifest);
            if (workflow.Path != transition.Document.Path ||
                workflow.Sha256 != transition.Document.Sha256 ||
                proposal.Path != expectedProposal.Path ||
                proposal.Sha256 != expectedProposal.Sha256 ||
                preview.Path != expectedPreview.Path ||
                preview.Sha256 != expectedPreview.Sha256)
                throw new InvalidDataException(
                    "The fake admission observed a stale physical binding.");
            return ValueTask.FromResult(
                new ProtocolV2ReviewPredecessorLease(
                    transition,
                    expectedProposal,
                    expectedPreview,
                    retained ?? [],
                    () => revalidate?.Invoke(
                        Interlocked.Increment(ref revalidations))));
        }
    }

    private sealed class DisposalProbe : IDisposable
    {
        internal bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class MutatePinnedReadbackHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        internal bool Mutated { get; private set; }

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) => actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(string sourcePath, string destinationPath)
        {
        }

        public void BeforeCleanup(string admittedPath)
        {
        }

        public void AfterPinnedReadback(
            string admittedPath,
            byte[] observed)
        {
            if (Mutated)
                return;
            byte[] marker = "ReviewNpc"u8.ToArray();
            int offset = FindSequence(observed, marker);
            Require(offset >= 0,
                $"Read-mutation hook could not find its canonical marker in {admittedPath}.");
            observed[offset + marker.Length - 1] ^= 0x01;
            Mutated = true;
        }

        private static int FindSequence(
            ReadOnlySpan<byte> bytes,
            ReadOnlySpan<byte> needle)
        {
            for (int offset = 0; offset <= bytes.Length - needle.Length; offset++)
            {
                if (bytes.Slice(offset, needle.Length).SequenceEqual(needle))
                    return offset;
            }
            return -1;
        }
    }

    private sealed class RefusePrepublicationReparseHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        private bool substituted;

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath)
        {
            if (substituted)
                return actualFinalPath;
            substituted = true;
            return Path.Combine(
                actualFinalPath,
                "injected-reparse-substitution");
        }

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(string sourcePath, string destinationPath)
        {
        }

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private sealed class FailAccessDeniedBeforeCreateHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) => actualFinalPath;

        public void BeforeCreate(string admittedPath) =>
            throw new UnauthorizedAccessException("Access is denied.");

        public void BeforeRename(string sourcePath, string destinationPath)
        {
        }

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private sealed class FailBeforeRenameHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) => actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(string sourcePath, string destinationPath) =>
            throw new IOException(
                "Injected prepublication receipt rename failure.");

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private sealed class CorruptStagedReadbackHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        private bool corrupted;

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) => actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(string sourcePath, string destinationPath)
        {
        }

        public void BeforeCleanup(string admittedPath)
        {
        }

        public void AfterOwnedReadback(
            string admittedPath,
            byte[] observed)
        {
            if (corrupted || observed.Length == 0)
                return;
            observed[^1] ^= 0x01;
            corrupted = true;
        }
    }

    private sealed class CorruptPromotedReadbackHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        private int readbacks;

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) => actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(string sourcePath, string destinationPath)
        {
        }

        public void BeforeCleanup(string admittedPath)
        {
        }

        public void AfterOwnedReadback(
            string admittedPath,
            byte[] observed)
        {
            if (++readbacks != 2 || observed.Length == 0)
                return;
            observed[^1] ^= 0x01;
        }
    }

    private sealed class ObserveTwoFilePromotionsHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public List<string> RenameDestinations { get; } = [];

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) => actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(string sourcePath, string destinationPath) =>
            RenameDestinations.Add(destinationPath);

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private sealed class FailFirstPostRenameValidationHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        private bool renamed;
        private int resolutionsAfterRename;

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath)
        {
            if (renamed && ++resolutionsAfterRename == 1)
                throw new IOException(
                    "Injected first post-rename retained-handle validation failure.");
            return actualFinalPath;
        }

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(string sourcePath, string destinationPath)
        {
            renamed = true;
            resolutionsAfterRename = 0;
        }

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private sealed class FailPromotedReadbackHooks :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        private bool promoted;
        private int resolutionsAfterRename;

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath)
        {
            if (promoted && ++resolutionsAfterRename == 2)
                throw new IOException(
                    "Injected promoted receipt readback failure.");
            return actualFinalPath;
        }

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(string sourcePath, string destinationPath)
        {
            promoted = true;
            resolutionsAfterRename = 0;
        }

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private sealed class ProbingJournal(Action probe) :
        ILocalOperationJournal
    {
        internal List<OperationJournalRecord> Records { get; } = [];

        internal int Probes { get; private set; }

        public ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Probes++;
            probe();
            Records.Add(record);
            return ValueTask.FromResult(
                new OperationJournalAppendResult(true, null, null));
        }
    }

    private sealed class ProbingWriter(Action probe) : StringWriter
    {
        internal int Probes { get; private set; }

        internal string Content => ToString();

        public override void WriteLine(string? value)
        {
            Probes++;
            probe();
            base.WriteLine(value);
        }
    }

    private sealed record AcceptedReviewEvidence(
        ProtocolArtifact Receipt,
        ProtocolArtifact Workflow,
        AgentWorkflowBundleDocument Successor);

    private sealed record ReviewFixture(
        WorkspacePath Root,
        KOnlyWorkspacePolicy Policy,
        AgentReviewReceiptService ReceiptService,
        AgentWorkflowBundleTransition Transition,
        WorkflowArtifactBinding Proposal,
        WorkflowArtifactBinding Preview,
        WorkspacePath? SourcePackageRoot = null,
        WorkspacePath? SourceManifest = null,
        WorkspacePath? SourcePlugin = null,
        WorkspacePath? SourceFaceGeom = null,
        WorkspacePath? SourceFaceTint = null)
    {
        internal static ReviewFixture CreatePackage(
            WorkspacePath root,
            AgentWorkflowBundleTransitionService lifecycle,
            string name = "package",
            bool previewAssetDrift = false,
            bool copyPreviewAssets = false,
            bool wrongPreviewProvider = false,
            bool wrongPreviewPath = false)
        {
            var policy = new KOnlyWorkspacePolicy(
                root,
                new WorkspacePath(@"F:\ExampleGame"));
            var receiptService = new AgentReviewReceiptService(policy, root);
            WorkspacePath packageRoot = Child(root, "inputs", name, "package");
            WorkspacePath plugin = WritePackageFile(
                packageRoot,
                Path.Combine("Data", "ReviewNpc.esp"),
                "plugin");
            WorkspacePath faceGeom = WritePackageFile(
                packageRoot,
                Path.Combine(
                    "Data", "meshes", "actors", "character",
                    "FaceGenData", "FaceGeom", "ReviewNpc.esp",
                    "00000800.nif"),
                "facegeom");
            WorkspacePath faceTint = WritePackageFile(
                packageRoot,
                Path.Combine(
                    "Data", "textures", "actors", "character",
                    "FaceGenData", "FaceTint", "ReviewNpc.esp",
                    "00000800.dds"),
                "facetint");
            WorkspacePath proposal = WritePackageFile(
                packageRoot,
                Path.Combine("evidence", "npc-creation-proposal.json"),
                "{\"schemaVersion\":1,\"artifactKind\":\"npc-creation-proposal\"}");
            WorkspacePath manifest = Child(
                packageRoot,
                "npcmanager-package.json");
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                edition = "skyrimse",
                presetFormat = "blank-npc-creation-proposal",
                sourcePreset = "evidence/npc-creation-proposal.json",
                sourcePresetSha256 = Hash(File.ReadAllBytes(proposal.Value)),
                sourcePlugin = "Skyrim.esm",
                sourcePluginSha256 = new string('1', 64),
                outputPlugin = "ReviewNpc.esp",
                targetFormId = "00000800",
                artifacts = new[]
                {
                    PackageRow(packageRoot, "plugin", plugin),
                    PackageRow(packageRoot, "faceGeom", faceGeom),
                    PackageRow(packageRoot, "faceTint", faceTint),
                    PackageRow(
                        packageRoot,
                        "npc-creation-proposal",
                        proposal)
                }
            });
            File.WriteAllBytes(manifest.Value, manifestBytes);
            string manifestSha = Hash(manifestBytes);
            var package = new WorkflowArtifactBinding(
                WorkflowArtifactKinds.NpcPackageManifest,
                "application/json",
                manifest,
                manifestBytes.LongLength,
                manifestSha,
                "npc create-from-jslot",
                PredecessorDigest,
                []);

            WorkspacePath previewFaceGeom = faceGeom;
            WorkspacePath previewFaceTint = faceTint;
            string? previewProvider = null;
            if (copyPreviewAssets)
            {
                WorkspacePath copiedRoot = Child(root, "inputs", name, "preview",
                    wrongPreviewPath ? "foreign-assets" : "assets");
                previewFaceGeom = Child(copiedRoot, Path.GetRelativePath(packageRoot.Value, faceGeom.Value));
                previewFaceTint = Child(copiedRoot, Path.GetRelativePath(packageRoot.Value, faceTint.Value));
                Directory.CreateDirectory(Path.GetDirectoryName(previewFaceGeom.Value)!);
                Directory.CreateDirectory(Path.GetDirectoryName(previewFaceTint.Value)!);
                File.Copy(faceGeom.Value, previewFaceGeom.Value);
                File.Copy(faceTint.Value, previewFaceTint.Value);
                previewProvider = wrongPreviewProvider ? "package-overlay:foreign" :
                    $"package-overlay:{Path.GetFileName(packageRoot.Value)}";
            }
            if (previewAssetDrift)
            {
                previewFaceGeom = WritePackageFile(
                    Child(root, "inputs", name, "foreign"),
                    "foreign.nif",
                    "different-facegeom");
                previewFaceTint = WritePackageFile(
                    Child(root, "inputs", name, "foreign"),
                    "foreign.dds",
                    "different-facetint");
            }
            (WorkspacePath previewPath, byte[] previewBytes) =
                WriteCanonicalPreview(
                    root,
                    name,
                    faceGeomSource: previewFaceGeom,
                    faceTintSource: previewFaceTint,
                    assetProvider: previewProvider);
            var preview = new WorkflowArtifactBinding(
                WorkflowArtifactKinds.NpcPreviewManifest,
                NpcVisualPreviewPersistenceContract.BundleSchema,
                previewPath,
                previewBytes.LongLength,
                Hash(previewBytes),
                "preview npc",
                PredecessorDigest,
                [manifestSha]);
            WorkspacePath workflow = Child(
                root,
                "inputs",
                name,
                "workflow.json");
            AgentWorkflowBundleTransition transition = lifecycle.WriteInitial(
                new WorkflowNpcIdentity(
                    "ReviewNpc",
                    "Review NPC",
                    "ReviewNpc.esp",
                    "0x00000800"),
                PredecessorDigest,
                [package, preview],
                workflow);
            return new ReviewFixture(
                root,
                policy,
                receiptService,
                transition,
                package,
                preview);
        }

        internal static ReviewFixture CreateFinish(
            WorkspacePath root,
            AgentWorkflowBundleTransitionService lifecycle,
            string name = "finish",
            bool nestedRequestDrift = false,
            bool workflowNpcDrift = false,
            bool previewIdentityDrift = false,
            bool previewLineageDrift = false,
            bool previewAssetIdentityDrift = false,
            bool crossPackageManifest = false,
            bool includePreviewIntake = false,
            bool wrongPreviewPackage = false) => Create(
                root,
                lifecycle,
                name,
                [
                    (WorkflowArtifactKinds.NpcFinishCoreRequest, null),
                    (WorkflowArtifactKinds.NpcFinishCoreProposal, null),
                    (WorkflowArtifactKinds.NpcPreviewManifest, null)
                ],
                nestedRequestDrift,
                workflowNpcDrift,
                previewIdentityDrift,
                previewLineageDrift,
                previewAssetIdentityDrift,
                crossPackageManifest,
                includePreviewIntake,
                wrongPreviewPackage);

        private static ReviewFixture Create(
            WorkspacePath root,
            AgentWorkflowBundleTransitionService lifecycle,
            string name,
            (string Kind, string? SemanticSha256)[] kinds,
            bool nestedRequestDrift,
            bool workflowNpcDrift = false,
            bool previewIdentityDrift = false,
            bool previewLineageDrift = false,
            bool previewAssetIdentityDrift = false,
            bool crossPackageManifest = false,
            bool includePreviewIntake = false,
            bool wrongPreviewPackage = false)
        {
            var policy = new KOnlyWorkspacePolicy(
                root,
                new WorkspacePath(@"F:\ExampleGame"));
            var receiptService = new AgentReviewReceiptService(policy, root);
            ImmutableArray<WorkflowArtifactBinding>.Builder artifacts =
                ImmutableArray.CreateBuilder<WorkflowArtifactBinding>();
            WorkspacePath? finishPackageRoot = null;
            WorkspacePath? finishFaceGeom = null;
            WorkspacePath? finishFaceTint = null;
            WorkspacePath? finishManifest = null;
            WorkspacePath? finishPlugin = null;
            Sha256Hash? finishPackageTreeSha256 = null;
            if (kinds.Any(item => item.Kind ==
                    WorkflowArtifactKinds.NpcFinishCoreRequest))
            {
                FinishSourcePackage source = WriteFinishSourcePackage(
                    Child(root, "inputs", name, "source-package"),
                    "canonical");
                finishPackageRoot = source.Root;
                finishManifest = source.Manifest;
                finishPlugin = source.Plugin;
                finishFaceGeom = source.FaceGeom;
                finishFaceTint = source.FaceTint;
                finishPackageTreeSha256 = source.TreeSha256;
                if (crossPackageManifest)
                {
                    FinishSourcePackage foreign = WriteFinishSourcePackage(
                        Child(root, "inputs", name, "foreign-package"),
                        "foreign");
                    finishManifest = foreign.Manifest;
                }
            }
            SkyrimNpcFinishCoreRequest? finishRequest = kinds.Any(item =>
                item.Kind == WorkflowArtifactKinds.NpcFinishCoreRequest)
                ? new SkyrimNpcFinishCoreRequest
                {
                    Source = new SkyrimNpcFinishCoreSource
                    {
                        PackageRoot = finishPackageRoot,
                        PackageManifest = finishManifest,
                        PackageManifestSha256 = new Sha256Hash(Hash(
                            File.ReadAllBytes(finishManifest!.Value.Value))),
                        PackageTreeSha256 = finishPackageTreeSha256,
                        PluginPath = finishPlugin,
                        Plugin = new PluginName("ReviewNpc.esp"),
                        PluginSha256 = new Sha256Hash(Hash(
                            File.ReadAllBytes(finishPlugin!.Value.Value)))
                    },
                    Actor = new SkyrimNpcFinishCoreActor
                    {
                        EditorId = new EditorId("ReviewNpc"),
                        FormId = new FormId(0x800)
                    },
                    AiPolicy = CurrentV2FinishAiPolicy(),
                    SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
                    {
                        Template = new FormReference(
                            new PluginName("Skyrim.esm"),
                            new FormId(0x1B217)),
                        TemplateEditorId =
                            "DefaultSandboxEditorLocation512"
                    },
                    Output = new SkyrimNpcFinishCoreOutput
                    {
                        PluginFileName = "ReviewNpc.esp"
                    }
                }
                : null;
            byte[]? finishRequestBytes = finishRequest is null
                ? null
                : SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                    finishRequest,
                    root);
            foreach ((string kind, string? semanticSha256) in kinds)
            {
                string? artifactSemanticSha256 = semanticSha256;
                byte[] bytes;
                if (kind == WorkflowArtifactKinds.NpcFinishCoreRequest)
                    bytes = finishRequestBytes!;
                else if (kind ==
                         WorkflowArtifactKinds.NpcFinishCoreProposal)
                {
                    SkyrimNpcFinishCoreRequest nestedRequest =
                        nestedRequestDrift
                            ? finishRequest! with
                            {
                                Actor = finishRequest!.Actor with
                                {
                                    EditorId = new EditorId(
                                        "DifferentNestedNpc")
                                }
                            }
                            : finishRequest!;
                    var finishProposal = new SkyrimNpcFinishCoreProposal
                    {
                        Request = nestedRequest,
                        RequestSha256 = new Sha256Hash(
                            Hash(finishRequestBytes!)),
                        Status = SkyrimNpcFinishCoreStatus
                            .ReadyForReviewedWrite
                    };
                    Sha256Hash semantic =
                        SkyrimNpcFinishCoreDocumentCodec
                            .HashProposalWithoutSelf(
                                SkyrimNpcFinishCoreDocumentCodec
                                    .SerializeProposal(finishProposal, root));
                    bytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                        finishProposal with { ProposalSha256 = semantic },
                        root);
                    artifactSemanticSha256 = semantic.Value.ToUpperInvariant();
                }
                else if (kind ==
                         WorkflowArtifactKinds.NpcPreviewManifest &&
                         finishRequest is not null)
                {
                    (WorkspacePath previewPath, byte[] previewBytes) =
                        WriteCanonicalPreview(
                            root,
                            name,
                            sourcePlugin: previewIdentityDrift
                                ? "DifferentNpc.esp"
                                : "ReviewNpc.esp",
                            assetPlugin: previewAssetIdentityDrift
                                ? "DifferentNpc.esp"
                                : null,
                            faceGeomSource: finishFaceGeom,
                            faceTintSource: finishFaceTint);
                    bytes = previewBytes;
                    string previewSha = Hash(bytes);
                    string sourcePackageSha = wrongPreviewPackage ? new string('C', 64) :
                        finishRequest.Source.PackageManifestSha256!.Value.Value.ToUpperInvariant();
                    ImmutableArray<string> previewInputs = previewLineageDrift ? [] :
                        includePreviewIntake ? new[] { PredecessorDigest, sourcePackageSha }
                            .Order(StringComparer.Ordinal).ToImmutableArray() : [sourcePackageSha];
                    artifacts.Add(new WorkflowArtifactBinding(
                        kind,
                        NpcVisualPreviewPersistenceContract.BundleSchema,
                        previewPath,
                        bytes.LongLength,
                        previewSha,
                        "preview npc",
                        PredecessorDigest,
                        previewInputs));
                    continue;
                }
                else
                    bytes = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        Kind = kind,
                        Fixture = name
                    });
                WorkspacePath path = Child(
                    root,
                    "inputs",
                    name,
                    kind + ".json");
                File.WriteAllBytes(path.Value, bytes);
                string sha = Hash(bytes);
                ImmutableArray<string> inputs = kind ==
                        WorkflowArtifactKinds.NpcPreviewManifest
                    ? [artifacts[^1].Sha256]
                    : [];
                artifacts.Add(new WorkflowArtifactBinding(
                    kind,
                    "application/json",
                    path,
                    bytes.LongLength,
                    sha,
                    kind == WorkflowArtifactKinds.NpcPreviewManifest
                        ? "preview npc"
                        : "npc finish analyze",
                    PredecessorDigest,
                    inputs,
                    artifactSemanticSha256));
            }
            WorkspacePath workflow = Child(
                root,
                "inputs",
                name,
                "workflow.json");
            AgentWorkflowBundleTransition transition = lifecycle.WriteInitial(
                new WorkflowNpcIdentity(
                    workflowNpcDrift ? "DifferentNpc" : "ReviewNpc",
                    "Review NPC",
                    workflowNpcDrift ? "DifferentNpc.esp" : "ReviewNpc.esp",
                    "0x00000800"),
                PredecessorDigest,
                artifacts.ToImmutable(),
                workflow);
            WorkflowArtifactBinding proposal = transition.Document.Bundle
                .Artifacts.Single(item =>
                    item.Kind is WorkflowArtifactKinds.NpcPackageManifest or
                        WorkflowArtifactKinds.NpcFinishCoreProposal);
            WorkflowArtifactBinding preview = transition.Document.Bundle
                .Artifacts.Single(item => item.Kind ==
                    WorkflowArtifactKinds.NpcPreviewManifest);
            return new ReviewFixture(
                root,
                policy,
                receiptService,
                transition,
                proposal,
                preview,
                finishPackageRoot,
                finishManifest,
                finishPlugin,
                finishFaceGeom,
                finishFaceTint);
        }

        private static FinishSourcePackage WriteFinishSourcePackage(
            WorkspacePath packageRoot,
            string marker)
        {
            WorkspacePath plugin = WritePackageFile(
                packageRoot,
                Path.Combine("Data", "ReviewNpc.esp"),
                marker + "-plugin");
            WorkspacePath faceGeom = WritePackageFile(
                packageRoot,
                Path.Combine(
                    "Data", "meshes", "actors", "character",
                    "FaceGenData", "FaceGeom", "ReviewNpc.esp",
                    "00000800.nif"),
                marker + "-facegeom");
            WorkspacePath faceTint = WritePackageFile(
                packageRoot,
                Path.Combine(
                    "Data", "textures", "actors", "character",
                    "FaceGenData", "FaceTint", "ReviewNpc.esp",
                    "00000800.dds"),
                marker + "-facetint");
            WorkspacePath proposal = WritePackageFile(
                packageRoot,
                Path.Combine("evidence", "npc-creation-proposal.json"),
                "{\"schemaVersion\":1,\"artifactKind\":\"npc-creation-proposal\",\"fixture\":\"" +
                marker + "\"}");
            WorkspacePath manifest = Child(
                packageRoot,
                "npcmanager-package.json");
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                edition = "skyrimse",
                presetFormat = "blank-npc-creation-proposal",
                sourcePreset = "evidence/npc-creation-proposal.json",
                sourcePresetSha256 = Hash(File.ReadAllBytes(proposal.Value)),
                sourcePlugin = "Skyrim.esm",
                sourcePluginSha256 = new string('1', 64),
                outputPlugin = "ReviewNpc.esp",
                targetFormId = "00000800",
                artifacts = new[]
                {
                    PackageRow(packageRoot, "plugin", plugin),
                    PackageRow(packageRoot, "faceGeom", faceGeom),
                    PackageRow(packageRoot, "faceTint", faceTint),
                    PackageRow(
                        packageRoot,
                        "npc-creation-proposal",
                        proposal)
                }
            });
            File.WriteAllBytes(manifest.Value, manifestBytes);
            return new FinishSourcePackage(
                packageRoot,
                manifest,
                plugin,
                faceGeom,
                faceTint,
                SkyrimNpcFinishCoreSourcePackageReader
                    .ComputePackageTreeSha256(packageRoot));
        }

        private static WorkspacePath WritePackageFile(
            WorkspacePath packageRoot,
            string relativePath,
            string content)
        {
            WorkspacePath path = Child(packageRoot, relativePath);
            File.WriteAllText(path.Value, content, new UTF8Encoding(false));
            return path;
        }

        private static object PackageRow(
            WorkspacePath packageRoot,
            string kind,
            WorkspacePath path) => new
        {
            kind,
            relativePath = Path.GetRelativePath(
                    packageRoot.Value,
                    path.Value)
                .Replace(Path.DirectorySeparatorChar, '/'),
            byteLength = new FileInfo(path.Value).Length,
            sha256 = Hash(File.ReadAllBytes(path.Value))
        };

        private sealed record FinishSourcePackage(
            WorkspacePath Root,
            WorkspacePath Manifest,
            WorkspacePath Plugin,
            WorkspacePath FaceGeom,
            WorkspacePath FaceTint,
            Sha256Hash TreeSha256);

        internal ParsedCommand Command(
            WorkspacePath receipt,
            WorkspacePath workflow,
            string outcome,
            string? attestation,
            bool valuelessAttestation = false)
        {
            ImmutableDictionary<string, string>.Builder options =
                ImmutableDictionary.CreateBuilder<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            options["workflow-bundle"] = Transition.Document.Path.Value;
            options["workflow-bundle-sha256"] = Transition.Document.Sha256;
            options["proposal"] = Proposal.Path.Value;
            options["proposal-sha256"] = Proposal.Sha256;
            options["preview-manifest"] = Preview.Path.Value;
            options["preview-manifest-sha256"] = Preview.Sha256;
            options["outcome"] = outcome;
            if (attestation is not null)
                options["operator-attestation"] = attestation;
            options["receipt-output"] = receipt.Value;
            options["workflow-output"] = workflow.Value;
            return new ParsedCommand(
                "gui",
                [],
                options.ToImmutable(),
                [],
                Json: true,
                Help: false,
                RequestedProtocol: "2",
                Correlation: null,
                ValuelessOptions: valuelessAttestation
                    ? ["operator-attestation"]
                    : []);
        }
    }
}
