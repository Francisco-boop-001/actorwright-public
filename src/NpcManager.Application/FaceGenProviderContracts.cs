using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum FaceGenProviderArtifactKind
{
    FaceGeom,
    FaceTint,
    FaceCustomizationDiffuse,
    FaceCustomizationNormal,
    FaceCustomizationSpecular,
    FaceDetailNeutral,
    FaceDiffuse,
    FaceNormal
}

public enum FaceGenProviderRequiredness
{
    Required,
    Optional
}

public sealed record FaceGenProviderEvidence(
    AssetProviderKind Kind,
    string Source,
    long Size,
    Sha256Hash Sha256);

public sealed record FaceGenProviderArtifact(
    FaceGenProviderArtifactKind Kind,
    AssetPath CanonicalPath,
    FaceGenProviderRequiredness Requiredness,
    ImmutableArray<FaceGenProviderEvidence> Providers);

/// <summary>
/// Typed identity context read from the winning copied-Data NPC record. The
/// context explains which race and headpart references were used alongside the
/// canonical FaceGen paths; it does not claim that those references are live,
/// deployable, or renderable in a game process.
/// </summary>
public sealed record FaceGenProviderNpcContext(
    NpcSex? Sex,
    FormId? RaceFormId,
    ImmutableArray<FormId> HeadPartFormIds);

/// <summary>
/// Copied-plugin FO4 FaceTint binding. This records the winning NPC race,
/// gender-specific RACE tint groups, and every referenced CLFM provider without
/// claiming that the live game will load or render those records.
/// </summary>
public enum FaceTintColorDataKind
{
    Unknown,
    Rgb,
    RemappingIndex
}

public sealed record FaceTintColorProviderBinding(
    string FormId,
    string Plugin,
    string? EditorId,
    FaceTintColorDataKind DataKind,
    byte? Red,
    byte? Green,
    byte? Blue,
    float? RemappingIndex);

public sealed record FaceTintTemplateColorBinding(
    short TemplateIndex,
    float Alpha,
    string BlendOperation,
    FaceTintColorProviderBinding Color);

public sealed record FaceTintOptionBinding(
    ushort Index,
    string Slot,
    string? Name,
    float? Default,
    ImmutableArray<string> Textures,
    ImmutableArray<FaceTintTemplateColorBinding> TemplateColors);

public sealed record FaceTintGroupBinding(
    uint? CategoryIndex,
    string? Name,
    ImmutableArray<FaceTintOptionBinding> Options);

public sealed record FaceTintRaceBinding(
    string RaceFormId,
    string RacePlugin,
    string? RaceEditorId,
    NpcSex Sex,
    ImmutableArray<FaceTintGroupBinding> Groups);

public sealed record FaceTintProviderBindingResult(
    bool Resolved,
    FaceTintRaceBinding? Binding,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record FaceTintProviderBindingRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    FormId NpcFormId,
    PluginName WinningPlugin,
    NpcSex Sex,
    ImmutableArray<PluginName> PluginOrder);

public interface IFaceTintProviderBindingReader
{
    ValueTask<FaceTintProviderBindingResult> ReadAsync(
        FaceTintProviderBindingRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Read-only, copied-Data evidence for the upstream canonical FaceGen file naming
/// contract. It intentionally reports provider presence rather than claiming game
/// loadability or runtime appearance.
/// </summary>
public sealed record FaceGenProviderResolutionArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string NpcFormId,
    string LocalFormId,
    string OriginatingPlugin,
    string WinningPlugin,
    ImmutableArray<PluginName> OverrideChain,
    ImmutableArray<FaceGenProviderArtifact> Artifacts,
    FaceGenProviderNpcContext? NpcContext = null,
    FaceTintRaceBinding? FaceTintBinding = null);

public sealed record FaceGenProviderResolutionRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    FormId NpcFormId,
    ImmutableArray<PluginName> PluginOrder,
    bool IncludeSharedNeutralDetail = false);

public sealed record FaceGenProviderResolutionResult(
    bool Resolved,
    FaceGenProviderResolutionArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IFaceGenProviderResolutionService
{
    ValueTask<FaceGenProviderResolutionResult> ResolveAsync(
        FaceGenProviderResolutionRequest request, CancellationToken cancellationToken);
}
