using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>Creates a hash-bound OTFT proposal without mutating a plugin or emitting unrelated records.</summary>
public sealed class OutfitProposalService : IOutfitProposalService
{
    private readonly IPluginReader _pluginReader;
    private readonly IWorkspacePolicy _policy;
    private readonly WorkspacePath _labRoot;
    private readonly IFileArtifactCleanup _cleanup;

    public OutfitProposalService(
        IPluginReader pluginReader,
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
        : this(pluginReader, policy, labRoot, new FileArtifactCleanup())
    {
    }

    internal OutfitProposalService(
        IPluginReader pluginReader,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        IFileArtifactCleanup cleanup)
    {
        _pluginReader = pluginReader;
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

    public async ValueTask<OutfitProposalResult> ProposeAsync(OutfitProposalRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var parent = Path.GetDirectoryName(request.SourcePlugin.Value);
        if (parent is null)
            diagnostics.Add(new Diagnostic("outfit-proposal-source-parent", DiagnosticSeverity.Error,
                "The source plugin must have a parent Data directory."));
        else diagnostics.AddRange(_policy.EvaluateReadRoot(_labRoot, request.SourcePlugin));
        diagnostics.AddRange(ValidateDestination(request.OutputProposal));
        if (!File.Exists(request.SourcePlugin.Value))
            diagnostics.Add(new Diagnostic("outfit-proposal-source-missing", DiagnosticSeverity.Error,
                "The source plugin does not exist."));
        else
        {
            try
            {
                if (File.GetAttributes(request.SourcePlugin.Value).HasFlag(FileAttributes.ReparsePoint))
                    diagnostics.Add(new Diagnostic("outfit-proposal-source-reparse", DiagnosticSeverity.Error,
                        "The source plugin may not be a reparse point."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("outfit-proposal-source-attributes-failed", DiagnosticSeverity.Error,
                    exception.Message));
            }
        }
        if (request.Items.IsDefaultOrEmpty)
            diagnostics.Add(new Diagnostic("outfit-proposal-items-required", DiagnosticSeverity.Error,
                "At least one outfit item reference is required."));
        if (request.Items.Select(item => item.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Items.Length)
            diagnostics.Add(new Diagnostic("outfit-proposal-duplicate-item", DiagnosticSeverity.Error,
                "Outfit items may not contain duplicate references."));
        if (request.Mode is not (OutfitProposalMode.New or OutfitProposalMode.Override))
            diagnostics.Add(new Diagnostic("outfit-proposal-mode-invalid", DiagnosticSeverity.Error,
                "Outfit proposal mode must be new or override."));
        if (request.Mode == OutfitProposalMode.New && request.EditorId is null)
            diagnostics.Add(new Diagnostic("outfit-proposal-editor-id-required", DiagnosticSeverity.Error,
                "A new outfit proposal requires an explicit EditorID."));
        if (request.Mode == OutfitProposalMode.New && request.TargetFormId is not { Value: > 0 and <= 0x00FF_FFFF })
            diagnostics.Add(new Diagnostic("outfit-proposal-target-form-required", DiagnosticSeverity.Error,
                "A new outfit proposal requires a nonzero plugin-local 24-bit target FormID."));
        if (request.Mode == OutfitProposalMode.Override && request.TargetFormId is not null)
            diagnostics.Add(new Diagnostic("outfit-proposal-override-target-form", DiagnosticSeverity.Error,
                "Override proposals derive the target FormID from --source and may not supply --target-form."));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        Sha256Hash inputHash;
        try
        {
            inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(request.SourcePlugin.Value, cancellationToken))));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("outfit-proposal-source-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        PluginInspection inspection;
        try
        {
            inspection = await _pluginReader.ReadAsync(new PluginReadRequest(request.Edition,
                request.SourcePlugin), cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            diagnostics.Add(new Diagnostic("outfit-proposal-source-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics, inputHash);
        }
        var sourceMatches = inspection.Records.Where(record => record.FormId == request.SourceFormId &&
            string.Equals(record.Signature, "OTFT", StringComparison.Ordinal)).ToArray();
        if (sourceMatches.Length == 0)
            diagnostics.Add(new Diagnostic("outfit-proposal-source-not-found", DiagnosticSeverity.Error,
                $"OTFT source record {request.SourceFormId} was not found in the source plugin."));
        else if (sourceMatches.Length > 1)
            diagnostics.Add(new Diagnostic("outfit-proposal-source-duplicate", DiagnosticSeverity.Error,
                $"OTFT source record {request.SourceFormId} is duplicated in the source plugin."));
        else if (request.Mode == OutfitProposalMode.Override && string.IsNullOrWhiteSpace(sourceMatches[0].EditorId))
            diagnostics.Add(new Diagnostic("outfit-proposal-source-editor-id-missing", DiagnosticSeverity.Error,
                "Override proposals require the source OTFT EditorID."));
        var source = sourceMatches.FirstOrDefault();
        var sourcePlugin = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var allowedPlugins = inspection.Masters.Append(sourcePlugin)
            .Select(plugin => plugin.Value)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.Items)
        {
            if (item.FormId.Value == 0)
                diagnostics.Add(new Diagnostic("outfit-proposal-item-null", DiagnosticSeverity.Error,
                    $"Outfit item {item} uses the null FormID."));
            if (!allowedPlugins.Contains(item.Plugin.Value))
                diagnostics.Add(new Diagnostic("outfit-proposal-master-unresolved", DiagnosticSeverity.Error,
                    $"Outfit item {item} is not provided by the source plugin or one of its masters."));
        }
        if (!HasErrors(diagnostics))
            await ValidateItemSignaturesAsync(
                request,
                inspection,
                sourcePlugin,
                diagnostics,
                cancellationToken);
        if (request.Mode == OutfitProposalMode.Override && request.EditorId is not null)
            diagnostics.Add(new Diagnostic("outfit-proposal-override-editor-id", DiagnosticSeverity.Error,
                "Override proposals inherit the source EditorID and may not supply a replacement."));
        if (!HasErrors(diagnostics) && request.ActorRace is { } actorRace)
            await ValidateRaceAsync(request, inspection, actorRace, diagnostics, cancellationToken);
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);

        var editorId = request.Mode == OutfitProposalMode.Override
            ? new EditorId(source!.EditorId ?? string.Empty)
            : request.EditorId!.Value;
        var sourceOwner = source?.OwnerPlugin ?? sourcePlugin;
        var artifact = new OutfitProposalArtifact("1", "outfit-record-proposal",
            request.Edition.ToWireName(), request.Mode, request.SourcePlugin.Value, request.SourceFormId.ToString(),
            editorId.Value, inputHash.Value, request.Items.Select(item => item.ToString()).ToImmutableArray(),
            inspection.Masters.Select(item => item.Value).ToImmutableArray(), true,
            (request.TargetFormId ?? request.SourceFormId).ToString(), sourceOwner.Value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputProposal.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, request.OutputProposal.Value, overwrite: false);
            return new OutfitProposalResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException exception)
        {
            AttachCancellationCleanupFailure(
                exception,
                "outfit-proposal-cleanup-failed",
                temporary,
                _cleanup.DeleteIfPresent(temporary));
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddCleanupDiagnostic(
                diagnostics,
                "outfit-proposal-cleanup-failed",
                temporary,
                _cleanup.DeleteIfPresent(temporary));
            diagnostics.Add(new Diagnostic("outfit-proposal-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new OutfitProposalResult(false, artifact, null, diagnostics.ToImmutable());
        }
    }

    private async ValueTask ValidateRaceAsync(OutfitProposalRequest request, PluginInspection inspection,
        FormReference actorRace, ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition || actorRace.FormId.Value is 0 or > 0xFFFFFF)
        {
            diagnostics.Add(new Diagnostic("outfit-proposal-actor-race", DiagnosticSeverity.Error,
                "Actor race admission requires Skyrim SE and a nonzero local FormReference."));
            return;
        }
        var providers = ImmutableArray.CreateBuilder<SkyrimNpcFinishCoreProviderAuthority>();
        foreach (PluginName plugin in inspection.Masters.Concat(request.Items.Select(item => item.Plugin))
                     .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase))
        {
            var path = new WorkspacePath(Path.Combine(Path.GetDirectoryName(request.SourcePlugin.Value)!, plugin.Value));
            if (path == request.SourcePlugin) continue;
            try
            {
                if (!File.Exists(path.Value)) continue;
                if ((File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Copied outfit provider {plugin} is a reparse point.");
                ImmutableArray<Diagnostic> admission =
                    _policy.EvaluateReadRoot(_labRoot, path);
                diagnostics.AddRange(admission);
                if (admission.Any(item =>
                        item.Severity == DiagnosticSeverity.Error))
                    continue;
                byte[] bytes = await File.ReadAllBytesAsync(path.Value, cancellationToken);
                providers.Add(new SkyrimNpcFinishCoreProviderAuthority { Plugin = plugin, Path = path,
                    Sha256 = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), ByteLength = bytes.LongLength });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                diagnostics.Add(new Diagnostic("outfit-proposal-race-provider", DiagnosticSeverity.Error, exception.Message));
            }
        }
        if (HasErrors(diagnostics)) return;
        diagnostics.AddRange(BethesdaSkyrimNpcFinishCoreSourceReader.CheckOutfitRace(request.SourcePlugin, actorRace,
            new SkyrimNpcFinishCoreRequest
            {
                Authorities = new() { Providers = providers.ToImmutable() },
                OutfitPolicy = new() { Policy = SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit, ArmorItems = request.Items }
            }));
    }

    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(_labRoot)) diagnostics.Add(new Diagnostic("outfit-proposal-output-outside-lab",
            DiagnosticSeverity.Error, "Outfit proposals must remain under the K-only lab root."));
        if (!output.Value.EndsWith(".outfit-proposal.json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("outfit-proposal-output-extension", DiagnosticSeverity.Error,
                "Outfit proposals must use the .outfit-proposal.json extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("outfit-proposal-output-exists",
            DiagnosticSeverity.Error, "Outfit proposals never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("outfit-proposal-output-parent-missing",
            DiagnosticSeverity.Error, "The outfit proposal output directory must already exist."));
        else diagnostics.AddRange(_policy.Evaluate(_labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private async ValueTask ValidateItemSignaturesAsync(
        OutfitProposalRequest request,
        PluginInspection sourceInspection,
        PluginName sourcePlugin,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        string? dataRoot = Path.GetDirectoryName(request.SourcePlugin.Value);
        if (dataRoot is null) return;
        var inspections = new Dictionary<string, PluginInspection>(StringComparer.OrdinalIgnoreCase)
        {
            [sourcePlugin.Value] = sourceInspection
        };
        foreach (IGrouping<string, FormReference> group in request.Items.GroupBy(
                     item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inspections.TryGetValue(group.Key, out PluginInspection? providerInspection))
            {
                string providerPath = Path.Combine(dataRoot, group.Key);
                if (!File.Exists(providerPath))
                {
                    diagnostics.Add(new Diagnostic("outfit-proposal-item-provider-missing",
                        DiagnosticSeverity.Error,
                        $"Outfit item provider '{group.Key}' does not exist beside the source plugin."));
                    continue;
                }
                try
                {
                    if (File.GetAttributes(providerPath).HasFlag(FileAttributes.ReparsePoint))
                    {
                        diagnostics.Add(new Diagnostic("outfit-proposal-item-provider-reparse",
                            DiagnosticSeverity.Error,
                            $"Outfit item provider '{group.Key}' may not be a reparse point."));
                        continue;
                    }
                    ImmutableArray<Diagnostic> admission = _policy.EvaluateReadRoot(
                        _labRoot,
                        new WorkspacePath(providerPath));
                    diagnostics.AddRange(admission);
                    if (admission.Any(item =>
                            item.Severity == DiagnosticSeverity.Error))
                        continue;
                    providerInspection = await _pluginReader.ReadAsync(
                        new PluginReadRequest(request.Edition, new WorkspacePath(providerPath)),
                        cancellationToken);
                    inspections.Add(group.Key, providerInspection);
                    diagnostics.AddRange(providerInspection.Diagnostics);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    diagnostics.Add(new Diagnostic("outfit-proposal-item-provider-read-failed",
                        DiagnosticSeverity.Error,
                        $"Outfit item provider '{group.Key}' could not be read: {exception.Message}"));
                    continue;
                }
            }

            foreach (FormReference item in group)
            {
                PluginRecordSummary[] matches = providerInspection.Records
                    .Where(record => record.FormId == item.FormId &&
                        string.Equals(
                            (record.OwnerPlugin ?? providerInspection.Plugin).Value,
                            item.Plugin.Value,
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length != 1)
                {
                    diagnostics.Add(new Diagnostic("outfit-proposal-item-record-unresolved",
                        DiagnosticSeverity.Error,
                        $"Outfit item {item} did not resolve to exactly one owned record."));
                    continue;
                }
                PluginRecordSummary record = matches[0];
                if (record.Signature is not ("ARMO" or "LVLI"))
                    diagnostics.Add(new Diagnostic("outfit-proposal-item-signature-invalid",
                        DiagnosticSeverity.Error,
                        $"Outfit item {item} is {record.Signature}; only ARMO and LVLI are valid."));
                if (record.IsDeleted)
                    diagnostics.Add(new Diagnostic("outfit-proposal-item-deleted",
                        DiagnosticSeverity.Error,
                        $"Outfit item {item} is deleted in its owner plugin."));
            }
        }
    }

    private static OutfitProposalResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics,
        Sha256Hash? inputHash = null) => new(false, null, null, diagnostics.ToImmutable());

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static void AddCleanupDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string code,
        string path,
        FileArtifactCleanupResult cleanup)
    {
        if (!cleanup.Succeeded)
            diagnostics.Add(new Diagnostic(
                code,
                DiagnosticSeverity.Warning,
                $"Cleanup could not remove '{path}': {cleanup.ErrorMessage}"));
    }

    private static void AttachCancellationCleanupFailure(
        OperationCanceledException exception,
        string code,
        string path,
        FileArtifactCleanupResult cleanup)
    {
        if (!cleanup.Succeeded)
            exception.Data[code] =
                $"Cleanup could not remove '{path}': {cleanup.ErrorMessage}";
    }
}
