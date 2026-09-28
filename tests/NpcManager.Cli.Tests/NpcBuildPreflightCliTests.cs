using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Cli.Tests;

internal static class NpcBuildPreflightCliTests
{
    private const string NpcCreateRequestSchemaIdentifier =
        "npc.create-from-jslot.request.v1";

    public static async Task RunAsync()
    {
        AssertReviewedMasterProviderParity();
        await GoldenSkyrimWorkflowResumptionTests.RunPreflightDependencyClosureAsync();
        AssertBlankOutputPolicy();
        await GoldenSkyrimWorkflowResumptionTests.RunPreflightPluginTypeAsync();
        var root = new WorkspacePath(@"K:\Actorwright");
        var source = new WorkspacePath(
            @"K:\Actorwright\artifacts\preflight-cli-request.json");
        var sourceHash = Hash('1');
        var loader = new FakeLoader(source, sourceHash);
        var build = new FakeJslotBuildService();
        var preflight = new FakePreflightService();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var handler = new RaceMenuJslotNpcBuildCommandHandler(
            build, root, output, error, loader, null, preflight);

        AssertStrictPreflightBinding();

        ParsedCommand preflightCommand = Parse(
            "--preflight-output",
            @"K:\Actorwright\artifacts\reviewed-preflight.json");
        CommandExitCode preflightExit = await handler.RunAsync(
            preflightCommand, CancellationToken.None);
        Require(preflightExit == CommandExitCode.Success &&
                preflight.CreateCalls == 1 && build.Calls == 0 &&
                preflight.LastCreate?.Output?.Value.EndsWith(
                    "reviewed-preflight.json",
                    StringComparison.OrdinalIgnoreCase) == true &&
                output.ToString().Contains("PREFLIGHT READY",
                    StringComparison.Ordinal),
            "Preflight-only mode did not return before build staging.");

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        ParsedCommand reviewedCommand = Parse(
            "--reviewed-preflight",
            @"K:\Actorwright\artifacts\reviewed-preflight.json",
            "--reviewed-preflight-sha256", Hash('2').Value);
        CommandExitCode reviewedExit = await handler.RunAsync(
            reviewedCommand, CancellationToken.None);
        Require(reviewedExit == CommandExitCode.ValidationFailure &&
                build.Calls == 1 &&
                build.LastRequest?.ReviewedPreflight is not null &&
                build.LastRequest.SourceRequest == source &&
                build.LastRequest.SourceRequestSha256 == sourceHash,
            "Reviewed mode did not bind the source and reviewed artifact to execution.");

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        ParsedCommand conflict = Parse(
            "--preflight-output",
            @"K:\Actorwright\artifacts\second-preflight.json",
            "--reviewed-preflight",
            @"K:\Actorwright\artifacts\reviewed-preflight.json",
            "--reviewed-preflight-sha256", Hash('2').Value);
        CommandExitCode conflictExit = await handler.RunAsync(
            conflict, CancellationToken.None);
        Require(conflictExit == CommandExitCode.UsageError &&
                preflight.CreateCalls == 1 && build.Calls == 1 &&
                error.ToString().Contains("jslot-npc-preflight-usage",
                    StringComparison.Ordinal),
            "Conflicting preflight modes were not refused before service dispatch.");

        await AssertProtocolAdapter(loader, preflight);
        await AssertProtocolAdmission(root, loader, preflight);
    }

