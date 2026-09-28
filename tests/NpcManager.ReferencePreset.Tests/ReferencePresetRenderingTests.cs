using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Infrastructure;
using NpcManager.Rendering;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferencePresetRenderingTests
{
    private const string LabRoot = @"K:\ExampleWorkspace";
    private const string AuthenticHeadNif =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\gate2-emi2\headpart-geometry-bases\meshes\KL\High Poly Head\FemaleHead.nif";
    private const string AuthenticDiffuse =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\p12-009-authentic-render\textures\actors\character\female\femalehead.dds";
    private const string AuthenticCorDiffuse =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\sophia-live-closure-20260723\Data\Textures\!COR\head\femalehead.dds";
    private const string AuthenticCorHeadNif =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\sophia-live-closure-20260723\Data\meshes\!COR\Head\FemaleHead.nif";
    private const string AuthenticCorHairNif =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\sophia-live-closure-20260723\Data\meshes\!COR\Hair\hair01.nif";
    private const string SourceImage =
        @"K:\ExampleWorkspace\Resources\Screenshot\Sofia Fergar.png";

    public static Sha256Hash? AuthenticRenderSha256 { get; private set; }
    public static Sha256Hash? ComparisonSetSha256 { get; private set; }

    public static Task TestAuthenticSkinnedHeadpartPlacement()
    {
        SseSelectedHeadpartNifRestShape head =
            ReadAuthenticShape(
                AuthenticCorHeadNif,
                "meshes/!COR/Head/FemaleHead.nif");
        SseSelectedHeadpartNifRestShape hair =
            ReadAuthenticShape(
                AuthenticCorHairNif,
                "meshes/!COR/Hair/hair01.nif");

        Vector3 rawHeadCenter = BoundsCenter(head.RestPositions);
        Vector3 rawHairCenter = BoundsCenter(hair.RestPositions);
        Vector3 placedHeadCenter = BoundsCenter(head.RestPositions
            .Select(head.RenderPlacement.TransformPoint));
        Vector3 placedHairCenter = BoundsCenter(hair.RestPositions
            .Select(hair.RenderPlacement.TransformPoint));

        Require(Math.Abs(rawHeadCenter.Z - rawHairCenter.Z) > 100F,
            "authentic fixture no longer exposes the local-space hair discriminator");
        Require(Vector3.Distance(placedHeadCenter, placedHairCenter) < 5F,
            $"NIF skin placement did not co-locate authentic COR headparts: head={placedHeadCenter}; hair={placedHairCenter}");
        Require(Math.Abs(placedHairCenter.Z - rawHairCenter.Z) > 100F &&
                hair.RenderPlacement !=
                SseSelectedHeadpartNifPlacement.Identity,
            "authentic COR hair did not retain its non-identity bind placement");
        Require(hair.Materials is
                [{ AlphaTestEnabled: true, AlphaTestThreshold: 77 }],
            "authentic COR hair did not retain its exact NiAlphaProperty cutoff");
        return Task.CompletedTask;
    }

    public static Task TestCpuRasterizerControls()
    {
        SkyrimAssetAuthority redAuthority = Authority(
            "textures/test/red.dds", Hash('1'), 4);
        SkyrimAssetAuthority blueAuthority = Authority(
            "textures/test/blue.dds", Hash('2'), 4);
        ReferenceRenderTexture red = Texture(
            redAuthority, [255, 0, 0, 255]);
        ReferenceRenderTexture blue = Texture(
            blueAuthority, [0, 0, 255, 255]);
        ReferenceRenderShape near = Quad(
            "near", 0.25F, redAuthority, 0xFFFF_FFFF);
        ReferenceRenderShape far = Quad(
            "far", -0.25F, blueAuthority, 0xFFFF_FFFF);
        ReferenceOrthographicCamera camera = Camera();
        var renderer = new ReferencePresetCpuRenderer();

        ReferencePresetCpuRenderResult occlusion = renderer.Render(
            new ReferencePresetCpuRenderRequest(
                [far, near], [red, blue], camera, 64, 64));
        Require(occlusion.Accepted &&
                Pixel(occlusion.CanonicalRgba, 64, 32, 32).R >
                Pixel(occlusion.CanonicalRgba, 64, 32, 32).B,
            $"z-buffer did not retain the near red shape: {Codes(occlusion.Diagnostics)}");

        SkyrimAssetAuthority transparentAuthority = Authority(
            "textures/test/transparent.dds", Hash('f'), 4);
        ReferenceRenderTexture transparent = Texture(
            transparentAuthority, [255, 0, 0, 0]);
        ReferenceRenderShape cutout = Quad(
            "cutout", 0.5F, transparentAuthority, 0xFFFF_FFFF) with
        {
            Materials =
            [
                new ReferenceRenderMaterialAuthority(
                    "cutout",
                    transparentAuthority,
                    null,
                    null,
                    0xFFFF_FFFF)
                {
                    AlphaTestEnabled = true,
                    AlphaTestThreshold = 128
                }
            ]
        };
        ReferencePresetCpuRenderResult cutoutResult = renderer.Render(
            new ReferencePresetCpuRenderRequest(
                [far, cutout], [blue, transparent], camera, 64, 64));
        Require(cutoutResult.Accepted &&
                Pixel(cutoutResult.CanonicalRgba, 64, 32, 32).B >
                Pixel(cutoutResult.CanonicalRgba, 64, 32, 32).R,
            "alpha-tested hair-style cutout erased the opaque surface behind it");

        ReferenceRenderShape reversed = near with
        {
            TriangleIndices = [0, 2, 3, 0, 1, 2],
            TriangleMaterialOrdinals = [0, 0]
        };
        ReferencePresetCpuRenderResult ordered = renderer.Render(
            new ReferencePresetCpuRenderRequest(
                [near], [red], camera, 64, 64));
        ReferencePresetCpuRenderResult reordered = renderer.Render(
            new ReferencePresetCpuRenderRequest(
                [reversed], [red], camera, 64, 64));
        Require(ordered.Accepted &&
                reordered.Accepted &&
                ordered.PngSha256 == reordered.PngSha256,
            "equivalent triangle order changed deterministic PNG bytes");

        SkyrimAssetAuthority sampleAuthority = Authority(
            "textures/test/sample.dds", Hash('3'), 16);
        ReferenceRenderTexture sample = new(
            sampleAuthority,
            2,
            2,
            [
                255, 0, 0, 255,
                0, 255, 0, 255,
                0, 0, 255, 255,
                255, 255, 255, 255
            ],
            Sha([
                255, 0, 0, 255,
                0, 255, 0, 255,
                0, 0, 255, 255,
                255, 255, 255, 255
            ]));
        ReferenceRenderShape sampled = Quad(
            "sampled", 0, sampleAuthority, 0x80FF_FFFF) with
        {
            TextureCoordinates =
            [
                new Vector2(-0.25F, 1.25F),
                new Vector2(-0.25F, 1.25F),
                new Vector2(-0.25F, 1.25F),
                new Vector2(-0.25F, 1.25F)
            ]
        };
        ReferencePresetCpuRenderResult clamped = renderer.Render(
            new ReferencePresetCpuRenderRequest(
                [sampled], [sample], camera, 64, 64));
        (byte R, byte G, byte B, byte A) center =
            Pixel(clamped.CanonicalRgba, 64, 32, 32);
        Require(clamped.Accepted &&
                center.B > center.R &&
                center.B > center.G &&
                center.A == byte.MaxValue,
            "UV clamp, nearest sample, or source-over tint alpha was not exact");
        Require(PngChunkTypes(clamped.PngBytes)
                    .All(type => type is
                        "IHDR" or "IDAT" or "IEND") &&
                clamped.PngSha256 is not null,
            "renderer PNG contains non-deterministic metadata chunks");

        ReferencePresetCpuRenderResult missing = renderer.Render(
            new ReferencePresetCpuRenderRequest(
                [near], [], camera, 64, 64));
        Require(!missing.Accepted &&
                missing.Diagnostics.Any(item =>
                    item.Code == "reference-render-texture-missing"),
            "renderer substituted a texture when exact content was missing");
        ReferenceRenderTexture wrong = red with
        {
            Authority = redAuthority with
            {
                ContentSha256 = Hash('9')
            }
        };
        ReferencePresetCpuRenderResult mismatched = renderer.Render(
            new ReferencePresetCpuRenderRequest(
                [near], [wrong], camera, 64, 64));
        Require(!mismatched.Accepted &&
                mismatched.Diagnostics.Any(item =>
                    item.Code == "reference-render-texture-authority"),
            "renderer accepted the wrong texture content authority");
        return Task.CompletedTask;
    }

    public static async Task TestAuthenticFourKTextureProfile()
    {
        var labRoot = new WorkspacePath(LabRoot);
        var source = new WorkspacePath(AuthenticCorDiffuse);
        FaceTintTextureDecodeResult faceTint =
            await new InProcessDdsTextureDecoder(labRoot)
                .DecodeAsync(source, CancellationToken.None);
        Require(!faceTint.Decoded,
            "the bounded FaceTint composition profile unexpectedly admitted the authentic 4096x4096 source");

        FaceTintTextureDecodeResult preview =
            await new InProcessDdsTextureDecoder(
                    labRoot,
                    InProcessDdsTextureDecodeProfile.ReferencePreview)
                .DecodeAsync(source, CancellationToken.None);
        Require(preview.Decoded &&
                preview.Width == 4096 &&
                preview.Height == 4096 &&
                preview.Bytes is { Length: 4096 * 4096 * 4 } &&
                preview.SourceSha256 ==
                Sha(await File.ReadAllBytesAsync(AuthenticCorDiffuse)),
            $"the reference-preview profile did not decode the exact authentic COR DDS: {Codes(preview.Diagnostics)}");
    }

    public static async Task TestAuthenticRenderAndComparison()
    {
        (ReferencePresetRenderInput input,
            ReferencePresetResourceSnapshot snapshot,
            ReviewedReferencePresetDesign design,
            ReferencePresetIntake intake) =
            await AuthenticFixture();
        ReferencePresetCpuRenderResult authentic =
            new ReferencePresetCpuRenderer().Render(
                new ReferencePresetCpuRenderRequest(
                    input.Shapes,
                    input.Textures,
                    input.Cameras.Single(),
                    256,
                    256));
        Require(authentic.Accepted &&
                authentic.PngSha256 is not null &&
                NonBackgroundPixels(authentic.CanonicalRgba) > 1000,
            $"authentic copied HPH NIF/DDS render failed: accepted={authentic.Accepted}; nonBackground={NonBackgroundPixels(authentic.CanonicalRgba)}; diagnostics={Codes(authentic.Diagnostics)}");
        AuthenticRenderSha256 = authentic.PngSha256;
        ReferencePresetCpuRenderResult authenticBaseline =
            new ReferencePresetCpuRenderer().Render(
                new ReferencePresetCpuRenderRequest(
                    input.Shapes,
                    input.Textures,
                    input.Cameras.Single(),
                    900,
                    900));
        const string retainedPreChangeBaselineSha256 =
            "0B6487D153A1603CA12E0BEA73F04259FBB0DBFB82F0520B9054E0F7038AF1F9";
        Require(authenticBaseline.Accepted &&
                string.Equals(
                    authenticBaseline.PngSha256?.Value,
                    retainedPreChangeBaselineSha256,
                    StringComparison.OrdinalIgnoreCase),
            "the authentic 900x900 baseline preview changed bytes: " +
            $"expected={retainedPreChangeBaselineSha256}; " +
            $"actual={authenticBaseline.PngSha256?.Value ?? "missing"}; " +
            $"diagnostics={Codes(authenticBaseline.Diagnostics)}");

        var solver = new ReferenceRaceMenuPresetSolverResult(
            ImmutableDictionary<string, double>.Empty,
            ImmutableDictionary<string, double>.Empty,
            [],
            [
                new ReferencePresetResidual(
                    ReferenceImageViewRole.Front,
                    ReferenceSemanticAnchorKind.NoseTip,
                    0.5, 0.5, 0.5, 0.5, 1, 0)
            ],
            [],
            ReferencePresetAuthoringRules.SolverIterations,
            0,
            Hash('8'),
            []);
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(LabRoot),
            new WorkspacePath(@"F:\ExampleGame"));
        var comparison = new ReferencePresetComparisonService(
            policy,
            new WorkspacePath(LabRoot),
            new SseFaceMorphPlanBuilder(),
            new SseFaceMorphEvaluator(),
            new ReferencePresetCpuRenderer());
        string parent = Path.Combine(
            LabRoot,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "NpcManager.ReferencePreset.Tests");
        string firstRoot = Path.Combine(
            parent,
            $"scratch-comparison-a-{Environment.ProcessId}-{Guid.NewGuid():N}");
        string secondRoot = Path.Combine(
            parent,
            $"scratch-comparison-b-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            ReferencePresetComparisonResult first =
                await comparison.RenderAsync(
                    new ReferencePresetComparisonRequest(
                        input,
                        design,
                        solver,
                        new WorkspacePath(firstRoot))
                    {
                        Snapshot = snapshot,
                        Intake = intake
                    },
                    CancellationToken.None);
            ReferencePresetComparisonResult second =
                await comparison.RenderAsync(
                    new ReferencePresetComparisonRequest(
                        input,
                        design,
                        solver,
                        new WorkspacePath(secondRoot))
                    {
                        Snapshot = snapshot,
                        Intake = intake
                    },
                    CancellationToken.None);
            Require(!first.Diagnostics.Any(item =>
                        item.Severity == DiagnosticSeverity.Error) &&
                    first.Artifacts.Length == 4 &&
                    File.Exists(Path.Combine(firstRoot, "front.png")) &&
                    File.Exists(Path.Combine(
                        firstRoot, "left-not-observed.json")) &&
                    File.Exists(Path.Combine(
                        firstRoot, "right-not-observed.json")) &&
                    File.Exists(Path.Combine(
                        firstRoot, "comparison-sheet.png")),
                $"comparison artifact closure failed: {Codes(first.Diagnostics)}");
            Dictionary<string, Sha256Hash> firstHashes =
                first.Artifacts.ToDictionary(
                    item => Path.GetFileName(item.Path.Value),
                    item => item.ContentSha256,
                    StringComparer.Ordinal);
            Dictionary<string, Sha256Hash> secondHashes =
                second.Artifacts.ToDictionary(
                    item => Path.GetFileName(item.Path.Value),
                    item => item.ContentSha256,
                    StringComparer.Ordinal);
            Require(firstHashes.Count == secondHashes.Count &&
                    firstHashes.All(item =>
                        secondHashes.TryGetValue(
                            item.Key, out Sha256Hash hash) &&
                        hash == item.Value),
                "separate authentic comparison roots changed content hashes");
            byte[] comparisonIdentity =
                System.Text.Encoding.UTF8.GetBytes(string.Join(
                    "\n",
                    firstHashes.OrderBy(item => item.Key,
                            StringComparer.Ordinal)
                        .Select(item =>
                            $"{item.Key}={item.Value.Value}")));
            ComparisonSetSha256 = Sha(comparisonIdentity);
        }
        finally
        {
            if (Directory.Exists(firstRoot))
                Directory.Delete(firstRoot, recursive: true);
            if (Directory.Exists(secondRoot))
                Directory.Delete(secondRoot, recursive: true);
        }
    }

    public static async Task TestShapeScopedSolvedMorphRouting()
    {
        (ReferencePresetRenderInput input,
            ReferencePresetResourceSnapshot snapshot,
            ReviewedReferencePresetDesign design,
            ReferencePresetIntake intake) =
            await AuthenticFixture();
        ReferenceRenderShape head = input.Shapes.Single();
        ReferenceRenderShape accessory = head with
        {
            NifIdentity = "meshes/test/accessory.nif",
            ShapeIdentity = "Accessory"
        };
        var routedInput = new ReferencePresetRenderInput(
            [head, accessory],
            input.Textures,
            input.Cameras,
            Hash('1'));
        ImmutableArray<Vector3> rest =
            head.SourceRestPositions.IsDefaultOrEmpty
                ? head.Positions
                : head.SourceRestPositions;
        var headTri = new SseTriHeadDocument(
            new AssetPath("meshes/test/head.tri"),
            Hash('2'),
            rest.Length,
            0,
            0,
            0,
            rest,
            [
                new SseTriHeadMorph(
                    "Brow_BrowArch",
                    SseTriHeadMorphEncoding.DenseInt16,
                    1,
                    [
                        new SseTriHeadVertexDelta(
                            0,
                            new Vector3(0.01F, 0, 0))
                    ])
            ]);
        var accessoryTri = new SseTriHeadDocument(
            new AssetPath("meshes/test/accessory.tri"),
            Hash('3'),
            rest.Length,
            0,
            0,
            0,
            rest,
            []);
        SkyrimFaceMorphPlanBuildRequest Template(
            SseTriHeadDocument tri) =>
            new(
                rest.Length,
                "NordRace",
                true,
                new SkyrimFaceMorphSnapshot(
                    Enumerable.Repeat(0F, 18)
                        .ToImmutableArray(),
                    float.MaxValue,
                    Enumerable.Repeat(uint.MaxValue, 4)
                        .ToImmutableArray(),
                    true,
                    true),
                [],
                [],
                [],
                100,
                new SkyrimRaceMenuSliderCatalog([], []),
                new SkyrimFaceMorphTriSource(
                    SkyrimFaceMorphTriRole.Race,
                    tri),
                null,
                null,
                [],
                true);
        snapshot = snapshot with
        {
            MorphBases =
            [
                new ReferenceFaceMorphShapeBasis(
                    head.NifIdentity,
                    head.ShapeIdentity,
                    Template(headTri)),
                new ReferenceFaceMorphShapeBasis(
                    accessory.NifIdentity,
                    accessory.ShapeIdentity,
                    Template(accessoryTri))
            ]
        };
        var solver = new ReferenceRaceMenuPresetSolverResult(
            ImmutableDictionary<string, double>.Empty,
            ImmutableDictionary<string, double>.Empty
                .Add("Brow_BrowArch", 0.25),
            [],
            [],
            [],
            ReferencePresetAuthoringRules.SolverIterations,
            0,
            Hash('4'),
            []);
        var policy = new KOnlyWorkspacePolicy(
            new WorkspacePath(LabRoot),
            new WorkspacePath(@"F:\ExampleGame"));
        string output = Path.Combine(
            LabRoot,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "NpcManager.ReferencePreset.Tests",
            $"scratch-shape-routing-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            ReferencePresetComparisonResult result =
                await new ReferencePresetComparisonService(
                        policy,
                        new WorkspacePath(LabRoot),
                        new SseFaceMorphPlanBuilder(),
                        new SseFaceMorphEvaluator(),
                        new ReferencePresetCpuRenderer())
                    .RenderAsync(
                        new ReferencePresetComparisonRequest(
                            routedInput,
                            design,
                            solver,
                            new WorkspacePath(output))
                        {
                            Snapshot = snapshot,
                            Intake = intake
                        },
                        CancellationToken.None);
            Require(!result.Diagnostics.Any(item =>
                        item.Severity ==
                        DiagnosticSeverity.Error) &&
                    result.Artifacts.Length == 4,
                "shape-scoped solved morph routing failed: " +
                Codes(result.Diagnostics));
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    private static async Task<(
        ReferencePresetRenderInput Input,
        ReferencePresetResourceSnapshot Snapshot,
        ReviewedReferencePresetDesign Design,
        ReferencePresetIntake Intake)> AuthenticFixture()
    {
        byte[] nifBytes = await File.ReadAllBytesAsync(AuthenticHeadNif);
        SseSelectedHeadpartNifGeometryReadResult nifResult =
            new SseSelectedHeadpartNifGeometryReader().Read(
                new SseSelectedHeadpartNifGeometryReadRequest(
                    new AssetPath(
                        "meshes/KL/High Poly Head/FemaleHead.nif"),
                    Sha(nifBytes),
                    ImmutableArray.CreateRange(nifBytes)));
        Require(nifResult.Accepted &&
                nifResult.Document is not null,
            "authentic render NIF parse failed");
        SseSelectedHeadpartNifGeometryDocument nif =
            nifResult.Document!;
        SseSelectedHeadpartNifRestShape sourceShape =
            nif.Shapes.Single();
        FaceTintTextureDecodeResult decoded =
            await new InProcessDdsTextureDecoder(
                    new WorkspacePath(LabRoot))
                .DecodeAsync(
                    new WorkspacePath(AuthenticDiffuse),
                    CancellationToken.None);
        Require(decoded.Decoded &&
                decoded.Bytes is not null &&
                decoded.SourceSha256 is not null,
            $"authentic DDS decode failed: {Codes(decoded.Diagnostics)}");
        byte[] rgba = new byte[decoded.Bytes!.Length];
        for (var index = 0; index < rgba.Length; index += 4)
        {
            rgba[index] = decoded.Bytes[index + 2];
            rgba[index + 1] = decoded.Bytes[index + 1];
            rgba[index + 2] = decoded.Bytes[index];
            rgba[index + 3] = decoded.Bytes[index + 3];
        }
        Sha256Hash sourceSha256 = decoded.SourceSha256 ??
            throw new InvalidOperationException(
                "decoded source hash was absent");
        SkyrimAssetAuthority diffuse = Authority(
            "textures/actors/character/female/femalehead.dds",
            sourceSha256,
            new FileInfo(AuthenticDiffuse).Length);
        var material = new ReferenceRenderMaterialAuthority(
            "authentic-hph-face",
            diffuse,
            null,
            null,
            0xFFFF_FFFF);
        var shape = new ReferenceRenderShape(
            nif.SourcePath.Value,
            sourceShape.Name,
            sourceShape.RestPositions,
            sourceShape.TriangleIndices,
            sourceShape.TextureCoordinates,
            sourceShape.Normals,
            Enumerable.Repeat(
                    0,
                    sourceShape.TriangleIndices.Length / 3)
                .ToImmutableArray(),
            Hash('d'))
        {
            NifSha256 = nif.SourceSha256,
            TopologySha256 = sourceShape.TopologySha256,
            RestPositionsSha256 = sourceShape.PackedPositionSha256,
            Materials = [material]
        };
        ReferenceRenderTexture texture = new(
            diffuse,
            decoded.Width,
            decoded.Height,
            ImmutableArray.CreateRange(rgba),
            Sha(rgba));
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        foreach (Vector3 position in shape.Positions)
        {
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }
        Vector3 extents = maximum - minimum;
        double halfWidth = Math.Max(extents.X, extents.Y) * 0.60;
        double halfHeight = Math.Max(extents.Z, extents.Y) * 0.60;
        double depth =
            Math.Max(Math.Max(extents.X, extents.Y), extents.Z) * 4;
        Vector3 centerBounds = (minimum + maximum) * 0.5F;
        var camera = new ReferenceOrthographicCamera(
            ReferenceImageViewRole.Front,
            0,
            0,
            centerBounds.X - halfWidth,
            centerBounds.X + halfWidth,
            centerBounds.Z - halfHeight,
            centerBounds.Z + halfHeight,
            centerBounds.Y - depth,
            centerBounds.Y + depth,
            Hash('e'));
        var input = new ReferencePresetRenderInput(
            [shape], [texture], [camera], Hash('f'));
        int a = sourceShape.TriangleIndices[0];
        int b = sourceShape.TriangleIndices[1];
        int c = sourceShape.TriangleIndices[2];
        var anchor = new ReferenceSemanticAnchor(
            ReferenceSemanticAnchorKind.NoseTip,
            1,
            0.5,
            0.5,
            1,
            true,
            ReferenceAnchorReviewState.Accepted);
        var view = new ReviewedReferenceView(
            "front",
            ReferenceImageViewRole.Front,
            Hash('7'),
            1,
            0,
            true,
            [anchor]);
        FormReference face = new(
            new PluginName("High Poly Head.esm"),
            new FormId(0x000A06));
        var selection = new ReferencePresetCatalogSelection(
            true, face, face, face, face, face, []);
        var binding = new ReferenceMeshAnchorBinding(
            view.ViewRole,
            anchor.Anchor,
            nif.SourcePath.Value,
            sourceShape.Name,
            nif.SourceSha256,
            sourceShape.TopologySha256,
            sourceShape.PackedPositionSha256,
            camera.CameraSha256,
            input.InputSha256,
            0,
            a, b, c,
            1.0 / 3,
            1.0 / 3,
            1.0 / 3);
        var design = new ReviewedReferencePresetDesign(
            1,
            ReferencePresetAuthorityKind.ReviewedDesign,
            Hash('6'),
            true,
            [view],
            [],
            [],
            selection,
            [binding]);
        PresetDocument baseline =
            ReferencePresetSolverTests.CreateEmptyPresetForTests();
        var snapshot = new ReferencePresetResourceSnapshot(
            1,
            "authentic-render",
            Hash('6'),
            baseline.SourceHash,
            baseline,
            selection,
            [diffuse],
            [],
            [],
            Hash('5'),
            Hash('4'),
            []);
        byte[] sourceBytes = await File.ReadAllBytesAsync(SourceImage);
        var race = new FormReference(
            new PluginName("Skyrim.esm"),
            new FormId(0x013746));
        var intake = new ReferencePresetIntake(
            1,
            "authentic-render",
            "Authentic Render",
            race,
            NpcSex.Female,
            50,
            "high-poly-head",
            new WorkspacePath(
                @"K:\ExampleWorkspace\projects\Emi2FreshBuild\01-source-copies\fresh-export-drop\emi2-neutral.jslot"),
            baseline.SourceHash,
            string.Empty,
            [
                new ReferenceImageAuthority(
                    "front",
                    new WorkspacePath(SourceImage),
                    Sha(sourceBytes),
                    sourceBytes.Length,
                    ReferenceImageViewRole.Front)
            ],
            new RaceMenuPresetTarget(
                "authentic-render",
                race,
                NpcSex.Female,
                new WorkspacePath(
                    @"K:\ExampleWorkspace\projects\NpcManagerReimplementation"),
                []));
        return (input, snapshot, design, intake);
    }

    private static SseSelectedHeadpartNifRestShape ReadAuthenticShape(
        string physicalPath,
        string assetPath)
    {
        byte[] bytes = File.ReadAllBytes(physicalPath);
        SseSelectedHeadpartNifGeometryReadResult result =
            new SseSelectedHeadpartNifGeometryReader().Read(
                new SseSelectedHeadpartNifGeometryReadRequest(
                    new AssetPath(assetPath),
                    Sha(bytes),
                    ImmutableArray.CreateRange(bytes)));
        Require(result.Accepted && result.Document?.Shapes.Length == 1,
            $"authentic NIF placement fixture was refused: {Codes(result.Diagnostics)}");
        return result.Document!.Shapes.Single();
    }

    private static Vector3 BoundsCenter(IEnumerable<Vector3> positions)
    {
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        foreach (Vector3 value in positions)
        {
            minimum = Vector3.Min(minimum, value);
            maximum = Vector3.Max(maximum, value);
        }
        return (minimum + maximum) * 0.5F;
    }

    private static ReferenceRenderShape Quad(
        string identity,
        float y,
        SkyrimAssetAuthority diffuse,
        uint tint) =>
        new(
            $"meshes/{identity}.nif",
            identity,
            [
                new Vector3(-0.8F, y, -0.8F),
                new Vector3(0.8F, y, -0.8F),
                new Vector3(0.8F, y, 0.8F),
                new Vector3(-0.8F, y, 0.8F)
            ],
            [0, 1, 2, 0, 2, 3],
            [
                Vector2.Zero,
                Vector2.UnitX,
                Vector2.One,
                Vector2.UnitY
            ],
            [
                -Vector3.UnitY,
                -Vector3.UnitY,
                -Vector3.UnitY,
                -Vector3.UnitY
            ],
            [0, 0],
            Hash('d'))
        {
            NifSha256 = Hash('a'),
            TopologySha256 = Hash('b'),
            RestPositionsSha256 = Hash('c'),
            Materials =
            [
                new ReferenceRenderMaterialAuthority(
                    identity, diffuse, null, null, tint)
            ]
        };

    private static ReferenceOrthographicCamera Camera() => new(
        ReferenceImageViewRole.Front,
        0, 0, -1, 1, -1, 1, -1, 1, Hash('e'));

    private static SkyrimAssetAuthority Authority(
        string path,
        Sha256Hash content,
        long length) =>
        new(
            "controlled",
            AssetProviderKind.Loose,
            new WorkspacePath(Path.Combine(
                LabRoot,
                "projects",
                "NpcManagerReimplementation",
                "01-source-copies",
                path.Replace('/', Path.DirectorySeparatorChar))),
            Hash('a'),
            new AssetPath(path),
            length,
            content);

    private static ReferenceRenderTexture Texture(
        SkyrimAssetAuthority authority,
        ImmutableArray<byte> rgba) =>
        new(authority, 1, 1, rgba, Sha(rgba.AsSpan()));

    private static (byte R, byte G, byte B, byte A) Pixel(
        ImmutableArray<byte> rgba,
        int width,
        int x,
        int y)
    {
        int offset = (y * width + x) * 4;
        return (rgba[offset], rgba[offset + 1],
            rgba[offset + 2], rgba[offset + 3]);
    }

    private static IEnumerable<string> PngChunkTypes(
        ImmutableArray<byte> png)
    {
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            int length =
                (png[offset] << 24) |
                (png[offset + 1] << 16) |
                (png[offset + 2] << 8) |
                png[offset + 3];
            yield return System.Text.Encoding.ASCII.GetString(
                png.AsSpan(offset + 4, 4));
            offset += checked(length + 12);
        }
    }

    private static int NonBackgroundPixels(
        ImmutableArray<byte> rgba)
    {
        var count = 0;
        for (var index = 0; index < rgba.Length; index += 4)
        {
            if (rgba[index] != 32 ||
                rgba[index + 1] != 32 ||
                rgba[index + 2] != 32)
                count++;
        }
        return count;
    }

    private static Sha256Hash Hash(char value) =>
        ReferenceMeshAnchorBindingTests.Hash(value);

    private static Sha256Hash Sha(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void Require(bool condition, string message) =>
        ReferenceMeshAnchorBindingTests.Require(condition, message);

    private static string Codes(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(',', diagnostics.Select(item => item.Code));
}
