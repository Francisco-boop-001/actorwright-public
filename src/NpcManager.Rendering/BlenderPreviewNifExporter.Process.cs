using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using DesktopFailureOperationId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopFailureOperationId;
using DesktopProcessIdentityId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopProcessIdentityId;


namespace NpcManager.Rendering;

public sealed partial class BlenderPreviewNifExporter
{
    private static async ValueTask<ImmutableArray<ResolvedTri>> DiscoverTriFilesAsync(
        WorkspacePath source, WorkspacePath assetRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            source.Value[..^Path.GetExtension(source.Value).Length] + ".tri",
            Path.Combine(Path.GetDirectoryName(source.Value)!, Path.GetFileNameWithoutExtension(source.Value) + "chargen.tri")
        };
        var result = ImmutableArray.CreateBuilder<ResolvedTri>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            var tri = new WorkspacePath(candidate);
            if (!tri.IsUnder(assetRoot) || new FileInfo(candidate).Length is <= 0 or > MaximumAssetBytes)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-tri-invalid", DiagnosticSeverity.Error,
                    $"Adjacent TRI dependency '{candidate}' is outside the asset root or size bound."));
                continue;
            }
            AddReparseDiagnostic(diagnostics, candidate, "binary NIF TRI dependency");
            try
            {
                var hash = await HashFileAsync(tri, cancellationToken);
                result.Add(new ResolvedTri(candidate, hash.Value));
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-tri-read", DiagnosticSeverity.Error,
                    exception.Message));
            }
        }
        return result.ToImmutable();
    }

    private static bool MorphStatusMatches(RenderStatus status, ImmutableArray<PreviewNifExportMorph> morphs,
        ImmutableArray<ResolvedAsset>.Builder resolved)
    {
        var actualMorphs = status.Morphs.IsDefault ? ImmutableArray<PreviewNifExportMorph>.Empty : status.Morphs;
        if (!actualMorphs.SequenceEqual(morphs)) return false;
        var expectedTriFiles = resolved.SelectMany(item => item.TriFiles)
            .Select(item => new RenderDependency(item.Path, item.Sha256)).ToImmutableArray();
        var actualTriFiles = status.TriFiles.IsDefault ? ImmutableArray<RenderDependency>.Empty : status.TriFiles;
        if (!actualTriFiles.SequenceEqual(expectedTriFiles)) return false;
        if (morphs.IsDefaultOrEmpty)
            return !status.MorphDeformed && status.BaseVertexSha256 is null && status.BakedVertexSha256 is null;
        if (!status.MorphDeformed || string.IsNullOrWhiteSpace(status.BaseVertexSha256) ||
            string.IsNullOrWhiteSpace(status.BakedVertexSha256) ||
            string.Equals(status.BaseVertexSha256, status.BakedVertexSha256, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool HairZapStatusMatches(RenderStatus status, PreviewHairZapPlan? plan)
    {
        if (plan is null)
            return !status.HairZapApplied && !status.HairZapTop && !status.HairZapLong &&
                status.HairZapAffectedMeshCount == 0 && status.HairZapRemovedFaceCount == 0 &&
                !status.FaceCullApplied && status.FaceCullAffectedMeshCount == 0;
        if (status.HairZapTop != plan.TopCovered || status.HairZapLong != plan.LongCovered ||
            status.FaceCullApplied != plan.FaceGenHeadCovered ||
            (plan.FaceGenHeadCovered && status.FaceCullAffectedMeshCount <= 0))
            return false;
        return plan.Parts == PreviewHairZapParts.None ||
            (status.HairZapApplied && status.HairZapAffectedMeshCount > 0 && status.HairZapRemovedFaceCount > 0);
    }

    private async ValueTask<ProcessResult> RunBlenderAsync(string requestPath, string statusPath,
        CancellationToken cancellationToken)
    {
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
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("The Blender NIF exporter could not be started.");
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
        if (process.ExitCode != 0)
        {
            ActorwrightObservabilityEventSource.Log.RecordDesktopProcessFailure(
                Activity.Current,
                DesktopFailureOperationId.BlenderPreviewNifExport,
                DesktopProcessIdentityId.Blender,
                expectedExecutableSha256.Value,
                process.ExitCode,
                "preview-nif-binary-process");
            var details = string.Join(" ", new[] { output, error }
                .Where(text => !string.IsNullOrWhiteSpace(text))).Trim();
            if (details.Length > 4096) details = details[..4096];
            return new ProcessResult([new Diagnostic("preview-nif-binary-process", DiagnosticSeverity.Error,
                $"Blender exited with code {process.ExitCode}: {details}")]);
        }
        return new ProcessResult([]);
    }

    private static async ValueTask<RenderStatus?> ReadStatusAsync(string path,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumStatusBytes)
        {
            diagnostics.Add(new Diagnostic("preview-nif-binary-status-size", DiagnosticSeverity.Error,
                "The Blender NIF status file is outside the accepted size bound."));
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<RenderStatus>(
                await File.ReadAllBytesAsync(path, cancellationToken), JsonOptions);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("preview-nif-binary-status-json", DiagnosticSeverity.Error,
                exception.Message));
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
        if (!header.StartsWith("Gamebryo File Format, Version 20.2.0.7",
                StringComparison.Ordinal)) return false;
        return BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(newline + 1, sizeof(uint))) == 0x14020007;
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path.Value);
        return new Sha256Hash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
    }

}
