using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>Hash-bound, lossless JSON patcher for Skyrim RaceMenu custom morphs. Only the
/// customMorphs property is rewritten; unknown root fields and all other preset data are copied
/// through the JSON reader unchanged in meaning.</summary>
public sealed class RaceMenuExtendedMorphPatchService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IRaceMenuExtendedMorphPatchService
{
    private const int MaxMorphs = 2048;
    private const int MaxNameLength = 256;
    private static readonly ImmutableArray<string> PreservedFields = [
        "all root JSON properties except customMorphs", "custom morph names not selected by the patch",
        "customMorphs property order and non-morph JSON values"];
    private static readonly JsonSerializerOptions ProposalOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Sha256Hash ZeroHash = new(new string('0', 64));

    public ValueTask<RaceMenuExtendedMorphPatchProposal> AnalyzeAsync(RaceMenuExtendedMorphPatchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, false, false).ToBuilder();
        var inputHash = File.Exists(request.InputPreset.Value) ? ComputeHash(request.InputPreset.Value) : ZeroHash;
        if (request.ExpectedInputHash is { } expected && expected != inputHash)
            diagnostics.Add(new Diagnostic("extended-morph-input-hash-mismatch", DiagnosticSeverity.Error,
                $"Input hash {inputHash} does not match expected hash {expected}."));

