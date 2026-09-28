using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimCharGenSortList
{
    Tint,
    Overlay
}

public sealed record SkyrimCharGenOptionsEditorResult(
    bool Accepted,
    CharGenOptions? Options,
    ImmutableArray<Diagnostic> Diagnostics);

public static class SkyrimCharGenOptionsEditorRules
{
    public static SkyrimCharGenOptionsEditorResult Save(CharGenOptions? options)
    {
        ImmutableArray<Diagnostic> diagnostics = Validate(options);
        return diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error)
            ? new SkyrimCharGenOptionsEditorResult(false, null, diagnostics)
            : new SkyrimCharGenOptionsEditorResult(true, options, diagnostics);
    }

    public static SkyrimCharGenOptionsEditorResult Cancel() =>
        new(false, null, []);

    public static CharGenOptions NormalizeTextureMode(CharGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.PerLayerResolution)
            return options with
            {
                SpecularResolution = FaceGenChannelResolution.Inherit,
                SpecularCompression = FaceGenNormalSpecularCompression.Bc5
            };
        return options with
        {
            NormalResolution = options.DiffuseResolution,
            SpecularResolution = options.DiffuseResolution,
            NormalCompression = FaceGenNormalSpecularCompression.Uncompressed,
            SpecularCompression = EffectiveSpecularCompression(
                options.DiffuseCompression)
        };
    }

    public static CharGenOptions ResetTexture(CharGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        CharGenOptions defaults = CharGenOptionsDefaults.For(
            GameEdition.SkyrimSpecialEdition);
        return options with
        {
            PerLayerResolution = defaults.PerLayerResolution,
            DiffuseResolution = defaults.DiffuseResolution,
            NormalResolution = defaults.NormalResolution,
            SpecularResolution = defaults.SpecularResolution,
            DiffuseCompression = defaults.DiffuseCompression,
            NormalCompression = defaults.NormalCompression,
            SpecularCompression = defaults.SpecularCompression,
            GenerateTga = defaults.GenerateTga
        };
    }

    public static CharGenOptions ResetConvention(CharGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with
        {
            Convention = CharGenOptionsDefaults.For(
                GameEdition.SkyrimSpecialEdition).Convention
        };
    }

    public static CharGenOptions ResetSort(CharGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with
        {
            TintSort = CharGenOptionsDefaults.For(
                GameEdition.SkyrimSpecialEdition).TintSort
        };
    }

    public static SkyrimCharGenOptionsEditorResult AddSortRule(
        CharGenOptions options,
        SkyrimCharGenSortList list,
        FaceTintSortRule rule)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rule);
        ImmutableArray<FaceTintSortRule> current = Rules(options, list);
        if (current.IsDefault)
            return Refused("chargen-editor-sort-default",
                "The selected sort list must be initialized.");
        if (current.Any(item => item.Key == rule.Key))
            return Refused("chargen-editor-sort-duplicate",
                "That sort key is already present in the selected list.");
        return Save(WithRules(options, list, current.Add(rule)));
    }

    public static SkyrimCharGenOptionsEditorResult RemoveSortRule(
        CharGenOptions options,
        SkyrimCharGenSortList list,
        int index)
    {
        ArgumentNullException.ThrowIfNull(options);
        ImmutableArray<FaceTintSortRule> current = Rules(options, list);
        if (current.IsDefault || index < 0 || index >= current.Length)
            return Refused("chargen-editor-sort-selection",
                "Choose one existing sort rule to remove.");
        return Save(WithRules(options, list, current.RemoveAt(index)));
    }

    public static SkyrimCharGenOptionsEditorResult MoveSortRule(
        CharGenOptions options,
        SkyrimCharGenSortList list,
        int index,
        int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(options);
        ImmutableArray<FaceTintSortRule> current = Rules(options, list);
        if (current.IsDefault || index < 0 || index >= current.Length ||
            targetIndex < 0 || targetIndex >= current.Length ||
            index == targetIndex)
            return Refused("chargen-editor-sort-move",
                "The selected sort rule cannot move to that position.");
        FaceTintSortRule moved = current[index];
        ImmutableArray<FaceTintSortRule> reordered = current
            .RemoveAt(index)
            .Insert(targetIndex, moved);
        return Save(WithRules(options, list, reordered));
    }

    public static ImmutableArray<Diagnostic> Validate(CharGenOptions? options)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (options is null)
        {
            diagnostics.Add(Error(
                "chargen-editor-document-null",
                "A complete Skyrim CharGen options document is required."));
            return diagnostics.ToImmutable();
        }
        if (options.SchemaVersion != "1")
            diagnostics.Add(Error(
                "chargen-editor-schema",
                "Only schema version 1 is supported."));
        if (options.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error(
                "chargen-editor-edition",
                "The Skyrim CharGen editor requires Skyrim Special Edition."));
        if (options.ApplyGhoulHeadRearFix ||
            options.ApplyEyebrowsFixedColor ||
            options.ApplyMouthVanillaFix)
            diagnostics.Add(Error(
                "chargen-editor-fo4-only",
                "Fallout-only fixes must remain disabled in Skyrim options."));
        if (!Enum.IsDefined(options.DiffuseResolution) ||
            !Enum.IsDefined(options.NormalResolution) ||
            !Enum.IsDefined(options.SpecularResolution) ||
            !Enum.IsDefined(options.DiffuseCompression) ||
            !Enum.IsDefined(options.NormalCompression) ||
            !Enum.IsDefined(options.SpecularCompression))
            diagnostics.Add(Error(
                "chargen-editor-texture-value",
                "Texture resolutions and compression values must be defined enum values."));
        if (!options.PerLayerResolution &&
            (options.NormalResolution != options.DiffuseResolution ||
             options.SpecularResolution != options.DiffuseResolution ||
             options.NormalCompression !=
                 FaceGenNormalSpecularCompression.Uncompressed ||
             options.SpecularCompression != EffectiveSpecularCompression(
                 options.DiffuseCompression)))
            diagnostics.Add(Error(
                "chargen-editor-uniform-derivation",
                "Uniform mode requires Skyrim game-aware Normal and Specular derivation."));
        if (options.PerLayerResolution &&
            (options.SpecularResolution != FaceGenChannelResolution.Inherit ||
             options.SpecularCompression !=
                 FaceGenNormalSpecularCompression.Bc5))
            diagnostics.Add(Error(
                "chargen-editor-sse-specular",
                "Skyrim per-layer mode must keep the unused Specular channel neutral."));
        ValidateConvention(options.Convention, diagnostics);
        ValidateSort(options.TintSort, diagnostics);
        return diagnostics.ToImmutable();
    }

    private static void ValidateConvention(
        FaceTintConventionSettings? convention,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (convention is null || convention.Diffuse is null ||
            convention.NormalSpecular is null || convention.Swap is null ||
            convention.DiffuseWorkingSpaceByBlend is null)
        {
            diagnostics.Add(Error(
                "chargen-editor-convention-null",
                "Complete convention buckets and blend working spaces are required."));
            return;
        }
        ValidateBucket(convention.Diffuse, diagnostics);
        ValidateBucket(convention.NormalSpecular, diagnostics);
        ValidateBucket(convention.Swap, diagnostics);
        FaceTintBlendWorkingSpaces spaces =
            convention.DiffuseWorkingSpaceByBlend;
        if (!Enum.IsDefined(spaces.Replace) ||
            !Enum.IsDefined(spaces.Multiply) ||
            !Enum.IsDefined(spaces.Overlay) ||
            !Enum.IsDefined(spaces.SoftLight) ||
            !Enum.IsDefined(spaces.HardLight) ||
            !Enum.IsDefined(convention.DiffuseTextureSourceSpace) ||
            !Enum.IsDefined(convention.SeedMode))
            diagnostics.Add(Error(
                "chargen-editor-convention-value",
                "Convention working-space or seed values are invalid."));
        if (convention.Diffuse.MaskChannel != FaceTintMaskChannel.R)
            diagnostics.Add(Error(
                "chargen-editor-mask-channel",
                "Skyrim diffuse FaceTint masks require the red channel."));
        if (convention.SeedConstant.IsDefault ||
            convention.SeedConstant.Length != 3 ||
            convention.SeedConstant.Any(value =>
                !double.IsFinite(value) || value is < 0 or > 1))
            diagnostics.Add(Error(
                "chargen-editor-seed-constant",
                "The preserved seed constant requires exactly three finite 0..1 values."));
    }

    private static void ValidateBucket(
        FaceTintBucketConvention bucket,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Enum.IsDefined(bucket.WorkingSpace) ||
            !Enum.IsDefined(bucket.CompositeSpace) ||
            !Enum.IsDefined(bucket.SourceSpace) ||
            !Enum.IsDefined(bucket.OutputSpace) ||
            !Enum.IsDefined(bucket.MaskConversion) ||
            !Enum.IsDefined(bucket.Framework) ||
            !Enum.IsDefined(bucket.SoftLight) ||
            !Enum.IsDefined(bucket.MaskChannel))
            diagnostics.Add(Error(
                "chargen-editor-bucket-value",
                "A FaceTint convention bucket contains an undefined value."));
    }

    private static void ValidateSort(
        FaceTintSortSettings? sort,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (sort is null || sort.TintRules.IsDefault ||
            sort.SwapRules.IsDefault)
        {
            diagnostics.Add(Error(
                "chargen-editor-sort-default",
                "Both Skyrim sort lists must be initialized."));
            return;
        }
        ValidateRules(sort.TintRules, 0, 4, "tint", diagnostics);
        ValidateRules(sort.SwapRules, 0, 2, "overlay", diagnostics);
        if (!Enum.IsDefined(sort.SkinTonePlacement))
            diagnostics.Add(Error(
                "chargen-editor-skin-placement",
                "Skin-tone placement is invalid."));
    }

    private static void ValidateRules(
        ImmutableArray<FaceTintSortRule> rules,
        int minimum,
        int maximum,
        string name,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var seen = new HashSet<int>();
        foreach (FaceTintSortRule? rule in rules)
        {
            if (rule is null || rule.Key < minimum || rule.Key > maximum ||
                !seen.Add(rule.Key))
                diagnostics.Add(Error(
                    "chargen-editor-sort-rule",
                    $"Skyrim {name} rules require unique supported keys."));
        }
    }

    private static FaceGenNormalSpecularCompression EffectiveSpecularCompression(
        FaceGenDiffuseCompression diffuse) =>
        diffuse == FaceGenDiffuseCompression.Uncompressed
            ? FaceGenNormalSpecularCompression.Uncompressed
            : FaceGenNormalSpecularCompression.Bc5;

    private static ImmutableArray<FaceTintSortRule> Rules(
        CharGenOptions options,
        SkyrimCharGenSortList list) =>
        list == SkyrimCharGenSortList.Tint
            ? options.TintSort.TintRules
            : options.TintSort.SwapRules;

    private static CharGenOptions WithRules(
        CharGenOptions options,
        SkyrimCharGenSortList list,
        ImmutableArray<FaceTintSortRule> rules) =>
        options with
        {
            TintSort = list == SkyrimCharGenSortList.Tint
                ? options.TintSort with { TintRules = rules }
                : options.TintSort with { SwapRules = rules }
        };

    private static SkyrimCharGenOptionsEditorResult Refused(
        string code,
        string message) =>
        new(false, null, [Error(code, message)]);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
