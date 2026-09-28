using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>The source surface covered by one explicit runtime-mapping disposition.</summary>
public enum SkyrimNpcRuntimeAppearanceSurface
{
    Overlay,
    SkinOverride,
    NodeTransform,
    BodyMorphs
}

/// <summary>
/// The complete, non-silent outcome for one RaceMenu runtime source row. Only
/// <see cref="RuntimeMapped"/> rows become VMAD data.
/// </summary>
public enum SkyrimNpcRuntimeDispositionKind
{
    RuntimeMapped,
    FaceBaked,
    FirstPersonExcluded,
    NoEffectiveOverride,
    UserOmitted,
    BodyGenExcludedFromVmad,
    BlockedUnsupported
}

public sealed record SkyrimNpcRuntimeSourceDisposition(
    SkyrimNpcRuntimeAppearanceSurface Surface,
    int SourceIndex,
    string SourceIdentity,
    SkyrimNpcRuntimeDispositionKind Kind,
    string Reason);

/// <summary>One non-face RaceMenu overlay that can be applied to an NPC by NiOverride.</summary>
public sealed record SkyrimNpcRuntimeOverlay(
    string Node,
    string Diffuse,
    string Normal,
    bool HasTint,
    int PackedTintArgb,
    bool HasAlpha,
    float Alpha,
    bool HasEmissiveColor = false,
    int PackedEmissiveColorArgb = 0,
    bool HasEmissiveMultiple = false,
    float EmissiveMultiple = 0F);

/// <summary>
/// One third-person biped-slot override. <see cref="SlotMaskBits"/> is the
/// signed Int32 reinterpretation of RaceMenu's unsigned 32-bit slot mask.
/// </summary>
public sealed record SkyrimNpcRuntimeSkinOverride(
    int SlotMaskBits,
    string Diffuse,
    string Normal,
    bool HasTint,
    int PackedTintArgb);

/// <summary>
/// One third-person node transform. Rotation is the original nine-value,
/// row-major RaceMenu matrix; it is never converted to Euler angles.
/// </summary>
public sealed record SkyrimNpcRuntimeNodeTransform(
    string Node,
    bool HasScale,
    float Scale,
    bool HasPosition,
    float PositionX,
    float PositionY,
    float PositionZ,
    bool HasRotation,
    ImmutableArray<float> RotationRowMajor,
    int ScaleMode);

/// <summary>
/// Strongly typed runtime portion of an admitted RaceMenu NPC plan. Face
/// overlays are bake-owned, body morphs are BodyGen-owned, and first-person
/// rows are explicitly excluded because the pinned NPC script always calls
/// NiOverride with <c>firstPerson=false</c>.
/// </summary>
public sealed record SkyrimNpcRuntimeAppearancePayload(
    bool IsFemale,
    ImmutableArray<SkyrimNpcRuntimeOverlay> Overlays,
    ImmutableArray<SkyrimNpcRuntimeSkinOverride> SkinOverrides,
    ImmutableArray<SkyrimNpcRuntimeNodeTransform> NodeTransforms,
    ImmutableArray<SkyrimNpcRuntimeSourceDisposition> Dispositions)
{
    public bool CanEmit =>
        !Dispositions.Any(item => item.Kind == SkyrimNpcRuntimeDispositionKind.BlockedUnsupported);

    public bool HasRuntimeRows =>
        !Overlays.IsDefaultOrEmpty || !SkinOverrides.IsDefaultOrEmpty ||
        !NodeTransforms.IsDefaultOrEmpty;
}

/// <summary>The eleven parallel overlay arrays consumed by NPCM_Manolov_ApplySSE.</summary>
public sealed record SkyrimNpcApplySseOverlayArrays(
    ImmutableArray<string> Nodes,
    ImmutableArray<string> Diffuse,
    ImmutableArray<string> Normal,
    ImmutableArray<bool> HasEmissiveColor,
    ImmutableArray<int> EmissiveColor,
    ImmutableArray<bool> HasEmissiveMultiple,
    ImmutableArray<float> EmissiveMultiple,
    ImmutableArray<bool> HasTint,
    ImmutableArray<int> Tint,
    ImmutableArray<bool> HasAlpha,
    ImmutableArray<float> Alpha);

/// <summary>The five parallel skin arrays consumed by NPCM_Manolov_ApplySSE.</summary>
public sealed record SkyrimNpcApplySseSkinArrays(
    ImmutableArray<int> Slots,
    ImmutableArray<string> Diffuse,
    ImmutableArray<string> Normal,
    ImmutableArray<bool> HasTint,
    ImmutableArray<int> Tint);

/// <summary>
/// The eighteen parallel node arrays consumed by NPCM_Manolov_ApplySSE.
/// Nine distinct rotation arrays preserve Papyrus's 128-element ceiling for
/// 128 nodes instead of exhausting it after fourteen flattened matrices.
/// </summary>
public sealed record SkyrimNpcApplySseNodeArrays(
    ImmutableArray<string> Names,
    ImmutableArray<bool> HasScale,
    ImmutableArray<float> Scale,
    ImmutableArray<bool> HasPosition,
    ImmutableArray<float> PositionX,
    ImmutableArray<float> PositionY,
    ImmutableArray<float> PositionZ,
    ImmutableArray<bool> HasRotation,
    ImmutableArray<float> RotationM0,
    ImmutableArray<float> RotationM1,
    ImmutableArray<float> RotationM2,
    ImmutableArray<float> RotationM3,
    ImmutableArray<float> RotationM4,
    ImmutableArray<float> RotationM5,
    ImmutableArray<float> RotationM6,
    ImmutableArray<float> RotationM7,
    ImmutableArray<float> RotationM8,
    ImmutableArray<int> ScaleMode);

/// <summary>
/// Complete strongly typed VMAD payload for the pinned SSE apply script.
/// Every contained array is required to be present and nonempty; empty source
/// categories are represented by a one-element, correctly typed sentinel.
/// </summary>
public sealed record SkyrimNpcApplySseVmadPayload(
    bool IsFemale,
    int SchemaVersion,
    SkyrimNpcApplySseOverlayArrays Overlays,
    SkyrimNpcApplySseSkinArrays SkinOverrides,
    SkyrimNpcApplySseNodeArrays NodeTransforms);

public static class SkyrimNpcApplySseContract
{
    public const string ScriptName = "NPCM_Manolov_ApplySSE";
    public const string NodeTransformOverrideKey = "NPCM_Manolov";
    public const int PapyrusArrayLimit = 128;
    public const int PropertyCount = 36;
}

public sealed record SkyrimNpcRuntimeProposalBinding(
    FormId NpcFormId,
    WorkspacePath Output,
    WorkspacePath? SourcePlugin = null);
