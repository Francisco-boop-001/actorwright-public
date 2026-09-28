using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimAppearancePasteCategory
{
    BodyWeight,
    BodyShape,
    RaceMenuPaintsAndSkin,
    Outfits,
    FaceParts,
    HairColor,
    FaceTints,
    FaceMorphs,
    Sculpt,
    CharGenFlag
}

public static class SkyrimAppearancePasteCategoryExtensions
{
    public static string ToWireName(this SkyrimAppearancePasteCategory category) =>
        category switch
        {
            SkyrimAppearancePasteCategory.BodyWeight => "body-weight",
            SkyrimAppearancePasteCategory.BodyShape => "body-shape",
            SkyrimAppearancePasteCategory.RaceMenuPaintsAndSkin =>
                "racemenu-paints-and-skin",
            SkyrimAppearancePasteCategory.Outfits => "outfits",
            SkyrimAppearancePasteCategory.FaceParts => "face-parts",
            SkyrimAppearancePasteCategory.HairColor => "hair-color",
            SkyrimAppearancePasteCategory.FaceTints => "face-tints",
            SkyrimAppearancePasteCategory.FaceMorphs => "face-morphs",
            SkyrimAppearancePasteCategory.Sculpt => "sculpt",
            SkyrimAppearancePasteCategory.CharGenFlag => "chargen-flag",
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };
}

public sealed record SkyrimSelectiveAppearancePasteSelection(
    ImmutableArray<SkyrimAppearancePasteCategory> Categories)
{
    public static SkyrimSelectiveAppearancePasteSelection All { get; } = new(
        Enum.GetValues<SkyrimAppearancePasteCategory>().ToImmutableArray());

    public static SkyrimSelectiveAppearancePasteSelection None { get; } = new([]);

    public bool Includes(SkyrimAppearancePasteCategory category) =>
        Categories.Contains(category);
}

/// <summary>
/// Complete immutable value used by the selective-paste transaction. Face and
/// body retain the same full RaceMenu overlay table so either editor can hide
/// unrelated zones without creating a second source of truth.
/// </summary>
public sealed record SkyrimSelectiveAppearancePasteDocument(
    SkyrimFaceEditorDocument Face,
    SkyrimBodyEditorDocument Body,
    NpcOutfitSnapshot Outfits);

