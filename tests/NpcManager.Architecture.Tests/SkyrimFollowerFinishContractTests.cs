using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimFollowerFinishContracts()
    {
        TestSkyrimFollowerFinishFixedAllocation();
        TestSkyrimFollowerFinishValueBounds();
        await TestSkyrimFollowerFinishRequestLoader();
        await TestSkyrimFollowerFinishProposalLoader();
    }

    private static void TestSkyrimFollowerFinishFixedAllocation()
    {
        var allocation = SkyrimFollowerFinishAllocation.SimpleFollowerV1;
        Assert(allocation.Package == new FormId(0x805),
            "PACK allocation drifted.");
        Assert(allocation.Anchor == new FormId(0x806),
            "REFR allocation drifted.");
        Assert(allocation.Actor == new FormId(0x807),
            "ACHR allocation drifted.");
        Assert(allocation.NextFormId == new FormId(0x808),
            "NextFormID drifted.");
    }

    private static void TestSkyrimFollowerFinishValueBounds()
    {
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = new SkyrimFollowerFinishHairChange(
                new FormId(0x801),
                new SkyrimPackedRgb(0x94876A),
                new SkyrimPackedRgb(0x1000000)));

        var target = new FormReference(
            new PluginName("Follower.esp"),
            new FormId(0x806));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = new SkyrimFollowerFinishSandbox(
                "sandbox", 0, "continuous", target, "rank < 0"));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = new SkyrimFollowerFinishSandbox(
                "sandbox", 2049, "continuous", target, "rank < 0"));

        AssertThrows<ArgumentException>(() =>
            _ = new SkyrimFollowerFinishAllocation(
                new FormId(0x805),
                new FormId(0x805),
                new FormId(0x807),
                new FormId(0x808)));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = new SkyrimFollowerFinishAllocation(
                new FormId(0x7FF),
                new FormId(0x806),
                new FormId(0x807),
                new FormId(0x808)));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = new SkyrimFollowerFinishAllocation(
                new FormId(0x805),
                new FormId(0x806),
                new FormId(0x807),
                new FormId(0x1000)));
    }

    private static async Task TestSkyrimFollowerFinishRequestLoader()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        string scratch = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "follower-finish-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string sourceZip = Path.Combine(scratch, "source.zip");
        await File.WriteAllBytesAsync(sourceZip, [0x50, 0x4B, 0x05, 0x06]);
        string packageManifest = Path.Combine(
            scratch,
            "source-npcmanager-package.json");
        await File.WriteAllTextAsync(packageManifest, "{}");
        JsonObject externalAuthorities =
            await CreateFollowerFinishExternalAuthoritiesAsync(scratch);
        var loader = new SkyrimFollowerFinishRequestFileLoader(labRoot);

        try
        {
            JsonObject validDocument = CreateFollowerFinishRequestDocument(
                sourceZip,
                packageManifest,
                externalAuthorities,
                Path.Combine(scratch, "candidate"),
                Path.Combine(scratch, "candidate.zip"));
            (WorkspacePath requestPath, Sha256Hash requestHash) =
                await WriteFollowerFinishJson(
                    scratch,
                    "valid-request.json",
                    validDocument);

            SkyrimFollowerFinishRequestLoadResult loaded =
                await loader.LoadRequestAsync(
                    requestPath,
                    requestHash,
                    CancellationToken.None);
            Assert(loaded.Loaded && loaded.Request is not null &&
                   loaded.ActualSha256 == requestHash &&
                   loaded.ByteLength > 0,
                "A complete schema-1 follower-finish request did not load.");
            SkyrimFollowerFinishRequest request = loaded.Request ??
                throw new InvalidOperationException(
                    "Loaded request result omitted the request.");
            Assert(request.Operation == "skyrim-simple-follower-finish" &&
                   request.Source.Plugin ==
                   new PluginName("BrigitteBardotNpcManager.esp") &&
                   request.Source.PackageManifest ==
                   new WorkspacePath(packageManifest) &&
                   request.ExternalAuthorities.PlacementEvidence.ByteLength ==
                   19 &&
                   request.ExternalAuthorities.Providers
                       .Select(provider => provider.Plugin.Value)
                       .SequenceEqual(["Skyrim.esm", "Update.esm"]) &&
                   request.NpcFormId == new FormId(0x800) &&
                   request.Hair.NewPackedRgb.Value == 0xD6BE83 &&
                   request.Sandbox.Radius == 768 &&
                   request.Allocation ==
                   SkyrimFollowerFinishAllocation.SimpleFollowerV1 &&
                   request.SetEslFlag &&
                   !request.CompactFormIds &&
                   request.AllowedPackageFiles.Length == 4,
                "The strict request loader lost a closed typed value.");
            Sha256Hash changedZipHash = new(new string('A', 64));
            SkyrimFollowerFinishRequest changed = request with
            {
                Source = request.Source with
                {
                    ZipSha256 = changedZipHash
                }
            };
            Assert(changed.Source.ZipSha256 == changedZipHash &&
                   request.Source.ZipSha256 != changedZipHash,
                "Immutable follower-finish records do not support isolated hostile-test copies.");

            JsonObject requestProviderAlias = Clone(validDocument);
            requestProviderAlias["externalAuthorities"]!["providers"]![0]![
                "path"] = Path.Combine(scratch, "Update.esm")
                .ToUpperInvariant();
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "Update.esm",
                requestProviderAlias,
                "A request document path that case-aliased a provider was accepted.",
                expectedDiagnosticCode:
                    "follower-finish-request-invalid",
                expectedMessageFragment:
                    "loaded request document path must be pairwise disjoint from provider authority Update.esm");

            string requestDocumentOutputRoot = Path.Combine(
                scratch,
                "request-document-output-root");
            Directory.CreateDirectory(requestDocumentOutputRoot);
            JsonObject requestOutputAncestor = Clone(validDocument);
            requestOutputAncestor["outputRoot"] =
                requestDocumentOutputRoot;
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                Path.Combine(
                    "request-document-output-root",
                    "request-under-output-root.json"),
                requestOutputAncestor,
                "A request document below its declared output root was accepted.",
                expectedDiagnosticCode:
                    "follower-finish-request-invalid",
                expectedMessageFragment:
                    "loaded request document path must be pairwise disjoint from output root");

            JsonObject missingAuthorities = Clone(validDocument);
            missingAuthorities.Remove("externalAuthorities");
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "missing-external-authorities.json",
                missingAuthorities,
                "A request without external authority bindings was accepted.");

            JsonObject missingSkyrim = Clone(validDocument);
            missingSkyrim["externalAuthorities"]!["providers"]!
                .AsArray()
                .RemoveAt(1);
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "missing-skyrim-provider.json",
                missingSkyrim,
                "A request without exactly one Skyrim.esm provider was accepted.");

            JsonObject duplicatePlugin = Clone(validDocument);
            JsonObject duplicateProvider =
                duplicatePlugin["externalAuthorities"]!["providers"]![0]!
                    .DeepClone()
                    .AsObject();
            string aliasDirectory = Path.Combine(
                scratch,
                "duplicate-plugin-alias");
            Directory.CreateDirectory(aliasDirectory);
            string aliasUpdate = Path.Combine(aliasDirectory, "Update.esm");
            await File.WriteAllBytesAsync(aliasUpdate, [0x55, 0x50, 0x44]);
            duplicateProvider["path"] = aliasUpdate;
            duplicateProvider["byteLength"] = 3;
            duplicateProvider["sha256"] =
                Hash([0x55, 0x50, 0x44]).Value;
            duplicatePlugin["externalAuthorities"]!["providers"]!
                .AsArray()
                .Add(duplicateProvider);
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "duplicate-provider-plugin.json",
                duplicatePlugin,
                "A filename-consistent duplicate provider plugin was accepted.",
                expectedDiagnosticCode:
                    "follower-finish-request-invalid",
                expectedMessageFragment:
                    "External provider plugin names must be case-insensitively unique.");

            JsonObject duplicatePathAlias = Clone(validDocument);
            duplicatePathAlias["externalAuthorities"]!["providers"]!
                .AsArray()
                .Add(
                    duplicatePathAlias["externalAuthorities"]!["providers"]![
                        0]!.DeepClone());
            duplicatePathAlias["externalAuthorities"]!["providers"]![2]![
                "path"] = duplicatePathAlias["externalAuthorities"]![
                    "providers"]![0]!["path"]!.GetValue<string>()
                    .ToUpperInvariant();
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "duplicate-provider-path-alias.json",
                duplicatePathAlias,
                "A filename-consistent Windows-equivalent provider path alias was accepted.",
                expectedDiagnosticCode:
                    "follower-finish-request-invalid",
                expectedMessageFragment:
                    "External provider paths must be case-insensitively unique.");

            JsonObject outsidePlacement = Clone(validDocument);
            outsidePlacement["externalAuthorities"]!["placementEvidence"]![
                "path"] = @"C:\tmp\placement-evidence.json";
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "outside-placement-authority.json",
                outsidePlacement,
                "A placement authority outside the K workspace was accepted.");

            JsonObject filenameMismatch = Clone(validDocument);
            string mismatchedProvider = Path.Combine(
                scratch,
                "NotUpdate.esm");
            await File.WriteAllBytesAsync(
                mismatchedProvider,
                [0x55, 0x50, 0x44]);
            filenameMismatch["externalAuthorities"]!["providers"]![1]![
                "path"] = mismatchedProvider;
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "provider-filename-mismatch.json",
                filenameMismatch,
                "A provider whose filename disagreed with its plugin identity was accepted.");

            string authorityReparseBoundary = Path.Combine(
                scratch,
                "virtual-authority-reparse-boundary");
            Directory.CreateDirectory(authorityReparseBoundary);
            string reparsePlacement = Path.Combine(
                authorityReparseBoundary,
                "placement-evidence.json");
            await File.WriteAllBytesAsync(
                reparsePlacement,
                new byte[20]);
            JsonObject reparseAuthorityDocument = Clone(validDocument);
            reparseAuthorityDocument["externalAuthorities"]![
                "placementEvidence"]!["path"] = reparsePlacement;
            (WorkspacePath authorityRequestPath,
                Sha256Hash authorityRequestHash) =
                await WriteFollowerFinishJson(
                    scratch,
                    "authority-reparse-request.json",
                    reparseAuthorityDocument);
            var authorityReparseLoader =
                new SkyrimFollowerFinishRequestFileLoader(
                    labRoot,
                    candidate =>
                    {
                        if (string.Equals(
                                candidate,
                                authorityReparseBoundary,
                                StringComparison.OrdinalIgnoreCase))
                            return FileAttributes.Directory |
                                   FileAttributes.ReparsePoint;
                        return File.Exists(candidate) ||
                               Directory.Exists(candidate)
                            ? File.GetAttributes(candidate)
                            : null;
                    });
            SkyrimFollowerFinishRequestLoadResult authorityReparse =
                await authorityReparseLoader.LoadRequestAsync(
                    authorityRequestPath,
                    authorityRequestHash,
                    CancellationToken.None);
            Assert(!authorityReparse.Loaded &&
                   authorityReparse.Request is null &&
                   authorityReparse.Diagnostics.Any(
                       diagnostic =>
                           diagnostic.Code ==
                           "follower-finish-request-security-refused"),
                "An external authority traversing a reparse boundary did not return the typed security refusal.");

            JsonObject nonEsp = Clone(validDocument);
            nonEsp["source"]!["plugin"] = "BrigitteBardotNpcManager.esm";
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "non-esp.json",
                nonEsp,
                "A non-.esp source plugin was accepted.");

            JsonObject nonK = Clone(validDocument);
            nonK["source"]!["zip"] = @"C:\tmp\source.zip";
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "non-k.json",
                nonK,
                "A non-K source path was accepted.");

            JsonObject missingManifest = Clone(validDocument);
            missingManifest["source"]!
                .AsObject()
                .Remove("packageManifest");
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "missing-package-manifest.json",
                missingManifest,
                "A source without an explicit package-manifest path was accepted.");

            JsonObject unknownManifestPath = Clone(validDocument);
            unknownManifestPath["source"]!["packageManifestPath"] =
                packageManifest;
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "unknown-package-manifest-path.json",
                unknownManifestPath,
                "An unknown package-manifest path property was accepted.");

            JsonObject wrongCaseManifest = Clone(validDocument);
            JsonObject wrongCaseSource =
                wrongCaseManifest["source"]!.AsObject();
            wrongCaseSource.Remove("packageManifest");
            wrongCaseSource["PackageManifest"] = packageManifest;
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "wrong-case-package-manifest.json",
                wrongCaseManifest,
                "A wrong-case packageManifest property was accepted.");

            JsonObject nonexistentManifest = Clone(validDocument);
            nonexistentManifest["source"]!["packageManifest"] =
                Path.Combine(
                    scratch,
                    "does-not-exist-package-manifest.json");
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "nonexistent-package-manifest.json",
                nonexistentManifest,
                "A nonexistent package-manifest input was accepted.");

            string manifestDirectory = Path.Combine(
                scratch,
                "package-manifest-directory");
            Directory.CreateDirectory(manifestDirectory);
            JsonObject directoryManifest = Clone(validDocument);
            directoryManifest["source"]!["packageManifest"] =
                manifestDirectory;
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "directory-package-manifest.json",
                directoryManifest,
                "A directory was accepted as the package-manifest input.");

            foreach ((string path, string name) in
                     new[]
                     {
                         (packageManifest + ":stream", "ads"),
                         (@"\\?\" + packageManifest, "device"),
                         (@"\\server\share\npcmanager-package.json", "unc")
                     })
            {
                JsonObject unsafeManifest = Clone(validDocument);
                unsafeManifest["source"]!["packageManifest"] = path;
                await AssertFollowerFinishRequestRefused(
                    loader,
                    scratch,
                    $"unsafe-package-manifest-{name}.json",
                    unsafeManifest,
                    $"A package-manifest {name} path was accepted.");
            }

            string manifestReparseBoundary = Path.Combine(
                scratch,
                "virtual-manifest-reparse-boundary");
            Directory.CreateDirectory(manifestReparseBoundary);
            string reparseManifest = Path.Combine(
                manifestReparseBoundary,
                "npcmanager-package.json");
            await File.WriteAllTextAsync(reparseManifest, "{}");
            JsonObject reparseManifestDocument = Clone(validDocument);
            reparseManifestDocument["source"]!["packageManifest"] =
                reparseManifest;
            (WorkspacePath manifestRequestPath,
                Sha256Hash manifestRequestHash) =
                await WriteFollowerFinishJson(
                    scratch,
                    "manifest-reparse-request.json",
                    reparseManifestDocument);
            var manifestReparseLoader =
                new SkyrimFollowerFinishRequestFileLoader(
                    labRoot,
                    candidate =>
                    {
                        if (string.Equals(
                                candidate,
                                manifestReparseBoundary,
                                StringComparison.OrdinalIgnoreCase))
                            return FileAttributes.Directory |
                                   FileAttributes.ReparsePoint;
                        return File.Exists(candidate) ||
                               Directory.Exists(candidate)
                            ? File.GetAttributes(candidate)
                            : null;
                    });
            SkyrimFollowerFinishRequestLoadResult manifestReparse =
                await manifestReparseLoader.LoadRequestAsync(
                    manifestRequestPath,
                    manifestRequestHash,
                    CancellationToken.None);
            Assert(!manifestReparse.Loaded &&
                   manifestReparse.Request is null &&
                   manifestReparse.Diagnostics.Any(
                       diagnostic =>
                           diagnostic.Code ==
                           "follower-finish-request-security-refused"),
                "A package manifest traversing a reparse boundary did not return the typed security refusal.");

            JsonObject manifestZipCaseAlias = Clone(validDocument);
            manifestZipCaseAlias["source"]!["packageManifest"] =
                sourceZip.ToUpperInvariant();
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "manifest-zip-case-alias.json",
                manifestZipCaseAlias,
                "Windows-equivalent source ZIP and package-manifest aliases were accepted.");

            JsonObject outputRootBelowManifest = Clone(validDocument);
            outputRootBelowManifest["outputRoot"] = Path.Combine(
                packageManifest,
                "candidate");
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "output-root-below-manifest.json",
                outputRootBelowManifest,
                "An output root nested below the package manifest was accepted.");

            JsonObject outputZipBelowManifest = Clone(validDocument);
            outputZipBelowManifest["outputZip"] = Path.Combine(
                packageManifest,
                "candidate.zip");
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "output-zip-below-manifest.json",
                outputZipBelowManifest,
                "An output ZIP nested below the package manifest was accepted.");

            JsonObject sameOutput = Clone(validDocument);
            sameOutput["outputZip"] = sourceZip;
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "same-output.json",
                sameOutput,
                "An output equal to the immutable source ZIP was accepted.");

            string caseAlias = Path.Combine(
                scratch,
                "candidate-case-alias.zip");
            JsonObject caseOnlyAlias = Clone(validDocument);
            caseOnlyAlias["outputRoot"] = caseAlias;
            caseOnlyAlias["outputZip"] = caseAlias.ToUpperInvariant();
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "case-only-output-alias.json",
                caseOnlyAlias,
                "Windows-equivalent case-only output aliases were accepted.");

            string nestedOutputRoot = Path.Combine(
                scratch,
                "candidate-nested");
            JsonObject zipBelowOutputRoot = Clone(validDocument);
            zipBelowOutputRoot["outputRoot"] = nestedOutputRoot;
            zipBelowOutputRoot["outputZip"] = Path.Combine(
                nestedOutputRoot,
                "candidate.zip");
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "zip-below-output-root.json",
                zipBelowOutputRoot,
                "An output ZIP nested below the output root was accepted.");

            string outputZipAncestor = Path.Combine(
                scratch,
                "candidate-container.zip");
            JsonObject outputRootBelowZip = Clone(validDocument);
            outputRootBelowZip["outputRoot"] = Path.Combine(
                outputZipAncestor,
                "candidate");
            outputRootBelowZip["outputZip"] = outputZipAncestor;
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "output-root-below-zip.json",
                outputRootBelowZip,
                "An output root nested below the output ZIP was accepted.");

            JsonObject outputBelowSourceZip = Clone(validDocument);
            outputBelowSourceZip["outputRoot"] = Path.Combine(
                sourceZip,
                "candidate");
            outputBelowSourceZip["outputZip"] = Path.Combine(
                scratch,
                "source-ancestor-output.zip");
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "output-below-source-zip.json",
                outputBelowSourceZip,
                "An output nested below the immutable source ZIP was accepted.");

            JsonObject unknown = Clone(validDocument);
            unknown["unexpected"] = true;
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "unknown.json",
                unknown,
                "An unknown request property was accepted.");

            byte[] duplicateBytes = JsonSerializer.SerializeToUtf8Bytes(
                validDocument);
            string duplicateJson = System.Text.Encoding.UTF8.GetString(
                    duplicateBytes)
                .Replace(
                    "\"schemaVersion\":1",
                    "\"schemaVersion\":1,\"schemaVersion\":1",
                    StringComparison.Ordinal);
            var duplicatePath = new WorkspacePath(
                Path.Combine(scratch, "duplicate.json"));
            await File.WriteAllTextAsync(duplicatePath.Value, duplicateJson);
            Sha256Hash duplicateHash = Hash(
                await File.ReadAllBytesAsync(duplicatePath.Value));
            SkyrimFollowerFinishRequestLoadResult duplicate =
                await loader.LoadRequestAsync(
                    duplicatePath,
                    duplicateHash,
                    CancellationToken.None);
            Assert(!duplicate.Loaded && duplicate.Request is null,
                "A duplicate request property was accepted.");

            SkyrimFollowerFinishRequestLoadResult stale =
                await loader.LoadRequestAsync(
                    requestPath,
                    new Sha256Hash(new string('0', 64)),
                    CancellationToken.None);
            Assert(!stale.Loaded && stale.Request is null,
                "A stale request-file hash was accepted.");

            JsonObject ads = Clone(validDocument);
            ads["source"]!["zip"] = sourceZip + ":stream";
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "ads.json",
                ads,
                "An alternate-data-stream source path was accepted.");

            string reparseBoundary = Path.Combine(
                scratch,
                "virtual-reparse-boundary");
            Directory.CreateDirectory(reparseBoundary);
            (WorkspacePath reparseRequestPath, Sha256Hash reparseRequestHash) =
                await WriteFollowerFinishJson(
                    reparseBoundary,
                    "reparse-request.json",
                    validDocument);
            var reparseLoader = new SkyrimFollowerFinishRequestFileLoader(
                labRoot,
                candidate =>
                {
                    if (string.Equals(
                            candidate,
                            reparseBoundary,
                            StringComparison.OrdinalIgnoreCase))
                        return FileAttributes.Directory |
                               FileAttributes.ReparsePoint;
                    return File.Exists(candidate) ||
                           Directory.Exists(candidate)
                        ? File.GetAttributes(candidate)
                        : null;
                });
            SkyrimFollowerFinishRequestLoadResult reparse =
                await reparseLoader.LoadRequestAsync(
                    reparseRequestPath,
                    reparseRequestHash,
                    CancellationToken.None);
            Assert(!reparse.Loaded &&
                   reparse.Request is null &&
                   reparse.Diagnostics.Any(
                       diagnostic =>
                           diagnostic.Code ==
                           "follower-finish-request-security-refused"),
                "A request traversing a reparse-point boundary did not return the typed security refusal.");

            string occupiedOutput = Path.Combine(scratch, "occupied-output");
            Directory.CreateDirectory(occupiedOutput);
            JsonObject existing = Clone(validDocument);
            existing["outputRoot"] = occupiedOutput;
            await AssertFollowerFinishRequestRefused(
                loader,
                scratch,
                "existing-output.json",
                existing,
                "An already-existing output root was accepted.");
            (WorkspacePath postWriteRequestPath,
                Sha256Hash postWriteRequestHash) =
                await WriteFollowerFinishJson(
                    scratch,
                    "post-write-existing-output.json",
                    existing);
            SkyrimFollowerFinishRequestLoadResult postWriteLoaded =
                await loader.LoadRequestAsync(
                    postWriteRequestPath,
                    postWriteRequestHash,
                    CancellationToken.None,
                    SkyrimFollowerFinishDocumentLoadMode
                        .PostWriteVerification);
            Assert(
                postWriteLoaded.Loaded &&
                postWriteLoaded.Request is not null,
                "Post-write verification could not load its exact request after the output root existed.");
            Directory.Delete(occupiedOutput);
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task TestSkyrimFollowerFinishProposalLoader()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        string scratch = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "follower-finish-proposal-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string sourceZip = Path.Combine(scratch, "source.zip");
        await File.WriteAllBytesAsync(sourceZip, [0x50, 0x4B, 0x05, 0x06]);
        string packageManifest = Path.Combine(
            scratch,
            "source-npcmanager-package.json");
        await File.WriteAllTextAsync(packageManifest, "{}");
        JsonObject externalAuthorities =
            await CreateFollowerFinishExternalAuthoritiesAsync(scratch);
        var loader = new SkyrimFollowerFinishRequestFileLoader(labRoot);

        try
        {
            JsonObject request = CreateFollowerFinishRequestDocument(
                sourceZip,
                packageManifest,
                externalAuthorities,
                Path.Combine(scratch, "candidate"),
                Path.Combine(scratch, "candidate.zip"));
            JsonObject validProposal = CreateFollowerFinishProposalDocument(
                request);
            (WorkspacePath proposalPath, Sha256Hash proposalHash) =
                await WriteFollowerFinishJson(
                    scratch,
                    "valid-proposal.json",
                    validProposal);

            SkyrimFollowerFinishProposalLoadResult loaded =
                await loader.LoadProposalAsync(
                    proposalPath,
                    proposalHash,
                    CancellationToken.None);
            Assert(loaded.Loaded && loaded.Proposal is not null &&
                   loaded.Proposal.Request.NpcFormId == new FormId(0x800) &&
                   loaded.Proposal.Request.Source.PackageManifest ==
                   new WorkspacePath(packageManifest) &&
                   loaded.Proposal.Request.ExternalAuthorities.Providers
                       .Length == 2 &&
                   loaded.Proposal.SourceSnapshot.RecordInventory.Length == 5 &&
                   loaded.Proposal.NextFormId == new FormId(0x808) &&
                   !loaded.Proposal.RuntimeAuthority,
                "A complete strict proposal did not retain its nested typed shape.");

            JsonObject proposalProviderAlias = Clone(validProposal);
            proposalProviderAlias["request"]!["externalAuthorities"]![
                "providers"]![0]!["path"] =
                Path.Combine(scratch, "Update.esm").ToUpperInvariant();
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                "Update.esm",
                proposalProviderAlias,
                "A proposal document path that case-aliased a provider was accepted.",
                expectedDiagnosticCode:
                    "follower-finish-proposal-invalid",
                expectedMessageFragment:
                    "loaded proposal document path must be pairwise disjoint from provider authority Update.esm");

            JsonObject proposalPlacementAlias = Clone(validProposal);
            proposalPlacementAlias["request"]!["externalAuthorities"]![
                "placementEvidence"]!["path"] =
                Path.Combine(scratch, "placement-evidence.json")
                    .ToUpperInvariant();
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                "placement-evidence.json",
                proposalPlacementAlias,
                "A proposal document path that case-aliased placement evidence was accepted.",
                expectedDiagnosticCode:
                    "follower-finish-proposal-invalid",
                expectedMessageFragment:
                    "loaded proposal document path must be pairwise disjoint from placement-evidence authority");

            string proposalDocumentOutputRoot = Path.Combine(
                scratch,
                "proposal-document-output-root");
            Directory.CreateDirectory(proposalDocumentOutputRoot);
            JsonObject proposalOutputAncestor = Clone(validProposal);
            proposalOutputAncestor["request"]!["outputRoot"] =
                proposalDocumentOutputRoot;
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                Path.Combine(
                    "proposal-document-output-root",
                    "proposal-under-output-root.json"),
                proposalOutputAncestor,
                "A proposal document below its nested output root was accepted.",
                expectedDiagnosticCode:
                    "follower-finish-proposal-invalid",
                expectedMessageFragment:
                    "loaded proposal document path must be pairwise disjoint from output root");

            JsonObject missingNestedManifest = Clone(validProposal);
            missingNestedManifest["request"]!["source"]!
                .AsObject()
                .Remove("packageManifest");
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                "proposal-missing-package-manifest.json",
                missingNestedManifest,
                "A proposal whose nested request omitted the package manifest was accepted.");

            JsonObject missingNestedAuthorities = Clone(validProposal);
            missingNestedAuthorities["request"]!
                .AsObject()
                .Remove("externalAuthorities");
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                "proposal-missing-external-authorities.json",
                missingNestedAuthorities,
                "A proposal whose nested request omitted external authorities was accepted.");

            JsonObject forbiddenExistingChange = Clone(validProposal);
            forbiddenExistingChange["existingRecordChanges"]!.AsArray().Add(
                "LAND 0x00001234: unrelated terrain mutation");
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                "forbidden-existing-change.json",
                forbiddenExistingChange,
                "A proposal widened the request's existing-record change surface.");

            JsonObject forbiddenNewRecord = Clone(validProposal);
            forbiddenNewRecord["newRecords"]!.AsArray().Add(
                "ARMO 0x00000808");
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                "forbidden-new-record.json",
                forbiddenNewRecord,
                "A proposal widened the request's new-record surface.");

            (string Name, Action<JsonObject> Mutate)[] snapshotMutations =
            [
                ("plugin", proposal =>
                    proposal["sourceSnapshot"]!["plugin"] = "Other.esp"),
                ("plugin-hash", proposal =>
                    proposal["sourceSnapshot"]!["pluginSha256"] =
                        new string('9', 64)),
                ("hair-rgb", proposal =>
                    proposal["sourceSnapshot"]!["hairPackedRgb"] = 0xD6BE83),
                ("hair-link", proposal =>
                    proposal["sourceSnapshot"]!["actorHairColor"] =
                        "BrigitteBardotNpcManager.esp|0x00000802"),
                ("faction-rank", proposal =>
                    proposal["sourceSnapshot"]!["factionRanks"]![0]!["rank"] =
                        1),
                ("relationship-rank", proposal =>
                    proposal["sourceSnapshot"]!["relationshipRank"] =
                        "Friend"),
                ("relationship-raw-rank", proposal =>
                    proposal["sourceSnapshot"]![
                        "relationshipRankRawDiscriminator"] = 2)
            ];
            foreach ((string name, Action<JsonObject> mutate) in
                     snapshotMutations)
            {
                JsonObject mismatch = Clone(validProposal);
                mutate(mismatch);
                await AssertFollowerFinishProposalRefused(
                    loader,
                    scratch,
                    $"snapshot-{name}.json",
                    mismatch,
                    $"A proposal with mismatched snapshot {name} identity was accepted.");
            }

            JsonObject nextMismatch = Clone(validProposal);
            nextMismatch["nextFormId"] = "0x00000809";
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                "next-mismatch.json",
                nextMismatch,
                "A proposal NextFormID inconsistent with the request was accepted.");

            JsonObject runtimeAuthority = Clone(validProposal);
            runtimeAuthority["runtimeAuthority"] = true;
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                "runtime-authority.json",
                runtimeAuthority,
                "A proposal claiming runtime authority was accepted.");

            foreach ((string value, string name) in new[]
                     {
                         (@"Data\plugin.esp", "backslash"),
                         ("../plugin.esp", "traversal"),
                         ("Data/plugin.esp:stream", "ads")
                     })
            {
                JsonObject unsafeAllowlist = Clone(validProposal);
                unsafeAllowlist["allowedPackageFiles"]![0] = value;
                await AssertFollowerFinishProposalRefused(
                    loader,
                    scratch,
                    $"allowlist-{name}.json",
                    unsafeAllowlist,
                    $"A {name} proposal package path was accepted.");
            }

            JsonObject duplicateAllowlist = Clone(validProposal);
            duplicateAllowlist["allowedPackageFiles"]![1] =
                "DATA/BRIGITTEBARDOTNPCMANAGER.ESP";
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                "allowlist-duplicate.json",
                duplicateAllowlist,
                "A case-insensitive duplicate proposal package path was accepted.");

            JsonObject unknown = Clone(validProposal);
            unknown["unexpected"] = true;
            await AssertFollowerFinishProposalRefused(
                loader,
                scratch,
                "proposal-unknown.json",
                unknown,
                "An unknown proposal property was accepted.");

            byte[] duplicateBytes = JsonSerializer.SerializeToUtf8Bytes(
                validProposal);
            string duplicateJson = System.Text.Encoding.UTF8.GetString(
                    duplicateBytes)
                .Replace(
                    "\"runtimeAuthority\":false",
                    "\"runtimeAuthority\":false,\"runtimeAuthority\":false",
                    StringComparison.Ordinal);
            var duplicatePath = new WorkspacePath(
                Path.Combine(scratch, "proposal-duplicate.json"));
            await File.WriteAllTextAsync(duplicatePath.Value, duplicateJson);
            Sha256Hash duplicateHash = Hash(
                await File.ReadAllBytesAsync(duplicatePath.Value));
            SkyrimFollowerFinishProposalLoadResult duplicate =
                await loader.LoadProposalAsync(
                    duplicatePath,
                    duplicateHash,
                    CancellationToken.None);
            Assert(!duplicate.Loaded && duplicate.Proposal is null,
                "A duplicate proposal property was accepted.");

            SkyrimFollowerFinishProposalLoadResult stale =
                await loader.LoadProposalAsync(
                    proposalPath,
                    new Sha256Hash(new string('0', 64)),
                    CancellationToken.None);
            Assert(!stale.Loaded && stale.Proposal is null,
                "A stale proposal-file hash was accepted.");
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    private static JsonObject CreateFollowerFinishRequestDocument(
        string sourceZip,
        string packageManifest,
        JsonObject externalAuthorities,
        string outputRoot,
        string outputZip) =>
        new()
        {
            ["schemaVersion"] = 1,
            ["operation"] = "skyrim-simple-follower-finish",
            ["source"] = new JsonObject
            {
                ["zip"] = sourceZip,
                ["zipByteLength"] = 4,
                ["zipSha256"] = new string('1', 64),
                ["packageManifest"] = packageManifest,
                ["packageManifestSha256"] = new string('2', 64),
                ["plugin"] = "BrigitteBardotNpcManager.esp",
                ["pluginSha256"] = new string('3', 64),
                ["faceGeomSha256"] = new string('4', 64),
                ["faceTintSha256"] = new string('5', 64)
            },
            ["externalAuthorities"] = externalAuthorities.DeepClone(),
            ["npcEditorId"] = "BrigitteBardotNpcManager",
            ["npcFormId"] = "0x00000800",
            ["occupiedLocalFormIds"] = new JsonArray(
                "0x00000800",
                "0x00000801",
                "0x00000802",
                "0x00000803",
                "0x00000804"),
            ["expectedRace"] = "COR_AllRace.esp|0x0005A184",
            ["expectedBodyRoute"] = "inherited-cotr",
            ["expectedDefaultOutfitNull"] = true,
            ["expectedFactionRanks"] = new JsonArray(
                new JsonObject
                {
                    ["faction"] = "Skyrim.esm|0x0005C84D",
                    ["rank"] = 0
                },
                new JsonObject
                {
                    ["faction"] = "Skyrim.esm|0x0005C84E",
                    ["rank"] = -1
                }),
            ["relationshipFormId"] = "0x00000804",
            ["expectedRelationshipRank"] = "Ally",
            ["expectedRelationshipRankRawDiscriminator"] = 1,
            ["hair"] = new JsonObject
            {
                ["colorFormId"] = "0x00000801",
                ["oldPackedRgb"] = 0x94876A,
                ["newPackedRgb"] = 0xD6BE83
            },
            ["setEslFlag"] = true,
            ["compactFormIds"] = false,
            ["sandbox"] = new JsonObject
            {
                ["procedure"] = "bounded-exterior-sandbox",
                ["radius"] = 768,
                ["schedule"] = "continuous",
                ["target"] =
                    "BrigitteBardotNpcManager.esp|0x00000806",
                ["condition"] =
                    "GetFactionRank(Skyrim.esm|0x0005C84E) < 0"
            },
            ["placement"] = new JsonObject
            {
                ["worldspace"] = "Skyrim.esm|0x0000003C",
                ["cell"] = "Skyrim.esm|0x0000A16A",
                ["markerBase"] = "Skyrim.esm|0x0000003B",
                ["actor"] = Transform(1, 2, 3, 0, 0, 90),
                ["anchor"] = Transform(4, 5, 6, 0, 0, 0)
            },
            ["allocation"] = new JsonObject
            {
                ["package"] = "0x00000805",
                ["anchor"] = "0x00000806",
                ["actor"] = "0x00000807",
                ["nextFormId"] = "0x00000808"
            },
            ["allowedNewRecords"] = new JsonArray(
                "PACK 0x00000805",
                "REFR 0x00000806",
                "ACHR 0x00000807"),
            ["allowedExistingRecordChanges"] = new JsonArray(
                "TES4: set ESL flag and mechanical header metadata",
                "CLFM 0x00000801: 0x94876A -> 0xD6BE83",
                "NPC_ 0x00000800: add PKID 0x00000805"),
            ["allowedPackageFiles"] = new JsonArray(
                "Data/BrigitteBardotNpcManager.esp",
                "Data/meshes/actors/character/facegendata/facegeom/BrigitteBardotNpcManager.esp/00000800.nif",
                "Data/textures/actors/character/facegendata/facetint/BrigitteBardotNpcManager.esp/00000800.dds",
                "npcmanager-package.json"),
            ["outputRoot"] = outputRoot,
            ["outputZip"] = outputZip,
            ["narrative"] =
                "A peaceful blonde follower keeps vigil near Markarth Stables."
        };

    private static async Task<JsonObject>
        CreateFollowerFinishExternalAuthoritiesAsync(string scratch)
    {
        byte[] placementBytes = System.Text.Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1}");
        byte[] skyrimBytes = [0x53, 0x4B, 0x59, 0x52, 0x49, 0x4D];
        byte[] updateBytes = [0x55, 0x50, 0x44, 0x41, 0x54, 0x45];
        string placementPath = Path.Combine(
            scratch,
            "placement-evidence.json");
        string skyrimPath = Path.Combine(scratch, "Skyrim.esm");
        string updatePath = Path.Combine(scratch, "Update.esm");
        await File.WriteAllBytesAsync(placementPath, placementBytes);
        await File.WriteAllBytesAsync(skyrimPath, skyrimBytes);
        await File.WriteAllBytesAsync(updatePath, updateBytes);
        return new JsonObject
        {
            ["placementEvidence"] = new JsonObject
            {
                ["path"] = placementPath,
                ["byteLength"] = placementBytes.LongLength,
                ["sha256"] = Hash(placementBytes).Value
            },
            ["providers"] = new JsonArray(
                new JsonObject
                {
                    ["plugin"] = "Update.esm",
                    ["path"] = updatePath,
                    ["byteLength"] = updateBytes.LongLength,
                    ["sha256"] = Hash(updateBytes).Value
                },
                new JsonObject
                {
                    ["plugin"] = "Skyrim.esm",
                    ["path"] = skyrimPath,
                    ["byteLength"] = skyrimBytes.LongLength,
                    ["sha256"] = Hash(skyrimBytes).Value
                })
        };
    }

    private static JsonObject CreateFollowerFinishProposalDocument(
        JsonObject request) =>
        new()
        {
            ["schemaVersion"] = 1,
            ["operation"] = "skyrim-simple-follower-finish",
            ["requestSha256"] = new string('6', 64),
            ["request"] = request.DeepClone(),
            ["sourceSnapshot"] = new JsonObject
            {
                ["valid"] = true,
                ["plugin"] = "BrigitteBardotNpcManager.esp",
                ["pluginSha256"] = new string('3', 64),
                ["tes4Flags"] = 0,
                ["masters"] = new JsonArray(
                    "Skyrim.esm",
                    "COR_AllRace.esp"),
                ["nextFormId"] = "0x00000805",
                ["recordInventory"] = new JsonArray(
                    "NPC_ 0x00000800",
                    "CLFM 0x00000801",
                    "TXST 0x00000802",
                    "HDPT 0x00000803",
                    "RELA 0x00000804"),
                ["actorSubrecordDigests"] = new JsonArray(
                    "RNAM=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "HCLF=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
                ["hairPackedRgb"] = 0x94876A,
                ["actorHairColor"] =
                    "BrigitteBardotNpcManager.esp|0x00000801",
                ["defaultOutfitNull"] = true,
                ["factionRanks"] = new JsonArray(
                    new JsonObject
                    {
                        ["faction"] = "Skyrim.esm|0x0005C84D",
                        ["rank"] = 0
                    },
                    new JsonObject
                    {
                        ["faction"] = "Skyrim.esm|0x0005C84E",
                        ["rank"] = -1
                    }),
                ["relationshipRank"] = "Ally",
                ["relationshipRankRawDiscriminator"] = 1,
                ["absentSignatures"] = new JsonArray(
                    "PACK",
                    "CELL",
                    "WRLD",
                    "ACHR",
                    "REFR"),
                ["diagnostics"] = new JsonArray(
                    new JsonObject
                    {
                        ["code"] = "source-valid",
                        ["severity"] = "info",
                        ["message"] = "Source snapshot is valid."
                    })
            },
            ["existingRecordChanges"] = new JsonArray(
                "TES4: set ESL flag and mechanical header metadata",
                "CLFM 0x00000801: 0x94876A -> 0xD6BE83",
                "NPC_ 0x00000800: add PKID 0x00000805"),
            ["newRecords"] = new JsonArray(
                "PACK 0x00000805",
                "REFR 0x00000806",
                "ACHR 0x00000807"),
            ["nextFormId"] = "0x00000808",
            ["rawGroupTreeSurface"] = new JsonArray(
                "TES4",
                "GRUP/NPC_",
                "GRUP/PACK",
                "GRUP/WRLD/CELL/REFR",
                "GRUP/WRLD/CELL/ACHR"),
            ["allowedPackageFiles"] =
                request["allowedPackageFiles"]!.DeepClone(),
            ["runtimeAuthority"] = false
        };

    private static JsonObject Transform(
        double x,
        double y,
        double z,
        double rotationX,
        double rotationY,
        double rotationZ) =>
        new()
        {
            ["x"] = x,
            ["y"] = y,
            ["z"] = z,
            ["rotationX"] = rotationX,
            ["rotationY"] = rotationY,
            ["rotationZ"] = rotationZ
        };

    private static JsonObject Clone(JsonObject value) =>
        value.DeepClone().AsObject();

    private static async Task<(WorkspacePath Path, Sha256Hash Sha256)>
        WriteFollowerFinishJson(
            string scratch,
            string fileName,
            JsonObject document)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        var path = new WorkspacePath(Path.Combine(scratch, fileName));
        await File.WriteAllBytesAsync(path.Value, bytes);
        return (path, Hash(bytes));
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static async Task AssertFollowerFinishRequestRefused(
        SkyrimFollowerFinishRequestFileLoader loader,
        string scratch,
        string fileName,
        JsonObject document,
        string message,
        string? expectedDiagnosticCode = null,
        string? expectedMessageFragment = null)
    {
        (WorkspacePath path, Sha256Hash hash) = await WriteFollowerFinishJson(
            scratch,
            fileName,
            document);
        SkyrimFollowerFinishRequestLoadResult result =
            await loader.LoadRequestAsync(
                path,
                hash,
                CancellationToken.None);
        Assert(!result.Loaded && result.Request is null &&
               result.Diagnostics.Any(diagnostic =>
                   diagnostic.Severity == DiagnosticSeverity.Error &&
                   (expectedDiagnosticCode is null ||
                    diagnostic.Code == expectedDiagnosticCode) &&
                   (expectedMessageFragment is null ||
                    diagnostic.Message.Contains(
                        expectedMessageFragment,
                        StringComparison.Ordinal))),
            message);
    }

    private static async Task AssertFollowerFinishProposalRefused(
        SkyrimFollowerFinishRequestFileLoader loader,
        string scratch,
        string fileName,
        JsonObject document,
        string message,
        string? expectedDiagnosticCode = null,
        string? expectedMessageFragment = null)
    {
        (WorkspacePath path, Sha256Hash hash) = await WriteFollowerFinishJson(
            scratch,
            fileName,
            document);
        SkyrimFollowerFinishProposalLoadResult result =
            await loader.LoadProposalAsync(
                path,
                hash,
                CancellationToken.None);
        Assert(!result.Loaded && result.Proposal is null &&
               result.Diagnostics.Any(diagnostic =>
                   diagnostic.Severity == DiagnosticSeverity.Error &&
                   (expectedDiagnosticCode is null ||
                    diagnostic.Code == expectedDiagnosticCode) &&
                   (expectedMessageFragment is null ||
                    diagnostic.Message.Contains(
                        expectedMessageFragment,
                        StringComparison.Ordinal))),
            message);
    }
}
