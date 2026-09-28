using NpcManager.Application;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;

namespace NpcManager.Architecture.Tests;

internal static class AgentProtocolContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly string[] V2Commands =
    [
        "capabilities",
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "schema export",
        "version",
        "workspace preflight"
    ];

    private static readonly string[] ReadyWorkflowCommands =
    [
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight"
    ];

    public static void RunRegistry()
    {
        var contracts = AgentCommandRegistry.All;
        Assert(contracts.Length == 142,
            "v2 registry must project all legacy commands");
        Assert(CommandCatalog.All.Length == 142 &&
               CommandCatalog.All.Select(item => item.Name)
                   .Distinct(StringComparer.Ordinal).Count() == 142,
            "the exact 142-name command catalogue changed");
        Assert(contracts.Select(item => item.Name).SequenceEqual(
                CommandCatalog.All.Select(item => item.Name),
                StringComparer.Ordinal),
            "v2 registry must preserve catalogue order and names");
        Assert(AgentCommandRegistry.Validate().Length == 0,
            "registry validation returned errors");
        AssertAuthorityValidation();
        AssertClosedFailureProjection();
        AssertTypedEffectConstructionBoundary();
        AssertHiddenAllowedResultScopes();

        Assert(contracts.Where(item =>
                    item.Readiness == ProtocolReadiness.V2)
                .Select(item => item.Name)
                .OrderBy(item => item, StringComparer.Ordinal)
                .SequenceEqual(V2Commands, StringComparer.Ordinal),
            "the discovery kernel and current preview.281 workflow surfaces must advertise v2 behavior");
        Assert(contracts.Where(item =>
                    item.Readiness == ProtocolReadiness.V2 &&
                    item.Name is not ("capabilities" or "schema export" or "version"))
                .Select(item => item.Name)
                .OrderBy(item => item, StringComparer.Ordinal)
                .SequenceEqual(ReadyWorkflowCommands, StringComparer.Ordinal),
            "preview.281 must advertise exactly the current ready workflow commands");
        foreach (string deferredCommand in new[]
                 { "gui" })
        {
            AgentCommandContract deferred = contracts.Single(item =>
                item.Name == deferredCommand);
            Assert(deferred.Readiness == ProtocolReadiness.Legacy &&
                   deferred.ResultSchemaIds.IsEmpty,
                $"deferred public command '{deferredCommand}' must remain legacy and unadvertised");
        }
        Assert(contracts.Where(item =>
                    item.Readiness == ProtocolReadiness.V2)
                .All(HasRichMetadata),
            "a v2-ready command lost complete rich metadata");
        AgentCommandContract assemblyPreflight = contracts.Single(item =>
            item.Name == "npc assembly preflight");
        Assert(assemblyPreflight.Readiness == ProtocolReadiness.V2 &&
               assemblyPreflight.Options.Select(item =>
                       (item.CliName, item.ValueKind, item.Required))
                   .SequenceEqual([
                       ("contract", AgentValueKind.Path, true),
                       ("contract-sha256", AgentValueKind.Sha256, true),
                       ("output", AgentValueKind.Path, true)
                   ]) &&
               assemblyPreflight.InputArtifactKinds.SequenceEqual(
                   ["actor-assembly-preflight-contract"],
                   StringComparer.Ordinal) &&
               assemblyPreflight.ResultSchemaIds.SequenceEqual([
                   "urn:actorwright:protocol-v2:npc-assembly-preflight-result:v1"
               ], StringComparer.Ordinal) &&
               assemblyPreflight.Effects.Select(item =>
                       (item.Kind, item.Condition, item.Scope))
                   .SequenceEqual([
                       (AgentEffectKind.ReadWorkspace,
                           "When the exact contract file and uppercase physical SHA-256 are admitted before service execution.",
                           "The exact K-local --contract file."),
                       (AgentEffectKind.WriteNewArtifact,
                           "Only after a typed preflight outcome is canonically persisted and independently reopened.",
                           "The fresh exact K-local --output file."),
                       (AgentEffectKind.AppendLocalOperationJournal,
                           "When the operation journal is available.",
                           "<labRoot>/.actorwright/operations")
                   ]) &&
               assemblyPreflight.Transitions.IsEmpty &&
               assemblyPreflight.RetryPolicy ==
                   AgentRetryPolicy.RequiresFreshOutput &&
               assemblyPreflight.Determinism == AgentDeterminism.Deterministic &&
               !assemblyPreflight.SupportsDryRun,
            "v2 Actor Assembly preflight target metadata changed");
        Assert(CommandCatalog.All.All(item =>
                    AgentCommandRegistry.GetLegacyDiscoveryRequired(item.Name)
                        .ContractStatus == AgentContractStatus.Complete),
            "a V1 scoped discovery projection remains incomplete");

        AssertDiscoveryDraft(
            AgentCommandRegistry.GetRequired("CAPABILITIES"));
        AssertDiscoveryDraft(
            AgentCommandRegistry.GetRequired("version"));

        var schemaExport = AgentCommandRegistry.GetRequired("schema export");
        Assert(schemaExport.Options.Select(item => item.CliName)
                .SequenceEqual(["command", "output"], StringComparer.Ordinal),
            "schema export options changed");
        Assert(schemaExport.Options.All(item =>
                !item.Required &&
                item.AllowedValues.Length == 0 &&
                item.ConflictsWith.Length == 0 &&
                !item.SecretLike),
            "schema export options must remain optional and unconstrained");
        Assert(schemaExport.Options[0].JsonName == "command" &&
               schemaExport.Options[0].ValueKind == AgentValueKind.String,
            "schema export --command contract changed");
        Assert(schemaExport.Options[1].JsonName == "output" &&
               schemaExport.Options[1].ValueKind == AgentValueKind.Path,
            "schema export --output contract changed");
        Assert(schemaExport.ResultSchemaIds.SequenceEqual(
                ["urn:actorwright:protocol-v2:schema-export-result:v1"],
                StringComparer.Ordinal),
            "schema export result schema changed");
        Assert(schemaExport.Limitations.Contains(
                "Protocol v2 schema export refuses to overwrite an existing output file.",
                StringComparer.Ordinal),
            "schema export must declare no-overwrite behavior");
        Assert(schemaExport.Effects.Any(item =>
                item.Kind == AgentEffectKind.WriteNewArtifact &&
                item.Condition == "When --output is supplied." &&
                item.Scope == "The admitted K-local --output path."),
            "schema export conditional write effect changed");
        Assert(schemaExport.RetryPolicy == AgentRetryPolicy.RequiresFreshOutput,
            "schema export retry policy changed");
        AssertCompleteKernelDraft(schemaExport);

        Assert(BuildInfo.ProtocolVersion == "1",
            "protocol v1 identity changed");
        Assert(BuildInfo.ProductVersion == "1.0.0-preview.281" &&
               BuildInfo.SourceLine == "preview.281-public",
            "current product identity must be preview.281");
        Assert(BuildInfo.LatestProtocolVersion == "2",
            "latest protocol identity changed");
        Assert(BuildInfo.SupportedProtocolVersions.SequenceEqual(
                ["1", "2"],
                StringComparer.Ordinal),
            "supported protocol versions changed");
    }

    private static bool HasRichMetadata(AgentCommandContract contract) =>
        contract.Options.Length > 0 ||
        contract.InputArtifactKinds.Length > 0 ||
        contract.ResultSchemaIds.Length > 0 ||
        contract.Effects.Length > 0 ||
        contract.Authority.Length > 0 ||
        contract.Transitions.Length > 0;

    private static void AssertAuthorityValidation()
    {
        var draft = AgentCommandRegistry.GetRequired("capabilities");
        var v2 = draft with { Readiness = ProtocolReadiness.V2 };

        AssertAuthorityError(
            draft with { Authority = draft.Authority.RemoveAt(0) },
            "missing authority kind: capabilities InputAdmission");
        AssertAuthorityError(
            draft with { Authority = draft.Authority.Add(draft.Authority[0]) },
            "duplicate authority kind: capabilities InputAdmission");
        AssertAuthorityError(
            v2 with { Authority = v2.Authority.RemoveAt(0) },
            "missing authority kind: capabilities InputAdmission");
        AssertAuthorityError(
            v2 with { Authority = v2.Authority.Add(v2.Authority[0]) },
            "duplicate authority kind: capabilities InputAdmission");
    }

    private static void AssertTypedEffectConstructionBoundary()
    {
        Type effectType = typeof(ProtocolEffect);
        ConstructorInfo[] instanceConstructors = effectType.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance);
        Assert(instanceConstructors.All(constructor => constructor.IsPrivate),
            "ProtocolEffect exposes a non-private instance constructor");
        ConstructorInfo rawConstructor = effectType.GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null,
            [typeof(AgentEffectKind), typeof(string), typeof(string)],
            modifiers: null) ?? throw new InvalidOperationException(
                "ProtocolEffect raw constructor is missing");
        Assert(rawConstructor.IsPrivate,
            "ProtocolEffect raw constructor is not exactly private");
        foreach (string propertyName in new[] { "Kind", "Status", "Scope" })
        {
            PropertyInfo property = effectType.GetProperty(propertyName) ??
                throw new InvalidOperationException(
                    $"ProtocolEffect omitted {propertyName}");
            Assert(property.SetMethod is null,
                $"ProtocolEffect.{propertyName} remains mutable");
        }

        Type statusType = effectType.Assembly.GetType(
            "NpcManager.Application.ApplicationEffectStatus") ??
            throw new InvalidOperationException(
                "ApplicationEffectStatus is missing");
        Type scopeType = effectType.Assembly.GetType(
            "NpcManager.Application.ApplicationEffectScope") ??
            throw new InvalidOperationException(
                "ApplicationEffectScope is missing");
        AssertEnumWireMap(statusType,
        [
            ("Attempted", "attempted"),
            ("Completed", "completed"),
            ("Failed", "failed"),
            ("Refused", "refused"),
            ("Blocked", "blocked"),
            ("Skipped", "skipped")
        ]);
        AssertEnumWireMap(scopeType,
        [
            ("Workspace", "workspace"),
            ("ReviewedWorkspace", "reviewed-workspace"),
            ("KLocalOutput", "k-local-output"),
            ("Stdout", "stdout"),
            ("InlineResult", "inline-result"),
            ("WorkspaceLocalJournal", "workspace-local-journal")
        ]);

        MethodInfo create = effectType.GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static) ??
            throw new InvalidOperationException(
                "ProtocolEffect.Create is missing");
        Assert(create.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(AgentEffectKind), statusType, scopeType]),
            "ProtocolEffect.Create does not expose the exact typed boundary");
        AssertInvocationRefused(create,
            AgentEffectKind.ReadWorkspace,
            Enum.ToObject(statusType, 999),
            Enum.Parse(scopeType, "Workspace"));
        AssertInvocationRefused(create,
            AgentEffectKind.ReadWorkspace,
            Enum.Parse(statusType, "Completed"),
            Enum.Parse(scopeType, "KLocalOutput"));
    }

    private static void AssertEnumWireMap(
        Type enumType,
        (string Name, string Wire)[] expected)
    {
        Assert(Enum.GetNames(enumType).SequenceEqual(
                expected.Select(item => item.Name), StringComparer.Ordinal),
            $"{enumType.Name} members changed");
        MethodInfo toWire = typeof(ApplicationEffectVocabulary).GetMethod(
            "ToWire",
            BindingFlags.Public | BindingFlags.Static,
            [enumType]) ?? throw new InvalidOperationException(
                $"ApplicationEffectVocabulary.ToWire({enumType.Name}) is missing");
        foreach (var item in expected)
        {
            object value = Enum.Parse(enumType, item.Name);
            Assert((string?)toWire.Invoke(null, [value]) == item.Wire,
                $"{enumType.Name}.{item.Name} wire value changed");
        }
        AssertInvocationRefused(toWire, Enum.ToObject(enumType, 999));
    }

    private static void AssertInvocationRefused(
        MethodInfo method,
        params object[] arguments)
    {
        try
        {
            _ = method.Invoke(null, arguments);
        }
        catch (TargetInvocationException exception) when (
            exception.InnerException is ArgumentOutOfRangeException)
        {
            return;
        }
        throw new InvalidOperationException(
            $"{method.DeclaringType?.Name}.{method.Name} admitted an invalid value");
    }

    private static void AssertHiddenAllowedResultScopes()
    {
        PropertyInfo property = typeof(AgentEffectContract).GetProperty(
            "AllowedResultScopes") ?? throw new InvalidOperationException(
                "AgentEffectContract.AllowedResultScopes is missing");
        Assert(property.PropertyType.IsGenericType &&
               property.PropertyType.GetGenericTypeDefinition() ==
                   typeof(System.Collections.Immutable.ImmutableArray<>),
            "AllowedResultScopes is not a typed immutable scope set");
        foreach (AgentCommandContract contract in AgentCommandRegistry.All)
        {
            foreach (AgentEffectContract effect in contract.Effects)
            {
                object value = property.GetValue(effect) ??
                    throw new InvalidOperationException(
                        $"{contract.Name} has null allowed result scopes");
                bool isDefaultOrEmpty = (bool)(value.GetType().GetProperty(
                    "IsDefaultOrEmpty")?.GetValue(value) ?? true);
                Assert(!isDefaultOrEmpty,
                    $"{contract.Name} has no allowed result scopes");
            }
        }

        string json = JsonSerializer.Serialize(
            AgentCommandRegistry.GetRequired("workspace preflight"),
            JsonOptions);
        Assert(!json.Contains("allowedResultScopes", StringComparison.Ordinal),
            "allowed result scopes escaped into discovery JSON");

        AgentCommandContract workspace = AgentCommandRegistry.GetRequired(
            "workspace preflight");
        AgentEffectContract read = workspace.Effects.Single(effect =>
            effect.Kind == AgentEffectKind.ReadWorkspace);
        Assert(read.AllowedResultScopes.SequenceEqual(
            [
                ApplicationEffectScope.Workspace,
                ApplicationEffectScope.ReviewedWorkspace
            ]), "workspace preflight read scopes changed");
        Assert(AgentCommandRegistry.All
                .Where(contract => contract.Name != "workspace preflight")
                .SelectMany(contract => contract.Effects)
                .Where(effect => effect.Kind == AgentEffectKind.ReadWorkspace)
                .All(effect => effect.AllowedResultScopes.SequenceEqual(
                    [ApplicationEffectScope.Workspace])),
            "a command borrowed the reviewed-workspace read scope");

        AssertScopeValidationError(
            workspace,
            read with { AllowedResultScopes = [] },
            "missing allowed result scopes: workspace preflight ReadWorkspace");
        AssertScopeValidationError(
            workspace,
            read with
            {
                AllowedResultScopes =
                [
                    ApplicationEffectScope.Workspace,
                    ApplicationEffectScope.Workspace
                ]
            },
            "duplicate allowed result scope: workspace preflight ReadWorkspace Workspace");
        AssertScopeValidationError(
            workspace,
            read with
            {
                AllowedResultScopes = [(ApplicationEffectScope)999]
            },
            "undefined allowed result scope: workspace preflight ReadWorkspace");
        AssertScopeValidationError(
            workspace,
            read with
            {
                AllowedResultScopes = [ApplicationEffectScope.KLocalOutput]
            },
            "illegal allowed result scope: workspace preflight ReadWorkspace KLocalOutput");

        AgentCommandContract legacy = workspace with
        {
            Readiness = ProtocolReadiness.Legacy
        };
        AssertScopeValidationError(
            legacy,
            read with { AllowedResultScopes = [] },
            "missing allowed result scopes: workspace preflight ReadWorkspace");
        Assert(!AgentCommandRegistry.Validate(
                    [legacy with { Effects = [] }])
                .Any(error => error.Contains(
                    "allowed result scope", StringComparison.Ordinal)),
            "scope validation did not remain a no-op for a contract with no effects");
    }

    private static void AssertScopeValidationError(
        AgentCommandContract contract,
        AgentEffectContract replacement,
        string expected)
    {
        AgentCommandContract invalid = contract with
        {
            Effects = contract.Effects.Select(effect =>
                    effect.Kind == replacement.Kind ? replacement : effect)
                .ToImmutableArray()
        };
        Assert(AgentCommandRegistry.Validate([invalid]).Contains(
                expected, StringComparer.Ordinal),
            $"scope validation omitted: {expected}");
    }

    private static void AssertClosedFailureProjection()
    {
        AssertFailureProjection(
            ProtocolV2DiagnosticCodes.ProjectWorkflowBundleFailure(
                "workflow-reparse-refused"),
            ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused,
            DiagnosticClass.Security);
        AssertFailureProjection(
            ProtocolV2DiagnosticCodes.ProjectWorkflowBundleFailure(
                "workflow-write-failed"),
            ProtocolV2DiagnosticCodes.WorkflowBundlePersistenceFailed,
            DiagnosticClass.Operation);
        AssertFailureProjection(
            ProtocolV2DiagnosticCodes.ProjectWorkflowBundleFailure(
                "workflow-path-looking-but-unknown"),
            ProtocolV2DiagnosticCodes.WorkflowBundleValidationFailed,
            DiagnosticClass.Validation);
        AssertFailureProjection(
            ProtocolV2DiagnosticCodes.ProjectFinishVerificationFailure(
                "finish-verification-reparse-refused"),
            ProtocolV2DiagnosticCodes.FinishVerificationPathRefused,
            DiagnosticClass.Security);
        AssertFailureProjection(
            ProtocolV2DiagnosticCodes.ProjectFinishVerificationFailure(
                "finish-verification-promoted-readback-mismatch"),
            ProtocolV2DiagnosticCodes.FinishVerificationWriteFailed,
            DiagnosticClass.Operation);
        AssertFailureProjection(
            ProtocolV2DiagnosticCodes.ProjectFinishVerificationFailure(
                "finish-verification-write-looking-but-unknown"),
            ProtocolV2DiagnosticCodes.FinishVerificationFailed,
            DiagnosticClass.Verification);
    }

    private static void AssertFailureProjection(
        ProtocolFailureProjection actual,
        string expectedCode,
        DiagnosticClass expectedClass) =>
        Assert(actual.Code == expectedCode && actual.Class == expectedClass,
            $"closed failure projection changed for {expectedCode}");

    private static void AssertAuthorityError(
        AgentCommandContract contract,
        params string[] expected)
    {
        var errors = AgentCommandRegistry.Validate([contract]);
        Assert(errors.SequenceEqual(expected, StringComparer.Ordinal),
            $"authority validation changed: {string.Join(" | ", errors)}");
    }

    private static void AssertDiscoveryDraft(
        AgentCommandContract contract)
    {
        Assert(contract.Options.Length == 0,
            $"{contract.Name} must not fabricate command options");
        Assert(contract.ResultSchemaIds.Length == 1 &&
               contract.ResultSchemaIds[0] ==
                   $"urn:actorwright:protocol-v2:{contract.Name}-result:v1",
            $"{contract.Name} result schema changed");
        Assert(contract.Effects.Length == 1 &&
               contract.Effects[0].Kind ==
                   AgentEffectKind.AppendLocalOperationJournal,
            $"{contract.Name} effect declaration changed");
        Assert(contract.RetryPolicy == AgentRetryPolicy.SafeUnchanged,
            $"{contract.Name} retry policy changed");
        AssertCompleteKernelDraft(contract);
    }

    private static void AssertCompleteKernelDraft(
        AgentCommandContract contract)
    {
        Assert(contract.Readiness == ProtocolReadiness.V2,
            $"{contract.Name} must be runnable under protocol v2");
        Assert(contract.Effects.Any(item =>
                item.Kind == AgentEffectKind.AppendLocalOperationJournal &&
                item.Condition == "When the operation journal is available." &&
                item.Scope == "<labRoot>/.actorwright/operations"),
            $"{contract.Name} must declare its possible journal append");
        Assert(contract.Determinism == AgentDeterminism.Deterministic,
            $"{contract.Name} determinism changed");
        Assert(!contract.SupportsDryRun,
            $"{contract.Name} must not advertise an unimplemented dry-run");
        Assert(contract.Authority.Length ==
               Enum.GetValues<AgentAuthorityKind>().Length,
            $"{contract.Name} authority declaration is incomplete");
        Assert(contract.Authority.Select(item => item.Kind)
                .SequenceEqual(Enum.GetValues<AgentAuthorityKind>()),
            $"{contract.Name} authority declaration order changed");
        Assert(contract.Authority[0].State == AgentAuthorityState.Established,
            $"{contract.Name} must establish input admission");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
