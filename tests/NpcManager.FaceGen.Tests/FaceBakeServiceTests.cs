using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Infrastructure;

namespace NpcManager.FaceGen.Tests;

internal static partial class Program
{
    private static void TestMultiShapeFaceBake()
    {
        SkyrimRaceMenuFaceBakeRequest request = CreateSyntheticBakeRequest();
        SkyrimRaceMenuFaceBakeResult result = new SseRaceMenuFaceBakeService().Bake(request);
        Assert(result.Accepted && result.Shapes.Length == 4,
            "Valid multi-shape bake was refused: " + Format(result.Diagnostics));
        Assert(HasCode(result.Diagnostics, "sse-face-bake-extension-unavailable"),
            "Omitted declared extension did not produce its availability diagnostic.");

        SkyrimRaceMenuFaceBakeShapeOutput head = result.Shapes[0];
        SkyrimRaceMenuFaceBakeShapeOutput mouth = result.Shapes[1];
        SkyrimRaceMenuFaceBakeShapeOutput hair = result.Shapes[2];
        SkyrimRaceMenuFaceBakeShapeOutput hairHighlight = result.Shapes[3];
        Assert(head.CarrierShapeName == "Head" && mouth.CarrierShapeName == "Mouth" &&
               hair.CarrierShapeName == "Hair" &&
               hairHighlight.CarrierShapeName == "HairHighlight",
            "Bake output order drifted from the explicit carrier binding order.");
        Assert(head.FinalPositions.Single() == new Vector3(12F, 20F, 30F),
            "Head output did not use the caller-supplied carrier base before custom TRI deltas.");
        Assert(mouth.FinalPositions.Single() == new Vector3(-5F, -4F, -7F),
            "Mouth output did not use the caller-supplied carrier base before sculpt deltas.");
        Assert(hair.ChargenMorphHost is null &&
               hair.FinalPositions.Single() == new Vector3(1F, 2F, 3F) &&
               hair.MergedTriOrder.IsEmpty && hair.TriEvidence.IsEmpty,
            "Rest-only hair was not preserved without forcing face custom/sculpt resolution.");
        Assert(hairHighlight.FinalPositions.Single() == new Vector3(1F, 4F, 3F),
            "One shared mesh-TRI sculpt host was not applied to both compatible carrier shapes.");
        Assert(head.MergedTriOrder.Select(path => path.Value).SequenceEqual(
        [
            "meshes/headchargen.tri",
            "meshes/actors/character/FaceGenMorphs/morphs/synthetic/head-a.tri",
            "meshes/actors/character/FaceGenMorphs/morphs/synthetic/head-b.tri"
        ], StringComparer.OrdinalIgnoreCase),
            "Ordered available extension subset was not preserved in the head plan.");
        Assert(head.FinalPositionSha256 == PositionHash(head.FinalPositions) &&
               mouth.FinalPositionSha256 == PositionHash(mouth.FinalPositions) &&
               hair.FinalPositionSha256 == PositionHash(hair.FinalPositions),
            "Bake output position hashes do not bind the returned float32 XYZ arrays.");
    }

