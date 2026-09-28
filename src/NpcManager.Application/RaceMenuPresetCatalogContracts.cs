using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Static race-compatibility result for one parsed RaceMenu preset.</summary>
public enum RaceMenuPresetCompatibilityKind
{
    Unavailable,
    Compatible,
    Incompatible
}

/// <summary>
/// Target identity used by a provider-backed compatibility evaluator. The
/// authority identifier names the exact reviewed race/head-part/tint closure;
/// it is not runtime authority.
/// </summary>
public sealed record RaceMenuPresetTarget(
    string AuthorityId,
    FormReference Race,
    NpcSex Sex,
    WorkspacePath DataRoot,
    ImmutableArray<SkyrimFaceRecordPluginAuthority> PluginOrder);

public sealed record RaceMenuPresetCompatibilityResult(
    RaceMenuPresetCompatibilityKind Kind,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IRaceMenuPresetCompatibilityEvaluator
{
    ValueTask<RaceMenuPresetCompatibilityResult> EvaluateAsync(
        PresetDocument preset,
        RaceMenuPresetTarget target,
        CancellationToken cancellationToken);
}

/// <summary>Counts shown by the preset loader without reparsing the file.</summary>
public sealed record RaceMenuPresetSummary(
    int HeadParts,
    int Tints,
    int FaceMorphPresets,
    int SliderMorphs,
    int CustomMorphs,
    int SculptParts,
    float? Weight,
    int BodyMorphs,
    int BodyOverlays,
    int NodeTransforms,
    int SkinOverrides);

/// <summary>One admitted, hash-bound preset catalog entry.</summary>
public sealed record RaceMenuPresetCatalogEntry(
    string DisplayName,
    WorkspacePath SourcePath,
    Sha256Hash SourceSha256,
    PresetDocument Document,
    RaceMenuPresetSummary Summary,
    RaceMenuPresetCompatibilityKind Compatibility,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record RaceMenuPresetCatalogRequest(
    WorkspacePath PresetDirectory,
    RaceMenuPresetTarget? Target = null);

public sealed record RaceMenuPresetCatalogResult(
    ImmutableArray<RaceMenuPresetCatalogEntry> Entries,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => !Diagnostics.Any(item =>
        item.Severity == DiagnosticSeverity.Error);
}

public interface IRaceMenuPresetCatalogService
{
    ValueTask<RaceMenuPresetCatalogResult> LoadAsync(
        RaceMenuPresetCatalogRequest request,
        CancellationToken cancellationToken);
}

public sealed record RaceMenuPresetTargetBuildResult(
    RaceMenuPresetTarget? Target,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Accepted => Target is not null &&
                            !Diagnostics.Any(item =>
                                item.Severity == DiagnosticSeverity.Error);
}

/// <summary>Projects one immutable reviewed intake into preset compatibility authority.</summary>
public static class RaceMenuPresetTargetFactory
{
    public static RaceMenuPresetTargetBuildResult Create(
        ReviewedGameIntake intake,
        FormReference race,
        NpcSex sex)
    {
        ArgumentNullException.ThrowIfNull(intake);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (intake.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error("preset-target-edition",
                "RaceMenu compatibility requires a reviewed Skyrim SE intake."));
        }
        if (!Enum.IsDefined(sex))
        {
            diagnostics.Add(Error("preset-target-sex",
                "The target NPC sex is unsupported."));
        }

        PluginClosureReviewEntry[] ordered = intake.Plugins
            .OrderBy(item => item.Order)
            .ToArray();
        if (ordered.Length == 0 || ordered.Length > 64 ||
            ordered.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Length)
        {
            diagnostics.Add(Error("preset-target-plugin-order",
                "The reviewed intake must contain 1-64 distinct plugins in ascending load order."));
        }

        var authorities = ImmutableArray.CreateBuilder<SkyrimFaceRecordPluginAuthority>(
            ordered.Length);
        foreach (PluginClosureReviewEntry plugin in ordered)
        {
            if (!plugin.Exists || !plugin.ReadSucceeded || plugin.SourceHash is not { } hash)
            {
                diagnostics.Add(Error("preset-target-plugin-unqualified",
                    $"Reviewed plugin '{plugin.Plugin}' has no successful ordinary-file SHA-256 authority."));
                continue;
            }
            authorities.Add(new SkyrimFaceRecordPluginAuthority(
                plugin.Plugin, plugin.Path, hash));
        }

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            return new RaceMenuPresetTargetBuildResult(null,
                diagnostics.ToImmutable());
        }

        string authorityId = $"{intake.IntakeFingerprint.Value}:{race}:{sex}";
        return new RaceMenuPresetTargetBuildResult(
            new RaceMenuPresetTarget(authorityId, race, sex, intake.DataRoot,
                authorities.ToImmutable()),
            diagnostics.ToImmutable());
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
