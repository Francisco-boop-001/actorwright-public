using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static class SkyrimFollowerFinishCliTests
{
    private static readonly Sha256Hash RequestHash = Hash('1');
    private static readonly Sha256Hash ProposalHash = Hash('2');

    public static Task TestCatalogAndParsing()
    {
        string[] names =
        [
            "npc follower-finish analyze",
            "npc follower-finish apply",
            "npc follower-finish verify"
        ];
        foreach (string name in names)
        {
            CommandDescriptor[] matches = CommandCatalog.All
                .Where(item => item.Name == name)
                .ToArray();
            Require(matches.Length == 1,
                $"catalog must contain exactly one '{name}' descriptor");
            Require(matches[0].SchemaVersion == "1" &&
                    matches[0].SupportedGames.SequenceEqual(
                        [GameEdition.SkyrimSpecialEdition]) &&
                    matches[0].Limitations.Any(item =>
                        item.Contains(
                            "runtime",
                            StringComparison.OrdinalIgnoreCase)),
                $"catalog descriptor '{name}' lost its bounded runtime-required contract");

            ParsedCommand parsed = CommandLine.Parse(
            [
                .. name.Split(' '),
                "--request", @"K:\inputs\request.json",
                "--request-sha256", RequestHash.Value,
                "--json"
            ]);
            Require(parsed.Name == name && parsed.Json,
                $"three-word route '{name}' was not parsed exactly");
        }

        return Task.CompletedTask;
    }

    public static async Task TestHandlerMapsAllModes()
    {
        (SkyrimFollowerFinishRequest request,
            SkyrimFollowerFinishProposal proposal) = Fixture();
        var loader = new ControlledLoader(request, proposal);
        var service = new ControlledService(proposal);
        var output = new StringWriter();
        var error = new StringWriter();
        var handler = new SkyrimFollowerFinishCommandHandler(
            service, loader, output, error);

        CommandExitCode analyze = await handler.RunAsync(
            CommandLine.Parse(
            [
                "npc", "follower-finish", "analyze",
                "--request", @"K:\inputs\request.json",
                "--request-sha256", RequestHash.Value,
                "--proposal", @"K:\outputs\proposal.json",
                "--json"
            ]),
            CancellationToken.None);
        using (JsonDocument json =
               JsonDocument.Parse(output.ToString()))
        {
            Require(analyze == CommandExitCode.Success &&
                    service.AnalyzeRequest == request &&
                    service.AnalyzeRequestHash == RequestHash &&
                    service.AnalyzeOutput?.Value ==
                        @"K:\outputs\proposal.json" &&
                    service.ApplyRequest is null &&
                    json.RootElement.GetProperty("schemaVersion")
                        .GetInt32() == 1 &&
                    json.RootElement.GetProperty("command")
                        .GetString() ==
                        "npc follower-finish analyze" &&
                    json.RootElement.GetProperty("runtimeAuthority")
                        .ValueKind == JsonValueKind.False,
                "analyze did not remain read-only or emit the schema-1 static verdict");
        }

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        CommandExitCode apply = await handler.RunAsync(
            CommandLine.Parse(
            [
                "npc", "follower-finish", "apply",
                "--request", @"K:\inputs\request.json",
                "--request-sha256", RequestHash.Value,
                "--proposal", @"K:\inputs\proposal.json",
                "--proposal-sha256", ProposalHash.Value,
                "--json"
            ]),
            CancellationToken.None);
        using (JsonDocument json =
               JsonDocument.Parse(output.ToString()))
        {
            Require(apply == CommandExitCode.Success &&
                    service.ApplyRequest == request &&
                    service.ApplyRequestHash == RequestHash &&
                    service.ApplyProposal?.RequestSha256 ==
                        proposal.RequestSha256 &&
                    service.ApplyProposal.Request
                        .OccupiedLocalFormIds.SequenceEqual(
                            proposal.Request
                                .OccupiedLocalFormIds) &&
                    service.ApplyProposalHash == ProposalHash &&
                    json.RootElement.GetProperty("verdict")
                        .GetString() ==
                        "STATIC_PASS_RUNTIME_REQUIRED" &&
                    json.RootElement.GetProperty("runtimeAuthority")
                        .ValueKind == JsonValueKind.False,
                "apply did not preserve the loaded request/proposal binding or static verdict");
        }

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        CommandExitCode verify = await handler.RunAsync(
            CommandLine.Parse(
            [
                "npc", "follower-finish", "verify",
                "--request", @"K:\inputs\request.json",
                "--request-sha256", RequestHash.Value,
                "--proposal", @"K:\inputs\proposal.json",
                "--proposal-sha256", ProposalHash.Value,
                "--manifest", @"K:\outputs\npcmanager-package.json",
                "--json"
            ]),
            CancellationToken.None);
        using (JsonDocument json =
               JsonDocument.Parse(output.ToString()))
        {
            Require(verify == CommandExitCode.Success &&
                    service.VerifyRequest == request &&
                    service.VerifyRequestHash == RequestHash &&
                    service.VerifyProposal?.RequestSha256 ==
                        proposal.RequestSha256 &&
                    service.VerifyProposal.Request
                        .OccupiedLocalFormIds.SequenceEqual(
                            proposal.Request
                                .OccupiedLocalFormIds) &&
                    service.VerifyManifest?.Value ==
                        @"K:\outputs\npcmanager-package.json" &&
                    json.RootElement.GetProperty("verified")
                        .GetBoolean() &&
                    json.RootElement.GetProperty("runtimeAuthority")
                        .ValueKind == JsonValueKind.False,
                "verify did not remain read-only or report the typed verification result");
        }
        Require(error.ToString().Length == 0,
            $"successful follower-finish modes wrote errors: {error}");
    }

    public static async Task TestHandlerRefusalsAndCancellation()
    {
        (SkyrimFollowerFinishRequest request,
            SkyrimFollowerFinishProposal proposal) = Fixture();
        var output = new StringWriter();
        var error = new StringWriter();
        var service = new ControlledService(proposal);
        var loader = new ControlledLoader(request, proposal);
        var handler = new SkyrimFollowerFinishCommandHandler(
            service, loader, output, error);

        CommandExitCode unknown = await handler.RunAsync(
            CommandLine.Parse(
            [
                "npc", "follower-finish", "analyze",
                "--request", @"K:\inputs\request.json",
                "--request-sha256", RequestHash.Value,
                "--proposal", @"K:\outputs\proposal.json",
                "--surprise", "true",
                "--json"
            ]),
            CancellationToken.None);
        Require(unknown == CommandExitCode.UsageError &&
                service.AnalyzeRequest is null &&
                error.ToString().Contains(
                    "unknown-option",
                    StringComparison.Ordinal),
            "unknown options reached the follower-finish service");

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        CommandExitCode missing = await handler.RunAsync(
            CommandLine.Parse(
            [
                "npc", "follower-finish", "apply",
                "--request", @"K:\inputs\request.json",
                "--request-sha256", RequestHash.Value,
                "--json"
            ]),
            CancellationToken.None);
        Require(missing == CommandExitCode.UsageError &&
                service.ApplyRequest is null &&
                error.ToString().Contains(
                    "usage-error",
                    StringComparison.Ordinal),
            "missing proposal/hash options reached apply");

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        loader.RefuseSecurity = true;
        CommandExitCode unsafeInput = await handler.RunAsync(
            CommandLine.Parse(
            [
                "npc", "follower-finish", "analyze",
                "--request", @"K:\inputs\request.json",
                "--request-sha256", RequestHash.Value,
                "--proposal", @"K:\outputs\proposal.json",
                "--json"
            ]),
            CancellationToken.None);
        Require(unsafeInput == CommandExitCode.SecurityRefusal &&
                error.ToString().Contains(
                    "follower-finish-request-security-refused",
                    StringComparison.Ordinal),
            "a loader security refusal did not retain its security exit");

        loader.RefuseSecurity = false;
        service.CancelAnalyze = true;
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        CommandExitCode cancelled = await handler.RunAsync(
            CommandLine.Parse(
            [
                "npc", "follower-finish", "analyze",
                "--request", @"K:\inputs\request.json",
                "--request-sha256", RequestHash.Value,
                "--proposal", @"K:\outputs\proposal.json",
                "--json"
            ]),
            CancellationToken.None);
        Require(cancelled == CommandExitCode.Cancelled &&
                error.ToString().Contains(
                    "\"code\": \"cancelled\"",
                    StringComparison.Ordinal),
            "follower-finish cancellation did not map to the stable cancelled exit");
    }

    public static async Task TestRunnerDispatchAndUnavailableService()
    {
        (SkyrimFollowerFinishRequest request,
            SkyrimFollowerFinishProposal proposal) = Fixture();
        var service = new ControlledService(proposal);
        var loader = new ControlledLoader(request, proposal);
        (CliRunner runner, StringWriter output,
            StringWriter error) =
            Program.CreateRunnerForFollowerFinishTests(
                service, loader);
        CommandExitCode dispatched = await runner.RunAsync(
            CommandLine.Parse(
            [
                "npc", "follower-finish", "analyze",
                "--request", @"K:\inputs\request.json",
                "--request-sha256", RequestHash.Value,
                "--proposal", @"K:\outputs\proposal.json",
                "--json"
            ]),
            CancellationToken.None);
        Require(dispatched == CommandExitCode.Success &&
                service.AnalyzeRequest == request &&
                output.ToString().Contains(
                    "\"command\": \"npc follower-finish analyze\"",
                    StringComparison.Ordinal) &&
                error.ToString().Length == 0,
            "CliRunner did not dispatch follower-finish through its typed service bundle");

        (CliRunner unavailable, _, StringWriter unavailableError) =
            Program.CreateRunnerForFollowerFinishTests(
                null, null);
        CommandExitCode unavailableExit =
            await unavailable.RunAsync(
                CommandLine.Parse(
                [
                    "npc", "follower-finish", "analyze",
                    "--request", @"K:\inputs\request.json",
                    "--request-sha256", RequestHash.Value,
                    "--proposal", @"K:\outputs\proposal.json",
                    "--json"
                ]),
                CancellationToken.None);
        Require(unavailableExit == CommandExitCode.UsageError &&
                unavailableError.ToString().Contains(
                    "unavailable",
                    StringComparison.OrdinalIgnoreCase),
            "an uncomposed follower-finish service did not fail closed");
    }

    private static (
        SkyrimFollowerFinishRequest Request,
        SkyrimFollowerFinishProposal Proposal) Fixture()
    {
        var plugin = new PluginName("FixtureFollower.esp");
        var factions = ImmutableArray.Create(
            new NpcFactionEntry(
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x5C84D)),
                0),
            new NpcFactionEntry(
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x5C84E)),
                -1));
        ImmutableArray<string> existingChanges =
        [
            "TES4: set ESL flag and mechanical header metadata",
            "CLFM 0x00000801: old -> new",
            "NPC_ 0x00000800: add PKID 0x00000805"
        ];
        ImmutableArray<string> newRecords =
        [
            "PACK 0x00000805",
            "REFR 0x00000806",
            "ACHR 0x00000807"
        ];
        ImmutableArray<AssetPath> files =
        [
            new("Data/FixtureFollower.esp"),
            new("npcmanager-package.json")
        ];
        var request = new SkyrimFollowerFinishRequest(
            1,
            SkyrimFollowerFinishRequest.OperationName,
            new SkyrimFollowerFinishSourceAuthority(
                new WorkspacePath(@"K:\inputs\source.zip"),
                100,
                Hash('a'),
                new WorkspacePath(
                    @"K:\inputs\source-manifest.json"),
                Hash('b'),
                plugin,
                Hash('c'),
                Hash('d'),
                Hash('e')),
            new EditorId("FixtureFollower"),
            new FormId(0x800),
            [
                new FormId(0x800),
                new FormId(0x801),
                new FormId(0x802),
                new FormId(0x803),
                new FormId(0x804)
            ],
            new FormReference(
                new PluginName("FixtureRace.esp"),
                new FormId(0x800)),
            "fixture-route",
            true,
            factions,
            new FormId(0x804),
            "Ally",
            1,
            new SkyrimFollowerFinishHairChange(
                new FormId(0x801),
                new SkyrimPackedRgb(0x102030),
                new SkyrimPackedRgb(0xD0C090)),
            true,
            false,
            new SkyrimFollowerFinishSandbox(
                "bounded-exterior-sandbox",
                512,
                "continuous",
                new FormReference(plugin, new FormId(0x806)),
                "GetFactionRank(Skyrim.esm|0x0005C84E) < 0"),
            new SkyrimFollowerFinishPlacement(
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x3C)),
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0xA16A)),
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x3B)),
                new SkyrimExteriorTransform(1, 2, 3, 0, 0, 0),
                new SkyrimExteriorTransform(4, 5, 6, 0, 0, 0)),
            SkyrimFollowerFinishAllocation.SimpleFollowerV1,
            newRecords,
            existingChanges,
            files,
            new WorkspacePath(@"K:\outputs\candidate"),
            new WorkspacePath(@"K:\outputs\candidate.zip"),
            "Fixture follower finish");
        var snapshot = new SkyrimFollowerFinishPluginSnapshot(
            true,
            plugin,
            Hash('c'),
            0,
            [],
            new FormId(0x805),
            [
                "NPC_ 0x00000800",
                "CLFM 0x00000801"
            ],
            ["FULL:fixture"],
            new SkyrimPackedRgb(0x102030),
            new FormReference(plugin, new FormId(0x801)),
            true,
            factions,
            "Ally",
            1,
            ["LAND"],
            []);
        var proposal = new SkyrimFollowerFinishProposal(
            1,
            SkyrimFollowerFinishRequest.OperationName,
            RequestHash,
            request,
            snapshot,
            existingChanges,
            newRecords,
            new FormId(0x808),
            ["TES4", "GRUP:NPC_"],
            files,
            false);
        return (request, proposal);
    }

    private sealed class ControlledLoader(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishProposal proposal) :
        ISkyrimFollowerFinishRequestFileLoader
    {
        public bool RefuseSecurity { get; set; }

        public ValueTask<SkyrimFollowerFinishRequestLoadResult>
            LoadRequestAsync(
                WorkspacePath path,
                Sha256Hash expectedSha256,
                CancellationToken cancellationToken,
                SkyrimFollowerFinishDocumentLoadMode mode =
                    SkyrimFollowerFinishDocumentLoadMode.PreWrite)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RefuseSecurity)
            {
                return ValueTask.FromResult(
                    new SkyrimFollowerFinishRequestLoadResult(
                        false,
                        path,
                        expectedSha256,
                        null,
                        null,
                        null,
                        [
                            new Diagnostic(
                                "follower-finish-request-security-refused",
                                DiagnosticSeverity.Error,
                                "Refused controlled reparse input.")
                        ]));
            }
            return ValueTask.FromResult(
                new SkyrimFollowerFinishRequestLoadResult(
                    true,
                    path,
                    expectedSha256,
                    expectedSha256,
                    100,
                    request,
                    []));
        }

        public ValueTask<SkyrimFollowerFinishProposalLoadResult>
            LoadProposalAsync(
                WorkspacePath path,
                Sha256Hash expectedSha256,
                CancellationToken cancellationToken,
                SkyrimFollowerFinishDocumentLoadMode mode =
                    SkyrimFollowerFinishDocumentLoadMode.PreWrite)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new SkyrimFollowerFinishProposalLoadResult(
                    true,
                    path,
                    expectedSha256,
                    expectedSha256,
                    100,
                    proposal with
                    {
                        Request = proposal.Request with
                        {
                            OccupiedLocalFormIds =
                            [
                                .. proposal.Request
                                    .OccupiedLocalFormIds
                            ]
                        }
                    },
                    []));
        }
    }

    private sealed class ControlledService(
        SkyrimFollowerFinishProposal proposal) :
        ISkyrimFollowerFinishService
    {
        public bool CancelAnalyze { get; set; }
        public SkyrimFollowerFinishRequest? AnalyzeRequest { get; private set; }
        public Sha256Hash? AnalyzeRequestHash { get; private set; }
        public WorkspacePath? AnalyzeOutput { get; private set; }
        public SkyrimFollowerFinishRequest? ApplyRequest { get; private set; }
        public Sha256Hash? ApplyRequestHash { get; private set; }
        public SkyrimFollowerFinishProposal? ApplyProposal { get; private set; }
        public Sha256Hash? ApplyProposalHash { get; private set; }
        public SkyrimFollowerFinishRequest? VerifyRequest { get; private set; }
        public Sha256Hash? VerifyRequestHash { get; private set; }
        public SkyrimFollowerFinishProposal? VerifyProposal { get; private set; }
        public WorkspacePath? VerifyManifest { get; private set; }

        public ValueTask<SkyrimFollowerFinishProposalResult>
            AnalyzeAsync(
                SkyrimFollowerFinishRequest request,
                Sha256Hash verifiedRequestFileSha256,
                WorkspacePath proposalOutput,
                CancellationToken cancellationToken)
        {
            AnalyzeRequest = request;
            AnalyzeRequestHash = verifiedRequestFileSha256;
            AnalyzeOutput = proposalOutput;
            if (CancelAnalyze)
                throw new OperationCanceledException();
            return ValueTask.FromResult(
                new SkyrimFollowerFinishProposalResult(
                    true,
                    proposal,
                    proposalOutput,
                    ProposalHash,
                    []));
        }

        public ValueTask<SkyrimFollowerFinishResult> ApplyAsync(
            SkyrimFollowerFinishRequest request,
            Sha256Hash verifiedRequestFileSha256,
            SkyrimFollowerFinishProposal loadedProposal,
            Sha256Hash expectedProposalSha256,
            IProgress<SkyrimFollowerFinishProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyRequest = request;
            ApplyRequestHash = verifiedRequestFileSha256;
            ApplyProposal = loadedProposal;
            ApplyProposalHash = expectedProposalSha256;
            return ValueTask.FromResult(
                new SkyrimFollowerFinishResult(
                    true,
                    null,
                    null,
                    null,
                    false,
                    []));
        }

        public ValueTask<SkyrimFollowerFinishVerificationResult>
            VerifyAsync(
                SkyrimFollowerFinishRequest request,
                Sha256Hash verifiedRequestFileSha256,
                SkyrimFollowerFinishProposal loadedProposal,
                Sha256Hash expectedProposalSha256,
                WorkspacePath manifest,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyRequest = request;
            VerifyRequestHash = verifiedRequestFileSha256;
            VerifyProposal = loadedProposal;
            VerifyManifest = manifest;
            return ValueTask.FromResult(
                new SkyrimFollowerFinishVerificationResult(
                    true,
                    null,
                    null,
                    []));
        }
    }

    private static Sha256Hash Hash(char value) =>
        new(new string(value, 64));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