    private static void TestMultiShapeFaceBakeRefusals()
    {
        SkyrimRaceMenuFaceBakeRequest request = CreateSyntheticBakeRequest();

        AssertBakeRefused(request with
        {
            CarrierShapes = [.. request.CarrierShapes, request.CarrierShapes[0]]
        }, "sse-face-bake-shape-duplicate");

        AssertBakeRefused(request with
        {
            ShapeTriInputs = [request.ShapeTriInputs[0]]
        }, "sse-face-bake-shape-missing");

        SkyrimRaceMenuFaceBakeShapeTriInputs head = request.ShapeTriInputs[0];
        AssertBakeRefused(request with
        {
            ShapeTriInputs =
            [
                head with
                {
                    ChargenMorphTri = head.ChargenMorphTri! with
                    {
                        SourcePath = new AssetPath("meshes/wrongchargen.tri")
                    }
                },
                request.ShapeTriInputs[1],
                request.ShapeTriInputs[2],
                request.ShapeTriInputs[3]
            ]
        }, "sse-face-bake-host-mismatch");

        AssertBakeRefused(request with
        {
            ShapeTriInputs =
            [
                head with
                {
                    ChargenMorphTri = SyntheticTri("meshes/headchargen.tri",
                        [Vector3.Zero, Vector3.One], ("Nonempty", (short)1,
                            (short)0, (short)0))
                },
                request.ShapeTriInputs[1],
                request.ShapeTriInputs[2],
                request.ShapeTriInputs[3]
            ]
        }, "sse-face-plan-source-topology");

        AssertBakeRefused(request with
        {
            ShapeTriInputs =
            [
                head with
                {
                    ExtendedMorphTris =
                    [
                        SyntheticTri(
                            "meshes/actors/character/FaceGenMorphs/morphs/synthetic/rogue.tri",
                            [Vector3.Zero])
                    ]
                },
                request.ShapeTriInputs[1],
                request.ShapeTriInputs[2],
                request.ShapeTriInputs[3]
            ]
        }, "sse-face-bake-extension-unexpected");

        AssertBakeRefused(request with
        {
            ShapeTriInputs =
            [
                head with { ExtendedMorphTris = [.. head.ExtendedMorphTris.Reverse()] },
                request.ShapeTriInputs[1],
                request.ShapeTriInputs[2],
                request.ShapeTriInputs[3]
            ]
        }, "sse-face-bake-extension-order");

        AssertBakeRefused(request with
        {
            ShapeTriInputs =
            [
                head with
                {
                    ExtendedMorphTris =
                    [head.ExtendedMorphTris[0], head.ExtendedMorphTris[0]]
                },
                request.ShapeTriInputs[1],
                request.ShapeTriInputs[2],
                request.ShapeTriInputs[3]
            ]
        }, "sse-face-bake-tri-duplicate");

        AssertBakeRefused(request with
        {
            ShapeTriInputs =
            [
                head with { ChargenMorphTri = null },
                request.ShapeTriInputs[1],
                request.ShapeTriInputs[2],
                request.ShapeTriInputs[3]
            ]
        }, "sse-face-bake-chargen-missing");

        SkyrimRaceMenuFaceBakeShapeTriInputs hair = request.ShapeTriInputs[2];
        AssertBakeRefused(request with
        {
            ShapeTriInputs =
            [
                head,
                request.ShapeTriInputs[1],
                hair with
                {
                    ExtendedMorphTris =
                    [SyntheticTri("meshes/hostless-extension.tri", [Vector3.Zero])]
                },
                request.ShapeTriInputs[3]
            ]
        }, "sse-face-bake-hostless-source");

        AssertBakeRefused(request with
        {
            CustomMorphs = [new SkyrimRaceMenuCustomMorphValue("NeverAnywhere", 1F)]
        }, "sse-face-bake-custom-unresolved");
        var unresolved = new SseRaceMenuFaceBakeService().Bake(request with
        {
            CustomMorphs = [new SkyrimRaceMenuCustomMorphValue("NeverAnywhere", 1F)]
        });
        string unresolvedMessage = unresolved.Diagnostics.Single(d => d.Code == "sse-face-bake-custom-unresolved").Message;
        Assert(request.CatalogRequest.Assets.All(asset => unresolvedMessage.Contains(asset.Path.Value, StringComparison.Ordinal)) &&
            unresolvedMessage.Contains(request.MorphRaceEditorId, StringComparison.Ordinal) &&
            unresolvedMessage.Contains("provider", StringComparison.OrdinalIgnoreCase),
            "Custom-morph refusal must name inspected catalog files, race and missing provider binding: " + unresolvedMessage);


        AssertBakeRefused(request with
        {
            ExplicitUnavailableExtendedTris = ImmutableArray<AssetPath>.Empty
        }, "sse-face-bake-unavailable-extension-closure");

        AssertBakeRefused(request with
        {
            ExplicitUnavailableExtendedTris =
            [new AssetPath("meshes/actors/character/FaceGenMorphs/morphs/synthetic/head-a.tri")]
        }, "sse-face-bake-unavailable-extension-closure");

        AssertBakeRefused(request with
        {
            SculptParts =
            [
                new RaceMenuSculptPart("meshes/absentchargen.tri", 1,
                    [new RaceMenuSculptVertex(0, 1F, 0F, 0F)], true, true)
            ]
        }, "sse-face-bake-sculpt-unresolved");

        AssertBakeRefused(request with
        {
            CustomMorphs =
            [new SkyrimRaceMenuCustomMorphValue("HeadOnly", float.MaxValue)]
        }, "sse-face-evaluate-overflow");

        SkyrimRaceMenuFaceBakeCarrierShapeBinding headBinding = request.CarrierShapes[0];
        AssertBakeRefused(request with
        {
            CarrierShapes =
            [
                headBinding with { ExpectedBasePositionSha256 = FixtureHash },
                request.CarrierShapes[1],
                request.CarrierShapes[2],
                request.CarrierShapes[3]
            ]
        }, "sse-face-bake-carrier-position-hash");
    }

