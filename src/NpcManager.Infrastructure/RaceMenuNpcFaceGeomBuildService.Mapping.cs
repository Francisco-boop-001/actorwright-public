using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class RaceMenuNpcFaceGeomBuildService
{
    private static ImmutableArray<FormReference> DeriveGeometrySelectedRoots(
        RaceMenuNpcAppearancePlan plan,
        SkyrimFaceBakeAuthority authority,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = ImmutableArray.CreateBuilder<FormReference>();
        // ResolvedHeadParts contains mapped PNAM roots only. Baked-only source
        // rows remain distinct dispositions and do not claim an HDPT carrier.
        var recordOnly = new HashSet<string>(
            authority.RecordOnlyMappedHeadParts.Select(FormKey),
            StringComparer.OrdinalIgnoreCase);
        var encounteredRecordOnly = ImmutableArray.CreateBuilder<FormReference>();
        foreach (RaceMenuResolvedHeadPart mapped in plan.ResolvedHeadParts)
        {
            FormReference reference = mapped.Binding.Reference;
            string key = FormKey(reference);
            if (!seen.Add(key))
            {
                diagnostics.Add(Error("facegeom-selected-root-duplicate",
                    $"Mapped headpart '{reference}' occurs more than once."));
                continue;
            }

            int carrierCount = authority.CarrierShapes.Count(item =>
                SameForm(item.HeadPart, reference));
            if (carrierCount == 0)
            {
                if (recordOnly.Contains(key))
                {
                    encounteredRecordOnly.Add(reference);
                }
                else
                {
                    diagnostics.Add(Error("facegeom-selected-root-authority-missing",
                        $"Mapped headpart '{reference}' has neither FaceGeom carrier authority " +
                        "nor an explicit record-only authority. Mapped HDPT rows may not be " +
                        "silently omitted from the geometry route."));
                }
                continue;
            }

            // One HDPT may legitimately own more than one carrier shape. The
            // selected-root route therefore contains the HDPT once while the
            // authority retains every independently bound carrier shape.
            selected.Add(reference);
        }

        if (!encounteredRecordOnly.SequenceEqual(
                authority.RecordOnlyMappedHeadParts, FormReferenceComparer.Instance))
        {
            diagnostics.Add(Error("facegeom-record-only-headpart-closure",
                "Mapped record-only HDPT rows do not exactly match authority order and identity."));
        }

        if (selected.Count == 0)
        {
            diagnostics.Add(Error("facegeom-selected-root-empty",
                "No mapped preset headpart has a FaceGeom carrier authority."));
        }
        return selected.ToImmutable();
    }

    private static ImmutableArray<SkyrimFaceRecordHeadPartSelection> BuildRecordSelections(
        SkyrimFaceBakeAuthority authority,
        ImmutableArray<FormReference> selectedRoots,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Dictionary<string, SkyrimFaceBakeShapeTriAuthority> triByShape =
            authority.ShapeTriInputs.ToDictionary(item => item.CarrierShapeName,
                StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<SkyrimFaceRecordHeadPartSelection>();
        foreach (FormReference selected in selectedRoots)
        {
            SkyrimFaceBakeCarrierShapeAuthority[] carriers = authority.CarrierShapes
                .Where(item => SameForm(item.HeadPart, selected)).ToArray();
            if (carriers.Length == 0)
            {
                diagnostics.Add(Error("facegeom-selected-root-unmapped",
                    $"Selected root '{selected}' has no authority carrier mapping."));
                continue;
            }
            var roles = new HashSet<SkyrimHdptTriRole>();
            foreach (SkyrimFaceBakeCarrierShapeAuthority carrier in carriers)
            {
                if (!triByShape.TryGetValue(carrier.CarrierShapeName,
                        out SkyrimFaceBakeShapeTriAuthority? tri))
                {
                    diagnostics.Add(Error("facegeom-selected-root-tri-row",
                        $"Carrier '{carrier.CarrierShapeName}' has no TRI authority row."));
                    continue;
                }
                if (tri.RaceMorphTri is not null) roles.Add(SkyrimHdptTriRole.RaceMorph);
                if (tri.MeshMorphTri is not null) roles.Add(SkyrimHdptTriRole.Mesh);
                if (tri.ChargenMorphTri is not null) roles.Add(SkyrimHdptTriRole.CharGen);
            }
            result.Add(new SkyrimFaceRecordHeadPartSelection(selected,
                roles.Order().ToImmutableArray()));
        }
        return result.ToImmutable();
    }

    private static void ValidateAuthorityAgainstRecordRoute(
        SkyrimFaceBakeAuthority authority,
        SkyrimFaceRecordRoute route,
        FormReference expectedRace,
        ImmutableArray<FormReference> selectedRoots,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!SameForm(route.Race.Reference, expectedRace))
        {
            diagnostics.Add(Error("facegeom-record-race",
                "The resolved race differs from the accepted appearance plan."));
        }

        string[] carrierNames = authority.CarrierShapes
            .Select(item => item.CarrierShapeName).ToArray();
        string[] carrierForms = authority.CarrierShapes
            .Select(item => FormKey(item.HeadPart)).ToArray();
        string[] routeForms = route.HeadParts.Select(item => FormKey(item.Reference)).ToArray();
        if (carrierNames.Distinct(StringComparer.Ordinal).Count() != carrierNames.Length ||
            routeForms.Distinct(StringComparer.OrdinalIgnoreCase).Count() != routeForms.Length ||
            !carrierForms.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(routeForms))
        {
            diagnostics.Add(Error("facegeom-carrier-record-closure",
                "Authority carriers and the resolved selected/default/HNAM record graph are not an exact closed mapping."));
        }

        foreach (FormReference selected in selectedRoots)
        {
            SkyrimFaceHeadPartRecordRoute[] matches = route.HeadParts
                .Where(item => SameForm(item.Reference, selected)).ToArray();
            if (matches.Length != 1 || !matches[0].IsSelected)
            {
                diagnostics.Add(Error("facegeom-selected-record-root",
                    $"Derived root '{selected}' is not exactly one explicitly selected HDPT route."));
            }
        }

        Dictionary<string, SkyrimFaceBakeShapeTriAuthority> triByShape =
            authority.ShapeTriInputs.ToDictionary(item => item.CarrierShapeName,
                StringComparer.Ordinal);
        foreach (SkyrimFaceBakeCarrierShapeAuthority carrier in authority.CarrierShapes)
        {
            SkyrimFaceHeadPartRecordRoute[] records = route.HeadParts
                .Where(item => SameForm(item.Reference, carrier.HeadPart)).ToArray();
            if (records.Length != 1)
            {
                diagnostics.Add(Error("facegeom-carrier-record-unmapped",
                    $"Carrier '{carrier.CarrierShapeName}' maps to {records.Length} routed HDPT records."));
                continue;
            }
            SkyrimFaceHeadPartRecordRoute record = records[0];
            SkyrimFaceRecordPluginAuthority[] providers = authority.RecordPluginAuthorities
                .Where(item => string.Equals(item.Plugin.Value,
                    record.Provider.Plugin.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (providers.Length != 1 || providers[0].Path != record.Provider.Path ||
                providers[0].ExpectedSha256 != record.Provider.Sha256)
            {
                diagnostics.Add(Error("facegeom-record-provider-drift",
                    $"Routed HDPT '{record.Reference}' is not backed by its exact manifest plugin provider."));
            }
            if (!string.Equals(carrier.ModelNif.AssetPath.Value, record.ModelNif.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("facegeom-model-route-drift",
                    $"Carrier '{carrier.CarrierShapeName}' model '{carrier.ModelNif.AssetPath}' differs from HDPT MODL '{record.ModelNif}'."));
            }
            if (!triByShape.TryGetValue(carrier.CarrierShapeName,
                    out SkyrimFaceBakeShapeTriAuthority? tri))
            {
                diagnostics.Add(Error("facegeom-tri-row-unmapped",
                    $"Carrier '{carrier.CarrierShapeName}' has no TRI authority row."));
                continue;
            }
            ValidateTriRoles(record, tri, diagnostics);
        }
    }

    private static void ValidateTriRoles(
        SkyrimFaceHeadPartRecordRoute record,
        SkyrimFaceBakeShapeTriAuthority tri,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var declared = new Dictionary<SkyrimHdptTriRole, AssetPath>();
        foreach (SkyrimHdptTriRoute route in record.TriRoutes)
        {
            if (!declared.TryAdd(route.Role, route.Path))
            {
                diagnostics.Add(Error("facegeom-record-tri-role-duplicate",
                    $"HDPT '{record.Reference}' declares TRI role '{route.Role}' more than once."));
            }
        }
        var admitted = new Dictionary<SkyrimHdptTriRole, SkyrimFaceBakeAuthorityAsset>();
        if (tri.RaceMorphTri is not null) admitted.Add(SkyrimHdptTriRole.RaceMorph, tri.RaceMorphTri);
        if (tri.MeshMorphTri is not null) admitted.Add(SkyrimHdptTriRole.Mesh, tri.MeshMorphTri);
        if (tri.ChargenMorphTri is not null) admitted.Add(SkyrimHdptTriRole.CharGen, tri.ChargenMorphTri);
        if (declared.Count != admitted.Count)
        {
            diagnostics.Add(Error("facegeom-record-tri-role-closure",
                $"HDPT '{record.Reference}' TRI roles and authority roles differ."));
        }
        foreach ((SkyrimHdptTriRole role, SkyrimFaceBakeAuthorityAsset asset) in admitted)
        {
            if (!declared.TryGetValue(role, out AssetPath path) ||
                !string.Equals(path.Value, asset.AssetPath.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("facegeom-record-tri-path-drift",
                    $"HDPT '{record.Reference}' role '{role}' does not declare authority TRI '{asset.AssetPath}'."));
            }
        }
    }

    private SkyrimRaceMenuFaceBakeRequest? BuildBakeRequest(
        RaceMenuNpcFaceGeomBuildRequest request,
        SkyrimFaceBakeAuthority authority,
        SkyrimFaceRecordRoute route,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var geometryByAsset = new Dictionary<string, SseSelectedHeadpartNifGeometryDocument>(
            StringComparer.Ordinal);
        var bindings = ImmutableArray.CreateBuilder<SkyrimRaceMenuFaceBakeCarrierShapeBinding>();
        var triInputs = ImmutableArray.CreateBuilder<SkyrimRaceMenuFaceBakeShapeTriInputs>();
        Dictionary<string, SkyrimFaceBakeShapeTriAuthority> triByShape =
            authority.ShapeTriInputs.ToDictionary(item => item.CarrierShapeName,
                StringComparer.Ordinal);

        foreach (SkyrimFaceBakeCarrierShapeAuthority carrier in authority.CarrierShapes)
        {
            if (!geometryByAsset.TryGetValue(carrier.ModelNif.Id,
                    out SseSelectedHeadpartNifGeometryDocument? document))
            {
                SseSelectedHeadpartNifGeometryReadResult read = _geometryReader.Read(
                    new SseSelectedHeadpartNifGeometryReadRequest(
                        carrier.ModelNif.AssetPath,
                        carrier.ModelNif.ContentSha256,
                        carrier.ModelNif.Content));
                Append(diagnostics, read.Diagnostics);
                if (!read.Accepted || read.Document is null)
                {
                    AddMissingAcceptedPayloadDiagnostic(read.Accepted, read.Document,
                        "facegeom-model-geometry-payload", diagnostics);
                    continue;
                }
                document = read.Document;
                geometryByAsset.Add(carrier.ModelNif.Id, document);
            }

            SseSelectedHeadpartNifRestShape[] shapes = document.Shapes
                .Where(item => string.Equals(item.Name, carrier.ModelShapeName,
                    StringComparison.Ordinal)).ToArray();
            if (shapes.Length != 1)
            {
                diagnostics.Add(Error("facegeom-model-shape-unmapped",
                    $"Model '{carrier.ModelNif.AssetPath}' contains {shapes.Length} exact shapes named '{carrier.ModelShapeName}'."));
                continue;
            }
            SseSelectedHeadpartNifRestShape shape = shapes[0];
            if (shape.PackedPositionSha256 != carrier.ExpectedModelPositionSha256 ||
                shape.TopologySha256 != carrier.ExpectedModelTopologySha256 ||
                shape.TopologySha256 != carrier.ExpectedCarrierTopologySha256)
            {
                diagnostics.Add(Error("facegeom-model-geometry-drift",
                    $"Model shape '{carrier.ModelShapeName}' does not match its exact position/topology authority."));
            }
            if (!triByShape.TryGetValue(carrier.CarrierShapeName,
                    out SkyrimFaceBakeShapeTriAuthority? tri))
            {
                diagnostics.Add(Error("facegeom-bake-tri-row",
                    $"Carrier '{carrier.CarrierShapeName}' has no TRI input row."));
                continue;
            }

            bindings.Add(new SkyrimRaceMenuFaceBakeCarrierShapeBinding(
                carrier.CarrierShapeName,
                tri.ChargenMorphTri?.AssetPath,
                shape.VertexCount,
                carrier.ExpectedCarrierTopologySha256,
                carrier.ExpectedModelPositionSha256,
                shape.RestPositions));
            triInputs.Add(new SkyrimRaceMenuFaceBakeShapeTriInputs(
                carrier.CarrierShapeName,
                ToTriRequest(tri.RaceMorphTri),
                ToTriRequest(tri.ChargenMorphTri),
                ToTriRequest(tri.MeshMorphTri),
                tri.ExtendedMorphTris.Select(item => ToTriRequest(item)!).ToImmutableArray()));
        }

        PresetWeight? weight = request.AppearancePlan.Preset.Appearance.Weight;
        if (weight is null || !float.IsFinite(weight.Value))
        {
            diagnostics.Add(Error("facegeom-actor-weight",
                "The accepted preset must carry a finite actor weight for SkinnyMorph."));
        }
        if (HasErrors(diagnostics))
        {
            return null;
        }

        ImmutableArray<SkyrimRaceMenuCatalogAsset> catalogAssets = authority.CatalogConfigs
            .Select(item => new SkyrimRaceMenuCatalogAsset(
                item.Asset.AssetPath, item.Asset.ContentSha256, item.Asset.Content))
            .ToImmutableArray();
        ImmutableArray<string> keywords = route.Race.Keywords
            .Select(item => item.EditorId).ToImmutableArray();
        ImmutableArray<RaceMenuSculptPart> sculpt =
            request.AppearancePlan.Preset.Appearance.RaceMenu?.SculptParts ??
            ImmutableArray<RaceMenuSculptPart>.Empty;
        return new SkyrimRaceMenuFaceBakeRequest(
            route.Race.MorphRaceEditorId,
            request.AppearancePlan.Request.Traits.Sex == NpcSex.Female,
            request.NativeMorphs,
            keywords,
            request.OrderedCustomMorphs,
            sculpt,
            weight!.Value,
            new SkyrimRaceMenuCatalogParseRequest(authority.LoadedPlugins, catalogAssets),
            bindings.ToImmutable(),
            triInputs.ToImmutable(),
            authority.OptionalUnavailableExtensions);
    }

    private static SseTriHeadReadRequest? ToTriRequest(SkyrimFaceBakeAuthorityAsset? asset) =>
        asset is null
            ? null
            : new SseTriHeadReadRequest(asset.AssetPath, asset.ContentSha256, asset.Content);

    private static void ValidateBakedClosure(
        SkyrimFaceBakeAuthority authority,
        SkyrimRaceMenuFaceBakeResult baked,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!baked.Accepted)
        {
            if (!baked.Shapes.IsEmpty)
            {
                diagnostics.Add(Error("facegeom-bake-partial-refusal",
                    "A refused face bake returned partial shape output."));
            }
            return;
        }
        if (baked.Shapes.IsDefault || baked.Shapes.Length != authority.CarrierShapes.Length)
        {
            diagnostics.Add(Error("facegeom-bake-shape-count",
                "The accepted face bake did not emit the exact authority carrier count."));
            return;
        }
        for (int index = 0; index < authority.CarrierShapes.Length; index++)
        {
            SkyrimFaceBakeCarrierShapeAuthority carrier = authority.CarrierShapes[index];
            SkyrimRaceMenuFaceBakeShapeOutput shape = baked.Shapes[index];
            if (!string.Equals(carrier.CarrierShapeName, shape.CarrierShapeName,
                    StringComparison.Ordinal) ||
                shape.TopologySha256 != carrier.ExpectedCarrierTopologySha256 ||
                shape.BasePositionSha256 != carrier.ExpectedModelPositionSha256 ||
                shape.FinalPositions.IsDefault || shape.FinalPositions.Length != shape.VertexCount ||
                shape.FinalPositions.Any(item => !float.IsFinite(item.X) ||
                                                 !float.IsFinite(item.Y) ||
                                                 !float.IsFinite(item.Z)))
            {
                diagnostics.Add(Error("facegeom-bake-shape-drift",
                    $"Baked shape at index {index} differs from its authority binding."));
            }
        }
    }

    private static ImmutableArray<RaceMenuNpcFaceGeomShapeEvidence> BuildShapeEvidence(
        SkyrimFaceBakeAuthority authority,
        SkyrimFaceRecordRoute route,
        ImmutableArray<SkyrimRaceMenuFaceBakeShapeOutput> baked,
        ImmutableArray<GeneratedXyzFile> files,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = ImmutableArray.CreateBuilder<RaceMenuNpcFaceGeomShapeEvidence>();
        Dictionary<string, SkyrimFaceBakeShapeTriAuthority> triByShape =
            authority.ShapeTriInputs.ToDictionary(item => item.CarrierShapeName,
                StringComparer.Ordinal);
        for (int index = 0; index < authority.CarrierShapes.Length; index++)
        {
            SkyrimFaceBakeCarrierShapeAuthority carrier = authority.CarrierShapes[index];
            SkyrimFaceHeadPartRecordRoute? record = route.HeadParts.SingleOrDefault(item =>
                SameForm(item.Reference, carrier.HeadPart));
            if (record is null || index >= baked.Length || index >= files.Length ||
                !triByShape.TryGetValue(carrier.CarrierShapeName,
                    out SkyrimFaceBakeShapeTriAuthority? tri))
            {
                diagnostics.Add(Error("facegeom-evidence-closure",
                    $"Carrier '{carrier.CarrierShapeName}' lacks exact output evidence."));
                continue;
            }
            ImmutableArray<RaceMenuNpcFaceGeomTriEvidence> triEvidence =
                BuildTriEvidence(tri, baked[index].TriEvidence, diagnostics);
            result.Add(new RaceMenuNpcFaceGeomShapeEvidence(
                carrier.HeadPart,
                record.Provider,
                carrier.ModelNif.AssetPath,
                carrier.ModelNif.ContentSha256,
                carrier.ModelShapeName,
                carrier.CarrierShapeName,
                baked[index].VertexCount,
                carrier.ExpectedModelPositionSha256,
                carrier.ExpectedCarrierTopologySha256,
                files[index].Path,
                files[index].Sha256,
                baked[index].FinalPositionSha256,
                triEvidence));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<RaceMenuNpcFaceGeomTriEvidence> BuildTriEvidence(
        SkyrimFaceBakeShapeTriAuthority authority,
        ImmutableArray<SkyrimRaceMenuFaceBakeTriEvidence> baked,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var assets = new List<(SkyrimFaceMorphTriRole Role, SkyrimFaceBakeAuthorityAsset Asset)>();
        if (authority.RaceMorphTri is not null) assets.Add((SkyrimFaceMorphTriRole.Race, authority.RaceMorphTri));
        if (authority.ChargenMorphTri is not null) assets.Add((SkyrimFaceMorphTriRole.Chargen, authority.ChargenMorphTri));
        if (authority.MeshMorphTri is not null) assets.Add((SkyrimFaceMorphTriRole.Mesh, authority.MeshMorphTri));
        assets.AddRange(authority.ExtendedMorphTris.Select(item =>
            (SkyrimFaceMorphTriRole.Extended, item)));
        if (assets.Count != baked.Length)
        {
            diagnostics.Add(Error("facegeom-tri-evidence-count",
                $"Carrier '{authority.CarrierShapeName}' TRI evidence count drifted."));
            return ImmutableArray<RaceMenuNpcFaceGeomTriEvidence>.Empty;
        }
        var result = ImmutableArray.CreateBuilder<RaceMenuNpcFaceGeomTriEvidence>();
        for (int index = 0; index < assets.Count; index++)
        {
            (SkyrimFaceMorphTriRole role, SkyrimFaceBakeAuthorityAsset asset) = assets[index];
            SkyrimRaceMenuFaceBakeTriEvidence evidence = baked[index];
            if (role != evidence.Role || asset.AssetPath != evidence.SourcePath ||
                asset.ContentSha256 != evidence.SourceSha256)
            {
                diagnostics.Add(Error("facegeom-tri-evidence-drift",
                    $"Carrier '{authority.CarrierShapeName}' TRI evidence at index {index} changed identity."));
                continue;
            }
            result.Add(new RaceMenuNpcFaceGeomTriEvidence(
                role,
                asset.AssetPath,
                asset.ContentSha256,
                asset.ProviderId,
                asset.ProviderKind,
                asset.ProviderPath,
                asset.ProviderSha256,
                evidence.Disposition));
        }
        return result.ToImmutable();
    }

    private static bool SameForm(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(left.Plugin.Value,
            right.Plugin.Value, StringComparison.OrdinalIgnoreCase);

    private static string FormKey(FormReference reference) =>
        $"{reference.Plugin.Value}|{reference.FormId.Value:X8}";

    private sealed class FormReferenceComparer : IEqualityComparer<FormReference>
    {
        public static FormReferenceComparer Instance { get; } = new();

        public bool Equals(FormReference left, FormReference right) => SameForm(left, right);

        public int GetHashCode(FormReference value) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(FormKey(value));
    }
}
