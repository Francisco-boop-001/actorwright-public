using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class SkyrimLeveledListProductionLoadService(
    ISkyrimOutfitItemCatalogReader itemCatalogReader) :
    ISkyrimLeveledListProductionLoadService
{
    public ValueTask<SkyrimLeveledListProductionLoadResult> LoadAsync(
        SkyrimLeveledListProductionLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ReviewedGameIntake intake = request.Intake;
        if (intake.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("leveled-list-production-edition",
                "A reviewed Skyrim SE/AE intake is required."));

        PluginClosureReviewEntry[] ordered = intake.Plugins
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded &&
                           item.SourceHash is not null)
            .OrderBy(item => item.Order)
            .ToArray();
        if (ordered.Length == 0)
            diagnostics.Add(Error("leveled-list-production-closure-empty",
                "The reviewed copied plugin closure is empty."));
        if (ordered.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Length)
            diagnostics.Add(Error("leveled-list-production-closure-duplicate",
                "The reviewed copied plugin closure contains duplicate plugin names."));

        foreach (PluginClosureReviewEntry entry in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry.SourceHash is null || HashFile(entry.Path.Value) != entry.SourceHash)
                    diagnostics.Add(Error("leveled-list-production-plugin-stale",
                        $"Reviewed plugin '{entry.Plugin}' changed after intake."));
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException)
            {
                diagnostics.Add(Error("leveled-list-production-plugin-read",
                    $"Reviewed plugin '{entry.Plugin}' could not be hashed: {exception.Message}"));
            }
        }
        if (HasErrors(diagnostics))
            return ValueTask.FromResult(new SkyrimLeveledListProductionLoadResult(
                false, null, diagnostics.ToImmutable()));

        ImmutableArray<PluginName> pluginOrder = ordered
            .Select(item => item.Plugin)
            .ToImmutableArray();
        SkyrimOutfitItemCatalogResult catalog = itemCatalogReader.Read(
            new SkyrimOutfitItemCatalogRequest(
                intake.DataRoot,
                pluginOrder,
                request.InitialPreviewSeed));
        diagnostics.AddRange(catalog.Diagnostics);
        if (!catalog.Accepted || HasErrors(diagnostics))
            return ValueTask.FromResult(new SkyrimLeveledListProductionLoadResult(
                false, null, diagnostics.ToImmutable()));

        var existingEditorIds = ImmutableArray.CreateBuilder<EditorId>();
        foreach (SkyrimOutfitEditorItem item in catalog.Items.Where(item =>
                     item.Kind == SkyrimOutfitEditorItemKind.LeveledList))
        {
            try { existingEditorIds.Add(new EditorId(item.DisplayName)); }
            catch (ArgumentException) { }
        }
        var state = new SkyrimLeveledListProductionState(
            intake,
            pluginOrder,
            catalog.Items,
            existingEditorIds.ToImmutable());
        return ValueTask.FromResult(new SkyrimLeveledListProductionLoadResult(
            true, state, diagnostics.ToImmutable()));
    }

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
