using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Assets;

/// <summary>
/// Resolves a bounded set of exact loose files and Skyrim SE BSA members. The
/// service does not search load order or guess winners: callers must first
/// declare the winning provider, canonical path, lengths, and hashes.
/// </summary>
public sealed class SkyrimAssetContentResolver(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) : ISkyrimAssetContentResolver
{
    private const int MaximumAuthorities = 256;
    private const long MaximumContentBytes = 256L * 1024 * 1024;
    private const long MaximumBatchBytes = 512L * 1024 * 1024;

    public async ValueTask<SkyrimAssetContentResolutionResult> ResolveAsync(
        SkyrimAssetContentResolutionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var resolved = ImmutableArray.CreateBuilder<ResolvedSkyrimAssetContent>();

        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, request.AllowedRoot));
        foreach (var providerPath in request.Authorities.IsDefault
                     ? []
                     : request.Authorities.Select(item => item.ProviderPath)
                         .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(request.AllowedRoot, providerPath));
        }
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        if (!ValidateRequest(request, diagnostics))
        {
            return Refused(diagnostics);
        }

        foreach (var authority in request.Authorities
                     .Where(item => item.Kind == SkyrimAssetContentProviderKind.Loose)
                     .OrderBy(item => item.AssetPath.Value, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResolveLooseAsync(request.AllowedRoot, authority, resolved, diagnostics, cancellationToken);
        }

        foreach (var group in request.Authorities
                     .Where(item => item.Kind == SkyrimAssetContentProviderKind.Bsa)
                     .GroupBy(item => item.ProviderPath.Value, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResolveArchiveAsync(request.AllowedRoot, group.ToImmutableArray(), resolved, diagnostics,
                cancellationToken);
        }

        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        var assets = resolved.ToImmutable()
            .OrderBy(item => item.AssetPath.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new SkyrimAssetContentResolutionResult(true, assets, diagnostics.ToImmutable());
    }

    private static bool ValidateRequest(
        SkyrimAssetContentResolutionRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Authorities.IsDefaultOrEmpty || request.Authorities.Length > MaximumAuthorities)
        {
            diagnostics.Add(Error("asset-content-authority-count-invalid",
                $"Declare between 1 and {MaximumAuthorities} provider authorities."));
            return false;
        }

        var duplicate = request.Authorities
            .GroupBy(item => item.AssetPath.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            diagnostics.Add(Error("asset-content-path-ambiguous",
                $"Canonical asset '{duplicate.Key}' has more than one declared winner."));
        }

        long totalBytes = 0;
        foreach (var authority in request.Authorities)
        {
            if (!IsValidProviderId(authority.ProviderId))
            {
                diagnostics.Add(Error("asset-content-provider-id-invalid",
                    $"Provider ID for '{authority.AssetPath}' must contain 1-128 printable characters without outer whitespace."));
            }

            if (authority.Kind is not (SkyrimAssetContentProviderKind.Loose or SkyrimAssetContentProviderKind.Bsa))
            {
                diagnostics.Add(Error("asset-content-provider-kind-invalid",
                    $"Provider kind for '{authority.AssetPath}' is unsupported."));
            }

            if (authority.ContentLength <= 0 || authority.ContentLength > MaximumContentBytes)
            {
                diagnostics.Add(Error("asset-content-length-invalid",
                    $"Declared length for '{authority.AssetPath}' must be between 1 and {MaximumContentBytes} bytes."));
                continue;
            }

            try
            {
                totalBytes = checked(totalBytes + authority.ContentLength);
            }
            catch (OverflowException)
            {
                totalBytes = long.MaxValue;
            }

            if (authority.Kind == SkyrimAssetContentProviderKind.Loose &&
                authority.ProviderSha256 != authority.ContentSha256)
            {
                diagnostics.Add(Error("asset-content-loose-hash-inconsistent",
                    $"Loose provider and content hashes must be identical for '{authority.AssetPath}'."));
            }

            if (authority.Kind == SkyrimAssetContentProviderKind.Bsa &&
                !Path.GetExtension(authority.ProviderPath.Value).Equals(".bsa", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("asset-content-archive-extension-invalid",
                    $"Skyrim archive provider for '{authority.AssetPath}' must be a .bsa file."));
            }
        }

        if (totalBytes > MaximumBatchBytes)
        {
            diagnostics.Add(Error("asset-content-batch-too-large",
                $"Declared content exceeds the {MaximumBatchBytes}-byte batch limit."));
        }

        foreach (var group in request.Authorities
                     .Where(item => item.Kind == SkyrimAssetContentProviderKind.Bsa)
                     .GroupBy(item => item.ProviderPath.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Select(item => item.ProviderSha256).Distinct().Count() != 1 ||
                group.Select(item => item.ProviderId).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                diagnostics.Add(Error("asset-content-archive-authority-conflict",
                    $"Archive '{group.Key}' has conflicting provider identity or hash declarations."));
            }
        }

        return !HasErrors(diagnostics);
    }

    private static async ValueTask ResolveLooseAsync(
        WorkspacePath allowedRoot,
        SkyrimAssetContentAuthority authority,
        ImmutableArray<ResolvedSkyrimAssetContent>.Builder resolved,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!ValidateProviderPath(allowedRoot, authority.ProviderPath, authority.AssetPath, diagnostics) ||
            !File.Exists(authority.ProviderPath.Value))
        {
            if (!File.Exists(authority.ProviderPath.Value))
            {
                diagnostics.Add(Error("asset-content-provider-missing",
                    $"Loose provider for '{authority.AssetPath}' does not exist."));
            }
            return;
        }

        try
        {
            await using var stream = new FileStream(authority.ProviderPath.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != authority.ContentLength)
            {
                diagnostics.Add(Error("asset-content-length-mismatch",
                    $"Loose asset '{authority.AssetPath}' has length {stream.Length}, expected {authority.ContentLength}."));
                return;
            }

            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)authority.ContentLength));
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            var hash = Hash(bytes);
            if (hash != authority.ProviderSha256)
            {
                diagnostics.Add(Error("asset-content-provider-hash-mismatch",
                    $"Loose provider hash for '{authority.AssetPath}' is {hash}, expected {authority.ProviderSha256}."));
                return;
            }

            resolved.Add(CreateResolved(authority, bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException)
        {
            diagnostics.Add(Error("asset-content-provider-read-failed",
                $"Loose provider for '{authority.AssetPath}' could not be read: {exception.Message}"));
        }
    }

    private static async ValueTask ResolveArchiveAsync(
        WorkspacePath allowedRoot,
        ImmutableArray<SkyrimAssetContentAuthority> authorities,
        ImmutableArray<ResolvedSkyrimAssetContent>.Builder resolved,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var first = authorities[0];
        if (!ValidateProviderPath(allowedRoot, first.ProviderPath, first.AssetPath, diagnostics) ||
            !File.Exists(first.ProviderPath.Value))
        {
            if (!File.Exists(first.ProviderPath.Value))
            {
                diagnostics.Add(Error("asset-content-provider-missing",
                    $"BSA provider '{Path.GetFileName(first.ProviderPath.Value)}' does not exist."));
            }
            return;
        }

        try
        {
            var providerHash = await HashFileAsync(first.ProviderPath.Value, cancellationToken);
            if (providerHash != first.ProviderSha256)
            {
                diagnostics.Add(Error("asset-content-provider-hash-mismatch",
                    $"BSA provider hash is {providerHash}, expected {first.ProviderSha256}."));
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var requested = authorities.ToDictionary(item => item.AssetPath.Value,
                StringComparer.OrdinalIgnoreCase);
            var matches = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var reader = Archive.CreateReader(GameRelease.SkyrimSE, new FilePath(first.ProviderPath.Value),
                new FileSystem());
            foreach (var entry in reader.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssetPath memberPath;
                try
                {
                    memberPath = new AssetPath(entry.Path);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!requested.TryGetValue(memberPath.Value, out var authority))
                {
                    continue;
                }

                if (matches.ContainsKey(memberPath.Value))
                {
                    diagnostics.Add(Error("asset-content-archive-member-ambiguous",
                        $"BSA member '{authority.AssetPath}' occurs more than once."));
                    continue;
                }

                if (entry.Size != authority.ContentLength)
                {
                    diagnostics.Add(Error("asset-content-length-mismatch",
                        $"BSA member '{authority.AssetPath}' has length {entry.Size}, expected {authority.ContentLength}."));
                    continue;
                }

                matches.Add(memberPath.Value, entry.GetBytes());
                cancellationToken.ThrowIfCancellationRequested();
            }

            foreach (var authority in authorities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!matches.TryGetValue(authority.AssetPath.Value, out var bytes))
                {
                    diagnostics.Add(Error("asset-content-archive-member-missing",
                        $"BSA provider does not contain declared member '{authority.AssetPath}'."));
                    continue;
                }

                if (bytes.LongLength != authority.ContentLength)
                {
                    diagnostics.Add(Error("asset-content-length-mismatch",
                        $"Extracted BSA member '{authority.AssetPath}' has length {bytes.LongLength}, expected {authority.ContentLength}."));
                    continue;
                }

                var contentHash = Hash(bytes);
                if (contentHash != authority.ContentSha256)
                {
                    diagnostics.Add(Error("asset-content-hash-mismatch",
                        $"BSA member hash for '{authority.AssetPath}' is {contentHash}, expected {authority.ContentSha256}."));
                    continue;
                }

                resolved.Add(CreateResolved(authority, bytes));
            }

            var closingProviderHash = await HashFileAsync(first.ProviderPath.Value, cancellationToken);
            if (closingProviderHash != providerHash)
            {
                diagnostics.Add(Error("asset-content-provider-changed",
                    "BSA provider changed while its declared members were being resolved."));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or OverflowException)
        {
            diagnostics.Add(Error("asset-content-archive-read-failed",
                $"BSA provider '{Path.GetFileName(first.ProviderPath.Value)}' could not be read: {exception.Message}"));
        }
    }

    private static bool ValidateProviderPath(
        WorkspacePath allowedRoot,
        WorkspacePath providerPath,
        AssetPath assetPath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!providerPath.IsUnder(allowedRoot) || HasAlternateDataStream(providerPath.Value))
        {
            diagnostics.Add(Error("asset-content-provider-outside-root",
                $"Provider for '{assetPath}' must remain under the declared K-local authority root."));
            return false;
        }

        try
        {
            var current = providerPath.Value;
            while (!string.Equals(current, allowedRoot.Value, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    {
                        diagnostics.Add(Error("asset-content-provider-reparse-refused",
                            $"Provider for '{assetPath}' traverses a reparse point."));
                        return false;
                    }
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Error("asset-content-provider-outside-root",
                        $"Provider for '{assetPath}' could not be proven inside the authority root."));
                    return false;
                }
                current = parent;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Add(Error("asset-content-provider-path-invalid",
                $"Provider path for '{assetPath}' could not be qualified: {exception.Message}"));
            return false;
        }

        return true;
    }

    private static bool HasAlternateDataStream(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }

    private static bool IsValidProviderId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value == value.Trim() &&
        value.All(character => !char.IsControl(character));

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static ResolvedSkyrimAssetContent CreateResolved(
        SkyrimAssetContentAuthority authority,
        byte[] bytes) =>
        new(authority.ProviderId, authority.Kind, authority.ProviderPath, authority.ProviderSha256,
            authority.AssetPath, authority.ContentLength, authority.ContentSha256,
            ImmutableCollectionsMarshal.AsImmutableArray(bytes));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimAssetContentResolutionResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, [], diagnostics.ToImmutable());
}
