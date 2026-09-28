using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class BodySidecarWriteService(IWorkspacePolicy policy, WorkspacePath labRoot) : IBodySidecarWriteService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async ValueTask<BodySidecarWriteResult> WriteAsync(BodySidecarWriteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Failed(request, diagnostics.ToImmutable());
        var rootName = request.Edition == GameEdition.Fallout4 ? "Fallout4.esm" : "Skyrim.esm";
        var document = new SidecarDocument(request.Edition == GameEdition.Fallout4 ? 1 : 11, request.Plugin.Value,
            new Dictionary<string, SidecarNpc>(StringComparer.Ordinal)
            {
                [$"{rootName}|{request.NpcFormId.Value:X6}"] = new SidecarNpc(request.EditorId?.Value, request.BodyMorphs)
            });
        var temporary = request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, request.Output.Value, overwrite: false);
            return new BodySidecarWriteResult(true, request.Edition, request.Plugin, request.NpcFormId, request.Output,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception) { diagnostics.Add(new Diagnostic("body-sidecar-output-write-failed", DiagnosticSeverity.Error, exception.Message)); }
        catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("body-sidecar-output-write-denied", DiagnosticSeverity.Error, exception.Message)); }
        finally { TryDelete(temporary); }
        return Failed(request, diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> Validate(BodySidecarWriteRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.NpcFormId.Value == 0 || request.NpcFormId.Value > 0x00FF_FFFF)
            diagnostics.Add(new Diagnostic("body-sidecar-formid-invalid", DiagnosticSeverity.Error, "NPC FormID must be a nonzero plugin-local 24-bit value."));
        if (!request.Output.Value.EndsWith(".bssliders", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("body-sidecar-extension-invalid", DiagnosticSeverity.Error, "BodySlide sidecar output must use the .bssliders extension."));
        if (!string.Equals(Path.GetFileNameWithoutExtension(request.Output.Value), Path.GetFileNameWithoutExtension(request.Plugin.Value), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("body-sidecar-plugin-path", DiagnosticSeverity.Error, "Sidecar filename must match the plugin filename."));
        if (request.BodyMorphs.Count == 0 || request.BodyMorphs.Count > 512)
            diagnostics.Add(new Diagnostic("body-sidecar-morphs-invalid", DiagnosticSeverity.Error, "At least one and at most 512 body morphs are required."));
        if (request.BodyMorphs.Keys.Any(name => string.IsNullOrWhiteSpace(name) || name.Contains('\0') || name.Contains(':')))
            diagnostics.Add(new Diagnostic("body-sidecar-morph-name-invalid", DiagnosticSeverity.Error, "Body morph names must be non-empty and delimiter-safe."));
        if (request.BodyMorphs.Values.Any(value => !float.IsFinite(value) || value is < -1f or > 1f))
            diagnostics.Add(new Diagnostic("body-sidecar-morph-value-invalid", DiagnosticSeverity.Error, "Body morph values must be finite and in the range -1 through 1."));
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("body-sidecar-output-outside-lab", DiagnosticSeverity.Error, "Output must remain under the K-only lab root."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null) diagnostics.Add(new Diagnostic("body-sidecar-output-parent-invalid", DiagnosticSeverity.Error, "Output has no parent directory."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("body-sidecar-output-exists", DiagnosticSeverity.Error, "Output already exists; sidecar writes never overwrite."));
        if (request.BodyMorphs.Keys.Count() != request.BodyMorphs.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()) diagnostics.Add(new Diagnostic("body-sidecar-morph-duplicate", DiagnosticSeverity.Error, "Body morph names must be unique case-insensitively."));
        return diagnostics.ToImmutable();
    }

    private static BodySidecarWriteResult Failed(BodySidecarWriteRequest request, ImmutableArray<Diagnostic> diagnostics) =>
        new(false, request.Edition, request.Plugin, request.NpcFormId, request.Output, null, diagnostics);

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private sealed record SidecarDocument(int Version, string Plugin, Dictionary<string, SidecarNpc> Npcs);
    private sealed record SidecarNpc([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EditorId,
        ImmutableDictionary<string, float> BodyMorphs);
}