    private static void AssertReviewedMasterProviderParity()
    {
        string root = Path.Combine(@"K:\Actorwright\artifacts\test-work",
            $"preflight-provider-{Guid.NewGuid():N}");
        string data = Path.Combine(root, "Data");
        string reviewedRoot = Path.Combine(root, "reviewed-providers");
        string provider = Path.Combine(reviewedRoot, "Dawnguard.esm");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(reviewedRoot);
        byte[] bytes = "reviewed-master-provider"u8.ToArray();
        File.WriteAllBytes(provider, bytes);
        try
        {
            var master = new PluginName("Dawnguard.esm");
            var expected = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            var authority = new NpcCreationPluginAuthority(
                master, new WorkspacePath(provider), expected);

            var valid = NpcBuildPreflightDependencyClosureService
                .CollectMasterProviders(new WorkspacePath(data), [master], [authority]);
            Require(valid.Diagnostics.IsEmpty && valid.Dependencies is
                    [{ Kind: "plugin", Path: "Dawnguard.esm", Status: "present" }],
                "Preflight did not honor a valid reviewed master provider outside Data.");

            File.Delete(provider);
            var missing = NpcBuildPreflightDependencyClosureService
                .CollectMasterProviders(new WorkspacePath(data), [master], [authority]);
            Require(missing.Dependencies.Single().Status == "missing" &&
                    missing.Diagnostics.Any(item =>
                        item.Code == "npc-create-master-provider-missing"),
                "Preflight did not refuse a missing reviewed master provider.");

            File.WriteAllBytes(provider, "drifted-master-provider"u8.ToArray());
            var drifted = NpcBuildPreflightDependencyClosureService
                .CollectMasterProviders(new WorkspacePath(data), [master], [authority]);
            Require(drifted.Dependencies.Single().Status == "drift" &&
                    drifted.Diagnostics.Any(item =>
                        item.Code == "npc-create-plugin-authority-hash-mismatch"),
                "Preflight did not refuse a hash-drifted reviewed master provider.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertProtocolAdapter(
        IRaceMenuNpcExecutionRequestFileLoader loader,
        FakePreflightService preflight)
    {
        var adapter = new ProtocolV2NpcCreatePreflightAdapter(loader, preflight);
        ProtocolCommandResult result = await adapter.RunAsync(
            Parse("--preflight-output",
                @"K:\Actorwright\artifacts\protocol-preflight.json"),
            Hash('9').Value.ToUpperInvariant(),
            CancellationToken.None);

        Require(result.Artifacts.IsEmpty &&
                result.Diagnostics.Length == 1 &&
                result.Diagnostics[0].Code ==
                    ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed &&
                result.ResultSchemaId ==
                    AgentProtocolSchemaIds.NpcCreatePreflightResult &&
                result.Authority.Length == 8 &&
                result.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.InputAdmission).State ==
                    AgentAuthorityState.Blocked &&
                result.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.SourceProviderIdentity).State ==
                    AgentAuthorityState.Required &&
                result.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.HumanVisualAcceptance).State ==
                    AgentAuthorityState.NotApplicable &&
                result.Authority.Single(item => item.Kind ==
                    AgentAuthorityKind.PromotionApproval).State ==
                    AgentAuthorityState.NotApplicable &&
                result.Effects.Select(item =>
                        (item.Kind, item.Status, item.Scope))
                    .SequenceEqual([
                        (AgentEffectKind.ReadWorkspace, "refused", "workspace")
                    ]) &&
                result.NextActions.IsEmpty &&
                preflight.CreateCalls == 1,
            "Protocol-v2 NPC preflight admitted a workflow-less direct invocation or dispatched its service.");

