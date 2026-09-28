using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class BlankNpcBuildService
{
    private async ValueTask<ProviderMaterializationResult> MaterializeProductProviderAsync(
        BlankNpcBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        ProviderResourceAuthoritySet resources = request.ProviderResources!;
        string parent = Path.GetDirectoryName(request.OutputRoot.Value) ?? "";
        var root = new WorkspacePath(Path.Combine(parent,
            $".actorwright-provider-inputs-{Guid.NewGuid():N}"));
        try
        {
            if (!root.IsUnder(labRoot) || root == labRoot ||
                root.IsUnder(request.OutputRoot) || request.OutputRoot.IsUnder(root) ||
                File.Exists(root.Value) || Directory.Exists(root.Value))
                throw new InvalidDataException(
                    "The product-provider materialization root is not a new disjoint workspace path.");
            Directory.CreateDirectory(root.Value);
            EnsureOrdinaryMaterializationDirectory(root.Value);

            WorkspacePath manifest = Destination(root, "provider-manifest.json");
            WorkspacePath faceTintManifest = Destination(root, "facetint-manifest.json");
            WorkspacePath dependencyManifest = Destination(root, "dependency-manifest.json");
            WorkspacePath dataRoot = Destination(root, "Data");
            Directory.CreateDirectory(dataRoot.Value);

            WorkspacePath template = DataDestination(
                resources, resources.TemplatePlugin, dataRoot);
            WorkspacePath carrier = DataDestination(
                resources, resources.FaceGeomCarrier, dataRoot);
            WorkspacePath faceTintSource = DataDestination(
                resources, resources.FaceTintSource, dataRoot);

            await CopyApplicationResourceAsync(
                resources.Manifest, manifest, cancellationToken);
            await CopyApplicationResourceAsync(
                resources.TemplatePlugin, template, cancellationToken);
            await CopyApplicationResourceAsync(
                resources.FaceGeomCarrier, carrier, cancellationToken);
            await CopyApplicationResourceAsync(
                resources.FaceTintManifest, faceTintManifest, cancellationToken);
            await CopyApplicationResourceAsync(
                resources.FaceTintSource, faceTintSource, cancellationToken);
            await CopyApplicationResourceAsync(
                resources.DependencyManifest, dependencyManifest, cancellationToken);

            var effective = request with
            {
                ProviderManifest = manifest,
                ExpectedProviderManifestSha256 = resources.Manifest.ExpectedSha256,
                TemplatePlugin = template,
                ExpectedTemplatePluginSha256 = resources.TemplatePlugin.ExpectedSha256,
                TemplateNpcFormId = resources.TemplateNpcFormId,
                FaceGeomCarrier = carrier,
                ExpectedFaceGeomCarrierSha256 = resources.FaceGeomCarrier.ExpectedSha256,
                FaceTintManifest = faceTintManifest,
                FaceTintProviderRoot = dataRoot,
                DependencyManifest = dependencyManifest
            };
            return new ProviderMaterializationResult(true, effective, root);
        }
        catch (OperationCanceledException)
        {
            CleanupProductProviderMaterialization(root, request.OutputRoot, diagnostics);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic(
                "blank-npc-product-provider-materialization",
                DiagnosticSeverity.Error,
                exception.Message));
            CleanupProductProviderMaterialization(root, request.OutputRoot, diagnostics);
            return new ProviderMaterializationResult(false, null, null);
        }
    }

    private static WorkspacePath DataDestination(
        ProviderResourceAuthoritySet resources,
        ProviderResourceAuthority resource,
        WorkspacePath dataRoot)
    {
        if (resources.FaceTintProviderRoot is not
                ApplicationProviderResourceAuthority providerRoot ||
            resource is not ApplicationProviderResourceAuthority application)
            throw new InvalidDataException(
                "Product-provider materialization accepts application authorities only.");
        string relative = Path.GetRelativePath(
            providerRoot.Path.Value, application.Path.Value);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Product-provider role '{resource.Role}' escaped its authenticated Data root.");
        return Destination(dataRoot, relative);
    }

    private static WorkspacePath Destination(WorkspacePath root, string relative)
    {
        var result = new WorkspacePath(Path.Combine(root.Value, relative));
        if (!result.IsUnder(root))
            throw new InvalidDataException(
                "A product-provider destination escaped its owned materialization root.");
        string? parent = Path.GetDirectoryName(result.Value);
        if (!string.IsNullOrWhiteSpace(parent))
            Directory.CreateDirectory(parent);
        return result;
    }

    private static async ValueTask CopyApplicationResourceAsync(
        ProviderResourceAuthority resource,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        if (resource is not ApplicationProviderResourceAuthority application ||
            resource.IsDirectory)
            throw new InvalidDataException(
                $"Product-provider role '{resource.Role}' is not one application file.");
        var sourceInfo = new FileInfo(application.Path.Value);
        if (!sourceInfo.Exists || sourceInfo.Length <= 0 ||
            sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException(
                $"Product-provider role '{resource.Role}' is absent or not ordinary.");
        if (File.Exists(destination.Value) || Directory.Exists(destination.Value))
            throw new InvalidDataException(
                $"Product-provider destination for '{resource.Role}' already exists.");

        File.Copy(application.Path.Value, destination.Value, overwrite: false);
        await using var stream = new FileStream(destination.Value, FileMode.Open,
            FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)));
        if (actual != application.ExpectedSha256)
            throw new InvalidDataException(
                $"Product-provider role '{resource.Role}' changed during materialization.");
    }

    private static void CleanupProductProviderMaterialization(
        WorkspacePath root,
        WorkspacePath outputRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Directory.Exists(root.Value)) return;
        try
        {
            if (root == outputRoot || root.IsUnder(outputRoot) || outputRoot.IsUnder(root))
                throw new InvalidDataException(
                    "Refused unsafe product-provider materialization cleanup.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         root.Value, "*", SearchOption.AllDirectories))
            {
                if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException(
                        "Refused product-provider cleanup across a reparse point.");
            }
            Directory.Delete(root.Value, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic(
                "blank-npc-product-provider-cleanup",
                DiagnosticSeverity.Warning,
                exception.Message));
        }
    }

    private static void EnsureOrdinaryMaterializationDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException(
                "The product-provider materialization root is not an ordinary directory.");
    }

    private sealed record ProviderMaterializationResult(
        bool Materialized,
        BlankNpcBuildRequest? Request,
        WorkspacePath? Root);
}
