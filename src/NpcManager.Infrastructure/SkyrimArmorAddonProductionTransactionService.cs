using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class SkyrimArmorAddonProductionTransactionService(
    IArmorAddonProposalService proposalService,
    IArmorAddonBinaryWriteService writer,
    ISkyrimArmorAddonProductionOutputReader outputReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimArmorAddonProductionTransactionService
{
    public async ValueTask<SkyrimArmorAddonProductionProposal> AnalyzeAsync(
        SkyrimArmorAddonProductionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = (await ValidateRequestAsync(
                request,
                requireFreshProposal: true,
                requireFreshOutput: true,
                cancellationToken).ConfigureAwait(false)).ToBuilder();
        if (HasErrors(diagnostics)) return RefusedProposal(request, diagnostics);

        ArmorAddonProposalResult proposed = await proposalService.ProposeAsync(
            request.ProposalRequest,
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(proposed.Diagnostics);
        if (!proposed.Written ||
            proposed.Artifact is not { CompleteDocument: true } ||
            proposed.OutputSha256 is null ||
            !File.Exists(request.ProposalRequest.OutputProposal.Value) ||
            File.Exists(request.OutputPlugin.Value))
        {
            diagnostics.Add(Error("armor-addon-production-analysis",
                "Armor-addon Review did not produce exactly one complete proposal while leaving the output ESP absent."));
            return new(request, proposed.Artifact, null, diagnostics.ToImmutable());
        }
        return new(
            request,
            proposed.Artifact,
            proposed.OutputSha256,
            diagnostics.ToImmutable());
    }

    public async ValueTask<SkyrimArmorAddonProductionResult> ApplyAsync(
        SkyrimArmorAddonProductionRequest request,
        SkyrimArmorAddonProductionProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = (await ValidateRequestAsync(
                request,
                requireFreshProposal: false,
                requireFreshOutput: true,
                cancellationToken).ConfigureAwait(false)).ToBuilder();
        await ValidateBindingAsync(
            request,
            proposal,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (HasErrors(diagnostics))
            return new(false, proposal, null, null, diagnostics.ToImmutable());

        ArmorAddonBinaryWriteResult written = await writer.WriteAsync(
            new ArmorAddonBinaryWriteRequest(
                GameEdition.SkyrimSpecialEdition,
                request.ProposalRequest.OutputProposal,
                request.OutputPlugin),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(written.Diagnostics);
        if (!written.Written || written.OutputSha256 is null)
            return new(false, proposal, written, null, diagnostics.ToImmutable());

        SkyrimArmorAddonProductionVerification verified = await VerifyAsync(
            request,
            proposal,
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(verified.Diagnostics);
        if (!verified.IsValid)
        {
            bool rolledBack = await TryRollbackAsync(
                request.OutputPlugin,
                written.OutputSha256.Value,
                cancellationToken).ConfigureAwait(false);
            diagnostics.Add(rolledBack
                ? new Diagnostic(
                    "armor-addon-production-readback-rollback",
                    DiagnosticSeverity.Warning,
                    "The newly written ARMA failed independent readback and was removed.")
                : Error("armor-addon-production-readback-rollback-refused",
                    "The failed ARMA could not be proven identical to this transaction and was not removed."));
        }
        return new(
            verified.IsValid,
            proposal,
            written,
            verified,
            diagnostics.ToImmutable());
    }

    public async ValueTask<SkyrimArmorAddonProductionVerification> VerifyAsync(
        SkyrimArmorAddonProductionRequest request,
        SkyrimArmorAddonProductionProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = (await ValidateRequestAsync(
                request,
                requireFreshProposal: false,
                requireFreshOutput: false,
                cancellationToken).ConfigureAwait(false)).ToBuilder();
        await ValidateBindingAsync(
            request,
            proposal,
            diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (!File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("armor-addon-production-output-missing",
                "The output ARMA plugin does not exist for explicit verification."));
        if (HasErrors(diagnostics)) return EmptyVerification(request, diagnostics);

        SkyrimArmorAddonProductionVerification readback =
            await outputReader.ReadAsync(
                proposal.Artifact!,
                request.OutputPlugin,
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(readback.Diagnostics);
        return readback with
        {
            IsValid = readback.IsValid && !HasErrors(diagnostics),
            Diagnostics = diagnostics.ToImmutable()
        };
    }

    public static async ValueTask<bool> TryRollbackVerifiedOutputAsync(
        SkyrimArmorAddonProductionVerification verification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(verification);
        return verification.OutputSha256 is { } hash &&
               await TryRollbackAsync(
                   verification.OutputPlugin,
                   hash,
                   cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ImmutableArray<Diagnostic>> ValidateRequestAsync(
        SkyrimArmorAddonProductionRequest request,
        bool requireFreshProposal,
        bool requireFreshOutput,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ArmorAddonProposalRequest proposal = request.ProposalRequest;
        if (proposal.Edition != GameEdition.SkyrimSpecialEdition ||
            !proposal.CompleteDocument)
            diagnostics.Add(Error("armor-addon-production-contract",
                "The production ARMA transaction requires one complete Skyrim SE/AE proposal."));
        if (!proposal.OutputProposal.IsUnder(labRoot) ||
            !proposal.OutputProposal.Value.EndsWith(
                ".armor-addon-proposal.json",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("armor-addon-production-proposal-path",
                "The proposal must use a K-only .armor-addon-proposal.json path."));
        if (requireFreshProposal && File.Exists(proposal.OutputProposal.Value))
            diagnostics.Add(Error("armor-addon-production-proposal-exists",
                "Armor-addon Review requires a fresh proposal path."));
        if (!request.OutputPlugin.IsUnder(labRoot) ||
            !request.OutputPlugin.Value.EndsWith(
                ".esp",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("armor-addon-production-output-path",
                "The ARMA output must use a K-only ordinary .esp path."));
        if (requireFreshOutput && File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("armor-addon-production-output-exists",
                "The ARMA output must use a fresh path."));
        if (PathEquals(proposal.OutputProposal.Value, request.OutputPlugin.Value))
            diagnostics.Add(Error("armor-addon-production-output-alias",
                "The proposal JSON and output ESP must use different paths."));
        if (string.Equals(
                Path.GetFileName(proposal.SourcePlugin.Value),
                Path.GetFileName(request.OutputPlugin.Value),
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("armor-addon-production-output-source-collision",
                "The fresh ARMA output plugin may not overwrite or impersonate its source plugin."));
        if (proposal.Mode == ArmorAddonProposalMode.New &&
            !string.Equals(
                proposal.TargetPlugin?.Value,
                Path.GetFileName(request.OutputPlugin.Value),
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("armor-addon-production-target-plugin",
                "A new ARMA output filename must exactly match its qualified target plugin."));
        ValidateParent(proposal.OutputProposal, diagnostics);
        ValidateParent(request.OutputPlugin, diagnostics);

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Sha256Hash current = await HashFileAsync(
                proposal.SourcePlugin.Value,
                cancellationToken).ConfigureAwait(false);
            if (current != request.ReviewedSourceSha256)
                diagnostics.Add(Error("armor-addon-production-source-stale",
                    "The reviewed source ARMA plugin changed after intake."));
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            diagnostics.Add(Error("armor-addon-production-source-read",
                exception.Message));
        }
        return diagnostics.ToImmutable();
    }

    private static async ValueTask ValidateBindingAsync(
        SkyrimArmorAddonProductionRequest request,
        SkyrimArmorAddonProductionProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!proposal.IsApplicable || proposal.Artifact is null ||
            proposal.ProposalSha256 is null)
        {
            diagnostics.Add(Error("armor-addon-production-proposal-invalid",
                "Apply requires one accepted complete reviewed ARMA proposal."));
            return;
        }
        if (!Equals(request, proposal.Request))
            diagnostics.Add(Error("armor-addon-production-request-binding",
                "Apply or Verify request does not exactly match Review."));
        if (!File.Exists(request.ProposalRequest.OutputProposal.Value))
        {
            diagnostics.Add(Error("armor-addon-production-proposal-missing",
                "The reviewed ARMA proposal file is missing."));
            return;
        }
        Sha256Hash current = await HashFileAsync(
            request.ProposalRequest.OutputProposal.Value,
            cancellationToken).ConfigureAwait(false);
        if (current != proposal.ProposalSha256.Value)
            diagnostics.Add(Error("armor-addon-production-proposal-hash",
                "The reviewed ARMA proposal bytes changed before Apply or Verify."));
        if (!string.Equals(
                proposal.Artifact.InputSha256,
                request.ReviewedSourceSha256.Value,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("armor-addon-production-source-hash-binding",
                "The ARMA artifact is not bound to the reviewed source hash."));
    }

    private void ValidateParent(
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string? parent = Path.GetDirectoryName(path.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error("armor-addon-production-parent",
                "Every production artifact parent directory must already exist."));
            return;
        }
        diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<bool> TryRollbackAsync(
        WorkspacePath output,
        Sha256Hash expectedHash,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(output.Value) ||
                await HashFileAsync(output.Value, cancellationToken)
                    .ConfigureAwait(false) != expectedHash)
                return false;
            File.Delete(output.Value);
            return !File.Exists(output.Value);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return new Sha256Hash(Convert.ToHexString(hash));
    }

    private static SkyrimArmorAddonProductionProposal RefusedProposal(
        SkyrimArmorAddonProductionRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(request, null, null, diagnostics.ToImmutable());

    private static SkyrimArmorAddonProductionVerification EmptyVerification(
        SkyrimArmorAddonProductionRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) => new(
        false,
        request.OutputPlugin,
        null,
        0,
        0,
        null,
        null,
        null,
        false,
        false,
        false,
        false,
        diagnostics.ToImmutable());

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