    private static void TestCapturedEmi2FourShapeFaceBake()
    {
        string projectRoot = FindProjectRoot();
        string evidenceRoot = Path.Combine(projectRoot, "01-source-copies", "gate2-emi2");
        Dictionary<string, BoundFile> baseManifest = ReadBoundFileManifest(
            Path.Combine(projectRoot, "05-reports", "gate2-face-bake-base-sha256.csv"));
        Dictionary<string, BoundFile> providerManifest = ReadBoundFileManifest(
            Path.Combine(projectRoot, "05-reports",
                "gate2-active-morph-provider-closure-sha256.csv"));

        SkyrimRaceMenuCatalogParseRequest catalogRequest = new(
        [
            new PluginName("Expressive Facegen Morphs.esl"),
            new PluginName("High Poly Head.esm"),
            new PluginName("RaceMenu.esp")
        ],
        [
            BoundCatalogAsset(evidenceRoot,
                "morph-configs/Expressive Facegen Morphs SE/meshes/actors/character/facegenmorphs/Expressive Facegen Morphs.esl/morphs.ini",
                "meshes/actors/character/FaceGenMorphs/Expressive Facegen Morphs.esl/morphs.ini",
                "631f62c92f371169b3f987f74af94b911464a6c56238e66a73f84e529fc1e406"),
            BoundCatalogAsset(evidenceRoot,
                "morph-configs/High Poly Head SE/meshes/actors/character/facegenmorphs/high poly head.esm/morphs.ini",
                "meshes/actors/character/FaceGenMorphs/High Poly Head.esm/morphs.ini",
                "2c959526b7acb870d7f1b1b04741d3e4ce59a3da4be761a74ce218f54f7fc5c5"),
            BoundCatalogAsset(evidenceRoot,
                "morph-configs/RaceMenu/meshes/actors/character/facegenmorphs/racemenu.esp/morphs.ini",
                "meshes/actors/character/FaceGenMorphs/RaceMenu.esp/morphs.ini",
                "bc896ac5d69f9ff7cfc850dea835730c4cbb5621008937dbd6d803b06743bad0")
        ]);

        RealShapeSpec[] specs =
        [
            new("00KLH_FemaleHeadNord",
                "face-bake-bases/meshes/KL/High Poly Head/FemaleHeadRaces.tri",
                "face-bake-bases/meshes/KL/High Poly Head/FemaleHeadCharGen.tri",
                "face-bake-bases/meshes/KL/High Poly Head/FemaleHead.tri",
            [
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/femalehead_cme.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/femalehead_ece.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/femalehead_rans.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/femalehead_nuska.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/femalehead_extra.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/femalehead_expr.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/femalehead_efm.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/femalehead_race.tri"
            ], 11),
            new("KoralinaEyebrowsF02",
                "face-bake-bases/meshes/KL/High Poly Head/FaceParts/FemaleHeadBrowsRace.tri",
                "face-bake-bases/meshes/KL/High Poly Head/FaceParts/FemaleHeadBrowsCharGen.tri",
                "face-bake-bases/meshes/KL/High Poly Head/FaceParts/FemaleHeadBrows.tri",
            [
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/faceparts/femaleheadbrows_cme.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/faceparts/femaleheadbrows_ece.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/faceparts/femaleheadbrows_rans.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/faceparts/femaleheadbrows_nuska.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/faceparts/femaleheadbrows_extra.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/faceparts/femaleheadbrows_expr.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/faceparts/femaleheadbrows_efm.tri",
                "morph-providers/High Poly Head SE/meshes/actors/character/facegenmorphs/morphs/kl/faceparts/femaleheadbrows_race.tri"
            ], 11),
            new("MJBFemaleEyesHumanGreen04", null,
                "face-bake-bases/meshes/Actors/Character/Character Assets/EyesFemaleChargen.tri",
                "face-bake-bases/meshes/Actors/Character/Character Assets/EyesFemale.tri",
            [
                "morph-providers/Expressive Facegen Morphs SE/meshes/actors/character/facegenmorphs/morphs/EFM/Female/EyesFemale.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/ece/CME_EyesFemale.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/ece/RANs_EyesFemale.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/ece/ECEEyesFemale.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/nuska/eyesfemalechargen.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/extra/RM_eyesfemalechargen.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/expressions/EXPR_eyesfemale.tri"
            ], 9),
            new("FemaleMouthHumanoidDefault", null,
                "face-bake-bases/meshes/Actors/Character/Character Assets/Mouth/MouthHumanFChargen.tri",
                "face-bake-bases/meshes/Actors/Character/Character Assets/Mouth/MouthHumanF.tri",
            [
                "morph-providers/Expressive Facegen Morphs SE/meshes/actors/character/facegenmorphs/morphs/EFM/Female/MouthHumanF.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/ece/ECEMouthhumanFemale.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/extra/mouth/RM_mouthhumanfchargen.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/ece/CME_MouthhumanFemale.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/ece/RANs_MouthhumanFemale.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/expressions/mouth/EXPR_mouthhumanf.tri",
                "morph-providers/RaceMenu/meshes/actors/character/facegenmorphs/morphs/spg/SPG_MouthHumanFemale.tri"
            ], 9)
        ];

        ImmutableArray<SkyrimRaceMenuFaceBakeCarrierShapeBinding>.Builder bindings =
            ImmutableArray.CreateBuilder<SkyrimRaceMenuFaceBakeCarrierShapeBinding>(specs.Length);
        ImmutableArray<SkyrimRaceMenuFaceBakeShapeTriInputs>.Builder inputs =
            ImmutableArray.CreateBuilder<SkyrimRaceMenuFaceBakeShapeTriInputs>(specs.Length);
        Dictionary<string, ImmutableArray<Vector3>> callerBases =
            new(StringComparer.OrdinalIgnoreCase);

        for (int shapeIndex = 0; shapeIndex < specs.Length; shapeIndex++)
        {
            RealShapeSpec spec = specs[shapeIndex];
            SseTriHeadReadRequest chargen = BoundTri(evidenceRoot, baseManifest,
                spec.ChargenRelativePath);
            SseTriHeadReadRequest mesh = BoundTri(evidenceRoot, baseManifest,
                spec.MeshRelativePath);
            SseTriHeadDocument chargenDocument = RequireTriDocument(chargen);
            SseTriHeadDocument meshDocument = RequireTriDocument(mesh);
            Assert(chargenDocument.VertexCount == meshDocument.VertexCount,
                $"Real base TRI topology mismatch for '{spec.ShapeName}'.");

            ImmutableArray<Vector3> callerBase = CreateCallerSentinelBase(
                chargenDocument.VertexCount, shapeIndex);
            Assert(callerBase[0] != meshDocument.BaseVertices[0],
                $"Caller base sentinel accidentally equals TRI rest data for '{spec.ShapeName}'.");
            callerBases.Add(spec.ShapeName, callerBase);

            bindings.Add(new SkyrimRaceMenuFaceBakeCarrierShapeBinding(
                spec.ShapeName,
                chargen.SourcePath,
                chargenDocument.VertexCount,
                Hash(Encoding.UTF8.GetBytes(
                    $"{spec.ShapeName}|topology|{chargenDocument.VertexCount}")),
                PositionHash(callerBase),
                callerBase));
            inputs.Add(new SkyrimRaceMenuFaceBakeShapeTriInputs(
                spec.ShapeName,
                spec.RaceRelativePath is null
                    ? null
                    : BoundTri(evidenceRoot, baseManifest, spec.RaceRelativePath),
                chargen,
                mesh,
                [.. spec.ExtensionRelativePaths.Select(path =>
                    BoundTri(evidenceRoot, providerManifest, path))]));
        }

        SkyrimRaceMenuFaceBakeRequest request = new(
            "ContractTestMorphRace",
            true,
            Native([], 0F, []),
            ImmutableArray<string>.Empty,
            ImmutableArray<SkyrimRaceMenuCustomMorphValue>.Empty,
            ImmutableArray<RaceMenuSculptPart>.Empty,
            100F,
            catalogRequest,
            bindings.MoveToImmutable(),
            inputs.MoveToImmutable(),
            [new AssetPath(
                "meshes/actors/character/facegenmorphs/morphs/nuska/mouth/mouthhumanfchargen.tri")]);
        SkyrimRaceMenuFaceBakeResult result = new SseRaceMenuFaceBakeService().Bake(request);
        Assert(result.Accepted && result.Shapes.Length == 4,
            "Captured Emi2 four-shape closure was refused: " + Format(result.Diagnostics));
        Assert(result.Diagnostics.Count(item =>
                   item.Code == "sse-face-bake-extension-unavailable") == 1,
            "Captured mouth closure should report exactly one declared-but-unavailable Nuska TRI.");

        foreach (RealShapeSpec spec in specs)
        {
            SkyrimRaceMenuFaceBakeShapeOutput output = result.Shapes.Single(shape =>
                shape.CarrierShapeName.Equals(spec.ShapeName, StringComparison.OrdinalIgnoreCase));
            ImmutableArray<Vector3> expected = callerBases[spec.ShapeName];
            Assert(output.MergedTriOrder.Length == spec.ExpectedMergedTriCount,
                $"Merged TRI count drifted for '{spec.ShapeName}'.");
            Assert(output.FinalPositions.SequenceEqual(expected),
                $"'{spec.ShapeName}' did not retain the explicit caller base when no channels resolved.");
            Assert(output.FinalPositionSha256 == PositionHash(expected),
                $"Final position hash drifted for '{spec.ShapeName}'.");
        }
    }

