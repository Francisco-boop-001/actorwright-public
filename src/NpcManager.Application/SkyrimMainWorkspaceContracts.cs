using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimMainWorkspaceRecordKind
{
    Npc,
    LeveledNpc
}

public enum SkyrimMainWorkspaceGender
{
    Random,
    Male,
    Female
}

public enum SkyrimMainWorkspacePreviewMode
{
    FullCharacter,
    FaceOnly
}

public enum SkyrimMainWorkspaceRoute
{
    EditNpc,
    EditHeadParts,
    EditFace,
    EditBody,
    EditOutfit,
    LoadRaceMenuPreset,
    SaveRaceMenuPreset,
    CopyAppearance,
    PasteAppearance,
    CharGenOptions,
    BuildCharGen,
    SavePackage,
    ExportSceneNif,
    Lighting,
    Animation
}

/// <summary>
/// Stable source identity plus the separately tracked winning provider.
/// Overrides retain their original owner plugin and local FormID.
/// </summary>
public sealed record SkyrimMainWorkspaceIdentity(
    PluginName OwnerPlugin,
    PluginName WinningProvider,
    FormId FormId,
    string Signature);

public sealed record SkyrimMainWorkspaceRecord(
    SkyrimMainWorkspaceIdentity Identity,
    SkyrimMainWorkspaceRecordKind Kind,
    string? EditorId,
    string? Name,
    bool IsSourceDeleted,
    bool IsEmptyLeveledList,
    NpcSex? Sex,
    ImmutableArray<NpcCategory> Categories,
    NpcChangeState ChangeState,
    ImmutableArray<PluginName> ProviderChain,
    ImmutableArray<SkyrimMainWorkspaceIdentity> LeveledNpcEntries,
    string RawRecordSha256);

