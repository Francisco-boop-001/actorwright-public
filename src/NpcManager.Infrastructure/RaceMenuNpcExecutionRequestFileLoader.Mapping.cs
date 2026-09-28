using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class RaceMenuNpcExecutionRequestFileLoader
{
    private RaceMenuNpcExecutionRequest ToRequest(ExecutionRequestDto dto)
    {
        if (dto.SchemaVersion is not (1 or 2 or 3) ||
            !string.Equals(dto.Edition, "skyrimse", StringComparison.Ordinal))
            throw new InvalidDataException(
                "Preset NPC execution requests require schemaVersion 1, 2, or 3 and edition 'skyrimse'.");
        if (dto.SchemaVersion == 1 && dto.ExistingNpcTarget is not null)
            throw new InvalidDataException(
                "schemaVersion 1 cannot declare an existingNpcTarget; use schemaVersion 2.");
        if (dto.SchemaVersion == 3 && dto.ExistingNpcTarget is not null)
            throw new InvalidDataException(
                "schemaVersion 3 cannot declare an existingNpcTarget; use schemaVersion 2.");
        if (dto.WholeSkinAuthority is not null && dto.SchemaVersion != 2)
            throw new InvalidDataException("wholeSkinAuthority is supported only by schemaVersion 2 existingNpcTarget requests.");
        var presetBundle = Required(dto.PresetBundle, "presetBundle");
        var providerContext = Required(dto.ProviderContext, "providerContext");
        var standaloneAssets = Required(dto.StandaloneAssets, "standaloneAssets");
        var output = Required(dto.Output, "output");
        if (!BlankNpcOutputPolicy.TryParseWireName(output.PluginType, out BlankNpcPluginType pluginType))
            throw new InvalidDataException(
                $"output.pluginType must be \"{BlankNpcOutputPolicy.EspWireName}\" or \"{BlankNpcOutputPolicy.EspfeWireName}\"; observed '{output.PluginType}'.");
        if (output.PluginType is not null && dto.ExistingNpcTarget is not null)
            throw new InvalidDataException(
                "output.pluginType applies to newly allocated NPCs only; existingNpcTarget requests keep their source plugin kind.");
        var identity = Required(dto.Identity, "identity");
        var traits = Required(dto.Traits, "traits");
        var referencesDto = Required(dto.References, "references");
        var statsDto = Required(dto.Stats, "stats");

        var runtimeRoutes = new RaceMenuNpcRuntimeRouteAuthority(
            Resolve(presetBundle.RuntimeRoutesPath),
            Hash(presetBundle.RuntimeRoutesSha256));
        var bundle = new RaceMenuNpcPresetBundle(
            Resolve(presetBundle.ManifestPath), Hash(presetBundle.ManifestSha256),
            Resolve(presetBundle.PresetPath), Hash(presetBundle.PresetSha256),
            Resolve(presetBundle.FaceGeomPath), Hash(presetBundle.FaceGeomSha256),
            Resolve(presetBundle.FaceTintPath), Hash(presetBundle.FaceTintSha256),
            new RaceMenuNpcRecordAuthority(
                Resolve(presetBundle.RecordAuthorityPath),
                Hash(presetBundle.RecordAuthoritySha256)),
            runtimeRoutes);
        if (!FormId.TryParse(Required(providerContext.TemplateNpcFormId,
                "providerContext.templateNpcFormId"), out var templateNpc))
            throw new InvalidDataException("providerContext.templateNpcFormId is invalid.");
        BlankNpcProviderBindingRequest provider;
        bool hasProductArm = providerContext.ProductFixtureBundle is not null;
        bool hasWorkspaceArm = HasWorkspaceProviderDeclaration(providerContext);
        if (hasProductArm)
        {
            if (dto.SchemaVersion != 3 || hasWorkspaceArm)
                throw new InvalidDataException(
                    "productFixtureBundle is exclusive to schemaVersion 3 and cannot be combined with workspace provider fields.");
            if (applicationProviderRegistry is null)
                throw new ProductProviderUnavailableException(
                    "The application product-provider registry is unavailable.");
            ProductFixtureBundleDto product = providerContext.ProductFixtureBundle!;
            var reference = new ProductFixtureBundleReference(
                Required(product.BundleId,
                    "providerContext.productFixtureBundle.bundleId"),
                Hash(product.RegistryManifestSha256));
            if (string.Equals(reference.BundleId, "blank-npc-v1",
                    StringComparison.Ordinal) &&
                (!applicationProviderRegistry.TryGetDefaultBlankNpcFixture(
                     out ProductFixtureBundleReference? installedProduct) ||
                 installedProduct is null))
                throw new ProductProviderUnavailableException(
                    "The optional bundled provider is not installed. Use a workspace-provider request built from legally obtained local assets.");
            ApplicationProviderResourceAdmissionResult admitted =
                applicationProviderRegistry.Admit(
                    reference,
                    templateNpc,
                    GameEdition.SkyrimSpecialEdition,
                    ParseSex(traits.Sex));
            if (!admitted.Accepted || admitted.Authority is null)
                throw new InvalidDataException(
                    "Product-provider bundle failed admission: " +
                    string.Join(" ", admitted.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")));
            ProviderResourceAuthoritySet resources = admitted.Authority;
            provider = new BlankNpcProviderBindingRequest(
                default,
                resources.Manifest.ExpectedSha256,
                resources.Edition,
                resources.Sex,
                default,
                resources.TemplatePlugin.ExpectedSha256,
                templateNpc,
                default,
                resources.FaceGeomCarrier.ExpectedSha256,
                default,
                default,
                default)
            {
                ProviderResources = resources
            };
        }
        else
        {
            if (!hasWorkspaceArm)
                throw new InvalidDataException(
                    "providerContext must declare exactly one complete workspace or product-fixture arm.");
            provider = new BlankNpcProviderBindingRequest(
                Resolve(providerContext.ManifestPath),
                Hash(providerContext.ManifestSha256),
                GameEdition.SkyrimSpecialEdition, ParseSex(traits.Sex),
                Resolve(providerContext.TemplatePlugin),
                Hash(providerContext.TemplateSha256),
                templateNpc,
                Resolve(providerContext.FaceGeomCarrier),
                Hash(providerContext.FaceGeomSha256),
                Resolve(providerContext.FaceTintManifest),
                Resolve(providerContext.FaceTintProviderRoot),
                Resolve(providerContext.DependencyManifest));
        }
        var references = new SkyrimNpcCreationReferences(
            Reference(referencesDto.Race, "race"),
            Reference(referencesDto.Voice, "voice"),
            Reference(referencesDto.ActorClass, "class"),
            Reference(referencesDto.CombatStyle, "combatStyle"),
            OptionalReference(referencesDto.DefaultOutfit, "defaultOutfit"));
        var stats = new SkyrimNpcCreationStats(
            new NpcLevelValue(ParseLevelMode(statsDto.LevelMode), statsDto.Level),
            statsDto.MagickaOffset, statsDto.StaminaOffset, statsDto.HealthOffset,
            statsDto.CalcMinLevel, statsDto.CalcMaxLevel,
            statsDto.SpeedMultiplier, statsDto.DispositionBase,
            statsDto.BleedoutOverride, statsDto.BaseHealth, statsDto.BaseMagicka,
            statsDto.BaseStamina, statsDto.Height, statsDto.Weight,
            statsDto.FarAwayModelDistance);
        RaceMenuExistingNpcTarget? existingTarget = null;
        if (dto.SchemaVersion == 2)
        {
            var target = Required(dto.ExistingNpcTarget, "existingNpcTarget");
            if (!FormId.TryParse(Required(
                    target.TargetFormId,
                    "existingNpcTarget.targetFormId"), out var targetFormId) ||
                targetFormId.Value is 0 or > 0x00FF_FFFF)
            {
                throw new InvalidDataException(
                    "existingNpcTarget.targetFormId must be a nonzero plugin-local 24-bit FormID.");
            }
            existingTarget = new RaceMenuExistingNpcTarget(
                Resolve(target.SourcePlugin),
                Hash(target.SourcePluginSha256),
                targetFormId);
        }

        var build = new RaceMenuNpcBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            bundle,
            provider,
            Resolve(output.Root),
            new PluginName(Required(output.Plugin, "output.plugin")),
            new NpcCreationIdentity(
                new EditorId(Required(identity.EditorId, "identity.editorId")),
                new NpcName(Required(identity.Name, "identity.name"))),
            new SkyrimNpcCreationTraits(
                ParseSex(traits.Sex), ParseRole(traits.Role),
                traits.Unique, traits.Essential, traits.Protected,
                traits.Respawns, traits.AutoCalcStats),
            references,
            stats)
        {
            ExistingNpcTarget = existingTarget,
            PluginType = pluginType,
            WholeSkinAuthority = dto.WholeSkinAuthority is { } skin
                ? new RaceMenuNpcWholeSkinAuthority(Resolve(skin.ManifestPath), Hash(skin.ManifestSha256))
                : null
        };
        return new RaceMenuNpcExecutionRequest(
            build,
            new RaceMenuNpcStandaloneAssetAuthority(
                Resolve(standaloneAssets.ManifestPath),
                Hash(standaloneAssets.ManifestSha256)))
        {
            ApplyBodySlide = dto.ApplyBodySlide ?? true,
            AllowInheritedMeshEmbeddedSkinTextureRoute =
                dto.AllowInheritedMeshEmbeddedSkinTextureRoute ?? false,
            FaceGeomSkeletonAuthority = dto.FaceGeomSkeletonAuthority ??
                SseFaceGeomCarrierSkeletonAuthority
                    .SourceModelWorldTranslations
        };
    }

    private WorkspacePath Resolve(string? relativeValue)
    {
        var relative = new AssetPath(Required(relativeValue, "workspace-relative path"));
        var result = new WorkspacePath(Path.Combine(workspaceRoot.Value,
            relative.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.IsUnder(workspaceRoot))
            throw new InvalidDataException("Request path escaped the workspace root.");
        return result;
    }

    private static Sha256Hash Hash(string? value) =>
        new(Required(value, "sha256"));

    private static bool HasWorkspaceProviderDeclaration(
        ProviderContextDto provider) =>
        provider.ManifestPath is not null ||
        provider.ManifestSha256 is not null ||
        provider.TemplatePlugin is not null ||
        provider.TemplateSha256 is not null ||
        provider.FaceGeomCarrier is not null ||
        provider.FaceGeomSha256 is not null ||
        provider.FaceTintManifest is not null ||
        provider.FaceTintProviderRoot is not null ||
        provider.DependencyManifest is not null;

    private static FormReference Reference(string? value, string role)
    {
        if (!FormReference.TryParse(Required(value, $"references.{role}"), out var reference))
            throw new InvalidDataException($"references.{role} is not Plugin|0xFormID.");
        return reference;
    }

    private static FormReference? OptionalReference(
        string? value,
        string role)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!FormReference.TryParse(value, out var reference))
            throw new InvalidDataException(
                $"references.{role} is not Plugin|0xFormID or null.");
        return reference;
    }

    private static NpcSex ParseSex(string? value) => value switch
    {
        "female" => NpcSex.Female,
        "male" => NpcSex.Male,
        _ => throw new InvalidDataException("traits.sex must be 'female' or 'male'.")
    };

    private static NpcCreationRole ParseRole(string? value) => value switch
    {
        "civilian" => NpcCreationRole.Civilian,
        "combatant" => NpcCreationRole.Combatant,
        "follower" => NpcCreationRole.Follower,
        "merchant" => NpcCreationRole.Merchant,
        "static-validation" => NpcCreationRole.StaticValidation,
        _ => throw new InvalidDataException("traits.role is unsupported.")
    };

    private static NpcLevelMode ParseLevelMode(string? value) => value switch
    {
        "fixed" => NpcLevelMode.Fixed,
        "multiplier" => NpcLevelMode.Multiplier,
        _ => throw new InvalidDataException("stats.levelMode must be 'fixed' or 'multiplier'.")
    };

    private static T Required<T>(T? value, string role) where T : class =>
        value ?? throw new InvalidDataException($"{role} is required.");

    private static string Required(string? value, string role) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('\0')
            ? value
            : throw new InvalidDataException($"{role} is required.");

    private sealed class ProductProviderUnavailableException(string message)
        : Exception(message);
}
