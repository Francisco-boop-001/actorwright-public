using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static readonly string[] FollowerFinishForbiddenWorldSignatures =
    [
        "LAND", "NAVM", "NAVI", "WATR", "LTEX",
        "LCTN", "REGN", "CLMT", "MUSC", "IMGS"
    ];

    private static readonly string[]
        FollowerFinishForbiddenCellPayloadSignatures =
    [
        "XCLL", "XLCN", "XCLW", "XCWT", "XCAS",
        "XCCM", "XCIM", "XOWN", "XEZN"
    ];

    private const string CanonicalFollowerFinishXMarkerBase64 =
        "U1RBVEAAAAAAAIAAOwAAABpkDgAjAAIARURJRAgAWE1hcmtlcgBPQk5EDADu/" +
        "+z/AAASABIAEABNT0RMDABNYXJrZXJYLm5pZgBETkFNCAAAALRCAAAAAA==";

    private static async Task TestSkyrimFollowerFinishPlacement()
    {
        SkyrimFinishMasterFixture.EnsureAvailable();
        await using var fixture =
            await SkyrimFollowerFinishCoreFixture.CreateAsync();
        SkyrimFollowerFinishProposal proposal =
            PlacementProposal(fixture);
        var verifier = new BethesdaSkyrimFollowerFinishWorldVerifier();
        var service =
            new BethesdaSkyrimFollowerFinishPluginService(
                new KOnlyWorkspacePolicy(
                    new WorkspacePath(@"K:\ExampleWorkspace"),
                    new WorkspacePath(@"F:\ExampleGame")),
                new WorkspacePath(@"K:\ExampleWorkspace"));

        string canonicalProviderPath =
            WriteFollowerFinishPlacementProvider(
                fixture,
                "canonical-provider",
                includeCanonicalMarker: true,
                mutateCanonicalMarker: false);
        string placementEvidencePath =
            WriteFollowerFinishPlacementEvidence(
                fixture,
                proposal.Request,
                "synthetic-skyrim-exterior-coordinate-audit",
                "Skyrim.esm|0x00ABCDEF");

        SkyrimFollowerFinishProposal missingMarkerProposal =
            BindFollowerFinishPlacementAuthorities(
                proposal,
                placementEvidencePath,
                fixture.TemplatePath);
        await AssertFollowerFinishServiceRefusedAsync(
            service,
            fixture,
            missingMarkerProposal,
            "missing-marker-provider",
            "follower-finish-plugin-marker-authority");

        string wrongMarkerProviderPath =
            WriteFollowerFinishPlacementProvider(
                fixture,
                "wrong-marker-provider",
                includeCanonicalMarker: true,
                mutateCanonicalMarker: true);
        SkyrimFollowerFinishProposal wrongMarkerProviderProposal =
            BindFollowerFinishPlacementAuthorities(
                proposal,
                placementEvidencePath,
                wrongMarkerProviderPath);
        await AssertFollowerFinishServiceRefusedAsync(
            service,
            fixture,
            wrongMarkerProviderProposal,
            "wrong-marker-provider",
            "follower-finish-plugin-marker-authority");

        string duplicateMarkerProviderPath =
            WriteFollowerFinishPlacementProvider(
                fixture,
                "duplicate-marker-provider",
                includeCanonicalMarker: true,
                mutateCanonicalMarker: false,
                duplicateMarkerWithCanonicalLast: true);
        SkyrimFollowerFinishProposal duplicateMarkerProviderProposal =
            BindFollowerFinishPlacementAuthorities(
                proposal,
                placementEvidencePath,
                duplicateMarkerProviderPath);
        await AssertFollowerFinishServiceRefusedAsync(
            service,
            fixture,
            duplicateMarkerProviderProposal,
            "duplicate-marker-provider-output",
            "follower-finish-plugin-marker-authority");

        SkyrimFollowerFinishRequest wrongMarkerRequest =
            proposal.Request with
            {
                Placement = proposal.Request.Placement with
                {
                    MarkerBase = new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x3C))
                }
            };
        string wrongMarkerEvidencePath =
            WriteFollowerFinishPlacementEvidence(
                fixture,
                wrongMarkerRequest,
                "synthetic-skyrim-exterior-coordinate-audit",
                "Skyrim.esm|0x00ABCDEF",
                "wrong-marker-evidence.json");
        SkyrimFollowerFinishProposal wrongMarkerProposal =
            BindFollowerFinishPlacementAuthorities(
                WithFollowerFinishRequest(
                    proposal,
                    wrongMarkerRequest),
                wrongMarkerEvidencePath,
                canonicalProviderPath);
        await AssertFollowerFinishServiceRefusedAsync(
            service,
            fixture,
            wrongMarkerProposal,
            "caller-selected-marker",
            "follower-finish-plugin-marker-authority");

        SkyrimFollowerFinishProposal serviceProposal =
            BindFollowerFinishPlacementAuthorities(
                proposal,
                placementEvidencePath,
                canonicalProviderPath);
        SkyrimFollowerFinishExternalAuthorities canonicalAuthorities =
            serviceProposal.Request.ExternalAuthorities!;
        SkyrimFollowerFinishRequest zeroProviderRequest =
            serviceProposal.Request with
            {
                ExternalAuthorities = canonicalAuthorities with
                {
                    Providers = []
                }
            };
        await AssertFollowerFinishServiceRefusedAsync(
            service,
            fixture,
            WithFollowerFinishRequest(
                serviceProposal,
                zeroProviderRequest),
            "zero-skyrim-provider",
            "follower-finish-plugin-skyrim-provider-cardinality");
        SkyrimFollowerFinishRequest multipleProviderRequest =
            serviceProposal.Request with
            {
                ExternalAuthorities = canonicalAuthorities with
                {
                    Providers =
                    [
                        canonicalAuthorities.Providers[0],
                        canonicalAuthorities.Providers[0]
                    ]
                }
            };
        await AssertFollowerFinishServiceRefusedAsync(
            service,
            fixture,
            WithFollowerFinishRequest(
                serviceProposal,
                multipleProviderRequest),
            "multiple-skyrim-providers",
            "follower-finish-plugin-skyrim-provider-cardinality");

        SkyrimFollowerFinishRequest serviceRequest =
            serviceProposal.Request;
        string serviceDirectory = Path.Combine(
            fixture.Root,
            "service-output");
        Directory.CreateDirectory(serviceDirectory);
        var serviceOutput = new WorkspacePath(Path.Combine(
            serviceDirectory,
            serviceRequest.Source.Plugin.Value));
        SkyrimFollowerFinishPluginWriteResult serviceWrite =
            await service.WriteAsync(
                serviceRequest,
                serviceProposal,
                new WorkspacePath(fixture.SourcePath),
                serviceOutput,
                CancellationToken.None);
        Assert(serviceWrite.Written &&
               serviceWrite.OutputPlugin == serviceOutput &&
               serviceWrite.OutputSha256 is not null,
            "Authority-bound plugin service write failed: " +
            string.Join(
                " | ",
                serviceWrite.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert(serviceWrite.Diagnostics.Any(diagnostic =>
                   diagnostic.Code ==
                   "follower-finish-plugin-provider-snapshot-lock" &&
                   diagnostic.Severity ==
                   DiagnosticSeverity.Info),
            "The successful service write omitted retained snapshot-lock proof.");
        SkyrimFollowerFinishPluginVerification serviceVerification =
            await service.VerifyAsync(
                serviceRequest,
                serviceProposal,
                new WorkspacePath(fixture.SourcePath),
                serviceOutput,
                CancellationToken.None);
        Assert(serviceVerification.Verified &&
               !serviceVerification.RuntimeAuthority &&
               serviceVerification.RawGroupTreeSurface.Length > 0,
            "Authority-bound plugin service verification failed: " +
            string.Join(
                " | ",
                serviceVerification.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert(serviceVerification.Diagnostics.Any(diagnostic =>
                   diagnostic.Code ==
                   "follower-finish-plugin-provider-snapshot-lock" &&
                   diagnostic.Severity ==
                   DiagnosticSeverity.Info),
            "The successful service verification omitted retained snapshot-lock proof.");

        string outputPath = serviceOutput.Value;
        ExerciseFollowerFinishPairPlacementWriters(
            fixture,
            serviceProposal,
            outputPath);
        Assert(
            FollowerFinishRawRecordBody(
                outputPath,
                "CELL",
                0x717A)
                .SequenceEqual(
                    BuildFollowerFinishSubrecords(
                    [
                        ("XCLC", FollowerFinishCellGridPayload(
                            -42,
                            1))
                    ])),
            "The authentic placement writer did not minimize the structural " +
            "CELL to its exact XCLC-only body.");
        AssertFollowerFinishPythonVerifierPlacementContracts();
        string nonexistentPlugin = Path.Combine(
            fixture.Root,
            "high-bit-local-id-must-fail-before-file-read.esp");
        AssertThrows<ArgumentOutOfRangeException>(() =>
            SkyrimStructuralWorldspaceRecordSanitizer
                .MinimizeRequiredExteriorCellRecord(
                    nonexistentPlugin,
                    "Skyrim.esm",
                    0x01000000,
                    0x00717A,
                    -42,
                    1));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            SkyrimStructuralWorldspaceRecordSanitizer
                .MinimizeRequiredExteriorCellRecord(
                    nonexistentPlugin,
                    "Skyrim.esm",
                    0x0000003C,
                    0x0100717A,
                    -42,
                    1));
        SkyrimMod output = SkyrimMod.CreateFromBinary(
            new ModPath(
                ModKey.FromNameAndExtension(
                    serviceRequest.Source.Plugin.Value),
                new FilePath(outputPath)),
            SkyrimRelease.SkyrimSE);

        Assert(output.ModHeader.Stats.NextFormID == 0x808,
            "Placement write changed the proposal NextFormID.");
        Assert(output.Packages.Single().FormKey.ID == 0x805,
            "Placement write lost PACK 0x805.");
        Assert(output.Worldspaces.Count == 0,
            "Mutagen exposed an actual WRLD record after sanitization.");
        Assert(RawFollowerFinishFlags(
                   outputPath,
                   "REFR",
                   0x806).HasFlag(0x400) &&
               RawFollowerFinishFlags(
                   outputPath,
                   "ACHR",
                   0x807).HasFlag(0x400),
            "The two new placed records are not persistent.");

        BethesdaSkyrimFollowerFinishWorldVerification verified =
            verifier.Verify(
                new WorkspacePath(outputPath),
                serviceProposal,
                new P2Int(-42, 1));
        Assert(verified.Verified,
            "World verifier rejected the admitted minimal placement: " +
            string.Join(
                " | ",
                verified.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        string[] expectedRawSurface =
        [
            "0:5041434B:PACK:0x01000805",
            "0:57524C44/1:3C000000/4:0000FEFF/5:0000FAFF:" +
            "CELL:0x0000717A",
            "0:57524C44/1:3C000000/4:0000FEFF/5:0000FAFF/" +
            "6:7A710000/8:7A710000:REFR:0x01000806",
            "0:57524C44/1:3C000000/4:0000FEFF/5:0000FAFF/" +
            "6:7A710000/8:7A710000:ACHR:0x01000807"
        ];
        Assert(expectedRawSurface.All(
                serviceVerification.RawGroupTreeSurface.Contains),
            "The verified raw surface omits an exact record/group path: " +
            string.Join(
                " | ",
                serviceVerification.RawGroupTreeSurface));
        Assert(!serviceVerification.RawGroupTreeSurface.Any(row =>
                row.Contains(
                    ":WRLD:0x",
                    StringComparison.Ordinal)),
            "The placement plugin contains an actual master WRLD record; " +
            "only the WRLD group and nested placement branch are allowed: " +
            string.Join(
                " | ",
                serviceVerification.RawGroupTreeSurface));

        SkyrimFollowerFinishRequest alternateRequest =
            proposal.Request with
            {
                Placement = new SkyrimFollowerFinishPlacement(
                    new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x42)),
                    new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x8123)),
                    proposal.Request.Placement.MarkerBase,
                    new SkyrimExteriorTransform(
                        100.25, -200.5, 300.75,
                        0.1, 0.2, 0.3),
                    new SkyrimExteriorTransform(
                        110.25, -210.5, 310.75,
                        0.4, 0.5, 0.6))
            };
        string alternateEvidence =
            WriteFollowerFinishPlacementEvidence(
                fixture,
                alternateRequest,
                "another-project-coordinate-closure",
                "ExampleNavmesh.esm|0x00123456",
                "alternate-placement-evidence.json",
                new P2Int(3, -5));
        SkyrimFollowerFinishProposal alternateProposal =
            BindFollowerFinishPlacementAuthorities(
                WithFollowerFinishRequest(
                    proposal,
                    alternateRequest),
                alternateEvidence,
                canonicalProviderPath);
        string alternateDirectory = Path.Combine(
            fixture.Root,
            "alternate-placement");
        Directory.CreateDirectory(alternateDirectory);
        var alternateOutput = new WorkspacePath(Path.Combine(
            alternateDirectory,
            alternateRequest.Source.Plugin.Value));
        SkyrimFollowerFinishPluginWriteResult alternateWrite =
            await service.WriteAsync(
                alternateProposal.Request,
                alternateProposal,
                new WorkspacePath(fixture.SourcePath),
                alternateOutput,
                CancellationToken.None);
        Assert(alternateWrite.Written,
            "Generic proposal/evidence placement was rejected: " +
            string.Join(
                " | ",
                alternateWrite.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        SkyrimFollowerFinishPluginVerification alternateVerification =
            await service.VerifyAsync(
                alternateProposal.Request,
                alternateProposal,
                new WorkspacePath(fixture.SourcePath),
                alternateOutput,
                CancellationToken.None);
        Assert(alternateVerification.Verified,
            "Generic proposal/evidence placement did not verify: " +
            string.Join(
                " | ",
                alternateVerification.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));

        foreach (string signature in
                 FollowerFinishForbiddenWorldSignatures)
        {
            string hostile = Path.Combine(
                fixture.Root,
                $"forbidden-{signature}.esp");
            File.Copy(outputPath, hostile);
            ReplaceFollowerFinishRawSignature(
                hostile,
                "PACK",
                0x805,
                signature);
            AssertFollowerFinishWorldRefused(
                verifier,
                hostile,
                serviceProposal,
                "follower-finish-world-forbidden-signature");
        }

        foreach (string subrecord in
                 FollowerFinishForbiddenCellPayloadSignatures)
        {
            string hostile = Path.Combine(
                fixture.Root,
                $"cell-payload-{subrecord}.esp");
            File.Copy(outputPath, hostile);
            InsertFollowerFinishCellSubrecord(
                hostile,
                subrecord,
                new byte[4]);
            AssertFollowerFinishWorldRefused(
                verifier,
                hostile,
                serviceProposal,
                "follower-finish-world-cell-payload");
        }

        string extraEmptyGroup = Path.Combine(
            fixture.Root,
            "extra-empty-group.esp");
        File.Copy(outputPath, extraEmptyGroup);
        AppendFollowerFinishEmptyGroup(
            extraEmptyGroup,
            "JUNK",
            0);
        AssertFollowerFinishWorldRefused(
            verifier,
            extraEmptyGroup,
            serviceProposal,
            "follower-finish-world-group-tree");

        var groupPathCases = new (
            string Name,
            string Signature,
            uint FormId,
            int Ancestor,
            bool ChangeType)[]
        {
            ("pack-top-label", "PACK", 0x805, 0, false),
            ("world-top-label", "CELL", 0x717A, 0, false),
            ("world-child-label", "CELL", 0x717A, 1, false),
            ("cell-block-label", "CELL", 0x717A, 2, false),
            ("cell-subblock-label", "CELL", 0x717A, 3, false),
            ("cell-child-label", "REFR", 0x806, 4, false),
            ("persistent-label", "REFR", 0x806, 5, false),
            ("persistent-type", "ACHR", 0x807, 5, true)
        };
        foreach (var hostileCase in groupPathCases)
        {
            string hostile = Path.Combine(
                fixture.Root,
                $"group-{hostileCase.Name}.esp");
            File.Copy(outputPath, hostile);
            MutateFollowerFinishAncestorGroup(
                hostile,
                hostileCase.Signature,
                hostileCase.FormId,
                hostileCase.Ancestor,
                hostileCase.ChangeType);
            AssertFollowerFinishWorldRefused(
                verifier,
                hostile,
                serviceProposal,
                "follower-finish-world-group-tree");
        }

        string partialWorld = Path.Combine(
            fixture.Root,
            "partial-tamriel-world.esp");
        File.Copy(outputPath, partialWorld);
        InsertFollowerFinishPartialWorldRecord(
            partialWorld,
            0x0000003C);
        AssertFollowerFinishWorldRefused(
            verifier,
            partialWorld,
            serviceProposal,
            "follower-finish-world-partial-master-record");

        foreach (string subrecord in new[] { "DATA", "LTMP" })
        {
            string hostile = Path.Combine(
                fixture.Root,
                $"cell-hostile-{subrecord}.esp");
            File.Copy(outputPath, hostile);
            InsertFollowerFinishCellSubrecord(
                hostile,
                subrecord,
                subrecord == "DATA"
                    ? new byte[2]
                    : new byte[4]);
            AssertFollowerFinishWorldRefused(
                verifier,
                hostile,
                serviceProposal,
                "follower-finish-world-cell-payload");
        }

        foreach ((string record, uint formId, string subrecord)
                 in new[]
                 {
                     ("CELL", 0x717Au, "XCLC")
                 })
        {
            string hostile = Path.Combine(
                fixture.Root,
                $"cell-identity-{subrecord}.esp");
            File.Copy(outputPath, hostile);
            MutateFollowerFinishSubrecordByte(
                hostile,
                record,
                formId,
                subrecord);
            AssertFollowerFinishWorldRefused(
                verifier,
                hostile,
                serviceProposal,
                "follower-finish-world-cell-payload");
        }

        foreach ((string record, uint formId, string subrecord)
                 in new[]
                 {
                     ("REFR", 0x806u, "NAME"),
                     ("REFR", 0x806u, "DATA"),
                     ("ACHR", 0x807u, "NAME"),
                     ("ACHR", 0x807u, "DATA")
                 })
        {
            string hostile = Path.Combine(
                fixture.Root,
                $"placed-payload-{record}-{subrecord}.esp");
            File.Copy(outputPath, hostile);
            MutateFollowerFinishSubrecordByte(
                hostile,
                record,
                formId,
                subrecord);
            AssertFollowerFinishWorldRefused(
                verifier,
                hostile,
                serviceProposal,
                "follower-finish-world-record-payload");
        }

        foreach ((string record, uint formId, string expectedCode)
                 in new[]
                 {
                     ("CELL", 0x717Au,
                         "follower-finish-world-cell-payload"),
                     ("REFR", 0x806u,
                         "follower-finish-world-record-payload"),
                     ("ACHR", 0x807u,
                         "follower-finish-world-record-payload")
                 })
        {
            string hostile = Path.Combine(
                fixture.Root,
                $"record-flags-{record}.esp");
            File.Copy(outputPath, hostile);
            XorFollowerFinishRawFlags(
                hostile,
                record,
                formId,
                0x1);
            AssertFollowerFinishWorldRefused(
                verifier,
                hostile,
                serviceProposal,
                expectedCode);
        }

        string externalPack = Path.Combine(
            fixture.Root,
            "external-pack-override.esp");
        File.Copy(outputPath, externalPack);
        ReplaceFollowerFinishRawFormId(
            externalPack,
            "PACK",
            0x805,
            0x00000805);
        AssertFollowerFinishWorldRefused(
            verifier,
            externalPack,
            serviceProposal,
            "follower-finish-world-existing-record-override");

        string wrongCell = Path.Combine(
            fixture.Root,
            "wrong-cell.esp");
        File.Copy(outputPath, wrongCell);
        ReplaceFollowerFinishRawFormId(
            wrongCell,
            "CELL",
            0x717A,
            0x717B);
        AssertFollowerFinishWorldRefused(
            verifier,
            wrongCell,
            serviceProposal,
            "follower-finish-world-cell");
    }

    private static void ExerciseFollowerFinishPairPlacementWriters(
        SkyrimFollowerFinishCoreFixture fixture,
        SkyrimFollowerFinishProposal placementProposal,
        string companionSourcePath)
    {
        const string subjectPlugin = "PairSubject.esp";
        const string companionPlugin = "CoreFixture.esp";
        const string outfitPlugin = "PairOutfit.esp";
        string subjectSourcePath = Path.Combine(
            fixture.Root,
            subjectPlugin);
        string outfitPath = Path.Combine(
            fixture.Root,
            outfitPlugin);
        string pairCompanionSourcePath = Path.Combine(
            fixture.Root,
            "PairCompanionSource.esp");
        File.Copy(
            companionSourcePath,
            pairCompanionSourcePath);
        InsertFollowerFinishPartialWorldRecord(
            pairCompanionSourcePath,
            0x0000003C);
        WriteFollowerFinishPairSubjectSource(
            subjectSourcePath,
            subjectPlugin);
        WriteFollowerFinishPairOutfitSource(
            outfitPath,
            outfitPlugin);

        SkyrimFollowerFinishPairFile PairFile(string path) =>
            new(
                new WorkspacePath(path),
                new FileInfo(path).Length,
                SkyrimFollowerFinishCoreFixture
                    .HashFollowerFinishCoreFile(path));
        SkyrimFollowerFinishPairActor PairActor(
            string role,
            string plugin,
            string path) =>
            new(
                role,
                new WorkspacePath(fixture.Root),
                new PluginName(plugin),
                PairFile(path),
                new FormId(0x800),
                PairFile(path),
                PairFile(path),
                PairFile(path),
                PairFile(path),
                PairFile(path));

        SkyrimFollowerFinishPairActor companion = PairActor(
            "companion",
            companionPlugin,
            pairCompanionSourcePath);
        SkyrimFollowerFinishPairActor subject = PairActor(
            "subject",
            subjectPlugin,
            subjectSourcePath);
        var outfit = new SkyrimFollowerFinishPairOutfit(
            PairFile(outfitPath),
            new PluginName(outfitPlugin),
            PairFile(outfitPath),
            PairFile(outfitPath),
            PairFile(outfitPath),
            new FormReference(
                new PluginName(outfitPlugin),
                new FormId(0x801)),
            new FormReference(
                new PluginName(outfitPlugin),
                new FormId(0x800)),
            new FormReference(
                new PluginName(outfitPlugin),
                new FormId(0x802)),
            new FormReference(
                new PluginName(outfitPlugin),
                new FormId(0x803)),
            new FormReference(
                new PluginName(outfitPlugin),
                new FormId(0x804)));
        SkyrimFollowerFinishPlacement sourcePlacement =
            placementProposal.Request.Placement;
        var pairRequest = new SkyrimFollowerFinishPairRequest(
            SkyrimFollowerFinishPairRequest.SchemaVersionValue,
            SkyrimFollowerFinishPairRequest.OperationName,
            companion,
            subject,
            new FormReference(
                new PluginName(companionPlugin),
                new FormId(0x806)),
            outfit,
            new SkyrimFollowerFinishPairPlacement(
                sourcePlacement.Worldspace,
                sourcePlacement.Cell,
                -42,
                1,
                new SkyrimFollowerFinishPairTransform(
                    sourcePlacement.Actor.X,
                    sourcePlacement.Actor.Y,
                    sourcePlacement.Actor.Z,
                    sourcePlacement.Actor.RotationX,
                    sourcePlacement.Actor.RotationY,
                    sourcePlacement.Actor.RotationZ)),
            SkyrimFollowerFinishPairAllocation.Default,
            new WorkspacePath(Path.Combine(
                fixture.Root,
                "pair-output")),
            new WorkspacePath(Path.Combine(
                fixture.Root,
                "pair-output.zip")),
            "Synthetic paired placement writer regression.",
            new SkyrimFollowerFinishPairCompanionFinish(
                SkyrimFollowerFinishPairCompanionAllocation.Default,
                new SkyrimFollowerFinishPairHairFinish(
                    new FormId(0x801),
                    0x94876A,
                    0xD6BE83,
                    ["Hair"],
                    [0x21, 0x21, 0x21],
                    [0xD6, 0xBE, 0x83])));
        var companionSnapshot =
            new SkyrimFollowerFinishPairSourceSnapshot(
                new PluginName(companionPlugin),
                companion.PluginFile.Sha256,
                0,
                [new PluginName("Skyrim.esm")],
                new FormId(0x808),
                [],
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x13746)),
                new FormReference(
                    new PluginName(companionPlugin),
                    new FormId(0x801)),
                null,
                null,
                [
                    new FormReference(
                        new PluginName(companionPlugin),
                        new FormId(0x805))
                ]);
        var subjectSnapshot =
            new SkyrimFollowerFinishPairSourceSnapshot(
                new PluginName(subjectPlugin),
                subject.PluginFile.Sha256,
                0,
                [new PluginName("Skyrim.esm")],
                new FormId(0x805),
                [],
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x13746)),
                null,
                null,
                null,
                []);
        var pairProposal = new SkyrimFollowerFinishPairProposal(
            SkyrimFollowerFinishPairRequest.SchemaVersionValue,
            SkyrimFollowerFinishPairRequest.OperationName,
            new Sha256Hash(new string('6', 64)),
            pairRequest,
            companionSnapshot,
            subjectSnapshot,
            [
                new PluginName("Skyrim.esm"),
                new PluginName(companionPlugin),
                new PluginName(outfitPlugin)
            ],
            [],
            [],
            false,
            [
                new PluginName("Skyrim.esm"),
                new PluginName(outfitPlugin)
            ]);

        string subjectOutput = Path.Combine(
            fixture.Root,
            "pair-subject-output.esp");
        string companionOutput = Path.Combine(
            fixture.Root,
            "pair-companion-output.esp");
        InvokeFollowerFinishPairWriter(
            "WriteSubjectPlugin",
            pairRequest,
            pairProposal,
            subjectOutput);
        InvokeFollowerFinishPairWriter(
            "WriteCompanionPlugin",
            pairRequest,
            pairProposal,
            companionOutput);

        byte[] expectedCellBody =
            BuildFollowerFinishSubrecords(
            [
                ("XCLC", FollowerFinishCellGridPayload(
                    -42,
                    1))
            ]);
        Assert(
            FollowerFinishRawRecordBody(
                subjectOutput,
                "CELL",
                0x717A)
                .SequenceEqual(expectedCellBody),
            "The paired subject writer did not minimize the structural " +
            "CELL to XCLC only.");
        Assert(
            FollowerFinishRawRecordBody(
                companionOutput,
                "CELL",
                0x717A)
                .SequenceEqual(expectedCellBody),
            "The paired companion writer did not minimize the structural " +
            "CELL to XCLC only.");
    }

    private static void InvokeFollowerFinishPairWriter(
        string name,
        SkyrimFollowerFinishPairRequest request,
        SkyrimFollowerFinishPairProposal proposal,
        string output)
    {
        System.Reflection.MethodInfo method =
            typeof(BethesdaSkyrimFollowerFinishPairService)
                .GetMethod(
                    name,
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static) ??
            throw new InvalidOperationException(
                $"The paired writer {name} is missing.");
        method.Invoke(
            null,
            [request, proposal, new WorkspacePath(output)]);
    }

    private static void
        AssertFollowerFinishPythonVerifierPlacementContracts()
    {
        string tools = Path.Combine(
            @"K:\ExampleWorkspace",
            "projects",
            "NpcManagerReimplementation",
            "tools");
        string code = """
            import struct
            import sys

            sys.path.insert(0, sys.argv[1])
            import verify_skyrim_follower_finish_package as single
            import verify_skyrim_follower_finish_pair_package as pair

            payload = struct.pack("<iiI", -42, 1, 0)
            exact = b"XCLC" + struct.pack("<H", len(payload)) + payload
            hostile = (
                b"XXXX"
                + struct.pack("<H", 4)
                + struct.pack("<I", len(payload))
                + b"XCLC"
                + struct.pack("<H", 0)
                + payload
            )
            assert single.parse_subrecords(hostile) == [("XCLC", payload)]
            assert single.is_exact_exterior_cell_body(exact, -42, 1)
            assert not single.is_exact_exterior_cell_body(
                hostile,
                -42,
                1,
            )
            assert pair.required_exterior_placement_roles(
                {"companionFinish": None}
            ) == ("subject",)
            assert pair.required_exterior_placement_roles(
                {"companionFinish": {}}
            ) == ("subject", "companion")
            print("PASS Python exterior placement verifier contracts")
            """;
        var start = new ProcessStartInfo
        {
            FileName = "python",
            WorkingDirectory = tools,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(code);
        start.ArgumentList.Add(tools);
        using Process process = Process.Start(start) ??
            throw new InvalidOperationException(
                "Python verifier contract process did not start.");
        string standardOutput =
            process.StandardOutput.ReadToEnd();
        string standardError =
            process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert(
            process.ExitCode == 0,
            "Python exterior placement verifier contracts failed: " +
            standardOutput + standardError);
    }

    private static void WriteFollowerFinishPairSubjectSource(
        string path,
        string pluginName)
    {
        ModKey key = ModKey.FromNameAndExtension(pluginName);
        var mod = new SkyrimMod(
            key,
            SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(
            new MasterReference
            {
                Master =
                    ModKey.FromNameAndExtension("Skyrim.esm")
            });
        mod.ModHeader.Stats.NextFormID = 0x805;
        mod.Npcs.Add(
            new Npc(
                new FormKey(key, 0x800),
                SkyrimRelease.SkyrimSE)
            {
                FormVersion = 44,
                EditorID = "PairSubjectNpc"
            });
        WriteFollowerFinishCorePlugin(mod, path);
    }

    private static void WriteFollowerFinishPairOutfitSource(
        string path,
        string pluginName)
    {
        ModKey key = ModKey.FromNameAndExtension(pluginName);
        var mod = new SkyrimMod(
            key,
            SkyrimRelease.SkyrimSE);
        mod.ModHeader.Stats.NextFormID = 0x805;
        var addon = new ArmorAddon(
            new FormKey(key, 0x800),
            SkyrimRelease.SkyrimSE)
        {
            FormVersion = 44,
            EditorID = "PairTorsoAddon",
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags =
                    (BipedObjectFlag)0x04
            }
        };
        mod.ArmorAddons.Add(addon);
        var torso = new Armor(
            new FormKey(key, 0x801),
            SkyrimRelease.SkyrimSE)
        {
            FormVersion = 44,
            EditorID = "PairTorso"
        };
        torso.Armature.Add(
            new FormLink<IArmorAddonGetter>(
                addon.FormKey));
        mod.Armors.Add(torso);
        mod.Armors.Add(
            new Armor(
                new FormKey(key, 0x802),
                SkyrimRelease.SkyrimSE)
            {
                FormVersion = 44,
                EditorID = "PairBoots"
            });
        mod.Armors.Add(
            new Armor(
                new FormKey(key, 0x803),
                SkyrimRelease.SkyrimSE)
            {
                FormVersion = 44,
                EditorID = "PairGauntlets"
            });
        mod.TextureSets.Add(
            new TextureSet(
                new FormKey(key, 0x804),
                SkyrimRelease.SkyrimSE)
            {
                FormVersion = 44,
                EditorID = "PairSkin"
            });
        WriteFollowerFinishCorePlugin(mod, path);
    }

    private static string WriteFollowerFinishPlacementProvider(
        SkyrimFollowerFinishCoreFixture fixture,
        string directoryName,
        bool includeCanonicalMarker,
        bool mutateCanonicalMarker,
        bool duplicateMarkerWithCanonicalLast = false)
        => WriteFollowerFinishPlacementProvider(fixture.Root, fixture.TemplatePath,
            directoryName, includeCanonicalMarker, mutateCanonicalMarker,
            duplicateMarkerWithCanonicalLast);

    private static string WriteFollowerFinishPlacementProvider(
        string root,
        string templatePath,
        string directoryName,
        bool includeCanonicalMarker,
        bool mutateCanonicalMarker,
        bool duplicateMarkerWithCanonicalLast = false)
    {
        string directory = Path.Combine(
            root,
            directoryName);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Skyrim.esm");
        byte[] source = File.ReadAllBytes(templatePath);
        if (!includeCanonicalMarker)
        {
            File.WriteAllBytes(path, source);
            return path;
        }

        byte[] canonicalMarker = Convert.FromBase64String(
            CanonicalFollowerFinishXMarkerBase64);
        byte[] marker = canonicalMarker.ToArray();
        if (mutateCanonicalMarker)
            marker[30] ^= 0x01;
        byte[][] records = duplicateMarkerWithCanonicalLast
            ?
            [
                MutatedFollowerFinishMarker(canonicalMarker),
                canonicalMarker
            ]
            : [marker];
        int recordsLength = records.Sum(record => record.Length);
        byte[] group = new byte[24 + recordsLength];
        Encoding.ASCII.GetBytes("GRUP").CopyTo(group, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            group.AsSpan(4, 4),
            checked((uint)group.Length));
        Encoding.ASCII.GetBytes("STAT").CopyTo(group, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(
            group.AsSpan(12, 4),
            0);
        int recordOffset = 24;
        foreach (byte[] record in records)
        {
            record.CopyTo(group, recordOffset);
            recordOffset += record.Length;
        }

        byte[] provider = new byte[source.Length + group.Length];
        source.CopyTo(provider, 0);
        group.CopyTo(provider, source.Length);
        File.WriteAllBytes(path, provider);
        return path;
    }

    private static byte[] MutatedFollowerFinishMarker(
        byte[] canonical)
    {
        byte[] mutated = canonical.ToArray();
        mutated[30] ^= 0x01;
        return mutated;
    }

    private static string WriteFollowerFinishPlacementEvidence(
        SkyrimFollowerFinishCoreFixture fixture,
        SkyrimFollowerFinishRequest request,
        string artifactKind,
        string navmesh,
        string fileName = "placement-evidence.json",
        P2Int? cellGrid = null)
        => WriteFollowerFinishPlacementEvidence(fixture.Root, request, artifactKind,
            navmesh, fileName, cellGrid);

    private static string WriteFollowerFinishPlacementEvidence(
        string root,
        SkyrimFollowerFinishRequest request,
        string artifactKind,
        string navmesh,
        string fileName = "placement-evidence.json",
        P2Int? cellGrid = null)
    {
        P2Int grid = cellGrid ?? new P2Int(-42, 1);
        object document = new
        {
            schemaVersion = 1,
            artifactKind,
            status =
                "PASS_COORDINATE_QUALIFIED_STATIC_RUNTIME_REQUIRED",
            existingNavmeshProof = new
            {
                navmesh,
                conclusion =
                    "Both points are inside existing triangles. " +
                    "No NAVM edit is required or admitted."
            },
            clearanceSummary = new
            {
                result = "PASS"
            },
            target = new
            {
                worldspace =
                    request.Placement.Worldspace.ToString(),
                cell = request.Placement.Cell.ToString(),
                cellGrid = new[] { (int)grid.X, (int)grid.Y },
                markerBase =
                    request.Placement.MarkerBase.ToString(),
                actor = new
                {
                    position = new[]
                    {
                        request.Placement.Actor.X,
                        request.Placement.Actor.Y,
                        request.Placement.Actor.Z
                    },
                    rotation = new[]
                    {
                        request.Placement.Actor.RotationX,
                        request.Placement.Actor.RotationY,
                        request.Placement.Actor.RotationZ
                    }
                },
                anchor = new
                {
                    position = new[]
                    {
                        request.Placement.Anchor.X,
                        request.Placement.Anchor.Y,
                        request.Placement.Anchor.Z
                    },
                    rotation = new[]
                    {
                        request.Placement.Anchor.RotationX,
                        request.Placement.Anchor.RotationY,
                        request.Placement.Anchor.RotationZ
                    }
                }
            }
        };
        string path = Path.Combine(root, fileName);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(document));
        return path;
    }

    private static SkyrimFollowerFinishProposal
        BindFollowerFinishPlacementAuthorities(
            SkyrimFollowerFinishProposal proposal,
            string placementEvidencePath,
            string providerPath)
    {
        SkyrimFollowerFinishRequest request =
            proposal.Request with
            {
                ExternalAuthorities =
                    new SkyrimFollowerFinishExternalAuthorities(
                        new SkyrimFollowerFinishFileAuthority(
                            new WorkspacePath(placementEvidencePath),
                            new FileInfo(
                                placementEvidencePath).Length,
                            SkyrimFollowerFinishCoreFixture
                                .HashFollowerFinishCoreFile(
                                    placementEvidencePath)),
                        [
                            new SkyrimFollowerFinishPluginProviderAuthority(
                                new PluginName("Skyrim.esm"),
                                new WorkspacePath(providerPath),
                                new FileInfo(providerPath).Length,
                                SkyrimFollowerFinishCoreFixture
                                    .HashFollowerFinishCoreFile(
                                        providerPath))
                        ])
            };
        return WithFollowerFinishRequest(proposal, request);
    }

    private static async Task
        AssertFollowerFinishServiceRefusedAsync(
            BethesdaSkyrimFollowerFinishPluginService service,
            SkyrimFollowerFinishCoreFixture fixture,
            SkyrimFollowerFinishProposal proposal,
            string directoryName,
            string expectedCode)
    {
        string directory = Path.Combine(
            fixture.Root,
            directoryName);
        Directory.CreateDirectory(directory);
        var output = new WorkspacePath(Path.Combine(
            directory,
            proposal.Request.Source.Plugin.Value));
        SkyrimFollowerFinishPluginWriteResult result =
            await service.WriteAsync(
                proposal.Request,
                proposal,
                new WorkspacePath(fixture.SourcePath),
                output,
                CancellationToken.None);
        Assert(
            !result.Written &&
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == expectedCode &&
                diagnostic.Severity ==
                DiagnosticSeverity.Error),
            $"Plugin service accepted hostile '{directoryName}', " +
            $"or omitted {expectedCode}: " +
            string.Join(
                " | ",
                result.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    private static void MutateFollowerFinishAncestorGroup(
        string path,
        string signature,
        uint localFormId,
        int ancestorIndex,
        bool changeType)
    {
        byte[] bytes = File.ReadAllBytes(path);
        (_, List<int> ancestors) =
            FindFollowerFinishRawRecordWithGroups(
                bytes,
                0,
                bytes.Length,
                signature,
                localFormId,
                []);
        if (ancestorIndex < 0 ||
            ancestorIndex >= ancestors.Count)
            throw new InvalidDataException(
                $"Ancestor {ancestorIndex} does not exist for " +
                $"{signature} 0x{localFormId:X8}.");
        int group = ancestors[ancestorIndex];
        if (changeType)
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(group + 12, 4),
                9);
        else
            Encoding.ASCII.GetBytes("BAD!")
                .CopyTo(bytes, group + 8);
        File.WriteAllBytes(path, bytes);
    }

    private static void AppendFollowerFinishEmptyGroup(
        string path,
        string label,
        uint groupType)
    {
        byte[] labelBytes = Encoding.ASCII.GetBytes(label);
        if (labelBytes.Length != 4)
            throw new ArgumentException(
                "A hostile raw group label must be four bytes.",
                nameof(label));
        byte[] source = File.ReadAllBytes(path);
        byte[] output = new byte[source.Length + 24];
        source.CopyTo(output, 0);
        int group = source.Length;
        Encoding.ASCII.GetBytes("GRUP").CopyTo(output, group);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(group + 4, 4),
            24);
        labelBytes.CopyTo(output, group + 8);
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(group + 12, 4),
            groupType);
        File.WriteAllBytes(path, output);
    }

    private static void InsertFollowerFinishPartialWorldRecord(
        string path,
        uint rawWorldFormId)
    {
        byte[] source = File.ReadAllBytes(path);
        int tes4BodyLength = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(
                source.AsSpan(4, 4)));
        int position = checked(24 + tes4BodyLength);
        int worldGroup = -1;
        while (position < source.Length)
        {
            string signature =
                Encoding.ASCII.GetString(
                    source,
                    position,
                    4);
            int size = checked((int)
                BinaryPrimitives.ReadUInt32LittleEndian(
                    source.AsSpan(position + 4, 4)));
            if (signature != "GRUP")
                throw new InvalidDataException(
                    "A top-level fixture item is not a group.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(
                    source.AsSpan(position + 12, 4)) == 0 &&
                Encoding.ASCII.GetString(
                    source,
                    position + 8,
                    4) == "WRLD")
            {
                worldGroup = position;
                break;
            }
            position = checked(position + size);
        }
        if (worldGroup < 0)
            throw new InvalidDataException(
                "The fixture lacks its WRLD group.");

        byte[] record = new byte[81];
        Encoding.ASCII.GetBytes("WRLD")
            .CopyTo(record, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(4, 4),
            57);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(12, 4),
            rawWorldFormId);
        BinaryPrimitives.WriteUInt16LittleEndian(
            record.AsSpan(20, 2),
            44);
        int body = 24;
        foreach ((string signature, int length) in
                 new[]
                 {
                     ("ONAM", 16),
                     ("DATA", 1),
                     ("NAM0", 8),
                     ("NAM9", 8)
                 })
        {
            Encoding.ASCII.GetBytes(signature)
                .CopyTo(record, body);
            BinaryPrimitives.WriteUInt16LittleEndian(
                record.AsSpan(body + 4, 2),
                checked((ushort)length));
            body += 6 + length;
        }
        Assert(body == record.Length,
            "The hand-derived 81-byte WRLD fixture is malformed.");

        int insertAt = worldGroup + 24;
        byte[] output =
            new byte[source.Length + record.Length];
        source.AsSpan(0, insertAt).CopyTo(output);
        record.CopyTo(output, insertAt);
        source.AsSpan(insertAt).CopyTo(
            output.AsSpan(insertAt + record.Length));
        uint oldGroupSize =
            BinaryPrimitives.ReadUInt32LittleEndian(
                source.AsSpan(worldGroup + 4, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(worldGroup + 4, 4),
            checked(oldGroupSize + (uint)record.Length));

        int tes4Position = 24;
        int tes4End = 24 + tes4BodyLength;
        while (tes4Position < tes4End)
        {
            string signature = Encoding.ASCII.GetString(
                output,
                tes4Position,
                4);
            int length =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    output.AsSpan(tes4Position + 4, 2));
            if (signature == "HEDR")
            {
                uint oldCount =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        output.AsSpan(
                            tes4Position + 10,
                            4));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    output.AsSpan(
                        tes4Position + 10,
                        4),
                    checked(oldCount + 1));
                File.WriteAllBytes(path, output);
                return;
            }
            tes4Position += 6 + length;
        }
        throw new InvalidDataException(
            "The fixture lacks TES4 HEDR.");
    }

    private static void MutateFollowerFinishSubrecordByte(
        string path,
        string signature,
        uint localFormId,
        string subrecord)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int record = FindFollowerFinishRawRecord(
            bytes,
            0,
            bytes.Length,
            signature,
            localFormId);
        int bodyLength = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(record + 4, 4)));
        int position = record + 24;
        int end = checked(position + bodyLength);
        while (position < end)
        {
            string current = Encoding.ASCII.GetString(
                bytes,
                position,
                4);
            int length =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(position + 4, 2));
            if (current == subrecord)
            {
                if (length == 0)
                    throw new InvalidDataException(
                        $"Raw {subrecord} has no payload byte.");
                bytes[position + 6 + length - 1] ^= 0x01;
                File.WriteAllBytes(path, bytes);
                return;
            }
            position = checked(position + 6 + length);
        }
        throw new InvalidDataException(
            $"Raw {signature} lacks subrecord {subrecord}.");
    }

    private static void XorFollowerFinishRawFlags(
        string path,
        string signature,
        uint localFormId,
        uint mask)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int record = FindFollowerFinishRawRecord(
            bytes,
            0,
            bytes.Length,
            signature,
            localFormId);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(record + 8, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(record + 8, 4),
            flags ^ mask);
        File.WriteAllBytes(path, bytes);
    }

    private static SkyrimFollowerFinishProposal PlacementProposal(
        SkyrimFollowerFinishCoreFixture fixture)
    {
        SkyrimFollowerFinishRequest request = fixture.Request with
        {
            Placement = new SkyrimFollowerFinishPlacement(
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x3C)),
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x717A)),
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x3B)),
                new SkyrimExteriorTransform(
                    -171052.0,
                    6624.0,
                    -3927.4932,
                    0,
                    0,
                    0),
                new SkyrimExteriorTransform(
                    -171116.0,
                    6624.0,
                    -3925.4575,
                    0,
                    0,
                    0))
        };
        return WithFollowerFinishRequest(
            fixture.Proposal,
            request);
    }

    private static Cell FollowerFinishPlacementCell(
        SkyrimMod mod) =>
        mod.Worldspaces
            .SelectMany(world => world.SubCells)
            .SelectMany(block => block.Items)
            .SelectMany(subBlock => subBlock.Items)
            .Single();

    private static bool TransformMatches(
        Placement? actual,
        SkyrimExteriorTransform expected) =>
        actual is not null &&
        Math.Abs(actual.Position.X - expected.X) < 0.001 &&
        Math.Abs(actual.Position.Y - expected.Y) < 0.001 &&
        Math.Abs(actual.Position.Z - expected.Z) < 0.001 &&
        Math.Abs(actual.Rotation.X - expected.RotationX) < 0.001 &&
        Math.Abs(actual.Rotation.Y - expected.RotationY) < 0.001 &&
        Math.Abs(actual.Rotation.Z - expected.RotationZ) < 0.001;

    private static uint RawFollowerFinishFlags(
        string path,
        string signature,
        uint localFormId)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int offset = FindFollowerFinishRawRecord(
            bytes,
            0,
            bytes.Length,
            signature,
            localFormId);
        return BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(offset + 8, 4));
    }

    private static byte[] FollowerFinishRawRecordBody(
        string path,
        string signature,
        uint localFormId)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int offset = FindFollowerFinishRawRecord(
            bytes,
            0,
            bytes.Length,
            signature,
            localFormId);
        int bodyLength = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 4, 4)));
        return bytes.AsSpan(
                offset + 24,
                bodyLength)
            .ToArray();
    }

    private static byte[] FollowerFinishCellGridPayload(
        int x,
        int y)
    {
        byte[] bytes = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(0, 4),
            x);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(4, 4),
            y);
        return bytes;
    }

    private static byte[] BuildFollowerFinishSubrecords(
        IEnumerable<(string Signature, byte[] Payload)> rows)
    {
        (string Signature, byte[] Payload)[] materialized =
            rows.ToArray();
        int length = materialized.Sum(row =>
            checked(6 + row.Payload.Length));
        byte[] bytes = new byte[length];
        int position = 0;
        foreach ((string signature, byte[] payload)
                 in materialized)
        {
            Encoding.ASCII.GetBytes(signature)
                .CopyTo(bytes, position);
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2),
                checked((ushort)payload.Length));
            payload.CopyTo(bytes, position + 6);
            position += 6 + payload.Length;
        }
        return bytes;
    }

    private static bool HasFlag(this uint value, uint flag) =>
        (value & flag) == flag;

    private static void ReplaceFollowerFinishRawSignature(
        string path,
        string signature,
        uint localFormId,
        string replacement)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int offset = FindFollowerFinishRawRecord(
            bytes,
            0,
            bytes.Length,
            signature,
            localFormId);
        Encoding.ASCII.GetBytes(replacement)
            .CopyTo(bytes, offset);
        File.WriteAllBytes(path, bytes);
    }

    private static void ReplaceFollowerFinishRawFormId(
        string path,
        string signature,
        uint localFormId,
        uint replacement)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int offset = FindFollowerFinishRawRecord(
            bytes,
            0,
            bytes.Length,
            signature,
            localFormId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(offset + 12, 4),
            replacement);
        File.WriteAllBytes(path, bytes);
    }

    private static void InsertFollowerFinishCellSubrecord(
        string path,
        string signature,
        byte[] payload)
    {
        byte[] source = File.ReadAllBytes(path);
        List<int> groups = [];
        (int record, List<int> ancestors) =
            FindFollowerFinishRawRecordWithGroups(
                source,
                0,
                source.Length,
                "CELL",
                0x717A,
                groups);
        int bodySize = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(
                source.AsSpan(record + 4, 4)));
        int insertAt = checked(record + 24 + bodySize);
        int delta = checked(6 + payload.Length);
        byte[] output = new byte[source.Length + delta];
        source.AsSpan(0, insertAt).CopyTo(output);
        Encoding.ASCII.GetBytes(signature)
            .CopyTo(output, insertAt);
        BinaryPrimitives.WriteUInt16LittleEndian(
            output.AsSpan(insertAt + 4, 2),
            checked((ushort)payload.Length));
        payload.CopyTo(output, insertAt + 6);
        source.AsSpan(insertAt).CopyTo(
            output.AsSpan(insertAt + delta));
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(record + 4, 4),
            checked((uint)(bodySize + delta)));
        foreach (int group in ancestors)
        {
            uint oldSize =
                BinaryPrimitives.ReadUInt32LittleEndian(
                    output.AsSpan(group + 4, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(group + 4, 4),
                checked(oldSize + (uint)delta));
        }
        File.WriteAllBytes(path, output);
    }

    private static (int Record, List<int> Ancestors)
        FindFollowerFinishRawRecordWithGroups(
            byte[] bytes,
            int start,
            int end,
            string signature,
            uint localFormId,
            List<int> ancestors)
    {
        int position = start;
        while (position < end)
        {
            string current = Encoding.ASCII.GetString(
                bytes,
                position,
                4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 4, 4));
            if (current == "GRUP")
            {
                var childAncestors = new List<int>(ancestors)
                {
                    position
                };
                try
                {
                    return FindFollowerFinishRawRecordWithGroups(
                        bytes,
                        position + 24,
                        checked(position + (int)size),
                        signature,
                        localFormId,
                        childAncestors);
                }
                catch (InvalidDataException)
                {
                    position = checked(position + (int)size);
                    continue;
                }
            }
            uint formId = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 12, 4));
            if (current == signature &&
                (formId & 0x00FF_FFFFu) == localFormId)
                return (position, ancestors);
            position = checked(position + 24 + (int)size);
        }
        throw new InvalidDataException(
            $"Raw {signature} 0x{localFormId:X8} is missing.");
    }

    private static void SetFollowerFinishFormKey(
        IMajorRecord record,
        FormKey formKey)
    {
        var property = record.GetType().GetProperty(
            nameof(IMajorRecord.FormKey));
        if (property is null || !property.CanWrite)
            throw new InvalidOperationException(
                "Hostile fixture FormKey is not writable.");
        property.SetValue(record, formKey);
    }

    private static void AssertFollowerFinishModelHostile(
        SkyrimFollowerFinishCoreFixture fixture,
        SkyrimFollowerFinishProposal proposal,
        SkyrimMod valid,
        BethesdaSkyrimFollowerFinishWorldVerifier verifier,
        string name,
        Action<SkyrimMod> mutate,
        string expectedCode)
    {
        var hostile = (SkyrimMod)valid.DeepCopy();
        mutate(hostile);
        string path = Path.Combine(
            fixture.Root,
            $"{name}.esp");
        WriteFollowerFinishCorePlugin(hostile, path);
        AssertFollowerFinishWorldRefused(
            verifier,
            path,
            proposal,
            expectedCode);
    }

    private static void AssertFollowerFinishWorldRefused(
        BethesdaSkyrimFollowerFinishWorldVerifier verifier,
        string path,
        SkyrimFollowerFinishProposal proposal,
        string expectedCode)
    {
        BethesdaSkyrimFollowerFinishWorldVerification result =
            verifier.Verify(
                new WorkspacePath(path),
                proposal,
                new P2Int(-42, 1));
        Assert(
            !result.Verified &&
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == expectedCode &&
                diagnostic.Severity ==
                DiagnosticSeverity.Error),
            $"World verifier accepted hostile '{Path.GetFileName(path)}', " +
            $"or omitted {expectedCode}: " +
            string.Join(
                " | ",
                result.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
    }
}
