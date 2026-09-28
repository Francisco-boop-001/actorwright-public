using System.Collections.Immutable;
using System.Drawing;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed record BethesdaNpcCreationHeadPartValidation(
    int Index,
    bool Exists,
    bool TypeMatches,
    bool ModelSourceQualified);

public sealed record BethesdaNpcCreationAppearanceReferenceValidation(
    ImmutableArray<BethesdaNpcCreationHeadPartValidation> HeadParts,
    bool HairColorExists,
    bool FaceTextureSetExists)
{
    public bool NakedSkinBindingValid { get; init; } = true;

    public bool OutfitSkinBindingValid { get; init; } = true;

    public bool IsValid => HeadParts.All(item => item.Exists && item.TypeMatches) &&
        HeadParts.All(item => item.ModelSourceQualified) && HairColorExists &&
        FaceTextureSetExists && NakedSkinBindingValid && OutfitSkinBindingValid;
}

public static partial class BethesdaNpcCreationAdapter
{
    public static ImmutableArray<PluginName> BuildMasterList(
        ImmutableArray<PluginName> templateMasters,
        NpcCreationAppearanceSource appearance,
        PluginName outputPlugin,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities = default)
    {
        _ = BuildOwnedRecordLayout(appearance);
        var masters = ImmutableArray.CreateBuilder<PluginName>();
        var admitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(PluginName plugin)
        {
            if (string.Equals(plugin.Value, outputPlugin.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The authored appearance cannot reference its output plugin {outputPlugin} as an external master.");
            }

            if (admitted.Add(plugin.Value)) masters.Add(plugin);
        }

        foreach (var master in templateMasters) Add(master);
        if (appearance is FullyAuthoredSkyrimNpcAppearanceSource authored)
        {
            foreach (var headPart in authored.OrderedHeadParts)
            {
                switch (headPart)
                {
                    case ExternalSkyrimNpcHeadPart externalHeadPart:
                        Add(externalHeadPart.Hdpt.Plugin);
                        break;
                    case OutputOwnedSkyrimNpcFaceHeadPart outputOwned:
                        Add(outputOwned.QualifiedExternalFaceHdpt.Plugin);
                        break;
                    default:
                        throw new InvalidDataException("The authored headpart source is unsupported.");
                }
            }
            if (authored.HairColor is ExternalSkyrimNpcHairColor externalHairColor)
                Add(externalHairColor.Clfm.Plugin);
            if (authored.FaceTextureSet is ExternalSkyrimNpcFaceTextureSet externalTexture)
                Add(externalTexture.Txst.Plugin);
            if (authored.NakedSkinBinding is { } skinBinding)
            {
                Add(skinBinding.SourceSkinArmor.Reference.Plugin);
                foreach (var region in skinBinding.Regions)
                {
                    Add(region.SourceArmorAddon.Reference.Plugin);
                    if (region.TargetFemaleSkinTextureSet is not null)
                        Add(region.TargetFemaleSkinTextureSet.Reference.Plugin);
                }
                foreach (PluginName retained in
                         RetainedNakedSkinReferenceOwners(
                             skinBinding,
                             pluginAuthorities))
                    Add(retained);
            }
            if (authored.ExposedOutfitSkinBinding is { } outfitBinding)
            {
                Add(outfitBinding.SourceOutfit.Reference.Plugin);
                Add(outfitBinding.SourceArmor.Reference.Plugin);
                Add(outfitBinding.SourceArmorAddon.Reference.Plugin);
                Add(outfitBinding.TargetFemaleSkinTextureSet.Reference.Plugin);
                foreach (PluginName retained in
                         RetainedOutfitReferenceOwners(
                             outfitBinding,
                             pluginAuthorities))
                    Add(retained);
            }
        }

        if (masters.Count > byte.MaxValue)
            throw new InvalidDataException("A Skyrim plugin may not declare more than 255 full master files.");
        return masters.ToImmutable();
    }

    private static ImmutableArray<PluginName>
        RetainedOutfitReferenceOwners(
            OutputOwnedSkyrimNpcExposedOutfitSkinBinding binding,
            ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        var result = ImmutableArray.CreateBuilder<PluginName>();
        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        void AddLinks(IFormLinkContainerGetter record)
        {
            foreach (var link in record.EnumerateFormLinks())
            {
                string plugin = link.FormKey.ModKey.ToString();
                if (!string.IsNullOrWhiteSpace(plugin) &&
                    seen.Add(plugin))
                    result.Add(new PluginName(plugin));
            }
        }

        using (var provider = OpenProvider(
                   binding.SourceArmorAddon,
                   pluginAuthorities))
        {
            IArmorAddonGetter source =
                provider.ArmorAddons.FirstOrDefault(item =>
                    item.FormKey == ToFormKey(
                        binding.SourceArmorAddon.Reference)) ??
                throw new InvalidDataException(
                    "The winning exposed-outfit ARMA is missing while deriving retained master closure.");
            AddLinks(source);
        }
        using (var provider = OpenProvider(
                   binding.SourceArmor,
                   pluginAuthorities))
        {
            IArmorGetter source =
                provider.Armors.FirstOrDefault(item =>
                    item.FormKey == ToFormKey(
                        binding.SourceArmor.Reference)) ??
                throw new InvalidDataException(
                    "The winning exposed-outfit ARMO is missing while deriving retained master closure.");
            AddLinks(source);
        }
        using (var provider = OpenProvider(
                   binding.SourceOutfit,
                   pluginAuthorities))
        {
            IOutfitGetter source =
                provider.Outfits.FirstOrDefault(item =>
                    item.FormKey == ToFormKey(
                        binding.SourceOutfit.Reference)) ??
                throw new InvalidDataException(
                    "The winning exposed-outfit OTFT is missing while deriving retained master closure.");
            AddLinks(source);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<PluginName>
        RetainedNakedSkinReferenceOwners(
            OutputOwnedSkyrimNpcNakedSkinBinding binding,
            ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        var result = ImmutableArray.CreateBuilder<PluginName>();
        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        void AddLinks(IFormLinkContainerGetter record)
        {
            foreach (var link in record.EnumerateFormLinks())
            {
                FormKey key = link.FormKey;
                if (key.IsNull) continue;
                string plugin = key.ModKey.ToString();
                if (!string.IsNullOrWhiteSpace(plugin) &&
                    seen.Add(plugin))
                    result.Add(new PluginName(plugin));
            }
        }

        foreach (var region in binding.Regions)
        {
            using var provider = OpenProvider(
                region.SourceArmorAddon,
                pluginAuthorities);
            IArmorAddonGetter source =
                provider.ArmorAddons.FirstOrDefault(item =>
                    item.FormKey == ToFormKey(
                        region.SourceArmorAddon.Reference)) ??
                throw new InvalidDataException(
                    "A winning naked-skin ARMA is missing while deriving retained master closure.");
            AddLinks(source);
        }
        using (var provider = OpenProvider(
                   binding.SourceSkinArmor,
                   pluginAuthorities))
        {
            IArmorGetter source =
                provider.Armors.FirstOrDefault(item =>
                    item.FormKey == ToFormKey(
                        binding.SourceSkinArmor.Reference)) ??
                throw new InvalidDataException(
                    "The winning naked-skin ARMO is missing while deriving retained master closure.");
            AddLinks(source);
        }
        return result.ToImmutable();
    }

    public static uint ExpectedNextFormIdFor(NpcCreationAppearanceSource appearance) =>
        BuildOwnedRecordLayout(appearance).NextFormId;

    public static int ExpectedMajorRecordCountFor(NpcCreationAppearanceSource appearance) =>
        BuildOwnedRecordLayout(appearance).MajorRecordCount;

    public static int ExpectedTopGroupCountFor(NpcCreationAppearanceSource appearance) =>
        BuildOwnedRecordLayout(appearance).TopGroupCount;

    public static BethesdaNpcCreationAppearanceReferenceValidation ValidateAppearanceReferenceTypes(
        WorkspacePath dataRoot,
        NpcCreationAppearanceSource appearance,
        NpcSex? npcSex = null,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities = default,
        FormReference? targetRace = null)
    {
        if (appearance is not FullyAuthoredSkyrimNpcAppearanceSource authored)
        {
            return new BethesdaNpcCreationAppearanceReferenceValidation([], true, true);
        }

        var headParts = Enumerable.Range(0, authored.OrderedHeadParts.Length)
            .Select(index => new BethesdaNpcCreationHeadPartValidation(index, false, false, false))
            .ToArray();
        var hairColorExists = authored.HairColor is OutputOwnedSkyrimNpcHairColor;
        var faceTextureSetExists = authored.FaceTextureSet is OutputOwnedSkyrimNpcFaceTextureSet;
        var referencesByProvider = EnumerateExternalAppearanceReferences(authored)
            .GroupBy(item => item.Reference.Plugin.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var providerGroup in referencesByProvider)
        {
            var providerPath = BethesdaNpcCreationProviderResolver.Resolve(
                dataRoot, new PluginName(providerGroup.Key), pluginAuthorities);
            if (!File.Exists(providerPath)) continue;
            var providerModKey = ModKey.FromNameAndExtension(providerGroup.Key);
            using var provider = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(providerModKey, new Noggog.FilePath(providerPath)),
                SkyrimRelease.SkyrimSE);

            foreach (var reference in providerGroup)
            {
                var key = ToFormKey(reference.Reference);
                switch (reference.Kind)
                {
                    case AppearanceReferenceKind.HeadPart:
                        var record = provider.HeadParts.FirstOrDefault(item => item.FormKey == key);
                        var requested = authored.OrderedHeadParts[reference.Index];
                        headParts[reference.Index] = new BethesdaNpcCreationHeadPartValidation(
                            reference.Index,
                            record is not null,
                            record is not null && ParseHeadPartType(record.Type) ==
                                requested.Type,
                            record is not null &&
                            (npcSex is null ||
                             HeadPartSexCompatible(record, npcSex.Value)) &&
                            (targetRace is null ||
                             HeadPartRaceCompatible(
                                 record, targetRace.Value, dataRoot, pluginAuthorities)) &&
                            (requested is ExternalSkyrimNpcHeadPart ||
                             IsQualifiedFaceModelSource(record, npcSex)));
                        break;
                    case AppearanceReferenceKind.HairColor:
                        hairColorExists = provider.Colors.Any(item => item.FormKey == key);
                        break;
                    case AppearanceReferenceKind.FaceTextureSet:
                        faceTextureSetExists = provider.TextureSets.Any(item => item.FormKey == key);
                        break;
                    default:
                        throw new InvalidDataException("The appearance reference kind is unsupported.");
                }
            }
        }

        return new BethesdaNpcCreationAppearanceReferenceValidation(
            headParts.ToImmutableArray(), hairColorExists, faceTextureSetExists)
        {
            NakedSkinBindingValid = authored.NakedSkinBinding is null ||
                ValidateNakedSkinBinding(
                    authored.NakedSkinBinding,
                    pluginAuthorities),
            OutfitSkinBindingValid = authored.ExposedOutfitSkinBinding is null ||
                ValidateExposedOutfitSkinBinding(
                    dataRoot,
                    authored.ExposedOutfitSkinBinding,
                    pluginAuthorities)
        };
    }

    private static void EnsureAppearanceReferences(
        WorkspacePath dataRoot,
        NpcCreationAppearanceSource appearance,
        NpcSex npcSex,
        FormReference targetRace,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        var validation = ValidateAppearanceReferenceTypes(
            dataRoot, appearance, npcSex, pluginAuthorities, targetRace);
        var invalidHeadParts = validation.HeadParts
            .Where(item => !item.Exists || !item.TypeMatches)
            .Select(item => item.Index)
            .ToArray();
        if (invalidHeadParts.Length > 0)
        {
            throw new InvalidDataException(
                "The authored headpart providers do not expose the requested HDPT records and types at indexes " +
                string.Join(", ", invalidHeadParts) + ".");
        }
        var invalidModelSources = validation.HeadParts
            .Where(item => !item.ModelSourceQualified)
            .Select(item => item.Index)
            .ToArray();
        if (invalidModelSources.Length > 0)
        {
            throw new InvalidDataException(
                "The output-owned Face HDPT model sources are not qualified at indexes " +
                string.Join(", ", invalidModelSources) + ".");
        }
        if (!validation.HairColorExists)
            throw new InvalidDataException("The authored external hair color does not resolve to a CLFM record.");
        if (!validation.FaceTextureSetExists)
            throw new InvalidDataException("The authored face texture set does not resolve to a TXST record.");
        if (!validation.NakedSkinBindingValid)
            throw new InvalidDataException(
                "The authored private naked skin binding does not resolve to one exact ARMO plus body/hands/feet ARMA and TXST routes.");
        if (!validation.OutfitSkinBindingValid)
            throw new InvalidDataException(
                "The authored exposed-outfit skin binding does not resolve to one exact OTFT -> ARMO -> ARMA route and target TXST.");
    }

    private static void ApplyAppearance(
        Npc npc,
        SkyrimMod mod,
        ModKey outputModKey,
        NpcCreationRequest request)
    {
        if (request.Appearance is TemplateCarrierNpcAppearanceSource) return;
        if (request.Appearance is not FullyAuthoredSkyrimNpcAppearanceSource authored)
            throw new InvalidDataException("The NPC creation request has an unsupported appearance source.");

        ApplyAuthoredAppearance(
            npc,
            mod,
            outputModKey,
            new WorkspacePath(Path.GetDirectoryName(request.TemplatePlugin.Value)!),
            request.Identity.EditorId.Value,
            request.Traits.Sex,
            authored,
            request.PluginAuthorities);
    }

    internal static void ApplyAuthoredAppearance(
        Npc npc,
        SkyrimMod mod,
        ModKey outputModKey,
        WorkspacePath dataRoot,
        string npcEditorId,
        NpcSex npcSex,
        FullyAuthoredSkyrimNpcAppearanceSource authored,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities = default)
    {
        var outputTextureSet = authored.FaceTextureSet switch
        {
            ExternalSkyrimNpcFaceTextureSet => (FormKey?)null,
            OutputOwnedSkyrimNpcFaceTextureSet outputOwned =>
                AddOutputOwnedFaceTextureSet(mod, outputModKey, npcEditorId, outputOwned),
            _ => throw new InvalidDataException("The authored face texture-set source is unsupported.")
        };

        npc.HeadParts.Clear();
        foreach (var headPart in authored.OrderedHeadParts)
        {
            var key = headPart switch
            {
                ExternalSkyrimNpcHeadPart external => ToFormKey(external.Hdpt),
                OutputOwnedSkyrimNpcFaceHeadPart outputOwned when outputTextureSet is { } textureKey =>
                    AddOutputOwnedFaceHeadPart(
                        mod, outputModKey, dataRoot, npcEditorId,
                        npcSex, outputOwned, textureKey, pluginAuthorities),
                OutputOwnedSkyrimNpcFaceHeadPart => throw new InvalidDataException(
                    "An output-owned Face HDPT requires an output-owned private head TXST."),
                _ => throw new InvalidDataException("The authored headpart source is unsupported.")
            };
            npc.HeadParts.Add(new FormLink<IHeadPartGetter>(key));
        }

        npc.HairColor = authored.HairColor switch
        {
            ExternalSkyrimNpcHairColor external =>
                new FormLinkNullable<IColorRecordGetter>(ToFormKey(external.Clfm)),
            OutputOwnedSkyrimNpcHairColor outputOwned =>
                AddOutputOwnedHairColor(mod, outputModKey, npcEditorId, outputOwned),
            _ => throw new InvalidDataException("The authored hair color source is unsupported.")
        };
        npc.HeadTexture = new FormLinkNullable<ITextureSetGetter>(authored.FaceTextureSet switch
        {
            ExternalSkyrimNpcFaceTextureSet external => ToFormKey(external.Txst),
            OutputOwnedSkyrimNpcFaceTextureSet when outputTextureSet is { } key => key,
            _ => throw new InvalidDataException("The authored face texture-set source is unsupported.")
        });
        npc.Weight = authored.Weight;
        npc.FaceMorph = ToFaceMorph(authored.FaceMorphs);
        npc.FaceParts = ToFaceParts(authored.FaceMorphs);

        npc.TintLayers.Clear();
        foreach (var layer in authored.FaceTints.Layers)
        {
            npc.TintLayers.Add(new TintLayer
            {
                Index = layer.Index,
                Color = Color.FromArgb(layer.Alpha, layer.Red, layer.Green, layer.Blue),
                InterpolationValue = layer.Coverage / 100f,
                Preset = layer.PresetIndex
            });
        }

        npc.TextureLighting = Color.FromArgb(
            255,
            ToColorByte(authored.Qnam.Red),
            ToColorByte(authored.Qnam.Green),
            ToColorByte(authored.Qnam.Blue));

        if (authored.NakedSkinBinding is { } skinBinding)
        {
            AddOutputOwnedNakedSkinBinding(
                mod,
                outputModKey,
                npcEditorId,
                skinBinding,
                pluginAuthorities);
        }

        if (authored.ExposedOutfitSkinBinding is { } outfitBinding)
        {
            npc.DefaultOutfit = new FormLinkNullable<IOutfitGetter>(
                AddOutputOwnedExposedOutfitSkinBinding(
                    mod,
                    outputModKey,
                    dataRoot,
                    npcEditorId,
                    outfitBinding,
                    pluginAuthorities));
        }
    }

    private static FormLinkNullable<IColorRecordGetter> AddOutputOwnedHairColor(
        SkyrimMod mod,
        ModKey outputModKey,
        string npcEditorId,
        OutputOwnedSkyrimNpcHairColor source)
    {
        var key = new FormKey(outputModKey, source.AllocatedLocalFormId.Value);
        var color = new ColorRecord(key, SkyrimRelease.SkyrimSE)
        {
            FormVersion = RecordFormVersion,
            EditorID = BuildHairColorEditorId(npcEditorId),
            Color = Color.FromArgb(
                255,
                (int)((source.PackedRgb.Value >> 16) & 0xFFu),
                (int)((source.PackedRgb.Value >> 8) & 0xFFu),
                (int)(source.PackedRgb.Value & 0xFFu)),
            Playable = true
        };
        mod.Colors.Add(color);
        return new FormLinkNullable<IColorRecordGetter>(key);
    }

    internal static FormKey AddOutputOwnedFaceTextureSet(
        SkyrimMod mod,
        ModKey outputModKey,
        string npcEditorId,
        OutputOwnedSkyrimNpcFaceTextureSet source)
    {
        var key = new FormKey(outputModKey, source.AllocatedLocalFormId.Value);
        var paths = source.Paths ??
            throw new InvalidDataException("The output-owned private head TXST has no texture paths.");
        var slots = new Dictionary<string, AssetPath>(StringComparer.Ordinal)
        {
            ["diffuse"] = paths.Diffuse, ["normalOrGloss"] = paths.NormalOrGloss,
            ["glowOrDetailMap"] = paths.GlowOrDetailMap, ["height"] = paths.Height,
            ["backlightMaskOrSpecular"] = paths.BacklightMaskOrSpecular
        };
        if (paths.EnvironmentMaskOrSubsurfaceTint is { } environmentMask) slots.Add("environmentMaskOrSubsurfaceTint", environmentMask);
        if (paths.Environment is { } environment) slots.Add("environment", environment);
        if (paths.Multilayer is { } multilayer) slots.Add("multilayer", multilayer);
        AddOutputOwnedTextureSet(mod, key, BuildPrivateHeadTextureSetEditorId(npcEditorId), slots,
            TextureSet.Flag.FaceGenTextures | TextureSet.Flag.HasModelSpaceNormalMap);
        return key;
    }

    internal static void AddOutputOwnedTextureSet(SkyrimMod mod, FormKey key, string editorId,
        IReadOnlyDictionary<string, AssetPath> paths, TextureSet.Flag flags)
    {
        mod.TextureSets.Add(new TextureSet(key, SkyrimRelease.SkyrimSE)
        {
            FormVersion = RecordFormVersion, EditorID = editorId, Flags = flags,
            Diffuse = paths["diffuse"].Value, NormalOrGloss = paths["normalOrGloss"].Value,
            GlowOrDetailMap = paths["glowOrDetailMap"].Value,
            BacklightMaskOrSpecular = paths["backlightMaskOrSpecular"].Value,
            Height = paths.TryGetValue("height", out var height) ? height.Value : null,
            EnvironmentMaskOrSubsurfaceTint = paths.TryGetValue("environmentMaskOrSubsurfaceTint", out var mask) ? mask.Value : null,
            Environment = paths.TryGetValue("environment", out var environment) ? environment.Value : null,
            Multilayer = paths.TryGetValue("multilayer", out var multilayer) ? multilayer.Value : null
        });
    }

    private static FormKey AddOutputOwnedFaceHeadPart(
        SkyrimMod mod,
        ModKey outputModKey,
        WorkspacePath dataRoot,
        string npcEditorId,
        NpcSex npcSex,
        OutputOwnedSkyrimNpcFaceHeadPart source,
        FormKey privateTextureSet,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        var providerPath = BethesdaNpcCreationProviderResolver.Resolve(
            dataRoot, source.QualifiedExternalFaceHdpt.Plugin, pluginAuthorities);
        var providerModKey = ModKey.FromNameAndExtension(source.QualifiedExternalFaceHdpt.Plugin.Value);
        using var provider = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(providerModKey, new Noggog.FilePath(providerPath)),
            SkyrimRelease.SkyrimSE);
        var qualified = provider.HeadParts.FirstOrDefault(
            item => item.FormKey == ToFormKey(source.QualifiedExternalFaceHdpt)) ??
            throw new InvalidDataException("The qualified external Face HDPT source is missing.");
        if (ParseHeadPartType(qualified.Type) != NpcHeadPartType.Face ||
            !IsQualifiedFaceModelSource(qualified, npcSex))
        {
            throw new InvalidDataException(
                "The qualified external Face HDPT does not expose an admissible head model.");
        }

        return AddOutputOwnedFaceHeadPart(mod, new FormKey(outputModKey, source.AllocatedLocalFormId.Value),
            qualified, source.PreserveQualifiedEditorId ? qualified.EditorID : BuildPrivateFaceHeadPartEditorId(npcEditorId), privateTextureSet);
    }

    internal static FormKey AddOutputOwnedFaceHeadPart(SkyrimMod mod, FormKey key, IHeadPartGetter qualified,
        string? editorId, FormKey privateTextureSet)
    {
        var headPart = mod.HeadParts.DuplicateInAsNewRecord(qualified, key);
        headPart.FormVersion = RecordFormVersion;
        headPart.EditorID = editorId;
        headPart.Type = HeadPart.TypeEnum.Face;
        headPart.TextureSet = new FormLinkNullable<ITextureSetGetter>(privateTextureSet);
        return key;
    }

    internal static FormKey AddOutputOwnedNakedSkinBinding(
        SkyrimMod mod,
        ModKey outputModKey,
        string npcEditorId,
        OutputOwnedSkyrimNpcNakedSkinBinding source,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        var ownedAddons = ImmutableArray.CreateBuilder<FormKey>();
        foreach (var region in source.Regions)
        {
            FormKey sourceAddonKey = ToFormKey(
                region.SourceArmorAddon.Reference);
            FormKey ownedAddonKey = new(
                outputModKey, region.AllocatedArmorAddonLocalFormId.Value);
            using var provider = OpenProvider(
                region.SourceArmorAddon,
                pluginAuthorities);
            IArmorAddonGetter sourceAddon =
                provider.ArmorAddons.FirstOrDefault(item =>
                    item.FormKey == sourceAddonKey) ??
                throw new InvalidDataException(
                    "The naked-skin source ARMA is missing.");
            var ownedAddon = mod.ArmorAddons.DuplicateInAsNewRecord(
                sourceAddon, ownedAddonKey);
            ownedAddon.FormVersion = RecordFormVersion;
            ownedAddon.EditorID = BuildPrivateNakedSkinArmorAddonEditorId(
                npcEditorId, region.Region);
            ownedAddon.SkinTexture =
                new GenderedItem<
                    IFormLinkNullableGetter<ITextureSetGetter>>(
                    ownedAddon.SkinTexture?.Male ??
                    new FormLinkNullable<ITextureSetGetter>(),
                    region.AllocatedFemaleTextureSetLocalFormId is { } allocatedTexture
                        ? new FormLinkNullable<ITextureSetGetter>(new FormKey(outputModKey, allocatedTexture.Value))
                        : region.TargetFemaleSkinTextureSet is { } targetTexture
                        ? new FormLinkNullable<ITextureSetGetter>(
                            ToFormKey(targetTexture.Reference))
                        : ownedAddon.SkinTexture?.Female ??
                          new FormLinkNullable<ITextureSetGetter>());
            var femaleModel = ownedAddon.WorldModel?.Female ??
                              new Model();
            femaleModel.File = ToSkyrimModelPath(region.FemaleModel);
            ownedAddon.WorldModel = new GenderedItem<Model?>(
                ownedAddon.WorldModel?.Male,
                femaleModel);
            ownedAddons.Add(ownedAddonKey);
        }

        FormKey ownedArmorKey = new(
            outputModKey, source.AllocatedArmorLocalFormId.Value);
        using (var provider = OpenProvider(
                   source.SourceSkinArmor,
                   pluginAuthorities))
        {
            IArmorGetter sourceArmor =
                provider.Armors.FirstOrDefault(item =>
                    item.FormKey == ToFormKey(
                        source.SourceSkinArmor.Reference)) ??
                throw new InvalidDataException(
                    "The naked-skin source ARMO is missing.");
            var ownedArmor = mod.Armors.DuplicateInAsNewRecord(
                sourceArmor, ownedArmorKey);
            ownedArmor.FormVersion = RecordFormVersion;
            ownedArmor.EditorID = BuildPrivateNakedSkinArmorEditorId(
                npcEditorId);
            ownedArmor.Armature.Clear();
            foreach (FormKey addon in ownedAddons)
            {
                ownedArmor.Armature.Add(
                    new FormLink<IArmorAddonGetter>(addon));
            }
        }

        return ownedArmorKey;
    }

    private static FormKey AddOutputOwnedExposedOutfitSkinBinding(
        SkyrimMod mod,
        ModKey outputModKey,
        WorkspacePath dataRoot,
        string npcEditorId,
        OutputOwnedSkyrimNpcExposedOutfitSkinBinding source,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        FormKey sourceAddonKey = ToFormKey(source.SourceArmorAddon.Reference);
        FormKey ownedAddonKey = new(
            outputModKey, source.AllocatedArmorAddonLocalFormId.Value);
        using (var provider = OpenProvider(
                   source.SourceArmorAddon,
                   pluginAuthorities))
        {
            IArmorAddonGetter sourceAddon = provider.ArmorAddons.FirstOrDefault(
                item => item.FormKey == sourceAddonKey) ??
                throw new InvalidDataException(
                    "The exposed-outfit source ARMA is missing.");
            var ownedAddon = mod.ArmorAddons.DuplicateInAsNewRecord(
                sourceAddon, ownedAddonKey);
            ownedAddon.FormVersion = RecordFormVersion;
            ownedAddon.EditorID = BuildPrivateOutfitArmorAddonEditorId(
                npcEditorId);
            ownedAddon.SkinTexture =
                new GenderedItem<
                    IFormLinkNullableGetter<ITextureSetGetter>>(
                    ownedAddon.SkinTexture?.Male ??
                    new FormLinkNullable<ITextureSetGetter>(),
                    new FormLinkNullable<ITextureSetGetter>(
                        ToFormKey(
                            source.TargetFemaleSkinTextureSet.Reference)));
        }

        FormKey sourceArmorKey = ToFormKey(source.SourceArmor.Reference);
        FormKey ownedArmorKey = new(
            outputModKey, source.AllocatedArmorLocalFormId.Value);
        using (var provider = OpenProvider(
                   source.SourceArmor,
                   pluginAuthorities))
        {
            IArmorGetter sourceArmor = provider.Armors.FirstOrDefault(
                item => item.FormKey == sourceArmorKey) ??
                throw new InvalidDataException(
                    "The exposed-outfit source ARMO is missing.");
            FormKey[] armature = sourceArmor.Armature
                .Select(item => item.FormKey == sourceAddonKey
                    ? ownedAddonKey
                    : item.FormKey)
                .ToArray();
            if (armature.Count(item => item == ownedAddonKey) != 1)
                throw new InvalidDataException(
                    "The exposed-outfit source ARMO does not contain the source ARMA exactly once.");
            var ownedArmor = mod.Armors.DuplicateInAsNewRecord(
                sourceArmor, ownedArmorKey);
            ownedArmor.FormVersion = RecordFormVersion;
            ownedArmor.EditorID = BuildPrivateOutfitArmorEditorId(
                npcEditorId);
            ownedArmor.Armature.Clear();
            foreach (FormKey key in armature)
                ownedArmor.Armature.Add(
                    new FormLink<IArmorAddonGetter>(key));
        }

        FormKey sourceOutfitKey = ToFormKey(source.SourceOutfit.Reference);
        FormKey ownedOutfitKey = new(
            outputModKey, source.AllocatedOutfitLocalFormId.Value);
        using (var provider = OpenProvider(
                   source.SourceOutfit,
                   pluginAuthorities))
        {
            IOutfitGetter sourceOutfit = provider.Outfits.FirstOrDefault(
                item => item.FormKey == sourceOutfitKey) ??
                throw new InvalidDataException(
                    "The exposed-outfit source OTFT is missing.");
            FormKey[] items = (sourceOutfit.Items ?? [])
                .Select(item => item.FormKey == sourceArmorKey
                    ? ownedArmorKey
                    : item.FormKey)
                .ToArray();
            if (items.Count(item => item == ownedArmorKey) != 1)
                throw new InvalidDataException(
                    "The exposed-outfit source OTFT does not contain the source ARMO exactly once.");
            var ownedOutfit = mod.Outfits.DuplicateInAsNewRecord(
                sourceOutfit, ownedOutfitKey);
            ownedOutfit.FormVersion = RecordFormVersion;
            ownedOutfit.EditorID = BuildPrivateOutfitEditorId(
                npcEditorId);
            if (ownedOutfit.Items is null)
                throw new InvalidDataException(
                    "The output-owned OTFT lost its item collection.");
            ownedOutfit.Items.Clear();
            foreach (FormKey key in items)
                ownedOutfit.Items.Add(
                    new FormLink<IOutfitTargetGetter>(key));
        }
        return ownedOutfitKey;
    }

    private static NpcFaceMorph ToFaceMorph(SkyrimFaceMorphPatch source)
    {
        var values = source.Nam9Sliders;
        return new NpcFaceMorph
        {
            NoseLongVsShort = values[0],
            NoseUpVsDown = values[1],
            JawUpVsDown = values[2],
            JawNarrowVsWide = values[3],
            JawForwardVsBack = values[4],
            CheeksUpVsDown = values[5],
            CheeksForwardVsBack = values[6],
            EyesUpVsDown = values[7],
            EyesInVsOut = values[8],
            BrowsUpVsDown = values[9],
            BrowsInVsOut = values[10],
            BrowsForwardVsBack = values[11],
            LipsUpVsDown = values[12],
            LipsInVsOut = values[13],
            ChinNarrowVsWide = values[14],
            ChinUpVsDown = values[15],
            ChinUnderbiteVsOverbite = values[16],
            EyesForwardVsBack = values[17],
            Unknown = source.Nam9Trailing
        };
    }

    private static NpcFaceParts ToFaceParts(SkyrimFaceMorphPatch source) => new()
    {
        Nose = source.NamaValues[0],
        Unknown = source.NamaValues[1],
        Eyes = source.NamaValues[2],
        Mouth = source.NamaValues[3]
    };

    private static int ToColorByte(float normalized) =>
        Math.Clamp((int)MathF.Round(normalized * byte.MaxValue), byte.MinValue, byte.MaxValue);

    internal static string BuildHairColorEditorId(string npcEditorId)
    {
        const string suffix = "_HairColor";
        var prefixLength = Math.Min(npcEditorId.Length, 64 - suffix.Length);
        return npcEditorId[..prefixLength] + suffix;
    }

    internal static string BuildPrivateHeadTextureSetEditorId(string npcEditorId) =>
        BuildSuffixedEditorId(npcEditorId, "_PrivateHeadTXST");

    internal static string BuildPrivateFaceHeadPartEditorId(string npcEditorId) =>
        BuildSuffixedEditorId(npcEditorId, "_PrivateFaceHead");

    internal static string BuildPrivateOutfitArmorAddonEditorId(
        string npcEditorId) =>
        BuildSuffixedEditorId(npcEditorId, "_PrivateBodyARMA");

    internal static string BuildPrivateOutfitArmorEditorId(
        string npcEditorId) =>
        BuildSuffixedEditorId(npcEditorId, "_PrivateBodyARMO");

    internal static string BuildPrivateOutfitEditorId(
        string npcEditorId) =>
        BuildSuffixedEditorId(npcEditorId, "_PrivateOutfit");

    internal static string BuildPrivateNakedSkinArmorAddonEditorId(
        string npcEditorId,
        SkyrimNpcSkinRegion region) =>
        BuildSuffixedEditorId(npcEditorId, $"_Private{region}ARMA");

    internal static string BuildPrivateNakedSkinArmorEditorId(
        string npcEditorId) =>
        BuildSuffixedEditorId(npcEditorId, "_PrivateNakedSkin");

    private static string ToSkyrimModelPath(AssetPath dataRelativePath)
    {
        var value = dataRelativePath.Value.Replace('\\', '/');
        return value.StartsWith("Meshes/", StringComparison.OrdinalIgnoreCase)
            ? value["Meshes/".Length..]
            : value;
    }

    private static string BuildSuffixedEditorId(string npcEditorId, string suffix)
    {
        var prefixLength = Math.Min(npcEditorId.Length, 64 - suffix.Length);
        return npcEditorId[..prefixLength] + suffix;
    }

    private static IEnumerable<AppearanceReference> EnumerateExternalAppearanceReferences(
        FullyAuthoredSkyrimNpcAppearanceSource appearance)
    {
        for (var index = 0; index < appearance.OrderedHeadParts.Length; index++)
        {
            var headPart = appearance.OrderedHeadParts[index];
            var reference = headPart switch
            {
                ExternalSkyrimNpcHeadPart externalHeadPart => externalHeadPart.Hdpt,
                OutputOwnedSkyrimNpcFaceHeadPart outputOwned => outputOwned.QualifiedExternalFaceHdpt,
                _ => throw new InvalidDataException("The authored headpart source is unsupported.")
            };
            yield return new AppearanceReference(AppearanceReferenceKind.HeadPart, index, reference);
        }
        if (appearance.HairColor is ExternalSkyrimNpcHairColor externalHairColor)
            yield return new AppearanceReference(
                AppearanceReferenceKind.HairColor, -1, externalHairColor.Clfm);
        if (appearance.FaceTextureSet is ExternalSkyrimNpcFaceTextureSet externalTexture)
        {
            yield return new AppearanceReference(
                AppearanceReferenceKind.FaceTextureSet, -1, externalTexture.Txst);
        }
    }

    private static bool ValidateExposedOutfitSkinBinding(
        WorkspacePath dataRoot,
        OutputOwnedSkyrimNpcExposedOutfitSkinBinding binding,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        using var outfitProvider = OpenProvider(
            binding.SourceOutfit, pluginAuthorities);
        IOutfitGetter? outfit = outfitProvider.Outfits.FirstOrDefault(
            item => item.FormKey == ToFormKey(binding.SourceOutfit.Reference));
        if (outfit is null || outfit.IsDeleted ||
            !(outfit.Items ?? []).Any(item =>
                item.FormKey == ToFormKey(binding.SourceArmor.Reference)))
            return false;

        using var armorProvider = OpenProvider(
            binding.SourceArmor, pluginAuthorities);
        IArmorGetter? armor = armorProvider.Armors.FirstOrDefault(
            item => item.FormKey == ToFormKey(binding.SourceArmor.Reference));
        if (armor is null || armor.IsDeleted ||
            !armor.Armature.Any(item =>
                item.FormKey == ToFormKey(
                    binding.SourceArmorAddon.Reference)))
            return false;

        using var addonProvider = OpenProvider(
            binding.SourceArmorAddon, pluginAuthorities);
        IArmorAddonGetter? addon = addonProvider.ArmorAddons.FirstOrDefault(
            item => item.FormKey == ToFormKey(
                binding.SourceArmorAddon.Reference));
        if (addon is null || addon.IsDeleted || addon.BodyTemplate is null ||
            ((uint)addon.BodyTemplate.FirstPersonFlags & 0x04U) == 0)
            return false;

        using var textureProvider = OpenProvider(
            binding.TargetFemaleSkinTextureSet,
            pluginAuthorities);
        return textureProvider.TextureSets.Any(item =>
            item.FormKey == ToFormKey(
                binding.TargetFemaleSkinTextureSet.Reference) &&
            !item.IsDeleted);
    }

    private static bool ValidateNakedSkinBinding(
        OutputOwnedSkyrimNpcNakedSkinBinding binding,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        using (var armorProvider = OpenProvider(
                   binding.SourceSkinArmor,
                   pluginAuthorities))
        {
            IArmorGetter? armor = armorProvider.Armors.FirstOrDefault(
                item => item.FormKey == ToFormKey(
                    binding.SourceSkinArmor.Reference));
            if (armor is null || armor.IsDeleted)
                return false;
            var expectedAddons = binding.Regions
                .Select(item => ToFormKey(
                    item.SourceArmorAddon.Reference))
                .ToImmutableHashSet();
            if (!expectedAddons.IsSubsetOf(
                    armor.Armature.Select(item => item.FormKey)))
                return false;
        }

        foreach (var region in binding.Regions)
        {
            FormKey? sourceFemaleTexture;
            using (var addonProvider = OpenProvider(
                       region.SourceArmorAddon,
                       pluginAuthorities))
            {
                IArmorAddonGetter? addon =
                    addonProvider.ArmorAddons.FirstOrDefault(item =>
                        item.FormKey == ToFormKey(
                            region.SourceArmorAddon.Reference));
                if (addon is null || addon.IsDeleted ||
                    addon.BodyTemplate is null ||
                    (((uint)addon.BodyTemplate.FirstPersonFlags &
                      RegionSlotMask(region.Region)) == 0))
                    return false;
                sourceFemaleTexture =
                    addon.SkinTexture?.Female?.FormKeyNullable;
            }

            if (region.TargetFemaleSkinTextureSet is { } targetTexture)
            {
                using var textureProvider = OpenProvider(
                    targetTexture,
                    pluginAuthorities);
                if (!textureProvider.TextureSets.Any(item =>
                        item.FormKey == ToFormKey(
                            targetTexture.Reference) &&
                        !item.IsDeleted))
                    return false;
            }
            else if (sourceFemaleTexture is not null)
            {
                return false;
            }
        }

        return true;
    }

    private static ISkyrimModDisposableGetter OpenProvider(
        RaceMenuNpcFormBinding binding,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        NpcCreationPluginAuthority? authority = null;
        foreach (NpcCreationPluginAuthority candidate in
                 pluginAuthorities)
        {
            if (!string.Equals(
                    candidate.Plugin.Value,
                    binding.ProviderPluginName.Value,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (authority is not null)
                throw new InvalidDataException(
                    $"Winning provider '{binding.ProviderPluginName}' is declared more than once.");
            authority = candidate;
        }
        if (authority is null ||
            authority.PluginPath != binding.ProviderPlugin ||
            authority.ExpectedSha256 != binding.ProviderPluginSha256)
        {
            throw new InvalidDataException(
                $"The winning {binding.Signature} provider binding is not present with its exact path and hash in the creation request.");
        }
        var modKey = ModKey.FromNameAndExtension(
            binding.ProviderPluginName.Value);
        return SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                modKey,
                new Noggog.FilePath(binding.ProviderPlugin.Value)),
            SkyrimRelease.SkyrimSE);
    }

    private static ISkyrimModDisposableGetter OpenProvider(
        WorkspacePath dataRoot,
        PluginName plugin,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        string path = BethesdaNpcCreationProviderResolver.Resolve(
            dataRoot, plugin, pluginAuthorities);
        var modKey = ModKey.FromNameAndExtension(plugin.Value);
        return SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(modKey, new Noggog.FilePath(path)),
            SkyrimRelease.SkyrimSE);
    }

