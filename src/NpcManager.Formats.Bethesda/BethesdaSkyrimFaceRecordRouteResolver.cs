using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Admits an explicit, hash-bound K-local Skyrim plugin order and resolves only
/// the typed RACE/HDPT/KYWD fields used by the FaceGen input planner.
/// </summary>
public sealed class BethesdaSkyrimFaceRecordRouteResolver(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) : ISkyrimFaceRecordRouteResolver
{
    private const int MaximumPlugins = 64;
    private const int MaximumSelectedHeadParts = 128;
    private const long MaximumPluginBytes = 2L * 1024 * 1024 * 1024;

    public async ValueTask<SkyrimFaceRecordRouteResult> ResolveAsync(
        SkyrimFaceRecordRouteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequestShape(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        foreach (var authority in request.PluginOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateProviderPath(authority, diagnostics);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        foreach (var authority in request.PluginOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await VerifyProviderAsync(authority, diagnostics, cancellationToken);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimFaceRecordCatalog catalog;
        try
        {
            catalog = BethesdaSkyrimFaceRecordCatalogLoader.Load(
                request.PluginOrder,
                cancellationToken,
                requireWinningRecordEvidence: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("skyrim-face-record-provider-malformed",
                $"The copied plugin order could not be decoded as Skyrim SE records: {exception.Message}"));
            return Refused(diagnostics);
        }

        SkyrimFaceRecordRouteRequest normalizedRequest =
            NormalizeSelectedHeadParts(request, catalog, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        return BethesdaSkyrimFaceRecordGraphResolver.Resolve(
            normalizedRequest,
            catalog,
            diagnostics.ToImmutable());
    }

    private static SkyrimFaceRecordRouteRequest NormalizeSelectedHeadParts(
        SkyrimFaceRecordRouteRequest request,
        SkyrimFaceRecordCatalog catalog,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.SelectedHeadParts.IsDefaultOrEmpty ||
            catalog.LightProviderPlugins is not { Count: > 0 } lightProviders)
        {
            return request;
        }

        var normalized = ImmutableArray.CreateBuilder<SkyrimFaceRecordHeadPartSelection>(
            request.SelectedHeadParts.Length);
        foreach (var selection in request.SelectedHeadParts)
        {
            if (selection is null)
            {
                normalized.Add(selection!);
                continue;
            }
            if (!lightProviders.Contains(selection.Reference.Plugin.Value ?? string.Empty))
            {
                normalized.Add(selection);
                continue;
            }

            uint localId = selection.Reference.FormId.Value & 0x0000_0FFF;
            if (localId == 0)
            {
                diagnostics.Add(Error("skyrim-face-record-hdpt-formid",
                    $"Selected HDPT {selection.Reference} has a zero provider-local FormID after light-plugin normalization."));
                normalized.Add(selection);
                continue;
            }

            normalized.Add(selection with
            {
                Reference = new FormReference(
                    selection.Reference.Plugin,
                    new FormId(localId))
            });
        }

        return request with { SelectedHeadParts = normalized.ToImmutable() };
    }

    private static void ValidateRequestShape(
        SkyrimFaceRecordRouteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error("skyrim-face-record-edition",
                "Typed HDPT/RACE routing supports Skyrim Special Edition only."));
        }
        if (!Enum.IsDefined(request.Sex))
        {
            diagnostics.Add(Error("skyrim-face-record-sex", "NPC sex is unsupported."));
        }
        if (request.PluginOrder.IsDefaultOrEmpty || request.PluginOrder.Length > MaximumPlugins)
        {
            diagnostics.Add(Error("skyrim-face-record-plugin-count",
                $"PluginOrder must explicitly contain between 1 and {MaximumPlugins} copied plugins."));
            return;
        }
        if (request.SelectedHeadParts.IsDefault)
        {
            diagnostics.Add(Error("skyrim-face-record-selections-shape",
                "SelectedHeadParts must be explicit, even when empty."));
        }
        else if (request.SelectedHeadParts.Length > MaximumSelectedHeadParts)
        {
            diagnostics.Add(Error("skyrim-face-record-selections-shape",
                $"SelectedHeadParts may contain at most {MaximumSelectedHeadParts} rows."));
        }
        if (!IsConstructed(request.Race))
        {
            diagnostics.Add(Error("skyrim-face-record-race-formid",
                "The selected RACE must be a constructed, nonzero plugin-local reference."));
        }
        if (!request.SelectedHeadParts.IsDefault)
        {
            foreach (var selection in request.SelectedHeadParts)
            {
                if (selection is null)
                {
                    diagnostics.Add(Error("skyrim-face-record-selection-null",
                        "SelectedHeadParts may not contain null rows."));
                    continue;
                }
                if (!IsConstructed(selection.Reference))
                {
                    diagnostics.Add(Error("skyrim-face-record-hdpt-formid",
                        "Every selected HDPT must be a constructed, nonzero plugin-local reference."));
                }
            }
        }

        var plugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var authority in request.PluginOrder)
        {
            if (authority is null)
            {
                diagnostics.Add(Error("skyrim-face-record-provider-authority",
                    "PluginOrder may not contain null provider authorities."));
                continue;
            }
            if (string.IsNullOrWhiteSpace(authority.Plugin.Value) ||
                string.IsNullOrWhiteSpace(authority.Path.Value) ||
                string.IsNullOrWhiteSpace(authority.ExpectedSha256.Value))
            {
                diagnostics.Add(Error("skyrim-face-record-provider-authority",
                    "Every provider authority must contain constructed plugin, path, and SHA-256 values."));
                continue;
            }
            if (!plugins.Add(authority.Plugin.Value))
            {
                diagnostics.Add(Error("skyrim-face-record-plugin-duplicate",
                    $"PluginOrder contains plugin '{authority.Plugin}' more than once."));
            }
            if (!paths.Add(authority.Path.Value))
            {
                diagnostics.Add(Error("skyrim-face-record-provider-path-duplicate",
                    $"PluginOrder contains provider path '{authority.Path}' more than once."));
            }
        }
    }

