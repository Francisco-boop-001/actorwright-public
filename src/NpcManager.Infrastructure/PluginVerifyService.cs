using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>Verifies a proposal-bound plugin using a read-only TES record parser.</summary>
public sealed class PluginVerifyService(IWorkspacePolicy policy, WorkspacePath labRoot) : IPluginVerifyService
{
    private const long MaximumProposalBytes = 1_048_576;

    public async ValueTask<PluginVerifyResult> VerifyAsync(PluginVerifyRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(diagnostics.ToImmutable());
        string? artifactKind;
        try { artifactKind = await ReadArtifactKindAsync(request.Proposal, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("proposal-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics.ToImmutable());
        }
        if (string.Equals(artifactKind, "skyrim-npc-appearance-override-proposal",
                StringComparison.Ordinal))
        {
            return await VerifyAppearanceOverrideAsync(
                request,
                diagnostics,
                cancellationToken);
        }

        ProposalData? proposal = null;
        try { proposal = await ReadProposalAsync(request, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("proposal-read-failed", DiagnosticSeverity.Error,
                exception.Message));
        }
        if (proposal is null) return Refused(diagnostics.ToImmutable());
        diagnostics.AddRange(ValidateProposal(request, proposal));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(diagnostics.ToImmutable());
        var result = BethesdaPluginVerifier.Verify(new PluginVerificationRequest(request.Edition, request.Before, request.After,
            proposal.TargetFormId, proposal.Changes, proposal.PreservedFields), cancellationToken);
        return new PluginVerifyResult(result.IsValid, result.ObservedChanges, diagnostics.ToImmutable().AddRange(result.Diagnostics));
    }

    private async ValueTask<PluginVerifyResult> VerifyAppearanceOverrideAsync(
        PluginVerifyRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var proposal = await NpcAppearanceOverrideProposalReader.ReadAsync(
                request.Proposal,
                cancellationToken);
            var appearanceRequest = new NpcAppearanceOverrideRequest(
                request.Edition,
                request.Before,
                proposal.SourceSha256,
                proposal.TargetFormId,
                request.Proposal,
                request.After,
                proposal.Race,
                proposal.Sex,
                proposal.Appearance,
                proposal.RuntimeAppearance);
            var result = await new NpcAppearanceOverrideService(policy, labRoot)
                .VerifyAsync(appearanceRequest, proposal, cancellationToken);
            diagnostics.AddRange(result.Diagnostics);
            var observed = ImmutableArray.Create(
                new MutationChange("SourceOwnedTargetCount", null,
                    result.SourceOwnedTargetCount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
                new MutationChange("SelfOwnedTargetCount", null,
                    result.SelfOwnedTargetCount.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
                new MutationChange("AppearanceMatches", null,
                    result.AppearanceMatches.ToString().ToLowerInvariant()),
                new MutationChange("UnrelatedNpcSubrecordsPreserved", null,
                    result.UnrelatedNpcSubrecordsPreserved.ToString().ToLowerInvariant()),
                new MutationChange("UnrelatedScriptsPreserved", null,
                    result.UnrelatedScriptsPreserved.ToString().ToLowerInvariant()));
            return new PluginVerifyResult(
                result.IsValid && !diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error),
                observed,
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("proposal-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics.ToImmutable());
        }
    }

    private ImmutableArray<Diagnostic> ValidatePaths(PluginVerifyRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var afterParent = Path.GetDirectoryName(request.After.Value);
        if (afterParent is null) diagnostics.Add(new Diagnostic("output-parent-invalid", DiagnosticSeverity.Error, "After path has no parent directory."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(afterParent)));
        if (!request.Before.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("before-outside-lab", DiagnosticSeverity.Error, "Before plugin must remain under the K-only lab root."));
        if (!request.After.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("after-outside-lab", DiagnosticSeverity.Error, "After plugin must remain under the K-only lab root."));
        if (!request.Proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error, "Proposal must remain under the K-only lab root."));
        AddReparseDiagnostic(diagnostics, request.Before.Value, "before"); AddReparseDiagnostic(diagnostics, request.After.Value, "after"); AddReparseDiagnostic(diagnostics, request.Proposal.Value, "proposal");
        if (!File.Exists(request.Before.Value)) diagnostics.Add(new Diagnostic("before-missing", DiagnosticSeverity.Error, "Before plugin does not exist."));
        if (!File.Exists(request.After.Value)) diagnostics.Add(new Diagnostic("after-missing", DiagnosticSeverity.Error, "After plugin does not exist."));
        if (!File.Exists(request.Proposal.Value)) diagnostics.Add(new Diagnostic("proposal-missing", DiagnosticSeverity.Error, "Proposal does not exist."));
        return diagnostics.ToImmutable();
    }

    private static ImmutableArray<Diagnostic> ValidateProposal(PluginVerifyRequest request, ProposalData proposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (proposal.SchemaVersion != 1) diagnostics.Add(new Diagnostic("proposal-schema-unsupported", DiagnosticSeverity.Error, "Only mutation proposal schema version 1 is supported."));
        if (proposal.Edition != request.Edition) diagnostics.Add(new Diagnostic("proposal-edition-mismatch", DiagnosticSeverity.Error, "Proposal edition does not match --game."));
        if (!string.Equals(proposal.InputPlugin.Value, request.Before.Value, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("proposal-before-mismatch", DiagnosticSeverity.Error, "Proposal input does not match --before."));
        if (!string.Equals(proposal.OutputPlugin.Value, request.After.Value, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("proposal-after-mismatch", DiagnosticSeverity.Error, "Proposal output does not match --after."));
        if (proposal.Changes.Length == 0) diagnostics.Add(new Diagnostic("proposal-empty", DiagnosticSeverity.Error, "Proposal contains no selected changes."));
        if (proposal.Changes.GroupBy(change => change.Field, StringComparer.Ordinal).Any(group => group.Count() > 1)) diagnostics.Add(new Diagnostic("proposal-duplicate-change", DiagnosticSeverity.Error, "A proposal may contain each changed field only once."));
        try
        {
            using var stream = File.OpenRead(request.Before.Value);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(hash, proposal.InputHash.Value, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error, "Proposal input hash does not match --before."));
        }
        catch (IOException exception) { diagnostics.Add(new Diagnostic("before-read-failed", DiagnosticSeverity.Error, exception.Message)); }
        catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("before-read-failed", DiagnosticSeverity.Error, exception.Message)); }
        return diagnostics.ToImmutable();
    }

    private static async ValueTask<ProposalData> ReadProposalAsync(
        PluginVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var path = request.Proposal;
        var info = new FileInfo(path.Value);
        if (info.Length <= 0 || info.Length > MaximumProposalBytes) throw new InvalidDataException("Proposal size is outside the accepted bounds.");
        await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Proposal root must be a JSON object.");
        if (!GameEditionExtensions.TryParseWireName(root.GetProperty("edition").GetString() ?? string.Empty, out var edition)) throw new InvalidDataException("Proposal edition is invalid.");
        if (!FormId.TryParse(root.GetProperty("targetFormId").GetString() ?? string.Empty, out var formId)) throw new InvalidDataException("Proposal target FormID is invalid.");
        var changes = root.GetProperty("changes").EnumerateArray().Select(item => new MutationChange(item.GetProperty("field").GetString() ?? string.Empty,
            item.TryGetProperty("before", out var before) && before.ValueKind != JsonValueKind.Null ? before.GetString() : null,
            item.TryGetProperty("after", out var after) && after.ValueKind != JsonValueKind.Null ? after.GetString() : null)).ToImmutableArray();
        var preserved = root.TryGetProperty("preservedFields", out var fields) ? fields.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToImmutableArray() : ImmutableArray<string>.Empty;
        return new ProposalData(root.GetProperty("schemaVersion").GetInt32(), edition,
            new WorkspacePath(root.GetProperty("inputPlugin").GetString() ?? string.Empty),
            ResolveOutputPlugin(root, request.Proposal), formId,
            new Sha256Hash(root.GetProperty("inputSha256").GetString() ?? string.Empty), changes, preserved);
    }

    private static async ValueTask<string?> ReadArtifactKindAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.Value);
        if (info.Length <= 0 || info.Length > MaximumProposalBytes)
            throw new InvalidDataException("Proposal size is outside the accepted bounds.");
        await using var stream = new FileStream(path.Value, FileMode.Open,
            FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Proposal root must be a JSON object.");
        return root.TryGetProperty("artifactKind", out var kind) &&
               kind.ValueKind == JsonValueKind.String
            ? kind.GetString()
            : null;
    }

    private static WorkspacePath ResolveOutputPlugin(JsonElement root, WorkspacePath proposalPath)
    {
        var value = root.GetProperty("outputPlugin").GetString() ?? string.Empty;
        if (Path.IsPathFullyQualified(value)) return new WorkspacePath(value);

        var artifactKind = root.TryGetProperty("artifactKind", out var kind)
            ? kind.GetString()
            : null;
        if (!string.Equals(artifactKind, "existing-npc-edit-proposal", StringComparison.Ordinal))
            throw new InvalidDataException(
                "Relative output plugin paths are accepted only for existing-NPC package proposals.");

        var relative = new AssetPath(value);
        var evidenceRoot = Directory.GetParent(proposalPath.Value);
        if (evidenceRoot is null ||
            !string.Equals(evidenceRoot.Name, "evidence", StringComparison.OrdinalIgnoreCase) ||
            evidenceRoot.Parent is null)
            throw new InvalidDataException(
                "A package-relative existing-NPC proposal must be stored under an evidence directory.");

        var packageRoot = new WorkspacePath(evidenceRoot.Parent.FullName);
        var resolved = new WorkspacePath(Path.Combine(
            packageRoot.Value,
            relative.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.IsUnder(packageRoot))
            throw new InvalidDataException("The proposal output plugin escaped its package root.");
        return resolved;
    }

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try { if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) { diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error, $"The {role} traverses a reparse point.")); return; } }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, exception.Message)); return; }
            catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, exception.Message)); return; }
            var parent = Directory.GetParent(current)?.FullName; if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break; current = parent ?? string.Empty;
        }
    }

    private static PluginVerifyResult Refused(ImmutableArray<Diagnostic> diagnostics) => new(false, ImmutableArray<MutationChange>.Empty, diagnostics);

    private sealed record ProposalData(int SchemaVersion, GameEdition Edition, WorkspacePath InputPlugin, WorkspacePath OutputPlugin,
        FormId TargetFormId, Sha256Hash InputHash, ImmutableArray<MutationChange> Changes, ImmutableArray<string> PreservedFields);
}
