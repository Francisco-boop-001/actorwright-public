using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public sealed class ProviderMigrationService : IProviderMigrationService
{
    private readonly WorkspacePath workspaceRoot;
    private readonly IApplicationProviderResourceRegistry productRegistry;
    private readonly Func<string, bool> reparseAncestorProbe;
    private static readonly JsonSerializerOptions MigrationJsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

    public ProviderMigrationService(
        WorkspacePath workspaceRoot,
        IApplicationProviderResourceRegistry productRegistry)
    {
        this.workspaceRoot = workspaceRoot;
        this.productRegistry = productRegistry;
        reparseAncestorProbe = HasReparseAncestor;
    }

    internal ProviderMigrationService(
        WorkspacePath workspaceRoot,
        IApplicationProviderResourceRegistry productRegistry,
        Func<string, bool> reparseAncestorProbe) : this(workspaceRoot,
        productRegistry)
    {
        this.reparseAncestorProbe = reparseAncestorProbe ??
            throw new ArgumentNullException(nameof(reparseAncestorProbe));
    }

    public async ValueTask<ProviderMigrationResult> WriteReviewAsync(
        ProviderMigrationReviewWriteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            ValidateReviewTarget(request.Output, request.Review);
            ProviderMigrationPlan current = await RegenerateAsync(
                request.Review.SourceRequest,
                request.Review.SourceRequestSha256,
                request.Review.PlannedMigratedRequestRoot,
                cancellationToken).ConfigureAwait(false);
            byte[] supplied = JsonSerializer.SerializeToUtf8Bytes(
                request.Review, MigrationJsonOptions);
            if (!current.Review.Utf8Json.SequenceEqual(supplied))
                throw new InvalidDataException(
                    "The provider migration proposal drifted before review output.");
            await WriteNewAtomicallyAsync(
                request.Output.Value,
                current.Review.Utf8Json.ToArray(),
                current.Review.Sha256,
                cancellationToken).ConfigureAwait(false);
            return new ProviderMigrationResult(
                true,
                current.Review,
                [request.Output],
                [new Diagnostic(
                    "provider-migration-review-written",
                    DiagnosticSeverity.Info,
                    "The migration review was written; no build or migrated request directory was created.")]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderMigrationAdmissionException exception)
        {
            return Refused(exception.Code, exception.Message);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Refused("provider-migration-review-refused",
                exception.Message);
        }
    }

    public async ValueTask<ProviderMigrationResult> AcceptAsync(
        ProviderMigrationAcceptanceRequest request,
        CancellationToken cancellationToken)
    {
        string? candidate = null;
        try
        {
            ValidateAcceptanceTargets(request);
            byte[] reviewedBytes = await ReadBoundedAsync(
                request.ReviewedMigration,
                cancellationToken).ConfigureAwait(false);
            ProviderMigrationReviewDocument reviewed =
                ProviderMigrationDocumentCodec.DecodeReview(
                    reviewedBytes,
                    request.ReviewedMigrationSha256);
            if (reviewed.Value.BuildAuthority ||
                reviewed.Value.RuntimeAuthority)
                throw new InvalidDataException(
                    "The reviewed migration claims build or runtime authority.");

            ProviderMigrationPlan current = await RegenerateAsync(
                request.SourceRequest,
                request.SourceRequestSha256,
                request.MigratedRequestRoot,
                cancellationToken).ConfigureAwait(false);
            if (!current.Review.Utf8Json.SequenceEqual(
                    reviewed.Utf8Json) ||
                current.Review.Sha256 != reviewed.Sha256)
                throw new InvalidDataException(
                    "The reviewed provider migration is stale.");

            string parent = Path.GetDirectoryName(
                request.MigratedRequestRoot.Value) ??
                throw new InvalidDataException(
                    "The migrated request root has no parent.");
            candidate = Path.Combine(parent,
                "." + Path.GetFileName(request.MigratedRequestRoot.Value) +
                $".candidate-{Environment.ProcessId}-{Guid.NewGuid():N}");
            if (Directory.Exists(candidate) || File.Exists(candidate))
                throw new InvalidDataException(
                    "The migration candidate unexpectedly exists.");
            Directory.CreateDirectory(candidate);
            foreach ((string name, ImmutableArray<byte> content) in
                     current.Files.OrderBy(item => item.Key,
                         StringComparer.Ordinal))
            {
                string output = Path.Combine(candidate, name);
                await WriteNewAsync(output, content.ToArray(),
                    cancellationToken).ConfigureAwait(false);
            }
            VerifyCandidate(candidate, current.Review.Value.PlannedFiles);
            Directory.Move(candidate, request.MigratedRequestRoot.Value);
            candidate = null;
            VerifyCandidate(request.MigratedRequestRoot.Value,
                current.Review.Value.PlannedFiles);
            ImmutableArray<WorkspacePath> outputs = current.Review.Value
                .PlannedFiles.Select(item => new WorkspacePath(Path.Combine(
                    request.MigratedRequestRoot.Value,
                    item.RelativePath))).ToImmutableArray();
            return new ProviderMigrationResult(
                true,
                reviewed,
                outputs,
                [new Diagnostic(
                    "provider-migration-complete",
                    DiagnosticSeverity.Info,
                    "The reviewed three-file migrated request was promoted atomically.")]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProviderMigrationAdmissionException exception)
        {
            return Refused(exception.Code, exception.Message);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Refused("provider-migration-acceptance-refused",
                exception.Message);
        }
        finally
        {
            if (candidate is not null && Directory.Exists(candidate))
                Directory.Delete(candidate, recursive: true);
        }
    }

    private async ValueTask<ProviderMigrationPlan> RegenerateAsync(
        WorkspacePath sourceRequest,
        Sha256Hash expectedRequestHash,
        WorkspacePath migratedRoot,
        CancellationToken cancellationToken)
    {
        byte[] requestBytes = await ReadBoundedAsync(sourceRequest,
            cancellationToken).ConfigureAwait(false);
        if (Hash(requestBytes) != expectedRequestHash)
            throw new InvalidDataException(
                "The source request changed after migration review.");
        using JsonDocument requestJson = JsonDocument.Parse(requestBytes);
        JsonElement bundle = requestJson.RootElement.GetProperty(
            "presetBundle");
        string relativeBundle = bundle.GetProperty("manifestPath")
            .GetString() ?? throw new InvalidDataException(
                "The source request bundle path is absent.");
        var bundlePath = Resolve(relativeBundle);
        var bundleHash = new Sha256Hash(bundle.GetProperty(
            "manifestSha256").GetString() ?? "");
        byte[] bundleBytes = await ReadBoundedAsync(bundlePath,
            cancellationToken).ConfigureAwait(false);
        if (Hash(bundleBytes) != bundleHash)
            throw new InvalidDataException(
                "The source preset bundle changed after migration review.");
        if (!productRegistry.TryGetDefaultBlankNpcFixture(
                out ProductFixtureBundleReference? product) || product is null)
            throw Admission(
                "product-provider-unavailable",
                "The optional bundled provider is not installed. Use a workspace-provider request built from legally obtained local assets.");
        ApplicationProviderResourceAdmissionResult admitted =
            productRegistry.Admit(
                product,
                new FormId(0x0000_0800),
                GameEdition.SkyrimSpecialEdition,
                NpcSex.Female);
        if (!admitted.Accepted || admitted.Authority is null)
            throw Admission(
                "product-provider-unavailable",
                string.Join(" ", admitted.Diagnostics.Select(item =>
                    item.Message)));
        return new ProviderMigrationDocumentCodec(workspaceRoot).CreatePlan(
            sourceRequest,
            expectedRequestHash,
            requestBytes,
            bundlePath,
            bundleHash,
            bundleBytes,
            product,
            migratedRoot);
    }

    private void ValidateReviewTarget(
        WorkspacePath output,
        ProviderMigrationReviewArtifact review)
    {
        ValidateNewPath(output, expectDirectory: false);
        ValidateNewPath(review.PlannedMigratedRequestRoot,
            expectDirectory: true);
        if (Same(output.Value, review.SourceRequest.Value))
            throw Admission("provider-migration-target-source-collision",
                "The review target collides with the migration source request.");
        if (IsNested(output.Value, review.PlannedMigratedRequestRoot.Value))
            throw Admission("provider-migration-target-review-collision",
                "The review target collides with the planned migration root.");
    }

    private void ValidateAcceptanceTargets(
        ProviderMigrationAcceptanceRequest request)
    {
        ValidateExistingFile(request.SourceRequest);
        ValidateExistingFile(request.ReviewedMigration);
        ValidateNewPath(request.MigratedRequestRoot,
            expectDirectory: true);
        if (Same(request.MigratedRequestRoot.Value, request.SourceRequest.Value))
            throw Admission("provider-migration-target-source-collision",
                "The migration target collides with its source request.");
        if (Same(request.MigratedRequestRoot.Value,
                request.ReviewedMigration.Value))
            throw Admission("provider-migration-target-review-collision",
                "The migration target collides with its review document.");
        string[] paths =
        [
            request.SourceRequest.Value,
            request.ReviewedMigration.Value,
            request.MigratedRequestRoot.Value
        ];
        for (var left = 0; left < paths.Length; left++)
            for (var right = left + 1; right < paths.Length; right++)
                if (IsNested(paths[left], paths[right]))
                    throw Admission("provider-migration-target-nested",
                        "Migration source, review, and output paths must be disjoint and non-nested.");
    }

    private void ValidateNewPath(
        WorkspacePath path,
        bool expectDirectory)
    {
        if (!path.IsUnder(workspaceRoot) || path == workspaceRoot)
            throw Admission("provider-migration-target-outside-workspace",
                "The new migration target is outside the workspace.");
        if (File.Exists(path.Value) || Directory.Exists(path.Value))
            throw Admission("provider-migration-target-exists",
                "The new migration target already exists.");
        if (reparseAncestorProbe(Path.GetDirectoryName(path.Value)!))
            throw Admission("provider-migration-target-reparse-ancestor",
                "The new migration target has a reparse-point ancestor.");
    }

    private void ValidateExistingFile(WorkspacePath path)
    {
        var info = new FileInfo(path.Value);
        if (!path.IsUnder(workspaceRoot) || !info.Exists ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            HasReparseAncestor(info.DirectoryName!))
            throw new InvalidDataException(
                "A migration input is not one admitted ordinary workspace file.");
    }

    private bool HasReparseAncestor(string path)
    {
        DirectoryInfo? current = new(path);
        while (current is not null)
        {
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
            if (Same(current.FullName, workspaceRoot.Value))
                return false;
            current = current.Parent;
        }
        return true;
    }

    private WorkspacePath Resolve(string relative)
    {
        var asset = new AssetPath(relative);
        var path = new WorkspacePath(Path.Combine(workspaceRoot.Value,
            asset.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.IsUnder(workspaceRoot))
            throw new InvalidDataException(
                "The source preset-bundle path escaped the workspace.");
        return path;
    }

    private static async ValueTask WriteNewAtomicallyAsync(
        string output,
        byte[] bytes,
        Sha256Hash expected,
        CancellationToken cancellationToken)
    {
        string parent = Path.GetDirectoryName(output) ??
            throw new InvalidDataException("The review output has no parent.");
        Directory.CreateDirectory(parent);
        string candidate = Path.Combine(parent,
            "." + Path.GetFileName(output) +
            $".candidate-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            await WriteNewAsync(candidate, bytes, cancellationToken)
                .ConfigureAwait(false);
            if (Hash(await File.ReadAllBytesAsync(candidate,
                    cancellationToken).ConfigureAwait(false)) != expected)
                throw new InvalidDataException(
                    "The review candidate hash drifted.");
            File.Move(candidate, output, overwrite: false);
            if (Hash(await File.ReadAllBytesAsync(output,
                    cancellationToken).ConfigureAwait(false)) != expected)
                throw new InvalidDataException(
                    "The promoted review hash drifted.");
        }
        finally
        {
            if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    private static async ValueTask WriteNewAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void VerifyCandidate(
        string root,
        ImmutableArray<ProviderMigrationPlannedFile> planned)
    {
        string[] actual = Directory.EnumerateFiles(root, "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = planned.Select(item => item.RelativePath)
            .Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal) ||
            Directory.EnumerateDirectories(root, "*",
                SearchOption.AllDirectories).Any())
            throw new InvalidDataException(
                "The migration candidate inventory is not exact.");
        foreach (ProviderMigrationPlannedFile file in planned)
        {
            string path = Path.Combine(root, file.RelativePath);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.ByteLength ||
                Hash(File.ReadAllBytes(path)) != file.Sha256)
                throw new InvalidDataException(
                    $"Migration output '{file.RelativePath}' failed exact readback.");
        }
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.Value);
        if (!info.Exists || info.Length <= 0 || info.Length > 4 * 1024 * 1024 ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException(
                "A migration input is absent, empty, oversized, or not ordinary.");
        return await File.ReadAllBytesAsync(path.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsNested(string left, string right) =>
        Same(left, right) || IsUnder(left, right) || IsUnder(right, left);

    private static bool IsUnder(string path, string root)
    {
        var candidate = new WorkspacePath(path);
        var ancestor = new WorkspacePath(root);
        return candidate != ancestor && candidate.IsUnder(ancestor);
    }

    private static bool Same(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static bool IsExpected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or ArgumentException or
            NotSupportedException or OverflowException;

    private static ProviderMigrationResult Refused(
        string code,
        string message) =>
        new(false, null, [],
            [new Diagnostic(code, DiagnosticSeverity.Error, message)]);

    private static ProviderMigrationAdmissionException Admission(
        string code,
        string message) => new(code, message);
}

internal sealed class ProviderMigrationAdmissionException(
    string code,
    string message) : IOException(message)
{
    public string Code { get; } = code;
}
