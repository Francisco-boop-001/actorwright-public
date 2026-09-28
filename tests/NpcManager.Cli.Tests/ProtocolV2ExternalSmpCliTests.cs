using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal sealed class Preview254ExternalSmpProtocolCliScenario :
    IPreview254ExternalSmpCliScenario
{
    public string Selector => "--test-protocol-v2-external-smp";

    public ValueTask RunAsync(CancellationToken cancellationToken) =>
        ProtocolV2ExternalSmpCliTests.RunAsync(cancellationToken);
}

internal static class ProtocolV2ExternalSmpCliTests
{
    public static async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ProtocolV2NpcCreateBuildTests
            .RunExternalPublicationPathAsync();
        string rootValue = Path.Combine(
            Path.GetTempPath(),
            "actorwright-protocol-v2-external-smp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootValue);
        try
        {
            var root = new WorkspacePath(rootValue);
            var assets = new RaceMenuNpcStandaloneAssets(
                "external-smp",
                null!,
                null!,
                1,
                1,
                [],
                null,
                [],
                null)
            {
                SchemaVersion = 8
            };
            var typedExecution = new RaceMenuNpcExecutionResult(
                false,
                null,
                assets,
                null,
                null,
                null,
                null,
                [new Diagnostic(
                    ExternalHeadPartDiagnosticCodes.ProviderDisabled,
                    DiagnosticSeverity.Error,
                    "provider disabled")]);
            var expected = new RaceMenuJslotNpcBuildResult(
                false,
                null,
                null,
                null,
                null,
                typedExecution,
                [new Diagnostic(
                    ExternalHeadPartDiagnosticCodes.ProviderDisabled,
                    DiagnosticSeverity.Error,
                    "provider disabled")]);
            var service = new RecordingJslotBuildService(expected);
            var bridge = new RaceMenuJslotNpcBuildCommandExecutionBridge(service);
            var request = new RaceMenuJslotNpcBuildRequest(
                null!,
                new WorkspacePath(Path.Combine(root.Value, "preset.jslot")),
                new Sha256Hash(new string('A', 64)),
                new WorkspacePath(Path.Combine(root.Value, "Data")),
                [new PluginName("Skyrim.esm")],
                new WorkspacePath(Path.Combine(root.Value, "companion")));

            RaceMenuJslotNpcBuildCommandExecution execution =
                await bridge.ExecuteAsync(
                    CommandLine.Parse(["npc", "create-from-jslot", "--json"]),
                    request,
                    cancellationToken);

            Require(ReferenceEquals(expected, execution.TypedResult),
                "The typed bridge did not retain the service result in memory.");
            Require(execution.TypedExternalSmp,
                "Schema-8 execution did not stay on the typed external-SMP path.");
            Require(execution.ExitCode != CommandExitCode.Success,
                "A refused typed external-SMP result was projected as success.");
            Require(service.LastRequest is not null,
                "The typed bridge did not dispatch the typed JSlot request.");
            AssertCanonicalProjection();
        }
        finally
        {
            if (Directory.Exists(rootValue))
                Directory.Delete(rootValue, recursive: true);
        }
    }

    private static void AssertCanonicalProjection()
    {
        var hash = new Sha256Hash(new string('a', 64));
        var provider = new ExternalHeadPartInstallProviderObservation(
            new PluginName("HairPack.esp"),
            hash,
            hash,
            true);
        var verification = new ExternalHeadPartInstallVerificationArtifact(
            ExternalHeadPartSchemaIdentifiers.InstallVerification,
            true,
            true,
            null,
            [hash],
            null,
            ExternalInstallDependencyState.Verified,
            true,
            true,
            false,
            false,
            [provider],
            []);
        var artifact = new RaceMenuNpcExternalInstallPrepublicationArtifact(
            hash,
            hash,
            [new RaceMenuNpcExternalInstallPrepublicationBinding(hash, hash)],
            verification);

        var json = ExternalInstallPrepublicationProtocolProjection.Serialize(
            artifact);
        Require(json.GetProperty("packageManifestSha256").GetString() ==
                hash.Value,
            "The protocol projection changed the canonical lowercase hash.");
        Require(json.GetProperty("bindings")[0]
                    .GetProperty("descriptorId").GetString() == hash.Value,
            "The protocol projection wrapped a descriptor hash value.");
        JsonElement nested = json.GetProperty("verification");
        Require(nested.GetProperty("currentInstallDependencyState")
                    .GetString() == "verified" &&
                nested.GetProperty("providerObservations")[0]
                    .GetProperty("providerPlugin").GetString() ==
                    "HairPack.esp",
            "The protocol projection drifted canonical state/plugin tokens.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RecordingJslotBuildService(
        RaceMenuJslotNpcBuildResult result) : IRaceMenuJslotNpcBuildService
    {
        public RaceMenuJslotNpcBuildRequest? LastRequest { get; private set; }

        public ValueTask<RaceMenuJslotNpcBuildResult> ExecuteAsync(
            RaceMenuJslotNpcBuildRequest request,
            IProgress<BlankNpcBuildProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(result);
        }
    }
}
