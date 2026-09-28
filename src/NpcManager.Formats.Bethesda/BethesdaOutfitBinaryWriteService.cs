using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using Fo4 = Mutagen.Bethesda.Fallout4;
using Sse = Mutagen.Bethesda.Skyrim;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Materializes the bounded OTFT proposal into a new plugin containing exactly
/// one outfit record. The source hash, mode, FormID, masters, and item links are
/// revalidated before any binary write; the result is read back before promotion.
/// </summary>
public sealed class BethesdaOutfitBinaryWriteService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IOutfitBinaryWriteService
{
    private const long MaximumProposalBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<OutfitBinaryWriteResult> WriteAsync(
        OutfitBinaryWriteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(request, null, diagnostics.ToImmutable());

        OutfitProposalArtifact? proposal;
        try
        {
            var info = new FileInfo(request.Proposal.Value);
            if (info.Length <= 0 || info.Length > MaximumProposalBytes)
                throw new InvalidDataException("The outfit proposal is empty or exceeds the size limit.");
            await using var stream = new FileStream(request.Proposal.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.SequentialScan);
            proposal = await JsonSerializer.DeserializeAsync<OutfitProposalArtifact>(stream, JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic("outfit-binary-proposal-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, null, diagnostics.ToImmutable());
        }

        if (proposal is null)
        {
            diagnostics.Add(new Diagnostic("outfit-binary-proposal-empty", DiagnosticSeverity.Error,
                "The outfit proposal must contain a JSON object."));
            return Refused(request, null, diagnostics.ToImmutable());
        }

        if (!string.Equals(proposal.ArtifactKind, "outfit-record-proposal", StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("outfit-binary-proposal-kind", DiagnosticSeverity.Error,
                "The proposal artifact kind must be outfit-record-proposal."));
        if (!GameEditionExtensions.TryParseWireName(proposal.Edition, out var edition) || edition != request.Edition)
            diagnostics.Add(new Diagnostic("outfit-binary-proposal-edition", DiagnosticSeverity.Error,
                "The proposal edition does not match the write request."));
        if (!Enum.IsDefined(proposal.Mode))
            diagnostics.Add(new Diagnostic("outfit-binary-proposal-mode", DiagnosticSeverity.Error,
                "The proposal mode is invalid."));
        if (!FormId.TryParse(proposal.SourceFormId, out var sourceFormId) || sourceFormId.Value == 0)
            diagnostics.Add(new Diagnostic("outfit-binary-source-form", DiagnosticSeverity.Error,
                "The proposal source FormID is invalid."));
        try { _ = new EditorId(proposal.EditorId); }
        catch (ArgumentException)
        {
            diagnostics.Add(new Diagnostic("outfit-binary-editor-id", DiagnosticSeverity.Error,
                "The proposal EditorID is invalid."));
        }
        if (proposal.Items.IsDefaultOrEmpty)
            diagnostics.Add(new Diagnostic("outfit-binary-items-empty", DiagnosticSeverity.Error,
                "The proposal must contain at least one outfit item."));
        if (proposal.Items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != proposal.Items.Length)
            diagnostics.Add(new Diagnostic("outfit-binary-items-duplicate", DiagnosticSeverity.Error,
                "The proposal contains duplicate outfit items."));

        var sourcePath = TryWorkspacePath(proposal.SourcePlugin, diagnostics, "source");
        if (sourcePath is null || !File.Exists(sourcePath.Value.Value))
            diagnostics.Add(new Diagnostic("outfit-binary-source-missing", DiagnosticSeverity.Error,
                "The proposal source plugin does not exist under the K-only lab root."));
        else
        {
            AddReparseDiagnostic(diagnostics, sourcePath.Value.Value, "source");
            if (!string.Equals(Path.GetFileName(sourcePath.Value.Value), Path.GetFileName(proposal.SourcePlugin),
                    StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("outfit-binary-source-name-mismatch", DiagnosticSeverity.Error,
                    "The proposal source plugin filename must match the source path filename."));
        }
        var sourcePlugin = TryPluginName(proposal.SourcePlugin, diagnostics, "source");
        var sourceOwnerPlugin = TryPluginName(
            proposal.SourceOwnerPlugin ?? Path.GetFileName(proposal.SourcePlugin),
            diagnostics,
            "source owner");
        var itemReferences = ParseItems(proposal.Items, diagnostics);
        var targetFormId = ResolveTarget(proposal, sourceFormId, diagnostics);
        if (sourcePath is null || sourcePlugin is null || sourceOwnerPlugin is null ||
            targetFormId is null || HasErrors(diagnostics))
            return Refused(request, targetFormId, diagnostics.ToImmutable());

        try
        {
            var sourceHash = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(sourcePath.Value.Value, cancellationToken))));
            if (!string.Equals(sourceHash.Value, proposal.InputSha256, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("outfit-binary-input-hash-mismatch", DiagnosticSeverity.Error,
                    "The proposal source hash does not match the current source plugin."));
            var outputModKey = ToModKey(request.Output.Value);
            if (sourcePlugin.Value == outputModKey)
                diagnostics.Add(new Diagnostic("outfit-binary-output-master-self", DiagnosticSeverity.Error,
                    "The output plugin may not be the same plugin as the proposal source."));
            if (HasErrors(diagnostics)) return Refused(request, targetFormId, diagnostics.ToImmutable());

            var temporary = request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                switch (request.Edition)
                {
                    case GameEdition.Fallout4:
                        WriteFallout4(sourcePath.Value.Value, sourcePlugin.Value, outputModKey, proposal,
                            sourceOwnerPlugin.Value, targetFormId.Value, itemReferences, temporary);
                        VerifyFallout4(temporary, sourceOwnerPlugin.Value, outputModKey, proposal,
                            targetFormId.Value, itemReferences, diagnostics);
                        break;
                    case GameEdition.SkyrimSpecialEdition:
                        WriteSkyrim(sourcePath.Value.Value, sourcePlugin.Value, outputModKey, proposal,
                            sourceOwnerPlugin.Value, targetFormId.Value, itemReferences, temporary);
                        VerifySkyrim(temporary, sourceOwnerPlugin.Value, outputModKey, proposal,
                            targetFormId.Value, itemReferences, diagnostics);
                        break;
                    default:
                        diagnostics.Add(new Diagnostic("outfit-binary-edition-unsupported", DiagnosticSeverity.Error,
                            "The requested game edition is unsupported."));
                        break;
                }
                if (HasErrors(diagnostics)) return Refused(request, targetFormId, diagnostics.ToImmutable());
                File.Move(temporary, request.Output.Value, overwrite: false);
                var outputHash = new Sha256Hash(Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(request.Output.Value, cancellationToken))));
                return new OutfitBinaryWriteResult(true, request.Proposal, request.Output,
                    targetFormId, outputHash, diagnostics.ToImmutable());
            }
            finally { TryDelete(temporary); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            diagnostics.Add(new Diagnostic("outfit-binary-write-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, targetFormId, diagnostics.ToImmutable());
        }
    }

    private ImmutableArray<Diagnostic> ValidatePaths(OutfitBinaryWriteRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.Proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("outfit-binary-proposal-outside-lab",
            DiagnosticSeverity.Error, "The outfit proposal must remain under the K-only lab root."));
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("outfit-binary-output-outside-lab",
            DiagnosticSeverity.Error, "The outfit output must remain under the K-only lab root."));
        if (!request.Output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("outfit-binary-output-extension", DiagnosticSeverity.Error,
                "The bounded OTFT writer emits ordinary .esp plugins only; ESL/ESM flags are not inferred."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("outfit-binary-output-exists",
            DiagnosticSeverity.Error, "Binary outfit writes never overwrite an existing plugin."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("outfit-binary-output-parent",
            DiagnosticSeverity.Error, "The output plugin directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        AddReparseDiagnostic(diagnostics, request.Proposal.Value, "proposal");
        if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        if (!File.Exists(request.Proposal.Value)) diagnostics.Add(new Diagnostic("outfit-binary-proposal-missing",
            DiagnosticSeverity.Error, "The outfit proposal does not exist."));
        return diagnostics.ToImmutable();
    }

    private static FormId? ResolveTarget(OutfitProposalArtifact proposal, FormId source,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (proposal.Mode == OutfitProposalMode.Override)
        {
            if (proposal.TargetFormId is not null && (!FormId.TryParse(proposal.TargetFormId, out var target) || target != source))
                diagnostics.Add(new Diagnostic("outfit-binary-override-target", DiagnosticSeverity.Error,
                    "Override proposals must target the source FormID."));
            return source;
        }
        if (!FormId.TryParse(proposal.TargetFormId ?? string.Empty, out var newTarget) ||
            newTarget.Value == 0 || newTarget.Value > 0x00FF_FFFF)
            diagnostics.Add(new Diagnostic("outfit-binary-new-target", DiagnosticSeverity.Error,
                "New outfit proposals require a nonzero plugin-local 24-bit target FormID."));
        return newTarget.Value == 0 ? null : newTarget;
    }

    private static ImmutableArray<FormReference> ParseItems(ImmutableArray<string> values,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var value in values)
        {
            if (!FormReference.TryParse(value, out var reference) || reference.FormId.Value == 0)
            {
                diagnostics.Add(new Diagnostic("outfit-binary-item-invalid", DiagnosticSeverity.Error,
                    $"Outfit item '{value}' is not a valid non-null FormReference."));
                continue;
            }
            result.Add(reference);
        }
        return result.ToImmutable();
    }

    private static void WriteFallout4(string sourcePath, ModKey sourceModKey, ModKey outputModKey,
        OutfitProposalArtifact proposal, ModKey sourceOwnerModKey, FormId target,
        ImmutableArray<FormReference> items, string destination)
    {
        using var overlay = Fo4.Fallout4Mod.CreateFromBinaryOverlay(
            new ModPath(sourceModKey, new FilePath(sourcePath)), Fo4.Fallout4Release.Fallout4);
        if (!FormId.TryParse(proposal.SourceFormId, out var sourceFormId) ||
            overlay.Outfits.FirstOrDefault(item => item.FormKey == new FormKey(sourceOwnerModKey, sourceFormId.Value)) is null)
            throw new InvalidDataException($"OTFT source record {proposal.SourceFormId} was not found in the source plugin.");
        var mod = new Fo4.Fallout4Mod(outputModKey, Fo4.Fallout4Release.Fallout4);
        AddMasters(mod.ModHeader.MasterReferences, sourceModKey, sourceOwnerModKey, items, outputModKey);
        var formKey = proposal.Mode == OutfitProposalMode.Override
            ? new FormKey(sourceOwnerModKey, target.Value) : new FormKey(outputModKey, target.Value);
        var outfit = new Fo4.Outfit(formKey, Fo4.Fallout4Release.Fallout4)
        {
            EditorID = proposal.EditorId,
            Items = [.. items.Select(item => new FormLink<Fo4.IOutfitTargetGetter>(ToFormKey(item)))]
        };
        mod.Outfits.Add(outfit);
        WriteMod(mod, destination);
    }

    private static void WriteSkyrim(string sourcePath, ModKey sourceModKey, ModKey outputModKey,
        OutfitProposalArtifact proposal, ModKey sourceOwnerModKey, FormId target,
        ImmutableArray<FormReference> items, string destination)
    {
        using var overlay = Sse.SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(sourceModKey, new FilePath(sourcePath)), Sse.SkyrimRelease.SkyrimSE);
        if (!FormId.TryParse(proposal.SourceFormId, out var sourceFormId) ||
            overlay.Outfits.FirstOrDefault(item => item.FormKey == new FormKey(sourceOwnerModKey, sourceFormId.Value)) is null)
            throw new InvalidDataException($"OTFT source record {proposal.SourceFormId} was not found in the source plugin.");
        var mod = new Sse.SkyrimMod(outputModKey, Sse.SkyrimRelease.SkyrimSE);
        AddMasters(mod.ModHeader.MasterReferences, sourceModKey, sourceOwnerModKey, items, outputModKey);
        var formKey = proposal.Mode == OutfitProposalMode.Override
            ? new FormKey(sourceOwnerModKey, target.Value) : new FormKey(outputModKey, target.Value);
        var outfit = new Sse.Outfit(formKey, Sse.SkyrimRelease.SkyrimSE)
        {
            EditorID = proposal.EditorId,
            Items = [.. items.Select(item => new FormLink<Sse.IOutfitTargetGetter>(ToFormKey(item)))]
        };
        mod.Outfits.Add(outfit);
        WriteMod(mod, destination);
    }

    private static void VerifyFallout4(string path, ModKey sourceOwnerModKey, ModKey outputModKey,
        OutfitProposalArtifact proposal, FormId target, ImmutableArray<FormReference> items,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var mod = Fo4.Fallout4Mod.CreateFromBinaryOverlay(
            new ModPath(outputModKey, new FilePath(path)), Fo4.Fallout4Release.Fallout4);
        var formKey = proposal.Mode == OutfitProposalMode.Override
            ? new FormKey(sourceOwnerModKey, target.Value) : new FormKey(outputModKey, target.Value);
        var outfits = mod.Outfits.ToArray();
        var outfit = outfits.FirstOrDefault(item => item.FormKey == formKey);
        if (outfits.Length != 1 || outfit is null || !string.Equals(outfit.EditorID, proposal.EditorId, StringComparison.Ordinal) ||
            outfit.Items is null || !SameItems(outfit.Items, items))
            diagnostics.Add(new Diagnostic("outfit-binary-readback-mismatch", DiagnosticSeverity.Error,
                "Independent FO4 OTFT read-back did not match the proposal."));
    }

    private static void VerifySkyrim(string path, ModKey sourceOwnerModKey, ModKey outputModKey,
        OutfitProposalArtifact proposal, FormId target, ImmutableArray<FormReference> items,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var mod = Sse.SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(outputModKey, new FilePath(path)), Sse.SkyrimRelease.SkyrimSE);
        var formKey = proposal.Mode == OutfitProposalMode.Override
            ? new FormKey(sourceOwnerModKey, target.Value) : new FormKey(outputModKey, target.Value);
        var outfits = mod.Outfits.ToArray();
        var outfit = outfits.FirstOrDefault(item => item.FormKey == formKey);
        if (outfits.Length != 1 || outfit is null || !string.Equals(outfit.EditorID, proposal.EditorId, StringComparison.Ordinal) ||
            outfit.Items is null || !SameItems(outfit.Items, items))
            diagnostics.Add(new Diagnostic("outfit-binary-readback-mismatch", DiagnosticSeverity.Error,
                "Independent SSE OTFT read-back did not match the proposal."));
    }

    private static bool SameItems(
        IReadOnlyList<IFormLinkGetter<Fo4.IOutfitTargetGetter>> actual,
        ImmutableArray<FormReference> expected) =>
        actual.Count == expected.Length && actual.Zip(expected)
            .All(pair => pair.First.FormKey == ToFormKey(pair.Second));

    private static bool SameItems(
        IReadOnlyList<IFormLinkGetter<Sse.IOutfitTargetGetter>> actual,
        ImmutableArray<FormReference> expected) =>
        actual.Count == expected.Length && actual.Zip(expected)
            .All(pair => pair.First.FormKey == ToFormKey(pair.Second));

    private static void AddMasters(ExtendedList<MasterReference> masters, ModKey source,
        ModKey sourceOwner, ImmutableArray<FormReference> items, ModKey output)
    {
        var keys = new[] { source, sourceOwner }.Concat(items.Select(item => ToFormKey(item).ModKey))
            .Where(key => key != output).Distinct();
        foreach (var key in keys) masters.Add(new MasterReference { Master = key });
    }

    private static ModKey? TryPluginName(string value, ImmutableArray<Diagnostic>.Builder diagnostics, string role)
    {
        try { return ModKey.FromNameAndExtension(Path.GetFileName(value)); }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("outfit-binary-plugin-invalid", DiagnosticSeverity.Error,
                $"The {role} plugin is invalid: {exception.Message}"));
            return null;
        }
    }

    private WorkspacePath? TryWorkspacePath(string value, ImmutableArray<Diagnostic>.Builder diagnostics, string role)
    {
        try
        {
            var path = new WorkspacePath(value);
            if (!path.IsUnder(labRoot))
                diagnostics.Add(new Diagnostic("outfit-binary-source-outside-lab", DiagnosticSeverity.Error,
                    $"The {role} plugin must remain under the K-only lab root."));
            return path;
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("outfit-binary-source-invalid", DiagnosticSeverity.Error, exception.Message));
            return null;
        }
    }

    private static FormKey ToFormKey(FormReference reference) => new(
        ModKey.FromNameAndExtension(reference.Plugin.Value), reference.FormId.Value);

    private static ModKey ToModKey(string path) =>
        ModKey.FromNameAndExtension(Path.GetFileName(path));

    private static void WriteMod(IModGetter mod, string destination) => mod.WriteToBinary(new FilePath(destination),
        new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck
        });

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
                    diagnostics.Add(new Diagnostic("outfit-binary-reparse", DiagnosticSeverity.Error,
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("outfit-binary-path-inspection", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static OutfitBinaryWriteResult Refused(OutfitBinaryWriteRequest request, FormId? target,
        ImmutableArray<Diagnostic> diagnostics) => new(false, request.Proposal, request.Output, target, null, diagnostics);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
