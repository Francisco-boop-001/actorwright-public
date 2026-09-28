using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class FinishLifecycleArtifactPersistenceTests
{
    private const string RequestDigest =
        "1111111111111111111111111111111111111111111111111111111111111111";

    public static async Task RunAsync()
    {
        string testOwner = Path.Combine(
            FindRepositoryRoot(),
            "artifacts",
            "tests");
        string root = Path.Combine(
            testOwner,
            $"finish-lifecycle-persistence-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await PersistsPhysicalAndSemanticProposalHashesAsync(root);
            await PersistsExternalIndependentVerificationAsync(root);
        }
        finally
        {
            DeleteOwnedTestTree(root, testOwner);
        }
    }

    private static async Task PersistsPhysicalAndSemanticProposalHashesAsync(
        string root)
    {
        string proposalRoot = Path.Combine(root, "proposal");
        Directory.CreateDirectory(proposalRoot);
        WorkspacePath labRoot = new(root);
        SkyrimNpcFinishCoreRequest request = CreateRequest(proposalRoot);
        Sha256Hash requestSha256 = Hash(
            SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, labRoot));
        var proposal = new SkyrimNpcFinishCoreProposal
        {
            RequestSha256 = requestSha256,
            Request = request,
            Status = SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite,
            ExistingRecordChanges = ["NPC_ 0x00000800: test"],
            NextFormId = new FormId(0x801),
            MasterOrder = ["Skyrim.esm"],
            RuntimeAuthority = false
        };
        byte[] withoutSelfHash =
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                proposal,
                labRoot);
        Sha256Hash semanticSha256 =
            SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                withoutSelfHash);
        byte[] proposalBytes =
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                proposal with { ProposalSha256 = semanticSha256 },
                labRoot);
        string physicalSha256 = Hash(proposalBytes).Value.ToUpperInvariant();
        Assert(
            !string.Equals(
                physicalSha256,
                semanticSha256.Value,
                StringComparison.Ordinal),
            "the proposal fixture did not exercise distinct physical and semantic hashes");

        string proposalPath = Path.Combine(proposalRoot, "proposal.json");
        await File.WriteAllBytesAsync(proposalPath, proposalBytes);
        var proposalArtifact = new WorkflowArtifactBinding(
            WorkflowArtifactKinds.NpcFinishCoreProposal,
            SkyrimNpcFinishCoreProposal.SchemaIdentifier,
            new WorkspacePath(proposalPath),
            proposalBytes.LongLength,
            physicalSha256,
            "npc finish analyze",
            RequestDigest,
            [requestSha256.Value.ToUpperInvariant()],
            semanticSha256.Value.ToUpperInvariant());
        AgentWorkflowContractValidation.Validate(proposalArtifact);

        var bundle = new AgentWorkflowBundle(
            AgentWorkflowSchemas.BundleV1,
            AgentWorkflowSchemas.SkyrimJslotFollowerWorkflowV1,
            GameEdition.SkyrimSpecialEdition,
            new WorkflowNpcIdentity(
                "SemanticHashActor",
                null,
                "ProposalSource.esp",
                "0x00000800"),
            AgentWorkflowPhase.Analyze,
            RequestDigest,
            [proposalArtifact],
            [],
            []);
        var codec = new AgentWorkflowBundleCodec(
            new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath(@"F:\ExampleGame")),
            labRoot);
        WorkspacePath bundlePath = new(
            Path.Combine(proposalRoot, "workflow.json"));
        AgentWorkflowBundleDocument written = codec.WriteNew(
            bundle,
            bundlePath);
        AgentWorkflowBundleDocument loaded = codec.Load(
            bundlePath,
            written.Sha256);
        Assert(
            loaded.Bundle.Artifacts.Single().Sha256 == physicalSha256 &&
            string.Equals(
                loaded.Bundle.Artifacts.Single().SemanticSha256,
                semanticSha256.Value,
                StringComparison.OrdinalIgnoreCase),
            "the strict workflow codec did not preserve both proposal hashes");

        AssertThrows<ArgumentException>(
            () => AgentWorkflowContractValidation.Validate(
                proposalArtifact with { SemanticSha256 = null }),
            "a Finish proposal without its semantic SHA-256 was accepted");
        AssertThrows<ArgumentException>(
            () => AgentWorkflowContractValidation.Validate(
                proposalArtifact with
                {
                    Kind = WorkflowArtifactKinds.NpcFinishCoreManifest
                }),
            "a non-proposal artifact with a semantic SHA-256 was accepted");
        AssertCodecRefused(
            () => codec.WriteNew(
                bundle with
                {
                    Artifacts =
                    [
                        proposalArtifact with
                        {
                            SemanticSha256 = RequestDigest
                        }
                    ]
                },
                new WorkspacePath(Path.Combine(
                    proposalRoot,
                    "wrong-semantic-workflow.json"))),
            "workflow-artifact-semantic-hash-mismatch",
            "a semantic SHA-256 that did not match the proposal bytes was accepted");
    }

    private static async Task PersistsExternalIndependentVerificationAsync(
        string root)
    {
        string packageRootValue = Path.Combine(root, "verified-package");
        string evidenceRoot = Path.Combine(root, "external-evidence");
        Directory.CreateDirectory(packageRootValue);
        Directory.CreateDirectory(evidenceRoot);
        WorkspacePath labRoot = new(root);
        WorkspacePath packageRoot = new(packageRootValue);
        string archivePathValue = Path.Combine(root, "verified-package.zip");
        byte[] archiveBytes = "archive physical bytes"u8.ToArray();
        await File.WriteAllBytesAsync(archivePathValue, archiveBytes);
        string archiveSha256 = PhysicalHash(archiveBytes);
        var verification = new SkyrimNpcFinishCoreVerification
        {
            Status = SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired,
            Verified = true,
            PlacementIncluded = false,
            RuntimeAuthority = false,
            VisualAuthority = false,
            TypedForbiddenCounts = ImmutableDictionary<string, int>.Empty
                .Add("CELL", 0),
            RawForbiddenCounts = ImmutableDictionary<string, int>.Empty
                .Add("CELL", 0),
            PluginSha256 = Hash("plugin"u8),
            PackageTreeSha256 = Hash("package-tree"u8),
            SourcePackageTreeSha256 = Hash("source-tree"u8),
            ArchiveSha256 = new Sha256Hash(archiveSha256),
            RuntimeIdentity = new SkyrimNpcFinishCoreRuntimeIdentity
            {
                BaseNpc = new FormReference(
                    new PluginName("ProposalSource.esp"),
                    new FormId(0x800)),
                PlacedReference = null,
                PlacementIncluded = false
            }
        };
        var store = new SkyrimNpcFinishCoreVerificationArtifactStore(
            new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath(@"F:\ExampleGame")),
            labRoot);
        WorkspacePath archivePath = new(archivePathValue);
        SkyrimNpcFinishCoreManifest manifest = CreateManifest(
            packageRoot,
            archivePath,
            verification);
        string manifestPathValue = Path.Combine(
            packageRoot.Value,
            "finish-core-manifest.json");
        WorkspacePath manifestPath = new(manifestPathValue);
        byte[] manifestBytes =
            SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                manifest,
                labRoot);
        await File.WriteAllBytesAsync(manifestPathValue, manifestBytes);
        string manifestSha256 = PhysicalHash(manifestBytes);

        AssertStoreRefused(
            () => store.LoadManifest(manifestPath, RequestDigest),
            "finish-verification-manifest-hash-mismatch",
            "the manifest loader accepted the wrong expected physical hash");
        WorkspacePath noncanonicalManifestPath = new(Path.Combine(
            packageRoot.Value,
            "noncanonical-finish-core-manifest.json"));
        byte[] noncanonicalManifestBytes =
            [.. manifestBytes, (byte)'\n'];
        await File.WriteAllBytesAsync(
            noncanonicalManifestPath.Value,
            noncanonicalManifestBytes);
        AssertStoreRefused(
            () => store.LoadManifest(
                noncanonicalManifestPath,
                PhysicalHash(noncanonicalManifestBytes)),
            "finish-verification-manifest-noncanonical",
            "a hash-matched noncanonical Finish manifest was accepted");
        SkyrimNpcFinishCoreVerifiedManifestDocument verifiedManifest =
            store.LoadManifest(manifestPath, manifestSha256);

        await File.WriteAllBytesAsync(
            manifestPathValue,
            [.. manifestBytes, (byte)' ']);
        await AssertStoreRefusedAsync(
            () => store.WriteNewAsync(
                verification,
                verifiedManifest,
                RequestDigest,
                new WorkspacePath(Path.Combine(
                    evidenceRoot,
                    "tampered-manifest-must-not-write.json")),
                CancellationToken.None),
            "finish-verification-manifest-hash-mismatch",
            "a typed manifest whose physical bytes drifted was accepted");
        await File.WriteAllBytesAsync(manifestPathValue, manifestBytes);

        SkyrimNpcFinishCoreManifest mismatchedManifest = manifest with
        {
            PackageTreeSha256 = Hash("different-package-tree"u8),
            Evidence = manifest.Evidence with
            {
                PackageTreeSha256 = Hash("different-package-tree"u8)
            }
        };
        WorkspacePath mismatchedManifestPath = new(Path.Combine(
            packageRoot.Value,
            "mismatched-finish-core-manifest.json"));
        byte[] mismatchedManifestBytes =
            SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                mismatchedManifest,
                labRoot);
        await File.WriteAllBytesAsync(
            mismatchedManifestPath.Value,
            mismatchedManifestBytes);
        SkyrimNpcFinishCoreVerifiedManifestDocument mismatchedVerifiedManifest =
            store.LoadManifest(
                mismatchedManifestPath,
                PhysicalHash(mismatchedManifestBytes));
        await AssertStoreRefusedAsync(
            () => store.WriteNewAsync(
                verification,
                mismatchedVerifiedManifest,
                RequestDigest,
                new WorkspacePath(Path.Combine(
                    evidenceRoot,
                    "mismatched-linkage-must-not-write.json")),
                CancellationToken.None),
            "finish-verification-contract-invalid",
            "a manifest whose typed package binding differed from verification was accepted");

        WorkspacePath output = new(
            Path.Combine(evidenceRoot, "independent-verification.json"));

        SkyrimNpcFinishCoreVerificationArtifactDocument written =
            await store.WriteNewAsync(
                verification,
                verifiedManifest,
                RequestDigest,
                output,
                CancellationToken.None);
        byte[] physicalBytes = await File.ReadAllBytesAsync(output.Value);
        Assert(
            written.Path == output &&
            written.Size == physicalBytes.LongLength &&
            string.Equals(
                written.Sha256,
                Hash(physicalBytes).Value,
                StringComparison.OrdinalIgnoreCase) &&
            written.Utf8Json.AsSpan().SequenceEqual(physicalBytes) &&
            !(physicalBytes.Length >= 3 &&
              physicalBytes[0] == 0xEF &&
              physicalBytes[1] == 0xBB &&
              physicalBytes[2] == 0xBF) &&
            Array.IndexOf(physicalBytes, (byte)'\r') < 0,
            "the external verification artifact was not returned as exact canonical physical evidence");
        Assert(
            written.Artifact.Kind ==
            WorkflowArtifactKinds.NpcFinishCoreVerification &&
            written.Artifact.SchemaOrMediaType ==
            SkyrimNpcFinishCoreVerification.SchemaIdentifier &&
            written.Artifact.ProducerCommand == "npc finish verify" &&
            written.Artifact.RequestDigest == RequestDigest &&
            written.Artifact.SemanticSha256 is null &&
            written.Artifact.InputArtifactHashes.SequenceEqual(
                new[] { manifestSha256, archiveSha256 }
                    .Order(StringComparer.Ordinal)),
            "the verification workflow binding did not retain its exact manifest/archive provenance");

        SkyrimNpcFinishCoreVerificationArtifactDocument loaded = store.Load(
            output,
            written.Sha256,
            verifiedManifest,
            RequestDigest);
        Assert(
            loaded.Verification.Schema == verification.Schema &&
            loaded.Verification.Status == verification.Status &&
            loaded.Verification.Verified == verification.Verified &&
            loaded.Verification.PluginSha256 == verification.PluginSha256 &&
            loaded.Verification.PackageTreeSha256 ==
                verification.PackageTreeSha256 &&
            loaded.Verification.SourcePackageTreeSha256 ==
                verification.SourcePackageTreeSha256 &&
            loaded.Verification.ArchiveSha256 ==
                verification.ArchiveSha256 &&
            loaded.Verification.TypedForbiddenCounts.SequenceEqual(
                verification.TypedForbiddenCounts) &&
            loaded.Verification.RawForbiddenCounts.SequenceEqual(
                verification.RawForbiddenCounts) &&
            loaded.Verification.RuntimeIdentity ==
                verification.RuntimeIdentity &&
            loaded.Utf8Json.AsSpan().SequenceEqual(physicalBytes),
            "the promoted verification artifact did not survive strict pinned reload");

        byte[] tamperedPhysicalBytes = physicalBytes.ToArray();
        tamperedPhysicalBytes[^1] =
            (byte)(tamperedPhysicalBytes[^1] ^ 0x01);
        try
        {
            await File.WriteAllBytesAsync(
                output.Value,
                tamperedPhysicalBytes);
            AssertStoreRefused(
                () => store.Load(
                    output,
                    written.Sha256,
                    verifiedManifest,
                    RequestDigest),
                "finish-verification-hash-mismatch",
                "a tampered persisted verification matched its original physical SHA-256");
        }
        finally
        {
            await File.WriteAllBytesAsync(output.Value, physicalBytes);
        }

        await AssertStoreRefusedAsync(
            () => store.WriteNewAsync(
                verification,
                verifiedManifest,
                RequestDigest,
                output,
                CancellationToken.None),
            "finish-verification-output-exists",
            "the external verification store overwrote an existing output");
        await AssertStoreRefusedAsync(
            () => store.WriteNewAsync(
                verification,
                verifiedManifest,
                RequestDigest,
                new WorkspacePath(Path.Combine(
                    packageRoot.Value,
                    "must-not-mutate-package.json")),
                CancellationToken.None),
            "finish-verification-output-overlap",
            "the external verification store wrote inside the verified package");
        await AssertStoreRefusedAsync(
            () => store.WriteNewAsync(
                verification,
                verifiedManifest,
                RequestDigest,
                new WorkspacePath(output.Value + ":stream"),
                CancellationToken.None),
            "finish-verification-path-refused",
            "the external verification store accepted an alternate data stream");

        WorkspacePath noncanonicalPath = new(Path.Combine(
            evidenceRoot,
            "noncanonical-verification.json"));
        byte[] noncanonicalBytes = [.. physicalBytes, (byte)'\n'];
        await File.WriteAllBytesAsync(
            noncanonicalPath.Value,
            noncanonicalBytes);
        AssertStoreRefused(
            () => store.Load(
                noncanonicalPath,
                PhysicalHash(noncanonicalBytes),
                verifiedManifest,
                RequestDigest),
            "finish-verification-json-noncanonical",
            "a hash-matched but noncanonical persisted verification was accepted");

        WorkspacePath cancelledOutput = new(Path.Combine(
            evidenceRoot,
            "cancelled-verification.json"));
        await AssertCancelledAsync(() => store.WriteNewAsync(
            verification,
            verifiedManifest,
            RequestDigest,
            cancelledOutput,
            new CancellationToken(canceled: true)));
        Assert(
            !File.Exists(cancelledOutput.Value) &&
            !Directory.EnumerateFileSystemEntries(
                    evidenceRoot,
                    "cancelled-verification.json.tmp-*",
                    SearchOption.TopDirectoryOnly)
                .Any(),
            "a cancelled pre-promotion write retained its destination or owned temporary");
    }

    private static SkyrimNpcFinishCoreManifest CreateManifest(
        WorkspacePath packageRoot,
        WorkspacePath archivePath,
        SkyrimNpcFinishCoreVerification verification) =>
        new()
        {
            Plugin = new PluginName("ProposalSource.esp"),
            PluginSha256 = verification.PluginSha256,
            BaseNpc = verification.RuntimeIdentity.BaseNpc,
            RequestSha256 = Hash("finish-request"u8),
            ProposalSha256 = Hash("finish-proposal"u8),
            PlacementIncluded = false,
            RuntimeAuthority = false,
            VisualAuthority = false,
            PackageRoot = packageRoot,
            Archive = archivePath,
            ArchiveSha256 = null,
            SourcePackageTreeSha256 =
                verification.SourcePackageTreeSha256,
            PackageTreeSha256 = verification.PackageTreeSha256,
            RuntimeIdentity = verification.RuntimeIdentity,
            Evidence = new SkyrimNpcFinishCoreManifestEvidence
            {
                Files =
                [
                    new SkyrimNpcFinishCoreEvidenceEntry
                    {
                        Path = new AssetPath("ProposalSource.esp"),
                        ByteLength = 6,
                        Sha256 = verification.PluginSha256!.Value
                    }
                ],
                PackageTreeSha256 = verification.PackageTreeSha256,
                SourcePackageTreeSha256 =
                    verification.SourcePackageTreeSha256
            }
        };

    private static SkyrimNpcFinishCoreRequest CreateRequest(string root)
    {
        PluginName plugin = new("ProposalSource.esp");
        return new SkyrimNpcFinishCoreRequest
        {
            Source = new SkyrimNpcFinishCoreSource
            {
                PackageRoot = new WorkspacePath(root),
                PackageManifest = new WorkspacePath(
                    Path.Combine(root, "manifest.json")),
                PackageManifestSha256 = Hash("manifest"u8),
                PackageTreeSha256 = Hash("tree"u8),
                PluginPath = new WorkspacePath(
                    Path.Combine(root, plugin.Value)),
                Plugin = plugin,
                PluginSha256 = Hash("plugin"u8)
            },
            Actor = new SkyrimNpcFinishCoreActor
            {
                EditorId = new EditorId("SemanticHashActor"),
                FormId = new FormId(0x800)
            },
            Authorities = new SkyrimNpcFinishCoreAuthorities
            {
                BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                Providers = []
            },
            AiPolicy = new SkyrimNpcFinishCoreAiPolicy
            {
                Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                Confidence = SkyrimNpcFinishCoreConfidence.Average,
                Energy = 50,
                Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                Assistance = SkyrimNpcFinishCoreAssistance.HelpsAllies,
                Mood = SkyrimNpcFinishCoreMood.Neutral
            },
            OutfitPolicy = new SkyrimNpcFinishCoreOutfitPolicyDocument
            {
                Policy = SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit,
                ExistingOutfit = new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x12E46))
            },
            InventoryPolicy = new SkyrimNpcFinishCoreInventoryPolicyDocument
            {
                Policy = SkyrimNpcFinishCoreInventoryPolicy.PreserveInventory
            },
            SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
            {
                Template = new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x1B217)),
                TemplateEditorId = "DefaultSandboxEditorLocation512"
            },
            Output = new SkyrimNpcFinishCoreOutput
            {
                Root = new WorkspacePath(Path.Combine(root, "out")),
                Archive = new WorkspacePath(Path.Combine(root, "out.zip")),
                PluginFileName = plugin.Value
            }
        };
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static Sha256Hash Hash(byte[] bytes) => Hash(bytes.AsSpan());

    private static string PhysicalHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(
        Action action,
        string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void AssertCodecRefused(
        Action action,
        string code,
        string message)
    {
        try
        {
            action();
        }
        catch (AgentWorkflowCodecException exception)
            when (exception.Code == code)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void AssertStoreRefused(
        Action action,
        string code,
        string message)
    {
        try
        {
            action();
        }
        catch (SkyrimNpcFinishCoreVerificationArtifactException exception)
            when (exception.Code == code)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static async Task AssertStoreRefusedAsync(
        Func<ValueTask<SkyrimNpcFinishCoreVerificationArtifactDocument>> action,
        string code,
        string message)
    {
        try
        {
            await action();
        }
        catch (SkyrimNpcFinishCoreVerificationArtifactException exception)
            when (exception.Code == code)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static async Task AssertCancelledAsync(
        Func<ValueTask<SkyrimNpcFinishCoreVerificationArtifactDocument>> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException(
            "a cancelled pre-promotion write did not preserve cancellation");
    }

    private static string FindRepositoryRoot()
    {
        string? current = Directory.GetCurrentDirectory();
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current, "global.json")) &&
                (File.Exists(Path.Combine(current, ".git")) ||
                 Directory.Exists(Path.Combine(current, ".git"))))
                return current;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not locate the Actorwright repository root.");
    }

    private static void DeleteOwnedTestTree(
        string path,
        string testOwner)
    {
        string admitted = Path.GetFullPath(path);
        string owner = Path.GetFullPath(testOwner)
            .TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!admitted.StartsWith(owner, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(admitted).StartsWith(
                "finish-lifecycle-persistence-",
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Refused to clean a non-owned Finish lifecycle test root.");
        if (Directory.Exists(admitted))
            Directory.Delete(admitted, recursive: true);
    }
}
