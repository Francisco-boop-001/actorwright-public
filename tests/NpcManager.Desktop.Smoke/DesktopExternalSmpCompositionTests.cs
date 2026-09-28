using System.Collections;
using System.IO;
using System.Reflection;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Desktop.Smoke;

internal static class DesktopExternalSmpCompositionTests
{
    public static void Run()
    {
        string root = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "test-work",
            $"desktop-external-smp-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var labRoot = new WorkspacePath(root);
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            BlankNpcDesktopContext blank =
                BlankNpcDesktopComposition.Create(policy, labRoot);
            RaceMenuNpcDesktopContext raceMenu =
                RaceMenuNpcDesktopComposition.Create(
                    policy, labRoot, blank.FaceGeomCarrierService);

            AssertExternalSmpGraph(
                raceMenu.JslotBuildService,
                "RaceMenu JSlot composition",
                includeJslotServices: true);

            IFaceGenBakeAllService nativeBatch =
                NativeFaceGenBatchDesktopComposition.Create(policy, labRoot);
            AssertExternalSmpGraph(
                nativeBatch,
                "native FaceGen composition",
                includeJslotServices: false);

            AssertFinishIntakeForwarding(root);

            Console.WriteLine(
                "PASS Desktop external-SMP composition uses one provider-neutral graph.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertExternalSmpGraph(
        object root,
        string owner,
        bool includeJslotServices)
    {
        Require<ExternalHeadPartDependencyDiscoveryService>(
            root,
            owner,
            "external dependency discovery");
        Require<ExternalHeadPartPhysicsBindingResolver>(
            root,
            owner,
            "external physics binding");
        Require<ExternalHeadPartFaceGeomExclusionVerifier>(
            root,
            owner,
            "FaceGeom exclusion verification");
        if (includeJslotServices)
        {
            Require<RaceMenuJslotExternalHeadPartPrecheckService>(
                root,
                owner,
                "JSlot external precheck");
            Require<RaceMenuJslotOutputPluginBindingReader>(
                root,
                owner,
                "JSlot output-plugin binding reader");
            Require<BethesdaExternalHeadPartInstallVerifier>(
                root,
                owner,
                "strict external install verification");
        }

        Assert(UniqueReferences(FindServices<IExternalHeadPartPhysicsBindingResolver>(root)) == 1,
            $"{owner} duplicated the provider-neutral physics binding resolver.");
        Assert(UniqueReferences(FindServices<IExternalHeadPartDependencyDiscovery>(root)) == 1,
            $"{owner} duplicated the provider-neutral dependency discovery service.");
    }

    private static void AssertFinishIntakeForwarding(string root)
    {
        WorkspacePath labRoot = new(root);
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        DesktopShellFixture fixture = CreateShell(policy, labRoot);
        WorkspaceShellViewModel shell = fixture.Shell;
        if (shell.FinishCore is null)
            throw new InvalidOperationException(
                "The desktop shell did not compose the Finish wizard.");
        Assert(!shell.FinishCore.RuntimeAuthority &&
               !shell.FinishCore.VisualAuthority,
            "Desktop Finish composition must retain false runtime/visual authority.");

        string dataRoot = Path.Combine(root, "reviewed-data");
        Directory.CreateDirectory(dataRoot);
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(root),
            new WorkspacePath(dataRoot),
            new WorkspacePath(Path.Combine(root, "load-order.json")),
            new WorkspacePath(Path.Combine(root, "reviewed-output")),
            Hash('a'),
            [
                new PluginClosureReviewEntry(
                    new PluginName("Skyrim.esm"), 0, true, true, false,
                    false, true,
                    new WorkspacePath(Path.Combine(dataRoot, "Skyrim.esm")),
                    Hash('b'), []),
                new PluginClosureReviewEntry(
                    new PluginName("OrchidAdornment.esp"), 1, true, true,
                    false, false, true,
                    new WorkspacePath(Path.Combine(dataRoot,
                        "OrchidAdornment.esp")),
                    Hash('c'), [])
            ],
            [],
            [],
            [],
            0,
            Hash('d'),
            Hash('e'),
            RuntimeAuthority: false);

        shell.ApplyAcceptedIntakeAsync(intake).GetAwaiter().GetResult();
        Assert(fixture.RaceMenu.HasReviewedRequest,
            "The production RaceMenu request review did not complete: " +
            fixture.RaceMenu.Verdict + " / " + fixture.RaceMenu.Status +
            " / " + string.Join(" | ", fixture.RaceMenu.Diagnostics));
        Assert(fixture.RaceMenu.TryResolveTargetRace(intake) is not null,
            "The production RaceMenu target-race resolver did not retain the reviewed request.");
        Assert(shell.FinishCore.ReviewedIntake == intake,
            "Accepted reviewed intake was not forwarded to the Finish wizard.");
        Assert(shell.FinishCore.HasCompleteReviewedIntake &&
               shell.FinishCore.ReviewedEnabledPluginOrder.SequenceEqual(
                   [
                       new PluginName("Skyrim.esm"),
                       new PluginName("OrchidAdornment.esp")
                   ]),
            "Accepted reviewed intake did not preserve Data root and enabled plugin order.");
        AssertFinishAnalyzeReceivesIntakeContext(
            shell.FinishCore,
            fixture.FinishService,
            root,
            dataRoot);

        shell.ApplyAcceptedIntakeAsync(null).GetAwaiter().GetResult();
        Assert(shell.FinishCore.ReviewedIntake is null &&
               shell.FinishCore.ReviewedEnabledPluginOrder.Count == 0,
            "Cleared reviewed intake was not removed from the Finish wizard.");

        shell.Dispose();
    }

