using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;


namespace NpcManager.Rendering;

public sealed partial class BlenderPreviewNifExporter
{
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
                    diagnostics.Add(new Diagnostic("preview-nif-binary-reparse", DiagnosticSeverity.Error,
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-path-inspection", DiagnosticSeverity.Error,
                    exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("preview-nif-binary-path-denied", DiagnosticSeverity.Error,
                    exception.Message));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static PreviewNifBinaryExportResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics, Sha256Hash? sceneHash = null) =>
        new(false, null, null, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record ProcessResult(ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ResolvedTri(string Path, string Sha256);

    private sealed record ResolvedAsset(string Path, string Category, string Sha256, ImmutableArray<ResolvedTri> TriFiles);

    private sealed record RenderDependency(string Path, string Sha256);

    private sealed record RenderStatus(bool Exported, string? Output, string? Sha256,
        long Bytes, string? TargetGame, int MeshCount, string? Error,
        string? ImportMode, ImmutableArray<PreviewNifExportMorph> Morphs,
        bool MorphDeformed, string? BaseVertexSha256, string? BakedVertexSha256,
        ImmutableArray<RenderDependency> TriFiles, int ArmatureCount = 0,
        string? DeformationMode = null,
        bool HairZapApplied = false, bool HairZapTop = false, bool HairZapLong = false,
        int HairZapAffectedMeshCount = 0, int HairZapRemovedFaceCount = 0,
        bool FaceCullApplied = false, int FaceCullAffectedMeshCount = 0);
}
