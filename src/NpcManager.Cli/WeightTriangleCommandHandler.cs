using System.Globalization;
using System.Text.Json;
using NpcManager.Application;

namespace NpcManager.Cli;

internal sealed class WeightTriangleCommandHandler(TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public CommandExitCode Run(ParsedCommand command)
    {
        if (!TryGame(command, out var gameError)) return WriteUsageError(command.Json, gameError);
        if (!TryTriangle(command.Options.GetValueOrDefault("triangle") ?? command.Options.GetValueOrDefault("current"),
                out var current, out var parseError))
            return WriteUsageError(command.Json, parseError);

        WeightTriangle result;
        string operation;
        if (command.Name == "body weight normalize")
        {
            if (!WeightTriangleMath.TryNormalize(current.Thin, current.Muscular, current.Fat, out result, out var error))
                return WriteValidationError(command.Json, error);
            operation = "normalize";
        }
        else
        {
            if (!WeightTriangleAxisExtensions.TryParseWireName(command.Options.GetValueOrDefault("axis") ?? string.Empty, out var axis))
                return WriteUsageError(command.Json, "body weight redistribute requires --axis thin|muscular|fat.");
            if (!command.Options.TryGetValue("value", out var valueText) ||
                !float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return WriteUsageError(command.Json, "body weight redistribute requires finite --value.");
            if (!WeightTriangleMath.TryRedistribute(current, axis, value, out result, out var error))
                return WriteValidationError(command.Json, error);
            operation = "redistribute";
        }

        var response = new WeightTriangleResponse(operation, result.Thin, result.Muscular, result.Fat);
        if (command.Json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else output.WriteLine($"body weight {operation}: thin={result.Thin:R}, muscular={result.Muscular:R}, fat={result.Fat:R}");
        return CommandExitCode.Success;
    }

    private static bool TryGame(ParsedCommand command, out string message)
    {
        var game = command.Options.GetValueOrDefault("game") ?? command.Options.GetValueOrDefault("edition");
        if (!string.Equals(game, "fallout4", StringComparison.OrdinalIgnoreCase))
        {
            message = "body weight triangle commands require --game fallout4.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryTriangle(string? text, out WeightTriangle result, out string message)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            message = "The command requires --triangle thin=<n>,muscular=<n>,fat=<n>.";
            return false;
        }

        float? thin = null, muscular = null, fat = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length != 2 || !seen.Add(pieces[0]) ||
                !float.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                message = "--triangle uses unique thin|muscular|fat=<number> entries.";
                return false;
            }

            switch (pieces[0].ToLowerInvariant())
            {
                case "thin": thin = value; break;
                case "muscular": muscular = value; break;
                case "fat": fat = value; break;
                default: message = $"Unknown weight-triangle channel '{pieces[0]}'."; return false;
            }
        }

        if (thin is null || muscular is null || fat is null)
        {
            message = "--triangle requires thin, muscular, and fat channels.";
            return false;
        }

        result = new WeightTriangle(thin.Value, muscular.Value, fat.Value);
        message = string.Empty;
        return true;
    }

    private CommandExitCode WriteUsageError(bool json, string message)
    {
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private CommandExitCode WriteValidationError(bool json, string message)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(new ErrorResponse("weight-triangle-invalid", message), JsonOptions));
        else output.WriteLine($"body weight triangle: REFUSED ({message})");
        return CommandExitCode.ValidationFailure;
    }

    private sealed record ErrorResponse(string Code, string Message);
    private sealed record WeightTriangleResponse(string Operation, float Thin, float Muscular, float Fat);
}
