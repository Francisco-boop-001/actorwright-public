using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Document schema identifiers for the dialogue authoring transaction.</summary>
public static class SkyrimNpcDialogueSchemas
{
    public const string Manifest = "npc.dialogue.manifest.v1";
    public const string Proposal = "npc.dialogue.proposal.v1";
    public const string OutputManifest = "npc.dialogue.output-manifest.v1";
    public const string Verification = "npc.dialogue.verification.v1";
}

public static class SkyrimNpcDialogueDiagnosticCodes
{
    public const string ManifestInvalid = "dialogue-manifest-invalid";
    public const string ManifestHashMismatch = "dialogue-manifest-hash-mismatch";
    public const string LineDuplicate = "dialogue-line-duplicate";
    public const string LineTextEmpty = "dialogue-line-text-empty";
    public const string ConditionUnknown = "dialogue-condition-unknown";
    public const string ConditionUnresolved = "dialogue-condition-unresolved";
    public const string PluginTypeRefused = "dialogue-plugin-type-refused";
    public const string BudgetExceeded = "dialogue-light-budget-exceeded";
    public const string CooldownAdvisory = "dialogue-cooldown-advisory";
    public const string SourceHashMismatch = "dialogue-source-hash-mismatch";
    public const string SourceDialogueUnsupported = "dialogue-source-dialogue-unsupported";
    public const string SynthesisIncomplete = "dialogue-synthesis-incomplete";
    public const string OutputExists = "dialogue-output-exists";
    public const string LipToolMissing = "dialogue-lip-tool-missing";
    public const string LipToolFailed = "dialogue-lip-tool-failed";
    public const string VerifyRecordMissing = "dialogue-verify-record-missing";
    public const string VerifyAssetMissing = "dialogue-verify-asset-missing";
    public const string VerifyLinkUnresolved = "dialogue-verify-link-unresolved";
    public const string VerifyForeignOverride = "dialogue-verify-foreign-override";
    public const string VerifyLightRange = "dialogue-verify-light-range";
    public const string VerifySeqMissing = "dialogue-verify-seq-missing";
    public const string VerifyScriptMismatch = "dialogue-verify-script-mismatch";
    public const string VerifyDocumentBinding = "dialogue-verify-document-binding";
    public const string TemplateUnknown = "dialogue-template-unknown";
    public const string ProfileInvalid = "dialogue-profile-invalid";
}

/// <summary>Maps to INFO flags: Once = SayOnce, Random = Random, Always = no flag.</summary>
public enum SkyrimDialogueRepeatPolicy
{
    Once,
    Random,
    Always
}

/// <summary>
/// Player-facing follower actions bound to the shared precompiled
/// <c>ActorwrightFollowerDialogue</c> TopicInfo fragments, which delegate to the
/// vanilla <c>DialogueFollower</c> quest.
/// </summary>
public enum SkyrimDialogueAction
{
    None,
    Recruit,
    Dismiss,
    Wait,
    Follow,
    Trade
}

/// <summary>
/// One typed condition row. <see cref="Function"/> is the Skyrim condition
/// function name (for example <c>GetIsID</c>, <c>IsInInterior</c>,
/// <c>GetInCurrentLoc</c>, <c>GetIsCurrentWeather</c>, <c>GetCurrentTime</c>,
/// <c>GetStage</c>, <c>GetGlobalValue</c>, <c>IsSneaking</c>, <c>GetInFaction</c>,
/// <c>GetFactionRank</c>, <c>GetRandomPercent</c>, <c>GetRelationshipRank</c>,
/// <c>GetActorValue</c>, <c>IsWeaponOut</c>, <c>GetIsRace</c>, <c>HasKeyword</c>,
/// <c>LocationHasKeyword</c>, <c>IsInCombat</c>, <c>GetLocationCleared</c>).
/// <see cref="Parameter"/> is an EditorID (resolved against the copied masters),
/// <c>self</c> for the owning NPC, <c>player</c>, or a numeric literal, as the
/// function requires. <see cref="Operator"/> is one of <c>==</c>, <c>!=</c>,
/// <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c>. <see cref="RunOn"/> is
/// <c>subject</c>, <c>target</c>, or <c>player</c>.
/// </summary>
public sealed record SkyrimDialogueCondition(
    string Function,
    string? Parameter,
    string? SecondParameter,
    string Operator,
    float Value,
    bool Or,
    string RunOn);

