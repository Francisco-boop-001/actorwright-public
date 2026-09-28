using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestFaceGeomHairRegionsTransaction()
    {
        WorkspacePath workspaceRoot = ActorwrightWorkspace.ResolveRoot();
        SyntheticFaceGeomHairRegionsDocument fixtureDocument =
            SyntheticFaceGeomHairRegionsFixture.Load(workspaceRoot.Value);
        string fixture = fixtureDocument.Path;
        string expectedSourceHash = fixtureDocument.Sha256;
        string testRoot = Path.Combine(
            AppContext.BaseDirectory,
            "facegeom-hair-regions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            string sourcePath = Path.Combine(testRoot, "source.nif");
            File.Copy(fixture, sourcePath);
            byte[] sourceBefore = await File.ReadAllBytesAsync(sourcePath);
            Assert(
                Convert.ToHexString(SHA256.HashData(sourceBefore)) ==
                expectedSourceHash,
                "The synthetic FaceGeom source authority drifted.");

            var analyzer = new FaceGeomHairRegionsAnalyzer(workspaceRoot);
            byte[] malformed = BuildTwelveByteHairTintNif();
            string malformedPath = Path.Combine(
                testRoot,
                "overlapping-shader-color.nif");
            await File.WriteAllBytesAsync(
                malformedPath,
                malformed);
            await AssertThrowsAsync<InvalidDataException>(
                () => analyzer.AnalyzeAsync(
                    new WorkspacePath(malformedPath),
                    new Sha256Hash(
                        Convert.ToHexString(
                            SHA256.HashData(malformed))),
                    pluginColorContext: null,
                    CancellationToken.None).AsTask());
            AssertThrows<InvalidDataException>(
                () => SseNifVisualMaterialReader.Read(
                    malformed));

            FaceGeomHairRegionsAnalysis analysis =
                await analyzer.AnalyzeAsync(
                    new WorkspacePath(sourcePath),
                    new Sha256Hash(expectedSourceHash),
                    new FaceGeomHairRegionPluginColorContext(
                        "SyntheticHairRegions.esp|HCLF",
                        "SyntheticHairColor",
                        "#393728"),
                    CancellationToken.None);

            Assert(
                analysis.Schema ==
                FaceGeomHairRegionSchemas.Analysis &&
                analysis.Source.Path.Value == sourcePath &&
                analysis.Source.ByteLength == sourceBefore.LongLength &&
                analysis.Source.Sha256 ==
                new Sha256Hash(expectedSourceHash),
                "Analysis did not bind the canonical source path, length, hash, and schema.");
            Assert(
                analysis.Regions.Length == 5 &&
                analysis.Regions.All(region =>
                    region.ShapeBlockType is
                        "BSTriShape" or
                        "BSDynamicTriShape" or
                        "BSSubIndexTriShape" &&
                    region.ShaderBlockType ==
                    "BSLightingShaderProperty" &&
                    region.TintByteLength == 12 &&
                    region.ColorFloatBits.Length == 3 &&
                    region.TextureRoutes.Length == 9 &&
                    region.DefaultRole ==
                    FaceGeomHairRegionRole.Preserve),
                "Analysis did not structurally enumerate the five synthetic HairTint shapes.");
            Assert(
                analysis.Regions
                    .Select(region => region.StructuralId)
                    .Distinct(StringComparer.Ordinal)
                    .Count() == analysis.Regions.Length &&
                analysis.Regions.Count(region =>
                    region.CurrentColor == "#222222") == 3 &&
                analysis.Regions.Count(region =>
                    region.CurrentColor == "#303030") == 2,
                "Analysis lost stable identity or exact canonical HairTint colors.");
            foreach (string alternateShapeType in
                     new[]
                     {
                         "BSTriShape",
                         "BSSubIndexTriShape"
                     })
            {
                byte[] alternate = ReplaceNifTypeName(
                    sourceBefore,
                    "BSDynamicTriShape",
                    alternateShapeType);
                string alternatePath = Path.Combine(
                    testRoot,
                    alternateShapeType + ".nif");
                await File.WriteAllBytesAsync(
                    alternatePath,
                    alternate);
                FaceGeomHairRegionsAnalysis alternateAnalysis =
                    await analyzer.AnalyzeAsync(
                        new WorkspacePath(alternatePath),
                        new Sha256Hash(
                            Convert.ToHexString(
                                SHA256.HashData(alternate))),
                        pluginColorContext: null,
                        CancellationToken.None);
                Assert(
                    alternateAnalysis.Regions.Length == 5 &&
                    alternateAnalysis.Regions.All(region =>
                        region.ShapeBlockType ==
                        alternateShapeType),
                    $"Analysis did not recognize {alternateShapeType} HairTint shapes.");
                Assert(
                    SseNifVisualMaterialReader.Read(alternate)
                        .Count(material =>
                            material.ShaderType == 6) == 5,
                    $"Bethesda Formats did not independently recognize {alternateShapeType} HairTint shapes.");
            }

            var documents =
                new FaceGeomHairRegionsDocumentCodec();
            StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
                analysisDocument =
                    documents.BindAnalysis(analysis);
            Assert(
                analysisDocument.Sha256 ==
                new Sha256Hash(Convert.ToHexString(
                    SHA256.HashData(
                        analysisDocument.Utf8Json.AsSpan()))),
                "Analysis authority does not hash its exact canonical JSON bytes.");
            string outputPath = Path.Combine(
                testRoot,
                "dual-tone.nif");
            string manifestPath = Path.Combine(
                testRoot,
                "dual-tone.manifest.json");
            FaceGeomHairRegionsRequest template =
                analyzer.CreateAssignmentTemplate(
                    analysisDocument,
                    new WorkspacePath(outputPath),
                    new WorkspacePath(manifestPath));
            Assert(
                template.Schema ==
                FaceGeomHairRegionSchemas.Request &&
                template.Assignments.Length ==
                analysis.Regions.Length &&
                template.Assignments.All(assignment =>
                    assignment.Role ==
                    FaceGeomHairRegionRole.Preserve),
                "Generated assignment templates must classify every shape as Preserve.");

            string primaryGroup =
                analysis.Regions[0].SharedShaderGroupId;
            string accentGroup = analysis.Regions
                .Select(region => region.SharedShaderGroupId)
                .First(group => group != primaryGroup);
            ImmutableArray<FaceGeomHairRegionAssignment> assignments =
                template.Assignments
                    .Select(assignment =>
                    {
                        FaceGeomHairRegionsRegion region =
                            analysis.Regions.Single(value =>
                                value.StructuralId ==
                                assignment.StructuralId);
                        FaceGeomHairRegionRole role =
                            region.SharedShaderGroupId == primaryGroup
                                ? FaceGeomHairRegionRole.Primary
                                : region.SharedShaderGroupId == accentGroup
                                    ? FaceGeomHairRegionRole.Accent
                                    : FaceGeomHairRegionRole.Preserve;
                        return assignment with { Role = role };
                    })
                    .ToImmutableArray();
            FaceGeomHairRegionsRequest request = template with
            {
                PrimaryColor = "#D6BE83",
                AccentColor = "#B79A62",
                Assignments = assignments
            };
            StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
                requestDocument =
                    documents.BindRequest(request);
            var proposer =
                new FaceGeomHairRegionsProposer(
                    workspaceRoot,
                    documents);
            byte[] changedRequestBytes = ReplaceAsciiSameLength(
                requestDocument.Utf8Json.ToArray(),
                "#D6BE83",
                "#D7BE83");
            await AssertThrowsAsync<InvalidDataException>(
                () => proposer.ProposeAsync(
                    analysisDocument,
                    requestDocument with
                    {
                        Utf8Json =
                            changedRequestBytes.ToImmutableArray(),
                        Sha256 = new Sha256Hash(
                            Convert.ToHexString(
                                SHA256.HashData(
                                    changedRequestBytes)))
                    },
                    CancellationToken.None).AsTask());
            FaceGeomHairRegionsProposalResult proposed =
                await proposer.ProposeAsync(
                    analysisDocument,
                    requestDocument,
                    CancellationToken.None);
            StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
                proposalDocument = proposed.Proposal;
            FaceGeomHairRegionsProposal proposal =
                proposalDocument.Value;

            Assert(
                proposal.Schema ==
                FaceGeomHairRegionSchemas.Proposal &&
                proposal.AuthorizedEnvelopes.Length == 2 &&
                proposal.AuthorizedEnvelopes.All(envelope =>
                    envelope.ByteLength == 12 &&
                    envelope.OldFloatBits.Length == 3 &&
                    envelope.NewFloatBits.Length == 3) &&
                proposal.PredictedChangedByteOffsets.Length > 0 &&
                proposal.ExpectedOutput.ByteLength ==
                analysis.Source.ByteLength &&
                proposal.SourceFingerprints ==
                proposal.ExpectedOutputFingerprints &&
                proposal.PluginColorContext ==
                analysis.PluginColorContext &&
                proposed.Diagnostics.Any(diagnostic =>
                    diagnostic.Code ==
                    "facegeom-hair-regions-plugin-color-differs" &&
                    diagnostic.Severity ==
                    DiagnosticSeverity.Warning),
                "Proposal did not bind exact 12-byte envelopes and invariant fingerprints.");
            Assert(
                proposal.AuthorizedEnvelopes
                    .Single(envelope =>
                        envelope.Role ==
                        FaceGeomHairRegionRole.Primary)
                    .NewFloatBits.SequenceEqual(
                    [
                        0x3F56D6D7U,
                        0x3F3EBEBFU,
                        0x3F038384U
                    ]) &&
                proposal.AuthorizedEnvelopes
                    .Single(envelope =>
                        envelope.Role ==
                        FaceGeomHairRegionRole.Accent)
                    .NewFloatBits.SequenceEqual(
                    [
                        0x3F37B7B8U,
                        0x3F1A9A9BU,
                        0x3EC4C4C5U
                    ]),
                "Proposal did not use exact deterministic byte/255f IEEE-754 bits.");
            byte[] sourceAfterProposal =
                await File.ReadAllBytesAsync(sourcePath);
            Assert(
                sourceBefore.SequenceEqual(sourceAfterProposal),
                "Proposal generation mutated the source FaceGeom.");

            var independent =
                new BethesdaFaceGeomHairRegionsVerifier();
            var applier = new FaceGeomHairRegionsApplyService(
                workspaceRoot,
                independent,
                documents);

            byte[] tamperedProposalBytes =
                proposalDocument.Utf8Json.ToArray();
            tamperedProposalBytes[^2] ^= 0x01;
            var tamperedProposalDocument =
                proposalDocument with
                {
                    Utf8Json =
                        tamperedProposalBytes.ToImmutableArray()
                };
            FaceGeomHairRegionsApplyResult prewriteTamper =
                await applier.ApplyAsync(
                    requestDocument,
                    tamperedProposalDocument,
                    CancellationToken.None);
            Assert(
                !prewriteTamper.Succeeded &&
                !File.Exists(outputPath) &&
                !File.Exists(manifestPath),
                "Apply wrote output before rejecting changed proposal document bytes.");

            FaceGeomHairRegionsApplyResult applied =
                await applier.ApplyAsync(
                    requestDocument,
                    proposalDocument,
                    CancellationToken.None);
            Assert(
                applied.Succeeded &&
                applied.Manifest is not null &&
                applied.ManifestDocument is not null &&
                applied.Manifest.Schema ==
                FaceGeomHairRegionSchemas.Manifest &&
                applied.Verification is { Succeeded: true } &&
                applied.SurvivingArtifacts.IsEmpty,
                "The hash-bound FaceGeom transaction did not apply and independently verify.");
            byte[] sourceAfterApply =
                await File.ReadAllBytesAsync(sourcePath);
            Assert(
                File.Exists(outputPath) &&
                File.Exists(manifestPath) &&
                sourceBefore.SequenceEqual(sourceAfterApply),
                "Apply did not preserve the source or create both new targets.");

            byte[] output = await File.ReadAllBytesAsync(outputPath);
            ImmutableArray<int> actualChanged =
                Enumerable.Range(0, sourceBefore.Length)
                    .Where(index =>
                        sourceBefore[index] != output[index])
                    .ToImmutableArray();
            Assert(
                actualChanged.SequenceEqual(
                    proposal.PredictedChangedByteOffsets) &&
                actualChanged.All(offset =>
                    proposal.AuthorizedEnvelopes.Any(envelope =>
                        offset >= envelope.ByteOffset &&
                        offset <
                        envelope.ByteOffset +
                        envelope.ByteLength)) &&
                Convert.ToHexString(SHA256.HashData(output))
                    .Equals(
                        proposal.ExpectedOutput.Sha256.Value,
                        StringComparison.OrdinalIgnoreCase),
                "Whole-file diff proof did not match the proposal.");

            FaceGeomHairRegionsVerification verified =
                await applier.VerifyAsync(
                    requestDocument,
                    proposalDocument,
                    applied.ManifestDocument!,
                    CancellationToken.None);
            Assert(
                verified.Succeeded &&
                verified.ChangedByteOffsets.SequenceEqual(
                    actualChanged),
                "Post-write verification did not reproduce the exact byte proof.");
            FaceGeomHairRegionsAuthorizedEnvelope firstEnvelope =
                proposal.AuthorizedEnvelopes[0];
            string foreignStructuralId = analysis.Regions
                .First(region =>
                    !firstEnvelope.StructuralIds.Contains(
                        region.StructuralId,
                        StringComparer.Ordinal))
                .StructuralId;
            FaceGeomHairRegionsProposal adversarial =
                proposal with
                {
                    AuthorizedEnvelopes =
                        proposal.AuthorizedEnvelopes.SetItem(
                            0,
                            firstEnvelope with
                            {
                                StructuralIds =
                                    [foreignStructuralId]
                            })
                };
            Assert(
                !independent.Verify(
                        sourceBefore,
                        output,
                        adversarial)
                    .Succeeded,
                "Independent verification trusted adversarial structural IDs instead of rederiving shader ownership.");
            Assert(
                !independent.Verify(
                        sourceBefore,
                        output,
                        proposal with
                        {
                            AuthorizedEnvelopes =
                                proposal.AuthorizedEnvelopes
                                    .RemoveAt(1)
                        })
                    .Succeeded,
                "Independent verification accepted missing selected-group envelope coverage.");
            Assert(
                !independent.Verify(
                        sourceBefore,
                        output,
                        proposal with
                        {
                            AuthorizedEnvelopes =
                                proposal.AuthorizedEnvelopes.SetItem(
                                    0,
                                    firstEnvelope with
                                    {
                                        Role =
                                            FaceGeomHairRegionRole.Accent
                                    })
                        })
                    .Succeeded,
                "Independent verification accepted an envelope role that disagreed with assignments.");
            Assert(
                !independent.Verify(
                        sourceBefore,
                        output,
                        proposal with
                        {
                            AuthorizedEnvelopes =
                                proposal.AuthorizedEnvelopes.SetItem(
                                    0,
                                    firstEnvelope with
                                    {
                                        NewFloatBits =
                                        [
                                            0U,
                                            0U,
                                            0U
                                        ]
                                    })
                        })
                    .Succeeded,
                "Independent verification trusted proposal target bits instead of deriving the canonical color.");
            FaceGeomHairRegionsRegion preservedRegion =
                analysis.Regions.First(region =>
                    region.SharedShaderGroupId != primaryGroup &&
                    region.SharedShaderGroupId != accentGroup);
            var preserveEnvelope =
                new FaceGeomHairRegionsAuthorizedEnvelope(
                    preservedRegion.SharedShaderGroupId,
                    preservedRegion.ShaderOwnerStructuralIds,
                    FaceGeomHairRegionRole.Primary,
                    preservedRegion.TintByteOffset,
                    12,
                    preservedRegion.ColorFloatBits,
                    preservedRegion.ColorFloatBits);
            Assert(
                !independent.Verify(
                        sourceBefore,
                        output,
                        proposal with
                        {
                            AuthorizedEnvelopes =
                                proposal.AuthorizedEnvelopes.Add(
                                    preserveEnvelope)
                        })
                    .Succeeded,
                "Independent verification accepted an envelope for a Preserve shader group.");
            FaceGeomHairRegionsApplyResult staleRequest =
                await applier.ApplyAsync(
                    requestDocument with
                    {
                        Value = request with
                        {
                            PrimaryColor = "#D7BE83"
                        }
                    },
                    proposalDocument,
                    CancellationToken.None);
            Assert(
                !staleRequest.Succeeded &&
                staleRequest.Diagnostics.Any(diagnostic =>
                    diagnostic.Severity ==
                    DiagnosticSeverity.Error),
                "Apply accepted a typed request that did not match its canonical document bytes.");

            byte[] mutatedOutput = output.ToArray();
            mutatedOutput[0] ^= 0x01;
            await File.WriteAllBytesAsync(
                outputPath,
                mutatedOutput);
            FaceGeomHairRegionsVerification mutationVerdict =
                await applier.VerifyAsync(
                    requestDocument,
                    proposalDocument,
                    applied.ManifestDocument!,
                    CancellationToken.None);
            Assert(
                !mutationVerdict.Succeeded,
                "Independent verification accepted an out-of-envelope mutation.");
            await File.WriteAllBytesAsync(outputPath, output);

            await AssertThrowsAsync<InvalidDataException>(
                () => proposer.ProposeAsync(
                    analysisDocument,
                    documents.BindRequest(
                        template with
                        {
                            Output = new WorkspacePath(
                                Path.Combine(
                                    testRoot,
                                    "no-op.nif")),
                            Manifest = new WorkspacePath(
                                Path.Combine(
                                    testRoot,
                                    "no-op.json"))
                        }),
                    CancellationToken.None).AsTask());
            await AssertThrowsAsync<InvalidDataException>(
                () => proposer.ProposeAsync(
                    analysisDocument,
                    documents.BindRequest(
                        template with
                        {
                            Assignments =
                                template.Assignments.RemoveAt(0),
                            Output = new WorkspacePath(
                                Path.Combine(
                                    testRoot,
                                    "incomplete.nif")),
                            Manifest = new WorkspacePath(
                                Path.Combine(
                                    testRoot,
                                    "incomplete.json"))
                        }),
                    CancellationToken.None).AsTask());

            FaceGeomHairRegionsRequest matchingSelection =
                request with
                {
                    PrimaryColor = analysis.Regions
                        .First(region =>
                            region.SharedShaderGroupId ==
                            primaryGroup)
                        .CurrentColor,
                    Output = new WorkspacePath(
                        Path.Combine(
                            testRoot,
                            "matching-selection.nif")),
                    Manifest = new WorkspacePath(
                        Path.Combine(
                            testRoot,
                            "matching-selection.json"))
                };
            FaceGeomHairRegionsProposal matchingProposal =
                (await proposer.ProposeAsync(
                    analysisDocument,
                    documents.BindRequest(matchingSelection),
                    CancellationToken.None)).Proposal.Value;
            Assert(
                matchingProposal.AuthorizedEnvelopes
                    .Single(envelope =>
                        envelope.Role ==
                        FaceGeomHairRegionRole.Primary)
                    .OldFloatBits.SequenceEqual(
                        matchingProposal.AuthorizedEnvelopes
                            .Single(envelope =>
                                envelope.Role ==
                                FaceGeomHairRegionRole.Primary)
                            .NewFloatBits) &&
                !matchingProposal.PredictedChangedByteOffsets.IsEmpty,
                "A selected already-matching group was refused despite another physical group changing.");

            byte[] sharedBytes = sourceBefore.ToArray();
            FaceGeomHairRegionsRegion first = analysis.Regions[0];
            FaceGeomHairRegionsRegion second = analysis.Regions
                .First(region =>
                    region.ShaderBlockId !=
                    first.ShaderBlockId);
            BinaryPrimitives.WriteInt32LittleEndian(
                sharedBytes.AsSpan(
                    checked((int)second.ShapeShaderReferenceByteOffset),
                    4),
                first.ShaderBlockId);
            string sharedPath = Path.Combine(testRoot, "shared.nif");
            await File.WriteAllBytesAsync(sharedPath, sharedBytes);
            FaceGeomHairRegionsAnalysis sharedAnalysis =
                await analyzer.AnalyzeAsync(
                    new WorkspacePath(sharedPath),
                    new Sha256Hash(
                        Convert.ToHexString(
                            SHA256.HashData(sharedBytes))),
                    pluginColorContext: null,
                    CancellationToken.None);
            FaceGeomHairRegionsRegion[] linked =
                sharedAnalysis.Regions
                    .Where(region =>
                        region.SharedShaderGroupId ==
                        $"shader:{first.ShaderBlockId}")
                    .ToArray();
            Assert(
                linked.Length == 2 &&
                linked.All(region =>
                    region.SharedStructuralIds.Length == 1),
                "Analysis did not keep shared-shader shapes separate but linked.");

            FaceGeomHairRegionsRequest conflict =
                analyzer.CreateAssignmentTemplate(
                    documents.BindAnalysis(sharedAnalysis),
                    new WorkspacePath(
                        Path.Combine(testRoot, "conflict.nif")),
                    new WorkspacePath(
                        Path.Combine(testRoot, "conflict.json")));
            ImmutableArray<FaceGeomHairRegionAssignment> conflicting =
                conflict.Assignments
                    .Select(assignment =>
                        assignment.StructuralId ==
                        linked[0].StructuralId
                            ? assignment with
                            {
                                Role =
                                    FaceGeomHairRegionRole.Primary
                            }
                            : assignment.StructuralId ==
                              linked[1].StructuralId
                                ? assignment with
                                {
                                    Role =
                                        FaceGeomHairRegionRole.Accent
                                }
                                : assignment)
                    .ToImmutableArray();
            await AssertThrowsAsync<InvalidDataException>(
                () => proposer.ProposeAsync(
                    documents.BindAnalysis(sharedAnalysis),
                    documents.BindRequest(
                        conflict with
                        {
                            PrimaryColor = "#D6BE83",
                            AccentColor = "#B79A62",
                            Assignments = conflicting
                        }),
                    CancellationToken.None).AsTask());

            await TestFaceGeomHairRegionsReviewBoundaries(
                workspaceRoot,
                analyzer,
                documents,
                independent,
                analysisDocument,
                request,
                sourcePath,
                testRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task TestFaceGeomHairRegionsReviewBoundaries(
        WorkspacePath workspaceRoot,
        FaceGeomHairRegionsAnalyzer analyzer,
        FaceGeomHairRegionsDocumentCodec documents,
        IFaceGeomHairRegionsIndependentVerifier independent,
        StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
            analysisDocument,
        FaceGeomHairRegionsRequest completedRequest,
        string sourcePath,
        string testRoot)
    {
        await AssertThrowsAsync<UnauthorizedAccessException>(
            () => analyzer.AnalyzeAsync(
                new WorkspacePath(
                    @"F:\ExampleGame\Data\meshes\actors\face.nif"),
                new Sha256Hash(new string('0', 64)),
                pluginColorContext: null,
                CancellationToken.None).AsTask());
        await AssertThrowsAsync<UnauthorizedAccessException>(
            () => analyzer.AnalyzeAsync(
                new WorkspacePath(
                    @"\\server\share\face.nif"),
                new Sha256Hash(new string('0', 64)),
                pluginColorContext: null,
                CancellationToken.None).AsTask());
        var extendedDeviceSource = new WorkspacePath(
            @"\\?\K:\ExampleWorkspace\device-source.nif");
        var win32DeviceSource = new WorkspacePath(
            @"\\.\K:\ExampleWorkspace\device-source.nif");
        await AssertThrowsAsync<UnauthorizedAccessException>(
            () => analyzer.AnalyzeAsync(
                extendedDeviceSource,
                new Sha256Hash(new string('0', 64)),
                pluginColorContext: null,
                CancellationToken.None).AsTask());
        await AssertThrowsAsync<UnauthorizedAccessException>(
            () => analyzer.AnalyzeAsync(
                win32DeviceSource,
                new Sha256Hash(new string('0', 64)),
                pluginColorContext: null,
                CancellationToken.None).AsTask());
        await AssertThrowsAsync<UnauthorizedAccessException>(
            () => analyzer.AnalyzeAsync(
                new WorkspacePath(sourcePath + ":hair"),
                new Sha256Hash(new string('0', 64)),
                pluginColorContext: null,
                CancellationToken.None).AsTask());

        AssertThrows<InvalidDataException>(
            () => analyzer.CreateAssignmentTemplate(
                analysisDocument,
                new WorkspacePath(sourcePath),
                new WorkspacePath(
                    Path.Combine(
                        testRoot,
                        "alias-manifest.json"))));
        AssertThrows<InvalidDataException>(
            () => analyzer.CreateAssignmentTemplate(
                analysisDocument,
                new WorkspacePath(
                    Path.Combine(
                        testRoot,
                        "missing-parent",
                        "output.nif")),
                new WorkspacePath(
                    Path.Combine(
                        testRoot,
                        "missing-parent",
                        "manifest.json"))));
        string existing = Path.Combine(
            testRoot,
            "existing-target.nif");
        await File.WriteAllBytesAsync(existing, [0x01]);
        AssertThrows<IOException>(
            () => analyzer.CreateAssignmentTemplate(
                analysisDocument,
                new WorkspacePath(existing),
                new WorkspacePath(
                    Path.Combine(
                        testRoot,
                        "existing-manifest.json"))));
        AssertThrows<UnauthorizedAccessException>(
            () => analyzer.CreateAssignmentTemplate(
                analysisDocument,
                new WorkspacePath(
                    Path.Combine(
                        testRoot,
                        "ads-output.nif") + ":hair"),
                new WorkspacePath(
                    Path.Combine(
                        testRoot,
                        "ads-manifest.json"))));
        var extendedDeviceOutput = new WorkspacePath(
            @"\\?\K:\ExampleWorkspace\device-output.nif");
        var win32DeviceOutput = new WorkspacePath(
            @"\\.\K:\ExampleWorkspace\device-output.nif");
        AssertThrows<UnauthorizedAccessException>(
            () => analyzer.CreateAssignmentTemplate(
                analysisDocument,
                extendedDeviceOutput,
                new WorkspacePath(
                    Path.Combine(
                        testRoot,
                        "extended-device-manifest.json"))));
        AssertThrows<UnauthorizedAccessException>(
            () => analyzer.CreateAssignmentTemplate(
                analysisDocument,
                win32DeviceOutput,
                new WorkspacePath(
                    Path.Combine(
                        testRoot,
                        "win32-device-manifest.json"))));

        string zeroPath = Path.Combine(testRoot, "zero.nif");
        await File.WriteAllBytesAsync(zeroPath, []);
        await AssertThrowsAsync<InvalidDataException>(
            () => analyzer.AnalyzeAsync(
                new WorkspacePath(zeroPath),
                new Sha256Hash(Convert.ToHexString(
                    SHA256.HashData([]))),
                pluginColorContext: null,
                CancellationToken.None).AsTask());
        string oversizedPath = Path.Combine(
            testRoot,
            "oversized.nif");
        await using (var oversized = new FileStream(
                         oversizedPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            oversized.SetLength(
                (128L * 1024L * 1024L) + 1L);
        }
        await AssertThrowsAsync<InvalidDataException>(
            () => analyzer.AnalyzeAsync(
                new WorkspacePath(oversizedPath),
                new Sha256Hash(new string('0', 64)),
                pluginColorContext: null,
                CancellationToken.None).AsTask());
        byte[] tooManyShapes =
            BuildHairTintShapeCountNif(65);
        string tooManyPath = Path.Combine(
            testRoot,
            "sixty-five-shapes.nif");
        await File.WriteAllBytesAsync(
            tooManyPath,
            tooManyShapes);
        await AssertThrowsAsync<InvalidDataException>(
            () => analyzer.AnalyzeAsync(
                new WorkspacePath(tooManyPath),
                new Sha256Hash(Convert.ToHexString(
                    SHA256.HashData(tooManyShapes))),
                pluginColorContext: null,
                CancellationToken.None).AsTask());

        string reparseTarget = Path.Combine(
            testRoot,
            "reparse-target");
        string reparseLink = Path.Combine(
            testRoot,
            "reparse-link");
        Directory.CreateDirectory(reparseTarget);
        if (PhysicalReparseFixture.TryCreateDirectoryLink(
                reparseLink, reparseTarget, testRoot))
        {
            try
            {
                string linkedSource = Path.Combine(
                    reparseLink,
                    "source.nif");
                File.Copy(sourcePath, Path.Combine(
                    reparseTarget,
                    "source.nif"));
                await AssertThrowsAsync<UnauthorizedAccessException>(
                    () => analyzer.AnalyzeAsync(
                        new WorkspacePath(linkedSource),
                        analysisDocument.Value.Source.Sha256,
                        pluginColorContext: null,
                        CancellationToken.None).AsTask());
            }
            finally
            {
                if (Directory.Exists(reparseLink))
                    Directory.Delete(reparseLink);
            }
        }

        string rollbackOutput = Path.Combine(
            testRoot,
            "rollback-output.nif");
        string rollbackManifest = Path.Combine(
            testRoot,
            "rollback-manifest.json");
        var rollbackRequest = completedRequest with
        {
            Output = new WorkspacePath(rollbackOutput),
            Manifest = new WorkspacePath(rollbackManifest)
        };
        var proposer = new FaceGeomHairRegionsProposer(
            workspaceRoot,
            documents);
        FaceGeomHairRegionsProposalResult rollbackProposal =
            await proposer.ProposeAsync(
                analysisDocument,
                documents.BindRequest(rollbackRequest),
                CancellationToken.None);
        byte[] concurrentReplacement =
            Encoding.UTF8.GetBytes("concurrent replacement");
        var rollbackHooks =
            new ReplacingFaceGeomPinnedFileSystemHooks(
                rollbackManifest,
                rollbackOutput,
                concurrentReplacement);
        var rollbackBoundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                workspaceRoot,
                rollbackHooks);
        var rollbackDocuments =
            new FaceGeomHairRegionsDocumentCodec(
                rollbackBoundary);
        var rollbackApplier =
            new FaceGeomHairRegionsApplyService(
                workspaceRoot,
                independent,
                rollbackDocuments);
        FaceGeomHairRegionsApplyResult rollback =
            await rollbackApplier.ApplyAsync(
                documents.BindRequest(rollbackRequest),
                rollbackProposal.Proposal,
                CancellationToken.None);
        Assert(
            !rollback.Succeeded &&
            rollback.SurvivingArtifacts.IsEmpty &&
            rollbackHooks.SecondPromotionObserved &&
            rollbackHooks.OutputReplacementRefused &&
            !File.Exists(rollbackOutput) &&
            File.Exists(rollbackManifest) &&
            File.ReadAllBytes(rollbackManifest)
                .SequenceEqual(concurrentReplacement),
            "Failed rollback did not pin the promoted output handle or touched the unowned manifest collision.");
        File.Delete(rollbackManifest);

        string cancelOutput = Path.Combine(
            testRoot,
            "cancel-output.nif");
        string cancelManifest = Path.Combine(
            testRoot,
            "cancel-manifest.json");
        var cancelRequest = completedRequest with
        {
            Output = new WorkspacePath(cancelOutput),
            Manifest = new WorkspacePath(cancelManifest)
        };
        FaceGeomHairRegionsProposalResult cancelProposal =
            await proposer.ProposeAsync(
                analysisDocument,
                documents.BindRequest(cancelRequest),
                CancellationToken.None);
        var cancelHooks =
            new CleanupRefusingFaceGeomPinnedFileSystemHooks(
                ".npcmanager.tmp");
        var cancelBoundary =
            new FaceGeomHairRegionsWorkspaceBoundary(
                workspaceRoot,
                cancelHooks);
        var cancelDocuments =
            new FaceGeomHairRegionsDocumentCodec(
                cancelBoundary);
        var cancelApplier =
            new FaceGeomHairRegionsApplyService(
                workspaceRoot,
                new CancellingFaceGeomVerifier(),
                cancelDocuments);
        try
        {
            await cancelApplier.ApplyAsync(
                documents.BindRequest(cancelRequest),
                cancelProposal.Proposal,
                CancellationToken.None);
            throw new InvalidOperationException(
                "Cancellation verifier did not cancel apply.");
        }
        catch (FaceGeomHairRegionsOperationCanceledException exception)
        {
            Assert(
                !exception.SurvivingArtifacts.IsEmpty &&
                exception.SurvivingArtifacts.All(path =>
                    File.Exists(path.Value)),
                "Cancellation did not attach typed surviving cleanup paths. " +
                $"Message={exception.Message}; survivors=" +
                string.Join(
                    ",",
                    exception.SurvivingArtifacts.Select(path =>
                        $"{path.Value}:{File.Exists(path.Value)}")));
            foreach (WorkspacePath survivor in
                     exception.SurvivingArtifacts)
                File.Delete(survivor.Value);
        }
    }

    private static byte[] BuildTwelveByteHairTintNif()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(
            stream,
            Encoding.ASCII,
            leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(
            "Gamebryo File Format, Version 20.2.0.7\n"));
        writer.Write(0x14020007U);
        writer.Write((byte)1);
        writer.Write(12U);
        writer.Write(2U);
        writer.Write(100U);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)2);
        WriteNifSizedAscii(writer, "BSTriShape");
        WriteNifSizedAscii(
            writer,
            "BSLightingShaderProperty");
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write(100U);
        writer.Write(12U);
        writer.Write(1U);
        writer.Write(9U);
        WriteNifSizedAscii(writer, "HairShape");
        writer.Write(0U);

        writer.Write(0U);
        writer.Write(0U);
        writer.Write(-1);
        writer.Write(new byte[56]);
        writer.Write(-1);
        writer.Write(new byte[16]);
        writer.Write(-1);
        writer.Write(1);
        writer.Write(-1);

        writer.Write(6U);
        writer.Write(uint.MaxValue);
        writer.Write(0U);

        writer.Write(1U);
        writer.Write(0);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildHairTintShapeCountNif(
        int shapeCount)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(
            stream,
            Encoding.ASCII,
            leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(
            "Gamebryo File Format, Version 20.2.0.7\n"));
        writer.Write(0x14020007U);
        writer.Write((byte)1);
        writer.Write(12U);
        writer.Write(checked((uint)shapeCount + 2U));
        writer.Write(100U);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)3);
        WriteNifSizedAscii(writer, "BSTriShape");
        WriteNifSizedAscii(
            writer,
            "BSLightingShaderProperty");
        WriteNifSizedAscii(writer, "BSShaderTextureSet");
        for (int index = 0; index < shapeCount; index++)
            writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        for (int index = 0; index < shapeCount; index++)
            writer.Write(100U);
        writer.Write(56U);
        writer.Write(8U);
        writer.Write(1U);
        writer.Write(9U);
        WriteNifSizedAscii(writer, "HairShape");
        writer.Write(0U);

        for (int index = 0; index < shapeCount; index++)
        {
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(-1);
            writer.Write(new byte[56]);
            writer.Write(-1);
            writer.Write(new byte[16]);
            writer.Write(-1);
            writer.Write(shapeCount);
            writer.Write(-1);
        }

        writer.Write(6U);
        writer.Write(uint.MaxValue);
        writer.Write(0U);
        writer.Write(-1);
        writer.Write(new byte[24]);
        writer.Write(shapeCount + 1);
        float color = 34 / 255F;
        writer.Write(color);
        writer.Write(color);
        writer.Write(color);

        writer.Write(1U);
        WriteNifSizedAscii(writer, string.Empty);

        writer.Write(1U);
        writer.Write(0);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] ReplaceAsciiSameLength(
        byte[] source,
        string oldValue,
        string newValue)
    {
        byte[] oldBytes = Encoding.ASCII.GetBytes(oldValue);
        byte[] newBytes = Encoding.ASCII.GetBytes(newValue);
        Assert(
            oldBytes.Length == newBytes.Length,
            "The requested JSON mutation changed byte length.");
        int offset = source.AsSpan().IndexOf(oldBytes);
        Assert(offset >= 0, "The requested JSON mutation token was absent.");
        Assert(
            source.AsSpan(offset + oldBytes.Length).IndexOf(oldBytes) < 0,
            "The requested JSON mutation token was ambiguous.");
        byte[] changed = source.ToArray();
        newBytes.CopyTo(changed.AsSpan(offset));
        return changed;
    }

    private sealed class CancellingFaceGeomVerifier :
        IFaceGeomHairRegionsIndependentVerifier
    {
        public FaceGeomHairRegionsVerification Verify(
            ReadOnlyMemory<byte> sourceBytes,
            ReadOnlyMemory<byte> outputBytes,
            FaceGeomHairRegionsProposal proposal)
        {
            throw new OperationCanceledException(
                "Synthetic verification cancellation.");
        }
    }

    private sealed class CleanupRefusingFaceGeomPinnedFileSystemHooks(
        string deleteFailurePath) :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) =>
            actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
        }

        public void BeforeCleanup(string admittedPath)
        {
            if (admittedPath.Contains(
                    deleteFailurePath,
                    StringComparison.Ordinal))
                throw new IOException(
                    "Synthetic identity-bound cleanup failure.");
        }
    }

    private sealed class ReplacingFaceGeomPinnedFileSystemHooks(
        string manifestDestination,
        string outputDestination,
        byte[] replacement) :
        IFaceGeomHairRegionsPinnedFileSystemHooks
    {
        public bool OutputReplacementRefused
        {
            get;
            private set;
        }

        public bool SecondPromotionObserved
        {
            get;
            private set;
        }

        public void BeforeFinalOpen(string admittedPath)
        {
        }

        public string ResolveFinalPath(
            string admittedPath,
            string actualFinalPath) =>
            actualFinalPath;

        public void BeforeCreate(string admittedPath)
        {
        }

        public void BeforeRename(
            string sourcePath,
            string destinationPath)
        {
            if (!string.Equals(
                    destinationPath,
                    manifestDestination,
                    StringComparison.Ordinal))
                return;
            SecondPromotionObserved = true;
            try
            {
                File.Delete(outputDestination);
                File.WriteAllBytes(
                    outputDestination,
                    replacement);
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException)
            {
                OutputReplacementRefused = true;
            }
            File.WriteAllBytes(
                manifestDestination,
                replacement);
        }

        public void BeforeCleanup(string admittedPath)
        {
        }
    }

    private static void WriteNifSizedAscii(
        BinaryWriter writer,
        string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static byte[] ReplaceNifTypeName(
        byte[] source,
        string oldValue,
        string newValue)
    {
        byte[] oldBytes = Encoding.ASCII.GetBytes(oldValue);
        byte[] newBytes = Encoding.ASCII.GetBytes(newValue);
        int offset = source.AsSpan().IndexOf(oldBytes);
        Assert(
            offset >= 4 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                source.AsSpan(offset - 4, 4)) ==
            oldBytes.Length,
            "The synthetic NIF type-table fixture changed.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(
            stream,
            Encoding.ASCII,
            leaveOpen: true);
        writer.Write(source, 0, offset - 4);
        writer.Write(checked((uint)newBytes.Length));
        writer.Write(newBytes);
        writer.Write(
            source,
            offset + oldBytes.Length,
            source.Length - offset - oldBytes.Length);
        writer.Flush();
        return stream.ToArray();
    }
}
