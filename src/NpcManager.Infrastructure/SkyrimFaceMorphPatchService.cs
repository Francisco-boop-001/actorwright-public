using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>Hash-bound, Skyrim-only NAM9/NAMA patch service. It validates the native record
/// channel without pretending to resolve RaceMenu extended morphs or render a face.</summary>
public sealed class SkyrimFaceMorphPatchService(IWorkspacePolicy policy, WorkspacePath labRoot) : ISkyrimFaceMorphPatchService
{
    private static readonly ImmutableArray<string> PreservedFields = ["EDID", "FULL", "ACBS", "PNAM", "HCLF", "NAM9-trailing", "NAMA-sentinel", "all-unrelated-NPC-subrecords"];
    private static readonly JsonSerializerOptions ProposalOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Sha256Hash ZeroHash = new(new string('0', 64));

    public ValueTask<SkyrimFaceMorphPatchProposal> AnalyzeAsync(SkyrimFaceMorphPatchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, false, false).ToBuilder();
        var inputHash = File.Exists(request.InputPlugin.Value) ? ComputeHash(request.InputPlugin.Value) : ZeroHash;
        if (request.ExpectedInputHash is { } expected && expected != inputHash)
            diagnostics.Add(new Diagnostic("face-morph-input-hash-mismatch", DiagnosticSeverity.Error, $"Input hash {inputHash} does not match expected hash {expected}."));
        var changes = ImmutableArray.CreateBuilder<MutationChange>();
        if (!HasErrors(diagnostics))
        {
            try
            {
                var current = BethesdaSkyrimFaceMorphAdapter.Read(request.Edition, request.InputPlugin, request.TargetFormId);
                ValidatePatch(request.Patch, diagnostics);
                if (!HasErrors(diagnostics) && (!current.HasNam9 || !current.HasNama || !current.Nam9Sliders.SequenceEqual(request.Patch.Nam9Sliders) ||
                    current.Nam9Trailing != request.Patch.Nam9Trailing || !current.NamaValues.SequenceEqual(request.Patch.NamaValues)))
                    changes.Add(new MutationChange("NAM9/NAMA", Describe(current), Describe(request.Patch)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { diagnostics.Add(new Diagnostic("face-morph-read-failed", DiagnosticSeverity.Error, $"Skyrim face morph data could not be read: {exception.Message}")); }
        }
        if (changes.Count == 0 && !HasErrors(diagnostics)) diagnostics.Add(new Diagnostic("face-morph-noop", DiagnosticSeverity.Info, "The requested NAM9/NAMA values already match the NPC."));
        var proposal = new SkyrimFaceMorphPatchProposal(request.Edition, request.InputPlugin, request.OutputPlugin, request.TargetFormId,
            inputHash, changes.ToImmutable(), PreservedFields, diagnostics.ToImmutable());
        if (request.ProposalPath is { } proposalPath && !HasErrors(diagnostics)) WriteProposal(proposalPath, proposal, diagnostics);
        return ValueTask.FromResult(proposal with { Diagnostics = diagnostics.ToImmutable() });
    }

    public ValueTask<SkyrimFaceMorphPatchResult> ApplyAsync(SkyrimFaceMorphPatchRequest request,
        SkyrimFaceMorphPatchProposal proposal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = proposal.Diagnostics.ToBuilder(); diagnostics.AddRange(ValidateRequest(request, true, true)); ValidatePatch(request.Patch, diagnostics);
        if (request.DryRun) diagnostics.Add(new Diagnostic("dry-run", DiagnosticSeverity.Info, "Dry-run requested; no plugin was written."));
        if (File.Exists(request.InputPlugin.Value) && proposal.InputHash != ComputeHash(request.InputPlugin.Value)) diagnostics.Add(new Diagnostic("face-morph-input-changed", DiagnosticSeverity.Error, "The input changed after face-morph analysis."));
        if (request.ExpectedInputHash is null) diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        if (request.DryRun || HasErrors(diagnostics) || !proposal.IsApplicable)
            return ValueTask.FromResult(new SkyrimFaceMorphPatchResult(false, proposal, null, diagnostics.ToImmutable()));
        var temporary = request.OutputPlugin.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            BethesdaSkyrimFaceMorphAdapter.Write(request, new WorkspacePath(temporary)); File.Move(temporary, request.OutputPlugin.Value, overwrite: false);
            var after = BethesdaSkyrimFaceMorphAdapter.Read(request.Edition, request.OutputPlugin, request.TargetFormId);
            if (!after.HasNam9 || !after.HasNama || !after.Nam9Sliders.SequenceEqual(request.Patch.Nam9Sliders) || after.Nam9Trailing != request.Patch.Nam9Trailing || !after.NamaValues.SequenceEqual(request.Patch.NamaValues))
            {
                TryDelete(request.OutputPlugin.Value); diagnostics.Add(new Diagnostic("face-morph-mismatch", DiagnosticSeverity.Error, "Output NAM9/NAMA does not match the requested typed morphs."));
                return ValueTask.FromResult(new SkyrimFaceMorphPatchResult(false, proposal, null, diagnostics.ToImmutable()));
            }
            return ValueTask.FromResult(new SkyrimFaceMorphPatchResult(true, proposal, ComputeHash(request.OutputPlugin.Value), diagnostics.ToImmutable()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { TryDelete(request.OutputPlugin.Value); diagnostics.Add(new Diagnostic("face-morph-write-failed", DiagnosticSeverity.Error, exception.Message)); return ValueTask.FromResult(new SkyrimFaceMorphPatchResult(false, proposal, null, diagnostics.ToImmutable())); }
        finally { TryDelete(temporary); }
    }

    private static void ValidatePatch(SkyrimFaceMorphPatch patch, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch.Nam9Sliders.Length != 18) diagnostics.Add(new Diagnostic("face-morph-nam9-shape", DiagnosticSeverity.Error, "NAM9 requires exactly 18 editable sliders."));
        if (patch.NamaValues.Length != 4) diagnostics.Add(new Diagnostic("face-morph-nama-shape", DiagnosticSeverity.Error, "NAMA requires exactly four family values."));
        for (var i = 0; i < patch.Nam9Sliders.Length; i++)
        {
            var value = patch.Nam9Sliders[i];
            if (!float.IsFinite(value)) diagnostics.Add(new Diagnostic("face-morph-nam9-nonfinite", DiagnosticSeverity.Error, $"NAM9 slider {i} must be finite."));
            else if (value < -1F || value > 1F) diagnostics.Add(new Diagnostic("face-morph-nam9-range", DiagnosticSeverity.Error, $"NAM9 slider {i} must be between -1 and 1."));
        }
        if (!float.IsFinite(patch.Nam9Trailing)) diagnostics.Add(new Diagnostic("face-morph-trailing-nonfinite", DiagnosticSeverity.Error, "NAM9 trailing value must be finite."));
    }

    private ImmutableArray<Diagnostic> ValidateRequest(SkyrimFaceMorphPatchRequest request, bool requireExpectedHash, bool allowExistingProposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition) diagnostics.Add(new Diagnostic("face-morph-game-unsupported", DiagnosticSeverity.Error, "NAM9/NAMA face morph patching is currently supported for Skyrim SE only."));
        if (!request.InputPlugin.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error, "Input plugins must remain under the K-only lab root."));
        if (!request.OutputPlugin.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("output-outside-lab", DiagnosticSeverity.Error, "Output plugins must remain under the K-only lab root."));
        if (!File.Exists(request.InputPlugin.Value)) diagnostics.Add(new Diagnostic("input-plugin-missing", DiagnosticSeverity.Error, "The input plugin does not exist."));
        var parent = Path.GetDirectoryName(request.OutputPlugin.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("output-parent-missing", DiagnosticSeverity.Error, "The output directory must already exist.")); else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputPlugin.Value)) diagnostics.Add(new Diagnostic("output-exists", DiagnosticSeverity.Error, "The output path already exists; face-morph patches never overwrite."));
        if (string.Equals(request.InputPlugin.Value, request.OutputPlugin.Value, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("input-output-same", DiagnosticSeverity.Error, "Input and output paths must differ."));
        if (!string.Equals(Path.GetFileName(request.InputPlugin.Value), Path.GetFileName(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("plugin-identity-mismatch", DiagnosticSeverity.Error, "Input and output must keep the same plugin filename."));
        if (!IsPluginPath(request.InputPlugin.Value) || !IsPluginPath(request.OutputPlugin.Value)) diagnostics.Add(new Diagnostic("plugin-extension-invalid", DiagnosticSeverity.Error, "Input and output must use .esp, .esm, or .esl."));
        if (requireExpectedHash && request.ExpectedInputHash is null) diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        if (request.ProposalPath is { } proposal)
        {
            if (!proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error, "Proposal paths must remain under the K-only lab root."));
            var proposalParent = Path.GetDirectoryName(proposal.Value); if (proposalParent is null || !Directory.Exists(proposalParent)) diagnostics.Add(new Diagnostic("proposal-parent-missing", DiagnosticSeverity.Error, "The proposal directory must already exist."));
            if (File.Exists(proposal.Value) && !allowExistingProposal) diagnostics.Add(new Diagnostic("proposal-exists", DiagnosticSeverity.Error, "Proposal paths never overwrite artifacts."));
            if (proposalParent is not null) AddReparseDiagnostic(diagnostics, proposalParent, "proposal-parent");
        }
        AddReparseDiagnostic(diagnostics, request.InputPlugin.Value, "input-plugin"); if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        return diagnostics.ToImmutable();
    }

    private static string Describe(SkyrimFaceMorphSnapshot value) => JsonSerializer.Serialize(value, ProposalOptions);
    private static string Describe(SkyrimFaceMorphPatch value) => JsonSerializer.Serialize(value, ProposalOptions);
    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static bool IsPluginPath(string path) => Path.GetExtension(path) is ".esp" or ".esm" or ".esl";
    private static Sha256Hash ComputeHash(string path) { using var stream = File.OpenRead(path); return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()); }
    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path); while (!string.IsNullOrEmpty(current))
        {
            try { if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) { diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error, $"The {role} traverses a reparse point.")); return; } }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, $"The {role} could not be inspected: {exception.Message}")); return; }
            var parent = Directory.GetParent(current)?.FullName; if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break; current = parent ?? string.Empty;
        }
    }
    private static void WriteProposal(WorkspacePath path, SkyrimFaceMorphPatchProposal proposal, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try { var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N"); try { File.WriteAllText(temporary, JsonSerializer.Serialize(proposal, ProposalOptions), new UTF8Encoding(false)); File.Move(temporary, path.Value, overwrite: false); } finally { TryDelete(temporary); } }
        catch (Exception exception) { diagnostics.Add(new Diagnostic("proposal-write-failed", DiagnosticSeverity.Error, exception.Message)); }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
