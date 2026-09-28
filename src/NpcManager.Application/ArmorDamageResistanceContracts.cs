using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record ArmorDamageResistanceEntry(FormReference DamageType, uint Value);

public sealed record ArmorDamageResistanceRequest(
    GameEdition Edition,
    WorkspacePath SourcePlugin,
    FormId SourceFormId,
    ImmutableArray<ArmorDamageResistanceEntry> Entries,
    WorkspacePath OutputProposal);

public sealed record ArmorDamageResistanceArtifact(
    string SchemaVersion,
    string ArtifactKind,
    string Edition,
    string SourcePlugin,
    string SourceFormId,
    string InputSha256,
    ImmutableArray<ArmorDamageResistanceEntryArtifact> Entries,
    ImmutableArray<string> MasterDependencies,
    bool NoUnrelatedRecords);

public sealed record ArmorDamageResistanceEntryArtifact(string DamageType, uint Value);

public sealed record ArmorDamageResistanceResult(
    bool Written,
    ArmorDamageResistanceArtifact? Artifact,
    Sha256Hash? OutputSha256,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IArmorDamageResistanceService
{
    ValueTask<ArmorDamageResistanceResult> ProposeAsync(ArmorDamageResistanceRequest request,
        CancellationToken cancellationToken);
}
