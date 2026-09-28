using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>
/// Runs the approved K-local TRI-to-NIF FaceGeom bake through Blender/PyNifly.
/// The service owns path/hash/process binding; the adapter owns mesh semantics.
/// </summary>
public sealed class BlenderFaceGeomNifExporter(
    WorkspacePath executablePath,
    WorkspacePath profileRoot,
    EmbeddedBlenderScriptId scriptId,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    Sha256Hash expectedExecutableSha256,
    INifGeometryReadbackService geometryReadback,
    Func<string, string, CancellationToken, ValueTask<ImmutableArray<Diagnostic>>>? processRunner = null) : IFaceGeomBinaryBuildService
{
    private const long MaximumAssetBytes = 256L * 1024 * 1024;
    private const long MaximumOutputBytes = 512L * 1024 * 1024;
    private const int MaximumMorphs = 2048;
    private const int MaximumStatusBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<FaceGeomBinaryBuildResult> BuildAsync(
        FaceGeomBinaryBuildRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ValidateRequest(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        Sha256Hash sourceHash;
        ImmutableArray<WorkspacePath> triFiles;
        try
        {
            sourceHash = await HashFileAsync(request.SourceNif, cancellationToken);
            triFiles = DiscoverTriFiles(request.SourceNif, diagnostics);
            foreach (var tri in triFiles)
                _ = await HashFileAsync(tri, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-source-read", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-source-access", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        ValidateMorphs(request.Morphs, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        NifGeometryReadbackResult sourceReadback = await geometryReadback.ReadAsync(
            new NifGeometryReadbackRequest(request.Edition, request.SourceNif,
                sourceHash), cancellationToken);
        diagnostics.AddRange(sourceReadback.Diagnostics);
        if (!sourceReadback.Accepted || sourceReadback.Document is null)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-source-geometry-readback",
                DiagnosticSeverity.Error,
                "The FaceGeom source NIF did not produce admitted independent geometry evidence."));
            return Refused(diagnostics);
        }
        NifGeometryReadbackDocument sourceGeometry = sourceReadback.Document;

        var triSources = ImmutableArray.CreateBuilder<FaceGeomBinarySource>();
        try
        {
            foreach (var tri in triFiles)
            {
                var hash = await HashFileAsync(tri, cancellationToken);
                triSources.Add(new FaceGeomBinarySource(
                    Path.GetRelativePath(request.AssetRoot.Value, tri.Value).Replace('\\', '/'), hash.Value));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-tri-read", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-tri-access", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }

        var parent = Path.GetDirectoryName(request.OutputPath.Value)!;
        var temporaryOutput = Path.Combine(parent, ".facegeom-nif-" + Guid.NewGuid().ToString("N") + ".nif");
        var requestPath = Path.Combine(parent, ".facegeom-nif-request-" + Guid.NewGuid().ToString("N") + ".json");
        var statusPath = Path.Combine(parent, ".facegeom-nif-status-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var payload = new
            {
                root = labRoot.Value,
                output = temporaryOutput,
                targetGame = request.Edition == GameEdition.Fallout4 ? "FO4" : "SKYRIMSE",
                source = Path.GetRelativePath(labRoot.Value, request.SourceNif.Value).Replace('\\', '/'),
                sourceSha256 = sourceHash.Value,
                triFiles = triFiles.Select(path => Path.GetRelativePath(labRoot.Value, path.Value).Replace('\\', '/'))
                    .ToImmutableArray(),
                morphs = request.Morphs
            };
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);

            var beforeHash = await HashFileAsync(executablePath, cancellationToken);
            if (beforeHash != expectedExecutableSha256)
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-tool-hash", DiagnosticSeverity.Error,
                    "The pinned Blender executable hash does not match its admitted manifest."));
                return Refused(diagnostics);
            }
            var processDiagnostics = await RunBlenderAsync(requestPath, statusPath, cancellationToken);
            var afterHash = await HashFileAsync(executablePath, cancellationToken);
            if (afterHash != expectedExecutableSha256)
                diagnostics.Add(new Diagnostic("facegeom-binary-tool-changed", DiagnosticSeverity.Error,
                    "The Blender executable changed while baking FaceGeom."));
            var status = await ReadStatusAsync(statusPath, diagnostics, cancellationToken);
            if (status is { Exported: false, ErrorCode: "facegeom-binary-tri-import" or "facegeom-binary-morph-missing" })
            {
                diagnostics.AddRange(processDiagnostics.Where(item => item.Code != "facegeom-binary-process"));
                diagnostics.Add(new Diagnostic(status.ErrorCode, DiagnosticSeverity.Error,
                    status.Error ?? "The bound TRI morph import failed."));
                return Refused(diagnostics);
            }
            diagnostics.AddRange(processDiagnostics);
            if (HasErrors(diagnostics))
            {
                if (status is { Exported: false, Error: { } error })
                    diagnostics.Add(new Diagnostic("facegeom-binary-export-failed", DiagnosticSeverity.Error, error));
                return Refused(diagnostics);
            }
            if (status is null || !status.Exported)
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-export-failed", DiagnosticSeverity.Error,
                    status?.Error ?? "The Blender FaceGeom adapter did not produce a successful status."));
                return Refused(diagnostics);
            }
            if (!string.Equals(status.Output, temporaryOutput, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(temporaryOutput))
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-output-binding", DiagnosticSeverity.Error,
                    "The FaceGeom status did not bind the temporary NIF output."));
                return Refused(diagnostics);
            }
            var outputInfo = new FileInfo(temporaryOutput);
            if (outputInfo.Length <= 32 || outputInfo.Length > MaximumOutputBytes)
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-output-size", DiagnosticSeverity.Error,
                    "The FaceGeom NIF size is outside the accepted bound."));
                return Refused(diagnostics);
            }
            var outputHash = await HashFileAsync(new WorkspacePath(temporaryOutput), cancellationToken);
            var expectedTriSources = triSources.ToImmutable();
            if (!string.Equals(status.Sha256, outputHash.Value, StringComparison.OrdinalIgnoreCase) ||
                status.Bytes != outputInfo.Length || status.VertexCount <= 0 ||
                !string.Equals(status.TargetGame,
                    request.Edition == GameEdition.Fallout4 ? "FO4" : "SKYRIMSE", StringComparison.OrdinalIgnoreCase) ||
                status.Source is null ||
                !string.Equals(status.Source.Path, request.SourceNif.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(status.Source.Sha256, sourceHash.Value, StringComparison.OrdinalIgnoreCase) ||
                status.TriFiles.Length != triFiles.Length ||
                status.TriFiles.Select((actual, index) =>
                    string.Equals(actual.Path, triFiles[index].Value, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(actual.Sha256, expectedTriSources[index].Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    .Any(matches => !matches) ||
                !status.Morphs.SequenceEqual(request.Morphs) ||
                status.BaseVertexSha256 is null || status.BakedVertexSha256 is null || status.ImportMode is null ||
                string.Equals(status.BaseVertexSha256, status.BakedVertexSha256, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-status-mismatch", DiagnosticSeverity.Error,
                    "The FaceGeom status does not match the requested source, morph bake, or output."));
                return Refused(diagnostics);
            }
            if (!await HasSupportedNifHeaderAsync(temporaryOutput, cancellationToken))
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-header-invalid", DiagnosticSeverity.Error,
                    "The FaceGeom output does not have the admitted Gamebryo 20.2.0.7 NIF header."));
                return Refused(diagnostics);
            }

            NifGeometryReadbackResult outputReadback = await geometryReadback.ReadAsync(
                new NifGeometryReadbackRequest(request.Edition,
                    new WorkspacePath(temporaryOutput), outputHash), cancellationToken);
            diagnostics.AddRange(outputReadback.Diagnostics);
            if (!outputReadback.Accepted || outputReadback.Document is null)
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-output-geometry-readback",
                    DiagnosticSeverity.Error,
                    "The FaceGeom temporary output did not produce admitted independent geometry evidence."));
                return Refused(diagnostics);
            }
            NifGeometryReadbackDocument outputGeometry = outputReadback.Document;
            int outputVertexCount = outputGeometry.Shapes.Sum(shape => shape.VertexCount);
            if (outputGeometry.Edition != request.Edition ||
                status.VertexCount != outputVertexCount ||
                !string.Equals(status.BaseVertexSha256,
                    sourceGeometry.AggregateGeometrySha256.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(status.BakedVertexSha256,
                    outputGeometry.AggregateGeometrySha256.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                outputGeometry.AggregateGeometrySha256 == sourceGeometry.AggregateGeometrySha256)
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-output-geometry-mismatch",
                    DiagnosticSeverity.Error,
                    "The FaceGeom writer geometry assertion does not match independently read output geometry."));
                return Refused(diagnostics);
            }

            File.Move(temporaryOutput, request.OutputPath.Value, overwrite: false);
            var artifact = new FaceGeomBinaryBuildArtifact(
                "1", "facegeom-binary-sandbox-build", request.Edition.ToWireName(), request.OutputPath.Value,
                outputHash.Value, outputInfo.Length, outputVertexCount, sourceHash.Value,
                triSources.ToImmutable(), request.Morphs, sourceGeometry.AggregateGeometrySha256.Value,
                outputGeometry.AggregateGeometrySha256.Value, status.ImportMode!, RuntimeAuthority: false);
            return new FaceGeomBinaryBuildResult(true, artifact, outputHash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-io", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-access", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        finally
        {
            TryDelete(temporaryOutput);
            TryDelete(requestPath);
            TryDelete(statusPath);
        }
    }

    private ImmutableArray<Diagnostic> ValidateRequest(FaceGeomBinaryBuildRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!executablePath.IsUnder(labRoot) || !File.Exists(executablePath.Value))
            diagnostics.Add(new Diagnostic("facegeom-binary-tool-invalid", DiagnosticSeverity.Error,
                "The pinned Blender executable must exist under the K-only lab root."));
        if (!profileRoot.IsUnder(labRoot) || !Directory.Exists(profileRoot.Value))
            diagnostics.Add(new Diagnostic("facegeom-binary-profile-invalid", DiagnosticSeverity.Error,
                "The staged Blender profile must exist under the K-only lab root."));
        if (!request.AssetRoot.IsUnder(labRoot) || !Directory.Exists(request.AssetRoot.Value))
            diagnostics.Add(new Diagnostic("facegeom-binary-root-invalid", DiagnosticSeverity.Error,
                "FaceGeom asset roots must be existing K-local directories."));
        else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.AssetRoot));
        if (!request.SourceNif.IsUnder(request.AssetRoot) || !File.Exists(request.SourceNif.Value) ||
            !request.SourceNif.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facegeom-binary-source-invalid", DiagnosticSeverity.Error,
                "FaceGeom source must be an existing NIF under the asset root."));
        else if (new FileInfo(request.SourceNif.Value).Length is <= 0 or > MaximumAssetBytes)
            diagnostics.Add(new Diagnostic("facegeom-binary-source-size", DiagnosticSeverity.Error,
                "FaceGeom source NIF is outside the accepted size bound."));
        if (!request.OutputPath.IsUnder(labRoot) || !request.OutputPath.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facegeom-binary-output-invalid", DiagnosticSeverity.Error,
                "FaceGeom output must be a new .nif under K."));
        var parent = Path.GetDirectoryName(request.OutputPath.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("facegeom-binary-output-parent", DiagnosticSeverity.Error,
                "FaceGeom output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputPath.Value))
            diagnostics.Add(new Diagnostic("facegeom-binary-output-exists", DiagnosticSeverity.Error,
                "FaceGeom outputs never overwrite existing files."));
        if (request.Morphs.Length is 0 or > MaximumMorphs)
            diagnostics.Add(new Diagnostic("facegeom-binary-morph-count", DiagnosticSeverity.Error,
                $"FaceGeom accepts 1..{MaximumMorphs} morphs."));
        AddReparseDiagnostic(diagnostics, executablePath.Value, "Blender executable");
        AddReparseDiagnostic(diagnostics, profileRoot.Value, "Blender profile");
        AddReparseDiagnostic(diagnostics, request.AssetRoot.Value, "FaceGeom asset root");
        AddReparseDiagnostic(diagnostics, request.SourceNif.Value, "FaceGeom source");
        AddReparseDiagnostic(diagnostics, request.OutputPath.Value, "FaceGeom output");
        return diagnostics.ToImmutable();
    }

    private static ImmutableArray<WorkspacePath> DiscoverTriFiles(
        WorkspacePath source, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var candidates = new[]
        {
            source.Value[..^Path.GetExtension(source.Value).Length] + ".tri",
            Path.Combine(Path.GetDirectoryName(source.Value)!, Path.GetFileNameWithoutExtension(source.Value) + "chargen.tri")
        };
        var result = candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new WorkspacePath(path)).ToImmutableArray();
        if (result.IsDefaultOrEmpty)
            diagnostics.Add(new Diagnostic("facegeom-binary-tri-missing", DiagnosticSeverity.Error,
                "FaceGeom source must have an adjacent .tri or chargen.tri dependency."));
        foreach (var path in result)
        {
            if (new FileInfo(path.Value).Length is <= 0 or > MaximumAssetBytes)
                diagnostics.Add(new Diagnostic("facegeom-binary-tri-size", DiagnosticSeverity.Error,
                    $"TRI dependency '{path.Value}' is outside the accepted size bound."));
            AddReparseDiagnostic(diagnostics, path.Value, "FaceGeom TRI dependency");
        }
        return result;
    }

    private static void ValidateMorphs(ImmutableArray<FaceGeomBinaryMorph> morphs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasNonZero = false;
        foreach (var morph in morphs)
        {
            if (string.IsNullOrWhiteSpace(morph.Name) || morph.Name.Length > 255 ||
                !float.IsFinite(morph.Value))
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-morph-invalid", DiagnosticSeverity.Error,
                    "FaceGeom morph names must be non-empty and values must be finite."));
                continue;
            }
            if (!names.Add(morph.Name))
                diagnostics.Add(new Diagnostic("facegeom-binary-morph-duplicate", DiagnosticSeverity.Error,
                    $"FaceGeom morph '{morph.Name}' occurs more than once."));
            if (morph.Value is < -1f or > 1f)
                diagnostics.Add(new Diagnostic("facegeom-binary-morph-extended-range", DiagnosticSeverity.Warning,
                    $"FaceGeom morph '{morph.Name}' uses extended value {morph.Value.ToString("R", CultureInfo.InvariantCulture)} outside -1..1; the exact value will be baked."));
            hasNonZero |= morph.Value != 0f;
        }
        if (!hasNonZero)
            diagnostics.Add(new Diagnostic("facegeom-binary-morph-zero", DiagnosticSeverity.Error,
                "At least one non-zero FaceGeom morph is required for a bake."));
    }

    private async ValueTask<ImmutableArray<Diagnostic>> RunBlenderAsync(
        string requestPath, string statusPath, CancellationToken cancellationToken)
    {
        if (processRunner is not null)
            return await processRunner(requestPath, statusPath, cancellationToken);

        EmbeddedBlenderInvocation invocation = EmbeddedBlenderInvocationFactory.Create(
            scriptId.Value, ["--request", requestPath, "--status", statusPath]);
        var start = new ProcessStartInfo
        {
            FileName = executablePath.Value,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in invocation.Arguments) start.ArgumentList.Add(argument);
        start.Environment["BLENDER_USER_CONFIG"] = Path.Combine(profileRoot.Value, "config");
        start.Environment["BLENDER_USER_SCRIPTS"] = Path.Combine(profileRoot.Value, "scripts");
        start.Environment["BLENDER_USER_DATA"] = Path.Combine(profileRoot.Value, "data");
        start.Environment["ACTORWRIGHT_WORKSPACE_ROOT"] = labRoot.Value;
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The Blender FaceGeom adapter could not be started.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(5));
        var token = budget.Token;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        await process.StandardInput.WriteAsync(invocation.StandardInput.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode == 0) return [];
        var details = string.Join(" ", new[] { output, error }
            .Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
        if (details.Length > 4096) details = details[..4096];
        return [new Diagnostic("facegeom-binary-process", DiagnosticSeverity.Error,
            $"Blender exited with code {process.ExitCode}: {details}")];
    }

    private static async ValueTask<RenderStatus?> ReadStatusAsync(string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        int diagnosticCount = diagnostics.Count;
        AddReparseDiagnostic(diagnostics, path, "FaceGeom status");
        if (diagnostics.Count != diagnosticCount) return null;
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumStatusBytes)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-status-size", DiagnosticSeverity.Error,
                "The FaceGeom status file is outside the accepted size bound."));
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<RenderStatus>(
                await File.ReadAllBytesAsync(path, cancellationToken), JsonOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-status-json", DiagnosticSeverity.Error, exception.Message));
            return null;
        }
    }

    private static async ValueTask<bool> HasSupportedNifHeaderAsync(string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var prefix = new byte[256];
        var count = await stream.ReadAsync(prefix, cancellationToken);
        var newline = Array.IndexOf(prefix, (byte)'\n', 0, count);
        if (newline < 0 || newline + 1 + sizeof(uint) > count) return false;
        var header = Encoding.UTF8.GetString(prefix, 0, newline);
        return header.StartsWith("Gamebryo File Format, Version 20.2.0.7", StringComparison.Ordinal) &&
            BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(newline + 1, sizeof(uint))) == 0x14020007;
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path.Value);
        return new Sha256Hash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics,
        string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("facegeom-binary-reparse", DiagnosticSeverity.Error,
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-path-inspection", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-path-denied", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static FaceGeomBinaryBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) => new(false, null, null, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record RenderStatus(bool Exported, string? Output, string? Sha256, long Bytes,
        string? TargetGame, int VertexCount, RenderSource? Source, ImmutableArray<RenderTri> TriFiles,
        ImmutableArray<FaceGeomBinaryMorph> Morphs,
        string? BaseVertexSha256, string? BakedVertexSha256, string? ImportMode, string? Error,
        string? ErrorCode = null);

    private sealed record RenderSource(string Path, string Sha256);
    private sealed record RenderTri(string Path, string Sha256);
}
