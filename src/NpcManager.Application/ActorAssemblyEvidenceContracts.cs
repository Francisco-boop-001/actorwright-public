using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record ActorAssemblyAppliedOutput(
    string OutputId,
    ImmutableArray<string> AppliedChannels);

public abstract record ActorAssemblyOwnerEvidence(
    int SchemaVersion,
    string ArtifactKind,
    GameEdition Edition,
    ActorAssemblyActorIdentity BaseNpc,
    ImmutableArray<ActorAssemblyAppliedOutput> AppliedOutputs);

public sealed record ActorAssemblyOBodyEvidence(
    int SchemaVersion,
    string ArtifactKind,
    GameEdition Edition,
    ActorAssemblyActorIdentity BaseNpc,
    ActorAssemblyBoundFile Preset,
    ImmutableArray<ActorAssemblyBoundFile> Assignments,
    ImmutableArray<ActorAssemblyAppliedOutput> AppliedOutputs)
    : ActorAssemblyOwnerEvidence(SchemaVersion, ArtifactKind, Edition, BaseNpc, AppliedOutputs);

public sealed record ActorAssemblyBodyGenChannel(string Name, float Value);

public sealed record ActorAssemblyBodyGenEvidence(
    int SchemaVersion,
    string ArtifactKind,
    GameEdition Edition,
    ActorAssemblyActorIdentity BaseNpc,
    ActorAssemblyBoundFile Assignment,
    ActorAssemblyBoundFile Templates,
    ActorAssemblyBoundFile Morphs,
    ImmutableArray<ActorAssemblyBodyGenChannel> Channels,
    ImmutableArray<ActorAssemblyAppliedOutput> AppliedOutputs)
    : ActorAssemblyOwnerEvidence(SchemaVersion, ArtifactKind, Edition, BaseNpc, AppliedOutputs);

public sealed record ActorAssemblyRuntimeScriptEvidence(
    int SchemaVersion,
    string ArtifactKind,
    GameEdition Edition,
    ActorAssemblyActorIdentity BaseNpc,
    ActorAssemblyBoundFile Plugin,
    ActorAssemblyBoundFile Pex,
    ActorAssemblyBoundFile Vmad,
    ActorAssemblyBoundFile SemanticReview,
    ImmutableArray<ActorAssemblyBodyGenChannel> Channels,
    ImmutableArray<ActorAssemblyAppliedOutput> AppliedOutputs)
    : ActorAssemblyOwnerEvidence(SchemaVersion, ArtifactKind, Edition, BaseNpc, AppliedOutputs);

public sealed record ActorAssemblyCompositeComponent(
    ActorAssemblyMorphOwner Owner,
    ActorAssemblyBoundFile? Evidence);

public sealed record ActorAssemblyCompositeComponentsEvidence(
    int SchemaVersion,
    string ArtifactKind,
    GameEdition Edition,
    ActorAssemblyActorIdentity BaseNpc,
    ImmutableArray<ActorAssemblyCompositeComponent> Components,
    ImmutableArray<ActorAssemblyAppliedOutput> AppliedOutputs)
    : ActorAssemblyOwnerEvidence(SchemaVersion, ArtifactKind, Edition, BaseNpc, AppliedOutputs);

public abstract record ActorAssemblyOutfitEntry(string OutputId);
public sealed record ActorAssemblyMorphableOutfitEntry(
    string OutputId,
    ImmutableArray<AssetPath> NifPackagePaths,
    AssetPath? TriPackagePath,
    ActorAssemblyEvidenceReference Receipt) : ActorAssemblyOutfitEntry(OutputId);
public sealed record ActorAssemblyNonMorphableOutfitEntry(
    string OutputId,
    AssetPath PackagePath,
    string Reason) : ActorAssemblyOutfitEntry(OutputId);

public sealed record ActorAssemblyOutfitInventory(
    int SchemaVersion,
    string ArtifactKind,
    GameEdition Edition,
    Sha256Hash PackageManifestSha256,
    ActorAssemblyActorIdentity BaseNpc,
    ImmutableArray<ActorAssemblyOutfitEntry> Entries);

public sealed record ActorAssemblyTool(
    string Name,
    string Version,
    ActorAssemblyBoundFile Executable);

public sealed record ActorAssemblyOutfitReceiptFile(
    AssetPath PackagePath,
    Sha256Hash Sha256);

public sealed record ActorAssemblyOutfitReceiptOutput(
    string OutputId,
    ImmutableArray<ActorAssemblyOutfitReceiptFile> Nifs,
    ActorAssemblyOutfitReceiptFile? Tri);

public sealed record ActorAssemblyReceiptPreset(
    ActorAssemblyBoundFile File,
    string PresetName,
    string SliderSet);

public sealed record ActorAssemblyOutfitBuildReceipt(
    int SchemaVersion,
    string ArtifactKind,
    GameEdition Edition,
    ActorAssemblyTool Tool,
    ActorAssemblyReceiptPreset SourcePreset,
    ImmutableArray<ActorAssemblyOutfitReceiptOutput> Outputs);

public sealed record ActorAssemblyRuntimeChannelReview(
    int SchemaVersion,
    string ArtifactKind,
    string DecisionId,
    string Decision,
    ActorAssemblyActorIdentity BaseNpc,
    Sha256Hash ScriptSha256,
    ImmutableArray<string> Channels);

public sealed record ActorAssemblyReviewedCompositePolicyComponent(
    ActorAssemblyMorphOwner Owner,
    Sha256Hash? EvidenceSha256);

public sealed record ActorAssemblyReviewedCompositePolicyOutput(
    AssetPath PackagePath,
    Sha256Hash Sha256);

public sealed record ActorAssemblyReviewedCompositePolicy(
    int SchemaVersion,
    string ArtifactKind,
    GameEdition Edition,
    string DecisionId,
    string Decision,
    ActorAssemblyActorIdentity BaseNpc,
    ImmutableArray<ActorAssemblyReviewedCompositePolicyComponent> Components,
    Sha256Hash OutfitInventorySha256,
    ImmutableArray<Sha256Hash> ReceiptSha256s,
    ImmutableArray<ActorAssemblyReviewedCompositePolicyOutput> Outputs);

public interface IActorAssemblyEvidenceLoader
{
    ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyOwnerEvidence>> LoadOwnerEvidenceAsync(
        ActorAssemblyBoundFile file, CancellationToken cancellationToken);
    ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyOutfitInventory>> LoadOutfitInventoryAsync(
        ActorAssemblyBoundFile file, CancellationToken cancellationToken);
    ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyOutfitBuildReceipt>> LoadReceiptAsync(
        ActorAssemblyBoundFile file, CancellationToken cancellationToken);
    ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyRuntimeChannelReview>> LoadRuntimeReviewAsync(
        ActorAssemblyBoundFile file, CancellationToken cancellationToken);
    ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyReviewedCompositePolicy>> LoadCompositePolicyAsync(
        ActorAssemblyBoundFile file, CancellationToken cancellationToken);
}