        ProtocolCommandResult refused = await adapter.RunAsync(
            Parse("--preflight-output",
                @"K:\Actorwright\artifacts\protocol-preflight.json",
                "--request-sha256", new string('a', 64)),
            Hash('8').Value.ToUpperInvariant(),
            CancellationToken.None);
        Require(refused.Artifacts.IsEmpty &&
                refused.Diagnostics.Length == 1 &&
                refused.Diagnostics[0].Code ==
                    ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed,
            "Protocol-v2 NPC preflight adapter admitted a noncanonical hash binding.");
    }

    private static async Task AssertProtocolAdmission(
        WorkspacePath root,
        IRaceMenuNpcExecutionRequestFileLoader loader,
        INpcBuildPreflightService preflight)
    {
        var failures = new List<string>();
        AgentCommandContract contract =
            AgentCommandRegistry.GetRequired("npc create-from-jslot");
        if (contract.Readiness != ProtocolReadiness.V2 ||
            !contract.Options.Select(item =>
                    (item.CliName, item.ValueKind, item.Required))
                .SequenceEqual([
                    ("request", AgentValueKind.Path, true),
                    ("request-sha256", AgentValueKind.Sha256, true),
                    ("preset", AgentValueKind.Path, true),
                    ("preset-sha256", AgentValueKind.Sha256, true),
                    ("data-root", AgentValueKind.Path, true),
                    ("plugins", AgentValueKind.String, true),
                    ("companion-root", AgentValueKind.Path, true),
                    ("preflight-output", AgentValueKind.Path, false),
                    ("face-bake-authority-output", AgentValueKind.Path, false),
                    ("reviewed-preflight", AgentValueKind.Path, false),
                    ("reviewed-preflight-sha256", AgentValueKind.Sha256, false),
                    ("workflow-bundle", AgentValueKind.Path, true),
                    ("workflow-bundle-sha256", AgentValueKind.Sha256, true),
                    ("workflow-output", AgentValueKind.Path, true)
                ]) ||
            !contract.ResultSchemaIds.SequenceEqual(
                [
                    AgentProtocolSchemaIds.NpcCreatePreflightResult,
                    AgentProtocolSchemaIds.NpcCreateFromJslotBuildResult
                ]) ||
            !contract.Effects.Select(item => item.Kind).SequenceEqual([
                AgentEffectKind.ReadWorkspace,
                AgentEffectKind.WriteNewArtifact,
                AgentEffectKind.AppendLocalOperationJournal
            ]) ||
            contract.RetryPolicy != AgentRetryPolicy.RequiresFreshOutput ||
            contract.Determinism != AgentDeterminism.Deterministic ||
            contract.Authority.Length != 8 ||
            contract.Transitions is not
            [
                {
                    From: AgentWorkflowPhase.Analyze,
                    To: AgentWorkflowPhase.Apply,
                    RequiredBindings: [WorkflowArtifactKinds.NpcBuildPreflight]
                },
                {
                    From: AgentWorkflowPhase.Apply,
                    To: AgentWorkflowPhase.Verify,
                    RequiredBindings: [WorkflowArtifactKinds.NpcPackageManifest]
                }
            ])
            failures.Add("registry did not expose the exact preflight/build v2 contract");

        JsonElement schemaExport = ProtocolV2SchemaService.RenderInline(
            "npc create-from-jslot");
        JsonElement[] resultSchemas = schemaExport.GetProperty("resultSchemas")
            .EnumerateArray().ToArray();
        JsonElement[] documentSchemas = schemaExport
            .GetProperty("documentSchemas").EnumerateArray().ToArray();
        JsonElement requestSchema = documentSchemas.FirstOrDefault(item =>
            item.GetProperty("schemaIdentifier").GetString() ==
                NpcCreateRequestSchemaIdentifier);
        if (resultSchemas.Length != 2 ||
            resultSchemas[0].GetProperty("schemaIdentifier").GetString() !=
                AgentProtocolSchemaIds.NpcCreatePreflightResult ||
            resultSchemas[0].GetProperty("jsonSchema")
                .GetProperty("additionalProperties").GetBoolean() ||
            resultSchemas[1].GetProperty("schemaIdentifier").GetString() !=
                AgentProtocolSchemaIds.NpcCreateFromJslotBuildResult ||
            resultSchemas[1].GetProperty("jsonSchema")
                .GetProperty("additionalProperties").GetBoolean() ||
            documentSchemas.Length != 3 ||
            !documentSchemas.Any(item =>
                item.GetProperty("name").GetString() == "face-bake-authority" &&
                item.GetProperty("direction").GetString() == "output" &&
                item.GetProperty("schemaIdentifier").GetString() == "skyrim-face-bake-authority/1" &&
                !item.GetProperty("jsonSchema").GetProperty("additionalProperties").GetBoolean()) ||
            !documentSchemas.Any(item =>
                item.GetProperty("name").GetString() == "preflight" &&
                item.GetProperty("direction").GetString() == "output" &&
                item.GetProperty("schemaIdentifier").GetString() ==
                    NpcBuildPreflightSchemas.Artifact &&
                !item.GetProperty("jsonSchema")
                    .GetProperty("additionalProperties").GetBoolean()) ||
            requestSchema.ValueKind == JsonValueKind.Undefined ||
            requestSchema.GetProperty("name").GetString() != "request" ||
            requestSchema.GetProperty("direction").GetString() != "input" ||
            requestSchema.GetProperty("jsonSchema")
                .GetProperty("additionalProperties").GetBoolean())
            failures.Add("schema export omitted the closed preflight/build result or artifact schema");

        ImmutableArray<IProtocolV2CommandAdapter> productionAdapters =
            ProtocolV2GoldenWorkflowComposition.Create(root);
        if (!productionAdapters.Any(adapter => adapter.Commands.SequenceEqual(
                ["npc create-from-jslot"])))
            failures.Add("production composition omitted the NPC preflight adapter");

        var journal = new CapturingJournal();
        using var output = new StringWriter();
        var runner = new ProtocolV2Runner(
            output,
            _ => journal,
            [new ProtocolV2NpcCreatePreflightAdapter(loader, preflight)],
            AgentCommandRegistry.All);
        CommandExitCode exit = await runner.RunAsync(
            Parse(
                "--protocol", "2", "--json",
                "--preflight-output",
                @"K:\Actorwright\artifacts\protocol-admission-preflight.json"),
            CancellationToken.None);
        string[] lines = output.ToString().Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (exit != CommandExitCode.UsageError || lines.Length != 1)
        {
            failures.Add($"generic runner did not refuse a workflow-less preflight command (exit={exit}, lines={lines.Length}): {output}");
        }
        else
        {
            using JsonDocument envelope = JsonDocument.Parse(lines[0]);
            JsonElement envelopeRoot = envelope.RootElement;
            if (envelopeRoot.GetProperty("outcome").GetString() != "failed" ||
                envelopeRoot.GetProperty("artifacts").GetArrayLength() != 0 ||
                envelopeRoot.GetProperty("authority").GetArrayLength() != 0 ||
                !envelopeRoot.GetProperty("effects").EnumerateArray()
                    .Select(item => string.Join("|",
                        item.GetProperty("kind").GetString(),
                        item.GetProperty("status").GetString(),
                        item.GetProperty("scope").GetString()))
                    .SequenceEqual([
                        "appendLocalOperationJournal|attempted|workspace-local-journal",
                        "appendLocalOperationJournal|completed|workspace-local-journal"
                    ]) ||
                journal.Records.Count != 1)
                failures.Add("generic runner altered the workflow-required refusal or journal projection");
        }

        Require(failures.Count == 0,
            "Protocol-v2 NPC preflight admission gaps: " +
            string.Join("; ", failures));
    }

    /// <summary>
    /// The one shared blank-route output predicate used by preflight and the
    /// blank writer: only an ordinary '.esp' name is admitted, light output is
    /// the typed `espfe` option on that name, and light budgets never compact.
    /// </summary>
    private static void AssertBlankOutputPolicy()
    {
        const string code = "test-plugin-type";
        foreach ((string plugin, BlankNpcPluginType type, bool supported) in new[]
                 {
                     ("Output.esp", BlankNpcPluginType.Esp, true),
                     ("Output.esp", BlankNpcPluginType.Espfe, true),
                     ("Output.esl", BlankNpcPluginType.Esp, false),
                     ("Output.esl", BlankNpcPluginType.Espfe, false),
                     ("Output.esm", BlankNpcPluginType.Esp, false)
                 })
        {
            ImmutableArray<Diagnostic> verdict = BlankNpcOutputPolicy.Evaluate(
                new PluginName(plugin), type, code);
            Require(supported
                    ? verdict.IsEmpty
                    : verdict is [{ Code: code, Severity: DiagnosticSeverity.Error } refusal] &&
                      refusal.Message.Contains(plugin, StringComparison.Ordinal) &&
                      refusal.Message.Contains("espfe", StringComparison.Ordinal),
                $"Blank output policy misjudged {plugin} as {type}: " +
                string.Join(" | ", verdict.Select(item => item.Code + ": " + item.Message)));
        }
        Require(BlankNpcOutputPolicy.EvaluateLightBudget(BlankNpcPluginType.Esp, 0x2000).IsEmpty &&
                BlankNpcOutputPolicy.EvaluateLightBudget(BlankNpcPluginType.Espfe, 0x1000).IsEmpty,
            "Blank output policy refused an ordinary plugin or a light plugin whose owned IDs end at 0xFFF.");
        ImmutableArray<Diagnostic> exceeded = BlankNpcOutputPolicy.EvaluateLightBudget(
            BlankNpcPluginType.Espfe, 0x1001);
        Require(exceeded is [{ Code: "blank-npc-light-budget", Severity: DiagnosticSeverity.Error } budget] &&
                budget.Message.Contains("0x1000", StringComparison.Ordinal) &&
                budget.Message.Contains("0xFFF", StringComparison.Ordinal),
            "Blank output policy did not refuse a light plugin whose owned IDs exceed 0xFFF with exact numbers.");
    }

    private static void AssertStrictPreflightBinding()
    {
        ParsedCommand valid = Parse(
            "--preflight-output",
            @"K:\Actorwright\artifacts\reviewed-preflight.json");
        RaceMenuNpcPreflightCommandBindingResult bound =
            RaceMenuNpcPreflightCommandBinder.Bind(valid, strict: true);
        Require(bound.IsValid &&
                bound.Binding?.SourceRequest.Value.EndsWith(
                    "preflight-cli-request.json",
                    StringComparison.OrdinalIgnoreCase) == true &&
                bound.Binding.SourceRequestSha256 == Hash('1') &&
                bound.Binding.Preset.Value.EndsWith(
                    "selected.jslot",
                    StringComparison.OrdinalIgnoreCase) &&
                bound.Binding.ExpectedPresetSha256 == Hash('3') &&
                bound.Binding.PluginOrder.Select(item => item.Value)
                    .SequenceEqual(["Skyrim.esm"]) &&
                bound.Binding.Output.Value.EndsWith(
                    "reviewed-preflight.json",
                    StringComparison.OrdinalIgnoreCase),
            "Strict preflight binder lost an exact input binding.");

        (ParsedCommand Command, string Name)[] refused =
        [
            (CommandLine.Parse([
                "npc", "create-from-jslot", "extra",
                "--preflight-output", @"K:\Actorwright\artifacts\out.json"
            ]), "extra positional"),
            (Parse("--preflight-output", @"K:\Actorwright\artifacts\out.json",
                "--unknown", "value"), "unknown option"),
            (Parse("--preflight-output", @"K:\Actorwright\artifacts\out.json",
                "--preset", @"K:\Actorwright\artifacts\other.jslot"),
                "duplicate option"),
            (Parse("--preflight-output", @"K:\Actorwright\artifacts\out.json",
                "--request-sha256", new string('a', 64)), "lowercase hash"),
            (Parse("--preflight-output", @"K:\Actorwright\artifacts\out.json",
                "--plugins", ""), "empty plugins")
        ];
        foreach ((ParsedCommand command, string name) in refused)
            Require(!RaceMenuNpcPreflightCommandBinder.Bind(command, strict: true)
                    .IsValid,
                $"Strict preflight binder accepted {name}.");
    }

    private static ParsedCommand Parse(params string[] additional)
    {
        var arguments = new List<string>
        {
            "npc", "create-from-jslot",
            "--request", @"K:\Actorwright\artifacts\preflight-cli-request.json",
            "--request-sha256", Hash('1').Value.ToUpperInvariant(),
            "--preset", @"K:\Actorwright\artifacts\selected.jslot",
            "--preset-sha256", Hash('3').Value.ToUpperInvariant(),
            "--data-root", @"K:\Actorwright\artifacts\Data",
            "--plugins", "Skyrim.esm",
            "--companion-root", @"K:\Actorwright\artifacts\companion"
        };
        arguments.AddRange(additional);
        return CommandLine.Parse(arguments);
    }

    private static Sha256Hash Hash(char value) => new(
        new string(value, 64));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeLoader(
        WorkspacePath source,
        Sha256Hash sourceHash) : IRaceMenuNpcExecutionRequestFileLoader
    {
        private readonly RaceMenuNpcExecutionRequest request =
            new(null!, null!);

        public ValueTask<RaceMenuNpcExecutionRequestFileLoadResult> LoadAsync(
            RaceMenuNpcExecutionRequestFileLoadRequest requestFile,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new RaceMenuNpcExecutionRequestFileLoadResult(
                RaceMenuNpcExecutionRequestFileLoadStatus.Loaded,
                source,
                sourceHash,
                sourceHash,
                1,
                request,
                []));
    }

    private sealed class FakePreflightService(bool repeatInfo = false) :
        INpcBuildPreflightService
    {
        public int CreateCalls { get; private set; }
        public NpcBuildPreflightRequest? LastCreate { get; private set; }

        public ValueTask<NpcBuildPreflightResult> CreateAsync(
            NpcBuildPreflightRequest request,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            LastCreate = request;
            var artifact = new NpcBuildPreflightArtifact(
                NpcBuildPreflightSchemas.Artifact,
                "Actorwright", "test", "test", null,
                NpcBuildPreflightSchemas.DerivationVersion,
                request.SourceRequest,
                request.SourceRequestSha256,
                request.ExpectedPresetSha256,
                "Skyrim.esm|0x00013746", "female",
                ["Skyrim.esm"], [], [], [], [],
                [new NpcBuildPreflightGate(
                    "request-authority", true, true, "accepted")],
                [new NpcBuildPreflightGate(
                    "preview:test", false, true, "accepted")],
                [], true, true, false);
            var document = new NpcBuildPreflightDocument(
                artifact, [1], Hash('4'), request.Output);
            return ValueTask.FromResult(new NpcBuildPreflightResult(
                true, true, document, repeatInfo
                    ? [
                        new Diagnostic("first", DiagnosticSeverity.Info, "first"),
                        new Diagnostic("second", DiagnosticSeverity.Info, "second")
                    ]
                    : []));
        }

        public ValueTask<NpcBuildPreflightResult> VerifyReviewedAsync(
            NpcBuildPreflightRequest request,
            NpcBuildPreflightReviewAuthority reviewed,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeJslotBuildService : IRaceMenuJslotNpcBuildService
    {
        public int Calls { get; private set; }
        public RaceMenuJslotNpcBuildRequest? LastRequest { get; private set; }

        public ValueTask<RaceMenuJslotNpcBuildResult> ExecuteAsync(
            RaceMenuJslotNpcBuildRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return ValueTask.FromResult(new RaceMenuJslotNpcBuildResult(
                false, null, null, null, null, null,
                [new Diagnostic("expected-refusal",
                    DiagnosticSeverity.Error, "test refusal")]));
        }
    }

    private sealed class CapturingJournal : ILocalOperationJournal
    {
        public List<OperationJournalRecord> Records { get; } = [];

        public ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            Records.Add(record);
            return ValueTask.FromResult(
                new OperationJournalAppendResult(true, null, null));
        }
    }
}
