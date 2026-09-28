using System.Text.Json;
using System.Collections.Immutable;
using System.Reflection;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static class ProtocolV2CliTests
{
    public static async Task RunAsync()
    {
        await CapabilitiesExposeTheCompleteTruthfulRegistry();
        await VersionUsesOneEnvelope();
        await SchemaExportReusesRealCommandSchemas();
        await ScopedHelpUsesContractResultForReadyCommands();
        await ScopedHelpDescribesLegacyCommandsWithoutMakingThemCallable();
        await InvalidSelectionsUseOneEnvelope();
        await MalformedWorkspaceRootUsesOneEnvelope();
        await SchemaOutputRequiresKDrive();
        await SchemaOutputRejectsRelativePathAsTypedRefusal();
        await InlineSchemaExportDoesNotResolveWorkspace();
        await SchemaOutputWritesOneBoundArtifact();
        await SchemaOutputFailureUsesInjectedStage();
        await UnknownSchemaCommandDoesNotEnterOutputStage();
        await JournalEverySelectedInvocationWithoutChangingPrimaryOutcome();
        await ProgramDispatchObservabilityTests.RunAsync();
    }

    private static async Task JournalEverySelectedInvocationWithoutChangingPrimaryOutcome()
    {
        const string secret = "secret-value-must-not-be-journaled";
        var cases = new[]
        {
            new JournalCase(
                "success", ["capabilities", "--protocol", "2", "--json",
                    "--correlation", secret], 0, "succeeded", null),
            new JournalCase(
                "scoped-help", ["version", "--protocol", "2", "--json",
                    "--help", "--correlation", secret], 0, "succeeded", null),
            new JournalCase(
                "validation-refusal", ["capabilities", "extra", "--protocol",
                    "2", "--json", "--correlation", secret], 2, "failed",
                "positional-unexpected")
        };

        foreach (JournalCase testCase in cases)
        {
            var journal = new CapturingJournal();
            var response = await ProtocolV2TestHost.RunWithJournalAsync(
                testCase.Args, journal);
            using var envelope = AssertSingleEnvelope(
                response, testCase.ExitCode,
                testCase.Args[0] == "version" ? "version" : "capabilities");
            Assert(journal.Records.Count == 1,
                $"{testCase.Name} did not append exactly one journal record");
            OperationJournalRecord record = journal.Records.Single();
            Assert(record.Command == envelope.RootElement.GetProperty("command").GetString() &&
                   record.RequestDigest == envelope.RootElement.GetProperty("requestDigest").GetString() &&
                   record.ExitCode == testCase.ExitCode &&
                   record.Outcome == testCase.Outcome,
                $"{testCase.Name} journal did not bind the primary envelope");
            Assert(record.Effects.Any(item =>
                    item.Kind == AgentEffectKind.AppendLocalOperationJournal &&
                    item.Status == "attempted" &&
                    item.Scope == "workspace-local-journal"),
                $"{testCase.Name} journal omitted its attempted effect");
            Assert(testCase.DiagnosticCode is null ||
                   record.DiagnosticCodes.Contains(
                       testCase.DiagnosticCode, StringComparer.Ordinal),
                $"{testCase.Name} journal omitted diagnostic classification");
            string projected = JsonSerializer.Serialize(record);
            Assert(!projected.Contains(secret, StringComparison.Ordinal),
                $"{testCase.Name} journal leaked correlation/options");
            AssertEnvelopeJournalEffect(envelope, "completed");
        }

        var failedJournal = new CapturingJournal(
            new OperationJournalAppendResult(
                false,
                null,
                new Diagnostic(
                    "operation-journal-write-failed",
                    DiagnosticSeverity.Warning,
                    "The local operation journal could not append a redacted record.")));
        var failedAppendResponse = await ProtocolV2TestHost.RunWithJournalAsync(
            ["capabilities", "--protocol", "2", "--json"], failedJournal);
        using (var envelope = AssertSingleEnvelope(
                   failedAppendResponse, 0, "capabilities"))
        {
            AssertEnvelopeJournalEffect(envelope, "failed");
            AssertDiagnostic(envelope, "operation-journal-write-failed");
            Assert(envelope.RootElement.GetProperty("outcome").GetString() ==
                   "succeeded", "journal failure changed primary outcome");
        }

        var noFlagJournal = new CapturingJournal();
        var noFlagFactoryCalls = 0;
        var noFlag = await ProtocolV2TestHost.RunWithJournalFactoryAsync(
            ["version", "--json"], _ =>
            {
                noFlagFactoryCalls++;
                return noFlagJournal;
            });
        var explicitJournal = new CapturingJournal();
        var explicitFactoryCalls = 0;
        var explicitProtocol1 =
            await ProtocolV2TestHost.RunWithJournalFactoryAsync(
                ["version", "--protocol", "1", "--json"], _ =>
                {
                    explicitFactoryCalls++;
                    return explicitJournal;
                });
        Assert(noFlag.ExitCode == 0 && noFlagFactoryCalls == 0 &&
               noFlagJournal.Records.Count == 0 &&
               explicitFactoryCalls == 0 && explicitJournal.Records.Count == 0,
            "protocol v1 constructed or appended the operation journal");
        Assert(explicitProtocol1.ExitCode == noFlag.ExitCode &&
               explicitProtocol1.StandardOutput == noFlag.StandardOutput &&
               explicitProtocol1.StandardError == noFlag.StandardError,
            "explicit protocol 1 diverged from the no-flag legacy boundary");
    }

    private static void AssertEnvelopeJournalEffect(
        JsonDocument envelope,
        string terminalStatus)
    {
        var effects = envelope.RootElement.GetProperty("effects")
            .EnumerateArray().Where(item =>
                item.GetProperty("kind").GetString() ==
                "appendLocalOperationJournal").ToArray();
        Assert(effects.Length == 2 &&
               effects[0].GetProperty("status").GetString() == "attempted" &&
               effects[1].GetProperty("status").GetString() == terminalStatus,
            $"journal effect sequence did not end {terminalStatus}");
    }

    private static async Task CapabilitiesExposeTheCompleteTruthfulRegistry()
    {
        var response = await ProtocolV2TestHost.RunAsync(
            ["capabilities", "--protocol", "2", "--json"]);
        using var envelope = AssertSingleEnvelope(response, 0, "capabilities");
        Assert(response.StandardError == string.Empty, "v2 wrote stderr");
        Assert(!response.StandardOutput.Contains(
                "allowedResultScopes", StringComparison.Ordinal),
            "capabilities exposed internal result-scope metadata");
        var commands = Result(envelope).GetProperty("commands");
        Assert(commands.GetArrayLength() == 142,
            "v2 capabilities omitted commands");
        Assert(ReadReadiness(commands, "npc create") == "legacy",
            "legacy command advertised v2");
        Assert(ReadReadiness(commands, "capabilities") == "v2" &&
               ReadReadiness(commands, "version") == "v2" &&
               ReadReadiness(commands, "schema export") == "v2" &&
               ReadReadiness(commands, "workspace preflight") == "v2",
            "discovery kernel or reviewed workspace preflight was not advertised as v2");
    }

    private static async Task VersionUsesOneEnvelope()
    {
        var response = await ProtocolV2TestHost.RunAsync(
            ["version", "--protocol", "2", "--json"]);
        using var envelope = AssertSingleEnvelope(response, 0, "version");
        var result = Result(envelope);
        Assert(result.GetProperty("productName").GetString() ==
               BuildInfo.ProductName, "v2 product identity changed");
        Assert(result.GetProperty("supportedProtocolVersions")
                .EnumerateArray().Select(item => item.GetString())
                .SequenceEqual(["1", "2"], StringComparer.Ordinal),
            "v2 supported protocol versions changed");
    }

    private static async Task SchemaExportReusesRealCommandSchemas()
    {
        var response = await ProtocolV2TestHost.RunAsync(
        [
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc finish analyze"
        ]);
        using var envelope = AssertSingleEnvelope(response, 0, "schema export");
        Assert(!response.StandardOutput.Contains(
                "allowedResultScopes", StringComparison.Ordinal),
            "schema export exposed internal result-scope metadata");
        var result = Result(envelope);
        Assert(result.GetProperty("contract").GetProperty("name").GetString() ==
               "npc finish analyze", "schema scope changed");
        var schemas = result.GetProperty("documentSchemas");
        Assert(
            schemas.GetArrayLength() == 9 &&
            schemas.EnumerateArray()
                .Select(item => (
                    Name: item.GetProperty("name").GetString()!,
                    Direction: item.GetProperty("direction").GetString()!,
                    SchemaIdentifier: item.GetProperty("schemaIdentifier").GetString()!))
                .SequenceEqual([
                    ("request-legacy", "input", "npc.finish-core.request.v1"),
                    ("request", "input", "npc.finish-core.request.v2"),
                    ("request-external", "input", "npc.finish-core.request.v3"),
                    ("request-policy", "input", "npc.finish-core.request.v4"),
                    ("proposal-legacy", "output", "npc.finish-core.proposal.v1"),
                    ("proposal", "output", "npc.finish-core.proposal.v2"),
                    ("proposal-external", "output", "npc.finish-core.proposal.v3"),
                    ("proposal-policy", "output", "npc.finish-core.proposal.v4"),
                    ("validation", "output", "npc.finish-core.validation.v1")
                ]),
            "Finish Core analyze schema export did not publish the exact nine-row versioned catalog.");

        foreach (var commandName in new[]
                 {
                     "capabilities",
                     "version",
                     "schema export"
                 })
        {
            var commandSchema = await ProtocolV2TestHost.RunAsync(
            [
                "schema", "export", "--protocol", "2", "--json",
                "--command", commandName
            ]);
            using var commandEnvelope = AssertSingleEnvelope(
                commandSchema, 0, "schema export");
            var commandResult = Result(commandEnvelope);
            var contract = commandResult.GetProperty("contract");
            var advertisedId = contract.GetProperty("resultSchemaIds")[0]
                .GetString();
            var resultSchemas = commandResult.GetProperty("resultSchemas");
            Assert(resultSchemas.GetArrayLength() == 1,
                $"{commandName} did not export its one advertised result schema");
            var definition = resultSchemas[0];
            Assert(definition.GetProperty("schemaIdentifier").GetString() ==
                   advertisedId,
                $"{commandName} advertised an unbound result schema identifier");
            var jsonSchema = definition.GetProperty("jsonSchema");
            Assert(jsonSchema.GetProperty("$id").GetString() == advertisedId &&
                   jsonSchema.GetProperty("$schema").GetString() ==
                   "https://json-schema.org/draft/2020-12/schema" &&
                   jsonSchema.GetProperty("type").GetString() == "object",
                $"{commandName} result schema was not a concrete JSON Schema");
            var helpSchema = commandResult.GetProperty(
                "scopedHelpResultSchema");
            Assert(helpSchema.GetProperty("schemaIdentifier").GetString() ==
                   "urn:actorwright:protocol-v2:scoped-help-result:v1" &&
                   helpSchema.GetProperty("jsonSchema").GetProperty("$id")
                       .GetString() ==
                   "urn:actorwright:protocol-v2:scoped-help-result:v1",
                $"{commandName} scoped-help schema was not exportable");
        }
    }

    private static async Task ScopedHelpDescribesLegacyCommandsWithoutMakingThemCallable()
    {
        var help = await ProtocolV2TestHost.RunAsync(
        [
            "package", "archive", "--protocol", "2", "--json",
            "--help"
        ]);
        using (var envelope = AssertSingleEnvelope(
                   help, 0, "package archive"))
        {
            var result = Result(envelope);
            Assert(result.GetProperty("schemaId").GetString() ==
                   "urn:actorwright:protocol-v2:scoped-help-result:v1" &&
                   result.GetProperty("contract").GetProperty("name")
                       .GetString() == "package archive" &&
                   result.GetProperty("contract").GetProperty("readiness")
                       .GetString() == "legacy",
                "legacy scoped help did not return its discoverable contract");
        }

        var invocation = await ProtocolV2TestHost.RunAsync(
        [
            "package", "archive", "--protocol", "2", "--json"
        ]);
        using var refused = AssertSingleEnvelope(
            invocation, 2, "package archive");
        AssertDiagnostic(refused, "protocol-command-legacy");
    }

    private static async Task ScopedHelpUsesContractResultForReadyCommands()
    {
        var output = Path.Combine(
            Environment.CurrentDirectory,
            $"protocol-v2-help-{Guid.NewGuid():N}.json");
        try
        {
            var cases = new[]
            {
                (Name: "capabilities", Args: new[]
                {
                    "capabilities", "--protocol", "2", "--json", "--help"
                }),
                (Name: "version", Args: new[]
                {
                    "version", "--protocol", "2", "--json", "--help"
                }),
                (Name: "schema export", Args: new[]
                {
                    "schema", "export", "--protocol", "2", "--json",
                    "--output", output, "--help"
                })
            };

            foreach (var testCase in cases)
            {
                var response = await ProtocolV2TestHost.RunAsync(testCase.Args);
                using var envelope = AssertSingleEnvelope(
                    response, 0, testCase.Name);
                Assert(!response.StandardOutput.Contains(
                        "allowedResultScopes", StringComparison.Ordinal),
                    $"{testCase.Name} scoped help exposed internal result scopes");
                var result = Result(envelope);
                Assert(result.GetProperty("schemaId").GetString() ==
                       "urn:actorwright:protocol-v2:scoped-help-result:v1",
                    $"{testCase.Name} help returned its ordinary result");
                Assert(result.GetProperty("contract").GetProperty("name")
                        .GetString() == testCase.Name,
                    $"{testCase.Name} help was not registry-scoped");
                Assert(response.StandardError == string.Empty,
                    $"{testCase.Name} help wrote stderr");
            }

            Assert(!File.Exists(output),
                "schema export help executed the command output write");
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    private static async Task InvalidSelectionsUseOneEnvelope()
    {
        var unsupported = await ProtocolV2TestHost.RunAsync(
            ["capabilities", "--protocol", "3", "--json"]);
        using var unsupportedEnvelope = AssertSingleEnvelope(
            unsupported, 2, "capabilities");
        AssertDiagnostic(unsupportedEnvelope, "protocol-unsupported");
        Assert(unsupported.StandardError == string.Empty,
            "unsupported protocol wrote stderr");

        var missingJson = await ProtocolV2TestHost.RunAsync(
            ["capabilities", "--protocol", "2"]);
        using var missingJsonEnvelope = AssertSingleEnvelope(
            missingJson, 2, "capabilities");
        AssertDiagnostic(missingJsonEnvelope, "protocol-json-required");
        Assert(missingJson.StandardError == string.Empty,
            "invalid v2 request wrote stderr");

        var extraPositional = await ProtocolV2TestHost.RunAsync(
            ["capabilities", "extra", "--protocol", "2", "--json"]);
        using var positionalEnvelope = AssertSingleEnvelope(
            extraPositional, 2, "capabilities");
        AssertDiagnostic(positionalEnvelope, "positional-unexpected");
        Assert(extraPositional.StandardError == string.Empty,
            "unexpected v2 positional wrote stderr");
    }

    private static async Task SchemaOutputWritesOneBoundArtifact()
    {
        var output = Path.Combine(
            Environment.CurrentDirectory,
            $"protocol-v2-schema-{Guid.NewGuid():N}.json");
        try
        {
            var response = await ProtocolV2TestHost.RunAsync(
            [
                "schema", "export", "--protocol", "2", "--json",
                "--command", "npc finish analyze", "--output", output
            ]);
            using var envelope = AssertSingleEnvelope(
                response, 0, "schema export");
            var root = envelope.RootElement;
            var artifact = root.GetProperty("artifacts")[0];
            Assert(File.Exists(output), "schema output was not written");
            Assert(artifact.GetProperty("path").GetString() == output,
                "schema artifact path changed");
            Assert(artifact.GetProperty("sha256").GetString()?.Length == 64,
                "schema artifact was not hash-bound");
            AssertSchemaWriteEffect(envelope, "completed");

            var overwrite = await ProtocolV2TestHost.RunAsync(
            [
                "schema", "export", "--protocol", "2", "--json",
                "--command", "npc finish analyze", "--output", output
            ]);
            using var overwriteEnvelope = AssertSingleEnvelope(
                overwrite, 3, "schema export");
            AssertDiagnostic(overwriteEnvelope, "schema-output-exists");
            AssertSchemaWriteEffect(overwriteEnvelope, "refused");
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    private static async Task MalformedWorkspaceRootUsesOneEnvelope()
    {
        var output = Path.Combine(
            Environment.CurrentDirectory,
            $"protocol-v2-malformed-root-{Guid.NewGuid():N}.json");
        var response = await ProtocolV2TestHost.RunWithWorkspaceRootAsync(
        [
            "schema", "export", "--protocol", "2", "--json",
            "--output", output
        ], new string('a', 32_767));
        using var envelope = AssertSingleEnvelope(
            response, 1, "schema export");
        AssertDiagnostic(envelope, "protocol-operation-failed");
        AssertSchemaWriteEffect(envelope, "failed");
        Assert(response.StandardError == string.Empty,
            "malformed workspace root wrote stderr");
        Assert(!File.Exists(output),
            "malformed workspace root wrote a schema artifact");
    }

    private static async Task SchemaOutputRequiresKDrive()
    {
        var malformedWorkspaceRoot = new string('a', 32_767);
        foreach (var output in new[]
                 {
                     @"F:\ExampleGame",
                     @"C:\Windows"
                 })
        {
            var response = await ProtocolV2TestHost.RunWithWorkspaceRootAsync(
            [
                "schema", "export", "--protocol", "2", "--json",
                "--output", output
            ], malformedWorkspaceRoot);
            using var envelope = AssertSingleEnvelope(
                response, 3, "schema export");
            AssertDiagnostic(envelope, "schema-output-outside-k-drive");
            AssertSchemaWriteEffect(envelope, "refused");
            Assert(envelope.RootElement.GetProperty("artifacts")
                    .GetArrayLength() == 0,
                $"non-K schema output {output} produced an artifact");
            Assert(response.StandardError == string.Empty,
                $"non-K schema output {output} wrote stderr");
        }
    }

    private static async Task SchemaOutputRejectsRelativePathAsTypedRefusal()
    {
        string output = $"protocol-v2-relative-{Guid.NewGuid():N}.json";
        try
        {
            Assert(!Path.IsPathFullyQualified(output),
                "relative schema-output fixture became fully qualified");
            var response = await ProtocolV2TestHost.RunAsync(
            [
                "schema", "export", "--protocol", "2", "--json",
                "--command", "version", "--output", output
            ]);
            using var envelope = AssertSingleEnvelope(
                response, 3, "schema export");
            AssertDiagnostic(
                envelope, ProtocolV2DiagnosticCodes.UnsafePathForm);
            AssertSchemaWriteEffect(envelope, "refused");
            Assert(envelope.RootElement.GetProperty("artifacts")
                    .GetArrayLength() == 0,
                "relative schema output produced an artifact");
            Assert(response.StandardError == string.Empty,
                "relative schema output wrote stderr");
        }
        finally
        {
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    private static async Task InlineSchemaExportDoesNotResolveWorkspace()
    {
        var response = await ProtocolV2TestHost.RunWithWorkspaceRootAsync(
        [
            "schema", "export", "--protocol", "2", "--json",
            "--command", "version"
        ], new string('a', 32_767));
        using var envelope = AssertSingleEnvelope(
            response, 0, "schema export");
        Assert(Result(envelope).GetProperty("contract").GetProperty("name")
                .GetString() == "version",
            "inline schema export did not render the requested contract");
        Assert(envelope.RootElement.GetProperty("artifacts")
                .GetArrayLength() == 0,
            "inline schema export produced an artifact");
        AssertSchemaWriteEffect(envelope, null);
        AssertEnvelopeJournalEffect(envelope, "failed");
        AssertDiagnostic(envelope, "operation-journal-write-failed");
        Assert(response.StandardError == string.Empty,
            "inline schema export with malformed workspace wrote stderr");
    }

    private static async Task SchemaOutputFailureUsesInjectedStage()
    {
        Type writerType = typeof(Func<WorkspacePath, JsonElement, string,
            string, ProtocolArtifact>);
        ConstructorInfo constructor = typeof(ProtocolV2Runner).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(TextWriter),
                typeof(Func<WorkspacePath, ILocalOperationJournal>),
                typeof(ImmutableArray<IProtocolV2CommandAdapter>),
                typeof(ImmutableArray<AgentCommandContract>),
                writerType
            ],
            modifiers: null) ?? throw new InvalidOperationException(
                "schema output stage injection seam is missing");
        using var output = new StringWriter();
        var journal = new CapturingJournal();
        Func<WorkspacePath, JsonElement, string, string, ProtocolArtifact>
            writer = (_, _, _, _) => throw new IOException(
                "deterministic schema output failure");
        var runner = (ProtocolV2Runner)constructor.Invoke(
        [
            output,
            (Func<WorkspacePath, ILocalOperationJournal>)(_ => journal),
            ImmutableArray<IProtocolV2CommandAdapter>.Empty,
            AgentCommandRegistry.All,
            writer
        ]);
        string path = Path.Combine(
            Environment.CurrentDirectory,
            $"protocol-v2-injected-failure-{Guid.NewGuid():N}.json");

        CommandExitCode exit = await runner.RunAsync(
            CommandLine.Parse(
            [
                "schema", "export", "--protocol", "2", "--json",
                "--command", "version", "--output", path
            ]),
            CancellationToken.None);

        Assert(exit == CommandExitCode.GeneralFailure,
            "injected schema output failure changed the primary exit");
        using JsonDocument envelope = JsonDocument.Parse(
            output.ToString().Trim());
        AssertDiagnostic(envelope, "protocol-operation-failed");
        AssertSchemaWriteEffect(envelope, "failed");
        Assert(!File.Exists(path),
            "injected schema output failure created an artifact");
    }

    private static async Task UnknownSchemaCommandDoesNotEnterOutputStage()
    {
        string path = Path.Combine(
            Environment.CurrentDirectory,
            $"protocol-v2-unknown-schema-{Guid.NewGuid():N}.json");
        var response = await ProtocolV2TestHost.RunAsync(
        [
            "schema", "export", "--protocol", "2", "--json",
            "--command", "unknown-command", "--output", path
        ]);
        using JsonDocument envelope = AssertSingleEnvelope(
            response, 2, "schema export");
        AssertDiagnostic(envelope, "schema-command-unknown");
        AssertSchemaWriteEffect(envelope, null);
        Assert(!File.Exists(path),
            "unknown schema command entered the output stage");
    }

    private static void AssertSchemaWriteEffect(
        JsonDocument envelope,
        string? expectedStatus)
    {
        JsonElement[] effects = envelope.RootElement.GetProperty("effects")
            .EnumerateArray()
            .Where(effect => effect.GetProperty("kind").GetString() ==
                "writeNewArtifact")
            .ToArray();
        if (expectedStatus is null)
        {
            Assert(effects.Length == 0,
                "schema export reported a write effect before output staging");
            return;
        }
        Assert(effects.Length == 1 &&
               effects[0].GetProperty("status").GetString() == expectedStatus &&
               effects[0].GetProperty("scope").GetString() == "k-local-output",
            $"schema export did not report the {expectedStatus} output stage");
    }

    private static JsonDocument AssertSingleEnvelope(
        CliBoundaryResult response,
        int expectedExit,
        string expectedCommand)
    {
        Assert(response.ExitCode == expectedExit,
            $"{expectedCommand} exit changed: {response.ExitCode}");
        var lines = response.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
        Assert(lines.Length == 1,
            $"{expectedCommand} emitted {lines.Length} stdout records");
        var document = JsonDocument.Parse(lines[0]);
        var root = document.RootElement;
        Assert(root.GetProperty("protocolVersion").GetString() == "2",
            "protocol envelope version changed");
        Assert(root.GetProperty("command").GetString() == expectedCommand,
            "protocol envelope command changed");
        Assert(root.GetProperty("exitCode").GetInt32() == expectedExit,
            "protocol envelope exit changed");
        return document;
    }

    private static JsonElement Result(JsonDocument envelope) =>
        envelope.RootElement.GetProperty("result");

    private static string? ReadReadiness(
        JsonElement commands,
        string commandName) =>
        commands.EnumerateArray().Single(item =>
            item.GetProperty("name").GetString() == commandName)
            .GetProperty("readiness").GetString();

    private static void AssertDiagnostic(
        JsonDocument envelope,
        string code) =>
        Assert(envelope.RootElement.GetProperty("diagnostics")
                .EnumerateArray().Any(item =>
                    item.GetProperty("code").GetString() == code),
            $"diagnostic '{code}' was not emitted");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record JournalCase(
        string Name,
        string[] Args,
        int ExitCode,
        string Outcome,
        string? DiagnosticCode);

    private sealed class CapturingJournal(
        OperationJournalAppendResult? result = null) : ILocalOperationJournal
    {
        public List<OperationJournalRecord> Records { get; } = [];

        public ValueTask<OperationJournalAppendResult> AppendAsync(
            OperationJournalRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(record);
            return ValueTask.FromResult(result ??
                new OperationJournalAppendResult(true, null, null));
        }
    }
}