    private void ValidateProviderPath(
        SkyrimFaceRecordPluginAuthority authority,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!authority.Path.IsUnder(labRoot) || authority.Path == labRoot ||
            HasAlternateDataStream(authority.Path.Value))
        {
            diagnostics.Add(Error("skyrim-face-record-provider-outside-workspace",
                $"Provider for '{authority.Plugin}' must be an ordinary file under the K-local lab root."));
            return;
        }

        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, authority.Path));
        if (!string.Equals(Path.GetFileName(authority.Path.Value), authority.Plugin.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("skyrim-face-record-provider-name",
                $"Provider filename for '{authority.Plugin}' must exactly identify that plugin."));
        }

        try
        {
            var current = authority.Path.Value;
            while (!string.Equals(current, labRoot.Value, StringComparison.OrdinalIgnoreCase))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("skyrim-face-record-provider-reparse",
                        $"Provider for '{authority.Plugin}' traverses a reparse point."));
                    return;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Error("skyrim-face-record-provider-outside-workspace",
                        $"Provider for '{authority.Plugin}' could not be proven inside the lab root."));
                    return;
                }
                current = parent;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Add(Error("skyrim-face-record-provider-path",
                $"Provider path for '{authority.Plugin}' could not be qualified: {exception.Message}"));
        }
    }

    private static async ValueTask VerifyProviderAsync(
        SkyrimFaceRecordPluginAuthority authority,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(authority.Path.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumPluginBytes ||
                info.Attributes.HasFlag(FileAttributes.Directory) ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("skyrim-face-record-provider-file",
                    $"Provider for '{authority.Plugin}' must be an existing, nonempty ordinary file no larger than {MaximumPluginBytes} bytes."));
                return;
            }

            await using var stream = new FileStream(
                authority.Path.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)));
            if (actual != authority.ExpectedSha256)
            {
                diagnostics.Add(Error("skyrim-face-record-provider-hash",
                    $"Provider for '{authority.Plugin}' does not match its expected SHA-256."));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Error("skyrim-face-record-provider-read",
                $"Provider for '{authority.Plugin}' could not be read: {exception.Message}"));
        }
    }

    private static bool HasAlternateDataStream(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }

    private static bool IsConstructed(FormReference reference) =>
        !string.IsNullOrWhiteSpace(reference.Plugin.Value) &&
        reference.FormId.Value is > 0 and <= 0x00FF_FFFF;

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimFaceRecordRouteResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
