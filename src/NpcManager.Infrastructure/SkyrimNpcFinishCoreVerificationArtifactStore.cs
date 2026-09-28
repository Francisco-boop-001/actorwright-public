using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed record SkyrimNpcFinishCoreVerificationArtifactDocument(
    SkyrimNpcFinishCoreVerification Verification,
    WorkspacePath Path,
    long Size,
    string Sha256,
    ImmutableArray<byte> Utf8Json,
    WorkflowArtifactBinding Artifact);

public sealed class SkyrimNpcFinishCoreVerifiedManifestDocument
{
    internal SkyrimNpcFinishCoreVerifiedManifestDocument(
        SkyrimNpcFinishCoreManifest manifest,
        WorkspacePath path,
        long size,
        string sha256,
        ImmutableArray<byte> utf8Json,
        WorkspacePath archivePath,
        long archiveSize,
        string archiveSha256)
    {
        Manifest = manifest;
        Path = path;
        Size = size;
        Sha256 = sha256;
        Utf8Json = utf8Json;
        ArchivePath = archivePath;
        ArchiveSize = archiveSize;
        ArchiveSha256 = archiveSha256;
    }

    public SkyrimNpcFinishCoreManifest Manifest { get; }

    public WorkspacePath Path { get; }

    public long Size { get; }

    public string Sha256 { get; }

    public ImmutableArray<byte> Utf8Json { get; }

    public WorkspacePath ArchivePath { get; }

    public long ArchiveSize { get; }

    public string ArchiveSha256 { get; }
}

