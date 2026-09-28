using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>File-backed, typed equivalent of the pinned Copy/Paste Look merge.</summary>
public sealed class PresetCopyService(IPresetService presetService, IWorkspacePolicy policy, WorkspacePath labRoot) : IPresetCopyService
{
    public async ValueTask<PresetCopyResult> CopyAsync(PresetCopyRequest request, CancellationToken cancellationToken)
    {
        var source = await presetService.InspectAsync(request.Source, cancellationToken);
        var target = await presetService.InspectAsync(request.Target, cancellationToken);
        var sourceDocument = source.Document ?? PresetService.EmptyDocument(request.Source.Format, request.Source.Edition);
        var targetDocument = target.Document ?? PresetService.EmptyDocument(request.Target.Format, request.Target.Edition);
        var diagnostics = source.Diagnostics.AddRange(target.Diagnostics).ToBuilder();
        if (request.Source.Format != request.Target.Format || request.Source.Edition != request.Target.Edition)
            diagnostics.Add(new Diagnostic("preset-copy-format-mismatch", DiagnosticSeverity.Error,
                "Copy source and target must use the same preset format and game edition."));
        if (request.Sections.IsDefaultOrEmpty)
            diagnostics.Add(new Diagnostic("preset-copy-sections-empty", DiagnosticSeverity.Error,
                "At least one appearance section must be selected."));
        var distinctSections = request.Sections.Distinct().ToImmutableArray();
        if (distinctSections.Length != request.Sections.Length)
            diagnostics.Add(new Diagnostic("preset-copy-sections-duplicate", DiagnosticSeverity.Error,
                "Appearance sections must be unique."));
        var effective = CopyAppearance(sourceDocument.Appearance, targetDocument.Appearance,
            request.Target.Edition, distinctSections, diagnostics);
        diagnostics.AddRange(request.Source.Format == PresetFormat.LooksMenu
            ? LooksMenuPresetCodec.ValidateForWrite(effective)
            : request.Source.Format == PresetFormat.RaceMenuJslot
                ? RaceMenuJslotCodec.ValidateForWrite(effective)
                : ImmutableArray<Diagnostic>.Empty);
        diagnostics.AddRange(ValidateDestination(request.Target.SourcePath, request.Destination));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new PresetCopyResult(false, sourceDocument, targetDocument, null, diagnostics.ToImmutable());

        var bytes = request.Source.Format switch
        {
            PresetFormat.LooksMenu => LooksMenuPresetCodec.Write(effective),
            PresetFormat.RaceMenuJslot => RaceMenuJslotCodec.Write(effective),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Source.Format, "Unsupported preset format.")
        };
        var temporary = request.Destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            await using (var handle = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
                await handle.FlushAsync(cancellationToken);
            File.Move(temporary, request.Destination.Value, overwrite: false);
            var outputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            return new PresetCopyResult(true, sourceDocument, targetDocument with { Appearance = effective }, outputHash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            diagnostics.Add(new Diagnostic("preset-copy-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new PresetCopyResult(false, sourceDocument, targetDocument, null, diagnostics.ToImmutable());
        }
    }

    private static PresetAppearance CopyAppearance(PresetAppearance source, PresetAppearance target,
        GameEdition edition, ImmutableArray<PresetCopySection> sections,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = target;
        foreach (var section in sections)
        {
            switch (section)
            {
                case PresetCopySection.BodyWeight:
                    result = result with { Weight = source.Weight };
                    break;
                case PresetCopySection.BodyRegions:
                    if (edition != GameEdition.Fallout4) { UnsupportedCopySection(section, edition, diagnostics); break; }
                    result = result with { Fallout4BodyMorphs = source.Fallout4BodyMorphs };
                    break;
                case PresetCopySection.BodySliders:
                    result = edition == GameEdition.Fallout4
                        ? result with { BodyMorphs = source.BodyMorphs }
                        : result with
                        {
                            BodyMorphs = source.BodyMorphs,
                            RaceMenu = MergeRaceMenu(result.RaceMenu, source.RaceMenu,
                                (targetRace, sourceRace) => targetRace with
                                {
                                    BodyMorphsKeyed = sourceRace.BodyMorphsKeyed,
                                    NodeTransforms = sourceRace.NodeTransforms
                                })
                        };
                    break;
                case PresetCopySection.Overlays:
                    result = edition == GameEdition.Fallout4
                        ? result with { Overlays = source.Overlays }
                        : result with
                        {
                            Overlays = source.Overlays,
                            RaceMenu = MergeRaceMenu(result.RaceMenu, source.RaceMenu,
                                (targetRace, sourceRace) => targetRace with
                                {
                                    BodyOverlays = sourceRace.BodyOverlays,
                                    SkinOverrides = sourceRace.SkinOverrides
                                })
                        };
                    break;
                case PresetCopySection.SkinOverride:
                    diagnostics.Add(new Diagnostic("preset-copy-section-unsupported", DiagnosticSeverity.Error,
                        "NPC WNAM skin override copy requires an NPC record carrier and is not represented by a preset file."));
                    break;
                case PresetCopySection.LmSkinTemplate:
                    if (edition != GameEdition.Fallout4) { UnsupportedCopySection(section, edition, diagnostics); break; }
                    result = result with { Skin = source.Skin };
                    break;
                case PresetCopySection.Outfit:
                    diagnostics.Add(new Diagnostic("preset-copy-section-unsupported", DiagnosticSeverity.Error,
                        "Outfit copy requires NPC DOFT/SOFT record fields and is not represented by a preset file."));
                    break;
                case PresetCopySection.FaceParts:
                    result = result with
                    {
                        HeadParts = source.HeadParts,
                        RaceMenu = edition == GameEdition.SkyrimSpecialEdition
                            ? MergeRaceMenu(result.RaceMenu, source.RaceMenu,
                                (targetRace, sourceRace) => targetRace with
                                {
                                    // The pinned Skyrim paste carries FTST with
                                    // face parts only when the source has an
                                    // explicit override; absence preserves the
                                    // target's effective head texture.
                                    HeadTexture = sourceRace.HeadTexture ??
                                                  targetRace.HeadTexture
                                })
                            : result.RaceMenu
                    };
                    break;
                case PresetCopySection.HairColor:
                    result = result with { HairColor = source.HairColor };
                    break;
                case PresetCopySection.FaceTints:
                    result = result with
                    {
                        Tints = source.Tints,
                        RaceMenu = edition == GameEdition.SkyrimSpecialEdition
                            ? MergeRaceMenu(result.RaceMenu, source.RaceMenu,
                                (targetRace, sourceRace) => targetRace with
                                {
                                    FaceTextures = sourceRace.FaceTextures
                                })
                            : result.RaceMenu
                    };
                    break;
                case PresetCopySection.FaceVertexMorphs:
                    result = edition == GameEdition.Fallout4
                        ? result with { Morphs = source.Morphs, ChargenFaceMorphs = source.ChargenFaceMorphs }
                        : result with
                        {
                            Morphs = source.Morphs,
                            SliderMorphs = source.SliderMorphs,
                            CustomMorphs = source.CustomMorphs,
                            OrderedCustomMorphs = source.OrderedCustomMorphs,
                            RaceMenu = MergeRaceMenu(result.RaceMenu, source.RaceMenu,
                                (targetRace, sourceRace) => targetRace with { FaceMorphPresets = sourceRace.FaceMorphPresets })
                        };
                    break;
                case PresetCopySection.FaceBoneRegions:
                    if (edition != GameEdition.Fallout4) { UnsupportedCopySection(section, edition, diagnostics); break; }
                    result = result with { FaceBoneRegions = source.FaceBoneRegions, FacialMorphIntensity = source.FacialMorphIntensity };
                    break;
                case PresetCopySection.Sculpt:
                    if (edition != GameEdition.SkyrimSpecialEdition) { UnsupportedCopySection(section, edition, diagnostics); break; }
                    result = result with
                    {
                        RaceMenu = MergeRaceMenu(result.RaceMenu, source.RaceMenu,
                            (targetRace, sourceRace) => targetRace with
                            {
                                SculptDivisor = sourceRace.SculptDivisor,
                                SculptParts = sourceRace.SculptParts
                            })
                    };
                    break;
                case PresetCopySection.IsCharGenPreset:
                    diagnostics.Add(new Diagnostic("preset-copy-section-unsupported", DiagnosticSeverity.Error,
                        "The ACBS CharGen flag is an NPC record field and is not represented by a preset file."));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(sections), section, "Unsupported appearance section.");
            }
        }
        return result;
    }

    private static RaceMenuPresetData? MergeRaceMenu(RaceMenuPresetData? target, RaceMenuPresetData? source,
        Func<RaceMenuPresetData, RaceMenuPresetData, RaceMenuPresetData> merge)
    {
        if (source is null) return target;
        var targetValue = target ?? new RaceMenuPresetData(null, ImmutableArray<uint>.Empty, 10_000,
            ImmutableArray<RaceMenuSculptPart>.Empty,
            ImmutableDictionary<string, ImmutableDictionary<string, float>>.Empty,
            ImmutableArray<RaceMenuBodyOverlay>.Empty, ImmutableArray<SkyrimNodeTransform>.Empty,
            ImmutableArray<SkyrimSkinOverride>.Empty);
        return merge(targetValue, source);
    }

    private static void UnsupportedCopySection(PresetCopySection section, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Add(new Diagnostic("preset-copy-section-game-mismatch", DiagnosticSeverity.Error,
            $"Section '{section.ToWireName()}' is not available for {edition.ToWireName()}."));

    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath source, WorkspacePath destination)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!destination.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("preset-output-outside-lab", DiagnosticSeverity.Error, "Preset outputs must remain under the K-only lab root."));
        if (File.Exists(destination.Value)) diagnostics.Add(new Diagnostic("preset-output-exists", DiagnosticSeverity.Error, "Preset export never overwrites an existing artifact."));
        if (string.Equals(source.Value, destination.Value, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("preset-input-output-same", DiagnosticSeverity.Error, "Preset input and output must be different files."));
        var parent = Path.GetDirectoryName(destination.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("preset-output-parent-missing", DiagnosticSeverity.Error, "Preset output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
