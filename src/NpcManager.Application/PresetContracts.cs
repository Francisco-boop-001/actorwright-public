using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public enum PresetFormat
{
    LooksMenu,
    RaceMenuJslot
}

public static class PresetFormatExtensions
{
    public static string ToWireName(this PresetFormat format) => format switch
    {
        PresetFormat.LooksMenu => "looksmenu",
        PresetFormat.RaceMenuJslot => "racemenu-jslot",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported preset format.")
    };

    public static bool TryParseWireName(string value, out PresetFormat format)
    {
        if (string.Equals(value, "looksmenu", StringComparison.OrdinalIgnoreCase))
        {
            format = PresetFormat.LooksMenu;
            return true;
        }

        if (string.Equals(value, "racemenu-jslot", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "jslot", StringComparison.OrdinalIgnoreCase))
        {
            format = PresetFormat.RaceMenuJslot;
            return true;
        }

        format = default;
        return false;
    }
}

public sealed record PresetFieldPresence(
    bool Gender,
    bool HeadParts,
    bool HairColor,
    bool Weight,
    bool Morphs,
    bool BodyMorphs,
    bool Tints,
    bool Overlays,
    bool Skin,
    bool Fallout4BodyMorphs = false,
    bool ChargenFaceMorphs = false,
    bool FaceBoneRegions = false,
    bool FacialMorphIntensity = false);

public sealed record PresetIdentifier(string Raw, PluginName? Plugin, FormId? FormId)
{
    public static PresetIdentifier Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Preset identifiers must be non-empty.", nameof(raw));
        }

        var separator = raw.LastIndexOf('|');
        if (separator <= 0 || separator == raw.Length - 1)
        {
            return new PresetIdentifier(raw, null, null);
        }

        var pluginText = raw[..separator];
        var formText = raw[(separator + 1)..];
        if (!NpcManager.Domain.FormId.TryParse(formText, out var formId))
        {
            return new PresetIdentifier(raw, null, null);
        }

        try
        {
            return new PresetIdentifier(raw, new PluginName(pluginText), formId);
        }
        catch (ArgumentException)
        {
            return new PresetIdentifier(raw, null, formId);
        }
    }
}

public sealed record PresetHeadPart(PresetIdentifier Identifier, int Type);

public sealed record PresetHairColor(PresetIdentifier? FormIdentifier, uint? PackedRgb)
{
    public static PresetHairColor FromIdentifier(string value) => new(PresetIdentifier.Parse(value), null);
    public static PresetHairColor FromPackedRgb(uint value) => new(null, value);
}

public sealed record PresetWeight(float Value, float? Thin, float? Muscular, float? Fat);

public sealed record PresetTint(int Index, uint Color, string Texture,
    int Type = 1, int Percent = 100, int? ColorId = null);

public sealed record PresetOverlay(string Template, int Priority, ImmutableArray<float> Tint,
    ImmutableArray<float> OffsetUv, ImmutableArray<float> ScaleUv);

public sealed record RaceMenuBodyOverlay(string Node, string? Diffuse, string? Normal,
    ImmutableArray<float> Tint, float? Alpha,
    ImmutableArray<RaceMenuValue> Values = default);

public sealed record RaceMenuFaceTexture(int Index, string Texture);

public sealed record RaceMenuModEntry(byte Index, PluginName Name);

public sealed record RaceMenuVersion(
    uint FormatVersion,
    uint RuntimeVersion,
    uint Signature,
    uint SkseVersion);

