using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimNpcFinishCoreService
{
    public async ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateAnalyzeAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var phases = ImmutableArray.CreateBuilder<SkyrimNpcFinishCoreValidationPhase>();
        phases.Add(Reached("request-binding", []));

        var proposalPathDiagnostics = ValidateProposalPath(proposalPath, projectRoot).ToBuilder();
        if (File.Exists(proposalPath.Value) || Directory.Exists(proposalPath.Value))
            proposalPathDiagnostics.Add(Error(
                "finish-core-proposal-exists",
                "Analyze refuses to overwrite an existing proposal path."));
        phases.Add(Reached("proposal-path", proposalPathDiagnostics));

        SkyrimNpcFinishCoreSourceReadResult? source = await InspectForValidationAsync(
            request, cancellationToken);
        phases.Add(Reached(
            "source-admission",
            source?.Diagnostics ??
            [Error("finish-core-validate-source-exception", "Source inspection did not return a result.")]));

        SkyrimNpcFinishCoreProposal? derived = null;
        if (source?.Admitted == true)
        {
            var proposalDiagnostics = ValidateSourceAndRequest(request, source).ToBuilder();
            if (!HasErrors(proposalDiagnostics))
            {
                SkyrimNpcFinishCoreMasterPlan masterPlan = BuildMasterPlan(request, source!);
                proposalDiagnostics.AddRange(masterPlan.Diagnostics);
                if (masterPlan.Admitted)
                {
                    try
                    {
                        derived = DeriveProposal(
                            request, requestSha256, source, masterPlan);
                    }
                    catch (SkyrimNpcFinishCoreIdOverflowException)
                    {
                        proposalDiagnostics.Add(Error(
                            "finish-core-id-overflow",
                            "The source FormID space cannot allocate the required Finish Core records."));
                    }
                }
            }
            phases.Add(Reached("proposal-derivation", proposalDiagnostics));
        }
        else
        {
            phases.Add(Skipped("proposal-derivation", ErrorCodes(phases)));
        }

        ImmutableArray<Diagnostic> transactionDiagnostics = ValidateTransactionPaths(request);
        phases.Add(Reached("transaction-paths", transactionDiagnostics));

        if (derived is not null && derived.Status == SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite &&
            !HasErrors(AllDiagnostics(phases)))
        {
            phases.Add(Reached(
                "disposable-write",
                await RunDisposableValidationAsync(request, source!, cancellationToken)));
        }
        else if (derived?.Status == SkyrimNpcFinishCoreStatus.NoChanges &&
                 !HasErrors(AllDiagnostics(phases)))
        {
            phases.Add(Skipped("disposable-write", ["finish-core-no-changes"]));
        }
        else
        {
            phases.Add(Skipped("disposable-write", ErrorCodes(phases)));
        }

        return BuildValidationResult(phases);
    }

    public async ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateApplyAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        SkyrimNpcFinishCoreProposal proposal,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var phases = ImmutableArray.CreateBuilder<SkyrimNpcFinishCoreValidationPhase>();
        phases.Add(Reached("request-binding", []));

        var binding = ImmutableArray.CreateBuilder<Diagnostic>();
        if (proposal.Request is null || proposal.RequestSha256 != requestSha256 ||
            proposalSha256 != HashProposal(proposal))
            binding.Add(Error(
                "finish-core-apply-proposal-hash",
                "The supplied proposal is not bound to the request and canonical proposal hash."));
        if (proposal.Status is not (
                SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite or
                SkyrimNpcFinishCoreStatus.NoChanges))
            binding.Add(Error(
                "finish-core-apply-status",
                "Only ReadyForReviewedWrite or NoChanges proposals may be validated."));
        phases.Add(Reached("proposal-binding", binding));

        SkyrimNpcFinishCoreSourceReadResult? source = await InspectForValidationAsync(
            request, cancellationToken);
        phases.Add(Reached(
            "source-admission",
            source?.Diagnostics ??
            [Error("finish-core-validate-source-exception", "Source inspection did not return a result.")]));

        SkyrimNpcFinishCoreProposal? expected = null;
        if (source?.Admitted == true)
        {
            var derivation = ValidateSourceAndRequest(request, source).ToBuilder();
            if (!HasErrors(derivation))
            {
                SkyrimNpcFinishCoreMasterPlan masterPlan = BuildMasterPlan(request, source);
                derivation.AddRange(masterPlan.Diagnostics);
                if (masterPlan.Admitted)
                {
                    try
                    {
                        expected = DeriveProposal(
                            request, requestSha256, source, masterPlan);
                        byte[] expectedBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                            expected with { ProposalSha256 = null }, projectRoot);
                        byte[] actualBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                            proposal with { ProposalSha256 = null }, projectRoot);
                        if (!expectedBytes.AsSpan().SequenceEqual(actualBytes))
                            derivation.Add(Error(
                                "finish-core-apply-stale-proposal",
                                "The proposal no longer matches a fresh source admission."));
                    }
                    catch (SkyrimNpcFinishCoreIdOverflowException)
                    {
                        derivation.Add(Error(
                            "finish-core-id-overflow",
                            "The source FormID space cannot allocate the required Finish Core records."));
                    }
                }
            }
            phases.Add(Reached("proposal-derivation", derivation));
        }
        else
        {
            phases.Add(Skipped("proposal-derivation", ErrorCodes(phases)));
        }

        phases.Add(Reached("transaction-paths", ValidateTransactionPaths(request)));
        if (expected is not null &&
            expected.Status == SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite &&
            !HasErrors(AllDiagnostics(phases)))
        {
            phases.Add(Reached(
                "disposable-write",
                await RunDisposableValidationAsync(request, source!, cancellationToken)));
        }
        else if (expected?.Status == SkyrimNpcFinishCoreStatus.NoChanges &&
                 !HasErrors(AllDiagnostics(phases)))
        {
            phases.Add(Skipped("disposable-write", ["finish-core-no-changes"]));
        }
        else
        {
            phases.Add(Skipped("disposable-write", ErrorCodes(phases)));
        }

        return BuildValidationResult(phases);
    }

    private async ValueTask<SkyrimNpcFinishCoreSourceReadResult?> InspectForValidationAsync(
        SkyrimNpcFinishCoreRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await inspectSource(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return new SkyrimNpcFinishCoreSourceReadResult(
                false,
                null,
                new Sha256Hash(new string('0', 64)),
                new Sha256Hash(new string('0', 64)),
                new FormReference(new PluginName("Invalid.esp"), new FormId(0)),
                new EditorId("Invalid"),
                false,
                null,
                ImmutableDictionary<string, int>.Empty,
                ImmutableDictionary<string, int>.Empty,
                [Error("finish-core-validate-source-exception", exception.Message)]);
        }
    }

    private async ValueTask<ImmutableArray<Diagnostic>> RunDisposableValidationAsync(
        SkyrimNpcFinishCoreRequest request,
        SkyrimNpcFinishCoreSourceReadResult source,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        string validationRoot = Path.Combine(
            projectRoot.Value,
            ".finish-core-validation-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(validationRoot);
            SkyrimNpcFinishCoreRequest disposableRequest = request with
            {
                Output = request.Output with
                {
                    Root = new WorkspacePath(Path.Combine(validationRoot, "output")),
                    Archive = new WorkspacePath(Path.Combine(validationRoot, "output.zip"))
                }
            };
            Sha256Hash disposableRequestHash = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                disposableRequest, projectRoot);
            SkyrimNpcFinishCoreMasterPlan masterPlan = BuildMasterPlan(
                disposableRequest, source);
            diagnostics.AddRange(masterPlan.Diagnostics);
            if (masterPlan.Admitted)
            {
                SkyrimNpcFinishCoreProposal disposableProposal = DeriveProposal(
                    disposableRequest, disposableRequestHash, source, masterPlan);
                Sha256Hash disposableProposalHash = HashProposal(disposableProposal);
                disposableProposal = disposableProposal with
                {
                    ProposalSha256 = disposableProposalHash
                };
                SkyrimNpcFinishCoreApplyResult result = await ApplyTransactionAsync(
                    disposableRequest,
                    disposableRequestHash,
                    disposableProposal,
                    disposableProposalHash,
                    cancellationToken);
                diagnostics.AddRange(result.Diagnostics);
                if (!result.Applied)
                    diagnostics.Add(Error(
                        "finish-core-validate-disposable-write",
                        "Disposable writer, verifier, package hash, and archive readback did not complete."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(Error(
                "finish-core-validate-disposable-exception", exception.Message));
        }
        finally
        {
            try
            {
                if (Directory.Exists(validationRoot))
                    Directory.Delete(validationRoot, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error(
                    "finish-core-validate-cleanup", exception.Message));
            }
        }
        return diagnostics.ToImmutable();
    }

    private static SkyrimNpcFinishCoreValidationResult BuildValidationResult(
        ImmutableArray<SkyrimNpcFinishCoreValidationPhase>.Builder phases)
    {
        ImmutableArray<SkyrimNpcFinishCoreValidationPhase> values = phases.ToImmutable();
        ImmutableArray<Diagnostic> diagnostics = AllDiagnostics(values);
        return new SkyrimNpcFinishCoreValidationResult(
            !HasErrors(diagnostics), values, diagnostics);
    }

    private static SkyrimNpcFinishCoreValidationPhase Reached(
        string name,
        IEnumerable<Diagnostic> diagnostics) =>
        new(name, SkyrimNpcFinishCoreValidationPhaseState.Reached,
            diagnostics.ToImmutableArray());

    private static SkyrimNpcFinishCoreValidationPhase Skipped(
        string name,
        IEnumerable<string> prerequisiteCodes) =>
        new(
            name,
            SkyrimNpcFinishCoreValidationPhaseState.Skipped,
            [new Diagnostic(
                "finish-core-validate-phase-skipped",
                DiagnosticSeverity.Info,
                $"Phase '{name}' was not checked because prerequisites failed: " +
                string.Join(",", prerequisiteCodes.Distinct(StringComparer.Ordinal))) ]);

    private static ImmutableArray<Diagnostic> AllDiagnostics(
        IEnumerable<SkyrimNpcFinishCoreValidationPhase> phases) =>
        phases.SelectMany(phase => phase.Diagnostics).ToImmutableArray();

    private static ImmutableArray<string> ErrorCodes(
        IEnumerable<SkyrimNpcFinishCoreValidationPhase> phases) =>
        phases.SelectMany(phase => phase.Diagnostics)
            .Where(item => item.Severity == DiagnosticSeverity.Error)
            .Select(item => item.Code)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
}
