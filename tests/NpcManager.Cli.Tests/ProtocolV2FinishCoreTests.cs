using System.Buffers.Binary;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Architecture.Tests;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    internal static async Task RunProtocolV2FinishCoreAsync()
    {
        SkyrimFinishMasterFixture.EnsureAvailable();
        string repository = Environment.CurrentDirectory;
        var root = new WorkspacePath(Path.Combine(repository, "artifacts", "test-work", "v2-finish-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        Console.WriteLine("V2 Finish fixture: " + root.Value);
        await AssertFinishReadAncestryAsync(root);
        foreach (string command in new[] { "version", "capabilities" })
            Require((await RunCliAsync(root, command, "--protocol", "2", "--json")).ExitCode == 0, "Exact CLI discovery failed.");
        foreach (string command in new[] { "workspace preflight", "preset inspect", "npc create-from-jslot", "npc finish analyze", "npc finish apply", "npc finish verify" })
            Require((await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", command)).ExitCode == 0,
                "Exact command discovery failed for " + command);
        WorkspacePath workflow = await CreateV2FinishSourceAsync(repository, root);
        SkyrimNpcFinishCoreRequest request = CreateV2FinishRequest(root);
        var requestPath = Child(root, "finish-request.json");
        byte[] canonical = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, root);
        File.WriteAllBytes(requestPath.Value, ReformatFinishDocument(canonical));
        string canonicalRequestHash = SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, root).Value.ToUpperInvariant();
        Require(HashFile(requestPath) != canonicalRequestHash, "The transport fixture must differ from canonical bytes.");
        var proposalPath = Child(root, "finish-proposal.json");
        var analyzedWorkflow = Child(root, "workflow", "finish-analyzed.json");
        await AssertV2FinishInputRefusalsAsync(root, workflow, requestPath);
        var analyzed = await RunCliAsync(root, "npc", "finish", "analyze", "--protocol", "2", "--json",
            "--request", requestPath.Value, "--request-sha256", HashFile(requestPath), "--proposal", proposalPath.Value, "--validate-all", "false",
            "--workflow-bundle", workflow.Value, "--workflow-bundle-sha256", HashFile(workflow),
            "--workflow-output", analyzedWorkflow.Value);
        Require(analyzed.ExitCode == 0, "Actual V2 Finish analyze must run without a receipt: " + analyzed.Root + analyzed.StdErr);
        Require(analyzed.Root.GetProperty("result").GetProperty("schemaId").GetString() == AgentProtocolSchemaIds.FinishAnalyzeResult &&
                analyzed.Root.GetProperty("result").GetProperty("status").GetString() == "readyForReviewedWrite",
            "V2 analyze did not produce a ready proposal.");
        RequireUnreviewedFinish(analyzed, analyzedWorkflow, requirePromotionNotApplicable: true);
        string proposalBeforeReplay = HashFile(proposalPath);
        string workflowBeforeReplay = HashFile(analyzedWorkflow);
        var replay = await RunCliAsync(root, "npc", "finish", "analyze", "--protocol", "2", "--json",
            "--request", requestPath.Value, "--request-sha256", canonicalRequestHash, "--proposal", proposalPath.Value,
            "--workflow-bundle", workflow.Value, "--workflow-bundle-sha256", HashFile(workflow),
            "--workflow-output", analyzedWorkflow.Value);
        Require(replay.ExitCode != 0 && HashFile(proposalPath) == proposalBeforeReplay && HashFile(analyzedWorkflow) == workflowBeforeReplay,
            "Fresh-output refusal must retain exact existing artifacts.");
        var appliedWorkflow = Child(root, "workflow", "finish-applied.json");
        var applied = await RunCliAsync(root, "npc", "finish", "apply", "--protocol", "2", "--json",
            "--request", requestPath.Value, "--request-sha256", canonicalRequestHash,
            "--proposal", proposalPath.Value, "--proposal-sha256", analyzed.Root.GetProperty("result").GetProperty("proposalSha256").GetString()!,
            "--workflow-bundle", analyzedWorkflow.Value, "--workflow-bundle-sha256", HashFile(analyzedWorkflow),
            "--workflow-output", appliedWorkflow.Value);
        Require(applied.ExitCode == 0 && applied.Root.GetProperty("result").GetProperty("applied").GetBoolean() &&
                applied.Root.GetProperty("result").GetProperty("schemaId").GetString() == AgentProtocolSchemaIds.FinishApplyResult,
            "Actual V2 Finish apply must run without a receipt: " + applied.Root + applied.StdErr);
        RequireUnreviewedFinish(applied, appliedWorkflow, requirePromotionNotApplicable: true);
        var manifest = Child(root, "finished", "NPCManager", "Evidence", "finish-core-manifest.json");
        await AssertVerifyRejectsAnalyzedWorkflowAsync(
            root,
            analyzedWorkflow,
            manifest);
        var verifiedWorkflow = Child(root, "workflow", "finish-verified.json");
        var verified = await RunCliAsync(root, "npc", "finish", "verify", "--protocol", "2", "--json",
            "--manifest", manifest.Value, "--manifest-sha256", HashFile(manifest),
            "--verification-output", Child(root, "finish-verification.json").Value,
            "--workflow-bundle", appliedWorkflow.Value, "--workflow-bundle-sha256", HashFile(appliedWorkflow),
            "--workflow-output", verifiedWorkflow.Value);
        Require(verified.ExitCode == 0 && verified.Root.GetProperty("result").GetProperty("verified").GetBoolean(),
            "Actual V2 Finish verify failed: " + verified.Root + verified.StdErr);
        RequireUnreviewedFinish(verified, verifiedWorkflow, requirePromotionNotApplicable: false);
        await AssertApplyRejectsVerifiedWorkflowAsync(
            root,
            verifiedWorkflow,
            requestPath,
            canonicalRequestHash,
            proposalPath,
            analyzed.Root.GetProperty("result").GetProperty("proposalSha256").GetString()!,
            manifest);
        var genericArchive = Child(root, "receipt-free-finish.zip");
        ProtocolInvocation verifiedPackage = await RunCliAsync(root, "package", "verify", "--json",
            "--manifest", Child(root, "finished", "npcmanager-package.json").Value,
            "--workflow-bundle", appliedWorkflow.Value,
            "--workflow-bundle-sha256", HashFile(appliedWorkflow));
        Require(verifiedPackage.ExitCode == 0 && verifiedPackage.Root.GetProperty("verified").GetBoolean(),
            "Exact receipt-free Finish workflow must verify its package: " + verifiedPackage.Root + verifiedPackage.StdErr);
        ProtocolInvocation archivedPackage = await RunCliAsync(root, "package", "archive", "--json",
            "--source-root", Child(root, "finished").Value, "--output", genericArchive.Value,
            "--workflow-bundle", appliedWorkflow.Value,
            "--workflow-bundle-sha256", HashFile(appliedWorkflow));
        Require(archivedPackage.ExitCode == 0 && File.Exists(genericArchive.Value),
            "Exact receipt-free Finish workflow must publish a fresh archive: " + archivedPackage.Root + archivedPackage.StdErr);
        string archiveSha256 = HashFile(genericArchive);
        ProtocolInvocation secondArchive = await RunCliAsync(root, "package", "archive", "--json",
            "--source-root", Child(root, "finished").Value, "--output", genericArchive.Value,
            "--workflow-bundle", appliedWorkflow.Value,
            "--workflow-bundle-sha256", HashFile(appliedWorkflow));
        Require(secondArchive.ExitCode != 0 && HashFile(genericArchive) == archiveSha256,
            "A second receipt-free Finish archive request overwrote the published archive.");
        var wrongHashArchive = Child(root, "wrong-workflow-hash.zip");
        ProtocolInvocation wrongHash = await RunCliAsync(root, "package", "archive", "--json",
            "--source-root", Child(root, "finished").Value, "--output", wrongHashArchive.Value,
            "--workflow-bundle", appliedWorkflow.Value,
            "--workflow-bundle-sha256", new string('A', 64));
        Require(wrongHash.ExitCode != 0 && !File.Exists(wrongHashArchive.Value),
            "A changed Finish workflow hash published an archive.");
        ProtocolInvocation archiveWithoutWorkflow = await RunCliAsync(root, "package", "archive", "--json",
            "--source-root", Child(root, "finished").Value, "--output", Child(root, "missing-workflow.zip").Value);
        Require(archiveWithoutWorkflow.ExitCode != 0 &&
                (archiveWithoutWorkflow.Root.ToString() + archiveWithoutWorkflow.StdErr).Contains("workflow-human-review-required", StringComparison.Ordinal),
            "The production package archive path admitted a workflow-free Finish output.");
        ProtocolInvocation verifyWithoutWorkflow = await RunCliAsync(root, "package", "verify", "--json",
            "--manifest", Child(root, "finished", "npcmanager-package.json").Value);
        Require(verifyWithoutWorkflow.ExitCode != 0 &&
                (verifyWithoutWorkflow.Root.ToString() + verifyWithoutWorkflow.StdErr).Contains("workflow-human-review-required", StringComparison.Ordinal),
            "The production package verify path admitted a workflow-free Finish manifest.");
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(root.Value + "-protected"));
        var lifecycle = new AgentWorkflowBundleTransitionService(new AgentWorkflowBundleCodec(policy, root));
        bool packageRefused = false;
        try { lifecycle.LoadForCommand(verifiedWorkflow, HashFile(verifiedWorkflow), "package archive"); }
        catch (AgentWorkflowCodecException exception) when (exception.Code == "workflow-transition-command-mismatch") { packageRefused = true; }
        Require(packageRefused, "The unreviewed lifecycle must not admit an Exchange packaging transition.");
        bool ambiguousFinishRefused = false;
        try { lifecycle.LoadForCommand(analyzedWorkflow, HashFile(analyzedWorkflow), "npc finish apply"); }
        catch (AgentWorkflowCodecException exception) when (exception.Code == "workflow-transition-command-mismatch") { ambiguousFinishRefused = true; }
        Require(ambiguousFinishRefused,
            "The generic workflow loader must refuse a command selected from an ambiguous multi-action bundle.");
        Require(string.Equals(HashFile(request.Source.PluginPath ?? throw new InvalidOperationException("Fixture plugin path absent.")),
                (request.Source.PluginSha256 ?? throw new InvalidOperationException("Fixture plugin hash absent.")).Value,
                StringComparison.OrdinalIgnoreCase) &&
                SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(request.Source.PackageRoot ??
                    throw new InvalidOperationException("Fixture package path absent.")) == request.Source.PackageTreeSha256,
            "V2 Finish changed the actual creation source.");
        Console.WriteLine("Actual V2 workspace→inspect→preflight→create→Finish analyze/apply/verify/package passed without a receipt; workflow and authority gates preserved.");
    }

    private static async Task AssertFinishReadAncestryAsync(WorkspacePath root)
    {
        var bound = Child(root, "ancestry-bound");
        var target = Child(root, "ancestry-outside");
        Directory.CreateDirectory(bound.Value);
        Directory.CreateDirectory(target.Value);
        var targetFile = Child(target, "document.json");
        File.WriteAllText(targetFile.Value, "{}");
        var link = Child(bound, "linked");
        if (!PhysicalReparseFixture.TryCreateDirectoryLink(
                link.Value,
                target.Value,
                root.Value))
            return;
        var linkedFile = Child(link, "document.json");
        try
        {
            bool readerRefused = false;
            try { await new SkyrimNpcFinishCoreCommandDocumentReader(bound).ReadBoundFileAsync(linkedFile,
                new Sha256Hash(HashFile(targetFile)), bytes => bytes, CancellationToken.None); }
            catch (InvalidDataException exception) when (exception.Message.Contains(ProtocolV2DiagnosticCodes.ReparsePointRefused, StringComparison.Ordinal))
            { readerRefused = true; }
            Require(readerRefused, "The shared V1/V2 reader must refuse ancestor traversal before reading document bytes.");
            using var lockedOutside = new FileStream(targetFile.Value, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            ProtocolCommandResult refused = await ProtocolV2GoldenWorkflowComposition.Create(bound)
                .Single(adapter => adapter.Commands.Contains("npc finish analyze", StringComparer.Ordinal))
                .RunAsync(CommandLine.Parse(["npc", "finish", "analyze", "--protocol", "2", "--json",
                    "--request", linkedFile.Value, "--request-sha256", new string('A', 64),
                    "--proposal", Child(bound, "proposal.json").Value, "--workflow-bundle", linkedFile.Value,
                    "--workflow-bundle-sha256", new string('A', 64), "--workflow-output", Child(bound, "successor.json").Value]),
                    new string('B', 64), CancellationToken.None);
            using (refused.TerminalArtifactLease)
                Require(refused.Diagnostics.Any(row => row.Message.Contains(ProtocolV2DiagnosticCodes.ReparsePointRefused, StringComparison.Ordinal)),
                    "Finish input pinning must reject ancestry before opening the locked outside file.");
        }
        finally { Directory.Delete(link.Value); }
    }

    private static async Task AssertV2FinishInputRefusalsAsync(WorkspacePath root, WorkspacePath workflow, WorkspacePath request)
    {
        var malformed = Child(root, "malformed-finish-request.json");
        File.WriteAllText(malformed.Value, "{}");
        foreach (string variant in new[] { "partial-receipt", "unbound-receipt", "malformed-request" })
        {
            WorkspacePath input = variant == "malformed-request" ? malformed : request;
            WorkspacePath proposal = Child(root, variant + "-proposal.json");
            WorkspacePath successor = Child(root, "workflow", variant + ".json");
            string[] options = ["npc", "finish", "analyze", "--protocol", "2", "--json",
                "--request", input.Value, "--request-sha256", HashFile(input), "--proposal", proposal.Value,
                "--workflow-bundle", workflow.Value, "--workflow-bundle-sha256", HashFile(workflow), "--workflow-output", successor.Value];
            if (variant != "malformed-request") options = [.. options, "--review-receipt", request.Value];
            if (variant == "unbound-receipt") options = [.. options, "--review-receipt-sha256", HashFile(request)];
            ProtocolInvocation refused = await RunCliAsync(root, options);
            Require(refused.ExitCode != 0 && !File.Exists(proposal.Value) && !File.Exists(successor.Value) &&
                !Directory.Exists(Child(root, "finished").Value), "Finish must refuse " + variant + " before publication.");
        }
        var validationProposal = Child(root, "validation-proposal.json");
        var validationWorkflow = Child(root, "workflow", "validation.json");
        ProtocolInvocation validation = await RunCliAsync(root, "npc", "finish", "analyze", "--protocol", "2", "--json",
            "--request", request.Value, "--request-sha256", HashFile(request), "--proposal", validationProposal.Value,
            "--validate-all", "1", "--workflow-bundle", workflow.Value, "--workflow-bundle-sha256", HashFile(workflow),
            "--workflow-output", validationWorkflow.Value);
        Require(validation.ExitCode == 0 && validation.Root.GetProperty("result").GetProperty("validation").GetProperty("valid").GetBoolean() &&
            !validation.Root.GetProperty("artifacts").EnumerateArray().Any() && !File.Exists(validationProposal.Value) &&
            !File.Exists(validationWorkflow.Value) && !Directory.Exists(Child(root, "finished").Value) &&
            !validation.Root.GetProperty("effects").EnumerateArray().Any(row =>
                row.GetProperty("kind").GetString() == "writeNewArtifact" && row.GetProperty("status").GetString() == "completed"),
            "validate-all must perform actual nonpromoting validation without workflow or proposal publication: " + validation.Root);
    }

    private static async Task AssertVerifyRejectsAnalyzedWorkflowAsync(
        WorkspacePath root,
        WorkspacePath workflow,
        WorkspacePath manifest)
    {
        WorkspacePath verification = Child(root, "wrong-transition-verification.json");
        WorkspacePath successor = Child(root, "workflow", "wrong-verify-transition.json");
        ProtocolInvocation refused = await RunCliAsync(
            root,
            "npc", "finish", "verify", "--protocol", "2", "--json",
            "--manifest", manifest.Value,
            "--manifest-sha256", HashFile(manifest),
            "--verification-output", verification.Value,
            "--workflow-bundle", workflow.Value,
            "--workflow-bundle-sha256", HashFile(workflow),
            "--workflow-output", successor.Value);
        string response = refused.Root + refused.StdErr;
        Require(refused.ExitCode != 0 &&
                response.Contains("workflow-transition-command-mismatch", StringComparison.Ordinal) &&
                !File.Exists(verification.Value) &&
                !File.Exists(successor.Value),
            "The real CLI admitted npc finish verify from the analyzed workflow: " + response);
    }

    private static async Task AssertApplyRejectsVerifiedWorkflowAsync(
        WorkspacePath root,
        WorkspacePath workflow,
        WorkspacePath request,
        string requestSha256,
        WorkspacePath proposal,
        string proposalSha256,
        WorkspacePath manifest)
    {
        WorkspacePath successor = Child(root, "workflow", "wrong-apply-transition.json");
        string archiveBefore = HashFile(Child(root, "finished.zip"));
        string manifestBefore = HashFile(manifest);
        ProtocolInvocation refused = await RunCliAsync(
            root,
            "npc", "finish", "apply", "--protocol", "2", "--json",
            "--request", request.Value,
            "--request-sha256", requestSha256,
            "--proposal", proposal.Value,
            "--proposal-sha256", proposalSha256,
            "--workflow-bundle", workflow.Value,
            "--workflow-bundle-sha256", HashFile(workflow),
            "--workflow-output", successor.Value);
        string response = refused.Root + refused.StdErr;
        Require(refused.ExitCode != 0 &&
                response.Contains("workflow-transition-command-mismatch", StringComparison.Ordinal) &&
                !File.Exists(successor.Value) &&
                HashFile(Child(root, "finished.zip")) == archiveBefore &&
                HashFile(manifest) == manifestBefore,
            "The real CLI admitted npc finish apply from the verified workflow or changed existing output: " + response);
    }

    private static void RequireUnreviewedFinish(ProtocolInvocation invocation, WorkspacePath workflow, bool requirePromotionNotApplicable)
    {
        JsonElement[] authority = invocation.Root.GetProperty("authority").EnumerateArray().ToArray();
        Require(authority.Single(row => row.GetProperty("kind").GetString() == "humanVisualAcceptance")
                    .GetProperty("state").GetString() == "required", "A receipt-free Finish command claimed human acceptance.");
        if (requirePromotionNotApplicable)
            Require(authority.Single(row => row.GetProperty("kind").GetString() == "promotionApproval")
                    .GetProperty("state").GetString() == "notApplicable", "Finish analyze/apply must not grant promotion authority.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(workflow.Value));
        Require(document.RootElement.GetProperty("phase").GetString() == "review-required" &&
                !document.RootElement.GetProperty("artifacts").EnumerateArray().Any(row => row.GetProperty("kind").GetString() == "review-receipt") &&
                invocation.Root.GetProperty("nextActions").EnumerateArray().Any(row => row.GetProperty("command").GetString() == "gui"),
            "Receipt-free Finish lost its review-required phase or real GUI recovery action.");
    }

    private static async Task<WorkspacePath> CreateV2FinishSourceAsync(string repository, WorkspacePath root)
    {
        string catalog = Path.Combine(repository, "tools", "release", "protocol-v2-workflow-probes", "catalog.json");
        foreach (string command in new[] { "workspace preflight", "preset inspect", "npc create-from-jslot" })
            MaterializeProbeFixtures(repository, catalog, command, root);
        var preset = Child(root, "npc-preflight", "fixture.jslot");
        PrepareGoldenPresetFixture(preset);
        var request = Child(root, "npc-preflight", "request.json");
        CorrectNpcRequestFixture(repository, root, request, HashFile(preset));
        var order = WriteCanonicalLoadOrder(root);
        Directory.CreateDirectory(Child(root, "workflow").Value);
        Directory.CreateDirectory(Child(root, "evidence").Value);
        var workspace = Child(root, "workflow", "workspace.json");
        RequireSucceeded(await RunCliAsync(root, "workspace", "preflight", "--protocol", "2", "--json",
            "--game", "skyrimse", "--workspace-root", root.Value, "--data-root", Child(root, "Data").Value,
            "--output-root", Child(root, "reserved-output").Value, "--load-order", order.Value,
            "--intake-output", Child(root, "evidence", "intake.json").Value, "--npc-editor-id", "ActorwrightResumptionNpc",
            "--workflow-output", workspace.Value), "workspace preflight");
        var inspected = Child(root, "workflow", "inspected.json");
        RequireSucceeded(await RunCliAsync(root, "preset", "inspect", "--protocol", "2", "--json",
            "--format", "racemenu-jslot", "--edition", "skyrimse", "--input", preset.Value, "--input-sha256", HashFile(preset),
            "--inspection-output", Child(root, "evidence", "inspection.json").Value,
            "--workflow-bundle", workspace.Value, "--workflow-bundle-sha256", HashFile(workspace), "--workflow-output", inspected.Value), "preset inspect");
        var preflight = Child(root, "evidence", "preflight.json");
        var preflightWorkflow = Child(root, "workflow", "preflight.json");
        string[] creation = ["npc", "create-from-jslot", "--protocol", "2", "--json",
            "--request", request.Value, "--request-sha256", HashFile(request), "--preset", preset.Value, "--preset-sha256", HashFile(preset),
            "--data-root", Child(root, "Data").Value, "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
            "--companion-root", Child(root, "companion").Value];
        RequireSucceeded(await RunCliAsync(root, [.. creation, "--preflight-output", preflight.Value,
            "--workflow-bundle", inspected.Value, "--workflow-bundle-sha256", HashFile(inspected),
            "--workflow-output", preflightWorkflow.Value]), "npc create-from-jslot");
        var built = Child(root, "workflow", "created.json");
        RequireSucceeded(await RunCliAsync(root, [.. creation, "--reviewed-preflight", preflight.Value, "--reviewed-preflight-sha256", HashFile(preflight),
            "--workflow-bundle", preflightWorkflow.Value, "--workflow-bundle-sha256", HashFile(preflightWorkflow),
            "--workflow-output", built.Value]), "npc create-from-jslot");
        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(root.Value + "-protected"));
        var codec = new AgentWorkflowBundleCodec(policy, root);
        AgentWorkflowBundleDocument created = codec.Load(built, HashFile(built));
        Require(FormId.TryParse(created.Bundle.Npc.LocalFormId!, out FormId local), "Created NPC identity is not a FormID.");
        // Existing reviewed Finish bundles use FormId.ToString's 0x-prefixed spelling.
        return new AgentWorkflowBundleTransitionService(codec).WriteInitial(
            created.Bundle.Npc with { LocalFormId = local.ToString() }, created.Bundle.RequestDigest,
            created.Bundle.Artifacts, Child(root, "workflow", "created-prefixed-id.json")).Document.Path;
    }

    private static SkyrimNpcFinishCoreRequest CreateV2FinishRequest(WorkspacePath root)
    {
        var package = Child(root, "planned-npc-output");
        var manifest = Child(package, "npcmanager-package.json");
        var pluginPath = Child(package, "Data", "PackagedNpc.esp");
        var key = ModKey.FromNameAndExtension("PackagedNpc.esp");
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(new ModPath(key, new FilePath(pluginPath.Value)), SkyrimRelease.SkyrimSE);
        INpcGetter npc = plugin.Npcs.Single();
        var copiedMaster = Child(root, "authority", "Skyrim.esm");
        SkyrimFinishMasterFixture.AppendCanonicalPackGroupToMaster(
            Child(root, "Data", "Skyrim.esm").Value,
            copiedMaster.Value,
            root.Value);
        return new SkyrimNpcFinishCoreRequest
        {
            Schema = SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier,
            Source = new() { PackageRoot = package, PackageManifest = manifest, PackageManifestSha256 = new Sha256Hash(HashFile(manifest)),
                PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(package), PluginPath = pluginPath,
                Plugin = new PluginName(key.ToString()), PluginSha256 = new Sha256Hash(HashFile(pluginPath)) },
            Actor = new() { EditorId = new EditorId(npc.EditorID!), FormId = new FormId(npc.FormKey.ID) },
            Authorities = new() { BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                Providers = [new() { Plugin = new PluginName(key.ToString()), Path = pluginPath, Sha256 = new Sha256Hash(HashFile(pluginPath)), ByteLength = new FileInfo(pluginPath.Value).Length }] },
            AiPolicy = new() { Aggression = SkyrimNpcFinishCoreAggression.Unaggressive, Confidence = SkyrimNpcFinishCoreConfidence.Brave,
                Energy = 50, Morality = SkyrimNpcFinishCoreMorality.NoCrime, Assistance = SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies, Mood = SkyrimNpcFinishCoreMood.Neutral },
            CombatPolicy = new() { SeedLocalStyle = true, Profile = SkyrimNpcFinishCoreCombatProfile.RangedFirst },
            OutfitPolicy = new() { Policy = SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit, ArmorItems = [new FormReference(new PluginName("Skyrim.esm"), new FormId(0x900))] },
            SandboxAuthority = new() { CopiedMaster = copiedMaster, CopiedMasterSha256 = new Sha256Hash(HashFile(copiedMaster)),
                Template = new FormReference(new PluginName("Skyrim.esm"), new FormId(0x1B217)), TemplateEditorId = "DefaultSandboxEditorLocation512" },
            Output = new() { Root = Child(root, "finished"), Archive = Child(root, "finished.zip"), PluginFileName = key.ToString() }
        };
    }
}
