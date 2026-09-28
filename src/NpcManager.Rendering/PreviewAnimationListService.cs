using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>
/// Provides deterministic, read-only animation discovery for the CLI. The
/// metadata mirrors the pinned picker taxonomy; it never inspects or executes
/// HKX behavior graphs and therefore does not claim runtime reachability.
/// </summary>
public sealed class PreviewAnimationListService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IPreviewAnimationListService
{
    public async ValueTask<PreviewAnimationListResult> ListAsync(
        PreviewAnimationListRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateManifest(request.ManifestPath).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        byte[] bytes;
        try
        {
            var info = new FileInfo(request.ManifestPath.Value);
            if (info.Length > PreviewSceneService.MaxBytes)
            {
                diagnostics.Add(new Diagnostic("animation-list-manifest-size-limit", DiagnosticSeverity.Error,
                    $"Animation manifests may not exceed {PreviewSceneService.MaxBytes} bytes."));
                return Refused(diagnostics);
            }
            bytes = await File.ReadAllBytesAsync(request.ManifestPath.Value, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("animation-list-manifest-read-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }

        var inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        ImmutableArray<PreviewAnimationClip> clips;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            PreviewSceneService.ValidateDuplicateProperties(document.RootElement, "$", diagnostics);
            ValidateEdition(document.RootElement, request.Edition, diagnostics);
            clips = PreviewAnimationCatalog.Read(document.RootElement, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new Diagnostic("animation-list-manifest-json-invalid", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        cancellationToken.ThrowIfCancellationRequested();

        string filter = request.Filter?.Trim() ?? string.Empty;
        ImmutableArray<PreviewAnimationListItem> allItems = clips
            .Select(ToItem)
            .ToImmutableArray();
        ImmutableArray<PreviewAnimationListItem> visible = allItems
            .Where(item => PreviewAnimationItemFilter.PassesGender(
                item, request.IsFemale, filterByGender: true))
            .Where(item => PreviewAnimationItemFilter.PassesPerspective(
                item, request.ShowFirstPerson))
            .Where(item => PreviewAnimationItemFilter.MatchesAll(item, filter))
            .OrderBy(item => string.IsNullOrEmpty(item.Category) ? 0 : 1)
            .ThenBy(item => RoleOrder(item.Roles.FirstOrDefault() ?? "Other"))
            .ThenBy(item => item.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ClipName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var artifact = new PreviewAnimationListArtifact("1", "preview-animation-list",
            request.Edition.ToWireName(), inputHash.Value, filter, request.IsFemale,
            request.ShowFirstPerson, clips.Length, visible.Length, visible);
        return new PreviewAnimationListResult(true, artifact, diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidateManifest(WorkspacePath manifest)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, manifest));
        if (!string.Equals(Path.GetExtension(manifest.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("animation-list-manifest-extension", DiagnosticSeverity.Error,
                "Animation list manifests must use the .json extension."));
        if (!File.Exists(manifest.Value))
            diagnostics.Add(new Diagnostic("animation-list-manifest-missing", DiagnosticSeverity.Error,
                "The animation list manifest does not exist."));
        else
        {
            try
            {
                if (File.GetAttributes(manifest.Value).HasFlag(FileAttributes.ReparsePoint))
                    diagnostics.Add(new Diagnostic("animation-list-manifest-reparse-refused", DiagnosticSeverity.Error,
                        "Animation list manifests may not be reparse points."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("animation-list-manifest-attributes-failed", DiagnosticSeverity.Error,
                    exception.Message));
            }
        }
        return diagnostics.ToImmutable();
    }

    private static void ValidateEdition(JsonElement root, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "edition", out var value)) return;
        if (value.ValueKind != JsonValueKind.String ||
            !GameEditionExtensions.TryParseWireName(value.GetString() ?? string.Empty, out var actual) ||
            actual != edition)
            diagnostics.Add(new Diagnostic("animation-list-edition-mismatch", DiagnosticSeverity.Error,
                $"Animation manifest edition must match {edition.ToWireName()}."));
    }

    private static PreviewAnimationListItem ToItem(PreviewAnimationClip clip) =>
        new(clip.Id, ClipDisplayName(clip), clip.Path.Value, clip.Skeleton.Value, clip.FrameCount,
            clip.FramesPerSecond, clip.Additive, clip.Roles.IsDefault ? [] : clip.Roles,
            clip.Category, clip.StateAxes, clip.RequiresFemale, clip.IsFirstPersonOnly,
            clip.FromBehaviorGraph, clip.Folder);

    private static string ClipDisplayName(PreviewAnimationClip clip) =>
        string.IsNullOrWhiteSpace(clip.ClipName)
            ? Path.GetFileNameWithoutExtension(clip.Path.Value)
            : clip.ClipName;

    private static int RoleOrder(string role) => role.ToLowerInvariant() switch
    {
        "core" => 0,
        "mt" => 1,
        "weapon" => 2,
        "furniture" => 3,
        "idle" => 4,
        "pipboy" => 5,
        _ => 9
    };

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static PreviewAnimationListResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
