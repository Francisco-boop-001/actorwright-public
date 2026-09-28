using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reads and validates the pinned BssliderSidecar JSON contract without applying any values.
/// Unknown fields and malformed values fail closed so a later writer cannot silently discard
/// appearance state. The canonical JSON hash proves a stable semantic round-trip in memory.
/// </summary>
public sealed partial class BodySidecarInspectionService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IBodySidecarInspectionService
{
    private const int CurrentSchemaVersion = 11;
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const int MaxNpcEntries = 10_000;
    private const int MaxMapEntries = 100_000;
    private const int MaxArrayEntries = 100_000;

    private static readonly ImmutableHashSet<string> RootFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "version", "plugin", "npcs");

    private static readonly ImmutableHashSet<string> EntryFields = ImmutableHashSet.Create(StringComparer.Ordinal,
        "editorId", "bodyMorphs", "bodyMorphsKeyed", "skinTemplateId", "gender", "overlays",
        "sseBodyOverlays", "sseNodeTransforms", "sseNodeScales", "sseHairColor", "sseSkinOverrides",
        "sseCustomMorphs", "sseSculpt", "sseSculptParts", "sseTintTextures");

    private static readonly ImmutableHashSet<string> OverlayFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "template", "priority", "tint", "offsetUV", "scaleUV");

    private static readonly ImmutableHashSet<string> SseOverlayFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "node", "diffuse", "normal", "tint", "alpha");

    private static readonly ImmutableHashSet<string> NodeTransformFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "node", "s", "sm", "p", "r");

    private static readonly ImmutableHashSet<string> SkinOverrideFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "slotMask", "diffuse", "normal", "tint");

    private static readonly ImmutableHashSet<string> CustomMorphFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "name", "value");

    private static readonly ImmutableHashSet<string> SculptFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "index", "dx", "dy", "dz");

    private static readonly ImmutableHashSet<string> SculptPartFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "host", "verts");

    private static readonly ImmutableHashSet<string> TintTextureFields =
        ImmutableHashSet.Create(StringComparer.Ordinal, "index", "texture");

    public async ValueTask<BodySidecarInspectResult> InspectAsync(
        BodySidecarInspectRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.File));
        if (!string.Equals(Path.GetExtension(request.File.Value), ".bssliders", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("body-sidecar-extension", DiagnosticSeverity.Error,
                "BodySlide sidecar files must use the .bssliders extension."));
        if (HasErrors(diagnostics)) return EmptyResult(request, diagnostics.ToImmutable());
        if (!File.Exists(request.File.Value))
        {
            diagnostics.Add(new Diagnostic("body-sidecar-file-missing", DiagnosticSeverity.Error,
                "The requested BodySlide sidecar does not exist."));
            return EmptyResult(request, diagnostics.ToImmutable());
        }
        if (ContainsReparsePoint(request.File.Value, labRoot.Value, diagnostics))
            return EmptyResult(request, diagnostics.ToImmutable());

        var info = new FileInfo(request.File.Value);
        if (info.Length > MaxFileBytes)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-size-limit", DiagnosticSeverity.Error,
                $"The BodySlide sidecar exceeds the {MaxFileBytes} byte safety limit."));
            return EmptyResult(request, diagnostics.ToImmutable());
        }

        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(request.File.Value, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("body-sidecar-read-failed", DiagnosticSeverity.Error,
                $"The BodySlide sidecar could not be read: {exception.Message}"));
            return EmptyResult(request, diagnostics.ToImmutable());
        }

        var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        using var document = ParseJson(bytes, diagnostics);
        if (document is null || HasErrors(diagnostics)) return EmptyResult(request, diagnostics.ToImmutable());
        ValidateJsonStructure(document.RootElement, "$", diagnostics);
        CheckKnownFields(document.RootElement, RootFields, "$", diagnostics);

        var version = ReadVersion(document.RootElement, diagnostics);
        var plugin = ReadPlugin(document.RootElement, request.File, diagnostics);
        var entries = ReadEntries(document.RootElement, request.Edition, diagnostics, cancellationToken);
        if (HasErrors(diagnostics) || plugin is null)
            return EmptyResult(request, diagnostics.ToImmutable());

        var canonical = Canonicalize(document.RootElement);
        var canonicalHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(canonical)));
        var roundTripPreserved = IsStableRoundTrip(canonical, diagnostics);
        if (!roundTripPreserved)
            diagnostics.Add(new Diagnostic("body-sidecar-roundtrip", DiagnosticSeverity.Error,
                "Canonical BodySlide sidecar JSON did not survive a second parse/write cycle."));
        if (HasErrors(diagnostics))
            return EmptyResult(request, diagnostics.ToImmutable());

        var summary = new BodySidecarDocumentSummary(version, plugin.Value, entries,
            sourceHash, canonicalHash, roundTripPreserved);
        return new BodySidecarInspectResult(request.Edition, request.File, summary, diagnostics.ToImmutable());
    }

    private static BodySidecarInspectResult EmptyResult(BodySidecarInspectRequest request,
        ImmutableArray<Diagnostic> diagnostics) =>
        new(request.Edition, request.File, null, diagnostics);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}
