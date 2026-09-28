using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Purely maps an admitted RaceMenu plan into the existing typed NPC-creation
/// appearance payload. It does not write a plugin or establish standalone,
/// runtime, or visual authority. The caller must obtain the NAM9 trailing value
/// from the same hash-bound template admitted by the plan and must supply the
/// exact private texture paths that are separately hash-bound for packaging.
/// </summary>
public static class RaceMenuNpcCreationAppearanceMapper
{
    private const uint OutputOwnedHairColorLocalFormId = 0x0000_0801;

    public static FullyAuthoredSkyrimNpcAppearanceSource Map(
        RaceMenuNpcAppearancePlan plan,
        float templateNam9Trailing,
        SkyrimPrivateHeadTexturePaths privateHeadTextures,
        bool preserveQualifiedFaceEditorId = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(privateHeadTextures);
        Require(plan.IsReady, "RaceMenu appearance plan is not accepted and ready.");
        Require(plan.Request.Edition == GameEdition.SkyrimSpecialEdition &&
                plan.Provider.Edition == GameEdition.SkyrimSpecialEdition,
            "Only an admitted Skyrim SE appearance plan can be mapped.");
        Require(plan.Preset.SourceHash == plan.Request.PresetBundle.ExpectedPresetSha256,
            "The admitted preset hash no longer matches the typed request.");
        Require(float.IsFinite(templateNam9Trailing),
            "Template NAM9 trailing authority must be finite.");

        var source = plan.Preset.Appearance;
        var raceMenu = source.RaceMenu ??
            throw new InvalidDataException("The admitted plan has no typed RaceMenu payload.");
        Require(source.SliderMorphs.Length == 19,
            "RaceMenu morphs.default.morphs must contain 18 sliders plus one source sentinel.");
        var nam9 = source.SliderMorphs.Take(18).ToImmutableArray();
        Require(nam9.All(float.IsFinite), "The first 18 RaceMenu NAM9 sliders must be finite.");
        Require(raceMenu.FaceMorphPresets.Length == 4,
            "RaceMenu NAMA authority requires exactly four preset values.");

        var hairColor = MapHairColor(plan, source);
        var nextOwnedFormId = hairColor is OutputOwnedSkyrimNpcHairColor
            ? OutputOwnedHairColorLocalFormId + 1
            : OutputOwnedHairColorLocalFormId;
        var outputTextureSet = new OutputOwnedSkyrimNpcFaceTextureSet(
            new FormId(nextOwnedFormId), privateHeadTextures);
        var headParts = MapHeadParts(
            plan,
            source,
            new FormId(nextOwnedFormId + 1),
            preserveQualifiedFaceEditorId);
        var headTexture = plan.ResolvedHeadTexture ??
            throw new InvalidDataException("The admitted plan has no resolved TXST authority.");
        Require(headTexture.Signature == new RecordSignature("TXST") &&
                headTexture.HeadPartType is null,
            "Resolved head texture authority is not a typed TXST binding.");

        var weight = source.Weight?.Value ??
            throw new InvalidDataException("The admitted plan has no RaceMenu actor weight.");
        Require(float.IsFinite(weight) && weight is >= 0F and <= 100F &&
                weight == plan.Request.Stats.Weight,
            "RaceMenu appearance weight is invalid or inconsistent with NPC stats.");

        var tintLayers = MapTints(plan, source);
        var qnam = plan.QnamDerivation ??
            throw new InvalidDataException("The admitted mapped tints have no QNAM derivation.");
        Require(float.IsFinite(qnam.Red) && float.IsFinite(qnam.Green) &&
                float.IsFinite(qnam.Blue) && qnam.Red is >= 0F and <= 1F &&
                qnam.Green is >= 0F and <= 1F && qnam.Blue is >= 0F and <= 1F,
            "QNAM derivation is not a finite normalized RGB value.");

        return new FullyAuthoredSkyrimNpcAppearanceSource(
            headParts,
            hairColor,
            outputTextureSet,
            weight,
            new SkyrimFaceMorphPatch(nam9, templateNam9Trailing,
                raceMenu.FaceMorphPresets),
            new SkyrimFaceTintPatch(tintLayers),
            new SkyrimQnamRgb(qnam.Red, qnam.Green, qnam.Blue));
    }