public sealed record RaceMenuPresetData(
    string? HeadTexture,
    ImmutableArray<uint> FaceMorphPresets,
    int SculptDivisor,
    ImmutableArray<RaceMenuSculptPart> SculptParts,
    ImmutableDictionary<string, ImmutableDictionary<string, float>> BodyMorphsKeyed,
    ImmutableArray<RaceMenuBodyOverlay> BodyOverlays,
    ImmutableArray<SkyrimNodeTransform> NodeTransforms,
    ImmutableArray<SkyrimSkinOverride> SkinOverrides,
    ImmutableArray<RaceMenuFaceTexture> FaceTextures = default,
    ImmutableArray<PluginName> ModNames = default,
    ImmutableArray<RaceMenuModEntry> Mods = default,
    RaceMenuVersion? Version = null)
{
    public ImmutableArray<RaceMenuFaceTexture> FaceTextures { get; init; } =
        FaceTextures.IsDefault ? ImmutableArray<RaceMenuFaceTexture>.Empty : FaceTextures;

    public ImmutableArray<PluginName> ModNames { get; init; } =
        ModNames.IsDefault ? ImmutableArray<PluginName>.Empty : ModNames;

    public ImmutableArray<RaceMenuModEntry> Mods { get; init; } =
        Mods.IsDefault ? ImmutableArray<RaceMenuModEntry>.Empty : Mods;
}

public sealed record PresetUnknownField(string Path, string JsonKind);

public sealed record PresetAppearance(
    int? Gender,
    ImmutableArray<PresetHeadPart> HeadParts,
    PresetHairColor? HairColor,
    PresetWeight? Weight,
    ImmutableDictionary<string, float> Morphs,
    ImmutableDictionary<string, float> BodyMorphs,
    ImmutableDictionary<string, float> CustomMorphs,
    ImmutableArray<float> SliderMorphs,
    ImmutableArray<PresetTint> Tints,
    ImmutableArray<PresetOverlay> Overlays,
    string? Skin,
    PresetFieldPresence Presence,
    ImmutableArray<PresetUnknownField> UnknownFields,
    Fallout4BodyMorphValues? Fallout4BodyMorphs = null,
    ImmutableDictionary<uint, float>? ChargenFaceMorphs = null,
    ImmutableDictionary<uint, ImmutableArray<float>>? FaceBoneRegions = null,
    float FacialMorphIntensity = 1.0F,
    RaceMenuPresetData? RaceMenu = null,
    ImmutableArray<SkyrimRaceMenuCustomMorphValue> OrderedCustomMorphs = default)
{
    /// <summary>
    /// Active <c>morphs.custom</c> rows in source-document order. Duplicate
    /// names are retained because the upstream resolver applies every row.
    /// </summary>
    public ImmutableArray<SkyrimRaceMenuCustomMorphValue> OrderedCustomMorphs { get; init; } =
        OrderedCustomMorphs.IsDefault
            ? ImmutableArray<SkyrimRaceMenuCustomMorphValue>.Empty
            : OrderedCustomMorphs;
}

public sealed record PresetDocument(
    PresetFormat Format,
    GameEdition Edition,
    PresetAppearance Appearance,
    Sha256Hash SourceHash,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public sealed record PresetParseRequest(PresetFormat Format, GameEdition Edition, WorkspacePath SourcePath);

public sealed record PresetExportRequest(PresetFormat Format, GameEdition Edition,
    WorkspacePath SourcePath, WorkspacePath DestinationPath);

public sealed record PresetDiffRequest(PresetParseRequest Left, PresetParseRequest Right);

public sealed record PresetParseResult(PresetDocument? Document, ImmutableArray<Diagnostic> Diagnostics);

public static class PresetInspectionSchemas
{
    public const string ArtifactKind = "preset-inspection";
    public const string CurrentDocument = "actorwright-preset-inspection/2";
    public const string LegacyDocument = "actorwright-preset-inspection/1";
}

public sealed record PresetInspectionReceipt(
    string Schema,
    string SourcePath,
    string SourceSha256,
    string Format,
    string Edition,
    bool IsValid,
    PresetAppearance Appearance,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public static PresetInspectionReceipt From(
        PresetDocument document,
        WorkspacePath sourcePath,
        string verifiedSourceSha256)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PresetInspectionReceipt(
            PresetInspectionSchemas.CurrentDocument,
            sourcePath.Value,
            verifiedSourceSha256,
            document.Format.ToWireName(),
            document.Edition.ToWireName(),
            document.IsValid,
            document.Appearance,
            document.Diagnostics);
    }
}

public sealed record PresetExactInputDocument(
    WorkspacePath Path,
    long Size,
    string Sha256,
    ImmutableArray<byte> Utf8Json);