/// <summary>
/// One authored line. <see cref="Subtype"/> is the DIAL subtype name used by
/// Mutagen (<c>Custom</c>, <c>Hello</c>, <c>Goodbye</c>, <c>Idle</c>, <c>Attack</c>,
/// <c>Hit</c>, <c>Flee</c>, <c>Taunt</c>, <c>Bleedout</c>, <c>AllyKilled</c>,
/// <c>NormalToCombat</c>, <c>CombatToNormal</c>, <c>LostToNormal</c>, <c>Steal</c>,
/// <c>Assault</c>, <c>Murder</c>, <c>Trespass</c>, <c>PickpocketNC</c>,
/// <c>ObserveCombat</c>, <c>NoticeCorpse</c>, ...). Lines sharing a
/// <see cref="Topic"/> EditorID become INFO children of one DIAL.
/// <see cref="Prompt"/> is the player-side prompt for <c>Custom</c> topics.
/// <see cref="CooldownHours"/> is advisory and is translated into
/// <see cref="RandomPercent"/> thinning because runtime polling is forbidden.
/// </summary>
public sealed record SkyrimDialogueLine(
    string Id,
    string Category,
    string Topic,
    string Subtype,
    string Text,
    string Emotion,
    uint EmotionValue,
    ImmutableArray<SkyrimDialogueCondition> Conditions,
    float Priority,
    SkyrimDialogueRepeatPolicy Repeat,
    int RandomPercent,
    int CooldownHours,
    SkyrimDialogueAction Action,
    string? Prompt,
    string Status,
    string? PositiveTest,
    string? NegativeTest);

public sealed record SkyrimDialogueNpcIdentity(
    PluginName Plugin,
    FormId FormId,
    EditorId? EditorId,
    string VoicePrefix,
    bool Female);

/// <summary>
/// Operator-supplied profile facts used only to fill template lines; Actorwright
/// never invents biography. <see cref="Exclusions"/> lists template categories
/// the operator removed, each with a reason, so coverage gaps stay justified.
/// </summary>
public sealed record SkyrimDialogueProfile(
    string Name,
    ImmutableArray<string> Tone,
    ImmutableArray<string> Exclusions,
    string? Home,
    string? Role)
{
    /// <summary>One operator-supplied justification per excluded category.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<string, string>? ExclusionReasons { get; init; }
}

public sealed record SkyrimDialogueManifest(
    string Schema,
    SkyrimDialogueNpcIdentity Npc,
    string Language,
    SkyrimDialogueProfile Profile,
    string QuestEditorId,
    byte QuestPriority,
    string? Template,
    ImmutableArray<SkyrimDialogueLine> Lines);

/// <summary>A built-in coverage template that turns a profile into a draft manifest.</summary>
public interface ISkyrimDialogueCoverageTemplate
{
    string Name { get; }

    /// <summary>Category names the template covers, in template order, for coverage reporting.</summary>
    ImmutableArray<string> Categories { get; }

    SkyrimDialogueManifest Create(
        SkyrimDialogueNpcIdentity npc,
        SkyrimDialogueProfile profile,
        string language);
}

public sealed record SkyrimDialogueRecordAllocation(
    string Signature,
    uint LocalFormId,
    string EditorId,
    string? LineId);

public sealed record SkyrimDialogueBudget(
    int OwnedRecords,
    int PlannedRecords,
    int Headroom,
    int Limit,
    bool Fits);

/// <summary>
/// Final voice asset identity for one line: file stem
/// <c>Quest_Topic_&lt;8-hex local INFO id&gt;_&lt;responseIndex&gt;</c>; quest/topic are truncated only above 25 combined characters.
/// under <c>sound/voice/&lt;plugin&gt;/&lt;voiceType&gt;/</c>.
/// </summary>
public sealed record SkyrimDialogueAssetPlan(
    string LineId,
    uint InfoLocalFormId,
    string VoiceTypeEditorId,
    string RelativeDirectory,
    string FileStem)
{
    public static string LookupStem(string quest, string topic, uint infoId, int responseNumber)
    {
        if (quest.Length + topic.Length > 25)
        {
            quest = quest[..Math.Min(10, quest.Length)];
            topic = topic[..Math.Min(25 - quest.Length, topic.Length)];
        }
        return $"{quest}_{topic}_{infoId & 0xFFFFFF:X8}_{responseNumber}";
    }
}

public enum SkyrimDialogueProposalStatus
{
    ReadyForReviewedWrite,
    Refused
}

