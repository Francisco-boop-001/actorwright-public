using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reconstructs one complete immutable editor baseline only from a previously
/// reviewed copied Skyrim closure. Missing writer fields are refused rather
/// than synthesized.
/// </summary>
public sealed class SkyrimFaceEditLoadService(
    ISkyrimHeadPartEditLoadService headPartLoader,
    IFormChoiceService formChoiceService,
    ISkyrimRaceMenuPaintChoiceService paintChoiceService,
    ISkyrimFaceEditSourceReader sourceReader) : ISkyrimFaceEditLoadService
{
    public async ValueTask<SkyrimFaceEditLoadResult> LoadAsync(
        SkyrimFaceEditLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Intake.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error(
                "face-edit-edition",
                "A reviewed Skyrim SE/AE intake is required."));
            return Refused(diagnostics);
        }

        SkyrimHeadPartEditLoadResult headParts = await headPartLoader.LoadAsync(
            new SkyrimHeadPartEditLoadRequest(
                request.Intake,
                request.SourcePlugin,
                request.TargetFormId),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(headParts.Diagnostics);
        if (!headParts.Accepted || headParts.Snapshot is null)
            return Refused(diagnostics);

        SkyrimFaceEditSourceSnapshot source;
        try
        {
            source = sourceReader.Read(
                headParts.SourcePluginPath,
                request.TargetFormId);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           KeyNotFoundException)
        {
            diagnostics.Add(Error(
                "face-edit-source-read",
                $"The selected NPC appearance could not be read exactly: {exception.Message}"));
            return Refused(diagnostics);
        }

        ValidateCompleteness(source, diagnostics);
        ValidateHeadPartAgreement(headParts.Snapshot, source, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<PluginName> pluginOrder = request.Intake.Plugins
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded &&
                           item.SourceHash is not null)
            .OrderBy(item => item.Order)
            .Select(item => item.Plugin)
            .ToImmutableArray();
        FormChoiceSearchResult typedForms = await formChoiceService.SearchAsync(
            new FormChoiceSearchRequest(
                GameEdition.SkyrimSpecialEdition,
                request.Intake.DataRoot,
                [
                    new RecordSignature("CLFM"),
                    new RecordSignature("TXST"),
                    new RecordSignature("RACE")
                ],
                null,
                null,
                true,
                pluginOrder),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(typedForms.Diagnostics);
        FormChoiceCandidate[] races = typedForms.Candidates
            .Where(item => item.Signature == new RecordSignature("RACE") &&
                           item.FormId == source.Race.FormId &&
                           string.Equals(
                               item.Plugin.Value,
                               source.Race.Plugin.Value,
                               StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (races.Length != 1 || string.IsNullOrWhiteSpace(races[0].EditorId))
        {
            diagnostics.Add(Error(
                "face-edit-race-identity",
                "The selected NPC RACE must resolve to exactly one typed EditorID."));
        }

        var paints = ImmutableDictionary.CreateBuilder<
            SkyrimRaceMenuPaintCategory,
            SkyrimRaceMenuPaintChoiceResult>();
        foreach (SkyrimRaceMenuPaintCategory category in
                 Enum.GetValues<SkyrimRaceMenuPaintCategory>())
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
        }
        if (HasErrors(diagnostics) ||
            paints.Values.Any(item => !item.Accepted))
        {
            return Refused(diagnostics);
        }

        ImmutableArray<NpcHeadPartSelection> defaults = BuildRaceDefaults(
            headParts.Catalogs,
            diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimFaceMorphSnapshot morph = source.FaceMorphs;
        var morphPatch = new SkyrimFaceMorphPatch(
            morph.Nam9Sliders,
            morph.Nam9Trailing,
            morph.NamaValues);
        var document = new SkyrimFaceEditorDocument(
            new SkyrimFaceEditorParts(
                headParts.Snapshot.HeadParts,
                OptionalFormReference.Set(source.HairColor),
                source.HeadTexture,
                source.IsCharGenFacePreset),
            morphPatch,
            [],
            source.FaceTints.Layers
                .Select(item => new SkyrimFaceEditorTintLayer(
                    item,
                    true,
                    null,
                    null))
                .ToImmutableArray(),
            [],
            []);
        var appearance = new FullyAuthoredSkyrimNpcAppearanceSource(
            headParts.Snapshot.HeadParts
                .Select(item =>
                    (SkyrimNpcHeadPartSource)new ExternalSkyrimNpcHeadPart(item))
                .ToImmutableArray(),
            new ExternalSkyrimNpcHairColor(source.HairColor),
            new ExternalSkyrimNpcFaceTextureSet(source.HeadTexture!.Value),
            source.Weight!.Value,
            morphPatch,
            source.FaceTints,
            source.Qnam!.Value);
        var writerBaseline = new SkyrimFaceEditWriterBaseline(
            document,
            appearance,
            null);
        var catalogs = new SkyrimFaceEditCatalogs(
            typedForms,
            headParts.Catalogs,
            paints.ToImmutable(),
            null,
            defaults);
        var state = new SkyrimFaceEditLoadedState(
            headParts.SourcePluginPath,
            headParts.SourcePluginSha256,
            request.TargetFormId,
            source.SourceEditorId,
            source.Race,
            races[0].EditorId!,
            source.Sex,
            document,
            writerBaseline,
            catalogs);
        return new SkyrimFaceEditLoadResult(
            true,
            state,
            diagnostics.ToImmutable());
    }

    private static void ValidateCompleteness(
        SkyrimFaceEditSourceSnapshot source,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (source.HeadTexture is null)
            diagnostics.Add(Error(
                "face-edit-source-ftst-required",
                "The face-edit writer baseline requires an existing typed FTST reference."));
        if (source.Weight is null)
            diagnostics.Add(Error(
                "face-edit-source-weight-required",
                "The face-edit writer baseline requires exact NAM7 weight authority."));
        if (!source.FaceMorphs.HasNam9 || !source.FaceMorphs.HasNama)
            diagnostics.Add(Error(
                "face-edit-source-morph-required",
                "The face-edit writer baseline requires exact NAM9 and NAMA authority."));
        if (source.FaceTints.Layers.IsDefaultOrEmpty)
            diagnostics.Add(Error(
                "face-edit-source-tint-required",
                "The face-edit writer baseline requires at least one exact authored tint layer."));
        if (source.Qnam is null)
            diagnostics.Add(Error(
                "face-edit-source-qnam-required",
                "The face-edit writer baseline requires exact QNAM authority."));
        if (source.ScriptNames.Contains(
                SkyrimNpcApplySseContract.ScriptName,
                StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                "face-edit-source-runtime-payload-unsupported",
                "The selected NPC already owns an apply-script VMAD payload that this loader cannot yet reconstruct without loss."));
        }
    }

    private static void ValidateHeadPartAgreement(
        NpcFaceSnapshot headPartSnapshot,
        SkyrimFaceEditSourceSnapshot source,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (headPartSnapshot.Sex != source.Sex ||
            headPartSnapshot.Race != source.Race ||
            headPartSnapshot.HairColor != source.HairColor ||
            !headPartSnapshot.HeadParts.Select(item => item.Reference)
                .SequenceEqual(source.OrderedHeadParts))
        {
            diagnostics.Add(Error(
                "face-edit-source-disagreement",
                "The typed head-part route and complete source reader disagree on the selected NPC."));
        }
    }

    private static ImmutableArray<NpcHeadPartSelection> BuildRaceDefaults(
        ImmutableDictionary<NpcHeadPartType, SkyrimHeadPartChoiceResult> catalogs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var defaults = ImmutableArray.CreateBuilder<NpcHeadPartSelection>();
        foreach ((NpcHeadPartType type, SkyrimHeadPartChoiceResult catalog) in catalogs)
        {
            SkyrimHeadPartChoiceCandidate[] matches = catalog.Candidates
                .Where(item => item.RaceMatch == SkyrimHeadPartRaceMatchKind.RaceDefault)
                .ToArray();
            if (matches.Length > 1)
            {
                diagnostics.Add(Error(
                    "face-edit-race-default-ambiguous",
                    $"The reviewed race exposes multiple {type.ToWireName()} defaults."));
            }
            else if (matches.Length == 1 && type != NpcHeadPartType.Misc)
            {
                defaults.Add(new NpcHeadPartSelection(matches[0].Reference, type));
            }
        }
        return defaults.ToImmutable();
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimFaceEditLoadResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
