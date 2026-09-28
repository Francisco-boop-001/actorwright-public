using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static readonly JsonSerializerOptions DurableEvidenceJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task TestQualifiedCarrierAlternateDataStream()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace");
        var service = new QualifiedFaceGeomCarrierService(
            new KOnlyWorkspacePolicy(root, new WorkspacePath("F:\\ExampleGame")), root);
        var result = await service.AnalyzeAsync(
            new QualifiedFaceGeomCarrierAnalyzeRequest(
                new WorkspacePath("K:\\ExampleWorkspace\\carrier:stream.nif"),
                new Sha256Hash(new string('0', 64)),
                new WorkspacePath("K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\Data\\meshes\\actors\\character\\FaceGenData\\FaceGeom\\NpcManagerTest.esp\\00000800.nif"),
                new AssetPath("textures/actors/character/FaceGenData/FaceTint/NpcManagerTest.esp/00000800.dds")),
            CancellationToken.None);

        Assert(!result.Qualified && result.Diagnostics.Any(diagnostic =>
                diagnostic.Code is "alternate-data-stream-refused" or
                    "qualified-carrier-alternate-data-stream-refused"),
            "The standalone carrier service did not refuse an alternate-data-stream source path.");
    }

    private static Task TestQualifiedCarrierTopology()
    {
        var admitted = CreateAdmittedCarrierDocument();
        var admittedDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        SseFaceGeomCarrierCodec.Qualify(
            admitted,
            SseFaceGeomCarrierCodec.BuildStructure(admitted),
            admittedDiagnostics);
        Assert(!admittedDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            "The exact admitted seven-shape carrier topology was rejected: " +
            string.Join(", ", admittedDiagnostics.Select(diagnostic => diagnostic.Code)));

        var blocks = admitted.Blocks.ToBuilder();
        blocks[4] = blocks[4] with
        {
            References = blocks[4].References.Select(reference =>
                    reference.Kind == "shader" ? reference with { Target = 15 } : reference)
                .ToImmutableArray()
        };
        blocks[0] = blocks[0] with
        {
            References = blocks[0].References
                .Add(new SseNifReference("effect", 8))
                .Add(new SseNifReference("effect", 9))
        };
        var unrelated = admitted with { Blocks = blocks.ToImmutable() };
        var structure = SseFaceGeomCarrierCodec.BuildStructure(unrelated);
        Assert(structure.BlockCount == 51 && structure.ReachableBlockCount == 51 &&
               structure.ReachableCensus.SequenceEqual(
                   SseFaceGeomCarrierCodec.BuildStructure(admitted).ReachableCensus),
            "The negative topology fixture did not preserve the census precondition.");

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        SseFaceGeomCarrierCodec.Qualify(unrelated, structure, diagnostics);
        Assert(diagnostics.Any(diagnostic => diagnostic.Code == "qualified-carrier-shader-ownership") &&
               diagnostics.Any(diagnostic => diagnostic.Code == "qualified-carrier-unrelated-reference"),
            "An unrelated reachable block was able to satisfy the carrier census.");
        return Task.CompletedTask;
    }

    private static Task TestQualifiedCarrierAlphaTopology()
    {
        var admitted = CreateAdmittedCarrierDocument();
        var blocks = admitted.Blocks.ToBuilder();
        blocks[38] = blocks[38] with
        {
            References = blocks[38].References.Select(reference =>
                    reference.Kind == "alpha" ? reference with { Target = 10 } : reference)
                .ToImmutableArray()
        };
        var malformed = admitted with { Blocks = blocks.ToImmutable() };
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        SseFaceGeomCarrierCodec.Qualify(
            malformed,
            SseFaceGeomCarrierCodec.BuildStructure(malformed),
            diagnostics);
        Assert(diagnostics.Any(diagnostic => diagnostic.Code == "qualified-carrier-alpha-topology"),
            "The carrier accepted an alpha property on the sole legitimate alpha-less head shape.");
        return Task.CompletedTask;
    }

    private static async Task TestProvenQualifiedCarrierFixture()
    {
        var path = "K:\\ExampleWorkspace\\projects\\Emi2FreshBuild\\03-builds\\feasibility-probes\\ck-carrier-root\\Data\\meshes\\Actors\\Character\\FaceGenData\\FaceGeom\\EmiCarrierProbe.esp\\00000800.NIF";
        var document = SseFaceGeomCarrierCodec.Parse(File.ReadAllBytes(path));
        var structure = SseFaceGeomCarrierCodec.BuildStructure(document);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        SseFaceGeomCarrierCodec.Qualify(document, structure, diagnostics);
        var target = SseFaceGeomCarrierCodec.FindFaceTintTarget(document, diagnostics) ??
                     throw new InvalidOperationException("The proven fixture lost its unique head FaceTint route.");
        var requested = new AssetPath(
            "textures/actors/character/FaceGenData/FaceTint/NpcManagerBlank.esp/00000800.dds");
        var rewritten = SseFaceGeomCarrierCodec.RewriteFaceTintPath(document, target, requested);
        Assert(Convert.ToHexString(SHA256.HashData(rewritten)) ==
               "E17856E53FDD6DF8CDBD53BE4B6EAF9C007EF9F75D44F65B81F8AC88D8077D6A",
            "The admitted fixture rewrite changed its proven byte result.");
        var output = SseFaceGeomCarrierCodec.Parse(rewritten);
        SseFaceGeomCarrierCodec.Qualify(output, SseFaceGeomCarrierCodec.BuildStructure(output), diagnostics);
        var changed = SseFaceGeomCarrierCodec.VerifyRewrite(document, output, target, requested, diagnostics);
        Assert(!diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) &&
               structure.BlockCount == 51 && structure.ReachableBlockCount == 51 &&
               structure.DynamicShapeCount == 7 && changed.SequenceEqual([target.BlockIndex]),
            "The proven seven-shape workspace fixture no longer qualifies: " +
            string.Join(", ", diagnostics.Select(diagnostic => diagnostic.Code)));

        await AssertPrivateHeadTextureRoute(path);
    }

    private static async Task TestManagerAssembledQualifiedCarrierFixture()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        string root = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\tests",
            $"manager-assembled-qualified-carrier-{Guid.NewGuid():N}");
        string sourceDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string sourcePath = Path.Combine(sourceDirectory, "ManagerCarrier.nif");
        string outputDirectory = Path.Combine(
            root,
            "Data",
            "meshes",
            "actors",
            "character",
            "FaceGenData",
            "FaceGeom",
            "SophiaNpcManager.esp");
        Directory.CreateDirectory(outputDirectory);
        string output = Path.Combine(outputDirectory, "00000800.nif");
        var targetFaceTint = new AssetPath(
            "textures/actors/character/FaceGenData/FaceTint/SophiaNpcManager.esp/00000800.dds");
        var targetHeadTextures = new SkyrimPrivateHeadTexturePaths(
            new AssetPath("!COR/Head/FemaleHead.dds"),
            new AssetPath("!COR/Head/femalehead_msn_009.dds"),
            new AssetPath("!COR/Head/femalehead_sk.dds"),
            new AssetPath("Actors/Character/Male/BlankDetailmap.dds"),
            new AssetPath("!COR/Head/femalehead_s.dds"));
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        var service = new QualifiedFaceGeomCarrierService(policy, labRoot);

        try
        {
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
            var sourceFaceTint = new AssetPath(
                "Textures/Actors/Character/FaceGenData/FaceTint/ManagerCarrier.esp/00000800.dds");
            SseFaceGeomCarrierAssemblyResult assembled =
                new SseFaceGeomCarrierAssembler().Assemble(
                    new SseFaceGeomCarrierAssemblyRequest(parts,
                        sourceFaceTint));
            Assert(assembled is
                {
                    Assembled: true,
                    Verified: true,
                    Artifact: not null
                },
                "The corrected Manager carrier fixture could not be assembled: " +
                Diagnostics(assembled.Diagnostics));
            SseFaceGeomCarrierAssemblyArtifact sourceArtifact =
                assembled.Artifact!;
            File.WriteAllBytes(sourcePath, sourceArtifact.Bytes.ToArray());
            Sha256Hash sourceHash = sourceArtifact.Sha256;

            QualifiedFaceGeomCarrierAnalysisResult defaultAnalysis =
                await service.AnalyzeAsync(
                    new QualifiedFaceGeomCarrierAnalyzeRequest(
                        new WorkspacePath(sourcePath),
                        sourceHash,
                        new WorkspacePath(output),
                        targetFaceTint),
                    CancellationToken.None);
            Assert(!defaultAnalysis.Qualified &&
                   defaultAnalysis.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-reachability") &&
                   !File.Exists(output),
                "The historical provider-pinned profile unexpectedly admitted the 58-block Sophia carrier.");

            QualifiedFaceGeomCarrierAnalysisResult managerAnalysis =
                await service.AnalyzeAsync(
                    new QualifiedFaceGeomCarrierAnalyzeRequest(
                        new WorkspacePath(sourcePath),
                        sourceHash,
                        new WorkspacePath(output),
                        targetFaceTint)
                    {
                        TargetHeadTextures = targetHeadTextures,
                        QualificationProfile =
                            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete
                    },
                    CancellationToken.None);
            Assert(managerAnalysis.Qualified &&
                   managerAnalysis.Proposal is
                   {
                       QualificationProfile:
                           QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
                       Structure:
                       {
                           BlockCount: 52,
                           ReachableBlockCount: 52,
                           RootCount: 1,
                           NiNodeCount: 3,
                           FadeNodeCount: 1,
                           DynamicShapeCount: 7,
                           NullChildReferenceCount: 0
                       }
                   } &&
                   !File.Exists(output),
                "The explicit Manager-assembled profile did not admit the corrected 52-block/seven-shape carrier: " +
                string.Join(", ", managerAnalysis.Diagnostics.Select(item => item.Code)));

            QualifiedFaceGeomCarrierMaterializationResult materialized =
                await service.ApplyAsync(
                    managerAnalysis.Proposal ??
                    throw new InvalidOperationException(
                        "Manager carrier analysis returned no proposal."),
                    CancellationToken.None);
            Assert(materialized.Written && materialized.Verified &&
                   materialized.Artifact is
                   {
                       SourceSha256: var artifactSourceHash,
                       SourceStructure.DynamicShapeCount: 7,
                       RuntimeAuthority: false
                   } &&
                   artifactSourceHash == sourceHash &&
                   materialized.Verification is
                   {
                       Verified: true,
                       Structure.DynamicShapeCount: 7,
                       ChangedBlocks.Length: 1
                   } &&
                   File.Exists(output),
                "The Manager-assembled carrier did not survive its one-block FaceTint rewrite and independent readback: " +
                string.Join(", ", materialized.Diagnostics.Select(item => item.Code)));

            const string historicalBrokenSource =
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\sophia-manager-slot0-regression-20260724\Sophia-Loren-COR-slot0.nif";
            string historicalOutputDirectory = Path.Combine(
                root,
                "historical",
                "Data",
                "meshes",
                "actors",
                "character",
                "FaceGenData",
                "FaceGeom",
                "SophiaNpcManager.esp");
            Directory.CreateDirectory(historicalOutputDirectory);
            string historicalOutput = Path.Combine(
                historicalOutputDirectory, "00000800.nif");
            QualifiedFaceGeomCarrierAnalysisResult historicalAnalysis =
                await service.AnalyzeAsync(
                    new QualifiedFaceGeomCarrierAnalyzeRequest(
                        new WorkspacePath(historicalBrokenSource),
                        new Sha256Hash(
                            "AE9FC7054269913CEE5F47268ECF9BC0481EC4D84E19B5F797114DDC8C747778"),
                        new WorkspacePath(historicalOutput),
                        targetFaceTint)
                    {
                        TargetHeadTextures = targetHeadTextures,
                        QualificationProfile =
                            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete
                    },
                    CancellationToken.None);
            Assert(!historicalAnalysis.Qualified &&
                   historicalAnalysis.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-facetint-slot") &&
                   !File.Exists(historicalOutput),
                "The corrected Manager profile did not reject the historical Sophia slot-0 FaceTint carrier.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertPrivateHeadTextureRoute(string sourcePath)
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var testRoot = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work",
            $"qualified-carrier-private-textures-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(testRoot, "Data", "meshes", "actors", "character",
            "FaceGenData", "FaceGeom", "NpcManagerPrivate.esp");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "00000800.nif");
        var faceTint = new AssetPath(
            "textures/actors/character/FaceGenData/FaceTint/NpcManagerPrivate.esp/00000800.dds");
        var privateTextures = new SkyrimPrivateHeadTexturePaths(
            new AssetPath("Actors/Character/NpcManagerPrivate/femalehead.dds"),
            new AssetPath("Actors/Character/NpcManagerPrivate/femalehead_msn.dds"),
            new AssetPath("Actors/Character/NpcManagerPrivate/femalehead_sk.dds"),
            new AssetPath("Actors/Character/NpcManagerPrivate/femalehead_height.dds"),
            new AssetPath("Actors/Character/NpcManagerPrivate/femalehead_s.dds"),
            EnvironmentMaskOrSubsurfaceTint:
                new AssetPath("Actors/Character/NpcManagerPrivate/femalehead_envmask.dds"),
            Environment: new AssetPath("Actors/Character/NpcManagerPrivate/femalehead_env.dds"),
            Multilayer: new AssetPath("Actors/Character/NpcManagerPrivate/femalehead_multilayer.dds"));

        try
        {
            var service = new QualifiedFaceGeomCarrierService(
                new KOnlyWorkspacePolicy(labRoot, new WorkspacePath(@"F:\ExampleGame")),
                labRoot);
            var request = new QualifiedFaceGeomCarrierAnalyzeRequest(
                new WorkspacePath(sourcePath),
                new Sha256Hash("4658408FF08F77F2C64EA8AD1837B6A449D9BD4B3958BDF21F4EECDB516EC2F9"),
                new WorkspacePath(outputPath),
                faceTint)
            {
                TargetHeadTextures = privateTextures
            };

            var analysis = await service.AnalyzeAsync(request, CancellationToken.None);
            Assert(analysis.Qualified && analysis.Proposal is not null && !File.Exists(outputPath),
                "Private-head carrier analysis failed or wrote output: " +
                string.Join(", ", analysis.Diagnostics.Select(item => item.Code)));
            var proposal = analysis.Proposal ?? throw new InvalidOperationException(
                "Qualified private-head analysis did not return a proposal.");
            Assert(proposal.TargetHeadTextures == privateTextures &&
                   proposal.TargetHeadTexturesBindingSha256 is not null,
                "The proposal did not bind the exact private-head texture authority.");

            var omittedRoute = proposal with { TargetHeadTextures = null };
            var omittedResult = await service.ApplyAsync(omittedRoute, CancellationToken.None);
            Assert(!omittedResult.Written && !File.Exists(outputPath) &&
                   omittedResult.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-proposal-mismatch"),
                "Apply accepted a proposal with its private-head route omitted.");

            var tamperedRoute = proposal with
            {
                TargetHeadTextures = privateTextures with
                {
                    Height = new AssetPath("Actors/Character/NpcManagerPrivate/tampered-height.dds")
                }
            };
            var tamperedResult = await service.ApplyAsync(tamperedRoute, CancellationToken.None);
            Assert(!tamperedResult.Written && !File.Exists(outputPath) &&
                   tamperedResult.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-proposal-mismatch"),
                "Apply accepted a proposal with a tampered private-head route.");

            var result = await service.ApplyAsync(proposal, CancellationToken.None);
            Assert(result.Written && result.Verified &&
                   result.Verification is { Verified: true } verification &&
                   verification.ChangedBlocks.SequenceEqual([proposal.TextureSetBlockIndex]),
                "Private-head carrier materialization did not pass post-write verification: " +
                string.Join(", ", result.Diagnostics.Select(item => item.Code)));

            var source = SseFaceGeomCarrierCodec.Parse(File.ReadAllBytes(sourcePath));
            var output = SseFaceGeomCarrierCodec.Parse(File.ReadAllBytes(outputPath));
            var sourceSlots = source.Blocks[proposal.TextureSetBlockIndex].Textures;
            var outputSlots = output.Blocks[proposal.TextureSetBlockIndex].Textures;
            Assert(sourceSlots.Length >= 8 && outputSlots.Length == sourceSlots.Length,
                "The head texture set does not retain its admitted slot count.");
            for (var slot = 0; slot < sourceSlots.Length; slot++)
            {
                var expected = slot switch
                {
                    0 => privateTextures.Diffuse.Value.Replace('/', '\\'),
                    1 => privateTextures.NormalOrGloss.Value.Replace('/', '\\'),
                    2 => privateTextures.GlowOrDetailMap.Value.Replace('/', '\\'),
                    6 => faceTint.Value.Replace('/', '\\'),
                    7 => privateTextures.BacklightMaskOrSpecular.Value.Replace('/', '\\'),
                    _ => sourceSlots[slot]
                };
                Assert(string.Equals(outputSlots[slot], expected, StringComparison.Ordinal),
                    $"Private-head texture slot {slot} did not reopen with its exact authorized value.");
            }
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task TestQualifiedCarrierDurableEvidence()
    {
        const string admittedFixture =
            @"K:\ExampleWorkspace\projects\Emi2FreshBuild\03-builds\feasibility-probes\ck-carrier-root\Data\meshes\Actors\Character\FaceGenData\FaceGeom\EmiCarrierProbe.esp\00000800.NIF";
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var testRoot = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work",
            $"qualified-carrier-durable-{Guid.NewGuid():N}");
        var stagingRoot = Path.Combine(testRoot, ".racemenu-stage-test");
        var sourcePath = Path.Combine(stagingRoot, "product-facegeom.nif");
        var originalPackageRoot = Path.Combine(testRoot, "package-original");
        var relocatedPackageRoot = Path.Combine(testRoot, "package-relocated");
        var outputRelative = new AssetPath(
            "Data/meshes/actors/character/FaceGenData/FaceGeom/NpcManagerDurable.esp/00000800.nif");
        var outputPath = Path.Combine(
            originalPackageRoot,
            outputRelative.Value.Replace('/', Path.DirectorySeparatorChar));
        var faceTint = new AssetPath(
            "textures/actors/character/FaceGenData/FaceTint/NpcManagerDurable.esp/00000800.dds");
        var privateTextures = new SkyrimPrivateHeadTexturePaths(
            new AssetPath("Actors/Character/NpcManagerDurable/femalehead.dds"),
            new AssetPath("Actors/Character/NpcManagerDurable/femalehead_msn.dds"),
            new AssetPath("Actors/Character/NpcManagerDurable/femalehead_sk.dds"),
            new AssetPath("Actors/Character/NpcManagerDurable/femalehead_height.dds"),
            new AssetPath("Actors/Character/NpcManagerDurable/femalehead_s.dds"),
            EnvironmentMaskOrSubsurfaceTint:
                new AssetPath("Actors/Character/NpcManagerDurable/femalehead_envmask.dds"),
            Environment: new AssetPath("Actors/Character/NpcManagerDurable/femalehead_env.dds"),
            Multilayer: new AssetPath("Actors/Character/NpcManagerDurable/femalehead_multilayer.dds"));

        try
        {
            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.Copy(admittedFixture, sourcePath, overwrite: false);
            var sourceHash = HashBytes(File.ReadAllBytes(sourcePath));
            var service = new QualifiedFaceGeomCarrierService(
                new KOnlyWorkspacePolicy(labRoot, new WorkspacePath(@"F:\ExampleGame")),
                labRoot);
            var analysis = await service.AnalyzeAsync(
                new QualifiedFaceGeomCarrierAnalyzeRequest(
                    new WorkspacePath(sourcePath),
                    sourceHash,
                    new WorkspacePath(outputPath),
                    faceTint)
                {
                    TargetHeadTextures = privateTextures
                },
                CancellationToken.None);
            Assert(analysis is { Qualified: true, Proposal: not null },
                "Durable-evidence analysis failed: " + Diagnostics(analysis.Diagnostics));

            var applied = await service.ApplyAsync(analysis.Proposal!, CancellationToken.None);
            Assert(applied is { Written: true, Verified: true, Artifact: not null },
                "Durable-evidence materialization failed: " + Diagnostics(applied.Diagnostics));
            var artifact = applied.Artifact!;
            var evidence = new QualifiedFaceGeomCarrierMaterializationEvidence(
                outputRelative,
                artifact);
            var serialized = JsonSerializer.Serialize(evidence, DurableEvidenceJsonOptions);
            Assert(!serialized.Contains(testRoot, StringComparison.OrdinalIgnoreCase) &&
                   !serialized.Contains(".racemenu-stage-", StringComparison.OrdinalIgnoreCase) &&
                   serialized.Contains(outputRelative.Value, StringComparison.Ordinal),
                "Durable evidence leaked an absolute or staging path, or lost its relative output path.");
            var evidenceDirectory = Path.Combine(originalPackageRoot, "evidence");
            Directory.CreateDirectory(evidenceDirectory);
            var evidencePath = Path.Combine(
                evidenceDirectory, "facegeom-carrier-materialization.json");
            File.WriteAllText(evidencePath, serialized);

            File.Delete(sourcePath);
            Directory.Delete(stagingRoot);
            Directory.Move(originalPackageRoot, relocatedPackageRoot);
            var relocatedRoot = new WorkspacePath(relocatedPackageRoot);
            var relocatedOutput = Path.Combine(
                relocatedPackageRoot,
                outputRelative.Value.Replace('/', Path.DirectorySeparatorChar));
            var verified = await service.VerifyEvidenceFileAsync(
                relocatedRoot,
                new AssetPath("evidence/facegeom-carrier-materialization.json"),
                CancellationToken.None);
            Assert(verified.Verified &&
                   verified.OutputSha256 == artifact.OutputSha256 &&
                   verified.ReconstructedSourceSha256 == artifact.SourceSha256 &&
                   verified.ChangedBlocks.SequenceEqual(artifact.ChangedBlocks),
                "Durable evidence did not survive source deletion and package relocation: " +
                Diagnostics(verified.Diagnostics));

            async Task<QualifiedFaceGeomCarrierEvidenceVerificationResult> VerifyMutationAsync(
                Action<JsonObject> mutate)
            {
                var document = JsonNode.Parse(serialized)?.AsObject() ??
                    throw new InvalidOperationException("The durable evidence fixture is not an object.");
                mutate(document);
                await File.WriteAllTextAsync(
                    Path.Combine(
                        relocatedPackageRoot,
                        "evidence",
                        "facegeom-carrier-materialization.json"),
                    document.ToJsonString(DurableEvidenceJsonOptions));
                return await service.VerifyEvidenceFileAsync(
                    relocatedRoot,
                    new AssetPath("evidence/facegeom-carrier-materialization.json"),
                    CancellationToken.None);
            }

            var missingChangedBlocks = await VerifyMutationAsync(document =>
                document["materialization"]!.AsObject().Remove("changedBlocks"));
            Assert(!missingChangedBlocks.Verified &&
                   missingChangedBlocks.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-evidence-file-invalid"),
                "Durable evidence did not fail closed when changedBlocks was omitted: " +
                Diagnostics(missingChangedBlocks.Diagnostics));

            var nullChangedBlocks = await VerifyMutationAsync(document =>
                document["materialization"]!["changedBlocks"] = null);
            Assert(!nullChangedBlocks.Verified &&
                   nullChangedBlocks.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-evidence-file-invalid"),
                "Durable evidence did not fail closed on a null changedBlocks array: " +
                Diagnostics(nullChangedBlocks.Diagnostics));

            var defaultChangedBlocks = await service.VerifyEvidenceAsync(
                relocatedRoot,
                evidence with
                {
                    Materialization = artifact with { ChangedBlocks = default }
                },
                CancellationToken.None);
            Assert(!defaultChangedBlocks.Verified &&
                   defaultChangedBlocks.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-evidence-change-surface"),
                "Typed durable evidence did not fail closed on a default changedBlocks array: " +
                Diagnostics(defaultChangedBlocks.Diagnostics));

            var nullSourceStructure = await VerifyMutationAsync(document =>
                document["materialization"]!["sourceStructure"] = null);
            Assert(!nullSourceStructure.Verified &&
                   nullSourceStructure.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-evidence-source-structure-missing"),
                "Durable evidence did not fail closed on a null source structure: " +
                Diagnostics(nullSourceStructure.Diagnostics));

            var unknownMember = await VerifyMutationAsync(document =>
                document["unsupportedMember"] = true);
            Assert(!unknownMember.Verified && unknownMember.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-evidence-file-invalid"),
                "Durable evidence accepted an unknown JSON member: " +
                Diagnostics(unknownMember.Diagnostics));

            var duplicateOutputNif = serialized.Insert(
                serialized.IndexOf('{') + 1,
                $"\"outputNif\":{{\"value\":\"{outputRelative.Value}\"}},");
            await File.WriteAllTextAsync(
                Path.Combine(relocatedPackageRoot, "evidence",
                    "facegeom-carrier-materialization.json"),
                duplicateOutputNif);
            var duplicateKnownMember = await service.VerifyEvidenceFileAsync(
                relocatedRoot,
                new AssetPath("evidence/facegeom-carrier-materialization.json"),
                CancellationToken.None);
            Assert(!duplicateKnownMember.Verified &&
                   duplicateKnownMember.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-evidence-file-invalid"),
                "Durable evidence accepted a duplicate known JSON member: " +
                Diagnostics(duplicateKnownMember.Diagnostics));

            File.WriteAllText(
                Path.Combine(
                    relocatedPackageRoot,
                    "evidence",
                    "facegeom-carrier-materialization.json"),
                serialized);

            var zeroHash = Hash(new string('0', 64));
            var preimageBytes = Convert.FromBase64String(
                artifact.SourceTextureSetPreimage.BytesBase64);
            preimageBytes[^1] ^= 0x01;
            var tamperedPreimageBase64 = Convert.ToBase64String(preimageBytes);
            var mutations = new (string Name,
                QualifiedFaceGeomCarrierMaterializationEvidence Evidence,
                string ExpectedCode)[]
            {
                ("schema",
                    evidence with
                    {
                        Materialization = artifact with { SchemaVersion = "2" }
                    },
                    "qualified-carrier-evidence-schema"),
                ("authority",
                    evidence with
                    {
                        Materialization = artifact with { RuntimeAuthority = true }
                    },
                    "qualified-carrier-evidence-authority-overclaim"),
                ("target binding",
                    evidence with
                    {
                        Materialization = artifact with { TargetTextureBindingSha256 = zeroHash }
                    },
                    "qualified-carrier-evidence-target-binding"),
                ("changed blocks",
                    evidence with
                    {
                        Materialization = artifact with { ChangedBlocks = [0] }
                    },
                    "qualified-carrier-evidence-change-surface"),
                ("preimage bytes",
                    evidence with
                    {
                        Materialization = artifact with
                        {
                            SourceTextureSetPreimage = artifact.SourceTextureSetPreimage with
                            {
                                BytesBase64 = tamperedPreimageBase64
                            }
                        }
                    },
                    "qualified-carrier-evidence-preimage-hash"),
                ("preimage length",
                    evidence with
                    {
                        Materialization = artifact with
                        {
                            SourceTextureSetPreimage = artifact.SourceTextureSetPreimage with
                            {
                                ByteLength = artifact.SourceTextureSetPreimage.ByteLength + 1
                            }
                        }
                    },
                    "qualified-carrier-evidence-preimage-length"),
                ("source hash",
                    evidence with
                    {
                        Materialization = artifact with { SourceSha256 = zeroHash }
                    },
                    "qualified-carrier-evidence-source-hash-mismatch"),
                ("source structure",
                    evidence with
                    {
                        Materialization = artifact with
                        {
                            SourceStructure = artifact.SourceStructure with { GraphSha256 = zeroHash }
                        }
                    },
                    "qualified-carrier-evidence-source-structure-drift")
            };
            foreach (var mutation in mutations)
            {
                var refused = await service.VerifyEvidenceAsync(
                    relocatedRoot, mutation.Evidence, CancellationToken.None);
                Assert(!refused.Verified && refused.Diagnostics.Any(item =>
                           item.Code == mutation.ExpectedCode),
                    $"Durable evidence accepted {mutation.Name} tampering: " +
                    Diagnostics(refused.Diagnostics));
            }

            var originalOutputBytes = File.ReadAllBytes(relocatedOutput);
            var nonTargetTamper = (byte[])originalOutputBytes.Clone();
            var outputDocument = SseFaceGeomCarrierCodec.Parse(nonTargetTamper);
            var nonTargetBlock = outputDocument.Blocks.First(block =>
                block.Index != artifact.SourceTextureSetPreimage.BlockIndex &&
                block.GeometryPayload is { Length: > 0 });
            var nonTargetPayload = nonTargetBlock.GeometryPayload!;
            nonTargetTamper[nonTargetPayload.Offset + nonTargetPayload.Length - 1] ^= 0x01;
            File.WriteAllBytes(relocatedOutput, nonTargetTamper);
            var nonTargetEvidence = evidence with
            {
                Materialization = artifact with
                {
                    OutputSha256 = HashBytes(nonTargetTamper),
                    OutputByteLength = nonTargetTamper.LongLength
                }
            };
            var nonTargetRefusal = await service.VerifyEvidenceAsync(
                relocatedRoot, nonTargetEvidence, CancellationToken.None);
            Assert(!nonTargetRefusal.Verified && nonTargetRefusal.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-evidence-source-hash-mismatch"),
                "Durable evidence accepted non-target output drift after its output hash was rebound: " +
                Diagnostics(nonTargetRefusal.Diagnostics));

            File.WriteAllBytes(relocatedOutput, originalOutputBytes);
            outputDocument = SseFaceGeomCarrierCodec.Parse(originalOutputBytes);
            var targetDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            var outputTarget = SseFaceGeomCarrierCodec.FindFaceTintTarget(
                outputDocument, targetDiagnostics) ??
                throw new InvalidOperationException("The relocated output lost its FaceTint target.");
            var targetTamper = SseFaceGeomCarrierCodec.RewriteFaceTintPath(
                outputDocument,
                outputTarget,
                new AssetPath(
                    "textures/actors/character/FaceGenData/FaceTint/TamperedDurable.esp/00000800.dds"));
            File.WriteAllBytes(relocatedOutput, targetTamper);
            var targetEvidence = evidence with
            {
                Materialization = artifact with
                {
                    OutputSha256 = HashBytes(targetTamper),
                    OutputByteLength = targetTamper.LongLength
                }
            };
            var targetRefusal = await service.VerifyEvidenceAsync(
                relocatedRoot, targetEvidence, CancellationToken.None);
            Assert(!targetRefusal.Verified && targetRefusal.Diagnostics.Any(item =>
                       item.Code is "qualified-carrier-texture-slot-drift" or
                           "qualified-carrier-facetint-postwrite-path"),
                "Durable evidence accepted target texture drift after its output hash was rebound: " +
                Diagnostics(targetRefusal.Diagnostics));
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static Task TestQualifiedCarrierRewriteBlockCountDrift()
    {
        var source = CreateAdmittedCarrierDocument();
        var shorter = source with { Blocks = source.Blocks.Take(source.Blocks.Length - 1).ToImmutableArray() };
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var changed = SseFaceGeomCarrierCodec.VerifyRewrite(
            source,
            shorter,
            new NifTextureTarget(43, 6,
                "textures\\actors\\character\\FaceGenData\\FaceTint\\EmiCarrierProbe.esp\\00000800.dds"),
            new AssetPath("textures/actors/character/FaceGenData/FaceTint/NpcManagerTest.esp/00000800.dds"),
            diagnostics);
        Assert(changed.IsEmpty && diagnostics.Any(diagnostic =>
                diagnostic.Code == "qualified-carrier-rewrite-target-unavailable"),
            "Rewrite verification did not fail closed on a shorter output block table.");
        return Task.CompletedTask;
    }

    private static SseNifDocument CreateAdmittedCarrierDocument()
    {
        var blocks = ImmutableArray.CreateBuilder<SseNifBlock>(51);
        blocks.Add(Block(0, "BSFadeNode", null,
            Ref("controller", -1), Ref("collision", -1),
            Ref("child", 1), Ref("child", 2), Ref("child", 3)));
        blocks.Add(Block(1, "NiNode", "NPC Head [Head]",
            Ref("controller", -1), Ref("collision", -1)));
        blocks.Add(Block(2, "NiNode", "NPC Spine2 [Spn2]",
            Ref("controller", -1), Ref("collision", -1)));
        blocks.Add(Block(3, "NiNode", "BSFaceGenNiNodeSkinned",
            Ref("controller", -1), Ref("collision", -1),
            Ref("child", 4), Ref("child", 11), Ref("child", 18), Ref("child", 24),
            Ref("child", 31), Ref("child", 38), Ref("child", 44)));

        AddShapeClosure(blocks, 4, "0_HAIRLINE_Female_Human_Straight",
            "BSDismemberSkinInstance", 5, 6, 7, 8, 9, 10, [1, 2]);
        AddShapeClosure(blocks, 11, "0LassiHL",
            "BSDismemberSkinInstance", 12, 13, 14, 15, 16, 17, [1, 2]);
        AddShapeClosure(blocks, 18, "0Lassi",
            "BSDismemberSkinInstance", 19, 20, 21, 22, 16, 23, [1, 2], addTextureSet: false);
        AddShapeClosure(blocks, 24, "KoralinaEyebrowsF02",
            "NiSkinInstance", 25, 26, 27, 28, 29, 30, [1]);
        AddShapeClosure(blocks, 31, "MJBFemaleEyesHumanGreen04",
            "NiSkinInstance", 32, 33, 34, 35, 36, 37, [1]);
        AddShapeClosure(blocks, 38, "00KLH_FemaleHeadNord",
            "BSDismemberSkinInstance", 39, 40, 41, 42, 43, -1, [2, 1]);
        AddShapeClosure(blocks, 44, "FemaleMouthHumanoidDefault",
            "NiSkinInstance", 45, 46, 47, 48, 49, 50, [1]);

        Assert(blocks.Count == 51, "The synthetic admitted carrier has the wrong block count.");
        return new SseNifDocument(
            [0],
            12,
            100,
            0,
            0,
            0,
            blocks.Select(block => block.Type).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            [],
            [0],
            blocks.ToImmutable());
    }

    private static void AddShapeClosure(
        ImmutableArray<SseNifBlock>.Builder blocks,
        int shapeIndex,
        string shapeName,
        string skinType,
        int skinIndex,
        int dataIndex,
        int partitionIndex,
        int shaderIndex,
        int textureIndex,
        int alphaIndex,
        ImmutableArray<int> bones,
        bool addTextureSet = true)
    {
        Assert(blocks.Count == shapeIndex, $"Expected shape block {shapeIndex}.");
        blocks.Add(Block(shapeIndex, "BSDynamicTriShape", shapeName,
            Ref("controller", -1), Ref("collision", -1), Ref("skin", skinIndex),
            Ref("shader", shaderIndex), Ref("alpha", alphaIndex)));
        blocks.Add(Block(skinIndex, skinType, null,
            [Ref("skindata", dataIndex), Ref("skinpartition", partitionIndex), Ref("skeletonroot", 3),
                .. bones.Select(bone => Ref("bone", bone))]));
        blocks.Add(Block(dataIndex, "NiSkinData"));
        blocks.Add(Block(partitionIndex, "NiSkinPartition"));
        blocks.Add(Block(shaderIndex, "BSLightingShaderProperty", null,
            Ref("controller", -1), Ref("textureset", textureIndex)));
        if (addTextureSet) blocks.Add(Block(textureIndex, "BSShaderTextureSet"));
        if (alphaIndex >= 0)
            blocks.Add(Block(alphaIndex, "NiAlphaProperty", null, Ref("controller", -1)));
    }

    private static SseNifReference Ref(string kind, int target) => new(kind, target);

    private static SseNifBlock Block(
        int index,
        string type,
        string? name = null,
        params SseNifReference[] references) =>
        new(index, type, name, 0, 0, references.ToImmutableArray(), [], null, null);
}
