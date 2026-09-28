using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

public sealed partial class BlenderFaceGeomHairRegionsRenderer
{
    private async ValueTask VerifyAuthoritiesAsync(
        FaceGeomHairRegionsRenderRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string phase,
        CancellationToken cancellationToken)
    {
        await VerifyHashAsync(
            blenderPath,
            expectedBlenderSha256,
            $"facegeom-hair-regions-blender-{phase}",
            diagnostics,
            cancellationToken);
        await VerifyHashAsync(
            pyniflyArchivePath,
            expectedPyniflySha256,
            $"facegeom-hair-regions-pynifly-{phase}",
            diagnostics,
            cancellationToken);
        await VerifyPyniflyProfileAsync(
            profileRoot,
            phase,
            diagnostics,
            cancellationToken);
        await VerifyHashAsync(
            texconvPath,
            expectedTexconvSha256,
            $"facegeom-hair-regions-texconv-{phase}",
            diagnostics,
            cancellationToken);
        await VerifyHashAsync(
            request.Source.Candidate.MaterializedPath,
            request.Source.Candidate.Sha256,
            $"facegeom-hair-regions-candidate-{phase}",
            diagnostics,
            cancellationToken);
        foreach (FaceGeomHairTextureAuthority texture in
                 request.Source.Textures)
            await VerifyHashAsync(
                texture.MaterializedPath,
                texture.Sha256,
                $"facegeom-hair-regions-texture-{phase}",
                diagnostics,
                cancellationToken);
    }

    private async ValueTask VerifyPyniflyProfileAsync(
        WorkspacePath admittedProfileRoot,
        string phase,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        string addonRoot = Path.Combine(
            admittedProfileRoot.Value,
            "scripts",
            "addons",
            "io_scene_nifly");
        if (!Directory.Exists(addonRoot))
        {
            diagnostics.Add(Error(
                $"facegeom-hair-regions-pynifly-profile-{phase}",
                "The staged PyNifly addon root is missing from the Blender profile."));
            return;
        }
        var files = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((addonRoot, 0));
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string directory, int depth) = pending.Pop();
                if (depth > 16)
                    throw new InvalidDataException(
                        "The staged PyNifly addon exceeds the admitted directory depth.");
                FileAttributes directoryAttributes =
                    File.GetAttributes(directory);
                if ((directoryAttributes &
                     (FileAttributes.ReparsePoint |
                      FileAttributes.Device)) != 0)
                    throw new UnauthorizedAccessException(
                        "The staged PyNifly addon traverses a reparse or device directory.");
                foreach (string entry in
                         Directory.EnumerateFileSystemEntries(
                             directory))
                {
                    FileAttributes attributes =
                        File.GetAttributes(entry);
                    if ((attributes &
                         (FileAttributes.ReparsePoint |
                          FileAttributes.Device)) != 0)
                        throw new UnauthorizedAccessException(
                            "The staged PyNifly addon contains a reparse or device entry.");
                    if ((attributes &
                         FileAttributes.Directory) != 0)
                    {
                        if (!Path.GetFileName(entry).Equals(
                                "__pycache__",
                                StringComparison.Ordinal))
                            pending.Push((entry, depth + 1));
                        continue;
                    }
                    if (Path.GetExtension(entry).Equals(
                            ".pyc",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    files.Add(entry);
                    if (files.Count > 4096)
                        throw new InvalidDataException(
                            "The staged PyNifly addon exceeds its admitted file count.");
                }
            }

            using IncrementalHash fingerprint =
                IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
            long cumulative = 0;
            foreach (string file in files.OrderBy(
                         path => Path.GetRelativePath(
                                 addonRoot,
                                 path)
                             .Replace('\\', '/'),
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                cumulative = checked(
                    cumulative + info.Length);
                if (info.Length >
                        MaximumArtifactBytes ||
                    cumulative > 1024L * 1024L * 1024L)
                    throw new InvalidDataException(
                        "The staged PyNifly addon exceeds its admitted byte budget.");
                string relative = Path.GetRelativePath(
                        addonRoot,
                        file)
                    .Replace('\\', '/');
                fingerprint.AppendData(
                    Encoding.UTF8.GetBytes(
                        $"{relative}\0{info.Length}\0"));
                byte[] bytes = info.Length == 0
                    ? []
                    : await ReadBoundedAsync(
                        new WorkspacePath(file),
                        MaximumArtifactBytes,
                        cancellationToken);
                fingerprint.AppendData(
                    SHA256.HashData(bytes));
            }
            if (files.Count == 0 ||
                new Sha256Hash(Convert.ToHexString(
                    fingerprint.GetHashAndReset())) !=
                expectedPyniflyProfileSha256)
                diagnostics.Add(Error(
                    $"facegeom-hair-regions-pynifly-profile-{phase}",
                    "The staged PyNifly addon inventory does not match its exact admitted profile hash."));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                OverflowException)
        {
            diagnostics.Add(Error(
                $"facegeom-hair-regions-pynifly-profile-{phase}",
                exception.Message));
        }
    }

