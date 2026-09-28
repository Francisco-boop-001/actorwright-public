using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class SkyrimLightingSettingsService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    WorkspacePath settingsFile) : ISkyrimLightingSettingsService
{
    private const int SchemaVersion = 1;
    private const string Kind = "npc-studio-preview-lighting";
    private const long MaximumSettingsBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async ValueTask<SkyrimLightingSettingsLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImmutableArray<Diagnostic> pathDiagnostics = ValidatePath();
        if (HasErrors(pathDiagnostics)) return LoadRefused(pathDiagnostics);
        if (!File.Exists(settingsFile.Value))
        {
            return new SkyrimLightingSettingsLoadResult(
                false,
                SkyrimLightingRules.DefaultPreset,
                null,
                [new Diagnostic(
                    "lighting-settings-default",
                    DiagnosticSeverity.Info,
                    "No saved preview lighting exists; the documented default is active.")]);
        }

        try
        {
            var info = new FileInfo(settingsFile.Value);
            if (info.Length is <= 0 or > MaximumSettingsBytes)
            {
                return LoadRefused([
                    Error("lighting-settings-size",
                        "The saved preview-lighting file is empty or exceeds the accepted size bound.")
                ]);
            }
            byte[] bytes = await File.ReadAllBytesAsync(
                settingsFile.Value, cancellationToken).ConfigureAwait(false);
            return Decode(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           JsonException or
                                           NotSupportedException)
        {
            return LoadRefused([
                Error("lighting-settings-read", exception.Message)
            ]);
        }
    }

    public async ValueTask<SkyrimLightingSettingsSaveResult> SaveAsync(
        PreviewLightingPreset preset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePath().ToBuilder();
        diagnostics.AddRange(SkyrimLightingRules.Validate(preset));
        if (HasErrors(diagnostics)) return SaveRefused(diagnostics);

        string? parent = Path.GetDirectoryName(settingsFile.Value);
        if (string.IsNullOrWhiteSpace(parent))
            return SaveRefused([Error("lighting-settings-parent", "The settings file has no parent directory.")]);

        string temporary = settingsFile.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        string backup = settingsFile.Value + ".bak-" + Guid.NewGuid().ToString("N");
        bool destinationExisted = File.Exists(settingsFile.Value);
        bool committed = false;
        bool preserveBackup = false;
        try
        {
            Directory.CreateDirectory(parent);
            diagnostics.AddRange(ValidatePath());
            if (HasErrors(diagnostics)) return SaveRefused(diagnostics);

            var document = new SettingsDocument(SchemaVersion, Kind, preset);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.Length > MaximumSettingsBytes)
                return SaveRefused([Error("lighting-settings-size", "The serialized light rig exceeds the settings size bound.")]);

            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (destinationExisted)
                File.Replace(temporary, settingsFile.Value, backup, true);
            else
                File.Move(temporary, settingsFile.Value, false);
            committed = true;

            byte[] reopened = await File.ReadAllBytesAsync(
                settingsFile.Value, CancellationToken.None).ConfigureAwait(false);
            SkyrimLightingSettingsLoadResult readback = Decode(reopened);
            Sha256Hash expectedHash = Hash(bytes);
            if (!readback.LoadedFromDisk ||
                readback.Sha256 != expectedHash ||
                !SkyrimLightingRules.AreEquivalent(readback.Preset, preset))
            {
                throw new InvalidDataException(
                    "The committed preview-lighting settings failed exact readback.");
            }

            TryDelete(backup);
            return new SkyrimLightingSettingsSaveResult(
                true, preset, expectedHash, readback.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           JsonException or
                                           NotSupportedException or
                                           InvalidDataException)
        {
            var failures = ImmutableArray.CreateBuilder<Diagnostic>();
            failures.Add(Error("lighting-settings-write", exception.Message));
            if (committed)
            {
                try
                {
                    if (destinationExisted && File.Exists(backup))
                        File.Replace(backup, settingsFile.Value, null, true);
                    else if (!destinationExisted && File.Exists(settingsFile.Value))
                        File.Delete(settingsFile.Value);
                }
                catch (Exception rollbackException) when (rollbackException is IOException or
                                                          UnauthorizedAccessException)
                {
                    preserveBackup = File.Exists(backup);
                    failures.Add(Error(
                        "lighting-settings-rollback",
                        "The settings write failed and automatic rollback also failed: " +
                        rollbackException.Message +
                        (preserveBackup
                            ? $". The recoverable backup remains at '{backup}'."
                            : ".")));
                }
            }
            return SaveRefused(failures);
        }
        finally
        {
            TryDelete(temporary);
            if (!preserveBackup) TryDelete(backup);
        }
    }

    private static SkyrimLightingSettingsLoadResult Decode(byte[] bytes)
    {
        try
        {
            SettingsDocument? document = JsonSerializer.Deserialize<SettingsDocument>(
                bytes, JsonOptions);
            if (document is null ||
                document.SchemaVersion != SchemaVersion ||
                !string.Equals(document.Kind, Kind, StringComparison.Ordinal) ||
                document.Preset is null)
            {
                return LoadRefused([
                    Error("lighting-settings-schema",
                        "The preview-lighting settings schema or kind is not supported.")
                ]);
            }
            ImmutableArray<Diagnostic> diagnostics =
                SkyrimLightingRules.Validate(document.Preset);
            if (HasErrors(diagnostics)) return LoadRefused(diagnostics);
            return new SkyrimLightingSettingsLoadResult(
                true, document.Preset, Hash(bytes), diagnostics);
        }
        catch (Exception exception) when (exception is JsonException or
                                           NotSupportedException or
                                           ArgumentException)
        {
            return LoadRefused([
                Error("lighting-settings-schema", exception.Message)
            ]);
        }
    }

    private ImmutableArray<Diagnostic> ValidatePath()
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!settingsFile.IsUnder(labRoot) ||
            !settingsFile.Value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(settingsFile.Value, labRoot.Value, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "lighting-settings-path",
                "Preview-lighting settings must be an exact .json file under the K-only lab root."));
        }
        diagnostics.AddRange(policy.Evaluate(labRoot, settingsFile));
        return diagnostics.ToImmutable();
    }

    private static SkyrimLightingSettingsLoadResult LoadRefused(
        IEnumerable<Diagnostic> diagnostics) =>
        new(false, SkyrimLightingRules.DefaultPreset, null,
            diagnostics.ToImmutableArray());

    private static SkyrimLightingSettingsSaveResult SaveRefused(
        IEnumerable<Diagnostic> diagnostics) =>
        new(false, null, null, diagnostics.ToImmutableArray());

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            // The caller receives the primary result; exact temporary paths are
            // unique and never accepted as settings authority.
        }
    }

    private sealed record SettingsDocument(
        int SchemaVersion,
        string Kind,
        PreviewLightingPreset? Preset);
}
