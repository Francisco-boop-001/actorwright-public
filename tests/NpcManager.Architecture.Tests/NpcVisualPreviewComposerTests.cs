using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestNpcVisualPreviewComposer()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"npc-visual-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var source = new FakeNpcVisualSourceComposer(
                SourceGraph(NpcVisualPreviewRoute.Cotr, root));
            var renderer = new FakeNpcVisualPreviewRenderer();
            var composer = new NpcVisualPreviewComposer(
                source,
                renderer,
                new AcceptedNpcVisualValidator(),
                new KOnlyWorkspacePolicy(
                    labRoot, new WorkspacePath("F:\\ExampleGame")),
                labRoot);
            NpcVisualPreviewComposeResult result = await composer.ComposeAsync(
                Request(labRoot, root, "accepted"),
                CancellationToken.None);

            Assert(result.Composed && result.Bundle is not null,
                "A supported source graph did not produce a preview bundle.");
            NpcVisualPreviewBundle bundle = result.Bundle!;
            Assert(bundle.SchemaVersion == "npc-preview-bundle/1" &&
                   bundle.SceneSchemaVersion == "npc-preview-scene/2" &&
                   !bundle.RuntimeAuthority,
                "The preview bundle weakened its schema or runtime-authority boundary.");
            Assert(bundle.Label ==
                   "High-fidelity off-engine preview — Skyrim runtime remains authoritative",
                "The required off-engine authority label was not preserved.");
            Assert(bundle.Views.Select(item => item.Id).SequenceEqual(
                ["face-front", "face-left", "face-right", "face-alternate-light",
                 "body-front", "body-back"]),
                "The six deterministic NPC review views were not retained in order.");
            Assert(bundle.Source.Assets.Count(item =>
                       item.Role == NpcVisualAssetRole.FaceGeom) == 1 &&
                   bundle.Source.Assets.All(item =>
                       item.Role != NpcVisualAssetRole.Hair ||
                       !item.BakedIntoFaceGeom),
                "The authoritative FaceGeom graph was duplicated as a baked headpart mesh.");
            Assert(File.Exists(bundle.BundlePath.Value) &&
                   File.Exists(bundle.HashManifestPath.Value) &&
                   HashNpcPreviewTestFile(bundle.BundlePath.Value) ==
                       bundle.BundleSha256 &&
                   HashNpcPreviewTestFile(bundle.HashManifestPath.Value) ==
                       bundle.HashManifestSha256,
                "Bundle or hash-manifest readback was not independently bound.");
            string bundleJson = File.ReadAllText(bundle.BundlePath.Value);
            Assert(bundleJson.IndexOf(
                       "\"a-material\"", StringComparison.Ordinal) <
                   bundleJson.IndexOf(
                       "\"z-material\"", StringComparison.Ordinal),
                "Preview dictionary JSON is not canonical across process hash seeds.");
            using (JsonDocument document = JsonDocument.Parse(
                       File.ReadAllBytes(bundle.BundlePath.Value)))
            {
                Assert(document.RootElement.GetProperty("schemaVersion")
                           .GetString() == "npc-preview-bundle/1" &&
                       document.RootElement.GetProperty("runtimeAuthority")
                           .GetBoolean() == false,
                    "Serialized bundle identity differs from the accepted result.");
            }
            Assert(renderer.CallCount == 1,
                "The supported bundle was not rendered exactly once.");

            var advisoryComposer = new NpcVisualPreviewComposer(
                source,
                new FakeNpcVisualPreviewRenderer(),
                new AdvisoryFailureNpcVisualValidator(),
                new KOnlyWorkspacePolicy(
                    labRoot, new WorkspacePath("F:\\ExampleGame")),
                labRoot);
            NpcVisualPreviewComposeResult advisory =
                await advisoryComposer.ComposeAsync(
                    Request(labRoot, root, "advisory-visual-check"),
                    CancellationToken.None);
            Assert(advisory.Composed && advisory.Bundle is not null &&
                   advisory.Bundle.VisualEvidence.DetectedFaceCount == 2 &&
                   !advisory.Bundle.VisualEvidence.EyesNoseAndMouthBounded &&
                   advisory.Diagnostics.Any(item =>
                       item.Code == "reference-native-landmarker-face-count" &&
                       item.Severity == DiagnosticSeverity.Warning) &&
                   advisory.Diagnostics.Any(item =>
                       item.Code == "npc-preview-face-visual-check-failed" &&
                       item.Severity == DiagnosticSeverity.Warning) &&
                   advisory.Diagnostics.All(item =>
                       item.Severity != DiagnosticSeverity.Error),
                 "Advisory face-landmark failure discarded a successfully rendered preview or hid its warning.");

            ImmutableArray<Diagnostic> exposureWarnings =
            [
                new Diagnostic(
                    "npc-preview-body-underexposed",
                    DiagnosticSeverity.Warning,
                    "Body luminance measured front 54.25 and back 48.32; threshold 55."),
                new Diagnostic(
                    "npc-preview-hands-underexposed",
                    DiagnosticSeverity.Warning,
                    "Hand luminance measured 39.75; threshold 40.")
            ];
            var exposureRenderer = new FakeNpcVisualPreviewRenderer(
                exposureWarnings);
            var exposureComposer = new NpcVisualPreviewComposer(
                source,
                exposureRenderer,
                new AcceptedNpcVisualValidator(),
                new KOnlyWorkspacePolicy(
                    labRoot, new WorkspacePath("F:\\ExampleGame")),
                labRoot);
            NpcVisualPreviewComposeResult exposure =
                await exposureComposer.ComposeAsync(
                    Request(labRoot, root, "exposure-advisory"),
                    CancellationToken.None);
            Assert(exposure.Composed && exposure.Bundle is not null &&
                   exposure.Bundle.Label ==
                       "High-fidelity off-engine preview — Skyrim runtime remains authoritative" &&
                   !exposure.Bundle.RuntimeAuthority &&
                   File.Exists(exposure.Bundle.ContactSheetPath.Value) &&
                   File.Exists(exposure.Bundle.BundlePath.Value) &&
                   File.Exists(exposure.Bundle.HashManifestPath.Value) &&
                   exposure.Bundle.Diagnostics.Any(item =>
                       item.Code == "npc-preview-body-underexposed" &&
                       item.Severity == DiagnosticSeverity.Warning &&
                       item.Message.Contains("54.25", StringComparison.Ordinal) &&
                       item.Message.Contains("48.32", StringComparison.Ordinal) &&
                       item.Message.Contains("55", StringComparison.Ordinal)) &&
                   exposure.Bundle.Diagnostics.Any(item =>
                       item.Code == "npc-preview-hands-underexposed" &&
                       item.Severity == DiagnosticSeverity.Warning &&
                       item.Message.Contains("39.75", StringComparison.Ordinal) &&
                       item.Message.Contains("40", StringComparison.Ordinal)),
                "Exposure advisories did not retain a truthful bundle, contact sheet, and hash manifest.");

            var ubeRenderer = new FakeNpcVisualPreviewRenderer();
            var ubeComposer = new NpcVisualPreviewComposer(
                new FakeNpcVisualSourceComposer(
                    SourceGraph(NpcVisualPreviewRoute.Ube, root)),
                ubeRenderer,
                new AcceptedNpcVisualValidator(),
                new KOnlyWorkspacePolicy(
                    labRoot, new WorkspacePath("F:\\ExampleGame")),
                labRoot);
            NpcVisualPreviewComposeResult refused =
                await ubeComposer.ComposeAsync(
                    Request(labRoot, root, "ube-refused"),
                    CancellationToken.None);
            Assert(!refused.Composed && refused.Bundle is null &&
                   refused.Diagnostics.Any(item =>
                       item.Code ==
                       "npc-preview-runtime-composed-route-unsupported") &&
                   ubeRenderer.CallCount == 0,
                "UBE did not fail closed before off-engine rendering.");

            NpcVisualPreviewComposeResult existing =
                await composer.ComposeAsync(
                    Request(labRoot, root, "accepted"),
                    CancellationToken.None);
            Assert(!existing.Composed && existing.Diagnostics.Any(item =>
                       item.Code == "npc-preview-output-exists"),
                "A preview output root was overwritten.");
            string packageBuilder = File.ReadAllText(Path.Combine(
                labRoot.Value,
                "projects",
                "NpcManagerReimplementation",
                "tools",
                "hardening",
                "build_package_candidate.ps1"));
            Assert(
                packageBuilder.Contains(
                    "'tools\\rendering\\render_npc_preview_bundle.py'",
                    StringComparison.Ordinal),
                "The package source snapshot omitted the production NPC preview renderer.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static NpcVisualPreviewComposeRequest Request(
        WorkspacePath labRoot,
        string root,
        string name)
    {
        var plugin = new PluginName("Fixture.esp");
        var identity = new SkyrimMainWorkspaceIdentity(
            plugin, plugin, new FormId(0x800), "NPC_");
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            new WorkspacePath(root),
            new WorkspacePath(Path.Combine(root, "loadorder.txt")),
            new WorkspacePath(Path.Combine(root, "unused-output")),
            new Sha256Hash(new string('1', 64)),
            [], [], [], [], 0,
            new Sha256Hash(new string('2', 64)),
            new Sha256Hash(new string('3', 64)),
            false);
        return new NpcVisualPreviewComposeRequest(
            intake,
            identity,
            null,
            new NpcVisualPreviewOptions(),
            new WorkspacePath(Path.Combine(root, name)));
    }

    private static NpcVisualSourceGraph SourceGraph(
        NpcVisualPreviewRoute route,
        string root)
    {
        var plugin = new PluginName("Fixture.esp");
        var identity = new SkyrimMainWorkspaceIdentity(
            plugin, plugin, new FormId(0x800), "NPC_");
        ImmutableArray<NpcVisualAsset> assets =
        [
            Asset(NpcVisualAssetRole.FaceGeom, "face.nif", root, false),
            Asset(NpcVisualAssetRole.FaceTint, "facetint.dds", root, false),
            Asset(NpcVisualAssetRole.Body, "body.nif", root, false),
            Asset(NpcVisualAssetRole.Outfit, "dress.nif", root, false),
            Asset(NpcVisualAssetRole.Hair, "hair-baked.nif", root, false)
        ];
        return new NpcVisualSourceGraph(
            route,
            identity,
            NpcSex.Female,
            100,
            route == NpcVisualPreviewRoute.Cotr
                ? "COR_AllRace.esp|0x0005A184"
                : "UBE.esp|0x00000800",
            "#D6BE83",
            "#E7B79D",
            assets,
            [],
            true,
            []);
    }

    private static NpcVisualAsset Asset(
        NpcVisualAssetRole role,
        string name,
        string root,
        bool baked) =>
        new(
            role,
            new AssetPath(name),
            "FixtureProvider",
            new Sha256Hash(new string('A', 64)),
            128,
            new WorkspacePath(Path.Combine(root, name)),
            baked,
            []);

    private static Sha256Hash HashNpcPreviewTestFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed class FakeNpcVisualSourceComposer(
        NpcVisualSourceGraph source)
        : INpcVisualSourceComposer
    {
        public ValueTask<NpcVisualSourceComposeResult> ComposeSourceAsync(
            NpcVisualPreviewComposeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new NpcVisualSourceComposeResult(
                    true, source, source.Diagnostics));
        }
    }

    private sealed class FakeNpcVisualPreviewRenderer
        : INpcVisualPreviewRenderer
    {
        private readonly ImmutableArray<Diagnostic> renderDiagnostics;

        public FakeNpcVisualPreviewRenderer(
            ImmutableArray<Diagnostic> renderDiagnostics = default)
        {
            this.renderDiagnostics = renderDiagnostics.IsDefault
                ? []
                : renderDiagnostics;
        }

        public int CallCount { get; private set; }

        public async ValueTask<NpcVisualPreviewRenderResult> RenderAsync(
            NpcVisualPreviewRenderRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ImmutableArray<string> ids =
            [
                "face-front", "face-left", "face-right",
                "face-alternate-light", "body-front", "body-back"
            ];
            var views = ImmutableArray.CreateBuilder<NpcVisualPreviewView>();
            foreach (string id in ids)
            {
                string image = Path.Combine(
                    request.OutputRoot.Value, $"{id}.png");
                string mask = Path.Combine(
                    request.OutputRoot.Value, $"{id}.roles.png");
                await File.WriteAllBytesAsync(
                    image, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3],
                    cancellationToken);
                await File.WriteAllBytesAsync(
                    mask, [0x89, 0x50, 0x4E, 0x47, 4, 5, 6],
                    cancellationToken);
                views.Add(new NpcVisualPreviewView(
                    id,
                    new WorkspacePath(image),
                    HashNpcPreviewTestFile(image),
                    new WorkspacePath(mask),
                    HashNpcPreviewTestFile(mask),
                    900,
                    900));
            }
            string contact = Path.Combine(
                request.OutputRoot.Value, "contact-sheet.png");
            await File.WriteAllBytesAsync(
                contact, [0x89, 0x50, 0x4E, 0x47, 7, 8, 9],
                cancellationToken);
            return new NpcVisualPreviewRenderResult(
                true,
                views.ToImmutable(),
                new WorkspacePath(contact),
                HashNpcPreviewTestFile(contact),
                new NpcVisualPreviewRenderEvidence(
                    "4.5.1",
                    "BLENDER_EEVEE_NEXT",
                    1,
                    true,
                    1,
                    ["FixtureSkeleton"],
                    0,
                    ImmutableDictionary<string, int>.Empty
                        .Add("z-material", 1)
                        .Add("a-material", 1),
                    ImmutableDictionary<string, long>.Empty,
                    ImmutableDictionary<string, double>.Empty,
                    [],
                    [],
                    new WorkspacePath(contact),
                    HashNpcPreviewTestFile(contact)),
                renderDiagnostics);
        }
    }

    private sealed class AcceptedNpcVisualValidator
        : INpcVisualPreviewVisualValidator
    {
        public ValueTask<NpcVisualPreviewVisualEvidence> ValidateAsync(
            NpcVisualPreviewView faceFront,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new NpcVisualPreviewVisualEvidence(
                    1,
                    0.99,
                    478,
                    31,
                    true,
                    []));
        }

        public ValueTask<NpcVisualPreviewVisualEvidence>
            ValidateEncodedAsync(
                NpcVisualPreviewView faceFront,
                ReadOnlyMemory<byte> encodedImage,
                CancellationToken cancellationToken) =>
            ValidateAsync(
                faceFront,
                cancellationToken);
    }

    private sealed class AdvisoryFailureNpcVisualValidator
        : INpcVisualPreviewVisualValidator
    {
        public ValueTask<NpcVisualPreviewVisualEvidence> ValidateAsync(
            NpcVisualPreviewView faceFront,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new NpcVisualPreviewVisualEvidence(
                    2,
                    0.51,
                    0,
                    0,
                    false,
                    [new Diagnostic(
                        "reference-native-landmarker-face-count",
                        DiagnosticSeverity.Error,
                        "The native landmarker returned 2 faces; exactly one is required.")]));
        }

        public ValueTask<NpcVisualPreviewVisualEvidence>
            ValidateEncodedAsync(
                NpcVisualPreviewView faceFront,
                ReadOnlyMemory<byte> encodedImage,
                CancellationToken cancellationToken) =>
            ValidateAsync(faceFront, cancellationToken);
    }
}