public sealed record SkyrimSelectiveAppearancePasteResult(
    bool Accepted,
    SkyrimSelectiveAppearancePasteDocument? Document,
    bool HasChanges,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Pure Skyrim source-to-target category merge. It performs no file, preview,
/// plugin, FaceGen, BodySlide, or runtime operation.
/// </summary>
public static class SkyrimSelectiveAppearancePasteRules
{
    public static SkyrimSelectiveAppearancePasteResult Merge(
        SkyrimSelectiveAppearancePasteDocument source,
        SkyrimSelectiveAppearancePasteDocument target,
        SkyrimSelectiveAppearancePasteSelection selection)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateDocument(source, "source", diagnostics);
        ValidateDocument(target, "target", diagnostics);
        ValidateSelection(selection, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new(false, null, false, diagnostics.ToImmutable());

        SkyrimBodyEditorDocument body = target.Body;
        SkyrimFaceEditorDocument face = target.Face;
        SkyrimFaceEditorParts parts = face.Parts;
        NpcOutfitSnapshot outfits = target.Outfits;

        if (selection.Includes(SkyrimAppearancePasteCategory.BodyWeight))
            body = body with { Weight = source.Body.Weight };

        if (selection.Includes(SkyrimAppearancePasteCategory.BodyShape))
        {
            body = body with
            {
                BodySlide = source.Body.BodySlide,
                NodeTransforms = source.Body.NodeTransforms
            };
        }

        if (selection.Includes(SkyrimAppearancePasteCategory.RaceMenuPaintsAndSkin))
        {
            body = body with
            {
                SkinOverrides = source.Body.SkinOverrides,
                BodyOverlays = source.Body.BodyOverlays
            };
            face = face with { BodyOverlays = source.Face.BodyOverlays };
        }

        if (selection.Includes(SkyrimAppearancePasteCategory.Outfits))
            outfits = source.Outfits;

        if (selection.Includes(SkyrimAppearancePasteCategory.FaceParts))
        {
            parts = parts with
            {
                OrderedHeadParts = source.Face.Parts.OrderedHeadParts,
                HeadTexture = source.Face.Parts.HeadTexture
            };
        }

        if (selection.Includes(SkyrimAppearancePasteCategory.HairColor))
            parts = parts with { HairColor = source.Face.Parts.HairColor };

        if (selection.Includes(SkyrimAppearancePasteCategory.FaceTints))
            face = face with { Tints = source.Face.Tints };

        if (selection.Includes(SkyrimAppearancePasteCategory.FaceMorphs))
        {
            face = face with
            {
                NativeMorphs = source.Face.NativeMorphs,
                CustomMorphs = source.Face.CustomMorphs
            };
        }

        if (selection.Includes(SkyrimAppearancePasteCategory.Sculpt))
            face = face with { SculptParts = source.Face.SculptParts };

        if (selection.Includes(SkyrimAppearancePasteCategory.CharGenFlag))
        {
            parts = parts with
            {
                IsCharGenFacePreset = source.Face.Parts.IsCharGenFacePreset
            };
        }

        face = face with { Parts = parts };
        var merged = new SkyrimSelectiveAppearancePasteDocument(face, body, outfits);
        ValidateDocument(merged, "merged", diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new(false, null, false, diagnostics.ToImmutable());

        return new(
            true,
            merged,
            !Equivalent(target, merged),
            diagnostics.ToImmutable());
    }

    public static bool Equivalent(
        SkyrimSelectiveAppearancePasteDocument left,
        SkyrimSelectiveAppearancePasteDocument right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return SkyrimFaceEditorDocumentRules.Equivalent(left.Face, right.Face) &&
               SkyrimBodyEditorDocumentRules.Equivalent(left.Body, right.Body) &&
               left.Outfits == right.Outfits;
    }

    private static void ValidateDocument(
        SkyrimSelectiveAppearancePasteDocument? document,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (document?.Face is null || document.Body is null || document.Outfits is null)
        {
            diagnostics.Add(Error(
                "selective-paste-document-missing",
                $"The {role} appearance document is incomplete."));
            return;
        }

        SkyrimFaceEditorValidationResult face =
            SkyrimFaceEditorDocumentRules.Validate(document.Face);
        SkyrimBodyEditorValidationResult body =
            SkyrimBodyEditorDocumentRules.Validate(document.Body);
        diagnostics.AddRange(face.Diagnostics.Select(item => Prefix(role, item)));
        diagnostics.AddRange(body.Diagnostics.Select(item => Prefix(role, item)));

        ValidateOutfit(document.Outfits.DefaultOutfit, role, "default", diagnostics);
        ValidateOutfit(document.Outfits.SleepingOutfit, role, "sleeping", diagnostics);
        if (!OverlayEquivalent(
                document.Face.BodyOverlays,
                document.Body.BodyOverlays))
        {
            diagnostics.Add(Error(
                "selective-paste-overlay-carrier-mismatch",
                $"The {role} face and body documents do not carry the same complete overlay table."));
        }
    }

    private static void ValidateSelection(
        SkyrimSelectiveAppearancePasteSelection? selection,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (selection is null || selection.Categories.IsDefault)
        {
            diagnostics.Add(Error(
                "selective-paste-selection-missing",
                "The Skyrim appearance category selection must be initialized."));
            return;
        }

        if (selection.Categories.Any(category => !Enum.IsDefined(category)))
        {
            diagnostics.Add(Error(
                "selective-paste-category-invalid",
                "The Skyrim appearance category selection contains an unknown value."));
        }

        if (selection.Categories.Distinct().Count() != selection.Categories.Length)
        {
            diagnostics.Add(Error(
                "selective-paste-category-duplicate",
                "Each Skyrim appearance category may be selected at most once."));
        }
    }

    private static void ValidateOutfit(
        FormReference? reference,
        string role,
        string field,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (reference is not { } value) return;
        if (value.FormId.Value is > 0 and <= 0x00FF_FFFF &&
            !string.IsNullOrWhiteSpace(value.Plugin.Value)) return;
        diagnostics.Add(Error(
            "selective-paste-outfit-invalid",
            $"The {role} {field} outfit reference is invalid."));
    }

    private static bool OverlayEquivalent(
        ImmutableArray<RaceMenuBodyOverlay> left,
        ImmutableArray<RaceMenuBodyOverlay> right)
    {
        if (left.IsDefault || right.IsDefault || left.Length != right.Length)
            return false;
        for (int index = 0; index < left.Length; index++)
        {
            RaceMenuBodyOverlay? a = left[index];
            RaceMenuBodyOverlay? b = right[index];
            if (a is null || b is null ||
                !string.Equals(a.Node, b.Node, StringComparison.Ordinal) ||
                !string.Equals(a.Diffuse, b.Diffuse, StringComparison.Ordinal) ||
                !string.Equals(a.Normal, b.Normal, StringComparison.Ordinal) ||
                a.Alpha != b.Alpha ||
                !a.Tint.SequenceEqual(b.Tint) ||
                !a.Values.SequenceEqual(b.Values)) return false;
        }
        return true;
    }

    private static Diagnostic Prefix(string role, Diagnostic diagnostic) =>
        diagnostic with
        {
            Code = $"selective-paste-{role}-{diagnostic.Code}",
            Message = $"{role}: {diagnostic.Message}"
        };

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
