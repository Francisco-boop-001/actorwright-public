using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using Fo4 = Mutagen.Bethesda.Fallout4;

namespace NpcManager.Formats.Bethesda;

/// <summary>Materializes one typed Fallout 4 MSWP proposal into an ordinary plugin.</summary>
public sealed class BethesdaMaterialSwapBinaryWriteService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IMaterialSwapBinaryWriteService
{
    private const long MaximumProposalBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<MaterialSwapBinaryWriteResult> WriteAsync(MaterialSwapBinaryWriteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(request, null, diagnostics.ToImmutable());
        MaterialSwapProposalArtifact? proposal;
        try
        {
            var info = new FileInfo(request.Proposal.Value);
            if (info.Length <= 0 || info.Length > MaximumProposalBytes)
                throw new InvalidDataException("The material-swap proposal is empty or exceeds the size limit.");
            await using var stream = new FileStream(request.Proposal.Value, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            proposal = await JsonSerializer.DeserializeAsync<MaterialSwapProposalArtifact>(stream, JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic("material-swap-binary-proposal-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, null, diagnostics.ToImmutable());
        }
        if (proposal is null)
        {
            diagnostics.Add(new Diagnostic("material-swap-binary-proposal-empty", DiagnosticSeverity.Error, "The material-swap proposal must contain a JSON object."));
            return Refused(request, null, diagnostics.ToImmutable());
        }
        if (!string.Equals(proposal.ArtifactKind, "material-swap-record-proposal", StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("material-swap-binary-proposal-kind", DiagnosticSeverity.Error, "The proposal artifact kind must be material-swap-record-proposal."));
        if (!GameEditionExtensions.TryParseWireName(proposal.Edition, out var edition) || edition != GameEdition.Fallout4 || request.Edition != GameEdition.Fallout4)
            diagnostics.Add(new Diagnostic("material-swap-binary-proposal-edition", DiagnosticSeverity.Error, "Material-swap binary writes support Fallout 4 only and the proposal edition must match."));
        if (!Enum.IsDefined(proposal.Mode))
            diagnostics.Add(new Diagnostic("material-swap-binary-proposal-mode", DiagnosticSeverity.Error, "The proposal mode is invalid."));
        if (!FormId.TryParse(proposal.SourceFormId, out var sourceFormId) || sourceFormId.Value == 0)
            diagnostics.Add(new Diagnostic("material-swap-binary-source-form", DiagnosticSeverity.Error, "The proposal source FormID is invalid."));
        if (string.IsNullOrWhiteSpace(proposal.EditorId))
            diagnostics.Add(new Diagnostic("material-swap-binary-editor-id", DiagnosticSeverity.Error, "The proposal EditorID is required."));
        else
        {
            try { _ = new EditorId(proposal.EditorId); }
            catch (ArgumentException) { diagnostics.Add(new Diagnostic("material-swap-binary-editor-id", DiagnosticSeverity.Error, "The proposal EditorID is invalid.")); }
        }
        if (!proposal.NoUnrelatedRecords)
            diagnostics.Add(new Diagnostic("material-swap-binary-scope", DiagnosticSeverity.Error, "The proposal must declare the no-unrelated-records invariant."));
        ValidateProposalPayload(proposal, diagnostics);
        var sourcePath = TryWorkspacePath(proposal.SourcePlugin, diagnostics);
        if (sourcePath is null || !File.Exists(sourcePath.Value.Value))
            diagnostics.Add(new Diagnostic("material-swap-binary-source-missing", DiagnosticSeverity.Error, "The proposal source plugin does not exist under the K-only lab root."));
        else
        {
            AddReparseDiagnostic(diagnostics, sourcePath.Value.Value, "source");
            if (!string.Equals(Path.GetFileName(sourcePath.Value.Value), Path.GetFileName(proposal.SourcePlugin), StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("material-swap-binary-source-name-mismatch", DiagnosticSeverity.Error, "The proposal source plugin filename must match the source path filename."));
        }
        var sourcePlugin = TryPluginName(proposal.SourcePlugin, diagnostics);
        var targetFormId = ResolveTarget(proposal, sourceFormId, diagnostics);
        if (sourcePath is null || sourcePlugin is null || targetFormId is null || HasErrors(diagnostics))
            return Refused(request, targetFormId, diagnostics.ToImmutable());
        try
        {
            var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath.Value.Value, cancellationToken))));
            if (!string.Equals(sourceHash.Value, proposal.InputSha256, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("material-swap-binary-input-hash-mismatch", DiagnosticSeverity.Error, "The proposal source hash does not match the current source plugin."));
            var outputModKey = ToModKey(request.Output.Value);
            if (outputModKey == sourcePlugin.Value)
                diagnostics.Add(new Diagnostic("material-swap-binary-output-master-self", DiagnosticSeverity.Error, "The output plugin may not be the same plugin as the proposal source."));
            if (HasErrors(diagnostics)) return Refused(request, targetFormId, diagnostics.ToImmutable());
            var temporaryDirectory = Path.Combine(Path.GetDirectoryName(request.Output.Value)!, ".mswp-tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var temporary = Path.Combine(temporaryDirectory, Path.GetFileName(request.Output.Value));
            try
            {
                WriteFallout4(sourcePath.Value.Value, sourcePlugin.Value, outputModKey, proposal, sourceFormId, targetFormId.Value, temporary);
                VerifyFallout4(temporary, sourcePlugin.Value, outputModKey, proposal, sourceFormId, targetFormId.Value, diagnostics);
                if (HasErrors(diagnostics)) return Refused(request, targetFormId, diagnostics.ToImmutable());
                File.Move(temporary, request.Output.Value, overwrite: false);
                var outputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.Output.Value, cancellationToken))));
                return new MaterialSwapBinaryWriteResult(true, request.Proposal, request.Output, targetFormId, outputHash, diagnostics.ToImmutable());
            }
            finally { TryDelete(temporary); TryDeleteDirectory(temporaryDirectory); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            diagnostics.Add(new Diagnostic("material-swap-binary-write-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, targetFormId, diagnostics.ToImmutable());
        }
    }

    private static FormId? ResolveTarget(MaterialSwapProposalArtifact proposal, FormId source, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (proposal.Mode == MaterialSwapProposalMode.Override)
        {
            if (proposal.TargetFormId is not null && (!FormId.TryParse(proposal.TargetFormId, out var target) || target != source))
                diagnostics.Add(new Diagnostic("material-swap-binary-override-target", DiagnosticSeverity.Error, "Override proposals must target the source FormID."));
            return source;
        }
        if (!FormId.TryParse(proposal.TargetFormId ?? string.Empty, out var newTarget) || newTarget.Value == 0 || newTarget.Value > 0x00FF_FFFF)
            diagnostics.Add(new Diagnostic("material-swap-binary-new-target", DiagnosticSeverity.Error, "New material-swap proposals require a nonzero plugin-local 24-bit target FormID."));
        return newTarget.Value == 0 ? null : newTarget;
    }

    private static void WriteFallout4(string sourcePath, ModKey sourceModKey, ModKey outputModKey,
        MaterialSwapProposalArtifact proposal, FormId sourceFormId, FormId targetFormId, string destination)
    {
        using var overlay = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(sourceModKey, new FilePath(sourcePath)), Fo4.Fallout4Release.Fallout4);
        var source = overlay.MaterialSwaps.FirstOrDefault(item => item.FormKey == new FormKey(sourceModKey, sourceFormId.Value));
        if (source is null) throw new InvalidDataException($"MSWP source record {proposal.SourceFormId} was not found in the source plugin.");
        if (proposal.Mode == MaterialSwapProposalMode.Override && !string.Equals(source.EditorID, proposal.EditorId, StringComparison.Ordinal))
            throw new InvalidDataException("The override proposal EditorID does not match the source MSWP record.");
        var formKey = proposal.Mode == MaterialSwapProposalMode.Override ? source.FormKey : new FormKey(outputModKey, targetFormId.Value);
        var swap = proposal.Mode == MaterialSwapProposalMode.Override
            ? source.DeepCopy()
            : new Fo4.MaterialSwap(formKey, Fo4.Fallout4Release.Fallout4)
            {
                EditorID = proposal.EditorId,
                TreeFolder = proposal.TreeFolder,
                Substitutions = BuildSubstitutions(proposal.Entries)
            };
        Apply(swap, proposal);
        var mod = new Fo4.Fallout4Mod(outputModKey, Fo4.Fallout4Release.Fallout4);
        if (proposal.Mode == MaterialSwapProposalMode.Override)
        {
            mod.ModHeader.MasterReferences.Add(new MasterReference { Master = sourceModKey });
            foreach (var master in overlay.ModHeader.MasterReferences) if (master.Master != outputModKey && master.Master != sourceModKey) mod.ModHeader.MasterReferences.Add(new MasterReference { Master = master.Master });
        }
        mod.MaterialSwaps.Add(swap);
        WriteMod(mod, destination);
    }

    private static void Apply(Fo4.MaterialSwap swap, MaterialSwapProposalArtifact proposal)
    {
        swap.EditorID = proposal.EditorId;
        if (HasChanged(proposal, "treeFolder")) swap.TreeFolder = proposal.TreeFolder;
        if (HasChanged(proposal, "entries") && proposal.Mode == MaterialSwapProposalMode.Override)
        {
            swap.Substitutions.Clear();
            foreach (var item in BuildSubstitutions(proposal.Entries)) swap.Substitutions.Add(item);
        }
    }

    private static ExtendedList<Fo4.MaterialSubstitution> BuildSubstitutions(ImmutableArray<MaterialSwapEntryArtifact> entries)
    {
        var substitutions = new ExtendedList<Fo4.MaterialSubstitution>();
        foreach (var entry in entries)
            substitutions.Add(new Fo4.MaterialSubstitution
            {
                OriginalMaterial = entry.OriginalMaterial,
                ReplacementMaterial = entry.ReplacementMaterial,
                ColorRemappingIndex = entry.ColorRemapIndex is { } value ? checked((float)value) : null
            });
        return substitutions;
    }

    private static void VerifyFallout4(string path, ModKey sourceModKey, ModKey outputModKey,
        MaterialSwapProposalArtifact proposal, FormId sourceFormId, FormId targetFormId, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var mod = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(outputModKey, new FilePath(path)), Fo4.Fallout4Release.Fallout4);
        var expected = proposal.Mode == MaterialSwapProposalMode.Override ? new FormKey(sourceModKey, sourceFormId.Value) : new FormKey(outputModKey, targetFormId.Value);
        var records = mod.MaterialSwaps.ToArray();
        var swap = records.FirstOrDefault(item => item.FormKey == expected);
        if (records.Length != 1 || swap is null || swap.EditorID != proposal.EditorId || !Matches(swap, proposal))
        {
            diagnostics.Add(new Diagnostic("material-swap-binary-readback-mismatch", DiagnosticSeverity.Error, "Independent MSWP read-back did not match the proposal."));
        }
    }

