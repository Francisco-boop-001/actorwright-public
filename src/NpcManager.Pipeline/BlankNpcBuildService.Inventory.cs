using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class BlankNpcBuildService
{
    private static BuildPaths CreatePaths(BlankNpcBuildRequest request)
    {
        const string formId = "00000800";
        var data = Path.Combine(request.OutputRoot.Value, "Data");
        var evidence = Path.Combine(request.OutputRoot.Value, "evidence");
        var plugin = new WorkspacePath(Path.Combine(data, request.OutputPlugin.Value));
        var faceGeom = new WorkspacePath(Path.Combine(data, "meshes", "actors", "character", "FaceGenData",
            "FaceGeom", request.OutputPlugin.Value, formId + ".nif"));
        var faceTint = new WorkspacePath(Path.Combine(data, "textures", "actors", "character", "FaceGenData",
            "FaceTint", request.OutputPlugin.Value, formId + ".dds"));
        var exactFaceTint = request.FaceTintSource is ExactDdsBlankNpcFaceTintSource;
        var faceTintEvidenceKind = exactFaceTint ? "facetint-exact-source" : "facetint-build";
        var transitiveAssets = NormalizedTransitiveAssets(request)
            .Select((asset, index) => new TransitiveAssetPath(
                asset.Kind ?? $"transitive-package-asset-{index:D4}",
                asset,
                new WorkspacePath(Path.Combine(
                    data,
                    asset.Destination.Value.Replace('/', Path.DirectorySeparatorChar)))))
            .ToImmutableArray();
        var requiredDirectories = new[]
            {
                data,
                evidence,
                Path.GetDirectoryName(faceGeom.Value)!,
                Path.GetDirectoryName(faceTint.Value)!
            }
            .Concat(transitiveAssets.Select(asset => Path.GetDirectoryName(asset.Destination.Value)!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new BuildPaths(
            plugin,
            faceGeom,
            faceTint,
            new AssetPath($"textures/actors/character/FaceGenData/FaceTint/{request.OutputPlugin.Value}/{formId}.dds"),
            new WorkspacePath(Path.Combine(evidence, "npc-creation-proposal.json")),
            new WorkspacePath(Path.Combine(evidence, "facegeom-carrier-materialization.json")),
            new WorkspacePath(Path.Combine(evidence,
                exactFaceTint ? "facetint-exact-source.json" : "facetint-build.json")),
            faceTintEvidenceKind,
            new WorkspacePath(Path.Combine(evidence, "provider-bundle.json")),
            new WorkspacePath(Path.Combine(evidence, "dependency-manifest.json")),
            new WorkspacePath(Path.Combine(evidence, "runtime-test-instructions.json")),
            new WorkspacePath(Path.Combine(data,
                $"diag-{request.Identity.EditorId.Value.ToLowerInvariant()}.txt")),
            new WorkspacePath(Path.Combine(request.OutputRoot.Value, "npcmanager-package.json")),
            transitiveAssets,
            requiredDirectories);
    }

    private static async ValueTask<ImmutableArray<PresetToNpcPackageArtifact>> BuildArtifactInventoryAsync(
        BuildPaths paths,
        CancellationToken cancellationToken)
    {
        var rows = ImmutableArray.CreateBuilder<PresetToNpcPackageArtifact>();
        var artifacts = new[]
            {
                ("plugin", paths.Plugin),
                ("facegeom", paths.FaceGeom),
                ("facetint", paths.FaceTint),
                ("npc-creation-proposal", paths.NpcProposal),
                ("facegeom-carrier-materialization", paths.FaceGeomEvidence),
                (paths.FaceTintEvidenceKind, paths.FaceTintEvidence),
                ("provider-bundle", paths.ProviderManifest),
                ("dependencies", paths.DependencyManifest),
                ("runtime-kit", paths.RuntimeKit),
                ("runtime-diagnostic-batch", paths.RuntimeDiagnosticBatch)
            }
            .Concat(paths.TransitiveAssets.Select(asset => (asset.Kind, asset.Destination)));
        foreach (var (kind, path) in artifacts)
        {
            var info = new FileInfo(path.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > int.MaxValue)
                throw new IOException($"Package artifact '{path.Value}' is missing, empty, or too large.");
            await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)));
            rows.Add(new PresetToNpcPackageArtifact(kind,
                Relative(new WorkspacePath(Path.GetDirectoryName(paths.Manifest.Value)!), path),
                checked((int)info.Length), hash));
        }
        return rows.ToImmutable();
    }

    private async ValueTask<FinalInventoryLease?> AcquireFinalInventoryLeaseAsync(
        WorkspacePath packageRoot,
        PackageVerificationArtifact inventory,
        Sha256Hash expectedManifestHash,
        IReadOnlyDictionary<string, Sha256Hash> semanticHashes,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var streams = new List<FileStream>(inventory.Files.Length + 1);
        var hashes = new Dictionary<string, Sha256Hash>(StringComparer.OrdinalIgnoreCase);
        var leased = false;
        try
        {
            foreach (var row in inventory.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = new WorkspacePath(Path.Combine(packageRoot.Value,
                    row.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.IsUnder(packageRoot))
                {
                    diagnostics.Add(new Diagnostic("blank-npc-final-inventory-outside-root", DiagnosticSeverity.Error,
                        $"Package artifact '{row.RelativePath.Value}' escaped the final package root."));
                    return null;
                }
                var pathDiagnostics = policy.EvaluateReadRoot(labRoot, path);
                diagnostics.AddRange(pathDiagnostics);
                if (pathDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return null;

                var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                streams.Add(stream);
                var currentLength = stream.Length;
                var currentHash = new Sha256Hash(Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)));
                if (!row.Matches || currentLength != row.ActualByteLength ||
                    currentHash != row.ActualSha256 || currentHash != row.ExpectedSha256)
                {
                    diagnostics.Add(new Diagnostic("blank-npc-final-inventory-drift", DiagnosticSeverity.Error,
                        $"Package artifact '{row.RelativePath.Value}' changed after inventory verification."));
                    return null;
                }
                if (!hashes.TryAdd(row.Kind, currentHash))
                {
                    diagnostics.Add(new Diagnostic("blank-npc-final-inventory-kind-duplicate", DiagnosticSeverity.Error,
                        $"The final inventory contains duplicate artifact kind '{row.Kind}'."));
                    return null;
                }
            }

            foreach (var (kind, semanticHash) in semanticHashes)
            {
                if (!hashes.TryGetValue(kind, out var inventoryHash) || inventoryHash != semanticHash)
                {
                    diagnostics.Add(new Diagnostic("blank-npc-semantic-inventory-mismatch", DiagnosticSeverity.Error,
                        $"The final semantic hash for '{kind}' does not match the locked package inventory."));
                    return null;
                }
            }

            var manifestDiagnostics = policy.EvaluateReadRoot(labRoot, inventory.ManifestPath);
            diagnostics.AddRange(manifestDiagnostics);
            if (manifestDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return null;
            var manifestStream = new FileStream(inventory.ManifestPath.Value, FileMode.Open,
                FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            streams.Add(manifestStream);
            var manifestHash = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(manifestStream, cancellationToken)));
            if (manifestHash != inventory.ManifestSha256 || manifestHash != expectedManifestHash)
            {
                diagnostics.Add(new Diagnostic("blank-npc-final-manifest-drift", DiagnosticSeverity.Error,
                    "The package manifest changed after final inventory verification."));
                return null;
            }

            var expectedFiles = inventory.Files
                .Select(row => CanonicalPath(Path.Combine(packageRoot.Value,
                    row.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar))))
                .Append(CanonicalPath(inventory.ManifestPath.Value))
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            if (!TryVerifyExactPackageTree(packageRoot, expectedFiles, diagnostics)) return null;

            var result = new FinalInventoryLease(
                streams.ToImmutableArray(),
                hashes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
                manifestHash);
            leased = true;
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("blank-npc-final-inventory-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return null;
        }
        finally
        {
            if (!leased) foreach (var stream in streams) stream.Dispose();
        }
    }

    private static bool TryVerifyExactPackageTree(
        WorkspacePath packageRoot,
        ImmutableHashSet<string> expectedFiles,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var canonicalRoot = CanonicalPath(packageRoot.Value);
        var expectedDirectories = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        expectedDirectories.Add(canonicalRoot);
        foreach (var file in expectedFiles)
        {
            var current = Directory.GetParent(file)?.FullName;
            while (current is not null && IsSameOrUnder(current, canonicalRoot))
            {
                expectedDirectories.Add(CanonicalPath(current));
                if (string.Equals(current, canonicalRoot, StringComparison.OrdinalIgnoreCase)) break;
                current = Directory.GetParent(current)?.FullName;
            }
        }

        var actualFiles = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(canonicalRoot);
        try
        {
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var entry in Directory.GetFileSystemEntries(directory))
                {
                    var canonical = CanonicalPath(entry);
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        diagnostics.Add(new Diagnostic("blank-npc-final-tree-reparse", DiagnosticSeverity.Error,
                            $"The final package contains reparse entry '{canonical}'."));
                        return false;
                    }
                    if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        if (!expectedDirectories.Contains(canonical))
                        {
                            diagnostics.Add(new Diagnostic("blank-npc-final-tree-undeclared-directory",
                                DiagnosticSeverity.Error,
                                $"The final package contains undeclared directory '{canonical}'."));
                            return false;
                        }
                        pending.Push(canonical);
                    }
                    else
                    {
                        actualFiles.Add(canonical);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("blank-npc-final-tree-enumeration", DiagnosticSeverity.Error,
                $"The final package tree could not be enumerated safely: {exception.Message}"));
            return false;
        }

        if (actualFiles.SetEquals(expectedFiles)) return true;
        var missing = expectedFiles.Except(actualFiles, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var extra = actualFiles.Except(expectedFiles, StringComparer.OrdinalIgnoreCase).Order().ToArray();
        diagnostics.Add(new Diagnostic("blank-npc-final-tree-mismatch", DiagnosticSeverity.Error,
            $"The final package file set drifted after leases were acquired. Missing: " +
            $"[{string.Join(", ", missing)}]; extra: [{string.Join(", ", extra)}]."));
        return false;
    }

    private static AssetPath Relative(WorkspacePath root, WorkspacePath path) =>
        new(Path.GetRelativePath(root.Value, path.Value));

    private sealed record BuildPaths(
        WorkspacePath Plugin,
        WorkspacePath FaceGeom,
        WorkspacePath FaceTint,
        AssetPath FaceTintAssetPath,
        WorkspacePath NpcProposal,
        WorkspacePath FaceGeomEvidence,
        WorkspacePath FaceTintEvidence,
        string FaceTintEvidenceKind,
        WorkspacePath ProviderManifest,
        WorkspacePath DependencyManifest,
        WorkspacePath RuntimeKit,
        WorkspacePath RuntimeDiagnosticBatch,
        WorkspacePath Manifest,
        ImmutableArray<TransitiveAssetPath> TransitiveAssets,
        ImmutableArray<string> RequiredDirectories);

    private sealed record TransitiveAssetPath(
        string Kind,
        BlankNpcTransitivePackageAsset Source,
        WorkspacePath Destination);

    private sealed class FinalInventoryLease(
        ImmutableArray<FileStream> streams,
        ImmutableDictionary<string, Sha256Hash> hashes,
        Sha256Hash manifestHash) : IDisposable
    {
        public ImmutableDictionary<string, Sha256Hash> Hashes { get; } = hashes;
        public Sha256Hash ManifestHash { get; } = manifestHash;

        public void Dispose()
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }
}
