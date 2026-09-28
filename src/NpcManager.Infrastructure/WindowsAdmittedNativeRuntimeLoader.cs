using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

internal sealed record NativeRuntimeAssetAuthority(
    string FileName,
    string FullPath,
    Sha256Hash Sha256,
    bool IsPrimary);

internal sealed class NativeRuntimeLoadException : Exception
{
    public NativeRuntimeLoadException(
        string diagnosticCode,
        string message)
        : base(message)
    {
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

internal sealed class AdmittedNativeRuntime : IDisposable
{
    private ImmutableArray<WindowsModuleSafeHandle> _modules;

    internal AdmittedNativeRuntime(
        ImmutableArray<WindowsModuleSafeHandle> modules,
        nint primaryHandle,
        ImmutableDictionary<string, string> loadedModules)
    {
        _modules = modules;
        PrimaryHandle = primaryHandle;
        LoadedModules = loadedModules;
    }

    public nint PrimaryHandle { get; }

    public ImmutableDictionary<string, string> LoadedModules { get; }

    public void Dispose()
    {
        ImmutableArray<WindowsModuleSafeHandle> modules = _modules;
        _modules = ImmutableArray<WindowsModuleSafeHandle>.Empty;
        for (int index = modules.Length - 1; index >= 0; index--)
        {
            modules[index].Dispose();
        }
    }
}

internal static class WindowsAdmittedNativeRuntimeLoader
{
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private const string LoadDiagnostic =
        "reference-native-runtime-dependency-load";
    private const string OriginDiagnostic =
        "reference-native-runtime-dependency-origin";

    public static AdmittedNativeRuntime Load(
        ImmutableArray<NativeRuntimeAssetAuthority> authorities)
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw LoadError(
                "The admitted native runtime requires a Windows x64 process.");
        }

        if (authorities.IsDefaultOrEmpty ||
            authorities.Count(item => item.IsPrimary) != 1 ||
            !authorities[^1].IsPrimary)
        {
            throw OriginError(
                "The admitted native dependency closure is incomplete or not ordered with its primary module last.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modules =
            ImmutableArray.CreateBuilder<WindowsModuleSafeHandle>(
                authorities.Length);
        var loadedPaths =
            ImmutableDictionary.CreateBuilder<string, string>(
                StringComparer.Ordinal);
        nint primaryHandle = nint.Zero;
        try
        {
            foreach (NativeRuntimeAssetAuthority authority in
                     authorities)
            {
                string expectedPath = ValidateAuthority(
                    authority,
                    names);
                RejectWrongOriginPreload(
                    authority.FileName,
                    expectedPath);
                using FileStream admittedFile = OpenVerifiedStream(
                    authority.FileName,
                    expectedPath,
                    authority.Sha256);

                nint module = LoadLibraryExW(
                    expectedPath,
                    nint.Zero,
                    LoadLibrarySearchDllLoadDir |
                    LoadLibrarySearchSystem32);
                if (module == nint.Zero)
                {
                    throw LoadError(
                        $"Native dependency '{authority.FileName}' could not be loaded from its admitted path (Win32 {Marshal.GetLastWin32Error()}).");
                }

                var safeModule = new WindowsModuleSafeHandle(module);
                modules.Add(safeModule);
                string loadedPath;
                try
                {
                    loadedPath = GetModulePath(module);
                    if (!SamePath(loadedPath, expectedPath))
                    {
                        throw OriginError(
                            $"Native dependency '{authority.FileName}' resolved to '{loadedPath}' instead of its admitted path.");
                    }

                    VerifyHash(
                        authority.FileName,
                        loadedPath,
                        authority.Sha256);
                }
                catch
                {
                    modules.RemoveAt(modules.Count - 1);
                    safeModule.Dispose();
                    throw;
                }

                loadedPaths.Add(authority.FileName, loadedPath);
                if (authority.IsPrimary)
                {
                    primaryHandle = module;
                }
            }

            ImmutableArray<WindowsModuleSafeHandle> retained =
                modules.MoveToImmutable();
            return new AdmittedNativeRuntime(
                retained,
                primaryHandle,
                loadedPaths.ToImmutable());
        }
        catch
        {
            for (int index = modules.Count - 1; index >= 0; index--)
            {
                modules[index].Dispose();
            }

            throw;
        }
    }

    internal static string? GetLoadedModulePath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        nint module = GetModuleHandleW(fileName);
        return module == nint.Zero
            ? null
            : GetModulePath(module);
    }

    private static string ValidateAuthority(
        NativeRuntimeAssetAuthority authority,
        HashSet<string> names)
    {
        if (string.IsNullOrWhiteSpace(authority.FileName) ||
            string.IsNullOrWhiteSpace(authority.FullPath) ||
            Path.GetFileName(authority.FullPath) !=
                authority.FileName ||
            !Path.IsPathFullyQualified(authority.FullPath) ||
            !names.Add(authority.FileName))
        {
            throw OriginError(
                "A native dependency authority has an invalid, duplicate, or non-canonical name/path.");
        }

        string expectedPath;
        try
        {
            expectedPath = Path.GetFullPath(authority.FullPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            throw OriginError(
                $"Native dependency '{authority.FileName}' has an invalid admitted path: {exception.Message}");
        }

        if (!File.Exists(expectedPath))
        {
            throw LoadError(
                $"Native dependency '{authority.FileName}' is absent from its admitted path '{expectedPath}'.");
        }

        return expectedPath;
    }

    private static void RejectWrongOriginPreload(
        string fileName,
        string expectedPath)
    {
        nint preloaded = GetModuleHandleW(fileName);
        if (preloaded == nint.Zero)
        {
            return;
        }

        string preloadedPath = GetModulePath(preloaded);
        if (!SamePath(preloadedPath, expectedPath))
        {
            throw OriginError(
                $"Native dependency '{fileName}' is already loaded from non-admitted path '{preloadedPath}'.");
        }
    }

    internal static FileStream OpenVerifiedStream(
        string fileName,
        string path,
        Sha256Hash expected)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var measured = new Sha256Hash(
                Convert.ToHexString(SHA256.HashData(stream)));
            if (measured != expected)
            {
                throw OriginError(
                    $"Native dependency '{fileName}' does not match its admitted SHA-256.");
            }

            return stream;
        }
        catch (NativeRuntimeLoadException)
        {
            stream?.Dispose();
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            stream?.Dispose();
            throw LoadError(
                $"Native dependency '{fileName}' could not be hashed at its admitted path: {exception.Message}");
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private static void VerifyHash(
        string fileName,
        string path,
        Sha256Hash expected)
    {
        using FileStream _ = OpenVerifiedStream(fileName, path, expected);
    }

    private static string GetModulePath(nint module)
    {
        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new char[capacity];
            uint length = GetModuleFileNameW(
                module,
                buffer,
                (uint)buffer.Length);
            if (length == 0)
            {
                throw LoadError(
                    $"A loaded native dependency path could not be resolved (Win32 {Marshal.GetLastWin32Error()}).");
            }

            if (length < buffer.Length - 1)
            {
                return Path.GetFullPath(
                    new string(buffer, 0, checked((int)length)));
            }

            capacity *= 2;
        }

        throw LoadError(
            "A loaded native dependency path exceeded the supported Windows path length.");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static NativeRuntimeLoadException LoadError(
        string message) =>
        new(LoadDiagnostic, message);

    private static NativeRuntimeLoadException OriginError(
        string message) =>
        new(OriginDiagnostic, message);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "LoadLibraryExW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryExW(
        string fileName,
        nint file,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetModuleHandleW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string moduleName);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetModuleFileNameW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(
        nint module,
        [Out] char[] fileName,
        uint size);
}

internal sealed class WindowsModuleSafeHandle
    : SafeHandleZeroOrMinusOneIsInvalid
{
    public WindowsModuleSafeHandle(nint module)
        : base(ownsHandle: true)
    {
        SetHandle(module);
    }

    protected override bool ReleaseHandle() =>
        FreeLibrary(handle);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "FreeLibrary",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);
}
