using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Converts reopened JSlot record authority into the closed inputs consumed by
/// the product-owned NPC and native FaceGen writers. It performs no I/O.
/// </summary>
public static class RaceMenuJslotCompanionAppearanceMapper
{
    private static readonly FormId HairColorFormId = new(0x0000_0801);
    private static readonly FormId TextureSetFormId = new(0x0000_0802);
    private static readonly FormId FaceHeadPartFormId = new(0x0000_0803);

    public static RaceMenuJslotCompanionAppearance Map(
        RaceMenuPresetRecordAuthorityDraft draft,
        float templateNam9Trailing,
        PluginName originatingPlugin,
        FormId npcFormId,
        SkyrimFaceGenSidecarAuthority? sourceAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Require(draft.Preset.IsValid &&
                draft.Preset.Format == PresetFormat.RaceMenuJslot &&
                draft.Preset.Edition == GameEdition.SkyrimSpecialEdition,
            "Companion mapping requires one valid Skyrim SE RaceMenu preset.");
        Require(!draft.RuntimeAuthority &&
                !draft.HeadTextureAuthority.RuntimeAuthority &&
                !draft.TintPlan.RaceAuthority.RuntimeAuthority,
            "Static companion inputs may not inherit runtime authority.");
        Require(float.IsFinite(templateNam9Trailing),
            "Template NAM9 trailing authority must be finite.");
        Require(npcFormId.Value != 0,
            "The temporary NPC FormID must be nonzero.");

        PresetAppearance source = draft.Preset.Appearance;
        RaceMenuPresetData raceMenu = source.RaceMenu ??
            throw new InvalidDataException("The preset has no RaceMenu payload.");
        Require(source.SliderMorphs.Length == 19 &&
                source.SliderMorphs.Take(18).All(float.IsFinite),
            "The preset must provide 18 finite NAM9 sliders plus its source sentinel.");
        Require(raceMenu.FaceMorphPresets.Length == 4,
            "The preset must provide exactly four NAMA values.");
        float weight = source.Weight?.Value ??
            throw new InvalidDataException("The preset has no actor weight.");
        Require(float.IsFinite(weight) && weight is >= 0F and <= 100F,
            "The preset actor weight must be finite and within 0-100.");

        ImmutableArray<SkyrimNpcHeadPartSource> headParts = draft.HeadParts
            .Select(item =>
            {
                NpcHeadPartType type = item.Binding.HeadPartType ??
                    throw new InvalidDataException(
                        $"Headpart '{item.Source.Identifier.Raw}' has no typed HDPT authority.");
                Require(item.Binding.Signature == new RecordSignature("HDPT"),
                    "Every companion headpart must be a provider-read HDPT.");
                return type == NpcHeadPartType.Face
                    ? (SkyrimNpcHeadPartSource)new OutputOwnedSkyrimNpcFaceHeadPart(
                        FaceHeadPartFormId, item.Binding.Reference)
                    : new ExternalSkyrimNpcHeadPart(item.Binding.Reference, type);
            })
            .ToImmutableArray();
        Require(headParts.Length == source.HeadParts.Length &&
                headParts.Count(item => item.Type == NpcHeadPartType.Face) == 1,
            "Companion mapping requires complete source-order headparts and exactly one Face HDPT.");

        (ImmutableArray<SkyrimFaceTintLayer> tintLayers,
                ImmutableArray<SkyrimNativeFaceTintMaskOverride> tintOverrides) =
            MapTints(draft.TintPlan);
        var qnam = draft.TintPlan.Qnam;
        Require(float.IsFinite(qnam.Red) && float.IsFinite(qnam.Green) &&
                float.IsFinite(qnam.Blue) &&
                qnam.Red is >= 0F and <= 1F &&
                qnam.Green is >= 0F and <= 1F &&
                qnam.Blue is >= 0F and <= 1F,
            "Companion QNAM authority must be finite normalized RGB.");

        var appearance = new FullyAuthoredSkyrimNpcAppearanceSource(
            headParts,
            new OutputOwnedSkyrimNpcHairColor(
                HairColorFormId, new SkyrimPackedRgb(draft.HairColorPackedRgb)),
            new OutputOwnedSkyrimNpcFaceTextureSet(
                TextureSetFormId, draft.HeadTextureAuthority.Paths),
            weight,
            new SkyrimFaceMorphPatch(
                source.SliderMorphs.Take(18).ToImmutableArray(),
                templateNam9Trailing,
                raceMenu.FaceMorphPresets),
            new SkyrimFaceTintPatch(tintLayers),
            new SkyrimQnamRgb(qnam.Red, qnam.Green, qnam.Blue));

        ImmutableArray<BodySidecarSculptPart> sculptParts = raceMenu.SculptParts
            .Select(item => new BodySidecarSculptPart(item.Host, item.Vertices))
            .ToImmutableArray();
        ImmutableArray<SkyrimFaceGenSidecarAuthority> sources =
            sourceAuthority is null ? [] : [sourceAuthority];
        var sidecar = new SkyrimFaceGenSidecarOverlay(
            originatingPlugin,
            npcFormId,
            source.OrderedCustomMorphs,
            ImmutableArray<RaceMenuSculptVertex>.Empty,
            sculptParts,
            tintOverrides,
            sources);
        return new RaceMenuJslotCompanionAppearance(appearance, sidecar);
    }

