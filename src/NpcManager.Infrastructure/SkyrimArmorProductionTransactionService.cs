using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class SkyrimArmorProductionTransactionService(
    IArmorProposalService proposalService,
    IArmorBinaryWriteService writer,
    ISkyrimArmorProductionOutputReader outputReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    ISkyrimArmorAddonProductionTransactionService? armorAddonTransactionService = null) :
    ISkyrimArmorProductionTransactionService
{
    public async ValueTask<SkyrimArmorProductionProposal> AnalyzeAsync(
        SkyrimArmorProductionRequest request,
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

        SkyrimArmorProposalAdapterResult adapted = SkyrimArmorEditorRules.ToProposal(
            request.Document,
            request.OutputProposal);
        diagnostics.AddRange(adapted.Diagnostics);
        if (!adapted.Accepted || adapted.Proposal is null)
            return RefusedProposal(request, diagnostics);

        ArmorProposalResult proposed = await proposalService.ProposeAsync(
            adapted.Proposal,
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(proposed.Diagnostics);
        if (!proposed.Written || proposed.Artifact is not { CompleteDocument: true } ||
            proposed.OutputSha256 is null ||
            !File.Exists(request.OutputProposal.Value) ||
            File.Exists(request.OutputPlugin.Value))
        {
            diagnostics.Add(Error("armor-production-analysis",
                "Armor Review did not produce exactly one complete proposal while leaving the output ESP absent."));
            return new(request, proposed.Artifact, null, diagnostics.ToImmutable());
        }

        var addonProposals =
            ImmutableArray.CreateBuilder<SkyrimArmorAddonProductionProposal>();
        foreach (SkyrimArmorAddonProductionRequest addonRequest in
                 Normalize(request.ArmorAddonOutputs))
        {
            if (armorAddonTransactionService is null)
            {
                diagnostics.Add(Error("armor-production-nested-addon-service",
                    "Nested authored ARMA work requires the typed Armor-addon production transaction."));
                break;
            }
            SkyrimArmorAddonProductionProposal addonProposal =
                await armorAddonTransactionService.AnalyzeAsync(
                    addonRequest,
                    cancellationToken).ConfigureAwait(false);
            addonProposals.Add(addonProposal);
            diagnostics.AddRange(addonProposal.Diagnostics);
            if (!addonProposal.IsApplicable) break;
        }
        return new(
            request,
            proposed.Artifact,
            proposed.OutputSha256,
            diagnostics.ToImmutable(),
            addonProposals.ToImmutable());
    }

    public async ValueTask<SkyrimArmorProductionResult> ApplyAsync(
        SkyrimArmorProductionRequest request,
        SkyrimArmorProductionProposal proposal,
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

        var appliedAddons =
            ImmutableArray.CreateBuilder<SkyrimArmorAddonProductionResult>();
        ImmutableArray<SkyrimArmorAddonProductionRequest> addonRequests =
            Normalize(request.ArmorAddonOutputs);
        ImmutableArray<SkyrimArmorAddonProductionProposal> addonProposals =
            Normalize(proposal.ArmorAddonProposals);
        for (int index = 0; index < addonRequests.Length; index++)
        {
            SkyrimArmorAddonProductionResult addonApplied =
                await armorAddonTransactionService!.ApplyAsync(
                    addonRequests[index],
                    addonProposals[index],
                    cancellationToken).ConfigureAwait(false);
            appliedAddons.Add(addonApplied);
            diagnostics.AddRange(addonApplied.Diagnostics);
            if (!addonApplied.Applied ||
                addonApplied.Verification is not { IsValid: true })
            {
                await RollbackAddonOutputsAsync(
                    appliedAddons,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                return new(
                    false,
                    proposal,
                    null,
                    null,
                    diagnostics.ToImmutable());
            }
        }

        ArmorBinaryWriteResult written = await writer.WriteAsync(
            new ArmorBinaryWriteRequest(
                GameEdition.SkyrimSpecialEdition,
                request.OutputProposal,
                request.OutputPlugin),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(written.Diagnostics);
        if (!written.Written || written.OutputSha256 is null)
        {
            await RollbackAddonOutputsAsync(
                appliedAddons,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            return new(false, proposal, written, null, diagnostics.ToImmutable());
        }

        SkyrimArmorProductionVerification verified = await VerifyAsync(
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
                    "armor-production-readback-rollback",
                    DiagnosticSeverity.Warning,
                    "The newly written ARMO failed independent readback and was removed.")
                : Error("armor-production-readback-rollback-refused",
                    "The failed ARMO could not be proven identical to this transaction and was not removed."));
            if (rolledBack)
                await RollbackAddonOutputsAsync(
                    appliedAddons,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
        }
        return new(
            verified.IsValid,
            proposal,
            written,
            verified,
            diagnostics.ToImmutable());
    }

    public async ValueTask<SkyrimArmorProductionVerification> VerifyAsync(
        SkyrimArmorProductionRequest request,
        SkyrimArmorProductionProposal proposal,
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
            diagnostics.Add(Error("armor-production-output-missing",
                "The output ARMO plugin does not exist for explicit verification."));
        if (HasErrors(diagnostics)) return EmptyVerification(request, diagnostics);

        SkyrimArmorProductionVerification readback = await outputReader.ReadAsync(
            proposal.Artifact!,
            request.OutputPlugin,
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(readback.Diagnostics);
        var addonVerifications =
            ImmutableArray.CreateBuilder<SkyrimArmorAddonProductionVerification>();
        ImmutableArray<SkyrimArmorAddonProductionRequest> addonRequests =
            Normalize(request.ArmorAddonOutputs);
        ImmutableArray<SkyrimArmorAddonProductionProposal> addonProposals =
            Normalize(proposal.ArmorAddonProposals);
        for (int index = 0; index < addonRequests.Length; index++)
        {
            SkyrimArmorAddonProductionVerification addonVerification =
                await armorAddonTransactionService!.VerifyAsync(
                    addonRequests[index],
                    addonProposals[index],
                    cancellationToken).ConfigureAwait(false);
            addonVerifications.Add(addonVerification);
            diagnostics.AddRange(addonVerification.Diagnostics);
        }
        return readback with
        {
            IsValid = readback.IsValid &&
                      addonVerifications.All(item => item.IsValid) &&
                      !HasErrors(diagnostics),
            Diagnostics = diagnostics.ToImmutable(),
            ArmorAddonVerifications = addonVerifications.ToImmutable()
        };
    }

    private async ValueTask<ImmutableArray<Diagnostic>> ValidateRequestAsync(
        SkyrimArmorProductionRequest request,
        bool requireFreshProposal,
        bool requireFreshOutput,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Source.Intake.Edition != GameEdition.SkyrimSpecialEdition ||
            request.Document.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("armor-production-edition",
                "The production ARMO transaction supports Skyrim SE/AE only."));
        if (request.Document.SourcePlugin != request.Source.SourcePluginPath ||
            request.Document.SourceFormId != request.Source.SourceReference.FormId)
            diagnostics.Add(Error("armor-production-source-binding",
                "The edited ARMO document is not bound to the reviewed source identity."));
        if (request.Document.Mode == ArmorProposalMode.New &&
            request.Document.TargetFormId != request.Source.NewFromTemplate.TargetFormId)
            diagnostics.Add(Error("armor-production-target-binding",
                "The new ARMO target identity changed after reviewed load."));
        SkyrimArmorEditorResult accepted = SkyrimArmorEditorRules.Save(
            request.Document,
            request.Source.ExistingEditorIds);
        diagnostics.AddRange(accepted.Diagnostics);
        SkyrimArmorProposalAdapterResult adapted = SkyrimArmorEditorRules.ToProposal(
            request.Document,
            request.OutputProposal);
        diagnostics.AddRange(adapted.Diagnostics);
        ImmutableArray<ArmorAddonProposalRequest> expectedAddons =
            adapted.AuthoredArmorAddons.IsDefault
                ? []
                : adapted.AuthoredArmorAddons;
        ImmutableArray<SkyrimArmorAddonProductionRequest> actualAddons =
            Normalize(request.ArmorAddonOutputs);
        if (expectedAddons.Length > 1)
            diagnostics.Add(Error("armor-production-nested-addon-count",
                "One Armor production transaction currently supports at most one authored ARMA output."));
        if (expectedAddons.Length != actualAddons.Length ||
            !expectedAddons.Zip(actualAddons).All(pair =>
                Equals(pair.First, pair.Second.ProposalRequest)))
            diagnostics.Add(Error("armor-production-nested-addon-binding",
                "Every authored ARMA proposal must map exactly once to its reviewed production output."));
        if (actualAddons.Length > 0 && armorAddonTransactionService is null)
            diagnostics.Add(Error("armor-production-nested-addon-service",
                "Nested authored ARMA work requires the typed Armor-addon production transaction."));
        if (actualAddons.Any(item =>
                PathEquals(item.OutputPlugin.Value, request.OutputPlugin.Value)))
            diagnostics.Add(Error("armor-production-nested-output-alias",
                "The child ARMA and parent ARMO must use distinct output plugins."));
        if (actualAddons.Select(item => item.OutputPlugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            actualAddons.Length)
            diagnostics.Add(Error("armor-production-nested-output-duplicate",
                "Each authored ARMA requires a unique fresh output plugin."));

        if (!request.OutputProposal.IsUnder(labRoot) ||
            !request.OutputProposal.Value.EndsWith(
                ".armor-proposal.json",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("armor-production-proposal-path",
                "The proposal must use the shared K-only .armor-proposal.json path."));
        if (requireFreshProposal && File.Exists(request.OutputProposal.Value))
            diagnostics.Add(Error("armor-production-proposal-exists",
                "Armor Review requires a fresh proposal path."));
        if (!request.OutputPlugin.IsUnder(labRoot) ||
            !request.OutputPlugin.Value.EndsWith(
                ".esp",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("armor-production-output-path",
                "The ARMO output must use a K-only ordinary .esp path."));
        if (requireFreshOutput && File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("armor-production-output-exists",
                "The ARMO output must use a fresh path."));
        if (PathEquals(request.OutputProposal.Value, request.OutputPlugin.Value))
            diagnostics.Add(Error("armor-production-output-alias",
                "The proposal JSON and output ESP must use different paths."));
        ValidateParent(request.OutputProposal, diagnostics);
        ValidateParent(request.OutputPlugin, diagnostics);

        string outputName = Path.GetFileName(request.OutputPlugin.Value);
        if (request.Source.PluginOrder.Any(item => string.Equals(
                item.Value,
                outputName,
                StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(Error("armor-production-output-reviewed-collision",
                "The output filename may not collide with a reviewed input plugin."));

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Sha256Hash current = await HashFileAsync(
                request.Source.SourcePluginPath.Value,
                cancellationToken).ConfigureAwait(false);
            if (current != request.Source.SourcePluginSha256)
                diagnostics.Add(Error("armor-production-source-stale",
                    "The reviewed source ARMO plugin changed after intake."));
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            diagnostics.Add(Error("armor-production-source-read",
                exception.Message));
        }
        return diagnostics.ToImmutable();
    }

    private static async ValueTask ValidateBindingAsync(
        SkyrimArmorProductionRequest request,
        SkyrimArmorProductionProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!proposal.IsApplicable || proposal.Artifact is null ||
            proposal.ProposalSha256 is null)
        {
            diagnostics.Add(Error("armor-production-proposal-invalid",
                "Apply requires one accepted complete reviewed ARMO proposal."));
            return;
        }
        if (!RequestsMatch(request, proposal.Request))
            diagnostics.Add(Error("armor-production-request-binding",
                "Apply or Verify request does not exactly match Review."));
        if (!File.Exists(request.OutputProposal.Value))
        {
            diagnostics.Add(Error("armor-production-proposal-missing",
                "The reviewed ARMO proposal file is missing."));
            return;
        }
        Sha256Hash current = await HashFileAsync(
            request.OutputProposal.Value,
            cancellationToken).ConfigureAwait(false);
        if (current != proposal.ProposalSha256.Value)
            diagnostics.Add(Error("armor-production-proposal-hash",
                "The reviewed ARMO proposal bytes changed before Apply or Verify."));
        if (!string.Equals(
                proposal.Artifact.InputSha256,
                request.Source.SourcePluginSha256.Value,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("armor-production-source-hash-binding",
                "The ARMO artifact is not bound to the reviewed source hash."));
        ImmutableArray<SkyrimArmorAddonProductionRequest> addonRequests =
            Normalize(request.ArmorAddonOutputs);
        ImmutableArray<SkyrimArmorAddonProductionProposal> addonProposals =
            Normalize(proposal.ArmorAddonProposals);
        if (addonRequests.Length != addonProposals.Length ||
            !addonRequests.Zip(addonProposals).All(pair =>
                Equals(pair.First, pair.Second.Request)))
            diagnostics.Add(Error("armor-production-nested-review-binding",
                "The reviewed ARMA proposal set no longer matches the parent transaction."));
    }

    private void ValidateParent(
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string? parent = Path.GetDirectoryName(path.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error("armor-production-parent",
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

    private static async ValueTask RollbackAddonOutputsAsync(
        IEnumerable<SkyrimArmorAddonProductionResult> results,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (SkyrimArmorAddonProductionVerification verification in results
                     .Select(item => item.Verification)
                     .Where(item => item is not null)
                     .Cast<SkyrimArmorAddonProductionVerification>()
                     .Reverse())
        {
            if (verification.OutputSha256 is not { } hash) continue;
            bool rolledBack = await TryRollbackAsync(
                verification.OutputPlugin,
                hash,
                cancellationToken).ConfigureAwait(false);
            diagnostics.Add(rolledBack
                ? new Diagnostic(
                    "armor-production-nested-rollback",
                    DiagnosticSeverity.Warning,
                    $"Rolled back transaction-owned ARMA output {verification.OutputPlugin.Value}.")
                : Error("armor-production-nested-rollback-refused",
                    $"Could not prove and roll back ARMA output {verification.OutputPlugin.Value}."));
        }
    }

    private static ImmutableArray<SkyrimArmorAddonProductionRequest> Normalize(
        ImmutableArray<SkyrimArmorAddonProductionRequest> values) =>
        values.IsDefault ? [] : values;

    private static ImmutableArray<SkyrimArmorAddonProductionProposal> Normalize(
        ImmutableArray<SkyrimArmorAddonProductionProposal> values) =>
        values.IsDefault ? [] : values;

    private static bool RequestsMatch(
        SkyrimArmorProductionRequest current,
        SkyrimArmorProductionRequest reviewed) =>
        Equals(current.Source, reviewed.Source) &&
        Equals(current.Document, reviewed.Document) &&
        current.OutputProposal == reviewed.OutputProposal &&
        current.OutputPlugin == reviewed.OutputPlugin &&
        Normalize(current.ArmorAddonOutputs).SequenceEqual(
            Normalize(reviewed.ArmorAddonOutputs));

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

    private static SkyrimArmorProductionProposal RefusedProposal(
        SkyrimArmorProductionRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(request, null, null, diagnostics.ToImmutable());

    private static SkyrimArmorProductionVerification EmptyVerification(
        SkyrimArmorProductionRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) => new(
        false,
        request.OutputPlugin,
        null,
        0,
        0,
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