        var changes = ImmutableArray.CreateBuilder<MutationChange>();
        if (!HasErrors(diagnostics))
        {
            try
            {
                var source = File.ReadAllBytes(request.InputPreset.Value);
                var current = ReadCustomMorphs(source, diagnostics);
                var desired = ApplyPatch(current, request.Patch, diagnostics, reportUnknown: true);
                if (!HasErrors(diagnostics) && !SemanticEqual(current, desired))
                    changes.Add(new MutationChange("customMorphs", Describe(current), Describe(desired)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { diagnostics.Add(new Diagnostic("extended-morph-read-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (changes.Count == 0 && !HasErrors(diagnostics))
            diagnostics.Add(new Diagnostic("extended-morph-noop", DiagnosticSeverity.Info, "The requested custom morph values already match the preset."));

        var proposal = new RaceMenuExtendedMorphPatchProposal(request.Edition, request.InputPreset, request.OutputPreset,
            inputHash, changes.ToImmutable(), PreservedFields, diagnostics.ToImmutable());
        if (request.ProposalPath is { } proposalPath && !HasErrors(diagnostics)) WriteProposal(proposalPath, proposal, diagnostics);
        return ValueTask.FromResult(proposal with { Diagnostics = diagnostics.ToImmutable() });
    }

    public ValueTask<RaceMenuExtendedMorphPatchResult> ApplyAsync(RaceMenuExtendedMorphPatchRequest request,
        RaceMenuExtendedMorphPatchProposal proposal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = proposal.Diagnostics.ToBuilder();
        diagnostics.AddRange(ValidateRequest(request, true, true));
        ValidatePatch(request.Patch, diagnostics);
        if (request.DryRun) diagnostics.Add(new Diagnostic("dry-run", DiagnosticSeverity.Info, "Dry-run requested; no preset was written."));
        if (File.Exists(request.InputPreset.Value) && proposal.InputHash != ComputeHash(request.InputPreset.Value))
            diagnostics.Add(new Diagnostic("extended-morph-input-changed", DiagnosticSeverity.Error, "The input changed after extended-morph analysis."));
        if (request.ExpectedInputHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        if (request.DryRun || HasErrors(diagnostics) || !proposal.IsApplicable)
            return ValueTask.FromResult(new RaceMenuExtendedMorphPatchResult(false, proposal, null, diagnostics.ToImmutable()));

        var temporary = request.OutputPreset.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var source = File.ReadAllBytes(request.InputPreset.Value);
            var current = ReadCustomMorphs(source, diagnostics);
            var desired = ApplyPatch(current, request.Patch, diagnostics, reportUnknown: false);
            if (HasErrors(diagnostics)) return ValueTask.FromResult(new RaceMenuExtendedMorphPatchResult(false, proposal, null, diagnostics.ToImmutable()));
            var output = WritePatchedDocument(source, desired, diagnostics);
            File.WriteAllBytes(temporary, output);
            using (var handle = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read)) handle.Flush(true);
            File.Move(temporary, request.OutputPreset.Value, overwrite: false);
            var after = ReadCustomMorphs(File.ReadAllBytes(request.OutputPreset.Value), diagnostics);
            if (HasErrors(diagnostics) || !SemanticEqual(desired, after))
            {
                TryDelete(request.OutputPreset.Value);
                diagnostics.Add(new Diagnostic("extended-morph-mismatch", DiagnosticSeverity.Error, "Output customMorphs do not match the requested typed values."));
                return ValueTask.FromResult(new RaceMenuExtendedMorphPatchResult(false, proposal, null, diagnostics.ToImmutable()));
            }
            return ValueTask.FromResult(new RaceMenuExtendedMorphPatchResult(true, proposal, ComputeHash(request.OutputPreset.Value), diagnostics.ToImmutable()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(request.OutputPreset.Value);
            diagnostics.Add(new Diagnostic("extended-morph-write-failed", DiagnosticSeverity.Error, exception.Message));
            return ValueTask.FromResult(new RaceMenuExtendedMorphPatchResult(false, proposal, null, diagnostics.ToImmutable()));
        }
        finally { TryDelete(temporary); }
    }

    private static Dictionary<string, float> ReadCustomMorphs(byte[] source, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!PresetJsonSupport.TryParse(source, out var document, out var parseDiagnostics) || document is null)
        {
            diagnostics.AddRange(parseDiagnostics); throw new InvalidDataException("RaceMenu preset JSON is invalid.");
        }
        diagnostics.AddRange(parseDiagnostics);
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("RaceMenu preset root must be an object.");
            if (!PresetJsonSupport.TryGet(document.RootElement, "customMorphs", out var value)) return new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("customMorphs must be an array.");
            var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !PresetJsonSupport.TryGet(item, "name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String ||
                    !PresetJsonSupport.TryGet(item, "value", out var valueElement) || !PresetJsonSupport.TryReadFloat(valueElement, $"$.customMorphs[{index}].value", out var parsed, diagnostics))
                    throw new InvalidDataException($"customMorphs[{index}] requires a name and finite value.");
                var name = nameElement.GetString()!;
                if (!IsNameValid(name)) throw new InvalidDataException($"customMorphs[{index}] has an invalid name.");
                if (parsed < -1F || parsed > 1F) throw new InvalidDataException($"customMorphs[{index}] value must be between -1 and 1.");
                if (!result.TryAdd(name, parsed)) throw new InvalidDataException($"customMorphs contains duplicate name '{name}'.");
                index++;
            }
            return result;
        }
    }

    private static Dictionary<string, float> ApplyPatch(Dictionary<string, float> current, RaceMenuExtendedMorphPatch patch,
        ImmutableArray<Diagnostic>.Builder diagnostics, bool reportUnknown)
    {
        ValidatePatch(patch, diagnostics);
        var desired = new Dictionary<string, float>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var morph in patch.Morphs)
        {
            var existing = desired.Keys.FirstOrDefault(name => string.Equals(name, morph.Name, StringComparison.OrdinalIgnoreCase));
            if (reportUnknown && existing is null && Math.Abs(morph.Value) >= 0.0001F)
                diagnostics.Add(new Diagnostic("extended-morph-uncatalogued", DiagnosticSeverity.Warning,
                    $"RaceMenu morph '{morph.Name}' is not present in the source preset; it is preserved as a direct name/value channel."));
            if (Math.Abs(morph.Value) < 0.0001F)
            {
                if (existing is not null) desired.Remove(existing);
            }
            else desired[existing ?? morph.Name] = morph.Value;
        }
        return desired;
    }

    private static byte[] WritePatchedDocument(byte[] source, Dictionary<string, float> desired,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!PresetJsonSupport.TryParse(source, out var document, out var parseDiagnostics) || document is null)
        { diagnostics.AddRange(parseDiagnostics); throw new InvalidDataException("RaceMenu preset JSON is invalid."); }
        diagnostics.AddRange(parseDiagnostics);
        using (document)
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject(); var found = false;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "customMorphs", StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WritePropertyName(property.Name); WriteMorphArray(writer, desired); found = true;
                    }
                    else { writer.WritePropertyName(property.Name); property.Value.WriteTo(writer); }
                }
                if (!found) { writer.WritePropertyName("customMorphs"); WriteMorphArray(writer, desired); }
                writer.WriteEndObject(); writer.Flush();
            }
            return stream.ToArray();
        }
    }

    private static void WriteMorphArray(Utf8JsonWriter writer, Dictionary<string, float> morphs)
    {
        writer.WriteStartArray();
        foreach (var morph in morphs.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject(); writer.WriteString("name", morph.Key); writer.WriteNumber("value", morph.Value); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void ValidatePatch(RaceMenuExtendedMorphPatch patch, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch.Morphs.Length > MaxMorphs) diagnostics.Add(new Diagnostic("extended-morph-count", DiagnosticSeverity.Error, $"A patch may contain at most {MaxMorphs} morphs."));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var morph in patch.Morphs)
        {
            if (!IsNameValid(morph.Name)) diagnostics.Add(new Diagnostic("extended-morph-name", DiagnosticSeverity.Error, "Morph names must be non-empty, printable, and at most 256 characters."));
            else if (!names.Add(morph.Name)) diagnostics.Add(new Diagnostic("extended-morph-duplicate", DiagnosticSeverity.Error, $"Duplicate morph name '{morph.Name}'."));
            if (!float.IsFinite(morph.Value)) diagnostics.Add(new Diagnostic("extended-morph-nonfinite", DiagnosticSeverity.Error, $"Morph '{morph.Name}' must be finite."));
            else if (morph.Value < -1F || morph.Value > 1F) diagnostics.Add(new Diagnostic("extended-morph-range", DiagnosticSeverity.Error, $"Morph '{morph.Name}' must be between -1 and 1."));
        }
    }

    private ImmutableArray<Diagnostic> ValidateRequest(RaceMenuExtendedMorphPatchRequest request, bool requireExpectedHash, bool allowExistingProposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition) diagnostics.Add(new Diagnostic("extended-morph-game-unsupported", DiagnosticSeverity.Error, "RaceMenu extended morphs are supported for Skyrim SE only."));
        if (!request.InputPreset.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error, "Preset inputs must remain under the K-only lab root."));
        if (!request.OutputPreset.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("output-outside-lab", DiagnosticSeverity.Error, "Preset outputs must remain under the K-only lab root."));
        if (!File.Exists(request.InputPreset.Value)) diagnostics.Add(new Diagnostic("input-preset-missing", DiagnosticSeverity.Error, "The input .jslot does not exist."));
        if (!string.Equals(Path.GetExtension(request.InputPreset.Value), ".jslot", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("input-extension-invalid", DiagnosticSeverity.Error, "The input must use the .jslot extension."));
        if (!string.Equals(Path.GetExtension(request.OutputPreset.Value), ".jslot", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("output-extension-invalid", DiagnosticSeverity.Error, "The output must use the .jslot extension."));
        var inputParent = Path.GetDirectoryName(request.InputPreset.Value);
        if (inputParent is not null && Directory.Exists(inputParent)) diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(inputParent)));
        var parent = Path.GetDirectoryName(request.OutputPreset.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("output-parent-missing", DiagnosticSeverity.Error, "The output directory must already exist.")); else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputPreset.Value)) diagnostics.Add(new Diagnostic("output-exists", DiagnosticSeverity.Error, "Extended morph patches never overwrite."));
        if (string.Equals(request.InputPreset.Value, request.OutputPreset.Value, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("input-output-same", DiagnosticSeverity.Error, "Input and output paths must differ."));
        if (!string.Equals(Path.GetFileName(request.InputPreset.Value), Path.GetFileName(request.OutputPreset.Value), StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("preset-identity-mismatch", DiagnosticSeverity.Error, "Input and output must keep the same .jslot filename."));
        if (requireExpectedHash && request.ExpectedInputHash is null) diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        if (request.ProposalPath is { } proposal)
        {
            if (!proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error, "Proposal paths must remain under the K-only lab root."));
            var proposalParent = Path.GetDirectoryName(proposal.Value); if (proposalParent is null || !Directory.Exists(proposalParent)) diagnostics.Add(new Diagnostic("proposal-parent-missing", DiagnosticSeverity.Error, "The proposal directory must already exist."));
            if (File.Exists(proposal.Value) && !allowExistingProposal) diagnostics.Add(new Diagnostic("proposal-exists", DiagnosticSeverity.Error, "Proposal paths never overwrite artifacts."));
            if (proposalParent is not null) AddReparseDiagnostic(diagnostics, proposalParent, "proposal-parent");
        }
        AddReparseDiagnostic(diagnostics, request.InputPreset.Value, "input-preset"); if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        return diagnostics.ToImmutable();
    }

    private static bool IsNameValid(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= MaxNameLength && name.All(character => !char.IsControl(character));
    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static bool SemanticEqual(Dictionary<string, float> left, Dictionary<string, float> right) => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private static string Describe(Dictionary<string, float> morphs) => JsonSerializer.Serialize(morphs.OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Value), ProposalOptions);
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
    private static void WriteProposal(WorkspacePath path, RaceMenuExtendedMorphPatchProposal proposal, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try { var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N"); try { File.WriteAllText(temporary, JsonSerializer.Serialize(proposal, ProposalOptions), new UTF8Encoding(false)); using (var handle = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read)) handle.Flush(true); File.Move(temporary, path.Value, overwrite: false); } finally { TryDelete(temporary); } }
        catch (Exception exception) { diagnostics.Add(new Diagnostic("proposal-write-failed", DiagnosticSeverity.Error, exception.Message)); }
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
