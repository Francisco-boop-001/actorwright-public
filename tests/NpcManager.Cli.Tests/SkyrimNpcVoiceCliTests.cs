using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class SkyrimNpcVoiceCliTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    public static async Task RunAsync()
    {
        var service = new RecordingVoiceService();
        (CliRunner runner, StringWriter output, _) = Program.CreateRunner(skyrimNpcVoiceService: service);

        CommandExitCode discover = await runner.RunAsync(CommandLine.Parse([
            "npc", "voice", "discover", "--endpoint", "http://127.0.0.1:8020", "--timeout-seconds", "7", "--output", "K:\\Actorwright\\artifacts\\voice-fixtures\\voice-services.json", "--json"]), CancellationToken.None);
        Check(discover == CommandExitCode.Success && service.Discovery == ("http://127.0.0.1:8020", 7, "K:\\Actorwright\\artifacts\\voice-fixtures\\voice-services.json"), "Voice discovery options were not bound to the typed service.");
        using (JsonDocument discoverJson = JsonDocument.Parse(output.ToString()))
            Check(discoverJson.RootElement.GetProperty("requestTextLoggedByService").GetBoolean(), "Voice discovery JSON omitted the service request-text logging disclosure.");
        JsonElement serviceSchema = ProtocolV2SchemaService.RenderInline("npc voice discover")
            .GetProperty("documentSchemas").EnumerateArray().Single().GetProperty("jsonSchema");
        Check(serviceSchema.GetProperty("required").EnumerateArray().Any(item => item.GetString() == "requestTextLoggedByService") &&
              serviceSchema.GetProperty("properties").GetProperty("requestTextLoggedByService").GetProperty("const").GetBoolean(),
            "The voice service inventory schema did not require requestTextLoggedByService=true.");
        foreach (string invalidTimeoutToken in new[] { "banana", "31" })
        {
            CommandExitCode invalidTimeout = await runner.RunAsync(CommandLine.Parse([
                "npc", "voice", "discover", "--timeout-seconds", invalidTimeoutToken, "--json"]), CancellationToken.None);
            Check(invalidTimeout == CommandExitCode.UsageError, $"Invalid discovery timeout '{invalidTimeoutToken}' silently used the default.");
        }

        CommandExitCode import = await runner.RunAsync(CommandLine.Parse([
            "npc", "voice", "import", "--sample", "K:\\Actorwright\\artifacts\\voice-fixtures\\sample.wav", "--plugin", "Fixture.esp", "--form-id", "0x800", "--editor-id", "FixtureNpc", "--voice-prefix", "Fixture", "--output", "K:\\Actorwright\\artifacts\\voice-fixtures\\voice", "--json"]), CancellationToken.None);
        Check(import == CommandExitCode.Success && service.Import is { Plugin.Value: "Fixture.esp", FormId.Value: 0x800, VoicePrefix: "Fixture" }, "Voice import options were not bound to the typed request.");

        CommandExitCode synthesize = await runner.RunAsync(CommandLine.Parse([
            "npc", "voice", "synthesize", "--manifest", "K:\\Actorwright\\artifacts\\voice-fixtures\\dialogue.json", "--manifest-sha256", Hash, "--sample-authority", "K:\\Actorwright\\artifacts\\voice-fixtures\\sample.json", "--sample-authority-sha256", Hash,
            "--output", "K:\\Actorwright\\artifacts\\voice-fixtures\\synthesis", "--endpoint", "http://127.0.0.1:8020", "--language", "en", "--max-lines", "3", "--resume", "--json"]), CancellationToken.None);
        Check(synthesize == CommandExitCode.Success && service.Synthesis is { MaxLines: 3, Resume: true, Language: "en" }, "Voice synthesis options were not bound to the typed request.");
        CommandExitCode invalidResume = await runner.RunAsync(CommandLine.Parse([
            "npc", "voice", "synthesize", "--manifest", "K:\\Actorwright\\artifacts\\voice-fixtures\\dialogue.json", "--manifest-sha256", Hash, "--sample-authority", "K:\\Actorwright\\artifacts\\voice-fixtures\\sample.json", "--sample-authority-sha256", Hash,
            "--output", "K:\\Actorwright\\artifacts\\voice-fixtures\\synthesis", "--resume", "banana", "--json"]), CancellationToken.None);
        Check(invalidResume == CommandExitCode.UsageError, "An invalid --resume token silently disabled resume.");

        CommandExitCode missingHash = await runner.RunAsync(CommandLine.Parse(["npc", "voice", "synthesize", "--manifest", "x", "--json"]), CancellationToken.None);
        Check(missingHash == CommandExitCode.UsageError, "Incomplete synthesis input was not rejected as usage.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class RecordingVoiceService : ISkyrimNpcVoiceService
    {
        public (string?, int, string?) Discovery { get; private set; }
        public SkyrimVoiceSampleImportRequest? Import { get; private set; }
        public SkyrimVoiceSynthesisRequest? Synthesis { get; private set; }

        public ValueTask<SkyrimVoiceServiceInventory> DiscoverAsync(string? endpointOverride, int timeoutSeconds, WorkspacePath? outputPath, CancellationToken cancellationToken)
        {
            Discovery = (endpointOverride, timeoutSeconds, outputPath?.Value);
            return ValueTask.FromResult(new SkyrimVoiceServiceInventory(SkyrimNpcVoiceSchemas.Services, endpointOverride, ImmutableArray<SkyrimVoiceServiceDescriptor>.Empty, ImmutableArray<Diagnostic>.Empty, "2026-09-07T00:00:00Z"));
        }

        public ValueTask<SkyrimVoiceSampleImportResult> ImportSampleAsync(SkyrimVoiceSampleImportRequest request, CancellationToken cancellationToken)
        {
            Import = request;
            return ValueTask.FromResult(new SkyrimVoiceSampleImportResult(true, null, null, null, ImmutableArray<Diagnostic>.Empty));
        }

        public ValueTask<SkyrimVoiceSynthesisResult> SynthesizeAsync(SkyrimVoiceSynthesisRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            Synthesis = request;
            return ValueTask.FromResult(new SkyrimVoiceSynthesisResult(true, 0, 0, 0, 0, null, null, ImmutableArray<Diagnostic>.Empty));
        }
    }
}
