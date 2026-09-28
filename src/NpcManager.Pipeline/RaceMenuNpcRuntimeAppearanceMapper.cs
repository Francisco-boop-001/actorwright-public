using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Pure RaceMenu-to-NiOverride mapper for the pinned Skyrim NPC apply script.
/// It does not write a plugin, materialize BodyGen, bake FaceGen, or establish
/// runtime/visual authority.
/// </summary>
public static class RaceMenuNpcRuntimeAppearanceMapper
{
    public static SkyrimNpcRuntimeAppearancePayload Map(RaceMenuNpcAppearancePlan plan) =>
        Map(plan, null);

    public static SkyrimNpcRuntimeAppearancePayload Map(
        RaceMenuNpcAppearancePlan plan,
        RaceMenuNpcOverlayDecisionSet? overlayDecisionSet) =>
        Map(plan, overlayDecisionSet, applyBodyMorphs: true);

    public static SkyrimNpcRuntimeAppearancePayload Map(
        RaceMenuNpcAppearancePlan plan,
        RaceMenuNpcOverlayDecisionSet? overlayDecisionSet,
        bool applyBodyMorphs)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Require(plan.IsReady, "RaceMenu appearance plan is not accepted and ready.");
        Require(plan.Request.Edition == GameEdition.SkyrimSpecialEdition &&
                plan.Provider.Edition == GameEdition.SkyrimSpecialEdition &&
                plan.Preset.Edition == GameEdition.SkyrimSpecialEdition,
            "The SSE runtime mapper accepts only an admitted Skyrim SE plan.");
        Require(plan.Preset.Format == PresetFormat.RaceMenuJslot,
            "The SSE runtime mapper requires a RaceMenu .jslot plan.");
        Require(plan.Preset.SourceHash == plan.Request.PresetBundle.ExpectedPresetSha256,
            "The admitted preset hash no longer matches the typed request.");

        var appearance = plan.Preset.Appearance;
        var raceMenu = appearance.RaceMenu ??
            throw new InvalidDataException("The admitted plan has no typed RaceMenu payload.");
        var overlayDecisions = ValidateOverlayDecisions(
            plan, raceMenu, overlayDecisionSet);
        var dispositions = ImmutableArray.CreateBuilder<SkyrimNpcRuntimeSourceDisposition>();
        var overlays = MapOverlays(appearance, raceMenu, overlayDecisions, dispositions);
        var skins = MapSkinOverrides(raceMenu, dispositions);
        var nodes = MapNodeTransforms(raceMenu, dispositions);

        RequireRuntimeCoverage(plan, RaceMenuNpcAppearanceField.Overlays,
            raceMenu.BodyOverlays.Length > 0 || appearance.Overlays.Length > 0);
        RequireRuntimeCoverage(plan, RaceMenuNpcAppearanceField.SkinOverrides,
            raceMenu.SkinOverrides.Length > 0 || !string.IsNullOrWhiteSpace(appearance.Skin));
        RequireRuntimeCoverage(plan, RaceMenuNpcAppearanceField.NodeTransforms,
            raceMenu.NodeTransforms.Length > 0);
        AddBodyMorphDisposition(
            plan, appearance, raceMenu, applyBodyMorphs, dispositions);

        AddLimitDisposition(overlays.Length, SkyrimNpcRuntimeAppearanceSurface.Overlay,
            "overlays", dispositions);
        AddLimitDisposition(skins.Length, SkyrimNpcRuntimeAppearanceSurface.SkinOverride,
            "skin overrides", dispositions);
        AddLimitDisposition(nodes.Length, SkyrimNpcRuntimeAppearanceSurface.NodeTransform,
            "node transforms", dispositions);

