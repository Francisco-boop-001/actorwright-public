using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Verifies a pipeline package manifest against the written files below its root.</summary>
public sealed class PackageVerifyService(
    PackageManifestReader reader,
    IExternalHeadPartInstallVerifier? externalInstallVerifier = null,
    IRaceMenuSelectedDependencyManifestReader? selectedDependencyReader = null) : IPackageVerifyService
{
    private const long MaximumArtifactBytes = 512L * 1024 * 1024;
    private const int MaximumFileCount = 10_000;
    private readonly IExternalHeadPartInstallVerifier externalInstallVerifier =
        externalInstallVerifier ?? new BethesdaExternalHeadPartInstallVerifier();
    private readonly IRaceMenuSelectedDependencyManifestReader selectedDependencyReader =
        selectedDependencyReader ?? new RaceMenuSelectedDependencyManifestReader();

    public async ValueTask<PackageVerifyResult> VerifyAsync(
        PackageVerifyRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = await reader.ReadAsync(request.ManifestPath, cancellationToken);
        var diagnostics = read.Diagnostics.ToBuilder();
        if (read.Identity is null || HasErrors(diagnostics))
            return new PackageVerifyResult(false, null, diagnostics.ToImmutable());

        var identity = read.Identity;
        var packageRoot = new WorkspacePath(Path.GetDirectoryName(identity.ManifestPath.Value)!);
        var rows = ImmutableArray.CreateBuilder<PackageFileVerification>(identity.Files.Length);
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFileName(identity.ManifestPath.Value)
        };
        foreach (var item in identity.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            declared.Add(item.RelativePath.Value);
            var artifactPath = Path.GetFullPath(Path.Combine(packageRoot.Value,
                item.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnder(artifactPath, packageRoot.Value))
            {
                diagnostics.Add(new Diagnostic("package-artifact-outside-root", DiagnosticSeverity.Error,
                    $"Package artifact '{item.RelativePath.Value}' escaped its package root."));
                continue;
            }
            if (!File.Exists(artifactPath))
            {
                diagnostics.Add(new Diagnostic("package-artifact-missing", DiagnosticSeverity.Error,
                    $"Package artifact '{item.RelativePath.Value}' is missing."));
                continue;
            }
            if (File.GetAttributes(artifactPath).HasFlag(FileAttributes.ReparsePoint) ||
                ContainsReparseBetween(packageRoot.Value, artifactPath))
            {
                diagnostics.Add(new Diagnostic("package-artifact-reparse", DiagnosticSeverity.Error,
                    $"Package artifact '{item.RelativePath.Value}' is a reparse path."));
                continue;
            }

            var info = new FileInfo(artifactPath);
            if (info.Length <= 0 || info.Length > MaximumArtifactBytes)
            {
                diagnostics.Add(new Diagnostic("package-artifact-size", DiagnosticSeverity.Error,
                    $"Package artifact '{item.RelativePath.Value}' is empty or exceeds 512 MiB."));
                continue;
            }
            var actualHash = await HashAsync(artifactPath, cancellationToken);
            var matches = info.Length == item.ByteLength &&
                string.Equals(actualHash.Value, item.Sha256.Value, StringComparison.OrdinalIgnoreCase);
            rows.Add(new PackageFileVerification(item.Kind, item.RelativePath, item.ByteLength, info.Length,
                item.Sha256, actualHash, matches));
            if (!matches)
                diagnostics.Add(new Diagnostic("package-artifact-hash-mismatch", DiagnosticSeverity.Error,
                    $"Package artifact '{item.RelativePath.Value}' failed its declared size/hash check."));
        }

        var actualFiles = EnumerateFiles(packageRoot.Value, diagnostics, MaximumFileCount);
        foreach (var actualFile in actualFiles)
        {
            var relative = Path.GetRelativePath(packageRoot.Value, actualFile)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!declared.Contains(relative))
                diagnostics.Add(new Diagnostic("package-undeclared-file", DiagnosticSeverity.Error,
                    $"Package contains undeclared file '{relative}'."));
        }

        var noUndeclaredFiles = !diagnostics.Any(item => item.Code == "package-undeclared-file");
        var artifact = new PackageVerificationArtifact("1", "npcmanager-package-verification",
            identity.Edition, identity.PresetFormat, identity.OutputPlugin, identity.TargetFormId,
            identity.ManifestPath, identity.ManifestSha256, rows.ToImmutable(), noUndeclaredFiles, true, false);

        bool packageBytesVerified = !HasErrors(diagnostics);
        bool malformedExternalEvidence = false;
        ExternalHeadPartInstallVerificationArtifact? externalArtifact = null;
        PackageManifestFile[] externalRows = identity.Files
            .Where(item => string.Equals(item.Kind,
                "external-headpart-dependencies", StringComparison.Ordinal))
            .ToArray();
        PackageManifestFile[] canonicalSelectedRows = identity.Files
            .Where(item => string.Equals(item.RelativePath.Value,
                RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath,
                StringComparison.Ordinal))
            .ToArray();
        PackageManifestFile[] canonicalExternalRows = externalRows
            .Where(item => string.Equals(item.RelativePath.Value,
                RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath,
                StringComparison.Ordinal))
            .ToArray();
        if (externalRows.Length > 0)
        {
            if (externalRows.Length != 1 || canonicalExternalRows.Length != 1)
            {
                malformedExternalEvidence = true;
                diagnostics.Add(new Diagnostic(
                    "external-headpart-package-row-cardinality",
                    DiagnosticSeverity.Error,
                    "A package must contain exactly one external-headpart-dependencies row at the canonical selected-dependency path."));
            }
            else
            {
                PackageManifestFile selectedRow = canonicalExternalRows[0];
                PackageManifestFile? outputRow = identity.Files.FirstOrDefault(item =>
                    string.Equals(Path.GetFileName(item.RelativePath.Value),
                        identity.OutputPlugin, StringComparison.OrdinalIgnoreCase));
                try
                {
                    var packageOutputPath = new WorkspacePath(Path.Combine(
                        packageRoot.Value,
                        (outputRow?.RelativePath.Value ?? identity.OutputPlugin)
                            .Replace('/', Path.DirectorySeparatorChar)));
                    var externalResult = await externalInstallVerifier.VerifyAsync(
                        new ExternalHeadPartInstallVerificationRequest(
                            packageRoot,
                            packageOutputPath,
                            new PluginName(identity.OutputPlugin),
                            outputRow?.Sha256 ?? ZeroHash,
                            new WorkspacePath(Path.Combine(
                                packageRoot.Value,
                                selectedRow.RelativePath.Value.Replace('/',
                                    Path.DirectorySeparatorChar))),
                            selectedRow.Sha256,
                            request.InstallContext,
                            request.RequireInstallDependencyAuthority,
                            identity.TargetFormId),
                        cancellationToken);
                    diagnostics.AddRange(externalResult.Diagnostics);
                    externalArtifact = externalResult.Artifact;
                }
                catch (ArgumentException exception)
                {
                    malformedExternalEvidence = true;
                    diagnostics.Add(new Diagnostic(
                        "external-headpart-package-identity",
                        DiagnosticSeverity.Error,
                        exception.Message));
                }
            }
        }
        else if (canonicalSelectedRows.Length > 0)
        {
            PackageManifestFile selectedRow = canonicalSelectedRows[0];
            RaceMenuSelectedDependencyManifestReadResult selected =
                await selectedDependencyReader.ReadAsync(
                    new WorkspacePath(Path.Combine(
                        packageRoot.Value,
                        selectedRow.RelativePath.Value.Replace('/',
                            Path.DirectorySeparatorChar))),
                    selectedRow.Sha256,
                    packageRoot,
                    cancellationToken);
            bool legacyNonExternal = IsLegacyNonExternalManifest(selected);
            if (!legacyNonExternal)
                diagnostics.AddRange(selected.Diagnostics);
            if (!legacyNonExternal && (selected.Artifact is null ||
                selected.Diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error) ||
                selected.Artifact.ExternalInstallDependencies is
                    { IsDefaultOrEmpty: false }))
            {
                malformedExternalEvidence = true;
                if (selected.Artifact is not null &&
                    selected.Diagnostics.All(item =>
                        item.Severity != DiagnosticSeverity.Error) &&
                    selected.Artifact.ExternalInstallDependencies is
                        { IsDefaultOrEmpty: false })
                    diagnostics.Add(new Diagnostic(
                        "external-headpart-package-row-kind",
                        DiagnosticSeverity.Error,
                        "The canonical selected-dependency path must use kind 'external-headpart-dependencies'."));
            }
        }

        bool verified = packageBytesVerified && !malformedExternalEvidence;
        if (externalArtifact is not null)
            externalArtifact = verified
                ? externalArtifact with { PackageIntegrity = true }
                : FailClosed(externalArtifact);

        return new PackageVerifyResult(verified, artifact, diagnostics.ToImmutable())
        {
            ExternalInstallDependencyVerification = externalArtifact
        };
    }

    private static readonly Sha256Hash ZeroHash =
        new(new string('0', 64));

    private static bool IsLegacyNonExternalManifest(
        RaceMenuSelectedDependencyManifestReadResult selected) =>
        selected.Artifact is null &&
        selected.Diagnostics.Length == 1 &&
        selected.Diagnostics[0].Severity == DiagnosticSeverity.Error &&
        (selected.Diagnostics[0].Message.Contains(
            "Only selected dependency manifest schema 3 is accepted",
            StringComparison.Ordinal) ||
         selected.Diagnostics[0].Message.Contains(
            "properties are not in canonical order",
            StringComparison.Ordinal));

    private static ExternalHeadPartInstallVerificationArtifact FailClosed(
        ExternalHeadPartInstallVerificationArtifact artifact)
    {
        return artifact with
        {
            PackageIntegrity = false,
            HistoricalSnapshotValid = null,
            VerifiedInstallSnapshot = null,
            CurrentInstallDependencyState =
                ExternalInstallDependencyState.DeclaredUnverified,
            InstallReady = false,
            InstallDependencyAuthority = false
        };
    }

    private static async ValueTask<Sha256Hash> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return new Sha256Hash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static IEnumerable<string> EnumerateFiles(string root,
        ImmutableArray<Diagnostic>.Builder diagnostics, int maximum)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory); }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("package-directory-read-failed", DiagnosticSeverity.Error, exception.Message));
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("package-directory-read-denied", DiagnosticSeverity.Error, exception.Message));
                continue;
            }
            foreach (var file in files)
            {
                if (++count > maximum)
                {
                    diagnostics.Add(new Diagnostic("package-file-count", DiagnosticSeverity.Error,
                        $"A package may contain at most {maximum} files."));
                    yield break;
                }
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("package-file-reparse", DiagnosticSeverity.Error,
                        $"Package file '{file}' is a reparse point."));
                    continue;
                }
                yield return file;
            }

            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(directory); }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("package-directory-read-failed", DiagnosticSeverity.Error, exception.Message));
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("package-directory-read-denied", DiagnosticSeverity.Error, exception.Message));
                continue;
            }
            foreach (var child in directories)
            {
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    diagnostics.Add(new Diagnostic("package-directory-reparse", DiagnosticSeverity.Error,
                        $"Package directory '{child}' is a reparse point."));
                else
                    pending.Push(child);
            }
        }
    }

    private static bool IsUnder(string path, string root) =>
        new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    private static bool ContainsReparseBetween(string root, string path)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(path);
        while (current.Length >= rootFull.Length)
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            if (string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase)) return false;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return false;
            current = parent;
        }
        return true;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}
