using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reads the pinned reference marker and known FaceGen sidecar locations without mutating the
/// copied Data root. Plugin bytes are treated as untrusted at this boundary and every discovered
/// artifact is hash-bound for downstream review.
/// </summary>
public sealed class GeneratedArtifactScanService(
    IWorkspacePolicy policy,
    IPluginReader pluginReader,
    WorkspacePath labRoot) : IGeneratedArtifactScanService
{
    private const int PluginHeaderLength = 24;
    private const int MaxPluginHeaderBodyBytes = 16 * 1024 * 1024;
    private const long MaxSidecarBytes = 512L * 1024 * 1024;
    private const int MaxPluginCount = 10_000;
    private const int MaxSidecarCount = 100_000;

    public async ValueTask<GeneratedArtifactScanResult> ScanAsync(
        GeneratedArtifactScanRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        if (HasErrors(diagnostics))
            return EmptyResult(request, diagnostics.ToImmutable());

        if (!Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(new Diagnostic("generated-scan-data-root-missing", DiagnosticSeverity.Error,
                "The copied Data root does not exist."));
            return EmptyResult(request, diagnostics.ToImmutable());
        }

        if (IsReparsePoint(request.DataRoot.Value, diagnostics, "generated-scan-data-root-reparse"))
            return EmptyResult(request, diagnostics.ToImmutable());

        var plugins = await ScanGeneratedPluginsAsync(request, diagnostics, cancellationToken);
        var sidecars = await ScanFaceGenSidecarsAsync(request, plugins, diagnostics, cancellationToken);
        var orderedPlugins = plugins
            .OrderBy(entry => entry.Plugin.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Plugin.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var orderedSidecars = sidecars
            .OrderBy(entry => entry.RelativePath.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.RelativePath.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        return new GeneratedArtifactScanResult(request.Edition, request.DataRoot,
            GeneratedArtifactMarkers.ReferenceAuthor, orderedPlugins, orderedSidecars, diagnostics.ToImmutable());
    }

    private async ValueTask<ImmutableArray<GeneratedPluginScanEntry>> ScanGeneratedPluginsAsync(
        GeneratedArtifactScanRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        ImmutableArray<string> paths;
        try
        {
            var files = new List<string>();
            foreach (var extension in new[] { "*.esp", "*.esm", "*.esl" })
                files.AddRange(Directory.EnumerateFiles(request.DataRoot.Value, extension, SearchOption.TopDirectoryOnly));
            paths = files.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToImmutableArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("generated-scan-plugin-enumeration-failed", DiagnosticSeverity.Error,
                $"Generated plugin enumeration failed: {exception.Message}"));
            return [];
        }

        if (paths.Length > MaxPluginCount)
        {
            diagnostics.Add(new Diagnostic("generated-scan-plugin-count-limit", DiagnosticSeverity.Error,
                $"The Data root contains more than {MaxPluginCount} plugin files."));
            return [];
        }

        var entries = ImmutableArray.CreateBuilder<GeneratedPluginScanEntry>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(path, diagnostics, "generated-scan-plugin-reparse"))
                continue;

            PluginName plugin;
            try { plugin = new PluginName(Path.GetFileName(path)); }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("generated-scan-plugin-name-invalid", DiagnosticSeverity.Error,
                    $"Plugin '{Path.GetFileName(path)}' is not a safe plugin name: {exception.Message}"));
                continue;
            }

            string? author;
            try
            {
                author = await ReadTes4AuthorAsync(new WorkspacePath(path), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                diagnostics.Add(new Diagnostic("generated-scan-plugin-header-invalid", DiagnosticSeverity.Warning,
                    $"Plugin '{plugin.Value}' could not be inspected: {exception.Message}"));
                continue;
            }

            if (!string.Equals(author, GeneratedArtifactMarkers.ReferenceAuthor, StringComparison.Ordinal))
                continue;

            Sha256Hash? sha256 = null;
            try
            {
                sha256 = await HashAsync(new WorkspacePath(path), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("generated-scan-plugin-hash-failed", DiagnosticSeverity.Error,
                    $"Generated plugin '{plugin.Value}' could not be hashed: {exception.Message}"));
            }

            var npcFormIds = ImmutableArray<FormId>.Empty;
            var readSucceeded = false;
            try
            {
                // Mutagen is isolated behind IPluginReader; malformed plugin exceptions are contained
                // here so one untrusted generated file cannot abort the complete Data-root scan.
                var inspection = await pluginReader.ReadAsync(
                    new PluginReadRequest(request.Edition, new WorkspacePath(path)), cancellationToken);
                npcFormIds = inspection.Records.Where(record => record.IsNpc)
                    .Select(record => record.FormId)
                    .Distinct()
                    .OrderBy(formId => formId.Value)
                    .ToImmutableArray();
                diagnostics.AddRange(inspection.Diagnostics);
                readSucceeded = !inspection.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                diagnostics.Add(new Diagnostic("generated-scan-plugin-read-failed", DiagnosticSeverity.Error,
                    $"Generated plugin '{plugin.Value}' could not be read: {exception.GetType().Name}: {exception.Message}"));
            }

            entries.Add(new GeneratedPluginScanEntry(plugin, new WorkspacePath(path),
                GeneratedArtifactMarkers.ReferenceAuthor,
                sha256, readSucceeded, npcFormIds));
        }

        return entries.ToImmutable();
    }

    private static async ValueTask<ImmutableArray<GeneratedSidecarEntry>> ScanFaceGenSidecarsAsync(
        GeneratedArtifactScanRequest request,
        ImmutableArray<GeneratedPluginScanEntry> plugins,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var entries = ImmutableArray.CreateBuilder<GeneratedSidecarEntry>();
        foreach (var plugin in plugins)
        {
            foreach (var directory in SidecarDirectories(request.Edition))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directoryPath = Path.Combine(request.DataRoot.Value, directory.RelativeDirectory, plugin.Plugin.Value);
                if (!Directory.Exists(directoryPath)) continue;
                if (ContainsReparsePoint(directoryPath, request.DataRoot.Value, diagnostics,
                        "generated-scan-sidecar-directory-reparse")) continue;

                string[] files;
                try
                {
                    files = Directory.EnumerateFiles(directoryPath, "*", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                        .ThenBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                        .ToArray();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new Diagnostic("generated-scan-sidecar-enumeration-failed", DiagnosticSeverity.Error,
                        $"FaceGen sidecar directory '{directoryPath}' could not be scanned: {exception.Message}"));
                    continue;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReparsePoint(file, diagnostics, "generated-scan-sidecar-reparse")) continue;
                    var fileName = Path.GetFileName(file);
                    if (!TryParseSidecarName(fileName, directory.Kinds, out var kind, out var formId, out var variant))
                    {
                        diagnostics.Add(new Diagnostic("generated-scan-sidecar-unrecognized", DiagnosticSeverity.Warning,
                            $"Unrecognized file '{fileName}' was found in the generated FaceGen sidecar directory."));
                        continue;
                    }

                    var info = new FileInfo(file);
                    if (info.Length > MaxSidecarBytes)
                    {
                        diagnostics.Add(new Diagnostic("generated-scan-sidecar-size-limit", DiagnosticSeverity.Error,
                            $"FaceGen sidecar '{fileName}' exceeds the {MaxSidecarBytes} byte safety limit."));
                        continue;
                    }

                    Sha256Hash hash;
                    try { hash = await HashAsync(new WorkspacePath(file), cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        diagnostics.Add(new Diagnostic("generated-scan-sidecar-hash-failed", DiagnosticSeverity.Error,
                            $"FaceGen sidecar '{fileName}' could not be hashed: {exception.Message}"));
                        continue;
                    }

                    var relative = Path.GetRelativePath(request.DataRoot.Value, file).Replace('\\', '/');
                    try
                    {
                        entries.Add(new GeneratedSidecarEntry(plugin.Plugin, kind, variant,
                            new AssetPath(relative), new WorkspacePath(file), formId, info.Length, hash));
                    }
                    catch (ArgumentException exception)
                    {
                        diagnostics.Add(new Diagnostic("generated-scan-sidecar-path-invalid", DiagnosticSeverity.Error,
                            $"FaceGen sidecar '{fileName}' produced an invalid relative path: {exception.Message}"));
                    }

                    if (entries.Count > MaxSidecarCount)
                    {
                        diagnostics.Add(new Diagnostic("generated-scan-sidecar-count-limit", DiagnosticSeverity.Error,
                            $"The scan found more than {MaxSidecarCount} FaceGen sidecars."));
                        return entries.ToImmutable();
                    }
                }
            }
        }

        return entries.ToImmutable();
    }

    private static ImmutableArray<SidecarDirectory> SidecarDirectories(GameEdition edition)
    {
        var common = new List<SidecarDirectory>
        {
            new([GeneratedSidecarKind.FaceGeom], "Meshes/Actors/Character/FaceGenData/FaceGeom")
        };
        if (edition == GameEdition.Fallout4)
        {
            common.Add(new([
                    GeneratedSidecarKind.FaceCustomizationDiffuse,
                    GeneratedSidecarKind.FaceCustomizationNormal,
                    GeneratedSidecarKind.FaceCustomizationSpecular
                ],
                "Textures/Actors/Character/FaceCustomization"));
        }
        else
        {
            common.Add(new([GeneratedSidecarKind.FaceTint, GeneratedSidecarKind.FaceDetailNeutral],
                "Textures/Actors/Character/FaceGenData/FaceTint"));
            common.Add(new([GeneratedSidecarKind.FaceDiffuse],
                "Textures/Actors/Character/FaceGenData/FaceDiffuse"));
            common.Add(new([GeneratedSidecarKind.FaceNormal],
                "Textures/Actors/Character/FaceGenData/FaceNormal"));
        }

        return common.ToImmutableArray();
    }

    private static bool TryParseSidecarName(string fileName, ImmutableArray<GeneratedSidecarKind> kinds,
        out GeneratedSidecarKind kind, out FormId? formId, out GeneratedSidecarVariant variant)
    {
        kind = default;
        formId = null;
        variant = GeneratedSidecarVariant.Canonical;
        foreach (var candidate in kinds)
        {
            if (TryParseSidecarName(fileName, candidate, out formId, out variant))
            {
                kind = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryParseSidecarName(string fileName, GeneratedSidecarKind kind,
        out FormId? formId, out GeneratedSidecarVariant variant)
    {
        formId = null;
        variant = GeneratedSidecarVariant.Canonical;
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.EndsWith("_2", StringComparison.OrdinalIgnoreCase))
        {
            variant = GeneratedSidecarVariant.DebugSandbox;
            stem = stem[..^2];
        }

        if (kind == GeneratedSidecarKind.FaceDetailNeutral)
            return string.Equals(stem, "facedetailneutral", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(extension, ".dds", StringComparison.OrdinalIgnoreCase);

        if (kind == GeneratedSidecarKind.FaceCustomizationDiffuse ||
            kind == GeneratedSidecarKind.FaceCustomizationNormal ||
            kind == GeneratedSidecarKind.FaceCustomizationSpecular)
        {
            var suffix = kind switch
            {
                GeneratedSidecarKind.FaceCustomizationDiffuse => "_d",
                GeneratedSidecarKind.FaceCustomizationNormal => "_msn",
                _ => "_s"
            };
            if (!stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(extension, ".dds", StringComparison.OrdinalIgnoreCase)) return false;
            stem = stem[..^suffix.Length];
        }
        else if (!string.Equals(extension, kind == GeneratedSidecarKind.FaceGeom ? ".nif" : ".dds",
                     StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (stem.Length != 8 || !FormId.TryParse(stem, out var parsed)) return false;
        formId = parsed;
        return true;
    }

    private static async ValueTask<string?> ReadTes4AuthorAsync(WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length < PluginHeaderLength)
            throw new InvalidDataException("Plugin is shorter than a TES4 header.");

        var header = new byte[PluginHeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException("Plugin does not begin with a TES4 record.");

        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (bodyLength > MaxPluginHeaderBodyBytes || bodyLength > stream.Length - PluginHeaderLength)
            throw new InvalidDataException("TES4 header body exceeds the safe readable range.");
        var body = new byte[bodyLength];
        await ReadExactlyAsync(stream, body, cancellationToken);

        var position = 0;
        uint? extendedSize = null;
        while (position < body.Length)
        {
            if (body.Length - position < 6)
                throw new InvalidDataException("TES4 subrecord header is truncated.");
            var signature = Encoding.ASCII.GetString(body, position, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(position + 4, 2));
            position += 6;
            if (signature == "XXXX")
            {
                if (size != 4 || body.Length - position < 4)
                    throw new InvalidDataException("TES4 XXXX subrecord is malformed.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(position, 4));
                position += 4;
                continue;
            }

            var actualSize = extendedSize ?? size;
            extendedSize = null;
            if (actualSize > int.MaxValue || actualSize > body.Length - position)
                throw new InvalidDataException($"TES4 subrecord {signature} exceeds the header body.");
            if (signature == "CNAM")
            {
                var value = body.AsSpan(position, (int)actualSize);
                var text = Encoding.UTF8.GetString(value).TrimEnd('\0').Trim();
                return text;
            }
            position += (int)actualSize;
        }

        return null;
    }

    private static async ValueTask<Sha256Hash> HashAsync(WorkspacePath path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var digest = await sha256.ComputeHashAsync(stream, cancellationToken);
        return new Sha256Hash(Convert.ToHexString(digest));
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (count == 0) throw new EndOfStreamException("Unexpected end of plugin header.");
            read += count;
        }
    }

    private static bool IsReparsePoint(string path, ImmutableArray<Diagnostic>.Builder diagnostics, string code)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error,
                    $"The path '{path}' is a reparse point and cannot be trusted."));
                return true;
            }
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("generated-scan-attributes-failed", DiagnosticSeverity.Error,
                $"Could not inspect '{path}': {exception.Message}"));
            return true;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("generated-scan-attributes-denied", DiagnosticSeverity.Error,
                $"Could not inspect '{path}': {exception.Message}"));
            return true;
        }

        return false;
    }

    private static bool ContainsReparsePoint(string path, string root,
        ImmutableArray<Diagnostic>.Builder diagnostics, string code)
    {
        var current = Path.GetFullPath(path);
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (true)
        {
            if (!IsPathUnder(current, boundary))
            {
                diagnostics.Add(new Diagnostic("generated-scan-sidecar-path-outside-root", DiagnosticSeverity.Error,
                    $"Generated sidecar path '{path}' escaped the declared Data root."));
                return true;
            }
            if (IsReparsePoint(current, diagnostics, code)) return true;
            if (string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase)) return false;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("generated-scan-sidecar-parent-invalid", DiagnosticSeverity.Error,
                    $"Generated sidecar path '{path}' has no safe parent chain."));
                return true;
            }
            current = parent;
        }
    }

    private static bool IsPathUnder(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static GeneratedArtifactScanResult EmptyResult(GeneratedArtifactScanRequest request,
        ImmutableArray<Diagnostic> diagnostics) => new(request.Edition, request.DataRoot,
        GeneratedArtifactMarkers.ReferenceAuthor, [], [], diagnostics);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private sealed record SidecarDirectory(
        ImmutableArray<GeneratedSidecarKind> Kinds,
        string RelativeDirectory);
}