    private static void TestCapturedEmi2MeshOnlyHairFaceBake()
    {
        string projectRoot = FindProjectRoot();
        string evidenceRoot = Path.Combine(projectRoot, "01-source-copies", "gate2-emi2");
        Dictionary<string, BoundFile> manifest = ReadBoundFileManifest(
            Path.Combine(projectRoot, "05-reports", "gate2-headpart-geometry-base-sha256.csv"));
        HairShapeSpec[] specs =
        [
            new("0_HAIRLINE_Female_Human_Straight", "straightscalpHUMAN",
                "headpart-geometry-bases/meshes/KS Hairdo's/hairline/straightscalpHUMAN.nif",
                "headpart-geometry-bases/meshes/KS Hairdo's/hairline/straightscalpHUMAN.tri",
                897,
                "5556EB1D3E9379DAFFD9F95F1ED65D3B6A07D5647EF64C7E61C21920675CC360"),
            new("0Lassi", "s4studio_mesh_3",
                "headpart-geometry-bases/meshes/KS Hairdo's/Lassi.nif",
                "headpart-geometry-bases/meshes/KS Hairdo's/Lassi.tri",
                7362,
                "374432F2FE324292283EA1658D2CC006525735E53A8E6864C38E7F20C63D841A"),
            new("0LassiHL", "s4studio_mesh_3",
                "headpart-geometry-bases/meshes/KS Hairdo's/LassiHL.nif",
                "headpart-geometry-bases/meshes/KS Hairdo's/Lassi.tri",
                7362,
                "374432F2FE324292283EA1658D2CC006525735E53A8E6864C38E7F20C63D841A")
        ];

        ImmutableArray<SkyrimRaceMenuFaceBakeCarrierShapeBinding>.Builder bindings =
            ImmutableArray.CreateBuilder<SkyrimRaceMenuFaceBakeCarrierShapeBinding>(specs.Length);
        ImmutableArray<SkyrimRaceMenuFaceBakeShapeTriInputs>.Builder inputs =
            ImmutableArray.CreateBuilder<SkyrimRaceMenuFaceBakeShapeTriInputs>(specs.Length);
        Dictionary<string, ImmutableArray<Vector3>> restPositions =
            new(StringComparer.Ordinal);
        foreach (HairShapeSpec spec in specs)
        {
            SseSelectedHeadpartNifRestShape rest = ReadBoundNifRestShape(
                evidenceRoot, manifest, spec);
            restPositions.Add(spec.CarrierShapeName, rest.RestPositions);
            bindings.Add(new SkyrimRaceMenuFaceBakeCarrierShapeBinding(
                spec.CarrierShapeName,
                null,
                rest.VertexCount,
                rest.TopologySha256,
                rest.PackedPositionSha256,
                rest.RestPositions));
            inputs.Add(new SkyrimRaceMenuFaceBakeShapeTriInputs(
                spec.CarrierShapeName,
                null,
                null,
                BoundTri(evidenceRoot, manifest, spec.MeshTriRelativePath),
                ImmutableArray<SseTriHeadReadRequest>.Empty));
        }

        SkyrimRaceMenuFaceBakeRequest request = new(
            "NordRace",
            true,
            Native([], 0F, []),
            ImmutableArray<string>.Empty,
            ImmutableArray<SkyrimRaceMenuCustomMorphValue>.Empty,
            ImmutableArray<RaceMenuSculptPart>.Empty,
            0F,
            new SkyrimRaceMenuCatalogParseRequest(
                ImmutableArray<PluginName>.Empty,
                ImmutableArray<SkyrimRaceMenuCatalogAsset>.Empty),
            bindings.MoveToImmutable(),
            inputs.MoveToImmutable(),
            ImmutableArray<AssetPath>.Empty);

        SkyrimRaceMenuFaceBakeResult skinny = new SseRaceMenuFaceBakeService().Bake(request);
        Assert(skinny.Accepted && skinny.Shapes.Length == specs.Length,
            "Captured mesh-only hair bake was refused: " + Format(skinny.Diagnostics));
        foreach (HairShapeSpec spec in specs)
        {
            SkyrimRaceMenuFaceBakeTriEvidence evidence = skinny.Shapes.Single(shape =>
                shape.CarrierShapeName == spec.CarrierShapeName).TriEvidence.Single();
            Assert(evidence.SourcePath == ToDataAssetPath(spec.MeshTriRelativePath) &&
                   evidence.SourceSha256 == manifest[spec.MeshTriRelativePath].Sha256,
                $"Mesh-TRI evidence lost its path/hash authority for '{spec.CarrierShapeName}'.");
        }

        SkyrimRaceMenuFaceBakeShapeOutput hairline = skinny.Shapes.Single(shape =>
            shape.CarrierShapeName == "0_HAIRLINE_Female_Human_Straight");
        Assert(!hairline.FinalPositions.SequenceEqual(restPositions[hairline.CarrierShapeName]) &&
               hairline.FinalPositionSha256 ==
               new Sha256Hash("3B1BD1884176BE624078EBDD1667076DF88FAF403EEEC0AC7520DCD4EFB252AA") &&
               hairline.FinalPositionSha256 != hairline.BasePositionSha256,
            "Actor weight zero did not apply the real 897-vertex hairline SkinnyMorph.");
        Assert(hairline.MergedTriOrder.Length == 1 && hairline.TriEvidence is
               [
            {
                Role: SkyrimFaceMorphTriRole.Mesh,
                DeclaredVertexCount: 897,
                MorphCount: 1,
                Disposition: SkyrimRaceMenuFaceBakeTriDisposition.EligibleMorphSource
            }
               ],
            "Hairline mesh-TRI evidence did not bind its eligible SkinnyMorph source.");

        foreach (string name in new[] { "0Lassi", "0LassiHL" })
        {
            SkyrimRaceMenuFaceBakeShapeOutput output = skinny.Shapes.Single(shape =>
                shape.CarrierShapeName == name);
            Assert(output.FinalPositions.SequenceEqual(restPositions[name]) &&
                   output.BasePositionSha256 ==
                   new Sha256Hash("374432F2FE324292283EA1658D2CC006525735E53A8E6864C38E7F20C63D841A") &&
                   output.FinalPositionSha256 == output.BasePositionSha256,
                $"{name} did not preserve its exact 7362-vertex selected-NIF rest positions.");
            Assert(output.TriEvidence is
                   [
                {
                    Role: SkyrimFaceMorphTriRole.Mesh,
                    DeclaredVertexCount: 7432,
                    MorphCount: 0,
                    Disposition: SkyrimRaceMenuFaceBakeTriDisposition.EmptyMorphNoOp
                }
                   ],
                $"{name} did not expose the 7432-vertex empty TRI as an explicit no-op.");
        }

        Assert(skinny.Diagnostics.Count(item =>
                   item.Code == "sse-face-plan-empty-source-noop") == 2,
            "The two Lassi topology-mismatched empty TRIs did not produce explicit no-op diagnostics.");

        SkyrimRaceMenuFaceBakeResult fullWeight = new SseRaceMenuFaceBakeService().Bake(
            request with { ActorWeight = 100F });
        Assert(fullWeight.Accepted && fullWeight.Shapes.All(output =>
                   output.FinalPositions.SequenceEqual(restPositions[output.CarrierShapeName]) &&
                   output.FinalPositionSha256 == output.BasePositionSha256),
            "A selected headpart with no applicable morph did not preserve exact NIF rest positions.");

        SkyrimRaceMenuFaceBakeShapeTriInputs nonemptyMismatch = request.ShapeTriInputs[0] with
        {
            CarrierShapeName = "0Lassi"
        };
        AssertBakeRefused(request with
        {
            CarrierShapes = [request.CarrierShapes[1]],
            ShapeTriInputs = [nonemptyMismatch]
        }, "sse-face-plan-source-topology");

        Console.WriteLine(
            $"EVIDENCE HAIRLINE-SKINNY final={hairline.FinalPositionSha256} base={hairline.BasePositionSha256}; LASSI-NOOP rest=374432F2FE324292283EA1658D2CC006525735E53A8E6864C38E7F20C63D841A tri-vertices=7432 nif-vertices=7362");
    }