    private static ImmutableArray<SkyrimNpcHeadPartSource> MapHeadParts(
        RaceMenuNpcAppearancePlan plan,
        PresetAppearance source,
        FormId outputFaceHeadPartFormId,
        bool preserveQualifiedFaceEditorId)
    {
        Require(plan.HeadPartDispositions.Length == source.HeadParts.Length,
            "Head-part disposition coverage is incomplete.");
        for (var index = 0; index < source.HeadParts.Length; index++)
        {
            Require(plan.HeadPartDispositions[index].Source == source.HeadParts[index],
                "Head-part dispositions do not preserve RaceMenu source order.");
        }

        var mapped = plan.HeadPartDispositions
            .Where(item => item.Kind == RaceMenuNpcHeadPartDispositionKind.MappedRecord)
            .Select(item => item.MappedHeadPart ??
                throw new InvalidDataException("A mapped head-part disposition lost its HDPT binding."))
            .ToImmutableArray();
        Require(mapped.SequenceEqual(plan.ResolvedHeadParts),
            "Resolved HDPT order is inconsistent with complete head-part dispositions.");
        Require(!mapped.IsDefaultOrEmpty,
            "A fully authored NPC requires at least one mapped HDPT record.");

        foreach (var disposition in plan.HeadPartDispositions)
        {
            if (disposition.Kind == RaceMenuNpcHeadPartDispositionKind.Baked)
            {
                Require(disposition.MappedHeadPart is null &&
                        disposition.FaceGeomSha256 == plan.CharGenFaceGeomSha256,
                    "Baked head-part authority is not bound to the admitted CharGen NIF.");
            }
        }

        var faceCount = mapped.Count(item => item.Binding.HeadPartType == NpcHeadPartType.Face);
        Require(faceCount == 1,
            "A standalone authored NPC requires exactly one provider-read Face HDPT carrier.");

        return mapped.Select(item =>
        {
            Require(item.Binding.Signature == new RecordSignature("HDPT"),
                "Mapped head-part authority lacks a provider-read HDPT type.");
            var headPartType = item.Binding.HeadPartType ??
                throw new InvalidDataException(
                    "Mapped head-part authority lacks a provider-read HDPT type.");
            return headPartType == NpcHeadPartType.Face
                ? (SkyrimNpcHeadPartSource)new OutputOwnedSkyrimNpcFaceHeadPart(
                    outputFaceHeadPartFormId, item.Binding.Reference)
                {
                    PreserveQualifiedEditorId = preserveQualifiedFaceEditorId
                }
                : new ExternalSkyrimNpcHeadPart(item.Binding.Reference, headPartType);
        }).ToImmutableArray();
    }

    private static SkyrimNpcHairColorSource MapHairColor(
        RaceMenuNpcAppearancePlan plan,
        PresetAppearance source)
    {
        var packed = source.HairColor?.PackedRgb ??
            throw new InvalidDataException("The admitted plan has no packed RaceMenu hair color.");
        return plan.ResolvedHairColor switch
        {
            RaceMenuNpcExternalHairColorAuthority external
                when external.PackedRgb == packed &&
                     external.Binding.Signature == new RecordSignature("CLFM") =>
                new ExternalSkyrimNpcHairColor(external.Binding.Reference),
            RaceMenuNpcOutputOwnedHairColorAuthority outputOwned
                when outputOwned.PackedRgb == packed &&
                     outputOwned.AllocatedLocalFormId.Value == OutputOwnedHairColorLocalFormId =>
                new OutputOwnedSkyrimNpcHairColor(outputOwned.AllocatedLocalFormId,
                    new SkyrimPackedRgb(outputOwned.PackedRgb)),
            _ => throw new InvalidDataException(
                "RaceMenu hair color lacks a consistent external or output-owned CLFM authority.")
        };
    }

    private static ImmutableArray<SkyrimFaceTintLayer> MapTints(
        RaceMenuNpcAppearancePlan plan,
        PresetAppearance source)
    {
        Require(plan.TintDispositions.Length == source.Tints.Length,
            "Tint disposition coverage is incomplete.");
        for (var index = 0; index < source.Tints.Length; index++)
        {
            var disposition = plan.TintDispositions[index];
            Require(disposition.Source == source.Tints[index],
                "Tint dispositions do not preserve RaceMenu source order.");
            var sourceAlpha = (byte)(disposition.Source.Color >> 24);
            if (disposition.Kind == RaceMenuNpcTintDispositionKind.Baked)
            {
                Require(disposition.MappedLayer is null && sourceAlpha > 0 &&
                        disposition.FaceGeomSha256 == plan.CharGenFaceGeomSha256 &&
                        disposition.FaceTintSha256 == plan.CharGenFaceTintSha256,
                    "Baked tint authority is inconsistent with the admitted CharGen pair.");
            }
            else if (disposition.Kind == RaceMenuNpcTintDispositionKind.Inactive)
            {
                Require(disposition.MappedLayer is null && sourceAlpha == 0,
                    "Only an alpha-zero source tint may be inactive.");
            }
        }

        var mapped = plan.TintDispositions
            .Where(item => item.Kind == RaceMenuNpcTintDispositionKind.MappedRecord)
            .Select(item => item.MappedLayer ??
                throw new InvalidDataException("A mapped tint disposition lost its TINI layer."))
            .ToImmutableArray();
        Require(mapped.SequenceEqual(plan.ResolvedTintLayers),
            "Resolved TINI layers are inconsistent with complete tint dispositions.");
        return mapped.Select(item => item.Layer).ToImmutableArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
