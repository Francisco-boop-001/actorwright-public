using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static readonly JsonSerializerOptions ForgedPackageJsonOptions = new()
    {
        WriteIndented = true
    };

    internal static async Task RunProtocolV2FinishReviewedAsync(bool runtimeArchive = false)
    {
        SkyrimFinishMasterFixture.EnsureAvailable();
        string repository = Environment.CurrentDirectory;
        using var scratch = new OwnedScratchDirectory(
            Path.Combine(repository, "artifacts", "x"),
            "fr-");
        var root = new WorkspacePath(scratch.Root);
        foreach (string command in new[] { "version", "capabilities" })
            RequireSucceeded(await RunCliAsync(root, command, "--protocol", "2", "--json"), command);
        foreach (string command in new[] { "preview npc", "npc finish analyze", "npc finish apply", "npc finish verify" })
            RequireSucceeded(await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", command), "schema export");
        await CreateV2FinishSourceAsync(repository, root);
        WorkspacePath created = Child(root, "workflow", "created.json");
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(root.Value + "-protected"));
        var codec = new AgentWorkflowBundleCodec(policy, root);
        AgentWorkflowBundleDocument package = codec.Load(created, HashFile(created));
        WorkflowArtifactBinding intake = package.Bundle.Artifacts.Single(row => row.Kind == WorkflowArtifactKinds.ReviewedWorkspaceIntake);
        WorkflowArtifactBinding manifest = package.Bundle.Artifacts.Single(row => row.Kind == WorkflowArtifactKinds.NpcPackageManifest);
        PackageManifestIdentity sourcePackage = (await new PackageManifestReader(policy, root)
            .ReadAsync(manifest.Path, CancellationToken.None)).Identity ?? throw new InvalidOperationException("Created package manifest was not readable.");
        Require(sourcePackage.ManifestSha256 == new Sha256Hash(manifest.Sha256), "Created package manifest changed from its retained workflow binding.");
        PackageManifestFile faceGeom = sourcePackage.Files.Single(row => string.Equals(row.Kind, "facegeom", StringComparison.OrdinalIgnoreCase));
        NifGeometryReadbackResult readback = await new BethesdaNifGeometryReadbackService().ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.SkyrimSpecialEdition, new WorkspacePath(Path.Combine(Path.GetDirectoryName(manifest.Path.Value)!, faceGeom.RelativePath.Value)), faceGeom.Sha256), CancellationToken.None);
        Require(readback.Accepted && readback.Document is not null, "Actual created FaceGeom failed independent geometry admission: " + JsonSerializer.Serialize(readback.Diagnostics));
        CopyRendererAuthorities(repository, root);
        WorkspacePath previewWorkflow = Child(root, "workflow", "preview.json");
        ProtocolInvocation preview = await RunCliAsync(root, PreviewChildDeadline,
            "preview", "npc", "--protocol", "2", "--json", "--intake", intake.Path.Value,
            "--plugin", package.Bundle.Npc.Plugin!, "--form", package.Bundle.Npc.LocalFormId!,
            "--package-manifest", manifest.Path.Value, "--expected-package-sha256", manifest.Sha256,
            "--output-root", Child(root, "npc-preview").Value, "--workflow-bundle", created.Value,
            "--workflow-bundle-sha256", HashFile(created), "--workflow-output", previewWorkflow.Value);
        RequireSucceeded(preview, "preview npc");
        Console.WriteLine("Actual source creation and off-engine preview complete; no human visual acceptance is claimed.");

        SkyrimNpcFinishCoreRequest request = CreateV2FinishRequest(root);
        WorkspacePath requestPath = Child(root, "reviewed-request.json");
        File.WriteAllBytes(requestPath.Value, SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, root));
        (WorkspacePath packageReceipt, WorkspacePath reviewedPackage) = await ProduceFinishTestReceiptAsync(
            root, previewWorkflow, WorkflowArtifactKinds.NpcPackageManifest, "package-review");
        WorkspacePath proposal = Child(root, "reviewed-proposal.json");
        WorkspacePath analyzed = Child(root, "workflow", "reviewed-analyzed.json");
        RequireSucceeded(await RunCliAsync(root, "npc", "finish", "analyze", "--protocol", "2", "--json",
            "--request", requestPath.Value, "--request-sha256", HashFile(requestPath), "--proposal", proposal.Value,
            "--workflow-bundle", reviewedPackage.Value, "--workflow-bundle-sha256", HashFile(reviewedPackage),
            "--review-receipt", packageReceipt.Value, "--review-receipt-sha256", HashFile(packageReceipt),
            "--workflow-output", analyzed.Value), "npc finish analyze");
        (WorkspacePath finishReceipt, WorkspacePath reviewedProposal) = await ProduceFinishTestReceiptAsync(
            root, analyzed, WorkflowArtifactKinds.NpcFinishCoreProposal, "proposal-review");
        WorkspacePath applied = Child(root, "workflow", "reviewed-applied.json");
        RequireSucceeded(await RunCliAsync(root, "npc", "finish", "apply", "--protocol", "2", "--json",
            "--request", requestPath.Value, "--request-sha256", HashFile(requestPath),
            "--proposal", proposal.Value, "--proposal-sha256", HashFile(proposal),
            "--workflow-bundle", reviewedProposal.Value, "--workflow-bundle-sha256", HashFile(reviewedProposal),
            "--review-receipt", finishReceipt.Value, "--review-receipt-sha256", HashFile(finishReceipt),
            "--workflow-output", applied.Value), "npc finish apply");
        await VerifyReviewedFinishAsync(root, applied, "reviewed-verified");
        await VerifyFinishPackagingReviewGateAsync(
            root,
            request.Output.Root!.Value,
            applied);
        if (runtimeArchive)
            await AssertRuntimeArchiveAsync(
                root,
                request.Output.Root.Value,
                "reviewed-finish-runtime.zip",
                reviewedWorkflow: applied);

        SkyrimNpcFinishCoreRequest lateRequest = request with
        { Output = request.Output with { Root = Child(root, "late-finished"), Archive = Child(root, "late-finished.zip") } };
        WorkspacePath lateRequestPath = Child(root, "late-request.json");
        File.WriteAllBytes(lateRequestPath.Value, SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(lateRequest, root));
        WorkspacePath lateProposal = Child(root, "late-proposal.json");
        WorkspacePath lateAnalyzed = Child(root, "workflow", "late-analyzed.json");
        RequireSucceeded(await RunCliAsync(root, "npc", "finish", "analyze", "--protocol", "2", "--json",
            "--request", lateRequestPath.Value, "--request-sha256", HashFile(lateRequestPath), "--proposal", lateProposal.Value,
            "--workflow-bundle", previewWorkflow.Value, "--workflow-bundle-sha256", HashFile(previewWorkflow),
            "--workflow-output", lateAnalyzed.Value), "npc finish analyze");
        WorkspacePath lateApplied = Child(root, "workflow", "late-applied.json");
        RequireSucceeded(await RunCliAsync(root, "npc", "finish", "apply", "--protocol", "2", "--json",
            "--request", lateRequestPath.Value, "--request-sha256", HashFile(lateRequestPath),
            "--proposal", lateProposal.Value, "--proposal-sha256", HashFile(lateProposal),
            "--workflow-bundle", lateAnalyzed.Value, "--workflow-bundle-sha256", HashFile(lateAnalyzed),
            "--workflow-output", lateApplied.Value), "npc finish apply");
        (_, WorkspacePath lateReviewed) = await ProduceFinishTestReceiptAsync(root, lateApplied,
            WorkflowArtifactKinds.NpcFinishCoreProposal, "post-apply-review");
        await VerifyReviewedFinishAsync(root, lateReviewed, "post-review-verified");
        await RequireCrossPackageReviewRefusalAsync(
            root,
            request.Output.Root!.Value,
            lateReviewed);
        Require(new Sha256Hash(HashFile(request.Source.PluginPath!.Value)) == request.Source.PluginSha256 &&
            SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(request.Source.PackageRoot!.Value) == request.Source.PackageTreeSha256,
            "Reviewed Finish mutated the original source package.");
        Console.WriteLine("PASS actual package receipt→Finish analyze, proposal receipt→apply/verify, and post-apply receipt→verify; simulated operator attestation only.");
    }

    private static async Task VerifyFinishPackagingReviewGateAsync(
        WorkspacePath root,
        WorkspacePath packageRoot,
        WorkspacePath reviewedWorkflow)
    {
        WorkspacePath packageManifest = Child(packageRoot, "npcmanager-package.json");
        ProtocolInvocation unreviewed = await RunCliAsync(
            root,
            "package", "verify", "--json", "--manifest", packageManifest.Value);
        Require(
            unreviewed.ExitCode != 0 &&
            (unreviewed.Root.ToString() + unreviewed.StdErr).Contains(
                "workflow-human-review-required",
                StringComparison.Ordinal),
            "Finish package verification accepted no exact workflow bundle.");

        ProtocolInvocation verified = await RunCliAsync(
            root,
            "package", "verify", "--json", "--manifest", packageManifest.Value,
            "--workflow-bundle", reviewedWorkflow.Value,
            "--workflow-bundle-sha256", HashFile(reviewedWorkflow));
        Require(
            verified.ExitCode == 0,
            "reviewed Finish package verify failed: " + verified.Root + " stderr=" + verified.StdErr);
        Require(
            verified.Root.GetProperty("verified").GetBoolean(),
            "Reviewed Finish package verification did not verify the package.");

        WorkspacePath finishManifest = Child(
            packageRoot,
            "NPCManager/Evidence/finish-core-manifest.json");
        await RequireFinishManifestLeaseAcrossPackageOperationsAsync(
            root,
            packageManifest,
            packageRoot,
            finishManifest,
            reviewedWorkflow);
        await using (FileStream locked = File.Open(
                         finishManifest.Value,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.None))
        {
            ProtocolInvocation refused = await RunCliAsync(
                root,
                "package", "verify", "--json", "--manifest", packageManifest.Value,
                "--workflow-bundle", reviewedWorkflow.Value,
                "--workflow-bundle-sha256", HashFile(reviewedWorkflow));
            Require(
                refused.ExitCode != 0 &&
                (refused.Root.ToString() + refused.StdErr).Contains(
                    "workflow-human-review-required",
                    StringComparison.Ordinal),
                "A locked Finish manifest escaped the packaging gate as an untyped failure.");
        }

        WorkspacePath archive = Child(root, "reviewed-finish-package.zip");
        ProtocolInvocation unreviewedArchive = await RunCliAsync(
            root,
            "package", "archive", "--json", "--source-root", packageRoot.Value,
            "--output", Child(root, "unreviewed-finish-package.zip").Value);
        Require(
            unreviewedArchive.ExitCode != 0 &&
            (unreviewedArchive.Root.ToString() + unreviewedArchive.StdErr).Contains(
                "workflow-human-review-required",
                StringComparison.Ordinal),
            "Finish package archive accepted no exact workflow bundle.");
        ProtocolInvocation archived = await RunCliAsync(
            root,
            "package", "archive", "--json", "--source-root", packageRoot.Value,
            "--output", archive.Value,
            "--workflow-bundle", reviewedWorkflow.Value,
            "--workflow-bundle-sha256", HashFile(reviewedWorkflow));
        Require(
            archived.ExitCode == 0,
            "reviewed Finish package archive failed: " + archived.Root + " stderr=" + archived.StdErr);
        Require(File.Exists(archive.Value), "Reviewed Finish package archive was not written.");
        await RequireForgedHybridWorkflowRefusalAsync(
            root,
            packageManifest,
            finishManifest,
            reviewedWorkflow);
        await RequireForgedPackageAuthorityRefusalAsync(
            root,
            packageManifest,
            finishManifest,
            reviewedWorkflow);
    }

    private static async Task RequireFinishManifestLeaseAcrossPackageOperationsAsync(
        WorkspacePath root,
        WorkspacePath packageManifest,
        WorkspacePath packageRoot,
        WorkspacePath finishManifest,
        WorkspacePath reviewedWorkflow)
    {
        var probes = new FinishManifestLeaseProbeServices(finishManifest);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var handler = new PackageCommandHandler(
            new FinishPackageNoopBuildService(),
            new FinishPackageNoopInspectService(),
            probes,
            probes,
            root,
            output,
            error,
            new FinishVerificationSuccessService());
        CommandExitCode verify = await handler.RunAsync(CommandLine.Parse([
            "package", "verify", "--json", "--manifest", packageManifest.Value,
            "--workflow-bundle", reviewedWorkflow.Value,
            "--workflow-bundle-sha256", HashFile(reviewedWorkflow)]), CancellationToken.None);
        Require(verify == CommandExitCode.Success && probes.VerifyCalled,
            "Finish package verify lease probe did not reach the package verifier: " + error);
        Require(!probes.VerifyWriteOpened,
            "Finish package verify released its Finish-manifest read lease before verification completed.");

        CommandExitCode archive = await handler.RunAsync(CommandLine.Parse([
            "package", "archive", "--json", "--source-root", packageRoot.Value,
            "--output", Child(root, "lease-probe.zip").Value,
            "--workflow-bundle", reviewedWorkflow.Value,
            "--workflow-bundle-sha256", HashFile(reviewedWorkflow)]), CancellationToken.None);
        Require(archive == CommandExitCode.Success && probes.ArchiveCalled,
            "Finish package archive lease probe did not reach the package archiver: " + error);
        Require(!probes.ArchiveWriteOpened,
            "Finish package archive released its Finish-manifest read lease before archive publication completed.");
    }

    private static async Task RequireForgedHybridWorkflowRefusalAsync(
        WorkspacePath root,
        WorkspacePath packageManifest,
        WorkspacePath finishManifest,
        WorkspacePath reviewedWorkflow)
    {
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(root.Value + "-protected"));
        var codec = new AgentWorkflowBundleCodec(policy, root);
        AgentWorkflowBundleDocument reviewed = codec.Load(reviewedWorkflow, HashFile(reviewedWorkflow));
        WorkflowArtifactBinding oldManifest = reviewed.Bundle.Artifacts.Single(
            row => row.Kind == WorkflowArtifactKinds.NpcFinishCoreManifest);
        byte[] originalManifest = File.ReadAllBytes(finishManifest.Value);
        try
        {
            SkyrimNpcFinishCoreManifest parsed = SkyrimNpcFinishCoreDocumentCodec.ParseManifest(
                originalManifest, root);
            SkyrimNpcFinishCoreManifest forgedManifest = parsed with
            {
                RequestSha256 = new Sha256Hash(new string('A', 64)),
                ProposalSha256 = new Sha256Hash(new string('B', 64))
            };
            byte[] forgedBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(forgedManifest, root);
            File.WriteAllBytes(finishManifest.Value, forgedBytes);
            WorkspacePath forgedWorkflow = WriteForgedFinishWorkflow(
                root, codec, reviewed, oldManifest, finishManifest, forgedBytes,
                "forged-hybrid-package-review.json");

            ProtocolInvocation refused = await RunCliAsync(
                root,
                "package", "verify", "--json", "--manifest", packageManifest.Value,
                "--workflow-bundle", forgedWorkflow.Value,
                "--workflow-bundle-sha256", HashFile(forgedWorkflow));
            Require(
                refused.ExitCode != 0 &&
                (refused.Root.ToString() + refused.StdErr).Contains(
                    "workflow-human-review-required",
                    StringComparison.Ordinal),
                "A forged hybrid workflow combined an accepted receipt with unrelated Finish-manifest request/proposal identities.");
        }
        finally
        {
            File.WriteAllBytes(finishManifest.Value, originalManifest);
        }
    }

    private static async Task RequireForgedPackageAuthorityRefusalAsync(
        WorkspacePath root,
        WorkspacePath packageManifest,
        WorkspacePath finishManifest,
        WorkspacePath reviewedWorkflow)
    {
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(root.Value + "-protected"));
        var codec = new AgentWorkflowBundleCodec(policy, root);
        AgentWorkflowBundleDocument reviewed = codec.Load(reviewedWorkflow, HashFile(reviewedWorkflow));
        WorkflowArtifactBinding oldManifest = reviewed.Bundle.Artifacts.Single(
            row => row.Kind == WorkflowArtifactKinds.NpcFinishCoreManifest);
        byte[] originalManifest = File.ReadAllBytes(finishManifest.Value);
        byte[] originalPackageManifest = File.ReadAllBytes(packageManifest.Value);
        try
        {
            SkyrimNpcFinishCoreManifest parsed = SkyrimNpcFinishCoreDocumentCodec.ParseManifest(
                originalManifest, root);
            Sha256Hash forgedTree = new(new string('C', 64));
            SkyrimNpcFinishCoreManifest forgedManifest = parsed with
            {
                PluginSha256 = new Sha256Hash(new string('D', 64)),
                PackageTreeSha256 = forgedTree,
                Evidence = parsed.Evidence with { PackageTreeSha256 = forgedTree }
            };
            byte[] forgedBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(forgedManifest, root);
            File.WriteAllBytes(finishManifest.Value, forgedBytes);
            UpdatePackageManifestBinding(packageManifest, forgedBytes);
            PackageVerifyResult ordinary = await new PackageVerifyService(
                new PackageManifestReader(policy, root)).VerifyAsync(
                    new PackageVerifyRequest(packageManifest), CancellationToken.None);
            Require(ordinary.Verified,
                "Package-authority forgery did not retain ordinary package-manifest verification.");
            WorkspacePath forgedWorkflow = WriteForgedFinishWorkflow(
                root, codec, reviewed, oldManifest, finishManifest, forgedBytes,
                "forged-package-authority-review.json");

            ProtocolInvocation refused = await RunCliAsync(
                root,
                "package", "verify", "--json", "--manifest", packageManifest.Value,
                "--workflow-bundle", forgedWorkflow.Value,
                "--workflow-bundle-sha256", HashFile(forgedWorkflow));
            Require(
                refused.ExitCode != 0 &&
                (refused.Root.ToString() + refused.StdErr).Contains(
                    "workflow-human-review-required",
                    StringComparison.Ordinal),
                "A reviewed workflow authorized forged Finish package tree, plugin, or evidence authority.");
        }
        finally
        {
            File.WriteAllBytes(finishManifest.Value, originalManifest);
            File.WriteAllBytes(packageManifest.Value, originalPackageManifest);
        }
    }

    private static WorkspacePath WriteForgedFinishWorkflow(
        WorkspacePath root,
        AgentWorkflowBundleCodec codec,
        AgentWorkflowBundleDocument reviewed,
        WorkflowArtifactBinding oldManifest,
        WorkspacePath finishManifest,
        byte[] forgedBytes,
        string name)
    {
        string forgedSha256 = HashFile(finishManifest);
        WorkflowArtifactBinding forgedBinding = oldManifest with
        {
            Size = forgedBytes.LongLength,
            Sha256 = forgedSha256
        };
        AgentWorkflowBundle forgedBundle = reviewed.Bundle with
        {
            Artifacts = reviewed.Bundle.Artifacts.Select(row =>
                row.Kind == WorkflowArtifactKinds.NpcFinishCoreManifest ? forgedBinding : row).ToImmutableArray(),
            Authority = reviewed.Bundle.Authority.Select(row =>
                row.Kind == AgentAuthorityKind.DeterministicMaterialization
                    ? row with { ArtifactHashes = [forgedSha256] }
                    : row).ToImmutableArray(),
            NextActions = reviewed.Bundle.NextActions.Select(action => action with
            {
                RequiredBindings = action.RequiredBindings.Select(binding => binding.Option switch
                {
                    "--manifest" => binding with
                    {
                        Value = finishManifest.Value,
                        ArtifactSha256 = forgedSha256
                    },
                    "--manifest-sha256" => binding with
                    {
                        Value = forgedSha256,
                        ArtifactSha256 = forgedSha256
                    },
                    _ => binding
                }).ToImmutableArray()
            }).ToImmutableArray()
        };
        WorkspacePath forgedWorkflow = Child(root, "workflow", name);
        codec.WriteNew(forgedBundle, forgedWorkflow);
        return forgedWorkflow;
    }

    private static void UpdatePackageManifestBinding(
        WorkspacePath packageManifest,
        byte[] finishManifestBytes)
    {
        JsonObject document = JsonNode.Parse(File.ReadAllBytes(packageManifest.Value))!.AsObject();
        JsonObject binding = document["artifacts"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(row => string.Equals(
                row["relativePath"]!.GetValue<string>(),
                "NPCManager/Evidence/finish-core-manifest.json",
                StringComparison.Ordinal));
        binding["byteLength"] = finishManifestBytes.LongLength;
        binding["sha256"] = Convert.ToHexString(SHA256.HashData(finishManifestBytes)).ToLowerInvariant();
        File.WriteAllBytes(packageManifest.Value,
            JsonSerializer.SerializeToUtf8Bytes(document, ForgedPackageJsonOptions));
    }

    private static async Task RequireCrossPackageReviewRefusalAsync(
        WorkspacePath root,
        WorkspacePath packageRoot,
        WorkspacePath otherPackageWorkflow)
    {
        WorkspacePath packageManifest = Child(packageRoot, "npcmanager-package.json");
        ProtocolInvocation mismatched = await RunCliAsync(
            root,
            "package", "verify", "--json", "--manifest", packageManifest.Value,
            "--workflow-bundle", otherPackageWorkflow.Value,
            "--workflow-bundle-sha256", HashFile(otherPackageWorkflow));
        Require(
            mismatched.ExitCode != 0 &&
            (mismatched.Root.ToString() + mismatched.StdErr).Contains(
                "workflow-human-review-required",
                StringComparison.Ordinal),
            "A reviewed workflow for another Finish package authorized this package.");
    }

    private static async Task<(WorkspacePath Receipt, WorkspacePath Workflow)> ProduceFinishTestReceiptAsync(
        WorkspacePath root, WorkspacePath predecessor, string proposalKind, string name)
    {
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(root.Value + "-protected"));
        var codec = new AgentWorkflowBundleCodec(policy, root);
        AgentWorkflowBundleDocument document = codec.Load(predecessor, HashFile(predecessor));
        WorkflowArtifactBinding proposal = document.Bundle.Artifacts.Single(row => row.Kind == proposalKind);
        WorkflowArtifactBinding preview = document.Bundle.Artifacts.Single(row => row.Kind == WorkflowArtifactKinds.NpcPreviewManifest);
        WorkspacePath receipt = Child(root, "workflow", name + "-receipt.json");
        WorkspacePath successor = Child(root, "workflow", name + ".json");
        // Invoke the existing internal GUI receipt adapter with a simulated operator attestation;
        // it independently reopens actual source/preview evidence and writes the genuine receipt.
        ParsedCommand command = CommandLine.Parse(["gui", "--protocol", "2", "--json",
            "--workflow-bundle", predecessor.Value, "--workflow-bundle-sha256", HashFile(predecessor),
            "--proposal", proposal.Path.Value, "--proposal-sha256", proposal.Sha256,
            "--preview-manifest", preview.Path.Value, "--preview-manifest-sha256", preview.Sha256,
            "--outcome", "accepted", "--operator-attestation", "true", "--receipt-output", receipt.Value,
            "--workflow-output", successor.Value]) with
        { Positionals = [] };
        var service = new AgentReviewReceiptService(policy, root);
        ProtocolCommandResult result = await new ProtocolV2ReviewReceiptAdapter(root, service,
            new AgentWorkflowBundleTransitionService(codec)).RunAsync(command, ProtocolRequestDigest.Compute(command), CancellationToken.None);
        using (result.TerminalArtifactLease)
            Require(!result.Diagnostics.Any(row => row.Severity == DiagnosticSeverity.Error),
                "Actual receipt producer refused " + name + ": " + JsonSerializer.Serialize(result.Diagnostics));
        Require(File.Exists(receipt.Value) && File.Exists(successor.Value), "Receipt producer omitted physical outputs.");
        AgentWorkflowBundleDocument reviewed = codec.Load(successor, HashFile(successor));
        service.LoadForSuccessor(reviewed, receipt, HashFile(receipt));
        Require(reviewed.Bundle.Authority.Single(row => row.Kind == AgentAuthorityKind.HumanVisualAcceptance).State == AgentAuthorityState.Required,
            "Simulated operator acceptance must not grant human visual authority.");
        return (receipt, successor);
    }

    private static async Task VerifyReviewedFinishAsync(WorkspacePath root, WorkspacePath workflow, string name)
    {
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(root.Value + "-protected"));
        AgentWorkflowBundleDocument input = new AgentWorkflowBundleCodec(policy, root).Load(workflow, HashFile(workflow));
        WorkflowArtifactBinding manifest = input.Bundle.Artifacts.Single(row => row.Kind == WorkflowArtifactKinds.NpcFinishCoreManifest);
        ProtocolInvocation result = await RunCliAsync(root, "npc", "finish", "verify", "--protocol", "2", "--json",
            "--manifest", manifest.Path.Value, "--manifest-sha256", manifest.Sha256,
            "--verification-output", Child(root, name + ".json").Value, "--workflow-bundle", workflow.Value,
            "--workflow-bundle-sha256", HashFile(workflow), "--workflow-output", Child(root, "workflow", name + ".json").Value);
        RequireSucceeded(result, "npc finish verify");
        Require(result.Root.GetProperty("result").GetProperty("verified").GetBoolean(), "Reviewed Finish failed independent verification.");
    }

    private sealed class FinishManifestLeaseProbeServices(WorkspacePath finishManifest) :
        IPackageVerifyService,
        IPackageArchiveService
    {
        internal bool VerifyCalled { get; private set; }

        internal bool VerifyWriteOpened { get; private set; }

        internal bool ArchiveCalled { get; private set; }

        internal bool ArchiveWriteOpened { get; private set; }

        public ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalled = true;
            VerifyWriteOpened = CanOpenForWrite();
            return ValueTask.FromResult(new PackageVerifyResult(true, null, []));
        }

        public ValueTask<PackageArchiveResult> ArchiveAsync(
            PackageArchiveRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveCalled = true;
            ArchiveWriteOpened = CanOpenForWrite();
            return ValueTask.FromResult(new PackageArchiveResult(true, null, []));
        }

        public ValueTask<RuntimeArchiveVerificationResult> VerifyRuntimeAsync(
            RuntimeArchiveVerificationRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private bool CanOpenForWrite()
        {
            try
            {
                using FileStream ignored = File.Open(
                    finishManifest.Value,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private sealed class FinishPackageNoopBuildService : IPackageBuildService
    {
        public ValueTask<PackageBuildResult> BuildAsync(
            PackageBuildRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FinishPackageNoopInspectService : IPackageInspectService
    {
        public ValueTask<PackageInspectResult> InspectAsync(
            PackageInspectRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FinishVerificationSuccessService : ISkyrimNpcFinishCoreService
    {
        public ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
            WorkspacePath manifestPath,
            Sha256Hash manifestSha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimNpcFinishCoreVerificationResult(
                true,
                new SkyrimNpcFinishCoreVerification { Verified = true },
                []));
        }

        public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateAnalyzeAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            WorkspacePath proposalPath,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateApplyAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal,
            Sha256Hash proposalSha256,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            WorkspacePath proposalPath,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal,
            Sha256Hash proposalSha256,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