    private static SkyrimRaceMenuFaceBakeRequest CreateSyntheticBakeRequest()
    {
        AssetPath headHost = new("meshes/headchargen.tri");
        AssetPath mouthHost = new("meshes/mouthchargen.tri");
        AssetPath mouthMeshHost = new("meshes/mouth.tri");
        Vector3 headBase = new(10F, 20F, 30F);
        Vector3 mouthBase = new(-5F, -6F, -7F);
        Vector3 hairBase = new(1F, 2F, 3F);
        SseTriHeadReadRequest headA = SyntheticTri(
            "meshes/actors/character/FaceGenMorphs/morphs/synthetic/head-a.tri",
            [Vector3.Zero], ("HeadOnly", (short)2, (short)0, (short)0));
        SseTriHeadReadRequest headB = SyntheticTri(
            "meshes/actors/character/FaceGenMorphs/morphs/synthetic/head-b.tri",
            [Vector3.Zero]);
        SkyrimRaceMenuCatalogParseRequest catalog = new(
            [new PluginName("Synthetic.esp")],
        [
            TextAsset(
                "meshes/actors/character/FaceGenMorphs/Synthetic.esp/morphs.ini",
                "extension=headchargen.tri,synthetic/head-a.tri,synthetic/head-missing.tri,synthetic/head-b.tri\n")
        ]);

        return new SkyrimRaceMenuFaceBakeRequest(
            "NordRace",
            true,
            Native([], 0F, []),
            ImmutableArray<string>.Empty,
            [new SkyrimRaceMenuCustomMorphValue("HeadOnly", 1F)],
        [
            new RaceMenuSculptPart(mouthMeshHost.Value, 1,
                [new RaceMenuSculptVertex(0, 0F, 2F, 0F)], true, true)
        ],
            100F,
            catalog,
        [
            new SkyrimRaceMenuFaceBakeCarrierShapeBinding("Head", headHost, 1,
                Hash(Encoding.UTF8.GetBytes("head-topology")),
                PositionHash([headBase]), [headBase]),
            new SkyrimRaceMenuFaceBakeCarrierShapeBinding("Mouth", mouthHost, 1,
                Hash(Encoding.UTF8.GetBytes("mouth-topology")),
                PositionHash([mouthBase]), [mouthBase]),
            new SkyrimRaceMenuFaceBakeCarrierShapeBinding("Hair", null, 1,
                Hash(Encoding.UTF8.GetBytes("hair-topology")),
                PositionHash([hairBase]), [hairBase]),
            new SkyrimRaceMenuFaceBakeCarrierShapeBinding("HairHighlight", null, 1,
                Hash(Encoding.UTF8.GetBytes("hair-highlight-topology")),
                PositionHash([hairBase]), [hairBase])
        ],
        [
            new SkyrimRaceMenuFaceBakeShapeTriInputs("Head", null,
                SyntheticTri(headHost.Value, [Vector3.Zero]), null, [headA, headB]),
            new SkyrimRaceMenuFaceBakeShapeTriInputs("Mouth", null,
                SyntheticTri(mouthHost.Value, [Vector3.Zero]),
                SyntheticTri(mouthMeshHost.Value, [Vector3.Zero]),
                ImmutableArray<SseTriHeadReadRequest>.Empty),
            new SkyrimRaceMenuFaceBakeShapeTriInputs("Hair", null, null, null,
                ImmutableArray<SseTriHeadReadRequest>.Empty),
            new SkyrimRaceMenuFaceBakeShapeTriInputs("HairHighlight", null, null,
                SyntheticTri(mouthMeshHost.Value, [Vector3.Zero]),
                ImmutableArray<SseTriHeadReadRequest>.Empty)
        ],
        [
            new AssetPath(
                "meshes/actors/character/FaceGenMorphs/morphs/synthetic/head-missing.tri")
        ]);
    }

