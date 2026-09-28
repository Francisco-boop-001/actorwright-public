using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>Hash-bound, Skyrim-only TINI/TINC/TINV/TIAS patch service. It owns persisted
/// NPC layer bytes only; race defaults and RaceMenu custom mask paths remain explicit
/// provider/sidecar concerns rather than being guessed or dropped.</summary>
public sealed class SkyrimFaceTintPatchService(IWorkspacePolicy policy, WorkspacePath labRoot) : ISkyrimFaceTintPatchService
{
    private static readonly ImmutableArray<string> PreservedFields = [
        "EDID", "FULL", "ACBS", "PNAM", "NAM9", "NAMA", "QNAM", "NAM7", "all-unrelated-NPC-subrecords"];
    private static readonly JsonSerializerOptions ProposalOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly Sha256Hash ZeroHash = new(new string('0', 64));

    public ValueTask<SkyrimFaceTintPatchProposal> AnalyzeAsync(SkyrimFaceTintPatchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, requireExpectedHash: false, allowExistingProposal: false).ToBuilder();
        var inputHash = File.Exists(request.InputPlugin.Value) ? ComputeHash(request.InputPlugin.Value) : ZeroHash;
        if (request.ExpectedInputHash is { } expected && expected != inputHash)
            diagnostics.Add(new Diagnostic("sse-face-tint-input-hash-mismatch", DiagnosticSeverity.Error,
                $"Input hash {inputHash} does not match the expected hash {expected}."));

        var changes = ImmutableArray.CreateBuilder<MutationChange>();
        if (!HasErrors(diagnostics))
        {
            try
            {
                var current = BethesdaSkyrimFaceTintAdapter.Read(request.Edition, request.InputPlugin, request.TargetFormId);
                ValidatePatch(request.Patch, diagnostics);
                if (!HasErrors(diagnostics) && !current.Layers.SequenceEqual(request.Patch.Layers))
                    changes.Add(new MutationChange("TINI/TINC/TINV/TIAS", Describe(current.Layers), Describe(request.Patch.Layers)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("sse-face-tint-read-failed", DiagnosticSeverity.Error,
                    $"Skyrim face-tint data could not be read: {exception.Message}"));
            }
        }

        if (changes.Count == 0 && !HasErrors(diagnostics))
            diagnostics.Add(new Diagnostic("sse-face-tint-noop", DiagnosticSeverity.Info,
                "The requested ordered Skyrim tint layers already match the NPC."));
        var proposal = new SkyrimFaceTintPatchProposal(request.Edition, request.InputPlugin, request.OutputPlugin,
            request.TargetFormId, inputHash, changes.ToImmutable(), PreservedFields, diagnostics.ToImmutable());
        if (request.ProposalPath is { } proposalPath && !HasErrors(diagnostics))
            WriteProposal(proposalPath, proposal, diagnostics);
        return ValueTask.FromResult(proposal with { Diagnostics = diagnostics.ToImmutable() });
    }

    public ValueTask<SkyrimFaceTintPatchResult> ApplyAsync(SkyrimFaceTintPatchRequest request,
        SkyrimFaceTintPatchProposal proposal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = proposal.Diagnostics.ToBuilder();
        diagnostics.AddRange(ValidateRequest(request, requireExpectedHash: true, allowExistingProposal: true));
        ValidatePatch(request.Patch, diagnostics);
        if (request.DryRun)
            diagnostics.Add(new Diagnostic("dry-run", DiagnosticSeverity.Info, "Dry-run requested; no plugin was written."));
        if (File.Exists(request.InputPlugin.Value) && proposal.InputHash != ComputeHash(request.InputPlugin.Value))
            diagnostics.Add(new Diagnostic("sse-face-tint-input-changed", DiagnosticSeverity.Error,
                "The input changed after Skyrim face-tint analysis."));
        if (request.ExpectedInputHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error,
                "Apply requires --expected-sha256."));
        if (request.DryRun || HasErrors(diagnostics) || !proposal.IsApplicable)
            return ValueTask.FromResult(new SkyrimFaceTintPatchResult(false, proposal, null, diagnostics.ToImmutable()));

