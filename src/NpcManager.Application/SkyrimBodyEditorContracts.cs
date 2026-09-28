using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum SkyrimBodyEditorSection
{
    Weight,
    BodySlide,
    Transforms,
    SkinOverrides,
    BodyOverlays
}

/// <summary>
/// Complete immutable Skyrim body-editor transaction payload. It deliberately
/// contains the complete transform, skin, and overlay carriers so UI edits can
/// preserve values that the selected field does not understand.
/// </summary>
public sealed record SkyrimBodyEditorDocument(
    float Weight,
    ImmutableArray<BodySlideSliderValue> BodySlide,
    ImmutableArray<SkyrimNodeTransform> NodeTransforms,
    ImmutableArray<SkyrimSkinOverride> SkinOverrides,
    ImmutableArray<RaceMenuBodyOverlay> BodyOverlays);

public sealed record SkyrimBodyEditorValidationResult(
    bool Accepted,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Stateless invariants and pure transitions for one body-editor document.
/// Persistence remains the responsibility of a separately verified writer.
/// </summary>
public static partial class SkyrimBodyEditorDocumentRules
{
    public const int MaximumBodySlideRows = 4096;
    public const int MaximumTransforms = 256;
    public const int MaximumSkinOverrides = 256;
    public const int MaximumBodyOverlays = 128;
    public const float BodySlideZeroEpsilon = 0.001F;
    public const float MinimumTransformScale = 0.01F;
    public const float MaximumTransformScale = 1000F;
    public const float MaximumTransformPosition = 10000F;

    public static SkyrimBodyEditorValidationResult Validate(
        SkyrimBodyEditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateWeight(document.Weight, diagnostics);
        ValidateBodySlide(document.BodySlide, diagnostics);
        ValidateTransforms(document.NodeTransforms, diagnostics);
        ValidateSkinOverrides(document.SkinOverrides, diagnostics);
        ValidateOverlays(document.BodyOverlays, diagnostics);
        return new SkyrimBodyEditorValidationResult(
            !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error),
            diagnostics.ToImmutable());
    }

    public static bool Equivalent(
        SkyrimBodyEditorDocument left,
        SkyrimBodyEditorDocument right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Weight.Equals(right.Weight) &&
               left.BodySlide.SequenceEqual(right.BodySlide) &&
               TransformArrayEquivalent(left.NodeTransforms, right.NodeTransforms) &&
               SkinArrayEquivalent(left.SkinOverrides, right.SkinOverrides) &&
               OverlayArrayEquivalent(left.BodyOverlays, right.BodyOverlays);
    }

    public static SkyrimBodyEditorDocument ResetSection(
        SkyrimBodyEditorDocument current,
        SkyrimBodyEditorDocument baseline,
        SkyrimBodyEditorSection section)
    {
        RequireValid(current);
        RequireValid(baseline);
        return section switch
        {
            SkyrimBodyEditorSection.Weight => current with { Weight = baseline.Weight },
            SkyrimBodyEditorSection.BodySlide => current with
            {
                // The pinned upstream editor intentionally defines BodySlide
                // Reset as "all sliders to zero", not opening-snapshot restore.
                BodySlide = ImmutableArray<BodySlideSliderValue>.Empty
            },
            SkyrimBodyEditorSection.Transforms => current with
            {
                NodeTransforms = baseline.NodeTransforms
            },
            SkyrimBodyEditorSection.SkinOverrides => current with
            {
                SkinOverrides = baseline.SkinOverrides
            },
            SkyrimBodyEditorSection.BodyOverlays => current with
            {
                BodyOverlays = baseline.BodyOverlays
            },
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
        };
    }

    private static void RequireValid(SkyrimBodyEditorDocument document)
    {
        SkyrimBodyEditorValidationResult result = Validate(document);
        if (!result.Accepted)
            throw new ArgumentException(result.Diagnostics[0].Message, nameof(document));
    }

    private static bool ValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
        !value.Any(char.IsControl);

    private static bool ValidUnit(float value) =>
        float.IsFinite(value) && value is >= 0F and <= 1F;

    private static bool ValidTexture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 260 ||
            Path.IsPathRooted(value) || value.Contains(':') || value.Contains('\0')) return false;
        string normalized = value.Replace('/', '\\');
        return !normalized.Split('\\').Any(part => part is "." or "..") &&
               normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);
    }

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