    private async ValueTask CopyPrivatePyniflyProfileAsync(
        WorkspacePath destinationProfileRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        string sourceAddon = Path.Combine(
            profileRoot.Value,
            "scripts",
            "addons",
            "io_scene_nifly");
        string destinationAddon = Path.Combine(
            destinationProfileRoot.Value,
            "scripts",
            "addons",
            "io_scene_nifly");
        try
        {
            Directory.CreateDirectory(destinationAddon);
            foreach (string source in EnumerateAdmittedPyniflyFiles(
                         sourceAddon,
                         cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(
                    sourceAddon,
                    source);
                string destination = Path.Combine(
                    destinationAddon,
                    relative);
                string? parent =
                    Path.GetDirectoryName(destination);
                if (parent is null)
                    throw new InvalidDataException(
                        "Private PyNifly destination parent is absent.");
                Directory.CreateDirectory(parent);
                var info = new FileInfo(source);
                byte[] bytes = info.Length == 0
                    ? []
                    : await ReadBoundedAsync(
                        new WorkspacePath(source),
                        MaximumArtifactBytes,
                        cancellationToken);
                await WriteNewAllowEmptyAsync(
                    new WorkspacePath(destination),
                    bytes,
                    cancellationToken);
                byte[] readback = info.Length == 0
                    ? File.ReadAllBytes(destination)
                    : await ReadBoundedAsync(
                        new WorkspacePath(destination),
                        MaximumArtifactBytes,
                        cancellationToken);
                if (!readback.AsSpan().SequenceEqual(bytes))
                    throw new InvalidDataException(
                        $"Private PyNifly copy '{relative}' failed exact readback.");
            }
            Directory.CreateDirectory(Path.Combine(
                destinationProfileRoot.Value,
                "config"));
            Directory.CreateDirectory(Path.Combine(
                destinationProfileRoot.Value,
                "data"));
            await VerifyPyniflyProfileAsync(
                destinationProfileRoot,
                "private-copy",
                diagnostics,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                OverflowException)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-pynifly-private-copy",
                exception.Message));
        }
    }

    private static IEnumerable<string>
        EnumerateAdmittedPyniflyFiles(
            string addonRoot,
            CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((addonRoot, 0));
        int count = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string directory, int depth) = pending.Pop();
            if (depth > 16)
                throw new InvalidDataException(
                    "The staged PyNifly addon exceeds the admitted directory depth.");
            FileAttributes directoryAttributes =
                File.GetAttributes(directory);
            if ((directoryAttributes &
                 (FileAttributes.ReparsePoint |
                  FileAttributes.Device)) != 0)
                throw new UnauthorizedAccessException(
                    "The staged PyNifly addon traverses a reparse or device directory.");
            foreach (string entry in
                     Directory.EnumerateFileSystemEntries(
                         directory)
                         .OrderBy(
                             item => item,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(
                             item => item,
                             StringComparer.Ordinal))
            {
                FileAttributes attributes =
                    File.GetAttributes(entry);
                if ((attributes &
                     (FileAttributes.ReparsePoint |
                      FileAttributes.Device)) != 0)
                    throw new UnauthorizedAccessException(
                        "The staged PyNifly addon contains a reparse or device entry.");
                if ((attributes &
                     FileAttributes.Directory) != 0)
                {
                    if (!Path.GetFileName(entry).Equals(
                            "__pycache__",
                            StringComparison.Ordinal))
                        pending.Push((entry, depth + 1));
                    continue;
                }
                if (Path.GetExtension(entry).Equals(
                        ".pyc",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                count++;
                if (count > 4096)
                    throw new InvalidDataException(
                        "The staged PyNifly addon exceeds its admitted file count.");
                yield return entry;
            }
        }
    }

    private async ValueTask<ProfileSnapshot?>
        SnapshotFullProfileAsync(
            ImmutableArray<Diagnostic>.Builder diagnostics,
            string phase,
            CancellationToken cancellationToken)
    {
        try
        {
            using IncrementalHash fingerprint =
                IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((profileRoot.Value, 0));
            int fileCount = 0;
            int directoryCount = 0;
            long cumulative = 0;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string directory, int depth) = pending.Pop();
                if (depth > 24)
                    throw new InvalidDataException(
                        "The complete Blender profile exceeds its admitted directory depth.");
                FileAttributes rootAttributes =
                    File.GetAttributes(directory);
                if ((rootAttributes &
                     (FileAttributes.ReparsePoint |
                      FileAttributes.Device)) != 0)
                    throw new UnauthorizedAccessException(
                        "The complete Blender profile traverses a reparse or device directory.");
                foreach (string entry in
                         Directory.EnumerateFileSystemEntries(
                                 directory)
                             .OrderBy(
                                 item => item,
                                 StringComparer.OrdinalIgnoreCase)
                             .ThenBy(
                                 item => item,
                                 StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes =
                        File.GetAttributes(entry);
                    if ((attributes &
                         (FileAttributes.ReparsePoint |
                          FileAttributes.Device)) != 0)
                        throw new UnauthorizedAccessException(
                            "The complete Blender profile contains a reparse or device entry.");
                    string relative = Path.GetRelativePath(
                            profileRoot.Value,
                            entry)
                        .Replace('\\', '/');
                    if ((attributes &
                         FileAttributes.Directory) != 0)
                    {
                        directoryCount++;
                        fingerprint.AppendData(
                            Encoding.UTF8.GetBytes(
                                $"D\0{relative}\n"));
                        pending.Push((entry, depth + 1));
                        continue;
                    }
                    fileCount++;
                    var info = new FileInfo(entry);
                    cumulative = checked(
                        cumulative + info.Length);
                    if (fileCount > 8192 ||
                        info.Length >
                            MaximumArtifactBytes ||
                        cumulative >
                            1024L * 1024L * 1024L)
                        throw new InvalidDataException(
                            "The complete Blender profile exceeds its admitted inventory budget.");
                    byte[] bytes = info.Length == 0
                        ? []
                        : await ReadBoundedAsync(
                            new WorkspacePath(entry),
                            MaximumArtifactBytes,
                            cancellationToken);
                    fingerprint.AppendData(
                        Encoding.UTF8.GetBytes(
                            $"F\0{relative}\0{info.Length}\0"));
                    fingerprint.AppendData(
                        SHA256.HashData(bytes));
                    fingerprint.AppendData(
                        "\n"u8);
                }
            }
            if (fileCount == 0)
                throw new InvalidDataException(
                    "The complete Blender profile inventory is empty.");
            return new ProfileSnapshot(
                fileCount,
                directoryCount,
                cumulative,
                new Sha256Hash(Convert.ToHexString(
                    fingerprint.GetHashAndReset())));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                OverflowException)
        {
            diagnostics.Add(Error(
                $"facegeom-hair-regions-pynifly-full-profile-{phase}",
                exception.Message));
            return null;
        }
    }

    private static void ValidateEmptyPythonCache(
        WorkspacePath cacheRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string phase)
    {
        try
        {
            FileAttributes attributes =
                File.GetAttributes(cacheRoot.Value);
            if ((attributes &
                 (FileAttributes.ReparsePoint |
                  FileAttributes.Device)) != 0 ||
                (attributes &
                 FileAttributes.Directory) == 0)
                throw new UnauthorizedAccessException(
                    "The Python bytecode-cache prefix is not an ordinary directory.");
            string? survivor =
                Directory.EnumerateFileSystemEntries(
                        cacheRoot.Value,
                        "*",
                        SearchOption.AllDirectories)
                    .FirstOrDefault();
            if (survivor is not null)
                throw new InvalidDataException(
                    $"Python bytecode cache wrote '{survivor}'.");
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException)
        {
            diagnostics.Add(Error(
                $"facegeom-hair-regions-python-cache-{phase}",
                exception.Message));
        }
    }

}
