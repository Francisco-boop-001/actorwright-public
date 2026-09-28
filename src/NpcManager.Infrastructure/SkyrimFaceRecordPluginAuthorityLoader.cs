using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Admits only explicitly named ordinary plugin files below one copied K-local
/// Data root and binds their bytes in the caller's ascending load order.
/// </summary>
public sealed class SkyrimFaceRecordPluginAuthorityLoader(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) : ISkyrimFaceRecordPluginAuthorityLoader
{
    private const int MaximumPlugins = 64;
    private const long MaximumPluginBytes = 2L * 1024 * 1024 * 1024;

    public async ValueTask<SkyrimFaceRecordPluginAuthorityResult> LoadAsync(
        SkyrimFaceRecordPluginAuthorityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        if (request.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error("skyrim-record-authority-edition",
                "Skyrim face-record authorities support Skyrim Special Edition only."));
        }
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, request.DataRoot));
        if (!request.DataRoot.IsUnder(labRoot) || request.DataRoot == labRoot ||
            !Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(Error("skyrim-record-authority-data-root",
                "The copied Data root must be an existing directory below the K-local lab root."));
        }
        else if (TryTraverseReparsePoint(
                     request.DataRoot.Value, out string? traversalError))
        {
            diagnostics.Add(Error("skyrim-record-authority-data-reparse",
                traversalError ??
                "The copied Data root may not traverse a reparse point."));
        }
        if (request.PluginOrder.IsDefaultOrEmpty || request.PluginOrder.Length > MaximumPlugins ||
            request.PluginOrder.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
        {
            diagnostics.Add(Error("skyrim-record-authority-plugin-order",
                $"PluginOrder must contain 1-{MaximumPlugins} distinct plugin names in ascending load order."));
        }
        ImmutableArray<SkyrimFaceRecordPluginAuthority> staged =
            request.StagedPluginAuthorities.IsDefault
                ? []
                : request.StagedPluginAuthorities;
        if (staged.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != staged.Length ||
            staged.Any(item => !request.PluginOrder.Any(plugin =>
                string.Equals(plugin.Value, item.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase))))
        {
            diagnostics.Add(Error("skyrim-record-authority-staged-shape",
                "Staged plugin authorities must be distinct members of PluginOrder."));
        }
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        var authorities = ImmutableArray.CreateBuilder<SkyrimFaceRecordPluginAuthority>(
            request.PluginOrder.Length);
        foreach (PluginName plugin in request.PluginOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkspacePath path;
            SkyrimFaceRecordPluginAuthority? stagedAuthority = staged
                .SingleOrDefault(item => string.Equals(
                    item.Plugin.Value, plugin.Value,
                    StringComparison.OrdinalIgnoreCase));
            try
            {
                path = stagedAuthority?.Path ?? new WorkspacePath(
                    Path.Combine(request.DataRoot.Value, plugin.Value));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error("skyrim-record-authority-path", exception.Message));
                continue;
            }
            bool admittedStagedPath = stagedAuthority is not null &&
                                      path.IsUnder(labRoot) &&
                                      path != labRoot &&
                                      string.Equals(Path.GetFileName(path.Value),
                                          plugin.Value,
                                          StringComparison.OrdinalIgnoreCase);
            bool admittedDataPath = path.IsUnder(request.DataRoot) &&
                                    string.Equals(Path.GetDirectoryName(path.Value),
                                        request.DataRoot.Value,
                                        StringComparison.OrdinalIgnoreCase);
            if (!admittedStagedPath && !admittedDataPath)
            {
                diagnostics.Add(Error("skyrim-record-authority-path",
                    $"Plugin '{plugin}' must be an immediate child of the copied Data root."));
                continue;
            }
            if (stagedAuthority is not null)
            {
                diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(
                    labRoot, path));
                if (TryTraverseReparsePoint(path.Value,
                        out string? stagedTraversalError))
                {
                    diagnostics.Add(Error(
                        "skyrim-record-authority-staged-reparse",
                        stagedTraversalError ??
                        $"Staged plugin '{plugin}' traverses a reparse point."));
                    continue;
                }
            }

            try
            {
                var info = new FileInfo(path.Value);
                if (!info.Exists || info.Length <= 0 || info.Length > MaximumPluginBytes ||
                    info.Attributes.HasFlag(FileAttributes.Directory) ||
                    info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("skyrim-record-authority-file",
                        $"Plugin '{plugin}' is not an admitted ordinary file."));
                    continue;
                }

                await using var stream = new FileStream(path.Value, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = new Sha256Hash(Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
                if (stagedAuthority is not null &&
                    hash != stagedAuthority.ExpectedSha256)
                {
                    diagnostics.Add(Error("skyrim-record-authority-staged-hash",
                        $"Staged plugin '{plugin}' no longer matches its declared SHA-256."));
                    continue;
                }
                authorities.Add(new SkyrimFaceRecordPluginAuthority(plugin, path, hash));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               ArgumentException or
                                               NotSupportedException)
            {
                diagnostics.Add(Error("skyrim-record-authority-read",
                    $"Plugin '{plugin}' could not be fingerprinted: {exception.Message}"));
            }
        }

        return HasErrors(diagnostics)
            ? Refused(diagnostics)
            : new SkyrimFaceRecordPluginAuthorityResult(
                true, authorities.ToImmutable(), diagnostics.ToImmutable());
    }

    private bool TryTraverseReparsePoint(string path, out string? error)
    {
        try
        {
            var current = path;
            while (!string.Equals(current, labRoot.Value, StringComparison.OrdinalIgnoreCase))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    error = "The copied Data root may not traverse a reparse point.";
                    return true;
                }
                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    error = "The copied Data root could not be proven below the lab root.";
                    return true;
                }
                current = parent;
            }
            error = null;
            return false;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            error = $"The copied Data root could not be qualified: {exception.Message}";
            return true;
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimFaceRecordPluginAuthorityResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, [], diagnostics.ToImmutable());
}
