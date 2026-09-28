using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>
/// Binds the reusable picker to one reviewed copied-workspace NPC. Catalogs are
/// rebuilt only from the intake's exact ascending plugin order.
/// </summary>
public sealed class SkyrimHeadPartEditLoadService(
    ISkyrimHeadPartChoiceService choiceService) : ISkyrimHeadPartEditLoadService
{
    public async ValueTask<SkyrimHeadPartEditLoadResult> LoadAsync(
        SkyrimHeadPartEditLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ReviewedGameIntake intake = request.Intake;
        if (intake.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("headpart-edit-edition", "A reviewed Skyrim SE/AE intake is required."));

        PluginClosureReviewEntry[] ordered = intake.Plugins
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded &&
                           item.SourceHash is not null)
            .OrderBy(item => item.Order)
            .ToArray();
        PluginClosureReviewEntry? source = ordered.LastOrDefault(item =>
            string.Equals(item.Plugin.Value, request.SourcePlugin.Value,
                StringComparison.OrdinalIgnoreCase));
        if (source is null)
            diagnostics.Add(Error("headpart-edit-source", "The selected source plugin is not in the reviewed copied closure."));
        if (source?.SourceHash is not { } sourceHash)
            diagnostics.Add(Error("headpart-edit-source-hash", "The selected source plugin has no reviewed SHA-256."));

        NpcFaceSnapshot? snapshot = null;
        if (!HasErrors(diagnostics))
        {
            try
            {
                Sha256Hash observed = HashFile(source!.Path.Value);
                if (observed != source.SourceHash)
                    diagnostics.Add(Error("headpart-edit-source-changed", "The selected source plugin changed after workspace review."));
                else
                    snapshot = BethesdaNpcFaceAdapter.Read(
                        GameEdition.SkyrimSpecialEdition,
                        source.Path,
                        request.TargetFormId);
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               ArgumentException or
                                               KeyNotFoundException)
            {
                diagnostics.Add(Error("headpart-edit-read", $"The selected NPC face baseline could not be read: {exception.Message}"));
            }
        }
        if (snapshot?.Race is null)
            diagnostics.Add(Error("headpart-edit-race", "The selected NPC has no typed RACE reference."));

        var catalogs = ImmutableDictionary.CreateBuilder<NpcHeadPartType, SkyrimHeadPartChoiceResult>();
        if (!HasErrors(diagnostics))
        {
            ImmutableArray<PluginName> pluginOrder = ordered
                .Select(item => item.Plugin)
                .ToImmutableArray();
            foreach (NpcHeadPartType type in Enum.GetValues<NpcHeadPartType>())
            {
                SkyrimHeadPartChoiceResult result = await choiceService.SearchAsync(
                    new SkyrimHeadPartChoiceRequest(
                        intake.DataRoot,
                        pluginOrder,
                        snapshot!.Race!.Value,
                        snapshot.Sex,
                        type,
                        null),
                    cancellationToken).ConfigureAwait(false);
                catalogs.Add(type, result);
                diagnostics.AddRange(result.Diagnostics.Where(item =>
                    item.Severity == DiagnosticSeverity.Error));
            }

            var typeByReference = new Dictionary<FormReference, NpcHeadPartType>();
            foreach (SkyrimHeadPartChoiceCandidate candidate in catalogs.Values
                         .SelectMany(result => result.Candidates))
            {
                if (typeByReference.TryGetValue(candidate.Reference, out NpcHeadPartType existing) &&
                    existing != candidate.Type)
                {
                    diagnostics.Add(Error(
                        "headpart-edit-type-ambiguous",
                        $"HDPT {candidate.Reference} appeared in both {existing.ToWireName()} and {candidate.Type.ToWireName()} catalogs."));
                    continue;
                }
                typeByReference[candidate.Reference] = candidate.Type;
            }
            snapshot = snapshot! with
            {
                HeadParts = snapshot.HeadParts.Select(selection =>
                    typeByReference.TryGetValue(selection.Reference, out NpcHeadPartType type)
                        ? selection with { Type = type }
                        : selection).ToImmutableArray()
            };
        }

        bool accepted = snapshot is not null && !HasErrors(diagnostics) &&
                        catalogs.Count == Enum.GetValues<NpcHeadPartType>().Length;
        return new SkyrimHeadPartEditLoadResult(
            accepted,
            source?.Path ?? intake.DataRoot,
            source?.SourceHash ?? new Sha256Hash(new string('0', 64)),
            snapshot,
            catalogs.ToImmutable(),
            diagnostics.ToImmutable());
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
