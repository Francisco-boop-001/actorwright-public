using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record BodyGenMorph(string Name, float Value);

public sealed record BodyGenBuildRequest(
    GameEdition Edition,
    PluginName Plugin,
    FormId NpcFormId,
    string ModName,
    WorkspacePath MorphsPath,
    WorkspacePath OutputRoot);

public sealed record BodyGenTypedBuildRequest(
    GameEdition Edition,
    PluginName Plugin,
    FormId NpcFormId,
    string ModName,
    ImmutableArray<BodyGenMorph> Morphs,
    WorkspacePath OutputRoot)
{
    /// <summary>
    /// Optional authored NPC identity used for Skyrim's deterministic
    /// <c>NPCM_&lt;EditorID&gt;</c> template name. Legacy callers may omit it.
    /// </summary>
    public EditorId? EditorId { get; init; }
}

public sealed record BodyGenFileArtifact(
    AssetPath RelativePath,
    WorkspacePath AbsolutePath,
    int ByteLength,
    Sha256Hash Sha256);

public sealed record BodyGenBuildResult(
    bool Written,
    GameEdition Edition,
    PluginName Plugin,
    FormId NpcFormId,
    string TemplateName,
    Sha256Hash? SourceHash,
    ImmutableArray<BodyGenFileArtifact> Files,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IBodyGenService
{
    ValueTask<BodyGenBuildResult> BuildAsync(BodyGenBuildRequest request, CancellationToken cancellationToken);

    ValueTask<BodyGenBuildResult> BuildTypedAsync(BodyGenTypedBuildRequest request, CancellationToken cancellationToken);
}
