using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// One bounded, K-local settings document for the integrated Skyrim
/// workbench. A write is accepted only after temporary-file readback.
/// </summary>
public sealed class SkyrimMainWorkspaceSettingsService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    WorkspacePath projectRoot)
    : ISkyrimMainWorkspaceSettingsService
{
    private const string SchemaVersion = "1";
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly WorkspacePath _settingsFile = new(Path.Combine(
        projectRoot.Value,
        "03-builds",
        "work",
        "npc-studio-settings",
        "main-workspace.json"));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public async ValueTask<SkyrimMainWorkspaceSettingsLoadResult>
        LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<Diagnostic> pathDiagnostics = ValidatePath();
        if (HasErrors(pathDiagnostics))
            return LoadRefused(pathDiagnostics);
        if (!File.Exists(_settingsFile.Value))
        {
            return new SkyrimMainWorkspaceSettingsLoadResult(
                false,
                Defaults(),
                null,
                [
                    new Diagnostic(
                        "main-workspace-settings-default",
                        DiagnosticSeverity.Info,
                        "No saved main-workspace settings exist; documented defaults are active.")
                ]);
        }

        try
        {
            FileInfo info = new(_settingsFile.Value);
            if (info.Length is <= 0 or > MaximumBytes)
                return LoadRefused([
                    Error(
                        "main-workspace-settings-size",
                        "The settings file is empty or exceeds 4 MiB.")
                ]);
            byte[] bytes = await File.ReadAllBytesAsync(
                _settingsFile.Value, cancellationToken)
                .ConfigureAwait(false);
            return Decode(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException or ArgumentException)
        {
            return LoadRefused([
                Error(
                    "main-workspace-settings-read",
                    exception.Message)
            ]);
        }
    }

    public async ValueTask<SkyrimMainWorkspaceSettingsSaveResult>
        SaveAsync(
            SkyrimMainWorkspaceSettings settings,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePath().ToBuilder();
        diagnostics.AddRange(Validate(settings));
        if (File.Exists(_settingsFile.Value))
            diagnostics.Add(Error(
                "main-workspace-settings-exists",
                "Main-workspace settings never overwrite an existing document."));
        if (HasErrors(diagnostics))
            return SaveRefused(diagnostics);

        string parent = Path.GetDirectoryName(_settingsFile.Value) ??
            throw new InvalidDataException(
                "The settings path has no parent.");
        string temporary = _settingsFile.Value + ".tmp-" +
                           Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(parent);
            diagnostics.AddRange(ValidatePath());
            if (HasErrors(diagnostics))
                return SaveRefused(diagnostics);

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                settings, JsonOptions);
            if (bytes.Length is <= 0 or > MaximumBytes)
                return SaveRefused([
                    Error(
                        "main-workspace-settings-size",
                        "Serialized settings are empty or exceed 4 MiB.")
                ]);
            await File.WriteAllBytesAsync(
                    temporary, bytes, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            byte[] reopened = await File.ReadAllBytesAsync(
                    temporary, cancellationToken)
                .ConfigureAwait(false);
            SkyrimMainWorkspaceSettingsLoadResult readback =
                Decode(reopened);
            Sha256Hash expectedHash = Hash(bytes);
            if (!readback.LoadedFromDisk ||
                readback.Sha256 != expectedHash ||
                !Equivalent(settings, readback.Settings))
                throw new InvalidDataException(
                    "Temporary settings failed exact semantic and hash readback.");

            File.Move(
                temporary, _settingsFile.Value, overwrite: false);
            byte[] committed = await File.ReadAllBytesAsync(
                    _settingsFile.Value, CancellationToken.None)
                .ConfigureAwait(false);
            SkyrimMainWorkspaceSettingsLoadResult committedReadback =
                Decode(committed);
            if (!committedReadback.LoadedFromDisk ||
                committedReadback.Sha256 != expectedHash ||
                !Equivalent(settings, committedReadback.Settings))
            {
                TryDelete(_settingsFile.Value);
                throw new InvalidDataException(
                    "Committed settings failed exact semantic and hash readback.");
            }
            return new SkyrimMainWorkspaceSettingsSaveResult(
                true,
                committedReadback.Settings,
                expectedHash,
                committedReadback.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException or
            InvalidDataException)
        {
            return SaveRefused([
                Error(
                    "main-workspace-settings-write",
                    exception.Message)
            ]);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static SkyrimMainWorkspaceSettingsLoadResult Decode(
        byte[] bytes)
    {
        try
        {
            SkyrimMainWorkspaceSettings? settings =
                JsonSerializer.Deserialize<SkyrimMainWorkspaceSettings>(
                    bytes, JsonOptions);
            if (settings is null)
                return LoadRefused([
                    Error(
                        "main-workspace-settings-schema",
                        "The settings document is empty.")
                ]);
            ImmutableArray<Diagnostic> diagnostics =
                Validate(settings);
            if (HasErrors(diagnostics))
                return LoadRefused(diagnostics);
            return new SkyrimMainWorkspaceSettingsLoadResult(
                true, settings, Hash(bytes), diagnostics);
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or
            ArgumentException)
        {
            return LoadRefused([
                Error(
                    "main-workspace-settings-schema",
                    exception.Message)
            ]);
        }
    }

    private ImmutableArray<Diagnostic> ValidatePath()
    {
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        if (!projectRoot.IsUnder(labRoot) ||
            !_settingsFile.IsUnder(projectRoot) ||
            !string.Equals(
                _settingsFile.Value,
                Path.Combine(
                    projectRoot.Value,
                    "03-builds",
                    "work",
                    "npc-studio-settings",
                    "main-workspace.json"),
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "main-workspace-settings-path",
                "Settings must use the fixed project-local main-workspace path."));
        diagnostics.AddRange(
            policy.Evaluate(projectRoot, _settingsFile));
        return diagnostics.ToImmutable();
    }

    private static ImmutableArray<Diagnostic> Validate(
        SkyrimMainWorkspaceSettings settings)
    {
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        if (!string.Equals(
                settings.SchemaVersion,
                SchemaVersion,
                StringComparison.Ordinal))
            diagnostics.Add(Error(
                "main-workspace-settings-schema",
                "Only main-workspace settings schema 1 is accepted."));
        if (settings.Filter is null || settings.Preview is null)
        {
            diagnostics.Add(Error(
                "main-workspace-settings-content",
                "Settings require filter and preview documents."));
            return diagnostics.ToImmutable();
        }
        if (settings.Filter.Search is null ||
            settings.Filter.Search.Length > 512 ||
            settings.Filter.Search.Any(char.IsControl))
            diagnostics.Add(Error(
                "main-workspace-settings-search",
                "The saved search must be at most 512 characters and contain no controls."));
        if (!Enum.IsDefined(settings.Filter.Gender) ||
            !Enum.IsDefined(settings.Preview.Mode) ||
            !Enum.IsDefined(settings.Preview.Gender))
            diagnostics.Add(Error(
                "main-workspace-settings-enum",
                "Settings contain an unsupported enum value."));
        if (settings.Filter.Categories is null ||
            settings.Filter.Categories.Count == 0 ||
            settings.Filter.Categories.Any(
                category => !Enum.IsDefined(category)))
            diagnostics.Add(Error(
                "main-workspace-settings-categories",
                "The category filter must contain supported NPC categories."));
        return diagnostics.ToImmutable();
    }

    private static bool Equivalent(
        SkyrimMainWorkspaceSettings left,
        SkyrimMainWorkspaceSettings right) =>
        left.SchemaVersion == right.SchemaVersion &&
        left.Filter.Search == right.Filter.Search &&
        left.Filter.ShowNpcs == right.Filter.ShowNpcs &&
        left.Filter.ShowLeveledNpcs ==
            right.Filter.ShowLeveledNpcs &&
        left.Filter.Categories.SetEquals(
            right.Filter.Categories) &&
        left.Filter.Gender == right.Filter.Gender &&
        left.Filter.ChangedOnly == right.Filter.ChangedOnly &&
        left.Filter.IncludeDeleted ==
            right.Filter.IncludeDeleted &&
        left.Preview == right.Preview;

    private static SkyrimMainWorkspaceSettings Defaults() =>
        new(
            SchemaVersion,
            SkyrimMainWorkspaceFilter.Default,
            SkyrimMainWorkspacePreviewOptions.Default);

    private static SkyrimMainWorkspaceSettingsLoadResult
        LoadRefused(IEnumerable<Diagnostic> diagnostics) =>
        new(false, Defaults(), null, diagnostics.ToImmutableArray());

    private static SkyrimMainWorkspaceSettingsSaveResult
        SaveRefused(IEnumerable<Diagnostic> diagnostics) =>
        new(false, null, null, diagnostics.ToImmutableArray());

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A unique temporary sibling is never accepted as authority.
        }
    }
}
