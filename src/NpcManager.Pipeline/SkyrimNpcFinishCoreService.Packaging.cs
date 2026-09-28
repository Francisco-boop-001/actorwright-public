using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimNpcFinishCoreService
{
    private static readonly DateTimeOffset DeterministicZipTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private ImmutableArray<Diagnostic> ValidateTransactionPaths(
        SkyrimNpcFinishCoreRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Source.PackageRoot is not { } sourceRoot ||
            request.Source.PluginPath is not { } pluginPath ||
            request.Source.Plugin is not { } plugin ||
            request.Output.Root is not { } outputRoot ||
            request.Output.Archive is not { } archive ||
            request.SandboxAuthority.CopiedMaster is not { } copiedMaster)
        {
            diagnostics.Add(new Diagnostic(
                "finish-core-transaction-paths",
                DiagnosticSeverity.Error,
                "Source, copied-master, and output paths must be fully bound."));
            return diagnostics.ToImmutable();
        }

        if (!sourceRoot.IsUnder(projectRoot) || !pluginPath.IsUnder(sourceRoot) ||
            !copiedMaster.IsUnder(projectRoot) || !outputRoot.IsUnder(projectRoot) ||
            !archive.IsUnder(projectRoot))
            diagnostics.Add(new Diagnostic(
                "finish-core-transaction-outside-project",
                DiagnosticSeverity.Error,
                "Finish Core transaction paths must remain below the K-local project root."));
        if (!TryGetPackageRelativePluginPath(
                sourceRoot.Value, pluginPath.Value, plugin.Value, out _))
            diagnostics.Add(new Diagnostic(
                "finish-core-transaction-plugin-path",
                DiagnosticSeverity.Error,
                "The bound source plugin must be a safe package-relative member with its original filename."));
        if (!Directory.Exists(sourceRoot.Value) || !File.Exists(pluginPath.Value) ||
            !File.Exists(copiedMaster.Value))
            diagnostics.Add(new Diagnostic(
                "finish-core-transaction-source-missing",
                DiagnosticSeverity.Error,
                "The source package, plugin, and copied master must exist before apply."));
        if (File.Exists(outputRoot.Value) || Directory.Exists(outputRoot.Value))
            diagnostics.Add(new Diagnostic(
                "finish-core-transaction-output-exists",
                DiagnosticSeverity.Error,
                "Finish Core never overwrites an existing output root."));
        if (File.Exists(archive.Value) || Directory.Exists(archive.Value))
            diagnostics.Add(new Diagnostic(
                "finish-core-transaction-archive-exists",
                DiagnosticSeverity.Error,
                "Finish Core never overwrites an existing archive."));
        string? outputParent = Path.GetDirectoryName(outputRoot.Value);
        string? archiveParent = Path.GetDirectoryName(archive.Value);
        if (outputParent is null || !Directory.Exists(outputParent) ||
            archiveParent is null || !Directory.Exists(archiveParent))
            diagnostics.Add(new Diagnostic(
                "finish-core-transaction-parent-missing",
                DiagnosticSeverity.Error,
                "Output and archive parents must already exist."));
        if (outputRoot.IsUnder(sourceRoot) || sourceRoot.IsUnder(outputRoot))
            diagnostics.Add(new Diagnostic(
                "finish-core-transaction-root-overlap",
                DiagnosticSeverity.Error,
                "Source and output package roots must be distinct and non-overlapping."));
        if (archive.IsUnder(outputRoot) || outputRoot.IsUnder(archive))
            diagnostics.Add(new Diagnostic(
                "finish-core-transaction-archive-overlap",
                DiagnosticSeverity.Error,
                "The archive path must be separate from the promoted package root."));
        if (reparseAncestorProbe(sourceRoot.Value) ||
            reparseAncestorProbe(pluginPath.Value) ||
            reparseAncestorProbe(copiedMaster.Value) ||
            (outputParent is not null && reparseAncestorProbe(outputParent)) ||
            (archiveParent is not null && reparseAncestorProbe(archiveParent)))
            diagnostics.Add(new Diagnostic(
                "finish-core-transaction-reparse",
                DiagnosticSeverity.Error,
                "Source and copied-master authorities may not traverse reparse points."));
        return diagnostics.ToImmutable();
    }

    private static async ValueTask CopyPackageTreeAsync(
        string sourceRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        foreach (string directory in EnumerateOrdinaryDirectories(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceRoot, directory);
            string destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(destination);
        }
        foreach (string file in EnumerateOrdinaryFiles(sourceRoot)
                     .OrderBy(path => Path.GetRelativePath(sourceRoot, path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(sourceRoot, file);
            string destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using FileStream input = new(
                file, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream output = new(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

    private static SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding
        BuildPromotedOutputBinding(
            RaceMenuSelectedDependencyManifestArtifact source,
            PluginName outputPlugin,
            Sha256Hash outputHash,
            long outputLength,
            WorkspacePath outputPluginPath,
            FormId targetFormId,
            ImmutableArray<PluginName> expectedMasterOrder)
    {
        ImmutableArray<RaceMenuSelectedDependencyManifestExternalInstallDependency> groups =
            source.ExternalInstallDependencies;
        RaceMenuSelectedDependencyManifestOutputPluginBinding first =
            groups[0].OutputPlugin;
        if (groups.Any(group => !group.OutputPlugin.Masters.SequenceEqual(first.Masters)))
            throw new InvalidDataException(
                "External Finish Core groups disagree on source master order.");
        ImmutableArray<FormReference> authorized = groups
            .SelectMany(group => group.OutputPlugin.PnamBindings)
            .ToImmutableArray();
        SkyrimMod output = SkyrimMod.CreateFromBinary(
            new ModPath(
                ModKey.FromNameAndExtension(outputPlugin.Value),
                new FilePath(outputPluginPath.Value)),
            SkyrimRelease.SkyrimSE);
        ImmutableArray<PluginName> finalMasters = output.ModHeader.MasterReferences
            .Select(item => new PluginName(item.Master.ToString())).ToImmutableArray();
        if (!expectedMasterOrder.IsDefault &&
            !finalMasters.SequenceEqual(expectedMasterOrder))
            throw new InvalidDataException(
                "The promoted output master order differs from the admitted Finish master plan.");
        FormKey target = new(
            ModKey.FromNameAndExtension(outputPlugin.Value), targetFormId.Value);
        INpcGetter npc = output.Npcs.Single(item => item.FormKey == target);
        HashSet<string> providers = groups.Select(group => group.Descriptor.Provider.Plugin.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ImmutableArray<FormReference> pnam = npc.HeadParts.Select(item =>
                new FormReference(
                    new PluginName(item.FormKey.ModKey.ToString()),
                    new FormId(item.FormKey.ID)))
            .Where(item => providers.Contains(item.Plugin.Value))
            .ToImmutableArray();
        if (!pnam.SequenceEqual(authorized))
            throw new InvalidDataException(
                "The promoted output PNAM sequence is not the exact authorized external closure.");
        return new(
            SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding.SchemaIdentifierValue,
            new AssetPath(SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath),
            source.ManifestSha256,
            groups.Select(group => new SkyrimNpcFinishCoreExternalHeadPartPromotedOutputGroupBinding(
                group.Descriptor.DescriptorId,
                group.Attestation.AttestationSha256,
                outputPlugin,
                outputHash,
                outputLength,
                finalMasters,
                group.OutputPlugin.PnamBindings)).ToImmutableArray(),
            outputPlugin,
            outputHash,
            outputLength,
            finalMasters,
            pnam);
    }

    private static bool TryGetPackageRelativePluginPath(
        string packageRoot,
        string pluginPath,
        string pluginName,
        out string relativePath)
    {
        relativePath = Path.GetRelativePath(
                Path.GetFullPath(packageRoot), Path.GetFullPath(pluginPath))
            .Replace('\\', '/');
        return relativePath.Length > 0 &&
               !Path.IsPathRooted(relativePath) &&
               !string.Equals(relativePath, ".", StringComparison.Ordinal) &&
               !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
               !relativePath.StartsWith("../", StringComparison.Ordinal) &&
               !relativePath.Contains("/../", StringComparison.Ordinal) &&
               !relativePath.Contains("/./", StringComparison.Ordinal) &&
               string.Equals(Path.GetFileName(relativePath), pluginName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string RequirePackageRelativePluginPath(
        SkyrimNpcFinishCoreRequest request)
    {
        if (request.Source.PackageRoot is not { } packageRoot ||
            request.Source.PluginPath is not { } pluginPath ||
            request.Source.Plugin is not { } plugin ||
            !TryGetPackageRelativePluginPath(
                packageRoot.Value, pluginPath.Value, plugin.Value, out string relativePath))
            throw new InvalidDataException(
                "finish-core-transaction-plugin-path: the bound source plugin path is not a safe package member.");
        return relativePath;
    }

    private async ValueTask<SkyrimNpcFinishCoreManifest> PublishPackageAsync(
        SkyrimNpcFinishCoreRequest request,
        SkyrimNpcFinishCoreProposal proposal,
        Sha256Hash requestSha256,
        Sha256Hash proposalSha256,
        WorkspacePath stagingRoot,
        WorkspacePath outputPlugin,
        BethesdaSkyrimNpcFinishCoreVerification binaryVerification,
        Sha256Hash sourceTreeAuthority,
        WorkspacePath archivePath,
        ExternalFinishCoreState? externalState,
        Action<ISkyrimNpcFinishCoreOwnedFileLease> archiveTemporaryCreated,
        CancellationToken cancellationToken)
    {
        await RetainFinishedSourceManifestAsync(request, stagingRoot, cancellationToken);
        ImmutableArray<SkyrimNpcFinishCoreInheritedEvidenceEntry> inherited =
            await RelocateInheritedEvidenceAsync(
                stagingRoot.Value, request.Source.PackageTreeSha256!.Value, cancellationToken)
                .ConfigureAwait(false);
        string evidenceRoot = Path.Combine(stagingRoot.Value, "NPCManager", "Evidence");
        Directory.CreateDirectory(evidenceRoot);
        string editorId = request.Actor.EditorId!.Value.Value;
        string diagName = "diag-" + editorId + ".txt";
        string requestPath = Path.Combine(evidenceRoot, "finish-core-request.json");
        string proposalPath = Path.Combine(evidenceRoot, "finish-core-proposal.json");
        string manifestPath = Path.Combine(evidenceRoot, "finish-core-manifest.json");
        string verificationPath = Path.Combine(evidenceRoot, "finish-core-verification.json");
        string runtimePath = Path.Combine(evidenceRoot, "runtime-identities.json");
        string diagPath = Path.Combine(evidenceRoot, diagName);
        await WriteNewBytesAsync(requestPath,
            SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, projectRoot), cancellationToken);
        await WriteNewBytesAsync(proposalPath,
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(proposal, projectRoot), cancellationToken);
        await WriteNewBytesAsync(runtimePath, Encoding.UTF8.GetBytes(
            "{\"baseNpc\":\"" + request.Source.Plugin!.Value + "|" +
            request.Actor.FormId!.Value + "\",\"placedReference\":null,\"placementIncluded\":false}\n"),
            cancellationToken);
        await WriteNewBytesAsync(diagPath, Encoding.UTF8.GetBytes(
            "help \"" + editorId + "\" 4\r\n"), cancellationToken);
        await WriteNewBytesAsync(Path.Combine(stagingRoot.Value, "README-Finish-Core.txt"),
            Encoding.UTF8.GetBytes(
                "NPC Manager Finish Core\r\n" +
                "Placement is not included. This package contains no ACHR or CELL records.\r\n" +
                "runtimeAuthority=false; visualAuthority=false; runtime proof remains open.\r\n"),
            cancellationToken);

        Sha256Hash sourceTreeHash = sourceTreeAuthority;
        Sha256Hash packageTreeHash = ComputeFinishOutputTree(
            stagingRoot.Value, RequirePackageRelativePluginPath(request), generatedManifest: true);
        Sha256Hash pluginHash = await HashFileAsync(outputPlugin.Value, cancellationToken);
        ImmutableArray<string> evidencePaths =
            new[] { requestPath, proposalPath, runtimePath, diagPath }
                .Concat(externalState is null
                    ? []
                    : [Path.Combine(stagingRoot.Value,
                        SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath
                            .Replace('/', Path.DirectorySeparatorChar)),
                       Path.Combine(stagingRoot.Value,
                        SkyrimNpcFinishCoreDocumentCodec.CanonicalPromotedOutputBindingPath
                            .Replace('/', Path.DirectorySeparatorChar))])
                .ToImmutableArray();
        ImmutableArray<SkyrimNpcFinishCoreEvidenceEntry> evidence = evidencePaths
            .Select(path => ReadStableEvidenceEntry(stagingRoot.Value, path))
            .ToImmutableArray();
        var runtimeIdentity = new SkyrimNpcFinishCoreRuntimeIdentity
        {
            BaseNpc = new FormReference(request.Source.Plugin!.Value, request.Actor.FormId!.Value),
            PlacedReference = null,
            PlacementIncluded = false
        };
        SkyrimNpcFinishCoreExternalHeadPartManifestAuthority? externalManifest = null;
        if (externalState is not null)
        {
            ExternalHeadPartVerifiedInstallSnapshot snapshot =
                externalState.Result.Artifact.VerifiedInstallSnapshot ??
                throw new InvalidDataException(
                    "External Finish Core apply did not retain a verified install snapshot.");
            externalManifest = new SkyrimNpcFinishCoreExternalHeadPartManifestAuthority(
                request.Authorities.ExternalHeadParts!.SelectedManifestPath,
                externalState.SelectedManifest.ManifestSha256,
                externalState.Descriptors,
                externalState.Attestations,
                snapshot)
            {
                PromotedOutputBindingPath = new AssetPath(
                    SkyrimNpcFinishCoreDocumentCodec.CanonicalPromotedOutputBindingPath),
                PromotedOutputBindingSha256 = ReadStableFile(
                    Path.Combine(
                        stagingRoot.Value,
                        SkyrimNpcFinishCoreDocumentCodec.CanonicalPromotedOutputBindingPath
                            .Replace('/', Path.DirectorySeparatorChar)))
                    .Sha256
            };
        }
        var manifest = new SkyrimNpcFinishCoreManifest
        {
            Schema = externalManifest is null
                ? SkyrimNpcFinishCoreManifest.SchemaIdentifier
                : SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier,
            Plugin = request.Source.Plugin,
            PluginSha256 = pluginHash,
            BaseNpc = runtimeIdentity.BaseNpc,
            RequestSha256 = requestSha256,
            ProposalSha256 = proposalSha256,
            PlacementIncluded = false,
            RuntimeAuthority = false,
            VisualAuthority = false,
            PackageRoot = request.Output.Root,
            Archive = archivePath,
            SourcePackageTreeSha256 = sourceTreeHash,
            PackageTreeSha256 = packageTreeHash,
            RuntimeIdentity = runtimeIdentity,
            Evidence = new SkyrimNpcFinishCoreManifestEvidence
            {
                Files = evidence,
                Inherited = inherited,
                PackageTreeSha256 = packageTreeHash,
                SourcePackageTreeSha256 = sourceTreeHash
            },
            ExternalHeadParts = externalManifest
        };
        await WriteNewBytesAsync(manifestPath,
            SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(manifest, projectRoot),
            cancellationToken);
        ExternalHeadPartInstallVerificationArtifact? externalVerification = null;
        if (externalState is not null)
        {
            externalVerification = externalState.Result.Artifact with
            {
                HistoricalSnapshotValid = true,
                CurrentInstallDependencyState = ExternalInstallDependencyState.Verified,
                InstallReady = true,
                InstallDependencyAuthority = true,
                RuntimeAuthority = false,
                VisualAuthority = false
            };
        }
        var verification = new SkyrimNpcFinishCoreVerification
        {
            Schema = externalVerification is null
                ? SkyrimNpcFinishCoreVerification.SchemaIdentifier
                : SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier,
            Status = SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired,
            Verified = binaryVerification.Verified,
            PlacementIncluded = false,
            RuntimeAuthority = false,
            VisualAuthority = false,
            PluginSha256 = pluginHash,
            PackageTreeSha256 = packageTreeHash,
            SourcePackageTreeSha256 = sourceTreeHash,
            RuntimeIdentity = runtimeIdentity,
            TypedForbiddenCounts = binaryVerification.TypedForbiddenCounts,
            RawForbiddenCounts = binaryVerification.RawForbiddenCounts,
            ExternalHeadParts = externalVerification is null
                ? null
                : new SkyrimNpcFinishCoreExternalHeadPartVerification(
                    externalVerification)
        };
        await WriteNewBytesAsync(verificationPath,
            SkyrimNpcFinishCoreDocumentCodec.SerializeVerification(verification, projectRoot),
            cancellationToken);
        await PublishFinishedPackageManifestAsync(request, stagingRoot, cancellationToken);
        Sha256Hash postEvidencePackageTreeHash = ComputeFinishOutputTree(
            stagingRoot.Value, RequirePackageRelativePluginPath(request), generatedManifest: true);
        if (postEvidencePackageTreeHash != packageTreeHash)
            throw new InvalidDataException(
                $"Finish package tree changed outside excluded authority paths (expected {packageTreeHash.Value}, observed {postEvidencePackageTreeHash.Value}).");
        // Apply runs the same physical closure the separate verifier runs, so a
        // package that verify would refuse is never promoted.
        var closureDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!VerifyPhysicalEvidenceClosure(
                stagingRoot.Value,
                externalState is not null,
                BuildCanonicalEvidencePaths(request, externalState is not null),
                inherited.Select(entry => entry.Path.Value).ToImmutableArray(),
                closureDiagnostics))
            throw new InvalidDataException(
                "finish-core-apply-evidence-closure: " +
                string.Join(" | ", closureDiagnostics.Select(item => item.Code + ": " + item.Message)));

        string temporaryArchive = archivePath.Value + ".tmp";
        if (File.Exists(temporaryArchive) || Directory.Exists(temporaryArchive))
            throw new IOException("The transaction archive temporary path already exists.");
        await WriteDeterministicArchiveAsync(
            ownedLeaseFactory,
            projectRoot,
            stagingRoot.Value,
            temporaryArchive,
            archivePath.Value,
            archiveTemporaryCreated,
            cancellationToken);
        return manifest;
    }

    private static async ValueTask WriteDeterministicArchiveAsync(
        ISkyrimNpcFinishCoreOwnedLeaseFactory ownedLeaseFactory,
        WorkspacePath workspaceRoot,
        string sourceRoot,
        string archivePath,
        string destinationArchivePath,
        Action<ISkyrimNpcFinishCoreOwnedFileLease> archiveCreated,
        CancellationToken cancellationToken)
    {
        ISkyrimNpcFinishCoreOwnedFileLease owned =
            ownedLeaseFactory.CreateFile(
                workspaceRoot,
                new WorkspacePath(archivePath),
                new WorkspacePath(destinationArchivePath),
                "Finish Core archive temporary");
        archiveCreated(owned);
        await owned.WriteAsync(async (stream, token) =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true,
                Encoding.UTF8);
            var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in EnumerateOrdinaryFiles(sourceRoot)
                         .OrderBy(path => Path.GetRelativePath(sourceRoot, path), StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
                string entryName = FinishCoreArchiveEntryName(relative);
                if (!entryNames.Add(entryName))
                    throw new InvalidDataException(
                        $"Finish Core archive entry '{entryName}' is duplicated or case-aliased.");
                ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                entry.LastWriteTime = DeterministicZipTimestamp;
                await using Stream input = File.OpenRead(file);
                await using Stream output = entry.Open();
                await input.CopyToAsync(output, token);
            }
            await ValueTask.CompletedTask;
        }, cancellationToken);
        owned.EnsureCurrent();
    }

    private static string FinishCoreArchiveEntryName(string packageRelativePath) =>
        packageRelativePath.Equals("README-Finish-Core.txt", StringComparison.Ordinal)
            ? packageRelativePath
            : packageRelativePath.StartsWith("NPCManager/Evidence/",
                StringComparison.Ordinal) ||
              packageRelativePath.StartsWith("Data/NPCManager/Evidence/",
                StringComparison.Ordinal)
                ? packageRelativePath
            : packageRelativePath.StartsWith("Data/", StringComparison.Ordinal)
                ? packageRelativePath["Data/".Length..]
                : "Data/" + packageRelativePath;

    private const int MaximumInheritedEvidenceMembers = 1024;
    private const long MaximumInheritedEvidenceMemberBytes = 64L * 1024 * 1024;
    private const long MaximumInheritedEvidenceBytes = 512L * 1024 * 1024;

    /// <summary>
    /// A create-from-jslot host owns its own package-root <c>NPCManager/Evidence</c>
    /// namespace (racemenu-bundle.json, FaceGeom authority, ...). Finish never
    /// re-publishes those members in place, where the verifier's exact canonical
    /// closure would refuse them: every member present after the source copy moves
    /// under <c>NPCManager/Evidence/Inherited/&lt;source-tree-sha256-8&gt;/&lt;original path&gt;</c>
    /// and is listed hash-bound in the manifest. The whole evidence namespace is
    /// already excluded from the package-tree hash, so the relocation changes no
    /// tree-hash domain.
    /// </summary>
    private static async ValueTask<ImmutableArray<SkyrimNpcFinishCoreInheritedEvidenceEntry>> RelocateInheritedEvidenceAsync(
        string stagingRoot,
        Sha256Hash sourceTree,
        CancellationToken cancellationToken)
    {
        string evidenceRoot = Path.Combine(stagingRoot, "NPCManager", "Evidence");
        if (!Directory.Exists(evidenceRoot))
            return [];
        string[] members = EnumerateOrdinaryFiles(evidenceRoot)
            .Select(path => Path.GetRelativePath(stagingRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (members.Length == 0)
            return [];
        if (members.Length > MaximumInheritedEvidenceMembers)
            throw new InvalidDataException(
                $"finish-core-apply-inherited-evidence-budget: the source evidence namespace carries {members.Length} members; at most {MaximumInheritedEvidenceMembers} are retained.");
        string inheritedRoot = SkyrimNpcFinishCoreManifestEvidence.InheritedNamespacePrefix +
            sourceTree.Value[..8];
        var entries = ImmutableArray.CreateBuilder<SkyrimNpcFinishCoreInheritedEvidenceEntry>(members.Length);
        long totalBytes = 0;
        foreach (string sourcePath in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relocated = inheritedRoot + "/" + sourcePath;
            string from = Path.Combine(stagingRoot, sourcePath.Replace('/', Path.DirectorySeparatorChar));
            string to = Path.Combine(stagingRoot, relocated.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(to) || Directory.Exists(to))
                throw new InvalidDataException(
                    $"finish-core-apply-inherited-evidence-collision: '{relocated}' already exists in transaction staging.");
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            StableFileHash read = await HashStableBoundedFileAsync(
                to, MaximumInheritedEvidenceMemberBytes, cancellationToken)
                .ConfigureAwait(false);
            if (totalBytes > MaximumInheritedEvidenceBytes - read.ByteLength)
                throw new InvalidDataException(
                    $"finish-core-apply-inherited-evidence-budget: inherited evidence member '{sourcePath}' exceeds the bounded retention budget.");
            totalBytes += read.ByteLength;
            entries.Add(new SkyrimNpcFinishCoreInheritedEvidenceEntry
            {
                Path = new AssetPath(relocated),
                SourcePath = new AssetPath(sourcePath),
                ByteLength = read.ByteLength,
                Sha256 = read.Sha256
            });
        }
        // Directories emptied by the move never survive into the promoted package.
        string inheritedNative = Path.Combine(stagingRoot, inheritedRoot.Replace('/', Path.DirectorySeparatorChar));
        foreach (string directory in EnumerateOrdinaryDirectories(evidenceRoot)
                     .OrderByDescending(path => path.Length))
        {
            if (new WorkspacePath(directory).IsUnder(new WorkspacePath(inheritedNative)) ||
                Directory.EnumerateFileSystemEntries(directory).Any())
                continue;
            Directory.Delete(directory, recursive: false);
        }
        return entries.ToImmutable();
    }

    private static Sha256Hash ComputeTreeHash(
        string root,
        Func<string, bool> exclude,
        string? domain = null)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        if (domain is not null) hash.AppendData(Encoding.UTF8.GetBytes(domain));
        foreach (string file in EnumerateOrdinaryFiles(root)
                     .OrderBy(path => Path.GetRelativePath(root, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (exclude(relative))
                continue;
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            using var stream = File.OpenRead(file);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                hash.AppendData(buffer, 0, read);
            hash.AppendData([0]);
        }
        return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static IEnumerable<string> EnumerateOrdinaryDirectories(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new InvalidDataException(
                $"Finish Core directory '{root}' is missing or unsafe.");
        FileAttributes rootAttributes = File.GetAttributes(fullRoot);
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint) ||
            rootAttributes.HasFlag(FileAttributes.Device))
            throw new InvalidDataException(
                $"Finish Core directory '{root}' is missing or unsafe.");
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            FileAttributes currentAttributes = File.GetAttributes(current);
            if (currentAttributes.HasFlag(FileAttributes.ReparsePoint) ||
                currentAttributes.HasFlag(FileAttributes.Device))
                throw new InvalidDataException(
                    $"Finish Core path '{current}' became a reparse or device path.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         current, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    attributes.HasFlag(FileAttributes.Device))
                    throw new InvalidDataException(
                        $"Finish Core path '{entry}' is a reparse or device path.");
                if (Directory.Exists(entry))
                {
                    yield return entry;
                    pending.Push(entry);
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateOrdinaryFiles(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new InvalidDataException(
                $"Finish Core directory '{root}' is missing or unsafe.");
        FileAttributes rootAttributes = File.GetAttributes(fullRoot);
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint) ||
            rootAttributes.HasFlag(FileAttributes.Device))
            throw new InvalidDataException(
                $"Finish Core directory '{root}' is missing or unsafe.");
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            FileAttributes currentAttributes = File.GetAttributes(current);
            if (currentAttributes.HasFlag(FileAttributes.ReparsePoint) ||
                currentAttributes.HasFlag(FileAttributes.Device))
                throw new InvalidDataException(
                    $"Finish Core path '{current}' became a reparse or device path.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         current, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    attributes.HasFlag(FileAttributes.Device))
                    throw new InvalidDataException(
                        $"Finish Core path '{entry}' is a reparse or device path.");
                if (Directory.Exists(entry))
                    pending.Push(entry);
                else if (File.Exists(entry))
                    yield return entry;
            }
        }
    }

    private static Sha256Hash HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static SkyrimNpcFinishCoreEvidenceEntry ReadStableEvidenceEntry(
        string root,
        string path)
    {
        StableFileRead read = ReadStableFile(path);
        return new SkyrimNpcFinishCoreEvidenceEntry
        {
            Path = new AssetPath(Path.GetRelativePath(root, path).Replace('\\', '/')),
            ByteLength = read.Bytes.LongLength,
            Sha256 = read.Sha256
        };
    }

    private static StableFileRead ReadStableFile(string path)
    {
        FileInfo before = new(path);
        if (!before.Exists || before.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            before.Attributes.HasFlag(FileAttributes.Device))
            throw new InvalidDataException(
                $"Finish Core evidence file '{path}' is missing or unsafe.");
        byte[] bytes = File.ReadAllBytes(path);
        FileInfo after = new(path);
        if (!after.Exists || after.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            after.Attributes.HasFlag(FileAttributes.Device) ||
            before.Length != after.Length || bytes.LongLength != after.Length)
            throw new InvalidDataException(
                $"Finish Core evidence file '{path}' changed while it was read.");
        return new StableFileRead(
            bytes,
            new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))));
    }

    private sealed record StableFileRead(byte[] Bytes, Sha256Hash Sha256);

    private sealed record StableFileHash(long ByteLength, Sha256Hash Sha256);

    private static async ValueTask<StableFileHash> HashStableBoundedFileAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        FileInfo before = new(path);
        if (!before.Exists || before.Length < 0 || before.Length > maximumBytes ||
            before.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            before.Attributes.HasFlag(FileAttributes.Device))
            throw new InvalidDataException(
                $"Finish Core evidence file '{path}' is missing, unsafe, or exceeds its byte limit.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != before.Length || stream.Length > maximumBytes)
            throw new InvalidDataException(
                $"Finish Core evidence file '{path}' changed before it was read.");
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(buffer.Length, maximumBytes - total + 1);
            int read = await stream.ReadAsync(
                buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException(
                    $"Finish Core evidence file '{path}' grew beyond its byte limit while it was read.");
            hash.AppendData(buffer, 0, read);
        }
        FileInfo after = new(path);
        if (!after.Exists || after.Length != before.Length || total != after.Length ||
            after.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            after.Attributes.HasFlag(FileAttributes.Device))
            throw new InvalidDataException(
                $"Finish Core evidence file '{path}' changed while it was read.");
        return new StableFileHash(
            total,
            new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset())));
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)));
    }

    private static async ValueTask WriteNewBytesAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.Device);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private static bool HasReparseAncestor(string path)
    {
        try
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (HasReparsePoint(current))
                    return true;
                string? parent = Path.GetDirectoryName(current);
                if (parent is null || string.Equals(parent, current,
                        StringComparison.OrdinalIgnoreCase))
                    break;
                current = parent;
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or InvalidDataException or
            InvalidOperationException or NotSupportedException)
        {
            return true;
        }
    }

    private static bool TryDeleteDirectory(
        string projectRoot,
        string path,
        string expectedParent,
        Func<string, bool> reparseAncestorProbe,
        Func<string, bool> reparseDescendantProbe,
        out string? failure)
    {
        failure = null;
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(projectRoot).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parentRoot = Path.GetFullPath(expectedParent).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(full);
            if (parent is null || !full.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith(".finish-core-transaction-",
                    StringComparison.Ordinal) ||
                !string.Equals(Path.GetFullPath(parent), parentRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                reparseAncestorProbe(full) || reparseDescendantProbe(full))
            {
                failure = $"Refused unsafe Finish transaction cleanup path '{path}'.";
                return false;
            }
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(full);
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.Device) ||
                !attributes.HasFlag(FileAttributes.Directory))
            {
                failure = $"Refused to clean Finish transaction path '{path}' because its owned directory identity changed.";
                return false;
            }
            Directory.Delete(full, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or InvalidDataException or
            InvalidOperationException or NotSupportedException)
        {
            failure = $"Could not safely clean Finish transaction path '{path}': {exception.Message}";
            return false;
        }
    }

    private static bool TryDeleteFile(
        string projectRoot,
        string? archivePath,
        Func<string, bool> reparseAncestorProbe,
        out string? failure)
    {
        failure = null;
        try
        {
            if (string.IsNullOrWhiteSpace(archivePath))
                return true;
            string full = Path.GetFullPath(archivePath);
            string root = Path.GetFullPath(projectRoot).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? parent = Path.GetDirectoryName(full);
            if (parent is null || !full.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || reparseAncestorProbe(full) ||
                HasReparsePoint(full))
            {
                failure = $"Refused unsafe Finish archive cleanup path '{archivePath}'.";
                return false;
            }
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(full);
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.Device) ||
                attributes.HasFlag(FileAttributes.Directory))
            {
                failure = $"Refused to clean Finish archive path '{archivePath}' because its owned file identity changed.";
                return false;
            }
            File.Delete(full);
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or InvalidDataException or
            InvalidOperationException or NotSupportedException)
        {
            failure = $"Could not safely clean Finish archive path '{archivePath}': {exception.Message}";
            return false;
        }
    }

    private static bool HasDescendantReparsePoint(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return false;
            var pending = new Stack<string>();
            pending.Push(Path.GetFullPath(root));
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                FileAttributes currentAttributes = File.GetAttributes(current);
                if (currentAttributes.HasFlag(FileAttributes.ReparsePoint) ||
                    currentAttributes.HasFlag(FileAttributes.Device))
                    return true;
                foreach (string entry in Directory.EnumerateFileSystemEntries(
                             current, "*", SearchOption.TopDirectoryOnly))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        attributes.HasFlag(FileAttributes.Device))
                        return true;
                    if (Directory.Exists(entry))
                        pending.Push(entry);
                }
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }
}
