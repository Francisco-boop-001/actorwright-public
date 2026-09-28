using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class SkyrimOutfitProductionLoadService(
    IOutfitChoiceService outfitChoiceService,
    ISkyrimOutfitItemCatalogReader itemCatalogReader) :
    ISkyrimOutfitProductionLoadService
{
    public async ValueTask<SkyrimOutfitProductionLoadResult> LoadAsync(
        SkyrimOutfitProductionLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ReviewedGameIntake intake = request.Intake;
        if (intake.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("outfit-production-edition",
                "A reviewed Skyrim SE/AE intake is required."));
        if (request.TemplateFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("outfit-production-template-form",
                "The template OTFT must use a nonzero plugin-local 24-bit FormID."));
        if (request.NewTargetFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("outfit-production-new-target",
                "The new OTFT target must use a nonzero plugin-local 24-bit FormID."));

        PluginClosureReviewEntry[] ordered = intake.Plugins
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded &&
                           item.SourceHash is not null)
            .OrderBy(item => item.Order)
            .ToArray();
        if (ordered.Length == 0)
            diagnostics.Add(Error("outfit-production-closure-empty",
                "The reviewed copied plugin closure is empty."));
        if (ordered.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Length)
            diagnostics.Add(Error("outfit-production-closure-duplicate",
                "The reviewed copied plugin closure contains duplicate plugin names."));

        foreach (PluginClosureReviewEntry entry in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (entry.SourceHash is null || HashFile(entry.Path.Value) != entry.SourceHash)
                    diagnostics.Add(Error("outfit-production-plugin-stale",
                        $"Reviewed plugin '{entry.Plugin}' changed after intake."));
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException)
            {
                diagnostics.Add(Error("outfit-production-plugin-read",
                    $"Reviewed plugin '{entry.Plugin}' could not be hashed: {exception.Message}"));
            }
        }

        PluginClosureReviewEntry? template = ordered.LastOrDefault(item =>
            string.Equals(item.Plugin.Value, request.TemplatePlugin.Value,
                StringComparison.OrdinalIgnoreCase));
        if (template is null)
            diagnostics.Add(Error("outfit-production-template-plugin",
                "The selected template plugin is not in the reviewed copied closure."));
        if (HasErrors(diagnostics)) return new(false, null, diagnostics.ToImmutable());

        ImmutableArray<PluginName> pluginOrder = ordered
            .Select(item => item.Plugin)
            .ToImmutableArray();
        OutfitChoiceSearchResult outfits = await outfitChoiceService.SearchAsync(
            new OutfitChoiceSearchRequest(
                GameEdition.SkyrimSpecialEdition,
                intake.DataRoot,
                null,
                pluginOrder),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(outfits.Diagnostics);
        SkyrimOutfitItemCatalogResult items = itemCatalogReader.Read(
            new SkyrimOutfitItemCatalogRequest(
                intake.DataRoot,
                pluginOrder,
                request.InitialPreviewSeed));
        diagnostics.AddRange(items.Diagnostics);
        OutfitChoiceCandidate[] templateMatches = outfits.Candidates.Where(item =>
            string.Equals(item.Plugin.Value, request.TemplatePlugin.Value,
                StringComparison.OrdinalIgnoreCase) &&
            item.FormId == request.TemplateFormId &&
            !item.IsDeleted).ToArray();
        if (templateMatches.Length != 1)
            diagnostics.Add(Error("outfit-production-template-outfit",
                "The selected winning template OTFT did not resolve exactly once."));
        if (!items.Accepted || HasErrors(diagnostics))
            return new(false, null, diagnostics.ToImmutable());

        var state = new SkyrimOutfitProductionState(
            intake,
            pluginOrder,
            template!.Path,
            template.SourceHash!.Value,
            request.TemplateFormId,
            request.NewTargetFormId,
            request.InitialPreviewSeed,
            outfits.Candidates,
            items.Items);
        return new(true, state, diagnostics.ToImmutable());
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
