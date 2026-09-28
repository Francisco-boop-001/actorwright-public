using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public abstract record ProviderResourceAuthority(
    string Role,
    Sha256Hash ExpectedSha256,
    bool IsDirectory)
{
    public abstract string PhysicalPath { get; }
}

public sealed record WorkspaceProviderResourceAuthority(
    string LogicalRole,
    WorkspacePath Path,
    Sha256Hash Sha256,
    bool Directory = false) :
    ProviderResourceAuthority(LogicalRole, Sha256, Directory)
{
    public override string PhysicalPath => Path.Value;
}

public sealed record ApplicationProviderResourceAuthority(
    string BundleId,
    string LogicalRole,
    ApplicationResourcePath Path,
    Sha256Hash Sha256,
    Sha256Hash RegistryManifestSha256,
    bool Directory = false) :
    ProviderResourceAuthority(LogicalRole, Sha256, Directory)
{
    public override string PhysicalPath => Path.Value;
}

public sealed record ProductFixtureBundleReference(
    string BundleId,
    Sha256Hash RegistryManifestSha256);

public sealed record ProviderResourceAuthoritySet(
    string BundleId,
    Sha256Hash RegistryManifestSha256,
    ProviderResourceAuthority Manifest,
    ProviderResourceAuthority TemplatePlugin,
    ProviderResourceAuthority FaceGeomCarrier,
    ProviderResourceAuthority FaceTintManifest,
    ProviderResourceAuthority FaceTintProviderRoot,
    ProviderResourceAuthority FaceTintSource,
    ProviderResourceAuthority DependencyManifest,
    FormId TemplateNpcFormId,
    GameEdition Edition,
    NpcSex Sex)
{
    public ImmutableArray<ProviderResourceAuthority> Files =>
    [
        Manifest,
        TemplatePlugin,
        FaceGeomCarrier,
        FaceTintManifest,
        FaceTintSource,
        DependencyManifest
    ];
}

public sealed record ApplicationProviderResourceAdmissionResult(
    ProviderResourceAuthoritySet? Authority,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted =>
        Authority is not null &&
        !Diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);
}

public interface IApplicationProviderResourceRegistry
{
    bool TryGetDefaultBlankNpcFixture(
        out ProductFixtureBundleReference? reference);

    ApplicationProviderResourceAdmissionResult Admit(
        ProductFixtureBundleReference reference,
        FormId templateNpcFormId,
        GameEdition edition,
        NpcSex sex);
}
