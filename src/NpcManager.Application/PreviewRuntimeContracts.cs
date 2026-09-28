using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record ApplicationResourceAuthority(
    string Role,
    ApplicationResourcePath Path,
    long ByteLength,
    Sha256Hash Sha256,
    Sha256Hash ManifestSha256);

public sealed record ApplicationResourceRuntimeAuthority(
    ApplicationResourcePath ResourceBase,
    ApplicationResourcePath RuntimeRoot,
    ApplicationResourceAuthority Manifest,
    ImmutableArray<ApplicationResourceAuthority> Assets)
{
    public Sha256Hash ManifestSha256 => Manifest.Sha256;

    public ApplicationResourceAuthority Require(string role) =>
        Assets.Single(item => string.Equals(
            item.Role,
            role,
            StringComparison.Ordinal));
}

public sealed record ApplicationResourceRuntimeAdmissionResult(
    ApplicationResourceRuntimeAuthority? Authority,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted =>
        Authority is not null &&
        !Diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);
}

public sealed record PreviewDependencyAuthority(
    string Role,
    string ExpectedPath,
    long? ByteLength,
    Sha256Hash? Sha256,
    bool ApplicationOwned);

public sealed record PreviewDependencyPreflightResult(
    ImmutableArray<PreviewDependencyAuthority> Authorities,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted =>
        !Diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);
}

public sealed record FaceGeomHairRegionsPreviewServices(
    IFaceGeomHairRegionsPreviewService Service,
    INpcVisualPreviewVisualValidator Validator);
