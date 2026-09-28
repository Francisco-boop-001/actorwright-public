using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Bounded, read-only binding reader for the private JSlot output-plugin
/// phase. It deliberately returns the complete PNAM list from the target NPC;
/// the caller compares descriptor/provider members separately rather than
/// mistaking a provider-only subset for the plugin's full PNAM closure.
/// </summary>
public sealed class RaceMenuJslotOutputPluginBindingReader(
    IPluginReader? pluginReader = null) : IRaceMenuJslotOutputPluginBindingReader
{
    // The Bethesda parser opens the complete plugin through its binary
    // overlay. Keep the parser input bounded before it is invoked.
    private const long MaximumOutputPluginBytes = 256L * 1024 * 1024;
    private readonly IPluginReader _pluginReader =
        pluginReader ?? new NpcManager.Formats.Bethesda.BethesdaPluginReader();

    public async ValueTask<RaceMenuJslotOutputPluginBindingReadResult> ReadAsync(
        RaceMenuJslotOutputPluginBindingReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            if (!File.Exists(request.PluginPath.Value) ||
                (File.GetAttributes(request.PluginPath.Value) &
                    FileAttributes.ReparsePoint) != 0)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    "The JSlot output plugin is missing or is a reparse point."));
                return Refused(diagnostics);
            }

            var info = new FileInfo(request.PluginPath.Value);
            if (info.Length <= 0 || info.Length > MaximumOutputPluginBytes)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    "The JSlot output plugin exceeds the bounded read-only binding limit."));
                return Refused(diagnostics);
            }

            await using var stream = new FileStream(
                request.PluginPath.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            Sha256Hash hash = new(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false)));
            long byteLength = stream.Length;

            PluginInspection inspection = await _pluginReader.ReadAsync(
                new PluginReadRequest(request.Edition, request.PluginPath),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(inspection.Diagnostics);
            if (inspection.Diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error) ||
                !PluginEquals(inspection.Plugin, request.ExpectedPlugin))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    "The reopened output plugin identity did not match the requested file."));
                return Refused(diagnostics);
            }

            PluginRecordSummary[] matches = inspection.Records
                .Where(record => record.IsNpc &&
                    record.FormId == request.TargetNpcFormId)
                .ToArray();
            if (matches.Length != 1 ||
                matches[0].OwnerPlugin is not { } owner ||
                !PluginEquals(owner, request.ExpectedPlugin) ||
                matches[0].NpcMetadata is not { } metadata ||
                metadata.HeadParts.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    "The output plugin did not contain exactly one target NPC with a complete PNAM list."));
                return Refused(diagnostics);
            }

            ImmutableArray<PluginName> masters = inspection.Masters;
            if (masters.IsDefaultOrEmpty ||
                masters.Length != masters
                    .Select(item => item.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count())
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.OutputMasterMissing,
                    "The output plugin master table was empty or contained duplicate identities."));
                return Refused(diagnostics);
            }

            return new RaceMenuJslotOutputPluginBindingReadResult(
                true,
                new RaceMenuSelectedDependencyManifestOutputPluginBinding(
                    request.ExpectedPlugin,
                    hash,
                    byteLength,
                    masters,
                    metadata.HeadParts),
                diagnostics.ToImmutable())
            {
                FullPnam = metadata.HeadParts
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PluginReadDiagnosticException exception)
        {
            diagnostics.Add(Error(exception.Code, exception.Message));
            return Refused(diagnostics);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException or
                                           FormatException or
                                           OverflowException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                $"The output plugin could not be reopened read-only: {exception.Message}"));
            return Refused(diagnostics);
        }
    }

    private static bool PluginEquals(PluginName left, PluginName right) =>
        string.Equals(left.Value, right.Value,
            StringComparison.OrdinalIgnoreCase);

    private static RaceMenuJslotOutputPluginBindingReadResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
