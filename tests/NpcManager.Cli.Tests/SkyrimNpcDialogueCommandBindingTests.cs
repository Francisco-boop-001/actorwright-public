using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static class SkyrimNpcDialogueCommandBindingTests
{
    private const string Hash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    public static async Task RunAsync()
    {
        var service = new RecordingDialogueService();
        (CliRunner runner, _, _) = Program.CreateRunner(skyrimNpcDialogueService: service);

        CommandExitCode template = await runner.RunAsync(CommandLine.Parse([
            "npc", "dialogue", "analyze", "--template", "vanilla-safe", "--profile", "K:\\Actorwright\\artifacts\\voice-fixtures\\profile.json", "--manifest-output", "K:\\Actorwright\\artifacts\\voice-fixtures\\dialogue.json",
            "--npc-plugin", "Fixture.esp", "--form-id", "0x800", "--editor-id", "FixtureNpc", "--voice-prefix", "Fixture", "--female", "--language", "en", "--json"]), CancellationToken.None);
        Check(template == CommandExitCode.Success && service.Template is { Template: "vanilla-safe", Npc.FormId.Value: 0x800, Npc.Female: true }, "Dialogue template options were not bound.");
        CommandExitCode invalidFemale = await runner.RunAsync(CommandLine.Parse([
            "npc", "dialogue", "analyze", "--template", "vanilla-safe", "--profile", "K:\\Actorwright\\artifacts\\voice-fixtures\\profile.json", "--manifest-output", "K:\\Actorwright\\artifacts\\voice-fixtures\\dialogue.json",
            "--npc-plugin", "Fixture.esp", "--form-id", "0x800", "--voice-prefix", "Fixture", "--female", "banana", "--json"]), CancellationToken.None);
        Check(invalidFemale == CommandExitCode.UsageError, "An invalid --female token silently authored a male NPC.");

        string[] analyzeArguments = [
            "npc", "dialogue", "analyze", "--manifest", "K:\\Actorwright\\artifacts\\voice-fixtures\\dialogue.json", "--manifest-sha256", Hash, "--plugin", "K:\\Actorwright\\artifacts\\voice-fixtures\\Fixture.esp", "--plugin-sha256", Hash,
            "--data-root", "K:\\Actorwright\\artifacts\\voice-fixtures\\Data", "--plugins", "Skyrim.esm,Update.esm", "--sample-authority", "K:\\Actorwright\\artifacts\\voice-fixtures\\sample.json", "--sample-authority-sha256", Hash, "--output", "K:\\Actorwright\\artifacts\\voice-fixtures\\proposal.json", "--json"];
        CommandExitCode analyze = await runner.RunAsync(CommandLine.Parse(analyzeArguments), CancellationToken.None);
        Check(analyze == CommandExitCode.Success && service.Analyze is { LoadOrder.Length: 2 }, "Dialogue analyze bindings lost the reviewed load order.");
        foreach (string option in new[] { "--editor-id", "--language" })
        {
            CommandExitCode ignoredOption = await runner.RunAsync(CommandLine.Parse([.. analyzeArguments, option, "ignored"]), CancellationToken.None);
            Check(ignoredOption == CommandExitCode.UsageError, "Normal analyze silently ignored template-only " + option);
        }

        CommandExitCode apply = await runner.RunAsync(CommandLine.Parse([
            "npc", "dialogue", "apply", "--proposal", "K:\\Actorwright\\artifacts\\voice-fixtures\\proposal.json", "--proposal-sha256", Hash, "--synthesis", "K:\\Actorwright\\artifacts\\voice-fixtures\\synthesis.json", "--synthesis-sha256", Hash,
            "--output", "K:\\Actorwright\\artifacts\\voice-fixtures\\package", "--lip-tools", "K:\\Actorwright\\artifacts\\voice-fixtures\\tools", "--json"]), CancellationToken.None);
        Check(apply == CommandExitCode.Success && service.Apply is { LipTools.Value: "K:\\Actorwright\\artifacts\\voice-fixtures\\tools" }, "Dialogue apply bindings lost the lip-tool root.");

        CommandExitCode verify = await runner.RunAsync(CommandLine.Parse(["npc", "dialogue", "verify", "--manifest", "K:\\Actorwright\\artifacts\\voice-fixtures\\output.json", "--manifest-sha256", Hash, "--json"]), CancellationToken.None);
        Check(verify == CommandExitCode.Success && string.Equals(service.Verify?.ManifestSha256.Value, Hash, StringComparison.OrdinalIgnoreCase), "Dialogue verify hash binding was not forwarded.");

        CommandExitCode mixedModes = await runner.RunAsync(CommandLine.Parse([
            "npc", "dialogue", "analyze", "--template", "vanilla-safe", "--profile", "p", "--manifest-output", "m", "--npc-plugin", "Fixture.esp", "--form-id", "0x800", "--voice-prefix", "Fixture", "--female", "--manifest", "other", "--json"]), CancellationToken.None);
        Check(mixedModes == CommandExitCode.UsageError, "Mixed template and normal analyze modes were not refused.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class RecordingDialogueService : ISkyrimNpcDialogueService
    {
        public SkyrimDialogueTemplateRequest? Template { get; private set; }
        public SkyrimDialogueAnalyzeRequest? Analyze { get; private set; }
        public SkyrimDialogueApplyRequest? Apply { get; private set; }
        public SkyrimDialogueVerifyRequest? Verify { get; private set; }
        public ValueTask<SkyrimDialogueStageResult<SkyrimDialogueManifest>> CreateTemplateAsync(SkyrimDialogueTemplateRequest request, CancellationToken cancellationToken) { Template = request; return ValueTask.FromResult(Ok<SkyrimDialogueManifest>()); }
        public ValueTask<SkyrimDialogueStageResult<SkyrimDialogueProposal>> AnalyzeAsync(SkyrimDialogueAnalyzeRequest request, CancellationToken cancellationToken) { Analyze = request; return ValueTask.FromResult(Ok<SkyrimDialogueProposal>()); }
        public ValueTask<SkyrimDialogueStageResult<SkyrimDialogueOutputManifest>> ApplyAsync(SkyrimDialogueApplyRequest request, IProgress<string>? progress, CancellationToken cancellationToken) { Apply = request; return ValueTask.FromResult(Ok<SkyrimDialogueOutputManifest>()); }
        public ValueTask<SkyrimDialogueStageResult<SkyrimDialogueVerification>> VerifyAsync(SkyrimDialogueVerifyRequest request, CancellationToken cancellationToken) { Verify = request; return ValueTask.FromResult(Ok<SkyrimDialogueVerification>()); }
        private static SkyrimDialogueStageResult<T> Ok<T>() where T : class => new(true, null, null, null, ImmutableArray<Diagnostic>.Empty);
    }
}
