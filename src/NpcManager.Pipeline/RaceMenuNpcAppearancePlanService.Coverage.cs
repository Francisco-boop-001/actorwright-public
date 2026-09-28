using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcAppearancePlanService
{
    private static ImmutableArray<RaceMenuNpcFieldCoverage> BuildCoverage(
        PresetAppearance appearance,
        ResolvedReferences resolved,
        ImmutableDictionary<RaceMenuNpcAppearanceField, RuntimeRouteBinding> runtimeRoutes,
        RaceMenuNpcPresetBundle bundle,
        RecordAuthorityBinding authority,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var coverage = ImmutableArray.CreateBuilder<RaceMenuNpcFieldCoverage>();
        coverage.Add(Mapped(RaceMenuNpcAppearanceField.Race, true,
            "explicit build request race"));
        coverage.Add(Mapped(RaceMenuNpcAppearanceField.Sex, true,
            "explicit build request sex"));

        var hasHeadParts = appearance.HeadParts.Length > 0;
        coverage.Add(new RaceMenuNpcFieldCoverage(RaceMenuNpcAppearanceField.HeadParts,
            hasHeadParts,
            !hasHeadParts || authority.HeadPartDispositions.Length == appearance.HeadParts.Length
                ? RaceMenuNpcFieldCoverageKind.Mapped
                : RaceMenuNpcFieldCoverageKind.Blocked,
            "complete mapped-HDPT or CharGen-baked head-part dispositions"));

        var hasHair = appearance.HairColor is not null;
        var hairMapped = resolved.HairColor is not null;
        coverage.Add(new RaceMenuNpcFieldCoverage(RaceMenuNpcAppearanceField.HairColor,
            hasHair, !hasHair || hairMapped
                ? RaceMenuNpcFieldCoverageKind.Mapped
                : RaceMenuNpcFieldCoverageKind.Blocked,
            "closed hash-bound external or deterministic output-owned CLFM authority"));
        coverage.Add(Mapped(RaceMenuNpcAppearanceField.Weight,
            appearance.Weight is not null, "RaceMenu actor.weight"));

        var hasHeadTexture = !string.IsNullOrWhiteSpace(appearance.RaceMenu?.HeadTexture);
        coverage.Add(new RaceMenuNpcFieldCoverage(RaceMenuNpcAppearanceField.HeadTexture,
            hasHeadTexture, !hasHeadTexture || resolved.HeadTexture is not null
                ? RaceMenuNpcFieldCoverageKind.Mapped
                : RaceMenuNpcFieldCoverageKind.Blocked,
            "signature-verified, hash-bound TXST provider binding"));
        coverage.Add(Baked(RaceMenuNpcAppearanceField.FaceTextures,
            (appearance.RaceMenu?.FaceTextures.Length ?? 0) > 0,
            $"CharGen NIF/head-texture authority {bundle.CharGenFaceGeom.Value}",
            bundle.ExpectedCharGenFaceGeomSha256));

        var presetCount = appearance.RaceMenu?.FaceMorphPresets.Length ?? 0;
        coverage.Add(CountedMapped(RaceMenuNpcAppearanceField.FaceMorphPresets,
            presetCount, 4, "RaceMenu morphs.presets", diagnostics));
        coverage.Add(CountedMapped(RaceMenuNpcAppearanceField.FaceMorphSliders,
            appearance.SliderMorphs.Length, 19, "RaceMenu sliderMorphs", diagnostics));

        coverage.Add(Baked(RaceMenuNpcAppearanceField.CustomMorphs,
            !appearance.OrderedCustomMorphs.IsDefaultOrEmpty, bundle.CharGenFaceGeom.Value,
            bundle.ExpectedCharGenFaceGeomSha256));
        coverage.Add(Baked(RaceMenuNpcAppearanceField.Sculpt,
            (appearance.RaceMenu?.SculptParts.Length ?? 0) > 0,
            bundle.CharGenFaceGeom.Value, bundle.ExpectedCharGenFaceGeomSha256));
        coverage.Add(new RaceMenuNpcFieldCoverage(RaceMenuNpcAppearanceField.FaceTints,
            appearance.Tints.Length > 0,
            appearance.Tints.Length == authority.TintDispositions.Length &&
            (authority.TintLayers.Length == 0 || authority.Qnam is not null)
                ? RaceMenuNpcFieldCoverageKind.Baked
                : RaceMenuNpcFieldCoverageKind.Blocked,
            $"complete mapped/baked/inactive disposition authority plus CharGen DDS {bundle.CharGenFaceTint.Value}",
            authority.ManifestSha256));

        var hasBodyMorphs = appearance.BodyMorphs.Count > 0 ||
            (appearance.RaceMenu?.BodyMorphsKeyed.Count ?? 0) > 0;
        coverage.Add(new RaceMenuNpcFieldCoverage(RaceMenuNpcAppearanceField.BodyMorphs,
            hasBodyMorphs, RaceMenuNpcFieldCoverageKind.Mapped,
            "deterministic typed BodyGen templates.ini/morphs.ini materialization bound to the fresh NPC identity",
            bundle.ExpectedPresetSha256));
        var hasOverlays = appearance.Overlays.Length > 0 ||
            (appearance.RaceMenu?.BodyOverlays.Length ?? 0) > 0;
        coverage.Add(RuntimeCoverage(RaceMenuNpcAppearanceField.Overlays,
            hasOverlays, runtimeRoutes, diagnostics));
        coverage.Add(RuntimeCoverage(RaceMenuNpcAppearanceField.NodeTransforms,
            (appearance.RaceMenu?.NodeTransforms.Length ?? 0) > 0,
            runtimeRoutes, diagnostics));
        coverage.Add(RuntimeCoverage(RaceMenuNpcAppearanceField.SkinOverrides,
            !string.IsNullOrWhiteSpace(appearance.Skin) ||
            (appearance.RaceMenu?.SkinOverrides.Length ?? 0) > 0,
            runtimeRoutes, diagnostics));

        var raceMenu = appearance.RaceMenu;
        coverage.Add(ProvenanceMetadata(RaceMenuNpcAppearanceField.ModNames,
            (raceMenu?.ModNames.Length ?? 0) > 0, bundle.ExpectedPresetSha256));
        coverage.Add(ProvenanceMetadata(RaceMenuNpcAppearanceField.Mods,
            (raceMenu?.Mods.Length ?? 0) > 0, bundle.ExpectedPresetSha256));
        coverage.Add(ProvenanceMetadata(RaceMenuNpcAppearanceField.Version,
            raceMenu?.Version is not null, bundle.ExpectedPresetSha256));

        var hasUnknowns = appearance.UnknownFields.Length > 0;
        if (hasUnknowns)
        {
            diagnostics.Add(Error("racemenu-plan-unknown-fields-blocked",
                "The .jslot contains unsupported fields without a preservation authority."));
        }
        coverage.Add(new RaceMenuNpcFieldCoverage(RaceMenuNpcAppearanceField.UnknownFields,
            hasUnknowns, RaceMenuNpcFieldCoverageKind.Blocked,
            "unsupported RaceMenu source fields"));
        return coverage.ToImmutable();
    }

    private static RaceMenuNpcFieldCoverage CountedMapped(
        RaceMenuNpcAppearanceField field,
        int actualCount,
        int requiredCount,
        string authority,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var present = actualCount > 0;
        if (present && actualCount != requiredCount)
        {
            diagnostics.Add(Error($"racemenu-plan-{field.ToWireName()}-count",
                $"{authority} must contain exactly {requiredCount} values; found {actualCount}."));
        }
        return new RaceMenuNpcFieldCoverage(field, present,
            !present || actualCount == requiredCount
                ? RaceMenuNpcFieldCoverageKind.Mapped
                : RaceMenuNpcFieldCoverageKind.Blocked,
            authority);
    }

    private static RaceMenuNpcFieldCoverage RuntimeCoverage(
        RaceMenuNpcAppearanceField field,
        bool present,
        ImmutableDictionary<RaceMenuNpcAppearanceField, RuntimeRouteBinding> runtimeRoutes,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (runtimeRoutes.TryGetValue(field, out var route))
        {
            if (!present)
            {
                diagnostics.Add(Error($"racemenu-plan-{field.ToWireName()}-route-unexpected",
                    $"Runtime route '{field.ToWireName()}' is declared for an absent .jslot field."));
                return new RaceMenuNpcFieldCoverage(field, false,
                    RaceMenuNpcFieldCoverageKind.Blocked,
                    route.ArtifactPath.Value, route.ArtifactSha256);
            }
            return new RaceMenuNpcFieldCoverage(field, present,
                RaceMenuNpcFieldCoverageKind.RuntimeDeclared,
                route.ArtifactPath.Value, route.ArtifactSha256);
        }
        if (present)
        {
            diagnostics.Add(Error($"racemenu-plan-{field.ToWireName()}-authority-missing",
                $"Present RaceMenu field '{field.ToWireName()}' requires an explicit hash-bound runtime route."));
        }
        return new RaceMenuNpcFieldCoverage(field, present,
            RaceMenuNpcFieldCoverageKind.Blocked, "no runtime route supplied");
    }

    private static RaceMenuNpcFieldCoverage Mapped(
        RaceMenuNpcAppearanceField field, bool present, string authority) =>
        new(field, present, RaceMenuNpcFieldCoverageKind.Mapped, authority);

    private static RaceMenuNpcFieldCoverage ProvenanceMetadata(
        RaceMenuNpcAppearanceField field,
        bool present,
        Sha256Hash presetHash) =>
        new(field, present, RaceMenuNpcFieldCoverageKind.Mapped,
            "hash-bound .jslot provenance validated against the qualified provider master set",
            presetHash);

    private static RaceMenuNpcFieldCoverage Baked(
        RaceMenuNpcAppearanceField field,
        bool present,
        string authority,
        Sha256Hash authorityHash) =>
        new(field, present, RaceMenuNpcFieldCoverageKind.Baked, authority, authorityHash);
}
