using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Atomic coordinator for one source-owned ESP override and one canonical
/// RaceMenu preset. Each underlying writer retains its own independent
/// readback; this coordinator additionally proves selected/unchecked preset
/// semantics and rolls back both fresh outputs on partial failure.
/// </summary>
public sealed class SkyrimSelectiveAppearancePasteTransactionService
    : ISkyrimSelectiveAppearancePasteTransactionService
{
    private readonly INpcAppearanceOverrideService _overrideService;
    private readonly IPresetService _presetService;
    private readonly IPresetCopyService _presetCopyService;
    private readonly IWorkspacePolicy _policy;
    private readonly WorkspacePath _labRoot;
    private readonly IFileArtifactCleanup _cleanup;

    public SkyrimSelectiveAppearancePasteTransactionService(
        INpcAppearanceOverrideService overrideService,
        IPresetService presetService,
        IPresetCopyService presetCopyService,
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
        : this(
            overrideService,
            presetService,
            presetCopyService,
            policy,
            labRoot,
            new FileArtifactCleanup())
    {
    }

    internal SkyrimSelectiveAppearancePasteTransactionService(
        INpcAppearanceOverrideService overrideService,
        IPresetService presetService,
        IPresetCopyService presetCopyService,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        IFileArtifactCleanup cleanup)
    {
        _overrideService = overrideService;
        _presetService = presetService;
        _presetCopyService = presetCopyService;
        _policy = policy;
        _labRoot = labRoot;
        _cleanup = cleanup;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<SkyrimSelectiveAppearancePasteProposal> AnalyzeAsync(
        SkyrimSelectiveAppearancePasteTransactionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request, proposalMustExist: false).ToBuilder();
        SkyrimSelectiveAppearancePasteProjectionResult projection = Project(request);
        diagnostics.AddRange(projection.Diagnostics);
        if (!projection.Accepted || projection.PluginRequest is null)
            return EmptyProposal(request, projection, diagnostics);

        NpcAppearanceOverrideProposal pluginProposal =
            await _overrideService.AnalyzeAsync(
                projection.PluginRequest,
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(pluginProposal.Diagnostics);
        var proposal = new SkyrimSelectiveAppearancePasteProposal(
            1,
            request.TransactionProposalPath,
            null,
            request.OutputPreset,
            request.Selection,
            pluginProposal,
            projection.PresetSections,
            diagnostics.ToImmutable());
        if (!pluginProposal.IsApplicable || HasErrors(diagnostics))
            return proposal;

        try
        {
            Sha256Hash hash = await WriteProposalAsync(
                request,
                proposal,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            return proposal with
            {
                TransactionProposalSha256 = hash,
                Diagnostics = diagnostics.ToImmutable()
            };
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error(
                "selective-paste-proposal-write",
                exception.Message));
            AddCleanupDiagnostic(
                diagnostics,
                request.PluginProposalPath.Value,
                "plugin proposal",
                DiagnosticSeverity.Warning);
            AddCleanupDiagnostic(
                diagnostics,
                request.TransactionProposalPath.Value,
                "transaction proposal",
                DiagnosticSeverity.Warning);
            return proposal with { Diagnostics = diagnostics.ToImmutable() };
        }
    }

    public async ValueTask<SkyrimSelectiveAppearancePasteTransactionResult> ApplyAsync(
        SkyrimSelectiveAppearancePasteTransactionRequest request,
        SkyrimSelectiveAppearancePasteProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request, proposalMustExist: true).ToBuilder();
        ValidateBinding(request, proposal, diagnostics);
        SkyrimSelectiveAppearancePasteProjectionResult projection = Project(request);
        diagnostics.AddRange(projection.Diagnostics);
        if (!projection.Accepted || projection.PluginRequest is null ||
            HasErrors(diagnostics))
        {
            return new SkyrimSelectiveAppearancePasteTransactionResult(
                false, proposal, null, diagnostics.ToImmutable());
        }

        NpcAppearanceOverrideResult plugin = await _overrideService.ApplyAsync(
            projection.PluginRequest,
            proposal.PluginProposal,
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(plugin.Diagnostics);
        if (!plugin.Applied || plugin.Verification is not { IsValid: true })
        {
            return new SkyrimSelectiveAppearancePasteTransactionResult(
                false, proposal, null, diagnostics.ToImmutable());
        }

        bool presetWritten = false;
        try
        {
            if (proposal.PresetSections.IsDefaultOrEmpty)
            {
                PresetExportResult exported = await _presetService.ExportAsync(
                    new PresetExportRequest(
                        PresetFormat.RaceMenuJslot,
                        GameEdition.SkyrimSpecialEdition,
                        request.State.Target.PresetPath,
                        request.OutputPreset),
                    cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(exported.Diagnostics);
                presetWritten = exported.Written;
            }
            else
            {
                PresetCopyResult copied = await _presetCopyService.CopyAsync(
                    new PresetCopyRequest(
                        new PresetParseRequest(
                            PresetFormat.RaceMenuJslot,
                            GameEdition.SkyrimSpecialEdition,
                            request.State.Source.PresetPath),
                        new PresetParseRequest(
                            PresetFormat.RaceMenuJslot,
                            GameEdition.SkyrimSpecialEdition,
                            request.State.Target.PresetPath),
                        proposal.PresetSections,
                        request.OutputPreset),
                    cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(copied.Diagnostics);
                presetWritten = copied.Written;
            }
            if (!presetWritten || HasErrors(diagnostics))
            {
                diagnostics.AddRange(RollBackOutputs(request));
                return new SkyrimSelectiveAppearancePasteTransactionResult(
                    false, proposal, null, diagnostics.ToImmutable());
            }

            SkyrimSelectiveAppearancePasteVerificationResult verification =
                await VerifyCoreAsync(
                    request,
                    proposal,
                    projection.PluginRequest,
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(verification.Diagnostics);
            if (!verification.IsValid || HasErrors(diagnostics))
            {
                diagnostics.AddRange(RollBackOutputs(request));
                return new SkyrimSelectiveAppearancePasteTransactionResult(
                    false, proposal, verification, diagnostics.ToImmutable());
            }
            return new SkyrimSelectiveAppearancePasteTransactionResult(
                true,
                proposal,
                verification,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException exception)
        {
            AttachCancellationCleanupFailures(
                exception,
                RollBackOutputs(request));
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error(
                "selective-paste-apply-failed",
                exception.Message));
            diagnostics.AddRange(RollBackOutputs(request));
            return new SkyrimSelectiveAppearancePasteTransactionResult(
                false, proposal, null, diagnostics.ToImmutable());
        }
    }

    public async ValueTask<SkyrimSelectiveAppearancePasteVerificationResult> VerifyAsync(
        SkyrimSelectiveAppearancePasteTransactionRequest request,
        SkyrimSelectiveAppearancePasteProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(
            request,
            proposalMustExist: true,
            outputsMustExist: true).ToBuilder();
        ValidateBinding(request, proposal, diagnostics);
        SkyrimSelectiveAppearancePasteProjectionResult projection = Project(request);
        diagnostics.AddRange(projection.Diagnostics);
        if (!projection.Accepted || projection.PluginRequest is null ||
            HasErrors(diagnostics))
        {
            return new SkyrimSelectiveAppearancePasteVerificationResult(
                false, null, request.OutputPreset, null, false, false,
                diagnostics.ToImmutable());
        }
        SkyrimSelectiveAppearancePasteVerificationResult result =
            await VerifyCoreAsync(
                request,
                proposal,
                projection.PluginRequest,
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(result.Diagnostics);
        return result with
        {
            IsValid = result.IsValid && !HasErrors(diagnostics),
            Diagnostics = diagnostics.ToImmutable()
        };
    }

    private async ValueTask<SkyrimSelectiveAppearancePasteVerificationResult>
        VerifyCoreAsync(
            SkyrimSelectiveAppearancePasteTransactionRequest request,
            SkyrimSelectiveAppearancePasteProposal proposal,
            NpcAppearanceOverrideRequest pluginRequest,
            CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        NpcAppearanceOverrideVerificationResult plugin =
            await _overrideService.VerifyAsync(
                pluginRequest,
                proposal.PluginProposal,
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(plugin.Diagnostics);

        var outputParse = new PresetParseRequest(
            PresetFormat.RaceMenuJslot,
            GameEdition.SkyrimSpecialEdition,
            request.OutputPreset);
        PresetParseResult output = await _presetService.InspectAsync(
            outputParse,
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(output.Diagnostics);
        bool selectedMatches = false;
        bool uncheckedPreserved = false;
        Sha256Hash? outputHash = output.Document?.SourceHash;
        if (output.Document is not null && output.Document.IsValid)
        {
            PresetDiffResult sourceDiff = await _presetService.DiffAsync(
                new PresetDiffRequest(
                    new PresetParseRequest(
                        PresetFormat.RaceMenuJslot,
                        GameEdition.SkyrimSpecialEdition,
                        request.State.Source.PresetPath),
                    outputParse),
                cancellationToken).ConfigureAwait(false);
            PresetDiffResult targetDiff = await _presetService.DiffAsync(
                new PresetDiffRequest(
                    new PresetParseRequest(
                        PresetFormat.RaceMenuJslot,
                        GameEdition.SkyrimSpecialEdition,
                        request.State.Target.PresetPath),
                    outputParse),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(sourceDiff.Diagnostics.Where(item =>
                item.Severity == DiagnosticSeverity.Error));
            diagnostics.AddRange(targetDiff.Diagnostics.Where(item =>
                item.Severity == DiagnosticSeverity.Error));
            ImmutableHashSet<PresetCopySection> selected =
                proposal.PresetSections.ToImmutableHashSet();
            PresetDifference[] selectedDrift = sourceDiff.Differences
                .Where(item => PathSection(item.Path) is { } section &&
                               selected.Contains(section))
                .ToArray();
            PresetDifference[] uncheckedDrift = targetDiff.Differences
                .Where(item => PathSection(item.Path) is not { } section ||
                               !selected.Contains(section))
                .ToArray();
            selectedMatches = selectedDrift.Length == 0;
            uncheckedPreserved = uncheckedDrift.Length == 0;
            if (!selectedMatches)
                diagnostics.Add(Error(
                    "selective-paste-preset-selected-drift",
                    "One or more selected preset carriers do not equal the source after reopen: " +
                    string.Join(", ", selectedDrift.Select(item => item.Path))));
            if (!uncheckedPreserved)
                diagnostics.Add(Error(
                    "selective-paste-preset-unchecked-drift",
                    "One or more unchecked or metadata preset carriers differ from the target after reopen: " +
                    string.Join(", ", uncheckedDrift.Select(item => item.Path))));
        }
        else
        {
            diagnostics.Add(Error(
                "selective-paste-preset-output-invalid",
                "The output preset could not be independently parsed."));
        }
        bool valid = plugin.IsValid && selectedMatches && uncheckedPreserved &&
                     outputHash is not null && !HasErrors(diagnostics);
        return new SkyrimSelectiveAppearancePasteVerificationResult(
            valid,
            plugin,
            request.OutputPreset,
            outputHash,
            selectedMatches,
            uncheckedPreserved,
            diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidatePaths(
        SkyrimSelectiveAppearancePasteTransactionRequest request,
        bool proposalMustExist,
        bool outputsMustExist = false)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach ((WorkspacePath path, string role) in new[]
                 {
                     (request.TransactionProposalPath, "transaction proposal"),
                     (request.PluginProposalPath, "plugin proposal"),
                     (request.OutputPlugin, "output plugin"),
                     (request.OutputPreset, "output preset")
                 })
        {
            if (!path.IsUnder(_labRoot))
                diagnostics.Add(Error(
                    "selective-paste-path-outside-lab",
                    $"The {role} must remain under the K-only workspace."));
            string? parent = Path.GetDirectoryName(path.Value);
            if (parent is null || !Directory.Exists(parent))
                diagnostics.Add(Error(
                    "selective-paste-parent-missing",
                    $"The {role} parent directory must already exist."));
            else
                diagnostics.AddRange(_policy.Evaluate(
                    _labRoot,
                    new WorkspacePath(parent)));
        }
        if (proposalMustExist)
        {
            if (!File.Exists(request.TransactionProposalPath.Value) ||
                !File.Exists(request.PluginProposalPath.Value))
                diagnostics.Add(Error(
                    "selective-paste-proposal-missing",
                    "Apply and verify require both exact persisted proposals."));
        }
        else if (File.Exists(request.TransactionProposalPath.Value) ||
                 File.Exists(request.PluginProposalPath.Value))
        {
            diagnostics.Add(Error(
                "selective-paste-proposal-exists",
                "Selective-paste analysis never overwrites a proposal."));
        }
        if (outputsMustExist)
        {
            if (!File.Exists(request.OutputPlugin.Value) ||
                !File.Exists(request.OutputPreset.Value))
                diagnostics.Add(Error(
                    "selective-paste-output-missing",
                    "Verification requires both output carriers."));
        }
        else if (File.Exists(request.OutputPlugin.Value) ||
                 File.Exists(request.OutputPreset.Value))
        {
            diagnostics.Add(Error(
                "selective-paste-output-exists",
                "Selective-paste writes never overwrite an output carrier."));
        }
        return diagnostics.ToImmutable();
    }

    private static void ValidateBinding(
        SkyrimSelectiveAppearancePasteTransactionRequest request,
        SkyrimSelectiveAppearancePasteProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (proposal.SchemaVersion != 1 ||
            proposal.TransactionProposalPath != request.TransactionProposalPath ||
            proposal.OutputPreset != request.OutputPreset ||
            !proposal.Selection.Categories.SequenceEqual(
                request.Selection.Categories) ||
            proposal.PluginProposal.ProposalPath != request.PluginProposalPath ||
            proposal.PluginProposal.OutputPlugin != request.OutputPlugin ||
            !proposal.PresetSections.SequenceEqual(
                SkyrimSelectiveAppearancePasteProjector.MapPresetSections(
                    request.Selection)))
        {
            diagnostics.Add(Error(
                "selective-paste-proposal-binding",
                "The supplied proposal is not bound to the exact transaction request."));
        }
        if (proposal.TransactionProposalSha256 is null ||
            !File.Exists(request.TransactionProposalPath.Value) ||
            HashFile(request.TransactionProposalPath.Value) !=
                proposal.TransactionProposalSha256)
        {
            diagnostics.Add(Error(
                "selective-paste-proposal-hash",
                "The persisted transaction proposal hash does not match the analyzed proposal."));
        }
        ValidateInputHash(
            request.State.Source.PluginPath,
            request.State.Source.PluginSha256,
            "source plugin",
            diagnostics);
        ValidateInputHash(
            request.State.Target.PluginPath,
            request.State.Target.PluginSha256,
            "target plugin",
            diagnostics);
        ValidateInputHash(
            request.State.Source.PresetPath,
            request.State.Source.Preset.SourceHash,
            "source preset",
            diagnostics);
        ValidateInputHash(
            request.State.Target.PresetPath,
            request.State.Target.Preset.SourceHash,
            "target preset",
            diagnostics);
    }

    private static void ValidateInputHash(
        WorkspacePath path,
        Sha256Hash expected,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!File.Exists(path.Value) || HashFile(path.Value) != expected)
            diagnostics.Add(Error(
                "selective-paste-input-stale",
                $"The hash-bound {role} changed after load/review."));
    }

    private static PresetCopySection? PathSection(string path)
    {
        if (path is "weight" or "presence.weight")
            return PresetCopySection.BodyWeight;
        if (path.StartsWith("bodyMorphs", StringComparison.Ordinal) ||
            path is "presence.bodyMorphs" ||
            path.StartsWith("raceMenu.bodyMorphsKeyed", StringComparison.Ordinal) ||
            path.StartsWith("raceMenu.nodeTransforms", StringComparison.Ordinal))
            return PresetCopySection.BodySliders;
        if (path.StartsWith("overlays", StringComparison.Ordinal) ||
            path is "presence.overlays" ||
            path.StartsWith("raceMenu.bodyOverlays", StringComparison.Ordinal) ||
            path.StartsWith("raceMenu.skinOverrides", StringComparison.Ordinal))
            return PresetCopySection.Overlays;
        if (path.StartsWith("headParts", StringComparison.Ordinal) ||
            path is "presence.headParts" or "raceMenu.headTexture")
            return PresetCopySection.FaceParts;
        if (path is "hairColor" or "presence.hairColor")
            return PresetCopySection.HairColor;
        if (path.StartsWith("tints", StringComparison.Ordinal) ||
            path is "presence.tints" ||
            path.StartsWith("raceMenu.faceTextures", StringComparison.Ordinal))
            return PresetCopySection.FaceTints;
        if (path.StartsWith("morphs", StringComparison.Ordinal) ||
            path is "presence.morphs" ||
            path.StartsWith("customMorphs", StringComparison.Ordinal) ||
            path.StartsWith("sliderMorphs", StringComparison.Ordinal) ||
            path.StartsWith("raceMenu.faceMorphPresets", StringComparison.Ordinal))
            return PresetCopySection.FaceVertexMorphs;
        if (path is "raceMenu.sculptDivisor" ||
            path.StartsWith("raceMenu.sculptParts", StringComparison.Ordinal))
            return PresetCopySection.Sculpt;
        return null;
    }

    private static SkyrimSelectiveAppearancePasteProjectionResult Project(
        SkyrimSelectiveAppearancePasteTransactionRequest request) =>
        SkyrimSelectiveAppearancePasteProjector.Project(
            new SkyrimSelectiveAppearancePasteProjectionRequest(
                request.State,
                request.Selection,
                request.PluginProposalPath,
                request.OutputPlugin));

    private static SkyrimSelectiveAppearancePasteProposal EmptyProposal(
        SkyrimSelectiveAppearancePasteTransactionRequest request,
        SkyrimSelectiveAppearancePasteProjectionResult projection,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var emptyPlugin = new NpcAppearanceOverrideProposal(
            1,
            GameEdition.SkyrimSpecialEdition,
            request.State.Target.PluginPath,
            request.State.Target.PluginSha256,
            request.State.Target.Plugin,
            request.State.Target.NpcFormId,
            request.State.Target.EditorId,
            request.PluginProposalPath,
            null,
            request.OutputPlugin,
            new PluginName(Path.GetFileName(request.OutputPlugin.Value)),
            request.State.Target.Race,
            request.State.Target.Sex,
            request.State.Target.Appearance,
            request.State.Target.RuntimeAppearance,
            [], [], [], diagnostics.ToImmutable());
        return new SkyrimSelectiveAppearancePasteProposal(
            1,
            request.TransactionProposalPath,
            null,
            request.OutputPreset,
            request.Selection,
            emptyPlugin,
            projection.PresetSections,
            diagnostics.ToImmutable());
    }

    private async ValueTask<Sha256Hash> WriteProposalAsync(
        SkyrimSelectiveAppearancePasteTransactionRequest request,
        SkyrimSelectiveAppearancePasteProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var document = new ProposalDocument(
            "1",
            "skyrim-selective-appearance-paste-proposal",
            request.State.Source.Plugin.Value,
            request.State.Source.PluginPath.Value,
            request.State.Source.PluginSha256.Value,
            request.State.Source.NpcFormId.ToString(),
            request.State.Source.PresetPath.Value,
            request.State.Source.Preset.SourceHash.Value,
            request.State.Target.Plugin.Value,
            request.State.Target.PluginPath.Value,
            request.State.Target.PluginSha256.Value,
            request.State.Target.NpcFormId.ToString(),
            request.State.Target.PresetPath.Value,
            request.State.Target.Preset.SourceHash.Value,
            request.Selection.Categories.Select(item => item.ToWireName())
                .ToImmutableArray(),
            proposal.PresetSections.Select(item => item.ToWireName())
                .ToImmutableArray(),
            request.PluginProposalPath.Value,
            proposal.PluginProposal.ProposalSha256?.Value,
            request.OutputPlugin.Value,
            request.OutputPreset.Value);
        byte[] bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(document, JsonOptions) + "\n");
        string temporary = request.TransactionProposalPath.Value +
                           ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(
                temporary,
                bytes,
                cancellationToken).ConfigureAwait(false);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
                stream.Flush(flushToDisk: true);
            File.Move(
                temporary,
                request.TransactionProposalPath.Value,
                overwrite: false);
            return HashFile(request.TransactionProposalPath.Value);
        }
        catch (OperationCanceledException exception)
        {
            AttachCancellationCleanupFailure(
                exception,
                temporary,
                "temporary transaction proposal");
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            AddCleanupDiagnostic(
                diagnostics,
                temporary,
                "temporary transaction proposal",
                DiagnosticSeverity.Warning);
            throw;
        }
    }

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private ImmutableArray<Diagnostic> RollBackOutputs(
        SkyrimSelectiveAppearancePasteTransactionRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        AddCleanupDiagnostic(
            diagnostics,
            request.OutputPlugin.Value,
            "output plugin",
            DiagnosticSeverity.Error);
        AddCleanupDiagnostic(
            diagnostics,
            request.OutputPreset.Value,
            "output preset",
            DiagnosticSeverity.Error);
        return diagnostics.ToImmutable();
    }

    private void AddCleanupDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string path,
        string role,
        DiagnosticSeverity severity)
    {
        FileArtifactCleanupResult cleanup = _cleanup.DeleteIfPresent(path);
        if (!cleanup.Succeeded)
            diagnostics.Add(new Diagnostic(
                "selective-paste-cleanup-failed",
                severity,
                $"Cleanup could not remove the {role} '{path}': {cleanup.ErrorMessage}"));
    }

    private void AttachCancellationCleanupFailure(
        OperationCanceledException exception,
        string path,
        string role)
    {
        FileArtifactCleanupResult cleanup = _cleanup.DeleteIfPresent(path);
        if (!cleanup.Succeeded)
            exception.Data[$"selective-paste-cleanup-failed:{role}"] =
                $"Cleanup could not remove the {role} '{path}': {cleanup.ErrorMessage}";
    }

    private static void AttachCancellationCleanupFailures(
        OperationCanceledException exception,
        ImmutableArray<Diagnostic> diagnostics)
    {
        for (int index = 0; index < diagnostics.Length; index++)
            exception.Data[$"selective-paste-cleanup-failed:{index}"] =
                diagnostics[index].Message;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record ProposalDocument(
        string SchemaVersion,
        string ArtifactKind,
        string SourcePlugin,
        string SourcePluginPath,
        string SourcePluginSha256,
        string SourceNpcFormId,
        string SourcePreset,
        string SourcePresetSha256,
        string TargetPlugin,
        string TargetPluginPath,
        string TargetPluginSha256,
        string TargetNpcFormId,
        string TargetPreset,
        string TargetPresetSha256,
        ImmutableArray<string> Categories,
        ImmutableArray<string> PresetSections,
        string PluginProposal,
        string? PluginProposalSha256,
        string OutputPlugin,
        string OutputPreset);
}
