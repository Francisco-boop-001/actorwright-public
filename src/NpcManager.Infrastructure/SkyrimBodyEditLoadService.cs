using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reconstructs one exact NPC weight baseline from a reviewed copied closure.
/// Sidecar-only body sections start honestly empty because an NPC plugin does
/// not prove that any RaceMenu preset or runtime carrier belongs to the actor.
/// </summary>
public sealed class SkyrimBodyEditLoadService(
    ISkyrimRaceMenuPaintChoiceService paintChoiceService,
    ISkyrimBodyEditSourceReader sourceReader) : ISkyrimBodyEditLoadService
{
    private const int DefaultOverlaySlotLimit = 16;

    public async ValueTask<SkyrimBodyEditLoadResult> LoadAsync(
        SkyrimBodyEditLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Intake.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error(
                "body-edit-edition",
                "A reviewed Skyrim SE/AE intake is required."));
        }

        PluginClosureReviewEntry[] ordered = request.Intake.Plugins
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded &&
                           item.SourceHash is not null)
            .OrderBy(item => item.Order)
            .ToArray();
        PluginClosureReviewEntry? source = ordered.LastOrDefault(item =>
            string.Equals(
                item.Plugin.Value,
                request.SourcePlugin.Value,
                StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            diagnostics.Add(Error(
                "body-edit-source",
                "The selected source plugin is not in the reviewed copied closure."));
        }
        if (source?.SourceHash is null)
        {
            diagnostics.Add(Error(
                "body-edit-source-hash",
                "The selected source plugin has no reviewed SHA-256."));
        }

        SkyrimBodyEditSourceSnapshot? snapshot = null;
        if (!HasErrors(diagnostics))
        {
            try
            {
                Sha256Hash observed = HashFile(source!.Path.Value);
                if (observed != source.SourceHash)
                {
                    diagnostics.Add(Error(
                        "body-edit-source-changed",
                        "The selected source plugin changed after workspace review."));
                }
                else
                {
                    snapshot = sourceReader.Read(
                        source.Path,
                        request.TargetFormId);
                }
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               ArgumentException or
                                               KeyNotFoundException)
            {
                diagnostics.Add(Error(
                    "body-edit-source-read",
                    $"The selected NPC body baseline could not be read exactly: {exception.Message}"));
            }
        }
        if (snapshot?.Weight is null)
        {
            diagnostics.Add(Error(
                "body-edit-source-weight-required",
                "The body-edit writer baseline requires exact NAM7 weight authority."));
        }

        var paints = ImmutableDictionary.CreateBuilder<
            SkyrimRaceMenuPaintCategory,
            SkyrimRaceMenuPaintChoiceResult>();
        if (!HasErrors(diagnostics))
        {
            ImmutableArray<PluginName> pluginOrder = ordered
                .Select(item => item.Plugin)
                .ToImmutableArray();
            foreach (SkyrimRaceMenuPaintCategory category in BodyCategories())
            {
                SkyrimRaceMenuPaintChoiceResult result =
                    await paintChoiceService.SearchAsync(
                        new SkyrimRaceMenuPaintChoiceRequest(
                            request.Intake.DataRoot,
                            pluginOrder,
                            category,
                            null),
                        cancellationToken).ConfigureAwait(false);
                paints.Add(category, result);
                diagnostics.AddRange(result.Diagnostics);
                if (!result.Accepted)
                {
                    diagnostics.Add(Error(
                        "body-edit-paint-catalog-refused",
                        $"The reviewed {category.ToWireName()} paint catalog was refused."));
                }
            }
        }

        if (HasErrors(diagnostics) || snapshot?.Weight is null || source is null)
        {
            return new SkyrimBodyEditLoadResult(
                false,
                null,
                diagnostics.ToImmutable());
        }

        var document = new SkyrimBodyEditorDocument(
            snapshot.Weight.Value,
            [],
            [],
            [],
            []);
        var limits = ImmutableDictionary.CreateRange(
            Enum.GetValues<BodyOverlayTarget>().Select(target =>
                new KeyValuePair<BodyOverlayTarget, int>(
                    target,
                    DefaultOverlaySlotLimit)));
        var catalogs = new SkyrimBodyEditCatalogs(
            [],
            [],
            paints.ToImmutable(),
            limits);
        var state = new SkyrimBodyEditLoadedState(
            source.Path,
            source.SourceHash!.Value,
            request.TargetFormId,
            snapshot.SourceEditorId,
            document,
            new SkyrimBodyEditWriterBaseline(document),
            catalogs);
        return new SkyrimBodyEditLoadResult(
            true,
            state,
            diagnostics.ToImmutable());
    }

    private static IEnumerable<SkyrimRaceMenuPaintCategory> BodyCategories()
    {
        yield return SkyrimRaceMenuPaintCategory.Body;
        yield return SkyrimRaceMenuPaintCategory.Hands;
        yield return SkyrimRaceMenuPaintCategory.Feet;
    }

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
