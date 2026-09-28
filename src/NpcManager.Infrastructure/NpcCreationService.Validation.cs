using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed partial class NpcCreationService
{
    private const int MaximumHeaderBytes = 1_048_576;
    private const int MaximumAuthoredHeadParts = 256;
    private const string BlankDetailMapTexturePath = "Actors/Character/Male/BlankDetailmap.dds";

    private BethesdaNpcCreationTemplateSnapshot? InspectTemplate(
        NpcCreationRequest request,
        PluginName outputPlugin,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var template = BethesdaNpcCreationAdapter.ReadTemplate(
                request.TemplatePlugin, request.TemplateNpcFormId);
            if (template.IsMaster || template.IsSmallMaster)
            {
                diagnostics.Add(Error("npc-create-template-compact-or-master",
                    "The admitted template provider must be an ordinary ESP, not a master or compact/light plugin."));
            }
            if (template.FormVersion != BethesdaNpcCreationAdapter.RecordFormVersion)
            {
                diagnostics.Add(Error("npc-create-template-form-version",
                    $"The admitted template NPC FormVersion is {template.FormVersion?.ToString() ?? "missing"}; expected 44."));
            }
            if (template.HasTemplateInheritance)
            {
                diagnostics.Add(Error("npc-create-template-inheritance",
                    "The admitted template NPC inherits template categories and cannot be duplicated as a complete standalone baseline."));
            }
            if (template.Masters.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error("npc-create-template-masters-empty",
                    "A complete Skyrim NPC template must declare its master providers."));
            }
            if (template.Masters.Select(item => item.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != template.Masters.Length)
            {
                diagnostics.Add(Error("npc-create-template-masters-duplicate",
                    "The template contains duplicate master names."));
            }
            if (template.Masters.Any(master =>
                    string.Equals(master.Value, outputPlugin.Value, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(Error("npc-create-output-self-master",
                    "The output plugin name appears in the source master list."));
            }

            var expectedMasters = BuildCreationMasterList(
                template.Masters,
                request.Appearance,
                outputPlugin,
                request.PluginAuthorities,
                diagnostics);
            ValidateReferenceClosure(request, expectedMasters, diagnostics);
            ValidateMasterProviders(request, expectedMasters, diagnostics, cancellationToken);
            if (!HasErrors(diagnostics))
            {
                var dataRoot = new WorkspacePath(Path.GetDirectoryName(request.TemplatePlugin.Value)!);
                var referenceTypes = BethesdaNpcCreationAdapter.ValidateReferenceTypes(
                    dataRoot, request.References, request.PluginAuthorities);
                if (!referenceTypes.RaceExists)
                    diagnostics.Add(Error("npc-create-race-signature-mismatch",
                        "The requested race FormID does not resolve to a RACE record in its declared provider."));
                if (!referenceTypes.VoiceExists)
                    diagnostics.Add(Error("npc-create-voice-signature-mismatch",
                        "The requested voice FormID does not resolve to a VTYP record in its declared provider."));
                if (!referenceTypes.ClassExists)
                    diagnostics.Add(Error("npc-create-class-signature-mismatch",
                        "The requested class FormID does not resolve to a CLAS record in its declared provider."));
                if (!referenceTypes.CombatStyleExists)
                    diagnostics.Add(Error("npc-create-combat-style-signature-mismatch",
                        "The requested combat-style FormID does not resolve to a CSTY record in its declared provider."));
                if (!referenceTypes.DefaultOutfitExists)
                    diagnostics.Add(Error("npc-create-default-outfit-signature-mismatch",
                        "The requested default-outfit FormID does not resolve to an OTFT record in its declared provider."));

                if (request.Appearance is FullyAuthoredSkyrimNpcAppearanceSource)
                {
                    var appearanceTypes = BethesdaNpcCreationAdapter.ValidateAppearanceReferenceTypes(
                        dataRoot, request.Appearance, request.Traits.Sex,
                        request.PluginAuthorities, request.References.Race);
                    foreach (var headPart in appearanceTypes.HeadParts.Where(item => !item.Exists))
                    {
                        diagnostics.Add(Error("npc-create-appearance-headpart-signature-mismatch",
                            $"Authored headpart {headPart.Index} does not resolve to an HDPT record in its declared provider."));
                    }
                    foreach (var headPart in appearanceTypes.HeadParts.Where(item => item.Exists && !item.TypeMatches))
                    {
                        diagnostics.Add(Error("npc-create-appearance-headpart-type-provider-mismatch",
                            $"Authored headpart {headPart.Index} does not match the requested HDPT type."));
                    }
                    foreach (var headPart in appearanceTypes.HeadParts.Where(item =>
                                 item.Exists && item.TypeMatches && !item.ModelSourceQualified))
                    {
                        diagnostics.Add(Error("npc-create-appearance-headpart-model-source-invalid",
                            $"Authored headpart {headPart.Index} is incompatible with the NPC sex or target race, or its output-owned Face carrier is not qualified."));
                    }
                    if (!appearanceTypes.HairColorExists)
                    {
                        diagnostics.Add(Error("npc-create-appearance-hair-color-signature-mismatch",
                            "The authored external hair color does not resolve to a CLFM record in its declared provider."));
                    }
                    if (!appearanceTypes.FaceTextureSetExists)
                    {
                        diagnostics.Add(Error("npc-create-appearance-face-texture-signature-mismatch",
                            "The authored face texture set does not resolve to a TXST record in its declared provider."));
                    }
                    if (!appearanceTypes.OutfitSkinBindingValid)
                    {
                        diagnostics.Add(Error(
                            "npc-create-appearance-outfit-skin-binding-invalid",
                            "The output-owned outfit skin binding does not resolve to one exact source OTFT -> ARMO -> ARMA route and target TXST."));
                    }
                }
            }
            return template;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                            InvalidDataException or ArgumentException or KeyNotFoundException)
        {
            diagnostics.Add(Error("npc-create-template-read-failed", exception.Message));
            return null;
        }
    }

    private ImmutableArray<Diagnostic> ValidatePaths(
        NpcCreationRequest request,
        ValidationMode mode)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("npc-create-edition-unsupported", "New-NPC creation supports Skyrim Special Edition only."));
        if (!request.TemplatePlugin.IsUnder(labRoot))
            diagnostics.Add(Error("npc-create-template-outside-lab", "The template provider must remain under the K-only lab root."));
        if (!request.Proposal.IsUnder(labRoot))
            diagnostics.Add(Error("npc-create-proposal-outside-lab", "The proposal must remain under the K-only lab root."));
        if (!request.Output.IsUnder(labRoot))
            diagnostics.Add(Error("npc-create-output-outside-lab", "The output must remain under the K-only lab root."));
        if (!request.TemplatePlugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("npc-create-template-extension", "The admitted template provider must be an ordinary .esp plugin."));
        if (!request.Output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("npc-create-output-extension", "New NPC creation emits ordinary .esp plugins only."));
        if (!request.Proposal.Value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("npc-create-proposal-extension", "The creation proposal must use a .json path."));
        if (!File.Exists(request.TemplatePlugin.Value))
            diagnostics.Add(Error("npc-create-template-missing", "The explicit template plugin does not exist."));

        var outputParent = Path.GetDirectoryName(request.Output.Value);
        var proposalParent = Path.GetDirectoryName(request.Proposal.Value);
        ValidateParent(outputParent, "output", diagnostics);
        ValidateParent(proposalParent, "proposal", diagnostics);
        AddUnsafePathDiagnostic(request.TemplatePlugin.Value, "template", diagnostics);
        AddUnsafePathDiagnostic(request.Proposal.Value, "proposal", diagnostics);
        AddUnsafePathDiagnostic(request.Output.Value, "output", diagnostics);
        AddReparseDiagnostic(request.TemplatePlugin.Value, "template", diagnostics);
        if (proposalParent is not null) AddReparseDiagnostic(proposalParent, "proposal-parent", diagnostics);
        if (outputParent is not null) AddReparseDiagnostic(outputParent, "output-parent", diagnostics);

        if (string.Equals(request.TemplatePlugin.Value, request.Output.Value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.TemplatePlugin.Value, request.Proposal.Value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.Output.Value, request.Proposal.Value, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("npc-create-path-collision",
                "Template, proposal, and output paths must be three distinct files."));
        }

        if (mode is ValidationMode.Analyze or ValidationMode.Apply)
        {
            if (File.Exists(request.Output.Value))
                diagnostics.Add(Error("npc-create-output-exists", "Creation never overwrites an existing plugin."));
        }
        else if (!File.Exists(request.Output.Value))
        {
            diagnostics.Add(Error("npc-create-output-missing", "The output plugin does not exist."));
        }

        if (mode == ValidationMode.Analyze)
        {
            if (File.Exists(request.Proposal.Value))
                diagnostics.Add(Error("npc-create-proposal-exists", "Creation analysis never overwrites an existing proposal."));
        }
        else if (!File.Exists(request.Proposal.Value))
        {
            diagnostics.Add(Error("npc-create-proposal-missing", "Apply and verify require the persisted proposal."));
        }
        return diagnostics.ToImmutable();
    }

    private void ValidateParent(
        string? parent,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error($"npc-create-{role}-parent-missing",
                $"The {role} parent directory must already exist."));
            return;
        }
        diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
    }

    private static void ValidateSpecification(
        NpcCreationRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.TemplateNpcFormId.Value == 0 || request.TemplateNpcFormId.Value > 0x00FF_FFFF)
            diagnostics.Add(Error("npc-create-template-form-id", "The template NPC FormID must be a nonzero plugin-local 24-bit value."));
        if (!Enum.IsDefined(request.Traits.Sex))
            diagnostics.Add(Error("npc-create-sex-invalid", "The NPC sex value is invalid."));
        if (!Enum.IsDefined(request.Traits.Role))
            diagnostics.Add(Error("npc-create-role-invalid", "An explicit supported NPC role is required."));
        if (!request.Traits.IsUnique)
            diagnostics.Add(Error("npc-create-unique-required", "The Gate 1 standalone NPC must be explicitly unique."));
        if (!request.Stats.Level.TryGetRaw(out _) ||
            request.Stats.Level.Mode == NpcLevelMode.Fixed && request.Stats.Level.Value > short.MaxValue)
            diagnostics.Add(Error("npc-create-level-invalid", "The NPC level is not representable by a Skyrim ACBS level field."));
        if (request.Stats.CalcMinLevel > short.MaxValue ||
            request.Stats.CalcMaxLevel > short.MaxValue ||
            request.Stats.CalcMinLevel > request.Stats.CalcMaxLevel)
            diagnostics.Add(Error("npc-create-level-bounds-invalid", "Level bounds must be ordered signed-16-bit Skyrim values."));
        if (!float.IsFinite(request.Stats.Height) || Math.Abs(request.Stats.Height - 1f) > 0.000001f)
            diagnostics.Add(Error("npc-create-height-invalid", "The Gate 1 NPC height must be exactly 1.0."));
        if (!float.IsFinite(request.Stats.Weight) || request.Stats.Weight is < 0f or > 100f)
            diagnostics.Add(Error("npc-create-weight-invalid", "Skyrim NPC weight must be finite and between 0 and 100."));
        if (request.Stats.SpeedMultiplier <= 0)
            diagnostics.Add(Error("npc-create-speed-invalid", "The NPC speed multiplier must be positive."));
        if (request.Stats.FarAwayModelDistance != 255)
            diagnostics.Add(Error("npc-create-nam5-invalid", "The Gate 1 NPC NAM5 value must be 255."));
        ValidateAppearance(request, diagnostics);
        ValidateRuntimeAppearance(request, diagnostics);
    }

    private static void ValidateAppearance(
        NpcCreationRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch (request.Appearance)
        {
            case TemplateCarrierNpcAppearanceSource:
                return;
            case FullyAuthoredSkyrimNpcAppearanceSource authored:
                ValidateFullyAuthoredAppearance(
                    request.Stats.Weight,
                    authored,
                    diagnostics);
                return;
            default:
                diagnostics.Add(Error("npc-create-appearance-source-invalid",
                    "A supported explicit NPC appearance source is required."));
                return;
        }
    }

    internal static void ValidateFullyAuthoredAppearance(
        float expectedWeight,
        FullyAuthoredSkyrimNpcAppearanceSource appearance,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (appearance.OrderedHeadParts.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error("npc-create-appearance-headparts-empty",
                "A fully authored Skyrim appearance requires an ordered headpart list."));
        }
        else if (appearance.OrderedHeadParts.Length > MaximumAuthoredHeadParts)
        {
            diagnostics.Add(Error("npc-create-appearance-headparts-count",
                $"A fully authored Skyrim appearance may contain at most {MaximumAuthoredHeadParts} headparts."));
        }

        if (!appearance.OrderedHeadParts.IsDefaultOrEmpty)
        {
            if (appearance.OrderedHeadParts.Any(item => item is null))
            {
                diagnostics.Add(Error("npc-create-appearance-headpart-source-missing",
                    "The ordered headpart list cannot contain a missing source."));
            }
            var duplicateTypes = appearance.OrderedHeadParts
                .Where(item => item is not null)
                .Where(item => item.Type != NpcHeadPartType.Misc && Enum.IsDefined(item.Type))
                .GroupBy(item => item.Type)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key.ToWireName())
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (duplicateTypes.Length > 0)
            {
                diagnostics.Add(Error("npc-create-appearance-headpart-type-duplicate",
                    "Only the misc headpart bucket may appear more than once; duplicate types: " +
                    string.Join(", ", duplicateTypes) + "."));
            }

            var duplicateReferences = appearance.OrderedHeadParts
                .Where(item => item is not null)
                .Select(GetHeadPartAuthorityReference)
                .GroupBy(item => (item.Plugin.Value.ToUpperInvariant(), item.FormId.Value))
                .Where(group => group.Count() > 1)
                .Select(group => $"{group.Key.Item1}|0x{group.Key.Item2:X8}")
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            if (duplicateReferences.Length > 0)
            {
                diagnostics.Add(Error("npc-create-appearance-headpart-reference-duplicate",
                    "The ordered headpart list contains duplicate record references: " +
                    string.Join(", ", duplicateReferences) + "."));
            }

            for (var index = 0; index < appearance.OrderedHeadParts.Length; index++)
            {
                var headPart = appearance.OrderedHeadParts[index];
                if (headPart is null) continue;
                if (!Enum.IsDefined(headPart.Type))
                {
                    diagnostics.Add(Error("npc-create-appearance-headpart-type-invalid",
                        $"Headpart {index} has an unsupported type value."));
                }
                switch (headPart)
                {
                    case ExternalSkyrimNpcHeadPart external:
                        ValidateExternalAppearanceReference(
                            external.Hdpt, $"headpart-{index}", diagnostics);
                        break;
                    case OutputOwnedSkyrimNpcFaceHeadPart outputOwned:
                        ValidateExternalAppearanceReference(
                            outputOwned.QualifiedExternalFaceHdpt,
                            $"headpart-{index}-model-source", diagnostics);
                        break;
                    default:
                        diagnostics.Add(Error("npc-create-appearance-headpart-source-invalid",
                            $"Headpart {index} has an unsupported source kind."));
                        break;
                }
            }
        }

        ValidateHairColor(appearance.HairColor, diagnostics);
        ValidateFaceTextureSet(appearance.FaceTextureSet, diagnostics);
        ValidateNakedSkinBinding(
            appearance.NakedSkinBinding, diagnostics);
        ValidateOutfitSkinBinding(
            appearance.ExposedOutfitSkinBinding, diagnostics);
        ValidateOwnedHeadRecordLayout(appearance, diagnostics);

        if (!float.IsFinite(appearance.Weight) || appearance.Weight is < 0f or > 100f)
        {
            diagnostics.Add(Error("npc-create-appearance-weight-invalid",
                "The fully authored Skyrim weight must be finite and between 0 and 100."));
        }
        else if (appearance.Weight != expectedWeight)
        {
            diagnostics.Add(Error("npc-create-appearance-weight-mismatch",
                "The fully authored appearance weight must exactly match the NPC stats weight."));
        }

        ValidateFaceMorphs(appearance.FaceMorphs, diagnostics);
        ValidateFaceTints(appearance.FaceTints, diagnostics);
        ValidateQnam(appearance.Qnam, diagnostics);
    }

    private static void ValidateExternalAppearanceReference(
        FormReference reference,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // Provider/master closure and record signatures are deliberately owned
        // by the Bethesda adapter, which has the load-order context required to
        // distinguish an external FormID from an output-owned record.
        if (reference.FormId.Value == 0 || reference.FormId.Value > 0x00FF_FFFF)
        {
            diagnostics.Add(Error($"npc-create-appearance-{role}-form-id",
                $"The {role} reference must use a nonzero plugin-local 24-bit FormID."));
        }
    }

    private static FormReference GetHeadPartAuthorityReference(
        SkyrimNpcHeadPartSource source) => source switch
        {
            ExternalSkyrimNpcHeadPart external => external.Hdpt,
            OutputOwnedSkyrimNpcFaceHeadPart outputOwned => outputOwned.QualifiedExternalFaceHdpt,
            _ => throw new InvalidDataException("The authored headpart source is unsupported.")
        };

    private static void ValidateFaceTextureSet(
        SkyrimNpcFaceTextureSetSource faceTextureSet,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch (faceTextureSet)
        {
            case ExternalSkyrimNpcFaceTextureSet external:
                ValidateExternalAppearanceReference(
                    external.Txst, "face-texture-set", diagnostics);
                return;
            case OutputOwnedSkyrimNpcFaceTextureSet outputOwned:
                if (outputOwned.Paths is null)
                {
                    diagnostics.Add(Error("npc-create-appearance-private-txst-paths-missing",
                        "An output-owned private head TXST requires an exact texture-path payload."));
                    return;
                }
                var paths = new (string Role, AssetPath? Path)[]
                {
                    ("diffuse", outputOwned.Paths.Diffuse),
                    ("normal-or-gloss", outputOwned.Paths.NormalOrGloss),
                    ("environment-mask-or-subsurface", outputOwned.Paths.EnvironmentMaskOrSubsurfaceTint),
                    ("glow-or-detail", outputOwned.Paths.GlowOrDetailMap),
                    ("height", outputOwned.Paths.Height),
                    ("environment", outputOwned.Paths.Environment),
                    ("multilayer", outputOwned.Paths.Multilayer),
                    ("backlight-or-specular", outputOwned.Paths.BacklightMaskOrSpecular)
                };
                foreach (var (role, path) in paths.Where(item => item.Path is not null))
                    ValidatePrivateHeadTexturePath(path!.Value, role, diagnostics);
                var duplicatePaths = paths
                    .Where(item => item.Path is not null &&
                                   !string.IsNullOrWhiteSpace(item.Path.Value.Value) &&
                                   !IsBlankDetailMapTexturePath(item.Path.Value))
                    .GroupBy(item => item.Path!.Value.Value.Replace('\\', '/'),
                        StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToArray();
                if (duplicatePaths.Length > 0)
                {
                    diagnostics.Add(Error("npc-create-appearance-private-txst-path-duplicate",
                        "Private head TXST slots must not reuse the same DDS path: " +
                        string.Join(", ", duplicatePaths) + "."));
                }
                return;
            default:
                diagnostics.Add(Error("npc-create-appearance-face-texture-source-invalid",
                    "A fully authored appearance requires an external or output-owned TXST source."));
                return;
        }
    }

    private static bool IsBlankDetailMapTexturePath(AssetPath path) =>
        string.Equals(path.Value.Replace('\\', '/'),
            BlankDetailMapTexturePath,
            StringComparison.OrdinalIgnoreCase);

    private static void ValidatePrivateHeadTexturePath(
        AssetPath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var value = path.Value;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 260 ||
            value.Any(char.IsControl) ||
            value.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("npc-create-appearance-private-txst-path-invalid",
                $"The private head {role} path must be a bounded traversal-free DDS path relative to Data/Textures, without a Data/ or Textures/ prefix."));
        }
    }

    private static void ValidateOwnedHeadRecordLayout(
        FullyAuthoredSkyrimNpcAppearanceSource appearance,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var next = BethesdaNpcCreationAdapter.ExpectedNextFormId;
        if (appearance.HairColor is OutputOwnedSkyrimNpcHairColor hairColor)
        {
            CheckOwnedAllocation(hairColor.AllocatedLocalFormId, next, "clfm", diagnostics);
            next++;
        }

        var ownedTexture = appearance.FaceTextureSet as OutputOwnedSkyrimNpcFaceTextureSet;
        var ownedHeads = appearance.OrderedHeadParts
            .OfType<OutputOwnedSkyrimNpcFaceHeadPart>()
            .ToArray();
        if ((ownedTexture is null) != (ownedHeads.Length == 0) || ownedHeads.Length > 1)
        {
            diagnostics.Add(Error("npc-create-appearance-private-head-pair-invalid",
                "The bounded private-head contract requires exactly one output-owned TXST paired with exactly one output-owned Face HDPT."));
            return;
        }
        if (ownedTexture is not null)
        {
            CheckOwnedAllocation(ownedTexture.AllocatedLocalFormId, next, "txst", diagnostics);
            next++;
            CheckOwnedAllocation(ownedHeads[0].AllocatedLocalFormId, next, "hdpt", diagnostics);
            next++;
        }
        if (appearance.NakedSkinBinding is { } skinBinding)
        {
            foreach (var region in skinBinding.Regions)
            {
                CheckOwnedAllocation(
                    region.AllocatedArmorAddonLocalFormId,
                    next, "arma", diagnostics);
                next++;
            }
            CheckOwnedAllocation(
                skinBinding.AllocatedArmorLocalFormId,
                next, "armo", diagnostics);
            next++;
        }
        if (appearance.ExposedOutfitSkinBinding is { } outfitBinding)
        {
            CheckOwnedAllocation(
                outfitBinding.AllocatedArmorAddonLocalFormId,
                next, "arma", diagnostics);
            next++;
            CheckOwnedAllocation(
                outfitBinding.AllocatedArmorLocalFormId,
                next, "armo", diagnostics);
            next++;
            CheckOwnedAllocation(
                outfitBinding.AllocatedOutfitLocalFormId,
                next, "otft", diagnostics);
        }
    }

    private static void ValidateNakedSkinBinding(
        OutputOwnedSkyrimNpcNakedSkinBinding? binding,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (binding is null) return;
        if (binding.SourceSkinArmor.Signature != new RecordSignature("ARMO"))
        {
            diagnostics.Add(Error(
                "npc-create-appearance-naked-skin-armor-signature",
                "The private naked skin source armor binding must be an ARMO."));
        }
        ValidateExternalAppearanceReference(
            binding.SourceSkinArmor.Reference,
            "naked-skin-source-armor", diagnostics);
        if (binding.Regions.IsDefault || binding.Regions.Length != 3)
        {
            diagnostics.Add(Error(
                "npc-create-appearance-naked-skin-region-count",
                "The private naked skin binding requires body, hands, and feet regions."));
            return;
        }
        var expected = new[]
        {
            SkyrimNpcSkinRegion.Body,
            SkyrimNpcSkinRegion.Hands,
            SkyrimNpcSkinRegion.Feet
        };
        if (!binding.Regions.Select(item => item.Region).SequenceEqual(expected))
        {
            diagnostics.Add(Error(
                "npc-create-appearance-naked-skin-region-order",
                "The private naked skin regions must be ordered body, hands, feet."));
        }
        var modelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in binding.Regions)
        {
            if (region.SourceArmorAddon.Signature != new RecordSignature("ARMA"))
            {
                diagnostics.Add(Error(
                    "npc-create-appearance-naked-skin-addon-signature",
                    "The private naked skin source addon binding must be an ARMA."));
            }
            ValidateExternalAppearanceReference(
                region.SourceArmorAddon.Reference,
                $"naked-skin-{region.Region}-source-addon", diagnostics);
            if (region.TargetFemaleSkinTextureSet is { } targetTexture)
            {
                if (targetTexture.Signature != new RecordSignature("TXST"))
                {
                    diagnostics.Add(Error(
                        "npc-create-appearance-naked-skin-txst-signature",
                        "The private naked skin target texture binding must be a TXST when present."));
                }
                ValidateExternalAppearanceReference(
                    targetTexture.Reference,
                    $"naked-skin-{region.Region}-target-texture-set", diagnostics);
            }
            string value = region.FemaleModel.Value;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 260 ||
                value.Any(char.IsControl) ||
                !value.StartsWith("Meshes/", StringComparison.OrdinalIgnoreCase) ||
                !value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error(
                    "npc-create-appearance-naked-skin-model-path",
                    "Private naked skin female models must be Data-relative Meshes/*.nif paths."));
            }
            if (!modelPaths.Add(value))
            {
                diagnostics.Add(Error(
                    "npc-create-appearance-naked-skin-model-duplicate",
                    $"Private naked skin model '{value}' occurs more than once."));
            }
        }
    }

    private static void ValidateOutfitSkinBinding(
        OutputOwnedSkyrimNpcExposedOutfitSkinBinding? binding,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (binding is null) return;
        ValidateExternalAppearanceReference(
            binding.SourceOutfit.Reference,
            "outfit-skin-source-outfit", diagnostics);
        ValidateExternalAppearanceReference(
            binding.SourceArmor.Reference,
            "outfit-skin-source-armor", diagnostics);
        ValidateExternalAppearanceReference(
            binding.SourceArmorAddon.Reference,
            "outfit-skin-source-addon", diagnostics);
        ValidateExternalAppearanceReference(
            binding.TargetFemaleSkinTextureSet.Reference,
            "outfit-skin-target-texture-set", diagnostics);
        if (binding.SourceOutfit.Reference ==
                binding.SourceArmor.Reference ||
            binding.SourceOutfit.Reference ==
                binding.SourceArmorAddon.Reference ||
            binding.SourceArmor.Reference ==
                binding.SourceArmorAddon.Reference)
        {
            diagnostics.Add(Error(
                "npc-create-appearance-outfit-skin-source-collision",
                "The source OTFT, ARMO, and ARMA references must be distinct."));
        }
    }

    private static void CheckOwnedAllocation(
        FormId actual,
        uint expected,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (actual.Value != expected)
        {
            diagnostics.Add(Error($"npc-create-appearance-owned-{role}-form-id-sequence",
                $"The output-owned {role.ToUpperInvariant()} must use deterministic local FormID 0x{expected:X8}; received {actual}."));
        }
    }

    private static void ValidateHairColor(
        SkyrimNpcHairColorSource hairColor,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch (hairColor)
        {
            case ExternalSkyrimNpcHairColor external:
                ValidateExternalAppearanceReference(
                    external.Clfm, "hair-color", diagnostics);
                break;
            case OutputOwnedSkyrimNpcHairColor outputOwned:
                if (outputOwned.AllocatedLocalFormId.Value is < 0x0000_0800 or > 0x00FF_FFFE)
                {
                    diagnostics.Add(Error("npc-create-appearance-hair-color-form-id",
                        "An output-owned CLFM must use an explicit local FormID from 0x00000800 through 0x00FFFFFE so NextFormID remains representable."));
                }
                if (outputOwned.AllocatedLocalFormId.Value == BethesdaNpcCreationAdapter.AllocatedLocalFormId)
                {
                    diagnostics.Add(Error("npc-create-appearance-hair-color-form-id-collision",
                        "The output-owned CLFM FormID collides with the allocated NPC FormID."));
                }
                if (outputOwned.PackedRgb.Value > 0x00FF_FFFF)
                {
                    diagnostics.Add(Error("npc-create-appearance-hair-color-rgb",
                        "The output-owned CLFM color must be a packed 24-bit RGB value."));
                }
                break;
            default:
                diagnostics.Add(Error("npc-create-appearance-hair-color-source-invalid",
                    "A fully authored appearance requires an external or output-owned CLFM source."));
                break;
        }
    }

    private static void ValidateFaceMorphs(
        SkyrimFaceMorphPatch faceMorphs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (faceMorphs is null)
        {
            diagnostics.Add(Error("npc-create-appearance-face-morphs-missing",
                "A fully authored appearance requires a Skyrim face-morph payload."));
            return;
        }
        if (faceMorphs.Nam9Sliders.IsDefault || faceMorphs.Nam9Sliders.Length != 18)
        {
            diagnostics.Add(Error("npc-create-appearance-nam9-count",
                "A fully authored NAM9 payload requires exactly 18 editable sliders."));
        }
        if (faceMorphs.NamaValues.IsDefault || faceMorphs.NamaValues.Length != 4)
        {
            diagnostics.Add(Error("npc-create-appearance-nama-count",
                "A fully authored NAMA payload requires exactly four family values."));
        }
        if (!faceMorphs.Nam9Sliders.IsDefault)
        {
            for (var index = 0; index < faceMorphs.Nam9Sliders.Length; index++)
            {
                var value = faceMorphs.Nam9Sliders[index];
                if (!float.IsFinite(value) || value is < -1f or > 1f)
                {
                    diagnostics.Add(Error("npc-create-appearance-nam9-range",
                        $"NAM9 slider {index} must be finite and between -1 and 1."));
                }
            }
        }
        if (!float.IsFinite(faceMorphs.Nam9Trailing))
        {
            diagnostics.Add(Error("npc-create-appearance-nam9-trailing",
                "The engine-owned NAM9 trailing value must be finite."));
        }
    }

    private static void ValidateFaceTints(
        SkyrimFaceTintPatch faceTints,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (faceTints is null || faceTints.Layers.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error("npc-create-appearance-face-tints-empty",
                "A fully authored appearance requires at least one Skyrim face-tint layer."));
            return;
        }
        if (faceTints.Layers.Length > 256)
        {
            diagnostics.Add(Error("npc-create-appearance-face-tints-count",
                "A fully authored Skyrim face tint may contain at most 256 layers."));
        }
        if (faceTints.Layers.GroupBy(item => item.Index).Any(group => group.Count() > 1))
        {
            diagnostics.Add(Error("npc-create-appearance-face-tint-index-duplicate",
                "Skyrim TINI indexes must be unique within the authored tint order."));
        }
        for (var index = 0; index < faceTints.Layers.Length; index++)
        {
            if (faceTints.Layers[index].Coverage > 100)
            {
                diagnostics.Add(Error("npc-create-appearance-face-tint-coverage",
                    $"Face-tint layer {index} coverage must be between 0 and 100."));
            }
        }
    }

    private static void ValidateQnam(
        SkyrimQnamRgb qnam,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var channels = new[] { ("red", qnam.Red), ("green", qnam.Green), ("blue", qnam.Blue) };
        foreach (var (name, value) in channels)
        {
            if (!float.IsFinite(value) || value is < 0f or > 1f)
            {
                diagnostics.Add(Error("npc-create-appearance-qnam-range",
                    $"The QNAM {name} channel must be finite and between 0 and 1."));
            }
        }
    }

    private static ImmutableArray<PluginName> BuildCreationMasterList(
        ImmutableArray<PluginName> templateMasters,
        NpcCreationAppearanceSource appearance,
        PluginName outputPlugin,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            return BethesdaNpcCreationAdapter.BuildMasterList(
                templateMasters,
                appearance,
                outputPlugin,
                pluginAuthorities);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
        {
            diagnostics.Add(Error("npc-create-appearance-master-union-invalid", exception.Message));
            return [];
        }
    }

    private static void ValidateReferenceClosure(
        NpcCreationRequest request,
        ImmutableArray<PluginName> masters,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var allowed = masters.Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = new List<(string Role, FormReference Value)>
        {
            ("race", request.References.Race),
            ("voice", request.References.Voice),
            ("class", request.References.Class),
            ("combat-style", request.References.CombatStyle)
        };
        if (request.References.DefaultOutfit is { } outfit)
            references.Add(("default-outfit", outfit));
        foreach (var (role, reference) in references)
        {
            if (reference.FormId.Value == 0 || reference.FormId.Value > 0x00FF_FFFF)
                diagnostics.Add(Error($"npc-create-{role}-form-id", $"The {role} reference must use a nonzero plugin-local 24-bit FormID."));
            if (!allowed.Contains(reference.Plugin.Value))
                diagnostics.Add(Error($"npc-create-{role}-outside-masters",
                    $"The {role} provider {reference.Plugin} is not in the template's exact master list."));
        }
    }

    private void ValidateProposalBinding(
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!proposal.IsApplicable)
            diagnostics.Add(Error("npc-create-proposal-not-applicable", "The analyzed proposal is not applicable."));
        var currentTemplateHash = HashIfPresent(request.TemplatePlugin, diagnostics);
        if (currentTemplateHash != request.ExpectedTemplateHash || currentTemplateHash != proposal.TemplateHash)
            diagnostics.Add(Error("npc-create-template-hash-drift", "The template plugin changed after analysis."));
        if (proposal.Edition != request.Edition ||
            proposal.TemplatePlugin != request.TemplatePlugin ||
            proposal.TemplateNpcFormId != request.TemplateNpcFormId ||
            proposal.Proposal != request.Proposal ||
            proposal.Output != request.Output ||
            proposal.PluginType != request.PluginType ||
            proposal.Identity != request.Identity ||
            proposal.Traits != request.Traits ||
            proposal.References != request.References ||
            !AppearanceSourcesEqual(proposal.Appearance, request.Appearance) ||
            proposal.Stats != request.Stats ||
            !RuntimeAppearancePayloadsEqual(proposal.RuntimeAppearance, request.RuntimeAppearance) ||
            !ArraysEqual(proposal.PluginAuthorities,
                request.PluginAuthorities.IsDefault ? [] : request.PluginAuthorities) ||
            proposal.AllocatedFormId.Value != BethesdaNpcCreationAdapter.AllocatedLocalFormId)
        {
            diagnostics.Add(Error("npc-create-proposal-request-mismatch",
                "The proposal is not bound to this complete creation request."));
        }
        try
        {
            var currentProposalHash = HashFile(request.Proposal.Value);
            if (proposal.ProposalHash is null || currentProposalHash != proposal.ProposalHash)
                diagnostics.Add(Error("npc-create-proposal-hash-drift", "The persisted proposal changed after analysis."));
            var expectedBytes = SerializeProposal(proposal);
            var actualBytes = File.ReadAllBytes(request.Proposal.Value);
            if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
                diagnostics.Add(Error("npc-create-proposal-content-mismatch",
                    "The persisted proposal bytes do not match the supplied typed proposal."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Add(Error("npc-create-proposal-read-failed", exception.Message));
        }

        var outputPlugin = ParseOutputPlugin(request.Output, diagnostics);
        if (proposal.OutputPlugin != outputPlugin)
            diagnostics.Add(Error("npc-create-proposal-output-plugin-mismatch",
                "The proposal output plugin name does not match the requested output filename."));
        var template = InspectTemplate(request, outputPlugin, diagnostics, cancellationToken);
        if (template is not null)
        {
            var expectedMasters = BuildCreationMasterList(
                template.Masters,
                request.Appearance,
                outputPlugin,
                request.PluginAuthorities,
                diagnostics);
            if (!expectedMasters.SequenceEqual(proposal.Masters))
            {
                diagnostics.Add(Error("npc-create-master-list-drift",
                    "The deterministic creation master union changed after analysis."));
            }
        }
    }

    private static bool AppearanceSourcesEqual(
        NpcCreationAppearanceSource left,
        NpcCreationAppearanceSource right) => (left, right) switch
        {
            (TemplateCarrierNpcAppearanceSource, TemplateCarrierNpcAppearanceSource) => true,
            (FullyAuthoredSkyrimNpcAppearanceSource leftAuthored,
                FullyAuthoredSkyrimNpcAppearanceSource rightAuthored) =>
                ArraysEqual(leftAuthored.OrderedHeadParts, rightAuthored.OrderedHeadParts) &&
                HairColorSourcesEqual(leftAuthored.HairColor, rightAuthored.HairColor) &&
                leftAuthored.FaceTextureSet == rightAuthored.FaceTextureSet &&
                leftAuthored.Weight.Equals(rightAuthored.Weight) &&
                FaceMorphsEqual(leftAuthored.FaceMorphs, rightAuthored.FaceMorphs) &&
                FaceTintsEqual(leftAuthored.FaceTints, rightAuthored.FaceTints) &&
                leftAuthored.Qnam == rightAuthored.Qnam &&
                leftAuthored.NakedSkinBinding ==
                rightAuthored.NakedSkinBinding &&
                leftAuthored.ExposedOutfitSkinBinding ==
                rightAuthored.ExposedOutfitSkinBinding,
            _ => false
        };

    private static bool HairColorSourcesEqual(
        SkyrimNpcHairColorSource left,
        SkyrimNpcHairColorSource right) => (left, right) switch
        {
            (ExternalSkyrimNpcHairColor leftExternal,
                ExternalSkyrimNpcHairColor rightExternal) =>
                leftExternal.Clfm == rightExternal.Clfm,
            (OutputOwnedSkyrimNpcHairColor leftOutputOwned,
                OutputOwnedSkyrimNpcHairColor rightOutputOwned) =>
                leftOutputOwned.AllocatedLocalFormId == rightOutputOwned.AllocatedLocalFormId &&
                leftOutputOwned.PackedRgb == rightOutputOwned.PackedRgb,
            _ => false
        };

    private static bool FaceMorphsEqual(
        SkyrimFaceMorphPatch left,
        SkyrimFaceMorphPatch right) =>
        left is not null && right is not null &&
        ArraysEqual(left.Nam9Sliders, right.Nam9Sliders) &&
        left.Nam9Trailing.Equals(right.Nam9Trailing) &&
        ArraysEqual(left.NamaValues, right.NamaValues);

    private static bool FaceTintsEqual(
        SkyrimFaceTintPatch left,
        SkyrimFaceTintPatch right) =>
        left is not null && right is not null &&
        ArraysEqual(left.Layers, right.Layers);

    private static bool ArraysEqual<T>(ImmutableArray<T> left, ImmutableArray<T> right) =>
        left.IsDefault == right.IsDefault &&
        (left.IsDefault || left.SequenceEqual(right));

    private enum ValidationMode
    {
        Analyze,
        Apply,
        Verify
    }
}
