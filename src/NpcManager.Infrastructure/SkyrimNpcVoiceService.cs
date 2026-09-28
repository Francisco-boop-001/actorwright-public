using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class SkyrimNpcVoiceService : ISkyrimNpcVoiceService, IDisposable
{
    private readonly WorkspacePath _workspaceRoot;
    private readonly IWorkspacePolicy _workspacePolicy;
    private readonly XttsServiceDiscovery _discovery;
    private readonly XttsApiServerClient _client;
    private readonly HttpClient? _ownedHttpClient;

    public SkyrimNpcVoiceService(
        WorkspacePath workspaceRoot,
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        IWorkspacePolicy? workspacePolicy = null)
    {
        _workspaceRoot = workspaceRoot;
        _workspacePolicy = workspacePolicy ?? new KOnlyWorkspacePolicy(workspaceRoot, ActorwrightWorkspace.ResolveProtectedRoot(workspaceRoot));
        HttpClient http = httpClient ?? (_ownedHttpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3)
        }) { Timeout = Timeout.InfiniteTimeSpan });
        _discovery = new XttsServiceDiscovery(workspaceRoot, http, _workspacePolicy);
        _client = new XttsApiServerClient(http, retryDelay);
    }

    public ValueTask<SkyrimVoiceServerSetupResult> LoadServerSetupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!_discovery.TryLoadServerSetup(out SkyrimVoiceServerSetup? loaded, diagnostics) || loaded is null)
            return ValueTask.FromResult(new SkyrimVoiceServerSetupResult(false, null, null, diagnostics.ToImmutable()));
        if (!XttsServiceDiscovery.TryNormalizeSetup(loaded, out SkyrimVoiceServerSetup setup, out string error))
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.ServerSetupInvalid, error));
            return ValueTask.FromResult(new SkyrimVoiceServerSetupResult(false, null, null, diagnostics.ToImmutable()));
        }
        return ValueTask.FromResult(new SkyrimVoiceServerSetupResult(true, setup, null, diagnostics.ToImmutable()));
    }

    public async ValueTask<SkyrimVoiceServerSetupResult> CheckServerSetupAsync(SkyrimVoiceServerSetup setup, CancellationToken cancellationToken)
    {
        if (!XttsServiceDiscovery.TryNormalizeSetup(setup, out SkyrimVoiceServerSetup normalizedSetup, out string error))
            return new(false, null, null, [Error(SkyrimNpcVoiceDiagnosticCodes.ServerSetupInvalid, error)]);
        SkyrimVoiceServiceInventory inventory = await _discovery.DiscoverSetupAsync(normalizedSetup, 3, cancellationToken);
        bool succeeded = inventory.SelectedEndpoint is not null;
        return new(succeeded, succeeded ? normalizedSetup : null, inventory, inventory.Diagnostics.AddRange(inventory.Services.SelectMany(item => item.Diagnostics)));
    }

    public async ValueTask<SkyrimVoiceServerSetupResult> SaveServerSetupAsync(SkyrimVoiceServerSetup setup, CancellationToken cancellationToken)
    {
        var existingDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!_discovery.TryLoadServerSetup(out _, existingDiagnostics))
            return new(false, null, null, existingDiagnostics.ToImmutable());
        SkyrimVoiceServerSetupResult check = await CheckServerSetupAsync(setup, cancellationToken);
        if (!check.Succeeded || check.Settings is null) return check;
        var diagnostics = existingDiagnostics;
        diagnostics.AddRange(check.Diagnostics);
        if (!await SaveSetupConfigurationAsync(check.Settings, diagnostics, cancellationToken))
            return new(false, null, check.Inventory, diagnostics.ToImmutable());
        return check with { Diagnostics = diagnostics.ToImmutable() };
    }

    private async Task<bool> SaveSetupConfigurationAsync(SkyrimVoiceServerSetup setup, ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_workspaceRoot.Value, "voice-services.json");
        ImmutableArray<Diagnostic> readAdmission = _workspacePolicy.EvaluateReadRoot(_workspaceRoot, new WorkspacePath(path));
        diagnostics.AddRange(readAdmission);
        if (HasErrors(readAdmission)) return false;
        JsonObject document;
        try
        {
            document = File.Exists(path) ? JsonNode.Parse(await File.ReadAllBytesAsync(path, cancellationToken))?.AsObject() ?? throw new InvalidDataException("The configuration root must be an object.") : [];
            JsonArray endpoints = document["endpoints"] switch
            {
                null => new JsonArray(),
                JsonArray array => array,
                _ => throw new InvalidDataException("The endpoints property must be an array.")
            };
            bool present = endpoints.Any(node => node is JsonValue value && value.TryGetValue<string>(out string? endpoint) &&
                XttsServiceDiscovery.TryNormalizeEndpoint(endpoint, out string normalized, out _) && string.Equals(normalized, setup.Endpoint, StringComparison.OrdinalIgnoreCase));
            if (!present) endpoints.Add(setup.Endpoint);
            if (document["endpoints"] is null) document["endpoints"] = endpoints;
            document["defaultEndpoint"] = setup.Endpoint;

            JsonArray setups = document["setups"] switch
            {
                null => new JsonArray(),
                JsonArray array => array,
                _ => throw new InvalidDataException("The setups property must be an array.")
            };
            int replaceAt = -1;
            for (int index = 0; index < setups.Count; index++)
            {
                if (setups[index] is not JsonObject current || current["endpoint"] is not JsonValue endpointValue || !endpointValue.TryGetValue<string>(out string? endpoint))
                    throw new InvalidDataException("Every saved setup must be an object with a string endpoint.");
                if (XttsServiceDiscovery.TryNormalizeEndpoint(endpoint, out string normalized, out _) && string.Equals(normalized, setup.Endpoint, StringComparison.OrdinalIgnoreCase)) replaceAt = index;
            }
            JsonObject saved = replaceAt >= 0 ? (JsonObject)setups[replaceAt]! : new JsonObject();
            saved["endpoint"] = setup.Endpoint;
            saved["platform"] = setup.Platform;
            saved["serverFolder"] = setup.ServerFolder;
            saved["outputFolder"] = setup.OutputFolder;
            if (replaceAt < 0) setups.Add(saved);
            if (document["setups"] is null) document["setups"] = setups;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or InvalidOperationException)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid, $"voice-services.json is invalid: {exception.Message}"));
            return false;
        }

        if (!TryAdmitOutputLeaf(path, diagnostics)) return false;
        string temporary = Path.Combine(_workspaceRoot.Value, $".voice-services.{Guid.NewGuid():N}.tmp");
        if (!TryAdmitOutputLeaf(temporary, diagnostics)) return false;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            await WriteNewAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async ValueTask<SkyrimVoiceServiceInventory> DiscoverAsync(
        string? endpointOverride,
        int timeoutSeconds,
        WorkspacePath? outputPath,
        CancellationToken cancellationToken)
    {
        ImmutableArray<Diagnostic> admission = outputPath is null
            ? _workspacePolicy.EvaluateReadRoot(_workspaceRoot, _workspaceRoot)
            : _workspacePolicy.Evaluate(_workspaceRoot, outputPath.Value);
        if (HasErrors(admission))
            return new SkyrimVoiceServiceInventory(SkyrimNpcVoiceSchemas.Services, null, [], admission, SkyrimNpcVoiceDocumentCodec.UtcNow());
        if (outputPath is not null && (File.Exists(outputPath.Value.Value) || Directory.Exists(outputPath.Value.Value)))
            return new SkyrimVoiceServiceInventory(SkyrimNpcVoiceSchemas.Services, null, [],
                [Error(SkyrimNpcVoiceDiagnosticCodes.OutputExists, "Discovery output must be a new file inside the workspace.")], SkyrimNpcVoiceDocumentCodec.UtcNow());

        SkyrimVoiceServiceInventory inventory = await _discovery.DiscoverAsync(endpointOverride, timeoutSeconds, cancellationToken);
        if (outputPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath.Value.Value)!);
            await WriteNewAsync(outputPath.Value.Value, SkyrimNpcVoiceDocumentCodec.Serialize(inventory), cancellationToken);
        }
        return inventory;
    }

    public async ValueTask<SkyrimVoiceSampleImportResult> ImportSampleAsync(
        SkyrimVoiceSampleImportRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(_workspacePolicy.EvaluateReadRoot(_workspaceRoot, request.Sample));
        diagnostics.AddRange(_workspacePolicy.Evaluate(_workspaceRoot, request.OutputRoot));
        if (HasErrors(diagnostics))
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SamplePathRefused, "The voice sample and output must remain inside the admitted workspace."));
        if (!HasErrors(diagnostics) && (!File.Exists(request.Sample.Value) ||
                 !string.Equals(Path.GetExtension(request.Sample.Value), ".wav", StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SampleInvalid, "The sample must be an existing .wav file."));
        if (!HasErrors(diagnostics) && (Directory.Exists(request.OutputRoot.Value) || File.Exists(request.OutputRoot.Value)))
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.OutputExists, "The voice output already exists."));
        if (diagnostics.Count > 0) return FailedImport(diagnostics);

        byte[] originalBytes;
        WavDecodedAudio decoded;
        try
        {
            originalBytes = await File.ReadAllBytesAsync(request.Sample.Value, cancellationToken);
            decoded = WavSampleCodec.Decode(originalBytes);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SampleInvalid, exception.Message));
            return FailedImport(diagnostics);
        }
        SkyrimVoiceSampleAudio originalAudio = WavSampleCodec.Measure(decoded);
        if (originalAudio.PeakDbfs <= -90 || originalAudio.RmsDbfs <= -90)
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SampleSilent, "The voice sample is silent."));
        if (originalAudio.DurationSeconds < 0.33)
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SampleTooShort, "The voice sample is shorter than the backend minimum of 0.33 seconds."));
        if (originalAudio.DurationSeconds > 60)
            diagnostics.Add(new Diagnostic(SkyrimNpcVoiceDiagnosticCodes.SampleTooLong, DiagnosticSeverity.Warning, "The voice sample exceeds the 60 second advisory length."));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return FailedImport(diagnostics);

        byte[] normalizedBytes = WavSampleCodec.Normalize(decoded);
        SkyrimVoiceSampleAudio normalizedAudio = WavSampleCodec.Measure(WavSampleCodec.Decode(normalizedBytes));
        if (normalizedAudio.PeakDbfs <= -90 || normalizedAudio.RmsDbfs <= -90)
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SampleSilent, "The normalized mono voice sample is silent."));
        if (normalizedAudio.DurationSeconds < 0.33)
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SampleTooShort, "The normalized voice sample is shorter than the backend minimum of 0.33 seconds."));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return FailedImport(diagnostics);
        Sha256Hash originalHash = Hash(originalBytes);
        Sha256Hash normalizedHash = Hash(normalizedBytes);
        string normalizedPath = Path.Combine(request.OutputRoot.Value, $"ref-{normalizedHash.Value[..16].ToLowerInvariant()}.wav");
        var authority = new SkyrimVoiceSampleAuthority(
            SkyrimNpcVoiceSchemas.Sample, request.Plugin, request.FormId, request.EditorId, request.VoicePrefix,
            Path.GetFullPath(request.Sample.Value), originalHash, originalAudio,
            normalizedPath, normalizedHash, normalizedAudio, SkyrimNpcVoiceDocumentCodec.UtcNow());
        byte[] authorityBytes = SkyrimNpcVoiceDocumentCodec.Serialize(authority);
        string authorityPath = Path.Combine(request.OutputRoot.Value, "voice-sample.json");
        bool normalizedAdmitted = TryAdmitOutputLeaf(normalizedPath, diagnostics);
        bool authorityAdmitted = TryAdmitOutputLeaf(authorityPath, diagnostics);
        if (!normalizedAdmitted || !authorityAdmitted)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SamplePathRefused, "A voice authority output leaf was refused by the workspace policy."));
            return FailedImport(diagnostics);
        }
        Directory.CreateDirectory(request.OutputRoot.Value);
        await WriteNewAsync(normalizedPath, normalizedBytes, cancellationToken);
        await WriteNewAsync(authorityPath, authorityBytes, cancellationToken);
        return new SkyrimVoiceSampleImportResult(true, authority, new WorkspacePath(authorityPath), Hash(authorityBytes), diagnostics.ToImmutable());
    }

    public async ValueTask<SkyrimVoiceSynthesisResult> SynthesizeAsync(
        SkyrimVoiceSynthesisRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!ValidateInput(request.Manifest, request.ManifestSha256, out byte[] manifestBytes, diagnostics) ||
            !ValidateInput(request.SampleAuthority, request.SampleAuthoritySha256, out byte[] authorityBytes, diagnostics))
            return FailedSynthesis(diagnostics);
        SkyrimDialogueManifest manifest;
        SkyrimVoiceSampleAuthority authority;
        try
        {
            manifest = SkyrimNpcVoiceDocumentCodec.Parse<SkyrimDialogueManifest>(manifestBytes);
            authority = SkyrimNpcVoiceDocumentCodec.Parse<SkyrimVoiceSampleAuthority>(authorityBytes);
            if (manifest.Schema != SkyrimNpcDialogueSchemas.Manifest || authority.Schema != SkyrimNpcVoiceSchemas.Sample)
                throw new InvalidDataException("A voice input document has the wrong schema identifier.");
            if (ValidateManifestShape(manifest) is { } manifestShapeError)
                throw new InvalidDataException(manifestShapeError);
            if (ValidateAuthorityShape(authority) is { } authorityShapeError)
                throw new InvalidDataException(authorityShapeError);
            if (authority.Plugin != manifest.Npc.Plugin || authority.FormId != manifest.Npc.FormId ||
                authority.EditorId != manifest.Npc.EditorId || authority.VoicePrefix != manifest.Npc.VoicePrefix)
                throw new InvalidDataException("The voice sample authority belongs to another NPC identity.");
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid, exception.Message));
            return FailedSynthesis(diagnostics);
        }
        diagnostics.AddRange(_workspacePolicy.Evaluate(_workspaceRoot, request.OutputRoot));
        if (request.Manifest == request.OutputRoot || request.SampleAuthority == request.OutputRoot)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SamplePathRefused, "Synthesis output must remain inside the workspace and separate from inputs."));
        }
        if (HasErrors(diagnostics)) return FailedSynthesis(diagnostics);
        string ledgerPath = Path.Combine(request.OutputRoot.Value, "voice-synthesis.json");
        if (!TryAdmitOutputLeaf(ledgerPath, diagnostics)) return FailedSynthesis(diagnostics);
        if (request.Resume)
        {
            ImmutableArray<Diagnostic> ledgerAdmission = _workspacePolicy.EvaluateReadRoot(request.OutputRoot, new WorkspacePath(ledgerPath));
            diagnostics.AddRange(ledgerAdmission);
            if (HasErrors(ledgerAdmission)) return FailedSynthesis(diagnostics);
        }
        if (File.Exists(request.OutputRoot.Value))
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.OutputExists, "The synthesis output path is an existing file."));
            return FailedSynthesis(diagnostics);
        }
        if (Directory.Exists(request.OutputRoot.Value) && !request.Resume)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.OutputExists, "The synthesis output already exists; pass --resume to reuse it."));
            return FailedSynthesis(diagnostics);
        }
        foreach (SkyrimDialogueLine line in manifest.Lines)
        {
            if (!SafeLineId(line.Id))
            {
                diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid, $"Line id '{line.Id}' is not a safe file name."));
                return FailedSynthesis(diagnostics);
            }
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid, $"Line '{line.Id}' has empty text."));
                return FailedSynthesis(diagnostics);
            }
            if (!TryAdmitOutputLeaf(Path.Combine(request.OutputRoot.Value, line.Id + ".wav"), diagnostics))
                return FailedSynthesis(diagnostics);
        }

        WorkspacePath normalizedPath;
        try
        {
            normalizedPath = new WorkspacePath(authority.NormalizedPath);
            diagnostics.AddRange(_workspacePolicy.EvaluateReadRoot(_workspaceRoot, normalizedPath));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SamplePathRefused, exception.Message));
            return FailedSynthesis(diagnostics);
        }
        if (HasErrors(diagnostics))
            return FailedSynthesis(diagnostics);
        if (!File.Exists(normalizedPath.Value) || Hash(await File.ReadAllBytesAsync(normalizedPath.Value, cancellationToken)) != authority.NormalizedSha256)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.DocumentHashMismatch, "The normalized sample bytes no longer match voice-sample.json."));
            return FailedSynthesis(diagnostics);
        }

        SkyrimVoiceSynthesisManifest? prior = null;
        if (request.Resume && File.Exists(ledgerPath))
        {
            try
            {
                prior = SkyrimNpcVoiceDocumentCodec.Parse<SkyrimVoiceSynthesisManifest>(await File.ReadAllBytesAsync(ledgerPath, cancellationToken));
                if (ValidateSynthesisShape(prior) is { } priorShapeError)
                    throw new InvalidDataException(priorShapeError);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
            {
                diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid, $"The resume ledger is invalid: {exception.Message}"));
                return FailedSynthesis(diagnostics);
            }
        }
        if (prior is not null)
        {
            foreach (SkyrimVoiceSynthesisLine priorLine in prior.Lines)
            {
                if (priorLine?.OutputFile is null) continue;
                if (!TryAdmitRelativeOutput(request.OutputRoot, priorLine.OutputFile, diagnostics))
                    return FailedSynthesis(diagnostics);
            }
        }

        SkyrimVoiceServiceInventory inventory = await _discovery.DiscoverAsync(request.Endpoint, 3, cancellationToken);
        if (inventory.SelectedEndpoint is null)
            return FailedSynthesis(diagnostics.AddRangeAndReturn(inventory.Diagnostics));
        SkyrimVoiceServiceDescriptor selected = inventory.Services.Single(item => item.Endpoint == inventory.SelectedEndpoint);
        string speakerReference;
        try { speakerReference = selected.Platform == "windows" ? normalizedPath.Value : ToWslPath(normalizedPath.Value); }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SamplePathRefused, exception.Message));
            return FailedSynthesis(diagnostics);
        }
        Sha256Hash settingsHash = Hash(System.Text.Encoding.UTF8.GetBytes(selected.SettingsJson ?? "null"));
        string backendIdentity = selected.Fingerprint ?? throw new InvalidDataException("Compatible XTTS service did not report an identity fingerprint.");

        Directory.CreateDirectory(request.OutputRoot.Value);
        string language = request.Language ?? manifest.Language;
        var lines = manifest.Lines.Select(line => InitialLine(line, language, prior, request, authority, settingsHash, backendIdentity, selected.Endpoint, speakerReference)).ToArray();
        int budget = request.MaxLines ?? int.MaxValue;
        int attempted = 0;
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].Status == SkyrimVoiceLineStatus.Skipped || attempted >= budget) continue;
            if (cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.Cancelled, "Synthesis was cancelled with remaining lines pending."));
                Sha256Hash? cancelledLedgerHash = await PersistAsync(ledgerPath, BuildManifest(request, authority, selected, settingsHash, speakerReference, lines), diagnostics, CancellationToken.None);
                return Result(false, lines, ledgerPath, cancelledLedgerHash, diagnostics);
            }
            attempted++;
            progress?.Report(lines[index].LineId);
            try
            {
                (byte[] wav, SkyrimVoiceSampleAudio audio, double elapsed) = await _client.SynthesizeAsync(new Uri(selected.Endpoint), lines[index].Text, speakerReference, language, cancellationToken);
                string outputName = lines[index].LineId + ".wav";
                string outputPath = Path.Combine(request.OutputRoot.Value, outputName);
                if (!await ReplaceOwnFileAsync(outputPath, wav, diagnostics, cancellationToken))
                    return FailedSynthesis(diagnostics);
                lines[index] = lines[index] with { Status = SkyrimVoiceLineStatus.Succeeded, OutputFile = outputName, OutputSha256 = Hash(wav), DurationSeconds = audio.DurationSeconds, SampleRate = audio.SampleRate, ElapsedSeconds = elapsed, FailureCode = null, FailureMessage = null };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.Cancelled, "Synthesis was cancelled with remaining lines pending."));
                Sha256Hash? cancelledLedgerHash = await PersistAsync(ledgerPath, BuildManifest(request, authority, selected, settingsHash, speakerReference, lines), diagnostics, CancellationToken.None);
                return Result(false, lines, ledgerPath, cancelledLedgerHash, diagnostics);
            }
            catch (XttsRequestException exception)
            {
                lines[index] = lines[index] with { Status = SkyrimVoiceLineStatus.Failed, FailureCode = exception.Code, FailureMessage = exception.Message };
                diagnostics.Add(Error(exception.Code, exception.Message));
            }
            if (await PersistAsync(ledgerPath, BuildManifest(request, authority, selected, settingsHash, speakerReference, lines), diagnostics, cancellationToken) is null)
                return FailedSynthesis(diagnostics);
        }
        SkyrimVoiceSynthesisManifest final = BuildManifest(request, authority, selected, settingsHash, speakerReference, lines);
        Sha256Hash? finalLedgerHash = await PersistAsync(ledgerPath, final, diagnostics, cancellationToken);
        if (finalLedgerHash is null)
            return FailedSynthesis(diagnostics);
        return Result(lines.All(line => line.Status is SkyrimVoiceLineStatus.Succeeded or SkyrimVoiceLineStatus.Skipped), lines, ledgerPath, finalLedgerHash, diagnostics);
    }

    private bool ValidateInput(WorkspacePath path, Sha256Hash expected, out byte[] bytes, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        bytes = [];
        ImmutableArray<Diagnostic> admission = _workspacePolicy.EvaluateReadRoot(_workspaceRoot, path);
        diagnostics.AddRange(admission);
        if (HasErrors(admission)) return false;
        if (!File.Exists(path.Value))
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SamplePathRefused, $"Input '{path.Value}' must be an existing workspace file."));
            return false;
        }
        bytes = File.ReadAllBytes(path.Value);
        if (Hash(bytes) == expected) return true;
        diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.DocumentHashMismatch, $"Input '{path.Value}' does not match its supplied SHA-256."));
        return false;
    }

    private bool TryAdmitRelativeOutput(WorkspacePath outputRoot, string relativePath, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (Path.IsPathFullyQualified(relativePath))
                throw new InvalidDataException("A resumed synthesis output filename must be relative to its output root.");
            var resolved = new WorkspacePath(Path.Combine(outputRoot.Value, relativePath));
            ImmutableArray<Diagnostic> admission = _workspacePolicy.EvaluateReadRoot(outputRoot, resolved);
            diagnostics.AddRange(admission);
            if (!resolved.IsUnder(outputRoot) || HasErrors(admission))
                throw new InvalidDataException("A resumed synthesis output filename must remain inside its output root.");
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or NotSupportedException)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SamplePathRefused, exception.Message));
            return false;
        }
    }

    private bool TryAdmitOutputLeaf(string path, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            ImmutableArray<Diagnostic> admission = _workspacePolicy.Evaluate(_workspaceRoot, new WorkspacePath(path));
            diagnostics.AddRange(admission);
            return !HasErrors(admission);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.SamplePathRefused, exception.Message));
            return false;
        }
    }

    private static string? ValidateManifestShape(SkyrimDialogueManifest manifest)
    {
        if (manifest.Npc is null || string.IsNullOrWhiteSpace(manifest.Npc.Plugin.Value) || string.IsNullOrWhiteSpace(manifest.Npc.VoicePrefix))
            return "The dialogue manifest requires a complete NPC identity.";
        if (string.IsNullOrWhiteSpace(manifest.Language))
            return "The dialogue manifest requires a language.";
        if (manifest.Lines.IsDefault)
            return "The dialogue manifest requires a lines array; an empty array is accepted.";
        if (manifest.Lines.Any(line => line is null))
            return "The dialogue manifest lines array cannot contain null rows.";
        return null;
    }

    private static string? ValidateAuthorityShape(SkyrimVoiceSampleAuthority authority)
    {
        if (string.IsNullOrWhiteSpace(authority.Plugin.Value) || string.IsNullOrWhiteSpace(authority.VoicePrefix) ||
            string.IsNullOrWhiteSpace(authority.OriginalPath) || string.IsNullOrWhiteSpace(authority.OriginalSha256.Value) || authority.Original is null ||
            string.IsNullOrWhiteSpace(authority.NormalizedPath) || string.IsNullOrWhiteSpace(authority.NormalizedSha256.Value) || authority.Normalized is null)
            return "The voice sample authority requires complete NPC, original-sample, and normalized-sample bindings.";
        return null;
    }

    private static string? ValidateSynthesisShape(SkyrimVoiceSynthesisManifest manifest)
    {
        if (manifest.Schema != SkyrimNpcVoiceSchemas.Synthesis || string.IsNullOrWhiteSpace(manifest.DialogueManifestPath) ||
            string.IsNullOrWhiteSpace(manifest.DialogueManifestSha256.Value) || string.IsNullOrWhiteSpace(manifest.SampleAuthorityPath) ||
            string.IsNullOrWhiteSpace(manifest.SampleAuthoritySha256.Value) || string.IsNullOrWhiteSpace(manifest.SampleSha256.Value) ||
            string.IsNullOrWhiteSpace(manifest.Endpoint) || string.IsNullOrWhiteSpace(manifest.BackendIdentity) ||
            string.IsNullOrWhiteSpace(manifest.SettingsSha256.Value) || string.IsNullOrWhiteSpace(manifest.SpeakerReference))
            return "The resume ledger requires complete manifest, sample, backend, and settings bindings.";
        if (manifest.Lines.IsDefault)
            return "The resume ledger requires a lines array; an empty array is accepted.";
        if (manifest.Lines.Any(line => line is null))
            return "The resume ledger lines array cannot contain null rows.";
        if (manifest.Lines.Any(line => string.IsNullOrWhiteSpace(line.LineId) || string.IsNullOrWhiteSpace(line.Text) ||
            string.IsNullOrWhiteSpace(line.Language) || string.IsNullOrWhiteSpace(line.TextSha256.Value)))
            return "Every resume ledger line requires its id, text, language, and text hash.";
        return null;
    }

    private static SkyrimVoiceSynthesisLine InitialLine(SkyrimDialogueLine line, string language, SkyrimVoiceSynthesisManifest? prior, SkyrimVoiceSynthesisRequest request, SkyrimVoiceSampleAuthority authority, Sha256Hash settingsHash, string backendIdentity, string endpoint, string speakerReference)
    {
        Sha256Hash textHash = Hash(System.Text.Encoding.UTF8.GetBytes(line.Text));
        SkyrimVoiceSynthesisLine? old = prior?.Lines.FirstOrDefault(item => item.LineId == line.Id);
        if (old is not null && (old.Status is SkyrimVoiceLineStatus.Succeeded or SkyrimVoiceLineStatus.Skipped) && old.TextSha256 == textHash && old.Language == language &&
            prior!.SampleAuthoritySha256 == request.SampleAuthoritySha256 &&
            prior.SampleSha256 == authority.NormalizedSha256 && prior.SettingsSha256 == settingsHash && prior.BackendIdentity == backendIdentity &&
            prior.Endpoint == endpoint && prior.SpeakerReference == speakerReference && old.OutputFile is not null)
        {
            string path = Path.Combine(request.OutputRoot.Value, old.OutputFile);
            if (File.Exists(path) && old.OutputSha256 is not null && Hash(File.ReadAllBytes(path)) == old.OutputSha256)
                return old with { Status = SkyrimVoiceLineStatus.Skipped };
        }
        return new SkyrimVoiceSynthesisLine(line.Id, line.Text, language, textHash, SkyrimVoiceLineStatus.Pending, null, null, null, null, null, null, null);
    }

    private static SkyrimVoiceSynthesisManifest BuildManifest(SkyrimVoiceSynthesisRequest request, SkyrimVoiceSampleAuthority authority, SkyrimVoiceServiceDescriptor service, Sha256Hash settingsHash, string speakerReference, SkyrimVoiceSynthesisLine[] lines) =>
        new(SkyrimNpcVoiceSchemas.Synthesis, request.Manifest.Value, request.ManifestSha256, request.SampleAuthority.Value, request.SampleAuthoritySha256,
            authority.NormalizedSha256, service.Endpoint, service.Fingerprint!, settingsHash, speakerReference, lines.ToImmutableArray(), SkyrimNpcVoiceDocumentCodec.UtcNow());

    private async Task<Sha256Hash?> PersistAsync(string path, SkyrimVoiceSynthesisManifest manifest, ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken token)
    {
        byte[] bytes = SkyrimNpcVoiceDocumentCodec.Serialize(manifest);
        return await ReplaceOwnFileAsync(path, bytes, diagnostics, token) ? Hash(bytes) : null;
    }
    private async Task<bool> ReplaceOwnFileAsync(string path, byte[] bytes, ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken token)
    {
        if (!TryAdmitOutputLeaf(path, diagnostics)) return false;
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        if (!TryAdmitOutputLeaf(temporary, diagnostics)) return false;
        try
        {
            await WriteNewAsync(temporary, bytes, token);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
    private static string ToWslPath(string path)
    {
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (root is null || root.Length < 2 || root[1] != ':') throw new InvalidDataException("The normalized sample must be on a Windows drive that WSL can mount.");
        return $"/mnt/{char.ToLowerInvariant(root[0])}/{full[root.Length..].Replace('\\', '/')}";
    }
    private static bool SafeLineId(string id) => !string.IsNullOrWhiteSpace(id) && id != "." && id != ".." && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.Contains('/') && !id.Contains('\\');
    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)));
    private static Diagnostic Error(string code, string message) => new(code, DiagnosticSeverity.Error, message);
    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static SkyrimVoiceSampleImportResult FailedImport(ImmutableArray<Diagnostic>.Builder diagnostics) => new(false, null, null, null, diagnostics.ToImmutable());
    private static SkyrimVoiceSynthesisResult FailedSynthesis(ImmutableArray<Diagnostic>.Builder diagnostics) => new(false, 0, 0, 0, 0, null, null, diagnostics.ToImmutable());
    private static SkyrimVoiceSynthesisResult Result(bool completed, SkyrimVoiceSynthesisLine[] lines, string path, Sha256Hash? manifestSha256, ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(completed, lines.Count(x => x.Status == SkyrimVoiceLineStatus.Succeeded), lines.Count(x => x.Status == SkyrimVoiceLineStatus.Failed), lines.Count(x => x.Status == SkyrimVoiceLineStatus.Skipped), lines.Count(x => x.Status == SkyrimVoiceLineStatus.Pending), new WorkspacePath(path), manifestSha256, diagnostics.ToImmutable());
    public void Dispose()
    {
        _client.Dispose();
        _ownedHttpClient?.Dispose();
    }
}

internal static class VoiceDiagnosticBuilderExtensions
{
    public static ImmutableArray<Diagnostic>.Builder AddRangeAndReturn(this ImmutableArray<Diagnostic>.Builder builder, IEnumerable<Diagnostic> diagnostics)
    {
        builder.AddRange(diagnostics);
        return builder;
    }
}