    private static void AssertFinishAnalyzeReceivesIntakeContext(
        SkyrimNpcFinishWizardViewModel finish,
        RecordingFinishService recordingService,
        string root,
        string dataRoot)
    {
        FinishRequestFixture request = WriteExternalFinishRequest(root);
        SkyrimNpcFinishCoreProposalResult analyzed = finish.AnalyzeAsync(
            request.RequestPath,
            request.RequestSha256,
            new WorkspacePath(Path.Combine(root, "finish-proposal.json")),
            CancellationToken.None).GetAwaiter().GetResult();
        Assert(!analyzed.Proposed && recordingService.ContextAnalyzeCalls == 1,
            "Finish Analyze did not use the contextful real service path.");
        ExternalHeadPartInstallVerificationContext? context =
            recordingService.LastAnalyzeContext;
        Assert(context is not null &&
               context.DataRoot.Value.Equals(dataRoot,
                   StringComparison.OrdinalIgnoreCase) &&
               context.EnabledPluginOrder.SequenceEqual(
                   [
                       new PluginName("Skyrim.esm"),
                       new PluginName("OrchidAdornment.esp")
                   ]),
            "Finish Analyze did not pass the accepted DataRoot and plugin order to the real service.");
    }

    internal static WorkspaceShellViewModel CreateShellWithVoice(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        SkyrimNpcVoiceViewModel voice) =>
        CreateShell(policy, labRoot, voice).Shell;

