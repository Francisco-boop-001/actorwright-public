using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Presets;
using NpcManager.Rendering;

namespace NpcManager.Cli;

internal sealed class NpcVisualPreviewCliComposition :
    IPreviewServiceFactory<INpcVisualPreviewComposer>
{
    private static readonly Sha256Hash BlenderSha256 = new(
        "B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794");
    private static readonly Sha256Hash TexconvSha256 = new(
        "DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06");

    private readonly IWorkspacePolicy _policy;
    private readonly WorkspacePath _labRoot;
    private readonly ApplicationResourcePath _resourceBase;

    public NpcVisualPreviewCliComposition(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        ApplicationResourcePath? resourceBase = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _labRoot = labRoot;
        _resourceBase = resourceBase ??
            new ApplicationResourcePath(AppContext.BaseDirectory);
    }

    public ValueTask<PreviewServiceLease<INpcVisualPreviewComposer>>
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
        WorkspacePath texconv = new(Path.Combine(
            _labRoot.Value,
            "tools",
            "external",
            "directxtex-texconv-2026.5.7",
            "texconv.exe"));
        ApplicationResourcePath profileManifestPath = new(Path.Combine(
            _resourceBase.Value,
            "runtime",
            "rendering",
            "npc-preview-profile-manifest.json"));
        PreviewDependencyPreflightResult preflight =
            PreviewDependencyPreflightService.AdmitNpcPreview(
                runtime,
                NpcVisualPreviewRendererAuthority.ScriptId,
                NpcVisualPreviewRendererAuthority.ScriptSha256,
                blender,
                BlenderSha256,
                profile,
                profileManifestPath,
                texconv,
                TexconvSha256);
        if (!preflight.Accepted || runtime.Authority is null)
        {
            ImmutableArray<Diagnostic> diagnostics =
                preflight.Diagnostics;
            if (!runtime.Accepted)
                diagnostics = diagnostics.Add(new Diagnostic(
                    "npc-preview-native-runtime-unavailable",
                    DiagnosticSeverity.Error,
                    "The high-fidelity preview could not admit its exact packaged face-validation runtime."));
            return ValueTask.FromResult(
                new PreviewServiceLease<INpcVisualPreviewComposer>(
                    new RefusingComposer(diagnostics)));
        }

        MediaPipeNativeApi? nativeApi = null;
        try
        {
            Sha256Hash profileManifestSha256 = preflight.Authorities
                .Single(item => item.Role == "Blender profile")
                .Sha256!.Value;
            nativeApi = new MediaPipeNativeApi(runtime.Authority);
            ReferencePresetRuntimeAdmissionResult nativeAdmission =
                nativeApi.AdmitRuntime();
            if (!nativeAdmission.Accepted ||
                nativeAdmission.ManifestSha256 is null)
            {
                ImmutableArray<Diagnostic> diagnostics =
                    nativeAdmission.Diagnostics.Add(new Diagnostic(
                        "npc-preview-native-runtime-unavailable",
                        DiagnosticSeverity.Error,
                        "The high-fidelity preview runtime drifted after application-resource admission."));
                nativeApi.Dispose();
                return ValueTask.FromResult(
                    new PreviewServiceLease<INpcVisualPreviewComposer>(
                        new RefusingComposer(diagnostics)));
            }

            var renderer = new BlenderNpcVisualPreviewRenderer(
                blender,
                profile,
                profileManifestPath,
                profileManifestSha256,
                NpcVisualPreviewRendererAuthority.ScriptId,
                texconv,
                _policy,
                _labRoot,
                BlenderSha256,
                NpcVisualPreviewRendererAuthority.ScriptSha256,
                TexconvSha256);
            var composer = new NpcVisualPreviewComposer(
                new BethesdaNpcVisualSourceComposer(
                    _policy,
                    _labRoot),
                renderer,
                new NpcVisualPreviewVisualValidator(
                    new SkiaReferenceImageDecoder(_labRoot),
                    new MediaPipeFaceLandmarkInferenceService(nativeApi),
                    new ReferenceSemanticLandmarkProjector(),
                    nativeAdmission.ManifestSha256.Value),
                _policy,
                _labRoot);
            return ValueTask.FromResult(
                new PreviewServiceLease<INpcVisualPreviewComposer>(
                    composer,
                    [nativeApi]));
        }
        catch
        {
            nativeApi?.Dispose();
            throw;
        }
    }

    internal static PreviewDependencyPreflightResult ProbeDependencies(
        WorkspacePath labRoot,
        ApplicationResourcePath? resourceBase = null)
    {
        ApplicationResourcePath applicationBase = resourceBase ??
            new ApplicationResourcePath(AppContext.BaseDirectory);
        return PreviewDependencyPreflightService.AdmitNpcPreview(
            new ApplicationResourceRuntimeLocator(applicationBase)
                .AdmitReferencePresetRuntime(),
            NpcVisualPreviewRendererAuthority.ScriptId,
            NpcVisualPreviewRendererAuthority.ScriptSha256,
            new WorkspacePath(Path.Combine(
                labRoot.Value, "tools", "external",
                "blender-4.5.1-windows-x64",
                "blender-4.5.1-windows-x64", "blender.exe")),
            BlenderSha256,
            new WorkspacePath(Path.Combine(
                labRoot.Value, "tools", "external",
                "blender-4.5.1-pynifly-profile")),
            new ApplicationResourcePath(Path.Combine(
                applicationBase.Value, "runtime", "rendering",
                "npc-preview-profile-manifest.json")),
            new WorkspacePath(Path.Combine(
                labRoot.Value, "tools", "external",
                "directxtex-texconv-2026.5.7", "texconv.exe")),
            TexconvSha256);
    }

    private sealed class RefusingComposer(
        ImmutableArray<Diagnostic> diagnostics) :
        INpcVisualPreviewComposer
    {
        public ValueTask<NpcVisualPreviewComposeResult> ComposeAsync(
            NpcVisualPreviewComposeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new NpcVisualPreviewComposeResult(
                    false,
                    null,
                    diagnostics));
        }
    }
}
