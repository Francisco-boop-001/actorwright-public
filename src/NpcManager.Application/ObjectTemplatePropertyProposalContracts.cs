using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record ObjectTemplatePropertyProposal(string ValueType, byte FunctionType, ushort PropertyIndex,
    int? Value1Integer, double? Value1Float, FormReference? Value1FormId, int Value2Integer, double Value2Float, double StepValue,
    short CombinationIndex = 0);

public sealed record ObjectTemplatePropertyProposalRequest(GameEdition Edition, WorkspacePath SourcePlugin, FormId SourceFormId,
    ImmutableArray<ObjectTemplatePropertyProposal> Properties, WorkspacePath OutputProposal);

public sealed record ObjectTemplatePropertyArtifact(string ValueType, byte FunctionType, ushort PropertyIndex,
    int? Value1Integer, double? Value1Float, string? Value1FormId, int Value2Integer, double Value2Float, double StepValue,
    short CombinationIndex = 0);

public sealed record ObjectTemplatePropertyProposalArtifact(string SchemaVersion, string ArtifactKind, string Edition,
    string SourcePlugin, string SourceFormId, string EditorId, string InputSha256, string PatchSha256,
    ImmutableArray<ObjectTemplatePropertyArtifact> Properties, ImmutableArray<string> MasterDependencies, bool NoUnrelatedRecords);

public sealed record ObjectTemplatePropertyProposalResult(bool Written, ObjectTemplatePropertyProposalArtifact? Artifact,
    Sha256Hash? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);

public interface IObjectTemplatePropertyProposalService
{
    ValueTask<ObjectTemplatePropertyProposalResult> ProposeAsync(ObjectTemplatePropertyProposalRequest request,
        CancellationToken cancellationToken);
}
