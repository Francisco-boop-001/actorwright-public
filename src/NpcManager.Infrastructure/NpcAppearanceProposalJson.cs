using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Stable, explicit JSON projection for the closed Skyrim appearance union.
/// Keeping this projection separate from the runtime records prevents
/// System.Text.Json from flattening derived CLFM, TXST, and HDPT payloads.
/// </summary>
internal static class NpcAppearanceProposalJson
{
    internal static FullyAuthoredDocument Create(
        FullyAuthoredSkyrimNpcAppearanceSource appearance)
    {
        if (appearance.OrderedHeadParts.IsDefault || appearance.HairColor is null ||
            appearance.FaceTextureSet is null || appearance.FaceMorphs is null ||
            appearance.FaceTints is null || appearance.FaceTints.Layers.IsDefault ||
            appearance.OrderedHeadParts.Any(item =>
                item is null || !Enum.IsDefined(item.Type)))
        {
            throw new JsonException(
                "The fully authored Skyrim appearance is structurally invalid.");
        }

        return new FullyAuthoredDocument(
            appearance.OrderedHeadParts.Select(CreateHeadPart).ToImmutableArray(),
            CreateHairColor(appearance.HairColor),
            CreateFaceTextureSet(appearance.FaceTextureSet),
            appearance.Weight,
            new FaceMorphDocument(
                appearance.FaceMorphs.Nam9Sliders,
                appearance.FaceMorphs.Nam9Trailing,
                appearance.FaceMorphs.NamaValues),
            new FaceTintDocument(appearance.FaceTints.Layers.Select(item =>
                new FaceTintLayerDocument(
                    item.Index,
                    item.Red,
                    item.Green,
                    item.Blue,
                    item.Alpha,
                    item.Coverage,
                    item.PresetIndex)).ToImmutableArray()),
            new QnamDocument(
                appearance.Qnam.Red,
                appearance.Qnam.Green,
                appearance.Qnam.Blue),
            appearance.ExposedOutfitSkinBinding is null
                ? null
                : CreateOutfitSkinBinding(
                    appearance.ExposedOutfitSkinBinding),
            appearance.NakedSkinBinding is null
                ? null
                : CreateNakedSkinBinding(
                    appearance.NakedSkinBinding));
    }

    internal static FullyAuthoredSkyrimNpcAppearanceSource Parse(
        FullyAuthoredDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.OrderedHeadParts.IsDefault || document.HairColor is null ||
            document.FaceTextureSet is null || document.FaceMorphs is null ||
            document.FaceTints is null || document.FaceTints.Layers.IsDefault ||
            document.Qnam is null)
        {
            throw new JsonException(
                "The persisted Skyrim appearance is structurally incomplete.");
        }