public sealed record SkyrimMainWorkspaceSnapshot(
    Sha256Hash IntakeFingerprint,
    ImmutableArray<SkyrimMainWorkspaceRecord> Records,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimMainWorkspacePluginRecord(
    PluginName Provider,
    PluginName Owner,
    FormId FormId,
    string Signature,
    string? EditorId,
    string? Name,
    bool IsDeleted,
    NpcRecordMetadata? NpcMetadata,
    ImmutableArray<FormReference> LeveledNpcEntries,
    Sha256Hash RawRecordSha256);

public sealed record SkyrimMainWorkspacePluginReadResult(
    PluginName Plugin,
    ImmutableArray<PluginName> Masters,
    ImmutableArray<SkyrimMainWorkspacePluginRecord> Records);

public interface ISkyrimMainWorkspacePluginReader
{
    SkyrimMainWorkspacePluginReadResult Read(
        WorkspacePath pluginPath);
}

public sealed record SkyrimMainWorkspaceFilter(
    string Search,
    bool ShowNpcs,
    bool ShowLeveledNpcs,
    ImmutableHashSet<NpcCategory> Categories,
    SkyrimMainWorkspaceGender Gender,
    bool ChangedOnly,
    bool IncludeDeleted)
{
    public static SkyrimMainWorkspaceFilter Default { get; } = new(
        string.Empty,
        true,
        true,
        Enum.GetValues<NpcCategory>().ToImmutableHashSet(),
        SkyrimMainWorkspaceGender.Random,
        false,
        true);
}

public sealed record SkyrimMainWorkspaceDraft(
    SkyrimMainWorkspaceIdentity Identity,
    bool IsChanged,
    bool IsDeletePending)
{
    public static SkyrimMainWorkspaceDraft Clean(
        SkyrimMainWorkspaceIdentity identity) => new(identity, false, false);
}

public sealed record SkyrimMainWorkspacePreviewOptions(
    SkyrimMainWorkspacePreviewMode Mode,
    SkyrimMainWorkspaceGender Gender,
    bool RenderBody,
    bool RenderUnderarmor,
    bool RenderArmor,
    bool RenderHeadwear,
    bool RenderGore,
    bool ApplyBoneMorphs,
    bool ApplyVertexMorphs,
    bool ApplyBodyWeight,
    bool ApplySculpt)
{
    public static SkyrimMainWorkspacePreviewOptions Default { get; } = new(
        SkyrimMainWorkspacePreviewMode.FullCharacter,
        SkyrimMainWorkspaceGender.Random,
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true);
}

public sealed record SkyrimMainWorkspacePreviewProjection(
    ImmutableHashSet<PreviewAssetCategory> VisibleAssets,
    ImmutableHashSet<PreviewMorphCategory> Morphs);

public sealed record SkyrimMainWorkspaceArtifactHandoff(
    SkyrimMainWorkspaceIdentity Identity,
    string Kind,
    WorkspacePath Path,
    Sha256Hash Sha256,
    WorkspacePath? ProposalPath,
    Sha256Hash? ProposalSha256,
    bool RuntimeAuthority);

public sealed record SkyrimMainWorkspaceSession(
    string SchemaVersion,
    Sha256Hash IntakeFingerprint,
    ImmutableArray<SkyrimMainWorkspaceIdentity> Selection,
    ImmutableArray<SkyrimMainWorkspaceDraft> Drafts,
    ImmutableArray<SkyrimMainWorkspaceArtifactHandoff> Artifacts,
    bool RuntimeAuthority);

public sealed record SkyrimMainWorkspaceCatalogRequest(
    ReviewedGameIntake Intake);

public sealed record SkyrimMainWorkspaceCatalogResult(
    bool Accepted,
    SkyrimMainWorkspaceSnapshot? Snapshot,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimMainWorkspaceCatalogService
{
    ValueTask<SkyrimMainWorkspaceCatalogResult> LoadAsync(
        SkyrimMainWorkspaceCatalogRequest request,
        CancellationToken cancellationToken);
}

public sealed record SkyrimMainWorkspaceSettings(
    string SchemaVersion,
    SkyrimMainWorkspaceFilter Filter,
    SkyrimMainWorkspacePreviewOptions Preview);

public sealed record SkyrimMainWorkspaceSettingsLoadResult(
    bool LoadedFromDisk,
    SkyrimMainWorkspaceSettings Settings,
    Sha256Hash? Sha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimMainWorkspaceSettingsSaveResult(
    bool Saved,
    SkyrimMainWorkspaceSettings? Settings,
    Sha256Hash? Sha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimMainWorkspaceSettingsService
{
    ValueTask<SkyrimMainWorkspaceSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken);

    ValueTask<SkyrimMainWorkspaceSettingsSaveResult> SaveAsync(
        SkyrimMainWorkspaceSettings settings,
        CancellationToken cancellationToken);
}

public sealed record SkyrimMainWorkspaceSessionSaveRequest(
    SkyrimMainWorkspaceSession Session,
    WorkspacePath Destination);

public sealed record SkyrimMainWorkspaceSessionReadRequest(
    WorkspacePath Path,
    Sha256Hash ExpectedSha256);

public sealed record SkyrimMainWorkspaceSessionResult(
    bool Accepted,
    SkyrimMainWorkspaceSession? Session,
    WorkspacePath? Path,
    Sha256Hash? Sha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimMainWorkspaceSessionService
{
    ValueTask<SkyrimMainWorkspaceSessionResult> SaveAsync(
        SkyrimMainWorkspaceSessionSaveRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimMainWorkspaceSessionResult> ReadAsync(
        SkyrimMainWorkspaceSessionReadRequest request,
        CancellationToken cancellationToken);
}

public sealed record SkyrimMainWorkspacePreviewRequest(
    SkyrimMainWorkspaceRecord SelectedRecord,
    WorkspacePath ManifestPath,
    Sha256Hash ExpectedManifestSha256,
    SkyrimMainWorkspacePreviewOptions Options,
    WorkspacePath AssetRoot,
    WorkspacePath ScenePath,
    WorkspacePath ImagePath,
    string? VariantId = null,
    PreviewAnimationSelection? Animation = null,
    PreviewLightingPreset? Lighting = null);

public sealed record SkyrimMainWorkspacePreviewResult(
    bool Written,
    SkyrimMainWorkspaceRecord? SelectedRecord,
    PreviewSceneArtifact? Artifact,
    WorkspacePath? ScenePath,
    Sha256Hash? SceneSha256,
    WorkspacePath? ImagePath,
    Sha256Hash? ImageSha256,
    bool RuntimeAuthority,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimMainWorkspaceRerollRequest(
    SkyrimMainWorkspacePreviewRequest Preview,
    int Seed);

public sealed record SkyrimMainWorkspaceNifExportRequest(
    SkyrimMainWorkspaceRecord SelectedRecord,
    WorkspacePath ScenePath,
    Sha256Hash ExpectedSceneSha256,
    WorkspacePath AssetRoot,
    WorkspacePath Destination);

public sealed record SkyrimMainWorkspaceNifExportResult(
    bool Written,
    PreviewNifBinaryExportArtifact? Artifact,
    WorkspacePath? Path,
    Sha256Hash? Sha256,
    bool RuntimeAuthority,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimMainWorkspacePreviewService
{
    ValueTask<SkyrimMainWorkspacePreviewResult> RenderAsync(
        SkyrimMainWorkspacePreviewRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimMainWorkspacePreviewResult> RerollAsync(
        SkyrimMainWorkspaceRerollRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimMainWorkspaceNifExportResult> ExportNifAsync(
        SkyrimMainWorkspaceNifExportRequest request,
        CancellationToken cancellationToken);
}
