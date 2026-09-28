using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static class ReferencePresetCliTests
{
    public static Task TestCommandCatalog()
    {
        string[] names =
        [
            "preset design-propose",
            "preset create-from-reference",
            "npc create-from-reference"
        ];
        foreach (string name in names)
        {
            CommandDescriptor[] matches =
                CommandCatalog.All
                    .Where(item => item.Name == name)
                    .ToArray();
            Require(matches.Length == 1,
                $"catalog must contain exactly one '{name}' descriptor");
            Require(matches[0].Mutates &&
                    matches[0].SchemaVersion == "1" &&
                    matches[0].SupportedGames.SequenceEqual(
                        [GameEdition.SkyrimSpecialEdition]) &&
                    matches[0].Limitations.Any(item =>
                        item.Contains(
                            "P12-009",
                            StringComparison.Ordinal)),
                $"catalog descriptor '{name}' lost its stable P12-009 contract");
        }

        return Task.CompletedTask;
    }

    public static async Task TestHandlerMapsAllThreeCommands()
    {
        ReferencePresetIntake intake = Intake();
        Sha256Hash intakeHash = Hash('1');
        Sha256Hash inferenceHash = Hash('2');
        Sha256Hash reviewHash = Hash('3');
        Sha256Hash resourceHash = Hash('4');
        Sha256Hash authoringHash = Hash('5');
        var sessions = new ControlledSessionService(
            intake,
            new LandmarkInferenceProposal(
                1,
                ReferencePresetAuthorityKind.InferenceProposal,
                intakeHash,
                Hash('9'),
                [],
                [],
                []));
        var transaction = new ControlledTransaction(
            intake,
            authoringHash);
        var currentRequest =
            new RaceMenuNpcExecutionRequest(null!, null!);
        var requestLoader =
            new ControlledExecutionRequestLoader(
                currentRequest);
        var output = new StringWriter();
        var error = new StringWriter();
        var handler = new ReferencePresetCommandHandler(
            transaction,
            sessions,
            requestLoader,
            output,
            error);

        CommandExitCode designExit =
            await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preset", "design-propose",
                    "--intake", "@K:\\inputs\\intake.json",
                    "--intake-sha256", intakeHash.Value,
                    "--output", "K:\\outputs\\design",
                    "--json"
                ]),
                CancellationToken.None);
        using JsonDocument designJson =
            JsonDocument.Parse(output.ToString());
        Require(designExit == CommandExitCode.Success &&
                transaction.DesignRequest is
                {
                    ExpectedIntakeSha256: var mappedIntakeHash
                } &&
                mappedIntakeHash == intakeHash &&
                transaction.DesignRequest.Intake == intake &&
                transaction.DesignRequest.OutputRoot.Value ==
                    @"K:\outputs\design" &&
                designJson.RootElement
                    .GetProperty("schemaVersion")
                    .GetInt32() == 1 &&
                designJson.RootElement
                    .GetProperty("runtimeAuthority")
                    .ValueKind == JsonValueKind.False,
            "design-propose did not map the typed intake or emit schema-1 static authority");

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        CommandExitCode presetExit =
            await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preset", "create-from-reference",
                    "--proposal",
                    @"K:\outputs\design\landmark-proposal.json",
                    "--proposal-sha256", inferenceHash.Value,
                    "--review", @"K:\inputs\review.json",
                    "--review-sha256", reviewHash.Value,
                    "--resource", @"K:\inputs\resource.json",
                    "--resource-sha256", resourceHash.Value,
                    "--jslot-output",
                    @"K:\outputs\draft\preset\Controlled Reference.jslot",
                    "--evidence-root", @"K:\outputs\draft",
                    "--json"
                ]),
                CancellationToken.None);
        using JsonDocument presetJson =
            JsonDocument.Parse(output.ToString());
        Require(presetExit == CommandExitCode.Success &&
                transaction.WriteRequest is
                {
                    Apply: false,
                    AcceptedAuthoringProposalSha256: null
                } &&
                transaction.WriteRequest.IntakePath.Value ==
                    @"K:\outputs\design\authoring-intake.json" &&
                transaction.WriteRequest.IntakeSha256 ==
                    intakeHash &&
                transaction.WriteRequest
                    .InferenceProposalSha256 ==
                    inferenceHash &&
                transaction.WriteRequest.ReviewedDesignSha256 ==
                    reviewHash &&
                transaction.WriteRequest.ResourceSnapshotSha256 ==
                    resourceHash &&
                presetJson.RootElement
                    .GetProperty("proposalSha256")
                    .GetString() == authoringHash.Value,
            "preset create-from-reference did not derive and map the complete authority chain");

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        CommandExitCode npcExit =
            await handler.RunAsync(
                CommandLine.Parse(
                [
                    "npc", "create-from-reference",
                    "--proposal",
                    @"K:\outputs\design\landmark-proposal.json",
                    "--proposal-sha256", inferenceHash.Value,
                    "--review", @"K:\inputs\review.json",
                    "--review-sha256", reviewHash.Value,
                    "--resource", @"K:\inputs\resource.json",
                    "--resource-sha256", resourceHash.Value,
                    "--accepted-proposal-sha256",
                    authoringHash.Value,
                    "--request", @"@K:\inputs\npc-request.json",
                    "--request-sha256", Hash('6').Value,
                    "--data-root", @"K:\inputs\Data",
                    "--plugins", "Skyrim.esm,Update.esm,P12Fixture.esp",
                    "--transaction-root", @"K:\outputs\npc",
                    "--apply",
                    "--json"
                ]),
                CancellationToken.None);
        using JsonDocument npcJson =
            JsonDocument.Parse(output.ToString());
        Require(npcExit == CommandExitCode.Success &&
                transaction.NpcRequest is not null &&
                transaction.NpcRequest.Authoring.Apply &&
                transaction.NpcRequest.Authoring
                    .AcceptedAuthoringProposalSha256 ==
                    authoringHash &&
                ReferenceEquals(
                    transaction.NpcRequest.JslotBuildRequest
                        .CurrentRequest,
                    currentRequest) &&
                transaction.NpcRequest.JslotBuildRequest
                    .PluginOrder.Select(item => item.Value)
                    .SequenceEqual(
                    [
                        "Skyrim.esm",
                        "Update.esm",
                        "P12Fixture.esp"
                    ]) &&
                npcJson.RootElement
                    .GetProperty("runtimeAuthority")
                    .ValueKind == JsonValueKind.False,
            "npc create-from-reference did not preserve the reviewed NPC request or plugin order");
        Require(error.ToString().Length == 0,
            $"successful reference commands wrote errors: {error}");
    }

    public static async Task TestHandlerRefusesUsageAndCancellation()
    {
        ReferencePresetIntake intake = Intake();
        var sessions = new ControlledSessionService(
            intake,
            new LandmarkInferenceProposal(
                1,
                ReferencePresetAuthorityKind.InferenceProposal,
                Hash('1'),
                Hash('9'),
                [],
                [],
                []));
        var transaction =
            new ControlledTransaction(
                intake,
                Hash('5'))
            {
                CancelDesign = true
            };
        var output = new StringWriter();
        var error = new StringWriter();
        var handler = new ReferencePresetCommandHandler(
            transaction,
            sessions,
            new ControlledExecutionRequestLoader(
                new RaceMenuNpcExecutionRequest(
                    null!, null!)),
            output,
            error);

        CommandExitCode unknown =
            await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preset", "design-propose",
                    "--intake", @"K:\inputs\intake.json",
                    "--intake-sha256", Hash('1').Value,
                    "--output", @"K:\outputs\design",
                    "--surprise", "true",
                    "--json"
                ]),
                CancellationToken.None);
        Require(unknown == CommandExitCode.UsageError &&
                transaction.DesignRequest is null &&
                error.ToString().Contains(
                    "unknown-option",
                    StringComparison.Ordinal),
            "unknown reference command option did not fail before the transaction");

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        CommandExitCode missing =
            await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preset", "create-from-reference",
                    "--proposal", @"K:\proposal.json",
                    "--json"
                ]),
                CancellationToken.None);
        Require(missing == CommandExitCode.UsageError &&
                transaction.WriteRequest is null &&
                error.ToString().Contains(
                    "usage-error",
                    StringComparison.Ordinal),
            "missing hash-bound reference options entered the transaction");

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        CommandExitCode cancelled =
            await handler.RunAsync(
                CommandLine.Parse(
                [
                    "preset", "design-propose",
                    "--intake", @"K:\inputs\intake.json",
                    "--intake-sha256", Hash('1').Value,
                    "--output", @"K:\outputs\design",
                    "--json"
                ]),
                CancellationToken.None);
        Require(cancelled == CommandExitCode.Cancelled &&
                error.ToString().Contains(
                    "\"code\": \"cancelled\"",
                    StringComparison.Ordinal),
            "transaction cancellation did not map to the stable cancelled exit and JSON error");
    }

    public static async Task TestRunnerDispatchesReferenceCommands()
    {
        ReferencePresetIntake intake = Intake();
        var transaction = new ControlledTransaction(
            intake, Hash('5'));
        (CliRunner runner, StringWriter output,
            StringWriter error) =
            Program.CreateRunnerForReferencePresetTests(
                transaction,
                new ControlledSessionService(
                    intake,
                    new LandmarkInferenceProposal(
                        1,
                        ReferencePresetAuthorityKind
                            .InferenceProposal,
                        Hash('1'),
                        Hash('9'),
                        [],
                        [],
                        [])),
                new ControlledExecutionRequestLoader(
                    new RaceMenuNpcExecutionRequest(
                        null!, null!)));
        CommandExitCode exit = await runner.RunAsync(
            CommandLine.Parse(
            [
                "preset", "design-propose",
                "--intake", @"K:\inputs\intake.json",
                "--intake-sha256", Hash('1').Value,
                "--output", @"K:\outputs\design",
                "--json"
            ]),
            CancellationToken.None);
        Require(exit == CommandExitCode.Success &&
                transaction.DesignRequest is not null &&
                output.ToString().Contains(
                    "\"command\": \"preset design-propose\"",
                    StringComparison.Ordinal) &&
                error.ToString().Length == 0,
            "CliRunner did not dispatch the cataloged reference command to its typed handler");
    }

    private static ReferencePresetIntake Intake()
    {
        var race = new FormReference(
            new PluginName("Skyrim.esm"),
            new FormId(0x00013746));
        return new ReferencePresetIntake(
            1,
            "p12-cli-test",
            "Controlled Reference",
            race,
            NpcSex.Female,
            50,
            "high-poly-head",
            new WorkspacePath(
                @"K:\inputs\baseline.jslot"),
            Hash('a'),
            "wide cheeks",
            [
                new ReferenceImageAuthority(
                    "front",
                    new WorkspacePath(
                        @"K:\inputs\front.png"),
                    Hash('b'),
                    1024,
                    ReferenceImageViewRole.Front)
            ],
            new RaceMenuPresetTarget(
                "p12-cli-test",
                race,
                NpcSex.Female,
                new WorkspacePath(@"K:\inputs\Data"),
                []));
    }

    private sealed class ControlledSessionService(
        ReferencePresetIntake intake,
        LandmarkInferenceProposal inference) :
        IReferencePresetSessionService
    {
        public ValueTask<ReferencePresetSessionWriteResult>
            WriteAsync(
                ReferencePresetSessionWriteRequest request,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReferencePresetSessionReadResult>
            ReadAsync(
                ReferencePresetSessionReadRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReferencePresetSessionDocument document =
                request.ExpectedKind switch
                {
                    ReferencePresetSessionDocumentKind.Intake =>
                        new(
                            ReferencePresetSessionDocumentKind
                                .Intake,
                            Intake: intake),
                    ReferencePresetSessionDocumentKind
                        .InferenceProposal =>
                        new(
                            ReferencePresetSessionDocumentKind
                                .InferenceProposal,
                            InferenceProposal: inference),
                    _ => throw new InvalidOperationException(
                        "unexpected session kind")
                };
            return ValueTask.FromResult(
                new ReferencePresetSessionReadResult(
                    document,
                    request.ExpectedSha256,
                    []));
        }
    }

    private sealed class ControlledTransaction(
        ReferencePresetIntake intake,
        Sha256Hash authoringHash) :
        IReferencePresetAuthoringTransaction
    {
        public bool CancelDesign { get; init; }
        public ReferencePresetDesignProposalRequest?
            DesignRequest { get; private set; }
        public ReferencePresetWriteRequest?
            WriteRequest { get; private set; }
        public ReferencePresetNpcBuildRequest?
            NpcRequest { get; private set; }

        public ValueTask<ReferencePresetDesignProposalResult>
            ProposeDesignAsync(
                ReferencePresetDesignProposalRequest request,
                IProgress<ReferencePresetProgress>? progress,
                CancellationToken cancellationToken)
        {
            DesignRequest = request;
            if (CancelDesign)
                throw new OperationCanceledException();
            return ValueTask.FromResult(
                new ReferencePresetDesignProposalResult(
                    true,
                    new LandmarkInferenceProposal(
                        1,
                        ReferencePresetAuthorityKind
                            .InferenceProposal,
                        request.ExpectedIntakeSha256,
                        Hash('9'),
                        [],
                        [],
                        []),
                    Hash('2'),
                    null,
                    null,
                    []));
        }

        public ValueTask<ReferencePresetWriteResult>
            WritePresetAsync(
                ReferencePresetWriteRequest request,
                IProgress<ReferencePresetProgress>? progress,
                CancellationToken cancellationToken)
        {
            WriteRequest = request;
            return ValueTask.FromResult(
                new ReferencePresetWriteResult(
                    true,
                    null,
                    authoringHash,
                    null,
                    []));
        }

        public ValueTask<ReferencePresetNpcBuildResult>
            WritePresetAndBuildNpcAsync(
                ReferencePresetNpcBuildRequest request,
                IProgress<ReferencePresetProgress>? progress,
                CancellationToken cancellationToken)
        {
            NpcRequest = request;
            WorkspacePath preset = new(
                Path.Combine(
                    request.Authoring.OutputRoot.Value,
                    "preset",
                    "Controlled Reference.jslot"));
            var handoff = new VerifiedReferenceNpcHandoff(
                1,
                ReferencePresetAuthorityKind
                    .VerifiedNpcHandoff,
                intake.ProjectId,
                Hash('7'),
                preset,
                Hash('8'),
                intake.Race,
                intake.Sex,
                intake.Weight,
                []);
            return ValueTask.FromResult(
                new ReferencePresetNpcBuildResult(
                    true,
                    handoff,
                    new RaceMenuJslotNpcBuildResult(
                        true,
                        null,
                        null,
                        null,
                        null,
                        null,
                        []),
                    []));
        }
    }

    private sealed class ControlledExecutionRequestLoader(
        RaceMenuNpcExecutionRequest request) :
        IRaceMenuNpcExecutionRequestFileLoader
    {
        public ValueTask<
            RaceMenuNpcExecutionRequestFileLoadResult> LoadAsync(
                RaceMenuNpcExecutionRequestFileLoadRequest load,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new RaceMenuNpcExecutionRequestFileLoadResult(
                    RaceMenuNpcExecutionRequestFileLoadStatus
                        .Loaded,
                    load.RequestFile,
                    load.ExpectedSha256,
                    load.ExpectedSha256,
                    100,
                    request,
                    []));
        }
    }

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private static void Require(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
