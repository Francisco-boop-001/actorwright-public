using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Derives a fresh authority without rewriting the bound execution request.</summary>
public sealed record SkyrimFaceBakeAuthorityDerivationRequest(
    WorkspacePath AllowedRoot,
    WorkspacePath Preset,
    Sha256Hash ExpectedPresetSha256,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    FormReference Race,
    NpcSex Sex,
    ProviderResourceAuthority Carrier,
    WorkspacePath Output);

public sealed record SkyrimFaceBakeAuthorityDerivationArtifact(
    WorkspacePath Path,
    Sha256Hash Sha256);

public sealed record SkyrimFaceBakeAuthorityDerivationResult(
    SkyrimFaceBakeAuthorityDerivationArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => Artifact is not null &&
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface ISkyrimFaceBakeAuthorityDerivationService
{
    ValueTask<SkyrimFaceBakeAuthorityDerivationResult> DeriveAsync(
        SkyrimFaceBakeAuthorityDerivationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Observed packed carrier lanes; no selected-model rest-placement inference.</summary>
public sealed record SkyrimFaceBakeCarrierShape(
    string Name, int VertexCount, Sha256Hash PackedPositionSha256, Sha256Hash TopologySha256);

public sealed record SkyrimFaceBakeCarrierGeometryResult(
    ImmutableArray<SkyrimFaceBakeCarrierShape> Shapes, ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => !Shapes.IsDefaultOrEmpty &&
        !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface ISkyrimFaceBakeCarrierGeometryReader
{
    SkyrimFaceBakeCarrierGeometryResult Read(ImmutableArray<byte> bytes, Sha256Hash expectedSha256);
}
