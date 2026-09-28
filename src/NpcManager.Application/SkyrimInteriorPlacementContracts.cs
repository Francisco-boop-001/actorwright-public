using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Closed, opt-in request for a separate interior placement patch.  The
/// transaction never edits the Finish Core package; it binds that package and
/// writes a new light patch beside it.
/// </summary>
public sealed record SkyrimInteriorPlacementRequest
{
    public const string SchemaIdentifier = "npc.interior-placement.request.v1";

    public string Schema { get; init; } = SchemaIdentifier;

    public SkyrimInteriorPlacementFinishCoreBinding FinishCore { get; init; } = new();

    public ImmutableArray<SkyrimInteriorPlacementProvider> LoadOrder { get; init; } =
        ImmutableArray<SkyrimInteriorPlacementProvider>.Empty;

    public SkyrimInteriorPlacementCell Cell { get; init; } = new();

    public SkyrimInteriorPlacementTransform Transform { get; init; } = new();

    public SkyrimInteriorPlacementLocation? Location { get; init; }

    public SkyrimInteriorPlacementPatch Patch { get; init; } = new();

    public SkyrimInteriorPlacementOutput Output { get; init; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlacementMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Marker { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? SandboxRadius { get; init; }
}

public sealed record SkyrimInteriorPlacementFinishCoreBinding
{
    public string Manifest { get; init; } = string.Empty;

    public string ManifestSha256 { get; init; } = string.Empty;
}

public sealed record SkyrimInteriorPlacementProvider
{
    public string Plugin { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;

    public int Order { get; init; }
}

public sealed record SkyrimInteriorPlacementCell
{
    public string ProviderPlugin { get; init; } = string.Empty;

    public string Owner { get; init; } = string.Empty;

    /// <summary>Raw owner-indexed CELL FormID in the winning provider.</summary>
    public string RawFormId { get; init; } = string.Empty;

    public string EditorId { get; init; } = string.Empty;

    public uint InteriorBlock { get; init; }

    public uint InteriorSubBlock { get; init; }
}

public enum SkyrimInteriorPlacementTransformMode
{
    ExplicitValues,
    ExistingReference
}

public sealed record SkyrimInteriorPlacementTransform
{
    public SkyrimInteriorPlacementTransformMode Mode { get; init; }

    public string? Reference { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Z { get; init; }

    public double RotationX { get; init; }

    public double RotationY { get; init; }

    public double RotationZ { get; init; }
}

public sealed record SkyrimInteriorPlacementLocation
{
    public string Reference { get; init; } = string.Empty;

    public string RawFormId { get; init; } = string.Empty;
}

public sealed record SkyrimInteriorPlacementPatch
{
    public bool Optional { get; init; } = true;
}

public sealed record SkyrimInteriorPlacementOutput
{
    public string Root { get; init; } = string.Empty;

    public string Archive { get; init; } = string.Empty;
}

public enum SkyrimInteriorPlacementStatus
{
    ReadyForReviewedWrite,
    StaticPassRuntimeRequired,
    Refused
}

public sealed record SkyrimInteriorPlacementProposal
{
    public const string SchemaIdentifier = "npc.interior-placement.proposal.v1";

    public string Schema { get; init; } = SchemaIdentifier;

    public string RequestSha256 { get; init; } = string.Empty;

    public string ProposalSha256 { get; init; } = string.Empty;

    public SkyrimInteriorPlacementStatus Status { get; init; }

    public string PatchPlugin { get; init; } = string.Empty;

    public ImmutableArray<string> MasterOrder { get; init; } = ImmutableArray<string>.Empty;

    public string CellOwner { get; init; } = string.Empty;

    public string CellRawFormId { get; init; } = string.Empty;

    public string NpcOwner { get; init; } = string.Empty;

    public string NpcRawFormId { get; init; } = string.Empty;

    public string? LocationOwner { get; init; }

    public string? LocationRawFormId { get; init; }

    public string CellEditorId { get; init; } = string.Empty;

    public uint InteriorBlock { get; init; }

    public uint InteriorSubBlock { get; init; }

    public float X { get; init; }

    public float Y { get; init; }

    public float Z { get; init; }

    public float RotationX { get; init; }

    public float RotationY { get; init; }

    public float RotationZ { get; init; }

    public bool ConflictContained { get; init; }

    public bool ConflictFree { get; init; }

    public bool PathingAuthority { get; init; }

    public bool RuntimeAuthority { get; init; }

    public string CorePlugin { get; init; } = string.Empty;

    public string CoreNpc { get; init; } = string.Empty;

    public ImmutableArray<string> ProviderChain { get; init; } = ImmutableArray<string>.Empty;

    public ImmutableArray<string> Diagnostics { get; init; } = ImmutableArray<string>.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlacementMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SkyrimQuestAliasPlacementEvidence? QuestAlias { get; init; }
}

public sealed record SkyrimInteriorPlacementManifest
{
    public const string SchemaIdentifier = "npc.interior-placement.manifest.v1";

    public string Schema { get; init; } = SchemaIdentifier;

    public string PatchPlugin { get; init; } = string.Empty;

    public string PatchPath { get; init; } = string.Empty;

    public string PatchSha256 { get; init; } = string.Empty;

    public string Archive { get; init; } = string.Empty;

    public string ArchiveSha256 { get; init; } = string.Empty;

    public string RequestSha256 { get; init; } = string.Empty;

    public string ProposalSha256 { get; init; } = string.Empty;

    public string FinishCoreManifestSha256 { get; init; } = string.Empty;

    public string CorePlugin { get; init; } = string.Empty;

    public string CoreNpc { get; init; } = string.Empty;

    public string Cell { get; init; } = string.Empty;

    public string CellRawFormId { get; init; } = string.Empty;

    public string NpcRawFormId { get; init; } = string.Empty;

    public uint InteriorBlock { get; init; }

    public uint InteriorSubBlock { get; init; }

    public string? LocationRawFormId { get; init; }

    public ImmutableArray<string> MasterOrder { get; init; } = ImmutableArray<string>.Empty;

    public string PlacedReference { get; init; } = string.Empty;

    public bool ConflictContained { get; init; }

    public bool ConflictFree { get; init; }

    public bool PathingAuthority { get; init; }

    public bool RuntimeAuthority { get; init; }

    public bool VisualAuthority { get; init; }

    public SkyrimInteriorPlacementStatus Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlacementMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SkyrimQuestAliasPlacementEvidence? QuestAlias { get; init; }
}

public sealed record SkyrimInteriorPlacementVerification
{
    public const string SchemaIdentifier = "npc.interior-placement.verification.v1";

    public string Schema { get; init; } = SchemaIdentifier;

    public bool Verified { get; init; }

    public SkyrimInteriorPlacementStatus Status { get; init; }

    public string PatchPlugin { get; init; } = string.Empty;

    public int Tes4Count { get; init; }

    public int CellCount { get; init; }

    public int AchrCount { get; init; }

    public bool HasOnlyEdidCell { get; init; }

    public bool HasPersistentActor { get; init; }

    public bool ConflictContained { get; init; }

    public bool RuntimeAuthority { get; init; }

    public ImmutableArray<string> Diagnostics { get; init; } = ImmutableArray<string>.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlacementMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SkyrimQuestAliasPlacementEvidence? QuestAlias { get; init; }
}

public sealed record SkyrimQuestAliasPlacementEvidence(
    string Marker,
    string MarkerProvider,
    string SandboxPackage,
    uint SandboxRadius,
    string Quest,
    string QuestRawFormId,
    string PackageRecordSha256,
    string SeqPath,
    string SeqSha256)
{
    public const string Mode = "quest-alias";

    public int QuestCount { get; init; } = 1;

    public int AliasCount { get; init; } = 2;

    public int PackageCount { get; init; } = 1;

    public bool StartGameEnabled { get; init; } = true;
}

public sealed record SkyrimInteriorPlacementProposalResult(
    bool Proposed,
    SkyrimInteriorPlacementProposal? Proposal,
    WorkspacePath? ProposalPath,
    Sha256Hash? ProposalSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimInteriorPlacementApplyResult(
    bool Applied,
    SkyrimInteriorPlacementManifest? Manifest,
    WorkspacePath? OutputRoot,
    WorkspacePath? Archive,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimInteriorPlacementVerificationResult(
    bool Verified,
    SkyrimInteriorPlacementVerification? Verification,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimInteriorPlacementService
{
    ValueTask<SkyrimInteriorPlacementProposalResult> AnalyzeAsync(
        SkyrimInteriorPlacementRequest request,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken);

    ValueTask<SkyrimInteriorPlacementApplyResult> ApplyAsync(
        SkyrimInteriorPlacementRequest request,
        Sha256Hash requestSha256,
        SkyrimInteriorPlacementProposal proposal,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken);

    ValueTask<SkyrimInteriorPlacementVerificationResult> VerifyAsync(
        WorkspacePath manifestPath,
        Sha256Hash manifestSha256,
        CancellationToken cancellationToken);
}
