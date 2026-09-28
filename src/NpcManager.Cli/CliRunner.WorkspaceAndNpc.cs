using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

public sealed partial class CliRunner
{
    private ValueTask<CommandExitCode> RunFaceTintPatchAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionText = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (editionText is not null && GameEditionExtensions.TryParseWireName(editionText, out var edition) &&
            edition == GameEdition.SkyrimSpecialEdition)
            return _skyrimFaceTintPatchCommands is null
                ? ValueTask.FromResult(WriteUsageError(command.Json, "Skyrim face tint patch is unavailable in this runner configuration."))
                : _skyrimFaceTintPatchCommands.RunAsync(command, cancellationToken);
        return _faceTintPatchCommands is null
            ? ValueTask.FromResult(WriteUsageError(command.Json, "face tint patch is unavailable in this runner configuration."))
            : _faceTintPatchCommands.RunAsync(command, cancellationToken);
    }

    private static CommandExitCode WriteVersion(bool json, TextWriter output)
    {
        var response = new VersionResponse(BuildInfo.ProductName, BuildInfo.ProductVersion, BuildInfo.SourceLine, BuildInfo.ProtocolVersion,
            BuildInfo.TargetFramework, Environment.Version.ToString());
        Write(response, json, $"{response.Product} {response.Version} ({response.TargetFramework})", output);
        return CommandExitCode.Success;
    }

    private static CommandExitCode WriteCapabilities(bool json, TextWriter output)
    {
        var response = new CapabilitiesResponse(BuildInfo.ProductName, BuildInfo.ProductVersion, BuildInfo.SourceLine,
            BuildInfo.ProtocolVersion, CommandCatalog.All,
            CommandCatalog.All.Select(command => new CapabilityLedgerMapping(command.Name,
                CommandCatalog.LedgerIdsFor(command.Name))).ToImmutableArray());
        Write(response, json, $"{response.Product}: {response.Commands.Length} commands", output);
        return CommandExitCode.Success;
    }

    private static CommandExitCode WriteDiagnosis(bool json, TextWriter output)
    {
        var response = new DiagnosisResponse(BuildInfo.ProductVersion, Environment.OSVersion.Platform.ToString(),
            Environment.Is64BitProcess, "read-only M1 shell; no game or Data root was opened");
        Write(response, json, $"diagnose: {response.Status}", output);
        return CommandExitCode.Success;
    }

    private async ValueTask<CommandExitCode> RunPreflightAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (command.Options.ContainsKey("plugin") || command.Options.ContainsKey("asset-index"))
            return await RunArchiveConsistencyPreflightAsync(command, cancellationToken);

        if (command.Options.ContainsKey("load-order") || command.Options.ContainsKey("loadorder"))
            return await RunReviewedIntakePreflightAsync(command, cancellationToken);

        var hasGameRootOptions = command.Options.ContainsKey("game") || command.Options.ContainsKey("edition") ||
                                 command.Options.ContainsKey("data-root");
        if (hasGameRootOptions)
            return await RunGameRootPreflightAsync(command, cancellationToken);

        if (!command.Options.TryGetValue("workspace-root", out var workspaceRoot) ||
            !command.Options.TryGetValue("output-root", out var outputRoot))
        {
            return WriteUsageError(command.Json, "workspace preflight requires --workspace-root and --output-root.");
        }

        try
        {
            var result = await services.preflightService.EvaluateAsync(
                new WorkspacePreflightRequest(new WorkspacePath(workspaceRoot), new WorkspacePath(outputRoot)),
                cancellationToken);
            var response = new PreflightResponse(result.IsAllowed, result.WorkspaceRoot.Value, result.OutputRoot.Value, result.Diagnostics);
            Write(response, command.Json, response.IsAllowed ? "preflight: PASS" : "preflight: REFUSED");
            return response.IsAllowed
                ? CommandExitCode.Success
                : DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            return WriteUsageError(command.Json, exception.Message);
        }
    }

    private async ValueTask<CommandExitCode> RunReviewedIntakePreflightAsync(
        ParsedCommand command, CancellationToken cancellationToken)
    {
        if (services.reviewedGameIntakeService is null)
            return WriteUsageError(command.Json,
                "Reviewed workspace preflight is unavailable in this runner configuration.");
        try
        {
            ReviewedWorkspacePreflightBinding binding =
                ReviewedWorkspacePreflightBinder.Bind(
                    command,
                    _workspaceRoot,
                    requireExplicitWorkspace: false);
            if (!binding.IsValid)
                return WriteUsageError(command.Json, binding.ErrorMessage!);
            ReviewedGameIntakeRequest request = binding.Request!;
            var result = await services.reviewedGameIntakeService.ReviewAsync(request, cancellationToken);
            var response = ReviewedGameIntakeResponse.From(request, result);
            Write(response, command.Json, result.IsAccepted
                ? $"preflight reviewed: PASS {response.Plugins.Length} plugins"
                : "preflight reviewed: REFUSED");
            return result.IsAccepted ? CommandExitCode.Success : ExitCodeForDiagnostics(result.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            return WriteUsageError(command.Json, exception.Message);
        }
    }

    private async ValueTask<CommandExitCode> RunArchiveConsistencyPreflightAsync(
        ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
            return WriteUsageError(command.Json, "workspace preflight requires --game|--edition fallout4|skyrimse.");
        if (!command.Options.TryGetValue("plugin", out var plugin) ||
            !command.Options.TryGetValue("asset-index", out var assetIndex))
            return WriteUsageError(command.Json,
                "workspace preflight archive consistency requires --plugin and --asset-index.");

        try
        {
            var result = await services.archiveConsistencyService.EvaluateAsync(new ArchiveConsistencyRequest(
                edition, new WorkspacePath(plugin), new WorkspacePath(assetIndex)), cancellationToken);
            var response = ArchiveConsistencyResponse.From(result);
            Write(response, command.Json, response.IsConsistent ? "preflight archives: PASS" : "preflight archives: REFUSED");
            return response.IsConsistent ? CommandExitCode.Success : ExitCodeForDiagnostics(result.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            return WriteUsageError(command.Json, exception.Message);
        }
    }

    private async ValueTask<CommandExitCode> RunGameRootPreflightAsync(ParsedCommand command,
        CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
            return WriteUsageError(command.Json, "workspace preflight requires --game|--edition fallout4|skyrimse.");
        if (!command.Options.TryGetValue("data-root", out var dataRoot) ||
            !command.Options.TryGetValue("output-root", out var outputRoot))
            return WriteUsageError(command.Json, "workspace preflight with --game requires --data-root and --output-root.");

        try
        {
            var workspaceRoot = new WorkspacePath(command.Options.GetValueOrDefault("workspace-root") ??
                _workspaceRoot.Value);
            var result = await services.gameRootPreflightService.EvaluateAsync(new GameRootPreflightRequest(
                edition, workspaceRoot, new WorkspacePath(dataRoot), new WorkspacePath(outputRoot)), cancellationToken);
            var response = new GameRootPreflightResponse("1", result.Edition.ToWireName(),
                result.IsAllowed, result.WorkspaceRoot.Value, result.DataRoot.Value, result.OutputRoot.Value,
                result.Diagnostics);
            Write(response, command.Json, response.IsAllowed ? "preflight: PASS" : "preflight: REFUSED");
            return response.IsAllowed ? CommandExitCode.Success : ExitCodeForDiagnostics(result.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            return WriteUsageError(command.Json, exception.Message);
        }
    }

    private async ValueTask<CommandExitCode> RunNpcListAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildInventoryRequest(command, out var request, out var errorMessage))
        {
            return WriteUsageError(command.Json, errorMessage);
        }

        var inventory = await services.inventoryService.ReadAsync(request with { IncludeAssets = false }, cancellationToken);
        var response = InventoryResponse.From(inventory);
        Write(response, command.Json, $"npc: {response.Npcs.Length} records across {response.Plugins.Length} plugins");
        return ExitCodeForDiagnostics(inventory.Diagnostics);
    }

    private async ValueTask<CommandExitCode> RunNpcInspectAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!command.Options.TryGetValue("npc", out var npcValue))
            return WriteUsageError(command.Json, "npc inspect requires --npc <form-id>.");
        if (!TryBuildInventoryRequest(command, out var request, out var errorMessage))
            return WriteUsageError(command.Json, errorMessage);
        if (!FormId.TryParse(npcValue, out var formId))
            return WriteUsageError(command.Json, "--npc must be hexadecimal, for example 0x00000800.");

        var inventory = await services.inventoryService.ReadAsync(request with { FormId = formId, IncludeAssets = false }, cancellationToken);
        var matches = inventory.Npcs.Where(npc => npc.FormId == formId).ToImmutableArray();
        if (matches.Length != 1)
        {
            var diagnostic = new Diagnostic("npc-not-unique", DiagnosticSeverity.Error,
                $"NPC {formId} resolved to {matches.Length} records; an explicit plugin is required for inspection.");
            var response = new NpcInspectResponse("1", null, [diagnostic],
                ["factions", "inventory", "outfits", "perks", "appearance", "scripts"]);
            Write(response, command.Json, "npc inspect: REFUSED");
            return CommandExitCode.ValidationFailure;
        }

        var selected = matches[0];
        var defaults = NpcInheritedDefaultsAudit.Read(request.Edition, _workspaceRoot,
            new WorkspacePath(Path.Combine(request.DataRoot.Value, selected.Plugin.Value)),
            new FormReference(selected.OwnerPlugin ?? selected.Plugin, selected.FormId), cancellationToken);
        var result = new NpcInspectResponse("1", NpcResponse.From(selected), inventory.Diagnostics.AddRange(defaults.Diagnostics),
            ["factions", "inventory", "outfits", "perks", "appearance", "scripts"], defaults.Fields);
        Write(result, command.Json, $"npc inspect: {matches[0].FormId} {matches[0].EditorId ?? matches[0].Name ?? "<unnamed>"}");
        return ExitCodeForDiagnostics(inventory.Diagnostics);
    }

    private async ValueTask<CommandExitCode> RunAssetIndexAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildInventoryRequest(command, out var request, out var errorMessage))
        {
            return WriteUsageError(command.Json, errorMessage);
        }

        if (command.Options.TryGetValue("output", out var outputValue))
        {
            try
            {
                var export = await services.assetIndexExportService.ExportAsync(new AssetIndexExportRequest(
                    request.Edition, request.DataRoot, new WorkspacePath(outputValue)), cancellationToken);
                var exportResponse = AssetIndexExportResponse.From(export);
                Write(exportResponse, command.Json, exportResponse.Written ? "assets: INDEX WRITTEN" : "assets: INDEX REFUSED");
                return ExitCodeForDiagnostics(export.Diagnostics);
            }
            catch (ArgumentException exception)
            {
                return WriteUsageError(command.Json, exception.Message);
            }
        }

        var inventory = await services.inventoryService.ReadAsync(request with { IncludeAssets = true }, cancellationToken);
        if (inventory.Assets is null)
        {
            var refused = new AssetResponse(request.Edition.ToWireName(), [], inventory.Diagnostics);
            Write(refused, command.Json, "assets: REFUSED");
            return ExitCodeForDiagnostics(inventory.Diagnostics);
        }
        var assets = inventory.Assets;
        var response = AssetResponse.From(assets);
        Write(response, command.Json, $"assets: {response.Providers.Length} providers");
        return ExitCodeForDiagnostics(response.Diagnostics);
    }

}