    private static SseTriHeadReadRequest SyntheticTri(
        string path,
        Vector3[] baseVertices,
        params (string Name, short X, short Y, short Z)[] morphs)
    {
        byte[] bytes = WriteTri(baseVertices, [], [], [],
        [
            .. morphs.Select(morph => new DenseFixture(morph.Name, 1F,
                [.. Enumerable.Repeat((morph.X, morph.Y, morph.Z), baseVertices.Length)]))
        ], []);
        return new SseTriHeadReadRequest(new AssetPath(path), Hash(bytes), [.. bytes]);
    }

    private static SkyrimRaceMenuCatalogAsset BoundCatalogAsset(
        string root,
        string relativePath,
        string assetPath,
        string expectedSha256)
    {
        string fullPath = ResolveEvidencePath(root, relativePath);
        byte[] bytes = File.ReadAllBytes(fullPath);
        return new SkyrimRaceMenuCatalogAsset(new AssetPath(assetPath),
            new Sha256Hash(expectedSha256), [.. bytes]);
    }

    private static SseTriHeadReadRequest BoundTri(
        string root,
        IReadOnlyDictionary<string, BoundFile> manifest,
        string relativePath)
    {
        if (!manifest.TryGetValue(relativePath, out BoundFile? bound))
        {
            throw new InvalidOperationException(
                $"Bound TRI '{relativePath}' is absent from its authority manifest.");
        }
        string fullPath = ResolveEvidencePath(root, relativePath);
        byte[] bytes = File.ReadAllBytes(fullPath);
        Assert(bytes.LongLength == bound.Length,
            $"Bound TRI length drifted for '{relativePath}'.");
        return new SseTriHeadReadRequest(ToDataAssetPath(relativePath), bound.Sha256, [.. bytes]);
    }

