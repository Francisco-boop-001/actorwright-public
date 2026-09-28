using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>Hash-bound, FO4-only TETI/TEND patch service. The provider RACE/CLFM lookup and
/// renderer are intentionally outside this service; it edits the persisted layer list only.</summary>
public sealed class NpcFaceTintPatchService(IWorkspacePolicy policy, WorkspacePath labRoot) : INpcFaceTintPatchService
{
    private static readonly ImmutableArray<string> PreservedFields = ["EDID", "FULL", "ACBS", "PNAM", "HCLF", "MSDK", "MSDV", "FMRI", "FMRS", "TEND-unrelated"];
    private static readonly JsonSerializerOptions ProposalOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly Sha256Hash ZeroHash = new(new string('0', 64));

    public ValueTask<NpcFaceTintPatchProposal> AnalyzeAsync(NpcFaceTintPatchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, requireExpectedHash: false, allowExistingProposal: false).ToBuilder();
        var inputHash = File.Exists(request.InputPlugin.Value) ? ComputeHash(request.InputPlugin.Value) : ZeroHash;
        if (request.ExpectedInputHash is { } expected && expected != inputHash)
            diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error,
                $"Input hash {inputHash} does not match the expected hash {expected}."));

        var changes = ImmutableArray.CreateBuilder<MutationChange>();
        if (!HasErrors(diagnostics))
        {
            try
            {
                var current = BethesdaNpcFaceTintAdapter.Read(request.Edition, request.InputPlugin, request.TargetFormId);
                ValidatePatch(request.Patch, diagnostics);
                if (!HasErrors(diagnostics) && !current.Layers.SequenceEqual(request.Patch.Layers))
                    changes.Add(new MutationChange("TETI/TEND", Describe(current.Layers), Describe(request.Patch.Layers)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("face-tint-read-failed", DiagnosticSeverity.Error,
                    $"NPC face-tint data could not be read: {exception.Message}"));
            }
        }

        if (changes.Count == 0 && !HasErrors(diagnostics))
            diagnostics.Add(new Diagnostic("face-tint-noop", DiagnosticSeverity.Info, "The requested ordered tint layers already match the NPC."));
        var proposal = new NpcFaceTintPatchProposal(request.Edition, request.InputPlugin, request.OutputPlugin,
            request.TargetFormId, inputHash, changes.ToImmutable(), PreservedFields, diagnostics.ToImmutable());
        if (request.ProposalPath is { } proposalPath && !HasErrors(diagnostics)) WriteProposal(proposalPath, proposal, diagnostics);
        return ValueTask.FromResult(proposal with { Diagnostics = diagnostics.ToImmutable() });
    }

    public ValueTask<NpcFaceTintPatchResult> ApplyAsync(NpcFaceTintPatchRequest request,
        NpcFaceTintPatchProposal proposal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = proposal.Diagnostics.ToBuilder();
        diagnostics.AddRange(ValidateRequest(request, requireExpectedHash: true, allowExistingProposal: true));
        ValidatePatch(request.Patch, diagnostics);
        if (request.DryRun) diagnostics.Add(new Diagnostic("dry-run", DiagnosticSeverity.Info, "Dry-run requested; no plugin was written."));
        if (File.Exists(request.InputPlugin.Value) && proposal.InputHash != ComputeHash(request.InputPlugin.Value))
            diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error, "The input changed after face-tint analysis."));
        if (request.ExpectedInputHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        if (request.DryRun || HasErrors(diagnostics) || !proposal.IsApplicable)
            return ValueTask.FromResult(new NpcFaceTintPatchResult(false, proposal, null, diagnostics.ToImmutable()));

        var temporary = request.OutputPlugin.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            BethesdaNpcFaceTintAdapter.Write(request, new WorkspacePath(temporary));
            if (!File.Exists(temporary)) throw new IOException("The face-tint adapter did not produce an output file.");
            File.Move(temporary, request.OutputPlugin.Value, overwrite: false);
            var after = BethesdaNpcFaceTintAdapter.Read(request.Edition, request.OutputPlugin, request.TargetFormId);
            if (!after.Layers.SequenceEqual(request.Patch.Layers))
            {
                TryDelete(request.OutputPlugin.Value);
                diagnostics.Add(new Diagnostic("face-tint-mismatch", DiagnosticSeverity.Error, "Output TETI/TEND layers do not match the requested typed layers."));
                return ValueTask.FromResult(new NpcFaceTintPatchResult(false, proposal, null, diagnostics.ToImmutable()));
            }
            return ValueTask.FromResult(new NpcFaceTintPatchResult(true, proposal, ComputeHash(request.OutputPlugin.Value), diagnostics.ToImmutable()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(request.OutputPlugin.Value);
            diagnostics.Add(new Diagnostic("face-tint-write-failed", DiagnosticSeverity.Error, exception.Message));
            return ValueTask.FromResult(new NpcFaceTintPatchResult(false, proposal, null, diagnostics.ToImmutable()));
        }
        finally { TryDelete(temporary); }
    }

    private static void ValidatePatch(NpcFaceTintPatch patch, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch.Layers.Length > 256)
            diagnostics.Add(new Diagnostic("face-tint-too-many", DiagnosticSeverity.Error, "A face-tint patch may contain at most 256 layers."));
        var duplicates = patch.Layers.GroupBy(item => item.OptionIndex).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        if (duplicates.Length > 0)
            diagnostics.Add(new Diagnostic("face-tint-duplicate", DiagnosticSeverity.Error, "TETI option indexes must be unique; the upstream Add action filters duplicates."));
        for (var index = 0; index < patch.Layers.Length; index++)
        {
            var layer = patch.Layers[index];
            if (!Enum.IsDefined(layer.DataType))
            {
                diagnostics.Add(new Diagnostic("face-tint-type-invalid", DiagnosticSeverity.Error, $"Layer {index} has unsupported TETI discriminator {(ushort)layer.DataType}."));
                continue;
            }
            if (layer.Value > 100)
                diagnostics.Add(new Diagnostic("face-tint-value-invalid", DiagnosticSeverity.Error, $"Layer {index} value must be between 0 and 100."));
            byte[]? raw = null;
            if (layer.RawTendBase64 is not null)
            {
                if (layer.RawTendBase64.Length > 64)
                {
                    diagnostics.Add(new Diagnostic("face-tint-raw-too-large", DiagnosticSeverity.Error, $"Layer {index} RawTendBase64 exceeds the safety limit."));
                    continue;
                }
                try { raw = Convert.FromBase64String(layer.RawTendBase64); }
                catch (FormatException) { diagnostics.Add(new Diagnostic("face-tint-raw-invalid", DiagnosticSeverity.Error, $"Layer {index} RawTendBase64 is invalid.")); }
                if (raw is not null && raw.Length is not (1 or 5 or 7))
                    diagnostics.Add(new Diagnostic("face-tint-tend-length", DiagnosticSeverity.Error, $"Layer {index} TEND must be 1, 5, or 7 bytes."));
            }
            if (layer.DataType == NpcFaceTintDataType.TextureSet)
            {
                if (raw is null) diagnostics.Add(new Diagnostic("face-tint-textureset-raw-required", DiagnosticSeverity.Error, $"TextureSet layer {index} requires RawTendBase64 to preserve its TEND payload."));
                if (layer.Color is not null || layer.TemplateColorIndex is not null)
                    diagnostics.Add(new Diagnostic("face-tint-textureset-fields", DiagnosticSeverity.Error, $"TextureSet layer {index} cannot carry palette color fields."));
                continue;
            }
            var length = raw?.Length ?? 7;
            if (length >= 5 && layer.Color is null) diagnostics.Add(new Diagnostic("face-tint-color-required", DiagnosticSeverity.Error, $"ValueColor layer {index} requires Color for a {length}-byte TEND."));
            if (length < 5 && layer.Color is not null) diagnostics.Add(new Diagnostic("face-tint-color-unavailable", DiagnosticSeverity.Error, $"ValueColor layer {index} cannot carry Color in a one-byte TEND."));
            if (length >= 7 && layer.TemplateColorIndex is null) diagnostics.Add(new Diagnostic("face-tint-template-required", DiagnosticSeverity.Error, $"ValueColor layer {index} requires TemplateColorIndex for a seven-byte TEND."));
            if (length < 7 && layer.TemplateColorIndex is not null) diagnostics.Add(new Diagnostic("face-tint-template-unavailable", DiagnosticSeverity.Error, $"ValueColor layer {index} cannot carry TemplateColorIndex in a {length}-byte TEND."));
            if (layer.TemplateColorIndex is < -1) diagnostics.Add(new Diagnostic("face-tint-template-invalid", DiagnosticSeverity.Error, $"Layer {index} TemplateColorIndex must be -1 or non-negative."));
        }
    }

    private ImmutableArray<Diagnostic> ValidateRequest(NpcFaceTintPatchRequest request, bool requireExpectedHash,
        bool allowExistingProposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.Fallout4) diagnostics.Add(new Diagnostic("face-tint-game-unsupported", DiagnosticSeverity.Error, "Face tint patching is currently supported for Fallout 4 only."));
        if (!request.InputPlugin.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error, "Input plugins must remain under the K-only lab root."));
        if (!request.OutputPlugin.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("output-outside-lab", DiagnosticSeverity.Error, "Output plugins must remain under the K-only lab root."));
        if (!File.Exists(request.InputPlugin.Value)) diagnostics.Add(new Diagnostic("input-plugin-missing", DiagnosticSeverity.Error, "The input plugin does not exist."));
        var parent = Path.GetDirectoryName(request.OutputPlugin.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("output-parent-missing", DiagnosticSeverity.Error, "The output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputPlugin.Value)) diagnostics.Add(new Diagnostic("output-exists", DiagnosticSeverity.Error, "The output path already exists; face-tint patches never overwrite."));
        if (string.Equals(request.InputPlugin.Value, request.OutputPlugin.Value, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("input-output-same", DiagnosticSeverity.Error, "Input and output paths must differ."));
        if (!string.Equals(Path.GetFileName(request.InputPlugin.Value), Path.GetFileName(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("plugin-identity-mismatch", DiagnosticSeverity.Error, "Input and output must keep the same plugin filename."));
        if (!IsPluginPath(request.InputPlugin.Value) || !IsPluginPath(request.OutputPlugin.Value)) diagnostics.Add(new Diagnostic("plugin-extension-invalid", DiagnosticSeverity.Error, "Input and output must use .esp, .esm, or .esl."));
        if (requireExpectedHash && request.ExpectedInputHash is null) diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        if (request.ProposalPath is { } proposal)
        {
            if (!proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error, "Proposal paths must remain under the K-only lab root."));
            var proposalParent = Path.GetDirectoryName(proposal.Value);
            if (proposalParent is null || !Directory.Exists(proposalParent)) diagnostics.Add(new Diagnostic("proposal-parent-missing", DiagnosticSeverity.Error, "The proposal directory must already exist."));
            if (File.Exists(proposal.Value) && !allowExistingProposal) diagnostics.Add(new Diagnostic("proposal-exists", DiagnosticSeverity.Error, "Proposal paths never overwrite artifacts."));
            if (proposalParent is not null) AddReparseDiagnostic(diagnostics, proposalParent, "proposal-parent");
        }
        AddReparseDiagnostic(diagnostics, request.InputPlugin.Value, "input-plugin");
        if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        return diagnostics.ToImmutable();
    }

    private static string Describe(IEnumerable<NpcFaceTintLayer> layers) => JsonSerializer.Serialize(layers, ProposalOptions);
    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static bool IsPluginPath(string path) => Path.GetExtension(path) is ".esp" or ".esm" or ".esl";
    private static Sha256Hash ComputeHash(string path) { using var stream = File.OpenRead(path); return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()); }
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, $"The {role} could not be inspected: {exception.Message}")); return; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }
    private static void WriteProposal(WorkspacePath path, NpcFaceTintPatchProposal proposal, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(proposal, ProposalOptions), new UTF8Encoding(false)); File.Move(temporary, path.Value, overwrite: false); }
            finally { TryDelete(temporary); }
        }
        catch (Exception exception) { diagnostics.Add(new Diagnostic("proposal-write-failed", DiagnosticSeverity.Error, exception.Message)); }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
