using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Architecture.Tests;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

internal static class SkyrimNpcDialogueCliTests
{
    public static async Task RunAsync()
    {
        var fixture = new SkyrimDialogueFixtures();
        string? previousRoot = Environment.GetEnvironmentVariable("ACTORWRIGHT_WORKSPACE_ROOT");
        Environment.SetEnvironmentVariable("ACTORWRIGHT_WORKSPACE_ROOT", fixture.Root);
        try
        {
            await CheckWindowsSetupAsync(fixture);
            using var server = new VoiceServer(fixture.Root);
            await Run("npc", "voice", "import", "--sample", PathOf("ref.wav"), "--plugin", "Example.esp", "--form-id", "0x800",
                "--editor-id", "ExampleActor", "--voice-prefix", "AWExample", "--output", PathOf("voice"));
            var sample = Read<SkyrimVoiceSampleAuthority>("voice/voice-sample.json");
            Check(sample.Normalized is { SampleRate: 22050, Channels: 1, BitsPerSample: 16 }, "CLI import normalized audio");
            fixture.Save("profile.json", new SkyrimDialogueProfile("Example", [], [], null, null));
            await Run("npc", "dialogue", "analyze", "--template", "follower-core-v1", "--profile", PathOf("profile.json"),
                "--npc-plugin", "Example.esp", "--form-id", "0x800", "--editor-id", "ExampleActor", "--voice-prefix", "AWExample",
                "--female", "true", "--manifest-output", PathOf("dialogue.json"));
            var manifest = Read<SkyrimDialogueManifest>("dialogue.json");
            Check(manifest.Lines.Length == 78, "CLI composes the actual full coverage registry");
            string[] synthesis = ["npc", "voice", "synthesize", "--manifest", PathOf("dialogue.json"), "--manifest-sha256", Hash("dialogue.json"),
                "--sample-authority", PathOf("voice/voice-sample.json"), "--sample-authority-sha256", Hash("voice/voice-sample.json"),
                "--output", PathOf("synth"), "--endpoint", server.Endpoint];
            await Run(synthesis);
            Check(server.PostCount == manifest.Lines.Length, "one synthesis request per authored line");
            await Run([.. synthesis, "--resume"]);
            var reused = Read<SkyrimVoiceSynthesisManifest>("synth/voice-synthesis.json");
            Check(server.PostCount == manifest.Lines.Length && reused.Lines.All(line => line.Status == SkyrimVoiceLineStatus.Skipped), "CLI resume reuses validated audio without new synthesis");
            await Run("npc", "dialogue", "analyze", "--manifest", PathOf("dialogue.json"), "--manifest-sha256", Hash("dialogue.json"),
                "--plugin", PathOf("Example.esp"), "--plugin-sha256", Hash("Example.esp"), "--data-root", fixture.Root, "--plugins", "Skyrim.esm,Example.esp",
                "--sample-authority", PathOf("voice/voice-sample.json"), "--sample-authority-sha256", Hash("voice/voice-sample.json"), "--output", PathOf("proposal.json"));
            JsonElement applied = await Run("npc", "dialogue", "apply", "--proposal", PathOf("proposal.json"), "--proposal-sha256", Hash("proposal.json"),
                "--synthesis", PathOf("synth/voice-synthesis.json"), "--synthesis-sha256", Hash("synth/voice-synthesis.json"), "--output", PathOf("output"));
            string outputManifest = applied.GetProperty("documentPath").GetString()!;
            JsonElement verified = await Run("npc", "dialogue", "verify", "--manifest", outputManifest, "--manifest-sha256", SkyrimNpcVoiceDocumentCodec.Hash(File.ReadAllBytes(outputManifest)).Value);
            Check(verified.GetProperty("document").GetProperty("verified").GetBoolean() && verified.GetProperty("document").GetProperty("audioCount").GetInt32() == 78,
                "CLI output independently verifies all template audio after real resume flow");
            await Run([.. synthesis, "--language", "es", "--resume"]);
            var translated = Read<SkyrimVoiceSynthesisManifest>("synth/voice-synthesis.json");
            Check(server.PostCount == 2 * manifest.Lines.Length && translated.Lines.All(line => line.Language == "es" && line.Status == SkyrimVoiceLineStatus.Succeeded),
                "Changing synthesis language regenerates every line");
            JsonElement translatedApply = await Run("npc", "dialogue", "apply", "--proposal", PathOf("proposal.json"), "--proposal-sha256", Hash("proposal.json"),
                "--synthesis", PathOf("synth/voice-synthesis.json"), "--synthesis-sha256", Hash("synth/voice-synthesis.json"), "--output", PathOf("output-es"));
            string translatedOutput = translatedApply.GetProperty("documentPath").GetString()!;
            JsonElement translatedVerify = await Run("npc", "dialogue", "verify", "--manifest", translatedOutput, "--manifest-sha256", SkyrimNpcVoiceDocumentCodec.Hash(File.ReadAllBytes(translatedOutput)).Value);
            Check(translatedVerify.GetProperty("document").GetProperty("verified").GetBoolean(), "Reviewed language override independently verifies after apply");
            foreach (string invalidLanguage in new[] { "en", "" })
            {
                fixture.Save("synth/voice-synthesis.json", translated with { Lines = translated.Lines.SetItem(0, translated.Lines[0] with { Language = invalidLanguage }) });
                JsonElement refused = await RunExpected(CommandExitCode.ValidationFailure, "npc", "dialogue", "apply", "--proposal", PathOf("proposal.json"), "--proposal-sha256", Hash("proposal.json"),
                    "--synthesis", PathOf("synth/voice-synthesis.json"), "--synthesis-sha256", Hash("synth/voice-synthesis.json"), "--output", PathOf("mixed-language-output"));
                Check(refused.GetProperty("diagnostics").EnumerateArray().Any(d => d.GetProperty("code").GetString() == "dialogue-synthesis-incomplete") && !Directory.Exists(PathOf("mixed-language-output")),
                    "Mixed or blank synthesis language must refuse before output publication");
            }
            Console.WriteLine("PASS voice import -> full template -> fixture synthesis -> resume -> dialogue analyze/apply/verify (78 lines)");
            Console.WriteLine("PASS language override regeneration -> apply/verify; mixed or blank language refused");
        }
        finally { Environment.SetEnvironmentVariable("ACTORWRIGHT_WORKSPACE_ROOT", previousRoot); }

        string PathOf(string name) => fixture.PathOf(name).Value;
        string Hash(string name) => fixture.Hash(name).Value;
        T Read<T>(string name) => SkyrimNpcVoiceDocumentCodec.Parse<T>(File.ReadAllBytes(PathOf(name)));
    }

