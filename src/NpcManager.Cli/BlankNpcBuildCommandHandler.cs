using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class BlankNpcBuildCommandHandler(
    IBlankNpcBuildService service,
    TextWriter output,
    TextWriter error)
{
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
        if (!TryBuildRequest(command, out var request, out var usageError))
            return Usage(command.Json, usageError);

        BlankNpcBuildResult result;
        try
        {
            result = await service.ExecuteAsync(request, null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (command.Json)
                error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("cancelled", "NPC creation was cancelled."), JsonOptions));
            else error.WriteLine("ERROR cancelled: NPC creation was cancelled.");
            return CommandExitCode.Cancelled;
        }
        catch (ArgumentException exception)
        {
            return Usage(command.Json, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure(command.Json, "npc-create-io-failed", exception.Message,
                CommandExitCode.ValidationFailure);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(command.Json, "npc-create-unexpected-failure",
                $"{exception.GetType().Name}: {exception.Message}",
                CommandExitCode.ValidationFailure);
        }

        var artifact = result.Artifact;
        var response = new BuildResponse(
            result.Completed,
            artifact?.Verdict,
            artifact?.AllocatedFormId.ToString(),
            artifact?.Plugin.Value,
            artifact?.PluginSha256.Value,
            artifact?.FaceGeom.Value,
            artifact?.FaceGeomSha256.Value,
            artifact?.FaceTint.Value,
            artifact?.FaceTintSha256.Value,
            artifact?.Manifest.Value,
            artifact?.ManifestSha256.Value,
            artifact?.RuntimeAuthority ?? false,
            result.Diagnostics);
        if (command.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
        else if (result.Completed && artifact is not null)
        {
            output.WriteLine($"npc create: {artifact.Verdict}");
            output.WriteLine($"NPC {artifact.AllocatedFormId} -> {artifact.Plugin.Value}");
            output.WriteLine($"FaceGeom -> {artifact.FaceGeom.Value}");
            output.WriteLine($"FaceTint -> {artifact.FaceTint.Value}");
            output.WriteLine($"Package -> {artifact.Manifest.Value}");
        }
        else
        {
            output.WriteLine("npc create: REFUSED");
            foreach (var diagnostic in result.Diagnostics)
                error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
        }

        return result.Completed ? CommandExitCode.Success : ExitCode(result.Diagnostics);
    }

    private static bool TryBuildRequest(
        ParsedCommand command,
        out BlankNpcBuildRequest request,
        out string errorMessage)
    {
        request = default!;
        var required = new[]
        {
            "provider-manifest", "provider-manifest-sha256",
            "template-plugin", "template-sha256", "template-npc",
            "facegeom-carrier", "facegeom-sha256", "facetint-manifest",
            "provider-root", "dependencies", "output-root", "plugin", "editor-id", "name",
            "role", "sex", "race", "voice", "class", "combat-style", "default-outfit"
        };
        var missing = required.Where(key => !command.Options.TryGetValue(key, out var value) ||
                                            string.IsNullOrWhiteSpace(value)).ToArray();
        if (missing.Length > 0)
        {
            errorMessage = "npc create requires " + string.Join(", ", missing.Select(key => "--" + key)) + ".";
            return false;
        }

        var game = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game") ?? "skyrimse";
        if (!GameEditionExtensions.TryParseWireName(game, out var edition) ||
            edition != GameEdition.SkyrimSpecialEdition)
        {
            errorMessage = "npc create supports only --edition skyrimse.";
            return false;
        }
        if (!FormId.TryParse(command.Options["template-npc"], out var templateNpc))
        {
            errorMessage = "--template-npc must be a hexadecimal FormID.";
            return false;
        }
        if (!string.Equals(command.Options["role"], "static-validation", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "Gate 1 npc create supports only --role static-validation; gameplay roles require their typed faction/package/service plans.";
            return false;
        }
        const NpcCreationRole role = NpcCreationRole.StaticValidation;
        if (!string.Equals(command.Options["sex"], "female", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "The admitted Gate 1 carrier supports only --sex female.";
            return false;
        }
        const NpcSex sex = NpcSex.Female;
        if (!TryReference(command.Options["race"], out var race) ||
            !TryReference(command.Options["voice"], out var voice) ||
            !TryReference(command.Options["class"], out var actorClass) ||
            !TryReference(command.Options["combat-style"], out var combatStyle) ||
            !TryReference(command.Options["default-outfit"], out var defaultOutfit))
        {
            errorMessage = "--race, --voice, --class, --combat-style, and --default-outfit must use Plugin.esp|0xFORMID.";
            return false;
        }

        try
        {
            var traits = new SkyrimNpcCreationTraits(
                sex,
                role,
                Bool(command, "unique", true),
                Bool(command, "essential", false),
                Bool(command, "protected", false),
                Bool(command, "respawns", false),
                Bool(command, "auto-calc-stats", true));
            var stats = new SkyrimNpcCreationStats(
                new NpcLevelValue(NpcLevelMode.Fixed, Decimal(command, "level", 1m)),
                Int16(command, "magicka-offset", 0),
                Int16(command, "stamina-offset", 0),
                Int16(command, "health-offset", 0),
                UInt16(command, "calc-min-level", 1),
                UInt16(command, "calc-max-level", 1),
                Int16(command, "speed", 100),
                Int16(command, "disposition", 35),
                Int16(command, "bleedout", 0),
                UInt16(command, "base-health", 50),
                UInt16(command, "base-magicka", 50),
                UInt16(command, "base-stamina", 50),
                Float(command, "height", 1f),
                Float(command, "weight", 0f),
                UInt16(command, "nam5", 255));
            request = new BlankNpcBuildRequest(
                edition,
                new WorkspacePath(command.Options["provider-manifest"]),
                new Sha256Hash(command.Options["provider-manifest-sha256"]),
                new WorkspacePath(command.Options["template-plugin"]),
                new Sha256Hash(command.Options["template-sha256"]),
                templateNpc,
                new WorkspacePath(command.Options["facegeom-carrier"]),
                new Sha256Hash(command.Options["facegeom-sha256"]),
                new WorkspacePath(command.Options["facetint-manifest"]),
                new WorkspacePath(command.Options["provider-root"]),
                new WorkspacePath(command.Options["dependencies"]),
                new WorkspacePath(command.Options["output-root"]),
                new PluginName(command.Options["plugin"]),
                new NpcCreationIdentity(new EditorId(command.Options["editor-id"]),
                    new NpcName(command.Options["name"])),
                traits,
                new SkyrimNpcCreationReferences(race, voice, actorClass, combatStyle, defaultOutfit),
                TemplateCarrierNpcAppearanceSource.Instance,
                stats);
            errorMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static bool TryReference(string value, out FormReference reference) =>
        FormReference.TryParse(value, out reference);

    private static bool Bool(ParsedCommand command, string name, bool fallback)
    {
        if (!command.Options.TryGetValue(name, out var value)) return fallback;
        if (bool.TryParse(value, out var parsed)) return parsed;
        throw new FormatException($"--{name} must be true or false.");
    }

    private static decimal Decimal(ParsedCommand command, string name, decimal fallback) =>
        command.Options.TryGetValue(name, out var value)
            ? decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture)
            : fallback;

    private static float Float(ParsedCommand command, string name, float fallback) =>
        command.Options.TryGetValue(name, out var value)
            ? float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : fallback;

    private static short Int16(ParsedCommand command, string name, short fallback) =>
        command.Options.TryGetValue(name, out var value)
            ? short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : fallback;

    private static ushort UInt16(ParsedCommand command, string name, ushort fallback) =>
        command.Options.TryGetValue(name, out var value)
            ? ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : fallback;

    private CommandExitCode Usage(bool json, string message)
    {
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private CommandExitCode Failure(bool json, string code, string message, CommandExitCode exitCode)
    {
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse(code, message), JsonOptions));
        else error.WriteLine($"ERROR {code}: {message}");
        return exitCode;
    }

    private static CommandExitCode ExitCode(ImmutableArray<Diagnostic> diagnostics) =>
        DiagnosticExitCodeClassifier.Classify(diagnostics);

    private sealed record BuildResponse(
        bool Completed,
        string? Verdict,
        string? NpcFormId,
        string? Plugin,
        string? PluginSha256,
        string? FaceGeom,
        string? FaceGeomSha256,
        string? FaceTint,
        string? FaceTintSha256,
        string? Manifest,
        string? ManifestSha256,
        bool RuntimeAuthority,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, string Message);
}
