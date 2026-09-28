using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimNpcFinishCoreService
{
    private const string GeneratedPackageManifestName = "npcmanager-package.json";
    private const string RetainedSourcePackageManifestName = "NPCManager/finish-core-source-package-manifest.json";

    private StableFileRead ReadFinishSourceManifest(
        SkyrimNpcFinishCoreRequest request, WorkspacePath sourceAuthority)
    {
        if (request.Source.PackageManifest is not { } sourcePath ||
            request.Source.PackageManifestSha256 is not { } sourceHash ||
            request.Source.PackageRoot is not { } sourceRoot ||
            !sourcePath.IsUnder(sourceRoot) || !sourcePath.IsUnder(projectRoot))
            throw new InvalidDataException("The retained source package manifest authority is missing or outside the project.");
        StableFileRead source = ReadBoundedPackageFile(sourceAuthority.Value, 4 * 1024 * 1024);
        if (source.Sha256 != sourceHash)
            throw new InvalidDataException("The retained source package manifest failed its request-bound hash check.");
        return source;
    }

    private PresetToNpcPackageManifest BuildFinishedPackageManifest(
        SkyrimNpcFinishCoreRequest request, string packageRoot, CancellationToken cancellationToken)
    {
        StableFileRead source = ReadFinishSourceManifest(request,
            new WorkspacePath(Path.Combine(packageRoot, RetainedSourcePackageManifestName)));
        using JsonDocument document = JsonDocument.Parse(source.Bytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement original = document.RootElement;
        if (original.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("The source package manifest must retain schemaVersion 1.");
        Dictionary<string, string> sourceKinds = original.GetProperty("artifacts").EnumerateArray()
            .ToDictionary(row => new AssetPath(RequiredString(row, "relativePath")).Value,
                row => RequiredString(row, "kind"), StringComparer.OrdinalIgnoreCase);
        string pluginRelative = RequirePackageRelativePluginPath(request);
        var artifacts = ImmutableArray.CreateBuilder<PresetToNpcPackageArtifact>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in EnumerateOrdinaryFiles(packageRoot)
                     .OrderBy(path => Path.GetRelativePath(packageRoot, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(packageRoot, path).Replace('\\', '/');
            if (string.Equals(relative, GeneratedPackageManifestName, StringComparison.Ordinal)) continue;
            if (!paths.Add(relative) || artifacts.Count >= 10_000 ||
                string.Equals(relative, GeneratedPackageManifestName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The finished package inventory contains duplicate/aliased paths or too many files.");
            StableFileRead file = ReadBoundedPackageFile(path, 512 * 1024 * 1024);
            string kind = string.Equals(relative, pluginRelative, StringComparison.OrdinalIgnoreCase)
                ? "plugin"
                : relative.StartsWith(SkyrimNpcFinishCoreManifestEvidence.InheritedNamespacePrefix, StringComparison.Ordinal)
                    ? "finish-core-inherited-evidence"
                    : sourceKinds.GetValueOrDefault(relative, "finish-core-evidence");
            artifacts.Add(new(kind, new AssetPath(relative), file.Bytes.Length, file.Sha256));
        }
        return new(1, RequiredString(original, "edition"), RequiredString(original, "presetFormat"),
            RequiredString(original, "sourcePreset"), new(RequiredString(original, "sourcePresetSha256")),
            RequiredString(original, "sourcePlugin"), new(RequiredString(original, "sourcePluginSha256")),
            request.Output.PluginFileName!, request.Actor.FormId!.Value, artifacts.ToImmutable());
    }

    private async ValueTask RetainFinishedSourceManifestAsync(
        SkyrimNpcFinishCoreRequest request, WorkspacePath stagingRoot, CancellationToken cancellationToken)
    {
        var stagedSourceManifest = new WorkspacePath(Path.Combine(stagingRoot.Value,
            Path.GetRelativePath(request.Source.PackageRoot!.Value.Value, request.Source.PackageManifest!.Value.Value)));
        if (!stagedSourceManifest.IsUnder(stagingRoot))
            throw new InvalidDataException("The copied source package manifest must remain inside transaction staging.");
        StableFileRead source = ReadFinishSourceManifest(request, stagedSourceManifest);
        string retainedPath = Path.Combine(stagingRoot.Value, RetainedSourcePackageManifestName);
        Directory.CreateDirectory(Path.GetDirectoryName(retainedPath)!);
        await WriteNewBytesAsync(retainedPath, source.Bytes, cancellationToken);
    }

    private async ValueTask PublishFinishedPackageManifestAsync(
        SkyrimNpcFinishCoreRequest request, WorkspacePath stagingRoot, CancellationToken cancellationToken)
    {
        PresetToNpcPackageManifest manifest = BuildFinishedPackageManifest(
            request, stagingRoot.Value, cancellationToken);
        if (PackageManifestWriter.Serialize(manifest).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("The generated package manifest exceeds its 4 MiB byte limit.");
        var destination = new WorkspacePath(Path.Combine(stagingRoot.Value, GeneratedPackageManifestName));
        // Only the transaction-owned copy is replaced; original source authority remains immutable.
        if (File.Exists(destination.Value))
        {
            _ = ReadBoundedPackageFile(destination.Value, 4 * 1024 * 1024);
            File.Delete(destination.Value);
        }
        PackageManifestWriteResult written = await PackageManifestWriter.WriteAsync(manifest, destination, cancellationToken);
        if (!written.Written)
            throw new InvalidDataException(string.Join(" | ", written.Diagnostics.Select(item => item.Code + ":" + item.Message)));
    }

    private bool VerifyFinishedPackageManifest(
        SkyrimNpcFinishCoreRequest request, string packageRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        byte[] expected = PackageManifestWriter.Serialize(BuildFinishedPackageManifest(
            request, packageRoot, cancellationToken));
        StableFileRead observed = ReadBoundedPackageFile(Path.Combine(packageRoot, GeneratedPackageManifestName), 4 * 1024 * 1024);
        if (observed.Bytes.AsSpan().SequenceEqual(expected)) return true;
        diagnostics.Add(new Diagnostic("finish-core-verify-package-manifest", DiagnosticSeverity.Error,
            "The generated root package manifest differs from its source-bound provenance and final file inventory."));
        return false;
    }

    private static StableFileRead ReadBoundedPackageFile(string path, int maximumBytes)
    {
        var info = new FileInfo(path);
        if (HasReparseAncestor(path) || !info.Exists || info.Length is <= 0 || info.Length > maximumBytes)
            throw new InvalidDataException($"Package file '{path}' is unsafe, empty, or exceeds its byte limit.");
        StableFileRead read = ReadStableFile(path);
        if (read.Bytes.Length > maximumBytes)
            throw new InvalidDataException($"Package file '{path}' exceeds its byte limit.");
        return read;
    }

    private static Sha256Hash ComputeFinishOutputTree(string root, string pluginRelativePath, bool generatedManifest) =>
        ComputeTreeHash(root, relative =>
            string.Equals(relative, pluginRelativePath, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("NPCManager/Evidence/", StringComparison.Ordinal) ||
            relative.StartsWith("Data/NPCManager/Evidence/", StringComparison.Ordinal) ||
            string.Equals(relative, "README-Finish-Core.txt", StringComparison.Ordinal) ||
            (generatedManifest && string.Equals(relative, GeneratedPackageManifestName, StringComparison.Ordinal)),
            generatedManifest ? "npc.finish-core.output-tree.v2\0" : null);
}
