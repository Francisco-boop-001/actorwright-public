using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// External identifiers for the Actor Assembly preflight document family.
/// These identifiers describe the existing v1 wire shapes; they are not
/// serialized as an additional <c>schema</c> member in those documents.
/// </summary>
public static class ActorAssemblyPreflightSchemas
{
    public const string ContractSchema =
        "npc.actor-assembly-preflight.contract.v1";
    public const string ResultSchema =
        "npc.actor-assembly-preflight.result.v1";
    public const string ErrorSchema =
        "npc.actor-assembly-preflight.error.v1";
    public const string ProtocolResultSchema =
        "urn:actorwright:protocol-v2:npc-assembly-preflight-result:v1";
    public const string ArtifactKind = "actor-assembly-preflight";

    public const string LegacyResultArtifactKind =
        "actor-assembly-preflight-result";
    public const string LegacyErrorArtifactKind =
        "actor-assembly-preflight-error";
}

public enum ActorAssemblyOutcome { Pass, Blocked, Unknown, NotApplicable }
public enum ActorAssemblyPlacementMode { PersistentReference, QuestAlias, DynamicSpawn, None }
public enum ActorAssemblyMorphOwner { None, OBody, BodyGen, BakedBodySlide, RuntimeScript, ReviewedComposite, Unknown }
public enum ActorAssemblyOutfitScopeStatus { Complete, Unavailable, NotApplicable }
public enum ActorAssemblyEvidenceStatus { Available, Unavailable, NotApplicable }
public enum ActorAssemblyObservationStatus { Found, Missing, Unknown }
public enum ActorAssemblyEvidenceKind { Contract, Manifest, File, PackageFile, Record, Decision }
public enum ActorAssemblyDocumentDisposition { Loaded, Missing, Unreadable, HashMismatch, Invalid, SecurityRefused }

public sealed record ActorAssemblyBoundFile(WorkspacePath Path, Sha256Hash Sha256);
public sealed record ActorAssemblyEvidenceReference(
    ActorAssemblyEvidenceStatus Status,
    ActorAssemblyBoundFile? File,
    string? Reason);
public sealed record ActorAssemblyActorIdentity(PluginName Plugin, FormId FormId);
public sealed record ActorAssemblyPlacement(
    ActorAssemblyPlacementMode Mode,
    FormId? PlacedReferenceFormId);
public sealed record ActorAssemblyBodyMorph(
    ActorAssemblyMorphOwner Owner,
    ActorAssemblyEvidenceReference Evidence);
public sealed record ActorAssemblyOutfitScope(
    ActorAssemblyOutfitScopeStatus Status,
    ActorAssemblyBoundFile? Inventory,
    string? Reason);

public sealed record ActorAssemblyContract(
    int SchemaVersion,
    string Operation,
    GameEdition Edition,
    ActorAssemblyBoundFile PackageManifest,
    ActorAssemblyActorIdentity BaseNpc,
    ActorAssemblyPlacement Placement,
    ActorAssemblyBodyMorph BodyMorph,
    ActorAssemblyOutfitScope OutfitScope,
    ActorAssemblyEvidenceReference? ReviewedCompositePolicy)
{
    public const string SchemaIdentifier =
        ActorAssemblyPreflightSchemas.ContractSchema;
}

public sealed record ActorAssemblyPreflightRequest(
    WorkspacePath ContractPath,
    Sha256Hash ContractSha256);

public sealed record ActorAssemblyDocumentLoadResult<T>(
    ActorAssemblyDocumentDisposition Disposition,
    T? Document,
    Sha256Hash? ActualSha256,
    bool SecurityRefusal,
    ImmutableArray<Diagnostic> Diagnostics) where T : class;

public interface IActorAssemblyContractLoader
{
    ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyContract>> LoadAsync(
        ActorAssemblyPreflightRequest request,
        CancellationToken cancellationToken);
}

public sealed record ActorAssemblyIdentityReadRequest(
    WorkspacePath PluginPath,
    ActorAssemblyActorIdentity BaseNpc,
    ActorAssemblyPlacement Placement);

public sealed record ActorAssemblyRecordObservation(
    ActorAssemblyObservationStatus Status,
    string? Signature,
    FormId? FormId,
    string? Reason);

public sealed record ActorAssemblyReferenceObservation(
    ActorAssemblyObservationStatus Status,
    PluginName? Plugin,
    FormId? FormId,
    string? Reason);

public sealed record ActorAssemblyBaseNpcEvidence(
    PluginName Plugin,
    FormId DeclaredFormId,
    ActorAssemblyRecordObservation TypedRecord,
    ActorAssemblyRecordObservation RawRecord,
    ActorAssemblyOutcome Outcome);

public sealed record ActorAssemblyPlacedReferenceEvidence(
    PluginName Plugin,
    FormId DeclaredFormId,
    ActorAssemblyRecordObservation TypedRecord,
    ActorAssemblyRecordObservation RawRecord,
    ActorAssemblyReferenceObservation TypedBase,
    ActorAssemblyReferenceObservation RawNameBase,
    ActorAssemblyOutcome Outcome);

public sealed record ActorAssemblyIdentityReadResult(
    ActorAssemblyBaseNpcEvidence BaseNpc,
    ActorAssemblyPlacedReferenceEvidence? PlacedReference,
    string DiagnosticTarget,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IActorAssemblyIdentityReader
{
    ValueTask<ActorAssemblyIdentityReadResult> ReadAsync(
        ActorAssemblyIdentityReadRequest request,
        CancellationToken cancellationToken);
}

public sealed record ActorAssemblyCheckEvidence(
    ActorAssemblyEvidenceKind Kind,
    string Value,
    Sha256Hash? Sha256);

public sealed record ActorAssemblyCheck(
    string Code,
    ActorAssemblyOutcome Outcome,
    string Message,
    ImmutableArray<ActorAssemblyCheckEvidence> Evidence);

public sealed record ActorAssemblyPreflightArtifact(
    int SchemaVersion,
    string ArtifactKind,
    ActorAssemblyOutcome Outcome,
    Sha256Hash ContractSha256,
    Sha256Hash PackageManifestSha256,
    ActorAssemblyBaseNpcEvidence BaseNpcEvidence,
    ActorAssemblyPlacedReferenceEvidence? PlacedReferenceEvidence,
    string DiagnosticTarget,
    ImmutableArray<ActorAssemblyCheck> Checks)
{
    public const string SchemaIdentifier =
        ActorAssemblyPreflightSchemas.ResultSchema;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822")]
    public bool Admitted => true;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822")]
    public bool NoWrite => true;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822")]
    public bool RuntimeAuthority => false;
}

public sealed record ActorAssemblyPreflightErrorArtifact(
    int SchemaVersion,
    string ArtifactKind,
    bool ContractAdmitted,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public const string SchemaIdentifier =
        ActorAssemblyPreflightSchemas.ErrorSchema;
}

public sealed record ActorAssemblyPreflightExecutionResult(
    bool ContractAdmitted,
    bool SecurityRefusal,
    ActorAssemblyPreflightArtifact? Artifact,
    ActorAssemblyPreflightErrorArtifact? Error);

public interface IActorAssemblyPreflightService
{
    ValueTask<ActorAssemblyPreflightExecutionResult> PreflightAsync(
        ActorAssemblyPreflightRequest request,
        CancellationToken cancellationToken);
}
