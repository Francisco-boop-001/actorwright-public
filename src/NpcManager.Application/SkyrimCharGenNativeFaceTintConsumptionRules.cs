using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Closed contract for the settings the current native Skyrim compositor can
/// truthfully consume. Unsupported editor values remain persistable but may
/// not be represented as having driven a native FaceTint bake.
/// </summary>
public static class SkyrimCharGenNativeFaceTintConsumptionRules
{
    public static ImmutableArray<Diagnostic> Validate(CharGenOptions? options)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (options is null)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-native-facetint-options-required",
                "A complete accepted Skyrim CharGen options document is required."));
            return diagnostics.ToImmutable();
        }

        diagnostics.AddRange(SkyrimCharGenOptionsEditorRules.Validate(options));
        if (options.PerLayerResolution ||
            options.DiffuseResolution != FaceGenChannelResolution.R512 ||
            options.NormalResolution != FaceGenChannelResolution.R512 ||
            options.SpecularResolution != FaceGenChannelResolution.R512)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-native-facetint-resolution",
                "The native Skyrim FaceTint compositor currently consumes uniform 512 resolution only."));
        }

        if (options.DiffuseCompression != FaceGenDiffuseCompression.Bc3 ||
            options.NormalCompression !=
                FaceGenNormalSpecularCompression.Uncompressed ||
            options.SpecularCompression != FaceGenNormalSpecularCompression.Bc5)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-native-facetint-compression",
                "The native Skyrim FaceTint compositor currently emits BC3/DXT5; Normal remains uncompressed and hidden Specular remains BC5."));
        }

        if (options.GenerateTga)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-native-facetint-tga",
                "The native FaceTint transaction does not emit a TGA sidecar."));
        }

        if (options.BakeSseRaceMenuOverlays)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-native-facetint-overlays",
                "RaceMenu overlay baking requires an explicit overlay carrier and is unavailable in the bounded native transaction."));
        }

        CharGenOptions defaults = CharGenOptionsDefaults.For(
            GameEdition.SkyrimSpecialEdition);
        if (!ConventionEquals(options.Convention, defaults.Convention))
        {
            diagnostics.Add(Error(
                "skyrim-chargen-native-facetint-convention",
                "The native compositor currently consumes the pinned Skyrim linear/red-mask/constant-seed convention only."));
        }

        if (!RulesEqual(options.TintSort.TintRules,
                defaults.TintSort.TintRules) ||
            !RulesEqual(options.TintSort.SwapRules,
                defaults.TintSort.SwapRules) ||
            options.TintSort.SkinTonePlacement !=
                FaceTintSkinTonePlacement.Positional)
        {
            diagnostics.Add(Error(
                "skyrim-chargen-native-facetint-order",
                "The native compositor currently consumes ascending RACE tint order, ascending overlay index, and positional skin tone only."));
        }

        return diagnostics.ToImmutable();
    }

    private static bool ConventionEquals(
        FaceTintConventionSettings left,
        FaceTintConventionSettings right) =>
        left.Diffuse == right.Diffuse &&
        left.NormalSpecular == right.NormalSpecular &&
        left.Swap == right.Swap &&
        left.DiffuseWorkingSpaceByBlend ==
            right.DiffuseWorkingSpaceByBlend &&
        left.DiffuseTextureSourceSpace ==
            right.DiffuseTextureSourceSpace &&
        left.SeedDiffuseG22 == right.SeedDiffuseG22 &&
        left.SeedMode == right.SeedMode &&
        left.SeedConstant.SequenceEqual(right.SeedConstant);

    private static bool RulesEqual(
        ImmutableArray<FaceTintSortRule> left,
        ImmutableArray<FaceTintSortRule> right) =>
        !left.IsDefault && !right.IsDefault && left.SequenceEqual(right);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