public sealed class SkyrimNpcFinishCoreVerificationArtifactException :
    IOException
{
    public SkyrimNpcFinishCoreVerificationArtifactException(
        string code,
        string message,
        Exception? innerException = null) :
        base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Persists independent Finish Verify evidence outside the package whose
/// manifest and archive it authenticates. It deliberately exposes no command
/// registration; callers must supply one explicit fresh K-local output.
/// </summary>
public sealed class SkyrimNpcFinishCoreVerificationArtifactStore
{
    private const int MaximumArtifactBytes = 64 * 1024;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const string ProducerCommand = "npc finish verify";
    private const string Role = "Finish Core verification artifact";

    private readonly IWorkspacePolicy workspacePolicy;
    private readonly WorkspacePath labRoot;
    private readonly FaceGeomHairRegionsPinnedFileSystem fileSystem;

    public SkyrimNpcFinishCoreVerificationArtifactStore(
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot)
    {
        ArgumentNullException.ThrowIfNull(workspacePolicy);
        this.workspacePolicy = workspacePolicy;
        this.labRoot = labRoot;
        if (!string.Equals(
                Path.GetPathRoot(labRoot.Value),
                @"K:\",
                StringComparison.OrdinalIgnoreCase))
            throw Refused(
                "finish-verification-path-outside-lab",
                "The Finish verification lab root must remain on K:.");
        RequirePolicyAllowed(labRoot, write: true);
        fileSystem = new FaceGeomHairRegionsPinnedFileSystem(labRoot);
    }

    public SkyrimNpcFinishCoreVerifiedManifestDocument LoadManifest(
        WorkspacePath path,
        string expectedSha256)
    {
        RequireHash(
            expectedSha256,
            "finish-verification-manifest-hash-invalid",
            "The expected manifest digest must be one uppercase SHA-256.");
        RequireWithinLab(path);
        RequirePolicyAllowed(path, write: false);
        byte[] bytes;
        try
        {
            using FaceGeomHairRegionsPinnedReadFile source =
                fileSystem.OpenRead(path, "Finish Core manifest");
            bytes = source.ReadExact(MaximumManifestBytes);
        }
        catch (SkyrimNpcFinishCoreVerificationArtifactException)
        {
            throw;
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("1..", StringComparison.Ordinal))
        {
            throw Refused(
                "finish-verification-manifest-size-invalid",
                "The Finish manifest is outside the admitted 1..4 MiB range.",
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw Refused(
                "finish-verification-manifest-not-ordinary",
                "The Finish manifest must be one existing ordinary file.",
                exception);
        }
        string observedSha256 = Hash(bytes);
        if (!string.Equals(
                observedSha256,
                expectedSha256,
                StringComparison.Ordinal))
            throw Refused(
                "finish-verification-manifest-hash-mismatch",
                "The Finish manifest SHA-256 does not match the expected physical digest.");

        SkyrimNpcFinishCoreManifest manifest;
        try
        {
            manifest = SkyrimNpcFinishCoreDocumentCodec.ParseManifest(
                bytes,
                labRoot);
            byte[] canonical = SkyrimNpcFinishCoreDocumentCodec
                .SerializeManifest(manifest, labRoot);
            if (!canonical.AsSpan().SequenceEqual(bytes))
                throw Refused(
                    "finish-verification-manifest-noncanonical",
                    "The Finish manifest is not canonical Actorwright JSON.");
        }
        catch (SkyrimNpcFinishCoreVerificationArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                NotSupportedException)
        {
            throw Refused(
                "finish-verification-manifest-invalid",
                "The Finish manifest is not valid strict JSON.",
                exception);
        }
        VerifiedManifestArchive archive = RequireManifest(manifest);
        return new SkyrimNpcFinishCoreVerifiedManifestDocument(
            manifest,
            path,
            bytes.LongLength,
            observedSha256,
            bytes.ToImmutableArray(),
            archive.Path,
            archive.Size,
            archive.Sha256);
    }

    public async ValueTask<SkyrimNpcFinishCoreVerificationArtifactDocument>
        WriteNewAsync(
            SkyrimNpcFinishCoreVerification verification,
            SkyrimNpcFinishCoreVerifiedManifestDocument verifiedManifest,
            string requestDigest,
            WorkspacePath output,
            CancellationToken cancellationToken)
    {
        SkyrimNpcFinishCoreVerifiedManifestDocument currentManifest =
            RequireCurrentManifest(verifiedManifest);
        RequireBindings(
            verification,
            currentManifest,
            requestDigest);
        WorkspacePath packageRoot =
            currentManifest.Manifest.PackageRoot!.Value;
        RequireFreshOutput(output, packageRoot);
        byte[] canonical = ComputeCanonicalBytes(verification);
        string expectedSha256 = Hash(canonical);
        var temporary = new WorkspacePath(
            output.Value + ".tmp-" + Guid.NewGuid().ToString("N"));
        FaceGeomHairRegionsOwnedFile? staged = null;
        bool promoted = false;
        try
        {
            staged = fileSystem.CreateOwned(temporary, output, Role);
            byte[] stagedReadback = await staged.WriteAndReadbackAsync(
                canonical,
                MaximumArtifactBytes,
                cancellationToken);
            RequireExactReadback(
                stagedReadback,
                canonical,
                expectedSha256,
                "finish-verification-staged-readback-mismatch",
                "The staged Finish verification artifact failed exact readback.");
            staged.PromoteNoOverwrite();
            promoted = true;
            byte[] promotedReadback = await staged.ReadbackExactAsync(
                MaximumArtifactBytes,
                CancellationToken.None);
            RequireExactReadback(
                promotedReadback,
                canonical,
                expectedSha256,
                "finish-verification-promoted-readback-mismatch",
                "The promoted Finish verification artifact changed during publication.");
            staged.Dispose();
            staged = null;
            byte[] pinnedReopen;
            try
            {
                using FaceGeomHairRegionsPinnedReadFile reopenedSource =
                    fileSystem.OpenRead(output, Role);
                pinnedReopen = reopenedSource.ReadExact(MaximumArtifactBytes);
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or
                    UnauthorizedAccessException)
            {
                throw Refused(
                    "finish-verification-promoted-readback-mismatch",
                    "The promoted Finish verification artifact could not be pinned and reopened.",
                    exception);
            }
            RequireExactReadback(
                pinnedReopen,
                canonical,
                expectedSha256,
                "finish-verification-promoted-readback-mismatch",
                "The promoted Finish verification artifact changed during pinned reopen.");
            SkyrimNpcFinishCoreVerification reopened = ParseCanonical(
                pinnedReopen);
            currentManifest = RequireCurrentManifest(currentManifest);
            RequireBindings(
                reopened,
                currentManifest,
                requestDigest);
            return Document(
                reopened,
                output,
                pinnedReopen,
                expectedSha256,
                currentManifest,
                requestDigest);
        }
        catch (OperationCanceledException) when (!promoted)
        {
            if (staged is not null &&
                !staged.TryDelete(out string? cleanupFailure))
                throw Refused(
                    "finish-verification-write-failed",
                    $"The cancelled Finish verification write could not clean its owned temporary: {cleanupFailure}");
            throw;
        }
        catch (Exception exception)
        {
            SkyrimNpcFinishCoreVerificationArtifactException primary =
                NormalizeWriteFailure(exception, output);
            if (!promoted && staged is not null &&
                !staged.TryDelete(out string? cleanupFailure))
                throw Refused(
                    primary.Code,
                    $"{primary.Message} Cleanup also failed: {cleanupFailure}",
                    primary);
            throw primary;
        }
        finally
        {
            staged?.Dispose();
        }
    }

    public SkyrimNpcFinishCoreVerificationArtifactDocument Load(
        WorkspacePath path,
        string expectedSha256,
        SkyrimNpcFinishCoreVerifiedManifestDocument verifiedManifest,
        string requestDigest)
    {
        RequireHash(
            expectedSha256,
            "finish-verification-hash-invalid",
            "The expected verification digest must be one uppercase SHA-256.");
        SkyrimNpcFinishCoreVerifiedManifestDocument currentManifest =
            RequireCurrentManifest(verifiedManifest);
        WorkspacePath packageRoot =
            currentManifest.Manifest.PackageRoot!.Value;
        RequireExternal(path, packageRoot);
        RequirePolicyAllowed(path, write: false);
        byte[] bytes;
        try
        {
            using FaceGeomHairRegionsPinnedReadFile source =
                fileSystem.OpenRead(path, Role);
            bytes = source.ReadExact(MaximumArtifactBytes);
        }
        catch (SkyrimNpcFinishCoreVerificationArtifactException)
        {
            throw;
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("1..", StringComparison.Ordinal))
        {
            throw Refused(
                "finish-verification-file-size-invalid",
                "The Finish verification artifact is outside the admitted 1..64 KiB range.",
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw Refused(
                "finish-verification-file-not-ordinary",
                "The Finish verification artifact must be one existing ordinary file.",
                exception);
        }
        string observedSha256 = Hash(bytes);
        if (!string.Equals(
                observedSha256,
                expectedSha256,
                StringComparison.Ordinal))
            throw Refused(
                "finish-verification-hash-mismatch",
                "The Finish verification artifact SHA-256 does not match the expected digest.");
        SkyrimNpcFinishCoreVerification verification = ParseCanonical(bytes);
        RequireBindings(
            verification,
            currentManifest,
            requestDigest);
        return Document(
            verification,
            path,
            bytes,
            observedSha256,
            currentManifest,
            requestDigest);
    }

    private byte[] ComputeCanonicalBytes(
        SkyrimNpcFinishCoreVerification verification)
    {
        try
        {
            byte[] bytes = SkyrimNpcFinishCoreDocumentCodec
                .SerializeVerification(verification, labRoot);
            if (bytes.Length is <= 0 or > MaximumArtifactBytes)
                throw Refused(
                    "finish-verification-file-size-invalid",
                    "The canonical Finish verification artifact is outside the admitted 1..64 KiB range.");
            return bytes;
        }
        catch (SkyrimNpcFinishCoreVerificationArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                NotSupportedException)
        {
            throw Refused(
                "finish-verification-contract-invalid",
                "The Finish verification contract is invalid.",
                exception);
        }
    }

    private SkyrimNpcFinishCoreVerification ParseCanonical(byte[] bytes)
    {
        try
        {
            SkyrimNpcFinishCoreVerification verification =
                SkyrimNpcFinishCoreDocumentCodec.ParseVerification(bytes);
            byte[] canonical = SkyrimNpcFinishCoreDocumentCodec
                .SerializeVerification(verification, labRoot);
            if (!canonical.AsSpan().SequenceEqual(bytes))
                throw Refused(
                    "finish-verification-json-noncanonical",
                    "The Finish verification artifact is not canonical Actorwright JSON.");
            return verification;
        }
        catch (SkyrimNpcFinishCoreVerificationArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                NotSupportedException)
        {
            throw Refused(
                "finish-verification-json-invalid",
                "The Finish verification artifact is not valid strict JSON.",
                exception);
        }
    }

    private static void RequireBindings(
        SkyrimNpcFinishCoreVerification verification,
        SkyrimNpcFinishCoreVerifiedManifestDocument verifiedManifest,
        string requestDigest)
    {
        ArgumentNullException.ThrowIfNull(verification);
        RequireHash(
            requestDigest,
            "finish-verification-binding-invalid",
            "The workflow-envelope request digest must be one uppercase SHA-256.");
        SkyrimNpcFinishCoreManifest manifest = verifiedManifest.Manifest;
        bool zeroCounts =
            verification.TypedForbiddenCounts is not null &&
            verification.RawForbiddenCounts is not null &&
            verification.TypedForbiddenCounts.All(row => row.Value == 0) &&
            verification.RawForbiddenCounts.All(row => row.Value == 0);
        bool external = string.Equals(
            manifest.Schema,
            SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier,
            StringComparison.Ordinal);
        bool schemaMatches = external
            ? string.Equals(
                verification.Schema,
                SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier,
                StringComparison.Ordinal)
            : string.Equals(
                verification.Schema,
                SkyrimNpcFinishCoreVerification.SchemaIdentifier,
                StringComparison.Ordinal);
        if (!schemaMatches ||
            verification.Status !=
                SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired ||
            !verification.Verified || verification.PlacementIncluded ||
            verification.RuntimeAuthority || verification.VisualAuthority ||
            verification.PluginSha256 is null ||
            verification.PackageTreeSha256 is null ||
            verification.SourcePackageTreeSha256 is null ||
            !zeroCounts || verification.RuntimeIdentity is null ||
            verification.RuntimeIdentity.BaseNpc is null ||
            verification.RuntimeIdentity.PlacedReference is not null ||
            verification.RuntimeIdentity.PlacementIncluded ||
            verification.PluginSha256 != manifest.PluginSha256 ||
            verification.PackageTreeSha256 != manifest.PackageTreeSha256 ||
            verification.SourcePackageTreeSha256 !=
                manifest.SourcePackageTreeSha256 ||
            (!external &&
             (verification.ArchiveSha256 is null ||
              (manifest.ArchiveSha256 is not null &&
               verification.ArchiveSha256 != manifest.ArchiveSha256) ||
              !string.Equals(
                  verification.ArchiveSha256.Value.Value,
                  verifiedManifest.ArchiveSha256,
                  StringComparison.OrdinalIgnoreCase))) ||
            verification.RuntimeIdentity != manifest.RuntimeIdentity ||
            verification.RuntimeIdentity.BaseNpc != manifest.BaseNpc)
            throw Refused(
                "finish-verification-contract-invalid",
                "Only successful static Finish verification bound to the current strict manifest may be persisted.");

        if (!external)
            return;

        if (manifest.ExternalHeadParts is not
                SkyrimNpcFinishCoreExternalHeadPartManifestAuthority manifestExternal ||
            verification.ExternalHeadParts is not
                SkyrimNpcFinishCoreExternalHeadPartVerification verificationExternal)
            throw Refused(
                "finish-verification-contract-invalid",
                "External Finish verification must retain manifest and verification dependency authority.");

        ExternalHeadPartInstallVerificationArtifact artifact =
            verificationExternal.Verification;
        if (artifact.HistoricalSnapshotValid != true ||
            artifact.CurrentInstallDependencyState !=
                ExternalInstallDependencyState.Verified ||
            !artifact.InstallReady ||
            !artifact.InstallDependencyAuthority ||
            artifact.VerifiedInstallSnapshot is not
                ExternalHeadPartVerifiedInstallSnapshot snapshot ||
            !artifact.DescriptorIds.SequenceEqual(
                manifestExternal.Descriptors.Select(item => item.DescriptorId)) ||
            !snapshot.DescriptorIds.SequenceEqual(artifact.DescriptorIds) ||
            snapshot.SelectedManifestSha256 !=
                manifestExternal.SelectedManifestSha256 ||
            !SnapshotsEqual(snapshot, manifestExternal.VerifiedInstallSnapshot))
            throw Refused(
                "finish-verification-contract-invalid",
                "External Finish verification must bind the current verified install snapshot to the manifest descriptor closure.");
    }

    private static bool SnapshotsEqual(
        ExternalHeadPartVerifiedInstallSnapshot left,
        ExternalHeadPartVerifiedInstallSnapshot right) =>
        left.SelectedManifestSha256 == right.SelectedManifestSha256 &&
        left.DescriptorIds.SequenceEqual(right.DescriptorIds) &&
        FingerprintsEqual(left.ContextFingerprint, right.ContextFingerprint);

    private static bool FingerprintsEqual(
        ExternalHeadPartInstallContextFingerprint left,
        ExternalHeadPartInstallContextFingerprint right) =>
        left.Sha256 == right.Sha256 &&
        left.Observations.Length == right.Observations.Length &&
        left.Observations.Zip(right.Observations).All(pair =>
            string.Equals(pair.First.Kind, pair.Second.Kind, StringComparison.Ordinal) &&
            string.Equals(
                pair.First.PortableIdentity,
                pair.Second.PortableIdentity,
                StringComparison.Ordinal) &&
            pair.First.Sha256 == pair.Second.Sha256 &&
            pair.First.ByteLength == pair.Second.ByteLength &&
            pair.First.Order == pair.Second.Order);

    private SkyrimNpcFinishCoreVerifiedManifestDocument RequireCurrentManifest(
        SkyrimNpcFinishCoreVerifiedManifestDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        SkyrimNpcFinishCoreVerifiedManifestDocument reopened = LoadManifest(
            document.Path,
            document.Sha256);
        if (reopened.Size != document.Size ||
            !reopened.Utf8Json.AsSpan().SequenceEqual(
                document.Utf8Json.AsSpan()) ||
            reopened.ArchivePath != document.ArchivePath ||
            reopened.ArchiveSize != document.ArchiveSize ||
            !string.Equals(
                reopened.ArchiveSha256,
                document.ArchiveSha256,
                StringComparison.Ordinal))
            throw Refused(
                "finish-verification-manifest-stale",
                "The supplied typed Finish manifest document changed.");
        return reopened;
    }

    private VerifiedManifestArchive RequireManifest(
        SkyrimNpcFinishCoreManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        bool external = string.Equals(
            manifest.Schema,
            SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier,
            StringComparison.Ordinal);
        bool complete =
            string.Equals(
                manifest.Schema,
                external
                    ? SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier
                    : SkyrimNpcFinishCoreManifest.SchemaIdentifier,
                StringComparison.Ordinal) &&
            (!external || manifest.ExternalHeadParts is not null) &&
            manifest.Plugin is not null &&
            manifest.PluginSha256 is not null &&
            manifest.BaseNpc is not null &&
            manifest.RequestSha256 is not null &&
            manifest.ProposalSha256 is not null &&
            !manifest.PlacementIncluded &&
            !manifest.RuntimeAuthority &&
            !manifest.VisualAuthority &&
            manifest.PackageRoot is not null &&
            manifest.Archive is not null &&
            manifest.SourcePackageTreeSha256 is not null &&
            manifest.PackageTreeSha256 is not null &&
            manifest.RuntimeIdentity is not null &&
            manifest.RuntimeIdentity.BaseNpc == manifest.BaseNpc &&
            manifest.RuntimeIdentity.PlacedReference is null &&
            !manifest.RuntimeIdentity.PlacementIncluded &&
            !manifest.Evidence.Files.IsDefault &&
            manifest.Evidence.PackageTreeSha256 ==
                manifest.PackageTreeSha256 &&
            manifest.Evidence.SourcePackageTreeSha256 ==
                manifest.SourcePackageTreeSha256;
        if (!complete)
            throw Refused(
                "finish-verification-manifest-invalid",
                "The Finish manifest is incomplete or grants unsupported placement, runtime, or visual authority.");

        WorkspacePath packageRoot = manifest.PackageRoot!.Value;
        RequireWithinLab(packageRoot);
        if (!Directory.Exists(packageRoot.Value))
            throw Refused(
                "finish-verification-package-root-invalid",
                "The manifest package root must be one existing directory.");
        RequirePolicyAllowed(packageRoot, write: false);
        WorkspacePath archive = manifest.Archive!.Value;
        RequireWithinLab(archive);
        RequirePolicyAllowed(archive, write: false);
        try
        {
            using FaceGeomHairRegionsPinnedReadFile archiveSource =
                fileSystem.OpenRead(archive, "Finish Core archive");
            if (archiveSource.Length <= 0)
                throw Refused(
                    "finish-verification-archive-invalid",
                    "The manifest archive cannot be empty.");
            string archiveSha256 = archiveSource.ComputeSha256();
            if (manifest.ArchiveSha256 is not null &&
                !string.Equals(
                    archiveSha256,
                    manifest.ArchiveSha256.Value.Value,
                    StringComparison.OrdinalIgnoreCase))
                throw Refused(
                    "finish-verification-archive-hash-mismatch",
                    "The optional manifest archive SHA-256 does not match its current bytes.");
            return new VerifiedManifestArchive(
                archive,
                archiveSource.Length,
                archiveSha256);
        }
        catch (SkyrimNpcFinishCoreVerificationArtifactException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                UnauthorizedAccessException)
        {
            throw Refused(
                "finish-verification-archive-invalid",
                "The manifest archive must be one existing ordinary file.",
                exception);
        }
    }

    private void RequireFreshOutput(
        WorkspacePath output,
        WorkspacePath verifiedPackageRoot)
    {
        RequireExternal(output, verifiedPackageRoot);
        RequirePolicyAllowed(output, write: true);
        string? parentValue = Path.GetDirectoryName(output.Value);
        if (string.IsNullOrWhiteSpace(parentValue) ||
            !Directory.Exists(parentValue))
            throw Refused(
                "finish-verification-parent-missing",
                "The verification output parent must already exist.");
        RequirePolicyAllowed(new WorkspacePath(parentValue), write: true);
        if (File.Exists(output.Value) || Directory.Exists(output.Value))
            throw Refused(
                "finish-verification-output-exists",
                "The verification output must be a fresh path.");
    }

    private void RequireExternal(
        WorkspacePath output,
        WorkspacePath verifiedPackageRoot)
    {
        RequireWithinLab(output);
        if (output.IsUnder(verifiedPackageRoot) ||
            verifiedPackageRoot.IsUnder(output))
            throw Refused(
                "finish-verification-output-overlap",
                "Independent Finish verification evidence must remain outside the verified package root.");
    }

    private void RequireWithinLab(WorkspacePath path)
    {
        if (!path.IsUnder(labRoot))
            throw Refused(
                "finish-verification-path-outside-lab",
                "Finish verification paths must remain under the exact K-local lab root.");
    }

    private void RequirePolicyAllowed(WorkspacePath path, bool write)
    {
        ImmutableArray<Diagnostic> diagnostics = write
            ? workspacePolicy.Evaluate(labRoot, path)
            : workspacePolicy.EvaluateReadRoot(labRoot, path);
        Diagnostic? refusal = diagnostics.FirstOrDefault(
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (refusal is null)
            return;
        string code = string.Equals(
            refusal.Code,
            "reparse-point-refused",
            StringComparison.Ordinal)
            ? "finish-verification-reparse-refused"
            : "finish-verification-path-refused";
        throw Refused(code, refusal.Message);
    }

    private static SkyrimNpcFinishCoreVerificationArtifactDocument Document(
        SkyrimNpcFinishCoreVerification verification,
        WorkspacePath path,
        byte[] bytes,
        string sha256,
        SkyrimNpcFinishCoreVerifiedManifestDocument verifiedManifest,
        string requestDigest)
    {
        ImmutableArray<string> inputs =
            ImmutableArray.Create(
                    verifiedManifest.Sha256,
                    verifiedManifest.ArchiveSha256)
                .Sort(StringComparer.Ordinal);
        var artifact = new WorkflowArtifactBinding(
            WorkflowArtifactKinds.NpcFinishCoreVerification,
            verification.Schema,
            path,
            bytes.LongLength,
            sha256,
            ProducerCommand,
            requestDigest,
            inputs);
        AgentWorkflowContractValidation.Validate(artifact);
        return new SkyrimNpcFinishCoreVerificationArtifactDocument(
            verification,
            path,
            bytes.LongLength,
            sha256,
            bytes.ToImmutableArray(),
            artifact);
    }

    private static void RequireExactReadback(
        byte[] observed,
        byte[] expected,
        string expectedSha256,
        string code,
        string message)
    {
        if (!observed.AsSpan().SequenceEqual(expected) ||
            !string.Equals(
                Hash(observed),
                expectedSha256,
                StringComparison.Ordinal))
            throw Refused(code, message);
    }

    private static void RequireHash(
        string value,
        string code,
        string message)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'A' && character <= 'F'))))
            throw Refused(code, message);
    }

    private static SkyrimNpcFinishCoreVerificationArtifactException
        NormalizeWriteFailure(Exception exception, WorkspacePath output)
    {
        if (exception is SkyrimNpcFinishCoreVerificationArtifactException
            artifactException)
            return artifactException;
        if (File.Exists(output.Value) || Directory.Exists(output.Value))
            return Refused(
                "finish-verification-output-exists",
                "The verification output became occupied before no-overwrite promotion.",
                exception);
        return Refused(
            "finish-verification-write-failed",
            "The Finish verification fresh-write transaction failed.",
            exception);
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static SkyrimNpcFinishCoreVerificationArtifactException Refused(
        string code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);

    private sealed record VerifiedManifestArchive(
        WorkspacePath Path,
        long Size,
        string Sha256);
}
