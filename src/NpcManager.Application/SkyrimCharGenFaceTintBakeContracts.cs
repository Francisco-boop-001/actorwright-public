using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimCharGenFaceTintBakeRequest(
    WorkspacePath OptionsArtifact,
    Sha256Hash ExpectedOptionsSha256,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    FormReference Npc,
    NpcSex ExpectedSex,
    FormReference ExpectedRace,
    WorkspacePath FaceTintOutput,
    WorkspacePath ReceiptOutput);

/// <summary>
/// Durable proof that one exact canonical options artifact admitted and bound
/// one independently reopened native Skyrim FaceTint DDS.
/// </summary>
public sealed record SkyrimCharGenFaceTintBakeReceipt(
    string SchemaVersion,
    string ArtifactKind,
    WorkspacePath OptionsArtifact,
    Sha256Hash OptionsSha256,
    CharGenOptions AcceptedOptions,
    FaceGenChannelResolution DiffuseResolution,
    FaceGenDiffuseCompression DiffuseCompression,
    string ConsumptionContract,
    SkyrimNativeFaceTintBuildArtifact NativeFaceTint,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginAuthorities,
    ImmutableArray<SkyrimNativeFaceTintMaskAuthority> MaskAuthorities,
    bool RuntimeAuthority);

public sealed record SkyrimCharGenFaceTintBakeResult(
    bool Written,
    SkyrimCharGenFaceTintBakeReceipt? Receipt,
    Sha256Hash? ReceiptSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimCharGenFaceTintBakeService
{
    ValueTask<SkyrimCharGenFaceTintBakeResult> BuildAsync(
        SkyrimCharGenFaceTintBakeRequest request,
        CancellationToken cancellationToken);
}
