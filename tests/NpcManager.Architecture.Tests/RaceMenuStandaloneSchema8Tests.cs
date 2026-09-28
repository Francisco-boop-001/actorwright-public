using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal sealed class Preview254ExternalSmpStandaloneSchema8Scenario :
    IPreview254ExternalSmpArchitectureScenario
{
    public string Selector => "--test-racemenu-standalone-schema8";

    public ValueTask RunAsync(CancellationToken cancellationToken) =>
        RaceMenuStandaloneSchema8Tests.RunAsync(cancellationToken);
}

internal static partial class RaceMenuStandaloneSchema8Tests
{
    private static readonly JsonSerializerOptions IndentedJson =
        new() { WriteIndented = true };
    private static readonly string[] BodySlideGroups = ["fixture"];
    private static readonly char[] ChildPidSeparators = ['\r', '\n', ' '];
    private static readonly (string Role, string Destination)[] BodyMeshRoles =
    [
        ("body0", "Meshes/Actors/Character/schema8/body_0.nif"),
        ("body1", "Meshes/Actors/Character/schema8/body_1.nif"),
        ("hands0", "Meshes/Actors/Character/schema8/hands_0.nif"),
        ("hands1", "Meshes/Actors/Character/schema8/hands_1.nif"),
        ("feet0", "Meshes/Actors/Character/schema8/feet_0.nif"),
        ("feet1", "Meshes/Actors/Character/schema8/feet_1.nif")
    ];
    private static readonly WorkspacePath LabRoot =
        new(FindRepositoryRoot());
    private static readonly WorkspacePath ProtectedRoot =
        new(@"F:\ExampleGame");
    private static readonly PluginName ProviderPlugin =
        new("OrchidAdornment.esp");
    private static readonly FormReference RootForm =
        new(ProviderPlugin, new FormId(0x800));

    public static async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.Combine(
            LabRoot.Value,
            "artifacts",
            "racemenu-standalone-schema8-red-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var descriptor = CreateDescriptor();
            var attestation = CreateAttestation(descriptor);
            string templatePath = Path.Combine(root, "template.esp");
            string templateRelative = new AssetPath(
                Path.GetRelativePath(LabRoot.Value, templatePath)
                    .Replace(Path.DirectorySeparatorChar, '/')).Value;
            await File.WriteAllBytesAsync(
                templatePath,
                Encoding.UTF8.GetBytes("template-plugin"),
                cancellationToken);
            byte[] bytes = BuildManifest(
                descriptor, attestation, templateRelative);
            string manifestPath = Path.Combine(root, "standalone-assets.json");
            await File.WriteAllBytesAsync(manifestPath, bytes, cancellationToken);

            await AssertCanonicalReaderAsync(
                new WorkspacePath(manifestPath), bytes, descriptor, attestation,
                cancellationToken);
            await AssertReaderNegativesAsync(
                root, bytes, descriptor, attestation, templateRelative,
                cancellationToken);
            await AssertBodySlideStatesAsync(
                root, bytes, descriptor, attestation, cancellationToken);
            await AssertWriterAsync(
                root, descriptor, attestation, templateRelative,
                cancellationToken);
            await AssertAuthoritativeLegacyProcessAsync(cancellationToken);
            await AssertLegacyCompatibilityAsync(cancellationToken);
            AssertPreparedRequestBoundary();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static ValueTask AssertLegacyCompatibilityAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return AssertLegacyCompatibilityCoreAsync(cancellationToken);
    }

    private static async ValueTask AssertAuthoritativeLegacyProcessAsync(
        CancellationToken cancellationToken)
    {
        ChildSelectorResult result = await RunGate2SelectorAsync(
            "--test-racemenu-standalone-schema3-7-goldens",
            TimeSpan.FromSeconds(60), cancellationToken);
        Require(!result.TimedOut && result.ExitCode == 0 && result.Output.Contains(
                "PASS --test-racemenu-standalone-schema3-7-goldens",
                StringComparison.Ordinal),
            "Authoritative Gate2 schema3-7 selector failed: " + result.Output.Trim());

        await AssertChildTerminationAsync(cancellationToken);
    }