    private static DesktopShellFixture CreateShell(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        SkyrimNpcVoiceViewModel? voice = null)
    {
        ReviewedGameIntakeDesktopContext reviewed =
            ReviewedGameIntakeDesktopComposition.Create(policy, labRoot);
        BlankNpcDesktopContext blank =
            BlankNpcDesktopComposition.Create(policy, labRoot);
        RaceMenuNpcDesktopContext raceMenu =
            RaceMenuNpcDesktopComposition.Create(
                policy, labRoot, blank.FaceGeomCarrierService);
        var raceMenuViewModel = new RaceMenuNpcBuildViewModel(
            new FixedRaceMenuRequestLoader(BuildRaceMenuRequest(labRoot.Value)),
            raceMenu.BuildService,
            raceMenu.PresetCatalogService,
            raceMenu.PresetCatalogLifetime,
            labRoot,
            raceMenu.InitialRequestFile,
            raceMenu.InitialRequestSha256,
            raceMenu.OutputParent,
            raceMenu.PresetPreviewService,
            raceMenu.PresetSelectionTransactionService,
            raceMenu.JslotBuildService,
            raceMenu.PreflightService,
            raceMenu.PreviewComposer);
        ExistingNpcEditDesktopContext existing =
            ExistingNpcEditDesktopComposition.Create(policy, labRoot);
        var lighting = new SkyrimLightingWorkspaceViewModel(
            new SkyrimLightingSettingsService(
                policy,
                labRoot,
                new WorkspacePath(Path.Combine(
                    labRoot.Value,
                    ".actorwright",
                    "work",
                    "npc-studio-settings",
                    "preview-lighting.json"))));
        var recordingService = new RecordingFinishService();
        var finish = SkyrimNpcFinishWizardDesktopComposition.CreateForTest(
            policy,
            labRoot,
            raceMenuViewModel.TryResolveTargetRace,
            service => recordingService.Attach(service));
        raceMenuViewModel.RequestPath = Path.Combine(
            labRoot.Value, "race-menu-request.json");
        raceMenuViewModel.RequestSha256 = Hash('a').Value;
        raceMenuViewModel.ReviewAsync().GetAwaiter().GetResult();
        var shell = new WorkspaceShellViewModel(
            new ReviewedGameIntakeViewModel(reviewed),
            blank.Service,
            blank.InitialRequest,
            raceMenuViewModel,
            new ExistingNpcEditViewModel(
                existing.Service,
                existing.FormChoices,
                labRoot,
                existing.InitialRequest),
            SkyrimHeadPartEditDesktopComposition.Create(policy, labRoot),
            SkyrimFaceEditDesktopComposition.Create(policy, labRoot),
            SkyrimBodyEditDesktopComposition.Create(policy, labRoot),
            SkyrimSelectiveAppearancePasteDesktopComposition.Create(
                policy, labRoot),
            SkyrimOutfitProductionDesktopComposition.Create(policy, labRoot),
            SkyrimLeveledListProductionDesktopComposition.Create(
                policy, labRoot),
            SkyrimArmorProductionDesktopComposition.Create(policy, labRoot),
            SkyrimCharGenOptionsProductionDesktopComposition.Create(
                policy, labRoot),
            new NativeFaceGenBatchViewModel(
                NativeFaceGenBatchDesktopComposition.Create(policy, labRoot),
                reviewed.Service),
            SkyrimSavePackageDesktopComposition.Create(policy, labRoot),
            lighting,
            finishCore: finish.ViewModel,
            voice: voice);
        return new DesktopShellFixture(shell, recordingService, raceMenuViewModel);
    }

    private sealed record DesktopShellFixture(
        WorkspaceShellViewModel Shell,
        RecordingFinishService FinishService,
        RaceMenuNpcBuildViewModel RaceMenu);

    private sealed record FinishRequestFixture(
        WorkspacePath RequestPath,
        Sha256Hash RequestSha256);