        return new FullyAuthoredSkyrimNpcAppearanceSource(
            document.OrderedHeadParts.Select(ParseHeadPart).ToImmutableArray(),
            ParseHairColor(document.HairColor),
            ParseFaceTextureSet(document.FaceTextureSet),
            document.Weight,
            new SkyrimFaceMorphPatch(
                document.FaceMorphs.Nam9Sliders,
                document.FaceMorphs.Nam9Trailing,
                document.FaceMorphs.NamaValues),
            new SkyrimFaceTintPatch(document.FaceTints.Layers.Select(item =>
                new SkyrimFaceTintLayer(
                    item.Index,
                    item.Red,
                    item.Green,
                    item.Blue,
                    item.Alpha,
                    item.Coverage,
                    item.PresetIndex)).ToImmutableArray()),
            new SkyrimQnamRgb(
                document.Qnam.Red,
                document.Qnam.Green,
                document.Qnam.Blue))
        {
            ExposedOutfitSkinBinding =
                document.ExposedOutfitSkinBinding is null
                    ? null
                    : ParseOutfitSkinBinding(
                        document.ExposedOutfitSkinBinding),
            NakedSkinBinding =
                document.NakedSkinBinding is null
                    ? null
                    : ParseNakedSkinBinding(
                        document.NakedSkinBinding)
        };
    }

    private static SkyrimNpcHeadPartSource ParseHeadPart(
        HeadPartDocument document)
    {
        if (document is null ||
            !NpcHeadPartTypeExtensions.TryParseWireName(document.Type, out var type))
        {
            throw new JsonException("The persisted headpart type is invalid.");
        }

        return document.Kind switch
        {
            "external-hdpt" when document.AllocatedLocalFormId is null &&
                                      document.QualifiedExternalFaceHdpt is null =>
                new ExternalSkyrimNpcHeadPart(
                    ParseReference(document.ExternalHdpt, "externalHdpt"),
                    type),
            "output-owned-face-hdpt" or
            "output-owned-face-hdpt-preserve-qualified-edid"
                when document.ExternalHdpt is null &&
                     type == NpcHeadPartType.Face =>
                new OutputOwnedSkyrimNpcFaceHeadPart(
                    ParseFormId(document.AllocatedLocalFormId,
                        "allocatedLocalFormId"),
                    ParseReference(document.QualifiedExternalFaceHdpt,
                        "qualifiedExternalFaceHdpt"))
                {
                    PreserveQualifiedEditorId =
                        document.Kind ==
                        "output-owned-face-hdpt-preserve-qualified-edid"
                },
            _ => throw new JsonException(
                "The persisted headpart source is ambiguous or unsupported.")
        };
    }

    private static SkyrimNpcHairColorSource ParseHairColor(
        HairColorDocument document) => document.Kind switch
        {
            "external-clfm" when document.AllocatedLocalFormId is null &&
                                  document.PackedRgb is null =>
                new ExternalSkyrimNpcHairColor(
                    ParseReference(document.ExternalClfm, "externalClfm")),
            "output-owned-clfm" when document.ExternalClfm is null &&
                                      document.PackedRgb is not null =>
                new OutputOwnedSkyrimNpcHairColor(
                    ParseFormId(document.AllocatedLocalFormId,
                        "allocatedLocalFormId"),
                    new SkyrimPackedRgb(document.PackedRgb.Value)),
            _ => throw new JsonException(
                "The persisted hair-color source is ambiguous or unsupported.")
        };

    private static SkyrimNpcFaceTextureSetSource ParseFaceTextureSet(
        FaceTextureSetDocument document) => document.Kind switch
        {
            "external-txst" when document.AllocatedLocalFormId is null &&
                                  document.Paths is null =>
                new ExternalSkyrimNpcFaceTextureSet(
                    ParseReference(document.ExternalTxst, "externalTxst")),
            "output-owned-private-head-txst" when document.ExternalTxst is null &&
                                                   document.Paths is not null =>
                new OutputOwnedSkyrimNpcFaceTextureSet(
                    ParseFormId(document.AllocatedLocalFormId,
                        "allocatedLocalFormId"),
                    ParseTexturePaths(document.Paths)),
            _ => throw new JsonException(
                "The persisted face texture-set source is ambiguous or unsupported.")
        };

    private static SkyrimPrivateHeadTexturePaths ParseTexturePaths(
        PrivateHeadTexturePathsDocument document) => new(
        new AssetPath(document.Diffuse),
        new AssetPath(document.NormalOrGloss),
        new AssetPath(document.GlowOrDetailMap),
        new AssetPath(document.Height),
        new AssetPath(document.BacklightMaskOrSpecular),
        OptionalAssetPath(document.EnvironmentMaskOrSubsurfaceTint),
        OptionalAssetPath(document.Environment),
        OptionalAssetPath(document.Multilayer));

    private static AssetPath? OptionalAssetPath(string? value) =>
        value is null ? null : new AssetPath(value);

    private static FormReference ParseReference(string? value, string property)
    {
        if (value is null || !FormReference.TryParse(value, out var reference))
            throw new JsonException($"The persisted {property} reference is invalid.");
        return reference;
    }

    private static FormId ParseFormId(string? value, string property)
    {
        if (value is null || !FormId.TryParse(value, out var formId))
            throw new JsonException($"The persisted {property} FormID is invalid.");
        return formId;
    }

    private static HeadPartDocument CreateHeadPart(
        SkyrimNpcHeadPartSource headPart) => headPart switch
        {
            ExternalSkyrimNpcHeadPart external => new(
                "external-hdpt", external.Hdpt.ToString(), null, null,
                external.Type.ToWireName()),
            OutputOwnedSkyrimNpcFaceHeadPart outputOwned => new(
                outputOwned.PreserveQualifiedEditorId
                    ? "output-owned-face-hdpt-preserve-qualified-edid"
                    : "output-owned-face-hdpt",
                null,
                outputOwned.AllocatedLocalFormId.ToString(),
                outputOwned.QualifiedExternalFaceHdpt.ToString(),
                outputOwned.Type.ToWireName()),
            _ => throw new JsonException(
                "The Skyrim headpart source is missing or unsupported.")
        };

    private static HairColorDocument CreateHairColor(
        SkyrimNpcHairColorSource hairColor) => hairColor switch
        {
            ExternalSkyrimNpcHairColor external => new(
                "external-clfm", external.Clfm.ToString(), null, null),
            OutputOwnedSkyrimNpcHairColor outputOwned => new(
                "output-owned-clfm", null,
                outputOwned.AllocatedLocalFormId.ToString(),
                outputOwned.PackedRgb.Value),
            _ => throw new JsonException(
                "The Skyrim hair-color source is missing or unsupported.")
        };

    private static FaceTextureSetDocument CreateFaceTextureSet(
        SkyrimNpcFaceTextureSetSource faceTextureSet) => faceTextureSet switch
        {
            ExternalSkyrimNpcFaceTextureSet external => new(
                "external-txst", external.Txst.ToString(), null, null),
            OutputOwnedSkyrimNpcFaceTextureSet outputOwned => new(
                "output-owned-private-head-txst", null,
                outputOwned.AllocatedLocalFormId.ToString(),
                outputOwned.Paths is null ? null : new PrivateHeadTexturePathsDocument(
                    outputOwned.Paths.Diffuse.Value,
                    outputOwned.Paths.NormalOrGloss.Value,
                    outputOwned.Paths.EnvironmentMaskOrSubsurfaceTint?.Value,
                    outputOwned.Paths.GlowOrDetailMap.Value,
                    outputOwned.Paths.Height.Value,
                    outputOwned.Paths.Environment?.Value,
                    outputOwned.Paths.Multilayer?.Value,
                    outputOwned.Paths.BacklightMaskOrSpecular.Value)),
            _ => throw new JsonException(
                "The Skyrim face texture-set source is missing or unsupported.")
        };

    private static OutfitSkinBindingDocument CreateOutfitSkinBinding(
        OutputOwnedSkyrimNpcExposedOutfitSkinBinding binding) => new(
        binding.AllocatedArmorAddonLocalFormId.ToString(),
        binding.AllocatedArmorLocalFormId.ToString(),
        binding.AllocatedOutfitLocalFormId.ToString(),
        CreateBinding(binding.SourceOutfit),
        CreateBinding(binding.SourceArmor),
        CreateBinding(binding.SourceArmorAddon),
        CreateBinding(binding.TargetFemaleSkinTextureSet));

    private static OutputOwnedSkyrimNpcExposedOutfitSkinBinding
        ParseOutfitSkinBinding(OutfitSkinBindingDocument document) => new(
            ParseFormId(document.AllocatedArmorAddonLocalFormId,
                "allocatedArmorAddonLocalFormId"),
            ParseFormId(document.AllocatedArmorLocalFormId,
                "allocatedArmorLocalFormId"),
            ParseFormId(document.AllocatedOutfitLocalFormId,
                "allocatedOutfitLocalFormId"),
            ParseBinding(document.SourceOutfit, "sourceOutfit"),
            ParseBinding(document.SourceArmor, "sourceArmor"),
            ParseBinding(document.SourceArmorAddon, "sourceArmorAddon"),
            ParseBinding(document.TargetFemaleSkinTextureSet,
                "targetFemaleSkinTextureSet"));

    private static NakedSkinBindingDocument CreateNakedSkinBinding(
        OutputOwnedSkyrimNpcNakedSkinBinding binding) => new(
        binding.AllocatedArmorLocalFormId.ToString(),
        CreateBinding(binding.SourceSkinArmor),
        binding.Regions.Select(region => new NakedSkinRegionDocument(
                region.Region.ToString(),
                region.AllocatedArmorAddonLocalFormId.ToString(),
                CreateBinding(region.SourceArmorAddon),
                region.TargetFemaleSkinTextureSet is null
                    ? null
                    : CreateBinding(region.TargetFemaleSkinTextureSet),
                region.FemaleModel.Value))
            .ToImmutableArray());

    private static OutputOwnedSkyrimNpcNakedSkinBinding ParseNakedSkinBinding(
        NakedSkinBindingDocument document) => new(
        ParseFormId(document.AllocatedArmorLocalFormId,
            "allocatedArmorLocalFormId"),
        ParseBinding(document.SourceSkinArmor, "sourceSkinArmor"),
        document.Regions.Select(ParseNakedSkinRegion).ToImmutableArray());

    private static OutputOwnedSkyrimNpcNakedSkinRegionBinding
        ParseNakedSkinRegion(NakedSkinRegionDocument document)
    {
        if (!Enum.TryParse<SkyrimNpcSkinRegion>(
                document.Region, ignoreCase: false, out var region))
            throw new JsonException(
                "The persisted naked skin region is invalid.");
        return new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
            region,
            ParseFormId(document.AllocatedArmorAddonLocalFormId,
                "allocatedArmorAddonLocalFormId"),
            ParseBinding(document.SourceArmorAddon, "sourceArmorAddon"),
            document.TargetFemaleSkinTextureSet is null
                ? null
                : ParseBinding(document.TargetFemaleSkinTextureSet,
                    "targetFemaleSkinTextureSet"),
            new AssetPath(document.FemaleModel));
    }

    private static FormBindingDocument CreateBinding(
        RaceMenuNpcFormBinding binding) => new(
        binding.Signature.Value,
        binding.SourceReference.ToString(),
        binding.Reference.ToString(),
        binding.ProviderPluginName.Value,
        binding.ProviderPlugin.Value,
        binding.ProviderPluginSha256.Value);

    private static RaceMenuNpcFormBinding ParseBinding(
        FormBindingDocument document,
        string role)
    {
        if (document is null)
            throw new JsonException($"The persisted {role} binding is missing.");
        var signature = new RecordSignature(document.Signature);
        return new RaceMenuNpcFormBinding(
            signature,
            ParseReference(document.SourceReference,
                role + ".sourceReference"),
            ParseReference(document.Reference, role + ".reference"),
            new PluginName(document.ProviderPluginName),
            new WorkspacePath(document.ProviderPlugin),
            new Sha256Hash(document.ProviderPluginSha256),
            null);
    }

    internal sealed record FullyAuthoredDocument(
        ImmutableArray<HeadPartDocument> OrderedHeadParts,
        HairColorDocument HairColor,
        FaceTextureSetDocument FaceTextureSet,
        float Weight,
        FaceMorphDocument FaceMorphs,
        FaceTintDocument FaceTints,
        QnamDocument Qnam,
        OutfitSkinBindingDocument? ExposedOutfitSkinBinding = null,
        NakedSkinBindingDocument? NakedSkinBinding = null);

    internal sealed record OutfitSkinBindingDocument(
        string AllocatedArmorAddonLocalFormId,
        string AllocatedArmorLocalFormId,
        string AllocatedOutfitLocalFormId,
        FormBindingDocument SourceOutfit,
        FormBindingDocument SourceArmor,
        FormBindingDocument SourceArmorAddon,
        FormBindingDocument TargetFemaleSkinTextureSet);

    internal sealed record FormBindingDocument(
        string Signature,
        string SourceReference,
        string Reference,
        string ProviderPluginName,
        string ProviderPlugin,
        string ProviderPluginSha256);

    internal sealed record NakedSkinBindingDocument(
        string AllocatedArmorLocalFormId,
        FormBindingDocument SourceSkinArmor,
        ImmutableArray<NakedSkinRegionDocument> Regions);

    internal sealed record NakedSkinRegionDocument(
        string Region,
        string AllocatedArmorAddonLocalFormId,
        FormBindingDocument SourceArmorAddon,
        FormBindingDocument? TargetFemaleSkinTextureSet,
        string FemaleModel);

    internal sealed record HeadPartDocument(
        string Kind,
        string? ExternalHdpt,
        string? AllocatedLocalFormId,
        string? QualifiedExternalFaceHdpt,
        string Type);

    internal sealed record HairColorDocument(
        string Kind,
        string? ExternalClfm,
        string? AllocatedLocalFormId,
        uint? PackedRgb);

    internal sealed record FaceTextureSetDocument(
        string Kind,
        string? ExternalTxst,
        string? AllocatedLocalFormId,
        PrivateHeadTexturePathsDocument? Paths);

    internal sealed record PrivateHeadTexturePathsDocument(
        string Diffuse,
        string NormalOrGloss,
        string? EnvironmentMaskOrSubsurfaceTint,
        string GlowOrDetailMap,
        string Height,
        string? Environment,
        string? Multilayer,
        string BacklightMaskOrSpecular);

    internal sealed record FaceMorphDocument(
        ImmutableArray<float> Nam9Sliders,
        float Nam9Trailing,
        ImmutableArray<uint> NamaValues);

    internal sealed record FaceTintDocument(
        ImmutableArray<FaceTintLayerDocument> Layers);

    internal sealed record FaceTintLayerDocument(
        ushort Index,
        byte Red,
        byte Green,
        byte Blue,
        byte Alpha,
        uint Coverage,
        short PresetIndex);

    internal sealed record QnamDocument(float Red, float Green, float Blue);
}
