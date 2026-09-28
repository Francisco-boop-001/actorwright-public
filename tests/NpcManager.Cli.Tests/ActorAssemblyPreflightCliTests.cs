using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static partial class Program
{
    private static async Task TestActorAssemblyPreflightCli()
    {
        var fake = new FakeActorAssemblyPreflightService();
        var (runner, output, error) = CreateRunner(actorAssemblyPreflightService: fake);
        var code = await runner.RunAsync(CommandLine.Parse(["npc", "assembly", "preflight", "--contract", "K:\\ExampleWorkspace\\contract.json", "--contract-sha256", new string('A', 64), "--json"]), CancellationToken.None);
        Assert(code == CommandExitCode.ValidationFailure, "Actor Assembly blocked result did not map to exit 4.");
        Assert(error.ToString().Length == 0 && output.ToString().Contains("actor-assembly-preflight-error", StringComparison.Ordinal), "Actor Assembly error envelope was not emitted on stdout.");
        var usage = await runner.RunAsync(CommandLine.Parse(["npc", "assembly", "preflight", "--json"]), CancellationToken.None);
        Assert(usage == CommandExitCode.UsageError && error.ToString().Contains("usage-error", StringComparison.Ordinal), "Actor Assembly missing options did not use the existing usage envelope.");
    }

    private sealed class FakeActorAssemblyPreflightService : IActorAssemblyPreflightService
    {
        public ValueTask<ActorAssemblyPreflightExecutionResult> PreflightAsync(ActorAssemblyPreflightRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ActorAssemblyPreflightExecutionResult(true, false, null,
                new ActorAssemblyPreflightErrorArtifact(1, "actor-assembly-preflight-error", true,
                    ImmutableArray.Create(new Diagnostic("fixture-blocked", DiagnosticSeverity.Error, "fixture")))));
    }
}
