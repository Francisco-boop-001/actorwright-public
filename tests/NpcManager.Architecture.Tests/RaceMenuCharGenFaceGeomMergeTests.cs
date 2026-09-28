using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private const string CharGenPath =
        @"K:\ExampleWorkspace\projects\Emi2FreshBuild\01-source-copies\fresh-export-drop\emi2-neutral.nif";
    private const string CarrierPath =
        @"K:\ExampleWorkspace\projects\Emi2FreshBuild\03-builds\feasibility-probes\ck-carrier-root\Data\meshes\actors\character\FaceGenData\FaceGeom\EmiCarrierProbe.esp\00000800.nif";
    private const string OracleRoot =
        @"K:\ExampleWorkspace\projects\Emi2FreshBuild\01-source-copies\runtime-face-oracle\20260718-073049-686";
    private const string ChelRaceMenuExportPath =
        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\chel-racemenu-export-20260725\Chelaccepted.nif";
    private const string ChelRaceMenuExportSha256 =
        "F860F5E1E56344151FAA92686F761CA9D2D87FC69A7AD8830FC9C11053DEA1EA";

    private static readonly ImmutableDictionary<string, Sha256Hash> ExpectedTopology =
        new Dictionary<string, Sha256Hash>(StringComparer.Ordinal)
        {
            ["00KLH_FemaleHeadNord"] = Hash("E70076F7E1A7D0E6297EF1BDA2A8CDA3E3AC9C30A9BFD2A115D7BAF3184F82D6"),
            ["FemaleMouthHumanoidDefault"] = Hash("36FDDB9E35D575E37B8D4F99217256A543513A827530C22A73C1D6AB2527D2F2"),
            ["MJBFemaleEyesHumanGreen04"] = Hash("068CACAA074F9AD223EC7BD91386D9D51822B4ABADDEBC277BB8C2BD16668F53"),
            ["KoralinaEyebrowsF02"] = Hash("517D2D69954459EE39A4A14AF540A54A2DC7BE425EAAE7A2251A2EEFFDA51528"),
            ["0_HAIRLINE_Female_Human_Straight"] = Hash("6B790FE1A74E7CE1659F3E1DCCB1080B1054FECACD1BC332B61BAA9A83B3C94C"),
            ["0Lassi"] = Hash("07F4E98B1266ABFD44E35E0F43F430F105DCF5C1EDA674326DD858C9F3C5385C"),
            ["0LassiHL"] = Hash("07F4E98B1266ABFD44E35E0F43F430F105DCF5C1EDA674326DD858C9F3C5385C")
        }.ToImmutableDictionary(StringComparer.Ordinal);

    private static async Task TestRaceMenuCharGenFaceGeomMerge()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var outputDirectory = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work",
            $"facegeom-merge-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "00000800.nif");
        try
        {
            var policy = new KOnlyWorkspacePolicy(labRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var service = new RaceMenuCharGenFaceGeomMergeService(policy, labRoot);
            var request = new RaceMenuCharGenFaceGeomMergeAnalyzeRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(CharGenPath),
                Hash("BAF9414F24E9894311C5DF4F8921929D637854CCE3CD43895F572D3A774E3CC5"),
                new WorkspacePath(CarrierPath),
                Hash("4658408FF08F77F2C64EA8AD1837B6A449D9BD4B3958BDF21F4EECDB516EC2F9"),
                new WorkspacePath(outputPath),
                BuildAuthorities());

            var analysis = await service.AnalyzeAsync(request, CancellationToken.None);
            Assert(analysis.Accepted && analysis.Proposal is not null,
                $"Real Emi2 merge analysis failed: {Diagnostics(analysis.Diagnostics)}");
            Assert(!File.Exists(outputPath), "Analyze wrote the FaceGeom output.");
            var proposal = analysis.Proposal ?? throw new InvalidOperationException(
                "Accepted analysis did not return a proposal.");
            Assert(proposal.CharGenBlockCount == 37 && proposal.CharGenDynamicShapeCount == 5,
                "The hash-bound CharGen structure drifted.");
            Assert(proposal.CarrierStructure.BlockCount == 51 &&
                   proposal.CarrierStructure.DynamicShapeCount == 7,
                "The complete carrier structure drifted.");
            Assert(proposal.ShapeDispositions.Length == 7,
                "Every carrier shape must have one explicit disposition.");
            Assert(proposal.ShapeDispositions.Count(item =>
                       item.Source.Route == RaceMenuCharGenFaceGeomShapeRoute.ExternalXyzOracle) == 4 &&
                   proposal.ShapeDispositions.Count(item =>
                       item.Source.Route == RaceMenuCharGenFaceGeomShapeRoute.CharGenXyz) == 3 &&
                   proposal.ShapeDispositions.All(item =>
                       item.Source.Route != RaceMenuCharGenFaceGeomShapeRoute.CarrierPreserved),
                "The accepted four-oracle/three-CharGen route table changed.");
            AssertExplicitRouteTable(proposal.ShapeDispositions);

            var result = await service.ApplyAsync(proposal, CancellationToken.None);
            Assert(result.Written && result.Verified && result.Artifact is not null &&
                   result.Verification is { Verified: true },
                $"Real Emi2 merge materialization failed: {Diagnostics(result.Diagnostics)}");
            Assert(File.Exists(outputPath), "Apply did not materialize the output NIF.");
            var artifact = result.Artifact ?? throw new InvalidOperationException(
                "Successful materialization did not return an artifact.");
            var verification = result.Verification ?? throw new InvalidOperationException(
                "Successful materialization did not return verification.");
            Assert(!artifact.CreationKitAuthority && !artifact.RuntimeAuthority,
                "The static merge overclaimed CK or runtime authority.");
            Assert(artifact.Structure.BlockCount == 51 &&
                   artifact.Structure.DynamicShapeCount == 7,
                "The materialized output lost complete-carrier structure.");
            Assert(verification.VerifiedShapeNames.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(ExpectedTopology.Keys),
                "Post-write verification did not cover all seven shapes.");
            Assert(verification.VerifiedPositionLaneCount ==
                   proposal.ShapeDispositions.Sum(item => item.VertexCount),
                "Post-write XYZ lane evidence is incomplete.");
            AssertExactByteSurface(proposal, outputPath);
            Console.WriteLine(
                $"EVIDENCE Emi2 FaceGeom SHA256={artifact.OutputSha256} shapes={artifact.RoutedShapeCount} changed={artifact.ChangedPositionShapeCount} expanded-radii={artifact.ExpandedRadiusCount}");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (Directory.Exists(outputDirectory) &&
                !Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                Directory.Delete(outputDirectory);
        }

        await AssertGeneratedXyzRoute();
        await AssertAllDynamicDirectBuild();
        await AssertManagerAssembledAllDynamicDirectBuild();
        AssertManagerQualifiedCarrierSelection();
    }

    private static async Task TestRaceMenuExportedCompleteCarrier()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var root = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work",
            $"chel-racemenu-export-test-{Guid.NewGuid():N}");
        var charGenPath = Path.Combine(root, "Chelaccepted.nif");
        var carrierPath = Path.Combine(root, "Chelaccepted-carrier.nif");
        var outputPath = Path.Combine(root, "Chelaccepted-output.nif");
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(ChelRaceMenuExportPath, charGenPath);
            File.Copy(ChelRaceMenuExportPath, carrierPath);
            var expectedHash = Hash(ChelRaceMenuExportSha256);
            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath(@"F:\ExampleGame"));
            var service = new RaceMenuDirectCharGenFaceGeomBuildService(
                new RaceMenuCharGenFaceGeomMergeService(policy, labRoot),
                policy,
                labRoot);

            RaceMenuDirectCharGenFaceGeomBuildResult result =
                await service.BuildAsync(
                    new RaceMenuDirectCharGenFaceGeomBuildRequest(
                        new WorkspacePath(charGenPath),
                        expectedHash,
                        new WorkspacePath(carrierPath),
                        expectedHash,
                        new WorkspacePath(outputPath))
                    {
                        QualificationProfile =
                            QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete
                    },
                    CancellationToken.None);

            Assert(result.Written && result.Verified &&
                   result.Artifact is
                   {
                       Proposal.QualificationProfile:
                           QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete,
                       RoutedShapeCount: 12,
                       ChangedPositionShapeCount: 0,
                       RuntimeAuthority: false
                   } &&
                   result.IndependentVerification is
                   {
                       Verified: true,
                       OutputStructure.DynamicShapeCount: 12
                   } &&
                   File.Exists(outputPath),
                "The genuine Chel RaceMenu export was not preserved as a complete " +
                $"12-shape carrier: {Diagnostics(result.Diagnostics)}");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertAllDynamicDirectBuild()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        string root = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work",
            $"direct-chargen-all-dynamic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string charGenPath = Path.Combine(root, "all-dynamic-chargen.nif");
        string carrierPath = Path.Combine(root, "all-dynamic-carrier.nif");
        string outputPath = Path.Combine(root, "all-dynamic-output.nif");
        try
        {
            File.Copy(CarrierPath, charGenPath, overwrite: false);
            File.Copy(CarrierPath, carrierPath, overwrite: false);
            Sha256Hash inputHash = HashBytes(File.ReadAllBytes(CarrierPath));
            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath(@"F:\ExampleGame"));
            var service = new RaceMenuDirectCharGenFaceGeomBuildService(
                new RaceMenuCharGenFaceGeomMergeService(policy, labRoot),
                policy,
                labRoot);
            RaceMenuDirectCharGenFaceGeomBuildResult result =
                await service.BuildAsync(
                    new RaceMenuDirectCharGenFaceGeomBuildRequest(
                        new WorkspacePath(charGenPath),
                        inputHash,
                        new WorkspacePath(carrierPath),
                        inputHash,
                        new WorkspacePath(outputPath)),
                    CancellationToken.None);

            Assert(result.Written && result.Verified &&
                   result.Artifact is
                   {
                       RoutedShapeCount: 7,
                       ChangedPositionShapeCount: 0,
                       ExpandedRadiusCount: 2,
                       RuntimeAuthority: false
                   } &&
                   result.Artifact.OutputSha256 != inputHash &&
                   result.IndependentVerification is { Verified: true },
                $"An all-dynamic same-topology CharGen/carrier pair was refused: {Diagnostics(result.Diagnostics)}");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertManagerAssembledAllDynamicDirectBuild()
    {
        const string historicalBrokenSource =
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\sophia-manager-slot0-regression-20260724\Sophia-Loren-COR-slot0.nif";
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        string root = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\tests",
            $"direct-chargen-manager-assembled-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string historicalCharGenPath = Path.Combine(
            root, "historical-manager-chargen.nif");
        string historicalCarrierPath = Path.Combine(
            root, "historical-manager-carrier.nif");
        string historicalOutputPath = Path.Combine(
            root, "historical-manager-output.nif");
        string charGenPath = Path.Combine(root, "manager-chargen.nif");
        string carrierPath = Path.Combine(root, "manager-carrier.nif");
        string outputPath = Path.Combine(root, "manager-output.nif");
        try
        {
            File.Copy(historicalBrokenSource, historicalCharGenPath,
                overwrite: false);
            File.Copy(historicalBrokenSource, historicalCarrierPath,
                overwrite: false);
            Sha256Hash historicalHash = Hash(
                "AE9FC7054269913CEE5F47268ECF9BC0481EC4D84E19B5F797114DDC8C747778");
            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath(@"F:\ExampleGame"));
            var service = new RaceMenuDirectCharGenFaceGeomBuildService(
                new RaceMenuCharGenFaceGeomMergeService(policy, labRoot),
                policy,
                labRoot);
            RaceMenuDirectCharGenFaceGeomBuildResult historicalResult =
                await service.BuildAsync(
                    new RaceMenuDirectCharGenFaceGeomBuildRequest(
                        new WorkspacePath(historicalCharGenPath),
                        historicalHash,
                        new WorkspacePath(historicalCarrierPath),
                        historicalHash,
                        new WorkspacePath(historicalOutputPath))
                    {
                        QualificationProfile =
                            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete
                    },
                    CancellationToken.None);
            Assert(!historicalResult.Written &&
                   !historicalResult.Verified &&
                   historicalResult.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-facetint-slot") &&
                   !File.Exists(historicalOutputPath),
                "The direct CharGen merge accepted the historical Sophia slot-0 FaceTint carrier.");

            var fixturePlugin = new PluginName("GeometryFixtures.esp");
            ImmutableArray<SseFaceGeomCarrierAssemblyPart> parts =
                HeadpartModelAssets.Select((asset, index) =>
                {
                    ExpectedHeadpartGeometry expected =
                        ExpectedHeadpartGeometryByAsset[asset];
                    return new SseFaceGeomCarrierAssemblyPart(
                        new FormReference(fixturePlugin,
                            new FormId(checked((uint)(0x800 + index)))),
                        new AssetPath(asset),
                        new Sha256Hash(expected.SourceSha256),
                        ImmutableArray.CreateRange(File.ReadAllBytes(
                            PhysicalPath(asset))),
                        expected.CarrierShapeName,
                        UsesFaceTint: asset == HeadpartModelAssets[0]);
                }).ToImmutableArray();
            SseFaceGeomCarrierAssemblyResult assembled =
                new SseFaceGeomCarrierAssembler().Assemble(
                    new SseFaceGeomCarrierAssemblyRequest(
                        parts,
                        new AssetPath(
                            "Textures/Actors/Character/FaceGenData/FaceTint/ManagerCarrier.esp/00000800.dds")));
            Assert(assembled is
                {
                    Assembled: true,
                    Verified: true,
                    Artifact: not null
                },
                "The corrected direct-build carrier fixture could not be assembled: " +
                Diagnostics(assembled.Diagnostics));
            SseFaceGeomCarrierAssemblyArtifact sourceArtifact =
                assembled.Artifact!;
            File.WriteAllBytes(charGenPath, sourceArtifact.Bytes.ToArray());
            File.WriteAllBytes(carrierPath, sourceArtifact.Bytes.ToArray());

            RaceMenuDirectCharGenFaceGeomBuildResult result =
                await service.BuildAsync(
                    new RaceMenuDirectCharGenFaceGeomBuildRequest(
                        new WorkspacePath(charGenPath),
                        sourceArtifact.Sha256,
                        new WorkspacePath(carrierPath),
                        sourceArtifact.Sha256,
                        new WorkspacePath(outputPath))
                    {
                        QualificationProfile =
                            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete
                    },
                    CancellationToken.None);

            Assert(result.Written && result.Verified &&
                   result.Artifact is
                   {
                       Proposal.QualificationProfile:
                            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
                       RoutedShapeCount: 7,
                       ChangedPositionShapeCount: 0,
                       RuntimeAuthority: false
                   } &&
                   result.IndependentVerification is
                    {
                        Verified: true,
                        OutputStructure.DynamicShapeCount: 7
                    },
                "The explicit Manager-assembled direct build did not preserve all seven exact shapes: " +
                Diagnostics(result.Diagnostics));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertManagerQualifiedCarrierSelection()
    {
        var charGen = new WorkspacePath(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work\manager-built.nif");
        var provider = new WorkspacePath(CarrierPath);
        var charGenHash = Hash(
            "AE9FC7054269913CEE5F47268ECF9BC0481EC4D84E19B5F797114DDC8C747778");
        var providerHash = Hash(
            "4658408FF08F77F2C64EA8AD1837B6A449D9BD4B3958BDF21F4EECDB516EC2F9");

        RaceMenuDirectFaceGeomCarrierSelection fallback =
            RaceMenuDirectFaceGeomCarrierSelector.Select(
                charGen,
                charGenHash,
                provider,
                providerHash,
                managerAuthority: null);
        Assert(fallback.SourceNif == provider &&
               fallback.SourceSha256 == providerHash &&
               !fallback.RequiresOwnedCopy,
            "An ordinary schema-6 request stopped using its qualified provider carrier.");

        RaceMenuDirectFaceGeomCarrierSelection managerOwned =
            RaceMenuDirectFaceGeomCarrierSelector.Select(
                charGen,
                charGenHash,
                provider,
                providerHash,
                new RaceMenuManagerOwnedFaceGeomCarrierAuthority(
                    charGen, charGenHash));
        Assert(managerOwned.SourceNif == charGen &&
               managerOwned.SourceSha256 == charGenHash &&
               managerOwned.RequiresOwnedCopy &&
               managerOwned.QualificationProfile ==
                   QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
            "A Manager-qualified companion did not replace the unrelated provider carrier.");

        RaceMenuDirectFaceGeomCarrierSelection exported =
            RaceMenuDirectFaceGeomCarrierSelector.Select(
                charGen,
                charGenHash,
                provider,
                providerHash,
                managerAuthority: null,
                externalAuthority: new RaceMenuNpcExternalCharGenExportAuthority(
                    new WorkspacePath(
                        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\chel-racemenu-export-authority.json"),
                    Hash("1111111111111111111111111111111111111111111111111111111111111111"),
                    "chel-racemenu-export-20260725",
                    new WorkspacePath(
                        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\UBE_Chel.jslot"),
                    Hash("2222222222222222222222222222222222222222222222222222222222222222"),
                    charGen,
                    charGenHash,
                    new WorkspacePath(
                        @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\Chelaccepted.dds"),
                    Hash("3333333333333333333333333333333333333333333333333333333333333333"),
                    new FormReference(
                        new PluginName("UBE_AllRace.esp"),
                        new FormId(0x0005A18E)),
                    NpcSex.Female,
                    UserConfirmedVisualMatch: true,
                    RuntimeAuthority: false));
        Assert(exported.SourceNif == charGen &&
               exported.SourceSha256 == charGenHash &&
               exported.RequiresOwnedCopy &&
               exported.QualificationProfile ==
                   QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete,
            "A qualified external RaceMenu export did not select its exact CharGen NIF.");

        AssertThrows<InvalidDataException>(
            () => RaceMenuDirectFaceGeomCarrierSelector.Select(
                charGen,
                charGenHash,
                provider,
                providerHash,
                new RaceMenuManagerOwnedFaceGeomCarrierAuthority(
                    charGen,
                    Hash("0000000000000000000000000000000000000000000000000000000000000000"))));
    }

    private static async Task AssertGeneratedXyzRoute()
    {
        const string generatedShape = "00KLH_FemaleHeadNord";
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var outputDirectory = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work",
            $"facegeom-generated-xyz-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var generatedPath = Path.Combine(outputDirectory, "compiled-head-f32le.bin");
        var outputPath = Path.Combine(outputDirectory, "00000800.nif");
        try
        {
            var carrier = SseFaceGeomCarrierCodec.Parse(File.ReadAllBytes(CarrierPath));
            var shape = DynamicShapes(carrier)[generatedShape];
            var generatedXyz = ExtractXyz(carrier.Data, shape.DynamicGeometry!);
            var originalX = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(generatedXyz.AsSpan(0, sizeof(float))));
            BinaryPrimitives.WriteInt32LittleEndian(
                generatedXyz.AsSpan(0, sizeof(float)),
                BitConverter.SingleToInt32Bits(originalX + 0.125F));
            File.WriteAllBytes(generatedPath, generatedXyz);
            var generatedHash = HashBytes(generatedXyz);
            var authorities = BuildAuthorities().Select(authority =>
                    authority.CarrierShapeName == generatedShape
                        ? new RaceMenuCharGenFaceGeomGeneratedXyzAuthority(
                            generatedShape,
                            new WorkspacePath(generatedPath),
                            generatedHash,
                            shape.DynamicGeometry!.VertexCount,
                            ExpectedTopology[generatedShape],
                            "Product-compiled float32-le XYZ regression evidence.")
                        : authority)
                .ToImmutableArray();
            var policy = new KOnlyWorkspacePolicy(labRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var service = new RaceMenuCharGenFaceGeomMergeService(policy, labRoot);
            var request = new RaceMenuCharGenFaceGeomMergeAnalyzeRequest(
                GameEdition.SkyrimSpecialEdition,
                new WorkspacePath(CharGenPath),
                Hash("BAF9414F24E9894311C5DF4F8921929D637854CCE3CD43895F572D3A774E3CC5"),
                new WorkspacePath(CarrierPath),
                Hash("4658408FF08F77F2C64EA8AD1837B6A449D9BD4B3958BDF21F4EECDB516EC2F9"),
                new WorkspacePath(outputPath),
                authorities);

            var analysis = await service.AnalyzeAsync(request, CancellationToken.None);
            Assert(analysis.Accepted && analysis.Proposal is not null,
                $"Generated XYZ analysis failed: {Diagnostics(analysis.Diagnostics)}");
            var proposal = analysis.Proposal ?? throw new InvalidOperationException(
                "Accepted generated XYZ analysis did not return a proposal.");
            var generatedDisposition = proposal.ShapeDispositions.Single(item =>
                item.CarrierShapeName == generatedShape);
            Assert(generatedDisposition.Source is
                RaceMenuCharGenFaceGeomGeneratedXyzSourceEvidence generatedEvidence &&
                generatedEvidence.Route == RaceMenuCharGenFaceGeomShapeRoute.GeneratedXyz &&
                generatedEvidence.GeneratedXyzFile == new WorkspacePath(generatedPath) &&
                generatedEvidence.GeneratedXyzFileSha256 == generatedHash &&
                generatedEvidence.VertexCount == shape.DynamicGeometry!.VertexCount &&
                generatedEvidence.TopologySha256 == ExpectedTopology[generatedShape],
                "Generated XYZ did not retain distinct hash, vertex-count, and topology evidence.");

            var tamperedXyz = (byte[])generatedXyz.Clone();
            tamperedXyz[^1] ^= 0x01;
            File.WriteAllBytes(generatedPath, tamperedXyz);
            var refusedApply = await service.ApplyAsync(proposal, CancellationToken.None);
            Assert(!refusedApply.Written && !refusedApply.Verified &&
                   !File.Exists(outputPath) &&
                   refusedApply.Diagnostics.Any(item =>
                       item.Code == "chargen-carrier-generated-xyz-binding"),
                "Apply did not refuse a tampered generated XYZ source before writing output.");

            File.WriteAllBytes(generatedPath, generatedXyz);
            var applied = await service.ApplyAsync(proposal, CancellationToken.None);
            Assert(applied.Written && applied.Verified &&
                   applied.Verification is { Verified: true },
                $"Generated XYZ apply/verify failed: {Diagnostics(applied.Diagnostics)}");
            var outputHashBeforeRefusedOverwrite = HashBytes(File.ReadAllBytes(outputPath));
            var refusedOverwrite = await service.ApplyAsync(proposal, CancellationToken.None);
            Assert(!refusedOverwrite.Written && !refusedOverwrite.Verified &&
                   refusedOverwrite.Diagnostics.Any(item =>
                       item.Code == "chargen-carrier-output-exists") &&
                   HashBytes(File.ReadAllBytes(outputPath)) == outputHashBeforeRefusedOverwrite,
                "Generated XYZ materialization overwrote an existing output.");

            File.WriteAllBytes(generatedPath, tamperedXyz);
            var refusedVerification = await service.VerifyAsync(
                proposal, CancellationToken.None);
            Assert(!refusedVerification.Verified &&
                   refusedVerification.Diagnostics.Any(item =>
                       item.Code == "chargen-carrier-generated-xyz-binding"),
                "Verification did not refuse a tampered generated XYZ source.");
            File.WriteAllBytes(generatedPath, generatedXyz);
            var verification = await service.VerifyAsync(proposal, CancellationToken.None);
            Assert(verification.Verified,
                $"Restored generated XYZ verification failed: {Diagnostics(verification.Diagnostics)}");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(generatedPath)) File.Delete(generatedPath);
            if (Directory.Exists(outputDirectory) &&
                !Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                Directory.Delete(outputDirectory);
        }
    }

    private static ImmutableArray<RaceMenuCharGenFaceGeomShapeAuthority> BuildAuthorities() =>
    [
        External("00KLH_FemaleHeadNord", "head-fod-f32le.bin",
            "67D1297F2EB3E31F42B4C2888690CA2C1EC82630DA2451B9F9D505584EBE935F", 3832),
        External("FemaleMouthHumanoidDefault", "mouth-fod-f32le.bin",
            "E15C1E9085A7D5709AA87BCEF43A27672B48C9208EF5C1CBEC8D8B16E299B307", 141),
        External("MJBFemaleEyesHumanGreen04", "eyes-fod-f32le.bin",
            "59992B915E6549894D6AA9B5BB6149BE8D756AE4CE186D78BDD6ABAC5E27E1DF", 176),
        External("KoralinaEyebrowsF02", "brows-fod-f32le.bin",
            "54410033FD15EECFC12654A3380474CC5561D47999BA1E131CBA08CF41A292A7", 371),
        CharGen("0_HAIRLINE_Female_Human_Straight"),
        CharGen("0Lassi"),
        CharGen("0LassiHL")
    ];

    private static RaceMenuCharGenFaceGeomExternalXyzAuthority External(
        string shape,
        string file,
        string hash,
        int vertexCount) =>
        new(shape, new WorkspacePath(Path.Combine(OracleRoot, file)), Hash(hash), vertexCount,
            ExpectedTopology[shape], "Accepted Emi2 float32-le XYZ oracle.");

    private static RaceMenuCharGenFaceGeomCharGenXyzAuthority CharGen(string shape) =>
        new(shape, ExpectedTopology[shape], "Hash-bound RaceMenu CharGen hair geometry.");

    private static void AssertExplicitRouteTable(
        ImmutableArray<RaceMenuCharGenFaceGeomShapeDisposition> dispositions)
    {
        var byName = dispositions.ToDictionary(item => item.CarrierShapeName, StringComparer.Ordinal);
        foreach (var (name, topology) in ExpectedTopology)
        {
            Assert(byName.ContainsKey(name),
                $"Missing disposition for {name}.");
            var disposition = byName[name];
            Assert(disposition.CarrierTopologySha256 == topology &&
                   disposition.Source.TopologySha256 == topology,
                $"Topology authority drifted for {name}.");
        }
        Assert(byName["00KLH_FemaleHeadNord"].Source.Route ==
               RaceMenuCharGenFaceGeomShapeRoute.ExternalXyzOracle,
            "The accepted external head oracle did not override the same-name CharGen head.");
        foreach (var name in new[]
                 {
                     "0_HAIRLINE_Female_Human_Straight", "0Lassi", "0LassiHL"
                 })
            Assert(byName[name].Source.Route == RaceMenuCharGenFaceGeomShapeRoute.CharGenXyz,
                $"Hair shape {name} lost its explicit CharGen route.");
    }

    private static void AssertExactByteSurface(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        string outputPath)
    {
        var charGen = SseFaceGeomCarrierCodec.Parse(File.ReadAllBytes(CharGenPath));
        var carrierBytes = File.ReadAllBytes(CarrierPath);
        var outputBytes = File.ReadAllBytes(outputPath);
        var carrier = SseFaceGeomCarrierCodec.Parse(carrierBytes);
        var output = SseFaceGeomCarrierCodec.Parse(outputBytes);
        Assert(carrierBytes.Length == outputBytes.Length,
            "The merge changed the complete-carrier byte length.");
        var charGenShapes = DynamicShapes(charGen);
        var carrierShapes = DynamicShapes(carrier);
        var outputShapes = DynamicShapes(output);
        var authorized = new bool[carrierBytes.Length];

        foreach (var disposition in proposal.ShapeDispositions)
        {
            var carrierShape = carrierShapes[disposition.CarrierShapeName];
            var outputShape = outputShapes[disposition.CarrierShapeName];
            var carrierLayout = carrierShape.DynamicGeometry!;
            var outputLayout = outputShape.DynamicGeometry!;
            var expectedPositions = disposition.Source switch
            {
                RaceMenuCharGenFaceGeomExternalXyzSourceEvidence external =>
                    File.ReadAllBytes(external.XyzFile.Value),
                RaceMenuCharGenFaceGeomCharGenSourceEvidence =>
                    ExtractXyz(charGen.Data,
                        charGenShapes[disposition.CarrierShapeName].DynamicGeometry!),
                RaceMenuCharGenFaceGeomCarrierPreservedSourceEvidence =>
                    ExtractXyz(carrier.Data, carrierLayout),
                _ => throw new InvalidOperationException("Unsupported test route.")
            };
            Assert(expectedPositions.Length == disposition.VertexCount * 12,
                $"Expected XYZ length drifted for {disposition.CarrierShapeName}.");
            for (var vertex = 0; vertex < disposition.VertexCount; vertex++)
            {
                var carrierOffset = carrierLayout.VertexDataOffset + vertex * 16;
                var outputOffset = outputLayout.VertexDataOffset + vertex * 16;
                Assert(output.Data.AsSpan(outputOffset, 12).SequenceEqual(
                        expectedPositions.AsSpan(vertex * 12, 12)),
                    $"XYZ transplant drifted for {disposition.CarrierShapeName} vertex {vertex}.");
                Assert(output.Data.AsSpan(outputOffset + 12, 4).SequenceEqual(
                        carrier.Data.AsSpan(carrierOffset + 12, 4)),
                    $"Fourth float changed for {disposition.CarrierShapeName} vertex {vertex}.");
                Array.Fill(authorized, true, outputOffset, 12);
            }
            if (disposition.RadiusChanged)
                Array.Fill(authorized, true, disposition.CarrierRadiusOffset, sizeof(float));
        }

        for (var offset = 0; offset < carrierBytes.Length; offset++)
            Assert(authorized[offset] || carrierBytes[offset] == outputBytes[offset],
                $"Byte {offset} changed outside authorized XYZ/radius lanes.");
    }

    private static Dictionary<string, SseNifBlock> DynamicShapes(SseNifDocument document) =>
        document.Blocks.Where(item => item.Type == "BSDynamicTriShape")
            .ToDictionary(item => item.Name!, StringComparer.Ordinal);

    private static byte[] ExtractXyz(byte[] data, SseNifDynamicGeometryLayout layout)
    {
        var result = new byte[layout.VertexCount * 12];
        for (var vertex = 0; vertex < layout.VertexCount; vertex++)
            data.AsSpan(layout.VertexDataOffset + vertex * layout.VertexStride, 12)
                .CopyTo(result.AsSpan(vertex * 12, 12));
        return result;
    }

    private static Sha256Hash Hash(string value) => new(value);

    private static Sha256Hash HashBytes(byte[] value) =>
        new(Convert.ToHexString(SHA256.HashData(value)));

    private static string Diagnostics(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item =>
            $"{item.Severity}:{item.Code}:{item.Message}"));
}
