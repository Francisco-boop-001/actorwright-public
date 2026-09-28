using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

internal sealed class BethesdaSkyrimRaceMenuPaintCatalogScanner(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot)
{
    private const int MaximumArchives = 512;
    private const int MaximumWinningScripts = 50_000;
    private const long MaximumPexBytes = 16L * 1024 * 1024;
    private const long MaximumTotalPexBytes = 1024L * 1024 * 1024;
    private const long MaximumArchiveBytes = 8L * 1024 * 1024 * 1024;

    public async ValueTask<SkyrimPaintCatalogScanResult> ScanAsync(
        WorkspacePath dataRoot,
        ImmutableArray<PluginName> pluginOrder,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, dataRoot));
        ValidateRootAndPlugins(dataRoot, pluginOrder, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<PaintArchivePlanEntry> archives;
        try
        {
            string[] archivePaths = Directory.GetFiles(
                dataRoot.Value,
                "*.bsa",
                SearchOption.TopDirectoryOnly);
            archives = BuildArchivePlan(archivePaths, pluginOrder);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           InvalidDataException)
        {
            diagnostics.Add(Error("paint-catalog-archive-plan", exception.Message));
            return Refused(diagnostics);
        }

        if (archives.Length > MaximumArchives)
        {
            diagnostics.Add(Error(
                "paint-catalog-archive-count",
                $"The selected plugin order admits more than {MaximumArchives} BSA files."));
            return Refused(diagnostics);
        }

        var winners = new Dictionary<string, SkyrimPaintScannedScript>(
            StringComparer.OrdinalIgnoreCase);
        long totalPexBytes = 0;
        int archiveScriptEntries = 0;
        int looseScriptEntries = 0;

        foreach (PaintArchivePlanEntry archive in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = ValidateOrdinaryFile(
                    archive.Path.Value,
                    MaximumArchiveBytes,
                    "BSA");
                Sha256Hash providerHash = await HashFileAsync(
                    archive.Path.Value,
                    cancellationToken).ConfigureAwait(false);
                long openingLength = info.Length;
                DateTime openingWriteTime = info.LastWriteTimeUtc;

                var reader = Archive.CreateReader(
                    GameRelease.SkyrimSE,
                    new FilePath(archive.Path.Value),
                    new FileSystem());
                var seenMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IArchiveFile entry in reader.Files
                             .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(item => item.Path, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryScriptPath(entry.Path, out AssetPath scriptPath)) continue;
                    if (!seenMembers.Add(scriptPath.Value))
                    {
                        throw new InvalidDataException(
                            $"BSA '{archive.Name}' contains duplicate script member '{scriptPath}'.");
                    }
                    byte[] bytes = ReadBoundedArchiveEntry(entry, scriptPath);
                    totalPexBytes = AddTotalBytes(totalPexBytes, bytes.Length);
                    archiveScriptEntries++;
                    winners[scriptPath.Value] = ScanScript(
                        scriptPath,
                        SkyrimRaceMenuPaintProviderKind.Bsa,
                        archive.Path,
                        providerHash,
                        bytes);
                    EnsureWinnerLimit(winners.Count);
                }

                info.Refresh();
                if (!info.Exists || info.Length != openingLength ||
                    info.LastWriteTimeUtc != openingWriteTime)
                {
                    throw new InvalidDataException(
                        $"BSA '{archive.Name}' changed while its scripts were being cataloged.");
                }
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               ArgumentException or
                                               NotSupportedException or
                                               OverflowException or
                                               CryptographicException)
            {
                diagnostics.Add(Error("paint-catalog-archive-read", exception.Message));
                return Refused(diagnostics);
            }
        }

        try
        {
            string scriptsRoot = Path.Combine(dataRoot.Value, "Scripts");
            if (Directory.Exists(scriptsRoot))
            {
                foreach (string path in EnumerateLooseScripts(scriptsRoot, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] bytes = await ReadBoundedFileAsync(path, cancellationToken)
                        .ConfigureAwait(false);
                    totalPexBytes = AddTotalBytes(totalPexBytes, bytes.Length);
                    looseScriptEntries++;
                    var scriptPath = new AssetPath(Path.GetRelativePath(dataRoot.Value, path));
                    Sha256Hash hash = Hash(bytes);
                    winners[scriptPath.Value] = ScanScript(
                        scriptPath,
                        SkyrimRaceMenuPaintProviderKind.Loose,
                        new WorkspacePath(path),
                        hash,
                        bytes);
                    EnsureWinnerLimit(winners.Count);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException or
                                           OverflowException or
                                           CryptographicException)
        {
            diagnostics.Add(Error("paint-catalog-loose-read", exception.Message));
            return Refused(diagnostics);
        }

        return new SkyrimPaintCatalogScanResult(
            true,
            winners.Values
                .OrderBy(item => item.ScriptPath.Value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ScriptPath.Value, StringComparer.Ordinal)
                .ToImmutableArray(),
            archives.Length,
            archiveScriptEntries,
            looseScriptEntries,
            diagnostics.ToImmutable());

        long AddTotalBytes(long current, int added)
        {
            long next = checked(current + added);
            if (next > MaximumTotalPexBytes)
            {
                throw new InvalidDataException(
                    $"Paint-catalog PEX reads exceed {MaximumTotalPexBytes} bytes.");
            }
            return next;
        }
    }

    internal static ImmutableArray<PaintArchivePlanEntry> BuildArchivePlan(
        IEnumerable<string> archivePaths,
        ImmutableArray<PluginName> pluginOrder)
    {
        var pluginBases = pluginOrder
            .Select((plugin, index) => new PluginBase(
                plugin,
                Path.GetFileNameWithoutExtension(plugin.Value),
                index))
            .ToImmutableArray();
        if (pluginBases.GroupBy(item => item.BaseName, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidDataException(
                "Loaded plugin names must have distinct extension-free base names for archive binding.");
        }

        var paths = archivePaths
            .Select(path => new WorkspacePath(path))
            .OrderBy(path => Path.GetFileName(path.Value), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => Path.GetFileName(path.Value), StringComparer.Ordinal)
            .ToImmutableArray();
        var result = ImmutableArray.CreateBuilder<PaintArchivePlanEntry>();
        int sourceOrder = 0;
        foreach (PluginBase plugin in pluginBases.OrderBy(item => item.Index))
        {
            foreach (WorkspacePath path in paths.Where(item =>
                         ArchiveBelongsToPlugin(Path.GetFileName(item.Value), plugin.BaseName)))
            {
                result.Add(new PaintArchivePlanEntry(
                    plugin.Plugin,
                    path,
                    Path.GetFileName(path.Value),
                    sourceOrder++));
            }
        }
        return result.ToImmutable();
    }

    private static bool ArchiveBelongsToPlugin(string archiveName, string pluginBase)
    {
        string archiveBase = Path.GetFileNameWithoutExtension(archiveName);
        return archiveBase.Equals(pluginBase, StringComparison.OrdinalIgnoreCase) ||
               archiveBase.StartsWith(pluginBase + " - ", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateRootAndPlugins(
        WorkspacePath dataRoot,
        ImmutableArray<PluginName> pluginOrder,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Directory.Exists(dataRoot.Value))
        {
            diagnostics.Add(Error(
                "paint-catalog-data-root-missing",
                "The explicit copied Skyrim Data root does not exist."));
        }
        if (string.Equals(
                dataRoot.Value.TrimEnd(Path.DirectorySeparatorChar),
                ActorwrightWorkspace.ResolveRoot().Value,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "paint-catalog-data-root-too-broad",
                "The Skyrim lab root itself cannot be used as a copied Data root."));
        }
        if (pluginOrder.IsDefaultOrEmpty || pluginOrder.Length > 512 ||
            pluginOrder.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != pluginOrder.Length)
        {
            diagnostics.Add(Error(
                "paint-catalog-plugin-order",
                "PluginOrder must contain 1-512 distinct plugins in ascending load order."));
            return;
        }
        if (pluginOrder.Select(item => Path.GetFileNameWithoutExtension(item.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != pluginOrder.Length)
        {
            diagnostics.Add(Error(
                "paint-catalog-plugin-base-ambiguous",
                "PluginOrder cannot contain two plugins with the same extension-free base name."));
            return;
        }
        if (!Directory.Exists(dataRoot.Value)) return;
        foreach (PluginName plugin in pluginOrder)
        {
            string path = Path.Combine(dataRoot.Value, plugin.Value);
            try
            {
                _ = ValidateOrdinaryFile(path, 4L * 1024 * 1024 * 1024, "plugin");
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               ArgumentException)
            {
                diagnostics.Add(Error(
                    "paint-catalog-plugin-authority",
                    $"Loaded plugin '{plugin.Value}' is unavailable: {exception.Message}"));
            }
        }
    }

    private static FileInfo ValidateOrdinaryFile(
        string path,
        long maximumBytes,
        string role)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes ||
            info.Attributes.HasFlag(FileAttributes.Directory) ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"'{path}' is not an admitted ordinary {role} file within the size limit.");
        }
        return info;
    }

    private static byte[] ReadBoundedArchiveEntry(
        IArchiveFile entry,
        AssetPath scriptPath)
    {
        if (entry.Size <= 0 || entry.Size > MaximumPexBytes)
        {
            throw new InvalidDataException(
                $"PEX member '{scriptPath}' exceeds the 1-{MaximumPexBytes} byte limit.");
        }
        byte[] bytes = entry.GetBytes();
        if (bytes.LongLength != entry.Size)
        {
            throw new InvalidDataException(
                $"PEX member '{scriptPath}' changed size during extraction.");
        }
        return bytes;
    }

    private static async ValueTask<byte[]> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileInfo info = ValidateOrdinaryFile(path, MaximumPexBytes, "PEX");
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != info.Length)
            throw new InvalidDataException($"PEX '{path}' changed before it was read.");
        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        info.Refresh();
        if (!info.Exists || info.Length != bytes.LongLength)
            throw new InvalidDataException($"PEX '{path}' changed while it was read.");
        return bytes;
    }

    private static IEnumerable<string> EnumerateLooseScripts(
        string scriptsRoot,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(scriptsRoot));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            FileAttributes directoryAttributes = File.GetAttributes(directory);
            if (directoryAttributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Scripts directory '{directory}' is a reparse point.");

            foreach (string file in Directory.GetFiles(directory)
                         .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!file.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"Loose PEX '{file}' is a reparse point.");
                yield return file;
            }
            foreach (string child in Directory.GetDirectories(directory)
                         .OrderByDescending(item => item, StringComparer.OrdinalIgnoreCase)
                         .ThenByDescending(item => item, StringComparer.Ordinal))
            {
                pending.Push(child);
            }
        }
    }

    private static bool TryScriptPath(string value, out AssetPath scriptPath)
    {
        scriptPath = default;
        try
        {
            var candidate = new AssetPath(value);
            if (!candidate.Value.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase) ||
                !candidate.Value.EndsWith(".pex", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            scriptPath = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static SkyrimPaintScannedScript ScanScript(
        AssetPath scriptPath,
        SkyrimRaceMenuPaintProviderKind providerKind,
        WorkspacePath providerPath,
        Sha256Hash providerHash,
        byte[] bytes)
    {
        Sha256Hash pexHash = Hash(bytes);
        return new SkyrimPaintScannedScript(
            scriptPath,
            providerKind,
            providerPath,
            providerHash,
            pexHash,
            bytes.LongLength,
            BethesdaSkyrimPapyrusPaintParser.Parse(bytes));
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void EnsureWinnerLimit(int count)
    {
        if (count > MaximumWinningScripts)
        {
            throw new InvalidDataException(
                $"Winning PEX count exceeds {MaximumWinningScripts}.");
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimPaintCatalogScanResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, [], 0, 0, 0, diagnostics.ToImmutable());

    private sealed record PluginBase(
        PluginName Plugin,
        string BaseName,
        int Index);
}

internal sealed record PaintArchivePlanEntry(
    PluginName Plugin,
    WorkspacePath Path,
    string Name,
    int SourceOrder);

internal sealed record SkyrimPaintScannedScript(
    AssetPath ScriptPath,
    SkyrimRaceMenuPaintProviderKind ProviderKind,
    WorkspacePath ProviderPath,
    Sha256Hash ProviderSha256,
    Sha256Hash PexSha256,
    long PexLength,
    SkyrimPexPaintParseResult ParseResult);

internal sealed record SkyrimPaintCatalogScanResult(
    bool Accepted,
    ImmutableArray<SkyrimPaintScannedScript> Scripts,
    int AdmittedArchiveCount,
    int ArchiveScriptEntryCount,
    int LooseScriptEntryCount,
    ImmutableArray<Diagnostic> Diagnostics);
