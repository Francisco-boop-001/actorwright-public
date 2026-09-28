using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ReferencePresetCommandHandler(
    IReferencePresetAuthoringTransaction transaction,
    IReferencePresetSessionService sessions,
    IRaceMenuNpcExecutionRequestFileLoader requestLoader,
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
                "preset design-propose" =>
                    await RunDesignAsync(
                        command, cancellationToken)
                        .ConfigureAwait(false),
                "preset create-from-reference" =>
                    await RunPresetAsync(
                        command, cancellationToken)
                        .ConfigureAwait(false),
                "npc create-from-reference" =>
                    await RunNpcAsync(
                        command, cancellationToken)
                        .ConfigureAwait(false),
                _ => Failure(
                    command.Json,
                    "usage-error",
                    "Unsupported reference preset command.",
                    CommandExitCode.UsageError)
            };
        }
        catch (OperationCanceledException)
        {
            return Failure(
                command.Json,
                "cancelled",
                "Reference preset authoring was cancelled.",
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
                "reference-preset-request-invalid",
                exception.Message,
                CommandExitCode.ValidationFailure);
        }
    }

    private async ValueTask<CommandExitCode> RunDesignAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Options.ContainsKey("template-output"))
        {
            string[] templateOptions = ["template-output"];
            CommandExitCode? templateUsage = ValidateOptions(command, templateOptions, templateOptions);
            if (templateUsage is not null) return templateUsage.Value;
            WorkspacePath path = PathOption(command, "template-output");
            ReferencePresetSessionWriteResult template = await sessions.WriteIntakeTemplateAsync(path, cancellationToken).ConfigureAwait(false);
            if (!template.Written) return DiagnosticFailure(command.Json, "reference-template-refused", template.Diagnostics);
            if (command.Json)
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, command = command.Name, completed = true,
                    status = "INTAKE_TEMPLATE_WRITTEN", templatePath = path.Value,
                    templateSha256 = template.ContentSha256!.Value.Value, runtimeAuthority = false,
                    diagnostics = template.Diagnostics
                }, JsonOptions));
            else
                output.WriteLine($"Intake template -> {path.Value}\nSHA256 -> {template.ContentSha256}\nFill the placeholders before normal intake validation.");
            return CommandExitCode.Success;
        }

        string[] allowed =
        [
            "intake",
            "intake-sha256",
            "output"
        ];
        CommandExitCode? usage =
            ValidateOptions(
                command,
                allowed,
                allowed);
        if (usage is not null)
            return usage.Value;

        WorkspacePath intakePath =
            PathOption(command, "intake");
        Sha256Hash intakeHash =
            HashOption(command, "intake-sha256");
        ReferencePresetSessionReadResult intakeRead =
            await sessions.ReadAsync(
                new ReferencePresetSessionReadRequest(
                    intakePath,
                    intakeHash,
                    ReferencePresetSessionDocumentKind.Intake),
                cancellationToken).ConfigureAwait(false);
        ReferencePresetIntake? intake =
            intakeRead.Document?.Intake;
        if (intake is null)
        {
            return DiagnosticFailure(
                command.Json,
                "reference-intake-invalid",
                intakeRead.Diagnostics);
        }

        ReferencePresetDesignProposalResult result =
            await transaction.ProposeDesignAsync(
                new ReferencePresetDesignProposalRequest(
                    intake,
                    intakeRead.ContentSha256!.Value,
                    PathOption(command, "output")),
                null,
                cancellationToken).ConfigureAwait(false);
        return WriteResult(
            command,
            result.Completed,
            result.ProposalSha256,
            null,
            null,
            null,
            result.Diagnostics);
    }

    private async ValueTask<CommandExitCode> RunPresetAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        string[] required =
        [
            "proposal",
            "proposal-sha256",
            "review",
            "review-sha256",
            "resource",
            "resource-sha256",
            "jslot-output",
            "evidence-root"
        ];
        string[] allowed =
            required.Concat(
                    ["apply",
                        "accepted-proposal-sha256"])
                .ToArray();
        CommandExitCode? usage =
            ValidateOptions(
                command, allowed, required);
        if (usage is not null)
            return usage.Value;
        if (!TryApply(command, out bool apply))
            return Usage(
                command.Json,
                "--apply must be true or false.");
        bool hasAccepted = command.Options.TryGetValue(
            "accepted-proposal-sha256",
            out string? acceptedText) &&
            !string.IsNullOrWhiteSpace(acceptedText);
        if (apply != hasAccepted)
        {
            return Usage(
                command.Json,
                apply
                    ? "--accepted-proposal-sha256 is required with --apply."
                    : "--accepted-proposal-sha256 is valid only with --apply.");
        }

        WorkspacePath proposalPath =
            PathOption(command, "proposal");
        Sha256Hash proposalHash =
            HashOption(command, "proposal-sha256");
        (WorkspacePath intakePath, Sha256Hash intakeHash,
            ImmutableArray<Diagnostic> readDiagnostics) =
            await ResolveIntakeAsync(
                proposalPath,
                proposalHash,
                cancellationToken).ConfigureAwait(false);
        if (readDiagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            return DiagnosticFailure(
                command.Json,
                "reference-proposal-invalid",
                readDiagnostics);
        }

        var request = new ReferencePresetWriteRequest(
            intakePath,
            intakeHash,
            proposalPath,
            proposalHash,
            PathOption(command, "review"),
            HashOption(command, "review-sha256"),
            PathOption(command, "resource"),
            HashOption(command, "resource-sha256"),
            PathOption(command, "evidence-root"),
            apply,
            hasAccepted
                ? new Sha256Hash(acceptedText!)
                : null);
        ReferencePresetWriteResult result =
            await transaction.WritePresetAsync(
                request,
                null,
                cancellationToken).ConfigureAwait(false);
        WorkspacePath requestedPreset =
            PathOption(command, "jslot-output");
        if (result.Completed &&
            result.VerifiedPreset is not null &&
            !PathEquals(
                requestedPreset,
                result.VerifiedPreset.PresetPath))
        {
            return Failure(
                command.Json,
                "reference-preset-output-mismatch",
                "The verified preset path does not match --jslot-output.",
                CommandExitCode.ValidationFailure);
        }
        return WriteResult(
            command,
            result.Completed,
            result.ProposalSha256,
            result.VerifiedPreset?.PresetPath,
            result.VerifiedPreset?.PresetSha256,
            null,
            result.Diagnostics);
    }

    private async ValueTask<CommandExitCode> RunNpcAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        string[] required =
        [
            "proposal",
            "proposal-sha256",
            "review",
            "review-sha256",
            "resource",
            "resource-sha256",
            "accepted-proposal-sha256",
            "request",
            "request-sha256",
            "data-root",
            "plugins",
            "transaction-root",
            "apply"
        ];
        CommandExitCode? usage =
            ValidateOptions(
                command, required, required);
        if (usage is not null)
            return usage.Value;
        if (!TryApply(command, out bool apply) ||
            !apply)
        {
            return Usage(
                command.Json,
                "npc create-from-reference requires --apply.");
        }

        WorkspacePath proposalPath =
            PathOption(command, "proposal");
        Sha256Hash proposalHash =
            HashOption(command, "proposal-sha256");
        (WorkspacePath intakePath, Sha256Hash intakeHash,
            ImmutableArray<Diagnostic> readDiagnostics) =
            await ResolveIntakeAsync(
                proposalPath,
                proposalHash,
                cancellationToken).ConfigureAwait(false);
        if (readDiagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
        {
            return DiagnosticFailure(
                command.Json,
                "reference-proposal-invalid",
                readDiagnostics);
        }

        WorkspacePath requestPath =
            PathOption(command, "request");
        Sha256Hash requestHash =
            HashOption(command, "request-sha256");
        RaceMenuNpcExecutionRequestFileLoadResult loaded =
            await requestLoader.LoadAsync(
                new RaceMenuNpcExecutionRequestFileLoadRequest(
                    requestPath,
                    requestHash),
                cancellationToken).ConfigureAwait(false);
        if (!loaded.Loaded ||
            loaded.Request is null)
        {
            return DiagnosticFailure(
                command.Json,
                "reference-npc-request-invalid",
                loaded.Diagnostics);
        }

        ImmutableArray<PluginName> plugins =
            ParsePlugins(command.Options["plugins"]);
        WorkspacePath root =
            PathOption(command, "transaction-root");
        var authoring = new ReferencePresetWriteRequest(
            intakePath,
            intakeHash,
            proposalPath,
            proposalHash,
            PathOption(command, "review"),
            HashOption(command, "review-sha256"),
            PathOption(command, "resource"),
            HashOption(command, "resource-sha256"),
            root,
            true,
            HashOption(
                command,
                "accepted-proposal-sha256"));
        var downstream = new RaceMenuJslotNpcBuildRequest(
            loaded.Request,
            new WorkspacePath(Path.Combine(
                root.Value,
                "preset",
                ".pending.jslot")),
            proposalHash,
            PathOption(command, "data-root"),
            plugins,
            new WorkspacePath(Path.Combine(
                root.Value, "npc")))
        {
            SourceRequest = requestPath,
            SourceRequestSha256 = requestHash
        };
        ReferencePresetNpcBuildResult result =
            await transaction.WritePresetAndBuildNpcAsync(
                new ReferencePresetNpcBuildRequest(
                    authoring, downstream),
                null,
                cancellationToken).ConfigureAwait(false);
        return WriteResult(
            command,
            result.Completed,
            result.Handoff is null
                ? null
                : authoring
                    .AcceptedAuthoringProposalSha256,
            result.Handoff?.PresetPath,
            result.Handoff?.PresetSha256,
            result.Handoff,
            result.Diagnostics);
    }

    private async ValueTask<(WorkspacePath IntakePath,
        Sha256Hash IntakeSha256,
        ImmutableArray<Diagnostic> Diagnostics)>
        ResolveIntakeAsync(
            WorkspacePath proposalPath,
            Sha256Hash proposalHash,
            CancellationToken cancellationToken)
    {
        ReferencePresetSessionReadResult proposalRead =
            await sessions.ReadAsync(
                new ReferencePresetSessionReadRequest(
                    proposalPath,
                    proposalHash,
                    ReferencePresetSessionDocumentKind
                        .InferenceProposal),
                cancellationToken).ConfigureAwait(false);
        LandmarkInferenceProposal? proposal =
            proposalRead.Document?.InferenceProposal;
        if (proposal is null)
        {
            return (
                default,
                default,
                proposalRead.Diagnostics);
        }
        string? parent =
            Path.GetDirectoryName(proposalPath.Value);
        if (parent is null)
        {
            return (
                default,
                default,
                proposalRead.Diagnostics.Add(
                    new Diagnostic(
                        "reference-proposal-parent",
                        DiagnosticSeverity.Error,
                        "The inference proposal has no parent directory.")));
        }
        return (
            new WorkspacePath(Path.Combine(
                parent,
                "authoring-intake.json")),
            proposal.IntakeSha256,
            proposalRead.Diagnostics);
    }

    private CommandExitCode WriteResult(
        ParsedCommand command,
        bool completed,
        Sha256Hash? proposalSha256,
        WorkspacePath? presetPath,
        Sha256Hash? presetSha256,
        VerifiedReferenceNpcHandoff? handoff,
        ImmutableArray<Diagnostic> diagnostics)
    {
        var response = new ReferenceCommandResponse(
            1,
            command.Name,
            completed,
            completed
                ? handoff is not null
                    ? "VERIFIED_NPC_HANDOFF"
                    : presetPath is not null
                        ? "VERIFIED_PRESET"
                        : "PROPOSAL_CREATED"
                : "REFUSED",
            proposalSha256?.Value,
            presetPath?.Value,
            presetSha256?.Value,
            handoff?.Race.ToString(),
            handoff?.Sex.ToString(),
            handoff?.Weight,
            false,
            diagnostics);
        if (command.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(
                response, JsonOptions));
        }
        else if (completed)
        {
            output.WriteLine(
                $"{command.Name}: {response.Status}");
            if (response.ProposalSha256 is not null)
                output.WriteLine(
                    $"Proposal -> {response.ProposalSha256}");
            if (response.PresetPath is not null)
                output.WriteLine(
                    $"Preset -> {response.PresetPath}");
            output.WriteLine(
                "Runtime authority -> false (requires in-game proof)");
        }
        else
        {
            output.WriteLine(
                $"{command.Name}: REFUSED");
            foreach (Diagnostic diagnostic in diagnostics
                         .Where(item =>
                             item.Severity !=
                             DiagnosticSeverity.Info))
            {
                error.WriteLine(
                    $"{diagnostic.Code}: {diagnostic.Message}");
            }
        }
        return completed
            ? CommandExitCode.Success
            : ExitCode(diagnostics);
    }

    private CommandExitCode? ValidateOptions(
        ParsedCommand command,
        IEnumerable<string> allowed,
        IEnumerable<string> required)
    {
        HashSet<string> allowedSet =
            allowed.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
        string? unknown = command.Options.Keys
            .FirstOrDefault(key =>
                !allowedSet.Contains(key));
        if (unknown is not null)
        {
            return Failure(
                command.Json,
                "unknown-option",
                $"Unknown option --{unknown}.",
                CommandExitCode.UsageError);
        }
        string? missing = required.FirstOrDefault(
            key =>
                !command.Options.TryGetValue(
                    key, out string? value) ||
                string.IsNullOrWhiteSpace(value));
        return missing is null
            ? null
            : Usage(
                command.Json,
                $"{command.Name} requires --{missing}.");
    }

    private static bool TryApply(
        ParsedCommand command,
        out bool apply)
    {
        if (!command.Options.TryGetValue(
                "apply", out string? text))
        {
            apply = false;
            return true;
        }
        return bool.TryParse(text, out apply);
    }

    private static ImmutableArray<PluginName>
        ParsePlugins(string text)
    {
        ImmutableArray<PluginName> plugins =
            text.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Select(item => new PluginName(item))
                .ToImmutableArray();
        if (plugins.IsDefaultOrEmpty ||
            plugins.Select(item => item.Value)
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Count() != plugins.Length)
        {
            throw new ArgumentException(
                "--plugins requires a nonempty, duplicate-free ordered list.",
                nameof(text));
        }
        return plugins;
    }

    private static WorkspacePath PathOption(
        ParsedCommand command,
        string key)
    {
        string value = command.Options[key];
        return new WorkspacePath(
            value.StartsWith('@')
                ? value[1..]
                : value);
    }

    private static Sha256Hash HashOption(
        ParsedCommand command,
        string key) =>
        new(command.Options[key]);

    private CommandExitCode DiagnosticFailure(
        bool json,
        string fallbackCode,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Diagnostic? first = diagnostics.FirstOrDefault(
            item =>
                item.Severity ==
                DiagnosticSeverity.Error);
        return Failure(
            json,
            first?.Code ?? fallbackCode,
            first?.Message ??
            "The requested authority was refused.",
            ExitCode(diagnostics));
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
        CommandExitCode exitCode)
    {
        if (json)
        {
            error.WriteLine(JsonSerializer.Serialize(
                new ErrorResponse(code, message),
                JsonOptions));
        }
        else
        {
            error.WriteLine(
                $"ERROR {code}: {message}");
        }
        return exitCode;
    }

    private static CommandExitCode ExitCode(
        IEnumerable<Diagnostic> diagnostics) =>
        DiagnosticExitCodeClassifier.ClassifyFailure(diagnostics);

    private static bool PathEquals(
        WorkspacePath left,
        WorkspacePath right) =>
        string.Equals(
            Path.GetFullPath(left.Value)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right.Value)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private sealed record ReferenceCommandResponse(
        int SchemaVersion,
        string Command,
        bool Completed,
        string Status,
        string? ProposalSha256,
        string? PresetPath,
        string? PresetSha256,
        string? Race,
        string? Sex,
        float? Weight,
        bool RuntimeAuthority,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(
        string Code,
        string Message);
}
