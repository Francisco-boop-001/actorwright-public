using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static class XttsClientTests
{
    public static async Task RunAsync()
    {
        await TestWindowsServerSetupAsync();
        await TestDiscoveryAndStorageVerdictsAsync();
        await TestClientRetryClassificationAsync();
        await TestImportSynthesisResumeAndCancellationAsync();
    }

    private static async Task TestWindowsServerSetupAsync()
    {
        using var server = new XttsFixtureServer { RelativeFolders = true, MantellaSpeakers = true };
        string root = NewRoot();
        string serverRoot = Path.Combine(root, "server");
        try
        {
            Directory.CreateDirectory(Path.Combine(serverRoot, "speakers"));
            Directory.CreateDirectory(Path.Combine(serverRoot, "output"));
            Directory.CreateDirectory(Path.Combine(serverRoot, "models"));
            await File.WriteAllTextAsync(Path.Combine(root, "voice-services.json"), $$"""
                {"unrelated":{"keep":true},"endpoints":["http://127.0.0.1:1","{{server.Endpoint}}"],"setups":[
                  {"endpoint":"http://127.0.0.1:1","platform":"windows","serverFolder":"{{JsonEscape(serverRoot)}}","outputFolder":"{{JsonEscape(Path.Combine(serverRoot, "output"))}}"},
                  {"endpoint":"{{server.Endpoint}}","platform":"windows","serverFolder":"{{JsonEscape(serverRoot)}}","outputFolder":"{{JsonEscape(Path.Combine(serverRoot, "output"))}}","note":"keep"}
                ]}
                """);

            using var concreteService = new SkyrimNpcVoiceService(new WorkspacePath(root), new HttpClient());
            ISkyrimNpcVoiceService service = concreteService;
            var setup = new SkyrimVoiceServerSetup(server.Endpoint, "windows", serverRoot, Path.Combine(serverRoot, "output"));
            int beforeCheck = server.RequestCount;
            SkyrimVoiceServerSetupResult checkedSetup = await service.CheckServerSetupAsync(setup, CancellationToken.None);
            Check(checkedSetup.Succeeded && checkedSetup.Settings == setup && checkedSetup.Inventory?.SelectedEndpoint == server.Endpoint &&
                  checkedSetup.Inventory.Services.Single() is { Platform: "windows", SpeakerCount: 2 } descriptor &&
                  descriptor.SpeakerFolder == Path.Combine(serverRoot, "speakers") && descriptor.OutputFolder == setup.OutputFolder &&
                  server.RequestCount == beforeCheck + 6 && server.PostCount == 0,
                "A native Windows setup did not resolve the Mantella service folders without synthesis: " +
                Encoding.UTF8.GetString(SkyrimNpcVoiceDocumentCodec.Serialize(checkedSetup)));

            SkyrimVoiceServerSetupResult saved = await service.SaveServerSetupAsync(setup, CancellationToken.None);
            SkyrimVoiceServerSetupResult loaded = await service.LoadServerSetupAsync(CancellationToken.None);
            JsonObject persisted = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "voice-services.json")))!.AsObject();
            Check(saved.Succeeded && loaded is { Succeeded: true, Settings: not null } && loaded.Settings == setup &&
                  persisted["unrelated"]?["keep"]?.GetValue<bool>() == true && persisted["defaultEndpoint"]?.GetValue<string>() == server.Endpoint &&
                  persisted["endpoints"]?.AsArray().Count == 2 && persisted["setups"]?.AsArray().Count == 2 &&
                  persisted["setups"]?.AsArray()[1]?["note"]?.GetValue<string>() == "keep",
                "Saving a checked setup did not preserve unrelated configuration and other endpoint setups.");
            int beforeDefaultDiscovery = server.RequestCount;
            SkyrimVoiceServiceInventory defaultInventory = await service.DiscoverAsync(null, 3, null, CancellationToken.None);
            Check(defaultInventory.SelectedEndpoint == server.Endpoint && server.RequestCount == beforeDefaultDiscovery + 6,
                "The saved default endpoint was not selected without probing preserved secondary endpoints.");

            byte[] validConfiguration = await File.ReadAllBytesAsync(Path.Combine(root, "voice-services.json"));
            foreach (string malformed in new[]
            {
                "{\"endpoints\":[42]}",
                $$"""{"endpoints":["{{server.Endpoint}}"],"setups":[{"endpoint":"{{server.Endpoint}}","platform":"windows"}]}""",
                $$"""{"endpoints":["{{server.Endpoint}}"],"setups":[{"endpoint":"{{server.Endpoint}}","platform":"windows","serverFolder":"{{JsonEscape(serverRoot)}}","outputFolder":"{{JsonEscape(setup.OutputFolder)}}"},{"endpoint":"{{server.Endpoint}}/","platform":"windows","serverFolder":"{{JsonEscape(serverRoot)}}","outputFolder":"{{JsonEscape(setup.OutputFolder)}}"}]}"""
            })
            {
                byte[] malformedBytes = Encoding.UTF8.GetBytes(malformed);
                await File.WriteAllBytesAsync(Path.Combine(root, "voice-services.json"), malformedBytes);
                int beforeMalformedSave = server.RequestCount;
                SkyrimVoiceServerSetupResult refusedSave = await service.SaveServerSetupAsync(setup, CancellationToken.None);
                Check(!refusedSave.Succeeded && refusedSave.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid) &&
                      (await File.ReadAllBytesAsync(Path.Combine(root, "voice-services.json"))).SequenceEqual(malformedBytes) && server.RequestCount == beforeMalformedSave,
                    "Save did not refuse malformed or duplicate existing configuration unchanged before probing.");
            }
            await File.WriteAllBytesAsync(Path.Combine(root, "voice-services.json"), validConfiguration);

            int beforeAds = server.RequestCount;
            SkyrimVoiceServerSetupResult ads = await service.CheckServerSetupAsync(setup with { ServerFolder = serverRoot + ":stream" }, CancellationToken.None);
            Check(!ads.Succeeded && ads.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.ServerSetupInvalid) && server.RequestCount == beforeAds,
                "A Windows alternate-data-stream folder reached filesystem or HTTP inspection before typed setup refusal.");
            SkyrimVoiceServerSetupResult shortFolder = await service.CheckServerSetupAsync(setup with { ServerFolder = "a" }, CancellationToken.None);
            Check(!shortFolder.Succeeded && shortFolder.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.ServerSetupInvalid) && server.RequestCount == beforeAds,
                "A one-character relative Windows folder escaped typed setup refusal or reached HTTP inspection.");

            int beforeInvalid = server.RequestCount;
            SkyrimVoiceServerSetupResult invalid = await service.CheckServerSetupAsync(setup with { Endpoint = server.Endpoint + "/admin" }, CancellationToken.None);
            Check(!invalid.Succeeded && invalid.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.ServerSetupInvalid) && server.RequestCount == beforeInvalid,
                "A non-authority endpoint reached the XTTS server instead of typed setup refusal.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task TestDiscoveryAndStorageVerdictsAsync()
    {
        using var server = new XttsFixtureServer();
        string root = NewRoot();
        try
        {
            using var http = new HttpClient();
            var discovery = new XttsServiceDiscovery(new WorkspacePath(root), http);
            SkyrimVoiceServiceInventory safe = await discovery.DiscoverAsync(server.Endpoint, 3, CancellationToken.None);
            Check(safe.RequestTextLoggedByService && safe.SelectedEndpoint == server.Endpoint && safe.Services.Single().Compatible && safe.Services.Single().StorageVerdict == "safe",
                "A complete loopback XTTS fixture was not selected: " + Encoding.UTF8.GetString(SkyrimNpcVoiceDocumentCodec.Serialize(safe)));
            server.RelativeFolders = true;
            SkyrimVoiceServiceInventory unknown = await discovery.DiscoverAsync(server.Endpoint, 3, CancellationToken.None);
            Check(unknown.SelectedEndpoint is null && unknown.Services.Single().StorageVerdict == "unknown" &&
                  unknown.Services.Single().Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.ServiceStorageRefused),
                "Relative service storage did not fail closed as unknown.");
            server.RelativeFolders = false;
            server.Folders = ("/mnt/f", "/mnt/k/voice/output", "/mnt/k/voice/models");
            SkyrimVoiceServiceInventory exactProtected = await discovery.DiscoverAsync(server.Endpoint, 3, CancellationToken.None);
            Check(exactProtected.SelectedEndpoint is null && exactProtected.Services.Single().StorageVerdict == "refused",
                "The exact protected /mnt/f root was not refused.");
            server.Folders = ("/mnt/k/../f/private", "/mnt/k/voice/output", "/mnt/k/voice/models");
            SkyrimVoiceServiceInventory traversedProtected = await discovery.DiscoverAsync(server.Endpoint, 3, CancellationToken.None);
            Check(traversedProtected.SelectedEndpoint is null && traversedProtected.Services.Single().StorageVerdict == "refused",
                "A normalized Linux path traversal into /mnt/f was not refused.");
            server.Folders = ("/var/lib/xtts", "/mnt/k/voice/output", "/mnt/k/voice/models");
            SkyrimVoiceServiceInventory unresolvedLinux = await discovery.DiscoverAsync(server.Endpoint, 3, CancellationToken.None);
            Check(unresolvedLinux.SelectedEndpoint is null && unresolvedLinux.Services.Single().StorageVerdict == "unknown",
                "Linux-local storage without WSL backing proof was not left unknown.");
            foreach (string folder in new[] { "/mnt/k/ExampleWorkspace/voice", "/mnt/k/ExampleWorkspace-lookalike/voice" })
            {
                server.Folders = (folder, folder, folder);
                SkyrimVoiceServiceInventory consumer = await discovery.DiscoverAsync(server.Endpoint, 3, CancellationToken.None);
                Check(consumer.SelectedEndpoint == server.Endpoint && consumer.Services.Single().StorageVerdict == "safe",
                    "The offline consumer storage was rejected: " + folder);
            }
            foreach (string folder in new[] { "/mnt/k/ExampleExchange/voice", "/mnt/k/ExampleMigrationArchive/voice", "/mnt/k/voice/../ExampleMigrationArchive/voice" })
            {
                server.Folders = (folder, folder, folder);
                SkyrimVoiceServiceInventory protectedStorage = await discovery.DiscoverAsync(server.Endpoint, 3, CancellationToken.None);
                Check(protectedStorage.SelectedEndpoint is null && protectedStorage.Services.Single().StorageVerdict == "refused",
                    "Protected storage was admitted: " + folder);
            }
            server.Folders = null;
            server.UnsupportedApi = true;
            SkyrimVoiceServiceInventory unsupported = await discovery.DiscoverAsync(server.Endpoint, 3, CancellationToken.None);
            Check(unsupported.SelectedEndpoint is null && unsupported.Services.Single().Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.ServiceUnsupportedApi),
                "A service without /tts_to_audio/ was not rejected.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task TestClientRetryClassificationAsync()
    {
        using var server = new XttsFixtureServer { RejectSpeaker = true };
        var delays = new List<TimeSpan>();
        using var client = new XttsApiServerClient(new HttpClient(), (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        try { await client.SynthesizeAsync(new Uri(server.Endpoint), "hello", "/mnt/k/ref.wav", "en", CancellationToken.None); throw new InvalidOperationException("Expected rejected speaker request."); }
        catch (XttsRequestException exception) { Check(exception.Code == SkyrimNpcVoiceDiagnosticCodes.ServiceRequestRejected && server.PostCount == 1 && delays.Count == 0, "A 4xx speaker rejection was retried or misclassified."); }
        server.RejectSpeaker = false;
        server.FailuresRemaining = 3;
        (byte[] bytes, SkyrimVoiceSampleAudio audio, _) = await client.SynthesizeAsync(new Uri(server.Endpoint), "hello", "/mnt/k/ref.wav", "en", CancellationToken.None);
        Check(bytes.Length > 44 && audio.SampleRate == 24000 && delays.SequenceEqual([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)]),
            "5/15/30 retry semantics or returned WAV validation drifted.");
        server.SuccessfulAudio = Program.SyntheticWav(24000, 1, 32, "float", 0.5, 0.5);
        (byte[] normalizedFloat, SkyrimVoiceSampleAudio normalizedAudio, _) = await client.SynthesizeAsync(new Uri(server.Endpoint), "hello", "/mnt/k/ref.wav", "en", CancellationToken.None);
        Check(normalizedAudio is { SampleRate: 24000, Channels: 1, BitsPerSample: 16, Format: "pcm" } &&
              WavSampleCodec.Decode(normalizedFloat).Format == "pcm",
            "A valid Mantella 24 kHz mono float32 WAV was not normalized to PCM16.");
        byte[] pcm = WavSampleCodec.WritePcm16(Enumerable.Range(0, 12000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 24000))).ToArray(), 24000);
        server.SuccessfulAudio = pcm;
        (byte[] preservedPcm, _, _) = await client.SynthesizeAsync(new Uri(server.Endpoint), "hello", "/mnt/k/ref.wav", "en", CancellationToken.None);
        Check(preservedPcm.SequenceEqual(pcm), "An already-valid PCM16 XTTS response was rewritten.");
        foreach (byte[] invalid in new[]
        {
            Program.SyntheticWav(22050, 1, 32, "float", 0.5, 0.5),
            Program.SyntheticWav(24000, 2, 32, "float", 0.5, 0.5),
            Program.SyntheticWav(24000, 1, 32, "float", 0.5, 0),
            Program.SyntheticWav(24000, 1, 32, "float", 0.5, 0.00004)
        })
        {
            server.SuccessfulAudio = invalid;
            try { await client.SynthesizeAsync(new Uri(server.Endpoint), "hello", "/mnt/k/ref.wav", "en", CancellationToken.None); throw new InvalidOperationException("Expected incompatible float WAV refusal."); }
            catch (XttsRequestException exception) { Check(exception.Code == SkyrimNpcVoiceDiagnosticCodes.AudioShape, "An incompatible float WAV escaped audio-shape refusal."); }
        }
        server.WrongAudioType = true;
        try { await client.SynthesizeAsync(new Uri(server.Endpoint), "hello", "/mnt/k/ref.wav", "en", CancellationToken.None); throw new InvalidOperationException("Expected non-WAV media refusal."); }
        catch (XttsRequestException exception) { Check(exception.Code == SkyrimNpcVoiceDiagnosticCodes.AudioShape, "A non-audio/wav response was not refused."); }
    }

    private static async Task TestImportSynthesisResumeAndCancellationAsync()
    {
        using var server = new XttsFixtureServer();
        string root = NewRoot();
        string outside = Path.Combine(Path.GetDirectoryName(root)!, $"voice-outside-{Guid.NewGuid():N}");
        string? reparse = null;
        try
        {
            Directory.CreateDirectory(outside);
            string sample = Path.Combine(root, "sample.wav");
            await File.WriteAllBytesAsync(sample, Program.SyntheticWav(44100, 2, 16, "pcm", 3, 0.7));
            using var service = new SkyrimNpcVoiceService(new WorkspacePath(root), retryDelay: (_, _) => Task.CompletedTask);
            string opposite = Path.Combine(root, "opposite.wav");
            await File.WriteAllBytesAsync(opposite, OppositePhaseStereoWav());
            string oppositeOutput = Path.Combine(root, "opposite-authority");
            SkyrimVoiceSampleImportResult oppositeResult = await service.ImportSampleAsync(new SkyrimVoiceSampleImportRequest(
                new WorkspacePath(opposite), new PluginName("Fixture.esp"), new FormId(0x800), null, "Fixture", new WorkspacePath(oppositeOutput)), CancellationToken.None);
            Check(!oppositeResult.Imported && oppositeResult.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.SampleSilent) && !Directory.Exists(oppositeOutput),
                "An opposite-phase stereo source published a silent normalized reference.");

            string outsideSample = Path.Combine(outside, "redirected.wav");
            await File.WriteAllBytesAsync(outsideSample, Program.SyntheticWav(22050, 1, 16, "pcm", 1, 0.5));
            reparse = Path.Combine(root, "redirected");
            bool reparseCreated = PhysicalReparseFixture.TryCreateDirectoryLink(
                reparse, outside, root);
            if (reparseCreated)
            {
                SkyrimVoiceSampleImportResult redirected = await service.ImportSampleAsync(new SkyrimVoiceSampleImportRequest(
                    new WorkspacePath(Path.Combine(reparse, "redirected.wav")), new PluginName("Fixture.esp"), new FormId(0x800), null, "Fixture", new WorkspacePath(Path.Combine(root, "redirected-authority"))), CancellationToken.None);
                Check(!redirected.Imported && redirected.Diagnostics.Any(d => d.Code == ProtocolV2DiagnosticCodes.ReparsePointRefused),
                    "Voice import followed a product-owned reparse-point ancestor.");
            }
            else
            {
                string virtualReparse = Path.Combine(root, "virtual-reparse");
                Directory.CreateDirectory(virtualReparse);
                await File.WriteAllBytesAsync(Path.Combine(virtualReparse, "redirected.wav"), Program.SyntheticWav(22050, 1, 16, "pcm", 1, 0.5));
                using var guardedService = new SkyrimNpcVoiceService(new WorkspacePath(root), retryDelay: (_, _) => Task.CompletedTask,
                    workspacePolicy: new ReparseMarkerPolicy(new KOnlyWorkspacePolicy(new WorkspacePath(root), ActorwrightWorkspace.ResolveProtectedRoot(new WorkspacePath(root)))));
                string guardedDiscoveryOutput = Path.Combine(virtualReparse, "services.json");
                SkyrimVoiceServiceInventory guardedDiscovery = await guardedService.DiscoverAsync(server.Endpoint, 3, new WorkspacePath(guardedDiscoveryOutput), CancellationToken.None);
                Check(guardedDiscovery.Diagnostics.Any(d => d.Code == ProtocolV2DiagnosticCodes.ReparsePointRefused) && !File.Exists(guardedDiscoveryOutput),
                    "Voice discovery did not honor the shared ancestry-policy output refusal.");
                SkyrimVoiceSampleImportResult redirected = await guardedService.ImportSampleAsync(new SkyrimVoiceSampleImportRequest(
                    new WorkspacePath(Path.Combine(virtualReparse, "redirected.wav")), new PluginName("Fixture.esp"), new FormId(0x800), null, "Fixture", new WorkspacePath(Path.Combine(root, "redirected-authority"))), CancellationToken.None);
                Check(!redirected.Imported && redirected.Diagnostics.Any(d => d.Code == ProtocolV2DiagnosticCodes.ReparsePointRefused),
                    "Voice import did not honor the shared ancestry-policy reparse refusal.");
                SkyrimVoiceSampleImportResult redirectedOutput = await guardedService.ImportSampleAsync(new SkyrimVoiceSampleImportRequest(
                    new WorkspacePath(sample), new PluginName("Fixture.esp"), new FormId(0x800), null, "Fixture", new WorkspacePath(Path.Combine(virtualReparse, "authority"))), CancellationToken.None);
                Check(!redirectedOutput.Imported && redirectedOutput.Diagnostics.Any(d => d.Code == ProtocolV2DiagnosticCodes.ReparsePointRefused),
                    "Voice import did not honor the shared ancestry-policy output refusal.");
                byte[] bogusManifest = "{}"u8.ToArray();
                string bogusManifestPath = Path.Combine(virtualReparse, "manifest.json");
                await File.WriteAllBytesAsync(bogusManifestPath, bogusManifest);
                int beforeGuardedSynthesis = server.PostCount;
                SkyrimVoiceSynthesisResult guardedSynthesis = await guardedService.SynthesizeAsync(new SkyrimVoiceSynthesisRequest(
                    new WorkspacePath(bogusManifestPath), Hash(bogusManifest), new WorkspacePath(sample), Hash(await File.ReadAllBytesAsync(sample)),
                    new WorkspacePath(Path.Combine(root, "guarded-synth")), server.Endpoint, null, null, false), null, CancellationToken.None);
                Check(!guardedSynthesis.Completed && guardedSynthesis.Diagnostics.Any(d => d.Code == ProtocolV2DiagnosticCodes.ReparsePointRefused) && server.PostCount == beforeGuardedSynthesis,
                    "Synthesis read a manifest refused by the shared ancestry policy.");
            }
            SkyrimVoiceSampleImportResult outsideImport = await service.ImportSampleAsync(new SkyrimVoiceSampleImportRequest(
                new WorkspacePath(outsideSample), new PluginName("Fixture.esp"), new FormId(0x800), null, "Fixture", new WorkspacePath(Path.Combine(root, "outside-authority"))), CancellationToken.None);
            Check(!outsideImport.Imported && outsideImport.Diagnostics.Any(d => d.Code == "data-root-outside-workspace") &&
                  outsideImport.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.SamplePathRefused),
                "Voice import did not retain its named path refusal alongside the policy diagnostic.");

            var innerPolicy = new KOnlyWorkspacePolicy(new WorkspacePath(root), ActorwrightWorkspace.ResolveProtectedRoot(new WorkspacePath(root)));
            string refusedImportRoot = Path.Combine(root, "leaf-refused-authority");
            var importLeafPolicy = new LeafRefusalPolicy(innerPolicy, writeRefusal: path => Path.GetFileName(path.Value) == "voice-sample.json");
            using (var leafGuardedImport = new SkyrimNpcVoiceService(new WorkspacePath(root), retryDelay: (_, _) => Task.CompletedTask, workspacePolicy: importLeafPolicy))
            {
                SkyrimVoiceSampleImportResult refusedImport = await leafGuardedImport.ImportSampleAsync(new SkyrimVoiceSampleImportRequest(
                    new WorkspacePath(sample), new PluginName("Fixture.esp"), new FormId(0x800), null, "Fixture", new WorkspacePath(refusedImportRoot)), CancellationToken.None);
                Check(!refusedImport.Imported && importLeafPolicy.WriteRefusals == 1 && !Directory.Exists(refusedImportRoot),
                    "Voice import wrote an output leaf refused by the workspace policy.");
            }
            string sampleOutput = Path.Combine(root, "authority");
            SkyrimVoiceSampleImportResult imported = await service.ImportSampleAsync(new SkyrimVoiceSampleImportRequest(
                new WorkspacePath(sample), new PluginName("Fixture.esp"), new FormId(0x800), new EditorId("FixtureNpc"), "Fixture", new WorkspacePath(sampleOutput)), CancellationToken.None);
            Check(imported.Imported && imported.Authority is { Normalized.SampleRate: 22050, Normalized.Channels: 1, Normalized.BitsPerSample: 16 } &&
                  File.Exists(imported.Authority.NormalizedPath), "Valid stereo sample import did not write a normalized hash-bound authority.");
            SkyrimVoiceSampleImportResult existing = await service.ImportSampleAsync(new SkyrimVoiceSampleImportRequest(
                new WorkspacePath(sample), new PluginName("Fixture.esp"), new FormId(0x800), null, "Fixture", new WorkspacePath(sampleOutput)), CancellationToken.None);
            Check(!existing.Imported && existing.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.OutputExists), "Existing sample output was not refused.");

            SkyrimDialogueManifest manifest = Manifest("hello there", "goodbye now");
            SkyrimDialogueManifest unsafeManifest = manifest with { Lines = [manifest.Lines[0] with { Id = "../escape" }] };
            string unsafePath = Path.Combine(root, "unsafe-dialogue.json");
            byte[] unsafeBytes = SkyrimNpcVoiceDocumentCodec.Serialize(unsafeManifest);
            await File.WriteAllBytesAsync(unsafePath, unsafeBytes);
            SkyrimVoiceSynthesisResult unsafeResult = await service.SynthesizeAsync(new SkyrimVoiceSynthesisRequest(
                new WorkspacePath(unsafePath), Hash(unsafeBytes), imported.AuthorityPath!.Value, imported.AuthoritySha256!.Value,
                new WorkspacePath(Path.Combine(root, "unsafe-synth")), server.Endpoint, null, null, false), null, CancellationToken.None);
            Check(!unsafeResult.Completed && unsafeResult.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid), "A traversal-bearing line id was not refused before synthesis.");

            string manifestPath = Path.Combine(root, "dialogue.json");
            byte[] manifestBytes = SkyrimNpcVoiceDocumentCodec.Serialize(manifest);
            await File.WriteAllBytesAsync(manifestPath, manifestBytes);
            int beforeInvalidShapePosts = server.PostCount;
            int beforeInvalidShapeRequests = server.RequestCount;
            foreach ((string name, byte[] bytes) in new[]
            {
                ("missing-lines", JsonVariant(manifestBytes, "lines", "missing")),
                ("null-lines", JsonVariant(manifestBytes, "lines", "null")),
                ("null-line-row", JsonVariant(manifestBytes, "lines", "null-row")),
                ("null-npc", JsonVariant(manifestBytes, "npc", "null"))
            })
            {
                string invalidManifestPath = Path.Combine(root, $"{name}.json");
                await File.WriteAllBytesAsync(invalidManifestPath, bytes);
                SkyrimVoiceSynthesisResult invalidShape = await service.SynthesizeAsync(new SkyrimVoiceSynthesisRequest(
                    new WorkspacePath(invalidManifestPath), Hash(bytes), imported.AuthorityPath!.Value, imported.AuthoritySha256!.Value,
                    new WorkspacePath(Path.Combine(root, $"{name}-synth")), server.Endpoint, null, null, false), null, CancellationToken.None);
                Check(!invalidShape.Completed && invalidShape.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid) &&
                      server.PostCount == beforeInvalidShapePosts && server.RequestCount == beforeInvalidShapeRequests,
                    $"Dialogue manifest shape '{name}' reached discovery or escaped typed document refusal.");
            }

            byte[] malformedAuthorityBytes = JsonVariant(await File.ReadAllBytesAsync(imported.AuthorityPath!.Value.Value), "normalized", "null");
            string malformedAuthorityPath = Path.Combine(root, "null-normalized-authority.json");
            await File.WriteAllBytesAsync(malformedAuthorityPath, malformedAuthorityBytes);
            SkyrimVoiceSynthesisResult malformedAuthority = await service.SynthesizeAsync(new SkyrimVoiceSynthesisRequest(
                new WorkspacePath(manifestPath), Hash(manifestBytes), new WorkspacePath(malformedAuthorityPath), Hash(malformedAuthorityBytes),
                new WorkspacePath(Path.Combine(root, "null-normalized-synth")), server.Endpoint, null, null, false), null, CancellationToken.None);
            Check(!malformedAuthority.Completed && malformedAuthority.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid) &&
                  server.PostCount == beforeInvalidShapePosts && server.RequestCount == beforeInvalidShapeRequests,
                "A sample authority with a null required measurement reached discovery or escaped typed document refusal.");

            SkyrimDialogueManifest emptyManifest = manifest with { Lines = [] };
            byte[] emptyManifestBytes = SkyrimNpcVoiceDocumentCodec.Serialize(emptyManifest);
            string emptyManifestPath = Path.Combine(root, "empty-dialogue.json");
            await File.WriteAllBytesAsync(emptyManifestPath, emptyManifestBytes);
            SkyrimVoiceSynthesisResult emptyResult = await service.SynthesizeAsync(new SkyrimVoiceSynthesisRequest(
                new WorkspacePath(emptyManifestPath), Hash(emptyManifestBytes), imported.AuthorityPath.Value, imported.AuthoritySha256.Value,
                new WorkspacePath(Path.Combine(root, "empty-synth")), server.Endpoint, null, null, false), null, CancellationToken.None);
            Check(emptyResult.Completed && emptyResult is { Succeeded: 0, Failed: 0, Skipped: 0, Pending: 0 } && server.PostCount == beforeInvalidShapePosts,
                "A valid empty dialogue manifest did not complete as the deliberate no-op policy.");

            byte[] outsideNormalizedBytes = Program.SyntheticWav(22050, 1, 16, "pcm", 1, 0.5);
            string outsideNormalized = Path.Combine(outside, "normalized.wav");
            await File.WriteAllBytesAsync(outsideNormalized, outsideNormalizedBytes);
            SkyrimVoiceSampleAuthority forgedAuthority = imported.Authority! with
            {
                NormalizedPath = outsideNormalized,
                NormalizedSha256 = Hash(outsideNormalizedBytes),
                Normalized = WavSampleCodec.Measure(WavSampleCodec.Decode(outsideNormalizedBytes))
            };
            byte[] forgedBytes = SkyrimNpcVoiceDocumentCodec.Serialize(forgedAuthority);
            string forgedPath = Path.Combine(root, "forged-authority.json");
            await File.WriteAllBytesAsync(forgedPath, forgedBytes);
            int beforeForged = server.PostCount;
            SkyrimVoiceSynthesisResult forgedResult = await service.SynthesizeAsync(new SkyrimVoiceSynthesisRequest(
                new WorkspacePath(manifestPath), Hash(manifestBytes), new WorkspacePath(forgedPath), Hash(forgedBytes),
                new WorkspacePath(Path.Combine(root, "forged-synth")), server.Endpoint, null, null, false), null, CancellationToken.None);
            Check(!forgedResult.Completed && forgedResult.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) && server.PostCount == beforeForged,
                "Synthesis read an embedded normalized sample outside the workspace.");

            using (var redirectTarget = new XttsFixtureServer())
            {
                server.RedirectSynthesisTo = redirectTarget.Endpoint + "/tts_to_audio/";
                int beforeRedirect = server.PostCount;
                SkyrimVoiceSynthesisResult redirectedSynthesis = await service.SynthesizeAsync(new SkyrimVoiceSynthesisRequest(
                    new WorkspacePath(manifestPath), Hash(manifestBytes), imported.AuthorityPath!.Value, imported.AuthoritySha256!.Value,
                    new WorkspacePath(Path.Combine(root, "redirect-synth")), server.Endpoint, null, null, false), null, CancellationToken.None);
                Check(!redirectedSynthesis.Completed && server.PostCount == beforeRedirect + manifest.Lines.Length && redirectTarget.PostCount == 0,
                    $"The owned voice client mishandled a synthesis redirect: completed={redirectedSynthesis.Completed}, sourcePosts={server.PostCount - beforeRedirect}, targetPosts={redirectTarget.PostCount}.");
                server.RedirectSynthesisTo = null;
            }

            string synthRoot = Path.Combine(root, "synth");
            var request = new SkyrimVoiceSynthesisRequest(new WorkspacePath(manifestPath), Hash(manifestBytes), imported.AuthorityPath!.Value, imported.AuthoritySha256!.Value,
                new WorkspacePath(synthRoot), server.Endpoint, null, null, false);
            SkyrimVoiceSynthesisResult first = await service.SynthesizeAsync(request, null, CancellationToken.None);
            int afterFirst = server.PostCount;
            Check(first.Completed && first.Succeeded == 2 && File.Exists(Path.Combine(synthRoot, "line-one.wav")) && File.Exists(Path.Combine(synthRoot, "line-two.wav")), "Fixture-server synthesis did not complete.");
            string ledgerPath = Path.Combine(synthRoot, "voice-synthesis.json");
            byte[] validLedgerBytes = await File.ReadAllBytesAsync(ledgerPath);
            int afterFirstRequests = server.RequestCount;
            foreach ((string name, byte[] bytes) in new[]
            {
                ("missing-lines", JsonVariant(validLedgerBytes, "lines", "missing")),
                ("null-lines", JsonVariant(validLedgerBytes, "lines", "null")),
                ("null-line-row", JsonVariant(validLedgerBytes, "lines", "null-row"))
            })
            {
                await File.WriteAllBytesAsync(ledgerPath, bytes);
                SkyrimVoiceSynthesisResult invalidPrior = await service.SynthesizeAsync(request with { Resume = true }, null, CancellationToken.None);
                Check(!invalidPrior.Completed && invalidPrior.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid) &&
                      server.PostCount == afterFirst && server.RequestCount == afterFirstRequests,
                    $"Resume ledger shape '{name}' reached discovery or escaped typed document refusal.");
            }
            await File.WriteAllBytesAsync(ledgerPath, validLedgerBytes);
            SkyrimVoiceSynthesisResult resumed = await service.SynthesizeAsync(request with { Resume = true }, null, CancellationToken.None);
            Check(resumed.Completed && resumed.Skipped == 2 && server.PostCount == afterFirst, "Resume did not reuse byte-valid unchanged lines.");

            var ledgerPolicy = new LeafRefusalPolicy(innerPolicy, readRefusal: path => path.Value == ledgerPath);
            using (var ledgerGuardedService = new SkyrimNpcVoiceService(new WorkspacePath(root), retryDelay: (_, _) => Task.CompletedTask, workspacePolicy: ledgerPolicy))
            {
                SkyrimVoiceSynthesisResult refusedLedger = await ledgerGuardedService.SynthesizeAsync(request with { Resume = true }, null, CancellationToken.None);
                Check(!refusedLedger.Completed && ledgerPolicy.ReadRefusals == 1 && server.PostCount == afterFirst,
                    "Resume read voice-synthesis.json without admitting the exact ledger leaf.");
            }

            string refusedWavRoot = Path.Combine(root, "leaf-refused-synthesis");
            var wavLeafPolicy = new LeafRefusalPolicy(innerPolicy, writeRefusal: path => Path.GetFileName(path.Value) == "line-one.wav");
            using (var wavGuardedService = new SkyrimNpcVoiceService(new WorkspacePath(root), retryDelay: (_, _) => Task.CompletedTask, workspacePolicy: wavLeafPolicy))
            {
                SkyrimVoiceSynthesisResult refusedWav = await wavGuardedService.SynthesizeAsync(request with { OutputRoot = new WorkspacePath(refusedWavRoot), Resume = false }, null, CancellationToken.None);
                Check(!refusedWav.Completed && wavLeafPolicy.WriteRefusals == 1 && server.PostCount == afterFirst && !Directory.Exists(refusedWavRoot),
                    "Synthesis requested audio before admitting its exact output leaf.");
            }

            string refusedTempRoot = Path.Combine(root, "temp-refused-synthesis");
            var tempLeafPolicy = new LeafRefusalPolicy(innerPolicy, writeRefusal: path => path.Value.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
            using (var tempGuardedService = new SkyrimNpcVoiceService(new WorkspacePath(root), retryDelay: (_, _) => Task.CompletedTask, workspacePolicy: tempLeafPolicy))
            {
                SkyrimVoiceSynthesisResult refusedTemp = await tempGuardedService.SynthesizeAsync(request with { OutputRoot = new WorkspacePath(refusedTempRoot), Resume = false }, null, CancellationToken.None);
                Check(!refusedTemp.Completed && tempLeafPolicy.WriteRefusals == 1 && !File.Exists(Path.Combine(refusedTempRoot, "line-one.wav")) &&
                      !Directory.EnumerateFiles(refusedTempRoot, "*.tmp").Any(), "Synthesis wrote through a temporary leaf refused by the workspace policy.");
            }
            int afterLeafControls = server.PostCount;

            SkyrimVoiceSynthesisManifest ledger = SkyrimNpcVoiceDocumentCodec.Parse<SkyrimVoiceSynthesisManifest>(await File.ReadAllBytesAsync(ledgerPath));
            string escapedOutput = Path.Combine(root, "escaped.wav");
            await File.WriteAllBytesAsync(escapedOutput, outsideNormalizedBytes);
            foreach (string unsafeOutput in new[] { outsideNormalized, Path.Combine("..", "escaped.wav") })
            {
                SkyrimVoiceSynthesisManifest unsafeLedger = ledger with { Lines = ledger.Lines.SetItem(0, ledger.Lines[0] with { Status = SkyrimVoiceLineStatus.Succeeded, OutputFile = unsafeOutput, OutputSha256 = Hash(outsideNormalizedBytes) }) };
                await File.WriteAllBytesAsync(ledgerPath, SkyrimNpcVoiceDocumentCodec.Serialize(unsafeLedger));
                SkyrimVoiceSynthesisResult unsafeResume = await service.SynthesizeAsync(request with { Resume = true }, null, CancellationToken.None);
                Check(!unsafeResume.Completed && unsafeResume.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) && server.PostCount == afterLeafControls,
                    $"Resume read unsafe output filename '{unsafeOutput}'.");
            }
            await File.WriteAllBytesAsync(ledgerPath, SkyrimNpcVoiceDocumentCodec.Serialize(ledger with
            {
                Lines = ledger.Lines.SetItem(0, ledger.Lines[0] with { Status = SkyrimVoiceLineStatus.Skipped, OutputFile = "line-one.wav", OutputSha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(synthRoot, "line-one.wav"))) })
            }));

            SkyrimDialogueManifest changed = Manifest("changed text", "goodbye now");
            byte[] changedBytes = SkyrimNpcVoiceDocumentCodec.Serialize(changed);
            await File.WriteAllBytesAsync(manifestPath, changedBytes);
            SkyrimVoiceSynthesisResult regenerated = await service.SynthesizeAsync(request with { ManifestSha256 = Hash(changedBytes), Resume = true }, null, CancellationToken.None);
            Check(regenerated.Completed && regenerated.Succeeded == 1 && regenerated.Skipped == 1 && server.PostCount == afterLeafControls + 1,
                $"Changed text did not invalidate only its synthesis line: completed={regenerated.Completed}, succeeded={regenerated.Succeeded}, skipped={regenerated.Skipped}, posts={server.PostCount}, initialPosts={afterLeafControls}.");

            int beforeLanguageChange = server.PostCount;
            SkyrimVoiceSynthesisRequest languageRequest = request with { ManifestSha256 = Hash(changedBytes), Language = "es", Resume = true };
            SkyrimVoiceSynthesisResult changedLanguage = await service.SynthesizeAsync(languageRequest, null, CancellationToken.None);
            Check(changedLanguage.Completed && changedLanguage.Succeeded == 2 && server.PostCount == beforeLanguageChange + 2,
                "A changed synthesis language did not regenerate every prior line.");
            SkyrimVoiceSynthesisResult unchangedLanguage = await service.SynthesizeAsync(languageRequest, null, CancellationToken.None);
            Check(unchangedLanguage.Completed && unchangedLanguage.Skipped == 2 && server.PostCount == beforeLanguageChange + 2,
                "An unchanged language binding did not retain normal resume reuse.");

            string secondSample = Path.Combine(root, "second-sample.wav");
            await File.WriteAllBytesAsync(secondSample, WavSampleCodec.WritePcm16(
                Enumerable.Range(0, 24000).Select(i => (float)(0.4 * Math.Sin(2 * Math.PI * 330 * i / 24000))).ToArray(), 24000));
            SkyrimVoiceSampleImportResult secondImported = await service.ImportSampleAsync(new SkyrimVoiceSampleImportRequest(
                new WorkspacePath(secondSample), new PluginName("Fixture.esp"), new FormId(0x800), new EditorId("FixtureNpc"), "Fixture",
                new WorkspacePath(Path.Combine(root, "second-authority"))), CancellationToken.None);
            Check(secondImported.Imported, "The second admitted reference sample fixture was not imported.");
            int beforeSampleChange = server.PostCount;
            SkyrimVoiceSynthesisRequest sampleRequest = languageRequest with
            {
                SampleAuthority = secondImported.AuthorityPath!.Value,
                SampleAuthoritySha256 = secondImported.AuthoritySha256!.Value
            };
            SkyrimVoiceSynthesisResult changedSample = await service.SynthesizeAsync(sampleRequest, null, CancellationToken.None);
            Check(changedSample.Completed && changedSample.Succeeded == 2 && server.PostCount == beforeSampleChange + 2,
                "A changed normalized sample binding did not regenerate every prior line.");

            SkyrimVoiceSampleAuthority changedIdentityAuthority = secondImported.Authority! with { VoicePrefix = "DifferentIdentity" };
            byte[] changedIdentityBytes = SkyrimNpcVoiceDocumentCodec.Serialize(changedIdentityAuthority);
            string changedIdentityPath = Path.Combine(root, "changed-identity-authority.json");
            await File.WriteAllBytesAsync(changedIdentityPath, changedIdentityBytes);
            int beforeMismatchedIdentityRequests = server.RequestCount;
            int beforeIdentityChange = server.PostCount;
            SkyrimVoiceSynthesisRequest mismatchedIdentityRequest = sampleRequest with
            {
                SampleAuthority = new WorkspacePath(changedIdentityPath),
                SampleAuthoritySha256 = Hash(changedIdentityBytes)
            };
            SkyrimVoiceSynthesisResult mismatchedIdentity = await service.SynthesizeAsync(mismatchedIdentityRequest, null, CancellationToken.None);
            Check(!mismatchedIdentity.Completed && mismatchedIdentity.Diagnostics.Any(d => d.Code == SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid) &&
                  server.PostCount == beforeIdentityChange && server.RequestCount == beforeMismatchedIdentityRequests,
                "An incoherent manifest and sample identity pair reached discovery or escaped typed refusal.");

            SkyrimDialogueManifest changedIdentityManifest = changed with { Npc = changed.Npc with { VoicePrefix = "DifferentIdentity" } };
            byte[] changedIdentityManifestBytes = SkyrimNpcVoiceDocumentCodec.Serialize(changedIdentityManifest);
            string changedIdentityManifestPath = Path.Combine(root, "changed-identity-dialogue.json");
            await File.WriteAllBytesAsync(changedIdentityManifestPath, changedIdentityManifestBytes);
            SkyrimVoiceSynthesisRequest coherentIdentityRequest = mismatchedIdentityRequest with
            {
                Manifest = new WorkspacePath(changedIdentityManifestPath),
                ManifestSha256 = Hash(changedIdentityManifestBytes)
            };
            SkyrimVoiceSynthesisResult changedIdentity = await service.SynthesizeAsync(coherentIdentityRequest, null, CancellationToken.None);
            Check(changedIdentity.Completed && changedIdentity.Succeeded == 2 && server.PostCount == beforeIdentityChange + 2,
                "A coherently changed NPC identity did not regenerate every prior line.");
            SkyrimVoiceSynthesisResult unchangedIdentity = await service.SynthesizeAsync(coherentIdentityRequest, null, CancellationToken.None);
            Check(unchangedIdentity.Completed && unchangedIdentity.Skipped == 2 && server.PostCount == beforeIdentityChange + 2,
                "An unchanged coherent NPC identity did not retain normal resume reuse.");

            string cancelRoot = Path.Combine(root, "cancel");
            using var cancel = new CancellationTokenSource();
            SkyrimVoiceSynthesisResult cancelled = await service.SynthesizeAsync(request with { OutputRoot = new WorkspacePath(cancelRoot), ManifestSha256 = Hash(changedBytes), Resume = false }, new InlineProgress(_ => cancel.Cancel()), cancel.Token);
            Check(!cancelled.Completed && cancelled.Pending == 2 && File.Exists(Path.Combine(cancelRoot, "voice-synthesis.json")), "Cancellation did not persist pending synthesis lines.");
        }
        finally
        {
            if (reparse is not null && Directory.Exists(reparse)) Directory.Delete(reparse);
            Directory.Delete(root, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }

    private static SkyrimDialogueManifest Manifest(string firstText, string secondText) => new(SkyrimNpcDialogueSchemas.Manifest,
        new SkyrimDialogueNpcIdentity(new PluginName("Fixture.esp"), new FormId(0x800), new EditorId("FixtureNpc"), "Fixture", true), "en",
        new SkyrimDialogueProfile("Fixture", ["plain"], [], null, null), "FixtureDialogue", 60, null,
        [new SkyrimDialogueLine("line-one", "greeting", "FixtureHello", "Hello", firstText, "Neutral", 0, [], 50, SkyrimDialogueRepeatPolicy.Once, 100, 0, SkyrimDialogueAction.None, null, "reviewed", "Approach", "Stay away"),
         new SkyrimDialogueLine("line-two", "farewell", "FixtureBye", "Goodbye", secondText, "Neutral", 0, [], 50, SkyrimDialogueRepeatPolicy.Once, 100, 0, SkyrimDialogueAction.None, null, "reviewed", "Approach", "Stay away")]);
    private static string NewRoot() { string root = Path.Combine(AppContext.BaseDirectory, "voice-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static Sha256Hash Hash(byte[] bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)));
    private static byte[] JsonVariant(byte[] bytes, string property, string variant)
    {
        JsonObject document = JsonNode.Parse(bytes)!.AsObject();
        if (variant == "missing") document.Remove(property);
        else if (variant == "null") document[property] = null;
        else
        {
            var rows = new JsonArray();
            rows.Add((JsonNode?)null);
            document[property] = rows;
        }
        return Encoding.UTF8.GetBytes(document.ToJsonString());
    }
    private static byte[] OppositePhaseStereoWav()
    {
        byte[] bytes = Program.SyntheticWav(22050, 2, 16, "pcm", 1, 0.7);
        for (int offset = 44; offset < bytes.Length; offset += 4)
        {
            short first = BitConverter.ToInt16(bytes, offset);
            BitConverter.TryWriteBytes(bytes.AsSpan(offset + 2, 2), (short)-first);
        }
        return bytes;
    }
    private static string JsonEscape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class InlineProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }

    private sealed class ReparseMarkerPolicy(IWorkspacePolicy inner) : IWorkspacePolicy
    {
        public ImmutableArray<Diagnostic> Evaluate(WorkspacePath workspaceRoot, WorkspacePath outputRoot) =>
            Refusal(outputRoot) ?? inner.Evaluate(workspaceRoot, outputRoot);
        public ImmutableArray<Diagnostic> EvaluateReadRoot(WorkspacePath workspaceRoot, WorkspacePath readRoot) =>
            Refusal(readRoot) ?? inner.EvaluateReadRoot(workspaceRoot, readRoot);
        private static ImmutableArray<Diagnostic>? Refusal(WorkspacePath path) =>
            path.Value.Contains("virtual-reparse", StringComparison.OrdinalIgnoreCase)
                ? [new Diagnostic(ProtocolV2DiagnosticCodes.ReparsePointRefused, DiagnosticSeverity.Error, "Synthetic product-owned reparse ancestry refusal.")]
                : null;
    }

    private sealed class LeafRefusalPolicy(
        IWorkspacePolicy inner,
        Func<WorkspacePath, bool>? readRefusal = null,
        Func<WorkspacePath, bool>? writeRefusal = null) : IWorkspacePolicy
    {
        public int ReadRefusals { get; private set; }
        public int WriteRefusals { get; private set; }
        public ImmutableArray<Diagnostic> Evaluate(WorkspacePath workspaceRoot, WorkspacePath outputRoot)
        {
            if (writeRefusal?.Invoke(outputRoot) != true) return inner.Evaluate(workspaceRoot, outputRoot);
            WriteRefusals++;
            return Refusal(outputRoot);
        }
        public ImmutableArray<Diagnostic> EvaluateReadRoot(WorkspacePath workspaceRoot, WorkspacePath readRoot)
        {
            if (readRefusal?.Invoke(readRoot) != true) return inner.EvaluateReadRoot(workspaceRoot, readRoot);
            ReadRefusals++;
            return Refusal(readRoot);
        }
        private static ImmutableArray<Diagnostic> Refusal(WorkspacePath path) =>
            [new Diagnostic(ProtocolV2DiagnosticCodes.ReparsePointRefused, DiagnosticSeverity.Error, $"Synthetic exact-leaf refusal for '{path.Value}'.")];
    }

    private sealed class XttsFixtureServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public XttsFixtureServer()
        {
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = LoopAsync();
        }
        public string Endpoint { get; }
        public bool RelativeFolders { get; set; }
        public bool MantellaSpeakers { get; set; }
        public bool UnsupportedApi { get; set; }
        public bool RejectSpeaker { get; set; }
        public bool WrongAudioType { get; set; }
        public string? RedirectSynthesisTo { get; set; }
        public (string Speaker, string Output, string Model)? Folders { get; set; }
        public int FailuresRemaining { get; set; }
        public byte[]? SuccessfulAudio { get; set; }
        public int PostCount { get; private set; }
        public int RequestCount { get; private set; }
        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try { using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token); await RespondAsync(client.GetStream()); }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException && _stop.IsCancellationRequested) { break; }
            }
        }
        private async Task RespondAsync(NetworkStream stream)
        {
            var request = new List<byte>();
            var buffer = new byte[1024];
            while (true)
            {
                int count = await stream.ReadAsync(buffer, _stop.Token);
                if (count == 0) break;
                request.AddRange(buffer.AsSpan(0, count).ToArray());
                if (Encoding.ASCII.GetString(request.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            string head = Encoding.ASCII.GetString(request.ToArray());
            string path = head.Split("\r\n", StringSplitOptions.None)[0].Split(' ')[1];
            RequestCount++;
            byte[] body;
            string type = "application/json";
            string extraHeaders = string.Empty;
            int status = 200;
            if (path == "/openapi.json") body = Encoding.UTF8.GetBytes(UnsupportedApi ? "{\"info\":{\"title\":\"fixture\",\"version\":\"1\"},\"paths\":{}}" : "{\"info\":{\"title\":\"fixture\",\"version\":\"1\"},\"paths\":{\"/tts_to_audio/\":{\"post\":{\"text\":true,\"speaker_wav\":true,\"language\":true}}}}");
            else if (path == "/speakers_list") body = MantellaSpeakers
                ? "{\"en\":{\"speakers\":[\"fixture\",\"second\"]},\"es\":{\"speakers\":[]}}"u8.ToArray()
                : "[\"fixture\"]"u8.ToArray();
            else if (path == "/languages") body = "{\"English\":\"en\"}"u8.ToArray();
            else if (path == "/get_folders")
            {
                (string speaker, string output, string model) = Folders ?? (RelativeFolders
                    ? ("speakers/", "output/", "models/")
                    : ("/mnt/k/voice/speakers", "/mnt/k/voice/output", "/mnt/k/voice/models"));
                body = Encoding.UTF8.GetBytes($"{{\"speaker_folder\":\"{speaker}\",\"output_folder\":\"{output}\",\"model_folder\":\"{model}\"}}");
            }
            else if (path == "/get_models_list") body = "[\"v2.0.2\"]"u8.ToArray();
            else if (path == "/get_tts_settings") body = "{\"temperature\":0.7}"u8.ToArray();
            else if (path == "/tts_to_audio/")
            {
                PostCount++; type = WrongAudioType ? "application/octet-stream" : "audio/wav";
                if (RedirectSynthesisTo is not null) { status = 307; type = "text/plain"; body = []; extraHeaders = $"Location: {RedirectSynthesisTo}\r\n"; }
                else if (RejectSpeaker) { status = 400; type = "text/plain"; body = "Speaker fixture not found"u8.ToArray(); }
                else if (FailuresRemaining-- > 0) { status = 500; type = "text/plain"; body = "busy"u8.ToArray(); }
                else body = SuccessfulAudio ?? WavSampleCodec.WritePcm16(Enumerable.Range(0, 12000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 24000))).ToArray(), 24000);
            }
            else { status = 404; body = "{}"u8.ToArray(); }
            string reason = status == 200 ? "OK" : status == 307 ? "Temporary Redirect" : status == 400 ? "Bad Request" : status == 500 ? "Internal Server Error" : "Not Found";
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\n{extraHeaders}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, _stop.Token); await stream.WriteAsync(body, _stop.Token); await stream.FlushAsync(_stop.Token);
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); try { _loop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } _stop.Dispose(); }
    }
}
