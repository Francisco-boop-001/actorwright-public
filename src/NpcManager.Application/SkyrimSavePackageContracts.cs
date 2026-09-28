using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimSaveScope
{
    SelectedOnly,
    AllChanged
}

public enum SkyrimSaveTargetMode
{
    FreshPackage,
    UpdateExisting
}

public enum SkyrimSaveEncodingMode
{
    PreservePackageBytes,
    Utf8,
    Windows1252
}

public enum SkyrimSaveArchiveMode
{
    PreservePackageLayout,
    Loose,
    Bsa
}

public enum SkyrimSaveLeveledListMode
{
    PreservePackageRecords,
    New,
    Existing
}

public sealed record SkyrimSavePackageNpc(
    FormId FormId,
    EditorId? EditorId,
    bool SelfOwned,
    PluginName? OwnerPlugin = null);

public sealed record SkyrimSavePackageSnapshot(
    PluginName OutputPlugin,
    FormId TargetFormId,
    int NpcRecordCount,
    bool MarkAsMaster,
    bool LightMaster,
    bool HasFaceGeom,
    bool HasFaceTint,
    bool HasBodySlideSidecar,
    bool HasBodyGen,
    bool HasApplyScript,
    bool HasArchive,
    int AuthoredRecordCount,
    int LeveledRecordCount,
    ImmutableArray<PackageManifestFile> Files,
    ImmutableArray<SkyrimSavePackageNpc> Npcs = default,
    SkyrimSaveEncodingMode DetectedEncoding =
        SkyrimSaveEncodingMode.PreservePackageBytes,
    ImmutableArray<string> LeveledNpcEditorIds = default,
    uint HighestSelfOwnedLocalFormId = 0,
    int LooseAssetCount = 0);

public sealed record SkyrimSavePackageOptions(
    SkyrimSaveScope Scope,
    SkyrimSaveTargetMode TargetMode,
    bool MarkAsMaster,
    bool LightMaster,
    bool HasFaceGeom,
    bool HasFaceTint,
    bool HasBodySlideSidecar,
    bool HasBodyGen,
    bool HasApplyScript,
    bool HasAuthoredRecords,
    SkyrimSaveEncodingMode EncodingMode,
    SkyrimSaveArchiveMode ArchiveMode,
    SkyrimSaveLeveledListMode LeveledListMode,
    string? LeveledListEditorId = null,
    bool NoDuplicateLeveledEntries = false);

