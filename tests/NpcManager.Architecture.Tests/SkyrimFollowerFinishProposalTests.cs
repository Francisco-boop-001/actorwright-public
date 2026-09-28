using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static readonly Sha256Hash FollowerFinishRequestFileSha256 =
        new(new string('6', 64));

    private static readonly string[] ExpectedFollowerFinishRecordInventory =
    [
        "NPC_ 0x00000800",
        "CLFM 0x00000801",
        "TXST 0x00000802",
        "HDPT 0x00000803",
        "RELA 0x00000804"
    ];

    private static readonly string[] ExpectedFollowerFinishAbsentSignatures =
        ["PACK", "CELL", "WRLD", "ACHR", "REFR"];

    private static async Task TestSkyrimFollowerFinishProposalAnalysis()
    {
        await using var fixture =
            await SkyrimFollowerFinishProposalFixture.CreateAsync();

        FollowerFinishCase accepted = await fixture.CreateCaseAsync("accepted");
        SkyrimFollowerFinishProposalResult first =
            await fixture.Service.AnalyzeAsync(
                accepted.Request,
                FollowerFinishRequestFileSha256,
                accepted.ProposalPath,
                CancellationToken.None);
        Assert(first.Proposed &&
               first.Proposal is not null &&
               first.ProposalPath == accepted.ProposalPath &&
               first.ProposalSha256 is not null,
            "A complete loose-manifest plus no-wrapper source ZIP was not proposed: " +
            string.Join(
                " | ",
                first.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        SkyrimFollowerFinishProposal proposal = first.Proposal ??
            throw new InvalidOperationException(
                "Successful follower-finish analysis omitted its proposal.");
        Assert(proposal.SchemaVersion == 1 &&
               proposal.Operation == "skyrim-simple-follower-finish" &&
               proposal.RequestSha256 ==
                   FollowerFinishRequestFileSha256 &&
               proposal.ExistingRecordChanges.SequenceEqual(
                   accepted.Request.AllowedExistingRecordChanges) &&
               proposal.NewRecords.SequenceEqual(
                   accepted.Request.AllowedNewRecords) &&
               proposal.Request.ExternalAuthorities.Providers
                   .Select(provider => provider.Plugin.Value)
                   .SequenceEqual(["Skyrim.esm", "Update.esm"]) &&
               proposal.NextFormId == new FormId(0x808) &&
               !proposal.RuntimeAuthority,
            "The analyze-only proposal widened or lost the closed change surface.");
        Assert(proposal.SourceSnapshot.RecordInventory.SequenceEqual(
                   ExpectedFollowerFinishRecordInventory) &&
               proposal.SourceSnapshot.ActorSubrecordDigests.Length > 10 &&
               proposal.SourceSnapshot.ActorSubrecordDigests.All(value =>
                   value.Contains('=') &&
                   value[(value.IndexOf('=') + 1)..].Length == 64) &&
               proposal.SourceSnapshot.HairPackedRgb ==
               new SkyrimPackedRgb(0x94876A) &&
               proposal.SourceSnapshot.ActorHairColor ==
               new FormReference(
                   accepted.Request.Source.Plugin,
                   new FormId(0x801)) &&
               proposal.SourceSnapshot.DefaultOutfitNull &&
               proposal.SourceSnapshot.FactionRanks.SequenceEqual(
                   accepted.Request.ExpectedFactionRanks) &&
               proposal.SourceSnapshot.RelationshipRank == "Ally" &&
               proposal.SourceSnapshot.RelationshipRankRawDiscriminator ==
               1 &&
               proposal.SourceSnapshot.AbsentSignatures.SequenceEqual(
                   ExpectedFollowerFinishAbsentSignatures),
            "Typed and raw source-plugin inventory did not preserve the accepted old values.");
        Assert(!Directory.Exists(accepted.Request.OutputRoot.Value) &&
               !File.Exists(accepted.Request.OutputZip.Value),
            "Analyze wrote a package root or archive.");

        await AssertRefusedAsync(
            accepted.Request with
            {
                ExternalAuthorities =
                    accepted.Request.ExternalAuthorities with
                    {
                        PlacementEvidence =
                            accepted.Request.ExternalAuthorities
                                .PlacementEvidence with
                                {
                                    ByteLength =
                                        accepted.Request
                                            .ExternalAuthorities
                                            .PlacementEvidence
                                            .ByteLength + 1
                                }
                    }
            },
            new WorkspacePath(Path.Combine(
                accepted.Root,
                "placement-wrong-length-proposal.json")),
            "follower-finish-external-authority-length");

        await AssertRefusedAsync(
            accepted.Request with
            {
                ExternalAuthorities =
                    accepted.Request.ExternalAuthorities with
                    {
                        PlacementEvidence =
                            accepted.Request.ExternalAuthorities
                                .PlacementEvidence with
                                {
                                    Sha256 = new Sha256Hash(
                                        new string('A', 64))
                                }
                    }
            },
            new WorkspacePath(Path.Combine(
                accepted.Root,
                "placement-stale-hash-proposal.json")),
            "follower-finish-external-authority-hash");

        SkyrimFollowerFinishPluginProviderAuthority firstProvider =
            accepted.Request.ExternalAuthorities.Providers[0];
        await AssertRefusedAsync(
            accepted.Request with
            {
                ExternalAuthorities =
                    accepted.Request.ExternalAuthorities with
                    {
                        Providers = accepted.Request
                            .ExternalAuthorities
                            .Providers
                            .SetItem(
                                0,
                                firstProvider with
                                {
                                    ByteLength =
                                        firstProvider.ByteLength + 1
                                })
                    }
            },
            new WorkspacePath(Path.Combine(
                accepted.Root,
                "provider-wrong-length-proposal.json")),
            "follower-finish-external-authority-length");

        await AssertRefusedAsync(
            accepted.Request with
            {
                ExternalAuthorities =
                    accepted.Request.ExternalAuthorities with
                    {
                        Providers = accepted.Request
                            .ExternalAuthorities
                            .Providers
                            .SetItem(
                                0,
                                firstProvider with
                                {
                                    Sha256 = new Sha256Hash(
                                        new string('B', 64))
                                })
                    }
            },
            new WorkspacePath(Path.Combine(
                accepted.Root,
                "provider-stale-hash-proposal.json")),
            "follower-finish-external-authority-hash");

        FollowerFinishCase missingPlacement =
            await fixture.CreateCaseAsync("missing-placement-authority");
        File.Delete(
            missingPlacement.Request.ExternalAuthorities
                .PlacementEvidence.Path.Value);
        await AssertRefusedAsync(
            missingPlacement.Request,
            missingPlacement.ProposalPath,
            "follower-finish-external-authority-missing");

        FollowerFinishCase missingProvider =
            await fixture.CreateCaseAsync("missing-provider-authority");
        File.Delete(
            missingProvider.Request.ExternalAuthorities.Providers[0]
                .Path.Value);
        await AssertRefusedAsync(
            missingProvider.Request,
            missingProvider.ProposalPath,
            "follower-finish-external-authority-missing");

        await AssertRefusedAsync(
            accepted.Request,
            accepted.Request.ExternalAuthorities.Providers[0].Path,
            "follower-finish-proposal-path-overlap");

        FollowerFinishCase exactStream =
            await fixture.CreateCaseAsync("exact-admission-stream");
        byte[] admittedZipBytes =
            await File.ReadAllBytesAsync(
                exactStream.Request.Source.Zip.Value);
        byte[] hostileReplacement =
            RootFollowerFinishZipEntries(admittedZipBytes);
        int streamGeneration = 0;
        Stream OpenChangingSource(WorkspacePath _)
        {
            streamGeneration++;
            return streamGeneration == 1
                ? new DisposeActionMemoryStream(
                    admittedZipBytes,
                    () => File.WriteAllBytes(
                        exactStream.Request.Source.Zip.Value,
                        hostileReplacement))
                : new MemoryStream(
                    hostileReplacement,
                    writable: false);
        }
        SkyrimFollowerFinishService exactStreamService =
            fixture.CreateService(openZipStream: OpenChangingSource);
        SkyrimFollowerFinishProposalResult exactStreamResult =
            await exactStreamService.AnalyzeAsync(
                exactStream.Request,
                FollowerFinishRequestFileSha256,
                exactStream.ProposalPath,
                CancellationToken.None);
        Assert(exactStreamResult.Proposed,
            "Analysis did not parse the exact stream whose length and SHA-256 were admitted.");

        var loader = new SkyrimFollowerFinishRequestFileLoader(
            fixture.WorkspaceRoot);
        SkyrimFollowerFinishProposalLoadResult reopened =
            await loader.LoadProposalAsync(
                accepted.ProposalPath,
                first.ProposalSha256!.Value,
                CancellationToken.None);
        Assert(reopened.Loaded &&
               reopened.Proposal is not null &&
               reopened.ActualSha256 == first.ProposalSha256,
            "The returned proposal hash did not bind a strict canonical reopen.");

        var repeatedPath = new WorkspacePath(Path.Combine(
            accepted.Root,
            "deterministic-repeat-proposal.json"));
        SkyrimFollowerFinishProposalResult second =
            await fixture.Service.AnalyzeAsync(
                accepted.Request,
                FollowerFinishRequestFileSha256,
                repeatedPath,
                CancellationToken.None);
        byte[] repeatedBytes =
            await File.ReadAllBytesAsync(repeatedPath.Value);
        byte[] acceptedBytes =
            await File.ReadAllBytesAsync(accepted.ProposalPath.Value);
        Assert(second.Proposed &&
               second.ProposalSha256 == first.ProposalSha256 &&
               repeatedBytes.SequenceEqual(acceptedBytes),
            "Equivalent follower-finish requests did not persist byte-identical proposals.");

        FollowerFinishCase reopenFailure =
            await fixture.CreateCaseAsync("proposal-reopen-failure");
        string retainedPackage = Path.Combine(
            reopenFailure.Root,
            "preexisting-package-state");
        Directory.CreateDirectory(retainedPackage);
        string retainedPackageFile = Path.Combine(
            retainedPackage,
            "sentinel.bin");
        await File.WriteAllBytesAsync(
            retainedPackageFile,
            [0x10, 0x20]);
        string retainedArchive = Path.Combine(
            reopenFailure.Root,
            "preexisting-archive-state.zip");
        await File.WriteAllBytesAsync(
            retainedArchive,
            [0x30, 0x40]);
        string retainedOutput = Path.Combine(
            reopenFailure.Root,
            "preexisting-output-state.bin");
        await File.WriteAllBytesAsync(
            retainedOutput,
            [0x50, 0x60]);
        byte[] sourceZipBefore = await File.ReadAllBytesAsync(
            reopenFailure.Request.Source.Zip.Value);
        byte[] sourceManifestBefore = await File.ReadAllBytesAsync(
            reopenFailure.Request.Source.PackageManifest.Value);
        SkyrimFollowerFinishService reopenFailingService =
            fixture.CreateService(new ReopenFailingFollowerFinishLoader());
        bool reopenFailureObserved = false;
        try
        {
            await reopenFailingService.AnalyzeAsync(
                reopenFailure.Request,
                FollowerFinishRequestFileSha256,
                reopenFailure.ProposalPath,
                CancellationToken.None);
        }
        catch (NotSupportedException exception) when (
            exception.Message == ReopenFailingFollowerFinishLoader.Message)
        {
            reopenFailureObserved = true;
        }
        string retainedPackageAfter = Convert.ToHexString(
            await File.ReadAllBytesAsync(retainedPackageFile));
        string retainedArchiveAfter = Convert.ToHexString(
            await File.ReadAllBytesAsync(retainedArchive));
        string retainedOutputAfter = Convert.ToHexString(
            await File.ReadAllBytesAsync(retainedOutput));
        byte[] sourceZipAfter = await File.ReadAllBytesAsync(
            reopenFailure.Request.Source.Zip.Value);
        byte[] sourceManifestAfter = await File.ReadAllBytesAsync(
            reopenFailure.Request.Source.PackageManifest.Value);
        Assert(reopenFailureObserved &&
               !File.Exists(reopenFailure.ProposalPath.Value) &&
               !Directory.Exists(
                   reopenFailure.Request.OutputRoot.Value) &&
               !File.Exists(reopenFailure.Request.OutputZip.Value) &&
               retainedPackageAfter == "1020" &&
               retainedArchiveAfter == "3040" &&
               retainedOutputAfter == "5060" &&
               sourceZipAfter.SequenceEqual(sourceZipBefore) &&
               sourceManifestAfter.SequenceEqual(sourceManifestBefore),
            "A proposal-reopen exception did not remove only the newly created proposal.");

        await AssertRefusedAsync(
            accepted.Request with
            {
                Source = accepted.Request.Source with
                {
                    ZipSha256 = new Sha256Hash(new string('A', 64))
                }
            },
            new WorkspacePath(Path.Combine(
                accepted.Root,
                "stale-zip-hash-proposal.json")),
            "follower-finish-source-zip-hash");

        await AssertRefusedAsync(
            accepted.Request with
            {
                Source = accepted.Request.Source with
                {
                    PackageManifestSha256 =
                        new Sha256Hash(new string('B', 64))
                }
            },
            new WorkspacePath(Path.Combine(
                accepted.Root,
                "stale-manifest-hash-proposal.json")),
            "follower-finish-source-manifest-hash");

        foreach ((string Name, Action<List<FollowerFinishZipEntry>> Mutate)
                 in new (string,
                     Action<List<FollowerFinishZipEntry>>)[]
                 {
                     ("unsafe-traversal", entries =>
                         entries.Add(new("../escape.bin", [1]))),
                     ("unsafe-ads", entries =>
                         entries.Add(new("escape.bin:stream", [1]))),
                     ("unsafe-rooted", entries =>
                         entries.Add(new("/escape.bin", [1]))),
                     ("duplicate-case", entries =>
                         entries.Add(new(
                             entries[0].Name.ToUpperInvariant(),
                             entries[0].Bytes))),
                     ("wrapper-directory", entries =>
                     {
                         for (int index = 0; index < entries.Count; index++)
                             entries[index] = entries[index] with
                             {
                                 Name = "wrapper/" + entries[index].Name
                             };
                     }),
                     ("undeclared-entry", entries =>
                         entries.Add(new("undeclared.bin", [1, 2, 3]))),
                     ("missing-plugin", entries =>
                         entries.RemoveAll(entry =>
                             entry.Name.EndsWith(
                                 ".esp",
                                 StringComparison.OrdinalIgnoreCase))),
                     ("multiple-plugins", entries =>
                         entries.Add(new(
                             "OtherFollower.esp",
                             entries.Single(entry =>
                                 entry.Name.EndsWith(
                                     ".esp",
                                     StringComparison.OrdinalIgnoreCase))
                                 .Bytes)))
                 })
        {
            FollowerFinishCase hostile = await fixture.CreateCaseAsync(
                Name,
                new FollowerFinishCaseOptions(ZipMutation: Mutate));
            await AssertRefusedAsync(
                hostile.Request,
                hostile.ProposalPath,
                "follower-finish-source-zip");
        }

        FollowerFinishCase manifestMismatch =
            await fixture.CreateCaseAsync(
                "manifest-identity-mismatch",
                new FollowerFinishCaseOptions(
                    ManifestMutation: manifest =>
                        manifest["outputPlugin"] = "OtherFollower.esp"));
        await AssertRefusedAsync(
            manifestMismatch.Request,
            manifestMismatch.ProposalPath,
            "follower-finish-source-manifest-identity");

        foreach ((string Name,
                     Func<SkyrimFollowerFinishSourceAuthority,
                         SkyrimFollowerFinishSourceAuthority> Mutate,
                     string DiagnosticCode)
                 in new (string Name,
                     Func<SkyrimFollowerFinishSourceAuthority,
                         SkyrimFollowerFinishSourceAuthority> Mutate,
                     string DiagnosticCode)[]
                 {
                     (
                         "stale-plugin",
                         source => source with
                         {
                             PluginSha256 =
                                 new Sha256Hash(new string('C', 64))
                         },
                         "follower-finish-source-plugin-hash"
                     ),
                     (
                         "stale-facegeom",
                         source => source with
                         {
                             FaceGeomSha256 =
                                 new Sha256Hash(new string('D', 64))
                         },
                         "follower-finish-source-facegeom-hash"
                     ),
                     (
                         "stale-facetint",
                         source => source with
                         {
                             FaceTintSha256 =
                                 new Sha256Hash(new string('E', 64))
                         },
                         "follower-finish-source-facetint-hash"
                     )
                 })
        {
            await AssertRefusedAsync(
                accepted.Request with
                {
                    Source = Mutate(accepted.Request.Source)
                },
                new WorkspacePath(Path.Combine(
                    accepted.Root,
                    $"{Name}-proposal.json")),
                DiagnosticCode);
        }

        (string Signature, uint FormId)[] requiredRecords =
        [
            ("NPC_", 0x800),
            ("CLFM", 0x801),
            ("TXST", 0x802),
            ("HDPT", 0x803),
            ("RELA", 0x804)
        ];
        foreach ((string signature, uint formId) in requiredRecords)
        {
            FollowerFinishCase missing = await fixture.CreateCaseAsync(
                $"missing-{signature.TrimEnd('_').ToLowerInvariant()}",
                new FollowerFinishCaseOptions(
                    PluginMutation: bytes => MutateRecordFormId(
                        bytes,
                        signature,
                        formId,
                        0x900 + (formId - 0x800))));
            await AssertRefusedAsync(
                missing.Request,
                missing.ProposalPath,
                "follower-finish-source-plugin");
        }

        FollowerFinishCase wrongNpc = await fixture.CreateCaseAsync(
            "wrong-npc-form-id",
            new FollowerFinishCaseOptions(
                PluginMutation: bytes => MutateRecordFormId(
                    bytes, "NPC_", 0x800, 0x899)));
        await AssertRefusedAsync(
            wrongNpc.Request,
            wrongNpc.ProposalPath,
            "follower-finish-source-plugin");

        FollowerFinishCase extraRecord = await fixture.CreateCaseAsync(
            "extra-self-owned-record",
            new FollowerFinishCaseOptions(
                PluginMutation: bytes => AppendTopLevelRecordClone(
                    bytes, "CLFM", 0x801, "CLFM", 0x805)));
        await AssertRefusedAsync(
            extraRecord.Request,
            extraRecord.ProposalPath,
            "follower-finish-source-plugin");

        FollowerFinishCase wrongHair = await fixture.CreateCaseAsync(
            "wrong-old-hair",
            new FollowerFinishCaseOptions(
                PluginMutation: bytes => MutateSubrecord(
                    bytes,
                    "CLFM",
                    0x801,
                    "CNAM",
                    payload =>
                    {
                        payload[0] = 0xD6;
                        payload[1] = 0xBE;
                        payload[2] = 0x83;
                    })));
        await AssertRefusedAsync(
            wrongHair.Request,
            wrongHair.ProposalPath,
            "follower-finish-source-plugin");

        FollowerFinishCase changedHclf = await fixture.CreateCaseAsync(
            "changed-npc-hclf",
            new FollowerFinishCaseOptions(
                PluginMutation: bytes => MutateSubrecord(
                    bytes,
                    "NPC_",
                    0x800,
                    "HCLF",
                    payload =>
                    {
                        payload[0] = 0x02;
                        payload[1] = 0x08;
                    })));
        await AssertRefusedAsync(
            changedHclf.Request,
            changedHclf.ProposalPath,
            "follower-finish-source-plugin");

        FollowerFinishCase nonNullOutfit = await fixture.CreateCaseAsync(
            "non-null-outfit",
            new FollowerFinishCaseOptions(
                PluginMutation: bytes => AppendNpcSubrecord(
                    bytes,
                    "DOFT",
                    [0x7C, 0xD6, 0x0F, 0x00])));
        await AssertRefusedAsync(
            nonNullOutfit.Request,
            nonNullOutfit.ProposalPath,
            "follower-finish-source-plugin");

        foreach (string signature in new[] { "PACK", "CELL", "WRLD" })
        {
            FollowerFinishCase forbiddenRecord =
                await fixture.CreateCaseAsync(
                    $"existing-{signature.ToLowerInvariant()}",
                    new FollowerFinishCaseOptions(
                        PluginMutation: bytes =>
                            AppendTopLevelRecordClone(
                                bytes,
                                "CLFM",
                                0x801,
                                signature,
                                (uint)(0x900 + signature[0]))));
            await AssertRefusedAsync(
                forbiddenRecord.Request,
                forbiddenRecord.ProposalPath,
                "follower-finish-source-plugin");
        }

        FollowerFinishCase outputCollision =
            await fixture.CreateCaseAsync("output-collision");
        Directory.CreateDirectory(outputCollision.Request.OutputRoot.Value);
        await AssertRefusedAsync(
            outputCollision.Request,
            outputCollision.ProposalPath,
            "follower-finish-output-collision");

        FollowerFinishCase archiveCollision =
            await fixture.CreateCaseAsync("archive-collision");
        await File.WriteAllBytesAsync(
            archiveCollision.Request.OutputZip.Value,
            [1]);
        await AssertRefusedAsync(
            archiveCollision.Request,
            archiveCollision.ProposalPath,
            "follower-finish-output-collision");

        FollowerFinishCase proposalCollision =
            await fixture.CreateCaseAsync("proposal-collision");
        await File.WriteAllTextAsync(
            proposalCollision.ProposalPath.Value,
            "do-not-overwrite");
        await AssertRefusedAsync(
            proposalCollision.Request,
            proposalCollision.ProposalPath,
            "follower-finish-proposal-collision");
        Assert(await File.ReadAllTextAsync(
                   proposalCollision.ProposalPath.Value) ==
               "do-not-overwrite",
            "Analyze overwrote an existing proposal.");

        Assert(!Directory
                .EnumerateDirectories(
                    fixture.Root,
                    ".npcmanager-follower-finish-analysis-*",
                    SearchOption.AllDirectories)
                .Any(),
            "A successful or refused analysis leaked its GUID-owned scratch directory.");

        async Task AssertRefusedAsync(
            SkyrimFollowerFinishRequest request,
            WorkspacePath proposalPath,
            string diagnosticPrefix)
        {
            bool outputRootExisted =
                Directory.Exists(request.OutputRoot.Value);
            bool outputZipExisted =
                File.Exists(request.OutputZip.Value);
            byte[]? outputZipBytes = outputZipExisted
                ? await File.ReadAllBytesAsync(request.OutputZip.Value)
                : null;
            SkyrimFollowerFinishProposalResult result =
                await fixture.Service.AnalyzeAsync(
                    request,
                    FollowerFinishRequestFileSha256,
                    proposalPath,
                    CancellationToken.None);
            Assert(!result.Proposed &&
                   result.Proposal is null &&
                   result.ProposalSha256 is null &&
                   result.Diagnostics.Any(diagnostic =>
                       diagnostic.Severity == DiagnosticSeverity.Error &&
                       diagnostic.Code.StartsWith(
                       diagnosticPrefix,
                       StringComparison.Ordinal)),
                $"Hostile case '{Path.GetFileName(proposalPath.Value)}' was not refused with {diagnosticPrefix}.");
            bool outputZipUnchanged = true;
            if (outputZipBytes is not null)
            {
                byte[] actualOutputZipBytes =
                    await File.ReadAllBytesAsync(request.OutputZip.Value);
                outputZipUnchanged =
                    outputZipBytes.SequenceEqual(actualOutputZipBytes);
            }
            Assert(
                Directory.Exists(request.OutputRoot.Value) ==
                    outputRootExisted &&
                File.Exists(request.OutputZip.Value) ==
                    outputZipExisted &&
                outputZipUnchanged,
                "A refused analysis created, removed, or changed a package root or archive.");
        }
    }

    private sealed class SkyrimFollowerFinishProposalFixture :
        IAsyncDisposable
    {
        private static readonly DateTimeOffset ZipTimestamp =
            new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly byte[] sourcePlugin;
        private readonly byte[] faceGeom = [0x4E, 0x49, 0x46, 0x01];
        private readonly byte[] faceTint = [0x44, 0x44, 0x53, 0x20];

        private SkyrimFollowerFinishProposalFixture(
            WorkspacePath workspaceRoot,
            string root,
            byte[] sourcePlugin,
            SkyrimFollowerFinishService service)
        {
            WorkspaceRoot = workspaceRoot;
            Root = root;
            this.sourcePlugin = sourcePlugin;
            Service = service;
        }

        public WorkspacePath WorkspaceRoot { get; }

        public string Root { get; }

        public SkyrimFollowerFinishService Service { get; }

        public SkyrimFollowerFinishService CreateService(
            ISkyrimFollowerFinishRequestFileLoader? loader = null,
            Func<WorkspacePath, Stream>? openZipStream = null)
        {
            var policy = new KOnlyWorkspacePolicy(
                WorkspaceRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var manifestReader =
                new PackageManifestReader(policy, WorkspaceRoot);
            var verifier = new PackageVerifyService(manifestReader);
            var pluginReader =
                new BethesdaSkyrimFollowerFinishSourceReader();
            SkyrimFollowerFinishSourcePackageReader sourceReader =
                openZipStream is null
                    ? new SkyrimFollowerFinishSourcePackageReader(
                        WorkspaceRoot,
                        policy,
                        manifestReader,
                        verifier,
                        pluginReader)
                    : new SkyrimFollowerFinishSourcePackageReader(
                        WorkspaceRoot,
                        policy,
                        manifestReader,
                        verifier,
                        pluginReader,
                        openZipStream);
            var archive = new PackageArchiveService(
                verifier,
                policy,
                WorkspaceRoot);
            return new SkyrimFollowerFinishService(
                sourceReader.InspectAsync,
                loader ??
                new SkyrimFollowerFinishRequestFileLoader(
                    WorkspaceRoot),
                verifier,
                policy,
                archive,
                WorkspaceRoot);
        }

        public static async Task<SkyrimFollowerFinishProposalFixture>
            CreateAsync()
        {
            var workspaceRoot =
                new WorkspacePath(@"K:\ExampleWorkspace");
            string root = Path.Combine(
                workspaceRoot.Value,
                "projects",
                "NpcManagerReimplementation",
                "03-builds",
                "work",
                "follower-finish-proposal-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            byte[] sourcePlugin = await ReadAcceptedSourcePluginAsync(
                workspaceRoot);
            var policy = new KOnlyWorkspacePolicy(
                workspaceRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var manifestReader =
                new PackageManifestReader(policy, workspaceRoot);
            var verifier = new PackageVerifyService(manifestReader);
            var pluginReader =
                new BethesdaSkyrimFollowerFinishSourceReader();
            var sourceReader =
                new SkyrimFollowerFinishSourcePackageReader(
                    workspaceRoot,
                    policy,
                    manifestReader,
                    verifier,
                    pluginReader);
            var loader =
                new SkyrimFollowerFinishRequestFileLoader(workspaceRoot);
            var archive =
                new PackageArchiveService(
                    verifier,
                    policy,
                    workspaceRoot);
            var service = new SkyrimFollowerFinishService(
                sourceReader.InspectAsync,
                loader,
                verifier,
                policy,
                archive,
                workspaceRoot);
            return new SkyrimFollowerFinishProposalFixture(
                workspaceRoot,
                root,
                sourcePlugin,
                service);
        }

        public async Task<FollowerFinishCase> CreateCaseAsync(
            string name,
            FollowerFinishCaseOptions? options = null)
        {
            options ??= new FollowerFinishCaseOptions();
            string caseRoot = Path.Combine(Root, name);
            Directory.CreateDirectory(caseRoot);
            byte[] plugin = options.PluginMutation is null
                ? sourcePlugin.ToArray()
                : options.PluginMutation(sourcePlugin.ToArray());
            string pluginPath = "BrigitteBardotNpcManager.esp";
            string faceGeomPath =
                "meshes/actors/character/FaceGenData/FaceGeom/" +
                "BrigitteBardotNpcManager.esp/00000800.nif";
            string faceTintPath =
                "textures/actors/character/FaceGenData/FaceTint/" +
                "BrigitteBardotNpcManager.esp/00000800.dds";
            var entries = new List<FollowerFinishZipEntry>
            {
                new(pluginPath, plugin),
                new(faceGeomPath, faceGeom),
                new(faceTintPath, faceTint),
                new("BUILD_INFO.txt", Encoding.UTF8.GetBytes("build")),
                new(
                    "README-NPCMANAGER-RUNTIME-TEST.txt",
                    Encoding.UTF8.GetBytes("readme")),
                new(
                    "RUNTIME-TEST-INSTRUCTIONS.md",
                    Encoding.UTF8.GetBytes("runtime"))
            };
            options.ZipMutation?.Invoke(entries);

            JsonObject manifest = CreateManifest(
                plugin,
                faceGeom,
                faceTint);
            options.ManifestMutation?.Invoke(manifest);
            string manifestPath = Path.Combine(
                caseRoot,
                "source-package-manifest.json");
            byte[] manifestBytes =
                JsonSerializer.SerializeToUtf8Bytes(manifest);
            await File.WriteAllBytesAsync(manifestPath, manifestBytes);

            string zipPath = Path.Combine(caseRoot, "source.zip");
            await using (var file = new FileStream(
                             zipPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None))
            {
                using var zip = new ZipArchive(
                    file,
                    ZipArchiveMode.Create,
                    leaveOpen: true);
                foreach (FollowerFinishZipEntry item in entries)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(
                        item.Name,
                        CompressionLevel.Optimal);
                    entry.LastWriteTime = ZipTimestamp;
                    await using Stream destination = entry.Open();
                    await destination.WriteAsync(item.Bytes);
                }
            }

            byte[] zipBytes = await File.ReadAllBytesAsync(zipPath);
            SkyrimFollowerFinishExternalAuthorities externalAuthorities =
                await CreateExternalAuthoritiesAsync(caseRoot);
            var pluginName =
                new PluginName("BrigitteBardotNpcManager.esp");
            var request = new SkyrimFollowerFinishRequest(
                1,
                "skyrim-simple-follower-finish",
                new SkyrimFollowerFinishSourceAuthority(
                    new WorkspacePath(zipPath),
                    zipBytes.LongLength,
                    HashFollowerFinishBytes(zipBytes),
                    new WorkspacePath(manifestPath),
                    HashFollowerFinishBytes(manifestBytes),
                    pluginName,
                    HashFollowerFinishBytes(plugin),
                    HashFollowerFinishBytes(faceGeom),
                    HashFollowerFinishBytes(faceTint)),
                new EditorId("BrigitteBardotNpcManager"),
                new FormId(0x800),
                [
                    new FormId(0x800),
                    new FormId(0x801),
                    new FormId(0x802),
                    new FormId(0x803),
                    new FormId(0x804)
                ],
                new FormReference(
                    new PluginName("COR_AllRace.esp"),
                    new FormId(0x5A184)),
                "inherited-cotr",
                true,
                [
                    new NpcFactionEntry(
                        new FormReference(
                            new PluginName("Skyrim.esm"),
                            new FormId(0x5C84D)),
                        0),
                    new NpcFactionEntry(
                        new FormReference(
                            new PluginName("Skyrim.esm"),
                            new FormId(0x5C84E)),
                        -1)
                ],
                new FormId(0x804),
                "Ally",
                1,
                new SkyrimFollowerFinishHairChange(
                    new FormId(0x801),
                    new SkyrimPackedRgb(0x94876A),
                    new SkyrimPackedRgb(0xD6BE83)),
                true,
                false,
                new SkyrimFollowerFinishSandbox(
                    "bounded-exterior-sandbox",
                    768,
                    "continuous",
                    new FormReference(pluginName, new FormId(0x806)),
                    "GetFactionRank(Skyrim.esm|0x0005C84E) < 0"),
                new SkyrimFollowerFinishPlacement(
                    new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x3C)),
                    new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0xA16A)),
                    new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x3B)),
                    new SkyrimExteriorTransform(1, 2, 3, 0, 0, 90),
                    new SkyrimExteriorTransform(4, 5, 6, 0, 0, 0)),
                SkyrimFollowerFinishAllocation.SimpleFollowerV1,
                [
                    "PACK 0x00000805",
                    "REFR 0x00000806",
                    "ACHR 0x00000807"
                ],
                [
                    "TES4: set ESL flag and mechanical header metadata",
                    "CLFM 0x00000801: 0x94876A -> 0xD6BE83",
                    "NPC_ 0x00000800: add PKID 0x00000805"
                ],
                [
                    new AssetPath(
                        "Data/BrigitteBardotNpcManager.esp"),
                    new AssetPath(
                        "Data/meshes/actors/character/facegendata/" +
                        "facegeom/BrigitteBardotNpcManager.esp/" +
                        "00000800.nif"),
                    new AssetPath(
                        "Data/textures/actors/character/facegendata/" +
                        "facetint/BrigitteBardotNpcManager.esp/" +
                        "00000800.dds"),
                    new AssetPath("evidence/source.json"),
                    new AssetPath("npcmanager-package.json")
                ],
                new WorkspacePath(Path.Combine(caseRoot, "candidate")),
                new WorkspacePath(Path.Combine(caseRoot, "candidate.zip")),
                "A peaceful blonde follower keeps vigil near Markarth Stables.",
                externalAuthorities);
            return new FollowerFinishCase(
                caseRoot,
                request,
                new WorkspacePath(
                    Path.Combine(caseRoot, "proposal.json")));
        }

        private static async Task<SkyrimFollowerFinishExternalAuthorities>
            CreateExternalAuthoritiesAsync(string caseRoot)
        {
            byte[] placementBytes = Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1}");
            byte[] skyrimBytes =
                [0x53, 0x4B, 0x59, 0x52, 0x49, 0x4D];
            byte[] updateBytes =
                [0x55, 0x50, 0x44, 0x41, 0x54, 0x45];
            string placementPath = Path.Combine(
                caseRoot,
                "placement-evidence.json");
            string providerRoot = Path.Combine(caseRoot, "providers");
            Directory.CreateDirectory(providerRoot);
            string skyrimPath = Path.Combine(
                providerRoot,
                "Skyrim.esm");
            string updatePath = Path.Combine(
                providerRoot,
                "Update.esm");
            await File.WriteAllBytesAsync(
                placementPath,
                placementBytes);
            await File.WriteAllBytesAsync(skyrimPath, skyrimBytes);
            await File.WriteAllBytesAsync(updatePath, updateBytes);
            return new SkyrimFollowerFinishExternalAuthorities(
                new SkyrimFollowerFinishFileAuthority(
                    new WorkspacePath(placementPath),
                    placementBytes.LongLength,
                    HashFollowerFinishBytes(placementBytes)),
                [
                    new SkyrimFollowerFinishPluginProviderAuthority(
                        new PluginName("Update.esm"),
                        new WorkspacePath(updatePath),
                        updateBytes.LongLength,
                        HashFollowerFinishBytes(updateBytes)),
                    new SkyrimFollowerFinishPluginProviderAuthority(
                        new PluginName("Skyrim.esm"),
                        new WorkspacePath(skyrimPath),
                        skyrimBytes.LongLength,
                        HashFollowerFinishBytes(skyrimBytes))
                ]);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static async Task<byte[]> ReadAcceptedSourcePluginAsync(
            WorkspacePath workspaceRoot)
        {
            string sourceArchive = Path.Combine(
                workspaceRoot.Value,
                "projects",
                "NpcManagerReimplementation",
                "04-packages",
                "NpcManagerReimplementation-Brigitte-Bardot-" +
                "v0.1-slot0-hardened-runtime-test-reviewed.zip");
            await using var stream = File.OpenRead(sourceArchive);
            using var zip = new ZipArchive(
                stream,
                ZipArchiveMode.Read,
                leaveOpen: false);
            ZipArchiveEntry entry = zip.GetEntry(
                "BrigitteBardotNpcManager.esp") ??
                throw new InvalidDataException(
                    "The accepted Brigitte source ZIP omitted its plugin.");
            await using Stream content = entry.Open();
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory);
            return memory.ToArray();
        }

        private static JsonObject CreateManifest(
            byte[] plugin,
            byte[] faceGeom,
            byte[] faceTint) =>
            new()
            {
                ["schemaVersion"] = 1,
                ["edition"] = "skyrimse",
                ["presetFormat"] =
                    "blank-npc-creation-proposal",
                ["sourcePreset"] = "evidence/source.json",
                ["sourcePresetSha256"] = new string('1', 64),
                ["sourcePlugin"] = @"K:\source\carrier.esp",
                ["sourcePluginSha256"] = new string('2', 64),
                ["outputPlugin"] =
                    "BrigitteBardotNpcManager.esp",
                ["targetFormId"] = "0x00000800",
                ["artifacts"] = new JsonArray(
                    Artifact(
                        "plugin",
                        "Data/BrigitteBardotNpcManager.esp",
                        plugin),
                    Artifact(
                        "facegeom",
                        "Data/meshes/actors/character/FaceGenData/" +
                        "FaceGeom/BrigitteBardotNpcManager.esp/" +
                        "00000800.nif",
                        faceGeom),
                    Artifact(
                        "facetint",
                        "Data/textures/actors/character/FaceGenData/" +
                        "FaceTint/BrigitteBardotNpcManager.esp/" +
                        "00000800.dds",
                        faceTint),
                    Artifact(
                        "source-preset",
                        "evidence/source.json",
                        Encoding.UTF8.GetBytes("{}")))
            };

        private static JsonObject Artifact(
            string kind,
            string path,
            byte[] bytes) =>
            new()
            {
                ["kind"] = kind,
                ["relativePath"] = path,
                ["byteLength"] = bytes.LongLength,
                ["sha256"] =
                    HashFollowerFinishBytes(bytes).Value.ToLowerInvariant()
            };
    }

    private sealed record FollowerFinishCase(
        string Root,
        SkyrimFollowerFinishRequest Request,
        WorkspacePath ProposalPath);

    private sealed record FollowerFinishCaseOptions(
        Func<byte[], byte[]>? PluginMutation = null,
        Action<List<FollowerFinishZipEntry>>? ZipMutation = null,
        Action<JsonObject>? ManifestMutation = null);

    private sealed record FollowerFinishZipEntry(
        string Name,
        byte[] Bytes);

    private sealed class ReopenFailingFollowerFinishLoader :
        ISkyrimFollowerFinishRequestFileLoader
    {
        public const string Message =
            "Injected proposal reopen failure after CreateNew.";

        public ValueTask<SkyrimFollowerFinishRequestLoadResult>
            LoadRequestAsync(
                WorkspacePath path,
                Sha256Hash expectedSha256,
                CancellationToken cancellationToken,
                SkyrimFollowerFinishDocumentLoadMode mode =
                    SkyrimFollowerFinishDocumentLoadMode.PreWrite) =>
            throw new NotSupportedException(
                "Request loading is not used by this regression test.");

        public ValueTask<SkyrimFollowerFinishProposalLoadResult>
            LoadProposalAsync(
                WorkspacePath path,
                Sha256Hash expectedSha256,
                CancellationToken cancellationToken,
                SkyrimFollowerFinishDocumentLoadMode mode =
                    SkyrimFollowerFinishDocumentLoadMode.PreWrite)
        {
            if (!File.Exists(path.Value))
                throw new InvalidOperationException(
                    "Analyze did not create the proposal before reopen.");
            throw new NotSupportedException(Message);
        }
    }

    private sealed class DisposeActionMemoryStream(
        byte[] bytes,
        Action onDispose) :
        MemoryStream(bytes, writable: false)
    {
        private bool actionCompleted;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !actionCompleted)
            {
                actionCompleted = true;
                onDispose();
            }
            base.Dispose(disposing);
        }
    }

    private static Sha256Hash HashFollowerFinishBytes(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static byte[] RootFollowerFinishZipEntries(byte[] source)
    {
        byte[] result = source.ToArray();
        byte[] pluginName =
            Encoding.ASCII.GetBytes("BrigitteBardotNpcManager.esp");
        int replacements = 0;
        for (int index = 0;
             index <= result.Length - pluginName.Length;
             index++)
        {
            if (!result.AsSpan(
                    index,
                    pluginName.Length).SequenceEqual(pluginName))
                continue;
            result[index] = (byte)'/';
            replacements++;
        }
        if (replacements < 2)
            throw new InvalidDataException(
                "The test ZIP omitted local/central root-plugin names.");
        return result;
    }

    private static byte[] MutateRecordFormId(
        byte[] source,
        string signature,
        uint oldLocalFormId,
        uint newLocalFormId)
    {
        byte[] bytes = source.ToArray();
        RawRecordLocation location = FindRawRecord(
            bytes,
            signature,
            oldLocalFormId);
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(location.Offset + 12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(location.Offset + 12, 4),
            (raw & 0xFF00_0000) | newLocalFormId);
        return bytes;
    }

    private static byte[] MutateSubrecord(
        byte[] source,
        string recordSignature,
        uint localFormId,
        string subrecordSignature,
        Action<Span<byte>> mutation)
    {
        byte[] bytes = source.ToArray();
        RawRecordLocation location = FindRawRecord(
            bytes,
            recordSignature,
            localFormId);
        int position = location.Offset + 24;
        int end = location.Offset + location.Length;
        while (position < end)
        {
            if (position + 6 > end)
                throw new InvalidDataException(
                    "Test fixture subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(
                bytes,
                position,
                4);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            position += 6;
            if (position + length > end)
                throw new InvalidDataException(
                    "Test fixture subrecord exceeds its record.");
            if (signature == subrecordSignature)
            {
                mutation(bytes.AsSpan(position, length));
                return bytes;
            }
            position += length;
        }
        throw new InvalidDataException(
            $"Test fixture omitted {recordSignature}.{subrecordSignature}.");
    }

    private static byte[] AppendNpcSubrecord(
        byte[] source,
        string signature,
        byte[] payload)
    {
        RawRecordLocation npc = FindRawRecord(
            source,
            "NPC_",
            0x800);
        int insertion = npc.Offset + npc.Length;
        byte[] subrecord = new byte[6 + payload.Length];
        Encoding.ASCII.GetBytes(signature).CopyTo(subrecord, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            subrecord.AsSpan(4, 2),
            checked((ushort)payload.Length));
        payload.CopyTo(subrecord, 6);
        byte[] bytes = InsertBytes(source, insertion, subrecord);
        uint oldPayload = BinaryPrimitives.ReadUInt32LittleEndian(
            source.AsSpan(npc.Offset + 4, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(npc.Offset + 4, 4),
            checked(oldPayload + (uint)subrecord.Length));
        foreach (int groupOffset in npc.GroupOffsets)
        {
            uint oldSize = BinaryPrimitives.ReadUInt32LittleEndian(
                source.AsSpan(groupOffset + 4, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(groupOffset + 4, 4),
                checked(oldSize + (uint)subrecord.Length));
        }
        return bytes;
    }

    private static byte[] AppendTopLevelRecordClone(
        byte[] source,
        string sourceSignature,
        uint sourceLocalFormId,
        string newSignature,
        uint newLocalFormId)
    {
        RawRecordLocation original = FindRawRecord(
            source,
            sourceSignature,
            sourceLocalFormId);
        byte[] clone = source
            .AsSpan(original.Offset, original.Length)
            .ToArray();
        Encoding.ASCII.GetBytes(newSignature).CopyTo(clone, 0);
        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(
            clone.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            clone.AsSpan(12, 4),
            (raw & 0xFF00_0000) | newLocalFormId);
        byte[] group = new byte[24 + clone.Length];
        Encoding.ASCII.GetBytes("GRUP").CopyTo(group, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            group.AsSpan(4, 4),
            checked((uint)group.Length));
        Encoding.ASCII.GetBytes(newSignature).CopyTo(group, 8);
        clone.CopyTo(group, 24);
        byte[] bytes = new byte[source.Length + group.Length];
        source.CopyTo(bytes, 0);
        group.CopyTo(bytes, source.Length);
        int hedr = FindAscii(bytes, "HEDR");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(hedr + 10, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(hedr + 10, 4),
            count + 1);
        return bytes;
    }

    private static RawRecordLocation FindRawRecord(
        byte[] bytes,
        string signature,
        uint localFormId)
    {
        if (bytes.Length < 24 ||
            Encoding.ASCII.GetString(bytes, 0, 4) != "TES4")
            throw new InvalidDataException(
                "Test source does not begin with TES4.");
        int start = checked(
            24 +
            (int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(4, 4)));
        RawRecordLocation? found = Walk(
            start,
            bytes.Length,
            ImmutableArray<int>.Empty);
        return found ??
            throw new InvalidDataException(
                $"Test source omitted {signature} 0x{localFormId:X}.");

        RawRecordLocation? Walk(
            int containerStart,
            int containerEnd,
            ImmutableArray<int> groups)
        {
            int position = containerStart;
            while (position < containerEnd)
            {
                string current = Encoding.ASCII.GetString(
                    bytes,
                    position,
                    4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                if (current == "GRUP")
                {
                    int end = checked(position + (int)size);
                    RawRecordLocation? nested = Walk(
                        position + 24,
                        end,
                        groups.Add(position));
                    if (nested is not null)
                        return nested;
                    position = end;
                    continue;
                }
                int length = checked(24 + (int)size);
                uint rawFormId =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 12, 4));
                if (current == signature &&
                    (rawFormId & 0x00FF_FFFF) == localFormId)
                    return new RawRecordLocation(
                        position,
                        length,
                        groups);
                position += length;
            }
            return null;
        }
    }

    private static byte[] InsertBytes(
        byte[] source,
        int offset,
        byte[] inserted)
    {
        byte[] result = new byte[source.Length + inserted.Length];
        source.AsSpan(0, offset).CopyTo(result);
        inserted.CopyTo(result, offset);
        source.AsSpan(offset).CopyTo(
            result.AsSpan(offset + inserted.Length));
        return result;
    }

    private static int FindAscii(byte[] bytes, string value)
    {
        byte[] needle = Encoding.ASCII.GetBytes(value);
        for (int index = 0;
             index <= bytes.Length - needle.Length;
             index++)
        {
            if (bytes.AsSpan(index, needle.Length).SequenceEqual(needle))
                return index;
        }
        throw new InvalidDataException(
            $"Test source omitted ASCII marker {value}.");
    }

    private sealed record RawRecordLocation(
        int Offset,
        int Length,
        ImmutableArray<int> GroupOffsets);
}
