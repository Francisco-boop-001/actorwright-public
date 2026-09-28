using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

public sealed class ProtocolV2Runner
{
    public const string CapabilitiesResultSchemaId =
        AgentProtocolSchemaIds.CapabilitiesResult;
    public const string VersionResultSchemaId =
        AgentProtocolSchemaIds.VersionResult;

    internal static readonly TimeSpan TerminalJournalBudget =
        TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private readonly TextWriter output;
    private readonly Func<WorkspacePath, ILocalOperationJournal> journalFactory;
    private readonly Func<WorkspacePath,
        ImmutableArray<IProtocolV2CommandAdapter>> adapterFactory;
    private readonly ImmutableArray<AgentCommandContract> contracts;
    private readonly Func<WorkspacePath, JsonElement, string, string,
        ProtocolArtifact> schemaOutputWriter;

    public ProtocolV2Runner(TextWriter output)
        : this(
            output,
            root => new LocalOperationJournal(root),
            [],
            AgentCommandRegistry.All)
    {
    }

    internal ProtocolV2Runner(
        TextWriter output,
        Func<WorkspacePath, ILocalOperationJournal> journalFactory)
        : this(output, journalFactory, [], AgentCommandRegistry.All)
    {
    }

    internal ProtocolV2Runner(
        TextWriter output,
        Func<WorkspacePath, ILocalOperationJournal> journalFactory,
        ImmutableArray<IProtocolV2CommandAdapter> adapters,
        ImmutableArray<AgentCommandContract> contracts)
        : this(
            output,
            journalFactory,
            _ => adapters,
            contracts,
            WriteSchemaOutput)
    {
        if (adapters.IsDefault)
            throw new ArgumentException(
                "The adapter collection must be initialized.",
                nameof(adapters));
    }

    internal ProtocolV2Runner(
        TextWriter output,
        Func<WorkspacePath, ILocalOperationJournal> journalFactory,
        Func<WorkspacePath, ImmutableArray<IProtocolV2CommandAdapter>>
            adapterFactory,
        ImmutableArray<AgentCommandContract> contracts)
        : this(
            output,
            journalFactory,
            adapterFactory,
            contracts,
            WriteSchemaOutput)
    {
    }

    internal ProtocolV2Runner(
        TextWriter output,
        Func<WorkspacePath, ILocalOperationJournal> journalFactory,
        ImmutableArray<IProtocolV2CommandAdapter> adapters,
        ImmutableArray<AgentCommandContract> contracts,
        Func<WorkspacePath, JsonElement, string, string, ProtocolArtifact>
            schemaOutputWriter)
        : this(
            output,
            journalFactory,
            _ => adapters,
            contracts,
            schemaOutputWriter)
    {
        if (adapters.IsDefault)
            throw new ArgumentException(
                "The adapter collection must be initialized.",
                nameof(adapters));
    }