public sealed record SkyrimDialogueProposal(
    string Schema,
    string ManifestPath,
    Sha256Hash ManifestSha256,
    SkyrimDialogueManifest Manifest,
    string SourcePluginPath,
    PluginName SourcePlugin,
    Sha256Hash SourcePluginSha256,
    string DataRoot,
    ImmutableArray<PluginName> LoadOrder,
    string SampleAuthorityPath,
    Sha256Hash SampleAuthoritySha256,
    bool LightPlugin,
    ImmutableArray<string> MasterOrder,
    uint NextFormId,
    ImmutableArray<SkyrimDialogueRecordAllocation> Records,
    ImmutableArray<SkyrimDialogueAssetPlan> Assets,
    SkyrimDialogueBudget Budget,
    SkyrimDialogueProposalStatus Status,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public ImmutableDictionary<string, Sha256Hash> CopiedMasterSha256 { get; init; } = ImmutableDictionary<string, Sha256Hash>.Empty;
    /// <summary>The selected source SEQ path. A null hash binds its absence at analyze time.</summary>
    public string? SourceSeqPath { get; init; }
    public Sha256Hash? SourceSeqSha256 { get; init; }
}

public sealed record SkyrimDialogueOutputAsset(
    string LineId,
    uint InfoLocalFormId,
    string Wav,
    Sha256Hash WavSha256,
    string? Lip,
    Sha256Hash? LipSha256,
    string? Fuz,
    Sha256Hash? FuzSha256);

public sealed record SkyrimDialogueOutputManifest(
    string Schema,
    string ProposalPath,
    Sha256Hash ProposalSha256,
    string SynthesisPath,
    Sha256Hash SynthesisSha256,
    string PackageRoot,
    PluginName Plugin,
    Sha256Hash PluginSha256,
    bool LightPlugin,
    string VoiceTypeEditorId,
    uint VoiceTypeLocalFormId,
    string QuestEditorId,
    uint QuestLocalFormId,
    ImmutableArray<SkyrimDialogueRecordAllocation> Records,
    ImmutableArray<SkyrimDialogueOutputAsset> Assets,
    string? SeqFile,
    Sha256Hash? SeqSha256,
    string? ScriptFile,
    Sha256Hash? ScriptSha256,
    string LipTool,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimDialogueVerification(
    string Schema,
    string ManifestPath,
    Sha256Hash ManifestSha256,
    bool Verified,
    int RecordCount,
    int InfoCount,
    int AudioCount,
    int LipCount,
    SkyrimDialogueBudget Budget,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimDialogueTemplateRequest(
    string Template,
    WorkspacePath Profile,
    SkyrimDialogueNpcIdentity Npc,
    string Language,
    WorkspacePath ManifestOutput);

public sealed record SkyrimDialogueAnalyzeRequest(
    WorkspacePath Manifest,
    Sha256Hash ManifestSha256,
    WorkspacePath Plugin,
    Sha256Hash PluginSha256,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> LoadOrder,
    WorkspacePath SampleAuthority,
    Sha256Hash SampleAuthoritySha256,
    WorkspacePath Output);

public sealed record SkyrimDialogueApplyRequest(
    WorkspacePath Proposal,
    Sha256Hash ProposalSha256,
    WorkspacePath Synthesis,
    Sha256Hash SynthesisSha256,
    WorkspacePath OutputRoot,
    WorkspacePath? LipTools);

public sealed record SkyrimDialogueVerifyRequest(
    WorkspacePath Manifest,
    Sha256Hash ManifestSha256);

public sealed record SkyrimDialogueStageResult<T>(
    bool Succeeded,
    T? Document,
    WorkspacePath? DocumentPath,
    Sha256Hash? DocumentSha256,
    ImmutableArray<Diagnostic> Diagnostics)
    where T : class;

/// <summary>
/// Application seam for the dialogue transaction. Template creation writes a
/// draft manifest only; analyze plans records and assets without writing game
/// files; apply performs the hash-bound reviewed write; verify reopens the
/// output independently.
/// </summary>
public interface ISkyrimNpcDialogueService
{
    ValueTask<SkyrimDialogueStageResult<SkyrimDialogueManifest>> CreateTemplateAsync(
        SkyrimDialogueTemplateRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimDialogueStageResult<SkyrimDialogueProposal>> AnalyzeAsync(
        SkyrimDialogueAnalyzeRequest request,
        CancellationToken cancellationToken);

    ValueTask<SkyrimDialogueStageResult<SkyrimDialogueOutputManifest>> ApplyAsync(
        SkyrimDialogueApplyRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken);

    ValueTask<SkyrimDialogueStageResult<SkyrimDialogueVerification>> VerifyAsync(
        SkyrimDialogueVerifyRequest request,
        CancellationToken cancellationToken);
}
