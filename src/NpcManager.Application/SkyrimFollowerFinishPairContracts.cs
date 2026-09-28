using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// One immutable file admitted into a paired follower-finish transaction.
/// Every path is K-local and every read is rebound to length plus SHA-256.
/// </summary>
public sealed record SkyrimFollowerFinishPairFile(
    WorkspacePath Path,
    long ByteLength,
    Sha256Hash Sha256);

/// <summary>
/// Runtime-bearing files for one already accepted Manager-authored actor.
/// PackageRoot is copied selectively; the transaction never rewrites it.
/// </summary>
public sealed record SkyrimFollowerFinishPairActor(
    string Role,
    WorkspacePath PackageRoot,
    PluginName Plugin,
    SkyrimFollowerFinishPairFile PluginFile,
    FormId ActorFormId,
    SkyrimFollowerFinishPairFile FaceGeom,
    SkyrimFollowerFinishPairFile FaceTint,
    SkyrimFollowerFinishPairFile BodyGenTemplates,
    SkyrimFollowerFinishPairFile BodyGenMorphs,
    SkyrimFollowerFinishPairFile RuntimeScript);

/// <summary>
/// Exact installed-provider closure selected for the subject's outfit.
/// Provider archives are hash-bound evidence/dependencies, not redistributed.
/// </summary>
public sealed record SkyrimFollowerFinishPairOutfit(
    SkyrimFollowerFinishPairFile PluginFile,
    PluginName Plugin,
    SkyrimFollowerFinishPairFile BaseMeshArchive,
    SkyrimFollowerFinishPairFile WinningFemaleMeshArchive,
    SkyrimFollowerFinishPairFile TextureArchive,
    FormReference TorsoArmor,
    FormReference TorsoArmorAddon,
    FormReference Boots,
    FormReference Gauntlets,
    FormReference TargetFemaleSkinTextureSet);

public sealed record SkyrimFollowerFinishPairTransform(
    double X,
    double Y,
    double Z,
    double RotationX,
    double RotationY,
    double RotationZ);

public sealed record SkyrimFollowerFinishPairPlacement(
    FormReference Worldspace,
    FormReference Cell,
    int CellGridX,
    int CellGridY,
    SkyrimFollowerFinishPairTransform Subject);

/// <summary>
/// Deterministic subject-owned record allocation. Existing accepted records
/// retain their local IDs and FaceGen filename; no compaction is performed.
/// </summary>
public sealed record SkyrimFollowerFinishPairAllocation(
    FormId PrivateArmorAddon,
    FormId PrivateArmor,
    FormId Outfit,
    FormId Package,
    FormId PlacedActor,
    FormId SubjectToCompanionRelationship,
    FormId CompanionToSubjectRelationship,
    FormId NextFormId)
{
    public static SkyrimFollowerFinishPairAllocation Default { get; } =
        new(
            new FormId(0x805),
            new FormId(0x806),
            new FormId(0x807),
            new FormId(0x808),
            new FormId(0x809),
            new FormId(0x80A),
            new FormId(0x80B),
            new FormId(0x80C));

    [JsonIgnore]
    public ImmutableArray<FormId> NewRecordIds =>
    [
        PrivateArmorAddon,
        PrivateArmor,
        Outfit,
        Package,
        PlacedActor,
        SubjectToCompanionRelationship,
        CompanionToSubjectRelationship
    ];
}

/// <summary>
/// Deterministic companion-owned outfit allocation. These records are added
/// only by schema 3; the accepted companion actor and placement keep their
/// existing local IDs.
/// </summary>
public sealed record SkyrimFollowerFinishPairCompanionAllocation(
    FormId PrivateArmorAddon,
    FormId PrivateArmor,
    FormId Outfit,
    FormId NextFormId)
{
    public static SkyrimFollowerFinishPairCompanionAllocation Default { get; } =
        new(
            new FormId(0x808),
            new FormId(0x809),
            new FormId(0x80A),
            new FormId(0x80B));

    [JsonIgnore]
    public ImmutableArray<FormId> NewRecordIds =>
    [
        PrivateArmorAddon,
        PrivateArmor,
        Outfit
    ];
}

/// <summary>
/// Exact plugin and FaceGeom hair-tint transition for an accepted companion.
/// RGB values are bytes so the NIF float encoding is deterministic.
/// </summary>
public sealed record SkyrimFollowerFinishPairHairFinish(
    FormId ColorFormId,
    uint OldPackedRgb,
    uint NewPackedRgb,
    ImmutableArray<string> FaceGeomShapeNames,
    ImmutableArray<byte> OldFaceGeomRgb,
    ImmutableArray<byte> NewFaceGeomRgb);

