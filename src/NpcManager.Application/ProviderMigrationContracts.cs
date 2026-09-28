using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static class ProviderMigrationSchemas
{
    public const string Review = "actorwright-provider-migration-review/1";
    public const string Receipt = "actorwright-provider-migration-receipt/1";
}

public sealed record ProviderMigrationDeclaredField(
    string Field,
    string DeclaredValue);

public sealed record ProviderMigrationChange(
    string Field,
    string Before,
    string After);

public sealed record ProviderMigrationPlannedFile(
    string RelativePath,
    Sha256Hash Sha256,
    long ByteLength);

public sealed record ProviderMigrationReviewArtifact(
    string Schema,
    WorkspacePath SourceRequest,
    Sha256Hash SourceRequestSha256,
    WorkspacePath SourcePresetBundle,
    Sha256Hash SourcePresetBundleSha256,
    ImmutableArray<ProviderMigrationDeclaredField> DeclaredLegacyProvider,
    string LegacyFingerprint,
    ProductFixtureBundleReference ProductFixtureBundle,
    ImmutableArray<ProviderMigrationChange> Changes,
    WorkspacePath PlannedMigratedRequestRoot,
    ImmutableArray<ProviderMigrationPlannedFile> PlannedFiles,
    string ActorwrightProductIdentity,
    bool BuildAuthority,
    bool RuntimeAuthority);

public sealed record ProviderMigrationReviewDocument(
    ProviderMigrationReviewArtifact Value,
    ImmutableArray<byte> Utf8Json,
    Sha256Hash Sha256);

public sealed record ProviderMigrationReviewWriteRequest(
    ProviderMigrationReviewArtifact Review,
    WorkspacePath Output);

public sealed record ProviderMigrationAcceptanceRequest(
    WorkspacePath SourceRequest,
    Sha256Hash SourceRequestSha256,
    WorkspacePath ReviewedMigration,
    Sha256Hash ReviewedMigrationSha256,
    WorkspacePath MigratedRequestRoot);

public sealed record ProviderMigrationResult(
    bool Completed,
    ProviderMigrationReviewDocument? Review,
    ImmutableArray<WorkspacePath> Outputs,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IProviderMigrationService
{
    ValueTask<ProviderMigrationResult> WriteReviewAsync(
        ProviderMigrationReviewWriteRequest request,
        CancellationToken cancellationToken);

    ValueTask<ProviderMigrationResult> AcceptAsync(
        ProviderMigrationAcceptanceRequest request,
        CancellationToken cancellationToken);
}
