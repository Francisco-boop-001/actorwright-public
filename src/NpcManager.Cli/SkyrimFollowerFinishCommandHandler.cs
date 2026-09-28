using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class SkyrimFollowerFinishCommandHandler(
    ISkyrimFollowerFinishService service,
    ISkyrimFollowerFinishRequestFileLoader loader,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling =
            JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase)
        }
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            return command.Name switch
            {
                "npc follower-finish analyze" =>
                    await RunAnalyzeAsync(
                        command, cancellationToken)
                        .ConfigureAwait(false),
                "npc follower-finish apply" =>
                    await RunApplyAsync(
                        command, cancellationToken)
                        .ConfigureAwait(false),
                "npc follower-finish verify" =>
                    await RunVerifyAsync(
                        command, cancellationToken)
                        .ConfigureAwait(false),
                _ => Usage(
                    command.Json,
                    "Unsupported follower-finish command.")
            };
        }
        catch (OperationCanceledException)
        {
            return Failure(
                command.Json,
                "cancelled",
                "Follower-finish work was cancelled.",
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
                "follower-finish-request-invalid",
                exception.Message,
                CommandExitCode.ValidationFailure);
        }
    }

    private async ValueTask<CommandExitCode> RunAnalyzeAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        string[] required =
        [
            "request",
            "request-sha256",
            "proposal"
        ];
        CommandExitCode? usage =
            ValidateOptions(command, required, required);
        if (usage is not null)
            return usage.Value;

        (SkyrimFollowerFinishRequest? request,
            Sha256Hash requestHash,
            CommandExitCode? failure) =
            await LoadRequestAsync(
                command, cancellationToken)
                .ConfigureAwait(false);
        if (failure is not null)
            return failure.Value;

        WorkspacePath proposalPath =
            PathOption(command, "proposal");
        SkyrimFollowerFinishProposalResult result =
            await service.AnalyzeAsync(
                request!,
                requestHash,
                proposalPath,
                cancellationToken).ConfigureAwait(false);
        FollowerFinishResponse response = Response(
            command.Name,
            request!,
            requestHash,
            result.Proposed,
            completed: false,
            verified: false,
            result.Proposed
                ? "READY_FOR_REVIEWED_WRITE"
                : "REFUSED",
            result.ProposalPath ?? proposalPath,
            result.ProposalSha256,
            manifest: null,
            pluginWrite: null,
            packageVerification: null,
            packageArchive: null,
            result.Diagnostics);
        Write(command.Json, response,
            result.Proposed
                ? "npc follower-finish analyze: READY_FOR_REVIEWED_WRITE"
                : "npc follower-finish analyze: REFUSED");
        return Exit(result.Proposed, result.Diagnostics);
    }

    private async ValueTask<CommandExitCode> RunApplyAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        string[] required =
        [
            "request",
            "request-sha256",
            "proposal",
            "proposal-sha256"
        ];
        CommandExitCode? usage =
            ValidateOptions(command, required, required);
        if (usage is not null)
            return usage.Value;

        (SkyrimFollowerFinishRequest? request,
            Sha256Hash requestHash,
            CommandExitCode? requestFailure) =
            await LoadRequestAsync(
                command, cancellationToken)
                .ConfigureAwait(false);
        if (requestFailure is not null)
            return requestFailure.Value;
        (SkyrimFollowerFinishProposal? proposal,
            Sha256Hash proposalHash,
            WorkspacePath proposalPath,
            CommandExitCode? proposalFailure) =
            await LoadProposalAsync(
                command,
                request!,
                requestHash,
                cancellationToken).ConfigureAwait(false);
        if (proposalFailure is not null)
            return proposalFailure.Value;

        SkyrimFollowerFinishResult result =
            await service.ApplyAsync(
                request!,
                requestHash,
                proposal!,
                proposalHash,
                null,
                cancellationToken).ConfigureAwait(false);
        if (result.RuntimeAuthority)
        {
            return Failure(
                command.Json,
                "follower-finish-runtime-authority",
                "A static follower-finish apply result cannot claim runtime authority.",
                CommandExitCode.ValidationFailure);
        }
        FollowerFinishResponse response = Response(
            command.Name,
            request!,
            requestHash,
            proposed: false,
            result.Completed,
            verified: false,
            result.Completed
                ? "STATIC_PASS_RUNTIME_REQUIRED"
                : "REFUSED",
            proposalPath,
            proposalHash,
            manifest: null,
            result.PluginWrite,
            result.PackageVerification,
            result.PackageArchive,
            result.Diagnostics);
        Write(command.Json, response,
            result.Completed
                ? "npc follower-finish apply: STATIC_PASS_RUNTIME_REQUIRED"
                : "npc follower-finish apply: REFUSED");
        return Exit(result.Completed, result.Diagnostics);
    }

    private async ValueTask<CommandExitCode> RunVerifyAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        string[] required =
        [
            "request",
            "request-sha256",
            "proposal",
            "proposal-sha256",
            "manifest"
        ];
        CommandExitCode? usage =
            ValidateOptions(command, required, required);
        if (usage is not null)
            return usage.Value;

        (SkyrimFollowerFinishRequest? request,
            Sha256Hash requestHash,
            CommandExitCode? requestFailure) =
            await LoadRequestAsync(
                command,
                cancellationToken,
                SkyrimFollowerFinishDocumentLoadMode
                    .PostWriteVerification)
                .ConfigureAwait(false);
        if (requestFailure is not null)
            return requestFailure.Value;
        (SkyrimFollowerFinishProposal? proposal,
            Sha256Hash proposalHash,
            WorkspacePath proposalPath,
            CommandExitCode? proposalFailure) =
            await LoadProposalAsync(
                command,
                request!,
                requestHash,
                cancellationToken,
                SkyrimFollowerFinishDocumentLoadMode
                    .PostWriteVerification).ConfigureAwait(false);
        if (proposalFailure is not null)
            return proposalFailure.Value;

        WorkspacePath manifest =
            PathOption(command, "manifest");
        SkyrimFollowerFinishVerificationResult result =
            await service.VerifyAsync(
                request!,
                requestHash,
                proposal!,
                proposalHash,
                manifest,
                cancellationToken).ConfigureAwait(false);
        if (result.PluginVerification?.RuntimeAuthority == true ||
            result.PackageVerification?.Artifact?.RuntimeProof == true)
        {
            return Failure(
                command.Json,
                "follower-finish-runtime-authority",
                "A static follower-finish verification cannot claim runtime authority.",
                CommandExitCode.ValidationFailure);
        }
        FollowerFinishResponse response = Response(
            command.Name,
            request!,
            requestHash,
            proposed: false,
            completed: false,
            result.Verified,
            result.Verified
                ? "STATIC_PASS_RUNTIME_REQUIRED"
                : "REFUSED",
            proposalPath,
            proposalHash,
            manifest,
            pluginWrite: null,
            result.PackageVerification,
            packageArchive: null,
            result.Diagnostics);
        Write(command.Json, response,
            result.Verified
                ? "npc follower-finish verify: STATIC_PASS_RUNTIME_REQUIRED"
                : "npc follower-finish verify: REFUSED");
        return Exit(result.Verified, result.Diagnostics);
    }

    private async ValueTask<(
        SkyrimFollowerFinishRequest? Request,
        Sha256Hash RequestHash,
        CommandExitCode? Failure)> LoadRequestAsync(
        ParsedCommand command,
        CancellationToken cancellationToken,
        SkyrimFollowerFinishDocumentLoadMode mode =
            SkyrimFollowerFinishDocumentLoadMode.PreWrite)
    {
        WorkspacePath path =
            PathOption(command, "request");
        Sha256Hash hash =
            HashOption(command, "request-sha256");
        SkyrimFollowerFinishRequestLoadResult loaded =
            await loader.LoadRequestAsync(
                path,
                hash,
                cancellationToken,
                mode).ConfigureAwait(false);
        if (!loaded.Loaded || loaded.Request is null)
        {
            return (
                null,
                hash,
                DiagnosticFailure(
                    command.Json,
                    "follower-finish-request-refused",
                    loaded.Diagnostics));
        }
        return (loaded.Request, hash, null);
    }

    private async ValueTask<(
        SkyrimFollowerFinishProposal? Proposal,
        Sha256Hash ProposalHash,
        WorkspacePath ProposalPath,
        CommandExitCode? Failure)> LoadProposalAsync(
        ParsedCommand command,
        SkyrimFollowerFinishRequest request,
        Sha256Hash requestHash,
        CancellationToken cancellationToken,
        SkyrimFollowerFinishDocumentLoadMode mode =
            SkyrimFollowerFinishDocumentLoadMode.PreWrite)
    {
        WorkspacePath path =
            PathOption(command, "proposal");
        Sha256Hash hash =
            HashOption(command, "proposal-sha256");
        SkyrimFollowerFinishProposalLoadResult loaded =
            await loader.LoadProposalAsync(
                path,
                hash,
                cancellationToken,
                mode).ConfigureAwait(false);
        if (!loaded.Loaded || loaded.Proposal is null)
        {
            return (
                null,
                hash,
                path,
                DiagnosticFailure(
                    command.Json,
                    "follower-finish-proposal-refused",
                    loaded.Diagnostics));
        }
        if (loaded.Proposal.RequestSha256 != requestHash)
        {
            return (
                null,
                hash,
                path,
                Failure(
                    command.Json,
                    "follower-finish-proposal-request-binding",
                    "The proposal does not bind the exact loaded request-file hash.",
                    CommandExitCode.ValidationFailure));
        }
        return (loaded.Proposal, hash, path, null);
    }

    private CommandExitCode? ValidateOptions(
        ParsedCommand command,
        IReadOnlyCollection<string> allowed,
        IReadOnlyCollection<string> required)
    {
        string? unknown = command.Options.Keys
            .FirstOrDefault(option =>
                !allowed.Contains(
                    option,
                    StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
        {
            return Failure(
                command.Json,
                "unknown-option",
                $"npc follower-finish does not support --{unknown}.",
                CommandExitCode.UsageError);
        }
        string? missing = required.FirstOrDefault(option =>
            !command.Options.TryGetValue(option, out string? value) ||
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        return missing is null
            ? null
            : Usage(
                command.Json,
                $"npc follower-finish requires --{missing}.");
    }

    private static WorkspacePath PathOption(
        ParsedCommand command,
        string name) =>
        new(command.Options[name]);

    private static Sha256Hash HashOption(
        ParsedCommand command,
        string name) =>
        new(command.Options[name]);

    private static FollowerFinishResponse Response(
        string command,
        SkyrimFollowerFinishRequest request,
        Sha256Hash requestHash,
        bool proposed,
        bool completed,
        bool verified,
        string verdict,
        WorkspacePath? proposalPath,
        Sha256Hash? proposalHash,
        WorkspacePath? manifest,
        SkyrimFollowerFinishPluginWriteResult? pluginWrite,
        PackageVerifyResult? packageVerification,
        PackageArchiveResult? packageArchive,
        ImmutableArray<Diagnostic> diagnostics) =>
        new(
            1,
            command,
            proposed,
            completed,
            verified,
            verdict,
            requestHash.Value,
            proposalPath?.Value,
            proposalHash?.Value,
            manifest?.Value,
            request.OutputRoot.Value,
            request.OutputZip.Value,
            new FollowerFinishAllocationResponse(
                request.Allocation.Package.ToString(),
                request.Allocation.Anchor.ToString(),
                request.Allocation.Actor.ToString(),
                request.Allocation.NextFormId.ToString()),
            pluginWrite?.OutputPlugin is null
                ? null
                : new FollowerFinishPluginResponse(
                    pluginWrite.OutputPlugin.Value.Value,
                    pluginWrite.OutputSha256?.Value),
            packageVerification?.Verified,
            packageArchive?.Artifact is null
                ? null
                : new FollowerFinishArchiveResponse(
                    packageArchive.Artifact.Archive.Value,
                    packageArchive.Artifact.ArchiveSha256.Value),
            false,
            diagnostics);

    private void Write<T>(
        bool json,
        T response,
        string human)
    {
        if (json)
            output.WriteLine(
                JsonSerializer.Serialize(response, JsonOptions));
        else
            output.WriteLine(human);
    }

    private CommandExitCode DiagnosticFailure(
        bool json,
        string code,
        ImmutableArray<Diagnostic> diagnostics)
    {
        string message = diagnostics.IsDefaultOrEmpty
            ? "Follower-finish input was refused without diagnostics."
            : string.Join(
                " | ",
                diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}"));
        return Failure(
            json,
            code,
            message,
            Exit(false, diagnostics));
    }

    private CommandExitCode Usage(
        bool json,
        string message) =>
        Failure(
            json,
            "usage-error",
            message,
            CommandExitCode.UsageError);

    private CommandExitCode Failure(
        bool json,
        string code,
        string message,
        CommandExitCode exit)
    {
        if (json)
        {
            error.WriteLine(
                JsonSerializer.Serialize(
                    new FollowerFinishErrorResponse(
                        code, message),
                    JsonOptions));
        }
        else
        {
            error.WriteLine(
                $"ERROR {code}: {message}");
        }
        return exit;
    }

    private static CommandExitCode Exit(
        bool success,
        ImmutableArray<Diagnostic> diagnostics)
    {
        if (success)
            return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.ClassifyFailure(diagnostics);
    }



    private sealed record FollowerFinishResponse(
        int SchemaVersion,
        string Command,
        bool Proposed,
        bool Completed,
        bool Verified,
        string Verdict,
        string RequestSha256,
        string? ProposalPath,
        string? ProposalSha256,
        string? Manifest,
        string OutputRoot,
        string OutputArchive,
        FollowerFinishAllocationResponse Allocation,
        FollowerFinishPluginResponse? Plugin,
        bool? PackageVerified,
        FollowerFinishArchiveResponse? Archive,
        bool RuntimeAuthority,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record FollowerFinishAllocationResponse(
        string Package,
        string Anchor,
        string Actor,
        string NextFormId);

    private sealed record FollowerFinishPluginResponse(
        string Output,
        string? Sha256);

    private sealed record FollowerFinishArchiveResponse(
        string Output,
        string Sha256);

    private sealed record FollowerFinishErrorResponse(
        string Code,
        string Message);
}