    private ProtocolV2Runner(
        TextWriter output,
        Func<WorkspacePath, ILocalOperationJournal> journalFactory,
        Func<WorkspacePath, ImmutableArray<IProtocolV2CommandAdapter>>
            adapterFactory,
        ImmutableArray<AgentCommandContract> contracts,
        Func<WorkspacePath, JsonElement, string, string, ProtocolArtifact>
            schemaOutputWriter)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.journalFactory = journalFactory ??
            throw new ArgumentNullException(nameof(journalFactory));
        this.adapterFactory = adapterFactory ??
            throw new ArgumentNullException(nameof(adapterFactory));
        this.schemaOutputWriter = schemaOutputWriter ??
            throw new ArgumentNullException(nameof(schemaOutputWriter));
        if (contracts.IsDefault)
            throw new ArgumentException(
                "The contract collection must be initialized.",
                nameof(contracts));
        this.contracts = contracts;
    }

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var stopwatch = Stopwatch.StartNew();
        var digest = string.Empty;
        string responseCommand = command.Name;
        TerminalEnvelope? terminal = null;
        try
        {
            ImmutableArray<string> contractErrors =
                AgentCommandRegistry.Validate(contracts);
            digest = ProtocolRequestDigest.Compute(command);
            cancellationToken.ThrowIfCancellationRequested();
            if (!contractErrors.IsEmpty)
            {
                terminal = TerminalEnvelope.WithoutLease(Failure(
                    responseCommand,
                    command.Correlation,
                    digest,
                    [Diagnostic(
                        ProtocolV2DiagnosticCodes.ProtocolOperationFailed,
                        "The protocol operation failed.",
                        DiagnosticClass.Operation,
                        RecoveryAction.RepairEnvironment)]));
            }
            else
            {
                ProtocolValidationResult validation =
                    ProtocolV2CommandLine.Validate(command, contracts);
                responseCommand = validation.Contract?.Name ?? command.Name;
                if (!validation.Diagnostics.IsEmpty)
                {
                    terminal = TerminalEnvelope.WithoutLease(Failure(
                        responseCommand,
                        command.Correlation,
                        digest,
                        validation.Diagnostics));
                }
                else if (command.Help)
                {
                    terminal = TerminalEnvelope.WithoutLease(ScopedHelp(
                        command,
                        validation.Contract!,
                        digest));
                }
                else if (IsKernelCommand(validation.Contract!.Name))
                {
                    terminal = TerminalEnvelope.WithoutLease(DispatchKernel(
                        command,
                        validation.Contract,
                        digest,
                        contracts));
                }
                else
                {
                    WorkspacePath workspaceRoot =
                        ActorwrightWorkspace.ResolveRoot();
                    ImmutableArray<IProtocolV2CommandAdapter> adapters =
                        adapterFactory(workspaceRoot);
                    if (adapters.IsDefault)
                        throw new InvalidOperationException(
                            "The adapter factory returned an uninitialized collection.");
                    terminal = await DispatchAdapterAsync(
                        command,
                        validation.Contract,
                        digest,
                        contracts,
                        adapters,
                        cancellationToken);
                }
            }
        }
        catch (ProtectedRootConfigurationException exception)
        {
            terminal?.Dispose();
            digest = EnsureDigest(command, digest);
            terminal = TerminalEnvelope.WithoutLease(Failure(
                responseCommand,
                command.Correlation,
                digest,
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.UnsafePathForm,
                    exception.Message,
                    DiagnosticClass.Security,
                    RecoveryAction.RepairEnvironment)]));
        }
        catch (ProtocolV2SchemaException exception)
        {
            terminal?.Dispose();
            digest = EnsureDigest(command, digest);
            terminal = TerminalEnvelope.WithoutLease(Failure(
                responseCommand,
                command.Correlation,
                digest,
                exception.Diagnostics));
        }
        catch (OperationCanceledException)
        {
            terminal?.Dispose();
            digest = EnsureDigest(command, digest);
            terminal = TerminalEnvelope.WithoutLease(Failure(
                responseCommand,
                command.Correlation,
                digest,
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                    "The protocol operation was cancelled.",
                    DiagnosticClass.Cancellation,
                    RecoveryAction.RetryUnchanged)]));
        }
        catch (Exception)
        {
            terminal?.Dispose();
            digest = EnsureDigest(command, digest);
            terminal = TerminalEnvelope.WithoutLease(Failure(
                responseCommand,
                command.Correlation,
                digest,
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.ProtocolOperationFailed,
                    "The protocol operation failed.",
                    DiagnosticClass.Operation,
                    RecoveryAction.RepairEnvironment)]));
        }

        try
        {
            using var terminalBudget =
                new CancellationTokenSource(TerminalJournalBudget);
            ProtocolV2Envelope envelope = await AppendJournalAsync(
                    terminal!.Envelope,
                    stopwatch.ElapsedMilliseconds,
                    journalFactory,
                    terminalBudget.Token)
                .ConfigureAwait(false);
            terminal.ReplaceEnvelope(envelope);
            ProtocolV2EnvelopeWriter.Write(output, envelope);
            return (CommandExitCode)envelope.ExitCode;
        }
        finally
        {
            terminal?.Dispose();
        }
    }

    private static async ValueTask<ProtocolV2Envelope> AppendJournalAsync(
        ProtocolV2Envelope envelope,
        long durationMilliseconds,
        Func<WorkspacePath, ILocalOperationJournal> journalFactory,
        CancellationToken cancellationToken)
    {
        ProtocolEffect attempted = ProtocolEffect.Create(
            AgentEffectKind.AppendLocalOperationJournal,
            ApplicationEffectStatus.Attempted,
            ApplicationEffectScope.WorkspaceLocalJournal);
        ImmutableArray<ProtocolEffect> effects = envelope.Effects.Add(attempted);
        OperationJournalAppendResult append;
        try
        {
            WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
            ILocalOperationJournal journal = journalFactory(root) ??
                throw new InvalidOperationException(
                    "The operation journal factory returned no journal.");
            append = await journal.AppendAsync(
                new OperationJournalRecord(
                    envelope.Command,
                    envelope.RequestDigest,
                    effects,
                    envelope.Diagnostics.Select(item => item.Code)
                        .Distinct(StringComparer.Ordinal)
                        .ToImmutableArray(),
                    envelope.Diagnostics.Select(item => ClassName(item.Class))
                        .Distinct(StringComparer.Ordinal)
                        .ToImmutableArray(),
                    envelope.Artifacts
                        .Where(item => item.Sha256 is not null)
                        .Select(item => item.Sha256!)
                        .Distinct(StringComparer.Ordinal)
                        .ToImmutableArray(),
                    durationMilliseconds,
                    OutcomeName(envelope.Outcome),
                    envelope.ExitCode),
                cancellationToken);
        }
        catch (Exception)
        {
            append = JournalWriteFailure();
        }

        ApplicationEffectStatus terminalStatus = append.Appended
            ? ApplicationEffectStatus.Completed
            : ApplicationEffectStatus.Failed;
        effects = effects.Add(ProtocolEffect.Create(
            AgentEffectKind.AppendLocalOperationJournal,
            terminalStatus,
            ApplicationEffectScope.WorkspaceLocalJournal));
        ImmutableArray<ProtocolDiagnostic> diagnostics = envelope.Diagnostics;
        if (append.Warning is not null)
            diagnostics = diagnostics.Add(ProjectWarning(append.Warning));
        return envelope with
        {
            Effects = effects,
            Diagnostics = diagnostics
        };
    }

    private static OperationJournalAppendResult JournalWriteFailure() => new(
        false,
        null,
        new NpcManager.Application.Diagnostic(
            "operation-journal-write-failed",
            DiagnosticSeverity.Warning,
            "The local operation journal could not append a redacted record."));

    private static ProtocolDiagnostic ProjectWarning(
        NpcManager.Application.Diagnostic warning) =>
        new(
            warning.Code,
            warning.Severity,
            warning.Message,
            DiagnosticClass.Operation,
            new DiagnosticRecovery(
                RecoveryAction.RepairEnvironment,
                null,
                null,
                warning.Message,
                true));

    private static string ClassName(DiagnosticClass diagnosticClass) =>
        diagnosticClass switch
        {
            DiagnosticClass.Operation => "operation",
            DiagnosticClass.Usage => "usage",
            DiagnosticClass.Security => "security",
            DiagnosticClass.Validation => "validation",
            DiagnosticClass.Verification => "verification",
            DiagnosticClass.Cancellation => "cancellation",
            _ => throw new ArgumentOutOfRangeException(nameof(diagnosticClass))
        };

    private static string OutcomeName(ProtocolOutcome outcome) =>
        outcome switch
        {
            ProtocolOutcome.Succeeded => "succeeded",
            ProtocolOutcome.Refused => "refused",
            ProtocolOutcome.Failed => "failed",
            ProtocolOutcome.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    private static ProtocolV2Envelope ScopedHelp(
        ParsedCommand command,
        AgentCommandContract contract,
        string digest)
    {
        JsonElement result = JsonSerializer.SerializeToElement(
            new
            {
                SchemaId = AgentProtocolSchemaIds.ScopedHelpResult,
                Contract = contract
            },
            JsonOptions);
        return Success(command, contract, digest, [], result);
    }

    private static bool IsKernelCommand(string commandName) =>
        commandName is "capabilities" or "version" or "schema export";

    private ProtocolV2Envelope DispatchKernel(
        ParsedCommand command,
        AgentCommandContract contract,
        string digest,
        ImmutableArray<AgentCommandContract> contracts)
    {
        JsonElement result;
        var artifacts = ImmutableArray<ProtocolArtifact>.Empty;

        switch (contract.Name)
        {
            case "capabilities":
                result = JsonSerializer.SerializeToElement(
                    new
                    {
                        SchemaId = CapabilitiesResultSchemaId,
                        ProtocolVersion = BuildInfo.LatestProtocolVersion,
                        Commands = contracts
                    },
                    JsonOptions);
                break;
            case "version":
                result = JsonSerializer.SerializeToElement(
                    new
                    {
                        SchemaId = VersionResultSchemaId,
                        BuildInfo.ProductName,
                        BuildInfo.ProductVersion,
                        BuildInfo.SourceLine,
                        BuildInfo.TargetFramework,
                        ProtocolVersion = BuildInfo.LatestProtocolVersion,
                        BuildInfo.SupportedProtocolVersions
                    },
                    JsonOptions);
                break;
            case "schema export":
                var hasSchemaOutput = command.Options.TryGetValue(
                    "output",
                    out var outputPath);
                result = ProtocolV2SchemaService.RenderInline(
                    command.Options.GetValueOrDefault("command"));
                if (!hasSchemaOutput)
                    break;
                return DispatchSchemaOutput(
                    command,
                    contract,
                    digest,
                    result,
                    outputPath!);
            default:
                throw new InvalidOperationException(
                    "The selected protocol command has no runner.");
        }

        return Success(command, contract, digest, artifacts, result);
    }

    private ProtocolV2Envelope DispatchSchemaOutput(
        ParsedCommand command,
        AgentCommandContract contract,
        string digest,
        JsonElement result,
        string outputPath)
    {
        WorkspacePath output;
        try
        {
            output = new WorkspacePath(outputPath);
        }
        catch (ArgumentException)
        {
            return Failure(
                contract.Name,
                command.Correlation,
                digest,
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.UnsafePathForm,
                    "Option '--output' must be a valid absolute workspace path.",
                    DiagnosticClass.Security,
                    RecoveryAction.ChooseFreshOutput,
                    "output")]) with
            {
                Effects =
                [SchemaWriteEffect(ApplicationEffectStatus.Refused)]
            };
        }

        try
        {
            ProtocolArtifact artifact = schemaOutputWriter(
                output,
                result,
                contract.Name,
                digest);
            return Success(
                command,
                contract,
                digest,
                [artifact],
                [SchemaWriteEffect(ApplicationEffectStatus.Completed)],
                result);
        }
        catch (ProtocolV2SchemaException exception)
        {
            ApplicationEffectStatus status = exception.Diagnostics.Any(item =>
                    item.Code == ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed)
                ? ApplicationEffectStatus.Failed
                : ApplicationEffectStatus.Refused;
            return Failure(
                contract.Name,
                command.Correlation,
                digest,
                exception.Diagnostics) with
            {
                Effects = [SchemaWriteEffect(status)]
            };
        }
        catch (ProtectedRootConfigurationException exception)
        {
            return Failure(
                contract.Name,
                command.Correlation,
                digest,
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.UnsafePathForm,
                    exception.Message,
                    DiagnosticClass.Security,
                    RecoveryAction.RepairEnvironment)]) with
            {
                Effects = [SchemaWriteEffect(ApplicationEffectStatus.Refused)]
            };
        }
        catch (Exception)
        {
            return Failure(
                contract.Name,
                command.Correlation,
                digest,
                [Diagnostic(
                    ProtocolV2DiagnosticCodes.ProtocolOperationFailed,
                    "The protocol operation failed.",
                    DiagnosticClass.Operation,
                    RecoveryAction.RepairEnvironment)]) with
            {
                Effects = [SchemaWriteEffect(ApplicationEffectStatus.Failed)]
            };
        }
    }

    private static ProtocolEffect SchemaWriteEffect(
        ApplicationEffectStatus status) => ProtocolEffect.Create(
            AgentEffectKind.WriteNewArtifact,
            status,
            ApplicationEffectScope.KLocalOutput);

    private static ProtocolArtifact WriteSchemaOutput(
        WorkspacePath output,
        JsonElement result,
        string producerCommand,
        string requestDigest)
    {
        ProtocolV2SchemaService.ValidateOutputDrive(output);
        WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
        var policy = new KOnlyWorkspacePolicy(
            root,
            ActorwrightWorkspace.ResolveProtectedRoot(root));
        var schemaService = new ProtocolV2SchemaService(policy, root);
        return schemaService.WriteNew(
            output,
            result,
            producerCommand,
            requestDigest);
    }

    private static async ValueTask<TerminalEnvelope> DispatchAdapterAsync(
        ParsedCommand command,
        AgentCommandContract contract,
        string digest,
        ImmutableArray<AgentCommandContract> contracts,
        ImmutableArray<IProtocolV2CommandAdapter> adapters,
        CancellationToken cancellationToken)
    {
        IProtocolV2CommandAdapter[] matches = adapters
            .Where(adapter => adapter is not null &&
                !adapter.Commands.IsDefault &&
                adapter.Commands.Contains(contract.Name, StringComparer.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length == 0)
        {
            return TerminalEnvelope.WithoutLease(AdapterFailure(
                command,
                contract,
                digest,
                ProtocolV2DiagnosticCodes.ProtocolAdapterMissing,
                "The protocol command adapter is unavailable."));
        }

        if (matches.Length > 1)
        {
            return TerminalEnvelope.WithoutLease(AdapterFailure(
                command,
                contract,
                digest,
                ProtocolV2DiagnosticCodes.ProtocolAdapterDuplicate,
                "The protocol command adapter selection is ambiguous."));
        }

        using var terminalLeaseScope =
            new ProtocolV2TerminalArtifactLeaseScope();
        ProtocolCommandResult? result = await matches[0].RunAsync(
            command,
            digest,
            cancellationToken);
        if (result?.TerminalArtifactLease is { } terminalArtifactLease)
            terminalLeaseScope.Own(terminalArtifactLease);
        bool valid;
        try
        {
            valid = result is not null &&
                IsValidResult(result, contract, contracts, digest);
        }
        catch (Exception)
        {
            valid = false;
        }

        if (!valid)
        {
            return TerminalEnvelope.WithoutLease(AdapterFailure(
                command,
                contract,
                digest,
                ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid,
                "The protocol command adapter returned an invalid result."));
        }

        CommandExitCode exitCode = ProtocolExitCodeMapper.Map(
            result!.Diagnostics);
        return new TerminalEnvelope(
            new ProtocolV2Envelope(
                BuildInfo.LatestProtocolVersion,
                "1",
                contract.Name,
                ProtocolExitCodeMapper.MapOutcome(exitCode),
                (int)exitCode,
                digest,
                command.Correlation,
                result.Effects,
                result.Diagnostics,
                result.Artifacts,
                result.Authority,
                result.NextActions,
                result.Result),
            terminalLeaseScope.Transfer());
    }

    private static ProtocolV2Envelope AdapterFailure(
        ParsedCommand command,
        AgentCommandContract contract,
        string digest,
        string code,
        string message) =>
        Failure(
            contract.Name,
            command.Correlation,
            digest,
            [Diagnostic(
                code,
                message,
                DiagnosticClass.Operation,
                RecoveryAction.RepairEnvironment)]);

    private static bool IsValidResult(
        ProtocolCommandResult result,
        AgentCommandContract contract,
        ImmutableArray<AgentCommandContract> contracts,
        string digest)
    {
        if (result.Effects.IsDefault ||
            result.Effects.Length >
                ApplicationEffectVocabulary.MaximumCommandEffects ||
            result.Diagnostics.IsDefault ||
            result.Artifacts.IsDefault ||
            result.Authority.IsDefault ||
            result.NextActions.IsDefault)
            return false;

        foreach (ProtocolEffect? effect in result.Effects)
        {
            if (effect is null ||
                !Enum.IsDefined(effect.Kind) ||
                effect.Kind == AgentEffectKind.AppendLocalOperationJournal ||
                !ApplicationEffectVocabulary.TryParseStatus(
                    effect.Status, out _) ||
                !ApplicationEffectVocabulary.TryParseScope(
                    effect.Scope, out ApplicationEffectScope scope) ||
                !ApplicationEffectVocabulary.IsAdmittedPair(
                    effect.Kind, scope))
                return false;
            AgentEffectContract? declared = contract.Effects.FirstOrDefault(
                item => item.Kind == effect.Kind);
            if (declared is null ||
                declared.AllowedResultScopes.IsDefaultOrEmpty ||
                !declared.AllowedResultScopes.Contains(scope))
                return false;
        }

        if (result.Diagnostics.Any(diagnostic =>
                diagnostic is null ||
                string.IsNullOrWhiteSpace(diagnostic.Code) ||
                !ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                    diagnostic.Code,
                    out ProtocolDiagnosticSemantics semantics) ||
                diagnostic.Severity != semantics.Severity ||
                string.IsNullOrWhiteSpace(diagnostic.Message) ||
                diagnostic.Class != semantics.Class ||
                diagnostic.Recovery is null ||
                !Enum.IsDefined(diagnostic.Recovery.Action) ||
                string.IsNullOrWhiteSpace(diagnostic.Recovery.Constraint) ||
                IsInvalidOptionalText(diagnostic.Recovery.Option) ||
                IsInvalidOptionalText(diagnostic.Recovery.ArtifactKind)))
            return false;

        if (result.Artifacts.Any(artifact =>
                artifact is null ||
                string.IsNullOrWhiteSpace(artifact.Kind) ||
                string.IsNullOrWhiteSpace(artifact.SchemaOrMediaType) ||
                string.IsNullOrWhiteSpace(artifact.Path) ||
                artifact.Size is < 0 ||
                (artifact.Sha256 is not null &&
                 !IsSha256(artifact.Sha256)) ||
                !string.Equals(
                    artifact.ProducerCommand,
                    contract.Name,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    artifact.RequestDigest,
                    digest,
                    StringComparison.Ordinal) ||
                artifact.InputBindings.IsDefault ||
                artifact.InputBindings.Any(string.IsNullOrWhiteSpace) ||
                string.IsNullOrWhiteSpace(artifact.State)))
            return false;

        if (result.Authority.Length != contract.Authority.Length)
            return false;
        bool hasError = result.Diagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        var authorityKinds = new HashSet<AgentAuthorityKind>();
        foreach (ProtocolAuthority authority in result.Authority)
        {
            if (authority is null ||
                !Enum.IsDefined(authority.Kind) ||
                !Enum.IsDefined(authority.State) ||
                string.IsNullOrWhiteSpace(authority.Reason) ||
                !authorityKinds.Add(authority.Kind))
                return false;
            AgentAuthorityContract? declared = contract.Authority
                .FirstOrDefault(item => item.Kind == authority.Kind);
            if (declared is null || !IsAdmittedAuthorityState(
                    declared.State,
                    authority.State,
                    hasError))
                return false;
        }

        foreach (ProtocolNextAction action in result.NextActions)
        {
            if (action is null ||
                string.IsNullOrWhiteSpace(action.Command) ||
                string.IsNullOrWhiteSpace(action.Reason) ||
                action.RequiredBindings.IsDefault ||
                action.MissingPrerequisites.IsDefault ||
                action.MissingPrerequisites.Any(string.IsNullOrWhiteSpace))
                return false;

            AgentCommandContract? target = contracts.FirstOrDefault(item =>
                string.Equals(
                    item.Name,
                    action.Command,
                    StringComparison.Ordinal));
            if (target is null)
                return false;

            ImmutableHashSet<string> targetOptions =
                ProtocolV2NextActionTargetCatalog.OptionsFor(target);
            var boundOptions = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProtocolNextActionBinding binding in
                     action.RequiredBindings)
            {
                if (binding is null ||
                    string.IsNullOrWhiteSpace(binding.Option) ||
                    string.IsNullOrWhiteSpace(binding.Value) ||
                    !boundOptions.Add(binding.Option) ||
                    !targetOptions.Contains(binding.Option) ||
                    (binding.ArtifactSha256 is not null &&
                     !IsUpperSha256(binding.ArtifactSha256)))
                    return false;
            }

            var missingOptions = new HashSet<string>(StringComparer.Ordinal);
            foreach (string missing in action.MissingPrerequisites)
            {
                if (!targetOptions.Contains(missing) ||
                    !missingOptions.Add(missing) ||
                    boundOptions.Contains(missing))
                    return false;
            }
        }

        JsonElement? resultElement = result.Result;
        if (contract.ResultSchemaIds.IsEmpty)
        {
            return result.ResultSchemaId is null && resultElement is null;
        }

        return !string.IsNullOrWhiteSpace(result.ResultSchemaId) &&
            contract.ResultSchemaIds.Contains(
                result.ResultSchemaId,
                StringComparer.Ordinal) &&
            resultElement is not null &&
            resultElement.Value.ValueKind == JsonValueKind.Object;
    }

    private static bool IsAdmittedAuthorityState(
        AgentAuthorityState declared,
        AgentAuthorityState returned,
        bool hasError) =>
        !hasError
            ? returned == declared
            : declared switch
            {
                AgentAuthorityState.Established => returned is
                    AgentAuthorityState.Established or
                    AgentAuthorityState.Required or
                    AgentAuthorityState.Blocked,
                AgentAuthorityState.Required => returned is
                    AgentAuthorityState.Required or
                    AgentAuthorityState.Blocked,
                AgentAuthorityState.Blocked =>
                    returned == AgentAuthorityState.Blocked,
                AgentAuthorityState.NotApplicable =>
                    returned == AgentAuthorityState.NotApplicable,
                _ => false
            };

    private static bool IsInvalidOptionalText(string? value) =>
        value is not null && string.IsNullOrWhiteSpace(value);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');

    private static bool IsUpperSha256(string value) =>
        IsSha256(value) && !value.Any(character =>
            character is >= 'a' and <= 'f');

    private sealed class TerminalEnvelope(
        ProtocolV2Envelope envelope,
        IDisposable? terminalArtifactLease) : IDisposable
    {
        private IDisposable? terminalArtifactLease = terminalArtifactLease;

        internal ProtocolV2Envelope Envelope { get; private set; } = envelope;

        internal static TerminalEnvelope WithoutLease(
            ProtocolV2Envelope envelope) => new(envelope, null);

        internal void ReplaceEnvelope(ProtocolV2Envelope envelope) =>
            Envelope = envelope;

        public void Dispose()
        {
            IDisposable? retained = Interlocked.Exchange(
                ref terminalArtifactLease,
                null);
            if (retained is null)
                return;

            try
            {
                retained.Dispose();
            }
            catch
            {
                // Terminal release must not mask journal or writer failures.
            }
        }
    }

    private static ProtocolV2Envelope Success(
        ParsedCommand command,
        AgentCommandContract contract,
        string digest,
        ImmutableArray<ProtocolArtifact> artifacts,
        JsonElement result) =>
        Success(command, contract, digest, artifacts, [], result);

    private static ProtocolV2Envelope Success(
        ParsedCommand command,
        AgentCommandContract contract,
        string digest,
        ImmutableArray<ProtocolArtifact> artifacts,
        ImmutableArray<ProtocolEffect> effects,
        JsonElement result) =>
        new(
            BuildInfo.LatestProtocolVersion,
            "1",
            contract.Name,
            ProtocolOutcome.Succeeded,
            (int)CommandExitCode.Success,
            digest,
            command.Correlation,
            effects,
            [],
            artifacts,
            contract.Authority.Select(item => new ProtocolAuthority(
                    item.Kind,
                    item.State,
                    item.Reason))
                .ToImmutableArray(),
            [],
            result);

    private static ProtocolV2Envelope Failure(
        string commandName,
        string? correlation,
        string digest,
        ImmutableArray<ProtocolDiagnostic> diagnostics)
    {
        var exitCode = ProtocolExitCodeMapper.Map(diagnostics);
        return ProtocolV2Envelope.Failure(
            commandName,
            digest,
            exitCode,
            diagnostics,
            correlation);
    }

    private static ProtocolDiagnostic Diagnostic(
        string code,
        string message,
        DiagnosticClass diagnosticClass,
        RecoveryAction recovery,
        string? option = null) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            diagnosticClass,
            new DiagnosticRecovery(
                recovery,
                option,
                null,
                message,
                recovery == RecoveryAction.RetryUnchanged));

    private static string EnsureDigest(
        ParsedCommand command,
        string digest) =>
        string.IsNullOrEmpty(digest)
            ? ProtocolRequestDigest.Compute(command)
            : digest;
}