    private static async Task CheckWindowsSetupAsync(SkyrimDialogueFixtures fixture)
    {
        using var server = new VoiceServer(fixture.Root, windows: true);
        fixture.Save("voice-services.json", new
        {
            endpoints = new[] { server.Endpoint },
            setups = new[] { new { endpoint = server.Endpoint, platform = "windows", serverFolder = fixture.Root, outputFolder = fixture.Root } }
        });
        try
        {
            await Run("npc", "voice", "discover", "--endpoint", server.Endpoint);
            Check(server.PostCount == 0, "Server setup discovery must not synthesize");
            fixture.Save("windows-dialogue.json", fixture.Manifest with { Lines = fixture.Manifest.Lines.Take(2).ToImmutableArray() });
            string[] args = ["npc", "voice", "synthesize", "--manifest", fixture.PathOf("windows-dialogue.json").Value,
                "--manifest-sha256", fixture.Hash("windows-dialogue.json").Value, "--sample-authority", fixture.PathOf("sample.json").Value,
                "--sample-authority-sha256", fixture.Hash("sample.json").Value, "--output", fixture.PathOf("windows-synth").Value];
            await Run(args);
            Check(server.PostCount == 2 && server.LastSpeakerReference == fixture.Sample.NormalizedPath,
                "Saved Windows setup must send an admitted native sample path, not a WSL path");
            SkyrimVoiceSynthesisManifest ledger = SkyrimNpcVoiceDocumentCodec.Parse<SkyrimVoiceSynthesisManifest>(
                File.ReadAllBytes(fixture.PathOf("windows-synth/voice-synthesis.json").Value));
            string referencePattern = ProtocolV2SchemaService.RenderInline("npc voice synthesize")
                .GetProperty("documentSchemas").EnumerateArray().Single(item => item.GetProperty("jsonSchema").GetProperty("properties").TryGetProperty("speakerReference", out _))
                .GetProperty("jsonSchema").GetProperty("properties").GetProperty("speakerReference").GetProperty("pattern").GetString()!;
            foreach (string reference in new[] { ledger.SpeakerReference, "/mnt/k/ExampleWorkspace/voice/sample.wav" })
                Check(System.Text.RegularExpressions.Regex.IsMatch(reference, referencePattern), "Published schema rejected an emitted platform reference: " + reference);
            foreach (string reference in new[] { "sample.wav", "K:sample.wav", @"\\server\share\sample.wav", @"\\?\K:\sample.wav", "/tmp/sample.wav", @"K:\", "/mnt/k/", "" })
                Check(!System.Text.RegularExpressions.Regex.IsMatch(reference, referencePattern), "Published schema admitted an unsupported sample reference: " + reference);
            await Run([.. args, "--resume"]);
            Check(server.PostCount == 2, "Unchanged Windows configuration must resume without another POST");
            Console.WriteLine("PASS saved Windows Mantella setup -> CLI discovery/native synthesis/resume; discovery sends no POST");
        }
        finally { File.Delete(fixture.PathOf("voice-services.json").Value); }
    }

    private static Task<JsonElement> Run(params string[] args) => RunExpected(CommandExitCode.Success, args);

    private static async Task<JsonElement> RunExpected(CommandExitCode expected, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int journalFactoryCalls = 0;
        CommandExitCode code = await NpcManager.Cli.Program.RunAsync([.. args, "--json"], output, error, root =>
        {
            journalFactoryCalls++;
            return new LocalOperationJournal(root);
        }, CancellationToken.None);
        Check(code == expected, string.Join(' ', args.Take(3)) + $" exit {(int)code}, expected {(int)expected}: " + output + error);
        Check(journalFactoryCalls == (args[1] == "dialogue" ? 1 : 0), "Only actual dialogue operations should construct their operation journal");
        using var document = JsonDocument.Parse(output.ToString());
        return document.RootElement.Clone();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class VoiceServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task loop;
        private readonly string folders;
        private readonly bool windows;
        public string Endpoint { get; }
        public int PostCount { get; private set; }
        public string? LastSpeakerReference { get; private set; }
        public VoiceServer(string root, bool windows = false)
        {
            this.windows = windows;
            string linuxRoot = "/mnt/k/" + root[3..].Replace('\\', '/');
            string folder = windows ? "." : linuxRoot;
            folders = JsonSerializer.Serialize(new { speaker_folder = folder, output_folder = folder, model_folder = folder });
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            loop = Loop();
        }
        private async Task Loop()
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    await Respond(client.GetStream());
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (SocketException) when (cancellation.IsCancellationRequested) { break; }
            }
        }
        private async Task Respond(NetworkStream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            string first = await reader.ReadLineAsync(cancellation.Token) ?? throw new IOException("Missing HTTP request.");
            int length = 0;
            bool chunked = false;
            for (string? header = await reader.ReadLineAsync(cancellation.Token); !string.IsNullOrEmpty(header); header = await reader.ReadLineAsync(cancellation.Token))
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                if (header.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)) chunked = true;
            }
            Check(length is >= 0 and <= 65536, "bounded fixture HTTP body");
            char[] request = new char[length];
            if (length > 0) Check(await reader.ReadBlockAsync(request.AsMemory(), cancellation.Token) == length, "complete fixture request body");
            var requestBody = new StringBuilder().Append(request);
            if (chunked)
            {
                int total = 0;
                for (int size = int.Parse((await reader.ReadLineAsync(cancellation.Token))!, System.Globalization.NumberStyles.HexNumber); size > 0;
                     size = int.Parse((await reader.ReadLineAsync(cancellation.Token))!, System.Globalization.NumberStyles.HexNumber))
                {
                    Check(size > 0 && (total += size) <= 65536, "bounded fixture request chunks");
                    char[] chunk = new char[size];
                    Check(await reader.ReadBlockAsync(chunk.AsMemory(), cancellation.Token) == size, "complete fixture request chunk");
                    requestBody.Append(chunk);
                    Check(await reader.ReadLineAsync(cancellation.Token) == "", "fixture chunk terminator");
                }
                Check(await reader.ReadLineAsync(cancellation.Token) == "", "fixture body terminator");
            }
            string path = first.Split(' ')[1];
            string json = path switch
            {
                "/openapi.json" => "{\"info\":{\"title\":\"fixture\",\"version\":\"1\"},\"paths\":{\"/tts_to_audio/\":{\"post\":{\"text\":true,\"speaker_wav\":true,\"language\":true}}}}",
                "/speakers_list" => windows ? "{\"en\":{\"speakers\":[]},\"es\":{\"speakers\":[]}}" : "[\"fixture\"]",
                "/languages" => "{\"English\":\"en\",\"Spanish\":\"es\"}",
                "/get_folders" => folders,
                "/get_models_list" => "[\"v2.0.2\"]",
                "/get_tts_settings" => "{\"temperature\":0.7}",
                _ => "{}"
            };
            byte[] body = Encoding.UTF8.GetBytes(json);
            string contentType = "application/json";
            if (path == "/tts_to_audio/")
            {
                using JsonDocument payload = JsonDocument.Parse(requestBody.ToString());
                LastSpeakerReference = payload.RootElement.GetProperty("speaker_wav").GetString();
                PostCount++; body = SkyrimDialogueFixtures.Wave(); contentType = "audio/wav";
            }
            byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellation.Token);
            await stream.WriteAsync(body, cancellation.Token);
        }
        public void Dispose()
        {
            cancellation.Cancel(); listener.Stop(); loop.GetAwaiter().GetResult(); cancellation.Dispose();
        }
    }
}
