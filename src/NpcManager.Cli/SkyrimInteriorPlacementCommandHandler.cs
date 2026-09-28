using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed class SkyrimInteriorPlacementCommandHandler(
    ISkyrimInteriorPlacementService service,
    WorkspacePath workspaceRoot,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        try
        {
            return command.Name switch
            {
                "npc placement interior analyze" => await RunAnalyzeAsync(command, cancellationToken),
                "npc placement interior apply" => await RunApplyAsync(command, cancellationToken),
                "npc placement interior verify" => await RunVerifyAsync(command, cancellationToken),
                _ => Usage(command, "Unsupported interior placement command.")
            };
        }
        catch (OperationCanceledException)
        {
            return Failure(command, "cancelled", "Interior placement work was cancelled.", CommandExitCode.Cancelled);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Failure(command, "interior-placement-invalid", exception.Message, CommandExitCode.ValidationFailure);
        }
    }

    private async ValueTask<CommandExitCode> RunAnalyzeAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (Validate(command, ["request", "request-sha256", "proposal"], ["request", "request-sha256", "proposal"]) is { } failure)
            return failure;
        (SkyrimInteriorPlacementRequest request, Sha256Hash requestHash) = await LoadRequestAsync(command, cancellationToken);
        WorkspacePath proposalPath = PathOption(command, "proposal");
        SkyrimInteriorPlacementProposalResult result = await service.AnalyzeAsync(request, requestHash, proposalPath, cancellationToken);
        return WriteResult(command, new
        {
            schema = SkyrimInteriorPlacementProposal.SchemaIdentifier,
            command = command.Name,
            proposed = result.Proposed,
            status = result.Proposal?.Status.ToString() ?? SkyrimInteriorPlacementStatus.Refused.ToString(),
            proposalPath = result.ProposalPath?.Value,
            proposalSha256 = result.ProposalSha256?.Value,
            diagnostics = result.Diagnostics
        }, result.Proposed
            ? CommandExitCode.Success
            : DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics));
    }

    private async ValueTask<CommandExitCode> RunApplyAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (Validate(command, ["request", "request-sha256", "proposal", "proposal-sha256"], ["request", "request-sha256", "proposal", "proposal-sha256"]) is { } failure)
            return failure;
        (SkyrimInteriorPlacementRequest request, Sha256Hash requestHash) = await LoadRequestAsync(command, cancellationToken);
        WorkspacePath proposalPath = PathOption(command, "proposal");
        Sha256Hash proposalHash = HashOption(command, "proposal-sha256");
        byte[] proposalBytes = await ReadProposalBoundFileAsync(proposalPath, proposalHash, cancellationToken);
        SkyrimInteriorPlacementProposal proposal = SkyrimInteriorPlacementDocumentCodec.ParseProposal(proposalBytes, workspaceRoot);
        SkyrimInteriorPlacementApplyResult result = await service.ApplyAsync(request, requestHash, proposal, proposalHash, cancellationToken);
        return WriteResult(command, new
        {
            schema = SkyrimInteriorPlacementManifest.SchemaIdentifier,
            command = command.Name,
            applied = result.Applied,
            status = result.Manifest?.Status.ToString() ?? SkyrimInteriorPlacementStatus.Refused.ToString(),
            outputRoot = result.OutputRoot?.Value,
            archive = result.Archive?.Value,
            manifest = result.Manifest is null ? null : Path.Combine(result.OutputRoot!.Value.Value, "interior-placement.manifest.json"),
            diagnostics = result.Diagnostics
        }, result.Applied
            ? CommandExitCode.Success
            : DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics));
    }

    private async ValueTask<CommandExitCode> RunVerifyAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (Validate(command, ["manifest", "manifest-sha256"], ["manifest", "manifest-sha256"]) is { } failure)
            return failure;
        WorkspacePath manifestPath = PathOption(command, "manifest");
        Sha256Hash manifestHash = HashOption(command, "manifest-sha256");
        _ = await ReadBoundFileAsync(manifestPath, manifestHash, cancellationToken);
        SkyrimInteriorPlacementVerificationResult result = await service.VerifyAsync(manifestPath, manifestHash, cancellationToken);
        return WriteResult(command, new
        {
            schema = SkyrimInteriorPlacementVerification.SchemaIdentifier,
            command = command.Name,
            verified = result.Verified,
            status = result.Verification?.Status.ToString() ?? SkyrimInteriorPlacementStatus.Refused.ToString(),
            verification = result.Verification,
            diagnostics = result.Diagnostics
        }, result.Verified
            ? CommandExitCode.Success
            : DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics));
    }

    private async ValueTask<(SkyrimInteriorPlacementRequest Request, Sha256Hash Hash)> LoadRequestAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        WorkspacePath path = PathOption(command, "request");
        Sha256Hash hash = HashOption(command, "request-sha256");
        byte[] bytes = await ReadBoundFileAsync(path, hash, cancellationToken);
        return (SkyrimInteriorPlacementDocumentCodec.ParseRequest(bytes, workspaceRoot), hash);
    }

    private async ValueTask<byte[]> ReadBoundFileAsync(WorkspacePath path, Sha256Hash expected, CancellationToken cancellationToken)
    {
        if (!path.IsUnder(workspaceRoot) || !File.Exists(path.Value) || (File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Interior placement documents must be ordinary K-local files.");
        byte[] bytes = await File.ReadAllBytesAsync(path.Value, cancellationToken);
        Sha256Hash actual = new(Convert.ToHexString(SHA256.HashData(bytes)));
        return actual == expected ? bytes : throw new InvalidDataException("Interior placement document hash binding failed.");
    }

    private async ValueTask<byte[]> ReadProposalBoundFileAsync(WorkspacePath path, Sha256Hash expected, CancellationToken cancellationToken)
    {
        if (!path.IsUnder(workspaceRoot) || !File.Exists(path.Value) || (File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Interior placement documents must be ordinary K-local files.");
        byte[] bytes = await File.ReadAllBytesAsync(path.Value, cancellationToken);
        return SkyrimInteriorPlacementDocumentCodec.HashProposalWithoutSelf(bytes) == expected
            ? bytes
            : throw new InvalidDataException("Interior placement proposal hash binding failed.");
    }

    private CommandExitCode? Validate(ParsedCommand command, IReadOnlyCollection<string> allowed, IReadOnlyCollection<string> required)
    {
        if (command.DuplicateOptions.Length != 0)
            return Usage(command, "Interior placement options may not be repeated.");
        string? unknown = command.Options.Keys.FirstOrDefault(key => !allowed.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
            return Usage(command, $"Interior placement does not support --{unknown}.");
        string? missing = required.FirstOrDefault(key => !command.Options.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value) || value == "true");
        return missing is null ? null : Usage(command, $"Interior placement requires --{missing}.");
    }

    private static WorkspacePath PathOption(ParsedCommand command, string name) => new(command.Options[name]);

    private static Sha256Hash HashOption(ParsedCommand command, string name) => new(command.Options[name]);

    private CommandExitCode WriteResult(ParsedCommand command, object result, CommandExitCode exitCode)
    {
        output.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return exitCode;
    }

    private CommandExitCode Usage(ParsedCommand command, string message) => Failure(command, "usage", message, CommandExitCode.UsageError);

    private CommandExitCode Failure(ParsedCommand command, string code, string message, CommandExitCode exitCode)
    {
        if (command.Json)
            output.WriteLine(JsonSerializer.Serialize(new { code, message }, JsonOptions));
        else
            error.WriteLine($"{code}: {message}");
        return exitCode;
    }
}
