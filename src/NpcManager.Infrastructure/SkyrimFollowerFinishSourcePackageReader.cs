using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Admits a hash-bound no-wrapper install ZIP against its separately bound
/// NPC Manager package manifest, materializes only a GUID-owned K-local
/// analysis tree, and removes that tree after typed/raw plugin inspection.
/// </summary>
public sealed class SkyrimFollowerFinishSourcePackageReader
{
    private const int MaximumEntryCount = 10_000;
    private const long MaximumEntryBytes = 512L * 1024 * 1024;
    private const long MaximumTotalBytes = 1024L * 1024 * 1024;
    private const string ScratchPrefix =
        ".npcmanager-follower-finish-analysis-";
    private static readonly ImmutableHashSet<string> GeneratedArchiveEntries =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "BUILD_INFO.txt",
            "README-NPCMANAGER-RUNTIME-TEST.txt",
            "RUNTIME-TEST-INSTRUCTIONS.md");
    private readonly WorkspacePath workspaceRoot;
    private readonly IWorkspacePolicy policy;
    private readonly PackageManifestReader manifestReader;
    private readonly IPackageVerifyService packageVerifier;
    private readonly ISkyrimFollowerFinishPluginService pluginReader;
    private readonly Func<WorkspacePath, Stream> openZipStream;

    public SkyrimFollowerFinishSourcePackageReader(
        WorkspacePath workspaceRoot,
        IWorkspacePolicy policy,
        PackageManifestReader manifestReader,
        IPackageVerifyService packageVerifier,
        ISkyrimFollowerFinishPluginService pluginReader)
        : this(
            workspaceRoot,
            policy,
            manifestReader,
            packageVerifier,
            pluginReader,
            OpenZipStream)
    {
    }

    internal SkyrimFollowerFinishSourcePackageReader(
        WorkspacePath workspaceRoot,
        IWorkspacePolicy policy,
        PackageManifestReader manifestReader,
        IPackageVerifyService packageVerifier,
        ISkyrimFollowerFinishPluginService pluginReader,
        Func<WorkspacePath, Stream> openZipStream)
    {
        this.workspaceRoot = workspaceRoot;
        this.policy = policy ??
            throw new ArgumentNullException(nameof(policy));
        this.manifestReader = manifestReader ??
            throw new ArgumentNullException(nameof(manifestReader));
        this.packageVerifier = packageVerifier ??
            throw new ArgumentNullException(nameof(packageVerifier));
        this.pluginReader = pluginReader ??
            throw new ArgumentNullException(nameof(pluginReader));
        this.openZipStream = openZipStream ??
            throw new ArgumentNullException(nameof(openZipStream));
    }

    public async ValueTask<SkyrimFollowerFinishPluginSnapshot> InspectAsync(
        SkyrimFollowerFinishRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? scratch = null;
        try
        {
            ImmutableArray<Diagnostic> pathDiagnostics =
                ValidateReadPaths(request);
            if (HasErrors(pathDiagnostics))
                return Refused(
                    request,
                    "follower-finish-source-path",
                    JoinErrors(pathDiagnostics));

            await using Stream admittedZip =
                openZipStream(request.Source.Zip);
            if (!admittedZip.CanRead || !admittedZip.CanSeek)
                return Refused(
                    request,
                    "follower-finish-source-zip-stream",
                    "The source ZIP admission stream must be readable and seekable.");
            long zipByteLength = admittedZip.Length;
            var zipHash = new Sha256Hash(
                Convert.ToHexString(
                    await SHA256.HashDataAsync(
                        admittedZip,
                        cancellationToken)));
            if (zipHash != request.Source.ZipSha256)
                return Refused(
                    request,
                    "follower-finish-source-zip-hash",
                    $"Source ZIP hash {zipHash} does not match {request.Source.ZipSha256}.");
            if (zipByteLength != request.Source.ZipByteLength)
                return Refused(
                    request,
                    "follower-finish-source-zip-length",
                    "The source ZIP byte length changed after request admission.");
            admittedZip.Position = 0;

            PackageManifestReadResult manifestRead =
                await manifestReader.ReadAsync(
                    request.Source.PackageManifest,
                    cancellationToken);
            if (manifestRead.Identity is null ||
                HasErrors(manifestRead.Diagnostics))
                return Refused(
                    request,
                    "follower-finish-source-manifest-invalid",
                    JoinErrors(manifestRead.Diagnostics));
            PackageManifestIdentity identity = manifestRead.Identity;
            if (identity.ManifestSha256 !=
                request.Source.PackageManifestSha256)
                return Refused(
                    request,
                    "follower-finish-source-manifest-hash",
                    $"Source manifest hash {identity.ManifestSha256} does not match {request.Source.PackageManifestSha256}.");

            ManifestClosure? closure = CloseManifest(
                request,
                identity,
                out Diagnostic? closureError);
            if (closure is null)
                return Refused(
                    request,
                    closureError?.Code ??
                    "follower-finish-source-manifest-identity",
                    closureError?.Message ??
                    "The source manifest did not close the requested source identity.");

            string parent = Directory.GetParent(
                    request.OutputRoot.Value)?.FullName ??
                throw new InvalidDataException(
                    "The follower-finish output root has no K-local parent.");
            scratch = Path.Combine(
                parent,
                ScratchPrefix + Guid.NewGuid().ToString("N"));
            var scratchPath = new WorkspacePath(scratch);
            ImmutableArray<Diagnostic> scratchDiagnostics =
                policy.Evaluate(workspaceRoot, scratchPath);
            if (HasErrors(scratchDiagnostics) ||
                File.Exists(scratch) ||
                Directory.Exists(scratch))
                return Refused(
                    request,
                    "follower-finish-source-scratch-refused",
                    HasErrors(scratchDiagnostics)
                        ? JoinErrors(scratchDiagnostics)
                        : "The GUID-owned analysis directory collided before creation.");
            Directory.CreateDirectory(scratch);

            string packageRoot = Path.Combine(scratch, "package");
            string archiveRoot = Path.Combine(scratch, "archive");
            Directory.CreateDirectory(packageRoot);
            Directory.CreateDirectory(archiveRoot);
            Diagnostic? archiveError = await ExtractAndVerifyArchiveAsync(
                request,
                closure,
                admittedZip,
                packageRoot,
                archiveRoot,
                cancellationToken);
            if (archiveError is not null)
                return Refused(
                    request,
                    archiveError.Code,
                    archiveError.Message);

            string derivedManifest = Path.Combine(
                packageRoot,
                "npcmanager-package.json");
            await WriteDerivedManifestAsync(
                identity,
                closure.DataFiles,
                derivedManifest,
                cancellationToken);
            PackageVerifyResult verification =
                await packageVerifier.VerifyAsync(
                    new PackageVerifyRequest(
                        new WorkspacePath(derivedManifest)),
                    cancellationToken);
            if (!verification.Verified ||
                verification.Artifact is null)
                return Refused(
                    request,
                    "follower-finish-source-package-verification",
                    JoinErrors(verification.Diagnostics));

            string pluginPath = Path.Combine(
                packageRoot,
                closure.Plugin.RelativePath.Value.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            SkyrimFollowerFinishPluginSnapshot snapshot =
                await pluginReader.InspectAsync(
                    request,
                    new WorkspacePath(pluginPath),
                    cancellationToken);
            if (!snapshot.Valid)
                return snapshot;
            if (snapshot.PluginSha256 != request.Source.PluginSha256)
                return Refused(
                    request,
                    "follower-finish-source-plugin-hash",
                    "Typed source readback did not retain the request-bound plugin hash.");
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                UnauthorizedAccessException or ArgumentException or
                OverflowException)
        {
            return Refused(
                request,
                "follower-finish-source-zip-invalid",
                exception.Message);
        }
        finally
        {
            DeleteOwnedScratch(scratch);
        }
    }

    private void DeleteOwnedScratch(string? scratch)
    {
        if (string.IsNullOrEmpty(scratch) ||
            !Directory.Exists(scratch))
            return;
        string name = Path.GetFileName(scratch);
        if (!name.StartsWith(
                ScratchPrefix,
                StringComparison.Ordinal) ||
            !new WorkspacePath(scratch).IsUnder(workspaceRoot))
            throw new InvalidOperationException(
                "Refusing cleanup outside the exact GUID-owned analysis directory.");
        Directory.Delete(scratch, recursive: true);
    }

    private ImmutableArray<Diagnostic> ValidateReadPaths(
        SkyrimFollowerFinishRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(
            workspaceRoot,
            request.Source.Zip));
        diagnostics.AddRange(policy.EvaluateReadRoot(
            workspaceRoot,
            request.Source.PackageManifest));
        if (!File.Exists(request.Source.Zip.Value) ||
            Directory.Exists(request.Source.Zip.Value))
            diagnostics.Add(Error(
                "follower-finish-source-zip-missing",
                "The source ZIP is not an ordinary file."));
        if (!File.Exists(request.Source.PackageManifest.Value) ||
            Directory.Exists(request.Source.PackageManifest.Value))
            diagnostics.Add(Error(
                "follower-finish-source-manifest-missing",
                "The source package manifest is not an ordinary file."));
        return diagnostics.ToImmutable();
    }

    private static ManifestClosure? CloseManifest(
        SkyrimFollowerFinishRequest request,
        PackageManifestIdentity identity,
        out Diagnostic? error)
    {
        error = null;
        if (!string.Equals(
                identity.Edition,
                "skyrimse",
                StringComparison.Ordinal) ||
            !string.Equals(
                identity.OutputPlugin,
                request.Source.Plugin.Value,
                StringComparison.Ordinal) ||
            identity.TargetFormId != request.NpcFormId)
        {
            error = Error(
                "follower-finish-source-manifest-identity",
                "The loose manifest edition, output plugin, or target FormID does not match the request.");
            return null;
        }

        ImmutableArray<PackageManifestFile> dataFiles = identity.Files
            .Where(file => file.RelativePath.Value.StartsWith(
                "Data/",
                StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
        if (dataFiles.IsDefaultOrEmpty)
        {
            error = Error(
                "follower-finish-source-manifest-data",
                "The loose manifest declares no installable Data payload.");
            return null;
        }

        string pluginRelative = "Data/" + request.Source.Plugin.Value;
        PackageManifestFile[] plugins = dataFiles
            .Where(file => file.RelativePath.Value.EndsWith(
                ".esp",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (plugins.Length != 1 ||
            !string.Equals(
                plugins[0].RelativePath.Value,
                pluginRelative,
                StringComparison.OrdinalIgnoreCase))
        {
            error = Error(
                "follower-finish-source-manifest-plugin",
                "The loose manifest must declare exactly the requested root Data plugin.");
            return null;
        }

        string formId = request.NpcFormId.Value.ToString("X8");
        string faceGeomRelative =
            $"Data/meshes/actors/character/FaceGenData/FaceGeom/{request.Source.Plugin.Value}/{formId}.nif";
        string faceTintRelative =
            $"Data/textures/actors/character/FaceGenData/FaceTint/{request.Source.Plugin.Value}/{formId}.dds";
        PackageManifestFile? faceGeom = SinglePath(
            dataFiles,
            faceGeomRelative);
        PackageManifestFile? faceTint = SinglePath(
            dataFiles,
            faceTintRelative);
        if (faceGeom is null || faceTint is null)
        {
            error = Error(
                "follower-finish-source-manifest-facegen",
                "The loose manifest does not declare the exact FaceGeom and FaceTint paths.");
            return null;
        }
        if (plugins[0].Sha256 != request.Source.PluginSha256)
        {
            error = Error(
                "follower-finish-source-plugin-hash",
                "The loose manifest plugin hash does not match the request.");
            return null;
        }
        if (faceGeom.Sha256 != request.Source.FaceGeomSha256)
        {
            error = Error(
                "follower-finish-source-facegeom-hash",
                "The loose manifest FaceGeom hash does not match the request.");
            return null;
        }
        if (faceTint.Sha256 != request.Source.FaceTintSha256)
        {
            error = Error(
                "follower-finish-source-facetint-hash",
                "The loose manifest FaceTint hash does not match the request.");
            return null;
        }
        return new ManifestClosure(
            dataFiles,
            plugins[0],
            faceGeom,
            faceTint);
    }

    private static async ValueTask<Diagnostic?>
        ExtractAndVerifyArchiveAsync(
            SkyrimFollowerFinishRequest request,
            ManifestClosure closure,
            Stream admittedZip,
            string packageRoot,
            string archiveRoot,
            CancellationToken cancellationToken)
    {
        var expected = closure.DataFiles.ToDictionary(
            file => file.RelativePath.Value["Data/".Length..],
            StringComparer.OrdinalIgnoreCase);
        var expectedPaths = expected.Keys
            .Concat(GeneratedArchiveEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        using var archive = new ZipArchive(
            admittedZip,
            ZipArchiveMode.Read,
            leaveOpen: true);
        if (archive.Entries.Count is <= 0 or > MaximumEntryCount)
            return Error(
                "follower-finish-source-zip-count",
                $"The source ZIP must contain 1 through {MaximumEntryCount} entries.");

        int pluginCount = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? normalized = NormalizeArchivePath(entry.FullName);
            if (normalized is null)
                return Error(
                    "follower-finish-source-zip-path",
                    $"Archive entry '{entry.FullName}' is unsafe.");
            if (!seen.Add(normalized))
                return Error(
                    "follower-finish-source-zip-duplicate",
                    $"Archive entry '{normalized}' is duplicated case-insensitively.");
            if (!expectedPaths.Contains(normalized))
                return Error(
                    "follower-finish-source-zip-undeclared",
                    $"Archive entry '{normalized}' is not closed by the loose manifest and archive contract.");
            if (entry.Length is <= 0 or > MaximumEntryBytes)
                return Error(
                    "follower-finish-source-zip-entry-size",
                    $"Archive entry '{normalized}' is empty or too large.");
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaximumTotalBytes)
                return Error(
                    "follower-finish-source-zip-total-size",
                    "The expanded source ZIP exceeds the bounded analysis budget.");

            if (!normalized.Contains('/') &&
                normalized.EndsWith(
                    ".esp",
                    StringComparison.OrdinalIgnoreCase))
            {
                pluginCount++;
                if (!string.Equals(
                        normalized,
                        request.Source.Plugin.Value,
                        StringComparison.Ordinal))
                    return Error(
                        "follower-finish-source-zip-plugin",
                        "The source ZIP root plugin name does not exactly match the request.");
            }

            string destination;
            PackageManifestFile? declared = expected.GetValueOrDefault(
                normalized);
            if (declared is not null)
            {
                destination = Path.Combine(
                    packageRoot,
                    declared.RelativePath.Value.Replace(
                        '/',
                        Path.DirectorySeparatorChar));
            }
            else
            {
                destination = Path.Combine(
                    archiveRoot,
                    normalized.Replace(
                        '/',
                        Path.DirectorySeparatorChar));
            }
            string destinationRoot = declared is null
                ? archiveRoot
                : packageRoot;
            if (!IsUnder(destination, destinationRoot))
                return Error(
                    "follower-finish-source-zip-path-escape",
                    $"Archive entry '{normalized}' escaped its GUID-owned analysis root.");
            Directory.CreateDirectory(
                Path.GetDirectoryName(destination)!);
            await using Stream content = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long length = 0;
            int read;
            while ((read = await content.ReadAsync(
                       buffer,
                       cancellationToken)) > 0)
            {
                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
                hash.AppendData(buffer, 0, read);
                length += read;
            }
            if (length != entry.Length)
                return Error(
                    "follower-finish-source-zip-length",
                    $"Archive entry '{normalized}' changed length while materializing.");
            if (declared is not null)
            {
                var actual = new Sha256Hash(
                    Convert.ToHexString(hash.GetHashAndReset()));
                if (declared.ByteLength != length ||
                    declared.Sha256 != actual)
                    return Error(
                        "follower-finish-source-zip-artifact",
                        $"Archive entry '{normalized}' does not match its loose-manifest size/hash.");
            }
        }

        if (pluginCount != 1)
            return Error(
                "follower-finish-source-zip-plugin-count",
                "The source ZIP must contain exactly one root plugin.");
        if (!seen.SetEquals(expectedPaths))
            return Error(
                "follower-finish-source-zip-missing",
                "The source ZIP is missing a loose-manifest or required generated archive entry.");
        return null;
    }

    private static async ValueTask WriteDerivedManifestAsync(
        PackageManifestIdentity identity,
        ImmutableArray<PackageManifestFile> dataFiles,
        string destination,
        CancellationToken cancellationToken)
    {
        var artifacts = new JsonArray();
        foreach (PackageManifestFile file in dataFiles)
        {
            artifacts.Add(new JsonObject
            {
                ["kind"] = file.Kind,
                ["relativePath"] = file.RelativePath.Value,
                ["byteLength"] = file.ByteLength,
                ["sha256"] = file.Sha256.Value.ToLowerInvariant()
            });
        }
        var document = new JsonObject
        {
            ["schemaVersion"] = identity.SchemaVersion,
            ["edition"] = identity.Edition,
            ["presetFormat"] = identity.PresetFormat,
            ["sourcePreset"] = identity.SourcePreset,
            ["sourcePresetSha256"] =
                identity.SourcePresetSha256.Value.ToLowerInvariant(),
            ["sourcePlugin"] = identity.SourcePlugin,
            ["sourcePluginSha256"] =
                identity.SourcePluginSha256.Value.ToLowerInvariant(),
            ["outputPlugin"] = identity.OutputPlugin,
            ["targetFormId"] = identity.TargetFormId.ToString(),
            ["artifacts"] = artifacts
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        await output.WriteAsync(bytes, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static PackageManifestFile? SinglePath(
        ImmutableArray<PackageManifestFile> files,
        string path)
    {
        PackageManifestFile[] matches = files
            .Where(file => string.Equals(
                file.RelativePath.Value,
                path,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? NormalizeArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('\\') ||
            value.Contains('\0') ||
            value.Contains(':') ||
            value.StartsWith('/') ||
            value.EndsWith('/') ||
            Path.IsPathRooted(value))
            return null;
        string[] segments = value.Split('/');
        if (segments.Any(segment =>
                segment is "" or "." or ".." ||
                !string.Equals(
                    segment,
                    segment.Trim(),
                    StringComparison.Ordinal)))
            return null;
        return string.Join('/', segments);
    }

    private static Stream OpenZipStream(WorkspacePath path) =>
        new FileStream(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);

    private static SkyrimFollowerFinishPluginSnapshot Refused(
        SkyrimFollowerFinishRequest request,
        string code,
        string message) =>
        new(
            false,
            request.Source.Plugin,
            request.Source.PluginSha256,
            0,
            [],
            request.Allocation.Package,
            ["UNAVAILABLE 0x00000000"],
            ["UNAVAILABLE=" + new string('0', 64)],
            request.Hair.OldPackedRgb,
            new FormReference(
                request.Source.Plugin,
                request.Hair.ColorFormId),
            request.ExpectedDefaultOutfitNull,
            request.ExpectedFactionRanks,
            request.ExpectedRelationshipRank,
            request.ExpectedRelationshipRankRawDiscriminator,
            ["PACK", "CELL", "WRLD", "ACHR", "REFR"],
            [Error(code, message)]);

    private static bool IsUnder(string path, string root) =>
        new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    private static string JoinErrors(
        IEnumerable<Diagnostic> diagnostics)
    {
        string value = string.Join(
            " | ",
            diagnostics
                .Where(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}"));
        return string.IsNullOrEmpty(value)
            ? "The source authority was refused."
            : value;
    }

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record ManifestClosure(
        ImmutableArray<PackageManifestFile> DataFiles,
        PackageManifestFile Plugin,
        PackageManifestFile FaceGeom,
        PackageManifestFile FaceTint);
}
