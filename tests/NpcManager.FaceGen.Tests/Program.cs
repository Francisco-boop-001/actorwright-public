using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.TestInfrastructure;

namespace NpcManager.FaceGen.Tests;

internal static partial class Program
{
    private const string Selector_FaceBakeDiagnosticEvidence = "--test-face-bake-diagnostic-evidence";
    private const string Selector_HeadPartNifUnsupportedBlock = "--test-headpart-nif-unsupported-block";
    private const string Selector_ExtendedHeadPartComposition = "--test-extended-headpart-composition";
    private const string Selector_NativeShaderlessDummy = "--test-native-shaderless-dummy";
    private const string Selector_RecordCarrierCoherence = "--test-record-carrier-coherence";
    private const string Selector_NativeTriTopology = "--test-native-tri-topology";
    private const string Selector_MorphPlanOnly = "--morph-plan-only";

    private static readonly Sha256Hash FixtureHash = new(new string('0', 64));

    public static int Main(string[] args)
    {
        if (NpcManager.TestInfrastructure.StandaloneSelectorInventory.TryList(args, typeof(Program)))
        {
            return 0;
        }

        if (args is [Selector_FaceBakeDiagnosticEvidence])
        {
            try { TestMultiShapeFaceBakeRefusals(); Console.WriteLine("PASS face-bake-diagnostic-evidence"); return 0; }
            catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
        }
        if (args is [Selector_HeadPartNifUnsupportedBlock]) { try { TestHeadPartNifUnsupportedBlock(); Console.WriteLine("PASS headpart NIF unsupported-block classification"); return 0; } catch (Exception exception) { Console.Error.WriteLine("FAIL headpart-nif-unsupported-block: " + exception.Message); return 1; } }
        if (args is [Selector_ExtendedHeadPartComposition]) { try { TestExtendedHeadPartCarrierComposition().GetAwaiter().GetResult(); Console.WriteLine("PASS extended HDPT type carrier composition"); return 0; } catch (Exception exception) { Console.Error.WriteLine("FAIL extended-headpart-composition: " + exception.Message); return 1; } }
        if (args is [Selector_NativeShaderlessDummy])
        {
            try
            {
                TestSkyrimRecordCarrierCoherence(shaderlessDummy: true).GetAwaiter().GetResult();
                Console.WriteLine("PASS native shaderless dummy composition");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL native-shaderless-dummy: " + exception.Message);
                return 1;
            }
        }

        if (args is [Selector_RecordCarrierCoherence])
        {
            try
            {
                TestSkyrimRecordCarrierCoherence().GetAwaiter().GetResult();
                Console.WriteLine("PASS Skyrim record/carrier coherence");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL record-carrier-coherence: " + exception.Message);
                return 1;
            }
        }

        if (args is [Selector_NativeTriTopology])
        {
            try
            {
                TestSkyrimNativeTriTopology().GetAwaiter().GetResult();
                TestMultiShapeFaceBake();
                TestMultiShapeFaceBakeRefusals();
                Console.WriteLine("RESULT PASS native-tri-topology");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL native-tri-topology: " + exception.Message);
                return 1;
            }
        }

        if (args.Length == 1 &&
            args[0] == Selector_MorphPlanOnly)
        {
            TestFaceMorphPlanSemantics();
            TestFaceMorphPlanRefusals();
            Console.WriteLine(
                "RESULT PASS morph-plan-only");
            return 0;
        }

        var tests = new (string Name, Action Run)[]
        {
            ("native model/TRI topology mismatches block before output",
                () => TestSkyrimNativeTriTopology().GetAwaiter().GetResult()),
            ("captured COR 24-bit BGR tint mask decodes to opaque BGRA",
                TestCapturedCorBgr24TintMask),
            ("captured COR mouth TRI admits its shipped duplicate-morph semantics",
                TestCapturedCorMouthTri),
            ("product-owned Emi2 FaceTint/private-diffuse composition reproduces the accepted pair",
                TestCapturedEmi2FaceTextureComposition),
            ("face-texture authority tamper, order, hash, classification, duplication, and neck drift fail closed",
                TestCapturedEmi2FaceTextureAuthorityRefusals),
            ("face-texture pixel and active-layer budgets refuse before decode",
                TestFaceTextureCompositionBudgets),
            ("direct face-texture refusal reports owned-output cleanup failure",
                TestFaceTextureCleanupFailureIsObservable),
            ("face-texture output transaction pins staging ancestry against substitution",
                TestFaceTextureOutputAncestorIdentityIsPinned),
            ("schema v4 refuses the real mixed mapped-and-baked tint plan",
                TestSchema4MixedTintRefusal),
            ("RaceMenu BGRA8 FaceTint decodes in-process without texconv",
                TestBgra8FaceTintTextureDecoder),
            ("native FaceTint retains a 108-row COR-shaped default table",
                TestSkyrimNativeFaceTintExtendedRaceTable),
            ("native Skyrim records and loose masks materialize one reopened DXT5 FaceTint",
                TestSkyrimNativeFaceTintMaterialization),
            ("FRTRI003 dense and modifier streams parse with opaque texture indices",
                TestTriDenseModifierAndOpaqueTextureIndices),
            ("the exact 30-row active Gate2 morph closure parses with bound hashes",
                TestActiveGate2MorphClosure),
            ("FRTRI003 corruption and consumed-index failures remain closed",
                TestTriCorruptionRefusals),
            ("RaceMenu catalog honors plugin precedence and extension order",
                TestCatalogPrecedenceAndExtensions),
            ("RaceMenu catalog refuses missing referenced slider assets",
                TestCatalogMissingSliderRefusal),
            ("face morph planning reproduces native, custom, sculpt, and merge semantics",
                TestFaceMorphPlanSemantics),
            ("face morph planning refuses unresolved custom geometry and bad closure",
                TestFaceMorphPlanRefusals),
            ("skinny-weight mapping covers zero, midpoint, and full weight",
                TestSkinnyWeightMapping),
            ("face morph evaluation applies thresholded ordered signed deltas",
                TestFaceMorphEvaluation),
            ("multi-shape bake uses caller carrier bases and available extension subsets",
                TestMultiShapeFaceBake),
            ("multi-shape bake refuses broken shape, host, extension, and morph closure",
                TestMultiShapeFaceBakeRefusals),
            ("captured Emi2 four-shape TRI closure bakes from explicit caller arrays",
                TestCapturedEmi2FourShapeFaceBake),
            ("captured Emi2 mesh-only hair bakes from exact selected-NIF rest geometry",
                TestCapturedEmi2MeshOnlyHairFaceBake)
        };

        int passed = 0;
        foreach ((string name, Action run) in tests)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
                passed++;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
                return 1;
            }
        }

        Console.WriteLine($"RESULT PASS {passed}/{tests.Length}");
        return 0;
    }

    private static void TestCapturedCorBgr24TintMask()
    {
        string path = Path.Combine(
            FindProjectRoot(),
            "01-source-copies",
            "sophia-live-closure-20260723",
            "Data",
            "textures",
            "!COR",
            "TintMasks",
            "femalehead_cheeks.dds");
        FaceTintTextureDecodeResult result =
            new InProcessDdsTextureDecoder(
                    new WorkspacePath(@"K:\ExampleWorkspace"))
                .DecodeAsync(new WorkspacePath(path), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        Assert(result.Decoded && result.Bytes is not null &&
               result.Width == 2048 && result.Height == 2048 &&
               result.SourceSha256?.Value ==
                   "1eccbfd675fb3ac9d2af45696ca257271a1beb1d7bde68fd960d62b832d1de56" &&
               result.Bytes.Length == 2048 * 2048 * 4 &&
               result.Bytes.Where((_, index) => (index & 3) == 3)
                   .All(alpha => alpha == byte.MaxValue),
            "Captured COR 24-bit BGR mask did not decode as one exact opaque BGRA raster: " +
            $"decoded={result.Decoded} size={result.Width}x{result.Height} " +
            $"bytes={result.Bytes?.Length ?? 0} hash={result.SourceSha256?.Value ?? "none"} " +
            $"opaque={result.Bytes?.Where((_, index) => (index & 3) == 3).All(alpha => alpha == byte.MaxValue)} " +
            Format(result.Diagnostics));
    }

    private static void TestCapturedCorMouthTri()
    {
        string projectRoot = FindProjectRoot();
        string meshes = Path.Combine(projectRoot, "01-source-copies",
            "sophia-live-closure-20260723", "Data", "meshes");
        string[] roots =
        [
            Path.Combine(meshes, "!COR", "Mouth"),
            Path.Combine(meshes, "!COR", "Head"),
            Path.Combine(meshes, "actors", "character", "facegenmorphs",
                "morphs", "Genesis", "Mouth")
        ];
        int lipType9Sources = 0;
        foreach (string path in roots.SelectMany(root =>
                     Directory.GetFiles(root, "*.tri",
                         SearchOption.TopDirectoryOnly)))
        {
            string assetPath = "meshes/" + Path.GetRelativePath(meshes, path)
                .Replace('\\', '/');
            byte[] bytes = File.ReadAllBytes(path);
            SseTriHeadReadResult result = ReadTri(bytes, assetPath);
            Assert(result.Accepted && result.Document is not null,
                $"Captured COR mouth closure TRI '{assetPath}' was refused: " +
                Format(result.Diagnostics));
            lipType9Sources += result.Document!.Morphs.Count(item =>
                item.Name.Equals("LipType9",
                    StringComparison.OrdinalIgnoreCase));
        }
        Assert(lipType9Sources > 0,
            "Captured COR mouth closure contained no canonical LipType9 morph.");
    }

    private static void TestTriDenseModifierAndOpaqueTextureIndices()
    {
        byte[] bytes = WriteTri(
            [new Vector3(1F, 2F, 3F), new Vector3(4F, 5F, 6F)],
            [0U, 1U, 0U],
            [new Vector2(0F, 0F)],
            [0U, 1U, 0U],
            [new DenseFixture("Dense", 0.5F, [(2, -4, 0), (0, 2, 4)])],
            [new ModifierFixture("Sparse", [1U], [new Vector3(5F, 7F, 9F)])]);

        SseTriHeadReadResult result = ReadTri(bytes);
        Assert(result.Accepted && result.Document is not null,
            "Valid mixed FRTRI003 was refused: " + Format(result.Diagnostics));
        SseTriHeadDocument document = result.Document ??
            throw new InvalidOperationException("Accepted TRI result omitted its document.");
        Assert(document.VertexCount == 2 && document.TriangleCount == 1 &&
               document.UvCount == 1 && document.Morphs.Length == 2,
            "Parsed FRTRI003 counts drifted.");
        Assert(document.Morphs[0] is
        {
            Name: "Dense",
            Encoding: SseTriHeadMorphEncoding.DenseInt16,
            Multiplier: 0.5F
        } && document.Morphs[0].Deltas[0].Delta == new Vector3(1F, -2F, 0F),
            "Dense multiplier/delta decoding drifted.");
        Assert(document.Morphs[1] is
        {
            Name: "Sparse",
            Encoding: SseTriHeadMorphEncoding.ModifierAbsolutePositions
        } && document.Morphs[1].Deltas.Single() ==
        new SseTriHeadVertexDelta(1, new Vector3(1F, 2F, 3F)),
            "Modifier absolute-position decoding drifted.");
    }

    private static void TestActiveGate2MorphClosure()
    {
        string projectRoot = FindProjectRoot();
        string evidenceRoot = Path.Combine(projectRoot, "01-source-copies", "gate2-emi2");
        string manifestPath = Path.Combine(projectRoot, "05-reports",
            "gate2-active-morph-provider-closure-sha256.csv");
        string[] lines = File.ReadAllLines(manifestPath);
        Assert(lines.Length == 31 && lines[0] == "relative_path,length,sha256",
            "Active morph-provider closure must have its exact header plus 30 data rows.");
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            string[] fields = line.Split(',');
            Assert(fields.Length == 3 && paths.Add(fields[0]),
                $"Malformed or duplicate active closure row: {line}");
            string file = Path.GetFullPath(Path.Combine(evidenceRoot,
                fields[0].Replace('/', Path.DirectorySeparatorChar)));
            Assert(file.StartsWith(evidenceRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) && File.Exists(file),
                $"Active provider fixture is absent or outside evidence root: {file}");
            Assert(long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture,
                       out long expectedLength),
                $"Active provider length is malformed: {line}");
            string marker = $"{Path.DirectorySeparatorChar}meshes{Path.DirectorySeparatorChar}";
            int markerIndex = file.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            Assert(markerIndex >= 0, $"Provider fixture has no Data-relative meshes path: {file}");
            string assetPath = file[(markerIndex + 1)..].Replace('\\', '/');
            byte[] bytes = File.ReadAllBytes(file);
            Assert(bytes.LongLength == expectedLength,
                $"Active provider length drifted for '{fields[0]}'.");
            SseTriHeadReadResult result = ReadTri(bytes, assetPath, new Sha256Hash(fields[2]));
            Assert(result.Accepted && result.Document is
            {
                VertexCount: > 0,
                Morphs.Length: > 0
            }, $"Real provider '{file}' was refused: {Format(result.Diagnostics)}");
        }
    }

    private static void TestTriCorruptionRefusals()
    {
        byte[] valid = WriteTri(
            [Vector3.Zero],
            [0U, 0U, 0U],
            [Vector2.Zero],
            [0U, 0U, 0U],
            [new DenseFixture("One", 1F, [(1, 0, 0)])],
            []);

        byte[] wrongVersion = (byte[])valid.Clone();
        wrongVersion[7] = (byte)'2';
        AssertRefused(ReadTri(wrongVersion), "sse-tri-version");
        AssertRefused(ReadTri(valid[..^1]), "sse-tri-truncated");
        AssertRefused(ReadTri([.. valid, 0x00]), "sse-tri-trailing-bytes");
        AssertRefused(ReadTri(valid, expectedHash: FixtureHash), "sse-tri-hash-mismatch");

        byte[] impossiblePool = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(impossiblePool.AsSpan(44, sizeof(uint)),
            16_000_000U);
        AssertRefused(ReadTri(impossiblePool), "sse-tri-truncated");

        byte[] invalidTriangle = WriteTri(
            [Vector3.Zero],
            [1U, 0U, 0U],
            [Vector2.Zero],
            [0U, 0U, 0U],
            [],
            []);
        AssertRefused(ReadTri(invalidTriangle), "sse-tri-index");

        byte[] duplicateMorph = WriteTri(
            [Vector3.Zero],
            [],
            [],
            [],
            [
                new DenseFixture("Same", 1F, [(1, 0, 0)]),
                new DenseFixture("same", 1F, [(0, 1, 0)])
            ],
            []);
        SseTriHeadReadResult firstWinsDuplicate = ReadTri(duplicateMorph);
        Assert(firstWinsDuplicate.Accepted &&
               HasCode(firstWinsDuplicate.Diagnostics,
                   "sse-tri-duplicate-morph") &&
               firstWinsDuplicate.Document?.Morphs is { Length: 1 } &&
               firstWinsDuplicate.Document.Morphs[0].Deltas[0].Delta ==
               new Vector3(1F, 0F, 0F),
            "Conflicting duplicate TRI morph rows did not retain the first occurrence with a warning.");

        byte[] identicalDuplicateMorph = WriteTri(
            [Vector3.Zero],
            [],
            [],
            [],
            [
                new DenseFixture("Same", 1F, [(1, 0, 0)]),
                new DenseFixture("same", 1F, [(1, 0, 0)])
            ],
            []);
        SseTriHeadReadResult identicalDuplicate = ReadTri(identicalDuplicateMorph);
        Assert(identicalDuplicate.Accepted &&
               identicalDuplicate.Document?.Morphs is { Length: 1 },
            "Byte-equivalent duplicate TRI morph rows were not canonicalized once.");

        byte[] invalidModifier = WriteTri(
            [Vector3.Zero],
            [],
            [],
            [],
            [],
            [new ModifierFixture("Bad", [1U], [Vector3.One])]);
        AssertRefused(ReadTri(invalidModifier), "sse-tri-index");

        byte[] unusedModifierPool = WriteTri(
            [Vector3.Zero],
            [],
            [],
            [],
            [],
            [new ModifierFixture("Unused", [], [Vector3.One])]);
        AssertRefused(ReadTri(unusedModifierPool), "sse-tri-modifier-pool");
    }

    private static void TestCatalogPrecedenceAndExtensions()
    {
        PluginName first = new("First.esp");
        PluginName second = new("Second.esp");
        SkyrimRaceMenuCatalogAsset[] assets =
        [
            TextAsset("meshes/actors/character/FaceGenMorphs/First.esp/races.ini",
                "NordRace=face.slider\n"),
            TextAsset("meshes/actors/character/FaceGenMorphs/First.esp/face.slider",
                "[Female]\nShape=16, Slider, FirstLow, FirstHigh\n"),
            TextAsset("meshes/actors/character/FaceGenMorphs/First.esp/morphs.ini",
                "extension=actors/character/character assets/femaleheadchargen.tri,ece/One.tri\n" +
                "extension=KL/High Poly Head/femaleheadchargen.tri,KL/femalehead_ece.tri\n"),
            TextAsset("meshes/actors/character/FaceGenMorphs/Second.esp/races.ini",
                "NordRace=face.slider\n"),
            TextAsset("meshes/actors/character/FaceGenMorphs/Second.esp/face.slider",
                "[Female]\nShape=16, Slider, SecondLow, SecondHigh\n"),
            TextAsset("meshes/actors/character/FaceGenMorphs/Second.esp/morphs.ini",
                "extension=actors/character/character assets/femaleheadchargen.tri,ece/Two.tri,ece/One.tri\n")
        ];

        SkyrimRaceMenuCatalogParseResult result = new RaceMenuSliderCatalogParserCore().Parse(
            new SkyrimRaceMenuCatalogParseRequest([first, second], [.. assets]));
        Assert(result.Accepted && result.Catalog is not null,
            "Valid RaceMenu catalog was refused: " + Format(result.Diagnostics));
        SkyrimRaceMenuSliderCatalog catalog = result.Catalog ??
            throw new InvalidOperationException("Accepted catalog result omitted its catalog.");
        SkyrimRaceMenuSliderDefinition slider = catalog.Sliders.Single();
        Assert(slider.SourcePlugin == second && slider.LowerBound == "SecondLow" &&
               slider.UpperBound == "SecondHigh",
            "Later loaded plugin did not win the race/sex/name slider key.");
        SkyrimRaceMenuMorphExtension lowPoly = catalog.MorphExtensions.Single(item =>
            item.BaseChargenTri.Value.Equals(
                "meshes/actors/character/character assets/femaleheadchargen.tri",
                StringComparison.OrdinalIgnoreCase));
        Assert(lowPoly.ExtendedTriPaths.Select(path => path.Value).SequenceEqual(
               [
                   "meshes/actors/character/FaceGenMorphs/morphs/ece/One.tri",
                   "meshes/actors/character/FaceGenMorphs/morphs/ece/Two.tri"
               ], StringComparer.OrdinalIgnoreCase),
            "Morph extensions did not append uniquely in plugin/load-file order.");
        SkyrimRaceMenuMorphExtension highPoly = catalog.MorphExtensions.Single(item =>
            item.BaseChargenTri.Value.Equals(
                "meshes/KL/High Poly Head/femaleheadchargen.tri",
                StringComparison.OrdinalIgnoreCase));
        Assert(highPoly.ExtendedTriPaths.Select(path => path.Value).SequenceEqual(
            ["meshes/actors/character/FaceGenMorphs/morphs/KL/femalehead_ece.tri"],
            StringComparer.OrdinalIgnoreCase),
            "Exact High Poly Head mapping was conflated with the low-poly head host.");
    }

    private static void TestCatalogMissingSliderRefusal()
    {
        SkyrimRaceMenuCatalogParseResult result = new RaceMenuSliderCatalogParserCore().Parse(
            new SkyrimRaceMenuCatalogParseRequest([new PluginName("Missing.esp")],
            [
                TextAsset("meshes/actors/character/FaceGenMorphs/Missing.esp/races.ini",
                    "NordRace=absent.slider\n")
            ]));
        AssertRefused(result, "racemenu-catalog-slider-missing");
    }

    private static void TestFaceMorphPlanSemantics()
    {
        AssetPath chargenPath = new(
            "meshes/actors/character/character assets/femaleheadchargen.tri");
        AssetPath extendedPath = new(
            "meshes/actors/character/FaceGenMorphs/morphs/ece/extended.tri");
        SseTriHeadDocument race = Document("meshes/race.tri",
            ("NordRace", 1F), ("NoseLong", 2F), ("NoseDown", 3F),
            ("NordKeywordMorph", 4F), ("Default", 5F), ("BrowType2", 6F),
            ("Lower", 7F), ("Upper", 8F), ("PresetMorph2", 9F),
            ("DirectNegative", 10F), ("SkinnyMorph", 11F), ("Shared", 12F));
        SseTriHeadDocument chargen = Document(chargenPath.Value, ("Shared", 20F));
        SseTriHeadDocument mesh = Document("meshes/head.nif.tri", ("Shared", 30F));
        SseTriHeadDocument extended = Document(extendedPath.Value, ("Shared", 40F));
        SkyrimRaceMenuSliderCatalog catalog = new(
        [
            Slider("ShapeNegative", SkyrimRaceMenuSliderType.Slider, "Lower", "Upper"),
            Slider("ShapePositive", SkyrimRaceMenuSliderType.Slider, "Lower", "Upper"),
            Slider("Preset", SkyrimRaceMenuSliderType.Preset, "PresetMorph", string.Empty),
            Slider("HairChoice", SkyrimRaceMenuSliderType.HeadPart, string.Empty, string.Empty)
        ],
        [
            new SkyrimRaceMenuMorphExtension(chargenPath, [extendedPath])
        ]);

        SkyrimFaceMorphPlanBuildRequest request = new(
            1,
            "NordRace",
            true,
            Native([0.5F, -0.5F, .. Enumerable.Repeat(0F, 16)], float.MaxValue,
                [0U, 2U, uint.MaxValue, uint.MaxValue]),
            ["NordKeyword"],
            [
                new SkyrimRaceMenuCustomMorphValue("ShapeNegative", -3F),
                new SkyrimRaceMenuCustomMorphValue("ShapePositive", 3F),
                new SkyrimRaceMenuCustomMorphValue("Preset", 2.9F),
                new SkyrimRaceMenuCustomMorphValue("HairChoice", 1F),
                new SkyrimRaceMenuCustomMorphValue("DirectNegative", -2F),
                new SkyrimRaceMenuCustomMorphValue("Shared", 2F)
            ],
            [
                new RaceMenuSculptPart(mesh.SourcePath.Value, 1,
                    [new RaceMenuSculptVertex(0, 0F, 1F, 0F)], true, true)
            ],
            50F,
            catalog,
            new SkyrimFaceMorphTriSource(SkyrimFaceMorphTriRole.Race, race),
            new SkyrimFaceMorphTriSource(SkyrimFaceMorphTriRole.Chargen, chargen),
            new SkyrimFaceMorphTriSource(SkyrimFaceMorphTriRole.Mesh, mesh),
            [new SkyrimFaceMorphTriSource(SkyrimFaceMorphTriRole.Extended, extended)]);

        SkyrimFaceMorphPlanBuildResult result = new SseFaceMorphPlanBuilder().Build(request);
        Assert(result.Accepted && result.Plan is not null,
            "Valid face morph plan was refused: " + Format(result.Diagnostics));
        SkyrimFaceMorphPlan plan = result.Plan ??
            throw new InvalidOperationException("Accepted build result omitted its plan.");
        Assert(plan.MergedTriOrder.Select(path => path.Value).SequenceEqual(
            [race.SourcePath.Value, chargenPath.Value, mesh.SourcePath.Value, extendedPath.Value],
            StringComparer.OrdinalIgnoreCase),
            "Bake TRI order was not Race > Chargen > Mesh > Extended.");
        AssertChannel(plan, "NordRace", 1F);
        AssertChannel(plan, "NoseLong", 0.5F);
        AssertChannel(plan, "NoseDown", 0.5F);
        AssertChannel(plan, "NordKeywordMorph", 1F);
        AssertChannel(plan, "Default", 1F);
        AssertChannel(plan, "BrowType2", 1F);
        AssertChannel(plan, "Lower", 3F);
        AssertChannel(plan, "Upper", 3F);
        AssertChannel(plan, "PresetMorph2", 1F);
        AssertChannel(plan, "DirectNegative", -2F);
        AssertChannel(plan, "RaceMenuSculpt", 1F);
        AssertChannel(plan, "SkinnyMorph", 0.5F);
        SkyrimFaceMorphChannel shared = AssertChannel(plan, "Shared", 2F);
        Assert(shared.MorphSourceRole == SkyrimFaceMorphTriRole.Race &&
               shared.Deltas.Single().Delta.X == 12F,
            "Duplicate morph geometry did not retain the first Race source.");
    }

    private static void TestFaceMorphPlanRefusals()
    {
        SkyrimFaceMorphPlanBuildRequest unresolved = BasicPlanRequest(100F) with
        {
            CustomMorphs = [new SkyrimRaceMenuCustomMorphValue("Absent", 1F)]
        };
        AssertRefused(new SseFaceMorphPlanBuilder().Build(unresolved),
            "sse-face-plan-custom-unresolved");

        AssetPath unexpected = new("meshes/extended.tri");
        SkyrimFaceMorphPlanBuildRequest badClosure = BasicPlanRequest(100F) with
        {
            ExtendedMorphTris =
            [
                new SkyrimFaceMorphTriSource(SkyrimFaceMorphTriRole.Extended,
                    Document(unexpected.Value, ("Unused", 1F)))
            ]
        };
        AssertRefused(new SseFaceMorphPlanBuilder().Build(badClosure),
            "sse-face-plan-extension-closure");

        AssetPath optional = new("meshes/optional-extended.tri");
        SkyrimFaceMorphPlanBuildRequest declaredUnavailable =
            BasicPlanRequest(100F) with
            {
                ChargenMorphTri =
                    new SkyrimFaceMorphTriSource(
                        SkyrimFaceMorphTriRole.Chargen,
                        Document(
                            "meshes/chargen.tri",
                            ("Unused", 1F))),
                Catalog = new SkyrimRaceMenuSliderCatalog(
                    [],
                    [
                        new SkyrimRaceMenuMorphExtension(
                            new AssetPath("meshes/chargen.tri"),
                            [optional])
                    ]),
                ExplicitUnavailableExtendedTris = [optional]
            };
        SkyrimFaceMorphPlanBuildResult acceptedUnavailable =
            new SseFaceMorphPlanBuilder().Build(
                declaredUnavailable);
        Assert(acceptedUnavailable.Accepted,
            "An exactly declared unavailable optional extended TRI was refused: " +
            Format(acceptedUnavailable.Diagnostics));

        SkyrimFaceMorphPlanBuildRequest undeclaredUnavailable =
            declaredUnavailable with
            {
                ExplicitUnavailableExtendedTris =
                    [new AssetPath("meshes/not-in-catalog.tri")]
            };
        AssertRefused(
            new SseFaceMorphPlanBuilder().Build(
                undeclaredUnavailable),
            "sse-face-plan-unavailable-extension-closure");
    }

    private static void TestSkinnyWeightMapping()
    {
        SseFaceMorphPlanBuilder builder = new();
        SkyrimFaceMorphPlan zero = RequirePlan(builder.Build(BasicPlanRequest(0F)));
        SkyrimFaceMorphPlan midpoint = RequirePlan(builder.Build(BasicPlanRequest(50F)));
        SkyrimFaceMorphPlan full = RequirePlan(builder.Build(BasicPlanRequest(100F)));
        AssertChannel(zero, "SkinnyMorph", 1F);
        AssertChannel(midpoint, "SkinnyMorph", 0.5F);
        Assert(!full.Channels.Any(channel =>
                channel.Name.Equals("SkinnyMorph", StringComparison.OrdinalIgnoreCase)),
            "Actor weight 100 unexpectedly retained SkinnyMorph.");
    }

    private static void TestFaceMorphEvaluation()
    {
        SkyrimFaceMorphPlan plan = new(1, ImmutableArray<AssetPath>.Empty,
        [
            Channel("BelowThreshold", 1F, new Vector3(0.0005F, 0F, 0F)),
            Channel("Positive", 2F, new Vector3(0.002F, 1F, 0F)),
            Channel("Signed", -0.5F, new Vector3(0F, 2F, 4F))
        ]);
        SkyrimFaceMorphEvaluationResult result = new SseFaceMorphEvaluator().Evaluate(
            new SkyrimFaceMorphEvaluationRequest([new Vector3(1F, 2F, 3F)], plan));
        Assert(result.Accepted && result.Positions.Length == 1,
            "Valid morph evaluation was refused: " + Format(result.Diagnostics));
        Assert(result.Positions[0] == new Vector3(1.004F, 3F, 1F),
            $"Ordered signed evaluation produced {result.Positions[0]}.");

        SkyrimFaceMorphEvaluationResult topology = new SseFaceMorphEvaluator().Evaluate(
            new SkyrimFaceMorphEvaluationRequest(ImmutableArray<Vector3>.Empty, plan));
        AssertRefused(topology, "sse-face-evaluate-topology");

        SkyrimFaceMorphChannel duplicate = new("Duplicate", 1F,
        [
            new SseTriHeadVertexDelta(0, Vector3.One),
            new SseTriHeadVertexDelta(0, Vector3.UnitX)
        ], null, null,
        [new SkyrimFaceMorphContribution(SkyrimFaceMorphContributionKind.Nam9, "test", 1F)]);
        SkyrimFaceMorphEvaluationResult duplicateResult = new SseFaceMorphEvaluator().Evaluate(
            new SkyrimFaceMorphEvaluationRequest([Vector3.Zero],
                new SkyrimFaceMorphPlan(1, ImmutableArray<AssetPath>.Empty, [duplicate])));
        AssertRefused(duplicateResult, "sse-face-evaluate-delta");
    }

    private static SkyrimFaceMorphPlanBuildRequest BasicPlanRequest(float actorWeight)
    {
        SseTriHeadDocument race = Document("meshes/race.tri", ("SkinnyMorph", 1F));
        return new SkyrimFaceMorphPlanBuildRequest(
            1,
            "NordRace",
            true,
            Native([], 0F, []),
            ImmutableArray<string>.Empty,
            ImmutableArray<SkyrimRaceMenuCustomMorphValue>.Empty,
            ImmutableArray<RaceMenuSculptPart>.Empty,
            actorWeight,
            new SkyrimRaceMenuSliderCatalog(
                ImmutableArray<SkyrimRaceMenuSliderDefinition>.Empty,
                ImmutableArray<SkyrimRaceMenuMorphExtension>.Empty),
            new SkyrimFaceMorphTriSource(SkyrimFaceMorphTriRole.Race, race),
            null,
            null,
            ImmutableArray<SkyrimFaceMorphTriSource>.Empty);
    }

    private static SkyrimFaceMorphSnapshot Native(
        IEnumerable<float> nam9,
        float trailing,
        IEnumerable<uint> nama)
    {
        ImmutableArray<float> nam9Values = [.. nam9];
        ImmutableArray<uint> namaValues = [.. nama];
        return new SkyrimFaceMorphSnapshot(nam9Values, trailing, namaValues,
            nam9Values.Length > 0, namaValues.Length > 0);
    }

    private static SkyrimRaceMenuSliderDefinition Slider(
        string name,
        SkyrimRaceMenuSliderType type,
        string lower,
        string upper) =>
        new("NordRace", SkyrimRaceMenuSliderGender.Female, name,
            SkyrimRaceMenuSliderCategory.Face, type, lower, upper, 10,
            new PluginName("Catalog.esp"), new AssetPath("meshes/catalog.slider"), 1);

    private static SseTriHeadDocument Document(string path, params (string Name, float X)[] morphs) =>
        new(new AssetPath(path), FixtureHash, 1, 0, 0, 0,
            [Vector3.Zero],
            [.. morphs.Select(morph => new SseTriHeadMorph(morph.Name,
                SseTriHeadMorphEncoding.DenseInt16, 1F,
                [new SseTriHeadVertexDelta(0, new Vector3(morph.X, 0F, 0F))]))]);

    private static SkyrimFaceMorphChannel Channel(string name, float weight, Vector3 delta) =>
        new(name, weight, [new SseTriHeadVertexDelta(0, delta)], null, null,
        [new SkyrimFaceMorphContribution(SkyrimFaceMorphContributionKind.Nam9, "test", weight)]);

    private static SkyrimRaceMenuCatalogAsset TextAsset(string path, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        return new SkyrimRaceMenuCatalogAsset(new AssetPath(path), Hash(bytes), [.. bytes]);
    }

    private static SseTriHeadReadResult ReadTri(
        byte[] bytes,
        string path = "meshes/fixture.tri",
        Sha256Hash? expectedHash = null) =>
        new SseTriHeadReader().Read(new SseTriHeadReadRequest(
            new AssetPath(path), expectedHash ?? Hash(bytes), [.. bytes]));

    private static byte[] WriteTri(
        Vector3[] baseVertices,
        uint[] triangles,
        Vector2[] uvs,
        uint[] textureTriangles,
        DenseFixture[] denseMorphs,
        ModifierFixture[] modifiers)
    {
        Assert(triangles.Length % 3 == 0 && textureTriangles.Length == triangles.Length,
            "TRI fixture triangle arrays are inconsistent.");
        Assert(denseMorphs.All(morph => morph.Deltas.Length == baseVertices.Length),
            "TRI fixture dense morph topology is inconsistent.");

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("FRTRI003"));
        writer.Write((uint)baseVertices.Length);
        writer.Write((uint)(triangles.Length / 3));
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write((uint)uvs.Length);
        writer.Write(0U);
        writer.Write((uint)denseMorphs.Length);
        writer.Write((uint)modifiers.Length);
        writer.Write((uint)modifiers.Sum(modifier => modifier.AbsolutePositions.Length));
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(0U);
        foreach (Vector3 vertex in baseVertices)
        {
            WriteVector(writer, vertex);
        }

        foreach (ModifierFixture modifier in modifiers)
        {
            foreach (Vector3 vertex in modifier.AbsolutePositions)
            {
                WriteVector(writer, vertex);
            }
        }

        foreach (uint index in triangles)
        {
            writer.Write(index);
        }

        foreach (Vector2 uv in uvs)
        {
            writer.Write(uv.X);
            writer.Write(uv.Y);
        }

        foreach (uint index in textureTriangles)
        {
            writer.Write(index);
        }

        foreach (DenseFixture morph in denseMorphs)
        {
            WriteName(writer, morph.Name);
            writer.Write(morph.Multiplier);
            foreach ((short x, short y, short z) in morph.Deltas)
            {
                writer.Write(x);
                writer.Write(y);
                writer.Write(z);
            }
        }

        foreach (ModifierFixture modifier in modifiers)
        {
            WriteName(writer, modifier.Name);
            writer.Write((uint)modifier.Indices.Length);
            foreach (uint index in modifier.Indices)
            {
                writer.Write(index);
            }
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        writer.Write((uint)(bytes.Length + 1));
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static SkyrimFaceMorphPlan RequirePlan(SkyrimFaceMorphPlanBuildResult result)
    {
        Assert(result.Accepted && result.Plan is not null,
            "Expected plan was refused: " + Format(result.Diagnostics));
        return result.Plan ??
               throw new InvalidOperationException("Accepted build result omitted its plan.");
    }

    private static SkyrimFaceMorphChannel AssertChannel(
        SkyrimFaceMorphPlan plan,
        string name,
        float expectedWeight)
    {
        SkyrimFaceMorphChannel channel = plan.Channels.Single(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Assert(channel.Weight == expectedWeight,
            $"Channel '{name}' weight was {channel.Weight}, expected {expectedWeight}.");
        return channel;
    }

    private static void AssertRefused(
        SseTriHeadReadResult result,
        string expectedCode) =>
        Assert(!result.Accepted && HasCode(result.Diagnostics, expectedCode),
            $"Expected refusal '{expectedCode}', got: {Format(result.Diagnostics)}");

    private static void AssertRefused(
        SkyrimRaceMenuCatalogParseResult result,
        string expectedCode) =>
        Assert(!result.Accepted && HasCode(result.Diagnostics, expectedCode),
            $"Expected refusal '{expectedCode}', got: {Format(result.Diagnostics)}");

    private static void AssertRefused(
        SkyrimFaceMorphPlanBuildResult result,
        string expectedCode) =>
        Assert(!result.Accepted && HasCode(result.Diagnostics, expectedCode),
            $"Expected refusal '{expectedCode}', got: {Format(result.Diagnostics)}");

    private static void AssertRefused(
        SkyrimFaceMorphEvaluationResult result,
        string expectedCode) =>
        Assert(!result.Accepted && HasCode(result.Diagnostics, expectedCode),
            $"Expected refusal '{expectedCode}', got: {Format(result.Diagnostics)}");

    private static bool HasCode(IEnumerable<Diagnostic> diagnostics, string code) =>
        diagnostics.Any(item => item.Code.Equals(code, StringComparison.Ordinal));

    private static string Format(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}:{item.Message}"));

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static string FindProjectRoot() =>
        TestAuthorityWorkspace.ResolveProjectRoot(
            AppContext.BaseDirectory);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record DenseFixture(
        string Name,
        float Multiplier,
        (short X, short Y, short Z)[] Deltas);

    private sealed record ModifierFixture(
        string Name,
        uint[] Indices,
        Vector3[] AbsolutePositions);
}