internal static class ProtocolV2NextActionTargetCatalog
{
    private static readonly ImmutableDictionary<string,
        ImmutableHashSet<string>> StagedLegacyTargets =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["gui"] =
            [
                "workflow-bundle", "workflow-bundle-sha256", "proposal",
                "proposal-sha256", "preview-manifest",
                "preview-manifest-sha256", "outcome",
                "operator-attestation", "reviewer-note", "receipt-output",
                "workflow-output"
            ],
            ["npc finish analyze"] =
            [
                "request", "request-sha256", "proposal", "review-receipt",
                "review-receipt-sha256", "workflow-bundle",
                "workflow-bundle-sha256", "workflow-output"
            ],
            ["npc finish apply"] =
            [
                "request", "request-sha256", "proposal", "proposal-sha256",
                "review-receipt", "review-receipt-sha256",
                "workflow-bundle", "workflow-bundle-sha256",
                "workflow-output"
            ]
        }.ToImmutableDictionary(
            item => item.Key,
            item => item.Value.ToImmutableHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

    internal static ImmutableHashSet<string> OptionsFor(
        AgentCommandContract target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ImmutableHashSet<string> advertised = target.Options
            .Select(option => $"--{option.CliName}")
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (target.Readiness == ProtocolReadiness.V2 ||
            !StagedLegacyTargets.TryGetValue(
                target.Name,
                out ImmutableHashSet<string>? staged))
            return advertised;
        if (!AgentCommandRegistry.All.Any(item => string.Equals(
                item.Name,
                target.Name,
                StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"The staged next-action target '{target.Name}' is not one canonical command.");
        return staged.Select(option => "--" + option)
            .ToImmutableHashSet(StringComparer.Ordinal);
    }
}
