using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SkyrimFollowerFinishPairCommandHandler(
    ISkyrimFollowerFinishPairService service,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            string[] required = command.Name switch
            {
                "npc follower-finish pair-analyze" =>
                    ["request", "request-sha256", "proposal"],
                "npc follower-finish pair-apply" =>
                    [
                        "request",
                        "request-sha256",
                        "proposal",
                        "proposal-sha256"
                    ],
                "npc follower-finish pair-verify" =>
                    [
                        "request",
                        "request-sha256",
                        "proposal",
                        "proposal-sha256",
                        "manifest"
                    ],
                _ => []
            };
            if (required.Length == 0)
                return Failure(
                    command.Json,
                    "usage-error",
                    "Unsupported paired follower-finish command.",
                    CommandExitCode.UsageError);
            string? unknown = command.Options.Keys
                .FirstOrDefault(option =>
                    !required.Contains(
                        option,
                        StringComparer.OrdinalIgnoreCase));
            if (unknown is not null)
                return Failure(
                    command.Json,
                    "unknown-option",
                    $"Paired follower-finish does not support --{unknown}.",
                    CommandExitCode.UsageError);
            string? missing = required.FirstOrDefault(option =>
                !command.Options.TryGetValue(
                    option,
                    out string? value) ||
                string.IsNullOrWhiteSpace(value) ||
                string.Equals(
                    value,
                    "true",
                    StringComparison.OrdinalIgnoreCase));
            if (missing is not null)
                return Failure(
                    command.Json,
                    "usage-error",
                    $"Paired follower-finish requires --{missing}.",
                    CommandExitCode.UsageError);

            WorkspacePath request =
                new(command.Options["request"]);
            Sha256Hash requestHash =
                new(command.Options["request-sha256"]);
            WorkspacePath proposal =
                new(command.Options["proposal"]);
            SkyrimFollowerFinishPairResult result =
                command.Name switch
                {
                    "npc follower-finish pair-analyze" =>
                        await service.AnalyzeAsync(
                            request,
                            requestHash,
                            proposal,
                            cancellationToken)
                            .ConfigureAwait(false),
                    "npc follower-finish pair-apply" =>
                        await service.ApplyAsync(
                            request,
                            requestHash,
                            proposal,
                            new Sha256Hash(
                                command.Options[
                                    "proposal-sha256"]),
                            cancellationToken)
                            .ConfigureAwait(false),
                    "npc follower-finish pair-verify" =>
                        await service.VerifyAsync(
                            request,
                            requestHash,
                            proposal,
                            new Sha256Hash(
                                command.Options[
                                    "proposal-sha256"]),
                            new WorkspacePath(
                                command.Options["manifest"]),
                            cancellationToken)
                            .ConfigureAwait(false),
                    _ => throw new InvalidOperationException(
                        "Unsupported paired follower-finish command.")
                };
            Write(command.Json, result);
            if (result.RuntimeAuthority)
                return Failure(
                    command.Json,
                    "follower-finish-pair-runtime-authority",
                    "A static paired transaction cannot claim runtime authority.",
                    CommandExitCode.ValidationFailure);
            return result.Succeeded
                ? CommandExitCode.Success
                : DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                command.Json,
                "cancelled",
                "Paired follower-finish work was cancelled.",
                CommandExitCode.Cancelled);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FormatException or
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException)
        {
            return Failure(
                command.Json,
                "follower-finish-pair-request-invalid",
                exception.Message,
                CommandExitCode.ValidationFailure);
        }
    }

    private void Write(
        bool json,
        SkyrimFollowerFinishPairResult result)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(
                new
                {
                    result.Succeeded,
                    result.Verdict,
                    proposal = result.Proposal?.Value,
                    proposalSha256 =
                        result.ProposalSha256?.Value,
                    manifest = result.Manifest?.Value,
                    manifestSha256 =
                        result.ManifestSha256?.Value,
                    outputPlugin =
                        result.OutputPlugin?.Value,
                    outputPluginSha256 =
                        result.OutputPluginSha256?.Value,
                    outputZip = result.OutputZip?.Value,
                    outputZipSha256 =
                        result.OutputZipSha256?.Value,
                    result.RuntimeAuthority,
                    result.Diagnostics
                },
                JsonOptions));
            return;
        }
        output.WriteLine(
            $"npc follower-finish pair: {result.Verdict}");
        foreach (Diagnostic diagnostic in result.Diagnostics)
            output.WriteLine(
                $"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
    }

    private CommandExitCode Failure(
        bool json,
        string code,
        string message,
        CommandExitCode exit)
    {
        if (json)
            error.WriteLine(JsonSerializer.Serialize(
                new { code, message },
                JsonOptions));
        else
            error.WriteLine($"ERROR {code}: {message}");
        return exit;
    }
}
