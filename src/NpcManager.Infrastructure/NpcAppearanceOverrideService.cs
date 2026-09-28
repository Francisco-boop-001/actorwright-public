using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>
/// Two-pass, no-overwrite transaction for a complete Skyrim appearance
/// override. Binary authoring and post-write evidence remain separate format
/// boundaries; this service owns only policy, proposal, and atomic promotion.
/// </summary>
public sealed partial class NpcAppearanceOverrideService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : INpcAppearanceOverrideService
{
    public ValueTask<NpcAppearanceOverrideProposal> AnalyzeAsync(
        NpcAppearanceOverrideRequest request,
        CancellationToken cancellationToken) =>
        AnalyzeCoreAsync(request, persistProposal: true, cancellationToken);

    public async ValueTask<NpcAppearanceOverrideResult> ApplyAsync(
        NpcAppearanceOverrideRequest request,
        NpcAppearanceOverrideProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(
            request,
            outputMustExist: false,
            proposalMustExist: true).ToBuilder();
        ValidateProposalBinding(request, proposal, diagnostics);
        if (!HasErrors(diagnostics))
        {
            var fresh = await AnalyzeCoreAsync(
                request,
                persistProposal: false,
                cancellationToken);
            diagnostics.AddRange(fresh.Diagnostics);
            if (!SameProposalSemantics(fresh, proposal))
            {
                diagnostics.Add(Error("npc-appearance-override-proposal-stale",
                    "Fresh source analysis no longer matches the persisted appearance proposal."));
            }
        }
        if (HasErrors(diagnostics))
            return new NpcAppearanceOverrideResult(
                false, proposal, null, diagnostics.ToImmutable());

        var destination = request.OutputPlugin.Value;
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileNameWithoutExtension(destination)}.tmp-{Guid.NewGuid():N}.esp");
        var promoted = false;
        try
        {
            BethesdaNpcAppearanceOverrideAdapter.Write(
                request,
                proposal,
                new WorkspacePath(temporary));
            FlushFile(temporary);
            var staged = BethesdaNpcAppearanceOverrideVerifier.Verify(
                request,
                proposal,
                new WorkspacePath(temporary),
                cancellationToken);
            diagnostics.AddRange(staged.Diagnostics);
            if (!staged.IsValid || HasErrors(diagnostics))
            {
                return new NpcAppearanceOverrideResult(
                    false, proposal, staged, diagnostics.ToImmutable());
            }

            File.Move(temporary, destination, overwrite: false);
            promoted = true;
            var final = BethesdaNpcAppearanceOverrideVerifier.Verify(
                request,
                proposal,
                request.OutputPlugin,
                cancellationToken);
            diagnostics.AddRange(final.Diagnostics);
            if (!final.IsValid || HasErrors(diagnostics))
            {
                TryDelete(destination);
                promoted = false;
                return new NpcAppearanceOverrideResult(
                    false, proposal, final, diagnostics.ToImmutable());
            }
            return new NpcAppearanceOverrideResult(
                true, proposal, final, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("npc-appearance-override-write-failed",
                exception.Message));
            if (promoted) TryDelete(destination);
            return new NpcAppearanceOverrideResult(
                false, proposal, null, diagnostics.ToImmutable());
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public ValueTask<NpcAppearanceOverrideVerificationResult> VerifyAsync(
        NpcAppearanceOverrideRequest request,
        NpcAppearanceOverrideProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(
            request,
            outputMustExist: true,
            proposalMustExist: true,
            proposalMayExist: true).ToBuilder();
        ValidateProposalBinding(request, proposal, diagnostics);
        if (HasErrors(diagnostics))
        {
            return ValueTask.FromResult(
                new NpcAppearanceOverrideVerificationResult(
                    false,
                    request.OutputPlugin,
                    null,
                    [],
                    [],
                    0,
                    0,
                    false,
                    false,
                    false,
                    false,
                    diagnostics.ToImmutable()));
        }

        var verified = BethesdaNpcAppearanceOverrideVerifier.Verify(
            request,
            proposal,
            request.OutputPlugin,
            cancellationToken);
        diagnostics.AddRange(verified.Diagnostics);
        return ValueTask.FromResult(verified with
        {
            IsValid = verified.IsValid && !HasErrors(diagnostics),
            Diagnostics = diagnostics.ToImmutable()
        });
    }

    private async ValueTask<NpcAppearanceOverrideProposal> AnalyzeCoreAsync(
        NpcAppearanceOverrideRequest request,
        bool persistProposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(
            request,
            outputMustExist: false,
            proposalMustExist: false,
            proposalMayExist: !persistProposal).ToBuilder();
        var sourceHash = File.Exists(request.SourcePlugin.Value)
            ? HashFile(request.SourcePlugin.Value)
            : request.ExpectedSourceSha256;
        if (File.Exists(request.SourcePlugin.Value) &&
            sourceHash != request.ExpectedSourceSha256)
        {
            diagnostics.Add(Error("npc-appearance-override-source-hash-mismatch",
                $"Source hash {sourceHash} does not match the bound hash {request.ExpectedSourceSha256}."));
        }

        var sourceName = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var outputName = new PluginName(Path.GetFileName(request.OutputPlugin.Value));
        var editorId = new EditorId("UnresolvedNpc");
        ImmutableArray<PluginName> masters = [];
        ImmutableArray<RecordSignature> signatures = [];
        var changed = ChangedSubrecords(
            request.RuntimeAppearance is not null,
            request.OutfitPatch);
        if (request.Appearance.NakedSkinBinding is not null) changed = changed.Add("WNAM");
        if (request.Appearance.ExposedOutfitSkinBinding is not null && !changed.Contains("DOFT")) changed = changed.Add("DOFT");
        if (!HasErrors(diagnostics))
        {
            try
            {
                var source = BethesdaNpcAppearanceOverrideAdapter.ReadSource(
                    request.SourcePlugin,
                    request.TargetFormId);
                editorId = source.EditorId;
                var dataRoot = new WorkspacePath(
                    Path.GetDirectoryName(request.SourcePlugin.Value)!);
                if (!BethesdaNpcAppearanceOverrideAdapter.RaceExists(
                        dataRoot,
                        request.Race))
                {
                    diagnostics.Add(Error("npc-appearance-override-race-missing",
                        "The preset-derived race does not resolve in the copied provider root."));
                }
                var appearance = BethesdaNpcCreationAdapter.ValidateAppearanceReferenceTypes(
                    dataRoot,
                    request.Appearance,
                    request.Sex,
                    request.PluginAuthorities,
                    request.Race);
                if (!appearance.IsValid)
                {
                    diagnostics.Add(Error("npc-appearance-override-provider-invalid",
                        "One or more authored HDPT, CLFM, or TXST references are missing, mistyped, or unqualified."));
                }
                ValidateOutfitPatch(
                    dataRoot,
                    request.OutfitPatch,
                    diagnostics);
                masters = BethesdaNpcAppearanceOverrideAdapter.BuildRequiredMasters(
                    request,
                    source);
                signatures = BethesdaNpcAppearanceOverrideAdapter
                    .ExpectedMajorRecordSignatures(request.Appearance);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(Error("npc-appearance-override-source-read-failed",
                    exception.Message));
            }
        }

        var proposal = new NpcAppearanceOverrideProposal(
            1,
            request.Edition,
            request.SourcePlugin,
            sourceHash,
            sourceName,
            request.TargetFormId,
            editorId,
            request.ProposalPath,
            null,
            request.OutputPlugin,
            outputName,
            request.Race,
            request.Sex,
            request.Appearance,
            request.RuntimeAppearance,
            masters,
            signatures,
            changed,
            diagnostics.ToImmutable(),
            request.OutfitPatch,
            request.IsCharGenFacePreset);
        if (persistProposal && proposal.IsApplicable)
        {
            try
            {
                var proposalHash = await WriteProposalAsync(
                    proposal,
                    cancellationToken);
                proposal = proposal with { ProposalSha256 = proposalHash };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(Error("npc-appearance-override-proposal-write-failed",
                    exception.Message));
                proposal = proposal with { Diagnostics = diagnostics.ToImmutable() };
            }
        }
        return proposal;
    }
}