public sealed record SkyrimFollowerFinishPairCompanionFinish(
    SkyrimFollowerFinishPairCompanionAllocation Allocation,
    SkyrimFollowerFinishPairHairFinish Hair);

/// <summary>
/// Closed request for pairing two accepted Manager-authored actors. Schema 2
/// preserves the companion byte-for-byte; schema 3 may finish its explicitly
/// bound hair and outfit surfaces.
/// </summary>
public sealed record SkyrimFollowerFinishPairRequest(
    int SchemaVersion,
    string Operation,
    SkyrimFollowerFinishPairActor Companion,
    SkyrimFollowerFinishPairActor Subject,
    FormReference CompanionAnchor,
    SkyrimFollowerFinishPairOutfit Outfit,
    SkyrimFollowerFinishPairPlacement Placement,
    SkyrimFollowerFinishPairAllocation Allocation,
    WorkspacePath OutputRoot,
    WorkspacePath OutputZip,
    string Narrative,
    SkyrimFollowerFinishPairCompanionFinish? CompanionFinish = null)
{
    public const int LegacySchemaVersionValue = 2;
    public const int SchemaVersionValue = 3;
    public const string OperationName =
        "skyrim-paired-follower-finish";
}

public sealed record SkyrimFollowerFinishPairSourceSnapshot(
    PluginName Plugin,
    Sha256Hash PluginSha256,
    uint Tes4Flags,
    ImmutableArray<PluginName> Masters,
    FormId NextFormId,
    ImmutableArray<string> RecordInventory,
    FormReference Race,
    FormReference? HairColor,
    FormReference? FaceTextureSet,
    FormReference? DefaultOutfit,
    ImmutableArray<FormReference> Packages);

public sealed record SkyrimFollowerFinishPairProposal(
    int SchemaVersion,
    string Operation,
    Sha256Hash RequestSha256,
    SkyrimFollowerFinishPairRequest Request,
    SkyrimFollowerFinishPairSourceSnapshot CompanionSnapshot,
    SkyrimFollowerFinishPairSourceSnapshot SubjectSnapshot,
    ImmutableArray<PluginName> OutputMasters,
    ImmutableArray<string> ExistingRecordChanges,
    ImmutableArray<string> NewRecords,
    bool RuntimeAuthority,
    ImmutableArray<PluginName>? CompanionOutputMasters = null);

public sealed record SkyrimFollowerFinishPairFaceGeomRewrite(
    ImmutableArray<byte> Bytes,
    ImmutableArray<int> ChangedByteOffsets);

/// <summary>
/// Narrow internal-product boundary for exact SSE FaceGeom HairTint rewrites.
/// Implementations must fail closed on unsupported NIFs or shared shaders.
/// </summary>
public interface ISkyrimFollowerFinishPairFaceGeomService
{
    SkyrimFollowerFinishPairFaceGeomRewrite Rewrite(
        ImmutableArray<byte> source,
        SkyrimFollowerFinishPairHairFinish request);

    void Verify(
        ImmutableArray<byte> source,
        ImmutableArray<byte> output,
        SkyrimFollowerFinishPairHairFinish request,
        ImmutableArray<int> expectedChangedByteOffsets);
}

public sealed record SkyrimFollowerFinishPairResult(
    bool Succeeded,
    string Verdict,
    WorkspacePath? Proposal,
    Sha256Hash? ProposalSha256,
    WorkspacePath? Manifest,
    Sha256Hash? ManifestSha256,
    WorkspacePath? OutputPlugin,
    Sha256Hash? OutputPluginSha256,
    WorkspacePath? OutputZip,
    Sha256Hash? OutputZipSha256,
    ImmutableArray<Diagnostic> Diagnostics,
    bool RuntimeAuthority);

public interface ISkyrimFollowerFinishPairService
{
    ValueTask<SkyrimFollowerFinishPairResult> AnalyzeAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken);

    ValueTask<SkyrimFollowerFinishPairResult> ApplyAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken);

    ValueTask<SkyrimFollowerFinishPairResult> VerifyAsync(
        WorkspacePath requestPath,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        Sha256Hash proposalSha256,
        WorkspacePath manifestPath,
        CancellationToken cancellationToken);
}
