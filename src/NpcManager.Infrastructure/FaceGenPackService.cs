using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Materializes an explicit FaceGen pack plan into a new K-local Data package.
/// The source Data root is read-only; the package contains the anchor plugin and
/// canonical loose FaceGen providers with independent hashes. It is not a live
/// deployment or runtime-appearance claim.
/// </summary>
public sealed class FaceGenPackService(
    IFaceGenPackPlanService planner,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IFaceGenPackService
{
    private const long MaximumFileBytes = 512L * 1024 * 1024;
    private const long MaximumManifestBytes = 4L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<FaceGenPackResult> PackAsync(
        FaceGenPackRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRoots(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(request.OutputRoot, diagnostics);

        var planResult = await planner.PlanAsync(request.Plan, cancellationToken);
        diagnostics.AddRange(planResult.Diagnostics);
        if (!planResult.Planned || planResult.Artifact is null || HasErrors(diagnostics))
            return Refused(request.OutputRoot, diagnostics);

        var plan = planResult.Artifact;
        var sourceEntries = new List<(string RelativePath, string SourcePath, FaceGenPackArchiveRole Role)>();
        sourceEntries.Add((
            Path.Combine("Data", request.Plan.AnchorPlugin.Value),
            Path.Combine(request.Plan.DataRoot.Value, request.Plan.AnchorPlugin.Value),
            FaceGenPackArchiveRole.Main));
        foreach (var entry in plan.Entries.Where(item => item.Status == FaceGenPackSourceStatus.Present))
        {
            sourceEntries.Add((
                Path.Combine("Data", entry.CanonicalEntryPath.Value.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(request.Plan.DataRoot.Value,
                    entry.SourcePath.Value.Replace('/', Path.DirectorySeparatorChar)),
                entry.ArchiveRole));
        }

        var temporaryRoot = request.OutputRoot.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            var files = ImmutableArray.CreateBuilder<FaceGenPackFileArtifact>(sourceEntries.Count);
            foreach (var item in sourceEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(item.SourcePath);
                var destination = Path.GetFullPath(Path.Combine(temporaryRoot,
                    item.RelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)));
                if (!IsUnder(source, request.Plan.DataRoot.Value) || !IsUnder(destination, temporaryRoot))
                {
                    diagnostics.Add(new Diagnostic("facegen-pack-path-escape", DiagnosticSeverity.Error,
                        "A FaceGen package source or destination escaped its K-local root."));
                    return Refused(request.OutputRoot, diagnostics);
                }
                if (!File.Exists(source))
                {
                    diagnostics.Add(new Diagnostic("facegen-pack-source-missing", DiagnosticSeverity.Error,
                        $"FaceGen package source '{item.SourcePath}' does not exist."));
                    return Refused(request.OutputRoot, diagnostics);
                }
                if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("facegen-pack-source-reparse", DiagnosticSeverity.Error,
                        $"FaceGen package source '{item.SourcePath}' is a reparse point."));
                    return Refused(request.OutputRoot, diagnostics);
                }
                var sourceInfo = new FileInfo(source);
                if (sourceInfo.Length <= 0 || sourceInfo.Length > MaximumFileBytes)
                {
                    diagnostics.Add(new Diagnostic("facegen-pack-source-size", DiagnosticSeverity.Error,
                        $"FaceGen package source '{item.SourcePath}' is empty or exceeds the safety limit."));
                    return Refused(request.OutputRoot, diagnostics);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var hash = await CopyAndHashAsync(source, destination, cancellationToken);
                var sourceRelative = Path.GetRelativePath(request.Plan.DataRoot.Value, source)
                    .Replace(Path.DirectorySeparatorChar, '/');
                files.Add(new FaceGenPackFileArtifact(
                    item.RelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    Path.Combine("Data", sourceRelative).Replace(Path.DirectorySeparatorChar, '/'),
                    item.Role, sourceInfo.Length, hash));
            }

            var artifact = new FaceGenPackArtifact(
                "1", "facegen-pack", request.Plan.Edition.ToWireName(),
                plan.NpcFormId, plan.AnchorPlugin, request.OutputRoot.Value,
                files.ToImmutable(), true, false);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
            if (manifestBytes.LongLength > MaximumManifestBytes)
            {
                diagnostics.Add(new Diagnostic("facegen-pack-manifest-size", DiagnosticSeverity.Error,
                    "The FaceGen package manifest exceeds the safety limit."));
                return Refused(request.OutputRoot, diagnostics);
            }
            var manifestPath = Path.Combine(temporaryRoot, "facegen-pack.json");
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);
            var manifestHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(manifestBytes)));
            Directory.Move(temporaryRoot, request.OutputRoot.Value);
            temporaryRoot = string.Empty;
            return new FaceGenPackResult(true, request.OutputRoot, artifact, manifestHash,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-pack-write-failed", DiagnosticSeverity.Error, exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegen-pack-write-denied", DiagnosticSeverity.Error, exception.Message));
        }
        finally
        {
            if (temporaryRoot.Length > 0) TryDeleteDirectory(temporaryRoot);
        }
        return Refused(request.OutputRoot, diagnostics);
    }

    private void ValidateRoots(FaceGenPackRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.Plan.DataRoot));
        if (!request.OutputRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("facegen-pack-output-outside-lab", DiagnosticSeverity.Error,
                "FaceGen package output must remain under the K-only lab root."));
        var parent = Path.GetDirectoryName(request.OutputRoot.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("facegen-pack-output-parent-missing", DiagnosticSeverity.Error,
                "The FaceGen package output parent must already exist."));
        else
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.OutputRoot.Value) || Directory.Exists(request.OutputRoot.Value))
            diagnostics.Add(new Diagnostic("facegen-pack-output-exists", DiagnosticSeverity.Error,
                "FaceGen packages never overwrite an existing output."));
        if (request.OutputRoot.IsUnder(request.Plan.DataRoot) || request.Plan.DataRoot.IsUnder(request.OutputRoot))
            diagnostics.Add(new Diagnostic("facegen-pack-root-overlap", DiagnosticSeverity.Error,
                "FaceGen package output and source Data roots must be distinct and non-overlapping."));
    }

    private static async ValueTask<Sha256Hash> CopyAndHashAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.WriteThrough);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hasher.AppendData(buffer, 0, read);
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        return new Sha256Hash(Convert.ToHexString(hasher.GetHashAndReset()));
    }

    private static bool IsUnder(string path, string root) =>
        new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static FaceGenPackResult Refused(WorkspacePath output, ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, output, null, null, diagnostics.ToImmutable());

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