    private static SseSelectedHeadpartNifRestShape ReadBoundNifRestShape(
        string root,
        IReadOnlyDictionary<string, BoundFile> manifest,
        HairShapeSpec spec)
    {
        if (!manifest.TryGetValue(spec.ModelRelativePath, out BoundFile? bound))
        {
            throw new InvalidOperationException(
                $"Bound selected-NIF '{spec.ModelRelativePath}' is absent from its authority manifest.");
        }

        string fullPath = ResolveEvidencePath(root, spec.ModelRelativePath);
        byte[] bytes = File.ReadAllBytes(fullPath);
        Assert(bytes.LongLength == bound.Length,
            $"Bound selected-NIF length drifted for '{spec.ModelRelativePath}'.");
        SseSelectedHeadpartNifGeometryReadResult result =
            new SseSelectedHeadpartNifGeometryReader().Read(
                new SseSelectedHeadpartNifGeometryReadRequest(
                    ToDataAssetPath(spec.ModelRelativePath), bound.Sha256, [.. bytes]));
        Assert(result.Accepted && result.Document is not null,
            $"Bound selected-NIF '{spec.ModelRelativePath}' was refused: {Format(result.Diagnostics)}");
        SseSelectedHeadpartNifRestShape shape = result.Document!.Shapes.Single();
        Assert(shape.Name == spec.ModelShapeName && shape.VertexCount == spec.VertexCount &&
               shape.PackedPositionSha256 == new Sha256Hash(spec.ExpectedRestPositionSha256),
            $"Bound selected-NIF geometry drifted for '{spec.CarrierShapeName}'.");
        return shape;
    }