    private static async ValueTask AssertChildTerminationAsync(
        CancellationToken cancellationToken)
    {
        ChildSelectorResult result = await RunGate2SelectorAsync(
            "--test-racemenu-standalone-schema8-child-probe",
            TimeSpan.FromSeconds(1), cancellationToken);
        Require(result.TimedOut && result.ExitCode is null &&
                result.Output.Contains("PROBE_READY PID=", StringComparison.Ordinal),
            "The bounded child probe did not time out with its output preserved: " +
            result.Output.Trim());
        string? pidText = result.Output.Split(
                "PROBE_READY PID=", StringSplitOptions.None)
            .ElementAtOrDefault(1)?.Split(
                ChildPidSeparators, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        Require(int.TryParse(pidText, out int pid),
            "The bounded child probe did not report a process id.");
        try
        {
            using Process child = Process.GetProcessById(pid);
            Require(false, $"The timed-out Gate2 child remained alive: {pid}");
        }
        catch (ArgumentException)
        {
            // The entire child process tree was terminated as required.
        }
    }

    private sealed record ChildSelectorResult(
        int? ExitCode, string Output, bool TimedOut);

    private static async ValueTask<ChildSelectorResult> RunGate2SelectorAsync(
        string selector,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string repositoryRoot = FindRepositoryRoot();
        string gateDll = Path.Combine(
            repositoryRoot,
            "tests",
            "NpcManager.Gate2.PipelineIntegration.Tests",
            "bin",
            "Release",
            "net10.0",
            "NpcManager.Gate2.PipelineIntegration.Tests.dll");
        Require(File.Exists(gateDll) && !Directory.Exists(gateDll),
            $"Authoritative Gate2 DLL was not built: {gateDll}");
        FileAttributes gateAttributes = File.GetAttributes(gateDll);
        Require((gateAttributes & (FileAttributes.ReparsePoint |
            FileAttributes.Device | FileAttributes.Directory)) == 0,
            $"Authoritative Gate2 DLL was not an ordinary file: {gateDll}");
        string dotnetHost = ResolveDotnetHost();
        string dotnetRoot = Path.GetDirectoryName(dotnetHost) ??
            throw new InvalidOperationException(
                "The resolved dotnet host had no parent directory.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = dotnetHost,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
        process.StartInfo.Environment["DOTNET_ROOT_X64"] = dotnetRoot;
        process.StartInfo.ArgumentList.Add(gateDll);
        process.StartInfo.ArgumentList.Add(selector);
        Require(process.Start(), "Could not start the authoritative Gate2 dotnet host.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(
            CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(
            CancellationToken.None);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
        {
            string output = await KillAndDrainAsync(process, stdout, stderr);
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(
                    "The authoritative Gate2 child was canceled. Output: " +
                    output, cancellationToken);
            return new ChildSelectorResult(null, output, TimedOut: true);
        }
        catch (Exception exception)
        {
            string output = await KillAndDrainAsync(process, stdout, stderr);
            throw new InvalidOperationException(
                "The authoritative Gate2 child wait failed. Output: " + output,
                exception);
        }

        string completedOutput = await DrainOutputAsync(stdout, stderr);
        return new ChildSelectorResult(process.ExitCode, completedOutput, TimedOut: false);
    }

    private static string ResolveDotnetHost()
    {
        string? processPath = Environment.ProcessPath;
        string processName = processPath is null
            ? string.Empty : Path.GetFileName(processPath);
        if (!string.IsNullOrWhiteSpace(processPath) &&
            (string.Equals(processName, "dotnet.exe",
                 StringComparison.OrdinalIgnoreCase) ||
             string.Equals(processName, "dotnet",
                 StringComparison.OrdinalIgnoreCase)))
            return ValidateDotnetHost(processPath, "the current dotnet host");

        string? explicitHost = Environment.GetEnvironmentVariable(
            "DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(explicitHost))
            return ValidateDotnetHost(explicitHost, "DOTNET_HOST_PATH");

        foreach (string variable in new[] { "DOTNET_ROOT_X64", "DOTNET_ROOT" })
        {
            string? root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                string candidate = Path.Combine(root, "dotnet.exe");
                if (File.Exists(candidate))
                    return ValidateDotnetHost(candidate, variable);
            }
        }

        string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        DirectoryInfo? current = new DirectoryInfo(runtimeDirectory);
        for (int depth = 0; current is not null && depth <= 6;
             depth++, current = current.Parent)
        {
            string candidate = Path.Combine(current.FullName, "dotnet.exe");
            if (File.Exists(candidate))
                return ValidateDotnetHost(candidate,
                    "the bounded runtime-directory traversal");
        }

        throw new InvalidOperationException(
            "Could not resolve an ordinary dotnet host without PATH search.");
    }

    private static string ValidateDotnetHost(string candidate, string source)
    {
        string resolved = Path.GetFullPath(candidate);
        Require(string.Equals(Path.GetFileName(resolved), "dotnet.exe",
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(resolved) && !Directory.Exists(resolved),
            $"Resolved {source} dotnet host was not an ordinary file: {resolved}");
        FileAttributes attributes = File.GetAttributes(resolved);
        Require((attributes & (FileAttributes.ReparsePoint |
            FileAttributes.Device | FileAttributes.Directory)) == 0,
            $"Resolved {source} dotnet host was not ordinary: {resolved}");
        return resolved;
    }

    private static async ValueTask<string> KillAndDrainAsync(
        Process process,
        Task<string> stdout,
        Task<string> stderr)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            return (await DrainOutputAsync(stdout, stderr)) +
                Environment.NewLine + "Kill failure: " + exception.Message;
        }

        using var waitSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(waitSource.Token);
        }
        catch (OperationCanceledException)
        {
            // Preserve the bounded output diagnostic below even if a descendant
            // ignored the tree termination request.
        }
        return await DrainOutputAsync(stdout, stderr);
    }

    private static async ValueTask<string> DrainOutputAsync(
        Task<string> stdout,
        Task<string> stderr)
    {
        Task<string[]> allOutput = Task.WhenAll(stdout, stderr);
        try
        {
            string[] output = await allOutput.WaitAsync(TimeSpan.FromSeconds(5));
            return output[0] + Environment.NewLine + output[1];
        }
        catch (Exception exception)
        {
            string standardOutput = stdout.IsCompletedSuccessfully
                ? stdout.Result : "<stdout drain incomplete>";
            string standardError = stderr.IsCompletedSuccessfully
                ? stderr.Result : "<stderr drain incomplete>";
            return standardOutput + Environment.NewLine + standardError +
                Environment.NewLine + "Output drain failure: " + exception.Message;
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Actorwright.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the Actorwright repository root.");
    }

    private const string LegacyGoldenRootPrefix =
        "racemenu-standalone-schema8-compat-golden";
    private const string LegacyGoldenPathPrefix =
        "artifacts/racemenu-standalone-schema8-compat-golden";
    // These are fixed review goldens. They are intentionally not produced by
    // RaceMenuPresetStandaloneAuthorityWriter, so a writer regression cannot
    // silently redefine the legacy wire surface.
    private const string LegacySchema3Golden =
        "{\"schemaVersion\":3,\"assetSetId\":\"legacy-schema3-final-oracle\",\"edition\":\"skyrimse\",\"nam9Authority\":{\"pluginPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/template.esp\",\"pluginSha256\":\"69dbc895aeecf39b0a0ce15bfe54f0c8021924d9358369e4a7bdf4a95938037b\",\"npcFormId\":\"0x00000800\",\"trailingValue\":0},\"faceTint\":{\"width\":1,\"height\":1},\"privateHeadTextures\":{\"diffuse\":\"Textures/golden.dds\",\"normalOrGloss\":\"Textures/golden.dds\",\"glowOrDetailMap\":\"Textures/golden.dds\",\"height\":\"Textures/golden.dds\",\"backlightMaskOrSpecular\":\"Textures/golden.dds\",\"environmentMaskOrSubsurfaceTint\":null,\"environment\":null,\"multilayer\":null},\"packageAssets\":[{\"sourcePath\":\"artifacts/racemenu-standalone-schema8-compat-golden/package.dds\",\"sha256\":\"97a0f8074d69fa7a3ffebad9458fa0f9995d465d58fbf53547b1c3ffd231d753\",\"destination\":\"Textures/legacy-package.dds\"}],\"overlayDecisions\":null,\"externalTextureAuthorities\":[],\"finalOutputAuthority\":{\"manifestPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/final.json\",\"manifestSha256\":\"2be97d6c2a024d4ca7afdc2172f799202e48703aa596bc8bc36438e16c634d97\"}}";
    private const string LegacySchema4Golden =
        "{\"schemaVersion\":4,\"assetSetId\":\"legacy-schema4-face-bake\",\"edition\":\"skyrimse\",\"nam9Authority\":{\"pluginPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/template.esp\",\"pluginSha256\":\"69dbc895aeecf39b0a0ce15bfe54f0c8021924d9358369e4a7bdf4a95938037b\",\"npcFormId\":\"0x00000800\",\"trailingValue\":0},\"faceTint\":{\"width\":1,\"height\":1},\"privateHeadTextures\":{\"diffuse\":\"Textures/golden.dds\",\"normalOrGloss\":\"Textures/golden.dds\",\"glowOrDetailMap\":\"Textures/golden.dds\",\"height\":\"Textures/golden.dds\",\"backlightMaskOrSpecular\":\"Textures/golden.dds\",\"environmentMaskOrSubsurfaceTint\":null,\"environment\":null,\"multilayer\":null},\"packageAssets\":[{\"sourcePath\":\"artifacts/racemenu-standalone-schema8-compat-golden/package.dds\",\"sha256\":\"97a0f8074d69fa7a3ffebad9458fa0f9995d465d58fbf53547b1c3ffd231d753\",\"destination\":\"Textures/legacy-package.dds\"}],\"overlayDecisions\":null,\"externalTextureAuthorities\":[],\"faceBakeAuthority\":{\"manifestPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/face-bake.json\",\"manifestSha256\":\"8555699288f06448ba3d1a078a2cfb97b884e04eba745e3a3c2a524e358996c6\"},\"finalOutputAuthority\":null}";
    private const string LegacySchema5Golden =
        "{\"schemaVersion\":5,\"assetSetId\":\"legacy-schema5-face-texture-bake\",\"edition\":\"skyrimse\",\"nam9Authority\":{\"pluginPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/template.esp\",\"pluginSha256\":\"69dbc895aeecf39b0a0ce15bfe54f0c8021924d9358369e4a7bdf4a95938037b\",\"npcFormId\":\"0x00000800\",\"trailingValue\":0},\"faceTint\":{\"width\":1,\"height\":1},\"privateHeadTextures\":{\"diffuse\":\"Textures/golden.dds\",\"normalOrGloss\":\"Textures/golden.dds\",\"glowOrDetailMap\":\"Textures/golden.dds\",\"height\":\"Textures/golden.dds\",\"backlightMaskOrSpecular\":\"Textures/golden.dds\",\"environmentMaskOrSubsurfaceTint\":null,\"environment\":null,\"multilayer\":null},\"packageAssets\":[{\"sourcePath\":\"artifacts/racemenu-standalone-schema8-compat-golden/package.dds\",\"sha256\":\"97a0f8074d69fa7a3ffebad9458fa0f9995d465d58fbf53547b1c3ffd231d753\",\"destination\":\"Textures/legacy-package.dds\"}],\"overlayDecisions\":null,\"externalTextureAuthorities\":[],\"faceBakeAuthority\":{\"manifestPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/face-bake.json\",\"manifestSha256\":\"8555699288f06448ba3d1a078a2cfb97b884e04eba745e3a3c2a524e358996c6\"},\"faceTextureBakeAuthority\":{\"manifestPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/face-texture-bake.json\",\"manifestSha256\":\"1ba5c3efbc52ca60a3fc15a89f7b26eb21bee68b2bf5cba4a74e109cc210b9b1\"},\"finalOutputAuthority\":null}";
    private const string LegacySchema6Golden =
        "{\"schemaVersion\":6,\"assetSetId\":\"legacy-schema6-ube-direct-carrier\",\"edition\":\"skyrimse\",\"nam9Authority\":{\"pluginPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/template.esp\",\"pluginSha256\":\"69dbc895aeecf39b0a0ce15bfe54f0c8021924d9358369e4a7bdf4a95938037b\",\"npcFormId\":\"0x00000800\",\"trailingValue\":0},\"faceTint\":{\"width\":1,\"height\":1},\"privateHeadTextures\":{\"diffuse\":\"Textures/golden.dds\",\"normalOrGloss\":\"Textures/golden.dds\",\"glowOrDetailMap\":\"Textures/golden.dds\",\"height\":\"Textures/golden.dds\",\"backlightMaskOrSpecular\":\"Textures/golden.dds\",\"environmentMaskOrSubsurfaceTint\":null,\"environment\":null,\"multilayer\":null},\"packageAssets\":[],\"overlayDecisions\":null,\"externalTextureAuthorities\":[],\"finalOutputAuthority\":null}";
    private const string LegacySchema7Golden =
        "{\"schemaVersion\":7,\"assetSetId\":\"legacy-schema7-dint-bodyslide\",\"edition\":\"skyrimse\",\"nam9Authority\":{\"pluginPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/template.esp\",\"pluginSha256\":\"69dbc895aeecf39b0a0ce15bfe54f0c8021924d9358369e4a7bdf4a95938037b\",\"npcFormId\":\"0x00000800\",\"trailingValue\":0},\"faceTint\":{\"width\":1,\"height\":1},\"privateHeadTextures\":{\"diffuse\":\"Textures/golden.dds\",\"normalOrGloss\":\"Textures/golden.dds\",\"glowOrDetailMap\":\"Textures/golden.dds\",\"height\":\"Textures/golden.dds\",\"backlightMaskOrSpecular\":\"Textures/golden.dds\",\"environmentMaskOrSubsurfaceTint\":null,\"environment\":null,\"multilayer\":null},\"packageAssets\":[],\"overlayDecisions\":null,\"externalTextureAuthorities\":[],\"bodySlidePresetAuthority\":{\"manifestPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/dint-preset.json\",\"manifestSha256\":\"4e9290e9d4f1e8509ef5bad77bf07a757719d71b167ad71eeb4e2643a1dd23c0\"},\"bodyMeshAuthority\":{\"manifestPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/dint-meshes.json\",\"manifestSha256\":\"182114bf104024260439b612394d7cc43cf927d705bd9e75dc0c57e2b136a27f\"},\"nativeFaceGeomExternalHeadParts\":[{\"order\":0,\"sourceFormIdentifier\":\"[dint999] HairPack02.esp|00BC05\",\"providerFormKey\":\"[dint999] HairPack02.esp|0x0000BC05\",\"disposition\":\"record-only-external\"}],\"finalOutputAuthority\":null}";

    private static async ValueTask AssertLegacyCompatibilityCoreAsync(
        CancellationToken cancellationToken)
    {
        string ownerId = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
        string root = Path.Combine(
            LabRoot.Value,
            "artifacts",
            LegacyGoldenRootPrefix + "-" + ownerId);
        string marker = Path.Combine(root, ".owner");
        Require(!Directory.Exists(root) && !File.Exists(root),
            "The compatibility root unexpectedly existed before this test owned it.");
        EnsureNoReparsePath(LabRoot.Value, root);
        Directory.CreateDirectory(root);
        bool ownsRoot = false;
        try
        {
            EnsureNoReparsePath(LabRoot.Value, root);
            Require((File.GetAttributes(root) & FileAttributes.Directory) != 0,
                "The compatibility root was not created as a directory.");
            using (var ownerStream = new FileStream(
                       marker,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                byte[] ownerBytes = Encoding.UTF8.GetBytes(ownerId);
                ownerStream.Write(ownerBytes);
            }
            ownsRoot = true;
            Require(File.ReadAllText(marker) == ownerId,
                "The compatibility ownership marker did not round-trip.");
            WriteLegacyFile(root, "template.esp", "golden-plugin");
            WriteLegacyFile(root, "package.dds", "golden-package");
            WriteLegacyFile(root, "facegeom.nif", "golden-facegeom");
            WriteLegacyFile(root, "facetint.dds", "golden-facetint");
            WriteLegacyFile(root, "evidence.json", "golden-evidence");
            WriteLegacyFile(root, "face-bake.json", "golden-facebake");
            WriteLegacyFile(root, "face-texture-bake.json", "golden-facetexture");
            string finalJson = NormalizeCompatibilityGolden(
                LegacyFinalOutputGolden, root);
            string presetJson = NormalizeCompatibilityGolden(
                LegacyPresetGolden, root);
            string meshJson = NormalizeCompatibilityGolden(
                LegacyMeshGolden, root).Replace(
                "4e9290e9d4f1e8509ef5bad77bf07a757719d71b167ad71eeb4e2643a1dd23c0",
                HashBytes(Encoding.UTF8.GetBytes(presetJson)).Value,
                StringComparison.Ordinal);
            string finalHash = HashBytes(Encoding.UTF8.GetBytes(finalJson)).Value;
            string presetHash = HashBytes(Encoding.UTF8.GetBytes(presetJson)).Value;
            string meshHash = HashBytes(Encoding.UTF8.GetBytes(meshJson)).Value;
            WriteLegacyFile(root, "final.json", finalJson);
            WriteLegacyFile(root, "dint.xml",
                "<SliderPresets><Preset name=\"Dint Legacy\" set=\"UBE SE 2.0 Release Body\" /></SliderPresets>");
            WriteLegacyFile(root, "dint-preset.json", presetJson);
            foreach (string role in new[] { "body0", "body1", "hands0", "hands1", "feet0", "feet1" })
                WriteLegacyFile(root, role + ".nif", role);
            WriteLegacyFile(root, "dint-meshes.json", meshJson);

            await InvokeExistingDirectCarrierGoldensAsync(cancellationToken);
            var goldens = new (int Schema, string Name, string Json)[]
            {
                (3, "schema3-final.json", NormalizeCompatibilityGolden(
                    LegacySchema3Golden, root).Replace(
                    "2be97d6c2a024d4ca7afdc2172f799202e48703aa596bc8bc36438e16c634d97",
                    finalHash, StringComparison.Ordinal)),
                (4, "schema4-face-bake.json", NormalizeCompatibilityGolden(
                    LegacySchema4Golden, root)),
                (5, "schema5-face-texture-bake.json", NormalizeCompatibilityGolden(
                    LegacySchema5Golden, root)),
                (6, "schema6-ube-direct-carrier.json", NormalizeCompatibilityGolden(
                    LegacySchema6Golden, root)),
                (7, "schema7-dint-bodyslide.json", NormalizeCompatibilityGolden(
                    LegacySchema7Golden, root)
                    .Replace("4e9290e9d4f1e8509ef5bad77bf07a757719d71b167ad71eeb4e2643a1dd23c0",
                        presetHash, StringComparison.Ordinal)
                    .Replace("182114bf104024260439b612394d7cc43cf927d705bd9e75dc0c57e2b136a27f",
                        meshHash, StringComparison.Ordinal))
            };
            foreach ((int schema, string name, string json) in goldens)
            {
                byte[] expected = Encoding.UTF8.GetBytes(json);
                WorkspacePath path = new(Path.Combine(root, name));
                await File.WriteAllBytesAsync(path.Value, expected, cancellationToken);
                byte[] before = await File.ReadAllBytesAsync(path.Value, cancellationToken);
                Require(before.AsSpan().SequenceEqual(expected),
                    $"Legacy schema-{schema} golden changed before readback.");
                RaceMenuNpcStandaloneAuthorityReadResult result =
                    await ReadManifestAsync(path, before, cancellationToken);
                Require(result.Accepted && result.Assets?.SchemaVersion == schema,
                    $"Legacy schema-{schema} golden was not readable: " +
                    FormatDiagnostics(result.Diagnostics));
                if (schema == 7)
                    Require(result.Assets!.NativeFaceGeomExternalHeadParts.Length == 1 &&
                            result.Assets.NativeFaceGeomExternalHeadParts[0].ProviderFormKey ==
                            SkyrimNativeFaceGeomExternalHeadPartAuthority.DintProviderFormKey,
                        "Readable Dint schema-7 golden lost its native external headpart row.");
                byte[] after = await File.ReadAllBytesAsync(path.Value, cancellationToken);
                Require(after.AsSpan().SequenceEqual(expected),
                    $"Legacy schema-{schema} golden changed after readback.");
            }
        }
        finally
        {
            if (ownsRoot)
                DeleteOwnedCompatibilityRoot(root, marker, ownerId);
        }
    }

    private static string NormalizeCompatibilityGolden(string json, string root)
    {
        string relativeRoot = Path.GetRelativePath(LabRoot.Value, root)
            .Replace(Path.DirectorySeparatorChar, '/');
        return json.Replace(LegacyGoldenPathPrefix, relativeRoot,
            StringComparison.Ordinal);
    }

    private static void EnsureNoReparsePath(string expectedParent, string target)
    {
        string parent = Path.GetFullPath(expectedParent)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(target);
        Require(resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase),
            "The compatibility path escaped its named test parent.");
        DirectoryInfo? current = new DirectoryInfo(resolved);
        while (current is not null)
        {
            if (Directory.Exists(current.FullName) || File.Exists(current.FullName))
            {
                FileAttributes attributes = File.GetAttributes(current.FullName);
                Require(!attributes.HasFlag(FileAttributes.ReparsePoint),
                    $"A reparse point was found in the compatibility path: {current.FullName}");
            }
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return;
            current = current.Parent;
        }
        throw new InvalidOperationException(
            "The compatibility path did not resolve beneath its expected parent.");
    }

    private static void DeleteOwnedCompatibilityRoot(
        string root, string marker, string ownerId)
    {
        Require(Directory.Exists(root) && !File.Exists(root),
            "The owned compatibility root was replaced before cleanup.");
        EnsureNoReparsePath(LabRoot.Value, root);
        FileAttributes markerAttributes = File.GetAttributes(marker);
        Require(!markerAttributes.HasFlag(FileAttributes.ReparsePoint) &&
                !markerAttributes.HasFlag(FileAttributes.Directory) &&
                File.ReadAllText(marker) == ownerId,
            "The compatibility ownership marker did not prove exact ownership.");
        EnsureNoReparseTree(root);
        Directory.Delete(root, recursive: true);
        Require(!Directory.Exists(root) && !File.Exists(root),
            "The owned compatibility root remained after cleanup.");
    }

    private static void EnsureNoReparseTree(string root)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            Require(!attributes.HasFlag(FileAttributes.ReparsePoint),
                $"A reparse point was found in the owned compatibility tree: {entry}");
            if (attributes.HasFlag(FileAttributes.Directory))
                EnsureNoReparseTree(entry);
        }
    }

