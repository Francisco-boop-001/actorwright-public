using System.Collections.Immutable;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Presets;
using NpcManager.Rendering;

namespace NpcManager.Desktop;

internal static class SkyrimMainWorkspaceDesktopComposition
{
    private static readonly Sha256Hash BlenderSha256 = new(
        "B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794");
    private static readonly Sha256Hash TexconvSha256 = new(
        "DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06");

    public static SkyrimMainWorkspaceViewModel Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var projectRoot = new WorkspacePath(Path.Combine(
            labRoot.Value,
            ".actorwright"));
        WorkspacePath blender = BlenderPath(labRoot);
        WorkspacePath profile = ProfilePath(labRoot);
        var nifGeometryReadback = new BethesdaNifGeometryReadbackService();
        var previewService = new SkyrimMainWorkspacePreviewService(
            new PreviewSceneService(
                policy,
                labRoot,
                new BlenderPreviewImageRenderer(
                    blender,
                    profile,
                    "render_preview_scene",
                    policy,
                    labRoot,
                    BlenderSha256)),
            new PreviewRerollService(policy, labRoot),
            new BlenderPreviewNifExporter(
                blender,
                profile,
                "export_preview_nif",
                policy,
                labRoot,
                BlenderSha256,
                nifGeometryReadback),
            policy,
            labRoot);
        var resourceBase = new ApplicationResourcePath(
            AppContext.BaseDirectory);
        var runtimeFactory = new RuntimeVisualFactory(
            resourceBase,
            policy,
            labRoot);
        var composerFactory = new NpcComposerFactory(
            resourceBase,
            policy,
            labRoot,
            blender,
            profile,
            TexconvPath(labRoot));
        WorkspacePath previewParent = new(Path.Combine(
            projectRoot.Value,
            "03-builds",
            "work",
            "desktop-npc-preview"));
        WorkspacePath comparisonParent = new(Path.Combine(
            projectRoot.Value,
            "03-builds",
            "work",
            "desktop-npc-preview-comparisons"));
        var crashReports = new DesktopCrashReportWriter(
            labRoot,
            ActorwrightWorkspace.WorkRoot(
                labRoot,
                "desktop-crash-reports"));
        var viewModel = new SkyrimMainWorkspaceViewModel(
            new SkyrimMainWorkspaceCatalogService(
                new BethesdaSkyrimMainWorkspaceReader()),
            new SkyrimMainWorkspaceSettingsService(
                policy,
                labRoot,
                projectRoot),
            new SkyrimMainWorkspaceSessionService(
                policy,
                labRoot,
                projectRoot),
            previewService,
            new PresetService(policy, labRoot),
            labRoot,
            new LazyComposer(composerFactory),
            previewParent,
            npcVisualLifetime: null,
            new LazyComparison(runtimeFactory),
            comparisonParent,
            new PackageVerifyService(
                new PackageManifestReader(policy, labRoot)),
            crashReports.WriteOperationFailure);
        viewModel.AttachHairRegionsDesktopCompositionFactory(() =>
            CreateHairRegionsComposition(
                policy,
                labRoot,
                resourceBase));
        return viewModel;
    }

    internal static INpcVisualPreviewComposer CreateNpcVisualPreviewComposer(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var resourceBase = new ApplicationResourcePath(
            AppContext.BaseDirectory);
        return new LazyComposer(new NpcComposerFactory(
            resourceBase,
            policy,
            labRoot,
            BlenderPath(labRoot),
            ProfilePath(labRoot),
            TexconvPath(labRoot)));
    }

    internal static PreviewDependencyPreflightResult
        ProbeNpcVisualPreviewDependencies(WorkspacePath labRoot)
    {
        var resourceBase = new ApplicationResourcePath(
            AppContext.BaseDirectory);
        return PreviewDependencyPreflightService.AdmitNpcPreview(
            new ApplicationResourceRuntimeLocator(resourceBase)
                .AdmitReferencePresetRuntime(),
            NpcVisualPreviewRendererAuthority.ScriptId,
            NpcVisualPreviewRendererAuthority.ScriptSha256,
            BlenderPath(labRoot),
            BlenderSha256,
            ProfilePath(labRoot),
            new ApplicationResourcePath(Path.Combine(
                resourceBase.Value,
                "runtime",
                "rendering",
                "npc-preview-profile-manifest.json")),
            TexconvPath(labRoot),
            TexconvSha256);
    }

    internal static (
        ApplicationResourcePath ProfileManifestPath,
        Sha256Hash ProfileManifestSha256)
        BindNpcVisualPreviewRendererAuthority(
            ApplicationResourcePath profileManifestPath,
            PreviewDependencyPreflightResult dependencies)
    {
        if (!dependencies.Accepted)
            throw new InvalidOperationException(
                "NPC preview dependencies must be accepted before renderer authority binding.");
        Sha256Hash profileManifestSha256 = dependencies.Authorities
            .Single(item => item.Role == "Blender profile")
            .Sha256 ?? throw new InvalidOperationException(
                "Accepted NPC preview dependencies omitted the Blender profile authority hash.");
        return (profileManifestPath, profileManifestSha256);
    }

    private static FaceGeomHairRegionsWizardDesktopComposition?
        CreateHairRegionsComposition(
            IWorkspacePolicy policy,
            WorkspacePath labRoot,
            ApplicationResourcePath resourceBase)
    {
        ApplicationResourceRuntimeAdmissionResult resource =
            new ApplicationResourceRuntimeLocator(resourceBase)
                .AdmitReferencePresetRuntime();
        WorkspacePath blender = BlenderPath(labRoot);
        WorkspacePath profile = ProfilePath(labRoot);
        WorkspacePath pynifly = new(Path.Combine(
            labRoot.Value,
            "tools",
            "external",
            "_downloads",
            "io_scene_nifly.zip"));
        WorkspacePath texconv = TexconvPath(labRoot);
        PreviewDependencyPreflightResult dependencies =
            PreviewDependencyPreflightService.AdmitHairRegionsPreview(
                resource,
                NpcVisualPreviewRendererAuthority.ScriptId,
                NpcVisualPreviewRendererAuthority.ScriptSha256,
                blender,
                BlenderSha256,
                profile,
                new Sha256Hash(
                    "A909978665FBF6927F53F97726D73F14EF69B62E14D1F29DBF5F8FC65089D9A3"),
                pynifly,
                new Sha256Hash(
                    "296427A5E30F151223700298A7875DF2C0A61B5F422818FC7002B4F4D8DDAEE7"),
                texconv,
                TexconvSha256);
        if (!dependencies.Accepted || resource.Authority is null)
            return null;

        MediaPipeNativeApi? native = null;
        try
        {
            native = new MediaPipeNativeApi(resource.Authority);
            ReferencePresetRuntimeAdmissionResult admission =
                native.AdmitRuntime();
            if (!admission.Accepted ||
                admission.ManifestSha256 is null)
            {
                native.Dispose();
                return null;
            }
            var validator = new NpcVisualPreviewVisualValidator(
                new SkiaReferenceImageDecoder(labRoot),
                new MediaPipeFaceLandmarkInferenceService(native),
                new ReferenceSemanticLandmarkProjector(),
                admission.ManifestSha256.Value);
            FaceGeomHairRegionsWizardDesktopComposition composition =
                FaceGeomHairRegionsWizardDesktopComposition.Create(
                    policy,
                    labRoot,
                    validator,
                    admission.ManifestSha256.Value,
                    native);
            native = null;
            return composition;
        }
        finally
        {
            native?.Dispose();
        }
    }

    private static WorkspacePath BlenderPath(WorkspacePath root) =>
        new(Path.Combine(
            root.Value,
            "tools",
            "external",
            "blender-4.5.1-windows-x64",
            "blender-4.5.1-windows-x64",
            "blender.exe"));

    private static WorkspacePath ProfilePath(WorkspacePath root) =>
        new(Path.Combine(
            root.Value,
            "tools",
            "external",
            "blender-4.5.1-pynifly-profile"));

    private static WorkspacePath TexconvPath(WorkspacePath root) =>
        new(Path.Combine(
            root.Value,
            "tools",
            "external",
            "directxtex-texconv-2026.5.7",
            "texconv.exe"));

    private sealed record RuntimeVisualServices(
        INpcVisualPreviewVisualValidator Validator,
        INpcVisualComparisonService Comparison);

    private sealed class RuntimeVisualFactory(
        ApplicationResourcePath resourceBase,
        IWorkspacePolicy policy,
        WorkspacePath labRoot) :
        IPreviewServiceFactory<RuntimeVisualServices>
    {
        public ValueTask<PreviewServiceLease<RuntimeVisualServices>>
            CreateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationResourceRuntimeAdmissionResult resource =
                new ApplicationResourceRuntimeLocator(resourceBase)
                    .AdmitReferencePresetRuntime();
            if (!resource.Accepted || resource.Authority is null)
                return ValueTask.FromResult(
                    new PreviewServiceLease<RuntimeVisualServices>(
                        new RuntimeVisualServices(
                            new RefusingValidator(resource.Diagnostics),
                            new RefusingComparison(resource.Diagnostics))));

            MediaPipeNativeApi? native = null;
            try
            {
                native = new MediaPipeNativeApi(resource.Authority);
                ReferencePresetRuntimeAdmissionResult admission =
                    native.AdmitRuntime();
                if (!admission.Accepted ||
                    admission.ManifestSha256 is null)
                {
                    native.Dispose();
                    return ValueTask.FromResult(
                        new PreviewServiceLease<RuntimeVisualServices>(
                            new RuntimeVisualServices(
                                new RefusingValidator(
                                    admission.Diagnostics),
                                new RefusingComparison(
                                    admission.Diagnostics))));
                }
                var decoder = new SkiaReferenceImageDecoder(labRoot);
                var inference =
                    new MediaPipeFaceLandmarkInferenceService(native);
                var projector =
                    new ReferenceSemanticLandmarkProjector();
                var services = new RuntimeVisualServices(
                    new NpcVisualPreviewVisualValidator(
                        decoder,
                        inference,
                        projector,
                        admission.ManifestSha256.Value),
                    new NpcVisualComparisonService(
                        decoder,
                        inference,
                        projector,
                        policy,
                        labRoot,
                        admission.ManifestSha256.Value));
                return ValueTask.FromResult(
                    new PreviewServiceLease<RuntimeVisualServices>(
                        services,
                        [native]));
            }
            catch
            {
                native?.Dispose();
                throw;
            }
        }
    }

    private sealed class NpcComposerFactory(
        ApplicationResourcePath resourceBase,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        WorkspacePath blender,
        WorkspacePath profile,
        WorkspacePath texconv) :
        IPreviewServiceFactory<INpcVisualPreviewComposer>
    {
        public ValueTask<PreviewServiceLease<INpcVisualPreviewComposer>>
            CreateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplicationResourceRuntimeAdmissionResult resource =
                new ApplicationResourceRuntimeLocator(resourceBase)
                    .AdmitReferencePresetRuntime();
            ApplicationResourcePath profileManifestPath = new(Path.Combine(
                resourceBase.Value,
                "runtime",
                "rendering",
                "npc-preview-profile-manifest.json"));
            PreviewDependencyPreflightResult dependencies =
                PreviewDependencyPreflightService.AdmitNpcPreview(
                    resource,
                    NpcVisualPreviewRendererAuthority.ScriptId,
                    NpcVisualPreviewRendererAuthority.ScriptSha256,
                    blender,
                    BlenderSha256,
                    profile,
                    profileManifestPath,
                    texconv,
                    TexconvSha256);
            if (!dependencies.Accepted || resource.Authority is null)
                return ValueTask.FromResult(
                    new PreviewServiceLease<INpcVisualPreviewComposer>(
                        new RefusingComposer(dependencies.Diagnostics)));

            MediaPipeNativeApi? native = null;
            try
            {
                (
                    ApplicationResourcePath admittedProfileManifestPath,
                    Sha256Hash admittedProfileManifestSha256) =
                    BindNpcVisualPreviewRendererAuthority(
                        profileManifestPath,
                        dependencies);
                native = new MediaPipeNativeApi(resource.Authority);
                ReferencePresetRuntimeAdmissionResult admission =
                    native.AdmitRuntime();
                if (!admission.Accepted ||
                    admission.ManifestSha256 is null)
                {
                    native.Dispose();
                    return ValueTask.FromResult(
                        new PreviewServiceLease<INpcVisualPreviewComposer>(
                            new RefusingComposer(
                                admission.Diagnostics)));
                }
                var composer = new NpcVisualPreviewComposer(
                    new BethesdaNpcVisualSourceComposer(
                        policy,
                        labRoot),
                    new BlenderNpcVisualPreviewRenderer(
                        blender,
                        profile,
                        admittedProfileManifestPath,
                        admittedProfileManifestSha256,
                        NpcVisualPreviewRendererAuthority.ScriptId,
                        texconv,
                        policy,
                        labRoot,
                        BlenderSha256,
                        NpcVisualPreviewRendererAuthority.ScriptSha256,
                        TexconvSha256),
                    new NpcVisualPreviewVisualValidator(
                        new SkiaReferenceImageDecoder(labRoot),
                        new MediaPipeFaceLandmarkInferenceService(native),
                        new ReferenceSemanticLandmarkProjector(),
                        admission.ManifestSha256.Value),
                    policy,
                    labRoot);
                return ValueTask.FromResult(
                    new PreviewServiceLease<INpcVisualPreviewComposer>(
                        composer,
                        [native]));
            }
            catch
            {
                native?.Dispose();
                throw;
            }
        }
    }

    private sealed class LazyComposer(
        IPreviewServiceFactory<INpcVisualPreviewComposer> factory) :
        INpcVisualPreviewComposer
    {
        public async ValueTask<NpcVisualPreviewComposeResult> ComposeAsync(
            NpcVisualPreviewComposeRequest request,
            CancellationToken cancellationToken)
        {
            await using PreviewServiceLease<INpcVisualPreviewComposer>
                lease = await factory.CreateAsync(cancellationToken);
            return await lease.Service.ComposeAsync(
                request,
                cancellationToken);
        }
    }

    private sealed class LazyComparison(
        IPreviewServiceFactory<RuntimeVisualServices> factory) :
        INpcVisualComparisonService
    {
        public async ValueTask<NpcVisualComparisonResult> CompareAsync(
            NpcVisualComparisonRequest request,
            CancellationToken cancellationToken)
        {
            await using PreviewServiceLease<RuntimeVisualServices> lease =
                await factory.CreateAsync(cancellationToken);
            return await lease.Service.Comparison.CompareAsync(
                request,
                cancellationToken);
        }
    }

    private sealed class RefusingComposer(
        ImmutableArray<Diagnostic> diagnostics) :
        INpcVisualPreviewComposer
    {
        public ValueTask<NpcVisualPreviewComposeResult> ComposeAsync(
            NpcVisualPreviewComposeRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NpcVisualPreviewComposeResult(
                false,
                null,
                diagnostics));
    }

    private sealed class RefusingValidator(
        ImmutableArray<Diagnostic> diagnostics) :
        INpcVisualPreviewVisualValidator
    {
        public ValueTask<NpcVisualPreviewVisualEvidence> ValidateAsync(
            NpcVisualPreviewView faceFront,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NpcVisualPreviewVisualEvidence(
                0, 0, 0, 0, false, diagnostics));

        public ValueTask<NpcVisualPreviewVisualEvidence> ValidateEncodedAsync(
            NpcVisualPreviewView faceFront,
            ReadOnlyMemory<byte> encodedImage,
            CancellationToken cancellationToken) =>
            ValidateAsync(faceFront, cancellationToken);
    }

    private sealed class RefusingComparison(
        ImmutableArray<Diagnostic> diagnostics) :
        INpcVisualComparisonService
    {
        public ValueTask<NpcVisualComparisonResult> CompareAsync(
            NpcVisualComparisonRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NpcVisualComparisonResult(
                false,
                null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null,
                0,
                diagnostics));
    }
}
