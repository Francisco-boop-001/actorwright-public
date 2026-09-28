using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Owns the production two-pass OTFT transaction: proposal-only analysis,
/// exact proposal/request binding, fresh binary write, and a separate reopen.
/// </summary>
public sealed class SkyrimOutfitProductionTransactionService(
    IOutfitProposalService proposalService,
    IOutfitBinaryWriteService writer,
    IPluginReader pluginReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimOutfitProductionTransactionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<SkyrimOutfitProductionProposal> AnalyzeAsync(
        SkyrimOutfitProductionTransactionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, requireFreshOutput: true).ToBuilder();
        if (HasErrors(diagnostics)) return new(request, null, null,
            diagnostics.ToImmutable());

        OutfitProposalResult proposed = await proposalService.ProposeAsync(
            request.OutfitProposal,
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(proposed.Diagnostics);
        if (!proposed.Written || proposed.Artifact is null ||
            proposed.OutputSha256 is null)
            return new(request, proposed.Artifact, proposed.OutputSha256,
                diagnostics.ToImmutable());
        if (File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("outfit-production-analysis-wrote-plugin",
                "Proposal review unexpectedly materialized the output plugin."));
        return new(request, proposed.Artifact, proposed.OutputSha256,
            diagnostics.ToImmutable());
    }

    public async ValueTask<SkyrimOutfitProductionResult> ApplyAsync(
        SkyrimOutfitProductionTransactionRequest request,
        SkyrimOutfitProductionProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, requireFreshOutput: true).ToBuilder();
        await ValidateBindingAsync(request, proposal, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        if (HasErrors(diagnostics)) return new(false, proposal, null, null,
            diagnostics.ToImmutable());

        OutfitBinaryWriteResult written = await writer.WriteAsync(
            new OutfitBinaryWriteRequest(
                GameEdition.SkyrimSpecialEdition,
                request.OutfitProposal.OutputProposal,
                request.OutputPlugin),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(written.Diagnostics);
        if (!written.Written || written.OutputSha256 is null)
            return new(false, proposal, written, null, diagnostics.ToImmutable());

        SkyrimOutfitProductionVerification verified = await VerifyAsync(
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
                ? new Diagnostic("outfit-production-readback-rollback",
                    DiagnosticSeverity.Warning,
                    "The newly written output failed independent readback and was removed.")
                : Error("outfit-production-readback-rollback-refused",
                    "The failed output could not be proven identical to this transaction and was not removed."));
        }
        return new(verified.IsValid, proposal, written, verified,
            diagnostics.ToImmutable());
    }

    public async ValueTask<SkyrimOutfitProductionVerification> VerifyAsync(
        SkyrimOutfitProductionTransactionRequest request,
        SkyrimOutfitProductionProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, requireFreshOutput: false).ToBuilder();
        await ValidateBindingAsync(request, proposal, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        if (!File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("outfit-production-output-missing",
                "The output plugin does not exist for independent verification."));
        if (HasErrors(diagnostics)) return EmptyVerification(request, diagnostics);

        PluginInspection inspection;
        Sha256Hash outputHash;
        try
        {
            inspection = await pluginReader.ReadAsync(
                new PluginReadRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.OutputPlugin),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(inspection.Diagnostics);
            outputHash = await HashFileAsync(request.OutputPlugin.Value,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException)
        {
            diagnostics.Add(Error("outfit-production-output-read-failed",
                exception.Message));
            return EmptyVerification(request, diagnostics);
        }

        OutfitProposalArtifact artifact = proposal.Artifact!;
        PluginRecordSummary? record = inspection.Records.Count(record =>
            string.Equals(record.Signature, "OTFT", StringComparison.Ordinal)) == 1
            ? inspection.Records.Single(record =>
                string.Equals(record.Signature, "OTFT", StringComparison.Ordinal))
            : null;
        if (inspection.Records.Length != 1 || record is null)
            diagnostics.Add(Error("outfit-production-record-surface",
                "Independent readback requires exactly one OTFT and zero unrelated records."));

        PluginName expectedOwner = artifact.Mode == OutfitProposalMode.New
            ? new PluginName(Path.GetFileName(request.OutputPlugin.Value))
            : new PluginName(artifact.SourceOwnerPlugin ??
                Path.GetFileName(request.OutfitProposal.SourcePlugin.Value));
        FormId expectedTarget = artifact.Mode == OutfitProposalMode.New
            ? request.OutfitProposal.TargetFormId!.Value
            : request.OutfitProposal.SourceFormId;
        bool ownerMatches = record?.OwnerPlugin is { } owner &&
            SamePlugin(owner, expectedOwner);
        bool targetMatches = record?.FormId == expectedTarget;
        bool editorIdMatches = record is not null && string.Equals(
            record.EditorId,
            artifact.EditorId,
            StringComparison.Ordinal);
        bool orderedItemsMatch = record is not null && SameItems(
            record.OutfitItemReferences,
            request.OutfitProposal.Items);
        bool masterSetMatches = SamePluginSet(
            inspection.Masters,
            ExpectedMasters(request, artifact));

        if (!ownerMatches || !targetMatches)
            diagnostics.Add(Error("outfit-production-identity-mismatch",
                "Independent readback did not preserve the expected OTFT owner and FormID."));
        if (!editorIdMatches)
            diagnostics.Add(Error("outfit-production-editor-id-mismatch",
                "Independent readback did not preserve the expected OTFT EditorID."));
        if (!orderedItemsMatch)
            diagnostics.Add(Error("outfit-production-items-mismatch",
                "Independent readback did not preserve the ordered ARMO/LVLI links."));
        if (!masterSetMatches)
            diagnostics.Add(Error("outfit-production-masters-mismatch",
                "Independent readback did not preserve the exact required master set."));

        bool valid = inspection.Records.Length == 1 && record is not null &&
                     ownerMatches && targetMatches && editorIdMatches &&
                     orderedItemsMatch && masterSetMatches && !HasErrors(diagnostics);
        return new(
            valid,
            request.OutputPlugin,
            outputHash,
            inspection.Records.Length,
            record?.OwnerPlugin,
            record?.FormId,
            ownerMatches && targetMatches,
            editorIdMatches,
            orderedItemsMatch,
            masterSetMatches,
            diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidateRequest(
        SkyrimOutfitProductionTransactionRequest request,
        bool requireFreshOutput)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.OutfitProposal.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("outfit-production-edition",
                "The production outfit transaction supports Skyrim SE/AE only."));
        if (!request.OutputPlugin.IsUnder(labRoot))
            diagnostics.Add(Error("outfit-production-output-outside-lab",
                "The output plugin must remain under the K-only lab root."));
        if (!request.OutputPlugin.Value.EndsWith(".esp",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("outfit-production-output-extension",
                "The bounded outfit transaction emits an ordinary .esp plugin."));
        if (requireFreshOutput && File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("outfit-production-output-exists",
                "The output plugin must be a fresh path."));
        string? parent = Path.GetDirectoryName(request.OutputPlugin.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(Error("outfit-production-output-parent",
                "The output plugin directory must already exist."));
        else
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (PathEquals(request.OutputPlugin.Value,
                request.OutfitProposal.OutputProposal.Value))
            diagnostics.Add(Error("outfit-production-output-alias",
                "The JSON proposal and binary plugin must use different paths."));
        return diagnostics.ToImmutable();
    }

    private static async ValueTask ValidateBindingAsync(
        SkyrimOutfitProductionTransactionRequest request,
        SkyrimOutfitProductionProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!proposal.IsApplicable || proposal.Artifact is null ||
            proposal.ProposalSha256 is null)
        {
            diagnostics.Add(Error("outfit-production-proposal-not-applicable",
                "The production proposal is missing or contains blocking diagnostics."));
            return;
        }
        if (!SameRequest(request, proposal.Request))
            diagnostics.Add(Error("outfit-production-request-binding",
                "The apply request does not exactly match the analyzed request."));
        string path = request.OutfitProposal.OutputProposal.Value;
        if (!File.Exists(path))
        {
            diagnostics.Add(Error("outfit-production-proposal-missing",
                "The analyzed outfit proposal no longer exists."));
            return;
        }
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (hash != proposal.ProposalSha256.Value)
                diagnostics.Add(Error("outfit-production-proposal-hash",
                    "The outfit proposal changed after analysis."));
            OutfitProposalArtifact? persisted = JsonSerializer.Deserialize<
                OutfitProposalArtifact>(bytes, JsonOptions);
            if (persisted is null || !SameArtifact(persisted, proposal.Artifact))
                diagnostics.Add(Error("outfit-production-proposal-content",
                    "The persisted outfit proposal does not match the analyzed artifact."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           JsonException or
                                           ArgumentException)
        {
            diagnostics.Add(Error("outfit-production-proposal-read",
                exception.Message));
        }
    }

    private static ImmutableArray<PluginName> ExpectedMasters(
        SkyrimOutfitProductionTransactionRequest request,
        OutfitProposalArtifact artifact)
    {
        string output = Path.GetFileName(request.OutputPlugin.Value);
        return new[]
            {
                new PluginName(Path.GetFileName(
                    request.OutfitProposal.SourcePlugin.Value)),
                new PluginName(artifact.SourceOwnerPlugin ??
                    Path.GetFileName(request.OutfitProposal.SourcePlugin.Value))
            }
            .Concat(request.OutfitProposal.Items.Select(item => item.Plugin))
            .Where(item => !string.Equals(item.Value, output,
                StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static bool SameRequest(
        SkyrimOutfitProductionTransactionRequest left,
        SkyrimOutfitProductionTransactionRequest right) =>
        PathEquals(left.OutputPlugin.Value, right.OutputPlugin.Value) &&
        SameProposalRequest(left.OutfitProposal, right.OutfitProposal);

    private static bool SameProposalRequest(
        OutfitProposalRequest left,
        OutfitProposalRequest right) =>
        left.Edition == right.Edition &&
        PathEquals(left.SourcePlugin.Value, right.SourcePlugin.Value) &&
        left.SourceFormId == right.SourceFormId &&
        left.Mode == right.Mode &&
        left.EditorId == right.EditorId &&
        left.TargetFormId == right.TargetFormId &&
        PathEquals(left.OutputProposal.Value, right.OutputProposal.Value) &&
        SameItems(left.Items, right.Items);

    private static bool SameArtifact(
        OutfitProposalArtifact left,
        OutfitProposalArtifact right) =>
        string.Equals(left.SchemaVersion, right.SchemaVersion,
            StringComparison.Ordinal) &&
        string.Equals(left.ArtifactKind, right.ArtifactKind,
            StringComparison.Ordinal) &&
        string.Equals(left.Edition, right.Edition,
            StringComparison.Ordinal) &&
        left.Mode == right.Mode &&
        PathEquals(left.SourcePlugin, right.SourcePlugin) &&
        string.Equals(left.SourceFormId, right.SourceFormId,
            StringComparison.Ordinal) &&
        string.Equals(left.EditorId, right.EditorId,
            StringComparison.Ordinal) &&
        string.Equals(left.InputSha256, right.InputSha256,
            StringComparison.OrdinalIgnoreCase) &&
        left.Items.SequenceEqual(right.Items, StringComparer.OrdinalIgnoreCase) &&
        left.MasterDependencies.SequenceEqual(right.MasterDependencies,
            StringComparer.OrdinalIgnoreCase) &&
        left.NoUnrelatedRecords == right.NoUnrelatedRecords &&
        string.Equals(left.TargetFormId, right.TargetFormId,
            StringComparison.Ordinal) &&
        string.Equals(left.SourceOwnerPlugin, right.SourceOwnerPlugin,
            StringComparison.OrdinalIgnoreCase);

    private static bool SameItems(
        ImmutableArray<FormReference> left,
        ImmutableArray<FormReference> right) =>
        !left.IsDefault && !right.IsDefault && left.Length == right.Length &&
        left.Zip(right).All(pair =>
            pair.First.FormId == pair.Second.FormId &&
            SamePlugin(pair.First.Plugin, pair.Second.Plugin));

    private static bool SamePluginSet(
        ImmutableArray<PluginName> left,
        ImmutableArray<PluginName> right) =>
        left.Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(right.Select(item => item.Value)) &&
        left.Select(item => item.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() == left.Length;

    private static bool SamePlugin(PluginName left, PluginName right) =>
        string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static async ValueTask<bool> TryRollbackAsync(
        WorkspacePath output,
        Sha256Hash expected,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(output.Value)) return true;
            Sha256Hash current = await HashFileAsync(output.Value,
                cancellationToken).ConfigureAwait(false);
            if (current != expected) return false;
            File.Delete(output.Value);
            return !File.Exists(output.Value);
        }
        catch (OperationCanceledException) { throw; }
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
        await using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        return new Sha256Hash(Convert.ToHexString(hash));
    }

    private static SkyrimOutfitProductionVerification EmptyVerification(
        SkyrimOutfitProductionTransactionRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) => new(
        false,
        request.OutputPlugin,
        null,
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
