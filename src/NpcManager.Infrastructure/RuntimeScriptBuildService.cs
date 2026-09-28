using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Collects reproducible PSC/PEX evidence and never installs or mutates game scripts.</summary>
public sealed class RuntimeScriptBuildService : IRuntimeScriptBuildService
{
    private readonly IWorkspacePolicy policy;
    private readonly WorkspacePath labRoot;
    private readonly IRuntimeScriptInspectorProcess inspectorProcess;
    private readonly TimeSpan inspectionTimeout;

    public RuntimeScriptBuildService(IWorkspacePolicy policy, WorkspacePath labRoot)
        : this(policy, labRoot, new RuntimeScriptInspectorProcess(), TimeSpan.FromMinutes(2))
    {
    }

    internal RuntimeScriptBuildService(IWorkspacePolicy policy, WorkspacePath labRoot,
        IRuntimeScriptInspectorProcess inspectorProcess, TimeSpan inspectionTimeout)
    {
        this.policy = policy;
        this.labRoot = labRoot;
        this.inspectorProcess = inspectorProcess;
        this.inspectionTimeout = inspectionTimeout > TimeSpan.Zero
            ? inspectionTimeout
            : throw new ArgumentOutOfRangeException(nameof(inspectionTimeout));
    }

    private static readonly (string RelativePath, long Length, string Sha256)[] CapricaIdentity =
    [
        (Path.Combine("tools", "manifests", "caprica-2024-06-14-patched.json"), 5_050,
            "17cb7b828d6906c4eedaef1f9d53867dab2ba4a593eb181d6e251a3828582119"),
        (Path.Combine("tools", "manifests", "caprica-2024-06-14-patched-sha256.csv"), 646,
            "4f787cfaaff3060f3b7db9f4ef07057bcda24eab1802a0eb6ea2592d4df51a92"),
        (Path.Combine("tools", "external", "caprica-2024-06-14-patched", "Caprica.exe"), 1_019_392,
            "c8013baf3350bf9b313a633f74997ce6a9a47c343e489f886bcde359369126f0"),
        (Path.Combine("tools", "external", "caprica-2024-06-14-patched",
            "boost_program_options-vc143-mt-x64-1_83.dll"), 347_136,
            "d091f8cf0a8a972d176ade2f0bc0b9079ef220a08f6b24f212d0962734b38da3"),
        (Path.Combine("tools", "external", "caprica-2024-06-14-patched", "fmt.dll"), 138_752,
            "cd84045080600dd697322927d62e0a7e7e48920f888b8fa1d4849e39d7927ada"),
        (Path.Combine("tools", "external", "caprica-2024-06-14-patched", "pugixml.dll"), 206_336,
            "69fb820528d9e420facc61441204a08e97c685b09d1ead600379d5c55aa14236"),
        (Path.Combine("tools", "external", "caprica-2024-06-14-patched", "TESV_Papyrus_Flags.flg"), 659,
            "ab61d098ef7a1402d8ee4d7bc77f26851d5380cd142ee4f3bf57ce41d0e7afa7")
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Regex PropertyPattern = new(@"^\s*([A-Za-z_][\w\[\]:]*)\s+Property\s+(\w+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async ValueTask<RuntimeScriptBuildResult> BuildAsync(RuntimeScriptBuildRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidatePaths(request, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(diagnostics);
        var script = request.Edition == GameEdition.Fallout4 ? "NPCM_Manolov_ApplyFO4" : "NPCM_Manolov_ApplySSE";
        var source = FindFile(request.SourceRoot.Value, "src_" + (request.Edition == GameEdition.Fallout4 ? "fo4" : "sse"), script + ".psc");
        var pex = FindFile(request.SourceRoot.Value, "pex_" + (request.Edition == GameEdition.Fallout4 ? "fo4" : "sse"), script + ".pex");
        if (source is null || pex is null)
        {
            diagnostics.Add(new Diagnostic("runtime-script-files-missing", DiagnosticSeverity.Error, "The pinned game-specific PSC and PEX pair is required."));
            return Refused(diagnostics);
        }
        var sourceText = await File.ReadAllTextAsync(source, cancellationToken);
        var sourceProperties = ParseSourceProperties(sourceText);
        if (sourceProperties.Length == 0) diagnostics.Add(new Diagnostic("runtime-script-source-invalid", DiagnosticSeverity.Error, "The PSC declares no typed properties."));
        var capricaManifest = new WorkspacePath(Path.Combine(labRoot.Value, "tools", "manifests", "caprica-2024-06-14-patched.json"));
        if (!File.Exists(capricaManifest.Value)) diagnostics.Add(new Diagnostic("runtime-script-compiler-manifest-missing", DiagnosticSeverity.Error, "The pinned Caprica manifest is missing."));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(diagnostics);

        var inspectionPath = request.Output.Value + ".pex-inspect.json";
        if (File.Exists(inspectionPath))
        {
            diagnostics.Add(new Diagnostic("runtime-script-inspection-exists", DiagnosticSeverity.Error, "The PEX inspection sidecar already exists; evidence writes never overwrite."));
            return Refused(diagnostics);
        }
        var evidenceWritten = false;
        try
        {
            var inspectionExit = await InspectPexAsync(pex, inspectionPath, diagnostics, cancellationToken);
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(diagnostics);
            if (inspectionExit != 0 || !File.Exists(inspectionPath))
            {
                diagnostics.Add(new Diagnostic("runtime-script-pex-inspect", DiagnosticSeverity.Error, $"Caprica pex-inspect failed with exit code {inspectionExit}."));
                return Refused(diagnostics);
            }
            using var inspection = JsonDocument.Parse(await File.ReadAllTextAsync(inspectionPath, cancellationToken));
            ParsedInspection parsed;
            try
            {
                parsed = ParseInspection(inspection.RootElement, request.Edition, script, sourceProperties, diagnostics);
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or JsonException)
            {
                diagnostics.Add(new Diagnostic("runtime-script-pex-inspection-malformed", DiagnosticSeverity.Error, exception.Message));
                return Refused(diagnostics);
            }
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(diagnostics);
            var sourceHash = await HashAsync(source, cancellationToken);
            var pexHash = await HashAsync(pex, cancellationToken);
            var inspectionHash = await HashAsync(inspectionPath, cancellationToken);
            var compilerHash = await HashAsync(capricaManifest.Value, cancellationToken);
            var flagsPath = FindFile(request.SourceRoot.Value, "", request.Edition == GameEdition.SkyrimSpecialEdition ? "TESV_Papyrus_Flags.flg" : "Institute_Papyrus_Flags.flg");
            var flagsStatus = flagsPath is null
                ? "not-present; precompiled-pex-inspection-only"
                : "present:" + (await HashAsync(flagsPath, cancellationToken)).Value;
            var artifact = new RuntimeScriptBuildArtifact("1", "npc-apply-script-build-evidence", request.Edition.ToWireName(), script,
                source, sourceHash.Value, pex, pexHash.Value, inspectionPath, inspectionHash.Value,
                capricaManifest.Value, compilerHash.Value, "precompiled-pex-inspection", flagsStatus,
                parsed.Dependencies, parsed.Properties, true, false, false);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
            var temporary = request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, request.Output.Value, overwrite: false);
                evidenceWritten = true;
                return new RuntimeScriptBuildResult(true, artifact,
                    new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("runtime-script-build-write-failed", DiagnosticSeverity.Error, exception.Message)); }
            catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("runtime-script-build-write-denied", DiagnosticSeverity.Error, exception.Message)); }
            finally { TryDelete(temporary); }
            return Refused(diagnostics, artifact);
        }
        finally { if (!evidenceWritten) TryDelete(inspectionPath); }
    }

    private void ValidatePaths(RuntimeScriptBuildRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.SourceRoot.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("runtime-script-source-outside-lab", DiagnosticSeverity.Error, "The source root must remain under the K-only lab root."));
        if (!Directory.Exists(request.SourceRoot.Value)) diagnostics.Add(new Diagnostic("runtime-script-source-missing", DiagnosticSeverity.Error, "The source root does not exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, request.SourceRoot));
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("runtime-script-output-outside-lab", DiagnosticSeverity.Error, "Output must remain under the K-only lab root."));
        if (!request.Output.Value.EndsWith(".runtime-script-build.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-build-extension", DiagnosticSeverity.Error, "Runtime-script evidence requires the .runtime-script-build.json extension."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("runtime-script-build-exists", DiagnosticSeverity.Error, "Runtime-script evidence never overwrites an existing artifact."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("runtime-script-build-parent-missing", DiagnosticSeverity.Error, "The evidence output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
    }

    private async Task<int> InspectPexAsync(string pex, string inspection,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(labRoot.Value, "tools", "external", "caprica-2024-06-14-patched", "Caprica.exe");
        var admittedFiles = new List<FileStream>();
        try
        {
            foreach (var identity in CapricaIdentity)
            {
                var path = Path.Combine(labRoot.Value, identity.RelativePath);
                var admitted = await OpenVerifiedIdentityAsync(path, labRoot.Value,
                    identity.Length, identity.Sha256, cancellationToken);
                if (admitted is not null)
                {
                    admittedFiles.Add(admitted);
                    continue;
                }
                diagnostics.Add(new Diagnostic("runtime-script-compiler-identity", DiagnosticSeverity.Error,
                    $"The pinned Caprica tool identity is missing or has drifted: {identity.RelativePath}."));
                return -1;
            }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("pex-inspect"); start.ArgumentList.Add("--input"); start.ArgumentList.Add(pex); start.ArgumentList.Add("--output"); start.ArgumentList.Add(inspection);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(inspectionTimeout);
            try
            {
                return await inspectorProcess.RunAsync(start, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add(new Diagnostic("runtime-script-compiler-timeout", DiagnosticSeverity.Error,
                    $"The pinned Caprica inspector exceeded {inspectionTimeout.TotalSeconds:0.###} seconds."));
                return -1;
            }
        }
        finally
        {
            // Keep the hashed files non-writable and non-replaceable until the process exits.
            foreach (var admitted in admittedFiles) admitted.Dispose();
        }
    }

    private static async ValueTask<FileStream?> OpenVerifiedIdentityAsync(string path, string admittedRoot, long length, string sha256,
        CancellationToken cancellationToken)
    {
        FileStream? stream = null;
        try
        {
            if (!HasAdmittedToolPath(path, admittedRoot)) return null;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == length &&
                string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)),
                    sha256, StringComparison.OrdinalIgnoreCase) &&
                HasAdmittedToolPath(path, admittedRoot))
            {
                var admitted = stream;
                stream = null;
                return admitted;
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private static bool HasAdmittedToolPath(string path, string admittedRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(admittedRoot));
        var current = Path.GetFullPath(path);
        if (!new WorkspacePath(current).IsUnder(new WorkspacePath(root))) return false;
        var attributes = new List<FileAttributes>();
        try
        {
            while (true)
            {
                attributes.Add(File.GetAttributes(current));
                if (string.Equals(Path.TrimEndingDirectorySeparator(current), root,
                        StringComparison.OrdinalIgnoreCase)) break;
                var parent = Directory.GetParent(current)?.FullName;
                if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return false;
                current = parent;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return IsAdmittedToolPathAttributes(attributes);
    }

    internal static bool IsAdmittedToolAttributes(FileAttributes attributes) =>
        !attributes.HasFlag(FileAttributes.ReparsePoint) && !attributes.HasFlag(FileAttributes.Device) &&
        !attributes.HasFlag(FileAttributes.Directory);

    internal static bool IsAdmittedToolPathAttributes(IReadOnlyList<FileAttributes> attributes) =>
        attributes.Count > 1 && IsAdmittedToolAttributes(attributes[0]) &&
        attributes.Skip(1).All(item => item.HasFlag(FileAttributes.Directory) &&
                                            !item.HasFlag(FileAttributes.ReparsePoint) &&
                                            !item.HasFlag(FileAttributes.Device));

    private static ParsedInspection ParseInspection(JsonElement root, GameEdition edition, string script,
        ImmutableArray<RuntimeScriptApiProperty> sourceProperties, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!root.TryGetProperty("valid", out var valid) || valid.ValueKind != JsonValueKind.True || !valid.GetBoolean()) diagnostics.Add(new Diagnostic("runtime-script-pex-invalid", DiagnosticSeverity.Error, "PEX inspection did not report a valid file."));
        var expectedGame = edition == GameEdition.Fallout4 ? "Fallout4" : "Skyrim";
        if (!root.TryGetProperty("game", out var game) || !string.Equals(game.GetString(), expectedGame, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-pex-game", DiagnosticSeverity.Error, "PEX game metadata does not match the requested edition."));
        var objects = root.TryGetProperty("objects", out var objectList) && objectList.ValueKind == JsonValueKind.Array ? objectList.EnumerateArray().ToArray() : [];
        var matchingObjects = objects.Where(item => item.TryGetProperty("name", out var name) && string.Equals(name.GetString(), script, StringComparison.Ordinal)).ToArray();
        if (matchingObjects.Length != 1) { diagnostics.Add(new Diagnostic("runtime-script-pex-script", DiagnosticSeverity.Error, "PEX must contain exactly one expected script object.")); return new ParsedInspection([], sourceProperties); }
        var scriptObject = matchingObjects[0];
        if (!scriptObject.TryGetProperty("parent", out var parent) || !string.Equals(parent.GetString(), "Actor", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-pex-parent", DiagnosticSeverity.Error, "The apply script must extend Actor."));
        var properties = scriptObject.TryGetProperty("properties", out var propertyList) && propertyList.ValueKind == JsonValueKind.Array
            ? propertyList.EnumerateArray().Select(item => new RuntimeScriptApiProperty(item.GetProperty("name").GetString() ?? "", NormalizePexType(item.GetProperty("type").GetString() ?? ""))).OrderBy(item => item.Name, StringComparer.Ordinal).ToImmutableArray()
            : ImmutableArray<RuntimeScriptApiProperty>.Empty;
        var source = sourceProperties.OrderBy(item => item.Name, StringComparer.Ordinal).ToImmutableArray();
        if (!properties.SequenceEqual(source)) diagnostics.Add(new Diagnostic("runtime-script-pex-api-mismatch", DiagnosticSeverity.Error, "PEX property API does not exactly match the PSC declaration."));
        var dependencies = root.TryGetProperty("apiDependencies", out var deps) && deps.ValueKind == JsonValueKind.Array
            ? deps.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).OrderBy(item => item, StringComparer.Ordinal).ToImmutableArray()
            : ImmutableArray<string>.Empty;
        return new ParsedInspection(dependencies, properties);
    }

    private static ImmutableArray<RuntimeScriptApiProperty> ParseSourceProperties(string text) => text.Split('\n').Select(line => PropertyPattern.Match(line)).Where(match => match.Success).Select(match => new RuntimeScriptApiProperty(match.Groups[2].Value, NormalizePscType(match.Groups[1].Value))).OrderBy(item => item.Name, StringComparer.Ordinal).ToImmutableArray();
    private static string NormalizePscType(string value) => value.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() switch { "bool" => "Bool", "int" => "Int", "float" => "Float", "string" => "String", "bool[]" => "Bool[]", "int[]" => "Int[]", "float[]" => "Float[]", "string[]" => "String[]", _ => value };
    private static string NormalizePexType(string value) => NormalizePscType(value);
    private static string? FindFile(string root, string directory, string file)
    {
        var candidates = directory.Length == 0
            ? new[] { Path.Combine(root, file), Path.Combine(root, "Papyrus", file) }
            : new[] { Path.Combine(root, directory, file), Path.Combine(root, "Papyrus", directory, file) };
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            var attributes = File.GetAttributes(candidate);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            return candidate;
        }
        return null;
    }
    private static async ValueTask<Sha256Hash> HashAsync(string path, CancellationToken cancellationToken) { await using var stream = File.OpenRead(path); return new Sha256Hash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))); }
    private static RuntimeScriptBuildResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics, RuntimeScriptBuildArtifact? artifact = null) => new(false, artifact, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private sealed record ParsedInspection(ImmutableArray<string> Dependencies, ImmutableArray<RuntimeScriptApiProperty> Properties);
}

internal interface IRuntimeScriptInspectorProcess
{
    ValueTask<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken);
}

internal sealed class RuntimeScriptInspectorProcess : IRuntimeScriptInspectorProcess
{
    public async ValueTask<int> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("Could not start the pinned Caprica inspector.");
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}
