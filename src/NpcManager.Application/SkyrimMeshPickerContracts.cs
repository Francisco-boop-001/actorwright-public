using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimMeshPickerCatalogRequest(
    GameEdition Edition,
    WorkspacePath DataRoot,
    AssetChoiceSearchResult SearchResult,
    string? InitialPath);

public sealed record SkyrimMeshPickerCandidate(
    AssetPath IndexedPath,
    AssetPath RelativePath,
    AssetChoiceProviderEvidence SelectedProvider,
    ImmutableArray<AssetChoiceProviderEvidence> Providers);

public sealed record SkyrimMeshPickerCatalogResult(
    bool Accepted,
    WorkspacePath DataRoot,
    ImmutableArray<SkyrimMeshPickerCandidate> Candidates,
    SkyrimMeshPickerCandidate? InitialCandidate,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record SkyrimMeshPickerSelection(
    bool Accepted,
    AssetPath? RelativePath,
    SkyrimMeshPickerCandidate? Candidate);

public sealed record SkyrimMeshPreviewRequest(
    WorkspacePath DataRoot,
    SkyrimMeshPickerCandidate Candidate,
    int Width = 512,
    int Height = 512);

public sealed record SkyrimMeshPreviewResult(
    bool Rendered,
    PreviewRenderedImage? Image,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISkyrimMeshPreviewService
{
    ValueTask<SkyrimMeshPreviewResult> RenderAsync(
        SkyrimMeshPreviewRequest request,
        CancellationToken cancellationToken);
}

public static class SkyrimMeshPickerRules
{
    private const string MeshPrefix = "meshes/";

    public static SkyrimMeshPickerCatalogResult BuildCatalog(
        SkyrimMeshPickerCatalogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(request.SearchResult.Diagnostics);
        if (request.Edition != GameEdition.SkyrimSpecialEdition ||
            request.SearchResult.Edition != request.Edition)
            diagnostics.Add(Error(
                "mesh-picker-edition",
                "The Skyrim mesh picker requires a Skyrim Special Edition asset catalog."));
        if (request.SearchResult.Kind != AssetChoiceKind.Mesh)
            diagnostics.Add(Error(
                "mesh-picker-kind",
                "The mesh picker requires an asset-choice mesh catalog."));
        if (request.SearchResult.Candidates.IsDefault)
            diagnostics.Add(Error(
                "mesh-picker-catalog-default",
                "The mesh catalog must be initialized."));
        if (HasErrors(diagnostics)) return Refused(request, diagnostics);

        var duplicate = request.SearchResult.Candidates
            .GroupBy(item => item.Path.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            diagnostics.Add(Error(
                "mesh-picker-path-duplicate",
                $"Mesh path '{duplicate.Key}' occurs more than once."));

        var candidates = ImmutableArray.CreateBuilder<SkyrimMeshPickerCandidate>();
        foreach (AssetChoiceCandidate source in request.SearchResult.Candidates)
        {
            if (source.Kind != AssetChoiceKind.Mesh ||
                !source.Path.Value.StartsWith(MeshPrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                source.Path.Value.Length <= MeshPrefix.Length ||
                !source.Path.Value.EndsWith(".nif",
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "mesh-picker-path-invalid",
                    $"Mesh candidate '{source.Path}' must be a .nif under meshes/."));
                continue;
            }
            if (source.Provider.Status != AssetChoiceProviderStatus.Resolved ||
                source.Provider.Evidence.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error(
                    "mesh-picker-provider-missing",
                    $"Mesh candidate '{source.Path}' has no resolved provider evidence."));
                continue;
            }
            if (source.Provider.Evidence.Any(item =>
                    item.Kind is not (AssetProviderKind.Loose or AssetProviderKind.Archive) ||
                    item.Size <= 0 ||
                    string.IsNullOrWhiteSpace(item.Source) ||
                    item.Source != item.Source.Trim() ||
                    item.Source.Any(char.IsControl)))
            {
                diagnostics.Add(Error(
                    "mesh-picker-provider-invalid",
                    $"Mesh candidate '{source.Path}' has malformed provider evidence."));
                continue;
            }

            candidates.Add(new SkyrimMeshPickerCandidate(
                source.Path,
                new AssetPath(source.Path.Value[MeshPrefix.Length..]),
                source.Provider.Evidence[0],
                source.Provider.Evidence));
        }
        if (HasErrors(diagnostics)) return Refused(request, diagnostics);

        ImmutableArray<SkyrimMeshPickerCandidate> ordered = candidates
            .OrderBy(item => item.IndexedPath.Value,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.IndexedPath.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        SkyrimMeshPickerCandidate? initial = ResolveInitial(
            request.InitialPath,
            ordered,
            diagnostics);
        return new SkyrimMeshPickerCatalogResult(
            true,
            request.DataRoot,
            ordered,
            initial,
            diagnostics.ToImmutable());
    }

    public static ImmutableArray<SkyrimMeshPickerCandidate> Filter(
        SkyrimMeshPickerCatalogResult catalog,
        string? search)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.Accepted || catalog.Candidates.IsDefault) return [];
        string query = search?.Trim() ?? string.Empty;
        if (query.Length == 0) return catalog.Candidates;
        return catalog.Candidates
            .Where(item => item.IndexedPath.Value.Contains(
                query,
                StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
    }

    public static SkyrimMeshPickerSelection Use(
        SkyrimMeshPickerCandidate? candidate) =>
        candidate is null
            ? Cancel()
            : new SkyrimMeshPickerSelection(
                true,
                candidate.RelativePath,
                candidate);

    public static SkyrimMeshPickerSelection Cancel() => new(false, null, null);

    private static SkyrimMeshPickerCandidate? ResolveInitial(
        string? initialPath,
        ImmutableArray<SkyrimMeshPickerCandidate> candidates,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string value = initialPath?.Trim().Replace('\\', '/') ?? string.Empty;
        if (value.Length == 0) return null;
        if (!value.StartsWith(MeshPrefix, StringComparison.OrdinalIgnoreCase))
            value = MeshPrefix + value;
        try
        {
            var path = new AssetPath(value);
            return candidates.FirstOrDefault(item => string.Equals(
                item.IndexedPath.Value,
                path.Value,
                StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic(
                "mesh-picker-initial-invalid",
                DiagnosticSeverity.Warning,
                $"The initial mesh path could not be preselected: {exception.Message}"));
            return null;
        }
    }

    private static SkyrimMeshPickerCatalogResult Refused(
        SkyrimMeshPickerCatalogRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, request.DataRoot, [], null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}
