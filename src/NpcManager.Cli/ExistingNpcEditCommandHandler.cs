using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ExistingNpcEditCommandHandler(
    IExistingNpcEditService service,
    TextWriter output,
    TextWriter error)
{
    private static readonly ImmutableHashSet<string> AllowedOptions =
        LegacyCommandOptionCatalog.For("npc edit-package")
            .Select(option => option.Name)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var errorMessage))
            return Usage(command.Json, errorMessage);

        var result = await service.ExecuteAsync(request, null, cancellationToken);
        if (command.Json)
            output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        else
            output.WriteLine(result.Completed
                ? $"npc edit-package: {result.Verdict}"
                : "npc edit-package: REFUSED");

        if (result.Completed) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.ClassifyFailure(
            result.Diagnostics);
    }

    private static bool TryBuildRequest(
        ParsedCommand command,
        out ExistingNpcEditRequest request,
        out string errorMessage)
    {
        request = default!;
        var unsupportedOption = command.Options.Keys.FirstOrDefault(option => !AllowedOptions.Contains(option));
        if (unsupportedOption is not null)
        {
            errorMessage = $"npc edit-package does not support --{unsupportedOption}.";
            return false;
        }
        var editionValue = command.Options.GetValueOrDefault("edition") ??
                           command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        {
            errorMessage = "npc edit-package requires --edition|--game skyrimse.";
            return false;
        }
        if (command.Options.ContainsKey("consolidate") || command.Options.ContainsKey("esl-flag") ||
            command.Options.ContainsKey("output"))
            return TryBuildConsolidationRequest(command, edition, out request, out errorMessage);
        if (!command.Options.TryGetValue("input-plugin", out var inputPlugin) ||
            !command.Options.TryGetValue("output-root", out var outputRoot) ||
            !command.Options.TryGetValue("plugin", out var outputPlugin) ||
            !command.Options.TryGetValue("npc", out var npcValue) ||
            !command.Options.TryGetValue("output-kind", out var outputKindValue))
        {
            errorMessage = "npc edit-package requires --input-plugin, --input-sha256, --npc, --output-root, --plugin, and --output-kind source-mastered-override|standalone-copy.";
            return false;
        }
        if (!TryParseOutputKind(outputKindValue, out var outputKind))
        {
            errorMessage = "--output-kind must be source-mastered-override or standalone-copy.";
            return false;
        }
        var hashValue = command.Options.GetValueOrDefault("input-sha256") ??
                        command.Options.GetValueOrDefault("expected-sha256");
        if (hashValue is null)
        {
            errorMessage = "npc edit-package requires --input-sha256|--expected-sha256 to bind the source bytes.";
            return false;
        }
        if (!FormId.TryParse(npcValue, out var formId))
        {
            errorMessage = "--npc must be hexadecimal, for example 0x00000800.";
            return false;
        }
        if (!TryBuildGameplayStats(command, edition, out var stats, out errorMessage)) return false;
        if (!MutationCommandHandler.TryKeywordPatch(
                command, edition, out var keywords, out errorMessage) ||
            !MutationCommandHandler.TryFactionPatch(
                command, out var factions, out errorMessage) ||
            !MutationCommandHandler.TryInventoryPatch(
                command, out var inventory, out errorMessage) ||
            !MutationCommandHandler.TryOutfitPatch(
                command, out var outfits, out errorMessage) ||
            !MutationCommandHandler.TryPerkPatch(
                command, out var perks, out errorMessage) ||
            !MutationCommandHandler.TryActorEffectPatch(
                command, out var actorEffects, out errorMessage))
            return false;
        if (!MutationCommandHandler.TryArchetype(
                command, out var archetype, out errorMessage))
            return false;

        try
        {
            EditorId? editorId = command.Options.TryGetValue("editor-id", out var editorValue)
                ? new EditorId(editorValue)
                : null;
            var fullName = command.Options.TryGetValue("name", out var nameValue)
                ? OptionalNpcText.Set(nameValue)
                : default;
            var shortName = command.Options.TryGetValue("short-name", out var shortNameValue)
                ? OptionalNpcText.Set(shortNameValue)
                : default;
            NpcEditableNamesPatch? names = fullName.IsSpecified || shortName.IsSpecified
                ? new NpcEditableNamesPatch(fullName, shortName)
                : null;
            request = new ExistingNpcEditRequest(
                edition,
                new WorkspacePath(inputPlugin),
                new Sha256Hash(hashValue),
                formId,
                new WorkspacePath(outputRoot),
                new PluginName(outputPlugin),
                editorId,
                null,
                stats,
                keywords,
                factions,
                inventory,
                outfits,
                perks,
                actorEffects,
                names,
                archetype,
                outputKind);
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static bool TryBuildConsolidationRequest(ParsedCommand command, GameEdition edition,
        out ExistingNpcEditRequest request, out string errorMessage)
    {
        request = default!;
        string[] allowed = ["edition", "game", "input-plugin", "input-sha256", "expected-sha256", "consolidate", "esl-flag", "output"];
        string? conflict = command.Options.Keys.FirstOrDefault(key => !allowed.Contains(key, StringComparer.OrdinalIgnoreCase));
        string? input = command.Options.GetValueOrDefault("input-plugin");
        string? hash = command.Options.GetValueOrDefault("input-sha256") ?? command.Options.GetValueOrDefault("expected-sha256");
        string? target = command.Options.GetValueOrDefault("output");
        string? providers = command.Options.GetValueOrDefault("consolidate");
        string? esl = command.Options.GetValueOrDefault("esl-flag");
        if (conflict is not null || input is null || hash is null || target is null ||
            (providers is null && esl is null) || (esl is not null && esl is not "true" and not "1"))
        {
            errorMessage = "Fresh-plugin mode requires --input-plugin, --input-sha256, --output and --consolidate or --esl-flag true|1; scalar/package options cannot be mixed.";
            return false;
        }
        try
        {
            var destination = new WorkspacePath(target);
            var paths = providers is null ? ImmutableArray<WorkspacePath>.Empty :
                providers.Split(',').Select(value => new WorkspacePath(value.Trim())).ToImmutableArray();
            request = new ExistingNpcEditRequest(edition, new WorkspacePath(input), new Sha256Hash(hash),
                new FormId(0), new WorkspacePath(Path.GetDirectoryName(destination.Value)!),
                new PluginName(Path.GetFileName(destination.Value)), null, null)
            { ConsolidationOutput = destination, ConsolidationProviders = paths, EslFlag = esl is not null };
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static bool TryBuildGameplayStats(
        ParsedCommand command,
        GameEdition edition,
        out NpcStatsPatch? stats,
        out string errorMessage) =>
        MutationCommandHandler.TryStats(command, edition, out stats, out errorMessage);

    private static bool TryParseOutputKind(
        string value,
        out ExistingNpcEditOutputKind outputKind)
    {
        outputKind = value switch
        {
            "source-mastered-override" => ExistingNpcEditOutputKind.SourceMasteredOverride,
            "standalone-copy" => ExistingNpcEditOutputKind.StandaloneCopy,
            _ => default
        };
        return value is "source-mastered-override" or "standalone-copy";
    }

    private CommandExitCode Usage(bool json, string message)
    {
        if (json)
            error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions));
        else
            error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }



    private sealed record ErrorResponse(string Code, string Message);
}
