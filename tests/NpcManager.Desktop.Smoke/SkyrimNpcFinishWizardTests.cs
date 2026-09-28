using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using System.IO;
using NpcManager.Desktop;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop.Smoke;

internal static class SkyrimNpcFinishWizardTests
{
    private static readonly FormReference ReviewedTargetRace =
        new(new PluginName("Skyrim.esm"), new FormId(0x0000_0901));

    public static void Run()
    {
        var service = new FakeService();
        var transaction = new SkyrimNpcFinishWizardTransaction(
            service,
            new WorkspacePath(@"K:\ExampleWorkspace"));
        using var viewModel = new SkyrimNpcFinishWizardViewModel(transaction);
        Assert(viewModel.Steps.SequenceEqual(
            ["Source", "Role and Outfit", "Routine", "Finish Core Review"]),
            "Finish Core desktop wizard steps drifted from the approved four-step workflow.");
        Assert(!viewModel.PlacementIncluded && !viewModel.RuntimeAuthority &&
               !viewModel.VisualAuthority,
            "Finish Core desktop wizard exposed placement/runtime/visual authority.");
        string xamlPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "src",
            "NpcManager.Desktop",
            "MainWindow.xaml");
        if (!File.Exists(xamlPath))
            xamlPath = Path.Combine(
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\src\NpcManager.Desktop",
                "MainWindow.xaml");
        string xaml = File.ReadAllText(xamlPath);
        Assert(xaml.Contains("Finish NPC", StringComparison.Ordinal) &&
               xaml.Contains("SkyrimNpcFinishWizardPanel", StringComparison.Ordinal),
            "The main desktop shell does not expose the Finish NPC workflow.");
    }

    public static async ValueTask RunExternalInstallAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "test-work",
            $"finish-wizard-external-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            RequestFixture external = await WriteRequestAsync(root, external: true);
            RequestFixture ordinary = await WriteRequestAsync(root, external: false);
            ReviewedGameIntake complete = ReviewedIntake(root, providerEnabled: true);
            ReviewedGameIntake disabled = ReviewedIntake(root, providerEnabled: false);
            ReviewedGameIntake incomplete = ReviewedIntake(
                root,
                providerEnabled: true,
                providerReadSucceeded: false);

            await AssertNoIntakeAsync(external, cancellationToken);
            await AssertMissingTargetRaceResolverAsync(external, complete, cancellationToken);
            await AssertVerifiedAsync(external, complete, cancellationToken);
            await AssertDisabledProviderAsync(external, disabled, cancellationToken);
            await AssertHashDriftAsync(external, complete, cancellationToken);
            await AssertMissingAssetAsync(external, complete, cancellationToken);
            await AssertVerifiedNotReadyAsync(external, complete, cancellationToken);
            await AssertClearedAndReplacedIntakeAsync(
                external,
                complete,
                incomplete,
                cancellationToken);
            await AssertOrdinaryAsync(ordinary, cancellationToken);
            AssertPanelPresentation();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async ValueTask AssertNoIntakeAsync(
        RequestFixture request,
        CancellationToken cancellationToken)
    {
        var service = new ExternalInstallFakeService
        {
            State = ExternalInstallDependencyState.DeclaredUnverified,
            CurrentHash = Hash('C'),
            ProviderEnabled = true
        };
        using var viewModel = CreateViewModel(service, request.ProjectRoot);
        viewModel.ApplyReviewedIntake(null);
        await viewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "proposal.json")),
            cancellationToken);
        Assert(service.ContextAnalyzeCalls == 0 && service.LegacyAnalyzeCalls == 1,
            "Desktop analysis called the install-context service without a complete reviewed intake.");
        Assert(viewModel.ExternalInstallDependencyState ==
                   ExternalInstallDependencyState.DeclaredUnverified &&
               viewModel.ExternalProviderCurrentHash ==
                   "Not checked this invocation" &&
               viewModel.ExternalProviderEnabledState ==
                   "Not checked this invocation" &&
               viewModel.ExternalHistoricalSnapshot == "None" &&
               !viewModel.CanApply,
            "Desktop no-intake projection exposed current external state or enabled Apply.");
        Assert(!viewModel.RuntimeAuthority && !viewModel.VisualAuthority,
            "Desktop no-intake analysis upgraded runtime or visual authority.");

        await viewModel.VerifyAsync(
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "no-intake-manifest.json")),
            Hash('E'),
            cancellationToken);
        Assert(service.ContextVerifyCalls == 0 && service.LegacyVerifyCalls == 1,
            "Desktop context-free verify did not stay on the legacy service surface.");
    }

    private static async ValueTask AssertMissingTargetRaceResolverAsync(
        RequestFixture request,
        ReviewedGameIntake intake,
        CancellationToken cancellationToken)
    {
        var service = new ExternalInstallFakeService
        {
            State = ExternalInstallDependencyState.Verified,
            CurrentHash = Hash('A'),
            ProviderEnabled = true
        };
        using var viewModel = new SkyrimNpcFinishWizardViewModel(
            new SkyrimNpcFinishWizardTransaction(service, request.ProjectRoot));
        viewModel.ApplyReviewedIntake(intake);
        await viewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "missing-race-proposal.json")),
            cancellationToken);
        Assert(service.ContextAnalyzeCalls == 0 && service.LegacyAnalyzeCalls == 1 &&
               !viewModel.HasCompleteReviewedIntake && !viewModel.CanApply &&
               viewModel.ExternalInstallDependencyState ==
                   ExternalInstallDependencyState.DeclaredUnverified,
            "Desktop established external install context without an exact target-race seam.");
    }

    private static async ValueTask AssertVerifiedAsync(
        RequestFixture request,
        ReviewedGameIntake intake,
        CancellationToken cancellationToken)
    {
        var service = new ExternalInstallFakeService
        {
            State = ExternalInstallDependencyState.Verified,
            CurrentHash = Hash('A'),
            ProviderEnabled = true
        };
        using var viewModel = CreateViewModel(service, request.ProjectRoot);
        viewModel.ApplyReviewedIntake(intake);
        SkyrimNpcFinishCoreProposalResult analyzed = await viewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "verified-proposal.json")),
            cancellationToken);
        Assert(service.ContextAnalyzeCalls == 1 && service.LegacyAnalyzeCalls == 0,
            "Desktop analysis did not use the contextful service for the complete reviewed order.");
        Assert(service.LastContext is not null &&
               service.LastContext.EnabledPluginOrder.Select(item => item.Value)
                   .SequenceEqual(["Skyrim.esm", "OrchidAdornment.esp"]),
            "Desktop context did not preserve the complete enabled reviewed order.");
        Assert(service.LastContext?.TargetRace == ReviewedTargetRace,
            "Desktop context did not preserve the exact reviewed target race.");
        Assert(viewModel.ExternalProviderName == "OrchidAdornment.esp" &&
               viewModel.ExternalProviderExpectedHash == Hash('A').Value &&
               viewModel.ExternalProviderCurrentHash == Hash('A').Value &&
               viewModel.ExternalProviderEnabledState == "Enabled" &&
               viewModel.ExternalDescriptorId == Hash('D').Value &&
               viewModel.ExternalInstallDependencyState ==
                   ExternalInstallDependencyState.Verified &&
               viewModel.ExternalInstallReady && viewModel.CanApply,
            "Desktop verified external dependency projection or Apply gate drifted.");
        Assert(viewModel.ExternalNextAction.Contains(
                    "Static Finish Core write is ready",
                    StringComparison.Ordinal) &&
               !viewModel.ExternalNextAction.Contains(request.ProjectRoot.Value,
                   StringComparison.OrdinalIgnoreCase),
            "Desktop next action was not typed and portable.");
        Assert(!viewModel.RuntimeAuthority && !viewModel.VisualAuthority,
            "Desktop verified external dependency upgraded runtime or visual authority.");

        SkyrimNpcFinishCoreApplyResult applied = await viewModel.ApplyAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "verified-proposal.json")),
            analyzed.ProposalSha256!.Value,
            cancellationToken);
        Assert(applied.Applied && service.ContextApplyCalls == 1 &&
               service.LegacyApplyCalls == 0,
            "Desktop verified Apply did not dispatch exactly once through the contextful service.");

        await viewModel.VerifyAsync(
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "manifest.json")),
            Hash('E'),
            cancellationToken);
        Assert(service.ContextVerifyCalls == 1 && service.LegacyVerifyCalls == 0,
            "Desktop verify did not use only the contextful service after a complete intake.");
    }

    private static async ValueTask AssertDisabledProviderAsync(
        RequestFixture request,
        ReviewedGameIntake intake,
        CancellationToken cancellationToken)
    {
        var service = new ExternalInstallFakeService
        {
            State = ExternalInstallDependencyState.DeclaredUnverified,
            CurrentHash = Hash('A'),
            ProviderEnabled = false,
            Prerequisites =
            [
                new ExternalHeadPartInstallPrerequisite(
                    ExternalHeadPartDiagnosticCodes.ProviderDisabled,
                    "OrchidAdornment.esp",
                    Hash('A'),
                    Hash('A'),
                    false,
                    "Enable OrchidAdornment.esp in the reviewed load order.")
            ]
        };
        using var viewModel = CreateViewModel(service, request.ProjectRoot);
        viewModel.ApplyReviewedIntake(intake);
        await viewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "disabled-proposal.json")),
            cancellationToken);
        Assert(service.ContextAnalyzeCalls == 1 &&
               viewModel.ExternalProviderEnabledState == "Disabled" &&
               viewModel.ExternalMissingPortableItems.Contains("OrchidAdornment.esp") &&
               !viewModel.CanApply &&
               viewModel.ExternalNextAction.Contains("Enable OrchidAdornment.esp",
                   StringComparison.Ordinal),
            "Desktop disabled-provider state did not remain visible or gated.");

        SkyrimNpcFinishCoreApplyResult blocked = await viewModel.ApplyAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "disabled-proposal.json")),
            Hash('0'),
            cancellationToken);
        Assert(!blocked.Applied && service.ContextApplyCalls == 0 &&
               service.LegacyApplyCalls == 0,
            "Desktop disabled-provider Apply bypassed the view-model gate or dispatched a service call.");
    }

    private static async ValueTask AssertHashDriftAsync(
        RequestFixture request,
        ReviewedGameIntake intake,
        CancellationToken cancellationToken)
    {
        var service = new ExternalInstallFakeService
        {
            State = ExternalInstallDependencyState.DeclaredUnverified,
            CurrentHash = Hash('B'),
            ProviderEnabled = true,
            Prerequisites =
            [
                new ExternalHeadPartInstallPrerequisite(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    "meshes/smp/orchid.nif",
                    Hash('A'),
                    Hash('B'),
                    true,
                    "Restore the expected external dependency bytes.")
            ]
        };
        using var viewModel = CreateViewModel(service, request.ProjectRoot);
        viewModel.ApplyReviewedIntake(intake);
        await viewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "drift-proposal.json")),
            cancellationToken);
        Assert(viewModel.ExternalProviderCurrentHash == Hash('B').Value &&
               viewModel.ExternalDriftedPortableItems.Contains("meshes/smp/orchid.nif") &&
               viewModel.ExternalMissingPortableItems.Count == 0 &&
               !viewModel.CanApply,
            "Desktop hash-drift state was not projected as a portable apply blocker.");
    }

    private static async ValueTask AssertMissingAssetAsync(
        RequestFixture request,
        ReviewedGameIntake intake,
        CancellationToken cancellationToken)
    {
        var service = new ExternalInstallFakeService
        {
            State = ExternalInstallDependencyState.DeclaredUnverified,
            CurrentHash = null,
            ProviderEnabled = true,
            Prerequisites =
            [
                new ExternalHeadPartInstallPrerequisite(
                    ExternalHeadPartDiagnosticCodes.AssetMissing,
                    "meshes/smp/orchid.nif",
                    Hash('A'),
                    null,
                    true,
                    "Provide the expected external dependency bytes.")
            ]
        };
        using var viewModel = CreateViewModel(service, request.ProjectRoot);
        viewModel.ApplyReviewedIntake(intake);
        await viewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "missing-proposal.json")),
            cancellationToken);
        Assert(viewModel.ExternalProviderCurrentHash == "Missing" &&
               viewModel.ExternalMissingPortableItems.Contains("meshes/smp/orchid.nif") &&
               !viewModel.CanApply,
            "Desktop missing-asset state was not projected as a portable apply blocker.");

        SkyrimNpcFinishCoreApplyResult blocked = await viewModel.ApplyAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "missing-proposal.json")),
            Hash('0'),
            cancellationToken);
        Assert(!blocked.Applied && service.ContextApplyCalls == 0 &&
               service.LegacyApplyCalls == 0,
            "Desktop blocked Apply bypassed the view-model gate or dispatched a service call.");
    }

    private static async ValueTask AssertVerifiedNotReadyAsync(
        RequestFixture request,
        ReviewedGameIntake intake,
        CancellationToken cancellationToken)
    {
        var service = new ExternalInstallFakeService
        {
            State = ExternalInstallDependencyState.Verified,
            InstallReady = false,
            CurrentHash = Hash('A'),
            ProviderEnabled = true
        };
        using var viewModel = CreateViewModel(service, request.ProjectRoot);
        viewModel.ApplyReviewedIntake(intake);
        await viewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "verified-not-ready-proposal.json")),
            cancellationToken);
        Assert(viewModel.ExternalInstallDependencyState ==
                   ExternalInstallDependencyState.Verified &&
               !viewModel.ExternalInstallReady && !viewModel.CanApply,
            "Desktop treated Verified current state with InstallReady=false as applyable.");

        SkyrimNpcFinishCoreApplyResult blocked = await viewModel.ApplyAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "verified-not-ready-proposal.json")),
            Hash('0'),
            cancellationToken);
        Assert(!blocked.Applied && service.ContextApplyCalls == 0 &&
               service.LegacyApplyCalls == 0,
            "Desktop Verified-but-not-ready Apply bypassed the InstallReady gate.");
    }

    private static async ValueTask AssertClearedAndReplacedIntakeAsync(
        RequestFixture request,
        ReviewedGameIntake complete,
        ReviewedGameIntake incomplete,
        CancellationToken cancellationToken)
    {
        var service = new ExternalInstallFakeService
        {
            State = ExternalInstallDependencyState.Verified,
            CurrentHash = Hash('A'),
            ProviderEnabled = true
        };
        using var viewModel = CreateViewModel(service, request.ProjectRoot);
        viewModel.ApplyReviewedIntake(complete);
        await viewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "clear-proposal.json")),
            cancellationToken);
        await viewModel.VerifyAsync(
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "clear-manifest.json")),
            Hash('E'),
            cancellationToken);
        Assert(viewModel.CanApply, "The verified baseline did not enable Apply.");

        viewModel.ApplyReviewedIntake(null);
        Assert(!viewModel.HasCompleteReviewedIntake && !viewModel.CanApply &&
               viewModel.ExternalInstallDependencyState ==
                   ExternalInstallDependencyState.DeclaredUnverified &&
               !viewModel.ExternalInstallReady &&
               viewModel.ExternalProviderCurrentHash ==
                   "Not checked this invocation" &&
               viewModel.ExternalProviderEnabledState ==
                   "Not checked this invocation" &&
               viewModel.ExternalMissingPortableItems.Count == 0 &&
               viewModel.ExternalDriftedPortableItems.Count == 0 &&
               viewModel.ExternalHistoricalSnapshot == "Historical only",
            "Clearing reviewed intake retained current observations or Apply enablement.");

        viewModel.ApplyReviewedIntake(incomplete);
        Assert(!viewModel.HasCompleteReviewedIntake && !viewModel.CanApply,
            "Replacing reviewed intake with incomplete evidence did not disable Apply.");
        await viewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "replaced-proposal.json")),
            cancellationToken);
        Assert(service.ContextAnalyzeCalls == 1 && service.LegacyAnalyzeCalls == 1 &&
               viewModel.ExternalInstallDependencyState ==
                   ExternalInstallDependencyState.DeclaredUnverified &&
               viewModel.ExternalMissingPortableItems.Count == 0 &&
               viewModel.ExternalDriftedPortableItems.Count == 0,
            "Desktop called the contextful service or retained current state for an incomplete replacement intake.");

        viewModel.ApplyReviewedIntake(complete);
        Assert(!viewModel.CanApply &&
               viewModel.ExternalInstallDependencyState ==
                   ExternalInstallDependencyState.DeclaredUnverified &&
               viewModel.ExternalProviderCurrentHash ==
                   "Not checked this invocation" &&
               viewModel.ExternalMissingPortableItems.Count == 0 &&
               viewModel.ExternalDriftedPortableItems.Count == 0,
            "Replacing intake did not clear the cached current observation before re-analysis.");

        var driftService = new ExternalInstallFakeService
        {
            State = ExternalInstallDependencyState.DeclaredUnverified,
            CurrentHash = Hash('B'),
            ProviderEnabled = true,
            Prerequisites =
            [
                new ExternalHeadPartInstallPrerequisite(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    "meshes/smp/orchid.nif",
                    Hash('A'),
                    Hash('B'),
                    true,
                    "Restore the expected external dependency bytes.")
            ]
        };
        using var driftViewModel = CreateViewModel(driftService, request.ProjectRoot);
        driftViewModel.ApplyReviewedIntake(complete);
        await driftViewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "drift-clear-proposal.json")),
            cancellationToken);
        await driftViewModel.VerifyAsync(
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "drift-clear-manifest.json")),
            Hash('E'),
            cancellationToken);
        Assert(driftViewModel.ExternalDriftedPortableItems.Contains("meshes/smp/orchid.nif"),
            "Desktop drift fixture did not expose its current drift row before clearing.");
        driftViewModel.ApplyReviewedIntake(null);
        Assert(driftViewModel.ExternalInstallDependencyState ==
                   ExternalInstallDependencyState.DeclaredUnverified &&
               driftViewModel.ExternalMissingPortableItems.Count == 0 &&
               driftViewModel.ExternalDriftedPortableItems.Count == 0 &&
               driftViewModel.ExternalProviderCurrentHash ==
                   "Not checked this invocation" &&
               driftViewModel.ExternalHistoricalSnapshot == "Historical only" &&
               !driftViewModel.CanApply,
            "Clearing a drifted intake retained current drift state or enabled Apply.");
    }

    private static async ValueTask AssertOrdinaryAsync(
        RequestFixture request,
        CancellationToken cancellationToken)
    {
        var service = new ExternalInstallFakeService
        {
            ExternalProposal = false,
            OrdinaryStatus = SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired
        };
        using var viewModel = CreateViewModel(service, request.ProjectRoot);
        viewModel.ApplyReviewedIntake(null);
        SkyrimNpcFinishCoreProposalResult analyzed = await viewModel.AnalyzeAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "ordinary-proposal.json")),
            cancellationToken);
        Assert(service.ContextAnalyzeCalls == 0 &&
               service.LegacyAnalyzeCalls == 1 &&
               viewModel.ExternalInstallDependencyState ==
                   ExternalInstallDependencyState.NotRequired &&
               viewModel.ExternalInstallReady && viewModel.CanApply &&
               viewModel.ExternalProviderName is null &&
               viewModel.Status ==
                   "Finish Core proposal ready for human review. Placement is not included.",
            "Ordinary Desktop Finish Core behavior no longer remains context-free and applyable.");
        Assert(!viewModel.RuntimeAuthority && !viewModel.VisualAuthority,
            "Ordinary Desktop Finish Core analysis upgraded runtime or visual authority.");

        SkyrimNpcFinishCoreApplyResult applied = await viewModel.ApplyAsync(
            request.RequestPath,
            request.RequestHash,
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "ordinary-proposal.json")),
            analyzed.ProposalSha256!.Value,
            cancellationToken);
        Assert(applied.Applied && service.ContextApplyCalls == 0 &&
               service.LegacyApplyCalls == 1,
            "Ordinary Desktop Apply did not dispatch exactly once through the context-free service.");
        await viewModel.VerifyAsync(
            new WorkspacePath(Path.Combine(request.ProjectRoot.Value, "ordinary-manifest.json")),
            Hash('E'),
            cancellationToken);
        Assert(service.ContextVerifyCalls == 0 && service.LegacyVerifyCalls == 1,
            "Ordinary Desktop Verify did not dispatch through the context-free service.");
    }

    private static void AssertPanelPresentation()
    {
        string xamlPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "src",
            "NpcManager.Desktop",
            "SkyrimNpcFinishWizardPanel.xaml");
        string xaml = File.ReadAllText(xamlPath);
        foreach (string name in new[]
        {
            "External hair provider",
            "External hair expected plugin hash",
            "External hair current plugin hash",
            "External hair provider enabled state",
            "External hair descriptor ID",
            "External hair drifted portable items",
            "External hair next gate"
        })
            Assert(xaml.Contains(name, StringComparison.Ordinal),
                $"Finish Core external dependency panel lost automation name '{name}'.");
        Assert(!xaml.Contains("<Button", StringComparison.Ordinal) &&
               !xaml.Contains("<CheckBox", StringComparison.Ordinal) &&
               !xaml.Contains("<ComboBox", StringComparison.Ordinal) &&
               !xaml.Contains("<TextBox", StringComparison.Ordinal),
            "Finish Core external dependency panel introduced a mutation control.");
    }

    private static SkyrimNpcFinishWizardViewModel CreateViewModel(
        ExternalInstallFakeService service,
        WorkspacePath workspaceRoot) =>
        new(
            new SkyrimNpcFinishWizardTransaction(service, workspaceRoot),
            _ => ReviewedTargetRace);

    private static async ValueTask<RequestFixture> WriteRequestAsync(
        string root,
        bool external)
    {
        string requestRoot = Path.Combine(
            root,
            external ? "external-request" : "ordinary-request");
        Directory.CreateDirectory(requestRoot);
        var projectRoot = new WorkspacePath(requestRoot);
        SkyrimNpcFinishCoreRequest request = BuildRequest(
            requestRoot,
            external);
        byte[] bytes = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
            request,
            projectRoot);
        WorkspacePath path = new(Path.Combine(requestRoot, "request.json"));
        await File.WriteAllBytesAsync(path.Value, bytes);
        return new(
            projectRoot,
            path,
            new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))));
    }

    private static SkyrimNpcFinishCoreRequest BuildRequest(
        string root,
        bool external)
    {
        PluginName plugin = new(external ? "ExternalRequest.esp" : "OrdinaryRequest.esp");
        string packageRoot = Path.Combine(root, "package");
        return new SkyrimNpcFinishCoreRequest
        {
            Schema = external
                ? SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier
                : SkyrimNpcFinishCoreRequest.SchemaIdentifier,
            Source = new SkyrimNpcFinishCoreSource
            {
                PackageRoot = new WorkspacePath(packageRoot),
                PackageManifest = new WorkspacePath(Path.Combine(packageRoot, "manifest.json")),
                PackageManifestSha256 = Hash('E'),
                PackageTreeSha256 = Hash('F'),
                PluginPath = new WorkspacePath(Path.Combine(packageRoot, plugin.Value)),
                Plugin = plugin,
                PluginSha256 = Hash('9')
            },
            Actor = new SkyrimNpcFinishCoreActor
            {
                EditorId = new EditorId("DesktopExternalHairActor"),
                FormId = new FormId(0x800)
            },
            Authorities = new SkyrimNpcFinishCoreAuthorities
            {
                BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                Providers = [],
                ExternalHeadParts = external
                    ? new SkyrimNpcFinishCoreExternalHeadPartAuthority(
                        new AssetPath(SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath),
                        Hash('8'),
                        [new SkyrimNpcFinishCoreExternalHeadPartBinding(Hash('D'), Hash('7'))])
                    : null
            },
            AiPolicy = new SkyrimNpcFinishCoreAiPolicy
            {
                Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                Confidence = SkyrimNpcFinishCoreConfidence.Average,
                Energy = 50,
                Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                Assistance = SkyrimNpcFinishCoreAssistance.HelpsAllies,
                Mood = SkyrimNpcFinishCoreMood.Neutral
            },
            OutfitPolicy = new SkyrimNpcFinishCoreOutfitPolicyDocument
            {
                Policy = SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit,
                ExistingOutfit = new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x12E46))
            },
            InventoryPolicy = new SkyrimNpcFinishCoreInventoryPolicyDocument
            {
                Policy = SkyrimNpcFinishCoreInventoryPolicy.PreserveInventory
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
                Root = new WorkspacePath(Path.Combine(root, "output")),
                Archive = new WorkspacePath(Path.Combine(root, "output.zip")),
                PluginFileName = plugin.Value
            }
        };
    }

    private static ReviewedGameIntake ReviewedIntake(
        string root,
        bool providerEnabled,
        bool providerReadSucceeded = true)
    {
        string dataRoot = Path.Combine(root, "reviewed-data");
        return new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(root),
            new WorkspacePath(dataRoot),
            new WorkspacePath(Path.Combine(root, "loadorder.txt")),
            new WorkspacePath(Path.Combine(root, "reviewed-output")),
            Hash('2'),
            [
                new PluginClosureReviewEntry(
                    new PluginName("OrchidAdornment.esp"),
                    1,
                    providerEnabled,
                    true,
                    true,
                    false,
                    providerReadSucceeded,
                    new WorkspacePath(Path.Combine(dataRoot, "OrchidAdornment.esp")),
                    providerReadSucceeded ? Hash('1') : null,
                    []),
                new PluginClosureReviewEntry(
                    new PluginName("Skyrim.esm"),
                    0,
                    true,
                    true,
                    false,
                    false,
                    true,
                    new WorkspacePath(Path.Combine(dataRoot, "Skyrim.esm")),
                    Hash('3'),
                    [])
            ],
            [],
            [],
            [],
            0,
            Hash('5'),
            Hash('4'),
            RuntimeAuthority: false);
    }

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private readonly record struct RequestFixture(
        WorkspacePath ProjectRoot,
        WorkspacePath RequestPath,
        Sha256Hash RequestHash);

    private sealed class ExternalInstallFakeService
        : ISkyrimNpcFinishCoreInstallContextService
    {
        public ExternalInstallDependencyState State { get; init; } =
            ExternalInstallDependencyState.DeclaredUnverified;

        public bool InstallReady { get; init; } = true;

        public Sha256Hash? CurrentHash { get; init; }

        public bool? ProviderEnabled { get; init; }

        public ImmutableArray<ExternalHeadPartInstallPrerequisite> Prerequisites
        {
            get;
            init;
        } = [];

        public bool ExternalProposal { get; init; } = true;

        public SkyrimNpcFinishCoreStatus OrdinaryStatus { get; init; } =
            SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite;

        public int LegacyAnalyzeCalls { get; private set; }

        public int ContextAnalyzeCalls { get; private set; }

        public int LegacyApplyCalls { get; private set; }

        public int ContextApplyCalls { get; private set; }

        public int LegacyVerifyCalls { get; private set; }

        public int ContextVerifyCalls { get; private set; }

        public ExternalHeadPartInstallVerificationContext? LastContext
        {
            get;
            private set;
        }

        public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateAnalyzeAsync(
            SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
            WorkspacePath proposalPath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimNpcFinishCoreValidationResult(
                false, [], ImmutableArray<Diagnostic>.Empty));

        public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateApplyAsync(
            SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal, Sha256Hash proposalSha256,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimNpcFinishCoreValidationResult(
                false, [], ImmutableArray<Diagnostic>.Empty));

        public ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
            SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
            WorkspacePath proposalPath, CancellationToken cancellationToken)
        {
            LegacyAnalyzeCalls++;
            return ValueTask.FromResult(CreateProposalResult(
                request,
                requestSha256,
                proposalPath));
        }

        public ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
            SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal, Sha256Hash proposalSha256,
            CancellationToken cancellationToken)
        {
            LegacyApplyCalls++;
            return ValueTask.FromResult(AppliedResult());
        }

        public ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
            WorkspacePath manifestPath, Sha256Hash manifestSha256,
            CancellationToken cancellationToken)
        {
            LegacyVerifyCalls++;
            return ValueTask.FromResult(CreateVerificationResult());
        }

        public ValueTask<SkyrimNpcFinishCoreProposalResult>
            AnalyzeWithInstallContextAsync(
                SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
                WorkspacePath proposalPath,
                ExternalHeadPartInstallVerificationContext installContext,
                CancellationToken cancellationToken)
        {
            ContextAnalyzeCalls++;
            LastContext = installContext;
            return ValueTask.FromResult(CreateProposalResult(
                request,
                requestSha256,
                proposalPath));
        }

        public ValueTask<SkyrimNpcFinishCoreApplyResult>
            ApplyWithInstallContextAsync(
                SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
                SkyrimNpcFinishCoreProposal proposal, Sha256Hash proposalSha256,
                ExternalHeadPartInstallVerificationContext installContext,
                CancellationToken cancellationToken)
        {
            ContextApplyCalls++;
            LastContext = installContext;
            return ValueTask.FromResult(AppliedResult());
        }

        public ValueTask<SkyrimNpcFinishCoreVerificationResult>
            VerifyWithInstallContextAsync(
                WorkspacePath manifestPath, Sha256Hash manifestSha256,
                ExternalHeadPartInstallVerificationContext installContext,
                CancellationToken cancellationToken)
        {
            ContextVerifyCalls++;
            LastContext = installContext;
            return ValueTask.FromResult(CreateVerificationResult());
        }

        private SkyrimNpcFinishCoreProposalResult CreateProposalResult(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            WorkspacePath proposalPath)
        {
            if (!ExternalProposal)
            {
                SkyrimNpcFinishCoreProposal ordinary = new()
                {
                    Schema = SkyrimNpcFinishCoreProposal.SchemaIdentifier,
                    Request = request,
                    RequestSha256 = requestSha256,
                    ProposalSha256 = null,
                    Status = OrdinaryStatus,
                    RuntimeAuthority = false
                };
                return PersistProposal(request, proposalPath, ordinary);
            }

            ExternalHeadPartInstallVerificationArtifact artifact =
                CreateArtifact();
            ExternalHeadPartInstallObservation[] observations =
            [
                new ExternalHeadPartInstallObservation(
                    "plugin",
                    "OrchidAdornment.esp",
                    Hash('A'),
                    1,
                    0)
            ];
            ExternalHeadPartInstallContextFingerprint? contextFingerprint =
                State == ExternalInstallDependencyState.Verified &&
                InstallReady && Prerequisites.IsDefaultOrEmpty
                    ? new ExternalHeadPartInstallContextFingerprint(
                        ExternalHeadPartDependencyDescriptorCodec
                            .ComputeInstallContextFingerprintHash(
                                observations.ToImmutableArray()),
                        observations.ToImmutableArray())
                    : null;
            var authority = new SkyrimNpcFinishCoreExternalHeadPartAuthority(
                new AssetPath(SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath),
                Hash('8'),
                [new SkyrimNpcFinishCoreExternalHeadPartBinding(Hash('D'), Hash('7'))]);
            SkyrimNpcFinishCoreStatus status = State ==
                ExternalInstallDependencyState.Verified
                ? SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite
                : SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired;
            SkyrimNpcFinishCoreProposal proposal = new()
            {
                Schema = SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier,
                Request = request,
                RequestSha256 = requestSha256,
                ProposalSha256 = Hash('0'),
                Status = status,
                RuntimeAuthority = false,
                ExternalHeadParts =
                    new SkyrimNpcFinishCoreExternalHeadPartProposalAuthority(
                        authority,
                        artifact,
                        contextFingerprint)
            };
            if (State != ExternalInstallDependencyState.Verified ||
                !InstallReady || !Prerequisites.IsDefaultOrEmpty)
                return new(true, proposal, proposalPath, Hash('0'), []);

            return PersistProposal(request, proposalPath, proposal);
        }

        private static SkyrimNpcFinishCoreProposalResult PersistProposal(
            SkyrimNpcFinishCoreRequest request,
            WorkspacePath proposalPath,
            SkyrimNpcFinishCoreProposal proposal)
        {
            WorkspacePath projectRoot = request.Source.PackageRoot is { } packageRoot
                ? new WorkspacePath(Path.GetDirectoryName(packageRoot.Value)!)
                : request.Output.Root!.Value;
            SkyrimNpcFinishCoreProposal unsigned = proposal with
            {
                ProposalSha256 = null
            };
            byte[] unsignedBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                unsigned,
                projectRoot);
            Sha256Hash proposalSha = SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                unsignedBytes);
            SkyrimNpcFinishCoreProposal bound = proposal with
            {
                ProposalSha256 = proposalSha
            };
            byte[] proposalBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                bound,
                projectRoot);
            File.WriteAllBytes(proposalPath.Value, proposalBytes);
            return new(true, bound, proposalPath, proposalSha, []);
        }

        private ExternalHeadPartInstallVerificationArtifact CreateArtifact(
            bool includeHistoricalValidity = false) =>
            new(
                ExternalHeadPartSchemaIdentifiers.InstallVerification,
                PackageIntegrity: true,
                DescriptorClosureValid: true,
                HistoricalSnapshotValid: includeHistoricalValidity ? true : null,
                DescriptorIds: [Hash('D')],
                VerifiedInstallSnapshot: includeHistoricalValidity
                    ? new ExternalHeadPartVerifiedInstallSnapshot(
                        Hash('6'),
                        [Hash('D')],
                        new ExternalHeadPartInstallContextFingerprint(Hash('F'), []))
                    : null,
                State,
                State == ExternalInstallDependencyState.Verified &&
                    InstallReady && Prerequisites.IsDefaultOrEmpty,
                State == ExternalInstallDependencyState.Verified && InstallReady,
                RuntimeAuthority: false,
                VisualAuthority: false,
                ProviderObservations:
                [
                    new ExternalHeadPartInstallProviderObservation(
                        new PluginName("OrchidAdornment.esp"),
                        Hash('A'),
                        CurrentHash,
                        ProviderEnabled)
                ],
                MissingPrerequisites: Prerequisites);

        private SkyrimNpcFinishCoreVerificationResult CreateVerificationResult()
        {
            if (!ExternalProposal)
                return new(true, new SkyrimNpcFinishCoreVerification
                {
                    Schema = SkyrimNpcFinishCoreVerification.SchemaIdentifier,
                    Status = SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired,
                    Verified = true,
                    PlacementIncluded = false,
                    RuntimeAuthority = false,
                    VisualAuthority = false
                }, []);
            return new(true, new SkyrimNpcFinishCoreVerification
            {
                Schema = SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier,
                Status = State == ExternalInstallDependencyState.Verified
                    ? SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite
                    : SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired,
                Verified = State == ExternalInstallDependencyState.Verified,
                PlacementIncluded = false,
                RuntimeAuthority = false,
                VisualAuthority = false,
                ExternalHeadParts =
                    new SkyrimNpcFinishCoreExternalHeadPartVerification(
                        CreateArtifact(includeHistoricalValidity: true))
            }, []);
        }

        private static SkyrimNpcFinishCoreProposalResult EmptyProposalResult() =>
            new(false, null, null, null, ImmutableArray<Diagnostic>.Empty);

        private static SkyrimNpcFinishCoreApplyResult AppliedResult() =>
            new(true, null, null, null, ImmutableArray<Diagnostic>.Empty);

        private static SkyrimNpcFinishCoreVerificationResult EmptyVerificationResult() =>
            new(false, null, ImmutableArray<Diagnostic>.Empty);
    }

    private sealed class FakeService : ISkyrimNpcFinishCoreService
    {
        public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateAnalyzeAsync(
            SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
            WorkspacePath proposalPath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimNpcFinishCoreValidationResult(
                false, [], ImmutableArray<Diagnostic>.Empty));

        public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateApplyAsync(
            SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal, Sha256Hash proposalSha256,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimNpcFinishCoreValidationResult(
                false, [], ImmutableArray<Diagnostic>.Empty));

        public ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
            SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
            WorkspacePath proposalPath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimNpcFinishCoreProposalResult(
                false, null, null, null, ImmutableArray<Diagnostic>.Empty));

        public ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
            SkyrimNpcFinishCoreRequest request, Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal, Sha256Hash proposalSha256,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimNpcFinishCoreApplyResult(
                false, null, null, null, ImmutableArray<Diagnostic>.Empty));

        public ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
            WorkspacePath manifestPath, Sha256Hash manifestSha256,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimNpcFinishCoreVerificationResult(
                false, null, ImmutableArray<Diagnostic>.Empty));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
