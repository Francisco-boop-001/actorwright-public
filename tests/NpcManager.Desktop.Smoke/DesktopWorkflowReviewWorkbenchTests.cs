using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NpcManager.Application;
using NpcManager.Desktop;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop.Smoke;

internal static class DesktopWorkflowReviewWorkbenchTests
{
    private static readonly JsonSerializerOptions FixtureJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Run()
    {
        string repository = FindRepositoryRoot();
        string owner = Path.Combine(repository, "artifacts", "tests");
        string root = Path.Combine(
            owner,
            $"desktop-workflow-review-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ProveStartupTable(root);
            ProveReviewAndReceiptTable(root);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ProveStartupTable(string root)
    {
        var labRoot = new WorkspacePath(root);
        string bundle = Path.Combine(root, "workflow.json");
        const string hash =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        DesktopStartupRequestSelection selected =
            DesktopStartupRequestOptions.Resolve(
                ["--workflow-bundle", bundle,
                 "--workflow-bundle-sha256", hash],
                labRoot);
        Assert(selected.Error is null &&
               selected.WorkflowReview ==
                   new DesktopWorkflowLaunchBinding(
                       new WorkspacePath(bundle), hash) &&
               selected.RequestFile is null,
            "exact paired workflow startup mode was not admitted");

        var cases = new (string Name, string[] Arguments)[]
        {
            ("path only", ["--workflow-bundle", bundle]),
            ("hash only", ["--workflow-bundle-sha256", hash]),
            ("lowercase hash", ["--workflow-bundle", bundle,
                "--workflow-bundle-sha256", hash.ToLowerInvariant()]),
            ("duplicate", ["--workflow-bundle", bundle,
                "--workflow-bundle", bundle,
                "--workflow-bundle-sha256", hash]),
            ("mixed modes", ["--workflow-bundle", bundle,
                "--workflow-bundle-sha256", hash,
                "--race-menu-request", Path.Combine(root, "request.json"),
                "--race-menu-request-sha256", hash])
        };
        foreach ((string name, string[] arguments) in cases)
        {
            DesktopStartupRequestSelection refusal =
                DesktopStartupRequestOptions.Resolve(arguments, labRoot);
            Assert(refusal.Error is not null &&
                   refusal.WorkflowReview is null &&
                   refusal.RequestFile is null,
                $"startup parser admitted {name}");
        }
    }

    private static void ProveReviewAndReceiptTable(string root)
    {
        var labRoot = new WorkspacePath(root);
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var codec = new AgentWorkflowBundleCodec(policy, labRoot);
        byte[] png = CreatePng();
        string contact = Write(root, "contact.png", png);
        var views = ImmutableArray.CreateBuilder<NpcVisualPreviewView>();
        foreach (string id in
                 NpcVisualPreviewPersistenceContract.RequiredViewIds)
        {
            string imagePath = Write(root, id + ".png", png);
            string mask = Write(root, id + "-roles.png", png);
            views.Add(new NpcVisualPreviewView(
                id,
                new WorkspacePath(imagePath),
                new Sha256Hash(Hash(png)),
                new WorkspacePath(mask),
                new Sha256Hash(Hash(png)),
                2,
                2));
        }

        byte[] faceGeomBytes = "retained-face-geometry"u8.ToArray();
        string faceGeomPath = Write(
            root,
            Path.Combine("preview-source", "00000800.nif"),
            faceGeomBytes);
        var faceGeomAssetPath = new AssetPath(
            "meshes/actors/character/FaceGenData/FaceGeom/ReviewNpc.esp/00000800.nif");
        var identity = new SkyrimMainWorkspaceIdentity(
            new PluginName("ReviewNpc.esp"),
            new PluginName("ReviewNpc.esp"),
            new FormId(0x800),
            "NPC_");
        var source = new NpcVisualSourceGraph(
            NpcVisualPreviewRoute.Cbbe3Ba,
            identity,
            NpcSex.Female,
            50,
            "NordRace",
            null,
            null,
            [new NpcVisualAsset(
                NpcVisualAssetRole.FaceGeom,
                faceGeomAssetPath,
                "ReviewNpc.esp",
                new Sha256Hash(Hash(faceGeomBytes)),
                faceGeomBytes.LongLength,
                new WorkspacePath(faceGeomPath),
                true,
                [])],
            [],
            false,
            []);

        byte[] statusBytes = "{\"status\":\"PASS\"}"u8.ToArray();
        string status = Write(root, "render-status.json", statusBytes);
        var renderEvidence = new NpcVisualPreviewRenderEvidence(
            "4.3.0",
            "BLENDER_EEVEE_NEXT",
            1,
            true,
            0,
            [],
            0,
            ImmutableDictionary<string, int>.Empty.Add(
                "materialCount", 1),
            ImmutableDictionary<string, long>.Empty.Add(
                "face-front:FaceGeom", 4),
            ImmutableDictionary<string, double>.Empty,
            [new NpcVisualPreviewImportedMesh(
                NpcVisualAssetRole.FaceGeom,
                faceGeomAssetPath,
                "FaceGeom",
                3,
                ["Skin"],
                [1d, 0d, 0d, 0d,
                 0d, 1d, 0d, 0d,
                 0d, 0d, 1d, 0d,
                 0d, 0d, 0d, 1d])],
            [new NpcVisualPreviewImportedMaterial(
                "FaceGeom",
                "Skin",
                ImmutableDictionary<string, string>.Empty,
                [],
                "OPAQUE",
                false)],
            new WorkspacePath(status),
            new Sha256Hash(Hash(statusBytes)));
        var previewDocument = new NpcVisualPreviewPersistenceDocument(
            NpcVisualPreviewPersistenceContract.BundleSchema,
            NpcVisualPreviewPersistenceContract.SceneSchema,
            NpcVisualPreviewPersistenceContract.OffEngineLabel,
            false,
            source,
            views.ToImmutable(),
            new WorkspacePath(contact),
            new Sha256Hash(Hash(png)),
            renderEvidence,
            new NpcVisualPreviewVisualEvidence(
                1, 0.99, 478, 31, true, []),
            []);
        byte[] previewBytes = JsonSerializer.SerializeToUtf8Bytes(
            previewDocument,
            FixtureJsonOptions);
        string previewPath = Write(
            root,
            "npc-preview-bundle.json",
            previewBytes);

        byte[] outputPluginBytes = "output-bytes"u8.ToArray();
        _ = Write(
            root,
            Path.Combine("Data", "ReviewNpc.esp"),
            outputPluginBytes);
        byte[] packageBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            edition = "skyrimse",
            presetFormat = "racemenu-jslot",
            sourcePreset = "ReviewNpc.jslot",
            sourcePresetSha256 = Hash("preset"u8),
            sourcePlugin = "Skyrim.esm",
            sourcePluginSha256 = Hash("plugin"u8),
            outputPlugin = "ReviewNpc.esp",
            targetFormId = "00000800",
            artifacts = new[]
            {
                new
                {
                    kind = "plugin",
                    relativePath = "Data/ReviewNpc.esp",
                    byteLength = outputPluginBytes.LongLength,
                    sha256 = Hash(outputPluginBytes)
                }
            }
        }, FixtureJsonOptions);
        string packagePath = Write(
            root,
            "npcmanager-package.json",
            packageBytes);
        string requestDigest = Hash("review-request"u8);
        WorkflowArtifactBinding package = Binding(
            WorkflowArtifactKinds.NpcPackageManifest,
            "npcmanager-package/1",
            packagePath,
            packageBytes,
            requestDigest);
        WorkflowArtifactBinding preview = Binding(
            WorkflowArtifactKinds.NpcPreviewManifest,
            NpcVisualPreviewPersistenceContract.BundleSchema,
            previewPath,
            previewBytes,
            requestDigest);
        WorkflowNpcIdentity workflowNpc = new(
            "ReviewNpc", "Review NPC", "ReviewNpc.esp", "00000800");
        AgentWorkflowBundleDocument document = WriteBundle(
            codec,
            root,
            "workflow.json",
            AgentWorkflowPhase.Review,
            workflowNpc,
            requestDigest,
            [package, preview]);
        var launch = new DesktopWorkflowLaunchBinding(
            document.Path,
            document.Sha256);
        var service = new DesktopWorkflowReviewService(policy, labRoot);
        DesktopWorkflowReviewLoadResult loaded = service.LoadAsync(
                launch,
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        Assert(loaded.Loaded && loaded.Snapshot is not null,
            "exact producer-shaped Review bundle did not load");
        DesktopWorkflowReviewSnapshot snapshot = loaded.Snapshot!;
        Assert(snapshot.NpcEditorId == "ReviewNpc" &&
               snapshot.Game == "skyrimse" &&
               snapshot.Phase == AgentWorkflowPhase.Review &&
               snapshot.BundleSha256 == document.Sha256 &&
               snapshot.Artifacts.Length == 2 &&
               snapshot.Authority.Length == 8 &&
               snapshot.NextActions is [var next] &&
               next.Command == "gui" &&
               !next.RequiredBindings.IsDefault &&
               !next.MissingPrerequisites.IsDefault &&
               next.MissingPrerequisites.Contains("--workflow-bundle") &&
               snapshot.Proposal.Changes.Any(change =>
                   change.Contains(
                       "ReviewNpc.esp",
                       StringComparison.Ordinal)) &&
               snapshot.Preview.Views.Length == 6 &&
               snapshot.Preview.ContactSheetSha256 == Hash(png) &&
               snapshot.Preview.ContactSheetBytes.AsSpan().SequenceEqual(png),
            "review projection omitted identity, authority, actions, bindings, proposal, or preview evidence");
        ImageSource? decodedImage = OnLoadImageSource.Load(
            snapshot.Preview.ContactSheetBytes);
        Assert(decodedImage is BitmapSource { IsFrozen: true },
            "immutable preview bytes did not decode eagerly");
        AssertRequiredAuthority(snapshot, "loading the review");

        ProveStrictAdmissionRefusals(
            root,
            codec,
            service,
            previewDocument,
            package,
            workflowNpc,
            requestDigest);
        ProveViewModelReceiptCommands(
            root,
            service,
            launch,
            snapshot);
        ProvePublicationMutationRefusal(
            root,
            policy,
            labRoot,
            launch,
            contact,
            png);

        string occupied = Write(
            root,
            "occupied-receipt.json",
            "occupied"u8.ToArray());
        DesktopWorkflowReviewReceiptResult overwrite =
            service.CreateReceiptAsync(
                    launch,
                    ReviewOutcome.Accepted,
                    null,
                    new WorkspacePath(occupied),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        Assert(!overwrite.Created &&
               File.ReadAllText(occupied) == "occupied",
            "receipt creation overwrote an occupied output");

        string stalePreviewOutput = Path.Combine(
            root,
            "stale-preview-receipt.json");
        File.WriteAllBytes(contact, CreatePng(blue: true));
        DesktopWorkflowReviewReceiptResult stalePreview =
            service.CreateReceiptAsync(
                    launch,
                    ReviewOutcome.Accepted,
                    null,
                    new WorkspacePath(stalePreviewOutput),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        Assert(!stalePreview.Created &&
               !File.Exists(stalePreviewOutput),
            "mutated transitive preview bytes produced a receipt");
        File.WriteAllBytes(contact, png);

        string staleProposalOutput = Path.Combine(
            root,
            "stale-proposal-receipt.json");
        File.AppendAllText(packagePath, " ");
        DesktopWorkflowReviewReceiptResult staleProposal =
            service.CreateReceiptAsync(
                    launch,
                    ReviewOutcome.Accepted,
                    null,
                    new WorkspacePath(staleProposalOutput),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        Assert(!staleProposal.Created &&
               !File.Exists(staleProposalOutput),
            "mutated package proposal bytes produced a receipt");
    }

    private static void ProveStrictAdmissionRefusals(
        string root,
        AgentWorkflowBundleCodec codec,
        DesktopWorkflowReviewService service,
        NpcVisualPreviewPersistenceDocument previewDocument,
        WorkflowArtifactBinding package,
        WorkflowNpcIdentity workflowNpc,
        string requestDigest)
    {
        var wrongIdentity = new SkyrimMainWorkspaceIdentity(
            new PluginName("WrongNpc.esp"),
            new PluginName("WrongNpc.esp"),
            new FormId(0x801),
            "NPC_");
        byte[] mismatchBytes = JsonSerializer.SerializeToUtf8Bytes(
            previewDocument with
            {
                Source = previewDocument.Source with
                {
                    Identity = wrongIdentity
                }
            },
            FixtureJsonOptions);
        string mismatchPath = Write(
            root,
            "identity-mismatch-preview.json",
            mismatchBytes);
        WorkflowArtifactBinding mismatch = Binding(
            WorkflowArtifactKinds.NpcPreviewManifest,
            NpcVisualPreviewPersistenceContract.BundleSchema,
            mismatchPath,
            mismatchBytes,
            requestDigest);
        AgentWorkflowBundleDocument mismatchBundle = WriteBundle(
            codec,
            root,
            "identity-mismatch-workflow.json",
            AgentWorkflowPhase.Review,
            workflowNpc,
            requestDigest,
            [package, mismatch]);
        AssertRefused(
            service,
            mismatchBundle,
            "preview/package/workflow identity mismatch");

        byte[] skeletalBytes = JsonSerializer.SerializeToUtf8Bytes(
            previewDocument with
            {
                RenderEvidence = previewDocument.RenderEvidence with
                {
                    MaterialApplicationCounts =
                        ImmutableDictionary<string, int>.Empty,
                    RoleMaskPixelCounts =
                        ImmutableDictionary<string, long>.Empty,
                    Meshes = [],
                    Materials = []
                }
            },
            FixtureJsonOptions);
        string skeletalPath = Write(
            root,
            "skeletal-preview.json",
            skeletalBytes);
        WorkflowArtifactBinding skeletal = Binding(
            WorkflowArtifactKinds.NpcPreviewManifest,
            NpcVisualPreviewPersistenceContract.BundleSchema,
            skeletalPath,
            skeletalBytes,
            requestDigest);
        AgentWorkflowBundleDocument skeletalBundle = WriteBundle(
            codec,
            root,
            "skeletal-workflow.json",
            AgentWorkflowPhase.Review,
            workflowNpc,
            requestDigest,
            [package, skeletal]);
        AssertRefused(service, skeletalBundle, "skeletal preview evidence");

        byte[] extraBytes = "extra"u8.ToArray();
        string extraPath = Write(root, "extra-review-artifact.json", extraBytes);
        WorkflowArtifactBinding extra = Binding(
            WorkflowArtifactKinds.NpcBuildPreflight,
            "npc-build-preflight/1",
            extraPath,
            extraBytes,
            requestDigest);
        AgentWorkflowBundleDocument extraBundle = WriteBundle(
            codec,
            root,
            "extra-artifact-workflow.json",
            AgentWorkflowPhase.Review,
            workflowNpc,
            requestDigest,
            [package, BindingFromDocument(previewDocument, root, requestDigest),
             extra]);
        AssertRefused(service, extraBundle, "Review artifact superset");

        AgentWorkflowBundleDocument wrongPhase = WriteBundle(
            codec,
            root,
            "wrong-phase-workflow.json",
            AgentWorkflowPhase.Discover,
            workflowNpc,
            requestDigest,
            [package, BindingFromDocument(previewDocument, root, requestDigest)]);
        AssertRefused(service, wrongPhase, "non-Review phase");
    }

    private static WorkflowArtifactBinding BindingFromDocument(
        NpcVisualPreviewPersistenceDocument document,
        string root,
        string requestDigest)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            FixtureJsonOptions);
        string path = Write(
            root,
            $"rebound-preview-{Guid.NewGuid():N}.json",
            bytes);
        return Binding(
            WorkflowArtifactKinds.NpcPreviewManifest,
            NpcVisualPreviewPersistenceContract.BundleSchema,
            path,
            bytes,
            requestDigest);
    }

    private static void AssertRefused(
        DesktopWorkflowReviewService service,
        AgentWorkflowBundleDocument document,
        string role)
    {
        DesktopWorkflowReviewLoadResult result = service.LoadAsync(
                new DesktopWorkflowLaunchBinding(
                    document.Path,
                    document.Sha256),
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        Assert(!result.Loaded && result.Snapshot is null,
            $"Desktop review admitted {role}");
    }

    private static void ProveViewModelReceiptCommands(
        string root,
        DesktopWorkflowReviewService service,
        DesktopWorkflowLaunchBinding launch,
        DesktopWorkflowReviewSnapshot expected)
    {
        foreach (ReviewOutcome outcome in Enum.GetValues<ReviewOutcome>())
        {
            var viewModel = new DesktopWorkflowReviewViewModel(service);
            viewModel.LoadAsync(launch).GetAwaiter().GetResult();
            Assert(viewModel.IsLoaded &&
                   viewModel.PreviewImage is BitmapSource { IsFrozen: true } &&
                   viewModel.Authority.Length == 8 &&
                   viewModel.ProposalChanges.SequenceEqual(
                       expected.Proposal.Changes),
                "review ViewModel did not project the trusted service snapshot");
            string outputPath = Path.Combine(
                root,
                outcome.ToString().ToLowerInvariant() + "-receipt.json");
            viewModel.ReceiptOutput = outputPath;
            viewModel.ReviewerNote = "Reviewed exact evidence.";
            ICommand command = outcome switch
            {
                ReviewOutcome.Accepted => viewModel.AcceptCommand,
                ReviewOutcome.Rejected => viewModel.RejectCommand,
                ReviewOutcome.RevisionRequested =>
                    viewModel.RequestRevisionCommand,
                _ => throw new InvalidOperationException(
                    $"Unexpected review outcome '{outcome}'.")
            };
            Assert(command.CanExecute(null),
                $"{outcome} ViewModel command was unavailable");
            command.Execute(null);
            Assert(SpinWait.SpinUntil(
                    () => !viewModel.IsBusy &&
                          viewModel.ReceiptOutcome.StartsWith(
                              $"{outcome}:",
                              StringComparison.Ordinal) &&
                          File.Exists(outputPath),
                    TimeSpan.FromSeconds(10)),
                $"{outcome} ViewModel command did not complete");
            AssertRequiredAuthority(
                viewModel.Snapshot!,
                $"the {outcome} ViewModel command");

            var output = new WorkspacePath(outputPath);
            string outputSha256 = Hash(File.ReadAllBytes(outputPath));
            DesktopWorkflowReviewReceiptResult reopened =
                service.ReopenReceiptAsync(
                        launch,
                        output,
                        outputSha256,
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            Assert(reopened.Created && reopened.Outcome == outcome,
                $"{outcome} ViewModel receipt did not reopen exactly");
            AssertRequiredAuthority(
                viewModel.Snapshot!,
                $"reopening the {outcome} receipt");
        }
    }

    private static void ProvePublicationMutationRefusal(
        string root,
        KOnlyWorkspacePolicy policy,
        WorkspacePath labRoot,
        DesktopWorkflowLaunchBinding launch,
        string contact,
        byte[] original)
    {
        string displaced = Path.Combine(root, "contact-displaced.png");
        string replacement = Write(
            root,
            "contact-replacement.png",
            CreatePng(blue: true));
        string output = Path.Combine(root, "publication-swap-receipt.json");
        bool swapped = false;
        var service = new DesktopWorkflowReviewService(
            policy,
            labRoot,
            () =>
            {
                File.Move(contact, displaced);
                try
                {
                    File.Move(replacement, contact);
                    swapped = true;
                }
                catch
                {
                    File.Move(displaced, contact);
                    throw;
                }
            });
        try
        {
            DesktopWorkflowReviewReceiptResult result =
                service.CreateReceiptAsync(
                        launch,
                        ReviewOutcome.Accepted,
                        null,
                        new WorkspacePath(output),
                        CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            Assert(!result.Created && !File.Exists(output),
                "a coordinated preview swap during publication produced a receipt");
        }
        finally
        {
            if (swapped)
            {
                if (File.Exists(contact))
                    File.Delete(contact);
                if (File.Exists(displaced))
                    File.Move(displaced, contact);
            }
            else if (!File.Exists(contact) && File.Exists(displaced))
            {
                File.Move(displaced, contact);
            }
            if (File.Exists(replacement))
                File.Delete(replacement);
            Assert(File.Exists(contact) &&
                   File.ReadAllBytes(contact).AsSpan().SequenceEqual(original),
                "publication swap fixture did not restore exact preview bytes");
        }
    }

    private static void AssertRequiredAuthority(
        DesktopWorkflowReviewSnapshot snapshot,
        string action)
    {
        Assert(Required(
                   snapshot,
                   AgentAuthorityKind.HumanVisualAcceptance) &&
               Required(
                   snapshot,
                   AgentAuthorityKind.GameRuntimeVerification) &&
               Required(
                   snapshot,
                   AgentAuthorityKind.PromotionApproval),
            $"{action} granted visual, runtime, or promotion authority");
    }

    private static bool Required(
        DesktopWorkflowReviewSnapshot snapshot,
        AgentAuthorityKind kind) => snapshot.Authority.Single(item =>
            item.Kind == kind).State == AgentAuthorityState.Required;

    private static AgentWorkflowBundleDocument WriteBundle(
        AgentWorkflowBundleCodec codec,
        string root,
        string leaf,
        AgentWorkflowPhase phase,
        WorkflowNpcIdentity npc,
        string requestDigest,
        ImmutableArray<WorkflowArtifactBinding> artifacts)
    {
        var bundle = new AgentWorkflowBundle(
            AgentWorkflowSchemas.BundleV1,
            AgentWorkflowSchemas.SkyrimJslotFollowerWorkflowV1,
            GameEdition.SkyrimSpecialEdition,
            npc,
            phase,
            requestDigest,
            artifacts,
            [],
            []);
        return codec.WriteNew(
            bundle,
            new WorkspacePath(Path.Combine(root, leaf)));
    }

    private static WorkflowArtifactBinding Binding(
        string kind,
        string schema,
        string path,
        byte[] bytes,
        string requestDigest) => new(
            kind,
            schema,
            new WorkspacePath(path),
            bytes.LongLength,
            Hash(bytes),
            kind == WorkflowArtifactKinds.NpcPreviewManifest
                ? "preview npc"
                : kind == WorkflowArtifactKinds.NpcPackageManifest
                    ? "npc create-from-jslot"
                    : "npc assembly preflight",
            requestDigest,
            []);

    private static string Write(string root, string leaf, byte[] bytes)
    {
        string path = Path.Combine(root, leaf);
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException(
                "The fixture file must have a parent directory.");
        Directory.CreateDirectory(parent);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] CreatePng(bool blue = false)
    {
        byte[] pixels = blue
            ? [255, 0, 0, 255, 255, 0, 0, 255,
               255, 0, 0, 255, 255, 0, 0, 255]
            : [0, 0, 255, 255, 0, 0, 255, 255,
               0, 0, 255, 255, 0, 0, 255, 255];
        BitmapSource source = BitmapSource.Create(
            2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static string FindRepositoryRoot()
    {
        string? current = Directory.GetCurrentDirectory();
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "Actorwright.sln")))
                return current;
            current = Path.GetDirectoryName(current);
        }
        throw new DirectoryNotFoundException(
            "Actorwright repository root was not found.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
