using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSseFaceGeomCarrierAssembler()
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
        var faceTint = new AssetPath(
            "Textures/Actors/Character/FaceGenData/FaceTint/BatchCarrier.esp/00000800.dds");
        var request = new SseFaceGeomCarrierAssemblyRequest(parts, faceTint);
        var assembler = new SseFaceGeomCarrierAssembler();

        SseFaceGeomCarrierAssemblyResult result = assembler.Assemble(request);
        Assert(result.Assembled && result.Verified && result.Artifact is not null,
            $"Real headpart carrier assembly failed: {GeometryDiagnostics(result.Diagnostics)}");
        SseFaceGeomCarrierAssemblyArtifact artifact = result.Artifact ??
            throw new InvalidOperationException("Verified carrier omitted its artifact.");
        Assert(!artifact.RuntimeAuthority && artifact.Shapes.Length == 7 &&
               artifact.BlockCount == 52 && artifact.ByteLength == artifact.Bytes.Length,
            "Carrier artifact lost its static-only seven-shape output envelope.");

        SseNifDocument output = SseFaceGeomCarrierCodec.Parse(
            artifact.Bytes.ToArray());
        ImmutableArray<Vector3> legacyBoneTranslations =
            ReadCarrierBoneTranslations(output);
        Assert(legacyBoneTranslations.Any(translation =>
                translation != Vector3.Zero),
            "The legacy carrier fixture no longer exercises source-model bone translations.");
        SseFaceGeomCarrierAssemblyResult explicitLegacy = assembler.Assemble(
            request with
            {
                SkeletonAuthority =
                    SseFaceGeomCarrierSkeletonAuthority.SourceModelWorldTranslations
            });
        Assert(explicitLegacy.Assembled &&
               explicitLegacy.Artifact is not null &&
               explicitLegacy.Artifact.Sha256 == artifact.Sha256 &&
               explicitLegacy.Artifact.Bytes.AsSpan()
                   .SequenceEqual(artifact.Bytes.AsSpan()),
            "Explicit legacy skeleton authority changed the default carrier bytes.");

        SseFaceGeomCarrierAssemblyResult identitySkeleton = assembler.Assemble(
            request with
            {
                SkeletonAuthority =
                    SseFaceGeomCarrierSkeletonAuthority.IdentityFaceGenBones
            });
        Assert(identitySkeleton.Assembled &&
               identitySkeleton.Verified &&
               identitySkeleton.Artifact is not null,
            $"Identity-skeleton carrier assembly failed: {GeometryDiagnostics(identitySkeleton.Diagnostics)}");
        SseNifDocument identityOutput = SseFaceGeomCarrierCodec.Parse(
            identitySkeleton.Artifact!.Bytes.ToArray());
        Assert(ReadCarrierBoneTranslations(identityOutput)
                   .All(translation => translation == Vector3.Zero) &&
               identitySkeleton.Diagnostics.Any(item =>
                   item.Code ==
                   "sse-facegeom-carrier-identity-skeleton-authority"),
            "Identity FaceGen skeleton authority did not write and report zero-valued carrier bone-node translations.");
        const string nonLexicalFaceOwnerName = "NativeTintFemaleFace";
        SseFaceGeomCarrierAssemblyResult nonLexicalFaceOwner =
            assembler.Assemble(request with
            {
                Parts = parts.SetItem(
                    0,
                    parts[0] with
                    {
                        OutputShapeName = nonLexicalFaceOwnerName
                    })
            });
        Assert(nonLexicalFaceOwner.Assembled &&
               nonLexicalFaceOwner.Verified &&
               nonLexicalFaceOwner.Artifact is not null &&
               nonLexicalFaceOwner.Artifact.Shapes.Single(shape =>
                   shape.UsesFaceTint).OutputShapeName ==
                   nonLexicalFaceOwnerName,
            $"A typed Manager FaceTint owner was rejected because its valid output name does not contain 'Head': {GeometryDiagnostics(nonLexicalFaceOwner.Diagnostics)}");
        SseNifBlock outputHeadShape = output.Blocks.Single(block =>
            block.Type == "BSDynamicTriShape" &&
            string.Equals(block.Name, parts[0].OutputShapeName,
                StringComparison.Ordinal));
        int outputHeadShaderIndex = outputHeadShape.References.Single(reference =>
            reference.Kind == "shader").Target;
        int outputHeadTextureSetIndex = output.Blocks[outputHeadShaderIndex]
            .References.Single(reference => reference.Kind == "textureset").Target;
        ImmutableArray<string> outputHeadTextures =
            output.Blocks[outputHeadTextureSetIndex].Textures;
        SseNifDocument sourceHead = SseFaceGeomCarrierCodec.Parse(
            parts[0].SourceBytes.ToArray());
        SseNifBlock sourceHeadShape = sourceHead.Blocks.Single(block =>
            block.Type == "BSDynamicTriShape");
        int sourceHeadShaderIndex = sourceHeadShape.References.Single(reference =>
            reference.Kind == "shader").Target;
        int sourceHeadTextureSetIndex = sourceHead.Blocks[sourceHeadShaderIndex]
            .References.Single(reference => reference.Kind == "textureset").Target;
        ImmutableArray<string> sourceHeadTextures =
            sourceHead.Blocks[sourceHeadTextureSetIndex].Textures;
        var faceTintShapedDiffuse = new SkyrimPrivateHeadTexturePaths(
            new AssetPath(
                "Actors/Character/FaceGenData/FaceTint/BrokenHead.dds"),
            new AssetPath("!COR/Head/femalehead_msn_009.dds"),
            new AssetPath("!COR/Head/femalehead_sk.dds"),
            new AssetPath("Actors/Character/Male/BlankDetailmap.dds"),
            new AssetPath("!COR/Head/femalehead_s.dds"));
        Assert(!SseFaceGeomCarrierCodec.IsValidHeadDiffusePath(string.Empty) &&
               !SseFaceGeomCarrierCodec.IsValidHeadDiffusePath(
                   faceTintShapedDiffuse.Diffuse.Value) &&
               SseFaceGeomCarrierCodec.IsValidHeadDiffusePath(
                   @"!COR\Head\FemaleHead.dds"),
            "Manager head-diffuse validation must reject empty and FaceTint-shaped slot-0 routes while preserving valid NIF-relative DDS paths.");
        string[] unsafeDiffuseAliases =
        [
            @"Actors\Character\FaceGenData\X\..\FaceTint\Bad.esp\00000800.dds",
            @"!COR\\Head\FemaleHead.dds",
            @".\!COR\Head\FemaleHead.dds",
            @"\!COR\Head\FemaleHead.dds",
            @"\\server\share\FemaleHead.dds",
            @"C:\COR\Head\FemaleHead.dds",
            @"!COR\Head\FemaleHead.dds:stream.dds"
        ];
        string? acceptedUnsafeDiffuse = unsafeDiffuseAliases.FirstOrDefault(
            SseFaceGeomCarrierCodec.IsValidHeadDiffusePath);
        Assert(acceptedUnsafeDiffuse is null,
            $"Manager head-diffuse validation accepted unsafe rooted, aliased, doubled-segment, drive, or ADS route '{acceptedUnsafeDiffuse}'.");
        byte[] malformedHeadSourceBytes =
            SseFaceGeomCarrierCodec.RewriteHeadTexturePaths(
                sourceHead,
                new NifTextureTarget(
                    sourceHeadTextureSetIndex,
                    6,
                    sourceHeadTextures[6]),
                faceTint,
                faceTintShapedDiffuse,
                QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete);
        var malformedHeadSourceHash = new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(malformedHeadSourceBytes)));
        var malformedParts = parts.ToBuilder();
        malformedParts[0] = parts[0] with
        {
            ExpectedSourceSha256 = malformedHeadSourceHash,
            SourceBytes = ImmutableArray.CreateRange(malformedHeadSourceBytes)
        };
        SseFaceGeomCarrierAssemblyResult malformedDiffuseAssembly =
            assembler.Assemble(request with
            {
                Parts = malformedParts.MoveToImmutable()
            });
        Assert(!malformedDiffuseAssembly.Assembled &&
               malformedDiffuseAssembly.Artifact is null &&
               malformedDiffuseAssembly.Diagnostics.Any(item =>
                   item.Code == "sse-facegeom-carrier-head-diffuse"),
            "Manager carrier assembly accepted a FaceTint-shaped slot-0 path as the head diffuse while slot 6 remained canonical.");

        var validTargetDiagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        NifTextureTarget validTarget =
            SseFaceGeomCarrierCodec.FindFaceTintTarget(
                output,
                QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
                validTargetDiagnostics) ??
            throw new InvalidOperationException(
                "The valid Manager carrier lost its canonical slot-6 FaceTint target.");
        Assert(validTargetDiagnostics.All(item =>
                item.Severity != DiagnosticSeverity.Error),
            "The valid Manager carrier unexpectedly failed texture-route validation.");
        byte[] malformedCarrierBytes =
            SseFaceGeomCarrierCodec.RewriteHeadTexturePaths(
                output,
                validTarget,
                faceTint,
                faceTintShapedDiffuse,
                QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete);
        string faceTintWirePath = faceTint.Value.Replace('/', '\\');
        int faceTintOccurrences = output.Blocks
            .Where(block => block.Type == "BSShaderTextureSet")
            .SelectMany(block => block.Textures)
            .Count(texture => string.Equals(texture, faceTintWirePath,
                StringComparison.Ordinal));
        Assert(outputHeadTextures.Length >= 7 &&
               sourceHeadTextures.Length == outputHeadTextures.Length &&
               string.Equals(outputHeadTextures[0], sourceHeadTextures[0],
                   StringComparison.Ordinal) &&
               string.Equals(outputHeadTextures[6], faceTintWirePath,
                   StringComparison.Ordinal) &&
               faceTintOccurrences == 1,
            "Manager FaceGeom assembly must preserve the source head diffuse in slot 0 and bind the actor FaceTint exactly once in slot 6.");
        SseNifDocument creationKit = SseFaceGeomCarrierCodec.Parse(
            File.ReadAllBytes(CarrierPath));
        Dictionary<string, int> outputCensus = Census(output);
        Dictionary<string, int> creationKitCensus = Census(creationKit);
        foreach ((string type, int count) in creationKitCensus)
        {
            int expected = type == "BSShaderTextureSet" ? count + 1 : count;
            Assert(outputCensus.GetValueOrDefault(type) == expected,
                $"Assembled carrier census for {type} differs from the CK carrier closure.");
        }
        Assert(outputCensus.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(creationKitCensus.Keys),
            "Assembled carrier introduced a block family absent from the CK carrier.");
        Assert(artifact.Shapes.All(shape =>
                ExpectedTopology.TryGetValue(shape.OutputShapeName,
                    out Sha256Hash expected) && expected == shape.TopologySha256),
            "Assembled carrier changed selected-headpart topology.");

        ImmutableArray<Vector3> sourcePositions = ReadPackedPositions(
            SseFaceGeomCarrierCodec.Parse(parts[0].SourceBytes.ToArray()),
            requiredName: null);
        var changedPositions = sourcePositions.ToBuilder();
        changedPositions[0] += new Vector3(0.01f, 0f, 0f);
        var bakedParts = parts.ToBuilder();
        bakedParts[0] = parts[0] with
        {
            BakedPositions = changedPositions.MoveToImmutable()
        };
        var bakedRequest = request with { Parts = bakedParts.MoveToImmutable() };
        SseFaceGeomCarrierAssemblyResult baked = assembler.Assemble(bakedRequest);
        Assert(baked.Assembled && baked.Verified && baked.Artifact is not null,
            $"Carrier refused real baked positions: {GeometryDiagnostics(baked.Diagnostics)}");
        SseFaceGeomCarrierAssemblyArtifact bakedArtifact = baked.Artifact ??
            throw new InvalidOperationException(
                "Verified baked carrier omitted its artifact.");
        SseFaceGeomCarrierAssemblyShape changedShape = bakedArtifact.Shapes
            .Single(shape => shape.HeadPart == parts[0].HeadPart);
        Assert(changedShape.PositionsChanged &&
               changedShape.SourcePositionSha256 != changedShape.OutputPositionSha256 &&
               bakedArtifact.Shapes.Count(shape => shape.PositionsChanged) == 1 &&
               bakedArtifact.Sha256 != artifact.Sha256,
            "Carrier did not retain a bounded, evidenced vertex-position mutation.");
        SseNifDocument bakedDocument = SseFaceGeomCarrierCodec.Parse(
            bakedArtifact.Bytes.ToArray());
        ImmutableArray<Vector3> reopenedPositions = ReadPackedPositions(
            bakedDocument, parts[0].OutputShapeName);
        Assert(reopenedPositions.SequenceEqual(bakedRequest.Parts[0].BakedPositions),
            "Reparsed carrier positions differ from the requested baked geometry.");
        Assert(bakedArtifact.Shapes.All(shape =>
                ExpectedTopology.TryGetValue(shape.OutputShapeName,
                    out Sha256Hash expected) && expected == shape.TopologySha256),
            "Baked position mutation changed selected-headpart topology.");

        SseFaceGeomCarrierAssemblyResult repeat = assembler.Assemble(request);
        Assert(repeat.Assembled && repeat.Artifact is not null &&
               repeat.Artifact.Sha256 == artifact.Sha256 &&
               repeat.Artifact.Bytes.AsSpan().SequenceEqual(artifact.Bytes.AsSpan()),
            "Carrier assembly is not byte-deterministic for identical admitted inputs.");

        var noFaceTint = request with
        {
            Parts = parts.Select(part => part with { UsesFaceTint = false })
                .ToImmutableArray()
        };
        SseFaceGeomCarrierAssemblyResult refused = assembler.Assemble(noFaceTint);
        Assert(!refused.Assembled && refused.Artifact is null &&
               refused.Diagnostics.Any(item =>
                   item.Code == "sse-facegeom-carrier-facetint-owner"),
            "Carrier assembly accepted a graph with no FaceTint-owning headpart.");

        var nonCanonicalFaceTint = request with
        {
            FaceTintPath = new AssetPath(
                "Textures/Actors/Character/Female/FemaleHead.dds")
        };
        refused = assembler.Assemble(nonCanonicalFaceTint);
        Assert(!refused.Assembled && refused.Artifact is null &&
               refused.Diagnostics.Any(item =>
                   item.Code == "sse-facegeom-carrier-facetint-path"),
            "Carrier assembly accepted a non-FaceGen DDS as its actor FaceTint route.");

        string outputPath = Path.Combine(AppContext.BaseDirectory,
            "carrier-materialization-" + Guid.NewGuid().ToString("N") + ".nif");
        string badOutputPath = Path.Combine(AppContext.BaseDirectory,
            "carrier-materialization-slot0-" + Guid.NewGuid().ToString("N") +
            ".nif");
        string badDiffuseOutputPath = Path.Combine(AppContext.BaseDirectory,
            "carrier-materialization-bad-diffuse-" +
            Guid.NewGuid().ToString("N") + ".nif");
        try
        {
            var workspaceRoot = new WorkspacePath("K:\\ExampleWorkspace");
            var materializer = new SseFaceGeomCarrierMaterializationService(
                assembler,
                new KOnlyWorkspacePolicy(workspaceRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                workspaceRoot);
            var analyzeRequest =
                new SseFaceGeomCarrierMaterializationAnalyzeRequest(request,
                    new WorkspacePath(outputPath));
            SseFaceGeomCarrierMaterializationAnalysisResult analysis =
                await materializer.AnalyzeAsync(analyzeRequest,
                    CancellationToken.None);
            Assert(analysis.Accepted && analysis.Proposal is not null &&
                   !File.Exists(outputPath),
                "Carrier analysis wrote output or omitted its exact-byte proposal.");
            SseFaceGeomCarrierMaterializationResult materialized =
                await materializer.ApplyAsync(analysis.Proposal!,
                    CancellationToken.None);
            Assert(materialized.Written && materialized.Verified &&
                   materialized.Artifact is not null &&
                   materialized.Verification?.Verified == true &&
                   File.Exists(outputPath) &&
                   materialized.Artifact.OutputSha256 == artifact.Sha256,
                "Two-pass carrier materialization did not atomically write and reopen the proposal.");
            SseFaceGeomCarrierMaterializationVerificationResult verified =
                await materializer.VerifyAsync(analysis.Proposal!,
                    CancellationToken.None);
            Assert(verified.Verified && verified.OutputSha256 == artifact.Sha256 &&
                   verified.OutputByteLength == artifact.ByteLength,
                "Independent carrier verification did not retain exact output bytes.");
            SseFaceGeomCarrierMaterializationProposal driftedProposal =
                analysis.Proposal! with
                {
                    Assembly = request with
                    {
                        FaceTintPath = new AssetPath(
                            "Textures/Actors/Character/FaceGenData/FaceTint/BatchCarrier.esp/00000801.dds")
                    }
                };
            SseFaceGeomCarrierMaterializationVerificationResult drifted =
                await materializer.VerifyAsync(driftedProposal,
                    CancellationToken.None);
            Assert(!drifted.Verified && drifted.Diagnostics.Any(item =>
                    item.Code == "sse-facegeom-carrier-proposal-drift"),
                "Independent verification trusted a proposal whose assembly inputs drifted.");

            var malformedCarrierHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(malformedCarrierBytes)));
            var malformedCarrierArtifact = artifact with
            {
                Bytes = ImmutableArray.CreateRange(malformedCarrierBytes),
                Sha256 = malformedCarrierHash,
                ByteLength = malformedCarrierBytes.Length
            };
            var malformedDiffuseMaterializer =
                new SseFaceGeomCarrierMaterializationService(
                    new FixedCarrierAssembler(malformedCarrierArtifact),
                    new KOnlyWorkspacePolicy(workspaceRoot,
                        new WorkspacePath("F:\\ExampleGame")),
                    workspaceRoot);
            SseFaceGeomCarrierMaterializationAnalysisResult
                badDiffuseAnalysis =
                    await malformedDiffuseMaterializer.AnalyzeAsync(
                        new SseFaceGeomCarrierMaterializationAnalyzeRequest(
                            request,
                            new WorkspacePath(badDiffuseOutputPath)),
                        CancellationToken.None);
            Assert(!badDiffuseAnalysis.Accepted &&
                   badDiffuseAnalysis.Proposal is null &&
                   badDiffuseAnalysis.Diagnostics.Any(item =>
                       item.Code ==
                       "qualified-carrier-head-diffuse") &&
                   !File.Exists(badDiffuseOutputPath),
                "Carrier materialization trusted a falsely certified Manager carrier whose slot 0 was a FaceTint-shaped route.");

            const string historicalSlot0Path =
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\01-source-copies\sophia-manager-slot0-regression-20260724\Sophia-Loren-COR-slot0.nif";
            byte[] historicalSlot0Bytes =
                File.ReadAllBytes(historicalSlot0Path);
            Sha256Hash historicalSlot0Hash = new(Convert.ToHexString(
                SHA256.HashData(historicalSlot0Bytes)));
            SseNifDocument historicalSlot0Document =
                SseFaceGeomCarrierCodec.Parse(historicalSlot0Bytes);
            ImmutableArray<SseFaceGeomCarrierAssemblyShape> historicalShapes =
                historicalSlot0Document.Blocks
                    .Where(block => block.Type == "BSDynamicTriShape")
                    .Select((block, index) =>
                        new SseFaceGeomCarrierAssemblyShape(
                            new FormReference(fixturePlugin,
                                new FormId(checked((uint)(0x900 + index)))),
                            new AssetPath(
                                $"meshes/NpcManagerRegression/part-{index}.nif"),
                            historicalSlot0Hash,
                            block.Name ?? $"shape-{index}",
                            block.Name ?? $"shape-{index}",
                            block.DynamicGeometry?.VertexCount ?? 0,
                            historicalSlot0Hash,
                            historicalSlot0Hash,
                            historicalSlot0Hash,
                            PositionsChanged: false,
                            UsesFaceTint: index == 0))
                    .ToImmutableArray();
            var historicalArtifact =
                new SseFaceGeomCarrierAssemblyArtifact(
                    ImmutableArray.CreateRange(historicalSlot0Bytes),
                    historicalSlot0Hash,
                    historicalSlot0Bytes.Length,
                    historicalSlot0Document.Blocks.Length,
                    new AssetPath(
                        "Textures/Actors/Character/FaceGenData/FaceTint/NPCM_Jslot_953648526625.esp/00000800.dds"),
                    historicalShapes,
                    RuntimeAuthority: false);
            var distrustfulMaterializer =
                new SseFaceGeomCarrierMaterializationService(
                    new FixedCarrierAssembler(historicalArtifact),
                    new KOnlyWorkspacePolicy(workspaceRoot,
                        new WorkspacePath("F:\\ExampleGame")),
                    workspaceRoot);
            SseFaceGeomCarrierMaterializationAnalysisResult badAnalysis =
                await distrustfulMaterializer.AnalyzeAsync(
                    new SseFaceGeomCarrierMaterializationAnalyzeRequest(
                        request with
                        {
                            FaceTintPath = historicalArtifact.FaceTintPath
                        },
                        new WorkspacePath(badOutputPath)),
                    CancellationToken.None);
            Assert(!badAnalysis.Accepted &&
                   badAnalysis.Proposal is null &&
                   badAnalysis.Diagnostics.Any(item =>
                       item.Code == "qualified-carrier-facetint-slot") &&
                   !File.Exists(badOutputPath),
                "Carrier materialization trusted an assembler that falsely certified the historical slot-0 FaceTint carrier.");
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            if (File.Exists(badOutputPath)) File.Delete(badOutputPath);
            if (File.Exists(badDiffuseOutputPath))
                File.Delete(badDiffuseOutputPath);
        }

        Console.WriteLine(
            $"EVIDENCE ASSEMBLED-CARRIER sha256={artifact.Sha256} bytes={artifact.ByteLength} blocks={artifact.BlockCount} shapes={artifact.Shapes.Length}");
    }

    private static ImmutableArray<Vector3> ReadCarrierBoneTranslations(
        SseNifDocument document) =>
        document.Blocks
            .Where(block => block.Type == "NiNode" &&
                            !string.Equals(block.Name,
                                "BSFaceGenNiNodeSkinned",
                                StringComparison.Ordinal))
            .Select(block =>
            {
                ReadOnlySpan<byte> bytes = document.Data.AsSpan(
                    block.Offset, block.Size);
                return new Vector3(
                    BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(
                            bytes.Slice(16, sizeof(float)))),
                    BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(
                            bytes.Slice(20, sizeof(float)))),
                    BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(
                            bytes.Slice(24, sizeof(float)))));
            })
            .ToImmutableArray();

    private static Dictionary<string, int> Census(SseNifDocument document) =>
        document.Blocks.GroupBy(block => block.Type, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(),
                StringComparer.Ordinal);

    private static ImmutableArray<Vector3> ReadPackedPositions(
        SseNifDocument document,
        string? requiredName)
    {
        SseNifBlock shape = document.Blocks.Single(block =>
            block.Type == "BSDynamicTriShape" &&
            (requiredName is null ||
             string.Equals(block.Name, requiredName, StringComparison.Ordinal)));
        SseNifDynamicGeometryLayout layout = shape.DynamicGeometry ??
            throw new InvalidOperationException(
                $"Shape '{shape.Name}' lacks packed geometry.");
        var positions = ImmutableArray.CreateBuilder<Vector3>(layout.VertexCount);
        for (int index = 0; index < layout.VertexCount; index++)
        {
            ReadOnlySpan<byte> bytes = document.Data.AsSpan(
                checked(layout.VertexDataOffset + index * layout.VertexStride),
                3 * sizeof(float));
            positions.Add(new Vector3(ReadSingle(bytes, 0),
                ReadSingle(bytes, sizeof(float)),
                ReadSingle(bytes, 2 * sizeof(float))));
        }
        return positions.MoveToImmutable();
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(offset, sizeof(float))));

    private sealed class FixedCarrierAssembler(
        SseFaceGeomCarrierAssemblyArtifact artifact) :
        ISseFaceGeomCarrierAssembler
    {
        public SseFaceGeomCarrierAssemblyResult Assemble(
            SseFaceGeomCarrierAssemblyRequest request) =>
            new(true, true, artifact, []);
    }
}
