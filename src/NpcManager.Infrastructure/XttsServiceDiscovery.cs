using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class XttsServiceDiscovery(WorkspacePath workspaceRoot, HttpClient http, IWorkspacePolicy? workspacePolicy = null)
{
    private const string DefaultEndpoint = "http://127.0.0.1:8020";
    private readonly IWorkspacePolicy _workspacePolicy = workspacePolicy ?? new KOnlyWorkspacePolicy(workspaceRoot, ActorwrightWorkspace.ResolveProtectedRoot(workspaceRoot));

    public ValueTask<SkyrimVoiceServiceInventory> DiscoverAsync(string? endpointOverride, int timeoutSeconds, CancellationToken cancellationToken) =>
        DiscoverCoreAsync(endpointOverride, null, timeoutSeconds, cancellationToken);

    internal ValueTask<SkyrimVoiceServiceInventory> DiscoverSetupAsync(SkyrimVoiceServerSetup setup, int timeoutSeconds, CancellationToken cancellationToken) =>
        DiscoverCoreAsync(setup.Endpoint, setup, timeoutSeconds, cancellationToken);

    internal bool TryLoadServerSetup(out SkyrimVoiceServerSetup? setup, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        setup = null;
        if (!TryLoadConfiguration(out VoiceConfiguration config, diagnostics)) return false;
        string endpoint = config.DefaultEndpoint ?? config.Endpoints.FirstOrDefault() ?? DefaultEndpoint;
        setup = config.Setups.FirstOrDefault(item => EndpointEquals(item.Endpoint, endpoint)) ?? new(endpoint, "wsl", string.Empty, string.Empty);
        return true;
    }

    private async ValueTask<SkyrimVoiceServiceInventory> DiscoverCoreAsync(string? endpointOverride, SkyrimVoiceServerSetup? transientSetup, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var candidates = new List<(string Endpoint, string Source, SkyrimVoiceServerSetup? Setup)>();
        if (transientSetup is not null)
            candidates.Add((transientSetup.Endpoint, "override", transientSetup));
        else if (TryLoadConfiguration(out VoiceConfiguration config, diagnostics))
        {
            IEnumerable<string> endpoints = string.IsNullOrWhiteSpace(endpointOverride)
                ? config.DefaultEndpoint is null ? config.Endpoints.DefaultIfEmpty(DefaultEndpoint) : [config.DefaultEndpoint]
                : [endpointOverride];
            string source = string.IsNullOrWhiteSpace(endpointOverride) ? (config.Endpoints.Length == 0 && config.DefaultEndpoint is null ? "default" : "config") : "override";
            foreach (string endpoint in endpoints)
                candidates.Add((endpoint, source, config.Setups.FirstOrDefault(item => EndpointEquals(item.Endpoint, endpoint))));
        }

        var services = ImmutableArray.CreateBuilder<SkyrimVoiceServiceDescriptor>();
        foreach ((string endpoint, string source, SkyrimVoiceServerSetup? setup) in candidates)
            services.Add(await ProbeAsync(endpoint, source, setup, Math.Clamp(timeoutSeconds, 1, 30), cancellationToken));
        SkyrimVoiceServiceDescriptor[] compatible = services.Where(item => item.Compatible && item.StorageVerdict == "safe").ToArray();
        string? selected = compatible.Length == 1 ? compatible[0].Endpoint : null;
        if (compatible.Length > 1) diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.ServiceAmbiguous, "More than one compatible storage-safe XTTS endpoint was found; pass --endpoint."));
        else if (compatible.Length == 0) diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.ServiceNone, "No compatible storage-safe XTTS endpoint was found."));
        return new(SkyrimNpcVoiceSchemas.Services, selected, services.ToImmutable(), diagnostics.ToImmutable(), SkyrimNpcVoiceDocumentCodec.UtcNow());
    }

    private bool TryLoadConfiguration(out VoiceConfiguration configuration, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        configuration = new([], [], null);
        string path = Path.Combine(workspaceRoot.Value, "voice-services.json");
        ImmutableArray<Diagnostic> admission = _workspacePolicy.EvaluateReadRoot(workspaceRoot, new WorkspacePath(path));
        diagnostics.AddRange(admission);
        if (HasErrors(admission) || !File.Exists(path)) return !HasErrors(admission);
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The configuration root must be an object.");
            var endpoints = ImmutableArray.CreateBuilder<string>();
            if (document.RootElement.TryGetProperty("endpoints", out JsonElement endpointValues))
            {
                if (endpointValues.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The endpoints property must be an array of strings.");
                foreach (JsonElement value in endpointValues.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidDataException("Every configured endpoint must be a non-empty string.");
                    if (!TryNormalizeEndpoint(value.GetString()!, out string normalized, out string endpointError)) throw new InvalidDataException(endpointError);
                    endpoints.Add(normalized);
                }
            }
            var setups = ImmutableArray.CreateBuilder<SkyrimVoiceServerSetup>();
            string? defaultEndpoint = null;
            if (document.RootElement.TryGetProperty("defaultEndpoint", out JsonElement defaultValue))
            {
                if (defaultValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(defaultValue.GetString()))
                    throw new InvalidDataException("The defaultEndpoint property must be a non-empty string.");
                if (!TryNormalizeEndpoint(defaultValue.GetString()!, out defaultEndpoint, out string endpointError)) throw new InvalidDataException(endpointError);
            }
            if (document.RootElement.TryGetProperty("setups", out JsonElement setupValues))
            {
                if (setupValues.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The setups property must be an array.");
                foreach (JsonElement value in setupValues.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Object || StringProperty(value, "endpoint") is not { } endpoint || StringProperty(value, "platform") is not { } platform ||
                        StringProperty(value, "serverFolder") is not { } serverFolder || StringProperty(value, "outputFolder") is not { } outputFolder)
                        throw new InvalidDataException("Every setup requires string endpoint, platform, serverFolder, and outputFolder properties.");
                    if (!TryNormalizeSetup(new(endpoint, platform, serverFolder, outputFolder), out SkyrimVoiceServerSetup setup, out string setupError))
                        throw new InvalidDataException(setupError);
                    if (setups.Any(item => EndpointEquals(item.Endpoint, setup.Endpoint)))
                        throw new InvalidDataException($"Only one setup may be saved for endpoint '{setup.Endpoint}'.");
                    setups.Add(setup);
                }
            }
            configuration = new(endpoints.ToImmutable(), setups.ToImmutable(), defaultEndpoint);
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ArgumentException)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.DocumentInvalid, $"voice-services.json is invalid: {exception.Message}"));
            return false;
        }
    }

    private async ValueTask<SkyrimVoiceServiceDescriptor> ProbeAsync(string endpointText, string source, SkyrimVoiceServerSetup? configuredSetup, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!TryNormalizeEndpoint(endpointText, out string normalizedEndpoint, out string endpointError))
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.ServiceRequestRejected, endpointError));
            return Descriptor(endpointText, source, "wsl", false, "refused", endpointError, diagnostics.ToImmutable());
        }
        if (!TryNormalizeSetup(configuredSetup ?? new(normalizedEndpoint, "wsl", string.Empty, string.Empty), out SkyrimVoiceServerSetup setup, out string setupError) || !EndpointEquals(setup.Endpoint, normalizedEndpoint))
        {
            string message = setupError.Length == 0 ? "The saved setup belongs to another endpoint." : setupError;
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.ServerSetupInvalid, message));
            return Descriptor(normalizedEndpoint, source, configuredSetup?.Platform ?? "wsl", false, "refused", message, diagnostics.ToImmutable());
        }
        var endpoint = new Uri(normalizedEndpoint);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            JsonElement openApi = await GetJsonAsync(endpoint, "/openapi.json", timeout.Token);
            JsonElement speakers = await GetJsonAsync(endpoint, "/speakers_list", timeout.Token);
            JsonElement languages = await GetJsonAsync(endpoint, "/languages", timeout.Token);
            JsonElement folders = await GetJsonAsync(endpoint, "/get_folders", timeout.Token);
            JsonElement models = await GetJsonAsync(endpoint, "/get_models_list", timeout.Token);
            JsonElement settings = await GetJsonAsync(endpoint, "/get_tts_settings", timeout.Token);
            JsonElement languageMap = languages.ValueKind == JsonValueKind.Object && languages.TryGetProperty("languages", out JsonElement nestedLanguages) ? nestedLanguages : languages;
            bool speakersValid = TrySpeakerCount(speakers, out int speakerCount);
            bool api = HasRequiredTtsOperation(openApi) && speakersValid && languageMap.ValueKind == JsonValueKind.Object &&
                languageMap.TryGetProperty("English", out JsonElement english) && english.ValueKind == JsonValueKind.String && english.GetString() == "en";
            if (!api) diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.ServiceUnsupportedApi, "The endpoint does not expose the required XTTS contract."));
            string? speakerFolder = StringProperty(folders, "speaker_folder", "speakerFolder");
            string? outputFolder = StringProperty(folders, "output_folder", "outputFolder");
            string? modelFolder = StringProperty(folders, "model_folder", "modelFolder");
            (string Verdict, string Detail) storage = setup.Platform == "windows"
                ? WindowsStorageVerdict(setup, ref speakerFolder, ref outputFolder, ref modelFolder)
                : StorageVerdict(speakerFolder, outputFolder, modelFolder);
            if (storage.Verdict != "safe") diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.ServiceStorageRefused, storage.Detail));
            string identity = string.Join('\n', normalizedEndpoint, setup.Platform, setup.ServerFolder, setup.OutputFolder, speakerFolder, outputFolder, modelFolder,
                openApi.GetRawText(), models.GetRawText(), settings.GetRawText());
            return new SkyrimVoiceServiceDescriptor(normalizedEndpoint, source, api,
                StringProperty(openApi, "title") ?? (openApi.TryGetProperty("info", out JsonElement info) ? StringProperty(info, "title") : null),
                openApi.TryGetProperty("info", out info) ? StringProperty(info, "version") : null,
                Strings(models), LanguageCodes(languageMap), speakerCount, speakerFolder, outputFolder, modelFolder, settings.GetRawText(), storage.Verdict, storage.Detail,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))), diagnostics.ToImmutable()) { Platform = setup.Platform };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidDataException or ArgumentException)
        {
            diagnostics.Add(Error(SkyrimNpcVoiceDiagnosticCodes.ServiceUnreachable, exception.Message));
            return Descriptor(normalizedEndpoint, source, setup.Platform, false, "unknown", exception.Message, diagnostics.ToImmutable());
        }
    }

    private async Task<JsonElement> GetJsonAsync(Uri endpoint, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(new Uri(endpoint, path), cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    internal static bool TryNormalizeSetup(SkyrimVoiceServerSetup input, out SkyrimVoiceServerSetup setup, out string error)
    {
        setup = input;
        error = string.Empty;
        if (!TryNormalizeEndpoint(input.Endpoint, out string endpoint, out error)) return false;
        string platform = input.Platform.Trim().ToLowerInvariant();
        if (platform == "wsl")
        {
            if (!string.IsNullOrWhiteSpace(input.ServerFolder) || !string.IsNullOrWhiteSpace(input.OutputFolder))
            {
                error = "WSL setup requires empty server and output folders because backing storage is verified automatically.";
                return false;
            }
            setup = new(endpoint, platform, string.Empty, string.Empty);
            return true;
        }
        if (platform != "windows") { error = "Voice server platform must be 'windows' or 'wsl'."; return false; }
        if (!TryCanonicalLocalPath(input.ServerFolder, out string serverFolder, out error) || !TryCanonicalLocalPath(input.OutputFolder, out string outputFolder, out error)) return false;
        setup = new(endpoint, platform, serverFolder, outputFolder);
        return true;
    }

    internal static bool TryNormalizeEndpoint(string text, out string endpoint, out string error)
    {
        endpoint = string.Empty;
        error = "XTTS endpoints must be authority-only HTTP loopback addresses without user information, query, or fragment.";
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || !XttsApiServerClient.IsLoopbackHttp(uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0)) return false;
        endpoint = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static bool TryCanonicalLocalPath(string path, out string canonical, out string error)
    {
        canonical = string.Empty;
        error = "Windows server folders must be absolute ordinary local paths outside protected roots.";
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            if (path.IndexOf(':', 2) >= 0) { error = "Windows server folders cannot contain an alternate-data-stream delimiter."; return false; }
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var value = new WorkspacePath(canonical);
            string? root = Path.GetPathRoot(canonical);
            return !ActorwrightWorkspace.IsVoiceExcludedPath(value) && root is { Length: >= 2 } && root[1] == ':';
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { error = exception.Message; return false; }
    }

    private static (string Verdict, string Detail) WindowsStorageVerdict(SkyrimVoiceServerSetup setup, ref string? speakerFolder, ref string? outputFolder, ref string? modelFolder)
    {
        if (!TryResolveWindowsFolder(setup.ServerFolder, speakerFolder, out string speaker, out string error) ||
            !TryResolveWindowsFolder(setup.ServerFolder, outputFolder, out string output, out error) ||
            !TryResolveWindowsFolder(setup.ServerFolder, modelFolder, out string model, out error)) return ("refused", error);
        speakerFolder = speaker; outputFolder = output; modelFolder = model;
        if (!string.Equals(output, setup.OutputFolder, StringComparison.OrdinalIgnoreCase)) return ("refused", $"The resolved server output folder '{output}' does not match expected output folder '{setup.OutputFolder}'.");
        foreach (string folder in new[] { setup.ServerFolder, speaker, output, model })
            if (!InspectOrdinaryDirectory(folder, out error)) return ("refused", error);
        return ("safe", "Windows storage folders were resolved against the user-declared server working folder. This does not prove the server process working directory or PID ownership.");
    }

    private static bool TryResolveWindowsFolder(string serverFolder, string? reported, out string resolved, out string error)
    {
        resolved = string.Empty;
        error = "The service must report non-empty Windows storage folders.";
        if (string.IsNullOrWhiteSpace(reported)) return false;
        string candidate = Path.IsPathFullyQualified(reported) ? reported : Path.Combine(serverFolder, reported);
        return TryCanonicalLocalPath(candidate, out resolved, out error);
    }

    private static bool InspectOrdinaryDirectory(string folder, out string error)
    {
        error = string.Empty;
        var path = new WorkspacePath(folder);
        if (ActorwrightWorkspace.IsVoiceExcludedPath(path)) { error = $"Service storage '{folder}' targets a protected root."; return false; }
        var ancestry = new List<string>();
        for (string? current = folder; !string.IsNullOrEmpty(current); current = Directory.GetParent(current)?.FullName) ancestry.Add(current);
        ancestry.Reverse();
        foreach (string current in ancestry)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) { error = $"Service storage traverses reparse point '{current}'."; return false; }
                if (string.Equals(current, folder, StringComparison.OrdinalIgnoreCase) && !attributes.HasFlag(FileAttributes.Directory))
                {
                    error = $"Service storage path '{folder}' is not a directory.";
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { error = $"Service storage ancestry could not be inspected at '{current}': {exception.Message}"; return false; }
        }
        string? volumeRoot = Path.GetPathRoot(folder);
        if (volumeRoot is null) { error = $"Service storage '{folder}' has no resolvable volume root."; return false; }
        var volume = new WorkspacePath(volumeRoot);
        ImmutableArray<Diagnostic> admission = new KOnlyWorkspacePolicy(volume, ActorwrightWorkspace.ResolveProtectedRoot(volume)).EvaluateReadRoot(volume, path);
        if (HasErrors(admission))
        {
            error = $"Service storage '{folder}' failed external-root admission: {string.Join("; ", admission.Select(item => item.Message))}";
            return false;
        }
        return true;
    }

    private static bool TrySpeakerCount(JsonElement value, out int count)
    {
        count = 0;
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (!value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)) return false;
            count = value.GetArrayLength(); return true;
        }
        if (value.ValueKind != JsonValueKind.Object) return false;
        foreach (JsonProperty language in value.EnumerateObject())
        {
            if (language.Value.ValueKind != JsonValueKind.Object || !language.Value.TryGetProperty("speakers", out JsonElement speakers) || speakers.ValueKind != JsonValueKind.Array ||
                !speakers.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)) return false;
            count += speakers.GetArrayLength();
        }
        return true;
    }

    private static bool HasRequiredTtsOperation(JsonElement openApi)
    {
        if (!openApi.TryGetProperty("paths", out JsonElement paths) || !paths.TryGetProperty("/tts_to_audio/", out JsonElement route) || !route.TryGetProperty("post", out JsonElement post)) return false;
        if (ContainsTtsFields(post)) return true;
        if (!post.TryGetProperty("requestBody", out JsonElement requestBody) || !requestBody.TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("application/json", out JsonElement jsonContent) || !jsonContent.TryGetProperty("schema", out JsonElement schema) ||
            !schema.TryGetProperty("$ref", out JsonElement reference) || reference.ValueKind != JsonValueKind.String) return false;
        const string prefix = "#/components/schemas/";
        string? text = reference.GetString();
        return text is not null && text.StartsWith(prefix, StringComparison.Ordinal) && openApi.TryGetProperty("components", out JsonElement components) &&
            components.TryGetProperty("schemas", out JsonElement schemas) && schemas.TryGetProperty(text[prefix.Length..], out JsonElement referenced) && ContainsTtsFields(referenced);
    }

    private static bool ContainsTtsFields(JsonElement value)
    {
        string raw = value.GetRawText();
        return raw.Contains("\"text\"", StringComparison.Ordinal) && raw.Contains("\"speaker_wav\"", StringComparison.Ordinal) && raw.Contains("\"language\"", StringComparison.Ordinal);
    }

    private static (string Verdict, string Detail) StorageVerdict(params string?[] folders)
    {
        bool needsWslBackingProof = false;
        foreach (string? folder in folders)
        {
            string? normalized = NormalizeLinuxPath(folder);
            if (normalized is null) { needsWslBackingProof = true; continue; }
            string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2 || !segments[0].Equals("mnt", StringComparison.OrdinalIgnoreCase) || segments[1].Length != 1 || !char.IsAsciiLetter(segments[1][0]))
                needsWslBackingProof = true;
            else if (ActorwrightWorkspace.IsVoiceExcludedPath(new WorkspacePath($@"{segments[1]}:\{string.Join('\\', segments.Skip(2))}")))
                return ("refused", $"Service storage '{folder}' targets a protected root.");
        }
        if (needsWslBackingProof)
        {
            string detail = "The service reports storage paths that require WSL backing proof, and the current process cannot bind them to one verified WSL BasePath.";
            if (!OperatingSystem.IsWindows() || !TryGetSingleWslBasePath(out string basePath, out detail)) return ("unknown", detail);
            string normalizedBase = Path.GetFullPath(Environment.ExpandEnvironmentVariables(basePath));
            if (ActorwrightWorkspace.IsVoiceExcludedPath(new WorkspacePath(normalizedBase)))
                return ("refused", $"The only registered WSL BasePath '{normalizedBase}' targets a protected root.");
            if (!Directory.Exists(normalizedBase)) return ("unknown", $"The only registered WSL BasePath '{normalizedBase}' does not exist as an inspectable directory.");
            string? volumeRoot = Path.GetPathRoot(normalizedBase);
            if (volumeRoot is null) return ("unknown", $"The only registered WSL BasePath '{normalizedBase}' has no resolvable volume root.");
            var policy = new KOnlyWorkspacePolicy(new WorkspacePath(volumeRoot), ActorwrightWorkspace.ResolveProtectedRoot(new WorkspacePath(volumeRoot)));
            ImmutableArray<Diagnostic> admission = policy.EvaluateReadRoot(new WorkspacePath(volumeRoot), new WorkspacePath(normalizedBase));
            if (HasErrors(admission)) return ("unknown", $"The only registered WSL BasePath '{normalizedBase}' failed ancestry inspection: {string.Join("; ", admission.Select(item => item.Message))}");
            return ("safe", $"Relative service folders are bound to the only registered WSL BasePath '{normalizedBase}', outside protected roots.");
        }
        return ("safe", "All reported service storage paths are absolute and outside protected roots.");
    }

    private static string? NormalizeLinuxPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string unix = path.Replace('\\', '/');
        if (unix[0] != '/') return null;
        var segments = new List<string>();
        foreach (string segment in unix.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetSingleWslBasePath(out string basePath, out string detail)
    {
        basePath = string.Empty;
        detail = "The service reports storage paths that require WSL backing proof, and the current process cannot bind them to one verified WSL BasePath.";
        try
        {
            using RegistryKey? lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss", writable: false);
            string[] paths = lxss?.GetSubKeyNames().Select(name => { using RegistryKey? distribution = lxss.OpenSubKey(name, writable: false); return distribution?.GetValue("BasePath") as string; })
                .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            if (paths.Length != 1) return false;
            basePath = paths[0];
            return Path.IsPathFullyQualified(Environment.ExpandEnvironmentVariables(basePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            detail = $"The service reports storage paths that require WSL backing proof, and WSL BasePath verification failed: {exception.Message}";
            return false;
        }
    }

    private static bool EndpointEquals(string first, string second) => TryNormalizeEndpoint(first, out string a, out _) && TryNormalizeEndpoint(second, out string b, out _) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string? StringProperty(JsonElement value, params string[] names)
    {
        foreach (string name in names) if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String) return property.GetString();
        return null;
    }
    private static ImmutableArray<string> Strings(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).ToImmutableArray() : [];
    private static ImmutableArray<string> LanguageCodes(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()!).ToImmutableArray() : [];
    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static Diagnostic Error(string code, string message) => new(code, DiagnosticSeverity.Error, message);
    private static SkyrimVoiceServiceDescriptor Descriptor(string endpoint, string source, string platform, bool compatible, string verdict, string? detail, ImmutableArray<Diagnostic> diagnostics) =>
        new(endpoint, source, compatible, null, null, [], [], 0, null, null, null, null, verdict, detail, null, diagnostics) { Platform = platform };
    private sealed record VoiceConfiguration(ImmutableArray<string> Endpoints, ImmutableArray<SkyrimVoiceServerSetup> Setups, string? DefaultEndpoint);
}
