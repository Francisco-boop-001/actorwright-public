using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimNpcVoiceViewModel :
    INotifyPropertyChanged,
    IDisposable
{
    private const string EmptyValue = "—";
    private const string InitialStatus =
        "Choose an NPC identity, then drop or browse to one WAV sample.";
    private const string DefaultServerEndpoint = "http://127.0.0.1:8020";
    private const string InitialServerSetupStatus =
        "Enter the server working folder and expected output folder, then check the local XTTS server.";
    private readonly ISkyrimNpcVoiceService service;
    private readonly WorkspacePath workspaceRoot;
    private readonly IDisposable? ownedServiceLifetime;
    private readonly AsyncCommand importCommand;
    private readonly AsyncCommand checkServerSetupCommand;
    private readonly AsyncCommand saveServerSetupCommand;
    private CancellationTokenSource? cancellation;
    private bool disposed;

    public SkyrimNpcVoiceViewModel(
        ISkyrimNpcVoiceService service,
        WorkspacePath workspaceRoot,
        WorkspacePath outputRoot,
        IDisposable? ownedServiceLifetime = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.workspaceRoot = workspaceRoot;
        this.ownedServiceLifetime = ownedServiceLifetime;
        this.outputRoot = outputRoot.Value;
        importCommand = new AsyncCommand(
            () => ImportSampleAsync(SamplePath),
            () => !IsBusy && !string.IsNullOrWhiteSpace(SamplePath));
        checkServerSetupCommand = new AsyncCommand(
            CheckServerSetupAsync,
            () => !IsBusy);
        saveServerSetupCommand = new AsyncCommand(
            SaveServerSetupAsync,
            () => !IsBusy);
    }

    public ObservableCollection<string> Diagnostics { get; } = [];

    public ICommand ImportCommand => importCommand;

    public ICommand CheckServerSetupCommand => checkServerSetupCommand;

    public ICommand SaveServerSetupCommand => saveServerSetupCommand;

    public IReadOnlyList<string> ServerPlatforms { get; } = ["windows", "wsl"];

    private string serverEndpoint = DefaultServerEndpoint;
    public string ServerEndpoint
    {
        get => serverEndpoint;
        set => SetServerInput(ref serverEndpoint, value);
    }

    private string serverPlatform = "windows";
    public string ServerPlatform
    {
        get => serverPlatform;
        set
        {
            string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!Set(ref serverPlatform, normalized))
                return;
            OnPropertyChanged(nameof(AreServerFoldersEnabled));
            if (!AreServerFoldersEnabled)
            {
                Set(ref serverFolder, string.Empty, nameof(ServerFolder));
                Set(ref serverOutputFolder, string.Empty, nameof(ServerOutputFolder));
            }
            InvalidateServerSetup();
        }
    }

    private string serverFolder = string.Empty;
    public string ServerFolder
    {
        get => serverFolder;
        set => SetServerInput(ref serverFolder, value);
    }

    private string serverOutputFolder = string.Empty;
    public string ServerOutputFolder
    {
        get => serverOutputFolder;
        set => SetServerInput(ref serverOutputFolder, value);
    }

    public bool AreServerFoldersEnabled =>
        string.Equals(ServerPlatform, "windows", StringComparison.Ordinal);

    private string serverSetupStatus = InitialServerSetupStatus;
    public string ServerSetupStatus
    {
        get => serverSetupStatus;
        private set => Set(ref serverSetupStatus, value);
    }

    private string plugin = string.Empty;
    public string Plugin
    {
        get => plugin;
        set => SetInput(ref plugin, value);
    }

    private string formId = string.Empty;
    public string FormId
    {
        get => formId;
        set => SetInput(ref formId, value);
    }

    private string editorId = string.Empty;
    public string EditorId
    {
        get => editorId;
        set => SetInput(ref editorId, value);
    }

    private string voicePrefix = string.Empty;
    public string VoicePrefix
    {
        get => voicePrefix;
        set => SetInput(ref voicePrefix, value);
    }

    private string outputRoot;
    public string OutputRoot
    {
        get => outputRoot;
        set => SetInput(ref outputRoot, value);
    }

    private string samplePath = string.Empty;
    public string SamplePath
    {
        get => samplePath;
        set
        {
            if (!Set(ref samplePath, value))
                return;
            InvalidateResult();
            importCommand.RaiseCanExecuteChanged();
        }
    }

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!Set(ref isBusy, value))
                return;
            OnPropertyChanged(nameof(IsNotBusy));
            importCommand.RaiseCanExecuteChanged();
            checkServerSetupCommand.RaiseCanExecuteChanged();
            saveServerSetupCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsNotBusy => !IsBusy;

    public async Task InitializeServerSetupAsync()
    {
        if (disposed || IsBusy)
            return;
        CancellationTokenSource source = BeginOperation(
            "Loading saved XTTS server settings…");
        try
        {
            SkyrimVoiceServerSetupResult result = await Task.Run(
                () => service.LoadServerSetupAsync(source.Token).AsTask(),
                source.Token);
            AddDiagnostics(result.Diagnostics);
            if (result.Succeeded && result.Settings is { } settings)
            {
                ApplyServerSetup(settings);
                ServerSetupStatus =
                    "Saved XTTS server settings loaded. Check the endpoint and storage before use.";
            }
            else
            {
                ApplyServerSetup(new SkyrimVoiceServerSetup(
                    DefaultServerEndpoint, "windows", string.Empty, string.Empty));
                ServerSetupStatus = InitialServerSetupStatus;
            }
        }
        catch (OperationCanceledException)
        {
            ServerSetupStatus = "XTTS server setup loading was cancelled.";
        }
        catch (Exception exception)
        {
            ReportServerSetupError($"voice-server-setup-error: {exception.Message}");
        }
        finally
        {
            EndOperation(source);
        }
    }

    public Task CheckServerSetupAsync() =>
        RunServerSetupAsync(save: false);

    public Task SaveServerSetupAsync() =>
        RunServerSetupAsync(save: true);

    private async Task RunServerSetupAsync(bool save)
    {
        if (disposed || IsBusy)
            return;
        var setup = new SkyrimVoiceServerSetup(
            ServerEndpoint.Trim(),
            ServerPlatform,
            ServerFolder.Trim(),
            ServerOutputFolder.Trim());
        if (!TryValidateServerSetup(setup, out string validation))
        {
            ReportServerSetupError(
                $"{SkyrimNpcVoiceDiagnosticCodes.ServerSetupInvalid}: {validation}");
            return;
        }

        CancellationTokenSource source = BeginOperation(save
            ? "Checking and saving XTTS server settings…"
            : "Checking the XTTS endpoint and storage…");
        try
        {
            SkyrimVoiceServerSetupResult result = save
                ? await Task.Run(
                    () => service.SaveServerSetupAsync(
                        setup, source.Token).AsTask(),
                    source.Token)
                : await Task.Run(
                    () => service.CheckServerSetupAsync(
                        setup, source.Token).AsTask(),
                    source.Token);
            AddDiagnostics(result.Diagnostics);
            if (!result.Succeeded || result.Settings is not { } accepted)
            {
                ServerSetupStatus = result.Diagnostics.Length == 0
                    ? "XTTS server setup was refused."
                    : string.Join(" ", result.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}"));
                return;
            }

            ApplyServerSetup(accepted);
            string prefix = save
                ? "XTTS server setup checked and saved"
                : "XTTS server setup checked";
            ServerSetupStatus = string.Equals(
                    accepted.Platform, "windows", StringComparison.Ordinal)
                ? $"{prefix}. Queried endpoint {accepted.Endpoint}. Storage passed using the user-declared working folder {accepted.ServerFolder}; expected server output is {accepted.OutputFolder}."
                : $"{prefix}. Queried endpoint {accepted.Endpoint}. WSL requires access to its distribution storage; no Windows folder is configured here.";
        }
        catch (OperationCanceledException)
        {
            ServerSetupStatus = save
                ? "XTTS server setup saving was cancelled."
                : "XTTS server setup check was cancelled.";
        }
        catch (Exception exception)
        {
            ReportServerSetupError($"voice-server-setup-error: {exception.Message}");
        }
        finally
        {
            EndOperation(source);
        }
    }

    private static bool TryValidateServerSetup(
        SkyrimVoiceServerSetup setup,
        out string error)
    {
        if (setup.Endpoint.Length == 0)
        {
            error = "Enter the loopback HTTP server address.";
            return false;
        }
        if (setup.Platform is not ("windows" or "wsl"))
        {
            error = "Choose windows or wsl.";
            return false;
        }
        if (setup.Platform == "windows" &&
            (setup.ServerFolder.Length == 0 || setup.OutputFolder.Length == 0))
        {
            error = "Enter both the server working folder and expected server output folder for Windows.";
            return false;
        }
        if (setup.Platform == "wsl" &&
            (setup.ServerFolder.Length != 0 || setup.OutputFolder.Length != 0))
        {
            error = "WSL requires access to its distribution storage; leave both Windows folder fields empty.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private void ApplyServerSetup(SkyrimVoiceServerSetup setup)
    {
        Set(ref serverEndpoint, setup.Endpoint, nameof(ServerEndpoint));
        bool platformChanged = Set(
            ref serverPlatform,
            setup.Platform,
            nameof(ServerPlatform));
        Set(ref serverFolder, setup.ServerFolder, nameof(ServerFolder));
        Set(ref serverOutputFolder, setup.OutputFolder, nameof(ServerOutputFolder));
        if (platformChanged)
            OnPropertyChanged(nameof(AreServerFoldersEnabled));
    }

    private CancellationTokenSource BeginOperation(string message)
    {
        Diagnostics.Clear();
        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        ServerSetupStatus = message;
        return source;
    }

    private void EndOperation(CancellationTokenSource source)
    {
        if (ReferenceEquals(cancellation, source))
            cancellation = null;
        source.Dispose();
        IsBusy = false;
    }

    private void AddDiagnostics(IEnumerable<Diagnostic> values)
    {
        foreach (Diagnostic diagnostic in values)
            Diagnostics.Add($"{diagnostic.Code}: {diagnostic.Message}");
    }

    private void ReportServerSetupError(string message)
    {
        Diagnostics.Clear();
        Diagnostics.Add(message);
        ServerSetupStatus = message;
    }

    private void SetServerInput(ref string field, string value,
        [CallerMemberName] string? propertyName = null)
    {
        if (!Set(ref field, value ?? string.Empty, propertyName))
            return;
        InvalidateServerSetup();
    }

    private void InvalidateServerSetup()
    {
        Diagnostics.Clear();
        ServerSetupStatus =
            "Server settings changed. Check again before saving or synthesis.";
    }

    private string status = InitialStatus;
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    private string authorityPath = EmptyValue;
    public string AuthorityPath
    {
        get => authorityPath;
        private set => Set(ref authorityPath, value);
    }

    private string authoritySha256 = EmptyValue;
    public string AuthoritySha256
    {
        get => authoritySha256;
        private set => Set(ref authoritySha256, value);
    }

    private string importCommandLine = "Import a valid WAV to bind the exact CLI equivalent.";
    public string ImportCommandLine
    {
        get => importCommandLine;
        private set => Set(ref importCommandLine, value);
    }

    private string templateCommand = "Import a valid WAV to bind the dialogue-template command.";
    public string TemplateCommand
    {
        get => templateCommand;
        private set => Set(ref templateCommand, value);
    }

    private string synthesisCommand = "Import a valid WAV to bind the synthesis command.";
    public string SynthesisCommand
    {
        get => synthesisCommand;
        private set => Set(ref synthesisCommand, value);
    }

    private string followUpRequirements =
        "The profile, NPC sex, dialogue manifest, manifest SHA-256, and fresh synthesis output are still required.";
    public string FollowUpRequirements
    {
        get => followUpRequirements;
        private set => Set(ref followUpRequirements, value);
    }

    public async Task ImportSampleAsync(string path)
    {
        if (disposed || IsBusy)
            return;
        SamplePath = path;
        Diagnostics.Clear();
        if (!string.Equals(
                Path.GetExtension(path),
                ".wav",
                StringComparison.OrdinalIgnoreCase))
        {
            ReportValidation(
                "voice-sample-invalid: Drop or browse to exactly one .wav file.");
            return;
        }

        SkyrimVoiceSampleImportRequest request;
        try
        {
            if (!NpcManager.Domain.FormId.TryParse(FormId, out FormId parsedFormId))
                throw new ArgumentException("FormID must be hexadecimal, for example 0x00000800.");
            var sample = new WorkspacePath(path);
            var output = new WorkspacePath(OutputRoot);
            if (!sample.IsUnder(workspaceRoot) || !output.IsUnder(workspaceRoot))
                throw new ArgumentException("The sample and output must remain inside the K-local workspace.");
            request = new SkyrimVoiceSampleImportRequest(
                sample,
                new PluginName(Plugin),
                parsedFormId,
                string.IsNullOrWhiteSpace(EditorId) ? null : new EditorId(EditorId),
                new EditorId(VoicePrefix).Value,
                output);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            ReportValidation($"voice-sample-invalid: {exception.Message}");
            return;
        }

        ClearAuthority();
        var source = new CancellationTokenSource();
        cancellation = source;
        IsBusy = true;
        Status = "Importing and normalizing the WAV sample…";
        try
        {
            SkyrimVoiceSampleImportResult result =
                await Task.Run(
                    () => service.ImportSampleAsync(request, source.Token).AsTask(),
                    source.Token);
            foreach (Diagnostic diagnostic in result.Diagnostics)
                Diagnostics.Add(
                    $"{diagnostic.Code}: {diagnostic.Message}");
            if (!result.Imported ||
                result.AuthorityPath is not { } importedAuthorityPath ||
                result.AuthoritySha256 is not { } importedAuthoritySha256)
            {
                Status = Diagnostics.Count == 0
                    ? "Voice sample import was refused."
                    : string.Join(" ", Diagnostics);
                return;
            }

            AuthorityPath = importedAuthorityPath.Value;
            AuthoritySha256 = importedAuthoritySha256.Value;
            BindCommands(request, importedAuthorityPath, importedAuthoritySha256);
            Status =
                $"Import complete. Authority SHA-256 {importedAuthoritySha256.Value}. " +
                "Listening, voice likeness, and Skyrim runtime behavior remain unverified.";
        }
        catch (OperationCanceledException)
        {
            Status = "Voice sample import was cancelled.";
        }
        catch (Exception exception)
        {
            ReportValidation($"voice-import-error: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(cancellation, source))
                cancellation = null;
            source.Dispose();
            IsBusy = false;
        }
    }

    internal void ReportValidation(string message)
    {
        Diagnostics.Clear();
        Diagnostics.Add(message);
        Status = message;
    }

    private void BindCommands(
        SkyrimVoiceSampleImportRequest request,
        WorkspacePath importedAuthorityPath,
        Sha256Hash importedAuthoritySha256)
    {
        string editor = request.EditorId is { } id
            ? $" --editor-id {Quote(id.Value)}"
            : string.Empty;
        ImportCommandLine =
            $"actorwright npc voice import --sample {Quote(request.Sample.Value)} " +
            $"--plugin {Quote(request.Plugin.Value)} --form-id {request.FormId} " +
            $"--voice-prefix {Quote(request.VoicePrefix)}{editor} " +
            $"--output {Quote(request.OutputRoot.Value)} --json";
        TemplateCommand =
            "actorwright npc dialogue analyze --template follower-core-v1 " +
            "--profile '<REQUIRED profile.json>' " +
            "--manifest-output '<REQUIRED new dialogue-manifest.json>' " +
            $"--npc-plugin {Quote(request.Plugin.Value)} --form-id {request.FormId}" +
            editor + $" --voice-prefix {Quote(request.VoicePrefix)} " +
            "--female <REQUIRED true|false> --json";
        SynthesisCommand =
            "actorwright npc voice synthesize " +
            "--manifest '<REQUIRED dialogue-manifest.json>' " +
            "--manifest-sha256 '<REQUIRED 64-hex SHA-256>' " +
            $"--sample-authority {Quote(importedAuthorityPath.Value)} " +
            $"--sample-authority-sha256 {importedAuthoritySha256.Value} " +
            "--output '<REQUIRED new synthesis directory>' --json";
        FollowUpRequirements =
            "Command templates are not ready to run until you provide a profile JSON, " +
            "NPC sex (--female true|false), a fresh dialogue-manifest output, then the " +
            "resulting manifest path and manifest SHA-256 plus a fresh synthesis output.";
    }

    private static string Quote(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private void ClearAuthority()
    {
        AuthorityPath = EmptyValue;
        AuthoritySha256 = EmptyValue;
        ImportCommandLine = "Import a valid WAV to bind the exact CLI equivalent.";
        TemplateCommand = "Import a valid WAV to bind the dialogue-template command.";
        SynthesisCommand = "Import a valid WAV to bind the synthesis command.";
    }

    private void InvalidateResult()
    {
        ClearAuthority();
        Diagnostics.Clear();
        Status = InitialStatus;
    }

    private void SetInput(ref string field, string value,
        [CallerMemberName] string? propertyName = null)
    {
        if (!Set(ref field, value, propertyName))
            return;
        InvalidateResult();
    }

    private bool Set<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        cancellation?.Cancel();
        ownedServiceLifetime?.Dispose();
    }
}
