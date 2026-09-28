using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>
/// Resets one face-editor section from an explicit construction snapshot. The pinned upstream
/// editor keeps this state in an in-memory overlay; this service gives that behavior a safe,
/// hash-bound CLI artifact without pretending the artifact is a plugin, .jslot, or runtime proof.
/// </summary>
public sealed class FaceSectionResetService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IFaceResetService
{
    private const int SchemaVersion = 1;
    private const int MaxSnapshotBytes = 4 * 1024 * 1024;
    private static readonly Sha256Hash ZeroHash = new(new string('0', 64));
    private static readonly JsonSerializerOptions ProposalOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly ImmutableArray<string> PreservedFields =
    [
        "schemaVersion, game, and npcFormId metadata",
        "all root properties except the selected face section",
        "all unrelated face sections and unknown JSON values"
    ];

    public ValueTask<FaceResetProposal> AnalyzeAsync(FaceResetRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, requireExpectedHash: false, allowExistingProposal: false).ToBuilder();
        var currentHash = HashOrZero(request.CurrentSnapshot.Value, diagnostics, "current-snapshot");
        var baselineHash = HashOrZero(request.BaselineSnapshot.Value, diagnostics, "baseline-snapshot");
        if (request.ExpectedCurrentHash is { } expected && expected != currentHash)
            diagnostics.Add(new Diagnostic("face-reset-input-hash-mismatch", DiagnosticSeverity.Error,
                $"Current snapshot hash {currentHash} does not match expected hash {expected}."));

        FaceSnapshot? current = null;
        FaceSnapshot? baseline = null;
        if (!HasErrors(diagnostics))
        {
            current = ReadSnapshot(request.CurrentSnapshot.Value, request, diagnostics, "current");
            baseline = ReadSnapshot(request.BaselineSnapshot.Value, request, diagnostics, "baseline");
        }

        var changes = ImmutableArray.CreateBuilder<MutationChange>();
        if (current is not null && baseline is not null && !HasErrors(diagnostics))
        {
            var currentValue = current.Find(request.Section.ToWireName());
            var baselineValue = baseline.Find(request.Section.ToWireName());
            if (!JsonValuesEqual(currentValue, baselineValue))
            {
                changes.Add(new MutationChange($"face.{request.Section.ToWireName()}",
                    Describe(currentValue), Describe(baselineValue)));
            }
            else
            {
                diagnostics.Add(new Diagnostic("face-reset-noop", DiagnosticSeverity.Info,
                    $"Face section '{request.Section.ToWireName()}' already matches the baseline."));
            }
        }