public sealed record SkyrimSavePackageDecision(
    bool Accepted,
    SkyrimSavePackageOptions? Options,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimSavePackageChoiceRequest(
    SkyrimSaveScope Scope,
    SkyrimSaveTargetMode TargetMode,
    bool MarkAsMaster,
    bool LightMaster,
    SkyrimSaveEncodingMode EncodingMode,
    SkyrimSaveArchiveMode ArchiveMode,
    SkyrimSaveLeveledListMode LeveledListMode,
    string? LeveledListEditorId = null,
    bool NoDuplicateLeveledEntries = false);

public static class SkyrimSavePackageRules
{
    public static SkyrimSavePackageOptions CreateExactOptions(
        SkyrimSavePackageSnapshot snapshot) =>
        new(
            SkyrimSaveScope.SelectedOnly,
            SkyrimSaveTargetMode.FreshPackage,
            snapshot.MarkAsMaster,
            snapshot.LightMaster,
            snapshot.HasFaceGeom,
            snapshot.HasFaceTint,
            snapshot.HasBodySlideSidecar,
            snapshot.HasBodyGen,
            snapshot.HasApplyScript,
            snapshot.AuthoredRecordCount > 0,
            SkyrimSaveEncodingMode.PreservePackageBytes,
            SkyrimSaveArchiveMode.PreservePackageLayout,
            SkyrimSaveLeveledListMode.PreservePackageRecords);

    public static SkyrimSavePackageOptions CreateOptions(
        SkyrimSavePackageSnapshot snapshot,
        SkyrimSavePackageChoiceRequest? choices) =>
        choices is null
            ? CreateExactOptions(snapshot)
            : new SkyrimSavePackageOptions(
                choices.Scope,
                choices.TargetMode,
                choices.MarkAsMaster,
                choices.LightMaster,
                snapshot.HasFaceGeom,
                snapshot.HasFaceTint,
                snapshot.HasBodySlideSidecar,
                snapshot.HasBodyGen,
                snapshot.HasApplyScript,
                snapshot.AuthoredRecordCount > 0,
                choices.EncodingMode,
                choices.ArchiveMode,
                choices.LeveledListMode,
                choices.LeveledListEditorId,
                choices.NoDuplicateLeveledEntries);

    public static SkyrimSavePackageDecision Save(
        SkyrimSavePackageSnapshot snapshot,
        SkyrimSavePackageOptions candidate)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (snapshot.NpcRecordCount <= 0)
            Error(diagnostics, "save-package-npc-count",
                "A Skyrim save package must contain at least one NPC.");
        if (!snapshot.Npcs.IsDefault &&
            snapshot.Npcs.Length != snapshot.NpcRecordCount)
            Error(diagnostics, "save-package-npc-inventory",
                "The independently derived NPC inventory does not match its record count.");
        if (candidate.Scope == SkyrimSaveScope.SelectedOnly &&
            !snapshot.Npcs.IsDefaultOrEmpty &&
            !snapshot.Npcs.Any(item => item.FormId == snapshot.TargetFormId))
            Error(diagnostics, "save-package-selected-target-missing",
                "The selected target NPC is absent from the reviewed plugin inventory.");
        if (candidate.LightMaster &&
            snapshot.HighestSelfOwnedLocalFormId > 0x0000_0FFF)
            Error(diagnostics, "save-package-light-form-id-range",
                "The plugin contains a self-owned FormID outside the ESL compact range.");
        if (candidate.HasFaceGeom != snapshot.HasFaceGeom ||
            candidate.HasFaceTint != snapshot.HasFaceTint ||
            candidate.HasBodySlideSidecar != snapshot.HasBodySlideSidecar ||
            candidate.HasBodyGen != snapshot.HasBodyGen ||
            candidate.HasApplyScript != snapshot.HasApplyScript ||
            candidate.HasAuthoredRecords != (snapshot.AuthoredRecordCount > 0))
            Error(diagnostics, "save-package-output-mismatch",
                "Displayed outputs must match the independently verified source inventory.");
        if (!Enum.IsDefined(candidate.Scope) ||
            !Enum.IsDefined(candidate.TargetMode) ||
            !Enum.IsDefined(candidate.EncodingMode) ||
            !Enum.IsDefined(candidate.ArchiveMode) ||
            !Enum.IsDefined(candidate.LeveledListMode))
            Error(diagnostics, "save-package-option-invalid",
                "The save review contains an unknown typed choice.");

        ValidateLeveledList(snapshot, candidate, diagnostics);

        return diagnostics.Count == 0
            ? new SkyrimSavePackageDecision(true, candidate, [])
            : new SkyrimSavePackageDecision(false, null, diagnostics.ToImmutable());
    }

    public static SkyrimSavePackageDecision Cancel(
        SkyrimSavePackageSnapshot snapshot)
    {
        _ = snapshot;
        return new SkyrimSavePackageDecision(false, null, []);
    }

    private static void Error(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string code,
        string message) =>
        diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message));

    private static void ValidateLeveledList(
        SkyrimSavePackageSnapshot snapshot,
        SkyrimSavePackageOptions candidate,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (candidate.LeveledListMode ==
            SkyrimSaveLeveledListMode.PreservePackageRecords)
        {
            if (!string.IsNullOrEmpty(candidate.LeveledListEditorId) ||
                candidate.NoDuplicateLeveledEntries)
                Error(diagnostics, "save-package-leveled-preserve-options",
                    "Preserve mode cannot carry an LVLN target or duplicate policy.");
            return;
        }

        if (!TryEditorId(candidate.LeveledListEditorId, out string editorId))
        {
            Error(diagnostics, "save-package-leveled-editor-id",
                "New and existing LVLN modes require a valid ASCII EditorID.");
            return;
        }

        if (candidate.LeveledListMode == SkyrimSaveLeveledListMode.Existing &&
            (snapshot.LeveledNpcEditorIds.IsDefaultOrEmpty ||
             !snapshot.LeveledNpcEditorIds.Contains(
                 editorId,
                 StringComparer.OrdinalIgnoreCase)))
            Error(diagnostics, "save-package-leveled-existing-missing",
                "The requested existing LVLN is absent from the reviewed plugin.");
    }

    private static bool TryEditorId(string? value, out string editorId)
    {
        editorId = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            editorId = new EditorId(value).Value;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

public sealed record SkyrimSavePackageReviewRequest(
    WorkspacePath SourceRoot,
    WorkspacePath OutputRoot,
    SkyrimSavePackageChoiceRequest? Choices = null);

public sealed record SkyrimSavePackageReviewArtifact(
    string SchemaVersion,
    string ArtifactKind,
    WorkspacePath SourceRoot,
    WorkspacePath OutputRoot,
    WorkspacePath ManifestPath,
    Sha256Hash ManifestSha256,
    SkyrimSavePackageSnapshot Snapshot,
    SkyrimSavePackageOptions Options,
    bool NoWrite,
    bool RuntimeAuthority);

public sealed record SkyrimSavePackageReviewResult(
    bool Accepted,
    SkyrimSavePackageReviewArtifact? Artifact,
    PackageVerifyResult? PackageVerification,
    ImmutableArray<Diagnostic> Diagnostics);

public enum SkyrimSavePackageStage
{
    Revalidate,
    PersistProposal,
    TransformPlugin,
    TransformAssets,
    WriteManifest,
    VerifyDestination,
    Complete
}

public sealed record SkyrimSavePackageProgress(
    SkyrimSavePackageStage Stage,
    int Percent,
    string Message);

public sealed record SkyrimSavePackageExecutionRequest(
    SkyrimSavePackageReviewArtifact Review);

public sealed record SkyrimSavePackageFileComparison(
    AssetPath RelativePath,
    long SourceByteLength,
    long OutputByteLength,
    Sha256Hash SourceSha256,
    Sha256Hash OutputSha256,
    bool Matches);

public sealed record SkyrimSavePackageExecutionArtifact(
    string SchemaVersion,
    string ArtifactKind,
    WorkspacePath SourceRoot,
    WorkspacePath OutputRoot,
    Sha256Hash SourceManifestSha256,
    Sha256Hash OutputManifestSha256,
    PluginName OutputPlugin,
    FormId TargetFormId,
    ImmutableArray<SkyrimSavePackageFileComparison> Files,
    bool NoWriteToSource,
    bool RuntimeAuthority,
    Sha256Hash? ProposalSha256 = null,
    SkyrimSavePluginTransformArtifact? PluginTransform = null,
    SkyrimSavePluginVerificationArtifact? PluginVerification = null,
    SkyrimBsaBuildArtifact? ArchiveBuild = null,
    ImmutableArray<SkyrimBsaMemberArtifact> ArchiveMembers = default,
    bool RollbackClean = true);

public sealed record SkyrimSavePackageExecutionResult(
    bool Completed,
    SkyrimSavePackageExecutionArtifact? Artifact,
    PackageVerifyResult? PackageVerification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimSavePackageService
{
    ValueTask<SkyrimSavePackageReviewResult> ReviewAsync(
        SkyrimSavePackageReviewRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimSavePackageExecutionResult> ExecuteAsync(
        SkyrimSavePackageExecutionRequest request,
        IProgress<SkyrimSavePackageProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record SkyrimSavePluginTransformRequest(
    WorkspacePath SourcePlugin,
    WorkspacePath OutputPlugin,
    SkyrimSavePackageSnapshot Snapshot,
    SkyrimSavePackageOptions Options);

public sealed record SkyrimSavePluginTransformArtifact(
    WorkspacePath SourcePlugin,
    WorkspacePath OutputPlugin,
    Sha256Hash SourceSha256,
    Sha256Hash OutputSha256,
    bool MarkAsMaster,
    bool LightMaster,
    SkyrimSaveEncodingMode EncodingMode,
    ImmutableArray<FormId> NpcFormIds,
    string? LeveledNpcEditorId,
    ImmutableArray<FormId> LeveledNpcEntries,
    int OtherRecordCount,
    bool NoWriteToSource,
    bool RuntimeAuthority);

public sealed record SkyrimSavePluginTransformResult(
    bool Completed,
    SkyrimSavePluginTransformArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimSavePluginTransformer
{
    ValueTask<SkyrimSavePluginTransformResult> TransformAsync(
        SkyrimSavePluginTransformRequest request,
        CancellationToken cancellationToken);
}

public sealed record SkyrimSavePluginEncodingProbe(string Value);

public sealed record SkyrimSavePluginVerifyRequest(
    WorkspacePath Plugin,
    SkyrimSavePluginTransformArtifact Expected,
    ImmutableArray<SkyrimSavePluginEncodingProbe> EncodingProbes = default);

public sealed record SkyrimSavePluginVerificationArtifact(
    WorkspacePath Plugin,
    Sha256Hash Sha256,
    bool MarkAsMaster,
    bool LightMaster,
    SkyrimSaveEncodingMode EncodingMode,
    ImmutableArray<FormId> NpcFormIds,
    string? LeveledNpcEditorId,
    ImmutableArray<FormId> LeveledNpcEntries,
    bool RuntimeAuthority);

public sealed record SkyrimSavePluginVerifyResult(
    bool Verified,
    SkyrimSavePluginVerificationArtifact? Artifact,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimSavePluginVerifier
{
    ValueTask<SkyrimSavePluginVerifyResult> VerifyAsync(
        SkyrimSavePluginVerifyRequest request,
        CancellationToken cancellationToken);
}
