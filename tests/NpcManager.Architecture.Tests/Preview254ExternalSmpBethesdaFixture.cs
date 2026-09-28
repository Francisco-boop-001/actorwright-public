using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal enum Preview254ExternalSmpBethesdaFixtureMode
{
    ModelLessRootWithChild = 0,
    CrossPluginWinningOverride = 1,
    LegacyMultipleOrderedRoots = 2
}

internal sealed class Preview254ExternalSmpFixtureFilesystemBinding
{
    private readonly ImmutableDictionary<string, Preview254ExternalSmpFixturePathStat> _expected;

    internal Preview254ExternalSmpFixtureFilesystemBinding(
        ImmutableArray<string> paths,
        ImmutableDictionary<string, Preview254ExternalSmpFixturePathStat> expected)
    {
        Paths = paths;
        _expected = expected;
    }

    internal ImmutableArray<string> Paths { get; }

    internal Preview254ExternalSmpFixturePathStat Stat(string path)
    {
        string full = Path.GetFullPath(path);
        if (!_expected.ContainsKey(full))
            throw new InvalidOperationException(
                $"Unbound fixture path '{full}' cannot be inspected.");
        return Preview254ExternalSmpFixtureFilesystem.Stat(full);
    }

    internal void RequireUnchanged(params string[] paths)
    {
        IEnumerable<string> selected = paths.Length == 0 ? Paths : paths;
        foreach (string path in selected)
        {
            string full = Path.GetFullPath(path);
            if (!_expected.TryGetValue(full, out var expected))
                throw new InvalidOperationException(
                    $"Unbound fixture path '{full}' cannot be verified.");
            var actual = Preview254ExternalSmpFixtureFilesystem.Stat(full);
            if (actual != expected)
                throw new IOException(
                    $"Fixture path changed before operation: '{full}'.");
        }
    }

    internal void RequireUnchangedExcept(params string[] excluded)
    {
        var excludedSet = excluded.Select(Path.GetFullPath).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        RequireUnchanged(Paths.Where(path => !excludedSet.Contains(path)).ToArray());
    }

    internal void RequireOrdinary(string path, string role)
    {
        var stat = Stat(path);
        if (!stat.Exists || !stat.IsDirectory || stat.IsReparsePoint)
            throw new IOException(
                $"Fixture {role} must be an ordinary directory: '{path}'.");
    }

    internal void RequireMissing(string path, string role)
    {
        var stat = Stat(path);
        if (stat.Exists)
            throw new IOException(
                $"Fixture {role} must not already exist: '{path}'.");
    }
}

internal readonly record struct Preview254ExternalSmpFixturePathStat(
    bool Exists,
    bool IsDirectory,
    bool IsReparsePoint,
    uint VolumeSerialNumber,
    ulong FileIndex);

internal static class Preview254ExternalSmpFixtureFilesystem
{
    private const uint FileReadAttributes = 0x0000_0080;
    private const uint DeleteAccess = 0x0001_0000;
    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    internal static Preview254ExternalSmpPinnedDirectory OpenDirectoryLease(
        string path, bool owned)
    {
        string full = Path.GetFullPath(path);
        var handle = CreateFile(
            full,
            FileReadAttributes | (owned ? DeleteAccess : 0u),
            (uint)(FileShare.Read | FileShare.Write),
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Could not pin fixture directory '{full}'.",
                new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(handle, out var info) ||
            (info.FileAttributes & 0x10) == 0 ||
            (info.FileAttributes & 0x400) != 0)
        {
            handle.Dispose();
            throw new IOException($"Fixture directory is not ordinary: '{full}'.");
        }
        return new(full, handle, ToIdentity(info));
    }

