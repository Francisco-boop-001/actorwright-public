using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>
/// Hash-bound, two-pass service for a fresh one-NPC Skyrim plugin. The output
/// is built under a sibling temporary directory and promoted only after both
/// Bethesda read-back implementations accept the written bytes.
/// </summary>
public sealed partial class NpcCreationService : INpcCreationService
{
    private readonly IWorkspacePolicy policy;
    private readonly WorkspacePath labRoot;

    public NpcCreationService(IWorkspacePolicy policy, WorkspacePath labRoot)
    {
        this.policy = policy;
        this.labRoot = labRoot;
    }

    public ValueTask<NpcCreationProposal> AnalyzeAsync(
        NpcCreationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request, ValidationMode.Analyze).ToBuilder();
        var outputPlugin = ParseOutputPlugin(request.Output, diagnostics);
        var templateHash = HashIfPresent(request.TemplatePlugin, diagnostics);
        BethesdaNpcCreationTemplateSnapshot? template = null;

        if (templateHash != request.ExpectedTemplateHash)
        {
            diagnostics.Add(Error("npc-create-template-hash-mismatch",
                $"Template hash {templateHash} does not match the required hash {request.ExpectedTemplateHash}."));
        }
        ValidateSpecification(request, diagnostics);
        if (!HasErrors(diagnostics))
        {
            template = InspectTemplate(request, outputPlugin, diagnostics, cancellationToken);
        }
        var masters = template is null || HasErrors(diagnostics)
            ? ImmutableArray<PluginName>.Empty
            : BuildCreationMasterList(
                template.Masters,
                request.Appearance,
                outputPlugin,
                request.PluginAuthorities,
                diagnostics);

        var proposal = new NpcCreationProposal(
            request.Edition,
            request.TemplatePlugin,
            templateHash,
            request.TemplateNpcFormId,
            request.Proposal,
            null,
            request.Output,
            outputPlugin,
            new FormId(BethesdaNpcCreationAdapter.AllocatedLocalFormId),
            masters,
            request.Identity,
            request.Traits,
            request.References,
            request.Appearance,
            request.Stats,
            request.RuntimeAppearance,
            diagnostics.ToImmutable())
        {
            PluginAuthorities = request.PluginAuthorities.IsDefault
                ? []
                : request.PluginAuthorities,
            PluginType = request.PluginType
        };

        if (!HasErrors(diagnostics))
        {
            try
            {
                WriteProposal(proposal);
                var proposalHash = HashFile(request.Proposal.Value);
                proposal = proposal with { ProposalHash = proposalHash };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                diagnostics.Add(Error("npc-create-proposal-write-failed", exception.Message));
                proposal = proposal with { Diagnostics = diagnostics.ToImmutable() };
            }
        }

        return ValueTask.FromResult(proposal);
    }

    public async ValueTask<NpcCreationResult> ApplyAsync(
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request, ValidationMode.Apply).ToBuilder();
        ValidateSpecification(request, diagnostics);
        ValidateProposalBinding(request, proposal, diagnostics, cancellationToken);
        if (HasErrors(diagnostics)) return Refused(request, proposal, null, diagnostics.ToImmutable());

        var outputParent = Path.GetDirectoryName(request.Output.Value)!;
        var temporaryDirectory = Path.Combine(outputParent, ".npc-create-tmp-" + Guid.NewGuid().ToString("N"));
        var temporaryOutput = Path.Combine(temporaryDirectory, Path.GetFileName(request.Output.Value));
        var outputParentAncestry = CaptureExistingAncestry(outputParent, diagnostics, "output parent");
        if (HasErrors(diagnostics)) return Refused(request, proposal, null, diagnostics.ToImmutable());

        var promoted = false;
        NpcCreationVerificationResult? lastVerification = null;

        NpcCreationResult Failed()
        {
            var rollback = RollbackApply(request, temporaryDirectory, promoted, outputParentAncestry);
            diagnostics.AddRange(rollback.Diagnostics);
            return Refused(request, proposal, lastVerification, diagnostics.ToImmutable(),
                rollback.RemainingOutputHash);
        }

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(temporaryDirectory)));
            if (HasErrors(diagnostics)) return Failed();

            BethesdaNpcCreationAdapter.Write(request, proposal, new WorkspacePath(temporaryOutput));
            if (!File.Exists(temporaryOutput))
                throw new IOException("The Bethesda creation adapter did not write the temporary plugin.");
            FlushFile(temporaryOutput);

            var temporaryRequest = request with { Output = new WorkspacePath(temporaryOutput) };
            var temporaryVerification = BethesdaNpcCreationVerifier.Verify(
                temporaryRequest, proposal, cancellationToken);
            lastVerification = temporaryVerification;
            diagnostics.AddRange(temporaryVerification.Diagnostics);
            if (!temporaryVerification.IsValid) return Failed();

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryOutput, request.Output.Value, overwrite: false);
            promoted = true;

            var finalVerification = await VerifyAsync(request, proposal, cancellationToken);
            lastVerification = finalVerification;
            diagnostics.AddRange(finalVerification.Diagnostics);
            if (!finalVerification.IsValid) return Failed();

            var temporaryCleanup = CleanupTemporaryDirectory(temporaryDirectory, outputParentAncestry);
            diagnostics.AddRange(temporaryCleanup);
            if (temporaryCleanup.Any(item => item.Severity == DiagnosticSeverity.Error)) return Failed();

            return new NpcCreationResult(
                true,
                request.Proposal,
                finalVerification.ProposalHash,
                request.Output,
                finalVerification.OutputHash,
                proposal.AllocatedFormId,
                finalVerification.Masters,
                finalVerification,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException exception)
        {
            var rollback = RollbackApply(request, temporaryDirectory, promoted, outputParentAncestry);
            if (!rollback.Complete)
            {
                throw new IOException(
                    "NPC creation was canceled and rollback was incomplete: " +
                    string.Join(" | ", rollback.Diagnostics.Select(item => $"{item.Code}: {item.Message}")),
                    exception);
            }
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
        {
            diagnostics.Add(Error("npc-create-write-failed", exception.Message));
            return Failed();
        }
        catch (Exception exception)
        {
            var rollback = RollbackApply(request, temporaryDirectory, promoted, outputParentAncestry);
            if (!rollback.Complete)
            {
                throw new IOException(
                    "NPC creation failed unexpectedly and rollback was incomplete: " +
                    string.Join(" | ", rollback.Diagnostics.Select(item => $"{item.Code}: {item.Message}")),
                    exception);
            }
            throw;
        }
    }

    public ValueTask<NpcCreationVerificationResult> VerifyAsync(
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request, ValidationMode.Verify).ToBuilder();
        ValidateSpecification(request, diagnostics);
        ValidateProposalBinding(request, proposal, diagnostics, cancellationToken);
        if (HasErrors(diagnostics))
        {
            return ValueTask.FromResult(new NpcCreationVerificationResult(
                false,
                request.Proposal,
                HashIfPresent(request.Proposal, diagnostics),
                request.Output,
                HashIfPresent(request.Output, diagnostics),
                proposal.AllocatedFormId,
                proposal.Masters,
                null,
                null,
                0,
                0,
                false,
                diagnostics.ToImmutable()));
        }

        var verification = BethesdaNpcCreationVerifier.Verify(request, proposal, cancellationToken);
        return ValueTask.FromResult(verification with
        {
            Diagnostics = diagnostics.ToImmutable().AddRange(verification.Diagnostics),
            IsValid = verification.IsValid && !HasErrors(diagnostics)
        });
    }

}