        var temporary = request.OutputPlugin.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            BethesdaSkyrimFaceTintAdapter.Write(request, new WorkspacePath(temporary));
            File.Move(temporary, request.OutputPlugin.Value, overwrite: false);
            var after = BethesdaSkyrimFaceTintAdapter.Read(request.Edition, request.OutputPlugin, request.TargetFormId);
            if (!after.Layers.SequenceEqual(request.Patch.Layers))
            {
                TryDelete(request.OutputPlugin.Value);
                diagnostics.Add(new Diagnostic("sse-face-tint-mismatch", DiagnosticSeverity.Error,
                    "Output TINI/TINC/TINV/TIAS layers do not match the requested typed layers."));
                return ValueTask.FromResult(new SkyrimFaceTintPatchResult(false, proposal, null, diagnostics.ToImmutable()));
            }
            return ValueTask.FromResult(new SkyrimFaceTintPatchResult(true, proposal,
                ComputeHash(request.OutputPlugin.Value), diagnostics.ToImmutable()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(request.OutputPlugin.Value);
            diagnostics.Add(new Diagnostic("sse-face-tint-write-failed", DiagnosticSeverity.Error, exception.Message));
            return ValueTask.FromResult(new SkyrimFaceTintPatchResult(false, proposal, null, diagnostics.ToImmutable()));
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void ValidatePatch(SkyrimFaceTintPatch patch, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch.Layers.Length > 256)
            diagnostics.Add(new Diagnostic("sse-face-tint-too-many", DiagnosticSeverity.Error,
                "A Skyrim face-tint patch may contain at most 256 layers."));
        var duplicates = patch.Layers.GroupBy(item => item.Index).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToArray();
        if (duplicates.Length > 0)
            diagnostics.Add(new Diagnostic("sse-face-tint-duplicate", DiagnosticSeverity.Error,
                "Skyrim TINI indexes must be unique; duplicate layers would make the authored order ambiguous."));
        for (var index = 0; index < patch.Layers.Length; index++)
        {
            if (patch.Layers[index].Coverage > 100)
                diagnostics.Add(new Diagnostic("sse-face-tint-coverage-invalid", DiagnosticSeverity.Error,
                    $"Layer {index} coverage must be between 0 and 100."));
        }
    }

    private ImmutableArray<Diagnostic> ValidateRequest(SkyrimFaceTintPatchRequest request, bool requireExpectedHash,
        bool allowExistingProposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(new Diagnostic("sse-face-tint-game-unsupported", DiagnosticSeverity.Error,
                "Skyrim face tint patching is currently supported for Skyrim SE only."));
        if (!request.InputPlugin.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error,
                "Input plugins must remain under the K-only lab root."));
        if (!request.OutputPlugin.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("output-outside-lab", DiagnosticSeverity.Error,
                "Output plugins must remain under the K-only lab root."));
        if (!File.Exists(request.InputPlugin.Value))
            diagnostics.Add(new Diagnostic("input-plugin-missing", DiagnosticSeverity.Error,
                "The input plugin does not exist."));
        var parent = Path.GetDirectoryName(request.OutputPlugin.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("output-parent-missing", DiagnosticSeverity.Error,
                "The output directory must already exist."));
        else
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(new Diagnostic("output-exists", DiagnosticSeverity.Error,
                "The output path already exists; Skyrim face-tint patches never overwrite."));
        if (string.Equals(request.InputPlugin.Value, request.OutputPlugin.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("input-output-same", DiagnosticSeverity.Error,
                "Input and output paths must differ."));
        if (!string.Equals(Path.GetFileName(request.InputPlugin.Value), Path.GetFileName(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("plugin-identity-mismatch", DiagnosticSeverity.Error,
                "Input and output must keep the same plugin filename."));
        if (!IsPluginPath(request.InputPlugin.Value) || !IsPluginPath(request.OutputPlugin.Value))
            diagnostics.Add(new Diagnostic("plugin-extension-invalid", DiagnosticSeverity.Error,
                "Input and output must use .esp, .esm, or .esl."));
        if (requireExpectedHash && request.ExpectedInputHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error,
                "Apply requires --expected-sha256."));
        if (request.ProposalPath is { } proposal)
        {
            if (!proposal.IsUnder(labRoot))
                diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error,
                    "Proposal paths must remain under the K-only lab root."));
            var proposalParent = Path.GetDirectoryName(proposal.Value);
            if (proposalParent is null || !Directory.Exists(proposalParent))
                diagnostics.Add(new Diagnostic("proposal-parent-missing", DiagnosticSeverity.Error,
                    "The proposal directory must already exist."));
            if (File.Exists(proposal.Value) && !allowExistingProposal)
                diagnostics.Add(new Diagnostic("proposal-exists", DiagnosticSeverity.Error,
                    "Proposal paths never overwrite artifacts."));
            if (proposalParent is not null) AddReparseDiagnostic(diagnostics, proposalParent, "proposal-parent");
        }
        AddReparseDiagnostic(diagnostics, request.InputPlugin.Value, "input-plugin");
        if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        return diagnostics.ToImmutable();
    }

    private static string Describe(IEnumerable<SkyrimFaceTintLayer> layers) =>
        JsonSerializer.Serialize(layers, ProposalOptions);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static bool IsPluginPath(string path) =>
        Path.GetExtension(path) is ".esp" or ".esm" or ".esl";

    private static Sha256Hash ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error,
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error,
                    $"The {role} could not be inspected: {exception.Message}"));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static void WriteProposal(WorkspacePath path, SkyrimFaceTintPatchProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, proposal, ProposalOptions);
                file.Flush(true);
                File.Move(temporary, path.Value, overwrite: false);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add(new Diagnostic("proposal-write-failed", DiagnosticSeverity.Error, exception.Message));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
