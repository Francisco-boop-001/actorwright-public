using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes the scalar NPC changes represented by a hash-bound mutation proposal.</summary>
public sealed partial class PluginWriteService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    INpcMutationService mutationService) : IPluginWriteService
{
    private const long MaximumProposalBytes = 1_048_576;

    public async ValueTask<PluginWriteResult> WriteAsync(PluginWriteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(request, diagnostics.ToImmutable());

        try
        {
            if (new FileInfo(request.Proposal.Value).Length is <= 0 or > MaximumProposalBytes)
                throw new InvalidDataException("Proposal size is outside the accepted bounds.");
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(request.Proposal.Value, cancellationToken));
            if (document.RootElement.TryGetProperty("artifactKind", out var kind) && kind.GetString() == "record-proposal")
                return await WriteHeadPartAsync(request, document.RootElement, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            diagnostics.Add(new Diagnostic("proposal-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, diagnostics.ToImmutable());
        }

        ProposalData? proposal = null;
        try
        {
            proposal = await ReadProposalAsync(request.Proposal, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("proposal-read-failed", DiagnosticSeverity.Error, exception.Message));
        }

        if (proposal is null)
            return Refused(request, diagnostics.ToImmutable());

        diagnostics.AddRange(ValidateProposal(request, proposal));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(request, proposal.Changes, diagnostics.ToImmutable());

        if (!File.Exists(proposal.InputPlugin.Value))
        {
            diagnostics.Add(new Diagnostic("input-plugin-missing", DiagnosticSeverity.Error, "The proposal input plugin does not exist."));
            return Refused(request, proposal.Changes, diagnostics.ToImmutable());
        }

        var requestBuild = BuildMutationRequest(request, proposal, diagnostics);
        if (requestBuild is null || diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Refused(request, proposal.Changes, diagnostics.ToImmutable());

        var mutationProposal = new NpcMutationProposal(proposal.Edition, proposal.InputPlugin, request.Output,
            proposal.TargetFormId, proposal.InputHash, proposal.Changes, proposal.PreservedFields, ImmutableArray<Diagnostic>.Empty);
        var result = await mutationService.ApplyAsync(requestBuild, mutationProposal, cancellationToken);
        return new PluginWriteResult(result.Applied, request.Proposal, request.Output, result.OutputHash,
            proposal.Changes, result.Diagnostics);
    }

    private ImmutableArray<Diagnostic> ValidatePaths(PluginWriteRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var outputParent = Path.GetDirectoryName(request.Output.Value);
        if (outputParent is null)
            diagnostics.Add(new Diagnostic("output-parent-invalid", DiagnosticSeverity.Error, "Output path has no parent directory."));
        else
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(outputParent)));
        if (!request.Proposal.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error, "The proposal must remain under the K-only lab root."));
        if (!request.Output.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("output-outside-lab", DiagnosticSeverity.Error, "The output must remain under the K-only lab root."));
        AddReparseDiagnostic(diagnostics, request.Proposal.Value, "proposal");
        if (outputParent is not null) AddReparseDiagnostic(diagnostics, outputParent, "output-parent");
        if (!File.Exists(request.Proposal.Value))
            diagnostics.Add(new Diagnostic("proposal-missing", DiagnosticSeverity.Error, "The proposal file does not exist."));
        if (File.Exists(request.Output.Value))
            diagnostics.Add(new Diagnostic("output-exists", DiagnosticSeverity.Error, "The output path already exists; plugin writes never overwrite artifacts."));
        return diagnostics.ToImmutable();
    }