    private static bool Matches(Fo4.IMaterialSwapGetter swap, MaterialSwapProposalArtifact proposal)
    {
        if (HasChanged(proposal, "treeFolder") && swap.TreeFolder != proposal.TreeFolder) return false;
        if (!HasChanged(proposal, "entries")) return true;
        var actual = swap.Substitutions;
        var offset = string.IsNullOrEmpty(proposal.TreeFolder) ? 0 : 1;
        if (actual.Count != proposal.Entries.Length + offset) return false;
        if (offset == 1 && (actual[0].OriginalMaterial is not null || actual[0].ReplacementMaterial is not null || actual[0].ColorRemappingIndex is not null)) return false;
        return actual.Skip(offset).Zip(proposal.Entries).All(pair =>
            pair.First.OriginalMaterial == pair.Second.OriginalMaterial &&
            pair.First.ReplacementMaterial == pair.Second.ReplacementMaterial &&
            (pair.Second.ColorRemapIndex is null
                ? pair.First.ColorRemappingIndex is null
                : pair.First.ColorRemappingIndex is { } value && Math.Abs(value - pair.Second.ColorRemapIndex.Value) < 0.0001f));
    }

    private static void ValidateProposalPayload(MaterialSwapProposalArtifact proposal, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!string.Equals(proposal.SchemaVersion, "1", StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("material-swap-binary-proposal-schema", DiagnosticSeverity.Error, "The material-swap proposal schema version is unsupported."));
        if (proposal.Entries.IsDefaultOrEmpty || proposal.Entries.Length > 4096)
            diagnostics.Add(new Diagnostic("material-swap-binary-entry-count", DiagnosticSeverity.Error, "The material-swap proposal must contain 1 to 4096 entries."));
        ValidateTreeFolder(proposal.TreeFolder, "record", diagnostics);
        foreach (var entry in proposal.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.OriginalMaterial) && string.IsNullOrWhiteSpace(entry.ReplacementMaterial))
                diagnostics.Add(new Diagnostic("material-swap-binary-empty-entry", DiagnosticSeverity.Error, "Each material substitution must contain an original or replacement material."));
            ValidateMaterial(entry.OriginalMaterial, "original", diagnostics);
            ValidateMaterial(entry.ReplacementMaterial, "replacement", diagnostics);
            ValidateTreeFolder(entry.TreeFolder, "entry", diagnostics);
            if (!string.IsNullOrEmpty(entry.TreeFolder))
                diagnostics.Add(new Diagnostic("material-swap-binary-entry-tree-folder", DiagnosticSeverity.Error, "Per-entry TreeFolder metadata is not representable by the bounded Mutagen MSWP writer."));
            if (entry.ColorRemapIndex is { } index && (!double.IsFinite(index) || index < 0 || index > 1))
                diagnostics.Add(new Diagnostic("material-swap-binary-color-remap", DiagnosticSeverity.Error, "Color remap indexes must be finite values from 0 to 1."));
        }
        if (string.IsNullOrWhiteSpace(proposal.PatchSha256) || proposal.PatchSha256.Length != 64 || proposal.PatchSha256.Any(character => !Uri.IsHexDigit(character)))
            diagnostics.Add(new Diagnostic("material-swap-binary-patch-hash", DiagnosticSeverity.Error, "The material-swap proposal patch hash must be a 64-character hexadecimal digest."));
    }

    private static void ValidateMaterial(string value, string role, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try { _ = new AssetPath(value); }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic($"material-swap-binary-{role}-path", DiagnosticSeverity.Error, exception.Message)); }
    }

    private static void ValidateTreeFolder(string? value, string role, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (value is { Length: > 4096 } || value?.Any(char.IsControl) == true)
            diagnostics.Add(new Diagnostic($"material-swap-binary-{role}-tree-folder", DiagnosticSeverity.Error, "TreeFolder must be at most 4096 characters and contain no control characters."));
    }

    private ImmutableArray<Diagnostic> ValidatePaths(MaterialSwapBinaryWriteRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.Proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("material-swap-binary-proposal-outside-lab", DiagnosticSeverity.Error, "The proposal must remain under the K-only lab root."));
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("material-swap-binary-output-outside-lab", DiagnosticSeverity.Error, "The output must remain under the K-only lab root."));
        if (!request.Output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("material-swap-binary-output-extension", DiagnosticSeverity.Error, "The bounded MSWP writer emits ordinary .esp plugins only."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("material-swap-binary-output-exists", DiagnosticSeverity.Error, "Binary material-swap writes never overwrite an existing plugin."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("material-swap-binary-output-parent", DiagnosticSeverity.Error, "The output plugin directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        AddReparseDiagnostic(diagnostics, request.Proposal.Value, "proposal");
        if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        if (!File.Exists(request.Proposal.Value)) diagnostics.Add(new Diagnostic("material-swap-binary-proposal-missing", DiagnosticSeverity.Error, "The material-swap proposal does not exist."));
        return diagnostics.ToImmutable();
    }

    private WorkspacePath? TryWorkspacePath(string value, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var path = new WorkspacePath(value);
            if (!path.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("material-swap-binary-source-outside-lab", DiagnosticSeverity.Error, "The source path must remain under the K-only lab root."));
            return path;
        }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("material-swap-binary-source-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
    }

    private static ModKey? TryPluginName(string value, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try { return new ModKey(Path.GetFileNameWithoutExtension(value), ModType.Plugin); }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("material-swap-binary-plugin-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
    }

    private static ModKey ToModKey(string path) => new(Path.GetFileNameWithoutExtension(path), ModType.Plugin);
    private static void WriteMod(IModGetter mod, string destination) => mod.WriteToBinary(new FilePath(destination));
    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                { diagnostics.Add(new Diagnostic("material-swap-binary-reparse", DiagnosticSeverity.Error, $"The {role} traverses a reparse point.")); return; }
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("material-swap-binary-path-inspection", DiagnosticSeverity.Error, exception.Message)); return; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasChanged(MaterialSwapProposalArtifact proposal, string field) => field switch
    {
        "entries" => true,
        "treeFolder" => proposal.TreeFolder is not null,
        _ => false
    };
    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static MaterialSwapBinaryWriteResult Refused(MaterialSwapBinaryWriteRequest request, FormId? target, ImmutableArray<Diagnostic> diagnostics) => new(false, request.Proposal, request.Output, target, null, diagnostics);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }
}
