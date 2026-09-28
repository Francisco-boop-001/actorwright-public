using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Resolves a typed LVLI proposal with deterministic, preview-compatible RNG semantics.</summary>
public sealed class LeveledListResolveService(IWorkspacePolicy policy, WorkspacePath labRoot) : ILeveledListResolveService
{
    private const long MaxResolvedItems = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<LeveledListResolveResult> ResolveAsync(LeveledListResolveRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (!File.Exists(request.ListProposal.Value)) diagnostics.Add(new Diagnostic("leveled-list-resolve-source-missing",
            DiagnosticSeverity.Error, "The leveled-list proposal does not exist."));
        else
        {
            try
            {
                if ((File.GetAttributes(request.ListProposal.Value) & FileAttributes.ReparsePoint) != 0)
                    diagnostics.Add(new Diagnostic("leveled-list-resolve-source-reparse", DiagnosticSeverity.Error,
                        "The leveled-list proposal may not be a reparse point."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { diagnostics.Add(new Diagnostic("leveled-list-resolve-source-attributes-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.ListProposal.Value);
            if (info.Length > 8 * 1024 * 1024)
            {
                diagnostics.Add(new Diagnostic("leveled-list-resolve-source-size", DiagnosticSeverity.Error,
                    "Leveled-list proposals may not exceed 8 MiB."));
                return Refused(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(request.ListProposal.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { diagnostics.Add(new Diagnostic("leveled-list-resolve-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }

        var inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        LeveledListProposalArtifact? proposal;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            proposal = JsonSerializer.Deserialize<LeveledListProposalArtifact>(document.RootElement.GetRawText(), JsonOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("leveled-list-resolve-source-json-invalid", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics, inputHash);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);
        if (proposal is null || proposal.ArtifactKind != "leveled-list-record-proposal" || proposal.SchemaVersion != "1")
        {
            diagnostics.Add(new Diagnostic("leveled-list-resolve-source-kind", DiagnosticSeverity.Error,
                "The input must be a schema-1 leveled-list record proposal."));
            return Refused(diagnostics, inputHash);
        }
        var pluginIsUnderLab = false;
        try { pluginIsUnderLab = Path.IsPathFullyQualified(proposal.SourcePlugin) && new WorkspacePath(proposal.SourcePlugin).IsUnder(labRoot); }
        catch (ArgumentException) { }
        if (!pluginIsUnderLab)
            diagnostics.Add(new Diagnostic("leveled-list-resolve-plugin-outside-lab", DiagnosticSeverity.Error,
                "The proposal's source plugin must remain under the K-only lab root."));
        else if (!File.Exists(proposal.SourcePlugin))
            diagnostics.Add(new Diagnostic("leveled-list-resolve-plugin-missing", DiagnosticSeverity.Error,
                "The proposal's source plugin does not exist."));
        else
        {
            try
            {
                if ((File.GetAttributes(proposal.SourcePlugin) & FileAttributes.ReparsePoint) != 0)
                    diagnostics.Add(new Diagnostic("leveled-list-resolve-plugin-reparse", DiagnosticSeverity.Error,
                        "The proposal's source plugin may not be a reparse point."));
                else
                {
                    using var stream = File.OpenRead(proposal.SourcePlugin);
                    var sourceHash = Convert.ToHexString(SHA256.HashData(stream));
                    if (!string.Equals(sourceHash, proposal.InputSha256, StringComparison.OrdinalIgnoreCase))
                        diagnostics.Add(new Diagnostic("leveled-list-resolve-plugin-stale", DiagnosticSeverity.Error,
                            "The source plugin hash no longer matches the proposal."));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { diagnostics.Add(new Diagnostic("leveled-list-resolve-plugin-read-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (!string.Equals(proposal.Edition, request.Edition.ToWireName(), StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("leveled-list-resolve-edition-mismatch", DiagnosticSeverity.Error,
                "The proposal edition does not match the requested game."));
        if (proposal.Entries.IsDefaultOrEmpty || proposal.Entries.Length > 4096)
            diagnostics.Add(new Diagnostic("leveled-list-resolve-entry-count", DiagnosticSeverity.Error,
                "The proposal must contain 1 to 4096 entries."));
        if (proposal.ChanceNone > 100 || proposal.Entries.Any(entry => entry.ChanceNone > 100 || entry.Level == 0 || entry.Count == 0))
            diagnostics.Add(new Diagnostic("leveled-list-resolve-entry-range", DiagnosticSeverity.Error,
                "Chance-none values must be 0..100 and entry levels/counts must be non-zero."));
        var maximumSelections = proposal.Entries.IsDefaultOrEmpty ? 0L : proposal.UseAll
            ? proposal.Entries.Sum(entry => (long)(proposal.CalculateEachInCount ? entry.Count : 1))
            : proposal.CalculateEachInCount ? proposal.Entries.Max(entry => (long)entry.Count) : 1L;
        if (maximumSelections > MaxResolvedItems)
            diagnostics.Add(new Diagnostic("leveled-list-resolve-output-limit", DiagnosticSeverity.Error,
                $"A deterministic resolution may emit at most {MaxResolvedItems} selected items."));
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);

        var rng = new SplitMix64(unchecked((ulong)request.Seed));
        int? listRoll = proposal.ChanceNone > 0 ? rng.NextPercent() : null;
        var listSuppressed = listRoll is { } roll && roll < proposal.ChanceNone;
        var selections = ImmutableArray.CreateBuilder<LeveledListResolutionSelection>();
        var resolved = ImmutableArray.CreateBuilder<string>();
        if (!listSuppressed)
        {
            if (proposal.UseAll)
            {
                for (var index = 0; index < proposal.Entries.Length; index++)
                    ResolveEntry(proposal.Entries[index], index, rng, proposal.CalculateEachInCount, selections, resolved);
            }
            else
            {
                var index = rng.NextIndex(proposal.Entries.Length);
                ResolveEntry(proposal.Entries[index], index, rng, proposal.CalculateEachInCount, selections, resolved);
            }
        }
        var artifact = new LeveledListResolveArtifact("1", "leveled-list-resolution", request.Edition.ToWireName(),
            request.ListProposal.Value, inputHash.Value, proposal.ListFormId, proposal.EditorId, request.Seed,
            proposal.ChanceNone, proposal.MaxCount, proposal.CalculateAllLevels, proposal.CalculateEachInCount,
            proposal.UseAll, false, "Preview has no player-level input; entry Level and CalculateAllLevels are preserved but not gate-applied.",
            listRoll, listSuppressed, selections.ToImmutable(), resolved.ToImmutable());
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputResolution.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, outputBytes, cancellationToken);
            File.Move(temporary, request.OutputResolution.Value, overwrite: false);
            return new LeveledListResolveResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(outputBytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { TryDelete(temporary); diagnostics.Add(new Diagnostic("leveled-list-resolve-write-failed", DiagnosticSeverity.Error, exception.Message)); return new LeveledListResolveResult(false, artifact, null, diagnostics.ToImmutable()); }
    }

    private static void ResolveEntry(LeveledListEntryArtifact entry, int index, SplitMix64 rng, bool calculateEach,
        ImmutableArray<LeveledListResolutionSelection>.Builder selections, ImmutableArray<string>.Builder resolved)
    {
        int? chanceRoll = entry.ChanceNone > 0 ? rng.NextPercent() : null;
        var included = chanceRoll is null || chanceRoll.Value >= entry.ChanceNone;
        var repetitions = included && calculateEach && entry.Count > 1 ? entry.Count : included ? 1 : 0;
        var recorded = Math.Max(1, repetitions);
        for (var ordinal = 0; ordinal < recorded; ordinal++)
        {
            selections.Add(new LeveledListResolutionSelection(entry.Item, entry.Level, entry.Count, entry.ChanceNone,
                chanceRoll, included, index, ordinal));
            if (included && ordinal < repetitions) resolved.Add(entry.Item);
        }
    }

    private ImmutableArray<Diagnostic> ValidatePaths(LeveledListResolveRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.ListProposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("leveled-list-resolve-source-outside-lab", DiagnosticSeverity.Error,
            "Leveled-list resolution inputs must remain under the K-only lab root."));
        if (!request.OutputResolution.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("leveled-list-resolve-output-outside-lab", DiagnosticSeverity.Error,
            "Leveled-list resolution outputs must remain under the K-only lab root."));
        if (!request.ListProposal.Value.EndsWith(".leveled-list-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("leveled-list-resolve-source-extension", DiagnosticSeverity.Error,
            "Resolution inputs must use the .leveled-list-proposal.json extension."));
        if (!request.OutputResolution.Value.EndsWith(".leveled-list-resolution.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("leveled-list-resolve-output-extension", DiagnosticSeverity.Error,
            "Resolution outputs must use the .leveled-list-resolution.json extension."));
        if (File.Exists(request.OutputResolution.Value)) diagnostics.Add(new Diagnostic("leveled-list-resolve-output-exists", DiagnosticSeverity.Error,
            "Resolution outputs never overwrite existing artifacts."));
        var parent = Path.GetDirectoryName(request.OutputResolution.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("leveled-list-resolve-output-parent-missing", DiagnosticSeverity.Error,
            "The resolution output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static void ValidateDuplicateProperties(JsonElement element, string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) diagnostics.Add(new Diagnostic("leveled-list-resolve-duplicate-property",
                    DiagnosticSeverity.Error, $"Duplicate JSON property '{path}.{property.Name}'."));
                ValidateDuplicateProperties(property.Value, $"{path}.{property.Name}", diagnostics);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in element.EnumerateArray()) ValidateDuplicateProperties(child, $"{path}[{index++}]", diagnostics);
        }
    }
    private static LeveledListResolveResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics, Sha256Hash? hash = null) => new(false, null, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed class SplitMix64(ulong state)
    {
        private ulong _state = state;
        public ulong Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
        public int NextPercent() => (int)(Next() % 100UL);
        public int NextIndex(int count) => (int)(Next() % (ulong)count);
    }
}
