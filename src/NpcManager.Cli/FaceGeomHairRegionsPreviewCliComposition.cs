using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Presets;
using NpcManager.Rendering;

namespace NpcManager.Cli;

internal sealed class FaceGeomHairRegionsPreviewCliComposition :
    IPreviewServiceFactory<FaceGeomHairRegionsPreviewServices>
{
    private static readonly Sha256Hash BlenderSha256 = new(
        "B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794");
    private static readonly Sha256Hash PyniflySha256 = new(
        "296427A5E30F151223700298A7875DF2C0A61B5F422818FC7002B4F4D8DDAEE7");
    private static readonly Sha256Hash PyniflyProfileSha256 = new(
        "A909978665FBF6927F53F97726D73F14EF69B62E14D1F29DBF5F8FC65089D9A3");
    private static readonly Sha256Hash TexconvSha256 = new(
        "DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06");

    private readonly IWorkspacePolicy _policy;
    private readonly WorkspacePath _labRoot;
    private readonly ApplicationResourcePath _resourceBase;

    public FaceGeomHairRegionsPreviewCliComposition(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        ApplicationResourcePath? resourceBase = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _labRoot = labRoot;
        _resourceBase = resourceBase ??
            new ApplicationResourcePath(AppContext.BaseDirectory);
    }

    public ValueTask<PreviewServiceLease<FaceGeomHairRegionsPreviewServices>>
        CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationResourceRuntimeAdmissionResult runtime =
            new ApplicationResourceRuntimeLocator(_resourceBase)
                .AdmitReferencePresetRuntime();
        WorkspacePath blender = new(Path.Combine(
            _labRoot.Value,
            "tools",
            "external",
            "blender-4.5.1-windows-x64",
            "blender-4.5.1-windows-x64",
            "blender.exe"));
        WorkspacePath profile = new(Path.Combine(
            _labRoot.Value,
            "tools",
            "external",
            "blender-4.5.1-pynifly-profile"));
        WorkspacePath pynifly = new(Path.Combine(
            _labRoot.Value,
            "tools",
            "external",
            "_downloads",
            "io_scene_nifly.zip"));
        WorkspacePath texconv = new(Path.Combine(
            _labRoot.Value,
            "tools",
            "external",
            "directxtex-texconv-2026.5.7",
            "texconv.exe"));
        PreviewDependencyPreflightResult preflight =
            PreviewDependencyPreflightService
                .AdmitHairRegionsPreview(
                    runtime,
                    NpcVisualPreviewRendererAuthority.ScriptId,
                    NpcVisualPreviewRendererAuthority.ScriptSha256,
                    blender,
                    BlenderSha256,
                    profile,
                    PyniflyProfileSha256,
                    pynifly,
                    PyniflySha256,
                    texconv,
                    TexconvSha256);
        if (!preflight.Accepted || runtime.Authority is null)
        {
            ImmutableArray<Diagnostic> diagnostics =
                preflight.Diagnostics;
            if (!runtime.Accepted)
                diagnostics = diagnostics.Add(new Diagnostic(
                    "facegeom-hair-regions-native-runtime-unavailable",
                    DiagnosticSeverity.Error,
                    "Hair-region preview could not admit its exact packaged face-validation runtime."));
            return ValueTask.FromResult(RefusingLease(diagnostics));
        }

        MediaPipeNativeApi? nativeApi = null;
        FaceGeomHairRegionsPreviewService? service = null;
        WorkspacePath? workRoot = null;
        try
        {
            nativeApi = new MediaPipeNativeApi(runtime.Authority);
            ReferencePresetRuntimeAdmissionResult nativeAdmission =
                nativeApi.AdmitRuntime();
            if (!nativeAdmission.Accepted ||
                nativeAdmission.ManifestSha256 is null)
            {
                ImmutableArray<Diagnostic> diagnostics =
                    nativeAdmission.Diagnostics.Add(new Diagnostic(
                        "facegeom-hair-regions-native-runtime-unavailable",
                        DiagnosticSeverity.Error,
                        "Hair-region preview runtime drifted after application-resource admission."));
                nativeApi.Dispose();
                return ValueTask.FromResult(RefusingLease(diagnostics));
            }

            var workParent = new WorkspacePath(Path.Combine(
                _labRoot.Value,
                ".actorwright",
                "work"));
            var rendererRoot = new WorkspacePath(Path.Combine(
                workParent.Value,
                ".hrr"));
            Directory.CreateDirectory(rendererRoot.Value);
            workRoot = new WorkspacePath(Path.Combine(
                workParent.Value,
                $"facegeom-hair-regions-cli-{Guid.NewGuid():N}"));
            var validator = new NpcVisualPreviewVisualValidator(
                new SkiaReferenceImageDecoder(_labRoot),
                new MediaPipeFaceLandmarkInferenceService(nativeApi),
                new ReferenceSemanticLandmarkProjector(),
                nativeAdmission.ManifestSha256.Value);
            var renderer = new BlenderFaceGeomHairRegionsRenderer(
                blender,
                profile,
                pynifly,
                NpcVisualPreviewRendererAuthority.ScriptId,
                texconv,
                _policy,
                _labRoot,
                rendererRoot,
                BlenderSha256,
                PyniflySha256,
                PyniflyProfileSha256,
                NpcVisualPreviewRendererAuthority.ScriptSha256,
                TexconvSha256);
            service = new FaceGeomHairRegionsPreviewService(
                _labRoot,
                workRoot.Value,
                new BethesdaFaceGeomHairRegionsPreviewSourceService(
                    _policy,
                    _labRoot),
                renderer,
                validator,
                new FaceGeomHairRegionsDocumentCodec(_labRoot),
                createOwnedWorkRoot: true);
            return ValueTask.FromResult(
                new PreviewServiceLease<FaceGeomHairRegionsPreviewServices>(
                    new FaceGeomHairRegionsPreviewServices(
                        service,
                        validator),
                    [nativeApi, service]));
        }
        catch
        {
            service?.Dispose();
            nativeApi?.Dispose();
            if (service is null &&
                workRoot is { } root &&
                Directory.Exists(root.Value))
                Directory.Delete(root.Value, recursive: true);
            throw;
        }
    }

    private static PreviewServiceLease<FaceGeomHairRegionsPreviewServices>
        RefusingLease(ImmutableArray<Diagnostic> diagnostics) =>
        new(
            new FaceGeomHairRegionsPreviewServices(
                new RefusingPreviewService(diagnostics),
                new RefusingVisualValidator(diagnostics)));

    private sealed class RefusingPreviewService(
        ImmutableArray<Diagnostic> diagnostics) :
        IFaceGeomHairRegionsPreviewService
    {
        public ValueTask<FaceGeomHairRegionsPreviewResult> PreviewAsync(
            FaceGeomHairRegionsPreviewRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new FaceGeomHairRegionsPreviewResult(
                    false,
                    null,
                    [],
                    diagnostics,
                    VisualAuthority: false,
                    RuntimeAuthority: false));
        }
    }

    private sealed class RefusingVisualValidator(
        ImmutableArray<Diagnostic> diagnostics) :
        INpcVisualPreviewVisualValidator
    {
        public ValueTask<NpcVisualPreviewVisualEvidence> ValidateAsync(
            NpcVisualPreviewView faceFront,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NpcVisualPreviewVisualEvidence(
                0,
                0,
                0,
                0,
                false,
                diagnostics));

        public ValueTask<NpcVisualPreviewVisualEvidence> ValidateEncodedAsync(
            NpcVisualPreviewView faceFront,
            ReadOnlyMemory<byte> encodedImage,
            CancellationToken cancellationToken) =>
            ValidateAsync(faceFront, cancellationToken);
    }
}
