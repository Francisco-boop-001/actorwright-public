using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

public sealed partial class RaceMenuSculptPatchService
{
    private static int ToRaw(float value, int divisor)
    {
        if (!float.IsFinite(value)) return 0;
        var scaled = (double)value * divisor;
        if (scaled < int.MinValue || scaled > int.MaxValue) return 0;
        return (int)Math.Round(scaled, MidpointRounding.ToEven);
    }

    private ImmutableArray<Diagnostic> ValidateRequest(RaceMenuSculptPatchRequest request, bool requireExpectedHash,
        bool allowExistingProposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(new Diagnostic("sculpt-game-unsupported", DiagnosticSeverity.Error,
                "RaceMenu sculpt patches are supported for Skyrim SE only."));
        if (!request.InputPreset.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error,
                "Preset inputs must remain under the K-only lab root."));
        if (!request.OutputPreset.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("output-outside-lab", DiagnosticSeverity.Error,
                "Preset outputs must remain under the K-only lab root."));
        if (!File.Exists(request.InputPreset.Value))
            diagnostics.Add(new Diagnostic("input-preset-missing", DiagnosticSeverity.Error, "The input .jslot does not exist."));
        if (!string.Equals(Path.GetExtension(request.InputPreset.Value), ".jslot", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("input-extension-invalid", DiagnosticSeverity.Error, "The input must use the .jslot extension."));
        if (!string.Equals(Path.GetExtension(request.OutputPreset.Value), ".jslot", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("output-extension-invalid", DiagnosticSeverity.Error, "The output must use the .jslot extension."));
        var inputParent = Path.GetDirectoryName(request.InputPreset.Value);
        if (inputParent is not null && Directory.Exists(inputParent))
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(inputParent)));
        var parent = Path.GetDirectoryName(request.OutputPreset.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("output-parent-missing", DiagnosticSeverity.Error, "The output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputPreset.Value))
            diagnostics.Add(new Diagnostic("output-exists", DiagnosticSeverity.Error, "Sculpt patches never overwrite."));
        if (string.Equals(request.InputPreset.Value, request.OutputPreset.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("input-output-same", DiagnosticSeverity.Error, "Input and output paths must differ."));
        if (!string.Equals(Path.GetFileName(request.InputPreset.Value), Path.GetFileName(request.OutputPreset.Value), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("preset-identity-mismatch", DiagnosticSeverity.Error, "Input and output must keep the same .jslot filename."));
        if (requireExpectedHash && request.ExpectedInputHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        if (request.ProposalPath is { } proposal)
        {
            if (!proposal.IsUnder(labRoot))
                diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error, "Proposal paths must remain under the K-only lab root."));
            var proposalParent = Path.GetDirectoryName(proposal.Value);
            if (proposalParent is null || !Directory.Exists(proposalParent))
                diagnostics.Add(new Diagnostic("proposal-parent-missing", DiagnosticSeverity.Error, "The proposal directory must already exist."));
            if (File.Exists(proposal.Value) && !allowExistingProposal)
                diagnostics.Add(new Diagnostic("proposal-exists", DiagnosticSeverity.Error, "Proposal paths never overwrite artifacts."));
            if (proposalParent is not null) AddReparseDiagnostic(diagnostics, proposalParent, "proposal-parent");
        }
        AddReparseDiagnostic(diagnostics, request.InputPreset.Value, "input-preset");
        if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        return diagnostics.ToImmutable();
    }

    private static bool SemanticEqual(ImmutableArray<StoredSculptPart> left, ImmutableArray<StoredSculptPart> right) =>
        left.Length == right.Length && left.Zip(right).All(pair =>
            string.Equals(pair.First.Host, pair.Second.Host, StringComparison.Ordinal) &&
            pair.First.VertexCount == pair.Second.VertexCount &&
            pair.First.Divisor == pair.Second.Divisor &&
            pair.First.Vertices.SequenceEqual(pair.Second.Vertices));

    private static string Describe(ImmutableArray<StoredSculptPart> parts) => JsonSerializer.Serialize(parts, DescribeOptions);

    private static void RejectUnknown(JsonElement value, string[] known, string path)
    {
        var allowed = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"{path} contains unknown field '{property.Name}'.");
    }

    private static bool TryGet(JsonElement value, string name, out JsonElement property)
    {
        if (value.TryGetProperty(name, out property)) return true;
        foreach (var item in value.EnumerateObject())
            if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) { property = item.Value; return true; }
        property = default; return false;
    }

    private static bool TryReadInt32(JsonElement value, out int result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result);
    }

    private static bool TryReadInt64(JsonElement value, out long result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result);
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

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
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
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

    private static void WriteProposal(WorkspacePath path, RaceMenuSculptPatchProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(proposal, ProposalOptions), new UTF8Encoding(false));
                using (var handle = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read)) handle.Flush(true);
                File.Move(temporary, path.Value, overwrite: false);
            }
            finally { TryDelete(temporary); }
        }
        catch (Exception exception) { diagnostics.Add(new Diagnostic("proposal-write-failed", DiagnosticSeverity.Error, exception.Message)); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record StoredSculptVertex(int Index, int Dx, int Dy, int Dz);
    private sealed record StoredSculptPart(string Host, long? VertexCount, ImmutableArray<StoredSculptVertex> Vertices,
        bool HadData, int Divisor);
}
