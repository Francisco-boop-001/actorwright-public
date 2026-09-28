using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.ReferencePreset.Tests;

internal static class NativeRuntimeLoaderTests
{
    private static readonly string[] NativeLoadOrder =
    [
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "msvcp140.dll",
        "concrt140.dll",
        "opencv_world3410.dll",
        "libmediapipe.dll"
    ];

    public static async Task TestAdmittedClosureAndRefusals()
    {
        string projectRoot = FindProjectRoot();
        string authenticRoot = Path.Combine(
            projectRoot,
            "runtime",
            "reference-preset");
        Require(
            File.Exists(Path.Combine(
                authenticRoot,
                "runtime-asset-manifest.json")),
            "native-loader-authentic-manifest-missing");

        string scratchRoot = Path.Combine(
            AppContext.BaseDirectory,
            $".native-loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchRoot);
        string? executableDecoyPath = null;
        try
        {
            TestVerifiedAssetReplacementDenied(scratchRoot);

            ChildResult authentic = await RunChildAsync(
                "success",
                authenticRoot);
            RequireChildPass(authentic, "authentic");

            string pathDecoyRoot = Path.Combine(
                scratchRoot,
                "path-decoy");
            Directory.CreateDirectory(pathDecoyRoot);
            WriteInvalidDecoys(pathDecoyRoot);

            executableDecoyPath = Path.Combine(
                AppContext.BaseDirectory,
                "opencv_world3410.dll");
            Require(
                !File.Exists(executableDecoyPath),
                "native-loader-executable-decoy-preexists");
            File.WriteAllBytes(
                executableDecoyPath,
                "not-an-admitted-opencv"u8.ToArray());
            ChildResult decoys = await RunChildAsync(
                "success",
                authenticRoot,
                pathDecoyRoot);
            RequireChildPass(decoys, "decoys");
            File.Delete(executableDecoyPath);
            executableDecoyPath = null;

            string preloadRoot = Path.Combine(
                scratchRoot,
                "preloaded-wrong-origin");
            Directory.CreateDirectory(preloadRoot);
            string wrongOpenCv = Path.Combine(
                preloadRoot,
                "opencv_world3410.dll");
            File.Copy(
                Path.Combine(
                    authenticRoot,
                    "opencv_world3410.dll"),
                wrongOpenCv);
            ChildResult preloaded = await RunChildAsync(
                "preloaded-wrong-origin",
                authenticRoot,
                wrongOpenCv);
            RequireChildPass(preloaded, "preloaded-wrong-origin");

            string mutableRoot = Path.Combine(
                scratchRoot,
                "mutable-runtime");
            CopyRuntime(authenticRoot, mutableRoot);
            File.Delete(Path.Combine(
                mutableRoot,
                "opencv_world3410.dll"));
            ChildResult missing = await RunChildAsync(
                "expected-error",
                mutableRoot,
                "reference-native-runtime-dependency-load");
            RequireChildPass(missing, "missing");

            CopyRuntime(authenticRoot, mutableRoot, replace: true);
            string tamperedPath = Path.Combine(
                mutableRoot,
                "opencv_world3410.dll");
            using (FileStream stream = new(
                       tamperedPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                stream.Position = stream.Length - 1;
                int original = stream.ReadByte();
                stream.Position = stream.Length - 1;
                stream.WriteByte(unchecked((byte)(original ^ 0x5A)));
            }
            ChildResult tampered = await RunChildAsync(
                "expected-error",
                mutableRoot,
                "reference-native-runtime-dependency-origin");
            RequireChildPass(tampered, "tampered");

            CopyRuntime(authenticRoot, mutableRoot, replace: true);
            ChildResult wrongHash = await RunChildAsync(
                "wrong-hash",
                mutableRoot,
                "reference-native-runtime-dependency-origin");
            RequireChildPass(wrongHash, "wrong-hash");
        }
        finally
        {
            if (executableDecoyPath is not null &&
                File.Exists(executableDecoyPath))
            {
                File.Delete(executableDecoyPath);
            }

            if (Directory.Exists(scratchRoot))
            {
                DeleteTree(scratchRoot);
            }
        }
    }

    public static int RunProbe(string[] args)
    {
        try
        {
            string mode = args[1];
            string runtimeRoot = Path.GetFullPath(args[2]);
            ImmutableArray<NativeRuntimeAssetAuthority> authorities =
                ReadAuthorities(runtimeRoot);

            if (mode == "wrong-hash")
            {
                Require(args.Length == 4,
                    "native-loader-probe-wrong-hash-args");
                authorities = authorities
                    .Select(item =>
                        item.FileName == "opencv_world3410.dll"
                            ? item with
                            {
                                Sha256 = new Sha256Hash(
                                    new string('F', 64))
                            }
                            : item)
                    .ToImmutableArray();
                return ExpectError(authorities, args[3]);
            }

            if (mode == "expected-error")
            {
                Require(args.Length == 4,
                    "native-loader-probe-expected-error-args");
                return ExpectError(authorities, args[3]);
            }

            if (mode == "preloaded-wrong-origin")
            {
                Require(args.Length == 4,
                    "native-loader-probe-preload-args");
                nint wrong = NativeLibrary.Load(
                    Path.GetFullPath(args[3]));
                try
                {
                    return ExpectError(
                        authorities,
                        "reference-native-runtime-dependency-origin");
                }
                finally
                {
                    NativeLibrary.Free(wrong);
                }
            }

            Require(mode == "success" && args.Length is 3 or 4,
                "native-loader-probe-success-args");
            if (args.Length == 4)
            {
                Environment.SetEnvironmentVariable(
                    "PATH",
                    args[3] + Path.PathSeparator +
                    Environment.GetEnvironmentVariable("PATH"));
            }

            using (AdmittedNativeRuntime runtime =
                   WindowsAdmittedNativeRuntimeLoader.Load(authorities))
            {
                Require(runtime.PrimaryHandle != nint.Zero,
                    "native-loader-primary-handle");
                Require(
                    runtime.LoadedModules.Count == authorities.Length,
                    "native-loader-closure-count");
                foreach (NativeRuntimeAssetAuthority authority in
                         authorities)
                {
                    Require(
                        runtime.LoadedModules.TryGetValue(
                            authority.FileName,
                            out string? loadedPath) &&
                        SamePath(loadedPath, authority.FullPath),
                        $"native-loader-closure-origin:{authority.FileName}");
                }
            }

            foreach (NativeRuntimeAssetAuthority authority in
                     authorities.Reverse())
            {
                Require(
                    WindowsAdmittedNativeRuntimeLoader
                        .GetLoadedModulePath(authority.FileName) is null,
                    $"native-loader-not-unloaded:{authority.FileName}");
            }

            Console.WriteLine("RESULT PASS native-loader-probe");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"RESULT FAIL native-loader-probe:{exception}");
            return 1;
        }
    }

    private static int ExpectError(
        ImmutableArray<NativeRuntimeAssetAuthority> authorities,
        string expectedCode)
    {
        try
        {
            using AdmittedNativeRuntime _ =
                WindowsAdmittedNativeRuntimeLoader.Load(authorities);
            throw new InvalidOperationException(
                "native-loader-expected-error-not-raised");
        }
        catch (NativeRuntimeLoadException exception)
        {
            Require(
                exception.DiagnosticCode == expectedCode,
                $"native-loader-error-code:{exception.DiagnosticCode}");
            Console.WriteLine(
                $"RESULT PASS native-loader-probe {exception.DiagnosticCode}");
            return 0;
        }
    }

    private static ImmutableArray<NativeRuntimeAssetAuthority>
        ReadAuthorities(string runtimeRoot)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(
                runtimeRoot,
                "runtime-asset-manifest.json")));
        var hashes = document.RootElement
            .GetProperty("assets")
            .EnumerateArray()
            .ToDictionary(
                row => row.GetProperty("path").GetString()!,
                row => new Sha256Hash(
                    row.GetProperty("sha256").GetString()!),
                StringComparer.Ordinal);
        return NativeLoadOrder
            .Select(fileName => new NativeRuntimeAssetAuthority(
                fileName,
                Path.Combine(runtimeRoot, fileName),
                hashes[fileName],
                fileName == "libmediapipe.dll"))
            .ToImmutableArray();
    }

    private static async Task<ChildResult> RunChildAsync(
        params string[] args)
    {
        string entryAssembly = Assembly.GetEntryAssembly()?.Location ??
            throw new InvalidOperationException(
                "native-loader-entry-assembly-missing");
        string appHost = Path.Combine(
            Path.GetDirectoryName(entryAssembly)!,
            Path.GetFileNameWithoutExtension(entryAssembly) + ".exe");
        string executable = File.Exists(appHost)
            ? appHost
            : Environment.ProcessPath ??
              throw new InvalidOperationException(
                  "native-loader-process-path-missing");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (!File.Exists(appHost))
        {
            start.ArgumentList.Add(entryAssembly);
        }

        start.ArgumentList.Add("--native-loader-probe");
        foreach (string argument in args)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ??
            throw new InvalidOperationException(
                "native-loader-child-start-failed");
        Task<string> stdoutTask =
            process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask =
            process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(45));
        await process.WaitForExitAsync(timeout.Token);
        return new ChildResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static void CopyRuntime(
        string source,
        string destination,
        bool replace = false)
    {
        if (replace && Directory.Exists(destination))
        {
            DeleteTree(destination);
        }

        Directory.CreateDirectory(destination);
        foreach (string sourcePath in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            File.Copy(
                sourcePath,
                Path.Combine(
                    destination,
                    Path.GetFileName(sourcePath)),
                overwrite: false);
        }
    }

    private static void WriteInvalidDecoys(string directory)
    {
        foreach (string fileName in NativeLoadOrder)
        {
            File.WriteAllBytes(
                Path.Combine(directory, fileName),
                "not-an-admitted-native-module"u8.ToArray());
        }
    }

    private static void TestVerifiedAssetReplacementDenied(string directory)
    {
        string path = Path.Combine(directory, "verified-asset.dll");
        string replacement = Path.Combine(directory, "replacement.dll");
        byte[] bytes = "verified native asset"u8.ToArray();
        File.WriteAllBytes(path, bytes);
        File.WriteAllBytes(replacement, "replacement native asset"u8.ToArray());
        var expected = new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(bytes)));

        bool replacementDenied = false;
        using (WindowsAdmittedNativeRuntimeLoader.OpenVerifiedStream(
                   "verified-asset.dll", path, expected))
        {
            try
            {
                File.Move(replacement, path, overwrite: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                replacementDenied = true;
            }

            Require(replacementDenied && File.Exists(path) &&
                    File.Exists(replacement),
                "native-loader-verified-asset-replacement-was-not-denied");
        }

        File.Move(replacement, path, overwrite: true);
        Require(File.ReadAllText(path) == "replacement native asset",
            "native-loader-verified-asset-replacement-did-not-resume-after-close");
    }

    private static void DeleteTree(string root)
    {
        foreach (string file in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }

    private static void RequireChildPass(
        ChildResult result,
        string scenario)
    {
        Require(
            result.ExitCode == 0 &&
            result.Stdout.Contains(
                "RESULT PASS native-loader-probe",
                StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(result.Stderr),
            $"native-loader-child:{scenario}:exit={result.ExitCode}:stdout={result.Stdout}:stderr={result.Stderr}");
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string FindProjectRoot()
    {
        foreach (string start in new[]
                 {
                     Directory.GetCurrentDirectory(),
                     AppContext.BaseDirectory
                 })
        {
            for (DirectoryInfo? current = new(start);
                 current is not null;
                 current = current.Parent)
            {
                if (File.Exists(Path.Combine(
                        current.FullName,
                        "Actorwright.sln")))
                {
                    return current.FullName;
                }
            }
        }

        throw new InvalidOperationException(
            "native-loader-project-root-missing");
    }

    private static void Require(bool condition, string diagnostic)
    {
        if (!condition)
        {
            throw new InvalidOperationException(diagnostic);
        }
    }

    private sealed record ChildResult(
        int ExitCode,
        string Stdout,
        string Stderr);
}