    private static bool IsQualifiedFaceModelSource(IHeadPartGetter source, NpcSex? npcSex)
    {
        var model = source.Model;
        var file = model?.File;
        if (source.IsDeleted || source.IsCompressed || model is null || file is null ||
            (model.AlternateTextures?.Count ?? 0) != 0 || source.ExtraParts.Count != 0 ||
            !source.Color.IsNull || source.Parts.Count is < 1 or > 4 ||
            source.Parts.Any(item => item.FileName is null) ||
            !source.Flags.HasFlag(HeadPart.Flag.Playable)) return false;
        if (npcSex is { } sex)
        {
            if (!HeadPartSexCompatible(source, sex)) return false;
        }
        var path = file.ToString();
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            _ = new AssetPath(path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HeadPartSexCompatible(IHeadPartGetter source, NpcSex expected)
    {
        var isFemale = source.Flags.HasFlag(HeadPart.Flag.Female);
        var isMale = source.Flags.HasFlag(HeadPart.Flag.Male);
        return SupportsSex(isMale, isFemale, expected == NpcSex.Female);
    }

    internal static bool HeadPartCompatible(
        IHeadPartGetter source,
        NpcSex expectedSex,
        FormReference targetRace,
        WorkspacePath dataRoot,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        if (!HeadPartSexCompatible(source, expectedSex)) return false;
        return HeadPartRaceCompatible(
            source, targetRace, dataRoot, pluginAuthorities);
    }

    private static bool HeadPartRaceCompatible(
        IHeadPartGetter source,
        FormReference targetRace,
        WorkspacePath dataRoot,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        if (source.ValidRaces.FormKeyNullable is not { } validRacesKey) return true;

        using var provider = OpenProvider(
            dataRoot,
            new PluginName(validRacesKey.ModKey.ToString()),
            pluginAuthorities);
        IFormListGetter? validRaces = provider.FormLists.FirstOrDefault(item =>
            item.FormKey == validRacesKey);
        return validRaces is not null && !validRaces.IsDeleted &&
               validRaces.Items.Any(item =>
                   item.FormKey == ToFormKey(targetRace));
    }

    private static bool SupportsSex(
        bool isMale,
        bool isFemale,
        bool npcIsFemale) =>
        isMale == isFemale || (npcIsFemale ? isFemale : isMale);

    private static OwnedAppearanceRecordLayout BuildOwnedRecordLayout(
        NpcCreationAppearanceSource appearance)
    {
        var next = ExpectedNextFormId;
        var majorRecordCount = 1;
        var topGroups = new HashSet<string>(StringComparer.Ordinal)
        {
            "NPC_"
        };
        if (appearance is not FullyAuthoredSkyrimNpcAppearanceSource authored)
            return new OwnedAppearanceRecordLayout(
                next, majorRecordCount, topGroups.Count);

        void AddOwnedRecord(string signature)
        {
            next++;
            majorRecordCount++;
            topGroups.Add(signature);
        }

        if (authored.HairColor is OutputOwnedSkyrimNpcHairColor hairColor)
        {
            RequireSequentialOwnedFormId(hairColor.AllocatedLocalFormId, next, "CLFM");
            AddOwnedRecord("CLFM");
        }

        var ownedTexture = authored.FaceTextureSet as OutputOwnedSkyrimNpcFaceTextureSet;
        var ownedHeadParts = authored.OrderedHeadParts
            .OfType<OutputOwnedSkyrimNpcFaceHeadPart>()
            .ToArray();
        if ((ownedTexture is null) != (ownedHeadParts.Length == 0) || ownedHeadParts.Length > 1)
        {
            throw new InvalidDataException(
                "The bounded private-head contract requires exactly one output-owned TXST paired with exactly one output-owned Face HDPT.");
        }
        if (ownedTexture is not null)
        {
            RequireSequentialOwnedFormId(ownedTexture.AllocatedLocalFormId, next, "TXST");
            AddOwnedRecord("TXST");
            RequireSequentialOwnedFormId(ownedHeadParts[0].AllocatedLocalFormId, next, "HDPT");
            AddOwnedRecord("HDPT");
        }
        if (authored.NakedSkinBinding is { } skinBinding)
        {
            foreach (var region in skinBinding.Regions)
            {
                RequireSequentialOwnedFormId(
                    region.AllocatedArmorAddonLocalFormId,
                    next, "ARMA");
                AddOwnedRecord("ARMA");
            }
            RequireSequentialOwnedFormId(
                skinBinding.AllocatedArmorLocalFormId,
                next, "ARMO");
            AddOwnedRecord("ARMO");
        }
        if (authored.ExposedOutfitSkinBinding is { } outfitBinding)
        {
            RequireSequentialOwnedFormId(
                outfitBinding.AllocatedArmorAddonLocalFormId,
                next, "ARMA");
            AddOwnedRecord("ARMA");
            RequireSequentialOwnedFormId(
                outfitBinding.AllocatedArmorLocalFormId,
                next, "ARMO");
            AddOwnedRecord("ARMO");
            RequireSequentialOwnedFormId(
                outfitBinding.AllocatedOutfitLocalFormId,
                next, "OTFT");
            AddOwnedRecord("OTFT");
        }
        return new OwnedAppearanceRecordLayout(
            next, majorRecordCount, topGroups.Count);
    }

    private static uint RegionSlotMask(SkyrimNpcSkinRegion region) =>
        region switch
        {
            SkyrimNpcSkinRegion.Body => 0x04U,
            SkyrimNpcSkinRegion.Hands => 0x08U,
            SkyrimNpcSkinRegion.Feet => 0x80U,
            _ => 0U
        };

    private static void RequireSequentialOwnedFormId(FormId actual, uint expected, string signature)
    {
        if (actual.Value != expected)
        {
            throw new InvalidDataException(
                $"The output-owned {signature} must use deterministic local FormID 0x{expected:X8}; received {actual}.");
        }
    }

    private static NpcHeadPartType ParseHeadPartType<TEnum>(TEnum? type)
        where TEnum : struct, Enum
        => BethesdaNpcFaceAdapter.ParseType(type).Type;

    private enum AppearanceReferenceKind
    {
        HeadPart,
        HairColor,
        FaceTextureSet
    }

    private sealed record AppearanceReference(
        AppearanceReferenceKind Kind,
        int Index,
        FormReference Reference);

    private sealed record OwnedAppearanceRecordLayout(
        uint NextFormId,
        int MajorRecordCount,
        int TopGroupCount);

}
