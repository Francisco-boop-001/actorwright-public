using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed class BethesdaAssetIndexer : IAssetIndexer
{
    public ValueTask<AssetIndex> IndexAsync(AssetIndexRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providers = ImmutableArray.CreateBuilder<AssetProvider>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var root = request.DataRoot.Value;
        if (!Directory.Exists(root))
        {
            diagnostics.Add(new Diagnostic("data-root-missing", DiagnosticSeverity.Error, "The explicit Data root does not exist."));
            return ValueTask.FromResult(new AssetIndex(request.Edition, providers.ToImmutable(), diagnostics.ToImmutable()));
        }

        var files = EnumerateFilesSafely(root, diagnostics, cancellationToken)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            return ValueTask.FromResult(new AssetIndex(
                request.Edition, ImmutableArray<AssetProvider>.Empty, diagnostics.ToImmutable()));
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsArchive(file, request.Edition)) continue;
            if (IsPlugin(file)) continue;
            try
            {
                TryAddProvider(providers, diagnostics, relative, AssetProviderKind.Loose, file,
                    new FileInfo(file).Length, () => File.ReadAllBytes(file), "loose");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("asset-file-inspection-failed", DiagnosticSeverity.Error,
                    $"Asset '{relative}' could not be inspected: {exception.Message}"));
            }
        }

        var release = request.Edition == GameEdition.Fallout4 ? GameRelease.Fallout4 : GameRelease.SkyrimSE;
        foreach (var archivePath in files
                     .Where(file => IsArchive(file, request.Edition))
                     .OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reader = Archive.CreateReader(release, new FilePath(archivePath), new FileSystem());
                foreach (var entry in reader.Files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryAddProvider(providers, diagnostics, entry.Path, AssetProviderKind.Archive, archivePath, entry.Size,
                        entry.GetBytes, Path.GetFileName(archivePath));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("archive-read-failed", DiagnosticSeverity.Error,
                    $"Archive '{Path.GetFileName(archivePath)}' could not be read: {exception.Message}"));
            }
        }

        var sorted = providers.ToImmutable().OrderBy(provider => provider.Path.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(provider => provider.Kind).ThenBy(provider => provider.Source, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        return ValueTask.FromResult(new AssetIndex(request.Edition, sorted, diagnostics.ToImmutable()));
    }

    private static IEnumerable<string> EnumerateFilesSafely(
        string root,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("asset-directory-inspection-failed", DiagnosticSeverity.Error,
                    $"Asset directory '{directory}' could not be inspected: {exception.Message}"));
                continue;
            }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(new Diagnostic("asset-directory-reparse-refused", DiagnosticSeverity.Error,
                    $"Asset directory '{directory}' is a reparse point and cannot enter a trusted index."));
                continue;
            }

            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory);
                children = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("asset-directory-enumeration-failed", DiagnosticSeverity.Error,
                    $"Asset directory '{directory}' could not be enumerated: {exception.Message}"));
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = Path.GetFullPath(file);
                if (full.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                {
                    yield return full;
                }
            }
            foreach (var child in children) pending.Push(child);
        }
    }

    private static bool IsArchive(string path, GameEdition edition) =>
        edition == GameEdition.Fallout4
            ? path.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)
            : path.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlugin(string path) =>
        Path.GetExtension(path).Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".esl", StringComparison.OrdinalIgnoreCase);

    private static void TryAddProvider(
        ImmutableArray<AssetProvider>.Builder providers,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string path,
        AssetProviderKind kind,
        string source,
        long size,
        Func<byte[]> readBytes,
        string sourceLabel)
    {
        try
        {
            var assetPath = new AssetPath(path);
            var hash = Convert.ToHexString(SHA256.HashData(readBytes())).ToLowerInvariant();
            providers.Add(new AssetProvider(assetPath, kind, sourceLabel, size, hash));
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("asset-path-invalid", DiagnosticSeverity.Error,
                $"Asset '{path}' from '{source}' is invalid: {exception.Message}"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("asset-read-failed", DiagnosticSeverity.Error,
                $"Asset '{path}' from '{source}' could not be read: {exception.Message}"));
        }
    }
}