public sealed record PresetInspectionArtifactDocument(
    PresetInspectionReceipt Receipt,
    WorkspacePath Path,
    long Size,
    string Sha256,
    ImmutableArray<byte> Utf8Json);

public interface IPresetExactInspectionService
{
    ValueTask<PresetParseResult> InspectExactAsync(
        PresetParseRequest request,
        ReadOnlyMemory<byte> exactUtf8Json,
        Sha256Hash admittedSha256,
        CancellationToken cancellationToken);
}

public sealed record PresetExportResult(bool Written, PresetDocument Source, Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public sealed record PresetDifference(string Path, string? Left, string? Right, string Kind);

public sealed record PresetDiffResult(PresetDocument Left, PresetDocument Right,
    ImmutableArray<PresetDifference> Differences, ImmutableArray<Diagnostic> Diagnostics);

public sealed record PresetResolutionRequest(PresetIdentifier Identifier, WorkspacePath LoadOrderPath)
{
    public WorkspacePath? DataRoot { get; init; }
}

public sealed record PresetResolutionResult(PresetIdentifier Identifier, FormId? ResolvedFormId,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsResolved => ResolvedFormId is not null && !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface IPresetFormResolver
{
    ValueTask<PresetResolutionResult> ResolveAsync(PresetResolutionRequest request, CancellationToken cancellationToken);
}

public enum PresetCopySection
{
    BodyWeight,
    BodyRegions,
    BodySliders,
    Overlays,
    SkinOverride,
    LmSkinTemplate,
    Outfit,
    FaceParts,
    HairColor,
    FaceTints,
    FaceVertexMorphs,
    FaceBoneRegions,
    Sculpt,
    IsCharGenPreset
}

public static class PresetCopySectionExtensions
{
    public static string ToWireName(this PresetCopySection section) => section switch
    {
        PresetCopySection.BodyWeight => "body-weight",
        PresetCopySection.BodyRegions => "body-regions",
        PresetCopySection.BodySliders => "body-sliders",
        PresetCopySection.Overlays => "overlays",
        PresetCopySection.SkinOverride => "skin-override",
        PresetCopySection.LmSkinTemplate => "lm-skin-template",
        PresetCopySection.Outfit => "outfit",
        PresetCopySection.FaceParts => "face-parts",
        PresetCopySection.HairColor => "hair-color",
        PresetCopySection.FaceTints => "face-tints",
        PresetCopySection.FaceVertexMorphs => "face-morphs",
        PresetCopySection.FaceBoneRegions => "face-bone-regions",
        PresetCopySection.Sculpt => "sculpt",
        PresetCopySection.IsCharGenPreset => "chargen-flag",
        _ => throw new ArgumentOutOfRangeException(nameof(section))
    };

    public static bool TryParseWireName(string value, out PresetCopySection section)
    {
        foreach (var candidate in Enum.GetValues<PresetCopySection>())
        {
            if (string.Equals(candidate.ToWireName(), value, StringComparison.OrdinalIgnoreCase))
            {
                section = candidate;
                return true;
            }
        }
        section = default;
        return false;
    }
}

public sealed record PresetCopyRequest(
    PresetParseRequest Source,
    PresetParseRequest Target,
    ImmutableArray<PresetCopySection> Sections,
    WorkspacePath Destination);

public sealed record PresetCopyResult(
    bool Written,
    PresetDocument Source,
    PresetDocument Target,
    Sha256Hash? OutputHash,
    ImmutableArray<Diagnostic> Diagnostics);

public interface IPresetCopyService
{
    ValueTask<PresetCopyResult> CopyAsync(PresetCopyRequest request, CancellationToken cancellationToken);
}

public interface IPresetService
{
    ValueTask<PresetParseResult> InspectAsync(PresetParseRequest request, CancellationToken cancellationToken);

    ValueTask<PresetExportResult> ExportAsync(PresetExportRequest request, CancellationToken cancellationToken);

    ValueTask<PresetDiffResult> DiffAsync(PresetDiffRequest request, CancellationToken cancellationToken);
}
