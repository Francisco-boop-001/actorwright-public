using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class AgentWorkflowContractTests
{
    private const string HashA =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string HashC =
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";

    private sealed record WorkflowTransitionCase(
        string Name,
        WorkflowArtifactBinding[] Artifacts,
        AgentReviewReceipt? Receipt,
        string? ReceiptSha256,
        string? BundleSha256,
        WorkflowEvaluationOutcome Outcome,
        AgentWorkflowPhase Phase,
        string? Command,
        ProtocolNextActionBinding[] RequiredBindings,
        string[] MissingPrerequisites,
        bool RequiresHumanAction,
        string? DiagnosticCode);

    public static void Run()
    {
        DerivesOnlyLegalGoldenWorkflowTransitions();
        PreservesImmutableBundleBindings();
        PublishesTheClosedGoldenArtifactKindSet();
        RefusesDuplicateSingularArtifactKinds();
        RefusesUndefinedEnumValues();
        RefusesPathlessWorkspacePathsStructurally();
        RefusesInvalidBundleBindings();
        RefusesMalformedUtf16ReviewerNotes();
        ProvesAstralReviewerNoteScalarBoundary();
        NormalizesAndValidatesReviewReceipts();
        PersistsStrictCanonicalWorkflowBundles();
    }

    private static void PersistsStrictCanonicalWorkflowBundles()
    {
        string testOwner = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "tests");
        string root = Path.Combine(
            testOwner,
            $"agent-workflow-codec-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var labRoot = new WorkspacePath(root);
            var codec = new AgentWorkflowBundleCodec(
                new KOnlyWorkspacePolicy(
                    labRoot,
                    new WorkspacePath(@"F:\ExampleGame")),
                labRoot);
            string artifactPath = Path.Combine(root, "artifact.bin");
            byte[] artifactBytes = "bound artifact bytes"u8.ToArray();
            File.WriteAllBytes(artifactPath, artifactBytes);
            string artifactHash = Hash(artifactBytes);
            AgentWorkflowBundle bundle = GoldenBundle(
                new WorkflowArtifactBinding(
                    WorkflowArtifactKinds.NpcBuildPreflight,
                    "application/octet-stream",
                    new WorkspacePath(artifactPath),
                    artifactBytes.Length,
                    artifactHash,
                    "npc assembly preflight",
                    HashB,
                    []));

            byte[] canonical = codec.ComputeCanonicalBytes(bundle);
            Assert(
                !(canonical.Length >= 3 &&
                  canonical[0] == 0xEF &&
                  canonical[1] == 0xBB &&
                  canonical[2] == 0xBF) &&
                Array.IndexOf(canonical, (byte)'\r') < 0 &&
                Array.IndexOf(canonical, (byte)'\n') >= 0,
                "canonical workflow JSON was not BOM-free UTF-8 with LF indentation");
            string outputPath = Path.Combine(root, "workflow.json");
            AgentWorkflowBundleDocument written = codec.WriteNew(
                bundle,
                new WorkspacePath(outputPath));
            Assert(
                written.Size == canonical.Length &&
                written.Sha256 == Hash(canonical) &&
                File.ReadAllBytes(outputPath).AsSpan().SequenceEqual(canonical),
                "fresh workflow bundle write did not preserve canonical bytes and binding");
            AgentWorkflowBundleDocument loaded = codec.Load(
                new WorkspacePath(outputPath),
                written.Sha256);
            Assert(
                loaded.Bundle.Schema == bundle.Schema &&
                loaded.Bundle.WorkflowKind == bundle.WorkflowKind &&
                loaded.Bundle.Game == bundle.Game &&
                loaded.Bundle.Npc == bundle.Npc &&
                loaded.Bundle.Phase == bundle.Phase &&
                loaded.Bundle.RequestDigest == bundle.RequestDigest &&
                loaded.Bundle.Artifacts.Length == 1 &&
                loaded.Bundle.Artifacts[0].Path == bundle.Artifacts[0].Path &&
                loaded.Bundle.Artifacts[0].Sha256 == bundle.Artifacts[0].Sha256 &&
                loaded.Utf8Json.AsSpan().SequenceEqual(canonical),
                "strict workflow bundle round trip lost its exact contract");

            const int largeArtifactSize = (4 * 1024 * 1024) + (64 * 1024);
            string largeArtifactPath = Path.Combine(root, "package.zip");
            byte[] largeArtifactBytes =
                GC.AllocateUninitializedArray<byte>(largeArtifactSize);
            for (int index = 0; index < largeArtifactBytes.Length; index++)
                largeArtifactBytes[index] = (byte)(index % 251);
            File.WriteAllBytes(largeArtifactPath, largeArtifactBytes);
            string largeArtifactHash = Hash(largeArtifactBytes);
            AgentWorkflowBundle largeArtifactBundle = GoldenBundle(
                new WorkflowArtifactBinding(
                    WorkflowArtifactKinds.PackageArchive,
                    "application/zip",
                    new WorkspacePath(largeArtifactPath),
                    largeArtifactBytes.LongLength,
                    largeArtifactHash,
                    "package build",
                    HashB,
                    []));
            byte[] largeArtifactBundleBytes =
                codec.ComputeCanonicalBytes(largeArtifactBundle);
            Assert(
                largeArtifactBundleBytes.Length < 4 * 1024 * 1024,
                "large-artifact workflow bundle fixture exceeded the document cap");
            string largeArtifactBundlePath = Path.Combine(
                root,
                "large-artifact-workflow.json");
            File.WriteAllBytes(
                largeArtifactBundlePath,
                largeArtifactBundleBytes);
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            AgentWorkflowBundleDocument largeArtifactLoaded = codec.Load(
                new WorkspacePath(largeArtifactBundlePath),
                Hash(largeArtifactBundleBytes));
            long verificationAllocations =
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert(
                largeArtifactLoaded.Bundle.Artifacts.Single().Sha256 ==
                largeArtifactHash,
                "valid physical artifact over 4 MiB was rejected");
            Assert(
                verificationAllocations < largeArtifactSize / 2,
                $"large physical artifact verification allocated {verificationAllocations} bytes instead of streaming");

            AssertCodecRefused(
                () => codec.Load(new WorkspacePath(outputPath), HashA),
                "workflow-hash-mismatch",
                "wrong expected workflow digest was accepted");
            AssertCodecRefused(
                () => codec.Load(
                    new WorkspacePath(outputPath),
                    written.Sha256.ToLowerInvariant()),
                "workflow-hash-invalid",
                "lowercase expected workflow digest was accepted");
            AssertCodecRefused(
                () => codec.Load(new WorkspacePath(outputPath), "ABC"),
                "workflow-hash-invalid",
                "short expected workflow digest was accepted");
            AssertCodecRefused(
                () => codec.Load(
                    new WorkspacePath(@"C:\ActorwrightOutside\workflow.json"),
                    HashA),
                "workflow-path-outside-lab",
                "outside-K workflow input was accepted");

            string missingParentOutput = Path.Combine(
                root,
                "missing-parent",
                "workflow.json");
            AssertCodecRefused(
                () => codec.WriteNew(
                    bundle,
                    new WorkspacePath(missingParentOutput)),
                "workflow-parent-missing",
                "workflow write created or accepted a missing parent");
            AssertCodecRefused(
                () => codec.WriteNew(
                    bundle,
                    new WorkspacePath(outputPath)),
                "workflow-output-exists",
                "workflow write overwrote an existing output");
            Assert(
                !Directory.EnumerateFileSystemEntries(
                        root,
                        "workflow.json.tmp-*",
                        SearchOption.TopDirectoryOnly)
                    .Any(),
                "failed workflow write left a sibling staging output");

            string directoryInput = Path.Combine(root, "directory-input");
            Directory.CreateDirectory(directoryInput);
            AssertCodecRefused(
                () => codec.Load(new WorkspacePath(directoryInput), HashA),
                "workflow-file-not-ordinary",
                "directory was accepted as a workflow bundle file");

            string oversizedPath = Path.Combine(root, "oversized.json");
            using (var oversized = new FileStream(
                       oversizedPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
                oversized.SetLength((4L * 1024L * 1024L) + 1L);
            AssertCodecRefused(
                () => codec.Load(new WorkspacePath(oversizedPath), HashA),
                "workflow-file-size-invalid",
                "workflow input over 4 MiB was accepted");

            string canonicalText = Encoding.UTF8.GetString(canonical);
            AssertMutatedDocumentRefused(
                codec,
                root,
                "unknown-property.json",
                canonicalText.Replace(
                    "{\n",
                    "{\n  \"unknown\": true,\n",
                    StringComparison.Ordinal),
                "workflow-json-unknown-property");
            AssertMutatedDocumentRefused(
                codec,
                root,
                "duplicate-property.json",
                canonicalText.Replace(
                    "{\n",
                    "{\n  \"schema\": \"actorwright.agent-workflow-bundle.v1\",\n",
                    StringComparison.Ordinal),
                "workflow-json-duplicate-property");
            AssertMutatedDocumentRefused(
                codec,
                root,
                "lowercase-artifact-hash.json",
                canonicalText.Replace(
                    artifactHash,
                    artifactHash.ToLowerInvariant(),
                    StringComparison.Ordinal),
                "workflow-contract-invalid");
            AssertMutatedDocumentRefused(
                codec,
                root,
                "short-artifact-hash.json",
                canonicalText.Replace(
                    artifactHash,
                    "ABC",
                    StringComparison.Ordinal),
                "workflow-contract-invalid");
            AssertMutatedDocumentRefused(
                codec,
                root,
                "noncanonical.json",
                canonicalText + "\n",
                "workflow-json-noncanonical");

            WorkflowArtifactBinding duplicate = bundle.Artifacts[0] with
            {
                Kind = WorkflowArtifactKinds.NpcPreviewManifest,
                Path = new WorkspacePath(Path.Combine(root, "second.bin"))
            };
            AssertCodecRefused(
                () => codec.ComputeCanonicalBytes(
                    bundle with
                    {
                        Artifacts = [bundle.Artifacts[0], duplicate]
                    }),
                "workflow-contract-invalid",
                "duplicate workflow artifact hash was accepted by canonical serialization");
            AssertCodecRefused(
                () => codec.ComputeCanonicalBytes(
                    bundle with
                    {
                        Artifacts =
                        [
                            bundle.Artifacts[0],
                            duplicate with
                            {
                                Kind = bundle.Artifacts[0].Kind,
                                Sha256 = HashC
                            }
                        ]
                    }),
                "workflow-contract-invalid",
                "duplicate singular workflow artifact kind was accepted by canonical serialization");

            string driftDocument = Path.Combine(root, "artifact-drift.json");
            File.WriteAllBytes(driftDocument, canonical);
            byte[] sameSizeDrift = artifactBytes.ToArray();
            sameSizeDrift[0] ^= 0x01;
            File.WriteAllBytes(artifactPath, sameSizeDrift);
            AssertCodecRefused(
                () => codec.Load(
                    new WorkspacePath(driftDocument),
                    Hash(canonical)),
                "workflow-artifact-binding-mismatch",
                "same-size physical artifact hash drift was accepted");
            File.WriteAllBytes(artifactPath, "changed artifact bytes"u8.ToArray());
            AssertCodecRefused(
                () => codec.Load(
                    new WorkspacePath(driftDocument),
                    Hash(canonical)),
                "workflow-artifact-binding-mismatch",
                "physical artifact size drift was accepted");

            ProveReparseTraversalRefused(codec, root, canonical);
        }
        finally
        {
            DeleteOwnedTestTree(root, testOwner);
        }
    }

    private static void ProveReparseTraversalRefused(
        AgentWorkflowBundleCodec codec,
        string root,
        byte[] canonical)
    {
        string target = Path.Combine(root, "reparse-target");
        string link = Path.Combine(root, "reparse-link");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "workflow.json"), canonical);
        if (!PhysicalReparseFixture.TryCreateDirectoryLink(
                link, target, root))
            return;
        try
        {
            AssertCodecRefused(
                () => codec.Load(
                    new WorkspacePath(Path.Combine(link, "workflow.json")),
                    Hash(canonical)),
                "workflow-reparse-refused",
                "workflow loader followed a reparse traversal");
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
        }
    }

    private static void AssertMutatedDocumentRefused(
        AgentWorkflowBundleCodec codec,
        string root,
        string fileName,
        string json,
        string expectedCode)
    {
        string path = Path.Combine(root, fileName);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(path, bytes);
        AssertCodecRefused(
            () => codec.Load(new WorkspacePath(path), Hash(bytes)),
            expectedCode,
            $"mutated workflow document '{fileName}' was accepted");
    }

    private static void AssertCodecRefused(
        Action action,
        string expectedCode,
        string message)
    {
        try
        {
            action();
        }
        catch (AgentWorkflowCodecException exception)
        {
            Assert(
                exception.Code == expectedCode,
                $"{message}; expected '{expectedCode}', observed '{exception.Code}'");
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Actorwright.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the Actorwright repository root for workflow codec tests.");
    }

    private static void DeleteOwnedTestTree(string root, string testOwner)
    {
        string expectedParent = Path.GetFullPath(testOwner);
        string admitted = Path.GetFullPath(root);
        if (!admitted.StartsWith(
                expectedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Refusing to clean a workflow codec test root outside its exact owner directory.");
        if (!Directory.Exists(admitted))
            return;
        foreach (string entry in Directory.EnumerateFileSystemEntries(admitted))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (attributes.HasFlag(FileAttributes.Directory))
                    Directory.Delete(entry);
                else
                    File.Delete(entry);
            }
            else if (attributes.HasFlag(FileAttributes.Directory))
            {
                DeleteOwnedTestTree(entry, testOwner);
            }
            else
            {
                File.Delete(entry);
            }
        }
        Directory.Delete(admitted);
    }

    private static void DerivesOnlyLegalGoldenWorkflowTransitions()
    {
        WorkflowArtifactBinding intake = Artifact(WorkflowArtifactKinds.ReviewedWorkspaceIntake, HashFor(10), "intake.json");
        WorkflowArtifactBinding preflight = Artifact(WorkflowArtifactKinds.NpcBuildPreflight, HashFor(11), "preflight.json");
        WorkflowArtifactBinding jslot = Artifact(WorkflowArtifactKinds.RaceMenuJslot, HashFor(12), "preset.jslot");
        WorkflowArtifactBinding package = Artifact(WorkflowArtifactKinds.NpcPackageManifest, HashFor(13), "package.json");
        WorkflowArtifactBinding preview = Artifact(WorkflowArtifactKinds.NpcPreviewManifest, HashFor(14), "preview.json");
        WorkflowArtifactBinding receiptArtifact = Artifact(
            WorkflowArtifactKinds.ReviewReceipt,
            HashFor(15),
            "review-receipt.json");
        WorkflowArtifactBinding finishRequest = Artifact(
            WorkflowArtifactKinds.NpcFinishCoreRequest,
            HashFor(16),
            "finish-request.json");
        WorkflowArtifactBinding finishManifest = Artifact(WorkflowArtifactKinds.NpcFinishCoreManifest, HashFor(17), "finish-manifest.json");
        WorkflowArtifactBinding finishVerification = Artifact(WorkflowArtifactKinds.NpcFinishCoreVerification, HashFor(18), "finish-verification.json");
        WorkflowArtifactBinding archive = Artifact(WorkflowArtifactKinds.PackageArchive, HashFor(19), "package.zip");
        WorkflowArtifactBinding runtime = Artifact(WorkflowArtifactKinds.RuntimeEvidence, HashFor(20), "runtime.json");
        string reviewedPredecessorSha256 = HashFor(30);
        AgentReviewReceipt acceptedPackageReview =
            AgentWorkflowContractValidation.CreateReviewReceipt(
                reviewedPredecessorSha256,
                package.Sha256,
                [package.Sha256, preview.Sha256],
                AgentReviewContract.FinishAnalyzeAuthorityNoticeSha256,
                ReviewOutcome.Accepted,
                AgentReviewContract.FinishAnalyzeScope,
                null);

        WorkflowTransitionCase[] cases =
        [
            new("discover requires reviewed intake inputs", [], null, null, null,
                WorkflowEvaluationOutcome.Blocked, AgentWorkflowPhase.Discover,
                "workspace preflight", [],
                ["--game", "--workspace-root", "--data-root", "--output-root",
                    "--load-order", "--intake-output", "--npc-editor-id",
                    "--workflow-output"], false,
                "workflow-next-action-prerequisites-missing"),
            new("preset input absent", [intake], null, null, null,
                WorkflowEvaluationOutcome.Blocked, AgentWorkflowPhase.Analyze,
                "preset inspect",
                [
                    new("--format", "racemenu-jslot", null),
                    new("--edition", "skyrimse", null)
                ],
                ["--input", "--input-sha256", "--inspection-output",
                    "--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"], false,
                "workflow-next-action-prerequisites-missing"),
            new("create preflight inputs absent", [intake, jslot], null,
                null, null, WorkflowEvaluationOutcome.Blocked,
                AgentWorkflowPhase.Analyze, "npc create-from-jslot",
                [
                    Bound("--preset", jslot),
                    HashBound("--preset-sha256", jslot)
                ],
                ["--request", "--request-sha256", "--data-root", "--plugins",
                    "--companion-root", "--preflight-output",
                    "--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"], false,
                "workflow-next-action-prerequisites-missing"),
            new("create execution remains exact after reviewed preflight",
                [intake, preflight, jslot], null, null, null,
                WorkflowEvaluationOutcome.Blocked,
                AgentWorkflowPhase.Apply, "npc create-from-jslot",
                [
                    Bound("--preset", jslot),
                    HashBound("--preset-sha256", jslot),
                    Bound("--reviewed-preflight", preflight),
                    HashBound("--reviewed-preflight-sha256", preflight)
                ],
                ["--request", "--request-sha256", "--data-root", "--plugins",
                    "--companion-root", "--workflow-bundle",
                    "--workflow-bundle-sha256", "--workflow-output"], false,
                "workflow-next-action-prerequisites-missing"),
            new("preview inputs absent", [intake, package], null, null, null,
                WorkflowEvaluationOutcome.Blocked, AgentWorkflowPhase.Verify,
                "preview npc",
                [
                    new("--plugin", "Test.esp", null),
                    new("--form", "0x00000800", null),
                    Bound("--package-manifest", package),
                    HashBound("--expected-package-sha256", package),
                    Bound("--intake", intake)
                ],
                ["--output-root"], false,
                "workflow-next-action-prerequisites-missing"),
            new("review receipt inputs absent", [package, preview], null, null, null,
                WorkflowEvaluationOutcome.Blocked, AgentWorkflowPhase.Review,
                "gui",
                [
                    Bound("--proposal", package),
                    HashBound("--proposal-sha256", package),
                    Bound("--preview-manifest", preview),
                    HashBound("--preview-manifest-sha256", preview)
                ],
                ["--outcome", "--operator-attestation", "--receipt-output",
                    "--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"],
                true, "workflow-next-action-prerequisites-missing"),
            new("accepted package review awaits external Finish request",
                [package, preview, receiptArtifact],
                acceptedPackageReview,
                receiptArtifact.Sha256,
                reviewedPredecessorSha256,
                WorkflowEvaluationOutcome.Blocked,
                AgentWorkflowPhase.Analyze,
                "npc finish analyze",
                [
                    Bound("--review-receipt", receiptArtifact),
                    HashBound("--review-receipt-sha256", receiptArtifact)
                ],
                ["--request", "--request-sha256", "--proposal",
                    "--workflow-bundle", "--workflow-bundle-sha256",
                    "--workflow-output"],
                false,
                "workflow-next-action-prerequisites-missing"),
            new("Finish request is not part of the first reviewed successor",
                [package, preview, finishRequest, receiptArtifact],
                acceptedPackageReview,
                receiptArtifact.Sha256,
                reviewedPredecessorSha256,
                WorkflowEvaluationOutcome.Blocked,
                AgentWorkflowPhase.Discover,
                null,
                [],
                [],
                false,
                "workflow-transition-invalid"),
            new("finish verify exact", [finishManifest], null, null, null,
                WorkflowEvaluationOutcome.Blocked, AgentWorkflowPhase.Verify,
                "npc finish verify",
                [
                    Bound("--manifest", finishManifest),
                    HashBound("--manifest-sha256", finishManifest)
                ],
                ["--verification-output", "--workflow-bundle",
                    "--workflow-bundle-sha256", "--workflow-output"],
                false, "workflow-next-action-prerequisites-missing"),
            new("runtime inputs absent", [finishVerification, archive], null,
                null, null, WorkflowEvaluationOutcome.Blocked,
                AgentWorkflowPhase.RuntimeAcceptance, "runtime smoke verify",
                [new("--edition", "skyrimse", null)],
                ["--runtime-report", "--package-acceptance"], true,
                "workflow-next-action-prerequisites-missing"),
            new("generic runtime bytes remain unverified",
                [finishVerification, archive, runtime], null, null, null,
                WorkflowEvaluationOutcome.Blocked,
                AgentWorkflowPhase.RuntimeAcceptance, "runtime smoke verify",
                [
                    new("--edition", "skyrimse", null),
                    Bound("--runtime-report", runtime)
                ],
                ["--package-acceptance"], true,
                "workflow-next-action-prerequisites-missing"),
            new("extra artifact invalidates exact signature",
                [finishManifest, archive], null, null, null,
                WorkflowEvaluationOutcome.Blocked, AgentWorkflowPhase.Discover,
                null, [], [], false, "workflow-transition-invalid"),
        ];

        foreach (WorkflowTransitionCase testCase in cases)
        {
            AgentWorkflowBundle bundle = Bundle(testCase.Artifacts);
            WorkflowEvaluation evaluation = AgentWorkflowService.Evaluate(
                new WorkflowEvaluationInput(
                    bundle,
                    Verified(testCase.Artifacts),
                    testCase.Receipt,
                    testCase.ReceiptSha256,
                    testCase.BundleSha256));
            Assert(
                evaluation.Outcome == testCase.Outcome &&
                evaluation.Phase == testCase.Phase &&
                evaluation.Diagnostics.Any() == (testCase.DiagnosticCode is not null) &&
                (testCase.DiagnosticCode is null || evaluation.Diagnostics.Any(item =>
                    item.Code == testCase.DiagnosticCode &&
                    item.Class == DiagnosticClass.Validation &&
                    item.Severity == DiagnosticSeverity.Error)),
                $"golden transition '{testCase.Name}' was not derived exactly");
            if (testCase.Command is null)
            {
                Assert(evaluation.NextActions.IsEmpty,
                    $"blocked transition '{testCase.Name}' invented an action");
            }
            else
            {
                Assert(evaluation.NextActions.Length == 1,
                    $"transition '{testCase.Name}' did not emit exactly one action");
                ProtocolNextAction action = evaluation.NextActions[0];
                Assert(action.Command == testCase.Command &&
                       action.RequiredBindings.SequenceEqual(testCase.RequiredBindings) &&
                       action.MissingPrerequisites.SequenceEqual(testCase.MissingPrerequisites) &&
                       action.RequiresHumanAction == testCase.RequiresHumanAction,
                    $"transition '{testCase.Name}' emitted an unsupported or incomplete CLI action");
                Assert(evaluation.Outcome != WorkflowEvaluationOutcome.Ready ||
                       action.MissingPrerequisites.IsEmpty,
                    $"transition '{testCase.Name}' was ready with missing prerequisites");
            }
            AssertExternalAuthorityRequired(evaluation, testCase.Name);
        }

        AgentWorkflowBundle invalid = Bundle([intake]) with { Schema = "actorwright.agent-workflow-bundle.v2" };
        AssertBlocked(invalid, Verified(invalid.Artifacts), "workflow-input-invalid");
        ImmutableDictionary<string, VerifiedWorkflowArtifact> unverified = Verified([intake]);
        unverified = unverified.SetItem(intake.Kind, unverified[intake.Kind] with { IndependentlyVerified = false });
        AssertBlocked(Bundle([intake]), unverified, "workflow-artifact-unverified");
        ImmutableDictionary<string, VerifiedWorkflowArtifact> stale = Verified([intake]);
        stale = stale.SetItem(intake.Kind, stale[intake.Kind] with { Sha256 = HashB });
        AssertBlocked(Bundle([intake]), stale, "workflow-artifact-binding-stale");

        AgentWorkflowBundle Bundle(IEnumerable<WorkflowArtifactBinding> artifacts) =>
            new(
                AgentWorkflowSchemas.BundleV1,
                AgentWorkflowSchemas.SkyrimJslotFollowerWorkflowV1,
                GameEdition.SkyrimSpecialEdition,
                new WorkflowNpcIdentity("TestNpc", "Test NPC", "Test.esp", "0x00000800"),
                AgentWorkflowPhase.Discover,
                HashB,
                artifacts.ToImmutableArray(),
                [],
                []);

        static ImmutableDictionary<string, VerifiedWorkflowArtifact> Verified(
            IEnumerable<WorkflowArtifactBinding> artifacts) =>
            artifacts.ToImmutableDictionary(
                item => item.Kind,
                item => new VerifiedWorkflowArtifact(item.Kind, item.Path, item.Size, item.Sha256, true),
                StringComparer.Ordinal);

        static AgentAuthorityState Authority(WorkflowEvaluation evaluation, AgentAuthorityKind kind) =>
            evaluation.Authority.Single(item => item.Kind == kind).State;

        static void AssertExternalAuthorityRequired(WorkflowEvaluation evaluation, string name) =>
            Assert(
                Authority(evaluation, AgentAuthorityKind.HumanVisualAcceptance) == AgentAuthorityState.Required &&
                Authority(evaluation, AgentAuthorityKind.GameRuntimeVerification) == AgentAuthorityState.Required &&
                Authority(evaluation, AgentAuthorityKind.PromotionApproval) == AgentAuthorityState.Required,
                $"golden transition '{name}' invented external authority");

        static void AssertBlocked(
            AgentWorkflowBundle bundle,
            ImmutableDictionary<string, VerifiedWorkflowArtifact> verified,
            string diagnosticCode)
        {
            WorkflowEvaluation evaluation = AgentWorkflowService.Evaluate(
                new WorkflowEvaluationInput(bundle, verified, null, null, null));
            Assert(
                evaluation.Outcome == WorkflowEvaluationOutcome.Blocked &&
                evaluation.NextActions.IsEmpty &&
                evaluation.Diagnostics.Any(item =>
                    item.Code == diagnosticCode &&
                    item.Class == DiagnosticClass.Validation &&
                    item.Severity == DiagnosticSeverity.Error),
                $"expected blocked diagnostic '{diagnosticCode}'");
        }

        static ProtocolNextActionBinding Bound(
            string option,
            WorkflowArtifactBinding artifact) =>
            new(option, artifact.Path.Value, artifact.Sha256);

        static ProtocolNextActionBinding HashBound(
            string option,
            WorkflowArtifactBinding artifact) =>
            new(option, artifact.Sha256, artifact.Sha256);
    }

    private static void ProvesAstralReviewerNoteScalarBoundary()
    {
        const string astralScalar = "\U0001F642";
        Assert(
            astralScalar.Length == 2 &&
            char.IsSurrogatePair(astralScalar, 0),
            "astral reviewer-note fixture is not one supplementary-plane scalar");

        AgentWorkflowContractValidation.CreateReviewReceipt(
            HashA,
            HashB,
            [HashC],
            HashC,
            ReviewOutcome.Accepted,
            "preview-and-proposal",
            string.Concat(Enumerable.Repeat(astralScalar, 1024)));
        AssertRefused(
            () => AgentWorkflowContractValidation.CreateReviewReceipt(
                HashA,
                HashB,
                [HashC],
                HashC,
                ReviewOutcome.Accepted,
                "preview-and-proposal",
                string.Concat(Enumerable.Repeat(astralScalar, 1025))),
            "1025 astral Unicode scalar values were accepted");
    }

    private static void RefusesMalformedUtf16ReviewerNotes()
    {
        AgentReviewReceipt Receipt(string reviewerNote) =>
            AgentWorkflowContractValidation.CreateReviewReceipt(
                HashA,
                HashB,
                [HashC],
                HashC,
                ReviewOutcome.Accepted,
                "preview-and-proposal",
                reviewerNote);

        var cases = new (string Name, string Note)[]
        {
            ("unpaired high surrogate", "\uD83D"),
            ("unpaired low surrogate", "\uDE42")
        };
        string[] accepted = cases
            .Where(testCase => !IsRefused(() => Receipt(testCase.Note)))
            .Select(testCase => testCase.Name)
            .ToArray();
        Assert(
            accepted.Length == 0,
            $"ill-formed UTF-16 notes were accepted: {string.Join(", ", accepted)}");

        Receipt("line one\nline two");
        AssertRefused(
            () => Receipt("line one\rline two"),
            "carriage return in reviewer note was accepted");
    }

    private static void RefusesPathlessWorkspacePathsStructurally()
    {
        WorkflowArtifactBinding artifact = Artifact(
            WorkflowArtifactKinds.NpcBuildPreflight,
            HashA,
            "preflight.json");
        VerifiedWorkflowArtifact verified = new(
            WorkflowArtifactKinds.NpcBuildPreflight,
            artifact.Path,
            artifact.Size,
            artifact.Sha256,
            true);
        var cases = new (string Name, Action Validate)[]
        {
            (
                "workflow artifact",
                () => AgentWorkflowContractValidation.Validate(
                    GoldenBundle(
                        artifact with
                        {
                            Path = default
                        }))),
            (
                "verified workflow artifact",
                () => AgentWorkflowContractValidation.Validate(
                    verified with
                    {
                        Path = default
                    }))
        };

        string[] accepted = cases
            .Where(testCase => !IsRefused(testCase.Validate))
            .Select(testCase => testCase.Name)
            .ToArray();
        Assert(
            accepted.Length == 0,
            $"pathless workspace paths were accepted: {string.Join(", ", accepted)}");

        AgentWorkflowContractValidation.Validate(
            GoldenBundle(
                artifact with
                {
                    Path = new WorkspacePath(@"C:\ContractOnly\preflight.json")
                }));
    }

    private static void RefusesUndefinedEnumValues()
    {
        WorkflowArtifactBinding artifact = Artifact(
            WorkflowArtifactKinds.NpcBuildPreflight,
            HashA,
            "preflight.json");
        AgentWorkflowBundle bundle = GoldenBundle(artifact);
        WorkflowAuthorityEvidence authority = new(
            AgentAuthorityKind.InputAdmission,
            AgentAuthorityState.Established,
            "Reviewed input is bound.",
            [HashA]);
        AgentReviewReceipt receipt =
            AgentWorkflowContractValidation.CreateReviewReceipt(
                HashA,
                HashB,
                [HashC],
                HashC,
                ReviewOutcome.Accepted,
                "preview-and-proposal",
                null);
        var cases = new (string Name, Action Validate)[]
        {
            (
                "workflow phase",
                () => AgentWorkflowContractValidation.Validate(
                    bundle with
                    {
                        Phase = (AgentWorkflowPhase)int.MaxValue
                    })),
            (
                "authority kind",
                () => AgentWorkflowContractValidation.Validate(
                    bundle with
                    {
                        Authority =
                        [
                            authority with
                            {
                                Kind = (AgentAuthorityKind)int.MaxValue
                            }
                        ]
                    })),
            (
                "authority state",
                () => AgentWorkflowContractValidation.Validate(
                    bundle with
                    {
                        Authority =
                        [
                            authority with
                            {
                                State = (AgentAuthorityState)int.MaxValue
                            }
                        ]
                    })),
            (
                "review outcome",
                () => AgentWorkflowContractValidation.Validate(
                    receipt with
                    {
                        Outcome = (ReviewOutcome)int.MaxValue
                    }))
        };

        string[] accepted = cases
            .Where(testCase => !IsRefused(testCase.Validate))
            .Select(testCase => testCase.Name)
            .ToArray();
        Assert(
            accepted.Length == 0,
            $"undefined enum values were accepted: {string.Join(", ", accepted)}");
    }

    private static void RefusesDuplicateSingularArtifactKinds()
    {
        foreach (string kind in WorkflowArtifactKinds.All)
        {
            WorkflowArtifactBinding first = Artifact(
                kind,
                HashFor(1),
                "first.json");
            WorkflowArtifactBinding duplicate = Artifact(
                kind,
                HashFor(2),
                "second.json");

            AssertRefused(
                () => AgentWorkflowContractValidation.Validate(
                    GoldenBundle(first) with
                    {
                        Artifacts = [first, duplicate]
                    }),
                $"duplicate singular artifact kind '{kind}' was accepted");
        }
    }

    private static void PreservesImmutableBundleBindings()
    {
        var file = new WorkspacePath(@"K:\AgentWorkflowTests\preflight.json");
        var artifact = new WorkflowArtifactBinding(
            WorkflowArtifactKinds.NpcBuildPreflight,
            "npc.build-preflight.v1",
            file,
            123,
            HashA,
            "npc assembly preflight",
            HashB,
            [HashC]);
        var bundle = new AgentWorkflowBundle(
            AgentWorkflowSchemas.BundleV1,
            AgentWorkflowSchemas.SkyrimJslotFollowerWorkflowV1,
            GameEdition.SkyrimSpecialEdition,
            new WorkflowNpcIdentity("TestNpc", "Test NPC", "Test.esp", "000800"),
            AgentWorkflowPhase.Preflight,
            HashB,
            [artifact],
            [
                new WorkflowAuthorityEvidence(
                    AgentAuthorityKind.InputAdmission,
                    AgentAuthorityState.Established,
                    "Reviewed input is bound.",
                    [HashA])
            ],
            [
                new ProtocolNextAction(
                    "preset inspect",
                    "Inspect the admitted preset.",
                    [
                        new ProtocolNextActionBinding(
                            "--input-sha256",
                            HashA,
                            HashA)
                    ],
                    [],
                    false)
            ]);

        AgentWorkflowContractValidation.Validate(bundle);

        Assert(
            bundle.Schema == "actorwright.agent-workflow-bundle.v1",
            "wrong workflow bundle schema");
        Assert(
            bundle.Artifacts.Single().Sha256 == HashA,
            "artifact hash was not preserved");
        Assert(
            bundle.Artifacts.Single().Path == file,
            "artifact path was not preserved");
    }

    private static void PublishesTheClosedGoldenArtifactKindSet()
    {
        string[] expected =
        [
            "npc-build-preflight",
            "npc-finish-core-manifest",
            "npc-finish-core-proposal",
            "npc-finish-core-request",
            "npc-finish-core-verification",
            "npc-package-manifest",
            "npc-preview-manifest",
            "package-archive",
            "racemenu-jslot",
            "review-receipt",
            "reviewed-workspace-intake",
            "runtime-evidence"
        ];

        Assert(
            WorkflowArtifactKinds.All.SequenceEqual(expected),
            "golden artifact kinds are incomplete, duplicated, or not ordinally sorted");
    }

    private static void RefusesInvalidBundleBindings()
    {
        var artifact = new WorkflowArtifactBinding(
            WorkflowArtifactKinds.NpcBuildPreflight,
            "npc.build-preflight.v1",
            new WorkspacePath(@"K:\AgentWorkflowTests\preflight.json"),
            123,
            HashA,
            "npc assembly preflight",
            HashB,
            [HashC]);
        var bundle = GoldenBundle(artifact);

        AssertRefused(
            () => AgentWorkflowContractValidation.Validate(
                bundle with
                {
                    Artifacts =
                    [
                        artifact with { Sha256 = HashA.ToLowerInvariant() }
                    ]
                }),
            "lowercase artifact SHA-256 was accepted");
        AssertRefused(
            () => AgentWorkflowContractValidation.Validate(
                bundle with
                {
                    Artifacts =
                    [
                        artifact with
                        {
                            InputArtifactHashes = [HashC, HashA]
                        }
                    ]
                }),
            "unsorted input artifact hashes were accepted");
        AssertRefused(
            () => AgentWorkflowContractValidation.Validate(
                bundle with
                {
                    Artifacts =
                    [
                        artifact with { Kind = "replacement-document" }
                    ]
                }),
            "unknown replacement artifact kind was accepted");
        AssertRefused(
            () => AgentWorkflowContractValidation.Validate(
                bundle with { Schema = "actorwright.agent-workflow-bundle.v2" }),
            "wrong workflow schema was accepted");
    }

    private static void NormalizesAndValidatesReviewReceipts()
    {
        AgentReviewReceipt receipt =
            AgentWorkflowContractValidation.CreateReviewReceipt(
                HashA,
                HashB,
                [HashC, HashA],
                HashC,
                ReviewOutcome.Accepted,
                "preview-and-proposal",
                "Reviewed");

        Assert(
            receipt.Schema == "actorwright.review-receipt.v1",
            "wrong review receipt schema");
        Assert(
            receipt.AttestationKind == "operator-attested",
            "receipt attestation was not operator-attested");
        Assert(
            receipt.DisplayedArtifactHashes.SequenceEqual([HashA, HashC]),
            "displayed hashes were not normalized to ordinal order");
        AgentWorkflowContractValidation.Validate(receipt);

        AssertRefused(
            () => AgentWorkflowContractValidation.CreateReviewReceipt(
                HashA,
                HashB,
                [],
                HashC,
                ReviewOutcome.Accepted,
                "preview-and-proposal",
                null),
            "empty displayed hashes were accepted");
        AssertRefused(
            () => AgentWorkflowContractValidation.CreateReviewReceipt(
                HashA,
                HashB,
                [HashC, HashC],
                HashC,
                ReviewOutcome.Accepted,
                "preview-and-proposal",
                null),
            "duplicate displayed hashes were accepted");
        var forged = receipt with { AttestationKind = "self-attested" };
        AssertRefused(
            () => AgentWorkflowContractValidation.Validate(forged),
            "non-operator receipt attestation was accepted");
    }

    private static AgentWorkflowBundle GoldenBundle(
        WorkflowArtifactBinding artifact) =>
        new(
            AgentWorkflowSchemas.BundleV1,
            AgentWorkflowSchemas.SkyrimJslotFollowerWorkflowV1,
            GameEdition.SkyrimSpecialEdition,
            new WorkflowNpcIdentity("TestNpc", null, null, null),
            AgentWorkflowPhase.Preflight,
            HashB,
            [artifact],
            [],
            []);

    private static WorkflowArtifactBinding Artifact(
        string kind,
        string sha256,
        string fileName) =>
        new(
            kind,
            "application/json",
            new WorkspacePath(
                Path.Combine(@"K:\AgentWorkflowTests", fileName)),
            123,
            sha256,
            "test producer",
            HashB,
            [],
            kind == WorkflowArtifactKinds.NpcFinishCoreProposal
                ? sha256
                : null);

    private static string HashFor(int value) =>
        value.ToString("X").PadLeft(64, '0');

    private static void AssertRefused(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static bool IsRefused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