    private static SseTriHeadDocument RequireTriDocument(SseTriHeadReadRequest request)
    {
        SseTriHeadReadResult result = new SseTriHeadReader().Read(request);
        Assert(result.Accepted && result.Document is not null,
            $"Bound TRI '{request.SourcePath}' was refused: {Format(result.Diagnostics)}");
        return result.Document ??
               throw new InvalidOperationException("Accepted bound TRI omitted its document.");
    }

    private static Dictionary<string, BoundFile> ReadBoundFileManifest(string path)
    {
        string[] lines = File.ReadAllLines(path);
        Assert(lines.Length > 1 && lines[0] == "relative_path,length,sha256",
            $"Bound file manifest '{path}' has an unexpected header.");
        Dictionary<string, BoundFile> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            string[] fields = line.Split(',');
            Assert(fields.Length == 3 &&
                   long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture,
                       out long length) &&
                   result.TryAdd(fields[0], new BoundFile(length, new Sha256Hash(fields[2]))),
                $"Malformed or duplicate bound file row: {line}");
        }

        return result;
    }

    private static AssetPath ToDataAssetPath(string relativePath)
    {
        const string marker = "/meshes/";
        int markerIndex = relativePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        Assert(markerIndex >= 0,
            $"Bound TRI path has no Data-relative meshes segment: {relativePath}");
        return new AssetPath(relativePath[(markerIndex + 1)..]);
    }

    private static string ResolveEvidencePath(string root, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert(fullPath.StartsWith(root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath),
            $"Evidence path is absent or escaped its root: {fullPath}");
        return fullPath;
    }

    private static ImmutableArray<Vector3> CreateCallerSentinelBase(int count, int shapeIndex) =>
    [
        .. Enumerable.Range(0, count).Select(index => new Vector3(
            100F + shapeIndex,
            -200F - shapeIndex,
            300F + index % 7 * 0.01F))
    ];

    private static Sha256Hash PositionHash(ImmutableArray<Vector3> positions)
    {
        byte[] packed = new byte[checked(positions.Length * 3 * sizeof(float))];
        int offset = 0;
        foreach (Vector3 position in positions)
        {
            WritePositionComponent(packed, ref offset, position.X);
            WritePositionComponent(packed, ref offset, position.Y);
            WritePositionComponent(packed, ref offset, position.Z);
        }

        return Hash(packed);
    }

    private static void WritePositionComponent(Span<byte> target, ref int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target[offset..],
            BitConverter.SingleToInt32Bits(value));
        offset += sizeof(float);
    }

    private static void AssertBakeRefused(SkyrimRaceMenuFaceBakeRequest request,
        string expectedCode)
    {
        SkyrimRaceMenuFaceBakeResult result = new SseRaceMenuFaceBakeService().Bake(request);
        Assert(!result.Accepted && result.Shapes.IsEmpty &&
               HasCode(result.Diagnostics, expectedCode),
            $"Expected all-or-nothing bake refusal '{expectedCode}', got: {Format(result.Diagnostics)}");
    }

    private sealed record BoundFile(long Length, Sha256Hash Sha256);

    private sealed record RealShapeSpec(
        string ShapeName,
        string? RaceRelativePath,
        string ChargenRelativePath,
        string MeshRelativePath,
        ImmutableArray<string> ExtensionRelativePaths,
        int ExpectedMergedTriCount);

    private sealed record HairShapeSpec(
        string CarrierShapeName,
        string ModelShapeName,
        string ModelRelativePath,
        string MeshTriRelativePath,
        int VertexCount,
        string ExpectedRestPositionSha256);
}