    private static (
        ImmutableArray<SkyrimFaceTintLayer> Layers,
        ImmutableArray<SkyrimNativeFaceTintMaskOverride> Overrides)
        MapTints(RaceMenuPresetTintAuthorityPlan plan)
    {
        var layers = ImmutableArray.CreateBuilder<SkyrimFaceTintLayer>();
        var overrides = ImmutableArray.CreateBuilder<SkyrimNativeFaceTintMaskOverride>();
        var usedTini = new HashSet<ushort>();
        foreach (RaceMenuPresetTintAuthorityDisposition disposition in plan.Dispositions)
        {
            byte alpha = (byte)(disposition.Source.Color >> 24);
            if (disposition.Kind == RaceMenuPresetTintAuthorityDispositionKind.Inactive)
            {
                Require(alpha == 0 && disposition.TiniIndex is null,
                    "Only an alpha-zero tint without TINI authority may be inactive.");
                continue;
            }
            Require(alpha > 0, "Every active companion tint must have nonzero alpha.");

            ushort tini;
            short tias;
            if (disposition.Kind == RaceMenuPresetTintAuthorityDispositionKind.MappedRecord)
            {
                tini = disposition.TiniIndex ??
                    throw new InvalidDataException("A mapped tint lost its TINI authority.");
                tias = disposition.TiasPresetIndex ??
                    throw new InvalidDataException("A mapped tint lost its TIAS authority.");
            }
            else
            {
                SkyrimRaceTintLayerAuthority raceLayer = plan.RaceAuthority.Layers
                    .SingleOrDefault(item => item.RaceOrder == disposition.Source.Index) ??
                    throw new InvalidDataException(
                        $"Baked JSlot tint {disposition.Source.Index} has no corresponding winning RACE row.");
                tini = raceLayer.Index;
                Require(plan.RaceAuthority.Layers.Count(item => item.Index == tini) == 1,
                    $"Baked JSlot tint {disposition.Source.Index} targets reused TINI {tini}, which cannot be authored unambiguously.");
                tias = -1;
                overrides.Add(new SkyrimNativeFaceTintMaskOverride(
                    tini, NormalizeTexture(disposition.Source.Texture)));
            }
            Require(usedTini.Add(tini),
                $"More than one active JSlot tint maps to TINI {tini}.");

            uint color = disposition.Source.Color;
            uint coverage = checked((uint)Math.Round(
                alpha * 100D / byte.MaxValue, MidpointRounding.AwayFromZero));
            layers.Add(new SkyrimFaceTintLayer(
                tini,
                (byte)((color >> 16) & 0xFF),
                (byte)((color >> 8) & 0xFF),
                (byte)(color & 0xFF),
                Alpha: 0,
                coverage,
                tias));
        }
        return (layers.ToImmutable(), overrides.ToImmutable());
    }

    private static AssetPath NormalizeTexture(string value)
    {
        Require(!string.IsNullOrWhiteSpace(value),
            "A baked tint requires one explicit custom mask texture.");
        string normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
            normalized = "textures/" + normalized;
        var path = new AssetPath(normalized);
        Require(path.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase),
            "A baked tint mask must be a DDS texture.");
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
