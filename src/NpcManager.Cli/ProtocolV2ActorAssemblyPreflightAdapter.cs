using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

/// <summary>
/// Protocol-v2 boundary for the existing read-only Actor Assembly preflight.
/// Contract admission and fresh-output admission happen before the service is
/// called; only a successfully persisted and reopened result is published.
/// </summary>
public sealed class ProtocolV2ActorAssemblyPreflightAdapter(
    WorkspacePath workspaceRoot,
    IActorAssemblyPreflightService service,
    ActorAssemblyPreflightResultStore resultStore,
    IActorAssemblyContractLoader? admissionLoader = null,
    FaceGeomHairRegionsWorkspaceBoundary? workflowPathBoundary = null) :
    IProtocolV2CommandAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private readonly IActorAssemblyPreflightService preflightService =
        service ?? throw new ArgumentNullException(nameof(service));
    private readonly ActorAssemblyPreflightResultStore artifactStore =
        resultStore ?? throw new ArgumentNullException(nameof(resultStore));
    private readonly FaceGeomHairRegionsWorkspaceBoundary pathBoundary =
        workflowPathBoundary ??
        new FaceGeomHairRegionsWorkspaceBoundary(workspaceRoot);
    private readonly IActorAssemblyContractLoader contractLoader =
        admissionLoader ?? new ActorAssemblyPreflightDocumentLoader(workspaceRoot);

    public ImmutableArray<string> Commands { get; } =
        ["npc assembly preflight"];

    public async ValueTask<ProtocolCommandResult> RunAsync(
        ParsedCommand command,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        if (!TryBind(
                command,
                out ProtocolV2PhysicalFileBinding? contractBinding,
                out WorkspacePath output,
                out ImmutableArray<ProtocolDiagnostic> bindingDiagnostics))
        {
            return Refused(
                Effects(ApplicationEffectStatus.Refused),
                bindingDiagnostics,
                BindingFailureAuthority(),
                ErrorResult(false, bindingDiagnostics));
        }

        var request = new ActorAssemblyPreflightRequest(
            contractBinding!.Path,
            new Sha256Hash(contractBinding.Sha256));
        ActorAssemblyDocumentLoadResult<ActorAssemblyContract> admitted;
        try
        {
            // The strict loader is deliberately before the existing service.
            // This keeps schema/hash/security admission fail-closed and gives
            // a drifted contract zero service calls.
            admitted = await contractLoader.LoadAsync(
                request,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                IOException or UnauthorizedAccessException)
        {
            ImmutableArray<ProtocolDiagnostic> diagnostics =
                [ToProtocolDiagnostic(
                    new Diagnostic(
                        "actor-assembly-contract-admission-failed",
                        DiagnosticSeverity.Error,
                        exception.Message))];
            return Refused(
                Effects(ApplicationEffectStatus.Refused),
                diagnostics,
                BindingFailureAuthority(),
                ErrorResult(false, diagnostics));
        }

        if (admitted.Disposition != ActorAssemblyDocumentDisposition.Loaded ||
            admitted.Document is null ||
            admitted.ActualSha256 is null ||
            !string.Equals(
                admitted.ActualSha256.Value.Value,
                contractBinding.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            ImmutableArray<ProtocolDiagnostic> diagnostics =
                ToProtocolDiagnostics(admitted.Diagnostics);
            if (diagnostics.IsEmpty)
            {
                diagnostics =
                [ToProtocolDiagnostic(
                    new Diagnostic(
                        "actor-assembly-contract-admission-failed",
                        DiagnosticSeverity.Error,
                        "The Actor Assembly contract was not admitted."))];
            }

            return Refused(
                Effects(ApplicationEffectStatus.Completed),
                diagnostics,
                BindingFailureAuthority(),
                ErrorResult(false, diagnostics));
        }

        ActorAssemblyPreflightExecutionResult execution;
        try
        {
            execution = await preflightService.PreflightAsync(
                request,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                IOException or UnauthorizedAccessException)
        {
            ImmutableArray<ProtocolDiagnostic> diagnostics =
                [ToProtocolDiagnostic(
                    new Diagnostic(
                        "actor-assembly-preflight-failed",
                        DiagnosticSeverity.Error,
                        exception.Message))];
            return Refused(
                Effects(ApplicationEffectStatus.Completed),
                diagnostics,
                ServiceFailureAuthority(),
                ErrorResult(true, diagnostics));
        }

        if (execution.Artifact is null)
        {
            ImmutableArray<ProtocolDiagnostic> diagnostics =
                ToProtocolDiagnostics(execution.Error?.Diagnostics ?? []);
            if (diagnostics.IsEmpty)
            {
                diagnostics =
                [ToProtocolDiagnostic(
                    new Diagnostic(
                        "actor-assembly-preflight-failed",
                        DiagnosticSeverity.Error,
                        "The Actor Assembly preflight did not produce a typed result."))];
            }

            return Refused(
                Effects(ApplicationEffectStatus.Completed),
                diagnostics,
                execution.SecurityRefusal
                    ? BindingFailureAuthority()
                    : ServiceFailureAuthority(),
                ErrorResult(
                    execution.ContractAdmitted,
                    diagnostics));
        }

        if (!execution.ContractAdmitted || execution.Error is not null ||
            !string.Equals(
                execution.Artifact.ContractSha256.Value,
                contractBinding.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            ImmutableArray<ProtocolDiagnostic> diagnostics =
            [ToProtocolDiagnostic(
                new Diagnostic(
                    "actor-assembly-preflight-result-invalid",
                    DiagnosticSeverity.Error,
                    "The preflight service returned a result that is not bound to the admitted contract."))];
            return Refused(
                Effects(ApplicationEffectStatus.Completed),
                diagnostics,
                ServiceFailureAuthority(),
                ErrorResult(true, diagnostics));
        }

        if (execution.Artifact.Outcome is
                (ActorAssemblyOutcome.Pass or ActorAssemblyOutcome.NotApplicable) &&
            !string.Equals(
                execution.Artifact.PackageManifestSha256.Value,
                admitted.Document!.PackageManifest.Sha256.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            ImmutableArray<ProtocolDiagnostic> diagnostics =
            [new ProtocolDiagnostic(
                ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed,
                DiagnosticSeverity.Error,
                "The pass-like preflight result is not bound to the admitted package manifest.",
                DiagnosticClass.Validation,
                RecoveryFor(
                    ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed,
                    DiagnosticClass.Validation))];
            return Refused(
                Effects(ApplicationEffectStatus.Completed),
                diagnostics,
                ServiceFailureAuthority(),
                ErrorResult(true, diagnostics));
        }

        ActorAssemblyPreflightResultLease? lease = null;
        try
        {
            lease = await artifactStore.WriteNewAsync(
                execution.Artifact,
                output,
                cancellationToken);
            ActorAssemblyPreflightResultDocument persisted = lease.Document;
            ImmutableArray<ProtocolDiagnostic> outcomeDiagnostics =
                OutcomeDiagnostics(persisted.Artifact.Outcome);
            var protocolArtifact = new ProtocolArtifact(
                ActorAssemblyPreflightSchemas.ArtifactKind,
                ActorAssemblyPreflightSchemas.ResultSchema,
                persisted.Path.Value,
                persisted.Size,
                persisted.Sha256.ToUpperInvariant(),
                "npc assembly preflight",
                requestDigest,
                [contractBinding.Sha256],
                "independentlyVerified");
            var result = new ProtocolCommandResult(
                [
                    ProtocolEffect.Create(
                        AgentEffectKind.ReadWorkspace,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.Workspace),
                    ProtocolEffect.Create(
                        AgentEffectKind.WriteNewArtifact,
                        ApplicationEffectStatus.Completed,
                        ApplicationEffectScope.KLocalOutput)
                ],
                outcomeDiagnostics,
                [protocolArtifact],
                SuccessAuthority(),
                [],
                ActorAssemblyPreflightSchemas.ProtocolResultSchema,
                ParseResult(persisted.Utf8Json))
            {
                TerminalArtifactLease = lease.TransferLease()
            };
            lease.Dispose();
            lease = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ActorAssemblyPreflightResultStoreException exception)
        {
            ImmutableArray<ProtocolDiagnostic> diagnostics =
                [StoreDiagnostic(exception)];
            return Refused(
                Effects(
                    ApplicationEffectStatus.Completed,
                    exception.Promoted
                        ? ApplicationEffectStatus.Failed
                        : StoreStatus(exception)),
                diagnostics,
                PersistenceFailureAuthority(),
                ErrorResult(true, diagnostics));
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private bool TryBind(
        ParsedCommand command,
        out ProtocolV2PhysicalFileBinding? contract,
        out WorkspacePath output,
        out ImmutableArray<ProtocolDiagnostic> diagnostics)
    {
        contract = null;
        output = workspaceRoot;
        var binder = new ProtocolV2WorkflowPathBinder(
            workspaceRoot,
            pathBoundary);
        if (!binder.TryExistingFilePair(
                command,
                "contract",
                "contract-sha256",
                out contract,
                out diagnostics))
            return false;
        if (!binder.TryFreshFile(command, "output", out output, out diagnostics))
            return false;
        if (!binder.TryNoOverlap(
                [contract!.Path],
                [output],
                out diagnostics))
        {
            contract = null;
            return false;
        }
        return true;
    }

    private static ProtocolCommandResult Refused(
        ImmutableArray<ProtocolEffect> effects,
        ImmutableArray<ProtocolDiagnostic> diagnostics,
        ImmutableArray<ProtocolAuthority> authority,
        JsonElement result) =>
        new(
            effects,
            diagnostics,
            [],
            authority,
            [],
            ActorAssemblyPreflightSchemas.ProtocolResultSchema,
            result);

    private static ImmutableArray<ProtocolEffect> Effects(
        ApplicationEffectStatus readStatus,
        ApplicationEffectStatus? writeStatus = null) =>
        writeStatus is null
            ? [ProtocolEffect.Create(
                AgentEffectKind.ReadWorkspace,
                readStatus,
                ApplicationEffectScope.Workspace)]
            : [
                ProtocolEffect.Create(
                    AgentEffectKind.ReadWorkspace,
                    readStatus,
                    ApplicationEffectScope.Workspace),
                ProtocolEffect.Create(
                    AgentEffectKind.WriteNewArtifact,
                    writeStatus.Value,
                    ApplicationEffectScope.KLocalOutput)
            ];

    private static ImmutableArray<ProtocolAuthority> SuccessAuthority() =>
    [
        Authority(
            AgentAuthorityKind.InputAdmission,
            AgentAuthorityState.Established,
            "The exact Actor Assembly contract and uppercase physical digest were admitted."),
        Authority(
            AgentAuthorityKind.SourceProviderIdentity,
            AgentAuthorityState.Established,
            "The hash-bound package and actor identity evidence were evaluated."),
        Authority(
            AgentAuthorityKind.DeterministicMaterialization,
            AgentAuthorityState.Established,
            "The typed Actor Assembly result was canonically materialized."),
        Authority(
            AgentAuthorityKind.IndependentStaticVerification,
            AgentAuthorityState.Established,
            "The promoted result was retained-read, pinned-reopened, and strictly validated."),
        Authority(
            AgentAuthorityKind.OffEnginePreview,
            AgentAuthorityState.NotApplicable,
            "Actor Assembly preflight does not render an off-engine preview."),
        Authority(
            AgentAuthorityKind.HumanVisualAcceptance,
            AgentAuthorityState.Required,
            "Static preflight evidence does not establish human visual acceptance."),
        Authority(
            AgentAuthorityKind.GameRuntimeVerification,
            AgentAuthorityState.Required,
            "Static preflight evidence does not establish Skyrim runtime behavior."),
        Authority(
            AgentAuthorityKind.PromotionApproval,
            AgentAuthorityState.Required,
            "Static preflight does not grant promotion approval.")
    ];

    private static ImmutableArray<ProtocolAuthority> BindingFailureAuthority() =>
        FailureAuthority(
            AgentAuthorityState.Blocked,
            AgentAuthorityState.Required,
            AgentAuthorityState.Required);

    private static ImmutableArray<ProtocolAuthority> ServiceFailureAuthority() =>
        FailureAuthority(
            AgentAuthorityState.Established,
            AgentAuthorityState.Blocked,
            AgentAuthorityState.Blocked);

    private static ImmutableArray<ProtocolAuthority> PersistenceFailureAuthority() =>
        FailureAuthority(
            AgentAuthorityState.Established,
            AgentAuthorityState.Blocked,
            AgentAuthorityState.Blocked);

    private static ImmutableArray<ProtocolAuthority> FailureAuthority(
        AgentAuthorityState input,
        AgentAuthorityState materialization,
        AgentAuthorityState verification) =>
        SuccessAuthority().Select(authority => authority.Kind switch
        {
            AgentAuthorityKind.InputAdmission => authority with
            {
                State = input,
                Reason = $"Input admission is {StateName(input)} after refusal."
            },
            AgentAuthorityKind.DeterministicMaterialization => authority with
            {
                State = materialization,
                Reason = $"Result materialization is {StateName(materialization)} after refusal."
            },
            AgentAuthorityKind.IndependentStaticVerification => authority with
            {
                State = verification,
                Reason = $"Result verification is {StateName(verification)} after refusal."
            },
            _ => authority
        }).ToImmutableArray();

    private static ProtocolAuthority Authority(
        AgentAuthorityKind kind,
        AgentAuthorityState state,
        string reason) => new(kind, state, reason);

    private static ImmutableArray<ProtocolDiagnostic> OutcomeDiagnostics(
        ActorAssemblyOutcome outcome) =>
        outcome is ActorAssemblyOutcome.Blocked or ActorAssemblyOutcome.Unknown
            ? [new ProtocolDiagnostic(
                ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed,
                DiagnosticSeverity.Error,
                $"Actor Assembly preflight outcome is {WireOutcome(outcome)}.",
                DiagnosticClass.Validation,
                new DiagnosticRecovery(
                    RecoveryAction.CorrectInput,
                    "contract",
                    ActorAssemblyPreflightSchemas.ArtifactKind,
                    "Review the typed preflight evidence before retrying.",
                    false))]
            : [];

    private static ImmutableArray<ProtocolDiagnostic> ToProtocolDiagnostics(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Select(ToProtocolDiagnostic).ToImmutableArray();

    private static ProtocolDiagnostic ToProtocolDiagnostic(
        Diagnostic diagnostic)
    {
        if (ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                diagnostic.Code,
                out ProtocolDiagnosticSemantics semantics) &&
            semantics.Severity == diagnostic.Severity)
        {
            return new ProtocolDiagnostic(
                diagnostic.Code,
                diagnostic.Severity,
                diagnostic.Message,
                semantics.Class,
                RecoveryFor(diagnostic.Code, semantics.Class));
        }

        string code = diagnostic.Severity switch
        {
            DiagnosticSeverity.Info =>
                ProtocolV2DiagnosticCodes.NpcBuildPreflightInfo,
            DiagnosticSeverity.Warning =>
                ProtocolV2DiagnosticCodes.NpcBuildPreflightWarning,
            _ => ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed
        };
        const DiagnosticClass diagnosticClass = DiagnosticClass.Validation;
        return new ProtocolDiagnostic(
            code,
            diagnostic.Severity,
            $"{diagnostic.Code}: {diagnostic.Message}",
            diagnosticClass,
            RecoveryFor(code, diagnosticClass));
    }

    private static ProtocolDiagnostic StoreDiagnostic(
        ActorAssemblyPreflightResultStoreException exception)
    {
        if (ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                exception.Code,
                out ProtocolDiagnosticSemantics semantics))
        {
            return new ProtocolDiagnostic(
                exception.Code,
                semantics.Severity,
                exception.Message,
                semantics.Class,
                RecoveryFor(exception.Code, semantics.Class));
        }

        return new ProtocolDiagnostic(
            ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed,
            DiagnosticSeverity.Error,
            exception.Message,
            DiagnosticClass.Operation,
            RecoveryFor(
                ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed,
                DiagnosticClass.Operation));
    }

    private static DiagnosticRecovery RecoveryFor(
        string code,
        DiagnosticClass diagnosticClass) =>
        code is ProtocolV2DiagnosticCodes.SchemaOutputExists or
            ProtocolV2DiagnosticCodes.SchemaOutputParentMissing or
            ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace or
            ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused
            ? new DiagnosticRecovery(
                RecoveryAction.ChooseFreshOutput,
                "output",
                ActorAssemblyPreflightSchemas.ArtifactKind,
                "Choose a fresh ordinary output beneath the exact workspace.",
                false)
            : new DiagnosticRecovery(
                diagnosticClass == DiagnosticClass.Operation
                    ? RecoveryAction.RepairEnvironment
                    : RecoveryAction.CorrectInput,
                null,
                null,
                diagnosticClass == DiagnosticClass.Operation
                    ? "Repair the local filesystem condition and retry."
                    : "Correct the exact Actor Assembly contract binding and retry.",
                false);

    private static JsonElement ErrorResult(
        bool contractAdmitted,
        ImmutableArray<ProtocolDiagnostic> diagnostics)
    {
        object value = new
        {
            schemaVersion = 1,
            artifactKind = ActorAssemblyPreflightSchemas.LegacyErrorArtifactKind,
            contractAdmitted,
            diagnostics = diagnostics.Select(item => new
            {
                item.Code,
                item.Severity,
                item.Message
            }).ToImmutableArray()
        };
        return JsonSerializer.SerializeToElement(value, JsonOptions);
    }

    private static JsonElement ParseResult(ImmutableArray<byte> bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes.AsMemory());
        return document.RootElement.Clone();
    }

    private static ApplicationEffectStatus StoreStatus(
        ActorAssemblyPreflightResultStoreException exception) =>
        ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                exception.Code,
                out ProtocolDiagnosticSemantics semantics) &&
            semantics.Class == DiagnosticClass.Security
            ? ApplicationEffectStatus.Refused
            : ApplicationEffectStatus.Failed;

    private static string StateName(AgentAuthorityState state) =>
        state.ToString().ToLowerInvariant();

    private static string WireOutcome(ActorAssemblyOutcome outcome) =>
        outcome switch
        {
            ActorAssemblyOutcome.Pass => "pass",
            ActorAssemblyOutcome.Blocked => "blocked",
            ActorAssemblyOutcome.Unknown => "unknown",
            ActorAssemblyOutcome.NotApplicable => "notApplicable",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}