    internal static void CreateDirectoryExclusive(string path)
    {
        if (!CreateDirectory(Path.GetFullPath(path), IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            throw new IOException(
                $"Could not exclusively create fixture directory '{path}'.",
                new Win32Exception(error));
        }
    }

    internal static Preview254ExternalSmpPinnedOutputFile CreateOutputFile(
        string path)
    {
        string full = Path.GetFullPath(path);
        var handle = CreateFile(
            full,
            GenericRead | GenericWrite,
            (uint)FileShare.Read,
            IntPtr.Zero,
            CreateNew,
            FileFlagOpenReparsePoint | 0x4000_0000,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Could not exclusively create fixture output '{full}'.",
                new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(handle, out var info) ||
            (info.FileAttributes & 0x10) != 0 ||
            (info.FileAttributes & 0x400) != 0)
        {
            handle.Dispose();
            throw new IOException($"Fixture output is not an ordinary file: '{full}'.");
        }
        return new(full, handle,
            new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, true));
    }

    internal static Preview254ExternalSmpPinnedFile OpenFileForDelete(string path)
    {
        string full = Path.GetFullPath(path);
        var handle = CreateFile(
            full,
            GenericRead | DeleteAccess,
            (uint)(FileShare.Read | FileShare.Delete),
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Could not pin fixture file '{full}' for deletion: " +
                new Win32Exception(error).Message,
                new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(handle, out var info) ||
            (info.FileAttributes & 0x10) != 0 ||
            (info.FileAttributes & 0x400) != 0)
        {
            handle.Dispose();
            throw new IOException($"Fixture file is not an ordinary file: '{full}'.");
        }
        return new(full, handle, ToIdentity(info));
    }

    internal static void DeleteByHandle(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle, FileInfoByHandleClass.FileDispositionInfo, ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new IOException(
                "Could not delete the pinned fixture identity.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
    }

    private static Preview254ExternalSmpFixturePathStat ToIdentity(
        ByHandleFileInformation info) => new(
            true,
            (info.FileAttributes & 0x10) != 0,
            (info.FileAttributes & 0x400) != 0,
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);

    internal static Preview254ExternalSmpFixtureFilesystemBinding Bind(
        params string[] paths)
    {
        var normalized = paths.Select(Path.GetFullPath).Distinct(
            StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        return new Preview254ExternalSmpFixtureFilesystemBinding(
            normalized,
            normalized.ToImmutableDictionary(
                path => path,
                Stat,
                StringComparer.OrdinalIgnoreCase));
    }

    internal static void EnsureOrdinaryDirectory(
        string path, string expectedParent, string role)
    {
        string full = Path.GetFullPath(path);
        string parent = Path.GetFullPath(expectedParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full)?.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                parent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refused to create {role} outside its exact parent.");
        }
        var preflight = Bind(parent, full);
        preflight.RequireOrdinary(parent, $"parent of {role}");
        var target = Stat(full);
        using var parentLease = OpenDirectoryLease(parent, owned: false);
        if (!target.Exists)
        {
            preflight.RequireUnchanged();
            CreateDirectoryExclusive(full);
            using var createdLease = OpenDirectoryLease(full, owned: false);
            var after = Stat(full);
            createdLease.RequireIdentity(after);
            if (!after.Exists || !after.IsDirectory || after.IsReparsePoint)
                throw new IOException($"Fixture {role} must be an ordinary directory: '{full}'.");
        }
        else
        {
            preflight.RequireOrdinary(full, role);
            preflight.RequireUnchanged();
        }
    }

    internal static Preview254ExternalSmpFixturePathStat Stat(string path)
    {
        string full = Path.GetFullPath(path);
        using SafeFileHandle handle = CreateFile(
            full,
            0,
            0x00000001 | 0x00000002 | 0x00000004,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                string? parent = Path.GetDirectoryName(full);
                var parentStat = string.IsNullOrEmpty(parent)
                    ? default
                    : Stat(parent);
                if (string.IsNullOrEmpty(parent) ||
                    (parentStat.Exists &&
                        (!parentStat.IsDirectory || parentStat.IsReparsePoint)))
                {
                    throw new IOException(
                        $"Could not stat fixture path '{full}' because an ancestor is unavailable.",
                        new Win32Exception(error));
                }
                return new(false, false, false, 0, 0);
            }
            throw new IOException(
                $"Could not stat fixture path '{full}'.",
                new Win32Exception(error));
        }
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException(
                $"Could not read fixture path identity '{full}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        return ToIdentity(info);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass informationClass,
        ref FileDispositionInformation information,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        internal bool DeleteFile;
    }

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4
    }
}