        var proposal = new FaceResetProposal(request.Edition, request.NpcFormId, request.CurrentSnapshot,
            request.BaselineSnapshot, request.OutputSnapshot, request.Section, currentHash, baselineHash,
            changes.ToImmutable(), PreservedFields, diagnostics.ToImmutable());
        if (request.ProposalPath is { } proposalPath && !HasErrors(diagnostics))
            WriteProposal(proposalPath, proposal, diagnostics);
        return ValueTask.FromResult(proposal with { Diagnostics = diagnostics.ToImmutable() });
    }

    public ValueTask<FaceResetResult> ApplyAsync(FaceResetRequest request, FaceResetProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = proposal.Diagnostics.ToBuilder();
        diagnostics.AddRange(ValidateRequest(request, requireExpectedHash: true, allowExistingProposal: true));
        if (request.DryRun)
            diagnostics.Add(new Diagnostic("dry-run", DiagnosticSeverity.Info,
                "Dry-run requested; no face snapshot was written."));
        if (request.ExpectedCurrentHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error,
                "Apply requires --expected-sha256 to bind the reset to an observed snapshot."));
        if (!File.Exists(request.CurrentSnapshot.Value) ||
            HashOrZero(request.CurrentSnapshot.Value, diagnostics, "current-snapshot") != proposal.CurrentHash)
            diagnostics.Add(new Diagnostic("face-reset-input-changed", DiagnosticSeverity.Error,
                "The current face snapshot changed after reset analysis."));
        if (!File.Exists(request.BaselineSnapshot.Value) ||
            HashOrZero(request.BaselineSnapshot.Value, diagnostics, "baseline-snapshot") != proposal.BaselineHash)
            diagnostics.Add(new Diagnostic("face-reset-baseline-changed", DiagnosticSeverity.Error,
                "The baseline face snapshot changed after reset analysis."));
        if (proposal.Section != request.Section || proposal.NpcFormId != request.NpcFormId ||
            proposal.Edition != request.Edition ||
            !SamePath(proposal.CurrentSnapshot, request.CurrentSnapshot) ||
            !SamePath(proposal.BaselineSnapshot, request.BaselineSnapshot) ||
            !SamePath(proposal.OutputSnapshot, request.OutputSnapshot))
            diagnostics.Add(new Diagnostic("face-reset-proposal-mismatch", DiagnosticSeverity.Error,
                "The face reset proposal is not bound to this request."));

        if (request.DryRun || HasErrors(diagnostics) || !proposal.IsApplicable)
            return ValueTask.FromResult(new FaceResetResult(false, proposal, null, diagnostics.ToImmutable()));

        FaceSnapshot? current = null;
        FaceSnapshot? baseline = null;
        try
        {
            current = ReadSnapshot(request.CurrentSnapshot.Value, request, diagnostics, "current");
            baseline = ReadSnapshot(request.BaselineSnapshot.Value, request, diagnostics, "baseline");
            if (current is null || baseline is null || HasErrors(diagnostics))
                return ValueTask.FromResult(new FaceResetResult(false, proposal, null, diagnostics.ToImmutable()));

            var bytes = WriteSnapshot(current, baseline, request.Section);
            var temporary = request.OutputSnapshot.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                WriteBytes(temporary, bytes);
                File.Move(temporary, request.OutputSnapshot.Value, overwrite: false);
            }
            finally
            {
                TryDelete(temporary);
            }

            var output = ReadSnapshot(request.OutputSnapshot.Value, request, diagnostics, "output");
            if (output is null || HasErrors(diagnostics) || !MatchesReset(current, baseline, output, request.Section))
            {
                TryDelete(request.OutputSnapshot.Value);
                diagnostics.Add(new Diagnostic("face-reset-output-mismatch", DiagnosticSeverity.Error,
                    "The output face snapshot did not reset only the requested section."));
                return ValueTask.FromResult(new FaceResetResult(false, proposal, null, diagnostics.ToImmutable()));
            }

            return ValueTask.FromResult(new FaceResetResult(true, proposal,
                HashOrZero(request.OutputSnapshot.Value, diagnostics, "output-snapshot"), diagnostics.ToImmutable()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TryDelete(request.OutputSnapshot.Value);
            diagnostics.Add(new Diagnostic("face-reset-write-failed", DiagnosticSeverity.Error, exception.Message));
            return ValueTask.FromResult(new FaceResetResult(false, proposal, null, diagnostics.ToImmutable()));
        }
    }

    private static FaceSnapshot? ReadSnapshot(string path, FaceResetRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics, string role)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxSnapshotBytes)
            {
                diagnostics.Add(new Diagnostic("face-reset-size-limit", DiagnosticSeverity.Error,
                    $"The {role} snapshot exceeds the {MaxSnapshotBytes} byte safety limit."));
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > MaxSnapshotBytes)
            {
                diagnostics.Add(new Diagnostic("face-reset-size-limit", DiagnosticSeverity.Error,
                    $"The {role} snapshot grew beyond the {MaxSnapshotBytes} byte safety limit while reading."));
                return null;
            }

            if (!PresetJsonSupport.TryParse(bytes, out var document, out var parseDiagnostics) || document is null)
            {
                diagnostics.AddRange(parseDiagnostics);
                return null;
            }

            diagnostics.AddRange(parseDiagnostics);
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Face snapshot root must be a JSON object.");
                if (!TryGet(root, "schemaVersion", out var schema) ||
                    schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) ||
                    version != SchemaVersion)
                    throw new InvalidDataException($"Face snapshot schemaVersion must be {SchemaVersion}.");
                if (!TryGet(root, "game", out var game) || game.ValueKind != JsonValueKind.String ||
                    !GameEditionExtensions.TryParseWireName(game.GetString()!, out var parsedGame) ||
                    parsedGame != request.Edition)
                    throw new InvalidDataException("Face snapshot game does not match the request.");
                if (!TryGet(root, "npcFormId", out var npc) || npc.ValueKind != JsonValueKind.String ||
                    !FormId.TryParse(npc.GetString()!, out var parsedNpc) || parsedNpc != request.NpcFormId)
                    throw new InvalidDataException("Face snapshot npcFormId does not match the request.");

                foreach (var section in Enum.GetValues<FaceResetSection>())
                {
                    if (TryGet(root, section.ToWireName(), out var value) &&
                        value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                        throw new InvalidDataException($"Face snapshot section '{section.ToWireName()}' must be an object or array.");
                }

                var properties = root.EnumerateObject()
                    .Select(property => new FaceSnapshotProperty(property.Name, property.Value.Clone()))
                    .ToImmutableArray();
                return new FaceSnapshot(parsedGame, parsedNpc, properties);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            diagnostics.Add(new Diagnostic("face-reset-read-failed", DiagnosticSeverity.Error,
                $"The {role} face snapshot could not be read: {exception.Message}"));
            return null;
        }
    }

    private static byte[] WriteSnapshot(FaceSnapshot current, FaceSnapshot baseline, FaceResetSection section)
    {
        var sectionName = section.ToWireName();
        var baselineValue = baseline.Find(sectionName);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            var wroteSection = false;
            foreach (var property in current.Properties)
            {
                if (string.Equals(property.Name, sectionName, StringComparison.OrdinalIgnoreCase))
                {
                    if (baselineValue is { } value)
                    {
                        writer.WritePropertyName(property.Name);
                        value.WriteTo(writer);
                        wroteSection = true;
                    }
                    continue;
                }

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            if (!wroteSection && baselineValue is { } appended)
            {
                writer.WritePropertyName(sectionName);
                appended.WriteTo(writer);
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static bool MatchesReset(FaceSnapshot current, FaceSnapshot baseline, FaceSnapshot output,
        FaceResetSection section)
    {
        var sectionName = section.ToWireName();
        if (!JsonValuesEqual(output.Find(sectionName), baseline.Find(sectionName))) return false;

        var names = current.Properties.Select(item => item.Name)
            .Concat(output.Properties.Select(item => item.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.Equals(name, sectionName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!JsonValuesEqual(current.Find(name), output.Find(name))) return false;
        }

        return output.Edition == current.Edition && output.NpcFormId == current.NpcFormId;
    }

    private ImmutableArray<Diagnostic> ValidateRequest(FaceResetRequest request, bool requireExpectedHash,
        bool allowExistingProposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!IsSectionSupported(request.Edition, request.Section))
            diagnostics.Add(new Diagnostic("face-reset-section-game-mismatch", DiagnosticSeverity.Error,
                $"Face reset section '{request.Section.ToWireName()}' is not available for {request.Edition.ToWireName()}."));
        ValidateInputPath(request.CurrentSnapshot, "current-snapshot", diagnostics);
        ValidateInputPath(request.BaselineSnapshot, "baseline-snapshot", diagnostics);
        ValidateOutputPath(request.OutputSnapshot, diagnostics);
        if (!string.Equals(Path.GetFileName(request.CurrentSnapshot.Value), Path.GetFileName(request.BaselineSnapshot.Value), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(request.CurrentSnapshot.Value), Path.GetFileName(request.OutputSnapshot.Value), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("face-reset-identity-mismatch", DiagnosticSeverity.Error,
                "Current, baseline, and output snapshots must keep the same filename."));
        if (SamePath(request.CurrentSnapshot, request.BaselineSnapshot) ||
            SamePath(request.CurrentSnapshot, request.OutputSnapshot) ||
            SamePath(request.BaselineSnapshot, request.OutputSnapshot))
            diagnostics.Add(new Diagnostic("face-reset-path-collision", DiagnosticSeverity.Error,
                "Current, baseline, and output snapshots must be distinct files."));
        if (File.Exists(request.OutputSnapshot.Value))
            diagnostics.Add(new Diagnostic("face-reset-output-exists", DiagnosticSeverity.Error,
                "Face reset never overwrites an existing snapshot."));
        if (requireExpectedHash && request.ExpectedCurrentHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error,
                "Apply requires --expected-sha256."));
        if (request.ProposalPath is { } proposal)
        {
            if (!proposal.IsUnder(labRoot))
                diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error,
                    "Proposal paths must remain under the K-only lab root."));
            var parent = Path.GetDirectoryName(proposal.Value);
            if (parent is null || !Directory.Exists(parent))
                diagnostics.Add(new Diagnostic("proposal-parent-missing", DiagnosticSeverity.Error,
                    "The proposal directory must already exist."));
            if (File.Exists(proposal.Value) && !allowExistingProposal)
                diagnostics.Add(new Diagnostic("proposal-exists", DiagnosticSeverity.Error,
                    "Proposals never overwrite artifacts."));
            if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "proposal-parent");
        }
        return diagnostics.ToImmutable();
    }

    private void ValidateInputPath(WorkspacePath path, string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!path.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error,
                $"The {role} path must remain under the K-only lab root."));
        if (!File.Exists(path.Value))
            diagnostics.Add(new Diagnostic("face-reset-input-missing", DiagnosticSeverity.Error,
                $"The {role} file does not exist."));
        if (!IsSnapshotPath(path.Value))
            diagnostics.Add(new Diagnostic("face-reset-extension-invalid", DiagnosticSeverity.Error,
                "Face reset snapshots must use the .face.json suffix."));
        var parent = Path.GetDirectoryName(path.Value);
        if (parent is not null && Directory.Exists(parent))
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
            AddReparseDiagnostic(diagnostics, path.Value, role);
        }
    }

    private void ValidateOutputPath(WorkspacePath path, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!path.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("output-outside-lab", DiagnosticSeverity.Error,
                "The output path must remain under the K-only lab root."));
        if (!IsSnapshotPath(path.Value))
            diagnostics.Add(new Diagnostic("face-reset-extension-invalid", DiagnosticSeverity.Error,
                "Face reset snapshots must use the .face.json suffix."));
        var parent = Path.GetDirectoryName(path.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("face-reset-output-parent-missing", DiagnosticSeverity.Error,
                "The output directory must already exist."));
        else
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
            AddReparseDiagnostic(diagnostics, parent, "output-parent");
        }
    }

    private static bool IsSectionSupported(GameEdition edition, FaceResetSection section) => edition switch
    {
        GameEdition.Fallout4 => section is FaceResetSection.FaceParts or FaceResetSection.Tints or
            FaceResetSection.VertexMorphs or FaceResetSection.BoneRegions,
        GameEdition.SkyrimSpecialEdition => section is FaceResetSection.FaceParts or FaceResetSection.SkyrimMorphs or
            FaceResetSection.SkyrimTints,
        _ => false
    };

    private static bool IsSnapshotPath(string path) => path.EndsWith(".face.json", StringComparison.OrdinalIgnoreCase);

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool JsonValuesEqual(JsonElement? left, JsonElement? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return JsonElement.DeepEquals(left.Value, right.Value);
    }

    private static string Describe(JsonElement? value) => value is null ? "<absent>" : value.Value.GetRawText();

    private static Sha256Hash HashOrZero(string path, ImmutableArray<Diagnostic>.Builder diagnostics, string role)
    {
        try
        {
            if (!File.Exists(path)) return ZeroHash;
            using var stream = File.OpenRead(path);
            return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("face-reset-hash-failed", DiagnosticSeverity.Error,
                $"The {role} hash could not be computed: {exception.Message}"));
            return ZeroHash;
        }
    }

    private static void WriteProposal(WorkspacePath path, FaceResetProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WriteBytes(temporary, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(proposal, ProposalOptions)));
            File.Move(temporary, path.Value, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("proposal-write-failed", DiagnosticSeverity.Error, exception.Message));
        }
        finally { TryDelete(temporary); }
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
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
                diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, exception.Message));
                return;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static bool SamePath(WorkspacePath left, WorkspacePath right) =>
        string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record FaceSnapshot(GameEdition Edition, FormId NpcFormId,
        ImmutableArray<FaceSnapshotProperty> Properties)
    {
        public JsonElement? Find(string name) => Properties
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private sealed record FaceSnapshotProperty(string Name, JsonElement Value);
}