    private static async ValueTask InvokeExistingDirectCarrierGoldensAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (string name in new[] { "TestQualifiedCarrierTopology",
                     "TestQualifiedCarrierAlphaTopology" })
        {
            var method = typeof(Program).GetMethod(name,
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic);
            Require(method is not null,
                $"Existing direct-carrier golden helper '{name}' was not found.");
            object? result = method!.Invoke(null, null);
            if (result is Task task)
                await task.ConfigureAwait(false);
        }
    }

    private static void WriteLegacyFile(string root, string name, string content) =>
        File.WriteAllText(Path.Combine(root, name), content);

    private const string LegacyFinalOutputGolden =
        "{\"schemaVersion\":1,\"authorityId\":\"legacy-final\",\"edition\":\"skyrimse\",\"sourceKind\":\"admitted-final-oracle\",\"presetSha256\":\"1111111111111111111111111111111111111111111111111111111111111111\",\"charGenFaceGeomSha256\":\"2222222222222222222222222222222222222222222222222222222222222222\",\"charGenFaceTintSha256\":\"3333333333333333333333333333333333333333333333333333333333333333\",\"faceGeom\":{\"path\":\"artifacts/racemenu-standalone-schema8-compat-golden/facegeom.nif\",\"sha256\":\"191bf72e006c1c2a4a9c9edcf986128292a8b8bb0a65dd1c500b1c971603fbbd\"},\"faceTint\":{\"path\":\"artifacts/racemenu-standalone-schema8-compat-golden/facetint.dds\",\"sha256\":\"233bfef0eb73736243cdde5924038d603ff365ed3601331972e8d0bedfcca77a\",\"width\":1,\"height\":1},\"evidence\":[{\"path\":\"artifacts/racemenu-standalone-schema8-compat-golden/evidence.json\",\"sha256\":\"4d739ce691e7d0323e1d77f7fd124598615fd94c3e5251d38dee802d596fd178\"}],\"runtimeAuthority\":false}";
    private const string LegacyPresetGolden =
        "{\"schemaVersion\":1,\"authorityId\":\"dint-legacy-bodyslide\",\"edition\":\"skyrimse\",\"sourceKind\":\"bodyslide-sliderpreset-xml\",\"presetXmlPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/dint.xml\",\"presetXmlSha256\":\"c689be8313082e1ca30a2454ce53f15c3f00098260df41374f4bfcb2afc69530\",\"presetName\":\"Dint Legacy\",\"sliderSet\":\"UBE SE 2.0 Release Body\",\"groups\":[\"UBE\",\"UBE Female\"],\"sliderCount\":1,\"runtimeAuthority\":false}";
    private const string LegacyMeshGolden =
        "{\"schemaVersion\":1,\"authorityId\":\"dint-legacy-meshes\",\"edition\":\"skyrimse\",\"sourceKind\":\"external-bodyslide-generated-meshes\",\"bodySlidePresetAuthority\":{\"manifestPath\":\"artifacts/racemenu-standalone-schema8-compat-golden/dint-preset.json\",\"manifestSha256\":\"4e9290e9d4f1e8509ef5bad77bf07a757719d71b167ad71eeb4e2643a1dd23c0\"},\"meshes\":[{\"role\":\"body0\",\"sourcePath\":\"artifacts/racemenu-standalone-schema8-compat-golden/body0.nif\",\"sha256\":\"cdb2708db3af6ec743ef3a63ed2465c002da3ddebd89f91522519ccbc32089cf\",\"destination\":\"Meshes/Actors/Character/schema8/body_0.nif\"},{\"role\":\"body1\",\"sourcePath\":\"artifacts/racemenu-standalone-schema8-compat-golden/body1.nif\",\"sha256\":\"85316b1d8ccfedc2fa38c3ec9cc3deb3bec633178e31f7c86af433cd9593ad32\",\"destination\":\"Meshes/Actors/Character/schema8/body_1.nif\"},{\"role\":\"hands0\",\"sourcePath\":\"artifacts/racemenu-standalone-schema8-compat-golden/hands0.nif\",\"sha256\":\"58e2191b57d545ee02884d670a6703c84124728f72efecce949e680c964673d2\",\"destination\":\"Meshes/Actors/Character/schema8/hands_0.nif\"},{\"role\":\"hands1\",\"sourcePath\":\"artifacts/racemenu-standalone-schema8-compat-golden/hands1.nif\",\"sha256\":\"a512db00f53d1e142f9bad5e8b3db3261f1ab0ddb4a6a45090bcb3339334b58c\",\"destination\":\"Meshes/Actors/Character/schema8/hands_1.nif\"},{\"role\":\"feet0\",\"sourcePath\":\"artifacts/racemenu-standalone-schema8-compat-golden/feet0.nif\",\"sha256\":\"a2fef52f85d1c59b272e63c4f7688393bf08fec52c433f8a452a4fbe08d73825\",\"destination\":\"Meshes/Actors/Character/schema8/feet_0.nif\"},{\"role\":\"feet1\",\"sourcePath\":\"artifacts/racemenu-standalone-schema8-compat-golden/feet1.nif\",\"sha256\":\"f1ce0952d7db7f5423836f964f630128ca9f35887794300cef215d74c7ca8972\",\"destination\":\"Meshes/Actors/Character/schema8/feet_1.nif\"}],\"runtimeAuthority\":false}";

    private static async ValueTask AssertCanonicalReaderAsync(
        WorkspacePath manifestPath,
        byte[] bytes,
        ExternalHeadPartDependencyDescriptor expectedDescriptor,
        ExternalHeadPartFaceGeomExclusionAttestation expectedAttestation,
        CancellationToken cancellationToken)
    {
        RaceMenuNpcStandaloneAuthorityReadResult result =
            await ReadManifestAsync(manifestPath, bytes, cancellationToken);
        Require(result.Accepted && result.Assets is not null,
            "A canonical schema-8 standalone authority was refused: " +
            FormatDiagnostics(result.Diagnostics));
        RaceMenuNpcStandaloneAssets assets = result.Assets ??
            throw new InvalidOperationException(
                "Schema-8 reader accepted without parsed assets.");
        Require(assets.SchemaVersion == 8 && assets.PackageAssets.IsEmpty &&
                assets.FaceBakeAuthority is null &&
                assets.FaceTextureBakeAuthority is null &&
                assets.ExternalCharGenExportAuthority is null &&
                assets.FinalOutputAuthority is null &&
                assets.NativeFaceGeomExternalHeadParts.IsEmpty,
            "Schema-8 standalone authority retained a forbidden legacy authority.");
        Require(assets.ExternalHeadPartDependencies.Length == 1 &&
                assets.ExternalHeadPartExclusionAttestations.Length == 1 &&
                CanonicalDescriptorEquals(
                    assets.ExternalHeadPartDependencies[0], expectedDescriptor) &&
                CanonicalAttestationEquals(
                    assets.ExternalHeadPartExclusionAttestations[0], expectedAttestation) &&
                assets.ExternalHeadPartExclusionAttestations[0].DescriptorId ==
                    assets.ExternalHeadPartDependencies[0].DescriptorId,
            "Schema-8 standalone authority did not retain exact descriptor/attestation semantics.");
    }

    private static async ValueTask AssertReaderNegativesAsync(
        string root,
        byte[] baseline,
        ExternalHeadPartDependencyDescriptor descriptor,
        ExternalHeadPartFaceGeomExclusionAttestation attestation,
        string templateRelative,
        CancellationToken cancellationToken)
    {
        await AssertRejectedAsync(root, baseline, "unknown-field",
            node => node["unexpected"] = true, cancellationToken);
        await AssertRejectedAsync(root, baseline, "missing-descriptors",
            node => node.Remove("externalHeadPartDependencies"), cancellationToken);
        await AssertRejectedAsync(root, baseline, "missing-attestations",
            node => node.Remove("externalHeadPartFaceGeomExclusionAttestations"),
            cancellationToken);
        await AssertRejectedAsync(root, baseline, "empty-descriptors",
            node => node["externalHeadPartDependencies"] = new JsonArray(),
            cancellationToken);
        await AssertRejectedAsync(root, baseline, "empty-attestations",
            node => node["externalHeadPartFaceGeomExclusionAttestations"] =
                new JsonArray(), cancellationToken);
        await AssertRejectedAsync(root, baseline, "duplicate-descriptor",
            node =>
            {
                var rows = node["externalHeadPartDependencies"]!.AsArray();
                rows.Add(rows[0]!.DeepClone());
                var attestations = node[
                    "externalHeadPartFaceGeomExclusionAttestations"]!.AsArray();
                attestations.Add(attestations[0]!.DeepClone());
            }, cancellationToken);
        await AssertRejectedAsync(root, baseline, "duplicate-attestation",
            node =>
            {
                var rows = node[
                    "externalHeadPartFaceGeomExclusionAttestations"]!.AsArray();
                rows.Add(rows[0]!.DeepClone());
                var descriptors = node["externalHeadPartDependencies"]!.AsArray();
                descriptors.Add(descriptors[0]!.DeepClone());
            }, cancellationToken);
        await AssertRejectedAsync(root, baseline, "orphan-attestation",
            node =>
            {
                ExternalHeadPartFaceGeomExclusionAttestation orphan = attestation with
                {
                    DescriptorId = Hash("schema8-orphan-descriptor")
                };
                orphan = orphan with
                {
                    AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                        .ComputeAttestationHash(orphan)
                };
                node["externalHeadPartFaceGeomExclusionAttestations"] =
                    JsonNode.Parse(Encoding.UTF8.GetString(
                        ExternalHeadPartDependencyDescriptorCodec
                            .SerializeAttestation(orphan)));
            }, cancellationToken);
        await AssertRejectedAsync(root, baseline, "bad-descriptor-id",
            node => node["externalHeadPartDependencies"]!.AsArray()[0]![
                "descriptorId"] = Hash("schema8-bad-id").Value,
            cancellationToken);
        await AssertRejectedAsync(root, baseline, "bad-attestation-hash",
            node => node["externalHeadPartFaceGeomExclusionAttestations"]!
                .AsArray()[0]!["attestationSha256"] = Hash("schema8-bad-hash").Value,
            cancellationToken);
        await AssertRejectedAsync(root, baseline, "nonempty-package-assets",
            node => node["packageAssets"] = new JsonArray(
                new JsonObject
                {
                    ["sourcePath"] = templateRelative,
                    ["sha256"] = Hash("template-plugin").Value,
                    ["destination"] = "Textures/schema8-forbidden.dds"
                }), cancellationToken);
        await AssertRejectedAsync(root, baseline, "nonnull-final-output",
            node => node["finalOutputAuthority"] = new JsonObject(),
            cancellationToken);
        await AssertRejectedAsync(root, baseline, "face-bake-authority",
            node => node["faceBakeAuthority"] = null, cancellationToken);
        await AssertRejectedAsync(root, baseline, "face-texture-bake-authority",
            node => node["faceTextureBakeAuthority"] = null, cancellationToken);
        await AssertRejectedAsync(root, baseline, "external-chargen-authority",
            node => node["externalCharGenExportAuthority"] = null,
            cancellationToken);
        await AssertRejectedAsync(root, baseline, "half-body-slide",
            node =>
            {
                node["bodySlidePresetAuthority"] = new JsonObject();
                node["bodyMeshAuthority"] = null;
            }, cancellationToken);

        string duplicatePath = Path.Combine(root, "schema8-duplicate-root.json");
        string json = Encoding.UTF8.GetString(baseline);
        int key = json.IndexOf("\"assetSetId\"", StringComparison.Ordinal);
        Require(key >= 0, "Schema-8 baseline omitted assetSetId.");
        byte[] duplicate = Encoding.UTF8.GetBytes(
            json.Insert(key, "\"assetSetId\":\"duplicate\",\n    "));
        await AssertRejectedAtPathAsync(
            new WorkspacePath(duplicatePath), duplicate, "duplicate-root-key",
            cancellationToken);
    }

    private static async ValueTask AssertRejectedAsync(
        string root,
        byte[] baseline,
        string name,
        Action<JsonObject> mutate,
        CancellationToken cancellationToken)
    {
        JsonObject node = JsonNode.Parse(baseline)?.AsObject() ??
            throw new InvalidDataException("Schema-8 test baseline was not an object.");
        mutate(node);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(node,
            IndentedJson);
        await AssertRejectedAtPathAsync(
            new WorkspacePath(Path.Combine(root, $"schema8-{name}.json")),
            bytes, name, cancellationToken);
    }

    private static async ValueTask AssertRejectedAtPathAsync(
        WorkspacePath path,
        byte[] bytes,
        string name,
        CancellationToken cancellationToken)
    {
        await File.WriteAllBytesAsync(path.Value, bytes, cancellationToken);
        RaceMenuNpcStandaloneAuthorityReadResult result =
            await ReadManifestAsync(path, bytes, cancellationToken);
        Require(!result.Accepted,
            $"Schema-8 reader accepted negative fixture '{name}'.");
    }

    private static async ValueTask<RaceMenuNpcStandaloneAuthorityReadResult>
        ReadManifestAsync(
            WorkspacePath manifestPath,
            byte[] bytes,
            CancellationToken cancellationToken,
            IAssetIndexer? assetIndexer = null)
    {
        var reader = new RaceMenuNpcStandaloneAuthorityReader(
            assetIndexer ?? new NoOpAssetIndexer(),
            new KOnlyWorkspacePolicy(LabRoot, ProtectedRoot),
            LabRoot);
        return await reader.ReadAsync(
            new RaceMenuNpcStandaloneAssetAuthority(
                manifestPath, HashBytes(bytes)), cancellationToken);
    }

    private sealed record BodySlideFixture(
        WorkspacePath PresetManifest,
        Sha256Hash PresetManifestHash,
        WorkspacePath MeshManifest,
        Sha256Hash MeshManifestHash);

    private static async ValueTask AssertBodySlideStatesAsync(
        string root,
        byte[] baseline,
        ExternalHeadPartDependencyDescriptor descriptor,
        ExternalHeadPartFaceGeomExclusionAttestation attestation,
        CancellationToken cancellationToken)
    {
        BodySlideFixture fixture = CreateBodySlideFixture(root);
        string json = Encoding.UTF8.GetString(baseline);
        string bodySlide = JsonSerializer.Serialize(new
        {
            manifestPath = Relative(fixture.PresetManifest),
            manifestSha256 = fixture.PresetManifestHash.Value
        }, IndentedJson);
        string bodyMesh = JsonSerializer.Serialize(new
        {
            manifestPath = Relative(fixture.MeshManifest),
            manifestSha256 = fixture.MeshManifestHash.Value
        }, IndentedJson);
        string bodySlideProperty = "\"bodySlidePresetAuthority\": null";
        string bodyMeshProperty = "\"bodyMeshAuthority\": null";
        Require(json.Contains(bodySlideProperty, StringComparison.Ordinal) &&
                json.Contains(bodyMeshProperty, StringComparison.Ordinal),
            "Schema-8 baseline omitted the optional BodySlide fields.");
        json = json.Replace(bodySlideProperty,
                "\"bodySlidePresetAuthority\": " + bodySlide,
                StringComparison.Ordinal)
            .Replace(bodyMeshProperty,
                "\"bodyMeshAuthority\": " + bodyMesh,
                StringComparison.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        WorkspacePath path = new(Path.Combine(root, "schema8-with-bodyslide.json"));
        await File.WriteAllBytesAsync(path.Value, bytes, cancellationToken);
        RaceMenuNpcStandaloneAuthorityReadResult result =
            await ReadManifestAsync(path, bytes, cancellationToken);
        Require(result.Accepted && result.Assets is not null,
            "Schema-8 standalone authority with paired BodySlide evidence was refused: " +
            FormatDiagnostics(result.Diagnostics));
        Require(result.Assets!.BodySlidePresetAuthority is not null &&
                result.Assets.BodyMeshAuthority is not null &&
                CanonicalDescriptorEquals(
                    result.Assets.ExternalHeadPartDependencies[0], descriptor) &&
                CanonicalAttestationEquals(
                    result.Assets.ExternalHeadPartExclusionAttestations[0], attestation),
            "Schema-8 paired BodySlide readback lost the external authority arrays.");
    }

    private static BodySlideFixture CreateBodySlideFixture(string root)
    {
        WorkspacePath xml = new(Path.Combine(root, "schema8-sliderpreset.xml"));
        File.WriteAllText(xml.Value,
            "<SliderPresets><Preset name=\"schema8\" set=\"fixture\" /></SliderPresets>");
        byte[] presetManifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                authorityId = "schema8-bodyslide",
                edition = "skyrimse",
                sourceKind = "bodyslide-sliderpreset-xml",
                presetXmlPath = Relative(xml),
                presetXmlSha256 = HashBytes(File.ReadAllBytes(xml.Value)).Value,
                presetName = "schema8",
                sliderSet = "fixture",
                groups = BodySlideGroups,
                sliderCount = 1,
                runtimeAuthority = false
            });
        WorkspacePath presetManifest = new(Path.Combine(
            root, "schema8-bodyslide-preset.json"));
        File.WriteAllBytes(presetManifest.Value, presetManifestBytes);

        var meshRows = new List<object>();
        foreach ((string role, string destination) in BodyMeshRoles)
        {
            WorkspacePath mesh = new(Path.Combine(root, $"schema8-{role}.nif"));
            File.WriteAllBytes(mesh.Value, Encoding.UTF8.GetBytes(role));
            meshRows.Add(new
            {
                role,
                sourcePath = Relative(mesh),
                sha256 = HashBytes(File.ReadAllBytes(mesh.Value)).Value,
                destination
            });
        }
        var bodySlideReference = new
        {
            manifestPath = Relative(presetManifest),
            manifestSha256 = HashBytes(presetManifestBytes).Value
        };
        byte[] meshManifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 1,
                authorityId = "schema8-body-meshes",
                edition = "skyrimse",
                sourceKind = "external-bodyslide-generated-meshes",
                bodySlidePresetAuthority = bodySlideReference,
                meshes = meshRows,
                runtimeAuthority = false
            });
        WorkspacePath meshManifest = new(Path.Combine(
            root, "schema8-bodyslide-meshes.json"));
        File.WriteAllBytes(meshManifest.Value, meshManifestBytes);
        return new BodySlideFixture(
            presetManifest, HashBytes(presetManifestBytes),
            meshManifest, HashBytes(meshManifestBytes));
    }

    private static async ValueTask AssertWriterAsync(
        string root,
        ExternalHeadPartDependencyDescriptor descriptor,
        ExternalHeadPartFaceGeomExclusionAttestation attestation,
        string templateRelative,
        CancellationToken cancellationToken)
    {
        WriterFixture fixture = CreateWriterFixture(root, descriptor, attestation);
        RaceMenuPresetStandaloneAuthorityWriteResult result =
            await fixture.Writer.WriteAsync(fixture.Request, cancellationToken);
        Require(result.Written && result.Artifact is not null,
            "Schema-8 standalone writer refused a canonical in-memory authority: " +
            FormatDiagnostics(result.Diagnostics));
        RaceMenuPresetStandaloneAuthorityArtifact artifact = result.Artifact!;
        Require(artifact.ExternalHeadPartDependencies.Length == 1 &&
                artifact.ExternalHeadPartExclusionAttestations.Length == 1,
            "Schema-8 writer artifact did not retain both descriptor arrays.");
        byte[] bytes = await File.ReadAllBytesAsync(
            fixture.Request.Destination.Value, cancellationToken);
        Require(artifact.ManifestSha256 == HashBytes(bytes),
            "Schema-8 writer artifact hash did not match reopened bytes.");
        using (JsonDocument document = JsonDocument.Parse(bytes))
        {
            JsonElement rootElement = document.RootElement;
            Require(rootElement.GetProperty("schemaVersion").GetInt32() == 8 &&
                    rootElement.GetProperty("packageAssets").GetArrayLength() == 0 &&
                    rootElement.GetProperty("finalOutputAuthority").ValueKind ==
                        JsonValueKind.Null &&
                    !rootElement.TryGetProperty("faceBakeAuthority", out _) &&
                    !rootElement.TryGetProperty("faceTextureBakeAuthority", out _) &&
                    !rootElement.TryGetProperty("externalCharGenExportAuthority", out _) &&
                    !rootElement.TryGetProperty("nativeFaceGeomExternalHeadParts", out _),
                "Schema-8 writer emitted a forbidden legacy or bake authority.");
            ExternalHeadPartDependencyDescriptor reopenedDescriptor =
                ExternalHeadPartDependencyDescriptorCodec.ParseDescriptor(
                    Encoding.UTF8.GetBytes(rootElement
                        .GetProperty("externalHeadPartDependencies")[0].GetRawText()));
            ExternalHeadPartFaceGeomExclusionAttestation reopenedAttestation =
                ExternalHeadPartDependencyDescriptorCodec.ParseAttestation(
                    Encoding.UTF8.GetBytes(rootElement
                        .GetProperty("externalHeadPartFaceGeomExclusionAttestations")[0]
                        .GetRawText()));
            Require(CanonicalDescriptorEquals(reopenedDescriptor, descriptor) &&
                    CanonicalAttestationEquals(reopenedAttestation, attestation) &&
                    reopenedAttestation.DescriptorId == reopenedDescriptor.DescriptorId,
                "Schema-8 writer readback changed descriptor or attestation semantics.");
        }
        RaceMenuNpcStandaloneAuthorityReadResult readback =
            await ReadManifestAsync(fixture.Request.Destination, bytes,
                cancellationToken, new LooseAssetIndexer());
        Require(readback.Accepted && readback.Assets is not null &&
                CanonicalDescriptorEquals(
                    readback.Assets.ExternalHeadPartDependencies[0], descriptor) &&
                CanonicalAttestationEquals(
                    readback.Assets.ExternalHeadPartExclusionAttestations[0], attestation) &&
                readback.Assets.NativeFaceGeomExternalHeadParts.IsEmpty,
            "Schema-8 writer output did not reopen through the production reader: " +
            FormatDiagnostics(readback.Diagnostics));

        // A second destination proves canonical bytes are independent of the
        // transaction path and that the writer did not smuggle legacy rows.
        WorkspacePath secondDestination = new(Path.Combine(root, "writer-second.json"));
        RaceMenuPresetStandaloneAuthorityWriteResult second =
            await fixture.Writer.WriteAsync(
                fixture.Request with { Destination = secondDestination },
                cancellationToken);
        Require(second.Written && second.Artifact is not null,
            "Schema-8 writer did not repeat a deterministic transaction.");
        byte[] secondBytes = await File.ReadAllBytesAsync(
            secondDestination.Value, cancellationToken);
        Require(bytes.AsSpan().SequenceEqual(secondBytes),
            "Schema-8 writer emitted different canonical bytes for the same authority.");

        ExternalHeadPartFaceGeomExclusionAttestation writerOrphan = attestation with
        {
            DescriptorId = Hash("schema8-writer-orphan")
        };
        writerOrphan = writerOrphan with
        {
            AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                .ComputeAttestationHash(writerOrphan)
        };
        await AssertWriterNegativeAsync(fixture, "writer-half-pair",
            fixture.Request with
            {
                Destination = new WorkspacePath(Path.Combine(root, "writer-half-pair.json")),
                ExternalHeadPartExclusionAttestations = []
            }, cancellationToken);
        await AssertWriterNegativeAsync(fixture, "writer-orphan",
            fixture.Request with
            {
                Destination = new WorkspacePath(Path.Combine(root, "writer-orphan.json")),
                ExternalHeadPartExclusionAttestations = [writerOrphan]
            }, cancellationToken);
        ExternalHeadPartDependencyDescriptor badDescriptor = descriptor with
        {
            DescriptorId = Hash("schema8-writer-bad-id")
        };
        await AssertWriterNegativeAsync(fixture, "writer-bad-id",
            fixture.Request with
            {
                Destination = new WorkspacePath(Path.Combine(root, "writer-bad-id.json")),
                ExternalHeadPartDependencies = [badDescriptor]
            }, cancellationToken);
        ExternalHeadPartFaceGeomExclusionAttestation badAttestation = attestation with
        {
            AttestationSha256 = Hash("schema8-writer-bad-hash")
        };
        await AssertWriterNegativeAsync(fixture, "writer-bad-hash",
            fixture.Request with
            {
                Destination = new WorkspacePath(Path.Combine(root, "writer-bad-hash.json")),
                ExternalHeadPartExclusionAttestations = [badAttestation]
            }, cancellationToken);
        await AssertWriterNegativeAsync(fixture, "writer-duplicate",
            fixture.Request with
            {
                Destination = new WorkspacePath(Path.Combine(root, "writer-duplicate.json")),
                ExternalHeadPartDependencies = [descriptor, descriptor],
                ExternalHeadPartExclusionAttestations = [attestation, attestation]
            }, cancellationToken);

        _ = templateRelative; // The writer fixture uses the same K-local root.
    }

    private static async ValueTask AssertWriterNegativeAsync(
        WriterFixture fixture,
        string name,
        RaceMenuPresetStandaloneAuthorityWriteRequest request,
        CancellationToken cancellationToken)
    {
        RaceMenuPresetStandaloneAuthorityWriteResult result =
            await fixture.Writer.WriteAsync(request, cancellationToken);
        Require(!result.Written && !File.Exists(request.Destination.Value),
            $"Schema-8 writer accepted negative fixture '{name}'.");
    }

    private sealed record WriterFixture(
        RaceMenuPresetStandaloneAuthorityWriter Writer,
        RaceMenuPresetStandaloneAuthorityWriteRequest Request);

    private static WriterFixture CreateWriterFixture(
        string root,
        ExternalHeadPartDependencyDescriptor descriptor,
        ExternalHeadPartFaceGeomExclusionAttestation attestation)
    {
        WorkspacePath rootPath = new(root);
        WorkspacePath presetPath = new(Path.Combine(root, "writer-preset.jslot"));
        WorkspacePath faceGeomPath = new(Path.Combine(root, "writer-facegeom.nif"));
        WorkspacePath faceTintPath = new(Path.Combine(root, "writer-facetint.dds"));
        WorkspacePath bundlePath = new(Path.Combine(root, "writer-bundle.json"));
        WorkspacePath recordPath = new(Path.Combine(root, "writer-record.json"));
        WorkspacePath providerPath = new(Path.Combine(root, "writer-provider.json"));
        WorkspacePath templatePath = new(Path.Combine(root, "template.esp"));
        WorkspacePath carrierPath = new(Path.Combine(root, "writer-carrier.nif"));
        WorkspacePath tintManifestPath = new(Path.Combine(root, "writer-tint.json"));
        WorkspacePath dependenciesPath = new(Path.Combine(root, "writer-dependencies.json"));
        string textureDirectory = Path.Combine(root, "Textures");
        Directory.CreateDirectory(textureDirectory);
        WorkspacePath texturePath = new(Path.Combine(textureDirectory, "head.dds"));
        foreach ((WorkspacePath path, string text) in new[]
        {
            (presetPath, "writer-preset"),
            (faceGeomPath, "writer-facegeom"),
            (faceTintPath, "writer-facetint"),
            (bundlePath, "writer-bundle"),
            (recordPath, "writer-record"),
            (providerPath, "writer-provider"),
            (templatePath, "template-plugin"),
            (carrierPath, "writer-carrier"),
            (tintManifestPath, "writer-tint"),
            (dependenciesPath, "writer-dependencies"),
            (texturePath, "writer-head")
        })
            File.WriteAllText(path.Value, text);

        Sha256Hash presetHash = HashBytes(File.ReadAllBytes(presetPath.Value));
        Sha256Hash faceGeomHash = HashBytes(File.ReadAllBytes(faceGeomPath.Value));
        Sha256Hash faceTintHash = HashBytes(File.ReadAllBytes(faceTintPath.Value));
        Sha256Hash bundleHash = HashBytes(File.ReadAllBytes(bundlePath.Value));
        Sha256Hash recordHash = HashBytes(File.ReadAllBytes(recordPath.Value));
        Sha256Hash providerHash = HashBytes(File.ReadAllBytes(providerPath.Value));
        Sha256Hash templateHash = HashBytes(File.ReadAllBytes(templatePath.Value));
        Sha256Hash carrierHash = HashBytes(File.ReadAllBytes(carrierPath.Value));
        Sha256Hash textureHash = HashBytes(File.ReadAllBytes(texturePath.Value));
        var skyrim = new PluginName("Skyrim.esm");
        var reference = new FormReference(skyrim, new FormId(0x13746));
        var build = new RaceMenuNpcBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            new RaceMenuNpcPresetBundle(
                bundlePath, bundleHash, presetPath, presetHash,
                faceGeomPath, faceGeomHash, faceTintPath, faceTintHash,
                new RaceMenuNpcRecordAuthority(recordPath, recordHash)),
            new BlankNpcProviderBindingRequest(
                providerPath, providerHash, GameEdition.SkyrimSpecialEdition,
                NpcSex.Female, templatePath, templateHash, new FormId(0x800),
                carrierPath, carrierHash, tintManifestPath, rootPath,
                dependenciesPath),
            rootPath,
            new PluginName("Schema8Writer.esp"),
            new NpcCreationIdentity(new EditorId("Schema8Writer"),
                new NpcName("Schema8 Writer")),
            new SkyrimNpcCreationTraits(NpcSex.Female,
                NpcCreationRole.Follower, true, false, true, false, true),
            new SkyrimNpcCreationReferences(reference, reference, reference,
                reference, reference),
            new SkyrimNpcCreationStats(
                new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                0, 0, 0, 1, 1, 100, 0, 0, 50, 50, 50, 1F, 0F, 255));
        PresetDocument preset = CreatePreset(presetHash);
        var binding = new RaceMenuNpcFormBinding(
            new RecordSignature("HDPT"),
            new FormReference(new PluginName("OrchidAdornment.esp"),
                new FormId(0x800)),
            new FormReference(new PluginName("OrchidAdornment.esp"),
                new FormId(0x800)),
            new PluginName("OrchidAdornment.esp"), templatePath, templateHash,
            NpcHeadPartType.Hair);
        PresetHeadPart headPart = preset.Appearance.HeadParts[0];
        var resolved = new RaceMenuResolvedHeadPart(headPart, binding);
        var provider = new BlankNpcProviderArtifact(
            "1", "blank-npc-provider-binding", "schema8-provider",
            providerPath, providerHash, GameEdition.SkyrimSpecialEdition,
            NpcSex.Female, [skyrim], Hash("schema8-graph"), ["Head"],
            new AssetPath("Textures/provider/facetint.dds"), Hash("provider-tint"),
            Hash("provider-manifest"), "schema8-dependencies", Hash("dependencies"),
            1, 0, 0);
        var raceBinding = binding with
        {
            Signature = new RecordSignature("RACE"),
            HeadPartType = null
        };
        var plan = new RaceMenuNpcAppearancePlan(
            "1", "schema8-writer-bundle", build, preset, provider,
            bundleHash, "schema8-record", recordHash, faceGeomHash,
            faceTintHash, raceBinding,
            [new RaceMenuNpcHeadPartDisposition(
                headPart, RaceMenuNpcHeadPartDispositionKind.MappedRecord,
                resolved, null)],
            [resolved], null,
            new RaceMenuNpcExternalHairColorAuthority(0x112233, binding),
            [], [], null, [skyrim], [skyrim],
            [
                new RaceMenuNpcFieldCoverage(
                    RaceMenuNpcAppearanceField.Overlays, false,
                    RaceMenuNpcFieldCoverageKind.Blocked, "none"),
                new RaceMenuNpcFieldCoverage(
                    RaceMenuNpcAppearanceField.NodeTransforms, false,
                    RaceMenuNpcFieldCoverageKind.Blocked, "none"),
                new RaceMenuNpcFieldCoverage(
                    RaceMenuNpcAppearanceField.SkinOverrides, false,
                    RaceMenuNpcFieldCoverageKind.Blocked, "none")
            ],
            [], false);
        var target = new RaceMenuPresetTarget(
            "schema8-record", reference, NpcSex.Female, rootPath, []);
        var faceRecordProvider = new SkyrimFaceRecordProvider(
            new PluginName("OrchidAdornment.esp"), templatePath, templateHash);
        var headTextures = new SkyrimPrivateHeadTexturePaths(
            new AssetPath("Textures/head.dds"),
            new AssetPath("Textures/head.dds"),
            new AssetPath("Textures/head.dds"),
            new AssetPath("Textures/head.dds"),
            new AssetPath("Textures/head.dds"));
        var headTextureAuthority = new SkyrimFaceTextureSetAuthority(
            new FormReference(new PluginName("Skyrim.esm"), new FormId(0x901)),
            faceRecordProvider, headTextures, false);
        var raceAuthority = new SkyrimRaceTintAuthority(
            reference, faceRecordProvider, NpcSex.Female, [], false);
        var tintPlan = new RaceMenuPresetTintAuthorityPlan(
            preset, raceAuthority, [], new RaceMenuPresetQnamPlan(0, 0F, 0F, 0F));
        var draft = new RaceMenuPresetRecordAuthorityDraft(
            "schema8-record", preset, target, raceBinding,
            [new RaceMenuPresetHeadPartAuthority(headPart, binding)],
            binding, headTextureAuthority, 0x112233, new FormId(0x810),
            tintPlan, false);
        var externalTexture = new RaceMenuNpcExternalTextureAuthority(
            new AssetPath("Textures/head.dds"), "loose", texturePath,
            textureHash, textureHash);
        WorkspacePath destination = new(Path.Combine(root, "writer-schema8.json"));
        var request = new RaceMenuPresetStandaloneAuthorityWriteRequest(
            plan, draft,
            new RaceMenuNpcNam9TrailingAuthority(
                templatePath, templateHash, new FormId(0x800), 0F),
            null, [externalTexture], destination)
        {
            ExternalHeadPartDependencies = [descriptor],
            ExternalHeadPartExclusionAttestations = [attestation]
        };
        return new WriterFixture(
            new RaceMenuPresetStandaloneAuthorityWriter(
                new FixedFaceMorphSnapshotService(templateHash),
                new EmptyAssetAuthorityPlanner(),
                new FixedFaceTintDecoder(faceTintHash),
                new KOnlyWorkspacePolicy(LabRoot, ProtectedRoot), LabRoot),
            request);
    }

    private static PresetDocument CreatePreset(Sha256Hash sourceHash)
    {
        var headPart = new PresetHeadPart(
            PresetIdentifier.Parse("Skyrim.esm|0x00012345"), 1);
        var appearance = new PresetAppearance(
            1, [headPart], PresetHairColor.FromPackedRgb(0x112233),
            new PresetWeight(50F, null, null, null),
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            ImmutableDictionary<string, float>.Empty,
            ImmutableArray<float>.Empty, ImmutableArray<PresetTint>.Empty,
            ImmutableArray<PresetOverlay>.Empty, null,
            new PresetFieldPresence(true, true, true, true,
                false, false, false, false, false),
            ImmutableArray<PresetUnknownField>.Empty,
            RaceMenu: new RaceMenuPresetData(
                null, ImmutableArray<uint>.Empty, 10000,
                ImmutableArray<RaceMenuSculptPart>.Empty,
                ImmutableDictionary<string,
                    ImmutableDictionary<string, float>>.Empty,
                ImmutableArray<RaceMenuBodyOverlay>.Empty,
                ImmutableArray<SkyrimNodeTransform>.Empty,
                ImmutableArray<SkyrimSkinOverride>.Empty));
        return new PresetDocument(PresetFormat.RaceMenuJslot,
            GameEdition.SkyrimSpecialEdition, appearance, sourceHash, []);
    }

    private static void AssertPreparedRequestBoundary()
    {
        Type? dto = typeof(RaceMenuNpcExecutionRequestFileLoader).GetNestedType(
            "ExecutionRequestDto",
            System.Reflection.BindingFlags.NonPublic);
        Require(dto is not null,
            "The prepared execution-request DTO could not be located for the schema-8 boundary test.");
        Require(dto!.GetProperty("ExternalHeadPartDependencies") is null &&
                dto.GetProperty("ExternalHeadPartExclusionAttestations") is null,
            "Prepared execution-request DTO accepted schema-8 descriptor or attestation properties.");
        var descriptorProperty = typeof(RaceMenuNpcExecutionRequest).GetProperty(
            "ExternalHeadPartDependencies")!;
        var attestationProperty = typeof(RaceMenuNpcExecutionRequest).GetProperty(
            "ExternalHeadPartExclusionAttestations")!;
        Require(descriptorProperty.GetCustomAttributes(
                    typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true)
                    .Length == 1 &&
                attestationProperty.GetCustomAttributes(
                    typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true)
                    .Length == 1,
            "In-memory schema-8 execution properties are exposed to generic JSON serialization.");
    }

    private sealed class FixedFaceMorphSnapshotService(Sha256Hash pluginHash) :
        ISkyrimFaceMorphSnapshotService
    {
        public ValueTask<SkyrimFaceMorphSnapshotResult> ReadAsync(
            SkyrimFaceMorphSnapshotRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimFaceMorphSnapshotResult(
                true, pluginHash,
                new SkyrimFaceMorphSnapshot([], 0F, [], true, true), []));
    }

    private sealed class EmptyAssetAuthorityPlanner : ISkyrimAssetAuthorityPlanner
    {
        public ValueTask<SkyrimAssetAuthorityPlanResult> PlanAsync(
            SkyrimAssetAuthorityPlanRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SkyrimAssetAuthorityPlanResult(
                true, [], [], []));
    }

    private sealed class FixedFaceTintDecoder(Sha256Hash sourceHash) :
        IFaceTintTextureDecoder
    {
        public ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
            WorkspacePath sourceDds,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FaceTintTextureDecodeResult(
                true, 1024, 1024, null, sourceHash, []));
    }

    private static byte[] BuildManifest(
        ExternalHeadPartDependencyDescriptor descriptor,
        ExternalHeadPartFaceGeomExclusionAttestation attestation,
        string templateRelative)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 8);
            writer.WriteString("assetSetId", "selection-assets-schema8");
            writer.WriteString("edition", "skyrimse");
            writer.WriteStartObject("nam9Authority");
            writer.WriteString("pluginPath", templateRelative);
            writer.WriteString("pluginSha256", Hash("template-plugin").Value);
            writer.WriteString("npcFormId", "0x00000800");
            writer.WriteNumber("trailingValue", 0F);
            writer.WriteEndObject();
            writer.WriteStartObject("faceTint");
            writer.WriteNumber("width", 1024);
            writer.WriteNumber("height", 1024);
            writer.WriteEndObject();
            writer.WriteStartObject("privateHeadTextures");
            writer.WriteString("diffuse", "Textures/head.dds");
            writer.WriteString("normalOrGloss", "Textures/head.dds");
            writer.WriteString("glowOrDetailMap", "Textures/head.dds");
            writer.WriteString("height", "Textures/head.dds");
            writer.WriteString("backlightMaskOrSpecular", "Textures/head.dds");
            writer.WriteNull("environmentMaskOrSubsurfaceTint");
            writer.WriteNull("environment");
            writer.WriteNull("multilayer");
            writer.WriteEndObject();
            writer.WriteStartArray("packageAssets");
            writer.WriteEndArray();
            writer.WriteNull("overlayDecisions");
            writer.WriteStartArray("externalTextureAuthorities");
            writer.WriteEndArray();
            writer.WriteStartArray("externalHeadPartDependencies");
            writer.WriteRawValue(
                Encoding.UTF8.GetString(
                    ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                        descriptor)),
                skipInputValidation: true);
            writer.WriteEndArray();
            writer.WriteStartArray("externalHeadPartFaceGeomExclusionAttestations");
            writer.WriteRawValue(
                Encoding.UTF8.GetString(
                    ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
                        attestation)),
                skipInputValidation: true);
            writer.WriteEndArray();
            writer.WriteNull("bodySlidePresetAuthority");
            writer.WriteNull("bodyMeshAuthority");
            writer.WriteNull("finalOutputAuthority");
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static ExternalHeadPartDependencyDescriptor CreateDescriptor()
    {
        var model = new AssetPath(
            "meshes/actors/character/character assets/hair/orchid-root.nif");
        var xml = new AssetPath(
            "SKSE/Plugins/hdtSkinnedMeshConfigs/orchid-root.xml");
        ExternalHeadPartProviderIdentity provider = new(
            ProviderPlugin,
            Hash("provider-plugin"),
            1120,
            ExternalHeadPartRedistributionMode.ExternalProviderRequired);
        var member = new ExternalHeadPartRecordDependency(
            RootForm,
            ProviderPlugin,
            RootForm,
            ProviderPlugin,
            provider.PluginSha256,
            provider.PluginByteLength,
            Hash("root-record"),
            "OrchidRootHair",
            NpcHeadPartType.Hair,
            NpcHeadPartType.Hair,
            model,
            [],
            [],
            null,
            0,
            0,
            NpcSex.Male,
            null);
        var descriptor = new ExternalHeadPartDependencyDescriptor(
            ExternalHeadPartSchemaIdentifiers.Descriptor,
            Hash("placeholder-descriptor"),
            ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
            RootForm,
            RootForm,
            NpcHeadPartType.Hair,
            Hash("graph"),
            provider,
            [member],
            new ExternalHeadPartPhysicsBinding(
                ExternalHeadPartPhysicsBindingMode.DirectNifExtraData,
                [new ExternalHeadPartPhysicsShapeBinding(
                    RootForm,
                    model,
                    "OrchidRoot",
                    xml,
                    Hash("root-xml"),
                    231)],
                null),
            [new ExternalHeadPartAssetDependency(
                model,
                Hash("root-nif"),
                4096,
                ProviderPlugin,
                provider.PluginSha256,
                null)],
            []);
        return descriptor with
        {
            DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(descriptor)
        };
    }

    private static ExternalHeadPartFaceGeomExclusionAttestation CreateAttestation(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        var attestation = new ExternalHeadPartFaceGeomExclusionAttestation(
            ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
            Hash("placeholder-attestation"),
            descriptor.DescriptorId,
            new AssetPath("meshes/actors/character/facegeom/schema8.nif"),
            Hash("output-facegeom"),
            4096,
            [],
            [new ExternalHeadPartExcludedShapeEvidence(
                descriptor.Members[0].ModelNif!.Value,
                "OrchidRoot")],
            [new ExternalHeadPartExcludedMetadataEvidence(
                "physics-locator",
                "HDT Skinned Mesh Physics Object")],
            "preview254-v1");
        return attestation with
        {
            AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                .ComputeAttestationHash(attestation)
        };
    }

    private sealed class NoOpAssetIndexer : IAssetIndexer
    {
        public ValueTask<AssetIndex> IndexAsync(
            AssetIndexRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AssetIndex(
                request.Edition,
                [],
                []));
    }

    private sealed class LooseAssetIndexer : IAssetIndexer
    {
        public ValueTask<AssetIndex> IndexAsync(
            AssetIndexRequest request,
            CancellationToken cancellationToken)
        {
            WorkspacePath source = new(Path.Combine(
                request.DataRoot.Value, "Textures", "head.dds"));
            byte[] bytes = File.ReadAllBytes(source.Value);
            return ValueTask.FromResult(new AssetIndex(
                request.Edition,
                [new AssetProvider(
                    new AssetPath("Textures/head.dds"),
                    AssetProviderKind.Loose,
                    "loose",
                    bytes.LongLength,
                    HashBytes(bytes).Value)],
                []));
        }
    }

    private static Sha256Hash Hash(string value) =>
        HashBytes(Encoding.UTF8.GetBytes(value));

    private static Sha256Hash HashBytes(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static string Relative(WorkspacePath path) =>
        new AssetPath(Path.GetRelativePath(LabRoot.Value, path.Value)
            .Replace(Path.DirectorySeparatorChar, '/')).Value;

    private static string FormatDiagnostics(
        IEnumerable<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(item =>
            item.Code + "=" + item.Message));

    private static bool CanonicalDescriptorEquals(
        ExternalHeadPartDependencyDescriptor left,
        ExternalHeadPartDependencyDescriptor right) =>
        ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(left)
            .AsSpan().SequenceEqual(
                ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(right));

    private static bool CanonicalAttestationEquals(
        ExternalHeadPartFaceGeomExclusionAttestation left,
        ExternalHeadPartFaceGeomExclusionAttestation right) =>
        ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(left)
            .AsSpan().SequenceEqual(
                ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(right));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
