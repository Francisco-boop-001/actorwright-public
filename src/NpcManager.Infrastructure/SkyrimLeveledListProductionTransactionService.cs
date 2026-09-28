using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class SkyrimLeveledListProductionTransactionService(
    ISkyrimLeveledListProductionBinaryWriter writer,
    ISkyrimLeveledListProductionOutputReader outputReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimLeveledListProductionTransactionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<SkyrimLeveledListProductionProposal> AnalyzeAsync(
        SkyrimLeveledListProductionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = (await ValidateRequestAsync(
                request,
                requireFreshProposal: true,
                requireFreshOutput: true,
                cancellationToken).ConfigureAwait(false)).ToBuilder();
        if (HasErrors(diagnostics))
            return new(request, null, null, diagnostics.ToImmutable());

        SkyrimLeveledListProductionArtifact artifact = BuildArtifact(request);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        string temporary = request.OutputProposal.Value + ".tmp-" +
                           Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, request.OutputProposal.Value, overwrite: false);
            if (File.Exists(request.OutputPlugin.Value))
                diagnostics.Add(Error("leveled-list-production-analysis-wrote-plugin",
                    "Proposal review unexpectedly materialized the output plugin."));
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            return new(request, artifact, hash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporary);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            TryDelete(temporary);
            diagnostics.Add(Error("leveled-list-production-proposal-write",
                exception.Message));
            return new(request, artifact, null, diagnostics.ToImmutable());
        }
    }

    public async ValueTask<SkyrimLeveledListProductionResult> ApplyAsync(
        SkyrimLeveledListProductionRequest request,
        SkyrimLeveledListProductionProposal proposal,
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
        await ValidateBindingAsync(request, proposal, diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (HasErrors(diagnostics))
            return new(false, proposal, null, null, diagnostics.ToImmutable());

        SkyrimLeveledListProductionWriteResult written = await writer.WriteAsync(
            proposal.Artifact!,
            request.OutputPlugin,
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(written.Diagnostics);
        if (!written.Written || written.OutputSha256 is null)
            return new(false, proposal, written, null, diagnostics.ToImmutable());

        SkyrimLeveledListProductionVerification verified = await VerifyAsync(
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
                ? new Diagnostic("leveled-list-production-readback-rollback",
                    DiagnosticSeverity.Warning,
                    "The newly written LVLI failed readback and was removed.")
                : Error("leveled-list-production-readback-rollback-refused",
                    "The failed LVLI could not be proven identical to this transaction and was not removed."));
        }
        return new(verified.IsValid, proposal, written, verified,
            diagnostics.ToImmutable());
    }

    public async ValueTask<SkyrimLeveledListProductionVerification> VerifyAsync(
        SkyrimLeveledListProductionRequest request,
        SkyrimLeveledListProductionProposal proposal,
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
        await ValidateBindingAsync(request, proposal, diagnostics,
            cancellationToken).ConfigureAwait(false);
        if (!File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("leveled-list-production-output-missing",
                "The output plugin does not exist for independent verification."));
        if (HasErrors(diagnostics)) return EmptyVerification(request, diagnostics);

        SkyrimLeveledListProductionVerification readback =
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

    private async ValueTask<ImmutableArray<Diagnostic>> ValidateRequestAsync(
        SkyrimLeveledListProductionRequest request,
        bool requireFreshProposal,
        bool requireFreshOutput,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.State.Intake.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("leveled-list-production-edition",
                "The new LVLI transaction supports Skyrim SE/AE only."));
        if (request.TargetFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("leveled-list-production-target",
                "The new LVLI target must use a nonzero plugin-local 24-bit FormID."));
        diagnostics.AddRange(SkyrimLeveledListEditorRules.ValidateDocument(
            request.Document));
        if (request.Document.Entries.IsDefaultOrEmpty)
            diagnostics.Add(Error("leveled-list-production-entries-empty",
                "A production LVLI requires at least one ordered LVLO row."));
        if (request.Document.MaxCount != 0)
            diagnostics.Add(Error("leveled-list-production-max-count",
                "Skyrim production writes require Max Count zero; the value cannot be silently dropped."));
        if (request.State.ExistingEditorIds.Any(item => string.Equals(
                item.Value,
                request.Document.EditorId.Value,
                StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(Error("leveled-list-production-editor-id-duplicate",
                $"EditorID '{request.Document.EditorId.Value}' already exists in the reviewed closure."));

        if (!request.OutputProposal.IsUnder(labRoot) ||
            !request.OutputProposal.Value.EndsWith(
                ".leveled-list-production-proposal.json",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("leveled-list-production-proposal-path",
                "The proposal must use a K-only .leveled-list-production-proposal.json path."));
        if (requireFreshProposal && File.Exists(request.OutputProposal.Value))
            diagnostics.Add(Error("leveled-list-production-proposal-exists",
                "Proposal review requires a fresh JSON path."));
        if (!request.OutputPlugin.IsUnder(labRoot) ||
            !request.OutputPlugin.Value.EndsWith(".esp",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("leveled-list-production-output-path",
                "The output must use a K-only ordinary .esp path."));
        if (requireFreshOutput && File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("leveled-list-production-output-exists",
                "The output plugin must be a fresh path."));
        if (PathEquals(request.OutputProposal.Value, request.OutputPlugin.Value))
            diagnostics.Add(Error("leveled-list-production-output-alias",
                "The JSON proposal and binary plugin must use different paths."));
        ValidateParent(request.OutputProposal, policy, labRoot, diagnostics);
        ValidateParent(request.OutputPlugin, policy, labRoot, diagnostics);

        string outputName = Path.GetFileName(request.OutputPlugin.Value);
        if (request.State.PluginOrder.Any(item => string.Equals(
                item.Value, outputName, StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(Error("leveled-list-production-output-reviewed-collision",
                "The new output filename may not collide with a reviewed input plugin."));

        var candidates = request.State.EntryCandidates.ToDictionary(
            item => item.Reference.ToString(),
            StringComparer.OrdinalIgnoreCase);
        foreach (LeveledListEntryProposal entry in request.Document.Entries)
        {
            if (!candidates.TryGetValue(entry.Item.ToString(), out
                    SkyrimOutfitEditorItem? candidate))
            {
                diagnostics.Add(Error("leveled-list-production-entry-unreviewed",
                    $"Entry '{entry.Item}' is not in the reviewed typed catalog."));
                continue;
            }
            if (candidate.Kind == SkyrimOutfitEditorItemKind.LeveledList)
                diagnostics.Add(Error("leveled-list-production-nested-adjacency",
                    "Nested LVLI production requires complete adjacency evidence; select a reviewed ARMO for this bounded transaction."));
        }

        foreach (PluginClosureReviewEntry input in request.State.Intake.Plugins
                     .Where(item => item.Enabled && item.Exists &&
                                    item.ReadSucceeded && item.SourceHash is not null)
                     .OrderBy(item => item.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Sha256Hash current = await HashFileAsync(input.Path.Value,
                    cancellationToken).ConfigureAwait(false);
                if (input.SourceHash is null || current != input.SourceHash.Value)
                    diagnostics.Add(Error("leveled-list-production-plugin-stale",
                        $"Reviewed plugin '{input.Plugin}' changed after intake."));
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException)
            {
                diagnostics.Add(Error("leveled-list-production-plugin-read",
                    $"Reviewed plugin '{input.Plugin}' could not be hashed: {exception.Message}"));
            }
        }
        return diagnostics.ToImmutable();
    }

    private static SkyrimLeveledListProductionArtifact BuildArtifact(
        SkyrimLeveledListProductionRequest request)
    {
        ImmutableArray<PluginName> masters = ExpectedMasters(request);
        ImmutableArray<SkyrimLeveledListReviewedInputArtifact> inputs =
            request.State.Intake.Plugins
                .Where(item => item.Enabled && item.Exists &&
                               item.ReadSucceeded && item.SourceHash is not null)
                .OrderBy(item => item.Order)
                .Select(item => new SkyrimLeveledListReviewedInputArtifact(
                    item.Plugin.Value,
                    item.Path.Value,
                    item.SourceHash!.Value.Value))
                .ToImmutableArray();
        return new(
            "1",
            "skyrim-new-leveled-list-production-proposal",
            "skyrimse",
            Path.GetFileName(request.OutputPlugin.Value),
            request.TargetFormId.ToString(),
            request.Document.EditorId.Value,
            request.Document.ChanceNone,
            request.Document.MaxCount,
            request.Document.CalculateAllLevels,
            request.Document.CalculateEachInCount,
            request.Document.UseAll,
            request.Document.Entries.Select(item =>
                    new SkyrimLeveledListProductionEntryArtifact(
                        item.Item.ToString(),
                        item.Level,
                        item.Count,
                        item.ChanceNone))
                .ToImmutableArray(),
            masters.Select(item => item.Value).ToImmutableArray(),
            inputs,
            true,
            true);
    }

    private static ImmutableArray<PluginName> ExpectedMasters(
        SkyrimLeveledListProductionRequest request)
    {
        var required = request.Document.Entries
            .Select(item => item.Item.Plugin.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (PluginClosureReviewEntry entry in request.State.Intake.Plugins)
            {
                if (!required.Contains(entry.Plugin.Value)) continue;
                foreach (PluginName master in entry.Masters)
                    changed |= required.Add(master.Value);
            }
        } while (changed);
        return request.State.PluginOrder
            .Where(item => required.Contains(item.Value))
            .ToImmutableArray();
    }

    private static async ValueTask ValidateBindingAsync(
        SkyrimLeveledListProductionRequest request,
        SkyrimLeveledListProductionProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!proposal.IsApplicable || proposal.Artifact is null ||
            proposal.ProposalSha256 is null)
        {
            diagnostics.Add(Error("leveled-list-production-proposal-not-applicable",
                "The production proposal is missing or contains blocking diagnostics."));
            return;
        }
        if (!SameRequest(request, proposal.Request))
            diagnostics.Add(Error("leveled-list-production-request-binding",
                "The apply request does not exactly match the analyzed request."));
        if (!File.Exists(request.OutputProposal.Value))
        {
            diagnostics.Add(Error("leveled-list-production-proposal-missing",
                "The analyzed proposal no longer exists."));
            return;
        }
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(
                request.OutputProposal.Value,
                cancellationToken).ConfigureAwait(false);
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (hash != proposal.ProposalSha256.Value)
                diagnostics.Add(Error("leveled-list-production-proposal-hash",
                    "The proposal changed after analysis."));
            SkyrimLeveledListProductionArtifact? persisted =
                JsonSerializer.Deserialize<SkyrimLeveledListProductionArtifact>(
                    bytes,
                    JsonOptions);
            if (persisted is null || !SameArtifact(persisted, proposal.Artifact))
                diagnostics.Add(Error("leveled-list-production-proposal-content",
                    "The persisted proposal does not match the analyzed artifact."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           JsonException or
                                           ArgumentException)
        {
            diagnostics.Add(Error("leveled-list-production-proposal-read",
                exception.Message));
        }
    }

    private static bool SameRequest(
        SkyrimLeveledListProductionRequest left,
        SkyrimLeveledListProductionRequest right) =>
        left.TargetFormId == right.TargetFormId &&
        PathEquals(left.OutputProposal.Value, right.OutputProposal.Value) &&
        PathEquals(left.OutputPlugin.Value, right.OutputPlugin.Value) &&
        SameDocument(left.Document, right.Document) &&
        SameState(left.State, right.State);

    private static bool SameState(
        SkyrimLeveledListProductionState left,
        SkyrimLeveledListProductionState right) =>
        left.PluginOrder.SequenceEqual(right.PluginOrder) &&
        left.ExistingEditorIds.SequenceEqual(right.ExistingEditorIds) &&
        left.EntryCandidates.Length == right.EntryCandidates.Length &&
        left.EntryCandidates.Zip(right.EntryCandidates).All(pair =>
            pair.First.Reference == pair.Second.Reference &&
            pair.First.Kind == pair.Second.Kind) &&
        SameReviewedInputs(left.Intake, right.Intake);

    private static bool SameReviewedInputs(
        ReviewedGameIntake left,
        ReviewedGameIntake right)
    {
        PluginClosureReviewEntry[] leftInputs = left.Plugins
            .OrderBy(item => item.Order).ToArray();
        PluginClosureReviewEntry[] rightInputs = right.Plugins
            .OrderBy(item => item.Order).ToArray();
        return left.Edition == right.Edition &&
               PathEquals(left.DataRoot.Value, right.DataRoot.Value) &&
               leftInputs.Length == rightInputs.Length &&
               leftInputs.Zip(rightInputs).All(pair =>
                   pair.First.Plugin == pair.Second.Plugin &&
                   pair.First.Order == pair.Second.Order &&
                   PathEquals(pair.First.Path.Value, pair.Second.Path.Value) &&
                   pair.First.SourceHash == pair.Second.SourceHash &&
                   pair.First.Masters.SequenceEqual(pair.Second.Masters));
    }

    private static bool SameDocument(
        SkyrimLeveledListEditorDocument left,
        SkyrimLeveledListEditorDocument right) =>
        string.Equals(left.NameSuffix, right.NameSuffix,
            StringComparison.Ordinal) &&
        left.EditorId == right.EditorId &&
        left.ChanceNone == right.ChanceNone &&
        left.MaxCount == right.MaxCount &&
        left.CalculateAllLevels == right.CalculateAllLevels &&
        left.CalculateEachInCount == right.CalculateEachInCount &&
        left.UseAll == right.UseAll &&
        left.Entries.SequenceEqual(right.Entries);

    private static bool SameArtifact(
        SkyrimLeveledListProductionArtifact left,
        SkyrimLeveledListProductionArtifact right) =>
        string.Equals(left.SchemaVersion, right.SchemaVersion,
            StringComparison.Ordinal) &&
        string.Equals(left.ArtifactKind, right.ArtifactKind,
            StringComparison.Ordinal) &&
        string.Equals(left.Edition, right.Edition,
            StringComparison.Ordinal) &&
        string.Equals(left.OutputPlugin, right.OutputPlugin,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.TargetFormId, right.TargetFormId,
            StringComparison.Ordinal) &&
        string.Equals(left.EditorId, right.EditorId,
            StringComparison.Ordinal) &&
        left.ChanceNone == right.ChanceNone &&
        left.MaxCount == right.MaxCount &&
        left.CalculateAllLevels == right.CalculateAllLevels &&
        left.CalculateEachInCount == right.CalculateEachInCount &&
        left.UseAll == right.UseAll &&
        left.Entries.SequenceEqual(right.Entries) &&
        left.MasterDependencies.SequenceEqual(right.MasterDependencies,
            StringComparer.OrdinalIgnoreCase) &&
        left.ReviewedInputs.SequenceEqual(right.ReviewedInputs) &&
        left.NewSelfOwnedRecord == right.NewSelfOwnedRecord &&
        left.NoUnrelatedRecords == right.NoUnrelatedRecords;

    private static void ValidateParent(
        WorkspacePath path,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string? parent = Path.GetDirectoryName(path.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(Error("leveled-list-production-parent",
                "Proposal and output directories must already exist."));
        else
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
    }

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

    private static SkyrimLeveledListProductionVerification EmptyVerification(
        SkyrimLeveledListProductionRequest request,
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

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
