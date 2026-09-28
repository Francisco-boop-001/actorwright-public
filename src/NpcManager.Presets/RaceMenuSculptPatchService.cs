using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>Hash-bound, loss-aware patcher for the persisted RaceMenu <c>morphs.sculpt</c>
/// blocks. It owns only the JSON sidecar representation: TRI topology, FaceGeom generation,
/// and rendering remain outside this service.</summary>
public sealed partial class RaceMenuSculptPatchService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IRaceMenuSculptPatchService
{
    private const int MaxParts = 64;
    private const int MaxVerticesPerPart = 200_000;
    private const int MaxHostLength = 512;
    private const int MaxDivisor = 1_000_000;
    private const float MaxCoordinate = 1_000F;
    private static readonly ImmutableArray<string> PreservedFields =
    [
        "all root JSON properties except morphs.sculpt and morphs.sculptDivisor",
        "all morphs properties except sculpt and sculptDivisor",
        "unknown JSON values and unrelated preset channels"
    ];
    private static readonly JsonSerializerOptions ProposalOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly JsonSerializerOptions DescribeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly Sha256Hash ZeroHash = new(new string('0', 64));

    public ValueTask<RaceMenuSculptPatchProposal> AnalyzeAsync(RaceMenuSculptPatchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, requireExpectedHash: false, allowExistingProposal: false).ToBuilder();
        var inputHash = File.Exists(request.InputPreset.Value) ? ComputeHash(request.InputPreset.Value) : ZeroHash;
        if (request.ExpectedInputHash is { } expected && expected != inputHash)
            diagnostics.Add(new Diagnostic("sculpt-input-hash-mismatch", DiagnosticSeverity.Error,
                $"Input hash {inputHash} does not match expected hash {expected}."));

        var changes = ImmutableArray.CreateBuilder<MutationChange>();
        if (!HasErrors(diagnostics))
        {
            try
            {
                var source = File.ReadAllBytes(request.InputPreset.Value);
                var current = ReadStoredSculpt(source, diagnostics);
                var desired = BuildDesired(request.Patch, diagnostics);
                if (!HasErrors(diagnostics) && !SemanticEqual(current, desired))
                    changes.Add(new MutationChange("morphs.sculpt", Describe(current), Describe(desired)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("sculpt-read-failed", DiagnosticSeverity.Error, exception.Message));
            }
        }
        if (request.Patch.Parts.Length == 0 && !HasErrors(diagnostics))
            diagnostics.Add(new Diagnostic("sculpt-no-applicable-shapes", DiagnosticSeverity.Error,
                "The sculpt patch must contain at least one applicable shape block."));
        else if (changes.Count == 0 && !HasErrors(diagnostics))
            diagnostics.Add(new Diagnostic("sculpt-noop", DiagnosticSeverity.Info,
                "The requested sculpt blocks already match the preset."));

        var proposal = new RaceMenuSculptPatchProposal(request.Edition, request.InputPreset, request.OutputPreset,
            inputHash, changes.ToImmutable(), PreservedFields, diagnostics.ToImmutable());
        if (request.ProposalPath is { } proposalPath && !HasErrors(diagnostics))
            WriteProposal(proposalPath, proposal, diagnostics);
        return ValueTask.FromResult(proposal with { Diagnostics = diagnostics.ToImmutable() });
    }

    public ValueTask<RaceMenuSculptPatchResult> ApplyAsync(RaceMenuSculptPatchRequest request,
        RaceMenuSculptPatchProposal proposal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = proposal.Diagnostics.ToBuilder();
        diagnostics.AddRange(ValidateRequest(request, requireExpectedHash: true, allowExistingProposal: true));
        var desired = BuildDesired(request.Patch, diagnostics);
        if (request.DryRun)
            diagnostics.Add(new Diagnostic("dry-run", DiagnosticSeverity.Info, "Dry-run requested; no preset was written."));
        if (File.Exists(request.InputPreset.Value) && proposal.InputHash != ComputeHash(request.InputPreset.Value))
            diagnostics.Add(new Diagnostic("sculpt-input-changed", DiagnosticSeverity.Error,
                "The input changed after sculpt analysis."));
        if (request.ExpectedInputHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error,
                "Apply requires --expected-sha256."));
        if (request.DryRun || HasErrors(diagnostics) || !proposal.IsApplicable)
            return ValueTask.FromResult(new RaceMenuSculptPatchResult(false, proposal, null, diagnostics.ToImmutable()));

        var temporary = request.OutputPreset.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var source = File.ReadAllBytes(request.InputPreset.Value);
            _ = ReadStoredSculpt(source, diagnostics);
            if (HasErrors(diagnostics))
                return ValueTask.FromResult(new RaceMenuSculptPatchResult(false, proposal, null, diagnostics.ToImmutable()));
            var output = WritePatchedDocument(source, desired, request.Patch.SculptDivisor, diagnostics);
            File.WriteAllBytes(temporary, output);
            using (var handle = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                handle.Flush(true);
            File.Move(temporary, request.OutputPreset.Value, overwrite: false);

            var after = ReadStoredSculpt(File.ReadAllBytes(request.OutputPreset.Value), diagnostics);
            if (HasErrors(diagnostics) || !SemanticEqual(desired, after))
            {
                TryDelete(request.OutputPreset.Value);
                diagnostics.Add(new Diagnostic("sculpt-mismatch", DiagnosticSeverity.Error,
                    "Output morphs.sculpt blocks do not match the requested typed values."));
                return ValueTask.FromResult(new RaceMenuSculptPatchResult(false, proposal, null, diagnostics.ToImmutable()));
            }
            return ValueTask.FromResult(new RaceMenuSculptPatchResult(true, proposal,
                ComputeHash(request.OutputPreset.Value), diagnostics.ToImmutable()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(request.OutputPreset.Value);
            diagnostics.Add(new Diagnostic("sculpt-write-failed", DiagnosticSeverity.Error, exception.Message));
            return ValueTask.FromResult(new RaceMenuSculptPatchResult(false, proposal, null, diagnostics.ToImmutable()));
        }
        finally
        {
            TryDelete(temporary);
        }
    }
}
