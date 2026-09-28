using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Typed JSON scalar used by the small RaceMenu value-table boundary.
/// Domain code never exposes an untyped value; the union keeps the original scalar kind visible.</summary>
public enum RaceMenuScalarKind
{
    SignedInteger,
    FloatingPoint,
    Text,
    Boolean,
    Null
}

public sealed record RaceMenuScalar(
    RaceMenuScalarKind Kind,
    long IntegerValue = 0,
    double NumberValue = 0,
    string? StringValue = null,
    bool BooleanValue = false)
{
    public static RaceMenuScalar FromInteger(long value) => new(RaceMenuScalarKind.SignedInteger, IntegerValue: value);

    public static RaceMenuScalar FromNumber(double value) => new(RaceMenuScalarKind.FloatingPoint, NumberValue: value);

    public static RaceMenuScalar FromText(string value) => new(RaceMenuScalarKind.Text, StringValue: value);

    public static RaceMenuScalar Empty() => new(RaceMenuScalarKind.Null);
}

/// <summary>A single skee value-table row. The key/type/index tuple is the
/// source-compatible identity used by RaceMenu's <c>OverrideVariant</c>.</summary>
public sealed record RaceMenuValue(int Key, int Type, int Index, RaceMenuScalar Data);

/// <summary>
/// One named RaceMenu transform value set. Names are plugin-defined routing
/// authorities (for example RSMTransform, RMX_Head, or PAF_Butt_Scale) and
/// therefore must be preserved rather than normalized by the codec.
/// </summary>
public sealed record RaceMenuTransformKeySet(
    string Name,
    ImmutableArray<RaceMenuValue> Values);

/// <summary>One validated RaceMenu node transform. Rotation is retained as the
/// nine row-major matrix floats used by the .jslot, avoiding an Euler-convention guess.</summary>
public sealed record SkyrimNodeTransform(
    string Node,
    bool FirstPerson,
    ImmutableArray<RaceMenuTransformKeySet> KeySets,
    float? Scale,
    int? ScaleMode,
    ImmutableArray<float> Position,
    ImmutableArray<float> RotationMatrix)
{
    public ImmutableArray<RaceMenuValue> Values =>
        KeySets.SelectMany(item => item.Values).ToImmutableArray();
}

/// <summary>One validated RaceMenu skin override. Texture slots retain every
/// key-9 index rather than silently discarding subsurface/specular channels.</summary>
public sealed record SkyrimSkinOverride(
    uint SlotMask,
    bool FirstPerson,
    ImmutableArray<RaceMenuValue> Values,
    ImmutableDictionary<int, string> Textures,
    ImmutableArray<float> Tint,
    float? Alpha);

/// <summary>Typed replacement for one node transform. Omitted members preserve
/// the source only when the whole section is omitted; a supplied section replaces
/// its entries deterministically.</summary>
public sealed record SkyrimNodeTransformPatch(
    string Node,
    bool FirstPerson,
    float? Scale,
    int? ScaleMode,
    ImmutableArray<float> Position,
    ImmutableArray<float> RotationMatrix,
    string KeyName = "RSMTransform");

public sealed record SkyrimSkinOverridePatch(
    uint SlotMask,
    bool FirstPerson,
    ImmutableDictionary<int, string> Textures,
    ImmutableArray<float> Tint,
    float? Alpha);

public sealed record SkyrimBodyTransformApplyRequest(
    GameEdition Edition,
    FormId NpcFormId,
    WorkspacePath PresetPath,
    WorkspacePath OutputPath,
    ImmutableArray<SkyrimNodeTransformPatch>? TransformReplacement = null,
    ImmutableArray<SkyrimSkinOverridePatch>? SkinReplacement = null,
    Sha256Hash? ExpectedInputSha256 = null);

public sealed record SkyrimBodyTransformProposal(
    GameEdition Edition,
    FormId NpcFormId,
    WorkspacePath PresetPath,
    WorkspacePath OutputPath,
    Sha256Hash InputSha256,
    Sha256Hash? OutputSha256,
    ImmutableArray<SkyrimNodeTransform> SourceTransforms,
    ImmutableArray<SkyrimSkinOverride> SourceSkinOverrides,
    ImmutableArray<SkyrimNodeTransform> EffectiveTransforms,
    ImmutableArray<SkyrimSkinOverride> EffectiveSkinOverrides,
    bool Applied,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}

public interface ISkyrimBodyTransformService
{
    ValueTask<SkyrimBodyTransformProposal> ApplyAsync(
        SkyrimBodyTransformApplyRequest request, CancellationToken cancellationToken);
}
