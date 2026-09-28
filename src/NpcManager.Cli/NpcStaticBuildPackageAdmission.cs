using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal interface INpcStaticBuildPackageAdmission
{
    ValueTask<NpcStaticBuildPackageLease> AdmitAsync(
        PackageVerificationArtifact verification,
        RaceMenuJslotNpcBuildCommandBinding binding,
        RaceMenuNpcExecutionRequest request,
        CancellationToken cancellationToken);

    ValueTask<NpcStaticBuildPackageLease> AdmitPreviewAsync(
        PackageVerificationArtifact verification,
        WorkflowArtifactBinding packageBinding,
        WorkflowNpcIdentity npc,
        CancellationToken cancellationToken);

    ValueTask<NpcStaticBuildPackageLease> AdmitFinishAsync(
        PackageVerificationArtifact verification,
        SkyrimNpcFinishCoreRequestDocument request,
        WorkflowNpcIdentity npc,
        CancellationToken cancellationToken);
}

internal sealed class NpcStaticBuildPackageAdmission(
    WorkspacePath workspaceRoot,
    PackageManifestReader manifestReader) : INpcStaticBuildPackageAdmission
{
    private const long MaximumManifestBytes = 4L * 1024 * 1024;
    private const long MaximumArtifactBytes = 512L * 1024 * 1024;
    private readonly FaceGeomHairRegionsPinnedFileSystem fileSystem = new(
        workspaceRoot);

    public async ValueTask<NpcStaticBuildPackageLease> AdmitAsync(
        PackageVerificationArtifact verification,
        RaceMenuJslotNpcBuildCommandBinding binding,
        RaceMenuNpcExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(request);
        if (!verification.NoUndeclaredFiles || !verification.NoWrite ||
            verification.RuntimeProof)
            throw new InvalidDataException(
                "The package verifier did not establish the required static no-write closure.");

        PackageManifestReadResult read = await manifestReader.ReadAsync(
            verification.ManifestPath,
            cancellationToken).ConfigureAwait(false);
        if (read.Identity is not { } identity ||
            read.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(read.Diagnostics.IsEmpty
                ? "The produced package manifest could not be reopened."
                : string.Join(" | ", read.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        RequireIdentity(identity, verification, binding, request);

        return Retain(identity, verification, cancellationToken);
    }

    public async ValueTask<NpcStaticBuildPackageLease> AdmitPreviewAsync(
        PackageVerificationArtifact verification,
        WorkflowArtifactBinding packageBinding,
        WorkflowNpcIdentity npc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(packageBinding);
        ArgumentNullException.ThrowIfNull(npc);
        if (!verification.NoUndeclaredFiles || !verification.NoWrite ||
            verification.RuntimeProof)
            throw new InvalidDataException(
                "The package verifier did not establish the required static no-write closure.");
        PackageManifestReadResult read = await manifestReader.ReadAsync(
            verification.ManifestPath,
            cancellationToken).ConfigureAwait(false);
        if (read.Identity is not { } identity ||
            read.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(read.Diagnostics.IsEmpty
                ? "The preview package manifest could not be reopened."
                : string.Join(" | ", read.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        RequirePreviewIdentity(
            identity,
            verification,
            packageBinding,
            npc);
        NpcStaticBuildPackageLease lease = Retain(
            identity,
            verification,
            cancellationToken);
        if (lease.ManifestSize != packageBinding.Size)
        {
            lease.Dispose();
            throw new InvalidDataException(
                "The preview package manifest size differs from the workflow binding.");
        }
        return lease;
    }

    public async ValueTask<NpcStaticBuildPackageLease> AdmitFinishAsync(
        PackageVerificationArtifact verification,
        SkyrimNpcFinishCoreRequestDocument request,
        WorkflowNpcIdentity npc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(npc);
        if (!verification.NoUndeclaredFiles || !verification.NoWrite ||
            verification.RuntimeProof)
            throw new InvalidDataException(
                "The Finish source package verifier did not establish the required static no-write closure.");
        if (request.Value.Source.PackageManifest is not { } manifestPath)
            throw new InvalidDataException(
                "The Finish request source package manifest is missing.");
        PackageManifestReadResult read = await manifestReader.ReadAsync(
            manifestPath,
            cancellationToken).ConfigureAwait(false);
        if (read.Identity is not { } identity ||
            read.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(read.Diagnostics.IsEmpty
                ? "The Finish source package manifest could not be reopened."
                : string.Join(" | ", read.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        RequireFinishIdentity(identity, verification, request.Value, npc);
        NpcStaticBuildPackageLease lease = Retain(
            identity,
            verification,
            cancellationToken);
        if (request.Value.Source.PackageTreeSha256 is not { } treeSha256 ||
            lease.ComputePackageTreeSha256() != treeSha256)
        {
            lease.Dispose();
            throw new InvalidDataException(
                "The retained Finish source package tree differs from the request binding.");
        }
        return lease;
    }

    private NpcStaticBuildPackageLease Retain(
        PackageManifestIdentity identity,
        PackageVerificationArtifact verification,
        CancellationToken cancellationToken)
    {

        var retained = new List<FaceGeomHairRegionsPinnedReadFile>(
            identity.Files.Length + 1);
        FaceGeomHairRegionsPinnedDirectory? packageDirectory = null;
        try
        {
            ImmutableDictionary<string, PackageFileVerification> rows =
                verification.Files.ToImmutableDictionary(
                    item => item.RelativePath.Value,
                    StringComparer.OrdinalIgnoreCase);
            if (rows.Count != identity.Files.Length)
                throw new InvalidDataException(
                    "The package verification rows do not exactly match the manifest files.");
            string packageRoot = Path.GetDirectoryName(
                identity.ManifestPath.Value) ??
                throw new InvalidDataException(
                    "The package manifest has no parent directory.");
            packageDirectory = fileSystem.OpenDirectory(
                new WorkspacePath(packageRoot),
                "NPC package root");
            ImmutableArray<NpcStaticBuildPackageTreeEntry> admittedTree =
                NpcStaticBuildPackageLease.Snapshot(packageDirectory);
            NpcStaticBuildPackageLease.RequireDeclaredFiles(
                admittedTree,
                identity);
            retained.Add(OpenExact(
                identity.ManifestPath,
                identity.ManifestSha256,
                MaximumManifestBytes,
                "NPC package manifest"));
            foreach (PackageManifestFile item in identity.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!rows.TryGetValue(
                        item.RelativePath.Value,
                        out PackageFileVerification? row) ||
                    !row.Matches ||
                    row.ExpectedByteLength != item.ByteLength ||
                    row.ActualByteLength != item.ByteLength ||
                    row.ExpectedSha256 != item.Sha256 ||
                    row.ActualSha256 != item.Sha256 ||
                    !string.Equals(
                        row.Kind,
                        item.Kind,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Package verification row '{item.RelativePath.Value}' is stale.");
                WorkspacePath path = new(Path.GetFullPath(Path.Combine(
                    packageRoot,
                    item.RelativePath.Value.Replace(
                        '/', Path.DirectorySeparatorChar))));
                retained.Add(OpenExact(
                    path,
                    item.Sha256,
                    MaximumArtifactBytes,
                    $"NPC package {item.Kind}"));
            }
            var lease = new NpcStaticBuildPackageLease(
                identity,
                retained,
                packageDirectory,
                admittedTree);
            lease.Revalidate();
            packageDirectory = null;
            return lease;
        }
        catch
        {
            Dispose(retained);
            packageDirectory?.Dispose();
            throw;
        }
    }

    private static void RequirePreviewIdentity(
        PackageManifestIdentity identity,
        PackageVerificationArtifact verification,
        WorkflowArtifactBinding packageBinding,
        WorkflowNpcIdentity npc)
    {
        if (!string.Equals(
                packageBinding.Kind,
                WorkflowArtifactKinds.NpcPackageManifest,
                StringComparison.Ordinal) ||
            !SamePath(identity.ManifestPath.Value, packageBinding.Path.Value) ||
            !string.Equals(
                identity.ManifestSha256.Value,
                packageBinding.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            identity.ManifestSha256 != verification.ManifestSha256 ||
            identity.TargetFormId != verification.TargetFormId ||
            !string.Equals(
                identity.OutputPlugin,
                verification.OutputPlugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                identity.OutputPlugin,
                npc.Plugin,
                StringComparison.OrdinalIgnoreCase) ||
            npc.LocalFormId is null ||
            !FormId.TryParse(npc.LocalFormId, out FormId workflowFormId) ||
            identity.TargetFormId != workflowFormId)
            throw new InvalidDataException(
                "The preview package identity differs from the workflow binding.");

        RequireGeneratedProposalIdentity(identity);
        string plugin = identity.OutputPlugin;
        string formId = identity.TargetFormId.Value.ToString(
            "X8", CultureInfo.InvariantCulture);
        RequireRole(identity.Files, "plugin", Path.Combine("Data", plugin)
            .Replace(Path.DirectorySeparatorChar, '/'));
        RequireRole(
            identity.Files,
            "facegeom",
            Path.Combine(
                    "Data", "meshes", "actors", "character", "FaceGenData",
                    "FaceGeom", plugin, formId + ".nif")
                .Replace(Path.DirectorySeparatorChar, '/'));
        RequireRole(
            identity.Files,
            "facetint",
            Path.Combine(
                    "Data", "textures", "actors", "character", "FaceGenData",
                    "FaceTint", plugin, formId + ".dds")
                .Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static void RequireFinishIdentity(
        PackageManifestIdentity identity,
        PackageVerificationArtifact verification,
        SkyrimNpcFinishCoreRequest request,
        WorkflowNpcIdentity npc)
    {
        if (request.Source.PackageRoot is not { } packageRoot ||
            request.Source.PackageManifest is not { } manifestPath ||
            request.Source.PackageManifestSha256 is not { } manifestSha256 ||
            request.Source.PluginPath is not { } pluginPath ||
            request.Source.Plugin is not { } sourcePlugin ||
            request.Source.PluginSha256 is not { } pluginSha256 ||
            request.Actor.EditorId is not { } editorId ||
            request.Actor.FormId is not { } formId ||
            npc.Plugin is null || npc.LocalFormId is null ||
            !FormId.TryParse(npc.LocalFormId, out FormId workflowFormId))
            throw new InvalidDataException(
                "The Finish request source package identity is incomplete.");
        string? manifestRoot = Path.GetDirectoryName(manifestPath.Value);
        string expectedPluginPath = Path.GetFullPath(Path.Combine(
            packageRoot.Value,
            "Data",
            sourcePlugin.Value));
        PackageManifestFile[] pluginRows = identity.Files.Where(item =>
                string.Equals(
                    item.Kind,
                    "plugin",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    item.RelativePath.Value,
                    Path.Combine("Data", sourcePlugin.Value)
                        .Replace(Path.DirectorySeparatorChar, '/'),
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (!SamePath(identity.ManifestPath.Value, manifestPath.Value) ||
            string.IsNullOrWhiteSpace(manifestRoot) ||
            !SamePath(manifestRoot, packageRoot.Value) ||
            identity.ManifestSha256 != manifestSha256 ||
            identity.ManifestSha256 != verification.ManifestSha256 ||
            !SamePath(verification.ManifestPath.Value, manifestPath.Value) ||
            identity.TargetFormId != verification.TargetFormId ||
            identity.TargetFormId != formId ||
            identity.TargetFormId != workflowFormId ||
            !string.Equals(
                identity.OutputPlugin,
                verification.OutputPlugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                identity.OutputPlugin,
                sourcePlugin.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                identity.OutputPlugin,
                request.Output.PluginFileName,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                identity.OutputPlugin,
                npc.Plugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(editorId.Value, npc.EditorId, StringComparison.Ordinal) ||
            !SamePath(pluginPath.Value, expectedPluginPath) ||
            pluginRows.Length != 1 ||
            pluginRows[0].Sha256 != pluginSha256)
            throw new InvalidDataException(
                "The Finish source package differs from its request and workflow identity bindings.");

        RequireGeneratedProposalIdentity(identity);
        string plugin = identity.OutputPlugin;
        string localFormId = identity.TargetFormId.Value.ToString(
            "X8",
            CultureInfo.InvariantCulture);
        RequireRole(
            identity.Files,
            "facegeom",
            Path.Combine(
                    "Data", "meshes", "actors", "character", "FaceGenData",
                    "FaceGeom", plugin, localFormId + ".nif")
                .Replace(Path.DirectorySeparatorChar, '/'));
        RequireRole(
            identity.Files,
            "facetint",
            Path.Combine(
                    "Data", "textures", "actors", "character", "FaceGenData",
                    "FaceTint", plugin, localFormId + ".dds")
                .Replace(Path.DirectorySeparatorChar, '/'));
    }

    private FaceGeomHairRegionsPinnedReadFile OpenExact(
        WorkspacePath path,
        Sha256Hash expectedSha256,
        long maximumBytes,
        string role)
    {
        FaceGeomHairRegionsPinnedReadFile retained = fileSystem.OpenRead(
            path,
            role);
        try
        {
            if (retained.Length <= 0 ||
                retained.Length > maximumBytes ||
                !string.Equals(
                    retained.ComputeSha256(),
                    expectedSha256.Value,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"The {role} size/hash changed after package verification.");
            return retained;
        }
        catch
        {
            retained.Dispose();
            throw;
        }
    }

    private static void RequireIdentity(
        PackageManifestIdentity identity,
        PackageVerificationArtifact verification,
        RaceMenuJslotNpcBuildCommandBinding binding,
        RaceMenuNpcExecutionRequest request)
    {
        string formId = identity.TargetFormId.Value.ToString(
            "X8", CultureInfo.InvariantCulture);
        string outputPlugin = request.Build.OutputPlugin.Value;
        if (identity.ManifestSha256 != verification.ManifestSha256 ||
            identity.TargetFormId != verification.TargetFormId ||
            !string.Equals(identity.OutputPlugin, verification.OutputPlugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.OutputPlugin, outputPlugin,
                StringComparison.OrdinalIgnoreCase) ||
            request.Build.PresetBundle.ExpectedPresetSha256 !=
                binding.ExpectedPresetSha256 ||
            !SamePath(
                request.Build.PresetBundle.PresetPath.Value,
                binding.Preset.Value))
            throw new InvalidDataException(
                "The package manifest identity differs from the admitted request/preset.");

        RequireGeneratedProposalIdentity(identity);

        RequireRole(
            identity.Files,
            "plugin",
            Path.Combine("Data", outputPlugin)
                .Replace(Path.DirectorySeparatorChar, '/'));
        RequireRole(
            identity.Files,
            "facegeom",
            Path.Combine(
                    "Data", "meshes", "actors", "character", "FaceGenData",
                    "FaceGeom", outputPlugin, formId + ".nif")
                .Replace(Path.DirectorySeparatorChar, '/'));
        RequireRole(
            identity.Files,
            "facetint",
            Path.Combine(
                    "Data", "textures", "actors", "character", "FaceGenData",
                    "FaceTint", outputPlugin, formId + ".dds")
                .Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static void RequireGeneratedProposalIdentity(
        PackageManifestIdentity identity)
    {
        const string presetFormat = "blank-npc-creation-proposal";
        const string proposalKind = "npc-creation-proposal";
        const string proposalPath = "evidence/npc-creation-proposal.json";
        PackageManifestFile[] proposals = identity.Files.Where(item =>
                string.Equals(
                    item.Kind,
                    proposalKind,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    item.RelativePath.Value,
                    proposalPath,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        AssetPath sourcePreset = new(identity.SourcePreset);
        if (!string.Equals(
                identity.PresetFormat,
                presetFormat,
                StringComparison.Ordinal) ||
            proposals.Length != 1 ||
            !string.Equals(
                sourcePreset.Value,
                proposals[0].RelativePath.Value,
                StringComparison.OrdinalIgnoreCase) ||
            identity.SourcePresetSha256 != proposals[0].Sha256)
            throw new InvalidDataException(
                "The package manifest source does not match its retained NPC creation proposal.");
    }

    private static void RequireRole(
        ImmutableArray<PackageManifestFile> files,
        string kind,
        string relativePath)
    {
        int matches = files.Count(item =>
            string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                item.RelativePath.Value,
                relativePath,
                StringComparison.OrdinalIgnoreCase));
        if (matches != 1)
            throw new InvalidDataException(
                $"The package requires exactly one '{kind}' artifact at '{relativePath}'.");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static void Dispose(
        IEnumerable<FaceGeomHairRegionsPinnedReadFile> retained)
    {
        foreach (FaceGeomHairRegionsPinnedReadFile file in retained.Reverse())
            file.Dispose();
    }
}

internal readonly record struct NpcStaticBuildPackageTreeEntry(
    string RelativePath,
    bool IsDirectory);

internal sealed class NpcStaticBuildPackageLease(
    PackageManifestIdentity identity,
    List<FaceGeomHairRegionsPinnedReadFile> retained,
    FaceGeomHairRegionsPinnedDirectory packageDirectory,
    ImmutableArray<NpcStaticBuildPackageTreeEntry> admittedTree) :
    IDisposable,
    IAsyncDisposable
{
    private bool disposed;
    private readonly long manifestSize = retained[0].Length;

    public PackageManifestIdentity Identity { get; } = identity;

    public long ManifestSize => manifestSize;

    public WorkspacePath PackageRoot => new(packageDirectory.Path);

    public void Revalidate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        RequireTreeUnchanged();
        if (retained[0].Length != manifestSize ||
            !string.Equals(
                retained[0].ComputeSha256(),
                Identity.ManifestSha256.Value.ToUpperInvariant(),
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The retained NPC package manifest changed after admission.");
        if (retained.Count != Identity.Files.Length + 1)
            throw new InvalidDataException(
                "The retained NPC package file closure changed after admission.");
        for (int index = 0; index < Identity.Files.Length; index++)
        {
            PackageManifestFile expected = Identity.Files[index];
            FaceGeomHairRegionsPinnedReadFile actual = retained[index + 1];
            if (actual.Length != expected.ByteLength ||
                !string.Equals(
                    actual.ComputeSha256(),
                    expected.Sha256.Value.ToUpperInvariant(),
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"The retained NPC package artifact '{expected.RelativePath.Value}' changed after admission.");
        }
        RequireTreeUnchanged();
    }

    internal static ImmutableArray<NpcStaticBuildPackageTreeEntry> Snapshot(
        FaceGeomHairRegionsPinnedDirectory packageDirectory) =>
        packageDirectory.EnumerateTree()
            .Select(item => new NpcStaticBuildPackageTreeEntry(
                Path.GetRelativePath(
                        packageDirectory.Path,
                        item.Path.Value)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .ToUpperInvariant(),
                item.IsDirectory))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.IsDirectory)
            .ToImmutableArray();

    internal static void RequireDeclaredFiles(
        ImmutableArray<NpcStaticBuildPackageTreeEntry> tree,
        PackageManifestIdentity identity)
    {
        string manifestName = Path.GetFileName(identity.ManifestPath.Value)
            .Replace(Path.DirectorySeparatorChar, '/')
            .ToUpperInvariant();
        string[] expectedFiles =
        [
            manifestName,
            .. identity.Files.Select(item =>
                item.RelativePath.Value.ToUpperInvariant())
        ];
        string[] observedFiles = tree
            .Where(item => !item.IsDirectory)
            .Select(item => item.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in expectedFiles)
        {
            int separator = file.LastIndexOf('/');
            while (separator > 0)
            {
                expectedDirectories.Add(file[..separator]);
                separator = file.LastIndexOf('/', separator - 1);
            }
        }
        string[] observedDirectories = tree
            .Where(item => item.IsDirectory)
            .Select(item => item.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!observedFiles.SequenceEqual(
                expectedFiles.Order(StringComparer.Ordinal),
                StringComparer.Ordinal) ||
            !observedDirectories.SequenceEqual(
                expectedDirectories.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw new InvalidDataException(
                "The retained NPC package tree does not exactly match its declared files and ancestor directories.");
    }

    internal Sha256Hash ComputePackageTreeSha256()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ImmutableArray<FaceGeomHairRegionsPinnedTreeEntry> tree =
            packageDirectory.EnumerateTree();
        var lines = new StringBuilder();
        foreach (FaceGeomHairRegionsPinnedTreeEntry entry in tree
                     .Where(item => !item.IsDirectory)
                     .OrderBy(
                         item => Path.GetRelativePath(
                                 packageDirectory.Path,
                                 item.Path.Value)
                             .Replace(Path.DirectorySeparatorChar, '/'),
                         StringComparer.Ordinal))
        {
            FaceGeomHairRegionsPinnedReadFile file = retained.Single(item =>
                string.Equals(
                    Path.GetFullPath(item.Path),
                    Path.GetFullPath(entry.Path.Value),
                    StringComparison.OrdinalIgnoreCase));
            string relative = Path.GetRelativePath(
                    packageDirectory.Path,
                    entry.Path.Value)
                .Replace(Path.DirectorySeparatorChar, '/');
            lines.Append(relative).Append('|')
                .Append(file.Length).Append('|')
                .Append(file.ComputeSha256()).Append('\n');
        }
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(lines.ToString()))));
    }

    private void RequireTreeUnchanged()
    {
        if (!Snapshot(packageDirectory).SequenceEqual(admittedTree))
            throw new InvalidDataException(
                "The retained NPC package tree changed after admission.");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        foreach (FaceGeomHairRegionsPinnedReadFile file in retained.AsEnumerable().Reverse())
            file.Dispose();
        packageDirectory.Dispose();
        disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