internal sealed class Preview254ExternalSmpPinnedDirectory(
    string path, SafeFileHandle handle,
    Preview254ExternalSmpFixturePathStat identity) : IDisposable
{
    internal string Path { get; } = path;
    internal SafeFileHandle Handle { get; } = handle;
    internal Preview254ExternalSmpFixturePathStat Identity { get; } = identity;

    internal void RequireIdentity(Preview254ExternalSmpFixturePathStat expected)
    {
        if (Identity != expected)
            throw new IOException($"Pinned directory identity changed: '{Path}'.");
    }

    public void Dispose() => Handle.Dispose();
}

internal sealed class Preview254ExternalSmpPinnedOutputFile(
    string path, SafeFileHandle handle, FileStream stream) : IDisposable
{
    private FileStream? stream = stream;
    private SafeFileHandle? handle = handle;

    internal string Path { get; } = path;

    internal void Write(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(stream is null, this);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    internal Preview254ExternalSmpFixtureOutputEvidence ReadEvidence()
    {
        ObjectDisposedException.ThrowIf(stream is null, this);
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
        byte[] digest = SHA256.HashData(stream);
        return new(new Sha256Hash(Convert.ToHexString(digest)), stream.Length);
    }

    public void Dispose()
    {
        if (stream is { } current)
        {
            current.Dispose();
        }
        handle?.Dispose();
        stream = null;
        handle = null;
    }
}

internal sealed class Preview254ExternalSmpPinnedFile(
    string path, SafeFileHandle handle,
    Preview254ExternalSmpFixturePathStat identity) : IDisposable
{
    internal string Path { get; } = path;
    internal SafeFileHandle Handle { get; } = handle;
    internal Preview254ExternalSmpFixturePathStat Identity { get; } = identity;

    internal void RequireIdentity(Preview254ExternalSmpFixturePathStat expected)
    {
        if (Identity != expected)
            throw new IOException($"Pinned file identity changed: '{Path}'.");
    }

    public void Dispose() => Handle.Dispose();
}

internal readonly record struct Preview254ExternalSmpFixtureOutputEvidence(
    Sha256Hash Sha256,
    long ByteLength);

internal sealed record Preview254ExternalSmpBethesdaFixture(
    WorkspacePath ScratchRoot,
    WorkspacePath DataRoot,
    WorkspacePath ProviderPluginPath,
    PluginName ProviderPlugin,
    Sha256Hash ProviderPluginSha256,
    long ProviderPluginByteLength,
    FormReference HairRoot,
    FormReference HairChild,
    ImmutableArray<PluginName> EnabledPluginOrder);

internal interface IPreview254ExternalSmpBethesdaFixtureFactory
{
    ValueTask<Preview254ExternalSmpBethesdaFixture> CreateAsync(
        WorkspacePath scratchRoot,
        Preview254ExternalSmpBethesdaFixtureMode mode,
        CancellationToken cancellationToken);
}

internal sealed class Preview254ExternalSmpBethesdaFixtureFactory :
    IPreview254ExternalSmpBethesdaFixtureFactory
{
    private const string ProviderPluginName = "OrchidAdornment.esp";
    private const string WinningOverridePluginName = "OrchidAdornmentPatch.esp";

    public ValueTask<Preview254ExternalSmpBethesdaFixture> CreateAsync(
        WorkspacePath scratchRoot,
        Preview254ExternalSmpBethesdaFixtureMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        var binding = ValidateScratchRoot(scratchRoot, mode);
        var leases = new List<Preview254ExternalSmpPinnedDirectory>();
        var outputs = new List<Preview254ExternalSmpPinnedOutputFile>();
        try
        {
            foreach (string ancestor in binding.Paths.Take(4))
                leases.Add(Preview254ExternalSmpFixtureFilesystem.OpenDirectoryLease(
                    ancestor, owned: false));
            string dataPath = Path.Combine(scratchRoot.Value, "Data");
            binding.RequireUnchanged();
            if (!binding.Stat(dataPath).Exists)
            {
                Preview254ExternalSmpFixtureFilesystem.CreateDirectoryExclusive(dataPath);
            }
            var dataLease = Preview254ExternalSmpFixtureFilesystem.OpenDirectoryLease(
                dataPath, owned: false);
            dataLease.RequireIdentity(Preview254ExternalSmpFixtureFilesystem.Stat(dataPath));
            leases.Add(dataLease);
            binding.RequireOrdinary(dataPath, "fixture Data directory");

            string providerPath = Path.Combine(dataPath, ProviderPluginName);
            string winningPath = WinningPath(dataPath);
            binding.RequireUnchangedExcept(dataPath, providerPath, winningPath);
            binding.RequireMissing(providerPath, "provider plugin destination");
            var providerOutput = WriteProvider(providerPath, mode);
            outputs.Add(providerOutput);
            var providerEvidence = providerOutput.ReadEvidence();
            if (mode == Preview254ExternalSmpBethesdaFixtureMode.CrossPluginWinningOverride)
            {
                binding.RequireUnchangedExcept(dataPath, providerPath, winningPath);
                binding.RequireMissing(winningPath, "override plugin destination");
                var winningOutput = WriteWinningOverride(winningPath);
                outputs.Add(winningOutput);
                winningOutput.ReadEvidence();
            }

            var provider = new PluginName(ProviderPluginName);
            var root = new FormReference(provider, new FormId(0x800));
            var child = new FormReference(provider, new FormId(0x801));
            return ValueTask.FromResult(new Preview254ExternalSmpBethesdaFixture(
                scratchRoot,
                new WorkspacePath(dataPath),
                new WorkspacePath(providerPath),
                provider,
                providerEvidence.Sha256,
                providerEvidence.ByteLength,
                root,
                child,
                [new PluginName("Skyrim.esm"), provider]));
        }
        finally
        {
            for (int index = outputs.Count - 1; index >= 0; index--)
                outputs[index].Dispose();
            for (int index = leases.Count - 1; index >= 0; index--)
                leases[index].Dispose();
        }
    }

    private static Preview254ExternalSmpFixtureFilesystemBinding ValidateScratchRoot(
        WorkspacePath scratchRoot, Preview254ExternalSmpBethesdaFixtureMode mode)
    {
        if (string.IsNullOrWhiteSpace(scratchRoot.Value) ||
            HasAlternateDataStream(scratchRoot.Value))
        {
            throw new ArgumentException(
                "Fixture scratch root must be an ordinary path.", nameof(scratchRoot));
        }

        string full = Path.GetFullPath(scratchRoot.Value);
        DirectoryInfo scratch = new(full);
        DirectoryInfo? testWork = scratch.Parent;
        DirectoryInfo? artifacts = testWork?.Parent;
        DirectoryInfo? repoRoot = artifacts?.Parent;
        if (testWork is null || artifacts is null || repoRoot is null ||
            !string.Equals(testWork.Name, "test-work", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifacts.Name, "artifacts", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.Combine(repoRoot.FullName, "src")) ||
            (!File.Exists(Path.Combine(repoRoot.FullName, ".git")) &&
             !Directory.Exists(Path.Combine(repoRoot.FullName, ".git"))))
        {
            throw new ArgumentException(
                "Fixture scratch root must be directly below the lane artifacts/test-work directory.",
                nameof(scratchRoot));
        }
        string dataPath = Path.Combine(full, "Data");
        string providerPath = Path.Combine(dataPath, ProviderPluginName);
        string winningPath = WinningPath(dataPath);
        var binding = Preview254ExternalSmpFixtureFilesystem.Bind(
            repoRoot.FullName,
            artifacts.FullName,
            testWork.FullName,
            full,
            dataPath,
            providerPath,
            winningPath);
        binding.RequireOrdinary(repoRoot.FullName, "repository root");
        binding.RequireOrdinary(artifacts.FullName, "artifacts directory");
        binding.RequireOrdinary(testWork.FullName, "test-work directory");
        binding.RequireOrdinary(full, "scratch root");
        if (binding.Stat(dataPath).Exists)
            binding.RequireOrdinary(dataPath, "fixture Data directory");
        binding.RequireMissing(providerPath, "provider plugin destination");
        if (mode == Preview254ExternalSmpBethesdaFixtureMode.CrossPluginWinningOverride)
            binding.RequireMissing(winningPath, "override plugin destination");
        return binding;
    }

    private static string WinningPath(string dataPath) =>
        Path.Combine(dataPath, WinningOverridePluginName);

    private static Preview254ExternalSmpPinnedOutputFile WriteProvider(
        string path, Preview254ExternalSmpBethesdaFixtureMode mode)
    {
        var key = ModKey.FromNameAndExtension(ProviderPluginName);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension("Skyrim.esm")
        });
        var rootKey = new FormKey(key, 0x800);
        var childKey = new FormKey(key, 0x801);
        var secondRootKey = new FormKey(key, 0x802);
        var secondChildKey = new FormKey(key, 0x803);
        var raceKey = new FormKey(key, 0x900);

        var root = new HeadPart(rootKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "OrchidRootHair",
            Name = "Orchid root hair",
            Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Hair
        };
        root.ExtraParts.Add(new FormLink<IHeadPartGetter>(childKey));
        mod.HeadParts.Add(root);

        var child = new HeadPart(childKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "OrchidChildHair",
            Name = "Orchid child hair",
            Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Hair,
            Model = new Model
            {
                File = new AssetLink<SkyrimModelAssetType>(
                    "meshes/actors/character/character assets/hair/orchid-child.nif")
            }
        };
        child.Parts.Add(new Part
        {
            PartType = Part.PartTypeEnum.Tri,
            FileName = new AssetLink<SkyrimDeformedModelAssetType>(
                "meshes/actors/character/character assets/hair/orchid-child.tri")
        });
        mod.HeadParts.Add(child);
        if (mode == Preview254ExternalSmpBethesdaFixtureMode.LegacyMultipleOrderedRoots)
        {
            var secondRoot = new HeadPart(secondRootKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "OrchidSecondRootHair",
                Name = "Orchid second root hair",
                Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
                Type = HeadPart.TypeEnum.FacialHair
            };
            secondRoot.ExtraParts.Add(new FormLink<IHeadPartGetter>(secondChildKey));
            mod.HeadParts.Add(secondRoot);

            var secondChild = new HeadPart(secondChildKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "OrchidSecondChildHair",
                Name = "Orchid second child hair",
                Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
                Type = HeadPart.TypeEnum.Hair,
                Model = new Model
                {
                    File = new AssetLink<SkyrimModelAssetType>(
                        "meshes/actors/character/character assets/hair/orchid-second-child.nif")
                }
            };
            secondChild.Parts.Add(new Part
            {
                PartType = Part.PartTypeEnum.Tri,
                FileName = new AssetLink<SkyrimDeformedModelAssetType>(
                    "meshes/actors/character/character assets/hair/orchid-second-child.tri")
            });
            mod.HeadParts.Add(secondChild);
        }
        mod.Races.Add(new Race(raceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "OrchidRace",
            HeadData = new GenderedItem<HeadData?>(new HeadData(), new HeadData())
        });
        return Write(mod, path);
    }

    private static Preview254ExternalSmpPinnedOutputFile WriteWinningOverride(string path)
    {
        var patchKey = ModKey.FromNameAndExtension(WinningOverridePluginName);
        var providerKey = ModKey.FromNameAndExtension(ProviderPluginName);
        var mod = new SkyrimMod(patchKey, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension("Skyrim.esm")
        });
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = providerKey
        });
        var root = new HeadPart(new FormKey(providerKey, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "OrchidRootHairOverride",
            Name = "Orchid root hair override",
            Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Hair
        };
        root.ExtraParts.Add(new FormLink<IHeadPartGetter>(
            new FormKey(providerKey, 0x801)));
        mod.HeadParts.Add(root);
        return Write(mod, path);
    }

    private static Preview254ExternalSmpPinnedOutputFile Write(
        SkyrimMod mod, string path)
    {
        using var encoded = new MemoryStream();
        mod.WriteToBinary(encoded, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
        var output = Preview254ExternalSmpFixtureFilesystem.CreateOutputFile(path);
        output.Write(encoded.ToArray());
        return output;
    }

    private static bool HasAlternateDataStream(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }
}