        var isFemale = plan.Request.Traits.Sex switch
        {
            NpcSex.Female => true,
            NpcSex.Male => false,
            _ => throw new InvalidDataException("The admitted NPC sex is unsupported.")
        };
        return new SkyrimNpcRuntimeAppearancePayload(isFemale, overlays, skins, nodes,
            dispositions.ToImmutable());
    }

    private static ImmutableArray<SkyrimNpcRuntimeOverlay> MapOverlays(
        PresetAppearance appearance,
        RaceMenuPresetData raceMenu,
        Dictionary<int, RaceMenuNpcOverlayDecision> decisions,
        ImmutableArray<SkyrimNpcRuntimeSourceDisposition>.Builder dispositions)
    {
        var result = ImmutableArray.CreateBuilder<SkyrimNpcRuntimeOverlay>();
        if (!appearance.Overlays.IsDefaultOrEmpty)
        {
            dispositions.Add(Blocked(SkyrimNpcRuntimeAppearanceSurface.Overlay, -1,
                "legacy-overlays",
                "A Skyrim RaceMenu runtime route cannot represent LooksMenu-style template overlays."));
        }

        for (var index = 0; index < raceMenu.BodyOverlays.Length; index++)
        {
            var source = raceMenu.BodyOverlays[index];
            var identity = source.Node;
            if (decisions.TryGetValue(index, out var decision))
            {
                Require(string.Equals(identity, decision.Node, StringComparison.Ordinal),
                    $"Overlay omission row {index} expected node '{decision.Node}' but the admitted preset contains '{identity}'.");
                var actualTexture = CanonicalTexturePath(source.Diffuse ?? string.Empty);
                Require(string.Equals(actualTexture.Value, decision.ExpectedTexture.Value,
                        StringComparison.OrdinalIgnoreCase),
                    $"Overlay omission row {index} expected texture '{decision.ExpectedTexture.Value}' but the admitted preset contains '{actualTexture.Value}'.");
                Require(decision.Action == RaceMenuNpcOverlayDecisionAction.Omit,
                    $"Overlay decision row {index} has an unsupported action.");
                dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
                    SkyrimNpcRuntimeAppearanceSurface.Overlay, index, identity,
                    SkyrimNpcRuntimeDispositionKind.UserOmitted,
                    $"User omission of normalized texture '{actualTexture.Value}': {decision.Reason} Evidence SHA256: {decision.UserDecisionEvidence.ExpectedSha256}."));
                continue;
            }
            if (IsDocumentedFaceOverlayNode(identity))
            {
                dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
                    SkyrimNpcRuntimeAppearanceSurface.Overlay, index, identity,
                    SkyrimNpcRuntimeDispositionKind.FaceBaked,
                    "SSE face overlays belong exclusively to the admitted FaceGen bake and are never emitted to VMAD."));
                continue;
            }
            if (IsFaceLikeNode(identity))
            {
                dispositions.Add(Blocked(SkyrimNpcRuntimeAppearanceSurface.Overlay, index,
                    identity,
                    "The row is face-like but does not match a documented Face [OvlN]/Face [SOvlN] node; fail-closed mapping will not emit it."));
                continue;
            }
            if (IsInactiveLegacyOverlay(source))
            {
                dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
                    SkyrimNpcRuntimeAppearanceSurface.Overlay, index, identity,
                    SkyrimNpcRuntimeDispositionKind.NoEffectiveOverride,
                    "The legacy body-overlay row has exact alpha zero and only zero-valued controls, so it cannot affect the rendered actor."));
                continue;
            }
            if (HasUnsupportedOverlayValue(source))
            {
                dispositions.Add(Blocked(SkyrimNpcRuntimeAppearanceSurface.Overlay, index,
                    identity,
                    "The overlay contains a RaceMenu key or texture index absent from the pinned SSE VMAD schema."));
                continue;
            }

            var hasTint = !source.Tint.IsDefaultOrEmpty;
            var hasAlpha = source.Alpha.HasValue;
            var emissiveColor = source.Values.SingleOrDefault(item => item.Key == 0);
            var hasEmissiveColor = emissiveColor is not null;
            var packedEmissiveColor = hasEmissiveColor
                ? checked((int)emissiveColor!.Data.IntegerValue)
                : 0;
            var emissiveMultiple = source.Values.SingleOrDefault(item => item.Key == 1);
            var hasEmissiveMultiple = emissiveMultiple is not null;
            var emissiveMultipleValue = hasEmissiveMultiple
                ? ToFiniteSingle(emissiveMultiple!.Data)
                : 0F;
            var diffuse = source.Diffuse ?? string.Empty;
            var normal = source.Normal ?? string.Empty;
            if (diffuse.Length == 0 && normal.Length == 0 && !hasTint && !hasAlpha &&
                !hasEmissiveColor && !hasEmissiveMultiple)
            {
                dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
                    SkyrimNpcRuntimeAppearanceSurface.Overlay, index, identity,
                    SkyrimNpcRuntimeDispositionKind.NoEffectiveOverride,
                    "The pinned emitter skips overlay rows with no texture, emissive, tint, or alpha value."));
                continue;
            }

            result.Add(new SkyrimNpcRuntimeOverlay(identity, diffuse, normal, hasTint,
                hasTint ? PackTint(source.Tint) : 0, hasAlpha, source.Alpha ?? 1F,
                hasEmissiveColor, packedEmissiveColor, hasEmissiveMultiple,
                emissiveMultipleValue));
            dispositions.Add(Mapped(SkyrimNpcRuntimeAppearanceSurface.Overlay, index, identity));
        }
        return result.ToImmutable();
    }

    internal static bool IsInactiveLegacyOverlay(RaceMenuBodyOverlay source)
    {
        if (source.Alpha != 0F ||
            (!source.Tint.IsDefaultOrEmpty && source.Tint.Any(value => value != 0F)) ||
            !string.IsNullOrEmpty(source.Normal))
            return false;

        foreach (RaceMenuValue value in source.Values)
        {
            if (value is { Key: 9, Type: 2, Index: 0 } &&
                value.Data.Kind == RaceMenuScalarKind.Text &&
                string.Equals(value.Data.StringValue, source.Diffuse,
                    StringComparison.Ordinal))
                continue;

            if (value.Key is not (0 or 2 or 3 or 7 or 8) ||
                value.Data.Kind is not (RaceMenuScalarKind.SignedInteger or
                    RaceMenuScalarKind.FloatingPoint) ||
                value.Data.IntegerValue != 0 ||
                value.Data.NumberValue != 0D)
                return false;
        }
        return true;
    }

    private static Dictionary<int, RaceMenuNpcOverlayDecision> ValidateOverlayDecisions(
        RaceMenuNpcAppearancePlan plan,
        RaceMenuPresetData raceMenu,
        RaceMenuNpcOverlayDecisionSet? decisionSet)
    {
        if (decisionSet is null) return new Dictionary<int, RaceMenuNpcOverlayDecision>();
        Require(decisionSet.PresetSha256 == plan.Preset.SourceHash &&
                decisionSet.PresetSha256 == plan.Request.PresetBundle.ExpectedPresetSha256,
            "Overlay decisions are bound to a different RaceMenu preset hash.");
        var decisions = decisionSet.Decisions.IsDefault
            ? ImmutableArray<RaceMenuNpcOverlayDecision>.Empty
            : decisionSet.Decisions;
        Require(decisions.Length <= raceMenu.BodyOverlays.Length,
            "Overlay decisions outnumber the admitted preset's body-overlay rows.");
        var result = new Dictionary<int, RaceMenuNpcOverlayDecision>();
        foreach (var decision in decisions)
        {
            Require(decision.SourceIndex >= 0 &&
                    decision.SourceIndex < raceMenu.BodyOverlays.Length,
                $"Overlay decision sourceIndex {decision.SourceIndex} is outside the admitted preset.");
            Require(result.TryAdd(decision.SourceIndex, decision),
                $"Overlay decision sourceIndex {decision.SourceIndex} occurs more than once.");
            Require(!string.IsNullOrWhiteSpace(decision.Node) && !decision.Node.Contains('\0'),
                $"Overlay decision sourceIndex {decision.SourceIndex} has an invalid node.");
            Require(!string.IsNullOrWhiteSpace(decision.Reason) && !decision.Reason.Contains('\0'),
                $"Overlay decision sourceIndex {decision.SourceIndex} has no reason.");
            Require(decision.ExpectedTexture.Value.StartsWith("Textures/",
                    StringComparison.OrdinalIgnoreCase),
                $"Overlay decision sourceIndex {decision.SourceIndex} expectedTexture is not Data-relative under Textures.");
        }
        return result;
    }

    private static AssetPath CanonicalTexturePath(string value)
    {
        Require(!string.IsNullOrWhiteSpace(value),
            "An omitted overlay row must contain the declared primary texture.");
        string candidate = value.Trim().Replace('\\', '/').ToLowerInvariant();
        if (candidate.StartsWith('/')) candidate = candidate[1..];
        try
        {
            var normalized = new AssetPath(candidate);
            return normalized.Value.StartsWith("textures/", StringComparison.Ordinal)
                ? normalized
                : new AssetPath($"textures/{normalized.Value}");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Overlay texture '{value}' must be a relative forward-slash asset path after normalizing one leading separator.", exception);
        }
    }

    private static ImmutableArray<SkyrimNpcRuntimeSkinOverride> MapSkinOverrides(
        RaceMenuPresetData raceMenu,
        ImmutableArray<SkyrimNpcRuntimeSourceDisposition>.Builder dispositions)
    {
        var result = ImmutableArray.CreateBuilder<SkyrimNpcRuntimeSkinOverride>();
        for (var index = 0; index < raceMenu.SkinOverrides.Length; index++)
        {
            var source = raceMenu.SkinOverrides[index];
            var identity = $"0x{source.SlotMask:X8}";
            if (source.FirstPerson)
            {
                dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
                    SkyrimNpcRuntimeAppearanceSurface.SkinOverride, index, identity,
                    SkyrimNpcRuntimeDispositionKind.FirstPersonExcluded,
                    "NPCM_Manolov_ApplySSE always calls skin APIs with firstPerson=false; the first-person row is not misapplied to the NPC skeleton."));
                continue;
            }
            if (source.SlotMask == 0)
            {
                dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
                    SkyrimNpcRuntimeAppearanceSurface.SkinOverride, index, identity,
                    SkyrimNpcRuntimeDispositionKind.NoEffectiveOverride,
                    "The pinned emitter treats slot mask zero as its non-applying sentinel."));
                continue;
            }
            if (source.Alpha.HasValue || source.Textures.Keys.Any(key => key is not (0 or 1)) ||
                source.Values.Any(value => !IsSupportedSkinValue(value)))
            {
                dispositions.Add(Blocked(SkyrimNpcRuntimeAppearanceSurface.SkinOverride,
                    index, identity,
                    "The pinned SSE script has no skin-alpha property and supports only diffuse index 0, normal index 1, and tint key 7."));
                continue;
            }

            var diffuse = source.Textures.GetValueOrDefault(0) ?? string.Empty;
            var normal = source.Textures.GetValueOrDefault(1) ?? string.Empty;
            var hasTint = !source.Tint.IsDefaultOrEmpty;
            if (diffuse.Length == 0 && normal.Length == 0 && !hasTint)
            {
                dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
                    SkyrimNpcRuntimeAppearanceSurface.SkinOverride, index, identity,
                    SkyrimNpcRuntimeDispositionKind.NoEffectiveOverride,
                    "The pinned emitter skips skin rows with no diffuse, normal, or tint value."));
                continue;
            }

            result.Add(new SkyrimNpcRuntimeSkinOverride(unchecked((int)source.SlotMask),
                diffuse, normal, hasTint, hasTint ? PackTint(source.Tint) : 0));
            dispositions.Add(Mapped(SkyrimNpcRuntimeAppearanceSurface.SkinOverride, index,
                identity));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<SkyrimNpcRuntimeNodeTransform> MapNodeTransforms(
        RaceMenuPresetData raceMenu,
        ImmutableArray<SkyrimNpcRuntimeSourceDisposition>.Builder dispositions)
    {
        var result = ImmutableArray.CreateBuilder<SkyrimNpcRuntimeNodeTransform>();
        for (var index = 0; index < raceMenu.NodeTransforms.Length; index++)
        {
            var source = raceMenu.NodeTransforms[index];
            if (source.FirstPerson)
            {
                dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
                    SkyrimNpcRuntimeAppearanceSurface.NodeTransform, index, source.Node,
                    SkyrimNpcRuntimeDispositionKind.FirstPersonExcluded,
                    "NPCM_Manolov_ApplySSE always calls node APIs with firstPerson=false; the first-person row is not misapplied to the NPC skeleton."));
                continue;
            }

            var activeKeySets = source.KeySets
                .Where(item => !item.Values.IsDefaultOrEmpty)
                .ToImmutableArray();
            var selectedKeySetIsRepresented = activeKeySets.Length == 1 &&
                (source.KeySets.Length == 1 || string.Equals(activeKeySets[0].Name,
                    "RSMTransform", StringComparison.Ordinal));
            if (activeKeySets.Length > 1 ||
                (activeKeySets.Length == 1 && !selectedKeySetIsRepresented))
            {
                dispositions.Add(Blocked(SkyrimNpcRuntimeAppearanceSurface.NodeTransform,
                    index, source.Node,
                    "Multiple named transform value sets cannot be losslessly represented by the script's fixed NPCM_Manolov override key."));
                continue;
            }

            var hasScale = source.Scale.HasValue;
            var hasPosition = !source.Position.IsDefaultOrEmpty;
            var hasRotation = !source.RotationMatrix.IsDefaultOrEmpty;
            if (!hasScale && !hasPosition && !hasRotation)
            {
                dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
                    SkyrimNpcRuntimeAppearanceSurface.NodeTransform, index, source.Node,
                    SkyrimNpcRuntimeDispositionKind.NoEffectiveOverride,
                    "The pinned emitter skips rows without scale, position, or rotation; scale-mode alone is not emitted."));
                continue;
            }

            if (activeKeySets.Length != 1 || source.Position.Length is not (0 or 3) ||
                source.RotationMatrix.Length is not (0 or 9) ||
                source.ScaleMode is < 0 or > 3)
            {
                dispositions.Add(Blocked(SkyrimNpcRuntimeAppearanceSurface.NodeTransform,
                    index, source.Node,
                    "The transform is not a complete scale, three-value position, or nine-value row-major rotation payload."));
                continue;
            }

            result.Add(new SkyrimNpcRuntimeNodeTransform(
                source.Node,
                hasScale,
                source.Scale ?? 1F,
                hasPosition,
                hasPosition ? source.Position[0] : 0F,
                hasPosition ? source.Position[1] : 0F,
                hasPosition ? source.Position[2] : 0F,
                hasRotation,
                hasRotation ? source.RotationMatrix : ImmutableArray.CreateRange(new float[9]),
                source.ScaleMode ?? -1));
            dispositions.Add(Mapped(SkyrimNpcRuntimeAppearanceSurface.NodeTransform, index,
                source.Node));
        }
        return result.ToImmutable();
    }

    private static void AddBodyMorphDisposition(
        RaceMenuNpcAppearancePlan plan,
        PresetAppearance appearance,
        RaceMenuPresetData raceMenu,
        bool applyBodyMorphs,
        ImmutableArray<SkyrimNpcRuntimeSourceDisposition>.Builder dispositions)
    {
        var present = appearance.BodyMorphs.Count > 0 || raceMenu.BodyMorphsKeyed.Count > 0;
        if (!present) return;
        var coverage = SingleCoverage(plan, RaceMenuNpcAppearanceField.BodyMorphs);
        Require(coverage.Present &&
                coverage.Classification is not RaceMenuNpcFieldCoverageKind.RuntimeDeclared and
                    not RaceMenuNpcFieldCoverageKind.Blocked,
            "Body morphs must have a concrete non-VMAD BodyGen route before runtime mapping.");
        if (!applyBodyMorphs)
        {
            dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
                SkyrimNpcRuntimeAppearanceSurface.BodyMorphs, -1, "body-morphs",
                SkyrimNpcRuntimeDispositionKind.UserOmitted,
                "The operator disabled the preset loader's Apply BodySlide choice; no BodyGen morph is emitted."));
            return;
        }
        dispositions.Add(new SkyrimNpcRuntimeSourceDisposition(
            SkyrimNpcRuntimeAppearanceSurface.BodyMorphs, -1, "body-morphs",
            SkyrimNpcRuntimeDispositionKind.BodyGenExcludedFromVmad,
            "Body morphs are intentionally excluded from VMAD and remain owned by the admitted BodyGen templates.ini/morphs.ini route."));
    }

    private static void RequireRuntimeCoverage(
        RaceMenuNpcAppearancePlan plan,
        RaceMenuNpcAppearanceField field,
        bool present)
    {
        var coverage = SingleCoverage(plan, field);
        Require(coverage.Present == present,
            $"Appearance coverage for '{field.ToWireName()}' disagrees with the admitted preset.");
        if (present)
        {
            Require(coverage.Classification == RaceMenuNpcFieldCoverageKind.RuntimeDeclared &&
                    coverage.AuthoritySha256 is not null,
                $"Present field '{field.ToWireName()}' lacks a hash-bound runtime-route declaration.");
        }
    }

    private static RaceMenuNpcFieldCoverage SingleCoverage(
        RaceMenuNpcAppearancePlan plan,
        RaceMenuNpcAppearanceField field)
    {
        var matches = plan.FieldCoverage.Where(item => item.Field == field).ToArray();
        Require(matches.Length == 1,
            $"Appearance coverage must contain exactly one '{field.ToWireName()}' row.");
        return matches[0];
    }

    private static bool HasUnsupportedOverlayValue(RaceMenuBodyOverlay overlay) =>
        overlay.Values.GroupBy(value => (value.Key, value.Type, value.Index))
            .Any(group => group.Count() > 1) ||
        overlay.Values.Any(value =>
            value.Key switch
            {
                0 => value.Type != 3 || value.Index != -1 ||
                     value.Data.Kind != RaceMenuScalarKind.SignedInteger ||
                     value.Data.IntegerValue is < int.MinValue or > int.MaxValue,
                1 => value.Type != 4 || value.Index != -1 ||
                     !TryReadFiniteSingle(value.Data, out _),
                9 => value.Type != 2 || value.Index is not (0 or 1) ||
                     value.Data.Kind != RaceMenuScalarKind.Text,
                7 => value.Type != 3 || value.Index != -1 ||
                     value.Data.Kind != RaceMenuScalarKind.SignedInteger,
                8 => value.Type != 4 || value.Index != -1 ||
                     !TryReadFiniteSingle(value.Data, out _),
                _ => true
            });

    private static float ToFiniteSingle(RaceMenuScalar scalar)
    {
        if (!TryReadFiniteSingle(scalar, out var value))
            throw new InvalidDataException("RaceMenu floating override is not a finite Single.");
        return value;
    }

    private static bool TryReadFiniteSingle(RaceMenuScalar scalar, out float value)
    {
        var number = scalar.Kind switch
        {
            RaceMenuScalarKind.FloatingPoint => scalar.NumberValue,
            RaceMenuScalarKind.SignedInteger => scalar.IntegerValue,
            _ => double.NaN
        };
        value = (float)number;
        return double.IsFinite(number) && float.IsFinite(value);
    }

    private static bool IsSupportedSkinValue(RaceMenuValue value) =>
        value.Key switch
        {
            9 => value.Type == 2 && value.Index is 0 or 1 &&
                 value.Data.Kind == RaceMenuScalarKind.Text,
            7 => value.Type == 3 && value.Index == -1 &&
                 value.Data.Kind == RaceMenuScalarKind.SignedInteger,
            _ => false
        };

    private static bool IsDocumentedFaceOverlayNode(string node)
    {
        const string normalPrefix = "Face [Ovl";
        const string spellPrefix = "Face [SOvl";
        var prefixLength = node.StartsWith(normalPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalPrefix.Length
            : node.StartsWith(spellPrefix, StringComparison.OrdinalIgnoreCase)
                ? spellPrefix.Length
                : -1;
        return prefixLength >= 0 && node.EndsWith(']') &&
               node.AsSpan(prefixLength, node.Length - prefixLength - 1)
                   .IndexOfAnyExceptInRange('0', '9') < 0 &&
               node.Length > prefixLength + 1;
    }

    private static bool IsFaceLikeNode(string node) =>
        node.StartsWith("Face", StringComparison.OrdinalIgnoreCase);

    private static int PackTint(ImmutableArray<float> tint)
    {
        Require(tint.Length == 4 && tint.All(value => float.IsFinite(value)),
            "RaceMenu tint must contain four finite RGBA channels.");
        var red = ToByte(tint[0]);
        var green = ToByte(tint[1]);
        var blue = ToByte(tint[2]);
        var alpha = ToByte(tint[3]);
        var bits = (uint)(alpha << 24 | red << 16 | green << 8 | blue);
        return unchecked((int)bits);
    }

    private static int ToByte(float value) =>
        (int)Math.Round(Math.Clamp(value, 0F, 1F) * 255F,
            MidpointRounding.ToEven);

    private static void AddLimitDisposition(
        int count,
        SkyrimNpcRuntimeAppearanceSurface surface,
        string identity,
        ImmutableArray<SkyrimNpcRuntimeSourceDisposition>.Builder dispositions)
    {
        if (count <= SkyrimNpcApplySseContract.PapyrusArrayLimit) return;
        dispositions.Add(Blocked(surface, -1, identity,
            $"The mapped category contains {count} rows and exceeds Skyrim Papyrus's 128-element array limit."));
    }

    private static SkyrimNpcRuntimeSourceDisposition Mapped(
        SkyrimNpcRuntimeAppearanceSurface surface,
        int sourceIndex,
        string identity) =>
        new(surface, sourceIndex, identity, SkyrimNpcRuntimeDispositionKind.RuntimeMapped,
            "Mapped to the pinned third-person NPCM_Manolov_ApplySSE property set.");

    private static SkyrimNpcRuntimeSourceDisposition Blocked(
        SkyrimNpcRuntimeAppearanceSurface surface,
        int sourceIndex,
        string identity,
        string reason) =>
        new(surface, sourceIndex, identity,
            SkyrimNpcRuntimeDispositionKind.BlockedUnsupported, reason);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