    private ImmutableArray<Diagnostic> ValidateProposal(PluginWriteRequest request, ProposalData proposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (proposal.SchemaVersion != 1)
            diagnostics.Add(new Diagnostic("proposal-schema-unsupported", DiagnosticSeverity.Error, "Only mutation proposal schema version 1 is supported."));
        if (proposal.Edition != request.Edition)
            diagnostics.Add(new Diagnostic("proposal-edition-mismatch", DiagnosticSeverity.Error, "The proposal edition does not match --game."));
        if (!proposal.InputPlugin.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error, "Proposal input plugins must be copied under the K-only lab root."));
        if (!string.Equals(proposal.OutputPlugin.Value, request.Output.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("proposal-output-mismatch", DiagnosticSeverity.Error, "--output must match the output bound into the proposal."));
        if (proposal.Changes.Length == 0)
            diagnostics.Add(new Diagnostic("proposal-empty", DiagnosticSeverity.Error, "The proposal contains no selected changes."));
        if (proposal.Changes.GroupBy(change => change.Field, StringComparer.Ordinal).Any(group => group.Count() > 1))
            diagnostics.Add(new Diagnostic("proposal-duplicate-change", DiagnosticSeverity.Error, "A proposal may contain each changed field only once."));
        if (proposal.Changes.Any(change => !IsSupportedScalar(change.Field)))
            diagnostics.Add(new Diagnostic("proposal-change-unsupported", DiagnosticSeverity.Error,
                "plugin write currently accepts only typed scalar NPC fields: EditorID, Name, Sex, SkyrimWeight, Thin, Muscular, and Fat."));
        if (!File.Exists(proposal.InputPlugin.Value))
            diagnostics.Add(new Diagnostic("input-plugin-missing", DiagnosticSeverity.Error, "The proposal input plugin does not exist."));
        else
        {
            try
            {
                var hash = ComputeHash(proposal.InputPlugin.Value);
                if (!string.Equals(hash, proposal.InputHash.Value, StringComparison.OrdinalIgnoreCase))
                    diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error, "The proposal input hash does not match the current input plugin."));
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("input-read-failed", DiagnosticSeverity.Error, exception.Message)); }
            catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("input-read-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        return diagnostics.ToImmutable();
    }

    private static NpcMutationRequest? BuildMutationRequest(PluginWriteRequest request, ProposalData proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        EditorId? editorId = null;
        NpcName? name = null;
        NpcSex? sex = null;
        float? skyrimWeight = null, thin = null, muscular = null, fat = null;
        foreach (var change in proposal.Changes)
        {
            try
            {
                switch (change.Field)
                {
                    case "EditorID":
                        if (change.After is null) throw new ArgumentException("EditorID cannot be cleared by plugin write.");
                        editorId = new EditorId(change.After); break;
                    case "Name":
                        if (change.After is null) throw new ArgumentException("Name cannot be cleared by plugin write.");
                        name = new NpcName(change.After); break;
                    case "Sex":
                        if (!Enum.TryParse<NpcSex>(change.After, true, out var parsedSex)) throw new ArgumentException("Sex must be male or female.");
                        sex = parsedSex; break;
                    case "SkyrimWeight": skyrimWeight = ParseFinite(change.After, change.Field); break;
                    case "Thin": thin = ParseFinite(change.After, change.Field); break;
                    case "Muscular": muscular = ParseFinite(change.After, change.Field); break;
                    case "Fat": fat = ParseFinite(change.After, change.Field); break;
                    default: throw new ArgumentException($"Unsupported change field {change.Field}.");
                }
            }
            catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("proposal-change-invalid", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return null;
        var weight = skyrimWeight is null && thin is null && muscular is null && fat is null
            ? null : new NpcWeightPatch(skyrimWeight, thin, muscular, fat);
        return new NpcMutationRequest(request.Edition, proposal.InputPlugin, request.Output, proposal.TargetFormId,
            editorId, name, weight, proposal.InputHash, false, null, sex);
    }

    private static float ParseFinite(string? value, string field)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            float.IsNaN(parsed) || float.IsInfinity(parsed))
            throw new ArgumentException($"{field} must contain a finite invariant-culture number.");
        return parsed;
    }

    private static bool IsSupportedScalar(string field) => field is "EditorID" or "Name" or "Sex" or "SkyrimWeight" or "Thin" or "Muscular" or "Fat";

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async ValueTask<ProposalData> ReadProposalAsync(WorkspacePath path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.Value);
        if (info.Length <= 0 || info.Length > MaximumProposalBytes) throw new InvalidDataException("Proposal size is outside the accepted bounds.");
        await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Proposal root must be a JSON object.");
        var schema = root.GetProperty("schemaVersion").GetInt32();
        if (!GameEditionExtensions.TryParseWireName(root.GetProperty("edition").GetString() ?? string.Empty, out var edition))
            throw new InvalidDataException("Proposal edition is invalid.");
        var input = new WorkspacePath(root.GetProperty("inputPlugin").GetString() ?? string.Empty);
        var output = new WorkspacePath(root.GetProperty("outputPlugin").GetString() ?? string.Empty);
        if (!FormId.TryParse(root.GetProperty("targetFormId").GetString() ?? string.Empty, out var formId)) throw new InvalidDataException("Proposal target FormID is invalid.");
        var hash = new Sha256Hash(root.GetProperty("inputSha256").GetString() ?? string.Empty);
        var changes = root.GetProperty("changes").EnumerateArray().Select(item => new MutationChange(
            item.GetProperty("field").GetString() ?? string.Empty,
            item.TryGetProperty("before", out var before) && before.ValueKind != JsonValueKind.Null ? before.GetString() : null,
            item.TryGetProperty("after", out var after) && after.ValueKind != JsonValueKind.Null ? after.GetString() : null)).ToImmutableArray();
        var preserved = root.TryGetProperty("preservedFields", out var fields) ? fields.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToImmutableArray() : ImmutableArray<string>.Empty;
        return new ProposalData(schema, edition, input, output, formId, hash, changes, preserved);
    }

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                { diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error, $"The {role} traverses a reparse point.")); return; }
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, exception.Message)); return; }
            catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, exception.Message)); return; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static PluginWriteResult Refused(PluginWriteRequest request, ImmutableArray<Diagnostic> diagnostics) => Refused(request, [], diagnostics);

    private static PluginWriteResult Refused(PluginWriteRequest request, ImmutableArray<MutationChange> changes, ImmutableArray<Diagnostic> diagnostics) =>
        new(false, request.Proposal, request.Output, null, changes, diagnostics);

    private sealed record ProposalData(int SchemaVersion, GameEdition Edition, WorkspacePath InputPlugin, WorkspacePath OutputPlugin,
        FormId TargetFormId, Sha256Hash InputHash, ImmutableArray<MutationChange> Changes, ImmutableArray<string> PreservedFields);
}
