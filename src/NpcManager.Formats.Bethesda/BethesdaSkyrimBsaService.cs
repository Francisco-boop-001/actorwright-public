using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Writes deterministic, uncompressed Skyrim SE BSA v105 archives and uses
/// Mutagen's independent archive reader for post-write verification.
/// </summary>
public sealed class BethesdaSkyrimBsaService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : ISkyrimBsaService
{
    private const uint Version = 105;
    private const uint ArchiveFlags = 0x0000_0003;
    private const long MaximumMemberBytes = 512L * 1024 * 1024;
    private const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;

    public async ValueTask<SkyrimBsaBuildResult> BuildAsync(
        SkyrimBsaBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateBuild(request).ToBuilder();
        if (HasErrors(diagnostics))
            return new(false, null, diagnostics.ToImmutable());

        string temporary = request.OutputArchive.Value + ".tmp-" +
                           Guid.NewGuid().ToString("N");
        try
        {
            ImmutableArray<BsaInput> inputs = await ReadInputsAsync(
                request,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            if (HasErrors(diagnostics))
                return new(false, null, diagnostics.ToImmutable());

            WriteArchive(temporary, inputs, cancellationToken);
            ImmutableArray<SkyrimBsaMemberArtifact> expected = inputs
                .Select(item => new SkyrimBsaMemberArtifact(
                    item.Path,
                    item.ByteLength,
                    item.Sha256,
                    true))
                .ToImmutableArray();
            SkyrimBsaVerifyResult verification = await VerifyPathAsync(
                temporary,
                expected,
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(verification.Diagnostics);
            if (!verification.Verified || verification.ArchiveSha256 is null ||
                HasErrors(diagnostics))
                return new(false, null, diagnostics.ToImmutable());

            File.Move(
                temporary,
                request.OutputArchive.Value,
                overwrite: false);
            temporary = string.Empty;
            var artifact = new SkyrimBsaBuildArtifact(
                request.OutputArchive,
                verification.ArchiveSha256.Value,
                checked((int)Version),
                false,
                verification.Members,
                false);
            return new(true, artifact, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temporary);
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or OverflowException)
        {
            diagnostics.Add(Error("skyrim-bsa-build-failed", exception.Message));
            return new(false, null, diagnostics.ToImmutable());
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    public ValueTask<SkyrimBsaVerifyResult> VerifyAsync(
        SkyrimBsaVerifyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateArchiveRead(request.Archive, diagnostics);
        if (HasErrors(diagnostics))
            return ValueTask.FromResult(new SkyrimBsaVerifyResult(
                false,
                null,
                [],
                diagnostics.ToImmutable()));
        return VerifyPathAsync(
            request.Archive.Value,
            request.ExpectedMembers,
            cancellationToken);
    }

    public async ValueTask<SkyrimBsaExtractResult> ExtractAsync(
        SkyrimBsaExtractRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateExtract(request).ToBuilder();
        if (HasErrors(diagnostics))
            return new(false, null, diagnostics.ToImmutable());

        string temporary = request.OutputRoot.Value + ".tmp-" +
                           Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(temporary);
            var members =
                ImmutableArray.CreateBuilder<SkyrimBsaMemberArtifact>();
            var paths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var reader = Archive.CreateReader(
                GameRelease.SkyrimSE,
                new FilePath(request.Archive.Value),
                new FileSystem());
            long totalDeclaredBytes = 0;
            foreach (var entry in reader.Files.OrderBy(
                         item => item.Path,
                         StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = new AssetPath(entry.Path);
                if (!paths.Add(path.Value))
                    throw new InvalidDataException(
                        $"Archive contains duplicate member '{path.Value}'.");
                string output = Path.GetFullPath(Path.Combine(
                    temporary,
                    path.Value.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
                if (!IsUnder(output, temporary))
                    throw new InvalidDataException(
                        $"Archive member '{path.Value}' escaped extraction.");
                long declaredSize = entry.Size;
                if (declaredSize <= 0 ||
                    declaredSize > MaximumMemberBytes)
                {
                    diagnostics.Add(Error(
                        "skyrim-bsa-member-size",
                        $"Archive member '{path.Value}' is empty or too large."));
                    return new(
                        false,
                        null,
                        diagnostics.ToImmutable());
                }

                totalDeclaredBytes = checked(
                    totalDeclaredBytes + declaredSize);
                if (totalDeclaredBytes > MaximumArchiveBytes)
                {
                    diagnostics.Add(Error(
                        "skyrim-bsa-archive-size",
                        "The uncompressed Skyrim BSA exceeds 2 GiB."));
                    return new(
                        false,
                        null,
                        diagnostics.ToImmutable());
                }

                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using Stream source = entry.AsStream();
                await using var destination = new FileStream(
                    output,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);
                BethesdaArchiveStreamTransferResult transfer =
                    await BethesdaArchiveStreamTransfer.TransferAsync(
                        source,
                        destination,
                        declaredSize,
                        MaximumMemberBytes,
                        cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
                members.Add(new SkyrimBsaMemberArtifact(
                    path,
                    transfer.ByteLength,
                    transfer.Sha256,
                    true));
            }
            if (members.Count == 0)
                throw new InvalidDataException(
                    "The Skyrim BSA contains no extractable members.");
            Sha256Hash archiveHash = await HashFileAsync(
                request.Archive.Value,
                cancellationToken).ConfigureAwait(false);
            Directory.Move(temporary, request.OutputRoot.Value);
            temporary = string.Empty;
            var artifact = new SkyrimBsaExtractArtifact(
                request.Archive,
                request.OutputRoot,
                archiveHash,
                members.ToImmutable(),
                false);
            return new(true, artifact, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(temporary);
            throw;
        }
        catch (BsaMemberLengthMismatchException exception)
        {
            diagnostics.Add(Error(
                BsaMemberLengthMismatchException.DiagnosticCode,
                exception.Message));
            return new(false, null, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException)
        {
            diagnostics.Add(Error(
                "skyrim-bsa-extract-failed",
                exception.Message));
            return new(false, null, diagnostics.ToImmutable());
        }
        finally
        {
            TryDeleteDirectory(temporary);
        }
    }

    private static async ValueTask<ImmutableArray<BsaInput>> ReadInputsAsync(
        SkyrimBsaBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var inputs = ImmutableArray.CreateBuilder<BsaInput>();
        var canonical = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (AssetPath requested in request.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string archivePath = requested.Value
                .Replace('/', '\\')
                .ToLowerInvariant();
            if (!canonical.Add(archivePath))
            {
                diagnostics.Add(Error(
                    "skyrim-bsa-member-duplicate",
                    $"BSA member '{requested.Value}' is duplicated."));
                continue;
            }
            if (!IsAscii(archivePath))
            {
                diagnostics.Add(Error(
                    "skyrim-bsa-member-encoding",
                    $"BSA member '{requested.Value}' is not an ASCII path."));
                continue;
            }
            string folder = Path.GetDirectoryName(archivePath)?
                .Replace('/', '\\') ?? string.Empty;
            string name = Path.GetFileName(archivePath);
            if (folder.Length == 0 || name.Length == 0 ||
                Encoding.ASCII.GetByteCount(folder) + 1 > byte.MaxValue)
            {
                diagnostics.Add(Error(
                    "skyrim-bsa-member-shape",
                    $"BSA member '{requested.Value}' lacks a valid folder and filename."));
                continue;
            }
            string source = Path.GetFullPath(Path.Combine(
                request.DataRoot.Value,
                requested.Value.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
            if (!IsUnder(source, request.DataRoot.Value) ||
                !File.Exists(source))
            {
                diagnostics.Add(Error(
                    "skyrim-bsa-member-missing",
                    $"BSA member source '{requested.Value}' is missing."));
                continue;
            }
            AddReparseDiagnostic(source, "BSA member", diagnostics);
            var info = new FileInfo(source);
            if (info.Length <= 0 || info.Length > MaximumMemberBytes)
            {
                diagnostics.Add(Error(
                    "skyrim-bsa-member-size",
                    $"BSA member '{requested.Value}' is empty or too large."));
                continue;
            }
            total = checked(total + info.Length);
            if (total > MaximumArchiveBytes)
            {
                diagnostics.Add(Error(
                    "skyrim-bsa-archive-size",
                    "The uncompressed Skyrim BSA would exceed 2 GiB."));
                continue;
            }
            Sha256Hash hash = await HashFileAsync(
                source,
                cancellationToken).ConfigureAwait(false);
            inputs.Add(new BsaInput(
                new AssetPath(archivePath),
                folder,
                name,
                HashName(folder),
                HashName(name),
                source,
                info.Length,
                hash));
        }
        return inputs
            .OrderBy(item => item.FolderHash)
            .ThenBy(item => item.Folder, StringComparer.Ordinal)
            .ThenBy(item => item.FileHash)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static void WriteArchive(
        string path,
        ImmutableArray<BsaInput> inputs,
        CancellationToken cancellationToken)
    {
        if (inputs.IsDefaultOrEmpty)
            throw new InvalidDataException(
                "A Skyrim BSA requires at least one member.");
        BsaFolder[] folders = inputs
            .GroupBy(item => item.Folder, StringComparer.Ordinal)
            .Select(group => new BsaFolder(
                group.Key,
                group.First().FolderHash,
                group.OrderBy(item => item.FileHash)
                    .ThenBy(item => item.Name, StringComparer.Ordinal)
                    .ToImmutableArray()))
            .OrderBy(item => item.Hash)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
        uint folderNameBytes = checked((uint)folders.Sum(item =>
            Encoding.ASCII.GetByteCount(item.Path) + 1));
        uint fileNameBytes = checked((uint)folders.Sum(folder =>
            folder.Files.Sum(item =>
                Encoding.ASCII.GetByteCount(item.Name) + 1)));
        long folderBlocksBytes = folders.Sum(folder =>
            1L + Encoding.ASCII.GetByteCount(folder.Path) + 1L +
            (16L * folder.Files.Length));
        long folderRecordsStart = 36;
        long folderBlocksStart = folderRecordsStart + (24L * folders.Length);
        long dataStart = checked(
            folderBlocksStart + folderBlocksBytes + fileNameBytes);
        if (dataStart + inputs.Sum(item => item.ByteLength) >
            MaximumArchiveBytes)
            throw new InvalidDataException(
                "The complete Skyrim BSA would exceed 2 GiB.");

        var folderOffsets = new Dictionary<string, ulong>(
            StringComparer.Ordinal);
        long blockCursor = folderBlocksStart;
        foreach (BsaFolder folder in folders)
        {
            folderOffsets[folder.Path] = checked(
                (ulong)(blockCursor + fileNameBytes));
            blockCursor +=
                1L + Encoding.ASCII.GetByteCount(folder.Path) + 1L +
                (16L * folder.Files.Length);
        }

        var dataOffsets = new Dictionary<string, uint>(
            StringComparer.OrdinalIgnoreCase);
        long dataCursor = dataStart;
        foreach (BsaInput input in folders.SelectMany(item => item.Files))
        {
            dataOffsets[input.Path.Value] = checked((uint)dataCursor);
            dataCursor += input.ByteLength;
        }

        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.WriteThrough);
        using var writer = new BinaryWriter(
            stream,
            Encoding.ASCII,
            leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("BSA\0"));
        writer.Write(Version);
        writer.Write(36U);
        writer.Write(ArchiveFlags);
        writer.Write(checked((uint)folders.Length));
        writer.Write(checked((uint)inputs.Length));
        writer.Write(folderNameBytes);
        writer.Write(fileNameBytes);
        writer.Write(0U);
        foreach (BsaFolder folder in folders)
        {
            writer.Write(folder.Hash);
            writer.Write(checked((uint)folder.Files.Length));
            writer.Write(0U);
            writer.Write(folderOffsets[folder.Path]);
        }
        foreach (BsaFolder folder in folders)
        {
            byte[] folderBytes = Encoding.ASCII.GetBytes(folder.Path);
            writer.Write(checked((byte)(folderBytes.Length + 1)));
            writer.Write(folderBytes);
            writer.Write((byte)0);
            foreach (BsaInput file in folder.Files)
            {
                writer.Write(file.FileHash);
                writer.Write(checked((uint)file.ByteLength));
                writer.Write(dataOffsets[file.Path.Value]);
            }
        }
        foreach (BsaInput input in folders.SelectMany(item => item.Files))
        {
            writer.Write(Encoding.ASCII.GetBytes(input.Name));
            writer.Write((byte)0);
        }
        foreach (BsaInput input in folders.SelectMany(item => item.Files))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = new FileStream(
                input.Source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            source.CopyTo(stream);
        }
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static async ValueTask<SkyrimBsaVerifyResult> VerifyPathAsync(
        string archivePath,
        ImmutableArray<SkyrimBsaMemberArtifact> expected,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var actual =
            ImmutableArray.CreateBuilder<SkyrimBsaMemberArtifact>();
        try
        {
            AddArchiveLengthDiagnostic(
                archivePath,
                diagnostics);
            if (HasErrors(diagnostics))
            {
                return new(
                    false,
                    null,
                    actual.ToImmutable(),
                    diagnostics.ToImmutable());
            }

            var expectedByPath = expected.ToDictionary(
                item => item.Path.Value,
                StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var reader = Archive.CreateReader(
                GameRelease.SkyrimSE,
                new FilePath(archivePath),
                new FileSystem());
            long totalDeclaredBytes = 0;
            foreach (var entry in reader.Files.OrderBy(
                         item => item.Path,
                         StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = new AssetPath(entry.Path);
                if (!seen.Add(path.Value))
                    throw new InvalidDataException(
                        $"Archive contains duplicate member '{path.Value}'.");
                long declaredSize = entry.Size;
                if (declaredSize <= 0 ||
                    declaredSize > MaximumMemberBytes)
                {
                    diagnostics.Add(Error(
                        "skyrim-bsa-member-size",
                        $"Archive member '{path.Value}' is empty or too large."));
                    return new(
                        false,
                        null,
                        actual.ToImmutable(),
                        diagnostics.ToImmutable());
                }

                totalDeclaredBytes = checked(
                    totalDeclaredBytes + declaredSize);
                if (totalDeclaredBytes > MaximumArchiveBytes)
                {
                    diagnostics.Add(Error(
                        "skyrim-bsa-archive-size",
                        "The uncompressed Skyrim BSA exceeds 2 GiB."));
                    return new(
                        false,
                        null,
                        actual.ToImmutable(),
                        diagnostics.ToImmutable());
                }

                using Stream source = entry.AsStream();
                BethesdaArchiveStreamTransferResult transfer =
                    await BethesdaArchiveStreamTransfer.TransferAsync(
                        source,
                        destination: null,
                        declaredSize,
                        MaximumMemberBytes,
                        cancellationToken).ConfigureAwait(false);
                bool matches = expectedByPath.TryGetValue(
                                   path.Value,
                                   out SkyrimBsaMemberArtifact? expectedRow) &&
                               expectedRow.ByteLength ==
                                   transfer.ByteLength &&
                               expectedRow.Sha256 == transfer.Sha256;
                actual.Add(new SkyrimBsaMemberArtifact(
                    path,
                    transfer.ByteLength,
                    transfer.Sha256,
                    matches));
                if (!matches)
                    diagnostics.Add(Error(
                        "skyrim-bsa-member-mismatch",
                        $"BSA member '{path.Value}' failed its expected size/hash check."));
            }
            foreach (SkyrimBsaMemberArtifact row in expected)
            {
                if (!seen.Contains(row.Path.Value))
                    diagnostics.Add(Error(
                        "skyrim-bsa-member-missing",
                        $"BSA member '{row.Path.Value}' is missing."));
            }
            if (actual.Count != expected.Length)
                diagnostics.Add(Error(
                    "skyrim-bsa-member-count",
                    "The BSA member count differs from the reviewed inventory."));
            Sha256Hash archiveHash = await HashFileAsync(
                archivePath,
                cancellationToken).ConfigureAwait(false);
            return new(
                !HasErrors(diagnostics),
                archiveHash,
                actual.ToImmutable(),
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (BsaMemberLengthMismatchException exception)
        {
            diagnostics.Add(Error(
                BsaMemberLengthMismatchException.DiagnosticCode,
                exception.Message));
            return new(
                false,
                null,
                actual.ToImmutable(),
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException)
        {
            diagnostics.Add(Error(
                "skyrim-bsa-readback-failed",
                exception.Message));
            return new(
                false,
                null,
                actual.ToImmutable(),
                diagnostics.ToImmutable());
        }
    }

    private ImmutableArray<Diagnostic> ValidateBuild(
        SkyrimBsaBuildRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.DataRoot.IsUnder(labRoot))
            diagnostics.Add(Error(
                "skyrim-bsa-data-root",
                "The BSA Data root must remain under the K-only lab root."));
        if (!Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(Error(
                "skyrim-bsa-data-missing",
                "The BSA Data root does not exist."));
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(
                labRoot,
                request.DataRoot));
            AddReparseDiagnostic(
                request.DataRoot.Value,
                "BSA Data root",
                diagnostics);
        }
        ValidateFreshFile(
            request.OutputArchive,
            ".bsa",
            "BSA output",
            diagnostics);
        if (request.Members.IsDefaultOrEmpty)
            diagnostics.Add(Error(
                "skyrim-bsa-members-empty",
                "A Skyrim BSA requires at least one reviewed member."));
        return diagnostics.ToImmutable();
    }

    private ImmutableArray<Diagnostic> ValidateExtract(
        SkyrimBsaExtractRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateArchiveRead(request.Archive, diagnostics);
        if (!request.OutputRoot.IsUnder(labRoot))
            diagnostics.Add(Error(
                "skyrim-bsa-extract-root",
                "The loose extraction root must remain under the K-only lab root."));
        if (File.Exists(request.OutputRoot.Value) ||
            Directory.Exists(request.OutputRoot.Value))
            diagnostics.Add(Error(
                "skyrim-bsa-extract-exists",
                "BSA extraction never overwrites an output."));
        string? parent = Path.GetDirectoryName(request.OutputRoot.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(Error(
                "skyrim-bsa-extract-parent",
                "The loose extraction parent must already exist."));
        else
        {
            diagnostics.AddRange(policy.Evaluate(
                labRoot,
                new WorkspacePath(parent)));
            AddReparseDiagnostic(parent, "extraction parent", diagnostics);
        }
        return diagnostics.ToImmutable();
    }

    private void ValidateArchiveRead(
        WorkspacePath archive,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!archive.IsUnder(labRoot))
            diagnostics.Add(Error(
                "skyrim-bsa-archive-root",
                "The Skyrim BSA must remain under the K-only lab root."));
        if (!File.Exists(archive.Value))
            diagnostics.Add(Error(
                "skyrim-bsa-archive-missing",
                "The Skyrim BSA does not exist."));
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, archive));
            AddReparseDiagnostic(archive.Value, "BSA archive", diagnostics);
            AddArchiveLengthDiagnostic(
                archive.Value,
                diagnostics);
        }
        if (!archive.Value.EndsWith(
                ".bsa",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "skyrim-bsa-extension",
                "The Skyrim archive must use the .bsa extension."));
    }

    private void ValidateFreshFile(
        WorkspacePath path,
        string extension,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!path.IsUnder(labRoot))
            diagnostics.Add(Error(
                "skyrim-bsa-output-root",
                $"The {role} must remain under the K-only lab root."));
        if (!path.Value.EndsWith(
                extension,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "skyrim-bsa-output-extension",
                $"The {role} must use the {extension} extension."));
        if (File.Exists(path.Value) || Directory.Exists(path.Value))
            diagnostics.Add(Error(
                "skyrim-bsa-output-exists",
                $"The {role} already exists."));
        string? parent = Path.GetDirectoryName(path.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(Error(
                "skyrim-bsa-output-parent",
                $"The {role} parent must already exist."));
        else
        {
            diagnostics.AddRange(policy.Evaluate(
                labRoot,
                new WorkspacePath(parent)));
            AddReparseDiagnostic(parent, $"{role} parent", diagnostics);
        }
    }

    private static ulong HashName(string value)
    {
        string normalized = value.ToLowerInvariant();
        int dot = normalized.LastIndexOf('.');
        string root = dot >= 0 ? normalized[..dot] : normalized;
        string extension = dot >= 0 ? normalized[dot..] : string.Empty;
        uint low = 0;
        if (root.Length > 0)
        {
            low = root[^1];
            if (root.Length > 2)
                low |= (uint)root[^2] << 8;
            low |= checked((uint)root.Length) << 16;
            low |= (uint)root[0] << 24;
        }
        low |= extension switch
        {
            ".kf" => 0x0000_0080U,
            ".nif" => 0x0000_8000U,
            ".dds" => 0x0000_8080U,
            ".wav" => 0x8000_0000U,
            _ => 0U
        };
        uint middle = HashCharacters(
            root.Length > 3 ? root[1..^2] : string.Empty);
        uint suffix = HashCharacters(extension);
        uint high = unchecked(middle + suffix);
        return ((ulong)high << 32) | low;
    }

    private static uint HashCharacters(string value)
    {
        uint hash = 0;
        foreach (char character in value)
            hash = unchecked((hash * 0x0001_003FU) + character);
        return hash;
    }

    private static bool IsAscii(string value) =>
        value.All(character => character <= 0x7F);

    private static bool IsUnder(string path, string root) =>
        new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    private static void AddReparseDiagnostic(
        string path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(
                    FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error(
                    "skyrim-bsa-reparse",
                    $"The {role} traverses a reparse point."));
                return;
            }
            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(
                    parent,
                    current,
                    StringComparison.OrdinalIgnoreCase))
                return;
            current = parent ?? string.Empty;
        }
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
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)));
    }

    private static void AddArchiveLengthDiagnostic(
        string archivePath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var info = new FileInfo(archivePath);
        if (info.Length <= 0 ||
            info.Length > MaximumArchiveBytes)
        {
            diagnostics.Add(Error(
                "skyrim-bsa-archive-size",
                "The Skyrim BSA is empty or exceeds 2 GiB."));
        }
    }

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record BsaInput(
        AssetPath Path,
        string Folder,
        string Name,
        ulong FolderHash,
        ulong FileHash,
        string Source,
        long ByteLength,
        Sha256Hash Sha256);

    private sealed record BsaFolder(
        string Path,
        ulong Hash,
        ImmutableArray<BsaInput> Files);
}
