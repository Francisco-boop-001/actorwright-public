using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Fingerprints a copied profile using only explicit K-local plugin and
/// load-order inputs. The fingerprint is evidence, not live-provider proof.
/// </summary>
public sealed class ProfileScanService(
    IPluginLoadOrderService loadOrderService,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IProfileScanService
{
    public async ValueTask<ProfileScanResult> ScanAsync(
        ProfileScanRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = policy.Evaluate(labRoot, request.DataRoot).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Empty(request, diagnostics.ToImmutable());
        if (!Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(new Diagnostic("data-root-missing", DiagnosticSeverity.Error,
                "The explicit Data root does not exist."));
            return Empty(request, diagnostics.ToImmutable());
        }

        var plugins = ImmutableArray.CreateBuilder<ProfilePluginFingerprint>();
        foreach (var path in EnumeratePlugins(request.DataRoot, diagnostics))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PluginName pluginName;
            try { pluginName = new PluginName(Path.GetFileName(path)); }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("plugin-name-invalid", DiagnosticSeverity.Error, exception.Message));
                continue;
            }
            if (IsReparsePoint(path, diagnostics, pluginName.Value)) continue;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                plugins.Add(new ProfilePluginFingerprint(pluginName, stream.Length,
                    new Sha256Hash(Convert.ToHexString(hash))));
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("plugin-fingerprint-failed", DiagnosticSeverity.Error,
                    $"Plugin '{pluginName.Value}' could not be fingerprinted: {exception.Message}"));
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("plugin-fingerprint-denied", DiagnosticSeverity.Error,
                    $"Plugin '{pluginName.Value}' could not be fingerprinted: {exception.Message}"));
            }
        }

        Sha256Hash? loadOrderHash = null;
        bool? loadOrderValid = null;
        if (request.LoadOrderPath is { } loadOrderPath)
        {
            var loadOrder = await loadOrderService.ResolveAsync(new PluginLoadOrderRequest(
                request.Edition, request.DataRoot, loadOrderPath), cancellationToken);
            diagnostics.AddRange(loadOrder.Diagnostics);
            loadOrderHash = loadOrder.SourceHash;
            loadOrderValid = loadOrder.IsValid;
        }

        var fingerprintInput = new StringBuilder()
            .Append(request.Edition.ToWireName()).Append('\n')
            .AppendJoin('\n', plugins.OrderBy(item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Plugin.Value}|{item.Bytes}|{item.Sha256.Value}"));
        fingerprintInput.Append('\n').Append(loadOrderHash?.Value ?? "<no-load-order>");
        var fingerprint = new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput.ToString()))));
        return new ProfileScanResult(request.Edition, request.DataRoot,
            plugins.OrderBy(item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            loadOrderHash, loadOrderValid, fingerprint, diagnostics.ToImmutable());
    }

    private static ProfileScanResult Empty(ProfileScanRequest request, ImmutableArray<Diagnostic> diagnostics) =>
        new(request.Edition, request.DataRoot, [], null, null,
            new Sha256Hash(new string('0', 64)), diagnostics);

    private static ImmutableArray<string> EnumeratePlugins(
        WorkspacePath dataRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            return Directory.EnumerateFiles(dataRoot.Value)
                .Where(IsPlugin)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-enumeration-failed", DiagnosticSeverity.Error, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-enumeration-denied", DiagnosticSeverity.Error, exception.Message));
        }
        return [];
    }

    private static bool IsReparsePoint(
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string plugin)
    {
        try
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-attributes-failed", DiagnosticSeverity.Error,
                $"Plugin '{plugin}' attributes could not be read: {exception.Message}"));
            return true;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("plugin-attributes-denied", DiagnosticSeverity.Error,
                $"Plugin '{plugin}' attributes could not be read: {exception.Message}"));
            return true;
        }

        diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error,
            $"Plugin '{plugin}' is a reparse point and cannot be read."));
        return true;
    }

    private static bool IsPlugin(string path) =>
        Path.GetExtension(path).Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esl", StringComparison.OrdinalIgnoreCase);
}
