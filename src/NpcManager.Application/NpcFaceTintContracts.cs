using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>FO4 NPC_.TETI discriminator values. The upstream editor treats value/color entries
/// as palette or mask options after resolving the RACE template; that provider lookup remains
/// outside this static, same-plugin byte patch.</summary>
public enum NpcFaceTintDataType : ushort
{
    ValueColor = 1,
    TextureSet = 2
}

public sealed record NpcFaceTintColor(byte Red, byte Green, byte Blue);

/// <summary>A typed TETI/TEND pair. RawTendBase64 is required for TextureSet entries so unknown
/// optional TEND bytes remain byte-preserved; palette entries may also carry it to preserve the
/// source's 1/5/7-byte optional-member shape.</summary>
public sealed record NpcFaceTintLayer(
    NpcFaceTintDataType DataType,
    ushort OptionIndex,
    byte Value,
    NpcFaceTintColor? Color,
    short? TemplateColorIndex,
    string? RawTendBase64);

public sealed record NpcFaceTintPatch(ImmutableArray<NpcFaceTintLayer> Layers)
{
    public bool IsEmpty => Layers.IsDefaultOrEmpty;
}

public sealed record NpcFaceTintSnapshot(ImmutableArray<NpcFaceTintLayer> Layers);

public sealed record NpcFaceTintPatchRequest(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    NpcFaceTintPatch Patch,
    Sha256Hash? ExpectedInputHash,
    bool DryRun,
    WorkspacePath? ProposalPath);

public sealed record NpcFaceTintPatchProposal(
    GameEdition Edition,
    WorkspacePath InputPlugin,
    WorkspacePath OutputPlugin,
    FormId TargetFormId,
    Sha256Hash InputHash,
    ImmutableArray<MutationChange> Changes,
    ImmutableArray<string> PreservedFields,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsApplicable => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) && Changes.Length > 0;
}

public sealed record NpcFaceTintPatchResult(
    bool Applied,
    NpcFaceTintPatchProposal Proposal,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface INpcFaceTintPatchService
{
    ValueTask<NpcFaceTintPatchProposal> AnalyzeAsync(NpcFaceTintPatchRequest request, CancellationToken cancellationToken);

    ValueTask<NpcFaceTintPatchResult> ApplyAsync(NpcFaceTintPatchRequest request, NpcFaceTintPatchProposal proposal,
        CancellationToken cancellationToken);
}
