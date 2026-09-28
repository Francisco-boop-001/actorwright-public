using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimFaceEditorSection
{
    FaceParts,
    NativeMorphs,
    CustomMorphs,
    Tints,
    Sculpt,
    FaceOverlays
}

public sealed record SkyrimFaceEditorParts(
    ImmutableArray<NpcHeadPartSelection> OrderedHeadParts,
    OptionalFormReference HairColor,
    FormReference? HeadTexture,
    bool IsCharGenFacePreset);

public sealed record SkyrimFaceEditorTintLayer(
    SkyrimFaceTintLayer Value,
    bool IsAuthored,
    AssetPath? RaceDefaultMask,
    AssetPath? MaskOverride);

/// <summary>
/// Complete immutable Skyrim face-editor transaction payload. It contains only
/// typed values and never implies that a plugin, FaceGen asset, or runtime state
/// has changed.
/// </summary>
public sealed record SkyrimFaceEditorDocument(
    SkyrimFaceEditorParts Parts,
    SkyrimFaceMorphPatch NativeMorphs,
    ImmutableArray<SkyrimRaceMenuCustomMorphValue> CustomMorphs,
    ImmutableArray<SkyrimFaceEditorTintLayer> Tints,
    ImmutableArray<RaceMenuSculptPart> SculptParts,
    ImmutableArray<RaceMenuBodyOverlay> BodyOverlays);

public sealed record SkyrimFaceEditorValidationResult(
    bool Accepted,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Stateless invariants for one immutable face-editor document. Section-specific
/// transitions are kept in separate partial files so this type remains a cohesive
/// rules facade rather than a stateful editor or writer authority.
/// </summary>
public static partial class SkyrimFaceEditorDocumentRules
{
    public const int NativeSliderCount = 18;
    public const int NativeFamilyCount = 4;
    public const int MaximumHeadParts = 128;
    public const int MaximumCustomMorphs = 4096;
    public const int MaximumTintLayers = 1024;
    public const int MaximumSculptParts = 1024;
    public const int MaximumSculptVerticesPerPart = 1_000_000;
    public const int MaximumBodyOverlays = 128;
    public const float CustomMorphZeroEpsilon = 0.0001F;
    public const uint NamaUnset = uint.MaxValue;

    public static SkyrimFaceEditorValidationResult Validate(
        SkyrimFaceEditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateParts(document.Parts, diagnostics);
        ValidateNativeMorphs(document.NativeMorphs, diagnostics);
        ValidateCustomMorphs(document.CustomMorphs, diagnostics);
        ValidateTints(document.Tints, diagnostics);
        ValidateSculpt(document.SculptParts, diagnostics);
        ValidateOverlays(document.BodyOverlays, diagnostics);
        return new SkyrimFaceEditorValidationResult(
            !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error),
            diagnostics.ToImmutable());
    }

    public static bool Equivalent(
        SkyrimFaceEditorDocument left,
        SkyrimFaceEditorDocument right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Parts.OrderedHeadParts.SequenceEqual(right.Parts.OrderedHeadParts) &&
               left.Parts.HairColor == right.Parts.HairColor &&
               left.Parts.HeadTexture == right.Parts.HeadTexture &&
               left.Parts.IsCharGenFacePreset == right.Parts.IsCharGenFacePreset &&
               left.NativeMorphs.Nam9Sliders.SequenceEqual(right.NativeMorphs.Nam9Sliders) &&
               left.NativeMorphs.Nam9Trailing.Equals(right.NativeMorphs.Nam9Trailing) &&
               left.NativeMorphs.NamaValues.SequenceEqual(right.NativeMorphs.NamaValues) &&
               left.CustomMorphs.SequenceEqual(right.CustomMorphs) &&
               left.Tints.SequenceEqual(right.Tints) &&
               SculptEquivalent(left.SculptParts, right.SculptParts) &&
               OverlayEquivalent(left.BodyOverlays, right.BodyOverlays);
    }

    public static SkyrimFaceEditorDocument ResetSection(
        SkyrimFaceEditorDocument current,
        SkyrimFaceEditorDocument baseline,
        SkyrimFaceEditorSection section)
    {
        RequireValid(current);
        RequireValid(baseline);
        return section switch
        {
            SkyrimFaceEditorSection.FaceParts => current with { Parts = baseline.Parts },
            SkyrimFaceEditorSection.NativeMorphs => current with { NativeMorphs = baseline.NativeMorphs },
            SkyrimFaceEditorSection.CustomMorphs => current with { CustomMorphs = baseline.CustomMorphs },
            SkyrimFaceEditorSection.Tints => current with { Tints = baseline.Tints },
            SkyrimFaceEditorSection.Sculpt => current with { SculptParts = baseline.SculptParts },
            SkyrimFaceEditorSection.FaceOverlays => current with { BodyOverlays = baseline.BodyOverlays },
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
        };
    }

    private static void RequireValid(SkyrimFaceEditorDocument document)
    {
        SkyrimFaceEditorValidationResult result = Validate(document);
        if (!result.Accepted)
            throw new ArgumentException(result.Diagnostics[0].Message, nameof(document));
    }

    private static string RequireName(string name)
    {
        string normalized = name?.Trim() ?? string.Empty;
        if (!ValidName(normalized))
            throw new ArgumentException(
                "Names must be 1-256 characters and contain no control characters.",
                nameof(name));
        return normalized;
    }

    private static bool ValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        !value.Any(char.IsControl);

    private static void RequireReference(FormReference value, string parameter)
    {
        if (!ValidReference(value))
            throw new ArgumentException("The FormReference is invalid.", parameter);
    }

    private static bool ValidReference(FormReference value) =>
        value.FormId.Value is > 0 and <= 0x00FF_FFFF &&
        !string.IsNullOrWhiteSpace(value.Plugin.Value);

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value, right.Plugin.Value, StringComparison.OrdinalIgnoreCase);

    private static int FindIndex<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        int index = 0;
        foreach (T value in values)
        {
            if (predicate(value)) return index;
            index++;
        }
        return -1;
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