    private sealed class FixedRaceMenuRequestLoader(
        RaceMenuNpcExecutionRequest loadedRequest)
        : IRaceMenuNpcExecutionRequestFileLoader
    {
        public ValueTask<RaceMenuNpcExecutionRequestFileLoadResult> LoadAsync(
            RaceMenuNpcExecutionRequestFileLoadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RaceMenuNpcExecutionRequestFileLoadResult(
                RaceMenuNpcExecutionRequestFileLoadStatus.Loaded,
                request.RequestFile,
                request.ExpectedSha256,
                request.ExpectedSha256,
                1,
                loadedRequest,
                []));
        }
    }

    private sealed class RecordingFinishService
    {
        private ISkyrimNpcFinishCoreInstallContextService? inner;

        public int ContextAnalyzeCalls { get; private set; }

        public ExternalHeadPartInstallVerificationContext? LastAnalyzeContext
        { get; private set; }

        public ISkyrimNpcFinishCoreService Attach(
            ISkyrimNpcFinishCoreService service)
        {
            inner = service as ISkyrimNpcFinishCoreInstallContextService ??
                throw new InvalidOperationException(
                    "Desktop Finish composition did not create the contextful service.");
            return new RecordingFinishServiceAdapter(this);
        }

        private sealed class RecordingFinishServiceAdapter(
            RecordingFinishService owner)
            : ISkyrimNpcFinishCoreInstallContextService
        {
            private ISkyrimNpcFinishCoreInstallContextService Inner =>
                owner.inner ?? throw new InvalidOperationException(
                    "The Finish service adapter was used before attachment.");

            public ValueTask<SkyrimNpcFinishCoreValidationResult>
                ValidateAnalyzeAsync(
                    SkyrimNpcFinishCoreRequest request,
                    Sha256Hash requestSha256,
                    WorkspacePath proposalPath,
                    CancellationToken cancellationToken) =>
                Inner.ValidateAnalyzeAsync(
                    request, requestSha256, proposalPath, cancellationToken);

            public ValueTask<SkyrimNpcFinishCoreValidationResult>
                ValidateApplyAsync(
                    SkyrimNpcFinishCoreRequest request,
                    Sha256Hash requestSha256,
                    SkyrimNpcFinishCoreProposal proposal,
                    Sha256Hash proposalSha256,
                    CancellationToken cancellationToken) =>
                Inner.ValidateApplyAsync(
                    request, requestSha256, proposal, proposalSha256,
                    cancellationToken);

            public ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
                SkyrimNpcFinishCoreRequest request,
                Sha256Hash requestSha256,
                WorkspacePath proposalPath,
                CancellationToken cancellationToken) =>
                Inner.AnalyzeAsync(
                    request, requestSha256, proposalPath, cancellationToken);

            public ValueTask<SkyrimNpcFinishCoreProposalResult>
                AnalyzeWithInstallContextAsync(
                    SkyrimNpcFinishCoreRequest request,
                    Sha256Hash requestSha256,
                    WorkspacePath proposalPath,
                    ExternalHeadPartInstallVerificationContext installContext,
                    CancellationToken cancellationToken)
            {
                owner.ContextAnalyzeCalls++;
                owner.LastAnalyzeContext = installContext;
                return Inner.AnalyzeWithInstallContextAsync(
                    request, requestSha256, proposalPath, installContext,
                    cancellationToken);
            }

            public ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
                SkyrimNpcFinishCoreRequest request,
                Sha256Hash requestSha256,
                SkyrimNpcFinishCoreProposal proposal,
                Sha256Hash proposalSha256,
                CancellationToken cancellationToken) =>
                Inner.ApplyAsync(
                    request, requestSha256, proposal, proposalSha256,
                    cancellationToken);

            public ValueTask<SkyrimNpcFinishCoreApplyResult>
                ApplyWithInstallContextAsync(
                    SkyrimNpcFinishCoreRequest request,
                    Sha256Hash requestSha256,
                    SkyrimNpcFinishCoreProposal proposal,
                    Sha256Hash proposalSha256,
                    ExternalHeadPartInstallVerificationContext installContext,
                    CancellationToken cancellationToken) =>
                Inner.ApplyWithInstallContextAsync(
                    request, requestSha256, proposal, proposalSha256,
                    installContext, cancellationToken);

            public ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
                WorkspacePath manifestPath,
                Sha256Hash manifestSha256,
                CancellationToken cancellationToken) =>
                Inner.VerifyAsync(manifestPath, manifestSha256, cancellationToken);

            public ValueTask<SkyrimNpcFinishCoreVerificationResult>
                VerifyWithInstallContextAsync(
                    WorkspacePath manifestPath,
                    Sha256Hash manifestSha256,
                    ExternalHeadPartInstallVerificationContext installContext,
                    CancellationToken cancellationToken) =>
                Inner.VerifyWithInstallContextAsync(
                    manifestPath, manifestSha256, installContext,
                    cancellationToken);
        }
    }

    private static RaceMenuNpcExecutionRequest BuildRaceMenuRequest(
        string root)
    {
        WorkspacePath PathOf(string name) =>
            new(Path.Combine(root, name));
        Sha256Hash hash = Hash('a');
        var skyrim = new PluginName("Skyrim.esm");
        var reference = new FormReference(skyrim, new FormId(0x13746));
        var build = new RaceMenuNpcBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            new RaceMenuNpcPresetBundle(
                PathOf("bundle.json"), hash,
                PathOf("selected.jslot"), hash,
                PathOf("char-gen.nif"), hash,
                PathOf("char-gen.dds"), hash,
                new RaceMenuNpcRecordAuthority(PathOf("record.json"), hash)),
            new BlankNpcProviderBindingRequest(
                PathOf("provider.json"), hash,
                GameEdition.SkyrimSpecialEdition,
                NpcSex.Female,
                PathOf("template.esp"), hash,
                new FormId(0x800),
                PathOf("carrier.nif"), hash,
                PathOf("tint.json"),
                PathOf("provider-data"),
                PathOf("dependencies.json")),
            PathOf("desktop-external-smp-output"),
            new PluginName("DesktopExternalSmp.esp"),
            new NpcCreationIdentity(
                new EditorId("DesktopExternalSmp"),
                new NpcName("Desktop external SMP")),
            new SkyrimNpcCreationTraits(
                NpcSex.Female, NpcCreationRole.Follower,
                true, false, true, false, true),
            new SkyrimNpcCreationReferences(
                reference, reference, reference, reference, reference),
            new SkyrimNpcCreationStats(
                new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                0, 0, 0, 1, 1, 100, 0, 0,
                50, 50, 50, 1f, 0f, 255));
        return new RaceMenuNpcExecutionRequest(
            build,
            new RaceMenuNpcStandaloneAssetAuthority(PathOf("standalone.json"), hash));
    }

    private static FinishRequestFixture WriteExternalFinishRequest(string root)
    {
        string requestRoot = Path.Combine(root, "external-finish-request");
        Directory.CreateDirectory(requestRoot);
        WorkspacePath projectRoot = new(requestRoot);
        string packageRoot = Path.Combine(requestRoot, "package");
        var request = new SkyrimNpcFinishCoreRequest
        {
            Schema = SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier,
            Source = new SkyrimNpcFinishCoreSource
            {
                PackageRoot = new WorkspacePath(packageRoot),
                PackageManifest = new WorkspacePath(Path.Combine(packageRoot, "manifest.json")),
                PackageManifestSha256 = Hash('b'),
                PackageTreeSha256 = Hash('c'),
                PluginPath = new WorkspacePath(Path.Combine(packageRoot, "ExternalFinish.esp")),
                Plugin = new PluginName("ExternalFinish.esp"),
                PluginSha256 = Hash('d')
            },
            Actor = new SkyrimNpcFinishCoreActor
            {
                EditorId = new EditorId("DesktopExternalSmpActor"),
                FormId = new FormId(0x800)
            },
            Authorities = new SkyrimNpcFinishCoreAuthorities
            {
                BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                Providers = [],
                ExternalHeadParts = new SkyrimNpcFinishCoreExternalHeadPartAuthority(
                    new AssetPath(SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath),
                    Hash('e'),
                    [new SkyrimNpcFinishCoreExternalHeadPartBinding(Hash('f'), Hash('a'))])
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
                    new PluginName("Skyrim.esm"), new FormId(0x12E46))
            },
            InventoryPolicy = new SkyrimNpcFinishCoreInventoryPolicyDocument
            {
                Policy = SkyrimNpcFinishCoreInventoryPolicy.PreserveInventory
            },
            SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
            {
                Template = new FormReference(
                    new PluginName("Skyrim.esm"), new FormId(0x1B217)),
                TemplateEditorId = "DefaultSandboxEditorLocation512"
            },
            Output = new SkyrimNpcFinishCoreOutput
            {
                Root = new WorkspacePath(Path.Combine(requestRoot, "output")),
                Archive = new WorkspacePath(Path.Combine(requestRoot, "output.zip")),
                PluginFileName = "ExternalFinish.esp"
            }
        };
        byte[] bytes = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
            request, projectRoot);
        WorkspacePath requestPath = new(Path.Combine(requestRoot, "request.json"));
        File.WriteAllBytes(requestPath.Value, bytes);
        return new(
            requestPath,
            new Sha256Hash(Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))));
    }

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private static void Require<T>(object root, string owner, string service)
        where T : class
    {
        Assert(FindServices<T>(root).Count > 0,
            $"{owner} omitted {service} ({typeof(T).Name}).");
    }

    private static int UniqueReferences<T>(IReadOnlyList<T> values)
        where T : class =>
        values.Cast<object>().Distinct(ReferenceEqualityComparer.Instance).Count();

    private static List<T> FindServices<T>(object root)
        where T : class
    {
        var found = new List<T>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(root, 0);
        return found;

        void Visit(object? value, int depth)
        {
            if (value is null || depth > 32 ||
                value is string || value is Type || value is MemberInfo ||
                value is Delegate || value.GetType().IsPrimitive)
                return;
            if (value is T service)
                found.Add(service);
            if (!visited.Add(value))
                return;

            foreach (FieldInfo field in value.GetType().GetFields(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic))
            {
                object? fieldValue;
                try
                {
                    fieldValue = field.GetValue(value);
                }
                catch (Exception exception) when (
                    exception is FieldAccessException or TargetException)
                {
                    continue;
                }

                if (fieldValue is IEnumerable sequence &&
                    fieldValue is not string)
                {
                    foreach (object? item in sequence)
                        Visit(item, depth + 1);
                }
                else
                {
                    Visit(fieldValue, depth + 1);
                }
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
