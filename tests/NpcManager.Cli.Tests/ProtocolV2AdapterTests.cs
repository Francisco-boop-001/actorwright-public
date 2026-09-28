using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2AdapterTests
{
    private const string CanonicalCommand = "workspace preflight";
    private const string AliasCommand = "ws-preflight";
    private const string AdapterSchemaId =
        "urn:actorwright:test:adapter-result:v1";
    private const string TargetAlias = "schema-export-test-alias";
    private const string Secret = "adapter-secret-must-not-escape";

    public static async Task RunAsync()
    {
        AssertFinishBooleanDialect();
        ImmutableArray<AgentCommandContract> contracts = CreateContracts();
        Assert(AgentCommandRegistry.Validate(contracts).IsEmpty,
            "adapter fixture contracts were invalid");
        AssertDiagnosticSemanticsAreClosed();

        await AssertRoutingAndKernelIsolation(contracts);
        await AssertValidResultIsPreserved(contracts);
        await AssertTerminalArtifactLeaseLifetime(contracts);
        await AssertEffectCountBudget(contracts);
        await TestOutcomeAuthorityAdmission();
        await AssertEffectVocabularyIsAdmittedExactly(contracts);
        await AssertMalformedResultsAreSanitized(contracts);
        await AssertCancellationAndExceptionAreStable(contracts);
        await AssertRealJournalAdmitsAdapterDiagnosticsAndEffects();
    }

    private static void AssertFinishBooleanDialect()
    {
        foreach (string command in new[] { "npc finish analyze", "npc finish apply" })
        {
            AgentCommandContract contract = AgentCommandRegistry.GetRequired(command);
            AgentOptionContract option = contract.Options.Single(item => item.CliName == "validate-all");
            Assert(option.AllowedValues.SequenceEqual(["true", "false", "1"], StringComparer.Ordinal), "Finish Boolean dialect drifted.");
            foreach (string value in new[] { "true", "false", "1", "invalid", "0" })
            {
                string[] args = [.. command.Split(' '), "--protocol", "2", "--json", "--request", "request.json",
                    "--request-sha256", new string('A', 64), "--proposal", "proposal.json", "--workflow-bundle", "workflow.json",
                    "--workflow-bundle-sha256", new string('A', 64), "--workflow-output", "successor.json", "--validate-all", value,
                    .. command.EndsWith("apply", StringComparison.Ordinal) ? new[] { "--proposal-sha256", new string('B', 64) } : []];
                ProtocolValidationResult result = ProtocolV2CommandLine.Validate(CommandLine.Parse(args));
                Assert(result.Diagnostics.IsEmpty == option.AllowedValues.Contains(value, StringComparer.Ordinal),
                    $"Advertised Finish Boolean '{value}' disagrees with actual V2 admission: " + string.Join(" | ", result.Diagnostics.Select(item => item.Message)));
                ImmutableArray<AgentCommandContract> trueOnly = [contract with
                { Options = contract.Options.Select(item => item.CliName == "validate-all" ? item with { AllowedValues = [] } : item).ToImmutableArray() }];
                ProtocolValidationResult defaultFlag = ProtocolV2CommandLine.Validate(CommandLine.Parse(args), trueOnly);
                Assert(defaultFlag.Diagnostics.IsEmpty == (value == "true"), "An empty Boolean value list must preserve true-only admission.");
            }
        }
    }

    public static async Task TestOutcomeAuthorityAdmission()
    {
        ImmutableArray<AgentCommandContract> baseline = CreateContracts();
        var cases = new[]
        {
            new AuthorityAdmissionCase(
                "success preserves established authority", false,
                AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Established,
                AgentAuthorityState.Established, 0, null),
            new AuthorityAdmissionCase(
                "success cannot downgrade established authority", false,
                AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Established,
                AgentAuthorityState.Required, 1,
                ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid),
            new AuthorityAdmissionCase(
                "refusal may retain completed established authority", true,
                AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Established,
                AgentAuthorityState.Established, 2,
                ProtocolV2DiagnosticCodes.OptionRequired),
            new AuthorityAdmissionCase(
                "refusal may require unestablished declared authority", true,
                AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Established,
                AgentAuthorityState.Required, 2,
                ProtocolV2DiagnosticCodes.OptionRequired),
            new AuthorityAdmissionCase(
                "refusal may block unestablished declared authority", true,
                AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Established,
                AgentAuthorityState.Blocked, 2,
                ProtocolV2DiagnosticCodes.OptionRequired),
            new AuthorityAdmissionCase(
                "refusal may block required authority", true,
                AgentAuthorityKind.GameRuntimeVerification,
                AgentAuthorityState.Required,
                AgentAuthorityState.Blocked, 2,
                ProtocolV2DiagnosticCodes.OptionRequired),
            new AuthorityAdmissionCase(
                "refusal cannot promote required authority", true,
                AgentAuthorityKind.GameRuntimeVerification,
                AgentAuthorityState.Required,
                AgentAuthorityState.Established, 1,
                ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid),
            new AuthorityAdmissionCase(
                "refusal retains declared blocked authority", true,
                AgentAuthorityKind.OffEnginePreview,
                AgentAuthorityState.Blocked,
                AgentAuthorityState.Blocked, 2,
                ProtocolV2DiagnosticCodes.OptionRequired),
            new AuthorityAdmissionCase(
                "refusal cannot relax declared blocked authority", true,
                AgentAuthorityKind.OffEnginePreview,
                AgentAuthorityState.Blocked,
                AgentAuthorityState.Required, 1,
                ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid),
            new AuthorityAdmissionCase(
                "refusal retains not-applicable authority", true,
                AgentAuthorityKind.HumanVisualAcceptance,
                AgentAuthorityState.NotApplicable,
                AgentAuthorityState.NotApplicable, 2,
                ProtocolV2DiagnosticCodes.OptionRequired),
            new AuthorityAdmissionCase(
                "refusal cannot promote not-applicable authority", true,
                AgentAuthorityKind.HumanVisualAcceptance,
                AgentAuthorityState.NotApplicable,
                AgentAuthorityState.Established, 1,
                ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid)
        };

        foreach (AuthorityAdmissionCase testCase in cases)
        {
            ImmutableArray<AgentCommandContract> contracts = baseline
                .Select(contract => contract.Name == CanonicalCommand
                    ? contract with
                    {
                        Authority = contract.Authority.Select(authority =>
                                authority.Kind == testCase.Kind
                                    ? authority with
                                    {
                                        State = testCase.DeclaredState
                                    }
                                    : authority)
                            .ToImmutableArray()
                    }
                    : contract)
                .ToImmutableArray();
            var adapter = new RecordingAdapter(
                [CanonicalCommand],
                (_, digest, _) =>
                {
                    ProtocolCommandResult result = CreateValidResult(digest);
                    result = result with
                    {
                        Diagnostics = testCase.HasError
                            ? [Diagnostic(
                                ProtocolV2DiagnosticCodes.OptionRequired,
                                DiagnosticSeverity.Error,
                                DiagnosticClass.Usage)]
                            : [],
                        Authority = result.Authority.Select(authority =>
                                authority.Kind == testCase.Kind
                                    ? authority with
                                    {
                                        State = testCase.ReturnedState
                                    }
                                    : authority)
                            .ToImmutableArray()
                    };
                    return ValueTask.FromResult(result);
                });
            RunResult run = await Run(
                ["workspace", "preflight", "--protocol", "2", "--json"],
                [adapter],
                contracts,
                CancellationToken.None);
            using JsonDocument envelope = AssertEnvelope(
                run,
                testCase.ExitCode,
                testCase.DiagnosticCode);
            if (testCase.ExitCode != 1)
            {
                JsonElement authority = envelope.RootElement
                    .GetProperty("authority").EnumerateArray().Single(item =>
                        item.GetProperty("kind").GetString() ==
                        JsonNamingPolicy.CamelCase.ConvertName(
                            testCase.Kind.ToString()));
                Assert(authority.GetProperty("state").GetString() ==
                       JsonNamingPolicy.CamelCase.ConvertName(
                           testCase.ReturnedState.ToString()),
                    $"{testCase.Name} did not preserve the admitted authority state");
            }
        }
    }

    private static async Task AssertRoutingAndKernelIsolation(
        ImmutableArray<AgentCommandContract> contracts)
    {
        var exact = new RecordingAdapter([CanonicalCommand], ValidResult);
        var missingSentinel = new RecordingAdapter(["version"], ValidResult);
        var duplicateA = new RecordingAdapter([CanonicalCommand], ValidResult);
        var duplicateB = new RecordingAdapter([CanonicalCommand], ValidResult);
        var kernelHijack = new RecordingAdapter(["capabilities"], ValidResult);
        var helpSentinel = new RecordingAdapter([CanonicalCommand], ValidResult);
        var cases = new[]
        {
            new RoutingCase(
                "exact canonical selection through alias",
                [exact],
                [AliasCommand, "--protocol", "2", "--json"],
                0,
                null,
                [exact],
                [1]),
            new RoutingCase(
                "missing adapter",
                [missingSentinel],
                [CanonicalCommand.Split(' ')[0], CanonicalCommand.Split(' ')[1],
                    "--protocol", "2", "--json"],
                1,
                "protocol-adapter-missing",
                [missingSentinel],
                [0]),
            new RoutingCase(
                "duplicate adapters",
                [duplicateA, duplicateB],
                [CanonicalCommand.Split(' ')[0], CanonicalCommand.Split(' ')[1],
                    "--protocol", "2", "--json"],
                1,
                "protocol-adapter-duplicate",
                [duplicateA, duplicateB],
                [0, 0]),
            new RoutingCase(
                "kernel cannot be intercepted",
                [kernelHijack],
                ["capabilities", "--protocol", "2", "--json"],
                0,
                null,
                [kernelHijack],
                [0]),
            new RoutingCase(
                "scoped help does not invoke adapter",
                [helpSentinel],
                [CanonicalCommand.Split(' ')[0], CanonicalCommand.Split(' ')[1],
                    "--protocol", "2", "--json", "--help"],
                0,
                null,
                [helpSentinel],
                [0])
        };

        foreach (RoutingCase testCase in cases)
        {
            RunResult run = await Run(
                testCase.Args,
                testCase.Adapters,
                contracts,
                CancellationToken.None);
            using JsonDocument envelope = AssertEnvelope(
                run,
                testCase.ExitCode,
                testCase.DiagnosticCode);
            for (var index = 0; index < testCase.Sentinels.Length; index++)
            {
                Assert(testCase.Sentinels[index].Calls ==
                       testCase.ExpectedCalls[index],
                    $"{testCase.Name} invoked the wrong adapter set");
            }

            if (testCase.DiagnosticCode is not null)
                AssertGenericAdapterFailure(envelope, testCase.DiagnosticCode);
        }
    }

    private static async Task AssertValidResultIsPreserved(
        ImmutableArray<AgentCommandContract> contracts)
    {
        var adapter = new RecordingAdapter([CanonicalCommand], ValidResult);
        RunResult run = await Run(
            [AliasCommand, "--protocol", "2", "--json"],
            [adapter],
            contracts,
            CancellationToken.None);
        using JsonDocument envelope = AssertEnvelope(run, 0, null);
        JsonElement root = envelope.RootElement;
        Assert(root.GetProperty("command").GetString() == CanonicalCommand,
            "alias routing did not emit the canonical command name");
        Assert(root.GetProperty("outcome").GetString() == "succeeded",
            "runner did not derive successful outcome from diagnostics");
        Assert(root.GetProperty("effects").EnumerateArray().Any(item =>
                item.GetProperty("kind").GetString() == "readWorkspace" &&
                item.GetProperty("status").GetString() == "completed"),
            "adapter effect was not preserved");
        Assert(root.GetProperty("artifacts")[0].GetProperty("producerCommand")
                .GetString() == CanonicalCommand,
            "adapter artifact was not preserved");
        Assert(root.GetProperty("authority")[0].GetProperty("kind")
                .GetString() == "inputAdmission",
            "adapter authority was not preserved");
        Assert(root.GetProperty("nextActions")[0].GetProperty("command")
                .GetString() == "schema export",
            "adapter next action was not preserved");
        Assert(root.GetProperty("nextActions")[0]
                .GetProperty("requiredBindings")[0]
                .GetProperty("artifactSha256").GetString() ==
               new string('D', 64),
            "adapter next-action binding was not preserved");
        Assert(root.GetProperty("result").GetProperty("Value").GetInt32() == 7,
            "adapter result was not preserved");
        Assert(run.Journal.Records.Count == 1 &&
               run.Journal.Records[0].Effects.Any(item =>
                   item.Kind == AgentEffectKind.ReadWorkspace) &&
               run.Journal.Records[0].Effects.Any(item =>
                   item.Kind == AgentEffectKind.AppendLocalOperationJournal &&
                   item.Status == "attempted"),
            "runner did not journal the adapter result through its sole boundary");
    }

    private static async Task AssertMalformedResultsAreSanitized(
        ImmutableArray<AgentCommandContract> contracts)
    {
        var malformed = new (string Name,
            Func<ProtocolCommandResult, ProtocolCommandResult> Mutate)[]
        {
            ("default effects", result => result with
                { Effects = default }),
            ("unregistered diagnostic", result => result with
                {
                    Diagnostics =
                    [
                        new ProtocolDiagnostic(
                            Secret,
                            DiagnosticSeverity.Error,
                            Secret,
                            DiagnosticClass.Operation,
                            new DiagnosticRecovery(
                                RecoveryAction.RepairEnvironment,
                                null,
                                null,
                                Secret,
                                false))
                    ]
                }),
            ("security warning downgrade", result => result with
                {
                    Diagnostics =
                    [Diagnostic(
                        ProtocolV2DiagnosticCodes.UnsafePathForm,
                        DiagnosticSeverity.Warning,
                        DiagnosticClass.Security)]
                }),
            ("security class relabel", result => result with
                {
                    Diagnostics =
                    [Diagnostic(
                        ProtocolV2DiagnosticCodes.UnsafePathForm,
                        DiagnosticSeverity.Error,
                        DiagnosticClass.Validation)]
                }),
            ("cancellation class relabel", result => result with
                {
                    Diagnostics =
                    [Diagnostic(
                        ProtocolV2DiagnosticCodes.ProtocolOperationCancelled,
                        DiagnosticSeverity.Error,
                        DiagnosticClass.Operation)]
                }),
            ("legacy security class relabel", result => result with
                {
                    Diagnostics =
                    [Diagnostic(
                        "data-root-outside-workspace",
                        DiagnosticSeverity.Error,
                        DiagnosticClass.Validation)]
                }),
            ("journal effect injection", result => result with
                {
                    Effects =
                    [
                        ProtocolEffect.CreateUncheckedForTesting(
                            AgentEffectKind.AppendLocalOperationJournal,
                            "attempted",
                            "workspace-local-journal")
                    ]
                }),
            ("selected-command wrong scope", result => result with
                {
                    Effects =
                    [
                        ProtocolEffect.CreateUncheckedForTesting(
                            AgentEffectKind.ReadWorkspace,
                            "completed",
                            "reviewed-workspace")
                    ]
                }),
            ("undeclared effect", result => result with
                {
                    Effects =
                    [
                        ProtocolEffect.CreateUncheckedForTesting(
                            AgentEffectKind.LaunchDesktop,
                            Secret,
                            Secret)
                    ]
                }),
            ("wrong artifact producer", result => result with
                {
                    Artifacts =
                    [result.Artifacts[0] with { ProducerCommand = Secret }]
                }),
            ("wrong artifact digest", result => result with
                {
                    Artifacts =
                    [result.Artifacts[0] with { RequestDigest = Secret }]
                }),
            ("duplicate authority", result => result with
                {
                    Authority = [result.Authority[0], result.Authority[0]]
                }),
            ("missing authority", result => result with
                {
                    Authority = result.Authority.RemoveAt(
                        result.Authority.Length - 1)
                }),
            ("extra authority", result => result with
                {
                    Authority = result.Authority.Add(new ProtocolAuthority(
                        (AgentAuthorityKind)999,
                        AgentAuthorityState.Required,
                        Secret))
                }),
            ("unsupported established authority", result => result with
                {
                    Authority = result.Authority.Select(authority =>
                            authority.Kind ==
                            AgentAuthorityKind.GameRuntimeVerification
                                ? authority with
                                {
                                    State = AgentAuthorityState.Established
                                }
                                : authority)
                        .ToImmutableArray()
                }),
            ("unknown next action", result => result with
                {
                    NextActions =
                    [result.NextActions[0] with { Command = Secret }]
                }),
            ("next action alias", result => result with
                {
                    NextActions =
                    [result.NextActions[0] with { Command = TargetAlias }]
                }),
            ("unknown next-action option", result => result with
                {
                    NextActions =
                    [result.NextActions[0] with
                        {
                            RequiredBindings =
                            [result.NextActions[0].RequiredBindings[0] with
                                { Option = Secret }]
                        }]
                }),
            ("bare next-action option", result => result with
                {
                    NextActions =
                    [result.NextActions[0] with
                        {
                            RequiredBindings =
                            [result.NextActions[0].RequiredBindings[0] with
                                { Option = "command" }]
                        }]
                }),
            ("duplicate next-action option", result => result with
                {
                    NextActions =
                    [result.NextActions[0] with
                        {
                            RequiredBindings =
                            [
                                result.NextActions[0].RequiredBindings[0],
                                result.NextActions[0].RequiredBindings[0]
                            ]
                        }]
                }),
            ("lowercase next-action artifact hash", result => result with
                {
                    NextActions =
                    [result.NextActions[0] with
                        {
                            RequiredBindings =
                            [result.NextActions[0].RequiredBindings[0] with
                                { ArtifactSha256 = new string('a', 64) }]
                        }]
                }),
            ("default next-action bindings", result => result with
                {
                    NextActions =
                    [result.NextActions[0] with { RequiredBindings = default }]
                }),
            ("arbitrary missing prerequisite", result => result with
                {
                    NextActions =
                    [result.NextActions[0] with
                        { MissingPrerequisites = [Secret] }]
                }),
            ("duplicate missing prerequisite", result => result with
                {
                    NextActions =
                    [result.NextActions[0] with
                        {
                            MissingPrerequisites =
                            ["--output", "--output"]
                        }]
                }),
            ("bound-overlapping missing prerequisite", result => result with
                {
                    NextActions =
                    [result.NextActions[0] with
                        { MissingPrerequisites = ["--command"] }]
                }),
            ("missing result schema", result => result with
                { ResultSchemaId = null }),
            ("unadvertised result schema", result => result with
                { ResultSchemaId = "urn:actorwright:test:unadvertised:v1" }),
            ("malformed result schema", result => result with
                {
                    ResultSchemaId = AdapterSchemaId,
                    Result = JsonSerializer.SerializeToElement(7)
                })
        };

        foreach (var testCase in malformed)
        {
            var adapter = new RecordingAdapter(
                [CanonicalCommand],
                (command, digest, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(testCase.Mutate(
                        CreateValidResult(digest)));
                });
            RunResult run = await Run(
                [CanonicalCommand.Split(' ')[0], CanonicalCommand.Split(' ')[1],
                    "--protocol", "2", "--json"],
                [adapter],
                contracts,
                CancellationToken.None);
            using JsonDocument envelope = AssertEnvelope(
                run,
                1,
                "protocol-adapter-result-invalid");
            AssertGenericAdapterFailure(
                envelope,
                "protocol-adapter-result-invalid");
            Assert(!run.Output.Contains(Secret, StringComparison.Ordinal),
                $"{testCase.Name} leaked malformed adapter details");
        }
    }

    private static async Task AssertEffectCountBudget(
        ImmutableArray<AgentCommandContract> contracts)
    {
        static ImmutableArray<ProtocolEffect> Effects(int count) =>
            Enumerable.Range(0, count)
                .Select(_ => ProtocolEffect.Create(
                    AgentEffectKind.ReadWorkspace,
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectScope.Workspace))
                .ToImmutableArray();

        var thirty = await Run(
            [AliasCommand, "--protocol", "2", "--json"],
            [new RecordingAdapter([CanonicalCommand],
                (_, digest, _) => ValueTask.FromResult(
                    CreateValidResult(digest) with { Effects = Effects(30) }))],
            contracts,
            CancellationToken.None);
        using (JsonDocument envelope = AssertEnvelope(thirty, 0, null))
        {
            Assert(thirty.Journal.Records.Single().Effects.Length == 31,
                "30 command effects did not produce 31 durable effects");
            Assert(envelope.RootElement.GetProperty("effects")
                    .GetArrayLength() == 32,
                "30 command effects did not produce a 32-effect envelope");
        }

        var thirtyOne = await Run(
            [AliasCommand, "--protocol", "2", "--json"],
            [new RecordingAdapter([CanonicalCommand],
                (_, digest, _) => ValueTask.FromResult(
                    CreateValidResult(digest) with { Effects = Effects(31) }))],
            contracts,
            CancellationToken.None);
        using JsonDocument refused = AssertEnvelope(
            thirtyOne,
            1,
            ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid);
        Assert(thirtyOne.Journal.Records.Single().Effects.Length == 1,
            "31 command effects reached the durable record");
    }

    private static async Task AssertEffectVocabularyIsAdmittedExactly(
        ImmutableArray<AgentCommandContract> contracts)
    {
        ImmutableArray<AgentCommandContract> scopeContracts = contracts
            .Select(contract => contract.Name == CanonicalCommand
                ? contract with
                {
                    Effects = contract.Effects.Select(effect =>
                            effect.Kind == AgentEffectKind.ReadWorkspace
                                ? effect with
                                {
                                    AllowedResultScopes =
                                    [
                                        ApplicationEffectScope.Workspace,
                                        ApplicationEffectScope.ReviewedWorkspace
                                    ]
                                }
                                : effect)
                        .ToImmutableArray()
                }
                : contract)
            .ToImmutableArray();
        var cases = new[]
        {
            new EffectVocabularyCase(
                "refused reviewed workspace",
                "refused",
                "reviewed-workspace",
                0,
                null,
                null),
            new EffectVocabularyCase(
                "unknown status",
                "unknown-effect-status",
                "workspace",
                1,
                ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid,
                "unknown-effect-status"),
            new EffectVocabularyCase(
                "unknown scope",
                "completed",
                "unknown-effect-scope",
                1,
                ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid,
                "unknown-effect-scope")
        };

        foreach (EffectVocabularyCase testCase in cases)
        {
            var adapter = new RecordingAdapter(
                [CanonicalCommand],
                (_, digest, _) =>
                {
                    ProtocolCommandResult result = CreateValidResult(digest);
                    return ValueTask.FromResult(result with
                    {
                        Effects =
                        [
                            ProtocolEffect.CreateUncheckedForTesting(
                                result.Effects[0].Kind,
                                testCase.Status,
                                testCase.Scope)
                        ]
                    });
                });
            RunResult run = await Run(
                ["workspace", "preflight", "--protocol", "2", "--json"],
                [adapter],
                scopeContracts,
                CancellationToken.None);
            using JsonDocument envelope = AssertEnvelope(
                run,
                testCase.ExitCode,
                testCase.DiagnosticCode);
            if (testCase.DiagnosticCode is null)
            {
                JsonElement effect = envelope.RootElement
                    .GetProperty("effects")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("kind").GetString() ==
                        "readWorkspace");
                Assert(effect.GetProperty("status").GetString() ==
                       testCase.Status &&
                       effect.GetProperty("scope").GetString() ==
                       testCase.Scope,
                    $"{testCase.Name} was not preserved");
            }
            else
            {
                AssertGenericAdapterFailure(
                    envelope, testCase.DiagnosticCode);
                Assert(testCase.RejectedValue is not null &&
                       !run.Output.Contains(
                           testCase.RejectedValue, StringComparison.Ordinal),
                    $"{testCase.Name} escaped the sanitized boundary");
            }
        }
    }

    private static async Task AssertTerminalArtifactLeaseLifetime(
        ImmutableArray<AgentCommandContract> contracts)
    {
        string rootPath = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            $"protocol-v2-terminal-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        string semanticPath = Path.Combine(rootPath, "primary-semantic.json");
        string workflowPath = Path.Combine(rootPath, "workflow-bundle.json");
        byte[] semanticBytes = Encoding.UTF8.GetBytes("primary-semantic");
        byte[] workflowBytes = Encoding.UTF8.GetBytes("workflow-output");
        File.WriteAllBytes(semanticPath, semanticBytes);
        File.WriteAllBytes(workflowPath, workflowBytes);

        try
        {
            using (var lease = new TrackingTerminalArtifactLease(
                       semanticPath,
                       workflowPath))
            {
                var journal = new MutationProbeJournal(() =>
                    AssertMutationRefusedAndBytesIntact(
                        "journal",
                        semanticPath,
                        semanticBytes,
                        workflowPath,
                        workflowBytes));
                using var outputBuffer = new StringWriter();
                using var output = new WriteLineProbeWriter(
                    outputBuffer,
                    () => AssertMutationRefusedAndBytesIntact(
                        "envelope writer",
                        semanticPath,
                        semanticBytes,
                        workflowPath,
                        workflowBytes));
                var adapter = new RecordingAdapter(
                    [CanonicalCommand],
                    (_, digest, _) => ValueTask.FromResult(
                        CreateValidResult(digest) with
                        {
                            TerminalArtifactLease = lease
                        }));
                var runner = new ProtocolV2Runner(
                    output,
                    _ => journal,
                    [adapter],
                    contracts);

                CommandExitCode exit = await runner.RunAsync(
                    CommandLine.Parse(
                    [
                        CanonicalCommand.Split(' ')[0],
                        CanonicalCommand.Split(' ')[1],
                        "--protocol",
                        "2",
                        "--json"
                    ]),
                    CancellationToken.None);

                Assert(exit == CommandExitCode.Success,
                    "terminal lease success path changed the exit code");
                Assert(journal.Probes == 1 && output.Probes == 1,
                    "terminal lease was not probed at both terminal boundaries");
                Assert(lease.DisposeCalls == 1,
                    "terminal lease was not disposed exactly once after output");
                AssertWriteOpenSucceedsAfterRun(
                    semanticPath,
                    workflowPath);
            }

            using (var invalidLease = new TrackingTerminalArtifactLease(
                       semanticPath,
                       workflowPath))
            {
                var invalidJournal = new DisposalOrderJournal(
                    invalidLease,
                    "invalid adapter result");
                var invalidAdapter = new RecordingAdapter(
                    [CanonicalCommand],
                    (_, digest, _) => ValueTask.FromResult(
                        CreateValidResult(digest) with
                        {
                            Effects = default,
                            TerminalArtifactLease = invalidLease
                        }));
                RunResult invalid = await Run(
                    [
                        CanonicalCommand.Split(' ')[0],
                        CanonicalCommand.Split(' ')[1],
                        "--protocol",
                        "2",
                        "--json"
                    ],
                    [invalidAdapter],
                    contracts,
                    CancellationToken.None,
                    invalidJournal);
                using JsonDocument envelope = AssertEnvelope(
                    invalid,
                    1,
                    ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid);
                Assert(invalidLease.DisposeCalls == 1,
                    "invalid adapter result did not dispose its terminal lease");
                Assert(invalidJournal.ObservedDisposed,
                    "invalid adapter result retained its lease into journaling");
            }

            using (var writerFailureLease = new TrackingTerminalArtifactLease(
                       semanticPath,
                       workflowPath))
            {
                using var throwingOutput = new ThrowingWriteLineProbeWriter(
                    () => AssertMutationRefusedAndBytesIntact(
                        "failing envelope writer",
                        semanticPath,
                        semanticBytes,
                        workflowPath,
                        workflowBytes));
                var writerFailureAdapter = new RecordingAdapter(
                    [CanonicalCommand],
                    (_, digest, _) => ValueTask.FromResult(
                        CreateValidResult(digest) with
                        {
                            TerminalArtifactLease = writerFailureLease
                        }));
                var writerFailureRunner = new ProtocolV2Runner(
                    throwingOutput,
                    _ => new CapturingJournal(),
                    [writerFailureAdapter],
                    contracts);
                bool threw = false;
                try
                {
                    await writerFailureRunner.RunAsync(
                        CommandLine.Parse(
                        [
                            CanonicalCommand.Split(' ')[0],
                            CanonicalCommand.Split(' ')[1],
                            "--protocol",
                            "2",
                            "--json"
                        ]),
                        CancellationToken.None);
                }
                catch (IOException exception) when (
                    exception.Message == ThrowingWriteLineProbeWriter.Message)
                {
                    threw = true;
                }

                Assert(threw,
                    "terminal writer failure did not escape the runner");
                Assert(throwingOutput.Probes == 1,
                    "terminal writer failure did not probe the retained files");
                Assert(writerFailureLease.DisposeCalls == 1,
                    "terminal writer failure did not dispose its lease");
                AssertWriteOpenSucceedsAfterRun(
                    semanticPath,
                    workflowPath);
            }
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static void AssertMutationRefusedAndBytesIntact(
        string stage,
        string semanticPath,
        byte[] semanticBytes,
        string workflowPath,
        byte[] workflowBytes)
    {
        AssertWriteOpenIsRefused(stage, "primary semantic", semanticPath);
        AssertWriteOpenIsRefused(stage, "workflow output", workflowPath);
        Assert(File.ReadAllBytes(semanticPath).SequenceEqual(semanticBytes),
            $"{stage} changed the primary semantic bytes");
        Assert(File.ReadAllBytes(workflowPath).SequenceEqual(workflowBytes),
            $"{stage} changed the workflow output bytes");
    }

    private static void AssertWriteOpenIsRefused(
        string stage,
        string artifact,
        string path)
    {
        bool refused = false;
        try
        {
            using FileStream _ = File.Open(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read);
        }
        catch (IOException)
        {
            refused = true;
        }
        catch (UnauthorizedAccessException)
        {
            refused = true;
        }

        Assert(refused,
            $"{stage} could write-open the retained {artifact} file");
    }

    private static void AssertWriteOpenSucceedsAfterRun(params string[] paths)
    {
        foreach (string path in paths)
        {
            using FileStream _ = File.Open(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
    }

    private static async Task AssertCancellationAndExceptionAreStable(
        ImmutableArray<AgentCommandContract> contracts)
    {
        var cancellation = new RecordingAdapter(
            [CanonicalCommand],
            (_, _, token) => throw new OperationCanceledException(token));
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        var cancellationCases = new[]
        {
            new TerminalJournalCase(
                "already-cancelled caller",
                new RecordingTerminalJournal(),
                false),
            new TerminalJournalCase(
                "terminal journal timeout",
                new TimeoutTerminalJournal(),
                true)
        };
        foreach (TerminalJournalCase testCase in cancellationCases)
        {
            RunResult cancelled = await Run(
                [CanonicalCommand.Split(' ')[0], CanonicalCommand.Split(' ')[1],
                    "--protocol", "2", "--json"],
                [cancellation],
                contracts,
                callerCancellation.Token,
                testCase.Journal);
            using JsonDocument envelope = AssertEnvelope(
                cancelled,
                5,
                "protocol-operation-cancelled");
            Assert(envelope.RootElement.GetProperty("outcome").GetString() ==
                   "cancelled", $"{testCase.Name} changed cancellation outcome");
            Assert(testCase.Journal.Calls == 1,
                $"{testCase.Name} did not append exactly once");
            Assert(!testCase.Journal.ReceivedAlreadyCancelled,
                $"{testCase.Name} received the cancelled caller token");
            Assert(testCase.Journal.Record is not null,
                $"{testCase.Name} did not receive the terminal record");

            bool hasWriteWarning = envelope.RootElement
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Any(item => item.GetProperty("code").GetString() ==
                    "operation-journal-write-failed");
            bool hasExpectedTerminalEffect = envelope.RootElement
                .GetProperty("effects")
                .EnumerateArray()
                .Any(item =>
                    item.GetProperty("kind").GetString() ==
                        "appendLocalOperationJournal" &&
                    item.GetProperty("status").GetString() ==
                        (testCase.ExpectWriteFailure ? "failed" : "completed") &&
                    item.GetProperty("scope").GetString() ==
                        "workspace-local-journal");
            Assert(hasWriteWarning == testCase.ExpectWriteFailure,
                $"{testCase.Name} projected the wrong journal warning state");
            Assert(hasExpectedTerminalEffect,
                $"{testCase.Name} projected the wrong terminal journal effect");
        }

        var exception = new RecordingAdapter(
            [CanonicalCommand],
            (_, _, _) => throw new InvalidOperationException(Secret));
        RunResult failed = await Run(
            [CanonicalCommand.Split(' ')[0], CanonicalCommand.Split(' ')[1],
                "--protocol", "2", "--json"],
            [exception],
            contracts,
            CancellationToken.None);
        using JsonDocument failedEnvelope = AssertEnvelope(
            failed,
            1,
            "protocol-operation-failed");
        Assert(!failed.Output.Contains(Secret, StringComparison.Ordinal),
            "adapter exception text escaped into the envelope");
    }

    private static async Task AssertRealJournalAdmitsAdapterDiagnosticsAndEffects()
    {
        string rootPath = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            $"protocol-v2-adapter-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        try
        {
            var journal = new LocalOperationJournal(new WorkspacePath(rootPath));
            OperationJournalAppendResult result = await journal.AppendAsync(
                new OperationJournalRecord(
                    CanonicalCommand,
                    new string('A', 64),
                    [
                        ProtocolEffect.Create(
                            AgentEffectKind.ReadWorkspace,
                            ApplicationEffectStatus.Completed,
                            ApplicationEffectScope.Workspace),
                        ProtocolEffect.Create(
                            AgentEffectKind.AppendLocalOperationJournal,
                            ApplicationEffectStatus.Attempted,
                            ApplicationEffectScope.WorkspaceLocalJournal)
                    ],
                    [
                        "protocol-adapter-missing",
                        "protocol-adapter-duplicate",
                        "protocol-adapter-result-invalid"
                    ],
                    ["operation"],
                    [],
                    1,
                    "failed",
                    1),
                CancellationToken.None);
            Assert(result.Appended && result.Warning is null,
                "real journal refused registered adapter diagnostics/effects");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static async Task<RunResult> Run(
        string[] args,
        ImmutableArray<IProtocolV2CommandAdapter> adapters,
        ImmutableArray<AgentCommandContract> contracts,
        CancellationToken cancellationToken,
        CapturingJournal? suppliedJournal = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        TextWriter originalError = Console.Error;
        CapturingJournal journal = suppliedJournal ?? new CapturingJournal();
        try
        {
            Console.SetError(error);
            var runner = new ProtocolV2Runner(
                output,
                _ => journal,
                adapters,
                contracts);
            CommandExitCode exit = await runner.RunAsync(
                CommandLine.Parse(args),
                cancellationToken);
            return new RunResult(
                (int)exit,
                output.ToString(),
                error.ToString(),
                journal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private static JsonDocument AssertEnvelope(
        RunResult run,
        int expectedExit,
        string? diagnosticCode)
    {
        Assert(run.ExitCode == expectedExit,
            $"adapter exit changed: {run.ExitCode}");
        Assert(run.Error == string.Empty, "adapter runner wrote stderr");
        string[] lines = run.Output.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
        Assert(lines.Length == 1,
            $"adapter runner emitted {lines.Length} stdout records");
        JsonDocument envelope = JsonDocument.Parse(lines[0]);
        Assert(envelope.RootElement.GetProperty("exitCode").GetInt32() ==
               expectedExit, "adapter envelope exit changed");
        if (diagnosticCode is not null)
        {
            Assert(envelope.RootElement.GetProperty("diagnostics")
                    .EnumerateArray().Any(item =>
                        item.GetProperty("code").GetString() == diagnosticCode),
                $"adapter diagnostic '{diagnosticCode}' was absent");
        }

        return envelope;
    }

    private static void AssertGenericAdapterFailure(
        JsonDocument envelope,
        string code)
    {
        JsonElement diagnostic = envelope.RootElement
            .GetProperty("diagnostics").EnumerateArray().Single();
        Assert(diagnostic.GetProperty("code").GetString() == code &&
               diagnostic.GetProperty("class").GetString() == "operation" &&
               diagnostic.GetProperty("recovery").GetProperty("action")
                   .GetString() == "repairEnvironment" &&
               envelope.RootElement.GetProperty("outcome").GetString() ==
                   "failed",
            $"adapter failure '{code}' did not use the generic operation contract");
    }

    private static ValueTask<ProtocolCommandResult> ValidResult(
        ParsedCommand _,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CreateValidResult(requestDigest));
    }

    private static ProtocolCommandResult CreateValidResult(
        string requestDigest) =>
        new(
            [
                ProtocolEffect.Create(
                    AgentEffectKind.ReadWorkspace,
                    ApplicationEffectStatus.Completed,
                    ApplicationEffectScope.Workspace)
            ],
            [],
            [
                new ProtocolArtifact(
                    "workflow-bundle",
                    "application/json",
                    @"K:\fixture\bundle.json",
                    7,
                    new string('B', 64),
                    CanonicalCommand,
                    requestDigest,
                    [new string('C', 64)],
                    "verified")
            ],
            [
                new ProtocolAuthority(
                    AgentAuthorityKind.InputAdmission,
                    AgentAuthorityState.Established,
                    "Inputs are admitted."),
                new ProtocolAuthority(
                    AgentAuthorityKind.SourceProviderIdentity,
                    AgentAuthorityState.NotApplicable,
                    "No source provider is used."),
                new ProtocolAuthority(
                    AgentAuthorityKind.DeterministicMaterialization,
                    AgentAuthorityState.NotApplicable,
                    "No artifact is materialized."),
                new ProtocolAuthority(
                    AgentAuthorityKind.IndependentStaticVerification,
                    AgentAuthorityState.NotApplicable,
                    "No static verification is performed."),
                new ProtocolAuthority(
                    AgentAuthorityKind.OffEnginePreview,
                    AgentAuthorityState.NotApplicable,
                    "No preview is produced."),
                new ProtocolAuthority(
                    AgentAuthorityKind.HumanVisualAcceptance,
                    AgentAuthorityState.NotApplicable,
                    "No visual review is requested."),
                new ProtocolAuthority(
                    AgentAuthorityKind.GameRuntimeVerification,
                    AgentAuthorityState.Required,
                    "Runtime verification remains required."),
                new ProtocolAuthority(
                    AgentAuthorityKind.PromotionApproval,
                    AgentAuthorityState.NotApplicable,
                    "No promotion is performed.")
            ],
            [
                new ProtocolNextAction(
                    "schema export",
                    "Inspect a command schema.",
                    [
                        new ProtocolNextActionBinding(
                            "--command",
                            "version",
                            new string('D', 64))
                    ],
                    [],
                    false)
            ],
            AdapterSchemaId,
            JsonSerializer.SerializeToElement(new { Value = 7 }));

    private static ImmutableArray<AgentCommandContract> CreateContracts()
    {
        AgentCommandContract template = AgentCommandRegistry.GetRequired(
            "capabilities");
        AgentCommandContract adapterContract = template with
        {
            Name = CanonicalCommand,
            Aliases = [AliasCommand],
            Purpose = "Exercise the generic adapter boundary.",
            ResultSchemaIds = [AdapterSchemaId],
            Effects =
            [
                new AgentEffectContract(
                    AgentEffectKind.ReadWorkspace,
                    "When the adapter runs.",
                    "workspace")
                {
                    AllowedResultScopes =
                        [ApplicationEffectScope.Workspace]
                },
                new AgentEffectContract(
                    AgentEffectKind.AppendLocalOperationJournal,
                    "When the local journal is available.",
                    "workspace-local-journal")
                {
                    AllowedResultScopes =
                        [ApplicationEffectScope.WorkspaceLocalJournal]
                }
            ],
            Authority = template.Authority.Select(authority =>
                    authority.Kind == AgentAuthorityKind.GameRuntimeVerification
                        ? authority with
                        {
                            State = AgentAuthorityState.Required,
                            Reason = "Runtime verification remains required."
                        }
                        : authority)
                .ToImmutableArray()
        };
        return AgentCommandRegistry.All.Select(contract => contract.Name switch
            {
                CanonicalCommand => adapterContract,
                "schema export" => contract with { Aliases = [TargetAlias] },
                _ => contract
            })
            .ToImmutableArray();
    }

    private static ProtocolDiagnostic Diagnostic(
        string code,
        DiagnosticSeverity severity,
        DiagnosticClass diagnosticClass) =>
        new(
            code,
            severity,
            "Adapter diagnostic fixture.",
            diagnosticClass,
            new DiagnosticRecovery(
                RecoveryAction.RepairEnvironment,
                null,
                null,
                "Repair the fixture.",
                false));

    private static void AssertDiagnosticSemanticsAreClosed()
    {
        var expected = new Dictionary<string, DiagnosticClass>(
            StringComparer.Ordinal)
        {
            [ProtocolV2DiagnosticCodes.AlternateDataStreamRefused] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.WorkflowBundlePersistenceFailed] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.WorkflowBundleValidationFailed] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.NpcBuildPreflightInfo] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.NpcBuildPreflightValidationFailed] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.NpcBuildPreflightWarning] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.NpcBuildOperationFailed] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.NpcBuildValidationFailed] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.NpcBuildVerificationFailed] =
                DiagnosticClass.Verification,
            [ProtocolV2DiagnosticCodes.NpcPreviewOperationFailed] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.NpcPreviewValidationFailed] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.NpcPreviewVerificationFailed] =
                DiagnosticClass.Verification,
            [ProtocolV2DiagnosticCodes.OptionConflict] = DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.OptionDuplicate] = DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.OptionEnumValue] = DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.OptionFlagValue] = DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.OptionRequired] = DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.OptionSha256Value] = DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.OptionUnknown] = DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.OptionValueRequired] = DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.OutputRootOutsideWorkspace] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.PathInspectionFailed] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.PresetInspectionInfo] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.PresetInspectionInputHashMismatch] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.PresetInspectionOutputExists] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.PresetInspectionPathRefused] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.PresetInspectionCanonicalityFailed] =
                DiagnosticClass.Verification,
            [ProtocolV2DiagnosticCodes.PresetInspectionPersistenceFailed] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.PresetInspectionValidationFailed] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.PresetInspectionWarning] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.PositionalUnexpected] =
                DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.ProtectedRootRefused] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.ProtocolAdapterDuplicate] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.ProtocolAdapterMissing] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.ProtocolAdapterResultInvalid] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.ProtocolCommandLegacy] =
                DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.ProtocolJsonRequired] =
                DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.ProtocolOperationCancelled] =
                DiagnosticClass.Cancellation,
            [ProtocolV2DiagnosticCodes.ProtocolOperationFailed] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.ProtocolUnsupported] =
                DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.ReparsePointRefused] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.ReviewReceiptPathRefused] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.ReviewReceiptPersistenceFailed] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.ReviewReceiptValidationFailed] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.FinishVerificationFailed] =
                DiagnosticClass.Verification,
            [ProtocolV2DiagnosticCodes.FinishCoreValidationFailed] = DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.FinishCoreInfo] = DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.FinishCoreWarning] = DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.FinishVerificationInfo] =
                DiagnosticClass.Verification,
            [ProtocolV2DiagnosticCodes.FinishVerificationPathRefused] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.FinishVerificationWarning] =
                DiagnosticClass.Verification,
            [ProtocolV2DiagnosticCodes.FinishVerificationWriteFailed] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.ReviewedIntakeOutputExists] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.ReviewedIntakeOutputParentMissing] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.ReviewedIntakeOutputOverlap] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.ReviewedIntakeOutputReuse] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.ReviewedIntakePersistenceFailed] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.ReviewedIntakeInfo] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.ReviewedIntakeWarning] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.ReviewedIntakeValidationFailed] =
                DiagnosticClass.Validation,
            [ProtocolV2DiagnosticCodes.SchemaCommandUnknown] =
                DiagnosticClass.Usage,
            [ProtocolV2DiagnosticCodes.SchemaOutputExists] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.SchemaOutputOutsideKDrive] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.SchemaOutputParentMissing] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.SchemaOutputWriteFailed] =
                DiagnosticClass.Operation,
            [ProtocolV2DiagnosticCodes.UnsafePathForm] =
                DiagnosticClass.Security,
            [ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab] =
                DiagnosticClass.Security
        };
        Assert(expected.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
                ProtocolV2DiagnosticCodes.All),
            "protocol diagnostic semantics were not closed over the vocabulary");
        foreach ((string code, DiagnosticClass diagnosticClass) in expected)
        {
            Assert(ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                    code,
                    out ProtocolDiagnosticSemantics semantics) &&
                   semantics.Severity == (code switch
                       {
                        ProtocolV2DiagnosticCodes.NpcBuildPreflightInfo =>
                            DiagnosticSeverity.Info,
                        ProtocolV2DiagnosticCodes.PresetInspectionInfo =>
                            DiagnosticSeverity.Info,
                        ProtocolV2DiagnosticCodes.ReviewedIntakeInfo =>
                            DiagnosticSeverity.Info,
                       ProtocolV2DiagnosticCodes.FinishCoreInfo or ProtocolV2DiagnosticCodes.FinishVerificationInfo =>
                           DiagnosticSeverity.Info,
                        ProtocolV2DiagnosticCodes.ReviewedIntakeWarning =>
                            DiagnosticSeverity.Warning,
                        ProtocolV2DiagnosticCodes.NpcBuildPreflightWarning =>
                            DiagnosticSeverity.Warning,
                        ProtocolV2DiagnosticCodes.PresetInspectionWarning =>
                            DiagnosticSeverity.Warning,
                       ProtocolV2DiagnosticCodes.FinishCoreWarning or ProtocolV2DiagnosticCodes.FinishVerificationWarning =>
                           DiagnosticSeverity.Warning,
                       _ => DiagnosticSeverity.Error
                   }) &&
                   semantics.Class == diagnosticClass,
                $"diagnostic semantics drifted for '{code}'");
        }

        Assert(ProtocolDiagnosticClassifier.TryGetAuthoritativeSemantics(
                "data-root-outside-workspace",
                out ProtocolDiagnosticSemantics legacySecurity) &&
               legacySecurity.Severity == DiagnosticSeverity.Error &&
               legacySecurity.Class == DiagnosticClass.Security,
            "legacy security diagnostics lacked authoritative security semantics");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record RoutingCase(
        string Name,
        ImmutableArray<IProtocolV2CommandAdapter> Adapters,
        string[] Args,
        int ExitCode,
        string? DiagnosticCode,
        ImmutableArray<RecordingAdapter> Sentinels,
        int[] ExpectedCalls);

    private sealed record AuthorityAdmissionCase(
        string Name,
        bool HasError,
        AgentAuthorityKind Kind,
        AgentAuthorityState DeclaredState,
        AgentAuthorityState ReturnedState,
        int ExitCode,
        string? DiagnosticCode);

    private sealed record EffectVocabularyCase(
        string Name,
        string Status,
        string Scope,
        int ExitCode,
        string? DiagnosticCode,
        string? RejectedValue);

    private sealed record TerminalJournalCase(
        string Name,
        TerminalJournalDouble Journal,
        bool ExpectWriteFailure);

    private sealed record RunResult(
        int ExitCode,
        string Output,
        string Error,
        CapturingJournal Journal);

    private sealed class RecordingAdapter(
        ImmutableArray<string> commands,
        Func<ParsedCommand, string, CancellationToken,
            ValueTask<ProtocolCommandResult>> run)
        : IProtocolV2CommandAdapter
    {
        public ImmutableArray<string> Commands { get; } = commands;

        public int Calls { get; private set; }

        public ValueTask<ProtocolCommandResult> RunAsync(
            ParsedCommand command,
            string requestDigest,
            CancellationToken cancellationToken)
        {
            Calls++;
            return run(command, requestDigest, cancellationToken);
        }
    }

    private sealed class TrackingTerminalArtifactLease : IDisposable
    {
        private readonly FileStream semanticHandle;
        private readonly FileStream workflowHandle;
        private bool disposed;

        public TrackingTerminalArtifactLease(
            string semanticPath,
            string workflowPath)
        {
            semanticHandle = File.Open(
                semanticPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            workflowHandle = File.Open(
                workflowPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }

        public int DisposeCalls { get; private set; }

        public bool IsDisposed => disposed;

        public void Dispose()
        {
            DisposeCalls++;
            if (disposed)
                return;

            disposed = true;
            workflowHandle.Dispose();
            semanticHandle.Dispose();
        }
    }

    private sealed class MutationProbeJournal(Action probe) : CapturingJournal
    {
        public int Probes { get; private set; }

        public override ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            Probes++;
            probe();
            return base.AppendAsync(record, cancellationToken);
        }
    }

    private sealed class DisposalOrderJournal(
        TrackingTerminalArtifactLease lease,
        string stage) : CapturingJournal
    {
        public bool ObservedDisposed { get; private set; }

        public override ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            ObservedDisposed = lease.IsDisposed;
            Assert(ObservedDisposed,
                $"{stage} retained its terminal lease into journaling");
            return base.AppendAsync(record, cancellationToken);
        }
    }

    private sealed class WriteLineProbeWriter(
        TextWriter inner,
        Action probe) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public int Probes { get; private set; }

        public override void WriteLine(string? value)
        {
            Probes++;
            probe();
            inner.WriteLine(value);
        }
    }

    private sealed class ThrowingWriteLineProbeWriter(Action probe)
        : TextWriter
    {
        public const string Message = "injected terminal writer failure";

        public override Encoding Encoding => Encoding.UTF8;

        public int Probes { get; private set; }

        public override void WriteLine(string? value)
        {
            Probes++;
            probe();
            throw new IOException(Message);
        }
    }

    private class CapturingJournal : ILocalOperationJournal
    {
        public List<OperationJournalRecord> Records { get; } = [];

        public virtual ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            Records.Add(record);
            return ValueTask.FromResult(
                new OperationJournalAppendResult(true, null, null));
        }
    }

    private abstract class TerminalJournalDouble : CapturingJournal
    {
        public int Calls { get; protected set; }

        public bool ReceivedAlreadyCancelled { get; protected set; }

        public OperationJournalRecord? Record { get; protected set; }

        protected void Capture(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            Calls++;
            ReceivedAlreadyCancelled = cancellationToken.IsCancellationRequested;
            Record = record;
        }
    }

    private sealed class RecordingTerminalJournal : TerminalJournalDouble
    {
        public override ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            Capture(record, cancellationToken);
            return ValueTask.FromResult(
                new OperationJournalAppendResult(true, null, null));
        }
    }

    private sealed class TimeoutTerminalJournal : TerminalJournalDouble
    {
        public override async ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            Capture(record, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "The terminal journal timeout did not cancel the append.");
        }
    }
}
